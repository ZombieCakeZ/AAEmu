# Collision Volume System

A server-side navigation aid for NPCs that does not require NavMesh or GeoNav data. NPCs use server-authored collision data to navigate around walls, walk on bridges and rooftops, optionally stay inside designated zones, and recover when stuck.

The system has three building blocks:

- **Wall volumes** block movement through a vertical surface.
- **Floor volumes** raise the ground reference height (bridges, decks, rooftops, multi-floor buildings).
- **Height grids** sample real terrain or walkable surfaces into a dense cell grid. Any NPC walking over a grid follows the grid height automatically; admins can additionally *bind* an NPC template to a grid for strict containment.

All authoring is done in-game with the `/colvol` (or `/cv` for short) admin command. Data is persisted as JSON under the server's volume storage and loaded automatically at server start.

---

## Quick start

1. Log in with a GM account.
2. Walk to the spot you want to author.
3. Run a `/cv` sub-command for the volume type you want to create.
4. Restart the server (or `/cv reload`) so newly spawned NPCs pick up the data.

Help text is always available with `/cv` (no args).

---

## Walls

A wall is a vertical surface defined by a polyline of corners and a height. NPCs cannot move from one side of the wall to the other through any of its segments.

### Manual wall (corner mode)
```
/cv wall start [height] [name]
```
Walk along the wall and run `/cv corner` at each vertex. End with `/cv wall finish`.

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
- NPCs check the direct movement vector AND two offset rays (body buffer ~0.3m) to prevent corner-clipping.
- A blocked NPC immediately attempts to repath around the wall using server-side A* over wall volumes (`FindWallPath`).
- In combat, `BaseCombatBehavior` pre-computes the A* path **before** the NPC walks into the wall, so chasing through buildings looks natural rather than "bump and recover".
- Random roaming (`AiUtils.CalcNextRoamingPosition`) rejects any destination that would require crossing a wall.
- If an NPC is stuck against a wall (or trapped by a height gap) for **3 seconds**, it gains **5 seconds of damage immunity** and buff `7376` is applied. This prevents player exploits where NPCs are kited against geometry and burst down while immobile.

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
- `GetReferenceHeight` queries CV floors (and grids — see below); falls back to the heightmap if nothing matches.
- A floor lifts the NPC's Z to that floor's surface for as long as it stays inside the polygon.
- Grid-bound NPCs use Floor volumes as designated exits when they need to leave their grid.

---

## Height grids

A height grid is a dense cell grid (typically 1–4m per cell) that samples real terrain or walkable surfaces around a center point.

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

### How NPCs interact with a grid

There are two modes:

#### 1. Auto-prefer (default, no binding needed)

Any NPC walking over a cell that has grid data will follow the grid's Z instead of the heightmap — **but only if the grid height is within 2m of the NPC's current Z**. This tolerance prevents an NPC falling past a roof grid from suddenly snapping onto it.

What this gives you:
- Walk a grid over a hilltop fort: NPCs roam normally on the surrounding terrain, but when they step onto the fort they follow the authored Z (no clipping, no floating).
- NPCs can freely walk on AND off the grid. No exit volume needed.
- Zero authoring overhead per NPC: just walk the grid and reload.

Implementation: `WorldManager.GetReferenceHeight` block `3b-active`, calling `CollisionVolumeManager.HasGridData` + `GetGridHeight`.

#### 2. Strict bind (opt-in, prevents leaving the grid)

For special cases — rooftop guards, balcony NPCs, NPCs that should never wander off a structure — bind the NPC template to a specific grid. Bound NPCs can only leave the grid by walking through a Floor volume (the designated exit).

```
/cv gridbind <npcTemplateId> [gridName]
/cv gridunbind <npcTemplateId> [gridName]
```

- Without `gridName`: wildcard `*` binding — the NPC is restricted to *any* grid that covers its position.
- Call `gridbind` multiple times to bind to multiple specific grids.
- Bound NPCs are blocked from moving onto cells outside their grids (`IsGridBoundNpcBlocked`).

### Inspect bindings
```
/cv bindings    # lists all template-level NPC bindings (volume + grid)
```

---

## IsAquatic detection

Aquatic NPCs (fish, sharks, rays) need 3D movement in water and must not walk on land.

At spawn an NPC is flagged `IsAquatic` if:

- It has `CanFly` (movement id 2) and spawns inside water, **or**
- It is a normal NPC that spawns more than 10m below the water surface (the depth check prevents false-tagging beach NPCs whose feet touch shallow water).

Effects:
- Aquatic NPCs use the interpolated destination Z when querying ground height (allows depth-swimming toward targets above/below).
- `MoveTowards` blocks aquatic NPCs from moving onto land.
- Non-aquatic NPCs use their current Z as the height reference, which prevents Z-drop bugs near multi-floor geometry.

No authoring required — detection runs from the world's water data.

---

## Wall stuck + combat evade immunity

If an NPC is repeatedly blocked by walls (or trapped against a height difference) for **3 seconds**, the server flags it `IsWallStuck` and:

1. Applies buff `7376` (Combat Evade — visible to clients).
2. Sets `CombatEvadeImmuneUntil = now + 5s`.
3. Overrides `ReduceCurrentHp` to ignore incoming damage while the timer is active.

The immunity clears when the NPC reaches attack range, gets within target tolerance, or enters `ReturnState`. The buff is removed at the same time.

This stops players from kiting NPCs into geometry corners and burning them down while the NPC is stationary.

---

## AI integration

CV awareness is wired into the AI layer at two points:

### Random roaming — `AiUtils.CalcNextRoamingPosition`
When an NPC picks its next idle wander target it now:
- Rejects targets that would require crossing a wall volume (uses `IsBlockedByWall`).
- For grid-bound NPCs, rejects targets outside the bound grid that aren't reachable through a Floor exit (uses `IsGridBoundNpcBlocked`).
- For aquatic NPCs, rejects targets on land.

If none of the random samples is valid, the NPC stays at its idle position for that tick.

### Combat chase — `BaseCombatBehavior.MoveInRange`
When chasing a target it now:
- Detects a direct line-of-sight block via `IsBlockedByWall`.
- Pre-computes an A* path around walls (`FindWallPath`) and stores it on the NPC's `_wallPath` so the next `MoveTowards` tick already follows waypoints.
- Clears `IsWallStuck`, `CombatEvadeImmuneUntil`, the wall path, and buff `7376` when the NPC reaches attack range.

This makes combat-chase look smooth — the NPC routes around the obstacle in one move instead of bumping into it first.

---

## ReturnState integration

When an NPC enters `ReturnState` (leashes back to spawn):
- Wall path data is cleared.
- `WallBlockedTicks`, `IsWallStuck`, `CombatEvadeImmuneUntil`, buff `7376` are all reset.
- On `OnCompletedReturn`, the NPC is hard-teleported to its spawn via `Transform.Local.SetPosition(...)` (bypasses wall collision so a stuck NPC always recovers).

---

## Data layout on disk

All CV data lives under `<Server-Bin>/Data/CollisionVolumes/`. The directory is created automatically on first server start if it doesn't exist.

| File pattern | Contents |
|---|---|
| `<worldName>.json` (e.g. `main_world.json`) | Walls, Floors, Boxes for that world |
| `pak_*.json` (e.g. `pak_buildings.json`) | Optional extra volume files; merged into the same world at load |
| `heightgrid_<gridName>.json` | Output of `/cv grid finish` |
| `npc_bindings.json` | Template-level NPC volume bindings (`/cv bind`) |
| `npc_grid_bindings.json` | Template-level NPC grid bindings (`/cv gridbind`) |

Files matching `*.bak`, or starting with `npc_bindings` / `npc_grid_bindings` / `heightgrid_`, are skipped by the world-volume loader and handled by their own load paths.

---

## Mesh volumes — not included

A `CollisionMesh` data type exists for API parity with downstream forks but **no loader is wired**. Mesh volumes would require client PAK extraction tooling and a binary mesh loader; this is out of scope for the upstream-friendly version. Use walls + floors + grids for authoring.

---

## Limitations vs full NavMesh

This system intentionally does not bundle NavMesh or GeoNav (Recast/Detour). Consequences:

- **Cliffs** are not auto-detected. Author a wall, or constrain via a strict grid binding, where you want to forbid drops.
- **Off-mesh boundaries** rely on grid bindings (strict mode). Unbound NPCs can walk anywhere the heightmap allows.
- **Open terrain** uses the heightmap exactly like before — wall recovery, grid-prefer, and immunity all still work.

In practice: walk a grid over your important NPC areas with `/cv grid` and add `/cv wall` for hard obstacles. The auto-prefer grid logic gives you the "NPCs follow the authored surface" benefit without per-template setup. Use strict `/cv gridbind` only when you actually want NPCs locked to a zone.

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
| `/cv gridbind <npcId> [name]` | **Strict** bind: lock NPC template to grid (use `*` for any) |
| `/cv gridunbind <npcId> [name]` | Remove strict grid binding |
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
