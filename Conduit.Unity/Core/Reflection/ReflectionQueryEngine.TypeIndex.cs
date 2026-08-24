#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ReflectionQueryEngine
    {
        static IReadOnlyList<Type> LoadIndex(out string warning)
        {
            lock (IndexLock)
            {
                if (cachedIndex != null)
                {
                    // generated snippets are folded into the sorted index only when reflection needs them.
                    ApplyPendingLoadedTypes();
                    warning = cachedLoadWarning;
                    return cachedIndex;
                }

                cachedIndex = BuildIndex(out cachedLoadWarning);
                warning = cachedLoadWarning;
                return cachedIndex;
            }
        }

        internal static IReadOnlyList<Type> LoadIndexForHelpers()
            => LoadIndex(out _);

        internal static int GetParallelScanWorkerCount(int entryCount)
            => Math.Min(
                Math.Min(Environment.ProcessorCount, MaxParallelScanWorkers),
                Math.Max(1, entryCount / ParallelScanEntriesPerWorker)
            );

        static TypeIndex BuildIndex(out string warning)
        {
            var types = new List<Type>();
            using var pooledWarnings = BridgeStringBuilderPool.Rent(out var warnings);
            var assemblies = new HashSet<Assembly>();
            while (true)
            {
                var addedAssembly = false;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!assemblies.Add(assembly))
                        continue;

                    addedAssembly = true;
                    AppendAssemblyTypes(types, assembly, warnings);
                }

                // GetTypes can load dependencies reentrantly; include them before publishing the index.
                if (!addedAssembly)
                    break;
            }

            warning = warnings.ToTrimmedString();
            return SortIndex(types);
        }

        static void AppendAssemblyTypes(List<Type> types, Assembly assembly, StringBuilder warnings)
        {
            try
            {
                types.AddRange(assembly.GetTypes());
            }
            catch (ReflectionTypeLoadException exception)
            {
                foreach (var type in exception.Types)
                    if (type != null)
                        types.Add(type);

                AppendLoadFailure(exception);
            }
            catch (Exception exception)
            {
                AppendLoadFailure(exception);
            }

            void AppendLoadFailure(Exception exception)
            {
                if (warnings.Length == 0)
                    warnings.AppendLine("Warning: some loaded assemblies could not be fully reflected.");
                else if (warnings[^1] != '\n')
                    warnings.AppendLine();

                warnings.Append("- ")
                    .Append(assembly.GetName().Name)
                    .Append(": ")
                    .AppendLine(exception.Message);
            }
        }

        static void SortTypes(List<Type> types) => types.Sort(CompareTypes);

        static TypeIndex SortIndex(List<Type> types)
        {
            var entries = new TypeSortEntry[types.Count];
            for (var index = 0; index < types.Count; index++)
            {
                var type = types[index];
                var info = CreateTypeSearchInfo(type);
                entries[index] = new(
                    type,
                    info
                );
            }

            // precomputed keys avoid repeated Mono reflection and cache lookups in the O(n log n) sort.
            Array.Sort(entries, static (left, right) =>
            {
                var assembly = string.Compare(
                    left.SearchInfo.AssemblyName,
                    right.SearchInfo.AssemblyName,
                    StringComparison.Ordinal
                );
                return assembly != 0
                    ? assembly
                    : string.Compare(
                        left.SearchInfo.FullName,
                        right.SearchInfo.FullName,
                        StringComparison.Ordinal
                    );
            });

            var sortedTypes = new Type[entries.Length];
            var searchInfos = new TypeSearchInfo[entries.Length];
            for (var index = 0; index < entries.Length; index++)
            {
                sortedTypes[index] = entries[index].Type;
                searchInfos[index] = entries[index].SearchInfo;
            }

            return new(sortedTypes, searchInfos);
        }

        static TypeIndex MergeTypes(TypeIndex existing, IReadOnlyList<Type> added)
        {
            var mergedTypes = new Type[existing.Count + added.Count];
            var mergedSearchInfos = new TypeSearchInfo[mergedTypes.Length];
            var addedSearchInfos = new TypeSearchInfo[added.Count];
            for (var index = 0; index < added.Count; index++)
                addedSearchInfos[index] = CreateTypeSearchInfo(added[index]);

            var existingIndex = 0;
            var addedIndex = 0;
            var mergedIndex = 0;
            while (existingIndex < existing.Count && addedIndex < added.Count)
            {
                if (CompareTypeSearchInfo(
                        existing.SearchInfos[existingIndex],
                        addedSearchInfos[addedIndex]
                    ) <= 0)
                    Add(existing[existingIndex], existing.SearchInfos[existingIndex++]);
                else
                    Add(added[addedIndex], addedSearchInfos[addedIndex++]);
            }

            while (existingIndex < existing.Count)
                Add(existing[existingIndex], existing.SearchInfos[existingIndex++]);
            while (addedIndex < added.Count)
                Add(added[addedIndex], addedSearchInfos[addedIndex++]);
            return new(mergedTypes, mergedSearchInfos);

            void Add(Type type, TypeSearchInfo searchInfo)
            {
                mergedTypes[mergedIndex] = type;
                mergedSearchInfos[mergedIndex++] = searchInfo;
            }
        }

        internal static int CompareTypes(Type? left, Type? right)
        {
            var leftInfo = left == null ? null : GetTypeSearchInfo(left);
            var rightInfo = right == null ? null : GetTypeSearchInfo(right);
            return CompareTypeSearchInfo(leftInfo, rightInfo);
        }

        static int CompareTypeSearchInfo(TypeSearchInfo? left, TypeSearchInfo? right)
        {
            var assembly = string.Compare(
                left?.AssemblyName,
                right?.AssemblyName,
                StringComparison.Ordinal
            );
            if (assembly != 0)
                return assembly;

            return string.Compare(
                left?.FullName,
                right?.FullName,
                StringComparison.Ordinal
            );
        }

        static string GetAssemblyName(Assembly? assembly)
            => assembly == null
                ? string.Empty
                : assemblyNameCache.GetOrAdd(assembly, static value => value.GetName().Name ?? string.Empty);

        static TypeSearchInfo GetTypeSearchInfo(Type type)
            => typeSearchInfoCache.GetOrAdd(type, static value => CreateTypeSearchInfo(value));

        static TypeSearchInfo CreateTypeSearchInfo(Type type)
        {
            var name = type.Name;
            return new(
                name,
                type.FullName ?? name,
                GetAssemblyName(type.Assembly),
                type.IsGenericType,
                type.IsNested,
                ClassifyType(type)
            );
        }

        static ReflectTypeKind ClassifyType(Type type)
        {
            if (type.IsInterface)
                return ReflectTypeKind.Interface;

            var baseType = type.BaseType;
            if (baseType == typeof(Enum))
                return ReflectTypeKind.Enum;
            if (baseType == typeof(MulticastDelegate))
                return ReflectTypeKind.Delegate;
            if (baseType == typeof(ValueType)
                && type != typeof(Enum))
                return ReflectTypeKind.Struct;
            return type == typeof(Delegate) || type == typeof(MulticastDelegate)
                ? ReflectTypeKind.Any
                : ReflectTypeKind.Class;
        }

        internal static string GetShortTypeName(Type type)
        {
            var info = GetTypeSearchInfo(type);
            if (!info.IsGenericType && !info.IsNested)
                return info.Name;

            return info.ShortDisplayName ??= ReflectionTypeFormatter.DisplayTypeName(
                type,
                includeNamespace: false
            );
        }

        static string ShortTypeName(Type type) => GetShortTypeName(type);

        readonly struct TypeSortEntry
        {
            internal TypeSortEntry(Type type, TypeSearchInfo searchInfo)
            {
                Type = type;
                SearchInfo = searchInfo;
            }

            internal Type Type { get; }
            internal TypeSearchInfo SearchInfo { get; }
        }
    }
}
