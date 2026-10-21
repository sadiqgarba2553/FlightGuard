using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlightGuard;

public sealed class ApproachOptions
{
    public bool Enabled {get;set;}=true;
    public bool CautionTone {get;set;}
    public double GateFeet {get;set;}=1000;
    public double TargetKnots {get;set;}
    public double BelowKnots {get;set;}=5;
    public double AboveKnots {get;set;}=15;
    public double SinkFpm {get;set;}=1000;
    public double BankDegrees {get;set;}=15;
    public int LandingFlapsMinState {get;set;}=1;
    public double PersistenceSeconds {get;set;}=2;
    public void Validate() {
        if(!new[]{GateFeet,TargetKnots,BelowKnots,AboveKnots,SinkFpm,BankDegrees,PersistenceSeconds}.All(double.IsFinite) || GateFeet<50||GateFeet>2500||TargetKnots<0||TargetKnots>400||BelowKnots<1||BelowKnots>50||AboveKnots<1||AboveKnots>50||SinkFpm<300||SinkFpm>4000||BankDegrees<5||BankDegrees>60||LandingFlapsMinState<0||LandingFlapsMinState>10||PersistenceSeconds<.5||PersistenceSeconds>10)
            throw new ArgumentException("Approach settings: gate 50–2500 ft; target 0–400 kt (0 disables speed check); tolerances 1–50 kt; sink 300–4000 fpm; bank 5–60°; flap state 0–10; persistence 0.5–10 s.");
    }
}

public sealed record ApproachAssessment(string State,string[] Issues,string[] Missing,DateTime Time)
{
    public bool Active=>State!="Inactive";
    public string Description=>State=="Inactive"?"Approach monitor standing by":State+ (Issues.Length>0?" · "+string.Join("; ",Issues):Missing.Length>0?" · "+string.Join("; ",Missing):"");
}

public sealed class ApproachMonitor
{
    private DateTime since;
    private string candidate="";
    private DateTime previous;
    public ApproachAssessment Current {get;private set;}=new("Inactive",Array.Empty<string>(),Array.Empty<string>(),default);
    public void Reset(){since=default;candidate="";previous=default;Current=new("Inactive",Array.Empty<string>(),Array.Empty<string>(),default);}
    public ApproachAssessment Update(Frame f,ApproachOptions o,bool approachAllowed) {
        if(previous!=default && ((f.Time-previous).TotalSeconds>3 || f.Time<=previous))Reset();
        previous=f.Time;
        if(!o.Enabled||!f.Usable||!f.Active||f.Ground||!approachAllowed||f.Agl>o.GateFeet){candidate="";return Current=new("Inactive",Array.Empty<string>(),Array.Empty<string>(),f.Time);}
        var issues=new List<string>();var missing=new List<string>();
        if(f.Vs < -o.SinkFpm)issues.Add("Excessive descent rate");
        if(Math.Abs(f.Bank)>o.BankDegrees)issues.Add("Excessive bank");
        if(o.TargetKnots>0 && (f.Ias<o.TargetKnots-o.BelowKnots||f.Ias>o.TargetKnots+o.AboveKnots))issues.Add("Outside target speed band");
        if(!Catalog.FixedGear(f.Aircraft)){if(f.GearDown==false)issues.Add("Landing gear not down");else if(f.GearDown==null)missing.Add("Gear data unavailable");}
        if(o.LandingFlapsMinState>0){if(f.Flaps==null)missing.Add("Flap data unavailable");else if(f.Flaps<o.LandingFlapsMinState)issues.Add("Below selected landing flap state");}
        string next=issues.Count>0?"Unstable":missing.Count>0?"Incomplete":"Stable";
        if(candidate!=next){candidate=next;since=f.Time;}
        string state=(f.Time-since).TotalSeconds>=o.PersistenceSeconds?next:"Checking";
        return Current=new(state,issues.ToArray(),missing.ToArray(),f.Time);
    }
}

public sealed class AircraftPreferences
{
    private readonly string folder;
    public AircraftPreferences(string folder){this.folder=folder;}
    public static string Key(string aircraft)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(aircraft.Trim().ToUpperInvariant())));
    public void Save(string aircraft,Settings settings) {
        if(string.IsNullOrWhiteSpace(aircraft)||aircraft=="Unknown aircraft")return;
        Directory.CreateDirectory(folder);var clone=Settings.Copy(settings);clone.SpeedsArmed=false;clone.Validate();
        string path=Path.Combine(folder,Key(aircraft)+".json");string temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(new AircraftPreference(aircraft,clone),new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,path,true);
    }
    public Settings Load(string aircraft,Settings defaults,Settings globals) {
        Settings result=Settings.Copy(defaults);
        string path=Path.Combine(folder,Key(aircraft)+".json");
        if(File.Exists(path)) { var p=JsonSerializer.Deserialize<AircraftPreference>(File.ReadAllText(path))??throw new InvalidDataException("Invalid aircraft profile"); if(!string.Equals(p.Aircraft.Trim(),aircraft.Trim(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Aircraft profile name mismatch");result=p.Settings;result.Validate(); }
        result.Host=globals.Host;result.Volume=globals.Volume;result.PollMilliseconds=globals.PollMilliseconds;result.AutoRecord=globals.AutoRecord;result.SpeedsArmed=false;return result;
    }
    public bool Exists(string aircraft)=>File.Exists(Path.Combine(folder,Key(aircraft)+".json"));
    private sealed record AircraftPreference(string Aircraft,Settings Settings);
}

public sealed record LandingResult(string Aircraft,DateTime TouchdownUtc,double? EstimatedTouchdownFpm,double? GroundSpeedKnots,double BankDegrees,int EstimatedBounces,string Outcome,ApproachAssessment? At1000Feet,ApproachAssessment? At500Feet,string[] Warnings,string DataQuality);

public sealed class LandingAnalyzer
{
    private Frame? previous;
    private ApproachAssessment? at1000,at500;
    private readonly HashSet<string> warnings=new();
    private LandingResult? pending;
    private DateTime airborneSince,slowSince;
    private double airbornePeak;
    public Action<LandingResult>? Completed;
    public LandingResult? Latest {get;private set;}
    public void Reset(){previous=null;pending=null;at1000=at500=null;warnings.Clear();airborneSince=slowSince=default;}
    public void End(string reason="Session ended"){if(pending!=null)Finish(reason);previous=null;at1000=at500=null;warnings.Clear();}
    private void Finish(string outcome){if(pending==null)return;Latest=pending with{Outcome=outcome,Warnings=warnings.ToArray()};pending=null;Completed?.Invoke(Latest);at1000=at500=null;warnings.Clear();}
    public void Update(Frame f,ApproachAssessment assessment,string warning) {
        var p=previous;
        if(!f.Active||!f.Usable || p!=null && (f.Aircraft!=p.Aircraft||(f.Time-p.Time).TotalSeconds>3||f.Time<=p.Time||Math.Abs(f.Agl-p.Agl)>Math.Max(180,Math.Abs(f.Vs)*(f.Time-p.Time).TotalSeconds/60*3+80))){End("Interrupted telemetry");previous=null;return;}
        previous=f;
        if(p==null)return;
        if(!f.Ground && f.Vs<0 && f.Agl<1500) {
            if(warning!="")warnings.Add(warning);
            if(p.Agl>1000 && f.Agl<=1000)at1000=assessment;
            if(p.Agl>500 && f.Agl<=500)at500=assessment;
        }
        if(!p.Ground && f.Ground) {
            if(pending!=null) {
                if(airborneSince!=default&&(f.Time-airborneSince).TotalSeconds<=3 && airbornePeak<50)pending=pending with{EstimatedBounces=pending.EstimatedBounces+1};
                else Finish("Touch-and-go / separate contact");
            }
            if(pending==null) {
                bool timely=(f.Time-p.Time).TotalSeconds<=.75;
                pending=new(f.Aircraft,f.Time,timely?Math.Min(0,p.Vs):null,f.GroundSpeedKnots,p.Bank,0,"Rollout in progress",at1000,at500,warnings.ToArray(),timely?"Estimated from last airborne sample; no impact-force or runway-centreline measurement":"Touchdown rate unavailable: last airborne sample too old");
            }
            airborneSince=slowSince=default;
        }
        if(pending==null)return;
        if(!f.Ground) {
            if(airborneSince==default){airborneSince=f.Time;airbornePeak=f.Agl;}
            airbornePeak=Math.Max(airbornePeak,f.Agl);
            if((f.Time-airborneSince).TotalSeconds>3 || f.Agl>50 && f.Vs>300)Finish("Touch-and-go / climb away");
        }else{
            double speed=f.GroundSpeedKnots??f.Ias;
            if(speed<35){if(slowSince==default)slowSince=f.Time;if((f.Time-slowSince).TotalSeconds>=2)Finish("Landing rollout completed");}else slowSince=default;
            if(pending!=null && (f.Time-pending.TouchdownUtc).TotalSeconds>60)Finish("Rollout observation ended");
        }
    }
    public static string Save(LandingResult r,string folder) {
        Directory.CreateDirectory(folder);string stem=Path.Combine(folder,r.TouchdownUtc.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(stem+".json",JsonSerializer.Serialize(r,new JsonSerializerOptions{WriteIndented=true}));
        string E(string? s)=>WebUtility.HtmlEncode(s??"Not captured");
        string html=$"<!doctype html><meta charset='utf-8'><title>FlightGuard landing report</title><style>body{{background:#09111e;color:#dce8f6;font:17px/1.7 Segoe UI,sans-serif;max-width:850px;margin:60px auto;padding:24px}}h1{{color:#55dbc4}}td{{padding:10px 24px;border-bottom:1px solid #294056}}</style><h1>{E(r.Aircraft)} landing report</h1><p>{r.TouchdownUtc:yyyy-MM-dd HH:mm:ss} UTC · {E(r.Outcome)}</p><table><tr><td>Estimated touchdown descent</td><td>{(r.EstimatedTouchdownFpm?.ToString("0")??"Unavailable")} fpm</td></tr><tr><td>Ground speed at contact</td><td>{(r.GroundSpeedKnots?.ToString("0")??"Unavailable")} kt</td></tr><tr><td>Bank before contact</td><td>{r.BankDegrees:0.0}°</td></tr><tr><td>Estimated bounces</td><td>{r.EstimatedBounces}</td></tr><tr><td>1000-ft approach snapshot</td><td>{E(r.At1000Feet?.Description)}</td></tr><tr><td>500-ft approach snapshot</td><td>{E(r.At500Feet?.Description)}</td></tr></table><p>Warnings: {E(string.Join(", ",r.Warnings))}</p><p>{E(r.DataQuality)}</p><p>Simulator estimates from Connect samples. Missing or interrupted data limits the report.</p>";
        File.WriteAllText(stem+".html",html);return stem+".html";
    }
}

public sealed class ExtraCrewMonitor
{
    private Frame? previous;
    private readonly HashSet<string> called=new();
    private DateTime rollout;
    private double initialSpeed;
    public string Status {get;private set;}="Crew telemetry standing by";
    public void Reset(){previous=null;called.Clear();rollout=default;Status="Crew telemetry standing by";}
    public List<SoundCue> Update(Frame f,string phase,bool enabled) {
        var result=new List<SoundCue>();var p=previous;previous=f;
        if(!f.Active||!f.Usable){Reset();return result;}
        if(p==null)return result;
        if(f.Aircraft!=p.Aircraft||(f.Time-p.Time).TotalSeconds>3||f.Time<=p.Time){Reset();previous=f;return result;}
        if(phase!="Landing roll"||!f.Ground){if(phase is "Takeoff roll" or "Initial climb" or "Go-around"){called.Clear();rollout=default;}Status="Crew rollout checks standing by";return result;}
        if(rollout==default){rollout=f.Time;initialSpeed=f.GroundSpeedKnots??f.Ias;}
        bool reverse=f.EngineCount is >=1 and <=4 && f.EngineReverse.Length>=f.EngineCount && f.EngineReverse.Take(f.EngineCount.Value).All(x=>x==true);
        Status="Spoilers "+(f.SpoilerPosition is null?"unavailable":f.SpoilerPosition>.5?"deployed":"not deployed")+" · "+(reverse?"reverse thrust confirmed (green indication unavailable)":"reverse unconfirmed")+" · "+(f.GroundSpeedKnots is null?"ground-speed data unavailable":"ground-speed data available");
        if(!enabled)return result;
        void Add(string cue){if(called.Add(cue))result.Add(Catalog.Get(cue));}
        if(f.SpoilerPosition>.5 && f.Ias>35)Add("spoilers-deployed");
        if(f.GroundSpeedKnots is double speed && p.GroundSpeedKnots is double prior && (f.Time-rollout).TotalSeconds>=.6 && (f.Time-p.Time).TotalSeconds>=.02 && initialSpeed-speed>=3 && (prior-speed)/(f.Time-p.Time).TotalSeconds>.8 && speed>30)Add("deceleration");
        return result;
    }
}
