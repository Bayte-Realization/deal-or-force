using System.IO;
using DealOrForce.Simulation;

/// <summary>
/// The deal and ultimatum composers use the same document as the peace table:
/// the package scrolls, the price, the refusal and the send button stay put,
/// and reading that preview does not write the campaign.
/// </summary>
static class ComposerDocumentTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new System.Exception("composer document: " + label);
        System.Console.WriteLine("PASS composer document: " + label);
    }

    public static void Run()
    {
        Check(SaveSystem.FormatVersion == 2, "save format stays 2");
        var repo = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
        string Source(string relative) => File.ReadAllText(Path.Combine(repo, relative));
        var ultimatum = Source("Scripts/ui/UltimatumComposerUI.cs");
        var deal = Source("Scripts/ui/DealComposerUI.cs");

        Check(!ultimatum.Contains("ScrollablePanelRows("), "the ultimatum page does not scroll its verdict away");
        Check(ultimatum.Contains("Name = \"UltimatumBodyScroll\""), "ultimatum terms have their own scroll");
        Check(ultimatum.IndexOf("UltimatumBodyScroll") < ultimatum.IndexOf("UltimatumSettlementFooter"),
            "ultimatum terms sit above the pinned footer");
        Check(ultimatum.IndexOf("UltimatumSettlementFooter") < ultimatum.IndexOf("_ultimatumSend = ActionButton"),
            "the ultimatum send button is built into the pinned footer");
        Check(deal.Contains("Name = \"DealSettlementFooter\""), "the deal table has a pinned footer");
        Check(deal.IndexOf("DealSettlementFooter") < deal.IndexOf("_dealSend = ActionButton"),
            "the deal send button is built into the pinned footer");
        Check(ultimatum.Contains("NumberCell(") && deal.Contains("NumberCell("),
            "both composers use the shared figure");
        Check(ultimatum.Contains("AutowrapMode.WordSmart") && deal.Contains("AutowrapMode.WordSmart"),
            "long terms wrap");
        Check(!ultimatum.Contains("ClipText = true") && !deal.Contains("ClipText = true"),
            "terms are not clipped to a fixed width");
        Check(!ultimatum.Contains("new Vector2(280") && !deal.Contains("new Vector2(280"),
            "a term does not force the panel to 280 pixels");
        Check(ultimatum.Contains("UltimatumWhy_"), "a refused ultimatum term is written on the page");
        Check(deal.Contains("DealRefusal("), "a refused deal promise is written on the page");
        Check(ultimatum.Contains("{0} regions · {1} selectable") && deal.Contains("{0} regions · {1} selectable"),
            "region search says how many can still be chosen");

        var english = Source("game/localization/english/zz_269_negotiation_workspace.json");
        var russian = Source("game/localization/russian/zz_269_negotiation_workspace.json");
        foreach (var key in new[]
                 {
                     "{0} regions · {1} selectable", "Their weight", "You pay", "They pay",
                     "Strength ratio", "Population share", "Secession is turned off for this campaign."
                 })
        {
            Check(english.Contains("\"" + key + "\""), "english has " + key);
            Check(russian.Contains("\"" + key + "\""), "russian has " + key);
            var englishValue = JsonValue(english, key);
            var russianValue = JsonValue(russian, key);
            Check(englishValue.Length > 0 && russianValue.Length > 0, key + " is not empty");
            Check(englishValue.Contains("{0}") == russianValue.Contains("{0}") &&
                  englishValue.Contains("{1}") == russianValue.Contains("{1}"),
                key + " keeps its placeholders");
            if (key != "{0} regions · {1} selectable")
                Check(russianValue != englishValue, key + " is translated");
        }
        Check(russian.Contains("Регионов: {0} · доступно: {1}"), "the region count is translated");

        var campaign = Campaign.CreateBootstrap(7);
        var player = campaign.NationByTag("KOR");
        var target = campaign.NationByTag("PRK");
        var before = CampaignFingerprint.Of(campaign);
        var treasury = target.Treasury;
        var self = Ultimatum.Assess(campaign, player, player, new Ultimatum.Demand { NonAggression = true });
        Check(!self.Valid && self.Reason == "Invalid target.", "a country cannot demand terms of itself");
        var empty = Ultimatum.Assess(campaign, player, target, new Ultimatum.Demand());
        Check(!empty.Valid && empty.Reason.Length > 0, "an empty ultimatum is refused with a reason");
        var preview = Ultimatum.Assess(campaign, player, target, new Ultimatum.Demand { NonAggression = true });
        Check(preview.Valid || preview.Reason.Length > 0, "a pact preview returns the assessment the button shows");
        var blank = new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side());
        var dealPreview = Ultimatum.AssessNegotiatedDeal(campaign, player, target, blank);
        Check(!dealPreview.Valid && dealPreview.Reason.Length > 0, "an empty deal is refused with a reason");
        Check(target.Treasury == treasury && CampaignFingerprint.Of(campaign) == before,
            "reading a deal or ultimatum preview does not change the campaign");
    }

    static string JsonValue(string json, string key)
    {
        var needle = "\"" + key + "\": \"";
        var start = json.IndexOf(needle, System.StringComparison.Ordinal);
        if (start < 0) return "";
        start += needle.Length;
        var end = json.IndexOf('"', start);
        return end < 0 ? "" : json.Substring(start, end - start);
    }
}
