using System;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace FlightGuard;
public static class VoiceVerification
{
    public static int Run(string root,string report){try{
        var inventory=new VoiceInventory(root);var models=Catalog.ModelProfiles.Select(p=>{var rows=inventory.Scan(new(),p);return new{Profile=p,Model=rows.Count(r=>r.Coverage.StartsWith("Model")),Fallback=rows.Count(r=>r.Coverage.StartsWith("Shared")),Missing=rows.Count(r=>r.Coverage=="Missing"),Invalid=rows.Count(r=>r.Coverage.StartsWith("Invalid"))};}).ToArray();
        if(models.Any(m=>m.Invalid>0))throw new InvalidDataException("Voice inventory contains invalid recordings");
        string output=Path.GetDirectoryName(Path.GetFullPath(report))!;Directory.CreateDirectory(output);VoiceInventory.Export(Path.Combine(output,"757-voice-coverage.csv"),inventory.Scan(new(),"Boeing 757"));VoiceInventory.Export(Path.Combine(output,"777-voice-coverage.csv"),inventory.Scan(new(),"Boeing 777"));
        File.WriteAllText(report,JsonSerializer.Serialize(new{Status="PASS",Profiles=models,ModelRecordingsAreNotOemCertification=true,SharedFallbacksLabelled=true},new JsonSerializerOptions{WriteIndented=true}));return 0;
    }catch(Exception ex){File.WriteAllText(report,JsonSerializer.Serialize(new{Status="FAIL",Error=ex.Message}));return 1;}}
}
