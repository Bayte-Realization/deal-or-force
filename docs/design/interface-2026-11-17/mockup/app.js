// New campaign screen mockup, revision 2: renders the frame for each state, tooltips, keys, census.
"use strict";

const F = document.getElementById("frame");
const qs = new URLSearchParams(location.search);
const SHOT = qs.has("shot");                           // ?shot: frame at 1:1, no page chrome (for screenshots)
if (SHOT) document.body.classList.add("shot");
const S = {
  state: 1, res: 1080, paused: true, speed: 2, mode: "pol", outliner: false,
  day: 0, alt: false, qa: null, undo: null, pcard: null, route: [826, 760], detach: [752, 752],
  budget: null, flash: null, mini: false, more: false, moreTab: "modes", news: false, hlPinned: false,
  buildings: false, bfocus: "rev:2", lpH: null, debtQ: 3, ocap: 4,
};
const L = () => LAY[S.res];
const K = () => (S.res === 1080 ? 1 : 2 / 3);          // map coordinates (1080 basis) -> frame
const DYN = {};                                        // dynamic tooltips registered during render
const WAR = () => S.state === 8;
const FIRST_STEPS = () => S.day < 60;

// ---------------------------------------------------------------- small helpers
const px = v => Math.round(v * 100) / 100 + "px";
const box = r => `left:${px(r[0])};top:${px(r[1])};width:${px(r[2] - r[0])};height:${px(r[3] - r[1])}`;
const ico = (n, s, cls = "") => `<i class="ico i-${n.replace(/_/g, "-")} ${cls}" style="--s:${s}px"></i>`;
const gl = (n, s, style = "") => `<span class="glyph" style="--s:${s}px;${style}">${GLYPH[n]}</span>`;
const sevGlyph = s => s === "n" ? `<span style="color:var(--neg)">●</span>` : s === "w" ? `<span style="color:var(--warn)">▲</span>` : `<span style="color:var(--info)">◆</span>`;
let dynId = 0;
const dyn = tip => { const k = "dyn" + (dynId++); DYN[k] = tip; return k; };
const big = () => S.res === 1080;

function polyInset(P, d) {
  const n = P.length, Ls = [];
  for (let i = 0; i < n; i++) {
    const a = P[i], b = P[(i + 1) % n], dx = b[0] - a[0], dy = b[1] - a[1], len = Math.hypot(dx, dy) || 1;
    Ls.push([a[0] - dy / len * d, a[1] + dx / len * d, dx, dy]);
  }
  return Ls.map((l2, i) => {
    const l1 = Ls[(i - 1 + n) % n], cr = l1[2] * l2[3] - l1[3] * l2[2];
    if (Math.abs(cr) < 1e-9) return [l2[0], l2[1]];
    const t = ((l2[0] - l1[0]) * l2[3] - (l2[1] - l1[1]) * l2[2]) / cr;
    return [l1[0] + t * l1[2], l1[1] + t * l1[3]];
  });
}
function chamfer(w, h, c, corners) {
  const has = k => corners.includes(k) ? c : 0, tl = has("tl"), tr = has("tr"), br = has("br"), bl = has("bl");
  const P = [];
  P.push([0, tl]); if (tl) P.push([tl, 0]);
  P.push([w - tr, 0]); if (tr) P.push([w, tr]);
  P.push([w, h - br]); if (br) P.push([w - br, h]);
  P.push([bl, h]); if (bl) P.push([0, h - bl]);
  return P;
}
const pts = P => P.map(p => `${p[0].toFixed(2)},${p[1].toFixed(2)}`).join(" ");

// A plate: shadow, clipped face, then the frame: dark outer line, a 2-3 px warm metal rim
// (bronze gradient, a material and not an accent colour), a light inner line, and the
// engraved line further in. One frame for every plate on the screen.
function plateHTML(x, y, w, h, P, { cls = "", panel = "", inner = "", pop = false, attrs = "" } = {}) {
  const clip = "polygon(" + P.map(p => `${p[0]}px ${p[1]}px`).join(",") + ")";
  const b = big(), rim = b ? 2.6 : 2, e = b ? 8 : 6;
  const svg = `<svg class="eng" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">
    <polygon points="${pts(polyInset(P, .5))}" fill="none" stroke="#090705"/>
    <polygon points="${pts(polyInset(P, 1 + rim / 2))}" fill="none" stroke="url(#rimG)" stroke-width="${rim}"/>
    <polygon points="${pts(polyInset(P, 1.5 + rim))}" fill="none" stroke="rgba(255,236,200,.20)"/>
    <polygon points="${pts(polyInset(P, 2.5 + rim))}" fill="none" stroke="rgba(0,0,0,.55)"/>
    <polygon points="${pts(polyInset(P, e))}" fill="none" stroke="#0b0907"/>
    <polygon points="${pts(polyInset(P, e + 1))}" fill="none" stroke="#4d4336" stroke-opacity=".8"/></svg>`;
  return `<div class="plate ${pop ? "pop " : ""}${cls}" ${panel ? `data-panel="${panel}"` : ""} ${attrs} style="left:${x}px;top:${y}px;width:${w}px;height:${h}px"><div class="sh"><div class="pl" style="clip-path:${clip}"></div></div>${svg}${inner}</div>`;
}
const plate = (r, c, corners, o = {}) => plateHTML(r[0], r[1], r[2] - r[0], r[3] - r[1], chamfer(r[2] - r[0], r[3] - r[1], c, corners), o);

// heater shield, era-neutral (the shape is skin data: game/skins/<id>/materials -> shield.path)
function shieldPath(w, h) {
  return `M0 0H${w}V${h * .46}C${w} ${h * .78} ${w * .74} ${h * .93} ${w / 2} ${h}C${w * .26} ${h * .93} 0 ${h * .78} 0 ${h * .46}Z`;
}

// ---------------------------------------------------------------- panel 1: corner
function cellDefs() {
  const crisis = S.state === 6, war = WAR();
  const c = [
    { k: "C1", ic: "treasury", cap: "Казна", v: war ? "$0,88<small>млрд</small>" : crisis ? "$1,41<small>млрд</small>" : "$3,12<small>млрд</small>", d: war ? `<span class="dn">▼ 1,3 млрд</span>` : crisis ? `<span class="dn">▼ 1,02 млрд</span>` : `<span class="dn">▼ 38 млн</span>`, st: war || crisis ? "bad" : "", act: "budget", topic: "cell:treasury" },
    { k: "C2", ic: "gdp", cap: "ВВП", v: "$75,2<small>млрд</small>", d: `<span class="up">▲ 2,4 %</span>`, topic: "cell:gdp" },
    { k: "C3", ic: "coins", cap: "Долг · ВВП", v: war ? "55,1 %" : SRB.debt, d: "", topic: "cell:debt" },
    { k: "C4", ic: "inflation", cap: "Инфляция", v: SRB.infl, d: `<span class="wn">▲</span>`, st: "warn", topic: "cell:inflation" },
    { k: "C5", ic: "stability", cap: "Стабильность", v: war ? "57 %" : crisis ? "59 %" : SRB.stab, d: crisis || war ? `<span class="dn">▼</span>` : "", topic: "cell:stability" },
    { k: "C6", ic: "manpower", cap: "Призыв", v: war ? "96<small>тыс.</small>" : crisis ? "121<small>тыс.</small>" : "182<small>тыс.</small>", d: `<span class="up">▲ 310</span>`, topic: "cell:manpower" },
    { k: "C7", ic: "government", cap: "Капитал", v: SRB.pc, d: `<span class="up">▲ 2,1</span>`, topic: "cell:capital" },
  ];
  if (war) c.push({ k: "C8", ic: "wars", cap: "Война", v: "63 %", d: `<span class="dn">▼</span>`, st: "warn", act: "flash", flash: "R9", topic: "cell:war" });
  return c;
}
function corner() {
  const l = L(), c = l.corner, cells = cellDefs(), n = cells.length;
  const W = l.cellX0 + n * l.cellW + (big() ? 12 : 8), ch = c.c, H = c.H, RW = c.RW, B = c.B;
  const P = [[0, 0], [W, 0], [W, H - ch], [W - ch, H], [RW, H], [RW, B - ch], [RW - ch, B], [0, B]];
  let h = plateHTML(0, 0, W, B, P, { panel: "corner" });
  // resource cells: one long sunken well with engraved separators
  const y1 = l.cellY[0], y2 = l.cellY[1];
  h += `<div class="well cellsbed" style="${box([l.cellX0 - 2, y1, l.cellX0 + n * l.cellW + 2, y2])}"></div>`;
  cells.forEach((cl, i) => {
    const x = l.cellX0 + i * l.cellW, r = [x, y1, x + l.cellW, y2];
    const on = (cl.k === "C1" && S.state === 5);
    const inner = `<span style="position:absolute;left:${l.cellIx}px;top:${l.cellIy}px">${ico(cl.ic, l.cellIcon, cl.st === "bad" ? "t-neg" : "")}</span>
      <div class="v ${cl.st || ""}" style="left:${l.cellVx}px;top:${l.cellVy}px">${cl.v}</div>
      <div class="cap" style="left:${l.cellIx}px;top:${l.cellCy}px"><span class="c">${cl.cap}</span>${big() ? cl.d : (cl.d ? cl.d.replace(/(▲|▼)[^<]*/, "$1") : "")}</div>`;
    h += `<div class="cell ${on ? "on" : ""}" data-btn data-tip="${cl.k}" data-topic="${cl.topic}" ${cl.act ? `data-act="${cl.act}"` : ""} ${cl.flash ? `data-k="${cl.flash}"` : ""} style="${box(r)}">${inner}${i < n - 1 ? '<span class="sep"></span>' : ""}</div>`;
  });
  // the shield: the one rich detail of the screen, over both arms
  const s = l.shield, sw = s[2] - s[0], sh = s[3] - s[1], ins = big() ? 9 : 6;
  const innerW = sw - 2 * ins, innerH = sh - 2 * ins;
  h += `<div class="shield ${S.state === 2 ? "on" : ""}" data-btn data-tip="U1" data-key="Q" data-act="country" style="${box(s)}">
    <svg width="${sw}" height="${sh}" viewBox="-2 -2 ${sw + 4} ${sh + 4}" style="position:absolute;left:-2px;top:-2px;overflow:visible">
      <defs><clipPath id="shclip"><path transform="translate(${ins} ${ins})" d="${shieldPath(innerW, innerH)}"/></clipPath>
      <linearGradient id="gloss" x1="0" y1="0" x2=".6" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".28"/><stop offset=".45" stop-color="#fff" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity=".35"/></linearGradient></defs>
      <path d="${shieldPath(sw, sh)}" fill="url(#plateG)" stroke="#090705" stroke-width="1.5"/>
      <path transform="translate(2.2 2.2)" d="${shieldPath(sw - 4.4, sh - 4.4)}" fill="none" stroke="url(#rimG)" stroke-width="${big() ? 3.4 : 2.4}"/>
      <path transform="translate(4.4 4.4)" d="${shieldPath(sw - 8.8, sh - 8.8)}" fill="none" stroke="rgba(255,236,200,.22)"/>
      <image href="img/flags/srb.png" x="${ins - 6}" y="${ins}" width="${innerW + 12}" height="${innerH}" preserveAspectRatio="xMidYMid slice" clip-path="url(#shclip)"/>
      <path transform="translate(${ins} ${ins})" d="${shieldPath(innerW, innerH)}" fill="url(#gloss)" stroke="#0b0907" stroke-width="2"/>
      <path transform="translate(${ins - 2} ${ins - 2})" d="${shieldPath(innerW + 4, innerH + 4)}" fill="none" stroke="#4d4336"/></svg></div>`;
  h += `<div class="rankplate" data-tip="${dyn(T("Место в мире", `<p>№ ${SRB.rank} по ВВП. Рейтинги — Сводки (L).</p>`, ""))}" style="${box(l.rank)}">№ ${SRB.rank}</div>`;
  // rail: nine doors and search; the country lives on the shield (Q)
  const openRail = { 4: "R7", 5: "R3" }[S.state];
  RAIL.forEach((it, i) => {
    const r = l.rail[i];
    let badge = "", b = it.badge, topic = it.topic;
    if (it.k === "R6" && S.day >= 60) { b = null; topic = null; }      // research running later in the year
    if (it.k === "R9" && WAR()) { b = "1"; topic = "wars:count"; }
    if (it.k === "R7" && WAR()) { b = "2"; topic = "army:unassigned"; }
    if (b === "dot") badge = `<span class="badge dot" style="right:${big() ? 9 : 5}px;top:${big() ? 9 : 5}px"></span>`;
    else if (b) badge = `<span class="badge" style="right:${big() ? 3 : 1}px;top:${big() ? 3 : 1}px">${b}</span>`;
    const act = { R3: "budget", R7: "army" }[it.k] || "flash";
    h += `<div class="rail ${openRail === it.k || S.flash === it.k ? "on" : ""}" data-btn data-rail="${i}" data-key="${it.key}" data-act="${act}" data-k="${it.k}" ${b && topic ? `data-topic="${topic}"` : ""} style="${box(r)}">${ico(it.ic, l.railIcon)}${badge}</div>`;
  });
  l.rules.forEach(r => { h += `<div class="rule-h" style="left:${r[0]}px;top:${r[1] - 1}px;width:${r[2] - r[0]}px"></div>`; });
  return h;
}

// ---------------------------------------------------------------- panel 2: date plate
const MONTHS = ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];
function dateText() {
  const d = new Date(Date.UTC(2024, 0, 1 + S.day));
  return `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
}
function datePlate() {
  const l = L(), r = l.date, b = big();
  let h = plate(r, b ? 8 : 6, "bl", { panel: "date" });
  h += `<div class="date ${S.paused ? "paused" : ""}" id="d1" data-btn data-tip="D1" data-key="Пробел" data-act="pause" style="${box(l.d1)}">${gl(S.paused ? "play" : "pause", b ? 20 : 15)}<span class="dt">${dateText()}</span><small class="p">${S.paused ? "пауза" : "идёт"}</small></div>`;
  const [mw, mh, st] = l.mark;
  let marks = "";
  for (let i = 0; i < 5; i++) marks += `<i data-speed="${i + 1}" class="${i < S.speed ? "lit" : ""} ${i + 1 === S.speed ? "cur" : ""}" style="left:${i * st}px;width:${mw}px;height:${mh}px"></i>`;
  h += `<div class="speed well ${S.paused ? "paused" : ""}" data-btn data-tip="D3" data-key="1–5" style="${box(l.d3)}"><div class="marks" style="left:${b ? 6 : 4}px;top:${(l.d3[3] - l.d3[1] - mh) / 2}px">${marks}</div></div>`;
  const crisis = S.state === 6 || WAR(), lv = crisis ? 3 : 5, ten = crisis ? 91 : 79;
  let lamps = ""; for (let i = 5; i >= 1; i--) lamps += `<i class="${i === lv ? "l" + lv : ""}"></i>`;
  const tipK = crisis ? dyn(T("Мир", `<table><tr><td><span class="term" data-term="defcon">DEFCON</span></td><td class="warn">3 · готовность</td></tr><tr><td><span class="term" data-term="tension">Напряжённость</span></td><td class="neg">91 · на грани</td></tr></table><p>Ступень поднялась из-за мобилизаций на Балканах; смена ступени поставила паузу (пресет «Paradox»).</p>`, "Щелчок — окно «Мир»: последние новости, DEFCON, державы")) : "D4";
  h += `<div class="world well ${crisis ? "pulse" : ""}" data-btn data-tip="${tipK}" data-act="news" style="${box(l.d4)}">
    <div class="dc"><span class="lb">DEFCON</span><b class="${lv <= 3 ? "wn" : ""}">${lv}</b><div class="lamps">${lamps}</div></div><span class="vr"></span>
    <div class="tn">${gl("tension", b ? 17 : 13)}<b class="${crisis ? "ng" : "wn"}">${ten}</b><div class="bar"><i style="width:${ten}%"></i><s style="left:75%"></s></div></div></div>`;
  h += `<div class="rule-v" style="left:${l.dv}px;top:${r[1] + 10}px;height:${r[3] - r[1] - 20}px"></div>`;
  const full = S.state === 5;
  h += `<div class="menu well btnwell" data-btn data-tip="D5" data-key="Esc" style="${box(l.d5)}">${gl("menu", b ? 26 : 20)}${full ? '<span class="badge red cnt">3</span>' : ""}</div>`;
  return h;
}

// ---------------------------------------------------------------- panel 3: alerts + "Слежу"
function alertList() {
  let list = S.state === 6 ? ALERTS_CRISIS : WAR() ? ALERTS_WAR : ALERTS_REST;
  if (!FIRST_STEPS()) list = list.filter(a => !a.next);
  const max = S.outliner ? 3 : 4;
  if (list.length <= max) return list;
  // fold the tail into one "Ещё N" row: the worst severity of what it hides, every name listed
  const keep = list.slice(0, max - 1), rest = list.slice(max - 1);
  const names = rest.flatMap(a => a.more ? a.tx.replace(/^Ещё \d+: /, "").split(", ") : [a.sh || a.tx.split(" ")[0]]);
  const worst = rest.some(a => a.sev === "n") ? "n" : rest.some(a => a.sev === "w") ? "w" : "i";
  return keep.concat([{ sev: worst, ic: rest[0].ic, more: true, tx: `Ещё ${names.length}: ${names.join(", ")}`, field: "", topic: "more:" + names.join(","),
    tip: T(`Ещё ${names.length}`, `<p>${rest.map(a => a.tx).join(" · ")}</p><p>Все тревоги с причинами и ответами — в Ситуационной.</p>`, "Щелчок — Ситуационная") }]);
}
function alertsPlate() {
  const l = L(), a = l.alerts, list = alertList(), b = big();
  const rows = list.length, tabY = a.y + rows * a.row + 4;
  const rowsO = S.state === 6 || WAR() ? OUTLINER_CRISIS : OUTLINER_REST;
  // the open outliner is as tall as what it shows, no taller
  const shownO = rowsO.slice(0, S.ocap), groups = shownO.filter((r, i) => i === 0 || r[0] !== shownO[i - 1][0]).length;
  const oh = shownO.length * (b ? 28 : 24) + groups * (b ? 26 : 21) + (rowsO.length > S.ocap ? 24 : 0) + 6;
  const open = S.outliner, bottom = open ? tabY + a.tab + 4 + oh + (b ? 8 : 6) : tabY + a.tab + (b ? 6 : 4);
  let h = plate([a.x1, a.y - (b ? 8 : 6), a.x2, bottom], b ? 8 : 6, "bl", { panel: "alerts" });
  list.forEach((al, i) => {
    const y = a.y + i * a.row, k = dyn(al.tip), w = a.x2 - a.x1;
    const fieldW = al.field ? a.field : 0, txW = w - a.tx1 - a.sev - 10 - (fieldW ? fieldW + 6 : 0);
    h += `<div class="arow ${al.next ? "next" : ""} ${al.more ? "more" : ""}" data-btn data-tip="${k}" data-alert="${i}" data-topic="${al.topic}" ${al.go ? `data-go="${al.go}"` : ""} style="left:${a.x1}px;top:${y}px;width:${w}px;height:${a.row}px">
      <span style="position:absolute;left:${a.ix}px;top:${(a.row - a.icon) / 2}px">${ico(al.ic, a.icon, al.sev === "n" ? "t-neg" : al.sev === "w" ? "t-warn" : "")}</span>
      <span class="tx" style="left:${a.tx1}px;width:${txW}px;top:${(a.row - (b ? 20 : 16)) / 2}px">${al.tx}</span>
      ${fieldW ? `<span class="fd ${al.sev === "n" ? "ng" : ""}" style="left:${w - a.sev - 10 - fieldW}px;width:${fieldW}px;top:${(a.row - (b ? 20 : 16)) / 2}px">${al.field}</span>` : ""}
      <span class="st" style="left:${w - a.sev - 4}px;top:${(a.row - 12) / 2}px;width:12px">${sevGlyph(al.sev)}</span></div>`;
  });
  h += `<div class="rule-h" style="left:${a.x1 + 12}px;top:${tabY - 3}px;width:${a.x2 - a.x1 - 24}px"></div>`;
  h += `<div class="ostab ${open ? "open" : ""}" data-btn data-tip="O1" data-key="J" data-act="outliner" style="left:${a.x1 + 6}px;top:${tabY}px;width:${a.x2 - a.x1 - 12}px;height:${a.tab}px">${gl("eye", b ? 17 : 13)}Слежу · ${rowsO.length}<span class="ar">▸</span></div>`;
  if (open) h += outliner(rowsO, a.x1 + 12, tabY + a.tab + 2, a.x2 - a.x1 - 24, oh);
  return h;
}
function outliner(rows, x, y, w, hgt) {
  const shown = rows.slice(0, S.ocap);
  let last = "", inner = "";
  shown.forEach(r => {
    const [grp, ic, n, v, topic, dot, bar] = r;
    if (grp !== last) { inner += `<h4>${grp}</h4>`; last = grp; }
    const dc = dot === "neg" ? "var(--neg)" : dot === "warn" ? "var(--warn)" : null;
    inner += `<div class="orow" data-btn data-topic="${topic}" data-tip="${dyn(T(n, `<p>${grp}: ${v}</p>`, "Щелчок — к месту · ПКМ — открепить"))}">${ico(ic, 16)}<span class="n">${n}</span>${bar ? `<span class="mini"><i style="width:${bar * 100}%"></i></span>` : ""}${dc ? `<span class="dot" style="background:${dc};box-shadow:0 0 4px ${dc}"></span>` : ""}<span class="v">${v}</span></div>`;
  });
  if (rows.length > S.ocap) inner += `<div class="omore">ещё ${rows.length - S.ocap} — колесом</div>`;
  return `<div class="outliner" style="left:${x}px;top:${y}px;width:${w}px;height:${hgt}px">${inner}</div>`;
}

// ---------------------------------------------------------------- panel 4: map plate
function mapPlate() {
  const l = L(), b = big(), mini = S.mini;
  const r = mini ? l.mapPlateMini : l.mapPlate;
  let h = plate(r, b ? 8 : 6, "tl", { panel: "map" });
  const dy = mini ? l.miniRow - l.modes[0][1] : 0, g = l.modeGlyph;
  MODES.forEach((m, i) => {
    const rr = l.modes[i].slice(); rr[1] += dy; rr[3] += dy;
    const inner = m.g ? gl(m.g, g) : ico(m.ic, g);
    h += `<div class="mode ${S.mode === m.cls ? "on" : ""} ${S.flash === m.k ? "flash" : ""}" data-btn data-tip="${m.k}" data-key="${m.key}" data-mode="${m.cls}" style="${box(rr)}">${inner}</div>`;
  });
  const mo = l.more.slice(); mo[1] += dy; mo[3] += dy;
  h += `<div class="mode ${S.more ? "on" : ""}" data-btn data-tip="MORE" data-key="M" data-act="more" style="${box(mo)}">${gl("more", g)}</div>`;
  if (mini) {
    const m8 = l.m8, w = m8[2] - m8[0], hh = m8[3] - m8[1];
    const vx = (280 - 140) / 1416 * w, vw = 1010 / 1416 * w, vy = (395 - 260) / 708 * hh, vh = 568 / 708 * hh;
    h += `<div class="minimap" data-btn data-tip="M8" style="${box(m8)}"><div class="vp" style="left:${vx}px;top:${vy}px;width:${vw}px;height:${vh}px"></div></div>`;
  }
  return h;
}
function morePop() {
  const l = L(), r = l.morePop, w = r[2] - r[0], hh = r[3] - r[1], b = big();
  const tabs = `<div class="ftabs" style="left:${b ? 14 : 10}px;right:${b ? 14 : 10}px;top:${b ? 12 : 8}px">${[["modes", "Режимы"], ["layers", "Слои"]].map(t => `<div class="ftab ${S.moreTab === t[0] ? "on" : ""}" data-btn data-mtab="${t[0]}">${t[1]}</div>`).join("")}</div>`;
  const body = S.moreTab === "modes"
    ? `<div class="mgrid">${ALL_MODES.map((m, i) => `<div class="btn mchip ${i < 5 && MODES[i].cls === S.mode ? "on" : ""}" data-btn>${m}</div>`).join("")}</div><p class="note" style="margin:10px 2px 0">Перетащите режим на любой из пяти слотов. Пятый слот — «Война» — постоянный.</p>`
    : `<div class="lgrid">${LAYERS.map(t => { const on = t[0] === "Мини-карта" ? S.mini : t[0] === "Постройки" ? S.buildings : t[2]; return `<div class="lrow" data-btn data-layer="${t[0]}"><span class="tg ${on ? "on" : ""}"></span>${t[0]}${t[1] ? `<span class="k">${t[1]}</span>` : ""}</div>`; }).join("")}</div>`;
  return plateHTML(r[0], r[1], w, hh, chamfer(w, hh, 8, "tl br"), { pop: true, inner: tabs + `<div class="lp-body doc-body" style="left:${b ? 14 : 10}px;right:${b ? 14 : 10}px;top:${b ? 52 : 40}px;bottom:${b ? 14 : 10}px">${body}</div>`, attrs: `id="morepop"` });
}

// ---------------------------------------------------------------- panel 5: bottom strip (army tokens, fronts, undo)
function bottomStrip(shift) {
  const l = L(), s = l.strip, b = big();
  const items = WAR() ? WAR_STRIP : ARMIES;
  if (!items.length && !S.undo) return "";
  // never hidden: beside a left panel or under a full screen the tokens shrink to flag and number
  const compact = !!shift || S.state === 5, x0 = shift ? l.tshift : s.x0, step = compact ? s.cstep : s.step, tw = compact ? s.cw : s.w;
  const n = Math.min(items.length, s.max - (S.undo ? 1 : 0));   // the undo cell takes the fifth place
  const tokensEnd = n ? x0 + (n - 1) * step + tw : x0 - 8;
  const undoX = tokensEnd + 8, end = S.undo ? undoX + s.undo : tokensEnd;
  let h = plate([x0 - 8, s.y[0], end + 8, s.y[1]], b ? 8 : 6, "tr", { panel: "strip" });
  items.slice(0, n).forEach((a, i) => {
    const r = [x0 + i * step, s.h[0], x0 + i * step + tw, s.h[1]];
    let st = a.st, g = a.g, edge = a.edge;
    if (S.state === 6 && i < 2) { st = i === 0 ? "Марш · Рашка, 3 дн." : "Марш · Бујановац, 2 дн."; g = "march"; edge = "#e7b058"; }
    if (S.state === 4 && i === 0) { st = "Марш · Ниш, 4 дн."; g = "march"; edge = "#6da7e6"; }
    const tip = dyn(T(a.front ? a.n : a.n, a.front
      ? `<p>${a.armies} армии · ${a.d} дивизий. Щелчок — выбрать весь фронт; ▸ — развернуть армии фронта в полосе.</p>`
      : `<table><tr><td>Командующий</td><td>${a.gen || "—"}</td></tr><tr><td>Дивизий</td><td>${a.d}</td></tr><tr><td>Сила</td><td>${Math.round(a.str * 100)} %</td></tr><tr><td><span class="term" data-term="org">Организация</span></td><td>88 %</td></tr>${a.grp ? `<tr><td>Группа</td><td>${a.grp}</td></tr>` : ""}</table>`,
      "Щелчок — выбрать · двойной — камера · <kbd>Ctrl</kbd>+щелчок — добавить · <kbd>Ctrl</kbd>+<kbd>6</kbd>…<kbd>0</kbd> — в группу"));
    const flag = a.front ? "" : `<img src="img/flags/srb.png" alt="">`;
    const grp = a.grp ? `<span class="grp">${a.grp % 10}</span>` : "";
    if (compact) {
      h += `<div class="token compact ${S.state === 4 && i === 0 ? "sel" : ""}" data-btn data-tip="${tip}" data-act="army" style="${box(r)};--edge:${edge}">${a.front ? gl("front", b ? 18 : 14) : flag}<b>${a.d}</b>${grp}</div>`;
    } else {
      h += `<div class="token ${a.front ? "front" : ""} ${S.state === 4 && i === 0 ? "sel" : ""}" data-btn data-tip="${tip}" data-act="army" style="${box(r)};--edge:${edge}">
        ${a.front ? `<span class="fg">${gl("front", b ? 22 : 16)}</span>` : flag}
        <div class="nm">${a.n}<span>×${a.d}</span></div>
        <div class="stt">${gl(g, b ? 12 : 10)}${st}</div>
        <div class="str"><i style="width:${a.str * 100}%"></i></div>${grp}${a.front ? `<span class="exp">▸</span>` : ""}</div>`;
    }
  });
  if (S.undo) {
    h += `<div class="undo" style="left:${undoX}px;top:${s.h[0]}px;width:${s.undo}px;height:${s.h[1] - s.h[0]}px">${gl("undo", b ? 18 : 14, "color:var(--paper-dim)")}<span class="ut">${S.undo}<small>до следующего действия</small></span><span class="btn sm" data-btn data-act="undo">Отменить <span class="k">Ctrl Z</span></span></div>`;
  }
  return h;
}

// ---------------------------------------------------------------- left work panel (height follows content)
function leftPanel(spec) {
  const l = L(), r = l.left, b = big(), w = r[2] - r[0];
  const hh = S.lpH || (r[3] - r[1]);
  const pad = b ? 14 : 10, head = l.head, tabs = l.tabs, foot = l.foot;
  const emb = spec.flag ? `<div class="emb shieldemb"><svg viewBox="0 0 52 60"><defs><clipPath id="embclip"><path transform="translate(4 4)" d="${shieldPath(44, 52)}"/></clipPath></defs><path d="${shieldPath(52, 60)}" fill="url(#plateG)" stroke="url(#rimG)" stroke-width="2.4"/><image href="img/flags/${spec.flag}.png" x="-2" y="4" width="56" height="52" preserveAspectRatio="xMidYMid slice" clip-path="url(#embclip)"/><path transform="translate(4 4)" d="${shieldPath(44, 52)}" fill="url(#gloss2)" stroke="#0b0907" stroke-width="1.5"/></svg></div>` : `<div class="emb icon hdr">${ico(spec.icon, b ? 30 : 22, spec.tint || "")}</div>`;
  let inner = `<div class="lp-head" style="left:${pad}px;right:${pad - 4}px;top:${b ? 12 : 8}px">
    ${emb}<div class="tt"><h2>${spec.title}</h2><div>${spec.sub}</div></div>
    <div class="hb" data-btn data-tip="BACK">${gl("back", 18)}</div><div class="hb ${spec.pinned ? "on" : ""}" data-btn data-tip="PIN">${gl("pin", 18)}</div><div class="hb" data-btn data-tip="CLOSE" data-act="close" data-key="Esc">${gl("close", 16)}</div></div>
    <div class="lp-figs" style="left:${pad}px;right:${pad}px;top:${b ? 72 : 50}px">${spec.figs.map(f => `<div class="fig well"><b class="${f[2] || ""}">${f[0]}</b><span>${f[1]}</span></div>`).join("")}</div>`;
  inner += `<div class="ftabs attached" style="left:${pad}px;right:${pad}px;top:${head + 2}px;height:${tabs}px">${spec.tabs.map((t, i) => `<div class="ftab ${i === spec.active ? "on" : ""}" data-btn>${t}</div>`).join("")}</div>`;
  inner += `<div class="lp-body" style="left:${pad}px;right:${pad}px;top:${head + tabs + 2}px;bottom:${foot + 2}px"><div class="lpc">${spec.body}</div></div>`;
  inner += `<div class="lp-foot" style="left:${pad}px;right:${pad}px;bottom:${b ? 9 : 6}px;height:${foot - (b ? 16 : 10)}px">${spec.foot}</div>`;
  return plateHTML(r[0], r[1], w, hh, chamfer(w, hh, b ? 12 : 8, "tl tr"), { panel: "left", inner, cls: "paperbody", attrs: `id="lp"` });
}
const kv = (k, v, cls = "", icn = "") => `<div class="kv"><span class="k">${icn}${k}</span><span class="v ${cls}">${v}</span></div>`;
const term = (t, k) => `<span class="term" data-term="${k}">${t}</span>`;

function countrySpec() {
  const nb = [["hun", "Венгрия", "$212", "+48"], ["rou", "Румыния", "$351", "+36"], ["bgr", "Болгария", "$101", "+21"], ["hrv", "Хорватия", "$82", "−34"], ["bih", "Босния и Герцеговина", "$27", "+8"], ["kos", "Косово", "$10", "−81"]];
  return {
    title: SRB.name, sub: `${SRB.form} · президент ${SRB.leader}`, flag: "srb",
    figs: [["71-е", "ВВП в мире · ▲ 2 места"], ["3-я из 8", "стабильность в регионе"], ["58-е", "сила армии в мире"]],
    tabs: ["Обзор", "Общество", `Решения <span class="badge">3</span>`, "Нации"], active: 0,
    body: `<div class="sec"><h3>Сравнение с соседями<span class="r">ВВП, млрд · отношения</span></h3>
      ${nb.map(n => `<div class="kv" data-btn><span class="k"><img src="img/flags/${n[0]}.png" class="kflag">${n[1]}</span><span class="v" style="min-width:56px;text-align:right;color:var(--paper-dim)">${n[2]}</span><span class="v ${n[3].startsWith("−") ? "neg" : ""}" style="min-width:40px;text-align:right">${n[3]}</span></div>`).join("")}</div>
      <div class="sec"><h3>Внутренний прогноз<span class="r">держится</span></h3>
      <div class="meter"><i style="width:64%"></i><b style="left:62%"></b></div>
      <p class="note" style="margin:8px 2px 0">При нынешних условиях стабильность идёт к 62 %. Тянут вниз: ${term("инфляция", "inflation")} (−1,2), безработица (−0,6). Держат: рост ВВП (+0,9), законы (+0,4).</p></div>`,
    foot: `<div class="btn" data-btn>${ico("laws", 16)}Законы</div><div class="btn primary" data-btn>${ico("decisions", 18)}Решения · 3</div>`,
  };
}
function regionSpec() {
  const bl = (ic, n, lv, cost, days) => {
    const tip = dyn(T(`Построить: ${n}`, `<p>Уровень ${lv} → ${lv + 1}. Стоит ${cost}, ${days} дней.</p><p><b>Shift</b>+щелчок или ПКМ — во всех 14 своих регионах, где можно: <b>$1,3 млрд</b> всего, по столько же уровней. Отменить можно до следующего действия.</p>`, "ЛКМ — здесь · Shift/ПКМ — везде · <kbd>Ctrl</kbd>+<kbd>Z</kbd>"));
    return `<div class="kv"><span class="k">${ico(ic, 18)}${n} <span style="color:var(--paper-faint)">ур. ${lv}</span></span>
      <span style="display:flex;gap:3px"><span class="btn chip" data-btn data-tip="${tip}" data-rc="build">+1</span><span class="btn chip" data-btn data-tip="${tip}" data-rc="build">×3</span><span class="btn chip" data-btn data-tip="${dyn(T("Сколько хватит денег", `<p>Строит, пока казна не опустится до запаса на 8 недель: здесь ${n.toLowerCase()} ×5 за $480 млн.</p>`, "Shift/ПКМ — во всех регионах"))}" data-rc="build">всё</span></span></div>`;
  };
  return {
    title: "Шумадия", sub: "Шумадия и Поморавье · Сербия · 7 провинций", icon: "region",
    figs: [["1,93 млн", "население · 3-й регион"], ["$14,9 млрд", "ВВП · 20 % страны"], ["66 %", "стабильность"]],
    tabs: ["Обзор", "Население", "Стройка", "Набор"], active: 0,
    body: `<div class="kv" data-btn style="background:rgba(239,228,200,.05)"><span class="k"><img src="img/flags/srb.png" class="kflag">Сербия</span><span class="v">к стране ›</span></div>
      <div class="sec" style="margin-top:10px"><h3>Регион</h3>
      ${kv(term("Радикализм", "radicalism"), "2,4 %", "", ico("unrest", 16))}${kv("Рельеф", "холмы, реки", "", ico("terrain", 16))}${kv("Казармы", "2 · набор до 8 дивизий", "", ico("building_barracks", 16))}</div>
      <div class="sec"><h3>Стройка<span class="r">Shift или ПКМ — во всех регионах</span></h3>
      ${bl("building_civilianindustry", "Гражданская промышленность", 4, "$96 млн", 180)}${bl("building_infrastructure", "Инфраструктура", 3, "$64 млн", 120)}${bl("building_armsindustry", "Военный завод", 1, "$140 млн", 240)}</div>`,
    foot: `<div class="btn" data-btn>${ico("province", 16)}Провинции</div><div class="btn primary" id="recruit" data-btn data-rc="recruit" data-act="qa" data-tip="${dyn(T("Набрать", `<p>Окошко «сколько»: 25 / 50 / 75 / 100 % (A S D F, пока окошко в фокусе), ползунок, поле, чип «Везде».</p><p><b>Shift</b>+щелчок или ПКМ — во всех 6 регионах с казармами, доля та же: <b>22 дивизии, $1,01 млрд</b>.</p>`, "ЛКМ — здесь · Shift/ПКМ — везде"))}">${ico("manpower", 18)}Набрать</div>`,
  };
}
function armySpec() {
  const divs = [["1-я механизированная", .98, "Пехота + БМП"], ["2-я механизированная", .97, "Пехота + БМП"], ["3-я пехотная", .95, "Пехота"], ["4-я пехотная", .96, "Пехота"], ["63-я парашютная", 1, "Десант"], ["Смешанная артиллерийская", .93, "Артиллерия"]];
  return {
    title: "1-я армия", sub: "ген. Д. Петрович · осторожный · группа 6", flag: "srb",
    figs: [["12", "дивизий · 18 400"], ["96 %", "сила"], ["88 %", "организация"]],
    tabs: ["Состав", "Приказы", "Снабжение", "Генерал"], active: 0,
    body: `<div class="sec"><h3>Приказ</h3><div class="kv sel"><span class="k">${gl("march", 16, "color:var(--accent)")}Марш: Белград → Ниш</span><span class="v">4 дн.</span></div>
      <p class="note" style="margin:6px 2px 0">ПКМ по карте — марш · <b>Shift</b>+ПКМ — точка пути · <b>Ctrl</b>+ПКМ — поддержать · <b>Alt</b>+ПКМ — отделить часть · ЛКМ по карте — карточка у курсора, панель остаётся</p></div>
      <div class="sec"><h3>Дивизии<span class="r">перетащите на жетон, чтобы перевести</span></h3>
      ${divs.map(d => `<div class="kv" data-btn><span class="k">${ico("military", 16)}${d[0]} <span style="color:var(--paper-faint)">${d[2]}</span></span><span class="v" style="display:flex;align-items:center;gap:8px"><span class="meter" style="width:60px;height:5px"><i style="width:${d[1] * 100}%"></i></span>${Math.round(d[1] * 100)} %</span></div>`).join("")}</div>`,
    foot: `<div class="btn" data-btn data-tip="${dyn(T("Пополам", "", "<kbd>Ctrl</kbd>+<kbd>X</kbd>"))}">Пополам <span class="k">Ctrl X</span></div><div class="btn" data-btn data-tip="${dyn(T("Объединить с выбранными", "", "<kbd>G</kbd>"))}">Объединить <span class="k">G</span></div><div class="btn primary" data-btn data-tip="${dyn(T("Отделить часть…", `<p>Сначала куда (щелчок по карте), потом сколько: 25 / 50 / 75 / 100 % (A S D F) или число дивизий.</p>`, "<kbd>Alt</kbd>+ПКМ по карте"))}">${ico("hq_detach", 18)}Отделить…</div>`,
  };
}

// ---------------------------------------------------------------- Budget: a full-screen ledger
function budgetInit() {
  S.budget = {
    rev: [
      { n: "НДС", ic: "taxes", v: 20, base: 268, min: 10, max: 30, rec: 20, last: 20, unit: "%" },
      { n: "Соцвзносы", ic: "welfare", v: 37, base: 142, min: 20, max: 45, rec: 37, last: 37, unit: "%" },
      { n: "Подоходный налог", ic: "income", v: 10, base: 118, min: 5, max: 30, rec: 12, last: 10, unit: "%" },
      { n: "Налог на прибыль", ic: "industry", v: 15, base: 64, min: 5, max: 35, rec: 15, last: 15, unit: "%" },
      { n: "Пошлины и акцизы", ic: "trade", v: 6, base: 20, min: 0, max: 15, rec: 6, last: 6, unit: "%" },
    ],
    exp: [
      { n: "Армия", ic: "military", v: 2.1, base: 30, min: 1, max: 5, rec: 2, last: 2.1, unit: "% ВВП", army: true },
      { n: "Пенсии и пособия", ic: "welfare", v: 100, base: 205, min: 70, max: 130, rec: 100, last: 100, unit: "%" },
      { n: "Госаппарат", ic: "government", v: 100, base: 116, min: 70, max: 130, rec: 92, last: 100, unit: "%" },
      { n: "Здравоохранение", ic: "building_hospital", v: 100, base: 98, min: 70, max: 130, rec: 100, last: 100, unit: "%" },
      { n: "Инфраструктура", ic: "building_infrastructure", v: 100, base: 88, min: 70, max: 130, rec: 105, last: 100, unit: "%" },
      { n: "Образование", ic: "building_university", v: 100, base: 72, min: 70, max: 130, rec: 100, last: 100, unit: "%" },
      { n: "Обслуживание долга", ic: "coins", v: 100, base: 41, min: 100, max: 100, rec: 100, last: 100, unit: "%", locked: true },
    ],
  };
}
const money = it => it.base * it.v / (it.army ? 2.1 : it.unit === "%" && it.min >= 70 ? 100 : it.last);
function budgetSums() {
  const b = S.budget, rev = b.rev.reduce((s, i) => s + money(i), 0), exp = b.exp.reduce((s, i) => s + money(i), 0);
  return { rev, exp, bal: rev - exp };
}
const fmtM = v => (v < 0 ? "−" : "") + "$" + Math.abs(Math.round(v)).toLocaleString("ru-RU") + " млн";
const fmtVal = (it, side) => it.army ? (Math.round(it.v * 10) / 10).toString().replace(".", ",") + " % ВВП" : side === "rev" ? (Math.round(it.v * 10) / 10).toString().replace(".", ",") + " %" : Math.round(it.v) + " %";
function brow(it, side, i) {
  const key = side + ":" + i, focus = S.bfocus === key;
  const pct = (it.v - it.min) / ((it.max - it.min) || 1) * 100, rec = (it.rec - it.min) / ((it.max - it.min) || 1) * 100;
  let quick = "";
  if (focus && !it.locked) {
    const opts = it.army ? [["минимум", it.min], ["мирное время", 2], ["военное время", 3.5], ["максимум", it.max]]
      : side === "rev" ? [["−5 п. п.", "d-5"], ["+5 п. п.", "d5"], ["как в прошлом году", it.last], ["рекомендуемое", it.rec]]
        : [["минимум", it.min], ["рекомендуемое", it.rec], ["максимум", it.max]];
    quick = `<div class="bquick">${opts.map(p => `<span class="btn chip ${it.v === p[1] ? "on" : ""}" data-btn data-set="${key}:${p[1]}">${p[0]}</span>`).join("")}<span class="note" style="margin-left:auto">A S D F — четверти · Enter</span></div>`;
  }
  return `<div class="brow ${it.locked ? "locked" : ""} ${focus ? "focus" : ""}" data-bfocus="${key}"><span class="n">${ico(it.ic, 18)}${it.n}</span>
    <span class="sliderwrap"><input type="range" class="qs" data-btn data-sl="${key}" min="${it.min}" max="${it.max}" step="${it.army ? 0.1 : 1}" value="${it.v}" ${it.locked ? "disabled" : ""} style="--p:${pct}%"><span class="rec" style="left:calc(${rec}% * .94 + 3%)"></span></span>
    <span class="val">${fmtVal(it, side)}</span><span class="mon">${fmtM(money(it))}</span></div>${quick}`;
}
function budgetScreen() {
  if (!S.budget) budgetInit();
  const l = L(), r = l.full, w = r[2] - r[0], hh = r[3] - r[1];
  const b = S.budget, s = budgetSums(), yr = 3.12 + s.bal * 52 / 1000, debt = 52.3 + Math.max(0, -yr) / 75.2 * 100;
  const q = S.debtQ, amt = [0.23, 0.45, 0.68, 0.9][q];
  const inner = `<div class="fs-head"><div class="emb icon hdr">${ico("economy", 30, "t-eco")}</div><div class="tt"><h2>Экономика</h2><div>Бюджет на 2024 год · неделя 1 · все суммы в неделю</div></div>
      <div class="ftabs" style="position:relative;flex:1;margin:0 24px;height:34px">${["Бюджет", "Долг", "Рынок", "Стройка", "Центробанк"].map((t, i) => `<div class="ftab ${i === 0 ? "on" : ""}" data-btn>${t}</div>`).join("")}</div>
      <div class="hb" data-btn data-tip="CLOSE" data-act="close" data-key="Esc">${gl("close", 18)}</div></div>
    <div class="fs-body"><div class="lcol"><div class="sec"><h3>Доходы<span class="r" id="b-rev">${fmtM(s.rev)}</span></h3>${b.rev.map((it, i) => brow(it, "rev", i)).join("")}</div>
      <div class="sec"><h3>Расходы<span class="r" id="b-exp">${fmtM(s.exp)}</span></h3>${b.exp.map((it, i) => brow(it, "exp", i)).join("")}</div>
      <div class="sec" style="grid-column:1/-1"><h3>Куда идут деньги<span class="r">доля расходов</span></h3><div class="stack">${b.exp.map(it => { const m = money(it); return `<i style="flex:${m}" data-tip="${dyn(T(it.n, `<p>${fmtM(m)} в неделю · ${Math.round(m / s.exp * 100)} %</p>`, ""))}"><span>${it.n}</span><b>${Math.round(m / s.exp * 100)} %</b></i>`; }).join("")}</div></div>
      <div class="sec" style="grid-column:1/-1"><h3>Казна за полгода<span class="r">${SRB.treasury}</span></h3>${spark([3.9, 3.8, 3.75, 3.6, 3.62, 3.5, 3.41, 3.38, 3.3, 3.25, 3.2, 3.12], 1180, 150, "#e7b058")}</div></div>
      <div class="rcol"><div class="sec"><h3>${term("Баланс", "balance")}</h3><div class="bigfig ${s.bal < -0.5 ? "neg" : "pos"}" id="b-bal">${fmtM(s.bal)}</div>
        <div class="meter" style="height:10px;margin-top:8px"><i style="width:${Math.min(100, s.exp / Math.max(s.rev, s.exp) * 100)}%"></i><b style="left:${s.rev / Math.max(s.rev, s.exp) * 100}%"></b></div>
        <p class="note" style="margin-top:8px">Через год при этом бюджете: казна <b>${yr >= 0 ? "$" + yr.toFixed(2).replace(".", ",") + " млрд" : "пуста"}</b> · долг <b>${debt.toFixed(1).replace(".", ",")} % ВВП</b> · ${term("инфляция", "inflation")} <b>7,1 %</b></p></div>
        <div class="sec"><h3>Долг<span class="r">$39,3 млрд · 52,3 % ВВП</span></h3>
        <div class="btn wide" data-btn data-debt="3" data-tip="${dyn(T("Погасить всё, что можно", "<p>Гасит долг до запаса казны на 8 недель. Уже есть в окне долга (2b021679).</p>", ""))}">${ico("coins", 16)}Погасить всё, что можно · $0,9 млрд</div>
        <div style="display:flex;align-items:center;gap:6px;margin-top:8px">${["25 %", "50 %", "75 %", "100 %"].map((t, i) => `<span class="btn chip ${i === q ? "on" : ""}" data-btn data-debt="${i}">${t}</span>`).join("")}<span class="qfield" style="margin-left:auto">$${amt.toFixed(2).replace(".", ",")} млрд</span></div>
        <p class="note" style="margin-top:8px">После погашения: казна <b>$${(3.12 - amt).toFixed(2).replace(".", ",")} млрд</b> · долг <b>${(52.3 - amt / 75.2 * 100).toFixed(1).replace(".", ",")} % ВВП</b></p></div>
        <p class="note">Готовые значения стоят только у выбранной строки — одна полоса под ней, а не четыре кнопки у каждой. Щелчок по строке выбирает её.</p></div></div>
    <div class="fs-foot"><div class="btn" data-btn data-act="b-last">Как в прошлом году</div><div class="btn" data-btn data-act="b-rec">Рекомендуемое</div>
      <div class="btn primary" data-btn data-act="b-zero" data-rc="b-zero-tax" data-tip="${dyn(T("Свести в ноль", `<p>Подгоняет расходы (кроме армии и долга) так, чтобы баланс стал нулём. <b>Shift</b>+щелчок или ПКМ — то же налогами.</p>`, "ЛКМ — расходами · Shift/ПКМ — налогами"))}">${ico("balance", 18)}Свести в ноль</div></div>`;
  return `<div id="dim"></div>` + plateHTML(r[0], r[1], w, hh, chamfer(w, hh, 14, "tl tr"), { panel: "full", inner, cls: "paperbody fullscreen" });
}

// ---------------------------------------------------------------- popups: quick amount, plan bar, headline, news, province card
function qaBox(x, y, title, sub, opts, sel, cons, everywhere) {
  const l = L(), [w, hgt] = l.qa, b = big();
  const letters = ["A", "S", "D", "F"];
  const inner = `<div style="position:absolute;inset:${b ? "10px 12px" : "7px 9px"}">
    <div class="qt">${title}<span>${sub}</span></div>
    <div class="quarts">${opts.map((o, i) => `<div class="q ${i === sel ? "on" : ""}" data-btn data-q="${i}"><span class="lt">${letters[i]}</span>${o[0]}<small>${o[1]}</small></div>`).join("")}</div>
    <div class="qrow"><input type="range" class="qs" data-btn min="0" max="100" value="${(sel + 1) * 25}" style="--p:${(sel + 1) * 25}%"><span class="field">${opts[sel][2]}</span>${everywhere ? `<span class="btn chip" data-btn data-rc="recruit" data-tip="${dyn(T("Везде", `<p>${everywhere}</p><p>Видимая кнопка для телефона: долгое нажатие на кнопке действия — только подсказка.</p>`, ""))}">Везде</span>` : ""}</div>
    <div class="cons">${cons}</div><div class="holds">A S D F — доли · Esc — закрыть</div></div>`;
  return plateHTML(x, y, w, hgt + 14, chamfer(w, hgt + 14, 6, "tl br"), { pop: true, cls: "qa holdkeys", inner, attrs: `id="qa"` });
}
function planBar(shift) {
  const l = L(), r = l.pbar.slice(), dx = shift ? l.tshift - r[0] : 0; r[0] += dx; r[2] += dx;
  const b = (ic, name, key) => `<div class="pb" data-btn data-tip="${dyn(T(name, "", key ? `<kbd>${key}</kbd>` : ""))}">${ico(ic, 18)}${key ? `<span class="k">${key}</span>` : ""}</div>`;
  const inner = `<div class="pbar" style="position:absolute;inset:0">
    <span class="lbl">1-я армия · 12 див. · группа 6</span>
    ${b("army_halt", "Стоять", "H")}${b("plan_front", "Линия фронта и сплочённость", "Ctrl F")}${b("army_defend", "Оборона области", "Ctrl G")}${b("army_advance", "Переброска", "Ctrl B")}${b("plan_execute", "Исполнить план", "Shift H")}
    <span class="vr"></span>${b("army_withdraw", "Отменить все передвижения всех армий", "Ctrl Alt H")}</div>`;
  return plate(r, 6, "tl tr", { inner, cls: "pbarplate" });
}
function headline() {
  const r = L().headline, w = r[2] - r[0], hh = r[3] - r[1];
  const war = WAR();
  const inner = `<div class="headline" style="position:absolute;inset:${big() ? "12px 16px" : "8px 10px"}"><div class="kick"><span>Мировая новость · ${war ? "20 мая" : "14 марта"}</span><span class="pin ${S.hlPinned ? "on" : ""}">${gl("pin", 14)}${S.hlPinned ? "закреплена" : "щелчок — закрепить"}</span></div>
    <h3>${war ? "ЕС вводит санкции против Сербии" : "KFOR: +1 200 военных в Косово"}</h3><p>${war ? "Заморожены активы центробанка; торговля с ЕС −30 %." : "НАТО отвечает на мобилизацию Сербии. Напряжённость +4."}</p></div>${S.hlPinned ? "" : `<div class="headline"><div class="timer"><i></i></div></div>`}`;
  return plateHTML(r[0], r[1], w, hh, chamfer(w, hh, 8, "tl br"), { pop: true, panel: "headline", inner, attrs: `data-btn data-act="hlpin" data-topic="news:${war ? "eu-sanctions" : "kfor"}" data-tip="${dyn(T("Мировая новость", "<p>Карточка живёт 9 секунд игрового хода (на паузе стоит). Щелчок закрепляет её. Все новости — окно «Мир» › Последние новости (щелчок по показателю мира).</p>", "Щелчок — закрепить · Esc — закрыть"))}"` });
}
function newsPop() {
  const r = L().news, w = r[2] - r[0], hh = r[3] - r[1];
  const items = [["14 мар", "KFOR: +1 200 военных в Косово", "pin"], ["13 мар", "Хорватия объявила учения у Срема", ""], ["11 мар", "DEFCON 3: мобилизации на Балканах", ""], ["9 мар", "Венгрия и Сербия подписали газовый договор", ""], ["2 мар", "Цены на никель +18 % за месяц", ""], ["26 фев", "Выборы в Румынии: победа правых", ""], ["19 фев", "Перемирие в Газе сорвано", ""]];
  const inner = `<div style="position:absolute;inset:12px 14px"><div class="ftabs" style="position:relative;height:32px">${["Последние новости", "DEFCON", "Державы"].map((t, i) => `<div class="ftab ${i === 0 ? "on" : ""}" data-btn>${t}</div>`).join("")}</div>
    <div class="newslist">${items.map(n => `<div class="nrow" data-btn><span class="nd">${n[0]}</span><span class="nt">${n[1]}</span>${n[2] ? gl("pin", 14, "color:var(--paper)") : ""}</div>`).join("")}</div>
    <p class="note" style="margin-top:6px">Последние 20 новостей; закреплённые — сверху. Что ставит паузу, что даёт карточку, что строку тревоги, что только запись — Настройки › Сообщения.</p></div>`;
  return plateHTML(r[0], r[1], w, hh, chamfer(w, hh, 8, "bl br"), { pop: true, inner, attrs: `id="newspop"` });
}
function provinceCard(x, y, note) {
  const w = L().pcard, hgt = note ? 236 : 214;
  x = Math.min(x, L().W - w - 8); y = Math.min(y, L().H - hgt - 8);
  const inner = `<div class="pcard" style="position:absolute;inset:12px 14px"><h3>Крагуевац</h3><div class="sub">Провинция · Шумадия · Сербия</div>
    ${kv("Население", "293 тыс.")}${kv("Рельеф", "холмы · снабжение 100 %")}${kv("Постройки", "6 · завод «Застава»")}${kv("Дивизий здесь", "0")}
    ${note ? `<p class="note" style="margin:6px 0 0">${note}</p>` : `<div class="btn" data-btn style="width:100%;margin-top:8px">${ico("hq_new", 16)}Собрать сюда свободные армии</div>`}</div>`;
  return plateHTML(x, y, w, hgt, chamfer(w, hgt, 6, "tl br"), { pop: true, inner, attrs: `id="pcard"` });
}

// ---------------------------------------------------------------- map overlay (1080-basis coordinates): the map budget
const SERBIA = [[697, 572], [740, 563], [767, 577], [773, 607], [797, 633], [813, 640], [827, 673], [847, 687], [860, 713], [857, 740], [870, 760], [873, 793], [870, 827], [853, 840], [827, 847], [807, 857], [797, 807], [783, 780], [750, 773], [727, 773], [713, 760], [700, 740], [697, 707], [693, 680], [690, 650], [693, 607]];
const KOSOVO = [[727, 773], [750, 773], [783, 780], [797, 807], [807, 857], [790, 862], [770, 858], [745, 850], [725, 830], [716, 800]];
const SUMADIJA = [[728, 690], [762, 684], [795, 686], [818, 704], [823, 736], [800, 756], [760, 760], [733, 748], [722, 718]];
function counter(x, y, flag, num, { sel = false, edge = "#86745a", glyph = "" } = {}) {
  return `<g class="mglyph" transform="translate(${x - 24} ${y - 11})">${sel ? `<rect x="-4" y="-4" width="56" height="30" rx="3" fill="none" stroke="#6da7e6" stroke-width="2.5"/>` : ""}
    <rect width="48" height="22" rx="2" fill="#221d19" stroke="#090705" stroke-width="1"/><rect x="1.5" y="1.5" width="45" height="19" rx="1.5" fill="none" stroke="${edge}" stroke-width="1.2"/>
    <image href="img/flags/${flag}.png" x="5" y="5" width="18" height="12" preserveAspectRatio="none"/><text x="36" y="15.5" text-anchor="middle" font-size="13" font-weight="700" fill="#efe4c8" font-family="Segoe UI,sans-serif">${num}</text>${glyph}</g>`;
}
function overlay() {
  const P = a => a.map(p => p.join(",")).join(" ");
  let s = `<svg id="overlay" viewBox="0 0 1920 1080" preserveAspectRatio="none"><defs>
    <filter id="glow" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="3"/></filter>
    <pattern id="hatch" width="8" height="8" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="3" height="8" fill="rgba(201,79,85,.55)"/></pattern>
    <marker id="arrow" viewBox="0 0 10 10" refX="5" refY="5" markerWidth="5" markerHeight="5" orient="auto"><path d="M0 0 10 5 0 10z" fill="#6da7e6"/></marker></defs>`;
  // the Ukrainian front: one line and one counter per country per sector, not a wall of counters
  const fl = FRONT_LINE.map(p => p.join(" ")).join(" L");
  s += `<path d="M${fl}" fill="none" stroke="#120f0c" stroke-width="7" stroke-linejoin="round" opacity=".75"/><path d="M${fl}" fill="none" stroke="#c94f55" stroke-width="2.4" stroke-dasharray="10 5" stroke-linejoin="round"/>`;
  COUNTERS.forEach(c => { s += counter(c.at[0], c.at[1], c.f, c.n); });
  if (S.state === 2) s += `<polygon points="${P(SERBIA)}" fill="rgba(239,228,200,.10)" stroke="#efe4c8" stroke-width="5" opacity=".35" filter="url(#glow)"/><polygon points="${P(SERBIA)}" fill="none" stroke="#efe4c8" stroke-width="2"/>`;
  if (S.state === 3) s += `<polygon points="${P(SERBIA)}" fill="none" stroke="#efe4c8" stroke-width="1.5" stroke-dasharray="4 4" opacity=".7"/><polygon points="${P(SUMADIJA)}" fill="rgba(109,167,230,.28)" stroke="#6da7e6" stroke-width="5" opacity=".5" filter="url(#glow)"/><polygon points="${P(SUMADIJA)}" fill="rgba(109,167,230,.22)" stroke="#9cc8f2" stroke-width="2"/>`;
  if (S.state === 6 || WAR()) s += `<polygon points="${P(KOSOVO)}" fill="url(#hatch)" stroke="#c94f55" stroke-width="2.5"/>`;
  // alert marks: the same record as the alert row (rule 6)
  const list = S.state === 5 ? [] : alertList();
  list.forEach((a, i) => {
    if (!a.mk) return;
    const c = a.sev === "n" ? "#c94f55" : a.sev === "w" ? "#e7b058" : "#77c9e8";
    s += `<g class="mglyph" id="mk${i}" transform="translate(${a.mk[0]} ${a.mk[1]})"><circle class="mk-pulse" r="11" fill="none" stroke="${c}" stroke-width="2" style="transform-box:fill-box;transform-origin:center"/>
      <rect x="-6" y="-6" width="12" height="12" transform="rotate(45)" fill="#1c1916" stroke="${c}" stroke-width="2"/><circle r="2.5" fill="${c}"/></g>`;
  });
  if (S.state === 4) {
    const a = ARMIES[0].pos, t = S.route, mx = (a[0] + t[0]) / 2 + 18, my = (a[1] + t[1]) / 2 - 10;
    s += `<path d="M${a[0]} ${a[1]} Q${mx} ${my} ${t[0]} ${t[1]}" fill="none" stroke="#0b1a2e" stroke-width="7" opacity=".7"/>
      <path class="marching" d="M${a[0]} ${a[1]} Q${mx} ${my} ${t[0]} ${t[1]}" fill="none" stroke="#6da7e6" stroke-width="3.5" marker-end="url(#arrow)"/>
      <g transform="translate(${t[0] + 26} ${t[1] - 40})"><rect x="0" y="0" width="70" height="22" rx="2" fill="#221d19" stroke="#6da7e6"/><text x="35" y="15.5" text-anchor="middle" font-size="13" font-weight="700" fill="#efe4c8" font-family="Segoe UI,sans-serif">Ниш · 4 дн.</text></g>
      <path d="M${a[0]} ${a[1]} L${S.detach[0]} ${S.detach[1]}" fill="none" stroke="#e7b058" stroke-width="2.5" stroke-dasharray="5 5"/><circle cx="${S.detach[0]}" cy="${S.detach[1]}" r="5" fill="#e7b058" stroke="#1c1916" stroke-width="2"/>`;
  }
  // own counters: merged by front in war
  if (WAR()) {
    s += counter(770, 796, "srb", 22, { edge: "#c94f55" }) + counter(802, 832, "kos", 4) + counter(800, 742, "srb", 6, { edge: "#e7b058" }) + counter(704, 612, "srb", 8) + counter(748, 662, "srb", 4);
  } else {
    ARMIES.forEach((a, i) => {
      const sel = S.state === 4 && i === 0, edge = S.state === 6 && i < 2 ? "#e7b058" : sel ? "#6da7e6" : "#86745a";
      s += counter(a.pos[0], a.pos[1], "srb", a.d, { sel, edge });
    });
  }
  return s + "</svg>";
}

// ---------------------------------------------------------------- render
function render() {
  for (const k in DYN) delete DYN[k];
  dynId = 0;
  S.res = S.state === 7 ? 720 : 1080;
  F.className = "r" + S.res + (S.alt ? " altkeys" : "");
  const l = L(), st = S.state;
  const showBuildings = S.mode === "eco" || S.buildings;
  let h = `<svg width="0" height="0" style="position:absolute"><defs>
      <linearGradient id="rimG" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#a48d68"/><stop offset=".45" stop-color="#7a6649"/><stop offset="1" stop-color="#43382b"/></linearGradient>
      <linearGradient id="plateG" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#3a322a"/><stop offset="1" stop-color="#221d19"/></linearGradient>
      <linearGradient id="gloss2" x1="0" y1="0" x2=".6" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".25"/><stop offset=".45" stop-color="#fff" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity=".3"/></linearGradient></defs></svg>
    <div id="map" class="m-${S.mode} ${showBuildings ? "bld" : ""}" data-map></div><div id="vignette"></div>${overlay()}`;
  let panel = null;
  if (st === 2) panel = countrySpec();
  if (st === 3) panel = regionSpec();
  if (st === 4) panel = armySpec();
  h += corner() + datePlate();
  if (st !== 5) h += alertsPlate() + mapPlate() + bottomStrip(!!panel);
  else h += bottomStrip(false);
  if (panel) h += leftPanel(panel);
  if (st === 5) h += budgetScreen();
  if (st === 4) {
    h += planBar(true);
    const d = S.detach;
    if (S.qa !== false) h += qaBox(d[0] * K() - 150 * K(), d[1] * K() + 26 * K(), "Отделить к Кралеву", "Alt+ПКМ · из 12", [["25 %", "3 див.", "3"], ["50 %", "6 див.", "6"], ["75 %", "9 див.", "9"], ["100 %", "12 див.", "12"]], S.qaSel ?? 1, `Останется <b>6</b> · уйдут <b>6</b> · <b>3 дн.</b> · Enter`, "");
  }
  if (st === 3 && S.qa !== false) {
    const r = l.left, top = r[1] + (S.lpH || (r[3] - r[1]));
    h += qaBox(r[2] - 6 - l.qa[0], top - l.foot - l.qa[1] - 20, "Набрать · пехота ▾", "макс. 8", [["25 %", "2 див.", "2"], ["50 %", "4 див.", "4"], ["75 %", "6 див.", "6"], ["100 %", "8 див.", "8"]], S.qaSel ?? 1, `<b>−5,8 тыс.</b> призыва · <b>$184 млн</b> · <b>41 дн.</b> · Enter`, "Во всех 6 регионах с казармами, доля та же: 22 дивизии, $1,01 млрд.");
  }
  if (st === 6 || st === 8) h += headline();
  if (S.more) h += morePop();
  if (S.news) h += newsPop();
  if (S.pcard) h += provinceCard(S.pcard[0], S.pcard[1], st === 4 ? "Армия выбрана: щелчок по региону открыл карточку у курсора, панель армии осталась на месте." : "");
  h += `<div class="raillabel" id="raillabel" style="display:none"></div>`;
  F.innerHTML = h;
  F.querySelectorAll("[data-key]").forEach(el => {
    const k = document.createElement("span"); k.className = "kbd"; k.textContent = el.dataset.key;
    k.style.right = "-4px"; k.style.top = "-6px"; el.appendChild(k);
  });
  closeTips();
  // the left panel's height follows its content: measure once, then draw again
  const lp = document.getElementById("lp");
  if (lp && S.lpH === null) {
    const body = lp.querySelector(".lpc"), r = l.left;
    const need = body.offsetHeight + (big() ? 24 : 16) + l.head + l.tabs + l.foot + 8;
    S.lpH = Math.min(r[3] - r[1], Math.max(320, need));
    return render();
  }
  // the open outliner takes only what the 40-button budget leaves: 4 rows at most, none if nothing is left
  if (S.outliner) {
    const others = countButtons().filter(el => !el.closest(".outliner")).length;
    const cap = Math.max(0, Math.min(4, 40 - others));
    if (cap !== S.ocap) { S.ocap = cap; return render(); }
  }
  fit(); census();
  requestAnimationFrame(() => { fit(); census(); });
}

// ---------------------------------------------------------------- scale to window
function fit() {
  const st = document.getElementById("stage").getBoundingClientRect(), l = L();
  const k = SHOT ? 1 : Math.min((st.width - 24) / l.W, (st.height - 24) / l.H);
  F.style.transform = `scale(${k})`;
  F.dataset.k = k;
}
window.addEventListener("resize", fit);
const fk = () => parseFloat(F.dataset.k || 1);
function toFrame(cx, cy) { const r = F.getBoundingClientRect(), k = fk(); return [(cx - r.left) / k, (cy - r.top) / k]; }
function rectInFrame(el) { const r = el.getBoundingClientRect(), f = F.getBoundingClientRect(), k = fk(); return [(r.left - f.left) / k, (r.top - f.top) / k, (r.right - f.left) / k, (r.bottom - f.top) / k]; }

// ---------------------------------------------------------------- census: buttons, panels, repeats, map glyphs
function countButtons() {
  const l = L();
  return [...F.querySelectorAll("[data-btn]")].filter(el => {
    if (el.closest(".tip") || el.closest("#raillabel")) return false;
    const r = el.getBoundingClientRect(); if (!r.width || !r.height) return false;
    const fr = rectInFrame(el); return fr[2] > 0 && fr[3] > 0 && fr[0] < l.W && fr[1] < l.H;
  });
}
function census() {
  const l = L(), btns = countButtons();
  const panels = [...F.querySelectorAll("[data-panel]")];
  const rects = panels.map(p => [p.dataset.panel, rectInFrame(p)]);
  let overlaps = 0, out = 0;
  for (let i = 0; i < rects.length; i++) {
    const a = rects[i][1];
    if (a[0] < -0.5 || a[1] < -0.5 || a[2] > l.W + .5 || a[3] > l.H + .5) out++;
    for (let j = i + 1; j < rects.length; j++) {
      const b = rects[j][1];
      if (a[0] < b[2] - .5 && b[0] < a[2] - .5 && a[1] < b[3] - .5 && b[1] < a[3] - .5) {
        if (rects[i][0] === "corner" || rects[j][0] === "corner") {
          // the corner's bounding box is an L: test against its two arms and the shield
          const o = rects[i][0] === "corner" ? b : a, c = l.corner, cw = rects[i][0] === "corner" ? a[2] : b[2];
          const arms = [[0, 0, cw, c.H], [0, 0, c.RW, c.B], [l.shield[0], l.shield[1], l.shield[2], l.rank[3]]];
          if (!arms.some(m => o[0] < m[2] && m[0] < o[2] && o[1] < m[3] && m[1] < o[3])) continue;
        }
        overlaps++;
      }
    }
  }
  // "no news twice": the same fact (data-topic) on screen more than once
  const seen = {};
  F.querySelectorAll("[data-topic]").forEach(el => { const t = el.dataset.topic; if (!t.startsWith("cell:")) seen[t] = (seen[t] || 0) + 1; });
  const repeats = Object.entries(seen).filter(e => e[1] > 1).map(e => e[0]);
  const glyphs = F.querySelectorAll("#overlay .mglyph").length;
  const rest = [1, 6, 7, 8].includes(S.state);
  const okB = btns.length <= 40, okP = panels.length <= 6;
  document.getElementById("census").innerHTML =
    `${S.state === 1 || S.state === 7 ? "Покой" : "Состояние " + S.state} · ${l.W}×${l.H} · сводка ${S.outliner ? "открыта" : "сложена"} · кнопок <b class="${rest ? (okB ? "ok" : "bad") : ""}">${btns.length}</b>${rest ? " / 40" : ""} · панелей <b class="${rest ? (okP ? "ok" : "bad") : ""}">${panels.length}</b>${rest ? " / 6" : ""} · повторов <b class="${repeats.length ? "bad" : "ok"}">${repeats.length}</b> · знаков на карте <b class="${glyphs <= 20 ? "ok" : "bad"}">${glyphs}</b> / 20 · пересечений ${overlaps}${out ? " · за краем " + out : ""}`;
  window.__census = { state: S.state, res: S.res, outliner: S.outliner, buttons: btns.length, panels: panels.map(p => p.dataset.panel), repeats, glyphs, overlaps, out };
}

// ---------------------------------------------------------------- tooltips: nested, pin after 0.6 s, depth up to 4
const tips = [];
let tipShowTimer = null, tipGrace = null;
function tipContent(key) {
  const t = TIPS[key] || DYN[key];
  if (!t) return null;
  return `<h4>${t.title}</h4>${t.body || ""}${t.key ? `<div class="key">${t.key}</div>` : ""}`;
}
function termContent(k) { const t = TERMS[k]; return t ? `<h4>${t.t}</h4>${t.b}` : null; }
function openTip(level, anchor, html) {
  closeTips(level);
  if (level >= 4 || !html) return;
  const el = document.createElement("div");
  el.className = "tip"; el.dataset.level = level;
  el.innerHTML = html + `<span class="pinmark">${GLYPH.pin.replace("<svg", '<svg width="12" height="12"')}</span><div class="pinbar"><i></i></div>`;
  el.style.maxWidth = L().tipMax + "px";
  F.appendChild(el);
  const a = rectInFrame(anchor), l = L(), w = el.offsetWidth, h = el.offsetHeight;
  let x = a[0], y = a[3] + 6;
  if (level > 0 && tips[level - 1]) {
    const p = rectInFrame(tips[level - 1].el);
    x = p[2] + 6; y = Math.max(6, a[1] - 14);
    if (x + w > l.W - 6) x = Math.max(6, p[0] - w - 6);
  }
  if (x + w > l.W - 6) x = Math.max(6, l.W - 6 - w);
  if (y + h > l.H - 6) y = Math.max(6, a[1] - h - 6);
  if (y + h > l.H - 6) y = Math.max(6, l.H - 6 - h);
  if (a[0] > l.W * .7 && level === 0) x = Math.max(6, a[0] - w - 8), y = Math.min(Math.max(6, a[1]), l.H - h - 6);
  el.style.left = x + "px"; el.style.top = y + "px";
  const rec = { el, anchor, pinned: false };
  requestAnimationFrame(() => el.classList.add("grow"));
  rec.timer = setTimeout(() => { rec.pinned = true; el.classList.add("pinned"); }, 600);
  tips[level] = rec;
}
function closeTips(from = 0) {
  for (let i = tips.length - 1; i >= from; i--) { if (tips[i]) { clearTimeout(tips[i].timer); tips[i].el.remove(); } }
  tips.length = Math.min(tips.length, from);
}
function inside(el, x, y, m = 2) { const r = el.getBoundingClientRect(); return x >= r.left - m && x <= r.right + m && y >= r.top - m && y <= r.bottom + m; }
F.addEventListener("mouseover", e => {
  const term = e.target.closest(".term");
  const tipEl = e.target.closest(".tip");
  if (term && tipEl) {
    const lv = +tipEl.dataset.level;
    if (!tips[lv] || !tips[lv].pinned) return;
    if (tips[lv + 1] && tips[lv + 1].anchor === term) return;
    clearTimeout(tipShowTimer);
    tipShowTimer = setTimeout(() => openTip(lv + 1, term, termContent(term.dataset.term)), 180);
    return;
  }
  if (tipEl) return;
  showRailLabel(e.target.closest("[data-rail]"));
  const a = e.target.closest("[data-tip],.term");
  if (!a) return;
  if (tips[0] && tips[0].anchor === a) return;
  clearTimeout(tipShowTimer);
  tipShowTimer = setTimeout(() => {
    const html = a.classList.contains("term") ? termContent(a.dataset.term) : tipContent(a.dataset.tip);
    openTip(0, a, html);
  }, tips[0] ? 60 : 280);
});
document.addEventListener("mousemove", e => {
  if (!tips.length) return;
  let keep = -1;
  for (let i = 0; i < tips.length; i++) {
    const t = tips[i]; if (!t) continue;
    if (inside(t.el, e.clientX, e.clientY, 4) || inside(t.anchor, e.clientX, e.clientY, 8)) keep = i;
  }
  if (keep < tips.length - 1) {
    const victim = keep + 1, t = tips[victim];
    if (t && t.pinned) {
      if (!tipGrace) tipGrace = setTimeout(() => { tipGrace = null; closeTips(victim); }, 650);
    } else closeTips(victim);
  } else { clearTimeout(tipGrace); tipGrace = null; }
});
F.addEventListener("mouseleave", () => { setTimeout(() => { if (!document.querySelector("#frame:hover")) closeTips(); }, 400); showRailLabel(null); });

function showRailLabel(rail) {
  const lab = document.getElementById("raillabel");
  if (!lab) return;
  if (!rail) { lab.style.display = "none"; return; }
  const it = RAIL[+rail.dataset.rail], l = L(), r = l.rail[+rail.dataset.rail];
  const w = l.railLabel[1] - l.railLabel[0], hgt = r[3] - r[1] + (it.tip ? 18 : 0);
  lab.style.display = "block";
  lab.style.left = l.railLabel[0] + "px"; lab.style.top = r[1] + "px"; lab.style.width = w + "px"; lab.style.height = hgt + "px";
  lab.innerHTML = plateHTML(0, 0, w, hgt, chamfer(w, hgt, 6, "tr br"), { pop: true, inner: `<div style="position:absolute;inset:${big() ? "9px 14px" : "5px 9px"}"><div class="t">${it.n}<kbd>${it.key}</kbd></div><div class="s">${it.s}</div>${it.tip ? `<div class="s" style="color:var(--paper)">● ${it.tip}</div>` : ""}</div>` });
}

// ---------------------------------------------------------------- interaction
function setState(n) {
  S.state = n; S.pcard = null; S.qa = undefined; S.qaSel = 1; S.more = false; S.news = false; S.hlPinned = false; S.lpH = null; S.ocap = 4;
  S.outliner = n === 6 || (n === +(qs.get("state") || 1) && qs.get("outliner") === "1");
  S.day = n === 6 ? 73 : n === 8 ? 140 : 0;
  S.undo = n === 4 ? "Отделено к Кралеву: 6 див." : null;
  if (n !== 5) S.budget = null;
  document.querySelectorAll("#states button").forEach(b => b.classList.toggle("on", +b.dataset.state === n));
  render();
}
function showUndo(text) { S.undo = text; render(); }        // stays until the player's next action or Ctrl+Z; no timer
function flash(k) { S.flash = k; render(); setTimeout(() => { S.flash = null; render(); }, 650); }
function everywhere(rc) {
  if (rc === "recruit") return showUndo("Набор в 6 регионах: 22 див., $1,01 млрд");
  if (rc === "build") return showUndo("Стройка в 14 регионах: $1,3 млрд");
  if (rc === "b-zero-tax") return budgetZero(true);
}

F.addEventListener("click", e => {
  const t = e.target;
  if (e.shiftKey) { const rc = t.closest("[data-rc]"); if (rc) { everywhere(rc.dataset.rc); return; } }
  const sp = t.closest("[data-speed]"); if (sp) { S.speed = +sp.dataset.speed; render(); return; }
  const md = t.closest("[data-mode]"); if (md) { S.mode = md.dataset.mode; render(); return; }
  const mt = t.closest("[data-mtab]"); if (mt) { S.moreTab = mt.dataset.mtab; render(); return; }
  const ly = t.closest("[data-layer]"); if (ly) { if (ly.dataset.layer === "Мини-карта") S.mini = !S.mini; if (ly.dataset.layer === "Постройки") S.buildings = !S.buildings; render(); return; }
  const q = t.closest("[data-q]"); if (q) { S.qaSel = +q.dataset.q; render(); return; }
  const set = t.closest("[data-set]"); if (set) { budgetSet(set.dataset.set); return; }
  const db = t.closest("[data-debt]"); if (db) { S.debtQ = +db.dataset.debt; render(); return; }
  const bf = t.closest("[data-bfocus]"); if (bf && !t.closest("input")) { S.bfocus = bf.dataset.bfocus; render(); return; }
  const al = t.closest("[data-go]"); if (al) { setState(+al.dataset.go); return; }
  const a = t.closest("[data-act]");
  if (a) {
    const act = a.dataset.act;
    if (act === "country") return setState(S.state === 2 ? 1 : 2);
    if (act === "budget") return setState(S.state === 5 ? 1 : 5);
    if (act === "army") return setState(S.state === 4 ? 1 : 4);
    if (act === "close") return setState(1);
    if (act === "pause") { togglePause(); return; }
    if (act === "outliner") { S.outliner = !S.outliner; render(); return; }
    if (act === "undo") { S.undo = null; render(); return; }
    if (act === "qa") { S.qa = S.qa === false ? undefined : false; render(); return; }
    if (act === "flash") return flash(a.dataset.k);
    if (act === "more") { S.more = !S.more; S.news = false; render(); return; }
    if (act === "news") { S.news = !S.news; S.more = false; render(); return; }
    if (act === "hlpin") { S.hlPinned = !S.hlPinned; render(); return; }
    if (act === "b-zero") return budgetZero(false);
    if (act === "b-last" || act === "b-rec") { S.budget.rev.concat(S.budget.exp).forEach(i => { if (!i.locked) i.v = act === "b-last" ? i.last : i.rec; }); render(); return; }
  }
  if (t.closest("#pcard") || t.closest("#morepop") || t.closest("#newspop")) return;
  if (S.pcard || S.more || S.news) { S.pcard = null; S.more = false; S.news = false; render(); return; }
  if (t.hasAttribute("data-map") || t.id === "vignette") {
    const [x, y] = toFrame(e.clientX, e.clientY), k = K(), mx = x / k, my = y / k;
    if (document.getElementById("qa")) { S.qa = false; render(); return; }      // a map click closes the "how much" box
    if (S.state === 4) { S.pcard = [x + 6, y + 6]; render(); return; }          // armies selected: card at the cursor, panel stays
    if (S.state === 1 || S.state === 2 || S.state === 3) {
      if (pointIn([mx, my], SERBIA)) setState(3); else if (S.state !== 1) setState(1);
    }
  }
});
F.addEventListener("dblclick", e => { if (e.target.closest(".token")) flash("R7"); });
F.addEventListener("contextmenu", e => {
  e.preventDefault();
  const t = e.target;
  const rc = t.closest("[data-rc]");
  if (rc) return everywhere(rc.dataset.rc);
  if (t.closest(".arow")) { const r = t.closest(".arow"); r.style.opacity = .35; r.querySelector(".tx").innerHTML += " <em>· скрыто на 30 дней</em>"; return; }
  if (t.hasAttribute("data-map") || t.id === "vignette") {
    const [x, y] = toFrame(e.clientX, e.clientY), k = K();
    if (S.state === 4) {
      if (e.altKey) S.detach = [x / k, y / k]; else S.route = [x / k, y / k];
      render(); return;
    }
    S.pcard = [x + 6, y + 6]; render();
  }
});
F.addEventListener("input", e => {
  const sl = e.target.closest("[data-sl]"); if (!sl) return;
  const [side, i] = sl.dataset.sl.split(":"), it = S.budget[side][+i];
  it.v = parseFloat(sl.value); budgetRefresh();
});
F.addEventListener("change", e => { const sl = e.target.closest("[data-sl]"); if (sl) { S.bfocus = sl.dataset.sl; render(); } });
function budgetRefresh() {
  const s = budgetSums();
  S.budget.rev.concat(S.budget.exp).forEach((it, n) => {
    const side = n < S.budget.rev.length ? "rev" : "exp", i = side === "rev" ? n : n - S.budget.rev.length;
    const sl = F.querySelector(`[data-sl="${side}:${i}"]`); if (!sl) return;
    const row = sl.closest(".brow");
    row.querySelector(".val").textContent = fmtVal(it, side);
    row.querySelector(".mon").textContent = fmtM(money(it));
    sl.style.setProperty("--p", (it.v - it.min) / ((it.max - it.min) || 1) * 100 + "%");
  });
  F.querySelector("#b-rev").textContent = fmtM(s.rev); F.querySelector("#b-exp").textContent = fmtM(s.exp);
  const b = F.querySelector("#b-bal"); b.textContent = fmtM(s.bal); b.className = "bigfig " + (s.bal < -0.5 ? "neg" : "pos");
}
function budgetSet(spec) {
  const [side, i, v] = spec.split(":"), it = S.budget[side][+i];
  if (v.startsWith("d")) it.v = Math.min(it.max, Math.max(it.min, it.v + parseFloat(v.slice(1)))); else it.v = parseFloat(v);
  render();
}
function budgetZero(byTax) {
  const b = S.budget, s = budgetSums();
  if (Math.abs(s.bal) < .5) return;
  if (byTax) {
    const tot = b.rev.reduce((a, i) => a + money(i), 0), f = (tot - s.bal) / tot;
    b.rev.forEach(i => { i.v = Math.min(i.max, Math.round(i.v * f * 10) / 10); });
  } else {
    const adj = b.exp.filter(i => !i.locked && !i.army), tot = adj.reduce((a, i) => a + money(i), 0), f = (tot + s.bal) / tot;
    adj.forEach(i => { i.v = Math.max(i.min, Math.round(i.v * f * 10) / 10); });
  }
  render();
}
function pointIn(p, poly) {
  let c = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const [xi, yi] = poly[i], [xj, yj] = poly[j];
    if ((yi > p[1]) !== (yj > p[1]) && p[0] < (xj - xi) * (p[1] - yi) / (yj - yi) + xi) c = !c;
  }
  return c;
}
// alert row hover lights the map marker (rule 6)
F.addEventListener("mouseover", e => {
  const r = e.target.closest("[data-alert]");
  F.querySelectorAll("#overlay g[id^=mk]").forEach(g => g.setAttribute("transform", g.getAttribute("transform").replace(/ scale\([^)]*\)/, "")));
  if (r) { const g = F.querySelector("#mk" + r.dataset.alert); if (g) g.setAttribute("transform", g.getAttribute("transform") + " scale(1.5)"); }
});

// time
let ticker = null;
function togglePause() {
  S.paused = !S.paused; clearInterval(ticker);
  if (!S.paused) ticker = setInterval(() => { S.day++; const d = document.querySelector("#d1 .dt"); if (d) d.textContent = dateText(); }, [0, 1400, 900, 550, 300, 150][S.speed]);
  render();
}

// keys
document.addEventListener("keydown", e => {
  if (!document.getElementById("view-mock").classList.contains("on")) return;
  if (e.key === "Alt") { e.preventDefault(); if (!S.alt) { S.alt = true; F.classList.add("altkeys"); } return; }
  if (e.target.tagName === "INPUT" && e.target.type !== "range") return;
  const k = e.code;
  if (e.ctrlKey && k === "KeyZ") { if (S.undo) { S.undo = null; render(); } e.preventDefault(); return; }
  if (e.ctrlKey && k === "KeyK") { e.preventDefault(); return flash("R11"); }
  if (e.ctrlKey && /^Digit[6-90]$/.test(k)) { e.preventDefault(); return flash("R7"); }
  if (e.ctrlKey || e.metaKey) return;
  const qa = document.getElementById("qa");
  if (qa && ["KeyA", "KeyS", "KeyD", "KeyF"].includes(k)) { S.qaSel = ["KeyA", "KeyS", "KeyD", "KeyF"].indexOf(k); render(); e.preventDefault(); return; }
  if (qa && k === "Enter") { const n = (S.qaSel + 1) * 2; S.qa = false; showUndo(S.state === 3 ? `Набор: ${n} див. в Шумадии, $${n * 46} млн` : "Отделено к Кралеву: 6 див."); return; }
  if (k === "Space") { e.preventDefault(); togglePause(); return; }
  if (/^Digit[1-5]$/.test(k)) { S.speed = +k.slice(5); render(); return; }
  if (/^Digit[06-9]$/.test(k)) return flash("R7");
  if (k === "Equal" || k === "NumpadAdd") { S.speed = Math.min(5, S.speed + 1); render(); return; }
  if (k === "Minus" || k === "NumpadSubtract") { S.speed = Math.max(1, S.speed - 1); render(); return; }
  if (k === "Escape") {
    if (tips.some(t => t && t.pinned)) { closeTips(); return; }
    if (qa) { S.qa = false; render(); return; }
    if (S.pcard || S.more || S.news) { S.pcard = null; S.more = false; S.news = false; render(); return; }
    if (![1, 6, 7, 8].includes(S.state)) setState(1);
    return;
  }
  if (k === "KeyJ") { S.outliner = !S.outliner; render(); return; }
  if (k === "KeyB") { S.buildings = !S.buildings; render(); return; }
  if (k === "KeyM") { S.more = !S.more; render(); return; }
  if (k === "KeyG") return flash("R7");
  if (k === "KeyQ") return setState(S.state === 2 ? 1 : 2);
  const rail = { KeyE: "R2", KeyR: "R3", KeyT: "R4", KeyY: "R5", KeyU: "R6", KeyI: "R7", KeyO: "R8", KeyP: "R9", KeyL: "R10", Slash: "R11" }[k];
  if (rail === "R3") return setState(S.state === 5 ? 1 : 5);
  if (rail === "R7") return setState(S.state === 4 ? 1 : 4);
  if (rail) return flash(rail);
  const mode = { KeyZ: "pol", KeyX: "dip", KeyC: "eco", KeyV: "ter", KeyN: "war" }[k];
  if (mode) { S.mode = mode; render(); return; }
});
document.addEventListener("keyup", e => { if (e.key === "Alt") { S.alt = false; F.classList.remove("altkeys"); e.preventDefault(); } });
window.addEventListener("blur", () => { S.alt = false; F.classList.remove("altkeys"); });

// page chrome
document.querySelectorAll("#states button").forEach(b => b.addEventListener("click", () => setState(+b.dataset.state)));
document.querySelectorAll("#tabs button").forEach(b => b.addEventListener("click", () => {
  document.querySelectorAll("#tabs button").forEach(x => x.classList.toggle("on", x === b));
  document.querySelectorAll(".view").forEach(v => v.classList.toggle("on", v.id === "view-" + b.dataset.tab));
  document.getElementById("states").style.visibility = b.dataset.tab === "mock" ? "visible" : "hidden";
  document.getElementById("census").style.visibility = b.dataset.tab === "mock" ? "visible" : "hidden";
  if (b.dataset.tab === "mock") fit();
}));
const kb = s => s.replace(/(Пробел|Ctrl\+[0-9A-Z]|[A-Za-z0-9][A-Za-z0-9+.\/]*|[+\/.](?=\s|$)|−)/g, "<kbd>$1</kbd>");
document.getElementById("t-mouse").innerHTML = "<tr><th>Жест</th><th>Что делает</th></tr>" + MOUSE.map(r => `<tr class="${r[2] ? "chg" : ""}"><td>${r[0]}</td><td>${r[1]}</td></tr>`).join("");
document.getElementById("t-keys").innerHTML = "<tr><th>Клавиша</th><th>Что</th></tr>" + KEYS.map(r => `<tr><td>${kb(r[0])}</td><td>${r[1]}</td></tr>`).join("");
document.getElementById("t-touch").innerHTML = "<tr><th>Мышь и клавиатура</th><th>Касание</th></tr>" + TOUCH.map(r => `<tr><td>${r[0]}</td><td>${r[1]}</td></tr>`).join("");
const MSGN = { P: "пауза", K: "карточка", R: "строка тревоги", L: "только запись" };
document.getElementById("t-msg").innerHTML = "<tr><th>Событие</th><th>«Paradox» (по умолчанию)</th><th>«AoH: не останавливаться»</th><th>«Спокойно»</th></tr>" + MSG_EVENTS.map(r => `<tr><td>${r[0]}</td>${r.slice(1).map(v => `<td class="m-${v}">${MSGN[v]}</td>`).join("")}</tr>`).join("");

setState(+(qs.get("state") || 1));
window.addEventListener("load", () => { fit(); census(); });
if (qs.get("tab")) document.querySelector(`#tabs button[data-tab="${qs.get("tab")}"]`)?.click();
