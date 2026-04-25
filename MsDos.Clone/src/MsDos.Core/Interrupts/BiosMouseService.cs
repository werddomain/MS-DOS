using MsDos.Core.Cpu;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Interrupts;

/// <summary>
/// INT 33h mouse driver services. Provides mouse position, button state,
/// cursor visibility, and user callback support through the IEventRegistry abstraction.
/// </summary>
public sealed class BiosMouseService
{
    private readonly Cpu8086 _cpu;
    private readonly MemoryBus _mem;
    private readonly IEventRegistry _events;

    private bool _installed;
    private bool _cursorVisible;
    private int _mouseX;
    private int _mouseY;
    private byte _buttons;
    private int _minX, _maxX = 639;
    private int _minY, _maxY = 199;

    // User callback for mouse events (function 0x000C)
    private ushort _callbackMask;    // Event mask (bits: move, left-down, left-up, right-down, right-up, middle-down, middle-up)
    private ushort _callbackSeg;     // Callback segment
    private ushort _callbackOff;     // Callback offset
    private bool _callbackPending;   // A callback is pending invocation
    private ushort _callbackCondition; // Condition bits for pending callback

    // Motion counters (mickeys)
    private int _mickeyX;
    private int _mickeyY;

    public BiosMouseService(Cpu8086 cpu, MemoryBus mem, IEventRegistry events)
    {
        _cpu = cpu;
        _mem = mem;
        _events = events;

        // Track mouse state from events
        events.MouseMove += e =>
        {
            int dx = e.X - _mouseX;
            int dy = e.Y - _mouseY;
            _mouseX = Math.Clamp(e.X, _minX, _maxX);
            _mouseY = Math.Clamp(e.Y, _minY, _maxY);
            _mickeyX += dx;
            _mickeyY += dy;
            CheckCallback(0x01); // Bit 0: mouse movement
        };
        events.MouseDown += e =>
        {
            _buttons |= (byte)e.Button;
            // Bit 1 = left down, bit 3 = right down, bit 5 = middle down
            ushort bit = e.Button switch { DosMouseButton.Left => 0x02, DosMouseButton.Right => 0x08, DosMouseButton.Middle => 0x20, _ => 0 };
            CheckCallback(bit);
        };
        events.MouseUp += e =>
        {
            _buttons &= (byte)~(byte)e.Button;
            // Bit 2 = left up, bit 4 = right up, bit 6 = middle up
            ushort bit = e.Button switch { DosMouseButton.Left => 0x04, DosMouseButton.Right => 0x10, DosMouseButton.Middle => 0x40, _ => 0 };
            CheckCallback(bit);
        };

        _installed = true;
    }

    /// <summary>Check if a callback should fire for the given condition.</summary>
    private void CheckCallback(ushort condition)
    {
        if (_callbackMask == 0 || _callbackSeg == 0 && _callbackOff == 0) return;
        if ((_callbackMask & condition) != 0)
        {
            _callbackPending = true;
            _callbackCondition = condition;
        }
    }

    /// <summary>
    /// Call after each CPU step to dispatch pending mouse callbacks.
    /// The callback is invoked as a FAR CALL: the driver pushes the current
    /// CS:IP, sets registers, then jumps to the user routine.
    /// Returns true if a callback was dispatched.
    /// </summary>
    public bool DispatchPendingCallback()
    {
        if (!_callbackPending) return false;
        _callbackPending = false;

        // Save current state on stack (simulate CALL FAR)
        ushort sp = _cpu.Regs.SP;
        sp -= 2; _mem.WriteWord(_cpu.Regs.SS, sp, _cpu.Regs.IP);
        sp -= 2; _mem.WriteWord(_cpu.Regs.SS, sp, _cpu.Regs.CS);
        _cpu.Regs.SP = sp;

        // Set registers as the mouse driver callback convention requires
        _cpu.Regs.AX = _callbackCondition;
        _cpu.Regs.BX = _buttons;
        _cpu.Regs.CX = (ushort)_mouseX;
        _cpu.Regs.DX = (ushort)_mouseY;
        _cpu.Regs.SI = (ushort)_mickeyX;
        _cpu.Regs.DI = (ushort)_mickeyY;

        // Jump to callback
        _cpu.Regs.CS = _callbackSeg;
        _cpu.Regs.IP = _callbackOff;

        return true;
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
                _callbackMask = 0;
                _callbackSeg = 0;
                _callbackOff = 0;
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
                _cpu.Regs.CX = (ushort)_mickeyX;
                _cpu.Regs.DX = (ushort)_mickeyY;
                _mickeyX = 0;
                _mickeyY = 0;
                break;

            case 0x000C: // Set user-defined subroutine
                _callbackMask = _cpu.Regs.CX;
                _callbackSeg = _cpu.Regs.ES;
                _callbackOff = _cpu.Regs.DX;
                break;

            case 0x0014: // Swap user-defined subroutine
            {
                ushort oldMask = _callbackMask;
                ushort oldSeg = _callbackSeg;
                ushort oldOff = _callbackOff;
                _callbackMask = _cpu.Regs.CX;
                _callbackSeg = _cpu.Regs.ES;
                _callbackOff = _cpu.Regs.DX;
                _cpu.Regs.CX = oldMask;
                _cpu.Regs.ES = oldSeg;
                _cpu.Regs.DX = oldOff;
                break;
            }

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
