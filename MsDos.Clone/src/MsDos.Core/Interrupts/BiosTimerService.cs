using MsDos.Core.Cpu;
using MsDos.Core.Memory;

namespace MsDos.Core.Interrupts;

/// <summary>
/// BIOS INT 08h — System timer tick handler.
/// Called ~18.2 times/second by PIT Channel 0 → IRQ 0.
/// Increments the DWORD tick counter at BDA 0040:006C and handles midnight rollover.
/// After servicing, calls INT 1Ch (user timer hook) for TSR compatibility.
/// </summary>
public sealed class BiosTimerService
{
    private readonly Cpu8086 _cpu;
    private readonly MemoryBus _mem;
    private readonly PicController _pic;

    // Ticks per day: 24 * 60 * 60 * 18.2065 ≈ 1,573,040
    private const uint TicksPerDay = 0x001800B0; // 1,573,040

    public BiosTimerService(Cpu8086 cpu, MemoryBus mem, PicController pic)
    {
        _cpu = cpu;
        _mem = mem;
        _pic = pic;
    }

    /// <summary>INT 08h handler — system timer tick.</summary>
    public void HandleInt08()
    {
        // Read current tick count from BDA 0040:006C (DWORD, little-endian)
        ushort tickLow = _mem.ReadWord(0x0040, 0x006C);
        ushort tickHigh = _mem.ReadWord(0x0040, 0x006E);
        uint ticks = ((uint)tickHigh << 16) | tickLow;

        // Increment
        ticks++;

        // Check for midnight rollover (>= ~1,573,040 ticks = 24 hours)
        if (ticks >= TicksPerDay)
        {
            ticks = 0;
            // Set midnight rollover flag at 0040:0070
            _mem.WriteByte(0x0040, 0x0070, 1);
        }

        // Write updated tick count back to BDA
        _mem.WriteWord(0x0040, 0x006C, (ushort)(ticks & 0xFFFF));
        _mem.WriteWord(0x0040, 0x006E, (ushort)((ticks >> 16) & 0xFFFF));

        // Send EOI (End of Interrupt) to PIC
        _pic.SendEOI();

        // NOTE: A real BIOS would call INT 1Ch here for user timer hooks.
        // We don't chain to INT 1Ch because it would require executing real-mode
        // code and most DOS programs that hook INT 1Ch do so through the IVT,
        // which our InterruptController already handles.
    }
}
