using System;

namespace Crowbar.UI;

/// <summary>
/// Static clipboard used by text inputs for Ctrl+C / Ctrl+V. Reads and writes go
/// through an optional <see cref="Provider"/> so a platform host can bridge to
/// the real system clipboard (e.g. SDL); when none is set, the API keeps an
/// in-process buffer so the copy/paste flow still works headed internally and
/// in headless tests.
/// </summary>
public static class Clipboard
{
    /// <summary>Text copied in this process, used when <see cref="Provider"/> is null.</summary>
    private static string _fallback = string.Empty;

    /// <summary>
    /// Optional platform bridge: <see cref="Read"/> / <see cref="Write"/> are
    /// redirected here when set. A host sets it once at startup.
    /// </summary>
    public static Func<string>? ReadProvider { get; set; }

    public static Action<string>? WriteProvider { get; set; }

    /// <summary>Reads the current clipboard text (empty string when none).</summary>
    public static string Read() =>
        ReadProvider?.Invoke() ?? _fallback;

    /// <summary>Writes the given text to the clipboard.</summary>
    public static void Write(string text)
    {
        text ??= string.Empty;
        if (WriteProvider is not null)
        {
            WriteProvider(text);
        }
        else
        {
            _fallback = text;
        }
    }
}