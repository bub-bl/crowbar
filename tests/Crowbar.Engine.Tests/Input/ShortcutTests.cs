using Crowbar.Engine.InputSystem;

namespace Crowbar.Engine.InputSystem.Tests;

/// <summary>
/// KeyChord + ShortcutManager tests. Shares the Input facade with
/// InputSystemTests through the same xunit collection so the static state is
/// never mutated in parallel.
/// </summary>
[Collection("InputFacade")]
public class ShortcutTests
{
    private readonly FakeInputSource _source = new();

    public ShortcutTests()
    {
        Input.Bind(_source);
        Input.UnbindAction("run");
        Input.UnbindAction("jump");
        ShortcutManager.Instance.Clear();
        ShortcutManager.Instance.IsEnabled = null;
    }

    [Fact]
    public void Chord_FiresOnlyWithTheExactModifiers()
    {
        Input.Poll();

        // Ctrl+S pressed → the chord matches.
        _source.SetKey(Key.LeftControl, true);
        Input.Poll();
        _source.SetKey(Key.S, true);
        Input.Poll();
        Assert.True(Input.WasPressed(new KeyChord(Key.S, KeyModifiers.Control)));

        // Ctrl+Shift+S must not fire the Ctrl+S chord.
        _source.SetKey(Key.S, false);
        Input.Poll();
        _source.SetKey(Key.LeftShift, true);
        _source.SetKey(Key.S, true);
        Input.Poll();
        Assert.False(Input.WasPressed(new KeyChord(Key.S, KeyModifiers.Control)));

        // Plain S (no modifier) must not fire the Ctrl+S chord, but fires the bare chord.
        _source.SetKey(Key.LeftControl, false);
        _source.SetKey(Key.LeftShift, false);
        _source.SetKey(Key.S, false);
        Input.Poll();
        _source.SetKey(Key.S, true);
        Input.Poll();
        Assert.False(Input.WasPressed(new KeyChord(Key.S, KeyModifiers.Control)));
        Assert.True(Input.WasPressed(new KeyChord(Key.S)));
    }

    [Fact]
    public void Chord_RightSideModifierCountsAsTheModifier()
    {
        Input.Poll();
        _source.SetKey(Key.RightControl, true);
        Input.Poll();
        _source.SetKey(Key.S, true);
        Input.Poll();

        Assert.True(Input.WasPressed(new KeyChord(Key.S, KeyModifiers.Control)));
        Assert.Equal(KeyModifiers.Control, Input.HeldModifiers());
    }

    [Fact]
    public void Chord_FiresOnlyOnThePressEdge()
    {
        Input.Poll();
        _source.SetKey(Key.LeftControl, true);
        Input.Poll();
        _source.SetKey(Key.S, true);
        Input.Poll();
        Assert.True(Input.WasPressed(new KeyChord(Key.S, KeyModifiers.Control)));

        // Still held on the next frame: no new edge.
        Input.Poll();
        Assert.False(Input.WasPressed(new KeyChord(Key.S, KeyModifiers.Control)));
        Assert.True(Input.IsDown(new KeyChord(Key.S, KeyModifiers.Control)));

        _source.SetKey(Key.S, false);
        Input.Poll();
        Assert.True(Input.WasReleased(new KeyChord(Key.S, KeyModifiers.Control)));
    }

    [Fact]
    public void ShortcutManager_FiresRegisteredChordOncePerPress()
    {
        var fired = 0;
        ShortcutManager.Instance.Register(new KeyChord(Key.S, KeyModifiers.Control), () => fired++);

        Input.Poll();
        _source.SetKey(Key.LeftControl, true);
        Input.Poll();
        _source.SetKey(Key.S, true);
        Input.Poll();
        ShortcutManager.Instance.Update();
        Assert.Equal(1, fired);

        // Held on the next frame: no re-fire.
        Input.Poll();
        ShortcutManager.Instance.Update();
        Assert.Equal(1, fired);

        // Release and press again: fires once more.
        _source.SetKey(Key.S, false);
        Input.Poll();
        _source.SetKey(Key.S, true);
        Input.Poll();
        ShortcutManager.Instance.Update();
        Assert.Equal(2, fired);
    }

    [Fact]
    public void ShortcutManager_RespectsIsEnabled()
    {
        var fired = 0;
        ShortcutManager.Instance.Register(new KeyChord(Key.S, KeyModifiers.Control), () => fired++);
        ShortcutManager.Instance.IsEnabled = () => false;

        Input.Poll();
        _source.SetKey(Key.LeftControl, true);
        Input.Poll();
        _source.SetKey(Key.S, true);
        Input.Poll();
        ShortcutManager.Instance.Update();
        Assert.Equal(0, fired);
    }

    [Fact]
    public void ShortcutManager_UnregisterStopsFiring()
    {
        var fired = 0;
        var chord = new KeyChord(Key.S, KeyModifiers.Control);
        ShortcutManager.Instance.Register(chord, () => fired++);
        ShortcutManager.Instance.Unregister(chord);

        Input.Poll();
        _source.SetKey(Key.LeftControl, true);
        Input.Poll();
        _source.SetKey(Key.S, true);
        Input.Poll();
        ShortcutManager.Instance.Update();
        Assert.Equal(0, fired);
    }

    [Fact]
    public void KeyChord_ToStringIsReadable()
    {
        Assert.Equal("S", new KeyChord(Key.S).ToString());
        Assert.Equal("Ctrl+S", new KeyChord(Key.S, KeyModifiers.Control).ToString());
        Assert.Equal("Ctrl+Shift+S", new KeyChord(Key.S, KeyModifiers.Control | KeyModifiers.Shift).ToString());
        Assert.Equal("F5", new KeyChord(Key.F5).ToString());
    }
}
