---
title: Invoke-Acp
description: An Agent Client Protocol client — drive Claude Code, Gemini, Codex or opencode from the terminal. Full parameter reference.
---

```powershell
Invoke-Acp -Agent claude "add a regression test for the trailing-newline bug"
```

Alias: `acp`.

The cmdlet launches the agent as a subprocess, negotiates capabilities, opens a session rooted at
the working directory, and then both streams the agent's `session/update` notifications into the
[viewer](/ps-agent/transcript/) and answers the callbacks the agent makes back — file reads and
writes, and permission requests, which surface as a chooser. The agent runs as a separate process
with its own model, its own key and its own tools; nothing here talks to an LLM.

## Parameters

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `Prompt` | `string` | — | Position 0, accepts pipeline input. What to ask. Omitted on a terminal, the viewer opens empty and waits for `i`. |
| `Agent` | `string` | `claude` | A known name (`claude`, `claude-code-acp`, `gemini`, `codex`, `opencode`) or an executable on `PATH` that speaks ACP on stdio. |
| `ArgumentList` | `string[]` | — | Alias `Args`. Extra arguments appended to the agent's command line. |
| `WorkspaceRoot` | `string` | current location | Aliases `Root`, `Path`. Directory the session is rooted at. |
| `AutoApprove` | `switch` | off | Answer every permission request with the agent's first allow-shaped option. |
| `NoUi` | `switch` | off | Skip the viewer and write transcript objects to the pipeline. |
| `ShowAgentLog` | `switch` | off | Show the agent's stderr as transcript rows. Useful when a launch fails. |
| `ConnectTimeoutSeconds` | `int` | `60` | `ValidateRange(1, 600)`. Seconds to wait for `initialize` before giving up on the agent. |
| `Css` | `string` | built-in `agent` sheet | Aliases `Style`, `Stylesheet`. Stylesheet name, inline CSS, or a `.pcss` path. |

## Agent presets

| `-Agent` | Runs |
|---|---|
| `claude`, `claude-code-acp` | `npx -y @zed-industries/claude-code-acp` |
| `gemini` | `npx -y @google/gemini-cli --experimental-acp` |
| `codex` | `npx -y @zed-industries/codex-acp` |
| `opencode` | `opencode acp` |

Matching is case-insensitive. Any other name is launched as-is with your `ArgumentList`:

```powershell
Invoke-Acp "..." -Agent my-agent -ArgumentList '--stdio'
```

## Permissions and `-AutoApprove`

When the agent sends `session/request_permission`, the chooser renders the agent's own options in
its own order; `Esc` answers `cancelled`. `-AutoApprove` does not guess an option id — `optionId`
strings are agent-specific, so it matches on the protocol-defined `kind`, preferring
`allow_always`, then `allow_once`, then the first option. Declining always answers `cancelled`; it
never falls through to an allow. Without an interactive view, an unset handler means every request
is cancelled, which is the safe default for a non-interactive run.

:::caution[Known gaps]
The permission chooser is covered only by the stub agent. opencode never sent
`session/request_permission` in any run, so that branch has not been exercised against a real
agent. ACP terminal callbacks are not implemented either — the client advertises
`terminal: false`, so agents that would use it fall back to their own execution.
:::

## Verified behaviour

Verified end-to-end against **opencode 1.18.18**:

```powershell
Invoke-Acp "Read hello.txt and tell me the magic word." -Agent opencode -NoUi -AutoApprove
```

One cosmetic quirk: opencode sends tool-call titles with the drive letter stripped
(`Users\andyb\...` rather than `C:\Users\andyb\...`). That is the agent's own text, passed through
verbatim — it looks like a bug here and is not.

The wire details — framing, capabilities, the nested permission outcome, confinement of the
`fs/*` callbacks — are on the [ACP reference page](/ps-agent/reference/acp/).
