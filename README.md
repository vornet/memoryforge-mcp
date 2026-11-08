# MemoryForge MCP

[![Build and release](https://github.com/vornet/memoryforge-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/vornet/memoryforge-mcp/actions/workflows/ci.yml)

A .NET 10 MCP server exposing local process-memory capabilities to AI agents on Windows, Linux and macOS. The agent decides what to investigate, researches clues with its own web tools, and interprets game behavior. MemoryForge handles process access, scanning, patterns, pointer chains, typed reads, guarded writes, observation, and conditional undo.

Original C# implementation. No Cheat Engine source, drivers, Lua runtime, or other Cheat Engine binaries are included. This is a focused memory-tool server, not complete Cheat Engine feature parity.

## Install

Download an installer and `SHA256SUMS` from [GitHub Releases](https://github.com/vornet/memoryforge-mcp/releases/latest). Each release has self-contained binaries; users do not need to install .NET.

| System | Architectures | Installer | Installed executable |
|---|---|---|---|
| Windows 10/11 | x64 | `.msi` | `C:\Program Files\MemoryForge\memoryforge.exe` |
| Linux, glibc | x64, ARM64 | `.deb` | `/usr/bin/memoryforge` |
| macOS | Intel x64, Apple Silicon ARM64 | `.pkg` | `/usr/local/bin/memoryforge` |

Portable ZIP/TAR.GZ archives are available for every target. On Linux use a supported .NET 10 glibc distribution with ICU, OpenSSL 3, libstdc++, and zlib. DEB installers target Ubuntu 22.04/24.04 and compatible Debian systems; Alpine/musl is not supported. macOS requires a version supported by .NET 10. Match the architecture of the host binary to the OS; pointer-chain width can independently be 4 or 8 bytes.

Windows: run the MSI, then restart the terminal/agent to pick up the PATH change. Uninstall through Installed Apps. Linux: `sudo apt install ./memoryforge-VERSION-linux-x64.deb`; uninstall with `sudo apt remove memoryforge-mcp`. macOS: open the matching PKG; see [operations](docs/operations.md) for removal.

Installers are currently **unsigned** by a commercial Windows publisher or Apple Developer ID. macOS executables are ad-hoc signed with debugger and .NET runtime entitlements; the PKG is not notarized. GitHub provenance attestations and SHA-256 checksums establish build origin, not OS publisher trust. OS authorization still determines which targets are attachable. See [platform permissions](docs/operations.md#platform-permissions).

Run `memoryforge --version` and `memoryforge --doctor`. Doctor performs a real self-process read/write/readback and memory-map query; success does not imply permission to attach to other processes.

## Connect your agent

Add a stdio MCP entry to your client's configuration:

```json
{
  "mcpServers": {
    "memoryforge": {
      "command": "memoryforge",
      "args": []
    }
  }
}
```

Use the absolute installed path if the client's environment does not include the install directory. On Windows JSON paths require doubled backslashes or forward slashes; substitute your actual username rather than using `%LOCALAPPDATA%` in a client that does not expand environment variables. The installer includes a configuration template. No credentials or network listener are required. Logs go to stderr; stdout is reserved for MCP JSON-RPC.

Then ask your agent, for example: **“Try to set my active character's money to 10,000. Investigate the game and verify the result.”** The agent needs its own web/browser tools to research online clues. No model or search provider is embedded in this server.

## Tools

| Area | Tools |
|---|---|
| Discovery | `get_capabilities`, `list_processes`, `list_modules`, `list_regions` |
| Sessions | `attach_process`, `list_sessions`, `detach_process` |
| Inspection | `read_memory`, `resolve_pointer_chain`, `observe_memory` |
| Discovery scans | `start_scan`, `scan_status`, `scan_results`, `refine_scan`, `cancel_scan`, `release_scan` |
| Modification | `write_memory`, `undo_write`, `write_history` |

Each tool advertises a parameter schema and description. `get_capabilities` also returns the agent investigation guide, platform, types and budgets.

- Addresses are hex (`"0x1234"`) or decimal strings. Numeric values are strings to preserve 64-bit precision.
- Types: signed/unsigned 8/16/32/64-bit integers, Float32/Float64, Utf8, Utf16, Bytes. Little endian by default, optional big endian.
- Patterns support whole-byte and nibble wildcards: `48 8B ?? ?F`.
- Known-value and unknown-value scans support subsequent exact, not-equal, changed, unchanged, increased, decreased, increased-by, decreased-by, greater-than and less-than refinements. Numeric exact matching is byte-exact, including floating-point representation.
- Background jobs return an ID immediately. Poll status until `completed`, `cancelled` or `failed`, then page results. There is no requirement for client support of optional MCP Tasks extensions.
- Changed/increased/decreased compare with the last successful scan, and successful refinement updates each retained candidate's baseline. Results are stored snapshots, not live reads.
- End addresses are exclusive. Default numeric alignment is the type width; use alignment 1 for unaligned values. Text and patterns default to alignment 1.
- Pointer chains dereference the current address, then add each signed offset. Module base plus an initial offset must be calculated before passing the initial address.

## Writes, recovery and limits

Sessions are read-only by default. Attach with `writable=true` to write. Every write requires an expected value in the same encoding and length. The server checks current bytes, flushes original/desired bytes to an append-only journal, writes, and checks immediate readback. **This is not an atomic compare-and-swap or a game-wide transaction.** The target can mutate memory at any point.

`verified` means the byte readback matched. Establishing that the intended character's actual balance changed is the agent's responsibility. Display caches, duplicated fields, moving objects, encoded values, and server-authoritative state require further evidence. No process pause or coherent snapshot is provided.

Undo works only for a verified write in the same live session, if current bytes still match the write. It restores bytes; it cannot reverse subsequent purchases, game logic, or saved-game effects. Prepared/partial/failed writes remain in `write_history`; inspect current memory before recovery. Audit records survive a crash, but session handles and automatic undo IDs do not.

Default audit directory is the user's local application-data directory under `MemoryForge`, one journal per server process. `--audit-log /absolute/path/audit.jsonl` selects a specific file (one writer at a time). Records contain PID, session, address, old/new bytes, timestamp, ID and state. Protect these files as process data; they are not automatically deleted or rotated.

Bounded resources: 4 sessions, 4 retained scans/session, 2 scan workers, 100,000 candidates/scan, 16 MiB retained byte payload/scan, 1 MiB/read, 4 KiB/write, 256 bytes/search value, 500 entries/result page, and 1,000 journaled writes/session. A scan defaults to 1 GiB of reads (up to 8 GiB explicitly). Wider scans can require dividing the address space. Reaching limits or losing readable pages marks results `incomplete`; refinements preserve that warning. Detach cancels scans and releases access. State is intentionally session-local and never reuses stale addresses after a restart.

This release does not include injection, a debugger/disassembler, automatic pointer-path discovery, Lua/Auto Assembler compatibility, kernel drivers, speed modification, anti-cheat bypass, or a game-control/browser integration. Ordinary OS process permissions are respected. Do not expose this local stdio server through an unauthenticated remote bridge.

## Build, test and release

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
python scripts/smoke.py --server src/MemoryForge.Server/bin/Release/net10.0/memoryforge.exe --fixture tests/MemoryForge.Fixture/bin/Release/net10.0/MemoryForge.Fixture.exe
pwsh scripts/package.ps1 -Runtime win-x64 -Version 1.0.0
```

On Unix omit `.exe` in smoke paths. Packaging runs on the corresponding host OS. The fixture is deliberately attachable and is never included in installers.

Every branch push and pull request builds/tests/packages five native targets: Windows x64, Linux x64/ARM64, and macOS Intel/ARM64. CI also runs a separate-process MCP workflow that discovers duplicate balances, refines after a game-side change, conditionally writes only the target character, verifies, undoes, tests a cross-chunk wildcard pattern, and rejects bad requests.

After **all** targets, dependency checks, and native installer smoke tests pass, each commit pushed to `main` automatically creates an immutable GitHub release `v1.0.N` (`N` is the workflow run number) with installers, portable archives, dependency inventory and checksums. PRs and other branches only upload CI artifacts. Release publishing is serialized and has its own write permission; matrix builds are read-only. GitHub Actions and NuGet dependencies are pinned; Dependabot proposes updates. This repository's `main` is the automatic production release branch.

See [operations and architecture](docs/operations.md) for permissions, operational limitations and recovery.
