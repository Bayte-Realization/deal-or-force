"""Generate game/common/production/30_world_resources.json.

The shape of every recipe (what makes a good, what needs it, which shortages bite) is written
here; how big each good is lives in calib.json as two multipliers per good - "out" on its
making terms and "dem" on its demand terms - so the calibration pass can resize a good without
touching its shape. Run: python gen_recipes.py
"""
import json
from pathlib import Path

HERE = Path(__file__).parent
OUT = Path(__file__).resolve().parents[2] / "game" / "common" / "production" / "30_world_resources.json"
CALIB = HERE / "calib.json"
calib = json.loads(CALIB.read_text(encoding="utf-8")) if CALIB.exists() else {}


def scale(good, kind):
    return calib.get(good, {}).get(kind, 1.0)


def terms(table, factor):
    return {k: round(v * factor, 5) for k, v in table.items()}


R = {}   # recipe id -> (comment, recipe dict, good whose calibration it follows, which of out/dem)


def recipe(rid, good, comment, output=None, demand=None, needs=None, shortfall=None, scope=None, size_of=None):
    body = {"good": good}
    if scope:
        body["scope"] = scope
    owner = size_of or good
    if output:
        body["output"] = terms(output, scale(owner, "out"))
    if demand:
        body["demand"] = terms(demand, scale(owner, "dem"))
    if needs:
        body["needs"] = needs
    if shortfall:
        body["shortfall"] = shortfall
    R[rid] = (comment, body)


# ---------------------------------------------------------------- the harvest
recipe("grain_farming", "Grain",
       "Grain: the authored plains (deposit:Grain, share points) worked at the country's own staffing and\n"
       "  // productivity - and at what war leaves of a region - plus every farm on ordinary land\n"
       "  // (deposit:Food is the fertility every region has). Needs fertiliser and diesel. Eaten as\n"
       "  // bread and rice and fed to animals: richer countries eat more of it through their meat.",
       output={"deposit:Grain*output_factor": 23.0, "deposit:Grain*Agriculture": 0.03, "deposit:Food*Agriculture": 0.001},
       demand={"population": 0.4, "population*urbanization*income": 1.2, "Agriculture": 0.9},
       needs={"Fertiliser": 0.75, "Fuel": 0.85})
recipe("food_from_grain", "Food",
       "What a country short of grain does not eat. Zero while grain arrives; a shortage takes bread\n"
       "  // off the table in proportion - Cairo's subsidised loaf is made of Ukrainian and Russian wheat.",
       output={"population": 0.40}, shortfall={"Grain": 0.0}, size_of="_fixed")

recipe("oilseed_farming", "Oilseeds",
       "Oilseeds and vegetable oil: palm in Indonesia and Malaysia, soy in the Americas, sunflower on the\n"
       "  // Black Sea steppe. Cooking oil, feed and biodiesel.",
       output={"deposit:Oilseeds*output_factor": 4.9, "deposit:Oilseeds*Agriculture": 0.098, "deposit:Food*Agriculture": 0.003},
       demand={"population*income": 1.0, "population*standard_of_living*income": 0.5, "ChemicalPlant": 0.4, "Agriculture": 0.3},
       needs={"Fertiliser": 0.85})
recipe("food_from_oilseeds", "Food", "Cooking oil missed at the table.",
       output={"population": 0.06}, shortfall={"Oilseeds": 0.0}, size_of="_fixed")

recipe("sugar_farming", "Sugar",
       "Cane and beet: Brazil, India and Thailand make half the world's sugar.",
       output={"deposit:Sugar*output_factor": 3.1, "deposit:Sugar*Agriculture": 0.062, "deposit:Food*Agriculture": 0.002},
       demand={"population*income": 1.0, "population*standard_of_living*income": 0.4, "ChemicalPlant": 0.2},
       needs={"Fertiliser": 0.85})
recipe("food_from_sugar", "Food", "Sugar missed at the table.",
       output={"population": 0.03}, shortfall={"Sugar": 0.0}, size_of="_fixed")

recipe("cash_crop_plantations", "CashCrops",
       "Coffee, cocoa and tea: grown in the tropics, drunk where people can afford them.",
       output={"deposit:CashCrops*output_factor": 1.0, "deposit:CashCrops*Agriculture": 0.02},
       demand={"population*standard_of_living*income": 1.0, "population*urbanization*income": 0.3})

recipe("fishing", "Fish",
       "The catch and the fish farms. A port lands more of it.",
       output={"deposit:Fish*output_factor": 1.0, "deposit:Fish*raw:Port": 0.1},
       demand={"population": 1.0, "population*standard_of_living*income": 0.8, "Agriculture": 0.2})
recipe("food_from_fish", "Food", "Fish missed at the table.",
       output={"population": 0.05}, shortfall={"Fish": 0.0}, size_of="_fixed")

recipe("cotton_farming", "Cotton",
       "Cotton, for the mills in textile_manufacture (which needs it).",
       output={"deposit:Cotton*output_factor": 1.0, "deposit:Cotton*Agriculture": 0.02},
       demand={"CivilianIndustry": 1.0, "population": 0.135})

# ---------------------------------------------------------------- the mines
def mine(good, comment, demand):
    recipe(good.lower() + "_mining", good, comment,
           output={f"deposit:{good}*extraction_efficiency": 1.0}, demand=demand)


mine("Gold", "Gold: jewellery for the rich and contacts for every circuit board (electronics need it).",
     {"population*standard_of_living*income": 1.0, "ElectronicsPlant": 0.5, "CivilianIndustry": 0.2})
mine("Diamonds", "Diamonds: gems, and the industrial grit machinery cannot be made without.",
     {"population*standard_of_living*income": 1.0, "raw:ExtractionIndustry": 0.3, "CivilianIndustry": 0.2})
mine("ZincLead", "Zinc and lead: galvanised steel, lead-acid batteries, bullets.",
     {"CivilianIndustry": 1.0, "Smelter": 1.0, "ArmsIndustry": 0.6, "raw:Infrastructure": 0.15})
mine("Tin", "Tin: solder, for electronics and chips.",
     {"ElectronicsPlant": 1.0, "CivilianIndustry": 0.5})
mine("Tungsten", "Tungsten: cutting tools, armour-piercing shot and turbines.",
     {"ArmsIndustry": 1.0, "CivilianIndustry": 0.4, "raw:ExtractionIndustry": 0.2})
mine("Gallium", "Gallium and germanium: power and radar chips, night sights, fibre optics.",
     {"ElectronicsPlant": 1.0, "ArmsIndustry": 0.6, "CivilianIndustry": 0.15,
      "deposit:Semiconductors*output_factor": 0.04, "deposit:AdvancedChips*output_factor": 0.04})
mine("Manganese", "Manganese: every tonne of steel takes some, and battery cathodes more.",
     {"Smelter": 1.0, "CivilianIndustry": 0.5, "ElectronicsPlant": 0.15})
mine("Chromium", "Chromium: stainless steel and the superalloys of jet engines.",
     {"Smelter": 1.0, "CivilianIndustry": 0.4, "ArmsIndustry": 0.4})
mine("PlatinumMetals", "Platinum and palladium: exhaust catalysts, refinery catalysts, electronics.",
     {"CivilianIndustry": 1.0, "population*urbanization*income": 0.3, "ChemicalPlant": 0.4, "Refinery": 0.4, "ElectronicsPlant": 0.2})
mine("Potash", "Potash: the potassium in fertiliser (fertiliser_production needs it).",
     {"Agriculture": 1.0, "ChemicalPlant": 0.5})
mine("Graphite", "Graphite: battery anodes and the electrodes of electric steel furnaces.",
     {"ElectronicsPlant": 1.0, "Smelter": 0.5, "CivilianIndustry": 0.2})
mine("NobleGases", "Helium and neon: lithography lasers, chip fabs, MRI and research cryogenics.",
     {"ElectronicsPlant": 0.4, "ResearchLab": 0.6, "raw:Hospital": 0.2, "CivilianIndustry": 0.05,
      "deposit:Semiconductors*output_factor": 0.06, "deposit:AdvancedChips*output_factor": 0.06})
mine("Titanium", "Titanium sponge: airframes, engines, submarine hulls.",
     {"ArmsIndustry": 1.0, "CivilianIndustry": 0.3, "Shipyard": 0.5})

# ---------------------------------------------------------------- what is made from them
recipe("fuel_refining", "Fuel",
       "Refined fuel. A country in 2024 already refines most of what it burns, so the base terms carry\n"
       "  // it and a refinery adds capacity. Needs crude - which is why an oil embargo empties petrol\n"
       "  // stations rather than just raising a price - and power.",
       output={"Refinery": 2.0, "population*urbanization": 0.5, "CivilianIndustry": 0.3, "deposit:Oil*extraction_efficiency": 0.15},
       demand={"population*urbanization*income": 1.0, "population*income": 0.25, "Agriculture": 0.35,
               "raw:Infrastructure": 0.1, "Barracks": 0.3},
       needs={"Oil": 0.35, "Energy": 0.8, "PlatinumMetals": 0.95})
recipe("fuel_from_sugarcane", "Fuel",
       "Brazil's ethanol: cane that goes into the tank instead of the cup.",
       output={"deposit:Sugar*output_factor": 0.012}, needs={"Sugar": 0.5}, size_of="_fixed")
recipe("army_refined_fuel_upkeep", "Fuel",
       "Diesel and jet fuel for the forces, on top of the crude bill in army_fuel_upkeep.",
       demand={"divisions": 0.05, "divisions_at_war": 0.10, "hulls": 0.02}, scope="nation")
recipe("food_from_fuel", "Food",
       "A farm without diesel: tractors stand and harvests rot. Zero while fuel arrives.",
       output={"Agriculture": 0.6}, shortfall={"Fuel": 0.0}, size_of="_fixed")

recipe("plastics_production", "Plastics",
       "Polymers from oil and gas chemistry.",
       output={"ChemicalPlant": 1.2, "Refinery": 0.5, "CivilianIndustry": 0.45, "deposit:NaturalGas*extraction_efficiency": 0.05,
               "deposit:Oil*extraction_efficiency": 0.04},
       demand={"CivilianIndustry": 1.0, "population*urbanization*income": 0.6, "ElectronicsPlant": 0.5, "raw:Infrastructure": 0.1},
       needs={"Chemicals": 0.6, "Energy": 0.7})
recipe("consumer_goods_from_plastics", "ConsumerGoods",
       "Everything households buy comes wrapped in, or made of, plastic.",
       output={"CivilianIndustry": 0.5}, shortfall={"Plastics": 0.0}, size_of="_fixed")

recipe("foundry_semiconductors", "Semiconductors",
       "The foundries: Hsinchu, Tainan, Pyeongtaek, Shanghai, Malta NY, Dresden (deposit:Semiconductors\n"
       "  // is fab capacity, history/states/2024/zz_world_resources.json). They run on Dutch and Japanese\n"
       "  // tools, neon and gallium, so a tool embargo reaches every one of them.",
       output={"deposit:Semiconductors*output_factor": 1.0, "deposit:Semiconductors*ElectronicsPlant": 0.1},
       needs={"ChipTools": 0.8, "NobleGases": 0.75, "Gallium": 0.85, "Silica": 0.7, "Chemicals": 0.7, "Energy": 0.55})

recipe("advanced_chip_fabs", "AdvancedChips",
       "Leading-edge logic. Nine in ten were made in Taiwan in 2024; nothing else on this list is\n"
       "  // as concentrated, and nothing a modern economy or army runs on is harder to replace.",
       output={"deposit:AdvancedChips*output_factor": 1.0, "deposit:AdvancedChips*ElectronicsPlant": 0.1},
       demand={"ElectronicsPlant": 1.0, "CivilianIndustry": 0.25, "ResearchLab": 0.5, "ArmsIndustry": 0.4,
               "population*urbanization*income": 0.04},
       needs={"ChipTools": 0.7, "NobleGases": 0.65, "Gallium": 0.9, "Silica": 0.85, "Chemicals": 0.8, "Energy": 0.6})
recipe("electronics_advanced_chip_dependency", "Electronics",
       "Phones, servers and cars without leading-edge chips: the shortfall a Taiwan war sends round the world.\n"
       "  // Floored at 0.4: older nodes stand in for part of what is missing, and most importers already\n"
       "  // live below full supply in 2024 (export controls, the import ceiling in TradeNetwork).",
       output={"CivilianIndustry": 0.6, "ElectronicsPlant": 0.6}, shortfall={"AdvancedChips": 0.4}, size_of="_fixed")

recipe("chipmaking_equipment", "ChipTools",
       "Lithography, etch and deposition tools (deposit:ChipTools: ASML, Zeiss, Applied, Lam, KLA, Tokyo\n"
       "  // Electron). Wanted wherever fabs stand, and in proportion to them.",
       output={"deposit:ChipTools*output_factor": 1.0, "deposit:ChipTools*ResearchLab": 0.2},
       demand={"deposit:Semiconductors*output_factor": 1.0, "deposit:AdvancedChips*output_factor": 1.0,
               "ElectronicsPlant": 0.6, "ResearchLab": 0.1},
       needs={"PrecisionComponents": 0.7, "Machinery": 0.8, "Electronics": 0.8})

recipe("uranium_enrichment", "NuclearFuel",
       "Enrichment halls (deposit:NuclearFuel): Rosatom, Urenco, Orano, CNNC.",
       output={"deposit:NuclearFuel*output_factor": 1.0},
       demand={"PowerPlant": 1.0, "population*urbanization*income": 0.05},
       needs={"Uranium": 0.5, "Energy": 0.7})
recipe("energy_nuclear_fuel_dependency", "Energy",
       "Reactors without fuel. Small - most of the world's power is not nuclear - and zero while it comes.",
       output={"PowerPlant": 0.3, "population*urbanization": 0.03}, shortfall={"NuclearFuel": 0.0}, size_of="_fixed")

HEADER = """// The world's resources: what the twenty-four goods of common/goods/30_world_resources.json are
// made of and what eats them. Shares of who makes what are in the deposits
// (history/states/2024/zz_world_resources.json); these recipes turn those points into market units.
//
// Generated by Tools/WorldResources/gen_recipes.py from one table of shapes and one of sizes
// (calib.json: "out" and "dem" per good), tuned so
// that at the 2024 start the world makes a little more of each than it asks for (SimBench
// --goods-probe). The goods that already existed are wired in where they are made: steel needs
// manganese, chromium, zinc and graphite (00_chains / 10_strategic_industry), fertiliser needs
// potash, textiles need cotton, the arsenal needs tungsten, titanium, gallium and advanced chips
// (20_military_industry), electronics need tin and gold.
//
// A `shortfall` recipe is zero while the input arrives and negative when it does not: that is
// how a grain shortage takes bread off the table, a chip shortage takes electronics off the
// shelves and a diesel shortage leaves harvests in the field."""

lines = [HEADER, "{"]
items = list(R.items())
for i, (rid, (comment, body)) in enumerate(items):
    lines.append(f"  // {comment}")
    text = json.dumps(body, ensure_ascii=False)
    comma = "," if i < len(items) - 1 else ""
    lines.append(f'  "{rid}": {text}{comma}')
    if i < len(items) - 1:
        lines.append("")
lines.append("}")
OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"wrote {len(items)} recipes")
