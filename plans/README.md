# Co-op Campaign Polish — Implementation Plans

Tracking folder for our four co-op campaign milestones on `KingdomCome-Together`, kept separate from upstream's `docs/` work-order archive.

## Workflow

1. Dedicated branch & git worktree per milestone under `d:\Code\kcd2\worktrees\`.
2. Implementation plan with numbered task checklist committed first in `plans/`.
3. One atomic commit per completed task (updating `[x]` in the milestone plan).
4. Record durable design, wire, save-format, and engine-hook decisions in [../docs/adr/README.md](../docs/adr/README.md) (`docs/adr/NNNN-*.md` — never upstream `WO-*` files) so future upstream merges have clear context.
5. Pull request back to `main` once all tasks and automated gates pass.

## Milestones

| Wave | Milestone | Plan | Branch | Status |
|---|---|---|---|---|
| 1 | **M1: Personal Bedside Stashes, Extensible `.hblk` v2 & Drop Duping Fixes** | [m1-personal-stashes-and-drops.md](m1-personal-stashes-and-drops.md) | `feat/m1-personal-stashes` | Merged (PR #2) |
| 1 | **M2: Shared World Chests, Corpse Stack/Put Sync & Loose World Pickups** | [m2-shared-world-loot-and-chests.md](m2-shared-world-loot-and-chests.md) | `feat/m2-shared-world-loot` | Merged (PR #3) |
| 2 | **M3: Independent Horse Ownership, Saddlebags, Tack & Whistle** | [m3-independent-horses.md](m3-independent-horses.md) | `feat/m3-independent-horses` | Merged (PR #4) |
| 2 | **M4: Independent/Shared Reputation, Individual Crime Persistence & Shared Bathhouse** | [m4-reputation-crime-bathhouse.md](m4-reputation-crime-bathhouse.md) | `feat/m4-reputation-crime-bathhouse` | Merged (PR #5) |
