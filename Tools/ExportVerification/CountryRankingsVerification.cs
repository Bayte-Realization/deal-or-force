using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private static Button RankingButton(DealOrForceGame game, string name) =>
        game.FindChild(name, true, false) as Button;

    private static string RankingExpectedValue(CountryRankings.Row row, CountryRankings.Metric metric) => metric switch
    {
        CountryRankings.Metric.Economy => Fmt.Money((float)row.Economy),
        CountryRankings.Metric.Population => Fmt.People(row.Population >= long.MaxValue ? long.MaxValue : (long)row.Population),
        CountryRankings.Metric.FieldPower => row.FieldPower.ToString("0.0"),
        CountryRankings.Metric.Technology => row.Technology.ToString("0"),
        CountryRankings.Metric.LivingStandards => row.LivingStandards.ToString("0.0"),
        _ => row.Overall.ToString("0.0")
    };

    private async Task ClickRankingControl(DealOrForceGame game, string name, string caption)
    {
        var button = RankingButton(game, name);
        Check(button is { Disabled: false } && button.IsVisibleInTree(), "ranking click target exists: " + caption);
        if (button is not { Disabled: false } || !button.IsVisibleInTree()) return;
        button.GrabFocus();
        for (Node parent = button.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) scroll.EnsureControlVisible(button);
        await Wait(.2);
        var point = button.GetGlobalTransformWithCanvas() * (button.Size * .5f);
        GetViewport().PushInput(new InputEventMouseMotion { Position = point }, true);
        if (GetViewport().GuiGetHoveredControl() != button)
            GD.Print($"RANKING_INPUT_DIAGNOSTIC {caption} point={point} size={button.Size} hover={GetViewport().GuiGetHoveredControl()?.Name}");
        Check(GetViewport().GuiGetHoveredControl() == button, "ranking mouse hit reaches the exact control: " + caption);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left,
            ButtonMask = MouseButtonMask.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left,
            Pressed = false }, true);
        await Wait(.25);
    }

    private async Task RankingEscape()
    {
        GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true }, true);
        GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = false }, true);
        await Wait(.2);
    }

    private void CheckRankingPage(DealOrForceGame game, IReadOnlyList<CountryRankings.Row> expected, string caption)
    {
        var page = game.VerificationRankingPage;
        var wanted = expected.Skip(page * 32).Take(32).ToArray();
        var actual = Enumerable.Range(0, 32).Select(i => RankingButton(game, "CountryRankingRow_" + i))
            .Where(row => row is not null && row.IsVisibleInTree()).ToArray();
        Check(actual.Length == wanted.Length && actual.Select(row => row.GetMeta("nation_id").AsInt32())
                .SequenceEqual(wanted.Select(row => row.NationId)),
            "ranking page has the oracle's countries in order: " + caption);
        for (var i = 0; i < Math.Min(actual.Length, wanted.Length); i++)
        {
            var labels = actual[i].FindChildren("*", "Label", true, false).OfType<Label>().Select(label => label.Text).ToArray();
            var values = Enum.GetValues<CountryRankings.Metric>().Select(id => RankingExpectedValue(wanted[i], id));
            Check(labels.Contains(wanted[i].Name, StringComparer.Ordinal) && labels.Skip(2).SequenceEqual(values),
                "rendered ranking row identifies the country and shows all six pure metric values: " + caption + " " + i);
        }
    }

    private void CheckRankingReadability(DealOrForceGame game, string caption)
    {
        var panel = game.FindChild("CountryRankingsPanel", true, false) as Control;
        Check(panel is not null && panel.IsVisibleInTree(), "ranking panel is visible: " + caption);
        if (panel is null) return;
        var rect = panel.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, panel.Size);
        Check(game.GetViewport().GetVisibleRect().Grow(1).Encloses(rect),
            "ranking panel fits the real viewport: " + caption);
        var textControls = panel.FindChildren("*", "Control", true, false).OfType<Control>()
            .Where(control => control.IsVisibleInTree() && (control is Label or Button or LineEdit)).ToArray();
        var fontFailures = new List<string>(); var targetFailures = new List<string>();
        foreach (var control in textControls)
        {
            var transform = game.GetViewport().GetStretchTransform() * control.GetGlobalTransformWithCanvas();
            var scale = MathF.Min(MathF.Abs(transform.Scale.X), MathF.Abs(transform.Scale.Y));
            var font = control is Label { LabelSettings: not null } label
                ? label.LabelSettings.FontSize : control.GetThemeFontSize("font_size");
            if (font * scale < Ui.Metric("decision.minimum_font_pixels", 14) - .1f) fontFailures.Add(control.Name.ToString());
            if ((control is Button or LineEdit) && control.Size.Y * scale < Ui.Metric("decision.minimum_target_pixels", 32) - .1f)
                targetFailures.Add(control.Name.ToString());
        }
        Check(fontFailures.Count == 0, "ranking text meets the physical font floor: " + caption + " " + string.Join(",", fontFailures));
        Check(targetFailures.Count == 0, "ranking controls meet the physical hit target floor: " + caption + " " + string.Join(",", targetFailures));
        var columns = game.FindChild("CountryRankingColumns", true, false) as Control;
        Check(columns is not null && columns.Size.Y > 15 &&
            columns.GetChildren().OfType<Label>().Where(label => label.Text.Length > 0).All(label => label.Size.X > 30 && label.GetVisibleLineCount() > 0),
            "ranking column captions keep a visible layout row: " + caption);
    }

    private async Task VerifyCountryRankings(DealOrForceGame game)
    {
        var world = game.VerificationWorld; world.IsPaused = true;
        // Use the normal observer-seat transition before taking the read-only baseline.
        // F7 only hides the chrome; the console observer command actually hands seats to AI.
        game.Call("ConsoleTag", "IRN"); world.IsPaused = true;
        var japan = world.NationByTag("JPN");
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size; game.Call("RefreshLocalisedText");
            game.Call("OpenScreen", "Nation"); await Wait(.4);
            var caption = language + " " + size.X;
            VerifyCampaignDossierLayout(game, "rankings " + caption);
            var fingerprint = CampaignFingerprint.Of(world); var random = world.Random.State; var hours = world.ElapsedHours;
            var oracle = CountryRankings.Read(world, CountryRankings.Weights.Default);
            Check(oracle.Count > 32 && japan is not null && oracle.Any(row => row.NationId == japan.Id),
                "visible-country fixture has Japan and several result pages: " + caption);
            await ClickRankingControl(game, "HudCountryRank", "HUD badge " + caption);
            var modal = game.FindChild("CountryRankings", true, false) as Control;
            Check(modal is { Visible: true }, "HUD badge opens the actual rankings modal: " + caption);
            if (modal is not { Visible: true }) continue;
            Check(game.VerificationRankingSelected == world.Player.Id, "HUD badge selects the player country: " + caption);
            Check(game.VerificationRankingSnapshot.SequenceEqual(oracle) && oracle.Count > 100,
                "normal-player UI includes the complete public world ranking: " + caption);
            CheckRankingReadability(game, caption);
            await Capture("country-rankings-overview-" + language + "-" + size.X);

            var search = game.FindChild("CountryRankingSearch", true, false) as LineEdit;
            var metric = game.FindChild("CountryRankingMetric", true, false) as OptionButton;
            Check(search is not null && metric?.ItemCount == 6, "ranking search and all six metrics exist: " + caption);
            if (search is null || metric is null) continue;
            foreach (var id in Enum.GetValues<CountryRankings.Metric>())
            {
                var index = metric.GetItemIndex((int)id);
                metric.Select(index); metric.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index); await Wait(.1);
                var expected = CountryRankings.Query(oracle, id, game.VerificationRankingDescending);
                Check(game.VerificationRankingRows.SequenceEqual(expected), "metric query matches pure values and order: " + id + " " + caption);
                CheckRankingPage(game, expected, id + " " + caption);
            }
            var overallIndex = metric.GetItemIndex((int)CountryRankings.Metric.Overall);
            metric.Select(overallIndex); metric.EmitSignal(OptionButton.SignalName.ItemSelected, (long)overallIndex); await Wait(.1);
            var descending = game.VerificationRankingDescending;
            await ClickRankingControl(game, "CountryRankingOrder", "reverse ranking order " + caption);
            Check(game.VerificationRankingDescending != descending, "order control reverses the comparison: " + caption);
            var all = CountryRankings.Query(oracle, CountryRankings.Metric.Overall, !descending);
            Check(game.VerificationRankingRows.SequenceEqual(all), "reversed order matches the pure query: " + caption);
            Check(RankingButton(game, "CountryRankingPrevious")?.Disabled == true,
                "first ranking page disables Previous: " + caption);
            var reached = new List<int>();
            var pages = (all.Count + 31) / 32;
            for (var page = 0; page < pages; page++)
            {
                Check(game.VerificationRankingPage == page, "ranking pager reaches page " + page + ": " + caption);
                CheckRankingPage(game, all, "page " + page + " " + caption);
                reached.AddRange(Enumerable.Range(0, 32).Select(i => RankingButton(game, "CountryRankingRow_" + i))
                    .Where(row => row is not null && row.IsVisibleInTree()).Select(row => row.GetMeta("nation_id").AsInt32()));
                if (page + 1 < pages) await ClickRankingControl(game, "CountryRankingNext", "next ranking page " + caption);
            }
            Check(reached.SequenceEqual(all.Select(row => row.NationId)) && reached.Distinct().Count() == all.Count,
                "normal paging reaches every eligible country once: " + caption);
            Check(RankingButton(game, "CountryRankingNext")?.Disabled == true, "last ranking page disables Next: " + caption);
            if (pages > 1)
            {
                await ClickRankingControl(game, "CountryRankingPrevious", "previous ranking page " + caption);
                Check(game.VerificationRankingPage == pages - 2, "Previous returns to the preceding page: " + caption);
            }
            search.Text = "JPN"; search.EmitSignal(LineEdit.SignalName.TextChanged, "JPN"); await Wait(.15);
            var japanRows = CountryRankings.Query(oracle, CountryRankings.Metric.Overall, !descending, "JPN");
            Check(game.VerificationRankingPage == 0 && game.VerificationRankingRows.SequenceEqual(japanRows) && japanRows.Count == 1,
                "country tag search resets paging and finds Japan: " + caption);
            CheckRankingPage(game, japanRows, "Japan search " + caption);
            await ClickRankingControl(game, "CountryRankingRow_0", "Japan comparison " + caption);
            var details = game.FindChild("CountryRankingDetails", true, false) as Label;
            Check(game.VerificationRankingSelected == japan.Id && details is not null && details.Text.Contains(japan.DisplayName, StringComparison.Ordinal),
                "clicking the result shows Japan's comparison details: " + caption);
            await Capture("country-rankings-japan-" + language + "-" + size.X);
            search.Text = ""; search.EmitSignal(LineEdit.SignalName.TextChanged, ""); await Wait(.1);
            Check(game.VerificationRankingRows.SequenceEqual(all), "clearing search restores the full comparison: " + caption);
            var count = game.FindChild("CountryRankingCount", true, false) as Label;
            Check(count is not null && count.Text.Contains(all.Count.ToString(), StringComparison.Ordinal),
                "ranking count reports the full eligible set: " + caption);
            await ClickRankingControl(game, "CloseCountryRankings", "close rankings to Nation " + caption);
            Check(!modal.Visible && game.FindChild("CampaignDossier", true, false) is Control { Visible: true } &&
                RankingButton(game, "HudCountryRank") is { } hudRank && hudRank.HasFocus() &&
                hudRank.GetThemeStylebox("focus") is StyleBoxEmpty && !hudRank.ButtonPressed,
                "mouse close returns neutral badge and keeps keyboard return position: " + caption);

            game.Call("CloseSidePanel");
            game.VerificationInspectPick(new GlobePick(japan, null, -1, japan.CapitalLatitude, japan.CapitalLongitude)); await Wait(.25);
            await Capture("ranking-country-card-" + language + "-" + size.X);
            await ClickRankingControl(game, "CountryCardRank", "selected country badge " + caption);
            Check(modal.Visible && game.VerificationRankingSelected == japan.Id, "country card opens rankings on Japan: " + caption);
            await RankingEscape();
            Check(!modal.Visible && game.FindChild("CountryCard", true, false) is Control { Visible: true } &&
                RankingButton(game, "CountryCardRank")?.HasFocus() == true,
                "Escape keeps the country card and restores its badge focus: " + caption);
            game.Call("CloseMapSelection");
            game.VerificationOpenDiplomacy("JPN"); await Wait(.25);
            await ClickRankingControl(game, "DiplomacyCountryRank", "diplomacy badge " + caption);
            Check(modal.Visible && game.VerificationRankingSelected == japan.Id, "diplomacy badge opens rankings on Japan: " + caption);
            await ClickRankingControl(game, "CloseCountryRankings", "close rankings to diplomacy " + caption);
            Check(!modal.Visible && game.FindChild("DiplomacyCountryBanner", true, false) is Control banner && banner.IsVisibleInTree() &&
                RankingButton(game, "DiplomacyCountryRank")?.HasFocus() == true,
                "close keeps the diplomacy dossier and its badge focus: " + caption);
            game.Call("CloseDiplomacyScreen");
            Check(CampaignFingerprint.Of(world) == fingerprint && world.Random.State == random && world.ElapsedHours == hours,
                "rankings navigation changes no campaign state, clock or randomness: " + caption);
            await VerifyFlagCloth(game, language, size.X);
            await VerifyRecruitmentDepotLabelsAndReleaseEscape(game, language, size.X);
        }
        // A HUD read must not consume the modal's update notification. Reuse all row
        // controls while refreshing the detached table; do not churn its focus nodes.
        game.Call("OpenCountryRankings", world.Player.Id); await Wait(.2);
        var rowIds = Enumerable.Range(0, 32).Select(i => RankingButton(game, "CountryRankingRow_" + i).GetInstanceId()).ToArray();
        var gdp = world.Player.Gdp; var stableBefore = CampaignFingerprint.Of(world); var stableRandom = world.Random.State;
        world.Player.Gdp += 123;
        game.Call("ReadCountryRankings", true); game.Call("RefreshCountryRankBadges"); await Wait(.4);
        var updated = CountryRankings.Read(world, CountryRankings.Weights.Default);
        Check(game.VerificationRankingRows.SequenceEqual(CountryRankings.Query(updated,
                CountryRankings.Metric.Overall, game.VerificationRankingDescending)),
            "modal refresh sees a snapshot first consumed by HUD badges");
        Check(rowIds.SequenceEqual(Enumerable.Range(0, 32).Select(i => RankingButton(game, "CountryRankingRow_" + i).GetInstanceId())),
            "live ranking refresh reuses every pooled row and focus control");
        world.Player.Gdp = gdp; game.Call("ReadCountryRankings", true); await Wait(.4);
        Check(CampaignFingerprint.Of(world) == stableBefore && world.Random.State == stableRandom,
            "restored live-refresh fixture leaves no UI side effects");
        game.Call("CloseCountryRankings");
        // Verify normal-player visibility separately from the all-country observer fixture.
        game.Call("ConsoleTag", world.Player.Tag); world.IsPaused = true;
        var fogBefore = CampaignFingerprint.Of(world); var fogRandom = world.Random.State;
        await ClickRankingControl(game, "HudCountryRank", "normal player visibility");
        var visible = CountryRankings.Read(world, CountryRankings.Weights.Default);
        Check(game.VerificationRankingSnapshot.SequenceEqual(visible) && visible.Count > 100 &&
            visible.Any(row => !game.VerificationRankingCanInspect(world.NationById(row.NationId))),
            "uninspectable foreign countries still have public world ranks without granting intelligence access");
        game.Call("CloseCountryRankings");
        Check(CampaignFingerprint.Of(world) == fogBefore && world.Random.State == fogRandom,
            "fog-filtered rankings leave normal-player state and random stream unchanged");
        Loc.Language = "english"; GetTree().Root.Size = new(1920, 1080); game.Call("RefreshLocalisedText");
    }
}
