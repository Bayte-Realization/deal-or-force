"""Every string a player reading in another language still meets in English - the wide net.

Tools/audit-untranslated.py reads only string literals handed straight to a translating call
(Tr, T, Loc.Get, ...). That missed whole screens: the Situation room's facts go through a local
F("Answer within", ...), battle notes are written into a BattleRound and translated later with
Tr(latest.Note), incident names come out of game/common/escalation through Loc.Get(variable).
This walks five sources and writes one inventory:

  1. table      keys the English tables have and the language does not (a screen whose author
                registered its English text but nobody translated it yet)
  2. data       display fields of game/**/*.json (name, description, title, label, help, ...)
                whose value - the English text the code passes to Loc.Get(variable) - has no
                entry. A field counts only when the C# that loads that folder reads the field
                (its name appears as a string literal in a file that names the folder).
  3. code       prose string literals in Scripts/ that reach Loc by another door than the calls
                the old scanner knows:
                  - a helper whose own body translates its parameter (every method and local
                    function is read, to a fixpoint; a helper counts in its own class only, so
                    a translating Add() in one class does not make every list.Add() a key);
                  - `return "..."` / `=> "..."` / switch arms of a method that a caller
                    translates (Tr(Foo.Name(x)));
                  - a member set in an object initializer (Note = "...") that a screen
                    translates (Tr(latest.Note));
                  - a row of a C# record table (new("id", "Name", ...)) whose member a screen
                    translates (Tr(NationalModifiers.Of(key)?.Name)).
  4. composed   keys built at run time for ids the data defines: country.{TAG}.name,
                region.{id}.name, city.{id}.name, name.land.{id}[.adj], a culture's country.
  5. bypass     text that cannot be translated by adding a key: interpolated or literal
                strings written into Text/TooltipText/AddItem/... without Loc, and $"..." text
                handed to Tr()/Title() (the interpolated result never matches a key). These
                need a code change and are reported separately with file:line.

It also checks the language's own values: identical to an English sentence, Latin-letter
sentences, an English phrase left inside a translation, mojibake, placeholders that differ
from the key's.

    python Tools/audit-untranslated-data.py                       russian
    python Tools/audit-untranslated-data.py --language german
    python Tools/audit-untranslated-data.py --out tmp_logs/friend

Writes <out>/untranslated_<lang>.json ({english key: where it is shown}), bypass_<lang>.json,
wrong_values_<lang>.json and translators_<lang>.json, and prints the counts.
"""
import argparse
import bisect
import collections
import glob
import io
import json
import os
import re
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))


# ---------------------------------------------------------------------------------------- tables

def strip_comments(text):
    """JSON with // and /* */ comments and trailing commas, the way StructuredData reads it."""
    out = []
    i, n, in_str = 0, len(text), False
    while i < n:
        c = text[i]
        if in_str:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1]); i += 2; continue
            if c == '"':
                in_str = False
            i += 1; continue
        if c == '"':
            in_str = True; out.append(c); i += 1; continue
        if text.startswith("//", i):
            j = text.find("\n", i); i = n if j < 0 else j; continue
        if text.startswith("/*", i):
            j = text.find("*/", i + 2); i = n if j < 0 else j + 2; continue
        out.append(c); i += 1
    return re.sub(r",(\s*[}\]])", r"\1", "".join(out))


def load_json(path):
    return json.loads(strip_comments(io.open(path, encoding="utf-8-sig").read()))


def table(language):
    """key -> (value, file), files in name order and a later file winning, as Loc.LoadDirectory."""
    result = {}
    for path in sorted(glob.glob(os.path.join(ROOT, "game", "localization", language, "*.json")),
                       key=lambda p: os.path.basename(p).lower()):
        try:
            data = load_json(path)
        except ValueError as error:
            print("unreadable table %s: %s" % (path, error), file=sys.stderr)
            continue
        for key, value in data.items():
            if isinstance(value, str):
                result[key] = (value, os.path.basename(path))
    return result


def prose(text):
    """English a player reads, not a token, a path, an id, a tag or a format."""
    if len(text) < 2 or not re.search(r"[A-Za-z]{2}", text):
        return False
    if "{Tr(" in text or "{Loc." in text or text.lstrip().startswith("}") or text.rstrip().endswith("{"):
        return False                                                # a piece of a nested $"..."
    if re.fullmatch(r"[a-z0-9_.:/\-{}|]+", text):                  # token, path, id, key
        return False
    if re.fullmatch(r"[A-Z0-9_]+(?:[ =][A-Za-z0-9_=.,]+)*", text) and ("_" in text or "=" in text or len(text) <= 4):
        return False                                                # TAG, UPPER_SNAKE, LOG_LINE key=value
    if re.fullmatch(r"[A-Za-z0-9_]+", text) and re.search(r"[a-z][A-Z]", text):
        return False                                                # PascalCase id
    if text.startswith(("res://", "user://", "#", "http", "uid://", ", ", ")")) or ")}" in text or "})" in text:
        return False
    if re.search(r"\.(png|svg|json|tscn|tres|ogg|wav|mp3|gd|cs|csv|txt|zip|import|res|webp|jpg|dll|exe)\b", text):
        return False
    return True


def rel(path):
    return os.path.relpath(path, ROOT).replace("\\", "/")


# ---------------------------------------------------------------------------------------- code

LITERAL = re.compile(r'(\$@|@\$|\$|@)?"((?:[^"\\\n]|\\.)*)"')
BASE_TRANSLATORS = {"Tr", "Loc.Get", "Loc.Format", "Loc.GetOr", "Simulation.Loc.Get", "Simulation.Loc.Format",
                    "Ui.Tip", "Title", "ActionButton", "TranslateOpinionDriver"}
# Names the old scanner treats as translating wherever they appear. Kept global on purpose:
# each is defined once, or every definition translates.
GLOBAL_TRANSLATORS = {"Tr", "Loc.Get", "Loc.Format", "Loc.GetOr", "Simulation.Loc.Get", "Simulation.Loc.Format",
                      "Ui.Tip", "Title", "ActionButton", "TranslateOpinionDriver", "Placeholder", "MenuAction",
                      "MenuCaption"}
LOGGING = re.compile(r'\b(GD\.(Print|PrintErr|PushWarning|PushError|PrintRich)|Debug\.|Trace\.|Console\.Write|'
                     r'errors?\??\.Add|warnings?\??\.Add|problems\.Add|failures\.Add|DiagnosticLogs|Logger\.|'
                     r'throw new|Exception\(|Assert|nameof\(|TryGetProperty|GetProperty|Write(String|Number|Boolean|'
                     r'StartObject|PropertyName)|Stopwatch)')
NOT_PROSE_MEMBERS = {"Name", "Text", "Id", "Tag", "Kind", "Key", "Icon", "Path", "Value", "Source", "Type", "Action",
                     "Screen", "Tab", "Mood", "Good", "Role", "Slot", "Branch", "Experience"}
CONTROL_WORDS = {"if", "for", "foreach", "while", "switch", "using", "lock", "catch", "return", "new", "nameof",
                 "typeof", "sizeof", "default", "when", "base", "this", "get", "set", "init", "fixed"}


def read_sources():
    sources = {}
    for path in glob.glob(os.path.join(ROOT, "Scripts", "**", "*.cs"), recursive=True):
        text = io.open(path, encoding="utf-8-sig", errors="replace").read()
        # "one half " + "the other half" over lines is one literal: C# folds it into one constant
        # and that constant is the key. The '+' may end the first line or begin the second. The
        # line breaks are put back after the joined literal, so line numbers stay true.
        text = join_literals(text)
        # Line comments out (keeping columns), so commented code is not read as code.
        text = "\n".join(strip_code_comments(line) for line in text.split("\n"))
        sources[rel(path)] = text
    return sources


JOIN = re.compile(r'(?<!\\)"[ \t]*(?:\r?\n[ \t]*)*\+[ \t]*(?:\r?\n[ \t]*)*(@?)"')


def join_literals(text):
    pos = 0
    while True:
        m = JOIN.search(text, pos)
        if not m:
            return text
        if "\n" not in m.group(0):
            pos = m.end()
            continue
        breaks = m.group(0).count("\n")
        text = text[:m.start()] + text[m.end():]
        # the closing quote of the joined literal, where the line breaks go back
        j = m.start()
        while j < len(text) and text[j] != '"' and text[j] != "\n":
            j += 2 if text[j] == "\\" else 1
        text = text[:j + 1] + "\n" * breaks + text[j + 1:]
        pos = m.start()


def strip_code_comments(line):
    out, in_str, i = [], False, 0
    while i < len(line):
        c = line[i]
        if in_str:
            if c == "\\":
                out.append(line[i:i + 2]); i += 2; continue
            if c == '"':
                in_str = False
        else:
            if c == '"':
                in_str = True
            elif line.startswith("//", i):
                break
        out.append(c); i += 1
    return "".join(out)


DEFINITION = re.compile(
    r'(?<![\w.])(?:(?:private|public|internal|protected|static|override|virtual|async|readonly|sealed|partial|new|unsafe|extern)\s+)*'
    r'(?:[\w<>\[\],.?]+(?:<[^<>()]*>)?\??\s+)(\w+)\s*\(([^()]*(?:\([^()]*\)[^()]*)*)\)\s*(=>|\{|\n\s*\{|where\b)')
CLASS = re.compile(r'\b(?:class|record|struct|interface)\s+(\w+)')


def class_index(text):
    """The top-level type a position belongs to. Nested records and enums are skipped: a helper
    of DealOrForceGame defined below a nested record is still DealOrForceGame's, which is what
    every partial file of that class shares."""
    marks, depth, last = [], 0, 0
    found = list(CLASS.finditer(text))
    for m in found:
        segment = text[last:m.start()]
        depth += segment.count("{") - segment.count("}")
        last = m.start()
        marks.append((m.start(), m.group(1), depth))
    if marks:
        top = min(d for _, _, d in marks)
        marks = [(p, n) for p, n, d in marks if d == top]
    starts = [p for p, _ in marks]

    def at(pos):
        i = bisect.bisect_right(starts, pos) - 1
        return marks[i][1] if i >= 0 else ""
    return at


def body_after(text, start, arrow):
    """The body of a definition: to the matching brace, or to the ';' ending an expression body."""
    if arrow == "=>":
        depth, i = 0, start
        while i < len(text):
            c = text[i]
            if c == '"':
                j = text.find('"', i + 1)
                while j > 0 and text[j - 1] == "\\":
                    j = text.find('"', j + 1)
                i = j + 1 if j > 0 else i + 1
                continue
            if c in "([{":
                depth += 1
            elif c in ")]}":
                depth -= 1
                if depth < 0:
                    return start, i
            elif c == ";" and depth == 0:
                return start, i
            i += 1
        return start, len(text)
    i = text.find("{", start - 1)
    if i < 0:
        return start, start
    depth = 0
    j = i
    while j < len(text):
        c = text[j]
        if c == '"':
            k = text.find('"', j + 1)
            while k > 0 and text[k - 1] == "\\":
                k = text.find('"', k + 1)
            j = k + 1 if k > 0 else j + 1
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i, j
        j += 1
    return i, len(text)


def definitions(sources):
    """(path, class, name, [(type, param)], body_start, body_end, start)"""
    found = []
    for path, text in sources.items():
        cls = class_index(text)
        for m in DEFINITION.finditer(text):
            name, params, arrow = m.group(1), m.group(2), m.group(3).strip()
            if name in CONTROL_WORDS:
                continue
            names = []
            for part in re.split(r",(?![^<]*>)", params):
                part = part.strip()
                pm = re.match(r'(?:params\s+|this\s+|in\s+|ref\s+|out\s+)?([\w<>\[\],.?]+)\s+(\w+)', part)
                names.append((pm.group(1), pm.group(2)) if pm else (None, None))
            begin, end = body_after(text, m.end(), "=>" if arrow == "=>" else "{")
            found.append((path, cls(m.start()), name, names, begin, end, m.start()))
    return found


FIRST_ARG = re.compile(r'(?<![\w])((?:\w+\.)*)(\w+)\(\s*(?:\w+\s*\?\?\s*)?(\w+)\s*(?:[,)+]|\?\?)')
FIRST_ARG_LEFT = re.compile(r'(?<![\w])((?:\w+\.)*)(\w+)\(\s*(\w+)\s*\?\?')
SINK = re.compile(r'\.(?:Text|TooltipText|PlaceholderText)\s*=\s*(\w+)\s*;')


def discover_translators(sources, defs):
    """(class, method) -> parameter positions that reach a translating call, to a fixpoint. A
    method counts in its own class (partial classes merge by name) and when called qualified
    with that class's name.

    Also returns the sinks: (class, method) -> positions written straight into a control's
    Text or TooltipText without passing Loc (SetArmyFeedback(text) => label.Text = text)."""
    translators = collections.defaultdict(set)
    sinks = collections.defaultdict(set)
    for name in GLOBAL_TRANSLATORS:
        translators[("*", name)].add(0)

    def translates(cls, qualifier, callee):
        if ("*", (qualifier + callee).rstrip(".")) in translators or ("*", callee) in translators and not qualifier:
            return True
        if not qualifier and 0 in translators.get((cls, callee), ()):
            return True
        q = qualifier.rstrip(".").split(".")[-1] if qualifier else ""
        return bool(q) and 0 in translators.get((q, callee), ())

    for path, cls, name, params, begin, end, _ in defs:
        body = sources[path][begin:end]
        for pos, (ptype, pname) in enumerate(params):
            if ptype == "string" and pname and re.search(r'\.(?:Text|TooltipText|PlaceholderText)\s*=\s*%s\s*;' % re.escape(pname), body):
                sinks[(cls, name)].add(pos)

    changed = True
    rounds = 0
    while changed and rounds < 8:
        changed, rounds = False, rounds + 1
        for path, cls, name, params, begin, end, _ in defs:
            body = sources[path][begin:end]
            wanted = {pname: pos for pos, (ptype, pname) in enumerate(params) if ptype == "string" and pname}
            if not wanted:
                continue
            for pattern in (FIRST_ARG, FIRST_ARG_LEFT):
                for cm in pattern.finditer(body):
                    qualifier, callee, arg = cm.group(1), cm.group(2), cm.group(3)
                    if arg not in wanted or callee == name:
                        continue
                    if translates(cls, qualifier, callee):
                        position = wanted[arg]
                        if position not in translators[(cls, name)]:
                            translators[(cls, name)].add(position)
                            changed = True
    # A parameter that is translated on one path is not a sink.
    for key in list(sinks):
        sinks[key] -= translators.get(key, set())
        if not sinks[key]:
            del sinks[key]
    return translators, sinks


def split_args(text, start):
    """Arguments of the call whose '(' is at text[start-1]: list of (offset, raw)."""
    args, depth, cur, i, in_str = [], 0, start, start, False
    while i < len(text):
        c = text[i]
        if in_str:
            if c == "\\":
                i += 2; continue
            if c == '"':
                in_str = False
        elif c == '"':
            in_str = True
        elif c in "([{":
            depth += 1
        elif c in ")]}":
            if depth == 0:
                args.append((cur, text[cur:i]))
                return args
            depth -= 1
        elif c == "," and depth == 0:
            args.append((cur, text[cur:i]))
            cur = i + 1
        elif c == ";" and depth == 0:
            return args
        i += 1
    return args


def expression_after(text, start):
    """The expression of `Member = <expr>` in an initializer: to ',' ';' or '}' at depth 0."""
    depth, i = 0, start
    while i < len(text):
        c = text[i]
        if c == '"':
            j = text.find('"', i + 1)
            while j > 0 and text[j - 1] == "\\":
                j = text.find('"', j + 1)
            i = j + 1 if j > 0 else i + 1
            continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            if depth == 0:
                return text[start:i]
            depth -= 1
        elif c in ",;" and depth == 0:
            return text[start:i]
        i += 1
    return text[start:]


SCREEN_BY_FILE = [
    (r"Doctrine|NationalModifiers", "Book of war (doctrines)"),
    (r"Production|Equipment|Commodit|Goods|Factory|Industr|EconomyWorkbench|Construction|Building|Recruitment",
     "Industry / production"),
    (r"Situation|CampaignAlerts|Escalation|Defcon", "Situation room"),
    (r"Combat|Battle", "Battles"),
    (r"DealComposer|DealInbox|DealTerritory|Negotiation|Ultimatum|Peace|Deal", "Deals and peace"),
    (r"Diplomac|Alliance|Organization|Outreach|Subjects|Guarantee|Submission", "Diplomacy"),
    (r"Army|Fleet|Naval|War|Military|Support|Front|Offensive|General", "Military"),
    (r"Research|Technology", "Research"),
    (r"Politic|Policy|Law|Election|Government|Movement|Radical|Kin|Coup|Crisis", "Politics and society"),
    (r"Settings|MainMenu|Pause|SaveLoad|Loading|Startup|Media|Soundtrack|Music|ChartSpan|Ledger", "Menus, charts, settings"),
    (r"Scenario|Editor|Modding|Mod|Author|Flag|ContentWorkbench", "Editors and mods"),
    (r"Globe|Map|Atlas|gfx/", "Map"),
    (r"Economy|Budget|Treasury|Finance|Bank|Trade|Market", "Economy"),
]


def screen_of(path):
    base = path.replace("DealOrForceGame", "Game")
    for pattern, name in SCREEN_BY_FILE:
        if re.search(pattern, base):
            return name
    return "Other screens"


def literal_value(raw):
    return raw.replace('\\"', '"').replace("\\n", "\n").replace("\\t", "\t").replace("\\\\", "\\")


def scan_code(sources, defs, known, translators, sinks):
    missing = collections.OrderedDict()
    bypass = []
    seen_bypass = set()

    def add(text, place, how):
        missing.setdefault(text, [])
        if (place, how) not in missing[text]:
            missing[text].append((place, how))

    def add_bypass(place, kind, text):
        if (place, text) in seen_bypass:
            return
        seen_bypass.add((place, text))
        bypass.append({"place": place, "kind": kind, "text": text[:200]})

    def english_words(value):
        """English a player reads, outside the {holes} of an interpolation. A literal cut short
        by a nested quote ($"{Tr("x")} ...") loses everything after its last open brace."""
        plain = value
        for _ in range(3):
            plain = re.sub(r"\{[^{}]*\}", " ", plain)
        if "{" in plain:
            plain = plain[:plain.index("{")]
        plain = re.sub(r"\b\w+(?:\.\w+)+\b|[\w./]*/[\w./]*", " ", plain)
        return len(re.findall(r"[A-Za-z]{3,}", plain)) >= 2 or bool(re.search(r"\b[A-Z][a-z]{3,}", plain))

    def inside_translation(before):
        """Whether a literal sits inside a still-open Tr(...)/Loc.*(...): Tr(x ? "A" : "B")."""
        for m in reversed(list(re.finditer(r'(?<![\w.])(?:Tr|T|Loc\.\w+|Simulation\.Loc\.\w+|Ui\.Tip|Title|Fmt\.\w+)\(', before))):
            tail = before[m.end():]
            if tail.count("(") >= tail.count(")"):
                return True
        return False

    # What callers translate: Tr(Qualifier.Method(...)) / Tr(Method(...)), Tr(x.Member)
    translated_results = set()      # (qualifier-or-class, method)
    translated_members = collections.defaultdict(set)   # member -> files whose Tr mentions it
    tr_open = re.compile(r'(?<![\w.])(?:Tr|T|Loc\.Get|Loc\.Format|Simulation\.Loc\.Get|Simulation\.Loc\.Format|Ui\.Tip|Title|TranslateOpinionDriver)\(')
    for path, text in sources.items():
        cls = class_index(text)
        for m in tr_open.finditer(text):
            args = split_args(text, m.end())
            if not args:
                continue
            first = args[0][1].strip()
            for q, meth in re.findall(r'(?:(\w+)\.)?(\w+)\(', first[:200]):
                if meth not in ("Get", "Format", "Tr", "Join", "Max", "Min", "Abs", "Round"):
                    translated_results.add((q or cls(m.start()), meth))
            for member in re.findall(r'\.(\w+)\s*(?:\?\?|$|\+|\)|\?)', first):
                if member[:1].isupper():
                    translated_members[member].add(path)
            mm = re.fullmatch(r'[\w.?()\[\]"]*?\.(\w+)', first)
            if mm:
                translated_members[mm.group(1)].add(path)

    record_params = {}
    for path, text in sources.items():
        for m in re.finditer(r'\brecord\s+(?:class\s+|struct\s+)?(\w+)\s*\(([^()]*)\)', text):
            params = []
            for part in re.split(r",(?![^<]*>)", m.group(2)):
                pm = re.match(r'\s*(?:\[[^\]]*\]\s*)?([\w<>\[\],.?]+)\s+(\w+)', part)
                params.append(pm.group(2) if pm else None)
            record_params[m.group(1)] = params

    call_names = set(name for (_, name) in translators)
    call = re.compile(r'(?<![\w])((?:\w+\.)*)(' + "|".join(sorted(map(re.escape, call_names), key=len, reverse=True)) + r')\(')

    for path, text in sources.items():
        if "/Tests" in path or "DeveloperConsole" in path:     # the console speaks English on purpose
            continue
        cls = class_index(text)
        lines = text.split("\n")
        offsets = [0]
        for line in lines:
            offsets.append(offsets[-1] + len(line) + 1)

        def line_of(pos):
            return bisect.bisect_right(offsets, pos)

        def place(pos):
            return "%s:%d" % (path, line_of(pos))

        claimed = set()
        ui_file = path.startswith(("Scripts/ui/", "Scripts/gfx/")) or path.count("/") == 1

        # (a) literal arguments of a translating call
        for m in call.finditer(text):
            qualifier, name = m.group(1), m.group(2)
            q = qualifier.rstrip(".").split(".")[-1] if qualifier else ""
            positions = set()
            positions |= translators.get(("*", (qualifier + name).rstrip(".")), set())
            if not qualifier or qualifier in ("Simulation.",):
                positions |= translators.get(("*", name), set())
                positions |= translators.get((cls(m.start()), name), set())
            elif q:
                positions |= translators.get((q, name), set())
            if not positions:
                continue
            line_text = lines[line_of(m.start()) - 1]
            if LOGGING.search(line_text):
                continue
            for position, (offset, raw) in enumerate(split_args(text, m.end())):
                if position not in positions:
                    continue
                for lit in LITERAL.finditer(raw):
                    pos = offset + lit.start()
                    claimed.add(pos)
                    prefix, value = lit.group(1) or "", literal_value(lit.group(2))
                    before = raw[:lit.start()]
                    if "$" in prefix:
                        if english_words(value) and not re.fullmatch(r"[a-z_.{}A-Za-z0-9]*", value.replace(" ", "x")) is None:
                            pass
                        if english_words(value) and not re.match(r"^[a-z_]+\.", value):
                            add_bypass(place(pos), "interpolated $\"...\" passed to %s(): the result never matches a key" % name, value)
                        continue
                    # Only a literal that is the argument, or a branch of a ternary/??, is the key.
                    stripped_before = before.strip()
                    if stripped_before and not re.search(r'(\?|:|\?\?|\()\s*$', stripped_before):
                        continue
                    after = raw[lit.end():].strip()
                    if after.startswith("+"):
                        continue
                    if prose(value) and value not in known:
                        add(value, place(pos), "literal passed to %s()" % (qualifier + name))

        # (b) literals returned by a method whose result a caller translates
        for path_d, dcls, name, params, begin, end, start in DEFS_BY_PATH.get(path, ()):
            if (dcls, name) not in translated_results:
                continue
            body = text[begin:end]
            for lit in re.finditer(r'(?:return|=>|\?|:|\?\?)\s*(\$?)"((?:[^"\\\n]|\\.)*)"', body):
                pos = begin + lit.start(2) - 1
                if pos in claimed:
                    continue
                if LOGGING.search(lines[line_of(pos) - 1]):
                    continue
                claimed.add(pos)
                value = literal_value(lit.group(2))
                if lit.group(1):
                    if english_words(value):
                        add_bypass(place(pos), "interpolated $\"...\" returned by %s(), which a caller translates" % name, value)
                    continue
                if prose(value) and value not in known:
                    add(value, place(pos), "returned by %s.%s(), which a caller translates" % (dcls, name))

        # (c) a member set in an initializer that a screen translates: Note = "..." / Tr(x.Note)
        for m in re.finditer(r'(?<![\w.])([A-Z]\w+)\s*=\s*(?!=)', text):
            member = m.group(1)
            if member in NOT_PROSE_MEMBERS or member not in translated_members:
                continue
            expr = expression_after(text, m.end())
            if '"' not in expr or LOGGING.search(lines[line_of(m.start()) - 1]):
                continue
            for lit in LITERAL.finditer(expr):
                pos = m.end() + lit.start()
                if pos in claimed:
                    continue
                claimed.add(pos)
                prefix, value = lit.group(1) or "", literal_value(lit.group(2))
                before = expr[:lit.start()].strip()
                if before and not re.search(r'(\?|:|\?\?)$', before):
                    continue
                if expr[lit.end():].lstrip().startswith("+"):
                    continue
                if "$" in prefix:
                    if english_words(value):
                        add_bypass(place(pos), "interpolated $\"...\" stored in %s, which the screen translates" % member, value)
                    continue
                if prose(value) and value not in known:
                    add(value, place(pos), "stored in %s, which a screen passes to Tr()" % member)

        # (d) rows of a C# record table: new("id", "Name", "Help") / new Key("id", ...)
        for m in re.finditer(r'\bnew\s*(\w+)?\s*\(\s*"', text):
            record = m.group(1)
            if record is None:
                # target-typed: the element type of the nearest collection declared above
                window = text[max(0, m.start() - 4000):m.start()]
                decl = list(re.finditer(r'(?:IReadOnlyList|List|IEnumerable|IReadOnlyCollection|ICollection)<(\w+)>|\b(\w+)\[\]\s+\w+\s*=|new\s+(\w+)\[\]', window))
                if not decl:
                    continue
                last = decl[-1]
                record = last.group(1) or last.group(2) or last.group(3)
            params = record_params.get(record)
            if not params:
                continue
            args = split_args(text, m.end() - 1)
            if LOGGING.search(lines[line_of(m.start()) - 1]):
                continue
            for position, (offset, raw) in enumerate(args):
                if position >= len(params) or not params[position]:
                    continue
                member = params[position]
                if member in NOT_PROSE_MEMBERS and member != "Name":
                    continue
                users = translated_members.get(member, set())
                # The member must be translated in a file that names the class the table lives
                # in (NationalModifiers for its nested Key): a record called Key or Entry is too
                # common a word to prove anything.
                owner = cls(m.start())
                anchor = owner if owner and owner != record else record
                if not any(re.search(r'\b%s\b' % re.escape(anchor), sources[u]) for u in users if u != path) \
                        and not any(u == path for u in users):
                    continue
                lm = re.fullmatch(r'\s*(\$?)"((?:[^"\\]|\\.)*)"\s*', raw)
                if not lm:
                    continue
                pos = offset + raw.find('"')
                if pos in claimed:
                    continue
                claimed.add(pos)
                value = literal_value(lm.group(2))
                if lm.group(1):
                    continue
                if prose(value) and value not in known:
                    add(value, place(pos), "C# table %s.%s, which a screen passes to Tr()" % (record, member))

        # (e) bypass: literal or interpolated text written to the screen without Loc - through a
        # helper that only assigns it (SetArmyFeedback), or straight into a control.
        for m in re.finditer(r'(?<![\w.])(\w+)\(', text):
            positions = sinks.get((cls(m.start()), m.group(1)))
            if not positions:
                continue
            for position, (offset, raw) in enumerate(split_args(text, m.end())):
                if position not in positions:
                    continue
                for lit in LITERAL.finditer(raw):
                    pos = offset + lit.start()
                    if pos in claimed:
                        continue
                    before = raw[:lit.start()]
                    if inside_translation(before):
                        continue
                    claimed.add(pos)
                    value = literal_value(lit.group(2))
                    if lit.group(1):
                        if english_words(value):
                            add_bypass(place(pos), "interpolated $\"...\" passed to %s(), which writes it to a control without Loc" % m.group(1), value)
                    elif prose(value) and value not in known and english_words(value):
                        add_bypass(place(pos), "literal passed to %s(), which writes it to a control without Loc" % m.group(1), value)
        if not ui_file:
            continue
        for number, line in enumerate(lines, 1):
            if not line.strip() or LOGGING.search(line):
                continue
            sink = re.search(r'(?:\.|\b)(Text|TooltipText|PlaceholderText|AccessibilityName|AccessibilityDescription)\s*\+?=\s*(.*)', line) \
                or re.search(r'\b(AddItem|SetItemText|SetItemTooltip|AddIconItem|AddCheckItem|AddRadioCheckItem|AddText|AppendText|SetTabTitle|SetTabTooltip|SetColumnTitle|PushNotification|ShowToast|Toast)\((.*)', line)
            if not sink:
                continue
            rhs = sink.group(2)
            for lit in LITERAL.finditer(rhs):
                prefix, value = lit.group(1) or "", literal_value(lit.group(2))
                before = rhs[:lit.start()]
                inside_loc = inside_translation(before)
                if "$" in prefix:
                    plain = re.sub(r"\{[^{}]*\}", " ", value)
                    if english_words(value) and re.search(r"[A-Za-z]{3,}", plain) and not inside_loc:
                        add_bypass("%s:%d" % (path, number), "interpolated $\"...\" into %s" % sink.group(1), value)
                    continue
                if inside_loc:
                    continue
                if prose(value) and value not in known and english_words(value):
                    add_bypass("%s:%d" % (path, number), "literal into %s without Loc" % sink.group(1), value)
                    claimed.add(offsets[number - 1] + len(line) - len(rhs) + lit.start())

        # (e2) any other interpolated English in interface code, wherever it goes: "day {n}",
        # "They will wait {days} more day(s)" - a $"..." can never be a key.
        if not re.search(r"LayoutAudit|PerformanceGovernor|DeveloperConsole|SupportReport", path):
            for lit in LITERAL.finditer(text):
                if lit.start() in claimed or "$" not in (lit.group(1) or ""):
                    continue
                number = line_of(lit.start())
                line = lines[number - 1]
                value = lit.group(2)
                if LOGGING.search(line) or re.search(r"Name\s*=|SetMeta|GetNode|res://|user://|\w=\{|\||\.json", line + value):
                    continue
                if inside_translation(line[:lit.start() - offsets[number - 1]]):
                    continue
                plain = value
                for _ in range(3):
                    plain = re.sub(r"\{[^{}]*\}", " ", plain)
                if "{" in plain:
                    plain = plain[:plain.index("{")]
                plain = re.sub(r"\b\w+(?:\.\w+)+\b|\w*_\w*|[\w./]*/[\w./]*", " ", plain)
                if re.search(r"\b[a-z]{3,}\b|\b[A-Z]{4,}\b(?<!DEFCON)", plain) and re.search(r"[A-Za-z]{3,}", plain):
                    claimed.add(lit.start())
                    add_bypass(place(lit.start()), "interpolated English in interface code (a $\"...\" can never be a key)", value)

        # (f) every other prose literal of interface code. Shown through a tuple, a variable or
        # a helper this reader cannot follow - or not shown at all: these are listed apart, as
        # "route not proven", so a translator can do them and a programmer can check them.
        if re.search(r"DeveloperConsole|LayoutAudit|LayoutPreview|VisualLayoutEditor|GraphicsRenderer|Shader", path):
            continue
        for lit in LITERAL.finditer(text):
            pos = lit.start()
            if pos in claimed or lit.group(1):
                continue
            number = line_of(pos)
            line = lines[number - 1]
            value = literal_value(lit.group(2))
            if not prose(value) or value in known or "_" in value or value.startswith(("f:", "*")):
                continue
            # A sentence or a caption of two words or more, starting the way interface text
            # starts. Single words here are mostly node names and tab ids.
            if not (english_words(value) and re.search(r"\b[a-z]{2,}\b", value) and re.match(r"[A-Z▲▼— ]", value)) \
                    or ";" in value or ("(" in value and not value.endswith(")")):
                continue
            if LOGGING.search(line) or re.search(
                    r'\b(Name\s*=|GetNode|HasNode|FindChild|SetMeta|GetMeta|HasMeta|Connect\(|EmitSignal|CallDeferred|'
                    r'AddThemeColorOverride|AddThemeFontSizeOverride|AddThemeStyleboxOverride|AddThemeConstantOverride|'
                    r'case\s+"|==\s*"|!=\s*"|Contains\("|StartsWith\("|EndsWith\("|Equals\("|uniform|render_mode|'
                    r'texture\(|shader_type|Input\.|IsActionPressed|ProjectSettings|OS\.|FileAccess|DirAccess|'
                    r'SpriteCatalog|TextureOf|SoundCatalog|Play\(|Art\(|Icon\()', line):
                continue
            if re.search(r'\[\s*$', line[:lit.start() - offsets[number - 1]]):
                continue                                   # a dictionary key
            add(value, place(pos), "literal in interface code; the route to Loc is not proven (tuple, variable or helper)")
    return missing, bypass


DEFS_BY_PATH = {}
SOURCES = {}


# ---------------------------------------------------------------------------------------- data

DISPLAY_FIELDS = {"name", "title", "description", "desc", "label", "tooltip", "text", "summary", "help",
                  "consequence", "unavailable", "short_name", "short", "purpose", "category", "state_name",
                  "war_name", "capital_name", "resting", "set", "on", "off", "munition", "screen", "sector",
                  "caption", "headline", "body", "subtitle", "noun", "demonym", "effect", "flavor", "flavour",
                  "quote", "message", "hint", "reason", "why", "outcome", "button", "verb", "head_of_state",
                  "head_of_government"}
NOT_SHOWN = {("professions", "class"), ("production", "good"), ("goods", "icon"),
             # composed below: region.{id}.name first, the English name second
             ("states", "name"),
             # history people: CountryEditor and portraits read them; leader names are shown raw
             # in the top bar (TopBarHudUI.cs) and through Title() elsewhere - counted separately.
             }
SKIP_DIRS = ("localization", "map_data", "gfx", "fonts", "sound", "documentation", "extensions")


def reader_literals(sources):
    """folder -> (field-name literals in the files that name the folder, [those files])."""
    readers = {}
    folder_ref = re.compile(r'"(?:common|events|history|gui|presentation|interface|scenarios|map_data)"\s*,\s*"([a-z_]+)"|'
                            r'"(?:game/)?(?:common|history|gui|presentation|map_data)/([a-z_]+)|'
                            r'DataFolder\(\s*"([a-z_]+)"')
    for path, text in sources.items():
        folders = set()
        for m in folder_ref.finditer(text):
            folders.add(m.group(1) or m.group(2) or m.group(3))
        for top in ("events", "gui", "presentation", "music"):
            if re.search(r'"%s"' % top, text):
                folders.add(top)
        if not folders:
            continue
        literals = set(re.findall(r'"([a-z_]+)"', text))
        for folder in folders:
            fields, files = readers.setdefault(folder, (set(), []))
            fields.update(literals)
            files.append(path)
    return readers


def data_folder(r):
    parts = r.split("/")
    return parts[2] if parts[1] in ("common", "history") else parts[1]


def scan_data(known, readers):
    missing = collections.OrderedDict()
    unread = collections.Counter()
    for path in sorted(glob.glob(os.path.join(ROOT, "game", "**", "*.json"), recursive=True)):
        r = rel(path)
        if r.split("/")[1] in SKIP_DIRS or r.startswith(("game/history/countries/", "game/common/naming/")):
            continue                                # people of history/countries: scan_people
        folder = data_folder(r)
        try:
            data = load_json(path)
        except ValueError as error:
            print("unreadable %s: %s" % (r, error), file=sys.stderr)
            continue
        fields, files = readers.get(folder, (set(), []))

        def walk(node):
            if isinstance(node, dict):
                has_label = "label" in node
                for key, value in node.items():
                    if key.startswith(("//", "_")):
                        continue
                    if isinstance(value, str):
                        field, shown = key, key in DISPLAY_FIELDS
                        # A tab with no label is captioned with its id (CampaignUiCatalog: Text(e, "label", id)).
                        if folder == "gui" and key == "id" and not has_label and re.match(r"^[A-Z][a-z]", value):
                            shown, field = True, "id (caption when no label)"
                        if not shown or (folder, key) in NOT_SHOWN:
                            continue
                        if not prose(value) or value in known:
                            continue
                        if key not in fields and not field.startswith("id"):
                            unread[(folder, key)] += 1
                            continue
                        reader = next((f for f in files if not f.startswith("Scripts/Modding/") and
                                       '"%s"' % key in SOURCES.get(f, "")), files[0] if files else "?")
                        missing.setdefault(value, []).append("%s, field '%s' (read by %s)" % (r, field, reader))
                    else:
                        walk(value)
            elif isinstance(node, list):
                for value in node:
                    walk(value)
        walk(data)
    return missing, unread


# ---------------------------------------------------------------------------------------- composed

def documents(*parts):
    for path in sorted(glob.glob(os.path.join(ROOT, "game", *parts, "*.json"))):
        try:
            yield rel(path), load_json(path)
        except ValueError:
            continue


def scan_composed(known):
    """Id-keyed families: the data defines the ids, the language must define the keys (or the
    English name, which is the fallback key)."""
    missing = collections.OrderedDict()

    def put(value, where):
        missing.setdefault(value, []).append(where)

    for r, data in documents("common", "countries"):
        for tag, row in data.items():
            if isinstance(row, dict) and isinstance(row.get("name"), str):
                if "country.%s.name" % tag not in known and row["name"] not in known:
                    put(row["name"], "country name: %s %s (key country.%s.name or the English name; CountryNames.cs)" % (r, tag, tag))
    for r, data in documents("common", "states"):
        for sid, row in data.items():
            if isinstance(row, dict) and isinstance(row.get("name"), str) and prose(row["name"]):
                if "region.%s.name" % sid not in known and row["name"] not in known:
                    put(row["name"], "region name, map and region panel: %s %s (key region.%s.name or the English name; RegionNames.cs)" % (r, sid, sid))
    for r, data in documents("map_data", "cities"):
        for cid, row in data.items():
            if isinstance(row, dict) and isinstance(row.get("name"), str):
                if "city.%s.name" % cid not in known:
                    put(row["name"], "city label on the map: %s %s (key city.%s.name; CityCatalog.cs falls back to the English name)" % (r, cid, cid))
    for r, data in documents("common", "naming"):
        lands = data.get("lands", data) if isinstance(data, dict) else {}
        for lid, row in lands.items():
            if not isinstance(row, dict):
                continue
            if isinstance(row.get("name"), str) and "name.land." + lid not in known and row["name"] not in known:
                put(row["name"], "generated country names (a land): %s %s (key name.land.%s or the English name; LandAtlas.cs)" % (r, lid, lid))
            if isinstance(row.get("adjective"), str) and "name.land.%s.adj" % lid not in known and row["adjective"] not in known:
                put(row["adjective"], "generated country names (adjective of a land): %s %s (key name.land.%s.adj or the English adjective; LandAtlas.cs)" % (r, lid, lid))
    for r, data in documents("common", "cultures"):
        for cid, row in (data.items() if isinstance(data, dict) else []):
            if isinstance(row, dict) and isinstance(row.get("country"), str) and prose(row["country"]):
                if row["country"] not in known:
                    put(row["country"], "generated country names (a people's country): %s %s, field 'country' (NationNaming.cs Loc.Get(definition.Country))" % (r, cid))
    return missing


def scan_people(known):
    """Leaders and ministers of 1 January 2024: shown raw in the top bar, through Title() on the
    subjects page and in the country dossier."""
    missing = collections.OrderedDict()
    for r, data in documents("history", "countries", "2024"):
        def walk(node, tag):
            if isinstance(node, dict):
                for key, value in node.items():
                    if isinstance(value, str) and key in ("head_of_state", "head_of_government", "name") and prose(value) \
                            and value not in known and re.search(r"[a-z]", value):
                        missing.setdefault(value, []).append("person's name, top bar / dossier / subjects page: %s %s field '%s'" % (r, tag, key))
                    elif isinstance(value, (dict, list)):
                        walk(value, key if re.fullmatch(r"[A-Z]{3}", key) else tag)
            elif isinstance(node, list):
                for value in node:
                    walk(value, tag)
        walk(data, "")
    return missing


# ---------------------------------------------------------------------------------------- wrong values

CYRILLIC = re.compile(r"[Ѐ-ӿ]")
MOJIBAKE = re.compile(r"(Ð[\u0080-¿Ѐ-џ]|Ñ[\u0080-¿Ѐ-џ]|Р[ЃЉЊЋЏђѓєѕіїјљњћўџ]|"
                      r"С[ЃЉЊЋЏђѓєѕіїјљњћўџ]|вЂ|Ã[\u0080-¿]|�)")
PLACE = re.compile(r"(?<!\{)\{(\d+)[^{}]*\}")
ACRONYM = re.compile(r"^(?:[A-Z]{2,6}s?|[A-Z][a-z]?[A-Z]+|Mk|II|III|IV)$")
# Folder names, URL schemes and literals a player types, which a translation keeps in Latin.
TECH_WORDS = {"http", "https", "true", "false", "null", "bak", "yaml", "yml", "json", "user", "res", "gui", "rgb",
              "rrggbb", "mods", "localization", "common", "countries", "history", "cultures", "tags", "tdebug",
              "programme", "interface", "game", "leaders", "music", "tracks"}
# Names that stay Latin in every language: the studio, graphics back ends, file formats.
BRANDS = re.compile(r"Bayte Realization|Direct3D|Vulkan|OpenGL|ANGLE|DEFCON|JSON|YAML|Godot|Steam|CreativeClaw|ElevenLabs|Lyria")


def wrong_values(lang, english):
    found = []
    for key, (value, source) in lang.items():
        if key.startswith("language."):
            continue
        key_is_prose = bool(re.search(r"[A-Za-z]{3,}\s+[A-Za-z]{3,}", key)) and not re.fullmatch(r"[a-z0-9_.]+", key)
        english_text = english.get(key, (key,))[0] if not key_is_prose else key
        if MOJIBAKE.search(value):
            found.append((source, key, value, "mojibake"))
            continue
        want, got = set(PLACE.findall(english_text)), set(PLACE.findall(value))
        if want != got and (want or got) and (key_is_prose or key in english):
            found.append((source, key, value, "placeholders %s in the English, %s in the translation" % (sorted(want), sorted(got))))
            continue
        if value.count("{") != value.count("}") and english_text.count("{") == english_text.count("}"):
            found.append((source, key, value, "unbalanced braces"))
            continue
        if value == key and re.search(r"[a-z]{3}", key) and not BRANDS.search(key):
            found.append((source, key, value, "identical to the English"))
            continue
        plain = re.sub(r"\{[^{}]*\}|\[[^\]]*\]|https?://\S+|res://\S+", " ", value)
        if value and not CYRILLIC.search(value):
            words = [w for w in re.findall(r"[A-Za-z]{3,}", plain) if not ACRONYM.match(w)]
            if len(words) >= 2 and (key_is_prose or key in english):
                found.append((source, key, value, "Latin-letter sentence, no Cyrillic"))
            continue
        mixed = re.findall(r"\b\w*(?:[A-Za-z][Ѐ-ӿ]|[Ѐ-ӿ][A-Za-z])\w*\b", value)
        if mixed:
            found.append((source, key, value, "Latin and Cyrillic letters inside one word: %s" % ", ".join(mixed[:3])))
            continue
        repeated = re.search(r"(?i)(?<![\w-])([Ѐ-ӿ]{1,})[  ]+\1(?![\w-])", value)
        if repeated:
            found.append((source, key, value, "word repeated: '%s'" % repeated.group(0)))
            continue
        # Paths, file names, ids and command names are allowed in Latin; English words are not.
        words_only = re.sub(r"[\w.\-]*[/\\][\w.\-/\\<>]*|\.\w+|\b\w+_\w+\b|«[^»]*»|\"[^\"]*\"", " ", plain)
        leftover = re.search(r"\b[A-Za-z]{3,}(?:[ ,'-]+[A-Za-z]{2,}){2,}\b", words_only)
        if leftover and not all(ACRONYM.match(w) or w[:1].isupper() for w in re.findall(r"[A-Za-z]{2,}", leftover.group(0))):
            found.append((source, key, value, "English phrase left inside: '%s'" % leftover.group(0)))
            continue
        single = [w for w in re.findall(r"(?<![\w.:/<-])[a-z]{3,}(?![\w./>-])", words_only)
                  if w not in TECH_WORDS
                  and not re.search(r"\b(команд(?:а|ой|ами|ы|у)?|консоли)\b[^.]{0,30}\b" + w, value)]
        if single:
            found.append((source, key, value, "English word(s) inside a Russian sentence: %s" % ", ".join(single[:3])))
            continue
        if len(key) > 80 and key_is_prose and len(value) < 0.35 * len(key):
            found.append((source, key, value, "translation is %d%% of the English length - something was dropped" % (100 * len(value) // len(key))))
    return found


# ---------------------------------------------------------------------------------------- main

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--language", default="russian")
    parser.add_argument("--out", default=os.path.join("tmp_logs", "friend"))
    args = parser.parse_args()
    out = os.path.join(ROOT, args.out)
    os.makedirs(out, exist_ok=True)
    short = "ru" if args.language == "russian" else args.language

    lang = table(args.language)
    english = table("english")
    known = set(lang)
    sources = read_sources()
    SOURCES.update(sources)
    defs = definitions(sources)
    for d in defs:
        DEFS_BY_PATH.setdefault(d[0], []).append(d)

    inventory = collections.OrderedDict()
    origin = {}

    def put(key, place, kind):
        inventory.setdefault(key, [])
        origin.setdefault(key, kind)
        if place not in inventory[key] and len(inventory[key]) < 3:
            inventory[key].append(place)

    for key, (value, source) in english.items():
        if key not in known and key.strip() and not key.startswith("language."):
            english_text = "" if value == key else " as \"%s\"" % value.replace("\n", " ")[:400]
            put(key, "%s - registered in game/localization/english/%s%s, absent from %s" % (
                screen_of(source.replace("situation_room", "Situation").replace("deal", "Deal")), source, english_text,
                args.language), "table")

    readers = reader_literals(sources)
    data_missing, unread = scan_data(known, readers)
    for key, places in data_missing.items():
        for place in places:
            put(key, "data: " + place, "data")

    translators, sinks = discover_translators(sources, defs)
    code_missing, bypass = scan_code(sources, defs, known, translators, sinks)
    for key, places in code_missing.items():
        for place, how in places:
            put(key, "%s - %s, %s" % (screen_of(place), place, how), "code")

    for key, places in scan_composed(known).items():
        for place in places:
            put(key, place, "composed")
    for key, places in scan_people(known).items():
        for place in places:
            put(key, place, "people")

    counts = collections.Counter(origin.values())
    result = collections.OrderedDict((key, " | ".join(places)) for key, places in inventory.items())
    with io.open(os.path.join(out, "untranslated_%s.json" % short), "w", encoding="utf-8") as handle:
        json.dump(result, handle, ensure_ascii=False, indent=1)
    with io.open(os.path.join(out, "untranslated_%s_origin.json" % short), "w", encoding="utf-8") as handle:
        json.dump(origin, handle, ensure_ascii=False, indent=0)
    with io.open(os.path.join(out, "bypass_%s.json" % short), "w", encoding="utf-8") as handle:
        json.dump(bypass, handle, ensure_ascii=False, indent=1)
    wrong = wrong_values(lang, english)
    with io.open(os.path.join(out, "wrong_values_%s.json" % short), "w", encoding="utf-8") as handle:
        json.dump([{"file": "game/localization/%s/%s" % (args.language, f), "key": k, "value": v, "problem": p}
                   for f, k, v, p in wrong], handle, ensure_ascii=False, indent=1)
    with io.open(os.path.join(out, "translators_%s.json" % short), "w", encoding="utf-8") as handle:
        json.dump({"%s.%s" % k: sorted(v) for k, v in sorted(translators.items())}, handle, indent=0)

    # Three slices of related material for three translators: the interface (screens, battles,
    # incidents, media library), heads of state with the place names, heads of government and
    # ministers.
    def group(key):
        kind, where = origin[key], result[key]
        if kind == "people":
            return 1 if "field 'head_of_state'" in where else 2
        if kind == "composed":
            return 1
        return 0
    slices = [collections.OrderedDict() for _ in range(3)]
    for key in sorted(result, key=lambda k: (group(k), result[k].split(" - ")[0], result[k])):
        slices[group(key)][key] = result[key]
    for index, part in enumerate(slices, 1):
        with io.open(os.path.join(out, "slice_%d.json" % index), "w", encoding="utf-8") as handle:
            json.dump(part, handle, ensure_ascii=False, indent=1)

    print("%s: %d keys in its tables, %d in English" % (args.language, len(known), len(english)))
    print("missing: %d  (%s)" % (len(result), ", ".join("%s %d" % kv for kv in sorted(counts.items()))))
    print("translating helpers: %d (beyond the %d global ones)" % (len(translators) - len(GLOBAL_TRANSLATORS), len(GLOBAL_TRANSLATORS)))
    print("slices: %s" % ", ".join(str(len(part)) for part in slices))
    print("bypass sites (need code changes): %d" % len(bypass))
    print("suspicious values: %d (%s)" % (len(wrong), ", ".join("%s %d" % kv for kv in collections.Counter(
        p.split(":")[0].split(" in")[0] if not p.startswith("placeholders") else "placeholders" for _, _, _, p in wrong).most_common())))
    if unread:
        print("display-looking fields that no reader names (not counted):")
        for (folder, key), n in unread.most_common(12):
            print("   %-24s %-20s %d" % (folder, key, n))
    return 0


if __name__ == "__main__":
    sys.exit(main())
