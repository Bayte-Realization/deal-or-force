// Deterministic format conversion of approved artwork, not a new logo generator.
// Requires the existing ImageMagick CLI. SVGs contain outlined paths, never embedded PNGs.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {spawnSync} from 'node:child_process';
const root=path.resolve(import.meta.dirname,'../..');
const sources=path.join(root,'art/branding/2026-10-27');
const out=path.resolve(process.argv[2]||'D:/Godot Projects/Bayte Realization Brand Kit/2026-10-27');
fs.mkdirSync(out,{recursive:true});
function run(args,input){const r=spawnSync('magick',args,{input,maxBuffer:96*1024*1024});if(r.status!==0)throw Error(r.stderr.toString());return r.stdout;}
function write(file,data){fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,data);}
function readImage(file){const [w,h]=run(['identify','-format','%w %h',file]).toString().split(' ').map(Number);return {w,h,p:run([file,'-depth','8','rgba:-'])};}
function cleanMask(mask,w,h){const raw=Buffer.from(mask.map(v=>v?255:0));return new Uint8Array(run(['-size',`${w}x${h}`,'-depth','8','gray:-','-blur','0x1.1','-threshold','50%','-depth','8','gray:-'],raw)).map(v=>v>127?1:0);}
function masks(file,studio){const {w,h,p}=readImage(file),ink=new Uint8Array(w*h),red=new Uint8Array(w*h);for(let i=0;i<w*h;i++){const r=p[i*4],g=p[i*4+1],b=p[i*4+2];if(p[i*4+3]<100)continue;if(r>90&&r>g*1.45&&r>b*1.4)red[i]=1;else if(Math.max(r,g,b)<(studio?165:150))ink[i]=1;}return {w,h,ink:studio?cleanMask(ink,w,h):ink,red:studio?cleanMask(red,w,h):red};}
function bounds(m){let x0=m.w,y0=m.h,x1=0,y1=0;for(let y=0;y<m.h;y++)for(let x=0;x<m.w;x++){const i=y*m.w+x;if(m.ink[i]||m.red[i]){x0=Math.min(x0,x);y0=Math.min(y0,y);x1=Math.max(x1,x);y1=Math.max(y1,y);}}return {x:x0,y:y0,w:x1-x0+1,h:y1-y0+1};}
function region(m,predicate){return {...m,ink:m.ink.map((v,i)=>predicate(i%m.w,Math.floor(i/m.w))?v:0),red:m.red.map((v,i)=>predicate(i%m.w,Math.floor(i/m.w))?v:0)};}
function simplify(points,epsilon=.65){if(points.length<4)return points;function dp(a){if(a.length<3)return a;let max=0,index=0;const [x,y]=a[0],[ex,ey]=a.at(-1),dx=ex-x,dy=ey-y,den=dx*dx+dy*dy;for(let i=1;i<a.length-1;i++){const [px,py]=a[i],t=den?Math.max(0,Math.min(1,((px-x)*dx+(py-y)*dy)/den)):0;const d=Math.hypot(px-x-t*dx,py-y-t*dy);if(d>max){max=d;index=i;}}return max>epsilon?[...dp(a.slice(0,index+1)).slice(0,-1),...dp(a.slice(index))]:[a[0],a.at(-1)];}const half=Math.floor(points.length/2);return [...dp(points.slice(0,half+1)).slice(0,-1),...dp([...points.slice(half),points[0]]).slice(0,-1)];}
function trace(mask,w,h){const edges=[],adj=new Map(),stride=w+1;function edge(ax,ay,bx,by){const a=ay*stride+ax,b=by*stride+bx,index=edges.length;edges.push({a,b,used:false});if(!adj.has(a))adj.set(a,[]);adj.get(a).push(index);}const filled=(x,y)=>x>=0&&y>=0&&x<w&&y<h&&mask[y*w+x];for(let y=0;y<h;y++)for(let x=0;x<w;x++)if(filled(x,y)){if(!filled(x,y-1))edge(x,y,x+1,y);if(!filled(x+1,y))edge(x+1,y,x+1,y+1);if(!filled(x,y+1))edge(x+1,y+1,x,y+1);if(!filled(x-1,y))edge(x,y+1,x,y);}const paths=[];for(let j=0;j<edges.length;j++){if(edges[j].used)continue;const first=edges[j].a,points=[];let current=j;while(current!==undefined&&!edges[current].used){const e=edges[current];e.used=true;points.push([e.a%stride,Math.floor(e.a/stride)]);if(e.b===first)break;current=adj.get(e.b)?.find(k=>!edges[k].used);}let area=0;for(let i=0;i<points.length;i++){const a=points[i],b=points[(i+1)%points.length];area+=a[0]*b[1]-b[0]*a[1];}if(Math.abs(area)<5)continue;const a=simplify(points);if(a.length>=3)paths.push('M'+a.map(p=>p.join(',')).join('L')+'Z');}return paths.join('');}
function svg(m,colors=['#202425','#e8523f'],box=bounds(m)){const pad=8;return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${box.x-pad} ${box.y-pad} ${box.w+pad*2} ${box.h+pad*2}" width="${box.w+pad*2}" height="${box.h+pad*2}"><title>${m.title||'Bayte Realization'}</title><g fill-rule="evenodd"><path fill="${colors[0]}" d="${trace(m.ink,m.w,m.h)}"/><path fill="${colors[1]}" d="${trace(m.red,m.w,m.h)}"/></g></svg>`;}
function render(source,dest,size=2048){run(['-background','none','-density','300','svg:-','-resize',`${size}x${size}`,'-strip',dest],source);}
function variant(folder,name,source){write(path.join(folder,name+'.svg'),source);render(source,path.join(folder,name+'.png'));}
function copy(a,b){fs.mkdirSync(path.dirname(b),{recursive:true});fs.copyFileSync(a,b);}
const studioFolder=path.join(out,'01_Studio'),gameFolder=path.join(out,'02_Deal_or_Force');
fs.mkdirSync(studioFolder,{recursive:true});fs.mkdirSync(gameFolder,{recursive:true});
const studio=masks(path.join(sources,'studio-approved.png'),true),emblem=region(studio,(_x,y)=>y<studio.h*.62),words=region(studio,(_x,y)=>y>=studio.h*.62);
const studioVariants={primary:svg(studio),on_dark:svg(studio,['#f5f1e7','#ef624d']),black:svg(studio,['#111111','#111111']),white:svg(studio,['#ffffff','#ffffff']),symbol:svg(emblem),symbol_white:svg(emblem,['#ffffff','#ffffff'])};
const ib=bounds(emblem),wb=bounds(words),symbolPaths=trace(emblem.ink,emblem.w,emblem.h),symbolRed=trace(emblem.red,emblem.w,emblem.h),wordPaths=trace(words.ink,words.w,words.h),wordRed=trace(words.red,words.w,words.h);
function horizontal(c1,c2){const scale=.66,hh=ib.h*scale,width=ib.w*scale+55+wb.w;return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width+32} ${Math.max(hh,wb.h)+32}"><g fill-rule="evenodd"><g transform="translate(16,16) scale(${scale}) translate(${-ib.x},${-ib.y})"><path fill="${c1}" d="${symbolPaths}"/><path fill="${c2}" d="${symbolRed}"/></g><g transform="translate(${16+ib.w*scale+55-wb.x},${16+(hh-wb.h)/2-wb.y})"><path fill="${c1}" d="${wordPaths}"/><path fill="${c2}" d="${wordRed}"/></g></g></svg>`;}
studioVariants.horizontal=horizontal('#202425','#e8523f');studioVariants.horizontal_on_dark=horizontal('#f5f1e7','#ef624d');
for(const [name,s]of Object.entries(studioVariants))variant(studioFolder,'bayte_realization_'+name,s);
for(const size of [256,512,1024,4096])render(studioVariants.primary,path.join(studioFolder,`bayte_realization_primary_${size}.png`),size);
for(const size of [64,128,256,512,1024])render(studioVariants.symbol,path.join(studioFolder,`bayte_realization_symbol_${size}.png`),size);
const game=masks(path.join(sources,'game-lettering-source.png'),false);game.title='Deal or Force';
for(const [name,colors]of Object.entries({lettering:['#082b41','#bd1214'],lettering_on_dark:['#f5f1e7','#ef3d3e'],black:['#111111','#111111'],white:['#ffffff','#ffffff']}))variant(gameFolder,'deal_or_force_'+name,svg(game,colors));
const plaque=path.join(gameFolder,'deal_or_force_parchment.png');
// Drop the generation's translucent outer halo, retain opaque parchment and worn edges.
run([path.join(sources,'game-parchment-generated.png'),'(','+clone','-alpha','extract','-threshold','90%',')','-alpha','off','-compose','CopyOpacity','-composite','-trim','+repage','-strip',plaque]);
for(const size of [1024,2048,4096])run([plaque,'-resize',`${size}x${size}`,'-strip',path.join(gameFolder,`deal_or_force_parchment_${size}.png`)]);
// The user explicitly retained the three-letter DoF monogram, not a lone F.
const monogram=masks(path.join(sources,'game-dof-reference.png'),false);monogram.title='Deal or Force — DoF monogram';
const mb=bounds(monogram),side=Math.max(mb.w,mb.h)*1.22;
const monogramSvg=svg(monogram,['#082b41','#bd1214'],{x:mb.x-(side-mb.w)/2,y:mb.y-(side-mb.h)/2,w:side,h:side});
variant(gameFolder,'deal_or_force_icon',monogramSvg);
if(fs.existsSync(path.join(sources,'game-dof-generated.png')))copy(path.join(sources,'game-dof-generated.png'),path.join(gameFolder,'deal_or_force_icon.png'));
for(const size of [32,48,64,128,256,512,1024])run([path.join(gameFolder,'deal_or_force_icon.png'),'-resize',`${size}x${size}`,'-strip',path.join(gameFolder,`deal_or_force_icon_${size}.png`)]);
run([path.join(gameFolder,'deal_or_force_icon.png'),'-background','#f5efdf','-alpha','remove','-resize','1024x1024!','-define','icon:auto-resize=256,128,64,48,32,16',path.join(gameFolder,'deal_or_force.ico')]);
run([path.join(studioFolder,'bayte_realization_symbol_1024.png'),'-background','#f8f6f1','-alpha','remove','-define','icon:auto-resize=256,128,64,48,32,16',path.join(studioFolder,'bayte_realization.ico')]);
for(const [folder,base]of [[studioFolder,'bayte_realization_primary'],[gameFolder,'deal_or_force_parchment']]){const file=path.join(folder,base+'.png');run([file,'-define','webp:lossless=true',path.join(folder,base+'.webp')]);run([file,'-background','#f8f6f1','-alpha','remove','-quality','96',path.join(folder,base+'.jpg')]);run([file,'-compress','LZW',path.join(folder,base+'.tiff')]);run([file,'-background','white','-alpha','remove',path.join(folder,base+'.pdf')]);}
function poster(file,dest,bg,w,h){run(['-size',`${w}x${h}`,`xc:${bg}`,'(',file,'-resize',`${Math.round(w*.72)}x${Math.round(h*.72)}`,')','-gravity','center','-compose','over','-composite','-strip',dest]);}
for(const [w,h]of [[1920,1080],[3840,2160]]){poster(path.join(studioFolder,'bayte_realization_on_dark.png'),path.join(studioFolder,`studio_splash_${w}x${h}.png`),'#10151c',w,h);poster(plaque,path.join(gameFolder,`game_splash_${w}x${h}.png`),'#10151c',w,h);}
for(const [folder,file]of [[studioFolder,'bayte_realization_primary.png'],[gameFolder,'deal_or_force_parchment.png']]){poster(path.join(folder,file),path.join(folder,'social_square_1080.png'),'#f8f6f1',1080,1080);}
const runtime=path.join(root,'game/gfx/interface/branding');
copy(path.join(studioFolder,'bayte_realization_on_dark.png'),path.join(runtime,'studio_logo_dark.png'));
copy(path.join(studioFolder,'bayte_realization_primary.png'),path.join(runtime,'studio_logo_light.png'));
copy(path.join(studioFolder,'bayte_realization_symbol_512.png'),path.join(runtime,'studio_symbol.png'));
copy(plaque,path.join(root,'game/gfx/interface/brand_logo_banner.png'));
copy(path.join(gameFolder,'deal_or_force_lettering_on_dark.png'),path.join(runtime,'game_logo_dark.png'));
copy(path.join(gameFolder,'deal_or_force.ico'),path.join(root,'icon.ico'));
copy(path.join(gameFolder,'deal_or_force_icon_128.png'),path.join(root,'icon_small.png'));
copy(path.join(sources,'README_RU.md'),path.join(out,'README_RU.md'));
const sourceDest=path.join(out,'00_Sources');fs.mkdirSync(sourceDest,{recursive:true});for(const n of ['studio-approved.png','game-lettering-source.png','game-parchment-generated.png','game-dof-reference.png','game-dof-generated.png','PROVENANCE.json'])if(fs.existsSync(path.join(sources,n)))copy(path.join(sources,n),path.join(sourceDest,n));
const files=[];function scan(dir){for(const d of fs.readdirSync(dir,{withFileTypes:true})){const full=path.join(dir,d.name);if(d.isDirectory())scan(full);else if(d.name!=='MANIFEST.json')files.push({path:path.relative(out,full).replaceAll('\\','/'),bytes:fs.statSync(full).size,sha256:crypto.createHash('sha256').update(fs.readFileSync(full)).digest('hex')});}}scan(out);write(path.join(out,'MANIFEST.json'),JSON.stringify({date:'2026-10-27',source:'Bayte Realization approved artwork + imagegen parchment adaptation',files},null,2));
console.log(JSON.stringify({out,files:files.length,studioBounds:bounds(studio),gameBounds:bounds(game)},null,2));
