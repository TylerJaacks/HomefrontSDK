using HomefrontSDK;

namespace HelloMod;

/// <summary>Greets joining players, answers "!hello", and logs the player count once a minute.</summary>
[Script(Description = "example mod")]
public sealed class Hello : Script
{
    float sinceReport;

    public override void OnStart() => Log.Write("hello from HelloMod");

    public override void OnMapLoaded(GameInfo game) =>
        Log.Write($"map {Game.MapName} loaded, game mode {game.ClassName}");

    public override void OnPlayerJoined(PlayerController player) => player.Tell($"[hello] welcome, {player.PlayerName}");

    public override void OnTick(float deltaSeconds)
    {
        sinceReport += deltaSeconds;
        if (sinceReport < 60f || !Game.MapLoaded)
            return;
        sinceReport = 0;
        Log.Write($"{Game.Players.Count} player(s) on {Game.MapName}");
    }

    // Every chat line, in HelloMod.log (an example of [Hook]; also shows whether chat reaches the server).
    [Hook("PlayerController", "ServerSay")]
    bool OnSay(PlayerController player, HookFrame frame)
    {
        Log.Write($"chat from {player.PlayerName}: {frame.String("msg")}");
        return true;
    }

    [Hook("PlayerController", "ServerTeamSay")]
    bool OnTeamSay(PlayerController player, HookFrame frame)
    {
        Log.Write($"team chat from {player.PlayerName}: {frame.String("msg")}");
        return true;
    }

    [Command("hello", Help = "!hello: says hello back")]
    void HelloCommand(CommandContext ctx)
    {
        PlayerController? player = ctx.Player;  // null when a tool (HomefrontEditor's console) sent the command
        Pawn? pawn = player?.Pawn;
        ctx.Reply(player == null ? $"[hello] hi tool; {Game.Players.Count} player(s) on {Game.MapName}"
            : pawn != null ? $"[hello] hi {player.PlayerName}, you're at {pawn.Location} with {pawn.Health} health"
            : $"[hello] hi {player.PlayerName}");
    }
}
