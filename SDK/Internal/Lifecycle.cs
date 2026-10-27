using System;
using System.Diagnostics;
using System.Linq;

namespace HomefrontSDK.Internal;

/// <summary>Turns engine state into script events: map changes, players joining and leaving, chat commands.</summary>
internal static unsafe class Lifecycle
{
    static nint world;
    static string map = "";
    static long nextCheck;
    static readonly Stopwatch clock = Stopwatch.StartNew();

    public static bool MapLoaded { get; private set; }

    public static void Start()
    {
        // GameInfo and PlayerController functions are redefined per game mode and inside states: hook the whole tree.
        Hooks.Add("GameInfo", "PostLogin", null, (self, frame) =>
        {
            if (frame.Object<PlayerController>("NewPlayer") is PlayerController pc)
            {
                Native.Log($"join: {pc.PlayerName}");
                Mods.ForEachScript("OnPlayerJoined", s => s.OnPlayerJoined(pc));
            }
        }, null);
        Hooks.Add("GameInfo", "Logout", (self, frame) =>
        {
            if (frame.Object<Controller>("Exiting") is Controller c)
            {
                Native.Log($"leave: {c.PlayerName}");
                Mods.ForEachScript("OnPlayerLeft", s => s.OnPlayerLeft(c));
            }
            return true;
        }, null, null);
        // Chat lines starting with a registered "!command" run it and stay out of chat.
        Hooks.Add("PlayerController", "ServerSay", (self, frame) =>
            self is not PlayerController pc || !Commands.TryRun(pc, frame.String("msg")), null, null);

        Commands.Add("sdk", "!sdk [status | mods | reload [mod] | hooks]", null, SdkCommand);
    }

    public static void Tick(float delta)
    {
        nint w = *(nint*)Native.Api->GWorld;
        if (w != world)
        {
            if (MapLoaded)
                Mods.ForEachScript("OnMapUnloading", s => s.OnMapUnloading(map));
            MapLoaded = false;
            Game.Info = null;
            world = w;
            map = w != 0 ? Names.ObjectName(*(nint*)(w + Layout.Outer)) : "";
            ObjectCache.Prune();
            Objects.ForgetObjects();
            Native.Log($"map: {(w != 0 ? map : "(none)")}");
        }

        // A map counts as loaded once its GameInfo exists and its streaming levels (geometry, spawn points,
        // objectives) are all loaded and visible.
        long now = clock.ElapsedMilliseconds;
        if (!MapLoaded && world != 0 && now >= nextCheck)
        {
            nextCheck = now + 500;
            if (FindGame() is GameInfo game && game.WorldInfo is WorldInfo info && info.StreamingSettled(out _))
            {
                MapLoaded = true;
                Game.Info = game;
                Native.Log($"map loaded: {map} ({game.ClassName})");
                Mods.ForEachScript("OnMapLoaded", s => s.OnMapLoaded(game));
            }
        }

        Mods.ForEachScript("OnTick", s => s.OnTick(delta));
        Bridge.Pump();
        Hooks.Sweep();
        Mods.ProcessReloads();
    }

    // The current map's GameInfo, found without a player to ask: the one in the current world's persistent level
    // (a previous map's can linger until garbage collection).
    static GameInfo? FindGame()
    {
        foreach (GameInfo g in Objects.All<GameInfo>())
        {
            nint outer = *(nint*)(g.Ptr + Layout.Outer);
            if (outer != 0 && *(nint*)(outer + Layout.Outer) == world && !g.bDeleteMe && g.WorldInfo != null)
                return g;
        }
        return null;
    }

    public static string Map => map;

    static void SdkCommand(CommandContext ctx)
    {
        switch (ctx.Arg(0, "status"))
        {
            case "status":
                ctx.Reply($"[sdk] map {(map.Length > 0 ? map : "none")}{(MapLoaded ? "" : " (loading)")}, " +
                          $"{Mods.All.Count(m => m.Running)}/{Mods.All.Count} mods running, {Hooks.Count} hooks, " +
                          $"commands: {string.Join(" ", Commands.Names.Select(n => "!" + n))}");
                break;
            case "mods":
                foreach (Mod m in Mods.All)
                    ctx.Reply($"[sdk] {m.Name} {m.Version}: {(m.Running ? "running" : "stopped")}");
                if (Mods.All.Count == 0)
                    ctx.Reply("[sdk] no mods in " + Mods.ModsDir);
                break;
            case "reload":
                string? name = ctx.Args.Length > 1 ? ctx.Args[1] : null;
                Mods.QueueReload(name);
                ctx.Reply($"[sdk] reloading {name ?? "all mods"}");
                break;
            case "hooks":
                ctx.Reply($"[sdk] {Hooks.Count} hooks");
                break;
            default:
                ctx.Reply("[sdk] " + Commands.Help("sdk"));
                break;
        }
    }
}
