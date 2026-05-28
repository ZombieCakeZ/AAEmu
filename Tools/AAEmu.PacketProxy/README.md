# AAEmu PacketProxy

A tiny TCP proxy that sits between the 2.0.1.7 game client and any AA server,
logging every byte in both directions with AA-wire-format decode (opcode, level,
hex, ASCII). The point: when something is broken on the Vanilla branch and the
old 2.0.1.7 alpha emulator works, run both side-by-side through this proxy and
diff the packet traces to find the exact field that drifted.

## Build / get the binary

A single-file Windows .exe is published at:

```
D:\Vanilla\publish\PacketProxy-win-x64\aaemu-packetproxy.exe
```

It's a self-contained .NET 10 build (~37 MB, no runtime install needed). Copy
it anywhere — onto a USB stick, into the AA server folder, wherever.

Rebuild from source:

```powershell
dotnet publish Tools/AAEmu.PacketProxy/AAEmu.PacketProxy.csproj `
    -c Release -r win-x64 -o publish/PacketProxy-win-x64
```

For Linux: swap `win-x64` for `linux-x64`.

## Usage

```
aaemu-packetproxy --listen <ip:port> --upstream <host:port> [--log <file>] [--label <name>]
```

Examples for AAEmu's two server endpoints:

```
:: Game server (world traffic, opcodes 0x000+ on the DD/05 frame)
aaemu-packetproxy --listen 0.0.0.0:1240 --upstream 127.0.0.1:1239 --log game.log --label GAME

:: Stream server (the simpler [len][opcode][body] frame on port 1238)
aaemu-packetproxy --listen 0.0.0.0:1241 --upstream 127.0.0.1:1238 --log stream.log --label STREAM
```

Then point your client at `<proxy-ip>:1240` for the game server and `1241` for
the stream server (`archeageus.ini` server entries on the client side).

The console shows a one-line summary per packet. The full hex + payload-only
hex + ASCII go into the log file so you can grep / diff later.

## Recommended workflow against the alpha-emulator reference

1. **Stand up the old 2.0.1.7 alpha emulator** somewhere (it lives at
   `D:\2.0.1.7\AAEmu-client_version-2.0_client_-2015-09-14-\AAEmu-client_version-2.0_client_-2015-09-14-`).
   The alpha works end-to-end against the 2.0.1.7 client.
2. **Run the proxy against the alpha**:
   ```
   aaemu-packetproxy --listen 0.0.0.0:1240 --upstream <alpha-ip>:1239 --log alpha-game.log --label ALPHA
   aaemu-packetproxy --listen 0.0.0.0:1241 --upstream <alpha-ip>:1238 --log alpha-stream.log --label ALPHA
   ```
3. **Point the client at the proxy** and reach the broken screen (character
   creation, world entry, whatever). Stop.
4. **Switch upstream to Vanilla** and replay the same client steps with the
   same proxy:
   ```
   aaemu-packetproxy --listen 0.0.0.0:1240 --upstream <vanilla-ip>:1239 --log vanilla-game.log --label VANILLA
   ```
5. **Diff the two logs**. The first place they differ in length or content
   for the same opcode is the field drift that crashes the client.

## What the log looks like

```
[17:30:01.123] [session 1] [GAME] S->C WORLD opcode=0x000(5) name="X2EnterWorldResponsePacket" len=268
  hex   : 09 01 DD 05 00 00 ...
  body  : ...

[17:30:01.140] [session 1] [GAME] C->S WORLD opcode=0x0DE(1) name="CSAesXorKeyPacket" len=270
  hex   : 0F 01 DD 01 00 00 DE 00 ...
  body  : ...
```

Body is bytes-after-the-header so you can compare payloads directly without
tripping over level / opcode differences.

## Limitations

- Does NOT decrypt Level-5 encrypted payloads. The hex you see for Level 5 is
  the wire-encrypted bytes. Use the alpha source's `EncryptionManager` keys to
  decrypt offline if you need plaintext, or wrap the alpha emulator in the
  middle (the alpha logs decoded packets to its own log).
- Detects packet boundaries via the length prefix. If the client ever sends a
  malformed or unsupported frame, the assembler stops draining and bytes pile
  up. The exact bytes are still in the log via the connection event.
- Single-threaded per session. Fine for a single test client; not a production
  load-bearing thing.
