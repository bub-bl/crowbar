namespace Crowbar.Engine.InputSystem;

/// <summary>
/// Cross-platform keyboard key. The numeric values mirror SDL2's scancodes so a
/// raw <c>SDL_Scancode</c> can be cast directly to a <see cref="Key"/> (scancodes
/// are layout-independent, unlike keycodes).
/// </summary>
public enum Key : int
{
    Unknown = 0,

    A = 4, B = 5, C = 6, D = 7, E = 8, F = 9, G = 10, H = 11, I = 12, J = 13,
    K = 14, L = 15, M = 16, N = 17, O = 18, P = 19, Q = 20, R = 21, S = 22,
    T = 23, U = 24, V = 25, W = 26, X = 27, Y = 28, Z = 29,

    D1 = 30, D2 = 31, D3 = 32, D4 = 33, D5 = 34, D6 = 35, D7 = 36, D8 = 37,
    D9 = 38, D0 = 39,

    Enter = 40,
    Escape = 41,
    Backspace = 42,
    Tab = 43,
    Space = 44,

    Minus = 45,
    Equals = 46,
    LeftBracket = 47,
    RightBracket = 48,
    Backslash = 49,
    Semicolon = 51,
    Apostrophe = 52,
    Grave = 53,
    Comma = 54,
    Period = 55,
    Slash = 56,

    CapsLock = 57,

    F1 = 58, F2 = 59, F3 = 60, F4 = 61, F5 = 62, F6 = 63, F7 = 64, F8 = 65,
    F9 = 66, F10 = 67, F11 = 68, F12 = 69, F13 = 104, F14 = 105, F15 = 106,
    F16 = 107, F17 = 108, F18 = 109, F19 = 110, F20 = 111, F21 = 112,
    F22 = 113, F23 = 114, F24 = 115,

    PrintScreen = 70,
    ScrollLock = 71,
    Pause = 72,

    Insert = 73,
    Home = 74,
    PageUp = 75,
    Delete = 76,
    End = 77,
    PageDown = 78,
    Right = 79,
    Left = 80,
    Down = 81,
    Up = 82,

    NumLock = 83,
    NumpadDivide = 84,
    NumpadMultiply = 85,
    NumpadMinus = 86,
    NumpadPlus = 87,
    NumpadEnter = 88,
    Numpad1 = 89, Numpad2 = 90, Numpad3 = 91, Numpad4 = 92, Numpad5 = 93,
    Numpad6 = 94, Numpad7 = 95, Numpad8 = 96, Numpad9 = 97, Numpad0 = 98,
    NumpadDecimal = 99,

    Application = 101,

    LeftControl = 224,
    LeftShift = 225,
    LeftAlt = 226,
    LeftSuper = 227,
    RightControl = 228,
    RightShift = 229,
    RightAlt = 230,
    RightSuper = 231,

    /// <summary>Number of defined keys; also the size of key-state arrays.</summary>
    KeyCount = 232
}
