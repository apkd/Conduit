#nullable enable

using System;
using System.Globalization;

namespace Conduit
{
    readonly struct ProfilerOptions
    {
        readonly string[] args;

        internal ProfilerOptions(string[]? args)
        {
            this.args = args ?? Array.Empty<string>();
        }

        internal string GetString(string key, string defaultValue)
        {
            // scan backwards to retain the former dictionary's last-value-wins behavior.
            for (var index = args.Length - 1; index >= 0; --index)
            {
                var argument = args[index];
                var separatorIndex = argument.IndexOf('=', StringComparison.Ordinal);
                if (separatorIndex < 0
                    || !argument.AsSpan(0, separatorIndex).Equals(
                        key.AsSpan(),
                        StringComparison.OrdinalIgnoreCase
                    ))
                    continue;

                return separatorIndex + 1 < argument.Length
                    ? argument[(separatorIndex + 1)..]
                    : defaultValue;
            }

            return defaultValue;
        }

        internal int GetInt(string key, int defaultValue, int min, int max)
        {
            var value = GetString(key, string.Empty);
            var parsed = int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var result
            )
                ? result
                : defaultValue;
            return Math.Min(max, Math.Max(min, parsed));
        }

        internal double GetDouble(string key, double defaultValue, double min, double max)
        {
            var value = GetString(key, string.Empty);
            var parsed = double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var result
            )
                ? result
                : defaultValue;
            return Math.Min(max, Math.Max(min, parsed));
        }

        internal bool GetBool(string key, bool defaultValue)
            => bool.TryParse(GetString(key, string.Empty), out var parsed) ? parsed : defaultValue;
    }
}
