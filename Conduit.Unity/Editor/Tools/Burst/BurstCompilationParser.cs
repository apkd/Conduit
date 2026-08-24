#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Conduit
{
    static class BurstCompilationParser
    {
        static readonly Regex remarkSourceLocation = new(@"^(?<file>.+?):(?<line>\d+):\d+:\s*(?<message>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static BurstCompilationContext ParseCompilationContext(string options, string cpu)
        {
            // inspector overrides are appended to Burst's resolved options, so the last occurrence is authoritative.
            var lines = BurstOutputFormatter.SplitLines(options);
            var compilerTarget = string.Empty;
            var optimizationLevel = string.Empty;
            var floatMode = string.Empty;
            var floatPrecision = string.Empty;
            var safetyChecks = string.Empty;
            var optimizationDisabled = false;
            var optimizeForSize = false;
            var safetyDisabled = false;
            foreach (var line in lines)
            {
                var text = line.Trim();
                if (text == "--disable-opt")
                    optimizationDisabled = true;
                else if (text == "--opt-for-size")
                    optimizeForSize = true;
                else if (text == "--disable-safety-checks")
                    safetyDisabled = true;
                else if (TryReadOption(text, "--target=", out var value))
                    compilerTarget = value;
                else if (TryReadOption(text, "--opt-level=", out value))
                    optimizationLevel = value;
                else if (TryReadOption(text, "--float-mode=", out value))
                    floatMode = value;
                else if (TryReadOption(text, "--float-precision=", out value))
                    floatPrecision = value;
                else if (TryReadOption(text, "--global-safety-checks-setting=", out value))
                    safetyChecks = value;
            }

            var optimization = optimizationDisabled ? "Disabled"
                : optimizeForSize ? "Size"
                : optimizationLevel switch
                {
                    "1" => "Fast compilation",
                    "2" => "Balanced",
                    "3" => "Performance",
                    { Length: > 0 } value => $"Optimization level {value}",
                    _ => "Default",
                };
            if (floatMode.Length == 0 || string.Equals(floatMode, "Default", StringComparison.OrdinalIgnoreCase))
                floatMode = "Strict";
            if (floatPrecision.Length == 0)
                floatPrecision = "Standard";
            if (safetyDisabled || safetyChecks.Length == 0)
                safetyChecks = safetyDisabled ? "Off" : "Default";

            return new(
                cpu.Length == 0 ? "x86" : cpu,
                compilerTarget.Length == 0 ? "AVX2" : compilerTarget,
                optimization,
                floatMode,
                floatPrecision,
                safetyChecks
            );

            static bool TryReadOption(string text, string prefix, out string value)
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    value = text[prefix.Length..];
                    return true;
                }

                value = string.Empty;
                return false;
            }
        }

        internal static void ParseOptimizationRemarks(string text, List<BurstOptimizationRemark> destination)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var type = string.Empty;
            var message = string.Empty;
            var pass = string.Empty;
            var reason = string.Empty;
            var function = string.Empty;
            var currentField = string.Empty;
            // structured IRPassAnalysis output can repeat the same remark within one compilation.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in BurstOutputFormatter.SplitLines(text))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("---", StringComparison.Ordinal))
                {
                    Flush();
                    continue;
                }

                if (TryRead(trimmed, "Remark Type:", "type", out var value))
                    type = value;
                else if (TryRead(trimmed, "Message:", "message", out value))
                    message = value;
                else if (TryRead(trimmed, "Pass:", "pass", out value))
                    pass = value;
                else if (TryRead(trimmed, "Remark:", "reason", out value))
                    reason = value;
                else if (TryRead(trimmed, "Function:", "function", out value))
                    function = value;
                else if (trimmed.Length > 0)
                    AppendContinuation(trimmed);
            }
            Flush();

            bool TryRead(string text, string prefix, string field, out string value)
            {
                value = string.Empty;
                if (!text.StartsWith(prefix, StringComparison.Ordinal))
                    return false;

                currentField = field;
                value = text[prefix.Length..].Trim();
                return true;
            }

            void AppendContinuation(string value)
            {
                switch (currentField)
                {
                    case "type":
                        type = JoinField(type, value);
                        break;
                    case "message":
                        message = JoinField(message, value);
                        break;
                    case "pass":
                        pass = JoinField(pass, value);
                        break;
                    case "reason":
                        reason = JoinField(reason, value);
                        break;
                    case "function":
                        function = JoinField(function, value);
                        break;
                }
            }

            void Flush()
            {
                if (message.Length == 0)
                {
                    Reset();
                    return;
                }

                var source = string.Empty;
                var match = remarkSourceLocation.Match(message);
                if (match.Success)
                {
                    source = $"{Path.GetFileName(match.Groups["file"].Value.Replace('\\', '/'))}:{match.Groups["line"].Value}";
                    message = match.Groups["message"].Value;
                }

                type = BurstSymbolFormatter.CleanDiagnosticLine(type);
                message = BurstSymbolFormatter.CleanDiagnosticLine(message);
                pass = BurstSymbolFormatter.CleanDiagnosticLine(pass);
                reason = BurstSymbolFormatter.CleanDiagnosticLine(reason);
                function = BurstSymbolFormatter.CleanDiagnosticLine(function);
                var key = $"{type}\0{message}\0{pass}\0{reason}\0{function}";
                if (seen.Add(key))
                    destination.Add(new(
                        type.Length == 0 ? "Remark" : type,
                        message,
                        pass,
                        reason,
                        function,
                        source
                    ));
                Reset();
            }

            void Reset()
            {
                type = string.Empty;
                message = string.Empty;
                pass = string.Empty;
                reason = string.Empty;
                function = string.Empty;
                currentField = string.Empty;
            }

            static string JoinField(string current, string value) =>
                current.Length == 0 ? value : current + " " + value;
        }
    }
}
