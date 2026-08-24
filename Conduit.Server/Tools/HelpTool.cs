namespace Conduit;

static class HelpTool
{
    public static string GetHelpString(string? unityVersion)
    {
        var usesEntityIds = UsesEntityIds(unityVersion);
        string eid = usesEntityIds ? "eid" : "id";
        string entity = usesEntityIds ? "entity" : "instance";

        return $"""
                # Common tool search format

                The same search query format is used for `search`, `show`, `to_json`, `from_json_overwrite`, and `reimport_assets`.

                If you have an exact search target, you can simply specify one of these:
                - exact {entity} ID: `{eid}:12345`
                - exact asset path: `Assets/Foo.prefab`, `Assets/Materials/My Material.mat`
                - exact hierarchy path: `/Root GameObject/Child (1)` for a scene object
                - list project NUnit tests: `t:test`, `t:test editmode`, `t:test SomeModule`

                # Unity Search fallback

                If none of the above match, the Unity Search query engine is used, supporting the following formats.

                ## Hierarchy (`h:`) filters

                - component search: `t:Camera`, `t=MeshRenderer`
                - property search: `Camera.Orthographic=false`, `fieldofview=60`, `p(Camera.Orthographic)=false`, `p(fieldofview)=60`
                - numeric layer filter: `layer=0`
                - tag filter: `tag=MainCamera`
                - references: `ref=Assets/HelpValidation/HelpMaterial.mat`, `ref:Assets/HelpValidation/HelpMaterial.mat`
                - prefab state: `prefab:any`, `prefab:variant`, `prefab:root`
                - scene-state filters: `active=true`, `components>3`, `is:child`, `is:leaf`, `is:prefab`, `is:root`, `is:static`, `path=/HelpRoot/HelpChild`, `size>1`
                - fuzzy matching: `+fuzzy HelpCam`

                ## Project (`p:`) filters

                - type: `t:material`, `t=Material`
                - labels: `l:Weapons`
                - search area: `a:assets`
                - prefab state: `prefab:any`, `prefab:variant`
                - references: `ref=Assets/HelpValidation/HelpMaterial.mat`
                - file filters: `dir=Assets/HelpValidation`, `ext=mat`, `name=HelpMaterial`, `is:subasset`, `size>0`
                - `+noResultsLimit`

                Fallback project queries index `Assets/`. Package assets resolve by exact `Packages/...` path;
                `UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()` exposes package roots inside `execute_code`.

                # `execute_code` search and reflection utilities

                Inside `execute_code`, use `Search<T>("query")` and `SearchMany<T>("query")` for typed object lookup.
                These helpers use the same query rules, filter for `T`, and return typed objects or components.
                `Search<T>` resolves one specific object; `SearchMany<T>` returns an array.

                You can also use `Reflect.Type("NativeArrayUnsafeUtility")`, `Reflect.Methods(type: "Camera")`, `Reflect.Interfaces(member: "Dispose")`,
                `Reflect.Classes("Handler")`, `Reflect.Enum("AggressiveInlining")` etc. for easy reflection lookup with the same API as the `reflect` tool.
                (Singular `Reflect.Method` returns exactly one MethodInfo; plural `Reflect.Methods` returns an array, and so on.)

                Use `return <value>;` to print the value in the tool response. `return;` exits without a result.
                Pass a prior snippet's filename, such as `7.cs` instead of source code to run it again.

                # `detour`

                Replace a managed method body at runtime without recompiling the Unity project.
                Find ways to use this tool whenever possible - this is much faster than editing source and running `refresh_asset_database`.
                Use cases: prototyping, debugging, testing fixes, checking hypotheses, etc.

                - inspect support and get the canonical selector: `detour("Game.Player.TakeDamage", "test")`
                - replace an instance method: `detour("Game.Player.TakeDamage", "@this.health -= arg0 * 2;")`
                - replace a returning method: `detour("Game.Player.GetHealth", "return 100;")`
                - apply any saved Conduit script: `detour("Game.Player.GetHealth", "MyReplacement.cs")`
                - restore the original method: `detour("Game.Player.GetHealth", "restore")`

                You can use `detour` with most managed methods loaded from any assembly, including packages and UnityEngine/UnityEditor.
                Run `test` first to check whether the method is compatible.
                Replacement bodies use `@this` for the instance and `arg0`, `arg1`, etc. for parameters.
                Does not support generic methods and small/inlined methods.

                # `project_settings`

                One central tool for reading and editing an extensive set of project-level options.

                - list all settings groups: `project_settings("get", key: "")`
                - all settings in a group: `project_settings("get", key: "build_settings")`
                - read or search: `project_settings("get", key: "log shader compilation")`
                - set a value: `project_settings("set", key: "graphics_settings.log_shader_compilation", value: "true")`
                - append an element: `project_settings("add_element", key: "tag_manager.tags", value: "Gameplay")`
                - remove an element: `project_settings("remove_element", key: "tag_manager.tags.4")`
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
