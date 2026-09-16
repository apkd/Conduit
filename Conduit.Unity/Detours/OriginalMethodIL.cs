#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Conduit
{
    /// <summary>Decodes IL in its source module before emitting equivalent instructions in another module.</summary>
    static class OriginalMethodIL
    {
        static readonly Dictionary<short, OpCode> longBranches = MethodIL.Opcodes.Values
            .Where(op => op.OperandType == OperandType.ShortInlineBrTarget)
            .ToDictionary(op => op.Value, op => MethodIL.Opcodes.Values.Single(longOp => longOp.Name == op.Name!.Replace(".s", "")));

        internal readonly struct Instruction
        {
            internal readonly int Offset;
            internal readonly OpCode OpCode;
            internal readonly object? Operand;

            internal Instruction(int offset, OpCode opCode, object? operand)
                => (Offset, OpCode, Operand) = (offset, opCode, operand);
        }

        internal static List<Instruction> Read(MethodInfo method, MethodBody body)
        {
            var result = new List<Instruction>();
            var resolver = new MethodIL.Resolver(method);
            foreach (var instruction in MethodIL.Read(body.GetILAsByteArray()!))
            {
                int offset = instruction.Offset;
                try
                {
                    var op = instruction.OpCode;
                    if (op == OpCodes.Calli || op == OpCodes.Jmp)
                        throw new NotSupportedException(op.Name);
                    var operand = resolver.Resolve(instruction);
                    if (op.OperandType == OperandType.ShortInlineBrTarget)
                        op = longBranches[op.Value]; // cloned instructions can grow when metadata tokens change
                    if (operand is ushort variable)
                        operand = unchecked((short)variable);
                    if (operand is Type type)
                        OriginalMethod.ValidateType(type);
                    if (operand is FieldInfo field)
                        OriginalMethod.ValidateType(field.FieldType);
                    if (operand is MethodBase called)
                    {
                        if ((called.CallingConvention & CallingConventions.VarArgs) != 0)
                            throw new NotSupportedException("varargs call sites");
                        if (called is MethodInfo calledMethod)
                            OriginalMethod.ValidateType(calledMethod.ReturnType);
                        foreach (var parameter in called.GetParameters())
                            OriginalMethod.ValidateType(parameter.ParameterType);
                    }
                    result.Add(new Instruction(offset, op, operand));
                }
                catch (Exception exception) when (exception is NotSupportedException or ArgumentException or IndexOutOfRangeException)
                {
                    throw new NotSupportedException($"{exception.Message} at IL_{offset:x4}", exception);
                }
            }
            return result;
        }

        internal static void Emit(ILGenerator il, Instruction instruction, Dictionary<int, Label> labels)
        {
            var op = instruction.OpCode;
            var operand = instruction.Operand;
            if (op.OperandType == OperandType.InlineBrTarget)
                il.Emit(op, labels[(int)operand!]);
            else if (op.OperandType == OperandType.InlineSwitch)
                il.Emit(op, ((int[])operand!).Select(t => labels[t]).ToArray());
            else
                switch (operand)
                {
                    case null: il.Emit(op); break;
                    case sbyte value: il.Emit(op, value); break;
                    case byte value: il.Emit(op, value); break;
                    case short value: il.Emit(op, value); break;
                    case int value: il.Emit(op, value); break;
                    case long value: il.Emit(op, value); break;
                    case float value: il.Emit(op, value); break;
                    case double value: il.Emit(op, value); break;
                    case string value: il.Emit(op, value); break;
                    case Type value: il.Emit(op, value); break;
                    case FieldInfo value: il.Emit(op, value); break;
                    case ConstructorInfo value: il.Emit(op, value); break;
                    case MethodInfo value: il.Emit(op, value); break;
                    default: throw new NotSupportedException($"IL operand {operand}");
                }
        }

        internal static void ValidateLocals(Module module, MethodBody body)
        {
            if (body.LocalSignatureMetadataToken == 0)
                return;
            var signature = module.ResolveSignature(body.LocalSignatureMetadataToken);
            int position = 0;
            if (signature[position++] != 0x07)
                throw new NotSupportedException("invalid local signature");
            int count = Integer();
            for (int index = 0; index < count; index++)
                Type();
            if (position != signature.Length)
                throw new NotSupportedException("trailing local signature data");

            // reflection omits local custom modifiers, so check the signature before using LocalVariableInfo.
            void Type()
            {
                byte kind = signature[position++];
                switch (kind)
                {
                    case 0x1b: throw new NotSupportedException("function-pointer locals");
                    case 0x1f:
                    case 0x20: throw new NotSupportedException("local custom modifiers");
                    case 0x0f:
                    case 0x10:
                    case 0x1d:
                    case 0x45: Type(); break;
                    case 0x11:
                    case 0x12:
                    case 0x13:
                    case 0x1e: Integer(); break;
                    case 0x14:
                        Type();
                        Integer(); // array rank
                        int sizes = Integer();
                        for (int index = 0; index < sizes; index++)
                            Integer();
                        int bounds = Integer();
                        for (int index = 0; index < bounds; index++)
                            Integer();
                        break;
                    case 0x15:
                        Type();
                        int arguments = Integer();
                        for (int index = 0; index < arguments; index++)
                            Type();
                        break;
                    case >= 0x01 and <= 0x0e:
                    case 0x16:
                    case 0x18:
                    case 0x19:
                    case 0x1c: break;
                    default: throw new NotSupportedException($"local signature element 0x{kind:x2}");
                }
            }

            int Integer()
            {
                int value = signature[position++];
                if ((value & 0x80) == 0)
                    return value;
                if ((value & 0xc0) == 0x80)
                    return ((value & 0x3f) << 8) | signature[position++];
                if ((value & 0xe0) != 0xc0)
                    throw new NotSupportedException("invalid compressed signature integer");
                return ((value & 0x1f) << 24) | (signature[position++] << 16)
                    | (signature[position++] << 8) | signature[position++];
            }
        }
    }
}
