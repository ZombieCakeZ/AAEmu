# Schema Delta — 1.2 → 2.0.1.7

Phase 1.2 of the [2.0.1.7 compatibility plan](Compatibility_2_0_1_7_Plan_en.md).

## Counter-intuitive finding

Naïvely comparing `SQL/aaemu_game.sql` between the current 1.2 codebase and the old 2.0.1.7 alpha source produces a misleading result:

| | 1.2 (current) | 2.0.1.7 alpha (old) |
|---|---|---|
| `CREATE TABLE` count | 34 | 23 |
| Unique to that version | 12 | 1 (`cash_shop_item`) |
| Shared | 22 | 22 |

The 2.0.1.7 alpha is **less complete than current 1.2**, not more. The alpha was abandoned mid-development — it never gained the 12 tables current 1.2 has accumulated over years of maintenance (`accounts`, `audit_*`, `crime`, `doodads`, `slaves`, `music`, `ics_*` shop, `character_active_buffs`, `item_containers`).

So the schema delta cannot be derived by diffing the two repos' SQL.

## How we derive the real 2.0 schema additions

The 2.0-specific state lives in the **protocol** — every new G2C packet in 2.0 carries fields the server must populate, which means the server must track that state, which means new schema (or extensions to existing tables).

Feature clusters that need new persistent state, derived from the new G2C packets identified in Phase 1.1:

### Mate system — `mate`, `mate_xp`, `mate_equipment_expiry`
Driving packets:
- `SCMateXpChangedPacket`
- `SCMateEquipmentExpiredPacket`
- `SCMateLevelChangedPacket`

Per-mate state to persist: owner_char_id, npc_template_id, xp, level, equipment slot expiries.

### Actability / non-combat XP — `character_actability`
Driving packets:
- `SCActabilityExpChangedPacket`
- `SCActabilityChangedPacket`
- `SCExpertLimitModifiedPacket`
- `SCExpertExpandedPacket`

Per-character, per-actability: xp, current step. Cap (`expert_limit`) is per-character not per-actability.

### Housing 2.0 — additions to `housings` + new `housing_decay`, `housing_tax_status`
Driving packets:
- `SCHousingDecayChangedPacket`
- `SCHousingDemolishedPacket`
- `SCHousingTaxStatusPacket`
- `SCHousingOwnedListPacket` (extended payload)

Columns to add to existing `housings`: decay_started_at, grace_period_until, demolish_pending, patron_status.
New table: `housing_tax_history` for the running ledger.

### Plot trees (multi-step skill graphs) — `plot_tree`, `plot_tree_node` (mostly static, from `compact.sqlite3`)
Driving packets:
- `SCPlotChainStartedPacket`
- `SCPlotStartPacket`
- `SCPlotInteractionPacket`

The static plot-tree definitions live in the client database (`compact.sqlite3`) — not in the MySQL DB. Server state is just "which plot is currently executing for which unit" and that fits in-memory; no MySQL change required for this cluster.

### Specialty / hauling — `specialty_pack`, `specialty_ratio`
Driving packets:
- `SCSpecialtyRatioPacket`
- `SCSpecialtyRegistrationPacket`

Pack ratios (supply/demand) tracked per delivery point. Persistence required so ratios survive restart.

### Cash shop 2.0 — extends existing `ics_*` tables in 1.2 with the 2.0 `cash_shop_item` shape
The 2.0 alpha defines `cash_shop_item`; current 1.2 has split this into `ics_menu`, `ics_shop_items`, `ics_skus`. The 1.2 layout is richer. Backporting goes the other way — 2.0 schema is a subset of 1.2 here, no migration needed.

## Net new-table list (recommendation for Phase 3)

Ordered by independence so each ships in isolation:

| Table | Purpose | Schema sketch |
|---|---|---|
| `character_actability` | Non-combat XP per actability per character | `(character_id, actability_id, xp, step, level)` |
| `mate` | Persistent mate (companion) state | `(id, owner_character_id, npc_template_id, name, xp, level, summoned_at)` |
| `mate_equipment` | Mate gear slots and expiries | `(id, mate_id, slot, item_id, expires_at)` |
| `housing_tax_history` | Per-house tax payment ledger | `(id, housing_id, paid_at, amount, source)` |
| `specialty_pack_ratio` | Trade-pack supply/demand | `(zone_group_id, ratio, computed_at)` |

Column additions on existing tables:

| Table | New columns |
|---|---|
| `characters` | `actability_xp_overall INT DEFAULT 0`, `expert_limit_overrides JSON NULL` |
| `housings` | `decay_started_at DATETIME NULL`, `grace_period_until DATETIME NULL`, `demolish_pending TINYINT DEFAULT 0`, `patron_status TINYINT DEFAULT 0` |
| `accounts` (1.2 only — keep as is) | no change |

All `ADD COLUMN` statements use `IF NOT EXISTS` so re-running the migration is safe and 1.2 binaries running against the same MySQL keep working (they just ignore the new columns).

## Migration script naming convention

`SQL/Updates/2_0_1_7_<NN>_<topic>.sql` where `NN` is 2-digit ordinal, e.g.:

- `SQL/Updates/2_0_1_7_01_actabilities.sql`
- `SQL/Updates/2_0_1_7_02_mates.sql`
- `SQL/Updates/2_0_1_7_03_housing_2_0.sql`
- `SQL/Updates/2_0_1_7_04_specialty.sql`

These get applied via the existing AAEmu Updater (`UpdateFiles`), which tracks which scripts have been run. Each script is idempotent.

## Out of scope for Phase 1.2

- Actually creating the SQL files (that's Phase 3.1).
- Reading the field-level layout of new packets to confirm the schema sketch (Phase 4.3 — packet structure work).
- The static-data side of plot trees / mate templates / actability definitions (Phase 1.3 — static-data delta).
