using System;
namespace FlightGuard;
public enum FlightPhase { Parked, Taxi, GroundRoll, TakeoffRoll, RejectedTakeoff, InitialClimb, Climb, Cruise, Descent, Approach, Flare, GoAround, LandingRoll }
public sealed class FlightPhaseTracker
{
    private Frame? previous;
    private DateTime candidateSince, departureTime;
    private FlightPhase candidate;
    private double climbStart;
    private bool departureEnded;
    public FlightPhase Current { get; private set; } = FlightPhase.Parked;
    public bool JustDeparted { get; private set; }
    public bool JustGoAround { get; private set; }
    public bool DepartureObserved { get; private set; }
    public bool DepartureProtection(Frame f) => DepartureObserved && !departureEnded && !f.Ground && f.Agl < 1000 && (f.Time-departureTime).TotalSeconds < 90;
    public string Label => Current switch { FlightPhase.TakeoffRoll=>"Takeoff roll", FlightPhase.InitialClimb=>"Initial climb", FlightPhase.RejectedTakeoff=>"Rejected takeoff", FlightPhase.GoAround=>"Go-around", FlightPhase.LandingRoll=>"Landing roll", FlightPhase.GroundRoll=>"Ground roll", _=>Current.ToString() };
    public void Reset() { previous=null; Current=FlightPhase.Parked; candidateSince=default; departureTime=default; DepartureObserved=false; departureEnded=false; JustDeparted=false; JustGoAround=false; }
    public void Update(Frame f)
    {
        JustDeparted=false; JustGoAround=false;
        var p=previous; previous=f;
        if(p==null) { Current=Classify(f); candidate=Current; candidateSince=f.Time; return; }
        double dt=(f.Time-p.Time).TotalSeconds;
        if(dt<=0||dt>3) { Reset(); previous=f; Current=Classify(f); return; }
        double acceleration=(f.Ias-p.Ias)/dt;
        if(DepartureObserved && (f.Agl>=1000 || (f.Time-departureTime).TotalSeconds>=90))departureEnded=true;
        if(f.Ground) {
            if(!p.Ground) { Current=FlightPhase.LandingRoll; DepartureObserved=false; candidateSince=f.Time; return; }
            if(f.Ias<35) { Current=f.Ias<3?FlightPhase.Parked:FlightPhase.Taxi; DepartureObserved=false; candidateSince=f.Time; return; }
            if(Current==FlightPhase.TakeoffRoll && acceleration < -1) { Current=FlightPhase.RejectedTakeoff; return; }
            if(Current==FlightPhase.RejectedTakeoff) return;
            if(Current==FlightPhase.TakeoffRoll) return;
            if(acceleration>.5 && f.Ias>40) {
                Confirm(FlightPhase.TakeoffRoll,f,.6);
            } else { candidateSince=f.Time; if(Current!=FlightPhase.LandingRoll)Current=FlightPhase.GroundRoll; }
            return;
        }
        if(p.Ground) { Current=FlightPhase.InitialClimb; Depart(f); JustDeparted=true; return; }
        if(Current is FlightPhase.Approach or FlightPhase.Flare) {
            if(f.Vs>400) {
                if(candidate!=FlightPhase.GoAround) { climbStart=f.Msl; candidate=FlightPhase.GoAround; candidateSince=f.Time; }
                if(f.Msl-climbStart>=5 && Confirm(FlightPhase.GoAround,f,1)) { Depart(f); JustGoAround=true; }
                return;
            }
        }
        if(Current is FlightPhase.InitialClimb or FlightPhase.GoAround) {
            // Remain in departure mode through a brief level-off or descent; GPWS Mode 3 stays active.
            if(DepartureProtection(f))return;
        }
        FlightPhase desired=Classify(f);
        if(desired==FlightPhase.Flare && Current==FlightPhase.Approach) { Current=desired; candidate=desired; candidateSince=f.Time; return; }
        Confirm(desired,f,1.2);
    }
    private void Depart(Frame f) { DepartureObserved=true; departureEnded=false; departureTime=f.Time; candidate=Current; candidateSince=f.Time; }
    private bool Confirm(FlightPhase next,Frame f,double seconds) {
        if(candidate!=next) { candidate=next; candidateSince=f.Time; return false; }
        if((f.Time-candidateSince).TotalSeconds<seconds)return false;
        Current=next;return true;
    }
    private static FlightPhase Classify(Frame f) => f.Ground ? f.Ias<3?FlightPhase.Parked:f.Ias<40?FlightPhase.Taxi:FlightPhase.GroundRoll
        : f.Vs < -150 ? f.Agl<50 && f.GearDown!=false ? FlightPhase.Flare : f.Agl<2500?FlightPhase.Approach:FlightPhase.Descent
        : f.Vs>300?FlightPhase.Climb:FlightPhase.Cruise;
}
