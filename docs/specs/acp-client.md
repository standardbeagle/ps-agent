# The ACP client (`Invoke-Acp`)

How this repo speaks the [Agent Client Protocol](https://agentclientprotocol.com), and the wire
details that are easy to get wrong in ways that produce a *silently dead session* rather than an
error.

## 1. Transport

JSON-RPC 2.0 over the agent subprocess's stdin/stdout, framed as **one JSON value per line**
(JSON Lines) — **not** LSP's `Content-Length` headers. Getting this wrong hangs on `initialize`
with nothing logged.

`JsonRpcConnection` takes a `TextReader`/`TextWriter` pair rather than a `Process`, so the whole
protocol layer is drivable from in-memory streams in a test.

Three properties the read loop must have, each of which was a real hazard:

- **Never block on a pending response.** While we await `session/prompt`, the agent calls *us* back.
  Inbound requests are dispatched to their own task so the loop stays free to receive the next frame.
- **Skip unparseable lines.** Agents print banners and npm warnings to stdout before framing
  settles. Treating the first bad line as fatal makes those agents unusable.
- **Fail every pending request when the stream closes.** Otherwise a caller awaits a response the
  dead agent can never send.

Writes are serialized: replies to inbound requests are produced off the read loop and would
otherwise interleave mid-frame.

## 2. Method surface

**Client → agent**

| Method | Params | Result |
|---|---|---|
| `initialize` | `protocolVersion` (**integer** `1`), `clientCapabilities`, `clientInfo` | `protocolVersion`, `agentCapabilities`, `agentInfo`, `authMethods` |
| `session/new` | `cwd` (absolute), `mcpServers` | `sessionId` |
| `session/prompt` | `sessionId`, `prompt` (ContentBlock[]) | `stopReason` |
| `session/cancel` | `sessionId` | *(notification — no reply)* |

**Agent → client** (we serve these)

| Method | We do |
|---|---|
| `session/update` | *(notification)* fold into the transcript |
| `fs/read_text_file` | read, confined to the session cwd; honours optional `line`/`limit` |
| `fs/write_text_file` | write, confined to the session cwd |
| `session/request_permission` | render a chooser, answer with the chosen `optionId` |

`protocolVersion` is an **integer major version**, not a string.

## 3. Capabilities

We advertise:

```json
{ "fs": { "readTextFile": true, "writeTextFile": true }, "terminal": false }
```

`terminal: false` is deliberate and load-bearing: this client has no terminal service, and
**advertising a capability you do not implement hangs the agent** on a call that never returns.
Adding terminal support means implementing `terminal/*` in `HandleRequestAsync` *and* flipping the
flag, in that order.

## 4. `session/update`

The discriminator is `sessionUpdate`, **not** `type` — and a tool call's id is `toolCallId`, **not**
`id`. `AcpTranscript` folds these into `AgentEvent` rows.

| `sessionUpdate` | Becomes |
|---|---|
| `agent_message_chunk` | Assistant row |
| `agent_thought_chunk` | Thought row |
| `user_message_chunk` | User row |
| `tool_call` | ToolCall row, keyed by `toolCallId` |
| `tool_call_update` | mutates that row in place |
| `plan` | Plan row with `[x]`/`[~]`/`[ ]` marks |
| anything else | ignored |

Two behaviours make this more than a switch:

**Chunks coalesce.** Message and thought updates arrive as many small fragments; one row per chunk
would be one row per token. Consecutive chunks of the same kind append to the currently open row.
Any other event closes it.

**Tool updates match back.** A call is reported twice — `tool_call` on start, `tool_call_update` as
it progresses — so updates are matched by `toolCallId` and applied in place. Appending instead
shows every call twice. An update for a call we never saw start is *shown* rather than dropped.

**Unknown kinds are ignored, not rejected.** ACP adds variants over time (`usage_update`,
`current_mode_update`, …); an unrecognised one is not a reason to break a live session.

## 5. Permissions

The request carries the tool call and the agent's own options. The response outcome is a **nested
object**, which is the single most likely thing to get wrong:

```json
{ "outcome": { "outcome": "selected", "optionId": "allow-once" } }
```

or `{ "outcome": { "outcome": "cancelled" } }`. Flattening it to a bare string is treated as
malformed.

`optionId` strings are agent-specific; `kind` is protocol-defined
(`allow_once` / `allow_always` / `reject_once` / `reject_always`). `-AutoApprove` therefore matches
on **kind** — preferring `allow_always`, then `allow_once` — rather than guessing an id. Declining
answers `cancelled`; it never falls through to an allow.

## 6. Confinement

The `fs/*` callbacks resolve through `WorkspacePath.TryResolve` against the session `cwd`. The
agent is a separate process with its own idea of what it may touch; this client enforces its own
boundary rather than trusting that one. A refusal is a JSON-RPC error (`-32602`), so the agent sees
it and can react.

## 7. Shutdown

Closing stdin is the graceful shutdown — an agent that honours it flushes and exits. A two-second
wait, then `Kill(entireProcessTree: true)` as the backstop. The read and stderr pumps are then
awaited with a timeout so a loop wedged on a dead pipe cannot hang the cmdlet.

## 8. Testing

`fixtures/stub-acp-agent.js` is a dependency-free Node agent that performs the full handshake,
streams chunked updates, calls back for `fs/read_text_file` and `session/request_permission`, and
reports a plan. `AcpClientIntegrationTests` drives the real client against it — covering framing,
handshake, notifications arriving while a request is outstanding, the agent calling back mid-turn,
and path confinement — with no ACP agent installed, no network, and no API key.

**Extend the stub when adding protocol behaviour.** An assertion against a real subprocess is worth
more than another unit test of a shape you also wrote.
