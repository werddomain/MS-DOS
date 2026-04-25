namespace MsDos.Core.Cpu;

/// <summary>
/// 8086 CPU flags register bits.
/// </summary>
[Flags]
public enum CpuFlags : ushort
{
    None      = 0,
    Carry     = 1 << 0,   // CF
    Parity    = 1 << 2,   // PF
    AuxCarry  = 1 << 4,   // AF
    Zero      = 1 << 6,   // ZF
    Sign      = 1 << 7,   // SF
    Trap      = 1 << 8,   // TF
    Interrupt = 1 << 9,   // IF
    Direction = 1 << 10,  // DF
    Overflow  = 1 << 11,  // OF
}
