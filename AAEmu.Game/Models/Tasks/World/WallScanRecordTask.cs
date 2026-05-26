using System;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Models.Tasks.World;

/// <summary>
/// Repeating task that records a character's position for the wall scanner.
/// Runs every 200ms while the admin is in wall scan mode.
/// Self-cancels when scanning stops or character goes offline.
/// </summary>
public class WallScanRecordTask(Character character) : Task
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Character _character = character;
    private int _tickCount;

    public override void Execute()
    {
        try
        {
            _tickCount++;

            if (_character?.IsOnline != true)
            {
                Logger.Warn($"WallScanRecordTask: Character offline or null (tick #{_tickCount}). Cancelling.");
                Cancel();
                return;
            }

            var mgr = CollisionVolumeManager.Instance;
            if (!mgr.IsWallScanning(_character.ObjId))
            {
                Logger.Debug($"WallScanRecordTask: No longer wall scanning (ObjId={_character.ObjId}, tick #{_tickCount}). Cancelling.");
                Cancel();
                return;
            }

            var pos = _character.Transform.World.Position;

            // Log first 3 ticks (immediate diagnosis) and then every 25 ticks (~5 seconds)
            if (_tickCount <= 3 || _tickCount % 25 == 0)
            {
                var state = mgr.GetWallScanState(_character.ObjId);
                Logger.Debug($"WallScanRecordTask: tick #{_tickCount}, ObjId={_character.ObjId}, " +
                             $"pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}), points={state?.Points.Count ?? -1}, " +
                             $"taskId={Id}, cancelled={Cancelled}");
            }

            mgr.RecordWallScanPosition(_character.ObjId, pos.X, pos.Y, pos.Z);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"WallScanRecordTask: Exception in Execute (tick #{_tickCount}, ObjId={_character?.ObjId}). Cancelling.");
            Cancel();
        }
    }
}
