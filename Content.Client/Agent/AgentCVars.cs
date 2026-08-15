using Robust.Shared.Configuration;

namespace Content.Client.Agent;

/// <summary>
///     CVars for the copilot bridge.
/// </summary>
/// <remarks>
///     <para>
///     Deliberately a separate <see cref="CVarDefsAttribute"/> class rather than an addition to
///     <c>CCVars</c> — upstream explicitly tells forks to do this (<c>Content.Shared/CCVar/CCVars.cs</c>),
///     and RobustToolbox picks it up by assembly scan (<c>GameController.cs:179</c>). Zero upstream edits.
///     </para>
///     <para>
///     Client assembly, not shared: <c>Content.Shared</c> is part of the client/server contract, and
///     changing it forces a matched rebuild of both sides. Nothing here concerns the server.
///     </para>
/// </remarks>
[CVarDefs]
public sealed class AgentCVars
{
    /// <summary>
    ///     Whether to listen for a copilot at all. Off means the game is stock.
    /// </summary>
    public static readonly CVarDef<bool> BridgeEnabled =
        CVarDef.Create("agent.bridge_enabled", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Loopback port the MCP server connects to. Never bound to a non-loopback address.
    /// </summary>
    public static readonly CVarDef<int> BridgePort =
        CVarDef.Create("agent.bridge_port", 21214, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Shared secret the MCP server must present as <c>?token=</c>. Empty disables the check.
    /// </summary>
    /// <remarks>
    ///     Empty is a reasonable default only while the bridge is read-only (phases 1–3): the worst
    ///     case is another local process reading what the player can already see. This must become
    ///     mandatory before Phase 4 grants the bridge the ability to act.
    /// </remarks>
    public static readonly CVarDef<string> BridgeToken =
        CVarDef.Create("agent.bridge_token", "", CVar.CLIENTONLY | CVar.ARCHIVE | CVar.CONFIDENTIAL);
}
