# MS-DOS Clone — Implementation Roadmap

This roadmap tracks all CPU instructions, DOS services, BIOS interrupts, and system
features needed for a fully functional MS-DOS 4.0 compatible emulator.

Legend: ✅ Implemented | 🔧 Partial | ❌ Not implemented

---

## 1. Intel 8086/8088 CPU Instructions

### 1.1 Data Transfer

| Opcode(s) | Mnemonic | Status | Notes |
|-----------|----------|--------|-------|
| 88-8B | MOV r/m, reg / reg, r/m | ✅ | |
| 8C, 8E | MOV r/m, sreg / sreg, r/m | ✅ | |
| A0-A3 | MOV AL/AX, [addr] / [addr], AL/AX | ✅ | |
| B0-BF | MOV reg, imm | ✅ | |
| C6, C7 | MOV r/m, imm | ✅ | |
| 86, 87 | XCHG r/m, reg | ✅ | |
| 91-97 | XCHG AX, reg | ✅ | |
| 8D | LEA reg, mem | ✅ | |
| C4 | LES reg, mem | ✅ | |
| C5 | LDS reg, mem | ✅ | |
| D7 | XLAT | ✅ | |
| 9E | SAHF | ✅ | |
| 9F | LAHF | ✅ | |
| E4-E7 | IN AL/AX, imm8 / OUT imm8, AL/AX | ✅ | Stub (no port I/O) |
| EC-EF | IN AL/AX, DX / OUT DX, AL/AX | ✅ | Stub (no port I/O) |
| 0x60 | PUSHA | ✅ | 80186+ |
| 0x61 | POPA | ✅ | 80186+ |
| 0x68 | PUSH imm16 | ✅ | 80186+ |
| 0x6A | PUSH imm8 (sign-ext) | ✅ | 80186+ |
| 0x6C-0x6D | INSB/INSW | ✅ | Stub (no port I/O) |
| 0x6E-0x6F | OUTSB/OUTSW | ✅ | Stub (no port I/O) |

### 1.2 Arithmetic

| Opcode(s) | Mnemonic | Status | Notes |
|-----------|----------|--------|-------|
| 00-05 | ADD r/m, reg / reg, r/m / AL/AX, imm | ✅ | |
| 10-15 | ADC r/m, reg / reg, r/m / AL/AX, imm | ✅ | |
| 28-2D | SUB r/m, reg / reg, r/m / AL/AX, imm | ✅ | |
| 18-1D | SBB r/m, reg / reg, r/m / AL/AX, imm | ✅ | |
| 38-3D | CMP r/m, reg / reg, r/m / AL/AX, imm | ✅ | |
| 40-47 | INC reg16 | ✅ | |
| 48-4F | DEC reg16 | ✅ | |
| FE /0, /1 | INC/DEC r/m8 | ✅ | |
| FF /0, /1 | INC/DEC r/m16 | ✅ | |
| F6 /4-/7 | MUL/IMUL/DIV/IDIV r/m8 | ✅ | |
| F7 /4-/7 | MUL/IMUL/DIV/IDIV r/m16 | ✅ | |
| 80-83 | Group 1 (ADD/OR/ADC/SBB/AND/SUB/XOR/CMP) imm | ✅ | |
| 27 | DAA | ✅ | |
| 2F | DAS | ✅ | |
| 37 | AAA | ✅ | |
| 3F | AAS | ✅ | |
| D4 | AAM | ✅ | |
| D5 | AAD | ✅ | |
| 98 | CBW | ✅ | |
| 99 | CWD | ✅ | |
| F6 /3 | NEG r/m8 | ✅ | |
| F7 /3 | NEG r/m16 | ✅ | |
| 0x69 | IMUL r16, r/m16, imm16 | ✅ | 80186+ |
| 0x6B | IMUL r16, r/m16, imm8 | ✅ | 80186+ |
| 0x62 | BOUND r16, mem | ✅ | 80186+ |

### 1.3 Logic & Shift

| Opcode(s) | Mnemonic | Status | Notes |
|-----------|----------|--------|-------|
| 20-25 | AND r/m, reg / reg, r/m / AL/AX, imm | ✅ | |
| 08-0D | OR r/m, reg / reg, r/m / AL/AX, imm | ✅ | |
| 30-35 | XOR r/m, reg / reg, r/m / AL/AX, imm | ✅ | |
| 84, 85 | TEST r/m, reg | ✅ | |
| A8, A9 | TEST AL/AX, imm | ✅ | |
| F6 /0-/1 | TEST r/m8, imm | ✅ | |
| F7 /0-/1 | TEST r/m16, imm | ✅ | |
| F6 /2 | NOT r/m8 | ✅ | |
| F7 /2 | NOT r/m16 | ✅ | |
| D0-D3 | Shift/Rotate by 1/CL (ROL/ROR/RCL/RCR/SHL/SHR/SAR) | ✅ | |
| C0, C1 | Shift/Rotate by imm8 | ✅ | 80186+ |

### 1.4 String Operations

| Opcode(s) | Mnemonic | Status | Notes |
|-----------|----------|--------|-------|
| A4, A5 | MOVSB/MOVSW | ✅ | |
| A6, A7 | CMPSB/CMPSW | ✅ | |
| AA, AB | STOSB/STOSW | ✅ | |
| AC, AD | LODSB/LODSW | ✅ | |
| AE, AF | SCASB/SCASW | ✅ | |
| F2 | REPNZ prefix | ✅ | |
| F3 | REP/REPZ prefix | ✅ | |

### 1.5 Control Transfer

| Opcode(s) | Mnemonic | Status | Notes |
|-----------|----------|--------|-------|
| E8 | CALL near rel16 | ✅ | |
| 9A | CALL far seg:off | ✅ | |
| FF /2 | CALL near indirect | ✅ | |
| FF /3 | CALL far indirect | ✅ | |
| C2 | RET near (pop n) | ✅ | |
| C3 | RET near | ✅ | |
| CA | RETF (pop n) | ✅ | |
| CB | RETF | ✅ | |
| E9 | JMP near rel16 | ✅ | |
| EB | JMP short rel8 | ✅ | |
| EA | JMP far seg:off | ✅ | |
| FF /4 | JMP near indirect | ✅ | |
| FF /5 | JMP far indirect | ✅ | |
| 70-7F | Jcc short (all 16 conditions) | ✅ | |
| E0 | LOOPNZ | ✅ | |
| E1 | LOOPZ | ✅ | |
| E2 | LOOP | ✅ | |
| E3 | JCXZ | ✅ | |
| CC | INT 3 | ✅ | |
| CD | INT n | ✅ | |
| CE | INTO | ✅ | |
| CF | IRET | ✅ | |
| 0xC8 | ENTER imm16, imm8 | ✅ | 80186+ |
| 0xC9 | LEAVE | ✅ | 80186+ |

### 1.6 Stack

| Opcode(s) | Mnemonic | Status | Notes |
|-----------|----------|--------|-------|
| 50-57 | PUSH reg16 | ✅ | |
| 58-5F | POP reg16 | ✅ | |
| 06,0E,16,1E | PUSH ES/CS/SS/DS | ✅ | |
| 07,17,1F | POP ES/SS/DS | ✅ | |
| 0F | POP CS | ✅ | 8086 only |
| 8F | POP r/m16 | ✅ | |
| FF /6 | PUSH r/m16 | ✅ | |
| 9C | PUSHF | ✅ | |
| 9D | POPF | ✅ | |

### 1.7 Flags & Processor Control

| Opcode(s) | Mnemonic | Status | Notes |
|-----------|----------|--------|-------|
| F8 | CLC | ✅ | |
| F9 | STC | ✅ | |
| FA | CLI | ✅ | |
| FB | STI | ✅ | |
| FC | CLD | ✅ | |
| FD | STD | ✅ | |
| F5 | CMC | ✅ | |
| 90 | NOP | ✅ | |
| F4 | HLT | ✅ | |
| 9B | WAIT/FWAIT | ✅ | NOP (no FPU) |
| F0 | LOCK prefix | ✅ | NOP (no multiprocessor) |
| 26,2E,36,3E | Segment override prefixes (ES/CS/SS/DS) | ✅ | |
| D8-DF | ESC (x87 FPU coprocessor) | ✅ | NOP — consumes ModR/M + displacement, no FPU |
| D6 | SALC/SETALC (undocumented) | ❌ | Set AL=FF if CF=1, else AL=0. Used by some programs |

### 1.8 Two-byte Opcodes (0x0F prefix) — 80186/80286+

These are needed by many real-world DOS programs compiled for 80186+.

| Opcode(s) | Mnemonic | Status | Notes |
|-----------|----------|--------|-------|
| 0F 80-8F | Jcc near rel16 (16-bit offsets) | ✅ | Required by most compilers |
| 0F B6 | MOVZX r16, r/m8 | ✅ | 80386+ but common |
| 0F B7 | MOVZX r16, r/m16 | ✅ | 80386+ |
| 0F BE | MOVSX r16, r/m8 | ✅ | 80386+ but common |
| 0F BF | MOVSX r16, r/m16 | ✅ | 80386+ |
| 0F A4/A5 | SHLD | ✅ | 80386+ |
| 0F AC/AD | SHRD | ✅ | 80386+ |
| 0F AF | IMUL r16, r/m16 | ✅ | 80386+ |
| 0F 90-9F | SETcc | ✅ | 80386+ |
| 0F A3/AB/B3/BA/BB | BT/BTS/BTR/BTC | ✅ | 80386+ |
| 0F BC/BD | BSF/BSR | ✅ | 80386+ |
| 0F B0/B1 | CMPXCHG | ❌ | 80486+ |
| 0F C0/C1 | XADD | ❌ | 80486+ |
| 0F 01 | LGDT/SGDT/LIDT/SIDT | ❌ | Protected mode, low priority |
| 0F 00 | SLDT/STR/LLDT/LTR/VERR/VERW | ❌ | Protected mode, low priority |
| 0F 02/03 | LAR/LSL | ❌ | Protected mode, low priority |
| 0F A0/A1 | PUSH FS / POP FS | ❌ | 80386+, some programs use FS/GS |
| 0F A8/A9 | PUSH GS / POP GS | ❌ | 80386+, some programs use FS/GS |

### 1.9 CPU Functional Gaps

These are not missing opcodes but missing *behaviors* within implemented instructions.

| Feature | Status | Severity | Notes |
|---------|--------|----------|-------|
| I/O port space (ports 0x00-0xFFFF) | ❌ | Medium | IN/OUT are NOPs — no PIT (0x40-0x43), PIC (0x20-0x21), keyboard (0x60-0x64), CRT (0x3D4-0x3D5), or game port (0x201) |
| PIT timer port 0x40 read | ❌ | Medium | Many programs read port 0x40 for high-resolution timing / random seeds |
| PIC ports 0x20/0x21 (EOI, mask) | ❌ | Medium | Programs that install hardware IRQ handlers issue EOI via OUT 0x20,0x20 |
| Keyboard controller port 0x60 | ❌ | Low | Low-level keyboard programs read scan codes directly from port 0x60 |
| CRT controller ports 0x3D4-0x3D5 | ❌ | Low | Programs that do CRT tricks (smooth scroll, split screen) probe these |
| VGA DAC ports 0x3C7-0x3C9 | ❌ | Medium | VGA palette programming — programs write RGB values via port I/O |
| Game port 0x201 | ❌ | Low | Joystick reading |
| Trap Flag (TF) single-step INT 1 | ❌ | Low | DEBUG.COM uses TF for single-stepping — INT 1 is never generated |
| Overflow flag (OF) on 1-bit shifts | ❌ | Low | ROL/ROR/SHL/SHR should set OF specifically for count=1 shifts |
| REP with INS/OUTS (0x6C-0x6F) | ❌ | Low | REP handler doesn't dispatch 0x6C-0x6F |
| BIOS Data Area (0040:0000) | ❌ | Medium | Not populated — programs probing BDA for video mode, drive count, keyboard buffer, etc. get zeros |

---

## 2. DOS Services (INT 21h)

### 2.1 Console I/O (AH=00h–0Ch)

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 00h | Terminate program | ✅ | |
| 01h | Read character with echo | ✅ | |
| 02h | Display character | ✅ | |
| 03h | Auxiliary input | ❌ | Rarely used |
| 04h | Auxiliary output | ❌ | Rarely used |
| 05h | Printer output | ❌ | Rarely used |
| 06h | Direct console I/O | ✅ | |
| 07h | Direct character input (no echo) | ✅ | |
| 08h | Character input without echo | ✅ | |
| 09h | Display string ($-terminated) | ✅ | |
| 0Ah | Buffered keyboard input | ✅ | |
| 0Bh | Check standard input status | ✅ | |
| 0Ch | Clear input buffer and invoke input | ✅ | |

### 2.2 Disk & File Operations (AH=0Dh–29h)

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 0Dh | Disk reset | ✅ | Flush all disk buffers |
| 0Eh | Select disk | ✅ | |
| 0Fh | Open file using FCB | ❌ | Legacy FCB, low priority |
| 10h | Close file using FCB | ❌ | Legacy FCB, low priority |
| 11h | Find first file using FCB | ❌ | Legacy FCB, low priority |
| 12h | Find next file using FCB | ❌ | Legacy FCB, low priority |
| 13h | Delete file using FCB | ❌ | Legacy FCB, low priority |
| 14h | Sequential read (FCB) | ❌ | Legacy FCB, low priority |
| 15h | Sequential write (FCB) | ❌ | Legacy FCB, low priority |
| 16h | Create file (FCB) | ❌ | Legacy FCB, low priority |
| 17h | Rename file (FCB) | ❌ | Legacy FCB, low priority |
| 19h | Get current default drive | ✅ | |
| 1Ah | Set DTA | ✅ | |
| 1Bh | Get allocation info (default drive) | 🔧 | Returns hardcoded values, no media descriptor pointer |
| 1Ch | Get allocation info (specific drive) | 🔧 | Returns hardcoded values, no media descriptor pointer |
| 21h | Random read (FCB) | ❌ | Legacy FCB, low priority |
| 22h | Random write (FCB) | ❌ | Legacy FCB, low priority |
| 23h | Get file size (FCB) | ❌ | Legacy FCB, low priority |
| 24h | Set random record number (FCB) | ❌ | Legacy FCB, low priority |
| 25h | Set interrupt vector | ✅ | |
| 26h | Create new PSP | ✅ | |
| 27h | Random block read (FCB) | ❌ | Legacy FCB, low priority |
| 28h | Random block write (FCB) | ❌ | Legacy FCB, low priority |
| 29h | Parse filename into FCB | ✅ | Needed by some programs |

### 2.3 Date/Time & System Info (AH=2Ah–36h)

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 2Ah | Get date | ✅ | |
| 2Bh | Set date | ✅ | Stub — accepted but ignored |
| 2Ch | Get time | ✅ | |
| 2Dh | Set time | ✅ | Stub — accepted but ignored |
| 2Eh | Set verify flag | ✅ | |
| 2Fh | Get DTA address | ✅ | |
| 30h | Get DOS version | ✅ | Returns 4.0 |
| 31h | Terminate and stay resident (TSR) | ❌ | Complex |
| 33h | Get/set Ctrl-Break flag | ✅ | |
| 34h | Get InDOS flag address | ✅ | |
| 35h | Get interrupt vector | ✅ | |
| 36h | Get disk free space | ✅ | |
| 37h | Get/set switch char | ✅ | |

### 2.4 File Handle Functions (AH=38h–62h)

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 38h | Get/set country info | ✅ | |
| 39h | Create directory (MKDIR) | ✅ | Stub (logs only) |
| 3Ah | Remove directory (RMDIR) | ✅ | Stub (logs only) |
| 3Bh | Change directory (CHDIR) | ✅ | Stub (logs only) |
| 3Ch | Create file | ✅ | |
| 3Dh | Open file | ✅ | |
| 3Eh | Close file | ✅ | |
| 3Fh | Read file/device | ✅ | |
| 40h | Write file/device | ✅ | |
| 41h | Delete file | ✅ | |
| 42h | Move file pointer (LSEEK) | ✅ | |
| 43h | Get/set file attributes | 🔧 | Always returns archive |
| 44h | IOCTL | 🔧 | Subfuncs 00h-01h,06h-0Bh,0Dh-0Fh stubbed; missing 02h-05h (read/write IOCTL data) |
| 45h | Duplicate handle (DUP) | ✅ | |
| 46h | Force duplicate handle (DUP2) | ✅ | |
| 47h | Get current directory | ✅ | Always returns root |
| 48h | Allocate memory | ✅ | Simplified |
| 49h | Free memory | ✅ | Stub |
| 4Ah | Resize memory block | ✅ | Stub |
| 4Bh | EXEC (load and execute) | 🔧 | Logs only, no child process |
| 4Ch | Terminate with return code | ✅ | |
| 4Dh | Get return code | ✅ | |
| 4Eh | Find first matching file | 🔧 | File size always returned as 0 in DTA |
| 4Fh | Find next matching file | 🔧 | File size always returned as 0 in DTA |
| 50h | Set PSP segment | ✅ | |
| 51h | Get PSP segment | ✅ | |
| 54h | Get verify flag | ✅ | |
| 55h | Create child PSP | 🔧 | Copies PSP bytes but doesn't set INT 22h/23h/24h vectors |
| 56h | Rename file | 🔧 | Accepts and succeeds without actually renaming |
| 57h | Get/set file date/time | ✅ | |
| 58h | Get/set memory allocation strategy | ✅ | |
| 59h | Get extended error information | 🔧 | Always returns "no error" — no error state tracking |
| 5Ah | Create temporary file | ✅ | |
| 5Bh | Create new file (fail if exists) | ✅ | |
| 5Ch | Lock/unlock file region | ❌ | |
| 5Dh | Set extended error info | ❌ | Undocumented |
| 5Eh | Network services | ❌ | Low priority |
| 5Fh | Network redirection | ❌ | Low priority |
| 60h | Canonicalize filename | ✅ | |
| 62h | Get PSP address | ✅ | |
| 63h | Get lead byte table (DBCS) | ✅ | |
| 65h | Get extended country info | 🔧 | Returns unsupported |
| 66h | Get/set global code page | ✅ | |
| 67h | Set handle count | ✅ | |
| 68h | Commit file (flush) | ✅ | |
| 6Ch | Extended open/create | ✅ | DOS 4.0+ |

### 2.5 DOS Kernel Infrastructure Gaps

These are missing internal systems that multiple INT 21h functions depend on.

| Feature | Status | Severity | Notes |
|---------|--------|----------|-------|
| MCB (Memory Control Block) chain | ❌ | Critical | 48h/49h/4Ah just fake it — no arena, programs corrupt each other |
| Environment block | ❌ | High | Programs can't read PATH, COMSPEC, or any env vars; EXEC needs this |
| Current working directory tracking | ❌ | High | CWD is always \ — CHDIR is cosmetic only; path resolution ignores CWD |
| Per-drive CWD (CDS table) | ❌ | High | DOS maintains a separate CWD per drive letter |
| Error code tracking | ❌ | Medium | INT 21h/59h always says "no error"; no error propagation between calls |
| SFT (System File Table) | ❌ | Medium | File handles use a simple Dictionary; no SFT entries for inheritance |
| SDA (Swappable Data Area) | ❌ | Low | Advanced programs/TSRs may probe this |
| File sharing / SHARE.EXE | ❌ | Low | No record locking across handles |
| InDOS flag (real tracking) | ❌ | Low | Flag at 0070:0000 is always 0; should be 1 while inside INT 21h |
| Handle inheritance on EXEC | ❌ | Medium | Child processes should inherit parent's open file handles |

---

## 3. BIOS Interrupts

### 3.1 INT 10h — Video Services

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 00h | Set video mode | 🔧 | Only modes 03h, 04h, 06h, 13h. Missing: 00h-02h (40-col text), 05h (320x200 B&W), 0Dh-12h (EGA/VGA) |
| 01h | Set cursor shape | ✅ | |
| 02h | Set cursor position | ✅ | |
| 03h | Get cursor position/shape | ✅ | |
| 04h | Get light pen position | ❌ | Rarely used |
| 05h | Set active display page | 🔧 | Page number stored but no multi-page video memory |
| 06h | Scroll up | 🔧 | Window corner registers (CH/CL/DH/DL) not used — always full-screen scroll |
| 07h | Scroll down | 🔧 | Window corner registers (CH/CL/DH/DL) not used — always full-screen scroll |
| 08h | Read char/attr at cursor | ✅ | |
| 09h | Write char/attr at cursor | ✅ | |
| 0Ah | Write char at cursor (keep attr) | ✅ | |
| 0Bh | Set color palette | 🔧 | Stub — no CGA palette switching |
| 0Ch | Write pixel | 🔧 | Only mode 13h (linear A000). CGA modes 04h/06h interlaced memory not emulated |
| 0Dh | Read pixel | 🔧 | Only mode 13h. CGA modes return 0 |
| 0Eh | TTY output | ✅ | |
| 0Fh | Get video mode | ✅ | |
| 10h | Set palette registers | ❌ | Stub only — VGA DAC palette writes have no effect |
| 11h | Character generator | 🔧 | Only AH=30h (get font info). Missing: load user font, set 8x8/8x14/8x16 |
| 12h | Video subsystem configuration | ✅ | |
| 13h | Write string | ✅ | |
| 1Ah | Get/set display combination | ❌ | VGA — returns active/alternate display |
| 1Bh | Get video functionality info | ❌ | VGA — returns state buffer |

### 3.2 INT 11h — Equipment List

| Function | Status | Notes |
|----------|--------|-------|
| Get equipment list | ✅ | Returns standard CGA config |

### 3.3 INT 12h — Memory Size

| Function | Status | Notes |
|----------|--------|-------|
| Get conventional memory size | ✅ | Returns 640 KB |

### 3.4 INT 13h — Disk Services

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 00h | Reset disk system | ✅ | |
| 01h | Get disk status | ✅ | |
| 02h | Read sectors | ✅ | |
| 03h | Write sectors | ✅ | |
| 04h | Verify sectors | ✅ | |
| 05h | Format track | ❌ | Needed by FORMAT.COM |
| 06h | Format track and set bad sector flags | ❌ | Hard disk only |
| 07h | Format drive from specified track | ❌ | Hard disk only |
| 08h | Get drive parameters | 🔧 | DL always returns 1 drive; should reflect actual mounted count |
| 09h | Initialize drive pair characteristics | ❌ | Hard disk only |
| 0Ah | Read long sectors (with ECC) | ❌ | Rarely used |
| 0Bh | Write long sectors (with ECC) | ❌ | Rarely used |
| 0Ch | Seek to cylinder | ❌ | Low priority |
| 0Dh | Alternate disk reset | ❌ | Hard disk only |
| 15h | Get disk type | ✅ | |
| 16h | Detect disk change | ✅ | |
| 17h | Set disk type for format | ❌ | Used by FORMAT.COM |
| 18h | Set media type for format | ❌ | Used by FORMAT.COM |

### 3.5 INT 14h — Serial Port Services

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 00h | Initialize serial port | ❌ | Low priority |
| 01h | Send character | ❌ | Low priority |
| 02h | Receive character | ❌ | Low priority |
| 03h | Get port status | ❌ | Low priority |

### 3.6 INT 15h — System Services

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 41h | Wait on external event | ✅ | Stub — returns immediately |
| 4Fh | Keyboard intercept | ❌ | Called by INT 09h to allow TSRs to filter keystrokes |
| 86h | Wait | ✅ | |
| 87h | Extended memory block move (GDT) | ❌ | Requires protected mode switch; some programs use for >1MB copy |
| 88h | Get extended memory size | ✅ | Returns 0 (no extended memory) |
| 89h | Switch to protected mode | ❌ | Low priority — 80286+ only |
| C0h | Get system configuration table | 🔧 | Returns unsupported; should return pointer to config table |
| C1h | Get extended BIOS data area segment | ❌ | Low priority |

### 3.7 INT 16h — Keyboard Services

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 00h | Wait for keypress | ✅ | |
| 01h | Check for keypress | 🔧 | Peek is destructive — consumes key instead of leaving it in buffer |
| 02h | Get shift flags | 🔧 | Always returns 0 — programs can't detect Shift/Ctrl/Alt state |
| 03h | Set typematic rate/delay | ❌ | |
| 05h | Store key in buffer | ❌ | TSRs use this to inject keystrokes |
| 10h | Wait for keypress (enhanced) | ✅ | |
| 11h | Check for keypress (enhanced) | 🔧 | Same destructive peek issue as AH=01h |
| 12h | Get extended shift flags | 🔧 | Always returns 0 — same as AH=02h |

### 3.8 INT 17h — Printer Services

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 00h | Print character | ❌ | Low priority |
| 01h | Initialize printer | ❌ | Low priority |
| 02h | Get printer status | ❌ | Low priority |

### 3.9 INT 19h — Bootstrap Loader

| Function | Status | Notes |
|----------|--------|-------|
| Reboot | ✅ | Restarts emulation |

### 3.10 INT 1Ah — Time of Day

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 00h | Get system timer | ✅ | |
| 01h | Set system timer | ❌ | |
| 02h | Get RTC time | ✅ | |
| 03h | Set RTC time | ❌ | |
| 04h | Get RTC date | ✅ | |
| 05h | Set RTC date | ❌ | |

### 3.11 INT 33h — Mouse Services

| AX | Function | Status | Notes |
|----|----------|--------|-------|
| 0000h | Reset/detect mouse | ✅ | |
| 0001h | Show cursor | ✅ | |
| 0002h | Hide cursor | ✅ | |
| 0003h | Get position/button status | ✅ | |
| 0004h | Set position | ✅ | |
| 0005h | Get button press info | ✅ | Press count always 0 |
| 0006h | Get button release info | ✅ | Release count always 0 |
| 0007h | Set horizontal limits | ✅ | |
| 0008h | Set vertical limits | ✅ | |
| 0009h | Set graphics cursor shape | ❌ | |
| 000Ah | Set text cursor type | ❌ | |
| 000Bh | Read motion counters | ✅ | Always returns 0 mickeys |
| 000Ch | Set user subroutine (callback) | 🔧 | Accepted but ignored — mouse-driven programs that rely on callbacks won't get events |
| 000Dh | Light pen emulation on | ❌ | Rarely used |
| 000Eh | Light pen emulation off | ❌ | Rarely used |
| 000Fh | Set mickey/pixel ratio | ❌ | |
| 0010h | Set mouse exclusion area | ❌ | |
| 0013h | Set double-speed threshold | ❌ | |
| 0014h | Swap user subroutine | ❌ | |
| 0015h | Get mouse driver state size | ❌ | |
| 0016h | Save mouse driver state | ❌ | |
| 0017h | Restore mouse driver state | ❌ | |
| 001Ah | Set mouse sensitivity | ❌ | |
| 001Bh | Get mouse sensitivity | ❌ | |

### 3.12 Hardware Interrupt Infrastructure

These are not BIOS service interrupts but hardware-level mechanisms that many programs depend on.

| Feature | Status | Severity | Notes |
|---------|--------|----------|-------|
| INT 08h — IRQ 0 Timer tick (18.2 Hz) | ❌ | High | Many programs (clocks, games, PRINT.COM) hook INT 08h for timing |
| INT 09h — IRQ 1 Keyboard hardware | ❌ | Low | Low-level keyboard; works around it via managed handler |
| INT 0Bh-0Ch — IRQ 3/4 Serial ports | ❌ | Low | Serial I/O programs |
| INT 0Eh — IRQ 6 Floppy disk | ❌ | Low | |
| INT 23h — Ctrl-C handler | ❌ | Medium | Ctrl-C doesn't invoke program's registered handler |
| INT 24h — Critical error handler | ❌ | Medium | Disk errors should invoke the user's critical error handler |
| INT 28h — DOS idle interrupt | ❌ | Low | TSR programs hook this to run during DOS idle |
| INT 2Fh — Multiplex interrupt | ❌ | Medium | Used by PRINT, ASSIGN, SHARE, APPEND, DOSKEY, many TSRs |
| PIC (8259) emulation | ❌ | Medium | No IRQ masking, no EOI — OUT 0x20,0x20 is a NOP |
| PIT (8253/8254) timer emulation | ❌ | Medium | No periodic tick generation; port 0x40 reads return nothing |
| DMA controller (8237) | ❌ | Low | Floppy disk and sound card DMA |

---

## 4. System Features

### 4.1 Memory Management

| Feature | Status | Notes |
|---------|--------|-------|
| 1 MB address space | ✅ | MemoryBus class |
| Segment:offset addressing | ✅ | |
| MCB (Memory Control Block) chain | ❌ | Critical — for proper alloc/free/EXEC |
| UMB (Upper Memory Blocks) | ❌ | Low priority |
| HMA (High Memory Area) | ❌ | Low priority |
| I/O port address space (64K ports) | ❌ | IN/OUT go nowhere — need at least PIT, PIC, keyboard, VGA ports |
| BIOS Data Area (0040:0000-0040:00FF) | ❌ | Not populated — keyboard buffer, video state, drive count, timer ticks all zero |
| ROM area (F000:0000+) write protection | ❌ | Low priority — writable in emulator |

### 4.2 File System

| Feature | Status | Notes |
|---------|--------|-------|
| FAT12 disk image reading | ✅ | DiskImageLoader |
| FAT16 disk image reading | ✅ | DiskImageLoader |
| FAT12 disk image writing | ✅ | DiskImageWriter |
| FAT16 disk image writing | ❌ | Only FAT12 blanks can be created |
| FAT chain following | ✅ | |
| Subdirectory traversal | ❌ | Only root directory — CD \SUBDIR and paths like \DOS\EDIT.COM fail on disk images |
| Subdirectory creation/deletion | ❌ | MKDIR/RMDIR accepted but do nothing |
| Long filename support | ❌ | Not needed (DOS 8.3 only) |
| File attribute support | 🔧 | Always returns archive — get/set both stubbed |
| File size in FindFirst/FindNext | ❌ | DTA always writes size=0; programs checking file size get wrong data |
| Volume label read/write | ❌ | |
| Disk image write-through / persist | ❌ | Writes go to in-memory byte array only — not saved unless explicitly exported |
| Correct media descriptor byte | ❌ | INT 21h/1Bh DS:BX should point to media descriptor; currently doesn't |

### 4.3 Process Management

| Feature | Status | Notes |
|---------|--------|-------|
| COM file loading | ✅ | BinaryLoader |
| EXE (MZ) file loading | ✅ | BinaryLoader with relocations |
| PSP setup | ✅ | |
| Command tail | ✅ | |
| Child process execution (EXEC) | ❌ | Critical — INT 21h/4Bh logs only, no child process loading |
| EXEC overlay loading (4Bh/03h) | ❌ | Load overlay without creating PSP |
| TSR (Terminate and Stay Resident) | ❌ | INT 21h/31h not implemented; INT 27h not handled |
| Environment block | ❌ | No COMSPEC=, PATH=, or custom env vars available to programs |
| Environment block passing to child | ❌ | EXEC should pass or inherit environment |
| Handle inheritance on EXEC | ❌ | Child processes should inherit parent's open file handles |
| Program return to parent | ❌ | After child exits, parent (COMMAND.COM or caller) should resume |
| INT 22h/23h/24h vector save/restore | ❌ | PSP should store these; EXEC should set/restore them |

### 4.4 Device Drivers

| Feature | Status | Notes |
|---------|--------|-------|
| CON (console) | ✅ | Via IOCTL |
| NUL | ❌ | |
| PRN/LPT1 | ❌ | |
| AUX/COM1 | ❌ | |
| CLOCK$ | ❌ | |
| Installable drivers (CONFIG.SYS) | ❌ | |

### 4.5 Command Shell (COMMAND.COM)

| Feature | Status | Notes |
|---------|--------|-------|
| Interactive prompt | ✅ | |
| DIR | ✅ | With cross-drive support |
| TYPE | ✅ | |
| COPY | ✅ | Including cross-drive |
| DEL/ERASE | ✅ | |
| REN/RENAME | 🔧 | Prints "not yet implemented" |
| MKDIR/MD | 🔧 | Prints success but doesn't actually create directory |
| RMDIR/RD | 🔧 | Prints success but doesn't actually delete directory |
| CD/CHDIR | 🔧 | Partial — no real path tracking, no validation |
| CLS | ✅ | |
| VER | ✅ | |
| DATE | ✅ | |
| TIME | ✅ | |
| ECHO | ✅ | |
| SET | ✅ | |
| PATH | ✅ | |
| MEM | ✅ | |
| HELP | ✅ | |
| EXIT | ✅ | |
| Drive switching (A:, B:, C:) | ✅ | |
| BAT file execution | ✅ | ECHO/GOTO/REM/PAUSE/@ |
| CONFIG.SYS processing | ✅ | FILES/BUFFERS/LASTDRIVE/etc. |
| AUTOEXEC.BAT processing | ✅ | |
| Piping (|) | ❌ | |
| Redirection (>, >>, <) | ❌ | |
| IF/FOR batch commands | ❌ | |
| CALL batch command | ❌ | |
| Environment variable expansion (%) | ❌ | |
| PROMPT command | ❌ | |
| MORE | ❌ | |
| FIND | ❌ | |
| SORT | ❌ | |
| COMP/FC | ❌ | |
| XCOPY | ❌ | |
| FORMAT | ❌ | |
| CHKDSK | ❌ | |
| SYS | ❌ | |
| MODE | ❌ | |
| TREE | ❌ | |
| ATTRIB | ❌ | |
| LABEL | ❌ | |
| VOL | ❌ | |
| VERIFY | ❌ | |
| BREAK | ❌ | |
| CTTY | ❌ | |
| RECOVER | ❌ | |
| EDLIN | ❌ | |
| DEBUG | ❌ | |

### 4.6 Disk Image Support

| Feature | Status | Notes |
|---------|--------|-------|
| Load IMG/RAW disk images | ✅ | |
| Mount on drive letters | ✅ | A:-Z: |
| Export drive as IMG | ✅ | FAT12 1.44MB |
| Import IMG to C: drive | ✅ | |
| Auto-detect FAT12/FAT16 | ✅ | |
| Floppy geometries (160K-1.44MB) | ✅ | |
| Hard disk images (raw) | ❌ | No MBR / partition table parsing |
| Partition table support | ❌ | |
| Boot sector execution | ❌ | Boot sector loaded but not executed as 8086 code |
| VHD/VMDK/QCOW2 formats | ❌ | Low priority — only raw IMG supported |

### 4.7 Platform Support

| Feature | Status | Notes |
|---------|--------|-------|
| WinForms frontend | ✅ | PictureBox renderer |
| Blazor WebAssembly frontend | ✅ | Canvas via JS interop |
| IGraphicsRenderer abstraction | ✅ | |
| IEventRegistry abstraction | ✅ | |
| IStreamProvider abstraction | ✅ | |
| EmulatorLog diagnostics | ✅ | With severity levels |
| CPU instruction tracing | ✅ | |

---

## 5. Implementation Priority

### Phase 1 — Core CPU Completeness ✅ (Complete)
1. ✅ **0x0F two-byte opcodes**: Jcc near (0F 80-8F) — used by virtually all compiled programs
2. ✅ **ENTER/LEAVE** (C8/C9) — used by compiled C programs for stack frames
3. ✅ **PUSHA/POPA** (60/61) — used widely by 80186+ programs
4. ✅ **PUSH imm** (68/6A) — used by compilers for pushing constants
5. ✅ **IMUL imm** (69/6B) — three-operand multiply used by compilers
6. ✅ **MOVZX/MOVSX** (0F B6/BE) — used by 386+ compiled code
7. ✅ **ESC opcodes** (D8-DF) — x87 FPU stubs, skip ModR/M correctly

### Phase 2 — Critical DOS Kernel Infrastructure
1. ❌ **MCB chain** — proper memory management (alloc/free/resize), foundation for EXEC
2. ❌ **INT 21h/4Bh EXEC** — properly load and execute child programs with PSP/env/handle inheritance
3. ❌ **Environment block** — programs need PATH, COMSPEC, and custom env vars
4. ❌ **Current working directory tracking** — per-drive CWD (CDS table), real CHDIR
5. ❌ **Error code tracking** — last-error state for INT 21h/59h
6. ✅ **INT 21h/29h Parse filename** — needed by many programs
7. ✅ **INT 21h/45h-46h DUP/DUP2** — handle duplication
8. ✅ **INT 21h/5Ah-5Bh** — temp file / create new
9. 🔧 **INT 21h/44h IOCTL** — subfuncs 00h-0Bh done; need 02h-05h (read/write IOCTL data)

### Phase 3 — File System Completeness
1. ❌ **Subdirectory traversal** in FAT images — CD \SUBDIR, nested paths
2. ❌ **Proper file size** in FindFirst/FindNext DTA results
3. ❌ **MKDIR/RMDIR** real implementation on disk images
4. ❌ **Rename file** (INT 21h/56h) real implementation
5. ❌ **File attributes** (INT 21h/43h) — read actual attrs from FAT directory entry

### Phase 4 — Hardware & Interrupt Infrastructure
1. ❌ **INT 08h timer tick** (18.2 Hz) — many programs depend on this for timing
2. ❌ **BIOS Data Area** (0040:0000) — keyboard buffer, video state, timer ticks, drive count
3. ❌ **PIT timer** (port 0x40) — programs read this for high-res timing and random seeds
4. ❌ **PIC emulation** (ports 0x20/0x21) — IRQ masking and EOI for hardware interrupt handlers
5. ❌ **INT 23h Ctrl-C handler** — invoke program's registered handler
6. ❌ **INT 24h Critical error handler** — invoke on disk errors
7. ❌ **INT 2Fh Multiplex** — used by PRINT, SHARE, DOSKEY, and many TSRs
8. ❌ **VGA palette ports** (0x3C7-0x3C9) — programs set RGB palette via port I/O

### Phase 5 — BIOS Improvements
1. ✅ **INT 13h Disk services** — sector-level disk I/O for boot/install
2. ✅ **INT 10h/0Ch-0Dh** — pixel read/write for graphics modes
3. ✅ **INT 10h/07h** — proper scroll down
4. ✅ **INT 33h Mouse** — mouse driver services
5. ✅ **INT 15h/86h Wait** — timed delays
6. ❌ **INT 13h/05h Format track** — needed by FORMAT.COM
7. ❌ **INT 10h/10h VGA palette** — program-settable palette
8. ❌ **INT 10h scroll window** — use CH/CL/DH/DL corner coordinates instead of full-screen
9. ❌ **INT 16h shift flags** — report actual Shift/Ctrl/Alt state
10. ❌ **INT 16h/01h non-destructive peek** — check key without removing from buffer
11. ❌ **CGA/EGA graphics memory** — modes 04h/06h interlaced memory layout
12. ❌ **Additional video modes** — modes 00h-02h (40-col), 0Dh-12h (EGA/VGA)

### Phase 6 — Shell & Utilities
1. ❌ I/O redirection (>, >>, <)
2. ❌ Piping (|)
3. ❌ IF/FOR/CALL in batch files
4. ❌ Environment variable expansion (%VAR%)
5. ❌ PROMPT command (with $P$G etc.)
6. ❌ Additional commands (MORE, FIND, SORT, ATTRIB, TREE, FORMAT, CHKDSK, MODE, LABEL, VOL)
7. ❌ Real REN/RENAME implementation
8. ❌ Real MKDIR/RMDIR implementation

### Phase 7 — Advanced Features
1. ❌ FCB-based file operations (compatibility with older programs)
2. ❌ TSR support (INT 21h/31h, INT 27h)
3. ❌ Installable device driver support (CONFIG.SYS DEVICE=)
4. ❌ Hard disk image support with partition tables
5. ❌ Boot sector execution as 8086 code
6. ❌ EMS/XMS memory (HIMEM.SYS, EMM386)
7. ❌ Sound (PC speaker via PIT channel 2)

---

## 6. Gap Summary

| Severity | Count | Top Items |
|----------|-------|-----------|
| **Critical** | 2 | EXEC (child process loading), MCB memory chain |
| **High** | 7 | Subdirectory traversal, I/O redirection, INT 08h timer tick, environment block, CWD tracking, TSR, MKDIR/RMDIR real impl |
| **Medium** | ~25 | BDA population, I/O ports, shift key detection, file attributes, IOCTL subfuncs, FindFirst file sizes, CGA graphics memory, VGA palette, keyboard peek vs consume, scroll window coords, rename file, hardware IRQs, critical error handler, INT 2Fh multiplex |
| **Low** | ~30 | Serial/printer, protected mode opcodes, light pen, advanced mouse functions, RTC set, CMPXCHG/XADD, ROM write protection, boot sector execution, EMS/XMS, sound |
