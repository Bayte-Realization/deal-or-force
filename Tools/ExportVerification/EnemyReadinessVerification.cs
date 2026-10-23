using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    // Opt-in development fixture only. Does not load or write any player's save.
    private async Task VerifyEnemyReadiness(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var own = c.NationByTag("POL");
        var enemy = c.NationByTag("DEU");
        Check(own is not null && enemy is not null && c.ProvinceMap is not null,
            "enemy readiness fixture has actual countries and loaded topology");
        if (own is null || enemy is null || c.ProvinceMap is null) return;
        c.ConfigureSinglePlayer(own.Id);
        c.IsPaused = true;
        c.Armies.Clear(); c.Battles.Clear(); c.Wars.Clear(); c.FrontLines.Clear();
        var map = c.ProvinceMap;
        var source = BorderFronts.Candidates(c, own.Id, enemy.Id).First(p =>
            map.Neighbors[p].Any(n => !map.IsWater(n) && c.ProvinceController[n] == enemy.MapIndex));
        var target = map.Neighbors[source].First(p => !map.IsWater(p) && c.ProvinceController[p] == enemy.MapIndex);
        var a = new Army { Id = 92001, NationId = own.Id, ProvinceId = source,
            Name = "Verification attacker", Divisions = 1, Personnel = 12000, MaxPersonnel = 15000,
            Organisation = .88f, Equipment = .73f, Strength = .8f, SupplyLevel = .9f };
        var d = new Army { Id = 92002, NationId = enemy.Id, ProvinceId = target,
            Name = "Verification defender", Divisions = 1, Personnel = 9000, MaxPersonnel = 15000,
            Organisation = .26f, Equipment = .41f, Strength = .6f, SupplyLevel = .7f };
        a.StateMapIndex = map.ProvinceToState[source]; d.StateMapIndex = map.ProvinceToState[target];
        (a.Latitude, a.Longitude) = map.CenterOf(source);
        (d.Latitude, d.Longitude) = map.CenterOf(target);
        c.Armies.Add(a); c.Armies.Add(d);
        c.Wars.Add(new StrategicWar { Id = 92000, AttackerId = own.Id, DefenderId = enemy.Id });
        CampaignIntel.Invalidate(c);
        Check(!CampaignIntel.PlayerCanInspect(c, enemy.Id) && CampaignIntel.PlayerCanSeeArmy(c, d),
            "native fixture observes enemy without granting foreign dossier intelligence");

        var originalPointer = DisplayServer.MouseGetPosition();
        var originalMouseMode = Input.MouseMode;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        try
        {
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language;
            GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText");
            a.BattleId = d.BattleId = -1; c.Battles.Clear();
            CampaignIntel.Invalidate(c);
            game.VerificationReadinessMap(target);
            await Wait(.9);
            var before = CampaignFingerprint.Of(c);
            var random = c.Random.State;
            var contact = game.VerificationEnemyContact(d.Id);
            Check(contact.Visible && contact.Bars && contact.Centered,
                "observed enemy has aligned tactical condition bars: " + language + size.X);
            Check(contact.Tooltip.Contains(Loc.Format("Organisation: {0:P0}", d.Organisation)) &&
                contact.Tooltip.Contains(Loc.Format("Equipment: {0:P0}", d.Equipment)) &&
                contact.Tooltip.Contains(Loc.Get("Foreign formation — orders and route are not available.")),
                "actual enemy map hover reports both percentages without exposing orders: " + language + size.X);
            // Native pointer polling can replace a synthetically injected position.
            // WarpMouse is the public viewport-coordinate API (GodotSharp 4.8 XML),
            // so the real pointer and GUI hover agree under either window stretch.
            GetTree().Root.GrabFocus();
            GetViewport().NotifyMouseExited(); GetViewport().NotifyMouseEntered();
            GetViewport().WarpMouse(new Vector2(300, 300));
            GetViewport().UpdateMouseCursorState();
            await Wait(.15);
            var pointerOnEnemy = await CaptureEnemyReadinessHover(game, d,
                "enemy-readiness-map-" + language + "-" + size.X);
            Check(pointerOnEnemy,
                "actual viewport pointer rests on enemy readiness at capture: " + language + size.X);
            Check(CampaignFingerprint.Of(c) == before && c.Random.State == random,
                "map readiness and hover preserve campaign authority");

            var battle = new LandBattle { Id = 92003, WarId = 92000, ProvinceId = target,
                AttackerId = own.Id, DefenderId = enemy.Id, StartedDay = c.ElapsedDays };
            battle.Attackers.Add(a.Id); battle.Defenders.Add(d.Id);
            a.BattleId = d.BattleId = battle.Id; c.Battles.Add(battle);
            CampaignIntel.Invalidate(c);
            before = CampaignFingerprint.Of(c);
            game.VerificationReadinessBattle(battle);
            await Wait(.4);
            var numbers = game.FindChild("FormationReadiness_" + d.Id, true, false) as Label;
            Check(numbers is not null && numbers.IsVisibleInTree() && numbers.Text ==
                Loc.Format("Organisation {0:P0} · equipment {1:P0}", d.Organisation, d.Equipment),
                "actual enemy battle roster prints organisation and equipment: " + language + size.X);
            if (numbers is not null)
            {
                var scroll = game.FindChild("DossierScroll", true, false) as ScrollContainer;
                scroll?.EnsureControlVisible(numbers);
                await Wait(.15);
                Check(game.GetViewportRect().Grow(1).Encloses(numbers.GetGlobalRect()),
                    "enemy readiness numbers fit the viewport: " + language + size.X);
            }
            await Capture("enemy-readiness-battle-" + language + "-" + size.X);
            Check(CampaignFingerprint.Of(c) == before && c.Random.State == random,
                "battle readiness inspection preserves campaign authority");
        }
        }
        finally
        {
            // Restore the original desktop position even after a failed assertion.
            // Input.WarpMouse takes client-area pixels; DisplayServer gives both
            // the saved screen point and the current client-area screen origin.
            Input.MouseMode = Input.MouseModeEnum.Visible;
            Input.WarpMouse((Vector2)(originalPointer - DisplayServer.WindowGetPosition()));
            Input.MouseMode = originalMouseMode;
        }
    }

    private async Task<bool> CaptureEnemyReadinessHover(DealOrForceGame game, Army enemy, string label)
    {
        var organisation = Loc.Format("Organisation: {0:P0}", enemy.Organisation);
        var equipment = Loc.Format("Equipment: {0:P0}", enemy.Equipment);
        var deadline = Time.GetTicksMsec() + 2500;
        var stableFrames = 0;
        var immediateWarpMisses = 0;
        var pointerTip = "";
        var popupTip = "";
        var target = Vector2.Zero;
        var actual = Vector2.Zero;
        for (var frame = 0; frame < 180 && Time.GetTicksMsec() < deadline; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!GetTree().Root.HasFocus()) break; // Do not fight a user switching away.
            var contact = game.VerificationEnemyContact(enemy.Id);
            target = contact.Pixel;
            // Keep OS polling and the GUI event in agreement in this frame, rather
            // than warping once and assuming the physical mouse stays put for .85s.
            GetViewport().WarpMouse(target);
            actual = GetViewport().GetMousePosition();
            if (actual.DistanceTo(target) > 3) immediateWarpMisses++;
            GetViewport().PushInput(new InputEventMouseMotion { Position = target }, true);
            GetViewport().UpdateMouseCursorState();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            actual = GetViewport().GetMousePosition();
            pointerTip = game.VerificationReadinessPointerTooltip();
            popupTip = game.VerificationReadinessPopupText();
            var verified = actual.DistanceTo(target) <= 3 &&
                pointerTip.Contains(organisation) && pointerTip.Contains(equipment) &&
                popupTip.Contains(organisation) && popupTip.Contains(equipment);
            stableFrames = verified ? stableFrames + 1 : 0;
            if (stableFrames < 2) continue;
            // Already at FramePostDraw: capture this verified frame immediately,
            // without Capture()'s additional frame in which the pointer can change.
            CaptureReadinessFrame(label);
            return true;
        }
        GD.Print("READINESS_POINTER target=" + target + " actual=" + actual +
            " immediate_warp_misses=" + immediateWarpMisses + " focused=" + GetTree().Root.HasFocus() +
            " screen_transform=" + GetViewport().GetScreenTransform() +
            " popup_window=" + DisplayServer.WindowGetActivePopup() +
            " tooltip=" + pointerTip.Replace('\n', '|')[..Math.Min(160, pointerTip.Length)] +
            " shown_popup=" + popupTip.Replace('\n', '|')[..Math.Min(160, popupTip.Length)]);
        CaptureReadinessFrame(label + "-unverified");
        return false;
    }

    private void CaptureReadinessFrame(string label)
    {
        if (_captureDirectory.Length == 0 || DisplayServer.GetName() == "headless") return;
        Check(DirAccess.MakeDirRecursiveAbsolute(_captureDirectory) == Error.Ok, "capture directory is available");
        using var image = GetTree().Root.GetTexture().GetImage();
        Check(image.SavePng(System.IO.Path.Combine(_captureDirectory, "export-" + label + ".png")) == Error.Ok,
            "capture " + label);
    }
}

public partial class DealOrForceGame
{
    internal void VerificationReadinessMap(int province)
    {
        CloseMapSelection();
        _battleDetailControls.Visible = false;
        _sidePanel.Visible = false;
        _globe.MilitaryMarkersEnabled = true;
        _globe.SetFlatMap(false);
        _globe.FogOfWarActive = true;
        VerificationFocusProvince(province);
        Refresh();
    }
    internal void VerificationReadinessBattle(LandBattle battle) => ShowBattleDetails(battle);
    internal (bool Visible, bool Bars, bool Centered, string Tooltip, Vector2 Pixel) VerificationEnemyContact(int army) =>
        _globe.VerificationEnemyContact(army);
    internal string VerificationReadinessPointerTooltip() => _globe.VerificationReadinessPointerTooltip();
    internal string VerificationReadinessPopupText() => _globe.VerificationReadinessPopupText();
}

public sealed partial class GlobePresentation
{
    internal (bool Visible, bool Bars, bool Centered, string Tooltip, Vector2 Pixel) VerificationEnemyContact(int armyId)
    {
        SyncArmyMarkers();
        UpdateMilitaryPresentation();
        var marker = _armyStacks.Values.FirstOrDefault(m => m.Stack.Any(a => a.Id == armyId));
        if (marker is null || !IsInstanceValid(marker.Node)) return (false, false, false, "", Vector2.Zero);
        UpdateArmyConditionBars(marker);
        var point = _camera.UnprojectPosition(marker.Node.GlobalPosition);
        var picked = TryPickArmyStack(point, out var stack, selectableOnly: false) && stack.Any(a => a.Id == armyId);
        return (picked && marker.Node.IsVisibleInTree(), marker.ConditionBars.IsVisibleInTree(),
            marker.ConditionBars.Position == Vector3.Zero && marker.ConditionBars.Offset == Vector2.Zero,
            picked ? _GetTooltip(point) : "", GetGlobalTransformWithCanvas() * point);
    }
    internal string VerificationReadinessPointerTooltip() => _GetTooltip(GetLocalMousePosition());
    internal string VerificationReadinessPopupText()
    {
        // Godot's real tooltip PopupPanel is attached directly to its owner Control.
        // Observe its displayed Label; never create a replacement screenshot overlay.
        foreach (var child in GetChildren())
            if (child is PopupPanel { Visible: true } popup)
                foreach (var content in popup.GetChildren())
                    if (content is Label { Visible: true } caption) return caption.Text;
        return "";
    }
}
