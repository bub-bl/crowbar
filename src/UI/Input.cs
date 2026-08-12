namespace Crowbar.UI;

public sealed partial class UiSystem
{
    private Panel? _hovered;
    private Panel? _captured;
    // A drag handler can request movement/release dispatch through an ancestor
    // even after the pointer leaves the original panel. This is important for
    // dock tabs: each render replaces the hit-test tree while the drag is live.
    private Panel? _pointerCapture;
    private Panel? _scrollDragPanel;
    private bool _scrollDragVertical;
    private Panel? _lastClickPanel;
    private long _lastClickTime;
    private float _lastClickX;
    private float _lastClickY;
    public Panel? FocusedPanel { get; private set; }
    public event Action<Panel, UiPointerEvent>? PointerMoved;
    public event Action<Panel, UiPointerEvent>? PointerDown;
    public event Action<Panel, UiPointerEvent>? PointerUp;
    public event Action<Panel, KeyEvent>? KeyChanged;
    public event Action<Panel, float, float>? PointerWheelChanged;

    public Panel? ProcessPointerMove(float x, float y)
    {
        if (_scrollDragPanel is not null)
            ApplyScrollDrag(_scrollDragPanel, x, y, _scrollDragVertical);
        var hit = Screen.HitTest(x / Math.Max(0.01f, Screen.Scale), y / Math.Max(0.01f, Screen.Scale));
        UpdateHoverPath(hit);
        _hovered = hit;
        // Hover tooltip: the deepest hovered panel with a `tooltip` attribute
        // (its own or inherited from an ancestor) is shown at the cursor.
        UpdateTooltip(hit, x, y);
        // Hover cursor: the deepest hovered panel with an explicit `cursor`
        // style (its own or inherited from an ancestor) is shown at the cursor.
        UpdateCursor(hit);
        if (_captured is TextInput textInput) textInput.UpdatePointerSelection(x / Math.Max(0.01f, Screen.Scale));
        if (hit is not null)
        {
            var e = new UiPointerEvent(x, y);
            PointerMoved?.Invoke(hit, e);
            if (_pointerCapture is { } captured)
                captured.RaisePointerMove(e);
            else
                for (var current = hit; current is not null; current = current.Parent) current.RaisePointerMove(e);
        }
        else if (_pointerCapture is { } capturedOutside)
        {
            capturedOutside.RaisePointerMove(new UiPointerEvent(x, y));
        }
        return hit;
    }

    public Panel? ProcessPointerDown(float x, float y, int button = 0)
    {
        var hit = ProcessPointerMove(x, y);
        if (hit is null || !hit.IsEnabled) return null;
        TryStartScrollDrag(hit, x, y, button);
        UpdateFocus(hit);
        if (hit is TextInput textInput && button == 0) textInput.BeginPointerSelection(x / Math.Max(0.01f, Screen.Scale));
        UpdatePressedPath(hit, true);
        var e = new UiPointerEvent(x, y, button);
        PointerDown?.Invoke(hit, e);
        for (var current = hit; current is not null; current = current.Parent) current.RaisePointerDown(e);
        _captured = hit is Button or TextInput ? hit : null;
        // Capture the nearest ancestor that owns both movement and release
        // handlers. The reference may become detached by a Razor re-render;
        // its delegate still points at the live component, which is exactly what
        // lets a dock drag finish after its original tab was reconciled away.
        _pointerCapture = PathToRoot(hit).FirstOrDefault(panel =>
            panel.HasPointerMoveHandler && panel.HasPointerUpHandler);
        // A click toggles the nearest checkbox/radio on the hit path (deepest
        // first), then fires on the deepest clickable panel (any panel with a
        // Clicked handler, not just buttons) and stops bubbling there.
        for (var current = hit; current is not null; current = current.Parent)
        {
            if (current is ToggleInput toggleInput) { ToggleRadioGroup(toggleInput); break; }
        }
        for (var current = hit; current is not null; current = current.Parent)
        {
            if (current.HasClickedHandler) { current.RaiseClicked(e); break; }
        }
        // Double-click: a second press close in time and space to the first
        // one fires DoubleClicked on the hit panel path.
        var now = Environment.TickCount64;
        if (_lastClickPanel is not null && ReferenceEquals(_lastClickPanel, hit) &&
            now - _lastClickTime < 400 && Math.Abs(x - _lastClickX) < 6 && Math.Abs(y - _lastClickY) < 6)
        {
            for (var current = hit; current is not null; current = current.Parent) current.RaiseDoubleClicked(e);
        }
        _lastClickPanel = hit;
        _lastClickTime = now;
        _lastClickX = x;
        _lastClickY = y;
        return hit;
    }

    public Panel? ProcessPointerUp(float x, float y, int button = 0)
    {
        var hit = _captured ?? Screen.HitTest(x / Math.Max(0.01f, Screen.Scale), y / Math.Max(0.01f, Screen.Scale));
        if (hit is not null) PointerUp?.Invoke(hit, new UiPointerEvent(x, y, button));
        if (hit is not null)
        {
            var e = new UiPointerEvent(x, y, button);
            if (_pointerCapture is { } captured)
                captured.RaisePointerUp(e);
            else
                for (var current = hit; current is not null; current = current.Parent) current.RaisePointerUp(e);
        }
        else if (_pointerCapture is { } capturedOutside)
        {
            capturedOutside.RaisePointerUp(new UiPointerEvent(x, y, button));
        }
        UpdatePressedPath(_captured ?? hit, false);
        if (_captured is TextInput textInput && button == 0) textInput.EndPointerSelection();
        _captured = null;
        _pointerCapture = null;
        _scrollDragPanel = null;
        _scrollDragVertical = false;
        return hit;
    }

    /// <summary>Starts a scrollbar drag when the press lands on a visible scrollbar of the hit container.</summary>
    private void TryStartScrollDrag(Panel hit, float x, float y, int button)
    {
        if (button != 0 || !hit.IsScrollContainer) return;
        if (ScrollBars.HitTestVertical(hit, x, y))
        {
            _scrollDragPanel = hit;
            _scrollDragVertical = true;
            ApplyScrollDrag(hit, x, y, vertical: true);
        }
        else if (ScrollBars.HitTestHorizontal(hit, x, y))
        {
            _scrollDragPanel = hit;
            _scrollDragVertical = false;
            ApplyScrollDrag(hit, x, y, vertical: false);
        }
    }

    private static void ApplyScrollDrag(Panel panel, float x, float y, bool vertical)
    {
        var offset = ScrollBars.OffsetFromPoint(panel, vertical ? y : x, vertical);
        if (vertical) panel.ScrollTo(panel.ScrollX, offset);
        else panel.ScrollTo(offset, panel.ScrollY);
    }

    private void UpdateHoverPath(Panel? hit)
    {
        var oldPath = PathToRoot(_hovered).ToHashSet();
        var newPath = PathToRoot(hit).ToHashSet();
        foreach (var panel in oldPath.Except(newPath)) panel.SetHovered(false);
        foreach (var panel in newPath.Except(oldPath)) panel.SetHovered(true);
    }

    /// <summary>
    /// Shows the hover tooltip of the hit panel (deepest hovered panel carrying
    /// a <c>tooltip</c> attribute wins), or hides it when nothing is hovered.
    /// The tooltip is drawn by the renderer on top of everything, so hiding is
    /// just a null text push.
    /// </summary>
    private void UpdateTooltip(Panel? hit, float x, float y)
    {
        string? tooltip = null;
        for (var current = hit; current is not null; current = current.Parent)
        {
            if (!string.IsNullOrEmpty(current.Tooltip))
            {
                tooltip = current.Tooltip;
                break;
            }
        }
        Renderer.SetTooltip(tooltip, x, y);
    }

    /// <summary>
    /// Resolves the cursor to show while the pointer is over
    /// <paramref name="hit"/>: the deepest hovered panel with an explicit
    /// <c>cursor</c> style wins (a child's cursor overrides its ancestors',
    /// matching CSS inheritance; <c>auto</c> means "not declared, keep
    /// looking"). Nothing hovered leaves the UI, so the platform restores the
    /// default arrow.
    /// </summary>
    private void UpdateCursor(Panel? hit)
    {
        var cursor = "auto";
        for (var current = hit; current is not null; current = current.Parent)
        {
            var value = current.ComputedStyle.Cursor;
            if (value != "auto")
            {
                cursor = value;
                break;
            }
        }
        HoveredCursor = cursor;
    }

    private void UpdatePressedPath(Panel? hit, bool pressed)
    {
        foreach (var panel in PathToRoot(hit)) panel.SetPressed(pressed);
    }

    private void UpdateFocus(Panel? panel)
    {
        if (FocusedPanel is not null && !ReferenceEquals(FocusedPanel, panel))
        {
            FocusedPanel.SetFocused(false);
            FocusedPanel.RaiseBlurred();
        }
        if (panel is not null && !ReferenceEquals(FocusedPanel, panel))
        {
            panel.SetFocused(true);
            panel.RaiseFocused();
        }
        FocusedPanel = panel;
    }

    /// <summary>
    /// Toggles a checkbox, or checks a radio and unchecks the other radios of
    /// the same group (mutual exclusion by the <c>name</c> attribute).
    /// </summary>
    private void ToggleRadioGroup(ToggleInput toggle)
    {
        if (!toggle.IsRadio) { toggle.Toggle(); return; }
        if (toggle.IsChecked) return; // A checked radio cannot be unchecked by clicking itself.
        foreach (var sibling in AllToggles(Screen).Where(other => other.IsRadio && !ReferenceEquals(other, toggle) &&
                     string.Equals(other.GroupName, toggle.GroupName, StringComparison.Ordinal)))
            sibling.SetCheckedQuiet(false);
        toggle.Toggle();
    }

    private static IEnumerable<ToggleInput> AllToggles(Panel panel)
    {
        if (panel is ToggleInput toggle) yield return toggle;
        foreach (var child in panel.ChildrenInternal)
            foreach (var nested in AllToggles(child)) yield return nested;
    }

    private const float WheelScrollStep = 40f;

    private static IEnumerable<Panel> PathToRoot(Panel? panel)
    {
        for (var current = panel; current is not null; current = current.Parent) yield return current;
    }

    public void ProcessPointerWheel(float x, float y, float deltaX, float deltaY)
    {
        var hit = Screen.HitTest(x / Math.Max(0.01f, Screen.Scale), y / Math.Max(0.01f, Screen.Scale));
        if (hit is null) return;
        var wheel = new WheelEvent(deltaX, deltaY);
        PointerWheelChanged?.Invoke(hit, deltaX, deltaY);
        // @onwheel handlers fire on the hovered path regardless of whether a
        // scrollable ancestor absorbs the wheel.
        for (var current = hit; current is not null; current = current.Parent) current.RaisePointerWheel(wheel);
        // Scroll the nearest scrollable ancestor under the cursor: vertical wheel
        // deltas prefer vertical scrolling, horizontal deltas horizontal. A delta
        // is reused on the other axis when the preferred one cannot scroll.
        for (var current = hit; current is not null; current = current.Parent)
        {
            if (deltaY != 0 && current.CanScrollVertically) { current.ScrollBy(0, -deltaY * WheelScrollStep); return; }
            if (deltaX != 0 && current.CanScrollHorizontally) { current.ScrollBy(deltaX * WheelScrollStep, 0); return; }
            if (deltaY != 0 && current.CanScrollHorizontally) { current.ScrollBy(-deltaY * WheelScrollStep, 0); return; }
            if (deltaX != 0 && current.CanScrollVertically) { current.ScrollBy(0, deltaX * WheelScrollStep); return; }
        }
    }

    public void ProcessKey(int keyCode, bool isDown, bool isRepeat = false)
    {
        ProcessKey(new KeyEvent(keyCode, isDown, isRepeat));
    }

    public void ProcessKey(KeyEvent keyEvent)
    {
        if (FocusedPanel is TextInput input) input.HandleKey(keyEvent.KeyCode, keyEvent.IsDown, keyEvent.Text);
        else if (FocusedPanel is not null) TryScrollFromKeyboard(FocusedPanel, keyEvent);
        if (FocusedPanel is not null)
        {
            if (keyEvent.IsDown) FocusedPanel.RaiseKeyDown(keyEvent);
            else FocusedPanel.RaiseKeyUp(keyEvent);
            KeyChanged?.Invoke(FocusedPanel, keyEvent);
        }
    }

    /// <summary>Scrolls the nearest scrollable ancestor of the focused panel with the arrow/page keys.</summary>
    private static void TryScrollFromKeyboard(Panel focused, KeyEvent keyEvent)
    {
        if (!keyEvent.IsDown) return;
        var scrollable = PathToRoot(focused).FirstOrDefault(p => p.CanScrollVertically || p.CanScrollHorizontally);
        if (scrollable is null) return;
        switch (keyEvent.KeyCode)
        {
            case 0x26: scrollable.ScrollBy(0, -WheelScrollStep); break; // Up
            case 0x28: scrollable.ScrollBy(0, WheelScrollStep); break; // Down
            case 0x25: scrollable.ScrollBy(-WheelScrollStep, 0); break; // Left
            case 0x27: scrollable.ScrollBy(WheelScrollStep, 0); break; // Right
            case 0x21: scrollable.ScrollBy(0, -scrollable.ClientHeight); break; // PageUp
            case 0x22: scrollable.ScrollBy(0, scrollable.ClientHeight); break; // PageDown
            case 0x24: scrollable.ScrollTo(0, 0); break; // Home
            case 0x23: scrollable.ScrollTo(scrollable.ScrollX, scrollable.MaxScrollY); break; // End
        }
    }

    private static IEnumerable<TextInput> Inputs(Panel panel)
    {
        if (panel is TextInput input) yield return input;
        foreach (var child in panel.ChildrenInternal)
            foreach (var nested in Inputs(child)) yield return nested;
    }
}
