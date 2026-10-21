using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace FlightGuard;
public static class AudioPriorityVerification
{
    public static int Run(string output)
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int code=1;
        app.Startup+=async(_,_)=>{
            Audio? audio=null;
            try{
                var settings=new Settings{Volume=0};audio=new(app.Dispatcher,settings);audio.Configure(settings,"Boeing 757");audio.CabinAllowed=_=>true;audio.CabinPreviewAllowed=()=>false;
                var starts=new List<string>();var notices=new List<string>();var cabin=new TaskCompletionSource<bool>();var warning=new TaskCompletionSource<bool>();
                audio.Notice=notices.Add;audio.Started=(cue,source)=>{starts.Add(cue.Id);if(cue.Id=="safety-briefing")cabin.TrySetResult(true);if(cue.Id=="pull-up")warning.TrySetResult(true);};
                audio.Enqueue(new[]{Catalog.Get("safety-briefing")});await cabin.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var timer=Stopwatch.StartNew();audio.SynchronizeWarning("pull-up");audio.SuppressLowerThan(70);audio.Enqueue(new[]{Catalog.Get("pull-up")});await warning.Task.WaitAsync(TimeSpan.FromSeconds(5));double delay=timer.Elapsed.TotalMilliseconds;
                audio.StopCabin();if(audio.ActiveCue!="pull-up")throw new Exception("Cabin stop interrupted GPWS.");
                audio.Preview(Catalog.Get("boarding-welcome"),"Boeing 757");if(audio.ActiveCue!="pull-up"||starts.Count!=2)throw new Exception("Cabin preview interrupted GPWS.");
                if(delay>250)throw new Exception("Warning preemption exceeded 250 ms: "+delay);
                File.WriteAllText(output,JsonSerializer.Serialize(new{Status="PASS",SafetyBriefingToGpwsMilliseconds=delay,CabinStopPreservesGpws=true,PreviewPreservesGpws=true,Played=starts,Notices=notices,Volume=0},new JsonSerializerOptions{WriteIndented=true}));code=0;
            }catch(Exception ex){File.WriteAllText(output,JsonSerializer.Serialize(new{Status="FAIL",Error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));}
            finally{audio?.Dispose();app.Shutdown(code);}
        };
        app.Run();return code;
    }
}
