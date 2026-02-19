namespace MsDos.Core.Platform;

/// <summary>
/// Abstraction for input event registration. Platform implementations wire
/// keyboard and mouse events from WinForms controls or Blazor JS interop.
/// </summary>
public interface IEventRegistry
{
    /// <summary>Fired when a key is pressed.</summary>
    event Action<DosKeyEventArgs>? KeyDown;

    /// <summary>Fired when a key is released.</summary>
    event Action<DosKeyEventArgs>? KeyUp;

    /// <summary>Fired when the mouse moves (graphics modes).</summary>
    event Action<DosMouseEventArgs>? MouseMove;

    /// <summary>Fired when a mouse button is pressed.</summary>
    event Action<DosMouseEventArgs>? MouseDown;

    /// <summary>Fired when a mouse button is released.</summary>
    event Action<DosMouseEventArgs>? MouseUp;

    /// <summary>Check if a key is currently available in the buffer (non-blocking).</summary>
    bool IsKeyAvailable { get; }

    /// <summary>Read the next key from the buffer (blocking-compatible via async).</summary>
    Task<DosKeyEventArgs> ReadKeyAsync(CancellationToken cancellationToken = default);
}

/// <summary>DOS key event arguments mapping to scan codes and ASCII.</summary>
public class DosKeyEventArgs : EventArgs
{
    /// <summary>IBM PC scan code (e.g., 0x1C = Enter).</summary>
    public byte ScanCode { get; set; }

    /// <summary>ASCII character (0 for extended keys).</summary>
    public byte AsciiChar { get; set; }

    /// <summary>Whether Shift is held.</summary>
    public bool Shift { get; set; }

    /// <summary>Whether Ctrl is held.</summary>
    public bool Ctrl { get; set; }

    /// <summary>Whether Alt is held.</summary>
    public bool Alt { get; set; }
}

/// <summary>DOS mouse event arguments.</summary>
public class DosMouseEventArgs : EventArgs
{
    public int X { get; set; }
    public int Y { get; set; }
    public DosMouseButton Button { get; set; }
}

public enum DosMouseButton : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 4,
}
