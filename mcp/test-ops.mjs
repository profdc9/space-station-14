// Exercises snapshot -> examine -> since together, the way the MCP server will.
//   node mcp/test-ops.mjs

import net from 'node:net';
import readline from 'node:readline';

const PORT = Number(process.env.AGENT_BRIDGE_PORT ?? 21214);
const pending = new Map();
let nextId = 1;

const sock = net.createConnection({ host: '127.0.0.1', port: PORT }, run);
const rl = readline.createInterface({ input: sock });

const events = [];

rl.on('line', line => {
  const msg = JSON.parse(line);

  if (msg.event) {
    events.push(msg);
    return;
  }

  const resolve = pending.get(msg.id);
  if (resolve) {
    pending.delete(msg.id);
    resolve(msg);
  }
});

function call(op, params) {
  const id = nextId++;
  return new Promise(resolve => {
    pending.set(id, resolve);
    sock.write(JSON.stringify({ id, op, params }) + '\n');
  });
}

async function run() {
  sock.write(JSON.stringify({ op: 'hello', token: process.env.AGENT_BRIDGE_TOKEN ?? '' }) + '\n');

  // 1. snapshot, and pick a target out of it
  const snap = await call('snapshot', { radius: 7 });
  if (!snap.ok) { console.error('snapshot failed:', snap.error); process.exit(1); }

  const groups = snap.result.entities;
  console.log(`snapshot: ${groups.length} entity groups, cursor ${snap.result.cursor}`);

  // Prefer something with interesting examine text over a wall.
  const preferred = groups.find(g => /vent|scrubber|airlock|APC|canister|locker|console/i.test(g.name))
                 ?? groups[0];
  const target = preferred.ids[0];
  console.log(`\nexamining "${preferred.name}" ${target}`);

  const ex = await call('examine', { entity: target });
  if (!ex.ok) {
    console.error('examine failed:', ex.error);
  } else {
    console.log(`  name: ${ex.result.name}`);
    console.log(`  tile: ${JSON.stringify(ex.result.tile)}`);
    console.log(`  clientSideOnly: ${ex.result.clientSideOnly}`);
    console.log('  text: ' + JSON.stringify(ex.result.text));
  }

  // 2. examine something that does not exist
  const bad = await call('examine', { entity: 'e:999999' });
  console.log(`\nexamine of a bogus id -> ok=${bad.ok} code=${bad.error?.code}`);

  // 3. since from 0 — everything the client still holds
  const all = await call('since', { cursor: 0 });
  console.log(`\nsince(0): ${all.result.events.length} events, cursor now ${all.result.cursor}, truncated=${all.result.truncated}`);
  for (const e of all.result.events.slice(-10)) {
    console.log('  ' + JSON.stringify(e));
  }

  // 4. since from the current cursor — should be empty
  const none = await call('since', { cursor: all.result.cursor });
  console.log(`\nsince(current): ${none.result.events.length} events (expect 0)`);

  console.log(`\nlive events seen during this run: ${events.length}`);
  sock.end();
  process.exit(0);
}

sock.on('error', e => { console.error(`connection failed: ${e.message}`); process.exit(1); });
setTimeout(() => { console.error('timed out'); process.exit(1); }, 20_000);
