#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using Conduit;
using UnityEngine;

public sealed partial class ConduitSetupWizardTests
{
    string CreateExecutable(string fileName)
    {
        string executablePath = Path.Combine(tempRoot, fileName);
        File.WriteAllText(executablePath, "echo conduit");
        return executablePath;
    }

    EditorClientSpec CreateTempSpec(string id, string configFileName)
    {
        var source = EditorClientCatalog.FindEditorSpec(id);
        string configPath = Path.Combine(tempRoot, configFileName);
        return new()
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            ManualSetupSection = source.ManualSetupSection,
            CreateMissingConfig = source.CreateMissingConfig,
            Format = source.Format,
            BodyPath = source.BodyPath,
            TypeValue = source.TypeValue,
            EnabledValue = source.EnabledValue,
            DisabledValue = source.DisabledValue,
            UseCommandArray = source.UseCommandArray,
            TypeOptionalWhenReading = source.TypeOptionalWhenReading,
            StateOptionalWhenReading = source.StateOptionalWhenReading,
            IncludeAllTools = source.IncludeAllTools,
            CreateOnlyConfig = source.CreateOnlyConfig,
            RequireUnambiguousConfigPath = source.RequireUnambiguousConfigPath,
            RemoveKeys = source.RemoveKeys,
            ResolveUserConfigPath = _ => configPath,
            ResolveUserConfigPaths = null,
        };
    }

    static void DeleteConfig(string path)
    {
        if (File.Exists(path))
            File.Delete(path);

        string? directoryPath = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(directoryPath)
               && Directory.Exists(directoryPath)
               && Directory.GetFileSystemEntries(directoryPath).Length == 0)
        {
            Directory.Delete(directoryPath);
            directoryPath = Path.GetDirectoryName(directoryPath);
        }
    }

    sealed class FileScope : IDisposable
    {
        readonly string path;
        readonly string backupPath;
        readonly bool existed;

        public FileScope(string path)
        {
            this.path = path;
            backupPath = path + ".bak";
            existed = File.Exists(path);
            if (!existed)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(path, backupPath, true);
        }

        public void Dispose()
        {
            if (File.Exists(path))
                File.Delete(path);

            if (existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Move(backupPath, path);
                return;
            }

            if (File.Exists(backupPath))
                File.Delete(backupPath);

            string? directoryPath = Path.GetDirectoryName(path);
            while (!string.IsNullOrWhiteSpace(directoryPath)
                   && Directory.Exists(directoryPath)
                   && Directory.GetFileSystemEntries(directoryPath).Length == 0)
            {
                Directory.Delete(directoryPath);
                directoryPath = Path.GetDirectoryName(directoryPath);
            }
        }
    }
}
