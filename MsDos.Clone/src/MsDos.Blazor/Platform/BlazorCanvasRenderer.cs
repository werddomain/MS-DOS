using Microsoft.JSInterop;
using MsDos.Core.Platform;

namespace MsDos.Blazor.Platform;

/// <summary>
/// Blazor WebAssembly implementation of IGraphicsRenderer using HTML5 Canvas
/// via JavaScript interop.
/// </summary>
public sealed class BlazorCanvasRenderer : IGraphicsRenderer
{
    private readonly IJSRuntime _js;
    private string _canvasId;
    private int _textCols = 80;
    private int _textRows = 25;

    // Off-screen character buffer (sent to JS in batch)
    private char[,] _charBuffer;
    private byte[,] _fgBuffer;
    private byte[,] _bgBuffer;
    private bool _dirty;
    private int _cursorCol;
    private int _cursorRow;
    private bool _cursorVisible = true;
    private VideoMode _mode = VideoMode.Text80x25;

    public int Width { get; private set; } = 640;
    public int Height { get; private set; } = 400;

    public BlazorCanvasRenderer(IJSRuntime js, string canvasId = "dosCanvas")
    {
        _js = js;
        _canvasId = canvasId;
        _charBuffer = new char[80, 25];
        _fgBuffer = new byte[80, 25];
        _bgBuffer = new byte[80, 25];
    }

    public void SetMode(VideoMode mode)
    {
        _mode = mode;
        switch (mode)
        {
            case VideoMode.Text80x25:
                _textCols = 80; _textRows = 25;
                Width = 640; Height = 400;
                break;
            case VideoMode.Graphics320x200_4:
            case VideoMode.Graphics320x200_256:
                Width = 320; Height = 200;
                break;
            case VideoMode.Graphics640x200_2:
                Width = 640; Height = 200;
                break;
        }
        _charBuffer = new char[_textCols, _textRows];
        _fgBuffer = new byte[_textCols, _textRows];
        _bgBuffer = new byte[_textCols, _textRows];
        _dirty = true;
    }

    public void DrawCharacter(int col, int row, char character, byte foreground, byte background)
    {
        if (col < 0 || col >= _textCols || row < 0 || row >= _textRows) return;
        _charBuffer[col, row] = character;
        _fgBuffer[col, row] = foreground;
        _bgBuffer[col, row] = background;
        _dirty = true;
    }

    public void DrawPixel(int x, int y, byte colorIndex)
    {
        _dirty = true;
    }

    public void Clear(byte backgroundColorIndex)
    {
        Array.Clear(_charBuffer, 0, _charBuffer.Length);
        Array.Clear(_fgBuffer, 0, _fgBuffer.Length);
        for (int r = 0; r < _textRows; r++)
            for (int c = 0; c < _textCols; c++)
                _bgBuffer[c, r] = backgroundColorIndex;
        _dirty = true;
    }

    public void SetCursorPosition(int col, int row)
    {
        _cursorCol = col;
        _cursorRow = row;
        _dirty = true;
    }

    public void SetCursorVisible(bool visible)
    {
        _cursorVisible = visible;
    }

    public void ScrollUp(int lines, byte backgroundColorIndex)
    {
        if (lines <= 0) return;
        for (int row = lines; row < _textRows; row++)
        {
            for (int col = 0; col < _textCols; col++)
            {
                _charBuffer[col, row - lines] = _charBuffer[col, row];
                _fgBuffer[col, row - lines] = _fgBuffer[col, row];
                _bgBuffer[col, row - lines] = _bgBuffer[col, row];
            }
        }
        for (int row = _textRows - lines; row < _textRows; row++)
        {
            for (int col = 0; col < _textCols; col++)
            {
                _charBuffer[col, row] = '\0';
                _fgBuffer[col, row] = 7;
                _bgBuffer[col, row] = backgroundColorIndex;
            }
        }
        _dirty = true;
    }

    public async Task FlushAsync()
    {
        if (!_dirty) return;
        _dirty = false;

        try
        {
            // Build a serializable screen state
            var screenData = new string[_textRows];
            var fgData = new byte[_textRows][];
            var bgData = new byte[_textRows][];

            for (int row = 0; row < _textRows; row++)
            {
                var chars = new char[_textCols];
                fgData[row] = new byte[_textCols];
                bgData[row] = new byte[_textCols];
                for (int col = 0; col < _textCols; col++)
                {
                    chars[col] = _charBuffer[col, row] == '\0' ? ' ' : _charBuffer[col, row];
                    fgData[row][col] = _fgBuffer[col, row];
                    bgData[row][col] = _bgBuffer[col, row];
                }
                screenData[row] = new string(chars);
            }

            await _js.InvokeVoidAsync("dosRenderer.renderScreen", _canvasId,
                screenData, fgData, bgData,
                _cursorCol, _cursorRow, _cursorVisible);
        }
        catch (JSDisconnectedException)
        {
            // Component disposed
        }
    }
}
