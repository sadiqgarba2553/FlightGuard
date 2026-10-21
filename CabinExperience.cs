using System;
using System.Collections.Generic;
using System.Linq;

namespace FlightGuard;

public sealed class CabinOptions
{
    public bool Enabled {get;set;}=true;
    public bool AutoBoarding {get;set;}=true;
    public bool Ambience {get;set;}=true;
    public bool IncludeOtherAircraft {get;set;}
    public int Volume {get;set;}=55;
    public void Validate(){if(Volume<0||Volume>100)throw new ArgumentException("Cabin volume must be 0–100.");}
}

// Cabin phases are inferred from observed movement and flight history. No game controls are sent.
public sealed class CabinExperience
{
    private Frame? previous;
    private DateTime parkedSince,stageSince,lastAmbience,climbSince;
    private readonly HashSet<string> once=new();
    private readonly List<SoundCue> requested=new();
    private bool departed,landed,startedAirborne;
    private double highestAgl;
    public string Stage {get;private set;}="Standby";
    public string Status {get;private set;}="Connect at a parked gate to begin the cabin experience.";
    public bool Quiet {get;private set;}=true;
    public bool CanBoard=>previous is {Active:true,Ground:true,GroundSpeedKnots:<1} && previous.ParkingBrake==true;
    public static bool Airliner(string aircraft)=>Catalog.Family(aircraft) is "Airbus" or "Boeing" or "McDonnell Douglas" or "Bombardier" or "Embraer";
    public void Reset(){previous=null;once.Clear();requested.Clear();parkedSince=stageSince=lastAmbience=climbSince=default;departed=landed=startedAirborne=false;highestAgl=0;Stage="Standby";Status="Waiting for fresh flight telemetry.";Quiet=true;}
    private void Enter(string stage,DateTime time){if(Stage!=stage){Stage=stage;stageSince=time;}}
    private void Add(List<SoundCue> list,string id){if(once.Add(id))list.Add(Catalog.Get(id));}
    public bool StartBoarding()
    {
        if(!CanBoard){Status="Boarding needs an active flight, parking brake set and ground speed below 1 kt.";return false;}
        var frame=previous!;Reset();previous=frame;Enter("Boarding",frame.Time);requested.Add(Catalog.Get("boarding-welcome"));once.Add("boarding-welcome");Status="Boarding started · recorded shared cabin pack";return true;
    }
    public bool ReadyForDeparture()
    {
        if(previous is not {Active:true,Ground:true,GroundSpeedKnots:<5} || departed){Status="Complete boarding while stationary or at low pushback speed.";return false;}
        Enter("Ready / pushback",previous.Time);if(once.Add("boarding-complete"))requested.Add(Catalog.Get("boarding-complete"));return true;
    }
    public bool Allowed(string id)=>!Quiet && (id switch {
        "boarding-welcome" or "boarding-ambience"=>Stage=="Boarding",
        "boarding-complete"=>Stage is "Ready / pushback" or "Taxi out",
        "safety-briefing"=>Stage is "Ready / pushback" or "Taxi out",
        "before-takeoff"=>Stage=="Taxi out",
        "taxi-ambience"=>Stage is "Taxi out" or "Taxi in",
        "cabin-landing"=>Stage=="Descent",
        "cabin-seats-landing"=>Stage is "Descent" or "Approach",
        "after-landing"=>Stage=="Taxi in",
        "doors-disarm" or "deboarding"=>Stage=="Arrived",
        _=>false
    });
    public List<SoundCue> Update(Frame f,string flightPhase,CabinOptions options,string warning)
    {
        var cues=new List<SoundCue>();Quiet=true;
        if(!options.Enabled || !Airliner(f.Aircraft)&&!options.IncludeOtherAircraft){previous=null;requested.Clear();Status="Cabin disabled for this aircraft · enable other aircraft if wanted.";return cues;}
        if(!f.Active||!f.Usable){previous=null;requested.Clear();parkedSince=default;Status="Cabin paused · simulator inactive";return cues;}
        var p=previous;previous=f;
        if(p!=null&&(f.Aircraft!=p.Aircraft || (f.Time-p.Time).TotalSeconds>3 || f.Time<=p.Time)){Reset();previous=f;p=null;}
        if(p==null){
            if(!f.Ground){startedAirborne=true;departed=true;Enter("In flight",f.Time);}
            highestAgl=f.Agl;
        }
        highestAgl=Math.Max(highestAgl,f.Agl);
        double? gs=f.GroundSpeedKnots;
        bool taxi=f.Ground&&gs is >1.5 and <35;
        bool parked=f.Ground&&gs is <1&&f.ParkingBrake==true;
        bool critical=warning!="" || !f.Ground&&f.Agl<1500 || f.Ground&&(f.Ias>45||gs>40) || flightPhase is "Rejected takeoff" or "Go-around";
        if(p is {Ground:true}&&!f.Ground){departed=true;Enter("Departure",f.Time);}
        if(p is {Ground:false}&&f.Ground&&departed){landed=true;Enter("Landing roll",f.Time);}
        if(!departed && !landed){
            if(parked&&Stage=="Standby"){
                if(parkedSince==default)parkedSince=f.Time;
                if(options.AutoBoarding&&(f.Time-parkedSince).TotalSeconds>=3){Enter("Boarding",f.Time);Add(cues,"boarding-welcome");}
            }else if(!parked)parkedSince=default;
            if(Stage=="Boarding" && (taxi || p?.ParkingBrake==true&&f.ParkingBrake==false)) {Enter("Ready / pushback",f.Time);Add(cues,"boarding-complete");}
            if(taxi){Enter("Taxi out",f.Time);Add(cues,"safety-briefing");}
            if(Stage=="Ready / pushback"&&(f.Time-stageSince).TotalSeconds>=3)Add(cues,"safety-briefing");
            // A deliberate taxi stop after safety is a conservative takeoff-cabin cue; never infer runway clearance.
            if(Stage=="Taxi out"&&parked&&(f.Time-stageSince).TotalSeconds>15)Add(cues,"before-takeoff");
        }
        else if(landed){
            if(taxi){Enter("Taxi in",f.Time);Add(cues,"after-landing");}
            if(parked){if(parkedSince==default)parkedSince=f.Time;if((f.Time-parkedSince).TotalSeconds>=3){Enter("Arrived",f.Time);Add(cues,"doors-disarm");}}
            else parkedSince=default;
            if(Stage=="Arrived"&&(f.Time-stageSince).TotalSeconds>6)Add(cues,"deboarding");
        }
        else if(!f.Ground){
            if(f.Vs>300){if(climbSince==default)climbSince=f.Time;if((f.Time-climbSince).TotalSeconds>3)Enter("Climb",f.Time);}else climbSince=default;
            if(flightPhase=="Go-around")Enter("Go-around",f.Time);
            else if(f.Vs< -100&&f.Agl<3500&&f.Agl>1500&&f.GearDown==true){Enter("Approach",f.Time);Add(cues,"cabin-seats-landing");}
            else if(highestAgl>4000&&f.Vs< -250&&f.Agl<11000&&f.Agl>3500){Enter("Descent",f.Time);Add(cues,"cabin-landing");}
            else if(Math.Abs(f.Vs)<250&&f.Agl>4000)Enter("Cruise",f.Time);
        }
        cues.AddRange(requested);requested.Clear();Quiet=critical;
        if(options.Ambience&&!critical&&(f.Time-lastAmbience).TotalSeconds>=30){
            if(Stage=="Boarding"&&(f.Time-stageSince).TotalSeconds>=12)cues.Add(Catalog.Get("boarding-ambience"));
            else if(Stage is "Taxi out" or "Taxi in")cues.Add(Catalog.Get("taxi-ambience"));
            if(cues.Any(c=>c.Id.EndsWith("ambience")))lastAmbience=f.Time;
        }
        cues.RemoveAll(c=>!Allowed(c.Id));
        Status=Stage+" · "+(critical?"Cabin quiet for cockpit priority":startedAirborne?"Joined in flight; gate sequence skipped":"Shared recorded cabin audio")+(gs==null?" · ground speed unavailable":"");
        return cues;
    }
}
