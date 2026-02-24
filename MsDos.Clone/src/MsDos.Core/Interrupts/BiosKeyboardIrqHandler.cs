using MsDos.Core.Cpu;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Interrupts;

/// <summary>
/// BIOS INT 09h — Keyboard hardware IRQ handler.
/// When a key event arrives from the platform, writes the scancode+ASCII pair
/// into the BDA circular keyboard buffer (0040:001E–003D) with head at 0040:001A
/// and tail at 0040:001C. This makes the BDA buffer the single source of truth,
/// matching real hardware behavior where INT 09h fires on each keystroke.
///
/// Also updates port 0x60 (keyboard data register) with the last scancode
/// so programs that read the keyboard controller directly can work.
/// </summary>
public sealed class BiosKeyboardIrqHandler
{
    private readonly MemoryBus _mem;
    private readonly PicController _pic;

    // Last scancode seen — returned by port 0x60
    private byte _lastScanCode;

    /// <summary>Standard IBM PC scancode → ASCII mapping for unshifted keys.</summary>
    private static readonly byte[] ScanCodeToAscii = new byte[128]
    {
        // 0x00-0x0F
        0x00, 0x1B, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, // NUL, ESC, 1-6
        0x37, 0x38, 0x39, 0x30, 0x2D, 0x3D, 0x08, 0x09, // 7-0, -, =, BS, TAB
        // 0x10-0x1F
        0x71, 0x77, 0x65, 0x72, 0x74, 0x79, 0x75, 0x69, // q,w,e,r,t,y,u,i
        0x6F, 0x70, 0x5B, 0x5D, 0x0D, 0x00, 0x61, 0x73, // o,p,[,],Enter,Ctrl,a,s
        // 0x20-0x2F
        0x64, 0x66, 0x67, 0x68, 0x6A, 0x6B, 0x6C, 0x3B, // d,f,g,h,j,k,l,;
        0x27, 0x60, 0x00, 0x5C, 0x7A, 0x78, 0x63, 0x76, // ',`,LShift,\,z,x,c,v
        // 0x30-0x3F
        0x62, 0x6E, 0x6D, 0x2C, 0x2E, 0x2F, 0x00, 0x2A, // b,n,m,comma,.,/,RShift,*
        0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Alt,Space,CapsLock,F1-F4
        // 0x40-0x4F
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // F5-F10,NumLock,ScrollLock
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Home,Up,PgUp,-,Left,5,Right,+
        // 0x50-0x5F
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Down,PgDn,Ins,Del,...
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // 0x60-0x6F
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // 0x70-0x7F
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    public BiosKeyboardIrqHandler(MemoryBus mem, PicController pic)
    {
        _mem = mem;
        _pic = pic;
    }

    /// <summary>
    /// Called when a key event arrives from the platform.
    /// Writes the scancode+ASCII into the BDA circular keyboard buffer
    /// and updates shift flags in the BDA.
    /// </summary>
    public void OnKeyEvent(DosKeyEventArgs e)
    {
        byte scanCode = e.ScanCode;
        byte asciiChar = e.AsciiChar;

        // Update port 0x60 value
        _lastScanCode = scanCode;

        // Update shift flags at BDA 0040:0017
        byte shiftFlags = 0;
        if (e.Shift) shiftFlags |= 0x03; // Both shift flags
        if (e.Ctrl) shiftFlags |= 0x04;
        if (e.Alt) shiftFlags |= 0x08;
        _mem.WriteByte(0x0040, 0x0017, shiftFlags);

        // Write scancode+ASCII into BDA circular keyboard buffer
        EnqueueKey(scanCode, asciiChar);

        // Send EOI for IRQ 1
        _pic.SendEOI();
    }

    /// <summary>
    /// Enqueue a scancode+ASCII word into the BDA keyboard buffer.
    /// Buffer is at 0040:001E–003D (32 bytes = 16 words).
    /// Head pointer at 0040:001A, Tail pointer at 0040:001C.
    /// Buffer start at 0040:0080, Buffer end at 0040:0082.
    /// </summary>
    private void EnqueueKey(byte scanCode, byte asciiChar)
    {
        ushort head = _mem.ReadWord(0x0040, 0x001A);
        ushort tail = _mem.ReadWord(0x0040, 0x001C);
        ushort bufStart = _mem.ReadWord(0x0040, 0x0080);
        ushort bufEnd = _mem.ReadWord(0x0040, 0x0082);

        // Default buffer bounds if not initialized
        if (bufStart == 0) bufStart = 0x001E;
        if (bufEnd == 0) bufEnd = 0x003E;

        // Calculate next head position
        ushort nextHead = (ushort)(head + 2);
        if (nextHead >= bufEnd)
            nextHead = bufStart;

        // Check if buffer is full (next head == tail)
        if (nextHead == tail)
            return; // Buffer full, drop the keystroke

        // Write the key word at current head: low byte = ASCII, high byte = scancode
        _mem.WriteByte(0x0040, head, asciiChar);
        _mem.WriteByte(0x0040, (ushort)(head + 1), scanCode);

        // Advance head pointer
        _mem.WriteWord(0x0040, 0x001A, nextHead);
    }

    /// <summary>Read port 0x60 — returns the last scancode received.</summary>
    public byte ReadPort60() => _lastScanCode;

    /// <summary>Write port 0x60 — keyboard controller command (ignored in emulation).</summary>
    public void WritePort60(byte value) { /* Ignored */ }
}
