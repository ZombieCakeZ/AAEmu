using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Models.Tasks.World;

/// <summary>
/// Repeating task that records a character's position into the height grid.
/// Runs every 50ms while the admin is in grid scan mode.
/// Self-cancels when scanning stops or character goes offline.
/// </summary>
public class GridScanRecordTask(Character character) : Task
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
        if (!mgr.IsGridScanning(_character.ObjId))
        {
            Cancel();
            return;
        }

        var pos = _character.Transform.World.Position;
        mgr.RecordGridPosition(_character.ObjId, pos.X, pos.Y, pos.Z);
    }
}
