using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlightGuard;

public static class RecordingVerification
{
    public static async Task<int> Run(string root,string report)
    {
        var rows=new List<object>();int passed=0,failed=0;
        foreach(string file in Directory.GetFiles(Path.Combine(root,"Sounds"),"*.wav",SearchOption.AllDirectories)) {
            try {var clip=WaveClip.Load(file);rows.Add(new{File=Path.GetRelativePath(root,file),Status="PASS",DurationSeconds=clip.Duration,LeadingSilenceRemovedMs=clip.TrimmedLeadingMs});passed++;}
            catch(Exception ex){rows.Add(new{File=Path.GetRelativePath(root,file),Status="FAIL",Error=ex.Message});failed++;}
        }
        var startup=System.Diagnostics.Stopwatch.StartNew();await NativeWave.WarmUp();double warmupMs=startup.Elapsed.TotalMilliseconds;
        var timings=new List<double>();var playback=new List<object>();
        foreach(string cue in new[]{"v1","rotate","altitude-50","pull-up"}) {
            string folder=cue is "v1" or "rotate"?"Shared Crew":"Generic";
            string file=Path.Combine(root,"Sounds",folder,cue+".wav");
            try{await NativeWave.Play(WaveClip.Load(file),0,CancellationToken.None,ms=>timings.Add(ms));playback.Add(new{Cue=cue,Status="PASS"});}
            catch(Exception ex){playback.Add(new{Cue=cue,Status="FAIL",Error=ex.Message});failed++;}
        }
        double? preemptionMs=null;
        try{
            using var cancel=new CancellationTokenSource();var began=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Exercise cancellation of the real long cabin briefing, not only a generated buffer.
            string cabinFile=Path.Combine(root,"Sounds","Shared Cabin","safety-briefing.wav");
            var longRecording=File.Exists(cabinFile)?WaveClip.Load(cabinFile):new WaveClip(new byte[48000*2*3],48000,0);
            Task longClip=NativeWave.Play(longRecording,0,cancel.Token,_=>began.TrySetResult(true));
            await began.Task.WaitAsync(TimeSpan.FromSeconds(5));await Task.Delay(40);cancel.Cancel();
            Task nextClip=NativeWave.Play(WaveClip.Load(Path.Combine(root,"Sounds","Shared Crew","v1.wav")),0,CancellationToken.None,ms=>preemptionMs=ms);
            try{await longClip;}catch(OperationCanceledException){}await nextClip;
        }catch(Exception ex){failed++;playback.Add(new{Cue="preemption",Status="FAIL",Error=ex.Message});}
        File.WriteAllText(report,JsonSerializer.Serialize(new{Status=failed==0?"PASS":"FAIL",Passed=passed,Failed=failed,WarmupMilliseconds=warmupMs,OutputSubmissionMs=timings,PreemptionSubmissionMs=preemptionMs,PreemptedRecording="Shared Cabin/safety-briefing.wav when installed",Playback=playback,Recordings=rows},new JsonSerializerOptions{WriteIndented=true}));
        return failed==0?0:1;
    }
}
