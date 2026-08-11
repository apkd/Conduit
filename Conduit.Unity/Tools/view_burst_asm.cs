#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
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
        const int MaxBackwardBranchDetails = 5;
        const int MaxDirectCalleeDetails = 5;
        const int MaxOptimizationRemarks = 10;
        const int MaxForwarderInstructions = 8;
        const int LargeOutputLineThreshold = 1000;
        static readonly Regex tempLabel = new(@"^\s*\.Ltmp\d+:\s*(?:[#;].*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex burstError = new(@"^.*\(\d+,\d+\):\sBurst\serror", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex sourceLocation = new(@"^(?<prefix>\s*[#;]\s+)(?<file>.+?)\((?<line>\d+),\s*\d+\)(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex renderedSourceLocation = new(@"^\s*[#;]\s+(?<file>.+?):(?<line>\d+)(?:\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex remarkSourceLocation = new(@"^(?<file>.+?):(?<line>\d+):\d+:\s*(?<message>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex assemblyQualifier = new(@",\s*[^,\]\)>]+,\s*Version=[0-9.]+,\s*Culture=[^,\]\)>\s]+,\s*PublicKeyToken=(?:null|[0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex fromAssemblyQualifier = new(@"\s+from\s+[^,\]\)>]+,\s*Version=[0-9.]+,\s*Culture=[^,\]\)>\s]+,\s*PublicKeyToken=(?:null|[0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex builtInTypeName = new(@"\b(?:System\.)?(?:Void|Boolean|Byte|SByte|Char|Decimal|Double|Single|Int32|UInt32|Int64|UInt64|Int16|UInt16|Object|String|IntPtr|UIntPtr)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex qualifiedTypeName = new(@"\b(?:[A-Z_][A-Za-z0-9_]*\.)+[A-Z_][A-Za-z0-9_]*(?:\+[A-Z_][A-Za-z0-9_]*)*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex unityMathematicsName = new(@"\bUnity\.Mathematics\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex hashSuffix = new(@"(?<=[A-Za-z0-9_])_[0-9a-fA-F]{32}(?=\b)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex guidId = new(@"(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex burstDiagnostic = new(@"(?m)^(?:.*\(\d+,\d+\):\s*)?Burst\s+(?:warning|error)\s+BC\d+\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex ignoredBurstWarning = new(@"(?m)^(?:.*\(\d+,\d+\):\s*)?Burst\s+warning\s+BC1371\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static BridgeCommandResult ViewBurstAsm(string targetName, string cpu = "x86")
        {
#if MODULE_BURST
            try
            {
                if (!TryParseCpuTarget(cpu, out var cpuTarget))
                    return Error($"Unknown Burst CPU '{cpu}'. Expected x86, wasm32, armv8, or armv9.");

                var targets = LoadTargets();
                if (targets.Count == 0)
                    return Error("No Burst compile targets were found in the current editor domain.");

                var match = MatchTarget(targetName, targets);
                return match.Kind switch
                {
                    BurstAsmTargetMatchKind.Matched   => Compile(targets[match.SelectedIndex], cpuTarget),
                    BurstAsmTargetMatchKind.Ambiguous => Ambiguous(targetName, targets, match),
                    _                                 => NoMatch(targetName, targets, match),
                };
            }
            catch (Exception exception)
            {
                return Error($"Could not inspect Burst assembly: {Unwrap(exception).Message}");
            }
#else
            _ = targetName;
            _ = cpu;
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

        static BridgeCommandResult Compile(BurstTarget target, BurstCpuTarget cpu)
        {
            DirtyBurstAssemblyCache();
            var options = BuildOptions(target, cpu);
            var rawDisassembly = GetInspectorDisassembly(target.Method!, options.Assembly);
            if (IsBurstError(rawDisassembly))
                return Error(rawDisassembly.Trim());

            if (string.IsNullOrWhiteSpace(rawDisassembly))
                return Error(BuildEmptyDisassemblyDiagnostic(target));

            var disassembly = StripTrailingTemporaryLabelBlocks(
                CleanDisassembly(RenderEnhancedDisassembly(rawDisassembly, cpu.AsmKind).TrimStart('\n'))
            );

            if (string.IsNullOrWhiteSpace(disassembly))
                return Error(BuildEmptyDisassemblyDiagnostic(target));

            var rawRemarks = GetInspectorDisassembly(target.Method!, options.OptimizationRemarks);
            // remarks use a separate Burst dump and may be unsupported even when assembly compilation succeeds.
            var remarksError = IsBurstError(rawRemarks)
                ? FirstLine(SimplifyBurstDiagnostic(rawRemarks))
                : string.Empty;
            var stats = AnalyzeAssembly(target, disassembly);
            stats.Context = options.Context;
            stats.RemarksError = remarksError;
            if (remarksError.Length == 0)
                ParseOptimizationRemarks(rawRemarks, stats.OptimizationRemarks);

            return CompleteOutput(target, disassembly, stats);
        }

        static (string Assembly, string OptimizationRemarks, BurstCompilationContext Context) BuildOptions(
            BurstTarget target,
            BurstCpuTarget cpu)
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

            var defaultOptions = (string?)args[1] ?? string.Empty;
            var assembly = BuildInspectorOptions(defaultOptions, cpu.CompilerTarget);
            return new(
                assembly,
                BuildInspectorOptions(defaultOptions, cpu.CompilerTarget, "1", "IRPassAnalysis"),
                ParseCompilationContext(assembly, cpu.Name)
            );
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
                // older Burst versions may not expose this command; the Inspector disassembly call still works without it.
            }
        }

        static string RenderEnhancedDisassembly(string disassembly, string asmKindName)
        {
            var disassemblerType = Type.GetType("Unity.Burst.Editor.BurstDisassembler, Unity.Burst.Editor", true)!;
            var asmKindType = Type.GetType("Unity.Burst.Editor.BurstDisassembler+AsmKind, Unity.Burst.Editor", true)!;
            var asmKind = Enum.Parse(asmKindType, asmKindName);
            var disassembler = Activator.CreateInstance(disassemblerType)!;

            disassemblerType
                .GetMethod("Initialize", new[] { typeof(string), asmKindType, typeof(bool), typeof(bool), typeof(bool) })!
                .Invoke(disassembler, new[] { disassembly, asmKind, true, false, false });

            return (string?)disassemblerType.GetMethod("RenderFullText")!.Invoke(disassembler, null)
                   ?? disassembly;
        }
#endif

        internal static void ApplyInspectorOptionOverrides(object options)
        {
            SetBool(options, "EnableBurstSafetyChecks", false);
            SetBool(options, "ForceEnableBurstSafetyChecks", false);
            SetBool(options, "EnableBurstDebug", false);
        }

        internal static bool TryParseCpuTarget(string? cpu, out BurstCpuTarget target)
        {
            // explicit targets keep reports reproducible when the same project is inspected from different editor hosts.
            switch (cpu?.Trim().ToLowerInvariant())
            {
                case null or "" or "x86":
                    target = new("x86", "AVX2", "Intel");
                    return true;
                case "wasm32":
                    target = new("wasm32", "WASM32", "Wasm");
                    return true;
                case "armv8":
                    target = new("armv8", "ARMV8A_AARCH64_HALFFP", "ARM");
                    return true;
                case "armv9":
                    target = new("armv9", "ARMV9A", "ARM");
                    return true;
                default:
                    target = default;
                    return false;
            }
        }

        internal static string BuildInspectorOptions(string defaultOptions, string compilerTarget = "AVX2")
            => BuildInspectorOptions(defaultOptions, compilerTarget, "2", "Asm");

        static string BuildInspectorOptions(string defaultOptions, string compilerTarget, string debugLevel, string dump)
        {
            var builder = new StringBuilder(defaultOptions.Trim());
            Append("--disable-warnings=BC1370;BC1322");
            Append($"--target={compilerTarget}");
            Append($"--debug={debugLevel}");
            Append($"--dump={dump}");
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
                return BurstAsmTargetMatch.None(FirstIndexes(targets), targets.Count);

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
                return BurstAsmTargetMatch.None(FirstIndexes(targets), targets.Count);

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
                if (candidate.Score < minimumScore)
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

        internal static bool ShouldSuppressBurstDiagnostic(string message) =>
            !string.IsNullOrEmpty(message) && ignoredBurstWarning.IsMatch(message);

        internal static bool IsBurstDiagnostic(string message) =>
            !string.IsNullOrEmpty(message)
            && (burstDiagnostic.IsMatch(message)
                || message.Contains("InvalidOperationException: Burst failed to compile", StringComparison.Ordinal)
                || message.Contains("BuildFailedException: Burst compiler failed running", StringComparison.Ordinal)
                || message.Contains("Unexpected exception Burst.Compiler.", StringComparison.Ordinal)
                || message.Contains("Burst.Compiler.", StringComparison.Ordinal) && message.Contains("Exception:", StringComparison.Ordinal));

        internal static string SimplifyBurstDiagnostic(string diagnostic)
        {
            var lines = diagnostic.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
                lines[i] = CleanDiagnosticLine(lines[i]);

            return Join(lines, lines.Length);
        }

        internal static string BuildOutput(BurstTarget target, string disassembly)
            => BuildOutput(target, disassembly, AnalyzeAssembly(target, disassembly));

        internal static string BuildOutput(
            BurstTarget target,
            string disassembly,
            BurstCompilationContext context,
            string optimizationRemarks)
        {
            var stats = AnalyzeAssembly(target, disassembly);
            stats.Context = context;
            ParseOptimizationRemarks(optimizationRemarks, stats.OptimizationRemarks);
            return BuildOutput(target, disassembly, stats);
        }

        internal static BridgeCommandResult CompleteOutput(BurstTarget target, string disassembly)
            => CompleteOutput(target, disassembly, AnalyzeAssembly(target, disassembly));

        static string BuildOutput(BurstTarget target, string disassembly, BurstAsmStats stats) =>
            $"**Assembly:** `{CleanDisplayName(target.DisplayName)}`\n\n{FormatStats(stats)}\n\n```asm\n{disassembly}\n```";

        static BridgeCommandResult CompleteOutput(BurstTarget target, string disassembly, BurstAsmStats stats)
        {
            var output = BuildOutput(target, disassembly, stats);
            if (CountLines(output) <= LargeOutputLineThreshold)
                return Success(output);

            var path = SaveLargeOutput(target, output);
            var kilobytes = Math.Max(1, (Encoding.UTF8.GetByteCount(output) + 1023) / 1024);
            return Success($"**Assembly:** `{CleanDisplayName(target.DisplayName)}`\n\n{FormatStats(stats)}\n\n*Assembly output very large ({kilobytes} KB); saved to `{path}`.*");
        }

        static BurstAsmStats AnalyzeAssembly(BurstTarget target, string disassembly)
        {
            var lines = disassembly.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var blocks = GetFunctionBlocks(lines);
            if (blocks.Count == 0)
                return AnalyzeLines(lines, 0, lines.Length);

            var selected = SelectMainBlock(target, blocks);
            var forwarders = new List<string>();
            // burst sometimes exposes the managed entry as a tiny thunk; follow only direct, scaffolding-only
            // forwarders; names such as $Invoke are useful evidence but do not establish which block holds the body.
            while (TryGetForwardedBlock(lines, selected, blocks, out var forwarded))
            {
                var selectedName = selected.Label.Trim('"');
                var forwardedName = forwarded.Label.Trim('"');
                if (forwardedName == selectedName || forwarders.Contains(forwardedName))
                    break;

                forwarders.Add(selectedName);
                selected = forwarded;
            }

            var stats = AnalyzeLines(lines, selected.Start, selected.End);
            stats.AnalyzedFunction = selected.Label.Trim('"');
            stats.EntryForwarders.AddRange(forwarders);
            return stats;
        }

        static bool TryGetForwardedBlock(
            string[] lines,
            BurstAsmFunctionBlock block,
            IReadOnlyList<BurstAsmFunctionBlock> blocks,
            out BurstAsmFunctionBlock forwarded)
        {
            forwarded = default;
            if (block.InstructionCount > MaxForwarderInstructions)
                return false;

            var target = string.Empty;
            for (int index = block.Start; index < block.End; ++index)
            {
                if (!TryParseInstruction(lines[index], out var mnemonic, out var operands))
                    continue;

                var parsedOperands = SplitOperands(operands);
                if (IsCall(mnemonic) && IsDirectCall(mnemonic, parsedOperands))
                {
                    if (!SetTarget(parsedOperands[0]))
                        return false;
                    continue;
                }

                if (IsUnconditionalBranch(mnemonic)
                    && TryGetDirectBranchTarget(parsedOperands, out var branchTarget))
                {
                    if (!SetTarget(branchTarget))
                        return false;
                    continue;
                }

                if (!IsScaffolding(BaseMnemonic(mnemonic), parsedOperands))
                    return false;
            }

            if (target.Length == 0)
                return false;

            foreach (var candidate in blocks)
            {
                if (!string.Equals(CleanTransferTarget(candidate.Label), target, StringComparison.Ordinal))
                    continue;

                forwarded = candidate;
                return true;
            }

            return false;

            bool SetTarget(string value)
            {
                value = CleanTransferTarget(value);
                if (value.Length == 0 || target.Length > 0 && target != value)
                    return false;

                target = value;
                return true;
            }

            static bool IsScaffolding(string mnemonic, IReadOnlyList<string> operands)
            {
                if (mnemonic is "ret" or "push" or "pop" or "nop" or "endbr64" or "vzeroupper")
                    return true;
                if (mnemonic == "mov" && operands.Count == 2)
                    return IsRegisterOperand(operands[0]) && IsRegisterOperand(operands[1]);
                if ((mnemonic is "add" or "sub") && operands.Count == 2)
                    return operands[0].Trim().TrimStart('%') is "rsp" or "esp" or "sp"
                           && IsNumericImmediate(operands[1]);

                return false;
            }
        }

        static List<BurstAsmFunctionBlock> GetFunctionBlocks(string[] lines)
        {
            var blocks = new List<BurstAsmFunctionBlock>();
            var currentLabel = string.Empty;
            var start = -1;
            for (int index = 0, n = lines.Length; index < n; ++index)
            {
                var line = lines[index].Trim();
                if (line.StartsWith(".section", StringComparison.Ordinal)
                    || line.StartsWith(".text", StringComparison.Ordinal))
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

                int instructionCount = 0;
                for (int index = start; index < end; ++index)
                    if (TryParseInstruction(lines[index], out _, out _))
                        instructionCount++;

                if (instructionCount > 0)
                    blocks.Add(new(currentLabel, start, end, instructionCount));

                currentLabel = string.Empty;
                start = -1;
            }
        }

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
            var displayName = NormalizeAsmText(CleanDisplayName(target.DisplayName));
            var jobType = target.JobTypeName.Length == 0
                ? string.Empty
                : NormalizeAsmText(ShortTypeName(target.JobTypeName));
            var declaringType = target.DeclaringTypeName.Length == 0
                ? string.Empty
                : NormalizeAsmText(ShortTypeName(target.DeclaringTypeName));
            var method = NormalizeAsmText(target.MethodName);
            var best = blocks[0];
            var bestScore = int.MinValue;
            foreach (var block in blocks)
            {
                var score = Score(block);
                if (score <= bestScore)
                    continue;

                best = block;
                bestScore = score;
            }

            return best;

            int Score(BurstAsmFunctionBlock block)
            {
                var label = NormalizeAsmText(block.Label);
                if (label.Length == 0)
                    return int.MinValue;

                // semantic matches dominate; body size only breaks ties between similarly named blocks.
                var score = Math.Min(block.InstructionCount, 1000);
                if (label.StartsWith("burstinitialize", StringComparison.Ordinal) || label == "feat00")
                    score -= 100000;
                if (IsHexLabel(label))
                    score -= 10000;
                if (displayName.Length > 0 && (label.Contains(displayName) || displayName.Contains(label)))
                    score += 20000;
                if (jobType.Length > 0 && label.Contains(jobType))
                    score += 8000;
                if (declaringType.Length > 0 && label.Contains(declaringType))
                    score += 4000;
                if (method.Length > 0)
                {
                    if (HasExactMethodName(block.Label))
                        score += 12000;
                    else if (label.Contains(method))
                        score += method == "execute" ? 500 : 2000;
                }
                if (label.Contains("jobstruct"))
                    score += 500;

                return score;
            }

            bool HasExactMethodName(string label)
            {
                var signatureStart = label.IndexOf('(');
                var name = (signatureStart < 0 ? label : label[..signatureStart]).Trim().Trim('"');
                var separator = Math.Max(name.LastIndexOf('.'), name.LastIndexOf(':'));
                if (separator >= 0)
                    name = name[(separator + 1)..];

                return NormalizeAsmText(name) == method;
            }

            static bool IsHexLabel(string label)
            {
                if (label.Length is < 8 or > 32)
                    return false;

                foreach (var character in label)
                    if (!IsHex(character))
                        return false;

                return true;
            }
        }

        static string NormalizeAsmText(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
                if (char.IsLetterOrDigit(character))
                    builder.Append(char.ToLowerInvariant(character));

            return builder.ToString();
        }

        static BurstAsmStats AnalyzeLines(string[] lines, int start, int end)
        {
            var stats = new BurstAsmStats();
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            var branches = new List<BurstAsmBranch>();
            var source = string.Empty;
            for (int index = start; index < end; ++index)
            {
                var line = lines[index];
                if (TryParseRenderedSourceLocation(line, out var nextSource))
                {
                    source = nextSource;
                    continue;
                }

                if (IsUnknownSourceLocation(line))
                {
                    source = string.Empty;
                    continue;
                }

                if (TryParseCodeLabel(line, out var label))
                {
                    labels[label] = stats.InstructionCount;
                    continue;
                }

                if (!TryParseInstruction(line, out var mnemonic, out var operands))
                    continue;

                var parsedOperands = SplitOperands(operands);
                var instructionIndex = stats.InstructionCount;
                stats.InstructionCount++;
                AnalyzeInstruction(stats, mnemonic, operands, parsedOperands);

                if (IsConditionalBranch(mnemonic, parsedOperands))
                {
                    stats.ConditionalBranchCount++;
                    AddBranch();
                }
                else if (IsUnconditionalBranch(mnemonic))
                {
                    stats.UnconditionalBranchCount++;
                    AddBranch();
                }

                void AddBranch()
                {
                    if (TryGetDirectBranchTarget(parsedOperands, out var target))
                        branches.Add(new(instructionIndex, mnemonic, target, source));
                }
            }

            foreach (var branch in branches)
            {
                if (!labels.TryGetValue(branch.Target, out var targetIndex) || targetIndex >= branch.InstructionIndex)
                    continue;

                // a backward direct branch is only a loop candidate; this deliberately avoids implying CFG knowledge.
                stats.BackwardBranches.Add(branch);
            }

            return stats;
        }

        static void AnalyzeInstruction(
            BurstAsmStats stats,
            string mnemonic,
            string operands,
            IReadOnlyList<string> parsedOperands)
        {
            var baseMnemonic = BaseMnemonic(mnemonic);
            var isXor = IsXorMnemonic(baseMnemonic);
            var isZeroingXor = isXor && IsZeroingIdiom(parsedOperands);

            if (isXor)
            {
                stats.XorInstructionCount++;
                if (isZeroingXor)
                    stats.ZeroingXorInstructionCount++;
            }

            if (baseMnemonic == "movabs")
            {
                var source = parsedOperands.Count > 1 ? parsedOperands[1] : string.Empty;
                if (IsNumericImmediate(source))
                    stats.NumericMovabsCount++;
                else
                    stats.SymbolMovabsCount++;
            }

            if (baseMnemonic is "push" or "vpush")
                stats.PushInstructionCount++;
            else if (baseMnemonic is "pop" or "vpop")
                stats.PopInstructionCount++;

            if (IsCall(mnemonic))
                AnalyzeCall(stats, mnemonic, parsedOperands);
            else if (IsReturn(mnemonic))
                stats.ReturnCount++;

            var memoryAccess = ClassifyMemoryAccess(baseMnemonic, parsedOperands);
            if (memoryAccess != BurstMemoryAccessKind.None)
            {
                switch (memoryAccess)
                {
                    case BurstMemoryAccessKind.Load:
                        stats.LoadInstructionCount++;
                        break;
                    case BurstMemoryAccessKind.Store:
                        stats.StoreInstructionCount++;
                        break;
                    case BurstMemoryAccessKind.ReadModifyWrite:
                        stats.ReadModifyWriteInstructionCount++;
                        break;
                    default:
                        stats.OtherMemoryInstructionCount++;
                        break;
                }

                if (HasStackOrFrameOperand(operands))
                    stats.StackFrameMemoryInstructionCount++;
                if (operands.IndexOf(".lcpi", StringComparison.OrdinalIgnoreCase) >= 0
                    || operands.IndexOf(".lconst", StringComparison.OrdinalIgnoreCase) >= 0)
                    stats.ConstantPoolMemoryInstructionCount++;
            }

            if (IsAddressGeneration(baseMnemonic))
                stats.AddressGenerationInstructionCount++;

            var simdRole = ClassifySimd(baseMnemonic, operands, isZeroingXor);
            switch (simdRole)
            {
                case BurstSimdRole.Transfer:
                    stats.SimdTransferInstructionCount++;
                    break;
                case BurstSimdRole.Lane:
                    stats.SimdLaneInstructionCount++;
                    break;
                case BurstSimdRole.ScalarCompute:
                    stats.SimdScalarComputeInstructionCount++;
                    break;
                case BurstSimdRole.PackedCompute:
                    stats.SimdPackedComputeInstructionCount++;
                    RecordPackedComputeWidth(stats, operands);
                    break;
                case BurstSimdRole.Setup:
                    stats.SimdSetupInstructionCount++;
                    break;
                case BurstSimdRole.Other:
                    stats.SimdOtherInstructionCount++;
                    break;
            }
        }

        static void AnalyzeCall(BurstAsmStats stats, string mnemonic, IReadOnlyList<string> operands)
        {
            if (!IsDirectCall(mnemonic, operands))
            {
                stats.IndirectCallCount++;
                return;
            }

            stats.DirectCallCount++;
            var target = CleanDiagnosticLine(CleanTransferTarget(operands[0]));
            if (target.Length == 0 || stats.DirectCallTargets.Contains(target))
                return;

            stats.DirectCallTargets.Add(target);
        }

        static bool IsDirectCall(string mnemonic, IReadOnlyList<string> operands)
        {
            if (BaseMnemonic(mnemonic) is "blr" or "call_indirect")
                return false;
            if (operands.Count == 0 || HasMemorySyntax(operands[0]))
                return false;

            return !IsRegisterOperand(operands[0]);
        }

        static string CleanTransferTarget(string value)
        {
            var target = value.Trim();
            if (target.EndsWith("@PLT", StringComparison.OrdinalIgnoreCase))
                target = target[..^4];

            return target.Trim().Trim('"');
        }

        static BurstMemoryAccessKind ClassifyMemoryAccess(
            string mnemonic,
            IReadOnlyList<string> operands)
        {
            if (IsAddressGeneration(mnemonic))
                return BurstMemoryAccessKind.None;

            var memoryIndex = -1;
            for (int index = 0, n = operands.Count; index < n; ++index)
            {
                if (!HasMemorySyntax(operands[index]))
                    continue;

                memoryIndex = index;
                break;
            }

            // arm and wasm encode direction in the mnemonic; x86 needs destination-position semantics.
            if (IsArmReadModifyWrite(mnemonic))
                return BurstMemoryAccessKind.ReadModifyWrite;
            if (IsLoadMnemonic(mnemonic))
                return BurstMemoryAccessKind.Load;
            if (IsStoreMnemonic(mnemonic))
                return BurstMemoryAccessKind.Store;
            if (memoryIndex < 0)
                return BurstMemoryAccessKind.None;
            if (memoryIndex > 0)
                return BurstMemoryAccessKind.Load;

            if (IsReadOnlyMemoryDestination(mnemonic))
                return BurstMemoryAccessKind.Load;
            if (IsWriteOnlyMemoryDestination(mnemonic))
                return BurstMemoryAccessKind.Store;
            if (IsReadModifyWriteMnemonic(mnemonic))
                return BurstMemoryAccessKind.ReadModifyWrite;

            return BurstMemoryAccessKind.Other;
        }

        static bool IsAddressGeneration(string mnemonic) =>
            mnemonic is "lea" or "adr" or "adrp";

        static bool IsArmReadModifyWrite(string mnemonic) =>
            mnemonic.StartsWith("ldadd", StringComparison.Ordinal)
            || mnemonic.StartsWith("ldclr", StringComparison.Ordinal)
            || mnemonic.StartsWith("ldeor", StringComparison.Ordinal)
            || mnemonic.StartsWith("ldset", StringComparison.Ordinal)
            || mnemonic.StartsWith("ldsmax", StringComparison.Ordinal)
            || mnemonic.StartsWith("ldsmin", StringComparison.Ordinal)
            || mnemonic.StartsWith("ldumax", StringComparison.Ordinal)
            || mnemonic.StartsWith("ldumin", StringComparison.Ordinal)
            || mnemonic.StartsWith("swp", StringComparison.Ordinal)
            || mnemonic.StartsWith("cas", StringComparison.Ordinal);

        static bool IsLoadMnemonic(string mnemonic) =>
            mnemonic.IndexOf(".load", StringComparison.Ordinal) > 0
            || mnemonic.StartsWith("ldr", StringComparison.Ordinal)
            || mnemonic.StartsWith("ldp", StringComparison.Ordinal)
            || mnemonic.StartsWith("ld1", StringComparison.Ordinal)
            || mnemonic.StartsWith("ld2", StringComparison.Ordinal)
            || mnemonic.StartsWith("ld3", StringComparison.Ordinal)
            || mnemonic.StartsWith("ld4", StringComparison.Ordinal)
            || mnemonic.StartsWith("ldar", StringComparison.Ordinal)
            || mnemonic.StartsWith("ldax", StringComparison.Ordinal)
            || mnemonic.StartsWith("ldxr", StringComparison.Ordinal)
            || mnemonic is "fld" or "fld1" or "fldl2e" or "fldl2t" or "fldlg2" or "fldln2" or "fldpi" or "fldz";

        static bool IsStoreMnemonic(string mnemonic) =>
            mnemonic.IndexOf(".store", StringComparison.Ordinal) > 0
            || mnemonic.StartsWith("str", StringComparison.Ordinal)
            || mnemonic.StartsWith("stp", StringComparison.Ordinal)
            || mnemonic.StartsWith("st1", StringComparison.Ordinal)
            || mnemonic.StartsWith("st2", StringComparison.Ordinal)
            || mnemonic.StartsWith("st3", StringComparison.Ordinal)
            || mnemonic.StartsWith("st4", StringComparison.Ordinal)
            || mnemonic.StartsWith("stlr", StringComparison.Ordinal)
            || mnemonic.StartsWith("stlx", StringComparison.Ordinal)
            || mnemonic.StartsWith("stxr", StringComparison.Ordinal);

        static bool IsReadOnlyMemoryDestination(string mnemonic) =>
            mnemonic is "cmp" or "test" or "bt" or "call" or "jmp" or "push"
            || mnemonic.StartsWith("prefetch", StringComparison.Ordinal)
            || mnemonic.StartsWith("mul", StringComparison.Ordinal)
            || mnemonic is "imul" or "div" or "idiv";

        static bool IsWriteOnlyMemoryDestination(string mnemonic) =>
            mnemonic is "pop" or "fstp" or "fistp" or "stmxcsr"
            || mnemonic.StartsWith("mov", StringComparison.Ordinal)
            || mnemonic.StartsWith("vmov", StringComparison.Ordinal)
            || mnemonic.StartsWith("set", StringComparison.Ordinal);

        static bool IsReadModifyWriteMnemonic(string mnemonic) =>
            mnemonic is "add" or "adc" or "sub" or "sbb" or "and" or "or" or "xor"
            or "inc" or "dec" or "neg" or "not" or "xchg" or "cmpxchg";

        static BurstSimdRole ClassifySimd(string mnemonic, string operands, bool isZeroingXor)
        {
            var hasXmm = ContainsRegisterPrefix(operands, "xmm");
            var hasYmm = ContainsRegisterPrefix(operands, "ymm");
            var hasZmm = ContainsRegisterPrefix(operands, "zmm");
            var hasArmVector = ContainsRegisterPrefix(operands, "v")
                               || ContainsRegisterPrefix(operands, "q")
                               || ContainsRegisterPrefix(operands, "z");
            var hasArmScalar = ContainsRegisterPrefix(operands, "s")
                               || ContainsRegisterPrefix(operands, "d");
            var hasPackedMnemonic = IsPackedVectorMnemonic(mnemonic);
            var isVector = hasXmm || hasYmm || hasZmm || hasArmVector || hasArmScalar
                           || hasPackedMnemonic
                           || mnemonic is "vzeroupper" or "vzeroall";
            if (!isVector)
                return BurstSimdRole.None;
            if (isZeroingXor || mnemonic is "vzeroupper" or "vzeroall")
                return BurstSimdRole.Setup;
            if (IsVectorTransfer(mnemonic))
                return BurstSimdRole.Transfer;
            if (IsVectorLaneOperation(mnemonic))
                return BurstSimdRole.Lane;
            if (IsScalarSimdMnemonic(mnemonic) || hasArmScalar && !hasArmVector)
                return BurstSimdRole.ScalarCompute;
            if (hasXmm || hasYmm || hasZmm || hasArmVector || hasPackedMnemonic)
                return BurstSimdRole.PackedCompute;

            return BurstSimdRole.Other;
        }

        static bool IsVectorTransfer(string mnemonic) =>
            mnemonic.StartsWith("mov", StringComparison.Ordinal)
            || mnemonic.StartsWith("vmov", StringComparison.Ordinal)
            || mnemonic.StartsWith("vld", StringComparison.Ordinal)
            || mnemonic.StartsWith("vst", StringComparison.Ordinal)
            || mnemonic.IndexOf(".load", StringComparison.Ordinal) > 0
            || mnemonic.IndexOf(".store", StringComparison.Ordinal) > 0
            || IsLoadMnemonic(mnemonic)
            || IsStoreMnemonic(mnemonic)
            || mnemonic.IndexOf("broadcast", StringComparison.Ordinal) >= 0;

        static bool IsVectorLaneOperation(string mnemonic) =>
            ContainsAny(
                mnemonic,
                "shuf", "perm", "blend", "unpck", "pack", "insert", "extract",
                "replace", "splat", "swizzle", "pinsr", "pextr", "alignr"
            )
            || StartsWithAny(
                mnemonic,
                "zip", "uzp", "trn", "tbl", "tbx", "ext", "ins", "dup", "rev"
            );

        static bool ContainsAny(string value, params string[] parts)
        {
            foreach (var part in parts)
                if (value.IndexOf(part, StringComparison.Ordinal) >= 0)
                    return true;

            return false;
        }

        static bool StartsWithAny(string value, params string[] prefixes)
        {
            foreach (var prefix in prefixes)
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                    return true;

            return false;
        }

        static void RecordPackedComputeWidth(BurstAsmStats stats, string operands)
        {
            if (ContainsRegisterPrefix(operands, "z") && !ContainsRegisterPrefix(operands, "zmm"))
            {
                stats.PackedComputeUsesScalableVectors = true;
                return;
            }

            var width = ContainsRegisterPrefix(operands, "zmm") ? 512
                : ContainsRegisterPrefix(operands, "ymm") ? 256
                : 128;
            stats.PackedComputeWidth = Math.Max(stats.PackedComputeWidth, width);
        }

        static bool IsXorMnemonic(string mnemonic) =>
            mnemonic is "xor" or "eor" or "veor" or "pxor" or "vpxor"
            or "xorps" or "xorpd" or "vxorps" or "vxorpd";

        static bool IsZeroingIdiom(IReadOnlyList<string> operands)
        {
            if (operands.Count < 2)
                return false;

            var left = operands.Count == 2 ? operands[0] : operands[^2];
            var right = operands[^1];
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase)
                   && IsRegisterOperand(left);
        }

        static bool IsNumericImmediate(string operand)
        {
            var value = operand.Trim();
            if (value.StartsWith("-", StringComparison.Ordinal) || value.StartsWith("+", StringComparison.Ordinal))
                value = value[1..];
            var hexadecimal = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (hexadecimal)
                value = value[2..];
            if (value.Length == 0)
                return false;

            foreach (var character in value)
                if (!char.IsDigit(character)
                    && (!hexadecimal || character is not (>= 'a' and <= 'f') and not (>= 'A' and <= 'F')))
                    return false;

            return true;
        }

        static string BaseMnemonic(string mnemonic)
        {
            var space = mnemonic.LastIndexOf(' ');
            return space < 0 ? mnemonic : mnemonic[(space + 1)..];
        }

        static bool HasMemorySyntax(string operand) =>
            operand.IndexOf('[', StringComparison.Ordinal) >= 0
            && operand.IndexOf(']', StringComparison.Ordinal) >= 0;

        static bool IsRegisterOperand(string operand)
        {
            var value = operand.Trim().TrimStart('%');
            var dot = value.IndexOf('.');
            if (dot >= 0)
                value = value[..dot];
            var bracket = value.IndexOf('[');
            if (bracket >= 0)
                value = value[..bracket];
            if (value.Length == 0
                || value.IndexOf(' ') >= 0
                || value.IndexOf('+') >= 0
                || value.IndexOf('-') >= 0
                || value.IndexOf('*') >= 0)
                return false;

            if (value is "rax" or "rbx" or "rcx" or "rdx" or "rsi" or "rdi" or "rbp" or "rsp"
                or "eax" or "ebx" or "ecx" or "edx" or "esi" or "edi" or "ebp" or "esp"
                or "ax" or "bx" or "cx" or "dx" or "si" or "di" or "bp" or "sp"
                or "al" or "bl" or "cl" or "dl" or "sil" or "dil" or "bpl" or "spl"
                or "lr" or "fp" or "xzr" or "wzr")
                return true;

            return RegisterHasNumericSuffix(value, "r")
                   || RegisterHasNumericSuffix(value, "x")
                   || RegisterHasNumericSuffix(value, "w")
                   || RegisterHasNumericSuffix(value, "v")
                   || RegisterHasNumericSuffix(value, "q")
                   || RegisterHasNumericSuffix(value, "s")
                   || RegisterHasNumericSuffix(value, "d")
                   || RegisterHasNumericSuffix(value, "z")
                   || RegisterHasNumericSuffix(value, "p")
                   || RegisterHasNumericSuffix(value, "xmm")
                   || RegisterHasNumericSuffix(value, "ymm")
                   || RegisterHasNumericSuffix(value, "zmm")
                   || RegisterHasNumericSuffix(value, "k");
        }

        static bool RegisterHasNumericSuffix(string value, string prefix)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || value.Length == prefix.Length)
                return false;

            for (int index = prefix.Length, n = value.Length; index < n; ++index)
                if (!char.IsDigit(value[index]))
                    return false;

            return true;
        }

        static List<string> SplitOperands(string operands)
        {
            var result = new List<string>();
            var start = 0;
            var depth = 0;
            var quoted = false;
            // quoted symbols and generic signatures can contain commas; only top-level commas separate operands.
            for (int index = 0, n = operands.Length; index <= n; ++index)
            {
                if (index < n)
                {
                    var character = operands[index];
                    if (character == '"')
                    {
                        quoted = !quoted;
                        continue;
                    }

                    if (quoted)
                        continue;
                    if (character is '[' or '(' or '{' or '<')
                    {
                        depth++;
                        continue;
                    }

                    if (character is ']' or ')' or '}' or '>')
                    {
                        if (depth > 0)
                            depth--;
                        continue;
                    }

                    if (character != ',' || depth != 0)
                        continue;
                }

                var operand = operands[start..index].Trim();
                if (operand.Length > 0)
                    result.Add(operand);
                start = index + 1;
            }

            return result;
        }

        static bool TryParseRenderedSourceLocation(string line, out string source)
        {
            source = string.Empty;
            var match = renderedSourceLocation.Match(line);
            if (!match.Success)
                return false;

            source = $"{Path.GetFileName(match.Groups["file"].Value)}:{match.Groups["line"].Value}";
            return true;
        }

        static bool IsUnknownSourceLocation(string line)
        {
            var text = line.TrimStart();
            return (text.StartsWith("#", StringComparison.Ordinal) || text.StartsWith(";", StringComparison.Ordinal))
                   && text.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool TryParseCodeLabel(string line, out string label)
        {
            label = string.Empty;
            var text = line.Trim();
            if (!text.EndsWith(":", StringComparison.Ordinal))
                return false;

            label = text[..^1].Trim().Trim('"');
            return label.Length > 0;
        }

        static bool TryGetDirectBranchTarget(IReadOnlyList<string> operands, out string target)
        {
            target = string.Empty;
            if (operands.Count == 0)
                return false;

            var value = operands[^1].Trim();
            if (value.StartsWith("short ", StringComparison.OrdinalIgnoreCase))
                value = value[6..].TrimStart();
            if (value.StartsWith("near ", StringComparison.OrdinalIgnoreCase))
                value = value[5..].TrimStart();
            if (HasMemorySyntax(value) || IsRegisterOperand(value))
                return false;

            target = value.Trim('"');
            return target.Length > 0;
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
            var operandStart = firstEnd;
            while (operandStart < text.Length && char.IsWhiteSpace(text[operandStart]))
                ++operandStart;
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

                ++index;
            }

            return index;
        }

        static bool IsScalarSimdMnemonic(string mnemonic) =>
            mnemonic.EndsWith("ss", StringComparison.Ordinal)
            || mnemonic.EndsWith("sd", StringComparison.Ordinal);

        static bool IsPackedVectorMnemonic(string mnemonic) =>
            mnemonic.StartsWith("v", StringComparison.Ordinal) && !IsScalarSimdMnemonic(mnemonic)
            || mnemonic.StartsWith("v128.", StringComparison.Ordinal)
            || StartsWithAny(
                mnemonic,
                "i8x16.", "i16x8.", "i32x4.", "i64x2.", "f32x4.", "f64x2."
            )
            || mnemonic.StartsWith("padd", StringComparison.Ordinal)
            || mnemonic.StartsWith("psub", StringComparison.Ordinal)
            || mnemonic.StartsWith("pmul", StringComparison.Ordinal)
            || mnemonic.StartsWith("pand", StringComparison.Ordinal)
            || mnemonic.StartsWith("por", StringComparison.Ordinal)
            || mnemonic.StartsWith("pxor", StringComparison.Ordinal)
            || mnemonic.EndsWith("ps", StringComparison.Ordinal)
            || mnemonic.EndsWith("pd", StringComparison.Ordinal);

        static bool IsConditionalBranch(string mnemonic, IReadOnlyList<string> operands)
        {
            if (mnemonic is "cbz" or "cbnz" or "tbz" or "tbnz" or "br_if")
                return true;

            if (operands.Count > 0 && mnemonic.StartsWith("loop", StringComparison.Ordinal))
                return true;

            if (mnemonic.StartsWith("b.", StringComparison.Ordinal))
                return true;

            return mnemonic.StartsWith("j", StringComparison.Ordinal) && mnemonic != "jmp";
        }

        static bool IsUnconditionalBranch(string mnemonic) =>
            mnemonic is "jmp" or "b" or "br";

        static bool IsCall(string mnemonic) =>
            mnemonic is "call" or "call_indirect" or "bl" or "blr";

        static bool IsReturn(string mnemonic) =>
            mnemonic.StartsWith("ret", StringComparison.Ordinal)
            || mnemonic == "end_function";

        static bool HasStackOrFrameOperand(string operands) =>
            ContainsRegister(operands, "rsp")
            || ContainsRegister(operands, "rbp")
            || ContainsRegister(operands, "esp")
            || ContainsRegister(operands, "ebp")
            || ContainsRegister(operands, "sp")
            || ContainsRegister(operands, "fp")
            || ContainsRegister(operands, "x29");

        static bool ContainsRegisterPrefix(string text, string prefix)
        {
            foreach (var token in RegisterTokens(text))
                if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    if (token.Length > prefix.Length && char.IsDigit(token[prefix.Length]))
                        return true;

            return false;
        }

        static bool ContainsRegister(string text, string register)
        {
            foreach (var token in RegisterTokens(text))
                if (string.Equals(token, register, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        static IEnumerable<string> RegisterTokens(string text)
        {
            var start = -1;
            for (int index = 0, n = text.Length; index <= n; ++index)
            {
                if (index < n && char.IsLetterOrDigit(text[index]))
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
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            if (!stats.Context.IsEmpty)
            {
                builder.Append("**Compilation:** ");
                builder.Append($"`{stats.Context.Cpu}/{stats.Context.CompilerTarget}` · ");
                builder.Append($"`{stats.Context.Optimization}` · ");
                builder.Append($"floats `{stats.Context.FloatMode}/{stats.Context.FloatPrecision}` · ");
                builder.Append($"safety checks `{stats.Context.SafetyChecks}`\n\n");
            }

            if (stats.AnalyzedFunction.Length > 0)
                builder.Append($"**Selected function:** `{stats.AnalyzedFunction}`\n\n");
            if (stats.EntryForwarders.Count > 0)
                builder.Append($"**Entry forwarder:** `{string.Join("` → `", stats.EntryForwarders)}`\n\n");

            builder.Append("**Static code summary**\n\n");
            builder.Append($"- Instructions: {stats.InstructionCount}\n");
            AppendControlFlow(builder, stats);
            AppendSimd(builder, stats);
            AppendMemory(builder, stats);
            AppendIntegerIdioms(builder, stats);

            if (stats.DirectCallTargets.Count > 0)
            {
                builder.Append("- Direct callees: ");
                var count = Math.Min(stats.DirectCallTargets.Count, MaxDirectCalleeDetails);
                for (int index = 0; index < count; ++index)
                {
                    if (index > 0)
                        builder.Append(", ");
                    builder.Append($"`{stats.DirectCallTargets[index]}`");
                }
                if (stats.DirectCallTargets.Count > count)
                    builder.Append($"; {stats.DirectCallTargets.Count - count} more");
                builder.Append('\n');
            }

            AppendBackwardBranches(builder, stats);
            AppendNotes(builder, stats);
            AppendOptimizationRemarks(builder, stats);
            return builder.ToString().TrimEnd();
        }

        static void AppendControlFlow(StringBuilder builder, BurstAsmStats stats)
        {
            var parts = new List<string>();
            Add(stats.ConditionalBranchCount, "conditional branch", "conditional branches");
            Add(stats.UnconditionalBranchCount, "jump", "jumps");
            Add(stats.DirectCallCount, "direct call", "direct calls");
            Add(stats.IndirectCallCount, "indirect call", "indirect calls");
            Add(stats.ReturnCount, "return", "returns");
            Add(stats.BackwardBranches.Count, "backward branch", "backward branches");
            if (parts.Count > 0)
                builder.Append($"- Control flow: {string.Join(", ", parts)}\n");

            void Add(int count, string singular, string plural)
            {
                if (count > 0)
                    parts.Add($"{count} {(count == 1 ? singular : plural)}");
            }
        }

        static void AppendSimd(StringBuilder builder, BurstAsmStats stats)
        {
            var parts = new List<string>();
            Add(stats.SimdPackedComputeInstructionCount, "packed compute");
            Add(stats.SimdScalarComputeInstructionCount, "scalar compute");
            Add(stats.SimdTransferInstructionCount, "transfer");
            Add(stats.SimdLaneInstructionCount, "lane/shuffle");
            Add(stats.SimdSetupInstructionCount, "setup/control");
            Add(stats.SimdOtherInstructionCount, "unclassified");
            if (parts.Count == 0)
                return;

            builder.Append($"- SIMD: {string.Join(", ", parts)}");
            if (stats.PackedComputeUsesScalableVectors)
                builder.Append("; widest packed compute scalable (SVE)");
            else if (stats.PackedComputeWidth > 0)
                builder.Append($"; widest packed compute {stats.PackedComputeWidth}-bit");
            builder.Append('\n');

            void Add(int count, string name)
            {
                if (count > 0)
                    parts.Add($"{name} {count}");
            }
        }

        static void AppendMemory(StringBuilder builder, BurstAsmStats stats)
        {
            var accesses = new List<string>();
            Add(stats.LoadInstructionCount, "load", "loads");
            Add(stats.StoreInstructionCount, "store", "stores");
            Add(stats.ReadModifyWriteInstructionCount, "read-modify-write", "read-modify-write");
            Add(stats.OtherMemoryInstructionCount, "unclassified", "unclassified");
            if (accesses.Count > 0)
            {
                builder.Append($"- Memory access instructions: {string.Join(", ", accesses)}");
                var annotations = new List<string>();
                if (stats.StackFrameMemoryInstructionCount > 0)
                    annotations.Add($"stack/frame {stats.StackFrameMemoryInstructionCount}");
                if (stats.ConstantPoolMemoryInstructionCount > 0)
                    annotations.Add($"constant-pool {stats.ConstantPoolMemoryInstructionCount}");
                if (annotations.Count > 0)
                    builder.Append($"; {string.Join(", ", annotations)}");
                builder.Append('\n');
            }

            var stack = new List<string>();
            if (stats.PushInstructionCount > 0)
                stack.Add($"push {stats.PushInstructionCount}");
            if (stats.PopInstructionCount > 0)
                stack.Add($"pop {stats.PopInstructionCount}");
            if (stack.Count > 0)
                builder.Append($"- Explicit stack operations: {string.Join(", ", stack)}\n");
            if (stats.AddressGenerationInstructionCount > 0)
                builder.Append($"- Address generation instructions: {stats.AddressGenerationInstructionCount}\n");

            void Add(int count, string singular, string plural)
            {
                if (count > 0)
                    accesses.Add($"{count} {(count == 1 ? singular : plural)}");
            }
        }

        static void AppendIntegerIdioms(StringBuilder builder, BurstAsmStats stats)
        {
            if (stats.XorInstructionCount > 0)
            {
                var nonZeroing = stats.XorInstructionCount - stats.ZeroingXorInstructionCount;
                builder.Append($"- XOR instructions: {stats.XorInstructionCount}");
                if (stats.ZeroingXorInstructionCount > 0)
                    builder.Append($"; zeroing {stats.ZeroingXorInstructionCount}");
                if (nonZeroing > 0)
                    builder.Append($"; non-zeroing {nonZeroing}");
                builder.Append('\n');
            }

            if (stats.NumericMovabsCount == 0 && stats.SymbolMovabsCount == 0)
                return;

            var parts = new List<string>();
            if (stats.NumericMovabsCount > 0)
                parts.Add($"numeric constants {stats.NumericMovabsCount}");
            if (stats.SymbolMovabsCount > 0)
                parts.Add($"symbol addresses {stats.SymbolMovabsCount}");
            builder.Append($"- `movabs` materialization: {string.Join(", ", parts)}\n");
        }

        static void AppendBackwardBranches(StringBuilder builder, BurstAsmStats stats)
        {
            if (stats.BackwardBranches.Count == 0)
                return;

            builder.Append("\n**Backward branches (loop candidates)**\n\n");
            var count = Math.Min(stats.BackwardBranches.Count, MaxBackwardBranchDetails);
            for (int index = 0; index < count; ++index)
            {
                var branch = stats.BackwardBranches[index];
                builder.Append($"- `{branch.Mnemonic} → {branch.Target}`");
                if (branch.Source.Length > 0)
                    builder.Append($" — `{branch.Source}`");
                builder.Append('\n');
            }
            if (stats.BackwardBranches.Count > count)
                builder.Append($"- {stats.BackwardBranches.Count - count} more backward branches omitted.\n");
        }

        static void AppendNotes(StringBuilder builder, BurstAsmStats stats)
        {
            var movementOnly = stats.SimdPackedComputeInstructionCount == 0
                               && stats.SimdScalarComputeInstructionCount == 0
                               && stats.SimdOtherInstructionCount == 0
                               && stats.SimdTransferInstructionCount + stats.SimdLaneInstructionCount > 0;
            var fastCompilation = stats.Context.Optimization == "Fast compilation";
            if (!movementOnly && !fastCompilation)
                return;

            builder.Append("\n**Notes**\n\n");
            if (movementOnly)
                builder.Append("- Vector registers are used only for transfers or lane manipulation; packed computation was not established.\n");
            if (fastCompilation)
                builder.Append("- Fast compilation limits Burst vectorization, inlining, and loop optimization.\n");
        }

        static void AppendOptimizationRemarks(StringBuilder builder, BurstAsmStats stats)
        {
            if (stats.OptimizationRemarks.Count > 0)
            {
                builder.Append("\n**Compiler optimization remarks**\n\n");
                var count = Math.Min(stats.OptimizationRemarks.Count, MaxOptimizationRemarks);
                for (int index = 0; index < count; ++index)
                {
                    var remark = stats.OptimizationRemarks[index];
                    builder.Append($"- `{remark.Type}`");
                    if (remark.Pass.Length > 0)
                    {
                        builder.Append($" · `{remark.Pass}");
                        if (remark.Reason.Length > 0)
                            builder.Append($"/{remark.Reason}");
                        builder.Append('`');
                    }
                    if (remark.Source.Length > 0)
                        builder.Append($" · `{remark.Source}`");
                    builder.Append($" — {remark.Message}\n");
                }
                if (stats.OptimizationRemarks.Count > count)
                    builder.Append($"- {stats.OptimizationRemarks.Count - count} more compiler remarks omitted.\n");
            }

            if (stats.RemarksError.Length > 0)
                builder.Append($"\n*Compiler optimization remarks could not be retrieved: {stats.RemarksError}*\n");
        }

        internal static BurstCompilationContext ParseCompilationContext(string options, string cpu)
        {
            // inspector overrides are appended to Burst's resolved options, so the last occurrence is authoritative.
            var lines = options.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var compilerTarget = ReadOption("target=");
            var optimization = HasOption("disable-opt") ? "Disabled"
                : HasOption("opt-for-size") ? "Size"
                : ReadOption("opt-level=") switch
                {
                    "1" => "Fast compilation",
                    "2" => "Balanced",
                    "3" => "Performance",
                    { Length: > 0 } value => $"Optimization level {value}",
                    _ => "Default",
                };
            var floatMode = ReadOption("float-mode=");
            if (floatMode.Length == 0 || string.Equals(floatMode, "Default", StringComparison.OrdinalIgnoreCase))
                floatMode = "Strict";
            var floatPrecision = ReadOption("float-precision=");
            if (floatPrecision.Length == 0)
                floatPrecision = "Standard";
            var safetyChecks = ReadOption("global-safety-checks-setting=");
            var safetyDisabled = HasOption("disable-safety-checks");
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

            string ReadOption(string name)
            {
                var value = string.Empty;
                var prefix = "--" + name;
                foreach (var line in lines)
                {
                    var text = line.Trim();
                    if (text.StartsWith(prefix, StringComparison.Ordinal))
                        value = text[prefix.Length..];
                }
                return value;
            }

            bool HasOption(string name)
            {
                var expected = "--" + name;
                foreach (var line in lines)
                    if (line.Trim() == expected)
                        return true;

                return false;
            }
        }

        static void ParseOptimizationRemarks(string text, List<BurstOptimizationRemark> destination)
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
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
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

                type = CleanDiagnosticLine(type);
                message = CleanDiagnosticLine(message);
                pass = CleanDiagnosticLine(pass);
                reason = CleanDiagnosticLine(reason);
                function = CleanDiagnosticLine(function);
                var key = $"{type}\0{message}\0{pass}\0{reason}\0{function}";
                if (seen.Add(key))
                    destination.Add(new(type.Length == 0 ? "Remark" : type, message, pass, reason, source));
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

        static string CleanDiagnosticLine(string line)
        {
            line = sourceLocation.Replace(
                line,
                match => $"{match.Groups["prefix"].Value}{match.Groups["file"].Value}:{match.Groups["line"].Value}{match.Groups["rest"].Value}"
            );

            line = fromAssemblyQualifier.Replace(line, string.Empty);
            line = assemblyQualifier.Replace(line, string.Empty);
            line = hashSuffix.Replace(line, string.Empty);
            line = FormatRawBurstSignatureParameters(line);
            line = SimplifyMetadataGenerics(line);
            line = ReplaceBuiltInTypeNames(line);
            line = StripCommonLowercaseTypeNamespaces(line);
            line = StripNamespaces(line);
            line = ReplaceBuiltInTypeNames(line);
            return LimitGuidIds(line);
        }

        static string FormatRawBurstSignatureParameters(string line)
        {
            if (line.IndexOf('|') < 0)
                return line;

            StringBuilder? builder = null;
            var offset = 0;
            while (offset < line.Length)
            {
                var open = line.IndexOf('(', offset);
                if (open < 0)
                    break;

                var close = FindMatchingParen(line, open);
                if (close < 0)
                    break;

                var parameters = line[(open + 1)..close];
                if (parameters.IndexOf('|') < 0)
                {
                    offset = close + 1;
                    continue;
                }

                if (!IsLikelySignatureOpen(line, open) || !LooksLikeRawBurstParameterList(parameters))
                {
                    offset = close + 1;
                    continue;
                }

                builder ??= new(line.Length);
                builder.Append(line, offset, open - offset + 1);
                AppendRawBurstParameters(builder, parameters);
                builder.Append(')');
                offset = close + 1;
            }

            if (builder == null)
                return line;

            builder.Append(line, offset, line.Length - offset);
            return builder.ToString();
        }

        static bool IsLikelySignatureOpen(string text, int open)
            => open > 0 && IsSignatureNameEnd(text[open - 1]);

        static bool IsSignatureNameEnd(char character)
            => char.IsLetterOrDigit(character)
               || character is '_' or '>' or ']';

        static int FindMatchingParen(string text, int open)
        {
            var depth = 0;
            for (var index = open; index < text.Length; index++)
            {
                if (text[index] == '(')
                    depth++;
                else if (text[index] == ')' && --depth == 0)
                    return index;
            }

            return -1;
        }

        static bool LooksLikeRawBurstParameterList(string parameters)
        {
            var start = 0;
            var depth = 0;
            var count = 0;
            for (var index = 0; index <= parameters.Length; index++)
            {
                if (index < parameters.Length)
                {
                    if (parameters[index] is '<' or '[')
                    {
                        depth++;
                        continue;
                    }

                    if (parameters[index] is '>' or ']')
                    {
                        if (depth > 0)
                            depth--;

                        continue;
                    }

                    if (parameters[index] != '|' || depth != 0)
                        continue;
                }

                if (!LooksLikeRawBurstParameter(parameters[start..index]))
                    return false;

                count++;
                start = index + 1;
            }

            return count > 1;
        }

        static bool LooksLikeRawBurstParameter(string parameter)
        {
            parameter = parameter.Trim();
            if (parameter.EndsWith("&", StringComparison.Ordinal))
                parameter = parameter[..^1].TrimEnd();

            if (parameter.Length == 0)
                return false;

            return char.IsUpper(parameter[0])
                   || parameter.IndexOf('.') >= 0
                   || parameter.IndexOf('`') >= 0
                   || parameter.IndexOf('<') >= 0
                   || parameter.IndexOf('*') >= 0;
        }

        static void AppendRawBurstParameters(StringBuilder builder, string parameters)
        {
            var start = 0;
            var depth = 0;
            var appendedAny = false;
            for (var index = 0; index <= parameters.Length; index++)
            {
                if (index < parameters.Length)
                {
                    if (parameters[index] is '<' or '[')
                    {
                        depth++;
                        continue;
                    }

                    if (parameters[index] is '>' or ']')
                    {
                        if (depth > 0)
                            depth--;

                        continue;
                    }

                    if (parameters[index] != '|' || depth != 0)
                        continue;
                }

                AppendRawBurstParameter(builder, parameters[start..index], ref appendedAny);
                start = index + 1;
            }
        }

        static void AppendRawBurstParameter(StringBuilder builder, string parameter, ref bool appendedAny)
        {
            parameter = parameter.Trim();
            if (parameter.Length == 0)
                return;

            if (appendedAny)
                builder.Append(", ");

            appendedAny = true;
            if (parameter.EndsWith("&", StringComparison.Ordinal))
            {
                builder.Append("ref ");
                builder.Append(parameter, 0, parameter.Length - 1);
                return;
            }

            builder.Append(parameter);
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
            symbol = StripCommonLowercaseTypeNamespaces(symbol);
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

        static string StripCommonLowercaseTypeNamespaces(string symbol) =>
            unityMathematicsName.Replace(symbol, "$1");

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

        static string FirstLine(string text)
        {
            var end = text.IndexOfAny(new[] { '\r', '\n' });
            return (end < 0 ? text : text[..end]).Trim();
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

        static BridgeCommandResult Ambiguous(
            string query,
            IReadOnlyList<BurstTarget> targets,
            BurstAsmTargetMatch match) =>
            new()
            {
                outcome = ToolOutcome.AmbiguousTarget,
                diagnostic = Candidates(
                    $"Multiple Burst compile targets match '{query?.Trim() ?? string.Empty}'.",
                    targets,
                    match.CandidateIndexes,
                    match.CandidateCount
                ),
            };

        static BridgeCommandResult NoMatch(
            string query,
            IReadOnlyList<BurstTarget> targets,
            BurstAsmTargetMatch match) =>
            Error(NoMatchDiagnostic(query, targets, match.CandidateIndexes, match.CandidateCount));

        internal static string NoMatchDiagnostic(string query, IReadOnlyList<BurstTarget> targets, int[] indexes)
            => NoMatchDiagnostic(query, targets, indexes, indexes.Length);

        internal static string NoMatchDiagnostic(
            string query,
            IReadOnlyList<BurstTarget> targets,
            int[] indexes,
            int candidateCount)
        {
            var trimmed = query?.Trim() ?? string.Empty;
            return Candidates(
                trimmed.Length == 0 ? string.Empty : $"No Burst compile target matched '{trimmed}'.",
                targets,
                indexes,
                candidateCount
            );
        }

        static string Candidates(
            string header,
            IReadOnlyList<BurstTarget> targets,
            int[] indexes,
            int candidateCount)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header))
                builder.AppendLine(header);

            if (indexes.Length == 0)
                return builder.ToString().TrimEnd();

            builder.AppendLine("Candidates:");
            foreach (var index in indexes)
                builder.AppendLine($"- {targets[index].DisplayName}");

            if (candidateCount > indexes.Length)
            {
                builder.AppendLine();
                builder.AppendLine($"{candidateCount - indexes.Length} additional candidates were omitted.");
                builder.AppendLine("More specific target names return a narrower candidate set.");
            }

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
        public readonly int CandidateCount;

        BurstAsmTargetMatch(
            BurstAsmTargetMatchKind kind,
            int selectedIndex,
            int[] candidateIndexes,
            int candidateCount)
        {
            Kind = kind;
            SelectedIndex = selectedIndex;
            CandidateIndexes = candidateIndexes;
            CandidateCount = candidateCount;
        }

        public static BurstAsmTargetMatch Matched(int index) =>
            new(BurstAsmTargetMatchKind.Matched, index, Array.Empty<int>(), 0);

        public static BurstAsmTargetMatch Ambiguous(IReadOnlyList<int> indexes) =>
            new(BurstAsmTargetMatchKind.Ambiguous, -1, Copy(indexes), indexes.Count);

        public static BurstAsmTargetMatch None(IReadOnlyList<int> indexes, int? candidateCount = null) =>
            new(BurstAsmTargetMatchKind.None, -1, Copy(indexes), candidateCount ?? indexes.Count);

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

    readonly struct BurstCpuTarget
    {
        public readonly string Name;
        public readonly string CompilerTarget;
        public readonly string AsmKind;

        public BurstCpuTarget(string name, string compilerTarget, string asmKind)
        {
            Name = name;
            CompilerTarget = compilerTarget;
            AsmKind = asmKind;
        }
    }

    readonly struct BurstCompilationContext
    {
        public readonly string Cpu;
        public readonly string CompilerTarget;
        public readonly string Optimization;
        public readonly string FloatMode;
        public readonly string FloatPrecision;
        public readonly string SafetyChecks;

        public bool IsEmpty => string.IsNullOrEmpty(Cpu);

        public BurstCompilationContext(
            string cpu,
            string compilerTarget,
            string optimization,
            string floatMode,
            string floatPrecision,
            string safetyChecks)
        {
            Cpu = cpu ?? string.Empty;
            CompilerTarget = compilerTarget ?? string.Empty;
            Optimization = optimization ?? string.Empty;
            FloatMode = floatMode ?? string.Empty;
            FloatPrecision = floatPrecision ?? string.Empty;
            SafetyChecks = safetyChecks ?? string.Empty;
        }
    }

    enum BurstMemoryAccessKind : byte
    {
        None,
        Load,
        Store,
        ReadModifyWrite,
        Other,
    }

    enum BurstSimdRole : byte
    {
        None,
        Transfer,
        Lane,
        ScalarCompute,
        PackedCompute,
        Setup,
        Other,
    }

    readonly struct BurstAsmBranch
    {
        public readonly int InstructionIndex;
        public readonly string Mnemonic;
        public readonly string Target;
        public readonly string Source;

        public BurstAsmBranch(
            int instructionIndex,
            string mnemonic,
            string target,
            string source)
        {
            InstructionIndex = instructionIndex;
            Mnemonic = mnemonic;
            Target = target;
            Source = source;
        }
    }

    readonly struct BurstOptimizationRemark
    {
        public readonly string Type;
        public readonly string Message;
        public readonly string Pass;
        public readonly string Reason;
        public readonly string Source;

        public BurstOptimizationRemark(
            string type,
            string message,
            string pass,
            string reason,
            string source)
        {
            Type = type;
            Message = message;
            Pass = pass;
            Reason = reason;
            Source = source;
        }
    }

    sealed class BurstAsmStats
    {
        public readonly List<string> DirectCallTargets = new();
        public readonly List<string> EntryForwarders = new();
        public readonly List<BurstAsmBranch> BackwardBranches = new();
        public readonly List<BurstOptimizationRemark> OptimizationRemarks = new();
        public BurstCompilationContext Context;
        public string AnalyzedFunction = string.Empty;
        public string RemarksError = string.Empty;
        public int InstructionCount;
        public int ConditionalBranchCount;
        public int UnconditionalBranchCount;
        public int DirectCallCount;
        public int IndirectCallCount;
        public int ReturnCount;
        public int LoadInstructionCount;
        public int StoreInstructionCount;
        public int ReadModifyWriteInstructionCount;
        public int OtherMemoryInstructionCount;
        public int StackFrameMemoryInstructionCount;
        public int ConstantPoolMemoryInstructionCount;
        public int AddressGenerationInstructionCount;
        public int PushInstructionCount;
        public int PopInstructionCount;
        public int SimdTransferInstructionCount;
        public int SimdLaneInstructionCount;
        public int SimdScalarComputeInstructionCount;
        public int SimdPackedComputeInstructionCount;
        public int SimdSetupInstructionCount;
        public int SimdOtherInstructionCount;
        public int PackedComputeWidth;
        public bool PackedComputeUsesScalableVectors;
        public int XorInstructionCount;
        public int ZeroingXorInstructionCount;
        public int NumericMovabsCount;
        public int SymbolMovabsCount;
    }
}
