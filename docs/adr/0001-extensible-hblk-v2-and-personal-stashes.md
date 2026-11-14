# 0001. Extensible `.hblk` v2, `ISaveExtension`, Personal Bedside Stashes & Drop Transfer Ledger

- **Status**: Accepted (PR #2 — Milestone 1)

## Context

Upstream's joiner character persistence (`WhsSave` / `HenryStore`, `.hblk` v1) spliced only Henry's inventory, equipped slots, stats/perks, keyring, and money out of the host's `.whs` save. It did **not** splice the player's personal bedside master chest (`playerMasterChest`, `01f4/01f8/7302/0006`, ID `0x04BAAE5F204857B4`), meaning a joiner saw the host's bedside chest on join and lost anything stored in their own bedside chest on leave. In addition, `.hblk` v1 had a fixed binary layout that could not carry new per-player subsystems without breaking existing snapshots, and item drops between snapshots could duplicate if a joiner disconnected after a peer claimed their dropped item or picked up a drop during its `< 750 ms` physics-placing window.

## Decision

1. **`.hblk` v2 TLV extension map & `ISaveExtension` seam**:
   - Upgraded `HenryParts` serialization (`WhsSave.Wo125.cs`) to `BlockVersion = 2`, appending a string-keyed `Dictionary<string, byte[]> Extensions` table while preserving full backward-compatible reads of v1 `.hblk` snapshots.
   - Added `WhsSave.ISaveExtension` (`Order`, `Key`, `Extract`, `Apply`) registered via `[ModuleInitializer]` so subsequent per-player save subsystems (horses, reputation) plug in without modifying the core `.whs` TLV splicer.
2. **Personal Bedside Master Stash (`personal_stash`)**:
   - Extracted and spliced only the `playerMasterChest` node (`WhsSave.PersonalStashIds`) under extension key `"personal_stash"`, leaving all other world stashes (`hs.WorldStashes`) owned by the host's `.whs` save.
   - Stripped quest items from a joiner's personal stash on cross-world splice (same rule as inventory quest items) so host quest triggers cannot break, while preserving bound keys that belong to items inside the joiner's personal stash.
3. **Anti-dupe item drop boundaries (`ItemTransferLedgerStore`)**:
   - Recorded peer-claimed joiner drops (`item_claim`) persistently per host world tag, deducted transferred items (cleaning equipped references, bound keys, and withdrawn personal-stash items) across snapshot replay, and gated `< 750 ms` placing entities by exact `createdWuid` and inventory-removal / game-capture timestamps.

## Upstream Conflict Guide

- **Files**: `dotnet/KcdMp.Client/WhsSave.cs`, `dotnet/KcdMp.Client/WhsSave.Wo125.cs`, `dotnet/KcdMp.Client/HenryStore.cs`, `dotnet/KcdMp.Client/ItemTransferLedgerStore.cs`, `kdcmp/Data/Scripts/Startup/kdcmp.lua` (`ITEM-SYNC`).
- **Invariants to preserve**:
  - Never overwrite non-personal `WorldStashes` from a joiner's `.hblk`; only `PersonalStashIds` (`0x04BAAE5F204857B4`) belongs to the individual player.
  - Keep v1 `.hblk` read support intact in `WhsSave.Wo125.cs`.
