using System.Net.Sockets;
using System.Text.Json;

namespace Agents.Cli;

/// <summary>
/// Out-of-process kill switch for <c>agents run</c>. <see cref="Arm"/> binds a Unix-domain socket at
/// a well-known path and writes a handle file; a separate <c>agents kill</c> process connects to that
/// socket (<see cref="Signal"/>), which trips the run's <see cref="CancellationTokenSource"/> and lets
/// the loop stop cleanly between actions.
/// </summary>
/// <remarks>
/// A socket is chosen over SIGTERM deliberately. Connecting can only ever reach the process that is
/// actively listening, so a crashed agent simply refuses the connection — we never risk signalling a
/// reused PID, and there is no signal-number P/Invoke to get wrong. On Wayland the app itself cannot
/// grab a global hotkey, so the user binds <c>agents kill</c> to a desktop shortcut; this class is the
/// endpoint that shortcut talks to. The whole channel is BCL-only (<see cref="System.Net.Sockets"/>).
/// </remarks>
internal sealed class KillSwitch : IDisposable
{
    private readonly Socket _listener;
    private readonly string _socketPath;
    private readonly string _handlePath;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;

    private KillSwitch(Socket listener, string socketPath, string handlePath, CancellationTokenSource runCts)
    {
        _listener = listener;
        _socketPath = socketPath;
        _handlePath = handlePath;
        _accept = AcceptAsync(runCts);
    }

    /// <summary>
    /// Bind the control socket, start listening, and write the handle file. On the first inbound
    /// connection the supplied <paramref name="runCts"/> is cancelled.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another agent already holds a live control socket.</exception>
    public static KillSwitch Arm(CancellationTokenSource runCts)
    {
        ArgumentNullException.ThrowIfNull(runCts);

        var socketPath = RuntimePaths.KillSocketPath;
        var handlePath = RuntimePaths.HandleFilePath;

        // Refuse to start a second run that would stomp a live control socket (and orphan its owner).
        if (IsListenerLive(socketPath))
        {
            throw new InvalidOperationException(
                $"another `agents run` is already live (control socket {socketPath}); " +
                "stop it with `agents kill` first");
        }

        // A leftover inode from a crashed run would make bind() fail with EADDRINUSE.
        TryDelete(socketPath);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(backlog: 1);
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        WriteHandle(handlePath, socketPath);
        return new KillSwitch(listener, socketPath, handlePath, runCts);
    }

    /// <summary>
    /// Connect to a running agent's control socket to request cancellation. Returns false when no
    /// agent is listening (nothing running, or only a stale socket file remains).
    /// </summary>
    public static bool Signal()
    {
        var socketPath = ReadHandleSocketPath() ?? RuntimePaths.KillSocketPath;
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(socketPath));

            // The connect alone already unblocks the peer's Accept; the byte just makes intent explicit.
            client.Send(new byte[] { 1 });
            return true;
        }
        catch (SocketException)
        {
            return false; // connection refused / address not available -> no live listener
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task AcceptAsync(CancellationTokenSource runCts)
    {
        try
        {
            using var client = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
            CliApp.Log("kill received — cancelling run");
            runCts.Cancel();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: the run finished and Dispose() cancelled the accept.
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();

        try
        {
            _listener.Dispose();
        }
        catch
        {
            // Best effort — we are tearing down anyway.
        }

        try
        {
            _accept.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore: the accept loop is bounded and about to be abandoned.
        }

        TryDelete(_socketPath);
        TryDelete(_handlePath);
        _stop.Dispose();
    }

    /// <summary>True when something is actively accepting on <paramref name="socketPath"/>.</summary>
    private static bool IsListenerLive(string socketPath)
    {
        if (!File.Exists(socketPath))
        {
            return false;
        }

        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch
        {
            return false; // refused / gone -> the file is stale
        }
    }

    private static void WriteHandle(string handlePath, string socketPath)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new HandleFile(Environment.ProcessId, socketPath, DateTimeOffset.UtcNow));
            File.WriteAllText(handlePath, payload);
        }
        catch
        {
            // The handle file is advisory (observability + kill's socket lookup); the well-known
            // socket path is the real channel, so a write failure here is non-fatal.
        }
    }

    private static string? ReadHandleSocketPath()
    {
        try
        {
            if (!File.Exists(RuntimePaths.HandleFilePath))
            {
                return null;
            }

            var handle = JsonSerializer.Deserialize<HandleFile>(File.ReadAllText(RuntimePaths.HandleFilePath));
            return string.IsNullOrWhiteSpace(handle?.Socket) ? null : handle.Socket;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort — a leftover file is cleaned on the next Arm() anyway.
        }
    }

    private sealed record HandleFile(int Pid, string Socket, DateTimeOffset StartedUtc);
}
