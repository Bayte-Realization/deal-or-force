using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce;

public enum PanelHistoryKind
{
    None,
    Screen,
    Region,
    Army
}

public sealed record PanelHistorySnapshot(
    PanelHistoryKind Kind,
    string ScreenName,
    int NationId,
    int StateMapIndex,
    int ProvinceId,
    int[] ArmyIds)
{
    public bool Matches(PanelHistorySnapshot other)
    {
        if (other is null || Kind != other.Kind) return false;
        return Kind switch
        {
            PanelHistoryKind.Screen => string.Equals(ScreenName, other.ScreenName, StringComparison.Ordinal),
            PanelHistoryKind.Region => NationId == other.NationId && StateMapIndex == other.StateMapIndex && ProvinceId == other.ProvinceId,
            PanelHistoryKind.Army => ArmyIds.SequenceEqual(other.ArmyIds),
            _ => true
        };
    }
}

/// <summary>
/// Browser-style Back / Forward panel & selection history stack driven by mouse thumb buttons
/// (MouseButton.Xbutton1 / Xbutton2) and Alt+Left / Alt+Right. Purely client-side presentation state.
/// </summary>
public sealed class PanelHistoryStack
{
    private const int MaxEntries = 32;
    private readonly List<PanelHistorySnapshot> _items = new();
    private int _cursor = -1;

    public bool IsNavigating { get; private set; }
    public bool CanGoBack => _cursor > 0;
    public bool CanGoForward => _cursor >= 0 && _cursor < _items.Count - 1;

    public void Clear()
    {
        _items.Clear();
        _cursor = -1;
        IsNavigating = false;
    }

    public void Record(PanelHistorySnapshot snapshot)
    {
        if (IsNavigating || snapshot is null || snapshot.Kind == PanelHistoryKind.None) return;
        if (_cursor >= 0 && _items[_cursor].Matches(snapshot)) return;

        if (_cursor < _items.Count - 1)
            _items.RemoveRange(_cursor + 1, _items.Count - _cursor - 1);

        _items.Add(snapshot);
        if (_items.Count > MaxEntries)
            _items.RemoveAt(0);
        _cursor = _items.Count - 1;
    }

    public bool TryStepBack(out PanelHistorySnapshot target)
    {
        target = null;
        if (!CanGoBack) return false;
        _cursor--;
        target = _items[_cursor];
        return true;
    }

    public bool TryStepForward(out PanelHistorySnapshot target)
    {
        target = null;
        if (!CanGoForward) return false;
        _cursor++;
        target = _items[_cursor];
        return true;
    }

    public void RunSuppressed(Action restore)
    {
        IsNavigating = true;
        try { restore?.Invoke(); }
        finally { IsNavigating = false; }
    }
}
