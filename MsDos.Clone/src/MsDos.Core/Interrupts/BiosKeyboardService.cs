using MsDos.Core.Cpu;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Interrupts;

/// <summary>Simple scan code + ASCII pair for keyboard buffer entries.</summary>
public readonly record struct KeyEvent(byte ScanCode, byte AsciiChar);

/// <summary>
/// BIOS INT 16h keyboard services.
/// Reads keystrokes from the BDA circular keyboard buffer (0040:001E–003D)
/// which is populated by BiosKeyboardIrqHandler (INT 09h path).
/// Falls back to IEventRegistry for backward compatibility when the BDA buffer is empty.
/// </summary>
public sealed class BiosKeyboardService
{
    private readonly Cpu8086 _cpu;
    private readonly IEventRegistry _events;
    private readonly MemoryBus? _mem;

    // Shift/modifier state flags
    private byte _shiftFlags; // Byte 0040:0017

    public BiosKeyboardService(Cpu8086 cpu, IEventRegistry events, MemoryBus? mem = null)
    {
        _cpu = cpu;
        _events = events;
        _mem = mem;
    }

    private void ReexecuteCurrentInterrupt()
    {
        // INT nn is 2 bytes (CD nn). Rewind IP to emulate BIOS blocking behavior
        // without blocking host runtime threads.
        _cpu.Regs.IP -= 2;
        _cpu.IsWaitingForInput = true; // Signal execution loop to yield
    }

    /// <summary>Update modifier flags from external input events.</summary>
    public void SetShiftFlags(byte flags) => _shiftFlags = flags;

    /// <summary>
    /// Try to dequeue a key from the BDA keyboard buffer.
    /// Returns true if a key was available, false if the buffer is empty.
    /// </summary>
    private bool TryDequeueFromBda(out byte scanCode, out byte asciiChar)
    {
        scanCode = 0;
        asciiChar = 0;
        if (_mem == null) return false;

        ushort head = _mem.ReadWord(0x0040, 0x001A);
        ushort tail = _mem.ReadWord(0x0040, 0x001C);

        if (head == tail)
            return false; // Buffer empty

        ushort bufStart = _mem.ReadWord(0x0040, 0x0080);
        ushort bufEnd = _mem.ReadWord(0x0040, 0x0082);
        if (bufStart == 0) bufStart = 0x001E;
        if (bufEnd == 0) bufEnd = 0x003E;

        // Read the key word at tail: low byte = ASCII, high byte = scancode
        asciiChar = _mem.ReadByte(0x0040, tail);
        scanCode = _mem.ReadByte(0x0040, (ushort)(tail + 1));

        // Advance tail pointer
        ushort nextTail = (ushort)(tail + 2);
        if (nextTail >= bufEnd)
            nextTail = bufStart;
        _mem.WriteWord(0x0040, 0x001C, nextTail);

        return true;
    }

    /// <summary>
    /// Peek at the next key in the BDA keyboard buffer without consuming it.
    /// Returns true if a key is available.
    /// </summary>
    private bool TryPeekFromBda(out byte scanCode, out byte asciiChar)
    {
        scanCode = 0;
        asciiChar = 0;
        if (_mem == null) return false;

        ushort head = _mem.ReadWord(0x0040, 0x001A);
        ushort tail = _mem.ReadWord(0x0040, 0x001C);

        if (head == tail)
            return false; // Buffer empty

        // Read but don't advance tail
        asciiChar = _mem.ReadByte(0x0040, tail);
        scanCode = _mem.ReadByte(0x0040, (ushort)(tail + 1));
        return true;
    }

    /// <summary>Check if the BDA keyboard buffer has a key available.</summary>
    private bool IsBdaKeyAvailable()
    {
        if (_mem == null) return false;
        ushort head = _mem.ReadWord(0x0040, 0x001A);
        ushort tail = _mem.ReadWord(0x0040, 0x001C);
        return head != tail;
    }

    public void Handle()
    {
        switch (_cpu.Regs.AH)
        {
            case 0x00: // Wait for keypress (standard)
            case 0x10: // Wait for keypress (enhanced)
            {
                // Try BDA buffer first
                if (TryDequeueFromBda(out byte sc, out byte ac))
                {
                    _cpu.Regs.AH = sc;
                    _cpu.Regs.AL = ac;
                    return;
                }

                // Fall back to IEventRegistry (for backward compat with direct event model)
                if (!_events.IsKeyAvailable)
                {
                    ReexecuteCurrentInterrupt();
                    return;
                }

                var readTask = _events.ReadKeyAsync();
                if (!readTask.IsCompletedSuccessfully)
                {
                    ReexecuteCurrentInterrupt();
                    return;
                }

                var evt = readTask.GetAwaiter().GetResult();
                _cpu.Regs.AH = evt.ScanCode;
                _cpu.Regs.AL = evt.AsciiChar;
                break;
            }

            case 0x01: // Check for keypress (non-destructive peek, standard)
            case 0x11: // Check for keypress (non-destructive peek, enhanced)
            {
                // Try BDA buffer first
                if (TryPeekFromBda(out byte sc, out byte ac))
                {
                    _cpu.Regs.AH = sc;
                    _cpu.Regs.AL = ac;
                    _cpu.Regs.Flags &= ~CpuFlags.Zero; // Key available
                    return;
                }

                // Fall back to IEventRegistry
                if (_events.IsKeyAvailable)
                {
                    var readTask = _events.ReadKeyAsync();
                    if (!readTask.IsCompletedSuccessfully)
                    {
                        _cpu.Regs.Flags |= CpuFlags.Zero; // treat as not available this cycle
                        break;
                    }

                    var evt = readTask.GetAwaiter().GetResult();
                    // Push into BDA buffer so next AH=00 can consume it
                    if (_mem != null)
                    {
                        // Enqueue into BDA buffer for consistency
                        EnqueueToBda(evt.ScanCode, evt.AsciiChar);
                    }
                    _cpu.Regs.AH = evt.ScanCode;
                    _cpu.Regs.AL = evt.AsciiChar;
                    _cpu.Regs.Flags &= ~CpuFlags.Zero; // Key available
                }
                else
                {
                    _cpu.Regs.Flags |= CpuFlags.Zero; // No key available
                }
                break;
            }

            case 0x02: // Get shift flags (standard)
                if (_mem != null)
                    _cpu.Regs.AL = _mem.ReadByte(0x0040, 0x0017);
                else
                    _cpu.Regs.AL = _shiftFlags;
                break;

            case 0x03: // Set typematic rate and delay
                // Accept but ignore (we don't control real keyboard timing)
                break;

            case 0x05: // Store key into keyboard buffer
                if (_mem != null)
                    EnqueueToBda(_cpu.Regs.CL, _cpu.Regs.CH);
                _cpu.Regs.AL = 0; // Success
                break;

            case 0x12: // Get extended shift flags
                if (_mem != null)
                    _cpu.Regs.AL = _mem.ReadByte(0x0040, 0x0017);
                else
                    _cpu.Regs.AL = _shiftFlags;
                _cpu.Regs.AH = 0; // Extended flags (no extra modifiers)
                break;

            default:
                // Unknown sub-function
                break;
        }
    }

    /// <summary>Enqueue a key into the BDA buffer (used by fallback path and AH=05h).</summary>
    private void EnqueueToBda(byte scanCode, byte asciiChar)
    {
        if (_mem == null) return;

        ushort head = _mem.ReadWord(0x0040, 0x001A);
        ushort tail = _mem.ReadWord(0x0040, 0x001C);
        ushort bufStart = _mem.ReadWord(0x0040, 0x0080);
        ushort bufEnd = _mem.ReadWord(0x0040, 0x0082);
        if (bufStart == 0) bufStart = 0x001E;
        if (bufEnd == 0) bufEnd = 0x003E;

        ushort nextHead = (ushort)(head + 2);
        if (nextHead >= bufEnd) nextHead = bufStart;
        if (nextHead == tail) return; // Full

        _mem.WriteByte(0x0040, head, asciiChar);
        _mem.WriteByte(0x0040, (ushort)(head + 1), scanCode);
        _mem.WriteWord(0x0040, 0x001A, nextHead);
    }
}
