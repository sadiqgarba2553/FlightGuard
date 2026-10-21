using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FlightGuard;

public sealed class CabinPanel
{
    private readonly CheckBox enabled=new(){Content="Enable recorded cabin experience"};
    private readonly CheckBox automatic=new(){Content="Start boarding automatically after 3 seconds parked at the gate"};
    private readonly CheckBox ambience=new(){Content="Boarding and taxi ambience"};
    private readonly CheckBox otherAircraft=new(){Content="Also enable for general aviation / other aircraft"};
    private readonly Slider volume=new(){Minimum=0,Maximum=100,Value=55,Width=280,HorizontalAlignment=HorizontalAlignment.Left};
    private readonly TextBlock status=new(){Text="Connect at a parked gate to begin.",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Turquoise,Margin=new(0,16,0,12),FontSize=16};
    public StackPanel Content {get;}=new();
    private static TextBlock Text(string s)=>new(){Text=s,TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,12)};
    public CabinPanel(Action<string> command,Func<bool> save)
    {
        Content.Children.Add(Text("Gate to gate · recorded cabin experience"));Content.Children.Add(status);
        Content.Children.Add(enabled);Content.Children.Add(automatic);Content.Children.Add(ambience);Content.Children.Add(otherAircraft);
        Content.Children.Add(Text("Cabin level · percentage of your main audio volume"));Content.Children.Add(volume);
        var row=new WrapPanel{Margin=new(0,16,0,10)};
        foreach(var (label,key) in new[]{("Start new boarding leg","board"),("Boarding complete / pushback","ready"),("Stop cabin audio","stop")}){
            var button=new Button{Content=label,Margin=new(0,0,10,10)};button.Click+=(_,_)=>command(key);row.Children.Add(button);
        }
        var apply=new Button{Content="Apply cabin settings",Margin=new(0,0,10,10)};apply.Click+=(_,_)=>save();row.Children.Add(apply);Content.Children.Add(row);
        Content.Children.Add(Text("Automatic sequence: parked gate → boarding → brake release / pushback preparation → taxi safety briefing → cabin ready at a taxi stop → climb / cruise → descent preparation → landing → taxi-in announcement → parked arrival / deboarding."));
        Content.Children.Add(Text("GPWS and cockpit callouts interrupt cabin audio immediately. Cabin stays quiet during takeoff, go-around and below 1,500 ft airborne. Interrupted announcements are not resumed. Taxi ambience requires observed ground speed; missing data is never replaced with indicated airspeed."));
        Content.Children.Add(Text("Start a new leg only when parked with the brake set. Connecting in the air skips boarding. Pushback preparation follows brake release or your button; actual tug motion, passenger count, door locks and runway clearance are not inferred. These controls schedule local audio and do not change the simulator."));
        Content.Children.Add(Text("Shared recordings: FlyByWire Simulations A32NX Wwise project · CC BY-NC-SA 4.0. Some recordings contain their original aircraft/airline wording. Use your own authorised WAVs through Sound library to customise. There is no speech synthesis."));
        Content.Children.Add(Text("Created for Infinite Flight Community member sadiq_ibraheem · github.com/sadiqgarba2553/FlightGuard"));
    }
    public void Load(CabinOptions s){enabled.IsChecked=s.Enabled;automatic.IsChecked=s.AutoBoarding;ambience.IsChecked=s.Ambience;otherAircraft.IsChecked=s.IncludeOtherAircraft;volume.Value=s.Volume;}
    public CabinOptions Read()=>new(){Enabled=enabled.IsChecked==true,AutoBoarding=automatic.IsChecked==true,Ambience=ambience.IsChecked==true,IncludeOtherAircraft=otherAircraft.IsChecked==true,Volume=(int)volume.Value};
    public void Update(string description)=>status.Text=description;
}
