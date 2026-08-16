// Smoke test for the copilot bridge transport. No dependencies — run with:
//   node mcp/test-bridge.mjs
//
// Exercises the handshake, every op implemented in Phase 1, and the error path.
// Exits non-zero if anything did not come back as expected.

import net from 'node:net';
import readline from 'node:readline';

const PORT = Number(process.env.AGENT_BRIDGE_PORT ?? 21214);
const TOKEN = process.env.AGENT_BRIDGE_TOKEN ?? '';

const expected = new Map([
  [1, r => r.ok === true && r.result?.pong === true],
  [2, r => r.ok === true],
  [3, r => r.ok === true],
  [4, r => r.ok === false && r.error?.code === 'unknown_op'],
]);

const seen = new Map();
let failed = false;

const sock = net.createConnection({ host: '127.0.0.1', port: PORT }, () => {
  console.log(`connected to 127.0.0.1:${PORT}`);

  const send = obj => {
    const line = JSON.stringify(obj);
    console.log('>>', line);
    sock.write(line + '\n');
  };

  send({ op: 'hello', token: TOKEN });
  send({ id: 1, op: 'ping' });
  send({ id: 2, op: 'say_to_human', params: { text: 'Copilot online. Reading your situation now.' } });
  send({ id: 3, op: 'notify', params: { text: 'Bridge connected.', urgency: 'high' } });
  send({ id: 4, op: 'definitely_not_an_op' });
});

readline.createInterface({ input: sock }).on('line', line => {
  console.log('<<', line);

  let msg;
  try {
    msg = JSON.parse(line);
  } catch {
    console.error('   ! not valid JSON');
    failed = true;
    return;
  }

  if (msg.event) {
    console.log(`   (event ${msg.event} @ cursor ${msg.cursor})`);
    return;
  }

  const check = expected.get(msg.id);
  if (!check) return;

  seen.set(msg.id, true);
  if (!check(msg)) {
    console.error(`   ! id ${msg.id} did not match expectation`);
    failed = true;
  }

  if (seen.size === expected.size) {
    console.log(failed ? '\nFAILED' : '\nall checks passed');
    sock.end();
    process.exit(failed ? 1 : 0);
  }
});

sock.on('error', e => {
  console.error(`connection failed: ${e.message}`);
  console.error('is the modified client running, and in a round?');
  process.exit(1);
});

setTimeout(() => {
  console.error(`\ntimed out; got ${seen.size}/${expected.size} responses`);
  process.exit(1);
}, 10_000);
