#nullable enable

#if UNITY_EDITOR
using System;
using JetBrains.Annotations;

namespace Conduit
{
    [UsedImplicitly]
    public static class CI
    {
        public static void RunTests()
            => CiTestRunner.RunAll();

        public static void RunFilteredEditModeTestsFromCommandLine()
            => CiTestRunner.RunFilteredEditMode();

        /// <summary>Builds the development Mono player used by transport E2E jobs.</summary>
        public static void BuildPlayer()
            => CiPlayerBuilder.Build(includeRuntime: true);

        /// <summary>Builds a production Mono player and verifies that the opt-in runtime bridge is excluded.</summary>
        public static void BuildConsumerPlayer()
            => CiPlayerBuilder.Build(includeRuntime: false);

        internal static string? ResolveCommandLineValue(string argumentName)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index < arguments.Length - 1; index++)
            {
                if (arguments[index] != argumentName)
                    continue;

                var value = arguments[index + 1].Trim();
                return value.Length == 0 ? null : value;
            }

            return null;
        }
    }
}
#endif
