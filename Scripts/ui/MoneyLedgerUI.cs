using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The money ledger drawn as rows: every income and expense of a country, a movement or an
/// organization for its last closed week (MoneyLedgerView), with the week, the year at that pace,
/// and one plain sentence of why printed under each row.
///
/// Nothing here is on hover only: the game is going to phones, where there is no cursor to hover
/// with. Each row is tall enough to read and touch (52 px), and the explanation is part of it.
/// </summary>
public partial class DealOrForceGame
{
    private const int MoneyLedgerRowHeight = 52;

    /// <summary>The rows, grouped under small captions, added to <paramref name="parent"/>.</summary>
    private void AddMoneyLedgerRows(Container parent, IReadOnlyList<MoneyLedgerLine> rows)
    {
        if (parent is null || rows is null) return;
        var group = "";
        var index = 0;
        foreach (var row in rows)
        {
            if (row.Group != group)
            {
                group = row.Group;
                var caption = Title(group, 12, Ui.Colour("text.accent_dim"), upper: true);
                caption.Name = "MoneyLedgerGroup";
                caption.CustomMinimumSize = new Vector2(0, 28);
                caption.VerticalAlignment = VerticalAlignment.Bottom;
                parent.AddChild(caption);
                index = 0;
            }
            parent.AddChild(MoneyLedgerRow(row, index++ % 2 == 0));
        }
    }

    private Control MoneyLedgerRow(MoneyLedgerLine row, bool alternate)
    {
        var card = new PanelContainer
        {
            Name = row.Total ? "MoneyLedgerTotal" : "MoneyLedgerRow",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, row.Note ? 40 : MoneyLedgerRowHeight),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        card.AddThemeStyleboxOverride("panel", ShellRowStyle(alternate, readOnly: true));
        var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        content.AddThemeConstantOverride("separation", 2);
        card.AddChild(content);

        var tone = row.Note ? "text.caption"
            : MathF.Abs(row.Weekly) < 0.0000005f ? "text.faint"
            : row.Weekly > 0f ? "state.positive" : "state.negative";
        if (row.Total) tone = row.Weekly >= 0f ? "state.positive" : "state.negative";

        var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        top.AddThemeConstantOverride("separation", 10);
        var caption = Title(row.Caption, row.Total ? 15 : row.Note ? 12 : 14,
            Ui.Colour(row.Total ? "text.bright" : row.Note ? "text.caption" : "text.body_soft"));
        caption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        caption.SizeFlagsStretchRatio = 1.4f;
        top.AddChild(caption);
        var hasAmount = !(row.Note && MathF.Abs(row.Weekly) < 0.0000005f);
        if (hasAmount)
        {
            var weekly = Title(Simulation.Loc.Format("{0} a week", SignedMoney(row.Weekly)), row.Total ? 15 : 14, Ui.Colour(tone));
            weekly.HorizontalAlignment = HorizontalAlignment.Right;
            weekly.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            top.AddChild(weekly);
        }
        content.AddChild(top);

        var bottom = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        bottom.AddThemeConstantOverride("separation", 10);
        var why = Title(row.Explanation ?? "", 12, Ui.Colour("text.caption"));
        why.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        why.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        why.SizeFlagsStretchRatio = 1.4f;
        bottom.AddChild(why);
        if (hasAmount)
        {
            var yearly = Title(Simulation.Loc.Format("{0} a year", SignedMoney(row.Yearly)), 12, Ui.Colour("text.faint"));
            yearly.HorizontalAlignment = HorizontalAlignment.Right;
            yearly.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            yearly.VerticalAlignment = VerticalAlignment.Top;
            bottom.AddChild(yearly);
        }
        content.AddChild(bottom);
        return card;
    }

    /// <summary>The country's whole ledger under the economy dossier's week.</summary>
    private void ShowMoneyLedger(Nation nation)
    {
        if (nation is null || _sideDetail is null) return;
        DetailHeading("MONEY IN AND OUT");
        AddMoneyLedgerRows(_sideDetail, MoneyLedgerView.ForNation(_campaign, nation));
    }
}
