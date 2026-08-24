#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine.SceneManagement;

namespace Conduit
{
    static partial class ConduitOpenSceneDiskChangeGuard
    {
        static void SnapshotOpenSceneStamps()
        {
            var sceneCount = SceneManager.sceneCount;
            for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
                RememberSceneStamp(SceneManager.GetSceneAt(sceneIndex));
        }

        static void RememberSceneStamp(Scene scene)
        {
            var scenePath = scene.path;
            if (!string.IsNullOrWhiteSpace(scenePath))
                RememberSceneStamp(scenePath);
        }

        static void RememberSceneStamp(string scenePath)
        {
            var stamp = TryReadSceneFileStamp(scenePath);
            lock (gate)
            {
                if (stamp == null)
                    knownSceneStamps.Remove(scenePath);
                else
                    knownSceneStamps[scenePath] = stamp.Value;

                pendingSceneFileChanges.Remove(scenePath);
                UpdatePendingChangeCount();
            }
        }

        static void ForgetScenePath(string scenePath)
        {
            lock (gate)
            {
                knownSceneStamps.Remove(scenePath);
                pendingSceneFileChanges.Remove(scenePath);
                UpdatePendingChangeCount();
            }
        }

        static void RemovePendingSceneFileChange(string scenePath)
        {
            lock (gate)
            {
                pendingSceneFileChanges.Remove(scenePath);
                UpdatePendingChangeCount();
            }
        }

        // callers hold gate while publishing the collection's lock-free idle state.
        static void UpdatePendingChangeCount()
            => Volatile.Write(ref pendingSceneFileChangeCount, pendingSceneFileChanges.Count);

        static SceneFileStamp? TryReadSceneFileStamp(string scenePath)
        {
            if (!TryConvertAssetPathToAbsolutePath(scenePath, out var absolutePath))
                return null;

            return TryReadSceneFileStampFromAbsolutePath(absolutePath);
        }

        static SceneFileStamp? TryReadSceneFileStampFromAbsolutePath(string absolutePath)
        {
            try
            {
                var fileInfo = new FileInfo(absolutePath);
                return fileInfo.Exists
                    ? new(fileInfo.Length, fileInfo.LastWriteTimeUtc)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        static bool TryConvertAbsoluteScenePathToAssetPath(string absolutePath, out string sceneAssetPath)
        {
            sceneAssetPath = string.Empty;
            if (string.IsNullOrWhiteSpace(projectRootPath))
                return false;

            try
            {
                var fullPath = Path.GetFullPath(absolutePath);
                var rootPath = projectRootPath!;
                if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                    return false;

                var relativePath = fullPath[rootPath.Length..].Replace(Path.DirectorySeparatorChar, '/');
                if (!relativePath.StartsWith(AssetsPrefix, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!relativePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    return false;

                sceneAssetPath = relativePath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool TryConvertAssetPathToAbsolutePath(string assetPath, out string absolutePath)
        {
            absolutePath = string.Empty;
            if (string.IsNullOrWhiteSpace(projectRootPath) || string.IsNullOrWhiteSpace(assetPath))
                return false;

            if (!assetPath.StartsWith(AssetsPrefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(assetPath, "Assets", StringComparison.OrdinalIgnoreCase))
                return false;

            absolutePath = Path.GetFullPath(Path.Combine(projectRootPath, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            return true;
        }

        static string BuildReloadReport(List<string> reloadedScenes)
        {
            if (reloadedScenes.Count == 0)
                return string.Empty;

            reloadedScenes.Sort(StringComparer.Ordinal);
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.AppendLine("Reloaded open scene(s) changed on disk:");
            for (var index = 0; index < reloadedScenes.Count; index++)
            {
                builder.Append("- ");
                builder.AppendLine(reloadedScenes[index]);
            }

            return builder.ToTrimmedString();
        }

        static string BuildBlockedDirtySceneDiagnostic(string commandType, List<string> blockedScenes)
        {
            blockedScenes.Sort(StringComparer.Ordinal);
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.Append("Cannot run '");
            builder.Append(commandType);
            builder.AppendLine("' because open scene file(s) changed on disk and could not be reloaded automatically.");
            builder.AppendLine("Blocked scenes:");
            for (var index = 0; index < blockedScenes.Count; index++)
            {
                builder.Append("- ");
                builder.AppendLine(blockedScenes[index]);
            }

            builder.AppendLine("This usually means Unity has unsaved in-memory scene changes.");
            builder.Append("Use '");
            builder.Append(BridgeCommandTypes.DiscardScenes);
            builder.Append("' to reload the on-disk scene version, or '");
            builder.Append(BridgeCommandTypes.SaveScenes);
            builder.Append("' to keep Unity's in-memory version.");
            return builder.ToString();
        }

        readonly struct SceneFileStamp : IEquatable<SceneFileStamp>
        {
            readonly long length;
            readonly DateTime lastWriteTimeUtc;

            internal SceneFileStamp(long length, DateTime lastWriteTimeUtc)
            {
                this.length = length;
                this.lastWriteTimeUtc = lastWriteTimeUtc;
            }

            public bool Equals(SceneFileStamp other)
                => length == other.length && lastWriteTimeUtc == other.lastWriteTimeUtc;

            public override bool Equals(object? obj)
                => obj is SceneFileStamp other && Equals(other);

            public override int GetHashCode()
                => unchecked((length.GetHashCode() * 397) ^ lastWriteTimeUtc.GetHashCode());

            public static bool operator ==(SceneFileStamp left, SceneFileStamp right) => left.Equals(right);

            public static bool operator !=(SceneFileStamp left, SceneFileStamp right) => !left.Equals(right);
        }

        readonly struct PendingSceneFileChange
        {
            internal PendingSceneFileChange(SceneFileStamp? observedStamp, double lastChangedAt)
            {
                ObservedStamp = observedStamp;
                LastChangedAt = lastChangedAt;
            }

            internal SceneFileStamp? ObservedStamp { get; }
            internal double LastChangedAt { get; }
        }
    }
}
