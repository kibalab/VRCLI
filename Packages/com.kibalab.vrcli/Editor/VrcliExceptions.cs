using System;

namespace KibaLab.VRCLI.Editor
{
    internal sealed class VrcliAuthenticationException : Exception
    {
        public VrcliAuthenticationException(string message) : base(message)
        {
        }
    }

    internal sealed class VrcliOwnershipException : Exception
    {
        public VrcliOwnershipException(string message) : base(message)
        {
        }
    }
}

