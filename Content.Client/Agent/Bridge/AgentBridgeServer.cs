using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Client.Agent.Bridge;

/// <summary>
///     Loopback line-protocol server the MCP process connects to. One frame per line, each frame
///     a single JSON object.
/// </summary>
/// <remarks>
///     <para>
///     Everything here runs off the game thread. It never touches an entity, a component or any
///     other engine state — it moves strings across a socket and nothing else. The only contact
///     with the game is <see cref="TryDequeue"/> and <see cref="Send"/>, both of which are called
///     from <see cref="AgentBridgeSystem"/> on the main thread and are backed by concurrent queues.
///     Keep it that way: RobustToolbox's entity API is not thread-safe.
///     </para>
///     <para>
///     Bound to <see cref="IPAddress.Loopback"/> only, never a routable address.
///     </para>
/// </remarks>
public sealed class AgentBridgeServer : IDisposable
{
    /// <summary>
    ///     Longest accepted frame. A frame this large means something has gone wrong upstream;
    ///     dropping the connection beats growing a buffer without bound.
    /// </summary>
    private const int MaxFrameChars = 1 << 20;

    private readonly ISawmill _log;
    private readonly ConcurrentQueue<string> _inbound = new();
    private readonly ConcurrentQueue<string> _outbound = new();
    private readonly SemaphoreSlim _outboundSignal = new(0);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    // Written from the accept/receive tasks, read from the game thread. int rather than the enum
    // so Volatile can be used on it directly.
    private int _state = (int) AgentConnectionState.Disconnected;

    private string _token = string.Empty;

    public AgentBridgeServer(ISawmill log)
    {
        _log = log;
    }

    public AgentConnectionState State => (AgentConnectionState) Volatile.Read(ref _state);

    /// <summary>
    ///     Begins listening. Safe to call when already listening — does nothing.
    /// </summary>
    public void Start(int port, string token)
    {
        if (_listener != null)
            return;

        _token = token;

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
        }
        catch (SocketException e)
        {
            _log.Error($"Copilot bridge could not listen on 127.0.0.1:{port}: {e.Message}");
            _listener = null;
            return;
        }

        _cts = new CancellationTokenSource();
        Volatile.Write(ref _state, (int) AgentConnectionState.Connecting);

        _log.Info($"Copilot bridge listening on 127.0.0.1:{port}"
                  + (string.IsNullOrEmpty(_token) ? " (no token required)" : " (token required)"));

        Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();

        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // Shutting down anyway.
        }

        _listener = null;
        _cts = null;

        Volatile.Write(ref _state, (int) AgentConnectionState.Disconnected);

        while (_inbound.TryDequeue(out _)) { }
        while (_outbound.TryDequeue(out _)) { }
    }

    /// <summary>
    ///     Takes the next frame received from the copilot, if any. Main thread.
    /// </summary>
    public bool TryDequeue([NotNullWhen(true)] out string? frame)
    {
        return _inbound.TryDequeue(out frame);
    }

    /// <summary>
    ///     Queues a frame for the copilot. Main thread. Drops silently when nothing is connected —
    ///     the copilot is optional and the game must not care whether it is there.
    /// </summary>
    public void Send(string frame)
    {
        if (State != AgentConnectionState.Connected)
            return;

        _outbound.Enqueue(frame);
        _outboundSignal.Release();
    }

    private async Task AcceptLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _log.Error($"Copilot bridge accept failed: {e.Message}");
                return;
            }

            // One copilot at a time. Serving the connection inline means a second one simply waits
            // in the accept backlog rather than racing the first.
            try
            {
                await ServeAsync(client, cancel);
            }
            catch (Exception e)
            {
                _log.Warning($"Copilot bridge connection ended: {e.Message}");
            }
            finally
            {
                client.Dispose();
                Volatile.Write(ref _state, (int) AgentConnectionState.Connecting);
            }
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancel)
    {
        client.NoDelay = true;

        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, leaveOpen: true)
        {
            AutoFlush = false,
        };

        if (!await AuthenticateAsync(reader, writer, cancel))
            return;

        Volatile.Write(ref _state, (int) AgentConnectionState.Connected);
        _log.Info("Copilot connected.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);

        var receive = ReceiveLoopAsync(reader, linked.Token);
        var send = SendLoopAsync(writer, linked.Token);

        // Whichever side fails first tears down the other.
        await Task.WhenAny(receive, send);
        await linked.CancelAsync();

        _log.Info("Copilot disconnected.");
    }

    /// <summary>
    ///     The copilot's first line must be a hello frame. When a token is configured it must match.
    /// </summary>
    private async Task<bool> AuthenticateAsync(StreamReader reader, StreamWriter writer, CancellationToken cancel)
    {
        var hello = await reader.ReadLineAsync(cancel);

        if (hello == null)
            return false;

        if (string.IsNullOrEmpty(_token))
            return true;

        // Deliberately crude: the token is a fixed string on a loopback socket, so a JSON parse
        // here would be more code for no more safety. Phase 4 should replace this wholesale.
        if (hello.Contains($"\"token\":\"{_token}\""))
            return true;

        _log.Warning("Copilot bridge rejected a connection: bad or missing token.");

        await writer.WriteLineAsync("""{"ok":false,"error":{"code":"unauthorized"}}""");
        await writer.FlushAsync(cancel);

        return false;
    }

    private async Task ReceiveLoopAsync(StreamReader reader, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancel);

            if (line == null)
                return;

            if (line.Length == 0)
                continue;

            if (line.Length > MaxFrameChars)
            {
                _log.Error($"Copilot sent a {line.Length} char frame, dropping connection.");
                return;
            }

            _inbound.Enqueue(line);
        }
    }

    private async Task SendLoopAsync(StreamWriter writer, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            await _outboundSignal.WaitAsync(cancel);

            while (_outbound.TryDequeue(out var frame))
            {
                await writer.WriteLineAsync(frame.AsMemory(), cancel);
            }

            await writer.FlushAsync(cancel);
        }
    }

    public void Dispose()
    {
        Stop();
        _outboundSignal.Dispose();
    }
}
