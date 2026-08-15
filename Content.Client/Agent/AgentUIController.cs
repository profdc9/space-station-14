using Content.Client.Agent.UI;
using Content.Client.Gameplay;
using Content.Shared.Input;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared.Input.Binding;

namespace Content.Client.Agent;

/// <summary>
///     Owns the copilot side panel and its toggle keybind.
/// </summary>
/// <remarks>
///     Auto-registered by reflection via <c>GetAllChildren&lt;UIController&gt;()</c>; the only
///     upstream edits this feature needs are the keybind declarations. See PLAN.md §3.
/// </remarks>
[UsedImplicitly]
public sealed class AgentUIController : UIController,
    IOnStateChanged<GameplayState>,
    IOnSystemChanged<AgentBridgeSystem>
{
    private AgentWindow? _window;
    private AgentBridgeSystem? _bridge;

    public void OnStateEntered(GameplayState state)
    {
        _window = UIManager.CreateWindow<AgentWindow>();
        _window.MessageEntered += OnMessageEntered;

        // The bridge may have loaded before we did, so seed from whatever it already holds
        // rather than assuming we saw every event.
        Replay();

        CommandBinds.Builder
            .Bind(ContentKeyFunctions.ToggleAgentPanel,
                InputCmdHandler.FromDelegate(_ => TogglePanel()))
            .Bind(ContentKeyFunctions.FocusAgentInput,
                InputCmdHandler.FromDelegate(_ => FocusInput()))
            .Register<AgentUIController>();
    }

    public void OnStateExited(GameplayState state)
    {
        CommandBinds.Unregister<AgentUIController>();

        if (_window == null)
            return;

        _window.MessageEntered -= OnMessageEntered;

        // Detach rather than Dispose: Control.Dispose is obsolete, and windows are meant to be
        // removed from the UI tree instead. Several upstream controllers still call Dispose here.
        _window.Close();
        _window.Orphan();
        _window = null;
    }

    public void OnSystemLoaded(AgentBridgeSystem system)
    {
        _bridge = system;

        system.MessageAdded += OnMessageAdded;
        system.NotificationAdded += OnNotificationAdded;
        system.StatusChanged += OnStatusChanged;

        Replay();
    }

    public void OnSystemUnloaded(AgentBridgeSystem system)
    {
        system.MessageAdded -= OnMessageAdded;
        system.NotificationAdded -= OnNotificationAdded;
        system.StatusChanged -= OnStatusChanged;

        _bridge = null;
    }

    public void TogglePanel()
    {
        if (_window == null)
            return;

        if (_window.IsOpen)
        {
            UIManager.ClickSound();
            _window.Close();
            return;
        }

        // Deliberately does NOT take keyboard focus. The panel is advisory and mostly read-only;
        // opening it must never silently disable movement. Use FocusAgentInput to type.
        _window.OpenCenteredRight();
    }

    /// <summary>
    ///     Opens the panel if needed and puts the caret in the input box.
    /// </summary>
    private void FocusInput()
    {
        if (_window == null)
            return;

        if (!_window.IsOpen)
            _window.OpenCenteredRight();

        _window.FocusInput();
    }

    private void OnMessageEntered(string text)
    {
        _bridge?.SendToAgent(text);
    }

    private void OnMessageAdded(AgentMessage message)
    {
        _window?.AddMessage(message);
    }

    private void OnNotificationAdded(AgentNotification notification)
    {
        _window?.AddNotification(notification);
    }

    private void OnStatusChanged()
    {
        if (_window == null || _bridge == null)
            return;

        _window.SetStatus(_bridge.Connection, _bridge.Mode);
    }

    /// <summary>
    ///     Rebuilds the panel contents from the bridge's history. Safe to call whenever either
    ///     half of the pair (window, bridge) appears, since the order is not guaranteed.
    /// </summary>
    private void Replay()
    {
        if (_window == null || _bridge == null)
            return;

        _window.ClearAll();

        foreach (var message in _bridge.Transcript)
        {
            _window.AddMessage(message);
        }

        foreach (var notification in _bridge.Notifications)
        {
            _window.AddNotification(notification);
        }

        _window.SetStatus(_bridge.Connection, _bridge.Mode);
    }
}
