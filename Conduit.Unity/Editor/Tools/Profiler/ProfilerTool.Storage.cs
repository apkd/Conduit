#nullable enable

using System;
using System.Globalization;
using System.IO;
using UnityEditorInternal;

namespace Conduit
{
    static partial class ProfilerTool
    {
        const string CaptureDirectory = "Temp/profiler";

        static BridgeCommandResult Save(ProfilerOptions options)
        {
            var fileName = options.GetString("file_name", "");
            var path = ResolveCapturePath(fileName, allocateDefault: true);
            try
            {
                if (CountAvailableFrames() == 0)
                    return Failure("Unable to save profile capture.", "No profiler frames are available.", path.DisplayPath);

                SaveProfile(path.AbsolutePath);
                return BridgeCommandResult.Success($"Profile capture saved!\nFile: {path.DisplayPath}");
            }
            catch (Exception exception)
            {
                return Failure("Unable to save profile capture.", exception.Message, path.DisplayPath);
            }
        }

        static BridgeCommandResult Load(ProfilerOptions options)
        {
            var fileName = options.GetString("file_name", "");
            var path = ResolveCapturePath(fileName, allocateDefault: false);
            try
            {
                if (!File.Exists(path.AbsolutePath))
                    return Failure("Unable to load profile capture.", "File not found.", path.DisplayPath);

                if (!ProfilerDriver.LoadProfile(path.AbsolutePath, false))
                    return Failure("Unable to load profile capture.", "Unity failed to load the profile capture.", path.DisplayPath);

                return BridgeCommandResult.Success(
                    $"Profile capture loaded!\nFile: {path.DisplayPath}\nFrame count: {CountAvailableFrames().ToString(CultureInfo.InvariantCulture)}"
                );
            }
            catch (Exception exception)
            {
                return Failure("Unable to load profile capture.", exception.Message, path.DisplayPath);
            }
        }

        static BridgeCommandResult ListCaptures()
        {
            var directory = Path.Combine(ConduitAssetPathUtility.GetProjectRootPath(), CaptureDirectory);
            if (!Directory.Exists(directory))
                return BridgeCommandResult.Success($"No profile captures found.\nDirectory: {CaptureDirectory}");

            var files = Directory.GetFiles(directory, "*.data");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            if (files.Length == 0)
                return BridgeCommandResult.Success($"No profile captures found.\nDirectory: {CaptureDirectory}");

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.AppendLine("Profile captures:");
            foreach (var file in files)
                builder.AppendLine(ToDisplayPath(file));

            return BridgeCommandResult.Success(builder.ToTrimmedString());
        }

        static void SaveProfile(string path)
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            ProfilerDriver.SaveProfile(path);
        }

        static CapturePath ResolveCapturePath(string? fileName, bool allocateDefault)
        {
            var projectRoot = ConduitAssetPathUtility.GetProjectRootPath();
            var value = fileName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (!allocateDefault)
                    value = "capture.data";
                else
                    value = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.data";
            }

            if (Path.GetExtension(value).Length == 0)
                value += ".data";

            if (!Path.IsPathRooted(value)
                && ContainsParentTraversal(value.AsSpan()))
                throw new InvalidOperationException(
                    $"Relative profiler capture path '{value}' contains parent traversal."
                );

            string absolutePath;
            if (Path.IsPathRooted(value))
                absolutePath = Path.GetFullPath(value);
            else if (value.IndexOf('/') < 0 && value.IndexOf('\\') < 0)
                absolutePath = Path.GetFullPath(Path.Combine(projectRoot, CaptureDirectory, value));
            else
                absolutePath = Path.GetFullPath(Path.Combine(projectRoot, value));

            return new(absolutePath, ToDisplayPath(absolutePath));

            static bool ContainsParentTraversal(ReadOnlySpan<char> path)
            {
                var segmentStart = 0;
                for (var index = 0; index <= path.Length; index++)
                {
                    if (index < path.Length && path[index] is not ('/' or '\\'))
                        continue;

                    if (path[segmentStart..index].SequenceEqual("..".AsSpan()))
                        return true;

                    segmentStart = index + 1;
                }

                return false;
            }
        }

        static string ToDisplayPath(string absolutePath)
        {
            var projectRoot = Path.GetFullPath(ConduitAssetPathUtility.GetProjectRootPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(absolutePath);
            if (fullPath.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(projectRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return fullPath[(projectRoot.Length + 1)..].Replace('\\', '/');

            return fullPath.Replace('\\', '/');
        }

    }
}
