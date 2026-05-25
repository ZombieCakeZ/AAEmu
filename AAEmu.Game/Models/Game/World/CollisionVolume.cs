using System.Collections.Generic;
using System.Numerics;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AAEmu.Game.Models.Game.World;

/// <summary>
/// Type of collision volume behavior
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum CollisionVolumeType
{
    Floor = 0,
    Wall = 1,
    Ramp = 2,
    Blocker = 3
}

/// <summary>
/// Shape type for collision volumes
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum CollisionShapeType
{
    Box = 0,
    Polygon = 1,
    Ramp = 2
}

/// <summary>
/// A single collision volume that defines server-side geometry for NPC height/collision queries.
/// Stored in JSON files under Data/CollisionVolumes/.
/// </summary>
public class CollisionVolume
{
    /// <summary>
    /// Unique identifier within the world file
    /// </summary>
    public uint Id { get; set; }

    /// <summary>
    /// Human-readable name for admin reference
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Optional group identifier for multi-volume structures (e.g. "Marianople_Taverne")
    /// </summary>
    public string GroupId { get; set; }

    /// <summary>
    /// Floor index for multi-story buildings (0 = ground floor)
    /// </summary>
    public int FloorIndex { get; set; }

    /// <summary>
    /// The behavior type of this volume
    /// </summary>
    public CollisionVolumeType VolumeType { get; set; } = CollisionVolumeType.Floor;

    /// <summary>
    /// The geometric shape type
    /// </summary>
    public CollisionShapeType Shape { get; set; } = CollisionShapeType.Polygon;

    /// <summary>
    /// Whether this volume is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Polygon vertices (for Polygon shape). Ordered, closed automatically.
    /// Each vertex has its own Z for uneven floor support.
    /// </summary>
    public List<Vector3> Vertices { get; set; } = [];

    /// <summary>
    /// Center point (for Box/Ramp shapes)
    /// </summary>
    public Vector3 Center { get; set; }

    /// <summary>
    /// Half-extents (for Box/Ramp shapes): X=half-width, Y=half-length, Z=half-height
    /// </summary>
    public Vector3 Size { get; set; }

    /// <summary>
    /// Y-axis rotation in degrees (for Box/Ramp shapes)
    /// </summary>
    public float RotationZ { get; set; }

    /// <summary>
    /// Slope direction in degrees, 0=North (for Ramp shape only)
    /// </summary>
    public float? SlopeDirection { get; set; }

    /// <summary>
    /// Slope angle in degrees (for Ramp shape only)
    /// </summary>
    public float? SlopeAngle { get; set; }

    /// <summary>
    /// Wall height in meters (for Wall volumes only). The wall extends upward from vertex Z.
    /// </summary>
    public float WallHeight { get; set; }

    // --- Runtime cached data (not serialized) ---

    /// <summary>
    /// Pre-computed triangles from ear-clipping (vertex indices into Vertices list).
    /// Each entry is (i0, i1, i2).
    /// </summary>
    [JsonIgnore]
    public List<(int A, int B, int C)> Triangles { get; set; }

    /// <summary>
    /// Cached 2D bounding box for fast rejection: (MinX, MinY, MaxX, MaxY)
    /// </summary>
    [JsonIgnore]
    public (float MinX, float MinY, float MaxX, float MaxY) BoundingBox { get; set; }
}

/// <summary>
/// Root JSON structure for a world's collision volumes file
/// </summary>
public class CollisionVolumeFile
{
    public string WorldName { get; set; } = "";
    public uint NextId { get; set; } = 1;
    public List<CollisionVolume> Volumes { get; set; } = [];
}
