// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

internal partial class Interop
{
    internal partial class NtDll
    {
        /// <summary>
        /// The system cannot find message text for the provided error number.
        /// </summary>
        public const int ERROR_MR_MID_NOT_FOUND = 317;

        // https://msdn.microsoft.com/en-us/library/windows/desktop/ms680600(v=vs.85).aspx
        [DllImport(Libraries.NtDll, ExactSpelling = true)]
        public unsafe static extern uint RtlNtStatusToDosError(
            int Status);
    }
}
