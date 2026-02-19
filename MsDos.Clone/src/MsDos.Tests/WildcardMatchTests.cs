using MsDos.Core.Dos;

namespace MsDos.Tests;

public class WildcardMatchTests
{
    [Theory]
    [InlineData("*.*", "FILE.TXT", true)]
    [InlineData("*.*", "HELLO", true)]
    [InlineData("*", "FILE.TXT", true)]
    [InlineData("*.TXT", "FILE.TXT", true)]
    [InlineData("*.TXT", "FILE.COM", false)]
    [InlineData("*.COM", "HELLO.COM", true)]
    [InlineData("*.COM", "HELLO.EXE", false)]
    [InlineData("FILE.*", "FILE.TXT", true)]
    [InlineData("FILE.*", "OTHER.TXT", false)]
    [InlineData("F??E.*", "FILE.TXT", true)]
    [InlineData("F??E.*", "FACE.TXT", true)]
    [InlineData("F??E.*", "FE.TXT", false)]
    [InlineData("TEST?.COM", "TEST1.COM", true)]
    [InlineData("TEST?.COM", "TEST12.COM", false)]
    [InlineData("*.EXE", "hello.exe", true)]  // case-insensitive
    [InlineData("hello.*", "HELLO.COM", true)] // case-insensitive
    public void MatchWildcard_CorrectResults(string pattern, string text, bool expected)
    {
        // Use reflection to access internal method, or test indirectly
        bool result = DosKernel.MatchWildcard(pattern, text);
        Assert.Equal(expected, result);
    }
}
