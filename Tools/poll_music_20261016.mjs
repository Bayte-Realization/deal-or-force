import fs from 'node:fs';
import { execSync } from 'node:child_process';

const mcpJson = JSON.parse(fs.readFileSync('.mcp.json', 'utf8'));
const token = mcpJson.mcpServers['creative-claw'].headers.Authorization.replace('Bearer ', '');
const jobs = [
  ['statecraft_papers', '905d3e52-1725-450c-bc16-4fa22ee46b2a'],
  ['western_wind', '396d7b16-93b3-4ce5-a933-11c248543e3d']
];

async function call(method, params = {}) {
  const res = await fetch('https://app.creativeclaw.co/mcp', { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream', 'Authorization': 'Bearer ' + token },
    body: JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) });
  const text = await res.text();
  for (const line of text.split('\n')) if (line.startsWith('data: ')) { try { return JSON.parse(line.slice(6)); } catch {} }
  try { return JSON.parse(text); } catch { return { raw: text.slice(0, 300) }; }
}

for (var round = 0; round < 40; round++) {
  let pending = 0;
  for (const [name, jobId] of jobs) {
    if (fs.existsSync(`.fennara/media-source-20261016/${name}.mp3`)) continue;
    const response = await call('tools/call', { name: 'check_job', arguments: { job_id: jobId } });
    const text = response.result?.content?.map(c => c.text).join('\n') ?? JSON.stringify(response).slice(0, 200);
    const url = /https?:\/\/[^\s"']+\.(mp3|wav)/.exec(text)?.[0];
    if (url) {
      const file = `.fennara/media-source-20261016/${name}.mp3`;
      execSync(`curl -sL "${url}" -o "${file}"`, { stdio: 'pipe' });
      console.log(`[${name}] downloaded (${fs.statSync(file).size} bytes)`);
      execSync(`ffmpeg -y -i "${file}" -ac 2 -ar 44100 -c:a libvorbis -q:a 5 "game/music/audio/${name}.ogg"`, { stdio: 'pipe' });
      console.log(`[${name}] converted to game/music/audio/${name}.ogg`);
    } else { pending++; console.log(`[${name}] waiting: ${text.replace(/\n/g, ' ').slice(0, 90)}`); }
  }
  if (!pending) { console.log('all music delivered'); break; }
  await new Promise(done => setTimeout(done, 30000));
}
