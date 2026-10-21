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
    private double closureFpm;
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
        peakMsl = 0; closureFpm = 0; acceleration = 0; landingRoll = false; pending = ""; ActiveWarning = ""; Phase = "Standby";
    }
    public List<SoundCue> Update(Frame f, Settings s, string family, bool enforceFreshness=true)
    {
        var result = new List<SoundCue>();
        string priorWarning=ActiveWarning; ActiveWarning = "";
        if (!f.Usable || enforceFreshness && (DateTime.UtcNow - f.Time).TotalSeconds > 3 || !f.Active) { Reset(); Phase = "Paused / inactive"; return result; }
        var p = previous;
        previous = f;
        phases.Update(f); Phase=phases.Label;
        ApproachAudioAllowed=!f.Ground && f.Vs < -100 && !phases.DepartureProtection(f);
        family=Catalog.ParentProfile(family);
        if (p == null) { peakMsl = f.Msl; return result; }
        double dt = (f.Time - p.Time).TotalSeconds;
        if (dt <= 0 || dt > 3 || f.Aircraft != p.Aircraft || Math.Abs(f.Agl - p.Agl) > Math.Max(180, Math.Abs(f.Vs) * dt / 60 * 3 + 80))
        { Reset(); previous = f; Phase = "Synchronizing"; return result; }
        double rawClosure = -(f.Agl-p.Agl)/dt*60;
        double alpha=1-Math.Exp(-dt/.3);
        closureFpm += alpha*(Math.Clamp(rawClosure,-24000,24000)-closureFpm);
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
            if((f.Time-pendingSince).TotalSeconds>=.3) { ActiveWarning="takeoff-config"; Add("takeoff-config",false,2); result.RemoveAll(x=>x.Priority<70); }
            return result;
        }
        if (!s.Warnings || f.Ground) { pending = ""; return result; }
        bool descent = f.Vs < -150 && !phases.DepartureProtection(f);
        // Continuous simulator envelopes, scaled by the user's 500-ft reference settings.
        // These are not copied manufacturer software envelopes or a certification claim.
        double sinkLimit=Envelope(f.Agl,new[]{1000d,1100,1500,2200,3000,4500})*s.SinkRate/1500;
        double pullLimit=Envelope(f.Agl,new[]{1500d,1700,2200,3500,4800,6500})*s.PullUpRate/2200;
        double bankLimit=Math.Min(s.BankAngle, f.Agl<150?10+(f.Agl-30)/120*15:f.Agl<800?25+(f.Agl-150)/650*(s.BankAngle-25):s.BankAngle);
        bool closureRisk=s.Terrain&&f.Agl>50&&f.Agl<2000&&closureFpm>Math.Max(1500,-f.Vs+700)&&f.Agl/(closureFpm/60)<s.TerrainSeconds;
        double sinkHysteresis=priorWarning=="sink-rate"?.92:1;
        double pullHysteresis=priorWarning=="pull-up"?.92:1;
        string warning = "";
        if (f.Agl < 2450 && f.Agl >= 30 && f.Vs < -pullLimit*pullHysteresis) warning = "pull-up";
        else if (f.Stall == true) warning = "stall";
        else if (closureRisk && f.Agl/(closureFpm/60)<s.TerrainSeconds*.5) warning="pull-up";
        else if (closureRisk) warning = "terrain";
        else if (f.Overspeed == true) warning = "overspeed";
        else if (f.Agl < 2450 && f.Agl >= 30 && f.Vs < -sinkLimit*sinkHysteresis) warning = "sink-rate";
        else if (phases.DepartureProtection(f) && peakMsl - f.Msl > Math.Max(25, f.Agl * .1) && f.Vs < -250) warning = "dont-sink";
        else if (Math.Abs(f.Bank) > bankLimit*(priorWarning=="bank-angle"?.92:1) && f.Agl < 2500 && f.Agl > 30) warning = "bank-angle";
        else if (s.GearWarnings && !Catalog.FixedGear(f.Aircraft) && descent && f.Agl < s.GearHeight && f.Agl > 30 && f.GearDown == false) warning = "too-low-gear";
        else if (s.FlapWarnings && descent && f.Agl < s.FlapsHeight && f.Agl > 30 && f.Flaps == 0 && f.GearDown == true) warning = "too-low-flaps";
        else if (s.Terrain && !Catalog.FixedGear(f.Aircraft) && f.Agl < 150 && f.Agl > 30 && f.GearDown == false && f.Ias > 160 && !phases.DepartureProtection(f)) warning = "too-low-terrain";
        if (warning == "") { pending = ""; return result; }
        if (pending != warning) { pending = warning; pendingSince = f.Time; }
        if (Catalog.Get(warning).Priority >= 95 || (f.Time - pendingSince).TotalSeconds >= (warning=="bank-angle"?.5:.3))
        {
            ActiveWarning = warning;
            Add(warning, false, warning is "pull-up" or "sink-rate" ? 1.6 : warning is "stall" or "overspeed" ? 2 : 2.8);
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
