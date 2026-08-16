// Drives the MCP server the way a real client does: spawn it, list tools, call them.
//   node test-mcp.ts

import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const transport = new StdioClientTransport({
  command: 'node',
  args: ['src/index.ts'],
});

const client = new Client({ name: 'test-harness', version: '0.0.1' });
await client.connect(transport);

const { tools } = await client.listTools();
console.log(`tools (${tools.length}):`);
for (const t of tools) {
  const args = Object.keys(t.inputSchema?.properties ?? {});
  console.log(`  ${t.name}(${args.join(', ')})`);
}

async function call(name: string, args: Record<string, unknown> = {}) {
  const r = await client.callTool({ name, arguments: args });
  const text = (r.content as { type: string; text?: string }[])
    .map(c => c.text ?? '')
    .join('\n');
  console.log(`\n=== ${name}(${JSON.stringify(args)}) ===`);
  console.log(text);
  return text;
}

await call('bridge_status');
await call('observe');
await call('read_events');

// Only meaningful when the game is up; harmless otherwise.
const looked = await call('look', { radius: 5 });

const id = looked.match(/\[(e:\d+)\]/)?.[1];
if (id) {
  await call('examine', { entity: id });
  await call('tell_human', { text: 'Copilot connected via MCP. Reading your situation.' });
  await call('notify', { text: 'MCP bridge online.', urgency: 'low' });
  await call('observe');
}

await client.close();
process.exit(0);
