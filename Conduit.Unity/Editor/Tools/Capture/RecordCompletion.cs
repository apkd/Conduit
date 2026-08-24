#nullable enable

namespace Conduit
{
    readonly struct RecordCompletion
    {
        RecordCompletion(bool succeeded, string message)
        {
            Succeeded = succeeded;
            Message = message;
        }

        internal bool Succeeded { get; }
        internal string Message { get; }

        internal static RecordCompletion Success(string message) => new(true, message);
        internal static RecordCompletion Failure(string message) => new(false, message);
    }
}
