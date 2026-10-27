using System;
using System.Collections.Generic;
using HomefrontSDK.Internal;

namespace HomefrontSDK;

/// <summary>Runs before a hooked function. Return false to skip the original (script functions only: a skipped
/// native would leave its arguments unread).</summary>
public delegate bool HookBefore(UObject self, HookFrame frame);

/// <summary>Runs after a hooked function (also when a "before" hook skipped it).</summary>
public delegate void HookAfter(UObject self, HookFrame frame);

/// <summary>The call a hook sees: the function and its parameters, read by name.</summary>
/// <remarks>Only reliable for script functions: a native function called from script reads its arguments from the
/// caller's frame, so <see cref="Function"/> is the caller there.</remarks>
public readonly unsafe struct HookFrame
{
    readonly nint frame;

    internal HookFrame(nint frame) => this.frame = frame;

    /// <summary>The UFunction running.</summary>
    public nint Function => *(nint*)(frame + Layout.FrameNode);

    /// <summary>The function's parameters and locals.</summary>
    public byte* Locals => *(byte**)(frame + Layout.FrameLocals);

    /// <summary>The name of the function running.</summary>
    public string FunctionName => Names.ObjectName(Function);

    byte* Slot(string param)
    {
        int offset = Objects.ParamOffset(Function, param);
        if (offset < 0)
            throw new ArgumentException($"{Names.ObjectFullName(Function)} has no parameter {param}");
        return Locals + offset;
    }

    /// <summary>A plain-value parameter (int, float, FName, a struct...), by reference: assign to change it.</summary>
    public ref T Param<T>(string param) where T : unmanaged => ref *(T*)Slot(param);

    /// <summary>An object parameter.</summary>
    public T? Object<T>(string param) where T : UObject => UObject.Get<T>(*(nint*)Slot(param));

    /// <summary>A string parameter.</summary>
    public string String(string param) => ((FString*)Slot(param))->ToString();

    /// <summary>A bool parameter.</summary>
    public bool Bool(string param) => *(uint*)Slot(param) != 0;
}

/// <summary>A hook added with Hooks.Add; dispose it to remove the hook.</summary>
public sealed class HookHandle : IDisposable
{
    internal readonly HookBefore? Before;
    internal readonly HookAfter? After;
    internal readonly Mod? Owner;
    internal int Depth;
    internal bool Removed;

    internal HookHandle(HookBefore? before, HookAfter? after, Mod? owner)
    {
        Before = before;
        After = after;
        Owner = owner;
    }

    /// <summary>How many functions it hooked (overrides and state versions included).</summary>
    public int Functions { get; internal set; }

    public void Dispose() => Hooks.Remove(this);
}

/// <summary>Hooks on script functions.</summary>
public static unsafe class Hooks
{
    // Per native hook slot: the handles hooked there. A handle hooking several functions (overrides) appears in
    // several slots; its Depth makes it see only the outermost call when they call each other (super calls).
    static readonly List<HookHandle>?[] slots = new List<HookHandle>?[4096];
    static readonly Stack<ulong> outermost = new();  // per active hooked call: which handles were outermost
    static readonly List<HookHandle> all = new();

    /// <summary>Hooks every function named <paramref name="function"/> in class <paramref name="className"/>
    /// ("GameInfo" or "Engine.GameInfo") and its subclasses, state versions included.</summary>
    public static HookHandle Add(string className, string function, HookBefore? before, HookAfter? after = null) =>
        Add(className, function, before, after, Mods.OwnerOf(before?.Method.Module.Assembly ?? after?.Method.Module.Assembly));

    internal static HookHandle Add(string className, string function, HookBefore? before, HookAfter? after, Mod? owner)
    {
        var handle = new HookHandle(before, after, owner);
        nint baseClass = Objects.FindClass(className);
        if (baseClass == 0)
        {
            Native.Log($"hooks: class {className} not found");
            return handle;
        }
        int count = Objects.Count;
        nint functionClass = Objects.FindClass("Core.Function");
        for (int i = 0; i < count; ++i)
        {
            nint o = Objects.At(i);
            if (o == 0 || *(nint*)(o + Layout.Class) != functionClass || Names.ObjectName(o) != function)
                continue;
            if (!DerivesFrom(OwnerClass(o), baseClass))
                continue;
            int slot = Native.HookFunction(o);
            if (slot < 0 || slot >= slots.Length)
            {
                Native.Log($"hooks: out of hook slots at {Names.ObjectFullName(o)}");
                break;
            }
            (slots[slot] ??= new List<HookHandle>()).Add(handle);
            handle.Functions++;
        }
        all.Add(handle);
        Native.Log($"hooks: {className}.{function}: {handle.Functions} function(s){(handle.Owner != null ? " for " + handle.Owner.Name : "")}");
        return handle;
    }

    /// <summary>Removes a hook (the function keeps its native thunk, which then just runs the original). It stops
    /// running at once; its bookkeeping is dropped at the end of the tick, outside any hooked call.</summary>
    public static void Remove(HookHandle handle)
    {
        if (handle.Removed)
            return;
        handle.Removed = true;
        removed.Add(handle);
    }

    internal static void RemoveAll(Mod owner)
    {
        foreach (var h in all)
            if (h.Owner == owner)
                Remove(h);
    }

    static readonly List<HookHandle> removed = new();

    /// <summary>Drops removed hooks from the slot lists; called by the tick, when no hooked call is running.</summary>
    internal static void Sweep()
    {
        if (removed.Count == 0)
            return;
        foreach (var list in slots)
            list?.RemoveAll(h => h.Removed);
        all.RemoveAll(h => h.Removed);
        removed.Clear();
    }

    internal static int Count => all.Count;

    // The class that owns a function (functions inside states have the state as outer).
    static nint OwnerClass(nint function)
    {
        nint classClass = Objects.ClassClass;
        for (nint o = *(nint*)(function + Layout.Outer); o != 0; o = *(nint*)(o + Layout.Outer))
            if (*(nint*)(o + Layout.Class) == classClass)
                return o;
        return 0;
    }

    static bool DerivesFrom(nint cls, nint baseClass)
    {
        for (nint c = cls; c != 0; c = *(nint*)(c + Layout.StructSuper))
            if (c == baseClass)
                return true;
        return false;
    }

    // ---- called by the host through Loader
    internal static int Pre(int slot, nint self, nint frame)
    {
        var list = (uint)slot < (uint)slots.Length ? slots[slot] : null;
        ulong mask = 0;
        bool run = true;
        if (list != null && list.Count > 0)
        {
            UObject? obj = null;
            for (int i = 0; i < list.Count && i < 64; ++i)
            {
                HookHandle h = list[i];
                if (h.Depth++ != 0)
                    continue;
                mask |= 1UL << i;
                if (h.Before == null || h.Removed)
                    continue;
                obj ??= UObject.Get<UObject>(self);
                try
                {
                    if (!h.Before(obj!, new HookFrame(frame)))
                        run = false;
                }
                catch (Exception e)
                {
                    Mods.Fault(h.Owner, $"hook before {Names.ObjectFullName(*(nint*)(frame + Layout.FrameNode))}", e);
                }
            }
        }
        outermost.Push(mask);
        return (run ? 1 : 0) | 2;
    }

    internal static void Post(int slot, nint self, nint frame)
    {
        ulong mask = outermost.Count > 0 ? outermost.Pop() : 0;
        var list = (uint)slot < (uint)slots.Length ? slots[slot] : null;
        if (list == null)
            return;
        UObject? obj = null;
        for (int i = 0; i < list.Count && i < 64; ++i)
        {
            HookHandle h = list[i];
            if (h.Depth > 0)
                --h.Depth;
            if ((mask & (1UL << i)) == 0 || h.After == null || h.Removed)
                continue;
            obj ??= UObject.Get<UObject>(self);
            try
            {
                h.After(obj!, new HookFrame(frame));
            }
            catch (Exception e)
            {
                Mods.Fault(h.Owner, $"hook after {Names.ObjectFullName(*(nint*)(frame + Layout.FrameNode))}", e);
            }
        }
    }
}
