using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace HomefrontSDK;

/// <summary>The bridge's built-in requests that browse and move game objects.</summary>
internal static unsafe class Requests
{
    /// <summary>The object a request names: "ptr" (a number) or "path" ("GCGame.Default__GCMPBot").</summary>
    public static nint Target(JsonObject r)
    {
        nint p = r["ptr"] is JsonNode ptr ? (nint)ptr.GetValue<long>() : r["path"] is JsonNode path ? HomefrontSDK.Objects.Find(path.GetValue<string>()) : 0;
        if (!Inspector.IsObject(p))
            throw new ArgumentException("no such object");
        return p;
    }

    /// <summary>"objects": objects by class ("class": "Pawn") and/or name ("search": "bot"), up to "limit" (500).
    /// Class default objects only with "defaults": true; actors of other maps only with "allMaps": true.</summary>
    public static JsonArray Objects(JsonObject r)
    {
        string? className = r["class"]?.GetValue<string>();
        string? search = r["search"]?.GetValue<string>();
        int limit = r["limit"]?.GetValue<int>() ?? 500;
        bool defaults = r["defaults"]?.GetValue<bool>() ?? false;
        bool allMaps = r["allMaps"]?.GetValue<bool>() ?? false;
        nint cls = className != null ? HomefrontSDK.Objects.FindClass(className) : 0;
        if (className != null && cls == 0)
            throw new ArgumentException($"no class {className}");
        nint actorClass = UObject.ClassPtr<Actor>();
        nint worldInfo = Game.WorldInfo?.Ptr ?? 0;
        int worldInfoOffset = HomefrontSDK.Objects.PropertyOffset(actorClass, "WorldInfo");
        var result = new JsonArray();
        int count = HomefrontSDK.Objects.Count;
        for (int i = 0; i < count && result.Count < limit; ++i)
        {
            nint o = HomefrontSDK.Objects.At(i);
            if (o == 0 || (cls != 0 && !HomefrontSDK.Objects.IsA(o, cls)))
                continue;
            string name = Names.ObjectName(o);
            if (!defaults && name.StartsWith("Default__", StringComparison.Ordinal))
                continue;
            if (search != null && !name.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !HomefrontSDK.Objects.ClassName(o).Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!allMaps && worldInfo != 0 && HomefrontSDK.Objects.IsA(o, actorClass) && *(nint*)(o + worldInfoOffset) != worldInfo)
                continue;
            result.Add(new JsonObject
            {
                ["ptr"] = (long)o,
                ["name"] = name,
                ["class"] = HomefrontSDK.Objects.ClassName(o),
                ["path"] = Names.ObjectFullName(o),
            });
        }
        return result;
    }

    // Spawn points, objectives and combat areas of the current map, found once per map.
    static nint cachedWorldInfo;
    static List<Actor> statics = new();

    static List<Actor> Statics(WorldInfo info)
    {
        if (cachedWorldInfo == info.Ptr && statics.TrueForAll(a => a.IsValid))
            return statics;
        cachedWorldInfo = info.Ptr;
        statics = new List<Actor>();
        foreach (PlayerStart s in Game.All<PlayerStart>())
            if (s.WorldInfo == info)
                statics.Add(s);
        foreach (Objective_GameMode o in Game.All<Objective_GameMode>())
            if (o.WorldInfo == info)
                statics.Add(o);
        foreach (GameMode_CombatVolume v in Game.All<GameMode_CombatVolume>())
            if (v.WorldInfo == info)
                statics.Add(v);
        return statics;
    }

    /// <summary>"actors": what the editor's map draws. Pawns always (from WorldInfo.PawnList); spawn points,
    /// objectives and combat areas too with "static": true. Objectives come every time (owners change).</summary>
    public static JsonObject Actors(JsonObject r)
    {
        var result = new JsonObject { ["map"] = Game.MapName };
        WorldInfo? info = Game.WorldInfo;
        var list = new JsonArray();
        result["actors"] = list;
        if (info == null)
            return result;
        bool all = r["static"]?.GetValue<bool>() ?? false;
        foreach (Actor a in Statics(info))
            if (all || a is Objective_GameMode)
                list.Add(Describe(a));
        for (Pawn? p = info.PawnList; p != null && list.Count < 4096; p = p.NextPawn)
            if (p.Alive)
                list.Add(Describe(p));
        return result;
    }

    static JsonObject Describe(Actor a)
    {
        FVector loc = a.Location;
        var o = new JsonObject
        {
            ["ptr"] = (long)a.Ptr,
            ["name"] = a.ObjectName,
            ["class"] = a.ClassName,
            ["x"] = loc.X,
            ["y"] = loc.Y,
            ["z"] = loc.Z,
            ["yaw"] = a.Rotation.Yaw,
        };
        switch (a)
        {
            case Pawn p:
                o["kind"] = "pawn";
                o["health"] = p.Health;
                if (p.Controller is Controller c)
                {
                    o["label"] = c.PlayerName;
                    o["team"] = c.TeamIndex;
                    o["bot"] = c is not PlayerController;
                    o["state"] = c.StateName;
                }
                break;
            case PlayerStart s:
                o["kind"] = "start";
                o["primary"] = s.bPrimaryStart;
                break;
            case Objective_GameMode obj:
                o["kind"] = "objective";
                o["team"] = (int)obj.OwningTeam;
                o["active"] = obj.bIsActive;
                o["label"] = obj.ObjectName;
                break;
            case GameMode_CombatVolume v:
                o["kind"] = "combat";
                o["ground"] = v.bGroundVolumeType;
                if (v.BrushComponent is BrushComponent b)
                {
                    ref FBoxSphereBounds bounds = ref b.Bounds;
                    o["bounds"] = new JsonArray(bounds.Origin.X - bounds.BoxExtent.X, bounds.Origin.Y - bounds.BoxExtent.Y,
                        bounds.Origin.X + bounds.BoxExtent.X, bounds.Origin.Y + bounds.BoxExtent.Y);
                }
                break;
            default:
                o["kind"] = "actor";
                break;
        }
        return o;
    }

    /// <summary>"teleport": moves an actor ("ptr") to "x", "y", "z" (the engine's fit checks skipped). A player's
    /// client is told too.</summary>
    public static JsonNode Teleport(JsonObject r)
    {
        var actor = UObject.Get<Actor>(Target(r)) ?? throw new ArgumentException("not an actor");
        var to = new FVector(r["x"]!.GetValue<float>(), r["y"]!.GetValue<float>(), r["z"]!.GetValue<float>());
        if (!actor.Teleport(to))
            throw new InvalidOperationException("the engine refused the move");
        if (actor is Pawn p)
        {
            p.SetMovementPhysics();
            if (p.Controller is PlayerController pc)
                pc.ClientSetLocation(to, p.Rotation);
        }
        FVector at = actor.Location;
        return new JsonObject { ["x"] = at.X, ["y"] = at.Y, ["z"] = at.Z };
    }
}
