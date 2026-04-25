// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace MsDos.Core.Platform;

/// <summary>
/// Platform-agnostic virtual keyboard model. Defines the layout, key definitions,
/// modifier state, and preset key combinations (Ctrl+C, Ctrl+Alt+Del, etc.).
/// Frontends (Blazor, WinForms) render this model and call <see cref="PressKey"/>
/// to inject keystrokes into the emulator via <see cref="IEventRegistry"/>.
/// </summary>
public sealed class VirtualKeyboard
{
    private readonly IEventRegistry _events;

    // Modifier toggle state
    private bool _shiftLocked;
    private bool _ctrlLocked;
    private bool _altLocked;
    private bool _capsLock;
    private bool _numLock;

    /// <summary>Whether Shift is currently active (toggled or held).</summary>
    public bool IsShiftActive => _shiftLocked;

    /// <summary>Whether Ctrl is currently active.</summary>
    public bool IsCtrlActive => _ctrlLocked;

    /// <summary>Whether Alt is currently active.</summary>
    public bool IsAltActive => _altLocked;

    /// <summary>Whether Caps Lock is on.</summary>
    public bool IsCapsLock => _capsLock;

    /// <summary>Whether Num Lock is on.</summary>
    public bool IsNumLock => _numLock;

    /// <summary>Raised when modifier state changes so the UI can update button highlights.</summary>
    public event Action? ModifierStateChanged;

    /// <summary>Raised when a key is sent, for UI feedback (e.g., flash the key).</summary>
    public event Action<VirtualKey>? KeySent;

    public VirtualKeyboard(IEventRegistry events)
    {
        _events = events;
    }

    // ═══════════════════════════════════════════════════════════════
    //  MODIFIER TOGGLES
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Toggle Shift lock on/off. Sticky modifier for touch/virtual use.</summary>
    public void ToggleShift() { _shiftLocked = !_shiftLocked; ModifierStateChanged?.Invoke(); }

    /// <summary>Toggle Ctrl lock on/off.</summary>
    public void ToggleCtrl() { _ctrlLocked = !_ctrlLocked; ModifierStateChanged?.Invoke(); }

    /// <summary>Toggle Alt lock on/off.</summary>
    public void ToggleAlt() { _altLocked = !_altLocked; ModifierStateChanged?.Invoke(); }

    /// <summary>Clear all modifier locks after a key press (auto-release mode).</summary>
    public void ClearModifiers()
    {
        bool changed = _shiftLocked || _ctrlLocked || _altLocked;
        _shiftLocked = false;
        _ctrlLocked = false;
        _altLocked = false;
        if (changed) ModifierStateChanged?.Invoke();
    }

    /// <summary>
    /// When true, modifiers auto-clear after each key press (smartphone-style).
    /// When false, modifiers stay locked until toggled again.
    /// </summary>
    public bool AutoReleaseModifiers { get; set; } = true;

    // ═══════════════════════════════════════════════════════════════
    //  KEY PRESS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Press a single key, combining with current modifier state.
    /// Fires both KeyDown and KeyUp events on the event registry.
    /// </summary>
    public void PressKey(VirtualKey key)
    {
        // Handle modifier-only keys
        if (key.IsModifier)
        {
            switch (key.ScanCode)
            {
                case 0x2A: // Left Shift
                case 0x36: // Right Shift
                    ToggleShift();
                    break;
                case 0x1D: // Ctrl
                    ToggleCtrl();
                    break;
                case 0x38: // Alt
                    ToggleAlt();
                    break;
                case 0x3A: // Caps Lock
                    _capsLock = !_capsLock;
                    SendKeyDownUp(key.ScanCode, 0, false, false, false);
                    ModifierStateChanged?.Invoke();
                    break;
                case 0x45: // Num Lock
                    _numLock = !_numLock;
                    SendKeyDownUp(key.ScanCode, 0, false, false, false);
                    ModifierStateChanged?.Invoke();
                    break;
            }
            KeySent?.Invoke(key);
            return;
        }

        // Determine effective modifiers
        bool shift = _shiftLocked || key.ForceShift;
        bool ctrl = _ctrlLocked || key.ForceCtrl;
        bool alt = _altLocked || key.ForceAlt;

        // Determine ASCII
        byte ascii = key.AsciiChar;
        if (shift && key.ShiftedAsciiChar != 0)
            ascii = key.ShiftedAsciiChar;
        if (ctrl && key.ScanCode >= 0x1E && key.ScanCode <= 0x32)
            ascii = (byte)(key.AsciiChar - 0x60); // Ctrl+A = 1, Ctrl+Z = 26

        SendKeyDownUp(key.ScanCode, ascii, shift, ctrl, alt);
        KeySent?.Invoke(key);

        if (AutoReleaseModifiers)
            ClearModifiers();
    }

    /// <summary>
    /// Send a preset key combination (e.g., Ctrl+C, Ctrl+Alt+Del).
    /// </summary>
    public void SendCombination(KeyCombination combo)
    {
        var def = combo.ToKeyDef();
        SendKeyDownUp(def.ScanCode, def.AsciiChar, def.Shift, def.Ctrl, def.Alt);
    }

    private void SendKeyDownUp(byte scanCode, byte ascii, bool shift, bool ctrl, bool alt)
    {
        var args = new DosKeyEventArgs
        {
            ScanCode = scanCode,
            AsciiChar = ascii,
            Shift = shift,
            Ctrl = ctrl,
            Alt = alt,
        };
        _events.RaiseKeyDown(args);
        // Small delay between down and up isn't needed — the BDA buffer handler is synchronous
        _events.RaiseKeyUp(args);
    }

    // ═══════════════════════════════════════════════════════════════
    //  LAYOUT DEFINITIONS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Get the standard keyboard layout rows for rendering.</summary>
    public static IReadOnlyList<IReadOnlyList<VirtualKey>> GetLayout() => _layout;

    /// <summary>Get the preset key combinations for the quick-combo bar.</summary>
    public static IReadOnlyList<KeyCombination> GetCombinations() => _combinations;

    // ── Standard US QWERTY layout ──
    private static readonly IReadOnlyList<IReadOnlyList<VirtualKey>> _layout = new[]
    {
        // Row 0: Function keys
        new VirtualKey[]
        {
            K(0x01, 0x1B, "Esc"),
            K(0x3B, 0, "F1"), K(0x3C, 0, "F2"), K(0x3D, 0, "F3"), K(0x3E, 0, "F4"),
            K(0x3F, 0, "F5"), K(0x40, 0, "F6"), K(0x41, 0, "F7"), K(0x42, 0, "F8"),
            K(0x43, 0, "F9"), K(0x44, 0, "F10"), K(0x57, 0, "F11"), K(0x58, 0, "F12"),
        },
        // Row 1: Number row
        new VirtualKey[]
        {
            KS(0x29, (byte)'`', (byte)'~', "`", "~"),
            KS(0x02, (byte)'1', (byte)'!', "1", "!"),
            KS(0x03, (byte)'2', (byte)'@', "2", "@"),
            KS(0x04, (byte)'3', (byte)'#', "3", "#"),
            KS(0x05, (byte)'4', (byte)'$', "4", "$"),
            KS(0x06, (byte)'5', (byte)'%', "5", "%"),
            KS(0x07, (byte)'6', (byte)'^', "6", "^"),
            KS(0x08, (byte)'7', (byte)'&', "7", "&"),
            KS(0x09, (byte)'8', (byte)'*', "8", "*"),
            KS(0x0A, (byte)'9', (byte)'(', "9", "("),
            KS(0x0B, (byte)'0', (byte)')', "0", ")"),
            KS(0x0C, (byte)'-', (byte)'_', "-", "_"),
            KS(0x0D, (byte)'=', (byte)'+', "=", "+"),
            K(0x0E, 0x08, "Bksp", 2.0),
        },
        // Row 2: QWERTY top row
        new VirtualKey[]
        {
            K(0x0F, 0x09, "Tab", 1.5),
            KL(0x10, 'q'), KL(0x11, 'w'), KL(0x12, 'e'), KL(0x13, 'r'),
            KL(0x14, 't'), KL(0x15, 'y'), KL(0x16, 'u'), KL(0x17, 'i'),
            KL(0x18, 'o'), KL(0x19, 'p'),
            KS(0x1A, (byte)'[', (byte)'{', "[", "{"),
            KS(0x1B, (byte)']', (byte)'}', "]", "}"),
            KS(0x2B, (byte)'\\', (byte)'|', "\\", "|"),
        },
        // Row 3: Home row
        new VirtualKey[]
        {
            Mod(0x3A, "Caps"),
            KL(0x1E, 'a'), KL(0x1F, 's'), KL(0x20, 'd'), KL(0x21, 'f'),
            KL(0x22, 'g'), KL(0x23, 'h'), KL(0x24, 'j'), KL(0x25, 'k'),
            KL(0x26, 'l'),
            KS(0x27, (byte)';', (byte)':', ";", ":"),
            KS(0x28, (byte)'\'', (byte)'"', "'", "\""),
            K(0x1C, 0x0D, "Enter", 2.25),
        },
        // Row 4: Bottom row
        new VirtualKey[]
        {
            Mod(0x2A, "Shift", 2.25),
            KL(0x2C, 'z'), KL(0x2D, 'x'), KL(0x2E, 'c'), KL(0x2F, 'v'),
            KL(0x30, 'b'), KL(0x31, 'n'), KL(0x32, 'm'),
            KS(0x33, (byte)',', (byte)'<', ",", "<"),
            KS(0x34, (byte)'.', (byte)'>', ".", ">"),
            KS(0x35, (byte)'/', (byte)'?', "/", "?"),
            Mod(0x36, "Shift", 2.75),
        },
        // Row 5: Space bar row
        new VirtualKey[]
        {
            Mod(0x1D, "Ctrl", 1.5),
            Mod(0x38, "Alt", 1.5),
            K(0x39, 0x20, "Space", 6.0),
            Mod(0x38, "Alt", 1.5),
            Mod(0x1D, "Ctrl", 1.5),
        },
        // Row 6: Arrow keys + special
        new VirtualKey[]
        {
            K(0x52, 0, "Ins"), K(0x53, 0, "Del"),
            K(0x47, 0, "Home"), K(0x4F, 0, "End"),
            K(0x49, 0, "PgUp"), K(0x51, 0, "PgDn"),
            K(0x48, 0, "↑"), K(0x4B, 0, "←"), K(0x50, 0, "↓"), K(0x4D, 0, "→"),
        },
    };

    // ── Preset key combinations ──
    private static readonly IReadOnlyList<KeyCombination> _combinations = new[]
    {
        new KeyCombination("Ctrl+C", 0x2E, (byte)'c', false, true, false, "Break/Interrupt"),
        new KeyCombination("Ctrl+Z", 0x2C, (byte)'z', false, true, false, "EOF marker"),
        new KeyCombination("Ctrl+S", 0x1F, (byte)'s', false, true, false, "Pause output"),
        new KeyCombination("Ctrl+Q", 0x10, (byte)'q', false, true, false, "Resume output"),
        new KeyCombination("Ctrl+P", 0x19, (byte)'p', false, true, false, "Print screen toggle"),
        new KeyCombination("Ctrl+Break", 0x46, 0, false, true, false, "Break program"),
        new KeyCombination("Ctrl+Alt+Del", 0x53, 0, false, true, true, "Reboot system"),
        new KeyCombination("Alt+F4", 0x3E, 0, false, false, true, "Close program"),
        new KeyCombination("Esc", 0x01, 0x1B, false, false, false, "Cancel/Escape"),
        new KeyCombination("Pause", 0x45, 0, false, false, false, "Pause"),
        new KeyCombination("PrtSc", 0x37, 0, false, false, false, "Print Screen"),
    };

    // ── Factory helpers ──
    private static VirtualKey K(byte scan, byte ascii, string label, double width = 1.0) =>
        new(scan, ascii, 0, label, label, width, false, false, false, false);

    private static VirtualKey KL(byte scan, char ch) =>
        new(scan, (byte)ch, (byte)char.ToUpper(ch),
            ch.ToString(), char.ToUpper(ch).ToString(), 1.0, false, false, false, false);

    private static VirtualKey KS(byte scan, byte ascii, byte shiftAscii, string label, string shiftLabel) =>
        new(scan, ascii, shiftAscii, label, shiftLabel, 1.0, false, false, false, false);

    private static VirtualKey Mod(byte scan, string label, double width = 1.0) =>
        new(scan, 0, 0, label, label, width, true, false, false, false);
}

/// <summary>
/// Represents a single key on the virtual keyboard.
/// </summary>
/// <param name="ScanCode">IBM PC scan code.</param>
/// <param name="AsciiChar">Normal ASCII character (0 for extended/modifier keys).</param>
/// <param name="ShiftedAsciiChar">ASCII when Shift is held (0 if same or N/A).</param>
/// <param name="Label">Display label for the key (normal state).</param>
/// <param name="ShiftLabel">Display label when Shift is active.</param>
/// <param name="WidthMultiplier">Key width as multiple of standard key (1.0 = normal).</param>
/// <param name="IsModifier">Whether this is a modifier key (Shift/Ctrl/Alt/Caps/Num Lock).</param>
/// <param name="ForceShift">Key always sends Shift flag (for preset combos).</param>
/// <param name="ForceCtrl">Key always sends Ctrl flag.</param>
/// <param name="ForceAlt">Key always sends Alt flag.</param>
public readonly record struct VirtualKey(
    byte ScanCode,
    byte AsciiChar,
    byte ShiftedAsciiChar,
    string Label,
    string ShiftLabel,
    double WidthMultiplier,
    bool IsModifier,
    bool ForceShift,
    bool ForceCtrl,
    bool ForceAlt);

/// <summary>
/// A preset key combination (e.g., Ctrl+C, Ctrl+Alt+Del).
/// </summary>
/// <param name="DisplayName">Label shown on the combo button.</param>
/// <param name="ScanCode">Main key scan code.</param>
/// <param name="AsciiChar">ASCII char for the key.</param>
/// <param name="Shift">Whether Shift is part of the combo.</param>
/// <param name="Ctrl">Whether Ctrl is part of the combo.</param>
/// <param name="Alt">Whether Alt is part of the combo.</param>
/// <param name="Tooltip">Description of what the combo does.</param>
public readonly record struct KeyCombination(
    string DisplayName,
    byte ScanCode,
    byte AsciiChar,
    bool Shift,
    bool Ctrl,
    bool Alt,
    string Tooltip)
{
    /// <summary>Convert to a DosKeyEventArgs-compatible tuple.</summary>
    public (byte ScanCode, byte AsciiChar, bool Shift, bool Ctrl, bool Alt) ToKeyDef() =>
        (ScanCode, AsciiChar, Shift, Ctrl, Alt);
}
