#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Conduit
{
    /// <summary>Reads original IL without applying the restrictions or rewrites needed for cloning.</summary>
    static class MethodIL
    {
        internal static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(op => op.Value);

        internal readonly struct Instruction
        {
            internal readonly int Offset;
            internal readonly OpCode OpCode;
            internal readonly object? Operand; // branches use absolute byte offsets; metadata tokens stay unresolved

            internal Instruction(int offset, OpCode opCode, object? operand)
                => (Offset, OpCode, Operand) = (offset, opCode, operand);
        }

        /// <summary>Reads instructions and checks their byte boundaries while preserving the original opcodes and tokens.</summary>
        internal static List<Instruction> Read(byte[] bytes)
        {
            var instructions = new List<Instruction>();
            int position = 0;
            while (position < bytes.Length)
            {
                int offset = position;
                try
                {
                    short code = bytes[position++];
                    if (code == 0xfe)
                        code = (short)(0xfe00 | bytes[position++]);
                    if (!Opcodes.TryGetValue(code, out var op))
                        throw new NotSupportedException("unknown opcode");

                    object? operand;
                    switch (op.OperandType)
                    {
                        case OperandType.InlineNone: operand = null; break;
                        case OperandType.ShortInlineI: operand = (sbyte)bytes[position++]; break;
                        case OperandType.InlineI8: operand = BitConverter.ToInt64(bytes, position); position += 8; break;
                        case OperandType.ShortInlineR: operand = BitConverter.ToSingle(bytes, position); position += 4; break;
                        case OperandType.InlineR: operand = BitConverter.ToDouble(bytes, position); position += 8; break;
                        case OperandType.ShortInlineVar: operand = bytes[position++]; break;
                        case OperandType.InlineVar: operand = BitConverter.ToUInt16(bytes, position); position += 2; break;
                        case OperandType.ShortInlineBrTarget:
                            int delta = (sbyte)bytes[position++];
                            operand = position + delta;
                            break;
                        case OperandType.InlineBrTarget:
                            int displacement = Int32();
                            operand = position + displacement;
                            break;
                        case OperandType.InlineSwitch:
                            int count = Int32();
                            if (count < 0 || count > (bytes.Length - position) / 4)
                                throw new NotSupportedException("invalid switch table");
                            int end = position + count * 4; // every displacement is relative to the end of the whole table
                            var targets = new int[count];
                            for (int index = 0; index < count; index++)
                                targets[index] = end + Int32();
                            operand = targets;
                            break;
                        case OperandType.InlineI:
                        case OperandType.InlineString:
                        case OperandType.InlineType:
                        case OperandType.InlineField:
                        case OperandType.InlineMethod:
                        case OperandType.InlineTok:
                        case OperandType.InlineSig: operand = Int32(); break;
                        default: throw new NotSupportedException($"operand {op.OperandType}");
                    }
                    instructions.Add(new Instruction(offset, op, operand));
                }
                catch (Exception exception) when (exception is NotSupportedException or ArgumentException or IndexOutOfRangeException)
                {
                    throw new NotSupportedException($"{exception.Message} at IL_{offset:x}", exception);
                }
            }

            // collect all offsets first so forward branches can be checked for jumps into operands.
            var boundaries = new HashSet<int> { bytes.Length };
            foreach (var instruction in instructions)
                boundaries.Add(instruction.Offset);
            foreach (var instruction in instructions)
            {
                if (instruction.OpCode.OperandType is OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget)
                    if (!boundaries.Contains((int)instruction.Operand!))
                        throw new NotSupportedException($"invalid branch destination at IL_{instruction.Offset:x}");

                if (instruction.Operand is int[] targets)
                    foreach (int target in targets)
                        if (!boundaries.Contains(target))
                            throw new NotSupportedException($"invalid branch destination at IL_{instruction.Offset:x}");
            }
            return instructions;

            int Int32()
            {
                int value = BitConverter.ToInt32(bytes, position);
                position += 4;
                return value;
            }
        }

        /// <summary>Resolves tokens in the generic context of the method that owns the IL.</summary>
        internal readonly struct Resolver
        {
            readonly Module module;
            readonly Type[] typeArguments;
            readonly Type[] methodArguments;

            internal Resolver(MethodBase method)
            {
                module = method.Module;
                typeArguments = method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
                methodArguments = method is MethodInfo info ? info.GetGenericArguments() : Type.EmptyTypes;
            }

            internal object? Resolve(Instruction instruction) => instruction.OpCode.OperandType switch
            {
                OperandType.InlineString => module.ResolveString((int)instruction.Operand!),
                OperandType.InlineType => module.ResolveType((int)instruction.Operand!, typeArguments, methodArguments),
                OperandType.InlineField => module.ResolveField((int)instruction.Operand!, typeArguments, methodArguments),
                OperandType.InlineMethod => module.ResolveMethod((int)instruction.Operand!, typeArguments, methodArguments),
                OperandType.InlineTok => module.ResolveMember((int)instruction.Operand!, typeArguments, methodArguments),
                OperandType.InlineSig => module.ResolveSignature((int)instruction.Operand!),
                _ => instruction.Operand,
            };
        }
    }
}
