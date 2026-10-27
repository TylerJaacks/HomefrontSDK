using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using HomefrontSDK.Internal;

namespace HomefrontSDK;

/// <summary>The editor bridge: a loopback TCP server (127.0.0.1:27040, or HFSDK_BRIDGE_PORT) speaking JSON lines,
/// for HomefrontEditor and other local tools.</summary>
/// <remarks>
/// A request is one line, <c>{"id": 1, "cmd": "objects", ...}</c>; the answer is <c>{"id": 1, "ok": true, "result": ...}</c>
/// or <c>{"id": 1, "ok": false, "error": "..."}</c>. Socket threads only queue requests: they run on the game thread
/// at the end of each tick, like everything else that touches the engine. Clients subscribed with "logs" also get
/// <c>{"event": "log", "source": ..., "line": ...}</c> lines. Only this machine can connect.
/// Built in: info, objects, object, set, actors, teleport, command, logs, handlers. Mods add their own with
/// <see cref="Handle"/>.
/// </remarks>
public static class Bridge
{
    public const int DefaultPort = 27040;
    const int MaxRequestsPerTick = 64;

    sealed class Client
    {
        public required TcpClient Tcp;
        public readonly BlockingCollection<string> Outgoing = new(new ConcurrentQueue<string>(), 10000);
        public volatile bool Logs;
        public volatile bool Closed;

        public void Send(string line)
        {
            if (!Closed && !Outgoing.TryAdd(line))
                Close();  // a client that stopped reading
        }

        public void Close()
        {
            Closed = true;
            try { Tcp.Close(); } catch (Exception) { }
            Outgoing.CompleteAdding();
        }
    }

    static readonly ConcurrentQueue<(Client client, JsonObject request)> requests = new();
    static readonly List<Client> clients = new();
    static readonly Dictionary<string, (Mod? owner, Func<JsonObject, JsonNode?> run)> handlers = new(StringComparer.OrdinalIgnoreCase);
    static TcpListener? listener;

    /// <summary>The port the bridge listens on, or 0 when it isn't running.</summary>
    public static int Port { get; private set; }

    /// <summary>Adds a request handler (a mod's, e.g. "bots.list"). It runs on the game thread and gets the whole
    /// request; what it returns is the result. Throw to answer with an error.</summary>
    public static void Handle(string name, Func<JsonObject, JsonNode?> handler) =>
        handlers[name] = (Mods.OwnerOf(handler.Method.Module.Assembly), handler);

    internal static void RemoveAll(Mod mod)
    {
        foreach (string name in handlers.Where(h => h.Value.owner == mod).Select(h => h.Key).ToList())
            handlers.Remove(name);
    }

    internal static void Start()
    {
        int port = int.TryParse(Environment.GetEnvironmentVariable("HFSDK_BRIDGE_PORT"), out int p) ? p : DefaultPort;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Port = port;
            new Thread(Accept) { IsBackground = true, Name = "HomefrontSDK bridge" }.Start();
            Native.Log($"bridge: listening on 127.0.0.1:{port}");
        }
        catch (SocketException e)
        {
            Native.Log($"bridge: can't listen on 127.0.0.1:{port}: {e.Message}");
        }
    }

    static void Accept()
    {
        while (true)
        {
            TcpClient tcp;
            try
            {
                tcp = listener!.AcceptTcpClient();
            }
            catch (Exception)
            {
                return;
            }
            tcp.NoDelay = true;
            var client = new Client { Tcp = tcp };
            lock (clients)
                clients.Add(client);
            new Thread(() => Read(client)) { IsBackground = true, Name = "HomefrontSDK bridge reader" }.Start();
            new Thread(() => Write(client)) { IsBackground = true, Name = "HomefrontSDK bridge writer" }.Start();
        }
    }

    static void Read(Client client)
    {
        try
        {
            using var reader = new StreamReader(client.Tcp.GetStream(), new UTF8Encoding(false));
            while (!client.Closed && reader.ReadLine() is string line)
            {
                if (line.Length == 0)
                    continue;
                JsonObject? request = null;
                try
                {
                    request = JsonNode.Parse(line) as JsonObject;
                }
                catch (Exception)
                {
                }
                if (request == null)
                    client.Send(new JsonObject { ["ok"] = false, ["error"] = "not a JSON object" }.ToJsonString());
                else
                    requests.Enqueue((client, request));
            }
        }
        catch (Exception)
        {
        }
        client.Close();
        lock (clients)
            clients.Remove(client);
    }

    static void Write(Client client)
    {
        try
        {
            var stream = client.Tcp.GetStream();
            foreach (string line in client.Outgoing.GetConsumingEnumerable())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception)
        {
            client.Close();
        }
    }

    /// <summary>A log line for subscribed clients (any thread).</summary>
    internal static void OnLog(string source, string line)
    {
        if (Port == 0)
            return;
        string? json = null;
        lock (clients)
            foreach (Client c in clients)
                if (c.Logs)
                    c.Send(json ??= new JsonObject { ["event"] = "log", ["source"] = source, ["line"] = line }.ToJsonString());
    }

    /// <summary>Runs queued requests; called by the tick on the game thread.</summary>
    internal static void Pump()
    {
        for (int n = 0; n < MaxRequestsPerTick && requests.TryDequeue(out var item); ++n)
        {
            var (client, request) = item;
            JsonNode? id = request["id"]?.DeepClone();
            JsonObject answer;
            try
            {
                answer = new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = Run(client, request) };
            }
            catch (Exception e)
            {
                answer = new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = e.Message };
            }
            client.Send(answer.ToJsonString());
        }
    }

    static JsonNode? Run(Client client, JsonObject r)
    {
        string cmd = r["cmd"]?.GetValue<string>() ?? throw new ArgumentException("no \"cmd\"");
        switch (cmd)
        {
            case "info": return Info();
            case "objects": return Requests.Objects(r);
            case "object": return Inspector.Describe(Requests.Target(r));
            case "set": return Inspector.Set(Requests.Target(r), Str(r, "property"), r["value"]);
            case "actors": return Requests.Actors(r);
            case "teleport": return Requests.Teleport(r);
            case "command":
            {
                string line = Str(r, "line");
                var output = Internal.Commands.RunFromTool(line) ?? throw new ArgumentException($"no command {line.Split(' ')[0]}");
                return new JsonArray(output.Select(o => (JsonNode?)o).ToArray());
            }
            case "logs":
                client.Logs = r["subscribe"]?.GetValue<bool>() ?? true;
                return client.Logs;
            case "handlers":
                return new JsonArray(handlers.Keys.Order().Select(k => (JsonNode?)k).ToArray());
        }
        if (handlers.TryGetValue(cmd, out var h))
        {
            if (h.owner != null && !h.owner.Running)
                throw new InvalidOperationException($"{h.owner.Name} is stopped");
            try
            {
                return h.run(r);
            }
            catch (Exception e) when (h.owner != null && e is not ArgumentException)
            {
                Mods.Fault(h.owner, $"bridge handler {cmd}", e);
                throw;
            }
        }
        throw new ArgumentException($"unknown cmd \"{cmd}\"");
    }

    internal static string Str(JsonObject r, string key) =>
        r[key]?.GetValue<string>() ?? throw new ArgumentException($"no \"{key}\"");

    static JsonObject Info() => new()
    {
        ["game"] = GeneratedRegistry.Game,
        ["sdk"] = typeof(Bridge).Assembly.GetName().Version?.ToString(3),
        ["map"] = Game.MapName,
        ["mapLoaded"] = Game.MapLoaded,
        ["gameMode"] = Game.Info?.ClassName,
        ["players"] = Game.Players.Count,
        ["controllers"] = Game.Controllers.Count,
        ["mods"] = new JsonArray(Mods.All.Select(m => (JsonNode?)new JsonObject
        {
            ["name"] = m.Name, ["version"] = m.Version, ["running"] = m.Running,
        }).ToArray()),
        ["handlers"] = new JsonArray(handlers.Keys.Order().Select(k => (JsonNode?)k).ToArray()),
        ["commands"] = new JsonArray(Internal.Commands.Names.Select(k => (JsonNode?)k).ToArray()),
    };
}
