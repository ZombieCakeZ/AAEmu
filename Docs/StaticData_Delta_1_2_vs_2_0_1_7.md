# Static Data Delta — 1.2 → 2.0.1.7

Phase 1.3 of the [2.0.1.7 compatibility plan](Compatibility_2_0_1_7_Plan_en.md).

## Sources of static data

| Source | What it holds | Decryption needed? |
|---|---|---|
| `AAEmu.Game/Data/compact.sqlite3` | items, skills, buffs, NPCs, quests, doodads, recipes, plot trees, achievements — the bulk of game definition | already decrypted on disk |
| `AAEmu.Game/Data/*.xml` | world cells, doodad spawn lists, terrain hooks | plain XML |
| `AAEmu.Game/Data/*.json` | character templates, battlefield defs, housing bindings | plain JSON |
| `AAEmu.Game/Data/Worlds/*` | per-world data (cell hmaps, AI paths, geo data) | plain |

For 2.0.1.7 compatibility, the question is: **what static data does the 2.0.1.7 client expect that the 1.2 `compact.sqlite3` does not have?**

## Why we can't answer that fully yet

The 2.0.1.7 client's own `D:\2.0.1.7\game\db\compact.sqlite` is encrypted (real cipher, not XOR — see Phase 1.1 / Phase 2). Until Phase 2 produces a working decryptor, we can't `sqlite3 .schema` the 2.0 database and produce a direct table-level diff against 1.2.

What we can derive **without** the decrypted 2.0 db:

## Manager-class delta (proxy signal)

The old 2.0.1.7-alpha emulator source has 41 `*Manager.cs` files; current 1.2 has 39. The interesting deltas:

**Present in 2.0 alpha but not in current 1.2:**
- `TransferTelescopeManager` — 2.0-only telescope / fast-travel feature

**Present in current 1.2 but not in 2.0 alpha** (mostly later additions):
- AI / pathing / geo: `AIManager`, `AiGeodataManager`, `AiPathsManager`, `AStar` family
- Combat / PvP: `CrimeManager`, `DuelManager`, `SusManager`
- World: `FishSchoolManager`, `GameScheduleManager`, `PublicFarmManager`, `RadarManager`, `SubZoneManager`, `TaxationsManager`, `TimedRewardsManager`, `MusicManager`
- Misc: `ManaRegenManager`, `TimedRewardsManager`

The headline conclusion: **the 2.0 alpha emulator did not need new "feature managers" beyond what 1.2 already has, plus `TransferTelescopeManager`**. The 2.0-specific behaviour lives inside the existing managers (`HousingManager`, `MateManager`, `AuctionManager`, `PlotManager`, `SkillManager`) — they just take different code paths against different data shapes.

This is good news for the backport: we don't need to grow the manager surface, only adjust the data loaders inside existing managers and the packet emitters that read from them.

## What likely changes in compact.sqlite3 between 1.2 and 2.0

Inferred from the protocol delta in Phase 1.1 — every new G2C packet group implies static-data backing. Likely new or extended tables:

| compact.sqlite3 table | Why it changed |
|---|---|
| `actabilities` / `actability_levels` | Actability progression curves (new in 2.x) |
| `mate_templates` / `mate_skills` | Mate combat + behaviour definitions |
| `housing_decorations` / `housing_decoration_categories` | Housing 2.0 expanded decoration catalogue |
| `plot_*` (chain, node, condition, expression) | Plot-tree definitions for multi-step skills |
| `expert_*` | Expert-rank tiers above normal actabilities |
| `specialty_*` | Trade pack zones, ratios, recipes |
| `items` / `item_armors` / `item_weapons` | New stat columns (crafting tags, set bonuses, racial restrictions in 2.0) |
| `skills` | `plot_id` linkage now mandatory; new effect-set ids |
| `cash_shop_*` | New SKU layout in 2.0 |

We cannot confirm column-level diffs without the decrypted 2.0 db.

## Tactical plan until we have the real 2.0 compact.sqlite3

1. **Continue with the existing 1.2 compact.sqlite3** (already at `AAEmu.Game/Data/compact.sqlite3`, 119 MB). The server boots and runs.
2. **Build the per-feature loader code on 1.2 data shape** — Phase 3 will add the MySQL side; the static-data side reads from whatever the server has, with NULL-safe defaults.
3. **Use JSON overlays at `Data/Overlays/2_0_1_7/<table>.json`** for the handful of items / skills / housing entries that are 2.0-only and would otherwise be missing. The loader looks at the overlay AFTER scanning the SQLite so the JSON entries augment what's there.
4. **When Phase 2 produces a working decryptor**, dump the 2.0 `compact.sqlite` to `compact.sqlite3.2_0_1_7`, diff its `.schema` and per-table row counts against 1.2, then decide which overlays to retire and which to fold back into a unified static-data store.

## Overlay format draft

```json
// Data/Overlays/2_0_1_7/actabilities.json
{
  "actabilities": [
    { "id": 1,  "name": "Husbandry",      "max_step": 19, "expert_step_count": 5 },
    { "id": 2,  "name": "Logging",        "max_step": 19, "expert_step_count": 5 },
    { "id": 3,  "name": "Mining",         "max_step": 19, "expert_step_count": 5 }
    // ...
  ]
}
```

Loader (Phase 3) reads `actabilities` from the SQLite first (returns empty if the table is absent — 1.2 db case), then appends any rows from the overlay JSON. Duplicate `id` is a configuration error logged at boot.

## Tooling deferred to Phase 2

To validate this plan once Phase 2 lands, we'll want a small command:

```
aaemu-clientdb diff <old.sqlite3> <new.sqlite3> --report schema-delta.md
```

That walks both databases and produces a `CREATE TABLE` diff plus row-count summary per table. Part of the Phase 2 `AAEmu.ClientDb` tool.
