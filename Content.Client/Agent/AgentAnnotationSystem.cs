using System.Numerics;
using Content.Client.Agent.UI;
using Robust.Client.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Client.Agent;

public enum AgentMarkKind : byte
{
    /// <summary>An outline around a specific entity, which follows it as it moves.</summary>
    Highlight,

    /// <summary>A marker on a fixed tile.</summary>
    Tile,

    /// <summary>A breadcrumb line through a series of tiles.</summary>
    Path,
}

/// <summary>
///     One thing the copilot has drawn into the human's view.
/// </summary>
public sealed class AgentMark
{
    public required string Id { get; init; }
    public required AgentMarkKind Kind { get; init; }
    public required string Label { get; init; }
    public required Color Colour { get; init; }
    public required TimeSpan Expires { get; init; }

    /// <summary>Target for <see cref="AgentMarkKind.Highlight"/>.</summary>
    public NetEntity? Entity { get; init; }

    /// <summary>Grid the tiles belong to, for tile and path marks.</summary>
    public NetEntity? Grid { get; init; }

    /// <summary>Tiles for <see cref="AgentMarkKind.Tile"/> (one) and <see cref="AgentMarkKind.Path"/> (many).</summary>
    public Vector2i[] Tiles { get; init; } = [];
}

/// <summary>
///     Owns everything the copilot draws into the human's view.
/// </summary>
/// <remarks>
///     <para>
///     Entirely client-local. Nothing here is sent to the server, no other player sees it, and it
///     touches no gameplay state — it is a heads-up display over the human's own screen. That is
///     what makes annotation safe to give an advisory agent: being wrong is visual clutter, not a
///     game action.
///     </para>
///     <para>
///     Marks expire. An agent that highlights a crowbar and then forgets about it leaves litter on
///     the screen, so every mark carries a TTL and is dropped without being asked.
///     </para>
/// </remarks>
public sealed class AgentAnnotationSystem : EntitySystem
{
    /// <summary>Refuse to hold more than this. Runaway marking should degrade, not blind the human.</summary>
    private const int MaxMarks = 64;

    private const float DefaultTtlSeconds = 30f;
    private const float MaxTtlSeconds = 600f;

    [Dependency] private IOverlayManager _overlay = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ISharedPlayerManager _player = default!;

    private readonly List<AgentMark> _marks = new();

    private AgentAnnotationOverlay? _overlayInstance;
    private int _nextId;

    public IReadOnlyList<AgentMark> Marks => _marks;

    public override void Initialize()
    {
        base.Initialize();

        _overlayInstance = new AgentAnnotationOverlay(this, EntityManager);
        _overlay.AddOverlay(_overlayInstance);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        if (_overlayInstance != null)
            _overlay.RemoveOverlay(_overlayInstance);

        _overlayInstance = null;
        _marks.Clear();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        _marks.RemoveAll(m => m.Expires <= now);
    }

    /// <summary>
    ///     Grid the human is currently standing on, which is the space tile coordinates are given in.
    /// </summary>
    public NetEntity? CurrentGrid()
    {
        if (_player.LocalEntity is not { } player || !Exists(player))
            return null;

        var grid = Transform(player).GridUid;
        return grid is { } g ? GetNetEntity(g) : null;
    }

    public AgentMark Add(
        AgentMarkKind kind,
        string label,
        Color colour,
        float? ttlSeconds,
        NetEntity? entity = null,
        NetEntity? grid = null,
        Vector2i[]? tiles = null)
    {
        var ttl = Math.Clamp(ttlSeconds ?? DefaultTtlSeconds, 1f, MaxTtlSeconds);

        var mark = new AgentMark
        {
            Id = $"m:{++_nextId}",
            Kind = kind,
            Label = label,
            Colour = colour,
            Expires = _timing.CurTime + TimeSpan.FromSeconds(ttl),
            Entity = entity,
            Grid = grid,
            Tiles = tiles ?? [],
        };

        _marks.Add(mark);

        // Oldest first, so a burst of new marks pushes out stale ones rather than being refused.
        if (_marks.Count > MaxMarks)
            _marks.RemoveRange(0, _marks.Count - MaxMarks);

        return mark;
    }

    /// <summary>Removes one mark by id, or all of them. Returns how many went.</summary>
    public int Clear(string? id = null)
    {
        if (id == null)
        {
            var n = _marks.Count;
            _marks.Clear();
            return n;
        }

        return _marks.RemoveAll(m => m.Id == id);
    }

    /// <summary>
    ///     Colour by name ("red", "cyan") or hex ("#ff8800"). Falls back rather than failing — a
    ///     mark in the wrong colour is better than no mark.
    /// </summary>
    public static Color ParseColour(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        if (Color.TryFromName(value, out var named))
            return named;

        if (Color.TryFromHex(value, out var hex))
            return hex;

        return fallback;
    }

    /// <summary>
    ///     World position of a tile centre on a grid, if that grid still exists.
    /// </summary>
    public bool TryTileCentre(NetEntity gridRef, Vector2i tile, out Vector2 world)
    {
        world = default;

        if (!TryGetEntity(gridRef, out var gridUid)
            || !TryComp<MapGridComponent>(gridUid, out var grid))
        {
            return false;
        }

        var local = new Vector2(tile.X + 0.5f, tile.Y + 0.5f) * grid.TileSize;
        var xform = EntityManager.System<SharedTransformSystem>();

        world = Vector2.Transform(local, xform.GetWorldMatrix(gridUid.Value));
        return true;
    }
}
