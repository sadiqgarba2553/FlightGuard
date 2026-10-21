using System;
using System.Collections.Generic;
using System.Linq;

namespace FlightGuard;

public static class CabinTests
{
    public static void Run(Action<string,Action> test)
    {
        void Check(bool b,string message="Cabin assertion failed"){if(!b)throw new Exception(message);}
        Frame Ground(double speed=0,bool brake=true)=>new(){Aircraft="757-200",Agl=0,Msl=100,Ias=speed,GroundSpeedKnots=speed,Ground=true,ParkingBrake=brake,GearDown=true,Flaps=1};
        Frame Air(double agl=6000,double vs=0)=>Ground() with{Ground=false,GroundSpeedKnots=200,Ias=200,Agl=agl,Msl=agl+100,Vs=vs,ParkingBrake=false};
        test("Cabin starts once only after a parked-gate observation",()=>{
            var cabin=new CabinExperience();var t=DateTime.UtcNow;var options=new CabinOptions();var cues=new List<SoundCue>();
            for(int i=0;i<11;i++)cues.AddRange(cabin.Update(Ground() with{Time=t.AddSeconds(i)},"Parked",options,""));
            Check(cabin.Stage=="Boarding");Check(cues.Count(c=>c.Id=="boarding-welcome")==1);
        });
        test("Cabin never infers taxi or boarding from IAS with unknown ground speed",()=>{
            var cabin=new CabinExperience();var t=DateTime.UtcNow;
            for(int i=0;i<10;i++)Check(cabin.Update(Ground(25) with{GroundSpeedKnots=null,Time=t.AddSeconds(i)},"Taxi",new(),"").Count==0);
            Check(!cabin.StartBoarding());
        });
        test("Airborne connection skips boarding and all departure recordings",()=>{
            var cabin=new CabinExperience();var t=DateTime.UtcNow;var cues=new List<SoundCue>();
            for(int i=0;i<10;i++)cues.AddRange(cabin.Update(Air() with{Time=t.AddSeconds(i)},"Cruise",new(),""));
            Check(cabin.Stage=="Cruise");Check(cues.Count==0);Check(!cabin.StartBoarding());Check(!cabin.ReadyForDeparture());
        });
        test("Brake release completes boarding and taxi schedules safety once",()=>{
            var cabin=new CabinExperience();var t=DateTime.UtcNow;var o=new CabinOptions{Ambience=false};var cues=new List<SoundCue>();
            for(int i=0;i<4;i++)cabin.Update(Ground() with{Time=t.AddSeconds(i)},"Parked",o,"");
            cues.AddRange(cabin.Update(Ground(0,false) with{Time=t.AddSeconds(4)},"Parked",o,""));
            for(int i=5;i<12;i++)cues.AddRange(cabin.Update(Ground(10,false) with{Time=t.AddSeconds(i)},"Taxi",o,""));
            Check(cues.Count(c=>c.Id=="boarding-complete")==1);Check(cues.Count(c=>c.Id=="safety-briefing")==1);Check(cabin.Stage=="Taxi out");
        });
        test("Cabin phase cancellation removes obsolete gate announcements",()=>{
            var cabin=new CabinExperience();var t=DateTime.UtcNow;
            for(int i=0;i<4;i++)cabin.Update(Ground() with{Time=t.AddSeconds(i)},"Parked",new(),"");
            Check(cabin.Allowed("boarding-welcome"));cabin.Update(Ground(12,false) with{Time=t.AddSeconds(4)},"Taxi",new(),"");
            Check(!cabin.Allowed("boarding-welcome")&&cabin.Allowed("safety-briefing"));
        });
        test("Cabin is silent for takeoff low approach warnings and go-around",()=>{
            foreach(var item in new[]{(Ground(70,false),"Takeoff roll",""),(Air(900,-700),"Approach",""),(Air(),"Cruise","pull-up"),(Air(2000,1500),"Go-around","")}){
                var cabin=new CabinExperience();Check(cabin.Update(item.Item1,item.Item2,new(),item.Item3).Count==0);Check(cabin.Quiet);Check(!cabin.Allowed("taxi-ambience"));
            }
        });
        test("Cabin descent seats call and arrival are phase ordered",()=>{
            var cabin=new CabinExperience();var t=DateTime.UtcNow;var o=new CabinOptions{Ambience=false};var cues=new List<SoundCue>();
            void Step(Frame f,string phase){t=t.AddSeconds(1);cues.AddRange(cabin.Update(f with{Time=t},phase,o,""));}
            Step(Air(),"Cruise");Step(Air(5000,-700),"Descent");Check(cabin.Stage=="Descent");
            Step(Air(3000,-700),"Approach");Check(cabin.Stage=="Approach");Step(Air(800,-700),"Approach");
            Step(Ground(110,false),"Landing roll");Step(Ground(20,false),"Landing roll");
            for(int i=0;i<11;i++)Step(Ground(),"Parked");
            Check(cabin.Stage=="Arrived");foreach(var id in new[]{"cabin-landing","cabin-seats-landing","after-landing","doors-disarm","deboarding"})Check(cues.Count(c=>c.Id==id)==1,id);
        });
        test("Disabled or paused cabin produces no audio and other aircraft opt in",()=>{
            var cabin=new CabinExperience();var t=DateTime.UtcNow;
            for(int i=0;i<8;i++)Check(cabin.Update(Ground() with{Time=t.AddSeconds(i)},"Parked",new(){Enabled=false},"").Count==0);
            cabin.Reset();for(int i=0;i<8;i++)Check(cabin.Update(Ground() with{Aircraft="Cessna 172",Time=t.AddSeconds(i)},"Parked",new(),"").Count==0);
            cabin.Reset();for(int i=0;i<8;i++)Check(cabin.Update(Ground() with{AppState="Paused",Time=t.AddSeconds(i)},"Parked",new(),"").Count==0);
        });
        test("A new parked boarding leg resets its once-only cabin events",()=>{
            var cabin=new CabinExperience();var t=DateTime.UtcNow;var f=Ground() with{Time=t};cabin.Update(f,"Parked",new(),"");
            Check(cabin.StartBoarding());Check(cabin.Update(f with{Time=t.AddSeconds(1)},"Parked",new(),"").Any(c=>c.Id=="boarding-welcome"));
            Check(cabin.StartBoarding());Check(cabin.Update(f with{Time=t.AddSeconds(2)},"Parked",new(),"").Any(c=>c.Id=="boarding-welcome"));
        });
        test("Every cockpit cue immediately preempts a long cabin recording",()=>{
            foreach(string id in new[]{"pull-up","stall","sink-rate","v1","rotate","altitude-50","parking-brake-set"}){
                var q=new CueQueue();Check(q.Offer(new[]{Catalog.Get(id)},DateTime.UtcNow,Catalog.Get("safety-briefing"),DateTime.UtcNow));
                Check(q.Next(DateTime.UtcNow)?.Cue.Id==id);
            }
        });
        test("Cabin-only stop preserves pending GPWS and phase cleanup preserves previews",()=>{
            var q=new CueQueue();var t=DateTime.UtcNow;q.Offer(new[]{Catalog.Get("pull-up"),Catalog.Get("boarding-welcome")},t,null,default);q.CancelCabin(_=>false);
            Check(q.Next(t)?.Cue.Id=="pull-up");q.Offer(new[]{Catalog.Get("safety-briefing")},t,null,default,"Boeing 757");q.CancelCabin(_=>false);Check(q.Count==1);
        });
        test("Cabin options clone validate and all shared clips resolve by catalog",()=>{
            var settings=new Settings();var clone=Settings.Copy(settings);clone.Cabin.Volume=25;Check(settings.Cabin.Volume==55);clone.Cabin.Volume=101;bool rejected=false;try{clone.Validate();}catch{rejected=true;}Check(rejected);Check(Catalog.SoundFolders("Boeing 757").Contains("Shared Cabin"));
        });
    }
}
