import fs from 'node:fs';
import { execSync } from 'node:child_process';

// Media delivery for 2026-10-16: two music companions and four interface cues, generated
// through Creative Claw with the project token, converted locally to the game's formats.
// Prompts follow the atlas provenance style: real materials, close and dry, nothing game-y.

const mcpJson = JSON.parse(fs.readFileSync('.mcp.json', 'utf8'));
const token = mcpJson.mcpServers['creative-claw'].headers.Authorization.replace('Bearer ', '');
const OUT = '.fennara/media-source-20261016';
fs.mkdirSync(OUT, { recursive: true });

async function call(method, params = {}, id = 1) {
  const res = await fetch('https://app.creativeclaw.co/mcp', { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream', 'Authorization': 'Bearer ' + token },
    body: JSON.stringify({ jsonrpc: '2.0', id, method, params }) });
  const text = await res.text();
  for (const line of text.split('\n')) if (line.startsWith('data: ')) { try { return JSON.parse(line.slice(6)); } catch {} }
  try { return JSON.parse(text); } catch { return { raw: text.slice(0, 400) }; }
}

function unwrap(result) {
  const text = result?.content?.map(c => c.text).join('\n') ?? '';
  const url = /https?:\/\/[^\s"']+\.(mp3|wav|ogg)/.exec(text)?.[0];
  return { text, url };
}

async function generate(name, tool, args) {
  console.log(`[${name}] generating...`);
  const response = await call('tools/call', { name: tool, arguments: args }, Math.floor(Math.random() * 1e6));
  if (response.error) { console.log(`[${name}] ERROR`, JSON.stringify(response.error).slice(0, 300)); return null; }
  const { text, url } = unwrap(response.result ?? {});
  if (!url) { console.log(`[${name}] no url in:`, text.slice(0, 300)); return null; }
  const ext = url.endsWith('.wav') ? 'wav' : 'mp3';
  const file = `${OUT}/${name}.${ext}`;
  execSync(`curl -sL "${url}" -o "${file}"`, { stdio: 'pipe' });
  console.log(`[${name}] downloaded ${file} (${fs.statSync(file).size} bytes)`);
  return { name, url, file, prompt: args.prompt, tool };
}

const jobs = [
  await generate('statecraft_papers', 'generate_music', {
    prompt: 'Slow, restrained chamber piece for a diplomacy desk late at night: felt-muted piano repeating a patient two-note question, a distant string quartet breathing long quiet chords, one dry woodwind line entering late like a clerk reading terms aloud. No percussion at all, no swelling crescendos, no vocals, no electronic elements. Tempo stays under 70, dynamics stay conversational, the ending is a quiet unresolved chord that simply stops, as if the meeting adjourned. Warm close-miked room, generous silence between phrases, low listening fatigue. About three minutes, instrumental only.',
    model: 'music/lyria-3.5'
  }),
  await generate('western_wind', 'generate_music', {
    prompt: 'Calm, wide-open instrumental for a study window overlooking farmland and coast at dawn: nylon-string guitar playing slow unhurried arpeggios like passing weather, a soft brushed wood Keeping lazy time far in the background, a warm analog pad underneath like distant fields, one melody that rises once and settles for good. Gentle optimism without fanfare - a good honest year, not a triumph. No drum kit, no vocals, no synthesizer leads. Around 80 bpm, sparse arrangement, long natural tail, ends on a resolved warm chord that fades by itself. About three minutes, instrumental only.',
    model: 'music/lyria-3.5'
  }),
  await generate('promise_seal', 'generate_sound_effect', {
    prompt: 'A single wax seal pressed into a letter on a wooden desk: soft paper settle, the seal\'s firm short press, one clean lift. Close dry Foley, modest warmth, quiet state-room feel, one motion only. No voice, no music, no reverb tail, no stamping boom.',
    model: 'sfx/elevenlabs-sound-v2', duration_seconds: 3
  }),
  await generate('goal_pinned', 'generate_sound_effect', {
    prompt: 'A pencil makes one short deliberate tick on a paper ledger, then the pencil is set down softly on the wood. Close, dry, handwritten and human, tiny graphite grain, clean ending. No pen click, no voice, no music.',
    model: 'sfx/elevenlabs-sound-v2', duration_seconds: 3
  }),
  await generate('goal_complete', 'generate_sound_effect', {
    prompt: 'A small ledger closes with a soft felt thump and one gentle wooden knock, like a clerk finishing a page at day\'s end. Close, warm, restrained, quietly satisfied but not celebratory. No fanfare, no chime sparkle, no voice, no music.',
    model: 'sfx/elevenlabs-sound-v2', duration_seconds: 4
  }),
  await generate('diplomacy_page', 'generate_sound_effect', {
    prompt: 'One thin diplomatic letter is unfolded once and laid flat on a wooden desk, paper fibres whispering, ending with a tiny brass corner pressed flat by two fingers. Close dry Foley, unhurried, no voice, no music, no long rustling sequence.',
    model: 'sfx/elevenlabs-sound-v2', duration_seconds: 3
  })
];

const done = jobs.filter(Boolean);
fs.writeFileSync(`${OUT}/provenance.json`, JSON.stringify({ generated: done, at: new Date().toISOString() }, null, 2));
console.log(`done: ${done.length}/${jobs.length}`);

// Convert: music to ogg (the game's track format), cues to wav (the cue format).
for (const job of done) {
  try {
    if (job.name === 'statecraft_papers' || job.name === 'western_wind') {
      execSync(`ffmpeg -y -i "${job.file}" -ac 2 -ar 44100 -c:a libvorbis -q:a 5 "game/music/audio/${job.name}.ogg"`, { stdio: 'pipe' });
      console.log(`[convert] game/music/audio/${job.name}.ogg`);
    } else {
      execSync(`ffmpeg -y -i "${job.file}" -ac 1 -ar 44100 -c:a pcm_s16le "game/sound/audio/atlas/${job.name}.wav"`, { stdio: 'pipe' });
      console.log(`[convert] game/sound/audio/atlas/${job.name}.wav`);
    }
  } catch (error) { console.log(`[convert] FAILED ${job.name}: ${String(error).slice(0, 200)}`); }
}
