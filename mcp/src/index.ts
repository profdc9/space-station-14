// MCP server exposing a modified Space Station 14 client as advisory tools.
//
// Phases 1-3 are read-only by construction: there is no tool here that can move, touch, or speak
// in-game. The human plays; this only observes and reports back. See PLAN.md §1.
//
//   node src/index.ts

import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';

import { Bridge, BridgeError } from './bridge.ts';
import { renderSnapshot, renderDelta, renderEvents, type Snapshot } from './render.ts';

const bridge = new Bridge();
bridge.connect();

/** Last snapshot handed to the model, so `observe` can report a delta instead of the world. */
let lastSnapshot: Snapshot | null = null;

/**
 * Radius the last snapshot was taken at.
 *
 * Diffing two snapshots taken at different radii invents events: widening from 5 to 7 reports
 * everything in the new ring as having "come into view" when nothing moved. A copilot that cries
 * "a recycler just appeared" because the caller changed a parameter is worse than useless, so a
 * radius change forces a full render instead of a delta.
 */
let lastRadius = 0;

const server = new McpServer({
  name: 'ss14-copilot',
  version: '0.1.0',
});

/** Wraps a handler so bridge failures become readable text rather than protocol errors. */
async function guard(fn: () => Promise<string>): Promise<{ content: { type: 'text'; text: string }[] }> {
  try {
    return { content: [{ type: 'text', text: await fn() }] };
  } catch (e) {
    if (e instanceof BridgeError) {
      const text =
        e.code === 'not_connected' || e.code === 'disconnected'
          ? 'The Space Station 14 client is not running, or the copilot bridge is not enabled. Nothing can be observed right now.'
          : e.code === 'no_player'
            ? 'The human is not currently controlling a character — lobby, ghost, or round restart.'
            : `The client refused that: ${e.message}`;

      return { content: [{ type: 'text', text }] };
    }

    throw e;
  }
}

server.registerTool(
  'observe',
  {
    title: 'Observe the situation',
    description:
      'What is happening around the human right now. Returns only what changed since your last ' +
      'observe, which is usually a line or two; pass full: true for the complete picture. Start ' +
      'here rather than with look — this is the cheap call.',
    inputSchema: {
      full: z.boolean().optional().describe('Return the whole situation rather than a delta.'),
      radius: z.number().int().min(1).max(12).optional().describe('Tiles to observe. Default 7.'),
    },
  },
  async ({ full, radius }) =>
    guard(async () => {
      const r = radius ?? 7;
      const snap = await bridge.call<Snapshot>('snapshot', { radius: r });

      const comparable = lastSnapshot !== null && lastRadius === r;
      const delta = !full && comparable ? renderDelta(lastSnapshot!, snap) : null;

      lastSnapshot = snap;
      lastRadius = r;

      if (full || !comparable) return renderSnapshot(snap, { map: true });

      return delta ?? 'No meaningful change since your last observation.';
    }),
);

server.registerTool(
  'look',
  {
    title: 'Look around in detail',
    description:
      'The full local picture: everything visible, plus a character map of the surrounding tiles ' +
      'showing walls, vacuum and fire. More expensive than observe — use when the layout matters, ' +
      'or when you need entity ids to examine something.',
    inputSchema: {
      radius: z.number().int().min(1).max(12).optional().describe('Tiles to look. Default 7.'),
    },
  },
  async ({ radius }) =>
    guard(async () => {
      const r = radius ?? 7;
      const snap = await bridge.call<Snapshot>('snapshot', { radius: r });

      lastSnapshot = snap;
      lastRadius = r;

      return renderSnapshot(snap, { map: true });
    }),
);

server.registerTool(
  'examine',
  {
    title: "Examine a thing, in the game's own words",
    description:
      "The game's own examine text for one entity — how damaged it is, whether it is powered, " +
      'what it contains. Takes an id from observe or look, like "e:6913". This is the game ' +
      'describing itself, not an inference, so prefer it over guessing from a name.',
    inputSchema: {
      entity: z.string().describe('Entity id from observe/look, e.g. "e:6913".'),
    },
  },
  async ({ entity }) =>
    guard(async () => {
      const r = await bridge.call<{
        name: string;
        tile: [number, number] | null;
        text: string;
      }>('examine', { entity });

      const at = r.tile ? ` at ${r.tile[0]},${r.tile[1]}` : '';
      return `${r.name}${at}\n\n${r.text}`;
    }),
);

server.registerTool(
  'read_events',
  {
    title: 'Read what has happened',
    description:
      'Alerts and messages recorded since a cursor. Call with no cursor to get everything since ' +
      'you last read. Use this to catch up rather than re-observing repeatedly.',
    inputSchema: {
      cursor: z.number().int().min(0).optional().describe('Read events after this cursor. Omit for "since last read".'),
    },
  },
  async ({ cursor }) =>
    guard(async () => {
      const r = await bridge.call<{
        cursor: number;
        truncated: boolean;
        events: { event: string; cursor: number; [k: string]: unknown }[];
      }>('since', { cursor: cursor ?? bridge.cursor });

      const warning = r.truncated
        ? 'WARNING: older events were dropped before your cursor; this history is incomplete.\n\n'
        : '';

      return warning + renderEvents(r.events) + `\n\n(cursor now ${r.cursor})`;
    }),
);

server.registerTool(
  'tell_human',
  {
    title: 'Say something to the human',
    description:
      'Writes a line into the copilot panel in the human\'s game window. This is how you talk to ' +
      'them — it is not in-game chat and no other player can see it.',
    inputSchema: {
      text: z.string().min(1).describe('What to tell them. Keep it short; they are playing.'),
    },
  },
  async ({ text }) =>
    guard(async () => {
      await bridge.call('say_to_human', { text });
      return 'Sent.';
    }),
);

server.registerTool(
  'notify',
  {
    title: 'Flag something urgent',
    description:
      'Adds a line to the notification feed, separate from conversation. Use urgency "high" only ' +
      'for things needing action now — a breach, a fire, dropping pressure. Crying wolf here makes ' +
      'the feed useless.',
    inputSchema: {
      text: z.string().min(1),
      urgency: z.enum(['low', 'normal', 'high']).optional(),
    },
  },
  async ({ text, urgency }) =>
    guard(async () => {
      await bridge.call('notify', { text, urgency: urgency ?? 'normal' });
      return 'Posted.';
    }),
);

server.registerTool(
  'bridge_status',
  {
    title: 'Check the bridge',
    description: 'Whether the game client is connected. Use when other tools report nothing is running.',
    inputSchema: {},
  },
  async () => ({
    content: [
      {
        type: 'text' as const,
        text: bridge.connected
          ? `Connected to the SS14 client. Event cursor ${bridge.cursor}.`
          : 'Not connected. Start the modified client with run-client.ps1 — it will attach automatically.',
      },
    ],
  }),
);

const transport = new StdioServerTransport();
await server.connect(transport);

process.on('SIGINT', () => {
  bridge.close();
  process.exit(0);
});
