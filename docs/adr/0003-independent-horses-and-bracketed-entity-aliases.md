# 0003. Independent Horse Ownership, Saddlebag Splice, Quest Horse Gate & Bracketed `lh` Aliases

- **Status**: Accepted (PR #4 — Milestone 3)

## Context

Upstream mirrored quest horse grants (`SetPlayerHorse`, e.g., Pebbles / Herring) onto every connected player (`wo137.cpp`), overwrote the joiner's horse companion and saddlebag inventory with the host's on every join, failed to relay dynamically spawned horse entities whose names contain brackets/slashes/GUIDs (`horse[animalcare/horseCare6_<guid>]`), and could not summon a player's owned horse across regions or when unstreamed on the host.

## Decision

1. **Per-player horse & saddlebag `.hblk` v2 extension (`horse`)**:
   - Implemented `M3HorseSaveExtension` (`WhsSave.M3Horse.cs`, `Order = 10`, key `"horse"`) splicing the player's owned horse companion link, horse soul/stats, saddlebag inventory, and equipped horse tack independently of the host's horse, while stripping host-save temporary tack modifications (`M3BeforeHostSave` / `M3AfterHostSave`) so the host's `.whs` stays clean.
2. **Independent quest horse grant gate (`mp_quest_horse_sync`, default `off`)**:
   - Gated `SetPlayerHorse` quest mirroring in `native/KCDMP/wo137.cpp` and `GameBridge.Wo137.cs` behind `mp_quest_horse_sync` (default `off`) so each player acquires, buys, or tames their own mount independently unless shared quest horses are explicitly enabled.
3. **Bracketed horse `lh<8hex>` aliases & cross-region far whistle**:
   - Relaxed C++ (`native/KCDMP/wo217_rules.h`) and C# (`Wo160Rules` / `GameBridge.Wo160.cs`) horse name validation to accept bracketed prefab names via deterministic `lh<8hex>` aliases resolved by `System.GetEntityByName`.
   - Added cross-region / unstreamed far-whistle stand-in spawning (`KCD2MP_M3FarWhistle`), parking (`parkedPeerHorses`), legality exemption (`WO-139` so riding a partner's or own horse is never flagged as horse theft), and live saddle/bridle/caparison tack sync.

## Upstream Conflict Guide

- **Files**: `dotnet/KcdMp.Client/WhsSave.M3Horse.cs`, `dotnet/KcdMp.Client/GameBridge.Wo160.cs`, `dotnet/KcdMp.Client/GameBridge.Wo137.cs`, `native/KCDMP/wo137.cpp`, `native/KCDMP/wo217_rules.h`, `kdcmp/Data/Scripts/Startup/kdcmp.lua` (`M3` block around `WO-160`).
- **Invariants to preserve**:
  - `M3HorseSaveExtension` (`Order = 10`) runs before reputation (`Order = 20`) and must keep stripping quest items from joiner saddlebags on cross-world splice.
  - `mp_quest_horse_sync` defaults to `false` (`off`).
