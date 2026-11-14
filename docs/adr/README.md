# Architecture Decision Records (Fork Divergences)

This directory records the architectural decisions, protocol extensions, save-format contracts, and engine-hook invariants introduced on our `KingdomCome-Together` fork (`mhruddick/KingdomCome-Together`), kept intentionally separate from upstream's `docs/WO-*` work-order archive.

## Purpose & Discipline

1. **No `WO-*` numbering**: We do not add new `docs/WO-*.md` work orders. Implementation checklists live in [../../plans/README.md](../../plans/README.md), and durable design decisions live here as sequential ADRs (`NNNN-slug.md`).
2. **Upstream conflict resolution**: Every ADR lists the exact `.hblk` extension keys, wire kinds, pipe opcodes, and file seams touched, plus the non-obvious invariants that must survive any future rebase or merge from `DeepFriedDepp/KingdomCome-Together`.
3. **When to add or update an ADR**: Record an ADR whenever a change introduces or alters a save/wire/pipe contract, changes authority or persistence boundaries between Host and Joiner, works around a CryEngine / Warhorse engine constraint, or intentionally diverges from upstream behavior.

## Index

| ADR | Title | PR / Origin | Key Seams |
| :--- | :--- | :--- | :--- |
| [0001](0001-extensible-hblk-v2-and-personal-stashes.md) | Extensible `.hblk` v2, `ISaveExtension`, Personal Bedside Stashes & Drop Transfer Ledger | PR #2 (M1) | `WhsSave`, `HenryStore`, `personal_stash`, `ItemTransferLedgerStore` |
| [0002](0002-coordinate-addressed-shared-chests-and-escrow-stacks.md) | Coordinate-Addressed Shared World Containers, Isolated Stack Escrows & Item Tombstones | PR #3 (M2) | `wc_<tag>_...`, `kdcmp.lua` (`W134`/`W217.L`), `WorldItemTombstoneStore` |
| [0003](0003-independent-horses-and-bracketed-entity-aliases.md) | Independent Horse Ownership, Saddlebag Splice, Quest Horse Gate & Bracketed `lh` Aliases | PR #4 (M3) | `WhsSave.M3Horse`, `horse` ext, `wo137.cpp`, `wo217_rules.h` |
| [0004](0004-reputation-isolation-individual-crime-and-shared-bathhouse.md) | Per-Player Reputation Splice, Selective Dialogue Isolation, Save-Paired Crime & Shared Bathhouse | PR #5 (M4) | `WhsSave.M4Reputation`, `JoinerCrimeStore`, `wo140` (`id=7`) |
| [0005](0005-preload-barrier-and-overlay-prompt-input.md) | Synchronous Pre-Load Barrier for `PostLoadReconstruction` & Overlay Actionmap / Native `F11`–`F12` Fallback | PR #6 (Hotfix 1) | `GameBridge.Wo136`, `wo216.cpp`, `KeybindPak`, `wo140.cpp` (`op 9` / `0xA5` kind `4`) |
| [0006](0006-shootable-nest-sync-and-container-reopen.md) | Bidirectional `Nest` / `DestroStash` Knockdown Sync, Multi-Radius Nest Resolution & Container Re-Open | PR #7 (Hotfix 2) | `ProtocolWo217` (`LootAsk 9` / `LootHost 14`), `kdcmp.lua` (`W217.L`) |
| [0007](0007-quiet-live-logs-and-launcher-lifecycle-shutdown.md) | Quiet Default Agent Telemetry (`--verbose` Opt-In) & Unified Companion / `trace_server` Shutdown | PR #8 (QOL) | `CombatPipe`, `GameBridge`, `VersionIpcServer`, `LauncherLifecycle` |
