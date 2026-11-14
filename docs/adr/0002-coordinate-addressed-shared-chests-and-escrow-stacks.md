# 0002. Coordinate-Addressed Shared World Containers, Isolated Stack Escrows & Item Tombstones

- **Status**: Accepted (PR #3 — Milestone 2)

## Context

Upstream treated world chests (`Stash`, `CartStash`, `Nest`, `DestroStash`) as separate per-player inventories reconciled via join-time delta ledgers (`ChestLedgerStore` / `KCD2MP_W134ChestApply`), while live item sync (`WO-217 C`) only covered dead NPC bodies addressed by unique entity names. World chests frequently share non-unique names across the map (e.g., dozens of `Stash19` entities) or contain brackets/slashes/colons rejected by `Wo134Rules.BodyName` (`^[A-Za-z0-9_]{1,80}$`). Furthermore, `WO-217 C` only handled whole-item takes, could merge overlapping partial arrow/coin takes inside a single shared escrow stash, and lost loose world-item pickups when a joiner wasn't streamed near the item or when physics-settled bows/shields drifted `> 0.35 m` from their spawn point.

## Decision

1. **Coordinate + class + dual-prime hash container keys (`wc_<tag>_...`)**:
   - Retired per-player chest delta replay; non-personal world containers (`Stash`, `CartStash`, `Nest`, `DestroStash`) are live host-authoritative containers routed through `WO-217 C` and persisted in the host's `.whs` save.
   - Personal bedside stashes are excluded via three checks (`playerMasterChest` name, valid `stash:GetMasterInventory()` WUID, or `GetInventoryToOpen() != inventory:GetId()`).
   - Addressed world containers on the wire as `wc_<S|C|N|D>_<encX>_<encY>_<encZ>_<hash8>_<stem16>` (`<= 80` ASCII `[A-Za-z0-9_]` chars, compatible with `Wo134Rules.BodyName` without changing relay packet schemas) and resolved via sphere + `hash8` lookup inside wrapped `System.GetEntityByName`.
   - Added lazy host loot generation (`StashInventoryCollector` / `AssignInventory` + `stash:ReportOpened(true)`) when a joiner opens an untouched chest first, and `flags = 2` refusal (`"Move closer to your host..."`) when a chest is unstreamed on the host so an unloaded host chest is never mistaken for empty.
2. **Isolated 64-slot escrow pool (`kcd2mp_w217_esc_1..64`) + scratch stash (`kcd2mp_w217_scratch`)**:
   - Added partial stack takes, stack merges, and live escrow-backed deposits (`puts`) even while the host's own loot UI is open (never calling raw `CreateItem`/`DeleteItem` directly on an open UI inventory, which crashes CryEngine on screen close).
   - Allocated one isolated hidden escrow stash per in-flight stack transfer and serialized same-class requests so CryEngine's automatic stack-merging on `inventory:AddItem` can never combine two concurrent takes or puts.
3. **Persistent loose world-item tombstones & `5.0 m` weapon drift**:
   - Added `WorldItemTombstoneStore` paired with host saves plus joiner unstreamed tombstone caching (`goneTombstones`), keeping a tight `0.35 m` match first (protecting dense table items/potions/books) with a `5.0 m` fallback restricted strictly to weapons/shields/corpse drops (`W134.canDrift`).

## Upstream Conflict Guide

- **Files**: `dotnet/KcdMp.Client/GameBridge.Wo217Loot.cs`, `dotnet/KcdMp.Client/GameBridge.Wo134.cs`, `dotnet/KcdMp.Client/WorldItemTombstoneStore.cs`, `kdcmp/Data/Scripts/Startup/kdcmp.lua` (`W134` and `W217.L`).
- **Invariants to preserve**:
  - Never mutate an open body/chest inventory via `CreateItem`/`DeleteItem` in place; always stage in `kcd2mp_w217_scratch` and transfer via `inventory:AddItem`.
  - Unstreamed host chests must return `w134_bstate ... open 2` (`flags = 2`), never an empty `w217_blist`.
