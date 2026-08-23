#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Conduit
{
    /// <summary>Builds compact, stable exception details for bridge results.</summary>
    static class BridgeExceptionFormatter
    {
        const string TargetInvocationDiagnostic = "Exception has been thrown by the target of an invocation.";
        static readonly Regex stackTraceFilePattern = new(@"\s*\[0x[0-9a-fA-F]+\]\s+in\s+(.+?)(?::line\s+|:)(\d+)\s*$", RegexOptions.Compiled);
        static readonly Regex runtimeLocationPattern = new(@"\s*\(<[^>]+>:\d+\)\s*$", RegexOptions.Compiled);
        static readonly Regex stateMachineFramePattern = new(@"^(?<type>.+)/<(?<method>.+)>d(?:__\d+)?:MoveNext$", RegexOptions.Compiled);
        static readonly Regex localFunctionNamePattern = new(@"^<(?<outer>[^>]+)>g__(?<local>[^|]+)\|[\d_]+$", RegexOptions.Compiled);

        public static BridgeExceptionInfo ToInfo(Exception exception)
        {
            var effective = exception is TargetInvocationException { InnerException: { } inner }
                ? inner
                : exception;
            return new()
            {
                type = SimplifyTypeName(effective.GetType().FullName ?? effective.GetType().Name),
                message = NormalizeUserFacingText(effective.Message) ?? string.Empty,
                stack_trace = SimplifyStackTrace(effective.StackTrace),
            };
        }

        public static string? NormalizeDiagnostic(string? diagnostic, string? exceptionMessage)
        {
            if (NormalizeUserFacingText(diagnostic) is not { Length: > 0 } normalizedDiagnostic)
                return null;

            var normalizedExceptionMessage = NormalizeUserFacingText(exceptionMessage);
            if (normalizedDiagnostic == normalizedExceptionMessage)
                return null;
            return normalizedDiagnostic == TargetInvocationDiagnostic
                   && !string.IsNullOrWhiteSpace(normalizedExceptionMessage)
                ? null
                : normalizedDiagnostic;
        }

        public static string? NormalizeUserFacingText(string? value)
            => value is not { Length: > 0 } ? value : value.Replace('"', '\'');

        public static string SimplifyTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return string.Empty;

            var separator = Math.Max(typeName.LastIndexOf('.'), typeName.LastIndexOf('+'));
            return separator >= 0 && separator + 1 < typeName.Length
                ? typeName.Substring(separator + 1)
                : typeName;
        }

        public static string? SimplifyStackTrace(string? stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
                return null;

            try
            {
                using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
                using var reader = new StringReader(stackTrace);
                string? pendingSourceWrapper = null;
                while (reader.ReadLine() is { } line)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0
                        || IsInternalStackTraceFrame(trimmed)
                        || IsAsyncMethodBuilderFrame(trimmed))
                        continue;

                    var frame = SimplifyStackTraceLine(trimmed);
                    if (pendingSourceWrapper is { } sourceWrapper)
                    {
                        pendingSourceWrapper = null;
                        if (!frame.IsStateMachine && frame.Identity == sourceWrapper)
                            continue;
                    }

                    if (builder.Length > 0)
                        builder.Append('\n');
                    builder.Append(frame.Text);
                    pendingSourceWrapper = frame.IsStateMachine ? frame.Identity : null;
                }

                return builder.Length == 0 ? null : builder.ToString();
            }
            catch
            {
                return stackTrace;
            }
        }

        public static string? TrimCommonLogTail(string? stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
                return stackTrace;

            var end = stackTrace!.Length;
            var removedAny = false;
            while (true)
            {
                while (end > 0 && stackTrace[end - 1] is '\r' or '\n')
                    end--;
                if (end == 0)
                    return null;

                var lastLineBreak = stackTrace.LastIndexOf('\n', end - 1);
                var lineStart = lastLineBreak < 0 ? 0 : lastLineBreak + 1;
                var frame = stackTrace.Substring(lineStart, end - lineStart).TrimEnd();
                if (!IsCommonLogTailFrame(frame))
                    break;

                removedAny = true;
                end = lineStart == 0 ? 0 : lineStart - 1;
            }

            if (!removedAny)
                return stackTrace;

            var trimmed = stackTrace.Substring(0, end).TrimEnd('\r', '\n');
            return trimmed.Length == 0 ? null : trimmed;
        }

        static bool IsInternalStackTraceFrame(string line)
            => line.Contains("Conduit.", StringComparison.Ordinal)
               || line.Contains("ConduitGenerated.", StringComparison.Ordinal);

        static bool IsAsyncMethodBuilderFrame(string line)
        {
            var frame = line.StartsWith("at ", StringComparison.Ordinal) ? line.Substring(3) : line;
            return frame.StartsWith("System.Runtime.CompilerServices.AsyncVoidMethodBuilder", StringComparison.Ordinal)
                   || frame.StartsWith("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", StringComparison.Ordinal)
                   || frame.StartsWith("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder", StringComparison.Ordinal);
        }

        static bool IsCommonLogTailFrame(string frame)
            => frame is "System.Reflection.MethodBase:Invoke"
                or "UnityEngine.UnitySynchronizationContext:ExecuteTasks"
                or "NUnit.Framework.Internal.MethodWrapper:Invoke"
                or "NUnit.Framework.Internal.Commands.TestMethodCommand:RunNonAsyncTestMethod"
                or "NUnit.Framework.Internal.Commands.TestMethodCommand:RunTestMethod"
                or "NUnit.Framework.Internal.Commands.TestMethodCommand:Execute"
                or "UnityEditor.EditorApplication:Internal_CallUpdateFunctions"
                || frame.StartsWith("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", StringComparison.Ordinal)
                && frame.EndsWith(":SetResult", StringComparison.Ordinal)
                || frame.StartsWith("System.Threading.Tasks.TaskCompletionSource", StringComparison.Ordinal)
                && frame.EndsWith(":TrySetResult", StringComparison.Ordinal)
                || frame.StartsWith(
                    "UnityEditor.Scripting.ScriptCompilation.EditorCompilationInterface:IsCompiling",
                    StringComparison.Ordinal
                );

        static (string Text, string Identity, bool IsStateMachine) SimplifyStackTraceLine(string line)
        {
            var match = stackTraceFilePattern.Match(line);
            var frame = match.Success
                ? line.Substring(0, match.Index).TrimEnd()
                : runtimeLocationPattern.Replace(line, string.Empty).TrimEnd();
            var simplified = SimplifyGeneratedMethodFrame(RemoveMethodParameters(frame));
            if (!match.Success)
                return (simplified.Text, simplified.Text, simplified.IsStateMachine);

            var fileName = Path.GetFileName(match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
            return ($"{simplified.Text} ({fileName}:{match.Groups[2].Value})", simplified.Text, simplified.IsStateMachine);
        }

        static (string Text, bool IsStateMachine) SimplifyGeneratedMethodFrame(string frame)
        {
            var stateMachineMatch = stateMachineFramePattern.Match(frame);
            if (stateMachineMatch.Success
                && SimplifyStateMachineMethodName(stateMachineMatch.Groups["method"].Value) is { } methodName)
                return (stateMachineMatch.Groups["type"].Value + ':' + methodName, true);

            var separator = frame.LastIndexOf(':');
            return separator >= 0
                   && SimplifyLocalFunctionName(frame.Substring(separator + 1)) is { } localFunctionName
                ? (frame.Substring(0, separator + 1) + localFunctionName, false)
                : (frame, false);
        }

        static string? SimplifyStateMachineMethodName(string generatedName)
            => SimplifyLocalFunctionName(generatedName)
               ?? (generatedName.Length > 0
                   && generatedName.IndexOf('<') < 0
                   && generatedName.IndexOf('>') < 0
                    ? generatedName
                    : null);

        static string? SimplifyLocalFunctionName(string generatedName)
        {
            var match = localFunctionNamePattern.Match(generatedName);
            return match.Success
                ? match.Groups["outer"].Value + '.' + match.Groups["local"].Value
                : null;
        }

        static string RemoveMethodParameters(string line)
        {
            var close = line.LastIndexOf(')');
            if (close < 0)
                return line;
            var open = line.LastIndexOf('(', close);
            return open < 0 ? line : line.Remove(open, close - open + 1).TrimEnd();
        }
    }
}
