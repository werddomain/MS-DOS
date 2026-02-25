using MsDos.Core.Cpu;
using MsDos.Core.Memory;

namespace MsDos.Tests.Intel8088;

/// <summary>
/// Executes a single 8088 test case against the emulator CPU.
/// Sets up initial state, runs one instruction step, validates final state.
///
/// The test runner follows the 8088 test suite convention:
/// - Sets all registers and RAM from the initial state
/// - Executes the instruction (one or more Step() calls to handle prefixes)
/// - Compares only registers/RAM that appear in the final state (delta-based)
/// - Applies flag masks to ignore undefined flag bits
/// </summary>
public sealed class TestRunner
{
    private readonly MemoryBus _mem;
    private readonly Cpu8086 _cpu;
    private readonly IOPortBus _ports;

    // Track whether an INT 0 (divide exception) was triggered
    private bool _divideExceptionTriggered;
    private bool _interruptTriggered;
    private byte _lastInterruptVector;

    public TestRunner()
    {
        _mem = new MemoryBus();
        _cpu = new Cpu8086(_mem);
        _ports = new IOPortBus();
        _cpu.SetIOPortBus(_ports);

        // All IN instructions should return 0xFF (per test suite spec).
        // IOPortBus.ReadByte already returns 0xFF for unregistered ports,
        // so no explicit registration is needed.

        // Track interrupts for divide exception testing
        _cpu.InterruptTriggered += vector =>
        {
            _interruptTriggered = true;
            _lastInterruptVector = vector;
            if (vector == 0)
                _divideExceptionTriggered = true;
        };
    }

    /// <summary>
    /// Run a single test case and return the result.
    /// </summary>
    public TestResult Run(CpuTestCase test, ushort flagsMask = 0xFFFF)
    {
        try
        {
            // Reset state
            _mem.Clear();
            _divideExceptionTriggered = false;
            _interruptTriggered = false;
            _lastInterruptVector = 0;

            // 1) Set up initial register state
            SetupRegisters(test.Initial);

            // 2) Set up initial RAM
            foreach (var ramEntry in test.Initial.Ram)
            {
                if (ramEntry.Count >= 2)
                {
                    uint addr = (uint)ramEntry[0];
                    byte val = (byte)ramEntry[1];
                    _mem.WriteByte(addr, val);
                }
            }

            // 3) IVT entries are already set from the test's initial RAM.
            //    Do NOT overwrite them — INT tests depend on correct IVT values.

            // 4) Fill remaining instruction space with NOPs (0x90)
            // The test suite states bytes after the instruction are 0x90.
            // They're already in the RAM entries, but ensure a few past the instruction.
            uint instrPhysAddr = (uint)(_cpu.Regs.CS << 4) + _cpu.Regs.IP;
            // Build a HashSet of initial RAM addresses for fast lookup
            var initialAddrs = new HashSet<uint>();
            foreach (var r in test.Initial.Ram)
            {
                if (r.Count >= 2) initialAddrs.Add((uint)r[0]);
            }
            for (int i = test.Bytes.Count; i < test.Bytes.Count + 16; i++)
            {
                uint addr = (instrPhysAddr + (uint)i) & 0xFFFFF;
                if (!initialAddrs.Contains(addr))
                    _mem.WriteByte(addr, 0x90);
            }

            // 5) Execute the instruction
            // The CPU will handle prefixes internally (segment overrides, REP, etc.)
            _cpu.Step();

            // 6) Validate final state
            var errors = new List<string>();

            // Check registers that changed (only those in the final state)
            ValidateRegisters(test, flagsMask, errors);

            // Check RAM that changed
            ValidateRam(test, errors);

            // Build actual register snapshot for logging on failure
            string? actualRegs = errors.Count > 0
                ? $"AX={_cpu.Regs.AX:X4} BX={_cpu.Regs.BX:X4} CX={_cpu.Regs.CX:X4} DX={_cpu.Regs.DX:X4} " +
                  $"SP={_cpu.Regs.SP:X4} BP={_cpu.Regs.BP:X4} SI={_cpu.Regs.SI:X4} DI={_cpu.Regs.DI:X4} " +
                  $"CS={_cpu.Regs.CS:X4} DS={_cpu.Regs.DS:X4} ES={_cpu.Regs.ES:X4} SS={_cpu.Regs.SS:X4} " +
                  $"IP={_cpu.Regs.IP:X4} FL={(ushort)_cpu.Regs.Flags:X4}"
                : null;

            return new TestResult
            {
                Success = errors.Count == 0,
                Errors = errors,
                TestName = test.Name,
                TestIndex = test.Idx,
                Hash = test.Hash,
                ActualRegisters = actualRegs
            };
        }
        catch (Exception ex)
        {
            return new TestResult
            {
                Success = false,
                Errors = new List<string> { $"Exception: {ex.Message}" },
                TestName = test.Name,
                TestIndex = test.Idx,
                Hash = test.Hash
            };
        }
    }

    private void SetupRegisters(CpuState state)
    {
        var regs = state.Regs;
        if (regs.TryGetValue("ax", out int ax)) _cpu.Regs.AX = (ushort)ax;
        if (regs.TryGetValue("bx", out int bx)) _cpu.Regs.BX = (ushort)bx;
        if (regs.TryGetValue("cx", out int cx)) _cpu.Regs.CX = (ushort)cx;
        if (regs.TryGetValue("dx", out int dx)) _cpu.Regs.DX = (ushort)dx;
        if (regs.TryGetValue("cs", out int cs)) _cpu.Regs.CS = (ushort)cs;
        if (regs.TryGetValue("ss", out int ss)) _cpu.Regs.SS = (ushort)ss;
        if (regs.TryGetValue("ds", out int ds)) _cpu.Regs.DS = (ushort)ds;
        if (regs.TryGetValue("es", out int es)) _cpu.Regs.ES = (ushort)es;
        if (regs.TryGetValue("sp", out int sp)) _cpu.Regs.SP = (ushort)sp;
        if (regs.TryGetValue("bp", out int bp)) _cpu.Regs.BP = (ushort)bp;
        if (regs.TryGetValue("si", out int si)) _cpu.Regs.SI = (ushort)si;
        if (regs.TryGetValue("di", out int di)) _cpu.Regs.DI = (ushort)di;
        if (regs.TryGetValue("ip", out int ip)) _cpu.Regs.IP = (ushort)ip;
        if (regs.TryGetValue("flags", out int flags)) _cpu.Regs.Flags = (CpuFlags)(ushort)flags;
    }

    private void ValidateRegisters(CpuTestCase test, ushort flagsMask, List<string> errors)
    {
        var finalRegs = test.Final.Regs;
        var initialRegs = test.Initial.Regs;

        // Helper to check a register
        void CheckReg(string name, ushort actual, ushort? expected)
        {
            if (expected.HasValue && actual != expected.Value)
            {
                errors.Add($"Register {name}: expected 0x{expected.Value:X4}, got 0x{actual:X4}");
            }
        }

        // For each register in the final state, verify it matches
        if (finalRegs.TryGetValue("ax", out int ax)) CheckReg("AX", _cpu.Regs.AX, (ushort)ax);
        if (finalRegs.TryGetValue("bx", out int bx)) CheckReg("BX", _cpu.Regs.BX, (ushort)bx);
        if (finalRegs.TryGetValue("cx", out int cx)) CheckReg("CX", _cpu.Regs.CX, (ushort)cx);
        if (finalRegs.TryGetValue("dx", out int dx)) CheckReg("DX", _cpu.Regs.DX, (ushort)dx);
        if (finalRegs.TryGetValue("cs", out int cs)) CheckReg("CS", _cpu.Regs.CS, (ushort)cs);
        if (finalRegs.TryGetValue("ss", out int ss)) CheckReg("SS", _cpu.Regs.SS, (ushort)ss);
        if (finalRegs.TryGetValue("ds", out int ds)) CheckReg("DS", _cpu.Regs.DS, (ushort)ds);
        if (finalRegs.TryGetValue("es", out int es)) CheckReg("ES", _cpu.Regs.ES, (ushort)es);
        if (finalRegs.TryGetValue("sp", out int sp)) CheckReg("SP", _cpu.Regs.SP, (ushort)sp);
        if (finalRegs.TryGetValue("bp", out int bp)) CheckReg("BP", _cpu.Regs.BP, (ushort)bp);
        if (finalRegs.TryGetValue("si", out int si)) CheckReg("SI", _cpu.Regs.SI, (ushort)si);
        if (finalRegs.TryGetValue("di", out int di)) CheckReg("DI", _cpu.Regs.DI, (ushort)di);
        if (finalRegs.TryGetValue("ip", out int ip)) CheckReg("IP", _cpu.Regs.IP, (ushort)ip);

        // Flags comparison with mask
        if (finalRegs.TryGetValue("flags", out int expectedFlags))
        {
            ushort actualFlags = (ushort)_cpu.Regs.Flags;
            ushort maskedActual = (ushort)(actualFlags & flagsMask);
            ushort maskedExpected = (ushort)((ushort)expectedFlags & flagsMask);

            if (maskedActual != maskedExpected)
            {
                errors.Add($"Flags: expected 0x{maskedExpected:X4}, got 0x{maskedActual:X4} " +
                           $"(raw: expected=0x{(ushort)expectedFlags:X4}, actual=0x{actualFlags:X4}, mask=0x{flagsMask:X4}) " +
                           $"[{FormatFlagsDiff((ushort)expectedFlags, actualFlags, flagsMask)}]");
            }
        }

        // Registers NOT in the final state should be unchanged from initial
        // (The test format only includes changed registers in final)
        void CheckUnchanged(string name, ushort actual, Dictionary<string, int> initial, Dictionary<string, int> final_)
        {
            if (!final_.ContainsKey(name) && initial.TryGetValue(name, out int initVal))
            {
                if (actual != (ushort)initVal)
                {
                    errors.Add($"Register {name.ToUpper()}: should be unchanged at 0x{(ushort)initVal:X4}, but is 0x{actual:X4}");
                }
            }
        }

        CheckUnchanged("ax", _cpu.Regs.AX, initialRegs, finalRegs);
        CheckUnchanged("bx", _cpu.Regs.BX, initialRegs, finalRegs);
        CheckUnchanged("cx", _cpu.Regs.CX, initialRegs, finalRegs);
        CheckUnchanged("dx", _cpu.Regs.DX, initialRegs, finalRegs);
        CheckUnchanged("cs", _cpu.Regs.CS, initialRegs, finalRegs);
        CheckUnchanged("ss", _cpu.Regs.SS, initialRegs, finalRegs);
        CheckUnchanged("ds", _cpu.Regs.DS, initialRegs, finalRegs);
        CheckUnchanged("es", _cpu.Regs.ES, initialRegs, finalRegs);
        CheckUnchanged("sp", _cpu.Regs.SP, initialRegs, finalRegs);
        CheckUnchanged("bp", _cpu.Regs.BP, initialRegs, finalRegs);
        CheckUnchanged("si", _cpu.Regs.SI, initialRegs, finalRegs);
        CheckUnchanged("di", _cpu.Regs.DI, initialRegs, finalRegs);
        // Don't check IP unchanged - it always advances
    }

    private void ValidateRam(CpuTestCase test, List<string> errors)
    {
        foreach (var ramEntry in test.Final.Ram)
        {
            if (ramEntry.Count < 2) continue;
            uint addr = (uint)ramEntry[0];
            byte expected = (byte)ramEntry[1];
            byte actual = _mem.ReadByte(addr);
            if (actual != expected)
            {
                errors.Add($"RAM [0x{addr:X5}]: expected 0x{expected:X2}, got 0x{actual:X2}");
            }
        }
    }

    private static string FormatFlagsDiff(ushort expected, ushort actual, ushort mask)
    {
        var parts = new List<string>();
        string[] flagNames = { "CF", "1", "PF", "3", "AF", "5", "ZF", "SF", "TF", "IF", "DF", "OF" };

        for (int i = 0; i < 12; i++)
        {
            ushort bit = (ushort)(1 << i);
            if ((mask & bit) == 0) continue; // Skip masked (undefined) flags

            bool exp = (expected & bit) != 0;
            bool act = (actual & bit) != 0;
            if (exp != act)
            {
                parts.Add($"{flagNames[i]}:{(exp ? "1" : "0")}→{(act ? "1" : "0")}");
            }
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "no diff";
    }
}

public sealed class TestResult
{
    public bool Success { get; set; }
    public List<string> Errors { get; set; } = new();
    public string TestName { get; set; } = "";
    public int TestIndex { get; set; }
    public string? Hash { get; set; }

    /// <summary>Actual register state after execution (for logging).</summary>
    public string? ActualRegisters { get; set; }

    public override string ToString()
    {
        if (Success) return $"PASS: [{TestIndex}] {TestName}";
        var sb = $"FAIL: [{TestIndex}] {TestName}\n  " + string.Join("\n  ", Errors);
        if (ActualRegisters != null)
            sb += $"\n  Actual regs: {ActualRegisters}";
        return sb;
    }
}
