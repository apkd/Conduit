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

        public static string[] GetDirtySceneDescriptions()
        {
            using var pooled = ConduitUtility.GetPooledList<string>(out var dirtyScenes);

            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i) is { isDirty: true } scene)
                    dirtyScenes.Add(GetSceneDisplayName(scene));

            return dirtyScenes.ToArray();
        }

        public static string SaveScenes(string? targetScenePath)
        {
            if (string.IsNullOrWhiteSpace(targetScenePath))
                return SaveAllOpenScenes();

            var scene = FindOpenSceneByPath(targetScenePath);
            if (!scene.IsValid())
                throw new InvalidOperationException($"Open scene '{targetScenePath}' was not found.");

            if (!scene.isDirty)
                return $"Scene already clean: {scene.path}";

            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Failed to save scene '{scene.path}'.");

            return $"Saved scene: {scene.path}";
        }

        public static string DiscardScenes(string? targetScenePath)
        {
            if (string.IsNullOrWhiteSpace(targetScenePath))
                return DiscardAllDirtyScenes();

            var scene = FindOpenSceneByPath(targetScenePath);
            if (!scene.IsValid())
                throw new InvalidOperationException($"Open scene '{targetScenePath}' was not found.");

            if (!scene.isDirty)
                return $"Scene already clean: {scene.path}";

            DiscardSingleScene(scene);
            return $"Discarded scene changes: {scene.path}";
        }

        public static string BuildDirtySceneDiagnostic(string commandType)
        {
            if (GetDirtySceneDescriptions() is not { Length: > 0 } dirtyScenes)
                return string.Empty;

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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
            using var pooledSaved = ConduitUtility.GetPooledList<string>(out var savedScenes);
            using var pooledCreated = ConduitUtility.GetPooledList<string>(out var createdScenes);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i) is not { isDirty: true } scene)
                    continue;

                if (string.IsNullOrWhiteSpace(scene.path))
                {
                    var tempPath = CreateTempScenePath();
                    if (!EditorSceneManager.SaveScene(scene, tempPath))
                        throw new InvalidOperationException($"Failed to save untitled scene to '{tempPath}'.");

                    createdScenes.Add(tempPath);
                    savedScenes.Add(tempPath);
                    continue;
                }

                if (!EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException($"Failed to save scene '{scene.path}'.");

                savedScenes.Add(scene.path);
            }

            return BuildSaveSummary(savedScenes, createdScenes);
        }

        static string DiscardAllDirtyScenes()
        {
            using var pooledDiscarded = ConduitUtility.GetPooledList<string>(out var discardedScenes);
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
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine("Discarded scene changes:");
            for (int i = 0; i < discardedScenes.Count; i++)
            {
                builder.Append("- ");
                builder.AppendLine(discardedScenes[i]);
            }

            return builder.TrimEnd().ToString();
        }

        static void DiscardSingleScene(Scene scene)
        {
            if (string.IsNullOrWhiteSpace(scene.path))
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
                EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);
                return;
            }

            var currentSetup = EditorSceneManager.GetSceneManagerSetup();
            var updatedSetup = new SceneSetup[currentSetup.Length];
            var replacementFound = false;
            for (var index = 0; index < currentSetup.Length; index++)
            {
                updatedSetup[index] = currentSetup[index];
                if (!string.Equals(currentSetup[index].path, scene.path, StringComparison.OrdinalIgnoreCase))
                    continue;

                updatedSetup[index].path = scene.path;
                updatedSetup[index].isLoaded = true;
                replacementFound = true;
            }

            if (!replacementFound)
                throw new InvalidOperationException($"Failed to rebuild scene setup for '{scene.path}'.");

            EditorSceneManager.RestoreSceneManagerSetup(updatedSetup);
        }

        static Scene FindOpenSceneByPath(string? targetScenePath)
        {
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
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
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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

            return builder.TrimEnd().ToString();
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
            => ConduitUtility.FormatScenePath(scene, "untitled");
    }
}
