#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;

namespace Conduit
{
    readonly struct WideMemberIndexEntry
    {
        internal readonly Type DeclaringType;
        internal readonly string Name;
        internal readonly MemberInfo Member;

        internal WideMemberIndexEntry(Type declaringType, MemberInfo member)
        {
            DeclaringType = declaringType;
            Name = member.Name;
            Member = member;
        }

        internal static IComparer<WideMemberIndexEntry> WithinTypeComparer { get; } = new EntryComparer();

        internal static int GetMetadataToken(MemberInfo member)
        {
            try
            {
                return member.MetadataToken;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                return 0;
            }
        }

        sealed class EntryComparer : IComparer<WideMemberIndexEntry>
        {
            public int Compare(WideMemberIndexEntry left, WideMemberIndexEntry right)
            {
                var name = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
                return name != 0
                    ? name
                    : GetMetadataToken(left.Member).CompareTo(GetMetadataToken(right.Member));
            }
        }
    }
}
