using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using DealOrForce.Modding;
using Godot;

namespace DealOrForce;

/// <summary>Metadata-only menu adapter. Only an explicit selection invokes mod code.</summary>
internal static class ModFrontendMenu
{
    internal static MenuButton Create(Func<IReadOnlyList<ModFrontendActionDescriptor>> describe, Action<string> selected)
    {
        var button = new MenuButton { Name = "ModFrontendActions", Text = Loc.Get("Mod actions"),
            CustomMinimumSize = new Vector2(160, 36), ClipText = true };
        button.SetMeta("loc_source", "Mod actions");
        DealOrForceGame.StyleButton(button, false);
        var popup = button.GetPopup();
        IReadOnlyList<ModFrontendActionDescriptor> actions = Array.Empty<ModFrontendActionDescriptor>();
        popup.AboutToPopup += () =>
        {
            popup.Clear(); actions = describe();
            for (var i = 0; i < actions.Count; i++)
                popup.AddItem(Loc.Get(actions[i].Caption), i);
        };
        popup.IdPressed += id =>
        {
            if (id >= 0 && id < actions.Count) selected(actions[(int)id].FullId);
        };
        button.Visible = describe().Count > 0;
        return button;
    }
}
