# Phase 2.1 — Cipher RE status

## Static analysis is blocked

The 2.0.1.7 client (`D:\2.0.1.7\bin32\archeage.exe`, 2.27 MB) and its CryEngine DLLs (`cry*.dll`, `compressati2.dll`, etc.) are **packed**:

```
$ strings D:/2.0.1.7/bin32/archeage.exe | wc -l
0
$ strings D:/2.0.1.7/bin32/compressati2.dll | wc -l
0
$ strings D:/2.0.1.7/bin32/cry3dengine.dll | wc -l
0
```

A 2.27 MB Windows binary should yield thousands of strings; zero means the entire string table is encrypted, compressed, or stripped. Common AA-era packers: Themida, EnigmaProtector, SafeNet. Without unpacking, neither `compact.sqlite`, `sqlite3_open_v2`, AES constants, nor key material are visible in the executable. Static decompilation in Ghidra / IDA will hit the same wall — the imports are obfuscated and the code at the entry point is the unpacker stub.

## Realistic paths forward

### Option A: Dynamic dump of the unpacked process

1. Boot the client to the login screen (it patches itself in memory at this point).
2. Attach a debugger (x32dbg / WinDbg) and use Scylla or a dedicated Themida unpacker to dump the unpacked image to disk.
3. Re-run the string + decompile pass against the dumped image. SQLite-related strings (`compact.sqlite`, `sqlite3_open`, etc.) and the cipher routines should now be visible.
4. Set a breakpoint on `CreateFileA` / `CreateFileW` filtered by `compact.sqlite`, step through the read path, identify the buffer where decryption happens, dump the key from registers.

Effort: half day to a day for someone comfortable with PE unpacking.

### Option B: Hook-based decryption oracle

Skip static analysis entirely. Inject a DLL into the running client that hooks file I/O, captures every read from `compact.sqlite`, and writes the *decrypted* buffer to a side file. The client itself acts as the decryptor.

Pros: no key extraction, no algorithm guess.
Cons: needs the client to actually run the game data (login + character select touches enough tables that most rows get read; world load fetches the rest); produces a file that's a CONCATENATION of decrypted reads, not a clean SQLite (requires reassembly).

Effort: a day to write the hook DLL + a day to merge captures into a usable file.

### Option C: Community decryptor

Several AAEmu community tools historically solved this for the 2.0.1.7 client (Trion era):
- `AAEmu compact-decrypt` (various forks)
- `aatools` (older project)
- `compactdecode` (one-off scripts in modders' shares)

We don't have any of these on disk (`find D:/ -name '*decrypt*'` returns nothing). If the user has access to one — or knows where the AAEmu Discord / GitHub Pages host it — that's the fast path. Drop the tool / source into `Tools/External/` and we wrap it in our pipeline.

### Option D: Use 1.2 data, defer 2.0 decryption indefinitely

The 1.2 `compact.sqlite3` already on disk is 119 MB and contains everything the 2.0.1.7 server actually needs for booting + login + world load. The 2.0-specific deltas (new items, new skills, new housing decorations) we backfill via JSON overlays at `Data/Overlays/2_0_1_7/`.

This is the **pragmatic choice for the immediate next steps** — Phase 3 (schema additions) and Phase 4 (protocol layer) do not need a decrypted 2.0 client db. We can have a working 2.0.1.7 server-against-1.2-data within a few days; decrypting the client db is an enhancement, not a prerequisite.

## Recommendation

Pursue **Option D** for immediate progress + **Option A or C** in parallel when time allows. Phase 3 and Phase 4 unblock without the decryptor; Phase 1.3's "real 2.0 schema diff" deliverable stays open until the decryptor lands.

## Tool scaffolding (committed)

`Tools/AAEmu.ClientDb/` contains the project skeleton — a .NET console project with `decrypt` / `encrypt` / `diff` subcommands, all currently returning `NotImplemented`. Wiring the cipher in is a single-file change in `Cipher.cs` once we know the algorithm + key. The pipeline (open file, identify header, drive cipher, write output) is already in place.

Acceptance test: given the 1.2 `compact.sqlite3` (which we have) and its known plaintext, a unit test in `AAEmu.UnitTests/Tools/CipherRoundTripTests.cs` should encrypt-then-decrypt-then-byte-equal once the 1.2 cipher is supplied. Then point at the 2.0 cipher and rerun.

## File-format sanity reminders

When the cipher works, the first 16 bytes of the decrypted file must be:

```
53 51 4c 69 74 65 20 66 6f 72 6d 61 74 20 33 00     "SQLite format 3\0"
```

…and offset 16 must be a valid SQLite page-size word (`04 00` or `10 00` for 1024 / 4096). If those bytes don't match, the cipher or key is wrong.
