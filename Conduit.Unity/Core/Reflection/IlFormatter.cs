#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;

namespace Conduit
{
    /// <summary>Formats type outlines and method IL without invoking the inspected code.</summary>
    static class IlFormatter
    {
        internal static string Format(MemberInfo member)
        {
            if (member is MethodBase method)
                return Body(method);
            if (member is Type type)
            {
                var members = ReflectionQueryEngine.GetInspectionMembers(type)
                    .Where(value => value is not MethodInfo info || !ReflectionMemberFormatter.IsPropertyOrEventAccessor(info))
                    .OrderBy(value => value.MemberType).ThenBy(value => ReflectionMemberFormatter.Selector(value), StringComparer.Ordinal)
                    .ToArray();
                var names = new IlTypeNames(members.Append(type).SelectMany(IlTypeNames.MemberTypes));
                return TypeHeader(type, names, TypeNameFormat.Qualified) + "\n{\n"
                    + string.Join("\n", members.Select(value => "  " + Declaration(value, names))) + "\n}";
            }

            var memberNames = new IlTypeNames(IlTypeNames.MemberTypes(member));
            var accessors = member switch
            {
                PropertyInfo property => property.GetAccessors(true),
                EventInfo @event => new[] { @event.AddMethod, @event.RemoveMethod, @event.RaiseMethod }
                    .Where(method => method != null).Cast<MethodInfo>().ToArray(),
                _ => Array.Empty<MethodInfo>(),
            };
            var output = Declaration(member, memberNames);
            if (accessors.Length > 0)
                output += "\n" + string.Join("\n", accessors.Select(method => ReflectionMemberFormatter.Selector(method)));
            else if (member is FieldInfo field && !field.IsLiteral)
                output += "\nInitializers: " + ReflectionMemberFormatter.Selector(field.DeclaringType!)
                    + (field.IsStatic ? "::.cctor" : "::.ctor");
            return output;
        }

        static string Declaration(MemberInfo member, IlTypeNames names)
        {
            if (member is Type type)
                return TypeHeader(type, names, TypeNameFormat.Short) + ";";
            string text = ReflectionMemberFormatter.FormatMemberSignature(member, names.Format);
            if (member is FieldInfo field)
            {
                if (field.IsLiteral)
                    text += "=" + Literal(field.GetRawConstantValue());
                var offset = field.GetCustomAttributesData()
                    .FirstOrDefault(attribute => attribute.AttributeType == typeof(FieldOffsetAttribute));
                text += ";";
                if (offset != null)
                    text += " // offset=" + offset.ConstructorArguments[0].Value;
            }
            else if (member is not PropertyInfo)
                text += ";";
            if (member is MethodBase method && GetBody(method) == null)
                text += method.IsAbstract ? " // abstract; no IL" : " // native/runtime body; no IL";
            return text;
        }

        static string TypeHeader(Type type, IlTypeNames names, TypeNameFormat format)
        {
            var builder = new StringBuilder();
            if (type.IsSerializable)
                builder.Append("[Serializable] ");
            if (type.IsPublic || type.IsNestedPublic)
                builder.Append("public ");
            else if (type.IsNestedFamily)
                builder.Append("protected ");
            else if (!type.IsNested || type.IsNestedAssembly)
                builder.Append("internal ");
            if (type.IsClass && !typeof(Delegate).IsAssignableFrom(type))
            {
                if (type.IsAbstract && type.IsSealed)
                    builder.Append("static ");
                else if (type.IsAbstract)
                    builder.Append("abstract ");
                else if (type.IsSealed)
                    builder.Append("sealed ");
            }
            builder.Append(ReflectionTypeFormatter.TypeKindLabel(type)).Append(' ').Append(names.Format(type, format));
            var bases = new List<Type>();
            if (type.IsEnum)
                bases.Add(Enum.GetUnderlyingType(type));
            else if (type.BaseType is { } parent && parent != typeof(object) && parent != typeof(ValueType)
                     && parent != typeof(MulticastDelegate))
                bases.Add(parent);
            bases.AddRange(type.GetInterfaces());
            if (bases.Count > 0)
                builder.Append(':').Append(string.Join(",", bases.Select(names.Format)));
            var flags = new List<string>();
            if (type.StructLayoutAttribute is { } layout)
            {
                if (layout.Value != LayoutKind.Auto)
                    flags.Add(layout.Value.ToString().ToLowerInvariant());
                if (layout.CharSet != CharSet.Ansi && layout.CharSet != CharSet.None)
                    flags.Add(layout.CharSet.ToString().ToLowerInvariant());
                if (layout.Pack != 0)
                    flags.Add("pack=" + layout.Pack);
                if (layout.Size != 0)
                    flags.Add("size=" + layout.Size);
            }
            if ((type.Attributes & TypeAttributes.BeforeFieldInit) != 0)
                flags.Add("beforefieldinit");
            if (flags.Count > 0)
                builder.Append(" // ").Append(string.Join(",", flags));
            return builder.ToString();
        }

        static string Body(MethodBase method)
        {
            var body = GetBody(method);
            if (body == null)
                return ReflectionMemberFormatter.Selector(method.DeclaringType!) + "\n"
                    + Declaration(method, new IlTypeNames(IlTypeNames.MemberTypes(method)));
            var instructions = MethodIL.Read(body.GetILAsByteArray()!);
            var resolver = new MethodIL.Resolver(method);
            // resolve the whole body before choosing short names; an operand can introduce a name collision.
            var operands = instructions.Select(instruction => Resolve(resolver, instruction)).ToArray();
            var names = new IlTypeNames(IlTypeNames.MemberTypes(method)
                .Concat(operands.OfType<MemberInfo>().SelectMany(IlTypeNames.MemberTypes))
                .Concat(body.LocalVariables.Select(local => local.LocalType))
                .Concat(body.ExceptionHandlingClauses.Cast<ExceptionHandlingClause>()
                    .Where(clause => clause.Flags == ExceptionHandlingClauseOptions.Clause).Select(clause => clause.CatchType!)));
            var builder = new StringBuilder(names.Format(method.DeclaringType!, TypeNameFormat.Qualified));
            builder.AppendLine().AppendLine(ReflectionMemberFormatter.FormatMemberSignature(method, names.Format));
            builder.Append("stack=").Append(body.MaxStackSize);
            if (body.LocalVariables.Count > 0)
                builder.Append(body.InitLocals ? " locals(init)=" : " locals=")
                    .Append(string.Join(",", body.LocalVariables.Select(local =>
                        local.LocalIndex + ":" + (local.IsPinned ? "pinned " : "") + names.Format(local.LocalType))));
            builder.AppendLine();
            // exception regions use an exclusive end offset, directly from the method's offset and length.
            foreach (var clause in body.ExceptionHandlingClauses)
            {
                builder.Append("try ").Append(clause.TryOffset.ToString("x")).Append("..")
                    .Append((clause.TryOffset + clause.TryLength).ToString("x")).Append(' ');
                if (clause.Flags == ExceptionHandlingClauseOptions.Clause)
                    builder.Append("catch ").Append(names.Format(clause.CatchType!));
                else if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
                    builder.Append("filter ").Append(clause.FilterOffset.ToString("x"));
                else
                    builder.Append(clause.Flags.ToString().ToLowerInvariant());
                builder.Append(' ').Append(clause.HandlerOffset.ToString("x")).Append("..")
                    .Append((clause.HandlerOffset + clause.HandlerLength).ToString("x")).AppendLine();
            }
            for (int index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                builder.Append(instruction.Offset.ToString("x")).Append(": ").Append(instruction.OpCode.Name);
                if (operands[index] is { } operand)
                    builder.Append(' ').Append(FormatOperand(instruction, operand, names));
                builder.AppendLine();
            }
            return builder.ToString().TrimEnd();
        }

        internal static object? Resolve(MethodIL.Resolver resolver, MethodIL.Instruction instruction)
        {
            try { return resolver.Resolve(instruction); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or NotSupportedException or TypeLoadException or BadImageFormatException
                                              or System.IO.FileNotFoundException or System.IO.FileLoadException or MissingMemberException)
            {
                // retain the operand and instruction boundary when optional metadata is unavailable.
                return new UnresolvedToken((int)instruction.Operand!);
            }
        }

        static string FormatOperand(MethodIL.Instruction instruction, object operand, IlTypeNames names)
        {
            if (instruction.OpCode.OperandType is OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget)
                return ((int)operand).ToString("x");
            return operand switch
            {
                UnresolvedToken token => $"unresolved(0x{token.Value:x8})",
                byte[] signature => $"sig(0x{instruction.Operand:x8}:" + BitConverter.ToString(signature).Replace("-", "") + ")",
                int[] targets => string.Join(",", targets.Select(target => target.ToString("x"))),
                Type type => names.Format(type),
                FieldInfo field => names.Format(field.FieldType) + " " + names.Format(field.DeclaringType!) + "." + field.Name,
                MethodBase called => (called.IsStatic ? "static " : "instance ")
                    + (called is MethodInfo info ? names.Format(info.ReturnType) : "void") + " "
                    + names.Format(called.DeclaringType!) + "." + called.Name
                    + (called.IsGenericMethod ? "<" + string.Join(",", called.GetGenericArguments().Select(names.Format)) + ">" : "")
                    + "(" + string.Join(",", called.GetParameters().Select(parameter => names.Format(parameter.ParameterType))) + ")",
                _ => Literal(operand),
            };
        }

        static MethodBody? GetBody(MethodBase method)
        {
            try { return method.GetMethodBody(); }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException) { return null; }
        }

        static string Literal(object? value)
        {
            if (value is not string && value is not char)
                return value switch
                {
                    null => "null",
                    float number => number.ToString("R", CultureInfo.InvariantCulture),
                    double number => number.ToString("R", CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
                };
            char quote = value is char ? '\'' : '"';
            var builder = new StringBuilder().Append(quote);
            foreach (char c in value.ToString()!)
                builder.Append(c switch
                {
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => c == quote ? "\\" + c : char.IsControl(c) ? "\\u" + ((int)c).ToString("x4") : c.ToString(),
                });
            return builder.Append(quote).ToString();
        }

        internal readonly struct UnresolvedToken
        {
            internal readonly int Value;
            internal UnresolvedToken(int value) => Value = value;
        }
    }
}
