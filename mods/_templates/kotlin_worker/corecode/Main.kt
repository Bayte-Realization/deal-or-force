// Deal or Force worker protocol 1, Kotlin/Native edition.
//
// The request is one JSON object on stdin, the response is one JSON object on
// stdout. Both are stateless snapshots: the worker never sees a live campaign
// and cannot mutate one directly - it may only queue whitelisted commands.
// Compile with Kotlin/Native (see README.md): the result is a single worker.exe
// with no Java runtime requirement.
//
// Hand-rolled JSON on purpose: kotlinx.serialization pulls a dependency tree a
// mod should not need for two fields. If your worker grows real parsing needs,
// add the library to YOUR build - the protocol itself stays two small objects.

private val DayPattern = Regex("\"day\":(\\d+)")
private val EventPattern = Regex("\"event\":\"([^\"]*)\"")

private fun readRequest(): String = buildString {
    // The snapshot is compact single-line JSON today, but read to EOF anyway:
    // the game closes stdin after the write, and a pretty-printed request must
    // not silently truncate the worker's view of the world.
    while (true) {
        val line = readlnOrNull() ?: break
        append(line)
    }
}

private fun jsonEscape(text: String): String = buildString(text.length) {
    for (ch in text) when (ch) {
        '"' -> append("\\\"")
        '\\' -> append("\\\\")
        '\n' -> append("\\n")
        '\r' -> append("\\r")
        '\t' -> append("\\t")
        else -> if (ch < ' ') {
            // No String.format on Kotlin/Native - hand the four hex digits over.
            append("\\u00")
            append((ch.code shr 4).toString(16))
            append((ch.code and 0xF).toString(16))
        } else append(ch)
    }
}

fun main() {
    val request = readRequest()
    val day = DayPattern.find(request)?.groupValues?.get(1)?.toIntOrNull() ?: 0
    val event = EventPattern.find(request)?.groupValues?.get(1) ?: ""

    val logs = mutableListOf("Kotlin hook: $event")
    val commands = mutableListOf<String>()

    // Optional example: unlock aviation once the scenario is a year old.
    // Disabled templates never affect the base game. Command shapes match the
    // Go/Rust/Luau workers - see WorkerModBackend.Capabilities for the whitelist.
    if (event == "day_advanced" && day >= 365) {
        commands.add("""{"type":"set_feature","feature":"aviation","enabled":true}""")
    }

    val logJson = logs.joinToString(",") { "\"${jsonEscape(it)}\"" }
    val commandJson = commands.joinToString(",")
    print("""{"protocol":1,"logs":[$logJson],"commands":[$commandJson]}""")
}
