// New campaign screen mockup: renders the frame for each state, tooltips, keys, census.
"use strict";

const F = document.getElementById("frame");
const qs = new URLSearchParams(location.search);
const SHOT = qs.has("shot");                           // ?shot: frame at 1:1, no page chrome (for screenshots)
if (SHOT) document.body.classList.add("shot");
const S = {
  state: 1, res: 1080, paused: true, speed: 2, mode: "pol", outliner: false,
  day: 0, alt: false, qa: null, undo: null, pcard: null, route: [826, 760], detach: [752, 752],
  budget: null, flash: null,
};
const L = () => LAY[S.res];
const K = () => (S.res === 1080 ? 1 : 2 / 3);          // map coordinates (1080 basis) -> frame
const DYN = {};                                        // dynamic tooltips registered during render

// ---------------------------------------------------------------- small helpers
const px = v => Math.round(v * 100) / 100 + "px";
const box = r => `left:${px(r[0])};top:${px(r[1])};width:${px(r[2] - r[0])};height:${px(r[3] - r[1])}`;
const ico = (n, s, cls = "") => `<i class="ico i-${n.replace(/_/g, "-")} ${cls}" style="--s:${s}px"></i>`;
const gl = (n, s, style = "") => `<span class="glyph" style="--s:${s}px;${style}">${GLYPH[n]}</span>`;
const sevGlyph = s => s === "n" ? `<span style="color:var(--neg)">●</span>` : s === "w" ? `<span style="color:var(--warn)">▲</span>` : `<span style="color:var(--info)">◆</span>`;
let dynId = 0;
const dyn = tip => { const k = "dyn" + (dynId++); DYN[k] = tip; return k; };

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

// A plate: shadow layer, clipped face, engraved line 4 px in, light bevel on top edges.
function plateHTML(x, y, w, h, P, { cls = "", panel = "", inner = "", pop = false, attrs = "" } = {}) {
  const clip = "polygon(" + P.map(p => `${p[0]}px ${p[1]}px`).join(",") + ")";
  const e = S.res === 1080 ? 4 : 3;
  const i1 = polyInset(P, e), i2 = polyInset(P, e + 1), o = polyInset(P, .5);
  let bevel = "";
  for (let i = 0; i < P.length; i++) {
    const a = P[i], b = P[(i + 1) % P.length];
    if (a[1] === b[1] && a[1] < h / 2) bevel += `<line x1="${a[0] + 1}" y1="1.5" x2="${b[0] - 1}" y2="1.5" stroke="rgba(255,236,200,.13)"/>`;
  }
  const svg = `<svg class="eng" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}"><polygon points="${pts(o)}" fill="none" stroke="#0e0c0a"/>${bevel}<polygon points="${pts(polyInset(P, 1.5))}" fill="none" stroke="rgba(239,228,200,.07)"/><polygon points="${pts(i1)}" fill="none" stroke="#090706"/><polygon points="${pts(i2)}" fill="none" stroke="#4a4135" stroke-opacity=".85"/></svg>`;
  return `<div class="plate ${pop ? "pop " : ""}${cls}" ${panel ? `data-panel="${panel}"` : ""} ${attrs} style="left:${x}px;top:${y}px;width:${w}px;height:${h}px"><div class="sh"><div class="pl" style="clip-path:${clip}"></div></div>${svg}${inner}</div>`;
}
const plate = (r, c, corners, o = {}) => plateHTML(r[0], r[1], r[2] - r[0], r[3] - r[1], chamfer(r[2] - r[0], r[3] - r[1], c, corners), o);

// ---------------------------------------------------------------- panel 1: corner
function corner() {
  const l = L(), c = l.corner, { S: Sh, W, H, RW, B } = c, ch = c.c;
  const P = [[0, 0], [W, 0], [W, H - ch], [W - ch, H], [Sh, H], [Sh, Sh], [RW, Sh], [RW, B - ch], [RW - ch, B], [0, B]];
  let h = plateHTML(0, 0, W, B, P, { panel: "corner" });
  // the corner node: the one rich detail of the screen
  const m = S.res === 1080 ? 6 : 4, s2 = Sh - m;
  const dia = (x, y) => `<rect x="${x - 3}" y="${y - 3}" width="6" height="6" transform="rotate(45 ${x} ${y})" fill="#3a332b" stroke="#8b7d63"/>`;
  h += `<svg class="abs" style="left:0;top:0;pointer-events:none" width="${Sh}" height="${Sh}">
    <defs><radialGradient id="boss" cx="50%" cy="42%" r="60%"><stop offset="0" stop-color="#3a332b"/><stop offset="1" stop-color="#1c1916"/></radialGradient></defs>
    <polygon points="${m},${m} ${s2},${m} ${s2},${s2 - 8} ${s2 - 8},${s2} ${m},${s2}" fill="url(#boss)" stroke="#0b0907"/>
    <polygon points="${m + 2},${m + 2} ${s2 - 2},${m + 2} ${s2 - 2},${s2 - 9} ${s2 - 9},${s2 - 2} ${m + 2},${s2 - 2}" fill="none" stroke="#6b5f4c"/>
    ${dia(m, m)}${dia(s2, m)}${dia(m, s2)}${dia(s2 - 4, s2 - 4)}</svg>`;
  const f = l.flag;
  h += `<div class="shield ${S.state === 2 ? "on" : ""}" data-btn data-tip="U1" data-key="Home" data-act="country" style="${box([0, 0, Sh, Sh])}">
    <div class="flagwell" style="${box(f)}"><img src="img/flags/srb.png" alt=""></div>
    <div class="rank" style="${box(l.rank)}">№ ${SRB.rank}</div></div>`;
  // resource cells: one long well with engraved separators
  const c1 = l.cells[0], c7 = l.cells[6];
  h += `<div class="well cellsbed" style="${box([c1[0], c1[1], c7[2], c1[3]])}"></div>`;
  const crisis = S.state === 6;
  const cells = [
    { k: "C1", ic: "treasury", t: "t-eco", v: crisis ? "$1,41<small>млрд</small>" : "$3,12<small>млрд</small>", d: crisis ? `<span class="dn">▼ −1,02 млрд/нед.</span>` : `<span class="dn">▼ ${SRB.balance}/нед.</span>`, bad: crisis, act: "budget" },
    { k: "C2", ic: "gdp", t: "t-eco", v: "$75,2<small>млрд</small>", d: `<span class="up">▲ ${SRB.growth}</span> в год` },
    { k: "C3", ic: "public_debt", t: "t-eco", v: SRB.debt, d: `цены <span class="wn">▲ ${SRB.infl}</span>`, warn: true },
    { k: "C4", ic: "stability", t: "t-soc", v: crisis ? "59 %" : SRB.stab, d: crisis ? `<span class="dn">▼</span> война 63 %` : `война ${SRB.wsup}` },
    { k: "C5", ic: "manpower", t: "t-mil", v: crisis ? "121<small>тыс.</small>" : "182<small>тыс.</small>", d: `<span class="up">▲ ${SRB.manG}</span>/день` },
    { k: "C6", ic: "victory", t: "t-state", v: SRB.pc, d: `<span class="up">▲ ${SRB.pcG}</span>/нед.` },
    { k: "C7", xp: true },
  ];
  cells.forEach((cl, i) => {
    const r = l.cells[i], w = r[2] - r[0];
    const on = (cl.k === "C1" && S.state === 5);
    let inner;
    if (cl.xp) {
      const sz = S.res === 1080 ? 15 : 12;
      inner = `<div class="xp" style="left:${l.cellIx}px;top:${S.res === 1080 ? 13 : 8}px;opacity:.5">
        <span>${ico("military", sz, "t-mil")}0</span><span>${ico("air", sz, "t-mil")}0</span><span>${ico("navy", sz, "t-mil")}0</span></div>`;
    } else {
      inner = `<span style="position:absolute;left:${l.cellIx}px;top:${l.cellIy}px">${ico(cl.ic, l.cellIcon, cl.t)}</span><div class="v" style="left:${l.cellVx}px;top:${l.cellVy}px">${cl.v}${S.res === 720 ? (cl.bad ? ' <span class="arr" style="color:var(--neg)">▼</span>' : "") : ""}</div>
        <div class="d" style="left:${l.cellVx}px;top:${l.cellDy}px">${cl.d}</div>`;
    }
    h += `<div class="cell ${cl.bad ? "bad" : ""} ${cl.xp ? "dim" : ""} ${on ? "on" : ""}" data-btn data-tip="${cl.k}" ${cl.act ? `data-act="${cl.act}"` : ""} style="${box([r[0], r[1], r[2], r[3]])}">
      ${inner}${i < 6 ? '<span class="sep"></span>' : ""}</div>`;
  });
  // rail
  const openRail = { 2: "R1", 3: null, 4: "R7", 5: "R3" }[S.state];
  RAIL.forEach((it, i) => {
    const r = l.rail[i];
    let badge = "";
    if (it.badge === "dot") badge = `<span class="badge dot" style="right:${S.res === 1080 ? 9 : 5}px;top:${S.res === 1080 ? 9 : 5}px;background:radial-gradient(circle at 35% 30%,#c3eaf6,#4c98b8)"></span>`;
    else if (it.badge) badge = `<span class="badge" style="right:${S.res === 1080 ? 3 : 1}px;top:${S.res === 1080 ? 3 : 1}px">${it.badge}</span>`;
    if (it.k === "R9" && S.state === 6) badge = "";
    const act = { R1: "country", R3: "budget", R7: "army" }[it.k] || "flash";
    h += `<div class="rail ${openRail === it.k || S.flash === it.k ? "on" : ""}" data-btn data-rail="${i}" data-key="${it.key}" data-act="${act}" data-k="${it.k}" style="${box(r)}">${ico(it.ic, l.railIcon, it.t || "")}${badge}</div>`;
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
  const l = L(), r = l.date;
  let h = plate(r, S.res === 1080 ? 8 : 6, "bl", { panel: "date" });
  h += `<div class="date ${S.paused ? "paused" : ""}" id="d1" data-tip="D1" style="${box(l.d1)}">${dateText()}<small class="p">${S.paused ? "пауза" : "00:00"}</small></div>`;
  const gs = S.res === 1080 ? 18 : 14;
  h += `<div class="pp well btnwell ${S.paused ? "paused" : ""}" data-btn data-tip="D2" data-key="Пробел" data-act="pause" style="${box(l.d2)}">${gl(S.paused ? "play" : "pause", gs)}</div>`;
  const [mw, mh, st] = l.mark;
  let marks = "";
  for (let i = 0; i < 5; i++) marks += `<i data-speed="${i + 1}" class="${i < S.speed ? "lit" : ""} ${i + 1 === S.speed ? "cur" : ""}" style="left:${i * st}px;width:${mw}px;height:${mh}px"></i>`;
  h += `<div class="speed ${S.paused ? "paused" : ""}" data-btn data-tip="D3" data-key="1–5" style="${box(l.d3)}">${marks}</div>`;
  const crisis = S.state === 6, lv = crisis ? 3 : 5, ten = crisis ? 91 : 79;
  let lamps = ""; for (let i = 5; i >= 1; i--) lamps += `<i class="${i === lv ? "l" + lv : ""}"></i>`;
  h += `<div class="world well ${crisis ? "pulse" : ""}" data-btn data-tip="${crisis ? dyn(T("Мир", `<table><tr><td><span class="term" data-term="defcon">DEFCON</span></td><td class="warn">3 · готовность</td></tr><tr><td><span class="term" data-term="tension">Напряжённость</span></td><td class="neg">91 · на грани</td></tr></table><p>Ступень поднялась из-за мобилизаций на Балканах. Показатель пульсировал один раз; тревогой он станет, только если появится решение.</p>`, "Щелчок — окно «Мир»")) : "D4"}" style="${box(l.d4)}">
    <div class="dc"><b>DEFCON ${lv}</b><div class="lamps">${lamps}</div></div>
    <div class="tn"><b><span>${S.res === 1080 ? "НАПР." : ""}</span><em style="color:${crisis ? "var(--neg)" : "var(--warn)"}">${ten}</em></b><div class="bar"><i style="width:${ten}%"></i></div></div></div>`;
  h += `<div class="rule-v" style="left:${l.dv}px;top:${r[1] + 8}px;height:${r[3] - r[1] - 16}px"></div>`;
  const full = false;
  h += `<div class="menu well btnwell" data-btn data-tip="D5" data-key="Esc" style="${box(l.d5)}">${gl("menu", S.res === 1080 ? 26 : 20)}${full ? '<span class="badge red cnt">4</span>' : ""}</div>`;
  return h;
}

// ---------------------------------------------------------------- panel 3: alerts + "Слежу"
function alertsPlate() {
  const l = L(), a = l.alerts, list = S.state === 6 ? ALERTS_CRISIS : ALERTS_REST;
  const rows = list.length, tabY = a.y + rows * a.row + 4;
  const open = S.outliner, bottom = open ? a.open : tabY + a.tab + (S.res === 1080 ? 4 : 2);
  let h = plate([a.x1, a.y, a.x2, bottom], S.res === 1080 ? 8 : 6, "bl", { panel: "alerts" });
  list.forEach((al, i) => {
    const y = a.y + i * a.row, k = dyn(al.tip);
    h += `<div class="arow ${al.next ? "next" : ""} ${al.more ? "more" : ""}" data-btn data-tip="${k}" data-alert="${i}" ${al.go ? `data-go="${al.go}"` : ""} style="left:${a.x1}px;top:${y}px;width:${a.x2 - a.x1}px;height:${a.row}px">
      <span style="position:absolute;left:${a.ix}px;top:${(a.row - a.icon) / 2}px">${ico(al.ic, a.icon, al.sev === "n" ? "t-neg" : al.sev === "w" ? "t-warn" : "t-info")}</span>
      <span class="tx" style="left:${a.tx1}px;width:${a.tx2 - a.tx1}px;top:${(a.row - 18) / 2}px">${al.tx}</span>
      <span class="st" style="left:${a.dot - 4}px;top:${(a.row - 10) / 2}px;width:10px">${sevGlyph(al.sev)}</span></div>`;
  });
  h += `<div class="rule-h" style="left:${a.x1 + 10}px;top:${tabY - 3}px;width:${a.x2 - a.x1 - 20}px"></div>`;
  h += `<div class="ostab ${open ? "open" : ""}" data-btn data-tip="O1" data-key="J" data-act="outliner" style="left:${a.x1 + 4}px;top:${tabY}px;width:${a.x2 - a.x1 - 8}px;height:${a.tab}px">${gl("eye", S.res === 1080 ? 16 : 13)}Слежу · 7<span class="ar">▸</span></div>`;
  if (open) h += outliner(a.x1 + 10, tabY + a.tab + 4, a.x2 - a.x1 - 20, bottom - (tabY + a.tab + 12));
  return h;
}
function outliner(x, y, w, hgt) {
  const dot = c => `<span class="dot" style="background:${c};box-shadow:0 0 4px ${c}"></span>`;
  const row = (ic, n, v, extra = "", t = "") => `<div class="orow" data-tip="${dyn(T(n, `<p>${v}</p>`, "Щелчок — к месту · ПКМ — открепить"))}">${ico(ic, 16, t)}<span class="n">${n}</span>${extra}<span class="v">${v}</span></div>`;
  return `<div class="outliner" style="left:${x}px;top:${y}px;width:${w}px;height:${hgt}px">
    <h4>Стройка</h4>${row("alert_construction", "Очередь пуста", "0 площадок", dot("var(--info)"), "t-info")}
    <h4>Наука</h4>${row("research", "Ничего не изучается", "—", dot("var(--info)"), "t-state")}
    <h4>Производство</h4>${row("industry", "Сталь", "88 %", `<span class="mini"><i style="width:88%"></i></span>${dot("var(--warn)")}`, "t-eco")}${row("good_lithium", "Аккумуляторы", "94 %", `<span class="mini"><i style="width:94%"></i></span>`, "t-eco")}
    <h4>Рынок</h4>${row("good_nickel", "Никель", "$16 900 <span style='color:var(--neg)'>▲</span>", "", "t-eco")}${row("energy", "Газ", "$34,1 <span style='color:var(--pos)'>▼</span>", "", "t-eco")}
    <h4>Флоты</h4>${row("navy", "Речная флотилия", "Нови-Сад · 6", "", "t-mil")}
    <h4>Закреплённое</h4>${row("region", "Косово", "напряжение 74", dot("var(--neg)"), "t-neg")}${row("military", "1-я армия", "Белград", "", "t-mil")}
  </div>`;
}

// ---------------------------------------------------------------- panel 4: map plate
function mapPlate() {
  const l = L(), mini = S.res === 1080;
  const r = mini ? l.mapPlate : l.mapPlateNoMini;
  let h = plate(r, S.res === 1080 ? 8 : 6, "tl", { panel: "map" });
  const dy = mini ? 0 : (S.res === 1080 ? r[1] + 8 - l.modes[0][1] : 0);
  const g = l.modeGlyph;
  const cur = S.mode;
  MODES.forEach((m, i) => {
    const rr = l.modes[i].slice(); rr[1] += dy; rr[3] += dy;
    const inner = m.g ? gl(m.g, g) : ico(m.ic, g, m.t || "");
    h += `<div class="mode ${cur === m.cls ? "on" : ""}" data-btn data-tip="${m.k}" data-key="${m.key}" data-mode="${m.cls}" style="${box(rr)}">${inner}</div>`;
  });
  const m6 = l.m6.slice(), m7 = l.m7.slice(); m6[1] += dy; m6[3] += dy; m7[1] += dy; m7[3] += dy;
  h += `<div class="mode sq" data-btn data-tip="M6" data-key="M" style="${box(m6)}">${gl("modes", g)}</div>`;
  h += `<div class="mode sq" data-btn data-tip="M7" style="${box(m7)}">${gl("layers", g)}</div>`;
  if (mini) {
    const m8 = l.m8, w = m8[2] - m8[0], hh = m8[3] - m8[1];
    // viewport of the main view inside the minimap crop (see tools/build_assets.py)
    const vx = (280 - 140) / 1416 * w, vw = 1010 / 1416 * w, vy = (395 - 260) / 708 * hh, vh = 568 / 708 * hh;
    h += `<div class="minimap" data-btn data-tip="M8" style="${box(m8)}"><div class="vp" style="left:${vx}px;top:${vy}px;width:${vw}px;height:${vh}px"></div></div>`;
  }
  return h;
}

// ---------------------------------------------------------------- panel 5: army tokens
function tokens(shift) {
  const l = L();
  if (S.state === 5) return "";                       // wide panel: no room between it and the map plate (see notes)
  const dx = shift ? l.tshift : 0;
  const r0 = l.tokens[0], rm = l.tmore;
  let h = `<div class="abs" data-panel="tokens" style="left:${r0[0] + dx}px;top:${r0[1]}px;width:${l.tokens[2][2] - r0[0]}px;height:${r0[3] - r0[1]}px;pointer-events:none"></div>`;
  const big = S.res === 1080;
  ARMIES.forEach((a, i) => {
    const r = l.tokens[i].slice(); r[0] += dx; r[2] += dx;
    let st = a.st, g = a.g, edge = a.edge;
    if (S.state === 6 && i < 2) { st = i === 0 ? "Марш · Рашка, 3 дн." : "Марш · Бујановац, 2 дн."; g = "march"; edge = "#e7b058"; }
    if (S.state === 4 && i === 0) { st = "Марш · Ниш, 4 дн."; g = "march"; edge = "#6da7e6"; }
    const tip = dyn(T(a.n, `<table><tr><td>Командующий</td><td>${a.gen}</td></tr><tr><td>Дивизий</td><td>${a.d}</td></tr><tr><td>Где</td><td>${a.at}</td></tr><tr><td>Сила</td><td>${Math.round(a.str * 100)} %</td></tr><tr><td><span class="term" data-term="org">Организация</span></td><td>88 %</td></tr></table>`, "Щелчок — выбрать · двойной — камера к армии · <kbd>Ctrl</kbd>+щелчок — добавить к выбору · ПКМ — меню"));
    const fl = big ? [10, 9] : [8, 6], nm = big ? [42, 7] : [36, 4], stt = big ? [42, 27] : [36, 22];
    h += `<div class="token ${S.state === 4 && i === 0 ? "sel" : ""}" data-btn data-tip="${tip}" data-act="army" style="${box(r)};--edge:${edge}">
      <img src="img/flags/srb.png" alt="" style="left:${fl[0]}px;top:${fl[1]}px;${big ? "" : "width:18px;height:12px"}">
      <div class="nm" style="left:${nm[0]}px;top:${nm[1]}px">${a.n}<span>×${a.d}</span></div>
      <div class="stt" style="left:${stt[0]}px;top:${stt[1]}px">${gl(g, big ? 11 : 9)}${st}</div>
      <div class="str" style="left:${fl[0]}px;right:8px;bottom:${big ? 7 : 5}px"><i style="width:${a.str * 100}%"></i></div></div>`;
  });
  return h;
}

// ---------------------------------------------------------------- left work panel
function leftPanel(spec) {
  const l = L(), r = spec.wide ? l.wide : l.left, w = r[2] - r[0], hh = r[3] - r[1];
  const big = S.res === 1080, pad = big ? 14 : 10, head = l.head, tabs = l.tabs, foot = l.foot;
  let inner = `<div class="lp-head" style="left:${pad}px;right:${pad - 4}px;top:${big ? 10 : 6}px;height:${head - (big ? 14 : 8)}px">
    ${spec.emb}<div class="tt"><h2 style="${big ? "" : "font-size:18px;line-height:22px"}">${spec.title}</h2><div>${spec.sub}</div></div>
    <div class="hb" data-btn data-tip="BACK">${gl("back", 18)}</div><div class="hb ${spec.pinned ? "on" : ""}" data-btn data-tip="PIN">${gl("pin", 18)}</div><div class="hb" data-btn data-tip="CLOSE" data-act="close" data-key="Esc">${gl("close", 16)}</div></div>`;
  inner += `<div class="lp-tabs" style="left:${pad - 4}px;right:${pad - 4}px;top:${head}px;height:${tabs}px">${spec.tabs.map((t, i) => `<div class="tab ${i === spec.active ? "on" : ""}" data-btn>${t}</div>`).join("")}</div>`;
  inner += `<div class="lp-body" style="left:${pad - 4}px;right:${pad - 4}px;top:${head + tabs + 4}px;bottom:${foot + 2}px;${big ? "" : "font-size:12px;padding:8px 10px"}">${spec.body}</div>`;
  inner += `<div class="lp-foot" style="left:${pad}px;right:${pad}px;bottom:${big ? 8 : 5}px;height:${foot - (big ? 14 : 8)}px">${spec.foot}</div>`;
  return plateHTML(r[0], r[1], w, hh, chamfer(w, hh, big ? 12 : 8, "tl tr"), { panel: "left", inner, cls: "paperbody" });
}
const kv = (k, v, cls = "", icn = "") => `<div class="kv"><span class="k">${icn}${k}</span><span class="v ${cls}">${v}</span></div>`;
const term = (t, k) => `<span class="term" data-term="${k}">${t}</span>`;

function countrySpec() {
  return {
    title: SRB.name, sub: `${SRB.form} · № ${SRB.rank} в мире`, emb: `<div class="emb"><img src="img/flags/srb.png" alt=""></div>`,
    tabs: ["Обзор", "Общество", `Решения <span class="badge">3</span>`, "Нации"], active: 0,
    body: `<div class="leader"><div class="seal">АВ</div><div class="who"><b>${SRB.leader}</b><span>${SRB.leaderRole} · СНС · премьер ${SRB.premier}</span></div></div>
      <div class="sec"><h3>Страна</h3>
      ${kv("Столица", SRB.capital, "", ico("province", 16))}${kv("Население", SRB.pop, "", ico("population", 16))}${kv("Площадь", SRB.area, "", ico("region", 16))}
      ${kv("ВВП", SRB.gdp, "", ico("gdp", 16, "t-eco"))}${kv("ВВП на душу", "$11 360", "", ico("income", 16, "t-eco"))}
      ${kv(term("Стабильность", "stability"), SRB.stab, "", ico("stability", 16, "t-soc"))}${kv(term("Поддержка войны", "wsup"), SRB.wsup, "", ico("wars", 16, "t-mil"))}
      ${kv("Дивизий", "28 · 41 тыс.", "", ico("military", 16, "t-mil"))}${kv("Флот", "речная флотилия · 6", "", ico("navy", 16, "t-mil"))}</div>
      <div class="sec"><h3>Внутренний прогноз<span class="r">держится</span></h3>
      <div style="display:flex;justify-content:space-between;font-size:13px;color:var(--paper-dim);margin:2px 2px 6px"><span>Сейчас <b style="color:var(--paper)">64 %</b></span><span>При нынешних условиях <b style="color:var(--paper)">62 %</b></span></div>
      <div class="meter"><i style="width:64%"></i><b style="left:62%"></b></div>
      <p class="note" style="margin:8px 2px 0">Тянут вниз: ${term("инфляция", "inflation")} 7,6 % (−1,2), безработица 9,4 % (−0,6). Держат: рост ВВП (+0,9), законы (+0,4).</p></div>
      <div class="sec"><h3>Соседи</h3>${["hun Венгрия · дружба +48", "rou Румыния · дружба +36", "bgr Болгария · +21", "mkd Северная Македония · +30", "mne Черногория · +12", "bih Босния и Герцеговина · +8", "hrv Хорватия · соперник −34", "kos Косово · противостояние −81"].map(s => { const [c, ...t] = s.split(" "); const tx = t.join(" "); const neg = tx.includes("−"); return `<div class="kv" data-btn><span class="k"><img src="img/flags/${c}.png" style="width:21px;height:14px;box-shadow:0 0 0 1px #000">${tx.split(" · ")[0]}</span><span class="v ${neg ? "neg" : ""}">${tx.split(" · ")[1]}</span></div>`; }).join("")}</div>`,
    foot: `<div class="btn" data-btn>${ico("laws", 16)}Законы</div><div class="btn" data-btn>${ico("agenda", 16)}Курс</div><div class="btn primary" data-btn>${ico("decisions", 18)}Решения · 3</div>`,
    pinned: false,
  };
}

function regionSpec() {
  const bl = (ic, n, lv, cost, days) => {
    const tip = dyn(T(`Построить: ${n}`, `<p>Уровень ${lv} → ${lv + 1}. Стоит ${cost}, ${days} дней.</p><p>ПКМ — во всех 14 своих регионах, где можно: <b>$1,3 млрд</b>, по столько же уровней. Полоса отмены 8 секунд.</p>`, "ЛКМ — здесь · ПКМ — сделать везде"));
    return `<div class="kv"><span class="k">${ico(ic, 18, "t-eco")}${n} <span style="color:var(--paper-faint)">ур. ${lv}</span></span>
      <span style="display:flex;gap:3px"><span class="btn chip" data-btn data-tip="${tip}" data-rc="build">+1</span><span class="btn chip" data-btn data-tip="${tip}" data-rc="build">×3</span><span class="btn chip" data-btn data-tip="${dyn(T("Сколько хватит денег", `<p>Строит, пока казна не опустится до запаса на 8 недель: здесь ${n.toLowerCase()} ×5 за $480 млн.</p>`, "ПКМ — во всех регионах"))}" data-rc="build">всё</span></span></div>`;
  };
  return {
    title: "Шумадия", sub: "Шумадия и Поморавье · регион Сербии · 7 провинций",
    emb: `<div class="emb icon">${ico("region", 26, "t-soc")}</div>`,
    tabs: ["Обзор", "Население", "Стройка", "Набор"], active: 0,
    body: `<div class="kv" data-btn style="background:rgba(109,167,230,.08);box-shadow:inset 0 0 0 1px rgba(109,167,230,.35)"><span class="k"><img src="img/flags/srb.png" style="width:24px;height:16px;box-shadow:0 0 0 1px #000">Сербия</span><span class="v" style="color:var(--accent)">к стране ›</span></div>
      <div class="sec" style="margin-top:10px"><h3>Регион</h3>
      ${kv("Население", "1,93 млн", "", ico("population", 16))}${kv("ВВП", "$14,9 млрд", "", ico("gdp", 16, "t-eco"))}${kv(term("Стабильность", "stability"), "66 %", "", ico("stability", 16, "t-soc"))}
      ${kv(term("Радикализм", "radicalism"), "2,4 %", "", ico("unrest", 16))}${kv("Рельеф", "холмы, реки", "", ico("terrain", 16))}${kv("Города", "Крагуевац, Чачак, Ягодина +4", "", ico("urbanisation", 16))}
      ${kv("Казармы", "2 · набор до 8 дивизий", "", ico("building_barracks", 16, "t-mil"))}</div>
      <div class="sec"><h3>Стройка<span class="r">ПКМ — во всех регионах</span></h3>
      ${bl("building_civilianindustry", "Гражданская промышленность", 4, "$96 млн", 180)}${bl("building_infrastructure", "Инфраструктура", 3, "$64 млн", 120)}${bl("building_armsindustry", "Военный завод", 1, "$140 млн", 240)}${bl("building_university", "Университет", 1, "$72 млн", 300)}</div>
      <div class="sec"><h3>Провинции<span class="r">7</span></h3>${[["Крагуевац", "293 тыс.", "холмы"], ["Чачак", "158 тыс.", "холмы"], ["Крушевац", "152 тыс.", "долина"], ["Кралево", "149 тыс.", "горы"], ["Ягодина", "112 тыс.", "долина"], ["Горни-Милановац", "41 тыс.", "горы"], ["Аранджеловац", "44 тыс.", "холмы"]].map(p => `<div class="kv" data-btn><span class="k">${ico("province", 14)}${p[0]} <span style="color:var(--paper-faint)">${p[2]}</span></span><span class="v">${p[1]}</span></div>`).join("")}</div>`,
    foot: `<div class="btn" data-btn>${ico("province", 16)}Перенести столицу</div><div class="btn primary" id="recruit" data-btn data-rc="recruit" data-act="qa" data-tip="${dyn(T("Набрать", `<p>Открывает окошко «сколько» у кнопки: 25 / 50 / 75 / 100 % (A S D F), ползунок, поле. 100 % — максимум, который позволяют призыв, деньги и казармы региона.</p><p>ПКМ — то же во всех выбранных регионах или во всех своих с казармами; доля та же.</p>`, "ЛКМ — здесь · ПКМ — во всех регионах"))}">${ico("manpower", 18)}Набрать</div>`,
  };
}

function armySpec() {
  const divs = [["1-я механизированная", .98, "Пехота + БМП"], ["2-я механизированная", .97, "Пехота + БМП"], ["3-я пехотная", .95, "Пехота"], ["4-я пехотная", .96, "Пехота"], ["63-я парашютная", 1, "Десант"], ["Смешанная артиллерийская", .93, "Артиллерия"]];
  return {
    title: "1-я армия", sub: "ген. Д. Петрович · осторожный · Белград",
    emb: `<div class="emb"><img src="img/flags/srb.png" alt=""></div>`,
    tabs: ["Состав", "Приказы", "Снабжение", "Генерал"], active: 0,
    body: `<div class="sec"><h3>Соединение<span class="r">12 дивизий · 18 400</span></h3>
      ${kv("Сила", "96 %", "", ico("strength", 16, "t-mil"))}${kv(term("Организация", "org"), "88 %", "", ico("organisation", 16, "t-mil"))}${kv("Снабжение", "100 %", "", ico("supply", 16, "t-mil"))}${kv("Опыт", "0 · новобранцы", "", ico("victory", 16))}</div>
      <div class="sec"><h3>Приказ</h3><div class="kv" style="background:rgba(109,167,230,.08)"><span class="k">${gl("march", 16, "color:var(--accent)")}Марш: Белград → Ниш</span><span class="v">4 дн.</span></div>
      <p class="note" style="margin:6px 2px 0">ПКМ по карте — марш всех выбранных · <b>Shift</b>+ПКМ — точка пути · <b>Ctrl</b>+ПКМ — поддержать · <b>Alt</b>+ПКМ — отделить часть туда</p></div>
      <div class="sec"><h3>Дивизии<span class="r">перетащите на жетон, чтобы перевести</span></h3>
      ${divs.map(d => `<div class="kv" data-btn><span class="k">${ico("military", 16, "t-mil")}${d[0]} <span style="color:var(--paper-faint)">${d[2]}</span></span><span class="v" style="display:flex;align-items:center;gap:8px"><span class="meter" style="width:60px;height:5px"><i style="width:${d[1] * 100}%"></i></span>${Math.round(d[1] * 100)} %</span></div>`).join("")}
      <div class="kv" data-btn><span class="k" style="color:var(--paper-faint)">ещё 6 дивизий ▾</span></div></div>`,
    foot: `<div class="btn" data-btn data-tip="${dyn(T("Пополам", "", "<kbd>Ctrl</kbd>+<kbd>X</kbd>"))}">Пополам <span class="k">Ctrl X</span></div><div class="btn" data-btn data-tip="${dyn(T("Объединить с выбранными", "", "<kbd>Ctrl</kbd>+<kbd>K</kbd>"))}">Объединить</div><div class="btn primary" data-btn data-tip="${dyn(T("Отделить часть…", `<p>Сначала куда (щелчок по карте), потом сколько: 25 / 50 / 75 / 100 % (A S D F) или число дивизий.</p>`, "<kbd>Alt</kbd>+ПКМ по карте"))}">${ico("hq_detach", 18)}Отделить часть…</div>`,
  };
}

// budget model
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
      { n: "Обслуживание долга", ic: "public_debt", v: 100, base: 41, min: 100, max: 100, rec: 100, last: 100, unit: "%", locked: true },
    ],
  };
}
const money = it => it.base * it.v / (it.army ? 2.1 : it.unit === "%" && it.min >= 70 ? 100 : it.last);
function budgetSums() {
  const b = S.budget, rev = b.rev.reduce((s, i) => s + money(i), 0), exp = b.exp.reduce((s, i) => s + money(i), 0);
  return { rev, exp, bal: rev - exp };
}
const fmtM = v => (v < 0 ? "−" : "") + "$" + Math.abs(Math.round(v)).toLocaleString("ru-RU") + " млн";
const fmtV = it => (Math.round(it.v * 10) / 10).toString().replace(".", ",") + (it.unit === "% ВВП" ? " %" : " %");
function brow(it, side, i) {
  const pct = (it.v - it.min) / ((it.max - it.min) || 1) * 100, rec = (it.rec - it.min) / ((it.max - it.min) || 1) * 100;
  const presets = it.locked ? `<span class="note">обязательная статья</span>` : it.army
    ? [["мин", it.min], ["мир", 2], ["война", 3.5], ["макс", it.max]].map(p => `<span class="btn chip ${it.v === p[1] ? "on" : ""}" data-btn data-set="${side}:${i}:${p[1]}">${p[0]}</span>`).join("")
    : side === "rev"
      ? [["−5", "d-5"], ["+5", "d5"], ["прошлый год", it.last], ["рек.", it.rec]].map(p => `<span class="btn chip ${it.v === p[1] ? "on" : ""}" data-btn data-set="${side}:${i}:${p[1]}">${p[0]}</span>`).join("")
      : [["мин", it.min], ["рек.", it.rec], ["макс", it.max]].map(p => `<span class="btn chip ${it.v === p[1] ? "on" : ""}" data-btn data-set="${side}:${i}:${p[1]}">${p[0]}</span>`).join("");
  return `<div class="brow ${it.locked ? "locked" : ""}"><div class="top"><span class="n">${ico(it.ic, 18, side === "rev" ? "t-eco" : it.army ? "t-mil" : "")}${it.n}</span><span class="val">${it.army ? fmtV(it) + " ВВП" : side === "rev" ? fmtV(it) : Math.round(it.v) + " %"}</span><span class="mon">${fmtM(money(it))}</span></div>
    <div class="ctl"><span class="sliderwrap"><input type="range" class="qs" data-btn data-sl="${side}:${i}" min="${it.min}" max="${it.max}" step="${it.army ? 0.1 : 1}" value="${it.v}" ${it.locked ? "disabled" : ""} style="--p:${pct}%"><span class="rec" style="left:calc(${rec}% * .94 + 3%)"></span></span><span class="presets">${presets}</span></div></div>`;
}
function budgetSpec() {
  if (!S.budget) budgetInit();
  const b = S.budget, s = budgetSums(), yr = 3.12 + s.bal * 52 / 1000, debt = 52.3 + Math.max(0, -yr) / 75.2 * 100;
  return {
    wide: true, title: "Экономика", sub: "Бюджет на 2024 год · неделя 1 · все суммы в неделю",
    emb: `<div class="emb icon">${ico("economy", 26, "t-eco")}</div>`,
    tabs: ["Бюджет", "Долг", "Рынок", "Стройка", "Центробанк"], active: 0,
    body: `<div class="cols2"><div><div class="sec"><h3>Доходы<span class="r" id="b-rev">${fmtM(s.rev)}</span></h3>${b.rev.map((it, i) => brow(it, "rev", i)).join("")}</div>${debtBox()}</div>
      <div class="sec"><h3>Расходы<span class="r" id="b-exp">${fmtM(s.exp)}</span></h3>${b.exp.map((it, i) => brow(it, "exp", i)).join("")}</div></div>
      <div class="balance"><div><div class="note">${term("Баланс", "balance")}</div><div class="big ${s.bal < -0.5 ? "neg" : "pos"}" id="b-bal">${fmtM(s.bal)}</div></div>
      <div style="flex:1"><div class="meter" style="height:10px"><i style="width:${Math.min(100, s.exp / Math.max(s.rev, s.exp) * 100)}%;background:linear-gradient(90deg,#8a7d62,#c9c0a8)"></i><b style="left:${s.rev / Math.max(s.rev, s.exp) * 100}%"></b></div>
      <div class="note" style="margin-top:6px">После года при этом бюджете: казна <b>${yr >= 0 ? "$" + yr.toFixed(2).replace(".", ",") + " млрд" : "пуста"}</b> · долг <b>${debt.toFixed(1).replace(".", ",")} % ВВП</b> · ${term("инфляция", "inflation")} <b>7,1 %</b></div></div></div>`,
    foot: `<div class="btn" data-btn data-act="b-last">Как в прошлом году</div><div class="btn" data-btn data-act="b-rec">Рекомендуемое</div><div class="btn primary" data-btn data-act="b-zero" data-rc="b-zero-tax" data-tip="${dyn(T("Свести в ноль", `<p>ЛКМ подгоняет расходы (кроме армии и долга) так, чтобы баланс стал нулём. ПКМ — то же налогами.</p><p>Полоса отмены 8 секунд.</p>`, "ЛКМ — расходами · ПКМ — налогами"))}">${ico("balance", 18)}Свести в ноль</div>`,
  };
}

function debtBox() {
  const q = S.debtQ ?? 3, amt = [0.23, 0.45, 0.68, 0.9][q];
  return `<div class="sec"><h3>Долг<span class="r">$39,3 млрд · 52,3 % ВВП</span></h3>
    <div style="display:flex;gap:6px;margin:2px 0 8px"><span class="btn sm" data-btn data-debt="3" data-tip="${dyn(T("Погасить всё, что можно", "<p>Гасит долг до запаса казны на 8 недель. Уже есть в окне долга (2b021679); здесь та же кнопка рядом с бюджетом.</p>", ""))}">${ico("public_debt", 14, "t-eco")}Погасить всё, что можно · $0,9 млрд</span><span class="btn sm" data-btn>${ico("coins", 14, "t-eco")}Занять сколько дают · $2,4 млрд</span></div>
    <div style="display:flex;align-items:center;gap:6px">${["25 %", "50 %", "75 %", "100 %"].map((t, i) => `<span class="btn chip ${i === q ? "on" : ""}" data-btn data-debt="${i}">${t}</span>`).join("")}
      <span class="sliderwrap" style="margin:0 4px"><input type="range" class="qs" data-btn min="0" max="100" value="${(q + 1) * 25}" style="--p:${(q + 1) * 25}%"></span><span class="qfield">$${amt.toFixed(2).replace(".", ",")} млрд</span></div>
    <div class="note" style="margin-top:6px">После погашения: казна <b>$${(3.12 - amt).toFixed(2).replace(".", ",")} млрд</b> · долг <b>${(52.3 - amt / 75.2 * 100).toFixed(1).replace(".", ",")} % ВВП</b> · обслуживание <b>−$${Math.round(amt * 1.1)} млн/нед.</b></div></div>`;
}

// ---------------------------------------------------------------- popups: quick amount, planning bar, headline, undo, province card
function qaBox(x, y, title, sub, opts, sel, cons, tailSide) {
  const l = L(), [w, hgt] = l.qa;
  const letters = ["A", "S", "D", "F"];
  const inner = `<div style="position:absolute;inset:${S.res === 1080 ? "10px 12px" : "7px 9px"}">
    <div class="qt">${title}<span>${sub}</span></div>
    <div class="quarts">${opts.map((o, i) => `<div class="q ${i === sel ? "on" : ""}" data-btn data-q="${i}"><span class="lt">${letters[i]}</span>${o[0]}<small>${o[1]}</small></div>`).join("")}</div>
    <div class="qrow"><input type="range" class="qs" data-btn min="0" max="100" value="${(sel + 1) * 25}" style="--p:${(sel + 1) * 25}%"><span class="field">${opts[sel][2]}</span></div>
    <div class="cons">${cons}</div></div>`;
  return plateHTML(x, y, w, hgt + 10, chamfer(w, hgt + 10, 6, "tl br"), { pop: true, cls: "qa", inner, attrs: `id="qa"` });
}
function planBar(shift) {
  const l = L(), r = l.pbar.slice(), dx = shift ? l.tshift : 0; r[0] += dx; r[2] += dx;
  const b = (ic, name, key, g) => `<div class="pb" data-btn data-tip="${dyn(T(name, "", key ? `<kbd>${key}</kbd>` : ""))}">${g ? gl(g, 16) : ico(ic, 18, "t-mil")}${key ? `<span class="k">${key}</span>` : ""}</div>`;
  const inner = `<div class="pbar" style="position:absolute;inset:0">
    <span class="lbl">1 армия · 12 див.</span>
    ${b("army_halt", "Стоять", "H")}${b("plan_front", "Линия фронта и сплочённость", "Ctrl F")}${b("army_defend", "Оборона области", "Ctrl G")}${b("army_advance", "Переброска", "Ctrl B")}${b("plan_attack", "План наступления", "")}${b("plan_execute", "Исполнить план", "Shift H")}
    <span class="vr"></span>${b("army_withdraw", "Отменить все передвижения всех армий", "Ctrl Alt H")}</div>`;
  return plate(r, 6, "tl tr", { inner, cls: "pbarplate" });
}
function headline() {
  const r = L().headline;
  const inner = `<div class="headline" style="position:absolute;inset:10px 14px"><div class="kick"><span>Экстренный выпуск · мир</span><span style="color:var(--paper-faint)">9 с</span></div><h3>KFOR: +1 200 военных в Косово</h3><p>НАТО отвечает на мобилизацию Сербии. Напряжённость +4.</p></div><div class="headline"><div class="timer"><i></i></div></div>`;
  return plate(r, 8, "tl br", { pop: true, inner, attrs: `data-btn data-tip="${dyn(T("Карточка заголовка", "<p>Мировая новость — карточка на 9 секунд, а не строка тревоги (правило 5). Если новость касается игрока, после карточки остаётся строка с действием.</p>", "Щелчок — подробности"))}"` });
}
function undoStrip(text) {
  const r = L().undo;
  const inner = `<div class="undo" style="position:absolute;inset:0">${gl("undo", 16, "color:var(--accent)")}<span>${text}</span><span class="btn sm" data-btn data-act="undo">Отменить <span class="k">Ctrl Z</span></span></div><div class="undo"><div class="timer"><i></i></div></div>`;
  return plate(r, 6, "tl br", { pop: true, inner, attrs: `id="undo"` });
}
function provinceCard(x, y) {
  const w = L().pcard, hgt = 214;
  x = Math.min(x, L().W - w - 8); y = Math.min(y, L().H - hgt - 8);
  const inner = `<div class="pcard" style="position:absolute;inset:12px 14px"><h3>Крагуевац</h3><div class="sub">Провинция № 19 352 · Шумадия · Сербия</div>
    ${kv("Население", "293 тыс.")}${kv("Рельеф", "холмы")}${kv("Постройки", "6 · завод «Застава»")}${kv("Дивизий здесь", "0")}
    <div class="btn" data-btn style="width:100%;margin-top:8px">${ico("hq_new", 16)}Собрать сюда свободные армии</div></div>`;
  return plateHTML(x, y, w, hgt, chamfer(w, hgt, 6, "tl br"), { pop: true, inner, attrs: `id="pcard"` });
}

// ---------------------------------------------------------------- map overlay (1080-basis coordinates)
const SERBIA = [[697, 572], [740, 563], [767, 577], [773, 607], [797, 633], [813, 640], [827, 673], [847, 687], [860, 713], [857, 740], [870, 760], [873, 793], [870, 827], [853, 840], [827, 847], [807, 857], [797, 807], [783, 780], [750, 773], [727, 773], [713, 760], [700, 740], [697, 707], [693, 680], [690, 650], [693, 607]];
const KOSOVO = [[727, 773], [750, 773], [783, 780], [797, 807], [807, 857], [790, 862], [770, 858], [745, 850], [725, 830], [716, 800]];
const SUMADIJA = [[728, 690], [762, 684], [795, 686], [818, 704], [823, 736], [800, 756], [760, 760], [733, 748], [722, 718]];
function overlay() {
  const P = a => a.map(p => p.join(",")).join(" ");
  let s = `<svg id="overlay" viewBox="0 0 1920 1080" preserveAspectRatio="none"><defs>
    <filter id="glow" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="3"/></filter>
    <pattern id="hatch" width="8" height="8" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="3" height="8" fill="rgba(201,79,85,.55)"/></pattern>
    <marker id="arrow" viewBox="0 0 10 10" refX="5" refY="5" markerWidth="5" markerHeight="5" orient="auto"><path d="M0 0 10 5 0 10z" fill="#6da7e6"/></marker></defs>`;
  if (S.state === 2) s += `<polygon points="${P(SERBIA)}" fill="rgba(239,228,200,.10)" stroke="#efe4c8" stroke-width="5" opacity=".35" filter="url(#glow)"/><polygon points="${P(SERBIA)}" fill="none" stroke="#efe4c8" stroke-width="2"/>`;
  if (S.state === 3) s += `<polygon points="${P(SERBIA)}" fill="none" stroke="#efe4c8" stroke-width="1.5" stroke-dasharray="4 4" opacity=".7"/><polygon points="${P(SUMADIJA)}" fill="rgba(109,167,230,.28)" stroke="#6da7e6" stroke-width="5" opacity=".5" filter="url(#glow)"/><polygon points="${P(SUMADIJA)}" fill="rgba(109,167,230,.22)" stroke="#9cc8f2" stroke-width="2"/>`;
  if (S.state === 6) s += `<polygon points="${P(KOSOVO)}" fill="url(#hatch)" stroke="#c94f55" stroke-width="2.5"/>`;
  // alert markers: the same record as the alert row (rule 6)
  const list = S.state === 6 ? ALERTS_CRISIS : (S.state === 7 || S.state <= 5 ? ALERTS_REST : []);
  list.forEach((a, i) => {
    if (!a.mk) return;
    const c = a.sev === "n" ? "#c94f55" : a.sev === "w" ? "#e7b058" : "#77c9e8";
    s += `<g id="mk${i}" transform="translate(${a.mk[0]} ${a.mk[1]})"><circle class="mk-pulse" r="11" fill="none" stroke="${c}" stroke-width="2" style="transform-box:fill-box;transform-origin:center"/>
      <rect x="-6" y="-6" width="12" height="12" transform="rotate(45)" fill="#1c1916" stroke="${c}" stroke-width="2"/><circle r="2.5" fill="${c}"/></g>`;
  });
  if (S.state === 4) {
    const a = ARMIES[0].pos, t = S.route, mx = (a[0] + t[0]) / 2 + 18, my = (a[1] + t[1]) / 2 - 10;
    s += `<path d="M${a[0]} ${a[1]} Q${mx} ${my} ${t[0]} ${t[1]}" fill="none" stroke="#0b1a2e" stroke-width="7" opacity=".7"/>
      <path class="marching" d="M${a[0]} ${a[1]} Q${mx} ${my} ${t[0]} ${t[1]}" fill="none" stroke="#6da7e6" stroke-width="3.5" marker-end="url(#arrow)"/>
      <g transform="translate(${t[0] + 26} ${t[1] - 40})"><rect x="0" y="0" width="58" height="20" rx="3" fill="#1c1916" stroke="#6da7e6"/><text x="29" y="14" text-anchor="middle" font-size="12" font-weight="700" fill="#efe4c8" font-family="Segoe UI,sans-serif">Ниш · 4 дн.</text></g>
      <path d="M${a[0]} ${a[1]} L${S.detach[0]} ${S.detach[1]}" fill="none" stroke="#e7b058" stroke-width="2.5" stroke-dasharray="5 5"/><circle cx="${S.detach[0]}" cy="${S.detach[1]}" r="5" fill="#e7b058" stroke="#1c1916" stroke-width="2"/>`;
  }
  // army counters on the map (new style): flag, count, state edge
  ARMIES.forEach((a, i) => {
    const sel = S.state === 4 && i === 0, edge = S.state === 6 && i < 2 ? "#e7b058" : sel ? "#6da7e6" : "#c9c0a8";
    s += `<g transform="translate(${a.pos[0] - 22} ${a.pos[1] - 12})">${sel ? `<rect x="-4" y="-4" width="52" height="32" rx="5" fill="none" stroke="#6da7e6" stroke-width="2.5" opacity=".9"/>` : ""}
      <rect width="44" height="24" rx="3" fill="#1c1916" stroke="${edge}" stroke-width="1.5"/><image href="img/flags/srb.png" x="4" y="5" width="20" height="14" preserveAspectRatio="none"/>
      <text x="35" y="17" text-anchor="middle" font-size="12" font-weight="700" fill="#efe4c8" font-family="Segoe UI,sans-serif">${a.d}</text></g>`;
  });
  return s + "</svg>";
}

// ---------------------------------------------------------------- render
function render() {
  for (const k in DYN) delete DYN[k];
  dynId = 0;
  S.res = S.state === 7 ? 720 : 1080;
  F.className = "r" + S.res + (S.alt ? " altkeys" : "");
  const l = L();
  let h = `<div id="map" class="m-${S.mode}" data-map></div><div id="vignette"></div>${overlay()}`;
  const st = S.state;
  let panel = null;
  if (st === 2) panel = countrySpec();
  if (st === 3) panel = regionSpec();
  if (st === 4) panel = armySpec();
  if (st === 5) panel = budgetSpec();
  h += corner() + datePlate() + alertsPlate() + mapPlate() + tokens(!!panel);
  if (panel) h += leftPanel(panel);
  if (st === 4) {
    h += planBar(true);
    const d = S.detach;
    h += qaBox(d[0] * K() - 150 * K(), d[1] * K() + 26 * K(), "Отделить к Кралеву", "Alt+ПКМ · из 12", [["25 %", "3 див.", "3"], ["50 %", "6 див.", "6"], ["75 %", "9 див.", "9"], ["100 %", "12 див.", "12"]], S.qaSel ?? 1, `Останется <b>6</b> · уйдут <b>6</b> · <b>3 дн.</b> · Enter`);
  }
  if (st === 3 && S.qa !== false) {
    const r = l.left;
    h += qaBox(r[2] - 6 - l.qa[0], r[3] - l.foot - l.qa[1] - 14, "Набрать · пехота ▾", "макс. 8", [["25 %", "2 див.", "2"], ["50 %", "4 див.", "4"], ["75 %", "6 див.", "6"], ["100 %", "8 див.", "8"]], S.qaSel ?? 1, `<b>−5,8 тыс.</b> призыва · <b>$184 млн</b> · <b>41 дн.</b> · Enter`);
  }
  if (st === 6) { h += headline(); }
  if (S.undo) h += undoStrip(S.undo);
  if (S.pcard) h += provinceCard(S.pcard[0], S.pcard[1]);
  h += `<div class="raillabel" id="raillabel" style="display:none"></div>`;
  F.innerHTML = h;
  // Alt overlay: key letters on every button that has one
  F.querySelectorAll("[data-key]").forEach(el => {
    const k = document.createElement("span"); k.className = "kbd"; k.textContent = el.dataset.key;
    k.style.right = "-4px"; k.style.top = "-6px"; el.appendChild(k);
  });
  closeTips();
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

// ---------------------------------------------------------------- census: buttons and panels on screen
function census() {
  const l = L(), btns = [...F.querySelectorAll("[data-btn]")].filter(el => {
    if (el.closest(".tip") || el.closest("#raillabel")) return false;
    const r = el.getBoundingClientRect(); if (!r.width || !r.height) return false;
    const fr = rectInFrame(el); return fr[2] > 0 && fr[3] > 0 && fr[0] < l.W && fr[1] < l.H;
  });
  const panels = [...F.querySelectorAll("[data-panel]")];
  const rects = panels.map(p => [p.dataset.panel, rectInFrame(p)]);
  let overlaps = 0, out = 0;
  for (let i = 0; i < rects.length; i++) {
    const a = rects[i][1];
    if (a[0] < -0.5 || a[1] < -0.5 || a[2] > l.W + .5 || a[3] > l.H + .5) out++;
    for (let j = i + 1; j < rects.length; j++) {
      const b = rects[j][1];
      if (a[0] < b[2] - .5 && b[0] < a[2] - .5 && a[1] < b[3] - .5 && b[1] < a[3] - .5) {
        // the corner's bounding box is an L: test against its two arms instead
        if (rects[i][0] === "corner" || rects[j][0] === "corner") {
          const o = rects[i][0] === "corner" ? b : a, c = l.corner;
          const arms = [[0, 0, c.W, c.H], [0, 0, c.RW, c.B], [0, 0, c.S, c.S]];
          if (!arms.some(m => o[0] < m[2] && m[0] < o[2] && o[1] < m[3] && m[1] < o[3])) continue;
        }
        overlaps++;
      }
    }
  }
  const rest = S.state === 1 || S.state === 7;
  const okB = btns.length <= 40, okP = panels.length <= 6;
  document.getElementById("census").innerHTML =
    `${rest ? "Покой" : "Состояние " + S.state} · ${l.W}×${l.H} · кнопок <b class="${rest ? (okB ? "ok" : "bad") : ""}">${btns.length}</b>${rest ? " / 40" : ""} · панелей <b class="${rest ? (okP ? "ok" : "bad") : ""}">${panels.length}</b>${rest ? " / 6" : ""} · пересечений ${overlaps}${out ? " · за краем " + out : ""}`;
  window.__census = { state: S.state, res: S.res, buttons: btns.length, panels: panels.map(p => p.dataset.panel), overlaps, out };
}

// ---------------------------------------------------------------- tooltips: nested, pin after 0.6 s, depth up to 4
const tips = [];   // {el, anchor, pinned, timer}
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
    // a nested tooltip stands beside its parent, as in Paradox games, so the parent stays readable
    const p = rectInFrame(tips[level - 1].el);
    x = p[2] + 6; y = Math.max(6, a[1] - 14);
    if (x + w > l.W - 6) x = Math.max(6, p[0] - w - 6);
  }
  if (x + w > l.W - 6) x = Math.max(6, l.W - 6 - w);
  if (y + h > l.H - 6) y = Math.max(6, a[1] - h - 6);
  if (y + h > l.H - 6) y = Math.max(6, l.H - 6 - h);
  if (a[0] > l.W * .7 && level === 0) x = Math.max(6, a[0] - w - 8 + (a[2] - a[0]) * 0), y = Math.min(Math.max(6, a[1]), l.H - h - 6);
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
    if (!tips[lv] || !tips[lv].pinned) return;                  // terms open only from a pinned tooltip
    if (tips[lv + 1] && tips[lv + 1].anchor === term) return;
    clearTimeout(tipShowTimer);
    tipShowTimer = setTimeout(() => openTip(lv + 1, term, termContent(term.dataset.term)), 180);
    return;
  }
  if (tipEl) return;
  const rail = e.target.closest("[data-rail]");
  showRailLabel(rail);
  const a = e.target.closest("[data-tip],.term");
  if (!a) return;
  if (tips[0] && tips[0].anchor === a) return;
  if (tips[0] && tips[0].pinned && !a.closest(".tip")) {
    // moving to another element closes a pinned tooltip only when the pointer leaves it (grace below)
  }
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
  // deeper tooltips survive while the pointer is in any shallower pinned one only if it is over them
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
  lab.innerHTML = "";
  const w = l.railLabel[1] - l.railLabel[0], hgt = r[3] - r[1] + (it.tip ? 16 : 0);
  lab.style.display = "block";
  lab.style.left = l.railLabel[0] + "px"; lab.style.top = r[1] + "px"; lab.style.width = w + "px"; lab.style.height = hgt + "px";
  lab.innerHTML = plateHTML(0, 0, w, hgt, chamfer(w, hgt, 6, "tr br"), { pop: true, inner: `<div style="position:absolute;inset:${S.res === 1080 ? "8px 12px" : "4px 8px"}"><div class="t">${it.n}<kbd>${it.key}</kbd></div><div class="s">${it.s}</div>${it.tip ? `<div class="s" style="color:var(--paper)">● ${it.tip}</div>` : ""}</div>` });
}

// ---------------------------------------------------------------- interaction
function setState(n) {
  S.state = n; S.pcard = null; S.outliner = n === 6; S.qa = undefined; S.qaSel = 1;
  S.undo = n === 6 ? "Мобилизация в 9 регионах · $612 млн" : null;
  if (n !== 5) S.budget = null;
  document.querySelectorAll("#states button").forEach(b => b.classList.toggle("on", +b.dataset.state === n));
  render();
}
let undoTimer = null;
function showUndo(text) {
  S.undo = text; render();
  clearTimeout(undoTimer); undoTimer = setTimeout(() => { S.undo = null; render(); }, 8000);
}
function flashRail(k) { S.flash = k; render(); setTimeout(() => { S.flash = null; render(); }, 650); }

F.addEventListener("click", e => {
  const t = e.target;
  const sp = t.closest("[data-speed]"); if (sp) { S.speed = +sp.dataset.speed; render(); return; }
  const md = t.closest("[data-mode]"); if (md) { S.mode = md.dataset.mode; render(); return; }
  const q = t.closest("[data-q]"); if (q) { S.qaSel = +q.dataset.q; render(); return; }
  const set = t.closest("[data-set]"); if (set) { budgetSet(set.dataset.set); return; }
  const db = t.closest("[data-debt]"); if (db) { S.debtQ = +db.dataset.debt; render(); return; }
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
    if (act === "flash") return flashRail(a.dataset.k);
    if (act === "b-zero") return budgetZero(false);
    if (act === "b-last" || act === "b-rec") { S.budget.rev.concat(S.budget.exp).forEach(i => { if (!i.locked) i.v = act === "b-last" ? i.last : i.rec; }); render(); return; }
  }
  if (t.closest("#pcard")) return;
  if (S.pcard) { S.pcard = null; render(); return; }
  if (t.hasAttribute("data-map") || t.id === "vignette") {
    const [x, y] = toFrame(e.clientX, e.clientY), k = K(), mx = x / k, my = y / k;
    if (S.state === 1 || S.state === 2 || S.state === 3) {
      if (pointIn([mx, my], SERBIA)) setState(3); else if (S.state !== 1) setState(1);
    }
  }
});
F.addEventListener("dblclick", e => { if (e.target.closest(".token")) flashRail("R7"); });
F.addEventListener("contextmenu", e => {
  e.preventDefault();
  const t = e.target;
  const rc = t.closest("[data-rc]");
  if (rc) {
    if (rc.dataset.rc === "recruit") return showUndo("Набор в 6 регионах с казармами — 50 %, 22 дивизии, $1,01 млрд");
    if (rc.dataset.rc === "build") return showUndo("Сделано в 14 регионах — $1,3 млрд");
    if (rc.dataset.rc === "b-zero-tax") return budgetZero(true);
  }
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
F.addEventListener("change", e => { if (e.target.closest("[data-sl]")) render(); });
function budgetRefresh() {
  const s = budgetSums();
  S.budget.rev.concat(S.budget.exp).forEach((it, n) => {
    const side = n < S.budget.rev.length ? "rev" : "exp", i = side === "rev" ? n : n - S.budget.rev.length;
    const sl = F.querySelector(`[data-sl="${side}:${i}"]`); if (!sl) return;
    const row = sl.closest(".brow");
    row.querySelector(".val").textContent = it.army ? fmtV(it) + " ВВП" : side === "rev" ? fmtV(it) : Math.round(it.v) + " %";
    row.querySelector(".mon").textContent = fmtM(money(it));
    sl.style.setProperty("--p", (it.v - it.min) / ((it.max - it.min) || 1) * 100 + "%");
  });
  F.querySelector("#b-rev").textContent = fmtM(s.rev); F.querySelector("#b-exp").textContent = fmtM(s.exp);
  const b = F.querySelector("#b-bal"); b.textContent = fmtM(s.bal); b.className = "big " + (s.bal < -0.5 ? "neg" : "pos");
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
  showUndo(byTax ? "Бюджет сведён в ноль налогами" : "Бюджет сведён в ноль: расходы −5,7 %");
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
  F.querySelectorAll("#overlay g[id^=mk]").forEach(g => g.classList.remove("mk-hi"));
  if (r) { const g = F.querySelector("#mk" + r.dataset.alert); if (g) { g.classList.add("mk-hi"); g.setAttribute("transform", g.getAttribute("transform").replace(/ scale\([^)]*\)/, "") + " scale(1.5)"); } }
  else F.querySelectorAll("#overlay g[id^=mk]").forEach(g => g.setAttribute("transform", g.getAttribute("transform").replace(/ scale\([^)]*\)/, "")));
});

// time
let ticker = null;
function togglePause() {
  S.paused = !S.paused; clearInterval(ticker);
  if (!S.paused) ticker = setInterval(() => { S.day++; const d = document.getElementById("d1"); if (d) d.firstChild.textContent = dateText(); }, [0, 1400, 900, 550, 300, 150][S.speed]);
  render();
}

// keys
document.addEventListener("keydown", e => {
  if (!document.getElementById("view-mock").classList.contains("on")) return;
  if (e.key === "Alt") { e.preventDefault(); if (!S.alt) { S.alt = true; F.classList.add("altkeys"); } return; }
  if (e.target.tagName === "INPUT" && e.target.type !== "range") return;
  const k = e.code;
  if (e.ctrlKey && k === "KeyZ") { if (S.undo) { S.undo = null; render(); } e.preventDefault(); return; }
  if (e.ctrlKey && k === "KeyK") { e.preventDefault(); return flashRail("R11"); }
  if (e.ctrlKey || e.metaKey) return;
  const qa = document.getElementById("qa");
  if (qa && ["KeyA", "KeyS", "KeyD", "KeyF"].includes(k)) { S.qaSel = ["KeyA", "KeyS", "KeyD", "KeyF"].indexOf(k); render(); e.preventDefault(); return; }
  if (qa && k === "Enter") { const n = (S.qaSel + 1) * 2; S.qa = false; showUndo(S.state === 3 ? `Набор: ${n} див. в Шумадии, $${n * 46} млн` : "Отправлено к Кралеву: 6 див."); return; }
  if (k === "Space") { e.preventDefault(); togglePause(); return; }
  if (/^Digit[1-5]$/.test(k)) { S.speed = +k.slice(5); if (!S.paused) { S.paused = true; togglePause(); } else render(); return; }
  if (k === "Equal" || k === "NumpadAdd") { S.speed = Math.min(5, S.speed + 1); render(); return; }
  if (k === "Minus" || k === "NumpadSubtract") { S.speed = Math.max(1, S.speed - 1); render(); return; }
  if (k === "Escape") { if (tips.some(t => t && t.pinned)) { closeTips(); return; } if (S.pcard) { S.pcard = null; render(); return; } if (S.state !== 1 && S.state !== 7) setState(S.state === 6 ? 6 : 1); return; }
  if (k === "KeyJ") { S.outliner = !S.outliner; render(); return; }
  const rail = { KeyQ: "R1", KeyE: "R2", KeyR: "R3", KeyT: "R4", KeyY: "R5", KeyU: "R6", KeyI: "R7", KeyO: "R8", KeyP: "R9", KeyL: "R10", Slash: "R11", KeyM: null }[k];
  if (rail === "R1") return setState(S.state === 2 ? 1 : 2);
  if (rail === "R3") return setState(S.state === 5 ? 1 : 5);
  if (rail === "R7") return setState(S.state === 4 ? 1 : 4);
  if (rail) return flashRail(rail);
  const mode = { KeyZ: "pol", KeyX: "dip", KeyC: "eco", KeyV: "ter", KeyN: "pop" }[k];
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
const kb = s => s.replace(/(Пробел|[A-Za-z0-9][A-Za-z0-9+.\/]*|[+\/.](?=\s|$)|−)/g, "<kbd>$1</kbd>");
document.getElementById("t-mouse").innerHTML = "<tr><th>Жест</th><th>Что делает</th></tr>" + MOUSE.map(r => `<tr class="${r[2] ? "chg" : ""}"><td>${r[0]}</td><td>${r[1]}</td></tr>`).join("");
document.getElementById("t-keys").innerHTML = "<tr><th>Клавиша</th><th>Что</th></tr>" + KEYS.map(r => `<tr><td>${kb(r[0])}</td><td>${r[1]}</td></tr>`).join("");
document.getElementById("t-touch").innerHTML = "<tr><th>Мышь и клавиатура</th><th>Касание</th></tr>" + TOUCH.map(r => `<tr><td>${r[0]}</td><td>${r[1]}</td></tr>`).join("");

setState(+(qs.get("state") || 1));
window.addEventListener("load", () => { fit(); census(); });
if (qs.get("tab")) document.querySelector(`#tabs button[data-tab="${qs.get("tab")}"]`)?.click();
