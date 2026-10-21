using System;
using System.Collections.Generic;
using System.Linq;
namespace FlightGuard;
public static class PhaseTests
{
    private static void Check(bool ok) { if(!ok)throw new Exception("Flight-phase regression"); }
    private static Frame F(double h=1000,double vs=0,double speed=140,bool ground=false) => new() { Aircraft="Boeing 737-800",Agl=h,Msl=h+100,Vs=vs,Ias=speed,Ground=ground,GearDown=true,Flaps=2 };
    public static void Run(Action<string,Action> test)
    {
        test("Model detection separates 737, 747, 777 and 787",()=>{
            foreach(string model in new[]{"737","747","777","787"})Check(Catalog.AircraftProfile(model+"-800")=="Boeing "+model);
            Check(Catalog.AircraftProfile("A350-1000")=="Airbus A350");Check(Catalog.AircraftProfile("MD-11F")=="McDonnell Douglas MD-11");Check(Catalog.AircraftProfile("DC-10F")=="McDonnell Douglas DC-10");
        });
        test("Model audio falls back explicitly through manufacturer",()=>Check(Catalog.SoundFolders("Boeing 747").SequenceEqual(new[]{"Boeing 747","Boeing","Boeing 737","Generic","Shared Crew","Shared Cabin"})));
        test("A220 does not inherit Airbus retard logic",()=>{Check(!Catalog.UsesRetard("Airbus A220-300"));Check(Catalog.UsesRetard("A350-1000"));});
        test("Complete flight distinguishes takeoff, climb, descent, flare and rollout",()=>{
            var p=new FlightPhaseTracker();DateTime t=DateTime.UtcNow;
            void Feed(Frame f,int count=1){for(int i=0;i<count;i++){t=t.AddSeconds(.2);p.Update(f with{Time=t});}}
            Feed(F(1,0,0,true));Check(p.Current==FlightPhase.Parked);
            Feed(F(1,0,15,true));Check(p.Current==FlightPhase.Taxi);
            for(int i=0;i<12;i++)Feed(F(1,0,45+i*2,true));Check(p.Current==FlightPhase.TakeoffRoll);
            Feed(F(10,1000,145));Check(p.Current==FlightPhase.InitialClimb);
            Feed(F(1200,1000),12);Check(p.Current==FlightPhase.Climb);
            Feed(F(10000,0),12);Check(p.Current==FlightPhase.Cruise);
            Feed(F(8000,-1000),12);Check(p.Current==FlightPhase.Descent);
            Feed(F(1500,-700),12);Check(p.Current==FlightPhase.Approach);
            Feed(F(40,-500));Check(p.Current==FlightPhase.Flare);
            Feed(F(0,0,110,true));Check(p.Current==FlightPhase.LandingRoll);
            Feed(F(0,0,25,true));Check(p.Current==FlightPhase.Taxi);
        });
        test("Vertical-speed noise does not flap between cruise and descent",()=>{
            var p=new FlightPhaseTracker();var t=DateTime.UtcNow;p.Update(F(10000) with{Time=t});
            for(int i=1;i<50;i++)p.Update(F(10000,i%2==0?350:-350) with{Time=t.AddSeconds(i*.2)});
            Check(p.Current==FlightPhase.Cruise);
        });
        test("Rejected takeoff remains distinct until slowing down",()=>{
            var p=new FlightPhaseTracker();var t=DateTime.UtcNow;
            for(int i=0;i<12;i++)p.Update(F(0,0,45+i*3,true) with{Time=t.AddSeconds(i*.2)});
            p.Update(F(0,0,65,true) with{Time=t.AddSeconds(2.4)});Check(p.Current==FlightPhase.RejectedTakeoff);
            p.Update(F(0,0,75,true) with{Time=t.AddSeconds(2.6)});Check(p.Current==FlightPhase.RejectedTakeoff);
            p.Update(F(0,0,20,true) with{Time=t.AddSeconds(2.8)});Check(p.Current==FlightPhase.Taxi);
        });
        test("Go-around requires sustained climb and actual altitude gain",()=>{
            var p=new FlightPhaseTracker();var t=DateTime.UtcNow;
            p.Update(F(200,-700) with{Time=t});Check(p.Current==FlightPhase.Approach);
            for(int i=1;i<10;i++)p.Update(F(200+i*3,900) with{Time=t.AddSeconds(i*.2)});
            Check(p.Current==FlightPhase.GoAround && p.DepartureProtection(F(230) with{Time=t.AddSeconds(2)}));
        });
        test("One climb spike on approach does not declare go-around",()=>{
            var p=new FlightPhaseTracker();var t=DateTime.UtcNow;
            p.Update(F(200,-700) with{Time=t});p.Update(F(202,800) with{Time=t.AddSeconds(.2)});p.Update(F(200,-700) with{Time=t.AddSeconds(.4)});
            Check(p.Current==FlightPhase.Approach && !p.DepartureObserved);
        });
        test("Departure protection cannot reactivate after clearing 1000 ft",()=>{
            var p=new FlightPhaseTracker();var t=DateTime.UtcNow;
            p.Update(F(0,0,145,true) with{Time=t});p.Update(F(20,900) with{Time=t.AddSeconds(.2)});
            p.Update(F(1100,900) with{Time=t.AddSeconds(2)});p.Update(F(950,-700) with{Time=t.AddSeconds(3)});
            Check(!p.DepartureProtection(F(950,-700) with{Time=t.AddSeconds(3)}));
        });
        test("Departure altitude loss triggers don't sink without landing calls",()=>{
            var e=new WarningEngine();var s=new Settings{CalloutLeadMilliseconds=0};var t=DateTime.UtcNow;var all=new List<SoundCue>();
            void Feed(Frame f){t=t.AddSeconds(.2);all.AddRange(e.Update(f with{Time=t,GearDown=false,Flaps=0},s,"Boeing 737"));}
            Feed(F(0,0,145,true));Feed(F(35,900));Feed(F(210,900));
            for(int i=0;i<20;i++)Feed(F(205-i*3,-600));
            Check(all.Any(c=>c.Id=="dont-sink"));Check(!all.Any(c=>c.Group=="Heights"||c.Id is "minimums" or "too-low-gear" or "too-low-flaps"));Check(e.Phase=="Initial climb");
        });
        test("Airborne connection does not invent an observed takeoff",()=>{
            var p=new FlightPhaseTracker();var f=F(300,900);p.Update(f);Check(!p.DepartureObserved && p.Current==FlightPhase.Climb);
        });
        test("Touch-and-go returns from rollout to initial climb",()=>{
            var p=new FlightPhaseTracker();var t=DateTime.UtcNow;
            p.Update(F(10,-500) with{Time=t});p.Update(F(0,0,130,true) with{Time=t.AddSeconds(.2)});
            p.Update(F(20,900) with{Time=t.AddSeconds(.4)});Check(p.Current==FlightPhase.InitialClimb&&p.JustDeparted);
        });
        test("Approach queue is cancelled on climb but manual previews survive",()=>{
            var q=new CueQueue();var t=DateTime.UtcNow;q.Offer(new[]{Catalog.Get("minimums"),Catalog.Get("altitude-50")},t,null,t);q.CancelApproach();Check(q.Count==0);
            q.Offer(new[]{Catalog.Get("altitude-50")},t,null,t,"Airbus");q.CancelApproach();Check(q.Count==1);
        });
        test("Armed spoilers do not imply deployed spoilers on takeoff",()=>{
            var e=new WarningEngine();var s=new Settings();var t=DateTime.UtcNow;var all=new List<SoundCue>();
            for(int i=0;i<20;i++)all.AddRange(e.Update(F(0,0,50+i*3,true) with{Time=t.AddSeconds(i*.2),Spoilers=2,SpoilerPosition=0,ParkingBrake=false},s,"Boeing 747"));
            Check(!all.Any(c=>c.Id=="takeoff-config"));
        });
        test("Takeoff configuration alerts on known deployed spoilers",()=>{
            var e=new WarningEngine();var s=new Settings();var t=DateTime.UtcNow;var all=new List<SoundCue>();
            for(int i=0;i<20;i++)all.AddRange(e.Update(F(0,0,50+i*3,true) with{Time=t.AddSeconds(i*.2),SpoilerPosition=1,ParkingBrake=false},s,"Boeing 747"));
            Check(all.Any(c=>c.Id=="takeoff-config"));
        });
        test("Built-in demo reaches approach after departure protection",()=>{
            var e=new WarningEngine();var s=new Settings();var t=DateTime.UtcNow;var labels=new HashSet<string>();var cues=new List<SoundCue>();int i=0;
            foreach(var f in MainWindow.DemoFrames()){cues.AddRange(e.Update(f with{Time=t.AddSeconds(i++*.2)},s,"Airbus A320 family"));labels.Add(e.Phase);}
            Check(new[]{"Initial climb","Cruise","Approach","Flare","Landing roll"}.All(labels.Contains));Check(cues.Any(c=>c.Id=="altitude-500"));
        });
        test("Go-around suppresses landing calls but never critical pull-up",()=>{
            var e=new WarningEngine();var s=new Settings{CalloutLeadMilliseconds=0};var t=DateTime.UtcNow;var cues=new List<SoundCue>();
            void Feed(Frame f){t=t.AddSeconds(.2);cues.AddRange(e.Update(f with{Time=t},s,"Boeing 737"));}
            Feed(F(220,-600));for(int i=0;i<12;i++)Feed(F(220+i*3,900));Check(e.Phase=="Go-around");cues.Clear();
            for(int i=0;i<12;i++)Feed(F(250-i*4,-600) with{GearDown=false,Flaps=0});
            Check(!cues.Any(c=>c.Group=="Heights"||c.Id is "minimums" or "too-low-gear" or "too-low-flaps"));
            Feed(F(190,-6000) with{GearDown=false,Flaps=0});Check(cues.Any(c=>c.Id=="pull-up"));
        });
        test("Native stall still alerts below the GPWS height floor",()=>{
            var e=new WarningEngine();var s=new Settings();var t=DateTime.UtcNow;
            e.Update(F(8,-100) with{Time=t},s,"Boeing");
            var cues=e.Update(F(7,-100) with{Time=t.AddSeconds(.2),Stall=true},s,"Boeing");Check(cues.Any(c=>c.Id=="stall"));
        });
        test("Landing rollout does not trigger takeoff configuration",()=>{
            var e=new WarningEngine();var s=new Settings();var t=DateTime.UtcNow;var all=new List<SoundCue>();
            e.Update(F(10,-500) with{Time=t},s,"Boeing 747");
            for(int i=1;i<20;i++)all.AddRange(e.Update(F(0,0,140-i*3,true) with{Time=t.AddSeconds(i*.2),SpoilerPosition=1,ParkingBrake=true},s,"Boeing 747"));
            Check(!all.Any(c=>c.Id=="takeoff-config"));
        });
    }
}
