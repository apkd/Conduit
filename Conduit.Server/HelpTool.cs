namespace Conduit;

static class HelpTool
{
    public static string GetHelpString(string? unityVersion)
    {
        var usesEntityIds = UsesEntityIds(unityVersion);
        string eid = usesEntityIds ? "eid" : "id";
        string entity = usesEntityIds ? "entity" : "instance";

        return $$$"""
                  Common object search help for `search`, `show`, `to_json`, and `from_json_overwrite`.

                  If you have an exact search target, you can simply specify one of these:
                  - exact {{{entity}}} ID: `{{{eid}}}:12345`
                  - exact asset path: `Assets/Foo.prefab`, `Assets/Materials/My Material.mat`
                  - exact hierarchy path: `/Root GameObject/Child (1)` for a scene object

                  Otherwise, the search will use the Unity Search query engine.

                  Resolution order is: exact {{{entity}}} ID (`{{{eid}}}:`) -> exact asset path -> exact hierarchy path -> Unity search query.
                  If you pass plain text that is not an ID or path, it is treated as a Unity search query.

                  The filters below were verified in-engine against this project.

                  **Hierarchy (`h:`) filters:**

                  - component search: `t:Camera`, `t=MeshRenderer`
                  - property search: `Camera.Orthographic=false`, `fieldofview=60`, `p(Camera.Orthographic)=false`, `p(fieldofview)=60`
                  - numeric layer filter: `layer=0`
                  - tag filter: `tag=MainCamera`
                  - references: `ref=Assets/ConduitHelpValidation/ConduitHelpMaterial.mat`, `ref:Assets/ConduitHelpValidation/ConduitHelpMaterial.mat`
                  - prefab state: `prefab:any`, `prefab:variant`, `prefab:root`
                  - scene-state filters: `active=true`, `components>3`, `is:child`, `is:leaf`, `is:prefab`, `is:root`, `is:static`, `path=/ConduitHelpRoot/ConduitHelpChild`, `size>1`
                  - fuzzy matching: `+fuzzy ConduitHelpCam`

                  **Project (`p:`) filters:**

                  - type: `t:material`, `t=Material`
                  - labels: `l:Weapons`
                  - search area: `a:assets`
                  - prefab state: `prefab:any`, `prefab:variant`
                  - references: `ref=Assets/ConduitHelpValidation/ConduitHelpMaterial.mat`
                  - file filters: `dir=Assets/ConduitHelpValidation`, `ext=mat`, `name=ConduitHelpMaterial`, `is:subasset`, `size>0`
                  - `+noResultsLimit`

                  The following were intentionally left out because they were not verified here or did not work as originally written: `renderinglayer`, `missing:...`, layer names such as `layer=Default`, `id=...`, `is:hidden`, `is:visible`, `age`, `a:packages`, `a:all`, and `prefab:asset`.
                  """;
    }

    static bool UsesEntityIds(string? unityVersion)
    {
        if (string.IsNullOrWhiteSpace(unityVersion))
            return true;

        var version = unityVersion.AsSpan();
        var firstDot = version.IndexOf('.');
        if (firstDot < 0)
            return true;

        var remainder = version[(firstDot + 1)..];
        var secondDot = remainder.IndexOf('.');
        var majorSpan = version[..firstDot];
        var minorSpan = secondDot < 0 ? remainder : remainder[..secondDot];
        if (!int.TryParse(majorSpan, out var major))
            return true;

        if (!int.TryParse(minorSpan, out var minor))
            return true;

        return (major, minor) is (> 6000, _) or (6000, >= 2);
        // return major > 6000 || major == 6000 && minor >= 2;
    }
}
