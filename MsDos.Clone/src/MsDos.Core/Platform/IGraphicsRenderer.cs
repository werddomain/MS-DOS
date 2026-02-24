namespace MsDos.Core.Platform;

/// <summary>
/// Abstraction for graphics/text rendering. Implementations exist for
/// WinForms (PictureBox/Graphics) and Blazor WebAssembly (Canvas via JS interop).
/// </summary>
public interface IGraphicsRenderer
{
    /// <summary>Width of the rendering surface in pixels.</summary>
    int Width { get; }

    /// <summary>Height of the rendering surface in pixels.</summary>
    int Height { get; }

    /// <summary>Set the video mode (text 80x25, graphics 320x200, etc.).</summary>
    void SetMode(VideoMode mode);

    /// <summary>Draw a single character at cell position (col, row) with foreground/background.</summary>
    void DrawCharacter(int col, int row, char character, byte foreground, byte background);

    /// <summary>Draw a pixel at (x, y) with a palette color index.</summary>
    void DrawPixel(int x, int y, byte colorIndex);

    /// <summary>Clear the entire surface with the given background color index.</summary>
    void Clear(byte backgroundColorIndex);

    /// <summary>Set the cursor position for text mode.</summary>
    void SetCursorPosition(int col, int row);

    /// <summary>Show or hide the text cursor.</summary>
    void SetCursorVisible(bool visible);

    /// <summary>Scroll the text display up by the specified number of lines.</summary>
    void ScrollUp(int lines, byte backgroundColorIndex);

    /// <summary>Scroll the text display down by the specified number of lines.</summary>
    void ScrollDown(int lines, byte backgroundColorIndex);

    /// <summary>
    /// Flush any buffered drawing commands to the display surface.
    /// Called once per frame or after a batch of draw operations.
    /// </summary>
    Task FlushAsync();
}

/// <summary>
/// Supported video modes matching standard DOS/BIOS INT 10h modes.
/// </summary>
public enum VideoMode : byte
{
    /// <summary>Text 80x25, 16 colors (mode 0x03).</summary>
    Text80x25 = 0x03,

    /// <summary>CGA Graphics 320x200, 4 colors (mode 0x04).</summary>
    Graphics320x200_4 = 0x04,

    /// <summary>CGA Graphics 640x200, 2 colors (mode 0x06).</summary>
    Graphics640x200_2 = 0x06,

    /// <summary>VGA Graphics 320x200, 256 colors (mode 0x13).</summary>
    Graphics320x200_256 = 0x13,
}
