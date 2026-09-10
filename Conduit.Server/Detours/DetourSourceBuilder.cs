using System.Reflection;
using System.Text;

namespace Conduit;

static class DetourSourceBuilder
{
    internal const string GeneratedNamespace = "ConduitGenerated.Detour";

    internal static string BuildProbeBody(MethodTarget method)
    {
        var builder = new StringBuilder();
        // test mode never invokes this body, but C# still requires definite assignment.
        foreach (var (index, parameter) in method.Parameters.Index())
            if (parameter.Type.IsByRef && (parameter.Attributes & ParameterAttributes.Out) != 0)
                builder.Append("arg").Append(index).AppendLine(" = default!;");

        return builder
            .Append(method.ReturnType switch
            {
                { IsByRef: true } => "throw new global::System.NotSupportedException();",
                { Source: "void" } => "return;",
                _ => "return default!;",
            })
            .ToString();
    }

    internal static string BuildSource(
        MethodTarget method,
        string typeName,
        string sourceFileName,
        SnippetParseResult parsed,
        IReadOnlyList<string> defaultUsings,
        IReadOnlyCollection<string> inferredNamespaces,
        bool async)
    {
        var builder = new StringBuilder(parsed.Body.Text.Length + 1024);
        SnippetSourceBuilder.AppendUsingDirectives(
            builder,
            defaultUsings,
            inferredNamespaces,
            parsed.Usings,
            sourceFileName
        );

        builder.AppendLine("#pragma warning disable CS0162, CS1998");
        builder.Append("namespace ").AppendLine(GeneratedNamespace);
        builder.AppendLine("{");
        foreach (var declaration in parsed.TypeDeclarations)
            SnippetSourceBuilder.AppendChunk(builder, declaration, sourceFileName);
        builder.Append("public static class ").AppendLine(typeName);
        builder.AppendLine("{");
        foreach (var field in parsed.StaticFields)
            SnippetSourceBuilder.AppendChunk(builder, field, sourceFileName);
        builder.AppendLine("[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        builder.Append("public static unsafe ");
        if (async)
            builder.Append("async ");
        builder.Append(method.ReturnType.ReturnDeclaration)
            .Append(" Replace(");
        var hasParameter = false;
        if (!method.IsStatic)
        {
            if (method.DeclaringType.IsValueType)
                builder.Append("ref ");
            builder.Append(method.DeclaringType.Source).Append(" @this");
            hasParameter = true;
        }
        for (int index = 0; index < method.Parameters.Length; ++index)
        {
            if (hasParameter)
                builder.Append(", ");
            builder.Append(method.Parameters[index].Declaration("arg" + index));
            hasParameter = true;
        }
        builder.AppendLine(")");
        builder.AppendLine("{");
        SnippetSourceBuilder.AppendChunk(builder, parsed.Body, sourceFileName);
        builder.AppendLine("}");
        builder.AppendLine("public static int AccessProbe() => global::Conduit.DetourAccessProbe.Value;");
        builder.AppendLine("}");
        builder.AppendLine("}");
        builder.AppendLine("#pragma warning restore CS0162, CS1998");
        return builder.ToString();
    }
}
