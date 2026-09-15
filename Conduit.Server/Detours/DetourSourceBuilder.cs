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
        if (DetourOriginalCall.Analyze(parsed))
        {
            builder.Append("public unsafe delegate ").Append(method.ReturnType.ReturnDeclaration).Append(" OriginalDelegate(");
            AppendParameters();
            builder.AppendLine(");");
            builder.AppendLine("public static OriginalDelegate __ConduitOriginal = null!;");
            builder.AppendLine("static OriginalDelegate @base => __ConduitOriginal;");
        }
        foreach (var field in parsed.StaticFields)
            SnippetSourceBuilder.AppendChunk(builder, field, sourceFileName);
        builder.AppendLine("[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        builder.Append(async ? "public static async " : "public static unsafe ");
        builder.Append(method.ReturnType.ReturnDeclaration)
            .Append(" Replace(");
        AppendParameters();
        builder.AppendLine(")");
        builder.AppendLine("{");
        SnippetSourceBuilder.AppendChunk(builder, parsed.Body, sourceFileName);
        builder.AppendLine("}");
        builder.AppendLine("public static int AccessProbe() => global::Conduit.DetourAccessProbe.Value;");
        builder.AppendLine("}");
        builder.AppendLine("}");
        builder.AppendLine("#pragma warning restore CS0162, CS1998");
        return builder.ToString();

        void AppendParameters()
        {
            bool hasParameter = false;
            if (!method.IsStatic)
            {
                if (method.DeclaringType.IsValueType)
                    builder.Append("ref ");
                builder.Append(method.DeclaringType.Source).Append(" @this");
                hasParameter = true;
            }
            foreach (var (index, parameter) in method.Parameters.Index())
            {
                if (hasParameter)
                    builder.Append(", ");
                builder.Append(parameter.Declaration("arg" + index));
                hasParameter = true;
            }
        }
    }
}
