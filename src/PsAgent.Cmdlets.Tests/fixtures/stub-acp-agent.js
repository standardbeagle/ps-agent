#!/usr/bin/env node
// A minimal, deterministic ACP agent used only by AcpClientIntegrationTests.
//
// It exists so the client's real path — spawn, newline-delimited JSON-RPC framing, the
// initialize/session.new/session.prompt handshake, streamed session/update notifications, and the
// agent-to-client callbacks (fs/read_text_file, session/request_permission) — is exercised against
// an actual subprocess, with no ACP agent installed, no network, and no API key.
//
// Deliberately dependency-free Node so it runs anywhere node does.

'use strict';

let buffer = '';

process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => {
  buffer += chunk;
  let index;
  while ((index = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, index).trim();
    buffer = buffer.slice(index + 1);
    if (line) {
      handle(JSON.parse(line));
    }
  }
});

function send(message) {
  process.stdout.write(JSON.stringify(message) + '\n');
}

function reply(id, result) {
  send({ jsonrpc: '2.0', id, result });
}

function notify(method, params) {
  send({ jsonrpc: '2.0', method, params });
}

// Requests we make of the client, keyed by id, so their replies can be resumed.
const pendingClientCalls = new Map();
let nextCallId = 1000;

function callClient(method, params) {
  return new Promise((resolve) => {
    const id = nextCallId++;
    pendingClientCalls.set(id, resolve);
    send({ jsonrpc: '2.0', id, method, params });
  });
}

function handle(message) {
  // A reply to something we asked the client for.
  if (message.method === undefined && message.id !== undefined) {
    const resolve = pendingClientCalls.get(message.id);
    if (resolve) {
      pendingClientCalls.delete(message.id);
      resolve(message);
    }
    return;
  }

  switch (message.method) {
    case 'initialize':
      reply(message.id, {
        protocolVersion: 1,
        agentInfo: { name: 'stub-agent', title: 'Stub', version: '9.9.9' },
        agentCapabilities: {
          loadSession: false,
          promptCapabilities: { image: false, audio: false, embeddedContext: false },
        },
        authMethods: [],
      });
      break;

    case 'session/new':
      reply(message.id, { sessionId: 'stub-session-1' });
      break;

    case 'session/prompt':
      runTurn(message);
      break;

    case 'session/cancel':
      // A notification; the in-flight prompt resolves as cancelled.
      cancelled = true;
      break;

    default:
      send({
        jsonrpc: '2.0',
        id: message.id,
        error: { code: -32601, message: 'stub agent does not implement ' + message.method },
      });
  }
}

let cancelled = false;

async function runTurn(message) {
  const sessionId = message.params.sessionId;
  const text = (message.params.prompt || []).map((b) => b.text || '').join('');

  const update = (u) => notify('session/update', { sessionId, update: u });

  update({ sessionUpdate: 'agent_thought_chunk', content: { type: 'text', text: 'considering ' } });
  update({ sessionUpdate: 'agent_thought_chunk', content: { type: 'text', text: 'the request' } });

  // Streamed prose, in chunks, so the client's coalescing is exercised.
  update({ sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: 'you said: ' } });
  update({ sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: text } });

  update({
    sessionUpdate: 'tool_call',
    toolCallId: 'call_1',
    title: 'Reading fixture.txt',
    kind: 'read',
    status: 'pending',
  });

  // Exercise the client's fs callback rather than reading the file ourselves.
  const read = await callClient('fs/read_text_file', { sessionId, path: 'fixture.txt' });
  const content = read.result ? read.result.content : 'ERROR:' + JSON.stringify(read.error);

  update({
    sessionUpdate: 'tool_call_update',
    toolCallId: 'call_1',
    status: 'completed',
    content: [{ type: 'content', content: { type: 'text', text: content } }],
  });

  // And the permission callback.
  const permission = await callClient('session/request_permission', {
    sessionId,
    toolCall: { toolCallId: 'call_2', title: 'Delete everything' },
    options: [
      { optionId: 'yes-always', name: 'Always allow', kind: 'allow_always' },
      { optionId: 'yes-once', name: 'Allow once', kind: 'allow_once' },
      { optionId: 'no', name: 'Reject', kind: 'reject_once' },
    ],
  });

  const outcome = permission.result ? permission.result.outcome : { outcome: 'error' };
  update({
    sessionUpdate: 'tool_call',
    toolCallId: 'call_2',
    title: 'permission outcome: ' + outcome.outcome + '/' + (outcome.optionId || 'none'),
    kind: 'other',
    status: outcome.outcome === 'selected' ? 'completed' : 'failed',
  });

  update({
    sessionUpdate: 'plan',
    entries: [
      { content: 'first step', status: 'completed' },
      { content: 'second step', status: 'pending' },
    ],
  });

  reply(message.id, { stopReason: cancelled ? 'cancelled' : 'end_turn' });
}
