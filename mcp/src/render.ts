// Turns bridge payloads into prose an agent can read cheaply.
//
// The raw snapshot is ~3 KB of JSON. Handing that over verbatim on every call would dominate the
// context window and bury the two or three facts that actually matter. So: prose first, numbers
// only where they carry information, and the map only when the layout is the point.

export type Snapshot = {
  cursor: number;
  self: {
    entity: string;
    name: string;
    state: string;
    alerts: string[];
    map: string;
    grid: string | null;
    tile: [number, number] | null;
    mapPosition: [number, number];
  };
  hands: { hand: string; active: boolean; item?: string; name?: string }[];
  entities: { name: string; count: number; tiles: ([number, number] | null)[]; ids: string[] }[];
  tiles: {
    centre: [number, number];
    radius: number;
    legend: Record<string, string>;
    rows: string[];
    notable: { tile: [number, number]; kind: string; kelvin?: number; state?: number }[];
  } | null;
};

/** Alerts worth leading with. Everything else is ambient and gets mentioned only in passing. */
const URGENT = new Set([
  'Weightless', 'LowPressure', 'HighPressure', 'Hot', 'Cold',
  'SuitPower', 'Internals', 'Crit', 'Dead', 'Starving', 'Dehydrated',
]);

export function renderSnapshot(s: Snapshot, opts: { map?: boolean } = {}): string {
  const lines: string[] = [];
  const self = s.self;

  const where = self.tile ? `tile ${self.tile[0]},${self.tile[1]}` : 'off-grid';
  lines.push(`You are ${self.name} — ${self.state}, at ${where}.`);

  // Hazards first. If the station is on fire, that goes above the furniture inventory.
  if (s.tiles?.notable.length) {
    const byKind = new Map<string, number>();
    for (const n of s.tiles.notable) byKind.set(n.kind, (byKind.get(n.kind) ?? 0) + 1);

    const parts = [...byKind].map(([kind, n]) => `${n} ${kind}`);
    lines.push(`HAZARD: ${parts.join(', ')} within ${s.tiles.radius} tiles.`);

    for (const n of s.tiles.notable.slice(0, 6)) {
      const k = n.kelvin !== undefined ? ` (${n.kelvin}K)` : '';
      lines.push(`  ${n.kind} at ${n.tile[0]},${n.tile[1]}${k}`);
    }
  }

  const urgent = self.alerts.filter(a => URGENT.has(a));
  if (urgent.length) lines.push(`Alerts: ${urgent.join(', ')}.`);

  const held = s.hands.filter(h => h.name);
  lines.push(
    held.length
      ? `Holding: ${held.map(h => `${h.name} (${h.hand}${h.active ? ', active' : ''})`).join(', ')}.`
      : 'Both hands empty.',
  );

  // Structure is already in the map; repeating it as an entity list is pure noise.
  const structural = /^(solid wall|reinforced wall|window|reinforced window|grille|plating|catwalk)$/i;
  const interesting = s.entities.filter(e => !structural.test(e.name));

  if (interesting.length) {
    lines.push('');
    lines.push('Nearby:');
    for (const e of interesting) {
      const at = e.tiles
        .filter(Boolean)
        .slice(0, 4)
        .map(t => `${t![0]},${t![1]}`)
        .join(' ');
      const more = e.count > 4 ? ` +${e.count - 4} more` : '';
      lines.push(`  ${e.name}${e.count > 1 ? ` x${e.count}` : ''} @ ${at}${more}  [${e.ids[0]}]`);
    }
  }

  const hidden = s.entities.length - interesting.length;
  if (hidden > 0) lines.push(`  (${hidden} structural entity types omitted; see the map)`);

  if (opts.map && s.tiles) {
    lines.push('');
    lines.push(`Map, centred on you at ${s.tiles.centre[0]},${s.tiles.centre[1]} (north is up):`);
    for (const row of s.tiles.rows) lines.push('  ' + row);
    lines.push('  . breathable   # wall   V vacuum   F fire   H hot   C cold   ? unknown   @ you');
  }

  return lines.join('\n');
}

/**
 * What changed between two snapshots.
 *
 * Returns null when nothing meaningful moved, so the caller can say "no change" in four words
 * instead of re-sending the world.
 */
export function renderDelta(prev: Snapshot, next: Snapshot): string | null {
  const lines: string[] = [];

  if (prev.self.state !== next.self.state) {
    lines.push(`You are now ${next.self.state}.`);
  }

  const before = new Set(prev.self.alerts);
  const after = new Set(next.self.alerts);

  for (const a of after) if (!before.has(a)) lines.push(`Alert started: ${a}.`);
  for (const a of before) if (!after.has(a)) lines.push(`Alert cleared: ${a}.`);

  const pt = prev.self.tile;
  const nt = next.self.tile;
  if (pt && nt && (pt[0] !== nt[0] || pt[1] !== nt[1])) {
    const dx = nt[0] - pt[0];
    const dy = nt[1] - pt[1];
    lines.push(`You moved ${Math.abs(dx)} east/west, ${Math.abs(dy)} north/south to ${nt[0]},${nt[1]}.`);
  }

  const prevHeld = prev.hands.map(h => h.name ?? '-').join('|');
  const nextHeld = next.hands.map(h => h.name ?? '-').join('|');
  if (prevHeld !== nextHeld) {
    const held = next.hands.filter(h => h.name);
    lines.push(held.length ? `Now holding: ${held.map(h => h.name).join(', ')}.` : 'Hands now empty.');
  }

  const prevNames = new Set(prev.entities.map(e => e.name));
  const nextNames = new Set(next.entities.map(e => e.name));

  const appeared = [...nextNames].filter(n => !prevNames.has(n));
  const gone = [...prevNames].filter(n => !nextNames.has(n));

  if (appeared.length) lines.push(`Came into view: ${appeared.slice(0, 10).join(', ')}.`);
  if (gone.length) lines.push(`Left view: ${gone.slice(0, 10).join(', ')}.`);

  // Hazards are always worth restating, even if the set is unchanged — "still on fire" matters.
  const nHaz = next.tiles?.notable.length ?? 0;
  const pHaz = prev.tiles?.notable.length ?? 0;
  if (nHaz > 0) {
    lines.push(`HAZARD: ${nHaz} notable tile(s) nearby${nHaz > pHaz ? ' — worsening' : ''}.`);
  } else if (pHaz > 0) {
    lines.push('Hazards nearby have cleared.');
  }

  return lines.length ? lines.join('\n') : null;
}

export function renderEvents(events: { event: string; cursor: number; [k: string]: unknown }[]): string {
  if (!events.length) return 'Nothing new.';

  return events
    .map(e => {
      switch (e.event) {
        case 'alert':
          return `[${e.cursor}] alert ${e.alert} ${e.state}`;
        case 'human':
          return `[${e.cursor}] the human says: ${e.text}`;
        case 'chat':
          return `[${e.cursor}] ${e.channel ? `(${e.channel}) ` : ''}${e.speaker}: ${e.text}`;
        default:
          return `[${e.cursor}] ${e.event} ${JSON.stringify(e)}`;
      }
    })
    .join('\n');
}
