using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The trade agreement on a country page, now that it has terms. The one-press button still asks
/// for the ordinary terms (half the tariff off everything, both ways); beside it "Terms..." opens
/// the deal window with the trade strip open, where every term can be moved. An agreement in force
/// shows what it says in one line - each side's cut and goods, the guarantee and how well it has
/// been kept, the year it ends - with "Revise" beside it, and the exit button names its price.
/// Both the country dossier and the full diplomacy screen carry the same row.
/// </summary>
public partial class DealOrForceGame
{
    private sealed class TradeTermsRow
    {
        public Label Line;
        public Button Terms;
        public Func<Nation> Target;
    }

    // Keyed by the trade button the row belongs to: the dossier's and the diplomacy screen's.
    private readonly Dictionary<Button, TradeTermsRow> _tradeTermsRows = new();

    private void AttachTradeTermsRow(Container parent, Button tradeButton, Func<Nation> target)
    {
        if (parent is null || tradeButton is null) return;
        var box = new VBoxContainer { Name = tradeButton.Name + "Terms" };
        box.AddThemeConstantOverride("separation", 4);
        parent.AddChild(box);
        var line = new Label
        {
            Name = "TradeTermsLine", AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false
        };
        line.AddThemeFontSizeOverride("font_size", 12);
        line.AddThemeColorOverride("font_color", Ui.Colour("text.muted"));
        box.AddChild(line);
        var terms = ActionButton(Tr("Terms…"), () => OpenTradeTerms(target()), 0, 48);
        terms.Name = "TradeTermsOpen";
        terms.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        box.AddChild(terms);
        _tradeTermsRows[tradeButton] = new TradeTermsRow { Line = line, Terms = terms, Target = target };
    }

    /// <summary>"Terms..." opens the deal window with the strip open; "Revise" opens it on the terms in force.</summary>
    private void OpenTradeTerms(Nation target)
    {
        if (_campaign?.Player is null || target is null || target == _campaign.Player) return;
        var relation = CampaignDiplomacy.Peek(_campaign, _campaign.Player.Id, target.Id);
        if (relation?.TradeAgreement == true)
        {
            OpenDealComposer(target, TradeTermsLifecycle.RenewalOf(TradeTerms.Of(relation)));
            return;
        }
        OpenDealComposer(target, TradeTermsCatalog.Ordinary(_campaign, _campaign.Player, target));
    }

    private void RefreshTradeTermsRow(Button tradeButton, Nation nation, DiplomaticRelation relation)
    {
        if (tradeButton is null || !_tradeTermsRows.TryGetValue(tradeButton, out var row) || _campaign?.Player is null) return;
        var player = _campaign.Player;
        var terms = relation?.TradeAgreement == true ? TradeTerms.Of(relation) : null;
        row.Line.Visible = terms is not null;
        row.Line.Text = terms is null ? "" : TermsLine(terms, player, nation);
        row.Terms.Text = terms is null ? Tr("Terms…") : Tr("Revise");
        row.Terms.TooltipText = Ui.Tip(terms is null
            ? Tr("Write the terms yourself: which goods, how much of each tariff, a supply guarantee, how many years.")
            : Tr("Put new terms on the table. They replace these if both sign; until then these stand."));
        // A deal can be put to a government only; a player's country answers its own papers.
        row.Terms.Disabled = nation is null || nation == player || !Ultimatum.CanOfferDeal(_campaign, player, nation, out _);
    }

    /// <summary>"We -50% on all goods; they -100% on oil · grain guarantee 25%, kept 92% · until 2031".</summary>
    private string TermsLine(TradeTerms t, Nation player, Nation other)
    {
        var parts = new List<string>();
        var ours = t.CutOf(player.Id, other.Id);
        var theirs = t.CutOf(other.Id, player.Id);
        parts.Add(Simulation.Loc.Format("We -{0} on {1}", Fmt.Percent(ours), TradeTermsText.GoodsList(t.GoodsOf(player.Id, other.Id))));
        parts.Add(Simulation.Loc.Format("they -{0} on {1}", Fmt.Percent(theirs), TradeTermsText.GoodsList(t.GoodsOf(other.Id, player.Id))));
        var line = string.Join("; ", parts);
        if (t.TryGuaranteeGood(out var good))
            line += " · " + Simulation.Loc.Format(t.GuarantorId == other.Id ? "their {0} guarantee {1}, kept {2}" : "our {0} guarantee {1}, kept {2}",
                Simulation.Loc.Get(CommodityCatalog.Get(good).Name), Fmt.Percent(t.GuaranteeShare), Fmt.Percent(M.Clamp01(t.Delivered)));
        line += " · " + (t.EndsDay >= 0
            ? Simulation.Loc.Format("until {0}", new HistoricalDate(_campaign.Date.Year, _campaign.Date.Month, _campaign.Date.Day)
                .AddDays(Math.Max(0, t.EndsDay - _campaign.ElapsedDays)).Year)
            : Tr("no end date"));
        return line;
    }

    /// <summary>What walking out costs, for the exit button: free, or the memory and the name it costs.</summary>
    private string TradeExitPrice(DiplomaticRelation relation, Nation player)
    {
        var cost = AgreementSystem.ExitCost(_campaign, relation, player.Id);
        if (cost.Free) return Tr("free");
        var opinion = OpinionCatalog.Find(cost.Opinion) is { } definition ? definition.Opinion * cost.Scale : 0f;
        var price = Simulation.Loc.Format("{0} opinion", opinion.ToString("+0;-0;0"));
        if (cost.Notoriety > 0f) price += ", " + Simulation.Loc.Format("+{0} notoriety", cost.Notoriety.ToString("0.#"));
        return price;
    }

    /// <summary>
    /// The trade button's tooltip: the ordinary terms counted from their seat, line by line - the same
    /// count their answer is made of, so "they would sign" here is what pressing the button gets.
    /// </summary>
    private string TradeAnswerTip(Nation player, Nation nation, TradeTerms terms)
    {
        var why = new Simulation.Breakdown();
        why.Begin("", "");
        var points = TradeTermsValue.For(_campaign, nation, player, terms, why) - Ultimatum.GrudgePenalty(_campaign, player, nation);
        var signs = DealAnswer.GovernmentSignsAgreement(_campaign, nation, player, AgreementKind.TradeAgreement, terms);
        var parts = why.Entries.Where(e => MathF.Abs(e.Value) >= 0.05f)
            .OrderByDescending(e => MathF.Abs(e.Value)).Take(5)
            .Select(e => $"{Tr(e.Source)} {e.Value:+0.0;-0.0}");
        return (signs
                   ? Simulation.Loc.Format("They would sign the ordinary terms: {0} points for them.", points.ToString("+0.0;-0.0;0.0"))
                   : Simulation.Loc.Format("They would refuse the ordinary terms: {0} points for them.", points.ToString("+0.0;-0.0;0.0"))) +
               "\n" + TradeTermsText.Describe(_campaign, terms, player, nation) + "\n" + string.Join("\n", parts);
    }
}
