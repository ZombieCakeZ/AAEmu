using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Models.Tasks.World;

/// <summary>
/// Repeating task that records a character's position for the collision volume floor scanner.
/// Runs every 500ms while the admin is in scan mode.
/// Self-cancels when scanning stops or character goes offline.
/// </summary>
public class FloorScanRecordTask(Character character) : Task
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
        if (!mgr.IsScanning(_character.ObjId))
        {
            Cancel();
            return;
        }

        var pos = _character.Transform.World.Position;
        mgr.RecordScanPosition(_character.ObjId, new System.Numerics.Vector3(pos.X, pos.Y, pos.Z));
    }
}
