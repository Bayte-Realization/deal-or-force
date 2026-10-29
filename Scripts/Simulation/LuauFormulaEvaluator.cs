using System;
using System.Collections.Generic;
using System.Globalization;

namespace DealOrForce.Simulation;

/// <summary>
/// Immutable value-only snapshot of a nation's state passed to a Luau formula.
/// Guarantees that a formula can never mutate the live campaign or break multiplayer determinism.
/// </summary>
public readonly record struct NationFormulaSnapshot(
    float Score,
    float Stability,
    float WarSupport,
    float Treasury,
    float Gdp,
    float Inflation,
    float PublicDebt,
    float DebtRatio,
    float Shortage,
    float ManpowerReserve,
    float PoliticalPoints,
    float YoungShare,
    float OldShare,
    float WorkingShare,
    float AtWar)
{
    public static NationFormulaSnapshot From(Campaign campaign, Nation nation, float currentScore = 0f)
    {
        if (nation is null) return new NationFormulaSnapshot(currentScore, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.3f, 0.09f, 0.61f, 0);
        var gdp = MathF.Max(0.01f, nation.Gdp);
        var atWar = 0f;
        if (campaign is not null)
        {
            for (var i = 0; i < campaign.Wars.Count; i++)
            {
                var w = campaign.Wars[i];
                if (!w.Ended && w.Involves(nation.Id)) { atWar = 1f; break; }
            }
        }
        return new NationFormulaSnapshot(
            currentScore,
            nation.Stability,
            nation.WarSupport,
            nation.Treasury,
            nation.Gdp,
            nation.Inflation,
            nation.PublicDebt,
            nation.PublicDebt / gdp,
            nation.ShortageIndex,
            nation.ManpowerReserve,
            nation.PoliticalPoints,
            nation.YoungShare,
            nation.OldShare,
            nation.WorkingShare,
            atWar);
    }

    public float Resolve(string identifier) => identifier switch
    {
        "score" => Score,
        "stability" => Stability,
        "war_support" => WarSupport,
        "treasury" => Treasury,
        "gdp" => Gdp,
        "inflation" => Inflation,
        "public_debt" => PublicDebt,
        "debt_ratio" => DebtRatio,
        "shortage" => Shortage,
        "manpower" => ManpowerReserve,
        "political_points" => PoliticalPoints,
        "young_share" => YoungShare,
        "old_share" => OldShare,
        "working_share" => WorkingShare,
        "at_war" => AtWar,
        _ => 0f
    };
}

/// <summary>
/// Deterministic, sandboxed evaluator for pure Luau mathematical expressions over
/// <see cref="NationFormulaSnapshot"/> (`math.clamp`, `math.min`, `math.max`, `math.abs`,
/// `math.sqrt`, `if ... then ... else ...`, comparison and arithmetic operators).
/// Compiles each expression once into a cached AST so evaluation in the campaign loop allocates nothing.
/// </summary>
public static class LuauFormulaEvaluator
{
    private static readonly Dictionary<string, Func<NationFormulaSnapshot, float>> Compiled = new(StringComparer.Ordinal);

    public static void ClearCache() => Compiled.Clear();

    public static float Evaluate(string expression, in NationFormulaSnapshot snapshot, float fallback = 0f)
    {
        if (string.IsNullOrWhiteSpace(expression)) return fallback;
        if (!Compiled.TryGetValue(expression, out var fn))
        {
            try
            {
                var parser = new Parser(PreprocessLuauModule(expression));
                fn = parser.ParseRoot();
            }
            catch
            {
                fn = null;
            }
            Compiled[expression] = fn;
        }
        if (fn is null) return fallback;
        var result = fn(snapshot);
        return float.IsFinite(result) ? result : fallback;
    }

    private static string PreprocessLuauModule(string source)
    {
        var trimmed = source.Trim();
        var retIdx = trimmed.LastIndexOf("return ", StringComparison.Ordinal);
        if (retIdx >= 0) trimmed = trimmed[(retIdx + 7)..].Trim();
        if (trimmed.EndsWith(';')) trimmed = trimmed[..^1].Trim();
        return trimmed;
    }

    private sealed class Parser
    {
        private readonly string _src;
        private int _pos;

        public Parser(string src) => _src = src;

        public Func<NationFormulaSnapshot, float> ParseRoot()
        {
            var expr = ParseExpression();
            SkipWhitespace();
            return expr;
        }

        private void SkipWhitespace()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
        }

        private bool MatchKeyword(string kw)
        {
            SkipWhitespace();
            if (_pos + kw.Length > _src.Length) return false;
            if (!string.Equals(_src.Substring(_pos, kw.Length), kw, StringComparison.Ordinal)) return false;
            var after = _pos + kw.Length;
            if (after < _src.Length && (char.IsLetterOrDigit(_src[after]) || _src[after] == '_')) return false;
            _pos = after;
            return true;
        }

        private bool MatchSymbol(string sym)
        {
            SkipWhitespace();
            if (_pos + sym.Length > _src.Length) return false;
            if (!string.Equals(_src.Substring(_pos, sym.Length), sym, StringComparison.Ordinal)) return false;
            _pos += sym.Length;
            return true;
        }

        private Func<NationFormulaSnapshot, float> ParseExpression()
        {
            if (MatchKeyword("if"))
            {
                var cond = ParseOr();
                MatchKeyword("then");
                var whenTrue = ParseExpression();
                var whenFalse = MatchKeyword("else") ? ParseExpression() : (_ => 0f);
                MatchKeyword("end");
                return s => cond(s) > 0.5f ? whenTrue(s) : whenFalse(s);
            }
            return ParseOr();
        }

        private Func<NationFormulaSnapshot, float> ParseOr()
        {
            var left = ParseAnd();
            while (MatchKeyword("or"))
            {
                var right = ParseAnd();
                var prev = left;
                left = s => (prev(s) > 0.5f || right(s) > 0.5f) ? 1f : 0f;
            }
            return left;
        }

        private Func<NationFormulaSnapshot, float> ParseAnd()
        {
            var left = ParseComparison();
            while (MatchKeyword("and"))
            {
                var right = ParseComparison();
                var prev = left;
                left = s => (prev(s) > 0.5f && right(s) > 0.5f) ? 1f : 0f;
            }
            return left;
        }

        private Func<NationFormulaSnapshot, float> ParseComparison()
        {
            var left = ParseAdditive();
            SkipWhitespace();
            if (MatchSymbol(">=")) { var r = ParseAdditive(); var l = left; return s => l(s) >= r(s) ? 1f : 0f; }
            if (MatchSymbol("<=")) { var r = ParseAdditive(); var l = left; return s => l(s) <= r(s) ? 1f : 0f; }
            if (MatchSymbol("==")) { var r = ParseAdditive(); var l = left; return s => MathF.Abs(l(s) - r(s)) < 1e-5f ? 1f : 0f; }
            if (MatchSymbol("~=") || MatchSymbol("!=")) { var r = ParseAdditive(); var l = left; return s => MathF.Abs(l(s) - r(s)) >= 1e-5f ? 1f : 0f; }
            if (MatchSymbol(">")) { var r = ParseAdditive(); var l = left; return s => l(s) > r(s) ? 1f : 0f; }
            if (MatchSymbol("<")) { var r = ParseAdditive(); var l = left; return s => l(s) < r(s) ? 1f : 0f; }
            return left;
        }

        private Func<NationFormulaSnapshot, float> ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                if (MatchSymbol("+")) { var r = ParseMultiplicative(); var l = left; left = s => l(s) + r(s); }
                else if (MatchSymbol("-")) { var r = ParseMultiplicative(); var l = left; left = s => l(s) - r(s); }
                else break;
            }
            return left;
        }

        private Func<NationFormulaSnapshot, float> ParseMultiplicative()
        {
            var left = ParseUnary();
            while (true)
            {
                if (MatchSymbol("*")) { var r = ParseUnary(); var l = left; left = s => l(s) * r(s); }
                else if (MatchSymbol("/"))
                {
                    var r = ParseUnary();
                    var l = left;
                    left = s =>
                    {
                        var denom = r(s);
                        return MathF.Abs(denom) < 1e-6f ? 0f : l(s) / denom;
                    };
                }
                else break;
            }
            return left;
        }

        private Func<NationFormulaSnapshot, float> ParseUnary()
        {
            if (MatchSymbol("-")) { var inner = ParseUnary(); return s => -inner(s); }
            if (MatchSymbol("+")) return ParseUnary();
            if (MatchKeyword("not")) { var inner = ParseUnary(); return s => inner(s) <= 0.5f ? 1f : 0f; }
            return ParsePrimary();
        }

        private Func<NationFormulaSnapshot, float> ParsePrimary()
        {
            SkipWhitespace();
            if (MatchSymbol("("))
            {
                var inner = ParseExpression();
                MatchSymbol(")");
                return inner;
            }
            if (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.'))
            {
                var start = _pos;
                while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.' || _src[_pos] == 'e' || _src[_pos] == 'E'))
                    _pos++;
                var literal = float.Parse(_src[start.._pos], CultureInfo.InvariantCulture);
                return _ => literal;
            }
            var idStart = _pos;
            while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_' || _src[_pos] == '.'))
                _pos++;
            var name = _src[idStart.._pos];
            if (name.StartsWith("ctx.", StringComparison.Ordinal)) name = name[4..];
            if (MatchSymbol("("))
            {
                var args = new List<Func<NationFormulaSnapshot, float>>();
                if (!MatchSymbol(")"))
                {
                    do { args.Add(ParseExpression()); } while (MatchSymbol(","));
                    MatchSymbol(")");
                }
                return name switch
                {
                    "math.clamp" or "clamp" when args.Count >= 3 =>
                        s => Math.Clamp(args[0](s), args[1](s), args[2](s)),
                    "math.min" or "min" when args.Count >= 2 =>
                        s => MathF.Min(args[0](s), args[1](s)),
                    "math.max" or "max" when args.Count >= 2 =>
                        s => MathF.Max(args[0](s), args[1](s)),
                    "math.abs" or "abs" when args.Count >= 1 =>
                        s => MathF.Abs(args[0](s)),
                    "math.sqrt" or "sqrt" when args.Count >= 1 =>
                        s => MathF.Sqrt(MathF.Max(0f, args[0](s))),
                    _ => _ => 0f
                };
            }
            return s => s.Resolve(name);
        }
    }
}
