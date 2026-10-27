using System.Collections.Generic;
using HomefrontSDK.Internal;

namespace HomefrontSDK;

/// <summary>The running game: world, map, game mode and players.</summary>
public static unsafe class Game
{
    /// <summary>The current world (UWorld), or null between maps.</summary>
    public static World? World => UObject.Get<World>(*(nint*)Native.Api->GWorld);

    /// <summary>The current map's name ("fl-farm"), or "".</summary>
    public static string MapName => Lifecycle.Map;

    /// <summary>Whether the map is loaded: its GameInfo exists and its streaming levels are all in place.</summary>
    public static bool MapLoaded => Lifecycle.MapLoaded;

    /// <summary>The map's GameInfo (the game mode), once the map is loaded.</summary>
    public static GameInfo? Info { get; internal set; }

    /// <summary>The map's WorldInfo, once the map is loaded.</summary>
    public static WorldInfo? WorldInfo => Info?.WorldInfo;

    /// <summary>Every controller (players and bots), from WorldInfo.ControllerList.</summary>
    public static List<Controller> Controllers
    {
        get
        {
            var list = new List<Controller>();
            for (Controller? c = WorldInfo?.ControllerList; c != null && list.Count < 1024; c = c.NextController)
                list.Add(c);
            return list;
        }
    }

    /// <summary>Human players (PlayerControllers).</summary>
    public static List<PlayerController> Players
    {
        get
        {
            var list = new List<PlayerController>();
            foreach (Controller c in Controllers)
                if (c is PlayerController pc)
                    list.Add(pc);
            return list;
        }
    }

    /// <summary>The engine object table, for searches by class.</summary>
    public static List<T> All<T>() where T : UObject => Objects.All<T>();
}
