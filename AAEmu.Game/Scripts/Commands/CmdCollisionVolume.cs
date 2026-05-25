using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.World;
using AAEmu.Game.Utils.Scripts;

using GameTask = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// Admin command for managing server-side collision volumes.
/// Usage:
///   /colvol floor start [name]          — Start polygon floor drawing mode
///   /colvol floor scan start [epsilon] [name] — Auto floor scan (walk the perimeter)
///   /colvol wall start [height] [name]  — Start wall drawing mode (manual corners)
///   /colvol wall scan start [height] [epsilon] [name] — Auto wall scan (walk along wall)
///   /colvol corner                      — Add a vertex at current position
///   /colvol undo                        — Remove last vertex
///   /colvol floor finish / wall finish  — Complete, save, activate
///   /colvol floor cancel / wall cancel  — Cancel drawing
///   /colvol box sX sY sZ [name]        — Create a box volume at current position
///   /colvol scan start [name] [spacing] — Start auto floor scan (walk around)
///   /colvol scan finish / cancel / status
///   /colvol grid start [cellSize] [radius] [name] — Start named height grid scan
///   /colvol grid finish / cancel / status [name] / list / clear [name] / delete name
///   /colvol grid show [radius] [name]   — Visualize grid cells
///   /colvol gridbind npcId [gridName]   — Bind NPC to grid(s) (call multiple times to add)
///   /colvol gridunbind npcId [gridName]  — Unbind NPC from grid(s)
/// </summary>
public class CmdCollisionVolume : ICommand
{
    public string[] CommandNames { get; set; } = ["colvol", "cv"];

    // Doodad template ID for debug visualization markers (torch)
    // 320 = Small Torch, widely available in most setups
    private const uint DebugDoodadTemplateId = 320;

    /// <summary>
    /// Tracks spawned debug marker ObjIds per character, so they can be removed with /cv hide.
    /// Key = character ObjId, Value = list of doodad ObjIds.
    /// </summary>
    private static readonly Dictionary<uint, List<uint>> _spawnedMarkers = new();

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "<floor|wall|corner|undo|box|scan|grid|bind|unbind|bindings|list|info|delete|toggle|show|hide|reload|save>";
    }

    public string GetCommandHelpText()
    {
        return "Manages server-side collision volumes for NPC ground detection and wall blocking.\n" +
               "  /colvol floor start [name] — Start polygon floor drawing\n" +
               "  /colvol floor scan start [epsilon] [name] — Auto floor scan (walk perimeter)\n" +
               "  /colvol floor scan finish — Create floor from scanned perimeter\n" +
               "  /colvol floor scan status — Show floor scan progress\n" +
               "  /colvol wall start [height] [name] — Start wall drawing (manual corners)\n" +
               "  /colvol wall scan start [height] [epsilon] [name] — Auto wall scan (walk along wall)\n" +
               "  /colvol wall scan finish — Create wall from auto-detected corners\n" +
               "  /colvol wall scan status — Show wall scan progress\n" +
               "  /colvol corner — Add vertex at your position\n" +
               "  /colvol undo — Remove last vertex\n" +
               "  /colvol floor finish / wall finish — Complete and save\n" +
               "  /colvol floor cancel / wall cancel — Cancel drawing\n" +
               "  /colvol box <sizeX> <sizeY> <sizeZ> [name] — Create box\n" +
               "  /colvol scan start [name] [spacing] — Start auto floor scan (walk around)\n" +
               "  /colvol scan finish — Generate floors from scan data\n" +
               "  /colvol scan cancel — Cancel scan\n" +
               "  /colvol scan status — Show scan progress\n" +
               "  /colvol grid start [cellSize] [radius] [name] — Start named grid scan\n" +
               "  /colvol grid finish — Save grid data to disk\n" +
               "  /colvol grid cancel — Cancel grid scan\n" +
               "  /colvol grid status [name] — Show grid statistics\n" +
               "  /colvol grid list — List all grids for this world\n" +
               "  /colvol grid clear [name] — Clear grid data\n" +
               "  /colvol grid delete <name> — Delete a named grid\n" +
               "  /colvol grid show [radius] [name] — Visualize grid cells\n" +
               "  /colvol bind <npcId> <volId1,volId2,...> — Bind NPC to volumes\n" +
               "  /colvol unbind <npcId> — Remove NPC volume binding\n" +
               "  /colvol gridbind <npcId> [gridName] — Add grid binding (call multiple times for multi-grid)\n" +
               "  /colvol gridunbind <npcId> [gridName] — Remove grid binding (without name = remove all)\n" +
               "  /colvol bindings — List all NPC bindings\n" +
               "  /colvol list [radius] — List nearby volumes\n" +
               "  /colvol info <id> — Volume details\n" +
               "  /colvol delete <id> — Delete volume\n" +
               "  /colvol toggle <id> — Enable/disable\n" +
               "  /colvol show [radius|id] — Visualize with torches\n" +
               "  /colvol hide — Remove all debug markers\n" +
               "  /colvol reload — Reload from disk\n" +
               "  /colvol save — Force save";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length == 0)
        {
            CommandManager.SendDefaultHelpText(this, messageOutput);
            return;
        }

        var mgr = CollisionVolumeManager.Instance;
        var worldName = character.Transform.WorldId < uint.MaxValue
            ? CollisionVolumeManager.GetWorldNameFromId(character.Transform.WorldId)
            : "main_world";

        var sub = args[0].ToLowerInvariant();
        switch (sub)
        {
            case "floor":
                HandleFloor(character, args, messageOutput, mgr, worldName);
                break;
            case "wall":
                HandleWall(character, args, messageOutput, mgr, worldName);
                break;
            case "corner":
                HandleCorner(character, messageOutput, mgr);
                break;
            case "undo":
                HandleUndo(character, messageOutput, mgr);
                break;
            case "box":
                HandleBox(character, args, messageOutput, mgr, worldName);
                break;
            case "scan":
                HandleScan(character, args, messageOutput, mgr, worldName);
                break;
            case "grid":
                HandleGrid(character, args, messageOutput, mgr, worldName);
                break;
            case "bind":
                HandleBind(character, args, messageOutput, mgr);
                break;
            case "unbind":
                HandleUnbind(character, args, messageOutput, mgr);
                break;
            case "gridbind":
                HandleGridBind(character, args, messageOutput, mgr);
                break;
            case "gridunbind":
                HandleGridUnbind(character, args, messageOutput, mgr);
                break;
            case "bindings":
                HandleBindings(character, messageOutput, mgr);
                break;
            case "list":
                HandleList(character, args, messageOutput, mgr, worldName);
                break;
            case "info":
                HandleInfo(character, args, messageOutput, mgr, worldName);
                break;
            case "delete":
                HandleDelete(character, args, messageOutput, mgr, worldName);
                break;
            case "toggle":
                HandleToggle(character, args, messageOutput, mgr, worldName);
                break;
            case "show":
                HandleShow(character, args, messageOutput, mgr, worldName);
                break;
            case "hide":
                HandleHide(character);
                break;
            case "reload":
                mgr.Reload();
                character.SendMessage($"[ColVol] Reloaded. {mgr.GetTotalVolumeCount()} volumes total.");
                break;
            case "save":
                mgr.SaveWorldFile(worldName);
                character.SendMessage($"[ColVol] Saved {worldName}.json");
                break;
            default:
                CommandManager.SendDefaultHelpText(this, messageOutput);
                break;
        }
    }

    private static void HandleFloor(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        if (args.Length < 2)
        {
            character.SendMessage("[ColVol] Usage: /colvol floor <start|scan|finish|cancel> [name]");
            character.SendMessage("  start - Manual mode: use /cv corner at each vertex");
            character.SendMessage("  scan  - Auto mode: walk the perimeter edges");
            return;
        }

        var action = args[1].ToLowerInvariant();
        switch (action)
        {
            case "start":
            {
                if (mgr.IsDrawing(character.ObjId))
                {
                    character.SendMessage("[ColVol] Already in drawing mode. Use 'floor finish' or 'floor cancel' first.");
                    return;
                }

                var name = args.Length >= 3 ? string.Join(" ", args, 2, args.Length - 2) : $"Volume_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                mgr.StartPolygonDraw(character.ObjId, worldName, name);
                character.SendMessage($"[ColVol] Polygon drawing started: \"{name}\"");
                character.SendMessage("[ColVol] Walk to corners and use /colvol corner to add vertices.");
                character.SendMessage("[ColVol] Use /colvol floor finish when done (min 3 corners).");
                break;
            }
            case "finish":
            {
                var state = mgr.GetDrawState(character.ObjId);
                if (state == null)
                {
                    character.SendMessage("[ColVol] Not in drawing mode. Use /colvol floor start first.");
                    return;
                }

                if (state.Vertices.Count < 3)
                {
                    character.SendMessage($"[ColVol] Need at least 3 corners, have {state.Vertices.Count}. Add more or cancel.");
                    return;
                }

                var volume = mgr.FinishPolygonDraw(character.ObjId);
                if (volume != null)
                {
                    character.SendMessage($"[ColVol] Polygon floor created! ID={volume.Id}, Name=\"{volume.Name}\", " +
                                          $"Vertices={volume.Vertices.Count}, Triangles={volume.Triangles?.Count ?? 0}");
                    character.SendMessage($"[ColVol] Saved to {worldName}.json. Active immediately.");

                    ShowVolumeMarkersDelayed(character, volume);
                }
                else
                {
                    character.SendMessage("[ColVol] Failed to create polygon. Triangulation may have failed.");
                }

                break;
            }
            case "cancel":
            {
                if (!mgr.IsDrawing(character.ObjId))
                {
                    character.SendMessage("[ColVol] Not in drawing mode.");
                    return;
                }

                mgr.CancelDraw(character.ObjId);
                character.SendMessage("[ColVol] Drawing cancelled.");
                break;
            }
            case "scan":
            {
                HandleFloorScan(character, args, messageOutput, mgr, worldName);
                break;
            }
            default:
                character.SendMessage("[ColVol] Usage: /colvol floor <start|scan|finish|cancel> [name]");
                character.SendMessage("  start - Manual mode: use /cv corner at each vertex");
                character.SendMessage("  scan  - Auto mode: walk the perimeter, polygon created automatically");
                break;
        }
    }

    /// <summary>
    /// Handle /cv floor scan sub-commands: start/finish/cancel/status
    /// </summary>
    private static void HandleFloorScan(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        if (args.Length < 3)
        {
            character.SendMessage("[ColVol] Usage: /colvol floor scan <start|finish|cancel|status> [epsilon] [name]");
            character.SendMessage("  start [epsilon] [name] - Start floor perimeter scan (walk the edges)");
            character.SendMessage("    epsilon: Corner detection sensitivity (default: 0.5, smaller = more corners)");
            character.SendMessage("  finish - Create floor polygon from scanned perimeter");
            character.SendMessage("  cancel - Abort floor scan");
            character.SendMessage("  status - Show recorded points");
            return;
        }

        var scanAction = args[2].ToLowerInvariant();
        switch (scanAction)
        {
            case "start":
            {
                if (mgr.IsFloorScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Already floor scanning! Use 'floor scan finish' or 'floor scan cancel' first.");
                    return;
                }

                if (mgr.IsDrawing(character.ObjId) || mgr.IsScanning(character.ObjId) ||
                    mgr.IsGridScanning(character.ObjId) || mgr.IsWallScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] You are in another mode. Finish or cancel it first.");
                    return;
                }

                // Parse: /cv floor scan start [epsilon] [name]
                var epsilon = 0.5f;
                var nameStartIdx = 3;

                if (args.Length >= 4 && float.TryParse(args[3], out var eps))
                {
                    epsilon = Math.Clamp(eps, 0.05f, 5f);
                    nameStartIdx = 4;
                }

                var name = args.Length > nameStartIdx
                    ? string.Join(" ", args, nameStartIdx, args.Length - nameStartIdx)
                    : $"FloorScan_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

                mgr.StartFloorScan(character.ObjId, worldName, name, epsilon);

                // Schedule position recording every 200ms
                var scanTask = new FloorPerimeterScanRecordTask(character);
                TaskManager.Instance.Schedule(scanTask, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));

                var state = mgr.GetFloorPerimeterScanState(character.ObjId);
                if (state != null)
                    state.ScanTask = scanTask;

                character.SendMessage($"[ColVol] Floor scan started: \"{name}\" (epsilon: {epsilon:F2}m)");
                character.SendMessage("[ColVol] Walk along the perimeter/edges - the polygon is built automatically!");
                character.SendMessage("[ColVol] Use '/cv floor scan finish' when done (walk back near start, min 3 points).");
                break;
            }
            case "finish":
            {
                if (!mgr.IsFloorScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Not floor scanning. Use '/cv floor scan start' first.");
                    return;
                }

                var (volume, rawPoints, simplifiedPoints) = mgr.FinishFloorScan(character.ObjId);
                if (volume == null)
                {
                    if (simplifiedPoints > 0)
                        character.SendMessage($"[ColVol] Floor scan failed - triangulation failed. Recorded: {rawPoints} -> Simplified: {simplifiedPoints}");
                    else
                        character.SendMessage($"[ColVol] Floor scan failed - need at least 3 points. Recorded: {rawPoints}");
                    return;
                }

                character.SendMessage($"[ColVol] Floor scan complete!");
                character.SendMessage($"  ID={volume.Id}, Name=\"{volume.Name}\"");
                character.SendMessage($"  Recorded: {rawPoints} points -> Simplified: {simplifiedPoints} corners");
                character.SendMessage($"  Triangles={volume.Triangles?.Count ?? 0}");
                character.SendMessage($"  Saved to {worldName}.json. Active immediately.");

                ShowVolumeMarkersDelayed(character, volume);
                break;
            }
            case "cancel":
            {
                if (!mgr.IsFloorScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Not floor scanning.");
                    return;
                }

                mgr.CancelFloorScan(character.ObjId);
                character.SendMessage("[ColVol] Floor scan cancelled. All data discarded.");
                break;
            }
            case "status":
            {
                var state = mgr.GetFloorPerimeterScanState(character.ObjId);
                if (state == null)
                {
                    character.SendMessage("[ColVol] Not floor scanning.");
                    return;
                }

                character.SendMessage($"[ColVol] Floor scan \"{state.Name}\":");
                character.SendMessage($"  Points: {state.Points.Count}, Epsilon: {state.SimplifyEpsilon:F2}m");

                if (state.Points.Count >= 3)
                {
                    var preview = AAEmu.Game.Utils.PolygonUtils.SimplifyPath(state.Points, state.SimplifyEpsilon);
                    var totalLength = 0f;
                    for (var i = 0; i < state.Points.Count - 1; i++)
                        totalLength += Vector3.Distance(state.Points[i], state.Points[i + 1]);
                    character.SendMessage($"  Perimeter walked: {totalLength:F1}m, Preview corners: {preview.Count}");
                }
                break;
            }
            default:
                character.SendMessage("[ColVol] Usage: /colvol floor scan <start|finish|cancel|status> [epsilon] [name]");
                break;
        }
    }

    private static void HandleWall(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        if (args.Length < 2)
        {
            character.SendMessage("[ColVol] Usage: /colvol wall <start|scan|finish|cancel> [height] [name]");
            return;
        }

        var action = args[1].ToLowerInvariant();
        switch (action)
        {
            case "start":
            {
                if (mgr.IsDrawing(character.ObjId))
                {
                    character.SendMessage("[ColVol] Already in drawing mode. Use 'wall finish' or 'wall cancel' first.");
                    return;
                }

                var height = 1f;
                var nameStartIdx = 2;

                if (args.Length >= 3 && float.TryParse(args[2], out var h))
                {
                    height = h;
                    nameStartIdx = 3;
                }

                var name = args.Length > nameStartIdx
                    ? string.Join(" ", args, nameStartIdx, args.Length - nameStartIdx)
                    : $"Wall_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

                mgr.StartWallDraw(character.ObjId, worldName, name, height);
                character.SendMessage($"[ColVol] Wall drawing started: \"{name}\" (height: {height}m)");
                character.SendMessage("[ColVol] Walk along the wall path and use /colvol corner at each point.");
                character.SendMessage("[ColVol] Use /colvol wall finish when done (min 2 corners).");
                break;
            }
            case "scan":
            {
                HandleWallScan(character, args, messageOutput, mgr, worldName);
                break;
            }
            case "finish":
            {
                var state = mgr.GetDrawState(character.ObjId);
                if (state == null || state.Mode != CollisionVolumeManager.DrawMode.Wall)
                {
                    character.SendMessage("[ColVol] Not in wall drawing mode. Use /colvol wall start first.");
                    return;
                }

                if (state.Vertices.Count < 2)
                {
                    character.SendMessage($"[ColVol] Need at least 2 corners for a wall, have {state.Vertices.Count}. Add more or cancel.");
                    return;
                }

                var volume = mgr.FinishPolygonDraw(character.ObjId);
                if (volume != null)
                {
                    var totalLength = 0f;
                    for (var i = 0; i < volume.Vertices.Count - 1; i++)
                        totalLength += Vector3.Distance(volume.Vertices[i], volume.Vertices[i + 1]);

                    character.SendMessage($"[ColVol] Wall created! ID={volume.Id}, Name=\"{volume.Name}\", " +
                                          $"Segments={volume.Vertices.Count - 1}, Length={totalLength:F1}m, Height={volume.WallHeight}m");
                    character.SendMessage($"[ColVol] Saved to {worldName}.json. Active immediately.");

                    ShowVolumeMarkersDelayed(character, volume);
                }
                else
                {
                    character.SendMessage("[ColVol] Failed to create wall.");
                }

                break;
            }
            case "cancel":
            {
                if (!mgr.IsDrawing(character.ObjId))
                {
                    character.SendMessage("[ColVol] Not in drawing mode.");
                    return;
                }

                mgr.CancelDraw(character.ObjId);
                character.SendMessage("[ColVol] Wall drawing cancelled.");
                break;
            }
            default:
                character.SendMessage("[ColVol] Usage: /colvol wall <start|scan|finish|cancel> [height] [name]");
                break;
        }
    }

    private static void HandleWallScan(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        if (args.Length < 3)
        {
            character.SendMessage("[ColVol] Usage: /colvol wall scan <start|finish|cancel|status> [height] [epsilon] [name]");
            return;
        }

        var scanAction = args[2].ToLowerInvariant();
        switch (scanAction)
        {
            case "start":
            {
                if (mgr.IsWallScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Already wall scanning! Use 'wall scan finish' or 'wall scan cancel' first.");
                    return;
                }

                if (mgr.IsDrawing(character.ObjId) || mgr.IsScanning(character.ObjId) || mgr.IsGridScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] You are in another mode. Finish or cancel it first.");
                    return;
                }

                var height = 2f;
                var epsilon = 0.1f;
                var nameStartIdx = 3;

                if (args.Length >= 4 && float.TryParse(args[3], out var h))
                {
                    height = h;
                    nameStartIdx = 4;
                }

                if (args.Length >= 5 && float.TryParse(args[4], out var eps))
                {
                    epsilon = Math.Clamp(eps, 0.05f, 5f);
                    nameStartIdx = 5;
                }

                var name = args.Length > nameStartIdx
                    ? string.Join(" ", args, nameStartIdx, args.Length - nameStartIdx)
                    : $"WallScan_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

                mgr.StartWallScan(character.ObjId, worldName, name, height, epsilon);

                var scanTask = new WallScanRecordTask(character);
                var scheduled = TaskManager.Instance.Schedule(scanTask, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));

                if (!scheduled)
                {
                    character.SendMessage("[ColVol] ERROR: Failed to schedule scan task! Please try again.");
                    mgr.CancelWallScan(character.ObjId);
                    return;
                }

                var state = mgr.GetWallScanState(character.ObjId);
                if (state != null)
                    state.ScanTask = scanTask;

                character.SendMessage($"[ColVol] Wall scan started: \"{name}\" (height: {height}m, epsilon: {epsilon:F2}m)");
                character.SendMessage("[ColVol] Walk along the wall - corners are detected automatically!");
                character.SendMessage("[ColVol] Use '/cv wall scan finish' when done (min 2m walked).");
                break;
            }
            case "finish":
            {
                if (!mgr.IsWallScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Not wall scanning. Use '/cv wall scan start' first.");
                    return;
                }

                var (volume, rawPoints, simplifiedPoints) = mgr.FinishWallScan(character.ObjId);
                if (volume == null)
                {
                    character.SendMessage($"[ColVol] Wall scan failed - need at least 2 points further apart. Recorded: {rawPoints}");
                    return;
                }

                var totalLength = 0f;
                for (var i = 0; i < volume.Vertices.Count - 1; i++)
                    totalLength += Vector3.Distance(volume.Vertices[i], volume.Vertices[i + 1]);

                character.SendMessage($"[ColVol] Wall scan complete!");
                character.SendMessage($"  ID={volume.Id}, Name=\"{volume.Name}\"");
                character.SendMessage($"  Recorded: {rawPoints} points -> Simplified: {simplifiedPoints} corners");
                character.SendMessage($"  Segments={volume.Vertices.Count - 1}, Length={totalLength:F1}m, Height={volume.WallHeight}m");
                character.SendMessage($"  Saved to {worldName}.json. Active immediately.");

                ShowVolumeMarkersDelayed(character, volume);
                break;
            }
            case "cancel":
            {
                if (!mgr.IsWallScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Not wall scanning.");
                    return;
                }

                mgr.CancelWallScan(character.ObjId);
                character.SendMessage("[ColVol] Wall scan cancelled. All data discarded.");
                break;
            }
            case "status":
            {
                var state = mgr.GetWallScanState(character.ObjId);
                if (state == null)
                {
                    character.SendMessage("[ColVol] Not wall scanning.");
                    return;
                }

                character.SendMessage($"[ColVol] Wall scan \"{state.Name}\":");
                character.SendMessage($"  Points: {state.Points.Count}, Height: {state.WallHeight}m, Epsilon: {state.SimplifyEpsilon:F2}m");

                if (state.Points.Count >= 2)
                {
                    var preview = AAEmu.Game.Utils.PolygonUtils.SimplifyPath(state.Points, state.SimplifyEpsilon);
                    var totalLength = 0f;
                    for (var i = 0; i < state.Points.Count - 1; i++)
                        totalLength += Vector3.Distance(state.Points[i], state.Points[i + 1]);
                    character.SendMessage($"  Path length: {totalLength:F1}m, Preview corners: {preview.Count}");
                }
                break;
            }
            default:
                character.SendMessage("[ColVol] Usage: /colvol wall scan <start|finish|cancel|status> [height] [epsilon] [name]");
                break;
        }
    }

    private static void HandleCorner(Character character, IMessageOutput messageOutput, CollisionVolumeManager mgr)
    {
        if (!mgr.IsDrawing(character.ObjId))
        {
            character.SendMessage("[ColVol] Not in drawing mode. Use /colvol floor start or /colvol wall start first.");
            return;
        }

        var pos = character.Transform.World.Position;
        var idx = mgr.AddCorner(character.ObjId, new Vector3(pos.X, pos.Y, pos.Z));

        var state = mgr.GetDrawState(character.ObjId);
        var modeStr = state?.Mode == CollisionVolumeManager.DrawMode.Wall ? "wall" : "floor";
        character.SendMessage($"[ColVol] Corner #{idx} set at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");

        var minRequired = state?.Mode == CollisionVolumeManager.DrawMode.Wall ? 2 : 3;
        if (state != null && state.Vertices.Count >= minRequired)
        {
            character.SendMessage($"[ColVol] {state.Vertices.Count} corners. Use /colvol {modeStr} finish to complete, or add more.");
        }
    }

    private static void HandleUndo(Character character, IMessageOutput messageOutput, CollisionVolumeManager mgr)
    {
        if (mgr.UndoCorner(character.ObjId))
        {
            var state = mgr.GetDrawState(character.ObjId);
            character.SendMessage($"[ColVol] Last corner removed. {state?.Vertices.Count ?? 0} corners remaining.");
        }
        else
        {
            character.SendMessage("[ColVol] Nothing to undo.");
        }
    }

    private static void HandleBox(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        if (args.Length < 4)
        {
            character.SendMessage("[ColVol] Usage: /colvol box <sizeX> <sizeY> <sizeZ> [name]");
            return;
        }

        if (!float.TryParse(args[1], out var sizeX) ||
            !float.TryParse(args[2], out var sizeY) ||
            !float.TryParse(args[3], out var sizeZ))
        {
            character.SendMessage("[ColVol] Invalid size values. Usage: /colvol box 5 10 0.5");
            return;
        }

        var name = args.Length >= 5 ? string.Join(" ", args, 4, args.Length - 4) : $"Box_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        var pos = character.Transform.World.Position;
        var rotZ = character.Transform.World.Rotation.Z * (180f / MathF.PI);

        var halfExtents = new Vector3(sizeX / 2f, sizeY / 2f, sizeZ / 2f);
        var volume = mgr.AddBox(worldName, name, new Vector3(pos.X, pos.Y, pos.Z),
            halfExtents, rotZ);

        character.SendMessage($"[ColVol] Box floor created! ID={volume.Id}, Name=\"{volume.Name}\", " +
                              $"Size=({sizeX}x{sizeY}x{sizeZ}), RotZ={rotZ:F1}deg");
        character.SendMessage($"[ColVol] Center=({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");

        ShowVolumeMarkersDelayed(character, volume);
    }

    private static void HandleScan(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        if (args.Length < 2)
        {
            character.SendMessage("[ColVol] Usage: /colvol scan <start|finish|cancel|status>");
            return;
        }

        var action = args[1].ToLowerInvariant();
        switch (action)
        {
            case "start":
            {
                if (mgr.IsScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Already scanning! Use 'scan finish' or 'scan cancel' first.");
                    return;
                }

                if (mgr.IsDrawing(character.ObjId))
                {
                    character.SendMessage("[ColVol] You are currently drawing. Cancel or finish it first.");
                    return;
                }

                var scanName = args.Length > 2 ? args[2] : "Scan";
                var spacing = 1.0f;
                if (args.Length > 3 && float.TryParse(args[3], out var parsedSpacing))
                    spacing = Math.Clamp(parsedSpacing, 0.25f, 10f);

                mgr.StartScan(character.ObjId, worldName, scanName, spacing);

                var scanTask = new FloorScanRecordTask(character);
                TaskManager.Instance.Schedule(scanTask, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

                var state = mgr.GetScanState(character.ObjId);
                if (state != null)
                    state.ScanTask = scanTask;

                character.SendMessage($"[ColVol] Floor scan started! Name=\"{scanName}\", MinSpacing={spacing:F2}m");
                character.SendMessage("[ColVol] Walk around the area. Multi-floor is auto-detected.");
                character.SendMessage("[ColVol] Use '/colvol scan finish' when done, or 'scan cancel' to abort.");
                break;
            }
            case "finish":
            {
                if (!mgr.IsScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Not scanning. Use '/colvol scan start [name]' first.");
                    return;
                }

                var state = mgr.GetScanState(character.ObjId);
                var pointCount = state?.Points.Count ?? 0;
                character.SendMessage($"[ColVol] Finishing scan with {pointCount} recorded points...");

                state?.ScanTask?.Cancel();

                var volumes = mgr.FinishScan(character.ObjId);
                if (volumes == null || volumes.Count == 0)
                {
                    character.SendMessage("[ColVol] Scan failed - not enough points. Walk more next time (min 10 points per floor).");
                    return;
                }

                character.SendMessage($"[ColVol] Scan complete! {volumes.Count} floor(s) created from {pointCount} points:");
                foreach (var vol in volumes)
                {
                    character.SendMessage($"  ID={vol.Id} \"{vol.Name}\" - {vol.Vertices.Count} vertices, " +
                                          $"FloorIndex={vol.FloorIndex}");
                }
                character.SendMessage("[ColVol] Volumes saved and active. Use '/colvol show' to visualize.");
                break;
            }
            case "cancel":
            {
                if (!mgr.IsScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Not scanning.");
                    return;
                }

                mgr.CancelScan(character.ObjId);
                character.SendMessage("[ColVol] Scan cancelled. All recorded data discarded.");
                break;
            }
            case "status":
            {
                if (!mgr.IsScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Not scanning.");
                    return;
                }

                var state = mgr.GetScanState(character.ObjId);
                if (state == null)
                {
                    character.SendMessage("[ColVol] Not scanning.");
                    return;
                }

                character.SendMessage($"[ColVol] Scan \"{state.Name}\" - {state.Points.Count} points, " +
                                      $"Spacing={state.MinPointSpacing:F2}m");
                if (state.Points.Count > 0)
                {
                    var floors = AAEmu.Game.Utils.PolygonUtils.ClusterByFloor(state.Points, 1.5f);
                    character.SendMessage($"[ColVol] Estimated {floors.Count} floor(s) from current data.");
                    for (var i = 0; i < floors.Count; i++)
                    {
                        var avgZ = floors[i].Average(p => p.Z);
                        character.SendMessage($"  Floor {i}: {floors[i].Count} pts, AvgZ={avgZ:F1}");
                    }
                }
                break;
            }
            default:
                character.SendMessage("[ColVol] Usage: /colvol scan <start|finish|cancel|status>");
                break;
        }
    }

    private static void HandleList(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        var radius = 100f;
        if (args.Length >= 2 && float.TryParse(args[1], out var r))
            radius = r;

        var pos = character.Transform.World.Position;
        var volumes = mgr.ListVolumesNear(worldName, pos.X, pos.Y, radius);

        if (volumes.Count == 0)
        {
            character.SendMessage($"[ColVol] No volumes within {radius}m.");
            return;
        }

        character.SendMessage($"[ColVol] {volumes.Count} volumes within {radius}m:");
        foreach (var vol in volumes)
        {
            var active = vol.IsActive ? "ON" : "OFF";
            var shape = vol.Shape.ToString();
            var type = vol.VolumeType.ToString();
            character.SendMessage($"  #{vol.Id} \"{vol.Name}\" [{type}/{shape}] {active}");
        }
    }

    private static void HandleInfo(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        if (args.Length < 2 || !uint.TryParse(args[1], out var id))
        {
            character.SendMessage("[ColVol] Usage: /colvol info <id>");
            return;
        }

        var vol = mgr.GetVolume(worldName, id);
        if (vol == null)
        {
            character.SendMessage($"[ColVol] Volume #{id} not found in {worldName}.");
            return;
        }

        character.SendMessage($"[ColVol] Volume #{vol.Id} \"{vol.Name}\"");
        character.SendMessage($"  Type: {vol.VolumeType}, Shape: {vol.Shape}, Active: {vol.IsActive}");
        character.SendMessage($"  Group: {vol.GroupId ?? "(none)"}, Floor: {vol.FloorIndex}");

        if (vol.Shape == CollisionShapeType.Polygon)
        {
            if (vol.VolumeType == CollisionVolumeType.Wall)
            {
                var totalLength = 0f;
                for (var i = 0; i < vol.Vertices.Count - 1; i++)
                    totalLength += Vector3.Distance(vol.Vertices[i], vol.Vertices[i + 1]);
                character.SendMessage($"  WallHeight: {vol.WallHeight}m, Segments: {vol.Vertices.Count - 1}, Length: {totalLength:F1}m");
            }
            else
            {
                character.SendMessage($"  Triangles: {vol.Triangles?.Count ?? 0}");
            }

            character.SendMessage($"  Vertices: {vol.Vertices.Count}");
            for (var i = 0; i < vol.Vertices.Count; i++)
            {
                var v = vol.Vertices[i];
                character.SendMessage($"    [{i}] ({v.X:F1}, {v.Y:F1}, {v.Z:F1})");
            }
        }
        else
        {
            character.SendMessage($"  Center: ({vol.Center.X:F1}, {vol.Center.Y:F1}, {vol.Center.Z:F1})");
            character.SendMessage($"  Size: ({vol.Size.X:F1}, {vol.Size.Y:F1}, {vol.Size.Z:F1})");
            character.SendMessage($"  RotationZ: {vol.RotationZ:F1}deg");
        }

        character.SendMessage($"  BBox: ({vol.BoundingBox.MinX:F0},{vol.BoundingBox.MinY:F0}) to ({vol.BoundingBox.MaxX:F0},{vol.BoundingBox.MaxY:F0})");
    }

    private static void HandleDelete(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        if (args.Length < 2 || !uint.TryParse(args[1], out var id))
        {
            character.SendMessage("[ColVol] Usage: /colvol delete <id>");
            return;
        }

        if (mgr.DeleteVolume(worldName, id))
            character.SendMessage($"[ColVol] Volume #{id} deleted and saved.");
        else
            character.SendMessage($"[ColVol] Volume #{id} not found in {worldName}.");
    }

    private static void HandleToggle(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        if (args.Length < 2 || !uint.TryParse(args[1], out var id))
        {
            character.SendMessage("[ColVol] Usage: /colvol toggle <id>");
            return;
        }

        if (mgr.ToggleVolume(worldName, id))
        {
            var vol = mgr.GetVolume(worldName, id);
            character.SendMessage($"[ColVol] Volume #{id} is now {(vol?.IsActive == true ? "ACTIVE" : "INACTIVE")}.");
        }
        else
        {
            character.SendMessage($"[ColVol] Volume #{id} not found in {worldName}.");
        }
    }

    private static void HandleShow(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        if (args.Length >= 2 && uint.TryParse(args[1], out var id))
        {
            var vol = mgr.GetVolume(worldName, id);
            if (vol == null)
            {
                character.SendMessage($"[ColVol] Volume #{id} not found.");
                return;
            }

            ShowVolumeMarkers(character, vol);
            character.SendMessage($"[ColVol] Showing volume #{id}.");
            return;
        }

        var radius = 100f;
        if (args.Length >= 2 && float.TryParse(args[1], out var r))
            radius = r;

        var pos = character.Transform.World.Position;
        var volumes = mgr.ListVolumesNear(worldName, pos.X, pos.Y, radius);

        if (volumes.Count == 0)
        {
            character.SendMessage($"[ColVol] No volumes within {radius}m to show.");
            return;
        }

        foreach (var vol in volumes)
        {
            ShowVolumeMarkers(character, vol);
        }

        character.SendMessage($"[ColVol] Showing {volumes.Count} volumes as markers.");
    }

    private static void ShowVolumeMarkers(Character character, CollisionVolume vol)
    {
        ShowVolumeMarkersInternal(character, vol);
    }

    private static void ShowVolumeMarkersDelayed(Character character, CollisionVolume vol, float delaySec = 2f)
    {
        var task = new ShowMarkersDelayedTask(character, vol);
        TaskManager.Instance.Schedule(task, TimeSpan.FromSeconds(delaySec));
    }

    private class ShowMarkersDelayedTask(Character character, CollisionVolume volume) : GameTask
    {
        public override void Execute()
        {
            if (character?.IsOnline == true)
                ShowVolumeMarkersInternal(character, volume);
        }
    }

    private static void ShowVolumeMarkersInternal(Character character, CollisionVolume vol)
    {
        var markerPositions = new List<Vector3>();

        if (vol.Shape == CollisionShapeType.Polygon && vol.Vertices.Count > 0)
        {
            foreach (var v in vol.Vertices)
                markerPositions.Add(v);

            var spacing = vol.VolumeType == CollisionVolumeType.Wall ? 0.5f : 3f;

            var segmentCount = vol.VolumeType == CollisionVolumeType.Wall
                ? vol.Vertices.Count - 1
                : vol.Vertices.Count;

            for (var i = 0; i < segmentCount; i++)
            {
                var a = vol.Vertices[i];
                var b = vol.Vertices[(i + 1) % vol.Vertices.Count];
                AddEdgeMarkers(markerPositions, a, b, spacing);
            }
        }
        else if (vol.Shape == CollisionShapeType.Box || vol.Shape == CollisionShapeType.Ramp)
        {
            var rad = vol.RotationZ * MathF.PI / 180f;
            var cosR = MathF.Cos(rad);
            var sinR = MathF.Sin(rad);
            var hx = vol.Size.X;
            var hy = vol.Size.Y;
            var cx = vol.Center.X;
            var cy = vol.Center.Y;
            var cz = vol.Center.Z;

            var corners = new[]
            {
                new Vector3(cx + hx * cosR - hy * sinR, cy + hx * sinR + hy * cosR, cz),
                new Vector3(cx - hx * cosR - hy * sinR, cy - hx * sinR + hy * cosR, cz),
                new Vector3(cx - hx * cosR + hy * sinR, cy - hx * sinR - hy * cosR, cz),
                new Vector3(cx + hx * cosR + hy * sinR, cy + hx * sinR - hy * cosR, cz)
            };

            foreach (var c in corners)
                markerPositions.Add(c);

            for (var i = 0; i < 4; i++)
                AddEdgeMarkers(markerPositions, corners[i], corners[(i + 1) % 4], 3f);
        }

        SendMarkerDoodads(character, markerPositions);
    }

    private static void HandleHide(Character character)
    {
        if (!_spawnedMarkers.TryGetValue(character.ObjId, out var markerIds) || markerIds.Count == 0)
        {
            character.SendMessage("[ColVol] No debug markers to remove.");
            return;
        }

        var ids = markerIds.ToArray();
        for (var offset = 0; offset < ids.Length; offset += SCDoodadsRemovedPacket.MaxCountPerPacket)
        {
            var length = ids.Length - offset;
            var last = length <= SCDoodadsRemovedPacket.MaxCountPerPacket;
            var batchSize = last ? length : SCDoodadsRemovedPacket.MaxCountPerPacket;
            var batch = new uint[batchSize];
            Array.Copy(ids, offset, batch, 0, batchSize);
            character.SendPacket(new SCDoodadsRemovedPacket(last, batch));
        }

        var count = markerIds.Count;
        markerIds.Clear();
        character.SendMessage($"[ColVol] Removed {count} debug markers.");
    }

    private static void AddEdgeMarkers(List<Vector3> positions, Vector3 a, Vector3 b, float spacing)
    {
        var dist = Vector3.Distance(a, b);
        if (dist < spacing * 1.5f)
            return;

        var steps = (int)(dist / spacing);
        for (var s = 1; s < steps; s++)
        {
            var t = s / (float)steps;
            positions.Add(Vector3.Lerp(a, b, t));
        }
    }

    private static void SendMarkerDoodads(Character character, List<Vector3> positions)
    {
        if (positions.Count == 0)
            return;

        var doodads = new List<Doodad>();
        foreach (var pos in positions)
        {
            var doodad = DoodadManager.Instance.Create(character.ParentWorld, 0, DebugDoodadTemplateId, character);
            if (doodad == null)
            {
                character.SendMessage("[ColVol] Warning: Could not create marker doodad (template not loaded?).");
                return;
            }

            doodad.Transform = character.Transform.Clone();
            doodad.Transform.Local.SetPosition(pos.X, pos.Y, pos.Z);
            doodads.Add(doodad);
        }

        if (!_spawnedMarkers.TryGetValue(character.ObjId, out var markerList))
        {
            markerList = [];
            _spawnedMarkers[character.ObjId] = markerList;
        }

        foreach (var d in doodads)
            markerList.Add(d.ObjId);

        for (var i = 0; i < doodads.Count; i += SCDoodadsCreatedPacket.MaxCountPerPacket)
        {
            var count = Math.Min(doodads.Count - i, SCDoodadsCreatedPacket.MaxCountPerPacket);
            var batch = doodads.GetRange(i, count).ToArray();
            character.SendPacket(new SCDoodadsCreatedPacket(batch));
        }

        character.SendMessage($"[ColVol] Spawned {positions.Count} debug markers. Use '/cv hide' to remove.");
    }

    // ─────────────────────────────────────────────────────────
    // NPC Volume Bindings
    // ─────────────────────────────────────────────────────────

    private static void HandleBind(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr)
    {
        if (args.Length < 3)
        {
            character.SendMessage("[ColVol] Usage: /colvol bind <npcTemplateId> <volId1,volId2,...>");
            character.SendMessage("[ColVol] Example: /colvol bind 12345 1,2,5");
            return;
        }

        if (!uint.TryParse(args[1], out var templateId))
        {
            character.SendMessage($"[ColVol] Invalid NPC template ID: {args[1]}");
            return;
        }

        var volumeIds = new List<uint>();
        var parts = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (uint.TryParse(part.Trim(), out var volId))
                volumeIds.Add(volId);
            else
            {
                character.SendMessage($"[ColVol] Invalid volume ID: {part}");
                return;
            }
        }

        if (volumeIds.Count == 0)
        {
            character.SendMessage("[ColVol] No valid volume IDs provided.");
            return;
        }

        mgr.BindNpcToVolumes(templateId, volumeIds);
        character.SendMessage($"[ColVol] Bound NPC template {templateId} to volumes [{string.Join(", ", volumeIds)}]");
        character.SendMessage("[ColVol] Bound NPCs will ONLY use these volumes for height - terrain is ignored.");
    }

    private static void HandleUnbind(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr)
    {
        if (args.Length < 2)
        {
            character.SendMessage("[ColVol] Usage: /colvol unbind <npcTemplateId>");
            return;
        }

        if (!uint.TryParse(args[1], out var templateId))
        {
            character.SendMessage($"[ColVol] Invalid NPC template ID: {args[1]}");
            return;
        }

        if (!mgr.HasNpcBinding(templateId))
        {
            character.SendMessage($"[ColVol] NPC template {templateId} has no bindings.");
            return;
        }

        mgr.UnbindNpc(templateId);
        character.SendMessage($"[ColVol] Unbound NPC template {templateId}. It will use normal terrain/volume logic.");
    }

    private static void HandleBindings(Character character, IMessageOutput messageOutput,
        CollisionVolumeManager mgr)
    {
        var bindings = mgr.GetAllNpcBindings();
        var gridBindings = mgr.GetAllNpcGridBindings();

        if (bindings.Count == 0 && gridBindings.Count == 0)
        {
            character.SendMessage("[ColVol] No NPC bindings configured.");
            return;
        }

        if (bindings.Count > 0)
        {
            character.SendMessage($"[ColVol] {bindings.Count} NPC volume binding(s):");
            foreach (var kvp in bindings.OrderBy(k => k.Key))
            {
                character.SendMessage($"  NPC Template {kvp.Key} -> Volumes [{string.Join(", ", kvp.Value.OrderBy(v => v))}]");
            }
        }

        if (gridBindings.Count > 0)
        {
            character.SendMessage($"[ColVol] {gridBindings.Count} NPC grid binding(s):");
            foreach (var kvp in gridBindings.OrderBy(k => k.Key))
            {
                var gridLabel = kvp.Value.Contains("*") ? "ALL grids" : $"Grids [{string.Join(", ", kvp.Value)}]";
                character.SendMessage($"  NPC Template {kvp.Key} -> {gridLabel} (terrain fallback when no data)");
            }
        }
    }

    private static void HandleGridBind(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr)
    {
        if (args.Length < 2)
        {
            character.SendMessage("[ColVol] Usage: /colvol gridbind <npcTemplateId> [gridName]");
            character.SendMessage("[ColVol] Without gridName: binds NPC to ALL grids for the world.");
            character.SendMessage("[ColVol] With gridName: adds that grid to the NPC's binding list.");
            return;
        }

        if (!uint.TryParse(args[1], out var templateId))
        {
            character.SendMessage($"[ColVol] Invalid NPC template ID: {args[1]}");
            return;
        }

        var gridName = args.Length >= 3
            ? string.Join(" ", args, 2, args.Length - 2)
            : "*";

        if (mgr.IsNpcGridBound(templateId))
        {
            var existing = mgr.GetNpcGridBinding(templateId);
            character.SendMessage($"[ColVol] NPC template {templateId} already bound to [{string.Join(", ", existing)}]. Adding...");
        }

        if (gridName != "*" && mgr.GetHeightGridByName(gridName) == null)
        {
            character.SendMessage($"[ColVol] Warning: Grid '{gridName}' not found yet. Binding created anyway (grid may be scanned later).");
        }

        mgr.BindNpcToGrid(templateId, gridName);
        var updated = mgr.GetNpcGridBinding(templateId);
        var label = updated.Contains("*") ? "ALL grids" : $"grids [{string.Join(", ", updated)}]";
        character.SendMessage($"[ColVol] NPC template {templateId} now bound to {label}.");
        character.SendMessage("[ColVol] This NPC prefers grid/volume height over terrain.");
    }

    private static void HandleGridUnbind(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr)
    {
        if (args.Length < 2)
        {
            character.SendMessage("[ColVol] Usage: /colvol gridunbind <npcTemplateId> [gridName]");
            return;
        }

        if (!uint.TryParse(args[1], out var templateId))
        {
            character.SendMessage($"[ColVol] Invalid NPC template ID: {args[1]}");
            return;
        }

        if (!mgr.IsNpcGridBound(templateId))
        {
            character.SendMessage($"[ColVol] NPC template {templateId} has no grid binding.");
            return;
        }

        var specificGrid = args.Length >= 3 ? string.Join(" ", args, 2, args.Length - 2) : null;
        mgr.UnbindNpcFromGrid(templateId, specificGrid);

        if (specificGrid != null)
        {
            var remaining = mgr.GetNpcGridBinding(templateId);
            if (remaining != null && remaining.Count > 0)
                character.SendMessage($"[ColVol] Removed grid '{specificGrid}' from NPC template {templateId}. Remaining: [{string.Join(", ", remaining)}]");
            else
                character.SendMessage($"[ColVol] Removed grid '{specificGrid}' from NPC template {templateId}. No grids left - binding removed.");
        }
        else
        {
            character.SendMessage($"[ColVol] NPC template {templateId} unbound from all grids. Normal terrain logic restored.");
        }
    }

    // ─────────────────────────────────────────────────────────
    // Height Grid
    // ─────────────────────────────────────────────────────────

    private static void HandleGrid(Character character, string[] args, IMessageOutput messageOutput,
        CollisionVolumeManager mgr, string worldName)
    {
        if (args.Length < 2)
        {
            character.SendMessage("[ColVol] Usage: /colvol grid <start|finish|cancel|status|clear|list|delete|show>");
            character.SendMessage("  start [cellSize] [radius] [name] - Start scanning (name = grid name)");
            character.SendMessage("  list - Show all grids for this world");
            character.SendMessage("  delete <name> - Delete a named grid");
            return;
        }

        var action = args[1].ToLowerInvariant();
        switch (action)
        {
            case "start":
            {
                if (mgr.IsGridScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Already grid scanning! Use 'grid finish' or 'grid cancel' first.");
                    return;
                }

                if (mgr.IsDrawing(character.ObjId) || mgr.IsScanning(character.ObjId) || mgr.IsWallScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] You are in another mode. Finish or cancel it first.");
                    return;
                }

                var cellSize = 0.5f;
                var scanRadius = 1;
                var nameStartIdx = 2;

                if (args.Length > 2 && float.TryParse(args[2], out var parsedSize))
                {
                    cellSize = Math.Clamp(parsedSize, 0.25f, 5f);
                    nameStartIdx = 3;
                }

                if (args.Length > 3 && int.TryParse(args[3], out var parsedRadius))
                {
                    scanRadius = Math.Clamp(parsedRadius, 0, 5);
                    nameStartIdx = 4;
                }

                string gridName = null;
                if (args.Length > nameStartIdx)
                    gridName = string.Join(" ", args, nameStartIdx, args.Length - nameStartIdx).Trim('"', '\'');

                mgr.StartGridScan(character.ObjId, worldName, cellSize, scanRadius, gridName);

                var state = mgr.GetGridScanState(character.ObjId);
                var effectiveName = state?.GridName ?? worldName;

                var scanTask = new GridScanRecordTask(character);
                TaskManager.Instance.Schedule(scanTask, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

                if (state != null)
                    state.ScanTask = scanTask;

                character.SendMessage($"[ColVol] Grid scan started! Grid='{effectiveName}', CellSize={cellSize:F2}m, Radius={scanRadius}");
                character.SendMessage("[ColVol] Use '/cv grid finish' when done, or 'grid cancel' to abort.");

                var existingGrid = mgr.GetHeightGridByName(effectiveName);
                if (existingGrid != null && existingGrid.CellCount > 0)
                    character.SendMessage($"[ColVol] Existing grid '{effectiveName}' has {existingGrid.CellCount} cells - new data will be merged.");
                break;
            }
            case "finish":
            {
                if (!mgr.IsGridScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Not grid scanning. Use '/colvol grid start' first.");
                    return;
                }

                var scanState = mgr.GetGridScanState(character.ObjId);
                var gridName2 = scanState?.GridName ?? worldName;

                var (newCells, updatedCells, gapsFilled, totalCells) = mgr.FinishGridScan(character.ObjId);
                character.SendMessage($"[ColVol] Grid scan finished and saved!");
                character.SendMessage($"  Grid: '{gridName2}'");
                character.SendMessage($"  New cells: {newCells}");
                character.SendMessage($"  Updated cells: {updatedCells}");
                character.SendMessage($"  Gaps filled: {gapsFilled}");
                character.SendMessage($"  Total cells in grid: {totalCells}");
                character.SendMessage($"  File: heightgrid_{gridName2}.json");
                break;
            }
            case "cancel":
            {
                if (!mgr.IsGridScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Not grid scanning.");
                    return;
                }

                mgr.CancelGridScan(character.ObjId);
                character.SendMessage("[ColVol] Grid scan cancelled. Data already merged is kept (use 'grid clear' to wipe).");
                break;
            }
            case "status":
            {
                var statusName = args.Length > 2
                    ? string.Join(" ", args, 2, args.Length - 2).Trim('"', '\'')
                    : null;

                if (statusName != null)
                {
                    var grid = mgr.GetHeightGridByName(statusName);
                    if (grid == null || grid.CellCount == 0)
                    {
                        character.SendMessage($"[ColVol] No height grid '{statusName}' found.");
                        break;
                    }

                    character.SendMessage($"[ColVol] Height grid '{grid.GridName}' (world: {grid.WorldName}):");
                    character.SendMessage($"  Cells: {grid.CellCount}, Cell size: {grid.CellSize:F2}m");
                    character.SendMessage($"  Last updated: {grid.LastUpdated}");
                    var multiFloor = grid.Cells.Values.Count(f => f.Count > 1);
                    if (multiFloor > 0)
                        character.SendMessage($"  Multi-floor cells: {multiFloor}");
                }
                else
                {
                    var gridNames = mgr.GetGridNamesForWorld(worldName);
                    if (gridNames.Count == 0)
                    {
                        character.SendMessage($"[ColVol] No height grids for {worldName}.");
                        break;
                    }

                    character.SendMessage($"[ColVol] {gridNames.Count} grid(s) for {worldName}:");
                    foreach (var gn in gridNames)
                    {
                        var g = mgr.GetHeightGridByName(gn);
                        if (g != null)
                            character.SendMessage($"  '{gn}': {g.CellCount} cells, size={g.CellSize:F2}m, updated={g.LastUpdated}");
                    }
                }

                if (mgr.IsGridScanning(character.ObjId))
                {
                    var state = mgr.GetGridScanState(character.ObjId);
                    if (state != null)
                        character.SendMessage($"  Scanning '{state.GridName}': +{state.NewCells} new, {state.UpdatedCells} updated this session");
                }
                break;
            }
            case "list":
            {
                var gridNames = mgr.GetGridNamesForWorld(worldName);
                if (gridNames.Count == 0)
                {
                    character.SendMessage($"[ColVol] No height grids for {worldName}.");
                    break;
                }

                character.SendMessage($"[ColVol] {gridNames.Count} grid(s) for {worldName}:");
                foreach (var gn in gridNames)
                {
                    var g = mgr.GetHeightGridByName(gn);
                    if (g != null)
                        character.SendMessage($"  '{gn}': {g.CellCount} cells, size={g.CellSize:F2}m");
                }
                break;
            }
            case "clear":
            {
                if (mgr.IsGridScanning(character.ObjId))
                {
                    character.SendMessage("[ColVol] Stop scanning first (grid finish/cancel).");
                    return;
                }

                var clearName = args.Length > 2
                    ? string.Join(" ", args, 2, args.Length - 2).Trim('"', '\'')
                    : null;

                if (clearName != null)
                {
                    var grid = mgr.GetHeightGridByName(clearName);
                    if (grid == null || grid.CellCount == 0)
                    {
                        character.SendMessage($"[ColVol] No grid '{clearName}' found or already empty.");
                        break;
                    }

                    var count = grid.CellCount;
                    grid.Cells.Clear();
                    mgr.SaveHeightGrid(clearName);
                    character.SendMessage($"[ColVol] Cleared {count} cells from grid '{clearName}'.");
                }
                else
                {
                    var gridNames = mgr.GetGridNamesForWorld(worldName);
                    var totalCleared = 0;
                    foreach (var gn in gridNames)
                    {
                        var g = mgr.GetHeightGridByName(gn);
                        if (g != null && g.CellCount > 0)
                        {
                            totalCleared += g.CellCount;
                            g.Cells.Clear();
                            mgr.SaveHeightGrid(gn);
                        }
                    }
                    character.SendMessage($"[ColVol] Cleared {totalCleared} cells from {gridNames.Count} grid(s) in {worldName}.");
                }
                break;
            }
            case "delete":
            {
                if (args.Length < 3)
                {
                    character.SendMessage("[ColVol] Usage: /colvol grid delete <name>");
                    break;
                }

                var delName = string.Join(" ", args, 2, args.Length - 2).Trim('"', '\'');
                if (mgr.DeleteHeightGrid(delName))
                    character.SendMessage($"[ColVol] Grid '{delName}' deleted (file removed).");
                else
                    character.SendMessage($"[ColVol] Grid '{delName}' not found.");
                break;
            }
            case "show":
            {
                ShowGridCells(character, mgr, worldName, args);
                break;
            }
            default:
                character.SendMessage("[ColVol] Usage: /colvol grid <start|finish|cancel|status|clear|list|delete|show>");
                break;
        }
    }

    /// <summary>
    /// Visualize height grid cells near the player as doodad markers.
    /// </summary>
    private static void ShowGridCells(Character character, CollisionVolumeManager mgr,
        string worldName, string[] args)
    {
        var radius = 30f;
        var nameStartIdx = 2;

        if (args.Length > 2 && float.TryParse(args[2], out var parsedRadius))
        {
            radius = Math.Clamp(parsedRadius, 5f, 100f);
            nameStartIdx = 3;
        }

        string specificGridName = null;
        if (args.Length > nameStartIdx)
            specificGridName = string.Join(" ", args, nameStartIdx, args.Length - nameStartIdx).Trim('"', '\'');

        var gridsToShow = new List<HeightGridFile>();
        if (specificGridName != null)
        {
            var g = mgr.GetHeightGridByName(specificGridName);
            if (g != null)
                gridsToShow.Add(g);
            else
            {
                character.SendMessage($"[ColVol] Grid '{specificGridName}' not found.");
                return;
            }
        }
        else
        {
            foreach (var gn in mgr.GetGridNamesForWorld(worldName))
            {
                var g = mgr.GetHeightGridByName(gn);
                if (g != null && g.CellCount > 0)
                    gridsToShow.Add(g);
            }
        }

        if (gridsToShow.Count == 0)
        {
            character.SendMessage($"[ColVol] No height grid data for {worldName}.");
            return;
        }

        var pos = character.Transform.World.Position;
        var radiusSq = radius * radius;
        var markerPositions = new List<Vector3>();

        foreach (var grid in gridsToShow)
        {
            var cellSize = grid.CellSize;

            foreach (var (key, floors) in grid.Cells)
            {
                var (cx, cy) = HeightGridFile.ParseCellKey(key);

                var wx = (cx + 0.5f) * cellSize;
                var wy = (cy + 0.5f) * cellSize;

                var dx = wx - pos.X;
                var dy = wy - pos.Y;
                if (dx * dx + dy * dy > radiusSq)
                    continue;

                foreach (var floorZ in floors)
                {
                    markerPositions.Add(new Vector3(wx, wy, floorZ));
                }
            }
        }

        if (markerPositions.Count == 0)
        {
            character.SendMessage($"[ColVol] No grid cells within {radius:F0}m of your position.");
            return;
        }

        const int maxMarkers = 5000;
        if (markerPositions.Count > maxMarkers)
        {
            character.SendMessage($"[ColVol] Warning: {markerPositions.Count} markers found, showing closest {maxMarkers}.");
            markerPositions = markerPositions
                .OrderBy(p => (p.X - pos.X) * (p.X - pos.X) + (p.Y - pos.Y) * (p.Y - pos.Y))
                .Take(maxMarkers)
                .ToList();
        }

        SendMarkerDoodads(character, markerPositions);
        var gridLabel = specificGridName != null ? $"grid '{specificGridName}'" : $"{gridsToShow.Count} grid(s)";
        character.SendMessage($"[ColVol] Showing {markerPositions.Count} markers from {gridLabel} within {radius:F0}m.");
    }
}
