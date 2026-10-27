using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HomefrontSDK;

/// <summary>Engine object layouts of this build (UE3 v576, HomefrontSDK/Tools/README.md). Offsets past the object header come from
/// the engine's own reflection data.</summary>
internal static class Layout
{
    // UObject (0x3C, 4-byte packing)
    public const int ObjectIndex = 0x04, ObjectFlags = 0x08, StateFrame = 0x18, Outer = 0x28, Name = 0x2C,
        Class = 0x34, ObjectSize = 0x3C;
    // UField / UStruct
    public const int FieldNext = 0x3C, StructSuper = 0x48, StructChildren = 0x4C, StructPropertiesSize = 0x50;
    // UProperty
    public const int PropertyArrayDim = 0x40, PropertyElementSize = 0x44, PropertyFlags = 0x48, PropertyOffset = 0x60,
        PropertyExtra0 = 0x80;
    // UFunction
    public const int FunctionFlags = 0x8C, FunctionParmsSize = 0x9E, FunctionReturnOffset = 0xA0;
    // FStateFrame: FFrame, then StateNode
    public const int StateFrameNode = 0x28;
    // FFrame
    public const int FrameNode = 0x10, FrameObject = 0x14, FrameCode = 0x18, FrameLocals = 0x1C;
    // FNameEntry
    public const int NameEntryIndex = 0x08, NameEntryText = 0x10;

    public const uint FuncFinal = 0x1, FuncNative = 0x400, FuncStatic = 0x2000;
    public const ulong PropParm = 0x80, PropOutParm = 0x100, PropReturnParm = 0x400;
}

/// <summary>An engine name: an index into the name table plus a number ("Foo_3" is Foo with Number 4).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct FName : IEquatable<FName>
{
    public readonly int Index;
    public readonly int Number;

    public FName(int index, int number = 0)
    {
        Index = index;
        Number = number;
    }

    public static FName None => default;

    /// <summary>An existing name (the table isn't added to); None if the text was never used.</summary>
    public static FName Find(string text) => new(Names.Find(text));

    public bool IsNone => Index == 0 && Number == 0;

    public override string ToString()
    {
        string text = Names.Text(Index) ?? "?";
        return Number > 0 ? $"{text}_{Number - 1}" : text;
    }

    public bool Equals(FName other) => Index == other.Index && Number == other.Number;
    public override bool Equals(object? obj) => obj is FName n && Equals(n);
    public override int GetHashCode() => HashCode.Combine(Index, Number);
    public static bool operator ==(FName a, FName b) => a.Equals(b);
    public static bool operator !=(FName a, FName b) => !a.Equals(b);
}

/// <summary>An engine string (TArray of UTF-16 characters, null-terminated; Num counts the terminator).
/// Reading is safe; strings the game keeps must be allocated by the engine, so there is no setter.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FString
{
    public char* Data;
    public int Num, Max;

    public int Length
    {
        get
        {
            if (Data == null || Num <= 0)
                return 0;
            int end = new ReadOnlySpan<char>(Data, Num).IndexOf('\0');
            return end >= 0 ? end : Num;
        }
    }

    public override string ToString() => Length == 0 ? "" : new string(Data, 0, Length);
}

/// <summary>An engine dynamic array of plain values, as laid out in memory. Elements can be read and written in
/// place; the array can't grow (that needs the engine's allocator).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TArray<T> where T : unmanaged
{
    public T* Data;
    public int Num, Max;

    public int Count => Num;

    public ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Num)
                throw new IndexOutOfRangeException($"index {index}, count {Num}");
            return ref Data[index];
        }
    }

    public Span<T> AsSpan() => Data == null || Num <= 0 ? Span<T>.Empty : new Span<T>(Data, Num);
    public Span<T>.Enumerator GetEnumerator() => AsSpan().GetEnumerator();
}

/// <summary>An engine dynamic array of object references (TArray&lt;UObject*&gt;), typed.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TObjectArray<T> where T : UObject
{
    public nint* Data;
    public int Num, Max;

    public int Count => Num;

    public T? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Num)
                throw new IndexOutOfRangeException($"index {index}, count {Num}");
            return UObject.Get<T>(Data[index]);
        }
        set
        {
            if ((uint)index >= (uint)Num)
                throw new IndexOutOfRangeException($"index {index}, count {Num}");
            Data[index] = value?.Ptr ?? 0;
        }
    }

    /// <summary>The non-null elements, copied into a list.</summary>
    public List<T> Items()
    {
        var items = new List<T>(Num);
        for (int i = 0; i < Num; ++i)
            if (UObject.Get<T>(Data[i]) is T item)
                items.Add(item);
        return items;
    }
}

/// <summary>A fixed-size array inside an object (a script property declared with [N]).</summary>
public readonly unsafe struct FixedArray<T> where T : unmanaged
{
    readonly T* data;
    public int Length { get; }

    public FixedArray(nint address, int length)
    {
        data = (T*)address;
        Length = length;
    }

    public ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length)
                throw new IndexOutOfRangeException($"index {index}, length {Length}");
            return ref data[index];
        }
    }

    public Span<T> AsSpan() => new(data, Length);
}

/// <summary>A fixed-size array of object references inside an object.</summary>
public readonly unsafe struct FixedObjectArray<T> where T : UObject
{
    readonly nint* data;
    public int Length { get; }

    public FixedObjectArray(nint address, int length)
    {
        data = (nint*)address;
        Length = length;
    }

    public T? this[int index]
    {
        get => (uint)index < (uint)Length ? UObject.Get<T>(data[index]) : throw new IndexOutOfRangeException();
        set
        {
            if ((uint)index >= (uint)Length)
                throw new IndexOutOfRangeException();
            data[index] = value?.Ptr ?? 0;
        }
    }
}

/// <summary>A script delegate value (object and function name).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FScriptDelegate
{
    public nint Object;
    public FName FunctionName;
}

/// <summary>A script interface value (object and its interface vtable).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FScriptInterface
{
    public nint Object;
    public nint Interface;
}

/// <summary>A script map value; maps aren't readable from here yet.</summary>
[StructLayout(LayoutKind.Sequential, Size = 60)]
public struct FScriptMap
{
}

/// <summary>Bit helpers for bool properties (bitfields in a 32-bit word).</summary>
public static unsafe class Bits
{
    public static bool Get(nint address, uint mask) => (*(uint*)address & mask) != 0;

    public static void Set(nint address, uint mask, bool value)
    {
        if (value)
            *(uint*)address |= mask;
        else
            *(uint*)address &= ~mask;
    }
}
