# MS-DOS .NET Clone

A .NET 8.0 clone of MS-DOS v4.0 with an Intel 8086 CPU emulator, capable of running DOS COM and EXE binaries and loading disk images (IMG/RAW). Designed with platform abstractions enabling deployment as both a **WinForms** desktop application and a **Blazor WebAssembly** web application.

## Screenshots

### Blazor WebAssembly — UI with Disk Image Loading
![Blazor Emulator UI](https://github.com/user-attachments/assets/a11eb8b1-38e2-413d-81d7-2130c95bb8ad)

### Blazor WebAssembly — Shell with Drive Switching and Log Panel
![Blazor Shell Drive Switching](https://github.com/user-attachments/assets/c5bdd956-d39b-4d9e-b218-27322cac3ad0)

### Blazor WebAssembly — Interactive Shell with HELP command
![Blazor Shell Help](https://github.com/user-attachments/assets/85e27292-bf13-4581-823d-fa6ba2baa5c3)

### Blazor WebAssembly — VER command output
![Blazor Shell Ver](https://github.com/user-attachments/assets/39b0ae2d-19f6-4785-8098-9c7c11557ef1)

## Architecture

```
MsDos.Clone/
├── src/
│   ├── MsDos.Core/           # Platform-agnostic emulation library
│   │   ├── Cpu/              # 8086 CPU emulator (registers, flags, instruction decoder)
│   │   ├── Memory/           # 1MB segmented address space
│   │   ├── Interrupts/       # Interrupt controller, BIOS INT 10h/16h/1Ah
│   │   ├── Dos/              # DOS kernel (INT 21h), binary loader (COM/EXE), disk image loader (FAT12/16)
│   │   └── Platform/         # Abstraction interfaces
│   │       ├── IGraphicsRenderer.cs  # Display rendering (canvas/picturebox)
│   │       ├── IEventRegistry.cs     # Input event handling
│   │       ├── IStreamProvider.cs    # File/stream I/O
│   │       └── EmulatorLog.cs        # Logging/diagnostics system
│   ├── MsDos.WinForms/       # Windows Forms desktop frontend
│   │   └── Platform/         # WinForms implementations (PictureBox, KeyEvents, FileSystem)
│   ├── MsDos.Blazor/         # Blazor WebAssembly frontend
│   │   └── Platform/         # Blazor implementations (Canvas/JS interop, in-memory FS)
│   └── MsDos.Tests/          # Unit tests (xUnit)
└── MsDos.Clone.sln
```

## Platform Abstractions

The core emulation is fully decoupled from any UI framework via three key interfaces:

### `IGraphicsRenderer`
Renders text and graphics to a display surface. WinForms uses `PictureBox` with `System.Drawing`; Blazor uses HTML5 Canvas via JavaScript interop.

### `IEventRegistry`
Captures keyboard and mouse input. WinForms wires `Control.KeyDown/KeyUp` events; Blazor uses `@onkeydown`/`@onkeyup` Razor event handlers.

### `IStreamProvider`
Reads and writes byte streams. WinForms uses the local file system; Blazor uses in-memory dictionaries with browser file upload.

## Features

- **8086 CPU Emulator**: Full instruction set including arithmetic, logic, shifts, string operations, conditional jumps, CALL/RET, PUSH/POP, segment overrides, REP prefixes
- **1MB Memory Bus**: Segmented addressing (segment:offset → 20-bit physical)
- **DOS INT 21h Services**: Console I/O, file operations (create, open, read, write, close, seek, delete), process management, date/time, memory allocation, file search (FindFirst/FindNext with wildcard matching), disk free space, IOCTL
- **BIOS Services**: INT 10h (video/text), INT 16h (keyboard), INT 1Ah (time), INT 11h/12h (equipment/memory)
- **Binary Loader**: Loads COM files (flat binary at CS:0100h) and EXE files (MZ format with relocations)
- **Disk Image Loader**: Loads IMG/RAW/IMA/DSK disk images with FAT12/FAT16 file system support, reads files from disk images, supports common floppy formats (160K–1.44MB)
- **Multi-Drive Support**: Mount disk images on drive letters A:–Z:, switch between drives with `A:`, `B:`, `C:` etc.
- **PSP (Program Segment Prefix)**: Full PSP construction with command tail support
- **CONFIG.SYS Support**: Parsed on boot — FILES=, BUFFERS=, LASTDRIVE=, DEVICE=, SHELL=, COUNTRY=
- **AUTOEXEC.BAT Support**: Executed line by line on shell startup before showing the prompt
- **Logging/Diagnostics**: EmulatorLog with severity levels (Trace/Debug/Info/Warning/Error), event-based output to UI, CPU instruction tracing mode
- **Built-in Command Shell**: COMMAND.COM emulation with interactive commands:
  - `DIR [pattern]` — List directory contents with wildcard filtering
  - `TYPE filename` — Display file contents
  - `CLS` — Clear screen
  - `VER` — Show version
  - `DATE` / `TIME` — Show current date/time
  - `MEM` — Show memory information
  - `ECHO [text]` — Display text
  - `CD [path]` — Change directory
  - `A:`, `B:`, `C:` — Switch drives
  - `SET` / `PATH` — Show environment variables
  - `HELP` — Show available commands
  - `EXIT` — Exit the shell

## Building

```bash
# Build entire solution
cd MsDos.Clone
dotnet build

# Run tests
dotnet test

# Run WinForms app (Windows)
dotnet run --project src/MsDos.WinForms

# Run Blazor WebAssembly app
dotnet run --project src/MsDos.Blazor
```

## Importing and Running Binary Files

### WinForms
1. Launch the application
2. **Load a binary**: File → Load Binary (COM/EXE)... or press Ctrl+O
3. **Load a disk image**: File → Load Disk Image (IMG/RAW)... or press Ctrl+D
4. **Start the shell**: File → Start Shell (COMMAND.COM) or press Ctrl+S
5. The program executes in the emulated DOS environment
6. Switch drives with `A:`, `B:`, `C:` etc. once disk images are mounted

### Blazor WebAssembly
1. Open the web application in a browser
2. **Load a binary**: Click "Load Binary" and select a DOS COM or EXE file
3. **Load a disk image**: Click "Load Disk Image" and select an IMG/RAW/IMA/DSK file
4. **Start the shell**: Click the "Shell" button to open an interactive DOS prompt
5. Type commands (HELP, VER, DIR, CLS, etc.) in the emulated environment
6. System messages appear in the log panel at the bottom

## Extending

To add a new platform, implement the three interfaces:
```csharp
public class MyRenderer : IGraphicsRenderer { /* ... */ }
public class MyEvents : IEventRegistry { /* ... */ }
public class MyStreams : IStreamProvider { /* ... */ }

var machine = new DosMachine(myRenderer, myEvents, myStreams);
machine.LoadBinary(binaryData);
await machine.RunAsync();
```

### Loading Disk Images
```csharp
// Mount a floppy disk image as drive A:
byte[] imageData = File.ReadAllBytes("dos_disk.img");
machine.MountDiskImage('A', imageData);

// Start the shell and switch to A:
await machine.RunShellAsync();
// User types "A:" to switch drives, "DIR" to list files
```

### CONFIG.SYS and AUTOEXEC.BAT
Place `CONFIG.SYS` and/or `AUTOEXEC.BAT` in the stream provider's root to have them processed at boot:
```
FILES=20
BUFFERS=15
LASTDRIVE=Z
```

### Logging
```csharp
machine.Log.MinLevel = LogLevel.Debug;
machine.Log.EntryAdded += entry => Console.WriteLine(entry);
machine.Log.TraceInstructions = true; // Log every CPU instruction
```
