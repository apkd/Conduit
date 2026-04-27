#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Conduit
{
    static class view_burst_asm
    {
        const int MaxCandidates = 10;
        const int ClearMatchGap = 25;
        static readonly Regex tempLabel = new(@"^\s*\.Ltmp\d+:\s*(?:[#;].*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex burstError = new(@"^.*\(\d+,\d+\):\sBurst\serror", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex sourceLocation = new(@"^(?<prefix>\s*#\s+)(?<file>.+?)\((?<line>\d+),\s*\d+\)(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex assemblyQualifier = new(@",\s*[^,\]\)>]+,\s*Version=[0-9.]+,\s*Culture=[^,\]\)>\s]+,\s*PublicKeyToken=(?:null|[0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex builtInTypeName = new(@"\b(?:System\.)?(?:Void|Boolean|Byte|SByte|Char|Decimal|Double|Single|Int32|UInt32|Int64|UInt64|Int16|UInt16|Object|String|IntPtr|UIntPtr)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex qualifiedTypeName = new(@"\b(?:[A-Z_][A-Za-z0-9_]*\.)+[A-Z_][A-Za-z0-9_]*(?:\+[A-Z_][A-Za-z0-9_]*)*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex guidId = new(@"(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static BridgeCommandResult ViewBurstAsm(string targetName)
        {
#if MODULE_BURST
            try
            {
                var targets = LoadTargets();
                if (targets.Count == 0)
                    return Error("No Burst compile targets were found in the current editor domain.");

                var match = MatchTarget(targetName, targets);
                return match.Kind switch
                {
                    BurstAsmTargetMatchKind.Matched   => Compile(targets[match.SelectedIndex]),
                    BurstAsmTargetMatchKind.Ambiguous => Ambiguous(targetName, targets, match.CandidateIndexes),
                    _                                 => NoMatch(targetName, targets, match.CandidateIndexes),
                };
            }
            catch (Exception exception)
            {
                return Error($"Could not inspect Burst assembly: {Unwrap(exception).Message}");
            }
#else
            _ = targetName;
            return Error("Burst is not installed or not available in this Unity project. Install com.unity.burst to use view_burst_asm.");
#endif
        }

#if MODULE_BURST
        static List<BurstTarget> LoadTargets()
        {
            var reflectionType = Type.GetType("Unity.Burst.Editor.BurstReflection, Unity.Burst", true)!;
            reflectionType.GetMethod("EnsureInitialized", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

            var assemblies = reflectionType
                .GetField("EditorAssembliesThatCanPossiblyContainJobs", BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null);
            var optionsType = Type.GetType("Unity.Burst.Editor.BurstReflectionAssemblyOptions, Unity.Burst", true)!;
            var result = reflectionType
                .GetMethod("FindExecuteMethods", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new[] { assemblies, Enum.ToObject(optionsType, 0) });
            var compileTargets = (IEnumerable)result!
                .GetType()
                .GetField("CompileTargets", BindingFlags.Public | BindingFlags.Instance)!
                .GetValue(result)!;

            var targets = new List<BurstTarget>();
            foreach (var target in compileTargets)
                if (target.GetType().GetProperty("HasRequiredBurstCompileAttributes")?.GetValue(target) is true)
                    targets.Add(ReadTarget(target));

            targets.Sort((left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.Ordinal));
            return targets;
        }

        static BurstTarget ReadTarget(object target)
        {
            var type = target.GetType();
            var method = (MethodInfo)type.GetField("Method")!.GetValue(target)!;
            var jobType = (Type)type.GetField("JobType")!.GetValue(target)!;
            return new(
                displayName: (string)type.GetMethod("GetDisplayName")!.Invoke(target, null)!,
                methodName: method.Name,
                declaringTypeName: method.DeclaringType?.FullName ?? string.Empty,
                jobTypeName: jobType.FullName ?? jobType.Name,
                method: method,
                jobType: jobType,
                options: type.GetField("Options")!.GetValue(target)!,
                isStaticMethod: type.GetField("IsStaticMethod")!.GetValue(target) is true
            );
        }

        static BridgeCommandResult Compile(BurstTarget target)
        {
            var rawDisassembly = GetInspectorDisassembly(target.Method!, BuildOptions(target));
            if (IsBurstError(rawDisassembly))
                return Error(rawDisassembly.Trim());

            var disassembly = StripTrailingTemporaryLabelBlocks(CleanDisassembly(RenderEnhancedDisassembly(rawDisassembly).TrimStart('\n')));

            if (string.IsNullOrWhiteSpace(disassembly))
                return Error($"Burst returned empty assembly for '{target.DisplayName}'.");

            return Success($"{target.DisplayName}\n{disassembly}");
        }

        static string BuildOptions(BurstTarget target)
        {
            var options = target.Options!;
            ApplyInspectorOptionOverrides(options);

            var member = target.IsStaticMethod ? (MemberInfo)target.Method! : target.JobType!;
            var args = new object?[] { member, null, false, true, false };
            var tryGetOptions = options
                .GetType()
                .GetMethod(
                    "TryGetOptions",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(MemberInfo), typeof(string).MakeByRefType(), typeof(bool), typeof(bool), typeof(bool) },
                    null
                )!;

            if (tryGetOptions.Invoke(options, args) is not true)
                throw new InvalidOperationException($"Burst compiler options were not available for '{target.DisplayName}'.");

            return BuildInspectorOptions((string?)args[1] ?? string.Empty);
        }

        static string GetInspectorDisassembly(MethodInfo method, string options) =>
            (string?)Type.GetType("Unity.Burst.Editor.BurstInspectorGUI, Unity.Burst.Editor", true)!
                .GetMethod("GetDisassembly", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { method, options })
            ?? string.Empty;

        static string RenderEnhancedDisassembly(string disassembly)
        {
            var disassemblerType = Type.GetType("Unity.Burst.Editor.BurstDisassembler, Unity.Burst.Editor", true)!;
            var asmKindType = Type.GetType("Unity.Burst.Editor.BurstDisassembler+AsmKind, Unity.Burst.Editor", true)!;
            var asmKind = Enum.Parse(asmKindType, HostIsArm() ? "ARM" : "Intel");
            var disassembler = Activator.CreateInstance(disassemblerType)!;

            disassemblerType
                .GetMethod("Initialize", new[] { typeof(string), asmKindType, typeof(bool), typeof(bool), typeof(bool) })!
                .Invoke(disassembler, new[] { disassembly, asmKind, true, false, false });

            return (string?)disassemblerType.GetMethod("RenderFullText")!.Invoke(disassembler, null)
                   ?? disassembly;
        }

        static bool HostIsArm()
        {
            var hostCpu = (string?)Type.GetType("Unity.Burst.BurstCompiler, Unity.Burst", true)!
                .GetMethod("GetTargetCpuFromHost", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(null, null);
            return hostCpu?.IndexOf("Arm", StringComparison.OrdinalIgnoreCase) >= 0;
        }
#endif

        internal static void ApplyInspectorOptionOverrides(object options)
        {
            SetBool(options, "EnableBurstSafetyChecks", false);
            SetBool(options, "ForceEnableBurstSafetyChecks", false);
            SetBool(options, "EnableBurstDebug", false);
        }

        internal static string BuildInspectorOptions(string defaultOptions)
        {
            var builder = new StringBuilder(defaultOptions.Trim());
            Append("--disable-warnings=BC1370;BC1322");
            Append("--target=Auto");
            Append("--debug=2");
            Append("--dump=Asm");
            return builder.ToString();

            void Append(string option)
            {
                if (builder.Length > 0)
                    builder.Append('\n');

                builder.Append(option);
            }
        }

        internal static BurstAsmTargetMatch MatchTarget(string? query, IReadOnlyList<BurstTarget> targets)
        {
            var text = query?.Trim() ?? string.Empty;
            if (text.Length == 0)
                return BurstAsmTargetMatch.None(FirstIndexes(targets));

            var matches = Find(targets, target => EqualsAny(target, text));
            if (matches.Count == 1)
                return BurstAsmTargetMatch.Matched(matches[0]);
            if (matches.Count > 1)
                return BurstAsmTargetMatch.Ambiguous(matches);

            matches = Find(targets, target => ContainsAny(target, text));
            if (matches.Count == 1)
                return BurstAsmTargetMatch.Matched(matches[0]);
            if (matches.Count > 1)
                return BurstAsmTargetMatch.Ambiguous(matches);

            var scored = Score(text, targets);
            if (scored.Count == 0)
                return BurstAsmTargetMatch.None(FirstIndexes(targets));

            scored.Sort((left, right) =>
            {
                var score = right.Score.CompareTo(left.Score);
                return score != 0
                    ? score
                    : string.Compare(targets[left.Index].DisplayName, targets[right.Index].DisplayName, StringComparison.Ordinal);
            });

            if (scored.Count == 1 || scored[0].Score - scored[1].Score >= ClearMatchGap)
                return BurstAsmTargetMatch.Matched(scored[0].Index);

            var candidates = new List<int>();
            var minimumScore = scored[0].Score - ClearMatchGap + 1;
            foreach (var candidate in scored)
            {
                if (candidate.Score < minimumScore || candidates.Count == MaxCandidates)
                    break;

                candidates.Add(candidate.Index);
            }

            return BurstAsmTargetMatch.Ambiguous(candidates);
        }

        internal static string StripTrailingTemporaryLabelBlocks(string assembly)
        {
            var lines = assembly.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var end = lines.Length - 1;
            while (end >= 0 && string.IsNullOrWhiteSpace(lines[end]))
                end--;

            var start = end;
            var sawLabel = false;
            while (start >= 0 && IsTemporarySuffixLine(lines[start], out var isLabel))
            {
                sawLabel |= isLabel;
                start--;
            }

            if (!sawLabel)
                return Join(lines, end + 1);

            var keep = start + 1;
            while (keep <= end && !tempLabel.IsMatch(lines[keep]))
                keep++;

            return Join(lines, keep);
        }

        internal static string CleanDisassembly(string assembly)
        {
            var lines = assembly.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
                lines[i] = CleanLine(lines[i]).TrimStart();

            return Join(lines, lines.Length);
        }

        static List<int> Find(IReadOnlyList<BurstTarget> targets, Func<BurstTarget, bool> predicate)
        {
            var matches = new List<int>();
            for (var i = 0; i < targets.Count; i++)
                if (predicate(targets[i]))
                    matches.Add(i);

            return matches;
        }

        static List<ScoredTarget> Score(string query, IReadOnlyList<BurstTarget> targets)
        {
            var tokens = Tokens(query);
            var matches = new List<ScoredTarget>();
            for (var i = 0; i < targets.Count; i++)
            {
                var score = Score(tokens, query, targets[i]);
                if (score > 0)
                    matches.Add(new(i, score));
            }

            return matches;
        }

        static int Score(string[] tokens, string query, BurstTarget target)
        {
            var score = 0;
            foreach (var token in tokens)
            {
                var part = 0;
                if (Contains(target.DisplayName, token))
                    part = Math.Max(part, 100);
                if (Contains(target.MethodName, token))
                    part = Math.Max(part, 90);
                if (Contains(target.DeclaringTypeName, token) || Contains(target.JobTypeName, token))
                    part = Math.Max(part, 70);
                if (part == 0)
                    return 0;

                score += part;
            }

            if (target.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                score += 50;
            if (target.MethodName.Equals(query, StringComparison.OrdinalIgnoreCase))
                score += 50;
            if (target.MethodName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                score += 25;

            return score;
        }

        static string[] Tokens(string text)
        {
            var tokens = new List<string>();
            var builder = new StringBuilder();
            foreach (var character in text)
            {
                if (char.IsLetterOrDigit(character) || character == '_')
                {
                    builder.Append(char.ToLowerInvariant(character));
                    continue;
                }

                Flush();
            }

            Flush();
            return tokens.ToArray();

            void Flush()
            {
                if (builder.Length == 0)
                    return;

                tokens.Add(builder.ToString());
                builder.Clear();
            }
        }

        static bool EqualsAny(BurstTarget target, string text) =>
            string.Equals(target.DisplayName, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target.MethodName, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target.DeclaringTypeName, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target.JobTypeName, text, StringComparison.OrdinalIgnoreCase);

        static bool ContainsAny(BurstTarget target, string text) =>
            Contains(target.DisplayName, text)
            || Contains(target.MethodName, text)
            || Contains(target.DeclaringTypeName, text)
            || Contains(target.JobTypeName, text);

        static bool Contains(string value, string text) =>
            value.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

        static int[] FirstIndexes(IReadOnlyList<BurstTarget> targets)
        {
            var indexes = new int[Math.Min(targets.Count, MaxCandidates)];
            for (var i = 0; i < indexes.Length; i++)
                indexes[i] = i;

            return indexes;
        }

        static bool IsTemporarySuffixLine(string line, out bool isLabel)
        {
            isLabel = tempLabel.IsMatch(line);
            if (isLabel)
                return true;

            var text = line.Trim();
            return text.Length == 0
                   || text.StartsWith("#", StringComparison.Ordinal)
                   || text.StartsWith("//", StringComparison.Ordinal)
                   || text.StartsWith(";", StringComparison.Ordinal)
                   || text.StartsWith(".", StringComparison.Ordinal) && text.IndexOf(':') < 0;
        }

        static string CleanLine(string line)
        {
            line = sourceLocation.Replace(
                line,
                match => $"{match.Groups["prefix"].Value}{match.Groups["file"].Value}:{match.Groups["line"].Value}{match.Groups["rest"].Value}"
            );

            return LimitGuidIds(CleanQuotedSymbols(line));
        }

        static string CleanQuotedSymbols(string line)
        {
            var firstQuote = line.IndexOf('"');
            if (firstQuote < 0)
                return line;

            var builder = new StringBuilder(line.Length);
            var offset = 0;
            while (offset < line.Length)
            {
                var start = line.IndexOf('"', offset);
                if (start < 0)
                {
                    builder.Append(line, offset, line.Length - offset);
                    break;
                }

                var end = FindClosingQuote(line, start + 1);
                if (end < 0)
                {
                    builder.Append(line, offset, line.Length - offset);
                    break;
                }

                builder.Append(line, offset, start - offset + 1);
                var symbol = line.Substring(start + 1, end - start - 1);
                builder.Append(ShouldCleanSymbol(symbol) ? CleanSymbol(symbol) : symbol);
                builder.Append('"');
                offset = end + 1;
            }

            return builder.ToString();
        }

        static int FindClosingQuote(string text, int start)
        {
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (text[i] == '"')
                    return i;
            }

            return -1;
        }

        static bool ShouldCleanSymbol(string symbol) =>
            symbol.IndexOf("Version=", StringComparison.Ordinal) >= 0
            || symbol.IndexOf("PublicKeyToken=", StringComparison.Ordinal) >= 0
            || symbol.IndexOf(" -> ", StringComparison.Ordinal) >= 0
            || symbol.IndexOf('`') >= 0
            || symbol.IndexOf("System.", StringComparison.Ordinal) >= 0;

        static string CleanSymbol(string symbol)
        {
            symbol = RemoveBurstLabelSuffix(symbol);
            symbol = assemblyQualifier.Replace(symbol, string.Empty);
            symbol = SimplifyMetadataGenerics(symbol);
            symbol = ReplaceBuiltInTypeNames(symbol);
            symbol = StripNamespaces(symbol);
            symbol = ReplaceBuiltInTypeNames(symbol);
            return symbol;
        }

        static string RemoveBurstLabelSuffix(string symbol)
        {
            var fromIndex = symbol.LastIndexOf(" from ", StringComparison.Ordinal);
            if (fromIndex < 0)
                return StripHashSuffix(symbol);

            var signature = StripHashSuffix(symbol[..fromIndex]);
            var suffix = symbol[(fromIndex + " from ".Length)..];
            var stringLabelIndex = suffix.IndexOf(".string.IL_", StringComparison.Ordinal);
            return stringLabelIndex < 0
                ? signature
                : signature + suffix[stringLabelIndex..];
        }

        static string StripHashSuffix(string text)
        {
            var underscore = text.LastIndexOf('_');
            if (underscore < 0 || text.Length - underscore != 33)
                return text;

            for (var i = underscore + 1; i < text.Length; i++)
                if (!IsHex(text[i]))
                    return text;

            return text[..underscore];
        }

        static bool IsHex(char character) =>
            character is >= '0' and <= '9'
            || character is >= 'a' and <= 'f'
            || character is >= 'A' and <= 'F';

        static string LimitGuidIds(string line) =>
            guidId.Replace(line, match => match.Value[..8]);

        static string SimplifyMetadataGenerics(string symbol)
        {
            var builder = new StringBuilder(symbol.Length);
            for (var i = 0; i < symbol.Length; i++)
            {
                if (symbol[i] != '`' || !TryReadGenericArity(symbol, i + 1, out var afterArity))
                {
                    builder.Append(symbol[i]);
                    continue;
                }

                if (TryReadMetadataGenericArguments(symbol, afterArity, out var afterArguments, out var arguments))
                {
                    builder.Append('<');
                    for (var argumentIndex = 0; argumentIndex < arguments.Count; argumentIndex++)
                    {
                        if (argumentIndex > 0)
                            builder.Append(',');

                        builder.Append(SimplifyMetadataGenerics(arguments[argumentIndex]));
                    }

                    builder.Append('>');
                    i = afterArguments - 1;
                    continue;
                }

                i = afterArity - 1;
            }

            return builder.ToString();
        }

        static bool TryReadGenericArity(string symbol, int start, out int end)
        {
            end = start;
            while (end < symbol.Length && char.IsDigit(symbol[end]))
                end++;

            return end > start;
        }

        static bool TryReadMetadataGenericArguments(string symbol, int start, out int end, out List<string> arguments)
        {
            end = start;
            arguments = new();
            if (start + 1 >= symbol.Length || symbol[start] != '[' || symbol[start + 1] != '[')
                return false;

            var index = start + 1;
            while (index < symbol.Length && symbol[index] == '[')
            {
                var argumentStart = ++index;
                var depth = 0;
                while (index < symbol.Length)
                {
                    if (symbol[index] == '[')
                    {
                        depth++;
                    }
                    else if (symbol[index] == ']')
                    {
                        if (depth == 0)
                            break;

                        depth--;
                    }

                    index++;
                }

                if (index >= symbol.Length)
                    return false;

                arguments.Add(symbol[argumentStart..index]);
                index++;
                if (index < symbol.Length && symbol[index] == ',')
                {
                    index++;
                    continue;
                }

                if (index < symbol.Length && symbol[index] == ']')
                {
                    end = index + 1;
                    return true;
                }

                return false;
            }

            return false;
        }

        static string ReplaceBuiltInTypeNames(string symbol) =>
            builtInTypeName.Replace(symbol, match => BuiltInAlias(match.Value));

        static string BuiltInAlias(string typeName)
        {
            if (typeName.StartsWith("System.", StringComparison.Ordinal))
                typeName = typeName["System.".Length..];

            return typeName switch
            {
                "Void"    => "void",
                "Boolean" => "bool",
                "Byte"    => "byte",
                "SByte"   => "sbyte",
                "Char"    => "char",
                "Decimal" => "decimal",
                "Double"  => "double",
                "Single"  => "float",
                "Int32"   => "int",
                "UInt32"  => "uint",
                "Int64"   => "long",
                "UInt64"  => "ulong",
                "Int16"   => "short",
                "UInt16"  => "ushort",
                "Object"  => "object",
                "String"  => "string",
                "IntPtr"  => "nint",
                "UIntPtr" => "nuint",
                _         => typeName,
            };
        }

        static string StripNamespaces(string symbol)
        {
            var names = new List<string>();
            foreach (Match match in qualifiedTypeName.Matches(symbol))
                names.Add(match.Value);

            if (names.Count == 0)
                return symbol;

            var commonPrefix = CommonNamespacePrefix(names);
            return qualifiedTypeName.Replace(symbol, match =>
            {
                var name = match.Value;
                return commonPrefix.Length > 0 && name.StartsWith(commonPrefix, StringComparison.Ordinal)
                    ? name[commonPrefix.Length..]
                    : ShortTypeName(name);
            });
        }

        static string CommonNamespacePrefix(IReadOnlyList<string> typeNames)
        {
            if (typeNames.Count < 2)
                return string.Empty;

            string[]? common = null;
            foreach (var typeName in typeNames)
            {
                var segments = NamespaceSegments(typeName);
                if (segments.Length == 0)
                    continue;

                if (common == null)
                {
                    common = segments;
                    continue;
                }

                var shared = 0;
                var length = Math.Min(common.Length, segments.Length);
                while (shared < length && common[shared] == segments[shared])
                    shared++;

                if (shared == 0)
                    return string.Empty;

                if (shared == common.Length)
                    continue;

                var reduced = new string[shared];
                Array.Copy(common, reduced, shared);
                common = reduced;
            }

            return common is { Length: > 0 }
                ? string.Join(".", common) + "."
                : string.Empty;
        }

        static string[] NamespaceSegments(string typeName)
        {
            var dot = typeName.LastIndexOf('.');
            return dot < 0 ? Array.Empty<string>() : typeName[..dot].Split('.');
        }

        static string ShortTypeName(string typeName)
        {
            var nestedIndex = typeName.IndexOf('+');
            var searchEnd = nestedIndex < 0 ? typeName.Length - 1 : nestedIndex - 1;
            var dot = typeName.LastIndexOf('.', searchEnd);
            return dot < 0 ? typeName : typeName[(dot + 1)..];
        }

        static string Join(string[] lines, int endExclusive)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < endExclusive; i++)
            {
                if (i > 0)
                    builder.Append('\n');

                builder.Append(lines[i]);
            }

            return builder.ToString().TrimEnd('\n');
        }

        static bool IsBurstError(string disassembly) =>
            disassembly.StartsWith("Failed to compile:", StringComparison.Ordinal)
            || burstError.IsMatch(disassembly);

        static void SetBool(object target, string name, bool value)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property?.CanWrite == true)
                property.SetValue(target, value);
        }

        static BridgeCommandResult Success(string returnValue) =>
            new()
            {
                outcome = ToolOutcome.Success,
                return_value = returnValue,
            };

        static BridgeCommandResult Error(string diagnostic) =>
            new()
            {
                outcome = ToolOutcome.Exception,
                diagnostic = diagnostic,
            };

        static BridgeCommandResult Ambiguous(string query, IReadOnlyList<BurstTarget> targets, int[] indexes) =>
            new()
            {
                outcome = ToolOutcome.AmbiguousTarget,
                diagnostic = Candidates(
                    $"Multiple Burst compile targets match '{query?.Trim() ?? string.Empty}'. Rerun with a more specific target name.",
                    targets,
                    indexes
                ),
            };

        static BridgeCommandResult NoMatch(string query, IReadOnlyList<BurstTarget> targets, int[] indexes) =>
            Error(Candidates($"No Burst compile target matched '{query?.Trim() ?? string.Empty}'.", targets, indexes));

        static string Candidates(string header, IReadOnlyList<BurstTarget> targets, int[] indexes)
        {
            var builder = new StringBuilder(header);
            if (indexes.Length == 0)
                return builder.ToString();

            builder.AppendLine();
            builder.AppendLine("Candidates:");
            foreach (var index in indexes)
                builder.AppendLine($"- {targets[index].DisplayName}");

            return builder.ToString().TrimEnd();
        }

        static Exception Unwrap(Exception exception) =>
            exception is TargetInvocationException { InnerException: { } inner }
                ? inner
                : exception;
    }

    readonly struct BurstTarget
    {
        public readonly string DisplayName;
        public readonly string MethodName;
        public readonly string DeclaringTypeName;
        public readonly string JobTypeName;
        public readonly MethodInfo? Method;
        public readonly Type? JobType;
        public readonly object? Options;
        public readonly bool IsStaticMethod;

        public BurstTarget(
            string displayName,
            string methodName,
            string declaringTypeName,
            string jobTypeName,
            MethodInfo? method = null,
            Type? jobType = null,
            object? options = null,
            bool isStaticMethod = false
        )
        {
            DisplayName = displayName ?? string.Empty;
            MethodName = methodName ?? string.Empty;
            DeclaringTypeName = declaringTypeName ?? string.Empty;
            JobTypeName = jobTypeName ?? string.Empty;
            Method = method;
            JobType = jobType;
            Options = options;
            IsStaticMethod = isStaticMethod;
        }
    }

    enum BurstAsmTargetMatchKind : byte
    {
        None,
        Matched,
        Ambiguous,
    }

    readonly struct BurstAsmTargetMatch
    {
        public readonly BurstAsmTargetMatchKind Kind;
        public readonly int SelectedIndex;
        public readonly int[] CandidateIndexes;

        BurstAsmTargetMatch(BurstAsmTargetMatchKind kind, int selectedIndex, int[] candidateIndexes)
        {
            Kind = kind;
            SelectedIndex = selectedIndex;
            CandidateIndexes = candidateIndexes;
        }

        public static BurstAsmTargetMatch Matched(int index) =>
            new(BurstAsmTargetMatchKind.Matched, index, Array.Empty<int>());

        public static BurstAsmTargetMatch Ambiguous(IReadOnlyList<int> indexes) =>
            new(BurstAsmTargetMatchKind.Ambiguous, -1, Copy(indexes));

        public static BurstAsmTargetMatch None(IReadOnlyList<int> indexes) =>
            new(BurstAsmTargetMatchKind.None, -1, Copy(indexes));

        static int[] Copy(IReadOnlyList<int> indexes)
        {
            var copy = new int[Math.Min(indexes.Count, MaxCandidates)];
            for (var i = 0; i < copy.Length; i++)
                copy[i] = indexes[i];

            return copy;
        }

        const int MaxCandidates = 10;
    }

    readonly struct ScoredTarget
    {
        public readonly int Index;
        public readonly int Score;

        public ScoredTarget(int index, int score)
        {
            Index = index;
            Score = score;
        }
    }
}
