using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FlightGuard;

public sealed class CueRow
{
    public bool Enabled { get; set; }
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public string Group { get; init; } = "";
    public string Source { get; init; } = "";
}

public sealed class MainWindow : Window
{
    private Settings settings = Settings.Load();
    private Settings defaults=new();
    private readonly AircraftPreferences aircraftPreferences=new(Path.Combine(Settings.DataFolder,"AircraftProfiles"));
    private readonly FlightRecorder recorder=new();
    private readonly ApproachMonitor approach=new();
    private readonly LandingAnalyzer landingAnalyzer=new();
    private readonly ExtraCrewMonitor extraCrew=new();
    private readonly CabinExperience cabin=new();
    private FlightToolsPanel tools=null!;
    private bool loadingSettings,replaying,recordingSuppressed;
    private Settings? beforeReplay;
    private bool muteBeforeReplay;
    private string familyBeforeReplay="Generic";
    private DateTime lastApproachTone;
    private string approachState="";
    private readonly WarningEngine engine = new();
    private readonly Audio audio;
    private CancellationTokenSource? session;
    private CancellationTokenSource? scan;
    private readonly DispatcherTimer watchdog;
    private Frame? latest;
    private string family = "Generic";
    private string lastAircraft = "";
    private string lastPhase = "";
    private bool demo;
    private bool shuttingDown;
    private readonly List<string> log = new();
    private readonly ObservableCollection<CueRow> rows = new();
    private string report = "No manifest yet. Connect to a flight to inspect capabilities.";
    private readonly FrameworkElement view;
    private T Get<T>(string name) where T : FrameworkElement => (T)view.FindName(name);
    private void Text(string name, string value) => Get<TextBlock>(name).Text = value;

    public MainWindow()
    {
        Title = "FlightGuard · Infinite Flight companion"; Width = 1160; Height = 850;
        MinWidth = 1000; MinHeight = 740; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#09111E"); Foreground = Brush("#E8EFF7"); FontFamily = new FontFamily("Segoe UI");
        view = (FrameworkElement)XamlReader.Parse(Xaml);
        Content = view;
        settings.SpeedsArmed = false;
        try { settings.Validate(); } catch { settings = new(); }
        audio = new(Dispatcher, settings);
        defaults=Settings.Copy(settings);
        tools=new(()=>settings,SaveSettings,ToggleRecording,StartReplay,Disconnect,()=>session==null,()=>{audio.Reload();RefreshSounds();UpdateProfile();},OpenFile,CabinCommand);
        audio.CabinAllowed=cabin.Allowed;
        audio.CabinPreviewAllowed=()=>latest==null||!latest.Active||!cabin.Quiet;
        Get<TabControl>("Tabs").Items.Add(tools.Tab);
        landingAnalyzer.Completed=result=>{string? path=null;if(!replaying&&!demo){try{path=LandingAnalyzer.Save(result,Path.Combine(Settings.DataFolder,"Landings"));AddLog("Landing report saved: "+Path.GetFileName(path));}catch(Exception ex){AddLog("Landing report could not be saved: "+ex.Message);}}tools.Landing(result,path);};
        audio.PlaybackRecorded=stamp=>{if(!replaying&&!demo)recorder.Record(new(){Kind="audio",Time=stamp.Time,Playback=stamp});};
        audio.Notice = AddLog;
        audio.Started = (cue, source) => {
            Text("LastCue", cue.Text.ToUpperInvariant()); Text("AudioSource", source);
            AddLog("AUDIO  " + cue.Id + "  ·  " + source);
        };
        LoadSettings();
        Get<Button>("Discover").Click += async (_, _) => await Discover();
        Get<Button>("Connect").Click += (_, _) => { if (session != null) Disconnect(); else Start(false); };
        Get<Button>("Demo").Click += (_, _) => { if (demo) Disconnect(); else { Disconnect(); Start(true); } };
        Get<Button>("Save").Click += (_, _) => SaveSettings();
        Get<Button>("Reset").Click += (_, _) => { engine.Reset(); audio.Stop(); AddLog("Callouts re-armed; telemetry baseline reset."); };
        Get<Button>("Mute").Click += (_, _) => { audio.Muted = !audio.Muted; audio.Stop(); Get<Button>("Mute").Content = audio.Muted ? "Unmute audio" : "Mute audio"; };
        Get<Button>("Test").Click += (_, _) => audio.Preview(Catalog.Get("gpws-test"));
        Get<Button>("Preview").Click += (_, _) => { if (Get<DataGrid>("SoundGrid").SelectedItem is CueRow row) audio.Preview(Catalog.Get(row.Id), Get<ComboBox>("PackFamily").SelectedItem?.ToString()); };
        Get<ComboBox>("PackFamily").SelectionChanged += (_, _) => RefreshSounds();
        Get<Button>("Import").Click += (_, _) => Import();
        Get<Button>("Export").Click += (_, _) => Export();
        Get<Button>("OpenGuide").Click += (_, _) => OpenFile(Path.Combine(AppContext.BaseDirectory, "START-HERE.html"));
        Get<ComboBox>("Devices").SelectionChanged += (_, _) => { if (Get<ComboBox>("Devices").SelectedItem is Device d) Get<TextBox>("Host").Text = d.Address; };
        Get<ComboBox>("Profile").SelectionChanged += (_, _) => {
            if(loadingSettings)return;
            settings.Profile = Get<ComboBox>("Profile").SelectedItem?.ToString() ?? "Auto";
            family = settings.Profile == "Auto" ? Catalog.AircraftProfile(latest?.Aircraft ?? "") : settings.Profile;
            Get<ComboBox>("PackFamily").SelectedItem = family;
            audio.Configure(settings, family); engine.Reset(); audio.Stop(); RefreshSounds(); UpdateProfile();
        };
        watchdog = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => {
            if (!replaying && session != null && latest != null && (DateTime.UtcNow - latest.Time).TotalSeconds > 3) {
                latest = null; engine.Reset(); audio.Stop(); Text("Status", "TELEMETRY STALE"); Text("Warning", "Waiting for fresh telemetry"); ClearMetrics();
            }
        }, Dispatcher);
        watchdog.Start();
        Closing += async (_, e) => { if(shuttingDown)return;e.Cancel=true;shuttingDown=true;scan?.Cancel();Disconnect();watchdog.Stop();tools.Close();await recorder.Stop("Application closed");audio.Dispose();await Task.Yield();Close(); };
        RefreshSounds(); UpdateProfile();
        AddLog("Ready. Enable Connect on your phone/tablet, then discover or enter its local IP.");
        AddLog("V-speeds start disarmed. Verify values for the current aircraft, arm, then Apply settings.");
    }
    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    private void OpenFile(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) { AddLog(e.Message); } }
    private void LoadSettings()
    {
        loadingSettings=true;
        Get<TextBox>("Host").Text = settings.Host;
        Get<ComboBox>("Profile").ItemsSource = new[]{"Auto"}.Concat(Catalog.Profiles).ToArray();
        Get<ComboBox>("Profile").SelectedItem = settings.Profile;
        foreach (var (name, n) in new[] { ("V1",settings.V1), ("VR",settings.Vr), ("V2",settings.V2), ("Minimums",settings.Minimums), ("Sink",settings.SinkRate), ("Pull",settings.PullUpRate), ("TerrainTime",settings.TerrainSeconds), ("Bank",settings.BankAngle), ("GearHeight",settings.GearHeight), ("FlapHeight",settings.FlapsHeight) }) Get<TextBox>(name).Text = n.ToString(CultureInfo.InvariantCulture);
        foreach (var (name, enabled) in new[] {("Armed",settings.SpeedsArmed),("Warnings",settings.Warnings),("Heights",settings.Heights),("Crew",settings.Crew),("Terrain",settings.Terrain),("GearWarnings",settings.GearWarnings),("FlapWarnings",settings.FlapWarnings),("Retard",settings.Retard)}) Get<CheckBox>(name).IsChecked = enabled;
        Get<Slider>("Volume").Value = settings.Volume;
        Get<TextBox>("PollMs").Text=settings.PollMilliseconds.ToString(); Get<TextBox>("LeadMs").Text=settings.CalloutLeadMilliseconds.ToString();
        Get<ComboBox>("PackFamily").ItemsSource = Catalog.Profiles; Get<ComboBox>("PackFamily").SelectedItem=family;
        tools.Load(settings);loadingSettings=false;
    }
    private bool SaveSettings()
    {
        if(replaying){AddLog("Stop replay before changing aircraft settings.");return false;}
        try {
            double Num(string name) { if (!double.TryParse(Get<TextBox>(name).Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) throw new ArgumentException("Enter a number for " + name + "."); return value; }
            bool On(string name) => Get<CheckBox>(name).IsChecked == true;
            var next = new Settings {
                Host = Get<TextBox>("Host").Text.Trim(), Profile = Get<ComboBox>("Profile").SelectedItem?.ToString() ?? "Auto",
                V1 = Num("V1"), Vr = Num("VR"), V2 = Num("V2"), Minimums = Num("Minimums"), SinkRate = Num("Sink"), PullUpRate = Num("Pull"),
                TerrainSeconds = Num("TerrainTime"), BankAngle = Num("Bank"), GearHeight = Num("GearHeight"), FlapsHeight = Num("FlapHeight"),
                SpeedsArmed = On("Armed"), Warnings = On("Warnings"), Heights = On("Heights"), Crew = On("Crew"), Terrain = On("Terrain"), GearWarnings = On("GearWarnings"), FlapWarnings = On("FlapWarnings"), Retard = On("Retard"),
                Volume = (int)Get<Slider>("Volume").Value, PollMilliseconds=(int)Num("PollMs"), CalloutLeadMilliseconds=(int)Num("LeadMs"),
                Cabin=tools.ReadCabin(), Approach=tools.ReadApproach(), AutoRecord=tools.AutoRecord, ExtraCrew=tools.ExtraCrew,
                DisabledSounds = rows.Where(x => !x.Enabled).Select(x => x.Id).ToList()
            };
            next.Save(); settings = next;
            if(latest!=null&&!demo){aircraftPreferences.Save(latest.Aircraft,settings);tools.SetAircraft(latest.Aircraft,true);}else if(!demo)defaults=Settings.Copy(settings);
            if(!settings.AutoRecord&&recorder.IsRecording){recordingSuppressed=true;_=recorder.Stop("Automatic recording disabled");}
            recorder.Record(new(){Kind="settings",Settings=Settings.Copy(settings)});
            audio.Configure(settings, family); AddLog("Settings applied. " + (settings.SpeedsArmed ? "V-speeds ARMED for this flight." : "V-speeds disarmed.")); return true;
        } catch (Exception e) { MessageBox.Show(this, e.Message, "Check settings", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
    }
    private async Task Discover()
    {
        if (scan != null) return;
        scan = new(); Get<Button>("Discover").IsEnabled = false; Text("ConnectionHelp", "Listening for your phone/tablet on UDP 15000…");
        try {
            var devices = await ConnectClient.Discover(scan.Token); Get<ComboBox>("Devices").ItemsSource = devices;
            if (devices.Count > 0) { Get<ComboBox>("Devices").SelectedIndex = 0; Text("ConnectionHelp", devices.Count + " device(s) found. Select yours and connect."); }
            else Text("ConnectionHelp", "No broadcast found. Enter your device's Wi-Fi IP. Check Connect is enabled and both devices share Wi-Fi.");
        } catch (OperationCanceledException) { }
        catch (Exception e) { Text("ConnectionHelp", "Discovery: " + e.Message + " You can still enter the device IP."); }
        finally { scan?.Dispose(); scan = null; if (!shuttingDown) Get<Button>("Discover").IsEnabled = true; }
    }
    private void Start(bool useDemo)
    {
        if(tools.Installing){AddLog("Wait for sound pack installation before connecting.");return;}
        if (!SaveSettings()) return;
        recordingSuppressed=false;demo = useDemo; session = new(); var token = session.Token;
        Get<Button>("Connect").Content = "Disconnect"; Get<Button>("Demo").Content = demo ? "Stop demo" : "Run demo";
        engine.Reset();cabin.Reset(); audio.Stop();
        if (demo) _ = RunDemo(token); else _ = RunConnect(settings.Host, token);
    }
    private async Task RunConnect(string host, CancellationToken token)
    {
        if (!System.Net.IPAddress.TryParse(host, out _)) { AddLog("Enter the device IP first, or choose Discover."); Disconnect(); return; }
        int retries = 0;
        while (!token.IsCancellationRequested)
        {
            try {
                Text("Status", "CONNECTING");
                using var client = new ConnectClient(); await client.Open(host, token);
                report = string.Join(Environment.NewLine, client.Manifest.Values.OrderBy(x => x.Path).Select(x => x.Id + "," + x.Type + "," + x.Path));
                var resolved = client.Resolve();
                Text("Capabilities", "Available: " + string.Join(", ", resolved.Keys) + "\nAutomatic glideslope, windshear, TCAS, fire and forward terrain prediction are not implemented. Use manual preview for these cues.");
                AddLog("Connected to " + host + ":10112. Loading current aircraft manifest.");
                engine.Reset();
                await client.Poll(f => Dispatcher.Invoke(() => { if (!token.IsCancellationRequested) { retries = 0; Receive(f); } }), message => Dispatcher.Invoke(() => AddLog(message)), token, settings.PollMilliseconds);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception e) {
                if (token.IsCancellationRequested) return;
                engine.Reset(); approach.Reset();extraCrew.Reset();landingAnalyzer.End("Connection lost");landingAnalyzer.Reset(); audio.Stop(); latest = null; ClearMetrics(); Text("Status", "CONNECTION LOST"); Text("Warning", "Warnings suspended · reconnecting"); AddLog(e is OperationCanceledException ? "Connection timed out. Check device IP, Wi-Fi and Connect setting." : e.Message);
                if (e is ArgumentException) { Disconnect(); return; }
                try { await Task.Delay(Math.Min(15000, 1000 * (1 << Math.Min(4, retries++))), token); } catch (OperationCanceledException) { return; }
            }
        }
    }
    private void Disconnect()
    {
        session?.Cancel(); session?.Dispose(); session = null;
        landingAnalyzer.End(replaying?"Replay stopped":"Session ended");_=recorder.Stop("Disconnected");
        demo = false; latest = null;lastAircraft="";lastApproachTone=default;approachState=""; engine.Reset();approach.Reset();extraCrew.Reset();cabin.Reset(); audio.Stop();
        if(replaying){replaying=false;if(beforeReplay!=null){settings=beforeReplay;settings.SpeedsArmed=false;family=familyBeforeReplay;beforeReplay=null;audio.Muted=muteBeforeReplay;audio.Configure(settings,family);if(!shuttingDown)LoadSettings();}}
        tools.Update(recorder,approach.Current,extraCrew.Status,false);
        if(!shuttingDown){Get<ComboBox>("Profile").IsEnabled=true;Get<Button>("Mute").Content=audio.Muted?"Unmute audio":"Mute audio";}
        if (shuttingDown) return;
        Get<Button>("Connect").Content = "Connect"; Get<Button>("Demo").Content = "Run demo"; Text("Status", "DISCONNECTED"); Text("Warning", "Ready when you are"); Text("Phase", "Standby"); ClearMetrics();
    }
    private void ClearMetrics() { Text("Agl", "—"); Text("Ias", "—"); Text("Vs", "—"); }
    private void Receive(Frame f)
    {
        bool changed = lastAircraft != f.Aircraft;
        latest=f;
        if(changed){
            if(recorder.IsRecording)_=recorder.Stop("Aircraft changed");
            landingAnalyzer.End("Aircraft changed");landingAnalyzer.Reset();approach.Reset();extraCrew.Reset();cabin.Reset();lastPhase="";
            if(!replaying){try{settings=aircraftPreferences.Load(f.Aircraft,defaults,settings);}catch(Exception ex){AddLog("Aircraft preferences unavailable: "+ex.Message);settings=Settings.Copy(defaults);settings.SpeedsArmed=false;}LoadSettings();tools.SetAircraft(f.Aircraft,aircraftPreferences.Exists(f.Aircraft));}
        }
        lastAircraft = f.Aircraft;
        latest = f; string nextFamily = settings.Profile == "Auto" ? Catalog.AircraftProfile(f.Aircraft) : settings.Profile;
        if (family != nextFamily || changed) {
            family = nextFamily; engine.Reset(); audio.Stop();
            Get<ComboBox>("PackFamily").SelectedItem = family;
            if(!replaying){settings.SpeedsArmed = false; Get<CheckBox>("Armed").IsChecked = false;}
            audio.Configure(settings, family); RefreshSounds();
            AddLog("Aircraft/profile changed. Recheck and re-arm V-speeds.");
        }
        Text("Aircraft", f.Aircraft); if(changed)UpdateProfile();
        Text("Status", replaying ? "REPLAY · LIVE DISCONNECTED" : demo ? "DEMO · SIMULATED DATA" : f.Active ? "CONNECTED · LIVE" : "SIMULATOR INACTIVE");
        Text("Agl", f.Agl.ToString("N0")); Text("Ias", f.Ias.ToString("N0")); Text("Vs", f.Vs.ToString("+0;-0;0"));
        var cues = engine.Update(f, settings, family,!replaying);
        cues.AddRange(extraCrew.Update(f,engine.Phase,settings.ExtraCrew&&settings.Crew));
        cues.AddRange(cabin.Update(f,engine.Phase,settings.Cabin,engine.ActiveWarning));
        tools.UpdateCabin(cabin.Status);
        var assessment=approach.Update(f,settings.Approach,engine.ApproachAudioAllowed);
        if(assessment.State=="Unstable"&&approachState!="Unstable"&&settings.Approach.CautionTone&&engine.ActiveWarning==""&&(f.Time-lastApproachTone).TotalSeconds>15){cues.Add(Catalog.Get("master-caution"));lastApproachTone=f.Time;}
        approachState=assessment.State;Text("ApproachStrip",assessment.Active?assessment.Description:"");
        landingAnalyzer.Update(f,assessment,engine.ActiveWarning);
        if(!replaying&&!demo&&session!=null){if(settings.AutoRecord&&!recordingSuppressed&&!recorder.IsRecording&&recorder.Error==null)_=recorder.Start(Path.Combine(Settings.DataFolder,"Recordings"),f.Aircraft,settings);recorder.Record(new(){Kind="frame",Time=f.Time,Frame=f,Phase=engine.Phase,CabinStage=cabin.Stage,Warning=engine.ActiveWarning,Cues=cues.Select(c=>c.Id).ToArray(),Approach=assessment});}
        tools.Update(recorder,assessment,extraCrew.Status,replaying);
        Text("Phase", engine.Phase);
        if(lastPhase!=engine.Phase) { lastPhase=engine.Phase; AddLog($"PHASE  {lastPhase} · {f.Agl:0} ft AGL · {f.Vs:+0;-0;0} fpm"); }
        if (!f.Active) { audio.Stop(); Text("Warning", "Paused / inactive · audio suspended"); }
        else {
            audio.SynchronizeWarning(engine.ActiveWarning);
            audio.SynchronizeApproach(engine.ApproachAudioAllowed);
            audio.SynchronizeCabin();
            if (engine.ActiveWarning != "") audio.SuppressLowerThan(70);
            audio.Enqueue(cues);
            Text("Warning", engine.ActiveWarning != "" ? Catalog.Get(engine.ActiveWarning).Text.ToUpperInvariant() : "Monitoring flight");
        }
        Text("Config", "Gear " + (f.GearDown.HasValue ? f.GearDown.Value ? "DOWN" : "UP" : "N/A") + "   ·   Flaps " + (f.Flaps?.ToString() ?? "N/A") + "   ·   Spoilers " + (f.Spoilers?.ToString() ?? "N/A") + "   ·   Bank " + f.Bank.ToString("0") + "°");
    }
    private void UpdateProfile() { Text("ProfileLabel", family.ToUpperInvariant() + "  /  " + (settings.Profile == "Auto" ? "AUTO DETECT" : "MANUAL")); Text("PackSummary", family + ": " + Catalog.All.Count(x => audio.FileFor(x.Id) != null) + " of " + Catalog.All.Count + " cues have recordings. Shared sources are labelled in the library. OEM sets remain partial."); }
    private void RefreshSounds()
    {
        rows.Clear();
        string selectedFamily = Get<ComboBox>("PackFamily").SelectedItem?.ToString() ?? family;
        foreach (var cue in Catalog.All.OrderBy(x => x.Group).ThenBy(x => x.Id)) rows.Add(new() { Enabled = !settings.DisabledSounds.Contains(cue.Id), Id = cue.Id, Text = cue.Text, Group = cue.Group, Source = audio.SourceFor(cue.Id, selectedFamily) });
        Get<DataGrid>("SoundGrid").ItemsSource = rows;
    }
    private void AddLog(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AddLog(message)); return; }
        if (shuttingDown) return;
        if(!replaying&&!demo)recorder.Record(new(){Kind="note",Note=message});
        log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        if (log.Count > 200) log.RemoveAt(0);
        Get<TextBox>("Log").Text = string.Join(Environment.NewLine, log); Get<TextBox>("Log").ScrollToEnd();
    }
    private void ApplyCabinCommand(string command){if(command=="board")cabin.StartBoarding();else if(command=="ready")cabin.ReadyForDeparture();else if(command=="stop"){settings.Cabin.Enabled=false;audio.StopCabin();tools.Load(settings);}}
    private void CabinCommand(string command){
        if(replaying||demo||session==null||latest==null){AddLog("Connect to a live flight before controlling the cabin sequence.");return;}
        ApplyCabinCommand(command);tools.UpdateCabin(cabin.Status);recorder.Record(new(){Kind="cabin-command",Note=command});
        if(command=="stop")AddLog("Cabin audio stopped; enable it and Apply to resume.");else AddLog(cabin.Status);
    }
    private void ToggleRecording() {
        if(replaying||demo||latest==null||session==null){AddLog("Connect to a live flight before recording.");return;}
        if(recorder.IsRecording){recordingSuppressed=true;_=recorder.Stop("Stopped by user");}
        else{recordingSuppressed=false;_=recorder.Start(Path.Combine(Settings.DataFolder,"Recordings"),latest.Aircraft,settings);}
    }
    private async Task StartReplay(List<FlightEvent> events,double speed,bool playAudio) {
        if(tools.Installing){AddLog("Wait for sound pack installation before replaying.");return;}Disconnect();await recorder.Stop("Replay started");
        beforeReplay=Settings.Copy(settings);familyBeforeReplay=family;muteBeforeReplay=audio.Muted;replaying=true;audio.Muted=!playAudio;Get<Button>("Mute").Content=audio.Muted?"Unmute audio":"Mute audio";Get<ComboBox>("Profile").IsEnabled=false;session=new();var replaySession=session;var token=session.Token;
        Get<Button>("Connect").Content="Stop replay";settings=Settings.Copy(events[0].Settings??settings);LoadSettings();
        DateTime prior=default;
        try{
            foreach(var e in events){
                if(token.IsCancellationRequested)break;
                if(e.Settings!=null){settings=Settings.Copy(e.Settings);audio.Configure(settings,family);LoadSettings();}
                if(e.Kind=="cabin-command"){ApplyCabinCommand(e.Note??"");}
                if(e.Frame is not Frame frame)continue;
                if(prior!=default)await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((frame.Time-prior).TotalMilliseconds/speed,0,2000)),token);
                prior=frame.Time;if(!token.IsCancellationRequested)Receive(frame);
            }
        }catch(OperationCanceledException){}finally{if(ReferenceEquals(session,replaySession)&&replaying){Disconnect();AddLog("Replay ended; live simulator remains disconnected.");}}
    }
    public async Task VerifyReplayWorkflow(string input,string output) {
        int before=ConnectClient.ConnectionAttempts;var snapshot=Settings.Copy(settings);
        await StartReplay(FlightRecorder.Read(input),1000,false);
        if(session!=null||replaying||recorder.IsRecording||ConnectClient.ConnectionAttempts!=before)throw new InvalidOperationException("Replay reached a live connection or recorder.");
        snapshot.SpeedsArmed=false;
        if(System.Text.Json.JsonSerializer.Serialize(settings)!=System.Text.Json.JsonSerializer.Serialize(snapshot))throw new InvalidOperationException("Replay changed live aircraft settings.");
        System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(new{Status="PASS",LiveConnections=ConnectClient.ConnectionAttempts-before,RecorderActive=recorder.IsRecording,LiveSettingsRestored=true,Frames=FlightRecorder.Read(input).Count(e=>e.Frame!=null)},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    }
    public void RenderTools(string path,int page){tools.SelectPage(page);Render(path,4);}
    public void CloseForRender(){shuttingDown=true;watchdog.Stop();tools.Close();audio.Dispose();Close();}

    private void Import()
    {
        string selected = Get<ComboBox>("PackFamily").SelectedItem?.ToString() ?? "Generic";
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose a sound pack with cue-id.wav files or pack.json" };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try { int count = Audio.ImportFolder(dialog.SelectedPath, selected); audio.Reload(); AddLog("Imported " + count + " WAV files into " + selected + "."); RefreshSounds(); UpdateProfile(); }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Sound import", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void Export()
    {
        try {
            string folder = Path.Combine(Settings.DataFolder, "Diagnostics"); Directory.CreateDirectory(folder);
            string prefix = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            File.WriteAllText(prefix + "-manifest.csv", report); File.WriteAllText(prefix + "-log.txt", string.Join(Environment.NewLine, log));
            AddLog("Saved manifest and log to Data/Diagnostics."); OpenFile(folder);
        } catch (Exception e) { AddLog("Export failed: " + e.Message); }
    }
    private async Task RunDemo(CancellationToken token)
    {
        AddLog("Demo: takeoff, climb, approach, terrain closure and pull-up. No simulator required.");
        Text("Capabilities", "DEMO: synthetic telemetry for a complete flight. Exit demo before connecting to your phone/tablet.");
        try {
            foreach (var frame in DemoFrames()) {
                if (token.IsCancellationRequested) return;
                Receive(frame with { Time = DateTime.UtcNow }); await Task.Delay(200, token);
            }
            AddLog("Demo complete."); Disconnect();
        } catch (OperationCanceledException) { }
    }
    public static IEnumerable<Frame> DemoFrames()
    {
        Frame Base() => new() { Aircraft = "Airbus A320 · Demonstration", Ground = true, Agl = 5, Msl = 105, GearDown = true, Flaps = 2, Spoilers = 0, ParkingBrake = true, Autopilot = false, Stall = false, Overspeed = false };
        for (int i = 0; i < 15; i++) yield return Base();
        for (int i = 0; i <= 160; i++) yield return Base() with { Ias = i, ParkingBrake = false };
        for (int i = 0; i < 160; i++) yield return Base() with { Ground = false, Ias = 165 + i * .2, Agl = 10 + i * 8, Msl = 110 + i * 8, Vs = 2400, GearDown = i < 22, Flaps = i < 100 ? 2 : 0, ParkingBrake = false };
        for(int i=0;i<20;i++)yield return Base() with{Ground=false,Ias=180,Agl=1285,Msl=1385,Vs=0,GearDown=false,Flaps=0,ParkingBrake=false};
        for (int i = 0; i < 356; i++) yield return Base() with { Ground = false, Ias = 145, Agl = 1285 - i * 3.6, Msl = 1385 - i * 3.6, Vs = -1080, Spoilers = 2, ParkingBrake = false };
        for (int i = 145; i >= 0; i--) yield return Base() with { Ias = i, Spoilers = 2, ParkingBrake = false };
        // Slow step changes keep the discontinuity detector engaged correctly; baseline before warning demonstration.
        for (int i = 0; i < 10; i++) yield return Base() with { Ground = false, Agl = 900, Msl = 1000, Ias = 210, Vs = -3000, GearDown = false, Flaps = 0 };
        for (int i = 0; i < 35; i++) yield return Base() with { Ground = false, Agl = 900 - i * 10, Msl = 1000 - i * 10, Ias = 210, Vs = -3000, GearDown = false, Flaps = 0 };
    }
    public void Render(string path, int tabIndex = 0)
    {
        Get<TabControl>("Tabs").SelectedIndex = tabIndex;
        Width = 1160; Height = 850; view.Measure(new Size(1144, 810)); view.Arrange(new Rect(0, 0, 1144, 810)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1144, 810, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
    }
    public void RenderSnapshot(Frame frame, string path) { Receive(frame with{Time=DateTime.UtcNow});Render(path); }

    private const string Xaml = """
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="#09111E">
 <Grid.Resources>
  <Style TargetType="TextBlock"><Setter Property="Foreground" Value="#E6EDF7"/><Setter Property="FontSize" Value="13"/></Style>
  <Style x:Key="Caption" TargetType="TextBlock"><Setter Property="Foreground" Value="#849AB5"/><Setter Property="FontSize" Value="11"/><Setter Property="Margin" Value="0,0,0,7"/></Style>
  <Style TargetType="Button"><Setter Property="Foreground" Value="#DDE8F7"/><Setter Property="Background" Value="#1D3047"/><Setter Property="BorderThickness" Value="0"/><Setter Property="Padding" Value="16,10"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="B" CornerRadius="6" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="B" Property="Opacity" Value=".8"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="B" Property="Opacity" Value=".4"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
  <Style TargetType="TextBox"><Setter Property="Foreground" Value="#EDF5FF"/><Setter Property="Background" Value="#0C192A"/><Setter Property="BorderBrush" Value="#2A405B"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="9,7"/><Setter Property="FontSize" Value="14"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="CaretBrush" Value="White"/></Style>
  <Style TargetType="CheckBox"><Setter Property="Foreground" Value="#CAD7E8"/><Setter Property="Margin" Value="0,0,0,12"/><Setter Property="FontSize" Value="13"/></Style>
  <Style TargetType="ComboBoxItem"><Setter Property="Background" Value="#14263B"/><Setter Property="Foreground" Value="#DCE8F6"/><Setter Property="Padding" Value="9,6"/></Style>
  <Style TargetType="ComboBox"><Setter Property="Foreground" Value="#DCE8F6"/><Setter Property="MinHeight" Value="34"/><Setter Property="FontSize" Value="13"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid><ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="#13253A" BorderBrush="#2A405B" BorderThickness="1" CornerRadius="4"><TextBlock Text="⌄" Foreground="#93AFC9" HorizontalAlignment="Right" Margin="0,0,9,4" VerticalAlignment="Center"/></Border></ControlTemplate></ToggleButton.Template></ToggleButton><ContentPresenter IsHitTestVisible="False" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" Margin="10,6,26,6" VerticalAlignment="Center"/><Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False"><Border Background="#14263B" BorderBrush="#385572" BorderThickness="1" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"><ScrollViewer MaxHeight="260"><ItemsPresenter/></ScrollViewer></Border></Popup></Grid></ControlTemplate></Setter.Value></Setter></Style>
  <Style TargetType="TabItem"><Setter Property="Foreground" Value="#96ABC5"/><Setter Property="Padding" Value="18,10"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem"><Border x:Name="Tab" BorderThickness="0,0,0,2" BorderBrush="Transparent" Padding="{TemplateBinding Padding}"><ContentPresenter ContentSource="Header"/></Border><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="Tab" Property="BorderBrush" Value="#55DBC4"/><Setter Property="Foreground" Value="#55DBC4"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
  <Style TargetType="DataGridColumnHeader"><Setter Property="Background" Value="#1A2B40"/><Setter Property="Foreground" Value="#9EB3CE"/><Setter Property="Padding" Value="8,10"/><Setter Property="BorderThickness" Value="0"/></Style>
  <Style TargetType="DataGridCell"><Setter Property="BorderThickness" Value="0"/><Setter Property="Padding" Value="6,5"/></Style>
 </Grid.Resources>
 <Grid.RowDefinitions><RowDefinition Height="76"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="30"/></Grid.RowDefinitions>
 <Grid Margin="28,20"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
  <StackPanel Orientation="Horizontal"><Border Background="#55DBC4" CornerRadius="7" Width="38" Height="38" Margin="0,0,12,0"><Viewbox Margin="5"><Canvas Width="64" Height="64"><Path Fill="#092329" Data="M32,3 L57,13 L55,37 Q52,51 32,61 Q12,51 9,37 L7,13 Z"/><Path Fill="#55DBC4" Data="M30,14 L34,14 L37,29 L49,37 L49,42 L35,36 L35,47 L41,51 L41,54 L32,51 L23,54 L23,51 L29,47 L29,36 L15,42 L15,37 L27,29 Z"/></Canvas></Viewbox></Border><StackPanel VerticalAlignment="Center"><TextBlock Text="FLIGHTGUARD" FontSize="19" FontWeight="SemiBold"/><TextBlock Text="INFINITE FLIGHT  /  AURAL COMPANION" FontSize="10" Foreground="#849AB5" Margin="0,4,0,0"/></StackPanel></StackPanel>
  <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center"><TextBlock x:Name="Status" Text="DISCONNECTED" Foreground="#55DBC4" FontSize="11" VerticalAlignment="Center" Margin="0,0,18,0"/><Button x:Name="OpenGuide" Content="Setup guide"/></StackPanel>
 </Grid>
 <Border Grid.Row="1" Background="#101E30" BorderBrush="#20334A" BorderThickness="1" CornerRadius="10" Margin="28,0,28,16" Padding="18,14">
  <StackPanel><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="165"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
   <TextBox x:Name="Host" ToolTip="Phone/tablet local Wi-Fi IP address" Margin="0,0,10,0"/>
   <ComboBox x:Name="Devices" Grid.Column="1" Margin="0,0,10,0" ToolTip="Discovered Infinite Flight devices"/>
   <Button x:Name="Discover" Grid.Column="2" Content="Discover" Margin="0,0,10,0"/>
   <Button x:Name="Connect" Grid.Column="3" Content="Connect" Foreground="#092329" Background="#55DBC4" Margin="0,0,10,0"/>
   <Button x:Name="Demo" Grid.Column="4" Content="Run demo"/>
  </Grid><TextBlock x:Name="ConnectionHelp" Text="Same Wi-Fi · Enable Infinite Flight Connect in the game's Settings → General · TCP 10112" FontSize="11" Foreground="#849AB5" Margin="0,10,0,0" TextWrapping="Wrap"/></StackPanel>
 </Border>
 <TabControl x:Name="Tabs" Grid.Row="2" Background="Transparent" BorderThickness="0" Margin="28,0,28,0">
  <TabItem Header="Flight deck"><ScrollViewer VerticalScrollBarVisibility="Auto"><StackPanel Margin="0,18,0,0">
   <Grid Margin="0,0,0,12"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="230"/></Grid.ColumnDefinitions><StackPanel><TextBlock x:Name="ProfileLabel" Text="GENERIC / AUTO DETECT" Style="{StaticResource Caption}"/><TextBlock x:Name="Aircraft" Text="Connect your aircraft to begin." FontSize="22" FontWeight="SemiBold"/></StackPanel><ComboBox Grid.Column="1" x:Name="Profile" VerticalAlignment="Center"/></Grid>
   <UniformGrid Columns="3" Margin="0,0,-12,14">
    <Border Background="#111F32" CornerRadius="8" Padding="18" Margin="0,0,12,0"><StackPanel><TextBlock Text="HEIGHT ABOVE GROUND" Style="{StaticResource Caption}"/><StackPanel Orientation="Horizontal"><TextBlock x:Name="Agl" Text="—" FontSize="42" FontWeight="Light"/><TextBlock Text="FT" Foreground="#849AB5" Margin="10,0,0,8" VerticalAlignment="Bottom"/></StackPanel></StackPanel></Border>
    <Border Background="#111F32" CornerRadius="8" Padding="18" Margin="0,0,12,0"><StackPanel><TextBlock Text="INDICATED AIRSPEED" Style="{StaticResource Caption}"/><StackPanel Orientation="Horizontal"><TextBlock x:Name="Ias" Text="—" FontSize="42" FontWeight="Light"/><TextBlock Text="KT" Foreground="#849AB5" Margin="10,0,0,8" VerticalAlignment="Bottom"/></StackPanel></StackPanel></Border>
    <Border Background="#111F32" CornerRadius="8" Padding="18" Margin="0,0,12,0"><StackPanel><TextBlock Text="VERTICAL SPEED" Style="{StaticResource Caption}"/><StackPanel Orientation="Horizontal"><TextBlock x:Name="Vs" Text="—" FontSize="42" FontWeight="Light"/><TextBlock Text="FPM" Foreground="#849AB5" Margin="10,0,0,8" VerticalAlignment="Bottom"/></StackPanel></StackPanel></Border>
   </UniformGrid>
   <Border Background="#142A32" BorderBrush="#2B5052" BorderThickness="1" CornerRadius="8" Padding="18" Margin="0,0,0,16"><Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><StackPanel><TextBlock x:Name="Phase" Text="Standby" Foreground="#55DBC4" FontSize="11"/><TextBlock x:Name="Warning" Text="Ready when you are" FontSize="22" FontWeight="SemiBold" Margin="0,5,0,6"/><TextBlock x:Name="Config" Text="Live aircraft configuration appears here." Foreground="#8FACB8" FontSize="11"/><TextBlock x:Name="ApproachStrip" Text="" Foreground="#E8BE73" FontSize="11" TextWrapping="Wrap" Margin="0,5,0,0"/></StackPanel><StackPanel Grid.Column="1" HorizontalAlignment="Right" VerticalAlignment="Center"><TextBlock Text="LAST AUDIO" Foreground="#8FACB8" FontSize="10" HorizontalAlignment="Right"/><TextBlock x:Name="LastCue" Text="—" FontSize="14" Margin="15,5,0,2"/><TextBlock x:Name="AudioSource" Text="" FontSize="10" Foreground="#8FACB8" HorizontalAlignment="Right"/></StackPanel></Grid></Border>
   <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="18"/><ColumnDefinition Width="320"/></Grid.ColumnDefinitions>
    <Border Background="#101D2D" CornerRadius="8" Padding="18"><StackPanel><TextBlock Text="FLIGHT CALLOUTS" Style="{StaticResource Caption}"/>
     <UniformGrid Columns="4" Margin="0,7,-10,12"><StackPanel Margin="0,0,10,0"><TextBlock Text="V1 · KT" Style="{StaticResource Caption}"/><TextBox x:Name="V1"/></StackPanel><StackPanel Margin="0,0,10,0"><TextBlock Text="ROTATE · KT" Style="{StaticResource Caption}"/><TextBox x:Name="VR"/></StackPanel><StackPanel Margin="0,0,10,0"><TextBlock Text="V2 · KT" Style="{StaticResource Caption}"/><TextBox x:Name="V2"/></StackPanel><StackPanel Margin="0,0,10,0"><TextBlock Text="MIN · FT AGL" Style="{StaticResource Caption}"/><TextBox x:Name="Minimums"/></StackPanel></UniformGrid>
     <CheckBox x:Name="Armed" Content="Arm these V-speeds for the current aircraft"/><TextBlock Text="Enter speeds for aircraft, weight and conditions. Values shown are placeholders; they are not performance calculations." FontSize="11" Foreground="#849AB5" TextWrapping="Wrap"/>
     <StackPanel Orientation="Horizontal" Margin="0,16,0,0"><Button x:Name="Save" Content="Apply settings" Background="#55DBC4" Foreground="#092329" Margin="0,0,10,0"/><Button x:Name="Reset" Content="Re-arm callouts"/></StackPanel>
    </StackPanel></Border>
    <Border Grid.Column="2" Background="#101D2D" CornerRadius="8" Padding="18"><StackPanel><TextBlock Text="AUDIO" Style="{StaticResource Caption}"/><TextBlock x:Name="PackSummary" TextWrapping="Wrap" FontSize="12" Margin="0,4,0,10"/><Slider x:Name="Volume" Minimum="0" Maximum="100" TickFrequency="5" IsSnapToTickEnabled="True" Margin="0,0,0,14" ToolTip="Volume; apply settings to save"/><StackPanel Orientation="Horizontal"><Button x:Name="Mute" Content="Mute audio" Margin="0,0,10,0"/><Button x:Name="Test" Content="Test audio"/></StackPanel></StackPanel></Border>
   </Grid>
   <TextBlock Text="Terrain alerts estimate closure below the aircraft. Forward terrain, TCAS and windshear prediction are unavailable." FontSize="11" Foreground="#849AB5" TextWrapping="Wrap" Margin="2,14,0,16"/>
  </StackPanel></ScrollViewer></TabItem>
  <TabItem Header="Warning settings"><ScrollViewer VerticalScrollBarVisibility="Auto"><Grid Margin="0,22,0,0"><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="35"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
   <StackPanel><TextBlock Text="SIMULATOR WARNING THRESHOLDS" Style="{StaticResource Caption}"/><TextBlock Text="Adjust to your flight" FontSize="23" Margin="0,0,0,15"/>
    <UniformGrid Columns="2"><StackPanel Margin="0,0,12,15"><TextBlock Text="Sink reference at 500 ft · FPM" Style="{StaticResource Caption}"/><TextBox x:Name="Sink"/></StackPanel><StackPanel Margin="0,0,12,15"><TextBlock Text="Pull-up reference at 500 ft · FPM" Style="{StaticResource Caption}"/><TextBox x:Name="Pull"/></StackPanel><StackPanel Margin="0,0,12,15"><TextBlock Text="Terrain closure horizon · SEC" Style="{StaticResource Caption}"/><TextBox x:Name="TerrainTime"/></StackPanel><StackPanel Margin="0,0,12,15"><TextBlock Text="Bank threshold · DEG" Style="{StaticResource Caption}"/><TextBox x:Name="Bank"/></StackPanel><StackPanel Margin="0,0,12,15"><TextBlock Text="Gear warning height · FT AGL" Style="{StaticResource Caption}"/><TextBox x:Name="GearHeight"/></StackPanel><StackPanel Margin="0,0,12,15"><TextBlock Text="Flaps warning height · FT AGL" Style="{StaticResource Caption}"/><TextBox x:Name="FlapHeight"/></StackPanel></UniformGrid>
    <TextBlock Text="Sink and pull-up use continuous height-based envelopes, scaled by these 500-ft reference settings. These are configurable simulator approximations, not certified aircraft envelopes. Minimums use height AGL. Apply changes from the Flight deck tab." TextWrapping="Wrap" Foreground="#849AB5" FontSize="12"/>
   </StackPanel>
   <StackPanel Grid.Column="2"><TextBlock Text="AUTOMATIC AUDIO" Style="{StaticResource Caption}"/><CheckBox x:Name="Warnings" Content="GPWS and flight warnings" Margin="0,12,0,14"/><CheckBox x:Name="Heights" Content="Height and minimums callouts"/><CheckBox x:Name="Crew" Content="Co-pilot and configuration callouts"/><CheckBox x:Name="Terrain" Content="Local terrain closure estimate"/><CheckBox x:Name="GearWarnings" Content="Too low gear (retractable aircraft)"/><CheckBox x:Name="FlapWarnings" Content="Too low flaps (zero flap setting)"/><CheckBox x:Name="Retard" Content="Airbus retard at 20 ft (simplified)"/>
    <TextBlock Text="RECORDING TIMING" Style="{StaticResource Caption}" Margin="0,15,0,8"/><UniformGrid Columns="2"><StackPanel Margin="0,0,12,0"><TextBlock Text="Poll interval · MS" Style="{StaticResource Caption}"/><TextBox x:Name="PollMs"/></StackPanel><StackPanel><TextBlock Text="Callout lead · MS" Style="{StaticResource Caption}"/><TextBox x:Name="LeadMs"/></StackPanel></UniformGrid><TextBlock Text="Preloaded recordings only. Timing compensation: 0–250 ms. Poll interval: 50–500 ms; reconnect after changing it. V1, rotate and heights interrupt routine crew calls. Missing recordings remain silent." Foreground="#849AB5" TextWrapping="Wrap" Margin="0,12,0,0"/>
   </StackPanel>
  </Grid></ScrollViewer></TabItem>
  <TabItem Header="Sound library"><Grid Margin="0,18,0,0"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
   <Grid Margin="0,0,0,14"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="190"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><TextBlock Text="Recordings &amp; callouts" FontSize="22" VerticalAlignment="Center"/><ComboBox x:Name="PackFamily" Grid.Column="1" Margin="0,0,10,0"/><Button x:Name="Import" Content="Import WAV pack" Grid.Column="2" Margin="0,0,10,0"/><Button x:Name="Preview" Content="Preview selected" Grid.Column="3"/></Grid>
   <DataGrid x:Name="SoundGrid" Grid.Row="1" AutoGenerateColumns="False" CanUserAddRows="False" CanUserDeleteRows="False" CanUserResizeRows="False" HeadersVisibility="Column" GridLinesVisibility="None" Background="#0E1B2B" Foreground="#DCE8F6" RowBackground="#101E30" AlternatingRowBackground="#122237" BorderThickness="0" SelectionMode="Single" RowHeight="35"><DataGrid.Columns><DataGridCheckBoxColumn Header="On" Binding="{Binding Enabled, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" Width="45"/><DataGridTextColumn Header="Cue ID" Binding="{Binding Id}" Width="190" IsReadOnly="True"/><DataGridTextColumn Header="Callout / effect" Binding="{Binding Text}" Width="230" IsReadOnly="True"/><DataGridTextColumn Header="Group" Binding="{Binding Group}" Width="155" IsReadOnly="True"/><DataGridTextColumn Header="Audio source" Binding="{Binding Source}" Width="*" MinWidth="260" IsReadOnly="True"/></DataGrid.Columns></DataGrid>
   <TextBlock Grid.Row="2" Text="Preview uses the selected family; automatic audio follows the flight profile. Apply settings to save cue toggles. Automatic triggers depend on telemetry and enabled monitors; missing clips stay silent. Import cue-id.wav files or pack.json." FontSize="11" Foreground="#849AB5" TextWrapping="Wrap" Margin="0,14,0,10"/>
  </Grid></TabItem>
  <TabItem Header="Connection &amp; diagnostics"><Grid Margin="0,20,0,0"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
   <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><TextBlock Text="Know what the app can see" FontSize="22"/><Button x:Name="Export" Grid.Column="1" Content="Export diagnostics"/></Grid>
   <TextBlock Grid.Row="1" x:Name="Capabilities" Text="Connect to load the current aircraft's state manifest. Missing optional states disable the related automatic callouts." TextWrapping="Wrap" Foreground="#94ABC7" Margin="0,14,0,18"/>
   <TextBox Grid.Row="2" x:Name="Log" IsReadOnly="True" VerticalContentAlignment="Top" AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto" FontFamily="Consolas" FontSize="12" Margin="0,0,0,12"/>
  </Grid></TabItem>
 </TabControl>
 <TextBlock Grid.Row="3" Text="FLIGHTGUARD v0.5   ·   sadiq_ibraheem / INFINITE FLIGHT COMMUNITY   ·   SIMULATOR USE ONLY" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="#526D8E" FontSize="10"/>
</Grid>
""";
}
