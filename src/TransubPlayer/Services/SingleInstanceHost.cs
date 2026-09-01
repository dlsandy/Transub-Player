using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace TransubPlayer.Services;

/// <summary>One primary window; later launches forward file paths over a named pipe.</summary>
internal static class SingleInstanceHost
{
    private const string MutexName = @"Global\TransubPlayer.SingleInstance.v1";
    private const string PipeName = "TransubPlayer.OpenFiles.v1";
    private const int MaxPayloadBytes = 256 * 1024;
    private const int AsfwAny = -1;

    private static Mutex? _mutex;
    private static CancellationTokenSource? _listenerCts;

    public static bool TryBecomePrimary(out string[] startupArgs)
    {
        startupArgs = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(NormalizeArg)
            .Where(a => a.Length > 0)
            .ToArray();

        // initiallyOwned: false + WaitOne so an abandoned mutex after a crash is reclaimable.
        _mutex = new Mutex(initiallyOwned: false, MutexName, out _);
        bool owned;
        try
        {
            owned = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (owned)
            return true;

        ForwardToPrimary(startupArgs);
        try { _mutex.Dispose(); } catch { /* ignore */ }
        _mutex = null;
        return false;
    }

    public static void StartListening(Action<string[]> onOpen)
    {
        _listenerCts?.Cancel();
        _listenerCts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(onOpen, _listenerCts.Token));
    }

    public static void StopListening()
    {
        try { _listenerCts?.Cancel(); } catch { /* ignore */ }
        try { _listenerCts?.Dispose(); } catch { /* ignore */ }
        _listenerCts = null;
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // may already be released / abandoned
        }

        try { _mutex?.Dispose(); } catch { /* ignore */ }
        _mutex = null;
    }

    private static void ForwardToPrimary(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return;

        // This process was just launched by Explorer; grant the running instance foreground rights.
        AllowSetForegroundWindow(AsfwAny);

        var payload = Encoding.UTF8.GetBytes(string.Join('\0', args));
        if (payload.Length > MaxPayloadBytes)
            payload = payload.AsSpan(0, MaxPayloadBytes).ToArray();

        // Retry: primary may still be starting the pipe listener.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(400);
                client.Write(payload, 0, payload.Length);
                return;
            }
            catch
            {
                Thread.Sleep(120 + attempt * 40);
            }
        }
    }

    private static async Task ListenLoop(Action<string[]> onOpen, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var ms = new MemoryStream(Math.Min(MaxPayloadBytes, 16 * 1024));
                var buffer = new byte[8192];
                while (ms.Length < MaxPayloadBytes)
                {
                    var read = await server.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read <= 0) break;
                    ms.Write(buffer, 0, read);
                    if (read < buffer.Length) break;
                }

                if (ms.Length <= 0) continue;

                var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                var paths = text.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (paths.Length == 0) continue;

                var app = Application.Current;
                if (app is null) continue;
                app.Dispatcher.Invoke(() => onOpen(paths));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(300, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static string NormalizeArg(string arg)
    {
        arg = arg.Trim();
        if (arg.StartsWith('"') && arg.EndsWith('"') && arg.Length >= 2)
            arg = arg[1..^1];
        return arg.Trim();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
