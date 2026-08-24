#nullable enable

namespace Conduit
{
    static class BridgeMessageTypes
    {
        public const string Hello = "hello";
        public const string Command = "command";
        public const string CancelCommand = "cancel_command";
        public const string CommandStarted = "command_started";
        public const string CommandResult = "command_result";
    }
}
