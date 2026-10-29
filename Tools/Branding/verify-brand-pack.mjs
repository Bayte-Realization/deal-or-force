import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {spawnSync} from 'node:child_process';
const root=path.resolve(import.meta.dirname,'../..');
const kit=path.resolve(process.argv[2]||'D:/Godot Projects/Bayte Realization Brand Kit/2026-10-27');
let passed=0,failed=0;
function check(ok,name){console.log(`${ok?'PASS':'FAIL'} branding: ${name}`);if(ok)passed++;else failed++;}
const hash=bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
const manifest=JSON.parse(fs.readFileSync(path.join(kit,'MANIFEST.json'),'utf8'));
for(const file of manifest.files){
  const full=path.resolve(kit,file.path);
  check(full.startsWith(kit+path.sep)&&fs.existsSync(full)&&fs.statSync(full).size===file.bytes&&hash(fs.readFileSync(full))===file.sha256,`manifest ${file.path}`);
  if(file.path.endsWith('.svg')){
    const svg=fs.readFileSync(full,'utf8');
    check(svg.includes('<path')&&!/<image\b|data:image|href=/i.test(svg),`outlined vector ${file.path}`);
  }
}
for(const [runtime,file]of [['icon.ico','02_Deal_or_Force/deal_or_force.ico'],['icon_small.png','02_Deal_or_Force/deal_or_force_icon_128.png'],['game/gfx/interface/brand_logo_banner.png','02_Deal_or_Force/deal_or_force_parchment.png'],['game/gfx/interface/branding/studio_logo_dark.png','01_Studio/bayte_realization_on_dark.png']])
  check(hash(fs.readFileSync(path.join(root,runtime)))===hash(fs.readFileSync(path.join(kit,file))),`runtime matches kit: ${runtime}`);
const ico=fs.readFileSync(path.join(root,'icon.ico'));
check(ico.readUInt16LE(0)===0&&ico.readUInt16LE(2)===1&&ico.readUInt16LE(4)===6,'Windows icon has six entries');
const sizes=[];for(let i=0;i<ico.readUInt16LE(4);i++)sizes.push(ico[6+i*16]||256);
check(JSON.stringify(sizes)===JSON.stringify([256,128,64,48,32,16]),'Windows icon sizes 16–256');
for(const name of ['01_Studio/bayte_realization_primary.png','01_Studio/bayte_realization_on_dark.png','02_Deal_or_Force/deal_or_force_lettering.png','02_Deal_or_Force/deal_or_force_parchment.png']){
  const result=spawnSync('magick',['identify','-format','%[opaque]',path.join(kit,name)],{encoding:'utf8'});
  check(result.status===0&&result.stdout.toLowerCase()==='false',`real transparent alpha: ${name}`);
}
check(hash(fs.readFileSync(path.join(kit,'02_Deal_or_Force/deal_or_force_icon.png')))===hash(fs.readFileSync(path.join(kit,'00_Sources/game-dof-generated.png'))),'app icon is the approved DoF adaptation, not an F crop');
const en=JSON.parse(fs.readFileSync(path.join(root,'game/localization/english/zz_271_startup_branding.json'),'utf8'));
const ru=JSON.parse(fs.readFileSync(path.join(root,'game/localization/russian/zz_271_startup_branding.json'),'utf8'));
check(Object.keys(en).length===3&&Object.keys(en).every(k=>ru[k]&&en[k]),'all new startup strings have EN/RU text');
console.log(`BRAND_PACK passed=${passed} failures=${failed}`);process.exitCode=failed?1:0;
