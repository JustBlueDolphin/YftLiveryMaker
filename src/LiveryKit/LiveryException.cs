using System;

namespace LiveryKit
{
    /// <summary>A problem the user can act on; the message is written for them.</summary>
    public sealed class LiveryException : Exception
    {
        public LiveryException(string message) : base(message) { }
        public LiveryException(string message, Exception inner) : base(message, inner) { }
    }
}
