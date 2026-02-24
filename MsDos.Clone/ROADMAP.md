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
| 0x60 | PUSHA | ❌ | 80186+ |
| 0x61 | POPA | ❌ | 80186+ |
| 0x68 | PUSH imm16 | ❌ | 80186+ |
| 0x6A | PUSH imm8 (sign-ext) | ❌ | 80186+ |
| 0x6C-0x6D | INSB/INSW | ❌ | 80186+ |
| 0x6E-0x6F | OUTSB/OUTSW | ❌ | 80186+ |

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
| 0x69 | IMUL r16, r/m16, imm16 | ❌ | 80186+ |
| 0x6B | IMUL r16, r/m16, imm8 | ❌ | 80186+ |
| 0x62 | BOUND r16, mem | ❌ | 80186+ |

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
| 0xC8 | ENTER imm16, imm8 | ❌ | 80186+ |
| 0xC9 | LEAVE | ❌ | 80186+ |

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

### 1.8 Two-byte Opcodes (0x0F prefix) — 80186/80286+

These are needed by many real-world DOS programs compiled for 80186+.

| Opcode(s) | Mnemonic | Status | Notes |
|-----------|----------|--------|-------|
| 0F 80-8F | Jcc near rel16 (16-bit offsets) | ❌ | Required by most compilers |
| 0F B6 | MOVZX r16, r/m8 | ❌ | 80386+ but common |
| 0F B7 | MOVZX r16, r/m16 | ❌ | 80386+ |
| 0F BE | MOVSX r16, r/m8 | ❌ | 80386+ but common |
| 0F BF | MOVSX r16, r/m16 | ❌ | 80386+ |
| 0F A4/A5 | SHLD | ❌ | 80386+ |
| 0F AC/AD | SHRD | ❌ | 80386+ |
| 0F AF | IMUL r16, r/m16 | ❌ | 80386+ |
| 0F B0/B1 | CMPXCHG | ❌ | 80486+ |
| 0F C0/C1 | XADD | ❌ | 80486+ |
| 0F 01 | LGDT/SGDT/LIDT/SIDT | ❌ | Protected mode, low priority |

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
| 0Dh | Disk reset | ❌ | Flush all disk buffers |
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
| 1Bh | Get allocation info (default drive) | ❌ | |
| 1Ch | Get allocation info (specific drive) | ❌ | |
| 21h | Random read (FCB) | ❌ | Legacy FCB, low priority |
| 22h | Random write (FCB) | ❌ | Legacy FCB, low priority |
| 23h | Get file size (FCB) | ❌ | Legacy FCB, low priority |
| 24h | Set random record number (FCB) | ❌ | Legacy FCB, low priority |
| 25h | Set interrupt vector | ✅ | |
| 26h | Create new PSP | ❌ | |
| 27h | Random block read (FCB) | ❌ | Legacy FCB, low priority |
| 28h | Random block write (FCB) | ❌ | Legacy FCB, low priority |
| 29h | Parse filename into FCB | ❌ | Needed by some programs |

### 2.3 Date/Time & System Info (AH=2Ah–36h)

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 2Ah | Get date | ✅ | |
| 2Bh | Set date | ❌ | |
| 2Ch | Get time | ✅ | |
| 2Dh | Set time | ❌ | |
| 2Eh | Set verify flag | ✅ | |
| 2Fh | Get DTA address | ✅ | |
| 30h | Get DOS version | ✅ | Returns 4.0 |
| 31h | Terminate and stay resident (TSR) | ❌ | Complex |
| 33h | Get/set Ctrl-Break flag | ✅ | |
| 34h | Get InDOS flag address | ❌ | |
| 35h | Get interrupt vector | ✅ | |
| 36h | Get disk free space | ✅ | |

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
| 44h | IOCTL | 🔧 | Only subfunc 00h |
| 45h | Duplicate handle (DUP) | ❌ | |
| 46h | Force duplicate handle (DUP2) | ❌ | |
| 47h | Get current directory | ✅ | Always returns root |
| 48h | Allocate memory | ✅ | Simplified |
| 49h | Free memory | ✅ | Stub |
| 4Ah | Resize memory block | ✅ | Stub |
| 4Bh | EXEC (load and execute) | 🔧 | Logs only, no child process |
| 4Ch | Terminate with return code | ✅ | |
| 4Dh | Get return code | ✅ | |
| 4Eh | Find first matching file | ✅ | |
| 4Fh | Find next matching file | ✅ | |
| 50h | Set PSP segment | ✅ | |
| 51h | Get PSP segment | ✅ | |
| 54h | Get verify flag | ✅ | |
| 55h | Create child PSP | ✅ | Stub |
| 56h | Rename file | ✅ | Stub |
| 57h | Get/set file date/time | ✅ | |
| 58h | Get/set memory allocation strategy | ✅ | |
| 59h | Get extended error information | ✅ | Always returns "no error" |
| 5Ah | Create temporary file | ❌ | |
| 5Bh | Create new file (fail if exists) | ❌ | |
| 5Ch | Lock/unlock file region | ❌ | |
| 5Dh | Set extended error info | ❌ | Undocumented |
| 5Eh | Network services | ❌ | Low priority |
| 5Fh | Network redirection | ❌ | Low priority |
| 60h | Canonicalize filename | ❌ | |
| 62h | Get PSP address | ✅ | |
| 63h | Get lead byte table (DBCS) | ✅ | |
| 65h | Get extended country info | 🔧 | Returns unsupported |
| 66h | Get/set global code page | ✅ | |
| 67h | Set handle count | ❌ | |
| 68h | Commit file (flush) | ❌ | |
| 6Ch | Extended open/create | ❌ | DOS 4.0+ |

---

## 3. BIOS Interrupts

### 3.1 INT 10h — Video Services

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 00h | Set video mode | ✅ | Modes 03h, 04h, 06h, 13h |
| 01h | Set cursor shape | ✅ | |
| 02h | Set cursor position | ✅ | |
| 03h | Get cursor position/shape | ✅ | |
| 04h | Get light pen position | ❌ | Rarely used |
| 05h | Set active display page | ✅ | |
| 06h | Scroll up | ✅ | |
| 07h | Scroll down | 🔧 | Delegates to scroll up |
| 08h | Read char/attr at cursor | ✅ | |
| 09h | Write char/attr at cursor | ✅ | |
| 0Ah | Write char at cursor (keep attr) | ✅ | |
| 0Bh | Set color palette | ❌ | |
| 0Ch | Write pixel | ❌ | Graphics modes |
| 0Dh | Read pixel | ❌ | Graphics modes |
| 0Eh | TTY output | ✅ | |
| 0Fh | Get video mode | ✅ | |
| 10h | Set palette registers | 🔧 | Stub |
| 11h | Character generator | 🔧 | Stub |
| 12h | Video subsystem configuration | ✅ | |
| 13h | Write string | ✅ | |

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
| 00h | Reset disk system | ❌ | |
| 01h | Get disk status | ❌ | |
| 02h | Read sectors | ❌ | Required for disk image boot |
| 03h | Write sectors | ❌ | |
| 04h | Verify sectors | ❌ | |
| 05h | Format track | ❌ | |
| 08h | Get drive parameters | ❌ | |
| 15h | Get disk type | ❌ | |
| 16h | Detect disk change | ❌ | |

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
| 86h | Wait | ❌ | |
| 87h | Extended memory copy | ❌ | |
| 88h | Get extended memory size | ❌ | |
| C0h | Get system config | ❌ | |

### 3.7 INT 16h — Keyboard Services

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 00h | Wait for keypress | ✅ | |
| 01h | Check for keypress | ✅ | |
| 02h | Get shift flags | ✅ | Always returns 0 |
| 03h | Set typematic rate | ❌ | |
| 05h | Store key in buffer | ❌ | |
| 10h | Wait for keypress (enhanced) | ✅ | |
| 11h | Check for keypress (enhanced) | ✅ | |
| 12h | Get extended shift flags | ✅ | |

### 3.8 INT 17h — Printer Services

| AH | Function | Status | Notes |
|----|----------|--------|-------|
| 00h | Print character | ❌ | Low priority |
| 01h | Initialize printer | ❌ | Low priority |
| 02h | Get printer status | ❌ | Low priority |

### 3.9 INT 19h — Bootstrap Loader

| Function | Status | Notes |
|----------|--------|-------|
| Reboot | ❌ | Could restart emulation |

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
| 0000h | Reset/detect mouse | ❌ | |
| 0001h | Show cursor | ❌ | |
| 0002h | Hide cursor | ❌ | |
| 0003h | Get position/button status | ❌ | |
| 0004h | Set position | ❌ | |
| 0007h | Set horizontal limits | ❌ | |
| 0008h | Set vertical limits | ❌ | |
| 000Ch | Set interrupt subroutine | ❌ | |

---

## 4. System Features

### 4.1 Memory Management

| Feature | Status | Notes |
|---------|--------|-------|
| 1 MB address space | ✅ | MemoryBus class |
| Segment:offset addressing | ✅ | |
| MCB (Memory Control Block) chain | ❌ | For proper alloc/free |
| UMB (Upper Memory Blocks) | ❌ | Low priority |
| HMA (High Memory Area) | ❌ | Low priority |

### 4.2 File System

| Feature | Status | Notes |
|---------|--------|-------|
| FAT12 disk image reading | ✅ | DiskImageLoader |
| FAT16 disk image reading | ✅ | DiskImageLoader |
| FAT12 disk image writing | ✅ | DiskImageWriter |
| FAT chain following | ✅ | |
| Subdirectory traversal | ❌ | Only root directory |
| Long filename support | ❌ | Not needed (DOS 8.3 only) |
| File attribute support | 🔧 | Always returns archive |
| Volume label support | ❌ | |
| Disk image write-through | ❌ | Writes are in-memory only |

### 4.3 Process Management

| Feature | Status | Notes |
|---------|--------|-------|
| COM file loading | ✅ | BinaryLoader |
| EXE (MZ) file loading | ✅ | BinaryLoader with relocations |
| PSP setup | ✅ | |
| Command tail | ✅ | |
| Child process execution (EXEC) | ❌ | INT 21h/4Bh stub only |
| TSR (Terminate and Stay Resident) | ❌ | |
| Environment block | ❌ | |

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
| REN/RENAME | ✅ | |
| MKDIR/MD | ✅ | |
| RMDIR/RD | ✅ | |
| CD/CHDIR | ✅ | Partial (no real path tracking) |
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

### 4.6 Disk Image Support

| Feature | Status | Notes |
|---------|--------|-------|
| Load IMG/RAW disk images | ✅ | |
| Mount on drive letters | ✅ | A:-Z: |
| Export drive as IMG | ✅ | FAT12 1.44MB |
| Import IMG to C: drive | ✅ | |
| Auto-detect FAT12/FAT16 | ✅ | |
| Floppy geometries (160K-1.44MB) | ✅ | |
| Hard disk images | ❌ | |
| Partition table support | ❌ | |

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

### Phase 1 — Core CPU Completeness (Highest Priority)
1. ❌ **0x0F two-byte opcodes**: Jcc near (0F 80-8F) — used by virtually all compiled programs
2. ❌ **ENTER/LEAVE** (C8/C9) — used by compiled C programs for stack frames
3. ❌ **PUSHA/POPA** (60/61) — used widely by 80186+ programs
4. ❌ **PUSH imm** (68/6A) — used by compilers for pushing constants
5. ❌ **IMUL imm** (69/6B) — three-operand multiply used by compilers
6. ❌ **MOVZX/MOVSX** (0F B6/BE) — used by 386+ compiled code

### Phase 2 — Critical DOS Services
1. ❌ **INT 21h/4Bh EXEC** — properly load and execute child programs
2. ❌ **INT 21h/29h Parse filename** — needed by many programs
3. ❌ **INT 21h/43h File attributes** — proper implementation
4. ❌ **INT 21h/44h IOCTL** — additional subfunctions
5. ❌ **INT 21h/45h-46h DUP/DUP2** — handle duplication
6. ❌ **INT 21h/5Ah-5Bh** — temp file / create new
7. ❌ **MCB chain** — proper memory management

### Phase 3 — BIOS Services
1. ❌ **INT 13h Disk services** — sector-level disk I/O for boot/install
2. ❌ **INT 10h/0Ch-0Dh** — pixel read/write for graphics modes
3. ❌ **INT 10h/07h** — proper scroll down
4. ❌ **INT 33h Mouse** — mouse driver services
5. ❌ **INT 15h/86h Wait** — timed delays

### Phase 4 — Shell & Utilities
1. ❌ I/O redirection (>, >>, <)
2. ❌ Piping (|)
3. ❌ IF/FOR/CALL in batch files
4. ❌ Environment variable expansion
5. ❌ PROMPT command
6. ❌ Additional commands (MORE, FIND, ATTRIB, TREE)

### Phase 5 — Advanced Features
1. ❌ Subdirectory traversal in FAT images
2. ❌ FCB-based file operations (compatibility)
3. ❌ TSR support
4. ❌ Device driver support
5. ❌ Environment block for processes
6. ❌ Hard disk image support
