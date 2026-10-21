using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;

namespace FlightGuard;

public sealed class FlightToolsPanel
{
    public TabItem Tab {get;}=new(){Header="Flight tools"};
    private readonly TabControl tabs=new(){Background=Brushes.Transparent};
    private readonly CheckBox autoRecord=new(){Content="Automatically record live flights"};
    private readonly CheckBox extraCrew=new(){Content="Automatic deployment and deceleration calls"};
    private readonly CheckBox approachEnabled=new(){Content="Enable approach monitoring"};
    private readonly CheckBox approachTone=new(){Content="Play recorded caution tone when approach becomes unstable"};
    private readonly CheckBox replayAudio=new(){Content="Replay audio (off by default)"};
    private readonly ComboBox replaySpeed=new(){Width=100,ItemsSource=new[]{"1x","2x","4x","8x"},SelectedIndex=0};
    private readonly TextBlock aircraft=Text("Connect to load an aircraft's saved settings.");
    private readonly TextBlock recordStatus=Text("Recorder stopped");
    private readonly TextBlock approachStatus=Text("Approach monitor standing by");
    private readonly TextBlock crewStatus=Text("Crew telemetry standing by");
    private readonly TextBlock landingStatus=Text("No landing observed yet.");
    private readonly TextBox timeline=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=180};
    private readonly ComboBox packChoice=new(){MinWidth=360};
    private readonly TextBlock packStatus=Text("Select a source-verified pack.");
    private readonly TextBox packDetails=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Height=230};
    private readonly ListBox landingList=new(){Height=165,Background=Brushes.Transparent,Foreground=Brushes.LightGray};
    private readonly Dictionary<string,TextBox> numbers=new();
    private readonly CancellationTokenSource downloads=new();
    private readonly Func<Settings> settings;
    private readonly Func<bool> canInstall;
    private readonly Action installed;
    private readonly Action<string> open;
    private readonly PackCatalog catalog;
    private readonly CabinPanel cabinPanel;
    private readonly Button install;
    public bool Installing {get;private set;}
    public bool AutoRecord=>autoRecord.IsChecked==true;
    public bool ExtraCrew=>extraCrew.IsChecked==true;
    private static TextBlock Text(string value)=>new(){Text=value,TextWrapping=TextWrapping.Wrap,Margin=new(0,7,0,10)};
    private static Button Button(string label,Action action){var b=new Button{Content=label,Margin=new(0,0,10,8)};b.Click+=(_,_)=>action();return b;}
    private static StackPanel Row(params UIElement[] controls){var p=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,8,0,8)};foreach(var c in controls)p.Children.Add(c);return p;}
    private void Page(string name,StackPanel content){tabs.Items.Add(new TabItem{Header=name,Content=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=content,Margin=new(0,12,0,8)}});}
    public FlightToolsPanel(Func<Settings> settings,Func<bool> save,Action recordToggle,Func<List<FlightEvent>,double,bool,Task> replay,Action stop,Func<bool> canInstall,Action installed,Action<string> open,Action<string> cabinCommand)
    {
        this.settings=settings;this.canInstall=canInstall;this.installed=installed;this.open=open;
        catalog=PackCatalog.Load(Path.Combine(AppContext.BaseDirectory,"pack-catalog.json"));
        Tab.Content=tabs;Tab.Margin=new(0);
        var profile=new StackPanel();profile.Children.Add(Text("Aircraft settings"));profile.Children.Add(aircraft);
        profile.Children.Add(Text("V-speeds, minimums, warning thresholds, enabled cues, approach targets and timing lead are stored separately for the exact aircraft name. V-speeds load disarmed. Verify them for each flight. Apply saves the current aircraft; a new aircraft starts from the default settings carried into this release."));
        profile.Children.Add(Row(Button("Save current aircraft",()=>save()),Button("Open saved profiles",()=>OpenFolder("AircraftProfiles"))));
        profile.Children.Add(autoRecord);profile.Children.Add(extraCrew);profile.Children.Add(crewStatus);
        profile.Children.Add(Text("Deployment uses actual spoiler position. Decel uses sustained observed ground-speed reduction. All-engine reverse confirmation is visual and recorded; the API does not verify a green reverser indication, so the spoken reverse-green cue remains manual."));
        Page("Aircraft",profile);
        var recorder=new StackPanel();recorder.Children.Add(Text("Flight recording and replay"));recorder.Children.Add(recordStatus);
        recorder.Children.Add(Row(Button("Start / stop recording",recordToggle),Button("Open recordings",()=>OpenFolder("Recordings"))));
        var replayButton=new Button{Content="Choose recording…",Margin=new(0,0,10,8)};
        replayButton.Click+=async(_,_)=>{
            var dialog=new Microsoft.Win32.OpenFileDialog{Title="Replay a FlightGuard recording",Filter="FlightGuard recordings|*.fgflight.jsonl|JSONL files|*.jsonl",InitialDirectory=Folder("Recordings")};
            if(dialog.ShowDialog()!=true)return;
            try{var events=await Task.Run(()=>FlightRecorder.Read(dialog.FileName));timeline.Text=FlightRecorder.Summary(events);double speed=double.Parse((replaySpeed.SelectedItem?.ToString()??"1x").TrimEnd('x'),CultureInfo.InvariantCulture);await replay(events,speed,replayAudio.IsChecked==true);}catch(Exception ex){MessageBox.Show(ex.Message,"Replay");}
        };
        recorder.Children.Add(Row(replayButton,replaySpeed,Button("Stop replay / disconnect",stop)));recorder.Children.Add(replayAudio);
        recorder.Children.Add(Text("Replay disconnects from the simulator and re-evaluates recorded telemetry using recorded settings and timestamps. Long pauses are compressed for viewing. Replay does not send simulator commands, overwrite aircraft profiles or create new flight records. Original audio-start timings appear below."));recorder.Children.Add(timeline);Page("Recorder",recorder);
        var approach=new StackPanel();approach.Children.Add(Text("Unstable-approach monitor"));approach.Children.Add(approachEnabled);approach.Children.Add(approachTone);
        var fields=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};
        foreach(var (key,label) in new[]{("Gate","Monitor below · FT AGL"),("Target","Target approach speed · KT (0 disables)"),("Below","Allowed below target · KT"),("Above","Allowed above target · KT"),("Sink","Maximum descent · FPM"),("Bank","Maximum bank · DEG"),("Flaps","Minimum landing flap STATE (0 disables)"),("Hold","Condition persistence · SEC")}){
            var box=new TextBox();numbers[key]=box;var group=new StackPanel{Margin=new(0,0,18,12)};group.Children.Add(Text(label));group.Children.Add(box);fields.Children.Add(group);
        }
        approach.Children.Add(fields);approach.Children.Add(Row(Button("Apply aircraft settings",()=>save())));approach.Children.Add(approachStatus);
        approach.Children.Add(Text("These are your simulator criteria, not manufacturer performance data. Flap STATE is the simulator's position index, not degrees. Speed monitoring stays off until a target is entered. Missing configuration data is marked incomplete rather than stable. GPWS warnings retain priority."));Page("Approach",approach);
        var landings=new StackPanel();landings.Children.Add(Text("Landing reports"));landings.Children.Add(landingStatus);landings.Children.Add(Row(Button("Refresh reports",RefreshLandings),Button("Open reports folder",()=>OpenFolder("Landings")),Button("Open selected report",()=>{if(landingList.SelectedItem is string file)open(Path.Combine(Folder("Landings"),file));})));
        landings.Children.Add(landingList);landings.Children.Add(Text("Touchdown descent rate is estimated from the last airborne sample, not a measured impact force. Reports include 1000/500-ft approach snapshots when captured, warning history and estimated bounces. Gaps, pauses and aircraft changes can limit or interrupt a report."));Page("Landings",landings);
        var packs=new StackPanel();packs.Children.Add(Text("Downloadable recording packs"));packChoice.ItemsSource=catalog.Packs;packChoice.SelectedIndex=0;
        install=new Button{Content="Download / repair originals",Margin=new(10,0,0,0)};
        install.Click+=async(_,_)=>await InstallPack();packChoice.SelectionChanged+=(_,_)=>ShowPack();packs.Children.Add(Row(packChoice,install));packs.Children.Add(packStatus);
        packs.Children.Add(Text("Pinned source revisions, file hashes and audio decoding are verified before installation. This verifies the source files, not OEM authenticity. Repair replaces bundled cues and keeps a backup of the previous pack. Disconnect before replacing recordings. Missing windshear/go-around recordings are not fabricated."));packs.Children.Add(packDetails);Page("Sound packs",packs);
        cabinPanel=new(cabinCommand,save);Page("Cabin",cabinPanel.Content);
        Load(settings());ShowPack();RefreshLandings();
    }
    public void SelectPage(int index){tabs.SelectedIndex=Math.Clamp(index,0,tabs.Items.Count-1);}
    private string Folder(string name){string path=Path.Combine(Settings.DataFolder,name);Directory.CreateDirectory(path);return path;}
    private void OpenFolder(string name)=>open(Folder(name));
    public void SetAircraft(string name,bool saved)=>aircraft.Text=name+" · "+(saved?"saved aircraft settings loaded":"default settings; Apply to save this aircraft")+" · speeds require verification and arming";
    public CabinOptions ReadCabin()=>cabinPanel.Read();
    public void UpdateCabin(string description)=>cabinPanel.Update(description);
    public void Load(Settings s){cabinPanel.Load(s.Cabin);autoRecord.IsChecked=s.AutoRecord;extraCrew.IsChecked=s.ExtraCrew;approachEnabled.IsChecked=s.Approach.Enabled;approachTone.IsChecked=s.Approach.CautionTone;foreach(var pair in new[]{("Gate",s.Approach.GateFeet),("Target",s.Approach.TargetKnots),("Below",s.Approach.BelowKnots),("Above",s.Approach.AboveKnots),("Sink",s.Approach.SinkFpm),("Bank",s.Approach.BankDegrees),("Flaps",(double)s.Approach.LandingFlapsMinState),("Hold",s.Approach.PersistenceSeconds)})numbers[pair.Item1].Text=pair.Item2.ToString(CultureInfo.InvariantCulture);}
    public ApproachOptions ReadApproach(){double N(string k)=>double.TryParse(numbers[k].Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:throw new ArgumentException("Enter a number for approach "+k);var o=new ApproachOptions{Enabled=approachEnabled.IsChecked==true,CautionTone=approachTone.IsChecked==true,GateFeet=N("Gate"),TargetKnots=N("Target"),BelowKnots=N("Below"),AboveKnots=N("Above"),SinkFpm=N("Sink"),BankDegrees=N("Bank"),LandingFlapsMinState=checked((int)N("Flaps")),PersistenceSeconds=N("Hold")};if(N("Flaps")!=o.LandingFlapsMinState)throw new ArgumentException("Flap STATE must be an integer.");o.Validate();return o;}
    public void Update(FlightRecorder recorder,ApproachAssessment approach,string crew,bool replaying){recordStatus.Text=replaying?"REPLAY · live simulator disconnected":recorder.Error!=null?"Recorder error: "+recorder.Error:recorder.IsRecording?$"RECORDING · {recorder.FrameCount} frames · {recorder.CurrentPath}":"Recorder stopped";approachStatus.Text=approach.Description;crewStatus.Text=crew;}
    public void Landing(LandingResult result,string? path){landingStatus.Text=$"{result.Aircraft} · {result.TouchdownUtc.ToLocalTime():HH:mm:ss} · {result.Outcome}\nEstimated touchdown: {result.EstimatedTouchdownFpm?.ToString("0")??"unavailable"} fpm · estimated bounces: {result.EstimatedBounces}\n{result.DataQuality}";if(path!=null)RefreshLandings();}
    private void RefreshLandings(){landingList.ItemsSource=Directory.GetFiles(Folder("Landings"),"*.html").OrderByDescending(p=>p).Take(100).Select(Path.GetFileName).ToArray();}
    private void ShowPack(){if(packChoice.SelectedItem is SoundPack p)packDetails.Text=p.Profile+" · "+p.Version+"\n"+p.Note+"\n\n"+string.Join("\n\n",p.Assets.Select(a=>a.Cue+" · "+a.License+"\n"+a.Url+"\nSHA256 "+a.Sha256));}
    private async Task InstallPack(){if(!canInstall()){packStatus.Text="Disconnect live flight or replay before installing recordings.";return;}if(packChoice.SelectedItem is not SoundPack pack)return;install.IsEnabled=false;Installing=true;try{var result=await SoundPackManager.Install(pack,AppContext.BaseDirectory,new Progress<string>(s=>packStatus.Text=s),downloads.Token);installed();packStatus.Text=result.Status+$" · {result.Clips} clips · {result.Downloaded} downloaded"+(result.Backup==null?"":" · backup saved under Data/PackBackups");}catch(Exception ex){packStatus.Text="Pack unchanged: "+ex.Message;}finally{install.IsEnabled=true;Installing=false;}}
    public void Close()=>downloads.Cancel();
}
