using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json;

namespace AAEmu.Game.Models.Game.World;

/// <summary>
/// A cell-based height grid that stores accurate floor heights (including buildings).
/// Each cell is CellSize × CellSize meters and can store multiple floor heights (multi-floor).
/// Cells with no data = impassable (admin never walked there = wall/obstacle).
/// Supports neighbor interpolation to fill small gaps (stairs, doorways).
/// </summary>
public class HeightGridFile
{
    // ── Tuning constants (single source of truth) ──

    /// <summary>Default symmetric Z window for floor lookups. Matches RecordHeight zTolerance so reads/writes use the same span.</summary>
    public const float DefaultLookupTolerance = 1.5f;

    /// <summary>Tight Z band around an anchor floor used to keep multi-cell interpolation on the same storey.</summary>
    public const float AnchorBand = 0.8f;

    /// <summary>Max Z difference between the 4 inner cells before bicubic interpolation gives up (= MinFloorSeparation).</summary>
    public const float BicubicCliffMaxStep = 2.5f;

    /// <summary>
    /// World name (e.g. "main_world")
    /// </summary>
    public string WorldName { get; set; } = "main_world";

    /// <summary>
    /// Grid name — unique identifier for this grid (e.g. "Hacly", "Tunnel_North").
    /// If empty, defaults to WorldName. Multiple grids can exist for the same world.
    /// </summary>
    public string GridName { get; set; } = "";

    /// <summary>
    /// Cell size in meters (default 1.0m)
    /// </summary>
    public float CellSize { get; set; } = 1.0f;

    /// <summary>
    /// Grid cell data, keyed by "cellX,cellY" (string for JSON serialization).
    /// Each cell stores a sorted list of floor Z heights (multi-floor support).
    /// </summary>
    public Dictionary<string, List<float>> Cells { get; set; } = [];

    /// <summary>
    /// Sample count per floor per cell — tracks how many times each floor height was recorded.
    /// Used for running average that converges to true floor height with more measurements.
    /// Keys match Cells keys; each list is parallel to the Cells floor list.
    /// </summary>
    public Dictionary<string, List<int>> SampleCounts { get; set; } = [];

    /// <summary>
    /// Metadata: when the grid was last updated
    /// </summary>
    public string LastUpdated { get; set; } = "";

    /// <summary>
    /// Runtime lookup cache: (cellX, cellY) → floor list.
    /// Avoids string allocation/parsing on every GetFloorsAt call.
    /// Built lazily on first lookup.
    /// </summary>
    [JsonIgnore]
    private Dictionary<(int, int), List<float>> _cellCache;

    /// <summary>
    /// Get or build the runtime cell cache for fast lookups.
    /// </summary>
    [JsonIgnore]
    private Dictionary<(int, int), List<float>> CellCache
    {
        get
        {
            if (_cellCache != null) return _cellCache;
            _cellCache = new Dictionary<(int, int), List<float>>(Cells.Count);
            foreach (var (key, floors) in Cells)
            {
                var (cx, cy) = ParseCellKey(key);
                _cellCache[(cx, cy)] = floors;
            }
            return _cellCache;
        }
    }

    /// <summary>
    /// Invalidate the runtime cache (call after modifying Cells).
    /// </summary>
    public void InvalidateCache() => _cellCache = null;

    /// <summary>
    /// Total number of cells with data
    /// </summary>
    [JsonIgnore]
    public int CellCount => Cells.Count;

    /// <summary>
    /// Ensure SampleCounts has entries for all cells (legacy migration for older grids).
    /// Call after loading from JSON.
    /// </summary>
    public void EnsureSampleCounts()
    {
        SampleCounts ??= [];
        foreach (var (key, floors) in Cells)
        {
            if (!SampleCounts.TryGetValue(key, out var counts) || counts.Count != floors.Count)
                SampleCounts[key] = Enumerable.Repeat(1, floors.Count).ToList();
        }
    }

    // ── Cell coordinate helpers ──

    /// <summary>
    /// Convert world coordinates to integer cell coordinates
    /// </summary>
    public static (int CX, int CY) WorldToCell(float x, float y, float cellSize)
    {
        return ((int)MathF.Floor(x / cellSize), (int)MathF.Floor(y / cellSize));
    }

    /// <summary>
    /// Convert world coordinates to cell key string
    /// </summary>
    public static string CellKey(float x, float y, float cellSize)
    {
        var (cx, cy) = WorldToCell(x, y, cellSize);
        return $"{cx},{cy}";
    }

    /// <summary>
    /// Create a cell key from integer cell coordinates
    /// </summary>
    public static string CellKeyFromCoords(int cx, int cy) => $"{cx},{cy}";

    /// <summary>
    /// Parse a cell key back to integer coordinates
    /// </summary>
    public static (int CX, int CY) ParseCellKey(string key)
    {
        var idx = key.IndexOf(',');
        return (int.Parse(key.AsSpan(0, idx)), int.Parse(key.AsSpan(idx + 1)));
    }

    // ── 8-neighbor offsets (N, NE, E, SE, S, SW, W, NW) ──
    private static readonly (int DX, int DY)[] Neighbors =
    [
        (0, 1), (1, 1), (1, 0), (1, -1),
        (0, -1), (-1, -1), (-1, 0), (-1, 1)
    ];

    // ── 4-neighbor offsets (N, E, S, W) — for cardinal checks ──
    private static readonly (int DX, int DY)[] CardinalNeighbors =
    [
        (0, 1), (1, 0), (0, -1), (-1, 0)
    ];

    // ── Data access ──

    /// <summary>
    /// Get floor heights at a world position, or null if no data
    /// </summary>
    public List<float> GetFloorsAt(float x, float y)
    {
        var (cx, cy) = WorldToCell(x, y, CellSize);
        return CellCache.GetValueOrDefault((cx, cy));
    }

    /// <summary>
    /// Check if a cell has data (walkable)
    /// </summary>
    public bool HasData(float x, float y)
    {
        return Cells.ContainsKey(CellKey(x, y, CellSize));
    }

    /// <summary>
    /// Check if a cell at integer coords has data
    /// </summary>
    public bool HasDataAt(int cx, int cy)
    {
        return Cells.ContainsKey(CellKeyFromCoords(cx, cy));
    }

    /// <summary>
    /// Count how many of the 8 neighbors have data
    /// </summary>
    public int CountNeighborsWithData(int cx, int cy)
    {
        var count = 0;
        foreach (var (dx, dy) in Neighbors)
        {
            if (HasDataAt(cx + dx, cy + dy))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Count how many of the 4 cardinal neighbors have data
    /// </summary>
    public int CountCardinalNeighborsWithData(int cx, int cy)
    {
        var count = 0;
        foreach (var (dx, dy) in CardinalNeighbors)
        {
            if (HasDataAt(cx + dx, cy + dy))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Get interpolated height from neighbors for a cell that has no data.
    /// Averages the closest floor matching the NPC's Z from all neighbors that have data.
    /// Returns null if too few neighbors have data (= actual wall, not a gap).
    /// </summary>
    /// <param name="cx">Cell X coordinate</param>
    /// <param name="cy">Cell Y coordinate</param>
    /// <param name="z">NPC's current Z</param>
    /// <param name="tolerance">Max Z above a floor to consider</param>
    /// <param name="minNeighbors">Minimum neighbors with usable data to interpolate (default 3)</param>
    /// <returns>Interpolated floor Z, or null if not enough neighbors</returns>
    public float? InterpolateFromNeighbors(int cx, int cy, float z, float tolerance = DefaultLookupTolerance, int minNeighbors = 3)
    {
        // Single-pass: cache neighbor floor lists while finding the anchor (closest floor to z),
        // then average only floors within ±AnchorBand of the anchor (same storey). The anchor
        // prevents edge cells from dropping the NPC by mixing rooftop Z with ground Z.
        // Small heap alloc (8 refs = ~64 bytes, Gen0 only) trades for 8 saved dict lookups.
        var neighborFloors = new List<float>[Neighbors.Length];
        float anchorZ = 0f;
        var anchorDist = float.MaxValue;
        var hasAnchor = false;

        for (var i = 0; i < Neighbors.Length; i++)
        {
            var (dx, dy) = Neighbors[i];
            if (!Cells.TryGetValue(CellKeyFromCoords(cx + dx, cy + dy), out var floors))
                continue;
            neighborFloors[i] = floors;
            foreach (var floorZ in floors)
            {
                var d = MathF.Abs(floorZ - z);
                if (d <= tolerance && d < anchorDist)
                {
                    anchorZ = floorZ;
                    anchorDist = d;
                    hasAnchor = true;
                }
            }
        }
        if (!hasAnchor)
            return null;

        float sum = 0;
        var count = 0;
        for (var i = 0; i < Neighbors.Length; i++)
        {
            var floors = neighborFloors[i];
            if (floors == null)
                continue;

            var bestFloor = FindBestFloor(floors, anchorZ, AnchorBand);
            if (bestFloor.HasValue)
            {
                sum += bestFloor.Value;
                count++;
            }
        }

        if (count < minNeighbors)
            return null;

        return sum / count;
    }

    // ── Recording ──

    /// <summary>
    /// Record a height at a world position. Adds a new floor if the Z differs
    /// from existing floors by more than the tolerance.
    /// </summary>
    /// <param name="x">World X</param>
    /// <param name="y">World Y</param>
    /// <param name="z">World Z (floor height)</param>
    /// <param name="zTolerance">Minimum Z difference to count as a new floor (default 1.5m)</param>
    /// <returns>True if a new data point was added or updated</returns>
    public bool RecordHeight(float x, float y, float z, float zTolerance = 1.5f)
    {
        var key = CellKey(x, y, CellSize);
        return RecordHeightByKey(key, z, zTolerance);
    }

    /// <summary>
    /// Record a height at a cell key. Shared logic for single and radius recording.
    /// </summary>
    private bool RecordHeightByKey(string key, float z, float zTolerance = 1.5f)
    {
        if (!Cells.TryGetValue(key, out var floors))
        {
            Cells[key] = [z];
            SampleCounts[key] = [1];
            return true;
        }

        if (!SampleCounts.TryGetValue(key, out var counts))
        {
            counts = floors.Select(_ => 1).ToList();
            SampleCounts[key] = counts;
        }

        // Ensure counts list matches floors length
        while (counts.Count < floors.Count) counts.Add(1);

        for (var i = 0; i < floors.Count; i++)
        {
            if (MathF.Abs(floors[i] - z) < zTolerance)
            {
                // Running average: converges to true floor height with more samples.
                // avg_new = (avg_old * n + sample) / (n + 1)
                var n = counts[i];
                floors[i] = (floors[i] * n + z) / (n + 1);
                counts[i] = Math.Min(n + 1, 200); // cap to maintain some plasticity
                return false;
            }
        }

        // New floor — add and bubble-sort into position (list is already sorted)
        floors.Add(z);
        counts.Add(1);
        for (var i = floors.Count - 1; i > 0 && floors[i] < floors[i - 1]; i--)
        {
            (floors[i], floors[i - 1]) = (floors[i - 1], floors[i]);
            (counts[i], counts[i - 1]) = (counts[i - 1], counts[i]);
        }

        return true;
    }

    /// <summary>
    /// Record a height at a position AND fill all cells within a diamond radius.
    /// This simulates the character having physical width — one step fills a wider area.
    /// </summary>
    /// <param name="x">World X</param>
    /// <param name="y">World Y</param>
    /// <param name="z">World Z (floor height)</param>
    /// <param name="radius">Fill radius in cells (0 = single cell, 2 = diamond of ~13 cells)</param>
    /// <param name="zTolerance">Minimum Z difference to count as a new floor</param>
    /// <returns>Number of NEW cells created (not updates)</returns>
    public int RecordWithRadius(float x, float y, float z, int radius, float zTolerance = 1.5f)
    {
        if (radius <= 0)
        {
            return RecordHeight(x, y, z, zTolerance) ? 1 : 0;
        }

        var (cx, cy) = WorldToCell(x, y, CellSize);
        var newCells = 0;

        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                // Diamond shape (Manhattan distance) — better than square for circular buildings
                if (Math.Abs(dx) + Math.Abs(dy) > radius)
                    continue;

                var key = CellKeyFromCoords(cx + dx, cy + dy);
                if (RecordHeightByKey(key, z, zTolerance))
                    newCells++;
            }
        }

        return newCells;
    }

    /// <summary>
    /// Record height with radius fill, adjusting Z for each offset cell based on local slope.
    /// This prevents flat plateaus on ramps/stairs — each radius cell gets a slope-corrected Z
    /// that approximates the actual floor height at that offset position.
    /// </summary>
    /// <param name="x">World X (center)</param>
    /// <param name="y">World Y (center)</param>
    /// <param name="z">World Z (floor height at center)</param>
    /// <param name="radius">Fill radius in cells (0 = single cell)</param>
    /// <param name="dzDx">Z gradient per meter in X direction</param>
    /// <param name="dzDy">Z gradient per meter in Y direction</param>
    /// <param name="zTolerance">Minimum Z difference to count as a new floor</param>
    /// <returns>Number of NEW cells created</returns>
    public int RecordWithRadiusSloped(float x, float y, float z, int radius, float dzDx, float dzDy, float zTolerance = 1.5f)
    {
        if (radius <= 0)
        {
            return RecordHeight(x, y, z, zTolerance) ? 1 : 0;
        }

        var (cx, cy) = WorldToCell(x, y, CellSize);
        var newCells = 0;

        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                if (Math.Abs(dx) + Math.Abs(dy) > radius)
                    continue;

                // Adjust Z based on slope: how much the floor rises/falls at this offset cell
                var adjustedZ = z + dx * CellSize * dzDx + dy * CellSize * dzDy;

                var key = CellKeyFromCoords(cx + dx, cy + dy);
                if (RecordHeightByKey(key, adjustedZ, zTolerance))
                    newCells++;
            }
        }

        return newCells;
    }

    /// <summary>
    /// Record all cells along a line from (x1,y1,z1) to (x2,y2,z2), each with slope-aware radius fill.
    /// Uses Bresenham-style stepping to ensure no cells are skipped between movement steps.
    /// The Z is linearly interpolated along the line (handles stairs).
    /// Computes the local slope gradient so radius cells get accurate Z offsets.
    /// </summary>
    /// <param name="x1">Start world X</param>
    /// <param name="y1">Start world Y</param>
    /// <param name="z1">Start world Z</param>
    /// <param name="x2">End world X</param>
    /// <param name="y2">End world Y</param>
    /// <param name="z2">End world Z</param>
    /// <param name="radius">Fill radius per point</param>
    /// <param name="zTolerance">Minimum Z difference for new floor</param>
    /// <returns>Number of NEW cells created</returns>
    public int RecordLine(float x1, float y1, float z1, float x2, float y2, float z2, int radius, float zTolerance = 1.5f)
    {
        var (cx1, cy1) = WorldToCell(x1, y1, CellSize);
        var (cx2, cy2) = WorldToCell(x2, y2, CellSize);

        var steps = Math.Max(Math.Abs(cx2 - cx1), Math.Abs(cy2 - cy1));
        if (steps == 0)
            return RecordWithRadius(x1, y1, z1, radius, zTolerance);

        // Compute local Z gradient (slope per meter in X and Y direction)
        // so radius cells get slope-corrected Z values instead of flat plateaus
        var dx = x2 - x1;
        var dy = y2 - y1;
        var dz = z2 - z1;
        var distSq = dx * dx + dy * dy;
        var dzDx = 0f;
        var dzDy = 0f;
        if (distSq > 0.0001f)
        {
            // Project slope onto X and Y axes: dZ = dzDx * deltaX + dzDy * deltaY
            dzDx = dz * dx / distSq;
            dzDy = dz * dy / distSq;
        }

        var newCells = 0;
        for (var i = 0; i <= steps; i++)
        {
            var t = (float)i / steps;
            var ix = x1 + (x2 - x1) * t;
            var iy = y1 + (y2 - y1) * t;
            var iz = z1 + (z2 - z1) * t;
            newCells += RecordWithRadiusSloped(ix, iy, iz, radius, dzDx, dzDy, zTolerance);
        }

        return newCells;
    }

    // ── Height queries ──

    /// <summary>
    /// Get the best matching floor height for an NPC at the given position.
    /// Uses bilinear interpolation between the 4 surrounding cells for smooth movement.
    /// Falls back to direct cell lookup if not enough neighbors for interpolation.
    /// </summary>
    /// <param name="x">World X</param>
    /// <param name="y">World Y</param>
    /// <param name="z">NPC's current Z</param>
    /// <param name="tolerance">Symmetric (±) Z window to look for a matching floor. Default 1.5m matches RecordHeight zTolerance.</param>
    /// <returns>Floor Z (smoothly interpolated), or null if no suitable floor found</returns>
    public float? GetBestFloorHeight(float x, float y, float z, float tolerance = DefaultLookupTolerance)
    {
        // Try Catmull-Rom bicubic interpolation first (smoothest, uses 4×4 grid)
        var interpolated = GetBicubicHeight(x, y, z, tolerance);
        if (interpolated.HasValue)
            return interpolated;

        // Fallback: direct cell lookup (edge cells with few neighbors)
        var floors = GetFloorsAt(x, y);
        if (floors != null && floors.Count > 0)
        {
            // Prefer the floor closest to NPC's current Z (avoids floor-switching jumps)
            var direct = FindBestFloor(floors, z, tolerance);
            if (direct.HasValue)
                return direct;
        }

        // Last resort: neighbor average interpolation (for gap cells on stairs/doorways)
        var (cx, cy) = WorldToCell(x, y, CellSize);
        return InterpolateFromNeighbors(cx, cy, z, tolerance);
    }

    /// <summary>
    /// Catmull-Rom bicubic interpolation using a 4×4 grid of surrounding cells.
    /// Produces C1-continuous surface (smooth position AND smooth first derivative).
    /// Uses two-pass floor selection: first determines the reference floor at the NPC's
    /// center cell, then selects the same floor level in all 16 surrounding cells.
    /// This prevents multi-floor confusion (e.g., picking ground floor in one cell
    /// and second floor in another, which causes wild interpolation jumps).
    /// Additionally, cells with low sample counts (radius-extrapolated) are replaced
    /// by the average of high-confidence neighbors to reduce recording noise.
    /// </summary>
    private float? GetBicubicHeight(float x, float y, float z, float tolerance)
    {
        // Fractional position relative to cell centers
        var fx = x / CellSize - 0.5f;
        var fy = y / CellSize - 0.5f;

        var cx = (int)MathF.Floor(fx);
        var cy = (int)MathF.Floor(fy);

        var tx = fx - cx; // 0..1 between cell center cx and cx+1
        var ty = fy - cy;

        // ── Pass 1: Determine reference floor from the cell the NPC is actually in ──
        // This is the "anchor" — all other cells must match THIS floor level.
        var npcCx = (int)MathF.Floor(x / CellSize);
        var npcCy = (int)MathF.Floor(y / CellSize);
        var refFloor = GetCellBestFloor(npcCx, npcCy, z, tolerance);
        if (!refFloor.HasValue)
        {
            // NPC's actual cell has no data — try the 4 inner cells
            refFloor = GetCellBestFloor(cx, cy, z, tolerance)
                    ?? GetCellBestFloor(cx + 1, cy, z, tolerance)
                    ?? GetCellBestFloor(cx, cy + 1, z, tolerance)
                    ?? GetCellBestFloor(cx + 1, cy + 1, z, tolerance);
        }
        if (!refFloor.HasValue)
            return null;

        var refZ = refFloor.Value;

        // ── Pass 2: Sample inner 2×2 cells with confidence gating ──
        // Cells with ≥ minSamples are "high confidence" (directly walked).
        // Cells below that threshold were likely radius-extrapolated and may have
        // inaccurate Z — these get replaced by the average of confident neighbors.
        const int minSamples = 3;

        var (h00, s00) = GetCellFloorNearWithSamples(cx, cy, refZ, AnchorBand);
        var (h10, s10) = GetCellFloorNearWithSamples(cx + 1, cy, refZ, AnchorBand);
        var (h01, s01) = GetCellFloorNearWithSamples(cx, cy + 1, refZ, AnchorBand);
        var (h11, s11) = GetCellFloorNearWithSamples(cx + 1, cy + 1, refZ, AnchorBand);

        var innerCount = (h00.HasValue ? 1 : 0) + (h10.HasValue ? 1 : 0) +
                         (h01.HasValue ? 1 : 0) + (h11.HasValue ? 1 : 0);

        // Need at least 2 inner cells for meaningful interpolation
        if (innerCount < 2)
            return null;

        // Compute confidence-weighted average of inner cells
        // High-confidence cells dominate; low-confidence ones contribute less
        float confSum = 0, confWeightSum = 0;
        if (h00.HasValue) { var w = MathF.Min(s00, 50); confSum += h00.Value * w; confWeightSum += w; }
        if (h10.HasValue) { var w = MathF.Min(s10, 50); confSum += h10.Value * w; confWeightSum += w; }
        if (h01.HasValue) { var w = MathF.Min(s01, 50); confSum += h01.Value * w; confWeightSum += w; }
        if (h11.HasValue) { var w = MathF.Min(s11, 50); confSum += h11.Value * w; confWeightSum += w; }
        var innerAvg = confWeightSum > 0 ? confSum / confWeightSum : refZ;

        // Replace low-confidence or missing cells with the confidence-weighted average
        var v00 = (h00.HasValue && s00 >= minSamples) ? h00.Value : innerAvg;
        var v10 = (h10.HasValue && s10 >= minSamples) ? h10.Value : innerAvg;
        var v01 = (h01.HasValue && s01 >= minSamples) ? h01.Value : innerAvg;
        var v11 = (h11.HasValue && s11 >= minSamples) ? h11.Value : innerAvg;

        // Cliff check — don't interpolate across real storey boundaries.
        var maxDiff = MathF.Max(
            MathF.Max(MathF.Abs(v00 - v10), MathF.Abs(v00 - v01)),
            MathF.Max(MathF.Abs(v11 - v10), MathF.Abs(v11 - v01)));
        if (maxDiff > BicubicCliffMaxStep)
            return null;

        // Build 4 rows: Catmull-Rom in X per row, then interpolate rows in Y.
        // Outer cells use tight tolerance around refZ to stay on the same floor level.
        Span<float> rows = stackalloc float[4];
        for (var j = -1; j <= 2; j++)
        {
            float c0, c1;
            if (j == 0) { c0 = v00; c1 = v10; }
            else if (j == 1) { c0 = v01; c1 = v11; }
            else
            {
                c0 = GetCellFloorNear(cx, cy + j, refZ, AnchorBand) ?? (j < 0 ? v00 : v01);
                c1 = GetCellFloorNear(cx + 1, cy + j, refZ, AnchorBand) ?? (j < 0 ? v10 : v11);
            }

            var left = GetCellFloorNear(cx - 1, cy + j, refZ, AnchorBand) ?? c0;
            var right = GetCellFloorNear(cx + 2, cy + j, refZ, AnchorBand) ?? c1;

            rows[j + 1] = CatmullRom(left, c0, c1, right, tx);
        }

        return CatmullRom(rows[0], rows[1], rows[2], rows[3], ty);
    }

    /// <summary>
    /// Find the floor in a cell closest to a reference Z within a tight band.
    /// Used by bicubic interpolation to ensure all cells pick the SAME floor level.
    /// </summary>
    private float? GetCellFloorNear(int cx, int cy, float refZ, float band)
    {
        var key = CellKeyFromCoords(cx, cy);
        if (!Cells.TryGetValue(key, out var floors) || floors.Count == 0)
            return null;

        float? closest = null;
        var closestDist = float.MaxValue;
        foreach (var floorZ in floors)
        {
            var dist = MathF.Abs(floorZ - refZ);
            if (dist <= band && dist < closestDist)
            {
                closest = floorZ;
                closestDist = dist;
            }
        }
        return closest;
    }

    /// <summary>
    /// Find the floor in a cell closest to a reference Z within a band,
    /// AND return the sample count for that floor (confidence indicator).
    /// Cells with high sample counts were directly walked on; low counts were radius-extrapolated.
    /// </summary>
    private (float? Z, int Samples) GetCellFloorNearWithSamples(int cx, int cy, float refZ, float band)
    {
        var key = CellKeyFromCoords(cx, cy);
        if (!Cells.TryGetValue(key, out var floors) || floors.Count == 0)
            return (null, 0);

        SampleCounts.TryGetValue(key, out var counts);

        float? closest = null;
        var closestDist = float.MaxValue;
        var closestSamples = 0;
        for (var i = 0; i < floors.Count; i++)
        {
            var dist = MathF.Abs(floors[i] - refZ);
            if (dist <= band && dist < closestDist)
            {
                closest = floors[i];
                closestDist = dist;
                closestSamples = (counts != null && i < counts.Count) ? counts[i] : 1;
            }
        }
        return (closest, closestSamples);
    }

    /// <summary>
    /// Catmull-Rom spline evaluation (tension = 0.5, standard uniform).
    /// C1-continuous: interpolated position AND its first derivative are smooth.
    /// p0..p3 are the 4 control points; t (0..1) interpolates between p1 and p2.
    /// </summary>
    private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
    {
        // Horner form: p1 + 0.5t * [(p2-p0) + t * (2p0-5p1+4p2-p3 + t * (-p0+3p1-3p2+p3))]
        return p1 + 0.5f * t * (p2 - p0 +
            t * (2f * p0 - 5f * p1 + 4f * p2 - p3 +
            t * (-p0 + 3f * p1 - 3f * p2 + p3)));
    }

    /// <summary>
    /// Get the best floor from a specific cell by integer coordinates.
    /// </summary>
    private float? GetCellBestFloor(int cx, int cy, float z, float tolerance)
    {
        var key = CellKeyFromCoords(cx, cy);
        if (!Cells.TryGetValue(key, out var floors) || floors.Count == 0)
            return null;
        return FindBestFloor(floors, z, tolerance);
    }

    /// <summary>
    /// Check if a position is walkable: has data directly OR has enough neighbors
    /// to interpolate (= gap, not a real wall).
    /// </summary>
    public bool IsWalkable(float x, float y)
    {
        if (HasData(x, y))
            return true;

        // Check if this is a small gap (enough neighbors = walkable)
        var (cx, cy) = WorldToCell(x, y, CellSize);
        return CountCardinalNeighborsWithData(cx, cy) >= 3;
    }

    /// <summary>
    /// Check if a position is a real wall: no data AND not enough neighbors to interpolate.
    /// Only meaningful when checked FROM a cell that IS on the grid.
    /// </summary>
    public bool IsWall(float toX, float toY)
    {
        if (HasData(toX, toY))
            return false;

        var (cx, cy) = WorldToCell(toX, toY, CellSize);
        // With 3+ cardinal neighbors it's a gap (stair/threshold), not a wall
        return CountCardinalNeighborsWithData(cx, cy) < 3;
    }

    /// <summary>
    /// Find the best floor from a list of Z values within a SYMMETRIC tolerance window
    /// (|floorZ - z| ≤ tolerance). Picks the floor closest to the reference Z.
    /// Symmetric clamping is critical to prevent NPCs from dropping to a much lower floor
    /// when they drift slightly above the current one (ground floor often within +tolerance
    /// of an upper floor's reference even though it's tens of meters below).
    /// </summary>
    private static float? FindBestFloor(List<float> floors, float z, float tolerance)
    {
        float? best = null;
        var bestDist = float.MaxValue;
        foreach (var floorZ in floors)
        {
            var dist = MathF.Abs(floorZ - z);
            if (dist <= tolerance && dist < bestDist)
            {
                best = floorZ;
                bestDist = dist;
            }
        }
        return best;
    }

    // ── Post-processing ──

    /// <summary>
    /// Fill small gaps in the grid — multi-pass with configurable neighbor threshold.
    /// Each pass fills empty cells that have enough cardinal neighbors with data.
    /// Runs multiple passes so newly filled cells allow further filling outward.
    /// Should be called after a scan session (before save).
    /// </summary>
    /// <param name="minCardinalNeighbors">Minimum cardinal neighbors with data to fill a gap (default 2)</param>
    /// <param name="maxPasses">Maximum number of fill passes (default 5)</param>
    /// <returns>Total number of cells filled across all passes</returns>
    public int FillGaps(int minCardinalNeighbors = 2, int maxPasses = 5)
    {
        var totalFilled = 0;

        for (var pass = 0; pass < maxPasses; pass++)
        {
            var filled = FillGapsSinglePass(minCardinalNeighbors);
            if (filled == 0)
                break;
            totalFilled += filled;
        }

        return totalFilled;
    }

    /// <summary>
    /// Single pass of gap filling. Returns number of cells filled in this pass.
    /// </summary>
    private int FillGapsSinglePass(int minCardinalNeighbors)
    {
        var cellsToFill = new List<(int CX, int CY, float Z)>();

        // Get all existing cell coordinates
        var existingCoords = new HashSet<(int, int)>();
        foreach (var key in Cells.Keys)
        {
            var (cx, cy) = ParseCellKey(key);
            existingCoords.Add((cx, cy));
        }

        // For each existing cell, check all 8 neighbors
        var checkedEmpty = new HashSet<(int, int)>();
        foreach (var (cx, cy) in existingCoords)
        {
            foreach (var (dx, dy) in Neighbors)
            {
                var nx = cx + dx;
                var ny = cy + dy;

                // Skip if already has data or already checked
                if (existingCoords.Contains((nx, ny)) || !checkedEmpty.Add((nx, ny)))
                    continue;

                // Count how many of this empty cell's cardinal neighbors have data
                var cardinalCount = 0;
                float zSum = 0;
                var zCount = 0;

                foreach (var (cdx, cdy) in CardinalNeighbors)
                {
                    var nnKey = CellKeyFromCoords(nx + cdx, ny + cdy);
                    if (Cells.TryGetValue(nnKey, out var neighborFloors) && neighborFloors.Count > 0)
                    {
                        cardinalCount++;
                        zSum += neighborFloors[0];
                        zCount++;
                    }
                }

                // Fill if enough cardinal neighbors have data
                if (cardinalCount >= minCardinalNeighbors && zCount > 0)
                {
                    cellsToFill.Add((nx, ny, zSum / zCount));
                }
            }
        }

        // Apply fills
        foreach (var (cx, cy, z) in cellsToFill)
        {
            var key = CellKeyFromCoords(cx, cy);
            if (!Cells.ContainsKey(key))
            {
                Cells[key] = [z];
                SampleCounts[key] = [1];
            }
        }

        return cellsToFill.Count;
    }
}
