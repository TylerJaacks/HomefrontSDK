using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HomefrontSDK.Internal;

namespace HomefrontSDK;

/// <summary>The engine's name table (GNames).</summary>
public static unsafe class Names
{
    static readonly Dictionary<string, int> found = new();

    static nint* Data => *(nint**)Native.Api->GNames;
    static int Num => *(int*)(Native.Api->GNames + 4);

    /// <summary>The text of name <paramref name="index"/>, or null (out of range or a Unicode entry, which this
    /// build's script names don't use).</summary>
    public static string? Text(int index)
    {
        if (index < 0 || index >= Num)
            return null;
        nint entry = Data[index];
        if (entry == 0 || (*(int*)(entry + Layout.NameEntryIndex) & 1) != 0)
            return null;
        return Marshal.PtrToStringAnsi(entry + Layout.NameEntryText);
    }

    /// <summary>The index of an existing name, or 0 (None).</summary>
    public static int Find(string text)
    {
        if (found.TryGetValue(text, out int index))
            return index;
        int num = Num;
        for (int i = 0; i < num; ++i)
            if (Text(i) == text)
            {
                index = i;
                break;
            }
        found[text] = index;
        return index;
    }

    /// <summary>An object's name ("Foo" or "Foo_3").</summary>
    public static string ObjectName(nint obj) => obj == 0 ? "None" : (*(FName*)(obj + Layout.Name)).ToString();

    /// <summary>"Package.Outer.Name".</summary>
    public static string ObjectFullName(nint obj)
    {
        if (obj == 0)
            return "None";
        string s = ObjectName(obj);
        for (nint o = *(nint*)(obj + Layout.Outer); o != 0; o = *(nint*)(o + Layout.Outer))
            s = ObjectName(o) + "." + s;
        return s;
    }
}

/// <summary>The engine's object table (GObjects) and its reflection data.</summary>
public static unsafe class Objects
{
    static readonly Dictionary<string, nint> byPath = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(nint, int), nint> functions = new();
    static readonly Dictionary<(nint, string), nint> members = new();
    static nint classClass;

    static nint* Data => *(nint**)Native.Api->GObjects;

    /// <summary>Slots in the object table (some are empty).</summary>
    public static int Count => *(int*)(Native.Api->GObjects + 4);

    /// <summary>The object in slot <paramref name="index"/>, or 0.</summary>
    public static nint At(int index) => (uint)index < (uint)Count ? Data[index] : 0;

    /// <summary>Every live object of class <typeparamref name="T"/> (or a subclass). Walks the whole table.</summary>
    public static List<T> All<T>() where T : UObject
    {
        var result = new List<T>();
        nint cls = UObject.ClassPtr<T>();
        if (cls == 0)
            return result;
        int count = Count;
        for (int i = 0; i < count; ++i)
        {
            nint o = Data[i];
            if (o != 0 && IsA(o, cls) && UObject.Get<T>(o) is T t)
                result.Add(t);
        }
        return result;
    }

    /// <summary>An object by full path ("Engine.Default__Actor", "Engine.Actor"), cached.</summary>
    public static nint Find(string path)
    {
        if (byPath.TryGetValue(path, out nint cached) && cached != 0 && Names.ObjectFullName(cached).Equals(path, StringComparison.OrdinalIgnoreCase))
            return cached;
        int dot = path.LastIndexOf('.');
        string last = dot >= 0 ? path[(dot + 1)..] : path;
        int count = Count;
        nint result = 0;
        for (int i = 0; i < count && result == 0; ++i)
        {
            nint o = Data[i];
            if (o != 0 && Names.ObjectName(o).Equals(last, StringComparison.OrdinalIgnoreCase) &&
                Names.ObjectFullName(o).Equals(path, StringComparison.OrdinalIgnoreCase))
                result = o;
        }
        byPath[path] = result;
        return result;
    }

    /// <summary>A class by short name ("Actor") or full path ("Engine.Actor"), or 0.</summary>
    public static nint FindClass(string name)
    {
        if (name.Contains('.'))
            return Find(name);
        if (byPath.TryGetValue("class:" + name, out nint cached))
            return cached;
        nint cc = ClassClass;
        int count = Count;
        nint result = 0;
        for (int i = 0; i < count && result == 0; ++i)
        {
            nint o = Data[i];
            if (o != 0 && *(nint*)(o + Layout.Class) == cc && Names.ObjectName(o) == name)
                result = o;
        }
        byPath["class:" + name] = result;
        return result;
    }

    /// <summary>The class "Class" (every class object's class).</summary>
    internal static nint ClassClass
    {
        get
        {
            if (classClass == 0)
                for (int i = 0; i < Count && classClass == 0; ++i)
                    if (Data[i] != 0 && Names.ObjectName(Data[i]) == "Class" && Names.ObjectFullName(Data[i]) == "Core.Class")
                        classClass = Data[i];
            return classClass;
        }
    }

    /// <summary>Whether <paramref name="obj"/>'s class is <paramref name="cls"/> or derives from it.</summary>
    public static bool IsA(nint obj, nint cls)
    {
        if (obj == 0 || cls == 0)
            return false;
        for (nint c = *(nint*)(obj + Layout.Class); c != 0; c = *(nint*)(c + Layout.StructSuper))
            if (c == cls)
                return true;
        return false;
    }

    /// <summary>The name of an object's class ("PlayerController").</summary>
    public static string ClassName(nint obj) => obj == 0 ? "None" : Names.ObjectName(*(nint*)(obj + Layout.Class));

    /// <summary>The first field named <paramref name="name"/> in a struct or class chain (properties and functions
    /// alike), or 0.</summary>
    public static nint FindMember(nint structure, string name)
    {
        if (members.TryGetValue((structure, name), out nint cached))
            return cached;
        nint result = 0;
        for (nint s = structure; s != 0 && result == 0; s = *(nint*)(s + Layout.StructSuper))
            for (nint c = *(nint*)(s + Layout.StructChildren); c != 0; c = *(nint*)(c + Layout.FieldNext))
                if (Names.ObjectName(c) == name)
                {
                    result = c;
                    break;
                }
        members[(structure, name)] = result;
        return result;
    }

    /// <summary>A property's offset in its owner (searches the class chain), or -1.</summary>
    public static int PropertyOffset(nint structure, string name)
    {
        nint p = FindMember(structure, name);
        return p != 0 && ClassName(p).EndsWith("Property", StringComparison.Ordinal) ? *(int*)(p + Layout.PropertyOffset) : -1;
    }

    /// <summary>A parameter's offset in a function's parameter block, or -1.</summary>
    public static int ParamOffset(nint function, string name)
    {
        for (nint c = *(nint*)(function + Layout.StructChildren); c != 0; c = *(nint*)(c + Layout.FieldNext))
            if (Names.ObjectName(c) == name)
                return *(int*)(c + Layout.PropertyOffset);
        return -1;
    }

    /// <summary>The function a script call of <paramref name="name"/> on <paramref name="obj"/> runs: the version in
    /// the object's current state (and its parent states), else the one in its class chain, like
    /// UObject::FindFunction. 0 if there is none.</summary>
    public static nint ResolveFunction(nint obj, FName name)
    {
        nint frame = *(nint*)(obj + Layout.StateFrame);
        nint state = frame != 0 ? *(nint*)(frame + Layout.StateFrameNode) : 0;
        nint cls = *(nint*)(obj + Layout.Class);
        if (state != 0)
        {
            nint f = FunctionIn(state, name);
            if (f != 0)
                return f;
        }
        return FunctionIn(cls, name);
    }

    static nint FunctionIn(nint structure, FName name)
    {
        if (functions.TryGetValue((structure, name.Index), out nint cached))
            return cached;
        nint result = 0;
        for (nint s = structure; s != 0 && result == 0; s = *(nint*)(s + Layout.StructSuper))
            for (nint c = *(nint*)(s + Layout.StructChildren); c != 0; c = *(nint*)(c + Layout.FieldNext))
                if ((*(FName*)(c + Layout.Name)).Index == name.Index && ClassName(c) == "Function")
                {
                    result = c;
                    break;
                }
        functions[(structure, name.Index)] = result;
        return result;
    }

    /// <summary>Drops cached lookups that can go stale when objects are garbage collected (map changes).</summary>
    internal static void ForgetObjects()
    {
        byPath.Clear();
    }
}
