---
title: Getting Started
description: Clone the three sibling repos, pack the Strata feed, build, and run your first agent session.
---

You end up with the `PsAgent` module imported and a live agent in your terminal:

```powershell
Import-Module ./src/PsAgent.Cmdlets/bin/Release/net10.0/PsAgent.psd1
Invoke-Acp "Read hello.txt and tell me the magic word." -Agent opencode -NoUi -AutoApprove
```

## 1. Clone the siblings

This repo builds against two siblings by **relative path**. Clone all three next to each other:

```bash
git clone https://github.com/standardbeagle/ps-bash
git clone https://github.com/standardbeagle/strata
git clone https://github.com/standardbeagle/ps-agent
```

```
core/
├── ps-bash/     ← ProjectReference: Show-Styled, BashRuntime.RunChildProcess
├── strata/      ← the CSS cascade, consumed from strata/local-feed
└── ps-agent/    ← this repo
```

## 2. Pack the Strata feed

Strata is not on nuget.org, so its local feed has to be packed first:

```bash
cd ../strata && ./scripts/pack-local.sh      # populates ../strata/local-feed
```

The build auto-detects the feed and the newest `Strata.Css` nupkg version, so a strata bump needs
no edit in this repo. A missing feed fails the build with the `pack-local.sh` instruction rather
than a wall of `CS0246`.

## 3. Build

```bash
cd ../ps-agent && dotnet build -c Release -m:1
```

`-m:1` is not optional. This repo ProjectReferences `../ps-bash/src/PsBash.Cmdlets`, so a build
here writes into ps-bash's shared `src/*/bin`; two concurrent builds against those outputs are the
documented root cause of ps-bash's test-suite flakiness.

## 4. Import and run

```powershell
Import-Module ./src/PsAgent.Cmdlets/bin/Release/net10.0/PsAgent.psd1

# Needs Anthropic credentials — see Authentication.
Invoke-Agent "why does the parser drop trailing newlines?"

# Needs an ACP agent installed. opencode is the easiest: no npx download.
Invoke-Acp "Read hello.txt and tell me the magic word." -Agent opencode -NoUi -AutoApprove
```

`Invoke-Acp` is verified end-to-end against **opencode 1.18.18**. The known `-Agent` names shell
out through `npx`:

| `-Agent` | Runs |
|---|---|
| `claude` | `npx -y @zed-industries/claude-code-acp` |
| `gemini` | `npx -y @google/gemini-cli --experimental-acp` |
| `codex` | `npx -y @zed-industries/codex-acp` |
| `opencode` | `opencode acp` — ACP is a subcommand of its own binary |

Anything else is treated as an executable on `PATH` that speaks ACP on stdio:

```powershell
Invoke-Acp "..." -Agent my-agent -ArgumentList '--stdio'
```

## Run the tests

```bash
dotnet build -c Debug -m:1
dotnet test src/PsAgent.Cmdlets.Tests --no-build
```

The ACP client has an end-to-end suite against `fixtures/stub-acp-agent.js`, a dependency-free
Node agent, so the handshake, framing, streamed updates and agent-to-client callbacks are covered
with no ACP agent installed, no network and no API key. Those tests self-skip when Node is absent.

Next: [the transcript model](/ps-agent/transcript/) both cmdlets write to, or the full parameter
sets for [`Invoke-Agent`](/ps-agent/commands/invoke-agent/) and
[`Invoke-Acp`](/ps-agent/commands/invoke-acp/).
