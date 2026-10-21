using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace FlightGuard;
public static class LiveVerification
{
    public static async Task<int> Run(string host, string output)
    {
        var frames = new List<Frame>(); var info = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var client = new ConnectClient();
        try {
            await client.Open(host,stop.Token);
            try { await client.Poll(frame => { frames.Add(frame); if(frames.Count>=12)stop.Cancel(); },info.Add,stop.Token,50); }
            catch(OperationCanceledException) when(frames.Count>=12) { }
            if(frames.Count<12)throw new IOException("Not enough telemetry frames.");
            double average=frames.Skip(1).Select((f,i)=>(f.Time-frames[i].Time).TotalMilliseconds).Average();
            string json=JsonSerializer.Serialize(new { Status="PASS", Host=host, Aircraft=frames[0].Aircraft, Family=Catalog.Family(frames[0].Aircraft), ManifestStates=client.Manifest.Count, Samples=frames.Count, AverageIntervalMs=average, Info=info, Frames=frames },new JsonSerializerOptions{WriteIndented=true});
            File.WriteAllText(output,json);return 0;
        }catch(Exception ex){File.WriteAllText(output,JsonSerializer.Serialize(new{Status="FAIL",Error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
    }
}
