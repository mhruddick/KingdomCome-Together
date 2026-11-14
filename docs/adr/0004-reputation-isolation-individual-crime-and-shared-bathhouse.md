# 0004. Per-Player Reputation Splice, Selective Dialogue Isolation, Save-Paired Crime & Shared Bathhouse

- **Status**: Accepted (PR #5 — Milestone 4)

## Context

Upstream shared the host's faction reputation, renown, and personal NPC dispositions onto every joiner on join, broadcast all dialogue out-ports (so one player haggling, hiring a bathhouse maid, or insulting a villager mutated quest/reputation nodes for everyone), defaulted `mp_crime_mode` to `shared` without persisting individual joiner bounties across sessions/reloads, and treated bathhouse service time-skips (`C_SkipTime` `id = 7`) as unshared solo skips that desynchronized world clocks.

## Decision

1. **Per-player reputation `.hblk` v2 extension (`reputation`) + selective dialogue out-ports**:
   - Implemented `M4ReputationSaveExtension` (`WhsSave.M4Reputation.cs`, `Order = 20`, key `"reputation"`) splicing the joiner's own faction reputations, renown, and individual NPC opinion tables while keeping quest progression nodes (`ConceptState`) host-authoritative.
   - Isolated haggle, bathhouse, indulgence, and solo open-world chitchat dialogue out-ports (`GameBridge.Wo194.cs`, `Wo194Rules`) to the speaker while continuing to share quest dialogues and dialogues where a partner is actively listening in (`WO-203`/`WO-204`).
2. **Default `mp_crime_mode = individual` with save-paired `JoinerCrimeStore`**:
   - Changed default crime mode from `shared` to `individual` (`Wo139.cs`, `kdcmp.lua`).
   - Added `JoinerCrimeStore` paired by host `.whs` save sequence/hash so a joiner's active bounties/fines persist across disconnects and roll back accurately when the host reloads an earlier save.
3. **Shared bathhouse `C_SkipTime` (`id = 7`)**:
   - Extended `WO-140` (`wo140_rules.h`, `Wo140.cs`, `GameBridge.Wo140.cs`) to gate and vote on bathhouse time skips (`id = 7`, `"bath"`), running a bystander wait skip (`id = 1`, falling back to `id = 14` if in trespass/combat, or immediately sending `SleepCancel("woke")` if the host is in a cutscene/loading so the joiner never hangs on a black fader screen) and triggering the host schnapps save when the bathhouse service includes saving.

## Upstream Conflict Guide

- **Files**: `dotnet/KcdMp.Client/WhsSave.M4Reputation.cs`, `dotnet/KcdMp.Client/JoinerCrimeStore.cs`, `dotnet/KcdMp.Client/GameBridge.Wo139.cs`, `dotnet/KcdMp.Client/GameBridge.Wo140.cs`, `dotnet/KcdMp.Client/GameBridge.Wo194.cs`, `native/KCDMP/wo140_rules.h`.
- **Invariants to preserve**:
  - If the host cannot start a bystander skip (`Start` returns `0` for both `id = 1` and `id = 14`, or `Wo136Holding` is true), the host **must** immediately reply `SleepCancel("woke")` without arming `_w140SaveAfter`, otherwise `WO-192` holds the joiner behind a 15-second black fader.
