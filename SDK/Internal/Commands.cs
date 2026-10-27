using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HomefrontSDK.Internal;

/// <summary>Chat commands: "!name args" typed by a player. The SDK's own are under "!sdk".</summary>
public static class Commands
{
    sealed record Entry(string Name, string? Help, Mod? Owner, Action<CommandContext> Run);

    static readonly Dictionary<string, Entry> commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The registered command names.</summary>
    public static IEnumerable<string> Names => commands.Keys.OrderBy(n => n);

    internal static void Register(Mod mod, Script script)
    {
        foreach (MethodInfo m in script.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var a = m.GetCustomAttribute<CommandAttribute>();
            if (a == null)
                continue;
            var ps = m.GetParameters();
            if (ps.Length != 1 || ps[0].ParameterType != typeof(CommandContext))
            {
                mod.Log?.Warn($"[Command] {m.Name}: needs one CommandContext parameter");
                continue;
            }
            var run = (Action<CommandContext>)m.CreateDelegate(typeof(Action<CommandContext>), script);
            Add(a.Name, a.Help, mod, run);
        }
    }

    internal static void Add(string name, string? help, Mod? owner, Action<CommandContext> run)
    {
        if (commands.TryGetValue(name, out var existing) && existing.Owner != owner)
            Native.Log($"command !{name} of {owner?.Name ?? "SDK"} replaces the one of {existing.Owner?.Name ?? "SDK"}");
        commands[name] = new Entry(name, help, owner, run);
    }

    internal static void RemoveAll(Mod mod)
    {
        foreach (var e in commands.Values.Where(e => e.Owner == mod).ToList())
            commands.Remove(e.Name);
    }

    /// <summary>Runs "!name args" if a command has that name. Returns false when none does (the line is chat).</summary>
    internal static bool TryRun(PlayerController player, string line) => Run(player, line) != null;

    /// <summary>Runs a command line ("!bots list", the "!" optional) for a tool; returns its replies, or null when
    /// no command has that name.</summary>
    public static List<string>? RunFromTool(string line) => Run(null, line.StartsWith('!') ? line : "!" + line);

    static List<string>? Run(PlayerController? player, string line)
    {
        if (line.Length < 2 || line[0] != '!')
            return null;
        string[] words = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !commands.TryGetValue(words[0], out var entry))
            return null;
        var ctx = new CommandContext(player, words[0], words[1..], line);
        string who = player?.PlayerName ?? "a tool";
        (entry.Owner?.Log)?.Write($"command from {who}: {line}");
        try
        {
            entry.Run(ctx);
        }
        catch (Exception e)
        {
            Mods.Fault(entry.Owner, $"command !{entry.Name}", e);
            ctx.Reply($"[{entry.Name}] failed: {e.Message}");
        }
        return ctx.Output;
    }

    /// <summary>The help line of a command, if it has one.</summary>
    public static string? Help(string name) => commands.TryGetValue(name, out var e) ? e.Help : null;
}
