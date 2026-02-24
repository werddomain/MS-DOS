using System.Collections.Concurrent;
using System.Threading.Channels;
using MsDos.Core.Platform;

namespace MsDos.Blazor.Platform;

/// <summary>
/// Blazor WebAssembly implementation of IEventRegistry.
/// Events are pushed from JavaScript via interop.
/// </summary>
public sealed class BlazorEventRegistry : IEventRegistry
{
    private readonly Channel<DosKeyEventArgs> _keyChannel = Channel.CreateUnbounded<DosKeyEventArgs>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private int _pendingKeyCount;

    public event Action<DosKeyEventArgs>? KeyDown;
    public event Action<DosKeyEventArgs>? KeyUp;
    public event Action<DosMouseEventArgs>? MouseMove;
    public event Action<DosMouseEventArgs>? MouseDown;
    public event Action<DosMouseEventArgs>? MouseUp;

    public bool IsKeyAvailable => Volatile.Read(ref _pendingKeyCount) > 0;

    public async Task<DosKeyEventArgs> ReadKeyAsync(CancellationToken cancellationToken = default)
    {
        var key = await _keyChannel.Reader.ReadAsync(cancellationToken);
        Interlocked.Decrement(ref _pendingKeyCount);
        return key;
    }

    /// <summary>Called from Blazor component when a key is pressed.</summary>
    public void OnKeyDown(string key, string code, bool shift, bool ctrl, bool alt)
    {
        var dosKey = new DosKeyEventArgs
        {
            AsciiChar = MapAscii(key),
            ScanCode = MapScanCode(code),
            Shift = shift,
            Ctrl = ctrl,
            Alt = alt,
        };
        _keyChannel.Writer.TryWrite(dosKey);
        Interlocked.Increment(ref _pendingKeyCount);
        KeyDown?.Invoke(dosKey);
    }

    /// <summary>Called from Blazor component when a key is released.</summary>
    public void OnKeyUp(string key, string code, bool shift, bool ctrl, bool alt)
    {
        KeyUp?.Invoke(new DosKeyEventArgs
        {
            AsciiChar = MapAscii(key),
            ScanCode = MapScanCode(code),
            Shift = shift,
            Ctrl = ctrl,
            Alt = alt,
        });
    }

    private static byte MapAscii(string key)
    {
        if (key.Length == 1) return (byte)key[0];
        return key switch
        {
            "Enter" => 0x0D,
            "Escape" => 0x1B,
            "Backspace" => 0x08,
            "Tab" => 0x09,
            " " => 0x20,
            _ => 0x00
        };
    }

    private static byte MapScanCode(string code) => code switch
    {
        "Escape" => 0x01,
        "Digit1" => 0x02, "Digit2" => 0x03, "Digit3" => 0x04, "Digit4" => 0x05,
        "Digit5" => 0x06, "Digit6" => 0x07, "Digit7" => 0x08, "Digit8" => 0x09,
        "Digit9" => 0x0A, "Digit0" => 0x0B,
        "Backspace" => 0x0E,
        "Tab" => 0x0F,
        "KeyQ" => 0x10, "KeyW" => 0x11, "KeyE" => 0x12, "KeyR" => 0x13,
        "KeyT" => 0x14, "KeyY" => 0x15, "KeyU" => 0x16, "KeyI" => 0x17,
        "KeyO" => 0x18, "KeyP" => 0x19,
        "Enter" => 0x1C,
        "KeyA" => 0x1E, "KeyS" => 0x1F, "KeyD" => 0x20, "KeyF" => 0x21,
        "KeyG" => 0x22, "KeyH" => 0x23, "KeyJ" => 0x24, "KeyK" => 0x25,
        "KeyL" => 0x26,
        "KeyZ" => 0x2C, "KeyX" => 0x2D, "KeyC" => 0x2E, "KeyV" => 0x2F,
        "KeyB" => 0x30, "KeyN" => 0x31, "KeyM" => 0x32,
        "Space" => 0x39,
        "F1" => 0x3B, "F2" => 0x3C, "F3" => 0x3D, "F4" => 0x3E,
        "F5" => 0x3F, "F6" => 0x40, "F7" => 0x41, "F8" => 0x42,
        "F9" => 0x43, "F10" => 0x44,
        "ArrowUp" => 0x48, "ArrowLeft" => 0x4B, "ArrowRight" => 0x4D, "ArrowDown" => 0x50,
        _ => 0x00
    };
}
