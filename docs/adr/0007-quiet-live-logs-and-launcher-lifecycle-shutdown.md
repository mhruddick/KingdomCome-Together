# 0007. Quiet Default Agent Telemetry (`--verbose` Opt-In) & Unified Companion / `trace_server` Shutdown

- **Status**: Accepted (PR #8 — QOL & Lifecycle)

## Context

Live co-op sessions exposed two day-to-day usability pain points:
1. **84% terminal & `agent.log` frame spam**: Out of `211,636` lines in a live `agent.log`, `53.7%` were raw `[combat] pipe frame 0xXX (N bytes)` lines (where newer native opcodes `0x87`–`0xAE` missed an old hardcoded exclusion list in `CombatPipe.ReadLoopAsync`), `19.2%` were 25 Hz `[pos]` lines, and `11.0%` were 20 Hz `[ghost N]` lines. Because `KCDMP_launcher.exe` builds as `<OutputType>Exe</OutputType>` and shares its console with `KcdMpClient.exe`, this drowned out meaningful events (`MP-*`, joins, saves, loot, quests, sleep votes) and wrote ~25–35 MB/hr of noise that `LogPack`'s `NoiseRules` immediately threw away anyway.
2. **Orphaned processes, lingering console window & `trace_server` tray icon on exit**:
   - Closing the launcher via the Windows title-bar `X` button (`WM_CLOSE`) exited `app.Run()` without stopping `KcdMpClient`, `KcdMpServer`, or `KcdMpMasterServer`.
   - Even clicking in-app `EXIT -> QUIT` (`ConfirmExit()`) stopped `hostedRelayProcess` and `hostedMasterServerProcess` but forgot `agentProcess` (`KcdMpClient.exe`), leaving `KcdMpClient.exe` attached to the inherited console window so the terminal stayed open on screen.
   - Launching KCD2 Modding Tools (`KingdomCome.exe`) automatically spawns `Tools\TraceServer\trace_server.run.exe`, which intercepts `WM_CLOSE` to hide in the Windows system tray and remained running after both the game and launcher closed.

## Decision

1. **Quiet default telemetry with `--verbose` opt-in**:
   - Added `ClientConfig.VerboseLogging` (default `false`; CLI `--verbose` / `--no-verbose`) and `CombatPipe.VerboseLogging`.
   - Gated raw `[combat] pipe frame ...` (`CombatPipe.cs`), per-tick `[pos] <x> <y> <z> ...` (`GameBridge.cs`), and per-tick `[ghost N] <x> <y> <z> ...` (`GameBridge.cs`) behind `VerboseLogging`, while keeping all edge warnings (`[pos] mod emitter silent/resumed`), `[ping]`, `MP-W207-Q` (consumed by `LogPack`'s `W218Section`), `MP-DIGEST`, and domain events active by default.
2. **Unified `LauncherLifecycle` shutdown + loopback `/shutdown` IPC + Win32 Job Object**:
   - Added loopback-only `POST /shutdown` to `VersionIpcServer.cs` so `KcdMpClient.exe` cancels its run token cleanly (disconnecting from the relay, clearing Discord Rich Presence, and releasing joiner save locks via `ReleaseSaveLockOnExit`) before exiting.
   - Added `LauncherLifecycle` (`KCDMP_launcher/Models/LauncherLifecycle.cs`) wired to Photino `RegisterWindowClosingHandler`, `ConfirmExit()`, `Program.Main` `finally`, and `AppDomain.ProcessExit`:
     - Attaches `KcdMpClient`, `KcdMpServer`, and `KcdMpMasterServer` (never `KingdomCome.exe`) to a Win32 Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK`) so even a launcher crash leaves zero orphaned `KcdMp*` processes.
     - Gracefully requests `/shutdown` on `KcdMpClient`, stops tracked + install-directory companion processes (`KcdMpClient`, `KcdMpServer`, `KcdMpMasterServer`), and stops KCD2Mod's `Tools\TraceServer\trace_server*.exe` (both when `KingdomCome.exe` exits and when the launcher closes) by posting `WM_QUIT` (`0x0012`) to its threads first so Qt deletes its tray icon cleanly, falling back to `Kill` + notification-area sweep if needed.

## Upstream Conflict Guide

- **Files**: `dotnet/KcdMp.Client/ClientConfig.cs`, `dotnet/KcdMp.Client/CombatPipe.cs`, `dotnet/KcdMp.Client/GameBridge.cs`, `dotnet/KcdMp.Client/VersionIpcServer.cs`, `KCDMP_launcher/Models/LauncherLifecycle.cs`, `KCDMP_launcher/Program.cs`, `KCDMP_launcher/Pages/Home.razor.cs`, `Home.Wo127.cs`, `Home.Wo154.cs`, `Home.Wo159.cs`.
- **Invariants to preserve**:
  - Never attach `KingdomCome.exe` to the kill-on-close Job Object (a launcher crash must never kill an active game with unsaved progress).
  - Keep `MP-W207-Q` enabled by default unless `dotnet/KcdMp.LogPack/W218Section.cs` is updated alongside it.
