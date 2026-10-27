"""Dump every class, struct, enum, constant, function and property from a running Homefront process.

Read-only. Writes into Tools/out/<exe>/:
  sdk/<Package>.h   readable C++-style layouts: offsets, sizes, flags, bool masks, function signatures
  sdk.json          the same data for code generation
  natives.csv       every native function: path, iNative, flags, C++ thunk address
  classes.txt       class hierarchy with instance sizes
With --api [PATH] it also writes the compact copy HomefrontSDK's Generator reads (default
HomefrontSDK/api-reference.json).

usage: python dump_sdk.py [HFDedicated.exe] [--api [PATH]]
"""
import collections
import csv
import json
import os
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # run with python -I, which drops the script dir
from proc import Process
from ue3 import *

BLOCK = 0xB0  # bytes read per type object; covers UProperty extras and UFunction fields


class Dumper:
    def __init__(self, exe, api_path=None):
        self.exe = exe
        self.api_path = api_path
        self.ue = UE3(Process.by_name(exe))
        self.p = self.ue.p
        self.blocks = {}
        self.hdr = {}  # obj -> (outer, name, cls)

    def block(self, o):
        b = self.blocks.get(o)
        if b is None:
            b = self.p.try_read(o, BLOCK) or self.p.try_read(o, 0x88) or self.p.read(o, UOBJECT_SIZE)
            b = b.ljust(BLOCK, b"\0")
            self.blocks[o] = b
        return b

    def u32(self, o, off):
        return struct.unpack_from("<I", self.block(o), off)[0]

    def name(self, o):
        b = self.block(o)
        idx, num = struct.unpack_from("<ii", b, O_NAME)
        base = self.ue.name_by_index(idx) or "?%d" % idx
        return base if num == 0 else "%s_%d" % (base, num - 1)

    def cls_name(self, o):
        return self.name(self.u32(o, O_CLASS))

    def outer(self, o):
        return self.u32(o, O_OUTER)

    def path(self, o):
        parts = []
        while o:
            parts.append(self.name(o))
            o = self.outer(o)
        return ".".join(reversed(parts))

    def package(self, o):
        while self.outer(o):
            o = self.outer(o)
        return self.name(o)

    def children(self, st):
        c = self.u32(st, S_CHILDREN)
        while c:
            yield c
            c = self.u32(c, F_NEXT)

    def supers(self, st):
        s = self.u32(st, S_SUPER)
        while s:
            yield s
            s = self.u32(s, S_SUPER)

    def cpp_class(self, c):
        if not c:
            return "UObject"
        chain = [self.name(c)] + [self.name(s) for s in self.supers(c)]
        prefix = "A" if "Actor" in chain else "U"
        return prefix + self.name(c)

    def ptype(self, prop):
        cn = self.cls_name(prop)
        x0, x1 = self.u32(prop, P_EXTRA0), self.u32(prop, P_EXTRA1)
        if cn == "ByteProperty":
            return self.name(x0) if x0 else "BYTE"
        if cn == "BoolProperty":
            return "BITFIELD"
        if cn in ("ObjectProperty", "ComponentProperty"):
            return self.cpp_class(x0) + "*"
        if cn == "ClassProperty":
            return "UClass* /*%s*/" % self.name(x1) if x1 else "UClass*"
        if cn == "StructProperty":
            return "F" + self.name(x0)
        if cn == "ArrayProperty":
            return "TArray<%s>" % self.ptype(x0) if x0 else "TArray<?>"
        if cn == "DelegateProperty":
            return "FScriptDelegate /*%s*/" % self.name(x0) if x0 else "FScriptDelegate"
        if cn == "InterfaceProperty":
            return "TScriptInterface<I%s>" % self.name(x0) if x0 else "FScriptInterface"
        if cn == "MapProperty":
            return "TMap<%s, %s>" % (self.ptype(x0), self.ptype(x1)) if x0 and x1 else "TMap<?>"
        return {"IntProperty": "INT", "FloatProperty": "FLOAT", "NameProperty": "FName", "StrProperty": "FString"}.get(cn, cn)

    def type_ref(self, prop):
        """What a property refers to, by path, for code generation (HomefrontSDK's Generator)."""
        cn = self.cls_name(prop)
        x0, x1 = self.u32(prop, P_EXTRA0), self.u32(prop, P_EXTRA1)
        if cn == "ByteProperty":
            return {"enum": self.path(x0)} if x0 else {}
        if cn in ("ObjectProperty", "ComponentProperty", "InterfaceProperty"):
            return {"class": self.path(x0)} if x0 else {}
        if cn == "ClassProperty":
            return {"meta_class": self.path(x1)} if x1 else {}
        if cn == "StructProperty":
            return {"struct": self.path(x0)} if x0 else {}
        if cn == "ArrayProperty":
            return {"inner": self.prop_info(x0)} if x0 else {}
        if cn == "DelegateProperty":
            return {"function": self.path(x0)} if x0 else {}
        if cn == "MapProperty":
            return {"key": self.prop_info(x0), "value": self.prop_info(x1)} if x0 and x1 else {}
        return {}

    def prop_info(self, prop):
        b = self.block(prop)
        arraydim, elem = struct.unpack_from("<ii", b, P_ARRAYDIM)
        flags = struct.unpack_from("<Q", b, P_FLAGS)[0]
        offset = struct.unpack_from("<i", b, P_OFFSET)[0]
        info = {"name": self.name(prop), "kind": self.cls_name(prop), "type": self.ptype(prop), "offset": offset,
                "size": elem * max(arraydim, 1), "element_size": elem, "array_dim": arraydim, "flags": flags,
                "flag_names": flag_names(flags & 0xFFFFFFFF, PROP_FLAGS)}
        info.update(self.type_ref(prop))
        if info["kind"] == "BoolProperty":
            info["bitmask"] = self.u32(prop, P_EXTRA0)
        return info

    def func_info(self, fn):
        b = self.block(fn)
        flags = struct.unpack_from("<I", b, FN_FLAGS)[0]
        parms = [self.prop_info(c) for c in self.children(fn) if self.cls_name(c).endswith("Property")]
        script_num = struct.unpack_from("<i", b, S_SCRIPT + 4)[0]
        return {"name": self.name(fn), "flags": flags, "flag_names": flag_names(flags, FUNC_FLAGS),
                "inative": struct.unpack_from("<H", b, FN_INATIVE)[0], "func": self.u32(fn, FN_FUNC),
                "num_parms": b[FN_NUMPARMS], "parms_size": struct.unpack_from("<H", b, FN_PARMSSIZE)[0],
                "return_offset": struct.unpack_from("<H", b, FN_RETOFFSET)[0], "script_bytes": script_num,
                "parms": [q for q in parms if q["flags"] & 0x80]}

    def struct_info(self, st):
        size_align = self.u32(st, S_SIZE_ALIGN)
        sup = self.u32(st, S_SUPER)
        props, funcs, consts, enums = [], [], [], []
        for c in self.children(st):
            k = self.cls_name(c)
            if k.endswith("Property"):
                props.append(self.prop_info(c))
            elif k == "Function":
                funcs.append(self.func_info(c))
            elif k == "Const":
                d = self.u32(c, 0x40)
                consts.append({"name": self.name(c), "value": self.p.cstr(d, wide=True) if d else ""})
            elif k == "Enum":
                enums.append(self.enum_info(c))
        props.sort(key=lambda q: (q["offset"], q.get("bitmask", 0)))
        return {"name": self.name(st), "path": self.path(st), "package": self.package(st), "kind": self.cls_name(st),
                "super": self.path(sup) if sup else None, "size": size_align & 0xFFFF, "align": size_align >> 16,
                "properties": props, "functions": funcs, "consts": consts, "enums": enums}

    def enum_info(self, e):
        data, num = self.u32(e, 0x40), self.u32(e, 0x44)
        raw = self.p.try_read(data, 8 * num) if num else b""
        names = [self.ue.name_by_index(struct.unpack_from("<i", raw, 8 * i)[0]) for i in range(num)] if raw else []
        return {"name": self.name(e), "path": self.path(e), "values": names}

    def run(self, outdir):
        t0 = time.time()
        objs = list(self.ue.objects())
        kinds = collections.Counter()
        types = {"Class": [], "ScriptStruct": [], "Enum": []}
        for o in objs:
            k = self.cls_name(o)
            kinds[k] += 1
            if k in types:
                types[k].append(o)
        print("%d objects scanned in %.1fs; classes %d, structs %d, enums %d" % (
            len(objs), time.time() - t0, len(types["Class"]), len(types["ScriptStruct"]), len(types["Enum"])))

        classes = [self.struct_info(c) for c in types["Class"]]
        structs = [self.struct_info(s) for s in types["ScriptStruct"]]
        enums = [self.enum_info(e) for e in types["Enum"]]
        print("decoded in %.1fs" % (time.time() - t0))

        os.makedirs(os.path.join(outdir, "sdk"), exist_ok=True)
        dump = {"game": self.exe, "dumped": time.strftime("%Y-%m-%d %H:%M:%S"), "classes": classes,
                "structs": structs, "enums": enums}
        with open(os.path.join(outdir, "sdk.json"), "w") as f:
            json.dump(dump, f, indent=1)
        if self.api_path:  # HomefrontSDK/api-reference.json: the Generator's input
            with open(self.api_path, "w", newline="\n") as f:
                json.dump(dump, f, indent=None, separators=(",", ":"))
                f.write("\n")
            print("wrote", self.api_path)

        with open(os.path.join(outdir, "natives.csv"), "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["function", "inative", "func_address", "flags"])
            for c in classes:
                for fn in c["functions"]:
                    if fn["flags"] & 0x400:
                        w.writerow(["%s.%s" % (c["path"], fn["name"]), fn["inative"], "0x%08X" % fn["func"],
                                    "|".join(fn["flag_names"])])

        by_super = collections.defaultdict(list)
        for c in classes:
            by_super[c["super"]].append(c)
        with open(os.path.join(outdir, "classes.txt"), "w") as f:
            def walk(parent, depth):
                for c in sorted(by_super.get(parent, []), key=lambda c: c["path"]):
                    f.write("%s%s  (0x%X)\n" % ("  " * depth, c["path"], c["size"]))
                    walk(c["path"], depth + 1)
            walk(None, 0)

        by_pkg = collections.defaultdict(list)
        for s in structs + classes:
            by_pkg[s["package"]].append(s)
        for e in enums:
            by_pkg[e["path"].split(".")[0]].append(dict(e, kind="Enum"))
        for pkg, items in by_pkg.items():
            with open(os.path.join(outdir, "sdk", pkg + ".h"), "w", encoding="utf-8") as f:
                f.write("// %s - dumped from the live process by HomefrontSDK/Tools/dump_sdk.py\n\n" % pkg)
                for it in sorted(items, key=lambda i: ({"Enum": 0, "ScriptStruct": 1}.get(i["kind"], 2), i["path"])):
                    write_item(f, it)
        print("wrote %s in %.1fs" % (outdir, time.time() - t0))


def write_item(f, it):
    if it["kind"] == "Enum":
        f.write("// Enum %s\nenum %s {\n%s};\n\n" % (it["path"], it["name"], "".join("    %s,\n" % v for v in it["values"])))
        return
    head = "struct F%s" % it["name"] if it["kind"] == "ScriptStruct" else "class %s" % it["name"]
    f.write("// %s %s  size 0x%X align %d\n%s%s {\n" % (it["kind"], it["path"], it["size"], it["align"], head,
                                                        " : %s" % it["super"] if it["super"] else ""))
    for c in it["consts"]:
        f.write("    // const %s = %s\n" % (c["name"], c["value"]))
    for q in it["properties"]:
        dim = "[%d]" % q["array_dim"] if q["array_dim"] > 1 else ""
        bit = " : mask 0x%X" % q["bitmask"] if "bitmask" in q else ""
        f.write("    /*0x%04X 0x%04X*/ %s %s%s%s;  // %s\n" % (q["offset"], q["size"], q["type"], q["name"], dim, bit,
                                                        "|".join(q["flag_names"])))
    for fn in it["functions"]:
        ret = next((q["type"] for q in fn["parms"] if q["flags"] & 0x400), "void")
        args = ", ".join("%s%s %s" % ("out " if q["flags"] & 0x100 else "", q["type"], q["name"])
                         for q in fn["parms"] if not q["flags"] & 0x400)
        native = " native 0x%08X" % fn["func"] if fn["flags"] & 0x400 else ""
        inat = " iNative %d" % fn["inative"] if fn["inative"] else ""
        f.write("    // %s%s%s parms 0x%X\n    %s %s(%s);\n" % ("|".join(fn["flag_names"]), native, inat,
                                                            fn["parms_size"], ret, fn["name"], args))
    f.write("};\n\n")


if __name__ == "__main__":
    args = sys.argv[1:]
    api = None
    if "--api" in args:  # also write the compact reference the HomefrontSDK Generator reads
        i = args.index("--api")
        api = args[i + 1] if i + 1 < len(args) else os.path.join(
            os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "api-reference.json")
        del args[i:i + 2]
    exe = args[0] if args else "HFDedicated.exe"
    Dumper(exe, api).run(os.path.join(os.path.dirname(os.path.abspath(__file__)), "out", exe.split(".")[0]))
