import fs from 'node:fs';
import path from 'node:path';

const flagsDir = path.join('game', 'gfx', 'flags');
const flagFiles = fs.readdirSync(flagsDir).filter(f => f.startsWith('flag_') && f.endsWith('.png'));
const flagSet = new Set(flagFiles.map(f => f.slice(0, -4).toLowerCase()));
console.log('Total flag PNGs:', flagSet.size);

// All tags in ownership.csv
const ownershipRaw = fs.readFileSync(path.join('game', 'history', 'provinces', 'ownership.csv'), 'utf8');
const ownerTags = new Set([...ownershipRaw.matchAll(/;([A-Z0-9]{3})(?:;|$)/gm)].map(m => m[1]));
const missingOwners = [...ownerTags].filter(t => !flagSet.has('flag_' + t.toLowerCase()));
console.log('Missing province owner flags (' + missingOwners.length + '):', missingOwners.join(', '));

// All tags in history/countries/2024/00_countries.json
const historyRaw = fs.readFileSync(path.join('game', 'history', 'countries', '2024', '00_countries.json'), 'utf8');
const historyTags = new Set([...historyRaw.matchAll(/^\s*"([A-Z0-9]{3})":/gm)].map(m => m[1]));
const missingHistory = [...historyTags].filter(t => !flagSet.has('flag_' + t.toLowerCase()));
console.log('Missing history tags flags (' + missingHistory.length + '):', missingHistory.join(', '));

// Check movements with tags and check their ideology variants
const ideologies = ['communism', 'fascism', 'theocracy', 'absolute_monarchy'];
const movementsDir = path.join('game', 'common', 'movements');
let movementTags = new Set();
for (const f of fs.readdirSync(movementsDir)) {
  if (!f.endsWith('.json')) continue;
  const content = fs.readFileSync(path.join(movementsDir, f), 'utf8');
  for (const m of content.matchAll(/"tag":\s*"([A-Za-z0-9]{3})"/g)) {
    movementTags.add(m[1].toUpperCase());
  }
}
console.log('Total movement tags:', movementTags.size);
const missingMovementsBase = [...movementTags].filter(t => !flagSet.has('flag_' + t.toLowerCase()));
console.log('Missing movement base flags (' + missingMovementsBase.length + '):', missingMovementsBase.join(', '));

let missingMovementIdeology = [];
for (const tag of movementTags) {
  const tLow = tag.toLowerCase();
  for (const ideo of ideologies) {
    const key = `flag_${tLow}_${ideo}`;
    if (!flagSet.has(key)) {
      missingMovementIdeology.push(key);
    }
  }
}
console.log('Movement ideology variants missing (' + missingMovementIdeology.length + ')');
if (missingMovementIdeology.length > 0 && missingMovementIdeology.length <= 40) {
  console.log(missingMovementIdeology.join(', '));
} else if (missingMovementIdeology.length > 40) {
  console.log(missingMovementIdeology.slice(0, 30).join(', ') + ' ... and ' + (missingMovementIdeology.length - 30) + ' more');
}

// Check real-world movements specifically: CMA, SDF, HOU, HZB, PFS, NRF, AFC, STC, BDF, ICH, MJL, etc.
const realWorldTags = ['CMA', 'SDF', 'HOU', 'HZB', 'PFS', 'NRF', 'AFC', 'STC', 'BDF', 'ICH', 'MJL', 'PKK', 'BLA', 'TTP', 'OLA', 'FAN', 'TPL', 'IPB', 'ULM', 'WUC', 'ARS', 'ELN', 'TPN'];
let missingRealWorldVariants = [];
for (const tag of realWorldTags) {
  const tLow = tag.toLowerCase();
  for (const ideo of ideologies) {
    const key = `flag_${tLow}_${ideo}`;
    if (!flagSet.has(key)) {
      missingRealWorldVariants.push(key);
    }
  }
}
console.log('Real world movement missing ideology variants (' + missingRealWorldVariants.length + '):', missingRealWorldVariants.join(', '));

// Check countries ideology variants
const countriesRaw = fs.readFileSync(path.join('game', 'common', 'countries', '00_countries.json'), 'utf8');
const countryTags = [...countriesRaw.matchAll(/^\s*"([A-Z0-9]{3})":/gm)].map(m => m[1]);
let missingByCountry = {};
for (const tag of countryTags) {
  const tLow = tag.toLowerCase();
  for (const ideo of ideologies) {
    const key = 'flag_' + tLow + '_' + ideo;
    if (!flagSet.has(key)) {
      if (!missingByCountry[tag]) missingByCountry[tag] = [];
      missingByCountry[tag].push(ideo);
    }
  }
}
let totalMissing = 0;
let countsByIdeo = { communism: 0, fascism: 0, theocracy: 0, absolute_monarchy: 0 };
for (const tag of countryTags) {
  const tLow = tag.toLowerCase();
  for (const ideo of ideologies) {
    if (!flagSet.has('flag_' + tLow + '_' + ideo)) {
      totalMissing++;
      countsByIdeo[ideo]++;
    }
  }
}
console.log('Total country-ideology flags missing:', totalMissing, 'out of', countryTags.length * 4);
console.log('Breakdown by ideology:', JSON.stringify(countsByIdeo));

// Check all tags in 21_countries_by_tag.json
const locEn = fs.readFileSync('game/localization/english/21_countries_by_tag.json', 'utf8');
const countriesInLoc = [...locEn.matchAll(/"country\.([A-Z0-9]{3})\.name"/g)].map(m => m[1]);
const missingFromLoc = countriesInLoc.filter(t => !flagSet.has('flag_' + t.toLowerCase()));
console.log('Countries in 21_countries_by_tag.json missing base flag (' + missingFromLoc.length + '):', missingFromLoc.join(', '));

// Check formable nations flags:
// Some formables have flag_formable_id, but some may have alternative tags or IDs
const formablesPath = path.join('game', 'common', 'formable_nations');
const formablesAll = [];
for (const f of fs.readdirSync(formablesPath)) {
  if (!f.endsWith('.json')) continue;
  const content = fs.readFileSync(path.join(formablesPath, f), 'utf8');
  for (const m of content.matchAll(/"([a-z0-9_]+)":\s*\{/g)) {
    formablesAll.push(m[1]);
  }
}
const missingFormableFlags = formablesAll.filter(id => !flagSet.has('flag_formable_' + id.toLowerCase()) && !flagSet.has('flag_' + id.toLowerCase()));
console.log('Formables missing flag (' + missingFormableFlags.length + '):', missingFormableFlags.join(', '));
