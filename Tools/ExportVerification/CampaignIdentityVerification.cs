using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task<bool> ClickCampaignIdentity(Button button, string caption)
    {
        Check(button is { Disabled: false } && button.IsVisibleInTree(), "campaign identity control is available: " + caption);
        if (button is not { Disabled: false } || !button.IsVisibleInTree()) return false;
        for (Node parent = button.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) scroll.EnsureControlVisible(button);
        await Wait(.15);
        var root = GetTree().Root;
        var point = button.GetGlobalTransformWithCanvas() * (button.Size * .5f);
        if (button.GetWindow() != root) point += button.GetWindow().Position;
        root.PushInput(new InputEventMouseMotion { Position = point }, true);
        var hit = button.GetViewport().GuiGetHoveredControl() == button;
        Check(hit, "campaign identity mouse reaches the exact control: " + caption);
        if (!hit) return false;
        root.PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left,
            ButtonMask = MouseButtonMask.Left, Pressed = true }, true);
        root.PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Wait(.15);
        return true;
    }

    private async Task VerifyCampaignIdentity(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var nation = c.Player;
        Check(nation is not null && CampaignCosmetics.CanEdit(c, nation, nation), "campaign identity uses an authorised player nation");
        if (nation is null || !CampaignCosmetics.CanEdit(c, nation, nation)) return;
        var original = (nation.CustomName, nation.CustomFlag, nation.CustomFlagDesignJson,
            nation.Color, nation.MapColorBeforeOverride, c.IsPaused);
        var interceptor = c.Commands.SubmitInterceptor;
        var languageBefore = Loc.Language; var sizeBefore = GetTree().Root.Size;
        var noModRoot = Path.Combine(OS.GetUserDataDir(), "campaign-identity-no-mod-" + Guid.NewGuid().ToString("N"));
        var oldAuthorRoot = game.VerificationCampaignIdentityModRoot(noModRoot);
        Window Visible(string name) => game.FindChildren(name, "Window", true, false).OfType<Window>().FirstOrDefault(w => w.Visible);
        Button ButtonIn(Window window, string name) => window?.FindChild(name, true, false) as Button;
        void CheckWindow(Window window, string caption)
        {
            Check(window is not null && window.Visible, "campaign identity window opens: " + caption);
            if (window is null) return;
            var rootSize = GetTree().Root.GetVisibleRect().Size;
            Check(window.Position.X >= 0 && window.Position.Y >= 0 &&
                window.Position.X + window.Size.X <= rootSize.X + 1 && window.Position.Y + window.Size.Y <= rootSize.Y + 1,
                "campaign identity window stays inside the viewport: " + caption);
            Check(window.FindChildren("*", "Button", true, false).OfType<Button>().All(button =>
                button.Text != Loc.Get("Back to scenarios") && button.Text != Loc.Get("Save flag to my mod") &&
                button.Text != Loc.Get("Open mod folder")), "campaign mode has no scenario exit or implicit mod write action: " + caption);
        }
        try
        {
            c.IsPaused = true;
            foreach (var language in new[] { "english", "russian" })
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                Loc.Language = language; GetTree().Root.Size = size; game.Call("RefreshLocalisedText");
                game.Call("CloseSidePanel"); game.Call("CloseMapSelection");
                game.VerificationInspectPick(new GlobePick(nation, null, -1, nation.CapitalLatitude, nation.CapitalLongitude));
                await Wait(.2);
                var caption = language + " " + size.X;
                var before = CampaignFingerprint.Of(c); var clock = c.ElapsedHours; var random = c.Random.State;
                if (!await ClickCampaignIdentity(game.FindChild("CountryCardAppearance", true, false) as Button, "appearance " + caption)) continue;
                var identity = Visible("CampaignCountryIdentity"); CheckWindow(identity, caption);
                if (identity is null) continue;
                var name = identity.FindChild("CampaignIdentityName", true, false) as LineEdit;
                var hex = identity.FindChild("CampaignIdentityHex", true, false) as LineEdit;
                name.Text = language == "russian" ? "Новый облик — тест" : "Campaign identity — test";
                name.EmitSignal(LineEdit.SignalName.TextChanged, name.Text);
                var palette = identity.FindChild("CampaignIdentityPalette", true, false) as HFlowContainer;
                var picker = identity.FindChild("CampaignIdentityColour", true, false) as ColorPickerButton;
                await ClickCampaignIdentity(palette?.GetChildOrNull<Button>(5), "palette preset " + caption);
                Check(hex.Text == FlagDesign.Palette[5] && picker.Color.IsEqualApprox(Color.FromHtml(FlagDesign.Palette[5])) &&
                    CampaignFingerprint.Of(c) == before && c.Random.State == random,
                    "real palette click synchronises the HEX and colour picker without applying the draft: " + caption);
                hex.Text = nation.Color == "#5637a8" ? "#327b8f" : "#5637a8";
                hex.EmitSignal(LineEdit.SignalName.TextChanged, hex.Text);
                Check(CampaignFingerprint.Of(c) == before && c.Random.State == random && c.ElapsedHours == clock && c.IsPaused,
                    "name and palette drafts leave the campaign, random state and clock unchanged: " + caption);
                await Capture("campaign-identity-draft-" + language + "-" + size.X);
                var sent = new List<SetNationalCosmeticsCommand>();
                c.Commands.SubmitInterceptor = command =>
                {
                    if (command is SetNationalCosmeticsCommand cosmetics) { sent.Add(cosmetics); return true; }
                    return interceptor?.Invoke(command) ?? false;
                };
                await ClickCampaignIdentity(ButtonIn(identity, "CampaignIdentityApply"), "send appearance " + caption);
                Check(sent.Count == 1 && sent[0].IssuerId == nation.Id && sent[0].TargetNationId == nation.Id &&
                    sent[0].CustomName == name.Text && sent[0].MapColor == hex.Text && sent[0].CustomFlagDesignJson is null &&
                    CampaignFingerprint.Of(c) == before,
                    "appearance uses exactly one intercepted authoritative command without optimistic local mutation: " + caption);
                Check((identity.FindChild("CampaignIdentityStatus", true, false) as Label)?.Text ==
                    Loc.Get("Appearance request sent to the host; waiting for confirmation."), "transported appearance is pending, not claimed applied: " + caption);
                c.Commands.SubmitInterceptor = interceptor;
                if (interceptor is not null) continue;
                var chosenName = name.Text; var chosenColour = hex.Text; var revision = c.MapRevision;
                await ClickCampaignIdentity(ButtonIn(identity, "CampaignIdentityApply"), "apply appearance " + caption);
                Check(nation.CustomName == chosenName && nation.DisplayName == chosenName && nation.MapName == chosenName &&
                    nation.Color == chosenColour && c.MapRevision > revision,
                    "ordinary command changes the real country name and map colour and invalidates the map: " + caption);
                Check(game.VerificationCampaignPaletteColour(nation.MapIndex).IsEqualApprox(Color.FromHtml(chosenColour)),
                    "the actual uploaded political map palette uses the chosen colour: " + caption);
                await ClickCampaignIdentity(ButtonIn(identity, "CampaignIdentityEditFlag"), "campaign flag route " + caption);
                var flag = Visible("CampaignFlagEditor"); CheckWindow(flag, "flag " + caption);
                Check(Visible("CampaignCountryIdentity") is null && !game.VerificationCampaignIdentityAuthorOpen,
                    "flag editing opens over the campaign, not the scenario workspace: " + caption);
                if (flag is null) continue;
                var pattern = flag.FindChild("FlagEditorPattern", true, false) as OptionButton;
                var document = flag.FindChild("FlagEditorDocument", true, false) as CodeEdit;
                var flagBefore = CampaignFingerprint.Of(c);
                pattern.Selected = (pattern.Selected + 1) % pattern.ItemCount;
                pattern.EmitSignal(OptionButton.SignalName.ItemSelected, pattern.Selected);
                Check(CampaignFingerprint.Of(c) == flagBefore, "editing the flag preview is read-only: " + caption);
                await Capture("campaign-flag-draft-" + language + "-" + size.X);
                var draft = document.Text;
                sent.Clear(); c.Commands.SubmitInterceptor = command =>
                {
                    if (command is SetNationalCosmeticsCommand cosmetics) { sent.Add(cosmetics); return true; }
                    return interceptor?.Invoke(command) ?? false;
                };
                await ClickCampaignIdentity(ButtonIn(flag, "CampaignFlagApply"), "send flag " + caption);
                // Compare validated designs rather than presentation formatting, while still
                // checking the exact issuer, target and absence of local mutation.
                Check(sent.Count == 1 &&
                    CampaignCosmetics.TryNormaliseFlagDesign(sent[0].CustomFlagDesignJson, nation.Id, out var transmitted, out _) &&
                    CampaignCosmetics.TryNormaliseFlagDesign(draft, nation.Id, out var previewed, out _) && transmitted == previewed &&
                    sent[0].IssuerId == nation.Id && sent[0].TargetNationId == nation.Id &&
                    sent[0].CustomName == nation.CustomName && sent[0].MapColor is null && CampaignFingerprint.Of(c) == flagBefore,
                    "flag draft also travels through the host command interceptor without local writes: " + caption);
                if (sent.Count != 1 || sent[0].CustomFlagDesignJson != draft)
                    GD.Print($"CAMPAIGN_FLAG_TRANSPORT count={sent.Count} preview={draft} sent={(sent.Count > 0 ? sent[0].CustomFlagDesignJson : "<none>")}");
                c.Commands.SubmitInterceptor = interceptor;
                await ClickCampaignIdentity(ButtonIn(flag, "CampaignFlagApply"), "apply flag " + caption);
                Check(CampaignCosmetics.TryNormaliseFlagDesign(draft, nation.Id, out var expected, out _) &&
                    nation.CustomFlagDesignJson == expected && !FlagDesignCatalog.Has("flag_campaign_cosmetic_" + nation.Id),
                    "applied flag lives in Nation save state and not the global mod catalogue: " + caption);
                var texture = DealOrForceGame.FlagTextureFor(nation.Tag);
                Check(texture is not null && Enumerable.Range(0, 100).All(_ =>
                    ReferenceEquals(texture, DealOrForceGame.FlagTextureFor(nation.Tag))) && game.VerificationCampaignFlagCacheCount == 1,
                    "repeated campaign flag reads reuse one bounded per-country texture: " + caption);
                var hudBadge = (game.FindChild("HudCountryRank", true, false) as Control)?.GetParent()?.GetChildOrNull<Control>(0);
                Check(hudBadge?.GetChildOrNull<TextureRect>(0)?.Texture == texture,
                    "the actual HUD uses the same flag source as map markers: " + caption);
                Check(game.VerificationCampaignCardIdentityMatches(nation, texture),
                    "the already open country card immediately shows the applied name and flag on pause: " + caption);
                await Capture("campaign-flag-applied-" + language + "-" + size.X);
                await ClickCampaignIdentity(ButtonIn(flag, "CampaignIdentityBack"), "back to campaign " + caption);
                Check(Visible("CampaignFlagEditor") is null && !game.VerificationCampaignIdentityAuthorOpen &&
                    c.IsPaused && c.ElapsedHours == clock, "Back returns to the existing campaign without changing pause or time: " + caption);
                // Escape and the native close button use the same unsaved-draft guard.
                foreach (var escape in new[] { true, false })
                {
                    game.VerificationOpenCampaignIdentity(); await Wait(.15);
                    identity = Visible("CampaignCountryIdentity");
                    var field = identity.FindChild("CampaignIdentityName", true, false) as LineEdit;
                    field.Text = "Discard me"; field.EmitSignal(LineEdit.SignalName.TextChanged, field.Text);
                    var cancelledBefore = CampaignFingerprint.Of(c);
                    if (escape)
                    {
                        GetTree().Root.PushInput(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true }, true);
                        GetTree().Root.PushInput(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = false }, true);
                    }
                    else identity.EmitSignal(Window.SignalName.CloseRequested);
                    await Wait(.15);
                    var discard = identity.FindChild("CampaignIdentityDiscard", true, false) as ConfirmationDialog;
                    Check(discard is { Visible: true }, "Escape/window close preserves an unsaved draft until confirmation: " + caption + " " + escape);
                    if (discard is not { Visible: true }) { identity.QueueFree(); continue; }
                    await ClickCampaignIdentity(discard.GetCancelButton(), "keep draft " + caption);
                    Check(identity.Visible && field.Text == "Discard me" && CampaignFingerprint.Of(c) == cancelledBefore,
                        "cancelling close keeps the draft without touching campaign state: " + caption);
                    await ClickCampaignIdentity(ButtonIn(identity, "CampaignIdentityBack"), "discard draft route " + caption);
                    discard = identity.FindChild("CampaignIdentityDiscard", true, false) as ConfirmationDialog;
                    if (discard is not null) await ClickCampaignIdentity(discard.GetOkButton(), "discard confirmation " + caption);
                    Check(Visible("CampaignCountryIdentity") is null && CampaignFingerprint.Of(c) == cancelledBefore &&
                        c.ElapsedHours == clock && c.IsPaused, "confirmed close discards only the draft and returns to the campaign: " + caption);
                }
                Check(!Directory.Exists(noModRoot), "campaign editing never creates the redirected authoring directory: " + caption);
            }
            if (interceptor is null)
            {
                var fingerprint = CampaignFingerprint.Of(c);
                var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "campaign-identity-proof"), "campaign-identity-proof");
                Check(loaded.Success && CampaignFingerprint.Of(loaded.Campaign) == fingerprint &&
                    loaded.Campaign.Player.CustomFlagDesignJson == nation.CustomFlagDesignJson &&
                    loaded.Campaign.Player.Color == nation.Color && loaded.Campaign.Player.CustomName == nation.CustomName,
                    "real save/network snapshot round-trip preserves identity and the authoritative fingerprint");
                if (loaded.Success)
                {
                    var originalDesign = nation.CustomFlagDesignJson;
                    var replacement = new FlagDesign { Id = "flag_probe", Pattern = "solid", Colours = new List<string> { "#ffcc00" } };
                    var copy = loaded.Campaign; var copyNation = copy.Player;
                    copy.Commands.Enqueue(new SetNationalCosmeticsCommand { IssuerId = copyNation.Id, CustomName = copyNation.CustomName,
                        CustomFlag = copyNation.CustomFlag, CustomFlagDesignJson = replacement.ToJson() });
                    copy.Commands.ApplyAll(copy);
                    var first = DealOrForceGame.FlagTextureFor(nation.Tag);
                    game.VerificationBindCampaignIdentityFlags(copy);
                    var other = DealOrForceGame.FlagTextureFor(copyNation.Tag);
                    Check(other is not null && !ReferenceEquals(first, other) && nation.CustomFlagDesignJson == originalDesign &&
                        game.VerificationCampaignFlagCacheCount == 1, "switching campaigns with equal country IDs never leaks a cached flag design");
                    game.VerificationBindCampaignIdentityFlags(c); game.Call("Refresh");
                }
            }
        }
        finally
        {
            c.Commands.SubmitInterceptor = interceptor;
            foreach (var window in game.GetChildren().OfType<Window>().Where(window =>
                window.Name.ToString() is "CampaignCountryIdentity" or "CampaignFlagEditor")) window.QueueFree();
            (nation.CustomName, nation.CustomFlag, nation.CustomFlagDesignJson,
                nation.Color, nation.MapColorBeforeOverride, c.IsPaused) = original;
            game.VerificationCampaignIdentityModRoot(oldAuthorRoot);
            game.VerificationBindCampaignIdentityFlags(c);
            Loc.Language = languageBefore; GetTree().Root.Size = sizeBefore;
            game.Call("CloseMapSelection"); game.Call("RefreshLocalisedText");
        }
    }
}

public partial class DealOrForceGame
{
    internal void VerificationOpenCampaignIdentity() => OpenCampaignCountryIdentity();
    internal bool VerificationCampaignIdentityAuthorOpen => IsInstanceValid(_authorCanvas);
    internal int VerificationCampaignFlagCacheCount => CampaignFlagCache.Count;
    internal void VerificationBindCampaignIdentityFlags(Campaign campaign) => BindFlags(campaign);
    internal bool VerificationCampaignCardIdentityMatches(Nation nation, Texture2D texture)
        => _countryCardName.Text == nation.DisplayName && _countryCardFlag.GetChildOrNull<TextureRect>(0)?.Texture == texture;
    internal Color VerificationCampaignPaletteColour(int mapIndex) => _globe.VerificationNationPaletteColour(mapIndex);
    internal string VerificationCampaignIdentityModRoot(string root)
    {
        var before = _authorVerificationRoot; _authorVerificationRoot = root; return before;
    }
}

public sealed partial class GlobePresentation
{
    internal Color VerificationNationPaletteColour(int mapIndex)
    {
        using var image = _nationPaletteTexture?.GetImage();
        return image is null ? default : image.GetPixel(mapIndex % 256, mapIndex / 256);
    }
}
