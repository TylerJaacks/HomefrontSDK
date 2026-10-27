"""Read-only access to a running 32-bit process (the Homefront server or client) from 64-bit Python.

Only reads memory: nothing here writes to, suspends or injects into the target.

    p = Process.by_name("HFDedicated.exe")
    p.u32(0x401000); p.read(addr, n); p.cstr(addr); p.find(b"None\\0")
"""
import ctypes
import ctypes.wintypes as wt
import struct
import subprocess

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p), ("AllocationProtect", wt.DWORD),
                ("PartitionId", wt.WORD), ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD), ("Protect", wt.DWORD),
                ("Type", wt.DWORD)]


_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_k32.OpenProcess.restype = wt.HANDLE
_k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MBI), ctypes.c_size_t]
_k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                   ctypes.POINTER(ctypes.c_size_t)]


class Process:
    def __init__(self, pid):
        self.pid = pid
        self.h = _k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
        if not self.h:
            raise OSError("OpenProcess(%d) failed: %d" % (pid, ctypes.get_last_error()))

    @classmethod
    def by_name(cls, image):
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq %s" % image, "/FO", "CSV", "/NH"],
                             capture_output=True, text=True).stdout
        for line in out.splitlines():
            parts = [p.strip('"') for p in line.split('","')]
            if len(parts) > 1 and parts[0].lower() == image.lower():
                return cls(int(parts[1]))
        raise LookupError("%s is not running" % image)

    def regions(self):
        """Yield (base, size) of committed, readable regions below 4 GB."""
        addr, mbi = 0, MBI()
        while addr < 0x100000000 and _k32.VirtualQueryEx(self.h, addr, ctypes.byref(mbi), ctypes.sizeof(mbi)):
            if mbi.State == MEM_COMMIT and not (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)):
                yield addr, mbi.RegionSize
            addr += mbi.RegionSize

    def read(self, addr, n):
        buf = ctypes.create_string_buffer(n)
        got = ctypes.c_size_t()
        if not _k32.ReadProcessMemory(self.h, addr, buf, n, ctypes.byref(got)) or got.value != n:
            raise OSError("read %#x+%d failed" % (addr, n))
        return buf.raw

    def try_read(self, addr, n):
        try:
            return self.read(addr, n)
        except OSError:
            return None

    def u32(self, addr):
        return struct.unpack("<I", self.read(addr, 4))[0]

    def i32(self, addr):
        return struct.unpack("<i", self.read(addr, 4))[0]

    def cstr(self, addr, limit=256, wide=False):
        raw = self.try_read(addr, limit * (2 if wide else 1))
        if raw is None:
            return None
        if wide:
            end = next((i for i in range(0, len(raw) - 1, 2) if raw[i:i + 2] == b"\0\0"), len(raw))
            return raw[:end].decode("utf-16le", "replace")
        end = raw.find(b"\0")
        return raw[: end if end >= 0 else len(raw)].decode("latin-1")

    def find(self, needle, limit=None):
        """Yield addresses of `needle` in all readable memory."""
        found = 0
        for base, size in self.regions():
            data = self.try_read(base, size)
            if not data:
                continue
            i = data.find(needle)
            while i >= 0:
                yield base + i
                found += 1
                if limit and found >= limit:
                    return
                i = data.find(needle, i + 1)
