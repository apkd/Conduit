using System.IO.Pipes;

namespace Conduit;

sealed partial class BridgeTransport
{
    static async Task<BridgeTransport> ConnectNamedPipeAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
            return FromStream(pipe, () => pipe.IsConnected, () => pipe.DisposeAsync());
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }
}
