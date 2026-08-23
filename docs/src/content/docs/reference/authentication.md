---
title: Authentication
description: How each cmdlet gets its credentials — the Anthropic SDK chain for Invoke-Agent, the agent's own account for Invoke-Acp.
---

The two cmdlets authenticate in completely different places.

## Invoke-Agent

`Invoke-Agent` resolves a credential in a fixed order and prints which step won as the first
transcript row:

```
- auth: OPEN_ROUTER_KEY in .env.local (via openrouter.ai) -> https://openrouter.ai/api
```

1. `-ApiKey`
2. `ANTHROPIC_API_KEY`, then `ANTHROPIC_AUTH_TOKEN`
3. a gateway key, when `-BaseUrl` names one: `OPENROUTER_API_KEY` or `OPEN_ROUTER_KEY`, from the
   environment or the workspace's `.env.local`
4. `ANTHROPIC_API_KEY` in the workspace's `.env.local`
5. an `anthropic` API key in another CLI's credential store
6. the SDK's own lookup, which ends at an `ant auth login` profile

An unset environment variable does not mean unauthenticated — a profile may still apply. Steps 3
to 5 are discovery; `-NoCredentialDiscovery` turns them off.

## Gateways

Any endpoint serving the Anthropic Messages API works. OpenRouter is verified end to end:

```powershell
Invoke-Agent "fix the bug in calc.py" `
  -BaseUrl 'https://openrouter.ai/api' `
  -Model 'anthropic/claude-sonnet-4.6'
```

Give the base **without** a version segment. The SDK appends `/v1/messages` itself, so
`https://openrouter.ai/api/v1` builds `/api/v1/v1/messages` and returns an HTML 404 that reads
like an outage rather than a typo.

## Whose credentials get used

Discovery reads API keys — a key you were issued, spending your quota as you intended. It does not
read sign-in tokens. A credential store also holds OAuth tokens that a *different* application
obtained by signing you in to a subscription; those are bound to that application's client
registration. ps-agent lists them by name so it can explain the skip, and never reads them:

```
Found API keys for deepseek, openrouter, which this cmdlet cannot use: it talks to the
Anthropic Messages API. Reach those providers with Invoke-Acp instead. Skipped openai
sign-in tokens: those were issued to another application, and are not ps-agent's to spend.
```

Put the key in `.env.local` and add `.env*` to `.gitignore`. A key in a tracked file is a key in
the history.

## Invoke-Acp

`Invoke-Acp` never talks to an LLM and holds no API key. The agent subprocess authenticates
itself against its own service — Claude Code against your Claude account, Gemini against yours.
What this client needs is only that the agent binary launches and answers the ACP handshake
within `-ConnectTimeoutSeconds` (default 60). If the launch fails you get:

```
Could not launch ACP agent 'npx'. Is it installed?
```

Pass `-ShowAgentLog` to surface the agent's stderr as transcript rows; that is where an agent
reports its own auth failures.

To check your setup end to end without credentials of any kind, run the stub agent used by the
test suite — `fixtures/stub-acp-agent.js` is a dependency-free Node script that performs the full
handshake, or use opencode, which is verified against version 1.18.18:

```powershell
Invoke-Acp "Read hello.txt and tell me the magic word." -Agent opencode -NoUi -AutoApprove
```
