using System.Numerics;
using System.Runtime.InteropServices;

namespace AAEmu.Game.Models.Game.World;

/// <summary>
/// BVH node — byte-exact mirror of the editor's BvhBuilder.BvhNode and the layout
/// written by AAEditor.Core.Collision.MeshCacheWriter (32 bytes, little-endian).
///
/// Leaf marker: RightOrTriCount &lt; 0 — TriCount = -RightOrTriCount, FirstTri = LeftOrFirstTri.
/// Internal:    LeftOrFirstTri = left child node index, RightOrTriCount = right child node index.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BvhNode
{
    public Vector3 Min;
    public Vector3 Max;
    public int LeftOrFirstTri;
    public int RightOrTriCount;

    public bool IsLeaf => RightOrTriCount < 0;
    public int FirstTri => LeftOrFirstTri;
    public int TriCount => -RightOrTriCount;
}

/// <summary>
/// A collision mesh loaded from a binary .mesh file (extracted from CGF by PakExplorer).
/// Contains triangle data for precise server-side collision detection.
/// </summary>
public class CollisionMesh
{
    /// <summary>
    /// Mesh file name (e.g. "tree_oak_A3B2C1D0.mesh")
    /// </summary>
    public string FileName { get; set; } = "";

    /// <summary>
    /// Vertex positions in local/object space.
    /// Flat array: [x0, y0, z0, x1, y1, z1, ...]
    /// </summary>
    public float[] Vertices { get; set; } = [];

    /// <summary>
    /// Triangle indices into the Vertices array.
    /// Every 3 consecutive values form one triangle: [i0, i1, i2, i3, i4, i5, ...]
    /// Index values reference vertex index (multiply by 3 to get array offset).
    /// </summary>
    public int[] Indices { get; set; } = [];

    /// <summary>
    /// Local-space AABB for quick rejection before triangle tests.
    /// </summary>
    public Vector3 LocalMin { get; set; }
    public Vector3 LocalMax { get; set; }

    /// <summary>
    /// Whether this mesh represents a tree/vegetation object (identified by asset path regex).
    /// When true, only triangles in the bottom 30% of mesh height are used for height queries
    /// (trunk footprint, excluding canopy).
    /// </summary>
    public bool IsTree { get; set; }

    /// <summary>
    /// Whether this mesh represents a rock/stone/cliff object.
    /// Full mesh geometry is used for height queries.
    /// </summary>
    public bool IsRock { get; set; }

    /// <summary>
    /// For tree meshes: local-space Z cutoff for the trunk portion.
    /// Only triangles with ALL vertices at or below this Z are used for height queries.
    /// Computed as LocalMin.Z + (LocalMax.Z - LocalMin.Z) * 0.30 (bottom 30%).
    /// </summary>
    public float TrunkCutoffLocalZ { get; set; }

    /// <summary>
    /// FNV-1a 64-bit hash of the normalized lowercase CGF path. Mirrors
    /// AAEditor.Core.Collision.MeshCacheWriter.Fnv1a64(NormalizePath(...)) — the lookup
    /// key used by MeshCollisionManager._templatesByHash and by per-instance "h" entries.
    /// </summary>
    public ulong PathHash { get; set; }

    /// <summary>
    /// Bounding Volume Hierarchy over Indices (already BVH-reordered by the editor).
    /// Root is node 0. Leaf nodes (RightOrTriCount &lt; 0) reference FirstTri..FirstTri+TriCount-1
    /// triangle slots in the Indices array. Empty when the source mesh has zero triangles or
    /// when loaded from a legacy file without a BVH chunk — consumers fall back to a linear
    /// triangle scan when BvhNodes.Length == 0.
    /// </summary>
    public BvhNode[] BvhNodes { get; set; } = [];

    /// <summary>
    /// Number of vertices (Vertices.Length / 3)
    /// </summary>
    public int VertexCount => Vertices.Length / 3;

    /// <summary>
    /// Number of triangles (Indices.Length / 3)
    /// </summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// A placed instance of a collision mesh in the world.
/// References a shared CollisionMesh and has its own transform.
/// </summary>
public class CollisionMeshInstance
{
    /// <summary>
    /// Reference to the shared mesh data
    /// </summary>
    public CollisionMesh Mesh { get; set; }

    /// <summary>
    /// World position (translation from Matrix34)
    /// </summary>
    public Vector3 Position { get; set; }

    /// <summary>
    /// 2D rotation matrix components from Matrix34 (r00, r01, r10, r11).
    /// Used to transform local vertices to world space: worldXY = R * localXY + Position
    /// </summary>
    public float R00 { get; set; }
    public float R01 { get; set; }
    public float R10 { get; set; }
    public float R11 { get; set; }

    /// <summary>
    /// Uniform scale factor
    /// </summary>
    public float Scale { get; set; }

    /// <summary>
    /// Pre-computed world-space AABB for spatial index broad phase.
    /// Computed once at load time from transformed mesh bounds.
    /// </summary>
    public (float MinX, float MinY, float MaxX, float MaxY) WorldBounds { get; set; }

    /// <summary>
    /// Pre-computed world-space Z range for vertical rejection.
    /// </summary>
    public float WorldMinZ { get; set; }
    public float WorldMaxZ { get; set; }

    /// <summary>
    /// Transform a local-space vertex to world space.
    /// </summary>
    public Vector3 TransformVertex(float lx, float ly, float lz)
    {
        float wx = Position.X + R00 * lx + R01 * ly;
        float wy = Position.Y + R10 * lx + R11 * ly;
        float wz = Position.Z + lz * Scale;
        return new Vector3(wx, wy, wz);
    }

    /// <summary>
    /// Compute and cache the world-space bounding box from the mesh's local AABB.
    /// </summary>
    public void ComputeWorldBounds()
    {
        if (Mesh == null) return;

        // Transform all 4 corners of the local XY AABB to find world-space extents
        var corners = new[]
        {
            (Mesh.LocalMin.X, Mesh.LocalMin.Y),
            (Mesh.LocalMax.X, Mesh.LocalMin.Y),
            (Mesh.LocalMax.X, Mesh.LocalMax.Y),
            (Mesh.LocalMin.X, Mesh.LocalMax.Y)
        };

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var (lx, ly) in corners)
        {
            float wx = Position.X + R00 * lx + R01 * ly;
            float wy = Position.Y + R10 * lx + R11 * ly;
            if (wx < minX) minX = wx;
            if (wy < minY) minY = wy;
            if (wx > maxX) maxX = wx;
            if (wy > maxY) maxY = wy;
        }

        WorldBounds = (minX, minY, maxX, maxY);
        WorldMinZ = Position.Z + Mesh.LocalMin.Z * Scale;
        WorldMaxZ = Position.Z + Mesh.LocalMax.Z * Scale;
    }
}
