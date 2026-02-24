using MsDos.Core.Memory;

namespace MsDos.Core.Interrupts;

/// <summary>
/// Intel 8259A Programmable Interrupt Controller emulation.
/// Handles IRQ masking, priority, and End-of-Interrupt (EOI).
/// Master PIC: ports 0x20-0x21, Slave PIC: ports 0xA0-0xA1.
/// </summary>
public sealed class PicController
{
    // IRQ → INT vector mapping (master: IRQ 0-7 → INT 08h-0Fh, slave: IRQ 8-15 → INT 70h-77h)
    private byte _masterBase = 0x08;
    private byte _slaveBase = 0x70;

    // Interrupt Mask Register (1 = masked/disabled)
    private byte _masterIMR = 0xFF; // All masked initially
    private byte _slaveIMR = 0xFF;

    // In-Service Register (ISR) — which IRQ is currently being serviced
    private byte _masterISR;
    private byte _slaveISR;

    // Interrupt Request Register (IRR) — pending interrupts
    private byte _masterIRR;
    private byte _slaveIRR;

    // ICW state machine for initialization
    private int _masterInitStep;
    private int _slaveInitStep;

    /// <summary>Raised when an IRQ should trigger an INT on the CPU.</summary>
    public event Action<byte>? InterruptRequest;

    public PicController()
    {
    }

    /// <summary>Register port handlers on the I/O port bus.</summary>
    public void RegisterPorts(IOPortBus ports)
    {
        // Master PIC
        ports.Register(0x20, ReadMasterCommand, WriteMasterCommand);
        ports.Register(0x21, ReadMasterData, WriteMasterData);
        // Slave PIC
        ports.Register(0xA0, ReadSlaveCommand, WriteSlaveCommand);
        ports.Register(0xA1, ReadSlaveData, WriteSlaveData);
    }

    /// <summary>Raise an IRQ (0-15). IRQ 0-7 are master, 8-15 are slave.</summary>
    public void RaiseIRQ(int irq)
    {
        if (irq < 8)
        {
            _masterIRR |= (byte)(1 << irq);
        }
        else if (irq < 16)
        {
            _slaveIRR |= (byte)(1 << (irq - 8));
            _masterIRR |= (byte)(1 << 2); // Slave is on IRQ 2
        }
        ServicePendingInterrupts();
    }

    /// <summary>
    /// Send a non-specific End-of-Interrupt to the master PIC.
    /// Called by managed interrupt handlers (INT 08h, INT 09h) after servicing.
    /// </summary>
    public void SendEOI()
    {
        for (int i = 0; i < 8; i++)
        {
            if ((_masterISR & (1 << i)) != 0)
            {
                _masterISR &= (byte)~(1 << i);
                break;
            }
        }
    }

    /// <summary>Check and deliver pending interrupts to the CPU.</summary>
    public void ServicePendingInterrupts()
    {
        // Check master PIC for unmasked, un-serviced interrupts
        byte pending = (byte)(_masterIRR & ~_masterIMR & ~_masterISR);
        if (pending == 0) return;

        // Find highest-priority (lowest bit number)
        for (int i = 0; i < 8; i++)
        {
            if ((pending & (1 << i)) != 0)
            {
                if (i == 2) // Cascade — check slave
                {
                    byte slavePending = (byte)(_slaveIRR & ~_slaveIMR & ~_slaveISR);
                    if (slavePending == 0) continue;
                    for (int j = 0; j < 8; j++)
                    {
                        if ((slavePending & (1 << j)) != 0)
                        {
                            _slaveIRR &= (byte)~(1 << j);
                            _slaveISR |= (byte)(1 << j);
                            InterruptRequest?.Invoke((byte)(_slaveBase + j));
                            return;
                        }
                    }
                }
                else
                {
                    _masterIRR &= (byte)~(1 << i);
                    _masterISR |= (byte)(1 << i);
                    InterruptRequest?.Invoke((byte)(_masterBase + i));
                    return;
                }
            }
        }
    }

    // --- Master PIC port handlers ---

    private byte ReadMasterCommand(ushort port) => _masterISR;

    private void WriteMasterCommand(ushort port, byte value)
    {
        if ((value & 0x10) != 0) // ICW1
        {
            _masterInitStep = 1;
            _masterIMR = 0;
            _masterISR = 0;
            _masterIRR = 0;
        }
        else if ((value & 0x20) != 0) // EOI (non-specific)
        {
            // Clear highest-priority in-service bit
            for (int i = 0; i < 8; i++)
            {
                if ((_masterISR & (1 << i)) != 0)
                {
                    _masterISR &= (byte)~(1 << i);
                    break;
                }
            }
            ServicePendingInterrupts();
        }
        else if (value == 0x60) // Specific EOI for IRQ 0
        {
            _masterISR &= unchecked((byte)~1);
            ServicePendingInterrupts();
        }
        else if ((value & 0x60) == 0x60) // Specific EOI
        {
            int irq = value & 7;
            _masterISR &= (byte)~(1 << irq);
            ServicePendingInterrupts();
        }
    }

    private byte ReadMasterData(ushort port) => _masterIMR;

    private void WriteMasterData(ushort port, byte value)
    {
        switch (_masterInitStep)
        {
            case 1: // ICW2 — base vector
                _masterBase = value;
                _masterInitStep = 2;
                break;
            case 2: // ICW3 — cascade
                _masterInitStep = 3;
                break;
            case 3: // ICW4 — mode
                _masterInitStep = 0;
                _masterIMR = 0; // Unmask all after init
                break;
            default: // OCW1 — interrupt mask
                _masterIMR = value;
                break;
        }
    }

    // --- Slave PIC port handlers ---

    private byte ReadSlaveCommand(ushort port) => _slaveISR;

    private void WriteSlaveCommand(ushort port, byte value)
    {
        if ((value & 0x10) != 0) // ICW1
        {
            _slaveInitStep = 1;
            _slaveIMR = 0;
            _slaveISR = 0;
            _slaveIRR = 0;
        }
        else if ((value & 0x20) != 0) // EOI
        {
            for (int i = 0; i < 8; i++)
            {
                if ((_slaveISR & (1 << i)) != 0)
                {
                    _slaveISR &= (byte)~(1 << i);
                    break;
                }
            }
            // Also clear cascade bit on master
            _masterISR &= unchecked((byte)~(1 << 2));
            ServicePendingInterrupts();
        }
    }

    private byte ReadSlaveData(ushort port) => _slaveIMR;

    private void WriteSlaveData(ushort port, byte value)
    {
        switch (_slaveInitStep)
        {
            case 1: _slaveBase = value; _slaveInitStep = 2; break;
            case 2: _slaveInitStep = 3; break;
            case 3: _slaveInitStep = 0; _slaveIMR = 0; break;
            default: _slaveIMR = value; break;
        }
    }
}
