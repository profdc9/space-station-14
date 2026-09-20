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
const urgentLog = path.join(root, 'logs', 'copilot-urgent.log');
const outbox = path.join(root, 'logs', 'copilot-outbox.jsonl');

fs.mkdirSync(path.dirname(eventsLog), { recursive: true });
fs.writeFileSync(eventsLog, '');
fs.writeFileSync(urgentLog, '');
fs.writeFileSync(outbox, '');

let sock = null;
let sent = 0;
let outboxOffset = 0;
let nextId = 1000;

// Reconnect backoff. The client is often simply not running yet - during a reset the daemon
// starts before the client finishes loading, and after a session it keeps trying against a dead
// bridge until someone kills it. A flat 1s retry logged every attempt turned a 6 MB events log
// into 97.8% "disconnected, retrying" (121,741 of 124,502 lines), burying the real events.
const RETRY_MIN = 1000;
const RETRY_MAX = 30000;
let retryDelay = RETRY_MIN;
let attempts = 0;
let wasConnected = false;

const log = line => fs.appendFileSync(eventsLog, line + '\n');


// ---------------------------------------------------------------------------
// The watcher.
//
// The events log recorded LowOxygen minutes before the human went into crit, and nothing read it.
// It recorded the station's mice suffocating one by one. All of it sat on disk while the human
// died, because the copilot only ever looked when asked.
//
// This reacts on arrival, in the daemon, with no model in the loop: a life-threatening alert
// should reach the human in milliseconds and not wait on a round trip. The advice is deliberately
// fixed text - the daemon is a reflex, not a brain. Anything needing judgement is still the
// model's job.
// ---------------------------------------------------------------------------

// Alert ids are the prototype ids from Resources/Prototypes/Alerts/*.yml, verified against that
// tree rather than guessed.
const WATCH = {
  LowOxygen:    { level: 'high',   say: 'NO OXYGEN. Internals on now: tank on belt or back, mask on face, then the internals button.' },
  LowNitrogen:  { level: 'high',   say: 'NO NITROGEN. If you breathe nitrogen, get internals on now.' },
  LowPressure:  { level: 'high',   say: 'LOW PRESSURE - you are in or near a breach. Get behind a door and seal up.' },
  HighPressure: { level: 'high',   say: 'HIGH PRESSURE. Move away from the source before it ruptures something.' },
  Fire:         { level: 'high',   say: 'YOU ARE ON FIRE. Stop, drop and roll, or find a fire extinguisher or vacuum.' },
  Toxins:       { level: 'high',   say: 'TOXIC AIR. Internals on and leave the area.' },
  HumanCrit:    { level: 'high',   say: 'CRITICAL. You are dying. Call for medical on radio NOW.' },
  BorgCrit:     { level: 'high',   say: 'CRITICAL. Get to a charging station or a roboticist.' },
  HumanDead:    { level: 'high',   say: 'You are dead. Ghost out, or wait for someone to revive you.' },
  BorgDead:     { level: 'high',   say: 'You are destroyed.' },
  Hot:          { level: 'high',   say: 'OVERHEATING. Get away from the heat source.' },
  Cold:         { level: 'high',   say: 'FREEZING. Get somewhere warm or into a suit.' },
  Bleed:        { level: 'normal', say: 'You are bleeding. Gauze or a medkit, or it will not stop on its own.' },
  Starving:     { level: 'normal', say: 'Starving - this does real damage now. Find food.' },
  Parched:      { level: 'normal', say: 'Parched - find a drink.' },
  Ensnared:     { level: 'low',    say: 'Ensnared. Click yourself or move to break free.' },
};

// Never watch these. They are persistent gauges, not warnings - the alert exists for as long as
// the mob does, and it is the severity that moves, which the bridge does not send. Watching
// Stamina announced "you are about to collapse" to a healthy passenger the instant they spawned.
// Firing on a gauge is how a watcher teaches the human to ignore it.
const GAUGES = new Set([
  'Health', 'HumanHealth', 'BorgHealth', 'Stamina', 'Breathing', 'Pressure', 'Temperature',
  'Hunger', 'Thirst', 'Internals', 'Battery', 'BorgBattery', 'AiBattery', 'Essence', 'SuitPower',
  'Piloting', 'PilotingShuttle', 'Pulling', 'Walking',
]);

// The same alert flapping on and off must not become a wall of panel spam - Bleed alone flapped
// 4 times in as many seconds in the last session's log.
const REPEAT_SILENCE_MS = 90_000;
const lastFired = new Map();

function urgent(line) {
  fs.appendFileSync(urgentLog, `[${new Date().toISOString()}] ${line}\n`);
}

function watch(msg) {
  if (msg.event !== 'alert' || msg.state !== 'entered')
    return;

  const rule = GAUGES.has(msg.alert) ? undefined : WATCH[msg.alert];
  if (!rule)
    return;

  const now = Date.now();
  const previous = lastFired.get(msg.alert) ?? 0;

  if (now - previous < REPEAT_SILENCE_MS)
    return;

  lastFired.set(msg.alert, now);
  urgent(`${rule.level.toUpperCase()} ${msg.alert}: ${rule.say}`);

  // Straight down the live socket rather than through the outbox: the outbox is polled every
  // 300ms and this is the one message where that matters.
  if (sock) {
    sock.write(JSON.stringify({
      id: nextId++,
      op: 'notify',
      params: { text: rule.say, urgency: rule.level },
    }) + '\n');
  }
}

function connect() {
  sock = net.createConnection({ host: '127.0.0.1', port: 21214 }, () => {
    sock.write(JSON.stringify({ op: 'hello', token: '' }) + '\n');
    sock.write(JSON.stringify({
      id: nextId++,
      op: 'say_to_human',
      params: { text: 'Copilot connected. I am watching your alerts and chat.' },
    }) + '\n');
    log(attempts
      ? `[${new Date().toISOString()}] connected after ${attempts} attempt(s)`
      : `[${new Date().toISOString()}] connected`);

    retryDelay = RETRY_MIN;
    attempts = 0;
    wasConnected = true;

    const rl = readline.createInterface({ input: sock });
    rl.on('line', line => {
      try {
        const m = JSON.parse(line);
        // Log responses too, not just events: otherwise anything asked through the outbox
        // vanishes, since the daemon holds the only connection slot the bridge allows.
        log(JSON.stringify(m));
        watch(m);
      } catch { /* ignore malformed */ }
    });
    rl.on('error', () => {});
  });

  sock.on('error', () => {});
  sock.on('close', () => {
    // Log the transition, not the waiting. A live connection dropping is news; the hundredth
    // failed attempt against a client that is still not running is not.
    if (wasConnected) {
      log(`[${new Date().toISOString()}] disconnected, retrying`);
    } else if (attempts === 0) {
      log(`[${new Date().toISOString()}] bridge unavailable, retrying with backoff`);
    }

    wasConnected = false;
    attempts++;
    sock = null;

    setTimeout(connect, retryDelay);
    retryDelay = Math.min(retryDelay * 2, RETRY_MAX);
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
