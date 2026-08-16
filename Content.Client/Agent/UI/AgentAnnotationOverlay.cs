using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;

namespace Content.Client.Agent.UI;

/// <summary>
///     Draws the copilot's marks over the human's view.
/// </summary>
/// <remarks>
///     World space for the shapes so they sit on the station and move with the camera, screen space
///     for the labels so text stays upright and legible at any zoom.
///
///     Everything here is drawn from local state only. No draw call implies anything about the
///     server, and nothing another player can see.
/// </remarks>
public sealed class AgentAnnotationOverlay : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpace | OverlaySpace.ScreenSpace;

    private readonly AgentAnnotationSystem _marks;
    private readonly IEntityManager _entities;
    private readonly IEyeManager _eye;
    private readonly EntityLookupSystem _lookup;
    private readonly Font _font;

    public AgentAnnotationOverlay(AgentAnnotationSystem marks, IEntityManager entities)
    {
        _marks = marks;
        _entities = entities;
        _eye = IoCManager.Resolve<IEyeManager>();
        _lookup = entities.System<EntityLookupSystem>();

        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_marks.Marks.Count == 0)
            return;

        switch (args.Space)
        {
            case OverlaySpace.WorldSpace:
                DrawWorld(args);
                break;
            case OverlaySpace.ScreenSpace:
                DrawLabels(args);
                break;
        }
    }

    private void DrawWorld(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;

        foreach (var mark in _marks.Marks)
        {
            switch (mark.Kind)
            {
                case AgentMarkKind.Highlight:
                    if (TryEntityBox(mark, out var box))
                    {
                        // Outline rather than fill: the human still has to see the thing itself.
                        handle.DrawRect(box.Enlarged(0.05f), mark.Colour, false);
                        handle.DrawRect(box.Enlarged(0.08f), mark.Colour.WithAlpha(0.4f), false);
                    }
                    break;

                case AgentMarkKind.Tile:
                    if (mark.Grid is { } tileGrid
                        && mark.Tiles.Length > 0
                        && _marks.TryTileCentre(tileGrid, mark.Tiles[0], out var centre))
                    {
                        var half = new Vector2(0.45f, 0.45f);
                        handle.DrawRect(new Box2(centre - half, centre + half), mark.Colour, false);
                        handle.DrawRect(new Box2(centre - half, centre + half), mark.Colour.WithAlpha(0.15f));
                    }
                    break;

                case AgentMarkKind.Path:
                    DrawPath(handle, mark);
                    break;
            }
        }
    }

    private void DrawPath(DrawingHandleWorld handle, AgentMark mark)
    {
        if (mark.Grid is not { } grid || mark.Tiles.Length < 2)
            return;

        Vector2? previous = null;

        foreach (var tile in mark.Tiles)
        {
            if (!_marks.TryTileCentre(grid, tile, out var point))
            {
                previous = null;
                continue;
            }

            if (previous is { } from)
                handle.DrawLine(from, point, mark.Colour);

            handle.DrawCircle(point, 0.08f, mark.Colour);
            previous = point;
        }

        // Ring the destination so it reads as a target rather than another waypoint.
        if (previous is { } last)
            handle.DrawCircle(last, 0.3f, mark.Colour, false);
    }

    private void DrawLabels(in OverlayDrawArgs args)
    {
        var handle = args.ScreenHandle;
        var scale = 1f;

        foreach (var mark in _marks.Marks)
        {
            if (string.IsNullOrWhiteSpace(mark.Label))
                continue;

            Vector2 world;

            switch (mark.Kind)
            {
                case AgentMarkKind.Highlight:
                    if (!TryEntityBox(mark, out var box))
                        continue;
                    world = new Vector2(box.Center.X, box.Top);
                    break;

                case AgentMarkKind.Tile:
                case AgentMarkKind.Path:
                    if (mark.Grid is not { } grid || mark.Tiles.Length == 0)
                        continue;
                    // Path labels go at the destination, which is where the human is being sent.
                    var anchor = mark.Kind == AgentMarkKind.Path ? mark.Tiles[^1] : mark.Tiles[0];
                    if (!_marks.TryTileCentre(grid, anchor, out world))
                        continue;
                    world += new Vector2(0f, 0.5f);
                    break;

                default:
                    continue;
            }

            if (!args.WorldAABB.Contains(world))
                continue;

            var screen = _eye.WorldToScreen(world).Rounded();
            handle.DrawString(_font, screen + new Vector2(6f, -14f), mark.Label, scale, mark.Colour);
        }
    }

    private bool TryEntityBox(AgentMark mark, out Box2 box)
    {
        box = default;

        if (mark.Entity is not { } netEntity)
            return false;

        if (!_entities.TryGetEntity(netEntity, out var uid) || !_entities.EntityExists(uid))
            return false;

        box = _lookup.GetWorldAABB(uid.Value);
        return true;
    }
}
