# SS14 Agent Copilot — Implementation Plan

**Goal:** an AI copilot that plays Space Station 14 *alongside* a human, sharing the same
character. The AI sees exactly what the human's client sees, talks to the human, and
annotates the human's screen. The human performs the actions.

**Status:** design. No code written yet.

---

## 1. Locked decisions

| Question | Decision |
|---|---|
| Control model | AI and human share **one** character / one client session |
| Default authority | **AI observes and advises; the human acts.** Agent control is a later, opt-in phase |
| Process topology | **Separate MCP server process**; the modified client exposes a dumb local JSON API |
| Injection layer | Content-system layer of the client — above game state, below UI |
| Information model | Whatever the client legitimately has (PVS/examine/radio filtered). No server mod, no privileged data |

### Why this shape

- A modified client only knows what the server sent it, so the honest-information problem
  solves itself. No visibility filter to build or police.
- No server cooperation needed; works against any build.
- The human handles everything sub-second. The agent handles the 2–20 s deliberative layer.
- Sitting below the UI but above the network means the agent can *draw on the human's
  screen* — the single most valuable capability in this design (see §5).

### What the agent is actually for

Design tools around what agents are good at and humans are bad at in SS14:

- recipe / chemistry / construction recall
- monitoring several radio channels at once
- map knowledge and route-finding
- checklist discipline during a crisis
- noticing the alarm the human tuned out

**Not** twitch play. Anything requiring a reaction inside one second is out of scope by
construction.

---

## 2. Architecture

```
                 ┌─────────────────────────────────────────────┐
                 │  Modified SS14 Client (C#, forked)          │
                 │                                             │
   human input ──┤  UI / Clyde                                 │
                 │      ↑ overlays, side panel                 │
                 │  Content.Client/Agent/         ◄── the mod  │
                 │      ↑ reads             ↓ annotates        │
                 │  Content client systems (verbs, chat, …)    │
                 │  IEntityManager  (client ECS mirror)        │
                 │  IClientGameStateManager (PVS deltas)       │
                 │  IClientNetManager (Lidgren/UDP)            │
                 └──────────────────┬──────────────────────────┘
                                    │ localhost WebSocket, JSON
                                    │ (dumb: raw state + commands)
                 ┌──────────────────┴──────────────────────────┐
                 │  ss14-mcp  (TypeScript, separate process)   │
                 │  • MCP tool schemas                         │
                 │  • observation rendering + token budgeting  │
                 │  • event buffer / cursor                    │
                 └──────────────────┬──────────────────────────┘
                                    │ MCP (stdio)
                              Claude / agent host
```

**Split rationale:** the agent-facing surface (tool descriptions, text rendering, how much
detail to emit) will be iterated on 100× more often than the game hooks. Keeping it in TS
means those iterations are a file save, not a C# rebuild plus game restart. Cost: one extra
process to run and a wire protocol to maintain. Accepted.

The client side stays deliberately stupid: it serialises state and executes primitive
commands. All judgment lives in the MCP server.

---

## 3. Repository layout

```
SS14MCP/
  PLAN.md
  client/            # git submodule or sibling checkout of the space-station-14 fork
  bridge/            # C# source that lives at Content.Client/Agent/ in the fork
  mcp/               # TypeScript MCP server
  protocol/          # shared JSON schema + generated types for both sides
  logs/              # recorded observation/action streams
```

### Fork discipline (verified — the seam is smaller than budgeted)

The upstream `space-station-14` repo moves fast. Inspection confirms the mod can be an
**essentially additive diff**: one new directory plus **two one-line additions**.

| Concern | Registration | Upstream edits |
|---|---|---|
| Our `EntitySystem` | reflection auto-registration | **0** |
| Side panel `UIController` | reflection — `GetAllChildren<UIController>()` (`RobustToolbox/…/UserInterfaceManager.Controllers.cs:178,188`) | **0** |
| Annotation overlays | `IOverlayManager.AddOverlay` from our own `Initialize()` | **0** |
| Keybinds (panel toggle, panic key) | see below | **4 files, 1 line each** |

The keybind seam is four one-line additions, not the two originally budgeted — the extra pair
buys rebinding from the in-game options menu, which is worth it:

| File | Addition |
|---|---|
| `Content.Shared/Input/ContentKeyFunctions.cs` | `BoundKeyFunction ToggleAgentPanel` |
| `Content.Client/Input/ContentContexts.cs` | `common.AddFunction(...)` in `SetupContexts` |
| `Resources/keybinds.yml` | default binding (`F12`, verified unbound upstream) |
| `Content.Client/Options/UI/Tabs/KeyRebindTab.xaml.cs` | `AddButton(...)` — the rebind list is curated, not enumerated, so without this the function is invisible in options |

Plus `Resources/Locale/en-US/escape-menu/ui/options-menu.ftl` for that button's display name.
All five are pure additions adjacent to the `OpenGuidebook` lines, which keeps rebase conflicts
to a single well-defined hunk per file.

> **Touching `ContentKeyFunctions` means rebuilding the server too.**
> `BoundKeyMap.PopulateKeyFunctionsMap()` (`RobustToolbox/Robust.Shared/Input/BoundKeyMap.cs:75`)
> assigns each key function a `KeyFunctionId` by **position** in a reflection-built list, and that
> id is what goes over the wire in input commands. `ContentKeyFunctions` lives in
> `Content.Shared`, so adding a function to it shifts the ids of every function after it.
>
> A client built against the new list talking to a server built against the old one does **not**
> fail the handshake — it connects fine and then silently misroutes input. Observed symptom:
> movement is predicted client-side, rejected server-side, and the player snaps back to spawn,
> unable to move. Rebuild **and restart** both sides after any change here.

- Everything new lives in `Content.Client/Agent/`.
- Anything that "would be easier if I just edited `InteractionSystem`" gets solved with an
  event subscription instead.
- Pin the engine (`RobustToolbox`) submodule; upgrade deliberately, never incidentally.

### The client cannot run sandboxed

RobustToolbox type-checks content assemblies against a whitelist
(`RobustToolbox/Robust.Shared/ContentPack/Sandbox.yml`) that forbids `System.Net.Sockets`,
the whole of `System.Text.Json`, `SemaphoreSlim`, `Volatile` and `UTF8Encoding`. A sandboxed
client does not merely fail to connect — it refuses to start, with
`TypeCheckFailedException` during module load.

The bridge needs all of those. So **every launch requires `ROBUST_DISABLE_SANDBOX=1`**
(`GameController.cs:172`), which is why `run-client.ps1` exists.

This is a permanent property of the project, not a dev-time shortcut:

- The sandbox exists to contain content assemblies **supplied by the server you connect to**.
  Running from source, the client loads `Content.Client.dll` from the local `bin/` and downloads
  no code, so there is nothing for it to contain. That reasoning holds only for a locally-built
  client against a trusted server.
- Rejected alternative: fork RobustToolbox and whitelist the types. That needs the engine fork we
  otherwise avoid, weakens the policy for *all* content rather than opting out per launch, and
  becomes a rebase burden on every engine upgrade.
- Rejected alternative: stay inside the sandbox using file-drop IPC via `IResourceManager.UserData`
  plus a hand-rolled JSON serialiser. Possible, but trades push for polling and puts a large amount
  of bug surface in the one layer that has to be trustworthy.

Anyone who ever runs this copilot has to accept the same trade. Say so plainly in any README.

Base commits recorded in `client/BASE_COMMIT`:
`space-station-14` `a86b131`, `RobustToolbox` `724345a` (2026-08-01, engine v286.0.0).

---

## 4. Client mod: observation

A single `AgentBridgeSystem : EntitySystem` on the client owns collection. It does **not**
poll the world every tick; it snapshots on request and maintains a cheap event log between
snapshots.

### 4.1 What to collect

**Self**
- damage by type (brute / burn / toxin / airloss / cellular), total, crit + death thresholds
- body part state, bleeding
- stamina, hunger, thirst
- status effects and active alerts (crit, low pressure, cold, hot, hungry, thirsty)
- job, ID card and access levels, current grid/map, coordinates
- standing / lying / buckled / pulling / pulled

**Hands and inventory**
- each hand: item name, and item-relevant state (welder lit? tank valve open? cell charge?)
- worn slots, and contents of held/worn containers one level deep
- pocket / bag contents

**Surroundings**
- tile grid, default 15×15 centred on the player, rendered as a legend + ASCII map:
  walls, floors, airlocks (and open/closed/locked), machines, space, hazards
- entity list with name, relative bearing and distance ("crowbar, 2 tiles NE"),
  plus salient state (lying down, on fire, dead, powered/unpowered)
- doors and their access requirements, since that drives routing

**Containers — filtered by us, not by the game**

PVS applies **no container culling**: the client receives the contents of closed lockers and
other players' bags whenever the container itself is visible (`docs/client-findings.md` §D3).
Mirroring that to the agent would be technically honest and functionally a cheat client.

The observation layer must therefore apply its own filter: report contents only for containers
the human has actually opened (the corresponding BUI is open) plus the player's own equipped
storage. This is the one place where the honest-information property is **not** inherited from
the architecture and has to be enforced in our code. Treat it as a correctness requirement.

**Atmos — tiered by instrument** *(revised after inspection; see `docs/client-findings.md` §Q1)*

The client does **not** hold per-tile pressure or gas composition. What it has:

| Tier | Source | Data |
|---|---|---|
| Always, every tile in PVS | `GasOverlayData` | fire state; **vacuum flag**; **wall / no-atmosphere flag**; temperature 0–1000 K at 4 K resolution; visual density of **visible gases only** — Plasma, Tritium, WaterVapor, Ammonia, Frezon |
| Always, on the player | Alert prototypes | `LowPressure`, `HighPressure`, `Cold`, `Hot`, `Breathing` — qualitative, not numeric |
| Only while held and used | Gas analyser BUI | full mixture for one target: volume, **pressure**, temperature, moles per gas |
| Only while a UI is open | Air alarm / atmos console | room sensor data |

The `ThermalByte` in `GasOverlayData` is a tagged union: `254` means vacuum (set at
`TotalMoles <= 5f`) and `255` means the tile cannot hold atmosphere at all — i.e. a wall. So
**breach and space detection work without an instrument**, and we get a free structural map of
atmosphere-blocking tiles. Temperature is quantised to 4 K steps and only re-sent on a >1-step
change, so treat it as a room-scale sensor, not a thermometer.

Oxygen, Nitrogen, CO2 and N2O remain **invisible** to the client. It cannot distinguish
breathable air from pure nitrogen without an instrument — vacuum is detectable, bad-but-present
atmosphere is not.

The atmos tools must report this honestly — including saying "I can't see pressure from here,
you'd need an analyser." The full-moles debug overlay exists but is `[AdminCommand]`-gated and
is **not** to be used; avoiding exactly that kind of privileged read is why we chose the
client-side architecture.

**Events (log, not snapshot)**
- chat: local speech, whispers, radio by channel, PDA, station announcements
- damage taken, alert transitions, DoAfter start/finish/interrupt
- entities entering/leaving view (rate-limited; this can flood)

### 4.2 Injection points — verified

Verified against `space-station-14` @ `a86b131` and `RobustToolbox` @ `724345a` (both
2026-08-01, engine v286.0.0). Full citations in `docs/client-findings.md`.

| Need | Verified touchpoint |
|---|---|
| Local player entity | `IPlayerManager.LocalEntity` (`Content.Client/Verbs/VerbSystem.cs:74`) |
| **Visible entity enumeration** | **Reuse `VerbSystem.TryGetEntityMenuEntities` (`:67-170`)** — already does sprite-tree lookup, container handling, and FOV/examinability filtering via `_examine.CanExamine` (`:144`). This is the game's own definition of what the player can see; do not reimplement it |
| Atmos | tiered — see §4.1. `SharedGasTileOverlaySystem.cs:67-86` (always), alert prototypes, `GasAnalyzerComponent.cs:36-71` (instrument only) |
| Chat receive | `ChatUIController.MessageAdded` — `public event Action<ChatMessage>` (`Content.Client/UserInterface/Systems/Chat/ChatUIController.cs:174`). No upstream edit needed |
| Chat channels | `ChatChannel` flags enum (`Content.Shared/Chat/ChatChannel.cs:7`). Radio department is **not** in the enum — it is markup inside `WrappedMessage` |
| Chat send (Phase 4) | `IChatManager.SendMessage` (`ChatUIController.cs:768`) |
| Verbs — list | `VerbSystem.GetVerbs` (`:176`) is **async**: it returns local verbs only and the server list arrives via the `OnVerbsResponse` callback (`:48`, `:231`). Must be request-then-await |
| Verbs — execute | `VerbSystem.ExecuteVerb` (`:209`) → `RaisePredictiveEvent(ExecuteVerbEvent)` (`:228`) |
| DoAfters | `Content.Shared/DoAfter/DoAfter.cs` — `Index` (`:13`), `StartTime` (`:24`), `CancelledTime` (`:30`), `Cancelled` (`:41`). `Index` is the `await_action` handle |
| Machine UIs | `SharedUserInterfaceSystem.TryGetOpenUi` (`RobustToolbox/…/SharedUserInterfaceSystem.cs:1042`) |
| Entity highlight | `_sprite.SetPostShader(...)` / `RemovePostShader(...)` as in `Content.Client/Outline/TargetOutlineSystem.cs:179,161` |
| Screen overlays | `IOverlayManager.AddOverlay` from our own `Initialize()` (pattern at `Content.Client/Entry/EntryPoint.cs:165-167`) |
| Entity identity | `NetEntity` (`RobustToolbox/Robust.Shared/GameObjects/NetEntity.cs:17`). `EntityUid` is local-only and must never cross the wire |

**Annotation gap:** the outline shader gives outlines, not text. Labelled highlights and
`mark_tile` need a custom `Overlay` subclass drawing world-space text. Real work, not reuse —
budget for it in Phase 2.

### 4.3 Snapshot vs delta

- `snapshot` returns the full picture above.
- `since(cursor)` returns only what changed plus the event log. The MCP server prefers this
  and only takes full snapshots on demand or when the delta grows large.
- Every payload carries `tick` and a monotonic `cursor`.

---

## 5. Client mod: annotation (the core feature)

Because the mod sits below the UI, it can render into the human's view. This is what makes a
purely advisory agent worth using.

- `highlight(entity, label, colour, ttl)` — outline an entity in-world with a floating label
- `mark_tile(coords, label, colour, ttl)` — waypoint / "breach here" marker
- `path_hint(coords[])` — a breadcrumb line to a destination
- `notify(text, urgency)` — line in the side panel; `urgency: high` also flashes the indicator

All annotations are client-local. Nothing is sent to the server, nothing is visible to other
players, nothing touches gameplay state.

### Side panel

A dockable UI panel, toggled by a keybind, containing:

1. **Conversation** — human types to the agent here, agent replies here. This is the primary
   channel. It keeps strategy out of in-game chat and avoids spamming other players.
2. **Notification feed** — timestamped `notify` entries.
3. **Status line** — current mode, connection state, and (in later phases) the agent's current
   and next intended action.

---

## 6. Client mod: control (Phase 4+, opt-in)

Not built until phases 1–3 have been lived with. Design fixed now so the earlier phases don't
paint us into a corner.

### 6.1 Modes

| Mode | Agent may |
|---|---|
| `observe` (**default, and the default at every startup**) | read state, annotate, converse. No world actions |
| `assist` | additionally act, but only while the human has been idle > N seconds; any human input preempts |
| `autopilot` | act freely; human input still preempts instantly |

Mode is never changed by the agent. Only the human changes it, through the UI. It resets to
`observe` on disconnect, round end, and client restart.

### 6.2 Safety requirements

- **Human input always wins.** Real input cancels the agent's queued action and any in-flight
  DoAfter it started.
- **Panic key** — hard stop, clear queue, drop to `observe`. Bound by default, always live.
- **Persistent on-screen indicator** of mode plus current/next agent action. You cannot debug
  an agent sharing your body without seeing what it intends.
- **Propose-then-confirm** for a denylist of irreversible actions (anything that damages,
  destroys, ignites, opens to space, or targets another player).
- **Chat rate limiting** on any agent-originated in-game speech — a spamming agent gets the
  human banned.

### 6.3 Command path

Drive the content APIs the UI itself calls. **Never synthesise input events into Clyde** —
synthetic input needs window focus, breaks on rebind, and is untestable.

Everything still round-trips through the server's normal validation, so the agent
structurally cannot do anything a player couldn't.

- movement: `InputSystem` bound-key state / mover input
- interaction: client `InteractionSystem` net events
- verbs: `VerbSystem.ExecuteVerb`
- chat/radio: chat manager send path
- machines: `BoundUserInterface.SendMessage`, with state read back from `UpdateState`

### 6.4 DoAfters

Most meaningful SS14 actions are multi-second and interruptible. Any control tool returns
`{started, handle}` immediately; completion arrives as an event. The MCP server exposes
`await_action(handle, timeout)`. Interruption reasons (moved, damaged, target gone) must be
reported, not swallowed.

---

## 7. Wire protocol (client ↔ MCP server)

Localhost WebSocket, newline-free JSON frames, JSON-RPC-ish. Client is the server, MCP
process is the client, reconnects with backoff. Schema and generated types live in
`protocol/`.

### Requests (MCP → client)

```jsonc
{ "id": 17, "op": "snapshot", "params": { "radius": 7 } }
{ "id": 18, "op": "since",    "params": { "cursor": 4412 } }
{ "id": 19, "op": "examine",  "params": { "entity": "e:1042" } }
{ "id": 20, "op": "verbs",    "params": { "entity": "e:1042" } }
{ "id": 21, "op": "annotate", "params": { "kind": "highlight",
                                          "entity": "e:1042",
                                          "label": "crowbar", "ttl": 30 } }
{ "id": 22, "op": "notify",   "params": { "text": "...", "urgency": "normal" } }
{ "id": 23, "op": "say_to_human", "params": { "text": "..." } }
// phase 4+
{ "id": 24, "op": "act", "params": { "kind": "move_to", "coords": [12, -4] } }
```

### Responses / events (client → MCP)

```jsonc
{ "id": 17, "ok": true, "result": { /* snapshot */ } }
{ "id": 24, "ok": false, "error": { "code": "mode_forbids", "mode": "observe" } }
{ "event": "chat",  "cursor": 4413, "channel": "Engineering",
  "speaker": "Urist McHands", "text": "breach in atmos" }
{ "event": "human", "cursor": 4414, "text": "what do I need to fix a breach?" }
{ "event": "alert", "cursor": 4415, "alert": "LowPressure", "state": "entered" }
{ "event": "mode",  "cursor": 4416, "mode": "assist" }
```

Entity references are opaque stable strings (`e:<netid>`), never raw `EntityUid` — client
UIDs are not stable across reconnects.

---

## 8. MCP server: tool surface

Phase 1–3 tools (advisory only):

| Tool | Purpose |
|---|---|
| `observe` | current situation, rendered. Delta by default, `full: true` for everything |
| `look` | local tile map + entity list at a chosen radius |
| `examine(entity)` | the game's own examine text |
| `list_verbs(entity)` | what is possible with this thing, per the game |
| `read_events(since?)` | chat, radio, alerts, human messages since cursor |
| `wait_for(condition, timeout)` | block until something happens instead of burning turns polling |
| `tell_human(text)` | side-panel message |
| `highlight(entity, label)` / `mark(coords, label)` / `clear_marks()` | annotation |
| `screenshot()` | optional, see below |

Phase 4+ adds `move_to`, `pickup`, `drop`, `equip`, `use`, `execute_verb`, `say`, `radio`,
`await_action` — all gated server-side by mode, and gated again client-side. Two independent
gates on purpose.

### Rendering and token budget

Do not re-emit the world every call. `observe` returns prose plus a compact map; the default
is a delta ("since last look: door 3 opened, pressure on your tile fell to 40 kPa,
Engineering radio: …"). Full snapshots only on request or when the delta exceeds a threshold.

### Screenshots

`IClyde` can capture the framebuffer, so feeding the model the actual view is possible.
Structured text stays primary — SS14 sprites are small and many are near-identical, and vision
will misidentify items. Screenshot is an optional supplement for spatial gestalt only.

### Logging

Record the full observation/action/conversation stream to `logs/` from day one, with ticks and
cursors. Trivial to add now; the only way to debug or evaluate behaviour later.

---

## 9. Build order

Each phase ends with something usable.

**Phase 0 — scaffolding**
Fork `space-station-14`, pin engine, build from source. Add an empty `Content.Client/Agent/`
module, the side panel, and the toggle keybind. Rebase onto a newer upstream commit once, on
purpose, to prove the diff stays small.
*Done when:* modified client connects to a local server, panel opens, upstream rebase is clean.

> **Status:** built and committed (`agent-copilot` @ `ff02595`). Rebase test passed — replayed
> onto 165 newer upstream commits (`a86b131`…`b8610a3`) with zero conflicts, so the fork
> discipline holds. Branch was left on the pinned `a86b131`; the rebase was run on a throwaway
> branch so the engine pin and the `docs/client-findings.md` citations stay valid.
> Runtime check passed: local server (`server/server_config.toml`, Saltern, lobby off, loopback
> only) hosts a round, the modified client connects and spawns, and `F12` opens the panel with a
> live status line. Bridge logs under sawmill `system.agent_bridge`.
> **Phase 0 complete.** The three §10 questions remain open; they need play, not startup.

**Phase 1 — read-only bridge**
WebSocket server in the client. `snapshot` / `since` with self, hands, entities, tiles, and
whatever atmos actually turns out to be available. MCP server with `observe`, `look`,
`examine`, `read_events`. Resolve the atmos question here.
*Done when:* the human plays and the agent can accurately describe the situation on request.

**Phase 2 — annotation**
`highlight`, `mark`, `path_hint`, `notify`, TTL expiry, `clear_marks`.
*Done when:* the agent can answer "where's the nearest crowbar" by outlining it.

> **Status: complete** (client `61081f856b`, tools `7e34a80`). All three draw types verified
> rendering over a live round; `clear_marks` verified; TTL auto-expiry verified by placing 5s and
> 300s marks and confirming only one survived 12s.
>
> The done-when scenario has been demonstrated mechanically — `observe` → pick an id → `highlight`
> puts an outline and label on that entity — but not yet driven by a model actually hunting for a
> named object during play. That is a Phase 3 exercise, once the human can ask for it in the panel.

**Phase 3 — conversation and chat**
Side-panel two-way conversation. Read local chat, radio by channel, PDA, announcements.
`wait_for`. No agent speech in-game yet.
*Done when:* the human can ask questions mid-round and get useful answers, and the agent
proactively flags radio traffic the human missed.

> **Status: mostly complete** (client `87fc2de3a0`, tools `bd2482a`).
>
> | | |
> |---|---|
> | Panel conversation, both ways | verified live |
> | Local chat, whisper, emotes | verified live, correct channel and speaker |
> | `wait_for` | verified; exposed and fixed a timeout longer than MCP's own 60s limit |
> | Worn equipment | verified live: 8 slots on a fresh passenger |
> | `canAct` | verified against a brain (false) and a live body (true) |
> | Radio **department** | **inferred, not observed** — see below |
> | Announcements, PDA | untested |
>
> Radio: the extraction regex passes 9/9 department names when tested against a message
> reconstructed from `RadioSystem.cs:117` and `headset-component.ftl:2`, and does not falsely
> match a non-radio message. The transport path is proven for Local/Whisper/Emotes, which use
> identical code. But no real radio message has been observed arriving end to end, so this is
> two verified halves rather than one verified whole. Worth closing with a live message when
> someone is at the keyboard.

> **Phases 1–3 are the deliverable.** A genuinely useful copilot that cannot touch the world.
> Live with it for several rounds before granting hands. Most of the value is here; all of the
> risk is after.

**Phase 4 — control, gated**
Mode switch, on-screen indicator, panic key, preemption, propose-then-confirm denylist. Then
`move_to`, hands, `execute_verb`. Rate-limited in-game speech.
*Done when:* the human can hand over navigation, take it back instantly, and always see what
the agent is about to do.

**Phase 5 — depth**
DoAfters with proper interruption reporting. Bound user interfaces for a chosen handful of
machines (pick by role — e.g. chem dispenser, comms console, air alarm), plus a raw
`send_ui_message` escape hatch. Action queue with preemption. Task list the agent maintains
across the round.

---

## 10. Open questions

Source inspection is complete across two passes (`docs/client-findings.md`). Only three
questions remain, all requiring a **running client**, and none block protocol or bridge design.

1. **Raw `OpenBoundInterfaceMessage` behaviour** — how the client handles BUI state for a UI
   with no locally constructed `BoundUserInterface`. Only matters if we want *agent-initiated*
   UI opening; driving UIs the human already opened needs no window work at all.
2. **Panel focus** — the side panel's text input must never swallow keys the game needs, and
   typing to the agent must not leak into in-game chat.
3. **Reconnect semantics** — round restarts, character changes, and observer/ghost state all
   need defined bridge behaviour. Ghosting mid-round is common.

*Resolved by inspection:* atmos visibility and the vacuum/wall sentinels (§4.1 — twice revised),
verb cost and async shape (§4.2), verb throttling (none exists), chat hooks and radio channel
extraction (§4.2), entity identity (`NetEntity`, §4.2), container culling (none exists — now a
correctness requirement on us, §4.1), headless BUI messaging (§4.2), and the mod seam
(§3, smaller than budgeted).

---

## 11. Rules and conduct

Automated play is against the rules on Wizard's Den and most public SS14 servers, and this
agent acts through the human's own account.

- Develop and run against a **local or private server**.
- Before any public use, ask that server's admins, and disclose it.
- Even in `observe` mode, treat agent-authored in-game speech as the highest-risk surface:
  rate-limit it, and keep it off by default.
- The human running the client is accountable for everything the agent does. The safety
  requirements in §6.2 exist to make that accountability practically exercisable, not as
  decoration.
