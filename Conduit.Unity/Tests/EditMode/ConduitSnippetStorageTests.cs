#nullable enable

using Conduit;
using NUnit.Framework;
using UnityEditor;

public sealed class ConduitSnippetStorageTests
{
    bool hadPreference;
    bool originalPreference;

    [SetUp]
    public void SetUp()
    {
        hadPreference = EditorPrefs.HasKey(ConduitSnippetStorage.PreservePreferenceKey);
        originalPreference = ConduitSnippetStorage.PreserveSnippets;
        ConduitSnippetStorage.PreserveSnippets = false;
        EditorPrefs.DeleteKey(ConduitSnippetStorage.PreservePreferenceKey);
    }

    [TearDown]
    public void TearDown()
    {
        if (hadPreference)
            ConduitSnippetStorage.PreserveSnippets = originalPreference;
        else
        {
            ConduitSnippetStorage.PreserveSnippets = false;
            EditorPrefs.DeleteKey(ConduitSnippetStorage.PreservePreferenceKey);
        }
    }

    [Test]
    public void PreserveSnippets_DefaultsToFalseAndUsesEditorPrefs()
    {
        Assert.That(ConduitSnippetStorage.PreserveSnippets, Is.False);

        ConduitSnippetStorage.PreserveSnippets = true;

        Assert.That(
            EditorPrefs.GetBool(ConduitSnippetStorage.PreservePreferenceKey),
            Is.True
        );
    }

    [Test]
    public void EditorHandshakeIncludesPreservePreference()
    {
        ConduitSnippetStorage.PreserveSnippets = true;

        var handshake = ConduitConnection.CreateHandshake(
            ConduitProjectIdentity.GetProjectPath()
        );

        Assert.That(handshake.PreserveSnippets, Is.True);
    }
}
