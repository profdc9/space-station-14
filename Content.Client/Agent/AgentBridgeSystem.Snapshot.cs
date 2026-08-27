using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using Content.Shared.Alert;
using Content.Shared.Chat;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.IdentityManagement;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Client.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.Client.Agent;

/// <summary>
///     Builds the world observation the copilot reads.
/// </summary>
/// <remarks>
///     <para>
///     <b>Honest information is the point.</b> The copilot is meant to see what the human sees and
///     no more. Two places where that is not automatic:
///     </para>
///     <list type="bullet">
///     <item><description>
///     Entities are filtered through <see cref="ExamineSystemShared.CanExamine"/>, the same
///     line-of-sight check the game uses for its own context menu.
///     </description></item>
///     <item><description>
///     Contained entities are excluded outright. PVS applies no container culling — the client
///     legitimately holds the contents of closed lockers and other players' bags — so mirroring
///     what the client knows would be a cheat. See PLAN.md §4.1.
///     </description></item>
///     </list>
/// </remarks>
public sealed partial class AgentBridgeSystem
{
    /// <summary>Largest observation radius in tiles. Beyond this the payload stops being readable.</summary>
    private const int MaxRadius = 12;
    private const int DefaultRadius = 7;

    private readonly HashSet<string> _knownAlerts = new();
    private readonly HashSet<string> _seenAlerts = new();

    /// <summary>Seconds between alert polls. Alerts are not eventful enough to check every tick.</summary>
    private const float AlertPollInterval = 0.5f;

    private float _alertPollAccumulator;

    [Dependency] private ISharedPlayerManager _player = default!;
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ExamineSystemShared _examine = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private InventorySystem _inventory = default!;

    /// <summary>
    ///     Assembles everything the copilot can legitimately observe right now.
    ///     Returns null when there is no player entity — lobby, ghost transition, round restart.
    /// </summary>
    private object? BuildSnapshot(int radius)
    {
        radius = Math.Clamp(radius, 1, MaxRadius);

        if (_player.LocalEntity is not { } player || !Exists(player))
            return null;

        var coords = Transform(player).Coordinates;
        var map = _xform.GetMapCoordinates(player);

        return new
        {
            cursor = _cursor,
            self = BuildSelf(player, map),
            hands = BuildHands(player),
            worn = BuildWorn(player),
            entities = BuildEntities(player, coords, radius),
            tiles = BuildTiles(player, radius),
        };
    }

    private object BuildSelf(EntityUid player, MapCoordinates map)
    {
        // "unknown" rather than a guess when there is no MobState: the human may be attached to a
        // brain, a ghost, or some other entity that is not a mob at all. Saying "alive" there would
        // be a confident lie, and the copilot needs to be able to tell the human it cannot read the
        // situation.
        var state = "unknown";

        if (HasComp<MobStateComponent>(player))
        {
            if (_mobState.IsDead(player))
                state = "dead";
            else if (_mobState.IsCritical(player))
                state = "critical";
            else if (_mobState.IsAlive(player))
                state = "alive";
        }

        // Capabilities, so the copilot can say "you cannot pick that up, you have no hands" instead
        // of advising actions the current body cannot perform.
        var canAct = HasComp<Content.Shared.Hands.Components.HandsComponent>(player);

        var alerts = TryComp<AlertsComponent>(player, out var alertComp)
            ? alertComp.Alerts.Keys.Select(k => k.AlertType.ToString()).ToArray()
            : [];

        var xform = Transform(player);

        return new
        {
            entity = Ref(player),
            name = Identity.Name(player, EntityManager),
            state,
            canAct,
            alerts,
            map = map.MapId.ToString(),
            grid = xform.GridUid is { } g ? Ref(g) : null,

            // Grid tile indices. This is the coordinate space everything else in the snapshot
            // uses — entity tiles and the atmos grid.
            tile = TileOf(player) is { } t ? new[] { t.X, t.Y } : null,

            // Map space, NOT grid space. Named explicitly because the two diverge arbitrarily
            // once the grid moves or rotates, and reading them as the same space produces
            // confident nonsense.
            mapPosition = new[] { MathF.Round(map.X, 2), MathF.Round(map.Y, 2) },
        };
    }

    private object[] BuildHands(EntityUid player)
    {
        if (!HasComp<Content.Shared.Hands.Components.HandsComponent>(player))
            return [];

        var active = _hands.GetActiveItem(player);
        var result = new List<object>();

        foreach (var hand in _hands.EnumerateHands(player))
        {
            var held = _hands.GetHeldItem(player, hand);

            result.Add(new
            {
                hand,
                active = held != null && held == active,
                item = held is { } h ? Ref(h) : null,
                name = held is { } h2 ? Identity.Name(h2, EntityManager) : null,
            });
        }

        return result.ToArray();
    }

    /// <summary>
    ///     What the human is wearing, by slot.
    /// </summary>
    /// <remarks>
    ///     Own equipped items only. This is the one container case the honesty rule permits: it is
    ///     the human's own kit, which they can see on their character sheet at any time. Other
    ///     people's pockets and the contents of closed lockers stay excluded, per PLAN.md 4.1.
    ///
    ///     Worth having because "do I have the thing I need" - a headset, internals, a crowbar - is
    ///     exactly the sort of question a copilot should answer without the human digging through
    ///     their own inventory UI.
    /// </remarks>
    private object[] BuildWorn(EntityUid player)
    {
        if (!HasComp<InventoryComponent>(player))
            return [];

        var result = new List<object>();
        var slots = _inventory.GetSlotEnumerator(player);

        while (slots.NextItem(out var item, out var slot))
        {
            if (!Exists(item))
                continue;

            result.Add(new
            {
                slot = slot.Name,
                id = Ref(item),
                name = Identity.Name(item, EntityManager),
            });
        }

        return result.ToArray();
    }

    private object[] BuildEntities(EntityUid player, EntityCoordinates coords, int radius)
    {
        var found = _lookup.GetEntitiesInRange(coords, radius);
        var result = new List<(string name, string id, Vector2i? tile)>();

        foreach (var ent in found)
        {
            if (ent == player || !Exists(ent))
                continue;

            // See the remarks on this class: container contents are not ours to report.
            if (_container.IsEntityInContainer(ent))
                continue;

            if (!_examine.CanExamine(player, ent))
                continue;

            // Must actually be drawn. Without this the list fills with things the human cannot
            // see — latejoin spawn points, markers, and other invisible entities — which is the
            // same dishonesty as reporting container contents, and wastes the token budget too.
            if (!TryComp<SpriteComponent>(ent, out var sprite) || !sprite.Visible)
                continue;

            var name = Identity.Name(ent, EntityManager);

            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add((name, Ref(ent), TileOf(ent)));
        }

        // Grouped by name: nine hydroponics trays become one entry with nine tiles rather than
        // nine near-identical objects. This also collapses the wall spam, which was about a
        // third of the list.
        //
        // "tiles" and "ids" are index-aligned: ids[i] is the entity at tiles[i]. The ids are what
        // examine/verbs/highlight take as a target, and are the only way to name one particular
        // tray out of nine.
        return result
            .GroupBy(e => e.name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(object (g) => new
            {
                name = g.Key,
                count = g.Count(),
                tiles = g.Select(e => e.tile is { } t ? new[] { t.X, t.Y } : null).ToArray(),
                ids = g.Select(e => e.id).ToArray(),
            })
            .ToArray();
    }

    /// <summary>
    ///     Renders the local atmosphere as a character grid.
    /// </summary>
    /// <remarks>
    ///     Every tile the client holds carries a <see cref="ThermalByte"/>, which is a tagged union:
    ///     a temperature, or a vacuum sentinel, or an "atmosphere impossible" sentinel that in
    ///     practice marks a wall. That gives breach detection and a structural map for free, with no
    ///     instrument and no admin-gated debug overlay. See docs/client-findings.md D1.
    /// </remarks>
    private object? BuildTiles(EntityUid player, int radius)
    {
        var xform = Transform(player);

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        if (!TryComp<GasTileOverlayComponent>(gridUid, out var overlay))
            return null;

        var centre = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var size = radius * 2 + 1;

        // '?' rather than '.' as the default: no data means we have not been told, which is not the
        // same as "normal". The copilot must be able to tell those apart.
        var cells = new char[size, size];
        var notable = new List<object>();

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            cells[y, x] = '?';

        foreach (var chunk in overlay.Chunks.Values)
        {
            var enumerator = new GasChunkEnumerator(chunk);

            while (enumerator.MoveNext(out var gas))
            {
                var tile = chunk.Origin + new Vector2i(enumerator.X, enumerator.Y);

                var dx = tile.X - centre.X + radius;
                var dy = tile.Y - centre.Y + radius;

                if (dx < 0 || dy < 0 || dx >= size || dy >= size)
                    continue;

                var temp = gas.ByteGasTemperature;
                char c;

                if (temp.IsAtmosImpossible)
                {
                    c = '#';
                }
                else if (temp.IsVacuum)
                {
                    c = 'V';
                    notable.Add(new { tile = new[] { tile.X, tile.Y }, kind = "vacuum" });
                }
                else if (gas.FireState > 0)
                {
                    c = 'F';
                    notable.Add(new { tile = new[] { tile.X, tile.Y }, kind = "fire", state = (int) gas.FireState });
                }
                else if (temp.TryGetTemperature(out var kelvin))
                {
                    if (kelvin > 350f)
                    {
                        c = 'H';
                        notable.Add(new { tile = new[] { tile.X, tile.Y }, kind = "hot", kelvin = MathF.Round(kelvin) });
                    }
                    else if (kelvin < 260f)
                    {
                        c = 'C';
                        notable.Add(new { tile = new[] { tile.X, tile.Y }, kind = "cold", kelvin = MathF.Round(kelvin) });
                    }
                    else
                    {
                        c = '.';
                    }
                }
                else
                {
                    c = '?';
                }

                cells[dy, dx] = c;
            }
        }

        // Rows north-first, so reading order matches the screen rather than grid coordinates.
        var rows = new string[size];

        for (var y = 0; y < size; y++)
        {
            var row = new char[size];

            for (var x = 0; x < size; x++)
            {
                row[x] = cells[size - 1 - y, x];
            }

            rows[y] = new string(row);
        }

        return new
        {
            centre = new[] { centre.X, centre.Y },
            radius,
            legend = new Dictionary<string, string>
            {
                ["."] = "breathable",
                ["#"] = "wall or otherwise cannot hold atmosphere",
                ["V"] = "vacuum",
                ["F"] = "fire",
                ["H"] = "hot, over 350K",
                ["C"] = "cold, under 260K",
                ["?"] = "no data received for this tile",
                ["@"] = "you",
            },
            rows = MarkSelf(rows, radius),
            notable = notable.ToArray(),
        };
    }

    /// <summary>Overwrites the centre cell with '@' so the copilot can locate the player in the grid.</summary>
    private static string[] MarkSelf(string[] rows, int radius)
    {
        var row = rows[radius].ToCharArray();
        row[radius] = '@';
        rows[radius] = new string(row);

        return rows;
    }

    /// <summary>
    ///     The game's own examine text for an entity, as plain text.
    /// </summary>
    /// <remarks>
    ///     Uses the client-side text rather than asking the server. Upstream's own note on this
    ///     (<c>Content.Client/Examine/ExamineSystem.cs:420</c>) is that the client version "will
    ///     usually be correct (barring server-only components, which generally only adds, not
    ///     replaces text)" — so this can be missing lines, but should not be *wrong*. Fetching the
    ///     server's version needs an async request/response pair with careful id selection, since
    ///     an id colliding with <c>ExamineSystem._idCounter</c> would pop a tooltip in the human's
    ///     face. Worth doing if this proves too thin in practice.
    ///
    ///     Deliberately does NOT call <c>DoExamine</c>, which opens that tooltip.
    /// </remarks>
    private object? BuildExamine(string reference)
    {
        if (_player.LocalEntity is not { } player || !Exists(player))
            return null;

        if (!TryResolve(reference, out var target))
            return null;

        // Same visibility rule as the snapshot: if the human could not examine it, neither can
        // the copilot.
        if (!_examine.CanExamine(player, target.Value))
            return null;

        var text = _examine.GetExamineText(target.Value, player);

        return new
        {
            entity = reference,
            name = Identity.Name(target.Value, EntityManager, player),
            tile = TileOf(target.Value) is { } t ? new[] { t.X, t.Y } : null,
            text = text.ToString(),
            clientSideOnly = true,
        };
    }

    /// <summary>
    ///     Turns an <c>e:&lt;netid&gt;</c> reference back into a live local entity.
    /// </summary>
    private bool TryResolve(string reference, [NotNullWhen(true)] out EntityUid? entity)
    {
        entity = null;

        if (!reference.StartsWith("e:", StringComparison.Ordinal))
            return false;

        if (!int.TryParse(reference.AsSpan(2), out var raw))
            return false;

        if (!TryGetEntity(new NetEntity(raw), out var uid) || !Exists(uid))
            return false;

        entity = uid;
        return true;
    }

    /// <summary>
    ///     Channels the copilot is allowed to hear.
    /// </summary>
    /// <remarks>
    ///     The copilot co-plays one character, so it hears what that character hears and nothing
    ///     else. Deliberately excluded: OOC and LOOC because they are out-of-character, admin chat
    ///     and dead chat because they are privileged. Hearing those would make the agent a better
    ///     informant than the human it is sitting next to, which is the whole thing this design
    ///     avoids.
    /// </remarks>
    private static readonly ChatChannel AudibleChannels =
        ChatChannel.Local
        | ChatChannel.Whisper
        | ChatChannel.Radio
        | ChatChannel.Server
        | ChatChannel.Emotes
        | ChatChannel.Notifications;

    /// <summary>
    ///     Radio messages carry their channel as escaped brackets at the head of the wrapped
    ///     message — <c>\[Engineering\]</c>. See docs/client-findings.md D2.
    /// </summary>
    private static readonly Regex RadioChannel = new(@"\\\[(.+?)\\\]", RegexOptions.Compiled);

    /// <summary>
    ///     Records an in-game chat message for the copilot. Called from
    ///     <see cref="AgentUIController"/>, which owns the subscription to the chat UI.
    /// </summary>
    public void RecordChat(ChatMessage message)
    {
        if ((message.Channel & AudibleChannels) == 0)
            return;

        if (message.HideChat || string.IsNullOrWhiteSpace(message.Message))
            return;

        var channel = message.Channel.ToString();

        // Radio carries a department; without it "Radio" alone is nearly useless to the copilot.
        if (message.Channel == ChatChannel.Radio)
        {
            var match = RadioChannel.Match(message.WrappedMessage);

            if (match.Success)
                channel = $"Radio:{match.Groups[1].Value}";
        }

        string? speaker = null;

        if (TryGetEntity(message.SenderEntity, out var sender) && Exists(sender))
            speaker = Identity.Name(sender.Value, EntityManager);

        RaiseBridgeEvent("chat", new
        {
            channel,
            speaker,
            text = message.Message,
        });
    }

    /// <summary>
    ///     Emits <c>alert</c> events when the player's alert set changes — low pressure, cold,
    ///     hunger, crit. This is the safety-critical half of what <c>since</c> carries.
    /// </summary>
    private void PollAlerts()
    {
        if (_player.LocalEntity is not { } player
            || !Exists(player)
            || !TryComp<AlertsComponent>(player, out var alerts))
        {
            _knownAlerts.Clear();
            return;
        }

        _seenAlerts.Clear();

        foreach (var key in alerts.Alerts.Keys)
        {
            if (key.AlertType is not { } type)
                continue;

            var name = type.Id;
            _seenAlerts.Add(name);

            if (_knownAlerts.Add(name))
                RaiseBridgeEvent("alert", new { alert = name, state = "entered" });
        }

        _knownAlerts.RemoveWhere(known =>
        {
            if (_seenAlerts.Contains(known))
                return false;

            RaiseBridgeEvent("alert", new { alert = known, state = "cleared" });
            return true;
        });
    }

    private Vector2i? TileOf(EntityUid uid)
    {
        var xform = Transform(uid);

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        return _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
    }

    /// <summary>
    ///     Opaque, reconnect-stable entity reference. Never a raw <see cref="EntityUid"/> — those are
    ///     local to one client session.
    /// </summary>
    private string Ref(EntityUid uid)
    {
        return $"e:{GetNetEntity(uid)}";
    }
}
