# NES2ILRecomp

**NesLifter.Studio** — NES Static Recompiler / Lifter to .NET Assembly (PoC)

<img width="350" height="20" alt="logo" src="https://github.com/user-attachments/assets/02dc6a19-a6f2-47a2-ad2d-9ed3db5e8442" />

> ⚠️ **Very experimental.** Exists as a proof of concept. Currently tested only on Super Mario Bros ROM.

Example: 

<img width="258" height="267" alt="screenshot" src="https://github.com/user-attachments/assets/1aaafdbc-bc5e-4544-8288-c1725ef5eca5" />

---

## What is this?

NES2ILRecomp is a static binary recompiler that lifts NES (6502) ROM images into self-contained .NET executables. Instead of interpreting opcodes at runtime, it disassembles the 6502 machine code, translates each instruction into equivalent C#, and compiles the result into a standalone `Game.exe` via `CSharpCodeProvider`.

The generated executable includes:
- A fully lifted CPU core (all official + most unofficial 6502 opcodes)
- A dispatch table for indirect jumps / RTS / RTI / BRK
- A software interpreter fallback for dynamically-discovered code paths
- PPU stub with background & sprite rendering (256×240, WinForms)
- APU stub with square/triangle/noise synthesis (winmm.dll, 22050 Hz 8-bit mono)
- Memory bus with RAM / SaveRAM / PRG / CHR mapping
- Keyboard-mapped joystick input
- Frame throttling (~60 fps)

---

## Pipeline

```
┌─────────────────────────────────────────────────────────────────┐
│  1. Parse iNES header (PRG ROM, CHR ROM, mapper, mirroring)     │
│  2. Disassemble 6502 code (recursive descent from vectors)      │
│  3. Lift instructions → C# source (goto-based control flow)     │
│  4. Build dispatch table (JMP indirect / RTS / RTI / BRK)       │
│  5. Generate Memory Bus, PPU, APU, Mapper stubs, WinForms UI    │
│  6. Compile generated C# → Game.exe (CSharpCodeProvider)        │
└─────────────────────────────────────────────────────────────────┘
```

---

## Requirements

- **.NET Framework 2.0 – 4.8** (Windows)
- No third-party dependencies
- Single-file build (`Program.cs`)

---

## Usage

```
NesLifter.exe --input <rom.nes|folder> --output <dir> [options]
```

### Options

| Flag | Description |
|------|-------------|
| `-i, --input <path>` | Input `.nes` file or folder with ROMs |
| `-o, --output <dir>` | Output directory (default: `nes_lifted_output`) |
| `-r, --recursive` | Recursively process subdirectories |
| `--no-tray` | Do not create a system tray icon |
| `--wait` | Stay in tray / wait after completion |
| `--fresh` | Ignore saved state (start from scratch) |
| `--no-compile` | Only generate C# source, skip EXE compilation |
| `--no-source` | Do not save intermediate C# (not recommended) |
| `--keep-source` | Save intermediate C# (default) |
| `--checkpoint <min>` | Auto-save interval in minutes (default: 10) |
| `-h, --help` | Show help |

### Examples

```bash
# Single ROM
NesLifter.exe game.nes

# Folder, recursive, stay in tray when done
NesLifter.exe --input C:\roms --output C:\lifted -r --wait

# Generate source only, checkpoint every 5 minutes
NesLifter.exe --input C:\roms -r --no-compile --checkpoint 5
```

### Interactive mode (TUI)

If launched without arguments in an interactive console, a text-based menu appears:

```
NES Static Recompiler -- TUI
> Указать путь к ROM или папке
  Рекурсивная обработка папок: выкл
  Запустить рекомпиляцию
  Выход
```

---

## Output Structure

```
nes_lifted_output/
├── .neslifter.state.xml        # Resume state (processed files)
├── neslifter.config.ini        # Auto-created config reference
└── <RomName>/
    ├── <RomName>.exe           # Compiled lifted game
    ├── dynamic_targets.txt     # Manual forced disassembly addresses
    ├── dynamic_targets.log     # Runtime-discovered jump targets
    └── src/
        └── Game.generated.cs   # Intermediate C# source
```

---

## Dynamic Targets

The lifter uses recursive-descent disassembly from the RESET / NMI / IRQ vectors. Code reachable only through computed jumps (e.g. `JMP ($0006)`) may be missed statically.

To handle this:

1. **`dynamic_targets.txt`** — add hex addresses (one per line) to force disassembly:
   ```
   ; Format: 8231 / 0x8231 / $8231
   8231
   ```

2. **Runtime logging** — the generated EXE writes newly-discovered targets to `dynamic_targets.log`. Feed these back into `dynamic_targets.txt` and re-run the lifter for full coverage.

3. **Interpreter fallback** — at runtime, any address not in the static dispatch table is executed by a built-in 6502 interpreter (up to 4M instructions per dispatch).

---

## Supported Opcodes

- All **151 official** 6502 instructions
- **Unofficial / illegal** opcodes commonly found in NES ROMs:
  `LAX`, `SAX`, `DCP`, `ISB`, `SLO`, `RLA`, `SRE`, `RRA`, `ANC`, `ALR`, `ARR`, `ANE`, `LXA`, `AXS`, `SHY`, `LAS`, `AXA`, `KIL`, various `NOP` variants

---

## Limitations

- **Mapper support**: Mapper 0 (NROM) only; other mappers use a stub
- **PPU**: Software rendering, no mid-scanline effects, no MMC2/4 CHR banking
- **APU**: Simplified synthesis (no DMC, no sweep, no envelope decay)
- **No save states** in the generated EXE
- **Large ROMs** may produce very large C# files (tens of MB)
- Tested primarily on **Super Mario Bros (Mapper 0, 32 KB PRG)**

---

## Architecture (single file)

| Class | Role |
|-------|------|
| `Program` | Entry point, CLI, TUI, tray, logging |
| `Options` | Argument parsing |
| `Pipeline` | Orchestrates file collection → disassembly → lift → compile |
| `NesRom` | iNES parser (header, PRG, CHR, vectors) |
| `Cpu6502` | Full opcode table (256 entries) |
| `Disassembler` | Recursive-descent 6502 disassembler |
| `Lifter` | Instruction → C# code generator |
| `StateManager` | XML-based resume/checkpoint |
| `TrayManager` | WinForms NotifyIcon background processing |
| `PointerScanner` | Heuristic ZP pointer target discovery |

---

## License

Licensed under GPLv3 

And this is proof of concept. Use at your own risk. 

---

*Previously known as NesToDotNetRecompiler.*
