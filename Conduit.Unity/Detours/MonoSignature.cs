#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Conduit
{
    /// <summary>Recovers function-pointer signatures that Unity Mono hides behind MonoFNPtrFakeClass.</summary>
    static class MonoSignature
    {
        const int FunctionPointerTypeCode = 27;
        static readonly Lazy<Exports> exports = new(CreateExports);

        public static bool IsFunctionPointer(Type type)
            => type.FullName == "System.MonoFNPtrFakeClass";

        public static string FormatFunctionPointer(Type type)
        {
            var api = exports.Value;
            var monoType = type.TypeHandle.Value;
            if (api.TypeGetType(monoType) != FunctionPointerTypeCode)
                throw new InvalidOperationException($"'{type}' is not a Mono function-pointer type.");

            return FormatFunctionPointer(monoType, api);
        }

        public static string? GetUnsupportedReason(Type type)
        {
            var api = exports.Value;
            return GetUnsupportedReason(type.TypeHandle.Value, api);
        }

        static string? GetUnsupportedReason(IntPtr monoType, Exports api)
        {
            if (monoType == IntPtr.Zero)
                return "Mono returned an incomplete function-pointer signature";
            if (api.TypeGetType(monoType) != FunctionPointerTypeCode)
                return null;

            var signature = api.TypeGetSignature(monoType);
            if (signature == IntPtr.Zero)
                return "Mono did not expose the function-pointer signature";
            if (api.SignatureGetCallConvention(signature) == 5)
                return "varargs function pointers are not supported";

            var iterator = IntPtr.Zero;
            for (var index = 0; index < api.SignatureGetParameterCount(signature); index++)
            {
                var reason = GetUnsupportedReason(api.SignatureGetParameters(signature, ref iterator), api);
                if (reason != null)
                    return reason;
            }

            return GetUnsupportedReason(api.SignatureGetReturnType(signature), api);
        }

        static string FormatFunctionPointer(IntPtr monoType, Exports api)
        {
            var signature = api.TypeGetSignature(monoType);
            if (signature == IntPtr.Zero)
                throw new InvalidOperationException("Mono did not expose the function-pointer signature.");

            var builder = new StringBuilder("delegate*");
            AppendCallingConvention(builder, api.SignatureGetCallConvention(signature));
            builder.Append('<');

            var parameterCount = api.SignatureGetParameterCount(signature);
            var iterator = IntPtr.Zero;
            for (var index = 0; index < parameterCount; index++)
            {
                if (index > 0)
                    builder.Append(", ");

                var parameter = api.SignatureGetParameters(signature, ref iterator);
                if (parameter == IntPtr.Zero)
                    throw new InvalidOperationException("Mono returned an incomplete function-pointer signature.");
                AppendSignatureType(builder, parameter, isReturn: false, api);
            }

            if (parameterCount > 0)
                builder.Append(", ");
            AppendSignatureType(builder, api.SignatureGetReturnType(signature), isReturn: true, api);
            return builder.Append('>').ToString();
        }

        static void AppendCallingConvention(StringBuilder builder, uint convention)
        {
            switch (convention)
            {
                case 0:
                    return;
                case 1:
                    builder.Append(" unmanaged[Cdecl]");
                    return;
                case 2:
                    builder.Append(" unmanaged[Stdcall]");
                    return;
                case 3:
                    builder.Append(" unmanaged[Thiscall]");
                    return;
                case 4:
                    builder.Append(" unmanaged[Fastcall]");
                    return;
                case 5:
                    builder.Append(" managed /* vararg */");
                    return;
                default:
                    builder.Append(" unmanaged");
                    return;
            }
        }

        static void AppendSignatureType(StringBuilder builder, IntPtr monoType, bool isReturn, Exports api)
        {
            if (monoType == IntPtr.Zero)
                throw new InvalidOperationException("Mono returned a null signature type.");

            if (api.TypeIsByRef(monoType) != 0)
                builder.Append(HasReadOnlyModifier(monoType, api)
                    ? isReturn ? "ref readonly " : "in "
                    : "ref ");

            if (api.TypeGetType(monoType) == FunctionPointerTypeCode)
            {
                builder.Append(FormatFunctionPointer(monoType, api));
                return;
            }

            builder.Append(FormatManagedTypeName(api.GetTypeName(monoType)));
        }

        static bool HasReadOnlyModifier(IntPtr monoType, Exports api)
        {
            var iterator = IntPtr.Zero;
            while (true)
            {
                var required = 0;
                var modifier = api.TypeGetModifiers(monoType, ref required, ref iterator);
                if (modifier == IntPtr.Zero)
                    return false;

                var @namespace = Marshal.PtrToStringAnsi(api.ClassGetNamespace(modifier)) ?? string.Empty;
                var name = Marshal.PtrToStringAnsi(api.ClassGetName(modifier)) ?? string.Empty;
                var fullName = @namespace.Length == 0 ? name : @namespace + "." + name;
                if (fullName is "System.Runtime.CompilerServices.IsReadOnlyAttribute"
                    or "System.Runtime.InteropServices.InAttribute"
                    or "System.Runtime.CompilerServices.RequiresLocationAttribute")
                    return true;
            }
        }

        static string FormatManagedTypeName(string name)
        {
            var index = 0;
            return ParseType();

            string ParseType()
            {
                var start = index;
                while (index < name.Length && name[index] is not ('[' or ']' or ','))
                    index++;

                var token = name.Substring(start, index - start).Replace('+', '.');
                var tick = token.LastIndexOf('`');
                var suffix = string.Empty;
                while (token.EndsWith("[]", StringComparison.Ordinal)
                       || token.EndsWith("*", StringComparison.Ordinal)
                       || token.EndsWith("&", StringComparison.Ordinal))
                {
                    if (token.EndsWith("[]", StringComparison.Ordinal))
                    {
                        suffix = "[]" + suffix;
                        token = token.Substring(0, token.Length - 2);
                    }
                    else
                    {
                        if (token[token.Length - 1] == '*')
                            suffix = "*" + suffix;
                        token = token.Substring(0, token.Length - 1);
                    }
                }

                var bareToken = tick < 0 ? token : token.Substring(0, tick);
                var builder = new StringBuilder(Alias(bareToken));
                if (tick >= 0 && index < name.Length && name[index] == '[')
                {
                    index++;
                    builder.Append('<');
                    var first = true;
                    while (index < name.Length && name[index] != ']')
                    {
                        if (!first)
                        {
                            if (name[index] == ',')
                                index++;
                            while (index < name.Length && char.IsWhiteSpace(name[index]))
                                index++;
                            builder.Append(", ");
                        }

                        builder.Append(ParseType());
                        first = false;
                    }

                    if (index < name.Length && name[index] == ']')
                        index++;
                    builder.Append('>');
                }

                return builder.Append(suffix).ToString();
            }
        }

        static string Alias(string name)
            => name switch
            {
                "System.Void"    => "void",
                "System.Boolean" => "bool",
                "System.Byte"    => "byte",
                "System.SByte"   => "sbyte",
                "System.Char"    => "char",
                "System.Decimal" => "decimal",
                "System.Double"  => "double",
                "System.Single"  => "float",
                "System.Int32"   => "int",
                "System.UInt32"  => "uint",
                "System.Int64"   => "long",
                "System.UInt64"  => "ulong",
                "System.Object"  => "object",
                "System.Int16"   => "short",
                "System.UInt16"  => "ushort",
                "System.String"  => "string",
                "System.IntPtr"  => "nint",
                "System.UIntPtr" => "nuint",
                _                => CSharpIdentifier.EscapeQualified(name),
            };

        static Exports CreateExports()
            => new(
                NativeSymbols.Resolve<MonoTypeGetSignature>("mono_type_get_signature"),
                NativeSymbols.Resolve<MonoSignatureGetReturnType>("mono_signature_get_return_type"),
                NativeSymbols.Resolve<MonoSignatureGetParameters>("mono_signature_get_params"),
                NativeSymbols.Resolve<MonoSignatureGetParameterCount>("mono_signature_get_param_count"),
                NativeSymbols.Resolve<MonoSignatureGetCallConvention>("mono_signature_get_call_conv"),
                NativeSymbols.Resolve<MonoTypeGetType>("mono_type_get_type"),
                NativeSymbols.Resolve<MonoTypeIsByRef>("mono_type_is_byref"),
                NativeSymbols.Resolve<MonoTypeGetNameFull>("mono_type_get_name_full"),
                NativeSymbols.Resolve<MonoTypeGetModifiers>("mono_type_get_modifiers"),
                NativeSymbols.Resolve<MonoClassGetName>("mono_class_get_name"),
                NativeSymbols.Resolve<MonoClassGetNamespace>("mono_class_get_namespace"),
                NativeSymbols.Resolve<MonoFree>("mono_free")
            );

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr MonoTypeGetSignature(IntPtr type);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr MonoSignatureGetReturnType(IntPtr signature);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr MonoSignatureGetParameters(IntPtr signature, ref IntPtr iterator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint MonoSignatureGetParameterCount(IntPtr signature);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint MonoSignatureGetCallConvention(IntPtr signature);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int MonoTypeGetType(IntPtr type);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int MonoTypeIsByRef(IntPtr type);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr MonoTypeGetNameFull(IntPtr type, int format);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr MonoTypeGetModifiers(IntPtr type, ref int isRequired, ref IntPtr iterator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr MonoClassGetName(IntPtr @class);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr MonoClassGetNamespace(IntPtr @class);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void MonoFree(IntPtr memory);

        sealed class Exports
        {
            readonly MonoTypeGetNameFull typeGetNameFull;
            readonly MonoFree free;

            internal Exports(
                MonoTypeGetSignature typeGetSignature,
                MonoSignatureGetReturnType signatureGetReturnType,
                MonoSignatureGetParameters signatureGetParameters,
                MonoSignatureGetParameterCount signatureGetParameterCount,
                MonoSignatureGetCallConvention signatureGetCallConvention,
                MonoTypeGetType typeGetType,
                MonoTypeIsByRef typeIsByRef,
                MonoTypeGetNameFull typeGetNameFull,
                MonoTypeGetModifiers typeGetModifiers,
                MonoClassGetName classGetName,
                MonoClassGetNamespace classGetNamespace,
                MonoFree free)
            {
                TypeGetSignature = typeGetSignature;
                SignatureGetReturnType = signatureGetReturnType;
                SignatureGetParameters = signatureGetParameters;
                SignatureGetParameterCount = signatureGetParameterCount;
                SignatureGetCallConvention = signatureGetCallConvention;
                TypeGetType = typeGetType;
                TypeIsByRef = typeIsByRef;
                this.typeGetNameFull = typeGetNameFull;
                TypeGetModifiers = typeGetModifiers;
                ClassGetName = classGetName;
                ClassGetNamespace = classGetNamespace;
                this.free = free;
            }

            internal MonoTypeGetSignature TypeGetSignature { get; }
            internal MonoSignatureGetReturnType SignatureGetReturnType { get; }
            internal MonoSignatureGetParameters SignatureGetParameters { get; }
            internal MonoSignatureGetParameterCount SignatureGetParameterCount { get; }
            internal MonoSignatureGetCallConvention SignatureGetCallConvention { get; }
            internal MonoTypeGetType TypeGetType { get; }
            internal MonoTypeIsByRef TypeIsByRef { get; }
            internal MonoTypeGetModifiers TypeGetModifiers { get; }
            internal MonoClassGetName ClassGetName { get; }
            internal MonoClassGetNamespace ClassGetNamespace { get; }

            internal string GetTypeName(IntPtr type)
            {
                var pointer = typeGetNameFull(type, 1);
                if (pointer == IntPtr.Zero)
                    throw new InvalidOperationException("Mono did not expose a signature type name.");

                try
                {
                    return Marshal.PtrToStringAnsi(pointer) ?? string.Empty;
                }
                finally
                {
                    free(pointer);
                }
            }
        }
    }
}
