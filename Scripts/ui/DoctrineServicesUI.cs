using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private string _doctrineService = "";
    private Label _doctrineCatalogueHeading;
    private Control _doctrineServiceTabs;
    private readonly Dictionary<string, Button> _doctrineServiceButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _doctrineLastServiceBook = new(StringComparer.Ordinal);

    private static string DoctrineServiceTitle(string slot) => slot switch
    {
        DoctrineSlots.Air => "AIR DOCTRINES",
        DoctrineSlots.Sea => "NAVAL DOCTRINES",
        DoctrineSlots.General => "STAFF DOCTRINES",
        _ => "GROUND DOCTRINES"
    };

    private static string DoctrineServiceIcon(string slot) => slot switch
    {
        DoctrineSlots.Air => "air",
        DoctrineSlots.Sea => "navy",
        DoctrineSlots.General => "agenda",
        _ => "military"
    };

    private void BuildDoctrineServices(VBoxContainer rows)
    {
        var tabs = TabStrip(rows, "DoctrineServiceTabs");
        _doctrineServiceTabs = tabs;
        foreach (var slot in DoctrineSlots.All)
        {
            var id = slot;
            var button = ActionButton(DoctrineServiceTitle(slot), () => SelectDoctrineService(id), 160, 34,
                false, "res://game/gfx/interface/icons/icon_" + DoctrineServiceIcon(slot) + ".png");
            button.Name = "DoctrineService_" + slot;
            button.SetMeta("atlas_tab_button", true);
            tabs.AddChild(button);
            _doctrineServiceButtons[slot] = button;
        }
    }

    private void SelectDoctrineService(string slot)
    {
        if (!DoctrineSlots.Knows(slot)) return;
        if (TacticalDoctrineCatalog.Find(_doctrineBook) is { } current)
            _doctrineLastServiceBook[current.Slot] = current.Id;
        var remembered = TacticalDoctrineCatalog.Find(_doctrineLastServiceBook.GetValueOrDefault(slot, ""));
        var book = remembered?.Slot == slot ? remembered : TacticalDoctrineCatalog.OnShelf(slot).FirstOrDefault();
        if (book is null) return;
        _doctrineService = slot;
        SelectDoctrineBook(book.Id);
        if (_doctrineBookList?.GetParent() is ScrollContainer scroll) scroll.ScrollVertical = 0;
    }

    private void RefreshDoctrineServices()
    {
        if (TacticalDoctrineCatalog.Find(_doctrineBook) is { } current)
        {
            _doctrineService = current.Slot;
            _doctrineLastServiceBook[current.Slot] = current.Id;
        }
        var player = _campaign?.Player;
        if (player is not null) TacticalDoctrines.EnsureShelves(player);

        foreach (var (slot, button) in _doctrineServiceButtons)
        {
            StyleButton(button, slot == _doctrineService);
            button.Disabled = !TacticalDoctrineCatalog.OnShelf(slot).Any();
            if (player is null) continue;
            var shelfBook = TacticalDoctrines.BookOn(player, slot);
            var track = player.TacticalDoctrine.Track(slot);
            button.TooltipText = Ui.Tip(shelfBook is not null
                ? Loc.Format("doctrine.ui.service_tab_tip_fmt", Tr(DoctrineServiceTitle(slot)), Tr(shelfBook.Name), track.Known.Count, shelfBook.Nodes.Count)
                : Loc.Format("doctrine.ui.service_tab_empty_tip_fmt", Tr(DoctrineServiceTitle(slot))));
        }

        if (_doctrineCatalogueHeading is not null)
        {
            var shelfCount = TacticalDoctrineCatalog.OnShelf(_doctrineService).Count();
            _doctrineCatalogueHeading.Text = Loc.Get(DoctrineServiceTitle(_doctrineService)) + " · " + shelfCount;
        }
        foreach (var (id, button) in _doctrineBookButtons)
            (button.GetParent() as Control).Visible = TacticalDoctrineCatalog.Find(id)?.Slot == _doctrineService;
    }
}
