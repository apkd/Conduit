using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Conduit;

static class MetadataPublicizer
{
    internal static unsafe PortableExecutableReference CreateReference(string path)
    {
        using var stream = File.OpenRead(path);
        var headers = new PEHeaders(stream);
        if (headers.MetadataSize == 0)
            throw new BadImageFormatException($"'{path}' has no managed metadata.");

        // method bodies and resources are never needed for binding, even when granting private access.
        var bytes = GC.AllocateUninitializedArray<byte>(headers.MetadataSize, pinned: true);
        stream.Position = headers.MetadataStartOffset;
        stream.ReadExactly(bytes);
        fixed (byte* pointer = bytes)
        {
            var reader = new MetadataReader(pointer, bytes.Length);
            RewriteTable(reader, TableIndex.TypeDef, 0, RewriteTypeAttributes);
            RewriteTable(reader, TableIndex.Field, 0, RewriteFieldAttributes);
            RewriteTable(reader, TableIndex.MethodDef, sizeof(uint) + sizeof(ushort), RewriteMethodAttributes);

            // the pinned object heap keeps the address stable; the callback retains the bytes for roslyn's lifetime.
            var module = ModuleMetadata.CreateFromMetadata((IntPtr)pointer, bytes.Length, () => GC.KeepAlive(bytes));
            return AssemblyMetadata.Create(module).GetReference(filePath: path);
        }

        // roslyn only needs a reference image. Rewriting table flags in a copy preserves assembly
        // identity and signatures while allowing generated source to bind private target symbols.
        void RewriteTable(MetadataReader reader, TableIndex table, int flagsOffset, Func<uint, uint> rewrite)
        {
            int rowCount = reader.GetTableRowCount(table);
            if (rowCount == 0)
                return;

            int rowSize = reader.GetTableRowSize(table);
            int tableOffset = reader.GetTableMetadataOffset(table);
            int width = table == TableIndex.TypeDef ? sizeof(uint) : sizeof(ushort);
            for (int row = 0; row < rowCount; ++row)
            {
                int offset = tableOffset + row * rowSize + flagsOffset;
                var value = width == sizeof(uint)
                    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, width))
                    : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, width));
                var rewritten = rewrite(value);
                if (width == sizeof(uint))
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, width), rewritten);
                else
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, width), checked((ushort)rewritten));
            }
        }
    }

    static uint RewriteTypeAttributes(uint value)
    {
        var attributes = (TypeAttributes)value;
        var visibility = attributes & TypeAttributes.VisibilityMask;
        var rewritten = visibility is TypeAttributes.NestedPublic
            or TypeAttributes.NestedPrivate
            or TypeAttributes.NestedFamily
            or TypeAttributes.NestedAssembly
            or TypeAttributes.NestedFamANDAssem
            or TypeAttributes.NestedFamORAssem
                ? TypeAttributes.NestedPublic
                : TypeAttributes.Public;
        return (uint)((attributes & ~TypeAttributes.VisibilityMask) | rewritten);
    }

    static uint RewriteFieldAttributes(uint value) =>
        (uint)(((FieldAttributes)value & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public);

    static uint RewriteMethodAttributes(uint value) =>
        (uint)(((MethodAttributes)value & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public);
}
