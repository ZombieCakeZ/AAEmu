using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Models.Tasks.World;

/// <summary>
/// Repeating task that records a character's position for the floor perimeter scanner.
/// Runs every 200ms while the admin is in floor scan mode.
/// Self-cancels when scanning stops or character goes offline.
/// </summary>
public class FloorPerimeterScanRecordTask(Character character) : Task
{
    private readonly Character _character = character;

    public override void Execute()
    {
        if (_character?.IsOnline != true)
        {
            Cancel();
            return;
        }

        var mgr = CollisionVolumeManager.Instance;
        if (!mgr.IsFloorScanning(_character.ObjId))
        {
            Cancel();
            return;
        }

        var pos = _character.Transform.World.Position;
        mgr.RecordFloorScanPosition(_character.ObjId, pos.X, pos.Y, pos.Z);
    }
}
