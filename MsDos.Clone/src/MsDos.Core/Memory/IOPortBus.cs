namespace MsDos.Core.Memory;

/// <summary>
/// I/O port address space (64K ports, 0x0000-0xFFFF).
/// Provides read/write access to emulated hardware ports.
/// Devices register handlers for their port ranges.
/// </summary>
public sealed class IOPortBus
{
    /// <summary>Handler for reading a byte from a port.</summary>
    public delegate byte PortReadHandler(ushort port);
    /// <summary>Handler for writing a byte to a port.</summary>
    public delegate void PortWriteHandler(ushort port, byte value);

    private readonly SortedList<ushort, PortReadHandler> _readers = new();
    private readonly SortedList<ushort, PortWriteHandler> _writers = new();
    private readonly Dictionary<ushort, ushort> _portRangeEnd = new(); // start → end (inclusive)

    /// <summary>
    /// Register a handler for a range of I/O ports.
    /// </summary>
    public void Register(ushort startPort, ushort endPort, PortReadHandler? reader, PortWriteHandler? writer)
    {
        for (ushort p = startPort; p <= endPort; p++)
        {
            if (reader != null) _readers[p] = reader;
            if (writer != null) _writers[p] = writer;
        }
    }

    /// <summary>Register a handler for a single port.</summary>
    public void Register(ushort port, PortReadHandler? reader, PortWriteHandler? writer)
    {
        if (reader != null) _readers[port] = reader;
        if (writer != null) _writers[port] = writer;
    }

    /// <summary>Read a byte from an I/O port.</summary>
    public byte ReadByte(ushort port)
    {
        if (_readers.TryGetValue(port, out var handler))
            return handler(port);
        return 0xFF; // Unconnected ports read as 0xFF on real hardware
    }

    /// <summary>Read a word (little-endian) from two consecutive I/O ports.</summary>
    public ushort ReadWord(ushort port)
    {
        byte lo = ReadByte(port);
        byte hi = ReadByte((ushort)(port + 1));
        return (ushort)(lo | (hi << 8));
    }

    /// <summary>Write a byte to an I/O port.</summary>
    public void WriteByte(ushort port, byte value)
    {
        if (_writers.TryGetValue(port, out var handler))
            handler(port, value);
        // Writes to unregistered ports are silently ignored
    }

    /// <summary>Write a word (little-endian) to two consecutive I/O ports.</summary>
    public void WriteWord(ushort port, ushort value)
    {
        WriteByte(port, (byte)(value & 0xFF));
        WriteByte((ushort)(port + 1), (byte)((value >> 8) & 0xFF));
    }
}
