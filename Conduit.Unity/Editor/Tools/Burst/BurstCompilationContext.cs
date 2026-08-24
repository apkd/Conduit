#nullable enable

namespace Conduit
{
    readonly struct BurstCompilationContext
    {
        internal readonly string Cpu;
        internal readonly string CompilerTarget;
        internal readonly string Optimization;
        internal readonly string FloatMode;
        internal readonly string FloatPrecision;
        internal readonly string SafetyChecks;

        internal bool IsEmpty => string.IsNullOrEmpty(Cpu);

        internal BurstCompilationContext(
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
}
