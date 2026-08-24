#nullable enable

using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

sealed class ConduitCustomShowAsset : ScriptableObject
{
    string ToStringForMCP() => "Custom MCP show output";
}

sealed class ConduitThrowingEnumerableAsset : ScriptableObject
{
    readonly ConduitThrowingEnumerable throwingEnumerable = new();
}

sealed class ConduitThrowingEnumerable : IEnumerable
{
    public IEnumerator GetEnumerator() => throw new NotImplementedException();
}

sealed class ConduitNativeIndexableAsset : ScriptableObject
{
    object? indexableNumbers;

    public void Initialize() => indexableNumbers = CreateNativeList(1, 2, 3);

    public void Dispose()
    {
        if (indexableNumbers is IDisposable disposable)
            disposable.Dispose();

        indexableNumbers = null;
    }

    static object CreateNativeList(params int[] values)
    {
        var collectionsAssembly = FindLoadedAssembly("Unity.Collections")
                                  ?? throw new InvalidOperationException("Unity.Collections assembly is not loaded.");
        var allocatorType = FindLoadedType("Unity.Collections.Allocator")
                            ?? throw new InvalidOperationException("Unity.Collections.Allocator type is not loaded.");
        var allocatorManagerType = collectionsAssembly.GetType("Unity.Collections.AllocatorManager")
                                   ?? throw new InvalidOperationException("Unity.Collections.AllocatorManager type is not loaded.");
        var nativeListType = collectionsAssembly.GetType("Unity.Collections.NativeList`1")
                             ?.MakeGenericType(typeof(int))
                             ?? throw new InvalidOperationException("Unity.Collections.NativeList<T> type is not loaded.");
        var allocator = Enum.Parse(allocatorType, "Persistent");
        var allocatorHandle = allocatorManagerType
                                  .GetMethod("ConvertToAllocatorHandle", BindingFlags.Public | BindingFlags.Static)
                                  ?.Invoke(null, new[] { allocator })
                              ?? throw new InvalidOperationException("Could not create a persistent allocator handle.");
        object list = Activator.CreateInstance(nativeListType, new[] { allocatorHandle })
                      ?? throw new InvalidOperationException("Could not create NativeList<int>.");
        var add = nativeListType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance)
                  ?? throw new InvalidOperationException("NativeList<int>.Add was not found.");

        foreach (var value in values)
            add.Invoke(list, new object[] { value });

        return list;

        static System.Reflection.Assembly? FindLoadedAssembly(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == name)
                    return assembly;

            return null;
        }

        static Type? FindLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetType(fullName) is { } type)
                    return type;

            return null;
        }
    }
}

sealed class ConduitShowFormatAsset : ScriptableObject
{
}

sealed class ConduitNestedShowAsset : ScriptableObject
{
    [SerializeField] ConduitNestedShowLoadout loadout = new();
}

[Serializable]
sealed class ConduitNestedShowLoadout
{
    [SerializeField] ConduitNestedShowInventoryLoot inventoryLoot = new();
}

[Serializable]
sealed class ConduitNestedShowInventoryLoot
{
    [SerializeField] int[] entries = { 1, 2 };
    [SerializeField] bool chooseSingle;
}
