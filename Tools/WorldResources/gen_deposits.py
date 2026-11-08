"""Generate game/history/states/2024/zz_world_resources.json from real 2024 producer shares.

Each good's shares add up to roughly 100 "share points" of world output; a country's points
are spread over the regions named for it, or over all its regions (farm goods by rural
population, fish by population, anything else evenly). The recipe in common/production
turns points into market units, so retuning a good's size is one weight there, not a
rewrite here.

Spec keys: "TAG" (spread over the country), "TAG:region,region" (spread over those regions),
or a bare region id.
"""
import json
import re
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "game" / "history" / "states" / "2024"
OUT = ROOT / "zz_world_resources.json"

# ---------------------------------------------------------------- region table
regions = {}      # tag -> list of (id, pop, urban)
owner = {}
for path in sorted(ROOT.glob("*.json")):
    tag = path.stem
    if not re.fullmatch(r"[A-Z][A-Z0-9]{2}", tag):
        continue
    current = None
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        s = line.strip()
        m = re.match(r'^"([a-z0-9_\-]+)":\s*\{', s)
        if m and line.startswith('  "'):
            current = [m.group(1), 0, 0.5]
            rows.append(current)
            continue
        if current is not None:
            m = re.match(r'^"population":\s*(\d+)', s)
            if m:
                current[1] = int(m.group(1))
            m = re.match(r'^"urbanization":\s*([0-9.]+)', s)
            if m:
                current[2] = float(m.group(1))
    regions[tag] = rows
    for r in rows:
        owner[r[0]] = tag

FARM = {"Grain", "Oilseeds", "Sugar", "CashCrops", "Cotton"}
PEOPLE = {"Fish"}


def spread(good, key, points, out, problems):
    if ":" in key:
        tag, names = key.split(":", 1)
        ids = [n.strip() for n in names.split(",") if n.strip()]
        for rid in ids:
            if rid not in owner:
                problems.append(f"{good}: unknown region {rid}")
                return
        for rid in ids:
            out[rid][good] += points / len(ids)
        return
    if key in owner:
        out[key][good] += points
        return
    rows = regions.get(key)
    if not rows:
        problems.append(f"{good}: unknown country {key}")
        return
    if good in FARM:
        weights = [max(1.0, r[1] * (1.0 - min(0.95, r[2]))) for r in rows]
    elif good in PEOPLE:
        weights = [max(1.0, r[1]) for r in rows]
    else:
        weights = [1.0 for _ in rows]
    total = sum(weights)
    for r, w in zip(rows, weights):
        out[r[0]][good] += points * w / total


# ---------------------------------------------------------------- 2024 shares
# Approximate shares of world output; sources noted per good. Rounded, and the long tail
# left to the recipes' generic terms (or to nobody).
SPEC = {
    # FAO FAOSTAT 2021-23 cereals (~3.0 bn t), calibrated to EXPORT shares where the two differ. The
    # game's grain demand is close to the same per head everywhere, so a country's exports are its
    # points less its people; written at their production shares, Russia (~5 %) and Ukraine (~2-3 %)
    # sold a sixth of world grain exports instead of the quarter to a third they sold in 2021-24
    # (USDA: Russia ~20 % of wheat, Ukraine ~10 % of grain), and the United States, which turns a
    # third of its maize into ethanol this good does not cover, sold a third. So Russia 6.8,
    # Ukraine 3.6, the United States 8.5: the plains that feed the export market, not the harvest.
    "Grain": {
        "CHN": 19.0, "IND": 11.0,  # China 21 % and India 11 % of output, both net importers or self-sufficient in 2024
        "USA:us_ia,us_il,us_ne,us_mn,us_in,us_ks,us_nd,us_sd,us_oh,us_mo,us_wi,us_ar,us_tx,us_ok,us_mt,us_wa": 8.5,
        "BRA:br_mt,br_pr,br_go,br_ms,br_rs,br_mg,br_sp": 5.5,
        "RUS:ru_kda,ru_ros,ru_sta,ru_vgg,ru_sar,ru_alt,ru_vor,ru_krs,ru_bel,ru_tam,ru_lip,ru_orl,ru_ore,ru_ba,ru_ta,ru_oms,ru_nvs,ru_kgn,ru_sam,ru_pnz": 6.8,
        "IDN": 2.8, "ARG:ar_b,ar_x,ar_s,ar_e,ar_l": 2.2, "UKR": 3.6, "FRA": 2.1,
        "CAN:ca_sk,ca_ab,ca_mb,ca_on": 2.1, "BGD": 2.0, "AUS:au_nsw,au_wa,au_sa,au_vic,au_qld": 1.8,
        "VNM": 1.6, "PAK:pk_pb,pk_sd": 1.6, "DEU": 1.5, "THA": 1.2, "TUR": 1.2, "MEX": 1.2, "POL": 1.2,
        "MMR": 1.0, "NGA": 1.0, "ETH": 1.0, "ROU": 0.9, "KAZ:kz_kus,kz_akm,kz_sev,kz_pav": 0.8,
        "EGY": 0.8, "PHL": 0.8, "GBR": 0.7, "ESP": 0.6, "IRN": 0.6,
    },
    # USDA FAS 2023/24 vegetable oils (~220 Mt) and oilseeds. Palm: Indonesia + Malaysia ~85 %;
    # sunflower oil: Ukraine + Russia ~55-60 % of world exports.
    "Oilseeds": {
        "IDN:id_ri,id_ss,id_su,id_kt,id_kb,id_ja,id_ki": 22.0, "MYS:my_12,my_13,my_01,my_06": 9.0,
        "CHN": 13.0, "BRA:br_mt,br_pr,br_go,br_ms,br_rs": 9.0, "USA:us_ia,us_il,us_mn,us_in,us_ne,us_nd": 7.0,
        "ARG:ar_b,ar_x,ar_s": 4.0, "IND:in_mp,in_rj,in_mh,in_gj": 4.0,
        "RUS:ru_kda,ru_ros,ru_sar,ru_vgg,ru_vor,ru_alt,ru_ore,ru_sam": 4.0, "UKR": 3.5,
        "CAN:ca_sk,ca_ab,ca_mb": 3.0, "FRA": 1.5, "DEU": 1.5, "THA": 1.5, "AUS": 1.0, "NGA": 1.0, "TUR": 0.8,
    },
    # USDA FAS sugar 2023/24 (~180 Mt raw value); Brazil ~45 % of world exports.
    "Sugar": {
        "BRA:br_sp,br_go,br_mg,br_pr,br_ms": 24.0, "IND:in_up,in_mh,in_ka,in_tn": 18.0, "THA": 6.0,
        "CHN:cn_gx,cn_yn,cn_gd": 6.0, "USA:us_la,us_fl,us_mn,us_nd,us_id": 4.5, "PAK": 3.5,
        "RUS:ru_kda,ru_vor,ru_krs,ru_bel,ru_lip,ru_tam": 3.5, "MEX": 3.0, "FRA": 2.5, "AUS:au_qld": 2.3,
        "DEU": 2.0, "GTM": 1.6, "EGY": 1.5, "TUR": 1.4, "IDN": 1.3, "POL": 1.2, "COL:co_vac": 1.2,
        "PHL": 1.0, "ZAF:za_nl": 1.0, "UKR": 1.0,
    },
    # ICO coffee 2023/24, ICCO cocoa 2023/24, FAO tea 2022, weighted by export value
    # (coffee ~$45 bn, cocoa ~$25 bn, tea ~$8 bn).
    "CashCrops": {
        "BRA:br_mg,br_es,br_sp,br_ba": 18.0, "CIV": 11.0, "CHN:cn_yn,cn_fj,cn_zj": 9.0,
        "VNM:vn_33,vn_35,vn_30": 9.0, "IND:in_as,in_wb,in_ka,in_kl": 6.0,
        "COL:co_hui,co_ant,co_tol,co_cau,co_nar": 5.0, "IDN:id_la,id_ss,id_sn": 5.0, "GHA": 4.0,
        "ETH:et_or,et_sn": 3.0, "ECU:ec_g,ec_r,ec_m": 3.0, "KEN": 3.0, "CMR": 2.0, "NGA": 2.0,
        "HND": 2.0, "UGA": 2.0, "PER": 2.0, "LKA": 2.0, "MEX": 1.5, "GTM": 1.5, "TUR:tr_08": 1.5,
    },
    # FAO SOFIA 2024: capture + aquaculture 2022 (~185 Mt live weight).
    "Fish": {
        "CHN": 36.0, "IDN": 12.0, "IND": 8.0, "VNM": 5.0, "PER": 3.0, "RUS": 3.0, "BGD": 2.6,
        "USA": 2.5, "PHL": 2.3, "NOR": 2.0, "JPN": 2.0, "CHL": 2.0, "KOR": 2.0, "THA": 1.5, "MMR": 1.5,
        "EGY": 1.2, "MYS": 1.0, "MEX": 1.0, "MAR": 1.0, "ECU": 0.8,
    },
    # USDA FAS cotton 2023/24 (~25 Mt); Xinjiang ~90 % of China's crop.
    "Cotton": {
        "CHN:cn_xj": 24.0, "IND:in_gj,in_mh,in_tg": 22.0, "BRA:br_mt,br_ba": 13.0,
        "USA:us_tx,us_ga,us_ms,us_ar": 11.0, "PAK:pk_pb,pk_sd": 5.0, "AUS:au_nsw,au_qld": 4.0,
        "TUR:tr_63,tr_01": 3.0, "UZB": 3.0, "ARG:ar_h": 1.2, "BFA": 1.0, "MEX": 1.0, "BEN": 1.0, "MLI": 1.0,
    },
    # USGS Mineral Commodity Summaries 2024 (2023 mine output ~3,000 t).
    "Gold": {
        "CHN:cn_sd,cn_ha,cn_nm,cn_yn,cn_gs": 11.0, "RUS:ru_kya,ru_irk,ru_sa,ru_mag,ru_chu,ru_amu": 10.0,
        "AUS:au_wa": 10.0, "CAN:ca_on,ca_qc,ca_bc": 6.0, "USA:us_nv,us_ak": 5.5, "GHA:gh_wp,gh_ah": 4.3,
        "PER:pe_caj,pe_lal": 4.0, "MEX:mx_son,mx_zac,mx_chh": 4.0, "KAZ:kz_vos,kz_kar": 4.0,
        "IDN:id_pa": 3.6, "UZB:uz_nw": 3.4, "ZAF:za_gt,za_fs,za_nw": 3.4, "BRA:br_mg,br_pa": 2.4,
        "SDN": 2.4, "MLI": 2.0, "BFA": 2.0, "COL:co_ant": 2.0, "TZA": 1.8, "PNG": 1.6, "ARG:ar_z,ar_j": 1.0,
    },
    # Kimberley Process 2023 by value.
    "Diamonds": {
        "RUS:ru_sa,ru_ark": 30.0, "BWA:bw_ce,bw_so": 25.0, "CAN:ca_nt": 13.0, "AGO:ao_lno,ao_lsu": 9.0,
        "ZAF:za_nc,za_lp": 7.0, "NAM:na_ka": 6.0, "COD:cd_ke,cd_kw": 3.0, "ZWE:zw_mv": 2.0, "SLE": 1.0, "LSO": 1.0,
    },
    # USGS MCS 2024, zinc mine output 2023 (~12 Mt); lead is similar with China higher.
    "ZincLead": {
        "CHN:cn_nm,cn_yn,cn_gs,cn_gx,cn_hn": 35.0, "PER:pe_pas,pe_jun,pe_anc": 12.0, "AUS:au_qld,au_nsw": 10.0,
        "IND:in_rj": 7.0, "USA:us_ak,us_mo": 6.0, "MEX:mx_zac,mx_chh": 5.5, "BOL:bo_p": 4.0,
        "RUS:ru_bu,ru_pri": 2.5, "SWE": 2.0, "KAZ:kz_vos": 2.0, "TUR": 1.5, "IRL": 1.0,
    },
    # USGS MCS 2024, tin mine output 2023 (~300 kt). Myanmar's Wa State halted mining in 2023.
    "Tin": {
        "CHN:cn_yn,cn_gx,cn_hn": 23.0, "IDN:id_bb": 24.0, "MMR:mm_17": 14.0, "PER:pe_pun": 7.0,
        "COD:cd_nk,cd_ma": 6.0, "BRA:br_ro,br_am": 6.0, "BOL:bo_p,bo_o": 6.0, "AUS:au_tas": 3.0,
        "NGA:ng_pl": 1.5, "MYS:my_08": 1.5, "RWA": 1.0, "VNM:vn_53": 1.0, "LAO:la_kh": 1.0,
    },
    # USGS MCS 2024, tungsten 2023 (~78 kt); China ~81 %.
    "Tungsten": {
        "CHN:cn_jx,cn_hn,cn_ha": 81.0, "VNM:vn_53": 4.5, "RUS:ru_pri,ru_kb,ru_zab": 2.5, "BOL:bo_p,bo_o": 1.8,
        "RWA": 1.5, "KOR:kr_42": 1.0, "MNG": 1.5, "AUT": 1.0, "ESP:es_sa": 1.0, "PRT": 0.7, "GBR:gb_dev": 0.5,
    },
    # USGS MCS 2024: low-purity primary gallium (China ~98 %) and germanium (China ~68 %,
    # the rest Russia and recycling in Belgium, Canada, Germany and Japan).
    "Gallium": {
        "CHN:cn_sx,cn_ha,cn_gx,cn_sd,cn_gz,cn_yn,cn_nm": 85.0, "RUS:ru_kya": 4.0, "JPN:jp_10": 3.0,
        "KOR:kr_47": 2.0, "DEU:de_nw": 2.0, "CAN:ca_bc": 2.0, "BEL": 2.0,
    },
    # USGS MCS 2024, manganese ore (metal content) 2023 (~20 Mt).
    "Manganese": {
        "ZAF:za_nc": 36.0, "GAB:ga_2": 23.0, "AUS:au_nt,au_wa": 14.0, "CHN:cn_gx,cn_gz,cn_hn": 4.5,
        "GHA:gh_wp": 4.0, "IND:in_or,in_mh,in_mp": 3.0, "BRA:br_pa": 3.0, "CIV": 2.0,
        "UKR:ua_12": 1.5, "KAZ:kz_kar": 1.5, "MEX:mx_hid": 1.0, "MYS:my_11": 1.0,
    },
    # USGS MCS 2024, chromite 2023 (~41 Mt).
    "Chromium": {
        "ZAF:za_nw,za_lp,za_mp": 44.0, "TUR:tr_48,tr_26": 17.0, "KAZ:kz_akt": 15.0, "IND:in_or": 9.0,
        "FIN:fi_10": 5.0, "ZWE:zw_mi,zw_mw": 3.0, "ALB": 2.0, "BRA:br_ba": 1.5, "PAK:pk_ba": 1.0, "OMN": 1.0,
    },
    # USGS MCS 2024, platinum (~180 t) and palladium (~200 t) together.
    "PlatinumMetals": {
        "ZAF:za_nw,za_lp,za_mp": 55.0, "RUS:ru_kya,ru_mur": 25.0, "ZWE:zw_mi,zw_mw": 8.0,
        "CAN:ca_on": 4.0, "USA:us_mt": 4.0, "CHN:cn_gs": 1.5, "FIN:fi_10": 0.5,
    },
    # USGS MCS 2024, potash (K2O) 2023 (~40 Mt).
    "Potash": {
        "CAN:ca_sk": 32.0, "RUS:ru_per,ru_vgg": 17.0, "BLR:by_mi": 16.0, "CHN:cn_qh,cn_xj": 14.0,
        "ISR": 5.0, "DEU:de_he,de_th,de_st": 5.0, "JOR": 4.0, "CHL:cl_ta,cl_an": 2.0,
        "LAO:la_vi,la_kh": 2.0, "USA:us_nm,us_ut": 1.0, "ESP:es_b": 1.0, "UZB:uz_qa": 0.5,
    },
    # USGS MCS 2024, natural graphite 2023 (~1.6 Mt); China also makes nearly all synthetic.
    "Graphite": {
        "CHN:cn_hl,cn_nm,cn_sd": 77.0, "MDG:mg_a,mg_a_916": 6.0, "MOZ:mz_p": 6.0, "BRA:br_mg": 4.0,
        "KOR": 1.5, "IND:in_or,in_jh": 1.0, "RUS:ru_amu": 1.0, "NOR:no_19": 0.6, "CAN:ca_qc": 0.5,
        "UKR:ua_35": 0.5, "TUR": 0.5,
    },
    # Helium (USGS MCS 2024: USA ~40 %, Qatar ~35 %, Algeria, Russia rising with Amur) and
    # chip-grade neon (before 2022 Ukraine's Ingas in Mariupol and Cryoin in Odesa purified
    # about half; by 2024 China's steelworks and Russia most of the rest).
    "NobleGases": {
        "USA:us_ks,us_tx,us_wy,us_ok": 30.0, "QAT": 25.0, "CHN:cn_he,cn_js": 14.0, "RUS:ru_amu,ru_ore": 12.0,
        "UKR:ua_51,ua_14": 8.0, "DZA": 4.0, "AUS:au_nt": 2.0, "POL:pl_wp": 1.0, "CAN:ca_sk": 1.0,
        "JPN:jp_10": 1.5, "KOR:kr_41": 1.5,
    },
    # USGS MCS 2024, titanium sponge 2023 (~280 kt). The United States made none after 2020.
    "Titanium": {
        "CHN:cn_ln,cn_sc,cn_yn,cn_gz": 62.0, "JPN:jp_10,jp_28": 15.0, "RUS:ru_sve,ru_per": 11.0,
        "KAZ:kz_vos": 6.0, "SAU:sa_03": 2.0, "UKR:ua_23": 1.5, "IND:in_kl": 1.0,
    },
    # Industrial clusters, not ground: the "deposit" of a fab or a tool-maker is the plant that
    # stands in that region, and goes with the region to whoever holds it.
    # Leading-edge logic (7 nm and below), 2024: TSMC ~90 % (Hsinchu, Taichung, Tainan),
    # Samsung (Hwaseong, Pyeongtaek), Intel (Arizona, Oregon). TrendForce/BCG-SIA 2024.
    "AdvancedChips": {
        "tw_khh": 48.0, "tw_nan": 40.0, "kr_41": 8.0, "us_az": 2.5, "us_or": 1.5,
    },
    # Foundry output by revenue 2024 (TrendForce): Taiwan ~65 % (TSMC, UMC, PSMC, VIS), China
    # ~11 % (SMIC, Hua Hong), Korea ~11 %, the United States (GlobalFoundries), Japan, Europe.
    "Semiconductors": {
        "tw_khh": 32.0, "tw_nan": 28.0, "CHN:cn_js,cn_bj,cn_gd": 12.0, "KOR:kr_41,kr_47": 12.0,
        "USA:us_ny,us_az,us_or,us_tx": 7.0, "JPN:jp_43,jp_10": 4.0, "DEU:de_sn": 2.0, "SGP": 2.0, "ISR": 1.0,
    },
    # Wafer-fab equipment 2024 by maker's home (SEMI, company reports): Applied Materials, Lam
    # and KLA in California; Tokyo Electron, Nikon, Canon, Screen in Japan; ASML in Veldhoven
    # (all EUV, ~90 % of lithography); Zeiss SMT optics in Oberkochen; Naura and AMEC in China.
    "ChipTools": {
        "us_ca": 36.0, "jp_10": 25.0, "nl_nb": 22.0, "CHN:cn_bj,cn_js": 8.0, "de_bw": 5.0, "kr_41": 4.0,
    },
    # Uranium enrichment capacity 2024 (WNA): Rosatom ~44 % (Novouralsk, Seversk, Zelenogorsk,
    # Angarsk), Urenco ~30 % (Capenhurst, Almelo, Gronau, Eunice), CNNC ~14 %, Orano ~12 %.
    "NuclearFuel": {
        "RUS:ru_sve,ru_tom,ru_kya,ru_irk": 44.0, "CHN:cn_sn,cn_gs": 14.0, "fr_26": 12.0,
        "GBR:gb_lan": 10.0, "nl_ge": 8.0, "de_nw": 6.0, "us_nm": 6.0,
    },
}

out = defaultdict(lambda: defaultdict(float))
problems = []
for good, shares in SPEC.items():
    for key, points in shares.items():
        spread(good, key, points, out, problems)

if problems:
    raise SystemExit("\n".join(problems))

HEADER = """// The world's resources in 2024: harvests, mines and industrial clusters behind the goods in
// common/goods/30_world_resources.json. Generated by Tools/WorldResources/gen_deposits.py from
// approximate real shares of world output
// (sources by good below); each good's regions add up to about 100 points - a country's share
// in per cent - and the recipe in common/production turns points into market units, so a good
// is resized there with one weight rather than here region by region.
//
// Overlaid key by key on the regions' own files, like zz_strategic_deposits.json: nothing else
// about a region changes, and a mod that moves one number writes that one number.
//
// Sources (rounded): FAO FAOSTAT 2023 cereals; USDA FAS 2023/24 oilseeds, vegetable oils, sugar
// and cotton; ICO / ICCO / FAO 2023 coffee, cocoa and tea; FAO SOFIA 2024 fish; USGS Mineral
// Commodity Summaries 2024 (2023 output) for gold, zinc and lead, tin, tungsten, gallium and
// germanium, manganese, chromium, platinum metals, potash, graphite, helium, titanium sponge;
// Kimberley Process 2023 diamonds by value; TrendForce 2024 foundry revenue and leading-edge
// capacity (Taiwan ~60-65 % of foundry, ~90 % of 7 nm and below); SEMI 2024 wafer-fab equipment
// by maker; WNA 2024 enrichment capacity (Rosatom ~44 %).
//
// Semiconductors, AdvancedChips, ChipTools and NuclearFuel are industrial clusters rather
// than ground: the fabs of Hsinchu and Tainan, ASML in Veldhoven, Zeiss in Oberkochen, the
// enrichment halls of Novouralsk. They are written as deposits because that is what they
// behave like - fixed where they stand, worked by whoever holds the region, wrecked by a war
// fought over it.
//
// Ukraine's neon is written where it was purified before 2022 (Odesa and Mariupol); Mariupol's
// share therefore produces for whoever holds Donetsk."""

lines = [HEADER, "{"]
items = sorted(out.items(), key=lambda kv: (owner[kv[0]], kv[0]))
for i, (rid, goods) in enumerate(items):
    body = ", ".join(f'"{g}": {round(v, 3)}' for g, v in sorted(goods.items()) if round(v, 3) > 0)
    comma = "," if i < len(items) - 1 else ""
    lines.append(f'  "{rid}": {{ "resources": {{ {body} }} }}{comma}')
lines.append("}")
OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"wrote {len(items)} regions to {OUT}")
