using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Content.Client.Agent.Bridge;
using Robust.Shared.Configuration;

namespace Content.Client.Agent;

/// <summary>
///     Client-side owner of everything the copilot sees, hears and says.
/// </summary>
/// <remarks>
///     <para>
///     Phase 0 is scaffolding. The bridge exists, holds the state the side panel renders, and
///     raises the events the panel listens to — but there is no transport yet. Text the human
///     enters is answered locally so the panel can be exercised end to end before the WebSocket
///     lands in Phase 1.
///     </para>
///     <para>
///     Registration is by reflection: nothing upstream references this type. See PLAN.md §3.
///     </para>
/// </remarks>
public sealed partial class AgentBridgeSystem : EntitySystem
{
    /// <summary>
    ///     How many conversation lines and notifications are kept for replay into a freshly
    ///     opened panel. Old entries are dropped from the front.
    /// </summary>
    private const int HistoryLimit = 200;

    // Not readonly: RA0051 — the injector assigns [Dependency] fields after construction.
    [Dependency] private IConfigurationManager _cfg = default!;

    private readonly List<AgentMessage> _transcript = new();
    private readonly List<AgentNotification> _notifications = new();

    private AgentBridgeServer? _server;

    /// <summary>
    ///     Monotonic sequence stamped on every event, so the copilot can ask for everything after
    ///     a point it has already seen rather than re-reading the world.
    /// </summary>
    private ulong _cursor;

    public AgentConnectionState Connection { get; private set; } = AgentConnectionState.Disconnected;
    public AgentControlMode Mode { get; private set; } = AgentControlMode.Observe;

    public IReadOnlyList<AgentMessage> Transcript => _transcript;
    public IReadOnlyList<AgentNotification> Notifications => _notifications;

    public event Action<AgentMessage>? MessageAdded;
    public event Action<AgentNotification>? NotificationAdded;
    public event Action? StatusChanged;

    public override void Initialize()
    {
        base.Initialize();

        if (!_cfg.GetCVar(AgentCVars.BridgeEnabled))
        {
            Log.Debug("Copilot bridge disabled by cvar; running as a stock client.");
            return;
        }

        _server = new AgentBridgeServer(Log);
        _server.Start(_cfg.GetCVar(AgentCVars.BridgePort), _cfg.GetCVar(AgentCVars.BridgeToken));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_server == null)
            return;

        // The socket runs on its own threads and only ever moves strings. Everything that touches
        // game state happens here, on the game thread.
        SetConnection(_server.State);

        while (_server.TryDequeue(out var frame))
        {
            HandleFrame(frame);
        }
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _server?.Dispose();
        _server = null;

        MessageAdded = null;
        NotificationAdded = null;
        StatusChanged = null;

        _transcript.Clear();
        _notifications.Clear();

        Connection = AgentConnectionState.Disconnected;
    }

    /// <summary>
    ///     Called when the human enters a line in the side panel.
    /// </summary>
    public void SendToAgent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        AddMessage(AgentSpeaker.Human, text);

        if (Connection != AgentConnectionState.Connected)
        {
            AddMessage(AgentSpeaker.System, Loc.GetString("agent-no-bridge"));
            return;
        }

        RaiseBridgeEvent("human", new { text });
    }

    /// <summary>
    ///     Pushes an unsolicited event to the copilot, stamped with the next cursor value.
    /// </summary>
    private void RaiseBridgeEvent(string name, object payload)
    {
        if (_server == null)
            return;

        _server.Send(BridgeProtocol.Event(name, ++_cursor, payload));
    }

    /// <summary>
    ///     Dispatches one inbound frame. Game thread.
    /// </summary>
    private void HandleFrame(string frame)
    {
        int id;
        string? op;
        JsonElement root;

        try
        {
            using var doc = JsonDocument.Parse(frame);

            // Clone: the JsonDocument is disposed at the end of this scope, and params may be
            // read after that point.
            root = doc.RootElement.Clone();

            id = root.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
            op = root.TryGetProperty("op", out var opProp) ? opProp.GetString() : null;
        }
        catch (JsonException e)
        {
            Log.Warning($"Copilot sent unparseable frame: {e.Message}");
            _server?.Send(BridgeProtocol.Error(0, "bad_json", e.Message));
            return;
        }

        if (op == null)
        {
            _server?.Send(BridgeProtocol.Error(id, "missing_op"));
            return;
        }

        var parameters = root.TryGetProperty("params", out var p) ? p : default;

        switch (op)
        {
            case "ping":
                _server?.Send(BridgeProtocol.Ok(id, new { pong = true, cursor = _cursor }));
                break;

            case "snapshot":
                var radius = parameters.ValueKind == JsonValueKind.Object
                             && parameters.TryGetProperty("radius", out var r)
                             && r.TryGetInt32(out var parsed)
                    ? parsed
                    : 7;

                var snapshot = BuildSnapshot(radius);

                if (snapshot == null)
                {
                    _server?.Send(BridgeProtocol.Error(id, "no_player",
                        "not attached to an entity — lobby, ghost, or round restart"));
                    break;
                }

                _server?.Send(BridgeProtocol.Ok(id, snapshot));
                break;

            case "say_to_human":
                if (!TryGetString(parameters, "text", out var say))
                {
                    _server?.Send(BridgeProtocol.Error(id, "bad_params", "text is required"));
                    break;
                }

                AddMessage(AgentSpeaker.Agent, say);
                _server?.Send(BridgeProtocol.Ok(id));
                break;

            case "notify":
                if (!TryGetString(parameters, "text", out var text))
                {
                    _server?.Send(BridgeProtocol.Error(id, "bad_params", "text is required"));
                    break;
                }

                var urgency = TryGetString(parameters, "urgency", out var u)
                    ? ParseUrgency(u)
                    : AgentUrgency.Normal;

                Notify(text, urgency);
                _server?.Send(BridgeProtocol.Ok(id));
                break;

            default:
                _server?.Send(BridgeProtocol.Error(id, "unknown_op", op));
                break;
        }
    }

    private static bool TryGetString(JsonElement parameters, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;

        if (parameters.ValueKind != JsonValueKind.Object)
            return false;

        if (!parameters.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;

        value = prop.GetString();
        return value != null;
    }

    private static AgentUrgency ParseUrgency(string value) => value.ToLowerInvariant() switch
    {
        "low" => AgentUrgency.Low,
        "high" => AgentUrgency.High,
        _ => AgentUrgency.Normal,
    };

    public void AddMessage(AgentSpeaker speaker, string text)
    {
        var message = new AgentMessage(DateTime.Now, speaker, text);

        Append(_transcript, message);
        MessageAdded?.Invoke(message);
    }

    public void Notify(string text, AgentUrgency urgency = AgentUrgency.Normal)
    {
        var notification = new AgentNotification(DateTime.Now, text, urgency);

        Append(_notifications, notification);
        NotificationAdded?.Invoke(notification);
    }

    /// <summary>
    ///     Mode changes are a Phase 4 concern; the setter exists now so the status line has
    ///     something real to render and so nothing has to be rewired later.
    /// </summary>
    public void SetMode(AgentControlMode mode)
    {
        if (Mode == mode)
            return;

        Mode = mode;
        StatusChanged?.Invoke();
    }

    public void SetConnection(AgentConnectionState state)
    {
        if (Connection == state)
            return;

        Connection = state;
        StatusChanged?.Invoke();
    }

    private static void Append<T>(List<T> list, T item)
    {
        list.Add(item);

        if (list.Count > HistoryLimit)
            list.RemoveRange(0, list.Count - HistoryLimit);
    }
}

public enum AgentConnectionState : byte
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
///     Who is allowed to act. See PLAN.md §6.1. Phase 0 supports <see cref="Observe"/> only.
/// </summary>
public enum AgentControlMode : byte
{
    /// <summary>Agent watches and advises; the human performs every action. The default.</summary>
    Observe,

    /// <summary>Agent may act, but only on an explicit per-action request from the human.</summary>
    Assist,

    /// <summary>Agent acts on its own initiative until the human takes control back.</summary>
    Autopilot,
}

public enum AgentUrgency : byte
{
    Low,
    Normal,
    High,
}

public enum AgentSpeaker : byte
{
    Human,
    Agent,

    /// <summary>The bridge itself — connection state, errors, mode changes.</summary>
    System,
}

public readonly record struct AgentMessage(DateTime At, AgentSpeaker Speaker, string Text);

public readonly record struct AgentNotification(DateTime At, string Text, AgentUrgency Urgency);
