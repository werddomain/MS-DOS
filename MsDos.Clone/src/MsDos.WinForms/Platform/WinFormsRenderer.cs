using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using MsDos.Core.Platform;

namespace MsDos.WinForms.Platform;

/// <summary>
/// WinForms implementation of IGraphicsRenderer using a PictureBox
/// with an off-screen Bitmap buffer for text and graphics rendering.
/// </summary>
public sealed class WinFormsRenderer : IGraphicsRenderer, IDisposable
{
    private readonly PictureBox _pictureBox;
    private Bitmap _buffer;
    private Graphics _gfx;
    private Font _font;

    // Text mode dimensions
    private int _textCols = 80;
    private int _textRows = 25;
    private int _charWidth = 8;
    private int _charHeight = 16;

    // Character buffer for scrolling
    private char[,] _charBuffer;
    private byte[,] _fgBuffer;
    private byte[,] _bgBuffer;

    // Cursor state
    private int _cursorCol;
    private int _cursorRow;
    private bool _cursorVisible = true;

    private VideoMode _currentMode = VideoMode.Text80x25;

    public int Width => _buffer.Width;
    public int Height => _buffer.Height;

    public WinFormsRenderer(PictureBox pictureBox)
    {
        _pictureBox = pictureBox;
        _charBuffer = new char[80, 25];
        _fgBuffer = new byte[80, 25];
        _bgBuffer = new byte[80, 25];
        _font = new Font("Consolas", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        _buffer = new Bitmap(640, 400);
        _gfx = Graphics.FromImage(_buffer);
        _gfx.TextRenderingHint = TextRenderingHint.SingleBitPerPixel;
        _gfx.InterpolationMode = InterpolationMode.NearestNeighbor;
        _gfx.Clear(Color.Black);
        _pictureBox.Image = _buffer;
        _pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
    }

    public void SetMode(VideoMode mode)
    {
        _currentMode = mode;
        switch (mode)
        {
            case VideoMode.Text80x25:
                _textCols = 80;
                _textRows = 25;
                ResizeBuffer(640, 400);
                break;
            case VideoMode.Graphics320x200_4:
            case VideoMode.Graphics320x200_256:
                ResizeBuffer(320, 200);
                break;
            case VideoMode.Graphics640x200_2:
                ResizeBuffer(640, 200);
                break;
        }
        _charBuffer = new char[_textCols, _textRows];
        _fgBuffer = new byte[_textCols, _textRows];
        _bgBuffer = new byte[_textCols, _textRows];
    }

    private void ResizeBuffer(int w, int h)
    {
        var old = _buffer;
        _gfx.Dispose();
        _buffer = new Bitmap(w, h);
        _gfx = Graphics.FromImage(_buffer);
        _gfx.TextRenderingHint = TextRenderingHint.SingleBitPerPixel;
        _gfx.InterpolationMode = InterpolationMode.NearestNeighbor;
        _gfx.Clear(Color.Black);
        _pictureBox.Image = _buffer;
        old.Dispose();
        _charWidth = w / _textCols;
        _charHeight = h / _textRows;
    }

    public void DrawCharacter(int col, int row, char character, byte foreground, byte background)
    {
        if (col < 0 || col >= _textCols || row < 0 || row >= _textRows) return;

        _charBuffer[col, row] = character;
        _fgBuffer[col, row] = foreground;
        _bgBuffer[col, row] = background;

        var bgColor = ToColor(background);
        var fgColor = ToColor(foreground);

        int x = col * _charWidth;
        int y = row * _charHeight;

        using var bgBrush = new SolidBrush(bgColor);
        using var fgBrush = new SolidBrush(fgColor);
        _gfx.FillRectangle(bgBrush, x, y, _charWidth, _charHeight);

        if (character > ' ')
        {
            _gfx.DrawString(character.ToString(), _font, fgBrush, x, y,
                new StringFormat { FormatFlags = StringFormatFlags.NoWrap });
        }
    }

    public void DrawPixel(int x, int y, byte colorIndex)
    {
        if (x < 0 || x >= _buffer.Width || y < 0 || y >= _buffer.Height) return;
        _buffer.SetPixel(x, y, ToColor(colorIndex));
    }

    public void Clear(byte backgroundColorIndex)
    {
        _gfx.Clear(ToColor(backgroundColorIndex));
        if (_charBuffer != null)
        {
            Array.Clear(_charBuffer, 0, _charBuffer.Length);
            Array.Clear(_fgBuffer, 0, _fgBuffer.Length);
            Array.Clear(_bgBuffer, 0, _bgBuffer.Length);
        }
    }

    public void SetCursorPosition(int col, int row)
    {
        _cursorCol = col;
        _cursorRow = row;
    }

    public void SetCursorVisible(bool visible)
    {
        _cursorVisible = visible;
    }

    public void ScrollUp(int lines, byte backgroundColorIndex)
    {
        if (lines <= 0) return;

        // Shift character buffers
        for (int row = lines; row < _textRows; row++)
        {
            for (int col = 0; col < _textCols; col++)
            {
                _charBuffer[col, row - lines] = _charBuffer[col, row];
                _fgBuffer[col, row - lines] = _fgBuffer[col, row];
                _bgBuffer[col, row - lines] = _bgBuffer[col, row];
            }
        }

        // Clear bottom lines
        for (int row = _textRows - lines; row < _textRows; row++)
        {
            for (int col = 0; col < _textCols; col++)
            {
                _charBuffer[col, row] = '\0';
                _fgBuffer[col, row] = 7;
                _bgBuffer[col, row] = backgroundColorIndex;
            }
        }

        // Redraw everything
        RedrawAll(backgroundColorIndex);
    }

    public void ScrollDown(int lines, byte backgroundColorIndex)
    {
        if (lines <= 0) return;

        // Shift character buffers down
        for (int row = _textRows - 1 - lines; row >= 0; row--)
        {
            for (int col = 0; col < _textCols; col++)
            {
                _charBuffer[col, row + lines] = _charBuffer[col, row];
                _fgBuffer[col, row + lines] = _fgBuffer[col, row];
                _bgBuffer[col, row + lines] = _bgBuffer[col, row];
            }
        }

        // Clear top lines
        for (int row = 0; row < lines && row < _textRows; row++)
        {
            for (int col = 0; col < _textCols; col++)
            {
                _charBuffer[col, row] = '\0';
                _fgBuffer[col, row] = 7;
                _bgBuffer[col, row] = backgroundColorIndex;
            }
        }

        RedrawAll(backgroundColorIndex);
    }

    private void RedrawAll(byte defaultBg)
    {
        _gfx.Clear(ToColor(defaultBg));
        for (int row = 0; row < _textRows; row++)
        {
            for (int col = 0; col < _textCols; col++)
            {
                char ch = _charBuffer[col, row];
                byte fg = _fgBuffer[col, row];
                byte bg = _bgBuffer[col, row];
                if (ch != '\0' || bg != defaultBg)
                {
                    int x = col * _charWidth;
                    int y = row * _charHeight;
                    using var bgBrush = new SolidBrush(ToColor(bg));
                    _gfx.FillRectangle(bgBrush, x, y, _charWidth, _charHeight);
                    if (ch > ' ')
                    {
                        using var fgBrush = new SolidBrush(ToColor(fg));
                        _gfx.DrawString(ch.ToString(), _font, fgBrush, x, y);
                    }
                }
            }
        }
    }

    public Task FlushAsync()
    {
        // Draw cursor
        if (_cursorVisible && _cursorCol < _textCols && _cursorRow < _textRows)
        {
            int cx = _cursorCol * _charWidth;
            int cy = _cursorRow * _charHeight + _charHeight - 2;
            using var cursorBrush = new SolidBrush(Color.White);
            _gfx.FillRectangle(cursorBrush, cx, cy, _charWidth, 2);
        }

        if (_pictureBox.InvokeRequired)
            _pictureBox.Invoke(() => _pictureBox.Refresh());
        else
            _pictureBox.Refresh();

        return Task.CompletedTask;
    }

    private static Color ToColor(byte index)
    {
        if (index >= DosColor.Palette.Length) index = 0;
        var dc = DosColor.Palette[index];
        return Color.FromArgb(dc.R, dc.G, dc.B);
    }

    public void Dispose()
    {
        _gfx.Dispose();
        _buffer.Dispose();
        _font.Dispose();
    }
}
