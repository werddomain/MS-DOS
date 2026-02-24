This folder is populated by scripts/build-msdos4.ps1.

Expected artifacts include:
- IO.SYS
- MSDOS.SYS
- COMMAND.COM
- core DOS 4.0 command and device binaries copied by v4.0/src/CPY.BAT

Run from repository root:
powershell -ExecutionPolicy Bypass -File .\MsDos.Clone\scripts\build-msdos4.ps1
