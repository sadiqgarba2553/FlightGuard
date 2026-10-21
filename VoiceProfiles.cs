using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlightGuard;

public sealed class VoiceOptions
{
    public string GpwsVoice {get;set;}="Auto";
    public string CrewVoice {get;set;}="Auto";
    public string CabinVoice {get;set;}="Auto";
    public int GpwsVolume {get;set;}=100;
    public int CrewVolume {get;set;}=100;
    public string HeightMode {get;set;}="All existing";
    public string CustomHeights {get;set;}="2500,1000,500,400,300,200,100,50,40,30,20,10";
    public bool SuppressRoutineCrewOnFinal {get;set;}=true;
    public static readonly string[] Choices=new[]{"Auto"}.Concat(Catalog.Profiles).Concat(new[]{"Shared Crew","Shared Cabin"}).ToArray();
    public static readonly string[] HeightModes={"All existing","Suggested by aircraft","Custom"};
    public static readonly int[] Heights={2500,2000,1000,500,400,300,200,100,50,40,30,20,10,5};
    public void Validate(){
        if(!Choices.Contains(GpwsVoice)||!Choices.Contains(CrewVoice)||!Choices.Contains(CabinVoice))throw new ArgumentException("Select a listed voice profile.");
        if(GpwsVolume<0||GpwsVolume>100||CrewVolume<0||CrewVolume>100)throw new ArgumentException("Voice levels must be 0–100 percent.");
        if(!HeightModes.Contains(HeightMode))throw new ArgumentException("Select a listed height schedule.");
        _=ParseHeights(CustomHeights);
    }
    public static int[] ParseHeights(string value){
        var parts=(value??"").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length==0||parts.Length>14)throw new ArgumentException("Choose 1–14 comma-separated callout heights.");
        var result=parts.Select(x=>int.TryParse(x,out int h)&&Heights.Contains(h)?h:throw new ArgumentException("Height must be one of: "+string.Join(", ",Heights))).Distinct().OrderByDescending(x=>x).ToArray();return result;
    }
    public int[] Schedule(string aircraft)=>HeightMode=="Custom"?ParseHeights(CustomHeights):HeightMode=="Suggested by aircraft"?Suggested(aircraft):Heights.Where(x=>x!=5).ToArray();
    // These are editable simulator templates, not verified airline/OEM configurations.
    public static int[] Suggested(string aircraft){string profile=Catalog.AircraftProfile(aircraft);return profile=="Airbus A320 family"?Heights.Where(x=>x!=2000).ToArray():Heights.Where(x=>x is not 2000 and not 5).ToArray();}
}

public static class VoiceRouting
{
    public static bool Gpws(SoundCue cue)=>cue.Group is "Warnings" or "Heights" or "Approach" or "Test"||cue.Id is "windshear" or "glideslope";
    public static string Role(SoundCue cue)=>Gpws(cue)?"GPWS":CueQueue.Cabin(cue)?"Cabin":"Co-pilot";
    public static string Requested(Settings s,string aircraftProfile,SoundCue cue){string requested=Gpws(cue)?s.Voices.GpwsVoice:CueQueue.Cabin(cue)?s.Voices.CabinVoice:s.Voices.CrewVoice;return requested=="Auto"?aircraftProfile:requested;}
    public static IEnumerable<string> Folders(Settings s,string profile,SoundCue cue,bool preview=false){
        string requested=preview?profile:Requested(s,profile,cue);
        // A220 is a separate aircraft lineage; do not silently treat its automatic voice as A320.
        var folders=requested=="Airbus A220"?new[]{requested,"Generic","Shared Crew","Shared Cabin"}:Catalog.SoundFolders(requested);
        return folders.Distinct();
    }
    public static string? Resolve(string root,Settings s,string profile,SoundCue cue,bool preview=false,Func<string,bool>? valid=null)=>Folders(s,profile,cue,preview).Select(f=>Path.Combine(root,"Sounds",f,cue.Id+".wav")).FirstOrDefault(p=>File.Exists(p)&&(valid==null||valid(p)));
    public static int Volume(Settings s,SoundCue cue)=>s.Volume*(Gpws(cue)?s.Voices.GpwsVolume:CueQueue.Cabin(cue)?s.Cabin.Volume:s.Voices.CrewVolume)/100;
}

public sealed record VoicePackInfo(string Name="Personal recordings",string Author="Unknown",string License="Not supplied — personal import",string SourceUrl="",string Aircraft="",string Notes="OEM authenticity unverified");
public sealed record VoiceAsset(string Cue,string Label,string Role,string Requested,string Source,string Coverage,string License,string Origin,string Revision,string Sha256,double Duration,double LeadingSilence,string File);
public sealed class VoiceInventory
{
    private readonly string root;
    private readonly Dictionary<(string,string),JsonElement> provenance=new();
    public VoiceInventory(string root){this.root=root;string path=Path.Combine(root,"sound-provenance.json");if(File.Exists(path)){using var doc=JsonDocument.Parse(File.ReadAllText(path));foreach(var r in doc.RootElement.EnumerateArray())provenance[(r.GetProperty("family").GetString()!,r.GetProperty("cue").GetString()!)]=r.Clone();}}
    public VoiceAsset Inspect(Settings settings,string profile,SoundCue cue,bool preview=false){
        string requested=preview?profile:VoiceRouting.Requested(settings,profile,cue);string? file=VoiceRouting.Resolve(root,settings,profile,cue,preview);
        if(file==null)return new(cue.Id,cue.Text,VoiceRouting.Role(cue),requested,"Missing","Missing","Unknown","","","",0,0,"");
        string folder=Path.GetFileName(Path.GetDirectoryName(file))!;string hash=Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(file))).ToLowerInvariant();string license="Unknown",origin="",revision="";bool hashMatched=false;
        if(provenance.TryGetValue((folder,cue.Id),out var r)){
            string Get(string key)=>r.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()??"":"";
            hashMatched=hash.Equals(Get("sha256"),StringComparison.OrdinalIgnoreCase);
            if(hashMatched){license=Get("license");origin=Get("source_url");if(origin==""&&r.TryGetProperty("source_urls",out var urls))origin=string.Join("; ",urls.EnumerateArray().Select(x=>x.GetString()));revision=Get("revision");}
        }
        if(!hashMatched){string info=Path.Combine(root,"Sounds",folder,"pack-info.json");if(System.IO.File.Exists(info)){try{var pack=JsonSerializer.Deserialize<VoicePackInfo>(System.IO.File.ReadAllText(info));license=pack?.License??"Unknown";origin=pack?.SourceUrl??"";}catch{}}}
        string coverage=folder==requested?"Model pack / unverified OEM":"Shared fallback";
        if(hashMatched&&provenance.Any(p=>p.Key.Item1!=folder&&p.Value.TryGetProperty("sha256",out var h)&&h.GetString()==hash))coverage+=" · same bytes in another pack";
        try{var clip=WaveClip.Load(file);return new(cue.Id,cue.Text,VoiceRouting.Role(cue),requested,folder,coverage,license,origin,revision,hash,Math.Round(clip.Duration,3),Math.Round(clip.TrimmedLeadingMs,1),file);}
        catch(Exception ex){return new(cue.Id,cue.Text,VoiceRouting.Role(cue),requested,folder,"Invalid audio: "+ex.Message,license,origin,revision,hash,0,0,file);}
    }
    public VoiceAsset[] Scan(Settings settings,string profile,bool preview=false)=>Catalog.All.Select(c=>Inspect(settings,profile,c,preview)).ToArray();
    public static string Summary(IEnumerable<VoiceAsset> input){var rows=input.ToArray();var safety=rows.Where(x=>x.Role=="GPWS"&&!new[]{"glideslope","windshear","gpws-test"}.Contains(x.Cue)).ToArray();return $"{rows.Count(x=>x.Coverage.StartsWith("Model"))} model-pack · {rows.Count(x=>x.Coverage.StartsWith("Shared"))} shared · {rows.Count(x=>x.Coverage=="Missing")} missing · {rows.Count(x=>x.Coverage.StartsWith("Invalid"))} invalid. GPWS recorded {safety.Count(x=>x.Duration>0)}/{safety.Length}. OEM origin remains unverified.";}
    public static void Export(string path,IEnumerable<VoiceAsset> rows){string C(string s)=>"\""+s.Replace("\"","\"\"")+"\"";var lines=new List<string>{"Cue,Label,Role,Requested voice,Source,Coverage,License,Origin,Revision,SHA256,Trimmed seconds,Leading silence ms"};lines.AddRange(rows.Select(r=>string.Join(",",new[]{r.Cue,r.Label,r.Role,r.Requested,r.Source,r.Coverage,r.License,r.Origin,r.Revision,r.Sha256,r.Duration.ToString(System.Globalization.CultureInfo.InvariantCulture),r.LeadingSilence.ToString(System.Globalization.CultureInfo.InvariantCulture)}.Select(C))));System.IO.File.WriteAllLines(path,lines);}
}

public static class PersonalVoicePacks
{
    public static void ValidateProfile(string profile){if(!VoiceOptions.Choices.Contains(profile)||profile=="Auto")throw new InvalidDataException("Choose a listed destination pack.");}
    private static void NoLink(string path){if((System.IO.File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked files/folders cannot be imported.");}
    private static string Inside(string parent,string relative){string path=Path.GetFullPath(Path.Combine(parent,relative));string prefix=Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Mapping escapes the selected folder.");return path;}
    private static void Copy(string from,string to){NoLink(from);Directory.CreateDirectory(to);foreach(string p in Directory.GetFiles(from)){NoLink(p);System.IO.File.Copy(p,Path.Combine(to,Path.GetFileName(p)),true);}foreach(string p in Directory.GetDirectories(from))Copy(p,Path.Combine(to,Path.GetFileName(p)));}
    public static int Import(string input,string profile,string appRoot){
        ValidateProfile(profile);NoLink(input);string mapPath=Path.Combine(input,"pack.json");var map=System.IO.File.Exists(mapPath)?JsonSerializer.Deserialize<Dictionary<string,string>>(System.IO.File.ReadAllText(mapPath))??new():new Dictionary<string,string>();
        if(map.Keys.Any(k=>!Catalog.All.Any(c=>c.Id==k)))throw new InvalidDataException("Unknown cue in pack.json.");
        var files=new Dictionary<string,string>();long total=0;
        foreach(var cue in Catalog.All){string relative=map.GetValueOrDefault(cue.Id,cue.Id+".wav");if(Path.IsPathRooted(relative))throw new InvalidDataException("Mapping must be relative.");string file=Inside(input,relative);if(!System.IO.File.Exists(file))continue;
            string? parent=Path.GetDirectoryName(file);while(parent!=null&&!parent.Equals(Path.GetFullPath(input),StringComparison.OrdinalIgnoreCase)){NoLink(parent);parent=Path.GetDirectoryName(parent);}NoLink(file);
            long bytes=new FileInfo(file).Length;total+=bytes;if(!file.EndsWith(".wav",StringComparison.OrdinalIgnoreCase)||bytes<512||bytes>20*1024*1024||total>200*1024*1024)throw new InvalidDataException("Import requires WAVs up to 20 MB each and 200 MB total.");_=WaveClip.Load(file);files[cue.Id]=file;
        }
        if(files.Count==0)throw new InvalidDataException("No mapped WAV recordings found.");
        string infoPath=Path.Combine(input,"pack-info.json");var info=System.IO.File.Exists(infoPath)?JsonSerializer.Deserialize<VoicePackInfo>(System.IO.File.ReadAllText(infoPath))??new():new VoicePackInfo();
        if(info.Name.Length>200||info.Author.Length>500||info.License.Length>2000||info.SourceUrl.Length>2000||info.Notes.Length>10000)throw new InvalidDataException("Pack information is too large.");
        string target=Path.Combine(appRoot,"Sounds",profile),id=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];string stage=Path.Combine(appRoot,"Data","VoiceImports",id);Directory.CreateDirectory(stage);
        if(Directory.Exists(target))Copy(target,stage);
        foreach(var item in files)System.IO.File.Copy(item.Value,Path.Combine(stage,item.Key+".wav"),true);
        foreach(string name in new[]{"LICENSE","LICENSE.txt","ATTRIBUTION.md","README.md"}){string file=Path.Combine(input,name);if(System.IO.File.Exists(file)){NoLink(file);if(new FileInfo(file).Length>2000000)throw new InvalidDataException("Notice is too large.");System.IO.File.Copy(file,Path.Combine(stage,name),true);}}
        System.IO.File.WriteAllText(Path.Combine(stage,"pack-info.json"),JsonSerializer.Serialize(info,new JsonSerializerOptions{WriteIndented=true}));
        string backup=Path.Combine(appRoot,"Data","VoiceBackups",profile,id);Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        if(Directory.Exists(target))Directory.Move(target,backup);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try{Directory.Move(stage,target);}catch{if(Directory.Exists(backup)&&!Directory.Exists(target))Directory.Move(backup,target);throw;}
        string journal=Path.Combine(appRoot,"Data","VoiceImports",profile+".json");System.IO.File.WriteAllText(journal,JsonSerializer.Serialize(new ImportJournal(id,Directory.Exists(backup))));return files.Count;
    }
    private sealed record ImportJournal(string Id,bool HasBackup);
    public static void Undo(string profile,string appRoot){ValidateProfile(profile);string journal=Path.Combine(appRoot,"Data","VoiceImports",profile+".json");var entry=JsonSerializer.Deserialize<ImportJournal>(System.IO.File.ReadAllText(journal))??throw new InvalidDataException("Import history missing.");if(!System.Text.RegularExpressions.Regex.IsMatch(entry.Id,@"^\d{8}-\d{6}-[a-f0-9]{8}$"))throw new InvalidDataException("Invalid backup identifier.");string backup=Path.Combine(appRoot,"Data","VoiceBackups",profile,entry.Id),target=Path.Combine(appRoot,"Sounds",profile),archive=Path.Combine(appRoot,"Data","VoiceImports","undone-"+Guid.NewGuid().ToString("N"));if(entry.HasBackup&&!Directory.Exists(backup))throw new InvalidDataException("Backup is unavailable.");NoLink(target);Directory.Move(target,archive);try{if(entry.HasBackup)Directory.Move(backup,target);}catch{Directory.Move(archive,target);throw;}System.IO.File.Delete(journal);}
    public static void Template(string folder,string profile){ValidateProfile(profile);Directory.CreateDirectory(folder);foreach(string name in new[]{"pack.json","pack-info.json","README.txt"})if(System.IO.File.Exists(Path.Combine(folder,name)))throw new IOException("Choose a new or empty template folder.");System.IO.File.WriteAllText(Path.Combine(folder,"pack.json"),JsonSerializer.Serialize(Catalog.All.ToDictionary(c=>c.Id,c=>c.Id+".wav"),new JsonSerializerOptions{WriteIndented=true}));System.IO.File.WriteAllText(Path.Combine(folder,"pack-info.json"),JsonSerializer.Serialize(new VoicePackInfo(Aircraft:profile),new JsonSerializerOptions{WriteIndented=true}));System.IO.File.WriteAllText(Path.Combine(folder,"README.txt"),"Place permitted recordings here and edit pack.json to map cue IDs to relative WAV paths. Remove unused entries or leave missing WAVs absent. Fill author, source and licence in pack-info.json; keep original notices. Import validates every recording before switching packs and keeps an undo backup. No TTS is generated.");}
}
