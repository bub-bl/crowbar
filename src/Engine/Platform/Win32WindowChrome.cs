using System.Runtime.InteropServices;
using Crowbar.UI;

namespace Crowbar.Engine.Platform;

/// <summary>
/// Windows-only window chrome. Removes the native caption (the white title bar)
/// while keeping the window styles intact — so the resize borders, drop shadow,
/// rounded corners, Aero snap and the minimize/maximize/restore animations all
/// stay native — and answers WM_NCHITTEST so the custom title bar becomes the
/// caption: draggable, with native minimize/maximize/close buttons (including
/// the Windows 11 snap-layout flyout on the maximize button, which the system
/// shows automatically when a region hit-tests as HTMAXBUTTON).
/// </summary>
internal sealed unsafe class Win32WindowChrome : IDisposable
{
    private const int GWLP_WNDPROC = -4;

    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_NCDESTROY = 0x0082;
    private const uint WM_NCMOUSELEAVE = 0x02A2;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTMINBUTTON = 8;
    private const int HTMAXBUTTON = 9;
    private const int HTTOP = 12;
    private const int HTCLOSE = 20;

    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private readonly nint _hwnd;
    private readonly nint _originalWndProc;
    private readonly WndProcDelegate _wndProc;
    private int _drawableWidth = 1;
    private int _drawableHeight = 1;
    private int _topResizeEdge;
    private WindowChromeLayout? _layout;
    private bool _restored;

    public Win32WindowChrome(nint hwnd, int drawableWidth, int drawableHeight)
    {
        _hwnd = hwnd;
        SetDrawableSize(drawableWidth, drawableHeight);
        _topResizeEdge = Math.Clamp(FrameMetric(SM_CYSIZEFRAME) + FrameMetric(SM_CXPADDEDBORDER), 4, 16);

        _wndProc = WndProc;
        _originalWndProc = GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
        if (_originalWndProc == 0)
            throw new InvalidOperationException("Failed to read the SDL window procedure.");
        SetWindowLongPtrW(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));

        ApplyDwmAttributes();
        // Force WM_NCCALCSIZE through the new procedure so the caption is
        // absorbed into the client area immediately.
        SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public WindowChromeButton HoveredButton { get; private set; }

    public void SetLayout(WindowChromeLayout? layout) => _layout = layout;

    public void SetDrawableSize(int width, int height)
    {
        _drawableWidth = Math.Max(1, width);
        _drawableHeight = Math.Max(1, height);
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_NCCALCSIZE:
                    return OnNcCalcSize(lParam);

                case WM_NCHITTEST:
                {
                    var hit = OnNcHitTest(lParam);
                    if (hit != HTCLIENT)
                        return hit;
                    break;
                }

                case WM_NCMOUSELEAVE:
                    HoveredButton = WindowChromeButton.None;
                    break;

                case WM_NCDESTROY:
                    RestoreWndProc();
                    break;
            }
        }
        catch
        {
            // A chrome failure must never break the window's message loop.
        }

        return CallWindowProcW(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Transforms the window rect into the client rect: keep the left/right/
    /// bottom resize borders in the non-client area, but absorb the caption
    /// (title bar) into the client so the custom top bar covers it. When
    /// maximized the window rect extends past the screen edge, so the top
    /// border is also subtracted to keep the client on screen.
    /// </summary>
    private nint OnNcCalcSize(nint lParam)
    {
        // wParam == FALSE: lParam is a RECT*. wParam == TRUE: lParam is an
        // NCCALCSIZE_PARAMS* whose first field is rgrc[0]. Both start with the
        // window rect we transform into the client rect.
        var rect = (RECT*)lParam;
        var border = FrameMetric(SM_CXSIZEFRAME) + FrameMetric(SM_CXPADDEDBORDER);
        rect->Left += border;
        rect->Right -= border;
        rect->Bottom -= border;
        if (IsZoomed(_hwnd))
            rect->Top += border;
        // 0: use rgrc[0] as the new client rect (the whole client is valid).
        return 0;
    }

    private nint OnNcHitTest(nint lParam)
    {
        var layout = _layout;
        if (layout is null)
        {
            HoveredButton = WindowChromeButton.None;
            return HTCLIENT;
        }

        var screenX = (short)(lParam & 0xFFFF);
        var screenY = (short)((lParam >> 16) & 0xFFFF);
        var point = new POINT(screenX, screenY);
        if (!ScreenToClient(_hwnd, ref point))
        {
            HoveredButton = WindowChromeButton.None;
            return HTCLIENT;
        }

        // Client (physical) → framebuffer/drawable coordinates, so the UI's
        // published rects match whatever DPI scaling is in effect.
        RECT client;
        GetClientRect(_hwnd, out client);
        var scaleX = (float)_drawableWidth / Math.Max(1, client.Right - client.Left);
        var scaleY = (float)_drawableHeight / Math.Max(1, client.Bottom - client.Top);
        var x = (point.X - client.Left) * scaleX;
        var y = (point.Y - client.Top) * scaleY;

        // Outside the title-bar strip: defer to the default procedure so the
        // native resize edges (left/right/bottom) keep working.
        if (y < 0 || y >= layout.TitleBarHeight)
        {
            HoveredButton = WindowChromeButton.None;
            return HTCLIENT;
        }

        // The caption buttons take precedence over everything else, so their
        // full height is native (including the top resize strip). The system
        // shows the snap-layout flyout when the cursor hovers HTMAXBUTTON.
        if (Contains(layout.CloseButton, x, y)) { HoveredButton = WindowChromeButton.Close; return HTCLOSE; }
        if (Contains(layout.MaximizeButton, x, y)) { HoveredButton = WindowChromeButton.Maximize; return HTMAXBUTTON; }
        if (Contains(layout.MinimizeButton, x, y)) { HoveredButton = WindowChromeButton.Minimize; return HTMINBUTTON; }

        // Interactive UI (tabs, tools, play, mode, user) stays clickable.
        foreach (var interactive in layout.InteractiveRects)
        {
            if (Contains(interactive, x, y))
            {
                HoveredButton = WindowChromeButton.None;
                return HTCLIENT;
            }
        }

        // Thin strip at the very top: resize (the top border is now client).
        if (!IsZoomed(_hwnd) && y < _topResizeEdge)
        {
            HoveredButton = WindowChromeButton.None;
            return HTTOP;
        }

        // The rest of the strip is the draggable caption.
        HoveredButton = WindowChromeButton.None;
        return HTCAPTION;
    }

    private static bool Contains(UiRect rect, float x, float y) =>
        x >= rect.X && x <= rect.Right && y >= rect.Y && y <= rect.Bottom;

    /// <summary>
    /// A frame metric scaled for the window's monitor DPI (so the invisible
    /// resize borders stay correct on mixed-DPI setups), falling back to the
    /// system-wide value when the DPI query is unavailable.
    /// </summary>
    private int FrameMetric(int index)
    {
        var dpi = GetDpiForWindow(_hwnd);
        return dpi > 0
            ? GetSystemMetricsForDpi(index, dpi)
            : GetSystemMetrics(index);
    }

    private void ApplyDwmAttributes()
    {
        // Dark border to match the editor's theme (the caption is gone, but the
        // resize border and rounded corners are drawn by the DWM).
        var dark = 1;
        if (DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, &dark, sizeof(int));

        // Windows 11 rounded corners (DWMWCP_ROUND); ignored on older systems.
        var corner = 2;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(int));
    }

    private void RestoreWndProc()
    {
        if (_restored)
            return;
        _restored = true;
        SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, _originalWndProc);
    }

    public void Dispose() => RestoreWndProc();

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, void* pvAttribute, int cbAttribute);
}
