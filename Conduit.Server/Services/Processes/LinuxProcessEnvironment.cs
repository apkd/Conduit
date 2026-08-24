using System.Text;

namespace Conduit;

sealed class LinuxProcessEnvironment
{
    // one snapshot keeps compositor probes consistent and avoids reopening /proc for every variable.
    readonly byte[]? bytes;
    readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

    LinuxProcessEnvironment(byte[]? bytes) => this.bytes = bytes;

    internal static LinuxProcessEnvironment Read(int processId) =>
        new(TryReadProcessEnvironment(processId));

    internal string? GetValue(string name)
    {
        if (values.TryGetValue(name, out var cachedValue))
            return cachedValue;

        var value = TryReadValue(name);
        values.Add(name, value);
        return value;
    }

    string? TryReadValue(string name)
    {
        if (bytes is null)
            return null;

        try
        {
            var nameByteCount = Encoding.UTF8.GetByteCount(name);
            Span<byte> encodedName = nameByteCount <= 128
                ? stackalloc byte[nameByteCount]
                : new byte[nameByteCount];
            Encoding.UTF8.GetBytes(name, encodedName);

            var offset = 0;
            while (offset < bytes.Length)
            {
                var terminatorOffset = Array.IndexOf(bytes, (byte)0, offset);
                if (terminatorOffset < 0)
                    terminatorOffset = bytes.Length;

                var length = terminatorOffset - offset;
                if (length > nameByteCount
                    && bytes[offset + nameByteCount] == (byte)'='
                    && bytes.AsSpan(offset, nameByteCount).SequenceEqual(encodedName))
                    return Encoding.UTF8.GetString(
                        bytes,
                        offset + nameByteCount + 1,
                        length - nameByteCount - 1
                    );

                offset = terminatorOffset + 1;
            }
        }
        catch { }

        return null;
    }

    static byte[]? TryReadProcessEnvironment(int processId)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            return File.ReadAllBytes($"/proc/{processId}/environ");
        }
        catch
        {
            return null;
        }
    }
}
