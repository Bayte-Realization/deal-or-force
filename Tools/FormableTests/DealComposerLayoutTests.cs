using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

/// <summary>
/// Regression guards for the Deal Composer modal layout (DealComposerUI, DealTradeTermsUI, DealAnswerUI):
/// - Top 3-column deal body (Offer / Demand / Review) owns vertical ExpandFill space with sensible min-height.
/// - SHARED GUARANTEES panel is compact (ShrinkBegin, never ExpandFill) and uses an HBoxContainer (never an
///   HFlowContainer wrapping CheckBoxes to 0px width and 500px vertical height).
/// - Shared guarantee CheckBoxes have ExpandFill, ClipText = false, and valid min-width/height.
/// - Trade agreement strip (DealSharedPanel) is ShrinkBegin so it never steals vertical space when closed.
/// - DealComposerUI raises cleanly above NewShell and uses zero raw Color literals.
/// </summary>
static class DealComposerLayoutTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL deal-composer-layout: " + label);
        Console.WriteLine("PASS deal-composer-layout: " + label);
    }

    [TestSuite("--deal-composer-layout-only", Order = 2635, IncludeInDefaultRun = true,
        Description = "Deal Composer 3-column vertical ownership, compact SHARED GUARANTEES non-wrapping CheckBoxes, NewShell raising, and zero raw Color literals.")]
    public static void Run()
    {
        var repo = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
        var composerSrc = File.ReadAllText(Path.Combine(repo, "Scripts", "ui", "DealComposerUI.cs"));
        var tradeSrc = File.ReadAllText(Path.Combine(repo, "Scripts", "ui", "DealTradeTermsUI.cs"));
        var answerSrc = File.ReadAllText(Path.Combine(repo, "Scripts", "ui", "DealAnswerUI.cs"));

        // 1. Top 3-column DealColumns owns vertical ExpandFill space with sensible CustomMinimumSize.Y >= 260
        var columnsMatch = Regex.Match(composerSrc,
            @"Name\s*=\s*""DealColumns""[\s\S]{0,300}?CustomMinimumSize\s*=\s*new\s+Vector2\s*\(\s*0\s*,\s*(\d+)\s*\)",
            RegexOptions.Singleline);
        Check(columnsMatch.Success, "DealColumns declares CustomMinimumSize");
        var columnsMinY = int.Parse(columnsMatch.Groups[1].Value);
        Check(columnsMinY >= 260, $"DealColumns CustomMinimumSize.Y ({columnsMinY}) >= 260");
        var columnsBlock = composerSrc.Substring(columnsMatch.Index, Math.Min(350, composerSrc.Length - columnsMatch.Index));
        Check(columnsBlock.Contains("SizeFlagsVertical = SizeFlags.ExpandFill", StringComparison.Ordinal),
            "DealColumns uses SizeFlagsVertical = ExpandFill");

        // 2. SHARED GUARANTEES (BuildDealSharedGuarantees) is compact ShrinkBegin, NOT ExpandFill, and NOT HFlowContainer
        var sharedStart = composerSrc.IndexOf("private void BuildDealSharedGuarantees", StringComparison.Ordinal);
        Check(sharedStart >= 0, "BuildDealSharedGuarantees method exists");
        var sharedEnd = composerSrc.IndexOf("private void BuildDealHalf", sharedStart, StringComparison.Ordinal);
        Check(sharedEnd > sharedStart, "BuildDealSharedGuarantees section bounded cleanly");
        var sharedBlock = composerSrc.Substring(sharedStart, sharedEnd - sharedStart);

        Check(sharedBlock.Contains("Name = \"DealStrategicPanel\"", StringComparison.Ordinal),
            "DealStrategicPanel exists in BuildDealSharedGuarantees");
        Check(sharedBlock.Contains("SizeFlagsVertical = SizeFlags.ShrinkBegin", StringComparison.Ordinal),
            "DealStrategicPanel uses SizeFlagsVertical = ShrinkBegin");
        Check(!sharedBlock.Contains("SizeFlagsVertical = SizeFlags.ExpandFill", StringComparison.Ordinal),
            "BuildDealSharedGuarantees never uses SizeFlagsVertical = ExpandFill");
        Check(!sharedBlock.Contains("new HFlowContainer", StringComparison.Ordinal),
            "DealSharedGuaranteesRow does not instantiate HFlowContainer (prevents 0px-width 1-char-per-line CheckBox collapse)");
        Check(sharedBlock.Contains("new HBoxContainer", StringComparison.Ordinal) &&
              sharedBlock.Contains("Name = \"DealSharedGuaranteesRow\"", StringComparison.Ordinal),
            "DealSharedGuaranteesRow uses HBoxContainer for balanced 2-column horizontal strip");
        Check(sharedBlock.Contains("SizeFlagsHorizontal = SizeFlags.ExpandFill", StringComparison.Ordinal),
            "Shared guarantee CheckBoxes use SizeFlagsHorizontal = ExpandFill");
        Check(sharedBlock.Contains("ClipText = false", StringComparison.Ordinal),
            "Shared guarantee CheckBoxes explicitly keep ClipText = false");
        Check(Regex.IsMatch(sharedBlock, @"CustomMinimumSize\s*=\s*new\s+Vector2\s*\(\s*(1[5-9]\d|[2-9]\d\d)\s*,\s*(2[89]|[3-9]\d)\s*\)"),
            "Shared guarantee CheckBoxes declare a valid minimum width (>=150) and height (>=28)");
        Check(sharedBlock.Contains("\"DealChokepointTransit\"", StringComparison.Ordinal) &&
              sharedBlock.Contains("\"DealCeasefireMediation\"", StringComparison.Ordinal),
            "Both shared guarantee CheckBoxes remain wired");

        // 3. Trade strip (DealSharedPanel) is also compact ShrinkBegin
        var tradePanelIdx = tradeSrc.IndexOf("Name = \"DealSharedPanel\"", StringComparison.Ordinal);
        Check(tradePanelIdx >= 0, "DealSharedPanel exists in DealTradeTermsUI");
        var tradePanelBlock = tradeSrc.Substring(tradePanelIdx, Math.Min(300, tradeSrc.Length - tradePanelIdx));
        Check(tradePanelBlock.Contains("SizeFlagsVertical = SizeFlags.ShrinkBegin", StringComparison.Ordinal),
            "DealSharedPanel uses SizeFlagsVertical = ShrinkBegin");
        Check(!tradePanelBlock.Contains("SizeFlagsVertical = SizeFlags.ExpandFill", StringComparison.Ordinal),
            "DealSharedPanel never uses SizeFlagsVertical = ExpandFill");

        // 4. DealComposerUI raises cleanly above NewShell and restores modal state on close
        Check(composerSrc.Contains("OnNewShellWorkspaceRaised(_dealComposer)", StringComparison.Ordinal) &&
              composerSrc.Contains("FitNewShellWorkspaceLayer(_dealComposer)", StringComparison.Ordinal),
            "OpenDealComposer raises and fits _dealComposer above _newShellRoot when NewShell is active");
        Check(composerSrc.Contains("SyncNewShellModalState()", StringComparison.Ordinal),
            "CloseDealComposer restores NewShell modal state");

        // 5. Token hygiene: zero raw new Color( across all Deal modal UI files
        Check(!composerSrc.Contains("new Color(", StringComparison.Ordinal), "DealComposerUI has zero raw new Color(");
        Check(!tradeSrc.Contains("new Color(", StringComparison.Ordinal), "DealTradeTermsUI has zero raw new Color(");
        Check(!answerSrc.Contains("new Color(", StringComparison.Ordinal), "DealAnswerUI has zero raw new Color(");

        // 6. Localization keys for shared guarantees exist and are non-empty in both EN and RU
        foreach (var lang in new[] { "english", "russian" })
        {
            var path = Path.Combine(GameDataLoader.DataFolder(), "localization", lang, "98_diplomacy_wave5.json");
            Check(File.Exists(path), $"{lang}/98_diplomacy_wave5.json exists");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var key in new[]
            {
                "deal.section.shared_guarantees",
                "deal.clause.chokepoint_transit",
                "deal.clause.ceasefire_mediation",
                "deal.tip.chokepoint_transit",
                "deal.tip.ceasefire_mediation"
            })
            {
                Check(doc.RootElement.TryGetProperty(key, out var val) && !string.IsNullOrWhiteSpace(val.GetString()),
                    $"{lang} localization has non-empty '{key}'");
            }
        }
    }
}
