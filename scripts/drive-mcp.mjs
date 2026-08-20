// Drives the mailkit-agent MCP server over stdio for one tool call, then exits.
// Each invocation is a SEPARATE server process, deliberately reproducing hosts
// that restart the server between calls.
//
// Usage:
//   node scripts/drive-mcp.mjs <toolName> '<jsonArguments>' [serverDll]
// Example:
//   node scripts/drive-mcp.mjs send_prepare '{"request":{"account_id":"sales26",...}}'
//
// The server defaults to the repository's Debug build; pass an explicit DLL path
// to drive a published server instead. The data directory is whatever the server
// resolves (PLUGIN_DATA / MAILKIT_AGENT_DATA_DIR / %LOCALAPPDATA%\MailKit.Agent).
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const defaultServerDll = path.join(
  scriptDir, '..', 'src', 'MailKit.Agent.Mcp', 'bin', 'Debug', 'net8.0', 'mailkit-agent.dll');

const [toolName, argsJson, serverDllArg] = process.argv.slice(2);
if (!toolName) {
  console.error('usage: node scripts/drive-mcp.mjs <toolName> <jsonArguments> [serverDll]');
  process.exit(2);
}
const serverDll = serverDllArg ?? defaultServerDll;
let arguments_;
try {
  arguments_ = argsJson ? JSON.parse(argsJson) : {};
} catch (error) {
  console.error('invalid arguments JSON: ' + error.message);
  process.exit(2);
}

const child = spawn('dotnet', [serverDll], {
  stdio: ['pipe', 'pipe', 'pipe'],
  env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' },
});

let buffer = '';
const pending = new Map();
let nextId = 1;
child.stdout.setEncoding('utf8');
child.stdout.on('data', (chunk) => {
  buffer += chunk;
  let index;
  while ((index = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, index).trim();
    buffer = buffer.slice(index + 1);
    if (!line) continue;
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      console.error('NON-JSON stdout: ' + line);
      continue;
    }
    if (message.id !== undefined && pending.has(message.id)) {
      pending.get(message.id)(message);
      pending.delete(message.id);
    }
  }
});

let stderrText = '';
child.stderr.setEncoding('utf8');
child.stderr.on('data', (chunk) => (stderrText += chunk));

function send(message) {
  child.stdin.write(JSON.stringify(message) + '\n');
}

function request(method, params) {
  const id = nextId++;
  return new Promise((resolve) => {
    pending.set(id, resolve);
    send({ jsonrpc: '2.0', id, method, params });
  });
}

const timeout = setTimeout(() => {
  console.error('TIMEOUT waiting for MCP response');
  child.kill();
  process.exit(3);
}, 120_000);

const init = await request('initialize', {
  protocolVersion: '2025-06-18',
  capabilities: {},
  clientInfo: { name: 'sales26-verify', version: '1.0' },
});
if (!init.result) {
  console.error('initialize failed: ' + JSON.stringify(init));
  process.exit(3);
}
send({ jsonrpc: '2.0', method: 'notifications/initialized' });

const call = await request('tools/call', { name: toolName, arguments: arguments_ });
clearTimeout(timeout);
console.log(JSON.stringify(call, null, 2));
child.stdin.end();
await new Promise((resolve) => child.on('exit', resolve));
if (stderrText.trim()) {
  console.error('--- server stderr ---');
  console.error(stderrText);
}
