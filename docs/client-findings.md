# Client inspection findings

Source of truth for PLAN.md §4.2 and §10. All citations are against the pinned tree:

```
space-station-14  a86b1312e593f00743ce996fd1fd0a493262fd83  (2026-08-01)
RobustToolbox     724345afdffcdedebc43577654385a9ecfe3a092  (2026-08-01, v286.0.0)
```

Paths are relative to `client/`. Line numbers are from the pinned commits and will drift.

---

## Q1 — Atmos visibility (was BLOCKING)

**Answer: the client has approximate temperature and *some* gas presence, but no pressure and
no composition, without an instrument.** This is worse than PLAN.md §4.1 assumed and forces a
design change (see §Design impact).

### What every client receives for every tile in PVS

`Content.Shared/Atmos/EntitySystems/SharedGasTileOverlaySystem.cs:67-86` — `GasOverlayData`:

| Field | Meaning |
|---|---|
| `byte FireState` | whether the tile is on fire, and roughly how badly |
| `byte[] Opacity` | per-gas visual density — **only for gases that have a sprite** |
| `ThermalByte ByteGasTemperature` | temperature compressed to one byte |

The temperature field carries an explicit accuracy warning in its own doc comment
(`:73-76`): *"Values are approximate and may deviate even ~10°C from the precise server side
only temperature."*

Chunked and delivered via `GasOverlayChunk` (`Content.Shared/Atmos/GasOverlayChunk.cs:11-28`).

### Only five gases are visible at all

Gases are invisible unless they define `gasOverlaySprite`
(`GasPrototype.cs:82-86`: *"Sprite to show in the gas overlay if this gas is present on a
tile. If null the gas will be invisible."*).

From `Resources/Prototypes/Atmospherics/gases.yml`:

- **Visible:** Plasma (`:42`), Tritium (`:57`), WaterVapor (`:72`), Ammonia (`:86`),
  Frezon (`:113`)
- **Invisible — zero client data:** Oxygen (`:2`), Nitrogen (`:14`), CarbonDioxide (`:25`),
  NitrousOxide (`:96`)

So the client can see the *dangerous* clouds and is blind to breathable-atmosphere
composition. It cannot tell breathable air from pure nitrogen.

### Pressure is not networked per tile at all

There is no pressure field in `GasOverlayData`. Pressure reaches a normal player only through:

1. **Body alerts** — `Resources/Prototypes/Alerts/alerts.yml`: `LowPressure` (`:67`),
   `HighPressure` (`:79`), `Cold` (`:99`), `Hot` (`:113`), plus the `Breathing` category
   (`:20-23`, `categories.yml:2-8`). Binary-ish state on the player, not a number.
2. **Gas analyser** — full mixture for one target.
3. **Air alarm / atmos monitoring console** — room sensor data.

### Instruments give the real numbers

`Content.Shared/Atmos/Components/GasAnalyzerComponent.cs:36-71` — `GasAnalyzerUserMessage`
carries `GasMixEntry[]`, and each entry has `Name`, `Volume`, `Pressure`, `Temperature`, and
`GasEntry[] Gases` (gas + moles). Delivered as a `BoundUserInterfaceMessage`, so it requires
holding an analyser and using it on a target.

Air alarms and the atmos monitoring console expose room data via
`Content.Shared/Atmos/Monitor/AtmosSensorData.cs` and
`Content.Client/Atmos/Monitor/UI/AirAlarmBoundUserInterface.cs`.

### The full data exists but is admin-gated — off-limits

`Content.Shared/Atmos/EntitySystems/SharedAtmosDebugOverlaySystem.cs:13-24` defines
`AtmosDebugOverlayData` with `Temperature`, `float[]? Moles`, `PressureDirection`,
`IsSpace`, and more — everything we'd want.

It is gated: `Content.Server/Atmos/Commands/ShowAtmosCommand.cs:8` is
`[AdminCommand(AdminFlags.Debug)]`. The client system that consumes it
(`Content.Client/Atmos/EntitySystems/AtmosDebugOverlaySystem.cs:36-38`) only ever receives
data if an admin enabled it server-side.

**We will not use this path.** It is the exact privileged-information problem the
modified-client architecture was chosen to avoid.

### Design impact

PLAN.md §4.1 listed "pressure, temperature, gas composition on the player's tile and the four
adjacent tiles" as first-class observation. That is not honestly deliverable. Replaced by a
tiered model — see PLAN.md §4.1 as revised.

This is arguably a better design: the agent must reason about *instrumentation*, and can tell
the human "I can't see pressure from here — you'd need an analyser," which is exactly the kind
of advisory behaviour the copilot is for.

---

## Q2 — Verb cost

**Answer: `GetVerbs` is asynchronous and split. The synchronous return value is incomplete.**

`Content.Client/Verbs/VerbSystem.cs:176-189`:

- `:179` raises `RequestServerVerbsEvent` over the network
- `:188` returns immediately with `GetLocalVerbs(...)` — **client-computable verbs only**
- The server's full list arrives later: `VerbsResponseEvent` → `HandleVerbResponse` (`:231-234`)
  → invokes the `public Action<VerbsResponseEvent>? OnVerbsResponse` callback (`:48`)

So the MCP `list_verbs` tool **must be request-then-await**, subscribing to `OnVerbsResponse`.
Treating the return value of `GetVerbs` as the answer would silently drop every server-side
verb — which is most of the interesting ones.

No explicit rate limit found on the client side; server-side throttling not investigated.

Execution is clean: `ExecuteVerb` (`:209-229`) sends
`RaisePredictiveEvent(new ExecuteVerbEvent(target, verb))` (`:228`) for server verbs, handles
client-exclusive verbs locally (`:224-226`), and refuses `verb.Disabled` (`:215-222`). This is
exactly the seam we want for Phase 4.

### Bonus: reusable visible-entity enumeration

`VerbSystem.TryGetEntityMenuEntities` (`:67-170`) already does what our observation layer
needs, correctly:

- sprite-tree AABB query for on-screen entities (`:90-96`)
- container handling, including entities sharing a container with the player (`:100-121`)
- **FOV / examinability filtering** via `_examine.CanExamine(...)` (`:144`)
- drops entities tagged `HideContextMenu` (`:152-156`)
- drops invisible sprites (`:163-167`)

**Reuse this rather than writing our own entity enumeration.** It is the game's own definition
of "what the player can see and interact with," which is precisely the honest-information
boundary we want.

---

## Q3 — Chat receive hooks

**Answer: there is a public C# event. No upstream edit required.**

`Content.Client/UserInterface/Systems/Chat/ChatUIController.cs`:

- `:174` — `public event Action<ChatMessage>? MessageAdded;` ← **subscribe here**
- `:184` — raw net registration `_net.RegisterNetMessage<MsgChatMessage>(OnChatMessage)`
- `:809` — `OnChatMessage(MsgChatMessage)` → `ProcessChatMessage` (`:821`)
- Sending: `SendMessage(ChatBox, ChatSelectChannel)` (`:737`) → `_manager.SendMessage(text, channel)`
  (`:768`). `IChatManager` (`Content.Client/Chat/Managers/IChatManager.cs`) is the send seam
  for Phase 4.

### Message shape

`Content.Shared/Chat/MsgChatMessage.cs:11-38` — `ChatMessage`: `Channel`, `Message` (spoken
text after accents), `WrappedMessage` (rich-text markup), `SenderEntity` (`NetEntity`),
`SenderKey`, `HideChat`, `MessageColorOverride`, `AudioPath`.

### Channels

`Content.Shared/Chat/ChatChannel.cs:7` — a `[Flags]`-style `ushort` enum: `Local` (`:14`),
`Whisper` (`:19`), `Server` (`:24`), `Damage` (`:29`), `Radio` (`:34`), `LOOC` (`:39`),
`OOC` (`:44`), `Visual` (`:50`), `Notifications` (PDA — `:56`), `Emotes` (`:61`),
`Dead` (`:66`), plus admin channels.

**Caveat:** `Radio` is a *single* flag. The specific department (Engineering, Security,
Command…) is not in the enum — it is carried inside `WrappedMessage` markup. Extracting the
department requires parsing that markup. Deferred to the build step to confirm the exact
format, since it matters for "monitor the Engineering channel" style tools.

---

## Q4 — Entity identity

**Answer: `NetEntity`. Confirmed.**

`RobustToolbox/Robust.Shared/GameObjects/NetEntity.cs:17` — `public readonly struct NetEntity`
wrapping `int Id` (`:19`), with `ClientEntity = 2 << 29` (`:21`) marking client-side-only
entities.

Corroborated by usage across the networked API: `VerbSystem.GetVerbs(NetEntity target, …)`
(`VerbSystem.cs:176`), `ExecuteVerb(NetEntity …)` (`:209`), `ChatMessage.SenderEntity`
(`MsgChatMessage.cs:27`), `ContainerData.ContainedEntities` (`ContainerManagerComponent.cs:51`).

Conversion helpers on `EntitySystem`: `GetNetEntity()`, `GetEntity()`, `TryGetEntity()`, and
`NetEntity.IsClientSide()`.

**Wire protocol `e:<NetEntity.Id>` is the right choice.** `EntityUid` is local-only and must
never cross the WebSocket.

---

## Q5 — Container depth

**Answer: contents are networked; exact culling needs runtime confirmation.**

`RobustToolbox/Robust.Shared/Containers/ContainerManagerComponent.cs:46-59` —
`ContainerManagerComponentState.ContainerData` carries `NetEntity[] ContainedEntities`
alongside `ShowContents` and `OccludesLight`, serialized for every container the client knows
about. Built in `SharedContainerSystem.cs:75-76`.

PVS builds its chunks by recursing over transform children
(`RobustToolbox/Robust.Server/GameStates/PvsChunk.cs:179-226`,
`PvsSystem.Overrides.cs:144-162` `RecursivelyAddChildren`), and contained entities are
transform children of their container. So contained entities' own component state generally
does reach the client.

**Implication:** the client plausibly knows the contents of bags and lockers it can see —
including other players' — without opening them.

**Deferred to the build step:** whether closed or opaque containers are culled in practice.
This is a genuine runtime question, and it matters beyond correctness: if the client really
does hold other players' bag contents, surfacing that to the agent would be technically
"honest" (the client has it) while being socially equivalent to cheating. Decide the policy
once the behaviour is confirmed.

---

## Q6 — Overlay / highlight API

**Answer: directly reusable for entity outlining. Labels need separate work.**

`Content.Client/Outline/TargetOutlineSystem.cs`:

- Apply: `:179-182` — `_sprite.SetPostShader(sprite, new SpriteComponent.PostShaderArgs(
  ContentPostShaderIds.TargetOutline, shader) { After = ContentPostShaderIds.AfterBaseEffects })`
- Remove: `:161` — `_sprite.RemovePostShader(sprite, ContentPostShaderIds.TargetOutline)`
- Shaders: `SelectionOutlineInrange` / `SelectionOutline` (`:19-20`)
- The system tracks applied sprites in `_highlightedSprites` and clears them in
  `RemoveHighlights()` (`:188`) — the same bookkeeping pattern our TTL-based annotations need

Related: `Content.Client/Outline/InteractionOutlineSystem.cs`,
`Content.Client/Interactable/Components/InteractionOutlineComponent.cs`.

Screen-space overlays go through `IOverlayManager.AddOverlay(...)`, as used at
`Content.Client/Entry/EntryPoint.cs:165-167`. Our system can call this from its own
`Initialize()` — no need to touch `EntryPoint`.

**Gap:** floating text labels next to highlighted entities are *not* provided by the outline
shader. `mark_tile` and labelled highlights need a custom `Overlay` subclass drawing into
world space. Straightforward, but it is real work, not reuse. PLAN.md §5 is still achievable;
budget for it.

---

## Mod seam — smaller than budgeted

PLAN.md §3 budgeted three upstream touchpoints. **The real number is two**, both append-only.

| Concern | Registration | Upstream edits |
|---|---|---|
| Our `EntitySystem` | Reflection auto-registration (`EntryPoint.Init` → `DoAutoRegistrations`, `:101`) | **0** |
| Side panel `UIController` | Reflection: `_reflectionManager.GetAllChildren<UIController>()` — `RobustToolbox/Robust.Client/UserInterface/Controllers/UserInterfaceManager.Controllers.cs:178,188` | **0** |
| Annotation overlays | `IOverlayManager.AddOverlay` from our own `Initialize()` | **0** |
| Keybinds (panel toggle, panic key) | Add `BoundKeyFunction` fields to `Content.Shared/Input/ContentKeyFunctions.cs`; register in `Content.Client/Input/ContentContexts.cs` `SetupContexts` | **2 files, 1 line each** |

Plus a default binding entry in the keybinds YAML under `Resources/Prototypes/`.

This is very good for rebase discipline — the fork diff is essentially one new directory plus
two one-line additions.

---

## Incidentals

- **SDK:** `global.json` pins `10.0.100` with `rollForward: latestFeature`. The machine
  currently has .NET **runtimes** 6/8/9/10 but **no SDK** — `dotnet --list-sdks` is empty.
  Install required before any build.
- **Solution:** `SpaceStation14.slnx`. Launch scripts `runclient.bat`, `runserver.bat`
  (and `-Tools` variants) at repo root.
- **DoAfters:** `Content.Shared/DoAfter/DoAfter.cs` — networked and client-visible:
  `ushort Index` (`:13`), `TimeSpan StartTime` (`:24`), `TimeSpan? CancelledTime` (`:30`),
  `bool Cancelled => CancelledTime != null` (`:41`). Client system at
  `Content.Client/DoAfter/DoAfterSystem.cs`, progress bar at `DoAfterOverlay.cs`.
  `Index` is the natural handle for the `await_action` tool.
- **BUIs without a window:** `RobustToolbox/Robust.Shared/GameObjects/Systems/SharedUserInterfaceSystem.cs:1042`
  — `TryGetOpenUi(Entity<UserInterfaceComponent?>, Enum uiKey, out BoundUserInterface bui)`,
  with a generic overload at `:1052`. Gives us the live BUI object for reading state and
  sending messages. Whether a BUI can be *opened* headlessly (without constructing its window)
  is still open — deferred to the build step.

---

---

# Second pass — deferred questions resolved by source reading

All five questions previously deferred to the build step turned out to be answerable from
source. Two of the answers change the design.

## D1 — `ByteGasTemperature` coverage, and the sentinel values

**Answer: populated for every tile that can hold atmosphere — and it encodes more than
temperature.**

`Content.Server/Atmos/EntitySystems/GasTileOverlaySystem.cs:218-225`, in `UpdateChunkTile`:

```csharp
ThermalByte newByteTemp = new();                            // default = AtmosImpossible (255)
if (tile.Hotspot.Valid)
    newByteTemp.SetTemperature(tile.Hotspot.Temperature);   // on fire
else if (!tile.Space && tile.Air?.TotalMoles <= 5f)
    newByteTemp.SetVacuum();                                // 254
else if (!tile.Space && tile.Air != null)
    newByteTemp = new(tile.Air.Temperature);                // 0-250
```

So this is not incidental to fire or visible gas — it covers all tiles with air.

The byte is a tagged union (`Content.Shared/Atmos/EntitySystems/SharedGasTileOverlaySystem.cs:131-163`):

| Value | Meaning |
|---|---|
| `0-250` | temperature, 0–1000 K, `TempDegreeResolution` = **4 K per step** |
| `251-253` | reserved |
| **`254` `StateVacuum`** | **vacuum** — set when `TotalMoles <= 5f` |
| **`255` `AtmosImpossible`** | **tile cannot hold atmosphere (i.e. a wall)** |

**This materially improves the atmos picture from Q1.** Two free per-tile signals we did not
have before:

1. **Vacuum detection.** Not a pressure *number*, but the safety-critical distinction —
   breach and space detection work without an analyser.
2. **Wall detection.** `AtmosImpossible` gives a free structural map of which tiles block
   atmosphere, useful for the tile-grid renderer independent of sprite inspection.

Update throttling (`:232-234`, and the doc comment at `:138-141`): the value is only re-sent
when it differs by more than one step, so expect ~4 K minimum / ~6 K average quantisation
before an update lands. Fine for "is this room on fire," useless for precise thermometry.

## D2 — Radio department extraction

**Answer: deterministic, and parseable.**

`Content.Server/Radio/EntitySystems/RadioSystem.cs:112-118` builds the wrapped message with
the channel injected as an escaped-bracket token:

```csharp
("channel", $"\\[{channel.LocalizedName}\\]"),
```

The format string (`Resources/Locale/en-US/headset/headset-component.ftl:2`):

```
chat-radio-message-wrap = [color={$color}]{$channel} [bold]{$name}[/bold] {$verb}, [font=…]"{$message}"[/font][/color]
```

So a radio `ChatMessage.WrappedMessage` looks like:

```
[color=#1b67a5]\[Engineering\] [bold]Urist McHands[/bold] says, [font=…]"breach in atmos"[/font][/color]
```

Extract the department with `\\\[(.+?)\\\]` against the head of `WrappedMessage`. The brackets
are deliberately escaped so the rich-text parser does not treat them as tags, which makes them
a reliable delimiter. There is a `-bold` variant (`:3`) with the same channel placement.

Note this is the **localized** channel name, so it is display text, not a stable id. Good
enough for "which channel was this," but do not key logic off the string across locales.

## D3 — Container culling

**Answer: there is none. Container contents are sent whenever the container is in PVS.**

`RobustToolbox/Robust.Server/GameStates/PvsSystem.Overrides.cs:144-164` — `RecursivelyAddChildren`
walks every transform child and filters on exactly two things:

- `VisibilityMask` (`:156`) — the ghost/AI visibility layer system, unrelated to containers
- PVS budget exhaustion (`:159-161`)

**No container check exists anywhere in the path.** Contained entities are transform children
of their container, so they are added like any other child. `PvsChunk.cs:179-226` recurses
children the same way.

Corroborating: `ClientGameStateManager.cs:1425-1426` describes an entity leaving PVS while its
container stays as a *"cursed scenario"* — i.e. the expected behaviour is that contents remain.

The `ExpectedEntities` mechanism (`Robust.Client/GameObjects/EntitySystems/ContainerSystem.cs:28`)
is not privacy either — it handles the case where the container state arrives before its
contents due to **budget or ordering**, and reconciles when they turn up.

### This is a policy decision, not a technical one

The client legitimately holds the contents of closed lockers and other players' backpacks.
Surfacing that to the agent would be *technically* honest — the client really does have it —
while being functionally equivalent to a cheat client.

**Recommendation:** the observation layer must apply its own container filter rather than
mirroring what the client happens to know. Report contents only for containers the human has
actually opened (i.e. where the corresponding BUI is open), plus the player's own equipped
storage. This is the first place where "what the client has" and "what we should expose"
genuinely diverge, and PLAN.md's honest-information claim now depends on us enforcing it in
code rather than getting it for free from PVS.

## D4 — Headless BUI

**Answer: sending is headless; opening is not.**

`RobustToolbox/Robust.Shared/GameObjects/Systems/SharedUserInterfaceSystem.cs:926` —

```csharp
public void ClientSendUiMessage(Entity<UserInterfaceComponent?> entity, Enum key, BoundUserInterfaceMessage message)
```

A plain system call taking entity + UI key. **No `BoundUserInterface` object and no window are
required.** `BoundUserInterface.SendMessage` (`BoundUserInterface.cs:114-117`) is just a thin
wrapper over it.

But it guards (`:931-936`) that the local player is already registered in
`entity.Comp.Actors[key]` — the UI must already be open for us server-side. Opening goes
through `OpenUi` (`:688-727`), which client-side constructs the concrete BUI subclass, and
content BUIs build their window in `Open()`.

**Practical consequence for Phase 5:** the agent can drive any UI the *human* has open, with no
window work at all. Agent-initiated opening will have to let the window be constructed (it can
be left unshown) or go through a raw `OpenBoundInterfaceMessage`
(`UserInterfaceComponent.cs:223`) — the latter still needs runtime confirmation of how the
client reacts to state for a UI it has no local BUI for. Given the copilot's advisory default,
"drive what the human opened" covers the realistic cases.

## D5 — Verb request throttling

**Answer: none server-side.**

`Content.Server/Verbs/VerbSystem.cs:28-66` — `HandleVerbRequest` validates entity existence
(`:32`) and that the sender has an attached entity (`:38`), then builds and returns the
response. There is **no rate limiter**, and no `RateLimit` reference anywhere in either the
client or server verb systems.

So `list_verbs` will not be throttled by the server. That makes it our responsibility not to
abuse it — a per-target, per-tick cache on our side remains worth having, both for token budget
and to avoid generating conspicuous traffic.

---

## Still open — genuinely require a running client

1. **Raw `OpenBoundInterfaceMessage` behaviour** — how the client handles incoming BUI state
   for a UI with no locally constructed `BoundUserInterface` (D4). Only matters if we want
   agent-initiated UI opening in Phase 5.
2. **Panel focus** — that the side panel's text input never swallows game keys, and that typing
   to the agent cannot leak into in-game chat.
3. **Reconnect semantics** — round restart, character change, ghosting mid-round.

Everything else previously deferred is now answered above.
