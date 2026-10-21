using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace FlightGuard;

public static class FeatureTests
{
    private static void Check(bool condition){if(!condition)throw new Exception("Feature regression");}
    private static void Reject(Action action){try{action();}catch{return;}throw new Exception("Invalid data accepted");}
    private static string Temp(){string p=Path.Combine(Path.GetTempPath(),"FlightGuard-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);return p;}
    private static Frame F(double h=500,double vs=-700,bool ground=false,double speed=140)=>new(){Aircraft="757-200",Agl=h,Msl=h+100,Vs=vs,Ias=speed,Ground=ground,GearDown=true,Flaps=2,GroundSpeedKnots=speed};
    public static void Run(Action<string,Action> test)
    {
        test("Aircraft settings remain separate and always load speeds disarmed",()=>{
            var store=new AircraftPreferences(Temp());var a=new Settings{Minimums=450,V1=150,Vr=155,V2=160,SpeedsArmed=true};var b=new Settings{Minimums=200};store.Save("757-200",a);store.Save("A320",b);
            var global=new Settings{Host="192.168.1.2",Volume=25};var loaded=store.Load("757-200",new(),global);Check(loaded.Minimums==450&&loaded.V1==150&&!loaded.SpeedsArmed&&loaded.Volume==25&&loaded.Host==global.Host);Check(store.Load("A320",new(),global).Minimums==200);Check(store.Load("New aircraft",new(),global).Minimums==200);
        });
        test("Aircraft names cannot escape the profile folder",()=>{string root=Temp();var store=new AircraftPreferences(root);store.Save("../../plane",new());Check(Directory.GetFiles(root).Length==1);});
        test("Untrusted recording settings cannot select paths outside known sound profiles",()=>Reject(()=>new Settings{Profile="../../outside"}.Validate()));
        test("Settings snapshots do not share cue lists or approach options",()=>{var original=new Settings();var copy=Settings.Copy(original);copy.DisabledSounds.Add("v1");copy.Approach.TargetKnots=145;Check(original.DisabledSounds.Count==0&&original.Approach.TargetKnots==0);});
        test("Unstable approach requires sustained criteria violation",()=>{var m=new ApproachMonitor();var t=DateTime.UtcNow;var o=new ApproachOptions();Check(m.Update(F() with{Time=t,Bank=20},o,true).State=="Checking");Check(m.Update(F() with{Time=t.AddSeconds(2.1),Bank=20},o,true).State=="Unstable");});
        test("A brief approach spike clears without an unstable alert",()=>{var m=new ApproachMonitor();var t=DateTime.UtcNow;var o=new ApproachOptions();m.Update(F() with{Time=t,Bank=20},o,true);m.Update(F() with{Time=t.AddSeconds(.3)},o,true);Check(m.Update(F() with{Time=t.AddSeconds(2.4)},o,true).State=="Stable");});
        test("Unavailable gear telemetry cannot produce stable status",()=>{var m=new ApproachMonitor();var t=DateTime.UtcNow;var o=new ApproachOptions();m.Update(F() with{Time=t,GearDown=null},o,true);var a=m.Update(F() with{Time=t.AddSeconds(2.2),GearDown=null},o,true);Check(a.State=="Incomplete"&&a.Missing.Length==1);});
        test("Approach speed checks require an explicit target",()=>{var m=new ApproachMonitor();var t=DateTime.UtcNow;var o=new ApproachOptions();m.Update(F(speed:280) with{Time=t},o,true);Check(m.Update(F(speed:280) with{Time=t.AddSeconds(2.2)},o,true).State=="Stable");o.TargetKnots=140;Check(m.Update(F(speed:280) with{Time=t.AddSeconds(2.4)},o,true).Issues.Contains("Outside target speed band"));});
        test("Approach monitoring is inhibited on go-around and above gate",()=>{var m=new ApproachMonitor();Check(m.Update(F(),new(),false).State=="Inactive");Check(m.Update(F(1500),new(),true).State=="Inactive");});
        test("Invalid approach options and optional NaNs are rejected",()=>{Reject(()=>new ApproachOptions{SinkFpm=double.NaN}.Validate());Check(!(F() with{GroundSpeedKnots=double.NaN}).Usable);});
        test("Touchdown rate uses airborne sample instead of zeroed ground rate",()=>{
            var l=new LandingAnalyzer();var t=DateTime.UtcNow;LandingResult? result=null;l.Completed=r=>result=r;var a=new ApproachAssessment("Stable",Array.Empty<string>(),Array.Empty<string>(),t);
            l.Update(F(10,-850) with{Time=t},a,"");l.Update(F(0,0,true) with{Time=t.AddSeconds(.2)},a,"");for(int i=1;i<14;i++)l.Update(F(0,0,true,20) with{Time=t.AddSeconds(.2+i*.2)},a,"");Check(result?.EstimatedTouchdownFpm==-850);
        });
        test("Bounce detection counts a short second ground contact",()=>{
            var l=new LandingAnalyzer();var t=DateTime.UtcNow;LandingResult? result=null;l.Completed=r=>result=r;var a=new ApproachAssessment("Stable",Array.Empty<string>(),Array.Empty<string>(),t);
            var frames=new[]{F(10),F(0,0,true),F(8,100),F(0,0,true),F(0,0,true,20)};
            for(int i=0;i<frames.Length;i++)l.Update(frames[i] with{Time=t.AddSeconds(i*.2)},a,"");for(int i=5;i<20;i++)l.Update(F(0,0,true,20) with{Time=t.AddSeconds(i*.2)},a,"");Check(result?.EstimatedBounces==1);
        });
        test("Landing reports do not invent touchdown across telemetry gaps",()=>{
            var l=new LandingAnalyzer();var t=DateTime.UtcNow;bool emitted=false;l.Completed=_=>emitted=true;var a=new ApproachAssessment("Stable",Array.Empty<string>(),Array.Empty<string>(),t);
            l.Update(F(10) with{Time=t},a,"");l.Update(F(0,0,true,20) with{Time=t.AddSeconds(5)},a,"");l.End();Check(!emitted);
        });
        test("Late last-airborne sample leaves touchdown-rate estimate unavailable",()=>{
            var l=new LandingAnalyzer();var t=DateTime.UtcNow;LandingResult? result=null;l.Completed=r=>result=r;var a=new ApproachAssessment("Stable",Array.Empty<string>(),Array.Empty<string>(),t);
            l.Update(F(10) with{Time=t},a,"");l.Update(F(0,0,true) with{Time=t.AddSeconds(1)},a,"");l.End();Check(result!=null&&result.EstimatedTouchdownFpm==null);
        });
        test("Landing report HTML escapes aircraft names",()=>{var r=new LandingResult("<plane>",DateTime.UtcNow,-600,120,2,0,"Landed",null,null,new[]{"pull-up"},"Estimate");string file=LandingAnalyzer.Save(r,Temp());Check(File.ReadAllText(file).Contains("&lt;plane&gt;"));});
        test("Rollout decel and deployed-spoiler calls fire once",()=>{
            var m=new ExtraCrewMonitor();var t=DateTime.UtcNow;var cues=new List<SoundCue>();for(int i=0;i<15;i++)cues.AddRange(m.Update(F(0,0,true,150-i*2) with{Time=t.AddSeconds(i*.2),SpoilerPosition=1},"Landing roll",true));Check(cues.Count(c=>c.Id=="spoilers-deployed")==1&&cues.Count(c=>c.Id=="deceleration")==1);
        });
        test("Armed spoilers and missing ground speed cannot invent crew calls",()=>{var m=new ExtraCrewMonitor();var t=DateTime.UtcNow;var cues=new List<SoundCue>();for(int i=0;i<15;i++)cues.AddRange(m.Update(F(0,0,true,150-i*2) with{Time=t.AddSeconds(i*.2),Spoilers=2,SpoilerPosition=0,GroundSpeedKnots=null},"Landing roll",true));Check(cues.Count==0);});
        test("Reverse confirmation requires all known engines and never announces green",()=>{
            var m=new ExtraCrewMonitor();var t=DateTime.UtcNow;m.Update(F(0,0,true) with{Time=t},"Landing roll",true);var cues=m.Update(F(0,0,true) with{Time=t.AddSeconds(.2),EngineCount=2,EngineReverse=new bool?[]{true,null}},"Landing roll",true);Check(!m.Status.Contains("reverse thrust confirmed"));cues.AddRange(m.Update(F(0,0,true) with{Time=t.AddSeconds(.4),EngineCount=2,EngineReverse=new bool?[]{true,true}},"Landing roll",true));Check(m.Status.Contains("reverse thrust confirmed")&&!cues.Any(c=>c.Id=="reverse-green"));
        });
        test("Recording flushes frames, settings and audio timing before replay",()=>RecordTest().GetAwaiter().GetResult());
        test("Recorder disk failure does not throw into GPWS processing",()=>{string path=Path.Combine(Temp(),"blocked");File.WriteAllText(path,"file");var r=new FlightRecorder();r.Start(path,"757-200",new()).GetAwaiter().GetResult();Check(!r.IsRecording&&r.Error!=null);});
        test("Sound catalogs reject unsafe URLs, paths and audio-overwriting notices",()=>{byte[] data=Wave(1);var p=Pack(data);Reject(()=>SoundPackManager.Validate(p with{Profile="../bad"}));Reject(()=>SoundPackManager.Validate(p with{Assets=new[]{p.Assets[0] with{Url="http://example.com/file.wav"}}}));Reject(()=>SoundPackManager.Validate(p with{Notices=new[]{new PackNotice("v1.wav","bad")}}));});
        test("Sound pack hash failure leaves current recordings untouched",()=>PackFailure().GetAwaiter().GetResult());
        test("Sound pack repair installs verified originals with a recoverable backup",()=>PackRepair().GetAwaiter().GetResult());
        test("Manual warning previews survive live warning synchronization",()=>{var q=new CueQueue();var t=DateTime.UtcNow;q.Offer(new[]{Catalog.Get("pull-up")},t,null,t,"Boeing 757");q.WarningState("");Check(q.Count==1);});
        test("Old recordings can replay deterministically without stale-data rejection",()=>{var e=new WarningEngine();var s=new Settings{CalloutLeadMilliseconds=0};var t=DateTime.UnixEpoch;e.Update(F(510) with{Time=t},s,"Boeing",false);Check(e.Update(F(499) with{Time=t.AddSeconds(.2)},s,"Boeing",false).Any(c=>c.Id=="altitude-500"));});
    }
    private static async Task RecordTest() {
        string root=Temp();var recorder=new FlightRecorder();var t=DateTime.UnixEpoch;await recorder.Start(root,"757-200",new());
        recorder.Record(new(){Kind="frame",Frame=F(510) with{Time=t},Time=t});recorder.Record(new(){Kind="settings",Settings=new Settings{Minimums=450}});
        recorder.Record(new(){Kind="audio",Playback=new("v1","Test",17,3,20,t),Time=t});recorder.Record(new(){Kind="frame",Frame=F(499) with{Time=t.AddSeconds(.2)},Time=t.AddSeconds(.2)});await recorder.Stop();
        string path=recorder.CurrentPath!;var events=FlightRecorder.Read(path);Check(events.Count(e=>e.Kind=="frame")==2&&events.Any(e=>e.Kind=="end")&&FlightRecorder.Summary(events).Contains("17 ms"));
        string report=Path.Combine(root,"analysis.json");Check(FlightRecorder.Analyze(path,report)==0);Check(JsonDocument.Parse(File.ReadAllText(report)).RootElement.GetProperty("Frames").GetInt32()==2);
        File.AppendAllText(path,JsonSerializer.Serialize(new FlightEvent{Kind="frame",Frame=F() with{Time=t}})+Environment.NewLine);Reject(()=>FlightRecorder.Read(path));
    }
    private static SoundPack Pack(byte[] data)=>new("Generic","test",new[]{new PackAsset("v1","https://raw.githubusercontent.com/test/test/"+new string('a',40)+"/v1.wav",Convert.ToHexString(SHA256.HashData(data)),data.Length,"Test fixture",new string('a',40))},new[]{new PackNotice("ATTRIBUTION.md","Synthetic tone used only by automated tests; not an aircraft recording.")},"Test fixture");
    private static async Task PackFailure(){string root=Temp();string dir=Path.Combine(root,"Sounds","Generic");Directory.CreateDirectory(dir);byte[] old=Wave(1),fresh=Wave(2);string path=Path.Combine(dir,"v1.wav");File.WriteAllBytes(path,old);try{await SoundPackManager.Install(Pack(fresh),root,fetch:(_,_)=>Task.FromResult(old));}catch(InvalidDataException){Check(File.ReadAllBytes(path).SequenceEqual(old));return;}throw new Exception("Incorrect hash accepted");}
    private static async Task PackRepair(){string root=Temp();string dir=Path.Combine(root,"Sounds","Generic");Directory.CreateDirectory(dir);byte[] old=Wave(1),fresh=Wave(2);File.WriteAllBytes(Path.Combine(dir,"v1.wav"),old);File.WriteAllText(Path.Combine(dir,"custom.txt"),"keep");var result=await SoundPackManager.Install(Pack(fresh),root,fetch:(_,_)=>Task.FromResult(fresh));Check(result.Downloaded==1&&result.Backup!=null&&File.ReadAllBytes(Path.Combine(result.Backup,"v1.wav")).SequenceEqual(old)&&File.Exists(Path.Combine(dir,"custom.txt")));var second=await SoundPackManager.Install(Pack(fresh),root,fetch:(_,_)=>throw new Exception("Already-valid recording downloaded again"));Check(second.Downloaded==0);}
    private static byte[] Wave(int multiplier){using var stream=new MemoryStream();using(var w=new BinaryWriter(stream,System.Text.Encoding.UTF8,true)){int samples=960;w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));w.Write(36+samples*2);w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(48000);w.Write(96000);w.Write((short)2);w.Write((short)16);w.Write(System.Text.Encoding.ASCII.GetBytes("data"));w.Write(samples*2);for(int i=0;i<samples;i++)w.Write((short)(Math.Sin(i*.14)*8000*multiplier));}return stream.ToArray();}
    public static void WriteDemo(string output){var engine=new WarningEngine();var s=new Settings{SpeedsArmed=true};var t=DateTime.UtcNow;int i=0;using var writer=new StreamWriter(output);writer.WriteLine(JsonSerializer.Serialize(new FlightEvent{Kind="session",Settings=s,Note="Synthetic telemetry example; no real flight or audio-start measurements."}));foreach(var frame in MainWindow.DemoFrames()){var f=frame with{Time=t.AddSeconds(i++*.2),GroundSpeedKnots=frame.Ias,EngineCount=2,EngineReverse=new bool?[]{false,false}};var cues=engine.Update(f,s,"Airbus A320 family",false);writer.WriteLine(JsonSerializer.Serialize(new FlightEvent{Kind="frame",Time=f.Time,Frame=f,Phase=engine.Phase,Cues=cues.Select(c=>c.Id).ToArray()}));}writer.WriteLine(JsonSerializer.Serialize(new FlightEvent{Kind="end"}));}
}
