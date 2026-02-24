using MsDos.Core.Dos;

namespace MsDos.Tests;

public class DosFileNameTests
{
    [Fact]
    public void SimpleFilename()
    {
        Assert.Equal("README.TXT", DosFileName.ToShortName("readme.txt"));
    }

    [Fact]
    public void LongBaseName_Truncated()
    {
        Assert.Equal("MY_DOCUM.TXT", DosFileName.ToShortName("my_document.txt"));
    }

    [Fact]
    public void LongExtension_Truncated()
    {
        Assert.Equal("FILE.BAC", DosFileName.ToShortName("file.backup"));
    }

    [Fact]
    public void NoExtension()
    {
        Assert.Equal("README", DosFileName.ToShortName("readme"));
    }

    [Fact]
    public void MultipleDots_UsesLastDot()
    {
        // "file...name" is the base, ".c" is the extension
        // Dots are valid characters in DOS filenames
        Assert.Equal("FILE...N.C", DosFileName.ToShortName("file...name.c"));
    }

    [Fact]
    public void SpacesAndInvalidChars_Replaced()
    {
        // Space is invalid, replaced with underscore
        Assert.Equal("HELLO_WO.DOC", DosFileName.ToShortName("hello world.doc"));
    }

    [Fact]
    public void LeadingDot_Stripped()
    {
        Assert.Equal("HIDDEN", DosFileName.ToShortName(".hidden"));
    }

    [Fact]
    public void PathComponents_Stripped()
    {
        Assert.Equal("FILE.TXT", DosFileName.ToShortName(@"C:\some\path\file.txt"));
    }

    [Fact]
    public void UppercaseConversion()
    {
        Assert.Equal("MASM.EXE", DosFileName.ToShortName("masm.exe"));
    }

    [Fact]
    public void EmptyOrWhitespace_ReturnsNoname()
    {
        Assert.Equal("NONAME", DosFileName.ToShortName(""));
        Assert.Equal("NONAME", DosFileName.ToShortName("  "));
    }

    [Fact]
    public void AlreadyDos83_Unchanged()
    {
        Assert.Equal("SETUP.BAT", DosFileName.ToShortName("SETUP.BAT"));
    }

    [Fact]
    public void To11ByteName_PaddedCorrectly()
    {
        string result = DosFileName.To11ByteName("MASM.EXE");
        Assert.Equal(11, result.Length);
        Assert.Equal("MASM    EXE", result);
    }

    [Fact]
    public void To11ByteName_NoExtension_PaddedCorrectly()
    {
        string result = DosFileName.To11ByteName("README");
        Assert.Equal(11, result.Length);
        Assert.Equal("README     ", result);
    }
}
