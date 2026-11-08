# Operations and architecture

## Platform permissions

Windows uses `OpenProcess`, `VirtualQueryEx`, `ReadProcessMemory`, and `WriteProcessMemory`. A same-user process often works without elevation; elevated targets can require an elevated agent/server. Protected processes may remain inaccessible. No privilege is enabled automatically.

Linux reads `/proc/PID/maps` and transfers memory using `process_vm_readv`/`process_vm_writev`. Ownership, dumpability, namespaces, Yama `ptrace_scope` and capabilities govern access. Launching a target as a child or a target-provided `PR_SET_PTRACER` relationship can permit access. Do not globally weaken ptrace restrictions as an installation step. Wine/Proton games are host Linux processes; discovery requires selecting the correct game process. Their compatibility is game-specific and is not claimed by the fixture tests.

macOS uses `task_for_pid`, `mach_vm_region`, `mach_vm_read_overwrite`, and `mach_vm_write`. The binary has debugger and .NET runtime entitlements. Ad-hoc signing alone does not grant broad debugger trust. Target signing/get-task-allow, system debugger authorization, user privileges, Hardened Runtime and SIP determine attachability. Some test environments require running an already trusted MCP host/server with appropriate privileges. Protected targets can reject attachment even with elevation; MemoryForge reports that failure. Disabling SIP or re-signing commercial games is not an installation requirement or implemented feature.

macOS CI signs the deliberately attachable fixture with `get-task-allow` and runs the separate-process smoke test with runner-local administrator privileges. This validates the Mach backend against an allowed target, not access to arbitrary signed games.

## Installation and removal

Windows MSI installs to Program Files with administrator authorization and adds its directory to the system PATH. It does not install a service or driver. Restart the MCP client to acquire the new PATH. Remove via Installed Apps; user audit journals are retained.

DEB packages install under `/usr/lib/memoryforge` with `/usr/bin/memoryforge` as a symlink. Use your package manager to upgrade/remove. The runtime is bundled, but native OS libraries are dependencies.

macOS PKG installs under `/usr/local/lib/memoryforge` with a `/usr/local/bin/memoryforge` symlink. To remove, stop clients, verify these exact paths belong to MemoryForge, remove the symlink and the named `memoryforge` library directory, then run `sudo pkgutil --forget us.vorn.memoryforge`. Do not remove `/usr/local/lib` or `/usr/local/bin` themselves. Audit journals are retained.

Portable archives contain a self-contained executable, configuration example, README, LICENSE and bundled third-party notices where supplied by .NET. Extract to a dedicated directory and configure the executable's absolute path. Single-file .NET may extract native runtime components to the user's temporary bundle cache; its directory must be writable and executable.

The first release has no commercial code-signing certificate or Apple notarization. To enable trusted publisher distribution, add organization-owned signing credentials to a protected GitHub environment, sign the executable/installer before checksums and attestations, and notarize/staple the macOS installer. Never commit signing credentials. GitHub provenance is independently verifiable using `gh attestation verify FILE --repo vornet/memoryforge-mcp`.

## Failure handling and audit

Tool failures use MCP `isError` results with actionable expected error messages. Unexpected errors are handled by the MCP SDK and logged to stderr. Invalid requests do not terminate the server. A failed process-identity check requires a fresh attachment. Handles are scoped to one server process and one process lifetime.

Writes are serialized per session. A durable `prepared` record precedes the native write; `verified`, `verification_failed`, or `write_failed_or_partial` follows. Disk errors before preparation prevent writing. Disk errors after modification can leave the last durable state `prepared`; inspect the process and journal before retrying. The journal is not a transactional database or tamper-proof log. Multiple server processes use separate default journals; a custom shared filename deliberately allows only one writer.

Do not replay old write records blindly: ASLR, restarts, allocation churn and updates invalidate addresses. In-session undo checks expected current bytes but cannot guarantee object identity if an allocation was recycled in the same process. Agents should revalidate entity identity before modification or undo.

Default journals are not rotated automatically. Keep them according to your storage/privacy policy. On Unix newly created journal directories are owner-only, and files are owner read/write; existing parent directory permissions are preserved. On Windows they inherit the current user's application-data ACLs.

Scans are best-effort live observations. Partial reads and unmapped pages are expected. Two background workers bound active scanning; queued jobs and cancellation are represented explicitly. Completed candidates remain server-side until released/detached; there is no whole-process memory dump sent to the model. Payload storage is bounded but object overhead and runtime allocations add memory use beyond payload limits.

## Code layout

- `MemoryForge.Core`: native OS adapters, value/pattern codecs, session lifecycle, write journal, asynchronous scan engine.
- `MemoryForge.Server`: .NET hosting and official MCP C# SDK, tools, expected-error translation, CLI diagnostic.
- `MemoryForge.Tests`: numeric precision, endian encoding, boundaries, refinements, cancellation, preconditions, conditional undo, native memory tests.
- `MemoryForge.Fixture` and `scripts/smoke.py`: end-to-end MCP and independent-process verification. Linux fixture permits sibling ptrace solely for testing.
- `scripts/package.ps1` and `packaging/`: native self-contained packages. No Cheat Engine code is bundled.

Native module bases are returned on Windows. Unix module listing groups file-backed mappings by path; the minimum address is not always a load bias. macOS mapping names depend on `proc_regionfilename` access. For reliable version-specific signatures inspect the actual mapped image and revalidate before applying offsets.

## Validation boundaries

CI proves native fixture access on each listed OS/architecture, MCP wire compatibility, and package installation/removal. It does not certify every OS version, game engine, game, anti-cheat environment, Wine/Proton configuration, or MCP client. No model-based claim of universal automatic cheating is made. Agent research, gameplay interaction and semantic verification are intentionally separate from this tool server.
