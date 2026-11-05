using System;
using System.IO;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void AddAuthorPackageBar(VBoxContainer rows, Action reopen)
    {
        var bar = new HBoxContainer { Name = "AuthorPackageBar" };
        rows.AddChild(bar);
        var destination = Title(Loc.Format("Editing mod: {0}", Path.GetFileName(AuthorRoot)), 13, Ui.Colour("text.caption"));
        destination.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        destination.AutowrapMode = TextServer.AutowrapMode.Off;
        destination.CustomMinimumSize = new Vector2(0, 32);
        destination.ClipText = true;
        destination.TooltipText = Ui.Tip(AuthorRoot);
        bar.AddChild(destination);
        bar.AddChild(ActionButton("Create / select mod", () => ConfirmAuthorExit(() => OpenAuthorPackagePicker(reopen)), 210, 32));
    }

    private void OpenAuthorPackagePicker(Action reopen)
    {
        if (!IsInstanceValid(_authorCanvas)) return;
        var dialog = new Window { Name = "AuthorPackagePicker", Title = Tr("Create / select mod"),
            Size = new Vector2I(640, 460), MinSize = new Vector2I(480, 400), Exclusive = true,
            Theme = ScenarioEditorTheme.Create() };
        _authorCanvas.AddChild(dialog);
        var surface = new Panel();
        dialog.AddChild(surface);
        surface.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        surface.AddThemeStyleboxOverride("panel", ScenarioEditorTheme.Surface("shell.background", "shell.border", 0));
        surface.MouseFilter = Control.MouseFilterEnum.Ignore;
        var rows = new VBoxContainer { OffsetLeft = 16, OffsetTop = 12, AnchorRight = 1, AnchorBottom = 1,
            OffsetRight = -16, OffsetBottom = -12 };
        dialog.AddChild(rows);
        var info = Title("Packages are saved to your Documents mods folder. Enable a new mod in the launcher before starting a new campaign.", 13, Ui.Colour("text.muted"));
        info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(info);
        var existing = new OptionButton { Name = "AuthorExistingPackages", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddChild(existing);
        var error = Title("", 13, Ui.Colour("state.warning"));
        error.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var packages = Array.Empty<ModWorkspaceEntry>();
        try
        {
            ContentWorkbench.EnsureMod(AuthorRoot);
            packages = System.Linq.Enumerable.ToArray(ModWorkspace.Discover(AuthorModsRoot));
            foreach (var package in packages) existing.AddItem(package.Name + " · " + package.Id);
        }
        catch (Exception e) { error.Text = e.Message; }
        void Select(string root)
        {
            _authorSelectedRoot = root;
            _authorDirty = false;
            dialog.QueueFree();
            reopen(); // Re-read baselines from the chosen package; never keep another mod's draft.
        }
        var choose = ActionButton("Use selected mod", () =>
        {
            if (existing.Selected >= 0 && existing.Selected < packages.Length) Select(packages[existing.Selected].Root);
        }, 0, 34);
        choose.Disabled = packages.Length == 0;
        rows.AddChild(choose);
        rows.AddChild(new HSeparator());
        rows.AddChild(Title("New mod ID", 13, Ui.Colour("text.caption")));
        var id = new LineEdit { Name = "AuthorNewModId", PlaceholderText = "my_mod", MaxLength = 64 };
        rows.AddChild(id);
        rows.AddChild(Title("New mod name", 13, Ui.Colour("text.caption")));
        var name = new LineEdit { Name = "AuthorNewModName", MaxLength = 120 };
        rows.AddChild(name);
        rows.AddChild(ActionButton("Create mod", () =>
        {
            try { Select(ModWorkspace.Create(AuthorModsRoot, id.Text.Trim(), name.Text).Root); }
            catch (Exception e) { error.Text = Tr(e.Message); }
        }, 0, 36, true));
        rows.AddChild(error);
        rows.AddChild(ActionButton("Cancel", () => dialog.QueueFree(), 0, 32));
        dialog.CloseRequested += () => dialog.QueueFree();
        dialog.PopupCentered();
    }
}
