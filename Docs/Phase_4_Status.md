# Phase 4 — World Protocol Layer status

Branch: `AA-2.0.1.7-Trion-r249376-2015-09-18`
Last update: 2026-05-28

## What is done

| Change | Commit |
|---|---|
| `SCOffsets.cs` rewritten with 2.0.1.7 values (659 packets) + 41 sentinels for 1.2-only | `4d2d7752` |
| `CSOffsets.cs` rewritten with 2.0.1.7 values (360 packets) + 12 sentinels for 1.2-only | `4d2d7752` |
| `Config.json` for Game points to `aaemu_game2` | `183b9fed` |
| `Config.json` for Login points to `aaemu_login2` | `172a366e` |
| Build status | `0 errors / 0 warnings` |

Every existing packet class compiles unchanged — the constants are referenced by name (e.g. `SCOffsets.SCEnterWorldPacket`), so the renumbering propagates implicitly. No call-site edits required.

## What is NOT done yet

### 1. Stub classes for the 183 new G2C + 114 new C2G packets

The opcode constants exist (`SCOffsets.SCMateXpChangedPacket`, etc.) but no class registers them with the dispatcher and no manager emits them. Implications:

- **C2G new packets**: when the 2.0 client sends one (e.g. `CSStartActabilityPacket`), the server logs `unknown opcode` and ignores it. The client may stall or disconnect depending on the packet. We recommend implementing them on demand as the user identifies which ones block their flow.
- **G2C new packets**: feature managers that need to emit these have to be wired up. Until they are, the 2.0 client won't see the corresponding UI updates (mate XP, actability progress, etc.).

### 2. Sentinel range packets

The 41 G2C + 12 C2G packets that exist in 1.2 but not 2.0 carry opcodes in `0xFF00..0xFF28`. If the server emits one of those to a 2.0 client, the client logs and drops it (unknown opcode). This is the intended behavior — but feature gates in managers should still avoid generating them in 2.0 mode for cleanliness. Recommended action when one of these is observed in logs:

```csharp
if (AppConfiguration.Instance.Compatibility.ProtocolVersion == "2_0_1_7")
    return;  // not sent on 2.0
```

…inside the manager that calls the offending `Send*` method.

### 3. Encryption / handshake

Current Vanilla `GamePacket.cs` already emits the `0xDD` + Level magic, so the wire frame matches 2.0.1.7. **However**, the encryption ratchet differs:

- Vanilla: Level 1 plaintext only. No `AAEmu.Commons.Cryptography.EncryptionManager`, no `CSAesXorKeyPacket` handler.
- 2.0.1.7 alpha: AES-128 + XOR ratchet, initialised by the client sending `CSAesXorKeyPacket` after the join handshake. Ratchets the SC message counter via `EncryptionManager.GetSCMessageCount()`.

**Whether the 2.0.1.7 client accepts Level 1 plaintext or demands Level 5 encryption is unknown** until we observe its behavior at connect. Two scenarios:

a. **Client accepts Level 1**: no further work needed for the connection itself; we can defer the EncryptionManager port indefinitely.
b. **Client demands Level 5**: we need to port `AAEmu.Commons.Cryptography.EncryptionManager` from the alpha source (`D:\2.0.1.7\AAEmu-client_version-2.0_client_-2015-09-14-\...\AAEmu.Commons\Cryptography\EncryptionManager.cs`, 351 lines), register `CSAesXorKeyPacket` as a real handler, and bump `GamePacket.cs` to emit Level 5 frames once the handshake completes.

The alpha's implementation includes RSA key-pair exchange (`xorConstRaw` derived from `keys.RsaKeyPair.Decrypt`, then `keys.AesKey = keys.RsaKeyPair.Decrypt(aesKeyEncrypted)`). Porting is mechanical but touches the protocol-handler hot path.

## Ready for test

Once the 2.0 MySQL databases are populated (`aaemu_game2` + `aaemu_login2`):

1. Start `aaemu-clientdb` Login server — `dotnet run --project AAEmu.Login`.
2. Start Game server — `dotnet run --project AAEmu.Game`.
3. Launch `D:\2.0.1.7\bin32\archeage.exe` patched to point at `localhost`.
4. Observe at Login server log:
   - Should accept the connection.
   - Should respond to `CARequestAuthTrionPacket` and emit `ACAuthResponsePacket`.
   - Should send `ACWorldListPacket`.
5. Observe at Game server log:
   - Either the client successfully joins (Level 1 path) — encryption deferral was correct.
   - Or the client disconnects after the initial handshake / sends garbage opcodes — encryption needed; port `EncryptionManager` from the alpha source.

Logs to grep for:
- `Invalid target` / `unknown opcode` / `Decode error` — packet-level issue
- `Cannot decrypt` / `bad hash` / `length mismatch` — encryption-level issue
- Sentinel-range opcodes (`0xFF00..0xFF28`) being emitted — a feature gate is missing

## Suggested next commits (once test results come in)

- **If encryption is needed**: a single commit porting `EncryptionManager` + `CSAesXorKeyPacket` handler + Level-5 path in `GamePacket.cs`.
- **If specific C2G packets stall the client**: a series of small commits, one per packet, stubbing the handler to log + ack until real logic is wired.
- **If specific G2C packets are needed for the client to advance**: stubbed emitters in the appropriate manager with TODO markers.

We'll iterate from the user's actual log output rather than over-implementing speculatively.
