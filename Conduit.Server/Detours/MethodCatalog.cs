using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Conduit;

sealed partial class MethodCatalog
{
    readonly MethodTarget[] indexedMethods;
    readonly Dictionary<string, MethodBucket> methodBuckets;
    readonly ConcurrentDictionary<string, MethodResolution> resolutionCache = new(StringComparer.Ordinal);

    MethodCatalog(MethodTarget[][] methodSets, int methodCount)
    {
        // real Unity metadata averages about two unique lookup names per five method rows.
        methodBuckets = new(methodCount * 2 / 5, StringComparer.Ordinal);
        foreach (var methods in methodSets)
            foreach (var method in methods)
            {
                Count(method.MethodName);
                var separator = method.MethodName.LastIndexOf('.');

                // explicit-interface short names repeat heavily; span lookup avoids one substring per method.
                if (separator >= 0)
                    CountShort(method.MethodName.AsSpan(separator + 1));
            }

        var indexedCount = 0;
        foreach (var name in methodBuckets.Keys)
        {
            ref var bucket = ref CollectionsMarshal.GetValueRefOrNullRef(methodBuckets, name);
            var count = bucket.Count;
            bucket = new(indexedCount, 0);
            indexedCount += count;
        }

        indexedMethods = new MethodTarget[indexedCount];
        foreach (var methods in methodSets)
            foreach (var method in methods)
            {
                Index(method.MethodName, method);
                var separator = method.MethodName.LastIndexOf('.');
                if (separator >= 0)
                    IndexShort(method.MethodName.AsSpan(separator + 1), method);
            }

        void Count(string name)
            => CollectionsMarshal.GetValueRefOrAddDefault(methodBuckets, name, out _).Count++;

        void CountShort(ReadOnlySpan<char> name)
        {
            var lookup = methodBuckets.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(name, out var bucket))
                lookup[name] = new(bucket.Offset, bucket.Count + 1);
            else
                methodBuckets.Add(name.ToString(), new(0, 1));
        }

        void Index(string name, MethodTarget method)
        {
            ref var bucket = ref CollectionsMarshal.GetValueRefOrNullRef(methodBuckets, name);
            indexedMethods[bucket.Offset + bucket.Count++] = method;
        }

        void IndexShort(ReadOnlySpan<char> name, MethodTarget method)
        {
            var lookup = methodBuckets.GetAlternateLookup<ReadOnlySpan<char>>();
            var bucket = lookup[name];
            indexedMethods[bucket.Offset + bucket.Count++] = method;
            lookup[name] = bucket;
        }
    }

    struct MethodBucket(int offset, int count)
    {
        internal int Offset = offset;
        internal int Count = count;
    }
}
