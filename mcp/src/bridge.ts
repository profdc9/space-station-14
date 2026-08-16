// Client for the copilot bridge: loopback TCP, newline-delimited JSON.
// See protocol/protocol.md.

import net from 'node:net';
import readline from 'node:readline';

export type BridgeEvent = {
  event: string;
  cursor: number;
  [key: string]: unknown;
};

type Response =
  | { id: number; ok: true; result?: unknown }
  | { id: number; ok: false; error: { code: string; message?: string } };

export class BridgeError extends Error {
  // Written out longhand rather than as a parameter property: Node runs these files by stripping
  // types only, and parameter properties emit real code, so they are rejected.
  readonly code: string;

  constructor(code: string, message?: string) {
    super(message ? `${code}: ${message}` : code);
    this.code = code;
  }
}

/**
 * Holds a connection to the game client, reconnecting when it goes away.
 *
 * The game client restarts far more often than this process does — every rebuild, every crash,
 * every round the human rejoins. So a dropped connection is normal operation, not an error worth
 * surfacing to the model. Calls made while disconnected fail fast with `not_connected` rather
 * than hanging, because a tool call that blocks for a minute is worse than one that says "the
 * game isn't running".
 */
export class Bridge {
  #socket: net.Socket | null = null;
  #connected = false;
  #nextId = 1;
  #pending = new Map<number, (r: Response) => void>();

  /** Events pushed by the client since we last handed them out. */
  #inbox: BridgeEvent[] = [];

  /** Highest cursor we have seen, so `read_events` can default to "since last time". */
  #cursor = 0;

  #reconnectDelay = 250;
  #closed = false;

  readonly #host: string;
  readonly #port: number;
  readonly #token: string;

  constructor(
    host = '127.0.0.1',
    port = Number(process.env.AGENT_BRIDGE_PORT ?? 21214),
    token = process.env.AGENT_BRIDGE_TOKEN ?? '',
  ) {
    this.#host = host;
    this.#port = port;
    this.#token = token;
  }

  get connected() {
    return this.#connected;
  }

  get cursor() {
    return this.#cursor;
  }

  connect() {
    if (this.#closed) return;

    const socket = net.createConnection({ host: this.#host, port: this.#port });
    this.#socket = socket;

    socket.on('connect', () => {
      this.#connected = true;
      this.#reconnectDelay = 250;
      socket.write(JSON.stringify({ op: 'hello', token: this.#token }) + '\n');

      // Attached only after a successful connect. Wiring readline to a socket that has not
      // connected means a refused connection is re-emitted as an error on the Interface, where
      // socket.on('error') does not catch it, and the process dies. This process normally starts
      // before the game does, so "connection refused" is the common path, not the rare one.
      const rl = readline.createInterface({ input: socket });
      rl.on('line', line => this.#onLine(line));
      rl.on('error', () => {});
    });

    socket.on('error', () => {
      // Handled by 'close'. Swallowed here so a refused connection is not an unhandled error —
      // the game simply is not running yet, which is expected.
    });

    socket.on('close', () => {
      this.#connected = false;
      this.#socket = null;

      // Fail anything in flight rather than leaving the model waiting on a dead socket.
      for (const [id, resolve] of this.#pending) {
        resolve({ id, ok: false, error: { code: 'disconnected' } });
      }
      this.#pending.clear();

      if (this.#closed) return;

      setTimeout(() => this.connect(), this.#reconnectDelay);
      this.#reconnectDelay = Math.min(this.#reconnectDelay * 2, 5000);
    });
  }

  close() {
    this.#closed = true;
    this.#socket?.destroy();
  }

  #onLine(line: string) {
    let msg: Response | BridgeEvent;

    try {
      msg = JSON.parse(line);
    } catch {
      return;
    }

    if ('event' in msg && typeof msg.event === 'string') {
      const ev = msg as BridgeEvent;

      // A cursor going backwards means the game client restarted and its counter reset. Anything
      // we held from the previous process is about a world that no longer exists.
      if (ev.cursor < this.#cursor) {
        this.#inbox = [];
      }

      this.#cursor = ev.cursor;
      this.#inbox.push(ev);

      // Bounded: a long session with no reads should not grow without limit.
      if (this.#inbox.length > 500) this.#inbox.splice(0, this.#inbox.length - 500);
      return;
    }

    const response = msg as Response;
    const resolve = this.#pending.get(response.id);

    if (resolve) {
      this.#pending.delete(response.id);
      resolve(response);
    }
  }

  /** Sends an op and waits for its reply. Throws BridgeError on a protocol-level failure. */
  async call<T = unknown>(op: string, params?: Record<string, unknown>, timeoutMs = 10_000): Promise<T> {
    if (!this.#connected || !this.#socket) {
      throw new BridgeError('not_connected', 'the modified SS14 client is not running');
    }

    const id = this.#nextId++;
    const socket = this.#socket;

    const response = await new Promise<Response>(resolve => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        resolve({ id, ok: false, error: { code: 'timeout' } });
      }, timeoutMs);

      this.#pending.set(id, r => {
        clearTimeout(timer);
        resolve(r);
      });

      socket.write(JSON.stringify({ id, op, params }) + '\n');
    });

    if (!response.ok) {
      throw new BridgeError(response.error.code, response.error.message);
    }

    return response.result as T;
  }

  /** Takes and clears buffered events. */
  drainEvents(): BridgeEvent[] {
    const out = this.#inbox;
    this.#inbox = [];
    return out;
  }
}
