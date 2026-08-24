#nullable enable

using System;
using System.Reflection;

namespace Conduit
{
    readonly struct BurstTarget
    {
        internal readonly string DisplayName;
        internal readonly string MethodName;
        internal readonly string DeclaringTypeName;
        internal readonly string JobTypeName;
        internal readonly MethodInfo? Method;
        internal readonly Type? JobType;
        internal readonly object? Options;
        internal readonly bool IsStaticMethod;

        internal BurstTarget(
            string displayName,
            string methodName,
            string declaringTypeName,
            string jobTypeName,
            MethodInfo? method = null,
            Type? jobType = null,
            object? options = null,
            bool isStaticMethod = false
        )
        {
            DisplayName = displayName ?? string.Empty;
            MethodName = methodName ?? string.Empty;
            DeclaringTypeName = declaringTypeName ?? string.Empty;
            JobTypeName = jobTypeName ?? string.Empty;
            Method = method;
            JobType = jobType;
            Options = options;
            IsStaticMethod = isStaticMethod;
        }
    }
}
