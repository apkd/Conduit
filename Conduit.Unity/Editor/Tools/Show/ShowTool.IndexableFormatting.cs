#nullable enable

using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Conduit
{
    static partial class ShowTool
    {
        static bool TryFormatIndexable(object value, int depth, out string text)
        {
            var access = indexableAccessCache.GetOrAdd(value.GetType(), CreateIndexableAccess);
            if (!access.Available)
            {
                text = string.Empty;
                return false;
            }

            text = FormatIndexable(value, depth, access);
            return true;
        }

        static IndexableAccess CreateIndexableAccess(Type type)
        {
            foreach (var candidate in type.GetInterfaces())
            {
                if (!candidate.IsGenericType
                    || candidate.GetGenericTypeDefinition().FullName != "Unity.Collections.IIndexable`1")
                    continue;

                var elementType = candidate.GetGenericArguments()[0];
                var lengthGetter = candidate.GetProperty("Length")?.GetMethod;
                var elementAt = candidate.GetMethod("ElementAt", new[] { typeof(int) });
                if (lengthGetter == null || elementAt == null)
                    continue;

                return new(
                    true,
                    elementType,
                    CreateIndexableLengthAccessor(candidate, lengthGetter),
                    CreateIndexableElementAccessor(candidate, elementAt, elementType)
                );
            }

            return IndexableAccess.Unavailable;
        }

        static Func<object, int> CreateIndexableLengthAccessor(Type indexableType, MethodInfo lengthGetter)
        {
            var method = new DynamicMethod(
                "GetIIndexableLength",
                typeof(int),
                new[] { typeof(object) },
                typeof(ShowTool).Module,
                true
            );
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, indexableType);
            il.Emit(OpCodes.Callvirt, lengthGetter);
            il.Emit(OpCodes.Ret);
            return (Func<object, int>)method.CreateDelegate(typeof(Func<object, int>));
        }

        static Func<object, int, object?> CreateIndexableElementAccessor(Type indexableType, MethodInfo elementAt, Type elementType)
        {
            var method = new DynamicMethod(
                "GetIIndexableElement",
                typeof(object),
                new[] { typeof(object), typeof(int) },
                typeof(ShowTool).Module,
                true
            );
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, indexableType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, elementAt);
            il.Emit(OpCodes.Ldobj, elementType);
            if (elementType.IsValueType)
                il.Emit(OpCodes.Box, elementType);

            il.Emit(OpCodes.Ret);
            return (Func<object, int, object?>)method.CreateDelegate(typeof(Func<object, int, object?>));
        }

        static string FormatIndexable(object value, int depth, IndexableAccess access)
        {
            var getLength = access.GetLength!;
            var getElement = access.GetElement!;
            var count = getLength(value);
            if (count <= 0)
                return access.ElementType == typeof(bool) ? string.Empty : "[]";

            var previewCount = GetPreviewCount(access.ElementType);
            if (access.ElementType == typeof(bool))
            {
                using var pooledBits = ConduitPool.GetStringBuilder(out var bits);
                var visibleCount = count <= previewCount ? count : previewCount - 1;
                for (var index = 0; index < visibleCount; index++)
                    bits.Append(getElement(value, index) is true ? '1' : '0');

                if (count <= previewCount)
                    return bits.ToString();

                var lastBit = getElement(value, count - 1) is true ? '1' : '0';
                bits.Append("...").Append(lastBit).Append(" (n=").Append(count).Append(')');
                return bits.ToString();
            }

            using var pooledPreview = ConduitPool.GetStringBuilder(out var preview);
            preview.Append('[');
            var appendedCount = 0;
            var visibleItems = count <= previewCount ? count : previewCount - 1;
            for (var index = 0; index < visibleItems; index++)
                AppendPreviewItem(
                    preview,
                    ref appendedCount,
                    FormatValue(getElement(value, index), depth)
                );

            if (count <= previewCount)
                return preview.Append(']').ToString();

            AppendPreviewItem(preview, ref appendedCount, "...");
            AppendPreviewItem(
                preview,
                ref appendedCount,
                FormatValue(getElement(value, count - 1), depth)
            );
            return preview.Append("] (n=").Append(count).Append(')').ToString();
        }

    }
}
