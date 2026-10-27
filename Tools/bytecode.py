"""UnrealScript bytecode disassembler for Homefront's UE3 (v576), reading cooked script out of a live process.

In memory, object references in bytecode are 4-byte pointers and names are 8-byte FNames.
Each token becomes a (offset, text) line; expressions are rendered inline as pseudo-UnrealScript.

    d = Disassembler(ue); for off, line in d.function(fn_ptr): print(off, line)
"""
import struct

from ue3 import FN_INATIVE, S_SCRIPT

# Cast codes for EX_PrimitiveCast in this (2009-era) UE3; later engines renumbered them.
# Checked against the server: NetMode (byte) compared with NotEqual_IntInt is cast 0x3A.
CASTS = {0x39: "RotatorToVector", 0x3A: "ByteToInt", 0x3B: "ByteToBool", 0x3C: "ByteToFloat", 0x3D: "IntToByte",
         0x3E: "IntToBool", 0x3F: "IntToFloat", 0x40: "BoolToByte", 0x41: "BoolToInt", 0x42: "BoolToFloat",
         0x43: "FloatToByte", 0x44: "FloatToInt", 0x45: "FloatToBool", 0x46: "ObjectToInterface",
         0x47: "ObjectToBool", 0x48: "NameToBool", 0x49: "StringToByte", 0x4A: "StringToInt", 0x4B: "StringToBool",
         0x4C: "StringToFloat", 0x4D: "StringToVector", 0x4E: "StringToRotator", 0x4F: "VectorToBool",
         0x50: "VectorToRotator", 0x51: "RotatorToBool", 0x52: "ByteToString", 0x53: "IntToString",
         0x54: "BoolToString", 0x55: "FloatToString", 0x56: "ObjectToString", 0x57: "NameToString",
         0x58: "VectorToString", 0x59: "RotatorToString", 0x5A: "DelegateToString", 0x60: "StringToName"}


class BytecodeError(Exception):
    pass


class Disassembler:
    def __init__(self, ue, natives=None):
        """natives: {iNative: function name} for tokens >= 0x60 (built lazily from GObjects if None)."""
        self.ue = ue
        self._natives = natives

    @property
    def natives(self):
        if self._natives is None:
            self._natives = {}
            for o in self.ue.objects():
                if self.ue.name(self.ue.cls(o)) == "Function":
                    n = struct.unpack("<H", self.ue.p.read(o + FN_INATIVE, 2))[0]
                    if n:
                        self._natives[n] = self.ue.name(o)
        return self._natives

    def script(self, struct_obj):
        data, num = struct.unpack("<Ii", self.ue.p.read(struct_obj + S_SCRIPT, 8))
        return self.ue.p.read(data, num) if num > 0 else b""

    def function(self, fn):
        """Disassemble a UFunction/UState/UClass script; returns [(offset, line)]."""
        self.code = self.script(fn)
        self.i = 0
        lines = []
        while self.i < len(self.code):
            start = self.i
            tok = self.code[self.i]
            text = self.expr()
            lines.append((start, text))
            if tok == 0x53:  # EX_EndOfScript
                break
        if self.i != len(self.code):
            raise BytecodeError("stopped at %#x of %#x" % (self.i, len(self.code)))
        return lines

    # ---- operand readers
    def byte(self):
        v = self.code[self.i]
        self.i += 1
        return v

    def word(self):
        v = struct.unpack_from("<H", self.code, self.i)[0]
        self.i += 2
        return v

    def dword(self):
        v = struct.unpack_from("<I", self.code, self.i)[0]
        self.i += 4
        return v

    def float_(self):
        v = struct.unpack_from("<f", self.code, self.i)[0]
        self.i += 4
        return v

    def ptr(self):
        p = self.dword()
        if not p:
            return "None"
        try:
            return self.ue.name(p)
        except OSError:
            return "?%08X" % p

    def fname(self):
        idx, num = struct.unpack_from("<ii", self.code, self.i)
        self.i += 8
        n = self.ue.name_by_index(idx) or "?%d" % idx
        return n if num == 0 else "%s_%d" % (n, num - 1)

    def cstr(self):
        end = self.code.index(b"\0", self.i)
        s = self.code[self.i:end].decode("latin-1")
        self.i = end + 1
        return s

    def ustr(self):
        j = self.i
        while self.code[j:j + 2] != b"\0\0":
            j += 2
            if j >= len(self.code):
                raise BytecodeError("unterminated unicode string at %#x" % self.i)
        s = self.code[self.i:j].decode("utf-16le", "replace")
        self.i = j + 2
        return s

    def args(self):
        out = []
        while self.code[self.i] != 0x16:  # EX_EndFunctionParms
            out.append(self.expr())
        self.i += 1
        return ", ".join(out)

    # ---- expressions
    def expr(self):
        if self.i >= len(self.code):
            raise BytecodeError("ran past end at %#x" % self.i)
        at = self.i
        t = self.byte()
        if t >= 0x70:
            return "%s(%s)" % (self.natives.get(t, "native%d" % t), self.args())
        if 0x60 <= t < 0x70:
            n = ((t - 0x60) << 8) + self.byte()
            return "%s(%s)" % (self.natives.get(n, "native%d" % n), self.args())
        h = HANDLERS.get(t)
        if h is None:
            raise BytecodeError("unknown token %#04x at %#x" % (t, at))
        return h(self)


def _ctx(d):
    obj = d.expr()
    d.word()   # skip size of the member expression
    d.byte()   # value size
    d.byte()   # (v576 has no property pointer here, unlike later UE3)
    return "%s.%s" % (obj, d.expr())


def _label_table(d):
    labels = []
    while True:
        name = d.fname()
        off = d.dword()
        if name == "None":
            break
        labels.append("%s@%04X" % (name, off))
    return "labels: " + ", ".join(labels)


def _default_parm(d):
    d.word()
    v = d.expr()
    d.byte()  # EX_EndParmValue
    return "default = %s" % v


def _delegate_function(d):
    d.byte()
    d.ptr()
    return "%s(%s)" % (d.fname(), d.args())


def _conditional(d):
    c = d.expr()
    d.word()
    a = d.expr()
    d.word()
    return "(%s ? %s : %s)" % (c, a, d.expr())


def _iterator(d):
    e = d.expr()
    d.word()
    return "foreach %s" % e


def _dyn_iter(d):
    arr = d.expr()
    item = d.expr()
    has_index = d.byte()
    index = d.expr()
    d.word()
    return "foreach %s(%s%s)" % (arr, item, ", " + index if has_index else "")


def _struct_member(d):
    prop = d.ptr()
    d.ptr()     # struct
    d.byte()
    d.byte()
    return "%s.%s" % (d.expr(), prop)


HANDLERS = {
    0x00: lambda d: d.ptr(),                                              # LocalVariable
    0x01: lambda d: d.ptr(),                                              # InstanceVariable
    0x02: lambda d: "default." + d.ptr(),                                 # DefaultVariable
    0x03: lambda d: d.ptr(),                                              # StateVariable
    0x04: lambda d: "return %s" % d.expr(),                               # Return
    0x05: lambda d: (d.byte(), d.byte(), "switch (%s)" % d.expr())[-1],    # Switch: v576 has 2 bytes, no property
    0x06: lambda d: "goto %04X" % d.word(),                               # Jump
    0x07: lambda d: (lambda off: "if (!(%s)) goto %04X" % (d.expr(), off))(d.word()),  # JumpIfNot
    0x08: lambda d: "stop",
    0x09: lambda d: (d.word(), d.byte(), "assert(%s)" % d.expr())[-1],    # Assert
    0x0A: lambda d: (lambda off: "default:" if off == 0xFFFF else "case %s:" % d.expr())(d.word()),  # Case
    0x0B: lambda d: "nothing",
    0x0C: _label_table,
    0x0D: lambda d: "goto label %s" % d.expr(),                           # GotoLabel
    0x0E: lambda d: (d.ptr(), "")[-1],                                    # EatReturnValue (followed by the call)
    0x0F: lambda d: "%s = %s" % (d.expr(), d.expr()),                     # Let
    0x10: lambda d: "%s[%s]" % ((lambda i: (d.expr(), i))(d.expr())[::-1]),  # DynArrayElement: index, array
    0x11: lambda d: "new(%s, %s, %s) %s" % (d.expr(), d.expr(), d.expr(), d.expr()),  # New
    0x12: _ctx,                                                           # ClassContext
    0x13: lambda d: "class<%s>(%s)" % (d.ptr(), d.expr()),                # MetaCast
    0x14: lambda d: "%s = %s" % (d.expr(), d.expr()),                     # LetBool
    0x15: lambda d: "end parm value",
    0x16: lambda d: ")",
    0x17: lambda d: "self",
    0x18: lambda d: (d.word(), d.expr())[-1],                             # Skip
    0x19: _ctx,                                                           # Context
    0x1A: lambda d: "%s[%s]" % ((lambda i: (d.expr(), i))(d.expr())[::-1]),  # ArrayElement: index, array
    0x1B: lambda d: "%s(%s)" % (d.fname(), d.args()),                     # VirtualFunction
    0x1C: lambda d: "%s(%s)" % (d.ptr(), d.args()),                       # FinalFunction
    0x1D: lambda d: str(struct.unpack("<i", struct.pack("<I", d.dword()))[0]),  # IntConst
    0x1E: lambda d: "%g" % d.float_(),                                    # FloatConst
    0x1F: lambda d: '"%s"' % d.cstr(),                                    # StringConst
    0x20: lambda d: d.ptr(),                                              # ObjectConst
    0x21: lambda d: "'%s'" % d.fname(),                                   # NameConst
    0x22: lambda d: "rot(%d,%d,%d)" % (d.dword(), d.dword(), d.dword()),  # RotationConst
    0x23: lambda d: "vect(%g,%g,%g)" % (d.float_(), d.float_(), d.float_()),  # VectorConst
    0x24: lambda d: str(d.byte()),                                        # ByteConst
    0x25: lambda d: "0", 0x26: lambda d: "1", 0x27: lambda d: "true", 0x28: lambda d: "false",
    0x29: lambda d: d.ptr(),                                              # NativeParm
    0x2A: lambda d: "None",                                               # NoObject
    0x2C: lambda d: str(d.byte()),                                        # IntConstByte
    0x2D: lambda d: d.expr(),                                             # BoolVariable
    0x2E: lambda d: "%s(%s)" % (d.ptr(), d.expr()),                       # DynamicCast
    0x2F: _iterator,
    0x30: lambda d: "iterator pop",
    0x31: lambda d: "iterator next",
    0x32: lambda d: (d.ptr(), "(%s == %s)" % (d.expr(), d.expr()))[-1],   # StructCmpEq
    0x33: lambda d: (d.ptr(), "(%s != %s)" % (d.expr(), d.expr()))[-1],   # StructCmpNe
    0x34: lambda d: '"%s"' % d.ustr(),                                    # UnicodeStringConst
    0x35: _struct_member,
    0x36: lambda d: "%s.Length" % d.expr(),                               # DynArrayLength
    0x37: lambda d: "global.%s(%s)" % (d.fname(), d.args()),              # GlobalFunction
    0x38: lambda d: (lambda c: "%s(%s)" % (CASTS.get(c, "cast%02X" % c), d.expr()))(d.byte()),  # PrimitiveCast
    0x39: lambda d: "%s.Insert(%s, %s)" % (d.expr(), d.expr(), d.expr()),  # DynArrayInsert
    0x3A: lambda d: (d.ptr(), "return")[-1],                              # ReturnNothing
    0x3B: lambda d: "(%s == %s)" % (d.expr(), d.args()),                  # EqualEqual_DelDel
    0x3C: lambda d: "(%s != %s)" % (d.expr(), d.args()),                  # NotEqual_DelDel
    0x3D: lambda d: "(%s == %s)" % (d.expr(), d.args()),                  # EqualEqual_DelFunc
    0x3E: lambda d: "(%s != %s)" % (d.expr(), d.args()),                  # NotEqual_DelFunc
    0x3F: lambda d: "None",                                               # EmptyDelegate
    0x40: lambda d: "%s.Remove(%s, %s)" % (d.expr(), d.expr(), d.expr()),  # DynArrayRemove
    0x41: lambda d: (d.dword(), d.dword(), d.dword(), d.byte(), "")[-1],  # DebugInfo
    0x42: _delegate_function,
    0x43: lambda d: d.fname(),                                            # DelegateProperty: v576 is just the name
    0x44: lambda d: "%s = %s" % (d.expr(), d.expr()),                     # LetDelegate
    0x45: _conditional,
    0x46: lambda d: (lambda a: (d.word(), "%s.Find(%s)" % (a, d.expr()))[-1])(d.expr()),  # DynArrayFind
    0x47: lambda d: (lambda a: (d.word(), "%s.Find(%s, %s)" % (a, d.expr(), d.expr()))[-1])(d.expr()),
    0x48: lambda d: d.ptr(),                                              # LocalOutVariable
    0x49: _default_parm,
    0x4A: lambda d: "empty parm",
    0x4B: lambda d: d.fname(),                                            # InstanceDelegate
    0x51: lambda d: d.expr(),                                             # InterfaceContext
    0x52: lambda d: "%s(%s)" % (d.ptr(), d.expr()),                       # InterfaceCast
    0x53: lambda d: "end of script",
    0x54: lambda d: "%s.Add(%s)" % (d.expr(), d.expr()),                  # DynArrayAdd
    0x55: lambda d: (lambda a: (d.word(), "%s.AddItem(%s)" % (a, d.expr()))[-1])(d.expr()),
    0x56: lambda d: (lambda a: (d.word(), "%s.RemoveItem(%s)" % (a, d.expr()))[-1])(d.expr()),
    0x57: lambda d: (lambda a: (d.word(), "%s.InsertItem(%s, %s)" % (a, d.expr(), d.expr()))[-1])(d.expr()),
    0x58: _dyn_iter,
    0x59: lambda d: (lambda a: (d.word(), "%s.Sort(%s)" % (a, d.expr()))[-1])(d.expr()),
    0x5A: lambda d: "editor-only code until %04X" % d.word(),             # FilterEditorOnly (refs stripped in cooked builds)
}
