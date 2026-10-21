using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FlightGuard;

public sealed record PlaybackStamp(string Cue,string Source,double QueueMilliseconds,double SubmitMilliseconds,double TrimmedMilliseconds,DateTime Time);
public sealed record FlightEvent
{
    public int Version {get;init;}=1;
    public string Kind {get;init;}="frame";
    public DateTime Time {get;init;}=DateTime.UtcNow;
    public string? Aircraft {get;init;}
    public Frame? Frame {get;init;}
    public Settings? Settings {get;init;}
    public string? Phase {get;init;}
    public string? CabinStage {get;init;}
    public string? Warning {get;init;}
    public string[]? Cues {get;init;}
    public ApproachAssessment? Approach {get;init;}
    public PlaybackStamp? Playback {get;init;}
    public string? Note {get;init;}
}

public sealed class FlightRecorder
{
    public static readonly JsonSerializerOptions JsonOptions=new(){DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull};
    private Channel<FlightEvent>? channel;
    private Task? writer;
    private readonly List<Task> writers=new();
    public bool IsRecording=>channel!=null;
    public string? CurrentPath {get;private set;}
    public string? Error {get;private set;}
    public int FrameCount {get;private set;}
    public Task Start(string folder,string aircraft,Settings settings) {
        if(IsRecording)throw new InvalidOperationException("A recording is already running.");
        Error=null;FrameCount=0;try{Directory.CreateDirectory(folder);}catch(Exception ex){Error=ex.Message;return Task.CompletedTask;}
        CurrentPath=Path.Combine(folder,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]+".fgflight.jsonl");
        var queue=Channel.CreateBounded<FlightEvent>(new BoundedChannelOptions(4096){SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});channel=queue;
        string path=CurrentPath;
        writer=Task.Run(async()=>{
            try{
                await using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,true);
                await using var text=new StreamWriter(stream);
                await foreach(var e in queue.Reader.ReadAllAsync())await text.WriteLineAsync(JsonSerializer.Serialize(e,JsonOptions));
                await text.FlushAsync();
            }catch(Exception ex){Error=ex.Message;queue.Writer.TryComplete();if(ReferenceEquals(channel,queue))channel=null;}
        });
        writers.Add(writer);
        Record(new(){Kind="session",Aircraft=aircraft,Settings=Settings.Copy(settings),Note="FlightGuard 0.5; UTC timestamps; frame units feet/knots/fpm/degrees; local recording"});
        return Task.CompletedTask;
    }
    public void Record(FlightEvent e) {
        var queue=channel;if(queue==null)return;
        if(queue.Writer.TryWrite(e)){if(e.Kind=="frame")FrameCount++;}
        else{Error="Recording buffer full; recording ended incomplete.";queue.Writer.TryComplete();channel=null;}
    }
    public async Task Stop(string reason="Stopped") {
        var queue=channel;channel=null;
        if(queue!=null){queue.Writer.TryWrite(new(){Kind="end",Note=reason});queue.Writer.TryComplete();}
        await Task.WhenAll(writers.ToArray());writers.RemoveAll(t=>t.IsCompleted);
    }
    public static List<FlightEvent> Read(string path) {
        if(new FileInfo(path).Length>300*1024*1024)throw new InvalidDataException("Replay files must be at most 300 MB.");
        var events=new List<FlightEvent>();DateTime prior=default;int frames=0;
        foreach(string line in File.ReadLines(path)) {
            if(line.Length>1024*1024)throw new InvalidDataException("Oversized recording entry.");
            if(string.IsNullOrWhiteSpace(line))continue;
            var e=JsonSerializer.Deserialize<FlightEvent>(line)??throw new InvalidDataException("Invalid recording entry.");
            if(e.Version!=1)throw new InvalidDataException("Unsupported recording version.");
            if(e.Settings!=null)e.Settings.Validate();
            if(e.Kind=="frame") {
                var f=e.Frame??throw new InvalidDataException("Frame data missing.");
                if(!f.Usable||f.Time==default||prior!=default&&f.Time<=prior)throw new InvalidDataException("Invalid or unordered telemetry in recording.");
                prior=f.Time;frames++;if(frames>500000)throw new InvalidDataException("Recording exceeds 500,000 frames.");
            }
            events.Add(e);if(events.Count>1000000)throw new InvalidDataException("Too many recording events.");
        }
        if(events.Count==0||events[0].Kind!="session"||frames<2)throw new InvalidDataException("Select a FlightGuard recording containing a session header and at least two frames.");
        return events;
    }
    public static string Summary(List<FlightEvent> events) {
        var frames=events.Where(e=>e.Frame!=null).ToArray();var played=events.Where(e=>e.Playback!=null).Select(e=>e.Playback!).ToArray();
        return $"{frames.Length} frames · {(frames[^1].Frame!.Time-frames[0].Frame!.Time).TotalMinutes:0.0} min · {played.Length} audio starts\n"+
            (events.Any(e=>e.Kind=="end")?"":"Recording did not end cleanly; data may be incomplete.\n")+
            (played.Length>0?$"Maximum recorded queue delay: {played.Max(p=>p.QueueMilliseconds):0} ms\n":"")+
            string.Join("\n",events.Where(e=>e.Playback!=null).TakeLast(35).Select(e=>$"{e.Playback!.Time:HH:mm:ss.fff} UTC  {e.Playback.Cue}  queue {e.Playback.QueueMilliseconds:0} ms  submit {e.Playback.SubmitMilliseconds:0} ms"));
    }
    public static int Analyze(string input,string output) {
        var events=Read(input);var settings=Settings.Copy(events[0].Settings??new Settings());var engine=new WarningEngine();var approach=new ApproachMonitor();var landings=new LandingAnalyzer();var crew=new ExtraCrewMonitor();var cabin=new CabinExperience();
        var decisions=new List<object>();var results=new List<LandingResult>();landings.Completed=results.Add;
        foreach(var e in events) {
            if(e.Settings!=null){settings=Settings.Copy(e.Settings);}
            if(e.Kind=="cabin-command"){if(e.Note=="board")cabin.StartBoarding();else if(e.Note=="ready")cabin.ReadyForDeparture();else if(e.Note=="stop")settings.Cabin.Enabled=false;}
            if(e.Frame is not Frame f)continue;
            var cues=engine.Update(f,settings,Catalog.AircraftProfile(f.Aircraft),false);cues.AddRange(crew.Update(f,engine.Phase,settings.ExtraCrew&&settings.Crew));
            cues.AddRange(cabin.Update(f,engine.Phase,settings.Cabin,engine.ActiveWarning));
            var a=approach.Update(f,settings.Approach,engine.ApproachAudioAllowed);landings.Update(f,a,engine.ActiveWarning);
            decisions.Add(new{f.Time,Phase=engine.Phase,CabinStage=cabin.Stage,Warning=engine.ActiveWarning,Cues=cues.Select(c=>c.Id),Approach=a});
        }
        landings.End("Replay ended");
        File.WriteAllText(output,JsonSerializer.Serialize(new{Status="PASS",Frames=decisions.Count,Summary=Summary(events),Landings=results,Decisions=decisions},new JsonSerializerOptions{WriteIndented=true}));return 0;
    }
}
