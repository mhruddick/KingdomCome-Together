# Milestone 1: Personal Bedside Stashes, Extensible `.hblk` v2, and Player Drop Duping Fixes

## Architecture Overview

Milestone 1 closes four persistence and item-transfer gaps in co-op shared-world sessions while preserving backward compatibility with existing `.hblk` v1 character snapshots and avoiding conflicts with parallel world-loot work:

1. **Extensible `.hblk` v2 Snapshot Container (`WhsSave.Wo125.cs`)**:
   - Extends `WhsSave.HenryParts` with `SortedDictionary<string, byte[]> Extensions` (ordinal-sorted keys).
   - Bumps serialized block format to `BlockVersion = 2`, appending `[i32 extCount]` followed by `{ [u16 keyLen][utf8 key][i32 valLen][bytes] }` after `KeyEntries` and before the 32-byte SHA-256 trailer.
   - `ParseBlock` accepts both `ver == 1` (leaving `Extensions` empty) and `ver == 2` (enforcing `extCount` in `0..256`, `valLen` in `0..64 MB`, unique keys, and exact stream consumption before the SHA-256 trailer).
   - `DiffBlocks` compares `Extensions` entries alongside `Record`, `Side`, and `KeyEntries`.

2. **Personal Bedside Master Stash (`01f4/01f8/7302/0006`) Isolation & Splicing (`WhsSave.cs`, `WhsSave.Wo125.cs`)**:
   - Identifies Henry's universal `playerMasterChest` record inside `01f4/01f8/7302/0006` by `(Lo: 0UL, Hi: 0x04BAAE5F204857B4UL)` via an extensible `PersonalStashIds` set.
   - **Extraction (`PartsFromStream`)**: Reads `01f4/01f8/7302/0006`, serializes matching `0x0000` personal stash TLV node(s) into `p.Extensions["personal_stash"]` (writing `Array.Empty<byte>()` when the joiner's personal stash is empty), and includes all `0x0001` item `instanceGuid`s from child `0x0005` (`C_Inventory`) in the joiner's item set when filtering `01f8/7302/000B` `KeyEntries`.
   - **Splicing (`SpliceStream` & `MergeKeys`)**:
     - Includes the host's personal-stash `0x0001` item `instanceGuid`s in `HenryItemSet(hostRaw)` so host dynamic key bindings stored in the host's stash are removed from the joiner's `000B`.
     - Rebuilds `01f4/01f8/7302/0006` keeping all non-personal `0x0000` stash records from the host byte-for-byte in exact order, **always** dropping the host's `PersonalStashIds` `0x0000` node(s) (even for legacy v1 `.hblk` or `OriginFreshDefault` so host stash items never leak/dupe into a joiner's chest), and appending the joiner's `personal_stash` node(s) with `IsQuestItem="true"` (`qclasses`) items stripped from child `0x0005` (omitting the `0x0000` node entirely if 0 items remain in `0x0005`, matching KCD2's native empty-stash representation).
     - Records `PersonalStashItemsJoiner` and `PersonalStashQuestItemsRemoved` on `SpliceReport`.
   - **Verification (`CheckParts`)**: Marks `"01f4/01f8/7302/0006"` as spliced and verifies non-personal host stash records byte-for-byte in order, personal stash contents against `parts.Extensions` minus `qclasses`, and `000B` key bindings with personal-stash item sets included.

3. **Fast-LAN Joiner `PostLoadAsync` Chest Ledger Race Fix (`GameBridge.Wo124.cs`, `GameBridge.Wo134.cs`)**:
   - Calls `Wo134AfterReady(wt0)` **before** `await SendJoinerReadyAsync("wo124")` in `PostLoadAsync` so sub-2ms LAN `LootHostLedger` responses from the host cannot arrive first and be wiped by `_w134RxLedger = null`.
   - Guards `Wo134AfterReady` under `_w134Gate` so a valid same-epoch `_w134RxLedger` is preserved if already received.

4. **Player Ground-Drop `< 750 ms` Immediate Pickup Dupe Race Fix (`kdcmp/Data/Scripts/Startup/kdcmp.lua`)**:
   - Snapshots local player inventory count/wuids for `d.cls` at `mp_item_spawn`, runs an immediate `mp_item_finalize` pass right after `PlaceItem`, and schedules a `60 ms` follow-up timer (`Script.SetTimer(60, ...)`).
   - Teaches `W134.isDrop` and `mp_item_detect` (via a `WO-48` helper) to recognize an unbound `d.state == "placing"` `PickableItem` matching `d.cls` within `3.0 m` of `(d.x, d.y, d.z)` (excluding `preIds` and other drops), binding it immediately and scheduling fast pickup claim detection so `item_claim` fires without hitting `W134` world-item fallback.
   - In `mp_item_finalize`, if the ground entity is absent on an async check while the local player's inventory count of `d.cls` increased since `mp_item_spawn`, transitions `d` to `claimed_local` and emits `item_claim` instead of letting it time out as `FAILED: placed entity never appeared`.

5. **Unpaired Joiner Disconnect Drop-Dupe Protection (`HenryStore.cs`, `WhsSave.Wo125.cs`, `GameBridge.cs`)**:
   - Persists a lightweight, zero-hitch transferred-out item ledger (`transfers.json`) inside `HenryStore` (`WorldDir(tag)`) whenever `ItemClaimDown` confirms a peer (`icClaimer != _myGhostId`) claimed one of the joiner's own open drops (`_myOpenDrops.TryRemove`).
   - When `HenryStore.PickFor` restores a snapshot whose `TakenUtc` precedes recorded peer-claimed transfers (`ClaimedUtc > snap.TakenUtc`), applies `WhsSave.ApplyTransferredOut` to deduct those transferred items (preferring unequipped stacks, updating stack amounts or removing `0x0001` nodes, cleaning equipment preset references and `KeyEntries`, and extended to `personal_stash` if needed) so disconnecting/crashing before the next host save never restores transferred items to the joiner.

6. **Build/Release Script `.NET` SDK Discovery Fix (`tools/Publish-Release.ps1`, `tools/Build-Installer.ps1`)**:
   - Updates both scripts to fall back to `$env:USERPROFILE\.dotnet-sdk8` only when `-not $env:DOTNET_ROOT -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)`, allowing standard `C:\Program Files\dotnet` installations to work out of the box.

---

## Implementation Checklist

- [x] **Task 1**: Implement extensible `.hblk` v2 format (`Extensions` dictionary on `WhsSave.HenryParts`, `BlockVersion = 2` writer, dual v1/v2 `ParseBlock` reader with strict bounds/duplicate/hash validation, `DiffBlocks` support) and unit tests in `KcdMp.Client.Tests`.
- [ ] **Task 2**: Implement personal master stash (`01f4/01f8/7302/0006`) extraction, host-stash stripping, quest-item filtering, `000B` key-set inclusion, and `CheckParts` verification in `WhsSave.cs` & `WhsSave.Wo125.cs`, with synthetic and conditional real-save tests (`save206.whs`, `quicksave022.whs`, `save318.whs`).
- [ ] **Task 3**: Fix the fast-LAN joiner `PostLoadAsync` chest ledger race in `GameBridge.Wo124.cs` and `GameBridge.Wo134.cs`, with unit tests verifying ordering and concurrent ledger preservation.
- [ ] **Task 4**: Fix the `< 750 ms` player ground-drop immediate pickup dupe race in `kdcmp/Data/Scripts/Startup/kdcmp.lua` (`WO-48` spawn/finalize/detect + `W134.isDrop`), verifying no UTF-8 BOM and passing synthetic Lua + client tests.
- [ ] **Task 5**: Implement zero-hitch joiner transferred-out drop ledger in `HenryStore.cs`, `WhsSave.Wo125.cs`, and `GameBridge.cs` (`ItemClaimDown`) to prevent unpaired disconnect duping, with comprehensive unit tests.
- [ ] **Task 6**: Fix `.NET` SDK lookup fallback in `tools/Publish-Release.ps1` and `tools/Build-Installer.ps1` when `dotnet` is already on `PATH`, with unit/script verification tests.
