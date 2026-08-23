---
title: ACP
description: The Agent Client Protocol as this repo speaks it — transport, method surface, capabilities, updates, permissions, shutdown.
---

ACP is the editor-to-agent protocol Zed defined: JSON-RPC over the agent's stdio. This repo is the
**client** half; `Invoke-Acp` is its front end.

## Transport

JSON-RPC 2.0 over the subprocess's stdin/stdout, framed as **one JSON value per line** (JSON
Lines) — not LSP's `Content-Length` headers. Getting this wrong hangs on `initialize` with nothing
logged.

Three properties of the read loop, each once a real hazard:

- **Never block on a pending response.** While we await `session/prompt`, the agent calls *us*
  back; inbound requests dispatch to their own task.
- **Skip unparseable lines.** Agents print banners and npm warnings to stdout before framing
  settles.
- **Fail every pending request when the stream closes.** Otherwise a caller awaits a response the
  dead agent can never send.

Writes are serialized so replies produced off the read loop cannot interleave mid-frame.

## Method surface

Client → agent:

| Method | Params | Result |
|---|---|---|
| `initialize` | `protocolVersion` (**integer** `1`), `clientCapabilities`, `clientInfo` | `protocolVersion`, `agentCapabilities`, `agentInfo`, `authMethods` |
| `session/new` | `cwd` (absolute), `mcpServers` | `sessionId` |
| `session/prompt` | `sessionId`, `prompt` (ContentBlock[]) | `stopReason` |
| `session/cancel` | `sessionId` | *(notification — no reply)* |

Agent → client (we serve these):

| Method | We do |
|---|---|
| `session/update` | *(notification)* fold into the transcript |
| `fs/read_text_file` | read, confined to the session cwd; honours optional `line` (1-based) / `limit` |
| `fs/write_text_file` | write, confined to the session cwd |
| `session/request_permission` | render a chooser, answer with the chosen `optionId` |

`protocolVersion` is an integer major version, not a string.

## Capabilities

```json
{ "fs": { "readTextFile": true, "writeTextFile": true }, "terminal": false }
```

`terminal: false` is deliberate: this client has no terminal service, and advertising a capability
you do not implement hangs the agent on a call that never returns. Adding terminal support means
implementing `terminal/*` in the request handler *and* flipping the flag, in that order.

## session/update

The discriminator is `sessionUpdate`, not `type`; a tool call's id is `toolCallId`, not `id`.

| `sessionUpdate` | Becomes |
|---|---|
| `agent_message_chunk` | Assistant row |
| `agent_thought_chunk` | Thought row |
| `user_message_chunk` | User row |
| `tool_call` | ToolCall row, keyed by `toolCallId` |
| `tool_call_update` | mutates that row in place |
| `plan` | Plan row with `[x]`/`[~]`/`[ ]` marks |
| anything else | ignored |

Chunks coalesce: consecutive chunks of the same kind append to the currently open row, so a
token-by-token stream is one row, not one row per token. Tool updates match back by `toolCallId`
and apply in place — appending instead would show every call twice; an update for a call never
seen is shown rather than dropped. Unknown kinds are ignored, not rejected, because ACP adds
variants over time and an unrecognised one is not a reason to break a live session.

## Permissions

The request carries the tool call and the agent's own options. The response outcome is a **nested
object**, the single most likely thing to get wrong:

```json
{ "outcome": { "outcome": "selected", "optionId": "allow-once" } }
```

or `{ "outcome": { "outcome": "cancelled" } }`. Flattening it to a bare string is treated as
malformed.

`optionId` strings are agent-specific; `kind` is protocol-defined (`allow_once`, `allow_always`,
`reject_once`, `reject_always`). `-AutoApprove` therefore matches on kind — preferring
`allow_always`, then `allow_once` — rather than guessing an id. Declining answers `cancelled`; it
never falls through to an allow.

## Confinement and shutdown

The `fs/*` callbacks resolve through `WorkspacePath.TryResolve` against the session `cwd`; a
refusal is a JSON-RPC error (`-32602`) so the agent sees it and can react. The agent is a separate
process with its own idea of what it may touch; this client enforces its own boundary rather than
trusting that one.

On shutdown, closing stdin is the graceful path — an agent that honours it flushes and exits. A
two-second wait, then `Kill(entireProcessTree: true)` as the backstop. The read and stderr pumps
are then awaited with a timeout so a loop wedged on a dead pipe cannot hang the cmdlet.

## Testing

`fixtures/stub-acp-agent.js` is a dependency-free Node agent that performs the full handshake,
streams chunked updates, calls back for `fs/read_text_file` and `session/request_permission`, and
reports a plan. `AcpClientIntegrationTests` drives the real client against it with no ACP agent
installed, no network and no API key. The whole client is additionally verified against a live
`opencode acp` 1.18.18 session — see [`Invoke-Acp`](/ps-agent/commands/invoke-acp/) for the exact
command.
