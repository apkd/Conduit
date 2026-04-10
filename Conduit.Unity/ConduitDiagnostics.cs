#nullable enable

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Conduit
{
    static class ConduitDiagnostics
    {
        static readonly object gate = new();

        static string LogPath
            => ConduitPaths.GetDiagnosticsLogPath();

        public static void Info(string message)
            => Write("INFO", message, null);

        public static void Warn(string message)
            => Write("WARN", message, null);

        public static void Error(string message, Exception? exception)
            => Write("ERROR", message, exception);

        static void Write(string level, string message, Exception? exception)
        {
            try
            {
                if (Path.GetDirectoryName(LogPath) is { Length: > 0 } directoryPath)
                    Directory.CreateDirectory(directoryPath);

                var builder = new StringBuilder();
                builder.Append(DateTime.UtcNow.ToString("O"));
                builder.Append(" [");
                builder.Append(level);
                builder.Append("] [T");
                builder.Append(Thread.CurrentThread.ManagedThreadId);
                builder.Append("] ");
                builder.Append(message);
                builder.AppendLine();

                if (exception != null)
                {
                    builder.AppendLine($"{exception.GetType().FullName}: {exception.Message}");
                    if (exception.StackTrace is { Length: > 0 })
                        builder.AppendLine(exception.StackTrace);
                }

                lock (gate)
                    File.AppendAllText(LogPath, builder.ToString());
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }
}
