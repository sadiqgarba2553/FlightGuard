using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FlightGuard;

public sealed class Settings
{
    public string Host { get; set; } = "";
    public string Profile { get; set; } = "Auto";
    public double V1 { get; set; } = 135;
    public double Vr { get; set; } = 140;
    public double V2 { get; set; } = 150;
    public bool SpeedsArmed { get; set; }
    public double Minimums { get; set; } = 200;
    public double SinkRate { get; set; } = 1300;
    public double PullUpRate { get; set; } = 2500;
    public double TerrainSeconds { get; set; } = 12;
    public double BankAngle { get; set; } = 40;
    public double GearHeight { get; set; } = 800;
    public double FlapsHeight { get; set; } = 500;
    public bool ForwardTerrain {get;set;}=true;
    public int TerrainLookAheadSeconds {get;set;}=60;
    public int GpwsLandingFlapState {get;set;}=-1;
    public bool Warnings { get; set; } = true;
    public bool Heights { get; set; } = true;
    public bool Crew { get; set; } = true;
    public bool Terrain { get; set; } = true;
    public bool GearWarnings { get; set; } = true;
    public bool FlapWarnings { get; set; } = true;
    public bool Retard { get; set; } = true;
    public int Volume { get; set; } = 85;
    public int PollMilliseconds { get; set; } = 50;
    public int CalloutLeadMilliseconds { get; set; } = 80;
    public ApproachOptions Approach {get;set;}=new();
    public CabinOptions Cabin {get;set;}=new();
    public VoiceOptions Voices {get;set;}=new();
    public bool AutoRecord {get;set;}=true;
    public bool ExtraCrew {get;set;}=true;
    public static Settings Copy(Settings settings)=>JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
    public List<string> DisabledSounds { get; set; } = new();
    public static string DataFolder => Path.Combine(AppContext.BaseDirectory, "Data");
    public static string FileName => Path.Combine(DataFolder, "settings.json");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FileName)) ?? new(); }
        catch { return new(); }
    }
    public void Validate()
    {
        if (!double.IsFinite(V1) || !double.IsFinite(Vr) || !double.IsFinite(V2) || V1 < 20 || V2 > 400 || V1 > Vr || Vr > V2)
            throw new ArgumentException("Set speeds between 20 and 400 knots, with V1 ≤ VR ≤ V2.");
        if (!double.IsFinite(Minimums) || Minimums < 0 || Minimums > 2500) throw new ArgumentException("Minimums must be 0–2500 feet AGL.");
        if (!double.IsFinite(SinkRate) || !double.IsFinite(PullUpRate) || SinkRate < 500 || PullUpRate <= SinkRate || PullUpRate > 10000)
            throw new ArgumentException("Sink rate must be at least 500 fpm; pull-up rate must be greater and at most 10,000 fpm.");
        if (!double.IsFinite(TerrainSeconds) || TerrainSeconds < 3 || TerrainSeconds > 30 || !double.IsFinite(BankAngle) || BankAngle < 10 || BankAngle > 75)
            throw new ArgumentException("Terrain closure must be 3–30 seconds and bank threshold 10–75 degrees.");
        if (!double.IsFinite(GearHeight) || GearHeight < 50 || GearHeight > 1500 || !double.IsFinite(FlapsHeight) || FlapsHeight < 50 || FlapsHeight > 1000)
            throw new ArgumentException("Gear height must be 50–1500 ft; flap height 50–1000 ft.");
        if(GpwsLandingFlapState< -1||GpwsLandingFlapState>20)throw new ArgumentException("GPWS landing flap state must be -1 (automatic) or a stop index 0–20.");
        if(TerrainLookAheadSeconds<15||TerrainLookAheadSeconds>90)throw new ArgumentException("Terrain look-ahead must be 15–90 seconds.");
        if(PollMilliseconds<50 || PollMilliseconds>500 || CalloutLeadMilliseconds<0 || CalloutLeadMilliseconds>250) throw new ArgumentException("Polling must be 50–500 ms; timing compensation 0–250 ms.");
        if(Profile!="Auto"&&!Catalog.Profiles.Contains(Profile))throw new ArgumentException("Select Auto or a listed aircraft/manufacturer sound profile.");
        Approach ??=new(); Approach.Validate(); Cabin ??=new();Cabin.Validate();Voices??=new();Voices.Validate();
        Volume = Math.Clamp(Volume, 0, 100);
        DisabledSounds ??= new();
    }
    public void Save() { Validate(); Directory.CreateDirectory(DataFolder); File.WriteAllText(FileName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
}

public sealed record SoundCue(string Id, string Text, string Group, int Priority = 20);
public static class Catalog
{
    public static readonly string[] Families = {"Airbus","Boeing","McDonnell Douglas","Bombardier","Embraer","Cessna","Cirrus","Piper","CubCrafters","Daher","Lockheed Martin","Grumman","Fairchild Republic","Supermarine","Generic"};
    public static readonly string[] ModelProfiles = {"Airbus A220","Airbus A320 family","Airbus A330","Airbus A340","Airbus A350","Airbus A380","Boeing 737","Boeing 747","Boeing 757","Boeing 767","Boeing 777","Boeing 787","McDonnell Douglas MD-11","McDonnell Douglas DC-10","McDonnell Douglas 717","Bombardier CRJ","Bombardier Q400","Bombardier Challenger","Embraer E-Jet"};
    public static string[] Profiles => ModelProfiles.Concat(Families).ToArray();
    public static string ParentProfile(string profile) => Families.FirstOrDefault(f=>profile.StartsWith(f+" ",StringComparison.OrdinalIgnoreCase)) ?? profile;
    public static string AircraftProfile(string name) {
        string n=name.ToUpperInvariant().Replace("-","").Replace(" ","");
        foreach(var (token,profile) in new[]{("A220","Airbus A220"),("A330","Airbus A330"),("A340","Airbus A340"),("A350","Airbus A350"),("A380","Airbus A380"),("717","McDonnell Douglas 717"),("737","Boeing 737"),("747","Boeing 747"),("757","Boeing 757"),("767","Boeing 767"),("777","Boeing 777"),("787","Boeing 787"),("MD11","McDonnell Douglas MD-11"),("DC10","McDonnell Douglas DC-10"),("CRJ","Bombardier CRJ"),("Q400","Bombardier Q400"),("CHALLENGER","Bombardier Challenger")})if(n.Contains(token))return profile;
        if(new[]{"A318","A319","A320","A321"}.Any(n.Contains))return "Airbus A320 family";
        if(n.Contains("E175")||n.Contains("E190")||n.Contains("EMBRAER"))return "Embraer E-Jet";
        return Family(name);
    }
    public static string DefaultModel(string manufacturer) => manufacturer switch {"Airbus"=>"Airbus A320 family","Boeing"=>"Boeing 737","McDonnell Douglas"=>"McDonnell Douglas MD-11","Bombardier"=>"Bombardier CRJ","Embraer"=>"Embraer E-Jet",_=>manufacturer};
    public static string[] SoundFolders(string profile) => new[]{profile,ParentProfile(profile),DefaultModel(ParentProfile(profile)),"Generic","Shared Crew","Shared Cabin"}.Distinct().ToArray();
    public static bool UsesRetard(string aircraft) => Family(aircraft)=="Airbus" && AircraftProfile(aircraft)!="Airbus A220";
    public static readonly List<SoundCue> All = Create();
    private static List<SoundCue> Create()
    {
        var a = new List<SoundCue> {
            new("pull-up", "Pull up, pull up", "Warnings", 100), new("terrain", "Terrain, terrain", "Warnings", 90),
            new("terrain-ahead", "Terrain ahead", "Warnings", 90),new("terrain-ahead-pull-up", "Terrain ahead · pull up", "Warnings", 100),
            new("sink-rate", "Sink rate", "Warnings", 80), new("dont-sink", "Don't sink", "Warnings", 80),
            new("too-low-terrain", "Too low, terrain", "Warnings", 85), new("too-low-gear", "Too low, gear", "Warnings", 75),
            new("too-low-flaps", "Too low, flaps", "Warnings", 70), new("bank-angle", "Bank angle, bank angle", "Warnings", 75),
            new("glideslope", "Glideslope", "Manual / unavailable", 78), new("stall", "Stall, stall", "Warnings", 95),
            new("overspeed", "Overspeed", "Warnings", 85), new("minimums", "Minimums", "Approach", 64),
            new("approaching-minimums", "Approaching minimums", "Approach", 57), new("retard", "Retard", "Approach", 63),
            new("v1", "V one", "Takeoff", 65), new("rotate", "Rotate", "Takeoff", 65), new("v2", "V two", "Takeoff", 59),
            new("80-knots", "Eighty knots", "Takeoff", 54), new("100-knots", "One hundred knots", "Takeoff", 54),
            new("positive-rate", "Positive rate", "Crew"), new("gear-up", "Gear up", "Crew"), new("gear-down", "Gear down", "Crew"),
            new("gear-locked", "Gear lock effect", "Manual / unavailable"),
            new("spoilers-armed", "Spoilers armed", "Crew"), new("spoilers-disarmed", "Spoilers disarmed", "Crew"),
            new("spoilers-deployed", "Spoilers deployed", "Crew"), new("parking-brake-set", "Parking brake set", "Crew"),
            new("parking-brake-released", "Parking brake released", "Crew"), new("autopilot-disconnect", "Autopilot disconnect", "Crew", 50),
            new("flaps-up", "Flaps up", "Crew"), new("flaps-set", "Flap lever", "Crew"), new("landing", "Touchdown effect", "Crew"),
            new("70-knots", "Seventy knots", "Crew"), new("60-knots", "Sixty knots", "Crew"),
            new("go-around", "Go around", "Crew", 55), new("thrust-set", "Thrust set", "Manual / unavailable"),
            new("reverse-green", "Reverse green", "Manual / unavailable"), new("deceleration", "Decel", "Crew"),
            new("takeoff-config", "Takeoff configuration", "Warnings", 80),
            new("windshear", "Windshear, windshear, windshear", "Manual / unavailable", 95),
            new("traffic", "Traffic, traffic", "Manual / unavailable", 80), new("climb", "Climb, climb", "Manual / unavailable", 85),
            new("descend", "Descend, descend", "Manual / unavailable", 85), new("clear-of-conflict", "Clear of conflict", "Manual / unavailable"),
            new("engine-fire", "Engine fire", "Manual / unavailable", 90), new("master-caution", "Master caution", "Manual / unavailable", 60),
            new("master-warning", "Master warning", "Manual / unavailable", 80),
            new("seatbelts-on", "Seat belt chime", "Manual / unavailable"), new("before-takeoff", "Cabin crew, seats for takeoff", "Cabin", 12),
            new("after-landing", "After-landing announcement", "Cabin", 12),
            new("boarding-welcome","Welcome aboard · shared recording","Cabin",12),
            new("boarding-complete","Boarding-complete effect","Cabin",12),
            new("safety-briefing","Cabin safety briefing · shared recording","Cabin",12),
            new("boarding-ambience","Boarding ambience","Ambience",4),
            new("taxi-ambience","Taxi rolling ambience","Ambience",4),
            new("cabin-landing","Cabin preparation for landing","Cabin",12),
            new("cabin-seats-landing","Cabin crew, seats for landing","Cabin",12),
            new("doors-disarm","Doors disarm · shared recording","Cabin",12),
            new("deboarding","Deboarding ambience","Ambience",4), new("gpws-test", "GPWS recording test sequence", "Test")
        };
        foreach (int n in new[] { 2500, 2000, 1000, 500, 400, 300, 200, 100, 50, 40, 30, 20, 10, 5 })
            a.Add(new("altitude-" + n, Number(n), "Heights", 60));
        return a;
    }
    private static string Number(int n) => n switch { 2500 => "Twenty five hundred", 2000 => "Two thousand", 1000 => "One thousand", 500 => "Five hundred", 400 => "Four hundred", 300 => "Three hundred", 200 => "Two hundred", 100 => "One hundred", 50 => "Fifty", 40 => "Forty", 30 => "Thirty", 20 => "Twenty", 10 => "Ten", _ => "Five" };
    public static SoundCue Get(string id) => All.First(x => x.Id == id);
    public static string Family(string name)
    {
        string n = name.ToUpperInvariant().Replace("-", "").Replace(" ", "");
        if (n.Contains("MCDONNELL") || n.Contains("MD11") || n.Contains("MD80") || n.Contains("DC10") || n.Contains("DC9") || n.Contains("717")) return "McDonnell Douglas";
        if (n.Contains("AIRBUS") || System.Text.RegularExpressions.Regex.IsMatch(n, @"A(22[01]|3\d\d)")) return "Airbus";
        if (n.Contains("BOEING") || System.Text.RegularExpressions.Regex.IsMatch(n, @"B?7[02345678]7")) return "Boeing";
        if(n.Contains("BOMBARDIER") || n.Contains("CRJ") || n.Contains("CHALLENGER") || n.Contains("DASH8") || n.Contains("Q400")) return "Bombardier";
        if(n.Contains("EMBRAER") || n.Contains("E175") || n.Contains("E190")) return "Embraer";
        if(n.Contains("CESSNA") || n.Contains("C172") || n.Contains("C208")) return "Cessna";
        if(n.Contains("CIRRUS") || n.Contains("SR22")) return "Cirrus";
        if(n.Contains("PIPER") || n.Contains("PA28") || n.Contains("ARCHER")) return "Piper";
        if(n.Contains("XCUB") || n.Contains("CUBCRAFTERS")) return "CubCrafters";
        if(n.Contains("TBM") || n.Contains("DAHER")) return "Daher";
        if(n.Contains("C130") || n.Contains("F16") || n.Contains("F22") || n.Contains("P38") || n.Contains("LOCKHEED")) return "Lockheed Martin";
        if(n.Contains("F14") || n.Contains("GRUMMAN")) return "Grumman";
        if(n.Contains("C17") || n.Contains("F/A18") || n.Contains("FA18")) return "Boeing";
        if(n=="A10" || n.Contains("FAIRCHILD")) return "Fairchild Republic";
        if(n.Contains("SPITFIRE") || n.Contains("SUPERMARINE")) return "Supermarine";
        return "Generic";
    }
    public static bool FixedGear(string name)
    {
        string n = System.Text.RegularExpressions.Regex.Replace(name.ToUpperInvariant(),"[^A-Z0-9]","");
        return n.Contains("CESSNA172") || n.Contains("CESSNA208") || n.StartsWith("C172") || n.StartsWith("C208") || n.Contains("SR22") || n.Contains("XCUB") || n.Contains("PA28") || n.Contains("ARCHER");
    }
}

public sealed record Frame
{
    public DateTime Time { get; init; } = DateTime.UtcNow;
    public string Aircraft { get; init; } = "Unknown aircraft";
    public string AppState { get; init; } = "Playing";
    public double Agl { get; init; }
    public double Msl { get; init; }
    public double? Latitude {get;init;}
    public double? Longitude {get;init;}
    public double Ias { get; init; }
    public double Vs { get; init; }
    public double Bank { get; init; }
    public double SampleLatencyMilliseconds {get;init;}
    public bool Ground { get; init; }
    public bool? GearDown { get; init; }
    public int? GearState { get; init; }
    public int? Spoilers { get; init; }
    public double? SpoilerPosition { get; init; }
    public double? GroundSpeedKnots {get;init;}
    public int? EngineCount {get;init;}
    public bool?[] EngineReverse {get;init;}=Array.Empty<bool?>();
    public bool? ParkingBrake { get; init; }
    public int? Flaps { get; init; }
    public FlapStop[] FlapStops {get;init;}=Array.Empty<FlapStop>();
    private bool ValidFlapStops=>FlapStops!=null&&FlapStops.Length<=21&&FlapStops.All(s=>s!=null&&s.State>=0&&s.State<=20&&s.Name!=null&&s.Name.Length<=160&&(s.Angle==null||double.IsFinite(s.Angle.Value)))&&FlapStops.Select(s=>s.State).Distinct().Count()==FlapStops.Length;
    public bool? Autopilot { get; init; }
    public bool? Stall { get; init; }
    public bool? Overspeed { get; init; }
    public bool Usable => (Latitude==null||double.IsFinite(Latitude.Value)&&Math.Abs(Latitude.Value)<=90)&&(Longitude==null||double.IsFinite(Longitude.Value)&&Math.Abs(Longitude.Value)<=180)&&ValidFlapStops && EngineReverse!=null && EngineReverse.Length<=8 && (GroundSpeedKnots==null || double.IsFinite(GroundSpeedKnots.Value)&&GroundSpeedKnots>=0&&GroundSpeedKnots<2500) && (SpoilerPosition==null || double.IsFinite(SpoilerPosition.Value)) && double.IsFinite(SampleLatencyMilliseconds) &&  double.IsFinite(Agl) && double.IsFinite(Msl) && double.IsFinite(Ias) && double.IsFinite(Vs) && double.IsFinite(Bank) && Agl >= -50 && Agl < 200000 && Ias >= 0 && Ias < 2500 && Math.Abs(Vs) < 100000;
    public bool Active => AppState.Equals("Playing", StringComparison.OrdinalIgnoreCase) || AppState.Equals("Flying", StringComparison.OrdinalIgnoreCase);
}
