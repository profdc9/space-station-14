// Pulls one snapshot from the copilot bridge and prints it readably.
//   node mcp/snapshot.mjs [radius]

import net from 'node:net';
import readline from 'node:readline';

const PORT = Number(process.env.AGENT_BRIDGE_PORT ?? 21214);
const TOKEN = process.env.AGENT_BRIDGE_TOKEN ?? '';
const radius = Number(process.argv[2] ?? 7);

const sock = net.createConnection({ host: '127.0.0.1', port: PORT }, () => {
  sock.write(JSON.stringify({ op: 'hello', token: TOKEN }) + '\n');
  sock.write(JSON.stringify({ id: 1, op: 'snapshot', params: { radius } }) + '\n');
});

readline.createInterface({ input: sock }).on('line', line => {
  const msg = JSON.parse(line);
  if (msg.event) return;

  if (!msg.ok) {
    console.error('error:', msg.error);
    process.exit(1);
  }

  const s = msg.result;

  console.log('--- self ---');
  console.log(JSON.stringify(s.self, null, 2));

  console.log('\n--- hands ---');
  for (const h of s.hands) {
    console.log(`  ${h.active ? '*' : ' '} ${h.hand}: ${h.name ?? '(empty)'}`);
  }

  console.log(`\n--- entities (${s.entities.length}) ---`);
  for (const e of s.entities.slice(0, 40)) {
    console.log(`  ${String(e.tile)}  ${e.name}  ${e.id}`);
  }
  if (s.entities.length > 40) console.log(`  ... ${s.entities.length - 40} more`);

  console.log('\n--- tiles ---');
  if (!s.tiles) {
    console.log('  (not on a grid)');
  } else {
    console.log(`  centre ${s.tiles.centre}, radius ${s.tiles.radius}`);
    for (const row of s.tiles.rows) console.log('  ' + row);
    console.log(`  notable: ${s.tiles.notable.length}`);
    for (const n of s.tiles.notable.slice(0, 15)) {
      console.log(`    ${String(n.tile)} ${n.kind}${n.kelvin ? ' ' + n.kelvin + 'K' : ''}`);
    }
  }

  console.log(`\nraw bytes: ${line.length}`);
  sock.end();
  process.exit(0);
});

sock.on('error', e => {
  console.error(`connection failed: ${e.message}`);
  process.exit(1);
});

setTimeout(() => { console.error('timed out'); process.exit(1); }, 15_000);
