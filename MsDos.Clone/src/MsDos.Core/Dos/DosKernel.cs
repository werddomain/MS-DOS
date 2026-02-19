using MsDos.Core.Cpu;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Dos;

/// <summary>
/// DOS INT 21h service implementation. Provides file I/O, console I/O,
/// process management, and memory allocation services.
/// </summary>
public sealed class DosKernel
{
    private readonly Cpu8086 _cpu;
    private readonly MemoryBus _mem;
    private readonly IStreamProvider _streams;
    private readonly IEventRegistry _events;
    private readonly IGraphicsRenderer _renderer;
    private readonly Interrupts.BiosVideoService _video;

    // DOS version to report
    private const byte MajorVersion = 4;
    private const byte MinorVersion = 0;

    // File handle table (5 standard + user files)
    private readonly Dictionary<ushort, DosFileHandle> _fileHandles = new();
    private ushort _nextHandle = 5; // 0-4 are reserved (stdin, stdout, stderr, stdaux, stdprn)

    // Return code from last terminated process
    private byte _lastReturnCode;

    // PSP segment of current process
    public ushort CurrentPSP { get; set; }

    // DTA (Disk Transfer Area) address
    public ushort DtaSegment { get; set; }
    public ushort DtaOffset { get; set; } = 0x0080;

    // Current default drive (0=A, 1=B, 2=C)
    private byte _currentDrive = 2; // C:

    // FindFirst/FindNext state
    private IReadOnlyList<string>? _findResults;
    private int _findIndex;
    private string _findPattern = "*.*";

    /// <summary>Trigger process termination from external callers (e.g., INT 20h).</summary>
    public void Terminate(byte code) => ProcessTerminated?.Invoke(code);

    /// <summary>Fired when a process terminates (INT 21h AH=4Ch).</summary>
    public event Action<byte>? ProcessTerminated;

    /// <summary>Fired when DOS needs to output a character to the console.</summary>
    public event Action<char>? ConsoleOutput;

    public DosKernel(
        Cpu8086 cpu,
        MemoryBus mem,
        IStreamProvider streams,
        IEventRegistry events,
        IGraphicsRenderer renderer,
        Interrupts.BiosVideoService video)
    {
        _cpu = cpu;
        _mem = mem;
        _streams = streams;
        _events = events;
        _renderer = renderer;
        _video = video;
    }

    public void Handle()
    {
        byte func = _cpu.Regs.AH;
        switch (func)
        {
            case 0x00: // Terminate program
                ProcessTerminated?.Invoke(0);
                break;

            case 0x01: // Read character with echo
                HandleCharInputWithEcho();
                break;

            case 0x02: // Display character
                OutputChar((char)_cpu.Regs.DL);
                break;

            case 0x06: // Direct console I/O
                HandleDirectConsoleIO();
                break;

            case 0x07: // Direct character input (no echo)
            case 0x08: // Character input (no echo)
                HandleCharInputNoEcho();
                break;

            case 0x09: // Display string (terminated by '$')
                HandleDisplayString();
                break;

            case 0x0A: // Buffered keyboard input
                HandleBufferedInput();
                break;

            case 0x0B: // Check standard input status
                _cpu.Regs.AL = (byte)(_events.IsKeyAvailable ? 0xFF : 0x00);
                break;

            case 0x0C: // Clear input buffer and invoke input
                // Clear buffer, then call function in AL
                _cpu.Regs.AH = _cpu.Regs.AL;
                Handle();
                break;

            case 0x0E: // Select disk
                _currentDrive = _cpu.Regs.DL;
                _cpu.Regs.AL = 26; // Number of logical drives
                break;

            case 0x19: // Get current default drive
                _cpu.Regs.AL = _currentDrive;
                break;

            case 0x1A: // Set DTA
                DtaSegment = _cpu.Regs.DS;
                DtaOffset = _cpu.Regs.DX;
                break;

            case 0x25: // Set interrupt vector
                SetInterruptVector();
                break;

            case 0x2A: // Get date
                HandleGetDate();
                break;

            case 0x2C: // Get time
                HandleGetTime();
                break;

            case 0x30: // Get DOS version
                _cpu.Regs.AL = MajorVersion;
                _cpu.Regs.AH = MinorVersion;
                _cpu.Regs.BX = 0;
                _cpu.Regs.CX = 0;
                break;

            case 0x33: // Get/set Ctrl-Break flag
                if (_cpu.Regs.AL == 0)
                    _cpu.Regs.DL = 0; // Ctrl-Break checking off
                break;

            case 0x35: // Get interrupt vector
                GetInterruptVector();
                break;

            case 0x3C: // Create file
                HandleCreateFile();
                break;

            case 0x3D: // Open file
                HandleOpenFile();
                break;

            case 0x3E: // Close file
                HandleCloseFile();
                break;

            case 0x3F: // Read file
                HandleReadFile();
                break;

            case 0x40: // Write file
                HandleWriteFile();
                break;

            case 0x41: // Delete file
                HandleDeleteFile();
                break;

            case 0x42: // Seek (LSEEK)
                HandleSeek();
                break;

            case 0x43: // Get/set file attributes
                _cpu.Regs.CX = 0x0020; // Archive bit set
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x44: // IOCTL
                HandleIoctl();
                break;

            case 0x47: // Get current directory
                HandleGetCurrentDir();
                break;

            case 0x48: // Allocate memory
                HandleAllocateMemory();
                break;

            case 0x49: // Free memory
                _cpu.Regs.Flags &= ~CpuFlags.Carry; // success
                break;

            case 0x4A: // Resize memory block
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x4B: // EXEC - load and execute program
                HandleExec();
                break;

            case 0x4C: // Terminate with return code
                _lastReturnCode = _cpu.Regs.AL;
                ProcessTerminated?.Invoke(_cpu.Regs.AL);
                break;

            case 0x4D: // Get return code
                _cpu.Regs.AX = _lastReturnCode;
                break;

            case 0x4E: // Find first matching file
                HandleFindFirst();
                break;

            case 0x4F: // Find next matching file
                HandleFindNext();
                break;

            case 0x50: // Set PSP segment
                CurrentPSP = _cpu.Regs.BX;
                break;

            case 0x51: // Get PSP segment
            case 0x62: // Get PSP address
                _cpu.Regs.BX = CurrentPSP;
                break;

            case 0x54: // Get verify flag
                _cpu.Regs.AL = 0; // Verify off
                break;

            case 0x56: // Rename file
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x57: // Get/set file date/time
                HandleFileDateTime();
                break;

            case 0x58: // Get/set memory allocation strategy
                if (_cpu.Regs.AL == 0)
                    _cpu.Regs.AX = 0; // First fit
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x59: // Get extended error information
                _cpu.Regs.AX = 0; // No error
                _cpu.Regs.BH = 0;
                _cpu.Regs.BL = 0;
                _cpu.Regs.CH = 0;
                break;

            case 0x36: // Get disk free space
                HandleGetDiskFreeSpace();
                break;

            case 0x2F: // Get DTA address
                _cpu.Regs.ES = DtaSegment;
                _cpu.Regs.BX = DtaOffset;
                break;

            default:
                System.Diagnostics.Debug.WriteLine($"Unhandled DOS INT 21h AH={func:X2}");
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
        }
    }

    private void OutputChar(char ch)
    {
        _video.TtyOutput(ch);
        ConsoleOutput?.Invoke(ch);
    }

    private void HandleCharInputWithEcho()
    {
        var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
        _cpu.Regs.AL = key.AsciiChar;
        OutputChar((char)key.AsciiChar);
    }

    private void HandleCharInputNoEcho()
    {
        var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
        _cpu.Regs.AL = key.AsciiChar;
    }

    private void HandleDirectConsoleIO()
    {
        if (_cpu.Regs.DL == 0xFF)
        {
            // Input
            if (_events.IsKeyAvailable)
            {
                var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
                _cpu.Regs.AL = key.AsciiChar;
                _cpu.Regs.Flags &= ~CpuFlags.Zero;
            }
            else
            {
                _cpu.Regs.AL = 0;
                _cpu.Regs.Flags |= CpuFlags.Zero;
            }
        }
        else
        {
            // Output
            OutputChar((char)_cpu.Regs.DL);
        }
    }

    private void HandleDisplayString()
    {
        ushort seg = _cpu.Regs.DS;
        ushort off = _cpu.Regs.DX;
        while (true)
        {
            byte ch = _mem.ReadByte(seg, off);
            if (ch == (byte)'$') break;
            OutputChar((char)ch);
            off++;
        }
    }

    private void HandleBufferedInput()
    {
        ushort seg = _cpu.Regs.DS;
        ushort off = _cpu.Regs.DX;
        byte maxLen = _mem.ReadByte(seg, off);
        byte count = 0;

        for (int i = 0; i < maxLen - 1; i++)
        {
            var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
            if (key.AsciiChar == 0x0D) // Enter
            {
                OutputChar('\r');
                OutputChar('\n');
                break;
            }
            if (key.AsciiChar == 0x08 && count > 0) // Backspace
            {
                count--;
                OutputChar('\b');
                OutputChar(' ');
                OutputChar('\b');
                continue;
            }
            if (key.AsciiChar >= 0x20)
            {
                _mem.WriteByte(seg, (ushort)(off + 2 + count), key.AsciiChar);
                count++;
                OutputChar((char)key.AsciiChar);
            }
        }
        _mem.WriteByte(seg, (ushort)(off + 1), count);
        _mem.WriteByte(seg, (ushort)(off + 2 + count), 0x0D);
    }

    private void SetInterruptVector()
    {
        byte vector = _cpu.Regs.AL;
        uint ivtAddr = (uint)(vector * 4);
        _mem.WriteWord(ivtAddr, _cpu.Regs.DX);
        _mem.WriteWord(ivtAddr + 2, _cpu.Regs.DS);
    }

    private void GetInterruptVector()
    {
        byte vector = _cpu.Regs.AL;
        uint ivtAddr = (uint)(vector * 4);
        _cpu.Regs.BX = _mem.ReadWord(ivtAddr);
        _cpu.Regs.ES = _mem.ReadWord(ivtAddr + 2);
    }

    private void HandleGetDate()
    {
        var now = DateTime.Now;
        _cpu.Regs.CX = (ushort)now.Year;
        _cpu.Regs.DH = (byte)now.Month;
        _cpu.Regs.DL = (byte)now.Day;
        _cpu.Regs.AL = (byte)now.DayOfWeek;
    }

    private void HandleGetTime()
    {
        var now = DateTime.Now;
        _cpu.Regs.CH = (byte)now.Hour;
        _cpu.Regs.CL = (byte)now.Minute;
        _cpu.Regs.DH = (byte)now.Second;
        _cpu.Regs.DL = (byte)(now.Millisecond / 10);
    }

    private string ReadDosString(ushort segment, ushort offset)
    {
        var chars = new List<char>();
        while (true)
        {
            byte b = _mem.ReadByte(segment, offset++);
            if (b == 0) break;
            chars.Add((char)b);
        }
        return new string(chars.ToArray());
    }

    private void HandleCreateFile()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        try
        {
            var stream = _streams.OpenWriteAsync(path).GetAwaiter().GetResult();
            ushort handle = _nextHandle++;
            _fileHandles[handle] = new DosFileHandle(path, stream);
            _cpu.Regs.AX = handle;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x05; // Access denied
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleOpenFile()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        try
        {
            Stream stream;
            byte mode = (byte)(_cpu.Regs.AL & 3);
            stream = mode switch
            {
                0 => _streams.OpenReadAsync(path).GetAwaiter().GetResult(),
                1 => _streams.OpenWriteAsync(path).GetAwaiter().GetResult(),
                _ => _streams.OpenReadWriteAsync(path).GetAwaiter().GetResult()
            };

            ushort handle = _nextHandle++;
            _fileHandles[handle] = new DosFileHandle(path, stream);
            _cpu.Regs.AX = handle;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x02; // File not found
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleCloseFile()
    {
        ushort handle = _cpu.Regs.BX;
        if (_fileHandles.TryGetValue(handle, out var fh))
        {
            fh.Stream.Dispose();
            _fileHandles.Remove(handle);
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        else
        {
            _cpu.Regs.AX = 0x06; // Invalid handle
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleReadFile()
    {
        ushort handle = _cpu.Regs.BX;
        ushort count = _cpu.Regs.CX;
        ushort bufSeg = _cpu.Regs.DS;
        ushort bufOff = _cpu.Regs.DX;

        if (handle == 0) // stdin
        {
            var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
            _mem.WriteByte(bufSeg, bufOff, key.AsciiChar);
            _cpu.Regs.AX = 1;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
            return;
        }

        if (_fileHandles.TryGetValue(handle, out var fh))
        {
            try
            {
                var buffer = new byte[count];
                int read = fh.Stream.Read(buffer, 0, count);
                for (int i = 0; i < read; i++)
                    _mem.WriteByte(bufSeg, (ushort)(bufOff + i), buffer[i]);
                _cpu.Regs.AX = (ushort)read;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
            }
            catch
            {
                _cpu.Regs.AX = 0x05;
                _cpu.Regs.Flags |= CpuFlags.Carry;
            }
        }
        else
        {
            _cpu.Regs.AX = 0x06;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleWriteFile()
    {
        ushort handle = _cpu.Regs.BX;
        ushort count = _cpu.Regs.CX;
        ushort bufSeg = _cpu.Regs.DS;
        ushort bufOff = _cpu.Regs.DX;

        // stdout (1) or stderr (2)
        if (handle is 1 or 2)
        {
            for (int i = 0; i < count; i++)
            {
                byte ch = _mem.ReadByte(bufSeg, (ushort)(bufOff + i));
                OutputChar((char)ch);
            }
            _cpu.Regs.AX = count;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
            return;
        }

        if (_fileHandles.TryGetValue(handle, out var fh))
        {
            try
            {
                var buffer = new byte[count];
                for (int i = 0; i < count; i++)
                    buffer[i] = _mem.ReadByte(bufSeg, (ushort)(bufOff + i));
                fh.Stream.Write(buffer, 0, count);
                _cpu.Regs.AX = count;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
            }
            catch
            {
                _cpu.Regs.AX = 0x05;
                _cpu.Regs.Flags |= CpuFlags.Carry;
            }
        }
        else
        {
            _cpu.Regs.AX = 0x06;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleDeleteFile()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        try
        {
            _streams.DeleteAsync(path).GetAwaiter().GetResult();
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x02;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleSeek()
    {
        ushort handle = _cpu.Regs.BX;
        if (!_fileHandles.TryGetValue(handle, out var fh))
        {
            _cpu.Regs.AX = 0x06;
            _cpu.Regs.Flags |= CpuFlags.Carry;
            return;
        }

        long offset = (int)((_cpu.Regs.CX << 16) | _cpu.Regs.DX);
        SeekOrigin origin = _cpu.Regs.AL switch
        {
            0 => SeekOrigin.Begin,
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => SeekOrigin.Begin
        };

        long newPos = fh.Stream.Seek(offset, origin);
        _cpu.Regs.DX = (ushort)(newPos >> 16);
        _cpu.Regs.AX = (ushort)(newPos & 0xFFFF);
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleIoctl()
    {
        byte subfunc = _cpu.Regs.AL;
        ushort handle = _cpu.Regs.BX;

        switch (subfunc)
        {
            case 0x00: // Get device information
                if (handle <= 2)
                    _cpu.Regs.DX = 0x80D3; // Console device
                else
                    _cpu.Regs.DX = 0x0000; // File device
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            default:
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
        }
    }

    private void HandleGetCurrentDir()
    {
        // Return root directory "\"
        ushort seg = _cpu.Regs.DS;
        ushort off = _cpu.Regs.SI;
        _mem.WriteByte(seg, off, 0); // Empty string = root
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleAllocateMemory()
    {
        // Simplified: always allocate from a high segment
        ushort paragraphs = _cpu.Regs.BX;
        // Return a segment above the current program
        _cpu.Regs.AX = (ushort)(CurrentPSP + 0x1000);
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleExec()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        System.Diagnostics.Debug.WriteLine($"DOS EXEC: {path}");
        // TODO: Implement child process loading
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleGetDiskFreeSpace()
    {
        // Return simulated 32 MB free space
        // DL = drive number (0=default, 1=A, 2=B, 3=C, ...)
        _cpu.Regs.AX = 64;     // Sectors per cluster
        _cpu.Regs.BX = 1024;   // Number of available clusters
        _cpu.Regs.CX = 512;    // Bytes per sector
        _cpu.Regs.DX = 2048;   // Total clusters on drive
    }

    private void HandleFindFirst()
    {
        string pattern = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        _findPattern = pattern;
        _findIndex = 0;

        try
        {
            // Extract directory part and filename pattern
            string dir = "";
            string filePattern = pattern;
            int lastSep = pattern.LastIndexOfAny(new[] { '\\', '/' });
            if (lastSep >= 0)
            {
                dir = pattern[..lastSep];
                filePattern = pattern[(lastSep + 1)..];
            }

            var entries = _streams.ListEntriesAsync(dir).GetAwaiter().GetResult();

            // Filter by pattern (simple wildcard matching)
            _findResults = entries
                .Where(e => MatchWildcard(filePattern, Path.GetFileName(e)))
                .ToList();

            if (_findResults.Count > 0)
            {
                WriteFindResult(_findResults[0]);
                _findIndex = 1;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
            }
            else
            {
                _cpu.Regs.AX = 0x12; // No more files
                _cpu.Regs.Flags |= CpuFlags.Carry;
            }
        }
        catch
        {
            _cpu.Regs.AX = 0x12;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleFindNext()
    {
        if (_findResults != null && _findIndex < _findResults.Count)
        {
            WriteFindResult(_findResults[_findIndex]);
            _findIndex++;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        else
        {
            _cpu.Regs.AX = 0x12;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    /// <summary>Write a FindFirst/FindNext result to the DTA.</summary>
    private void WriteFindResult(string filename)
    {
        // DTA structure (43 bytes):
        // 00-14h: Reserved for FindNext
        // 15h: File attribute
        // 16h-17h: File time
        // 18h-19h: File date
        // 1Ah-1Dh: File size (DWORD)
        // 1Eh-2Ah: Filename (ASCIIZ, 13 bytes)

        ushort seg = DtaSegment;
        ushort off = DtaOffset;

        // Clear DTA
        for (int i = 0; i < 43; i++)
            _mem.WriteByte(seg, (ushort)(off + i), 0);

        // File attribute (0x20 = archive)
        _mem.WriteByte(seg, (ushort)(off + 0x15), 0x20);

        // File time/date (current time)
        var now = DateTime.Now;
        ushort time = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
        ushort date = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);
        _mem.WriteWord(Cpu.Registers.PhysicalAddress(seg, (ushort)(off + 0x16)), time);
        _mem.WriteWord(Cpu.Registers.PhysicalAddress(seg, (ushort)(off + 0x18)), date);

        // File size (0 for simplicity, or try to get real size)
        _mem.WriteWord(Cpu.Registers.PhysicalAddress(seg, (ushort)(off + 0x1A)), 0);
        _mem.WriteWord(Cpu.Registers.PhysicalAddress(seg, (ushort)(off + 0x1C)), 0);

        // Filename (up to 12 chars + null, DOS 8.3 format)
        string name = Path.GetFileName(filename).ToUpperInvariant();
        if (name.Length > 12) name = name[..12];
        for (int i = 0; i < name.Length; i++)
            _mem.WriteByte(seg, (ushort)(off + 0x1E + i), (byte)name[i]);
        _mem.WriteByte(seg, (ushort)(off + 0x1E + name.Length), 0);
    }

    /// <summary>Simple wildcard pattern matching (supports * and ?).</summary>
    internal static bool MatchWildcard(string pattern, string text)
    {
        if (pattern == "*.*" || pattern == "*") return true;

        pattern = pattern.ToUpperInvariant();
        text = text.ToUpperInvariant();

        int pi = 0, ti = 0;
        int starPi = -1, starTi = -1;

        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || pattern[pi] == text[ti]))
            {
                pi++;
                ti++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                starPi = pi;
                starTi = ti;
                pi++;
            }
            else if (starPi >= 0)
            {
                pi = starPi + 1;
                starTi++;
                ti = starTi;
            }
            else
            {
                return false;
            }
        }

        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }

    private void HandleFileDateTime()
    {
        if (_cpu.Regs.AL == 0) // Get
        {
            // Return current time in DOS packed format
            var now = DateTime.Now;
            _cpu.Regs.CX = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
            _cpu.Regs.DX = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        else
        {
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
    }

    private sealed class DosFileHandle
    {
        public string Path { get; }
        public Stream Stream { get; }
        public DosFileHandle(string path, Stream stream) { Path = path; Stream = stream; }
    }
}
