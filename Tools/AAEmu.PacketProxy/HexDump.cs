using System.Text;

namespace AAEmu.PacketProxy;

internal static class HexDump
{
    public static string Format(byte[] data)
    {
        if (data.Length == 0) return string.Empty;
        var sb = new StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
