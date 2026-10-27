"""Talk to the HomefrontSDK editor bridge (127.0.0.1:27040) from the command line.

usage:
  python HomefrontSDK/Tools/hfbridge.py info
  python HomefrontSDK/Tools/hfbridge.py command "!bots add 4"
  python HomefrontSDK/Tools/hfbridge.py objects class=Pawn
  python HomefrontSDK/Tools/hfbridge.py object ptr=123456
  python HomefrontSDK/Tools/hfbridge.py set ptr=123456 property=Health value=50
  python HomefrontSDK/Tools/hfbridge.py logs          (follow the server's SDK and mod logs; Ctrl+C to stop)
  python HomefrontSDK/Tools/hfbridge.py <cmd> key=value ...   (any request; values are JSON when they parse, else text)
"""
import json
import socket
import sys


def connect(port=27040):
    s = socket.create_connection(("127.0.0.1", port), timeout=10)
    return s, s.makefile("r", encoding="utf-8")


def request(sock, reader, cmd, **args):
    sock.sendall((json.dumps(dict(args, id=1, cmd=cmd)) + "\n").encode("utf-8"))
    while True:
        line = reader.readline()
        if not line:
            raise SystemExit("bridge closed the connection")
        msg = json.loads(line)
        if msg.get("id") == 1:
            return msg


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    cmd = sys.argv[1]
    args = {}
    rest = sys.argv[2:]
    if cmd == "command" and rest and "=" not in rest[0]:
        args["line"] = " ".join(rest)
        rest = []
    for a in rest:
        k, _, v = a.partition("=")
        try:
            args[k] = json.loads(v)
        except ValueError:
            args[k] = v
    sock, reader = connect()
    if cmd == "logs":
        print(json.dumps(request(sock, reader, "logs", subscribe=True)))
        sock.settimeout(None)
        for line in reader:
            msg = json.loads(line)
            if msg.get("event") == "log":
                print("[%s] %s" % (msg["source"], msg["line"]), flush=True)
        return
    msg = request(sock, reader, cmd, **args)
    if not msg.get("ok"):
        raise SystemExit("error: " + msg.get("error", "?"))
    result = msg.get("result")
    if cmd == "command" and isinstance(result, list):
        print("\n".join(result))
    else:
        print(json.dumps(result, indent=1))


if __name__ == "__main__":
    main()
