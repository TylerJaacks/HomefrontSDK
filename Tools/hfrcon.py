"""Minimal client for the Homefront dedicated server admin (RCON) port.

Wire format, from the decompiled DedicatedServer.exe launcher:
  client -> server: b"CT" + uint32 BE length + UTF-8 text
  server -> client: 2-char type + 1-byte channel + uint32 BE length + UTF-8 text
Handshake: server sends "HELLO:<version>", client replies PASS: "<hash>", where <hash> is the
SHA-1 of AdminPassword (from the profile's DedicatedPlaylist) as uppercase hex bytes joined by
spaces, e.g. "60 37 4A ...". Commands the launcher uses: "admin NextMap", "admin kick \"name\"",
"RETRIEVE PLAYERLIST", ...

usage: python hfrcon.py [--host ADDRESS] [--port 27010] [--password PLAINTEXT] [--wait 5] [command ...]
  --host       default: this PC's internet-facing address (a server started with -MULTIHOME listens only there)
  --password   default: AdminPassword from the server's DedicatedPlaylist
"""
import argparse
import hashlib
import os
import socket
import struct
import time

PLAYLIST = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
                        "Homefront Dedicated Server", "GCGame", "Config", "DedicatedProfile_DefaultProfile",
                        "DedicatedPlaylist")


def internet_address():
    """This PC's address on the network that reaches the internet (a UDP connect sends nothing)."""
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.connect(("8.8.8.8", 53))
        return s.getsockname()[0]


def playlist_password():
    """AdminPassword from the server's DedicatedPlaylist, or None."""
    try:
        with open(PLAYLIST, encoding="latin-1") as f:
            for line in f:
                if line.startswith("AdminPassword="):
                    return line.split("=", 1)[1].strip() or None
    except OSError:
        pass
    return None


def send(sock, text, mtype=b"CT"):
    data = text.encode("utf-8")
    sock.sendall(mtype + struct.pack(">I", len(data)) + data)


def read_messages(sock, wait, until=None):
    """Yield (type, channel, text) for `wait` seconds, or until a message starts with `until`."""
    buf = b""
    end = time.time() + wait
    sock.settimeout(0.25)
    while time.time() < end:
        try:
            chunk = sock.recv(65536)
            if not chunk:
                print("-- server closed the connection")
                return
            buf += chunk
        except socket.timeout:
            pass
        except ConnectionError as e:
            print("-- connection lost:", e)
            return
        while len(buf) >= 7:
            mtype, channel = buf[:2].decode("latin-1"), buf[2]
            (length,) = struct.unpack(">I", buf[3:7])
            if len(buf) < 7 + length:
                break
            text = buf[7:7 + length].decode("utf-8", "replace")
            buf = buf[7 + length:]
            yield time.strftime("%H:%M:%S"), mtype, channel, text
            if until and text.startswith(until):
                return
    if buf:
        yield time.strftime("%H:%M:%S"), "??", -1, repr(buf)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host")
    ap.add_argument("--port", type=int, default=27010)
    ap.add_argument("--password")
    ap.add_argument("--wait", type=float, default=5.0)
    ap.add_argument("--listen", type=float, default=0, help="after commands, stay connected this many seconds, "
                    "printing server events (LOGIN:, CHANGE LEVEL:, KILL:, ...) and pinging like the launcher")
    ap.add_argument("commands", nargs="*")
    args = ap.parse_args()
    if args.host is None:
        args.host = internet_address()
    if args.password is None:
        args.password = playlist_password()

    with socket.create_connection((args.host, args.port), timeout=5) as sock:
        # The server closes the connection if PASS doesn't follow HELLO within a few seconds.
        for m in read_messages(sock, args.wait, until="HELLO"):
            print("%s %s ch%d: %s" % m, flush=True)
        if args.password is not None:
            digest = hashlib.sha1(args.password.encode("latin-1")).digest()
            send(sock, 'PASS: "%s"' % " ".join("%02X" % b for b in digest))
            for m in read_messages(sock, args.wait, until="AUTH"):
                print("%s %s ch%d: %s" % m, flush=True)
        for cmd in args.commands:
            print(">>", cmd)
            send(sock, cmd)
            for m in read_messages(sock, args.wait):
                print("%s %s ch%d: %s" % m, flush=True)
        end = time.time() + args.listen
        while time.time() < end:
            send(sock, "PING", b"CP")  # the launcher pings every 5 s; the server answers PONG
            for m in read_messages(sock, min(5.0, end - time.time())):
                if m[3] != "PONG":
                    print("%s %s ch%d: %s" % m, flush=True)


if __name__ == "__main__":
    main()
