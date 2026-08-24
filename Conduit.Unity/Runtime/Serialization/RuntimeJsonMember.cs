#nullable enable

using System;

namespace Conduit.Runtime
{
    readonly struct RuntimeJsonMember
    {
        internal RuntimeJsonMember(string name, string source)
        {
            Name = name;
            Source = source;
        }

        internal string Name { get; }
        internal string Source { get; }
        internal bool IsNull => string.Equals(Source, "null", StringComparison.Ordinal);
        internal bool IsString => Source.Length >= 2 && Source[0] == '"';
        internal bool IsObject => Source.Length > 0 && Source[0] == '{';
    }
}
