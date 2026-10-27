using System.Collections.Generic;

namespace HomefrontSDK.Generator;

// api-reference.json, as Tools/dump_sdk.py writes it (snake_case keys).

sealed class Api
{
    public string Game { get; set; } = "";
    public string Dumped { get; set; } = "";
    public List<TypeInfo> Classes { get; set; } = new();
    public List<TypeInfo> Structs { get; set; } = new();
    public List<EnumInfo> Enums { get; set; } = new();
}

sealed class TypeInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Package { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Super { get; set; }
    public int Size { get; set; }
    public int Align { get; set; }
    public List<PropInfo> Properties { get; set; } = new();
    public List<FuncInfo> Functions { get; set; } = new();
}

sealed class PropInfo
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Offset { get; set; }
    public int Size { get; set; }
    public int ElementSize { get; set; }
    public int ArrayDim { get; set; }
    public ulong Flags { get; set; }
    public uint? Bitmask { get; set; }
    public string? Class { get; set; }
    public string? MetaClass { get; set; }
    public string? Struct { get; set; }
    public string? Enum { get; set; }
    public string? Function { get; set; }
    public PropInfo? Inner { get; set; }
    public PropInfo? Key { get; set; }
    public PropInfo? Value { get; set; }
}

sealed class FuncInfo
{
    public string Name { get; set; } = "";
    public uint Flags { get; set; }
    public int ParmsSize { get; set; }
    public int ReturnOffset { get; set; }
    public List<PropInfo> Parms { get; set; } = new();
}

sealed class EnumInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> Values { get; set; } = new();
}

static class Flags
{
    public const uint FuncFinal = 0x1, FuncIterator = 0x4, FuncLatent = 0x8, FuncPreOperator = 0x10, FuncNative = 0x400,
        FuncEvent = 0x800, FuncOperator = 0x1000, FuncStatic = 0x2000, FuncDelegate = 0x100000;
    public const ulong PropOptional = 0x10, PropParm = 0x80, PropOut = 0x100, PropReturn = 0x400, PropConst = 0x2;
}
