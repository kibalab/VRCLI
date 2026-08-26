using System;

namespace KibaLab.WorldDeployment.Editor
{
    internal sealed class LoginException : Exception
    {
        public LoginException(string message) : base(message)
        {
        }
    }

    internal sealed class ContentOwnershipException : Exception
    {
        public ContentOwnershipException(string message) : base(message)
        {
        }
    }
}
