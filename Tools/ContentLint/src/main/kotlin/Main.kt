package dealorforce.lint

import java.io.File
import kotlin.system.exitProcess

/**
 * Content lint: what is wrong with the game's data that no compiler sees.
 *
 *   gradlew run --args="<game folder>"      (the folder that holds common/ and history/)
 *
 * Every check here is one a person found by playing and then by reading files:
 *   - a culture a region names that the catalogue of peoples does not have;
 *   - a region whose peoples do not add up to the whole;
 *   - a country whose regions all carry one and the same mix - its national split copied into
 *     every region, so its Hungarians live as much in Bucharest as in Harghita (114 countries
 *     on 22 October 2026);
 *   - one exact mix of peoples repeated across several countries - a filler pasted in, like the
 *     "Nepali 60 %, Sindhi 30 %, Parsi 10 %" that made India 15 % Nepali;
 *   - a country whose own peoples (primary_cultures) are under half its population - a
 *     federation declared as the state of one of its peoples, which then reads all the others
 *     as minorities (India, with Hindustani alone, broke apart in every long run);
 *   - a law or ideology modifier key the simulation does not read.
 * Errors make the exit code 1; warnings are printed and counted.
 */
fun main(args: Array<String>) {
    val game = File(args.firstOrNull() ?: "game")
    if (!File(game, "common").isDirectory) {
        System.err.println("content lint: '${game.path}' has no common/ folder")
        exitProcess(2)
    }
    val report = Report()
    val cultures = readCultures(game, report)
    val regions = readRegions(game, report)
    checkRegions(regions, cultures, report)
    checkPrimaryPeoples(game, regions, report)
    checkModifierKeys(game, report)
    report.print()
    exitProcess(if (report.errors > 0) 1 else 0)
}

class Report {
    var errors = 0
    var warnings = 0
    private val lines = ArrayList<String>()
    fun error(where: String, what: String) { errors++; lines += "ERROR   $where: $what" }
    fun warn(where: String, what: String) { warnings++; lines += "WARNING $where: $what" }
    fun print() {
        lines.forEach(::println)
        println("content lint: $errors error(s), $warnings warning(s)")
    }
}

class Region(val tag: String, val id: String, val population: Double, val cultures: Map<String, Double>)

@Suppress("UNCHECKED_CAST")
fun readObject(file: File, report: Report): Map<String, Any?>? =
    try { Json.parse(file.readText()) as? Map<String, Any?> }
    catch (e: Exception) { report.error(file.path, "does not parse: ${e.message}"); null }

fun jsonFiles(dir: File): List<File> = dir.listFiles { f -> f.extension == "json" }?.sortedBy { it.name } ?: emptyList()

fun readCultures(game: File, report: Report): Set<String> {
    val ids = HashSet<String>()
    for (file in jsonFiles(File(game, "common/cultures"))) readObject(file, report)?.keys?.let(ids::addAll)
    return ids
}

@Suppress("UNCHECKED_CAST")
fun readRegions(game: File, report: Report): List<Region> {
    val regions = ArrayList<Region>()
    for (file in jsonFiles(File(game, "history/states/2024"))) {
        val tag = file.nameWithoutExtension
        val root = readObject(file, report) ?: continue
        for ((id, value) in root) {
            val region = value as? Map<String, Any?> ?: continue
            val cultures = (region["cultures"] as? Map<String, Any?>)
                ?.mapValues { (it.value as? Double) ?: 0.0 } ?: continue
            regions += Region(tag, id, (region["population"] as? Double) ?: 0.0, cultures)
        }
    }
    return regions
}

fun mixKey(mix: Map<String, Double>): String =
    mix.entries.sortedBy { it.key }.joinToString(",") { "${it.key}:${"%.3f".format(it.value)}" }

fun checkRegions(regions: List<Region>, cultures: Set<String>, report: Report) {
    for (r in regions) {
        for (c in r.cultures.keys) if (c !in cultures) report.error("${r.tag}/${r.id}", "culture '$c' is not in common/cultures")
        val sum = r.cultures.values.sum()
        if (sum < 0.98 || sum > 1.02) report.error("${r.tag}/${r.id}", "peoples add up to ${"%.3f".format(sum)}, not 1")
    }
    // One national mix copied into every region of a country.
    for ((tag, list) in regions.groupBy { it.tag }) {
        if (list.size < 3) continue
        val (key, count) = list.groupingBy { mixKey(it.cultures) }.eachCount().maxByOrNull { it.value } ?: continue
        val peoples = list.first { mixKey(it.cultures) == key }.cultures.size
        if (peoples > 1 && count >= maxOf(3, (list.size * 0.6).toInt()))
            report.warn(tag, "$count of ${list.size} regions carry the same mix of $peoples peoples - the national split copied into every region?")
    }
    // One exact mix pasted across countries.
    val byMix = regions.filter { it.cultures.size >= 3 }.groupBy { mixKey(it.cultures) }
    for ((key, list) in byMix) {
        val countries = list.map { it.tag }.distinct()
        if (countries.size >= 2) report.warn(countries.joinToString(","), "the same mix of peoples in ${list.size} regions of ${countries.size} countries: ${key.take(90)}")
    }
}

@Suppress("UNCHECKED_CAST")
fun checkPrimaryPeoples(game: File, regions: List<Region>, report: Report) {
    val primary = HashMap<String, List<String>>()
    for (file in jsonFiles(File(game, "history/countries/2024"))) {
        val root = readObject(file, report) ?: continue
        for ((tag, value) in root) {
            val list = ((value as? Map<String, Any?>)?.get("primary_cultures") as? List<Any?>)?.filterIsInstance<String>()
            if (list != null) primary[tag] = list
        }
    }
    for ((tag, list) in regions.groupBy { it.tag }) {
        val own = primary[tag] ?: continue
        val people = list.sumOf { it.population }
        if (people <= 0) continue
        val ownPeople = list.sumOf { r -> r.population * own.sumOf { r.cultures[it] ?: 0.0 } }
        val share = ownPeople / people
        if (share < 0.5)
            report.warn(tag, "its own peoples ${own.joinToString("/")} are ${"%.0f".format(share * 100)} % of it - every other people reads as a minority")
    }
}

/** The keys a `modifiers` block may use are the ones NationalModifiers.cs lists. */
@Suppress("UNCHECKED_CAST")
fun checkModifierKeys(game: File, report: Report) {
    val source = File(game.parentFile, "Scripts/Simulation/NationalModifiers.cs")
    if (!source.isFile) return
    val known = Regex("""new\("([a-z_]+)",""").findAll(source.readText()).map { it.groupValues[1] }.toSet()
    if (known.isEmpty()) return
    for (folder in listOf("common/laws", "common/ideologies", "common/governments")) {
        for (file in jsonFiles(File(game, folder))) {
            val root = readObject(file, report) ?: continue
            for ((id, value) in root) {
                val modifiers = (value as? Map<String, Any?>)?.get("modifiers") as? Map<String, Any?> ?: continue
                for (key in modifiers.keys) if (key !in known) report.error("$folder/${file.name}:$id", "modifier key '$key' is read by nothing")
            }
        }
    }
}
