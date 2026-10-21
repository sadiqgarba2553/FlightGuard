using System;
using System.Collections.Generic;
using System.Linq;

namespace FlightGuard;

public sealed record FlapStop(int State,string Name,double? Angle);
public static class AircraftConfiguration
{
    public static bool? FlapsInsufficient(Frame f,int selected=-1)
    {
        if(f.FlapStops.Length==1)return false; // Known aircraft without a selectable landing flap range.
        if(f.Flaps==null)return null;
        var current=f.FlapStops.FirstOrDefault(s=>s.State==f.Flaps);
        if(selected>=0){
            if(f.Flaps==selected)return false;
            var target=f.FlapStops.FirstOrDefault(s=>s.State==selected);
            if(current?.Angle is double ca && target?.Angle is double ta)return Math.Abs(ca)+.001<Math.Abs(ta);
            return target==null?null:true; // Explicit stop, but no verified ordering: do not guess which is more extended.
        }
        if(current==null)return null;
        var angles=f.FlapStops.Where(s=>s.Angle!=null).ToArray();
        if(angles.Length>1 && current.Angle is double angle){
            double minimum=angles.Min(s=>Math.Abs(s.Angle!.Value)),maximum=angles.Max(s=>Math.Abs(s.Angle!.Value));
            if(maximum-minimum<.001)return null;
            return Math.Abs(angle)<=minimum+.001;
        }
        string name=current.Name.Trim().ToUpperInvariant();
        if(name is "UP" or "CLEAN" or "0" or "0°")return true;
        return null;
    }
    public static string FlapDescription(Frame f)=>f.Flaps==null?"flap telemetry unavailable":f.FlapStops.FirstOrDefault(s=>s.State==f.Flaps) is FlapStop stop?$"flaps {stop.Name} (state {stop.State})":$"flap state {f.Flaps}; stop definition unavailable";
    public static bool GearReady(Frame f)=>Catalog.FixedGear(f.Aircraft)||f.GearState==1&&f.GearDown!=false||f.GearState==null&&f.GearDown==true;
}

public sealed class AlertPersistence
{
    private readonly Dictionary<string,DateTime> since=new();
    public bool Ready(string key,bool condition,DateTime time,double seconds){if(!condition){since.Remove(key);return false;}if(!since.TryGetValue(key,out var start)){since[key]=time;return seconds<=0;}return (time-start).TotalSeconds>=seconds;}
    public void Reset()=>since.Clear();
}

public sealed class ConfigurationContext
{
    private DateTime gearChanged,flapsChanged,lastClimb,approachSeen;
    private Frame? previous;
    public bool Allowed {get;private set;}
    public bool GearUnsafe {get;private set;}
    public bool FlapsUnsafe {get;private set;}
    public bool ConfiguredLanding {get;private set;}
    public string Status {get;private set;}="Waiting for configuration history";
    public void Reset(){previous=null;gearChanged=flapsChanged=lastClimb=approachSeen=default;Allowed=GearUnsafe=FlapsUnsafe=ConfiguredLanding=false;Status="Waiting for configuration history";}
    public void Update(Frame f,FlightPhaseTracker phase,Settings s)
    {
        var p=previous;previous=f;
        if(p==null){gearChanged=flapsChanged=f.Time;}
        else{
            if(p.GearDown!=f.GearDown)gearChanged=f.Time;
            if(p.Flaps!=f.Flaps)flapsChanged=f.Time;
        }
        if(f.Vs>150)lastClimb=f.Time;
        bool departure=phase.DepartureProtection(f)||phase.Current is FlightPhase.InitialClimb or FlightPhase.GoAround;
        bool recentClimb=(f.Time-lastClimb).TotalSeconds<1;
        if(!departure&&!f.Ground&&f.Vs< -150&&f.Agl<2500&&!recentClimb)approachSeen=f.Time;
        Allowed=!f.Ground&&!departure&&!recentClimb&&f.Vs<100&&(f.Time-approachSeen).TotalSeconds<10;
        double contact=f.Vs< -100?f.Agl/(-f.Vs/60):double.PositiveInfinity;
        double gearGrace=f.Agl<150?1:Math.Clamp(contact-5,1,12);
        bool gearMoving=f.GearDown==true&&f.GearState is 2 or 4 or 5;
        bool gearKnownBad=f.GearDown==false||gearMoving;
        bool correctingGear=gearMoving&&(f.Time-gearChanged).TotalSeconds<gearGrace;
        // A just-observed retracted lever is already an established unsafe state, not an extension in progress.
        GearUnsafe=Allowed&&!Catalog.FixedGear(f.Aircraft)&&gearKnownBad&&!correctingGear;
        bool? insufficient=AircraftConfiguration.FlapsInsufficient(f,s.GpwsLandingFlapState);
        FlapsUnsafe=Allowed&&AircraftConfiguration.GearReady(f)&&insufficient==true&&(f.Time-flapsChanged).TotalSeconds>=3;
        ConfiguredLanding=AircraftConfiguration.GearReady(f)&&insufficient==false;
        Status=f.Ground?"Ground: airborne configuration alerts inhibited":departure||recentClimb||f.Vs>=100?"Departure / climb: landing configuration alerts inhibited":correctingGear?"Gear extension in progress; monitoring available clearance":insufficient==null?"Flap definition unavailable: no automatic flap-state guess":ConfiguredLanding?"Landing configuration accepted":Allowed?"Monitoring approach configuration":"Waiting for sustained approach / descent";
    }
}

public sealed class TerrainTrend
{
    private readonly Queue<Frame> samples=new();
    public bool Credible {get;private set;}
    public double ClosureFpm {get;private set;}
    public double TerrainRiseFpm {get;private set;}
    public double ContactSeconds {get;private set;}=double.PositiveInfinity;
    public string Status {get;private set;}="Learning observed terrain trend";
    public void Reset(){samples.Clear();Credible=false;ClosureFpm=TerrainRiseFpm=0;ContactSeconds=double.PositiveInfinity;Status="Learning observed terrain trend";}
    public void Update(Frame f)
    {
        samples.Enqueue(f);while(samples.Count>2&&(f.Time-samples.Peek().Time).TotalSeconds>1.5)samples.Dequeue();
        Credible=false;ContactSeconds=double.PositiveInfinity;
        if(samples.Count<4||(f.Time-samples.Peek().Time).TotalSeconds<.75){Status="Learning observed terrain trend";return;}
        var data=samples.ToArray();DateTime start=data[0].Time;double meanT=data.Average(x=>(x.Time-start).TotalSeconds);
        double denominator=data.Sum(x=>Math.Pow((x.Time-start).TotalSeconds-meanT,2));
        double Slope(Func<Frame,double> value){double mean=data.Average(value);return data.Sum(x=>((x.Time-start).TotalSeconds-meanT)*(value(x)-mean))/denominator;}
        double aglSlope=Slope(x=>x.Agl),mslSlope=Slope(x=>x.Msl),meanAgl=data.Average(x=>x.Agl);
        double residual=Math.Sqrt(data.Average(x=>Math.Pow(x.Agl-(meanAgl+aglSlope*((x.Time-start).TotalSeconds-meanT)),2)));
        double observedVs=mslSlope*60,reportedVs=data.Average(x=>x.Vs);
        if(residual>Math.Max(10,f.Agl*.025)){Status="Uneven AGL samples: local terrain estimate withheld";return;}
        if(Math.Abs(observedVs-reportedVs)>Math.Max(650,Math.Abs(reportedVs)*.55)){Status="MSL / vertical-speed disagreement: local terrain estimate withheld";return;}
        ClosureFpm=-aglSlope*60;TerrainRiseFpm=(mslSlope-aglSlope)*60;
        if(ClosureFpm>0)ContactSeconds=Math.Max(0,f.Agl)/(ClosureFpm/60);
        Credible=true;Status=TerrainRiseFpm>600?$"Observed ground rising {TerrainRiseFpm:0} fpm; estimated clearance time {ContactSeconds:0.0} s":"Observed terrain trend stable";
    }
}
