namespace MsDos.Core.Memory;

/// <summary>
/// Represents a snapshot of a memory region for debugging purposes.
/// Used to compare memory states before and after instruction execution.
/// </summary>
public sealed class MemorySnapshot
{
    /// <summary>Starting physical address of this snapshot.</summary>
    public uint StartAddress { get; }

    /// <summary>Length of the snapshot in bytes.</summary>
    public int Length => Data.Length;

    /// <summary>Raw memory data at the time of capture.</summary>
    public byte[] Data { get; }

    /// <summary>Timestamp when this snapshot was taken.</summary>
    public DateTime Timestamp { get; }

    /// <summary>CPU register state at the time of capture (CS:IP, AX, BX, CX, DX, etc.).</summary>
    public RegisterSnapshot Registers { get; }

    public MemorySnapshot(uint startAddress, byte[] data, RegisterSnapshot registers)
    {
        StartAddress = startAddress;
        Data = data;
        Timestamp = DateTime.Now;
        Registers = registers;
    }

    /// <summary>
    /// Compare this snapshot against another and return a diff of changed bytes.
    /// </summary>
    public MemoryDiff CompareTo(MemorySnapshot other)
    {
        var changes = new List<MemoryChange>();

        int minLen = Math.Min(Data.Length, other.Data.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (Data[i] != other.Data[i])
            {
                changes.Add(new MemoryChange
                {
                    Address = StartAddress + (uint)i,
                    OldValue = Data[i],
                    NewValue = other.Data[i]
                });
            }
        }

        return new MemoryDiff
        {
            Before = this,
            After = other,
            Changes = changes,
            RegistersBefore = Registers,
            RegistersAfter = other.Registers
        };
    }
}

/// <summary>
/// Represents the CPU register state at a point in time.
/// </summary>
public readonly struct RegisterSnapshot
{
    public ushort AX { get; init; }
    public ushort BX { get; init; }
    public ushort CX { get; init; }
    public ushort DX { get; init; }
    public ushort SI { get; init; }
    public ushort DI { get; init; }
    public ushort SP { get; init; }
    public ushort BP { get; init; }
    public ushort CS { get; init; }
    public ushort DS { get; init; }
    public ushort ES { get; init; }
    public ushort SS { get; init; }
    public ushort IP { get; init; }
    public ushort Flags { get; init; }
}

/// <summary>
/// A single changed byte between two memory snapshots.
/// </summary>
public readonly struct MemoryChange
{
    /// <summary>Physical address of the change.</summary>
    public uint Address { get; init; }

    /// <summary>Value before the change.</summary>
    public byte OldValue { get; init; }

    /// <summary>Value after the change.</summary>
    public byte NewValue { get; init; }
}

/// <summary>
/// Represents the difference between two memory snapshots.
/// Used for git-diff-style display of memory state changes.
/// </summary>
public sealed class MemoryDiff
{
    /// <summary>The snapshot before execution.</summary>
    public required MemorySnapshot Before { get; init; }

    /// <summary>The snapshot after execution.</summary>
    public required MemorySnapshot After { get; init; }

    /// <summary>List of individual byte changes.</summary>
    public required IReadOnlyList<MemoryChange> Changes { get; init; }

    /// <summary>Register state before execution.</summary>
    public RegisterSnapshot RegistersBefore { get; init; }

    /// <summary>Register state after execution.</summary>
    public RegisterSnapshot RegistersAfter { get; init; }

    /// <summary>Whether any memory or register changes occurred.</summary>
    public bool HasChanges => Changes.Count > 0 || RegistersChanged;

    /// <summary>Whether any registers changed.</summary>
    public bool RegistersChanged =>
        RegistersBefore.AX != RegistersAfter.AX ||
        RegistersBefore.BX != RegistersAfter.BX ||
        RegistersBefore.CX != RegistersAfter.CX ||
        RegistersBefore.DX != RegistersAfter.DX ||
        RegistersBefore.SI != RegistersAfter.SI ||
        RegistersBefore.DI != RegistersAfter.DI ||
        RegistersBefore.SP != RegistersAfter.SP ||
        RegistersBefore.BP != RegistersAfter.BP ||
        RegistersBefore.CS != RegistersAfter.CS ||
        RegistersBefore.DS != RegistersAfter.DS ||
        RegistersBefore.ES != RegistersAfter.ES ||
        RegistersBefore.SS != RegistersAfter.SS ||
        RegistersBefore.IP != RegistersAfter.IP ||
        RegistersBefore.Flags != RegistersAfter.Flags;
}

/// <summary>
/// Extension methods for MemoryBus to support snapshots.
/// </summary>
public static class MemoryBusSnapshotExtensions
{
    /// <summary>
    /// Capture a snapshot of a memory region.
    /// </summary>
    public static MemorySnapshot CaptureSnapshot(
        this MemoryBus memory,
        uint startAddress,
        int length,
        Cpu.Registers regs)
    {
        var data = memory.ReadBlock(startAddress, length);
        var regSnap = new RegisterSnapshot
        {
            AX = regs.AX, BX = regs.BX, CX = regs.CX, DX = regs.DX,
            SI = regs.SI, DI = regs.DI, SP = regs.SP, BP = regs.BP,
            CS = regs.CS, DS = regs.DS, ES = regs.ES, SS = regs.SS,
            IP = regs.IP, Flags = (ushort)regs.Flags
        };
        return new MemorySnapshot(startAddress, data, regSnap);
    }
}
