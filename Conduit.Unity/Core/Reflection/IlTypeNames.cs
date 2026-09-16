#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Conduit
{
    /// <summary>Shortens symbols within one report while retaining distinctions between colliding names.</summary>
    sealed class IlTypeNames
    {
        readonly HashSet<Type> qualified;
        readonly HashSet<Type> assemblyQualified;

        internal IlTypeNames(IEnumerable<Type> types)
        {
            var definitions = new HashSet<Type>();
            foreach (var type in types)
                Add(type);
            // qualify only collisions, first by namespace and then by assembly, to keep IL readable.
            qualified = Collisions(type => ShortName(type));
            assemblyQualified = Collisions(type => type.FullName ?? type.Name);

            void Add(Type type)
            {
                if (type.HasElementType)
                    Add(type.GetElementType()!);
                if (type.IsGenericParameter)
                    return;
                foreach (var argument in type.GetGenericArguments())
                    Add(argument);
                definitions.Add(type.IsGenericType ? type.GetGenericTypeDefinition() : type);
            }

            HashSet<Type> Collisions(Func<Type, string> key)
                => new(definitions.GroupBy(key).Where(group => group.Count() > 1).SelectMany(group => group));
        }

        internal string Format(Type type) => Format(type, TypeNameFormat.Short);

        internal string Format(Type type, TypeNameFormat format)
        {
            if (type.IsByRef)
                return Format(type.GetElementType()!, format) + "&";
            if (type.IsPointer)
                return Format(type.GetElementType()!, format) + "*";
            if (type.IsArray)
                return Format(type.GetElementType()!, format) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (type == typeof(IntPtr))
                return "nint";
            if (type == typeof(UIntPtr))
                return "nuint";
            if (type.IsGenericParameter || type.IsPrimitive || type == typeof(void) || type == typeof(object)
                || type == typeof(string) || type == typeof(decimal) || MonoSignature.IsFunctionPointer(type))
                return ReflectionTypeFormatter.FormatType(type);

            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            var hierarchy = new Stack<Type>();
            for (var current = type; current != null; current = current.DeclaringType)
                hierarchy.Push(current);
            var arguments = type.GetGenericArguments(); // includes enclosing types' arguments before the nested type's own
            int argumentIndex = 0;
            var builder = new StringBuilder();
            if (assemblyQualified.Contains(definition))
                builder.Append('[').Append(type.Assembly.GetName().Name).Append(']');
            bool first = true;
            foreach (var part in hierarchy)
            {
                if (!first)
                    builder.Append('.');
                else if ((format == TypeNameFormat.Qualified || qualified.Contains(definition)) && !string.IsNullOrEmpty(part.Namespace))
                    builder.Append(part.Namespace).Append('.');
                first = false;
                int tick = part.Name.IndexOf('`');
                builder.Append(CSharpIdentifier.Escape(tick < 0 ? part.Name : part.Name.Substring(0, tick)));
                if (tick < 0)
                    continue;
                int arity = int.Parse(part.Name.Substring(tick + 1), System.Globalization.CultureInfo.InvariantCulture);
                builder.Append('<');
                for (int index = 0; index < arity; index++)
                {
                    if (index > 0)
                        builder.Append(',');
                    builder.Append(Format(arguments[argumentIndex++], format));
                }
                builder.Append('>');
            }
            return builder.ToString();
        }

        static string ShortName(Type type)
            => type.DeclaringType is { } parent ? ShortName(parent) + "." + type.Name : type.Name;

        internal static IEnumerable<Type> MemberTypes(MemberInfo member)
        {
            if (member.DeclaringType is { } owner)
                yield return owner;
            switch (member)
            {
                case Type type:
                    yield return type;
                    if (type.BaseType is { } parent)
                        yield return parent;
                    foreach (var implemented in type.GetInterfaces())
                        yield return implemented;
                    break;
                case FieldInfo field: yield return field.FieldType; break;
                case PropertyInfo property:
                    yield return property.PropertyType;
                    foreach (var parameter in property.GetIndexParameters())
                        yield return parameter.ParameterType;
                    break;
                case EventInfo @event: yield return @event.EventHandlerType!; break;
                case MethodBase method:
                    if (method is MethodInfo info)
                    {
                        yield return info.ReturnType;
                        foreach (var argument in info.GetGenericArguments())
                            yield return argument;
                    }
                    foreach (var parameter in method.GetParameters())
                        yield return parameter.ParameterType;
                    break;
            }
        }
    }
}
