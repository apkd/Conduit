#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Conduit
{
    public static partial class ConduitReflect
    {
        static void ValidateResultType<T>(ReflectMode mode) where T : class
        {
            var requestedType = typeof(T);
            if (mode.Category == ReflectCategory.Types)
            {
                if (requestedType == typeof(Type))
                    return;

                throw new InvalidOperationException($"Reflect mode '{FormatMode(mode)}' returns Type; requested {requestedType.Name}.");
            }

            if (!typeof(MemberInfo).IsAssignableFrom(requestedType))
                throw new InvalidOperationException($"Reflect mode '{FormatMode(mode)}' returns MemberInfo; requested {requestedType.Name}.");

            var requestedKind = GetRequestedMemberKind(requestedType);
            if (requestedKind == ReflectMemberKind.None
                || mode.MemberKind == ReflectMemberKind.None
                || mode.MemberKind == requestedKind)
                return;

            throw new InvalidOperationException($"Reflect mode '{FormatMode(mode)}' cannot return {requestedType.Name}.");
        }

        static ReflectMemberKind GetEffectiveMemberKind<T>(ReflectMemberKind modeKind) where T : class
        {
            if (modeKind != ReflectMemberKind.None)
                return modeKind;

            return GetRequestedMemberKind(typeof(T));
        }

        static ReflectMemberKind GetRequestedMemberKind(Type requestedType)
        {
            if (requestedType == typeof(FieldInfo))
                return ReflectMemberKind.Field;
            if (requestedType == typeof(PropertyInfo))
                return ReflectMemberKind.Property;
            if (requestedType == typeof(MethodInfo))
                return ReflectMemberKind.Method;
            if (requestedType == typeof(ConstructorInfo))
                return ReflectMemberKind.Constructor;
            if (requestedType == typeof(MemberInfo))
                return ReflectMemberKind.None;

            throw new InvalidOperationException($"Reflect member lookup does not support result type {requestedType.Name}.");
        }

        static T SelectSingle<T>(string mode, string? type, string? member, IReadOnlyList<T> matches) where T : class
        {
            if (matches.Count == 1)
                return matches[0];

            var query = FormatQuery(mode, type, member);
            if (matches.Count == 0)
                throw new InvalidOperationException($"No reflected result matched {query}.");

            throw new InvalidOperationException(FormatResultCandidates($"Multiple reflected results match {query}.", matches));
        }

        static T[] CastResults<T, TSource>(IReadOnlyList<TSource> matches)
            where T : class
            where TSource : class
        {
            if (matches.Count == 0)
                return Array.Empty<T>();

            var results = new T[matches.Count];
            for (var index = 0; index < matches.Count; index++)
                results[index] = (T)(object)matches[index];

            return results;
        }

    }
}

