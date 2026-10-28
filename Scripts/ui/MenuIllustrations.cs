using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The front door's pictures: the loading illustrations - the war room, the negotiation hall,
/// the night side of the Earth, the port at dusk - one after another behind the menu, each
/// drifting slowly closer and dissolving into the next, under the atlas grid.
///
/// The drawn backdrop alone said "atlas" and nothing else; a Paradox front door says what the
/// game is about before a button is read. The pictures are the same loading_* sprites a mod
/// replaces or adds (LoadingScreen.IllustrationPaths), the player's "no generated art" setting
/// takes them away, and how dark they sit is the skin's `menu.backdrop_brightness`.
/// </summary>
public partial class MenuIllustrations : Control
{
    /// <summary>Seconds each picture holds, and how long one dissolves into the next.</summary>
    private const float Hold = 16f;
    private const float Fade = 3.5f;

    /// <summary>How much closer a picture drifts over its time on screen: six per cent.</summary>
    private const float Approach = 0.06f;

    private string[] _paths = System.Array.Empty<string>();
    private TextureRect _front, _back;
    private int _turn;
    private float _clock;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        _paths = LoadingScreen.IllustrationPaths();
        if (_paths.Length == 0) { Visible = false; return; }
        _turn = (int)(Time.GetUnixTimeFromSystem() / 60 % _paths.Length);
        _back = Picture();
        _front = Picture();
        AddChild(_back);
        AddChild(_front);
        _front.Texture = LoadingScreen.Picture(_paths[_turn]);
        var brightness = Mathf.Clamp(InterfaceSkin.Metric("menu.backdrop_brightness", 0.42f), 0.05f, 1f);
        Modulate = new Color(brightness, brightness, brightness);
    }

    private static TextureRect Picture() => new()
    {
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        MouseFilter = MouseFilterEnum.Ignore
    };

    public override void _Process(double delta)
    {
        if (_paths.Length == 0 || _front is null) return;
        _clock += (float)delta;
        var size = Size;
        if (_clock >= Hold)
        {
            // The next picture comes up underneath and the front one dissolves away.
            if (_back.Texture is null && _paths.Length > 1)
                _back.Texture = LoadingScreen.Picture(_paths[(_turn + 1) % _paths.Length]);
            var t = Mathf.Clamp((_clock - Hold) / Fade, 0f, 1f);
            _front.Modulate = new Color(1f, 1f, 1f, 1f - t);
            Frame(_back, size, (_clock - Hold) / (Hold + Fade));
            if (t >= 1f)
            {
                (_front, _back) = (_back, _front);
                MoveChild(_front, GetChildCount() - 1);
                _back.Texture = null;
                _back.Modulate = Colors.White;
                _front.Modulate = Colors.White;
                _turn = (_turn + 1) % _paths.Length;
                _clock -= Hold;
            }
        }
        Frame(_front, size, _clock / (Hold + Fade));
    }

    /// <summary>A picture's frame at this point of its time: centred, a little larger as it goes.</summary>
    private static void Frame(TextureRect picture, Vector2 size, float progress)
    {
        var scale = 1f + Approach * Mathf.Clamp(progress, 0f, 1.2f);
        var grown = size * scale;
        picture.Size = grown;
        picture.Position = (size - grown) / 2f;
    }
}
