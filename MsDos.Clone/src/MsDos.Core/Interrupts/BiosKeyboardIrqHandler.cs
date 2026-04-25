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

    /// <summary>Shifted scancode → ASCII mapping (Shift held).</summary>
    private static readonly byte[] ScanCodeToAsciiShifted = new byte[128]
    {
        // 0x00-0x0F
        0x00, 0x1B, 0x21, 0x40, 0x23, 0x24, 0x25, 0x5E, // NUL, ESC, !,@,#,$,%,^
        0x26, 0x2A, 0x28, 0x29, 0x5F, 0x2B, 0x08, 0x00, // &,*,(,),_,+,BS,Shift-TAB
        // 0x10-0x1F
        0x51, 0x57, 0x45, 0x52, 0x54, 0x59, 0x55, 0x49, // Q,W,E,R,T,Y,U,I
        0x4F, 0x50, 0x7B, 0x7D, 0x0D, 0x00, 0x41, 0x53, // O,P,{,},Enter,Ctrl,A,S
        // 0x20-0x2F
        0x44, 0x46, 0x47, 0x48, 0x4A, 0x4B, 0x4C, 0x3A, // D,F,G,H,J,K,L,:
        0x22, 0x7E, 0x00, 0x7C, 0x5A, 0x58, 0x43, 0x56, // ",~,LShift,|,Z,X,C,V
        // 0x30-0x3F
        0x42, 0x4E, 0x4D, 0x3C, 0x3E, 0x3F, 0x00, 0x2A, // B,N,M,<,>,?,RShift,*
        0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Alt,Space,CapsLock,F1-F4
        // 0x40-0x4F
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // F5-F10,NumLock,ScrollLock
        0x37, 0x38, 0x39, 0x2D, 0x34, 0x35, 0x36, 0x2B, // Numpad 7,8,9,-,4,5,6,+
        // 0x50-0x5F
        0x31, 0x32, 0x33, 0x30, 0x2E, 0x00, 0x00, 0x00, // Numpad 1,2,3,0,.
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // 0x60-0x6F
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // 0x70-0x7F
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    /// <summary>Ctrl+key → ASCII mapping. Letters map to 0x01-0x1A.</summary>
    private static readonly byte[] ScanCodeToAsciiCtrl = new byte[128]
    {
        // 0x00-0x0F
        0x00, 0x1B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, // NUL,ESC,...,Ctrl+6=^^
        0x00, 0x00, 0x00, 0x00, 0x1F, 0x00, 0x7F, 0x00, // ...,Ctrl+-=^_,...,Ctrl+BS=DEL
        // 0x10-0x1F
        0x11, 0x17, 0x05, 0x12, 0x14, 0x19, 0x15, 0x09, // ^Q,^W,^E,^R,^T,^Y,^U,^I
        0x0F, 0x10, 0x1B, 0x1D, 0x0A, 0x00, 0x01, 0x13, // ^O,^P,^[,^],^J(Enter),Ctrl,^A,^S
        // 0x20-0x2F
        0x04, 0x06, 0x07, 0x08, 0x0A, 0x0B, 0x0C, 0x00, // ^D,^F,^G,^H,^J,^K,^L
        0x00, 0x00, 0x00, 0x1C, 0x1A, 0x18, 0x03, 0x16, // ...,^\,^Z,^X,^C,^V
        // 0x30-0x3F
        0x02, 0x0E, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, // ^B,^N,^M
        0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Alt,Space,CapsLock,...
        // 0x40-0x7F (remaining entries are 0)
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    // Caps Lock / Num Lock / Scroll Lock toggle state
    private bool _capsLock;
    private bool _numLock;
    private bool _scrollLock;

    public BiosKeyboardIrqHandler(MemoryBus mem, PicController pic)
    {
        _mem = mem;
        _pic = pic;
    }

    /// <summary>Fired when Ctrl+Alt+Del is pressed (reboot request).</summary>
    public event Action? OnRebootRequested;

    /// <summary>
    /// Called when a key event arrives from the platform.
    /// Maps the scancode through the appropriate table (unshifted/shifted/ctrl/alt),
    /// handles toggle keys (Caps Lock, Num Lock, Scroll Lock), updates the BDA
    /// shift flags, and writes the scancode+ASCII into the BDA keyboard buffer.
    /// </summary>
    public void OnKeyEvent(DosKeyEventArgs e)
    {
        byte scanCode = e.ScanCode;

        // Update port 0x60 value
        _lastScanCode = scanCode;

        // Handle toggle keys
        if (scanCode == 0x3A) { _capsLock = !_capsLock; }    // Caps Lock
        if (scanCode == 0x45) { _numLock = !_numLock; }      // Num Lock
        if (scanCode == 0x46) { _scrollLock = !_scrollLock; } // Scroll Lock

        // Update shift flags at BDA 0040:0017
        byte shiftFlags = 0;
        if (e.Shift) shiftFlags |= 0x03;  // Left+Right Shift
        if (e.Ctrl) shiftFlags |= 0x04;   // Ctrl
        if (e.Alt) shiftFlags |= 0x08;    // Alt
        if (_scrollLock) shiftFlags |= 0x10;
        if (_numLock) shiftFlags |= 0x20;
        if (_capsLock) shiftFlags |= 0x40;
        // Bit 7 = Insert mode (not tracked here)
        _mem.WriteByte(0x0040, 0x0017, shiftFlags);

        // Extended shift flags at BDA 0040:0018
        byte extFlags = 0;
        // Left Ctrl=0x01, Left Alt=0x02 (treated same as generic for now)
        if (e.Ctrl) extFlags |= 0x01;
        if (e.Alt) extFlags |= 0x02;
        _mem.WriteByte(0x0040, 0x0018, extFlags);

        // Handle Ctrl+Alt+Del → reboot
        if (e.Ctrl && e.Alt && scanCode == 0x53)
        {
            OnRebootRequested?.Invoke();
            return;
        }

        // Handle Ctrl+Break (scancode 0x46 with Ctrl)
        if (e.Ctrl && scanCode == 0x46)
        {
            // Clear keyboard buffer
            ushort bufStart = _mem.ReadWord(0x0040, 0x0080);
            if (bufStart == 0) bufStart = 0x001E;
            _mem.WriteWord(0x0040, 0x001A, bufStart);
            _mem.WriteWord(0x0040, 0x001C, bufStart);
            // Enqueue Ctrl+Break (scancode=0, ascii=0, but INT 1Bh would fire)
            _pic.SendEOI();
            return;
        }

        // Determine ASCII character based on modifier state
        byte asciiChar;
        if (e.AsciiChar != 0 && !e.Ctrl && !e.Alt)
        {
            // Platform already provided the correct ASCII — use it
            asciiChar = e.AsciiChar;

            // Apply Caps Lock toggle: if CapsLock is on and it's a letter,
            // flip the case (unless Shift is also held, which cancels it)
            if (_capsLock && scanCode >= 0x10 && scanCode <= 0x32)
            {
                bool isLetter = (asciiChar >= 'a' && asciiChar <= 'z') ||
                                (asciiChar >= 'A' && asciiChar <= 'Z');
                if (isLetter)
                {
                    if (e.Shift)
                    {
                        // Shift + CapsLock = lowercase
                        asciiChar = (byte)char.ToLowerInvariant((char)asciiChar);
                    }
                    else
                    {
                        // CapsLock without Shift = uppercase
                        asciiChar = (byte)char.ToUpperInvariant((char)asciiChar);
                    }
                }
            }
        }
        else if (scanCode < 128)
        {
            // Use our scancode tables
            if (e.Alt)
            {
                // Alt+letter: ASCII=0, scancode is passed for function key handling
                asciiChar = 0;
            }
            else if (e.Ctrl)
            {
                asciiChar = ScanCodeToAsciiCtrl[scanCode];
            }
            else if (e.Shift)
            {
                asciiChar = ScanCodeToAsciiShifted[scanCode];
                // Apply Caps Lock (inverts shift for letters)
                if (_capsLock && asciiChar >= 'A' && asciiChar <= 'Z')
                    asciiChar = (byte)(asciiChar + 32); // back to lowercase
            }
            else
            {
                asciiChar = ScanCodeToAscii[scanCode];
                // Apply Caps Lock
                if (_capsLock && asciiChar >= 'a' && asciiChar <= 'z')
                    asciiChar = (byte)(asciiChar - 32); // to uppercase
            }
        }
        else
        {
            asciiChar = 0;
        }

        // Write scancode+ASCII into BDA circular keyboard buffer
        EnqueueKey(scanCode, asciiChar);

        // Send EOI for IRQ 1
        _pic.SendEOI();
    }

    /// <summary>
    /// Enqueue a scancode+ASCII word into the BDA keyboard buffer.
    /// Buffer is at 0040:001E–003D (32 bytes = 16 words).
    /// Head pointer at 0040:001A (read position), Tail pointer at 0040:001C (write position).
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

        // Calculate next tail position
        ushort nextTail = (ushort)(tail + 2);
        if (nextTail >= bufEnd)
            nextTail = bufStart;

        // Check if buffer is full (next tail == head)
        if (nextTail == head)
            return; // Buffer full, drop the keystroke

        // Write the key word at current tail: low byte = ASCII, high byte = scancode
        _mem.WriteByte(0x0040, tail, asciiChar);
        _mem.WriteByte(0x0040, (ushort)(tail + 1), scanCode);

        // Advance tail pointer
        _mem.WriteWord(0x0040, 0x001C, nextTail);
    }

    /// <summary>Read port 0x60 — returns the last scancode received.</summary>
    public byte ReadPort60() => _lastScanCode;

    /// <summary>Write port 0x60 — keyboard controller command (ignored in emulation).</summary>
    public void WritePort60(byte value) { /* Ignored */ }
}
