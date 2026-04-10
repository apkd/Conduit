using System.Collections.Frozen;

namespace Conduit;

static class EnumFormattingExtensions
{
    public static string ToStringNoAlloc<TEnum>(this TEnum value)
        where TEnum : struct, Enum
        => EnumStringCache<TEnum>.Get(value);
}

static class EnumStringCache<TEnum>
    where TEnum : struct, Enum
{
    static readonly FrozenDictionary<TEnum, string> namedValues = CreateNamedValues();
    static readonly Lock gate = new();
    static Dictionary<TEnum, string>? dynamicValues;

    public static string Get(TEnum value)
    {
        if (namedValues.TryGetValue(value, out var text))
            return text;

        lock (gate)
        {
            dynamicValues ??= [];
            if (dynamicValues.TryGetValue(value, out text))
                return text;

            text = value.ToString();
            dynamicValues.Add(value, text);
            return text;
        }
    }

    static FrozenDictionary<TEnum, string> CreateNamedValues()
    {
        var values = Enum.GetValues<TEnum>();
        var names = new Dictionary<TEnum, string>(values.Length);
        foreach (var value in values)
            names.TryAdd(value, value.ToString());

        return names.ToFrozenDictionary();
    }
}
