---
title: Tools & Safety
description: The four tools Invoke-Agent gives the model, and the two checks every call passes through.
---

`Invoke-Agent` gives the model exactly four tools. The capability comes from the tools, not from
orchestration.

| Tool | Mutating | Behaviour |
|---|---|---|
| `read_file` | no | Reads a text file relative to the workspace root. Output clamped at 20,000 chars with a truncation marker so the model knows. |
| `list_files` | no | Lists a directory (default: the root). Directory names end with a trailing `/`. |
| `edit_file` | **yes** | Exact-substring replace: `old_str` must appear **exactly once** and differ from `new_str`. An empty `old_str` on a missing file creates it. |
| `run_bash` | **yes** | Runs a shell command in the workspace root, returning stdout, stderr and exit code. Not interactive. |

## The two checks

Both live in the executor. Neither lives in the prompt — a system prompt is a request, not a
boundary.

**Path confinement.** Every path operand resolves through `WorkspacePath.TryResolve` against
`-WorkspaceRoot` (default: the current location). It collapses `..` and requires a
directory-separator boundary, so `repo-secrets` is not inside `repo`; comparison is ordinal on
Linux, case-insensitive elsewhere. An escaping path comes back as a failed tool result:

```
Refused: '../../.ssh/id_rsa' resolves outside the workspace root.
```

There is one path check in the codebase and this is it.

**The approval gate.** `edit_file` and `run_bash` describe exactly what is about to happen
(`edit src/Foo.cs (replace 84 chars with 91)`, or the command line itself) and ask first. In the
viewer the gate is a modal chooser:

- **Allow once**
- **Allow everything this session** — currently behaves as a single allow; `-AutoApprove` is the
  durable form (known gap)
- **Refuse**

With `-AutoApprove` the gate permits everything. Without a terminal and without `-AutoApprove`
the gate returns false: a prompt that cannot be shown cannot be answered, and a silent yes is the
one default a tool that edits files and runs commands must not have.

## Commands are bounded

`run_bash` spawns through `BashRuntime.RunChildProcess`, which drains stdout and stderr
concurrently and kills the whole process tree on `-CommandTimeoutSeconds` (default 120) — the
difference between a wedged command and a wedged session. Output is clamped at 20,000 chars so one
runaway command cannot fill the context window.

The shell is picked at runtime: `ps-bash` when it is on `PATH` (a real bash front end on Windows
with no MSYS install), otherwise `cmd.exe /c` on Windows or `/bin/sh -c` elsewhere.

Two error-handling choices to know before reading results:

- **A non-zero exit is not a tool failure.** `run_bash` returns success for exit 3; the compiler
  error is the point — the model is supposed to read it and fix the cause. Only a killed
  (timed-out) run is reported as an error, with `[timed out after 120s - process tree killed]`
  appended.
- **Errors come back as results, not exceptions.** A bad path, a missing file, a refused approval:
  all become a failed `tool_result` the model can read and recover from. Throwing would end the
  turn and lose the recovery.

`Invoke-Acp` applies the same path confinement to the `fs/read_text_file` and
`fs/write_text_file` callbacks the agent makes — it is a separate process, and its idea of what it
may touch is not this client's. See the [ACP reference](/ps-agent/reference/acp/).
