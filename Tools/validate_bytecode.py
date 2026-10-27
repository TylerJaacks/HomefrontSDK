"""Disassemble every script function in the live process and report how many decode exactly.

A function passes when disassembly consumes its whole bytecode and ends on EX_EndOfScript. Failures are
grouped by error so token formats can be fixed. usage: python validate_bytecode.py [HFDedicated.exe]
"""
import collections
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bytecode import BytecodeError, Disassembler
from proc import Process
from ue3 import S_SCRIPT, UE3


def main():
    ue = UE3(Process.by_name(sys.argv[1] if len(sys.argv) > 1 else "HFDedicated.exe")).index()
    d = Disassembler(ue)
    d.natives  # build the native table up front
    ok, errors, examples = 0, collections.Counter(), {}
    total = 0
    for o in ue.objects():
        if ue.name(ue.cls(o)) not in ("Function", "State"):
            continue
        if struct.unpack("<i", ue.p.read(o + S_SCRIPT + 4, 4))[0] <= 0:
            continue
        total += 1
        try:
            d.function(o)
            ok += 1
        except (BytecodeError, IndexError, struct.error, ValueError) as e:
            key = str(e).split(" at ")[0]
            errors[key] += 1
            examples.setdefault(key, "%s.%s: %s" % (ue.path(ue.outer(o)), ue.name(o), e))
    print("%d/%d scripts decode exactly (%.1f%%)" % (ok, total, 100.0 * ok / max(total, 1)))
    for key, n in errors.most_common(15):
        print("%6d  %s   e.g. %s" % (n, key, examples[key]))


if __name__ == "__main__":
    main()
