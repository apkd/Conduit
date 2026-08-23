#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.Search;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit
{
    enum ResolvedObjectMatchSource
    {
#if UNITY_6000_2_OR_NEWER
        EntityId,
#else
        InstanceId,
#endif
        EditorWindowQuery,
        AssetPath,
        HierarchyPath,
        SearchQuery,
    }

    static class ConduitSearchUtility
    {
        const int MaxResults = 25;
        static readonly string[] SearchProviderIds = { "asset", "scene" };
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

        public static List<ResolvedObjectMatch> Resolve(string query, int maxResults = MaxResults + 1)
            => Resolve(query, maxResults, includeAllSearchResults: false);

        internal static List<ResolvedObjectMatch> ResolveAll(string query)
            => Resolve(query, int.MaxValue, includeAllSearchResults: true);

        static List<ResolvedObjectMatch> Resolve(string query, int maxResults, bool includeAllSearchResults)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            if (normalizedQuery.Length == 0)
                return new();

            if (TryResolveDirect(normalizedQuery, maxResults, out var directMatches))
                return directMatches;

            return SearchByQuery(normalizedQuery, maxResults, includeAllSearchResults);
        }

        internal static bool TryResolveDirect(string query, int maxResults, out List<ResolvedObjectMatch> matches)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            matches = null!;
            if (normalizedQuery.Length == 0)
                return false;

            // direct selectors bypass Unity Search query parsing, so exact paths and IDs keep stable meaning.
            // ConduitSearch uses this path before adding generic type filters.
            if (IsEditorWindowQuery(normalizedQuery))
            {
                matches = ResolveEditorWindowQuery(normalizedQuery, maxResults);
                return true;
            }

            if (TryResolveObjectId(normalizedQuery, out var objectIdMatch, out var isObjectIdQuery))
            {
                matches = new() { objectIdMatch };
                return true;
            }

            if (isObjectIdQuery)
            {
                matches = new();
                return true;
            }

            if (TryResolveAssetPath(normalizedQuery, out var assetMatch))
            {
                matches = new() { assetMatch };
                return true;
            }

            if (LooksLikeHierarchyPath(normalizedQuery))
            {
                var hierarchyMatches = ResolveHierarchyPath(normalizedQuery, maxResults);
                if (hierarchyMatches.Count > 0)
                {
                    matches = hierarchyMatches;
                    return true;
                }
            }

            return false;
        }

        public static string Search(string query)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            if (TryParseTestSearch(normalizedQuery, out var testSearch))
                return SearchTests(normalizedQuery, testSearch);

            var matches = Resolve(normalizedQuery, MaxResults + 1);
            return matches.Count == 0
                ? FormatNoMatches(normalizedQuery)
                : FormatMatches(matches, includeHint: false);
        }

        public static string[] ResolveAssetPaths(string query)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            if (normalizedQuery.Length == 0)
                return Array.Empty<string>();

            var matches = Resolve(normalizedQuery, int.MaxValue);
            var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var match in matches)
                CollectImportableAssetPaths(match, assetPaths);

            return ConduitUtility.SortStrings(assetPaths, StringComparer.OrdinalIgnoreCase);
        }

        public static string FormatNoMatches(string query)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            return ShouldWarnAboutUnsupportedOrSyntax(normalizedQuery)
                ? "Unity search does not support OR operators. Run separate queries instead."
                : $"No matches for '{normalizedQuery}'.";
        }

        public static string FormatMatches(IReadOnlyList<ResolvedObjectMatch> matches, bool includeHint)
        {
            if (matches.Count > 0 && AreEditorWindowMatches(matches))
                return FormatEditorWindowMatches(matches, includeHint);

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            for (var index = 0; index < Math.Min(matches.Count, MaxResults); index++)
            {
                var match = matches[index];
                builder.Append("- ")
                    .Append(match.Name)
                    .Append(" | ")
                    .Append(match.Location)
                    .Append(" | ")
                    .AppendLine(ConduitUtility.FormatObjectId(match.ObjectId));
            }

            AppendTruncationNotice(builder, matches.Count);

#if UNITY_6000_2_OR_NEWER
            const string objectIdExample = "eid:<number>";
#else
            const string objectIdExample = "id:<number>";
#endif

            if (includeHint && matches.Count > 1)
            {
                builder.AppendLine();
                builder.AppendLine("Multiple objects match your query.");
                builder.Append($"Rerun with {objectIdExample} to select a specific match.");
            }

            return builder.TrimEnd().ToString();
        }

        internal static string FormatObjects(IReadOnlyList<Object> targets, bool includeHint)
        {
            using var pooledMatches = ConduitUtility.GetPooledList<ResolvedObjectMatch>(out var matches);
            // expanded component matches need the same candidate format as resolver matches.
            foreach (var target in targets)
                if (target != null)
                    matches.Add(CreateMatch(target, ResolvedObjectMatchSource.SearchQuery));

            return FormatMatches(matches, includeHint);
        }

        static bool TryResolveObjectId(string query, out ResolvedObjectMatch match, out bool isObjectIdQuery)
        {
            match = default;
            isObjectIdQuery = TryGetObjectIdValue(query, out var candidate);
            if (!isObjectIdQuery
                || candidate.IsEmpty
                || !BridgeObjectId.TryParse(candidate, out var objectId))
                return false;

            var target = ConduitUtility.ResolveObjectId(objectId);
            if (target == null)
                return false;

#if UNITY_6000_2_OR_NEWER
            match = CreateMatch(target, ResolvedObjectMatchSource.EntityId);
#else
            match = CreateMatch(target, ResolvedObjectMatchSource.InstanceId);
#endif
            return true;
        }

        static bool TryResolveAssetPath(string query, out ResolvedObjectMatch match)
        {
            match = default;
            if (!ConduitAssetPathUtility.TryResolveAssetPath(query, out var assetPath))
                return false;

            var target = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (target == null)
                return false;

            match = CreateMatch(target, ResolvedObjectMatchSource.AssetPath);
            return true;
        }

        static List<ResolvedObjectMatch> ResolveEditorWindowQuery(string query, int maxResults)
        {
            var windowQuery = query["window:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(windowQuery))
                return new();

            var openMatches = FindOpenEditorWindowMatches(windowQuery, maxResults);
            if (openMatches.Count > 0)
                return openMatches;

            var typeMatches = FindEditorWindowTypeMatches(windowQuery, maxResults);
            if (typeMatches.Count == 1)
                return OpenEditorWindowTypeMatch(typeMatches[0]);

            return typeMatches;
        }

        static List<ResolvedObjectMatch> ResolveHierarchyPath(string query, int maxResults)
        {
            var normalizedPath = NormalizeHierarchyPath(query);
            var matches = new List<ResolvedObjectMatch>(Math.Min(maxResults, 4));
            using var pooledRoots = ConduitUtility.GetPooledList<GameObject>(out var roots);
            using var pooledPending = ConduitUtility.GetPooledList<(Transform Transform, int PathOffset)>(out var pending);
            var sceneCount = SceneManager.sceneCount;
            for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;

                roots.Clear();
                scene.GetRootGameObjects(roots);
                foreach (var root in roots)
                {
                    pending.Clear();
                    pending.Add((root.transform, 0));
                    while (pending.Count > 0)
                    {
                        var lastIndex = pending.Count - 1;
                        var (transform, pathOffset) = pending[lastIndex];
                        pending.RemoveAt(lastIndex);

                        // compare raw name prefixes because Unity object names may contain path separators.
                        var name = transform.name;
                        if (normalizedPath.Length - pathOffset < name.Length
                            || string.CompareOrdinal(normalizedPath, pathOffset, name, 0, name.Length) != 0)
                            continue;

                        var nextOffset = pathOffset + name.Length;
                        if (nextOffset == normalizedPath.Length)
                        {
                            matches.Add(CreateMatch(transform.gameObject, ResolvedObjectMatchSource.HierarchyPath));
                            if (matches.Count >= maxResults)
                                return Deduplicate(matches, maxResults);
                            continue;
                        }

                        if (normalizedPath[nextOffset] != '/')
                            continue;

                        ++nextOffset;
                        for (var childIndex = transform.childCount - 1; childIndex >= 0; --childIndex)
                            pending.Add((transform.GetChild(childIndex), nextOffset));
                    }

                    if (matches.Count >= maxResults)
                        return Deduplicate(matches, maxResults);
                }
            }

            return Deduplicate(matches, maxResults);
        }

        static List<ResolvedObjectMatch> SearchByQuery(string query, int maxResults, bool includeAllSearchResults)
        {
            // Unity Search applies provider limits by default; array-returning APIs need the complete set.
            var searchQuery = includeAllSearchResults ? AddNoResultsLimit(query) : query;
            using var context = SearchService.CreateContext(
                SearchProviderIds,
                searchQuery,
                SearchFlags.Synchronous
            );

            var items = SearchService.GetItems(
                context,
                SearchFlags.Synchronous
            );

            var matches = new List<ResolvedObjectMatch>(
                maxResults == int.MaxValue ? MaxResults : Math.Min(maxResults, MaxResults + 1)
            );
            using var pooledObjectIds = ConduitUtility.GetPooledSet<ulong>(out var objectIds);

            foreach (var item in items)
            {
                var target = item.ToObject();
                if (target == null)
                    continue;

                var match = CreateMatch(target, ResolvedObjectMatchSource.SearchQuery);
                if (!objectIds.Add(match.ObjectId))
                    continue;

                matches.Add(match);
                if (matches.Count >= maxResults)
                    break;
            }

            SortMatches(matches);
            return matches;
        }

        static string AddNoResultsLimit(string query)
        {
            if (query.IndexOf("+noResultsLimit", StringComparison.OrdinalIgnoreCase) >= 0)
                return query;

            // keep user-authored query text intact and append the provider directive as a separate token.
            return string.IsNullOrWhiteSpace(query)
                ? "+noResultsLimit"
                : query + " +noResultsLimit";
        }

        internal static bool TryParseTestSearch(string query, out TestSearchCriteria criteria)
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
            using var pooledTokens = ConduitUtility.GetPooledList<string>(out var filterTokens);
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

        static string SearchTests(string query, TestSearchCriteria criteria)
        {
            if (criteria.Mode == TestSearchMode.None)
                return FormatNoMatches(query);

            var matches = DiscoverTests(criteria);
            return matches.Count == 0
                ? FormatNoMatches(query)
                : FormatTestMatches(matches);
        }

        static bool ShouldWarnAboutUnsupportedOrSyntax(string query)
            => !string.IsNullOrWhiteSpace(query)
               && !IsEditorWindowQuery(query)
               && !query.StartsWith("/", StringComparison.Ordinal)
               && !query.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
               && !query.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
               && !TryGetObjectIdValue(query, out _)
               && !TryParseTestSearch(query, out _)
               && ContainsUnsupportedOrSyntax(query);

        static bool ContainsUnsupportedOrSyntax(string query)
            => query.Contains("||", StringComparison.Ordinal) || query.Contains(" OR ", StringComparison.Ordinal);

        static List<DiscoveredTestMatch> DiscoverTests(TestSearchCriteria criteria)
        {
            var matches = new List<DiscoveredTestMatch>();
            using var pooledSeen = ConduitUtility.GetPooledSet<string>(out var seen);

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
                    foreach (var method in reflect.GetMethods(type, includeAccessors: true))
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
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            foreach (var match in matches)
                builder.Append("- ")
                    .Append(match.Name)
                    .Append(" | ")
                    .AppendLine(FormatModeLabel(match.Mode));

            return builder.TrimEnd().ToString();
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

        static bool IsEditorWindowQuery(string query)
            => query.StartsWith("window:", StringComparison.OrdinalIgnoreCase);

        static List<ResolvedObjectMatch> FindOpenEditorWindowMatches(string query, int maxResults)
        {
            var matches = new List<ResolvedObjectMatch>();
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null || !MatchesEditorWindowQuery(window, query))
                    continue;

                matches.Add(CreateMatch(window, ResolvedObjectMatchSource.EditorWindowQuery));
                if (matches.Count >= maxResults)
                    break;
            }

            return Deduplicate(matches, maxResults);
        }

        static List<ResolvedObjectMatch> FindEditorWindowTypeMatches(string query, int maxResults)
        {
            var matches = new List<ResolvedObjectMatch>();
            foreach (var windowType in TypeCache.GetTypesDerivedFrom<EditorWindow>())
            {
                if (!windowType.IsClass || windowType.IsAbstract || windowType.ContainsGenericParameters)
                    continue;

                if (!ContainsIgnoreCase(windowType.Name, query))
                    continue;

                matches.Add(
                    new()
                    {
                        Name = windowType.Name,
                        Location = "EditorWindow type",
                        Source = ResolvedObjectMatchSource.EditorWindowQuery,
                    }
                );

                if (matches.Count >= maxResults)
                    break;
            }

            matches.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            return matches;
        }

        static List<ResolvedObjectMatch> OpenEditorWindowTypeMatch(ResolvedObjectMatch typeMatch)
        {
            Type? windowType = null;
            foreach (var candidate in TypeCache.GetTypesDerivedFrom<EditorWindow>())
            {
                if (!string.Equals(candidate.Name, typeMatch.Name, StringComparison.Ordinal))
                    continue;

                windowType = candidate;
                break;
            }

            if (windowType == null)
                return new();

            try
            {
                var window = ConduitEditorWindowDocking.CreateDockedTab(windowType);
                return new() { CreateMatch(window, ResolvedObjectMatchSource.EditorWindowQuery) };
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Could not open editor window type '{windowType.Name}': {exception.Message}");
            }
        }

        internal static string GetEditorWindowDisplayName(EditorWindow window)
        {
            var title = GetEditorWindowTitle(window);
            return string.IsNullOrWhiteSpace(title) ? window.GetType().Name : title;
        }

        internal static string GetEditorWindowTitle(EditorWindow window)
            => window.titleContent?.text?.Trim() ?? string.Empty;

        static bool MatchesEditorWindowQuery(EditorWindow window, string query)
            => ContainsIgnoreCase(GetEditorWindowTitle(window), query)
               || ContainsIgnoreCase(window.GetType().Name, query);

        static bool ContainsIgnoreCase(string value, string query)
            => !string.IsNullOrEmpty(value)
               && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        static bool AreEditorWindowMatches(IReadOnlyList<ResolvedObjectMatch> matches)
        {
            foreach (var match in matches)
                if (match.Source != ResolvedObjectMatchSource.EditorWindowQuery)
                    return false;

            return true;
        }

        static string FormatEditorWindowMatches(IReadOnlyList<ResolvedObjectMatch> matches, bool includeHint)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            for (var index = 0; index < Math.Min(matches.Count, MaxResults); index++)
            {
                var match = matches[index];
                builder.Append("- ")
                    .Append(match.Name)
                    .Append(" | ")
                    .Append(match.Location);
                if (match.ObjectId != 0)
                    builder.Append(" | ").Append(ConduitUtility.FormatObjectId(match.ObjectId));
                builder.AppendLine();
            }

            AppendTruncationNotice(builder, matches.Count);

            if (includeHint && matches.Count > 1)
            {
                builder.AppendLine();
                builder.AppendLine("Multiple editor windows match your query.");
                builder.Append("Rerun with a more specific window title or type name.");
            }

            return builder.TrimEnd().ToString();
        }

        static void AppendTruncationNotice(StringBuilder builder, int resultCount)
        {
            if (resultCount <= MaxResults)
                return;

            builder.AppendLine();
            builder.AppendLine($"Showing the first {MaxResults} results; additional matches were omitted.");
            builder.AppendLine("More specific queries return a narrower result set.");
        }

        static void CollectImportableAssetPaths(ResolvedObjectMatch match, HashSet<string> assetPaths)
        {
            if (match.AssetPath is not { Length: > 0 } assetPath)
                return;

            CollectImportableAssetPaths(assetPath, assetPaths);
        }

        static void CollectImportableAssetPaths(string assetPath, HashSet<string> assetPaths)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            if (!AssetDatabase.IsValidFolder(assetPath))
            {
                if (IsImportableAssetPath(assetPath))
                    assetPaths.Add(assetPath);

                return;
            }

            var addedChildren = false;
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { assetPath }))
            {
                var childAssetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsImportableAssetPath(childAssetPath) || AssetDatabase.IsValidFolder(childAssetPath))
                    continue;

                assetPaths.Add(childAssetPath);
                addedChildren = true;
            }

            if (!addedChildren && IsImportableAssetPath(assetPath))
                assetPaths.Add(assetPath);
        }

        static bool IsImportableAssetPath(string assetPath)
            => !string.IsNullOrWhiteSpace(assetPath)
               && !assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
               && (assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                   || assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                   || assetPath.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                   || assetPath.Equals("Packages", StringComparison.OrdinalIgnoreCase));

        static List<ResolvedObjectMatch> Deduplicate(List<ResolvedObjectMatch> matches, int maxResults)
        {
            using var pooledObjectIds = ConduitUtility.GetPooledSet<ulong>(out var objectIds);
            var uniqueCount = 0;
            for (var readIndex = 0; readIndex < matches.Count; readIndex++)
            {
                var match = matches[readIndex];
                if (!objectIds.Add(match.ObjectId))
                    continue;

                matches[uniqueCount++] = match;
                if (uniqueCount >= maxResults)
                    break;
            }

            if (uniqueCount < matches.Count)
                matches.RemoveRange(uniqueCount, matches.Count - uniqueCount);

            SortMatches(matches);
            return matches;
        }

        static void SortMatches(List<ResolvedObjectMatch> matches)
            => matches.Sort(static (left, right)
                    =>
                {
                    var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
                    return nameComparison != 0
                        ? nameComparison
                        : StringComparer.OrdinalIgnoreCase.Compare(left.Location, right.Location);
                }
            );

        static ResolvedObjectMatch CreateMatch(Object target, ResolvedObjectMatchSource source)
        {
            var assetPath = EditorUtility.IsPersistent(target)
                ? AssetDatabase.GetAssetPath(target)
                : string.Empty;
            return new()
            {
                Target = target,
                Name = target is EditorWindow window
                    ? GetEditorWindowDisplayName(window)
                    : target.name,
                Location = GetLocation(target, assetPath),
                AssetPath = assetPath,
                ObjectId = ConduitUtility.GetObjectId(target),
                Source = source,
            };
        }

        static string GetLocation(Object target, string assetPath)
        {
            if (!string.IsNullOrWhiteSpace(assetPath))
                return assetPath;

            return target switch
            {
                EditorWindow window => $"EditorWindow:{GetEditorWindowDisplayName(window)} ({window.GetType().Name})",
                GameObject gameObject => FormatSceneLocation(gameObject.scene, ConduitUtility.BuildHierarchyPath(gameObject.transform)),
                Component component   => FormatSceneLocation(component.gameObject.scene, ConduitUtility.BuildHierarchyPath(component.transform)),
                _                     => target.GetType().Name,
            };
        }

        static string FormatSceneLocation(Scene scene, string hierarchyPath)
            => $"{ConduitUtility.FormatScenePath(scene, "unsaved scene")}:{hierarchyPath}";

        static bool LooksLikeHierarchyPath(string query)
            => query.StartsWith("/", StringComparison.Ordinal)
               || query.IndexOf('/') >= 0
               || query.IndexOf('\\') >= 0;

        static string NormalizeHierarchyPath(string query)
        {
            var start = 0;
            var end = query.Length;
            while (start < end && char.IsWhiteSpace(query[start]))
                start++;
            while (end > start && char.IsWhiteSpace(query[end - 1]))
                end--;
            while (start < end && query[start] == '/')
                start++;

            var hasBackslashes = false;
            for (var index = start; index < end; index++)
            {
                if (query[index] != '\\')
                    continue;

                hasBackslashes = true;
                break;
            }

            if (!hasBackslashes)
                return start == 0 && end == query.Length
                    ? query
                    : query.Substring(start, end - start);

            return string.Create(
                end - start,
                (query, start),
                static (result, state) =>
                {
                    for (var index = 0; index < result.Length; index++)
                    {
                        var character = state.query[state.start + index];
                        result[index] = character == '\\' ? '/' : character;
                    }
                }
            );
        }

        static bool TryGetObjectIdValue(string query, out ReadOnlySpan<char> value)
        {
            var querySpan = query.AsSpan();
            if (TryGetObjectIdValue(querySpan, "eid:", out value)
                || TryGetObjectIdValue(querySpan, "entity:", out value)
                || TryGetObjectIdValue(querySpan, "id:", out value))
                return true;

            value = default;
            return false;
        }

        static bool TryGetObjectIdValue(ReadOnlySpan<char> querySpan, string prefix, out ReadOnlySpan<char> value)
        {
            if (!querySpan.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = default;
                return false;
            }

            value = querySpan[prefix.Length..].TrimStart();
            return true;
        }
    }

    struct ResolvedObjectMatch
    {
        public Object Target;
        public string Name;
        public string Location;
        public string? AssetPath;
        public ulong ObjectId;
        public ResolvedObjectMatchSource Source;
    }

    enum TestSearchMode
    {
        Any,
        EditMode,
        PlayMode,
        None,
    }

    readonly struct TestSearchCriteria
    {
        readonly string[] nameTokens;

        public TestSearchCriteria(TestSearchMode mode, string[] nameTokens)
        {
            Mode = mode;
            this.nameTokens = nameTokens;
        }

        public TestSearchMode Mode { get; }

        public bool MatchesMode(TestSearchMode candidateMode)
            => Mode == TestSearchMode.Any || Mode == candidateMode;

        public bool MatchesName(string displayName)
        {
            foreach (var nameToken in nameTokens)
                if (displayName.IndexOf(nameToken, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

            return true;
        }
    }

    readonly struct DiscoveredTestMatch
    {
        public DiscoveredTestMatch(string name, TestSearchMode mode)
        {
            Name = name;
            Mode = mode;
        }

        public string Name { get; }
        public TestSearchMode Mode { get; }
    }
}
