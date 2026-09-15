#nullable enable

using System;
using System.Reflection;

namespace Conduit
{
    /// <summary>Temporarily replaces a managed method until disposed or restored by Conduit.</summary>
    /// <remarks>
    /// <para>
    /// Available directly to Unity scripts without an MCP connection or the player bridge.
    /// Assembly definitions must reference <c>Conduit.Unity.Detours</c>.
    /// </para>
    /// <para>
    /// The replacement must be static and match the target's return type and parameters, including <c>ref</c>, <c>in</c>,
    /// and <c>out</c>. For instance targets, its first parameter must be the declaring type; pass struct
    /// receivers by <c>ref</c>. The detour affects every instance. Calling the target from inside its
    /// replacement calls the replacement again.
    /// </para>
    /// <para>
    /// Only one detour can own a method. Applying over an active detour throws; MCP can still update its own
    /// detours. Keep the handle and call <see cref="Dispose"/> when finished, usually through a <c>using</c>
    /// statement or the owning component's <c>OnDisable</c> method.
    /// </para>
    /// <para>
    /// These detours appear in Conduit's status. Conduit also restores them on explicit MCP restoration,
    /// before domain reload, on Play Mode exit, and on editor shutdown. Callers must recreate them after
    /// reload; they are not saved with MCP detour snapshots. Disposing an already-restored handle is harmless,
    /// even if another detour has since replaced the same method.
    /// </para>
    /// <para>
    /// Supports Unity Mono on Windows/Linux x64. Targets must have managed bodies in assemblies with file
    /// locations. Generic methods, generic declaring types, and some small JIT bodies are unsupported.
    /// Apply and dispose only while no other thread can invoke the target. Already-inlined calls can bypass
    /// a detour; mark targets <see cref="System.Runtime.CompilerServices.MethodImplOptions.NoInlining"/>
    /// where possible.
    /// </para>
    /// </remarks>
    /// <example>
    /// <para>This replacement receives the counter instance explicitly and doubles each addition until disposal.</para>
    /// <code>
    /// using Conduit;
    /// using System.Runtime.CompilerServices;
    ///
    /// namespace MyGame
    /// {
    ///     sealed class Counter
    ///     {
    ///         public int Value;
    ///
    ///         [MethodImpl(MethodImplOptions.NoInlining)]
    ///         public void Add(int amount) => Value += amount;
    ///     }
    ///
    ///     static class DebugPatches
    ///     {
    ///         public static void Add(Counter counter, int amount) => counter.Value += amount * 2;
    ///
    ///         public static void Example()
    ///         {
    ///             var counter = new Counter();
    ///             var target = typeof(Counter).GetMethod(nameof(Counter.Add))!;
    ///             var replacement = typeof(DebugPatches).GetMethod(nameof(DebugPatches.Add))!;
    ///             using (new MethodDetour(target, replacement))
    ///                 counter.Add(3); // adds six while the replacement is active
    ///             counter.Add(3); // adds three after the original method is restored
    ///         }
    ///     }
    /// }
    /// </code>
    /// <para>The handle in <c>Example</c> can also be created from types and method names.
    /// The optional <c>parameterTypes</c> argument selects a target overload:</para>
    /// <code>
    /// using var detour = new MethodDetour(
    ///     typeof(Counter), nameof(Counter.Add),
    ///     typeof(DebugPatches), nameof(DebugPatches.Add),
    ///     parameterTypes: new[] { typeof(int) });
    /// </code>
    /// <para>Or use fully qualified method names to find the types among loaded assemblies:</para>
    /// <code>
    /// using var detour = new MethodDetour("MyGame.Counter.Add", "MyGame.DebugPatches.Add");
    /// </code>
    /// </example>
    public sealed class MethodDetour : IDisposable
    {
        readonly DetourRuntime.ActiveDetour detour;

        /// <summary>Applies a detour and binds a delegate to the target's original managed body.</summary>
        /// <remarks>
        /// Pass the replacement's delegate field as <paramref name="original"/> so it is bound before patching.
        /// The delegate uses the replacement signature, including an explicit receiver for instance methods.
        /// It remains callable after disposal. Recursive calls inside the original still enter the detour.
        /// Cloning is opt-in and can reject IL that ordinary detours support. Call while the target is quiescent.
        /// </remarks>
        public static MethodDetour Create<TDelegate>(MethodInfo target, MethodInfo replacement, out TDelegate original)
            where TDelegate : Delegate
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            MethodDetourSupport.Validate(target);
            MethodDetourSupport.ValidateReplacement(target, replacement);
            original = (TDelegate)OriginalMethod.CreateDelegate(target, typeof(TDelegate));
            return new MethodDetour(target, replacement);
        }

        /// <summary>Applies a replacement to a method that has no active detour.</summary>
        /// <param name="target">The managed method whose implementation will be replaced.</param>
        /// <param name="replacement">A different static method with the matching signature and, for instance targets,
        /// an explicit first receiver parameter.</param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">The replacement is the target itself, is not static, or has an incompatible signature.</exception>
        /// <exception cref="NotSupportedException">The runtime, method, or native entry point cannot be detoured.</exception>
        /// <exception cref="InvalidOperationException">The target already has a detour or its native code changed.</exception>
        public MethodDetour(MethodInfo target, MethodInfo replacement)
            => detour = DetourRuntime.Apply(target, replacement);

        /// <summary>Finds methods by exact names, including nonpublic methods, and applies the replacement.</summary>
        /// <param name="targetType">The type containing the target method.</param>
        /// <param name="targetMethodName">The target's metadata name; explicit interface method names are supported.</param>
        /// <param name="replacementType">The type containing the static replacement.</param>
        /// <param name="replacementMethodName">The replacement's metadata name. Its parameter types are inferred from the target.</param>
        /// <param name="parameterTypes">Exact target parameter types for overload selection, excluding the instance receiver,
        /// or <see langword="null"/> to require a unique name. Use <see cref="Type.EmptyTypes"/> for a parameterless
        /// overload and <see cref="Type.MakeByRefType"/> for <c>ref</c>, <c>in</c>, or <c>out</c> parameters.</param>
        /// <exception cref="ArgumentException">A method name is empty or whitespace, or the replacement violates the signature rules.</exception>
        /// <exception cref="MissingMethodException">No matching method exists.</exception>
        /// <exception cref="AmbiguousMatchException">More than one method matches.</exception>
        public MethodDetour(
            Type targetType,
            string targetMethodName,
            Type replacementType,
            string replacementMethodName,
            Type[]? parameterTypes = null)
            : this(DetourRuntime.ResolveMethods(targetType, targetMethodName, replacementType, replacementMethodName, parameterTypes)) { }

        /// <summary>Finds methods by fully qualified names in loaded assemblies and applies the replacement.</summary>
        /// <param name="targetMethodName">A name such as <c>Namespace.Type.Method</c>. Use <c>+</c> between nested type names.
        /// Use the <see cref="MethodDetour(Type, string, Type, string, Type[])"/> overload for explicit interface method
        /// names or types with duplicate full names across assemblies.</param>
        /// <param name="replacementMethodName">The fully qualified name of the static replacement method.</param>
        /// <param name="parameterTypes">Exact target parameter types, excluding the receiver, or <see langword="null"/>
        /// to require a unique name. Use <see cref="Type.EmptyTypes"/> for a parameterless overload and
        /// <see cref="Type.MakeByRefType"/> for <c>ref</c>, <c>in</c>, or <c>out</c> parameters.</param>
        /// <exception cref="ArgumentException">A name is not fully qualified, or the replacement violates the signature rules.</exception>
        /// <exception cref="TypeLoadException">A named type is not loaded.</exception>
        /// <exception cref="MissingMethodException">No matching method exists.</exception>
        /// <exception cref="AmbiguousMatchException">A type name or method lookup is ambiguous.</exception>
        public MethodDetour(string targetMethodName, string replacementMethodName, Type[]? parameterTypes = null)
            : this(DetourRuntime.ResolveMethods(targetMethodName, replacementMethodName, parameterTypes)) { }

        MethodDetour((MethodInfo Target, MethodInfo Replacement) methods)
            : this(methods.Target, methods.Replacement) { }

        /// <summary>Restores this detour; repeated calls and calls after Conduit restored it have no effect.</summary>
        /// <remarks>A stale handle never restores a newer detour. If restoration fails, the detour remains tracked
        /// and disposal can be retried. Call only while no other thread can invoke the target.</remarks>
        /// <exception cref="InvalidOperationException">The target's native code changed outside Conduit.</exception>
        public void Dispose() => DetourRuntime.Restore(detour);
    }
}
