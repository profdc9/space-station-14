// Phase 3 check: does in-game chat reach the copilot, with the right channel?
//   node test-chat.ts
//
// Speak in-game while this runs (T for local, or a radio prefix like ";" or ":e").

import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const transport = new StdioClientTransport({ command: 'node', args: ['src/index.ts'] });
const client = new Client({ name: 'chat-test', version: '0.0.1' });
await client.connect(transport);

async function call(name: string, args: Record<string, unknown> = {}) {
  const r = await client.callTool({ name, arguments: args });
  return (r.content as { text?: string }[]).map(c => c.text ?? '').join('\n');
}

console.log(await call('bridge_status'));

await call('tell_human', {
  text: 'Chat test running. Say something in local chat (T), or over radio, in the next 45 seconds.',
});
await call('notify', { text: 'Speak in-game now — testing chat capture.', urgency: 'high' });

console.log('\nwaiting up to 45s for you to speak in-game...\n');

const heard = await call('wait_for', { kind: 'chat', timeout: 45 });
console.log(heard);

console.log('\n--- everything recorded this session ---');
console.log(await call('read_events', { cursor: 0 }));

await client.close();
process.exit(0);
