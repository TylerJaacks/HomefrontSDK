using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace HomefrontSDK;

/// <summary>Reads and writes any object's script properties through the engine's reflection data (not the generated
/// bindings), for the editor's property grid. Object pointers found in properties are checked against the object
/// table before anything reads through them.</summary>
internal static unsafe class Inspector
{
    const int MaxDepth = 3, MaxArrayItems = 64;

    /// <summary>Whether <paramref name="p"/> is a live object (its table slot holds it). Safe on garbage pointers.</summary>
    public static bool IsObject(nint p)
    {
        if (p == 0)
            return false;
        int index;
        return Native.TryRead(p + Layout.ObjectIndex, &index, 4) && Objects.At(index) == p;
    }

    static string Kind(nint prop) => Objects.ClassName(prop);
    static int Offset(nint prop) => *(int*)(prop + Layout.PropertyOffset);
    static int ArrayDim(nint prop) => *(int*)(prop + Layout.PropertyArrayDim);
    static int ElementSize(nint prop) => *(int*)(prop + Layout.PropertyElementSize);
    static ulong Flags(nint prop) => *(ulong*)(prop + Layout.PropertyFlags);
    static nint Extra(nint prop) => *(nint*)(prop + Layout.PropertyExtra0);  // Struct/Inner/Enum/PropertyClass/BitMask

    /// <summary>The properties of a struct or class (its whole chain, base first).</summary>
    public static List<nint> PropertiesOf(nint structure)
    {
        var chain = new List<nint>();
        for (nint s = structure; s != 0; s = *(nint*)(s + Layout.StructSuper))
            chain.Insert(0, s);
        var props = new List<nint>();
        foreach (nint s in chain)
            for (nint c = *(nint*)(s + Layout.StructChildren); c != 0; c = *(nint*)(c + Layout.FieldNext))
                if (Kind(c).EndsWith("Property", StringComparison.Ordinal))
                    props.Add(c);
        return props;
    }

    /// <summary>An object: identity plus every property.</summary>
    public static JsonObject Describe(nint obj)
    {
        nint cls = *(nint*)(obj + Layout.Class);
        var props = new JsonArray();
        foreach (nint p in PropertiesOf(cls))
        {
            var entry = new JsonObject
            {
                ["name"] = Names.ObjectName(p),
                ["kind"] = Kind(p),
                ["owner"] = Names.ObjectName(*(nint*)(p + Layout.Outer)),
                ["offset"] = Offset(p),
                ["editable"] = Editable(p),
                ["value"] = ValueOf(p, obj + Offset(p), 0),
            };
            if (Kind(p) == "ByteProperty" && Extra(p) != 0)
                entry["enum"] = EnumNames(Extra(p));
            if ((Flags(p) & 0x1) != 0)
                entry["edit"] = true;  // CPF_Edit: shown in the game's own editor
            props.Add(entry);
        }
        nint frame = *(nint*)(obj + Layout.StateFrame);
        nint state = frame != 0 ? *(nint*)(frame + Layout.StateFrameNode) : 0;
        nint outer = *(nint*)(obj + Layout.Outer);
        return new JsonObject
        {
            ["ptr"] = (long)obj,
            ["name"] = Names.ObjectName(obj),
            ["class"] = Names.ObjectName(cls),
            ["path"] = Names.ObjectFullName(obj),
            ["outer"] = outer != 0 ? Names.ObjectFullName(outer) : null,
            ["state"] = state != 0 ? Names.ObjectName(state) : "",
            ["properties"] = props,
        };
    }

    static bool Editable(nint prop) =>
        ArrayDim(prop) <= 1 && Kind(prop) is "IntProperty" or "FloatProperty" or "ByteProperty" or "BoolProperty" or
            "NameProperty" or "ObjectProperty" or "ClassProperty" or "ComponentProperty" or "StructProperty";

    static JsonArray EnumNames(nint e)
    {
        var names = new JsonArray();
        nint data = *(nint*)(e + 0x40);
        int num = *(int*)(e + 0x44);
        for (int i = 0; i < num && i < 256; ++i)
            names.Add(((FName*)data)[i].ToString());
        return names;
    }

    static JsonNode? ValueOf(nint prop, nint addr, int depth)
    {
        int dim = ArrayDim(prop);
        if (dim > 1)
        {
            var items = new JsonArray();
            for (int i = 0; i < dim && i < MaxArrayItems; ++i)
                items.Add(Single(prop, addr + i * ElementSize(prop), depth));
            return items;
        }
        return Single(prop, addr, depth);
    }

    static JsonNode? ObjectRef(nint p)
    {
        if (p == 0)
            return null;
        if (!IsObject(p))
            return new JsonObject { ["ptr"] = (long)p, ["path"] = "(not an object)" };
        return new JsonObject { ["ptr"] = (long)p, ["path"] = Names.ObjectFullName(p), ["class"] = Objects.ClassName(p) };
    }

    static JsonNode? Single(nint prop, nint addr, int depth)
    {
        switch (Kind(prop))
        {
            case "IntProperty": return *(int*)addr;
            case "FloatProperty": return *(float*)addr;
            case "BoolProperty": return (*(uint*)addr & (uint)Extra(prop)) != 0;
            case "NameProperty": return (*(FName*)addr).ToString();
            case "StrProperty": return ((FString*)addr)->ToString();
            case "ByteProperty":
            {
                byte v = *(byte*)addr;
                nint e = Extra(prop);
                if (e == 0)
                    return v;
                nint data = *(nint*)(e + 0x40);
                int num = *(int*)(e + 0x44);
                return v < num ? ((FName*)data)[v].ToString() : v.ToString(CultureInfo.InvariantCulture);
            }
            case "ObjectProperty":
            case "ComponentProperty":
            case "ClassProperty":
                return ObjectRef(*(nint*)addr);
            case "InterfaceProperty":
                return ObjectRef(*(nint*)addr);
            case "DelegateProperty":
            {
                var d = (FScriptDelegate*)addr;
                return d->Object == 0 && d->FunctionName.IsNone ? null : $"{(IsObject(d->Object) ? Names.ObjectName(d->Object) : "?")}.{d->FunctionName}";
            }
            case "StructProperty":
            {
                nint st = Extra(prop);
                if (depth >= MaxDepth)
                    return "{" + Names.ObjectName(st) + "}";
                var fields = new JsonObject();
                foreach (nint f in PropertiesOf(st))
                    fields[Names.ObjectName(f)] = ValueOf(f, addr + Offset(f), depth + 1);
                return fields;
            }
            case "ArrayProperty":
            {
                nint inner = Extra(prop);
                nint data = *(nint*)addr;
                int num = *(int*)(addr + 4);
                var result = new JsonObject { ["count"] = num };
                if (depth < MaxDepth && data != 0)
                {
                    var items = new JsonArray();
                    for (int i = 0; i < num && i < MaxArrayItems; ++i)
                        items.Add(Single(inner, data + i * ElementSize(inner), depth + 1));
                    result["items"] = items;
                }
                return result;
            }
            default:
                return "(" + Kind(prop) + ")";
        }
    }

    /// <summary>Sets a property by path: "Health", "Location.X", "bHidden". Returns the new value.</summary>
    public static JsonNode? Set(nint obj, string path, JsonNode? value)
    {
        nint structure = *(nint*)(obj + Layout.Class);
        nint addr = obj;
        nint prop = 0;
        string[] parts = path.Split('.');
        for (int i = 0; i < parts.Length; ++i)
        {
            prop = Objects.FindMember(structure, parts[i]);
            if (prop == 0 || !Kind(prop).EndsWith("Property", StringComparison.Ordinal))
                throw new ArgumentException($"no property {parts[i]} in {Names.ObjectName(structure)}");
            addr += Offset(prop);
            if (i < parts.Length - 1)
            {
                if (Kind(prop) != "StructProperty")
                    throw new ArgumentException($"{parts[i]} is not a struct");
                structure = Extra(prop);
            }
        }
        if (!Editable(prop))
            throw new ArgumentException($"{path} ({Kind(prop)}) can't be set here");
        switch (Kind(prop))
        {
            case "IntProperty":
                *(int*)addr = (int)Number(value);
                break;
            case "FloatProperty":
                *(float*)addr = (float)Number(value);
                break;
            case "BoolProperty":
            {
                uint mask = (uint)Extra(prop);
                bool on = value is JsonValue v && v.TryGetValue(out bool b) ? b : Number(value) != 0;
                *(uint*)addr = on ? *(uint*)addr | mask : *(uint*)addr & ~mask;
                break;
            }
            case "ByteProperty":
            {
                nint e = Extra(prop);
                if (e != 0 && value is JsonValue v && v.TryGetValue(out string? s))
                {
                    nint data = *(nint*)(e + 0x40);
                    int num = *(int*)(e + 0x44);
                    int found = -1;
                    for (int i = 0; i < num && found < 0; ++i)
                        if (((FName*)data)[i].ToString() == s)
                            found = i;
                    *(byte*)addr = found >= 0 ? (byte)found : throw new ArgumentException($"{s} isn't a value of {Names.ObjectName(e)}");
                }
                else
                {
                    *(byte*)addr = (byte)Number(value);
                }
                break;
            }
            case "NameProperty":
            {
                string text = value?.GetValue<string>() ?? "None";
                FName n = FName.Find(text);
                if (n.IsNone && text != "None")
                    throw new ArgumentException($"no name \"{text}\" exists yet (names can't be created here)");
                *(FName*)addr = n;
                break;
            }
            case "ObjectProperty":
            case "ClassProperty":
            case "ComponentProperty":
            {
                nint p = value == null ? 0 : (nint)Number(value is JsonObject o ? o["ptr"] : value);
                if (p != 0 && (!IsObject(p) || !Objects.IsA(p, Extra(prop))))
                    throw new ArgumentException($"0x{p:X} isn't a {Names.ObjectName(Extra(prop))}");
                *(nint*)addr = p;
                break;
            }
            case "StructProperty":
                if (value is not JsonObject fields)
                    throw new ArgumentException("a struct takes an object of fields");
                foreach (var (name, v) in fields)
                    Set(obj, path + "." + name, v);
                break;
        }
        return Single(prop, addr, 0);
    }

    static double Number(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
        _ => throw new ArgumentException($"expected a number, got {value?.ToJsonString() ?? "null"}"),
    };
}
