import fs from 'node:fs';

const tokenData = JSON.parse(fs.readFileSync('tmp/creative_claw_token.json', 'utf8'));
const token = tokenData.access_token;

console.log('Testing Creative Claw connection with token...');

async function callMcp(method, params = {}) {
  const res = await fetch('https://app.creativeclaw.co/mcp', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Accept': 'text/event-stream, application/json',
      'Authorization': `Bearer ${token}`
    },
    body: JSON.stringify({
      jsonrpc: '2.0',
      id: Date.now(),
      method,
      params
    })
  });

  const contentType = res.headers.get('content-type') || '';
  const text = await res.text();
  console.log(`HTTP ${res.status}, content-type: ${contentType}`);

  if (contentType.includes('text/event-stream')) {
    // Parse SSE lines
    const lines = text.split('\n');
    for (const line of lines) {
      if (line.startsWith('data: ')) {
        const jsonStr = line.slice(6).trim();
        try {
          const parsed = JSON.parse(jsonStr);
          console.log('SSE Data payload:', JSON.stringify(parsed, null, 2));
        } catch {
          console.log('SSE Data text:', jsonStr);
        }
      }
    }
  } else {
    try {
      console.log('JSON response:', JSON.stringify(JSON.parse(text), null, 2));
    } catch {
      console.log('Raw response:', text);
    }
  }
}

// 1. Initialize MCP
console.log('1. Initializing...');
await callMcp('initialize', {
  protocolVersion: '2024-11-05',
  capabilities: {},
  clientInfo: { name: 'DealOrForceClient', version: '1.0.0' }
});

// 2. Call get_credits_balance
console.log('2. Checking credits...');
await callMcp('tools/call', {
  name: 'get_credits_balance',
  arguments: {}
});
