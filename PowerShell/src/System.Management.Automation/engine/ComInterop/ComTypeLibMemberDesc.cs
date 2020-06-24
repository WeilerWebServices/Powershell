// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if !SILVERLIGHT // ComObject

namespace System.Management.Automation.ComInterop
{
    internal class ComTypeLibMemberDesc
    {
        internal ComTypeLibMemberDesc(ComType kind)
        {
            Kind = kind;
        }

        public ComType Kind { get; }
    }
}

#endif

