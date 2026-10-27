using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HomefrontSDK.Internal;

/// <summary>The SDK's entry points, called by HomefrontModLoader (msimg32.dll).</summary>
public static unsafe class Loader
{
    static bool running;

    /// <summary>Called once by the loader, on the game thread, after the first engine tick.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Initialize(NativeApi* native, ManagedApi* managed)
    {
        try
        {
            if (native->ApiVersion != NativeApi.Version || native->Size != sizeof(NativeApi))
            {
                Fail(native, $"loader API {native->ApiVersion}/{native->Size} bytes, the SDK expects {NativeApi.Version}/{sizeof(NativeApi)}");
                return 0;
            }
            Native.Api = native;
            Native.BinariesDir = new string(native->BinariesDir);
            Native.SdkDir = new string(native->SdkDir);
            string version = typeof(Loader).Assembly.GetName().Version?.ToString(3) ?? "?";
            Native.Log($"HomefrontSDK {version} on .NET {Environment.Version}; bindings from {GeneratedRegistry.Game} dumped {GeneratedRegistry.Dumped}");

            string? problem = CheckEngine();
            if (problem != null)
            {
                Native.Log($"not starting: {problem}");
                return 0;
            }

            managed->Tick = &Tick;
            managed->HookPre = &HookPre;
            managed->HookPost = &HookPost;
            Lifecycle.Start();
            Bridge.Start();
            Mods.LoadAll();
            running = true;
            return 1;
        }
        catch (Exception e)
        {
            Native.Log($"start failed: {e}");
            return 0;
        }
    }

    static void Fail(NativeApi* native, string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* b = bytes)
            native->Log(b);
    }

    // The engine is the build the bindings were generated from: names, objects, then every generated property
    // offset against the live reflection data.
    static string? CheckEngine()
    {
        if (Names.Text(0) != "None" || Names.Text(1) != "ByteProperty")
            return "the name table doesn't look like this build's";
        if (Objects.Count < 1000 || Names.ObjectFullName(Objects.At(1)) != "Core.Object")
            return "the object table doesn't look like this build's";

        var classCache = new Dictionary<string, nint>(StringComparer.OrdinalIgnoreCase);
        nint classClass = Objects.ClassClass;
        for (int i = 0; i < Objects.Count; ++i)
        {
            nint o = Objects.At(i);
            if (o != 0 && *(nint*)(o + Layout.Class) == classClass)
            {
                classCache[Names.ObjectFullName(o)] = o;
            }
        }
        int checkedCount = 0, missing = 0;
        var wrong = new List<string>();
        foreach (string line in GeneratedRegistry.Offsets.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = line.Split('\t');
            if (!classCache.TryGetValue(f[0], out nint cls))
            {
                ++missing;  // a class this server hasn't loaded
                continue;
            }
            ++checkedCount;
            int offset = DeclaredOffset(cls, f[1]);
            if (offset != int.Parse(f[2]))
                wrong.Add($"{f[0]}.{f[1]} at {offset}, bindings say {f[2]}");
        }
        Native.Log($"layout check: {checkedCount} properties match{(missing > 0 ? $", {missing} in classes not loaded" : "")}, {wrong.Count} differ");
        if (wrong.Count > 0)
        {
            foreach (string w in wrong.GetRange(0, Math.Min(20, wrong.Count)))
                Native.Log("  " + w);
            return "the generated bindings don't match this game (regenerate them: HomefrontSDK/Tools/dump_sdk.py --api, then Build.ps1)";
        }
        return null;
    }

    // The property's offset among the class's own children (not inherited ones), or -1.
    static int DeclaredOffset(nint cls, string name)
    {
        for (nint c = *(nint*)(cls + Layout.StructChildren); c != 0; c = *(nint*)(c + Layout.FieldNext))
            if (Names.ObjectName(c) == name && Objects.ClassName(c).EndsWith("Property", StringComparison.Ordinal))
                return *(int*)(c + Layout.PropertyOffset);
        return -1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void Tick(float delta)
    {
        if (!running)
            return;
        try
        {
            Lifecycle.Tick(delta);
        }
        catch (Exception e)
        {
            Native.Log($"SDK tick threw {e}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int HookPre(int slot, nint self, nint frame)
    {
        try
        {
            return Hooks.Pre(slot, self, frame);
        }
        catch (Exception e)
        {
            Native.Log($"hook dispatch threw {e}");
            return 1 | 2;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void HookPost(int slot, nint self, nint frame, nint result)
    {
        try
        {
            Hooks.Post(slot, self, frame);
        }
        catch (Exception e)
        {
            Native.Log($"hook dispatch threw {e}");
        }
    }
}
