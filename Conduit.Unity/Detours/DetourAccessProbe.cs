namespace Conduit
{
    /// <summary>Checks that a generated detour assembly can access private members before it is installed.</summary>
    /// <remarks>
    /// MCP replacements may refer to private members of the target type. Compiling those references does
    /// not grant access when Mono executes them: MonoAssemblyAccess must first enable access on the loaded
    /// replacement assembly. The server emits an AccessProbe method that reads this private property.
    /// DetourRuntime invokes that method and checks its result before redirecting any native method entry.
    /// This catches broken access handling, including changes to Mono's internal assembly layout, while
    /// the original target is still intact. The property must stay private so the probe exercises access
    /// checks; using the constant directly would let the compiler inline the answer without checking access.
    /// </remarks>
    static class DetourAccessProbe
    {
        internal const int ExpectedValue = 0x43d72a1;
        static int Value => ExpectedValue;
    }
}
