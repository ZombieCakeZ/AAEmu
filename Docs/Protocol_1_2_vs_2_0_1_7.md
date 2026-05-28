# Protocol Delta — 1.2 (client_12_r208022) → 2.0.1.7 (Trion r249376)

Source files:
- Current 1.2 server: `AAEmu.Game/Core/Packets/{C2G,G2C}/{CS,SC}Offsets.cs`
- Old 2.0.1.7 alpha: `D:\2.0.1.7\AAEmu-client_version-2.0_client_-2015-09-14-\AAEmu-client_version-2.0_client_-2015-09-14-\AAEmu.Game\Core\Packets\{C2G,G2C}\{CS,SC}Offsets.cs`

Raw per-packet table: [`Opcode_Delta_G2C_1_2_vs_2_0_1_7.csv`](Opcode_Delta_G2C_1_2_vs_2_0_1_7.csv) and [`Opcode_Delta_C2G_1_2_vs_2_0_1_7.csv`](Opcode_Delta_C2G_1_2_vs_2_0_1_7.csv).

## Summary

### G2C (Server → Client)

| Bucket | Count |
|---|---|
| Total in 1.2 | **517** |
| Total in 2.0.1.7 | **661** |
| Renumbered (same name, different id) | **476** |
| New in 2.0.1.7 (no 1.2 equivalent) | **183** |
| Removed in 2.0.1.7 (existed in 1.2 only) | **41** |
| Identical id across both versions | **0** |

### C2G (Client → Server)

| Bucket | Count |
|---|---|
| Total in 1.2 | **258** |
| Total in 2.0.1.7 | **362** |
| Renumbered | **234** |
| New in 2.0.1.7 | **114** |
| Removed in 2.0.1.7 | **12** |

## Headline finding

**Zero opcodes survive unchanged between 1.2 and 2.0.1.7.** Every packet that exists in both versions has a different opcode number. Implication: the opcode constant tables are not patchable in place — the `AA-2.0.1.7` branch needs a full opcode-table replacement (both `CSOffsets.cs` and `SCOffsets.cs`). Any packet that exists in 1.2 but not 2.0.1.7 needs a `[Obsolete]` tag or removal; new 2.0 packets need fresh classes.

## Numbering scheme observations

- 1.2 numbering is **sequential** starting at 0x00 — packets registered in source order get consecutive ids.
- 2.0.1.7 numbering is **scattered** across 0x000–0x29e with large holes — the real Trion client opcodes from the wire protocol, captured / reverse-engineered into the alpha source. They are not designer-assigned but discovered.

This matters for how packets dispatch on each branch:
- On 1.2 the dispatch table can be a dense array (one slot per opcode).
- On 2.0.1.7 a sparse dictionary or compile-time switch is needed (the current 1.2 codebase already supports both forms — no infrastructure change needed, just use the sparse path).

## Categories of new packets in 2.0.1.7

Sampled from the new-packet list, grouped by name prefix:

| Group | Approx count | Examples |
|---|---|---|
| `SCMate*` | 12 | SCMateEquipmentExpiredPacket, SCMateExperienceChangedPacket |
| `SCHousing*` | 18 | SCHousingDecayChangedPacket, SCHousingDemolishedPacket, SCHousingTaxStatusPacket |
| `SCActability*` / `SCExpert*` | 14 | SCActabilityExpChangedPacket, SCExpertLimitModifiedPacket, SCExpertExpandedPacket |
| `SCPlot*` / plot-tree | 8 | SCPlotStartPacket, SCPlotChainStartedPacket |
| `SCAuctionHouse*` | 6 | SCAuctionHousePostedPacket, SCAuctionHouseFailedPacket |
| `SCSpecialty*` | 5 | SCSpecialtyRatioPacket, SCSpecialtyRegistrationPacket |
| `SCExpedition*` 2.0 additions | 9 | SCExpeditionRolePolicyChangedPacket, SCExpeditionSponsorChangedPacket |
| `SCUnknownPacket_0x*` (still RE-pending in alpha) | ~80 | (placeholder names for opcodes whose semantics weren't decoded in the alpha) |

The "unknown" cluster is significant — the original 2.0.1.7 alpha emulator never figured out what ~80 server packets do. We'll either need wire captures from a real Trion 2.0 server (unlikely) or to discover semantics from client-side handler analysis in `archeage.exe` if/when we want to implement those.

## Categories of removed packets (1.2 only)

Mostly internal / debug / diagnostics packets that didn't survive into 2.0:
- `CDominionStartUnkPacket` — dominion testing
- `CSQuestAcceptConditionalPacket` — likely merged into the regular accept-quest packet
- 39 others — full list in the CSV (filter Status=REMOVED_IN_2_0)

None of the removed packets appear to be load-bearing for gameplay; they're predominantly admin / cheat / debug paths.

## Recommended next steps

1. **Generate the 2.0.1.7 opcode source files** for the `AA-2.0.1.7` branch — replace the existing `CSOffsets.cs` and `SCOffsets.cs` with the 2.0.1.7 numbers, keeping the C# constant names identical so existing dispatch / packet registration code keeps compiling (Phase 4.1 in [the implementation plan](Compatibility_2_0_1_7_Plan_en.md)).
2. **Audit the 41 removed-in-2.0 packets** — most are safe to keep emitting (a 2.0 client that doesn't know the opcode just ignores it) but a handful may collide with re-used opcodes in 2.0.
3. **Stub the 183 new-in-2.0 packets** as empty classes with the right opcode so the registration table is complete. Real bodies come per feature (mate / housing / actability / etc.).

## How to regenerate

PowerShell from the repo root:

```powershell
function Get-Map($path) {
    $map = @{}
    Get-Content $path | ForEach-Object {
        if ($_ -match 'public const ushort (\w+)\s*=\s*(0x[0-9a-fA-F]+)') {
            $map[$matches[1]] = $matches[2]
        }
    }
    return $map
}

$cur = Get-Map 'AAEmu.Game\Core\Packets\G2C\SCOffsets.cs'
$old = Get-Map 'D:\2.0.1.7\AAEmu-client_version-2.0_client_-2015-09-14-\AAEmu-client_version-2.0_client_-2015-09-14-\AAEmu.Game\Core\Packets\G2C\SCOffsets.cs'
# ...emit CSV (see git history of this commit for the full script)
```

The CSV is committed so the lookup is grep-able without re-running the script.
