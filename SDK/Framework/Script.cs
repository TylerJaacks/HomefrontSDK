using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HomefrontSDK.Internal;

namespace HomefrontSDK;

/// <summary>Marks a mod's script class. A mod assembly can hold several.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ScriptAttribute : Attribute
{
    /// <summary>Shown by "!sdk mods"; defaults to the class name.</summary>
    public string? Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>Makes a script method a chat command: "!name args..." from a player runs it and is kept out of chat.
/// The method takes a <see cref="CommandContext"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public string? Help { get; set; }
}

/// <summary>Hooks a script method onto every function named <see cref="Function"/> in <see cref="Class"/> and its
/// subclasses. The method is <c>void M(TSelf self, HookFrame frame)</c> (after, or before when it can't skip) or
/// <c>bool M(TSelf self, HookFrame frame)</c> (before; return false to skip the original).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class HookAttribute(string className, string function) : Attribute
{
    public string Class { get; } = className;
    public string Function { get; } = function;
    public bool After { get; set; }
}

/// <summary>A chat command being run, typed by a player or sent by a tool (HomefrontEditor's console).</summary>
public sealed class CommandContext
{
    internal CommandContext(PlayerController? player, string name, string[] args, string line)
    {
        Player = player;
        Name = name;
        Args = args;
        Line = line;
    }

    /// <summary>Who typed it; null when a tool sent it (then replies go back to the tool).</summary>
    public PlayerController? Player { get; }

    /// <summary>Replies to a tool's command.</summary>
    internal readonly List<string> Output = new();

    /// <summary>The command name, without the "!".</summary>
    public string Name { get; }

    /// <summary>The words after the command name.</summary>
    public string[] Args { get; }

    /// <summary>The whole chat line.</summary>
    public string Line { get; }

    /// <summary>Argument <paramref name="index"/> as text, or <paramref name="fallback"/>.</summary>
    public string Arg(int index, string fallback = "") => index < Args.Length ? Args[index] : fallback;

    /// <summary>Argument <paramref name="index"/> as a number, or <paramref name="fallback"/>.</summary>
    public int Int(int index, int fallback) => index < Args.Length && int.TryParse(Args[index], out int v) ? v : fallback;

    /// <summary>A chat line to the player who typed the command (or a line back to the tool that sent it).</summary>
    public void Reply(string text)
    {
        if (Player != null && Player.IsValid)
            Player.Tell(text);
        else
            Output.Add(text);
    }
}

/// <summary>A mod's log: Mods\&lt;Mod&gt;\&lt;Mod&gt;.log, with a timestamp per line.</summary>
public sealed class ModLog
{
    readonly StreamWriter? writer;
    readonly string name;

    internal ModLog(string name, string path)
    {
        this.name = name;
        try
        {
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (IOException e)
        {
            Native.Log($"{name}: can't open {path}: {e.Message}");
        }
    }

    public void Write(string line)
    {
        writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        Bridge.OnLog(name, line);
    }

    /// <summary>Also copied to HomefrontSDK.log.</summary>
    public void Warn(string line)
    {
        Write(line);
        Native.Log($"[{name}] {line}");
    }

    internal void Close() => writer?.Dispose();
}

/// <summary>A mod's script: derive from it and mark the class [Script]. The SDK creates one instance when the mod
/// loads and calls the overrides on the game thread. An exception from any of them disables the whole mod.</summary>
public abstract class Script
{
    /// <summary>The mod's log.</summary>
    public ModLog Log { get; internal set; } = null!;

    /// <summary>The mod's folder (Mods\&lt;Mod&gt;\), for its data files.</summary>
    public string Directory { get; internal set; } = "";

    /// <summary>The mod's name (its assembly name).</summary>
    public string ModName { get; internal set; } = "";

    /// <summary>Once, after loading. If a map is already running, <see cref="OnMapLoaded"/> follows.</summary>
    public virtual void OnStart() { }

    /// <summary>Before the mod is unloaded (server shutdown isn't reported).</summary>
    public virtual void OnStop() { }

    /// <summary>After every engine tick.</summary>
    public virtual void OnTick(float deltaSeconds) { }

    /// <summary>A map is running: its GameInfo exists and all its streaming levels are loaded and visible.</summary>
    public virtual void OnMapLoaded(GameInfo game) { }

    /// <summary>The map is going away (the world changed). Its objects may already be half torn down.</summary>
    public virtual void OnMapUnloading(string map) { }

    /// <summary>A player finished joining (GameInfo.PostLogin).</summary>
    public virtual void OnPlayerJoined(PlayerController player) { }

    /// <summary>A player or bot is leaving (GameInfo.Logout).</summary>
    public virtual void OnPlayerLeft(Controller player) { }
}
