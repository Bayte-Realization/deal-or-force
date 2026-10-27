package dealorforce.lint

/**
 * The game's data files are JSON with `//` line comments. A small reader of exactly that, so the
 * tool has no dependency to fetch: objects keep their key order, numbers are doubles.
 */
object Json {
    fun parse(text: String): Any? = Reader(stripComments(text)).run { val v = value(); skip(); v }

    /** Line comments out, string contents untouched. */
    fun stripComments(text: String): String {
        val out = StringBuilder(text.length)
        var i = 0
        var inString = false
        while (i < text.length) {
            val c = text[i]
            if (inString) {
                out.append(c)
                if (c == '\\' && i + 1 < text.length) { out.append(text[i + 1]); i += 2; continue }
                if (c == '"') inString = false
                i++
                continue
            }
            if (c == '"') { inString = true; out.append(c); i++; continue }
            if (c == '/' && i + 1 < text.length && text[i + 1] == '/') {
                while (i < text.length && text[i] != '\n') i++
                continue
            }
            out.append(c)
            i++
        }
        return out.toString()
    }

    private class Reader(val s: String) {
        var i = 0

        fun skip() { while (i < s.length && s[i].isWhitespace()) i++ }

        fun value(): Any? {
            skip()
            if (i >= s.length) error("unexpected end")
            return when (s[i]) {
                '{' -> obj()
                '[' -> arr()
                '"' -> str()
                't' -> { i += 4; true }
                'f' -> { i += 5; false }
                'n' -> { i += 4; null }
                else -> num()
            }
        }

        fun obj(): LinkedHashMap<String, Any?> {
            val map = LinkedHashMap<String, Any?>()
            i++
            skip()
            if (s[i] == '}') { i++; return map }
            while (true) {
                skip()
                val key = str()
                skip()
                expect(':')
                map[key] = value()
                skip()
                if (s[i] == ',') { i++; skip(); if (s[i] == '}') { i++; return map }; continue }
                expect('}')
                return map
            }
        }

        fun arr(): ArrayList<Any?> {
            val list = ArrayList<Any?>()
            i++
            skip()
            if (s[i] == ']') { i++; return list }
            while (true) {
                list.add(value())
                skip()
                if (s[i] == ',') { i++; skip(); if (s[i] == ']') { i++; return list }; continue }
                expect(']')
                return list
            }
        }

        fun str(): String {
            expect('"')
            val b = StringBuilder()
            while (s[i] != '"') {
                if (s[i] == '\\') {
                    i++
                    when (s[i]) {
                        'n' -> b.append('\n'); 't' -> b.append('\t'); 'r' -> b.append('\r')
                        'u' -> { b.append(s.substring(i + 1, i + 5).toInt(16).toChar()); i += 4 }
                        else -> b.append(s[i])
                    }
                } else b.append(s[i])
                i++
            }
            i++
            return b.toString()
        }

        fun num(): Double {
            val start = i
            while (i < s.length && (s[i].isDigit() || s[i] in "+-.eE")) i++
            return s.substring(start, i).toDouble()
        }

        fun expect(c: Char) {
            if (i >= s.length || s[i] != c) error("expected '$c' at $i, found '${s.getOrNull(i)}'")
            i++
        }
    }
}
