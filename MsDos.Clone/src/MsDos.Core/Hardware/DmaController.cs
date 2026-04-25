namespace MsDos.Core.Hardware;

/// <summary>
/// Intel 8237A DMA Controller emulation.
/// Provides 4 DMA channels for the IBM PC. Channel 2 is used by the floppy
/// disk controller for data transfers.
///
/// Port mapping (DMA controller 1 — 8-bit channels 0-3):
///   0x00-0x07 : Channel address/count registers (interleaved)
///   0x08      : Status / Command register
///   0x09      : Request register
///   0x0A      : Single channel mask register
///   0x0B      : Mode register
///   0x0C      : Clear flip-flop
///   0x0D      : Master clear (reset)
///   0x0E      : Clear mask register
///   0x0F      : Write all mask register bits
///   0x81-0x83 : Page registers for channels 1-3
///   0x87      : Page register for channel 0
/// </summary>
public sealed class DmaController
{
    /// <summary>Number of DMA channels.</summary>
    private const int ChannelCount = 4;

    // Per-channel state
    private readonly ushort[] _baseAddress = new ushort[ChannelCount];
    private readonly ushort[] _baseCount = new ushort[ChannelCount];
    private readonly ushort[] _currentAddress = new ushort[ChannelCount];
    private readonly ushort[] _currentCount = new ushort[ChannelCount];
    private readonly byte[] _pageRegisters = new byte[ChannelCount];
    private readonly byte[] _mode = new byte[ChannelCount];

    // Controller state
    private bool _flipFlop; // Low/high byte toggle for 16-bit register access
    private byte _commandRegister;
    private byte _statusRegister;
    private byte _maskRegister = 0x0F; // All channels masked by default
    private byte _requestRegister;

    /// <summary>Whether the controller is disabled via the command register.</summary>
    public bool IsDisabled => (_commandRegister & 0x04) != 0;

    /// <summary>
    /// Register I/O port handlers with the port bus.
    /// Replaces the existing stub registration in DosMachine.
    /// </summary>
    public void RegisterPorts(Memory.IOPortBus ports)
    {
        // Channel address and count registers (0x00-0x07)
        for (int i = 0; i < 4; i++)
        {
            int ch = i;
            int addrPort = ch * 2;
            int countPort = ch * 2 + 1;

            ports.Register((ushort)addrPort,
                port => ReadAddressRegister(ch),
                (port, val) => WriteAddressRegister(ch, val));

            ports.Register((ushort)countPort,
                port => ReadCountRegister(ch),
                (port, val) => WriteCountRegister(ch, val));
        }

        // Status / Command register (0x08)
        ports.Register(0x08,
            port => ReadStatus(),
            (port, val) => WriteCommand(val));

        // Request register (0x09)
        ports.Register(0x09, null, (port, val) => WriteRequest(val));

        // Single channel mask (0x0A)
        ports.Register(0x0A, null, (port, val) => WriteSingleMask(val));

        // Mode register (0x0B)
        ports.Register(0x0B, null, (port, val) => WriteMode(val));

        // Clear flip-flop (0x0C)
        ports.Register(0x0C, null, (port, val) => _flipFlop = false);

        // Master clear / reset (0x0D)
        ports.Register(0x0D, null, (port, val) => MasterClear());

        // Clear mask register (0x0E)
        ports.Register(0x0E, null, (port, val) => _maskRegister = 0x00);

        // Write all mask bits (0x0F)
        ports.Register(0x0F,
            port => _maskRegister,
            (port, val) => _maskRegister = (byte)(val & 0x0F));

        // Page registers
        ports.Register(0x87, port => _pageRegisters[0], (port, val) => _pageRegisters[0] = val); // Ch 0
        ports.Register(0x83, port => _pageRegisters[1], (port, val) => _pageRegisters[1] = val); // Ch 1
        ports.Register(0x81, port => _pageRegisters[2], (port, val) => _pageRegisters[2] = val); // Ch 2 (floppy)
        ports.Register(0x82, port => _pageRegisters[3], (port, val) => _pageRegisters[3] = val); // Ch 3
    }

    /// <summary>
    /// Get the current physical address and byte count for a DMA channel transfer.
    /// Used by the FDC to determine where to read/write data in system memory.
    /// </summary>
    /// <param name="channel">DMA channel (0-3).</param>
    /// <returns>Physical 20-bit address and transfer byte count.</returns>
    public (int physicalAddress, int byteCount) GetTransferParameters(int channel)
    {
        int physAddr = (_pageRegisters[channel] << 16) | _currentAddress[channel];
        int count = _currentCount[channel] + 1; // Count register is N-1
        return (physAddr, count);
    }

    /// <summary>
    /// Signal that a DMA transfer on the given channel is complete.
    /// Sets the Terminal Count (TC) bit in the status register.
    /// </summary>
    public void SignalTransferComplete(int channel)
    {
        _statusRegister |= (byte)(1 << channel); // TC bit
        _statusRegister &= (byte)~(1 << (channel + 4)); // Clear request bit
    }

    /// <summary>
    /// Update the current address and count after a transfer.
    /// </summary>
    public void UpdateAfterTransfer(int channel, int bytesTransferred)
    {
        _currentAddress[channel] = (ushort)(_currentAddress[channel] + bytesTransferred);
        _currentCount[channel] = (ushort)(_currentCount[channel] - bytesTransferred);
    }

    /// <summary>Check if a channel is masked (disabled).</summary>
    public bool IsChannelMasked(int channel) => (_maskRegister & (1 << channel)) != 0;

    /// <summary>Get the transfer mode for a channel.</summary>
    public DmaTransferMode GetTransferMode(int channel) =>
        (DmaTransferMode)((_mode[channel] >> 2) & 0x03);

    /// <summary>Get the transfer direction for a channel.</summary>
    public DmaTransferDirection GetDirection(int channel) =>
        (DmaTransferDirection)((_mode[channel] >> 2) & 0x03);

    /// <summary>Whether auto-init is enabled for the channel.</summary>
    public bool IsAutoInit(int channel) => (_mode[channel] & 0x10) != 0;

    // --- Private helpers ---

    private byte ReadAddressRegister(int channel)
    {
        byte val;
        if (!_flipFlop)
            val = (byte)(_currentAddress[channel] & 0xFF);
        else
            val = (byte)((_currentAddress[channel] >> 8) & 0xFF);
        _flipFlop = !_flipFlop;
        return val;
    }

    private void WriteAddressRegister(int channel, byte value)
    {
        if (!_flipFlop)
        {
            _baseAddress[channel] = (ushort)((_baseAddress[channel] & 0xFF00) | value);
            _currentAddress[channel] = (ushort)((_currentAddress[channel] & 0xFF00) | value);
        }
        else
        {
            _baseAddress[channel] = (ushort)((_baseAddress[channel] & 0x00FF) | (value << 8));
            _currentAddress[channel] = (ushort)((_currentAddress[channel] & 0x00FF) | (value << 8));
        }
        _flipFlop = !_flipFlop;
    }

    private byte ReadCountRegister(int channel)
    {
        byte val;
        if (!_flipFlop)
            val = (byte)(_currentCount[channel] & 0xFF);
        else
            val = (byte)((_currentCount[channel] >> 8) & 0xFF);
        _flipFlop = !_flipFlop;
        return val;
    }

    private void WriteCountRegister(int channel, byte value)
    {
        if (!_flipFlop)
        {
            _baseCount[channel] = (ushort)((_baseCount[channel] & 0xFF00) | value);
            _currentCount[channel] = (ushort)((_currentCount[channel] & 0xFF00) | value);
        }
        else
        {
            _baseCount[channel] = (ushort)((_baseCount[channel] & 0x00FF) | (value << 8));
            _currentCount[channel] = (ushort)((_currentCount[channel] & 0x00FF) | (value << 8));
        }
        _flipFlop = !_flipFlop;
    }

    private byte ReadStatus()
    {
        byte val = _statusRegister;
        _statusRegister &= 0xF0; // Reading clears TC bits (bits 0-3)
        return val;
    }

    private void WriteCommand(byte value)
    {
        _commandRegister = value;
    }

    private void WriteRequest(byte value)
    {
        int channel = value & 0x03;
        if ((value & 0x04) != 0)
            _requestRegister |= (byte)(1 << channel);
        else
            _requestRegister &= (byte)~(1 << channel);
    }

    private void WriteSingleMask(byte value)
    {
        int channel = value & 0x03;
        if ((value & 0x04) != 0)
            _maskRegister |= (byte)(1 << channel);
        else
            _maskRegister &= (byte)~(1 << channel);
    }

    private void WriteMode(byte value)
    {
        int channel = value & 0x03;
        _mode[channel] = value;
    }

    private void MasterClear()
    {
        _flipFlop = false;
        _commandRegister = 0;
        _statusRegister = 0;
        _maskRegister = 0x0F; // All channels masked
        _requestRegister = 0;
    }
}

/// <summary>DMA transfer mode.</summary>
public enum DmaTransferMode
{
    Verify = 0,
    Write = 1,   // I/O → Memory
    Read = 2,    // Memory → I/O
    Invalid = 3
}

/// <summary>DMA transfer direction.</summary>
public enum DmaTransferDirection
{
    Verify = 0,
    IoToMemory = 1,
    MemoryToIo = 2,
    Invalid = 3
}
