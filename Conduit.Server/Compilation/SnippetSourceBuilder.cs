using System.Text;

namespace Conduit;

static class SnippetSourceBuilder
{
    internal const string Namespace = "ConduitGenerated.ExecuteCode";

    internal static string BuildSource(
        string typeName,
        string sourceFileName,
        SnippetParseResult parsed,
        IReadOnlyList<string> defaultUsingDirectives,
        IReadOnlyCollection<string> inferredNamespaces,
        bool async,
        bool returnsValue)
    {
        var builder = new StringBuilder(parsed.Body.Text.Length + 1024);
        AppendUsingDirectives(
            builder,
            defaultUsingDirectives,
            inferredNamespaces,
            parsed.Usings,
            sourceFileName
        );

        // one-off snippets expose runtime failures directly, while nullable warnings are usually wrapper or Unity metadata noise.
        builder.AppendLine("#nullable disable warnings");
        builder.AppendLine("#pragma warning disable CS0162, CS1998");
        builder.Append("namespace ").AppendLine(Namespace);
        builder.AppendLine("{");
        foreach (var declaration in parsed.TypeDeclarations)
            AppendChunk(builder, declaration, sourceFileName);

        builder.Append("public static class ").AppendLine(typeName);
        builder.AppendLine("{");
        foreach (var field in parsed.StaticFields)
            AppendChunk(builder, field, sourceFileName);

        builder.Append("public static ");
        if (async)
            builder.Append("async Task");
        else
            builder.Append(returnsValue ? "object" : "void");
        if (async && returnsValue)
            builder.Append("<object>");
        builder.AppendLine(" Execute()");
        builder.AppendLine("{");
        AppendChunk(builder, parsed.Body, sourceFileName);
        if (returnsValue)
        {
            builder.AppendLine("#line hidden");
            builder.AppendLine("return null;");
        }
        builder.AppendLine("}");
        builder.AppendLine("}");
        builder.AppendLine("}");
        builder.AppendLine("#pragma warning restore CS0162, CS1998");
        builder.AppendLine("#nullable restore warnings");
        return builder.ToString();
    }

    internal static void AppendUsingDirectives(
        StringBuilder builder,
        IReadOnlyList<string> defaultUsingDirectives,
        IReadOnlyCollection<string> inferredNamespaces,
        IEnumerable<SnippetChunk> snippetDirectives,
        string sourceFileName)
    {
        var emittedUsings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directive in defaultUsingDirectives)
            if (emittedUsings.Add(directive))
                builder.AppendLine(directive);

        foreach (var inferredNamespace in inferredNamespaces)
        {
            var directive = $"using {inferredNamespace};";
            if (emittedUsings.Add(directive))
                builder.AppendLine(directive);
        }

        foreach (var directive in snippetDirectives)
        {
            var normalized = directive.Text.Trim();
            if (emittedUsings.Add(normalized))
                AppendChunk(builder, directive, sourceFileName);
        }
    }

    // preserve user coordinates even though snippets are emitted inside a generated wrapper.
    internal static void AppendChunk(
        StringBuilder builder,
        SnippetChunk chunk,
        string sourceFileName)
    {
        if (chunk.Text.Length == 0)
            return;

        builder.Append("#line ")
            .Append(Math.Max(1, chunk.StartLine))
            .Append(" \"")
            .Append(sourceFileName.Replace("\\", "\\\\"))
            .AppendLine("\"");
        builder.Append(chunk.Text);
        if (chunk.Text[^1] != '\n')
            builder.AppendLine();
        builder.AppendLine("#line default");
    }
}
