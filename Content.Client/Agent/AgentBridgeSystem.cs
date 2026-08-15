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
public sealed class AgentBridgeSystem : EntitySystem
{
    /// <summary>
    ///     How many conversation lines and notifications are kept for replay into a freshly
    ///     opened panel. Old entries are dropped from the front.
    /// </summary>
    private const int HistoryLimit = 200;

    private readonly List<AgentMessage> _transcript = new();
    private readonly List<AgentNotification> _notifications = new();

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

        Log.Debug("Agent bridge initialised (phase 0: no transport).");
    }

    public override void Shutdown()
    {
        base.Shutdown();

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

        // Phase 1 replaces this with a send over the bridge socket.
        if (Connection != AgentConnectionState.Connected)
            AddMessage(AgentSpeaker.System, Loc.GetString("agent-no-bridge"));
    }

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
