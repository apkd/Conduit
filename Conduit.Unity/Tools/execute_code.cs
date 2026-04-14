#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;

namespace Conduit
{
    static class execute_code
    {
        const string SnippetNamespace = "ConduitGenerated.ExecuteCode";
        const string SnippetTempDirectoryName = "execute_code";
        static readonly string linqUsingDirective = string.Concat("using System.", "Linq;");
        static readonly object cacheGate = new();
        static bool initialized;
        static int nextSnippetArtifactId;
        static CachedAdditionalReferences? additionalReferencesCache;
        static readonly Dictionary<string, CachedSnippetCompilation> compilationCache = new(StringComparer.Ordinal);

        static readonly HashSet<string> defaultUsingDirectives = new(StringComparer.Ordinal)
        {
            "using System;",
            "using System.Collections.Generic;",
            "using System.IO;",
            linqUsingDirective,
            "using System.Threading.Tasks;",
            "using UnityEditor;",
            "using UnityEngine;",
        };

        public static async Task<BridgeCommandResult> ExecuteAsync(PendingOperationState operation)
        {
            try
            {
                Initialize();
                var snippetText = operation.snippet ?? string.Empty;
                if (TryGetCachedCompilation(snippetText, out var cachedCompilation))
                    return await ExecuteCachedCompilationAsync(cachedCompilation);

                var parsedSnippet = ConduitCodeParser.Parse(snippetText);
                var projectPath = GetCurrentProjectPath();
                var snippetArtifactId = AllocateSnippetArtifactId();
                var typeName = $"SnippetHost_{snippetArtifactId}";
                var fullTypeName = $"{SnippetNamespace}.{typeName}";
                var snippetRootPath = GetSnippetRootPath(projectPath);
                var sourceFileName = snippetArtifactId + ".cs";
                var displaySourcePath = sourceFileName;
                var sourceFilePath = Path.Combine(snippetRootPath, sourceFileName);
                var assemblyPath = Path.Combine(snippetRootPath, snippetArtifactId + ".dll");

                Directory.CreateDirectory(snippetRootPath);
                File.WriteAllText(
                    sourceFilePath,
                    BuildSnippetSource(typeName, displaySourcePath, parsedSnippet)
                );

                var compilerMessages = await CompileAssemblyAsync(
                    projectPath,
                    snippetRootPath,
                    sourceFilePath,
                    assemblyPath
                );
                var compilation = CacheCompilation(
                    snippetText,
                    fullTypeName,
                    assemblyPath,
                    compilerMessages
                );
                return await ExecuteCachedCompilationAsync(compilation);
            }
            catch (SnippetParseException exception)
            {
                var exceptionInfo = ConduitUtility.ToExceptionInfo(exception);
                return new()
                {
                    outcome = ToolOutcome.CompileError,
                    diagnostic = ConduitUtility.NormalizeDiagnostic(exception.Message, exceptionInfo.message),
                    exception = exceptionInfo,
                };
            }
            catch (Exception exception)
            {
                var exceptionInfo = ConduitUtility.ToExceptionInfo(exception);
                return new()
                {
                    outcome = ToolOutcome.Exception,
                    exception = exceptionInfo,
                    diagnostic = ConduitUtility.NormalizeDiagnostic(exception.Message, exceptionInfo.message),
                };
            }
        }

        internal static void Initialize()
        {
            if (initialized)
                return;

            initialized = true;
            CleanupGeneratedFiles();
        }

        static void CleanupGeneratedFiles()
        {
            lock (cacheGate)
                nextSnippetArtifactId = 0;

            var projectPath = GetCurrentProjectPath();
            DeleteDirectoryIfPresent(GetSnippetRootPath(projectPath));
            DeleteDirectoryIfPresent(Path.Combine(projectPath, "Library", "Conduit", "ExecuteCode"));
            DeleteDirectoryIfPresent(Path.Combine(projectPath, "Library", "Conduit", "ExecuteSnippet"));
        }

        static void DeleteDirectoryIfPresent(string directoryPath)
        {
            try
            {
                if (Directory.Exists(directoryPath))
                    Directory.Delete(directoryPath, true);
            }
            catch { }
        }

        static async Task<CompilerMessage[]> CompileAssemblyAsync(
            string projectPath,
            string snippetRootPath,
            string sourceFilePath,
            string assemblyPath
        )
        {
            var completion = new TaskCompletionSource<CompilerMessage[]>();
#pragma warning disable CS0618 // Type or member is obsolete
            var builder = new AssemblyBuilder(
#pragma warning restore CS0618
                ToProjectRelativePath(projectPath, assemblyPath),
                ToProjectRelativePath(projectPath, sourceFilePath)
            )
            {
                flags = AssemblyBuilderFlags.EditorAssembly,
                referencesOptions = ReferencesOptions.UseEngineModules,
                additionalReferences = GetAdditionalReferences(projectPath, snippetRootPath),
            };

            void OnBuildFinished(string _, CompilerMessage[] messages)
            {
                builder.buildFinished -= OnBuildFinished;
                completion.TrySetResult(messages ?? Array.Empty<CompilerMessage>());
            }

            builder.buildFinished += OnBuildFinished;

            try
            {
                if (!builder.Build())
                    throw new InvalidOperationException("Unity is already compiling scripts, so execute_code could not start.");
            }
            catch
            {
                builder.buildFinished -= OnBuildFinished;
                throw;
            }

            return await completion.Task;
        }

        internal static string[] GetAdditionalReferences(string projectPath, string snippetRootPath)
        {
            lock (cacheGate)
            {
                if (additionalReferencesCache is { } cachedReferences
                    && string.Equals(cachedReferences.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cachedReferences.SnippetRootPath, snippetRootPath, StringComparison.OrdinalIgnoreCase))
                    return cachedReferences.References;
            }

            var normalizedProjectPath = NormalizeDirectoryPath(projectPath);
            var normalizedSnippetRootPath = NormalizeDirectoryPath(snippetRootPath);
            using var pooledReferences = ConduitUtility.GetPooledList<string>(out var references);
            using var pooledSeen = ConduitUtility.GetPooledSet<string>(out var seen);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                if (assembly.Location is not { Length: > 0 } location)
                    continue;

                location = Path.GetFullPath(location);
                if (!location.StartsWith(normalizedProjectPath, StringComparison.OrdinalIgnoreCase)
                    || location.StartsWith(normalizedSnippetRootPath, StringComparison.OrdinalIgnoreCase)
                    || !location.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ToProjectRelativePath(projectPath, location) is { Length: > 0 } relativePath)
                    if (seen.Add(relativePath))
                        references.Add(relativePath);
            }

            var resolvedReferences = references.ToArray();
            lock (cacheGate)
            {
                additionalReferencesCache = new()
                {
                    ProjectPath = projectPath,
                    SnippetRootPath = snippetRootPath,
                    References = resolvedReferences,
                };
            }

            return resolvedReferences;
        }

        [HideInCallstack]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static async Task<object?> InvokeAsync(byte[] assemblyBytes, string fullTypeName)
        {
            var assembly = Assembly.Load(assemblyBytes);
            var snippetType = assembly.GetType(fullTypeName, true)
                              ?? throw new InvalidOperationException($"Generated snippet type '{fullTypeName}' was not found.");
            var method = snippetType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                throw new InvalidOperationException($"Generated snippet entry point '{fullTypeName}.Execute' was not found.");

            var invocationResult = method.Invoke(null, null);
            if (invocationResult is not Task task)
                return invocationResult;

            await task;

            if (!method.ReturnType.IsGenericType)
                return null;

            return method.ReturnType
                .GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(invocationResult, null);
        }

        static async Task<BridgeCommandResult> ExecuteCachedCompilationAsync(CachedSnippetCompilation compilation)
        {
            if (compilation.CompileErrorDiagnostic is { } compileErrorDiagnostic)
            {
                return new()
                {
                    outcome = ToolOutcome.CompileError,
                    diagnostic = compileErrorDiagnostic,
                };
            }

            var returnValue = await InvokeAsync(
                compilation.AssemblyBytes
                ?? throw new InvalidOperationException($"Cached assembly bytes for '{compilation.FullTypeName}' were not available."),
                compilation.FullTypeName
            );
            return new()
            {
                outcome = ToolOutcome.Success,
                return_value = ConduitUtility.Stringify(returnValue),
                diagnostic = compilation.WarningDiagnostic,
            };
        }

        static bool TryGetCachedCompilation(string snippetText, out CachedSnippetCompilation compilation)
        {
            lock (cacheGate)
                return compilationCache.TryGetValue(snippetText, out compilation!);
        }

        static CachedSnippetCompilation CacheCompilation(
            string snippetText,
            string fullTypeName,
            string assemblyPath,
            CompilerMessage[] compilerMessages
        )
        {
            var compileErrorDiagnostic = FormatCompilerMessages(FilterCompilerMessages(compilerMessages, CompilerMessageType.Error));
            var warningDiagnostic = FormatCompilerMessages(FilterCompilerMessages(compilerMessages, CompilerMessageType.Warning));
            var compilation = new CachedSnippetCompilation
            {
                FullTypeName = fullTypeName,
                AssemblyBytes = string.IsNullOrWhiteSpace(compileErrorDiagnostic) ? File.ReadAllBytes(assemblyPath) : null,
                CompileErrorDiagnostic = string.IsNullOrWhiteSpace(compileErrorDiagnostic) ? null : compileErrorDiagnostic,
                WarningDiagnostic = string.IsNullOrWhiteSpace(warningDiagnostic) ? null : warningDiagnostic,
            };

            lock (cacheGate)
                compilationCache[snippetText] = compilation;

            return compilation;
        }

        internal static string AllocateSnippetArtifactId()
        {
            lock (cacheGate)
                return (++nextSnippetArtifactId).ToString(CultureInfo.InvariantCulture);
        }

        static string BuildSnippetSource(string typeName, string displaySourcePath, SnippetParseResult parsedSnippet)
        {
            var builder = new StringBuilder();
            builder.AppendLine("using System;");
            builder.AppendLine("using System.Collections.Generic;");
            builder.AppendLine("using System.IO;");
            builder.AppendLine(linqUsingDirective);
            builder.AppendLine("using System.Threading.Tasks;");
            builder.AppendLine("using UnityEditor;");
            builder.AppendLine("using UnityEngine;");
            foreach (var usingDirective in parsedSnippet.Usings)
            {
                if (defaultUsingDirectives.Contains(usingDirective.Text.Trim()))
                    continue;

                AppendChunk(builder, usingDirective, displaySourcePath);
            }

            builder.AppendLine();
            builder.AppendLine("#pragma warning disable CS0162, CS1998");
            builder.AppendLine($"namespace {SnippetNamespace}");
            builder.AppendLine("{");
            foreach (var typeDeclaration in parsedSnippet.TypeDeclarations)
                AppendChunk(builder, typeDeclaration, displaySourcePath);

            builder.AppendLine($"    public static class {typeName}");
            builder.AppendLine("    {");
            foreach (var staticField in parsedSnippet.StaticFields)
                AppendChunk(builder, staticField, displaySourcePath);

            builder.AppendLine("        [HideInCallstack]");
            builder.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            builder.AppendLine("        public static async Task<object> Execute()");
            builder.AppendLine("        {");
            AppendChunk(builder, parsedSnippet.Body, displaySourcePath);
            builder.AppendLine("#line hidden");
            builder.AppendLine("            return null;");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            builder.AppendLine("#pragma warning restore CS0162, CS1998");
            return builder.ToString();
        }

        static void AppendChunk(StringBuilder builder, SnippetChunk chunk, string displaySourcePath)
        {
            if (chunk.Text is not { Length: > 0 })
                return;

            builder.AppendLine($"#line {Math.Max(1, chunk.StartLine)} \"{displaySourcePath.Replace("\\", "\\\\")}\"");
            builder.Append(chunk.Text);
            if (chunk.Text[^1] != '\n')
                builder.AppendLine();

            builder.AppendLine("#line default");
        }

        static CompilerMessage[] FilterCompilerMessages(CompilerMessage[] messages, CompilerMessageType type)
        {
            using var pooledFiltered = ConduitUtility.GetPooledList<CompilerMessage>(out var filtered);
            foreach (var message in messages)
                if (message.type == type)
                    filtered.Add(message);

            return filtered.Count == 0 ? Array.Empty<CompilerMessage>() : filtered.ToArray();
        }

        internal static string FormatCompilerMessages(IEnumerable<CompilerMessage> messages)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            foreach (var message in messages)
            {
                builder.Append('[');
                builder.Append(message.type == CompilerMessageType.Error ? "Error" : "Warning");
                builder.Append("] ");

                if (!string.IsNullOrWhiteSpace(message.file) && !CompilerMessageAlreadyIncludesLocation(message))
                {
                    builder.Append(message.file);
                    if (message.line > 0)
                    {
                        builder.Append('(');
                        builder.Append(message.line);
                        if (message.column > 0)
                        {
                            builder.Append(',');
                            builder.Append(message.column);
                        }

                        builder.Append(')');
                    }

                    builder.Append(": ");
                }

                builder.AppendLine(message.message);
            }

            return builder.Trim().ToString();
        }

        static bool CompilerMessageAlreadyIncludesLocation(CompilerMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.file) || string.IsNullOrWhiteSpace(message.message))
                return false;

            var normalizedFile = message.file.Replace('\\', '/');
            var normalizedMessage = message.message.TrimStart().Replace('\\', '/');
            return normalizedMessage.StartsWith(normalizedFile, StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetCurrentProjectPath()
            => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        internal static string GetSnippetRootPath(string projectPath)
            => Path.Combine(projectPath, "Temp", SnippetTempDirectoryName);

        static string NormalizeDirectoryPath(string path)
        {
            var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath + Path.DirectorySeparatorChar;
        }

        static string ToProjectRelativePath(string projectPath, string absolutePath)
            => Path.GetRelativePath(projectPath, absolutePath)
                .Replace(Path.DirectorySeparatorChar, '/');

        sealed class CachedAdditionalReferences
        {
            public string ProjectPath = string.Empty;
            public string SnippetRootPath = string.Empty;
            public string[] References = Array.Empty<string>();
        }

        sealed class CachedSnippetCompilation
        {
            public string FullTypeName = string.Empty;
            public byte[]? AssemblyBytes;
            public string? CompileErrorDiagnostic;
            public string? WarningDiagnostic;
        }
    }
}
