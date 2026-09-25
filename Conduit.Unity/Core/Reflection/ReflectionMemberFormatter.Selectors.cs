#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Conduit
{
    enum SelectorFormat { Qualified, AssemblyQualified }

    static partial class ReflectionMemberFormatter
    {
        /// <summary>Formats a reusable target for the type or member's metadata definition.</summary>
        internal static string Selector(MemberInfo member, SelectorFormat format = SelectorFormat.Qualified)
        {
            var type = member as Type ?? member.DeclaringType!;
            if (type is { IsGenericType: true, IsGenericTypeDefinition: false })
            {
                // the loaded type index contains definitions, not inherited generic substitutions.
                // rebind the member too: Collection<int>.Add(int) must become Collection<T>.Add(T).
                type = type.GetGenericTypeDefinition();
                if (member is not Type)
                {
                    foreach (var candidate in type.GetMember(member.Name, member.MemberType,
                                 BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static))
                    {
                        if (candidate.MetadataToken != member.MetadataToken)
                            continue;
                        member = candidate;
                        break;
                    }
                }
            }
            var name = ReflectionTypeFormatter.FormatType(type, true);
            if (format == SelectorFormat.AssemblyQualified)
                name = (type.FullName ?? type.Name) + ", " + type.Assembly.GetName().Name;
            if (member is Type)
                return name;
            string selector = name + "::" + MemberName(member, TypeNameFormat.Qualified);
            return member is MethodInfo method ? selector + " -> " + ReturnTypeName(method, TypeNameFormat.Qualified) : selector;
        }

        internal static string MemberName(MemberInfo member, TypeNameFormat format)
        {
            var name = member.Name;
            if (member is MethodInfo method && method.IsGenericMethod)
                name += "<" + string.Join(", ", method.GetGenericArguments().Select(type => type.Name)) + ">";
            var parameters = member switch
            {
                MethodBase callable => callable.GetParameters(),
                PropertyInfo property => property.GetIndexParameters(),
                _ => null,
            };
            if (parameters == null || member is PropertyInfo && parameters.Length == 0)
                return name;
            return name + "(" + string.Join(", ", parameters.Select(parameter =>
                (parameter.ParameterType.IsByRef ? parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref " : "")
                + ReflectionTypeFormatter.FormatType(parameter.ParameterType, format == TypeNameFormat.Qualified))) + ")";
        }

        internal static string ReturnTypeName(MethodInfo method, TypeNameFormat format)
        {
            var type = method.ReturnType;
            return (type.IsByRef ? IsReadOnly(method.ReturnParameter) ? "ref readonly " : "ref " : "")
                   + ReflectionTypeFormatter.FormatType(type, format == TypeNameFormat.Qualified);
        }

        internal static string TargetCandidates(IReadOnlyList<MemberInfo> matches, int maxCandidates)
        {
            var collisions = new HashSet<Type>(matches.Select(member => member as Type ?? member.DeclaringType!)
                .Distinct().GroupBy(type => type.FullName).Where(group => group.Count() > 1).SelectMany(group => group));
            return "Multiple matches; select one:\n" + string.Join("\n", matches.Take(maxCandidates)
                .Select(member => Selector(member, collisions.Contains(member as Type ?? member.DeclaringType!)
                    ? SelectorFormat.AssemblyQualified : SelectorFormat.Qualified)))
                + (matches.Count > maxCandidates ? $"\n+{matches.Count - maxCandidates} more; narrow the target." : "");
        }
    }
}
