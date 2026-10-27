"""Read Homefront's UE3 object system (engine v576/licensee 100) out of a running process.

Layouts were worked out from the live server (HFDedicated.exe, 2011-08-10 build); see README.md (this folder).
Addresses of the globals are for that exe; the client has its own (CLIENT_GLOBALS).

    ue = UE3(Process.by_name("HFDedicated.exe"))
    cls = ue.find("Class", "Actor"); ue.full_name(cls); for f in ue.fields(cls): ...
"""
import struct

SERVER_GLOBALS = {"GObjects": 0x018BBD6C, "GNames": 0x018BBD90}
CLIENT_GLOBALS = {}  # not mapped yet

# UObject (size 0x3C)
O_VTABLE, O_INDEX, O_FLAGS, O_HASHNEXT, O_HASHOUTERNEXT, O_STATEFRAME = 0x00, 0x04, 0x08, 0x10, 0x14, 0x18
O_LINKER, O_LINKERINDEX, O_NETINDEX, O_OUTER, O_NAME, O_CLASS, O_ARCHETYPE = 0x1C, 0x20, 0x24, 0x28, 0x2C, 0x34, 0x38
UOBJECT_SIZE = 0x3C
# FNameEntry: QWORD Flags; INT Index (<<1 | unicode); FNameEntry* HashNext; name text
NAMEENTRY_INDEX, NAMEENTRY_TEXT = 0x08, 0x10
# UField : UObject (size 0x40)
F_NEXT = 0x3C
# UStruct : UField (size 0x8C). PropertiesSize shares a dword with MinAlignment (high 16 bits).
S_SCRIPTTEXT, S_CPPTEXT, S_SUPER, S_CHILDREN, S_SIZE_ALIGN = 0x40, 0x44, 0x48, 0x4C, 0x50
S_SCRIPT = 0x54  # TArray<BYTE> bytecode {Data, Num, Max}
S_TEXTPOS, S_LINE = 0x60, 0x64
S_PROPERTYLINK = 0x68  # first of several property link lists
S_SCRIPTOBJREFS = 0x80  # TArray<UObject*>
USTRUCT_SIZE = 0x8C
# UProperty : UField (size 0x80); type-specific fields follow at 0x80
P_ARRAYDIM, P_ELEMSIZE, P_FLAGS, P_CATEGORY, P_REP, P_OFFSET = 0x40, 0x44, 0x48, 0x50, 0x58, 0x60
P_LINKNEXT, P_REPOWNER = 0x64, 0x74
P_EXTRA0, P_EXTRA1 = 0x80, 0x84  # PropertyClass/Struct/Inner/Enum/BitMask/Function, then MetaClass/SourceDelegate
# UFunction : UStruct
FN_FLAGS, FN_INATIVE, FN_REPOFFSET, FN_OPERPREC, FN_NUMPARMS, FN_PARMSSIZE, FN_RETOFFSET = 0x8C, 0x90, 0x92, 0x9C, 0x9D, 0x9E, 0xA0
FN_FUNC = 0xA8  # native exec thunk, or UObject::ProcessInternal for script functions
PROCESS_INTERNAL = 0x00486820  # server exe

FUNC_FLAGS = {0x1: "Final", 0x2: "Defined", 0x4: "Iterator", 0x8: "Latent", 0x10: "PreOperator", 0x20: "Singular",
              0x40: "Net", 0x80: "NetReliable", 0x100: "Simulated", 0x200: "Exec", 0x400: "Native", 0x800: "Event",
              0x1000: "Operator", 0x2000: "Static", 0x4000: "HasOptionalParms", 0x8000: "Const", 0x20000: "Public",
              0x40000: "Private", 0x80000: "Protected", 0x100000: "Delegate", 0x200000: "NetServer",
              0x400000: "HasOutParms", 0x800000: "HasDefaults", 0x1000000: "NetClient", 0x2000000: "DLLImport"}
PROP_FLAGS = {0x1: "Edit", 0x2: "Const", 0x4: "Input", 0x8: "ExportObject", 0x10: "OptionalParm", 0x20: "Net",
              0x40: "EditFixedSize", 0x80: "Parm", 0x100: "OutParm", 0x200: "SkipParm", 0x400: "ReturnParm",
              0x800: "CoerceParm", 0x1000: "Native", 0x2000: "Transient", 0x4000: "Config", 0x8000: "Localized",
              0x20000: "EditConst", 0x40000: "GlobalConfig", 0x80000: "Component", 0x100000: "AlwaysInit",
              0x200000: "DuplicateTransient", 0x400000: "NeedCtorLink", 0x800000: "NoExport",
              0x2000000: "NoClear", 0x4000000: "EditInline", 0x10000000: "EditInlineUse",
              0x20000000: "Deprecated", 0x40000000: "DataBinding", 0x80000000: "SerializeText"}


def flag_names(value, table):
    return [n for bit, n in table.items() if value & bit]


class UE3:
    def __init__(self, proc, globals_=SERVER_GLOBALS):
        self.p = proc
        self.g = globals_
        self._names = {}
        self._hdr = {}  # obj -> (outer, name index, name number, class), filled by index()
        self.refresh()

    def index(self):
        """Read every object's header once so name()/cls()/outer() don't each hit the process."""
        for o in self.objects():
            raw = self.p.try_read(o + O_OUTER, 0x10)
            if raw:
                self._hdr[o] = struct.unpack("<IiiI", raw)
        return self

    def refresh(self):
        self.objects_data, self.objects_num = struct.unpack("<II", self.p.read(self.g["GObjects"], 8))
        self.names_data, self.names_num = struct.unpack("<II", self.p.read(self.g["GNames"], 8))

    # ---- names
    def name_by_index(self, i):
        if i in self._names:
            return self._names[i]
        s = None
        if 0 <= i < self.names_num:
            e = self.p.u32(self.names_data + 4 * i)
            if e:
                unicode = self.p.u32(e + NAMEENTRY_INDEX) & 1
                s = self.p.cstr(e + NAMEENTRY_TEXT, wide=bool(unicode))
        self._names[i] = s
        return s

    def fname(self, addr):
        """FName stored at addr (index, number) -> text, with _N suffix for number > 0."""
        idx, num = struct.unpack("<ii", self.p.read(addr, 8))
        base = self.name_by_index(idx) or "?%d" % idx
        return base if num == 0 else "%s_%d" % (base, num - 1)

    # ---- objects
    def objects(self):
        """Yield every live object pointer."""
        n = self.objects_num
        raw = self.p.read(self.objects_data, 4 * n)
        for ptr in struct.unpack("<%dI" % n, raw):
            if ptr:
                yield ptr

    def name(self, obj):
        h = self._hdr.get(obj)
        if h is None:
            return self.fname(obj + O_NAME)
        base = self.name_by_index(h[1]) or "?%d" % h[1]
        return base if h[2] == 0 else "%s_%d" % (base, h[2] - 1)

    def cls(self, obj):
        h = self._hdr.get(obj)
        return h[3] if h else self.p.u32(obj + O_CLASS)

    def outer(self, obj):
        h = self._hdr.get(obj)
        return h[0] if h else self.p.u32(obj + O_OUTER)

    def full_name(self, obj):
        """'ClassName Outer.Outer.Name', like UObject::GetFullName."""
        parts, o = [], obj
        while o:
            parts.append(self.name(o))
            o = self.outer(o)
        return "%s %s" % (self.name(self.cls(obj)), ".".join(reversed(parts)))

    def path(self, obj):
        parts, o = [], obj
        while o:
            parts.append(self.name(o))
            o = self.outer(o)
        return ".".join(reversed(parts))

    def find(self, class_name, path):
        """Find an object by class name and dotted path (or bare name), e.g. find("Class", "Engine.Actor")."""
        want_name = path.split(".")[-1]
        for o in self.objects():
            if self.name(o) == want_name and self.name(self.cls(o)) == class_name:
                if "." not in path or self.path(o) == path:
                    return o
        return None
