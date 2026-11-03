using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyYamlWorkbench(DealOrForceGame game)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dof-yaml-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var world = game.VerificationWorld;
        world.IsPaused = true;
        var before = CampaignFingerprint.Of(world);
        game.VerificationAuthorRoot(directory);
        try
        {
            foreach (var language in new[] { "english", "russian" })
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                Loc.Language = language; game.Call("RefreshLocalisedText"); GetTree().Root.Size = size;
                var label = language + "-" + size.X;
                var relative = "gui/51_workshop_" + label + ".yaml";
                var path = ScenarioLibrary.SafePath(directory, relative);
                game.Call("OpenContentEditor");
                await Wait(.25);
                var create = game.FindChild("ContentNewDocument", true, false) as Button;
                Check(create is { Disabled: false } && create.IsVisibleInTree(), "new document action is reachable " + label);
                create.EmitSignal(Button.SignalName.Pressed);
                await Wait(.1);
                var dialog = game.FindChild("NewContentDocument", true, false) as ConfirmationDialog;
                var name = dialog?.FindChild("NewContentPath", true, false) as LineEdit;
                Check(name is not null, "new document dialog opens " + label);
                name.Text = relative;
                dialog.GetOkButton().EmitSignal(Button.SignalName.Pressed);
                await Wait(.15);
                Check(game.VerificationAuthorRelative == relative, "new YAML path accepted by real dialog " + label);
                const string raw = "# source comment remains on ordinary save\nmetrics:\n  yaml.workbench: 17\nlayout: {}\n";
                var source = game.FindChild("JSON", true, false) as CodeEdit;
                source.Text = raw;
                game.VerificationAuthorTab(1);
                var validate = game.FindChild("ContentValidate", true, false) as Button;
                var save = game.FindChild("ContentSave", true, false) as Button;
                validate.EmitSignal(Button.SignalName.Pressed);
                Check(game.VerificationAuthorStatus == Loc.Get("YAML valid. Domain rules are also checked when the game loads this content."),
                    "real YAML validation feedback is localised " + label);
                save.EmitSignal(Button.SignalName.Pressed);
                Check(File.Exists(path) && File.ReadAllText(path) == raw, "real save writes comments intact to isolated mod " + label);
                await Wait(.2);
                var files = game.FindChild("ContentFileList", true, false) as ItemList;
                Check(Enumerable.Range(0, files.ItemCount).Any(index => files.GetItemText(index) == relative),
                    "saved YAML appears in actual browser " + label);
                CheckFits(save, "persistent author save " + label);
                CheckFits(source, "source editor " + label);
                var heading = game.FindChild("ContentDocumentHeading", true, false) as Label;
                Check(heading?.Text == relative && heading.Size.X > 160 && heading.Size.Y >= heading.GetMinimumSize().Y,
                    "current document path has readable geometry " + label);
                await Capture("yaml-source-" + label);

                game.VerificationAuthorTab(0);
                await Wait(.15);
                Check(game.VerificationSelectAuthorProperty("/metrics/yaml.workbench"), "YAML property exists in real inspector " + label);
                var value = game.FindChild("PropertyValue", true, false) as CodeEdit;
                value.Text = "42";
                var apply = game.FindChildren("*", "Button", true, false).OfType<Button>()
                    .First(button => button.Text == Loc.Get("Apply property") && button.IsVisibleInTree());
                apply.EmitSignal(Button.SignalName.Pressed);
                save.EmitSignal(Button.SignalName.Pressed);
                using (var saved = StructuredData.ReadDocument(path))
                    Check(saved.RootElement.GetProperty("metrics").GetProperty("yaml.workbench").GetInt32() == 42,
                        "real property action and save retain YAML format and value " + label);
                Check(File.ReadAllText(path + ".bak") == raw, "real editor preserves previous version " + label);
                var badge = game.FindChild("ContentFormatBadge", true, false) as Label;
                Check(badge?.Text == "YAML", "document format remains visible " + label);
                CheckFits(apply, "author property action " + label);
                await Wait(.2);
                await Capture("yaml-properties-" + label);

                const string external = "metrics: {yaml.workbench: 99}\n";
                File.WriteAllText(path, external);
                source.Text = raw;
                save.EmitSignal(Button.SignalName.Pressed);
                Check(File.ReadAllText(path) == external && game.VerificationAuthorStatus == Loc.Get(
                    "Content changed outside this editor, or the document already exists. Nothing was overwritten. Reopen it from the file list."),
                    "real stale-draft refusal preserves external edit and is localised " + label);
                game.Call("CloseAuthorWindow", false);
                await Wait(.1);
            }
            Check(before == CampaignFingerprint.Of(world), "authoring and all view/language changes leave campaign unchanged");
        }
        finally
        {
            game.Call("CloseAuthorWindow", false);
            game.VerificationAuthorRoot(null);
            Directory.Delete(directory, true); // Precisely the random fixture directory created above.
        }
    }
}

public partial class DealOrForceGame
{
    internal void VerificationAuthorRoot(string root) => _authorVerificationRoot = root;
    internal string VerificationAuthorRelative => _authorRelative;
    internal string VerificationAuthorStatus => _authorStatus?.Text;
    internal void VerificationAuthorTab(int index) => _authorTabs.CurrentTab = index;
    internal bool VerificationSelectAuthorProperty(string pointer)
    {
        bool Select(TreeItem item)
        {
            if (item is null) return false;
            if (item.GetMetadata(0).AsString() == pointer) { item.Select(0); return true; }
            for (var child = item.GetFirstChild(); child is not null; child = child.GetNext())
                if (Select(child)) return true;
            return false;
        }
        return Select(_authorTree.GetRoot());
    }
}
