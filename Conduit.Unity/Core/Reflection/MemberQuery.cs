#nullable enable

using System;
using System.Reflection;

namespace Conduit
{
    /// <summary>Parses a member query once before scanning reflection metadata.</summary>
    readonly struct MemberQuery
    {
        internal readonly string Text;
        internal readonly string? ReturnType;
        internal readonly bool HasSignature;
        internal readonly bool IncludesAccessors;

        internal MemberQuery(string? text)
        {
            Text = text?.Trim() ?? string.Empty;
            HasSignature = Text.IndexOf('(') >= 0;
            ReturnType = null;
            if (HasSignature)
            {
                // wide searches visit many members; normalize whitespace once, before scanning any metadata.
                Text = Compact(Text);
                // return types distinguish conversion operators with identical names and parameters.
                int arrow = Text.IndexOf(")->", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    ReturnType = Text.Substring(arrow + 3);
                    Text = Text.Substring(0, arrow + 1);
                }
                if (Text.StartsWith("ctor(", StringComparison.OrdinalIgnoreCase)
                    || Text.StartsWith("cctor(", StringComparison.OrdinalIgnoreCase))
                    Text = "." + Text;
            }

            int end = HasSignature ? Text.IndexOf('(') : Text.Length;
            var name = Text.AsSpan(0, end);
            name = name.Slice(name.LastIndexOf('.') + 1);
            IncludesAccessors = name.StartsWith("get_".AsSpan(), StringComparison.OrdinalIgnoreCase)
                                || name.StartsWith("set_".AsSpan(), StringComparison.OrdinalIgnoreCase)
                                || name.StartsWith("add_".AsSpan(), StringComparison.OrdinalIgnoreCase)
                                || name.StartsWith("remove_".AsSpan(), StringComparison.OrdinalIgnoreCase)
                                || name.StartsWith("raise_".AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        internal int SignatureMatchRank(MemberInfo member)
        {
            // most candidates can be rejected without formatting parameter or return types.
            if (!Text.StartsWith(member.Name, StringComparison.OrdinalIgnoreCase))
                return NameMatching.None;
            int rank = SignatureRank(ReflectionMemberFormatter.MemberName(member, TypeNameFormat.Short), Text);
            if (rank != NameMatching.Exact)
                rank = Math.Min(rank, SignatureRank(ReflectionMemberFormatter.MemberName(member, TypeNameFormat.Qualified), Text));
            if (rank == NameMatching.None || ReturnType == null)
                return rank;
            if (member is not MethodInfo method)
                return NameMatching.None;
            int returnRank = SignatureRank(ReflectionMemberFormatter.ReturnTypeName(method, TypeNameFormat.Short), ReturnType);
            if (returnRank != NameMatching.Exact)
                returnRank = Math.Min(returnRank,
                    SignatureRank(ReflectionMemberFormatter.ReturnTypeName(method, TypeNameFormat.Qualified), ReturnType));
            // the whole selector must match exactly to win over a case-insensitive alternative.
            return Math.Max(rank, returnRank);
        }

        static int SignatureRank(string formatted, string query)
        {
            formatted = Compact(formatted);
            return formatted.Length == query.Length ? NameMatching.Rank(formatted, query) : NameMatching.None;
        }

        static string Compact(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                if (!char.IsWhiteSpace(text[index]))
                    continue;
                using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
                builder.Append(text, 0, index);
                for (; index < text.Length; index++)
                    if (!char.IsWhiteSpace(text[index]))
                        builder.Append(text[index]);
                return builder.ToString();
            }
            return text;
        }
    }
}
