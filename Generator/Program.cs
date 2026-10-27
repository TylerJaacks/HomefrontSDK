// HomefrontSDK.Generator: writes the C# wrappers of every game class, struct and enum.
//
// usage: HomefrontSDK.Generator <api-reference.json> <output folder>
//
// Input: the reflection dump of the running server (re/dump_sdk.py --api). Output, one file per package plus
// Registry.cs:
//   - classes: typed properties at their offsets, methods that call the script function through ProcessEvent;
//   - structs: explicit-layout C# structs (flattened over their super struct);
//   - enums: byte enums;
//   - the class path -> wrapper constructor table, and every property's offset for the runtime's startup check.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace HomefrontSDK.Generator;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: HomefrontSDK.Generator <api-reference.json> <output folder>");
            return 2;
        }
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };
        Api api;
        using (var stream = File.OpenRead(args[0]))
            api = JsonSerializer.Deserialize<Api>(stream, options) ?? throw new InvalidDataException("empty dump");
        var gen = new Gen(api);
        gen.Write(args[1]);
        Console.WriteLine($"generated {gen.ClassCount} classes ({gen.FunctionCount} functions, {gen.SkippedFunctions} skipped), " +
                          $"{gen.StructCount} structs, {gen.EnumCount} enums into {args[1]}");
        return 0;
    }
}

sealed class Gen
{
    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    // Members of the hand-written UObject (Framework/UObject.cs) and System.Object that generated members must not
    // take.
    static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "Ptr", "ObjectIndex", "IsValid", "ObjectName", "FullName", "ClassName", "StateName", "Cast", "Get",
        "ClassPtr", "ScriptPath", "Equals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "Finalize",
        "ReferenceEquals",
    };

    readonly Api api;
    readonly Dictionary<string, TypeInfo> classes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, TypeInfo> structs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, EnumInfo> enums = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);  // script path -> C# name
    readonly Dictionary<string, HashSet<string>> inheritedFunctions = new(StringComparer.OrdinalIgnoreCase);

    public int ClassCount, StructCount, EnumCount, FunctionCount, SkippedFunctions;

    public Gen(Api api)
    {
        this.api = api;
        foreach (var c in api.Classes.Where(c => !c.Name.StartsWith("Default__", StringComparison.Ordinal)))
        {
            classes[c.Path] = c;
            names[c.Path] = c.Package == "Core" ? "U" + c.Name : c.Name;
        }
        var dupStructs = api.Structs.GroupBy(s => s.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var s in api.Structs)
        {
            structs[s.Path] = s;
            string[] parts = s.Path.Split('.');
            names[s.Path] = dupStructs.Contains(s.Name) ? $"F{parts[^2]}_{s.Name}" : "F" + s.Name;
        }
        foreach (var e in api.Enums)
        {
            enums[e.Path] = e;
            names[e.Path] = e.Name;
        }
    }

    static string Id(string name) => Keywords.Contains(name) ? "@" + name : name;
    static string Hex(int v) => "0x" + v.ToString("X");

    string ClassRef(string? path) => path != null && classes.ContainsKey(path) ? names[path] : "UObject";

    // ---- types
    enum Access { Plain, Bool, String, Object, Ref, Unsupported }

    /// <summary>The C# type a property is used as, and how it's accessed.</summary>
    (string type, Access access) Map(PropInfo p)
    {
        switch (p.Kind)
        {
            case "IntProperty": return ("int", Access.Plain);
            case "FloatProperty": return ("float", Access.Plain);
            case "BoolProperty": return ("bool", Access.Bool);
            case "NameProperty": return ("FName", Access.Plain);
            case "StrProperty": return ("string", Access.String);
            case "ByteProperty":
                return (p.Enum != null && enums.ContainsKey(p.Enum) ? names[p.Enum] : "byte", Access.Plain);
            case "ObjectProperty":
            case "ComponentProperty":
                return (ClassRef(p.Class), Access.Object);
            case "ClassProperty": return ("UClass", Access.Object);
            case "InterfaceProperty": return ("FScriptInterface", Access.Ref);
            case "DelegateProperty": return ("FScriptDelegate", Access.Ref);
            case "MapProperty": return ("FScriptMap", Access.Ref);
            case "StructProperty":
                return p.Struct != null && structs.ContainsKey(p.Struct) ? (names[p.Struct], Access.Ref) : ("", Access.Unsupported);
            case "ArrayProperty":
                if (p.Inner == null)
                    return ("", Access.Unsupported);
                var (inner, innerAccess) = Map(p.Inner);
                if (innerAccess == Access.Object)
                    return ($"TObjectArray<{inner}>", Access.Ref);
                string raw = Raw(p.Inner);
                return raw.Length == 0 || p.Inner.Kind == "ArrayProperty" ? ("", Access.Unsupported) : ($"TArray<{raw}>", Access.Ref);
            default:
                return ("", Access.Unsupported);
        }
    }

    /// <summary>The unmanaged in-memory type of a value (for arrays, struct fields and fixed arrays).</summary>
    string Raw(PropInfo p)
    {
        var (type, access) = Map(p);
        return access switch
        {
            Access.Bool => "uint",
            Access.String => "FString",
            Access.Object => "nint",
            Access.Unsupported => "",
            _ => type,
        };
    }

    // ---- output
    public void Write(string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (string old in Directory.GetFiles(outDir, "*.cs"))
            File.Delete(old);

        var byPackage = new SortedDictionary<string, StringBuilder>(StringComparer.Ordinal);
        StringBuilder For(string package)
        {
            if (!byPackage.TryGetValue(package, out var sb))
                byPackage[package] = sb = Header($"Classes, structs and enums of the {package} package.");
            return sb;
        }

        foreach (var e in api.Enums.OrderBy(e => e.Path, StringComparer.Ordinal))
            WriteEnum(For(e.Path.Split('.')[0]), e);
        foreach (var s in api.Structs.OrderBy(s => s.Path, StringComparer.Ordinal))
            WriteStruct(For(s.Package), s);
        foreach (var c in Ordered())
            WriteClass(For(c.Package), c);

        foreach (var (package, sb) in byPackage)
            File.WriteAllText(Path.Combine(outDir, package + ".cs"), sb.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "Registry.cs"), Registry(), new UTF8Encoding(false));
    }

    // Classes, parents first (so inherited function names are known before their subclasses).
    IEnumerable<TypeInfo> Ordered()
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TypeInfo>();
        void Visit(TypeInfo c)
        {
            if (!done.Add(c.Path))
                return;
            if (c.Super != null && classes.TryGetValue(c.Super, out var parent))
                Visit(parent);
            result.Add(c);
        }
        foreach (var c in classes.Values.OrderBy(c => c.Path, StringComparer.Ordinal))
            Visit(c);
        return result;
    }

    static StringBuilder Header(string what)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"// {what} Written by HomefrontSDK.Generator from api-reference.json; don't edit.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS0108, CS0109, CS0114, CS0169, CS0649, CS1591");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Diagnostics.CodeAnalysis;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine();
        sb.AppendLine("namespace HomefrontSDK;");
        sb.AppendLine();
        return sb;
    }

    void WriteEnum(StringBuilder sb, EnumInfo e)
    {
        EnumCount++;
        sb.AppendLine($"/// <summary>{e.Path}</summary>");
        sb.AppendLine($"[ScriptPath(\"{e.Path}\")]");
        sb.AppendLine($"public enum {names[e.Path]} : byte");
        sb.AppendLine("{");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < e.Values.Count && i < 256; ++i)
        {
            string v = e.Values[i];
            if (string.IsNullOrEmpty(v) || !seen.Add(v))
                v = $"{v}_{i}";
            sb.AppendLine($"    {Id(v)} = {i},");
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    void WriteStruct(StringBuilder sb, TypeInfo s)
    {
        StructCount++;
        string name = names[s.Path];
        // Flatten the super chain: C# structs don't inherit, and offsets are from the start of the whole struct.
        var props = new List<PropInfo>();
        for (TypeInfo? t = s; t != null; t = t.Super != null && structs.TryGetValue(t.Super, out var sup) ? sup : null)
            props.InsertRange(0, t.Properties);
        int align = Math.Max(1, s.Align);
        int size = Math.Max(1, (s.Size + align - 1) / align * align);
        foreach (var p in props)
            size = Math.Max(size, p.Offset + p.Size);

        sb.AppendLine($"/// <summary>{s.Path} ({size} bytes)</summary>");
        sb.AppendLine($"[ScriptPath(\"{s.Path}\")]");
        sb.AppendLine($"[StructLayout(LayoutKind.Explicit, Size = {size})]");
        sb.AppendLine($"public unsafe partial struct {name}");
        sb.AppendLine("{");
        var used = new HashSet<string>(StringComparer.Ordinal) { name };
        var bitFields = new HashSet<int>();
        foreach (var p in props)
        {
            string member = p.Name == name || !used.Add(p.Name) ? p.Name + "_" : p.Name;
            used.Add(member);
            string id = Id(member);
            var (type, access) = Map(p);
            string at = $"[FieldOffset({Hex(p.Offset)})]";
            if (access == Access.Unsupported)
            {
                sb.AppendLine($"    // {p.Name}: {p.Kind} not supported");
                continue;
            }
            if (p.ArrayDim > 1)
            {
                string raw = Raw(p);
                sb.AppendLine($"    {at} fixed byte __{member}[{p.Size}];");
                sb.AppendLine($"    [UnscopedRef] public Span<{raw}> {id} => MemoryMarshal.CreateSpan(ref Unsafe.As<byte, {raw}>(ref __{member}[0]), {p.ArrayDim});");
                continue;
            }
            switch (access)
            {
                case Access.Bool:
                    if (bitFields.Add(p.Offset))
                        sb.AppendLine($"    {at} public uint __bits_{p.Offset:X};");
                    uint mask = p.Bitmask ?? 1;
                    sb.AppendLine($"    public bool {id} {{ readonly get => (__bits_{p.Offset:X} & {Hex((int)mask)}u) != 0; " +
                                  $"set => __bits_{p.Offset:X} = value ? __bits_{p.Offset:X} | {Hex((int)mask)}u : __bits_{p.Offset:X} & ~{Hex((int)mask)}u; }}");
                    break;
                case Access.Object:
                    sb.AppendLine($"    {at} public nint __{member};");
                    sb.AppendLine($"    public {type}? {id} {{ readonly get => UObject.Get<{type}>(__{member}); set => __{member} = value?.Ptr ?? 0; }}");
                    break;
                case Access.String:
                    sb.AppendLine($"    {at} public FString {id};");
                    break;
                default:
                    sb.AppendLine($"    {at} public {type} {id};");
                    break;
            }
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    void WriteClass(StringBuilder sb, TypeInfo c)
    {
        ClassCount++;
        string name = names[c.Path];
        bool root = c.Super == null;
        string super = root ? "" : $" : {ClassRef(c.Super)}";
        var functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (c.Super != null && inheritedFunctions.TryGetValue(c.Super, out var parentFunctions))
            functions.UnionWith(parentFunctions);

        sb.AppendLine($"/// <summary>{c.Path}</summary>");
        sb.AppendLine($"[ScriptPath(\"{c.Path}\")]");
        sb.AppendLine($"public unsafe partial class {name}{super}");
        sb.AppendLine("{");
        if (!root)
            sb.AppendLine($"    protected internal {name}(nint ptr) : base(ptr) {{ }}");
        sb.AppendLine();

        var used = new HashSet<string>(Reserved, StringComparer.Ordinal) { name };
        foreach (var p in c.Properties)
        {
            string member = used.Contains(p.Name) ? p.Name + "_" : p.Name;
            used.Add(member);
            WriteClassProperty(sb, p, Id(member));
        }

        foreach (var f in c.Functions)
        {
            bool overrides = functions.Contains(f.Name);
            functions.Add(f.Name);
            if (overrides)
                continue;  // declared by a parent: callers reach this version through virtual lookup
            string member = used.Contains(f.Name) && !(f.Name == "IsA" && root) ? f.Name + "_" : f.Name;
            used.Add(member);
            if (!WriteFunction(sb, c, f, Id(member)))
                SkippedFunctions++;
            else
                FunctionCount++;
        }
        inheritedFunctions[c.Path] = functions;
        sb.AppendLine("}");
        sb.AppendLine();
    }

    void WriteClassProperty(StringBuilder sb, PropInfo p, string id)
    {
        var (type, access) = Map(p);
        string at = $"Ptr + {Hex(p.Offset)}";
        if (access == Access.Unsupported)
        {
            sb.AppendLine($"    // {p.Name}: {p.Kind} not supported");
            return;
        }
        if (p.ArrayDim > 1)
        {
            if (access == Access.Bool)
                return;
            string array = access == Access.Object ? $"FixedObjectArray<{type}>" : $"FixedArray<{Raw(p)}>";
            sb.AppendLine($"    public {array} {id} => new({at}, {p.ArrayDim});");
            return;
        }
        switch (access)
        {
            case Access.Plain:
                sb.AppendLine($"    public {type} {id} {{ get => *({type}*)({at}); set => *({type}*)({at}) = value; }}");
                break;
            case Access.Bool:
                sb.AppendLine($"    public bool {id} {{ get => Bits.Get({at}, {Hex((int)(p.Bitmask ?? 1))}u); set => Bits.Set({at}, {Hex((int)(p.Bitmask ?? 1))}u, value); }}");
                break;
            case Access.String:
                sb.AppendLine($"    public string {id} => ((FString*)({at}))->ToString();");
                break;
            case Access.Object:
                sb.AppendLine($"    public {type}? {id} {{ get => UObject.Get<{type}>(*(nint*)({at})); set => *(nint*)({at}) = value?.Ptr ?? 0; }}");
                break;
            case Access.Ref:
                sb.AppendLine($"    public ref {type} {id} => ref *({type}*)({at});");
                break;
        }
    }

    bool WriteFunction(StringBuilder sb, TypeInfo c, FuncInfo f, string id)
    {
        const uint skip = Flags.FuncIterator | Flags.FuncLatent | Flags.FuncOperator | Flags.FuncPreOperator | Flags.FuncDelegate;
        if ((f.Flags & skip) != 0)
            return false;
        var parms = f.Parms.Where(p => (p.Flags & Flags.PropParm) != 0).ToList();
        if (parms.Any(p => p.ArrayDim > 1 || Map(p).access == Access.Unsupported))
            return false;
        PropInfo? ret = parms.FirstOrDefault(p => (p.Flags & Flags.PropReturn) != 0);
        var args = parms.Where(p => p != ret).ToList();

        bool isStatic = (f.Flags & Flags.FuncStatic) != 0;
        bool isFinal = (f.Flags & Flags.FuncFinal) != 0;
        string field = $"__fn_{f.Name}";

        // Signature. Optional parameters get "= default" when nothing required follows them.
        var argNames = new HashSet<string>(StringComparer.Ordinal);
        var decl = new List<string>();
        int lastRequired = -1;
        for (int i = 0; i < args.Count; ++i)
            if ((args[i].Flags & Flags.PropOptional) == 0 || (args[i].Flags & Flags.PropOut) != 0)
                lastRequired = i;
        var argIds = new List<string>();
        for (int i = 0; i < args.Count; ++i)
        {
            var p = args[i];
            string n = p.Name;
            while (!argNames.Add(n))
                n += "_";
            string pid = Id(n);
            argIds.Add(pid);
            var (type, access) = Map(p);
            bool isOut = (p.Flags & Flags.PropOut) != 0;
            string t = access == Access.Object ? type + "?" : access == Access.String && !isOut ? "string?" : type;
            string prefix = isOut ? (access == Access.String ? "out " : "ref ") : "";
            string opt = !isOut && i > lastRequired ? " = default" : "";
            decl.Add($"{prefix}{t} {pid}{opt}");
        }
        string retType = "void";
        if (ret != null)
        {
            var (type, access) = Map(ret);
            retType = access == Access.Object ? type + "?" : type;
        }

        string flags = string.Join(", ", new[]
        {
            (f.Flags & Flags.FuncNative) != 0 ? "native" : null, isFinal ? "final" : null, isStatic ? "static" : null,
            (f.Flags & Flags.FuncEvent) != 0 ? "event" : null,
        }.Where(x => x != null));
        sb.AppendLine($"    static readonly FunctionRef {field} = new(\"{c.Path}\", \"{f.Name}\", {(isFinal || isStatic ? "true" : "false")});");
        sb.AppendLine($"    /// <summary>{c.Path}.{f.Name}{(flags.Length > 0 ? " (" + flags + ")" : "")}</summary>");
        sb.AppendLine($"    public {(isStatic ? "static " : "")}{retType} {id}({string.Join(", ", decl)})");
        sb.AppendLine("    {");
        sb.AppendLine($"        byte* __p = stackalloc byte[{Math.Max(1, f.ParmsSize)}];");

        // Inputs.
        for (int i = 0; i < args.Count; ++i)
        {
            var p = args[i];
            var (type, access) = Map(p);
            bool isOut = (p.Flags & Flags.PropOut) != 0;
            string slot = $"__p + {Hex(p.Offset)}";
            string a = argIds[i];
            switch (access)
            {
                case Access.Plain:
                case Access.Ref:
                    sb.AppendLine($"        *({type}*)({slot}) = {a};");
                    break;
                case Access.Bool:
                    sb.AppendLine($"        *(uint*)({slot}) = {a} ? {Hex((int)(p.Bitmask ?? 1))}u : 0u;");
                    break;
                case Access.Object:
                    sb.AppendLine($"        *(nint*)({slot}) = {a}?.Ptr ?? 0;");
                    break;
                case Access.String when !isOut:
                    sb.AppendLine($"        char* __s{i} = stackalloc char[({a}?.Length ?? 0) + 1];");
                    sb.AppendLine($"        Marshalling.WriteString({slot}, {a}, __s{i});");
                    break;
            }
        }
        sb.AppendLine(isStatic ? $"        {field}.InvokeStatic(__p);" : $"        {field}.Invoke(this, __p);");

        // Outputs.
        for (int i = 0; i < args.Count; ++i)
        {
            var p = args[i];
            if ((p.Flags & Flags.PropOut) == 0)
                continue;
            var (type, access) = Map(p);
            string slot = $"__p + {Hex(p.Offset)}";
            string a = argIds[i];
            sb.AppendLine(access switch
            {
                Access.Bool => $"        {a} = *(uint*)({slot}) != 0;",
                Access.String => $"        {a} = Marshalling.ReadString({slot});",
                Access.Object => $"        {a} = UObject.Get<{type}>(*(nint*)({slot}));",
                _ => $"        {a} = *({type}*)({slot});",
            });
        }
        if (ret != null)
        {
            var (type, access) = Map(ret);
            string slot = $"__p + {Hex(ret.Offset)}";
            sb.AppendLine(access switch
            {
                Access.Bool => $"        return *(uint*)({slot}) != 0;",
                Access.String => $"        return Marshalling.ReadString({slot});",
                Access.Object => $"        return UObject.Get<{type}>(*(nint*)({slot}));",
                _ => $"        return *({type}*)({slot});",
            });
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        return true;
    }

    string Registry()
    {
        var sb = Header("The class table and layout check.");
        sb.AppendLine("internal static class GeneratedRegistry");
        sb.AppendLine("{");
        sb.AppendLine($"    public const string Game = \"{api.Game}\";");
        sb.AppendLine($"    public const string Dumped = \"{api.Dumped}\";");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Script class path -> wrapper constructor.</summary>");
        sb.AppendLine("    public static System.Collections.Generic.Dictionary<string, Func<nint, UObject>> Factories() =>");
        sb.AppendLine("        new(StringComparer.OrdinalIgnoreCase)");
        sb.AppendLine("        {");
        foreach (var c in classes.Values.OrderBy(c => c.Path, StringComparer.Ordinal))
            sb.AppendLine($"            [\"{c.Path}\"] = p => new {names[c.Path]}(p),");
        sb.AppendLine("        };");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>\"class path\\tproperty\\toffset\" lines: what the generated code assumes, checked at startup.</summary>");
        sb.Append("    public const string Offsets = \"");
        foreach (var c in classes.Values.OrderBy(c => c.Path, StringComparer.Ordinal))
            foreach (var p in c.Properties)
                sb.Append($"{c.Path}\\t{p.Name}\\t{p.Offset}\\n");
        sb.AppendLine("\";");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
