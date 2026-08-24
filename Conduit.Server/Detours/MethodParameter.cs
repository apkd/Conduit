using System.Reflection;

namespace Conduit;

readonly record struct MethodParameter(
    CSharpType Type,
    ParameterAttributes Attributes,
    bool IsRefReadonly)
{
    public string Display => Prefix + Type.BareDisplay;

    public string Declaration(string name) => Prefix + Type.Source + " " + name;

    string Prefix => (Type.IsByRef, Attributes) switch
    {
        (false, _) => string.Empty,
        _ when IsRefReadonly || Type.HasModifier("System.Runtime.CompilerServices.RequiresLocationAttribute") => "ref readonly ",
        (_, var attributes) when (attributes & ParameterAttributes.Out) != 0 => "out ",
        (_, var attributes) when (attributes & ParameterAttributes.In) != 0 || Type.IsReadOnly => "in ",
        _ => "ref ",
    };
}
