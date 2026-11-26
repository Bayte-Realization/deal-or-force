using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DealOrForce.Platform;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyMapSelectionContention(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        var globe = game.VerificationMapControl;
        var player = world.NationByTag("RUS");
        world.ConfigureSinglePlayer(player.Id);
        world.IsPaused = true;
        game.VerificationSelectPlayerCountry();
        game.VerificationContinuityView(false, player.CapitalLatitude, player.CapitalLongitude, 5.4f);
        globe.CountrySelectionOnly = true;
        await Wait(.25);

        var centre = globe.GetViewport().GetVisibleRect().Size / 2;
        var expected = game.VerificationCentrePick();
        Check(expected.ProvinceId > 0 && expected.Nation is not null, "contention test aims at a real province");
        var dispatches = 0;
        GlobePick selected = default;
        globe.CountryPicked += pick => { dispatches++; selected = pick; };

        async Task DispatchWithWriter(string route, Action input, Action after = null)
        {
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = Task.Run(() =>
            {
                world.AccessGate.EnterWrite();
                try { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(4)); }
                finally { world.AccessGate.ExitWrite(); }
            });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                var before = dispatches;
                var start = Stopwatch.GetTimestamp();
                input();
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Check(elapsed < 100 && globe.VerificationMapSelectionPending && dispatches == before &&
                    world.AccessGate.IsWriterActive, $"{route} returns without waiting for the held write gate");
                GD.Print(FormattableString.Invariant($"MAP_SELECTION_CONTENTION route={route} dispatch_ms={elapsed:F3}"));
                after?.Invoke();
            }
            finally { release.Set(); }
            await writer;
            await Wait(.10);
            Check(!globe.VerificationMapSelectionPending, $"{route} leaves no pending input");
        }

        void MouseClick()
        {
            using var down = new InputEventMouseButton { Position = centre, ButtonIndex = MouseButton.Left, Pressed = true };
            using var up = new InputEventMouseButton { Position = centre, ButtonIndex = MouseButton.Left };
            globe._GuiInput(down);
            globe._GuiInput(up);
        }

        void TouchTap()
        {
            using var down = new InputEventScreenTouch { Index = 0, Position = centre, Pressed = true };
            using var up = new InputEventScreenTouch { Index = 0, Position = centre };
            globe._GuiInput(down);
            globe._GuiInput(up);
        }

        await DispatchWithWriter("mouse", MouseClick);
        Check(dispatches == 1 && selected.ProvinceId == expected.ProvinceId && selected.PointerPosition == centre,
            "deferred mouse click opens the original province exactly once");

        await DispatchWithWriter("changed-tool", MouseClick, () => globe.CountrySelectionOnly = false);
        Check(dispatches == 1, "changing tool cancels an already queued primary click");
        globe.CountrySelectionOnly = true;

        await DispatchWithWriter("moved-camera", MouseClick, () => globe.VerificationMapCameraOffset = .2f);
        Check(dispatches == 1, "moving the camera cancels a queued screen coordinate");
        globe.VerificationMapCameraOffset = 0;

        globe.TouchAdapter.SetTapMode(TouchTapMode.Select);
        await DispatchWithWriter("touch", TouchTap);
        Check(dispatches == 2 && selected.ProvinceId == expected.ProvinceId && selected.PointerPosition == centre,
            "deferred touch tap opens the original province exactly once");

        await DispatchWithWriter("escape", TouchTap, () =>
        {
            using var key = new InputEventKey { Pressed = true, Keycode = Key.Escape };
            game._Input(key);
        });
        Check(dispatches == 2, "Escape cancels a queued map tap before it can reopen a panel");

        var army = world.Armies.First(a => a.NationId == player.Id && a.ProvinceId > 0);
        var armySite = world.ProvinceMap.CenterOf(army.ProvinceId);
        game.VerificationContinuityView(false, armySite.Latitude, armySite.Longitude, 5.8f);
        globe.CountrySelectionOnly = false;
        globe.TouchAdapter.SetTapMode(TouchTapMode.BoxSelect);
        await Wait(.35);
        var boxCount = 0;
        var pickedArmy = false;
        globe.SelectionBoxPicked += (armies, _) =>
        {
            boxCount++;
            pickedArmy = armies.Any(candidate => candidate.Id == army.Id);
        };
        await DispatchWithWriter("touch-box", () =>
        {
            var start = centre - new Vector2(145, 100);
            var end = centre + new Vector2(145, 100);
            using var down = new InputEventScreenTouch { Index = 0, Position = start, Pressed = true };
            using var move = new InputEventScreenDrag { Index = 0, Position = end, Relative = end - start };
            using var up = new InputEventScreenTouch { Index = 0, Position = end };
            globe._GuiInput(down);
            globe._GuiInput(move);
            globe._GuiInput(up);
        });
        Check(boxCount == 1 && pickedArmy, "deferred touch rectangle selects the visible army exactly once");
    }
}

public partial class DealOrForceGame
{
    internal GlobePresentation VerificationMapControl => _globe;
}

public sealed partial class GlobePresentation
{
    internal bool VerificationMapSelectionPending => _pendingMapSelection.HasValue;
    internal float VerificationMapCameraOffset { set => _camera.HOffset = value; }
}
