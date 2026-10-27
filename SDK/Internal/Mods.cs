using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace HomefrontSDK.Internal;

/// <summary>A loaded mod: one assembly in Mods\&lt;Name&gt;\&lt;Name&gt;.dll and its scripts.</summary>
public sealed class Mod
{
    internal Mod(string name, string directory)
    {
        Name = name;
        Directory = directory;
    }

    public string Name { get; }
    public string Directory { get; }
    public string Version { get; internal set; } = "";

    /// <summary>False once the mod threw (it stays loaded but gets no more calls until reloaded).</summary>
    public bool Running { get; internal set; }

    internal ModLoadContext? Context;
    internal readonly List<Script> Scripts = new();
    internal ModLog? Log;
}

/// <summary>A collectible load context per mod, so it can be unloaded and loaded again (hot reload).</summary>
internal sealed class ModLoadContext(string directory, string name) : AssemblyLoadContext(name, isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == typeof(Script).Assembly.GetName().Name)
            return typeof(Script).Assembly;  // one runtime for every mod
        string path = Path.Combine(directory, assemblyName.Name + ".dll");
        return File.Exists(path) ? Mods.LoadUnlocked(this, path) : null;
    }
}

/// <summary>Finds, loads, runs and unloads mods.</summary>
public static class Mods
{
    static readonly List<Mod> mods = new();
    static readonly Queue<string?> reloads = new();  // null = all

    /// <summary>The loaded mods.</summary>
    public static IReadOnlyList<Mod> All => mods;

    internal static string ModsDir => Path.Combine(Native.SdkDir, "Mods");

    // Loaded from bytes so the files stay unlocked: a rebuilt mod can be copied over the old one and reloaded.
    internal static Assembly LoadUnlocked(AssemblyLoadContext context, string dll)
    {
        string pdb = Path.ChangeExtension(dll, ".pdb");
        using var code = new MemoryStream(File.ReadAllBytes(dll));
        using var symbols = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null;
        return context.LoadFromStream(code, symbols);
    }

    internal static void LoadAll()
    {
        System.IO.Directory.CreateDirectory(ModsDir);
        foreach (string dir in System.IO.Directory.GetDirectories(ModsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(dir);
            if (File.Exists(Path.Combine(dir, name + ".dll")))
                Load(name);
        }
        Native.Log($"{mods.Count(m => m.Running)} of {mods.Count} mod(s) running");
    }

    static void Load(string name)
    {
        string dir = Path.Combine(ModsDir, name);
        var mod = new Mod(name, dir + Path.DirectorySeparatorChar);
        mods.Add(mod);
        try
        {
            mod.Log = new ModLog(name, Path.Combine(dir, name + ".log"));
            mod.Log.Write($"---- {name} loading");
            mod.Context = new ModLoadContext(dir, name);
            Assembly asm = LoadUnlocked(mod.Context, Path.Combine(dir, name + ".dll"));
            mod.Version = asm.GetName().Version?.ToString(3) ?? "";
            var types = asm.GetTypes().Where(t => !t.IsAbstract && typeof(Script).IsAssignableFrom(t)).ToList();
            if (types.Count == 0)
            {
                Native.Log($"{name}: no Script classes; not a mod");
                return;
            }
            foreach (Type t in types)
            {
                var script = (Script)Activator.CreateInstance(t)!;
                script.Log = mod.Log;
                script.Directory = mod.Directory;
                script.ModName = name;
                mod.Scripts.Add(script);
            }
            mod.Running = true;
            foreach (Script s in mod.Scripts)
            {
                Commands.Register(mod, s);
                RegisterHooks(mod, s);
            }
            foreach (Script s in mod.Scripts)
                Run(mod, s, "OnStart", x => x.OnStart());
            if (mod.Running && Lifecycle.MapLoaded && Game.Info is GameInfo game)
                foreach (Script s in mod.Scripts)
                    Run(mod, s, "OnMapLoaded", x => x.OnMapLoaded(game));
            if (mod.Running)
                Native.Log($"{name} {mod.Version} started ({string.Join(", ", mod.Scripts.Select(s => s.GetType().Name))})");
        }
        catch (Exception e)
        {
            Fault(mod, "loading", e is TargetInvocationException { InnerException: not null } t ? t.InnerException! : e);
        }
    }

    static void RegisterHooks(Mod mod, Script script)
    {
        foreach (MethodInfo m in script.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            foreach (HookAttribute a in m.GetCustomAttributes<HookAttribute>())
            {
                var ps = m.GetParameters();
                if (ps.Length != 2 || ps[1].ParameterType != typeof(HookFrame) || !typeof(UObject).IsAssignableFrom(ps[0].ParameterType))
                {
                    mod.Log?.Warn($"[Hook] {m.Name}: needs (UObject-or-subclass self, HookFrame frame)");
                    continue;
                }
                Type selfType = ps[0].ParameterType;
                bool canSkip = m.ReturnType == typeof(bool);
                HookBefore? before = null;
                HookAfter? after = null;
                if (a.After)
                    after = (self, frame) =>
                    {
                        if (selfType.IsInstanceOfType(self))
                            Invoke(m, script, self, frame);
                    };
                else
                    before = (self, frame) =>
                        !selfType.IsInstanceOfType(self) || Invoke(m, script, self, frame) is not false || !canSkip;
                Hooks.Add(a.Class, a.Function, before, after, mod);
            }
    }

    static object? Invoke(MethodInfo m, object target, params object?[] args)
    {
        try
        {
            return m.Invoke(target, args);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    /// <summary>Runs a script callback; an exception disables its mod.</summary>
    internal static void Run(Mod mod, Script script, string what, Action<Script> call)
    {
        if (!mod.Running)
            return;
        try
        {
            call(script);
        }
        catch (Exception e)
        {
            Fault(mod, $"{script.GetType().Name}.{what}", e);
        }
    }

    /// <summary>Calls <paramref name="call"/> on every running script.</summary>
    internal static void ForEachScript(string what, Action<Script> call)
    {
        foreach (Mod mod in mods.ToArray())
            foreach (Script s in mod.Scripts)
                Run(mod, s, what, call);
    }

    /// <summary>Records an exception from mod code and stops calling that mod.</summary>
    internal static void Fault(Mod? mod, string what, Exception e)
    {
        string text = $"{what} threw {e}";
        if (mod == null)
        {
            Native.Log($"SDK: {text}");
            return;
        }
        mod.Log?.Write(text);
        Native.Log($"{mod.Name}: {what} threw {e.GetType().Name}: {e.Message}; mod disabled (\"!sdk reload {mod.Name}\" to retry)");
        mod.Running = false;
        Hooks.RemoveAll(mod);
        Commands.RemoveAll(mod);
        Bridge.RemoveAll(mod);
    }

    /// <summary>The mod an assembly belongs to, or null (the SDK itself).</summary>
    internal static Mod? OwnerOf(Assembly? asm)
    {
        if (asm == null)
            return null;
        foreach (Mod m in mods)
            if (m.Context != null && AssemblyLoadContext.GetLoadContext(asm) == m.Context)
                return m;
        return null;
    }

    /// <summary>Reloads a mod (or all with null) at the end of this tick: it may be called from inside a hook.</summary>
    public static void QueueReload(string? name) => reloads.Enqueue(name);

    internal static void ProcessReloads()
    {
        while (reloads.Count > 0)
        {
            string? name = reloads.Dequeue();
            var targets = mods.Where(m => name == null || m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            var names = targets.Select(m => m.Name).ToList();
            if (name != null && targets.Count == 0 && File.Exists(Path.Combine(ModsDir, name, name + ".dll")))
                names.Add(name);  // a new mod
            foreach (Mod m in targets)
                Unload(m);
            Hooks.Sweep();
            if (name == null)
                LoadAll();
            else
                foreach (string n in names)
                    Load(n);
        }
    }

    static void Unload(Mod mod)
    {
        if (mod.Running)
            foreach (Script s in mod.Scripts)
                Run(mod, s, "OnStop", x => x.OnStop());
        mod.Running = false;
        Hooks.RemoveAll(mod);
        Commands.RemoveAll(mod);
        Bridge.RemoveAll(mod);
        mod.Scripts.Clear();
        mod.Log?.Write($"---- {mod.Name} unloaded");
        mod.Log?.Close();
        mod.Context?.Unload();
        mod.Context = null;
        mods.Remove(mod);
        Native.Log($"{mod.Name} unloaded");
    }
}
