using System;
using System.IO;
using System.Linq;
using System.Text;
namespace FlightGuard;
public static class Coverage
{
    public static int Export(string root,string reference)
    {
        string Cell(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
        string? Source(string profile,string cue){string? file=VoiceRouting.Resolve(root,new(),profile,Catalog.Get(cue));return file==null?null:Path.GetFileName(Path.GetDirectoryName(file));}
        var text=new StringBuilder("Cue,Label,Trigger group,"+string.Join(",",Catalog.Profiles.Select(Cell))+"\n");
        foreach(var cue in Catalog.All)text.AppendLine(string.Join(",",new[]{cue.Id,cue.Text,cue.Group}.Concat(Catalog.Profiles.Select(p=>Source(p,cue.Id)??"Missing · silent")).Select(Cell)));
        File.WriteAllText(Path.Combine(root,"sound-coverage.csv"),text.ToString(),Encoding.UTF8);
        text=new("Aircraft,Automatic profile,Direct pack cues,Shared fallback cues,Missing cues,Cabin automatic default,OEM fidelity\n");
        foreach(string aircraft in File.ReadAllLines(reference).Where(s=>!string.IsNullOrWhiteSpace(s))){
            string profile=Catalog.AircraftProfile(aircraft);var clips=Catalog.All.Select(c=>(Cue:c.Id,Source:Source(profile,c.Id))).ToArray();
            text.AppendLine(string.Join(",",new[]{aircraft,profile,clips.Count(c=>c.Source==profile).ToString(),clips.Count(c=>c.Source!=null&&c.Source!=profile).ToString(),string.Join("; ",clips.Where(c=>c.Source==null).Select(c=>c.Cue)),CabinExperience.Airliner(aircraft)?"Enabled":"Opt-in","Shared simulator recordings; exact OEM voice not independently verified"}.Select(Cell)));
        }
        File.WriteAllText(Path.Combine(root,"aircraft-coverage.csv"),text.ToString(),Encoding.UTF8);return 0;
    }
}
