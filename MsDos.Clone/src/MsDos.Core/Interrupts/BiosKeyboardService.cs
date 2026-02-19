using MsDos.Core.Cpu;
using MsDos.Core.Platform;

namespace MsDos.Core.Interrupts;

/// <summary>
/// BIOS INT 16h keyboard services.
/// </summary>
public sealed class BiosKeyboardService
{
    private readonly Cpu8086 _cpu;
    private readonly IEventRegistry _events;

    public BiosKeyboardService(Cpu8086 cpu, IEventRegistry events)
    {
        _cpu = cpu;
        _events = events;
    }

    public void Handle()
    {
        switch (_cpu.Regs.AH)
        {
            case 0x00: // Wait for keypress
            case 0x10:
            {
                // Blocking read - use synchronous wait
                var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
                _cpu.Regs.AH = key.ScanCode;
                _cpu.Regs.AL = key.AsciiChar;
                break;
            }
            case 0x01: // Check for keypress (non-blocking)
            case 0x11:
            {
                if (_events.IsKeyAvailable)
                {
                    var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
                    _cpu.Regs.AH = key.ScanCode;
                    _cpu.Regs.AL = key.AsciiChar;
                    _cpu.Regs.Flags &= ~CpuFlags.Zero; // Key available
                }
                else
                {
                    _cpu.Regs.Flags |= CpuFlags.Zero; // No key available
                }
                break;
            }
            case 0x02: // Get shift flags
            case 0x12:
                _cpu.Regs.AL = 0; // No shift keys pressed (simplified)
                break;
        }
    }
}
