# HomefrontSDK

A C# modding SDK for the Homefront dedicated server (`HFDedicated.exe`, 2011-08-10 build, UE3 v576), in the style
of [BmSDK](https://github.com/Team-BmSDK/BmSDK-AC). Mods are .NET 10 class libraries that see the game's own classes
(`Pawn`, `PlayerController`, `FL2GameMP`, ...), generated with every script property and function from the
engine's reflection data. It doesn't depend on Steam or a Steam emulator, and clients stay stock.

## Layout

| Path | What |
|---|---|
| `Host/` | The native host, C++: built as `msimg32.dll`, starts .NET inside the server |
| `SDK/` | The runtime, C# (`HomefrontSDK.csproj`, building `HomefrontSDK.dll`): `Framework/` (objects, calls, hooks, scripts), `Classes/` (hand-written helpers), `Internal/` (loader, mods, lifecycle), `Bridge/` (local tool access), `Generated/` (the game classes; made by the build, not checked in) |
| `Generator/` | Writes `SDK/Generated/*.cs` from `api-reference.json` |
| `api-reference.json` | The server's reflection dump: 3,727 classes, 1,053 structs, 586 enums |
| `Examples/HelloMod/` | The smallest mod |
| `Homefront.slnx` | The solution: SDK, generator, HelloMod, [HomefrontBots](../HomefrontBots/README.md) |
| `Build.ps1` | Builds everything; `-Install` installs into `server\Binaries` |
| `Homefront.bat` | Starts the game client for testing on this PC (refuses while real Steam runs) |

## Build and install

Needs the .NET 10 SDK, Visual Studio's x86 C++ tools, and on the server machine the **32-bit (x86) .NET 10
runtime**. No NuGet packages.

```
HomefrontSDK\Build.ps1             build the host, the bindings (when missing or older than api-reference.json), the solution
HomefrontSDK\Build.ps1 -Install    also install into server\Binaries (stop the server first)
HomefrontSDK\Build.ps1 -Generate   regenerate the bindings
```

Installed into `server\Binaries`:

```
msimg32.dll                                  the host
HomefrontSDK\HomefrontSDK.dll                the runtime (+ .runtimeconfig.json, .deps.json, .pdb)
HomefrontSDK\HomefrontSDK.log                the host's and runtime's log
HomefrontSDK\Mods\<Mod>\<Mod>.dll            each mod, with its log <Mod>.log and its data files
```

Uninstall: delete `server\Binaries\msimg32.dll`. Disable a mod: move its folder out of `Mods`.

## How it runs

1. **Host** (`Host/host.cpp`). The exe imports `AlphaBlend` from `msimg32.dll`, and msimg32 isn't a KnownDLL, so
   Windows loads the copy beside the exe; its five exports jump to the real one. A watcher thread waits for
   `GEngine` (`0x018D10A4`), checks the build (`GCGameEngine` vtable slot `0xFC`, `UEngine::Tick`, holds
   `0x00B8D370`), and hooks that slot.
2. On the first engine tick the host finds the 32-bit .NET install (`DOTNET_ROOT(x86)`, the registry, then
   `Program Files (x86)\dotnet`), loads `hostfxr`, and calls `HomefrontSDK.Internal.Loader.Initialize` with its
   native API (`Host/hfsdk_native.h`). That API has crash-guarded `ProcessEvent` and native calls, guarded memory
   reads, and 1024 hook slots.
3. The runtime checks the engine against the bindings. Every one of the 19,420 generated property offsets is
   compared with the live reflection data, and it refuses to start on any mismatch. Then it installs its own hooks
   (joins, leaves, chat), starts the bridge, and loads every mod in `Mods\`.
4. Every engine tick: map tracking, the scripts' `OnTick`, the bridge's queued requests, and queued mod reloads. All
   of this runs on the game thread.

## Writing a mod

A mod is a class library referencing `HomefrontSDK\SDK\HomefrontSDK.csproj` (with `Private="false"`: the game already
has the SDK loaded), with one or more `[Script]` classes. `Examples/HelloMod` is a template:

```csharp
using HomefrontSDK;

[Script(Description = "example mod")]
public sealed class Hello : Script
{
    public override void OnPlayerJoined(PlayerController player) => player.Tell($"welcome, {player.PlayerName}");

    [Command("hello", Help = "!hello: says hello back")]
    void HelloCommand(CommandContext ctx) => ctx.Reply($"hi, {Game.Players.Count} player(s) on {Game.MapName}");

    [Hook("GameInfo", "Killed", After = true)]
    void OnKilled(GameInfo game, HookFrame frame) =>
        Log.Write($"{frame.Object<Controller>("Killer")?.PlayerName} killed {frame.Object<Controller>("Killed")?.PlayerName}");
}
```

Install it as `server\Binaries\HomefrontSDK\Mods\<Name>\<Name>.dll`; add it to `Build.ps1`'s `$mods` list to have
the build do that.

### Script events

| Override | When |
|---|---|
| `OnStart` / `OnStop` | after loading / before unloading (also on `!sdk reload`) |
| `OnTick(deltaSeconds)` | after every engine tick |
| `OnMapLoaded(GameInfo)` | a map's GameInfo exists and all its streaming levels are loaded and visible |
| `OnMapUnloading(map)` | the world changed |
| `OnPlayerJoined(PlayerController)` / `OnPlayerLeft(Controller)` | `GameInfo.PostLogin` / `GameInfo.Logout` |

`Log` writes `Mods\<Mod>\<Mod>.log`, and `Directory` is the mod's folder. An exception from a mod disables that mod
only, with the stack trace in its log. `!sdk reload <mod>` loads it again. Mods are loaded from bytes into
collectible contexts, so a rebuilt DLL can be copied over the old one while the server runs.

### The game's classes

Generated per package into `HomefrontSDK` (namespace `HomefrontSDK`).

- **Naming:**
  - Core classes take a `U` prefix (`UObject`, `UClass`).
  - Others keep their script names (`Actor`, `GCMPBot`).
  - Structs take an `F` prefix (`FVector`, `FPackedLoadout`).
  - Enums keep their names (`ETeam`, `EPhysics`).
- **Properties:** typed accessors at their offsets.
  - Structs and arrays are returned by `ref`, so `pawn.Location.Z += 10` edits the game's value.
  - Objects come back as the most derived wrapper (`c.Pawn is GCPawn p`).
  - Strings are read-only: the game's strings must be allocated by the engine.
- **Functions:** call the script function through `ProcessEvent`.
  - Non-final functions are looked up on the object (state first, then class), so overrides run.
  - `out` parameters are `ref` (`out string` for strings).
  - Optional parameters have defaults.
  - Iterators, latent functions, operators and delegates aren't generated.
- **Hand-written helpers** in `Classes/`:
  - `FVector` maths;
  - `Actor.Alive` and `Actor.Teleport` (`UWorld::FarMoveActor`);
  - `Controller.PlayerName` and `Controller.TeamIndex`;
  - `PlayerController.Tell` (a chat line to one player);
  - `WorldInfo.StreamingSettled`;
  - `World.Trace` (`UWorld::SingleLineCheck`).
- **`Game`:** `World`, `MapName`, `MapLoaded`, `Info` (the GameInfo), `WorldInfo`, `Controllers`, `Players`, and
  `All<T>()` (every object of a class).
- **Object lifetime:** check `IsValid` on any wrapper you keep across ticks. A garbage-collected object's wrapper
  turns invalid; a destroyed but not yet collected actor has `bDeleteMe` set (`Alive` is false).

### Hooks

Use `[Hook("Class", "Function")]` on a script method, or `Hooks.Add(...)`.

- **What gets hooked:** every function of that name in the class and its subclasses, including state versions. A
  hook sees only the outermost call when they call each other through `super`.
- **"Before" hooks:** return `false` to skip the original.
- **Parameters:** read and written by name: `frame.Object<T>(name)`, `frame.String(name)`, `frame.Bool(name)`,
  `ref frame.Param<T>(name)`.
- **Script functions only:** a native function called from script reads its arguments from the caller's frame,
  so the parameters can't be read there. Skipping one would also leave its arguments unread.

### Native code

`Native.Call(address, this, args)` calls any engine function at a known address. The arguments are 32-bit words,
and `this` goes in ecx. A crash in the call is caught and thrown as `GameCrashException`. `Native.TryRead` copies
memory that may be unreadable. Addresses found so far are in `re/README.md`.

## Commands and the bridge

Chat lines starting with `!name` run a registered command and stay out of chat. The SDK's own command is
`!sdk status | mods | reload [mod] | hooks`.

The bridge is a loopback TCP server on `127.0.0.1:27040` (or `HFSDK_BRIDGE_PORT`) for local tools. Its protocol is
JSON lines: send `{"id":1,"cmd":"..."}` and get back `{"id":1,"ok":true,"result":...}`. Requests run on the game
thread at the end of a tick. The built-in requests:

| Request | What |
|---|---|
| `info` | map, game mode, players, mods, commands, handlers |
| `objects` | objects by `class` and/or `search` (this map's actors only, unless `allMaps`) |
| `object` | an object (`ptr` or `path`) with every property, read through reflection |
| `set` | sets a property: `{"ptr":..,"property":"Location.Z","value":120}` (numbers, bools, enums, names, object refs, structs) |
| `actors` | pawns and objectives; with `"static":true` also spawn points and combat areas |
| `teleport` | moves an actor to `x`, `y`, `z` |
| `command` | runs a chat command as a tool (`"line":"!bots add 4"`) and returns its replies |
| `logs` | streams the SDK and mod log lines to this connection |

Mods add their own with `Bridge.Handle(name, handler)`; HomefrontBots adds `bots.list` and `bots.nav`. To try it
from a shell:

```
python tools\hfbridge.py info
python tools\hfbridge.py command "!bots add 4"
python tools\hfbridge.py logs
```

## Regenerating the bindings

The bindings must match the exe; the startup check enforces that. After any change to the game's script
packages, run the server and do this:

```
python re\dump_sdk.py --api        writes HomefrontSDK\api-reference.json from the running server
HomefrontSDK\Build.ps1 -Install
```

## Limits

- **Strings and arrays can't be created or grown:** there's no wrapper for the engine allocator yet. Strings passed
  to functions point at temporary buffers, which is fine for calls that don't keep them. Strings the engine returns
  are copied and the engine's copy leaks a few bytes.
- **Maps** (`TMap` properties) can't be read.
- **Server only.** The client exe isn't a supported build, so in its folder the host only forwards `msimg32`.
- **No startup without the 32-bit .NET 10 runtime:** the host logs why, and the server runs without mods.
