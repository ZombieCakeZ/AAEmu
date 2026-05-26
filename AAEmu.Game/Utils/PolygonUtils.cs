using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AAEmu.Game.Utils;

/// <summary>
/// Provides 2D polygon utilities: ear-clipping triangulation, point-in-polygon test,
/// and barycentric height interpolation for collision volumes.
/// All operations work in XY plane using Z for height interpolation.
/// </summary>
public static class PolygonUtils
{
    /// <summary>
    /// Triangulate a simple polygon using the ear-clipping algorithm.
    /// Returns a list of triangle index triplets (A, B, C) referencing the input vertices.
    /// Works on the XY projection (Z is ignored for triangulation).
    /// </summary>
    public static List<(int A, int B, int C)> EarClipTriangulate(IList<Vector3> vertices)
    {
        var triangles = new List<(int A, int B, int C)>();
        var n = vertices.Count;
        if (n < 3)
            return triangles;

        // Create an index list that we'll shrink as we clip ears
        var indices = new List<int>(n);
        for (var i = 0; i < n; i++)
            indices.Add(i);

        // Ensure consistent winding (CCW in XY plane)
        if (SignedArea2D(vertices) < 0)
            indices.Reverse();

        var failSafe = 0;
        var maxIterations = n * n; // prevent infinite loops on degenerate input

        while (indices.Count > 2 && failSafe < maxIterations)
        {
            var earFound = false;

            for (var i = 0; i < indices.Count; i++)
            {
                var prevIdx = indices[(i - 1 + indices.Count) % indices.Count];
                var currIdx = indices[i];
                var nextIdx = indices[(i + 1) % indices.Count];

                var a = vertices[prevIdx];
                var b = vertices[currIdx];
                var c = vertices[nextIdx];

                // Check if this vertex forms a convex angle
                if (!IsConvex2D(a, b, c))
                {
                    failSafe++;
                    continue;
                }

                // Check that no other vertex lies inside the triangle
                var isEar = true;
                for (var j = 0; j < indices.Count; j++)
                {
                    var testIdx = indices[j];
                    if (testIdx == prevIdx || testIdx == currIdx || testIdx == nextIdx)
                        continue;

                    if (PointInTriangle2D(vertices[testIdx], a, b, c))
                    {
                        isEar = false;
                        break;
                    }
                }

                if (isEar)
                {
                    triangles.Add((prevIdx, currIdx, nextIdx));
                    indices.RemoveAt(i);
                    earFound = true;
                    break;
                }

                failSafe++;
            }

            if (!earFound)
                break; // degenerate polygon, can't clip more ears
        }

        return triangles;
    }

    /// <summary>
    /// Test if point P is inside a polygon (2D, XY plane) using ray-casting.
    /// </summary>
    public static bool PointInPolygon2D(float px, float py, IList<Vector3> polygon)
    {
        var inside = false;
        var n = polygon.Count;

        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var xi = polygon[i].X;
            var yi = polygon[i].Y;
            var xj = polygon[j].X;
            var yj = polygon[j].Y;

            if (((yi > py) != (yj > py)) &&
                (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Test if point P is inside triangle (a, b, c) in 2D (XY plane).
    /// Uses barycentric coordinate method.
    /// </summary>
    public static bool PointInTriangle2D(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        return PointInTriangle2D(p.X, p.Y, a, b, c);
    }

    /// <summary>
    /// Test if point (px, py) is inside triangle (a, b, c) in 2D.
    /// </summary>
    public static bool PointInTriangle2D(float px, float py, Vector3 a, Vector3 b, Vector3 c)
    {
        var denom = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(denom) < 1e-10f)
            return false; // degenerate triangle

        var u = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / denom;
        var v = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / denom;
        var w = 1f - u - v;

        return u >= -1e-6f && v >= -1e-6f && w >= -1e-6f;
    }

    /// <summary>
    /// Interpolate Z height at point (px, py) within triangle (a, b, c)
    /// using barycentric coordinates.
    /// </summary>
    public static float InterpolateHeight(float px, float py, Vector3 a, Vector3 b, Vector3 c)
    {
        var denom = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(denom) < 1e-10f)
            return (a.Z + b.Z + c.Z) / 3f; // degenerate, return average

        var u = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / denom;
        var v = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / denom;
        var w = 1f - u - v;

        return u * a.Z + v * b.Z + w * c.Z;
    }

    /// <summary>
    /// Compute the 2D signed area of a polygon (XY plane).
    /// Positive = CCW, Negative = CW.
    /// </summary>
    private static float SignedArea2D(IList<Vector3> vertices)
    {
        var area = 0f;
        var n = vertices.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            area += (vertices[j].X - vertices[i].X) * (vertices[j].Y + vertices[i].Y);
        }
        return area * 0.5f;
    }

    /// <summary>
    /// Check if angle at B is convex (CCW winding assumed) in 2D XY plane.
    /// </summary>
    private static bool IsConvex2D(Vector3 a, Vector3 b, Vector3 c)
    {
        // Cross product of (B-A) x (C-B) — if positive, B is convex (CCW)
        return (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) > 0;
    }

    /// <summary>
    /// Test if point (px, py) is inside an oriented box defined by center, half-extents, and rotation.
    /// </summary>
    public static bool PointInOBB2D(float px, float py, Vector3 center, Vector3 halfExtents, float rotationDeg)
    {
        var rad = rotationDeg * MathF.PI / 180f;
        var cosR = MathF.Cos(-rad);
        var sinR = MathF.Sin(-rad);

        // Transform point into box-local space
        var dx = px - center.X;
        var dy = py - center.Y;
        var localX = dx * cosR - dy * sinR;
        var localY = dx * sinR + dy * cosR;

        return MathF.Abs(localX) <= halfExtents.X && MathF.Abs(localY) <= halfExtents.Y;
    }

    /// <summary>
    /// Calculate bounding box for a list of vertices.
    /// </summary>
    public static (float MinX, float MinY, float MaxX, float MaxY) CalcBoundingBox(IList<Vector3> vertices)
    {
        if (vertices == null || vertices.Count == 0)
            return (0, 0, 0, 0);

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        foreach (var v in vertices)
        {
            if (v.X < minX) minX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.X > maxX) maxX = v.X;
            if (v.Y > maxY) maxY = v.Y;
        }

        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Calculate bounding box for a rotated box.
    /// </summary>
    public static (float MinX, float MinY, float MaxX, float MaxY) CalcBoxBoundingBox(Vector3 center, Vector3 halfExtents, float rotationDeg)
    {
        var rad = rotationDeg * MathF.PI / 180f;
        var cosR = MathF.Cos(rad);
        var sinR = MathF.Sin(rad);

        // Calculate the four corners
        var corners = new Vector3[4];
        corners[0] = new Vector3(center.X + halfExtents.X * cosR - halfExtents.Y * sinR,
                                  center.Y + halfExtents.X * sinR + halfExtents.Y * cosR, center.Z);
        corners[1] = new Vector3(center.X - halfExtents.X * cosR - halfExtents.Y * sinR,
                                  center.Y - halfExtents.X * sinR + halfExtents.Y * cosR, center.Z);
        corners[2] = new Vector3(center.X - halfExtents.X * cosR + halfExtents.Y * sinR,
                                  center.Y - halfExtents.X * sinR - halfExtents.Y * cosR, center.Z);
        corners[3] = new Vector3(center.X + halfExtents.X * cosR + halfExtents.Y * sinR,
                                  center.Y + halfExtents.X * sinR - halfExtents.Y * cosR, center.Z);

        return CalcBoundingBox(corners);
    }

    // ─────────────────────────────────────────────────────────
    // Wall collision utilities
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Test if two 2D line segments (P1→P2 and P3→P4) intersect.
    /// Returns true if they cross (proper or endpoint intersection).
    /// Uses the cross-product orientation method.
    /// </summary>
    public static bool SegmentsIntersect2D(
        float p1X, float p1Y, float p2X, float p2Y,
        float p3X, float p3Y, float p4X, float p4Y)
    {
        var d1X = p2X - p1X;
        var d1Y = p2Y - p1Y;
        var d2X = p4X - p3X;
        var d2Y = p4Y - p3Y;

        var denom = d1X * d2Y - d1Y * d2X;

        // Parallel lines — treat as non-intersecting for wall purposes
        if (MathF.Abs(denom) < 1e-8f)
            return false;

        var t = ((p3X - p1X) * d2Y - (p3Y - p1Y) * d2X) / denom;
        var u = ((p3X - p1X) * d1Y - (p3Y - p1Y) * d1X) / denom;

        return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
    }

    /// <summary>
    /// Calculate the minimum distance from a point (px, py) to a line segment (ax, ay)→(bx, by) in 2D.
    /// </summary>
    public static float PointToSegmentDistance2D(float px, float py, float ax, float ay, float bx, float by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lenSq = dx * dx + dy * dy;

        if (lenSq < 1e-8f)
            return MathF.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

        var t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
        t = Math.Clamp(t, 0f, 1f);

        var projX = ax + t * dx;
        var projY = ay + t * dy;

        return MathF.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
    }

    // ─────────────────────────────────────────────────────────
    // Convex Hull & Floor Scanner utilities
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Compute the 2D Convex Hull of a set of points using the Andrew's Monotone Chain algorithm.
    /// Returns the hull vertices in counterclockwise order. Z is averaged per hull vertex from nearby points.
    /// </summary>
    public static List<Vector3> ConvexHull2D(IList<Vector3> points)
    {
        if (points.Count < 3)
            return new List<Vector3>(points);

        // Sort by X, then by Y
        var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();

        var hull = new List<Vector3>();

        // Build lower hull
        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && Cross2D(hull[^2], hull[^1], p) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        // Build upper hull
        var lowerCount = hull.Count + 1;
        for (var i = sorted.Count - 2; i >= 0; i--)
        {
            var p = sorted[i];
            while (hull.Count >= lowerCount && Cross2D(hull[^2], hull[^1], p) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1); // Remove last (duplicate of first)
        return hull;
    }

    /// <summary>
    /// 2D cross product for convex hull: (B-A) × (C-A)
    /// Positive = counterclockwise, Negative = clockwise, Zero = collinear
    /// </summary>
    private static float Cross2D(Vector3 a, Vector3 b, Vector3 c)
    {
        return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    }

    /// <summary>
    /// Cluster a point cloud into floor groups by Z-height.
    /// Points within zTolerance of each other vertically are considered the same floor.
    /// Returns a list of clusters, each sorted by ascending average Z.
    /// </summary>
    /// <param name="points">All scanned positions</param>
    /// <param name="zTolerance">Maximum Z difference to be considered same floor (default 1.5m)</param>
    public static List<List<Vector3>> ClusterByFloor(IList<Vector3> points, float zTolerance = 1.5f)
    {
        if (points.Count == 0)
            return [];

        // Sort by Z
        var sorted = points.OrderBy(p => p.Z).ToList();
        var clusters = new List<List<Vector3>>();
        var current = new List<Vector3> { sorted[0] };
        var currentAvgZ = sorted[0].Z;

        for (var i = 1; i < sorted.Count; i++)
        {
            if (MathF.Abs(sorted[i].Z - currentAvgZ) <= zTolerance)
            {
                current.Add(sorted[i]);
                // Update running average Z for this cluster
                currentAvgZ = current.Average(p => p.Z);
            }
            else
            {
                clusters.Add(current);
                current = [sorted[i]];
                currentAvgZ = sorted[i].Z;
            }
        }

        if (current.Count > 0)
            clusters.Add(current);

        // Sort clusters by average Z (lowest floor first)
        clusters.Sort((a, b) => a.Average(p => p.Z).CompareTo(b.Average(p => p.Z)));
        return clusters;
    }

    /// <summary>
    /// Douglas-Peucker path simplification: reduces a path to its essential corners.
    /// Removes points that are closer than `epsilon` to the line between their neighbors.
    /// Preserves first and last point. Works in 2D (XY plane), keeps original Z values.
    /// </summary>
    /// <param name="path">Ordered list of path points (open polyline)</param>
    /// <param name="epsilon">Maximum perpendicular distance (meters) to consider a point redundant.
    /// Smaller = more detail preserved. 0.3m is good for walls.</param>
    public static List<Vector3> SimplifyPath(IList<Vector3> path, float epsilon = 0.3f)
    {
        if (path.Count <= 2)
            return new List<Vector3>(path);

        // Find the point with the greatest distance from the line (start→end)
        var startPt = path[0];
        var endPt = path[^1];
        var maxDist = 0f;
        var maxIdx = 0;

        for (var i = 1; i < path.Count - 1; i++)
        {
            var dist = PerpendicularDistance2D(path[i], startPt, endPt);
            if (dist > maxDist)
            {
                maxDist = dist;
                maxIdx = i;
            }
        }

        if (maxDist > epsilon)
        {
            // Recurse on both halves
            var left = SimplifyPath(path.Take(maxIdx + 1).ToList(), epsilon);
            var right = SimplifyPath(path.Skip(maxIdx).ToList(), epsilon);

            // Merge (skip duplicate middle point)
            var result = new List<Vector3>(left);
            result.AddRange(right.Skip(1));
            return result;
        }

        // All points are within epsilon — just keep start and end
        return [startPt, endPt];
    }

    /// <summary>
    /// Perpendicular distance from a point to a line segment (2D, XY plane).
    /// </summary>
    private static float PerpendicularDistance2D(Vector3 point, Vector3 lineStart, Vector3 lineEnd)
    {
        var dx = lineEnd.X - lineStart.X;
        var dy = lineEnd.Y - lineStart.Y;
        var lengthSq = dx * dx + dy * dy;

        if (lengthSq < 1e-10f)
            return Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(lineStart.X, lineStart.Y));

        // Area of triangle = |cross product| / 2, distance = area * 2 / base
        var cross = MathF.Abs(dx * (lineStart.Y - point.Y) - (lineStart.X - point.X) * dy);
        return cross / MathF.Sqrt(lengthSq);
    }

    /// <summary>
    /// Simplify a convex hull by removing vertices that are nearly collinear.
    /// This reduces polygon complexity while preserving the overall shape.
    /// </summary>
    /// <param name="hull">Input convex hull vertices</param>
    /// <param name="angleTolerance">Minimum angle (degrees) to keep a vertex. Vertices forming
    /// angles closer to 180° than this tolerance are removed.</param>
    public static List<Vector3> SimplifyHull(IList<Vector3> hull, float angleTolerance = 5f)
    {
        if (hull.Count <= 4)
            return new List<Vector3>(hull);

        var result = new List<Vector3>(hull);
        var cosThreshold = MathF.Cos((180f - angleTolerance) * MathF.PI / 180f);

        var changed = true;
        while (changed && result.Count > 4)
        {
            changed = false;
            for (var i = result.Count - 1; i >= 0 && result.Count > 4; i--)
            {
                var prev = result[(i - 1 + result.Count) % result.Count];
                var curr = result[i];
                var next = result[(i + 1) % result.Count];

                // Calculate angle at current vertex
                var d1 = Vector3.Normalize(new Vector3(prev.X - curr.X, prev.Y - curr.Y, 0));
                var d2 = Vector3.Normalize(new Vector3(next.X - curr.X, next.Y - curr.Y, 0));
                var dot = d1.X * d2.X + d1.Y * d2.Y;

                // If nearly collinear (angle close to 180°), remove this vertex
                if (dot < cosThreshold)
                {
                    result.RemoveAt(i);
                    changed = true;
                }
            }
        }

        return result;
    }
}
