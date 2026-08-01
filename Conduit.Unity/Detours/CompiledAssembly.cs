#nullable enable

using System;
using System.Reflection;

namespace Conduit
{
    static class CompiledAssembly
    {
        internal static Assembly Load(byte[] image, byte[]? symbols)
        {
            if (symbols == null)
                return Assembly.Load(image);

            try
            {
                return Assembly.Load(image, symbols);
            }
            catch (ArgumentException)
            {
                // some Unity Mono versions reject portable PDBs while accepting the same PE image.
                return Assembly.Load(image);
            }
        }
    }
}
