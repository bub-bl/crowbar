namespace Crowbar.UI;

public readonly record struct UiSize(float Width, float Height);
public readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
}

/// <summary>An integer rectangle in raster pixel space, used for damage tracking.</summary>
public readonly record struct UiRectInt(int X, int Y, int Width, int Height);

/// <summary>Resolved per-side values (padding, border or margin) from the layout pass.</summary>
public readonly record struct UiThickness(float Top, float Right, float Bottom, float Left);
