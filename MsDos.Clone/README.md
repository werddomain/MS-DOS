# MS-DOS .NET Clone

A .NET 8.0 clone of MS-DOS v4.0 with an Intel 8086 CPU emulator, capable of running DOS COM and EXE binaries. Designed with platform abstractions enabling deployment as both a **WinForms** desktop application and a **Blazor WebAssembly** web application.

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
- **DOS INT 21h Services**: Console I/O, file operations, process management, date/time, memory allocation
- **BIOS Services**: INT 10h (video/text), INT 16h (keyboard), INT 1Ah (time), INT 11h/12h (equipment/memory)
- **Binary Loader**: Loads COM files (flat binary at CS:0100h) and EXE files (MZ format with relocations)
- **PSP (Program Segment Prefix)**: Full PSP construction with command tail support

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
2. File → Load Binary (COM/EXE)... or press Ctrl+O
3. Select a DOS COM or EXE file
4. The program executes in the emulated DOS environment

### Blazor WebAssembly
1. Open the web application in a browser
2. Click the file upload button
3. Select a DOS COM or EXE file
4. The program executes in the browser via WebAssembly

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
