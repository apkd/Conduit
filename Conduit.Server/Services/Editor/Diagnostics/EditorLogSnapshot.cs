namespace Conduit;

readonly record struct EditorLogSnapshot(long Length, DateTimeOffset? LastWriteUtc)
{
    internal bool HasActivitySince(EditorLogSnapshot previous) =>
        Length != previous.Length || LastWriteUtc != previous.LastWriteUtc;
}
