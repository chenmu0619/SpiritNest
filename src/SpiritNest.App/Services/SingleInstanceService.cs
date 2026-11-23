using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace SpiritNest.Services;

public sealed class SingleInstanceService : IDisposable
{
    // Bump name if a hung previous build leaves an undead process holding the old mutex.
    public const string MutexName = "Local\\SpiritNest.SingleInstance.Mutex.v3";
    public const string PipeName = "SpiritNest.SingleInstance.Pipe.v3";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private bool _owned;

    public bool TryAcquire()
    {
        _mutex = new Mutex(false, MutexName, out _);
        try
        {
            _owned = _mutex.WaitOne(TimeSpan.FromMilliseconds(200));
            return _owned;
        }
        catch (AbandonedMutexException)
        {
            // Previous process crashed without releasing — we now own it.
            _owned = true;
            return true;
        }
    }

    /// <summary>Ask the running instance to exit cleanly (for republish). Returns true if the pipe accepted.</summary>
    public static bool TryRequestExit() => TrySendCommand("exit");

    public static bool TrySendCommand(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(800);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartActivationServer(Action onActivate, Action? onExit = null)
    {
        if (!_owned) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(onActivate, onExit, _cts.Token));
    }

    private static async Task ListenLoopAsync(Action onActivate, Action? onExit, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (string.Equals(line, "activate", StringComparison.OrdinalIgnoreCase))
                    onActivate();
                else if (string.Equals(line, "exit", StringComparison.OrdinalIgnoreCase))
                    onExit?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(200, token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (_owned && _mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
        }
        _mutex?.Dispose();
    }
}
