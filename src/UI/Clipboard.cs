using System;

namespace Crowbar.UI;

/// <summary>
/// Static clipboard used by text inputs for Ctrl+C / Ctrl+V.
/// <see cref="Read"/> returns the current text and <see cref="Write"/> sets it.
/// By default the API keeps an in-process buffer; a platform host can wire the
/// real system clipboard (e.g. SDL) through <see cref="SetPlatformBridge"/>,
/// which stays internal so the public surface is just read and write.
/// </summary>
public static class Clipboard
{
    /// <summary>Text copied in this process, used when no platform bridge is set.</summary>
    private static string _fallback = string.Empty;

    private static Func<string>? _readProvider;
    private static Action<string>? _writeProvider;

    /// <summary>Redirects reads/writes to a platform bridge. Internal: hosts wire it once at startup.</summary>
    internal static void SetPlatformBridge(Func<string>? read, Action<string>? write)
    {
        _readProvider = read;
        _writeProvider = write;
    }

    /// <summary>Reads the current clipboard text (empty string when none).</summary>
    public static string Read() =>
        _readProvider?.Invoke() ?? _fallback;

    /// <summary>Writes the given text to the clipboard.</summary>
    public static void Write(string text)
    {
        text ??= string.Empty;
        if (_writeProvider is not null)
        {
            _writeProvider(text);
        }
        else
        {
            _fallback = text;
        }
    }
}