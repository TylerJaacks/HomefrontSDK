using System;
using System.Collections.Generic;
using System.Reflection;
using HomefrontSDK.Internal;

namespace HomefrontSDK;

/// <summary>Marks a generated class or struct with the script type it wraps ("Engine.Actor").</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
public sealed class ScriptPathAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}

/// <summary>Every game object's wrapper: a pointer to the engine object plus its slot in the object table. The
/// script properties and functions of Core.Object are in the generated half of this class.</summary>
/// <remarks>Wrappers are cached per object; get one with <see cref="Get{T}"/>. An object can be garbage collected
/// while you hold its wrapper: check <see cref="IsValid"/> before using one kept across ticks.</remarks>
public unsafe partial class UObject : IEquatable<UObject>
{
    /// <summary>The engine object.</summary>
    public readonly nint Ptr;

    /// <summary>Its slot in the object table when the wrapper was made.</summary>
    public readonly int ObjectIndex;

    protected internal UObject(nint ptr)
    {
        Ptr = ptr;
        ObjectIndex = *(int*)(ptr + Layout.ObjectIndex);
    }

    /// <summary>Whether the object still exists (its object-table slot still holds it). Destroyed actors stay valid
    /// until garbage collection: check Actor.bDeleteMe for those.</summary>
    public bool IsValid => Objects.At(ObjectIndex) == Ptr;

    /// <summary>The object's name ("Bot_3").</summary>
    public string ObjectName => Names.ObjectName(Ptr);

    /// <summary>"Package.Outer.Name".</summary>
    public string FullName => Names.ObjectFullName(Ptr);

    /// <summary>The name of the object's class.</summary>
    public string ClassName => Objects.ClassName(Ptr);

    /// <summary>The name of the state the object is in ("Searching", "Dead"), or "" when it has none.</summary>
    public string StateName
    {
        get
        {
            nint frame = *(nint*)(Ptr + Layout.StateFrame);
            nint node = frame != 0 ? *(nint*)(frame + Layout.StateFrameNode) : 0;
            return node != 0 ? Names.ObjectName(node) : "";
        }
    }

    /// <summary>Whether the object is a <typeparamref name="T"/> (or a subclass of it).</summary>
    public bool IsA<T>() where T : UObject => Objects.IsA(Ptr, ClassPtr<T>());

    /// <summary>The object as a <typeparamref name="T"/>, or null if it isn't one.</summary>
    public T? Cast<T>() where T : UObject => IsA<T>() ? this as T ?? Get<T>(Ptr) : null;

    /// <summary>The wrapper of the object at <paramref name="ptr"/>, as its most derived generated class; null for 0
    /// or when that isn't a <typeparamref name="T"/>.</summary>
    public static T? Get<T>(nint ptr) where T : UObject => ptr == 0 ? null : ObjectCache.Wrap(ptr) as T;

    /// <summary>The engine class object of <typeparamref name="T"/>, or 0 if the game has no such class loaded.</summary>
    public static nint ClassPtr<T>() where T : UObject => ClassOf<T>.Ptr;

    /// <summary>The script path of <typeparamref name="T"/> ("Engine.Actor").</summary>
    public static string ScriptPath<T>() where T : UObject => ClassOf<T>.Path;

    public bool Equals(UObject? other) => other is not null && other.Ptr == Ptr;
    public override bool Equals(object? obj) => obj is UObject o && o.Ptr == Ptr;
    public override int GetHashCode() => Ptr.GetHashCode();
    public static bool operator ==(UObject? a, UObject? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(UObject? a, UObject? b) => !(a == b);
    public override string ToString() => FullName;

    static class ClassOf<T> where T : UObject
    {
        public static readonly string Path = typeof(T).GetCustomAttribute<ScriptPathAttribute>()?.Path ?? "";
        static nint ptr;

        public static nint Ptr => ptr != 0 ? ptr : ptr = Path.Length > 0 ? Objects.Find(Path) : 0;
    }
}

/// <summary>Object wrappers, one per engine object, of the most derived generated class.</summary>
internal static unsafe class ObjectCache
{
    // An object freed by garbage collection can be replaced by a new one at the same address and slot, so a cached
    // wrapper is reused only when the class matches too.
    static readonly Dictionary<nint, (UObject wrapper, nint cls)> wrappers = new();
    static readonly Dictionary<nint, Func<nint, UObject>> factories = new();  // class object -> constructor
    static Dictionary<string, Func<nint, UObject>>? byPath;

    public static UObject Wrap(nint ptr)
    {
        int index = *(int*)(ptr + Layout.ObjectIndex);
        nint cls = *(nint*)(ptr + Layout.Class);
        if (wrappers.TryGetValue(ptr, out var w) && w.wrapper.ObjectIndex == index && w.cls == cls)
            return w.wrapper;
        UObject wrapper = FactoryFor(cls)(ptr);
        wrappers[ptr] = (wrapper, cls);
        return wrapper;
    }

    static Func<nint, UObject> FactoryFor(nint cls)
    {
        if (factories.TryGetValue(cls, out var f))
            return f;
        byPath ??= GeneratedRegistry.Factories();
        f = p => new UObject(p);
        for (nint c = cls; c != 0; c = *(nint*)(c + Layout.StructSuper))
            if (byPath.TryGetValue(Names.ObjectFullName(c), out var found))
            {
                f = found;
                break;
            }
        factories[cls] = f;
        return f;
    }

    /// <summary>Forgets wrappers of objects that are gone (called on map changes).</summary>
    public static void Prune()
    {
        var dead = new List<nint>();
        foreach (var (ptr, w) in wrappers)
            if (!w.wrapper.IsValid)
                dead.Add(ptr);
        foreach (nint p in dead)
            wrappers.Remove(p);
    }
}

/// <summary>A script function as the generated code calls it: found by name on first use, then called through
/// ProcessEvent. Non-final functions are looked up on the object the way script does (state, then class), so
/// overrides run.</summary>
public sealed unsafe class FunctionRef
{
    readonly string owner;  // "Engine.Actor"
    readonly string name;
    readonly bool final;
    nint declared;
    FName fname;

    public FunctionRef(string ownerPath, string functionName, bool isFinal)
    {
        owner = ownerPath;
        name = functionName;
        final = isFinal;
    }

    /// <summary>The function as declared in its class, or 0 when the game doesn't have it.</summary>
    public nint Declared
    {
        get
        {
            if (declared == 0)
            {
                nint cls = Objects.Find(owner);
                declared = cls != 0 ? Objects.FindMember(cls, name) : 0;
            }
            return declared;
        }
    }

    /// <summary>Runs the function on <paramref name="self"/> with the parameter block <paramref name="parms"/>.</summary>
    public void Invoke(UObject self, byte* parms)
    {
        nint fn;
        if (final)
        {
            fn = Declared;
        }
        else
        {
            if (fname.IsNone)
                fname = FName.Find(name);
            fn = Objects.ResolveFunction(self.Ptr, fname);
        }
        if (fn == 0)
            throw new MissingMethodException($"{owner}.{name} not found on {self.FullName}");
        Native.ProcessEvent(self.Ptr, fn, parms);
    }

    /// <summary>Runs a static function, on its class's default object.</summary>
    public void InvokeStatic(byte* parms)
    {
        int dot = owner.IndexOf('.');
        nint cdo = Objects.Find($"{owner[..dot]}.Default__{owner[(dot + 1)..]}");
        if (cdo == 0 || Declared == 0)
            throw new MissingMethodException($"{owner}.{name} (static) not found");
        Native.ProcessEvent(cdo, Declared, parms);
    }
}

/// <summary>Marshalling helpers the generated code uses.</summary>
public static unsafe class Marshalling
{
    /// <summary>Points an FString parameter at a temporary copy of <paramref name="s"/>. The buffer must outlive
    /// the call (the generated code stackallocs it).</summary>
    public static void WriteString(byte* slot, string? s, char* buffer)
    {
        s ??= "";
        s.AsSpan().CopyTo(new Span<char>(buffer, s.Length));
        buffer[s.Length] = '\0';
        var f = (FString*)slot;
        f->Data = buffer;
        f->Num = f->Max = s.Length + 1;
    }

    /// <summary>Reads an FString the engine wrote (a return value or out parameter). The engine's copy is left
    /// allocated: there is no allocator wrapper yet, so this leaks a few bytes per call.</summary>
    public static string ReadString(byte* slot) => ((FString*)slot)->ToString();
}
