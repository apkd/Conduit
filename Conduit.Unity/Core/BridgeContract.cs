#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace Conduit
{
    /// <summary>Defines the bridge wire version shared by the server, Editor, and player.</summary>
    static class BridgeContract
    {
        public const int Version = 8;

        public static string FormatProtocolMismatch(int serverVersion, int unityVersion)
            => serverVersion < unityVersion
                ? $"Conduit server protocol {serverVersion} is older than Unity Editor bridge protocol {unityVersion}."
                : $"Unity Editor bridge protocol {unityVersion} is older than Conduit server protocol {serverVersion}.";
    }

    static class BridgeCommandTypes
    {
        public const string Help = "help";
        public const string Restart = "restart";
        public const string Status = "status";
        public const string PlayMode = "playmode";
        public const string EditMode = "editmode";
        public const string Screenshot = "screenshot";
        public const string Record = "record";
        public const string GetDependencies = "get_dependencies";
        public const string FindReferencesTo = "find_references_to";
        public const string FindMissingScripts = "find_missing_scripts";
        public const string Show = "show";
        public const string Search = "search";
        public const string ToJson = "to_json";
        public const string FromJsonOverwrite = "from_json_overwrite";
        public const string SaveScenes = "save_scenes";
        public const string DiscardScenes = "discard_scenes";
        public const string RefreshAssetDatabase = "refresh_asset_database";
        public const string ReimportAssets = "reimport_assets";
        public const string ExecuteCode = "execute_code";
        public const string Detour = "detour";
        public const string ViewBurstAsm = "view_burst_asm";
        public const string Reflect = "reflect";
        public const string ProjectSettings = "project_settings";
        public const string RunTestsEditMode = "run_tests_editmode";
        public const string RunTestsPlayMode = "run_tests_playmode";
        public const string RunTestsPlayer = "run_tests_player";
        public const string ProfilerRecord = "profiler_record";
        public const string ProfilerOverview = "profiler_overview";
        public const string ProfilerBrowse = "profiler_browse";
        internal const string ProfilerHasMarker = "profiler_has_marker";
        internal const string CompilationReferences = "compilation_references";
        internal const string AssemblyBlob = "assembly_blob";
        internal const string QuitPlayer = "quit_player";
    }

    enum BridgeCommandKind : byte
    {
        Unknown,
        Help,
        Restart,
        Status,
        PlayMode,
        EditMode,
        Screenshot,
        Record,
        GetDependencies,
        FindReferencesTo,
        FindMissingScripts,
        Show,
        Search,
        ToJson,
        FromJsonOverwrite,
        SaveScenes,
        DiscardScenes,
        RefreshAssetDatabase,
        ReimportAssets,
        ExecuteCode,
        Detour,
        ViewBurstAsm,
        Reflect,
        ProjectSettings,
        RunTestsEditMode,
        RunTestsPlayMode,
        RunTestsPlayer,
        ProfilerRecord,
        ProfilerOverview,
        ProfilerBrowse,
        ProfilerHasMarker,
        CompilationReferences,
        AssemblyBlob,
        QuitPlayer,
    }

    static class BridgeCommandKinds
    {
        public static BridgeCommandKind Parse(string? commandType)
            => commandType switch
            {
                BridgeCommandTypes.Help                  => BridgeCommandKind.Help,
                BridgeCommandTypes.Restart               => BridgeCommandKind.Restart,
                BridgeCommandTypes.Status                => BridgeCommandKind.Status,
                BridgeCommandTypes.PlayMode              => BridgeCommandKind.PlayMode,
                BridgeCommandTypes.EditMode              => BridgeCommandKind.EditMode,
                BridgeCommandTypes.Screenshot            => BridgeCommandKind.Screenshot,
                BridgeCommandTypes.Record                => BridgeCommandKind.Record,
                BridgeCommandTypes.GetDependencies       => BridgeCommandKind.GetDependencies,
                BridgeCommandTypes.FindReferencesTo      => BridgeCommandKind.FindReferencesTo,
                BridgeCommandTypes.FindMissingScripts    => BridgeCommandKind.FindMissingScripts,
                BridgeCommandTypes.Show                  => BridgeCommandKind.Show,
                BridgeCommandTypes.Search                => BridgeCommandKind.Search,
                BridgeCommandTypes.ToJson                => BridgeCommandKind.ToJson,
                BridgeCommandTypes.FromJsonOverwrite     => BridgeCommandKind.FromJsonOverwrite,
                BridgeCommandTypes.SaveScenes            => BridgeCommandKind.SaveScenes,
                BridgeCommandTypes.DiscardScenes         => BridgeCommandKind.DiscardScenes,
                BridgeCommandTypes.RefreshAssetDatabase  => BridgeCommandKind.RefreshAssetDatabase,
                BridgeCommandTypes.ReimportAssets        => BridgeCommandKind.ReimportAssets,
                BridgeCommandTypes.ExecuteCode           => BridgeCommandKind.ExecuteCode,
                BridgeCommandTypes.Detour                => BridgeCommandKind.Detour,
                BridgeCommandTypes.ViewBurstAsm          => BridgeCommandKind.ViewBurstAsm,
                BridgeCommandTypes.Reflect               => BridgeCommandKind.Reflect,
                BridgeCommandTypes.ProjectSettings       => BridgeCommandKind.ProjectSettings,
                BridgeCommandTypes.RunTestsEditMode      => BridgeCommandKind.RunTestsEditMode,
                BridgeCommandTypes.RunTestsPlayMode      => BridgeCommandKind.RunTestsPlayMode,
                BridgeCommandTypes.RunTestsPlayer        => BridgeCommandKind.RunTestsPlayer,
                BridgeCommandTypes.ProfilerRecord        => BridgeCommandKind.ProfilerRecord,
                BridgeCommandTypes.ProfilerOverview      => BridgeCommandKind.ProfilerOverview,
                BridgeCommandTypes.ProfilerBrowse        => BridgeCommandKind.ProfilerBrowse,
                BridgeCommandTypes.ProfilerHasMarker     => BridgeCommandKind.ProfilerHasMarker,
                BridgeCommandTypes.CompilationReferences => BridgeCommandKind.CompilationReferences,
                BridgeCommandTypes.AssemblyBlob          => BridgeCommandKind.AssemblyBlob,
                BridgeCommandTypes.QuitPlayer            => BridgeCommandKind.QuitPlayer,
                _                                        => BridgeCommandKind.Unknown,
            };

        public static bool IsTest(BridgeCommandKind commandKind)
            => commandKind is BridgeCommandKind.RunTestsEditMode
                or BridgeCommandKind.RunTestsPlayMode
                or BridgeCommandKind.RunTestsPlayer;

        public static bool IsAssetImport(BridgeCommandKind commandKind)
            => commandKind is BridgeCommandKind.RefreshAssetDatabase or BridgeCommandKind.ReimportAssets;

        public static bool IsEditorMode(BridgeCommandKind commandKind)
            => commandKind is BridgeCommandKind.PlayMode or BridgeCommandKind.EditMode;

        public static bool IsProfiler(BridgeCommandKind commandKind)
            => commandKind is BridgeCommandKind.ProfilerRecord
                or BridgeCommandKind.ProfilerOverview
                or BridgeCommandKind.ProfilerBrowse;

        public static bool SupportsCancellation(BridgeCommandKind commandKind)
            => IsTest(commandKind) || commandKind == BridgeCommandKind.Record;
    }

    static class BridgeMessageTypes
    {
        public const string Hello = "hello";
        public const string Command = "command";
        public const string CancelCommand = "cancel_command";
        public const string CommandStarted = "command_started";
        public const string CommandResult = "command_result";
    }

    static class ToolOutcome
    {
        public const string Success = "success";
        public const string Exception = "exception";
        public const string CompileError = "compile_error";
        public const string TestFailed = "test_failed";
        public const string Timeout = "timeout";
        public const string NotConnected = "not_connected";
        public const string DirtyScene = "dirty_scene";
        public const string AmbiguousTarget = "ambiguous_target";
        public const string Cancelled = "cancelled";
    }

    static class BridgeEndpointKinds
    {
        public const string Editor = "editor";
        public const string Player = "player";
    }

    static class BridgeTransportKinds
    {
        public const string NamedPipe = "named_pipe";
        public const string Fifo = "fifo";
    }

    [Serializable]
    sealed class BridgeMessage
    {
        public int protocol_version = BridgeContract.Version;
        public string message_type = string.Empty;
        public string? request_id;
        public BridgeProjectHandshake? project;
        public BridgeCommand? command;
        public BridgeCommandResult? result;

        public int ProtocolVersion { get => protocol_version; set => protocol_version = value; }
        public string MessageType { get => message_type; set => message_type = value; }
        public string? RequestId { get => request_id; set => request_id = value; }
        public BridgeProjectHandshake? Project { get => project; set => project = value; }
        public BridgeCommand? Command { get => command; set => command = value; }
        public BridgeCommandResult? Result { get => result; set => result = value; }

        public static BridgeMessage CreateHello(BridgeProjectHandshake project)
            => new() { message_type = BridgeMessageTypes.Hello, project = project };

        public static BridgeMessage CreateCommand(string requestId, BridgeCommand command)
            => new() { message_type = BridgeMessageTypes.Command, request_id = requestId, command = command };

        public static BridgeMessage CreateCommandStarted(string requestId)
            => new() { message_type = BridgeMessageTypes.CommandStarted, request_id = requestId };

        public static BridgeMessage CreateCancelCommand(string requestId)
            => new() { message_type = BridgeMessageTypes.CancelCommand, request_id = requestId };

        public static BridgeMessage CreateCommandResult(string requestId, BridgeCommandResult result)
            => new() { message_type = BridgeMessageTypes.CommandResult, request_id = requestId, result = result };
    }

    /// <summary>Identifies a connected Unity Editor or player process.</summary>
    [Serializable]
    public sealed class BridgeProjectHandshake
    {
        public string project_path = string.Empty;
        public string display_name = string.Empty;
        public string unity_version = string.Empty;
        public int editor_process_id;
        public int process_id;
        public string endpoint_kind = BridgeEndpointKinds.Editor;
        public string platform = string.Empty;
        public string build_guid = string.Empty;
        public string cloud_project_id = string.Empty;
        public string company_name = string.Empty;
        public string product_name = string.Empty;
        public bool can_monitor_process = true;
        public bool preserve_snippets;
        public string editor_log_path = string.Empty;
        public string session_instance_id = string.Empty;
        public string handoff_token = string.Empty;
        public string last_seen_utc = string.Empty;

        public string ProjectPath { get => project_path; set => project_path = value; }
        public string DisplayName { get => display_name; set => display_name = value; }
        public string UnityVersion { get => unity_version; set => unity_version = value; }
        public int EditorProcessId { get => editor_process_id; set => editor_process_id = value; }
        public int ProcessId { get => process_id; set => process_id = value; }
        public string EndpointKind { get => endpoint_kind; set => endpoint_kind = value; }
        public string Platform { get => platform; set => platform = value; }
        public string BuildGuid { get => build_guid; set => build_guid = value; }
        public string CloudProjectId { get => cloud_project_id; set => cloud_project_id = value; }
        public string CompanyName { get => company_name; set => company_name = value; }
        public string ProductName { get => product_name; set => product_name = value; }
        public bool CanMonitorProcess { get => can_monitor_process; set => can_monitor_process = value; }
        public bool PreserveSnippets { get => preserve_snippets; set => preserve_snippets = value; }
        public string EditorLogPath { get => editor_log_path; set => editor_log_path = value; }
        public string SessionInstanceId { get => session_instance_id; set => session_instance_id = value; }
        public string HandoffToken { get => handoff_token; set => handoff_token = value; }
        public DateTimeOffset LastSeenUtc
        {
            get => DateTimeOffset.TryParse(last_seen_utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
                ? value
                : default;
            set => last_seen_utc = value == default ? string.Empty : value.ToString("O", CultureInfo.InvariantCulture);
        }

        internal int EffectiveProcessId => process_id > 0 ? process_id : editor_process_id;
        internal bool IsPlayer => endpoint_kind == BridgeEndpointKinds.Player;
    }

    [Serializable]
    sealed class BridgeCommand
    {
        public string command_type = string.Empty;
        public string? target;
        public string? snippet;
        public string? display_name;
        public string? test_filter;
        public bool @async;
        public bool rebuild_cache;
        public bool track_usage;
        public string[] args = Array.Empty<string>();
        public BridgeArtifact[] artifacts = Array.Empty<BridgeArtifact>();

        public string CommandType { get => command_type; set => command_type = value; }
        public string? Target { get => target; set => target = value; }
        public string? Snippet { get => snippet; set => snippet = value; }
        public string? DisplayName { get => display_name; set => display_name = value; }
        public string? TestFilter { get => test_filter; set => test_filter = value; }
        public bool? Async { get => @async ? true : null; set => @async = value == true; }
        public bool? RebuildCache { get => rebuild_cache ? true : null; set => rebuild_cache = value == true; }
        public bool? TrackUsage { get => track_usage ? true : null; set => track_usage = value == true; }
        public string[] Args { get => args; set => args = value; }
        public BridgeArtifact[] Artifacts { get => artifacts; set => artifacts = value; }
    }

    [Serializable]
    sealed class BridgeCommandResult
    {
        public string outcome = ToolOutcome.Success;
        public string logs = string.Empty;
        public string? display_name;
        public string? return_value;
        public BridgeExceptionInfo? exception;
        public string? diagnostic;
        public BridgeArtifact[] artifacts = Array.Empty<BridgeArtifact>();

        public string Outcome { get => outcome; set => outcome = value; }
        public string Logs { get => logs; set => logs = value; }
        public string? DisplayName { get => display_name; set => display_name = value; }
        public string? ReturnValue { get => return_value; set => return_value = value; }
        public BridgeExceptionInfo? Exception { get => exception; set => exception = value; }
        public string? Diagnostic { get => diagnostic; set => diagnostic = value; }
        public BridgeArtifact[] Artifacts { get => artifacts; set => artifacts = value; }

        public static BridgeCommandResult Success(string? value = null)
            => new() { return_value = value };

        public static BridgeCommandResult EditorOnly(string commandName)
            => new()
            {
                outcome = ToolOutcome.Exception,
                diagnostic = $"The tool `{commandName}` is editor-only.",
            };

        public static BridgeCommandResult UnsupportedEditorTool(string commandName)
            => new()
            {
                outcome = ToolOutcome.Exception,
                diagnostic = $"Unity Editor bridge protocol {BridgeContract.Version} does not support the `{commandName}` tool.",
            };

        public static BridgeCommandResult FromException(Exception exception)
        {
            var info = BridgeExceptionFormatter.ToInfo(exception);
            return new()
            {
                outcome = ToolOutcome.Exception,
                exception = info,
                diagnostic = BridgeExceptionFormatter.NormalizeDiagnostic(exception.Message, info.message),
            };
        }
    }

    [Serializable]
    sealed class BridgeArtifact
    {
        public string name = string.Empty;
        public string media_type = "application/octet-stream";
        public string sha256 = string.Empty;
        public long length;
        public string? relative_path;

        public string Name { get => name; set => name = value; }
        public string MediaType { get => media_type; set => media_type = value; }
        public string Sha256 { get => sha256; set => sha256 = value; }
        public long Length { get => length; set => length = value; }
        public string? RelativePath { get => relative_path; set => relative_path = value; }

        internal byte[]? Content { get; set; }
        internal string? ResolvedPath { get; set; }

        public static BridgeArtifact FromBytes(string name, string mediaType, byte[] bytes)
            => new()
            {
                name = name,
                media_type = mediaType,
                sha256 = ComputeSha256(bytes),
                length = bytes.LongLength,
                Content = bytes,
            };

        public static BridgeArtifact FromProjectFile(string name, string mediaType, string relativePath, byte[] bytes)
            => new()
            {
                name = name,
                media_type = mediaType,
                sha256 = ComputeSha256(bytes),
                length = bytes.LongLength,
                relative_path = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            };

        internal void MaterializeInEndpoint(string endpointDirectory)
        {
            if (Content is not { } bytes)
                throw new InvalidOperationException($"Artifact '{name}' has no content to materialize.");

            Verify(bytes);
            var directory = Path.Combine(endpointDirectory, "artifacts");
            Directory.CreateDirectory(directory);
            RejectLink(directory);
            var fileName = sha256 + GetSafeExtension(name);
            var path = Path.Combine(directory, fileName);
            WriteAtomically(path, bytes, sha256);
            // endpoint-relative paths survive host/container path translation in pressure-vessel.
            relative_path = "artifacts/" + fileName;
            ResolvedPath = path;
            Content = null;
        }

        internal void ResolveInEndpoint(string endpointDirectory)
        {
            if (string.IsNullOrWhiteSpace(relative_path) || Path.IsPathRooted(relative_path))
                throw new InvalidDataException($"Artifact '{name}' must use an endpoint-relative path.");
            if (length < 0 || sha256.Length != 64)
                throw new InvalidDataException($"Artifact '{name}' has invalid verification metadata.");

            foreach (var character in sha256)
                if (!Uri.IsHexDigit(character))
                    throw new InvalidDataException($"Artifact '{name}' has an invalid SHA-256 value.");

            var fileName = sha256.ToLowerInvariant() + GetSafeExtension(name);
            var normalizedRelativePath = relative_path.Replace('/', Path.DirectorySeparatorChar);
            var expectedRelativePath = Path.Combine("artifacts", fileName);
            if (!string.Equals(
                    normalizedRelativePath,
                    expectedRelativePath,
                    StringComparison.Ordinal
                ))
                throw new InvalidDataException($"Artifact '{name}' has an unexpected endpoint path.");

            var artifactRoot = Path.GetFullPath(Path.Combine(endpointDirectory, "artifacts"));
            RejectLink(artifactRoot);
            ResolvedPath = Path.Combine(artifactRoot, fileName);
        }

        internal void ResolveInProject(string projectDirectory)
        {
            if (string.IsNullOrWhiteSpace(relative_path) || Path.IsPathRooted(relative_path))
                throw new InvalidDataException($"Artifact '{name}' must use a project-relative path.");

            var projectRoot = Path.GetFullPath(projectDirectory);
            var path = Path.GetFullPath(Path.Combine(projectRoot, relative_path));
            var normalized = Path.GetRelativePath(projectRoot, path);
            if (Path.IsPathRooted(normalized)
                || normalized == ".."
                || normalized.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"Artifact '{name}' escaped its Unity project.");

            ResolvedPath = path;
        }

        internal byte[] ReadVerified()
        {
            byte[] bytes;
            if (Content is { } content)
                bytes = content;
            else if (ResolvedPath is { } path)
            {
                RejectLink(path);
                bytes = File.ReadAllBytes(path);
            }
            else
                throw new InvalidOperationException($"Artifact '{name}' has no resolved content.");

            Verify(bytes);
            return bytes;
        }

        internal void Verify(byte[] bytes)
        {
            if (bytes.LongLength != length)
                throw new InvalidDataException($"Artifact '{name}' failed length verification.");
            if (!string.Equals(ComputeSha256(bytes), sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Artifact '{name}' failed SHA-256 verification.");
        }

        static string GetSafeExtension(string artifactName)
        {
            var extension = Path.GetExtension(Path.GetFileName(artifactName));
            if (extension.Length is 0 or > 16)
                return string.Empty;

            for (var index = 1; index < extension.Length; index++)
                if (!char.IsLetterOrDigit(extension[index]))
                    return string.Empty;

            return extension.ToLowerInvariant();
        }

        static void WriteAtomically(string path, byte[] bytes, string expectedSha256)
        {
            if (File.Exists(path))
            {
                VerifyFile(path, bytes.LongLength, expectedSha256);
                return;
            }

            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, bytes);
                try
                {
                    File.Move(temporaryPath, path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // another connection may have staged the same content-addressed artifact.
                    VerifyFile(path, bytes.LongLength, expectedSha256);
                }
            }
            finally
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }

        static void VerifyFile(string path, long expectedLength, string expectedSha256)
        {
            RejectLink(path);
            if (new FileInfo(path).Length != expectedLength)
                throw new InvalidDataException($"Shared artifact '{path}' has an unexpected length.");

            using var stream = File.OpenRead(path);
            using var algorithm = SHA256.Create();
            var actualSha256 = FormatSha256(algorithm.ComputeHash(stream));
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Shared artifact '{path}' failed SHA-256 verification.");
        }

        static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Shared artifact path '{path}' cannot be a symbolic link.");
        }

        internal static string ComputeSha256(byte[] bytes)
        {
            using var algorithm = SHA256.Create();
            return FormatSha256(algorithm.ComputeHash(bytes));
        }

        static string FormatSha256(byte[] hash)
        {
            var characters = new char[hash.Length * 2];
            const string alphabet = "0123456789abcdef";
            for (var index = 0; index < hash.Length; index++)
            {
                characters[index * 2] = alphabet[hash[index] >> 4];
                characters[index * 2 + 1] = alphabet[hash[index] & 0xf];
            }

            return new string(characters);
        }
    }

    [Serializable]
    sealed class BridgeExceptionInfo
    {
        public string type = string.Empty;
        public string message = string.Empty;
        public string? stack_trace;

        public string Type { get => type; set => type = value; }
        public string Message { get => message; set => message = value; }
        public string? StackTrace { get => stack_trace; set => stack_trace = value; }
    }

    [Serializable]
    sealed class BridgeEndpointDescriptor
    {
        public int protocol_version = BridgeContract.Version;
        public string endpoint_kind = string.Empty;
        public string transport = string.Empty;
        public string endpoint_id = string.Empty;
        public string pipe_name = string.Empty;
        public int process_id;
        public string session_instance_id = string.Empty;
        public string handoff_token = string.Empty;
        public string unity_version = string.Empty;
        public string platform = string.Empty;
        public string build_guid = string.Empty;
        public string cloud_project_id = string.Empty;
        public string company_name = string.Empty;
        public string product_name = string.Empty;
        public string started_utc = string.Empty;
        public string last_seen_utc = string.Empty;
        public bool can_monitor_process;
        public bool is_test_player;

        public int ProtocolVersion { get => protocol_version; set => protocol_version = value; }
        public string EndpointKind { get => endpoint_kind; set => endpoint_kind = value; }
        public string Transport { get => transport; set => transport = value; }
        public string EndpointId { get => endpoint_id; set => endpoint_id = value; }
        public string PipeName { get => pipe_name; set => pipe_name = value; }
        public int ProcessId { get => process_id; set => process_id = value; }
        public string SessionInstanceId { get => session_instance_id; set => session_instance_id = value; }
        public string HandoffToken { get => handoff_token; set => handoff_token = value; }
        public string UnityVersion { get => unity_version; set => unity_version = value; }
        public string Platform { get => platform; set => platform = value; }
        public string BuildGuid { get => build_guid; set => build_guid = value; }
        public string CloudProjectId { get => cloud_project_id; set => cloud_project_id = value; }
        public string CompanyName { get => company_name; set => company_name = value; }
        public string ProductName { get => product_name; set => product_name = value; }
        public string StartedUtc { get => started_utc; set => started_utc = value; }
        public string LastSeenUtc { get => last_seen_utc; set => last_seen_utc = value; }
        public bool CanMonitorProcess { get => can_monitor_process; set => can_monitor_process = value; }
        public bool IsTestPlayer { get => is_test_player; set => is_test_player = value; }

        internal string EndpointDirectoryPath { get; set; } = string.Empty;
        internal string Selector => $"player:{process_id.ToString(CultureInfo.InvariantCulture)}";
    }

    [Serializable]
    sealed class BridgeAssemblyReferenceManifest
    {
        public BridgeAssemblyReference[] references = Array.Empty<BridgeAssemblyReference>();
        public BridgeAssemblyReference[] References { get => references; set => references = value; }
    }

    [Serializable]
    sealed class BridgeAssemblyReference
    {
        public string id = string.Empty;
        public string assembly_name = string.Empty;
        public string path = string.Empty;
        public long length;

        public string Id { get => id; set => id = value; }
        public string AssemblyName { get => assembly_name; set => assembly_name = value; }
        public string Path { get => path; set => path = value; }
        public long Length { get => length; set => length = value; }
    }
}
