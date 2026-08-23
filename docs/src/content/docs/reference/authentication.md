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

## Signing in with a browser

`Connect-Agent` runs an authorization-code flow with PKCE (RFC 7636) and a loopback redirect
(RFC 8252): ps-agent starts a listener on `localhost`, opens the provider's consent page, and
catches the redirect. The code never transits a clipboard, and no client secret ships with the
module -- the flow proves possession of a one-time verifier instead.

```powershell
Connect-Agent -ShowExample          # the profile shape, and where to put it
Connect-Agent -Provider my-provider # opens the browser, stores the token
Connect-Agent -List                 # what is configured, and what is signed in
Disconnect-Agent my-provider        # forget the token, keep the profile
```

Then `Invoke-Agent` uses it. With one sign-in stored it is picked up automatically; with several,
name one with `-OAuthProvider`. The token refreshes itself when it is within two minutes of
expiry, and the transcript says so.

```
- auth: my-provider sign-in, valid until 2026-08-23 17:21:32Z -> https://api.example.com
```

A provider is described by a JSON profile in `~/.config/ps-agent/oauth/<name>.json`:

```json
{
  "name": "my-provider",
  "authorizeUrl": "https://auth.example.com/oauth/authorize",
  "tokenUrl": "https://auth.example.com/oauth/token",
  "clientId": "<the client id the provider issued to your application>",
  "scopes": ["openid", "offline_access"],
  "redirectPort": 1455,
  "redirectPath": "/auth/callback",
  "baseUrl": "https://api.example.com",
  "extraHeaders": {}
}
```

The `clientId` is configuration rather than something built in, because a public OAuth client is
an identity a provider issues to a named application. ps-agent ships the mechanism; you supply the
identity you are entitled to use. `extraHeaders` covers providers that require something alongside
a bearer token -- Anthropic's OAuth path wants `anthropic-beta: oauth-2025-04-20`, and without it
the token is rejected as though it were invalid.

Tokens are stored beside the profile as `<name>.token.json`, chmod 600 on POSIX.

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
