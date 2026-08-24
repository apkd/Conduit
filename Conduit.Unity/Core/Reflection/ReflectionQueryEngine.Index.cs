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
        const int ParallelScanEntriesPerWorker = 2048;
        const int MaxParallelScanWorkers = 16;
        const BindingFlags DeclaredMembers = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        static readonly object IndexLock = new();
        static readonly ConcurrentDictionary<Type, FieldInfo[]> fieldCache = new();
        static readonly ConcurrentDictionary<Type, PropertyInfo[]> propertyCache = new();
        static readonly ConcurrentDictionary<Type, MethodInfo[]> methodCache = new();
        static readonly ConcurrentDictionary<Type, MethodInfo[]> methodWithoutAccessorsCache = new();
        static readonly ConcurrentDictionary<Type, ConstructorInfo[]> constructorCache = new();
        static readonly ConcurrentDictionary<Assembly, string> assemblyNameCache = new();
        static readonly ConcurrentDictionary<Type, TypeSearchInfo> typeSearchInfoCache = new();

        static TypeIndex? cachedIndex;
        static List<Type>? pendingLoadedTypes;
        static Dictionary<string, Type?>? exactTypeLookup;
        static WideMemberIndex? wideFieldIndex;
        static WideMemberIndex? widePropertyIndex;
        static WideMemberIndex? wideMethodIndex;
        static WideMemberIndex? wideAccessorIndex;
        static WideMemberIndex? wideConstructorIndex;
        static string cachedLoadWarning = string.Empty;

        static ReflectionQueryEngine()
            => AppDomain.CurrentDomain.AssemblyLoad += static (_, args) => AddLoadedAssembly(args.LoadedAssembly);

        static void AddLoadedAssembly(Assembly assembly)
        {
            lock (IndexLock)
                if (cachedIndex == null)
                    return;

            var addedTypes = new List<Type>();
            using var pooledWarnings = BridgeStringBuilderPool.Rent(out var addedWarnings);
            AppendAssemblyTypes(addedTypes, assembly, addedWarnings);

            lock (IndexLock)
            {
                if (cachedIndex == null)
                    return;

                if (addedTypes.Count > 0)
                    (pendingLoadedTypes ??= new()).AddRange(addedTypes);

                if (addedWarnings.ToTrimmedString() is { Length: > 0 } warning)
                    AppendLoadWarning(warning);
            }
        }

        static void ApplyPendingLoadedTypes()
        {
            while (pendingLoadedTypes is { Count: > 0 } addedTypes)
            {
                pendingLoadedTypes = null;
                SortTypes(addedTypes);
                cachedIndex = MergeTypes(cachedIndex!, addedTypes);
                if (exactTypeLookup != null)
                    AddExactTypes(exactTypeLookup, addedTypes);
                ExtendWideMemberIndexes(addedTypes, cachedIndex);
            }
        }

        static void AppendLoadWarning(string warning)
        {
            var detailOffset = warning.IndexOf('\n');
            var detail = cachedLoadWarning.Length > 0 && detailOffset >= 0
                ? warning[(detailOffset + 1)..]
                : warning;
            cachedLoadWarning = cachedLoadWarning.Length == 0
                ? detail
                : cachedLoadWarning + "\n" + detail;
        }

    }
}
