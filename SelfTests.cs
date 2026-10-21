using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlightGuard;

public static class SelfTests
{
    public static int Run(string output)
    {
        var results = new List<string>(); int passed = 0, failed = 0;
        void Test(string name, Action run) {
            try { run(); results.Add("PASS  " + name); passed++; } catch (Exception ex) { results.Add("FAIL  " + name + ": " + ex.Message); failed++; }
        }
        void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
        void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection"); }
        var settings = new Settings { SpeedsArmed = true, CalloutLeadMilliseconds=0 };
        Frame Frame(double agl = 1000, double vs = -700, double ias = 140, bool ground = false) => new() { Aircraft = "Airbus A320", Agl = agl, Msl = agl + 100, Ias = ias, Vs = vs, Ground = ground, GearDown = true, Flaps = 2 };
        Func<Frame, List<SoundCue>> Sequence(WarningEngine engine) {
            DateTime time = DateTime.UtcNow;
            return f => { time = time.AddMilliseconds(200); return engine.Update(f with { Time = time }, settings, "Airbus"); };
        }
        Test("Manifest uses dynamic IDs and skips command entries", () => {
            var m = ConnectClient.ParseManifest("201,2,aircraft/0/altitude_agl\n88,-1,commands/Test\r\n202,0,aircraft/0/is_on_ground\ninvalid");
            Assert(m.Count == 2 && m["aircraft/0/altitude_agl"].Id == 201);
        });
        Test("All six primitive types decode", () => {
            Assert((bool)ConnectClient.Decode(0, new byte[] {1})); Assert((int)ConnectClient.Decode(1, BitConverter.GetBytes(-42)) == -42);
            Assert((float)ConnectClient.Decode(2, BitConverter.GetBytes(1.25f)) == 1.25f); Assert((double)ConnectClient.Decode(3, BitConverter.GetBytes(2.75)) == 2.75);
            Assert((long)ConnectClient.Decode(5, BitConverter.GetBytes(5000000000L)) == 5000000000L);
            Assert((string)ConnectClient.Decode(4, StringBytes("MD-11 · test")) == "MD-11 · test");
        });
        Test("Malformed length rejected", () => { Throws(() => ConnectClient.Decode(2, new byte[2])); Throws(() => ConnectClient.Decode(4, new byte[] {8,0,0,0,65})); });
        Test("Public network targets rejected", () => { Assert(!ConnectClient.LocalAddress(IPAddress.Parse("8.8.8.8"))); Assert(ConnectClient.LocalAddress(IPAddress.Parse("192.168.1.50"))); Assert(ConnectClient.LocalAddress(IPAddress.IPv6Loopback)); });
        Test("Aircraft profile detection", () => {
            Assert(Catalog.Family("Airbus A350-900") == "Airbus"); Assert(Catalog.Family("Boeing 777-300ER") == "Boeing");
            Assert(Catalog.Family("McDonnell Douglas DC-10") == "McDonnell Douglas"); Assert(Catalog.Family("MD-11") == "McDonnell Douglas");
            Assert(Catalog.Family("Boeing 717") == "McDonnell Douglas"); Assert(Catalog.Family("Cessna 172") == "Cessna");
        });
        Test("No false callouts on connection", () => { var next = Sequence(new()); Assert(next(Frame(30)).Count == 0); });
        Test("Descending radio height crossing fires once", () => { var next = Sequence(new()); next(Frame(510)); Assert(next(Frame(499)).Any(c => c.Id == "altitude-500")); Assert(!next(Frame(498)).Any(c => c.Id == "altitude-500")); });
        Test("Climbing does not announce descending heights", () => { var next = Sequence(new()); next(Frame(499,700)); Assert(!next(Frame(510,700)).Any(x => x.Group == "Heights")); });
        Test("Minimums and hundred above are AGL crossings", () => {
            var next = Sequence(new()); next(Frame(305)); Assert(next(Frame(299)).Any(c => c.Id == "approaching-minimums"));
            next=Sequence(new()); next(Frame(205)); Assert(next(Frame(199)).Any(c => c.Id == "minimums"));
        });
        Test("V1 and rotate require acceleration on ground", () => {
            var next = Sequence(new()); next(Frame(5,0,134,true)); Assert(next(Frame(5,0,136,true)).Any(c => c.Id == "v1"));
            Assert(next(Frame(5,0,141,true)).Any(c => c.Id == "rotate")); Assert(!next(Frame(5,0,139,true)).Any(c => c.Id == "rotate"));
        });
        Test("Disarmed speeds never fire", () => {
            var e = new WarningEngine(); var s = new Settings { SpeedsArmed = false }; DateTime t = DateTime.UtcNow;
            e.Update(Frame(5,0,130,true) with { Time=t },s,"Airbus"); var a=e.Update(Frame(5,0,145,true) with {Time=t.AddSeconds(.2)},s,"Airbus"); Assert(!a.Any(c => c.Id == "v1" || c.Id == "rotate"));
        });
        Test("Positive rate follows observed liftoff", () => { var next=Sequence(new()); next(Frame(5,0,150,true)); Assert(next(Frame(40,1000,155)).Any(c=>c.Id=="positive-rate")); });
        Test("No positive rate on airborne connection", () => { var next=Sequence(new()); next(Frame(40,1000)); Assert(!next(Frame(45,1000)).Any(c=>c.Id=="positive-rate")); });
        Test("Ground suppresses GPWS warnings", () => { var next=Sequence(new()); next(Frame(5,-4000,140,true)); Assert(!next(Frame(5,-4000,140,true)).Any(c=>c.Priority>=60)); });
        Test("Pull-up immediately preempts approach callouts", () => { var next=Sequence(new()); next(Frame(510,-4000)); var a=next(Frame(499,-4000)); Assert(a.Any(c=>c.Id=="pull-up") && a.All(c=>c.Priority>=60)); });
        Test("Sink warning repeats at the revised 1.6-second cadence", () => {
            var next=Sequence(new()); next(Frame(700,-1800)); var all=new List<SoundCue>(); for(int i=0;i<12;i++) all.AddRange(next(Frame(694-i*6,-1800)));
            Assert(all.Count(c=>c.Id=="sink-rate")==2);
        });
        Test("Gear warning requires a known retracted lever", () => {
            var next=Sequence(new()); next(Frame(420) with {GearDown=false}); var all=new List<SoundCue>(); for(int i=0;i<10;i++) all.AddRange(next(Frame(416-i*3) with {GearDown=false})); Assert(all.Any(c=>c.Id=="too-low-gear"));
            next=Sequence(new()); next(Frame(420) with {GearDown=null}); all.Clear(); for(int i=0;i<10;i++) all.AddRange(next(Frame(416-i*3) with {GearDown=null})); Assert(!all.Any(c=>c.Id=="too-low-gear"));
        });
        Test("Gear and brake changes do not announce initial state", () => { var next=Sequence(new()); Assert(next(Frame() with {ParkingBrake=true}).Count==0); Assert(next(Frame() with {ParkingBrake=false}).Any(c=>c.Id=="parking-brake-released")); });
        Test("Unknown flaps never produce flap configuration warning", () => {
            var next=Sequence(new()); next(Frame(190) with {Flaps=null}); var a=new List<SoundCue>();for(int i=0;i<10;i++) a.AddRange(next(Frame(185-i*2) with {Flaps=null})); Assert(!a.Any(c=>c.Id=="too-low-flaps"));
        });
        Test("Autopilot disconnect requires true-to-false edge", () => { var next=Sequence(new());next(Frame() with {Autopilot=true});Assert(next(Frame() with {Autopilot=false}).Any(c=>c.Id=="autopilot-disconnect")); });
        Test("Pause resets baseline and suppresses audio", () => { var next=Sequence(new());next(Frame(510));Assert(next(Frame(490) with {AppState="Paused"}).Count==0);Assert(next(Frame(480)).Count==0); });
        Test("Stale and NaN frames rejected", () => { var e=new WarningEngine(); Assert(e.Update(Frame() with {Time=DateTime.UtcNow.AddSeconds(-10)}, settings,"Airbus").Count==0); Assert(Frame().Usable); Assert(!(Frame() with {Agl=double.NaN}).Usable); });
        Test("Teleport does not announce crossed thresholds", () => { var next=Sequence(new());next(Frame(5000));Assert(next(Frame(20)).Count==0); });
        Test("Disabled cues respected", () => { var e=new WarningEngine();var s=new Settings {DisabledSounds=new(){"altitude-500"}};DateTime t=DateTime.UtcNow;e.Update(Frame(510) with{Time=t},s,"Boeing");Assert(!e.Update(Frame(499) with{Time=t.AddSeconds(.2)},s,"Boeing").Any(c=>c.Id=="altitude-500")); });
        Test("Airbus-only retard callout", () => { var e=new WarningEngine();DateTime t=DateTime.UtcNow;e.Update(Frame(21,-300) with{Time=t},settings,"Boeing");Assert(!e.Update(Frame(19,-300) with{Time=t.AddSeconds(.2)},settings,"Boeing").Any(c=>c.Id=="retard")); });
        Test("Invalid speed order and NaN settings rejected", () => { Throws(()=>new Settings{V1=180,Vr=140}.Validate());Throws(()=>new Settings{Minimums=double.NaN}.Validate()); });
        Test("Catalog IDs unique", () => Assert(Catalog.All.Select(x=>x.Id).Distinct().Count()==Catalog.All.Count));
        Test("Bank angle warning follows sustained excessive bank",()=>{var next=Sequence(new());next(Frame() with{Bank=50});var a=new List<SoundCue>();for(int i=0;i<10;i++)a.AddRange(next(Frame() with{Bank=50}));Assert(a.Any(c=>c.Id=="bank-angle"));});
        Test("Don't sink uses altitude lost after observed takeoff",()=>{var next=Sequence(new());next(Frame(5,0,150,true));next(Frame(50,900));next(Frame(150,900));var a=new List<SoundCue>();for(int i=0;i<10;i++)a.AddRange(next(Frame(120-i,-400)));Assert(a.Any(c=>c.Id=="dont-sink"));});
        Test("Terrain closure detects rising terrain under the aircraft",()=>{var next=Sequence(new());next(Frame(1000,-300));var a=new List<SoundCue>();for(int i=0;i<20;i++)a.AddRange(next(Frame(975-i*25,-300) with{Msl=1099-i}));Assert(a.Any(c=>c.Id=="terrain"));});
        Test("Normal configured descent does not raise terrain warning",()=>{var next=Sequence(new());next(Frame(1000,-1000));var a=new List<SoundCue>();for(int i=0;i<20;i++)a.AddRange(next(Frame(997-i*3.3333,-1000)));Assert(!a.Any(c=>c.Id=="terrain"));});
        Test("Known fixed-gear aircraft cannot raise too-low-gear",()=>{var next=Sequence(new());next(Frame(400) with{Aircraft="Cessna 172",GearDown=false});var a=new List<SoundCue>();for(int i=0;i<10;i++)a.AddRange(next(Frame(395-i*3) with{Aircraft="Cessna 172",GearDown=false}));Assert(!a.Any(c=>c.Id=="too-low-gear"));});
        Test("Takeoff callouts re-arm after an aborted roll",()=>{var next=Sequence(new());next(Frame(5,0,134,true));next(Frame(5,0,136,true));next(Frame(5,0,20,true));next(Frame(5,0,134,true));Assert(next(Frame(5,0,136,true)).Any(c=>c.Id=="v1"));});
        Test("Height callout interrupts a routine crew clip",()=>{var q=new CueQueue();var t=DateTime.UtcNow;Assert(q.Offer(new[]{Catalog.Get("altitude-50")},t,Catalog.Get("gear-down"),t.AddSeconds(-.1)));});
        Test("V1 interrupts routine parking-brake audio",()=>{var q=new CueQueue();var t=DateTime.UtcNow;Assert(q.Offer(new[]{Catalog.Get("v1")},t,Catalog.Get("parking-brake-set"),t));});
        Test("Height cannot interrupt a pull-up warning",()=>{var q=new CueQueue();var t=DateTime.UtcNow;Assert(!q.Offer(new[]{Catalog.Get("altitude-50")},t,Catalog.Get("pull-up"),t));});
        Test("Rotate interrupts a late V1 clip",()=>{var q=new CueQueue();var t=DateTime.UtcNow;Assert(q.Offer(new[]{Catalog.Get("rotate")},t,Catalog.Get("v1"),t.AddSeconds(-.4)));});
        Test("Rotate briefly preserves a newly-started V1",()=>{var q=new CueQueue();var t=DateTime.UtcNow;Assert(!q.Offer(new[]{Catalog.Get("rotate")},t,Catalog.Get("v1"),t.AddSeconds(-.05)));});
        Test("Only the newest queued height survives",()=>{var q=new CueQueue();var t=DateTime.UtcNow;q.Offer(new[]{Catalog.Get("altitude-50")},t,null,t);q.Offer(new[]{Catalog.Get("altitude-40")},t.AddMilliseconds(40),null,t);Assert(q.Count==1&&q.Next(t.AddMilliseconds(60))!.Cue.Id=="altitude-40");});
        Test("Height callouts expire after 250ms",()=>{var q=new CueQueue();var t=DateTime.UtcNow;q.Offer(new[]{Catalog.Get("altitude-50")},t,null,t);Assert(q.Next(t.AddMilliseconds(300))==null);});
        Test("Critical warning clears pending routine calls",()=>{var q=new CueQueue();var t=DateTime.UtcNow;q.Offer(new[]{Catalog.Get("gear-down"),Catalog.Get("flaps-up")},t,null,t);q.Offer(new[]{Catalog.Get("pull-up")},t,null,t);Assert(q.Count==1&&q.Next(t)!.Cue.Id=="pull-up");});
        Test("Resolved warnings cannot remain in the queue",()=>{var q=new CueQueue();var t=DateTime.UtcNow;q.Offer(new[]{Catalog.Get("sink-rate")},t,null,t);q.WarningState("");Assert(q.Count==0);});
        Test("Routine crew feedback is inhibited during takeoff roll",()=>{var next=Sequence(new());next(Frame(5,0,90,true) with{ParkingBrake=true});Assert(!next(Frame(5,0,92,true) with{ParkingBrake=false}).Any(c=>c.Id=="parking-brake-released"));});
        Test("Bounded lead compensates a height call without duplication",()=>{var e=new WarningEngine();var s=new Settings{CalloutLeadMilliseconds=80};var t=DateTime.UtcNow;e.Update(Frame(50.5,-600) with{Time=t},s,"Airbus");Assert(e.Update(Frame(50.4,-600) with{Time=t.AddSeconds(.1)},s,"Airbus").Any(c=>c.Id=="altitude-50"));Assert(!e.Update(Frame(49.9,-600) with{Time=t.AddSeconds(.2)},s,"Airbus").Any(c=>c.Id=="altitude-50"));});
        Test("Timing lead never invents acceleration at constant speed",()=>{var e=new WarningEngine();var s=new Settings{SpeedsArmed=true,CalloutLeadMilliseconds=150};var t=DateTime.UtcNow;e.Update(Frame(5,0,134.9,true) with{Time=t},s,"Airbus");Assert(!e.Update(Frame(5,0,134.9,true) with{Time=t.AddSeconds(.1)},s,"Airbus").Any(c=>c.Id=="v1"));});
        Test("Regional and general-aviation manufacturers detected",()=>{Assert(Catalog.Family("E175")=="Embraer");Assert(Catalog.Family("CRJ-900")=="Bombardier");Assert(Catalog.Family("Cirrus SR22 GTS")=="Cirrus");Assert(Catalog.Family("Piper PA28 Archer")=="Piper");Assert(Catalog.Family("TBM-930")=="Daher");Assert(Catalog.Family("C-130J")=="Lockheed Martin");Assert(Catalog.Family("F-14")=="Grumman");Assert(Catalog.Family("Spitfire")=="Supermarine");});
        Test("Wave decoder trims dead air and retains the signal",()=>{byte[] pcm=new byte[16000*2];for(int i=8000;i<9600;i++){short n=(short)(Math.Sin(i*.2)*10000);pcm[i*2]=(byte)n;pcm[i*2+1]=(byte)(n>>8);}using var ms=new MemoryStream();using(var w=new BinaryWriter(ms,Encoding.UTF8,true)){w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36+pcm.Length);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(16000);w.Write(32000);w.Write((short)2);w.Write((short)16);w.Write(Encoding.ASCII.GetBytes("data"));w.Write(pcm.Length);w.Write(pcm);}var clip=WaveClip.Decode(ms.ToArray());Assert(clip.TrimmedLeadingMs>=480&&clip.Duration>.09&&clip.Duration<.16);});
        Test("Empty WAVs cannot be played as silent placeholders",()=>Throws(()=>WaveClip.Decode(new byte[44])));
        Test("A complete stable approach has no nuisance GPWS warnings",()=>{var e=new WarningEngine();var s=new Settings{CalloutLeadMilliseconds=80};var t=DateTime.UtcNow;var alerts=new List<SoundCue>();for(int i=0;i<3120;i++){double h=2500-i*.8;alerts.AddRange(e.Update(Frame(Math.Max(6,h),-600) with{Time=t.AddSeconds(i*.08)},s,"Airbus"));}Assert(!alerts.Any(c=>c.Group=="Warnings"));Assert(alerts.Count(c=>c.Id=="altitude-50")==1);Assert(alerts.Count(c=>c.Id=="minimums")==1);});
        Test("V1 prediction is bounded and never repeated",()=>{var e=new WarningEngine();var s=new Settings{SpeedsArmed=true,CalloutLeadMilliseconds=80};var t=DateTime.UtcNow;var calls=new List<(double Ias,SoundCue Cue)>();for(int i=0;i<640;i++){double speed=i*.24;foreach(var cue in e.Update(Frame(5,0,speed,true) with{Time=t.AddSeconds(i*.08)},s,"Boeing"))calls.Add((speed,cue));}var v1=calls.Where(x=>x.Cue.Id=="v1").ToList();Assert(v1.Count==1&&v1[0].Ias>=134.5&&v1[0].Ias<=135.1);Assert(calls.Count(x=>x.Cue.Id=="rotate")==1);});
        Test("Fragmented TCP server: altitude feet, speed SI, read-only requests", () => ProtocolTest().GetAwaiter().GetResult());
        Test("Connect 500-ft AGL stays 500 ft, not 1640 ft", () => ProtocolTest(500f,1500f).GetAwaiter().GetResult());
        Test("Connect 50-ft AGL stays 50 ft near touchdown", () => ProtocolTest(50f,1050f).GetAwaiter().GetResult());
        PhaseTests.Run(Test);FeatureTests.Run(Test);CabinTests.Run(Test);GpwsContextTests.Run(Test);ForwardTerrainTests.Run(Test);
        results.Insert(0, $"FlightGuard self-tests: {passed} passed, {failed} failed · {DateTime.UtcNow:O}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);File.WriteAllLines(output,results);
        return failed==0?0:1;
    }
    private static byte[] StringBytes(string s) { byte[] b=Encoding.UTF8.GetBytes(s), result=new byte[b.Length+4];BinaryPrimitives.WriteInt32LittleEndian(result,b.Length);b.CopyTo(result,4);return result; }
    private static async Task ProtocolTest(float agl = 100f, float msl = 200f)
    {
        using var timeout = new CancellationTokenSource(12000);
        var server = new TcpListener(IPAddress.Loopback,10112);server.Start();
        var states = new (string Path,int Type,object Value)[] {
            ("aircraft/0/name",4,"Boeing 777"),("infiniteflight/app_state",4,"Playing"),
            ("aircraft/0/latitude",3,47.4d),("aircraft/0/longitude",3,-122.3d),
            ("aircraft/0/altitude_agl",2,agl),("aircraft/0/altitude_msl",2,msl),("aircraft/0/indicated_airspeed",2,100f),
            ("aircraft/0/vertical_speed",2,-5f),("aircraft/0/bank",2,.1f),("aircraft/0/is_on_ground",0,false),
            ("aircraft/0/groundspeed",2,100f),("aircraft/0/systems/engines/engine_count",1,2),
            ("aircraft/0/systems/engines/0/reverse",0,true),("aircraft/0/systems/engines/1/reverse",0,false),
            ("aircraft/0/systems/spoilers/position",2,1f),
            ("aircraft/0/configuration/flaps/0/name",4,"UP"),("aircraft/0/configuration/flaps/0/angle",2,0f),
            ("aircraft/0/configuration/flaps/1/name",4,"FULL"),("aircraft/0/configuration/flaps/1/angle",2,40f)
        };
        string manifest=string.Join("\n",states.Select((s,i)=>$"{700+i},{s.Type},{s.Path}"));
        var serverTask=Task.Run(async()=>{
            using var client=await server.AcceptTcpClientAsync(timeout.Token); using var ns=client.GetStream();
            for(int i=0;i<states.Length+1;i++) {
                byte[] req=new byte[5];await ns.ReadExactlyAsync(req,timeout.Token);if(req[4]!=0)throw new Exception("Client sent a write command");
                int id=BinaryPrimitives.ReadInt32LittleEndian(req); byte[] body;
                if(id==-1)body=StringBytes(manifest);
                else {var st=states[id-700];body=st.Type==4?StringBytes((string)st.Value):st.Type==0?new[]{(byte)((bool)st.Value?1:0)}:st.Type==1?BitConverter.GetBytes((int)st.Value):st.Type==3?BitConverter.GetBytes((double)st.Value):BitConverter.GetBytes((float)st.Value);}
                byte[] header=new byte[8];BinaryPrimitives.WriteInt32LittleEndian(header,id);BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4),body.Length);
                // Arbitrary fragmentation tests that neither headers nor strings assume one network read.
                foreach(byte b in header.Concat(body))await ns.WriteAsync(new[]{b},timeout.Token);
            }
        },timeout.Token);
        try {
            using var client=new ConnectClient();await client.Open("127.0.0.1",timeout.Token);
            Frame? received=null;using var polling=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            try { await client.Poll(f=>{received=f;polling.Cancel();},_=>{},polling.Token); } catch(OperationCanceledException) when(received!=null){}
            await serverTask;
            if(received==null||received.Latitude!=47.4d||received.Longitude!=-122.3d||Math.Abs(received.Agl-agl)>.001||Math.Abs(received.Msl-msl)>.001||Math.Abs(received.Ias-194.3844492)>.001||Math.Abs(received.Vs+984.2519685)>.001||Math.Abs(received.Bank-5.72957795)>.001||Math.Abs((received.GroundSpeedKnots??0)-194.3844492)>.001||received.EngineCount!=2||received.EngineReverse[0]!=true||received.EngineReverse[1]!=false||received.EngineReverse[2]!=null||received.SpoilerPosition!=1||received.FlapStops.Length!=2||received.FlapStops[1].Name!="FULL"||received.FlapStops[1].Angle!=40)throw new Exception("Telemetry conversion failed");
        } finally {server.Stop();}
    }
}
