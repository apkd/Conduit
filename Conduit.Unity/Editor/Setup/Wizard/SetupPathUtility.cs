#nullable enable

using System;
using System.IO;
using UnityEngine;

namespace Conduit
{
    static class SetupPathUtility
    {
        internal static string Combine(params string[] segments)
        {
            if (segments.Length == 0)
                return string.Empty;

            string path = segments[0];
            for (int index = 1, count = segments.Length; index < count; ++index)
                path = Path.Combine(path, segments[index]);

            return Path.GetFullPath(path);
        }

        internal static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    Application.platform == RuntimePlatform.WindowsEditor
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal
                );
            }
            catch
            {
                return false;
            }
        }
    }
}
