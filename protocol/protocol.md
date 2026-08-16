# Copilot bridge wire protocol

Between the modified SS14 client (**server** of this protocol) and the MCP process (**client**).

Status: **Phase 1**. Ops marked *planned* are specified but not yet implemented.

---

## Transport

Loopback TCP, **newline-delimited JSON**. One JSON object per line, UTF-8, no BOM.

- Address: `127.0.0.1`, port from cvar `agent.bridge_port` (default `21214`).
- The game client listens; the MCP process connects and reconnects with backoff.
- Never bound to a routable address.

### Why not WebSocket

PLAN.md §7 originally specified WebSocket. Hosting one in .NET means `HttpListener`, which on
Windows requires a registered URL ACL and otherwise fails with access-denied for a non-elevated
process. Requiring the game to run as administrator is a far worse trade than dropping a framing
layer that buys nothing on loopback. PLAN.md §7 already called for "newline-free JSON frames",
which is exactly NDJSON's requirement, and `System.Text.Json` escapes newlines inside string
values, so framing is safe.

### Handshake

The first line the MCP process sends must be a hello frame:

```json
{"op":"hello","token":"<agent.bridge_token>"}
```

If `agent.bridge_token` is empty (the default) the token is not checked and any local process may
connect. That is tolerable only while the bridge is read-only: the worst case is another process
on the same machine reading what the player can already see on screen. **This must become
mandatory before Phase 4**, when the bridge gains the ability to act.

One connection is served at a time; a second connection waits in the accept backlog.

---

## Requests — MCP → client

```jsonc
{ "id": 1, "op": "ping" }
{ "id": 2, "op": "say_to_human", "params": { "text": "hull breach two rooms north" } }
{ "id": 3, "op": "notify",       "params": { "text": "pressure falling", "urgency": "high" } }
```

| Op | Params | Effect |
|---|---|---|
| `ping` | — | Liveness. Returns the current cursor. |
| `say_to_human` | `text` | Adds a copilot line to the side-panel conversation. |
| `notify` | `text`, `urgency` (`low`\|`normal`\|`high`) | Adds a line to the notification feed. |
| `snapshot` | `radius` (1–12, default 7) | Self, hands, visible entities, local atmosphere. See below. |
| `since` | `cursor` | Events recorded after `cursor`, oldest first. See below. |
| `examine` | `entity` (`e:<netid>`) | The game's own examine text. See below. |
| `verbs` | `entity` | *planned* — legal interactions per the game |
| `annotate` | `kind`, `entity`/`coords`, `label`, `ttl` | *planned* (Phase 2) |

`id` is echoed in the response. Frames without `op` are rejected.

---

### snapshot result

```jsonc
{
  "cursor": 41,
  "self": {
    "entity": "e:16130", "name": "...", "state": "alive|critical|dead|unknown",
    "alerts": ["Internals", "Stamina"],
    "map": "1", "grid": "e:2",
    "tile": [-20, 5],              // grid indices — the space everything else uses
    "mapPosition": [17.81, 0.48]   // map space, NOT grid space
  },
  "hands": [ { "hand": "right", "active": true, "item": "e:521", "name": "bucket" } ],
  "entities": [
    { "name": "hydroponics tray", "count": 9,
      "tiles": [[-20,0], [-20,-1]],   // index-aligned with ids
      "ids":   ["e:7638", "e:7642"] }
  ],
  "tiles": { "centre": [-20,5], "radius": 7, "legend": {...}, "rows": [...], "notable": [] }
}
```

**Coordinate spaces.** `tile`, `entities[].tiles`, `tiles.centre` and `tiles.notable[].tile` are
all **grid indices**. `self.mapPosition` alone is map space; the two diverge arbitrarily once the
grid moves or rotates. Do not mix them.

**Entities are grouped by name.** `tiles[i]` and `ids[i]` refer to the same entity. Grouping keeps
nine identical trays — or eleven wall segments — from costing nine or eleven objects. A `tiles`
entry may be `null` if the entity is not on a grid.

**Ids are required for targeting.** `examine`, `verbs` and (Phase 2) `highlight` all take an
`e:<netid>`. It is the only way to name one particular tray out of nine, and unlike a position it
survives reconnects.

**Hands** omit `item` and `name` entirely when the hand is empty.

**What is deliberately absent.** Entities fail three filters before reaching this list: the game's
own line-of-sight check (`ExamineSystemShared.CanExamine`), anything inside a container, and
anything without a visible sprite. The second is not inherited from the engine — PVS applies no
container culling, so the client really does hold the contents of closed lockers and other
players' bags. Reporting them would be a cheat client. See PLAN.md §4.1.

### since result

```jsonc
{
  "cursor": 4,          // newest cursor the client holds; pass it back next time
  "truncated": false,   // true means events were dropped before your cursor
  "events": [
    { "alert": "Weightless", "state": "entered", "event": "alert", "cursor": 1 }
  ]
}
```

The client keeps the last **500** events. `truncated: true` means your cursor predates the oldest
retained event, so the history you are getting is incomplete — re-`snapshot` rather than assume
the gap was uneventful.

Events are recorded **whether or not anything is connected**, so a copilot attaching mid-round can
immediately ask what it missed.

### examine result

```jsonc
{
  "entity": "e:6913",
  "name": "air scrubber",
  "tile": [-38, -12],
  "text": "Has a valve and pump attached to it.\nIt appears to be un-powered.\n...",
  "clientSideOnly": true
}
```

`text` is the game's own examine prose, newline-separated. Subject to the same
`CanExamine` check as the snapshot: if the human could not examine it, `not_found` is returned
rather than the text.

`clientSideOnly: true` is a standing caveat, not a transient state. The client's examine text is
generated locally; the server can append text from server-only components that this does not
include. Upstream's own comment (`Content.Client/Examine/ExamineSystem.cs:420`) is that the client
version "will usually be correct (barring server-only components, which generally only adds, not
replaces text)" — so it may be *incomplete*, but should not be *wrong*.

Fetching the server's fuller version would mean an async request/response pair, and the id must be
chosen to avoid colliding with `ExamineSystem._idCounter` — a collision pops an examine tooltip in
the human's face mid-game.

---

## Responses — client → MCP

```jsonc
{ "id": 1, "ok": true,  "result": { "pong": true, "cursor": 41 } }
{ "id": 9, "ok": false, "error": { "code": "unknown_op", "message": "act" } }
```

| Error code | Meaning |
|---|---|
| `bad_json` | Frame did not parse. `id` is `0`. |
| `missing_op` | No `op` field. |
| `unknown_op` | Op not recognised, or not implemented in this phase. |
| `bad_params` | Op recognised, params wrong. |
| `unauthorized` | Handshake token missing or wrong. |
| `mode_forbids` | *planned* — control op attempted in a mode that disallows it. |

---

## Events — client → MCP, unsolicited

Flat objects carrying `event` and a monotonic `cursor`.

```jsonc
{ "event": "human", "cursor": 42, "text": "what do I need to fix a breach?" }
{ "event": "chat",  "cursor": 43, "channel": "Engineering", "speaker": "Urist McHands",
  "text": "breach in atmos" }          // planned (Phase 3)
{ "event": "alert", "cursor": 44, "alert": "LowPressure", "state": "entered" }  // planned
{ "event": "mode",  "cursor": 45, "mode": "assist" }                            // planned (Phase 4)
```

`cursor` increases by one per event and is process-lifetime only — it resets when the game client
restarts. The MCP side must treat a cursor going backwards as "reconnected, re-snapshot", not as
an error.

---

## Entity references

Opaque strings, `e:<netid>`, derived from `NetEntity` — never a raw `EntityUid`, which is local to
one client session and not stable across reconnects.

---

## Threading

Everything on the socket runs off the game thread and touches no engine state. Inbound frames are
queued and dispatched from `AgentBridgeSystem.Update` on the game thread; outbound frames are
queued from the game thread and written by the socket task. RobustToolbox's entity API is not
thread-safe and must never be reached from the socket threads.
