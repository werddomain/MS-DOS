# MS-DOS .NET Clone

A .NET 8.0 clone of MS-DOS v4.0 with an Intel 8086 CPU emulator, capable of running DOS COM and EXE binaries. Designed with platform abstractions enabling deployment as both a **WinForms** desktop application and a **Blazor WebAssembly** web application.

## Screenshots

### Blazor WebAssembly — Initial State
![Blazor Emulator Initial](https://github.com/user-attachments/assets/e111904a-be62-4292-a7ca-5a8968c882df)

### Blazor WebAssembly — Command Shell (COMMAND.COM)
![Blazor Shell Running](https://github.com/user-attachments/assets/9da10bc1-d882-485a-8817-f39951b12582)

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
│   │   ├── Dos/              # DOS kernel (INT 21h), binary loader (COM/EXE)
│   │   └── Platform/         # Abstraction interfaces
│   │       ├── IGraphicsRenderer.cs  # Display rendering (canvas/picturebox)
│   │       ├── IEventRegistry.cs     # Input event handling
│   │       └── IStreamProvider.cs    # File/stream I/O
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
- **PSP (Program Segment Prefix)**: Full PSP construction with command tail support
- **Built-in Command Shell**: COMMAND.COM emulation with interactive commands:
  - `DIR [pattern]` — List directory contents with wildcard filtering
  - `TYPE filename` — Display file contents
  - `CLS` — Clear screen
  - `VER` — Show version
  - `DATE` / `TIME` — Show current date/time
  - `MEM` — Show memory information
  - `ECHO [text]` — Display text
  - `CD [path]` — Change directory
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
3. **Start the shell**: File → Start Shell (COMMAND.COM) or press Ctrl+S
4. The program executes in the emulated DOS environment

### Blazor WebAssembly
1. Open the web application in a browser
2. **Load a binary**: Click "Choose File" and select a DOS COM or EXE file
3. **Start the shell**: Click the "Shell" button to open an interactive DOS prompt
4. Type commands (HELP, VER, DIR, CLS, etc.) in the emulated environment

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
