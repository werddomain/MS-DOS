using System.Diagnostics;
using Xunit.Abstractions;

namespace MsDos.Tests.Intel8088;

/// <summary>
/// Runs the 8088 single-step test suite (V2) against the emulator.
///
/// Each test sets initial CPU + RAM state, executes one instruction,
/// and verifies the final CPU + RAM state matches the hardware-captured
/// results from a real AMD D8088 CPU.
///
/// Test data lives in 8088-tests/v2/*.json.gz — each file has up to
/// 10,000 test cases per opcode. We run a configurable subset per opcode
/// to balance thoroughness with test execution time.
///
/// Flag masks from metadata.json are applied to ignore undefined flags
/// (e.g., AF after AND/OR/XOR, OF after DAA/DAS, etc.).
/// </summary>
public class Intel8088Tests
{
    /// <summary>
    /// Number of test cases to run per opcode for standard tests.
    /// Set to a higher value (e.g., 10000) for full validation.
    /// </summary>
    private const int TestsPerOpcode = 100;

    private readonly ITestOutputHelper _output;

    public Intel8088Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Run all test cases for a given opcode file, returning failures.
    /// </summary>
    private void RunOpcodeTests(string opcodeFile, int? regField = null, int maxTests = TestsPerOpcode)
    {
        if (!TestLoader.TestFileExists(opcodeFile))
        {
            _output.WriteLine($"[SKIP] Test file not found for opcode {opcodeFile}");
            return;
        }

        // --- Phase 1: Load tests (timed) ---
        var sw = Stopwatch.StartNew();
        var tests = TestLoader.LoadTests(opcodeFile, maxTests);
        sw.Stop();
        _output.WriteLine($"[LOAD] Opcode {opcodeFile}: loaded {tests.Count} tests in {sw.ElapsedMilliseconds} ms");

        // Determine the base opcode hex for flag mask lookup
        string opcodeHex = opcodeFile.Contains('.')
            ? opcodeFile.Split('.')[0]
            : opcodeFile;

        var runner = new TestRunner();

        var failures = new List<string>();
        int passCount = 0;

        // --- Phase 2: Execute tests (timed individually) ---
        var totalSw = Stopwatch.StartNew();
        foreach (var test in tests)
        {
            // Determine reg field from the instruction bytes if needed
            int? effectiveReg = regField;
            if (effectiveReg == null && opcodeFile.Contains('.'))
            {
                if (int.TryParse(opcodeFile.Split('.')[1], out int r))
                    effectiveReg = r;
            }

            ushort flagsMask = TestLoader.GetFlagsMask(opcodeHex, effectiveReg);

            string bytesHex = string.Join(" ", test.Bytes.Select(b => $"{b:X2}"));
            string initialRegs = FormatRegsCompact(test.Initial.Regs);

            // Run the test case
            var stepSw = Stopwatch.StartNew();
            TestResult result;
            try
            {
                result = runner.Run(test, flagsMask);
            }
            catch (Exception ex)
            {
                result = new TestResult
                {
                    Success = false,
                    Errors = new List<string> { $"Exception: {ex.GetBaseException().Message}" },
                    TestName = test.Name,
                    TestIndex = test.Idx
                };
            }
            stepSw.Stop();

            if (result.Success)
            {
                passCount++;
                // Only log first few passes to avoid noise
                if (passCount <= 3)
                    _output.WriteLine($"  [PASS] #{test.Idx} bytes=[{bytesHex}] ({stepSw.ElapsedMilliseconds} ms)");
            }
            else
            {
                _output.WriteLine($"  [FAIL] #{test.Idx} \"{test.Name}\" bytes=[{bytesHex}] ({stepSw.ElapsedMilliseconds} ms)");
                _output.WriteLine($"         Initial: {initialRegs}");
                string finalRegs = FormatRegsCompact(test.Final.Regs);
                _output.WriteLine($"         Expected: {finalRegs}");
                foreach (var err in result.Errors)
                    _output.WriteLine($"         >> {err}");

                failures.Add(result.ToString());
                // Stop early if too many failures (likely a systemic issue)
                if (failures.Count >= 10)
                {
                    failures.Add($"... stopping after {failures.Count} failures (of {tests.Count} tests)");
                    break;
                }
            }
        }
        totalSw.Stop();
        _output.WriteLine($"[DONE] Opcode {opcodeFile}: {passCount} passed, {failures.Count} failed in {totalSw.ElapsedMilliseconds} ms (exec only)");

        if (failures.Count > 0)
        {
            Assert.Fail(
                $"Opcode {opcodeFile}: {failures.Count} of {tests.Count} tests failed " +
                $"({passCount} passed):\n" +
                string.Join("\n", failures));
        }
    }

    /// <summary>Format register dict compactly for log output.</summary>
    private static string FormatRegsCompact(Dictionary<string, int> regs)
    {
        var parts = new List<string>();
        foreach (var kvp in regs.OrderBy(k => k.Key))
        {
            if (kvp.Key == "flags")
                parts.Add($"{kvp.Key}=0x{(ushort)kvp.Value:X4}");
            else
                parts.Add($"{kvp.Key}=0x{(ushort)kvp.Value:X4}");
        }
        return string.Join(" ", parts);
    }

    // =====================================================================
    // ADD (00-05): r/m8←r/m8+reg8, r/m16←r/m16+reg16, AL←AL+imm8, AX←AX+imm16
    // =====================================================================

    [Fact] public void Op00_ADD_rm8_reg8() => RunOpcodeTests("00");
    [Fact] public void Op01_ADD_rm16_reg16() => RunOpcodeTests("01");
    [Fact] public void Op02_ADD_reg8_rm8() => RunOpcodeTests("02");
    [Fact] public void Op03_ADD_reg16_rm16() => RunOpcodeTests("03");
    [Fact] public void Op04_ADD_AL_imm8() => RunOpcodeTests("04");
    [Fact] public void Op05_ADD_AX_imm16() => RunOpcodeTests("05");

    // =====================================================================
    // PUSH/POP segment registers (06, 07, 0E, 16, 17, 1E, 1F)
    // =====================================================================

    [Fact] public void Op06_PUSH_ES() => RunOpcodeTests("06");
    [Fact] public void Op07_POP_ES() => RunOpcodeTests("07");
    [Fact] public void Op0E_PUSH_CS() => RunOpcodeTests("0E");
    [Fact] public void Op16_PUSH_SS() => RunOpcodeTests("16");
    [Fact] public void Op17_POP_SS() => RunOpcodeTests("17");  // Note: not present in v2 per notes
    [Fact] public void Op1E_PUSH_DS() => RunOpcodeTests("1E");
    [Fact] public void Op1F_POP_DS() => RunOpcodeTests("1F");

    // =====================================================================
    // OR (08-0D)
    // =====================================================================

    [Fact] public void Op08_OR_rm8_reg8() => RunOpcodeTests("08");
    [Fact] public void Op09_OR_rm16_reg16() => RunOpcodeTests("09");
    [Fact] public void Op0A_OR_reg8_rm8() => RunOpcodeTests("0A");
    [Fact] public void Op0B_OR_reg16_rm16() => RunOpcodeTests("0B");
    [Fact] public void Op0C_OR_AL_imm8() => RunOpcodeTests("0C");
    [Fact] public void Op0D_OR_AX_imm16() => RunOpcodeTests("0D");

    // =====================================================================
    // ADC (10-15)
    // =====================================================================

    [Fact] public void Op10_ADC_rm8_reg8() => RunOpcodeTests("10");
    [Fact] public void Op11_ADC_rm16_reg16() => RunOpcodeTests("11");
    [Fact] public void Op12_ADC_reg8_rm8() => RunOpcodeTests("12");
    [Fact] public void Op13_ADC_reg16_rm16() => RunOpcodeTests("13");
    [Fact] public void Op14_ADC_AL_imm8() => RunOpcodeTests("14");
    [Fact] public void Op15_ADC_AX_imm16() => RunOpcodeTests("15");

    // =====================================================================
    // SBB (18-1D)
    // =====================================================================

    [Fact] public void Op18_SBB_rm8_reg8() => RunOpcodeTests("18");
    [Fact] public void Op19_SBB_rm16_reg16() => RunOpcodeTests("19");
    [Fact] public void Op1A_SBB_reg8_rm8() => RunOpcodeTests("1A");
    [Fact] public void Op1B_SBB_reg16_rm16() => RunOpcodeTests("1B");
    [Fact] public void Op1C_SBB_AL_imm8() => RunOpcodeTests("1C");
    [Fact] public void Op1D_SBB_AX_imm16() => RunOpcodeTests("1D");

    // =====================================================================
    // AND (20-25)
    // =====================================================================

    [Fact] public void Op20_AND_rm8_reg8() => RunOpcodeTests("20");
    [Fact] public void Op21_AND_rm16_reg16() => RunOpcodeTests("21");
    [Fact] public void Op22_AND_reg8_rm8() => RunOpcodeTests("22");
    [Fact] public void Op23_AND_reg16_rm16() => RunOpcodeTests("23");
    [Fact] public void Op24_AND_AL_imm8() => RunOpcodeTests("24");
    [Fact] public void Op25_AND_AX_imm16() => RunOpcodeTests("25");

    // =====================================================================
    // DAA / DAS (27, 2F)
    // =====================================================================

    [Fact] public void Op27_DAA() => RunOpcodeTests("27");
    [Fact] public void Op2F_DAS() => RunOpcodeTests("2F");

    // =====================================================================
    // SUB (28-2D)
    // =====================================================================

    [Fact] public void Op28_SUB_rm8_reg8() => RunOpcodeTests("28");
    [Fact] public void Op29_SUB_rm16_reg16() => RunOpcodeTests("29");
    [Fact] public void Op2A_SUB_reg8_rm8() => RunOpcodeTests("2A");
    [Fact] public void Op2B_SUB_reg16_rm16() => RunOpcodeTests("2B");
    [Fact] public void Op2C_SUB_AL_imm8() => RunOpcodeTests("2C");
    [Fact] public void Op2D_SUB_AX_imm16() => RunOpcodeTests("2D");

    // =====================================================================
    // XOR (30-35)
    // =====================================================================

    [Fact] public void Op30_XOR_rm8_reg8() => RunOpcodeTests("30");
    [Fact] public void Op31_XOR_rm16_reg16() => RunOpcodeTests("31");
    [Fact] public void Op32_XOR_reg8_rm8() => RunOpcodeTests("32");
    [Fact] public void Op33_XOR_reg16_rm16() => RunOpcodeTests("33");
    [Fact] public void Op34_XOR_AL_imm8() => RunOpcodeTests("34");
    [Fact] public void Op35_XOR_AX_imm16() => RunOpcodeTests("35");

    // =====================================================================
    // AAA / AAS (37, 3F)
    // =====================================================================

    [Fact] public void Op37_AAA() => RunOpcodeTests("37");
    [Fact] public void Op3F_AAS() => RunOpcodeTests("3F");

    // =====================================================================
    // CMP (38-3D)
    // =====================================================================

    [Fact] public void Op38_CMP_rm8_reg8() => RunOpcodeTests("38");
    [Fact] public void Op39_CMP_rm16_reg16() => RunOpcodeTests("39");
    [Fact] public void Op3A_CMP_reg8_rm8() => RunOpcodeTests("3A");
    [Fact] public void Op3B_CMP_reg16_rm16() => RunOpcodeTests("3B");
    [Fact] public void Op3C_CMP_AL_imm8() => RunOpcodeTests("3C");
    [Fact] public void Op3D_CMP_AX_imm16() => RunOpcodeTests("3D");

    // =====================================================================
    // INC reg16 (40-47)
    // =====================================================================

    [Fact] public void Op40_INC_AX() => RunOpcodeTests("40");
    [Fact] public void Op41_INC_CX() => RunOpcodeTests("41");
    [Fact] public void Op42_INC_DX() => RunOpcodeTests("42");
    [Fact] public void Op43_INC_BX() => RunOpcodeTests("43");
    [Fact] public void Op44_INC_SP() => RunOpcodeTests("44");
    [Fact] public void Op45_INC_BP() => RunOpcodeTests("45");
    [Fact] public void Op46_INC_SI() => RunOpcodeTests("46");
    [Fact] public void Op47_INC_DI() => RunOpcodeTests("47");

    // =====================================================================
    // DEC reg16 (48-4F)
    // =====================================================================

    [Fact] public void Op48_DEC_AX() => RunOpcodeTests("48");
    [Fact] public void Op49_DEC_CX() => RunOpcodeTests("49");
    [Fact] public void Op4A_DEC_DX() => RunOpcodeTests("4A");
    [Fact] public void Op4B_DEC_BX() => RunOpcodeTests("4B");
    [Fact] public void Op4C_DEC_SP() => RunOpcodeTests("4C");
    [Fact] public void Op4D_DEC_BP() => RunOpcodeTests("4D");
    [Fact] public void Op4E_DEC_SI() => RunOpcodeTests("4E");
    [Fact] public void Op4F_DEC_DI() => RunOpcodeTests("4F");

    // =====================================================================
    // PUSH reg16 (50-57)
    // =====================================================================

    [Fact] public void Op50_PUSH_AX() => RunOpcodeTests("50");
    [Fact] public void Op51_PUSH_CX() => RunOpcodeTests("51");
    [Fact] public void Op52_PUSH_DX() => RunOpcodeTests("52");
    [Fact] public void Op53_PUSH_BX() => RunOpcodeTests("53");
    [Fact] public void Op54_PUSH_SP() => RunOpcodeTests("54");
    [Fact] public void Op55_PUSH_BP() => RunOpcodeTests("55");
    [Fact] public void Op56_PUSH_SI() => RunOpcodeTests("56");
    [Fact] public void Op57_PUSH_DI() => RunOpcodeTests("57");

    // =====================================================================
    // POP reg16 (58-5F)
    // =====================================================================

    [Fact] public void Op58_POP_AX() => RunOpcodeTests("58");
    [Fact] public void Op59_POP_CX() => RunOpcodeTests("59");
    [Fact] public void Op5A_POP_DX() => RunOpcodeTests("5A");
    [Fact] public void Op5B_POP_BX() => RunOpcodeTests("5B");
    [Fact] public void Op5C_POP_SP() => RunOpcodeTests("5C");
    [Fact] public void Op5D_POP_BP() => RunOpcodeTests("5D");
    [Fact] public void Op5E_POP_SI() => RunOpcodeTests("5E");
    [Fact] public void Op5F_POP_DI() => RunOpcodeTests("5F");

    // =====================================================================
    // Jcc short - conditional jumps (70-7F)
    // =====================================================================

    [Fact] public void Op70_JO() => RunOpcodeTests("70");
    [Fact] public void Op71_JNO() => RunOpcodeTests("71");
    [Fact] public void Op72_JB() => RunOpcodeTests("72");
    [Fact] public void Op73_JNB() => RunOpcodeTests("73");
    [Fact] public void Op74_JZ() => RunOpcodeTests("74");
    [Fact] public void Op75_JNZ() => RunOpcodeTests("75");
    [Fact] public void Op76_JBE() => RunOpcodeTests("76");
    [Fact] public void Op77_JA() => RunOpcodeTests("77");
    [Fact] public void Op78_JS() => RunOpcodeTests("78");
    [Fact] public void Op79_JNS() => RunOpcodeTests("79");
    [Fact] public void Op7A_JP() => RunOpcodeTests("7A");
    [Fact] public void Op7B_JNP() => RunOpcodeTests("7B");
    [Fact] public void Op7C_JL() => RunOpcodeTests("7C");
    [Fact] public void Op7D_JGE() => RunOpcodeTests("7D");
    [Fact] public void Op7E_JLE() => RunOpcodeTests("7E");
    [Fact] public void Op7F_JG() => RunOpcodeTests("7F");

    // =====================================================================
    // Group 1: immediate ALU (80-83) with reg field sub-opcodes
    // =====================================================================

    // 80.x: byte r/m, imm8
    [Fact] public void Op80_0_ADD_rm8_imm8() => RunOpcodeTests("80.0", regField: 0);
    [Fact] public void Op80_1_OR_rm8_imm8() => RunOpcodeTests("80.1", regField: 1);
    [Fact] public void Op80_2_ADC_rm8_imm8() => RunOpcodeTests("80.2", regField: 2);
    [Fact] public void Op80_3_SBB_rm8_imm8() => RunOpcodeTests("80.3", regField: 3);
    [Fact] public void Op80_4_AND_rm8_imm8() => RunOpcodeTests("80.4", regField: 4);
    [Fact] public void Op80_5_SUB_rm8_imm8() => RunOpcodeTests("80.5", regField: 5);
    [Fact] public void Op80_6_XOR_rm8_imm8() => RunOpcodeTests("80.6", regField: 6);
    [Fact] public void Op80_7_CMP_rm8_imm8() => RunOpcodeTests("80.7", regField: 7);

    // 81.x: word r/m, imm16
    [Fact] public void Op81_0_ADD_rm16_imm16() => RunOpcodeTests("81.0", regField: 0);
    [Fact] public void Op81_1_OR_rm16_imm16() => RunOpcodeTests("81.1", regField: 1);
    [Fact] public void Op81_2_ADC_rm16_imm16() => RunOpcodeTests("81.2", regField: 2);
    [Fact] public void Op81_3_SBB_rm16_imm16() => RunOpcodeTests("81.3", regField: 3);
    [Fact] public void Op81_4_AND_rm16_imm16() => RunOpcodeTests("81.4", regField: 4);
    [Fact] public void Op81_5_SUB_rm16_imm16() => RunOpcodeTests("81.5", regField: 5);
    [Fact] public void Op81_6_XOR_rm16_imm16() => RunOpcodeTests("81.6", regField: 6);
    [Fact] public void Op81_7_CMP_rm16_imm16() => RunOpcodeTests("81.7", regField: 7);

    // 82.x: byte r/m, imm8 (alias of 80)
    [Fact] public void Op82_0_ADD_rm8_imm8() => RunOpcodeTests("82.0", regField: 0);
    [Fact] public void Op82_1_OR_rm8_imm8() => RunOpcodeTests("82.1", regField: 1);
    [Fact] public void Op82_2_ADC_rm8_imm8() => RunOpcodeTests("82.2", regField: 2);
    [Fact] public void Op82_3_SBB_rm8_imm8() => RunOpcodeTests("82.3", regField: 3);
    [Fact] public void Op82_4_AND_rm8_imm8() => RunOpcodeTests("82.4", regField: 4);
    [Fact] public void Op82_5_SUB_rm8_imm8() => RunOpcodeTests("82.5", regField: 5);
    [Fact] public void Op82_6_XOR_rm8_imm8() => RunOpcodeTests("82.6", regField: 6);
    [Fact] public void Op82_7_CMP_rm8_imm8() => RunOpcodeTests("82.7", regField: 7);

    // 83.x: word r/m, sign-extended imm8
    [Fact] public void Op83_0_ADD_rm16_simm8() => RunOpcodeTests("83.0", regField: 0);
    [Fact] public void Op83_1_OR_rm16_simm8() => RunOpcodeTests("83.1", regField: 1);
    [Fact] public void Op83_2_ADC_rm16_simm8() => RunOpcodeTests("83.2", regField: 2);
    [Fact] public void Op83_3_SBB_rm16_simm8() => RunOpcodeTests("83.3", regField: 3);
    [Fact] public void Op83_4_AND_rm16_simm8() => RunOpcodeTests("83.4", regField: 4);
    [Fact] public void Op83_5_SUB_rm16_simm8() => RunOpcodeTests("83.5", regField: 5);
    [Fact] public void Op83_6_XOR_rm16_simm8() => RunOpcodeTests("83.6", regField: 6);
    [Fact] public void Op83_7_CMP_rm16_simm8() => RunOpcodeTests("83.7", regField: 7);

    // =====================================================================
    // TEST (84-85)
    // =====================================================================

    [Fact] public void Op84_TEST_rm8_reg8() => RunOpcodeTests("84");
    [Fact] public void Op85_TEST_rm16_reg16() => RunOpcodeTests("85");

    // =====================================================================
    // XCHG (86-87)
    // =====================================================================

    [Fact] public void Op86_XCHG_rm8_reg8() => RunOpcodeTests("86");
    [Fact] public void Op87_XCHG_rm16_reg16() => RunOpcodeTests("87");

    // =====================================================================
    // MOV (88-8E)
    // =====================================================================

    [Fact] public void Op88_MOV_rm8_reg8() => RunOpcodeTests("88");
    [Fact] public void Op89_MOV_rm16_reg16() => RunOpcodeTests("89");
    [Fact] public void Op8A_MOV_reg8_rm8() => RunOpcodeTests("8A");
    [Fact] public void Op8B_MOV_reg16_rm16() => RunOpcodeTests("8B");
    [Fact] public void Op8C_MOV_rm16_sreg() => RunOpcodeTests("8C");
    [Fact] public void Op8D_LEA() => RunOpcodeTests("8D");
    [Fact] public void Op8E_MOV_sreg_rm16() => RunOpcodeTests("8E");

    // =====================================================================
    // POP r/m16 (8F)
    // =====================================================================

    [Fact] public void Op8F_POP_rm16() => RunOpcodeTests("8F");

    // =====================================================================
    // NOP / XCHG AX, reg (90-97)
    // =====================================================================

    [Fact] public void Op90_NOP() => RunOpcodeTests("90");
    [Fact] public void Op91_XCHG_AX_CX() => RunOpcodeTests("91");
    [Fact] public void Op92_XCHG_AX_DX() => RunOpcodeTests("92");
    [Fact] public void Op93_XCHG_AX_BX() => RunOpcodeTests("93");
    [Fact] public void Op94_XCHG_AX_SP() => RunOpcodeTests("94");
    [Fact] public void Op95_XCHG_AX_BP() => RunOpcodeTests("95");
    [Fact] public void Op96_XCHG_AX_SI() => RunOpcodeTests("96");
    [Fact] public void Op97_XCHG_AX_DI() => RunOpcodeTests("97");

    // =====================================================================
    // CBW / CWD (98-99)
    // =====================================================================

    [Fact] public void Op98_CBW() => RunOpcodeTests("98");
    [Fact] public void Op99_CWD() => RunOpcodeTests("99");

    // =====================================================================
    // CALL far / PUSHF / POPF / SAHF / LAHF (9A, 9C-9F)
    // =====================================================================

    [Fact] public void Op9A_CALL_far() => RunOpcodeTests("9A");
    [Fact] public void Op9C_PUSHF() => RunOpcodeTests("9C");
    [Fact] public void Op9D_POPF() => RunOpcodeTests("9D");
    [Fact] public void Op9E_SAHF() => RunOpcodeTests("9E");
    [Fact] public void Op9F_LAHF() => RunOpcodeTests("9F");

    // =====================================================================
    // MOV AL/AX ↔ mem (A0-A3)
    // =====================================================================

    [Fact] public void OpA0_MOV_AL_moffs8() => RunOpcodeTests("A0");
    [Fact] public void OpA1_MOV_AX_moffs16() => RunOpcodeTests("A1");
    [Fact] public void OpA2_MOV_moffs8_AL() => RunOpcodeTests("A2");
    [Fact] public void OpA3_MOV_moffs16_AX() => RunOpcodeTests("A3");

    // =====================================================================
    // String instructions (A4-AF)
    // =====================================================================

    [Fact] public void OpA4_MOVSB() => RunOpcodeTests("A4");
    [Fact] public void OpA5_MOVSW() => RunOpcodeTests("A5");
    [Fact] public void OpA6_CMPSB() => RunOpcodeTests("A6");
    [Fact] public void OpA7_CMPSW() => RunOpcodeTests("A7");
    [Fact] public void OpA8_TEST_AL_imm8() => RunOpcodeTests("A8");
    [Fact] public void OpA9_TEST_AX_imm16() => RunOpcodeTests("A9");
    [Fact] public void OpAA_STOSB() => RunOpcodeTests("AA");
    [Fact] public void OpAB_STOSW() => RunOpcodeTests("AB");
    [Fact] public void OpAC_LODSB() => RunOpcodeTests("AC");
    [Fact] public void OpAD_LODSW() => RunOpcodeTests("AD");
    [Fact] public void OpAE_SCASB() => RunOpcodeTests("AE");
    [Fact] public void OpAF_SCASW() => RunOpcodeTests("AF");

    // =====================================================================
    // MOV reg8, imm8 (B0-B7)
    // =====================================================================

    [Fact] public void OpB0_MOV_AL_imm8() => RunOpcodeTests("B0");
    [Fact] public void OpB1_MOV_CL_imm8() => RunOpcodeTests("B1");
    [Fact] public void OpB2_MOV_DL_imm8() => RunOpcodeTests("B2");
    [Fact] public void OpB3_MOV_BL_imm8() => RunOpcodeTests("B3");
    [Fact] public void OpB4_MOV_AH_imm8() => RunOpcodeTests("B4");
    [Fact] public void OpB5_MOV_CH_imm8() => RunOpcodeTests("B5");
    [Fact] public void OpB6_MOV_DH_imm8() => RunOpcodeTests("B6");
    [Fact] public void OpB7_MOV_BH_imm8() => RunOpcodeTests("B7");

    // =====================================================================
    // MOV reg16, imm16 (B8-BF)
    // =====================================================================

    [Fact] public void OpB8_MOV_AX_imm16() => RunOpcodeTests("B8");
    [Fact] public void OpB9_MOV_CX_imm16() => RunOpcodeTests("B9");
    [Fact] public void OpBA_MOV_DX_imm16() => RunOpcodeTests("BA");
    [Fact] public void OpBB_MOV_BX_imm16() => RunOpcodeTests("BB");
    [Fact] public void OpBC_MOV_SP_imm16() => RunOpcodeTests("BC");
    [Fact] public void OpBD_MOV_BP_imm16() => RunOpcodeTests("BD");
    [Fact] public void OpBE_MOV_SI_imm16() => RunOpcodeTests("BE");
    [Fact] public void OpBF_MOV_DI_imm16() => RunOpcodeTests("BF");

    // =====================================================================
    // RET near (C2-C3)
    // =====================================================================

    [Fact] public void OpC2_RET_imm16() => RunOpcodeTests("C2");
    [Fact] public void OpC3_RET() => RunOpcodeTests("C3");

    // =====================================================================
    // LES / LDS (C4-C5)
    // =====================================================================

    [Fact] public void OpC4_LES() => RunOpcodeTests("C4");
    [Fact] public void OpC5_LDS() => RunOpcodeTests("C5");

    // =====================================================================
    // MOV r/m, imm (C6-C7)
    // =====================================================================

    [Fact] public void OpC6_MOV_rm8_imm8() => RunOpcodeTests("C6");
    [Fact] public void OpC7_MOV_rm16_imm16() => RunOpcodeTests("C7");

    // =====================================================================
    // RET far (CA-CB)
    // =====================================================================

    [Fact] public void OpCA_RETF_imm16() => RunOpcodeTests("CA");
    [Fact] public void OpCB_RETF() => RunOpcodeTests("CB");

    // =====================================================================
    // INT (CC-CE)
    // =====================================================================

    [Fact] public void OpCC_INT3() => RunOpcodeTests("CC");
    [Fact] public void OpCD_INT_imm8() => RunOpcodeTests("CD");
    [Fact] public void OpCE_INTO() => RunOpcodeTests("CE");

    // =====================================================================
    // IRET (CF)
    // =====================================================================

    [Fact] public void OpCF_IRET() => RunOpcodeTests("CF");

    // =====================================================================
    // Shift/Rotate by 1 (D0-D1)
    // =====================================================================

    [Fact] public void OpD0_0_ROL_rm8_1() => RunOpcodeTests("D0.0", regField: 0);
    [Fact] public void OpD0_1_ROR_rm8_1() => RunOpcodeTests("D0.1", regField: 1);
    [Fact] public void OpD0_2_RCL_rm8_1() => RunOpcodeTests("D0.2", regField: 2);
    [Fact] public void OpD0_3_RCR_rm8_1() => RunOpcodeTests("D0.3", regField: 3);
    [Fact] public void OpD0_4_SHL_rm8_1() => RunOpcodeTests("D0.4", regField: 4);
    [Fact] public void OpD0_5_SHR_rm8_1() => RunOpcodeTests("D0.5", regField: 5);
    [Fact] public void OpD0_7_SAR_rm8_1() => RunOpcodeTests("D0.7", regField: 7);

    [Fact] public void OpD1_0_ROL_rm16_1() => RunOpcodeTests("D1.0", regField: 0);
    [Fact] public void OpD1_1_ROR_rm16_1() => RunOpcodeTests("D1.1", regField: 1);
    [Fact] public void OpD1_2_RCL_rm16_1() => RunOpcodeTests("D1.2", regField: 2);
    [Fact] public void OpD1_3_RCR_rm16_1() => RunOpcodeTests("D1.3", regField: 3);
    [Fact] public void OpD1_4_SHL_rm16_1() => RunOpcodeTests("D1.4", regField: 4);
    [Fact] public void OpD1_5_SHR_rm16_1() => RunOpcodeTests("D1.5", regField: 5);
    [Fact] public void OpD1_7_SAR_rm16_1() => RunOpcodeTests("D1.7", regField: 7);

    // =====================================================================
    // Shift/Rotate by CL (D2-D3)
    // =====================================================================

    [Fact] public void OpD2_0_ROL_rm8_CL() => RunOpcodeTests("D2.0", regField: 0);
    [Fact] public void OpD2_1_ROR_rm8_CL() => RunOpcodeTests("D2.1", regField: 1);
    [Fact] public void OpD2_2_RCL_rm8_CL() => RunOpcodeTests("D2.2", regField: 2);
    [Fact] public void OpD2_3_RCR_rm8_CL() => RunOpcodeTests("D2.3", regField: 3);
    [Fact] public void OpD2_4_SHL_rm8_CL() => RunOpcodeTests("D2.4", regField: 4);
    [Fact] public void OpD2_5_SHR_rm8_CL() => RunOpcodeTests("D2.5", regField: 5);
    [Fact] public void OpD2_7_SAR_rm8_CL() => RunOpcodeTests("D2.7", regField: 7);

    [Fact] public void OpD3_0_ROL_rm16_CL() => RunOpcodeTests("D3.0", regField: 0);
    [Fact] public void OpD3_1_ROR_rm16_CL() => RunOpcodeTests("D3.1", regField: 1);
    [Fact] public void OpD3_2_RCL_rm16_CL() => RunOpcodeTests("D3.2", regField: 2);
    [Fact] public void OpD3_3_RCR_rm16_CL() => RunOpcodeTests("D3.3", regField: 3);
    [Fact] public void OpD3_4_SHL_rm16_CL() => RunOpcodeTests("D3.4", regField: 4);
    [Fact] public void OpD3_5_SHR_rm16_CL() => RunOpcodeTests("D3.5", regField: 5);
    [Fact] public void OpD3_7_SAR_rm16_CL() => RunOpcodeTests("D3.7", regField: 7);

    // =====================================================================
    // AAM / AAD (D4-D5)
    // =====================================================================

    [Fact] public void OpD4_AAM() => RunOpcodeTests("D4");
    [Fact] public void OpD5_AAD() => RunOpcodeTests("D5");

    // =====================================================================
    // SALC - undocumented (D6)
    // =====================================================================

    [Fact] public void OpD6_SALC() => RunOpcodeTests("D6");

    // =====================================================================
    // XLAT (D7)
    // =====================================================================

    [Fact] public void OpD7_XLAT() => RunOpcodeTests("D7");

    // =====================================================================
    // ESC / FPU (D8-DF)
    // =====================================================================

    [Fact] public void OpD8_ESC() => RunOpcodeTests("D8");
    [Fact] public void OpD9_ESC() => RunOpcodeTests("D9");
    [Fact] public void OpDA_ESC() => RunOpcodeTests("DA");
    [Fact] public void OpDB_ESC() => RunOpcodeTests("DB");
    [Fact] public void OpDC_ESC() => RunOpcodeTests("DC");
    [Fact] public void OpDD_ESC() => RunOpcodeTests("DD");
    [Fact] public void OpDE_ESC() => RunOpcodeTests("DE");
    [Fact] public void OpDF_ESC() => RunOpcodeTests("DF");

    // =====================================================================
    // LOOP / LOOPcc / JCXZ (E0-E3)
    // =====================================================================

    [Fact] public void OpE0_LOOPNZ() => RunOpcodeTests("E0");
    [Fact] public void OpE1_LOOPZ() => RunOpcodeTests("E1");
    [Fact] public void OpE2_LOOP() => RunOpcodeTests("E2");
    [Fact] public void OpE3_JCXZ() => RunOpcodeTests("E3");

    // =====================================================================
    // IN / OUT (E4-E7, EC-EF)
    // =====================================================================

    [Fact] public void OpE4_IN_AL_imm8() => RunOpcodeTests("E4");
    [Fact] public void OpE5_IN_AX_imm8() => RunOpcodeTests("E5");
    [Fact] public void OpE6_OUT_imm8_AL() => RunOpcodeTests("E6");
    [Fact] public void OpE7_OUT_imm8_AX() => RunOpcodeTests("E7");
    [Fact] public void OpEC_IN_AL_DX() => RunOpcodeTests("EC");
    [Fact] public void OpED_IN_AX_DX() => RunOpcodeTests("ED");
    [Fact] public void OpEE_OUT_DX_AL() => RunOpcodeTests("EE");
    [Fact] public void OpEF_OUT_DX_AX() => RunOpcodeTests("EF");

    // =====================================================================
    // CALL near / JMP near/far/short (E8-EB)
    // =====================================================================

    [Fact] public void OpE8_CALL_near() => RunOpcodeTests("E8");
    [Fact] public void OpE9_JMP_near() => RunOpcodeTests("E9");
    [Fact] public void OpEA_JMP_far() => RunOpcodeTests("EA");
    [Fact] public void OpEB_JMP_short() => RunOpcodeTests("EB");

    // =====================================================================
    // CMC / Flag instructions (F5, F8-FD)
    // =====================================================================

    [Fact] public void OpF5_CMC() => RunOpcodeTests("F5");
    [Fact] public void OpF8_CLC() => RunOpcodeTests("F8");
    [Fact] public void OpF9_STC() => RunOpcodeTests("F9");
    [Fact] public void OpFA_CLI() => RunOpcodeTests("FA");
    [Fact] public void OpFB_STI() => RunOpcodeTests("FB");
    [Fact] public void OpFC_CLD() => RunOpcodeTests("FC");
    [Fact] public void OpFD_STD() => RunOpcodeTests("FD");

    // =====================================================================
    // Group 3: Unary operations (F6, F7)
    // =====================================================================

    [Fact] public void OpF6_0_TEST_rm8_imm8() => RunOpcodeTests("F6.0", regField: 0);
    [Fact] public void OpF6_1_TEST_rm8_imm8_alias() => RunOpcodeTests("F6.1", regField: 1);
    [Fact] public void OpF6_2_NOT_rm8() => RunOpcodeTests("F6.2", regField: 2);
    [Fact] public void OpF6_3_NEG_rm8() => RunOpcodeTests("F6.3", regField: 3);
    [Fact] public void OpF6_4_MUL_rm8() => RunOpcodeTests("F6.4", regField: 4);
    [Fact] public void OpF6_5_IMUL_rm8() => RunOpcodeTests("F6.5", regField: 5);
    [Fact] public void OpF6_6_DIV_rm8() => RunOpcodeTests("F6.6", regField: 6);
    [Fact] public void OpF6_7_IDIV_rm8() => RunOpcodeTests("F6.7", regField: 7);

    [Fact] public void OpF7_0_TEST_rm16_imm16() => RunOpcodeTests("F7.0", regField: 0);
    [Fact] public void OpF7_1_TEST_rm16_imm16_alias() => RunOpcodeTests("F7.1", regField: 1);
    [Fact] public void OpF7_2_NOT_rm16() => RunOpcodeTests("F7.2", regField: 2);
    [Fact] public void OpF7_3_NEG_rm16() => RunOpcodeTests("F7.3", regField: 3);
    [Fact] public void OpF7_4_MUL_rm16() => RunOpcodeTests("F7.4", regField: 4);
    [Fact] public void OpF7_5_IMUL_rm16() => RunOpcodeTests("F7.5", regField: 5);
    [Fact] public void OpF7_6_DIV_rm16() => RunOpcodeTests("F7.6", regField: 6);
    [Fact] public void OpF7_7_IDIV_rm16() => RunOpcodeTests("F7.7", regField: 7);

    // =====================================================================
    // Group 4/5: INC/DEC/CALL/JMP/PUSH (FE, FF)
    // =====================================================================

    [Fact] public void OpFE_0_INC_rm8() => RunOpcodeTests("FE.0", regField: 0);
    [Fact] public void OpFE_1_DEC_rm8() => RunOpcodeTests("FE.1", regField: 1);

    [Fact] public void OpFF_0_INC_rm16() => RunOpcodeTests("FF.0", regField: 0);
    [Fact] public void OpFF_1_DEC_rm16() => RunOpcodeTests("FF.1", regField: 1);
    [Fact] public void OpFF_2_CALL_rm16() => RunOpcodeTests("FF.2", regField: 2);
    [Fact] public void OpFF_3_CALL_far_m() => RunOpcodeTests("FF.3", regField: 3);
    [Fact] public void OpFF_4_JMP_rm16() => RunOpcodeTests("FF.4", regField: 4);
    [Fact] public void OpFF_5_JMP_far_m() => RunOpcodeTests("FF.5", regField: 5);
    [Fact] public void OpFF_6_PUSH_rm16() => RunOpcodeTests("FF.6", regField: 6);
    [Fact] public void OpFF_7_PUSH_rm16_alias() => RunOpcodeTests("FF.7", regField: 7);

    // =====================================================================
    // Aliases on 8088 (60-6F map to 70-7F on 8088, not 186 instructions)
    // =====================================================================

    [Fact] public void Op60_alias() => RunOpcodeTests("60");
    [Fact] public void Op61_alias() => RunOpcodeTests("61");
    [Fact] public void Op62_alias() => RunOpcodeTests("62");
    [Fact] public void Op63_alias() => RunOpcodeTests("63");
    [Fact] public void Op64_alias() => RunOpcodeTests("64");
    [Fact] public void Op65_alias() => RunOpcodeTests("65");
    [Fact] public void Op66_alias() => RunOpcodeTests("66");
    [Fact] public void Op67_alias() => RunOpcodeTests("67");
    [Fact] public void Op68_alias() => RunOpcodeTests("68");
    [Fact] public void Op69_alias() => RunOpcodeTests("69");
    [Fact] public void Op6A_alias() => RunOpcodeTests("6A");
    [Fact] public void Op6B_alias() => RunOpcodeTests("6B");
    [Fact] public void Op6C_alias() => RunOpcodeTests("6C");
    [Fact] public void Op6D_alias() => RunOpcodeTests("6D");
    [Fact] public void Op6E_alias() => RunOpcodeTests("6E");
    [Fact] public void Op6F_alias() => RunOpcodeTests("6F");

    // =====================================================================
    // D0.6 / D1.6 / D2.6 / D3.6: SHL (undocumented alias for SHL = SAL)
    // =====================================================================

    [Fact] public void OpD0_6_SHL_undoc_rm8_1() => RunOpcodeTests("D0.6", regField: 6);
    [Fact] public void OpD1_6_SHL_undoc_rm16_1() => RunOpcodeTests("D1.6", regField: 6);
    [Fact] public void OpD2_6_SHL_undoc_rm8_CL() => RunOpcodeTests("D2.6", regField: 6);
    [Fact] public void OpD3_6_SHL_undoc_rm16_CL() => RunOpcodeTests("D3.6", regField: 6);

    // Aliases C0/C1 (on 8088 these alias to shift/rotate group)
    [Fact] public void OpC0_alias() => RunOpcodeTests("C0");
    [Fact] public void OpC1_alias() => RunOpcodeTests("C1");
    // Aliases C8/C9 (on 8088 these alias to other instructions)
    [Fact] public void OpC8_alias() => RunOpcodeTests("C8");
    [Fact] public void OpC9_alias() => RunOpcodeTests("C9");
}
