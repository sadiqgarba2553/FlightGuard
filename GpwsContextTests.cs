using System;
using System.Collections.Generic;
using System.Linq;
namespace FlightGuard;
public static class GpwsContextTests
{
    private static readonly FlapStop[] Normal={new(0,"UP",0),new(1,"5",5),new(2,"15",15),new(3,"30",30)};
    private static readonly FlapStop[] Reversed={new(0,"FULL",40),new(1,"15",15),new(2,"5",5),new(3,"UP",0)};
    private static Frame F(double h=400,double vs=-600)=>new(){Aircraft="757-200",Agl=h,Msl=h+100,Vs=vs,Ias=140,GroundSpeedKnots=140,GearDown=true,GearState=1,Flaps=3,FlapStops=Normal};
    public static void Run(Action<string,Action> test)
    {
        void Check(bool ok,string message="Context assertion failed"){if(!ok)throw new Exception(message);}
        List<SoundCue> Run(Func<int,Frame> frame,int count=80,Settings? settings=null){var e=new WarningEngine();var t=DateTime.UtcNow;var all=new List<SoundCue>();for(int i=0;i<count;i++)all.AddRange(e.Update(frame(i) with{Time=t.AddSeconds(i*.1)},settings??new(),"Boeing 757",false));return all;}
        test("Flap clean/full meaning comes from definitions, including reversed indices",()=>{
            Check(AircraftConfiguration.FlapsInsufficient(F() with{Flaps=0})==true);
            Check(AircraftConfiguration.FlapsInsufficient(F() with{Flaps=0,FlapStops=Reversed})==false);
            Check(AircraftConfiguration.FlapsInsufficient(F() with{Flaps=3,FlapStops=Reversed})==true);
        });
        test("Selected landing flap compares extension angles, not numeric order",()=>{
            Check(AircraftConfiguration.FlapsInsufficient(F() with{Flaps=0,FlapStops=Reversed},1)==false);
            Check(AircraftConfiguration.FlapsInsufficient(F() with{Flaps=2,FlapStops=Reversed},1)==true);
            Check(AircraftConfiguration.FlapsInsufficient(F(),9)==null);
        });
        test("Unknown or absent flap definitions do not invent a clean configuration",()=>{
            Check(AircraftConfiguration.FlapsInsufficient(F() with{Flaps=0,FlapStops=Array.Empty<FlapStop>()})==null);
            Check(AircraftConfiguration.FlapsInsufficient(F() with{Flaps=null})==null);
            Check(AircraftConfiguration.FlapsInsufficient(F() with{FlapStops=new[]{new FlapStop(0,"NONE",0)}})==false);
        });
        test("A fully configured landing with state zero cannot raise too-low-flaps",()=>Check(!Run(i=>F(180-i*.5) with{Flaps=0,FlapStops=Reversed}).Any(c=>c.Id=="too-low-flaps")));
        test("Verified clean flaps alert only after travel grace and persistence",()=>{
            Check(!Run(i=>F(180-i*.5) with{Flaps=0},30).Any(c=>c.Id=="too-low-flaps"));
            Check(Run(i=>F(180-i*.5) with{Flaps=0}).Any(c=>c.Id=="too-low-flaps"));
        });
        test("Selecting sufficient landing flaps clears the configuration condition",()=>{
            var context=new ConfigurationContext();var phase=new FlightPhaseTracker();var t=DateTime.UtcNow;
            for(int i=0;i<45;i++){var f=F(180) with{Time=t.AddSeconds(i*.1),Flaps=0};phase.Update(f);context.Update(f,phase,new());}
            Check(context.FlapsUnsafe);var corrected=F(180) with{Time=t.AddSeconds(4.6)};phase.Update(corrected);context.Update(corrected,phase,new());Check(!context.FlapsUnsafe);
        });
        test("One retracted-gear sample does not create a nuisance warning",()=>Check(!Run(i=>F(400-i) with{GearDown=i==20?false:true,GearState=i==20?2:1}).Any(c=>c.Id=="too-low-gear")));
        test("Established gear-up approach still produces a warning",()=>Check(Run(i=>F(400-i) with{GearDown=false,GearState=2}).Any(c=>c.Id=="too-low-gear")));
        test("Selected gear extension gets travel time and confirmed down clears it",()=>{
            var context=new ConfigurationContext();var phase=new FlightPhaseTracker();var t=DateTime.UtcNow;
            for(int i=0;i<60;i++){var f=F(450-i*.5) with{Time=t.AddSeconds(i*.1),GearDown=true,GearState=4};phase.Update(f);context.Update(f,phase,new());Check(!context.GearUnsafe);}
            var down=F(400) with{Time=t.AddSeconds(6.1)};phase.Update(down);context.Update(down,phase,new());Check(!context.GearUnsafe);
        });
        test("Stuck extending gear eventually alerts and low clearance shortens grace",()=>{
            Check(Run(i=>F(450-i*.3) with{GearState=4},160).Any(c=>c.Id=="too-low-gear"));
            Check(Run(i=>F(100-i*.1) with{GearState=4},40).Any(c=>c.Id=="too-low-gear"));
        });
        test("Climb and go-around configuration changes never produce landing alerts",()=>{
            Check(!Run(i=>F(150+i,600) with{GearDown=false,GearState=2,Flaps=0}).Any(c=>c.Id is "too-low-gear" or "too-low-flaps"));
            var e=new WarningEngine();var t=DateTime.UtcNow;var all=new List<SoundCue>();e.Update(F(250) with{Time=t},new(),"Boeing",false);
            for(int i=1;i<40;i++)all.AddRange(e.Update(F(250+i,800) with{Time=t.AddSeconds(i*.1),GearDown=false,GearState=5,Flaps=0},new(),"Boeing",false));
            Check(!all.Any(c=>c.Id is "too-low-gear" or "too-low-flaps"));
        });
        test("Fixed-gear aliases with hyphens are excluded from gear warnings",()=>{
            foreach(string aircraft in new[]{"C-172","Cessna 172","C-208B","Cessna 208 Caravan","PA-28","SR-22"})Check(Catalog.FixedGear(aircraft),aircraft);
        });
        test("Normal flat-terrain approach has no terrain or configuration nuisance",()=>{
            var all=Run(i=>F(1000-i,-600),950);Check(!all.Any(c=>c.Id is "terrain" or "pull-up" or "too-low-gear" or "too-low-flaps"));
        });
        test("An isolated AGL glitch does not become terrain or pull-up",()=>{
            var all=Run(i=>F(700-i,-600) with{Agl=700-i-(i==25?100:0)},100);Check(!all.Any(c=>c.Id is "terrain" or "pull-up"));
        });
        test("Conflicting MSL and vertical speed withhold terrain estimation",()=>{
            var trend=new TerrainTrend();var t=DateTime.UtcNow;
            for(int i=0;i<20;i++)trend.Update(F(900-i*20,-300) with{Time=t.AddSeconds(i*.1)});
            Check(!trend.Credible&&trend.Status.Contains("disagreement"));
        });
        test("Sustained rising ground still produces terrain caution",()=>{
            var all=Run(i=>F(1000-i*8,-300) with{Msl=2000-i*.5},90);Check(all.Any(c=>c.Id is "terrain" or "pull-up"));
        });
        test("Rapid terrain closure remains an emergency with landing configuration",()=>{
            var all=Run(i=>F(500-i*10,0) with{Msl=2000},45);Check(all.Any(c=>c.Id=="pull-up"));
        });
        test("Terrain history resets after a telemetry gap",()=>{
            var e=new WarningEngine();var t=DateTime.UtcNow;e.Update(F(600) with{Time=t},new(),"Boeing",false);
            e.Update(F(590) with{Time=t.AddSeconds(4)},new(),"Boeing",false);Check(e.MonitorStatus.Contains("Learning"));
        });
        test("Independent confirmation timers survive a competing marginal alert",()=>{
            var gate=new AlertPersistence();var t=DateTime.UtcNow;
            for(int i=0;i<12;i++){gate.Ready("gear",true,t.AddSeconds(i*.1),1);gate.Ready("bank",i%2==0,t.AddSeconds(i*.1),.5);}
            Check(gate.Ready("gear",true,t.AddSeconds(1.2),1));Check(!gate.Ready("bank",true,t.AddSeconds(1.2),.5));
        });
        test("Invalid flap definitions are rejected without throwing from frame validation",()=>{
            Check(!(F() with{FlapStops=null!}).Usable);Check(!(F() with{FlapStops=new FlapStop[]{null!}}).Usable);
            Check(!(F() with{FlapStops=new[]{new FlapStop(0,"UP",double.NaN)}}).Usable);Check(!(F() with{FlapStops=new[]{new FlapStop(0,"UP",0),new FlapStop(0,"FULL",40)}}).Usable);
        });
    }
}
