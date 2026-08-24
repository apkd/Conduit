#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Conduit
{
    static class ViewBurstAsmTool
    {
        static readonly Regex splitSourceFileDirective = new(@"^(?<prefix>\s*\.file\s+\d+\s+)""(?<directory>[^""]*)""\s+""(?<file>[^""]+)""(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

        internal static BridgeCommandResult ViewBurstAsm(string targetName, string cpu = "x86")
        {
#if MODULE_BURST
            try
            {
                if (!TryParseOutputTarget(cpu, out var outputTarget))
                    return BridgeCommandResult.Error($"Unknown Burst output target '{cpu}'. Expected x86, wasm32, armv8, armv9, cil, or llvmir.");

                var targets = LoadTargets();
                if (targets.Count == 0)
                    return BridgeCommandResult.Error("No Burst compile targets were found in the current editor domain.");

                var match = BurstTargetMatcher.MatchTarget(targetName, targets);
                return match.Kind switch
                {
                    BurstAsmTargetMatchKind.Matched   => Compile(targets[match.SelectedIndex], outputTarget),
                    BurstAsmTargetMatchKind.Ambiguous => BurstTargetMatcher.Ambiguous(targetName, targets, match),
                    _                                 => BurstTargetMatcher.NoMatch(targetName, targets, match),
                };
            }
            catch (Exception exception)
            {
                return BridgeCommandResult.Error(
                    $"Could not inspect Burst output: {BridgeExceptionFormatter.UnwrapTargetInvocationException(exception).Message}"
                );
            }
#else
            _ = targetName;
            _ = cpu;
            return BridgeCommandResult.Error("Burst is not installed or not available in this Unity project. Install com.unity.burst to use view_burst_asm.");
#endif
        }

#if MODULE_BURST
        // discovery scans every candidate assembly; a domain reload invalidates this assembly-bound target set.
        static List<BurstTarget>? cachedTargets;

        static List<BurstTarget> LoadTargets()
            => cachedTargets ??= DiscoverTargets();

        static List<BurstTarget> DiscoverTargets()
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

        static BridgeCommandResult Compile(BurstTarget target, BurstOutputTarget outputTarget)
        {
            DirtyBurstAssemblyCache();
            var options = BuildOptions(target, outputTarget);
            var rawOutput = GetInspectorDisassembly(target.Method!, options.Output);
            if (BurstOutputFormatter.IsBurstError(rawOutput))
                return BridgeCommandResult.Error(rawOutput.Trim());

            if (string.IsNullOrWhiteSpace(rawOutput))
                return BridgeCommandResult.Error(
                    BurstOutputFormatter.BuildEmptyOutputDiagnostic(target, outputTarget.DisplayName)
                );

            if (outputTarget.OutputKind != BurstOutputKind.Assembly)
            {
                var rawStats = new BurstAsmStats { Context = options.Context };
                if (outputTarget.OutputKind == BurstOutputKind.OptimizedLlvmIr)
                    ReadOptimizationRemarks(target, options.OptimizationRemarks, rawStats);

                return BurstOutputFormatter.CompleteRawOutput(target, rawOutput.Trim(), outputTarget, rawStats);
            }

            var disassembly = BurstOutputFormatter.StripTrailingTemporaryLabelBlocks(
                BurstOutputFormatter.CleanDisassembly(
                    RenderEnhancedDisassembly(rawOutput, outputTarget.AsmKind).TrimStart('\n')
                )
            );

            if (string.IsNullOrWhiteSpace(disassembly))
                return BridgeCommandResult.Error(BurstOutputFormatter.BuildEmptyDisassemblyDiagnostic(target));

            var stats = BurstAssemblyAnalyzer.Analyze(target, disassembly, options.Context.Cpu);
            stats.Context = options.Context;
            ReadOptimizationRemarks(target, options.OptimizationRemarks, stats);

            return BurstOutputFormatter.CompleteOutput(target, disassembly, stats);
        }

        static (string Output, string OptimizationRemarks, BurstCompilationContext Context) BuildOptions(
            BurstTarget target,
            BurstOutputTarget outputTarget)
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
            var output = BuildInspectorOptions(
                defaultOptions,
                outputTarget.CompilerTarget,
                outputTarget.DebugLevel,
                outputTarget.Dump
            );
            return new(
                output,
                BuildInspectorOptions(defaultOptions, outputTarget.CompilerTarget, "1", "IRPassAnalysis"),
                BurstCompilationParser.ParseCompilationContext(output, outputTarget.Name)
            );
        }

        static void ReadOptimizationRemarks(BurstTarget target, string options, BurstAsmStats stats)
        {
            // remarks use a separate Burst dump and may be unsupported even when the requested output succeeds.
            var rawRemarks = GetInspectorDisassembly(target.Method!, options);
            stats.RemarksError = BurstOutputFormatter.IsBurstError(rawRemarks)
                ? BurstOutputFormatter.FirstLine(BurstOutputFormatter.SimplifyBurstDiagnostic(rawRemarks))
                : string.Empty;
            if (stats.RemarksError.Length == 0)
                BurstCompilationParser.ParseOptimizationRemarks(rawRemarks, stats.OptimizationRemarks);
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
                return "Failed to compile:\n"
                       + BridgeExceptionFormatter.UnwrapTargetInvocationException(exception);
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
            disassembly = NormalizeSourceFileDirectives(disassembly);
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

        // Burst's renderer expects one quoted path and otherwise mistakes the directory for the filename.
        internal static string NormalizeSourceFileDirectives(string disassembly) =>
            splitSourceFileDirective.Replace(disassembly, static match =>
            {
                var directory = match.Groups["directory"].Value.TrimEnd('/', '\\');
                var file = match.Groups["file"].Value.TrimStart('/', '\\');
                var path = directory.Length == 0 ? file : directory + "/" + file;
                return $"{match.Groups["prefix"].Value}\"{path}\"{match.Groups["rest"].Value}";
            });

        internal static void ApplyInspectorOptionOverrides(object options)
        {
            SetBool(options, "EnableBurstSafetyChecks", false);
            SetBool(options, "ForceEnableBurstSafetyChecks", false);
            SetBool(options, "EnableBurstDebug", false);
        }

        static void SetBool(object target, string name, bool value)
        {
            var property = target.GetType()
                .GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                );
            if (property?.CanWrite == true)
                property.SetValue(target, value);
        }

        internal static bool TryParseOutputTarget(string? selector, out BurstOutputTarget target)
        {
            // explicit targets keep reports reproducible when the same project is inspected from different editor hosts.
            switch (selector?.Trim().ToLowerInvariant())
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
                case "cil":
                    target = new("cil", "Auto", BurstOutputKind.Cil);
                    return true;
                case "llvmir":
                    target = new("llvmir", "Auto", BurstOutputKind.OptimizedLlvmIr);
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
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.Append(defaultOptions.Trim());
            Append("--disable-warnings=BC1370;BC1322");
            AppendValue("--target=", compilerTarget);
            AppendValue("--debug=", debugLevel);
            AppendValue("--dump=", dump);
            return builder.ToString();

            void Append(string option)
            {
                if (builder.Length > 0)
                    builder.Append('\n');

                builder.Append(option);
            }

            void AppendValue(string prefix, string value)
            {
                if (builder.Length > 0)
                    builder.Append('\n');

                builder.Append(prefix).Append(value);
            }
        }

    }
}
