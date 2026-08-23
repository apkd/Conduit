#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit
{
    // filesystem notifications remove permanent FIFO-directory polling; periodic scans remain as a loss-safe fallback.
    sealed class BridgeFilePublicationWatcher : IDisposable
    {
        static readonly TimeSpan notificationFallbackDelay = TimeSpan.FromMilliseconds(250);
        static readonly TimeSpan pollingDelay = TimeSpan.FromMilliseconds(50);
        readonly SemaphoreSlim signal = new(0, 1);
        readonly FileSystemWatcher? watcher;
        int notificationFailed;
        int disposed;

        public BridgeFilePublicationWatcher(string directory, string fileName)
        {
            FileSystemWatcher? createdWatcher = null;
            try
            {
                createdWatcher = new(directory, fileName)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName,
                };
                createdWatcher.Created += OnPublished;
                createdWatcher.Renamed += OnRenamed;
                createdWatcher.Error += OnError;
                createdWatcher.EnableRaisingEvents = true;
                watcher = createdWatcher;
            }
            catch
            {
                createdWatcher?.Dispose();
                Volatile.Write(ref notificationFailed, 1);
            }
        }

        public Task<bool> WaitAsync(CancellationToken cancellationToken)
            => signal.WaitAsync(
                Volatile.Read(ref notificationFailed) == 0
                    ? notificationFallbackDelay
                    : pollingDelay,
                cancellationToken
            );

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            watcher?.Dispose();
            signal.Dispose();
        }

        void OnPublished(object sender, FileSystemEventArgs args) => Signal();

        void OnRenamed(object sender, RenamedEventArgs args) => Signal();

        void OnError(object sender, ErrorEventArgs args)
        {
            Volatile.Write(ref notificationFailed, 1);
            Signal();
        }

        void Signal()
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            try
            {
                if (signal.CurrentCount == 0)
                    signal.Release();
            }
            catch (Exception exception) when (exception is ObjectDisposedException or SemaphoreFullException) { }
        }
    }
}
