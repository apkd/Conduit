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
        static readonly Dictionary<short, OpCode> opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(op => op.Value);
        static readonly Dictionary<short, OpCode> longBranches = opcodes.Values
            .Where(op => op.OperandType == OperandType.ShortInlineBrTarget)
            .ToDictionary(op => op.Value, op => opcodes.Values.Single(longOp => longOp.Name == op.Name!.Replace(".s", "")));

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
            var bytes = body.GetILAsByteArray()!;
            var result = new List<Instruction>();
            var module = method.Module;
            int position = 0;
            while (position < bytes.Length)
            {
                int offset = position;
                try
                {
                    short code = bytes[position++];
                    if (code == 0xfe)
                        code = (short)(0xfe00 | bytes[position++]);
                    if (!opcodes.TryGetValue(code, out var op))
                        throw new NotSupportedException("unknown opcode");
                    if (op == OpCodes.Calli || op == OpCodes.Jmp)
                        throw new NotSupportedException(op.Name);

                    object? operand;
                    switch (op.OperandType)
                    {
                        case OperandType.InlineNone: operand = null; break;
                        case OperandType.ShortInlineI: operand = (sbyte)bytes[position++]; break;
                        case OperandType.InlineI: operand = Int32(); break;
                        case OperandType.InlineI8: operand = BitConverter.ToInt64(bytes, position); position += 8; break;
                        case OperandType.ShortInlineR: operand = BitConverter.ToSingle(bytes, position); position += 4; break;
                        case OperandType.InlineR: operand = BitConverter.ToDouble(bytes, position); position += 8; break;
                        case OperandType.ShortInlineVar: operand = bytes[position++]; break;
                        case OperandType.InlineVar: operand = BitConverter.ToInt16(bytes, position); position += 2; break;
                        case OperandType.ShortInlineBrTarget:
                            int delta = (sbyte)bytes[position++];
                            operand = position + delta;
                            op = longBranches[op.Value];
                            break;
                        case OperandType.InlineBrTarget:
                            int displacement = Int32();
                            operand = position + displacement;
                            break;
                        case OperandType.InlineSwitch:
                            int count = Int32();
                            if (count < 0 || count > (bytes.Length - position) / 4)
                                throw new NotSupportedException("invalid switch table");
                            int end = position + count * 4;
                            var targets = new int[count];
                            for (int index = 0; index < count; index++)
                                targets[index] = end + Int32();
                            operand = targets;
                            break;
                        case OperandType.InlineString: operand = module.ResolveString(Int32()); break;
                        case OperandType.InlineType: operand = module.ResolveType(Int32()); break;
                        case OperandType.InlineField: operand = module.ResolveField(Int32()); break;
                        case OperandType.InlineMethod: operand = module.ResolveMethod(Int32()); break;
                        case OperandType.InlineTok: operand = module.ResolveMember(Int32()); break;
                        default: throw new NotSupportedException($"operand {op.OperandType}");
                    }
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
            var boundaries = new HashSet<int>(result.Select(i => i.Offset)) { bytes.Length };
            foreach (var instruction in result)
            {
                var targets = instruction.OpCode.OperandType switch
                {
                    OperandType.InlineBrTarget => new[] { (int)instruction.Operand! },
                    OperandType.InlineSwitch => (int[])instruction.Operand!,
                    _ => Array.Empty<int>()
                };
                if (targets.Any(t => !boundaries.Contains(t)))
                    throw new NotSupportedException($"invalid branch destination at IL_{instruction.Offset:x4}");
            }
            return result;

            int Int32()
            {
                int value = BitConverter.ToInt32(bytes, position);
                position += 4;
                return value;
            }
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
