using System;
using System.Text;
using HomefrontSDK.Internal;

namespace HomefrontSDK;

/// <summary>Thrown when game code crashed inside a call the loader guarded. The engine may be left inconsistent.</summary>
public sealed class GameCrashException : Exception
{
    public uint Code { get; }

    public GameCrashException(uint code, string what) : base($"{what} crashed in game code (exception 0x{code:X8})")
    {
        Code = code;
    }
}

/// <summary>Calls into HomefrontModLoader's native API. Everything here runs on the game thread.</summary>
public static unsafe class Native
{
    internal static NativeApi* Api;

    internal static string BinariesDir = "";
    internal static string SdkDir = "";

    /// <summary>A line in HomefrontSDK.log (shared with the loader).</summary>
    public static void Log(string line)
    {
        if (Api == null)
            return;
        int n = Encoding.UTF8.GetByteCount(line);
        byte* buf = stackalloc byte[n + 1];
        Encoding.UTF8.GetBytes(line, new Span<byte>(buf, n));
        buf[n] = 0;
        Api->Log(buf);
        Bridge.OnLog("sdk", line);
    }

    /// <summary>UObject::ProcessEvent: runs <paramref name="function"/> on <paramref name="obj"/> with the
    /// parameter block <paramref name="parms"/> (the function's ParmsSize bytes; out values are copied back).</summary>
    public static void ProcessEvent(nint obj, nint function, void* parms)
    {
        uint code;
        if (Api->ProcessEvent(obj, function, parms, &code) == 0)
            throw new GameCrashException(code, $"ProcessEvent({Names.ObjectFullName(function)})");
    }

    /// <summary>Calls native code at <paramref name="fn"/>: <paramref name="self"/> goes in ecx (thiscall; ignored
    /// by stdcall/cdecl functions) and each argument is pushed as one 32-bit word. Returns eax.</summary>
    public static uint Call(nint fn, nint self, ReadOnlySpan<uint> args)
    {
        uint ret, code;
        int ok;
        fixed (uint* a = args)
            ok = Api->CallNative(fn, self, a, args.Length, &ret, &code);
        if (ok == 0)
            throw new GameCrashException(code, $"native call 0x{fn:X8}");
        return ret;
    }

    /// <summary>Copies <paramref name="size"/> bytes from memory that may be unreadable; false if it was.</summary>
    public static bool TryRead(nint address, void* destination, int size) =>
        address != 0 && Api->ReadMemory(address, destination, size) != 0;

    internal static int HookFunction(nint function) => Api->HookFunction(function);
}
