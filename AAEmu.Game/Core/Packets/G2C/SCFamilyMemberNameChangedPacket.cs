using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C
{
    public class SCFamilyMemberNameChangedPacket : GamePacket
    {
        private readonly uint _familyId;
        private readonly uint _charId;
        private readonly string _newName;
        
        public SCFamilyMemberNameChangedPacket(uint familyId, uint charId, string newName) : base(0x034, 1) // TODO 1.0 opcode: 0x030
        {
            _familyId = familyId;
            _charId = charId;
            _newName = newName;
        }

        public override PacketStream Write(PacketStream stream)
        {
            stream.Write(_familyId);
            stream.Write(_charId);
            stream.Write(_newName);
            return stream;
        }
    }
}
