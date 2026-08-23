#!/usr/bin/env node
// A minimal OAuth 2.0 authorization server, used to exercise Connect-Agent's real flow end to end
// without a provider account: PKCE verification, the state echo, the code exchange, and refresh.
//
//   node stub-oauth-provider.js <port>
//
// GET  /authorize  -> 302 back to redirect_uri with ?code&state (records the code_challenge)
// POST /token      -> authorization_code: verifies S256(code_verifier) == recorded challenge
//                     refresh_token:      issues a new access token, returns no new refresh token
//                                         (the common provider behaviour that callers get wrong)

'use strict';

const http = require('http');
const crypto = require('crypto');
const { URL } = require('url');

const port = Number(process.argv[2] || 9099);
const codes = new Map();          // code -> { challenge, redirectUri }
const refreshTokens = new Set();

const b64url = (buf) => buf.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

function json(res, status, body) {
  const text = JSON.stringify(body);
  res.writeHead(status, { 'content-type': 'application/json', 'content-length': Buffer.byteLength(text) });
  res.end(text);
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, `http://localhost:${port}`);

  if (url.pathname === '/authorize') {
    const challenge = url.searchParams.get('code_challenge');
    const method = url.searchParams.get('code_challenge_method');
    const redirectUri = url.searchParams.get('redirect_uri');
    const state = url.searchParams.get('state');

    if (method !== 'S256' || !challenge || !redirectUri) {
      return json(res, 400, { error: 'invalid_request', error_description: 'missing PKCE parameters' });
    }

    const code = b64url(crypto.randomBytes(16));
    codes.set(code, { challenge, redirectUri });

    const back = new URL(redirectUri);
    back.searchParams.set('code', code);
    if (state) back.searchParams.set('state', state);
    res.writeHead(302, { location: back.toString() });
    return res.end();
  }

  if (url.pathname === '/token' && req.method === 'POST') {
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', () => {
      const form = new URLSearchParams(body);
      const grant = form.get('grant_type');

      if (grant === 'authorization_code') {
        const record = codes.get(form.get('code'));
        if (!record) {
          return json(res, 400, { error: 'invalid_grant', error_description: 'unknown code' });
        }
        codes.delete(form.get('code'));

        // The point of PKCE: the code is only redeemable by whoever generated the verifier.
        const verifier = form.get('code_verifier') || '';
        const computed = b64url(crypto.createHash('sha256').update(verifier, 'ascii').digest());
        if (computed !== record.challenge) {
          return json(res, 400, { error: 'invalid_grant', error_description: 'PKCE verification failed' });
        }

        const refresh = 'refresh-' + b64url(crypto.randomBytes(8));
        refreshTokens.add(refresh);
        return json(res, 200, {
          access_token: 'access-first',
          refresh_token: refresh,
          token_type: 'bearer',
          expires_in: Number(process.env.STUB_EXPIRES_IN || 3600),
        });
      }

      if (grant === 'refresh_token') {
        if (!refreshTokens.has(form.get('refresh_token'))) {
          return json(res, 400, { error: 'invalid_grant', error_description: 'unknown refresh token' });
        }
        // Deliberately omits refresh_token, as most providers do on refresh.
        return json(res, 200, { access_token: 'access-refreshed', token_type: 'bearer', expires_in: 3600 });
      }

      return json(res, 400, { error: 'unsupported_grant_type' });
    });
    return;
  }

  json(res, 404, { error: 'not_found' });
});

server.listen(port, '127.0.0.1', () => {
  process.stdout.write(`stub-oauth listening on ${port}\n`);
});
