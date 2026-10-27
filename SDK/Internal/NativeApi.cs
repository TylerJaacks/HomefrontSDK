using System.Runtime.InteropServices;

namespace HomefrontSDK.Internal;

/// <summary>The loader's API table (HomefrontModLoader/hfsdk_native.h, HFNativeApi). Change both together.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeApi
{
    public const int Version = 1;

    public int ApiVersion;
    public int Size;
    public nint GObjects;  // TArray<UObject*>
    public nint GNames;    // TArray<FNameEntry*>
    public nint GWorld;    // UWorld**
    public nint GEngine;   // UEngine**
    public char* BinariesDir;
    public char* SdkDir;
    public delegate* unmanaged[Cdecl]<byte*, void> Log;
    public delegate* unmanaged[Cdecl]<nint, nint, void*, uint*, int> ProcessEvent;
    public delegate* unmanaged[Cdecl]<nint, nint, uint*, int, uint*, uint*, int> CallNative;
    public delegate* unmanaged[Cdecl]<nint, void*, int, int> ReadMemory;
    public delegate* unmanaged[Cdecl]<nint, int> HookFunction;
    public int HookSlots;
}

/// <summary>What the SDK hands back to the loader (HFManagedApi).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ManagedApi
{
    public int Size;
    public delegate* unmanaged[Cdecl]<float, void> Tick;
    public delegate* unmanaged[Cdecl]<int, nint, nint, int> HookPre;
    public delegate* unmanaged[Cdecl]<int, nint, nint, nint, void> HookPost;
}
