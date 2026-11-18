using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private DeveloperConsole _developerConsole;
    private Control _developerConsoleLayer => _developerConsole?.Layer;
    private bool _observerMode;
    private bool _freeCameraMode;

    private ConsoleContext CreateConsoleContext() => new()
    {
        GetCampaign = () => _campaign,
        GetGlobe = () => _globe,
        GetSelectedNation = () => _selectedNation,
        SetSelectedNation = nation => _selectedNation = nation,
        GetSelectedState = () => _selectedState,
        SetSelectedState = state => _selectedState = state,
        GetSelectedProvinceId = () => _selectedProvinceId,
        GetSelectedArmies = () => _selectedArmies,
        SetObserverMode = enabled => _observerMode = enabled,
        GetFreeCameraMode = () => _freeCameraMode,
        SetSimulating = active => _simulating = active,
        GetNetworkConnected = () => _networkConnected,
        GetModLoader = () => _modLoader,
        GetModFingerprint = () => ModFingerprint,
        GetSaveRoot = () => SaveRoot,

        CreateFullLayer = FullLayer,
        AddChildNode = node => AddChild(node),
        CreateTexturedPanel = parent => TexturedPanel(parent),
        CreatePanelRows = PanelRows,
        CreateTitle = (text, size, colour) => Title(text, size, colour),
        CreateActionButton = (text, callback, width, height) => ActionButton(text, callback, width, height),
        ApplyPlaceholder = Placeholder,
        Tr = Tr,

        Refresh = Refresh,
        RefreshEvents = RefreshEventInterface,
        RefreshLocalisedText = RefreshLocalisedText,
        LayoutCampaignShell = LayoutCampaignShell,
        RepaintPresentation = () => PresentationSkinRepaint.Repaint(this, (s, c) => PanelStyle((PanelSurface)s, c), ShellEdgeStyle),
        TogglePause = TogglePause,
        SetSpeed = speed => SetSpeed(speed),
        ToggleFreeCamera = ToggleFreeCamera,
        ClearArmySelectionAndClose = ClearArmySelectionAndClose,
        OpenPeaceConference = OpenPeaceConference,
        ApplySkinOverlay = errors => ApplySkinOverlay(errors),
        RestoreCampaignFromSlot = RestoreCampaignFromSlot,
        CreateSupportReport = CreateSupportReport,
        SetObserverIdentityBadge = text => _identityTag.Text = text,
        UpdateLanguageButton = UpdateConsoleLanguageButton,
        MeasureLayout = () => LayoutAudit.Report(this),
        ToggleCountryTags = ConsoleCountryTags,
        CastSnowgrave = ConsoleSnowgrave,
        CastWeirdRoute = ConsoleWeirdRoute,
        TouchGrass = ConsoleTouchGrass
    };

    private DeveloperConsole EnsureDeveloperConsole() =>
        _developerConsole ??= new DeveloperConsole(CreateConsoleContext());

    private void BuildDeveloperConsole() => EnsureDeveloperConsole().Build();

    private void ToggleDeveloperConsole() => _developerConsole?.Toggle();

    private void CloseDeveloperConsole() => _developerConsole?.Close();

    private void ExecuteDeveloperCommand(string raw) => _developerConsole?.Execute(raw);

    private void UpdateConsoleLanguageButton()
    {
        if (_languageButton is null || !GodotObject.IsInstanceValid(_languageButton)) return;
        var caption = LanguageButtonLabel();
        _languageButton.Text = caption;
        _languageButton.SetMeta(LocAppliedMeta, caption);
    }

    // ExportVerification and graphics probes invoke ConsoleTag / ConsoleAdvance via Node.Call().
    private string ConsoleTag(string tag) => ConsoleWorldCommands.Tag(CreateConsoleContext(), tag);

    private string ConsoleAdvance(string argument) => ConsoleWorldCommands.Advance(CreateConsoleContext(), argument);

    private (float, float) StateCentre(ScenarioState state) => ConsoleContext.StateCentre(_campaign, state);
}
