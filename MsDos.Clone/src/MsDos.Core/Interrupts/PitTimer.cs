using MsDos.Core.Memory;

namespace MsDos.Core.Interrupts;

/// <summary>
/// Intel 8253/8254 Programmable Interval Timer emulation.
/// Channel 0: System timer (IRQ 0, INT 08h) at 18.2 Hz
/// Channel 1: DRAM refresh (unused)
/// Channel 2: PC Speaker tone generation
/// Ports: 0x40-0x42 (channels), 0x43 (control word)
/// </summary>
public sealed class PitTimer
{
    // PIT input clock: ~1.193182 MHz
    public const double ClockFrequency = 1193182.0;

    private readonly ushort[] _reloadValue = new ushort[3];
    private readonly ushort[] _counter = new ushort[3];
    private readonly byte[] _mode = new byte[3];
    private readonly bool[] _latched = new bool[3];
    private readonly ushort[] _latchValue = new ushort[3];
    private readonly byte[] _accessMode = new byte[3]; // 1=lobyte, 2=hibyte, 3=lo/hi
    private readonly bool[] _readHi = new bool[3]; // for lo/hi access: next read is high byte
    private readonly bool[] _writeHi = new bool[3]; // for lo/hi access: next write is high byte

    private long _cycleAccumulator;
    private const int CyclesPerTick = 65536; // Default: 18.2 Hz (1193182 / 65536 ≈ 18.2)

    /// <summary>Raised when Channel 0 counts to zero (IRQ 0 tick).</summary>
    public event Action? TimerTick;

    /// <summary>Raised when Channel 2 reload value changes (for PC speaker).</summary>
    public event Action<ushort>? Channel2OutputChanged;

    /// <summary>Whether Channel 2 output is high (speaker gate).</summary>
    public bool Channel2Output { get; private set; }

    public PitTimer()
    {
        // Default: all channels loaded with 0 (which means 65536)
        for (int i = 0; i < 3; i++)
        {
            _reloadValue[i] = 0; // 0 = 65536
            _counter[i] = 0;
            _mode[i] = 3; // Mode 3 (square wave) is the default for channel 0
            _accessMode[i] = 3; // lo/hi access
        }
    }

    /// <summary>Register port handlers on the I/O port bus.</summary>
    public void RegisterPorts(IOPortBus ports)
    {
        ports.Register(0x40, port => ReadChannel(0), (port, val) => WriteChannel(0, val));
        ports.Register(0x41, port => ReadChannel(1), (port, val) => WriteChannel(1, val));
        ports.Register(0x42, port => ReadChannel(2), (port, val) => WriteChannel(2, val));
        ports.Register(0x43, null, WriteControl);
    }

    /// <summary>
    /// Advance the PIT by the given number of CPU cycles.
    /// At ~4.77 MHz, one CPU cycle ≈ 1 PIT tick / 4.
    /// </summary>
    public void Tick(int cpuCycles)
    {
        // Each CPU cycle is roughly 4 PIT input clock cycles at 4.77 MHz
        // But we simplify: advance each channel's counter
        _cycleAccumulator += cpuCycles;

        // Channel 0 countdown
        int reload = _reloadValue[0] == 0 ? 65536 : _reloadValue[0];
        while (_cycleAccumulator >= reload)
        {
            _cycleAccumulator -= reload;
            TimerTick?.Invoke();
        }
    }

    /// <summary>
    /// Simulate a fixed number of timer ticks (for INT 08h generation).
    /// Call this once per emulator frame with the elapsed CPU cycles.
    /// Returns number of ticks that fired.
    /// </summary>
    public int AdvanceTicks(int cpuCycles)
    {
        int ticks = 0;
        _cycleAccumulator += cpuCycles;
        int reload = _reloadValue[0] == 0 ? 65536 : _reloadValue[0];

        while (_cycleAccumulator >= reload)
        {
            _cycleAccumulator -= reload;
            ticks++;
        }
        return ticks;
    }

    /// <summary>Read the current counter value of a channel (used by programs reading port 0x40).</summary>
    public ushort GetCounter(int channel)
    {
        // Return a pseudo-decrementing counter based on system ticks
        // Real programs read this for timing / random seeds
        return _counter[channel];
    }

    /// <summary>Decrement counters by a given amount (called from CPU loop).</summary>
    public void DecrementCounters(int amount)
    {
        for (int ch = 0; ch < 3; ch++)
        {
            int val = _counter[ch] == 0 ? 65536 : _counter[ch];
            val -= amount;
            if (val <= 0)
            {
                int reload = _reloadValue[ch] == 0 ? 65536 : _reloadValue[ch];
                val = reload + (val % reload);
                if (ch == 0) TimerTick?.Invoke();
            }
            _counter[ch] = (ushort)(val & 0xFFFF);
        }
    }

    private byte ReadChannel(int channel)
    {
        ushort value;
        if (_latched[channel])
        {
            value = _latchValue[channel];
        }
        else
        {
            // Free-running read — return current counter approximation
            value = _counter[channel];
        }

        byte result;
        switch (_accessMode[channel])
        {
            case 1: // Lo byte only
                result = (byte)(value & 0xFF);
                _latched[channel] = false;
                break;
            case 2: // Hi byte only
                result = (byte)((value >> 8) & 0xFF);
                _latched[channel] = false;
                break;
            case 3: // Lo/Hi
                if (!_readHi[channel])
                {
                    result = (byte)(value & 0xFF);
                    _readHi[channel] = true;
                }
                else
                {
                    result = (byte)((value >> 8) & 0xFF);
                    _readHi[channel] = false;
                    _latched[channel] = false;
                }
                break;
            default:
                result = (byte)(value & 0xFF);
                break;
        }
        return result;
    }

    private void WriteChannel(int channel, byte value)
    {
        switch (_accessMode[channel])
        {
            case 1: // Lo byte only
                _reloadValue[channel] = (ushort)((_reloadValue[channel] & 0xFF00) | value);
                _counter[channel] = _reloadValue[channel];
                if (channel == 2) Channel2OutputChanged?.Invoke(_reloadValue[2]);
                break;
            case 2: // Hi byte only
                _reloadValue[channel] = (ushort)((_reloadValue[channel] & 0x00FF) | (value << 8));
                _counter[channel] = _reloadValue[channel];
                if (channel == 2) Channel2OutputChanged?.Invoke(_reloadValue[2]);
                break;
            case 3: // Lo/Hi
                if (!_writeHi[channel])
                {
                    _reloadValue[channel] = (ushort)((_reloadValue[channel] & 0xFF00) | value);
                    _writeHi[channel] = true;
                }
                else
                {
                    _reloadValue[channel] = (ushort)((_reloadValue[channel] & 0x00FF) | (value << 8));
                    _counter[channel] = _reloadValue[channel];
                    _writeHi[channel] = false;
                    if (channel == 2) Channel2OutputChanged?.Invoke(_reloadValue[2]);
                }
                break;
        }
    }

    private void WriteControl(ushort port, byte value)
    {
        int channel = (value >> 6) & 3;
        if (channel == 3) return; // Read-back command (not impl)

        int access = (value >> 4) & 3;
        int mode = (value >> 1) & 7;

        if (access == 0)
        {
            // Latch command: latch current counter for reading
            _latchValue[channel] = _counter[channel];
            _latched[channel] = true;
            _readHi[channel] = false;
            return;
        }

        _accessMode[channel] = (byte)access;
        _mode[channel] = (byte)mode;
        _readHi[channel] = false;
        _writeHi[channel] = false;
        _latched[channel] = false;
    }
}
