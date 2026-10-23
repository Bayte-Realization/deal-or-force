import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

function base64url(buffer) {
  return buffer.toString('base64')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');
}

const verifier = base64url(crypto.randomBytes(32));
const challenge = base64url(crypto.createHash('sha256').update(verifier).digest());

const clientId = 'FwMtsuWQONwhVHIj';
const redirectUri = 'http://localhost:8089/callback';

const authUrl = `https://clerk.creativeclaw.co/oauth/authorize?response_type=code&client_id=${clientId}&redirect_uri=${encodeURIComponent(redirectUri)}&scope=profile%20email&code_challenge=${challenge}&code_challenge_method=S256`;

console.log('=== CREATIVE CLAW AUTH URL ===');
console.log(authUrl);
console.log('==============================');

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost:8089');
  if (url.pathname === '/callback') {
    const code = url.searchParams.get('code');
    const error = url.searchParams.get('error');
    if (error) {
      res.writeHead(400, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(`<h1>Auth Error: ${error}</h1>`);
      console.error('OAuth error:', error);
      server.close();
      return;
    }

    if (!code) {
      res.writeHead(400, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end('<h1>No code provided</h1>');
      return;
    }

    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    res.end('<h1>Creative Claw успешно авторизован!</h1><p>Можете закрыть эту вкладку и вернуться в чат/IDE.</p>');

    try {
      console.log('Exchanging code for token...');
      const params = new URLSearchParams();
      params.append('grant_type', 'authorization_code');
      params.append('client_id', clientId);
      params.append('code', code);
      params.append('redirect_uri', redirectUri);
      params.append('code_verifier', verifier);

      const tokenRes = await fetch('https://clerk.creativeclaw.co/oauth/token', {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: params.toString()
      });

      const tokenData = await tokenRes.json();
      console.log('Token response:', JSON.stringify(tokenData, null, 2));

      if (tokenData.access_token) {
        const outDir = path.resolve('tmp');
        if (!fs.existsSync(outDir)) fs.mkdirSync(outDir, { recursive: true });
        const tokenFile = path.join(outDir, 'creative_claw_token.json');
        fs.writeFileSync(tokenFile, JSON.stringify(tokenData, null, 2));
        console.log(`Saved token to ${tokenFile}`);

        // Test calling Creative Claw MCP endpoint
        console.log('Testing Creative Claw MCP endpoint...');
        const mcpRes = await fetch('https://app.creativeclaw.co/mcp', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${tokenData.access_token}`
          },
          body: JSON.stringify({
            jsonrpc: '2.0',
            id: 1,
            method: 'tools/call',
            params: {
              name: 'get_credits_balance',
              arguments: {}
            }
          })
        });

        // The MCP endpoint answers with an SSE stream ("event: message\ndata: {...}"), not a
        // JSON body. Parsing it as JSON made a successful authorisation look like a failure.
        const mcpText = await mcpRes.text();
        const dataLine = mcpText.split(/\r?\n/).find((line) => line.startsWith('data:'));
        let mcpData = null;
        try {
          mcpData = dataLine ? JSON.parse(dataLine.slice(5).trim()) : JSON.parse(mcpText);
        } catch {
          mcpData = { raw: mcpText.slice(0, 400) };
        }
        console.log('Credits check result:', JSON.stringify(mcpData, null, 2));
      }
    } catch (err) {
      console.error('Error during token exchange:', err);
    } finally {
      setTimeout(() => server.close(), 1000);
    }
  } else {
    res.writeHead(404);
    res.end('Not found');
  }
});

server.listen(8089, () => {
  console.log('Listening on http://localhost:8089/callback');
});
