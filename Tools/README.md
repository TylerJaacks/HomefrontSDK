# HomefrontSDK tools

Python scripts for working with the server, and the engine reference they and the SDK are built on.

## Server tools

| Script | What it does |
|---|---|
| `hfbridge.py` | Talks to the SDK's bridge (127.0.0.1:27040): `info`, `command "!bots add 4"`, `objects class=Pawn`, `object ptr=...`, `set ...`, `logs` |
| `hfrcon.py` | The server's admin port (TCP 27010): `--password <AdminPassword> "admin NextMap"`; `--listen` follows its events |

HomefrontBots' `build_navs.py` uses `hfrcon.py`.

## Reversing tools

Everything below reads a **running** `HFDedicated.exe` (2011-08-10 build, UE3 v576 / licensee 100) with
`ReadProcessMemory`. Nothing writes to, suspends or injects into the server. The server exe has no ASLR,
so the addresses below are fixed for this build.

Run them with the server up, from this folder; they write into `out/` (not checked in):

| Script | What it does |
|---|---|
| `proc.py` | Read-only access to a 32-bit process from 64-bit Python |
| `ue3.py` | The object model: names, objects, paths; all offsets and flag tables. Call `UE3(...).index()` before bulk work |
| `dump_sdk.py` | Writes `out/HFDedicated/sdk/<Package>.h`, `sdk.json`, `natives.csv`, `classes.txt` (~5 s); with `--api` also `HomefrontSDK/api-reference.json`, the input of the SDK's generator |
| `dump_script.py` | Writes `out/HFDedicated/script/<Package>/<Class>.txt`: every function/state as pseudo-UnrealScript (~4 s) |
| `bytecode.py` | UnrealScript bytecode disassembler for v576 |
| `validate_bytecode.py` | Disassembles every script and checks each consumes exactly its bytes (currently 15593/15593) |
| `show_script.py Class.Function` | Hex + disassembly of one function |

## Engine globals and functions (server exe)

| | Address | Notes |
|---|---|---|
| `GObjects` | `0x018BBD6C` | `TArray<UObject*>` (~192k objects) |
| `GNames` | `0x018BBD90` | `TArray<FNameEntry*>` (~75k names) |
| `GNatives` | `0x01898E80` | bytecode token → native thunk (4096) |
| `GWorld` | `0x018D5078` | `UWorld*` |
| `UObject::ProcessEvent` | `0x0048E750` | vtable slot `0xD0`; `AActor` overrides it at `0x005ECC00` |
| `UObject::ProcessInternal` | `0x00486820` | the script interpreter; `Func` of every script function |
| `UObject::FindFunctionChecked` | `0x004C2C00` | `(FName, INT Global)` |
| `UWorld::SpawnActor` | `0x0070E490` | `thiscall` on `GWorld`; UE3's 10-argument signature (class, name, &location, &rotation, template, bNoCollisionFail, bRemoteOwned, owner, instigator, bNoFail) |
| `AActor::execSpawn` | `0x0079E9A0` | native behind `Actor.Spawn` |
| `StaticLoadClass`-like | `0x004C98B0` | used to load `OnlineSubsystemSteamworks.SteamNetDriver` |

## Layouts

`FNameEntry`: `QWORD Flags; INT Index (name index << 1 | unicode); FNameEntry* HashNext; text at +0x10`.

| Struct (size) | Fields |
|---|---|
| `UObject` (0x3C) | vtable +00, Index +04, ObjectFlags +08 (QWORD), HashNext +10, HashOuterNext +14, StateFrame +18, Linker +1C, LinkerIndex +20, NetIndex +24, Outer +28, Name +2C (index, number), Class +34, ObjectArchetype +38 |
| `UField` (0x40) | Next +3C |
| `UStruct` (0x8C) | ScriptText +40, CppText +44, SuperStruct +48, Children +4C, PropertiesSize = low 16 bits of +50 (high 16 = alignment), Script `TArray<BYTE>` +54, TextPos +60, Line +64, property links +68…, ScriptObjectReferences +80 |
| `UProperty` (0x80) | ArrayDim +40, ElementSize +44, PropertyFlags +48 (QWORD), Category +50, rep info +58, **Offset +60**, PropertyLinkNext +64, RepOwner +74; then +80/+84: PropertyClass / Struct / Inner / Enum / BitMask / Function, and MetaClass for class properties |
| `UFunction` | FunctionFlags +8C, iNative +90 (WORD), NumParms +9D, ParmsSize +9E, ReturnValueOffset +A0, **Func +A8** |
| `UEnum` / `UConst` | names `TArray<FName>` +40 / value `FString` +40 |

How each was established: the name table's first entries ("None", "ByteProperty", …); `GObjects` as the large
array of objects whose vtables are in the exe; `UProperty::Offset` because `UObject`'s own script properties
(`Outer`, `Name`, `Class`, …) must report the offsets above; `ProcessEvent` as the vtable slot called 276
times right after `FindFunctionChecked` with a `NULL` result argument; `SpawnActor` from `execSpawn`.

## Bytecode notes (v576 vs later UE3)

In memory, object references are 4-byte pointers and names 8-byte `FName`s. Differences from later UE3:
`EX_Context`/`EX_ClassContext` = expr, WORD skip, BYTE, BYTE, expr (no property pointer); `EX_Switch` = 2 bytes
then expr; `EX_DelegateProperty` = name only; `0x5A` = editor-only block (WORD skip); cast codes use the older
numbering (`0x3A` ByteToInt …).

## Pointers for Phase 2 (mod loader)

- Hook any function, script or native, by swapping its `UFunction::Func` (+A8): script functions all point at
  `ProcessInternal`, so a wrapper can run code and then call the original.
- Call script functions with `ProcessEvent(FindFunctionChecked(name), &Parms, NULL)`; parameter layouts and
  offsets are in `sdk.json`.
- Bot-relevant script: `GameInfo.PickTeam`, `ChangeTeam`, `RestartPlayer` (already handles `AIController`s and
  has a `TryRestart` state), `SpawnDefaultPawnFor`, `PostLogin`; the dev bot `GCGame.GCMPBot` (size 0x598) moves
  with plain `MoveToward`, no pathfinding; `GCGame.GCBot` is the Kynapse-based single-player AI.

## Found later (mod loader, bots, navigation)

| | Address | Notes |
|---|---|---|
| `GEngine` | `0x018D10A4` | the `GCGameEngine` object (1272 code references) |
| `UEngine::Tick(FLOAT)` | vtable slot `0xFC` | `GCGameEngine::Tick` = `0x00B8D370` (`ret 4`); called by the main loop `0x00B2CA7A` right after the frame-time helper `0x00B2B0C0` (which calls `GetMaxTickRate`, slot `0x10C`). HomefrontSDK's host hooks this slot |
| `UWorld::SingleLineCheck` | `0x0070C3B0` | `thiscall` on `GWorld`: `(FCheckResult* Hit, AActor* Source, FVector* End, FVector* Start, DWORD Flags, FVector* Extent, ULightComponent*)`, `ret 0x1C`. `FCheckResult` (0x4C): Actor +4, Location +8, Normal +0x14, Time +0x20 (init 1.0), Item +0x24 (init -1). `TRACE_World` = `0x2086` |
| `UWorld::FarMoveActor` | `0x0070D920` | `(AActor*, FVector* Dest, UBOOL test, UBOOL bNoCheck, UBOOL bAttachedMoveForRelative)`; `Actor.SetLocation` = this with checks |
| `PlayerSpawnManagerMP::SpawnPlayer` | `0x00C05410` | casts the controller to `GCPlayerController` (`0x00DBA0A0`), so it fails for AI |
| spawn-point chooser | manager vtable `0x408` (`0x00BFE8A0`) | `(team, spawn type, controller)`; reads `FL2PlayerControllerMP` fields unless the controller is null |
| soldier class pick | `0x00BFCCB0` | `Team0_PlayerClasses` if team == `FL2GameMP.m_LowTeam` (`0x00DB8BA0`), else `Team1_PlayerClasses` |
| friendly-fire test | `0x00DB89F0` | from `FL2GameMP.ReduceDamage` (impl `0x00DBAAB0`); compares `GetTeamNum` |
| `AGCWeapon::ServerAddMPBot` | `0x00D7B630` | developers' bot spawner; sets `MaxTargetDistSoldier` (+0x548) = 4000, `TeamNum` (+0x57C) |
| `FStateFrame::StateNode` | `UObject.StateFrame` + `0x28` | current state (`UState*`) |

Disassembly helpers used: capstone + pefile (`hfdis.py`/`hfcalls.py`-style scripts reading `HFDedicated.exe`
with ImageBase `0x400000`; the exe has no ASLR).
