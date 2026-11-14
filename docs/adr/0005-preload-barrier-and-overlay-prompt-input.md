# 0005. Synchronous Pre-Load Barrier for `PostLoadReconstruction` & Overlay Actionmap / Native `F11`–`F12` Fallback

- **Status**: Accepted (PR #6 — Live Playtest Hotfix 1)

## Context

Two issues surfaced during our first two-PC live playtest on `0.65.0`:
1. **Joiner `PostLoadReconstruction` timeout on in-world reload**: Joining from the main menu worked cleanly (`wo216::g_role == 0`), but when the host reloaded a save while both players were already in-world, `KCD2MP_Wo124LoadGame` (`wh_sys_LoadGame`) executed on the joiner **before** `_where` switched to `Loading` or `wo216`/`wo217`/`wo218` dropped from `role = 2` to `0`. During `wh_sys_LoadGame`, `wo216`'s `spawn_detour` and `layer_detour` recorded every deserialized entity and `b_tick` hid/suspended NPCs (`wh_ai_PauseNPC`) right as CryEngine entered `PostLoadReconstruction` — stalling reconstruction until CryEngine's ~55 s `"Loading screen timeouted while still in post load reconstruction!"` error.
2. **Unresponsive `F11` / `F12` on sleep & co-op prompts**:
   - `tools/Apply-DirectInstall.ps1` wiped `Mods\kdcmp\Data\kdcmp_keys.pak` during updates without running `KcdMpClient.exe --keys-pak`.
   - Even with `kdcmp_keys.pak` mounted, `kcd2mp_dice_bank` (`F11`) and `kcd2mp_dice_yield` (`F12`) lived solely in the `interaction` actionmap (included only by `player` at priority 1), which CryEngine suppresses whenever any `exclusivity="1"` actionmap at priority `>= 3` is active (`sitting_bed`, `sitting_chair`, `horse_mounted`, `dialog`, or inventory/stash/map/shop UI).
   - Unquoted numeric vote IDs passed into Lua (`KCD2MP_W140Prompt(33554433, ...)`) could lose precision in CryEngine's 32-bit float Lua (`> 2^24`).

## Decision

1. **Synchronous pre-load barrier + native spawn-storm circuit breaker**:
   - Added `Wo136PrepareBeforeLoadAsync` (`GameBridge.Wo136.cs`) awaited **before** all four `KCD2MP_Wo124LoadGame` call sites (`Wo124`, `Wo125`, `Wo175`), synchronously setting `_where = GameWhere.Loading`, arming `KCD2MP_W136Hold`, pushing `wo216`/`wo217`/`wo218` native roles to `0`, and calling `Wo141ForgetAsync`.
   - Updated `native/KCDMP/wo216.cpp` (`kOpConfig` + `b_stop`) to immediately disarm `spawn_detour`/`layer_detour`, clear `g_spawned`/`g_layersOn`/`g_layerBorn`, and call `npcdrive::drop_all_for_load()`. Added a single-frame spawn-storm breaker (`kLoadSpawnStorm = 48` spawns in one frame while `role == 2`) so even a manual console/menu load disarms `wo216` before `b_hide` or `wh_ai_PauseNPC` can run.
   - Updated `KCD2MP_W136Hold(true)` in `kdcmp.lua` to immediately unbind `KCD2MP.npcPuppets`, clear `parkedPeerHorses`, and gate `KCD2MP_M3HostTick` / `KCD2MP_W216Tick`.
2. **Three-layer `F11` / `F12` prompt reliability**:
   - Updated `Apply-DirectInstall.ps1` to preserve `kdcmp_keys.pak` / `kdcmp_brand.pak` and invoke `KcdMpClient.exe --keys-pak` during every DirectInstall update.
   - Added `<actionmap name="kcd2mp_prompt" priority="overlays" exclusivity="0">` (`kdcmp/ConfigPatch/defaultProfile.prompt.xml`, merged by `KeybindPak.MergePromptMap` without altering `KeybindPak.Actions.Length == 16`), dynamically enabled via `KCD2MP_SyncPromptMap()` only while a co-op prompt is visible.
   - Added foreground-only native `VK_F11`/`VK_F12` rising-edge detection in `native/KCDMP/wo140.cpp` (`kOpPrompt = 9`, `0xA5` `kFrameKey = 4` — note `3` is reserved as invalid by `Wo140Tests`) and quoted vote IDs as strings across Lua boundaries.

## Upstream Conflict Guide

- **Files**: `dotnet/KcdMp.Client/GameBridge.Wo136.cs`, `native/KCDMP/wo216.cpp`, `native/KCDMP/npc_drive.cpp`, `dotnet/KcdMp.Client/KeybindPak.cs`, `kdcmp/ConfigPatch/defaultProfile.prompt.xml`, `native/KCDMP/wo140.cpp`, `dotnet/KcdMp.Client/GameBridge.Wo140.cs`, `tools/Apply-DirectInstall.ps1`.
- **Invariants to preserve**:
  - Keep `Wo140Frame.KindKey = 4` (never `3`).
  - Keep `KeybindPak.Actions.Length == 16` (`PromptSuperactions` maps `kcd2mp_prompt_yes`/`no` onto the existing `kcd2mp_dice_bank`/`yield` superactions).
