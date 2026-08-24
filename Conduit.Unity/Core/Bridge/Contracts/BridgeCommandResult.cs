#nullable enable

using System;

namespace Conduit
{
    [Serializable]
    sealed class BridgeCommandResult
    {
        public string outcome = ToolOutcome.Success;
        public string logs = string.Empty;
        public string? display_name;
        public string? return_value;
        public BridgeExceptionInfo? exception;
        public string? diagnostic;
        public BridgeArtifact[] artifacts = Array.Empty<BridgeArtifact>();

        public string Outcome { get => outcome; set => outcome = value; }
        public string Logs { get => logs; set => logs = value; }
        public string? DisplayName { get => display_name; set => display_name = value; }
        public string? ReturnValue { get => return_value; set => return_value = value; }
        public BridgeExceptionInfo? Exception { get => exception; set => exception = value; }
        public string? Diagnostic { get => diagnostic; set => diagnostic = value; }
        public BridgeArtifact[] Artifacts { get => artifacts; set => artifacts = value; }

        public static BridgeCommandResult Success(string? value = null)
            => new() { return_value = value };

        public static BridgeCommandResult Error(string diagnostic)
            => new()
            {
                outcome = ToolOutcome.Exception,
                diagnostic = diagnostic,
            };

        public static BridgeCommandResult Ambiguous(string diagnostic)
            => new()
            {
                outcome = ToolOutcome.AmbiguousTarget,
                diagnostic = diagnostic,
            };

        public static BridgeCommandResult EditorOnly(string commandName)
            => new()
            {
                outcome = ToolOutcome.Exception,
                diagnostic = $"The tool `{commandName}` is editor-only.",
            };

        public static BridgeCommandResult UnsupportedEditorTool(string commandName)
            => new()
            {
                outcome = ToolOutcome.Exception,
                diagnostic = $"Unity Editor bridge protocol {BridgeContract.Version} does not support the `{commandName}` tool.",
            };

        public static BridgeCommandResult FromException(Exception exception)
        {
            var info = BridgeExceptionFormatter.ToInfo(exception);
            return new()
            {
                outcome = ToolOutcome.Exception,
                exception = info,
                diagnostic = BridgeExceptionFormatter.NormalizeDiagnostic(exception.Message, info.message),
            };
        }
    }
}
