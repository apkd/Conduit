#nullable enable

using System;
using System.Reflection;

namespace Conduit
{
    static unsafe class MonoAssemblyAccess
    {
        static readonly bool assemblyNameHasArchitecture = HasAssemblyNameArchitecture();

        internal static void EnablePrivateAccess(Assembly assembly)
        {
            if (Type.GetType("Mono.Runtime") == null)
                throw new PlatformNotSupportedException("Runtime method detouring requires the Unity Mono runtime.");

            var runtimeType = assembly.GetType();
            var field = runtimeType.GetField(
                            "_mono_assembly",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                        )
                        ?? runtimeType.GetField(
                            "dynamic_assembly",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                        )
                        ?? throw new InvalidOperationException("The Unity Mono assembly handle field was not found.");
            var pointer = field.GetValue(assembly) switch
            {
                IntPtr value => value.ToInt64(),
                UIntPtr value => unchecked((long)value.ToUInt64()),
                _ => 0L,
            };
            if (pointer == 0)
                throw new InvalidOperationException("The generated Mono assembly has no native handle.");

            // mono grants unrestricted visibility to assemblies carrying its corlib_internal flag.
            // unity ships both legacy mscorlib and core-BCL MonoAssembly layouts; AssemblyName's
            // architecture field distinguishes their native MonoAssemblyName variants.
            bool coreBcl = typeof(object).Assembly.GetName().Name != "mscorlib";
            int versionFieldsSize = (assemblyNameHasArchitecture, coreBcl, IntPtr.Size) switch
            {
                (false, false, _) => 8,
                (false, true, _) => 16,
                (true, false, 4) => 12,
                (true, false, _) => 16,
                (true, true, 4) => 20,
                _ => 24,
            };
            int offset = IntPtr.Size * 6
                         + 20
                         + sizeof(uint) * 3
                         + versionFieldsSize
                         + IntPtr.Size * 2
                         + 3;
            *((byte*)pointer + offset) = 1;
        }

        static bool HasAssemblyNameArchitecture()
        {
#pragma warning disable 618
            return new AssemblyName("ConduitProbe, ProcessorArchitecture=MSIL").ProcessorArchitecture
                   == ProcessorArchitecture.MSIL;
#pragma warning restore 618
        }
    }
}
