// Dumps the raw snapshot frame exactly as it comes off the wire, pretty-printed.
//   node mcp/raw-snapshot.mjs [radius]

import net from 'node:net';
import readline from 'node:readline';

const PORT = Number(process.env.AGENT_BRIDGE_PORT ?? 21214);
const radius = Number(process.argv[2] ?? 7);

const sock = net.createConnection({ host: '127.0.0.1', port: PORT }, () => {
  sock.write(JSON.stringify({ op: 'hello', token: process.env.AGENT_BRIDGE_TOKEN ?? '' }) + '\n');
  sock.write(JSON.stringify({ id: 1, op: 'snapshot', params: { radius } }) + '\n');
});

readline.createInterface({ input: sock }).on('line', line => {
  const msg = JSON.parse(line);
  if (msg.event) return;

  console.log(`// wire size: ${Buffer.byteLength(line)} bytes on one line`);
  console.log(JSON.stringify(msg, null, 2));

  sock.end();
  process.exit(0);
});

sock.on('error', e => { console.error(e.message); process.exit(1); });
setTimeout(() => { console.error('timed out'); process.exit(1); }, 15_000);
