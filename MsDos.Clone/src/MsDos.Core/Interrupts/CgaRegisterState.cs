using MsDos.Core.Memory;

namespace MsDos.Core.Interrupts;

/// <summary>
/// CGA/EGA/VGA register state tracker.
/// Stores writes to CGA CRTC (0x3D4/0x3D5), mode control (0x3D8),
/// color select (0x3D9), and the status register (0x3DA) behaviour.
/// Exposes the register file so the renderer can determine active mode,
/// cursor shape, palette, display geometry, and overscan color.
/// </summary>
public sealed class CgaRegisterState
{
    // --- CRTC (6845) registers ---
    private byte _crtcIndex;
    private readonly byte[] _crtcRegs = new byte[24]; // R0–R17 (0x00–0x17)

    // --- CGA mode/color control ---
    /// <summary>Mode control register (port 0x3D8).</summary>
    public byte ModeControl { get; private set; }

    /// <summary>Color select register (port 0x3D9).</summary>
    public byte ColorSelect { get; private set; }

    // --- VGA DAC ---
    private byte _dacWriteIndex;
    private byte _dacReadIndex;
    private int _dacWriteComponent; // 0=R, 1=G, 2=B
    private int _dacReadComponent;
    private readonly byte[] _dacPalette = new byte[256 * 3];

    // --- Attribute controller ---
    private byte _attrIndex;
    private bool _attrFlipFlop; // toggled by reads of 0x3DA
    private readonly byte[] _attrRegs = new byte[21]; // 0x00–0x14

    // --- Sequencer ---
    private byte _seqIndex;
    private readonly byte[] _seqRegs = new byte[5]; // 0x00–0x04

    // --- Graphics controller ---
    private byte _gcIndex;
    private readonly byte[] _gcRegs = new byte[9]; // 0x00–0x08

    // --- Misc output ---
    private byte _miscOutput = 0x63;

    // --- Status register toggle for vertical retrace ---
    private byte _statusFlip;

    /// <summary>Read a CRTC register by index.</summary>
    public byte GetCrtcRegister(int index) =>
        index >= 0 && index < _crtcRegs.Length ? _crtcRegs[index] : (byte)0;

    /// <summary>The currently selected CGA palette (derived from color select register).</summary>
    public int CgaPalette => (ColorSelect >> 5) & 1;

    /// <summary>CGA background/border color (low 4 bits of color select).</summary>
    public byte CgaBackgroundColor => (byte)(ColorSelect & 0x0F);

    /// <summary>Whether high-intensity palette is active (bit 4 of color select).</summary>
    public bool CgaHighIntensity => (ColorSelect & 0x10) != 0;

    /// <summary>Whether video output is enabled (bit 3 of mode control).</summary>
    public bool VideoEnabled => (ModeControl & 0x08) != 0;

    /// <summary>Whether in graphics mode (bit 1 of mode control).</summary>
    public bool GraphicsMode => (ModeControl & 0x02) != 0;

    /// <summary>Whether 80-column text mode (bit 0 of mode control).</summary>
    public bool Mode80Column => (ModeControl & 0x01) != 0;

    /// <summary>Whether hi-res graphics (640x200) mode (bit 4 of mode control).</summary>
    public bool HiResGraphics => (ModeControl & 0x10) != 0;

    /// <summary>Whether blinking is enabled (bit 5 of mode control).</summary>
    public bool BlinkEnabled => (ModeControl & 0x20) != 0;

    /// <summary>Get the DAC palette array (256 entries × 3 bytes = R,G,B 6-bit values).</summary>
    public ReadOnlySpan<byte> DacPalette => _dacPalette;

    /// <summary>Register all CGA/VGA port handlers on the I/O port bus.</summary>
    public void RegisterPorts(IOPortBus ports)
    {
        // CRTC index/data
        ports.Register(0x3D4, port => _crtcIndex, (port, val) => _crtcIndex = (byte)(val & 0x1F));
        ports.Register(0x3D5, ReadCrtcData, WriteCrtcData);

        // Mode control / color select
        ports.Register(0x3D8, port => ModeControl, (port, val) => ModeControl = val);
        ports.Register(0x3D9, port => ColorSelect, (port, val) => ColorSelect = val);

        // Input Status Register 1 (toggles retrace bits + resets attr flip-flop)
        ports.Register(0x3DA, ReadStatus, null);

        // Attribute controller
        ports.Register(0x3C0, ReadAttr, WriteAttr);

        // Sequencer
        ports.Register(0x3C4, port => _seqIndex, (port, val) => _seqIndex = (byte)(val & 0x07));
        ports.Register(0x3C5, ReadSeqData, WriteSeqData);

        // Graphics controller
        ports.Register(0x3CE, port => _gcIndex, (port, val) => _gcIndex = (byte)(val & 0x0F));
        ports.Register(0x3CF, ReadGcData, WriteGcData);

        // VGA DAC
        ports.Register(0x3C7, port => _dacReadIndex, (port, val) =>
        {
            _dacReadIndex = val;
            _dacReadComponent = 0;
        });
        ports.Register(0x3C8, port => _dacWriteIndex, (port, val) =>
        {
            _dacWriteIndex = val;
            _dacWriteComponent = 0;
        });
        ports.Register(0x3C9, ReadDac, WriteDac);

        // Misc output register
        ports.Register(0x3CC, port => _miscOutput, null); // Read
        ports.Register(0x3C2, port => 0, (port, val) => _miscOutput = val); // Write / Input Status 0
    }

    private byte ReadCrtcData(ushort port)
    {
        return _crtcIndex < _crtcRegs.Length ? _crtcRegs[_crtcIndex] : (byte)0;
    }

    private void WriteCrtcData(ushort port, byte val)
    {
        if (_crtcIndex < _crtcRegs.Length)
            _crtcRegs[_crtcIndex] = val;
    }

    private byte ReadStatus(ushort port)
    {
        // Toggle bits 0 (display enable) and 3 (vertical retrace) each read
        _statusFlip ^= 0x09;
        // Reset attribute controller flip-flop
        _attrFlipFlop = false;
        return _statusFlip;
    }

    private byte ReadAttr(ushort port)
    {
        return _attrIndex < _attrRegs.Length ? _attrRegs[_attrIndex] : (byte)0;
    }

    private void WriteAttr(ushort port, byte val)
    {
        if (!_attrFlipFlop)
        {
            _attrIndex = (byte)(val & 0x1F);
        }
        else
        {
            if (_attrIndex < _attrRegs.Length)
                _attrRegs[_attrIndex] = val;
        }
        _attrFlipFlop = !_attrFlipFlop;
    }

    private byte ReadSeqData(ushort port)
    {
        return _seqIndex < _seqRegs.Length ? _seqRegs[_seqIndex] : (byte)0;
    }

    private void WriteSeqData(ushort port, byte val)
    {
        if (_seqIndex < _seqRegs.Length)
            _seqRegs[_seqIndex] = val;
    }

    private byte ReadGcData(ushort port)
    {
        return _gcIndex < _gcRegs.Length ? _gcRegs[_gcIndex] : (byte)0;
    }

    private void WriteGcData(ushort port, byte val)
    {
        if (_gcIndex < _gcRegs.Length)
            _gcRegs[_gcIndex] = val;
    }

    private byte ReadDac(ushort port)
    {
        int idx = _dacReadIndex * 3 + _dacReadComponent;
        byte val = idx < _dacPalette.Length ? _dacPalette[idx] : (byte)0;
        _dacReadComponent++;
        if (_dacReadComponent >= 3)
        {
            _dacReadComponent = 0;
            _dacReadIndex++;
        }
        return val;
    }

    private void WriteDac(ushort port, byte val)
    {
        int idx = _dacWriteIndex * 3 + _dacWriteComponent;
        if (idx < _dacPalette.Length)
            _dacPalette[idx] = val;
        _dacWriteComponent++;
        if (_dacWriteComponent >= 3)
        {
            _dacWriteComponent = 0;
            _dacWriteIndex++;
        }
    }
}
