#nullable enable

using System;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Conduit
{
    static class ConduitSceneCommandUtility
    {
        const string RecoveryDirectoryPath = "Assets/_Recovery";

        internal static string[] GetDirtySceneDescriptions()
        {
            using var pooled = ConduitPool.GetPooledList<string>(out var dirtyScenes);

            var sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
                if (SceneManager.GetSceneAt(i) is { isDirty: true } scene)
                    dirtyScenes.Add(GetSceneDisplayName(scene));

            return dirtyScenes.ToArray();
        }

        internal static string SaveScenes(string? targetScenePath)
        {
            if (string.IsNullOrWhiteSpace(targetScenePath))
                return SaveAllOpenScenes();

            var scene = FindOpenSceneByPath(targetScenePath);
            if (!scene.IsValid())
                throw new InvalidOperationException($"Open scene '{targetScenePath}' was not found.");

            var scenePath = scene.path;
            if (!scene.isDirty)
                return $"Scene already clean: {scenePath}";

            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Failed to save scene '{scenePath}'.");

            return $"Saved scene: {scenePath}";
        }

        internal static string DiscardScenes(string? targetScenePath)
        {
            if (string.IsNullOrWhiteSpace(targetScenePath))
                return DiscardAllDirtyScenes();

            var scene = FindOpenSceneByPath(targetScenePath);
            if (!scene.IsValid())
                throw new InvalidOperationException($"Open scene '{targetScenePath}' was not found.");

            var scenePath = scene.path;
            if (!scene.isDirty)
                return $"Scene already clean: {scenePath}";

            DiscardSingleScene(scene);
            return $"Discarded scene changes: {scenePath}";
        }

        internal static string BuildDirtySceneDiagnostic(string commandType)
        {
            if (GetDirtySceneDescriptions() is not { Length: > 0 } dirtyScenes)
                return string.Empty;

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.Append("Cannot run '");
            builder.Append(commandType);
            builder.AppendLine("' while scenes have unsaved changes.");
            builder.AppendLine("Dirty scenes:");
            for (var index = 0; index < dirtyScenes.Length; index++)
            {
                builder.Append("- ");
                builder.AppendLine(dirtyScenes[index]);
            }

            builder.Append("Use '");
            builder.Append(BridgeCommandTypes.SaveScenes);
            builder.Append("' to save them or '");
            builder.Append(BridgeCommandTypes.DiscardScenes);
            builder.Append("' to discard them.");
            return builder.ToString();
        }

        static string SaveAllOpenScenes()
        {
            using var pooledSaved = ConduitPool.GetPooledList<string>(out var savedScenes);
            using var pooledCreated = ConduitPool.GetPooledList<string>(out var createdScenes);
            var sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i) is not { isDirty: true } scene)
                    continue;

                var scenePath = scene.path;
                if (string.IsNullOrWhiteSpace(scenePath))
                {
                    var tempPath = CreateTempScenePath();
                    if (!EditorSceneManager.SaveScene(scene, tempPath))
                        throw new InvalidOperationException($"Failed to save untitled scene to '{tempPath}'.");

                    createdScenes.Add(tempPath);
                    savedScenes.Add(tempPath);
                    continue;
                }

                if (!EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException($"Failed to save scene '{scenePath}'.");

                savedScenes.Add(scenePath);
            }

            return BuildSaveSummary(savedScenes, createdScenes);
        }

        static string DiscardAllDirtyScenes()
        {
            using var pooledDiscarded = ConduitPool.GetPooledList<string>(out var discardedScenes);
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                if (SceneManager.GetSceneAt(i) is not { isDirty: true } scene)
                    continue;

                discardedScenes.Add(GetSceneDisplayName(scene));
                DiscardSingleScene(scene);
            }

            if (discardedScenes.Count == 0)
                return "No dirty scenes to discard.";

            discardedScenes.Sort(StringComparer.Ordinal);
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.AppendLine("Discarded scene changes:");
            for (int i = 0; i < discardedScenes.Count; i++)
            {
                builder.Append("- ");
                builder.AppendLine(discardedScenes[i]);
            }

            return builder.ToTrimmedString();
        }

        static void DiscardSingleScene(Scene scene)
        {
            var scenePath = scene.path;
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                if (SceneManager.sceneCount == 1)
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    return;
                }

                if (!EditorSceneManager.CloseScene(scene, removeScene: true))
                    throw new InvalidOperationException($"Failed to discard untitled scene '{scene.name}'.");

                return;
            }

            if (SceneManager.sceneCount == 1 && SceneManager.GetActiveScene() == scene)
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                return;
            }

            var currentSetup = EditorSceneManager.GetSceneManagerSetup();
            var updatedSetup = new SceneSetup[currentSetup.Length];
            var replacementFound = false;
            for (var index = 0; index < currentSetup.Length; index++)
            {
                updatedSetup[index] = currentSetup[index];
                if (!string.Equals(currentSetup[index].path, scenePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                updatedSetup[index].path = scenePath;
                updatedSetup[index].isLoaded = true;
                replacementFound = true;
            }

            if (!replacementFound)
                throw new InvalidOperationException($"Failed to rebuild scene setup for '{scenePath}'.");

            EditorSceneManager.RestoreSceneManagerSetup(updatedSetup);
        }

        static Scene FindOpenSceneByPath(string? targetScenePath)
        {
            var sceneCount = SceneManager.sceneCount;
            for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (string.Equals(scene.path, targetScenePath, StringComparison.OrdinalIgnoreCase))
                    return scene;
            }

            return default;
        }

        static string BuildSaveSummary(System.Collections.Generic.List<string> savedScenes, System.Collections.Generic.List<string> createdScenes)
        {
            if (savedScenes.Count == 0)
                return "No dirty scenes to save.";

            savedScenes.Sort(StringComparer.Ordinal);
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.AppendLine("Saved scenes:");
            for (var index = 0; index < savedScenes.Count; index++)
            {
                builder.Append("- ");
                builder.AppendLine(savedScenes[index]);
            }

            if (createdScenes.Count > 0)
            {
                createdScenes.Sort(StringComparer.Ordinal);
                builder.AppendLine("Created scene files:");
                for (var index = 0; index < createdScenes.Count; index++)
                {
                    builder.Append("- ");
                    builder.AppendLine(createdScenes[index]);
                }
            }

            return builder.ToTrimmedString();
        }

        static string CreateTempScenePath()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "_Recovery"));

            for (var index = 1; index < int.MaxValue; index++)
            {
                var candidate = $"{RecoveryDirectoryPath}/TempScene_{index}.unity";
                if (!System.IO.File.Exists(candidate))
                    return candidate;
            }

            throw new InvalidOperationException("Could not allocate a temporary scene path.");
        }

        static string GetSceneDisplayName(Scene scene)
            => ConduitHierarchyPathUtility.FormatScenePath(scene, "untitled");
    }
}
