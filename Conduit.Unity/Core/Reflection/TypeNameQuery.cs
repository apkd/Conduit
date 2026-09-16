#nullable enable

using System;

namespace Conduit
{
    readonly struct TypeNameQuery
    {
        internal readonly string Text;
        internal readonly Type? Alias;
        internal readonly bool HasGenericDisplay;
        internal readonly bool HasNestedDisplay;
        internal readonly bool HasAssembly;

        internal TypeNameQuery(string text)
        {
            Text = text;
            Alias = ReflectionTypeAliases.Resolve(text);
            HasGenericDisplay = text.IndexOf('<') >= 0;
            HasNestedDisplay = text.IndexOf('.') >= 0;
            HasAssembly = text.IndexOf(',') >= 0;
        }
    }
}
