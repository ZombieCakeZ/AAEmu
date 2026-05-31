using Newtonsoft.Json;

namespace AAEmu.Game.Models.Game.World;

/// <summary>
/// Server-side mirror of the editor's per-cell instance JSON schema. The editor
/// emits one file per 1024m cell as editor_&lt;world&gt;_&lt;cx&gt;_&lt;cy&gt;.json — see
/// AAEditor.Core.Collision.CollisionInstanceFile (D:/AAEditor/AAEditor.Core/Collision/CollisionExportFormat.cs).
/// Field names must stay aligned with that writer; the version int gates breaking changes.
/// </summary>
public sealed class CollisionInstanceFile
{
    [JsonProperty("world")]
    public string World { get; set; } = "";

    [JsonProperty("version")]
    public int Version { get; set; }

    [JsonProperty("cellX")]
    public int CellX { get; set; }

    [JsonProperty("cellY")]
    public int CellY { get; set; }

    [JsonProperty("instances")]
    public List<CollisionInstanceDto> Instances { get; set; } = new();
}

/// <summary>
/// Per-instance JSON record. <see cref="PathHashHex"/> is the lookup key into
/// MeshCollisionManager._templatesByHash; CgfPath is debug-only.
/// </summary>
public sealed class CollisionInstanceDto
{
    [JsonProperty("id")]
    public ulong Id { get; set; }

    [JsonProperty("cgf")]
    public string CgfPath { get; set; } = "";

    /// <summary>Hex-formatted FNV-1a64 of the normalized CGF path, e.g. "0x7F3E1A29C8B40551".</summary>
    [JsonProperty("h")]
    public string PathHashHex { get; set; } = "";

    /// <summary>World-space position (AA Z-up, meters): [x, y, z].</summary>
    [JsonProperty("pos")]
    public float[] Pos { get; set; } = new float[3];

    /// <summary>2D rotation packed as [R00, R01, R10, R11]. Matches CollisionMeshInstance.</summary>
    [JsonProperty("rot")]
    public float[] Rot { get; set; } = new float[4];

    [JsonProperty("scale")]
    public float Scale { get; set; } = 1f;

    /// <summary>Per-instance flag overrides (NoCollision bit 2, TrunkOnly bit 3).</summary>
    [JsonProperty("flags")]
    public uint Flags { get; set; }

    [JsonProperty("src")]
    public string Source { get; set; } = "";
}
