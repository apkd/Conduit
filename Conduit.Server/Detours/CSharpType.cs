using System.Collections.Immutable;

namespace Conduit;

sealed record CSharpType(
    string Source,
    string BareDisplay,
    bool IsByRef = false,
    bool IsReadOnly = false,
    bool IsValueType = false,
    ImmutableArray<string> Modifiers = default,
    string? UnsupportedReason = null)
{
    public string ReturnDeclaration => (IsByRef, IsReadOnly) switch
    {
        (true, true) => "ref readonly " + Source,
        (true, false) => "ref " + Source,
        _ => Source,
    };

    public string ReturnDisplay => (IsByRef, IsReadOnly) switch
    {
        (true, true) => "ref readonly " + BareDisplay,
        (true, false) => "ref " + BareDisplay,
        _ => BareDisplay,
    };

    public bool HasModifier(string modifier)
        => !Modifiers.IsDefault && Modifiers.Contains(modifier, StringComparer.Ordinal);

    public CSharpType WithModifier(string modifier, bool required)
    {
        var modifiers = Modifiers.IsDefault ? ImmutableArray<string>.Empty : Modifiers;
        bool isReadOnly = modifier is "System.Runtime.CompilerServices.IsReadOnlyAttribute"
            or "System.Runtime.InteropServices.InAttribute"
            or "System.Runtime.CompilerServices.RequiresLocationAttribute";
        bool supported = isReadOnly
                         || modifier.StartsWith(
                             "System.Runtime.CompilerServices.CallConv",
                             StringComparison.Ordinal
                         );
        return this with
        {
            IsReadOnly = IsReadOnly || isReadOnly,
            Modifiers = modifiers.Add(modifier),
            UnsupportedReason = UnsupportedReason ?? (required && !supported
                ? $"required custom modifier '{modifier}' cannot be represented exactly in C#"
                : null),
        };
    }
}
