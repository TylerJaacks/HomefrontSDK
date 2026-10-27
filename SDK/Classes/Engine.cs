using System;
using HomefrontSDK.Internal;

namespace HomefrontSDK;

// Hand-written halves of generated classes: helpers over script properties and the engine natives found by
// reversing (HomefrontSDK/Tools/README.md).

public partial class Actor
{
    /// <summary>The actor exists and isn't being destroyed.</summary>
    public bool Alive => IsValid && !bDeleteMe;

    /// <summary>Moves the actor with UWorld::FarMoveActor (0x0070D920), what SetLocation calls. With
    /// <paramref name="noCheck"/> the engine skips its fit and overlap checks: Actor.SetLocation refuses every spot for
    /// soldier pawns on this server, even open ground. False if the engine refused the move.</summary>
    public unsafe bool Teleport(FVector to, bool noCheck = true)
    {
        nint world = *(nint*)Native.Api->GWorld;
        if (world == 0)
            return false;
        // UBOOL UWorld::FarMoveActor(AActor*, const FVector& Dest, UBOOL test, UBOOL bNoCheck, UBOOL bAttachedMoveForRelative)
        return Native.Call(0x0070D920, world, [(uint)Ptr, (uint)(nint)(&to), 0, noCheck ? 1u : 0u, 0]) != 0;
    }
}

public partial class Controller
{
    /// <summary>The player's name from its PlayerReplicationInfo, or the object name.</summary>
    public string PlayerName => PlayerReplicationInfo?.PlayerName is { Length: > 0 } n ? n : ObjectName;

    /// <summary>The team index from the PlayerReplicationInfo (0 or 1), or -1.</summary>
    public int TeamIndex => PlayerReplicationInfo?.Team is TeamInfo t ? (int)t.TeamIndex : -1;
}

public partial class PlayerController
{
    /// <summary>A chat line to this player only (TeamMessage, a client call: the text is only serialised).
    /// Homefront's ClientMessage doesn't reach the chat box.</summary>
    public void Tell(string text)
    {
        TeamMessage(PlayerReplicationInfo, text, FName.Find("Say"));
    }
}

public partial class WorldInfo
{
    /// <summary>Whether every streaming level is loaded and visible as it should be. Spawn points, objectives and
    /// much of the geometry arrive in streaming levels after the persistent level.</summary>
    public bool StreamingSettled(out int pending)
    {
        pending = 0;
        foreach (LevelStreaming level in StreamingLevels.Items())
        {
            bool loaded = level.LoadedLevel != null;
            if (level.bHasLoadRequestPending || level.bHasUnloadRequestPending ||
                (level.bShouldBeLoaded && !loaded) || (level.bShouldBeVisible && !level.bIsVisible))
                ++pending;
        }
        return pending == 0;
    }
}

/// <summary>What a trace hit.</summary>
public struct TraceHit
{
    /// <summary>The actor hit, or null (nothing within the trace).</summary>
    public Actor? Actor;

    /// <summary>For a box sweep, the box center at impact.</summary>
    public FVector Location;

    public FVector Normal;

    /// <summary>The fraction of start -> end travelled (1 when nothing was hit).</summary>
    public float Time;

    public readonly bool Hit => Time < 1f;
}

public partial class World
{
    /// <summary>World geometry only: BSP, static meshes, terrain, movers (what Actor.Trace uses without actors).</summary>
    public const uint TraceWorld = 0x2086;

    /// <summary>UWorld::SingleLineCheck (0x0070C3B0): a line, or a box sweep when <paramref name="extent"/> isn't
    /// zero, from <paramref name="start"/> to <paramref name="end"/>. <paramref name="source"/> is ignored by the
    /// trace: pass an actor without collision such as the GameInfo (not the WorldInfo, which owns the BSP).</summary>
    public unsafe TraceHit Trace(Actor? source, FVector start, FVector end, FVector extent, uint flags = TraceWorld)
    {
        // FCheckResult (0x4C): Next, Actor +4, Location +8, Normal +0x14, Time +0x20, Item +0x24, ...; initialised
        // the way AActor::execTrace does.
        byte* check = stackalloc byte[0x80];
        *(float*)(check + 0x20) = 1f;
        *(int*)(check + 0x24) = -1;
        *(int*)(check + 0x44) = -1;
        // UBOOL UWorld::SingleLineCheck(FCheckResult& Hit, AActor* SourceActor, const FVector& End,
        //                               const FVector& Start, DWORD TraceFlags, const FVector& Extent, ULightComponent*)
        Native.Call(0x0070C3B0, Ptr,
            [(uint)(nint)check, (uint)(source?.Ptr ?? 0), (uint)(nint)(&end), (uint)(nint)(&start), flags, (uint)(nint)(&extent), 0]);
        return new TraceHit
        {
            Actor = UObject.Get<Actor>(*(nint*)(check + 0x04)),
            Location = *(FVector*)(check + 0x08),
            Normal = *(FVector*)(check + 0x14),
            Time = *(float*)(check + 0x20),
        };
    }
}
