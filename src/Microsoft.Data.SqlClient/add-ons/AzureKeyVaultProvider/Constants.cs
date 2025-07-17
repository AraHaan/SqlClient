// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.Data.SqlClient.AlwaysEncrypted.AzureKeyVaultProvider
{
    internal static class Constants
    {
        /// <summary>
        /// Azure Key Vault Domain Name
        /// </summary>
        internal static readonly string[] AzureKeyVaultPublicDomainNames =
        [
            // Azure Key Vaults
            "vault.azure.net",                 // Default
            "vault.azure.cn",                  // China
            "vault.usgovcloudapi.net",         // US Government
            "vault.microsoftazure.de",         // Azure Germany
            "vault.cloudapi.microsoft.scloud", // USSec
            "vault.cloudapi.eaglex.ic.gov",    // USNat
            "vault.sovcloud-api.fr",           // France (Bleu)
            "vault.sovcloud-api.de",           // Germany (Delos)

            // Managed High Security Modules (HSM) Vaults
            "managedhsm.azure.net",
            "managedhsm.azure.cn",
            "managedhsm.usgovcloudapi.net",
            "managedhsm.microsoftazure.de",
            "managedhsm.cloudapi.microsoft.scloud",
            "managedhsm.cloudapi.eaglex.ic.gov",
            "managedhsm.sovcloud-api.fr",
            "managedhsm.sovcloud-api.de"
        ];

        /// <summary>
        /// Always Encrypted Parameter names for exec handling
        /// </summary>
        internal const string AeParamColumnEncryptionKey = "columnEncryptionKey";
        internal const string AeParamEncryptionAlgorithm = "encryptionAlgorithm";
        internal const string AeParamMasterKeyPath = "masterKeyPath";
        internal const string AeParamEncryptedCek = "encryptedColumnEncryptionKey";
    }
}
