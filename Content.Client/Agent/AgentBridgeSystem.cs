using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    [Dependency] private AgentAnnotationSystem _annotations = default!;

    private readonly List<AgentMessage> _transcript = new();
    private readonly List<AgentNotification> _notifications = new();

    /// <summary>How many past events stay replayable through <c>since</c>.</summary>
    private const int EventLogLimit = 500;

    private readonly List<(ulong cursor, string name, object payload)> _eventLog = new();

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

    /// <summary>
    ///     Pumps the bridge.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>FrameUpdate, not Update.</b> Update only runs during predicted simulation ticks, so
    ///     whenever prediction is not ticking - between rounds, while dead, after a server restart -
    ///     the bridge accepted connections and then silently ignored every frame. No error, no log.
    ///     </para>
    ///     <para>
    ///     Setting <c>UpdatesOutsidePrediction</c> looks like the fix and is not: it makes the
    ///     system tick inside the state-application path, where reading entity state trips
    ///     <c>ClientGameStateManager.ResetPredictedEntities</c>'s debug assert and takes the client
    ///     down. FrameUpdate runs once per rendered frame, outside the simulation entirely, which
    ///     is where read-only transport work belongs.
    ///     </para>
    /// </remarks>
    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_server == null)
            return;

        // The socket runs on its own threads and only ever moves strings. Everything that touches
        // game state happens here, on the game thread.
        SetConnection(_server.State);

        _alertPollAccumulator += frameTime;

        if (_alertPollAccumulator >= AlertPollInterval)
        {
            _alertPollAccumulator = 0f;
            PollAlerts();
        }

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
    ///     Records an event and pushes it to the copilot, stamped with the next cursor value.
    /// </summary>
    /// <remarks>
    ///     Recorded whether or not anything is connected. The copilot may attach mid-round and
    ///     immediately ask what it missed, and "nothing happened before you arrived" would be a lie.
    /// </remarks>
    private void RaiseBridgeEvent(string name, object payload)
    {
        var cursor = ++_cursor;

        _eventLog.Add((cursor, name, payload));

        if (_eventLog.Count > EventLogLimit)
            _eventLog.RemoveRange(0, _eventLog.Count - EventLogLimit);

        _server?.Send(BridgeProtocol.Event(name, cursor, payload));
    }

    /// <summary>
    ///     Everything recorded after <paramref name="after"/>, oldest first.
    /// </summary>
    private object BuildSince(ulong after)
    {
        var events = new JsonArray();
        var dropped = false;

        // If the caller's cursor predates the oldest thing we still hold, say so rather than
        // silently returning a partial history that looks complete.
        if (_eventLog.Count > 0 && after < _eventLog[0].cursor - 1)
            dropped = true;

        foreach (var (cursor, name, payload) in _eventLog)
        {
            if (cursor > after)
                events.Add(BridgeProtocol.EventNode(name, cursor, payload));
        }

        return new
        {
            cursor = _cursor,
            truncated = dropped,
            events,
        };
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

            case "since":
                var after = parameters.ValueKind == JsonValueKind.Object
                            && parameters.TryGetProperty("cursor", out var cur)
                            && cur.TryGetUInt64(out var parsedCursor)
                    ? parsedCursor
                    : 0UL;

                _server?.Send(BridgeProtocol.Ok(id, BuildSince(after)));
                break;

            case "examine":
                if (!TryGetString(parameters, "entity", out var target))
                {
                    _server?.Send(BridgeProtocol.Error(id, "bad_params", "entity is required"));
                    break;
                }

                var examined = BuildExamine(target);

                if (examined == null)
                {
                    _server?.Send(BridgeProtocol.Error(id, "not_found",
                        $"{target} is unknown, gone, or not visible from here"));
                    break;
                }

                _server?.Send(BridgeProtocol.Ok(id, examined));
                break;

            case "annotate":
                HandleAnnotate(id, parameters);
                break;

            case "clear_marks":
                var cleared = _annotations.Clear(TryGetString(parameters, "id", out var markId) ? markId : null);
                _server?.Send(BridgeProtocol.Ok(id, new { cleared }));
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

    /// <summary>
    ///     Draws something into the human's view. Client-local only — see <see cref="AgentAnnotationSystem"/>.
    /// </summary>
    private void HandleAnnotate(int id, JsonElement parameters)
    {
        if (!TryGetString(parameters, "kind", out var kind))
        {
            _server?.Send(BridgeProtocol.Error(id, "bad_params", "kind is required"));
            return;
        }

        var label = TryGetString(parameters, "label", out var l) ? l : string.Empty;
        var ttl = parameters.TryGetProperty("ttl", out var t) && t.TryGetSingle(out var parsedTtl)
            ? parsedTtl
            : (float?) null;

        var colourText = TryGetString(parameters, "colour", out var c) ? c
            : TryGetString(parameters, "color", out var c2) ? c2
            : null;

        switch (kind)
        {
            case "highlight":
            {
                if (!TryGetString(parameters, "entity", out var reference) || !TryResolve(reference, out var uid))
                {
                    _server?.Send(BridgeProtocol.Error(id, "not_found", "entity is unknown or gone"));
                    return;
                }

                var mark = _annotations.Add(
                    AgentMarkKind.Highlight,
                    label,
                    AgentAnnotationSystem.ParseColour(colourText, Color.Yellow),
                    ttl,
                    entity: GetNetEntity(uid.Value));

                _server?.Send(BridgeProtocol.Ok(id, new { mark = mark.Id }));
                return;
            }

            case "mark":
            {
                if (!TryReadTile(parameters, "tile", out var tile))
                {
                    _server?.Send(BridgeProtocol.Error(id, "bad_params", "tile must be [x, y]"));
                    return;
                }

                if (_annotations.CurrentGrid() is not { } grid)
                {
                    _server?.Send(BridgeProtocol.Error(id, "no_grid", "the human is not standing on a grid"));
                    return;
                }

                var mark = _annotations.Add(
                    AgentMarkKind.Tile,
                    label,
                    AgentAnnotationSystem.ParseColour(colourText, Color.Cyan),
                    ttl,
                    grid: grid,
                    tiles: [tile]);

                _server?.Send(BridgeProtocol.Ok(id, new { mark = mark.Id }));
                return;
            }

            case "path":
            {
                if (!parameters.TryGetProperty("tiles", out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    _server?.Send(BridgeProtocol.Error(id, "bad_params", "tiles must be an array of [x, y]"));
                    return;
                }

                var tiles = new List<Vector2i>();

                foreach (var element in arr.EnumerateArray())
                {
                    if (TryReadTilePair(element, out var tile))
                        tiles.Add(tile);
                }

                if (tiles.Count < 2)
                {
                    _server?.Send(BridgeProtocol.Error(id, "bad_params", "a path needs at least two tiles"));
                    return;
                }

                if (_annotations.CurrentGrid() is not { } grid)
                {
                    _server?.Send(BridgeProtocol.Error(id, "no_grid", "the human is not standing on a grid"));
                    return;
                }

                var mark = _annotations.Add(
                    AgentMarkKind.Path,
                    label,
                    AgentAnnotationSystem.ParseColour(colourText, Color.LimeGreen),
                    ttl,
                    grid: grid,
                    tiles: tiles.ToArray());

                _server?.Send(BridgeProtocol.Ok(id, new { mark = mark.Id }));
                return;
            }

            default:
                _server?.Send(BridgeProtocol.Error(id, "bad_params", $"unknown annotation kind '{kind}'"));
                return;
        }
    }

    private static bool TryReadTile(JsonElement parameters, string name, out Vector2i tile)
    {
        tile = default;

        return parameters.ValueKind == JsonValueKind.Object
               && parameters.TryGetProperty(name, out var element)
               && TryReadTilePair(element, out tile);
    }

    private static bool TryReadTilePair(JsonElement element, out Vector2i tile)
    {
        tile = default;

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2)
            return false;

        var values = element.EnumerateArray();

        if (!values.MoveNext() || !values.Current.TryGetInt32(out var x))
            return false;

        if (!values.MoveNext() || !values.Current.TryGetInt32(out var y))
            return false;

        tile = new Vector2i(x, y);
        return true;
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
