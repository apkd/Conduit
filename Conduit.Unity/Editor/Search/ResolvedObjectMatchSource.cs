#nullable enable

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
}
