# CODE_MAP — ps-agent structural index

Static top-of-context repo map. Evergreen: edit when a responsibility moves, not for detail
(detail lives in `docs/specs/*`). Keep small.

## Shape (one line)

Two cmdlets, one transcript model, one renderer.

```
Invoke-Agent → CodingAgent  → Anthropic Messages API ─┐
                            → AgentTools (fs + shell) │→ AgentEvent* → TranscriptView → Strata cascade
Invoke-Acp   → AcpClient    → JsonRpcConnection ──────┘                                 → Spectre frame
                            → AcpTranscript
```

`AgentEvent` is the join: both cmdlets produce it, the viewer renders it, and `ToPSObject()` puts
it on the pipeline in the shape `Show-Styled` reads unconfigured (`Name` + `class`).

## Files → role

| File | Owns |
|---|---|
| `InvokeAgentCommand.cs` | The `Invoke-Agent` parameter surface, the approval gate wiring, and the interactive-vs-headless decision. |
| `InvokeAcpCommand.cs` | The `Invoke-Acp` parameter surface, agent launch, session lifecycle, permission chooser. |
| `Agent/AgentEvent.cs` | The transcript row: kind, status, stylesheet class, glyph, and the `PSObject` projection. |
| `Agent/AgentTools.cs` | The four tools (read/list/edit/bash), the approval gate, and the shell spawn. |
| `Agent/ToolSpec.cs` | SDK-free tool description + `ToolOutcome`. |
| `Agent/WorkspacePath.cs` | Path confinement. **The security boundary** — pure, so it is testable. |
| `Agent/CodingAgent.cs` | The loop: request → tool calls → results → repeat. Also the system prompt. |
| `Acp/JsonRpcConnection.cs` | JSON-RPC 2.0 over a line-delimited stream. Bidirectional. Stream-based, so it is testable. |
| `Acp/AcpClient.cs` | ACP: handshake, session, prompt, and the `fs/*` + permission callbacks we serve. |
| `Acp/AcpTranscript.cs` | Folds `session/update` into `AgentEvent` rows (chunk coalescing, tool-call matching). |
| `Ui/TranscriptView.cs` | The alt-screen key loop, repaint, prompt line, and modal chooser. |
| `Ui/AgentNode.cs` | Mutable Strata tree node (stable identity + pseudo-state). |
| `Ui/AgentStyles.cs` | Stylesheet resolution: name → embedded, user override, inline CSS, or path. |
| `styles/agent.pcss` | The transcript sheet. |

## Where to find X

- **Add a tool the agent can call** → `AgentTools.Catalog` (the spec) + a branch in
  `AgentTools.Execute`. Mark it `Mutating` if it can change the machine; that is what routes it
  through the approval gate. Nothing in the emitter or the loop needs to change.
- **Change what a transcript row looks like** → `AgentEvent.Class` / `.Glyph` / `.Display`, then
  `styles/agent.pcss` for colour. Never hard-code colour in C#.
- **Handle a new ACP `sessionUpdate` kind** → the switch in `AcpTranscript.Apply`. Unknown kinds
  are ignored by design; add a case rather than a guard.
- **Serve a new ACP client capability** (terminal, elicitation) → advertise it in
  `AcpClient.InitializeAsync` **and** implement it in `HandleRequestAsync`. Advertising without
  implementing hangs the agent on a call that never returns.
- **Anything that touches a path from model or agent output** → `WorkspacePath.TryResolve`. There
  is no second path check anywhere; do not add one, route through this.
- **Spawn a process** → `BashRuntime.RunChildProcess` (from PsBash.Cmdlets): timeout + kill-tree.
  Raw `Process.Start` only for the ACP agent itself, which owns its own lifetime.

## Dependencies

- **ps-bash** (`../ps-bash/src/PsBash.Cmdlets`) — ProjectReference. Supplies `Show-Styled`,
  `StyledInteractiveSession`, and `BashRuntime.RunChildProcess`.
- **Strata** (`../strata/local-feed`) — the CSS cascade and Spectre projection. Not on nuget.org;
  the build auto-detects the feed and errors with the fix if it is missing.
- **Anthropic** (nuget) — the C# SDK, `Invoke-Agent` only.

## Testing

`src/PsAgent.Cmdlets.Tests` — every pure seam is unit-tested; `AcpClientIntegrationTests` drives
the real client against `fixtures/stub-acp-agent.js` (dependency-free Node), so the protocol wiring
is covered with no ACP agent, no network, and no API key. Those tests **skip visibly** when node is
absent rather than passing silently.
