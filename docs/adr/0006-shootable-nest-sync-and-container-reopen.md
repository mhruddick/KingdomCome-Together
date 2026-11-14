# 0006. Bidirectional `Nest` / `DestroStash` Knockdown Sync, Multi-Radius Nest Resolution & Container Re-Open

- **Status**: Accepted (PR #7 — Live Playtest Hotfix 2)

## Context

In live co-op testing around Semín (`nest140` / `nest141`), three coupled bugs appeared with bird nests (`Scripts/Entities/WH/Stash/Nest.lua` deriving from `ShootableStashBase.lua`) and shared containers:
1. **Unsynced nest knockdowns & duplicate fallen nests**: Nests start static in trees (`shot = 0`) and become falling rigid bodies (`EntityCommon.PhysicalizeRigid` in `AfterShot`) when struck by a stone or arrow. Knocking down a nest was local-only, so both players could knock down the same nest independently and land at different coordinates.
2. **False `"Move closer to your host to open this chest."` on fallen nests**: `KCD2MP_W217ResolveChestKey` only searched a `2.5 m` sphere around the `wc_N_...` coordinates. When a joiner knocked down `nest140` (`7.5 m` drop from `z = 84.80` to `z = 77.30`), the host's unshot `nest140` in the tree was outside `2.5 m`, returning `flags = 2` (`unresolved`).
3. **False `"The host didn't answer -- try again."` when re-opening a container within `8 m`**: `L.sess[key]` stays active until the joiner walks `> 8 m` away (`> 6 m` for corpses). Closing the UI and pressing `E` again while still standing nearby left `s.opened == true`, so `L.apply` skipped opening the UI **and** failed to clear `w.pendingOpen[key]`, triggering the 4-second open timeout even though the host answered with `w217_blist`. In addition, `L.markChestTouched` called `e.stash:ReportOpened()` with `nil` instead of `true`.

## Decision

1. **Append-only `LootAskNestShot` (`9`) & `LootHostNest` (`14`)**:
   - Hooked `AfterShot` and `Client.OnHit` on `ShootableStashBase`, `Nest`, and `DestroStash` (installed in both `Player.Client.OnInit` and `KCD2MP_W217ChestInstall`, since `Nest.lua` loads after `Scripts/Startup/kdcmp.lua`), capturing the untouched tree `wc_N_...` / `wc_D_...` key before physics moves the entity.
   - Knocking down a nest on either Host or Joiner immediately drops the partner's nest (`shot = 1` on Host so `.whs` saves persist the fallen state) and snaps the Joiner's nest to the Host's settled ground pose (`SetWorldPos`, `SetWorldAngles`, `AwakePhysics(0)`). Unstreamed Joiner nests cache the shot in `L.hostShotStashes` and apply on stream-in.
2. **Multi-radius + class-scan `Nest` resolution**:
   - Expanded `KCD2MP_W217ResolveChestKey` for `Nest` from `{ 2.5 }` to `{ 2.5, 15.0, 45.0 }` plus a `120 m` `System.GetEntitiesByClass("Nest")` fallback gated on exact 32-bit `hash8` name match (so nearby nests like `nest140` and `nest141` `1.8 m` apart never cross-match).
3. **Nearby container re-open reset & `ReportOpened(true)`**:
   - Reset `s.opened = false` in `KCD2MP_W217ChestOpenRequest` and `KCD2MP_W134LootRequest` whenever the UI is not already open on that key, unconditionally cleared `w.pendingOpen[name]` in `L.apply`, and passed boolean `true` to `e.stash:ReportOpened(true)`.

## Upstream Conflict Guide

- **Files**: `dotnet/KcdMp.Protocol/ProtocolWo217.cs` (`LootAskNestShot = 9`, `LootHostNest = 14`), `dotnet/KcdMp.Protocol/ProtocolWo134.cs`, `dotnet/KcdMp.Client/GameBridge.Wo217Loot.cs` (`Wo217LootRules.ParseNestShot`), `kdcmp/Data/Scripts/Startup/kdcmp.lua` (`W217.L`).
- **Invariants to preserve**:
  - `LootAsk` kinds (`1..9`) and `LootHost` kinds (`1..14`) are append-only; if upstream adds new `LootAsk`/`LootHost` kinds, renumber or reconcile before shipping so both peers agree on kind IDs.
