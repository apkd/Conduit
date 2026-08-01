using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Conduit;

static class MetadataPublicizer
{
    internal static byte[] Publicize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            throw new BadImageFormatException($"'{path}' has no managed metadata.");

        var reader = pe.GetMetadataReader();
        var metadataOffset = pe.PEHeaders.MetadataStartOffset;
        RewriteTable(TableIndex.TypeDef, 0, RewriteTypeAttributes);
        RewriteTable(TableIndex.Field, 0, RewriteFieldAttributes);
        RewriteTable(TableIndex.MethodDef, sizeof(uint) + sizeof(ushort), RewriteMethodAttributes);
        return bytes;

        // roslyn only needs a reference image. Rewriting table flags in a copy preserves assembly
        // identity and signatures while allowing generated source to bind private target symbols.
        void RewriteTable(TableIndex table, int flagsOffset, Func<uint, uint> rewrite)
        {
            int rowCount = reader.GetTableRowCount(table);
            if (rowCount == 0)
                return;

            int rowSize = reader.GetTableRowSize(table);
            int tableOffset = metadataOffset + reader.GetTableMetadataOffset(table);
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
