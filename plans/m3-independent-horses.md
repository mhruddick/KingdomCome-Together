# Milestone 3: Independent Horse Ownership, Saddlebag/Tack Persistence, Host Parking & Tack Sync, Far Whistle, and Quest Horse Gating

## Architecture Overview

Milestone 3 gives every co-op player independent horse ownership, persistent personal saddlebag & tack inventories across `.whs` saves and `.hblk` snapshots, stationary dismount parking and visual tack sync on the Host without polluting Host saves, reliable cross-region whistling, and quest/trader horse-grant isolation:

1. **Per-Player Horse Companion & Saddlebag/Tack Save Splicer (`dotnet/KcdMp.Client/WhsSave.M3Horse.cs`)**:
   - Registers `M3HorseSaveExtension` (`WhsSave.ISaveExtension`, `Order => 10`) via `[ModuleInitializer]` without modifying `WhsSave.cs` or `WhsSave.Wo125.cs`.
   - Parses `01f4/01f8/7308/3529/1160` (`CompanionBlock`), supporting real empty payloads (`Len == 0`), `WhsSaveTests`'s synthetic 4-byte `00 00 00 00` placeholder (left untouched when no `0x0DD6` horse link exists so all existing save tests stay byte-identical), and real `0x0DD6` 36-byte TLV companion links (`[u16 0x0501][16B masterSoulGuid][u16 0x0501][16B companionSoulGuid]`).
   - Distinguishes horse souls in `01f4/01f8/7308/3529/1161` from Mutt (`tvez_vorech`, shared GUID `44a28861-719d-9fed-e3dd-b20c479e8781`), wild/town dogs, and wolves, and preserves Mutt plus all NPC companion links (`master != HenrySoul`).
   - Stores `"horse_companion"` in `HenryParts.Extensions`: `[0]` when Joiner owns no horse in a real save, or `[1][16B companionSoulGuid][i32 soulNodeLen][full 0x115E horse soul node]` when Joiner owns a horse.
   - Includes Joiner's horse `0x1301/0x0007` item `instanceGuid`s in `henryOwnedItemIds` and Host's owned horse item `instanceGuid`s in `hostPrivateItemIds` so dynamic `0x05AD` keys stored in saddlebags round-trip cleanly in `01f4/01f8/7302/000B`.
   - Rebuilds `1160` for Joiner's spliced save (removing Host's Henry->Horse link when Joiner owns no horse or a different horse, and appending Joiner's Henry->Horse link when present in `hostRaw`) and splices Joiner's horse `0x1301` (with `qclasses` quest items stripped from `0x0007` and cleaned from `0x0001`/`0x0006` equipped slots) and `0x12FB` onto `currentSoulNodeBytes` while preserving Host's `0x12F9` entity binding and `0x12FF` reputation field for clean composition with Milestone 4 (`Order => 20`).

2. **Quest Horse Assignment Gating & Extended Native Entity Names (`native/KCDMP/wo200_rules.h`, `wo200.cpp`, `wo139_rules.h`, `wo139.cpp`, `wo216.cpp`)**:
   - Adds `Method::PlayerHorse` (`ReplacePlayerHorse`, `SetPlayerHorse`, `ClearPlayerHorse`) and `"Kill"` under `Method::Damage` in `wo200_rules.h`, plus `is_horse_dispose_parent` (`replaceplayerhorse` / `disposeold`) in `wo200_rules.h` & `wo200.cpp` so quest/trader horse grants and old-horse disposal (`replaceplayerhorse_disposeoldifexists`) route to `Verdict::TalkerOnlyHost` during Host `OutPortReplay` closures and `Verdict::TalkerOnlyJoiner` / `Verdict::PuppetRefused` on Joiners.
   - Adds `wo139rules::is_entity_name` (`1..120` chars, `[A-Za-z0-9_\[\]/\-:]`) for `wo139` `kOpContext` and raises `wo216` `kOpOutfitOf` / `kOpExempt` name buffers to `120` chars so bracketed Trosky horse entity names (`horse[animalcare/horseCare6_<guid>]`) work with `crime_ignoredHorseTheft_Horse` and live soul equipment reads.

3. **Bracketed Horse Names, Agent Horse Protocol & Live Tack Polling (`M3HorseRules.cs`, `GameBridge.M3Horse.cs`, `Protocol.cs`, `CombatPipe.cs`, `GameBridge.Wo137.cs`, `GameBridge.Wo178.cs`)**:
   - Raises `Protocol.MaxHorseNameLen` to `120` and validates horse entity names (`1..120` chars, `[A-Za-z0-9_][A-Za-z0-9_\[\]/\-:]*`, rejecting quotes, spaces, control chars, and `kcd2mp_` prefixes) across `HorseInfoUp`/`HorseInfoDown`, `w160_ownhorse`/`QuestAskOwnHorse`, `w136_ride`, `w154_ride`, and `Wo139ContextAsync`.
   - Extends `QuestAskOwnHorse` text to carry `"<horse>"`, `"<horse> <tackClassCsv>"`, or `"-"`, polling Joiner's live horse equipped item classes via `Wo216OpOutfitOf` (with `.hblk` `horse_companion` `0x1301` fallback), setting/clearing `crime_ignoredHorseTheft_Horse` on Host for Joiner-owned world horses, stripping/restoring temporary peer tack around Host saves, and releasing peer horses on disconnect.

4. **Host Horse Dismount Parking, Legality Exclusion, Same-Soul Collision Proxy & Visual Tack Sync (`kdcmp/Data/Scripts/Startup/kdcmp.lua`)**:
   - Excludes Host's personal owned horse (`player.player:GetHorseId()`) and peer-owned horses from `KCD2MP_W139HostHorses()` so personal horses are not broadcast as free-for-all mounts.
   - Keeps Joiner-owned world horses (`name ~= hostOwnHorse`) paused (`wh_ai_PauseNPC`) at Joiner's dismount spot on Host instead of resuming trader schedules that walk them back to Semine/Nomad Camp, marks `SetMountIsLegal(true)`, and restores original state when switched or disconnected.
   - Protects Host's own horse on same-soul collisions (`Joiner horse == Host horse`, e.g., both own `tsem_sedivka`) by spawning/maintaining a dedicated `NO_SAVE` `kcd2mp_peerhorse_<peer>` proxy for Joiner's avatar.
   - Applies Joiner's equipped tack classes (`CreateItem` + `EquipInventoryItem`) onto Joiner's world horse or `NO_SAVE` proxy on Host while tracking every mod-created handle and stripping/restoring around Host saves so Host `.whs` saves are never polluted.

5. **Far Whistle Across Regions & Unloaded Layers (`kdcmp/Data/Scripts/Startup/kdcmp.lua`, `GameBridge.M3Horse.cs`)**:
   - Releases Joiner's own horse from Host's NPC stream on Joiner (`w136.ridden` for 15 s + `w136_ride <horse> 1`) when Joiner whistles (`call` action) so native `CallHorse` materializes/gallops `Horse B` to Joiner unimpeded.
   - On Host (`KCD2MP_W160Whistle`), queues `w.pendingFetch[src]` when Joiner's avatar is `> 150 m` from Host (`WO-157` physics range guard) for automatic placement once in range, and spawns/moves `NO_SAVE` stand-in `kcd2mp_peerhorse_<src>` 7 m beside Joiner's avatar when `<= 150 m` and `Horse B`'s level layer is unstreamed or collided with Host's horse.

---

## Implementation Checklist

- [x] **Task 1**: Implement `WhsSave.M3Horse.cs` (`ISaveExtension`, `Order => 10`, `[ModuleInitializer]`) for `1160` companion list & `1161` horse soul `0x1301`/`0x12FB` splicing, `000B` saddlebag key preservation, Mutt/NPC companion safety, and quest-item stripping, with synthetic and conditional real-save tests in `M3HorseSaveTests.cs`.
- [ ] **Task 2**: Implement quest horse assignment & old-horse disposal gating (`Method::PlayerHorse`, `"Kill"`, `is_horse_dispose_parent`) in `native/KCDMP/wo200_rules.h` & `wo200.cpp` and `1..120` bracketed entity name support in `wo139_rules.h`, `wo139.cpp`, & `wo216.cpp`, rebuild native binaries (`Build-Native.ps1`), and add unit tests in `wo200_tests.cpp` & `wo139_rules_tests.cpp`.
- [ ] **Task 3**: Implement `M3HorseRules.cs` and `GameBridge.M3Horse.cs` (bracketed horse name validation, `MaxHorseNameLen = 120`, `QuestAskOwnHorse` `<horse> [tackCsv]|-` protocol, `.hblk` + live `Wo216OpOutfitOf` tack sync, Host `crime_ignoredHorseTheft_Horse` toggling, pre/post-save tack hook, and disconnect cleanup), with unit tests in `M3HorseRulesTests.cs`.
- [ ] **Task 4**: Implement Lua independent horse ownership, `KCD2MP_W139HostHorses` personal horse exclusion, Host dismount parking, same-soul `NO_SAVE` proxy fallback, and save-safe visual tack equipping in `kdcmp/Data/Scripts/Startup/kdcmp.lua` (strictly within horse line ranges, zero new main-chunk locals, no BOM), with synthetic tests in `Test-WO139Synthetic.lua` and `Test-WO160Synthetic.lua`.
- [ ] **Task 5**: Implement Joiner whistle stream hold (`15 s`) and Host far/unloaded-layer whistle support (`pendingFetch` beyond `150 m` + `NO_SAVE` `kcd2mp_peerhorse_<src>` stand-in) in `kdcmp/Data/Scripts/Startup/kdcmp.lua`, with synthetic tests in `Test-WO160Synthetic.lua` and full validation across all `.NET`, native, synthetic, and static gates.
