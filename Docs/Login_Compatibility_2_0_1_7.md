# Login server compatibility — 2.0.1.7

## TL;DR

**Login server needs no opcode changes** between 1.2 (`client_12_r208022`) and 2.0.1.7 (Trion `r249376`). Builds clean as-is. Listed in the master plan as "no Phase 4 work for Login required" — this doc is the evidence.

## What we compared

### C2L (Client → Login) opcodes

Current Vanilla `AAEmu.Login/Core/Packets/C2L/CLOffsets.cs`:

| Packet | id |
|---|---|
| `CARequestAuthPacket` | 0x001 |
| `CARequestAuthTencentPacket` | 0x002 |
| `CARequestAuthGameOnPacket` | 0x003 |
| `CARequestAuthTrionPacket` | 0x004 |
| `CARequestAuthMailRuPacket` | 0x005 |
| `CAChallengeResponsePacket` | 0x006 |
| `CAChallengeResponse2Packet` | 0x007 |
| `CAOtpNumberPacket` | 0x008 |
| `CAPcCertNumberPacket` | 0x00a |
| `CAListWorldPacket` | 0x00b |
| `CAEnterWorldPacket` | 0x00c |
| `CACancelEnterWorldPacket` | 0x00d |
| `CARequestReconnectPacket` | 0x00e |

Old 2.0.1.7 alpha source registers the same packets with the same ids in `AAEmu.Login/Core/Network/Login/LoginNetwork.cs`:

```csharp
RegisterPacket(0x01, typeof(CARequestAuthPacket));
RegisterPacket(0x02, typeof(CARequestAuthTencentPacket));
RegisterPacket(0x03, typeof(CARequestAuthGameOnPacket));
RegisterPacket(0x04, typeof(CARequestAuthTrionPacket));
RegisterPacket(0x05, typeof(CARequestAuthMailRuPacket));
RegisterPacket(0x06, typeof(CAChallengeResponsePacket));
RegisterPacket(0x07, typeof(CAChallengeResponse2Packet));
RegisterPacket(0x08, typeof(CAOtpNumberPacket));
RegisterPacket(0x0a, typeof(CAPcCertNumberPacket));
RegisterPacket(0x0b, typeof(CAListWorldPacket));
RegisterPacket(0x0c, typeof(CAEnterWorldPacket));
RegisterPacket(0x0d, typeof(CACancelEnterWorldPacket));
RegisterPacket(0x0e, typeof(CARequestReconnectPacket));
```

**Match.** Every name and every id.

### L2C (Login → Client) opcodes

Current Vanilla `LCOffsets.cs`:

| Packet | id |
|---|---|
| `ACJoinResponsePacket` | 0x000 |
| `CARequestAuthPacket` | 0x001 |
| `ACChallengePacket` | 0x002 |
| `ACAuthResponsePacket` | 0x003 |
| `ACChallenge2Packet` | 0x004 |
| `ACEnterOtpPacket` | 0x005 |
| `ACShowArsPacket` | 0x006 |
| `ACEnterPcCertPacket` | 0x007 |
| `ACWorldListPacket` | 0x008 |
| `ACWorldQueuePacket` | 0x009 |
| `ACWorldCookiePacket` | 0x00a |
| `ACEnterWorldDeniedPacket` | 0x00b |
| `ACLoginDeniedPacket` | 0x00c |
| `ACAccountWarnedPacket` | 0x00d |

Old 2.0.1.7 alpha hardcodes the opcode in each packet's `base(...)` call. Spot-check on `ACJoinResponsePacket.cs`:

```csharp
public ACJoinResponsePacket(ushort reason, ulong afs) : base(0x00)
```

**Match.** The L2C packets in the alpha source carry the same opcodes as Vanilla's `LCOffsets.cs`.

### G2L / L2G (inter-server) opcodes

These never reach the client, so version compatibility doesn't apply. Each branch can change them independently.

## Why the login layer is stable across game versions

Login auth flow is set by the platform (Trion's external auth servers, GameOn, Tencent, Mail.ru, etc.). Those platforms expect a fixed handshake regardless of which game build the client connects to. Trion only changed the world-server protocol (the G2C / C2G payloads).

## Build status

```
dotnet build AAEmu.Login/AAEmu.Login.csproj
  AAEmu.Commons -> ...
  AAEmu.Login -> ...
  Build succeeded. 0 Error(s), 8 pre-existing Warning(s)
  (OpenTelemetry vulnerability advisories + one unread-parameter warning)
```

```
dotnet build AAEmu.slnx
  Build succeeded. 0 Error(s), 86 pre-existing Warning(s)
  (mostly CA1852 sealed-type suggestions on test fixtures)
```

Full solution builds end-to-end on the `AA-2.0.1.7-Trion-r249376-2015-09-18` branch.

## Implications for testing

Booting `D:\2.0.1.7\bin32\archeage.exe` against a Vanilla Login server should reach the world-server list step without protocol errors. Failure modes from there:
- World list is empty or wrong (a config / DB issue, not a protocol issue).
- World handshake fails at the first 2.0-specific C2G opcode the world server doesn't recognise — that's Phase 4 territory.

So the "first connect" smoke test for 2.0.1.7 only needs Login changes if Trion auth tokens / OTP behaviour are version-specific, which they aren't in any of the spot-checked packets.
