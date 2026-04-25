using MsDos.Core.Cpu;

namespace MsDos.Tests;

/// <summary>
/// Tests for 32-bit register support in the Registers class.
/// Covers EAX-EBP properties, 16/8-bit aliasing, GetReg32/SetReg32.
/// </summary>
public class Registers32Tests
{
    private readonly Registers _regs = new();

    // ─── 32-bit read/write round-trip ───────────────────────────────

    [Fact]
    public void EAX_ReadWrite_RoundTrip()
    {
        _regs.EAX = 0xDEADBEEF;
        Assert.Equal(0xDEADBEEFu, _regs.EAX);
    }

    [Fact]
    public void EBX_ReadWrite_RoundTrip()
    {
        _regs.EBX = 0xCAFEBABE;
        Assert.Equal(0xCAFEBABEu, _regs.EBX);
    }

    [Fact]
    public void ECX_ReadWrite_RoundTrip()
    {
        _regs.ECX = 0x12345678;
        Assert.Equal(0x12345678u, _regs.ECX);
    }

    [Fact]
    public void EDX_ReadWrite_RoundTrip()
    {
        _regs.EDX = 0xFFFFFFFF;
        Assert.Equal(0xFFFFFFFFu, _regs.EDX);
    }

    [Fact]
    public void ESI_ReadWrite_RoundTrip()
    {
        _regs.ESI = 0x11223344;
        Assert.Equal(0x11223344u, _regs.ESI);
    }

    [Fact]
    public void EDI_ReadWrite_RoundTrip()
    {
        _regs.EDI = 0xAABBCCDD;
        Assert.Equal(0xAABBCCDDu, _regs.EDI);
    }

    [Fact]
    public void ESP_ReadWrite_RoundTrip()
    {
        _regs.ESP = 0x00010000;
        Assert.Equal(0x00010000u, _regs.ESP);
    }

    [Fact]
    public void EBP_ReadWrite_RoundTrip()
    {
        _regs.EBP = 0x80000001;
        Assert.Equal(0x80000001u, _regs.EBP);
    }

    // ─── 16-bit aliasing (low word of 32-bit) ──────────────────────

    [Fact]
    public void AX_IsLowWordOfEAX()
    {
        _regs.EAX = 0xAABB1234;
        Assert.Equal((ushort)0x1234, _regs.AX);
    }

    [Fact]
    public void SetAX_PreservesHighWord()
    {
        _regs.EAX = 0xAABB0000;
        _regs.AX = 0x5678;
        Assert.Equal(0xAABB5678u, _regs.EAX);
    }

    [Fact]
    public void BX_IsLowWordOfEBX()
    {
        _regs.EBX = 0xFFFF4321;
        Assert.Equal((ushort)0x4321, _regs.BX);
    }

    [Fact]
    public void SetBX_PreservesHighWord()
    {
        _regs.EBX = 0xCCDD0000;
        _regs.BX = 0xABCD;
        Assert.Equal(0xCCDDABCDu, _regs.EBX);
    }

    [Fact]
    public void SI_IsLowWordOfESI()
    {
        _regs.ESI = 0x12340567;
        Assert.Equal((ushort)0x0567, _regs.SI);
    }

    [Fact]
    public void SP_IsLowWordOfESP()
    {
        _regs.ESP = 0x0001FFFE;
        Assert.Equal((ushort)0xFFFE, _regs.SP);
    }

    [Fact]
    public void BP_IsLowWordOfEBP()
    {
        _regs.EBP = 0xAAAA5555;
        Assert.Equal((ushort)0x5555, _regs.BP);
    }

    [Fact]
    public void DI_IsLowWordOfEDI()
    {
        _regs.EDI = 0x11111234;
        Assert.Equal((ushort)0x1234, _regs.DI);
    }

    // ─── 8-bit aliasing ────────────────────────────────────────────

    [Fact]
    public void AL_IsLowestByteOfEAX()
    {
        _regs.EAX = 0xAABBCC42;
        Assert.Equal(0x42, _regs.AL);
    }

    [Fact]
    public void AH_IsSecondByteOfEAX()
    {
        _regs.EAX = 0xAABBCC42;
        Assert.Equal(0xCC, _regs.AH);
    }

    [Fact]
    public void SetAL_PreservesUpperBytes()
    {
        _regs.EAX = 0xDDCCBB00;
        _regs.AL = 0xFF;
        Assert.Equal(0xDDCCBBFFu, _regs.EAX);
    }

    [Fact]
    public void SetAH_PreservesOtherBytes()
    {
        _regs.EAX = 0xDD00BB11;
        _regs.AH = 0xEE;
        Assert.Equal(0xDD00EE11u, _regs.EAX);
    }

    // ─── GetReg32 / SetReg32 indexed accessors ─────────────────────

    [Theory]
    [InlineData(0, 0xAAAAAAAAu)] // EAX
    [InlineData(1, 0xBBBBBBBBu)] // ECX
    [InlineData(2, 0xCCCCCCCCu)] // EDX
    [InlineData(3, 0xDDDDDDDDu)] // EBX
    [InlineData(4, 0xEEEEEEEEu)] // ESP
    [InlineData(5, 0x11111111u)] // EBP
    [InlineData(6, 0x22222222u)] // ESI
    [InlineData(7, 0x33333333u)] // EDI
    public void SetReg32_GetReg32_RoundTrip(int index, uint value)
    {
        _regs.SetReg32(index, value);
        Assert.Equal(value, _regs.GetReg32(index));
    }

    [Fact]
    public void SetReg32_InvalidIndex_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _regs.SetReg32(8, 0));
    }

    [Fact]
    public void GetReg32_InvalidIndex_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _regs.GetReg32(8));
    }

    // ─── FS / GS segment registers ─────────────────────────────────

    [Fact]
    public void FS_ReadWrite_RoundTrip()
    {
        _regs.FS = 0x1234;
        Assert.Equal((ushort)0x1234, _regs.FS);
    }

    [Fact]
    public void GS_ReadWrite_RoundTrip()
    {
        _regs.GS = 0x5678;
        Assert.Equal((ushort)0x5678, _regs.GS);
    }

    [Fact]
    public void GetSegReg_FS_GS()
    {
        _regs.FS = 0xABCD;
        _regs.GS = 0x1234;
        Assert.Equal((ushort)0xABCD, _regs.GetSegReg(4));
        Assert.Equal((ushort)0x1234, _regs.GetSegReg(5));
    }

    [Fact]
    public void SetSegReg_FS_GS()
    {
        _regs.SetSegReg(4, 0x9999);
        _regs.SetSegReg(5, 0x7777);
        Assert.Equal((ushort)0x9999, _regs.FS);
        Assert.Equal((ushort)0x7777, _regs.GS);
    }

    // ─── Reset clears 32-bit registers ──────────────────────────────

    [Fact]
    public void Reset_ClearsAll32BitRegisters()
    {
        _regs.EAX = 0xFFFFFFFF;
        _regs.EBX = 0xFFFFFFFF;
        _regs.ECX = 0xFFFFFFFF;
        _regs.EDX = 0xFFFFFFFF;
        _regs.ESI = 0xFFFFFFFF;
        _regs.EDI = 0xFFFFFFFF;
        _regs.ESP = 0xFFFFFFFF;
        _regs.EBP = 0xFFFFFFFF;
        _regs.FS = 0xFFFF;
        _regs.GS = 0xFFFF;

        _regs.Reset();

        Assert.Equal(0u, _regs.EAX);
        Assert.Equal(0u, _regs.EBX);
        Assert.Equal(0u, _regs.ECX);
        Assert.Equal(0u, _regs.EDX);
        Assert.Equal(0u, _regs.ESI);
        Assert.Equal(0u, _regs.EDI);
        Assert.Equal(0u, _regs.ESP);
        Assert.Equal(0u, _regs.EBP);
        Assert.Equal((ushort)0, _regs.FS);
        Assert.Equal((ushort)0, _regs.GS);
    }
}
