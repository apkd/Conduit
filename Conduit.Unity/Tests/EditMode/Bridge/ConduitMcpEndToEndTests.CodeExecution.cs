#nullable enable

#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading.Tasks;
using Conduit;
using NUnit.Framework;
using UnityEditor;

public sealed partial class ConduitMcpEndToEndTests
{
    [Test]
    [Order(14)]
    public async Task ExecuteCode_AutoInfersMissingNamespaceAndKeepsSuccessSilent()
    {
        const string snippet = "return BindingFlags.Public.ToString();";

        var first = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(
                ("projectPath", projectPath),
                ("snippet", snippet)
            )
        );

        var second = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(
                ("projectPath", projectPath),
                ("snippet", snippet)
            )
        );

        AssertSuccessful(first, "Public");
        AssertSuccessful(second, "Public");
        Assert.That(first.Text, Does.Not.Contain("Retried with inferred namespaces"));
        Assert.That(second.Text, Does.Not.Contain("Retried with inferred namespaces"));
    }

    [Test]
    [Order(15)]
    public async Task ExecuteCode_AutoInfersMultipleNamespacesInSingleRetry()
    {
        const string snippet = "return Regex.IsMatch(typeof(MethodInfo).Name, \"^Method\").ToString();";

        var result = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(
                ("projectPath", projectPath),
                ("snippet", snippet)
            )
        );

        AssertSuccessful(result, "true");
        Assert.That(result.Text, Does.Not.Contain("Retried with inferred namespaces"));
    }

    [Test]
    [Order(16)]
    public async Task ExecuteCode_ImportsConduitHelpers()
    {
        const string snippet = "return Search<Camera>(\"Main Camera\").name + \":\" + Reflect.Type(\"UnityEngine.Camera\").Name;";

        var result = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(
                ("projectPath", projectPath),
                ("snippet", snippet)
            )
        );

        AssertSuccessful(result, "Main Camera:Camera");
    }

    [Test]
    [Order(16)]
    public async Task ExecuteCode_SelectsSyncWrapperUnlessTopLevelAwaitRequiresAsync()
    {
        const string refSnippet
            = "var value = 1; ref var alias = ref value; alias = 7; return value;";
        const string nestedAsyncSnippet
            = "async Task TouchAsync() { await Task.CompletedTask; }\n"
              + "TouchAsync().GetAwaiter().GetResult();\n"
              + "var value = 2; ref var alias = ref value; alias = 8; return value;";
        const string topLevelAwaitSnippet
            = "await Task.Yield();\n"
              + "if (false) return;\n"
              + "return BindingFlags.Public.ToString();";
        const string asyncNoResultSnippet = "await Task.Yield(); return;";

        var refResult = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(("projectPath", projectPath), ("snippet", refSnippet))
        );
        var nestedAsyncResult = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(("projectPath", projectPath), ("snippet", nestedAsyncSnippet))
        );
        var topLevelAwaitResult = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(("projectPath", projectPath), ("snippet", topLevelAwaitSnippet))
        );
        var asyncNoResult = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(("projectPath", projectPath), ("snippet", asyncNoResultSnippet))
        );

        AssertSuccessful(refResult, "7");
        AssertSuccessful(nestedAsyncResult, "8");
        AssertSuccessful(topLevelAwaitResult, "Public");
        AssertSuccessful(asyncNoResult);
    }

    [Test]
    [Order(16)]
    public async Task ExecuteCode_RerunsNamedSnippetWithoutRecompiling()
    {
        var counterPath = Path.Combine(Path.GetTempPath(), $"ConduitExecuteCodeCounter_{Guid.NewGuid():N}.txt");
        var snippet
            = "var p = @\""
              + counterPath.Replace("\"", "\"\"")
              + "\"; var n = File.Exists(p) ? int.Parse(File.ReadAllText(p)) : 0; "
              + "File.WriteAllText(p, (++n).ToString()); return n;";
        try
        {
            var first = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", snippet)
                )
            );
            var snippetFileName = GetSnippetFileName(first.Text);
            var assemblyPath = Path.Combine(
                editorProjectPath,
                ConduitSnippetStorage.PreserveSnippets ? "Library" : "Temp",
                "Conduit",
                Path.ChangeExtension(snippetFileName, ".dll")
            );
            Assert.That(File.Exists(assemblyPath), Is.True, assemblyPath);
            var assemblyWriteTime = File.GetLastWriteTimeUtc(assemblyPath);

            var rerun = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", snippetFileName)
                )
            );
            var missing = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", "2147483647.cs")
                )
            );

            Assert.That(first.Text, Is.EqualTo($"NAME: `{snippetFileName}`\n1"));
            Assert.That(rerun.Text, Is.EqualTo($"NAME: `{snippetFileName}`\n2"));
            Assert.That(File.GetLastWriteTimeUtc(assemblyPath), Is.EqualTo(assemblyWriteTime));
            Assert.That(missing.Text, Does.Contain("was not found"));
            Assert.That(missing.Text, Does.Not.StartWith("NAME: `"));
        }
        finally
        {
            if (File.Exists(counterPath))
                File.Delete(counterPath);
        }
    }

    [Test]
    [Order(16)]
    public async Task ExecuteCode_PreservesSnippetsWhenEnabled()
    {
        bool hadPreference = EditorPrefs.HasKey(ConduitSnippetStorage.PreservePreferenceKey);
        bool previousPreference = ConduitSnippetStorage.PreserveSnippets;
        string? snippetFileName = null;
        var storageRoot = Path.Combine(editorProjectPath, "Library", "Conduit");
        try
        {
            ConduitSnippetStorage.PreserveSnippets = true;
            var marker = Guid.NewGuid().ToString("N");
            var result = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", $"return \"{marker}\";")
                )
            );
            snippetFileName = GetSnippetFileName(result.Text);

            AssertSuccessful(result, marker);
            Assert.That(File.Exists(Path.Combine(storageRoot, snippetFileName)), Is.True);
            Assert.That(
                File.Exists(
                    Path.Combine(
                        storageRoot,
                        Path.ChangeExtension(snippetFileName, ".dll")
                    )
                ),
                Is.True
            );
        }
        finally
        {
            if (hadPreference)
                ConduitSnippetStorage.PreserveSnippets = previousPreference;
            else
            {
                ConduitSnippetStorage.PreserveSnippets = false;
                EditorPrefs.DeleteKey(ConduitSnippetStorage.PreservePreferenceKey);
            }

            if (snippetFileName is not null)
                foreach (var extension in new[] { ".cs", ".dll", ".pdb" })
                    File.Delete(
                        Path.Combine(
                            storageRoot,
                            Path.ChangeExtension(snippetFileName, extension)
                        )
                    );
        }
    }

    [Test]
    [Order(16)]
    public async Task ExecuteCode_InvokesServerCompiledSnippetWithoutUnityCompilation()
    {
        var marker = Guid.NewGuid().ToString("N");
        var snippet
            = $"return \"{marker}:\" + Environment.StackTrace.Contains(\"UnityEditor.Compilation.AssemblyBuilder\").ToString();";

        var result = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(
                ("projectPath", projectPath),
                ("snippet", snippet)
            )
        );

        AssertSuccessful(result, marker + ":False");
    }

    [Test]
    [Order(16)]
    public async Task ExecuteCode_SupportsBareReturnsWithoutMaskingNestedErrors()
    {
        const string mixedSnippet
            = "var g = GameObject.Find(\"Main Camera\");\n"
              + "if (!g)\n"
              + "    return;\n"
              + "return BindingFlags.Public + \":\" + g.name;";
        const string noResultSnippet
            = "if (GameObject.Find(\"Main Camera\"))\n"
              + "    return;\n"
              + "throw new InvalidOperationException();";

        var mixed = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(
                ("projectPath", projectPath),
                ("snippet", mixedSnippet)
            )
        );
        var cachedMixed = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(
                ("projectPath", projectPath),
                ("snippet", mixedSnippet)
            )
        );
        var noResult = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(
                ("projectPath", projectPath),
                ("snippet", noResultSnippet)
            )
        );
        var nestedFailure = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(
                ("projectPath", projectPath),
                ("snippet", "object Broken() { return; }\nreturn Broken();")
            )
        );
        var lambdaFailure = await client.CallToolAsync(
            BridgeCommandTypes.ExecuteCode,
            Args(
                ("projectPath", projectPath),
                ("snippet", "Func<object> broken = () => { return; };\nreturn broken();")
            )
        );

        AssertSuccessful(mixed, "Public:Main Camera");
        AssertSuccessful(cachedMixed, "Public:Main Camera");
        AssertSuccessful(noResult);
        Assert.That(noResult.Text, Is.EqualTo($"NAME: `{GetSnippetFileName(noResult.Text)}`"));
        Assert.That(nestedFailure.Text, Does.Contain("CS0126"));
        Assert.That(lambdaFailure.Text, Does.Contain("CS0126"));
        Assert.That(nestedFailure.Text, Does.Not.StartWith("NAME: `"));
        Assert.That(lambdaFailure.Text, Does.Not.StartWith("NAME: `"));
    }

    [Test]
    [Order(16)]
    public async Task ExecuteCode_CoversSuccessCacheRuntimeFailureAndCompileFailure()
    {
        var runtimeTogglePath = Path.Combine(Path.GetTempPath(), $"ConduitExecuteCode_{Guid.NewGuid():N}.flag");
        var snippet
            = "return File.Exists(@\""
              + runtimeTogglePath.Replace("\"", "\"\"")
              + "\")"
              + " ? System.Int32.Parse(\"abc\")"
              + " : System.Math.Abs(-5);";
        try
        {
            var success = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", snippet)
                )
            );

            var cachedSuccess = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", snippet)
                )
            );

            File.WriteAllText(runtimeTogglePath, string.Empty);
            var snippetFileName = GetSnippetFileName(success.Text);
            var runtimeFailure = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", snippetFileName)
                )
            );

            var compileFailure = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", "namespace Rejected { }")
                )
            );
            var unsupportedCompileFailure = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", "return bindingFlags;")
                )
            );
            var retryFailure = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", "return BindingFlags.MissingMember.ToString();")
                )
            );

            AssertSuccessful(success, "5");
            AssertSuccessful(cachedSuccess, "5");
            Assert.That(GetSnippetFileName(cachedSuccess.Text), Is.EqualTo(snippetFileName));
            Assert.That(GetSnippetFileName(runtimeFailure.Text), Is.EqualTo(snippetFileName));
            AssertTextContainsAny(runtimeFailure.Text, "FormatException", "Input string");
            AssertTextContainsAny(compileFailure.Text, "Namespace declarations are not supported", "execute_code(");
            Assert.That(compileFailure.Text, Does.Not.StartWith("NAME: `"));
            Assert.That(unsupportedCompileFailure.Text, Does.Contain("bindingFlags"));
            Assert.That(unsupportedCompileFailure.Text, Does.Not.Contain("Retried with inferred namespaces"));
            Assert.That(retryFailure.Text, Does.Contain("Retried with inferred namespaces: System.Reflection."));
            Assert.That(retryFailure.Text, Does.Contain("MissingMember"));
        }
        finally
        {
            if (File.Exists(runtimeTogglePath))
                File.Delete(runtimeTogglePath);
        }
    }

    [Test]
    [Order(17)]
    public async Task ScriptFilesSupportArbitraryNamesAndEditsAcrossCodeTools()
    {
        const string methodName = "ConduitMcpEndToEndTests.DetourProbe";
        var marker = Guid.NewGuid().ToString("N");
        var storageRoot = Path.Combine(
            editorProjectPath,
            ConduitSnippetStorage.PreserveSnippets ? "Library" : "Temp",
            "Conduit"
        );
        var customFileName = $"Shared Script {marker}.cs";
        var customPath = Path.Combine(storageRoot, customFileName);
        Directory.CreateDirectory(storageRoot);
        File.WriteAllText(customPath, $"return 303; // {marker}");

        try
        {
            var customExecution = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(("projectPath", projectPath), ("snippet", customFileName))
            );
            AssertSuccessful(customExecution, "303");
            Assert.That(customExecution.Text, Does.StartWith($"NAME: `{customFileName}`\n"));

            var customDetour = await CallDetourAsync(methodName, customFileName);
            AssertSuccessful(customDetour, "Detoured", methodName);
            Assert.That(DetourProbe(1), Is.EqualTo(303));
            AssertSuccessful(
                await CallDetourAsync(methodName, "restore"),
                "Restored the original implementation"
            );

            // both tools must discard results derived from the first version of this shared file.
            File.WriteAllText(customPath, $"return 304; // {marker}");
            var editedExecution = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(("projectPath", projectPath), ("snippet", customFileName))
            );
            var editedDetour = await CallDetourAsync(methodName, customFileName);
            AssertSuccessful(editedExecution, "304");
            AssertSuccessful(editedDetour, "Detoured", methodName);
            Assert.That(DetourProbe(1), Is.EqualTo(304));
            AssertSuccessful(
                await CallDetourAsync(methodName, "restore"),
                "Restored the original implementation"
            );

            var executeSource = $"return 101; // {Guid.NewGuid():N}";
            var executeResult = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(("projectPath", projectPath), ("snippet", executeSource))
            );
            var executeFileName = GetSnippetFileName(executeResult.Text);
            var executeDetour = await CallDetourAsync(methodName, executeFileName);
            AssertSuccessful(executeDetour, "Detoured", methodName);
            Assert.That(DetourProbe(1), Is.EqualTo(101));
            AssertSuccessful(
                await CallDetourAsync(methodName, "restore"),
                "Restored the original implementation"
            );

            var detourSource = $"return 202; // {Guid.NewGuid():N}";
            var detourResult = await CallDetourAsync(methodName, detourSource);
            AssertSuccessful(detourResult, "Detoured", methodName);
            var detourFileName = GetSnippetFileName(detourResult.Text);
            Assert.That(DetourProbe(1), Is.EqualTo(202));
            AssertSuccessful(
                await CallDetourAsync(methodName, "restore"),
                "Restored the original implementation"
            );

            var detourExecution = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(("projectPath", projectPath), ("snippet", detourFileName))
            );
            AssertSuccessful(detourExecution, "202");
        }
        finally
        {
            await CallDetourAsync(methodName, "restore");
            File.Delete(customPath);
        }
    }

}
#endif
