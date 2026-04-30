#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Conduit
{
    static class view_burst_asm
    {
        const int MaxCandidates = 10;
        const int ClearMatchGap = 25;
        const int MaxTopInstructions = 20;
        const int LargeOutputLineThreshold = 1000;
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
            DirtyBurstAssemblyCache();
            var rawDisassembly = GetInspectorDisassembly(target.Method!, BuildOptions(target));
            if (IsBurstError(rawDisassembly))
                return Error(rawDisassembly.Trim());

            if (string.IsNullOrWhiteSpace(rawDisassembly))
                return Error(BuildEmptyDisassemblyDiagnostic(target));

            var disassembly = StripTrailingTemporaryLabelBlocks(CleanDisassembly(RenderEnhancedDisassembly(rawDisassembly).TrimStart('\n')));

            if (string.IsNullOrWhiteSpace(disassembly))
                return Error(BuildEmptyDisassemblyDiagnostic(target));

            return CompleteOutput(target, disassembly);
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

        static string GetInspectorDisassembly(MethodInfo method, string options)
        {
            try
            {
                var result = (string?)Type.GetType("Unity.Burst.LowLevel.BurstCompilerService, UnityEngine.CoreModule", true)!
                    .GetMethod("GetDisassembly", BindingFlags.Static | BindingFlags.Public)!
                    .Invoke(null, new object[] { method, options })
                    ?? string.Empty;

                if (result.IndexOf('\t') >= 0)
                    result = result.Replace("\t", "        ");

                if (!result.Contains("Burst timings"))
                    return result;

                var index = result.IndexOf("While compiling", StringComparison.Ordinal);
                return index > 0 ? result[index..] : result;
            }
            catch (Exception exception)
            {
                return "Failed to compile:\n" + Unwrap(exception);
            }
        }

        static void DirtyBurstAssemblyCache()
        {
            try
            {
                var optionsType = Type.GetType("Unity.Burst.BurstCompilerOptions, Unity.Burst", false);
                var command = (string?)optionsType
                    ?.GetField("CompilerCommandDirtyAllAssemblies", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(null);
                if (string.IsNullOrWhiteSpace(command))
                    return;

                Type.GetType("Unity.Burst.BurstCompiler, Unity.Burst", false)
                    ?.GetMethod("SendCommandToCompiler", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.Invoke(null, new object?[] { command, null });
            }
            catch (Exception)
            {
                // Older Burst versions may not expose this command; the Inspector disassembly call still works without it.
            }
        }

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

        internal static string BuildOutput(BurstTarget target, string disassembly)
        {
            var displayName = CleanDisplayName(target.DisplayName);
            var stats = AnalyzeAssembly(target, disassembly);
            return $"{displayName}\n{FormatStats(stats)}\n\n{disassembly}";
        }

        internal static BridgeCommandResult CompleteOutput(BurstTarget target, string disassembly)
        {
            var output = BuildOutput(target, disassembly);
            if (CountLines(output) <= LargeOutputLineThreshold)
                return Success(output);

            var path = SaveLargeOutput(target, output);
            var kilobytes = Math.Max(1, (Encoding.UTF8.GetByteCount(output) + 1023) / 1024);
            return Success($"{CleanDisplayName(target.DisplayName)}\n{FormatStats(AnalyzeAssembly(target, disassembly))}\n\nAssembly output very large ({kilobytes} KB); saved to {path}");
        }

        static BurstAsmStats AnalyzeAssembly(BurstTarget target, string disassembly)
        {
            var lines = disassembly.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var blocks = GetFunctionBlocks(lines);
            if (blocks.Count == 0)
                return AnalyzeLines(lines, 0, lines.Length);

            var selected = SelectMainBlock(target, blocks);
            return AnalyzeLines(lines, selected.Start, selected.End);
        }

        static List<BurstAsmFunctionBlock> GetFunctionBlocks(string[] lines)
        {
            var blocks = new List<BurstAsmFunctionBlock>();
            var currentLabel = string.Empty;
            var start = -1;
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (IsSectionBoundary(line))
                {
                    Flush(index);
                    continue;
                }

                if (!IsFunctionLabel(line, out var label))
                    continue;

                Flush(index);
                currentLabel = label;
                start = index + 1;
            }

            Flush(lines.Length);
            return blocks;

            void Flush(int end)
            {
                if (start < 0)
                    return;

                var instructionCount = CountInstructions(lines, start, end);
                if (instructionCount > 0)
                    blocks.Add(new(currentLabel, start, end, instructionCount));

                currentLabel = string.Empty;
                start = -1;
            }
        }

        static bool IsSectionBoundary(string line) =>
            line.StartsWith(".section", StringComparison.Ordinal)
            || line.StartsWith(".text", StringComparison.Ordinal);

        static bool IsFunctionLabel(string line, out string label)
        {
            label = string.Empty;
            if (!line.EndsWith(":", StringComparison.Ordinal))
                return false;

            label = line[..^1].Trim();
            if (label.Length == 0 || label.StartsWith(".L", StringComparison.Ordinal))
                return false;

            return !label.StartsWith(".seh", StringComparison.Ordinal)
                   && !label.StartsWith(".cv", StringComparison.Ordinal);
        }

        static BurstAsmFunctionBlock SelectMainBlock(BurstTarget target, IReadOnlyList<BurstAsmFunctionBlock> blocks)
        {
            var best = blocks[0];
            var bestScore = int.MinValue;
            foreach (var block in blocks)
            {
                var score = ScoreFunctionBlock(target, block);
                if (score <= bestScore)
                    continue;

                best = block;
                bestScore = score;
            }

            return best;
        }

        static int ScoreFunctionBlock(BurstTarget target, BurstAsmFunctionBlock block)
        {
            var label = NormalizeAsmText(block.Label);
            if (label.Length == 0)
                return int.MinValue;

            var score = block.InstructionCount;
            if (IsExcludedFunctionLabel(label))
                score -= 1000;

            if (IsHexLabel(label))
                score -= 50;

            var displayName = NormalizeAsmText(CleanDisplayName(target.DisplayName));
            if (displayName.Length > 0 && (label.Contains(displayName) || displayName.Contains(label)))
                score += 300;

            var jobType = target.JobTypeName.Length == 0 ? string.Empty : NormalizeAsmText(ShortTypeName(target.JobTypeName));
            if (jobType.Length > 0 && label.Contains(jobType))
                score += 180;

            var declaringType = target.DeclaringTypeName.Length == 0 ? string.Empty : NormalizeAsmText(ShortTypeName(target.DeclaringTypeName));
            if (declaringType.Length > 0 && label.Contains(declaringType))
                score += 140;

            var method = NormalizeAsmText(target.MethodName);
            if (method.Length > 0 && label.Contains(method))
                score += string.Equals(method, "execute", StringComparison.Ordinal) ? 20 : 100;

            if (label.Contains("jobstruct"))
                score += 20;

            return score;
        }

        static bool IsExcludedFunctionLabel(string label) =>
            label.StartsWith("burstinitialize", StringComparison.Ordinal)
            || label == "feat00";

        static bool IsHexLabel(string label)
        {
            if (label.Length is < 8 or > 32)
                return false;

            foreach (var character in label)
                if (!IsHex(character))
                    return false;

            return true;
        }

        static string NormalizeAsmText(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
                if (char.IsLetterOrDigit(character))
                    builder.Append(char.ToLowerInvariant(character));

            return builder.ToString();
        }

        static int CountInstructions(string[] lines, int start, int end)
        {
            var count = 0;
            for (var index = start; index < end; index++)
                if (TryParseInstruction(lines[index], out _, out _))
                    count++;

            return count;
        }

        static BurstAsmStats AnalyzeLines(string[] lines, int start, int end)
        {
            var stats = new BurstAsmStats();
            for (var index = start; index < end; index++)
            {
                if (!TryParseInstruction(lines[index], out var mnemonic, out var operands))
                    continue;

                stats.InstructionCount++;
                Increment(stats.InstructionCounts, mnemonic);

                var lowerOperands = operands.ToLowerInvariant();
                var hasXmm = ContainsRegisterPrefix(lowerOperands, "xmm");
                var hasYmm = ContainsRegisterPrefix(lowerOperands, "ymm");
                var hasZmm = ContainsRegisterPrefix(lowerOperands, "zmm");
                var hasNeon = ContainsRegisterPrefix(lowerOperands, "v") || ContainsRegisterPrefix(lowerOperands, "q");
                var hasSve = ContainsRegisterPrefix(lowerOperands, "z") || ContainsRegisterPrefix(lowerOperands, "p");

                if (hasXmm)
                    stats.XmmInstructionCount++;
                if (hasYmm)
                    stats.YmmInstructionCount++;
                if (hasZmm)
                    stats.ZmmInstructionCount++;
                if (hasNeon)
                    stats.NeonInstructionCount++;
                if (hasSve)
                    stats.SveInstructionCount++;

                if (IsVectorInstruction(mnemonic, hasXmm, hasYmm, hasZmm, hasNeon, hasSve))
                    stats.VectorInstructionCount++;

                if (IsConditionalBranch(mnemonic))
                    stats.ConditionalBranchCount++;
                else if (IsUnconditionalBranch(mnemonic))
                    stats.UnconditionalBranchCount++;
                else if (IsCall(mnemonic))
                    stats.CallCount++;
                else if (IsReturn(mnemonic))
                    stats.ReturnCount++;

                if (!HasMemoryOperand(mnemonic, lowerOperands))
                    continue;

                stats.MemoryOperandInstructionCount++;
                if (HasStackOrFrameOperand(lowerOperands))
                    stats.StackFrameOperandInstructionCount++;
            }

            return stats;
        }

        static bool TryParseInstruction(string line, out string mnemonic, out string operands)
        {
            mnemonic = string.Empty;
            operands = string.Empty;
            var text = line.Trim();
            if (text.Length == 0
                || text[0] is '#' or ';'
                || text.StartsWith("//", StringComparison.Ordinal)
                || text[0] == '.'
                || IsFunctionLabel(text, out _))
                return false;

            var firstEnd = ReadTokenEnd(text, 0);
            if (firstEnd == 0)
                return false;

            var first = text[..firstEnd].ToLowerInvariant();
            var operandStart = SkipWhitespace(text, firstEnd);
            if (first is "lock" or "rep" or "repe" or "repne")
            {
                var secondEnd = ReadTokenEnd(text, operandStart);
                if (secondEnd <= operandStart)
                    return false;

                mnemonic = $"{first} {text[operandStart..secondEnd].ToLowerInvariant()}";
                operands = secondEnd < text.Length ? text[secondEnd..].Trim() : string.Empty;
                return true;
            }

            mnemonic = first;
            operands = operandStart < text.Length ? text[operandStart..].Trim() : string.Empty;
            return true;
        }

        static int ReadTokenEnd(string text, int start)
        {
            var index = start;
            while (index < text.Length)
            {
                var character = text[index];
                if (!char.IsLetterOrDigit(character) && character is not '_' and not '.')
                    break;

                index++;
            }

            return index;
        }

        static int SkipWhitespace(string text, int start)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;

            return start;
        }

        static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        static bool IsVectorInstruction(string mnemonic, bool hasXmm, bool hasYmm, bool hasZmm, bool hasNeon, bool hasSve)
        {
            if (hasYmm || hasZmm || hasNeon || hasSve)
                return true;

            if (hasXmm && !IsScalarSimdMnemonic(mnemonic))
                return true;

            return IsPackedVectorMnemonic(mnemonic);
        }

        static bool IsScalarSimdMnemonic(string mnemonic) =>
            mnemonic.EndsWith("ss", StringComparison.Ordinal)
            || mnemonic.EndsWith("sd", StringComparison.Ordinal);

        static bool IsPackedVectorMnemonic(string mnemonic) =>
            mnemonic.StartsWith("v", StringComparison.Ordinal) && !IsScalarSimdMnemonic(mnemonic)
            || mnemonic.StartsWith("padd", StringComparison.Ordinal)
            || mnemonic.StartsWith("psub", StringComparison.Ordinal)
            || mnemonic.StartsWith("pmul", StringComparison.Ordinal)
            || mnemonic.StartsWith("pand", StringComparison.Ordinal)
            || mnemonic.StartsWith("por", StringComparison.Ordinal)
            || mnemonic.StartsWith("pxor", StringComparison.Ordinal)
            || mnemonic.EndsWith("ps", StringComparison.Ordinal)
            || mnemonic.EndsWith("pd", StringComparison.Ordinal);

        static bool IsConditionalBranch(string mnemonic)
        {
            if (mnemonic is "cbz" or "cbnz" or "tbz" or "tbnz")
                return true;

            if (mnemonic.StartsWith("loop", StringComparison.Ordinal))
                return true;

            if (mnemonic.StartsWith("b.", StringComparison.Ordinal))
                return true;

            return mnemonic.StartsWith("j", StringComparison.Ordinal) && mnemonic != "jmp";
        }

        static bool IsUnconditionalBranch(string mnemonic) =>
            mnemonic is "jmp" or "b" or "br";

        static bool IsCall(string mnemonic) =>
            mnemonic is "call" or "bl" or "blr";

        static bool IsReturn(string mnemonic) =>
            mnemonic.StartsWith("ret", StringComparison.Ordinal);

        static bool HasMemoryOperand(string mnemonic, string lowerOperands) =>
            lowerOperands.IndexOf('[', StringComparison.Ordinal) >= 0 && lowerOperands.IndexOf(']', StringComparison.Ordinal) >= 0
            || mnemonic is "ldr" or "ldp" or "ld1" or "str" or "stp" or "st1";

        static bool HasStackOrFrameOperand(string lowerOperands) =>
            ContainsRegister(lowerOperands, "rsp")
            || ContainsRegister(lowerOperands, "rbp")
            || ContainsRegister(lowerOperands, "esp")
            || ContainsRegister(lowerOperands, "ebp")
            || ContainsRegister(lowerOperands, "sp")
            || ContainsRegister(lowerOperands, "fp")
            || ContainsRegister(lowerOperands, "x29");

        static bool ContainsRegisterPrefix(string text, string prefix)
        {
            foreach (var token in RegisterTokens(text))
                if (token.StartsWith(prefix, StringComparison.Ordinal))
                    if (token.Length > prefix.Length && char.IsDigit(token[prefix.Length]))
                        return true;

            return false;
        }

        static bool ContainsRegister(string text, string register)
        {
            foreach (var token in RegisterTokens(text))
                if (token == register)
                    return true;

            return false;
        }

        static IEnumerable<string> RegisterTokens(string text)
        {
            var start = -1;
            for (var index = 0; index <= text.Length; index++)
            {
                if (index < text.Length && char.IsLetterOrDigit(text[index]))
                {
                    if (start < 0)
                        start = index;

                    continue;
                }

                if (start < 0)
                    continue;

                yield return text[start..index];
                start = -1;
            }
        }

        static string FormatStats(BurstAsmStats stats)
        {
            var branches = stats.ConditionalBranchCount + stats.UnconditionalBranchCount;
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append($"- Instructions: {stats.InstructionCount}\n");
            builder.Append($"- Vector instructions: {stats.VectorInstructionCount} ({Percent(stats.VectorInstructionCount, stats.InstructionCount)})\n");
            builder.Append($"- Control flow: branches={branches}, conditional={stats.ConditionalBranchCount}, unconditional={stats.UnconditionalBranchCount}, calls={stats.CallCount}, returns={stats.ReturnCount}\n");
            builder.Append($"- Memory operands: {stats.MemoryOperandInstructionCount} ({Percent(stats.MemoryOperandInstructionCount, stats.InstructionCount)}); stack/frame operands: {stats.StackFrameOperandInstructionCount}\n");
            builder.Append($"- Vector width hints: xmm={stats.XmmInstructionCount}, ymm={stats.YmmInstructionCount}, zmm={stats.ZmmInstructionCount}, neon/simd={stats.NeonInstructionCount}, sve={stats.SveInstructionCount}\n");
            builder.Append("- Top instructions: ");
            AppendTopInstructions(builder, stats.InstructionCounts);
            return builder.ToString();
        }

        static string Percent(int value, int total) =>
            total == 0
                ? "0%"
                : (value * 100.0 / total).ToString("0.#", CultureInfo.InvariantCulture) + "%";

        static void AppendTopInstructions(StringBuilder builder, Dictionary<string, int> counts)
        {
            if (counts.Count == 0)
            {
                builder.Append("<none>");
                return;
            }

            var entries = new List<KeyValuePair<string, int>>(counts);
            entries.Sort(static (left, right) =>
            {
                var count = right.Value.CompareTo(left.Value);
                return count != 0
                    ? count
                    : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
            });

            var count = Math.Min(entries.Count, MaxTopInstructions);
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                    builder.Append(", ");

                builder.Append(entries[index].Key);
                builder.Append('=');
                builder.Append(entries[index].Value.ToString(CultureInfo.InvariantCulture));
            }
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

        static string CleanDisplayName(string displayName) =>
            LimitGuidIds(CleanSymbol(displayName.Trim()));

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

                if (TryReadMetadataGenericArguments(symbol, afterArity, out var afterArguments, out var arguments)
                    || TryReadSimpleGenericArguments(symbol, afterArity, out afterArguments, out arguments))
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

        static bool TryReadSimpleGenericArguments(string symbol, int start, out int end, out List<string> arguments)
        {
            end = start;
            arguments = new();
            if (start >= symbol.Length || symbol[start] != '[')
                return false;

            var argumentStart = start + 1;
            var depth = 0;
            for (var i = argumentStart; i < symbol.Length; i++)
            {
                if (symbol[i] == '[')
                {
                    depth++;
                    continue;
                }

                if (symbol[i] == ']')
                {
                    if (depth > 0)
                    {
                        depth--;
                        continue;
                    }

                    arguments.Add(symbol[argumentStart..i]);
                    end = i + 1;
                    return true;
                }

                if (symbol[i] != ',' || depth != 0)
                    continue;

                arguments.Add(symbol[argumentStart..i]);
                argumentStart = i + 1;
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

            if (common is not { Length: > 0 } || IsBroadRootNamespace(common))
                return string.Empty;

            return string.Join(".", common) + ".";
        }

        static bool IsBroadRootNamespace(string[] segments) =>
            segments.Length == 1 && segments[0] is "Unity" or "System" or "Microsoft";

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

        static int CountLines(string text)
        {
            if (text.Length == 0)
                return 0;

            var lines = 1;
            foreach (var character in text)
                if (character == '\n')
                    lines++;

            return lines;
        }

        static string SaveLargeOutput(BurstTarget target, string output)
        {
            var path = Path.Combine("Temp", SafeFileName(OutputFileName(target)) + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, output);
            return path.Replace('\\', '/');
        }

        static string OutputFileName(BurstTarget target)
        {
            var name = CleanDisplayName(target.DisplayName);
            TrimAt(" - ");
            TrimAt("(");
            return name.Length > 0 ? name : target.MethodName;

            void TrimAt(string marker)
            {
                var index = name.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0)
                    name = name[..index].Trim();
            }
        }

        static string SafeFileName(string fileName)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(fileName.Length);
            foreach (var character in fileName)
            {
                if (Array.IndexOf(invalid, character) >= 0)
                    builder.Append('_');
                else if (!char.IsWhiteSpace(character))
                    builder.Append(character);
            }

            return builder.Length == 0 ? "burst_asm" : builder.ToString();
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

        internal static string BuildEmptyDisassemblyDiagnostic(BurstTarget target)
            => $"Failed to compile '{CleanDisplayName(target.DisplayName)}': Burst returned no assembly or diagnostic text.";

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
            Error(NoMatchDiagnostic(query, targets, indexes));

        internal static string NoMatchDiagnostic(string query, IReadOnlyList<BurstTarget> targets, int[] indexes)
        {
            var trimmed = query?.Trim() ?? string.Empty;
            return Candidates(
                trimmed.Length == 0 ? string.Empty : $"No Burst compile target matched '{trimmed}'.",
                targets,
                indexes
            );
        }

        static string Candidates(string header, IReadOnlyList<BurstTarget> targets, int[] indexes)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header))
                builder.AppendLine(header);

            if (indexes.Length == 0)
                return builder.ToString().TrimEnd();

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

    readonly struct BurstAsmFunctionBlock
    {
        public readonly string Label;
        public readonly int Start;
        public readonly int End;
        public readonly int InstructionCount;

        public BurstAsmFunctionBlock(string label, int start, int end, int instructionCount)
        {
            Label = label;
            Start = start;
            End = end;
            InstructionCount = instructionCount;
        }
    }

    sealed class BurstAsmStats
    {
        public readonly Dictionary<string, int> InstructionCounts = new(StringComparer.Ordinal);
        public int InstructionCount;
        public int VectorInstructionCount;
        public int ConditionalBranchCount;
        public int UnconditionalBranchCount;
        public int CallCount;
        public int ReturnCount;
        public int MemoryOperandInstructionCount;
        public int StackFrameOperandInstructionCount;
        public int XmmInstructionCount;
        public int YmmInstructionCount;
        public int ZmmInstructionCount;
        public int NeonInstructionCount;
        public int SveInstructionCount;
    }
}
