// Exercises the Phase 2 annotation tools against a live client.
//   node test-annotate.ts
//
// Watch the game window: you should see an outlined entity, a marked tile, and a route drawn
// through the room, each with a label.

import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const transport = new StdioClientTransport({ command: 'node', args: ['src/index.ts'] });
const client = new Client({ name: 'annotate-test', version: '0.0.1' });
await client.connect(transport);

async function call(name: string, args: Record<string, unknown> = {}) {
  const r = await client.callTool({ name, arguments: args });
  const text = (r.content as { text?: string }[]).map(c => c.text ?? '').join('\n');
  console.log(`${name}(${JSON.stringify(args)})\n  -> ${text.split('\n')[0]}`);
  return text;
}

const looked = await call('look', { radius: 7 });

// Pull a target and the player's tile straight out of the rendered view.
const id = looked.match(/\[(e:\d+)\]/)?.[1];
const self = looked.match(/at tile (-?\d+),(-?\d+)/);

if (!id || !self) {
  console.error('\nNo entity or player tile in the look output — is the human in a round?');
  console.error(looked.slice(0, 400));
  process.exit(1);
}

const [, sx, sy] = self;
const x = Number(sx);
const y = Number(sy);

console.log();
await call('highlight', { entity: id, label: 'this one', colour: 'orange', ttl: 120 });
await call('mark', { tile: [x + 2, y], label: 'over here', colour: 'cyan', ttl: 120 });
await call('path_hint', {
  tiles: [[x, y], [x + 1, y], [x + 2, y], [x + 2, y + 1], [x + 2, y + 2]],
  label: 'this way',
  ttl: 120,
});

await call('tell_human', { text: 'Marked three things in your view — outline, waypoint, and a route.' });

console.log('\nLook at the game window. Clearing in 20s...');
await new Promise(r => setTimeout(r, 20_000));

await call('clear_marks');

await client.close();
process.exit(0);
