// Resident copilot connection.
//
// The bridge serves one connection at a time, and the panel shows "offline" whenever nothing is
// attached. Short-lived query scripts therefore leave the human looking at a disconnected copilot
// even while it is actively working, and anything they type gets "No copilot connected."
//
// This holds the connection open for the whole session:
//   - every event is appended to logs/copilot-events.log
//   - every line written to logs/copilot-outbox.jsonl is sent to the client
//
//   node daemon.mjs

import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';

const root = path.resolve(import.meta.dirname, '..');
const eventsLog = path.join(root, 'logs', 'copilot-events.log');
const outbox = path.join(root, 'logs', 'copilot-outbox.jsonl');

fs.mkdirSync(path.dirname(eventsLog), { recursive: true });
fs.writeFileSync(eventsLog, '');
fs.writeFileSync(outbox, '');

let sock = null;
let sent = 0;
let outboxOffset = 0;
let nextId = 1000;

const log = line => fs.appendFileSync(eventsLog, line + '\n');

function connect() {
  sock = net.createConnection({ host: '127.0.0.1', port: 21214 }, () => {
    sock.write(JSON.stringify({ op: 'hello', token: '' }) + '\n');
    sock.write(JSON.stringify({
      id: nextId++,
      op: 'say_to_human',
      params: { text: 'Copilot connected. I am watching your alerts and chat.' },
    }) + '\n');
    log(`[${new Date().toISOString()}] connected`);

    const rl = readline.createInterface({ input: sock });
    rl.on('line', line => {
      try {
        const m = JSON.parse(line);
        // Log responses too, not just events: otherwise anything asked through the outbox
        // vanishes, since the daemon holds the only connection slot the bridge allows.
        log(JSON.stringify(m));
      } catch { /* ignore malformed */ }
    });
    rl.on('error', () => {});
  });

  sock.on('error', () => {});
  sock.on('close', () => {
    log(`[${new Date().toISOString()}] disconnected, retrying`);
    sock = null;
    setTimeout(connect, 1000);
  });
}

// Anything appended to the outbox gets sent. Lets another process talk through this connection
// without fighting it for the single slot the bridge allows.
setInterval(() => {
  if (!sock) return;

  let raw;
  try {
    raw = fs.readFileSync(outbox, 'utf8');
  } catch {
    return;
  }

  if (raw.length <= outboxOffset) return;

  const fresh = raw.slice(outboxOffset);
  outboxOffset = raw.length;

  for (const line of fresh.split('\n')) {
    if (!line.trim()) continue;
    try {
      const msg = JSON.parse(line);
      msg.id = nextId++;
      sock.write(JSON.stringify(msg) + '\n');
      sent++;
    } catch { /* skip bad lines */ }
  }
}, 300);

connect();
process.on('SIGINT', () => { sock?.end(); process.exit(0); });
