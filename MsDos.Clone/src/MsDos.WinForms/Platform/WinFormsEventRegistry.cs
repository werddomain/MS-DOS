using System.Collections.Concurrent;
using MsDos.Core.Platform;

namespace MsDos.WinForms.Platform;

/// <summary>
/// WinForms implementation of IEventRegistry. Captures keyboard and mouse
/// events from a Control and provides them to the DOS emulator.
/// </summary>
public sealed class WinFormsEventRegistry : IEventRegistry
{
    private readonly ConcurrentQueue<DosKeyEventArgs> _keyBuffer = new();
    private readonly SemaphoreSlim _keySemaphore = new(0);

    public event Action<DosKeyEventArgs>? KeyDown;
    public event Action<DosKeyEventArgs>? KeyUp;
    public event Action<DosMouseEventArgs>? MouseMove;
    public event Action<DosMouseEventArgs>? MouseDown;
    public event Action<DosMouseEventArgs>? MouseUp;

    public bool IsKeyAvailable => !_keyBuffer.IsEmpty;

    /// <summary>Attach to a WinForms control to capture input events.</summary>
    public void Attach(Control control)
    {
        control.KeyDown += OnKeyDown;
        control.KeyUp += OnKeyUp;
        control.MouseMove += OnMouseMove;
        control.MouseDown += OnMouseDown;
        control.MouseUp += OnMouseUp;
        control.PreviewKeyDown += (_, e) => e.IsInputKey = true;
    }

    public async Task<DosKeyEventArgs> ReadKeyAsync(CancellationToken cancellationToken = default)
    {
        await _keySemaphore.WaitAsync(cancellationToken);
        _keyBuffer.TryDequeue(out var key);
        return key ?? new DosKeyEventArgs();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var dosKey = MapKey(e);
        _keyBuffer.Enqueue(dosKey);
        _keySemaphore.Release();
        KeyDown?.Invoke(dosKey);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        KeyUp?.Invoke(MapKey(e));
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        MouseMove?.Invoke(new DosMouseEventArgs { X = e.X, Y = e.Y, Button = MapButton(e.Button) });
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        MouseDown?.Invoke(new DosMouseEventArgs { X = e.X, Y = e.Y, Button = MapButton(e.Button) });
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        MouseUp?.Invoke(new DosMouseEventArgs { X = e.X, Y = e.Y, Button = MapButton(e.Button) });
    }

    private static DosKeyEventArgs MapKey(KeyEventArgs e)
    {
        byte ascii = MapAscii(e.KeyCode, e.Shift);
        byte scan = MapScanCode(e.KeyCode);
        return new DosKeyEventArgs
        {
            AsciiChar = ascii,
            ScanCode = scan,
            Shift = e.Shift,
            Ctrl = e.Control,
            Alt = e.Alt,
        };
    }

    private static byte MapAscii(Keys key, bool shift)
    {
        if (key >= Keys.A && key <= Keys.Z)
            return (byte)(shift ? (key - Keys.A + 'A') : (key - Keys.A + 'a'));
        if (key >= Keys.D0 && key <= Keys.D9)
            return (byte)(key - Keys.D0 + '0');
        return key switch
        {
            Keys.Enter => 0x0D,
            Keys.Escape => 0x1B,
            Keys.Back => 0x08,
            Keys.Tab => 0x09,
            Keys.Space => 0x20,
            Keys.OemMinus => (byte)(shift ? '_' : '-'),
            Keys.Oemplus => (byte)(shift ? '+' : '='),
            Keys.OemOpenBrackets => (byte)(shift ? '{' : '['),
            Keys.OemCloseBrackets => (byte)(shift ? '}' : ']'),
            Keys.OemPipe => (byte)(shift ? '|' : '\\'),
            Keys.OemSemicolon => (byte)(shift ? ':' : ';'),
            Keys.OemQuotes => (byte)(shift ? '"' : '\''),
            Keys.Oemcomma => (byte)(shift ? '<' : ','),
            Keys.OemPeriod => (byte)(shift ? '>' : '.'),
            Keys.OemQuestion => (byte)(shift ? '?' : '/'),
            Keys.Oemtilde => (byte)(shift ? '~' : '`'),
            _ => 0x00 // Extended key
        };
    }

    private static byte MapScanCode(Keys key) => key switch
    {
        Keys.Escape => 0x01,
        Keys.D1 => 0x02, Keys.D2 => 0x03, Keys.D3 => 0x04, Keys.D4 => 0x05,
        Keys.D5 => 0x06, Keys.D6 => 0x07, Keys.D7 => 0x08, Keys.D8 => 0x09,
        Keys.D9 => 0x0A, Keys.D0 => 0x0B,
        Keys.Back => 0x0E,
        Keys.Tab => 0x0F,
        Keys.Q => 0x10, Keys.W => 0x11, Keys.E => 0x12, Keys.R => 0x13,
        Keys.T => 0x14, Keys.Y => 0x15, Keys.U => 0x16, Keys.I => 0x17,
        Keys.O => 0x18, Keys.P => 0x19,
        Keys.Enter => 0x1C,
        Keys.A => 0x1E, Keys.S => 0x1F, Keys.D => 0x20, Keys.F => 0x21,
        Keys.G => 0x22, Keys.H => 0x23, Keys.J => 0x24, Keys.K => 0x25,
        Keys.L => 0x26,
        Keys.Z => 0x2C, Keys.X => 0x2D, Keys.C => 0x2E, Keys.V => 0x2F,
        Keys.B => 0x30, Keys.N => 0x31, Keys.M => 0x32,
        Keys.Space => 0x39,
        Keys.F1 => 0x3B, Keys.F2 => 0x3C, Keys.F3 => 0x3D, Keys.F4 => 0x3E,
        Keys.F5 => 0x3F, Keys.F6 => 0x40, Keys.F7 => 0x41, Keys.F8 => 0x42,
        Keys.F9 => 0x43, Keys.F10 => 0x44,
        Keys.Home => 0x47, Keys.Up => 0x48, Keys.PageUp => 0x49,
        Keys.Left => 0x4B, Keys.Right => 0x4D,
        Keys.End => 0x4F, Keys.Down => 0x50, Keys.PageDown => 0x51,
        Keys.Insert => 0x52, Keys.Delete => 0x53,
        _ => 0x00
    };

    private static DosMouseButton MapButton(MouseButtons button) => button switch
    {
        MouseButtons.Left => DosMouseButton.Left,
        MouseButtons.Right => DosMouseButton.Right,
        MouseButtons.Middle => DosMouseButton.Middle,
        _ => DosMouseButton.None,
    };
}
