using MsDos.Core.Cpu;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Interrupts;

/// <summary>
/// Manages interrupt handlers and dispatches INT calls from the CPU
/// to the appropriate service (BIOS, DOS kernel, user-registered).
/// </summary>
public sealed class InterruptController
{
    private readonly Cpu8086 _cpu;
    private readonly MemoryBus _mem;
    private readonly Dictionary<byte, Action> _handlers = new();

    /// <summary>
    /// When true, only BIOS-level handlers (INT 08h-1Ah, 33h) are active.
    /// DOS-level handlers (INT 20h-24h) are bypassed so the real DOS loaded
    /// from disk can service those interrupts via the IVT.
    /// </summary>
    public bool NativeBootMode { get; set; }

    public InterruptController(Cpu8086 cpu, MemoryBus mem)
    {
        _cpu = cpu;
        _mem = mem;
        _cpu.InterruptTriggered += OnInterrupt;
    }

    /// <summary>Register a managed handler for the given interrupt vector.</summary>
    public void RegisterHandler(byte vector, Action handler)
    {
        _handlers[vector] = handler;
    }

    /// <summary>Unregister a managed handler for the given interrupt vector.</summary>
    public void UnregisterHandler(byte vector)
    {
        _handlers.Remove(vector);
    }

    private void OnInterrupt(byte vector)
    {
        // In native boot mode, skip managed DOS handlers (0x20-0x24, 0x25-0x28)
        // so the real DOS loaded from disk can handle them via the IVT.
        bool useManaged = _handlers.ContainsKey(vector);
        if (useManaged && NativeBootMode && vector >= 0x20 && vector <= 0x28)
            useManaged = false;

        if (useManaged)
        {
            _handlers[vector]();
            // Pop the saved IP, CS, Flags that CPU pushed
            _cpu.Regs.IP = Pop();
            _cpu.Regs.CS = Pop();
            ushort savedFlags = Pop(); // Clean up stack but DON'T restore
            // Keep the handler's flag modifications (e.g., carry flag for error indication).
            // Only restore Interrupt flag (IF) which was cleared by the INT instruction.
            _cpu.Regs.Flags |= (CpuFlags)(savedFlags & (ushort)CpuFlags.Interrupt);
        }
        else
        {
            // Check IVT for a real-mode handler address
            uint ivtAddr = (uint)(vector * 4);
            ushort handlerOff = _mem.ReadWord(ivtAddr);
            ushort handlerSeg = _mem.ReadWord(ivtAddr + 2);

            if (handlerSeg != 0 || handlerOff != 0)
            {
                // Stack already has FLAGS/CS/IP from TriggerInterrupt.
                // Just redirect execution to the IVT handler.
                // IRET in the handler will pop and restore FLAGS/CS/IP.
                _cpu.Regs.CS = handlerSeg;
                _cpu.Regs.IP = handlerOff;
            }
            else
            {
                // No handler - just pop and return
                _cpu.Regs.IP = Pop();
                _cpu.Regs.CS = Pop();
                _cpu.Regs.Flags = (CpuFlags)Pop();
            }
        }
    }

    private void Push(ushort value)
    {
        _cpu.Regs.SP -= 2;
        _mem.WriteWord(_cpu.Regs.SS, _cpu.Regs.SP, value);
    }

    private ushort Pop()
    {
        ushort val = _mem.ReadWord(_cpu.Regs.SS, _cpu.Regs.SP);
        _cpu.Regs.SP += 2;
        return val;
    }
}
