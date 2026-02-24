using MsDos.Core.Cpu;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Interrupts;

/// <summary>
    /// BIOS INT 10h video services. Manages text and graphics mode display through
    /// the IGraphicsRenderer abstraction. Supports CGA character rendering in
    /// graphics modes using the 8×8 CGA font.
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

        // VGA DAC palette (256 entries × 3 bytes RGB)
        private readonly byte[] _dacPalette = new byte[256 * 3];

        /// <summary>Whether the current mode is a graphics mode.</summary>
        public bool IsGraphicsMode => _currentMode >= 0x04 && _currentMode != 0x07;

        /// <summary>Width in pixels for the current graphics mode.</summary>
        private int GraphicsWidth => _currentMode switch
        {
            0x04 or 0x05 => 320,
            0x06 => 640,
            0x0D => 320,
            0x0E => 640,
            0x10 => 640,
            0x12 => 640,
            0x13 => 320,
            _ => 320
        };

        /// <summary>Height in pixels for the current graphics mode.</summary>
        private int GraphicsHeight => _currentMode switch
        {
            0x04 or 0x05 or 0x06 or 0x0D => 200,
            0x0E => 200,
            0x10 => 350,
            0x12 => 480,
            0x13 => 200,
            _ => 200
        };
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
                ScrollDown(_cpu.Regs.AL);
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

            case 0x0B: // Set color palette
                // Simplified: accept but don't change palette
                break;

            case 0x0C: // Write pixel
                WritePixel(_cpu.Regs.CX, _cpu.Regs.DX, _cpu.Regs.AL);
                break;

            case 0x0D: // Read pixel
                _cpu.Regs.AL = ReadPixel(_cpu.Regs.CX, _cpu.Regs.DX);
                break;

            case 0x0E: // TTY output
                TtyOutput((char)_cpu.Regs.AL);
                break;

            case 0x0F: // Get video mode
                _cpu.Regs.AL = _currentMode;
                _cpu.Regs.AH = (byte)_textCols;
                _cpu.Regs.BH = _currentPage;
                break;

            case 0x10: // Set/Get palette registers
                HandlePalette();
                break;

            case 0x11: // Character generator
                HandleCharacterGenerator();
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
        _currentMode = (byte)(mode & 0x7F); // Bit 7 = don't clear screen
        bool clearScreen = (mode & 0x80) == 0;

        switch (_currentMode)
        {
            case 0x00: // 40x25 B/W text
            case 0x01: // 40x25 color text
                _textCols = 40;
                _textRows = 25;
                _renderer.SetMode(VideoMode.Text80x25); // Best we can do
                break;
            case 0x02: // 80x25 B/W text
            case 0x03: // 80x25 color text
            case 0x07: // 80x25 monochrome text
                _textCols = 80;
                _textRows = 25;
                _renderer.SetMode(VideoMode.Text80x25);
                break;
            case 0x04: // 320x200 4-color CGA
            case 0x05: // 320x200 4-color CGA (no color burst)
                _renderer.SetMode(VideoMode.Graphics320x200_4);
                break;
            case 0x06: // 640x200 2-color CGA
                _renderer.SetMode(VideoMode.Graphics640x200_2);
                break;
            case 0x0D: // 320x200 16-color EGA
            case 0x0E: // 640x200 16-color EGA
            case 0x10: // 640x350 16-color EGA
            case 0x12: // 640x480 16-color VGA
                _renderer.SetMode(VideoMode.Graphics320x200_256); // Best available approximation
                break;
            case 0x13: // 320x200 256-color VGA
                _renderer.SetMode(VideoMode.Graphics320x200_256);
                break;
            default:
                _textCols = 80;
                _textRows = 25;
                _renderer.SetMode(VideoMode.Text80x25);
                break;
        }

        if (clearScreen)
            _renderer.Clear(0);

        _cursorCol = 0;
        _cursorRow = 0;

        // Update BDA
        _mem.WriteByte(0x0040, 0x0049, _currentMode);
        _mem.WriteWord(0x0040, 0x004A, (ushort)_textCols);
        _mem.WriteByte(0x0040, 0x0084, (byte)(_textRows - 1));
    }

    private void ScrollUp(byte lines)
    {
        byte top = _cpu.Regs.CH;
        byte left = _cpu.Regs.CL;
        byte bottom = _cpu.Regs.DH;
        byte right = _cpu.Regs.DL;
        byte attr = _cpu.Regs.BH;

        if (lines == 0)
        {
            // Clear the window
            if (top == 0 && left == 0 && bottom >= _textRows - 1 && right >= _textCols - 1)
            {
                _renderer.Clear((byte)(attr & 0x0F));
                return;
            }
            // Clear the specified window area
            for (int r = top; r <= bottom && r < _textRows; r++)
            {
                for (int c = left; c <= right && c < _textCols; c++)
                {
                    uint addr = Registers.PhysicalAddress(VideoSegment, (ushort)((r * _textCols + c) * 2));
                    _mem.WriteByte(addr, 0x20);
                    _mem.WriteByte(addr + 1, attr);
                    _renderer.DrawCharacter(c, r, ' ', (byte)(attr & 0x0F), (byte)((attr >> 4) & 0x0F));
                }
            }
            return;
        }

        // If full screen window, use the fast path
        if (top == 0 && left == 0 && bottom >= _textRows - 1 && right >= _textCols - 1)
        {
            _renderer.ScrollUp(lines, (byte)((attr >> 4) & 0x0F));
            return;
        }

        // Scroll within the window
        for (int r = top; r <= bottom - lines && r < _textRows; r++)
        {
            for (int c = left; c <= right && c < _textCols; c++)
            {
                uint srcAddr = Registers.PhysicalAddress(VideoSegment, (ushort)(((r + lines) * _textCols + c) * 2));
                uint dstAddr = Registers.PhysicalAddress(VideoSegment, (ushort)((r * _textCols + c) * 2));
                byte ch = _mem.ReadByte(srcAddr);
                byte at = _mem.ReadByte(srcAddr + 1);
                _mem.WriteByte(dstAddr, ch);
                _mem.WriteByte(dstAddr + 1, at);
                _renderer.DrawCharacter(c, r, (char)ch, (byte)(at & 0x0F), (byte)((at >> 4) & 0x0F));
            }
        }
        // Clear the newly exposed lines at the bottom
        for (int r = Math.Max(top, bottom - lines + 1); r <= bottom && r < _textRows; r++)
        {
            for (int c = left; c <= right && c < _textCols; c++)
            {
                uint addr = Registers.PhysicalAddress(VideoSegment, (ushort)((r * _textCols + c) * 2));
                _mem.WriteByte(addr, 0x20);
                _mem.WriteByte(addr + 1, attr);
                _renderer.DrawCharacter(c, r, ' ', (byte)(attr & 0x0F), (byte)((attr >> 4) & 0x0F));
            }
        }
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
        if (IsGraphicsMode)
        {
            // Graphics mode: blit the 8×8 font bitmap for each character
            for (int i = 0; i < count; i++)
            {
                int col = _cursorCol + i;
                int row = _cursorRow;
                while (col >= _textCols) { col -= _textCols; row++; }
                if (row >= _textRows) break;
                BlitCharGraphics(col, row, ch, attr);
            }
            return;
        }

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
                if (IsGraphicsMode)
                {
                    // Graphics mode: blit the 8×8 font bitmap
                    byte attr = _cpu.Regs.BL; // BL = foreground color in graphics mode TTY
                    if (attr == 0) attr = 0x07;
                    BlitCharGraphics(_cursorCol, _cursorRow, (byte)ch, attr);
                }
                else
                {
                    byte attr = 0x07; // default: white on black
                    uint addr = Registers.PhysicalAddress(VideoSegment, (ushort)((_cursorRow * _textCols + _cursorCol) * 2));
                    _mem.WriteByte(addr, (byte)ch);
                    _mem.WriteByte(addr + 1, attr);
                    _renderer.DrawCharacter(_cursorCol, _cursorRow, ch, 7, 0);
                }
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

    private void ScrollDown(byte lines)
    {
        byte top = _cpu.Regs.CH;
        byte left = _cpu.Regs.CL;
        byte bottom = _cpu.Regs.DH;
        byte right = _cpu.Regs.DL;
        byte attr = _cpu.Regs.BH;

        if (lines == 0)
        {
            // Clear the window (same as ScrollUp with 0)
            for (int r = top; r <= bottom && r < _textRows; r++)
            {
                for (int c = left; c <= right && c < _textCols; c++)
                {
                    uint addr = Registers.PhysicalAddress(VideoSegment, (ushort)((r * _textCols + c) * 2));
                    _mem.WriteByte(addr, 0x20);
                    _mem.WriteByte(addr + 1, attr);
                    _renderer.DrawCharacter(c, r, ' ', (byte)(attr & 0x0F), (byte)((attr >> 4) & 0x0F));
                }
            }
            return;
        }

        if (top == 0 && left == 0 && bottom >= _textRows - 1 && right >= _textCols - 1)
        {
            _renderer.ScrollDown(lines, (byte)((attr >> 4) & 0x0F));
            return;
        }

        // Scroll within window
        for (int r = bottom; r >= top + lines; r--)
        {
            for (int c = left; c <= right && c < _textCols; c++)
            {
                uint srcAddr = Registers.PhysicalAddress(VideoSegment, (ushort)(((r - lines) * _textCols + c) * 2));
                uint dstAddr = Registers.PhysicalAddress(VideoSegment, (ushort)((r * _textCols + c) * 2));
                byte ch = _mem.ReadByte(srcAddr);
                byte at = _mem.ReadByte(srcAddr + 1);
                _mem.WriteByte(dstAddr, ch);
                _mem.WriteByte(dstAddr + 1, at);
                _renderer.DrawCharacter(c, r, (char)ch, (byte)(at & 0x0F), (byte)((at >> 4) & 0x0F));
            }
        }
        // Clear the top lines
        for (int r = top; r < top + lines && r <= bottom && r < _textRows; r++)
        {
            for (int c = left; c <= right && c < _textCols; c++)
            {
                uint addr = Registers.PhysicalAddress(VideoSegment, (ushort)((r * _textCols + c) * 2));
                _mem.WriteByte(addr, 0x20);
                _mem.WriteByte(addr + 1, attr);
                _renderer.DrawCharacter(c, r, ' ', (byte)(attr & 0x0F), (byte)((attr >> 4) & 0x0F));
            }
        }
    }

    /// <summary>INT 10h AH=10h — Palette functions.</summary>
    private void HandlePalette()
    {
        switch (_cpu.Regs.AL)
        {
            case 0x00: // Set individual palette register
                // BL = palette register, BH = color value
                break;
            case 0x01: // Set overscan (border) color
                break;
            case 0x02: // Set all palette registers
            {
                // ES:DX → 17 bytes (16 palette + 1 overscan)
                break;
            }
            case 0x07: // Read individual palette register
                _cpu.Regs.BH = 0; // Return color value
                break;
            case 0x08: // Read overscan register
                _cpu.Regs.BH = 0;
                break;
            case 0x09: // Read all palette registers
                break;
            case 0x10: // Set individual DAC color register
            {
                // BX = register number, DH=R, CH=G, CL=B (6-bit values)
                int idx = _cpu.Regs.BX * 3;
                if (idx + 2 < _dacPalette.Length)
                {
                    _dacPalette[idx] = _cpu.Regs.DH;
                    _dacPalette[idx + 1] = _cpu.Regs.CH;
                    _dacPalette[idx + 2] = _cpu.Regs.CL;
                }
                break;
            }
            case 0x12: // Set block of DAC color registers
            {
                // BX = first register, CX = count, ES:DX → RGB triplets
                int first = _cpu.Regs.BX;
                int count = _cpu.Regs.CX;
                ushort seg = _cpu.Regs.ES;
                ushort off = _cpu.Regs.DX;
                for (int i = 0; i < count && (first + i) < 256; i++)
                {
                    int idx = (first + i) * 3;
                    _dacPalette[idx] = _mem.ReadByte(seg, (ushort)(off + i * 3));
                    _dacPalette[idx + 1] = _mem.ReadByte(seg, (ushort)(off + i * 3 + 1));
                    _dacPalette[idx + 2] = _mem.ReadByte(seg, (ushort)(off + i * 3 + 2));
                }
                break;
            }
            case 0x15: // Read individual DAC color register
            {
                int idx = _cpu.Regs.BX * 3;
                if (idx + 2 < _dacPalette.Length)
                {
                    _cpu.Regs.DH = _dacPalette[idx];
                    _cpu.Regs.CH = _dacPalette[idx + 1];
                    _cpu.Regs.CL = _dacPalette[idx + 2];
                }
                break;
            }
            case 0x17: // Read block of DAC color registers
            {
                int first = _cpu.Regs.BX;
                int count = _cpu.Regs.CX;
                ushort seg = _cpu.Regs.ES;
                ushort off = _cpu.Regs.DX;
                for (int i = 0; i < count && (first + i) < 256; i++)
                {
                    int idx = (first + i) * 3;
                    _mem.WriteByte(seg, (ushort)(off + i * 3), _dacPalette[idx]);
                    _mem.WriteByte(seg, (ushort)(off + i * 3 + 1), _dacPalette[idx + 1]);
                    _mem.WriteByte(seg, (ushort)(off + i * 3 + 2), _dacPalette[idx + 2]);
                }
                break;
            }
            case 0x1A: // Get/Set DAC color page state
                _cpu.Regs.BL = 0; // Paging mode
                _cpu.Regs.BH = 0; // Current page
                break;
        }
    }

    /// <summary>INT 10h AH=11h — Character generator functions.</summary>
    private void HandleCharacterGenerator()
    {
        switch (_cpu.Regs.AL)
        {
            case 0x00: // Load user-defined character set (CGA)
            case 0x01: // Load ROM 8x14 character set
            case 0x02: // Load ROM 8x8 double dot character set
            case 0x04: // Load ROM 8x16 character set
            case 0x10: // Load user-defined set (EGA)
            case 0x11: // Load ROM 8x14 (EGA)
            case 0x12: // Load ROM 8x8 (EGA)
            case 0x14: // Load ROM 8x16 (VGA)
                // Accept but ignore — we use our own rendering
                break;
            case 0x30: // Get font info
                switch (_cpu.Regs.BH)
                {
                    case 0x00: // INT 1Fh pointer (user font, upper 128 chars)
                        _cpu.Regs.ES = CgaFont8x8.RomSegment;
                        _cpu.Regs.BP = (ushort)(CgaFont8x8.RomOffset + 128 * 8);
                        break;
                    case 0x01: // INT 43h pointer (current font)
                    case 0x02: // ROM 8x14 font (return 8x8 as fallback)
                    case 0x03: // ROM 8x8 double dot font
                    case 0x04: // ROM 8x8 second half
                    case 0x05: // ROM 9x14 alternate
                    case 0x06: // ROM 8x16 font
                    case 0x07: // ROM 9x16 alternate
                    default:
                        _cpu.Regs.ES = CgaFont8x8.RomSegment;
                        _cpu.Regs.BP = CgaFont8x8.RomOffset;
                        break;
                }
                _cpu.Regs.CX = 8; // Bytes per character (8x8 font)
                _cpu.Regs.DL = (byte)(_textRows - 1);
                break;
        }
    }

    /// <summary>
    /// Blit an 8×8 character from the CGA font to video memory in graphics mode.
    /// </summary>
    private void BlitCharGraphics(int col, int row, byte ch, byte color)
    {
        int fontOffset = ch * 8;
        int px = col * 8;
        int py = row * 8;

        for (int y = 0; y < 8; y++)
        {
            byte fontRow = fontOffset + y < CgaFont8x8.FontData.Length
                ? CgaFont8x8.FontData[fontOffset + y]
                : (byte)0;

            for (int x = 0; x < 8; x++)
            {
                bool pixel = (fontRow & (0x80 >> x)) != 0;
                int sx = px + x;
                int sy = py + y;

                if (pixel)
                    WritePixel((ushort)sx, (ushort)sy, color);
                else
                    WritePixel((ushort)sx, (ushort)sy, 0); // background
            }
        }
    }

    /// <summary>Write a pixel at (x, y) in graphics mode.</summary>
    private void WritePixel(ushort x, ushort y, byte color)
    {
        if (_currentMode == 0x13)
        {
            // Mode 13h: linear framebuffer at A000:0000
            uint addr = Registers.PhysicalAddress(0xA000, (ushort)(y * 320 + x));
            _mem.WriteByte(addr, color);
        }
        else if (_currentMode == 0x04 || _currentMode == 0x05)
        {
            // CGA 320x200 4-color: interlaced at B800:0000
            // Even rows at offset 0, odd rows at offset 0x2000
            uint baseAddr = (uint)((y & 1) != 0 ? 0xBA000 : 0xB8000);
            int rowOffset = (y / 2) * 80;
            int byteOffset = rowOffset + (x / 4);
            int bitShift = 6 - (x % 4) * 2;
            uint addr = baseAddr + (uint)byteOffset;
            byte b = _mem.ReadByte(addr);
            b &= (byte)~(0x03 << bitShift);
            b |= (byte)((color & 0x03) << bitShift);
            _mem.WriteByte(addr, b);
        }
        else if (_currentMode == 0x06)
        {
            // CGA 640x200 2-color: interlaced at B800:0000
            uint baseAddr = (uint)((y & 1) != 0 ? 0xBA000 : 0xB8000);
            int rowOffset = (y / 2) * 80;
            int byteOffset = rowOffset + (x / 8);
            int bitShift = 7 - (x % 8);
            uint addr = baseAddr + (uint)byteOffset;
            byte b = _mem.ReadByte(addr);
            b &= (byte)~(1 << bitShift);
            b |= (byte)((color & 1) << bitShift);
            _mem.WriteByte(addr, b);
        }
        _renderer.DrawPixel(x, y, color);
    }

    /// <summary>Read a pixel at (x, y) in graphics mode.</summary>
    private byte ReadPixel(ushort x, ushort y)
    {
        if (_currentMode == 0x13)
        {
            uint addr = Registers.PhysicalAddress(0xA000, (ushort)(y * 320 + x));
            return _mem.ReadByte(addr);
        }
        else if (_currentMode == 0x04 || _currentMode == 0x05)
        {
            uint baseAddr = (uint)((y & 1) != 0 ? 0xBA000 : 0xB8000);
            int rowOffset = (y / 2) * 80;
            int byteOffset = rowOffset + (x / 4);
            int bitShift = 6 - (x % 4) * 2;
            uint addr = baseAddr + (uint)byteOffset;
            return (byte)((_mem.ReadByte(addr) >> bitShift) & 0x03);
        }
        else if (_currentMode == 0x06)
        {
            uint baseAddr = (uint)((y & 1) != 0 ? 0xBA000 : 0xB8000);
            int rowOffset = (y / 2) * 80;
            int byteOffset = rowOffset + (x / 8);
            int bitShift = 7 - (x % 8);
            uint addr = baseAddr + (uint)byteOffset;
            return (byte)((_mem.ReadByte(addr) >> bitShift) & 0x01);
        }
        return 0;
    }
}
