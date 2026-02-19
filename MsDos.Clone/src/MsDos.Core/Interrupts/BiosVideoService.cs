using MsDos.Core.Cpu;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Interrupts;

/// <summary>
/// BIOS INT 10h video services. Manages text mode display through
/// the IGraphicsRenderer abstraction.
/// </summary>
public sealed class BiosVideoService
{
    private readonly Cpu8086 _cpu;
    private readonly MemoryBus _mem;
    private readonly IGraphicsRenderer _renderer;

    // Text mode state
    private int _cursorCol;
    private int _cursorRow;
    private int _textCols = 80;
    private int _textRows = 25;
    private byte _currentPage;
    private byte _currentMode = 0x03;

    // Video memory base for text mode (B800:0000)
    public const ushort VideoSegment = 0xB800;

    public BiosVideoService(Cpu8086 cpu, MemoryBus mem, IGraphicsRenderer renderer)
    {
        _cpu = cpu;
        _mem = mem;
        _renderer = renderer;
    }

    public void Handle()
    {
        byte func = _cpu.Regs.AH;
        switch (func)
        {
            case 0x00: // Set video mode
                SetVideoMode(_cpu.Regs.AL);
                break;

            case 0x01: // Set cursor shape (simplified - just show/hide)
                _renderer.SetCursorVisible((_cpu.Regs.CH & 0x20) == 0);
                break;

            case 0x02: // Set cursor position
                _cursorRow = _cpu.Regs.DH;
                _cursorCol = _cpu.Regs.DL;
                _currentPage = _cpu.Regs.BH;
                _renderer.SetCursorPosition(_cursorCol, _cursorRow);
                break;

            case 0x03: // Get cursor position
                _cpu.Regs.DH = (byte)_cursorRow;
                _cpu.Regs.DL = (byte)_cursorCol;
                _cpu.Regs.CH = 0x06; // cursor start scanline
                _cpu.Regs.CL = 0x07; // cursor end scanline
                break;

            case 0x05: // Set active display page
                _currentPage = _cpu.Regs.AL;
                break;

            case 0x06: // Scroll up
                ScrollUp(_cpu.Regs.AL);
                break;

            case 0x07: // Scroll down
                // Simplified: treat as scroll up
                ScrollUp(_cpu.Regs.AL);
                break;

            case 0x08: // Read character and attribute at cursor
                ReadCharAtCursor();
                break;

            case 0x09: // Write character and attribute at cursor
                WriteCharAtCursor(_cpu.Regs.AL, _cpu.Regs.BL, _cpu.Regs.CX, false);
                break;

            case 0x0A: // Write character at cursor (use existing attribute)
                WriteCharAtCursor(_cpu.Regs.AL, 0x07, _cpu.Regs.CX, true);
                break;

            case 0x0E: // TTY output
                TtyOutput((char)_cpu.Regs.AL);
                break;

            case 0x0F: // Get video mode
                _cpu.Regs.AL = _currentMode;
                _cpu.Regs.AH = (byte)_textCols;
                _cpu.Regs.BH = _currentPage;
                break;

            case 0x10: // Set palette (simplified)
                break;

            case 0x11: // Character generator (simplified)
                break;

            case 0x12: // Video subsystem configuration
                _cpu.Regs.BL = 0x03; // 256K EGA memory
                break;

            case 0x13: // Write string
                WriteString();
                break;
        }
    }

    private void SetVideoMode(byte mode)
    {
        _currentMode = mode;
        switch (mode)
        {
            case 0x03:
                _textCols = 80;
                _textRows = 25;
                _renderer.SetMode(VideoMode.Text80x25);
                break;
            case 0x04:
                _renderer.SetMode(VideoMode.Graphics320x200_4);
                break;
            case 0x06:
                _renderer.SetMode(VideoMode.Graphics640x200_2);
                break;
            case 0x13:
                _renderer.SetMode(VideoMode.Graphics320x200_256);
                break;
        }
        _renderer.Clear(0);
        _cursorCol = 0;
        _cursorRow = 0;
    }

    private void ScrollUp(byte lines)
    {
        if (lines == 0)
        {
            _renderer.Clear((byte)(_cpu.Regs.BH & 0x0F));
            return;
        }
        _renderer.ScrollUp(lines, (byte)((_cpu.Regs.BH >> 4) & 0x0F));
    }

    private void ReadCharAtCursor()
    {
        // Read from video memory
        uint addr = Registers.PhysicalAddress(VideoSegment, (ushort)((_cursorRow * _textCols + _cursorCol) * 2));
        _cpu.Regs.AL = _mem.ReadByte(addr);     // character
        _cpu.Regs.AH = _mem.ReadByte(addr + 1); // attribute
    }

    private void WriteCharAtCursor(byte ch, byte attr, ushort count, bool keepAttr)
    {
        for (int i = 0; i < count; i++)
        {
            int col = _cursorCol + i;
            int row = _cursorRow;
            while (col >= _textCols) { col -= _textCols; row++; }
            if (row >= _textRows) break;

            if (keepAttr)
            {
                uint rAddr = Registers.PhysicalAddress(VideoSegment, (ushort)((row * _textCols + col) * 2 + 1));
                attr = _mem.ReadByte(rAddr);
            }

            uint addr = Registers.PhysicalAddress(VideoSegment, (ushort)((row * _textCols + col) * 2));
            _mem.WriteByte(addr, ch);
            _mem.WriteByte(addr + 1, attr);
            _renderer.DrawCharacter(col, row, (char)ch, (byte)(attr & 0x0F), (byte)((attr >> 4) & 0x0F));
        }
    }

    /// <summary>TTY-style character output with cursor advance and scrolling.</summary>
    public void TtyOutput(char ch)
    {
        switch (ch)
        {
            case '\r':
                _cursorCol = 0;
                break;
            case '\n':
                _cursorRow++;
                break;
            case '\b':
                if (_cursorCol > 0) _cursorCol--;
                break;
            case '\a':
                // Bell - ignore
                break;
            case '\t':
                _cursorCol = ((_cursorCol / 8) + 1) * 8;
                if (_cursorCol >= _textCols)
                {
                    _cursorCol = 0;
                    _cursorRow++;
                }
                break;
            default:
            {
                byte attr = 0x07; // default: white on black
                uint addr = Registers.PhysicalAddress(VideoSegment, (ushort)((_cursorRow * _textCols + _cursorCol) * 2));
                _mem.WriteByte(addr, (byte)ch);
                _mem.WriteByte(addr + 1, attr);
                _renderer.DrawCharacter(_cursorCol, _cursorRow, ch, 7, 0);
                _cursorCol++;
                if (_cursorCol >= _textCols)
                {
                    _cursorCol = 0;
                    _cursorRow++;
                }
                break;
            }
        }

        // Handle scrolling
        while (_cursorRow >= _textRows)
        {
            _renderer.ScrollUp(1, 0);
            _cursorRow--;
        }
        _renderer.SetCursorPosition(_cursorCol, _cursorRow);
    }

    private void WriteString()
    {
        byte mode = _cpu.Regs.AL;
        ushort count = _cpu.Regs.CX;
        _cursorRow = _cpu.Regs.DH;
        _cursorCol = _cpu.Regs.DL;
        ushort seg = _cpu.Regs.ES;
        ushort off = _cpu.Regs.BP;

        for (int i = 0; i < count; i++)
        {
            byte ch = _mem.ReadByte(seg, (ushort)(off + i));
            if (mode >= 2)
            {
                byte attr = _mem.ReadByte(seg, (ushort)(off + i + 1));
                i++; // skip attribute byte
                WriteCharAtCursor(ch, attr, 1, false);
            }
            else
            {
                TtyOutput((char)ch);
            }
        }
    }
}
