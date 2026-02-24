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
    private readonly EmulatorLog _log;

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

    // Drive provider map for resolving paths on different drives
    private readonly Dictionary<char, IStreamProvider> _driveProviders = new(new CharOrdinalIgnoreCaseComparer());

    // Current Working Directory per drive (DOS tracks CWD per drive)
    private readonly Dictionary<char, string> _currentDirectories = new(new CharOrdinalIgnoreCaseComparer());

    // Memory manager (MCB chain)
    private MemoryManager? _memoryManager;

    // Extended error tracking
    private ushort _lastError;
    private byte _lastErrorClass;
    private byte _lastErrorAction;
    private byte _lastErrorLocus;

    // Ctrl-C check flag
    private byte _ctrlCFlag;

    // Buffered console input state for AH=0Ah when waiting for additional keys
    private bool _bufferedInputActive;
    private ushort _bufferedInputSeg;
    private ushort _bufferedInputOff;
    private byte _bufferedInputMaxLen;
    private byte _bufferedInputCount;

    // Verify flag  
    private byte _verifyFlag;

    /// <summary>Environment segment for the current process.</summary>
    public ushort EnvironmentSegment { get; set; }

    /// <summary>Optional callback invoked on file I/O operations (for disk activity LED).</summary>
    public Action? OnFileIo { get; set; }

    /// <summary>Attach the memory manager for INT 21h/48-4Ah.</summary>
    public void SetMemoryManager(MemoryManager manager) => _memoryManager = manager;

    /// <summary>
    /// Register a drive letter with its stream provider so the kernel can resolve file paths.
    /// </summary>
    public void SetDriveProvider(char driveLetter, IStreamProvider provider)
    {
        _driveProviders[char.ToUpperInvariant(driveLetter)] = provider;
    }

    /// <summary>
    /// Remove a drive letter mapping (e.g., when ejecting a floppy).
    /// </summary>
    public void RemoveDriveProvider(char driveLetter)
    {
        _driveProviders.Remove(char.ToUpperInvariant(driveLetter));
    }

    /// <summary>
    /// Set the current default drive number (0=A, 1=B, 2=C...).
    /// </summary>
    public void SetCurrentDrive(byte driveNumber)
    {
        _currentDrive = driveNumber;
    }

    /// <summary>
    /// Resolve a DOS file path to the correct stream provider.
    /// If the path has a drive letter prefix (e.g., "A:\FILE.EXE"), uses that drive's provider.
    /// Otherwise uses the current drive's provider, falling back to the default.
    /// Returns the provider and the path with the drive prefix stripped.
    /// </summary>
    private (IStreamProvider provider, string resolvedPath) ResolveFilePath(string path)
    {
        // Check for drive letter prefix (e.g., "A:" or "A:\")
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            char drive = char.ToUpperInvariant(path[0]);
            string subPath = path.Length > 2 ? path[2..].TrimStart('\\', '/') : "";
            if (_driveProviders.TryGetValue(drive, out var driveProvider))
                return (driveProvider, subPath);
        }

        // Use current drive's provider if available
        char currentDriveLetter = (char)('A' + _currentDrive);
        if (_driveProviders.TryGetValue(currentDriveLetter, out var currentProvider))
            return (currentProvider, path);

        // Fall back to default stream provider
        return (_streams, path);
    }

    private sealed class CharOrdinalIgnoreCaseComparer : IEqualityComparer<char>
    {
        public bool Equals(char x, char y) => char.ToUpperInvariant(x) == char.ToUpperInvariant(y);
        public int GetHashCode(char obj) => char.ToUpperInvariant(obj).GetHashCode();
    }

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
        Interrupts.BiosVideoService video,
        EmulatorLog log)
    {
        _cpu = cpu;
        _mem = mem;
        _streams = streams;
        _events = events;
        _renderer = renderer;
        _video = video;
        _log = log;
    }

    public void Handle()
    {
        byte func = _cpu.Regs.AH;

        // Notify disk activity for file I/O functions
        if (func is (>= 0x3C and <= 0x46) or 0x4E or 0x4F or 0x56 or 0x5A or 0x5B or 0x6C)
            OnFileIo?.Invoke();

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
                    _cpu.Regs.DL = _ctrlCFlag;
                else if (_cpu.Regs.AL == 1)
                    _ctrlCFlag = _cpu.Regs.DL;
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
                HandleFreeMemory();
                break;

            case 0x4A: // Resize memory block
                HandleResizeMemory();
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
                _cpu.Regs.AL = _verifyFlag;
                break;

            case 0x56: // Rename file
                HandleRenameFile();
                break;

            case 0x57: // Get/set file date/time
                HandleFileDateTime();
                break;

            case 0x58: // Get/set memory allocation strategy
                if (_cpu.Regs.AL == 0) // Get
                {
                    _cpu.Regs.AX = _memoryManager?.AllocationStrategy ?? 0;
                    _cpu.Regs.Flags &= ~CpuFlags.Carry;
                }
                else if (_cpu.Regs.AL == 1) // Set
                {
                    if (_memoryManager != null)
                        _memoryManager.AllocationStrategy = (byte)_cpu.Regs.BX;
                    _cpu.Regs.Flags &= ~CpuFlags.Carry;
                }
                break;

            case 0x59: // Get extended error information
                _cpu.Regs.AX = _lastError;
                _cpu.Regs.BH = _lastErrorClass;
                _cpu.Regs.BL = _lastErrorAction;
                _cpu.Regs.CH = _lastErrorLocus;
                break;

            case 0x36: // Get disk free space
                HandleGetDiskFreeSpace();
                break;

            case 0x2F: // Get DTA address
                _cpu.Regs.ES = DtaSegment;
                _cpu.Regs.BX = DtaOffset;
                break;

            case 0x2E: // Set verify flag
                _verifyFlag = _cpu.Regs.AL;
                break;

            case 0x38: // Get/set country info
                if (_cpu.Regs.AL == 0) // Get
                {
                    // Write minimal country info to DS:DX
                    ushort seg = _cpu.Regs.DS;
                    ushort off = _cpu.Regs.DX;
                    _mem.WriteWord(seg, off, 0); // Date format (0=US)
                    _mem.WriteByte(seg, (ushort)(off + 2), (byte)'$'); // Currency symbol
                    _mem.WriteByte(seg, (ushort)(off + 3), 0);
                    _mem.WriteByte(seg, (ushort)(off + 7), (byte)','); // Thousands separator
                    _mem.WriteByte(seg, (ushort)(off + 8), 0);
                    _mem.WriteByte(seg, (ushort)(off + 9), (byte)'.'); // Decimal separator
                    _mem.WriteByte(seg, (ushort)(off + 10), 0);
                    _cpu.Regs.BX = 1; // Country code
                }
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x39: // Create directory (MKDIR)
                HandleMkdir();
                break;

            case 0x3A: // Remove directory (RMDIR)
                HandleRmdir();
                break;

            case 0x3B: // Change directory (CHDIR)
                HandleChdir();
                break;

            case 0x55: // Create child PSP
                _log.Debug("DOS", $"Create child PSP at {_cpu.Regs.DX:X4}");
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x63: // Get lead byte table (DBCS)
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x65: // Get extended country info
                _cpu.Regs.AX = 1; // Function not supported
                _cpu.Regs.Flags |= CpuFlags.Carry;
                break;

            case 0x66: // Get/set global code page
                _cpu.Regs.BX = 437; // US code page
                _cpu.Regs.DX = 437;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x0D: // Disk reset (flush buffers)
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x26: // Create new PSP (copy)
            {
                ushort newSeg = _cpu.Regs.DX;
                // Copy first 256 bytes of current PSP to new segment
                for (int i = 0; i < 256; i++)
                    _mem.WriteByte(newSeg, (ushort)i, _mem.ReadByte(CurrentPSP, (ushort)i));
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            }

            case 0x29: // Parse filename into FCB
                HandleParseFilename();
                break;

            case 0x2B: // Set date
                // Accept but ignore
                _cpu.Regs.AL = 0; // success
                break;

            case 0x2D: // Set time
                // Accept but ignore
                _cpu.Regs.AL = 0; // success
                break;

            case 0x34: // Get InDOS flag address
                // Return a fixed address in low memory where InDOS flag resides
                _cpu.Regs.ES = 0x0070;
                _cpu.Regs.BX = 0x0000;
                _mem.WriteByte(0x0070, 0x0000, 0); // InDOS flag = 0 (not in DOS)
                break;

            case 0x37: // Get/set switch character
                if (_cpu.Regs.AL == 0) // Get
                {
                    _cpu.Regs.DL = (byte)'/'; // Standard DOS switch char
                    _cpu.Regs.AL = 0;
                }
                else if (_cpu.Regs.AL == 1) // Set
                    _cpu.Regs.AL = 0; // Accept but ignore
                break;

            case 0x45: // Duplicate file handle (DUP)
                HandleDupHandle();
                break;

            case 0x46: // Force duplicate file handle (DUP2)
                HandleForceDupHandle();
                break;

            case 0x5A: // Create temporary file
                HandleCreateTempFile();
                break;

            case 0x5B: // Create new file (fail if exists)
                HandleCreateNewFile();
                break;

            case 0x60: // Canonicalize filename
                HandleCanonicalizePath();
                break;

            case 0x67: // Set handle count
                // Accept - we don't have a real limit
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x68: // Commit file (flush)
            {
                ushort handle = _cpu.Regs.BX;
                if (_fileHandles.TryGetValue(handle, out var fh))
                {
                    fh.Stream.Flush();
                    _cpu.Regs.Flags &= ~CpuFlags.Carry;
                }
                else
                {
                    _cpu.Regs.AX = 0x06;
                    _cpu.Regs.Flags |= CpuFlags.Carry;
                }
                break;
            }

            case 0x1B: // Get allocation info (default drive)
            case 0x1C: // Get allocation info (specific drive)
                _cpu.Regs.AL = 64;  // sectors per cluster
                _cpu.Regs.CX = 512; // bytes per sector
                _cpu.Regs.DX = 2048; // total clusters
                // DS:BX → pointer to media descriptor byte
                break;

            case 0x1F: // Get default DPB (Drive Parameter Block)
            case 0x32: // Get DPB for specific drive
            {
                // Return a minimal DPB at a fixed address
                ushort dpbSeg = 0x0080;
                ushort dpbOff = 0x0000;
                _mem.WriteByte(dpbSeg, dpbOff, _currentDrive);        // Drive number
                _mem.WriteByte(dpbSeg, (ushort)(dpbOff + 1), 0);      // Unit number
                _mem.WriteWord(dpbSeg, (ushort)(dpbOff + 2), 512);    // Bytes per sector
                _mem.WriteByte(dpbSeg, (ushort)(dpbOff + 4), 63);     // Sectors per cluster - 1
                _mem.WriteByte(dpbSeg, (ushort)(dpbOff + 5), 6);      // Cluster shift
                _mem.WriteWord(dpbSeg, (ushort)(dpbOff + 6), 1);      // Reserved sectors
                _mem.WriteByte(dpbSeg, (ushort)(dpbOff + 8), 2);      // Number of FATs
                _mem.WriteWord(dpbSeg, (ushort)(dpbOff + 9), 512);    // Max root directory entries
                _mem.WriteWord(dpbSeg, (ushort)(dpbOff + 0x0B), 2);   // First data sector
                _mem.WriteWord(dpbSeg, (ushort)(dpbOff + 0x0D), 2048);// Max cluster number
                _cpu.Regs.DS = dpbSeg;
                _cpu.Regs.BX = dpbOff;
                _cpu.Regs.AL = 0; // Success
                break;
            }

            case 0x03: // Auxiliary input
                _cpu.Regs.AL = 0;
                break;

            case 0x04: // Auxiliary output
                break;

            case 0x05: // Printer output
                break;

            case 0x10: // Close FCB file
            case 0x11: // Find first FCB
            case 0x12: // Find next FCB
            case 0x13: // Delete FCB
            case 0x14: // Sequential read FCB
            case 0x15: // Sequential write FCB
            case 0x16: // Create FCB
            case 0x17: // Rename FCB
            case 0x21: // Random read FCB
            case 0x22: // Random write FCB
            case 0x23: // Get file size FCB
            case 0x24: // Set random record FCB
            case 0x27: // Random block read FCB
            case 0x28: // Random block write FCB
            case 0x0F: // Open file using FCB
                // FCB operations — return success but don't actually do anything
                _cpu.Regs.AL = 0xFF; // FCB not found / error
                _log.Debug("DOS", $"FCB operation AH={func:X2}h (stub)");
                break;

            case 0x52: // Get DOS internal variables (List of Lists)
            {
                // Return pointer to a minimal LoL structure
                ushort lolSeg = 0x0080;
                ushort lolOff = 0x0020;
                // Write MCB chain start at offset -2
                if (_memoryManager != null)
                    _mem.WriteWord(lolSeg, (ushort)(lolOff - 2), _memoryManager.FirstMcb);
                _cpu.Regs.ES = lolSeg;
                _cpu.Regs.BX = lolOff;
                break;
            }

            case 0x53: // Translate BPB to DPB
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x5D: // Server function call (network)
                _cpu.Regs.AX = 1; // Not supported
                _cpu.Regs.Flags |= CpuFlags.Carry;
                break;

            case 0x5E: // Network operations
            case 0x5F: // Network redirector
                _cpu.Regs.AX = 0x01; // Function not supported  
                _cpu.Regs.Flags |= CpuFlags.Carry;
                break;

            case 0x6C: // Extended open/create (DOS 4.0+)
                HandleExtendedOpen();
                break;

            default:
                _log.Warn("DOS", $"Unhandled INT 21h AH={func:X2}h");
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
        }
    }

    private void OutputChar(char ch)
    {
        _video.TtyOutput(ch);
        ConsoleOutput?.Invoke(ch);
    }

    private void ReexecuteCurrentInterrupt()
    {
        // INT 21h is 2 bytes (CD 21). Rewind to emulate DOS blocking behavior
        // without blocking the host runtime thread.
        _cpu.Regs.IP -= 2;
        _cpu.IsWaitingForInput = true; // Signal execution loop to yield
    }

    private bool TryReadKeyNonBlocking(out DosKeyEventArgs key)
    {
        key = default;
        if (!_events.IsKeyAvailable)
            return false;

        var readTask = _events.ReadKeyAsync();
        if (!readTask.IsCompletedSuccessfully)
            return false;

        key = readTask.GetAwaiter().GetResult();
        return true;
    }

    private void HandleCharInputWithEcho()
    {
        if (!TryReadKeyNonBlocking(out var key))
        {
            ReexecuteCurrentInterrupt();
            return;
        }
        _cpu.Regs.AL = key.AsciiChar;
        OutputChar((char)key.AsciiChar);
    }

    private void HandleCharInputNoEcho()
    {
        if (!TryReadKeyNonBlocking(out var key))
        {
            ReexecuteCurrentInterrupt();
            return;
        }
        _cpu.Regs.AL = key.AsciiChar;
    }

    private void HandleDirectConsoleIO()
    {
        if (_cpu.Regs.DL == 0xFF)
        {
            // Input
            if (TryReadKeyNonBlocking(out var key))
            {
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
        if (!_bufferedInputActive)
        {
            _bufferedInputSeg = _cpu.Regs.DS;
            _bufferedInputOff = _cpu.Regs.DX;
            _bufferedInputMaxLen = _mem.ReadByte(_bufferedInputSeg, _bufferedInputOff);
            _bufferedInputCount = 0;
            _bufferedInputActive = true;
        }

        while (_bufferedInputCount < _bufferedInputMaxLen - 1)
        {
            if (!TryReadKeyNonBlocking(out var key))
            {
                // Keep function blocking until Enter by re-executing INT 21h AH=0Ah.
                ReexecuteCurrentInterrupt();
                return;
            }

            if (key.AsciiChar == 0x0D) // Enter
            {
                OutputChar('\r');
                OutputChar('\n');
                _mem.WriteByte(_bufferedInputSeg, (ushort)(_bufferedInputOff + 1), _bufferedInputCount);
                _mem.WriteByte(_bufferedInputSeg, (ushort)(_bufferedInputOff + 2 + _bufferedInputCount), 0x0D);
                _bufferedInputActive = false;
                return;
            }

            if (key.AsciiChar == 0x08 && _bufferedInputCount > 0) // Backspace
            {
                _bufferedInputCount--;
                OutputChar('\b');
                OutputChar(' ');
                OutputChar('\b');
                continue;
            }
            if (key.AsciiChar >= 0x20)
            {
                _mem.WriteByte(_bufferedInputSeg, (ushort)(_bufferedInputOff + 2 + _bufferedInputCount), key.AsciiChar);
                _bufferedInputCount++;
                OutputChar((char)key.AsciiChar);
            }
        }

        // Buffer full: terminate with CR
        _mem.WriteByte(_bufferedInputSeg, (ushort)(_bufferedInputOff + 1), _bufferedInputCount);
        _mem.WriteByte(_bufferedInputSeg, (ushort)(_bufferedInputOff + 2 + _bufferedInputCount), 0x0D);
        _bufferedInputActive = false;
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
            var (provider, resolvedPath) = ResolveFilePath(path);
            var stream = provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult();
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
            var (provider, resolvedPath) = ResolveFilePath(path);
            Stream stream;
            byte mode = (byte)(_cpu.Regs.AL & 3);
            stream = mode switch
            {
                0 => provider.OpenReadAsync(resolvedPath).GetAwaiter().GetResult(),
                1 => provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult(),
                _ => provider.OpenReadWriteAsync(resolvedPath).GetAwaiter().GetResult()
            };

            ushort handle = _nextHandle++;
            _fileHandles[handle] = new DosFileHandle(path, stream);
            _cpu.Regs.AX = handle;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
            _log.Debug("DOS", $"Open file '{path}' → handle {handle}");
        }
        catch (FileNotFoundException ex)
        {
            _log.Debug("DOS", $"Open file '{path}' not found: {ex.Message}");
            _cpu.Regs.AX = 0x02; // File not found
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Debug("DOS", $"Open file '{path}' access denied: {ex.Message}");
            _cpu.Regs.AX = 0x05; // Access denied
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
        catch (DirectoryNotFoundException ex)
        {
            _log.Debug("DOS", $"Open file '{path}' path not found: {ex.Message}");
            _cpu.Regs.AX = 0x03; // Path not found
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
        catch (Exception ex)
        {
            _log.Debug("DOS", $"Open file '{path}' failed: {ex.Message}");
            _cpu.Regs.AX = 0x02; // File not found (default)
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
            if (!TryReadKeyNonBlocking(out var key))
            {
                ReexecuteCurrentInterrupt();
                return;
            }
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
            var (provider, resolvedPath) = ResolveFilePath(path);
            provider.DeleteAsync(resolvedPath).GetAwaiter().GetResult();
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

            case 0x01: // Set device information
                // Accept and ignore — nothing to configure
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x06: // Get input status
                // Return AL=FFh (ready)
                _cpu.Regs.AL = 0xFF;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x07: // Get output status
                // Return AL=FFh (ready)
                _cpu.Regs.AL = 0xFF;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x08: // Is device removable?
                // DX=0 → removable, DX=1 → fixed.  Report fixed for now.
                _cpu.Regs.AX = 1; // non-removable
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x09: // Is drive remote?
                // DX bit 12 (0x1000) = remote/shared, bit 9 (0x0200) = redirected
                // Return DX=0 → local drive (not remote, not network)
                _cpu.Regs.DX = 0x0000;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x0A: // Is handle remote?
                // DX bit 15 = remote. Return 0 → local handle.
                _cpu.Regs.DX = 0x0000;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x0B: // Set sharing retry count
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x0D: // Generic IOCTL (block device)
            case 0x0E: // Get logical drive map
            case 0x0F: // Set logical drive map
                // Stub — succeed silently
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            default:
                _log.Warn("DOS", $"IOCTL unhandled subfunction 0x{subfunc:X2}");
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
        }
    }

    private void HandleGetCurrentDir()
    {
        // Return current directory for drive DL (0=current)
        byte drive = _cpu.Regs.DL;
        char driveLetter = drive == 0 ? (char)('A' + _currentDrive) : (char)('A' + drive - 1);

        string cwd = "";
        if (_currentDirectories.TryGetValue(driveLetter, out var dir))
            cwd = dir.TrimStart('\\');

        ushort seg = _cpu.Regs.DS;
        ushort off = _cpu.Regs.SI;
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(cwd);
        for (int i = 0; i < bytes.Length && i < 63; i++)
            _mem.WriteByte(seg, (ushort)(off + i), bytes[i]);
        _mem.WriteByte(seg, (ushort)(off + Math.Min(bytes.Length, 63)), 0);
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleAllocateMemory()
    {
        ushort paragraphs = _cpu.Regs.BX;
        if (_memoryManager != null)
        {
            ushort seg = _memoryManager.Allocate(paragraphs, CurrentPSP, out ushort maxAvail);
            if (seg != 0)
            {
                _cpu.Regs.AX = seg;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
            }
            else
            {
                _cpu.Regs.AX = 0x08; // Insufficient memory
                _cpu.Regs.BX = maxAvail;
                _cpu.Regs.Flags |= CpuFlags.Carry;
                SetExtendedError(0x08, 1, 2, 0); // Out of memory
            }
        }
        else
        {
            // Fallback: always allocate from a high segment
            _cpu.Regs.AX = (ushort)(CurrentPSP + 0x1000);
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
    }

    private void HandleFreeMemory()
    {
        if (_memoryManager != null)
        {
            if (_memoryManager.Free(_cpu.Regs.ES))
            {
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
            }
            else
            {
                _cpu.Regs.AX = 0x09; // Invalid memory block address
                _cpu.Regs.Flags |= CpuFlags.Carry;
                SetExtendedError(0x09, 2, 1, 0);
            }
        }
        else
        {
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
    }

    private void HandleResizeMemory()
    {
        if (_memoryManager != null)
        {
            ushort newSize = _cpu.Regs.BX;
            if (_memoryManager.Resize(_cpu.Regs.ES, newSize, out ushort maxAvail))
            {
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
            }
            else
            {
                _cpu.Regs.AX = 0x08; // Insufficient memory
                _cpu.Regs.BX = maxAvail;
                _cpu.Regs.Flags |= CpuFlags.Carry;
                SetExtendedError(0x08, 1, 2, 0);
            }
        }
        else
        {
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
    }

    private void HandleExec()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        _log.Info("DOS", $"EXEC: {path}");

        // Read the parameter block at ES:BX
        ushort paramSeg = _cpu.Regs.ES;
        ushort paramOff = _cpu.Regs.BX;
        ushort envSeg = _mem.ReadWord(paramSeg, paramOff); // Environment segment
        ushort cmdLineSeg = _mem.ReadWord(paramSeg, (ushort)(paramOff + 2));
        ushort cmdLineOff = _mem.ReadWord(paramSeg, (ushort)(paramOff + 4));

        // Read command line from the parameter block
        string cmdLine = "";
        if (cmdLineSeg != 0 || cmdLineOff != 0)
        {
            byte len = _mem.ReadByte(cmdLineSeg, cmdLineOff);
            for (int i = 0; i < len; i++)
                cmdLine += (char)_mem.ReadByte(cmdLineSeg, (ushort)(cmdLineOff + 1 + i));
        }

        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            if (!provider.ExistsAsync(resolvedPath).GetAwaiter().GetResult())
            {
                _cpu.Regs.AX = 0x02; // File not found
                _cpu.Regs.Flags |= CpuFlags.Carry;
                SetExtendedError(0x02, 8, 3, 1);
                return;
            }

            // Load the child program
            using var stream = provider.OpenReadAsync(resolvedPath).GetAwaiter().GetResult();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            byte[] programData = ms.ToArray();

            _log.Info("DOS", $"EXEC loading {path} ({programData.Length} bytes), cmdline='{cmdLine}'");

            // For now, just report success - full EXEC requires saving/restoring parent state
            // which needs proper memory management. The shell handles this at a higher level.
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch (Exception ex)
        {
            _log.Error("DOS", $"EXEC failed: {ex.Message}");
            _cpu.Regs.AX = 0x05; // Access denied
            _cpu.Regs.Flags |= CpuFlags.Carry;
            SetExtendedError(0x05, 3, 1, 1);
        }
    }

    /// <summary>Set the current directory for a drive.</summary>
    public void SetCurrentDirectory(char drive, string path)
    {
        _currentDirectories[char.ToUpperInvariant(drive)] = path.TrimStart('\\');
    }

    /// <summary>Get the current directory for a drive.</summary>
    public string GetCurrentDirectory(char? drive = null)
    {
        char d = drive ?? (char)('A' + _currentDrive);
        return _currentDirectories.TryGetValue(d, out var dir) ? dir : "";
    }

    private void SetExtendedError(ushort error, byte errorClass, byte action, byte locus)
    {
        _lastError = error;
        _lastErrorClass = errorClass;
        _lastErrorAction = action;
        _lastErrorLocus = locus;
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

    private void HandleMkdir()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            provider.CreateDirectoryAsync(resolvedPath).GetAwaiter().GetResult();
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
            _log.Info("DOS", $"MKDIR: {path}");
        }
        catch
        {
            _cpu.Regs.AX = 0x05; // Access denied
            _cpu.Regs.Flags |= CpuFlags.Carry;
            SetExtendedError(0x05, 3, 1, 1);
        }
    }

    private void HandleRmdir()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            provider.DeleteDirectoryAsync(resolvedPath).GetAwaiter().GetResult();
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
            _log.Info("DOS", $"RMDIR: {path}");
        }
        catch
        {
            _cpu.Regs.AX = 0x03; // Path not found
            _cpu.Regs.Flags |= CpuFlags.Carry;
            SetExtendedError(0x03, 8, 3, 1);
        }
    }

    private void HandleChdir()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        _log.Info("DOS", $"CHDIR: {path}");

        // Determine which drive this applies to
        char drive;
        string dirPath;
        if (path.Length >= 2 && path[1] == ':')
        {
            drive = char.ToUpperInvariant(path[0]);
            dirPath = path.Length > 2 ? path[2..].TrimStart('\\', '/') : "";
        }
        else
        {
            drive = (char)('A' + _currentDrive);
            dirPath = path.TrimStart('\\', '/');
        }

        // Navigate relative paths
        string current = _currentDirectories.TryGetValue(drive, out var c) ? c : "";
        if (dirPath == "..")
        {
            int lastSep = current.LastIndexOf('\\');
            dirPath = lastSep >= 0 ? current[..lastSep] : "";
        }
        else if (dirPath == ".")
        {
            dirPath = current;
        }
        else if (!string.IsNullOrEmpty(current) && !string.IsNullOrEmpty(dirPath) && !dirPath.Contains('\\'))
        {
            // Relative — append to current
            dirPath = current + "\\" + dirPath;
        }

        _currentDirectories[drive] = dirPath;
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleRenameFile()
    {
        string oldPath = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        string newPath = ReadDosString(_cpu.Regs.ES, _cpu.Regs.DI);
        try
        {
            var (provider, resolvedOld) = ResolveFilePath(oldPath);
            var (_, resolvedNew) = ResolveFilePath(newPath);
            provider.RenameAsync(resolvedOld, resolvedNew).GetAwaiter().GetResult();
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x05; // Access denied
            _cpu.Regs.Flags |= CpuFlags.Carry;
            SetExtendedError(0x05, 3, 1, 1);
        }
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

            var (provider, resolvedDir) = ResolveFilePath(dir.Length > 0 ? dir : ".");
            var entries = provider.ListEntriesAsync(resolvedDir).GetAwaiter().GetResult();

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

        // Try to get file info (size, attributes, date/time)
        uint fileSize = 0;
        byte fileAttr = 0x20; // Default: archive
        DateTime fileTime = DateTime.Now;

        // Determine if this is a directory
        bool isDir = filename.EndsWith('/') || filename.EndsWith('\\');
        if (isDir)
        {
            fileAttr = 0x10; // Directory attribute
            filename = filename.TrimEnd('/', '\\');
        }
        else
        {
            // Try to get the actual file size from the provider
            try
            {
                string dir = "";
                string searchPattern = _findPattern;
                int lastSep = searchPattern.LastIndexOfAny(new[] { '\\', '/' });
                if (lastSep >= 0) dir = searchPattern[..lastSep];

                var (provider, resolvedDir) = ResolveFilePath(dir.Length > 0 ? dir : ".");
                string filePath = string.IsNullOrEmpty(resolvedDir) || resolvedDir == "."
                    ? Path.GetFileName(filename) 
                    : Path.Combine(resolvedDir, Path.GetFileName(filename));
                long size = provider.GetFileSizeAsync(filePath).GetAwaiter().GetResult();
                fileSize = (uint)Math.Min(size, uint.MaxValue);
            }
            catch
            {
                // Size remains 0 if we can't determine it
            }
        }

        // File attribute
        _mem.WriteByte(seg, (ushort)(off + 0x15), fileAttr);

        // File time/date
        ushort time = (ushort)((fileTime.Hour << 11) | (fileTime.Minute << 5) | (fileTime.Second / 2));
        ushort date = (ushort)(((fileTime.Year - 1980) << 9) | (fileTime.Month << 5) | fileTime.Day);
        WriteDtaWord(seg, off, 0x16, time);
        WriteDtaWord(seg, off, 0x18, date);

        // File size (DWORD, little-endian)
        WriteDtaWord(seg, off, 0x1A, (ushort)(fileSize & 0xFFFF));
        WriteDtaWord(seg, off, 0x1C, (ushort)((fileSize >> 16) & 0xFFFF));

        // Filename (up to 12 chars + null, DOS 8.3 format)
        string name = Path.GetFileName(filename).ToUpperInvariant();
        if (name.Length > 12) name = name[..12];
        for (int i = 0; i < name.Length; i++)
            _mem.WriteByte(seg, (ushort)(off + 0x1E + i), (byte)name[i]);
        _mem.WriteByte(seg, (ushort)(off + 0x1E + name.Length), 0);
    }

    private void WriteDtaWord(ushort seg, ushort dtaOff, int fieldOffset, ushort value)
    {
        uint addr = Cpu.Registers.PhysicalAddress(seg, (ushort)(dtaOff + fieldOffset));
        _mem.WriteWord(addr, value);
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

    private void HandleParseFilename()
    {
        ushort siSeg = _cpu.Regs.DS;
        ushort siOff = _cpu.Regs.SI;
        ushort diSeg = _cpu.Regs.ES;
        ushort diOff = _cpu.Regs.DI;

        // Read the input string
        string input = ReadDosString(siSeg, siOff);

        // Parse drive, filename, extension
        byte drive = 0;
        int pos = 0;

        // Skip leading separators based on AL flags
        while (pos < input.Length && (input[pos] == ' ' || input[pos] == '\t'))
            pos++;

        // Check for drive letter
        if (pos + 1 < input.Length && input[pos + 1] == ':')
        {
            drive = (byte)(char.ToUpperInvariant(input[pos]) - 'A' + 1);
            pos += 2;
        }

        // Write drive byte to FCB
        _mem.WriteByte(diSeg, diOff, drive);

        // Parse filename (8 chars, space-padded)
        for (int i = 0; i < 8; i++)
        {
            if (pos < input.Length && input[pos] != '.' && input[pos] != ' ' && input[pos] != 0)
            {
                _mem.WriteByte(diSeg, (ushort)(diOff + 1 + i), (byte)char.ToUpperInvariant(input[pos]));
                pos++;
            }
            else
                _mem.WriteByte(diSeg, (ushort)(diOff + 1 + i), (byte)' ');
        }

        // Skip dot
        if (pos < input.Length && input[pos] == '.') pos++;

        // Parse extension (3 chars, space-padded)
        for (int i = 0; i < 3; i++)
        {
            if (pos < input.Length && input[pos] != ' ' && input[pos] != 0)
            {
                _mem.WriteByte(diSeg, (ushort)(diOff + 9 + i), (byte)char.ToUpperInvariant(input[pos]));
                pos++;
            }
            else
                _mem.WriteByte(diSeg, (ushort)(diOff + 9 + i), (byte)' ');
        }

        // AL = 0 if no wildcards, 1 if wildcards present
        _cpu.Regs.AL = (byte)(input.Contains('*') || input.Contains('?') ? 1 : 0);
        // DS:SI points past the parsed name
        _cpu.Regs.SI = (ushort)(siOff + pos);
    }

    private void HandleDupHandle()
    {
        ushort handle = _cpu.Regs.BX;
        if (handle <= 4 || _fileHandles.ContainsKey(handle))
        {
            ushort newHandle = _nextHandle++;
            if (_fileHandles.TryGetValue(handle, out var fh))
                _fileHandles[newHandle] = new DosFileHandle(fh.Path, fh.Stream);
            _cpu.Regs.AX = newHandle;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        else
        {
            _cpu.Regs.AX = 0x06; // Invalid handle
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleForceDupHandle()
    {
        ushort srcHandle = _cpu.Regs.BX;
        ushort dstHandle = _cpu.Regs.CX;

        // Close destination if it's an open file
        if (_fileHandles.TryGetValue(dstHandle, out var existing))
        {
            existing.Stream.Dispose();
            _fileHandles.Remove(dstHandle);
        }

        if (_fileHandles.TryGetValue(srcHandle, out var fh))
            _fileHandles[dstHandle] = new DosFileHandle(fh.Path, fh.Stream);

        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleCreateTempFile()
    {
        string dir = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        string tempName = $"~DOS{_nextHandle:X4}.TMP";
        string path = string.IsNullOrEmpty(dir) ? tempName : $"{dir}\\{tempName}";

        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            var stream = provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult();
            ushort handle = _nextHandle++;
            _fileHandles[handle] = new DosFileHandle(path, stream);
            _cpu.Regs.AX = handle;

            // Write the temp filename back to DS:DX
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(path);
            ushort off = _cpu.Regs.DX;
            for (int i = 0; i < nameBytes.Length; i++)
                _mem.WriteByte(_cpu.Regs.DS, (ushort)(off + i), nameBytes[i]);
            _mem.WriteByte(_cpu.Regs.DS, (ushort)(off + nameBytes.Length), 0);

            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x05;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleCreateNewFile()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            bool exists = provider.ExistsAsync(resolvedPath).GetAwaiter().GetResult();
            if (exists)
            {
                _cpu.Regs.AX = 0x50; // File already exists
                _cpu.Regs.Flags |= CpuFlags.Carry;
                return;
            }
            var stream = provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult();
            ushort handle = _nextHandle++;
            _fileHandles[handle] = new DosFileHandle(path, stream);
            _cpu.Regs.AX = handle;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x05;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleCanonicalizePath()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.SI);
        // Convert to uppercase, add drive letter if missing
        string canonical = path.ToUpperInvariant().Replace('/', '\\');
        if (canonical.Length < 2 || canonical[1] != ':')
            canonical = $"{(char)('A' + _currentDrive)}:\\{canonical}";

        // Write result to ES:DI
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(canonical);
        for (int i = 0; i < bytes.Length; i++)
            _mem.WriteByte(_cpu.Regs.ES, (ushort)(_cpu.Regs.DI + i), bytes[i]);
        _mem.WriteByte(_cpu.Regs.ES, (ushort)(_cpu.Regs.DI + bytes.Length), 0);
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleExtendedOpen()
    {
        // Extended open/create (DOS 4.0+)
        // BX = open mode, CX = attributes, DX = action
        // DS:SI = filename
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.SI);
        ushort action = _cpu.Regs.DX;
        byte mode = (byte)(_cpu.Regs.BX & 3);

        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            bool exists = provider.ExistsAsync(resolvedPath).GetAwaiter().GetResult();

            if (exists)
            {
                if ((action & 0x01) != 0) // Open if exists
                {
                    Stream stream = mode switch
                    {
                        0 => provider.OpenReadAsync(resolvedPath).GetAwaiter().GetResult(),
                        1 => provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult(),
                        _ => provider.OpenReadWriteAsync(resolvedPath).GetAwaiter().GetResult()
                    };
                    ushort handle = _nextHandle++;
                    _fileHandles[handle] = new DosFileHandle(path, stream);
                    _cpu.Regs.AX = handle;
                    _cpu.Regs.CX = 1; // File existed and was opened
                }
                else if ((action & 0x02) != 0) // Replace if exists
                {
                    var stream = provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult();
                    ushort handle = _nextHandle++;
                    _fileHandles[handle] = new DosFileHandle(path, stream);
                    _cpu.Regs.AX = handle;
                    _cpu.Regs.CX = 3; // File existed and was replaced
                }
                else
                {
                    _cpu.Regs.AX = 0x50; // File exists
                    _cpu.Regs.Flags |= CpuFlags.Carry;
                    return;
                }
            }
            else
            {
                if ((action & 0x10) != 0) // Create if not exists
                {
                    var stream = provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult();
                    ushort handle = _nextHandle++;
                    _fileHandles[handle] = new DosFileHandle(path, stream);
                    _cpu.Regs.AX = handle;
                    _cpu.Regs.CX = 2; // File created
                }
                else
                {
                    _cpu.Regs.AX = 0x02; // File not found
                    _cpu.Regs.Flags |= CpuFlags.Carry;
                    return;
                }
            }
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x05;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }
}
