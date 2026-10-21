using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FlightGuard;

public sealed class Audio : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly CueQueue queue=new();
    private readonly Dictionary<string,WaveClip> cache=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> invalid=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,string?> resolved=new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? playing;
    private Task? playback;
    private SoundCue? current;
    private bool previewing;
    private DateTime began;
    private readonly DispatcherTimer tick;
    private string family="Generic";
    private Settings settings;
    private bool disposed;
    public bool Muted {get;set;}
    internal string? ActiveCue=>playing is {IsCancellationRequested:false} && playback is {IsCompleted:false}?current?.Id:null;
    public Action<string>? Notice;
    public Action<SoundCue,string>? Started;
    public Action<PlaybackStamp>? PlaybackRecorded;
    public Func<string,bool>? CabinAllowed;
    public Func<bool>? CabinPreviewAllowed;
    private string warningId="";
    private DateTime warningSeen;
    public static string SoundsFolder=>Path.Combine(AppContext.BaseDirectory,"Sounds");
    public Audio(Dispatcher dispatcher,Settings settings)
    {
        this.dispatcher=dispatcher;this.settings=settings;
        _=NativeWave.WarmUp(); Reload();
        tick=new DispatcherTimer(TimeSpan.FromMilliseconds(10),DispatcherPriority.Send,(_,_)=>Pump(),dispatcher);tick.Start();
    }
    public void Reload(){cache.Clear();invalid.Clear();resolved.Clear();if(Directory.Exists(SoundsFolder))foreach(string file in Directory.GetFiles(SoundsFolder,"*.wav",SearchOption.AllDirectories)){try{cache[file]=WaveClip.Load(file);}catch{invalid.Add(file);}}}
    public void Configure(Settings s,string f){settings=s;family=f;resolved.Clear();}
    public string? FileFor(string id,string? f=null)
    {
        string key=(f==null?"Live:":"Preview:")+(f??family)+":"+id;if(resolved.TryGetValue(key,out var found))return found;
        foreach(string folder in VoiceRouting.Folders(settings,f??family,Catalog.Get(id),f!=null)){
            string path=Path.Combine(SoundsFolder,folder,id+".wav");if(File.Exists(path)&&!invalid.Contains(path)){resolved[key]=path;return path;}
        }
        resolved[key]=null;
        return null;
    }
    public string SourceFor(string id,string? f=null){string? file=FileFor(id,f);if(file==null)return "Missing recording Â· silent";string folder=Path.GetFileName(Path.GetDirectoryName(file)) ?? "Unknown";return (folder==(f??family)?"":"Shared: ")+folder+" recording";}
    public void Enqueue(IEnumerable<SoundCue> cues)
    {
        if(!dispatcher.CheckAccess()){var array=cues.ToArray();dispatcher.BeginInvoke(()=>Enqueue(array));return;}
        if(Muted||disposed)return;
        var available=new List<SoundCue>();
        foreach(var cue in cues){if(settings.DisabledSounds.Contains(cue.Id))continue;if(FileFor(cue.Id)==null){Notice?.Invoke("NO RECORDING  "+cue.Id+" Â· visual indication only");continue;}available.Add(cue);}
        if(queue.Offer(available,DateTime.UtcNow,current,began))playing?.Cancel();Pump();
    }
    public void Preview(SoundCue cue,string? selectedFamily=null){if(warningId!=""&&(DateTime.UtcNow-warningSeen).TotalMilliseconds<300||current!=null&&CueQueue.Warning(current)&&!previewing){Notice?.Invoke("Live warning has priority over previews.");return;}if(CueQueue.Cabin(cue)&&CabinPreviewAllowed?.Invoke()==false){Notice?.Invoke("Cabin preview is inhibited in this flight phase.");return;}Stop();if(Muted)return;if(FileFor(cue.Id,selectedFamily)==null){Notice?.Invoke("No recording for "+cue.Id+". Speech synthesis is disabled.");return;}queue.Offer(new[]{cue},DateTime.UtcNow,null,default,selectedFamily??family);Pump();}
    public void SuppressLowerThan(int priority){queue.Suppress(priority);if(current!=null&&current.Priority<priority)playing?.Cancel();}
    public void SynchronizeWarning(string active) { if(active!=""){warningId=active;warningSeen=DateTime.UtcNow;}else if((DateTime.UtcNow-warningSeen).TotalMilliseconds<180)active=warningId;queue.WarningState(active);if(!previewing && current?.Group=="Warnings" && current.Id!=active)playing?.Cancel(); }
    public void SynchronizeCabin(){if(CabinAllowed==null)return;queue.CancelCabin(CabinAllowed);if(!previewing&&current!=null&&CueQueue.Cabin(current)&&!CabinAllowed(current.Id))playing?.Cancel();}
    public void StopCabin(){queue.CancelCabin(_=>false);if(current!=null&&CueQueue.Cabin(current))playing?.Cancel();}
    public void SynchronizeApproach(bool allowed) { if(allowed)return; queue.CancelApproach(); if(!previewing && current?.Group is "Heights" or "Approach")playing?.Cancel(); }
    public void SynchronizeRoutineCrew(bool allowed){if(allowed)return;queue.CancelRoutineCrew();if(!previewing&&current?.Group=="Crew"&&current.Priority<50)playing?.Cancel();}
    private void Pump()
    {
        if(disposed)return;
        if(Muted){Stop();return;}
        if(playback!=null&&!playback.IsCompleted)return;
        if(playback!=null){if(playback.IsFaulted)Notice?.Invoke("Recorded playback failed: "+playback.Exception?.GetBaseException().Message);playback=null;playing?.Dispose();playing=null;current=null;}
        var item=queue.Next(DateTime.UtcNow);if(item==null)return;
        string? file=FileFor(item.Cue.Id,item.Family);if(file==null)return;
        try{
            if(!cache.TryGetValue(file,out var clip)){clip=WaveClip.Load(file);cache[file]=clip;}
            current=item.Cue;previewing=item.Family!=null;began=DateTime.UtcNow;playing=new();double waiting=(began-item.Created).TotalMilliseconds;
            int volume=VoiceRouting.Volume(settings,item.Cue);
            playback=NativeWave.Play(clip,volume,playing.Token,openMs=>{var submittedAt=DateTime.UtcNow;dispatcher.BeginInvoke(()=>{
                if(disposed)return;Started?.Invoke(item.Cue,SourceFor(item.Cue.Id,item.Family));
                PlaybackRecorded?.Invoke(new(item.Cue.Id,SourceFor(item.Cue.Id,item.Family),waiting,openMs,clip.TrimmedLeadingMs,submittedAt));
                Notice?.Invoke($"TIMING  {item.Cue.Id} Â· queue {waiting:0} ms Â· output submit {openMs:0} ms Â· trimmed {clip.TrimmedLeadingMs:0} ms");
            });});
            _=playback.ContinueWith(_=>{if(!disposed)dispatcher.BeginInvoke(DispatcherPriority.Send,new Action(Pump));},TaskScheduler.Default);
        }catch(Exception ex){invalid.Add(file);current=null;Notice?.Invoke("Invalid recording skipped: "+item.Cue.Id+" Â· "+ex.Message);}
    }
    public void Stop(){queue.Clear();playing?.Cancel();warningId="";warningSeen=default;}
    public void Dispose(){disposed=true;tick.Stop();Stop();_=NativeWave.Shutdown();}

    public static int ImportFolder(string input,string family)=>PersonalVoicePacks.Import(input,family,AppContext.BaseDirectory);
}
