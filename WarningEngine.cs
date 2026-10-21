using System;
using System.Collections.Generic;
using System.Linq;

namespace FlightGuard;

public sealed class WarningEngine
{
    private Frame? previous;
    private readonly FlightPhaseTracker phases = new();
    public bool ApproachAudioAllowed { get; private set; }
    private readonly HashSet<string> once = new();
    private readonly Dictionary<string, DateTime> last = new();
    private DateTime liftoff;
    private double peakMsl;
    private readonly TerrainTrend terrainTrend=new();
    private readonly ConfigurationContext configuration=new();
    private readonly AlertPersistence alertPersistence=new();
    public string WarningReason {get;private set;}="";
    public string MonitorStatus=>configuration.Status+" · "+terrainTrend.Status;
    private double acceleration;
    private bool takeoffObserved;
    private bool landingRoll;
    private string pending = "";
    private DateTime pendingSince;
    public string Phase { get; private set; } = "Standby";
    public string ActiveWarning { get; private set; } = "";
    public void Reset()
    {
        previous = null; phases.Reset(); ApproachAudioAllowed=false; once.Clear(); last.Clear(); liftoff = default; takeoffObserved = false;
        peakMsl = 0; acceleration = 0; landingRoll = false; pending = ""; ActiveWarning = "";WarningReason=""; Phase = "Standby";
        terrainTrend.Reset();configuration.Reset();alertPersistence.Reset();
    }
    public List<SoundCue> Update(Frame f, Settings s, string family, bool enforceFreshness=true,ForwardTerrainAssessment? ahead=null)
    {
        var result = new List<SoundCue>();
        string priorWarning=ActiveWarning; ActiveWarning = "";WarningReason="";
        if (!f.Usable || enforceFreshness && (DateTime.UtcNow - f.Time).TotalSeconds > 3 || !f.Active) { Reset(); Phase = "Paused / inactive"; return result; }
        var p = previous;
        previous = f;
        phases.Update(f); Phase=phases.Label;
        configuration.Update(f,phases,s);terrainTrend.Update(f);
        ApproachAudioAllowed=!f.Ground && f.Vs < -100 && !phases.DepartureProtection(f);
        family=Catalog.ParentProfile(family);
        if (p == null) { peakMsl = f.Msl; return result; }
        double dt = (f.Time - p.Time).TotalSeconds;
        if (dt <= 0 || dt > 3 || f.Aircraft != p.Aircraft || Math.Abs(f.Agl - p.Agl) > Math.Max(180, Math.Abs(f.Vs) * dt / 60 * 3 + 80))
        { Reset(); previous = f; Phase = "Synchronizing"; return result; }
        double rawAcceleration=Math.Clamp((f.Ias-p.Ias)/dt,-25,25);
        acceleration += (1-Math.Exp(-dt/.2))*(rawAcceleration-acceleration);
        double lead=Math.Clamp(s.CalloutLeadMilliseconds/1000d+Math.Min(120,f.SampleLatencyMilliseconds/2)/1000d,0,.25);
        double predictedIas=f.Ias+(f.Ias>p.Ias?Math.Max(0,acceleration)*lead:0);
        double predictedAgl=f.Agl+Math.Min(0,f.Vs)/60*lead;
        bool QuietFlightPhase=(!f.Ground&&f.Agl<250&&f.Vs<0)||(f.Ground&&f.Ias>50);
        void Add(string id, bool single = true, double repeat = 4)
        {
            if (s.DisabledSounds.Contains(id)) return;
            if (single ? !once.Add(id) : last.TryGetValue(id, out var t) && (f.Time - t).TotalSeconds < repeat) return;
            last[id] = f.Time; result.Add(Catalog.Get(id));
        }
        if (f.Ground && f.Ias < 35 && (p.Ias >= 35 || !p.Ground))
        { once.Clear(); takeoffObserved = false; landingRoll = false; liftoff = default; }
        if (phases.JustDeparted || phases.JustGoAround)
        { liftoff = f.Time; peakMsl = f.Msl; takeoffObserved = true; landingRoll = false; }
        if(phases.JustGoAround) { once.Remove("positive-rate"); once.Remove("minimums"); once.Remove("approaching-minimums"); once.Remove("retard"); if(s.Crew)Add("go-around",false,20); }
        if(phases.Current==FlightPhase.TakeoffRoll)landingRoll=false;
        if (takeoffObserved) peakMsl = Math.Max(peakMsl, f.Msl);
        if (!p.Ground && f.Ground)
        {
            landingRoll = true; Phase = "Landing roll"; takeoffObserved = false;
            if (s.Crew) Add("landing");
        }
        if (s.Crew)
        {
            if (!QuietFlightPhase && p.ParkingBrake.HasValue && f.ParkingBrake.HasValue && p.ParkingBrake != f.ParkingBrake)
                Add(f.ParkingBrake.Value ? "parking-brake-set" : "parking-brake-released", false, 2);
            if (!QuietFlightPhase && p.GearDown.HasValue && f.GearDown.HasValue && p.GearDown != f.GearDown)
                Add(f.GearDown.Value ? "gear-down" : "gear-up", false, 2);
            if (!QuietFlightPhase && p.Spoilers.HasValue && f.Spoilers.HasValue && p.Spoilers != f.Spoilers)
            {
                // Infinite Flight enum: 0 off, 1 flight, 2 armed. Deployed is verified by position separately, not inferred from "flight".
                if (f.Spoilers == 2) Add("spoilers-armed", false, 2);
                else if (f.Spoilers == 0 && p.Spoilers == 2) Add("spoilers-disarmed", false, 2);
            }
            if (!QuietFlightPhase && p.Flaps.HasValue && f.Flaps.HasValue && p.Flaps != f.Flaps) Add(f.Flaps == 0 ? "flaps-up" : "flaps-set", false, 2);
            if (p.Autopilot == true && f.Autopilot == false) Add("autopilot-disconnect", false, 3);
            if (takeoffObserved && !f.Ground && f.Agl > 35 && f.Vs > 300) Add("positive-rate");
            if (landingRoll && f.Ground && p.Ias > 70 && f.Ias <= 70) Add("70-knots");
            if (landingRoll && f.Ground && p.Ias > 60 && f.Ias <= 60) Add("60-knots");
            if (f.Ground && !landingRoll && phases.Current!=FlightPhase.RejectedTakeoff && f.Ias > p.Ias && p.Ground)
            {
                double check = family == "Airbus" && Catalog.AircraftProfile(f.Aircraft)!="Airbus A220" ? 100 : 80;
                if (p.Ias < check && predictedIas >= check) Add(check == 100 ? "100-knots" : "80-knots");
                if (s.SpeedsArmed) {
                    if (p.Ias < s.V1 && predictedIas >= s.V1) Add("v1");
                    if (p.Ias < s.Vr && predictedIas >= s.Vr) Add("rotate");
                }
            }
            if (s.SpeedsArmed && !f.Ground && takeoffObserved && p.Ias < s.V2 && predictedIas >= s.V2) Add("v2");
        }
        if (ApproachAudioAllowed)
        {
            bool Cross(double h) => p.Agl > h && predictedAgl <= h;
            if (s.Heights)
            {
                // Only the most recent height is useful when one sample spans several thresholds.
                var crossed = new[] {2500, 2000, 1000, 500, 400, 300, 200, 100, 50, 40, 30, 20, 10}.Where(n => Cross(n) && !once.Contains("altitude-" + n)).ToList();
                foreach (int n in crossed.SkipLast(1)) once.Add("altitude-" + n);
                if (crossed.Count > 0) Add("altitude-" + crossed.Last());
                if (s.Minimums > 0 && Cross(s.Minimums + 100)) Add("approaching-minimums");
                if (s.Minimums > 0 && Cross(s.Minimums)) Add("minimums");
            }
            if (family == "Airbus" && Catalog.UsesRetard(f.Aircraft) && s.Retard && f.GearDown != false && Cross(20)) Add("retard");
        }
        // Re-arm approach announcements after a sustained climb above each threshold + 150 ft.
        if (!f.Ground && f.Vs > 300)
        {
            foreach (int n in new[] {2500,2000,1000,500,400,300,200,100,50,40,30,20,10}) if (f.Agl > n + 150) once.Remove("altitude-" + n);
            if (f.Agl > s.Minimums + 250) { once.Remove("minimums"); once.Remove("approaching-minimums"); }
            if (f.Agl > 200) once.Remove("retard");
        }
        // Only known improper states are inferred; aircraft-specific flap settings are not guessed.
        if(s.Warnings && f.Ground && phases.Current==FlightPhase.TakeoffRoll && f.Ias>60 && (f.ParkingBrake==true || f.SpoilerPosition>.2)) {
            if(pending!="takeoff-config") { pending="takeoff-config"; pendingSince=f.Time; }
            if((f.Time-pendingSince).TotalSeconds>=.3) { ActiveWarning="takeoff-config";WarningReason="Takeoff roll with parking brake set or observed spoiler deployment"; Add("takeoff-config",false,2); result.RemoveAll(x=>x.Priority<70); }
            return result;
        }
        if (!s.Warnings || f.Ground) { pending = "";alertPersistence.Reset(); return result; }
        // Continuous simulator envelopes, scaled by the user's 500-ft reference settings.
        // These are not copied manufacturer software envelopes or a certification claim.
        double sinkLimit=Envelope(f.Agl,new[]{1000d,1100,1500,2200,3000,4500})*s.SinkRate/1500;
        double pullLimit=Envelope(f.Agl,new[]{1500d,1700,2200,3500,4800,6500})*s.PullUpRate/2200;
        double bankLimit=Math.Min(s.BankAngle, f.Agl<150?10+(f.Agl-30)/120*15:f.Agl<800?25+(f.Agl-150)/650*(s.BankAngle-25):s.BankAngle);
        double horizon=configuration.ConfiguredLanding?Math.Min(s.TerrainSeconds,8):s.TerrainSeconds;
        if(phases.DepartureProtection(f))horizon=Math.Min(horizon,6);
        bool closureRisk=s.Terrain&&terrainTrend.Credible&&f.Agl>35&&f.Agl<2000&&terrainTrend.ClosureFpm>(configuration.ConfiguredLanding?1500:1000)&&terrainTrend.TerrainRiseFpm>(configuration.ConfiguredLanding?900:600)&&terrainTrend.ContactSeconds<horizon;
        bool urgentClosure=closureRisk&&terrainTrend.ContactSeconds<Math.Min(4,s.TerrainSeconds*.45)&&terrainTrend.ClosureFpm>1800;
        if(configuration.ConfiguredLanding&&f.Agl<150&&!urgentClosure)closureRisk=false;
        double sinkHysteresis=priorWarning=="sink-rate"?.92:1;
        double pullHysteresis=priorWarning=="pull-up"?.92:1;
        var candidates=new List<(string Id,string Reason)>();
        void Candidate(string id,bool condition,double hold,string reason){if(alertPersistence.Ready(id,condition,f.Time,hold))candidates.Add((id,reason));}
        bool aheadValid=s.Terrain&&s.ForwardTerrain&&ahead?.ValidFor(f)==true;
        Candidate("terrain-ahead-pull-up",aheadValid&&ahead!.Alert=="terrain-ahead-pull-up",0,ahead?.Reason??"");
        Candidate("terrain-ahead",aheadValid&&ahead!.Alert=="terrain-ahead",.3,ahead?.Reason??"");
        // Each condition has its own timer, so one marginal alert cannot continually restart another's confirmation.
        Candidate("pull-up-sink",f.Agl<2450&&f.Agl>=30&&f.Vs< -pullLimit*pullHysteresis,0,$"Excessive descent {f.Vs:0} fpm at {f.Agl:0} ft AGL");
        Candidate("stall",f.Stall==true,0,"Simulator stall warning is active");
        Candidate("pull-up-terrain",urgentClosure,.2,$"Observed rising terrain; estimated clearance time {terrainTrend.ContactSeconds:0.0} s");
        Candidate("terrain",closureRisk,.6,$"Sustained ground rise {terrainTrend.TerrainRiseFpm:0} fpm and AGL closure {terrainTrend.ClosureFpm:0} fpm; no forward map");
        Candidate("overspeed",f.Overspeed==true,.3,"Simulator overspeed warning is active");
        Candidate("sink-rate",f.Agl<2450&&f.Agl>=30&&f.Vs< -sinkLimit*sinkHysteresis,.3,$"Descent {f.Vs:0} fpm exceeds the height-based sink envelope");
        Candidate("dont-sink",phases.DepartureProtection(f)&&peakMsl-f.Msl>Math.Max(25,f.Agl*.1)&&f.Vs< -250,.3,$"Lost {peakMsl-f.Msl:0} ft after observed departure / go-around");
        Candidate("bank-angle",Math.Abs(f.Bank)>bankLimit*(priorWarning=="bank-angle"?.92:1)&&f.Agl<2500&&f.Agl>30,.5,$"Bank {Math.Abs(f.Bank):0}° exceeds the height-based limit");
        Candidate("too-low-gear",s.GearWarnings&&configuration.GearUnsafe&&f.Agl<s.GearHeight+(priorWarning=="too-low-gear"?40:0)&&f.Agl>30,f.Agl<150?.2:.6,f.GearDown==true?"Gear selected down; extension not confirmed after clearance-based grace":"Sustained approach below gear gate with gear selected up");
        Candidate("too-low-flaps",s.FlapWarnings&&configuration.FlapsUnsafe&&f.Agl<s.FlapsHeight+(priorWarning=="too-low-flaps"?30:0)&&f.Agl>30,.5,$"Sustained approach below flap gate: {AircraftConfiguration.FlapDescription(f)} does not meet selected configuration");
        Candidate("too-low-terrain",s.Terrain&&!Catalog.FixedGear(f.Aircraft)&&!configuration.Allowed&&f.Vs<100&&f.Agl<150&&f.Agl>30&&f.GearDown==false&&f.Ias>160&&!phases.DepartureProtection(f),1,"Low clearance at high speed with gear up outside the landing configuration monitor");
        var selected=candidates.Select(c=>(Id:c.Id.StartsWith("pull-up-")?"pull-up":c.Id,c.Reason)).OrderByDescending(c=>Catalog.Get(c.Id).Priority).FirstOrDefault();
        if(selected.Id!=null){
            string warning=selected.Id;ActiveWarning=warning;WarningReason=selected.Reason;
            Add(warning, false, warning is "pull-up" or "terrain-ahead-pull-up"?1.2:warning=="sink-rate"?1.6:warning is "terrain" or "terrain-ahead" or "too-low-gear" or "too-low-flaps" or "too-low-terrain"?1.8:warning is "stall" or "overspeed"?2:2.8);
            // Safety alerts suppress callouts for this sample, even while the alert is in its repeat interval.
            result.RemoveAll(x => x.Priority < 70);
        }
        return result;
    }
    private static double Envelope(double height,double[] rates)
    {
        double[] heights={30,100,500,1000,1500,2450};
        for(int i=1;i<heights.Length;i++)if(height<=heights[i])return rates[i-1]+(rates[i]-rates[i-1])*Math.Clamp((height-heights[i-1])/(heights[i]-heights[i-1]),0,1);
        return rates[^1];
    }
}
