# Roadmap — Virtual Hardware & BIOS Emulation Gaps

> Identified by comparing against the **8088-PC-Compatible** reference BIOS source code
> and testing requirements from the **DOS_Files** program collection.

---

## Critical — Will break real DOS programs

### 1. PIC IRQ Dispatch Not Connected
- **File:** `DosMachine.cs` — `Pic.InterruptRequest` handler (line ~124)
- **Issue:** The PIC fires `InterruptRequest(vector)` when an unmasked IRQ triggers,
  but the handler only calls `Cpu.Unhalt()` and clears IF — it never dispatches the
  actual interrupt vector through the `InterruptController`.
- **Fix:** Call `Interrupts.Dispatch(vector)` (or equivalent) so that INT 08h, INT 09h,
  and any other hardware-triggered interrupts actually execute their handlers.

### 2. INT 08h — Timer Tick Handler Missing
- **File:** No handler registered for vector `0x08`
- **Issue:** The reference BIOS INT 08h handler increments the DWORD tick counter at
  `0040:006C` on every PIT channel 0 overflow (~18.2 Hz). Our emulator has no INT 08h
  handler, so the BDA tick counter is stale after boot. `INT 1Ah` works around this by
  reading host clock, but programs that poll `0040:006C` directly will hang or
  malfunction (timing loops, delays, game speed).
- **Fix:** Register an INT 08h handler that increments `0040:006C/006E`, handles
  midnight rollover (`0040:0070`), and sends EOI to the PIC.

### 3. INT 09h — Keyboard IRQ Handler Missing
- **File:** No handler registered for vector `0x09`
- **Issue:** The reference BIOS INT 09h reads scancodes via `IN AL, 0x60`, maps them
  through a lookup table to scancode+ASCII, and stores the result in the BDA circular
  keyboard buffer (`0040:001E–003D`) with head/tail at `0040:001A/001C`. Our emulator
  completely bypasses this — keyboard input flows through `IEventRegistry` directly to
  `BiosKeyboardService`. Programs that:
  - Read the BDA keyboard buffer directly
  - Use `IN AL, 0x60` to poll the keyboard controller
  - Compare `0040:001A` vs `0040:001C` to check for pending keys
  will not see any keyboard input.
- **Fix:** When a key event arrives, write the scancode+ASCII word into the BDA
  circular buffer and advance the head pointer. This makes the BDA buffer the source
  of truth that `BiosKeyboardService` (INT 16h) also reads from.

### 4. BDA Keyboard Buffer Not Populated
- **File:** `BiosKeyboardService.cs`
- **Issue:** INT 16h AH=00/01 reads keys from `IEventRegistry` but never touches the
  BDA keyboard buffer at `0040:001E`. Programs that peek at `0040:001A` vs `0040:001C`
  to detect pending keystrokes won't see anything.
- **Fix:** Refactor keyboard flow: key events → write to BDA buffer (INT 09h path) →
  INT 16h reads from BDA buffer. This is how real hardware works.

---

## Important — Needed for many test programs

### 5. CGA Hardware Register State Tracking
- **Files:** `DosMachine.cs` (port stubs), `BiosVideoService.cs`
- **Issue:** The reference BIOS programs CGA CRTC registers (`0x3D4/0x3D5`), mode
  control (`0x3D8`), and color select (`0x3D9`) with specific values for each video
  mode. Our emulator registers these ports as no-op stubs. The 29 AREA5150 demo
  programs and games like DIGGER, PRINCE that program CGA registers directly won't
  display correctly.
- **Fix:** Track CGA register writes — store the CRTC register file (R0-R17), mode
  control byte, and color select byte. Expose these to the renderer so it can
  determine active mode, cursor shape, palette, and display geometry.

### 6. INT 10h Character Generator for Graphics Modes
- **File:** `BiosVideoService.cs`
- **Issue:** The reference BIOS embeds a full 8×8 pixel character font (128 glyphs)
  for rendering text in CGA graphics modes (4, 5, 6). INT 10h AH=0Eh (teletype)
  in graphics mode must blit each character's 8×8 bitmap to video memory. Our video
  service likely falls back to text-mode character writes which won't work in
  graphics modes.
- **Fix:** Embed the standard IBM PC 8×8 CGA font. When in graphics mode, AH=09h,
  0Ah, 0Eh should blit the character bitmap to the correct position in video RAM.

### 7. Port 0x61 — System Control Port
- **File:** `DosMachine.cs` (port stubs)
- **Issue:** Port 0x61 controls PIT channel 2 gate (PC speaker) and reads back timer
  output / refresh toggle. The reference BIOS uses it for speaker control. Our
  emulator stubs it. Programs that use PC speaker sound or PIT channel 2 for precise
  timing won't work correctly.
- **Fix:** Implement port 0x61 read/write: bit 0 = PIT ch2 gate, bit 1 = speaker
  enable, bit 4 = refresh toggle (toggles on read), bit 5 = ch2 output.

---

## Minor — Edge cases and completeness

### 8. IVT Vectors 0x1D, 0x1E, 0x1F — BIOS Data Tables
- **Issue:** The reference BIOS stores pointers to video parameter tables (INT 1Dh),
  disk parameter table (INT 1Eh), and character generator (INT 1Fh) in the IVT. Some
  programs read the INT 1Fh vector to find the 8×8 character font in ROM. We don't
  set these up.
- **Fix:** Write a minimal video parameter table and character generator to a reserved
  ROM area (e.g., segment `0xF000`) and set IVT vectors `0x1D`, `0x1E`, `0x1F` to
  point to them.

### 9. INT 05h — Print Screen
- **Issue:** Not registered. Low priority — rarely used by programs.
- **Fix:** Register a stub or no-op handler.

### 10. INT 10h AH=09h CX Repeat Count
- **Issue:** Need to verify our implementation honors the CX register as a repeat
  count when writing characters. The reference BIOS ignores it, but the IBM PC BIOS
  uses it.
- **Fix:** Audit and ensure CX repeat count is handled.

---

## Test Program Categories (DOS_Files)

| Category | Programs | Key Requirements |
|----------|----------|-----------------|
| **Simple games** | BEAST, WILLY, GAME, BOWL | INT 10h text, INT 16h keyboard, timer |
| **CGA games** | DIGGER, FLIGHT, PRINCE | CGA mode 4, direct register programming |
| **Demos** | AREA5150 (29 effects) | Direct CGA hardware, precise timing |
| **BASIC** | BASIC86 + HELLO.BAS | INT 10h, INT 16h, INT 21h file I/O |
| **Complex** | WOLFENSTEIN, CONQUEST | CGA graphics, complex I/O, overlays |
| **Utilities** | MOUSE.COM, NET/*.COM | Device drivers, TSR patterns |

---

## Implementation Priority

1. **Fix PIC IRQ dispatch** → unblocks all hardware interrupts
2. **INT 08h timer tick** → BDA tick counter, timing loops
3. **INT 09h / BDA keyboard buffer** → hardware keyboard path
4. **CGA register tracking** → graphics programs
5. **8×8 character generator** → text in graphics modes
6. **Port 0x61** → speaker and timing
7. **IVT data tables** → ROM compatibility
