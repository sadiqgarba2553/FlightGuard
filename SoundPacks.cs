using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlightGuard;

public sealed record PackAsset(string Cue,string Url,string Sha256,int Bytes,string License,string Revision);
public sealed record PackNotice(string Name,string Content);
public sealed record SoundPack(string Profile,string Version,PackAsset[] Assets,PackNotice[] Notices,string Note)
{
    public override string ToString()=>Profile+" · "+Assets.Length+" clips · "+Version;
}
public sealed record PackCatalog(int Schema,SoundPack[] Packs)
{
    public static PackCatalog Load(string path){var c=JsonSerializer.Deserialize<PackCatalog>(File.ReadAllText(path))??throw new InvalidDataException("Invalid pack catalog.");if(c.Schema!=1||c.Packs.Length>100)throw new InvalidDataException("Unsupported catalog.");foreach(var p in c.Packs)SoundPackManager.Validate(p);return c;}
}
public sealed record PackInstallResult(string Profile,int Clips,int Downloaded,string? Backup,string Status);

public static class SoundPackManager
{
    private static bool Hex(string text,int length)=>text.Length==length&&text.All(Uri.IsHexDigit);
    public static void Validate(SoundPack pack) {
        if(!Catalog.Profiles.Contains(pack.Profile)&&pack.Profile!="Shared Cabin" && pack.Profile!="Shared Crew")throw new InvalidDataException("Unknown pack profile.");
        if(pack.Assets.Length is <1 or >100 || pack.Assets.Select(a=>a.Cue).Distinct().Count()!=pack.Assets.Length)throw new InvalidDataException("Invalid pack asset list.");
        foreach(var a in pack.Assets) {
            if(!Catalog.All.Any(c=>c.Id==a.Cue)||!Hex(a.Sha256,64)||!Hex(a.Revision,40)||a.Bytes<512||a.Bytes>20*1024*1024||string.IsNullOrWhiteSpace(a.License))throw new InvalidDataException("Invalid pack asset metadata.");
            var url=new Uri(a.Url,UriKind.Absolute);
            if(url.Scheme!="https"||url.Port!=443||url.UserInfo!=""||url.Query!=""||url.Fragment!=""||!new[]{"raw.githubusercontent.com","gitlab.com"}.Contains(url.Host)||!url.AbsolutePath.Contains("/"+a.Revision+"/",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Pack assets must use pinned HTTPS upstream URLs.");
        }
        foreach(var n in pack.Notices)if(n.Name!=Path.GetFileName(n.Name)||n.Name.Contains(':')||n.Name.Length<1||!(n.Name.EndsWith(".md",StringComparison.OrdinalIgnoreCase)||n.Name.EndsWith(".txt",StringComparison.OrdinalIgnoreCase)||n.Name is "LICENSE" or "COPYING")||n.Content.Length>2000000)throw new InvalidDataException("Invalid licence notice path.");
        if(pack.Notices.Length==0)throw new InvalidDataException("Pack licence notices are missing.");
    }
    private static string Inside(string parent,string child) {
        string root=Path.GetFullPath(parent)+Path.DirectorySeparatorChar;string target=Path.GetFullPath(child);
        if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Pack path escaped its destination.");return target;
    }
    public static async Task<PackInstallResult> Install(SoundPack pack,string appRoot,IProgress<string>? progress=null,CancellationToken token=default,Func<PackAsset,CancellationToken,Task<byte[]>>? fetch=null) {
        Validate(pack);string soundRoot=Path.Combine(appRoot,"Sounds");Directory.CreateDirectory(soundRoot);
        string target=Inside(soundRoot,Path.Combine(soundRoot,pack.Profile));
        bool Valid(string path,PackAsset a)=>File.Exists(path)&&new FileInfo(path).Length==a.Bytes&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(a.Sha256,StringComparison.OrdinalIgnoreCase);
        if(pack.Assets.All(a=>Valid(Path.Combine(target,a.Cue+".wav"),a))){foreach(var a in pack.Assets)_=WaveClip.Load(Path.Combine(target,a.Cue+".wav"));return new(pack.Profile,pack.Assets.Length,0,null,"All original recordings verified");}
        string stagingRoot=Path.Combine(appRoot,"Data","PackDownloads");Directory.CreateDirectory(stagingRoot);
        string stage=Inside(stagingRoot,Path.Combine(stagingRoot,Guid.NewGuid().ToString("N")));Directory.CreateDirectory(stage);
        if(Directory.Exists(target))CopyTree(target,stage);
        using var handler=new HttpClientHandler{AllowAutoRedirect=false};using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(45),MaxResponseContentBufferSize=20*1024*1024};client.DefaultRequestHeaders.UserAgent.ParseAdd("FlightGuard/0.5");
        using var slots=new SemaphoreSlim(4);int done=0,downloaded=0;
        await Task.WhenAll(pack.Assets.Select(async a=>{
            await slots.WaitAsync(token);try{
                string old=Path.Combine(target,a.Cue+".wav");byte[] data;
                if(Valid(old,a))data=await File.ReadAllBytesAsync(old,token);
                else{data=fetch==null?await client.GetByteArrayAsync(a.Url,token):await fetch(a,token);Interlocked.Increment(ref downloaded);}
                if(data.Length!=a.Bytes||!Convert.ToHexString(SHA256.HashData(data)).Equals(a.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Source hash mismatch: "+a.Cue);
                _=WaveClip.Decode(data);await File.WriteAllBytesAsync(Path.Combine(stage,a.Cue+".wav"),data,token);
                progress?.Report($"Verified {Interlocked.Increment(ref done)} / {pack.Assets.Length}: {a.Cue}");
            }finally{slots.Release();}
        }));
        foreach(var n in pack.Notices)await File.WriteAllTextAsync(Inside(stage,Path.Combine(stage,n.Name)),n.Content,token);
        await File.WriteAllTextAsync(Path.Combine(stage,"installed-pack.json"),JsonSerializer.Serialize(pack,new JsonSerializerOptions{WriteIndented=true}),token);
        token.ThrowIfCancellationRequested();
        string? backup=null;
        if(Directory.Exists(target)) {
            string backups=Path.Combine(appRoot,"Data","PackBackups");Directory.CreateDirectory(backups);
            backup=Inside(backups,Path.Combine(backups,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]+"-"+pack.Profile));
            Directory.Move(Inside(soundRoot,target),backup);
        }
        try{Directory.Move(Inside(stagingRoot,stage),Inside(soundRoot,target));}
        catch{if(backup!=null&&Directory.Exists(backup)&&!Directory.Exists(target))Directory.Move(backup,target);throw;}
        return new(pack.Profile,pack.Assets.Length,downloaded,backup,"Installed verified recordings");
    }
    private static void CopyTree(string source,string target) {
        var pending=new Stack<string>();pending.Push(source);
        while(pending.Count>0){string dir=pending.Pop();if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked sound folders are not supported.");
            foreach(string child in Directory.GetDirectories(dir))pending.Push(child);
            foreach(string file in Directory.GetFiles(dir)){if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked sound files are not supported.");string dest=Inside(target,Path.Combine(target,Path.GetRelativePath(source,file)));Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(file,dest);}
        }
    }

    public static async Task<int> VerifyDownload(string catalogPath,string profile,string target,string report) {
        try{var pack=PackCatalog.Load(catalogPath).Packs.Single(p=>p.Profile==profile);var result=await Install(pack,target);File.WriteAllText(report,JsonSerializer.Serialize(new{Status="PASS",Result=result},new JsonSerializerOptions{WriteIndented=true}));return 0;}
        catch(Exception ex){File.WriteAllText(report,JsonSerializer.Serialize(new{Status="FAIL",Error=ex.Message}));return 1;}
    }
}
