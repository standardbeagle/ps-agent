# ps-agent project instructions

**Navigation: read @CODE_MAP.md first** — the static structural index.

## What this is

Two cmdlets sharing one transcript model and one renderer:

- `Invoke-Agent` — a minimal coding agent (Anthropic Messages API, four tools, a manual loop).
- `Invoke-Acp` — an Agent Client Protocol **client** that drives someone else's agent as a subprocess.

Both produce `PsAgent.AgentEvent` rows. That shared currency is the design; keep it.

## Build

```bash
dotnet build -c Debug -m:1          # ALWAYS -m:1
dotnet test src/PsAgent.Cmdlets.Tests --no-build
```

**`-m:1` is not optional.** This repo ProjectReferences `../ps-bash/src/PsBash.Cmdlets`, so a build
here writes into **ps-bash's shared `src/*/bin`**. Two concurrent builds against those outputs are
the documented root cause of ps-bash's suite flakiness (a half-written test bin cannot start a
runspace). Before building, check `tman ls` in ps-bash for a live run and wait it out — `tman`
serializes ps-bash's own builds but knows nothing about this repo.

## Strata is required

`../strata/local-feed` must exist (`cd ../strata && ./scripts/pack-local.sh`). The version is
auto-detected from the newest `Strata.Css` nupkg, so a strata bump needs no edit here. A missing
feed fails the build with that instruction rather than a wall of `CS0246`.

Unlike ps-bash — where Strata is optional and the styled cmdlets compile out — Strata is
**mandatory** here: the stylesheet and the viewer *are* this module's UI.

## Invariants

1. **Every path from model or agent output goes through `WorkspacePath.TryResolve`.** It is the
   security boundary. The system prompt is a request, not a boundary; do not move the check there,
   and do not add a second one elsewhere.
2. **Mutating tools ask before they run.** Without a terminal and without `-AutoApprove` they are
   refused. A silent yes is the one default a tool that edits files and runs commands must not have.
3. **Colour lives in `styles/agent.pcss`, never in C#.** Rows carry a `class`; the sheet decides
   what it looks like. Every row also carries a glyph so the transcript reads under `NO_COLOR`.
4. **Emit `Name` and `class` properties.** Those are what `Show-Styled` reads by default; changing
   them silently breaks `Invoke-Agent -NoUi | Show-Styled` with no error.
5. **Never advertise an ACP capability you do not implement.** An agent that takes you up on
   `terminal: true` hangs on a call that never returns.

## SDK gotchas (Anthropic C#)

- There is **no `.ToParam()`**: response `ContentBlock`s must be rebuilt as `*Param` by hand
  (`CodingAgent.Project`). Thinking blocks must carry their `Signature` verbatim — the API rejects
  a tampered one.
- **Check `StopReason == "refusal"` before reading content.** A refusal is an HTTP 200 with nothing
  usable in it.
- **All `tool_result` blocks for one turn travel in ONE user message.** Splitting them across
  messages trains the model out of parallel tool calls.
- `budget_tokens` is removed on current models — use `Thinking = new ThinkingConfigAdaptive()` and
  `OutputConfig.Effort`.
- When a type name is unknown, **write the code and let the compiler name it**. The XML doc file
  beside the DLL does not carry undocumented members, and a reflection probe costs more than a
  compile.

## Testing

Pure seams are extracted specifically so they can be tested without a runspace, a network, or a
key: `WorkspacePath`, `AgentTools`, `AcpTranscript`, `JsonRpcConnection` (over in-memory streams),
`TranscriptView.Decide` / `.Window`.

`AcpClientIntegrationTests` spawns `fixtures/stub-acp-agent.js`. When adding protocol behaviour,
extend the stub — an assertion against a real subprocess is worth more than another unit test of a
shape you also wrote.

**A test that returns early when its fixture is missing is indistinguishable from one that ran.**
Use `[SkippableFact]` + `Skip.If`, so the run reports it.
