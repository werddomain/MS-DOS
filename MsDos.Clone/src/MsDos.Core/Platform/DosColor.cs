namespace MsDos.Core.Platform;

/// <summary>
/// Represents a color in the DOS 16-color palette (CGA/EGA/VGA text mode).
/// </summary>
public readonly struct DosColor
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public DosColor(byte r, byte g, byte b) { R = r; G = g; B = b; }

    /// <summary>Standard CGA 16-color palette.</summary>
    public static readonly DosColor[] Palette = new DosColor[]
    {
        new(0x00, 0x00, 0x00), // 0 Black
        new(0x00, 0x00, 0xAA), // 1 Blue
        new(0x00, 0xAA, 0x00), // 2 Green
        new(0x00, 0xAA, 0xAA), // 3 Cyan
        new(0xAA, 0x00, 0x00), // 4 Red
        new(0xAA, 0x00, 0xAA), // 5 Magenta
        new(0xAA, 0x55, 0x00), // 6 Brown
        new(0xAA, 0xAA, 0xAA), // 7 Light Gray
        new(0x55, 0x55, 0x55), // 8 Dark Gray
        new(0x55, 0x55, 0xFF), // 9 Light Blue
        new(0x55, 0xFF, 0x55), // 10 Light Green
        new(0x55, 0xFF, 0xFF), // 11 Light Cyan
        new(0xFF, 0x55, 0x55), // 12 Light Red
        new(0xFF, 0x55, 0xFF), // 13 Light Magenta
        new(0xFF, 0xFF, 0x55), // 14 Yellow
        new(0xFF, 0xFF, 0xFF), // 15 White
    };
}
