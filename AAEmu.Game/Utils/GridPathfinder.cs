using System;
using System.Collections.Generic;
using System.Numerics;

using AAEmu.Game.Core.Managers.World;

namespace AAEmu.Game.Utils;

/// <summary>
/// A* grid-based pathfinder that navigates around Wall collision volumes.
/// Creates a temporary grid between start and target, marks wall-blocked cells,
/// then runs A* to find a walkable path.
/// </summary>
public static class GridPathfinder
{
    /// <summary>
    /// Cell size for the pathfinding grid (meters). 1.0m balances precision and performance.
    /// Smaller values create exponentially more cells and IsBlockedByWall calls.
    /// </summary>
    private const float CellSize = 1.0f;

    /// <summary>
    /// Extra margin around the bounding box of start→target (meters).
    /// Allows paths that go around obstacles outside the direct line.
    /// </summary>
    private const float SearchMargin = 10f;

    /// <summary>
    /// Maximum grid dimension (cells per axis) to cap memory/CPU usage.
    /// </summary>
    private const int MaxGridSize = 200;

    /// <summary>
    /// Maximum A* iterations before giving up (prevents lag on impossible paths).
    /// Lower values trade path quality for guaranteed low latency.
    /// </summary>
    private const int MaxIterations = 5000;

    /// <summary>
    /// NPC body half-width (meters). Used in string-pulling to ensure the smoothed path
    /// keeps enough clearance from walls. NOT used during A* expansion to keep it fast.
    /// Must be >= the runtime MoveTowards body buffer (0.3m) to prevent clipping.
    /// </summary>
    private const float BodyBuffer = 0.5f;

    /// <summary>
    /// Global rate limiter: minimum milliseconds between pathfinding computations.
    /// Prevents multiple NPCs from pathfinding in the same game tick, which would
    /// freeze the entire server. Only one A* runs per interval; others get empty paths
    /// and retry next tick.
    /// </summary>
    private const double PathfindCooldownMs = 50.0;
    private static DateTime _lastPathfindTime = DateTime.MinValue;

    // 8-direction movement offsets (N, NE, E, SE, S, SW, W, NW)
    private static readonly (int DX, int DY, float Cost)[] Directions =
    [
        (0, 1, 1.0f), (1, 1, 1.414f), (1, 0, 1.0f), (1, -1, 1.414f),
        (0, -1, 1.0f), (-1, -1, 1.414f), (-1, 0, 1.0f), (-1, 1, 1.414f)
    ];

    /// <summary>
    /// Find a path from start to target that avoids Wall volumes.
    /// Returns an empty list if no path is found or if the direct path is clear.
    /// </summary>
    /// <param name="worldName">World name for wall volume lookup</param>
    /// <param name="start">Start position (world coordinates)</param>
    /// <param name="target">Target position (world coordinates)</param>
    /// <param name="z">NPC Z height (for wall height checks)</param>
    /// <returns>List of waypoints (world coordinates), empty if direct path is clear or no path found</returns>
    public static List<Vector3> FindPath(string worldName, Vector3 start, Vector3 target, float z)
    {
        return FindPathWithMargin(worldName, start, target, z, SearchMargin);
    }

    /// <summary>
    /// Find a path with a custom search margin. Larger margins allow searching wider
    /// around obstacles, useful as a retry when the default margin fails.
    /// </summary>
    public static List<Vector3> FindPathWithMargin(string worldName, Vector3 start, Vector3 target, float z, float margin)
    {
        // Global rate limiter — prevent multiple NPCs from pathfinding in the same tick
        var now = DateTime.UtcNow;
        if ((now - _lastPathfindTime).TotalMilliseconds < PathfindCooldownMs)
            return []; // Rate limited — another NPC pathfound recently, retry next tick
        _lastPathfindTime = now;

        var mgr = CollisionVolumeManager.Instance;

        // Quick check: if direct path has no wall → no pathing needed
        if (!mgr.IsBlockedByWall(worldName, start.X, start.Y, target.X, target.Y, z))
            return [];

        // Calculate grid bounds
        var minX = MathF.Min(start.X, target.X) - margin;
        var minY = MathF.Min(start.Y, target.Y) - margin;
        var maxX = MathF.Max(start.X, target.X) + margin;
        var maxY = MathF.Max(start.Y, target.Y) + margin;

        var gridW = (int)MathF.Ceiling((maxX - minX) / CellSize);
        var gridH = (int)MathF.Ceiling((maxY - minY) / CellSize);

        // Cap grid size
        if (gridW > MaxGridSize || gridH > MaxGridSize)
            return []; // Too far, give up

        // Convert world positions to grid coordinates
        var startGX = (int)((start.X - minX) / CellSize);
        var startGY = (int)((start.Y - minY) / CellSize);
        var targetGX = (int)((target.X - minX) / CellSize);
        var targetGY = (int)((target.Y - minY) / CellSize);

        startGX = Math.Clamp(startGX, 0, gridW - 1);
        startGY = Math.Clamp(startGY, 0, gridH - 1);
        targetGX = Math.Clamp(targetGX, 0, gridW - 1);
        targetGY = Math.Clamp(targetGY, 0, gridH - 1);

        // Build blocked-cell set by checking each cell's center against wall volumes
        // We check if movement from cell center to each neighbor's center is blocked by a wall
        // This is done lazily during A* to avoid pre-computing the entire grid

        // A* data structures
        var gScore = new Dictionary<int, float>();
        var fScore = new Dictionary<int, float>();
        var cameFrom = new Dictionary<int, int>();
        var openSet = new SortedSet<(float F, int Key)>();
        var closedSet = new HashSet<int>();

        int CellKey(int gx, int gy) => gy * gridW + gx;

        float Heuristic(int gx, int gy)
        {
            var dx = MathF.Abs(gx - targetGX);
            var dy = MathF.Abs(gy - targetGY);
            // Octile distance (8-direction)
            return MathF.Max(dx, dy) + 0.414f * MathF.Min(dx, dy);
        }

        (float WX, float WY) GridToWorld(int gx, int gy)
        {
            return (minX + (gx + 0.5f) * CellSize, minY + (gy + 0.5f) * CellSize);
        }

        var startKey = CellKey(startGX, startGY);
        var targetKey = CellKey(targetGX, targetGY);

        gScore[startKey] = 0;
        var startH = Heuristic(startGX, startGY);
        fScore[startKey] = startH;
        openSet.Add((startH, startKey));

        var iterations = 0;

        while (openSet.Count > 0 && iterations++ < MaxIterations)
        {
            // Get lowest fScore node
            var (_, currentKey) = openSet.Min;
            openSet.Remove(openSet.Min);

            if (currentKey == targetKey)
            {
                // Reconstruct path
                return ReconstructPath(cameFrom, currentKey, gridW, minX, minY, z, start, target, worldName);
            }

            if (!closedSet.Add(currentKey))
                continue;

            var currentGX = currentKey % gridW;
            var currentGY = currentKey / gridW;
            var (cwx, cwy) = GridToWorld(currentGX, currentGY);

            foreach (var (dx, dy, moveCost) in Directions)
            {
                var nx = currentGX + dx;
                var ny = currentGY + dy;

                if (nx < 0 || nx >= gridW || ny < 0 || ny >= gridH)
                    continue;

                var neighborKey = CellKey(nx, ny);
                if (closedSet.Contains(neighborKey))
                    continue;

                // Diagonal corner-cut prevention: for diagonal moves, both adjacent
                // cardinal cells must also be passable. This prevents NPCs from
                // squeezing through corners where two walls meet at 90°.
                if (dx != 0 && dy != 0)
                {
                    var (cardX1, cardY1) = GridToWorld(currentGX + dx, currentGY);
                    var (cardX2, cardY2) = GridToWorld(currentGX, currentGY + dy);
                    if (mgr.IsBlockedByWall(worldName, cwx, cwy, cardX1, cardY1, z) ||
                        mgr.IsBlockedByWall(worldName, cwx, cwy, cardX2, cardY2, z))
                        continue; // Would cut a corner
                }

                // Check if movement from current cell to neighbor is blocked by a wall
                var (nwx, nwy) = GridToWorld(nx, ny);
                if (mgr.IsBlockedByWall(worldName, cwx, cwy, nwx, nwy, z))
                    continue; // Wall blocks this edge

                // NOTE: Body buffer is NOT checked during A* expansion for performance.
                // With 1.0m cells, each expansion checks 8 neighbors × 1-3 IsBlockedByWall calls.
                // Adding body buffer would triple this (3-5 calls per neighbor × 5000 iterations = huge).
                // Instead, body buffer is applied during string-pulling (SmoothPath/HasWallBetween),
                // which runs once on the final ~10-30 waypoint path. This keeps A* fast while
                // ensuring the smoothed path has proper wall clearance.

                var tentativeG = gScore.GetValueOrDefault(currentKey, float.MaxValue) + moveCost;
                var existingG = gScore.GetValueOrDefault(neighborKey, float.MaxValue);

                if (tentativeG < existingG)
                {
                    cameFrom[neighborKey] = currentKey;
                    gScore[neighborKey] = tentativeG;
                    var f = tentativeG + Heuristic(nx, ny);
                    fScore[neighborKey] = f;
                    openSet.Add((f, neighborKey));
                }
            }
        }

        // No path found
        return [];
    }

    /// <summary>
    /// Reconstruct and smooth the path from A* result.
    /// </summary>
    private static List<Vector3> ReconstructPath(
        Dictionary<int, int> cameFrom, int targetKey, int gridW,
        float minX, float minY, float z,
        Vector3 start, Vector3 target, string worldName)
    {
        var rawPath = new List<(int GX, int GY)>();
        var current = targetKey;

        while (cameFrom.ContainsKey(current))
        {
            var gx = current % gridW;
            var gy = current / gridW;
            rawPath.Add((gx, gy));
            current = cameFrom[current];
        }

        // Add start cell
        rawPath.Add((current % gridW, current / gridW));
        rawPath.Reverse();

        // Convert to world coordinates
        var worldPath = new List<Vector3>(rawPath.Count);
        foreach (var (gx, gy) in rawPath)
        {
            var wx = minX + (gx + 0.5f) * CellSize;
            var wy = minY + (gy + 0.5f) * CellSize;
            worldPath.Add(new Vector3(wx, wy, z));
        }

        // Smooth path: remove intermediate points that have line-of-sight
        var smoothed = SmoothPath(worldPath, CollisionVolumeManager.Instance, start, target, z, worldName);

        return smoothed;
    }

    /// <summary>
    /// Simplify path by removing waypoints that are visible from earlier points (string-pulling).
    /// Also replaces first/last points with exact start/target positions.
    /// Uses actual result positions for visibility checks to prevent wall-clipping shortcuts.
    /// </summary>
    private static List<Vector3> SmoothPath(
        List<Vector3> path, CollisionVolumeManager mgr,
        Vector3 start, Vector3 target, float z, string worldName)
    {
        if (path.Count <= 2)
        {
            // Just start → target with one midpoint
            if (path.Count == 2)
                return [start, target];
            return [start];
        }

        // Replace first and last with exact positions for correct visibility checks
        path[0] = start;
        path[^1] = target;

        var result = new List<Vector3> { start };
        var currentIndex = 0;

        while (currentIndex < path.Count - 1)
        {
            // Find the farthest visible point from the ACTUAL current result position
            var currentPos = path[currentIndex];
            var farthest = currentIndex + 1;
            for (var i = path.Count - 1; i > currentIndex + 1; i--)
            {
                // Check line-of-sight from actual position (not grid center)
                if (!HasWallBetween(mgr, currentPos, path[i], z, worldName))
                {
                    farthest = i;
                    break;
                }
            }

            result.Add(path[farthest]);
            currentIndex = farthest;
        }

        return result;
    }

    /// <summary>
    /// Check if there's a wall between two points by sampling along the line.
    /// More thorough than a single IsBlockedByWall call for diagonal movement.
    /// </summary>
    private static bool HasWallBetween(CollisionVolumeManager mgr, Vector3 a, Vector3 b, float z, string worldName)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        // Sample every 0.5m for more precise wall detection
        var sampleDist = CellSize * 0.5f;
        var steps = (int)MathF.Ceiling(dist / sampleDist);
        if (steps <= 0) return false;

        // Check center line
        for (var i = 0; i < steps; i++)
        {
            var t0 = (float)i / steps;
            var t1 = (float)(i + 1) / steps;
            var x0 = a.X + dx * t0;
            var y0 = a.Y + dy * t0;
            var x1 = a.X + dx * t1;
            var y1 = a.Y + dy * t1;

            if (mgr.IsBlockedByWall(worldName, x0, y0, x1, y1, z))
                return true;
        }

        // Also check body-buffer offset lines (NPC has width)
        if (BodyBuffer > 0 && dist > 0.001f)
        {
            var perpX = -dy / dist * BodyBuffer;
            var perpY = dx / dist * BodyBuffer;

            // Check left offset
            for (var i = 0; i < steps; i++)
            {
                var t0 = (float)i / steps;
                var t1 = (float)(i + 1) / steps;
                if (mgr.IsBlockedByWall(worldName,
                        a.X + dx * t0 + perpX, a.Y + dy * t0 + perpY,
                        a.X + dx * t1 + perpX, a.Y + dy * t1 + perpY, z))
                    return true;
            }

            // Check right offset
            for (var i = 0; i < steps; i++)
            {
                var t0 = (float)i / steps;
                var t1 = (float)(i + 1) / steps;
                if (mgr.IsBlockedByWall(worldName,
                        a.X + dx * t0 - perpX, a.Y + dy * t0 - perpY,
                        a.X + dx * t1 - perpX, a.Y + dy * t1 - perpY, z))
                    return true;
            }
        }

        return false;
    }
}
