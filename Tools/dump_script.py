"""Disassemble every class's UnrealScript from a running Homefront process into text files.

Read-only. Writes Tools/out/<exe>/script/<Package>/<Class>.txt: each function and state of the class
with its flags and its bytecode rendered as pseudo-UnrealScript (offset: statement).

usage: python dump_script.py [HFDedicated.exe]
"""
import collections
import os
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bytecode import BytecodeError, Disassembler
from proc import Process
from ue3 import FN_FLAGS, FUNC_FLAGS, S_CHILDREN, S_SCRIPT, S_SUPER, F_NEXT, UE3, flag_names


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else "HFDedicated.exe"
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out", exe.split(".")[0], "script")
    t0 = time.time()
    ue = UE3(Process.by_name(exe)).index()
    d = Disassembler(ue)
    d.natives
    u32 = ue.p.u32
    written = failed = 0
    for cls in ue.objects():
        if ue.name(ue.cls(cls)) != "Class":
            continue
        members = []  # (kind, object) for functions and states, states also contributing their functions
        c = u32(cls + S_CHILDREN)
        while c:
            k = ue.name(ue.cls(c))
            if k in ("Function", "State"):
                members.append((k, c, None))
                if k == "State":
                    s = u32(c + S_CHILDREN)
                    while s:
                        if ue.name(ue.cls(s)) == "Function":
                            members.append(("Function", s, ue.name(c)))
                        s = u32(s + F_NEXT)
            c = u32(c + F_NEXT)
        if not members:
            continue
        sup = u32(cls + S_SUPER)
        lines = ["// %s extends %s" % (ue.path(cls), ue.path(sup) if sup else "(none)"), ""]
        for kind, o, state in members:
            nbytes = struct.unpack("<i", ue.p.read(o + S_SCRIPT + 4, 4))[0]
            title = "%s.%s" % (state, ue.name(o)) if state else ue.name(o)
            if kind == "Function":
                flags = flag_names(u32(o + FN_FLAGS), FUNC_FLAGS)
                lines.append("// %s  [%s]%s" % (title, "|".join(flags), "" if nbytes > 0 else "  (no script)"))
            else:
                lines.append("// state %s" % title)
            if nbytes > 0:
                try:
                    for off, text in d.function(o):
                        if text and text not in ("end of script", "nothing"):
                            lines.append("    %04X: %s" % (off, text))
                except (BytecodeError, IndexError, struct.error, ValueError) as e:
                    lines.append("    !! disassembly failed: %s" % e)
                    failed += 1
            lines.append("")
        pkg = ue.path(cls).split(".")[0]
        os.makedirs(os.path.join(out, pkg), exist_ok=True)
        with open(os.path.join(out, pkg, ue.name(cls) + ".txt"), "w", encoding="utf-8") as f:
            f.write("\n".join(lines))
        written += 1
    print("wrote %d class files to %s in %.1fs (%d scripts failed)" % (written, out, time.time() - t0, failed))


if __name__ == "__main__":
    main()
