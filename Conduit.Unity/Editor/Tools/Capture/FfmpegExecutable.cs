#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace Conduit
{

    static class FfmpegExecutable
    {
        static readonly object pathGate = new();
        static string? resolvedPath;

        internal static string Path
        {
            get
            {
                lock (pathGate)
                    return resolvedPath ??= Resolve();
            }
        }

        static string Resolve()
        {
            var configured = Environment.GetEnvironmentVariable("CONDUIT_FFMPEG_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (File.Exists(configured))
                    return configured;

                throw new InvalidOperationException(
                    $"CONDUIT_FFMPEG_PATH points to a missing file: {configured}"
                );
            }

            var executableName = Application.platform == RuntimePlatform.WindowsEditor
                ? "ffmpeg.exe"
                : "ffmpeg";
            var searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in searchPath.Split(System.IO.Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                var candidate = System.IO.Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }

            var standardPaths = Application.platform switch
            {
                RuntimePlatform.OSXEditor => new[]
                {
                    "/opt/homebrew/bin/ffmpeg",
                    "/usr/local/bin/ffmpeg",
                    "/usr/bin/ffmpeg",
                },
                RuntimePlatform.LinuxEditor => new[]
                {
                    "/usr/local/bin/ffmpeg",
                    "/usr/bin/ffmpeg",
                    "/run/current-system/sw/bin/ffmpeg",
                },
                _ => Array.Empty<string>(),
            };
            foreach (var candidate in standardPaths)
                if (File.Exists(candidate))
                    return candidate;

            throw new InvalidOperationException(
                "FFmpeg was not found. Install it on PATH or set CONDUIT_FFMPEG_PATH to its executable."
            );
        }
    }
}
