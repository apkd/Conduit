#nullable enable

using System;
using System.IO;

namespace Conduit
{
    sealed class EditorBridgeConnection : IDisposable
    {
        readonly Func<bool> isConnected;
        bool disposed;

        internal EditorBridgeConnection(
            Stream input,
            Stream output,
            Func<bool> isConnected)
        {
            Input = input;
            Output = output;
            this.isConnected = isConnected;
        }

        internal Stream Input { get; }

        internal Stream Output { get; }

        internal bool IsConnected => !disposed && isConnected();

        internal static EditorBridgeConnection FromSingleStream(
            Stream stream,
            Func<bool> isConnected) =>
            new(stream, stream, isConnected);

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            if (!ReferenceEquals(Input, Output))
                Input.Dispose();
            Output.Dispose();
        }
    }
}
