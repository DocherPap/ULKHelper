using System.IO.Pipes;
using System.Text;

namespace ULKHelper.Automation;

internal sealed class RemoteCommandPipe : IDisposable
{
    private readonly CancellationTokenSource shutdown = new();

    public RemoteCommandPipe(Func<string, CancellationToken, Task<string>> dispatch, Action<Exception> log)
    {
        _ = Run(dispatch, log);
    }

    private async Task Run(Func<string, CancellationToken, Task<string>> dispatch, Action<Exception> log)
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream("ULKHelper.Discord", PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    var buffer = new byte[256];
                    var length = 0;
                    var end = -1;
                    while (length < buffer.Length)
                    {
                        var count = await pipe.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        end = Array.IndexOf(buffer, (byte)'\n', length, count);
                        if (end >= 0) break;
                        length += count;
                    }

                    if (end < 0) continue;
                    var response = await dispatch(Encoding.UTF8.GetString(buffer, 0, end), timeout.Token)
                        .WaitAsync(timeout.Token).ConfigureAwait(false);
                    // Keep one pipe line per reply while preserving multi-line weather reports.
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(response.Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n") + "\n"), timeout.Token).ConfigureAwait(false);
                }
                catch (IOException) { }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception ex) { log(ex); }
        finally { shutdown.Dispose(); }
    }

    public void Dispose()
    {
        try { shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
