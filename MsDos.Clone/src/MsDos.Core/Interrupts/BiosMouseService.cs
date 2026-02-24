using MsDos.Core.Cpu;
using MsDos.Core.Platform;

namespace MsDos.Core.Interrupts;

/// <summary>
/// INT 33h mouse driver services. Provides mouse position, button state,
/// and cursor visibility through the IEventRegistry abstraction.
/// </summary>
public sealed class BiosMouseService
{
    private readonly Cpu8086 _cpu;
    private readonly IEventRegistry _events;

    private bool _installed;
    private bool _cursorVisible;
    private int _mouseX;
    private int _mouseY;
    private byte _buttons;
    private int _minX, _maxX = 639;
    private int _minY, _maxY = 199;

    public BiosMouseService(Cpu8086 cpu, IEventRegistry events)
    {
        _cpu = cpu;
        _events = events;

        // Track mouse state from events
        events.MouseMove += e => { _mouseX = e.X; _mouseY = e.Y; };
        events.MouseDown += e => _buttons |= (byte)e.Button;
        events.MouseUp += e => _buttons &= (byte)~(byte)e.Button;

        _installed = true;
    }

    public void Handle()
    {
        ushort func = _cpu.Regs.AX;

        switch (func)
        {
            case 0x0000: // Reset / detect mouse
                _cpu.Regs.AX = (ushort)(_installed ? 0xFFFF : 0);
                _cpu.Regs.BX = 3; // 3 buttons
                _mouseX = 0;
                _mouseY = 0;
                _buttons = 0;
                _cursorVisible = false;
                break;

            case 0x0001: // Show mouse cursor
                _cursorVisible = true;
                break;

            case 0x0002: // Hide mouse cursor
                _cursorVisible = false;
                break;

            case 0x0003: // Get position and button status
                _cpu.Regs.BX = _buttons;
                _cpu.Regs.CX = (ushort)_mouseX;
                _cpu.Regs.DX = (ushort)_mouseY;
                break;

            case 0x0004: // Set mouse cursor position
                _mouseX = _cpu.Regs.CX;
                _mouseY = _cpu.Regs.DX;
                break;

            case 0x0005: // Get button press info
                _cpu.Regs.AX = _buttons;
                _cpu.Regs.BX = 0; // Press count
                _cpu.Regs.CX = (ushort)_mouseX;
                _cpu.Regs.DX = (ushort)_mouseY;
                break;

            case 0x0006: // Get button release info
                _cpu.Regs.AX = _buttons;
                _cpu.Regs.BX = 0; // Release count
                _cpu.Regs.CX = (ushort)_mouseX;
                _cpu.Regs.DX = (ushort)_mouseY;
                break;

            case 0x0007: // Set horizontal limits
                _minX = _cpu.Regs.CX;
                _maxX = _cpu.Regs.DX;
                break;

            case 0x0008: // Set vertical limits
                _minY = _cpu.Regs.CX;
                _maxY = _cpu.Regs.DX;
                break;

            case 0x000B: // Read motion counters
                _cpu.Regs.CX = 0; // Horizontal mickeys
                _cpu.Regs.DX = 0; // Vertical mickeys
                break;

            case 0x000C: // Set user subroutine
                // Accept but ignore (would need callback mechanism)
                break;

            case 0x0015: // Get mouse driver info
                _cpu.Regs.BX = 8;  // Major version
                _cpu.Regs.CX = 0;  // Minor version
                break;

            case 0x001A: // Set mouse sensitivity
                break;

            case 0x0021: // Software reset
                _cpu.Regs.AX = (ushort)(_installed ? 0xFFFF : 0);
                _cpu.Regs.BX = 3;
                break;
        }
    }

    public bool IsCursorVisible => _cursorVisible;
}
