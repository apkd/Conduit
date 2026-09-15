using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Conduit;

/// <summary>Reserves the original-call identifier without mistaking comments or literal text for code.</summary>
static class DetourOriginalCall
{
    internal static bool Analyze(SnippetParseResult parsed)
    {
        foreach (var declaration in parsed.TypeDeclarations)
            Check(declaration, "", allowOriginal: false);
        foreach (var field in parsed.StaticFields)
            Check(field, "class Host {\n", allowOriginal: false);
        return Check(parsed.Body, "class Host { void Replace() {\n", allowOriginal: true);

        static bool Check(SnippetChunk chunk, string prefix, bool allowOriginal)
        {
            var root = CSharpSyntaxTree.ParseText(prefix + chunk.Text + "\n}}",
                new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
            bool used = false;
            foreach (var token in root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == "base"))
            {
                int line = chunk.StartLine + token.GetLocation().GetLineSpan().StartLinePosition.Line - prefix.Count(c => c == '\n');
                if (token.Parent is not IdentifierNameSyntax name)
                    throw new SnippetParseException(line, "@base is reserved for calling the original method.");
                if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name
                    || name.Parent is QualifiedNameSyntax or NameColonSyntax or NameEqualsSyntax)
                    continue;
                if (!allowOriginal)
                    throw new SnippetParseException(line, "Use @base in the replacement body; it is unavailable during static initialization or in leading type declarations.");
                used = true;
            }
            return used;
        }
    }
}
