# SS14 Copilot

An AI copilot for [Space Station 14](https://spacestation14.io). The AI and a human share one
character: **the AI observes and advises, the human plays.** It cannot move, touch, or speak
in-game — phases 1–3 are read-only by construction.

See [PLAN.md](PLAN.md) for the design and [protocol/protocol.md](protocol/protocol.md) for the wire
format.

## Layout

```
PLAN.md              design, decisions, and what has been verified against source
protocol/            wire protocol between the game client and the MCP server
docs/                findings from reading the SS14 source, pinned to a commit
mcp/                 MCP server (TypeScript, run directly by Node — no build step)
server/              local dev server config
client/              NOT in this repo — see below
```

`client/` is a checkout of a **fork of space-station-14** (`profdc9/space-station-14`, branch
`agent-copilot`). The bridge source lives there, at `Content.Client/Agent/`, versioned alongside
the code it modifies. Everything is additive: one new directory plus five one-line additions to
upstream files.

## Running it

Three processes: the game server, the modified client, and the MCP server.

```powershell
# 1. dev server — Saltern, lobby off, loopback only
dotnet client\bin\Content.Server\Content.Server.dll `
  --config-file server\server_config.toml --data-dir server\data

# 2. modified client
.\run-client.ps1

# 3. MCP server (or let your MCP host spawn it)
cd mcp; node src/index.ts
```

After changing client code, `.\rebuild-client.ps1` — it stops the running client first, because
the client holds open handles on `client\bin\` and a build started without stopping it fails after
a minute of retries.

## Two things that will bite you

**The client cannot run sandboxed.** RobustToolbox type-checks content assemblies and forbids
`System.Net.Sockets` and `System.Text.Json`, both of which the bridge needs — a sandboxed client
refuses to start. `run-client.ps1` sets `ROBUST_DISABLE_SANDBOX=1`. That is safe *here* because a
client run from source loads only locally-built assemblies and downloads no code, but it is a real
reduction in defence-in-depth and it applies to anyone who runs this. See PLAN.md §3.

**Touching `ContentKeyFunctions` means rebuilding the server too.** Key functions are numbered by
list position and that number goes over the wire. A client and server built against different
lists do not fail the handshake — they connect and silently misroute input, which presents as the
player snapping back to spawn. See PLAN.md §3.

## Conduct

Automated play is against the rules on Wizard's Den and most public SS14 servers. Develop and play
on a local or private server. If you ever want to use this somewhere public, ask that server's
admins first.

The copilot is built to see what the human sees and no more. Two filters exist only to preserve
that, because the engine does not give it for free:

- Entities are checked with the game's own line-of-sight test.
- Container contents are excluded. PVS applies no container culling, so the client legitimately
  holds the contents of closed lockers and other players' bags. Reporting them would be a cheat
  client.

If you extend the observation layer, keep that property.
