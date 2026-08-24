#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor.Compilation;

namespace Conduit
{
    static class UnityTestSearch
    {
        const int MaxResults = 25;
        static readonly string[] TestMethodAttributeNames =
        {
            "NUnit.Framework.TestAttribute",
            "NUnit.Framework.TestCaseAttribute",
            "NUnit.Framework.TestCaseSourceAttribute",
            "NUnit.Framework.TheoryAttribute",
            "UnityEngine.TestTools.UnityTestAttribute",
        };
        static readonly ConcurrentDictionary<System.Reflection.Assembly, string[]> testNameCache = new();
        static readonly object testAssemblyCacheGate = new();
        // script compilation reloads this assembly, so the cache lifetime matches the loaded test assemblies.
        static (System.Reflection.Assembly Assembly, TestSearchMode Mode)[]? cachedTestAssemblies;
        internal static bool TryParse(string query, out TestSearchCriteria criteria)
        {
            criteria = default;
            if (string.IsNullOrWhiteSpace(query))
                return false;

            if (query.Equals("test", StringComparison.OrdinalIgnoreCase)
                || query.Equals("tests", StringComparison.OrdinalIgnoreCase))
            {
                criteria = new(TestSearchMode.Any, Array.Empty<string>());
                return true;
            }

            var hasDirective = false;
            var mode = TestSearchMode.Any;
            using var pooledTokens = ConduitPool.GetPooledList<string>(out var filterTokens);
            var offset = 0;
            while (offset < query.Length)
            {
                while (offset < query.Length && char.IsWhiteSpace(query[offset]))
                    ++offset;
                if (offset == query.Length)
                    break;

                var start = offset;
                while (offset < query.Length && !char.IsWhiteSpace(query[offset]))
                    ++offset;
                var token = query.AsSpan(start, offset - start);
                if (token.Equals("t:test".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    hasDirective = true;
                    continue;
                }

                if (token.Equals("editmode".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    mode = mode switch
                    {
                        TestSearchMode.PlayMode => TestSearchMode.None,
                        TestSearchMode.None     => TestSearchMode.None,
                        _                       => TestSearchMode.EditMode,
                    };
                    continue;
                }

                if (token.Equals("playmode".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    mode = mode switch
                    {
                        TestSearchMode.EditMode => TestSearchMode.None,
                        TestSearchMode.None     => TestSearchMode.None,
                        _                       => TestSearchMode.PlayMode,
                    };
                    continue;
                }

                filterTokens.Add(query.Substring(start, offset - start));
            }

            if (!hasDirective)
                return false;

            criteria = new(
                mode,
                filterTokens.Count == 0 ? Array.Empty<string>() : filterTokens.ToArray()
            );
            return true;
        }

        internal static string Search(string query, TestSearchCriteria criteria)
        {
            if (criteria.Mode == TestSearchMode.None)
                return ConduitSearchUtility.FormatNoMatches(query);

            var matches = DiscoverTests(criteria);
            return matches.Count == 0
                ? ConduitSearchUtility.FormatNoMatches(query)
                : FormatTestMatches(matches);
        }

        static List<DiscoveredTestMatch> DiscoverTests(TestSearchCriteria criteria)
        {
            var matches = new List<DiscoveredTestMatch>();
            using var pooledSeen = ConduitPool.GetPooledSet<string>(out var seen);

            foreach (var assembly in GetTestAssemblies())
            {
                if (!criteria.MatchesMode(assembly.Mode))
                    continue;

                foreach (var discoveredTest in DiscoverAssemblyTests(
                             assembly.Assembly,
                             assembly.Mode,
                             criteria
                         ))
                {
                    if (!seen.Add(discoveredTest.Name))
                        continue;

                    matches.Add(discoveredTest);
                    if (matches.Count >= MaxResults)
                        break;
                }

                if (matches.Count >= MaxResults)
                    break;
            }

            matches.Sort(static (left, right) =>
                {
                    var modeComparison = left.Mode.CompareTo(right.Mode);
                    return modeComparison != 0
                        ? modeComparison
                        : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
                }
            );
            return matches;
        }

        static IEnumerable<DiscoveredTestMatch> DiscoverAssemblyTests(
            System.Reflection.Assembly runtimeAssembly,
            TestSearchMode mode,
            TestSearchCriteria criteria)
        {
            var testNames = testNameCache.GetOrAdd(runtimeAssembly, static assembly =>
            {
                var names = new List<string>();
                foreach (var type in GetLoadableTypes(assembly))
                    foreach (var method in ReflectionQueryEngine.GetMethods(type, includeAccessors: true))
                        if (HasTestAttribute(method))
                            names.Add($"{FormatTypeName(type)}.{method.Name}");

                return names.ToArray();
            });

            foreach (var displayName in testNames)
            {
                if (!criteria.MatchesName(displayName))
                    continue;

                yield return new(displayName, mode);
            }
        }

        static string FormatTestMatches(IReadOnlyList<DiscoveredTestMatch> matches)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            foreach (var match in matches)
                builder.Append("- ")
                    .Append(match.Name)
                    .Append(" | ")
                    .AppendLine(FormatModeLabel(match.Mode));

            return builder.ToTrimmedString();
        }

        static string FormatModeLabel(TestSearchMode mode)
            => mode switch
            {
                TestSearchMode.EditMode => "EditMode",
                TestSearchMode.PlayMode => "PlayMode",
                _                       => "Test",
            };

        static bool TryGetTestAssemblyMode(UnityEditor.Compilation.Assembly assembly, out TestSearchMode mode)
        {
            mode = default;
            var hasTestReference = false;
            foreach (var reference in assembly.assemblyReferences)
                if (reference.name is "UnityEngine.TestRunner" or "UnityEditor.TestRunner")
                {
                    hasTestReference = true;
                    break;
                }
            if (!hasTestReference)
                return false;

            var hasProjectSource = false;
            foreach (var sourceFile in assembly.sourceFiles)
                if (IsDiscoverableProjectSourceFile(sourceFile))
                {
                    hasProjectSource = true;
                    break;
                }
            if (!hasProjectSource)
                return false;

            mode = (assembly.flags & UnityEditor.Compilation.AssemblyFlags.EditorAssembly) != 0
                ? TestSearchMode.EditMode
                : TestSearchMode.PlayMode;
            return true;
        }

        internal static bool IsDiscoverableProjectSourceFile(string sourceFile)
        {
            sourceFile = sourceFile.Replace('\\', '/');
            if (sourceFile.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!sourceFile.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return false;

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(sourceFile);
            return package == null
                   || package.source is not UnityEditor.PackageManager.PackageSource.BuiltIn
                       and not UnityEditor.PackageManager.PackageSource.Registry;
        }

        static IReadOnlyList<(System.Reflection.Assembly Assembly, TestSearchMode Mode)> GetTestAssemblies()
        {
            lock (testAssemblyCacheGate)
            {
                if (cachedTestAssemblies != null)
                    return cachedTestAssemblies;

                var runtimeAssemblies = new Dictionary<string, System.Reflection.Assembly>(StringComparer.Ordinal);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetName().Name is { Length: > 0 } name)
                        runtimeAssemblies.TryAdd(name, assembly);

                var tests = new List<(System.Reflection.Assembly, TestSearchMode)>();
                foreach (var assembly in CompilationPipeline.GetAssemblies())
                    if (TryGetTestAssemblyMode(assembly, out var mode)
                        && runtimeAssemblies.TryGetValue(assembly.name, out var runtimeAssembly))
                        tests.Add((runtimeAssembly, mode));

                return cachedTestAssemblies = tests.ToArray();
            }
        }

        static IEnumerable<Type> GetLoadableTypes(System.Reflection.Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                var types = new List<Type>(exception.Types.Length);
                foreach (var type in exception.Types)
                {
                    if (type == null)
                        continue;

                    types.Add(type);
                }

                return types;
            }
        }

        static bool HasTestAttribute(MethodInfo method)
        {
            foreach (var attribute in CustomAttributeData.GetCustomAttributes(method))
            {
                var attributeName = attribute.AttributeType.FullName;
                if (string.IsNullOrWhiteSpace(attributeName))
                    continue;

                foreach (var testMethodAttributeName in TestMethodAttributeNames)
                    if (attributeName == testMethodAttributeName)
                        return true;
            }

            return false;
        }

        static string FormatTypeName(Type type)
            => (type.FullName ?? type.Name).Replace('+', '.');

    }
}
