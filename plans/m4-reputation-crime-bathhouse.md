# Milestone 4: Independent & Shared Reputation, Individual Crime Persistence, and Shared Bathhouse Time-Skip

## Architecture Summary

Milestone 4 gives each co-op player independent personal reputation, renown, vendor haggling, and crime accountability that persist across sessions and pair cleanly with Host `.whs` saves, while sharing reputation from story quests and listened-in conversations and synchronizing bathhouse time-skips across the party:

1. **Independent Reputation & Renown Persistence (`WhsSave.M4Reputation.cs`, `ISaveExtension` `Order => 20`)**:
   - **Save Stream Layout**: KCD2 persists reputation across three TLV locations:
     1. `01f4/01f8/7308/352C` (`FactionTree`): ~1,059 child TLV nodes storing settlement, town, social-class, and merchant-group faction reputations toward `player`.
     2. `0x12FF` on each non-Henry `0x115E` NPC soul in `01f4/01f8/7308/3529/1161` (`SoulList`): per-NPC personal opinion toward `player` (`0x1D2C` and optional `0x1D2B` child TLV nodes).
     3. `0x12FF` on `player_henry` (`4c2dcffb-dea1-6263-72d7-b39f4db2d8b5`): Henry's personal renown (`0x1D2C` child TLV node).
   - **Modular `.hblk` v2 Extension**: Implemented in `dotnet/KcdMp.Client/WhsSave.M4Reputation.cs` via `WhsSave.ISaveExtension` (`Order => 20`, registered via `[ModuleInitializer]`) with zero edits to `WhsSave.cs` or `WhsSave.Wo125.cs`:
     - `"reputation_factions"`: raw `352C` payload extracted when `Children(pay, 0, pay.Length)` parses as a non-empty TLV list.
     - `"reputation_henry_renown"`: Henry's `0x12FF` payload extracted when `Children` parses with first tag `0x1D2C`.
     - `"reputation_npc_opinions"`: binary table `[i32 count] { [16B soulGuid][i32 len][0x12FF payload] }*` for non-Henry `0x115E` souls whose `0x12FF` parses with first tag `0x1D2C`.
     - Legacy synthetic fixtures (`WhsSaveTests`, `Wo125Tests`) use raw ASCII `"joiner-world"` for `352C` and non-TLV bytes for `0x12FF`, so they remain 100% untouched.
   - **Cross-Playthrough Import Guard**: When a Joiner imports an external character from a different playthrough (`parts.Seed is uint js && ReadSeed(hostRaw) is uint hs && js != hs`), `SpliceStream` keeps Host's `352C` and NPC `0x12FF` so a fresh character never overwrites an established campaign's world faction states. Once snapshotted in Host's world (`parts.Seed == hs`), all three reputation layers belong to Joiner on every save, reload, and rejoin. `TransformSoulRecord` modifies only `0x12FF` on matching `0x115E` souls, leaving `0x12F9`, `0x12FB`, `0x1301` (including horse inventories), and short `0x115D` records untouched.

2. **Runtime Conversation & Haggle Isolation + Shared Listened-In / Quest Reputation (`Wo200.cs`, `GameBridge.Wo200.cs`, `GameBridge.Wo203Cam.cs`)**:
   - **Zero Native `wo200_rules.h` Overlap**: Host `WO-137` already filters `QuestHostChange` to `C_Quest` nodes (`questLen > 0`), so Host's solo `Barbora.open_world.*` dialogues never mirror to Joiner, while `C_Quest` story/side-quest `State` transitions mirror to Joiner and apply shared quest reputation on both machines.
   - **Joiner -> Host `OutPortReplay` Filtering**: Pure rules in `Wo200Rules` wired into `Wo200OnCaptureAsync` classify Joiner dialogue out-ports:
     1. **Always local-only**: shop/bribe haggling (`smlouvani_v_obchode`, `bribeHaggleNegotiation`, `bribe_haggle_negotiation`, `add_haggle_denial`), bathhouse service dialogues (`lazne_a_lazebnice`), and guard arrest/punishment dialogues (`nextnextgenpunishment`). Prevents failed Joiner haggles from applying `add_haggle_denial` buffs to Host's merchants or bathhouse orders from firing behaviors on Host.
     2. **Open-world dialogues (`Barbora.open_world.*`)**: local-only when solo (`!hasListeners`), replayed to Host when a partner is listening in (`_w3cTalk` has active listeners or ended within 3 s with listeners attached) so co-op listened-in conversations share reputation outcomes.
     3. **Story/side-quest dialogues (`Barbora.trosecko.*`, `Barbora.kutnohorsko.*`, `Barbora.klaster.*`)**: continue replaying to Host so quest steps and quest reputation apply to both players.

3. **Individual Crime Mode Default & Save-Paired `JoinerCrimeStore` (`GameBridge.Wo151.cs`, `JoinerCrimeStore.cs`, `GameBridge.Wo139.cs`, `GameBridge.Wo144.cs`, `GameBridge.Wo125.cs`, `kdcmp.lua`)**:
   - **Default `mp_crime_mode` to `individual`**: `_w151CrimeJoint` in `GameBridge.Wo151.cs` and `KCD2MP.w151.crimeJoint` in `kdcmp.lua` default to `false` (`individual`), while keeping `mp_crime_mode joint|individual` runtime switching intact.
   - **Save-Paired Crime Persistence (`JoinerCrimeStore`)**: Stores open `JoinerCrimeRecord.Crime` entries per normalized player name under `%LocalAppData%\KCDMP\crimes\<worldTag>\` (`latest.json` + `pair-<hostSaveMd5>.json`, bounded to 32 save snapshots). Monotonic `Environment.TickCount64` timers are serialized as remaining durations (`RemainingReportDelayMs`, `RemainingFreshMs`, `RemainingEscalationMs`) alongside `Known`, `Calmed`, `Resisted`, and `AtWorldS` (tracked against Host's `_lastPolledWorldTime`) so restored crimes across process restarts preserve known/calmed/arrestable state and multi-day expiration accurately.
   - **Host Save, Reload & Reconnect Hooks**: Paired on Host save (`Wo125HostIdentify` -> `Wo139OnHostWorld`), flushed on crime mutations and peer disconnects, restored on `Wo144OnPeerConnected` across sessions, and rolled back on Host save reload (canceling stale pursuits/stops and sending `CrimeHostCleared` / `CrimeHostRecord` to connected joiners).

4. **Shared Bathhouse Services Time-Skip (`native/KCDMP/wo140_rules.h`, `ProtocolWo140.cs`, `Wo140.cs`, `GameBridge.Wo140.cs`)**:
   - Bathhouse services (`Clean` = 1 h, `CureClean` = 2 h, `Love` / `Love_censored` = 3 h) run `C_SkipTime` `id == 7` (`"bath"`, forced skip without `ShowDialog`).
   - `native/KCDMP/wo140_rules.h` names `case 7: return "bath";` while keeping `gated_id` as `1, 2, 4`.
   - `Wo140Text` accepts `"bath"`, `Wo140Rules.KindOfSkipId(7)` returns `"bath"`, and `Wo140Rules.AccepterSkipId("bath")` returns `1` (`wait`, falling back to `14` forced skip in `Wo140StartOwnAsync`) so bystanders advance smoothly for the exact hours without playing bathhouse audio.
   - When `Wo140OnLocalBeganAsync` sees `EdgeBegan` with `f.Id == 7` in a partnered shared session with no active bed/wait vote, it marks a shared `"bath"` skip and sends `Protocol.SleepBegin` (`save = !_isDamageAuthority`). `Wo140OnBeginAsync` accepts `"bath"` without requiring a prior `SleepAsk` vote, relays to other joiners on Host, shows `"<Name> is using the bathhouse (Xh)..."`, starts the bystander skip screen, and sets `_w140SaveAfter = host && save` so Joiner bathhouse visits trigger `KCD2MP_W140RestSave` on Host at skip end.

## Branch Isolation Guardrails

- Work strictly inside `d:\Code\kcd2\worktrees\m4-reputation-crime-bathhouse` (`feat/m4-reputation-crime-bathhouse`).
- Do **NOT** modify `WhsSave.cs`, `WhsSave.Wo125.cs`, `GameBridge.Wo137.cs`, `GameBridge.Wo154Ride.cs`, `GameBridge.Wo160.cs`, `Wo160.cs`, `ProtocolWo137.cs`, `native/KCDMP/wo200_rules.h`, `native/KCDMP/wo200.cpp`, or `kdcmp/Data/kdcmp.pak`.
- In `kdcmp/Data/Scripts/Startup/kdcmp.lua`, stay strictly within sleep/crime/crimeJoint ranges, add zero top-level `local` variables, and never write a UTF-8 BOM.

## Implementation Checklist

- [x] **Task 1: Independent Reputation & Renown Save/Block Persistence (`WhsSave.M4Reputation.cs`)** — Implement `ISaveExtension` (`Order => 20`) via `[ModuleInitializer]` extracting and splicing `reputation_factions` (`352C`), `reputation_henry_renown` (Henry `0x12FF`), and `reputation_npc_opinions` (NPC `0x12FF`) with TLV validation, cross-playthrough seed guard, and synthetic + real `.whs` backup tests.
- [x] **Task 2: Runtime Dialogue & Haggle Isolation + Shared Listened-In / Quest Reputation (`Wo200.cs`, `GameBridge.Wo200.cs`, `GameBridge.Wo203Cam.cs`)** — Add pure `Wo200Rules` classification and `_w3cTalk` active/recent-end listener tracking in `Wo200OnCaptureAsync` to keep haggle, bathhouse, punishment, and solo `Barbora.open_world.*` out-ports local while replaying listened-in `open_world` and story/quest dialogues, verified with unit and bridge tests.
- [ ] **Task 3: Default `mp_crime_mode` to `individual` (`GameBridge.Wo151.cs`, `kdcmp.lua`)** — Change default crime mode from `joint` to `individual` in C# and Lua while keeping `mp_crime_mode joint|individual` runtime switching intact, and update Lua synthetic + C# unit tests.
- [ ] **Task 4: Save-Paired Per-World `JoinerCrimeStore` & Host Reload Rollback (`JoinerCrimeStore.cs`, `GameBridge.Wo139.cs`, `GameBridge.Wo144.cs`, `GameBridge.Wo125.cs`, `GameBridge.Wo151.cs`)** — Persist per-player open crimes with relative timer restoration and `AtWorldS` expiration under `%LocalAppData%\KCDMP\crimes\<worldTag>\`, pair/rollback with Host saves and reloads, restore across sessions on reconnect, and verify with unit/integration tests.
- [ ] **Task 5: Shared Bathhouse Services Time-Skip & Full Verification (`native/KCDMP/wo140_rules.h`, `ProtocolWo140.cs`, `Wo140.cs`, `GameBridge.Wo140.cs`)** — Support `C_SkipTime` `id == 7` (`"bath"`) in native rules and C# `WO-140`, broadcast un-voted `SleepBegin("bath")` with bystander wait/forced-skip screens, toast notification, and Host `RestSave` on Joiner bathhouse completion, and pass all C#, native, relay, Lua synthetic, and branch-isolation gates.
