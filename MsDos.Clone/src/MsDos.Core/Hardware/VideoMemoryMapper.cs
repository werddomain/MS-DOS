using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Hardware;

/// <summary>
/// Video memory mapper — intercepts reads/writes to the video memory region
/// (B800:0000 for CGA text, B000:0000 for MDA) and synchronizes with the
/// graphics renderer.
///
/// In text mode:
///   B800:0000 - B800:0F9F = Page 0 (80×25×2 = 4000 bytes)
///   B800:1000 - B800:1F9F = Page 1
///   ... up to page 7
///
///   Each character cell = 2 bytes:
///     Even byte: ASCII character code
///     Odd byte:  Attribute (foreground 0-3, background 4-6, blink 7)
///
/// In CGA graphics mode:
///   B800:0000 - B800:3FFF = 16K CGA framebuffer
///   Even scanlines at B800:0000, odd scanlines at B800:2000
///
/// In VGA mode 13h (320×200×256):
///   A000:0000 - A000:F9FF = 64K linear framebuffer
/// </summary>
public sealed class VideoMemoryMapper
{
    private readonly MemoryBus _memory;
    private readonly IGraphicsRenderer _renderer;
    private readonly EmulatorLog? _log;

    // Text mode state
    private int _columns = 80;
    private int _rows = 25;
    private int _activePage;
    private VideoModeType _currentMode = VideoModeType.Text;

    // CGA text mode base address
    private const uint CgaTextBase = 0xB8000;  // B800:0000
    private const uint MdaTextBase = 0xB0000;  // B000:0000
    private const uint VgaGraphicsBase = 0xA0000; // A000:0000

    // Page size for text mode
    private int PageSize => _columns * _rows * 2;

    /// <summary>Whether to use CGA (B800) or MDA (B000) text base.</summary>
    public bool UseMda { get; set; }

    /// <summary>Base address for the current text mode.</summary>
    private uint TextBase => UseMda ? MdaTextBase : CgaTextBase;

    /// <summary>Track whether video RAM was written this frame (for dirty-region optimization).</summary>
    public bool IsDirty { get; private set; }

    public VideoMemoryMapper(MemoryBus memory, IGraphicsRenderer renderer, EmulatorLog? log = null)
    {
        _memory = memory;
        _renderer = renderer;
        _log = log;
    }

    /// <summary>
    /// Set the current video mode. Called when INT 10h AH=00h changes the mode.
    /// </summary>
    public void SetMode(int modeNumber)
    {
        switch (modeNumber)
        {
            case 0x00: case 0x01: // 40×25 text
                _currentMode = VideoModeType.Text;
                _columns = 40;
                _rows = 25;
                break;
            case 0x02: case 0x03: case 0x07: // 80×25 text
                _currentMode = VideoModeType.Text;
                _columns = 80;
                _rows = 25;
                break;
            case 0x04: case 0x05: // CGA 320×200 4-color
                _currentMode = VideoModeType.CgaGraphics;
                break;
            case 0x06: // CGA 640×200 2-color
                _currentMode = VideoModeType.CgaGraphics;
                break;
            case 0x0D: // EGA 320×200 16-color
            case 0x0E: // EGA 640×200 16-color
            case 0x10: // EGA 640×350 16-color
            case 0x12: // VGA 640×480 16-color
                _currentMode = VideoModeType.EgaVgaPlanar;
                break;
            case 0x13: // VGA 320×200 256-color
                _currentMode = VideoModeType.Vga256;
                break;
            default:
                _currentMode = VideoModeType.Text;
                _columns = 80;
                _rows = 25;
                break;
        }

        IsDirty = true;
    }

    /// <summary>Set the active display page (text mode).</summary>
    public void SetActivePage(int page)
    {
        _activePage = page;
        IsDirty = true;
    }

    /// <summary>
    /// Called when a byte is written to video memory region.
    /// Updates the renderer to reflect the change.
    /// </summary>
    public void OnVideoMemoryWrite(uint physicalAddress, byte value)
    {
        IsDirty = true;

        if (_currentMode == VideoModeType.Text)
        {
            HandleTextModeWrite(physicalAddress, value);
        }
        else if (_currentMode == VideoModeType.Vga256)
        {
            HandleVga256Write(physicalAddress, value);
        }
        else if (_currentMode == VideoModeType.CgaGraphics)
        {
            HandleCgaGraphicsWrite(physicalAddress, value);
        }
    }

    /// <summary>
    /// Flush the entire video memory to the renderer.
    /// Called after mode changes or when the full screen needs refreshing.
    /// </summary>
    public void FlushToRenderer()
    {
        if (_currentMode == VideoModeType.Text)
        {
            FlushTextMode();
        }
        else if (_currentMode == VideoModeType.Vga256)
        {
            FlushVga256Mode();
        }
        else if (_currentMode == VideoModeType.CgaGraphics)
        {
            FlushCgaGraphicsMode();
        }

        IsDirty = false;
    }

    // ═══════════════════════════════════════════════════════════════
    //  TEXT MODE
    // ═══════════════════════════════════════════════════════════════

    private void HandleTextModeWrite(uint physAddr, byte value)
    {
        uint baseAddr = TextBase + (uint)(_activePage * PageSize);
        if (physAddr < baseAddr || physAddr >= baseAddr + (uint)PageSize)
            return; // Not on active page

        uint offset = physAddr - baseAddr;
        int cellIndex = (int)(offset / 2);
        int row = cellIndex / _columns;
        int col = cellIndex % _columns;

        if (row >= _rows) return;

        if ((offset & 1) == 0)
        {
            // Character byte written
            byte attr = _memory.ReadByte(physAddr + 1);
            _renderer.DrawCharacter(col, row, (char)value, (byte)(attr & 0x0F), (byte)((attr >> 4) & 0x0F));
        }
        else
        {
            // Attribute byte written
            byte ch = _memory.ReadByte(physAddr - 1);
            _renderer.DrawCharacter(col, row, (char)ch, (byte)(value & 0x0F), (byte)((value >> 4) & 0x0F));
        }
    }

    private void FlushTextMode()
    {
        uint baseAddr = TextBase + (uint)(_activePage * PageSize);

        for (int row = 0; row < _rows; row++)
        {
            for (int col = 0; col < _columns; col++)
            {
                uint cellAddr = baseAddr + (uint)((row * _columns + col) * 2);
                byte ch = _memory.ReadByte(cellAddr);
                byte attr = _memory.ReadByte(cellAddr + 1);
                _renderer.DrawCharacter(col, row, (char)ch, (byte)(attr & 0x0F), (byte)((attr >> 4) & 0x0F));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  VGA MODE 13h (320×200×256)
    // ═══════════════════════════════════════════════════════════════

    private void HandleVga256Write(uint physAddr, byte value)
    {
        if (physAddr < VgaGraphicsBase || physAddr >= VgaGraphicsBase + 64000)
            return;

        uint offset = physAddr - VgaGraphicsBase;
        int x = (int)(offset % 320);
        int y = (int)(offset / 320);

        if (x < 320 && y < 200)
        {
            _renderer.DrawPixel(x, y, value);
        }
    }

    private void FlushVga256Mode()
    {
        for (int y = 0; y < 200; y++)
        {
            for (int x = 0; x < 320; x++)
            {
                uint addr = VgaGraphicsBase + (uint)(y * 320 + x);
                byte color = _memory.ReadByte(addr);
                _renderer.DrawPixel(x, y, color);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  CGA GRAPHICS (320×200×4 / 640×200×2)
    // ═══════════════════════════════════════════════════════════════

    private void HandleCgaGraphicsWrite(uint physAddr, byte value)
    {
        if (physAddr < CgaTextBase || physAddr >= CgaTextBase + 0x4000)
            return;

        // CGA interlaced: even lines at B800:0000, odd lines at B800:2000
        uint offset = physAddr - CgaTextBase;
        bool oddBank = offset >= 0x2000;
        if (oddBank) offset -= 0x2000;

        int row = (int)(offset / 80) * 2 + (oddBank ? 1 : 0);
        int byteCol = (int)(offset % 80);

        if (row >= 200) return;

        // 320×200×4: 4 pixels per byte (2 bits each)
        for (int px = 0; px < 4; px++)
        {
            int x = byteCol * 4 + px;
            int colorIndex = (value >> (6 - px * 2)) & 0x03;
            if (x < 320)
                _renderer.DrawPixel(x, row, (byte)colorIndex);
        }
    }

    private void FlushCgaGraphicsMode()
    {
        for (int y = 0; y < 200; y++)
        {
            bool odd = (y & 1) != 0;
            uint bankBase = CgaTextBase + (uint)(odd ? 0x2000 : 0x0000);
            int rowInBank = y / 2;

            for (int byteCol = 0; byteCol < 80; byteCol++)
            {
                uint addr = bankBase + (uint)(rowInBank * 80 + byteCol);
                byte data = _memory.ReadByte(addr);

                for (int px = 0; px < 4; px++)
                {
                    int x = byteCol * 4 + px;
                    int colorIndex = (data >> (6 - px * 2)) & 0x03;
                    if (x < 320)
                        _renderer.DrawPixel(x, y, (byte)colorIndex);
                }
            }
        }
    }

    /// <summary>
    /// Write a character+attribute to video memory at the given position.
    /// Used by BIOS video services to write through the memory-mapped path.
    /// </summary>
    public void WriteTextCell(int col, int row, byte character, byte attribute, int page = -1)
    {
        if (page < 0) page = _activePage;
        uint baseAddr = TextBase + (uint)(page * PageSize);
        uint cellAddr = baseAddr + (uint)((row * _columns + col) * 2);

        _memory.WriteByte(cellAddr, character);
        _memory.WriteByte(cellAddr + 1, attribute);

        if (page == _activePage)
        {
            _renderer.DrawCharacter(col, row, (char)character, (byte)(attribute & 0x0F), (byte)((attribute >> 4) & 0x0F));
        }
    }

    /// <summary>
    /// Read a character+attribute from video memory at the given position.
    /// </summary>
    public (byte character, byte attribute) ReadTextCell(int col, int row, int page = -1)
    {
        if (page < 0) page = _activePage;
        uint baseAddr = TextBase + (uint)(page * PageSize);
        uint cellAddr = baseAddr + (uint)((row * _columns + col) * 2);

        return (_memory.ReadByte(cellAddr), _memory.ReadByte(cellAddr + 1));
    }

    /// <summary>
    /// Clear a region of video memory (for scrolling/clearing).
    /// </summary>
    public void ClearRegion(int col1, int row1, int col2, int row2, byte attribute, int page = -1)
    {
        if (page < 0) page = _activePage;
        uint baseAddr = TextBase + (uint)(page * PageSize);

        for (int row = row1; row <= row2 && row < _rows; row++)
        {
            for (int col = col1; col <= col2 && col < _columns; col++)
            {
                uint cellAddr = baseAddr + (uint)((row * _columns + col) * 2);
                _memory.WriteByte(cellAddr, 0x20); // Space
                _memory.WriteByte(cellAddr + 1, attribute);

                if (page == _activePage)
                    _renderer.DrawCharacter(col, row, ' ', (byte)(attribute & 0x0F), (byte)((attribute >> 4) & 0x0F));
            }
        }
    }

    private enum VideoModeType
    {
        Text,
        CgaGraphics,
        EgaVgaPlanar,
        Vga256
    }
}
