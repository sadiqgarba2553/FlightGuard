using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace FlightGuard;

public sealed record StateEntry(int Id, int Type, string Path);
public sealed record Device(string Address, string Name, string Aircraft)
{ public override string ToString() => Name + "  ·  " + Aircraft + "  ·  " + Address; }

public sealed class ConnectClient : IDisposable
{
    public static int ConnectionAttempts {get;private set;}
    private readonly TcpClient tcp = new() { NoDelay = true };
    private NetworkStream? stream;
    public Dictionary<string, StateEntry> Manifest { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, StateEntry> ParseManifest(string text)
    {
        var result = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string row in text.Split('\n'))
        {
            string[] p = row.Trim().Split(',', 3);
            if (p.Length == 3 && int.TryParse(p[0], out int id) && int.TryParse(p[1], out int type) && type >= 0 && type <= 5)
                result[p[2].Trim()] = new(id, type, p[2].Trim());
        }
        return result;
    }
    public async Task Open(string host, CancellationToken token)
    {
        ConnectionAttempts++;
        if (!IPAddress.TryParse(host, out var address) || !LocalAddress(address)) throw new ArgumentException("Enter the phone/tablet's local IP address (for example 192.168.1.20). Hostnames and Internet addresses are not used.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(6000);
        await tcp.ConnectAsync(address, 10112, timeout.Token);
        stream = tcp.GetStream();
        object manifest = await Request(new(-1, 4, "manifest"), timeout.Token);
        Manifest = ParseManifest((string)manifest);
        if (Manifest.Count < 3) throw new InvalidDataException("Connect returned an empty or invalid state manifest.");
    }
    public static bool LocalAddress(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.IsIPv4MappedToIPv6) return LocalAddress(a.MapToIPv4());
        byte[] b = a.GetAddressBytes();
        return a.AddressFamily == AddressFamily.InterNetwork ? b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 169 && b[1] == 254 : a.IsIPv6LinkLocal || (b[0] & 0xfe) == 0xfc;
    }
    public static object Decode(int type, byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        int expected = type switch { 0 => 1, 1 or 2 => 4, 3 or 5 => 8, _ => -1 };
        if (expected >= 0 && payload.Length != expected) throw new InvalidDataException("Invalid primitive payload length.");
        return type switch {
            0 => reader.ReadBoolean(), 1 => reader.ReadInt32(), 2 => reader.ReadSingle(), 3 => reader.ReadDouble(),
            4 => ReadString(reader, payload.Length), 5 => reader.ReadInt64(), _ => throw new InvalidDataException("Unsupported state type.")
        };
    }
    private static string ReadString(BinaryReader r, int length)
    {
        if (length < 4) throw new InvalidDataException("Missing string length.");
        int n = r.ReadInt32();
        if (n < 0 || n != length - 4) throw new InvalidDataException("Invalid string length.");
        return Encoding.UTF8.GetString(r.ReadBytes(n));
    }
    private async Task<object> Request(StateEntry state, CancellationToken token)
    {
        if (stream == null) throw new InvalidOperationException("Not connected.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(3000);
        byte[] request = new byte[5]; BinaryPrimitives.WriteInt32LittleEndian(request, state.Id);
        await stream.WriteAsync(request, timeout.Token);
        byte[] head = new byte[8]; await stream.ReadExactlyAsync(head, timeout.Token);
        int id = BinaryPrimitives.ReadInt32LittleEndian(head), length = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        if (id != state.Id || length < 0 || length > 8 * 1024 * 1024) throw new InvalidDataException("Invalid Connect response header.");
        byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, timeout.Token);
        return Decode(state.Type, data);
    }
    private async Task<Dictionary<string, object>> RequestBatch(Dictionary<string, StateEntry> states, CancellationToken token)
    {
        if (stream == null) throw new InvalidOperationException("Not connected.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(3000);
        byte[] requests = new byte[states.Count * 5]; int index = 0;
        var byId = new Dictionary<int, (string Key, StateEntry Entry)>();
        foreach (var pair in states) {
            BinaryPrimitives.WriteInt32LittleEndian(requests.AsSpan(index * 5), pair.Value.Id);
            byId.Add(pair.Value.Id, (pair.Key, pair.Value)); index++;
        }
        // Each command is still a five-byte GetState. One write eliminates a Wi-Fi round trip per state.
        await stream.WriteAsync(requests, timeout.Token);
        var values = new Dictionary<string, object>();
        for (int i = 0; i < states.Count; i++) {
            byte[] header = new byte[8]; await stream.ReadExactlyAsync(header, timeout.Token);
            int id = BinaryPrimitives.ReadInt32LittleEndian(header), length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (!byId.TryGetValue(id, out var entry) || values.ContainsKey(entry.Key) || length < 0 || length > 8 * 1024 * 1024) throw new InvalidDataException("Invalid batched Connect response.");
            byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, timeout.Token);
            values.Add(entry.Key, Decode(entry.Entry.Type, data));
        }
        return values;
    }
    public static readonly Dictionary<string, string[]> Paths = new() {
        ["latitude"]=new[]{"aircraft/0/latitude"},["longitude"]=new[]{"aircraft/0/longitude"},
        ["groundspeed"]=new[]{"aircraft/0/groundspeed"},
        ["engineCount"]=new[]{"aircraft/0/systems/engines/engine_count"},
        ["reverse0"]=new[]{"aircraft/0/systems/engines/0/reverse"},["reverse1"]=new[]{"aircraft/0/systems/engines/1/reverse"},
        ["reverse2"]=new[]{"aircraft/0/systems/engines/2/reverse"},["reverse3"]=new[]{"aircraft/0/systems/engines/3/reverse"},
        ["aircraft"] = new[] {"aircraft/0/name"}, ["app"] = new[] {"infiniteflight/app_state"},
        ["agl"] = new[] {"aircraft/0/altitude_agl"}, ["msl"] = new[] {"aircraft/0/altitude_msl"},
        ["ias"] = new[] {"aircraft/0/indicated_airspeed"}, ["vs"] = new[] {"aircraft/0/vertical_speed"},
        ["bank"] = new[] {"aircraft/0/bank"}, ["ground"] = new[] {"aircraft/0/is_on_ground"},
        ["gear"] = new[] {"aircraft/0/systems/landing_gear/lever_state"},
        ["gearState"] = new[] {"aircraft/0/systems/landing_gear/state"},
        ["spoilerPosition"] = new[] {"aircraft/0/systems/spoilers/position"},
        ["spoilers"] = new[] {"aircraft/0/systems/spoilers/state"}, ["brake"] = new[] {"aircraft/0/systems/parking_brake/state"},
        ["flaps"] = new[] {"aircraft/0/systems/flaps/state"}, ["ap"] = new[] {"aircraft/0/systems/autopilot/on"},
        ["stall"] = new[] {"aircraft/0/is_stall_warning_on", "aircraft/0/warning_stalling", "aircraft/0/is_stalling"},
        ["overspeed"] = new[] {"aircraft/0/is_over_speed_warning_on", "aircraft/0/warning_overspeed"}
    };
    public Dictionary<string, StateEntry> Resolve() => Paths.Select(kv => (kv.Key, Entry: kv.Value.Select(p => Manifest.GetValueOrDefault(p)).FirstOrDefault(x => x != null))).Where(x => x.Entry != null).ToDictionary(x => x.Key, x => x.Entry!);
    private async Task<FlapStop[]> FlapDefinitions(CancellationToken token)
    {
        var entries=new Dictionary<string,StateEntry>();
        foreach(var entry in Manifest.Values){var m=Regex.Match(entry.Path,@"^aircraft/0/configuration/flaps/(\d+)/(name|shortname|angle)$");if(m.Success&&int.Parse(m.Groups[1].Value)<=20)entries[m.Groups[1].Value+":"+m.Groups[2].Value]=entry;}
        if(entries.Count==0)return Array.Empty<FlapStop>();
        var values=await RequestBatch(entries,token);
        return entries.Keys.Select(k=>int.Parse(k.Split(':')[0])).Distinct().OrderBy(i=>i).Select(i=>new FlapStop(i,values.GetValueOrDefault(i+":name")?.ToString()??values.GetValueOrDefault(i+":shortname")?.ToString()??"State "+i,values.TryGetValue(i+":angle",out var a)?Convert.ToDouble(a):null)).ToArray();
    }
    public async Task Poll(Action<Frame> onFrame, Action<string> onInfo, CancellationToken token, int intervalMilliseconds = 80)
    {
        var selected = Resolve();
        var flapStops=await FlapDefinitions(token);
        foreach (string required in new[] {"agl", "msl", "ias", "vs", "bank", "ground", "app"})
            if (!selected.ContainsKey(required)) throw new InvalidDataException("Required state missing: " + required + ". Warnings cannot run on this manifest.");
        onInfo("Manifest: " + Manifest.Count + " states; " + selected.Count + " monitored. Missing optional: " + string.Join(", ", Paths.Keys.Except(selected.Keys)));
        string? aircraft = null;
        while (!token.IsCancellationRequested)
        {
            var start = DateTime.UtcNow;
            var values = await RequestBatch(selected, token);
            double Num(string k) => Convert.ToDouble(values[k]);
            bool? Bool(string k) => values.TryGetValue(k, out var v) ? Convert.ToBoolean(v) : null;
            int? Int(string k) => values.TryGetValue(k, out var v) ? Convert.ToInt32(v) : null;
            string name = values.GetValueOrDefault("aircraft")?.ToString() ?? "Unknown aircraft";
            if (aircraft != null && aircraft != name) throw new InvalidDataException("Aircraft changed. Reconnect to load its new manifest.");
            aircraft = name;
            if ((DateTime.UtcNow - start).TotalSeconds > 2) throw new IOException("Telemetry too slow. No warnings are issued from an incomplete sample; reconnect on a faster network.");
            var frame = new Frame {
                SampleLatencyMilliseconds = (DateTime.UtcNow-start).TotalMilliseconds,
                Aircraft = name, AppState = values["app"].ToString() ?? "Unknown",
                // Connect altitude_agl and altitude_msl are already feet. Speed states use metres/second.
                Agl = Num("agl"), Msl = Num("msl"),
                Latitude=values.TryGetValue("latitude",out var latitude)?Convert.ToDouble(latitude):null,Longitude=values.TryGetValue("longitude",out var longitude)?Convert.ToDouble(longitude):null,
                Ias = Num("ias") * 1.943844492, Vs = Num("vs") * 196.8503937, Bank = Num("bank") * 180 / Math.PI,
                GroundSpeedKnots=values.TryGetValue("groundspeed",out var gs)?Convert.ToDouble(gs)*1.943844492:null,
                EngineCount=Int("engineCount"),EngineReverse=new[]{Bool("reverse0"),Bool("reverse1"),Bool("reverse2"),Bool("reverse3")},
                SpoilerPosition=values.TryGetValue("spoilerPosition",out var position)?Convert.ToDouble(position):null,
                Ground = Bool("ground")!.Value, GearDown = Bool("gear"), GearState = Int("gearState"), Spoilers = Int("spoilers"),
                ParkingBrake = Bool("brake"), Flaps = Int("flaps"), FlapStops=flapStops, Autopilot = Bool("ap"), Stall = Bool("stall"), Overspeed = Bool("overspeed")
            };
            if (!frame.Usable) throw new InvalidDataException("Invalid telemetry values. Warnings stopped.");
            onFrame(frame);
            int delay = Math.Max(0, intervalMilliseconds - (int)(DateTime.UtcNow - start).TotalMilliseconds);
            await Task.Delay(delay, token);
        }
    }
    public static async Task<List<Device>> Discover(CancellationToken token)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 15000));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token); stop.CancelAfter(6500);
        var devices = new Dictionary<string, Device>();
        try {
            while (!stop.IsCancellationRequested) {
                var packet = await udp.ReceiveAsync(stop.Token);
                if (!LocalAddress(packet.RemoteEndPoint.Address)) continue;
                string host = packet.RemoteEndPoint.Address.ToString(), name = "Infinite Flight", aircraft = "Aircraft";
                try {
                    using var json = JsonDocument.Parse(packet.Buffer);
                    foreach (var p in json.RootElement.EnumerateObject()) {
                        if (p.Name.Equals("deviceName", StringComparison.OrdinalIgnoreCase)) name = p.Value.ToString();
                        if (p.Name.Equals("aircraft", StringComparison.OrdinalIgnoreCase)) aircraft = p.Value.ToString();
                    }
                } catch (JsonException) { continue; }
                devices[host] = new(host, name, aircraft);
            }
        } catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        return devices.Values.ToList();
    }
    public void Dispose() { stream?.Dispose(); tcp.Dispose(); }
}
