﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Pkcs;
using System.Threading;
using Microsoft.Extensions.Caching.Memory;

namespace Microsoft.Data.SqlClient
{
    internal abstract class VirtualizationBasedSecurityEnclaveProviderBase : EnclaveProviderBase
    {
        #region Members

        private static readonly MemoryCache rootSigningCertificateCache = new MemoryCache(new MemoryCacheOptions());

        #endregion

        #region Constants

        private const int DiffieHellmanKeySize = 384;
        private const int VsmHGSProtocolId = (int)SqlConnectionAttestationProtocol.HGS;

        // ENCLAVE_IDENTITY related constants
        private static readonly EnclaveIdentity ExpectedPolicy = new EnclaveIdentity()
        {
            OwnerId = new byte[]
            {
                0x10, 0x20, 0x30, 0x40, 0x41, 0x31, 0x21, 0x11,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            },

            UniqueId = new byte[] { },

            // This field is calculated as follows:
            // "Fixed Microsoft GUID" = {845319A6-706C-47BC-A7E8-5137B0BC750D}
            // CN of the certificate that signed the file
            // Opus Info - Authenticated Attribute that contains a Name and a URL
            //
            // In our case the Opus Info is:
            // Description: Microsoft SQL Server Always Encrypted VBS Enclave Library,
            // Description URL: https://go.microsoft.com/fwlink/?linkid=2018716
            AuthorId = new byte[]
            {
                0x04, 0x37, 0xCA, 0xE2, 0x53, 0x7D, 0x8B, 0x9B,
                0x07, 0x76, 0xB6, 0x1B, 0x11, 0xE6, 0xCE, 0xD3,
                0xD2, 0x32, 0xE9, 0x30, 0x8F, 0x60, 0xE2, 0x1A,
                0xDA, 0xB2, 0xFD, 0x91, 0xE3, 0xDA, 0x95, 0x98
            },

            FamilyId = new byte[]
            {
                0xFE, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            },

            ImageId = new byte[]
            {
                // This value should be same as defined in sqlserver code Sql/Ntdbms/aetm/enclave/dllmain.cpp
                0x19, 0x17, 0x12, 0x00, 0x01, 0x05, 0x20, 0x13,
                0x00, 0x05, 0x14, 0x03, 0x12, 0x01, 0x22, 0x05
            },

            EnclaveSvn = 0,

            SecureKernelSvn = 0,

            PlatformSvn = 1,

            // 0: ENCLAVE_VBS_FLAG_NO_DEBUG in ds_main; Flag does not permit debug enclaves
            Flags = 0,

            SigningLevel = 0,

            Reserved = 0
        };

        #endregion

        #region Internal methods

        // When overridden in a derived class, looks up an existing enclave session information in the enclave session cache.
        // If the enclave provider doesn't implement enclave session caching, this method is expected to return null in the sqlEnclaveSession parameter.
        internal override void GetEnclaveSession(EnclaveSessionParameters enclaveSessionParameters, bool generateCustomData, bool isRetry, out SqlEnclaveSession sqlEnclaveSession, out long counter, out byte[] customData, out int customDataLength)
        {
            GetEnclaveSessionHelper(enclaveSessionParameters, false, isRetry, out sqlEnclaveSession, out counter, out customData, out customDataLength);
        }

        // Gets the information that SqlClient subsequently uses to initiate the process of attesting the enclave and to establish a secure session with the enclave.
        internal override SqlEnclaveAttestationParameters GetAttestationParameters(string attestationUrl, byte[] customData, int customDataLength)
        {
            ECDiffieHellman clientDHKey = KeyConverter.CreateECDiffieHellman(DiffieHellmanKeySize);
            return new SqlEnclaveAttestationParameters(VsmHGSProtocolId, Array.Empty<byte>(), clientDHKey);
        }

        // When overridden in a derived class, performs enclave attestation, generates a symmetric key for the session, creates a an enclave session and stores the session information in the cache.
        internal override void CreateEnclaveSession(byte[] attestationInfo, ECDiffieHellman clientDHKey, EnclaveSessionParameters enclaveSessionParameters, byte[] customData, int customDataLength, out SqlEnclaveSession sqlEnclaveSession, out long counter)
        {
            sqlEnclaveSession = null;
            counter = 0;
            try
            {
                ThreadRetryCache.Remove(Thread.CurrentThread.ManagedThreadId.ToString());
                sqlEnclaveSession = GetEnclaveSessionFromCache(enclaveSessionParameters, out counter);
                if (sqlEnclaveSession == null)
                {
                    if (!string.IsNullOrEmpty(enclaveSessionParameters.AttestationUrl))
                    {
                        // Deserialize the payload
                        AttestationInfo info = new AttestationInfo(attestationInfo);

                        // Verify enclave policy matches expected policy
                        VerifyEnclavePolicy(info.EnclaveReportPackage);

                        // Perform Attestation per VSM protocol
                        VerifyAttestationInfo(enclaveSessionParameters.AttestationUrl, info.HealthReport, info.EnclaveReportPackage);

                        // Set up shared secret and validate signature
                        byte[] sharedSecret = GetSharedSecret(info.Identity, info.EnclaveDHInfo, clientDHKey);

                        // add session to cache
                        sqlEnclaveSession = AddEnclaveSessionToCache(enclaveSessionParameters, sharedSecret, info.SessionId, out counter);
                    }
                    else
                    {
                        throw SQL.AttestationFailed(Strings.FailToCreateEnclaveSession);
                    }
                }
            }
            finally
            {
                UpdateEnclaveSessionLockStatus(sqlEnclaveSession);
            }
        }

        // When overridden in a derived class, looks up and evicts an enclave session from the enclave session cache, if the provider implements session caching.
        internal override void InvalidateEnclaveSession(EnclaveSessionParameters enclaveSessionParameters, SqlEnclaveSession enclaveSessionToInvalidate)
        {
            InvalidateEnclaveSessionHelper(enclaveSessionParameters, enclaveSessionToInvalidate);
        }

        #endregion

        #region Private helpers

        // Performs Attestation per the protocol used by Virtual Secure Modules.
        private void VerifyAttestationInfo(string attestationUrl, HealthReport healthReport, EnclaveReportPackage enclaveReportPackage)
        {
            bool shouldRetryValidation;
            bool shouldForceUpdateSigningKeys = false;
            do
            {
                shouldRetryValidation = false;

                // Get HGS Root signing certs from HGS
                X509Certificate2Collection signingCerts = GetSigningCertificate(attestationUrl, shouldForceUpdateSigningKeys);

                // Verify SQL Health report root chain of trust is the HGS root signing cert
                if (!VerifyHealthReportAgainstRootCertificate(signingCerts, healthReport.Certificate, out X509ChainStatusFlags chainStatus) ||
                    chainStatus != X509ChainStatusFlags.NoError)
                {
                    // In cases if we fail to validate the health report, it might be possible that we are using old signing keys
                    // let's re-download the signing keys again and re-validate the health report
                    if (!shouldForceUpdateSigningKeys)
                    {
                        shouldForceUpdateSigningKeys = true;
                        shouldRetryValidation = true;
                    }
                    else
                    {
                        throw SQL.AttestationFailed(string.Format(Strings.VerifyHealthCertificateChainFormat, attestationUrl, chainStatus));
                    }
                }
            } while (shouldRetryValidation);

            // Verify enclave report is signed by IDK_S from health report
            VerifyEnclaveReportSignature(enclaveReportPackage, healthReport.Certificate);
        }

        // Makes a web request to the provided url and returns the response as a byte[]
        protected abstract byte[] MakeRequest(string url);

        // Gets the root signing certificate for the provided attestation service.
        // If the certificate does not exist in the cache, this will make a call to the
        // attestation service's "/signingCertificates" endpoint. This endpoint can
        // return multiple certificates if the attestation service consists
        // of multiple nodes.
        private X509Certificate2Collection GetSigningCertificate(string attestationUrl, bool forceUpdate)
        {
            attestationUrl = GetAttestationUrl(attestationUrl);
            X509Certificate2Collection signingCertificates = rootSigningCertificateCache.Get<X509Certificate2Collection>(attestationUrl);
            if (forceUpdate || signingCertificates == null || AnyCertificatesExpired(signingCertificates))
            {
                byte[] data = MakeRequest(attestationUrl);
                var certificateCollection = new X509Certificate2Collection();

                try
                {
                    SignedCms s = new SignedCms();
                    s.Decode(data);
                    certificateCollection.AddRange(s.Certificates);
                }
                catch (CryptographicException exception)
                {
                    throw SQL.AttestationFailed(string.Format(Strings.GetAttestationSigningCertificateFailedInvalidCertificate, attestationUrl), exception);
                }

                MemoryCacheEntryOptions options = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1)
                };
                rootSigningCertificateCache.Set<X509Certificate2Collection>(attestationUrl, certificateCollection, options);
            }

            return rootSigningCertificateCache.Get<X509Certificate2Collection>(attestationUrl);
        }

        // Return the endpoint for given attestation url
        protected abstract string GetAttestationUrl(string attestationUrl);

        // Checks if any certificates in the collection are expired
        private bool AnyCertificatesExpired(X509Certificate2Collection certificates)
        {
            if (certificates != null)
            {
                foreach (object item in certificates)
                {
                    if (item is X509Certificate2 certificate && certificate.NotAfter < DateTime.Now)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Verifies that a chain of trust can be built from the health report provided
        /// by SQL Server and the attestation service's root signing certificate(s).
        /// 
        /// If the method returns false, the value of chainStatus doesn't matter. The chain could not be validated.
        /// </summary>
        /// <param name="signingCerts"></param>
        /// <param name="healthReportCert"></param>
        /// <param name="chainStatus"></param>
        /// <returns>A <see cref="T:System.Boolean" /> that indicates if the certificate was able to be verified.</returns>
        private bool VerifyHealthReportAgainstRootCertificate(X509Certificate2Collection signingCerts, X509Certificate2 healthReportCert, out X509ChainStatusFlags chainStatus)
        {
            var chain = new X509Chain();
            chainStatus = X509ChainStatusFlags.NoError;

            foreach (var cert in signingCerts)
            {
                chain.ChainPolicy.ExtraStore.Add(cert);
            }

            // An Always Encrypted-enabled driver doesn't verify an expiration date or a certificate authority chain.
            // A certificate is simply used as a key pair consisting of a public and private key. This is by design.

            #pragma warning disable IA5352
            // CodeQL [SM00395] By design. Always Encrypted certificates should not be checked.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            #pragma warning restore IA5352

            if (!chain.Build(healthReportCert))
            {
                bool untrustedRoot = false;

                // iterate over the chain status to check why the build failed
                foreach (X509ChainStatus status in chain.ChainStatus)
                {
                    if (status.Status == X509ChainStatusFlags.UntrustedRoot)
                    {
                        untrustedRoot = true;
                    }
                    else
                    {
                        chainStatus = status.Status;
                        return true;
                    }
                }
                // The only ways past or out of the loop are:
                // 1. untrustedRoot is true, in which case we want to continue to below
                // 2. chainStatus is set to the first status in the chain and we return true
                // 3. the ChainStatus is empty

                // if the chain failed with untrusted root, this could be because the client doesn't have the root cert
                // installed. If the chain's untrusted root cert has the same thumbprint as the signing cert, then we
                // do trust it.
                if (untrustedRoot)
                {
                    // iterate through the certificate chain, starting at the root since it's likely the
                    // signing certificate is the root
                    for (int i = 0; i < chain.ChainElements.Count; i++)
                    {
                        X509ChainElement element = chain.ChainElements[chain.ChainElements.Count - 1 - i];

                        foreach (X509Certificate2 cert in signingCerts)
                        {
                            if (element.Certificate.Thumbprint == cert.Thumbprint)
                            {
                                return true;
                            }
                        }
                    }

                    // in the case where we didn't find matching thumbprint
                    chainStatus = X509ChainStatusFlags.UntrustedRoot;
                    return true;
                }

                // There was an unknown failure and X509Chain.Build() returned an empty ChainStatus.
                return false;
            }

            return true;
        }

        // Verifies the enclave report signature using the health report.
        private void VerifyEnclaveReportSignature(EnclaveReportPackage enclaveReportPackage, X509Certificate2 healthReportCert)
        {
            // Check if report is formatted correctly
            uint calculatedSize = Convert.ToUInt32(EnclaveReportPackageHeader.SizeInPayload) + enclaveReportPackage.PackageHeader.SignedStatementSize + enclaveReportPackage.PackageHeader.SignatureSize;

            if (calculatedSize != enclaveReportPackage.PackageHeader.PackageSize)
            {
                throw new ArgumentException(Strings.VerifyEnclaveReportFormatFailed);
            }

            using (RSA rsa = KeyConverter.GetRSAFromCertificate(healthReportCert))
            {
                if (!rsa.VerifyData(enclaveReportPackage.ReportAsBytes, enclaveReportPackage.SignatureBlob, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                {
                    throw new ArgumentException(Strings.VerifyEnclaveReportFailed);
                }
            }
        }

        // Verifies the enclave policy matches expected policy.
        private void VerifyEnclavePolicy(EnclaveReportPackage enclaveReportPackage)
        {
            EnclaveIdentity identity = enclaveReportPackage.Report.Identity;

            VerifyEnclavePolicyProperty("OwnerId", identity.OwnerId, ExpectedPolicy.OwnerId);
            VerifyEnclavePolicyProperty("AuthorId", identity.AuthorId, ExpectedPolicy.AuthorId);
            VerifyEnclavePolicyProperty("FamilyId", identity.FamilyId, ExpectedPolicy.FamilyId);
            VerifyEnclavePolicyProperty("ImageId", identity.ImageId, ExpectedPolicy.ImageId);
            VerifyEnclavePolicyProperty("EnclaveSvn", identity.EnclaveSvn, ExpectedPolicy.EnclaveSvn);
            VerifyEnclavePolicyProperty("SecureKernelSvn", identity.SecureKernelSvn, ExpectedPolicy.SecureKernelSvn);
            VerifyEnclavePolicyProperty("PlatformSvn", identity.PlatformSvn, ExpectedPolicy.PlatformSvn);

            // This is a check that the enclave is running without debug support or not.
            //
            if (identity.Flags != ExpectedPolicy.Flags)
            {
                throw new InvalidOperationException(Strings.VerifyEnclaveDebuggable);
            }
        }

        // Verifies a byte[] enclave policy property
        private void VerifyEnclavePolicyProperty(string property, byte[] actual, byte[] expected)
        {
            bool different = false;
            if (actual == null || expected == null)
            {
                different = true;
            }
            else
            {
                if (actual.Length != expected.Length)
                {
                    different = true;
                }
                else
                {
                    for (int index = 0; index < actual.Length; index++)
                    {
                        if (actual[index] != expected[index])
                        {
                            different = true;
                            break;
                        }
                    }
                }
            }

            if (different)
            {
                string exceptionMessage = String.Format(Strings.VerifyEnclavePolicyFailedFormat, property, BitConverter.ToString(actual), BitConverter.ToString(expected));
                throw new ArgumentException(exceptionMessage);
            }
        }

        // Verifies a uint enclave policy property
        private void VerifyEnclavePolicyProperty(string property, uint actual, uint expected)
        {
            if (actual < expected)
            {
                string exceptionMessage = string.Format(Strings.VerifyEnclavePolicyFailedFormat, property, actual, expected);
                throw new ArgumentException(exceptionMessage);
            }
        }

        // Derives the shared secret between the client and enclave.
        private byte[] GetSharedSecret(EnclavePublicKey enclavePublicKey, EnclaveDiffieHellmanInfo enclaveDHInfo, ECDiffieHellman clientDHKey)
        {
            // Perform signature verification. The enclave's DiffieHellman public key was signed by the enclave's RSA public key.
            using (RSA rsa = KeyConverter.CreateRSAFromPublicKeyBlob(enclavePublicKey.PublicKey))
            {
                if (!rsa.VerifyData(enclaveDHInfo.PublicKey, enclaveDHInfo.PublicKeySignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    throw new ArgumentException(Strings.GetSharedSecretFailed);
                }
            }

            using (ECDiffieHellman ecdh = KeyConverter.CreateECDiffieHellmanFromPublicKeyBlob(enclaveDHInfo.PublicKey))
            {
                return KeyConverter.DeriveKey(clientDHKey, ecdh.PublicKey);
            }
        }
        #endregion
    }
}
