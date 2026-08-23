---
title: The Transcript
description: One AgentEvent row type, one viewer, one stylesheet — the shared currency of both cmdlets.
---

Redirected output gives you the transcript as objects, which is the same data the viewer draws:

```powershell
Invoke-Agent "audit the error paths" -NoUi | Where-Object Kind -eq ToolCall
Invoke-Agent "audit the error paths" -NoUi | Show-Styled ./src/PsAgent.Cmdlets/styles/agent.pcss
```

## One row type

Both cmdlets emit `PsAgent.AgentEvent` rows. `Invoke-Agent` produces them from the Anthropic
Messages API; `Invoke-Acp` folds ACP `session/update` notifications into the same shape. Each row
carries:

| Property | Content |
|---|---|
| `Name` | The rendered summary line: glyph + one-line title. What `Show-Styled` shows collapsed. |
| `class` | Stylesheet classes the row selects on, e.g. `tool ok`, `thought muted`. |
| `Kind` | `User`, `Thought`, `Assistant`, `ToolCall`, `ToolResult`, `Plan`, `Status` or `Error`. |
| `Title` / `Body` | The collapsed one-line summary / the expanded detail (tool arguments, command output, full message text). |
| `Tool` / `ToolCallId` | Tool name, and the id correlating a `ToolCall` with its `ToolResult`. |
| `Status` | `None`, `Pending`, `InProgress`, `Completed` or `Failed`. |
| `Timestamp` | When the row was created (UTC). |

Every row also carries a glyph, so the transcript reads under `NO_COLOR` or in a pipe:

| Kind | Glyph |
|---|---|
| User | `›` |
| Thought | `·` |
| Assistant | `●` |
| Plan | `☰` |
| Status | `—` |
| Error | `✘` |
| ToolCall | `⚙` |
| ToolResult | `✔` (`✘` when the tool failed) |

## The viewer

On a real terminal, both cmdlets open a live transcript on the alternate screen:

```
ps-agent · claude-opus-5 · C:\work\core\ps-bash
› fix the off-by-one in Window()
· considering the two clamp branches
⚙ read_file src/…/TranscriptView.cs
✔ read src/…/TranscriptView.cs (9214 chars)
⚙ edit_file src/…/TranscriptView.cs
✔ edit src/…/TranscriptView.cs
⚙ $ dotnet test --filter Window -> exit 0
● Fixed. The clamp used total-window as an inclusive bound; …

[7/7] done · 18422 in / 1204 out tokens
↑↓/jk move · Enter expand · i prompt · Esc/q quit
```

| Key | Does |
|---|---|
| `↑` `↓` / `k` `j` | Move the cursor. Moving off the newest row stops the view chasing new output. |
| `Enter` / `Space` | Expand the focused row — full message, tool arguments, command output. |
| `g` / `G` | Jump to the oldest / newest row. |
| `PageUp` / `PageDown` | Same as `g` / `G`. |
| `i` or `/` | Type the next prompt. |
| `Esc` | Interrupt the running turn; quit when idle. |
| `q` | Quit. |

The approval and permission prompts are modal choosers on the same screen: `↑`/`↓` to pick,
`Enter` to confirm, `Esc` to cancel, or a digit `1`–`9` to select directly.

One quirk worth knowing: the viewer forces UTF-8 output while it owns the screen. Every row
marker is beyond Latin-1, so a console on a legacy code page would otherwise replace them with
`?` — and CP1252 is worse than obvious breakage, because `›`, `·` and `—` survive there while the
tool and assistant markers vanish.

## Styling

Colour lives in `styles/agent.pcss`, never in C#. Rows carry a `class`; the sheet decides what it
looks like. The sheet's own mapping:

```css
.user      { color: brightwhite; font-weight: bold }
.thought   { color: gray }
.plan      { color: brightmagenta }
.tool          { color: brightblue }
.tool.running  { color: brightyellow }
.tool.ok       { color: brightgreen }
.tool.error    { color: brightred;    font-weight: bold }
.error { color: brightred;    font-weight: bold }
:focused { color: black; background: brightcyan }
```

To re-theme without rebuilding, drop an `agent.pcss` in `~/.config/ps-agent/styles/`, or pass
`-Css` a name, an inline rule, or a `.pcss` path.

Piping rows into `Show-Styled` works unconfigured — they carry the `Name` and `class` properties
it reads by default — but it auto-picks ps-bash's generic `object` sheet, since it does not know
this module's kind. Point it at the agent sheet to get the transcript colours:

```powershell
Invoke-Agent "..." -NoUi | Show-Styled ./src/PsAgent.Cmdlets/styles/agent.pcss
```

The command references have the full parameter sets: [`Invoke-Agent`](/ps-agent/commands/invoke-agent/),
[`Invoke-Acp`](/ps-agent/commands/invoke-acp/).
