"""Print a function's bytecode as hex plus as much disassembly as decodes.

usage: python show_script.py Package.Class.Function [...]   (or Class.Function)
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bytecode import BytecodeError, Disassembler
from proc import Process
from ue3 import UE3


def find_function(ue, spec):
    parts = spec.split(".")
    fn_name, owner = parts[-1], ".".join(parts[:-1])
    for o in ue.objects():
        if ue.name(o) == fn_name and ue.name(ue.cls(o)) in ("Function", "State"):
            p = ue.path(ue.outer(o))
            if p == owner or p.endswith("." + owner):
                return o
    return None


def main():
    ue = UE3(Process.by_name(os.environ.get("HF_EXE", "HFDedicated.exe"))).index()
    d = Disassembler(ue)
    for spec in sys.argv[1:]:
        fn = find_function(ue, spec)
        if not fn:
            print("== %s: not found" % spec)
            continue
        code = d.script(fn)
        print("== %s (%d bytes)" % (spec, len(code)))
        for o in range(0, len(code), 16):
            print("   %04X  %s" % (o, code[o:o + 16].hex(" ")))
        d.code, d.i = code, 0
        while d.i < len(code):
            at = d.i
            try:
                print("   %04X: %s" % (at, d.expr()))
            except (BytecodeError, IndexError, Exception) as e:
                print("   %04X: !! %s" % (at, e))
                break


if __name__ == "__main__":
    main()
