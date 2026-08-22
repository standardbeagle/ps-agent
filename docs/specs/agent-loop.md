# The agent loop (`Invoke-Agent`)

What `CodingAgent` does, why it is shaped this way, and what it deliberately does not do.

## 1. The loop

```
user text
  └─ append to messages
     └─ loop, up to MaxTurns:
          Messages.Create(system + tools + messages)
          ├─ stop_reason == "refusal"  → report and stop
          ├─ project response blocks   → assistant echo + tool_use list
          ├─ no tool_use               → done
          └─ for each tool_use: approve → execute → tool_result
             append ALL results as ONE user message, repeat
```

That is the whole thing. The capability comes from the four tools, not from the orchestration —
which is the `pi` observation this is built on.

## 2. Why a manual loop, not `BetaToolRunner`

The SDK ships a tool runner that owns the loop. It is the right default, and it is wrong here: this
agent must interleave an **approval prompt** and a **transcript event** around every individual
tool call, and observe each result as it happens so the viewer can paint mid-turn. Owning the loop
is what makes that possible. The cost is about forty lines.

## 3. Why non-streaming

Each turn is one `Messages.Create`. Streaming would need the assistant echo — thinking blocks with
their signatures, and `tool_use` inputs accumulated from `input_json_delta` fragments —
reconstructed from the event stream. That reconstruction is the one place in this codebase where a
mistake corrupts a conversation *silently*: a mangled signature is rejected outright (loud), but a
subtly wrong tool input is not.

The visible cost is that prose does not appear token by token. The viewer mitigates it with a live
status row and a `⋯` busy marker, so a long turn never reads as a frozen terminal.

**If streaming is added later**, the shape is: `CreateStreaming` → accumulate per content-block
index on `content_block_start` / `content_block_delta` / `content_block_stop` → rebuild the same
`ContentBlockParam` list `Project` builds today, and assert byte-equality against the non-streaming
path on a recorded fixture before switching the default. `MaxTokens` should rise to ~64000 at the
same time; 16000 is chosen to stay inside the SDK's non-streaming HTTP timeout.

## 4. Request shape

| Field | Value | Why |
|---|---|---|
| `Model` | `claude-opus-5` | Default; `-Model` overrides. |
| `MaxTokens` | 16000 | The documented non-streaming default — room for a real edit, inside the HTTP timeout. |
| `Thinking` | `ThinkingConfigAdaptive { Display = Summarized }` | Adaptive is the only on-mode on current models. `Display` must be set explicitly: the default is `omitted`, which streams empty thinking text and makes the reasoning rows blank. |
| `OutputConfig.Effort` | `-EffortLevel`, default `high` | Nested under `OutputConfig`, not top-level. |
| `System` | one `TextBlockParam` with `CacheControl` | System + tools are the stable prefix; the breakpoint at its end means the growing history never invalidates it. |
| `Tools` | `AgentTools.Catalog` | A **static** list, so it renders byte-identically every turn — a tool list rebuilt per call would break the cache prefix. |

`budget_tokens` is not used: it is removed on current models and returns a 400.

## 5. Tools

Four, deliberately.

| Tool | Mutating | Notes |
|---|---|---|
| `read_file` | no | Clamped at 20k chars, with a truncation marker so the model knows. |
| `list_files` | no | Directories get a trailing `/`. |
| `edit_file` | **yes** | Exact-substring replace. |
| `run_bash` | **yes** | Through `BashRuntime.RunChildProcess`: concurrent drain, timeout, kill-tree. |

### `edit_file` is exactly-once by contract

`old_str` must appear exactly once. Replacing the first of several matches would edit a line the
model never looked at, and the result text gives it no way to notice — so ambiguity is a failure
with the occurrence count in the message, which is enough for the model to add context and retry.
An empty `old_str` on a missing file creates it; on an existing file, nothing.

### A non-zero exit is not a tool failure

`run_bash` returns `Ok` for exit 3. The compiler error is the *point* — the model is supposed to
read it and fix the cause. Only a killed (timed-out) run is reported as an error.

### Errors come back as results, not exceptions

A bad path, a missing file, a refused approval: all return a failed `ToolOutcome`, which becomes a
`tool_result` with `is_error`. Throwing would end the turn and lose the recovery.

## 6. Security

Two mechanisms, both in the executor, neither in the prompt.

**Path confinement** — `WorkspacePath.TryResolve` resolves against the workspace root, collapses
`..`, and requires a directory-separator boundary (so `repo-secrets` is not inside `repo`). Every
file operand goes through it. Comparison is ordinal on Linux, case-insensitive elsewhere.

**The approval gate** — `edit_file` and `run_bash` call `ApprovalGate` with a description of
exactly what is about to happen. In the viewer that is a modal chooser. With `-AutoApprove` it is
`AllowAll`. **Without a terminal and without `-AutoApprove` it returns false**, because a prompt
that cannot be shown cannot be answered, and proceeding would grant the permission the gate exists
to check.

## 7. Output

Every step emits an `AgentEvent` as it happens. Interactively those go to the viewer; under `-NoUi`
they are queued and written to the pipeline from the cmdlet thread (`WriteObject` is only legal
there). Either way the same rows are produced, so the interactive and scripted views of a session
agree.
