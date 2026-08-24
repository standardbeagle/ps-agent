---
title: Invoke-Agent
description: A minimal coding agent — a model, four tools, and a loop. Full parameter reference.
---

```powershell
Invoke-Agent "why does the parser drop trailing newlines?"
```

Aliases: `agent`, `pia`.

On a terminal it opens the [transcript viewer](/ps-agent/transcript/); redirected or under
`-NoUi` it writes `PsAgent.AgentEvent` objects to the pipeline. With no prompt at all, the viewer
opens empty and waits for `i`.

## Parameters

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `Prompt` | `string` | — | Position 0, accepts pipeline input. What to ask. |
| `WorkspaceRoot` | `string` | current location | Aliases `Root`, `Path`. The directory the agent may read, write and run commands in. |
| `Model` | `string` | `claude-opus-5` | Model id. |
| `MaxTokens` | `int` | `16000` | `ValidateRange(256, 128000)`. Per-response output ceiling; 16000 is chosen to stay inside the SDK's non-streaming HTTP timeout. |
| `EffortLevel` | `string` | `high` | `ValidateSet("low", "medium", "high", "max")`, case-insensitive. Reasoning effort; lower is cheaper and terser. |
| `MaxTurns` | `int` | `40` | `ValidateRange(1, 500)`. Hard ceiling on model round-trips in one turn. |
| `CommandTimeoutSeconds` | `int` | `120` | `ValidateRange(1, 3600)`. Seconds a single `run_bash` call may take before its process tree is killed. |
| `AutoApprove` | `switch` | off | Run edits and commands without asking. |
| `NoUi` | `switch` | off | Skip the viewer and write transcript objects to the pipeline. |
| `NoThinking` | `switch` | off | Do not show the model's reasoning rows. |
| `Css` | `string` | built-in `agent` sheet | Aliases `Style`, `Stylesheet`. Stylesheet name, inline CSS, or a `.pcss` path. |
| `SystemPrompt` | `string` | built-in | Replace the built-in system prompt. |
| `ApiKey` | `string` | resolved | The key to authenticate with. See [Authentication](/ps-agent/reference/authentication/). |
| `BaseUrl` | `string` | Anthropic | Alias `Endpoint`. A gateway serving the Anthropic Messages API, without a version segment. |
| `NoCredentialDiscovery` | `switch` | off | Do not read credentials from CLI stores or `.env.local`. |
| `OAuthProvider` | `string` | auto | Alias `OAuthProfile`. Use a browser sign-in stored by `Connect-Agent`. |
| `Api` | `string` | `auto` | `ValidateSet("auto", "anthropic", "openai")`. Which wire format the endpoint speaks. |

A typical invocation:

```powershell
Invoke-Agent "..." `
  -WorkspaceRoot ./src `
  -Model claude-opus-5 `
  -EffortLevel medium `
  -MaxTurns 40 `
  -CommandTimeoutSeconds 120 `
  -AutoApprove `
  -NoThinking `
  -NoUi
```

## The loop

Each turn appends the prompt to the message list and then, up to `MaxTurns` times: create one
`Messages.Create` request (system prompt + static tool catalog + history), stop on a refusal, echo
the response blocks into the transcript, and execute any `tool_use` calls — each mutating one
through the approval gate first. All `tool_result` blocks for one round-trip travel back in **one**
user message.

The loop is **non-streaming**: each turn is one request, which keeps the assistant echo (thinking
signatures, `tool_use` inputs) exact. Prose does not appear token by token; the viewer shows a live
status row and a `⋯` busy marker so a long turn does not read as frozen.

The tool catalog is a static list, byte-identical across turns, and the system prompt carries a
cache breakpoint at its end — together they keep the stable prefix cacheable as the history grows.

## Authentication and failure behaviour

The cmdlet constructs a zero-argument `AnthropicClient`: the SDK resolves `ANTHROPIC_API_KEY`,
then `ANTHROPIC_AUTH_TOKEN`, then an `ant auth login` profile. An unset environment variable does
not mean unauthenticated. A client that cannot be constructed at all fails with an
`AuthenticationError` terminating error.

A verified end-to-end run, against `anthropic/claude-sonnet-4.6` through OpenRouter, fixing a
one-line bug in a Python file:

```
- auth: OPEN_ROUTER_KEY in .env.local (via openrouter.ai) -> https://openrouter.ai/api
> Read calc.py, fix the bug in add(), then run it to prove the fix. Keep it short.
· Let me read the file first.
⚙ read_file calc.py          ✔ read calc.py (33 chars)
● Bug is clear: `-` should be `+`. Fixing it now.
⚙ edit_file calc.py          ✔ edit calc.py
⚙ run_bash python -c "..."   ✔ exit 0
● Fixed. add() was using `-` instead of `+`.
- done · 5802 in / 332 out tokens
```

That covers the tool loop, the assistant echo, the `tool_result` round-trip and four model
round-trips in one turn.

:::caution[Known gap]
`run_bash` spawns `ps-bash` when it is on PATH, and the `ps-bash-host` daemon that starts outlives
the command. It has twice kept a handle on the workspace directory, so deleting the workspace
afterwards failed. The command itself completes normally.
:::

## Safety in one paragraph

Every path operand is confined to `WorkspaceRoot` by `WorkspacePath.TryResolve`; `../../.ssh/id_rsa`
is refused by the executor, not the prompt. `edit_file` and `run_bash` ask before they run —
a modal chooser in the viewer with *Allow once* / *Allow everything this session* / *Refuse*.
"Allow everything this session" currently behaves as a single allow; `-AutoApprove` is the durable
form. Without a terminal and without `-AutoApprove`, mutating tools are refused outright: a prompt
that cannot be shown cannot be answered. Details: [Tools & Safety](/ps-agent/reference/tools-and-safety/).
