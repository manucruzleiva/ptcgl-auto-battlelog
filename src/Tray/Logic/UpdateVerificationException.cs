using System;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>An update was refused: untrusted URL, implausible manifest, or a download that does not match it.</summary>
    public sealed class UpdateVerificationException : Exception
    {
        public UpdateVerificationException() : base("The update could not be verified.") { }
        public UpdateVerificationException(string message) : base(message) { }
        public UpdateVerificationException(string message, Exception inner) : base(message, inner) { }
    }
}
