namespace Crowbar.UI;

public readonly record struct UiPointerEvent(float X, float Y, int Button = 0);

public class Label : Panel
{
    public Label(string text = "") { TagName = "text"; Text = text; }
}

public class Button : Panel
{
    public Button(string text = "")
    {
        TagName = "button";
        AddChild(new Label(text));
    }
}

public class TextInput : Panel
{
    public TextInput() { TagName = "input"; }
    public string Value { get; private set; } = string.Empty;
    /// <summary>Hint rendered while the input has no value.</summary>
    public string Placeholder { get; internal set; } = string.Empty;
    /// <summary>
    /// The value attribute the parser last applied to this input. The typed
    /// value wins only while this stays unchanged, so a parent changing the
    /// declared value (e.g. a new inspector selection) replaces the stale text.
    /// </summary>
    internal string LastDeclaredValue { get; set; } = string.Empty;
    public int CaretIndex { get; private set; }
    public int SelectionStart { get; private set; }
    public int SelectionEnd { get; private set; }
    public bool HasSelection => SelectionStart != SelectionEnd;
    /// <summary>Whether the caret is currently shown (blinks while focused). Read by the renderer.</summary>
    public bool CaretVisible { get; private set; } = true;
    private float _caretTime;
    private bool _shiftDown;
    private bool _controlDown;
    private bool _draggingSelection;
    public event Action<string>? ValueChanged;
    public void SetValue(string value) => SetValue(value, value.Length);
    internal void SetValue(string value, int caretIndex)
    {
        var nextCaret = Math.Clamp(caretIndex, 0, value.Length);
        if (Value == value && CaretIndex == nextCaret) return;
        Value = value;
        CaretIndex = nextCaret;
        SelectionStart = SelectionEnd = nextCaret;
        ValueChanged?.Invoke(value);
        Invalidate();
    }
    /// <summary>Sets the value without firing <see cref="ValueChanged"/> (state restoration, not a user edit).</summary>
    internal void SetValueQuiet(string value, int caretIndex)
    {
        var nextCaret = Math.Clamp(caretIndex, 0, value.Length);
        if (Value == value && CaretIndex == nextCaret) return;
        Value = value;
        CaretIndex = nextCaret;
        SelectionStart = SelectionEnd = nextCaret;
        Invalidate();
    }
    internal void FocusAtEnd() { CaretIndex = Value.Length; CaretVisible = true; _caretTime = 0; InvalidatePaint(); }
    internal void CopyInteractionStateFrom(TextInput previous)
    {
        CaretIndex = Math.Clamp(previous.CaretIndex, 0, Value.Length);
        SelectionStart = Math.Clamp(previous.SelectionStart, 0, Value.Length);
        SelectionEnd = Math.Clamp(previous.SelectionEnd, 0, Value.Length);
        CaretVisible = previous.CaretVisible;
        _caretTime = 0;
        SetFocused(previous.IsFocused);
    }
    internal void AdvanceCaret(float deltaTime)
    {
        if (!IsFocused) { CaretVisible = false; _caretTime = 0; return; }
        _caretTime += Math.Max(0, deltaTime);
        // Blinking only repaints the caret; the text box geometry is unchanged.
        if (_caretTime >= 0.5f) { _caretTime = 0; CaretVisible = !CaretVisible; InvalidatePaint(); }
    }
    internal void HandleKey(int keyCode, bool isDown, string? text = null)
    {
        if (keyCode is 0x10 or 0xA0 or 0xA1)
        {
            _shiftDown = isDown;
            return;
        }
        if (keyCode is 0x11 or 0xA2 or 0xA3)
        {
            _controlDown = isDown;
            return;
        }
        if (!isDown) return;
        if (_controlDown && keyCode == 0x41) SelectAll();
        else if (keyCode == 0x25) MoveCaret(_controlDown ? PreviousWord(CaretIndex) : Math.Max(0, CaretIndex - 1));
        else if (keyCode == 0x27) MoveCaret(_controlDown ? NextWord(CaretIndex) : Math.Min(Value.Length, CaretIndex + 1));
        else if (keyCode == 0x24) MoveCaret(_controlDown ? 0 : 0);
        else if (keyCode == 0x23) MoveCaret(_controlDown ? Value.Length : Value.Length);
        else if (keyCode == 0x08) DeleteBackward();
        else if (keyCode == 0x2E) DeleteForward();
        else if (!string.IsNullOrEmpty(text))
        {
            foreach (var character in text.Where(c => !char.IsControl(c))) Insert(character);
        }
        else if (keyCode == 0x20) Insert(' ');
        else if (keyCode is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            var character = keyCode is >= 0x41 and <= 0x5A
                ? (char)(keyCode + (_shiftDown ? 0 : 'a' - 'A'))
                : (_shiftDown ? " )!@#$%^&*("[keyCode - 0x2F] : (char)keyCode);
            Insert(character);
        }
        CaretVisible = true; _caretTime = 0; InvalidatePaint();
    }

    internal void BeginPointerSelection(float x)
    {
        var index = CaretFromX(x);
        CaretIndex = index;
        SelectionStart = SelectionEnd = index;
        _draggingSelection = true;
        ResetCaret();
    }

    internal void UpdatePointerSelection(float x)
    {
        if (!_draggingSelection) return;
        CaretIndex = SelectionEnd = CaretFromX(x);
        ResetCaret();
    }

    internal void EndPointerSelection() { _draggingSelection = false; InvalidatePaint(); }

    private void MoveCaret(int index)
    {
        index = Math.Clamp(index, 0, Value.Length);
        if (_shiftDown)
        {
            if (!HasSelection) SelectionStart = CaretIndex;
            CaretIndex = SelectionEnd = index;
        }
        else CaretIndex = SelectionStart = SelectionEnd = index;
        ResetCaret();
    }

    private void SelectAll() { SelectionStart = 0; SelectionEnd = CaretIndex = Value.Length; ResetCaret(); }
    private void DeleteBackward()
    {
        if (HasSelection) { ReplaceSelection(string.Empty); return; }
        var start = _controlDown ? PreviousWord(CaretIndex) : Math.Max(0, CaretIndex - 1);
        if (start != CaretIndex) Replace(start, CaretIndex - start);
    }
    private void DeleteForward()
    {
        if (HasSelection) { ReplaceSelection(string.Empty); return; }
        var end = _controlDown ? NextWord(CaretIndex) : Math.Min(Value.Length, CaretIndex + 1);
        if (end != CaretIndex) Replace(CaretIndex, end - CaretIndex);
    }
    private void Insert(char value) => ReplaceSelection(value.ToString());
    private void ReplaceSelection(string replacement)
    {
        var start = Math.Min(SelectionStart, SelectionEnd);
        var length = Math.Abs(SelectionEnd - SelectionStart);
        if (length == 0) start = CaretIndex;
        Replace(start, length, replacement);
    }
    private void Replace(int start, int length, string replacement = "")
    {
        Value = Value.Remove(start, length).Insert(start, replacement);
        CaretIndex = start + replacement.Length;
        SelectionStart = SelectionEnd = CaretIndex;
        ValueChanged?.Invoke(Value);
        // The text itself changed: the content box width may change, so this
        // needs a real layout (not just a caret repaint).
        Invalidate();
        ResetCaret();
    }
    private int PreviousWord(int index)
    {
        while (index > 0 && char.IsWhiteSpace(Value[index - 1])) index--;
        while (index > 0 && !char.IsWhiteSpace(Value[index - 1])) index--;
        return index;
    }
    private int NextWord(int index)
    {
        while (index < Value.Length && !char.IsWhiteSpace(Value[index])) index++;
        while (index < Value.Length && char.IsWhiteSpace(Value[index])) index++;
        return index;
    }
    private int CaretFromX(float x)
    {
        var contentX = Math.Max(0, x - Layout.X - LayoutPadding.Left);
        if (string.IsNullOrEmpty(Value) || contentX <= 0) return 0;

        var font = TextLayout.CreateFont(ComputedStyle);
        var tracking = ComputedStyle.LetterSpacing;
        var totalWidth = TextLayout.Measure(font, Value, tracking);
        if (contentX >= totalWidth) return Value.Length;

        float prevWidth = 0f;
        for (var i = 0; i < Value.Length; i++)
        {
            var nextWidth = TextLayout.Measure(font, Value[..(i + 1)], tracking);
            var midPoint = (prevWidth + nextWidth) / 2f;
            if (contentX < midPoint) return i;
            prevWidth = nextWidth;
        }

        return Value.Length;
    }
    private void ResetCaret() { CaretVisible = true; _caretTime = 0; InvalidatePaint(); }
}

public class Image : Panel
{
    public Image() { TagName = "image"; }
    public string? Source { get; set; }
}

/// <summary>
/// An SVG icon from the <c>Assets/Icons</c> pack (<c>&lt;icon name="..."&gt;</c>),
/// rasterized by the renderer and tinted with the computed <c>color</c> like
/// text. The name is the SVG file name without its extension.
/// </summary>
public class Icon : Panel
{
    public Icon() { TagName = "icon"; }
    public string? Name { get; set; }
}

/// <summary>
/// A checkbox or radio button: a clickable indicator drawn by the renderer
/// that toggles <see cref="Panel.IsChecked"/> (and feeds the <c>:checked</c>
/// pseudo-class). Radios sharing a <see cref="GroupName"/> behave as a group:
/// checking one unchecks the others.
/// </summary>
public class ToggleInput : Panel
{
    public ToggleInput(bool radio = false)
    {
        TagName = "input";
        IsRadio = radio;
    }

    /// <summary>True for a radio button (circle + dot), false for a checkbox (square + check).</summary>
    public bool IsRadio { get; }

    /// <summary>The <c>name</c> attribute: radios sharing it are mutually exclusive.</summary>
    public string? GroupName { get; set; }

    /// <summary>Raised when the checked state changes.</summary>
    public event Action<bool>? CheckedChanged;

    /// <summary>Sets the checked state without firing <see cref="CheckedChanged"/> (used for radio-group reset).</summary>
    internal void SetCheckedQuiet(bool value)
    {
        if (IsChecked == value) return;
        IsChecked = value;
        MarkStyleDirty();
    }

    internal void Toggle()
    {
        var next = !IsChecked;
        if (IsRadio && !next) return; // A checked radio cannot be unchecked by clicking itself.
        IsChecked = next;
        CheckedChanged?.Invoke(next);
        MarkStyleDirty();
    }
}

public static class PanelExtensions
{
    /// <summary>
    /// Depth-first hit test that mirrors how the renderer draws: children of a
    /// clipped panel (overflow hidden/scroll/auto/clip) are only hit inside the
    /// padding box, children of a scrollable panel are tested at the scrolled
    /// position, and visible scrollbars claim their zone so the container can
    /// be dragged. Children of an overflow:visible panel may be hit even when
    /// the pointer lies outside the panel itself.
    /// </summary>
    public static Panel? HitTest(this Panel panel, float x, float y)
    {
        if (!panel.IsVisible || panel.ComputedStyle.Display.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        var inside = x >= panel.Layout.X && x <= panel.Layout.Right && y >= panel.Layout.Y && y <= panel.Layout.Bottom;

        // A visible scrollbar owns its zone: report the container so the input
        // system can start a drag instead of leaking through to a child.
        if (inside && ScrollBars.HitTest(panel, x, y)) return panel;

        if (panel.ClipsContent)
        {
            if (!inside) return null;
            // Outside the padding box (border area): only the container itself.
            var border = panel.LayoutBorder;
            if (x < panel.Layout.X + border.Left || x > panel.Layout.Right - border.Right ||
                y < panel.Layout.Y + border.Top || y > panel.Layout.Bottom - border.Bottom)
                return panel;
        }

        // Children are hit-tested from the topmost painted sibling down, mirroring
        // the renderer: higher z-index paints above, and for equal z-index the
        // last document-order child wins. Children live in content coordinates, so
        // the pointer is translated by the scroll offset before recursing.
        var children = panel.Children;
        if (children.Count > 1 && children.Any(child => child.ComputedStyle.ZIndex != 0))
        {
            var ordered = children.OrderBy(child => child.ComputedStyle.ZIndex).ToList();
            for (var i = ordered.Count - 1; i >= 0; i--)
            {
                var hit = ordered[i].HitTest(x + panel.ScrollX, y + panel.ScrollY);
                if (hit is not null) return hit;
            }
        }
        else
        {
            for (var i = children.Count - 1; i >= 0; i--)
            {
                var hit = children[i].HitTest(x + panel.ScrollX, y + panel.ScrollY);
                if (hit is not null) return hit;
            }
        }
        return inside ? panel : null;
    }
}
