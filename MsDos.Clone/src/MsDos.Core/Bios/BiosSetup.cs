// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MsDos.Core.Hardware;
using MsDos.Core.Platform;

namespace MsDos.Core.Bios;

/// <summary>
/// Full BIOS Setup utility (like pressing DEL at POST). Renders a text-based
/// menu UI through IGraphicsRenderer and reads/writes settings from CmosRtc.
/// Menus: Main (date/time, floppy types, memory), Boot (boot order),
/// Advanced (PIT frequency, speaker), Exit (save/discard).
/// </summary>
public sealed class BiosSetup
{
    private readonly CmosRtc _cmos;
    private readonly IGraphicsRenderer _renderer;
    private readonly IEventRegistry _events;
    private readonly EmulatorLog? _log;

    private bool _active;
    private int _currentMenu;
    private int _selectedItem;
    private readonly List<SetupMenu> _menus = new();

    // Editable settings (copied from CMOS on entry, written back on save)
    private int _year, _month, _day, _hour, _minute, _second;
    private byte _floppyA, _floppyB;
    private ushort _baseMemoryKb, _extendedMemoryKb;
    private byte[] _bootOrder = { 0x00, 0x80, 0x01 }; // Default: A:, C:, B:
    private bool _numLockOnBoot = true;
    private bool _quickBoot;

    // UI constants
    private const int ScreenCols = 80;
    private const int ScreenRows = 25;
    private const byte ColorTitle = 0x1F;         // White on Blue
    private const byte ColorNormal = 0x07;        // Light gray on Black
    private const byte ColorSelected = 0x70;      // Black on Light gray
    private const byte ColorMenuBar = 0x30;       // Black on Cyan
    private const byte ColorMenuBarSel = 0x3F;    // White on Cyan
    private const byte ColorValue = 0x0E;         // Yellow on Black
    private const byte ColorHelp = 0x1E;          // Yellow on Blue
    private const byte ColorBorder = 0x1F;        // White on Blue

    /// <summary>Whether the BIOS setup is currently active (intercepting input).</summary>
    public bool IsActive => _active;

    /// <summary>Raised when setup exits (with save=true or save=false).</summary>
    public event Action<bool>? OnExit;

    public BiosSetup(CmosRtc cmos, IGraphicsRenderer renderer, IEventRegistry events, EmulatorLog? log = null)
    {
        _cmos = cmos;
        _renderer = renderer;
        _events = events;
        _log = log;
        BuildMenus();
    }

    /// <summary>
    /// Enter the BIOS setup screen. Reads current CMOS values and displays the UI.
    /// </summary>
    public void Enter()
    {
        if (_active) return;
        _active = true;
        _currentMenu = 0;
        _selectedItem = 0;

        // Read current settings from CMOS
        LoadFromCmos();

        _renderer.SetMode(VideoMode.Text80x25);
        _renderer.Clear(0);
        DrawFullScreen();

        _log?.Info("BIOS", "Entered BIOS Setup");
    }

    /// <summary>
    /// Process a key press while setup is active.
    /// Returns true if the key was consumed.
    /// </summary>
    public bool HandleKey(DosKeyEventArgs key)
    {
        if (!_active) return false;

        switch (key.ScanCode)
        {
            case 0x4B: // Left arrow — previous menu tab
                if (_currentMenu > 0) { _currentMenu--; _selectedItem = 0; }
                break;
            case 0x4D: // Right arrow — next menu tab
                if (!key.Shift && _currentMenu < _menus.Count - 1) { _currentMenu++; _selectedItem = 0; }
                break;
            case 0x48: // Up arrow — previous item
                if (_selectedItem > 0) _selectedItem--;
                break;
            case 0x50: // Down arrow — next item
                var menu = _menus[_currentMenu];
                if (_selectedItem < menu.Items.Count - 1) _selectedItem++;
                break;
            case 0x1C: // Enter — activate item
                ActivateCurrentItem(1);
                break;
            case 0x0C: // Minus — decrement value
            case 0x4A: // Numpad minus
                ActivateCurrentItem(-1);
                break;
            case 0x0D: // Plus key (=+)
            case 0x4E: // Numpad plus
                ActivateCurrentItem(1);
                break;
            case 0x3B: // F1 — Help
                // Already shown at bottom
                break;
            case 0x44: // F10 — Save & Exit
                SaveToCmos();
                Exit(true);
                return true;
            case 0x01: // Escape — Discard & Exit
                Exit(false);
                return true;
        }

        DrawFullScreen();
        return true;
    }

    private void Exit(bool save)
    {
        _active = false;
        _renderer.Clear(0);
        _log?.Info("BIOS", save ? "Setup: settings saved to CMOS" : "Setup: changes discarded");
        OnExit?.Invoke(save);
    }

    // ═══════════════════════════════════════════════════════════════
    //  DRAWING
    // ═══════════════════════════════════════════════════════════════

    private void DrawFullScreen()
    {
        ClearScreen(0x00);

        // Title bar
        DrawCentered(0, " BIOS Setup Utility v1.0 ", ColorTitle);

        // Menu tabs
        DrawMenuBar();

        // Current menu content
        DrawMenuContent();

        // Help bar at bottom
        DrawHelpBar();

        _ = _renderer.FlushAsync();
    }

    private void DrawMenuBar()
    {
        int x = 2;
        for (int i = 0; i < _menus.Count; i++)
        {
            byte color = i == _currentMenu ? ColorMenuBarSel : ColorMenuBar;
            string label = $" {_menus[i].Title} ";
            DrawString(2, x, label, color);
            x += label.Length + 1;
        }
        // Fill rest of row 2
        for (int c = x; c < ScreenCols; c++)
            _renderer.DrawCharacter(c, 2, ' ', Fg(ColorMenuBar), Bg(ColorMenuBar));
    }

    private void DrawMenuContent()
    {
        var menu = _menus[_currentMenu];

        // Draw border box from row 4 to row 20
        DrawBox(1, 4, 78, 16, ColorBorder);

        // Menu title inside box
        DrawCentered(5, $"[ {menu.Title} ]", ColorBorder);

        // Items
        for (int i = 0; i < menu.Items.Count; i++)
        {
            var item = menu.Items[i];
            int row = 7 + i;
            byte labelColor = i == _selectedItem ? ColorSelected : ColorNormal;

            // Label
            string label = item.Label.PadRight(35);
            DrawString(row, 4, label, labelColor);

            // Value
            string value = item.GetValue();
            if (!string.IsNullOrEmpty(value))
            {
                DrawString(row, 40, $"[{value}]".PadRight(30), ColorValue);
            }
        }

        // Item help text on the right side
        if (_selectedItem < menu.Items.Count)
        {
            var item = menu.Items[_selectedItem];
            if (!string.IsNullOrEmpty(item.HelpText))
            {
                DrawBox(50, 7, 28, (int)Math.Min(menu.Items.Count, 10), ColorBorder);
                var helpLines = WordWrap(item.HelpText, 26);
                for (int h = 0; h < helpLines.Count && h < 8; h++)
                    DrawString(8 + h, 51, helpLines[h].PadRight(26), ColorHelp);
            }
        }
    }

    private void DrawHelpBar()
    {
        string help = " ←→ Select Menu | ↑↓ Select Item | Enter/+/- Change | F10 Save & Exit | Esc Discard ";
        DrawString(24, 0, help.PadRight(ScreenCols), ColorTitle);
    }

    private void DrawString(int row, int col, string text, byte attr)
    {
        byte fg = Fg(attr);
        byte bg = Bg(attr);
        for (int i = 0; i < text.Length && col + i < ScreenCols; i++)
            _renderer.DrawCharacter(col + i, row, text[i], fg, bg);
    }

    private void DrawCentered(int row, string text, byte attr)
    {
        int col = (ScreenCols - text.Length) / 2;
        DrawString(row, col, text, attr);
    }

    private void DrawBox(int left, int top, int width, int height, byte attr)
    {
        byte fg = Fg(attr);
        byte bg = Bg(attr);

        _renderer.DrawCharacter(left, top, '┌', fg, bg);
        _renderer.DrawCharacter(left + width - 1, top, '┐', fg, bg);
        _renderer.DrawCharacter(left, top + height - 1, '└', fg, bg);
        _renderer.DrawCharacter(left + width - 1, top + height - 1, '┘', fg, bg);

        for (int x = left + 1; x < left + width - 1; x++)
        {
            _renderer.DrawCharacter(x, top, '─', fg, bg);
            _renderer.DrawCharacter(x, top + height - 1, '─', fg, bg);
        }
        for (int y = top + 1; y < top + height - 1; y++)
        {
            _renderer.DrawCharacter(left, y, '│', fg, bg);
            _renderer.DrawCharacter(left + width - 1, y, '│', fg, bg);
        }
    }

    private void ClearScreen(byte bg)
    {
        for (int r = 0; r < ScreenRows; r++)
            for (int c = 0; c < ScreenCols; c++)
                _renderer.DrawCharacter(c, r, ' ', 0x07, bg);
    }

    private static byte Fg(byte attr) => (byte)(attr & 0x0F);
    private static byte Bg(byte attr) => (byte)((attr >> 4) & 0x0F);

    private static List<string> WordWrap(string text, int maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ');
        string current = "";
        foreach (var word in words)
        {
            if (current.Length + word.Length + 1 > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = current.Length == 0 ? word : current + " " + word;
            }
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    // ═══════════════════════════════════════════════════════════════
    //  MENU DEFINITIONS
    // ═══════════════════════════════════════════════════════════════

    private void BuildMenus()
    {
        // ── Main Menu ──
        var main = new SetupMenu("Main");
        main.Items.Add(new SetupItem("System Date", () => $"{_year:D4}-{_month:D2}-{_day:D2}",
            dir => { _day += dir; ClampDate(); },
            "Set the system date. Use +/- to change day."));
        main.Items.Add(new SetupItem("System Time", () => $"{_hour:D2}:{_minute:D2}:{_second:D2}",
            dir => { _minute += dir; if (_minute < 0) _minute = 59; if (_minute > 59) _minute = 0; },
            "Set the system time. Use +/- to change minute."));
        main.Items.Add(new SetupItem("Floppy Drive A:", () => FloppyTypeName(_floppyA),
            dir => { _floppyA = NextFloppyType(_floppyA, dir); },
            "Type of floppy disk drive installed in the A: slot."));
        main.Items.Add(new SetupItem("Floppy Drive B:", () => FloppyTypeName(_floppyB),
            dir => { _floppyB = NextFloppyType(_floppyB, dir); },
            "Type of floppy disk drive installed in the B: slot."));
        main.Items.Add(new SetupItem("Base Memory", () => $"{_baseMemoryKb} KB",
            dir => { _baseMemoryKb = (ushort)Math.Clamp(_baseMemoryKb + dir * 64, 256, 640); },
            "Amount of conventional memory (256-640 KB)."));
        main.Items.Add(new SetupItem("Extended Memory", () => $"{_extendedMemoryKb} KB",
            dir => { _extendedMemoryKb = (ushort)Math.Clamp(_extendedMemoryKb + dir * 1024, 0, 65535); },
            "Extended memory above 1 MB boundary."));
        _menus.Add(main);

        // ── Boot Menu ──
        var boot = new SetupMenu("Boot");
        boot.Items.Add(new SetupItem("1st Boot Device", () => BootDeviceName(_bootOrder[0]),
            dir => { _bootOrder[0] = NextBootDevice(_bootOrder[0], dir); },
            "First device to try when booting the system."));
        boot.Items.Add(new SetupItem("2nd Boot Device", () => BootDeviceName(_bootOrder[1]),
            dir => { _bootOrder[1] = NextBootDevice(_bootOrder[1], dir); },
            "Second device to try if first device is not bootable."));
        boot.Items.Add(new SetupItem("3rd Boot Device", () => BootDeviceName(_bootOrder[2]),
            dir => { _bootOrder[2] = NextBootDevice(_bootOrder[2], dir); },
            "Third device to try if previous devices fail."));
        boot.Items.Add(new SetupItem("Quick Boot", () => _quickBoot ? "Enabled" : "Disabled",
            dir => { _quickBoot = !_quickBoot; },
            "Skip memory test during POST for faster boot."));
        boot.Items.Add(new SetupItem("Num Lock on Boot", () => _numLockOnBoot ? "On" : "Off",
            dir => { _numLockOnBoot = !_numLockOnBoot; },
            "Num Lock state after boot."));
        _menus.Add(boot);

        // ── Advanced Menu ──
        var advanced = new SetupMenu("Advanced");
        advanced.Items.Add(new SetupItem("CPU Speed", () => "4.77 MHz (fixed)",
            _ => { }, "CPU clock speed (not adjustable in this emulator)."));
        advanced.Items.Add(new SetupItem("PC Speaker", () => "Enabled",
            _ => { }, "PC speaker is always enabled."));
        advanced.Items.Add(new SetupItem("BIOS Date", () => "01/01/86",
            _ => { }, "BIOS ROM date string (read-only)."));
        advanced.Items.Add(new SetupItem("Machine ID", () => "0xFF (IBM PC)",
            _ => { }, "Machine identification byte (read-only)."));
        _menus.Add(advanced);

        // ── Exit Menu ──
        var exit = new SetupMenu("Exit");
        exit.Items.Add(new SetupItem("Save Changes & Exit", () => "",
            _ => { SaveToCmos(); Exit(true); },
            "Write settings to CMOS RAM and continue booting."));
        exit.Items.Add(new SetupItem("Discard Changes & Exit", () => "",
            _ => { Exit(false); },
            "Discard all changes and continue booting."));
        exit.Items.Add(new SetupItem("Load Default Settings", () => "",
            _ => { LoadDefaults(); DrawFullScreen(); },
            "Reset all settings to factory defaults."));
        _menus.Add(exit);
    }

    private void ActivateCurrentItem(int direction)
    {
        var menu = _menus[_currentMenu];
        if (_selectedItem < menu.Items.Count)
            menu.Items[_selectedItem].OnChange(direction);
    }

    // ═══════════════════════════════════════════════════════════════
    //  CMOS READ / WRITE
    // ═══════════════════════════════════════════════════════════════

    private void LoadFromCmos()
    {
        var now = DateTime.Now;
        _year = now.Year;
        _month = now.Month;
        _day = now.Day;
        _hour = now.Hour;
        _minute = now.Minute;
        _second = now.Second;

        // Read floppy types from CMOS register 0x10
        byte floppyReg = _cmos.ReadRegister(0x10);
        _floppyA = (byte)((floppyReg >> 4) & 0x0F);
        _floppyB = (byte)(floppyReg & 0x0F);

        // Read memory from CMOS
        _baseMemoryKb = (ushort)(_cmos.ReadRegister(0x15) | (_cmos.ReadRegister(0x16) << 8));
        if (_baseMemoryKb == 0) _baseMemoryKb = 640;
        _extendedMemoryKb = (ushort)(_cmos.ReadRegister(0x17) | (_cmos.ReadRegister(0x18) << 8));

        // Read boot order from CMOS extension area (custom registers 0x3D-0x3F)
        byte b0 = _cmos.ReadRegister(0x3D);
        byte b1 = _cmos.ReadRegister(0x3E);
        byte b2 = _cmos.ReadRegister(0x3F);
        if (b0 != 0 || b1 != 0 || b2 != 0)
        {
            _bootOrder = new byte[] { b0, b1, b2 };
        }
        else
        {
            _bootOrder = new byte[] { 0x00, 0x80, 0x01 }; // Default: A:, C:, B:
        }

        _numLockOnBoot = (_cmos.ReadRegister(0x3C) & 0x01) != 0;
        _quickBoot = (_cmos.ReadRegister(0x3C) & 0x02) != 0;
    }

    private void SaveToCmos()
    {
        // Floppy types
        _cmos.SetFloppyTypes(_floppyA, _floppyB);

        // Memory
        _cmos.SetBaseMemory(_baseMemoryKb);
        _cmos.SetExtendedMemory(_extendedMemoryKb);

        // Boot order in custom CMOS area
        _cmos.WriteRegister(0x3D, _bootOrder[0]);
        _cmos.WriteRegister(0x3E, _bootOrder[1]);
        _cmos.WriteRegister(0x3F, _bootOrder[2]);

        // Flags
        byte flags = 0;
        if (_numLockOnBoot) flags |= 0x01;
        if (_quickBoot) flags |= 0x02;
        _cmos.WriteRegister(0x3C, flags);

        _log?.Info("BIOS", $"CMOS saved: Floppy A={FloppyTypeName(_floppyA)}, B={FloppyTypeName(_floppyB)}, " +
                           $"Boot={BootDeviceName(_bootOrder[0])},{BootDeviceName(_bootOrder[1])},{BootDeviceName(_bootOrder[2])}, " +
                           $"BaseMem={_baseMemoryKb}KB, ExtMem={_extendedMemoryKb}KB");
    }

    private void LoadDefaults()
    {
        var now = DateTime.Now;
        _year = now.Year; _month = now.Month; _day = now.Day;
        _hour = now.Hour; _minute = now.Minute; _second = now.Second;
        _floppyA = 4; _floppyB = 4; // Both 1.44M
        _baseMemoryKb = 640;
        _extendedMemoryKb = 0;
        _bootOrder = new byte[] { 0x00, 0x80, 0x01 };
        _numLockOnBoot = true;
        _quickBoot = false;
    }

    // ═══════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════

    private void ClampDate()
    {
        int maxDay = DateTime.DaysInMonth(_year, _month);
        if (_day < 1) _day = maxDay;
        if (_day > maxDay) _day = 1;
    }

    private static string FloppyTypeName(byte type) => type switch
    {
        0 => "Not Installed",
        1 => "360 KB 5.25\"",
        2 => "1.2 MB 5.25\"",
        3 => "720 KB 3.5\"",
        4 => "1.44 MB 3.5\"",
        5 => "2.88 MB 3.5\"",
        _ => $"Unknown ({type})"
    };

    private static byte NextFloppyType(byte current, int dir)
    {
        int next = current + dir;
        if (next < 0) next = 5;
        if (next > 5) next = 0;
        return (byte)next;
    }

    private static string BootDeviceName(byte dev) => dev switch
    {
        0x00 => "Floppy A:",
        0x01 => "Floppy B:",
        0x80 => "Hard Disk C:",
        0xFF => "Disabled",
        _ => $"Drive 0x{dev:X2}"
    };

    private static byte NextBootDevice(byte current, int dir)
    {
        byte[] options = { 0x00, 0x01, 0x80, 0xFF };
        int idx = Array.IndexOf(options, current);
        if (idx < 0) idx = 0;
        idx += dir;
        if (idx < 0) idx = options.Length - 1;
        if (idx >= options.Length) idx = 0;
        return options[idx];
    }

    /// <summary>
    /// Get the boot order configured in BIOS setup.
    /// Returns BIOS drive numbers (0x00=A:, 0x01=B:, 0x80=C:) in order.
    /// </summary>
    public byte[] GetBootOrder()
    {
        byte b0 = _cmos.ReadRegister(0x3D);
        byte b1 = _cmos.ReadRegister(0x3E);
        byte b2 = _cmos.ReadRegister(0x3F);
        if (b0 == 0 && b1 == 0 && b2 == 0)
            return new byte[] { 0x00, 0x80, 0x01 }; // Default
        return new byte[] { b0, b1, b2 };
    }

    // ═══════════════════════════════════════════════════════════════
    //  INNER TYPES
    // ═══════════════════════════════════════════════════════════════

    private sealed class SetupMenu
    {
        public string Title { get; }
        public List<SetupItem> Items { get; } = new();
        public SetupMenu(string title) => Title = title;
    }

    private sealed class SetupItem
    {
        public string Label { get; }
        public Func<string> GetValue { get; }
        public Action<int> OnChange { get; }
        public string HelpText { get; }

        public SetupItem(string label, Func<string> getValue, Action<int> onChange, string helpText)
        {
            Label = label;
            GetValue = getValue;
            OnChange = onChange;
            HelpText = helpText;
        }
    }
}
