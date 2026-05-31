using System;
using System.Linq;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// Admin command for inspecting <see cref="MeshCollisionManager"/> state. Auto-registered
/// by ScriptReflector — drop the file in Scripts/Commands and it picks up on next boot.
/// Default access level 100 (GM-only) via AccessLevelManager fallback.
///
/// Usage:
///   /meshstats              — overall stats + per-world counts + top-5 cells in current world
///   /meshstats reload       — reload Data/CollisionMeshes/ from disk
/// </summary>
public class CmdMeshStats : ICommand
{
    public string[] CommandNames { get; set; } = ["meshstats"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp() => "[reload]";

    public string GetCommandHelpText()
        => "Displays MeshCollisionManager statistics: templates, total triangles, instances per world, "
           + "top-5 busiest 64m cells in current world, and estimated memory footprint. "
           + "Optional 'reload' arg reloads meshcache + editor_*.json from disk.";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        var mgr = MeshCollisionManager.Instance;

        if (args.Length > 0 && args[0].Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            mgr.Reload();
            character.SendMessage($"[MeshStats] Reloaded. {mgr.TemplateCount} templates, "
                                  + $"{mgr.InstanceCountsPerWorld.Values.Sum()} instances total.");
            return;
        }

        character.SendMessage($"[MeshStats] Templates: {mgr.TemplateCount:N0}   "
                              + $"Total triangles: {mgr.TotalTriangleCount:N0}");
        character.SendMessage($"[MeshStats] Approx memory: {mgr.EstimateMemoryBytes() / (1024 * 1024):N0} MB");

        var perWorld = mgr.InstanceCountsPerWorld;
        if (perWorld.Count == 0)
        {
            character.SendMessage("[MeshStats] No instance files loaded.");
            return;
        }

        character.SendMessage($"[MeshStats] {perWorld.Count} world(s) loaded:");
        foreach (var kvp in perWorld.OrderByDescending(k => k.Value))
            character.SendMessage($"  {kvp.Key}: {kvp.Value:N0} instances");

        // Resolve the caller's current world name via the same helper CollisionVolumeManager uses
        // (CollisionVolumeManager.GetWorldNameFromId), so /meshstats agrees with /cv on world labels.
        var worldName = character.Transform != null
            ? CollisionVolumeManager.GetWorldNameFromId(character.Transform.WorldId)
            : null;
        if (string.IsNullOrEmpty(worldName)) return;

        character.SendMessage($"[MeshStats] Top 5 busiest 64m cells in '{worldName}':");
        foreach (var (cell, count) in mgr.TopCellsByInstanceCount(worldName, 5))
            character.SendMessage($"  ({cell.RX},{cell.RY}): {count:N0} instances");
    }
}
