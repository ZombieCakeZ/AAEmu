# Collision Volume System

A server-side navigation aid for NPCs that does not require NavMesh or GeoNav data. NPCs use server-authored collision data to navigate around walls, walk on bridges and rooftops, stay inside designated zones, and recover when stuck.

The system has three building blocks:

- **Wall volumes** block direct movement through a vertical surface.
- **Floor volumes** raise the ground reference height (bridges, decks, rooftops, multi-floor buildings).
- **Height grids** sample terrain into a dense cell grid that NPCs can be bound to so they never leave their authored zone.

All authoring is done in-game with the `/colvol` (or `/cv` for short) admin command. Data is persisted as JSON under the server's volume storage and loaded automatically at server start.

---

## Quick start

1. Log in with a GM account.
2. Walk to the spot you want to author.
3. Run a `/cv` sub-command for the volume type you want to create.
4. Restart the server (or `/cv reload`) for changes to take effect for newly spawned NPCs.

Help text is always available with `/cv` (no args).

---

## Walls

A wall is a vertical surface defined by a polyline of corners and a height. NPCs cannot move from one side of the wall to the other through any of its segments.

### Manual wall (corner mode)
```
/cv wall start [height] [name]
```
Then walk along the wall and run `/cv corner` at each vertex. End with `/cv wall finish`.

- `height` defaults to a sensible building wall height.
- `/cv undo` removes the last placed corner.
- `/cv wall cancel` discards the current draft.

### Auto wall (scan mode)
```
/cv wall scan start [height] [epsilon] [name]
```
Walk along the wall. The server samples your position; when you call `/cv wall scan finish`, the samples are simplified (Douglas-Peucker, `epsilon` controls aggressiveness) into a clean polyline.

- Minimum 2m walked before `finish` is accepted.
- `/cv wall scan status` shows progress.
- `/cv wall scan cancel` aborts.

### Wall behavior
- NPCs check both the direct movement vector and two offset rays (body buffer ~0.3m) to prevent corner-clipping.
- A blocked NPC immediately attempts to repath around the wall using server-side A* over wall volumes (`FindWallPath`).
- If the NPC is stuck against a wall for 3 seconds, it gains 5 seconds of damage immunity and buff `7376` is applied. This prevents player exploits where NPCs are kited against geometry and burst down while immobile.

---

## Floors

A floor volume is a closed polygon at a specific Z height. NPCs standing inside the polygon use the floor's Z as their ground reference instead of the heightmap. This is what makes bridges, balconies, platforms, and multi-storey interiors traversable.

### Manual polygon
```
/cv floor start [name]
```
Walk to each vertex of the polygon and run `/cv corner`. End with `/cv floor finish`.

### Auto perimeter scan
```
/cv floor scan start [epsilon] [name]
```
Walk the perimeter back to your starting point. `/cv floor scan finish` builds the polygon. Minimum 3 points required.

### Box shorthand
```
/cv box <sizeX> <sizeY> <sizeZ> [name]
```
Drops an axis-aligned box volume at your position. Quick for square rooms or simple platforms.

### Floor behavior
- `GetReferenceHeight` queries CV floors first; if none match, falls back to the heightmap.
- A floor lifts the NPC's Z up to that floor's surface for as long as it stays inside the polygon.

---

## Height grids

A height grid is a dense cell grid (typically 1–4m per cell) that samples real terrain or walkable surfaces around a center point. NPCs can be **grid-bound** so they can only move on cells that exist in the grid. Leaving a bound grid requires walking through a Floor volume (the "exit" rule).

### Author a grid
```
/cv grid start [cellSize] [radius] [name]
```
Walk around to sample. The grid records the highest stable Z per cell.

- `cellSize` typically `1.0`–`4.0`.
- `radius` is the maximum sample distance from the start point.

Finish:
```
/cv grid finish
```
Cancel:
```
/cv grid cancel
```

### Inspect grids
```
/cv grid list
/cv grid status [name]
/cv grid show [radius] [name]    # spawns torch markers to visualize cells
/cv grid clear [name]
/cv grid delete <name>
```

### Binding NPCs to grids
There are two binding levels.

**Template-level (persistent, all spawns of this NPC):**
```
/cv gridbind <npcId> [gridName]
/cv gridunbind <npcId> [gridName]
```
- Without `gridName`, the wildcard `*` binding is added: the NPC is restricted to *any* grid that covers its spawn point.
- Call `gridbind` multiple times to bind to multiple specific grids.

**Instance-level (in-memory, this one spawn only):**
Set automatically at spawn — see "Auto-bind at spawn" below.

### Grid behavior
- NPCs use the cell's stored Z (not the heightmap) when standing on a grid cell.
- Movement to a cell that does not exist in the grid is blocked (`IsGridBoundNpcBlocked`).
- A grid-bound NPC can only leave its grid through a Floor volume (so you can author a designated exit).

### Inspect bindings
```
/cv bindings    # lists all NPC template bindings (volume + grid)
```

---

## Auto-bind at spawn

When `NpcSpawnerNpc` spawns an NPC, the server now:

1. Determines if the NPC is **aquatic** (see below).
2. For non-aquatic, non-flying NPCs, looks up template-level grid bindings.
3. If the NPC has bindings, probes the matching grid at the spawn position.
4. If a grid covers the spawn position, the NPC is bound to that grid for its lifetime, and its spawn Z is snapped to the grid's cell height.

Use `/cv gridbind <npcId> *` to opt every spawn of an NPC template into auto-binding to whichever grid covers it.

---

## IsAquatic detection

Aquatic NPCs (fish, sharks, rays) need 3D movement in water and must not walk on land.

At spawn, an NPC is flagged `IsAquatic` if:

- It has `CanFly` (movement id 2) and spawns inside water, **or**
- It is a normal NPC that spawns more than 10m below the water surface (the depth check prevents false-tagging beach NPCs whose feet touch shallow water).

Effects:
- Aquatic NPCs use the interpolated destination Z when querying ground height (allows depth-swimming toward targets above/below).
- `MoveTowards` blocks aquatic NPCs from moving onto land.
- Non-aquatic NPCs use their current Z as the height reference, which prevents Z-drop bugs near multi-floor geometry.

No authoring is required — detection runs from the world's water data.

---

## Wall stuck + combat evade immunity

If an NPC is repeatedly blocked by walls (or trapped against a height difference) for **3 seconds**, the server flags it `IsWallStuck` and:

1. Applies buff `7376` (Combat Evade — visible to clients).
2. Sets `CombatEvadeImmuneUntil = now + 5s`.
3. Overrides `ReduceCurrentHp` to ignore incoming damage while the timer is active.

The immunity clears when the NPC reaches attack range, gets within target tolerance, or enters `ReturnState`. The buff is removed at the same time.

This stops players from kiting NPCs into geometry corners and burning them down while the NPC is stationary.

---

## ReturnState integration

When an NPC enters `ReturnState` (leashes back to spawn):
- Wall path data is cleared.
- `WallBlockedTicks`, `IsWallStuck`, `CombatEvadeImmuneUntil`, buff `7376` are all reset.
- On `OnCompletedReturn`, the NPC is hard-teleported to its spawn via `Transform.Local.SetPosition(...)` (bypasses wall collision so a stuck NPC always recovers).

---

## Mesh volumes — not included

A `CollisionMesh` data type exists for API parity with downstream forks but **no loader is wired**. Mesh volumes would require client PAK extraction tooling and a binary mesh loader; this is out of scope for the upstream-friendly version. Use walls + floors + grids for authoring.

---

## Limitations vs full NavMesh

This system intentionally does not bundle NavMesh or GeoNav (Recast/Detour). Consequences:

- **Cliffs** are not auto-detected. Author a wall or constrain via a grid where you want to forbid drops.
- **Off-mesh boundaries** rely on grid bindings. NPCs without a grid binding can walk anywhere the heightmap allows.
- **Open terrain** uses the heightmap exactly like before — wall recovery and immunity still work.

In practice: bound your important NPC spawn areas with `/cv grid` and add `/cv wall` for hard obstacles. That covers the same scenarios NavMesh would handle, with the advantage that the data is authored in-game and version-controlled as JSON.

---

## Command reference (short)

| Command | Purpose |
|---|---|
| `/cv floor start [name]` | Begin manual floor polygon |
| `/cv floor scan start [eps] [name]` | Auto floor (walk perimeter) |
| `/cv wall start [h] [name]` | Begin manual wall polyline |
| `/cv wall scan start [h] [eps] [name]` | Auto wall (walk along) |
| `/cv corner` | Add vertex at current position |
| `/cv undo` | Remove last vertex |
| `/cv ... finish` / `... cancel` | Complete / discard the current draft |
| `/cv box <x> <y> <z> [name]` | Create axis-aligned box volume |
| `/cv scan start [name] [spacing]` | Auto multi-floor scan |
| `/cv grid start [cell] [radius] [name]` | Author a height grid |
| `/cv grid show [r] [name]` | Visualize a grid |
| `/cv grid list` / `status` / `clear` / `delete` | Manage grids |
| `/cv gridbind <npcId> [name]` | Bind NPC template to grid (use `*` for any) |
| `/cv gridunbind <npcId> [name]` | Remove grid binding |
| `/cv bind <npcId> <volId,...>` | Bind NPC template to volumes |
| `/cv unbind <npcId>` | Remove volume binding |
| `/cv bindings` | List all bindings |
| `/cv list [radius]` | List nearby volumes |
| `/cv info <id>` | Volume details |
| `/cv delete <id>` | Delete a volume |
| `/cv toggle <id>` | Enable / disable a volume |
| `/cv show [radius\|id]` | Visualize with torches |
| `/cv hide` | Remove debug markers |
| `/cv reload` | Reload all volume data from disk |
| `/cv save` | Force save |
