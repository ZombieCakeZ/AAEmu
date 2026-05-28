# AA 2.0.1.7 Compatibility — Implementation Plan

Branch: `AA-2.0.1.7-Trion-r249376-2015-09-18`
Target client: Trion Build r249376, 2015-09-18 (located at `D:\2.0.1.7\bin32\archeage.exe`)
Base codebase: current `dev-merged-features` (1.2 protocol)
Database strategy: **additive** — keep the existing 1.2 MySQL schema, add 2.0-only tables/columns on top, gate behavior with a `ProtocolVersion` flag.

---

## Strategic decisions

1. **One codebase, branched per protocol** — `AA-2.0.1.7-*` carries the 2.0 protocol layer; `master`/`develop` stays 1.2. No runtime version switch in the same binary (avoids handler-dispatch overhead and dual-test burden), but **DB schema is unified** so we can run both branches against the same MySQL.
2. **Use the existing 1.2 decrypted `compact.sqlite3` for development** (already present at `AAEmu.Game/Data/compact.sqlite3`, 119 MB). 2.0-specific static data gets added via a small JSON overlay loaded after the SQLite scan, until we have the real 2.0.1.7 `compact.sqlite3`.
3. **Decrypt / encrypt pipeline** is a separate tool, not part of the server runtime. Server reads decrypted `.sqlite3` (status quo). The tool is built once the cipher is reverse-engineered out of `archeage.exe` and produces:
   - `compact.sqlite` (encrypted, drop-in for client) → `compact.sqlite3` (decrypted, for server)
   - reverse direction for content updates

---

## Phase 1 — Inventory & deltas (no code changes)

### 1.1 Protocol delta

Diff our current `Core/Packets/G2C/Opcodes.cs` (1.2) vs the old 2.0.1.7 alpha source's `Core/Packets/G2C/SCOffsets.cs` + `Core/Packets/C2G/CSOffsets.cs`. Output: `Docs/Protocol_1_2_vs_2_0_1_7.md` with tables:

| Opcode name | 1.2 ID | 2.0.1.7 ID | Status |
|---|---|---|---|
| SCEnterWorldPacket | 0x1xx | 0x2xx | Renumber |
| SCUnitStatePacket | 0x09 | 0x0c | Renumber + new fields |
| SCActabilityChangedPacket | n/a | 0x21f | New in 2.0 |

For packets that **changed structure** between versions, document field-level diffs in a follow-up section.

### 1.2 Schema delta

`SQL/` folder in old 2.0.1.7 alpha has 23 game tables vs our current ~40+ in `aaemu_game.sql`. We need the *2.0-only* additions (Housing 2.0 columns, Mate tables, Actabilities, Plot trees) bolted onto our schema, not the old subset replacing it. Output: `SQL/Updates/0_2_0_1_7_additions.sql` with `CREATE TABLE IF NOT EXISTS` + `ALTER TABLE ADD COLUMN IF NOT EXISTS` blocks for:

- `actabilities` — XP progression per non-combat profession
- `housing_*` — plot system additions (Patron tax, decay, ownership transfer)
- `mate_*` — companion / vehicle hybrid system
- `plot_tree_*` — multi-step skill execution graphs
- New columns on `characters` (e.g. `actability_xp_*`, `housing_grace_until`)

Each block is idempotent so re-running is safe.

### 1.3 Static-data delta

`compact.sqlite3` tables that the old 2.0.1.7 alpha touches but the current 1.2-decrypted db doesn't have. Example: `actabilities`, `housing_decorations`, expanded `npc_chat_bubbles`. List of missing tables produced into `Docs/Compact_DB_2_0_1_7_Missing.md`.

For the missing static data we have three options (per table, decide individually):

a. **Stub** — empty table, server returns "no data" sensibly.
b. **JSON overlay** — author the minimal rows by hand in `Data/Overlays/2_0_1_7/*.json`, loaded after SQLite scan.
c. **Wait for real 2.0 db** — feature is gated until we have decrypted client data.

---

## Phase 2 — Decrypt / encrypt pipeline

### 2.1 Cipher identification

`compact.sqlite` (24.5 MB) at `D:\2.0.1.7\game\db\compact.sqlite` is encrypted with a real block/stream cipher (byte histogram on 64 KB sample: min=217 max=309 avg=256, near-uniform — rules out XOR). The cipher implementation lives in `D:\2.0.1.7\bin32\archeage.exe` (Trion 2.0.1.7 client, x86).

Reverse-engineering approach:
1. Open `archeage.exe` in Ghidra / IDA Free.
2. Locate string `compact.sqlite` (and `compact.sqlite3` if present) — typically near the open-file site.
3. Trace the read path to the decryption call. AA clients of this era commonly use AES-128-CBC with a hardcoded 16-byte key, sometimes XXTEA on smaller blocks.
4. Extract the key and IV (or key-derivation function).
5. Verify on the first page (offset 0..1024) by decrypting and comparing to the SQLite header.

Estimated effort: 4–8 hours for someone familiar with AA client RE.

### 2.2 Tool: `AAEmu.ClientDb` (new project)

Stand-alone .NET console tool at `Tools/AAEmu.ClientDb/`. Two subcommands:

```
aaemu-clientdb decrypt <input.sqlite> <output.sqlite3>
aaemu-clientdb encrypt <input.sqlite3> <output.sqlite>
```

Implementation uses `System.Security.Cryptography.Aes` (or appropriate library) with the discovered key. Unit tests against known-good pairs from the 1.2 client database (where we have both encrypted and decrypted) before we rely on it for 2.0.

### 2.3 Round-trip CI test

Once the tool works, add an integration test: take a small known SQLite file → encrypt → decrypt → compare bytes. Guards against future cipher-mode bugs.

---

## Phase 3 — Schema additions (additive, no breaking changes)

Implementation order, smallest blast-radius first:

### 3.1 New tables (no risk to existing data)

1. `actabilities` — labor-points / non-combat XP. Add manager `ActabilityManager` (already exists as stub in old alpha source — copy + adapt). Wire into `Character.Load` / `Character.Save`.
2. `mate_*` (mate, mate_passenger, mate_skill) — companion system. New `MateManager` + `Mate : Unit` model. Wire into spawn / despawn lifecycle.
3. `housing_decorations` + `housing_decay` — plot-system additions. Extends existing `HousingManager`.

### 3.2 Column additions on existing tables (controlled risk)

Each `ALTER TABLE` ships in a `SQL/Updates/<date>_<topic>.sql` script with:
- `ADD COLUMN IF NOT EXISTS <col> <type> DEFAULT <safe-default>` — so 1.2 binary still reads the row with safe defaults.
- A matching `DataAnnotation` migration if any of our managers cache schema.

Touched tables: `characters` (`actability_xp_*`, `housing_grace_until`, `protocol_version` hint), `items` (2.0-only crafting tags), `housings` (Patron / decay tracking).

### 3.3 Behavior gating

Add a small `Configurations.Compatibility` block:

```json
{
  "Compatibility": {
    "ProtocolVersion": "2_0_1_7",
    "EnableActabilities": true,
    "EnableMates": true,
    "EnableHousingDecay": false
  }
}
```

Branches stay narrow: 1.2 binary defaults all to false / "1_2"; 2.0.1.7 branch defaults the inverse. Saves a lot of `#ifdef`-style code branching.

---

## Phase 4 — Protocol-layer adaptation

This is the largest phase and the one that actually makes the 2.0.1.7 client connect.

### 4.1 Opcode table

Replace `Core/Packets/G2C/Opcodes.cs` and `Core/Packets/C2G/Opcodes.cs` constants on the 2.0.1.7 branch with the 2.0.1.7 IDs (from old alpha source's `CSOffsets.cs` / `SCOffsets.cs`). Each opcode keeps its name → less churn in the rest of the codebase. Renames only happen for packets whose semantics changed.

### 4.2 Handshake

2.0.1.7 uses a different login handshake:
- `0xDD` + level (1 or 5) magic — present in old source at `Core/Network/Game/GamePacket.cs`
- AES + XOR via `CSAesXorKeyPacket` — present in old source
- `EncryptionManager.GetSCMessageCount()` ratcheting state

Port the 2.0.1.7 `EncryptionManager` from the old alpha source into `AAEmu.Commons/Cryptography/`. The 1.2 branch keeps its own.

### 4.3 Packet structure deltas

Packets whose payload changed between 1.2 and 2.0.1.7 (subset, full list comes out of Phase 1.1):

- `SCEnterWorldPacket` — adds Patron-status flag, server-time field
- `SCUnitStatePacket` — adds Mate parent objId, actability levels
- `SCCharacterListPacket` — adds housing-summary block per character
- `CSStartSkillPacket` — adds plot-tree start node selector

For each, write the 2.0.1.7 layout in the existing packet class and `#if PROTOCOL_2_0_1_7` (or branch-only file) — to be decided in Phase 1.

### 4.4 New packets (2.0-only)

From old alpha source, the deltas relative to 1.2 are roughly:
- ~60 new G2C packets (mate, housing 2.0, actability, plot-tree state, auction-house improvements)
- ~20 new C2G packets

Port each as a fresh class. Many will route to existing managers (e.g. mate packets → MateManager) so server logic shares with 1.2 where possible.

---

## Decisions before starting Phase 1

1. **One MySQL DB or two?** User leans toward one (additive schema). Confirmed: stick with one. All `SQL/Updates/` scripts are idempotent `CREATE IF NOT EXISTS` / `ADD COLUMN IF NOT EXISTS`.
2. **Branch lifetime** — does the 2.0.1.7 branch eventually merge back into `develop`, or stay parallel forever? Recommended: stay parallel, periodically rebase from `develop` to pull bug fixes. Protocol changes never flow upward.
3. **Test client** — confirmed using `D:\2.0.1.7\bin32\archeage.exe`. Need a stable login (Trion auth servers are dead; the client must be patched to point to localhost). Old alpha source likely already has this patch — check `D:\2.0.1.7\bin32\archeageus.ini`.

---

## Suggested execution order

1. (this commit) Write this plan + create empty per-phase folders.
2. Phase 1.1 — Opcode delta table. 1–2 hours.
3. Phase 1.2 — Schema delta. 1–2 hours.
4. Phase 1.3 — Static-data delta. 1 hour.
5. Phase 3.1 — New tables + minimal managers (actability, mate). 1–2 days. Can ship without protocol changes — 1.2 client just won't see them.
6. Phase 2.1 — Cipher RE. Half day to a day.
7. Phase 2.2 — Build the tool. Half day.
8. Phase 4 — Protocol layer. Multi-day rolling work, can be staged opcode-by-opcode with a working sub-feature each step.

Each phase is a separate commit (or short PR-style series) so progress is reviewable and revertable.
