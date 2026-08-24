using System.Text;

namespace Conduit;

public sealed partial class UnityEditorProcessController
{
    internal static void PrepareRestartLogPath(string restartLogPath)
    {
        if (string.IsNullOrWhiteSpace(restartLogPath))
            return;

        try
        {
            using var stream = new FileStream(
                restartLogPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete
            );
        }
        catch
        {
            // Best-effort only; restart still proceeds and may fall back to stale log content if this fails.
        }
    }

    internal static string[] PreserveSceneBackups(string projectPath)
    {
        var platformProjectPath = ProjectPathNormalizer.ToPlatformPath(projectPath);
        var backupDirectoryPath = Path.Combine(platformProjectPath, "Temp", "__Backupscenes");
        if (!Directory.Exists(backupDirectoryPath))
            return Array.Empty<string>();

        var sourceFilePaths = Directory
            .EnumerateFiles(backupDirectoryPath, "*", SearchOption.TopDirectoryOnly)
            .ToArray();

        if (sourceFilePaths.Length == 0)
            return Array.Empty<string>();

        var recoveryDirectoryPath = Path.Combine(platformProjectPath, "Assets", "_Recovery");
        Directory.CreateDirectory(recoveryDirectoryPath);

        var copiedFilePaths = new string[sourceFilePaths.Length];
        for (var index = 0; index < sourceFilePaths.Length; index++)
        {
            var sourceFilePath = sourceFilePaths[index];
            var recoveryFileName = NormalizeRecoveryFileName(Path.GetFileName(sourceFilePath));
            copiedFilePaths[index] = GetUniqueRecoveryPath(recoveryDirectoryPath, recoveryFileName, copiedFilePaths, index);
        }

        for (var index = 0; index < sourceFilePaths.Length; index++)
            File.Copy(sourceFilePaths[index], copiedFilePaths[index], overwrite: false);

        foreach (var sourceFilePath in sourceFilePaths)
            File.Delete(sourceFilePath);

        if (!Directory.EnumerateFileSystemEntries(backupDirectoryPath).Any())
            Directory.Delete(backupDirectoryPath);

        return copiedFilePaths;
    }

    static void AppendLatestCompilationDiagnostics(StringBuilder builder, CompilationDiagnosticSummary restartCompilationDiagnostics)
    {
        if (!restartCompilationDiagnostics.HasAnyDiagnostics)
            return;

        var footer = restartCompilationDiagnostics.ErrorText ?? restartCompilationDiagnostics.WarningText;
        if (string.IsNullOrWhiteSpace(footer))
            return;

        builder.AppendLine();
        builder.AppendLine(footer);
    }

    internal static bool TryExtendRestartStartupWindow(
        DateTimeOffset currentWindowDeadlineUtc,
        DateTimeOffset startupDeadlineUtc,
        EditorLogSnapshot previousLogSnapshot,
        EditorLogSnapshot currentLogSnapshot,
        out DateTimeOffset nextWindowDeadlineUtc
    )
    {
        nextWindowDeadlineUtc = currentWindowDeadlineUtc;
        if (!currentLogSnapshot.HasActivitySince(previousLogSnapshot))
            return false;

        var remaining = startupDeadlineUtc - currentWindowDeadlineUtc;
        if (remaining <= TimeSpan.Zero)
            return false;

        nextWindowDeadlineUtc = currentWindowDeadlineUtc
            + (remaining < UnityToolTimeouts.RestartStartupWindow ? remaining : UnityToolTimeouts.RestartStartupWindow);
        return true;
    }

    static string NormalizeRecoveryFileName(string fileName)
    {
        if (!fileName.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
            return fileName;

        var withoutBackupSuffix = fileName[..^".backup".Length];
        return withoutBackupSuffix.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
            ? withoutBackupSuffix
            : withoutBackupSuffix + ".unity";
    }

    static string GetUniqueRecoveryPath(string directoryPath, string fileName, IReadOnlyList<string> pendingPaths, int pendingCount)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidatePath = Path.Combine(directoryPath, fileName);
        if (!PathExists(candidatePath, pendingPaths, pendingCount))
            return candidatePath;

        for (var suffix = 2;; suffix++)
        {
            candidatePath = Path.Combine(directoryPath, $"{nameWithoutExtension} ({suffix}){extension}");
            if (!PathExists(candidatePath, pendingPaths, pendingCount))
                return candidatePath;
        }
    }

    static bool PathExists(string candidatePath, IReadOnlyList<string> pendingPaths, int pendingCount)
    {
        if (File.Exists(candidatePath))
            return true;

        for (var index = 0; index < pendingCount; index++)
            if (string.Equals(pendingPaths[index], candidatePath, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
