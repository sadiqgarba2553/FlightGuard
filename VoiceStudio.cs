using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FlightGuard;

public sealed class VoiceStudio
{
    private readonly ComboBox aircraft=Choice(Catalog.Profiles),gpws=Choice(VoiceOptions.Choices),crew=Choice(VoiceOptions.Choices),cabin=Choice(VoiceOptions.Choices),height=Choice(VoiceOptions.HeightModes),compare=Choice(VoiceOptions.Choices.Where(x=>x!="Auto").ToArray());
    private readonly TextBox search=new(){MinWidth=200},custom=new(){Text="2500,1000,500,400,300,200,100,50,40,30,20,10"};
    private readonly ComboBox filter=Choice(new[]{"All cues","GPWS","Co-pilot","Cabin","Missing / invalid","Shared fallback"});
    private readonly Slider gpwsVolume=Level(),crewVolume=Level();
    private readonly CheckBox quiet=new(){Content="Keep routine co-pilot calls quiet below 500 ft on approach"};
    private readonly TextBlock summary=Text("Select an aircraft to inspect its recordings."),detail=Text("Select a cue to inspect its source and licence. Model pack does not mean certified OEM recording."),schedule=Text("");
    private readonly DataGrid grid=new(){AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Single,MinHeight=190,Height=250,Background=new SolidColorBrush(Color.FromRgb(14,27,43)),Foreground=Brushes.White,RowBackground=new SolidColorBrush(Color.FromRgb(16,30,48)),AlternatingRowBackground=new SolidColorBrush(Color.FromRgb(18,34,55)),GridLinesVisibility=DataGridGridLinesVisibility.None,HeadersVisibility=DataGridHeadersVisibility.Column};
    private readonly Func<Settings> settings;
    private readonly Func<bool> save,canEdit;
    private readonly Action refresh;
    private readonly Action<string> notice;
    private readonly Action<SoundCue,string> preview;
    private bool loading;
    private bool scanning;
    private int scanGeneration;
    private readonly TabControl pages=new(){Background=Brushes.Transparent,BorderThickness=new(0),Foreground=Brushes.White};
    private VoiceAsset[] rows=Array.Empty<VoiceAsset>();
    public TabItem Tab {get;}=new(){Header="Voice studio"};
    public VoiceStudio(Func<Settings> settings,Func<bool> save,Func<bool> canEdit,Action refresh,Action<string> notice,Action<SoundCue,string> preview){
        this.settings=settings;this.save=save;this.canEdit=canEdit;this.refresh=refresh;this.notice=notice;this.preview=preview;
        var body=new StackPanel{Margin=new(0,16,0,12)};Tab.Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        body.Children.Add(new TextBlock{Text="Aircraft voices & recording health",FontSize=23,Margin=new(0,0,0,8)});
        body.Children.Add(Text("GPWS, co-pilot and cabin selections save with each aircraft. Browse another model to compare packs; Apply still saves the current flight's settings. Shared fallbacks stay audible and are labelled."));
        var setup=new StackPanel();var library=new StackPanel();pages.Items.Add(new TabItem{Header="Voices & heights",Content=setup});pages.Items.Add(new TabItem{Header="Library & compare",Content=library});body.Children.Add(pages);
        body.Children.Insert(2,Field("Browse aircraft / model",aircraft));
        var controls=new UniformGrid{Columns=3};controls.Children.Add(Field("GPWS + height voice",gpws));controls.Children.Add(Field("Co-pilot voice",crew));controls.Children.Add(Field("Cabin voice",cabin));controls.Children.Add(Field("GPWS level · % of main",gpwsVolume));controls.Children.Add(Field("Co-pilot level · % of main",crewVolume));setup.Children.Add(controls);
        var heights=new UniformGrid{Columns=2};heights.Children.Add(Field("Height schedule",height));heights.Children.Add(Field("Custom heights · feet AGL, comma separated",custom));setup.Children.Add(heights);setup.Children.Add(quiet);setup.Children.Add(schedule);
        var apply=new WrapPanel();apply.Children.Add(Button("Apply voices & height schedule",()=>save()));apply.Children.Add(Button("Use suggested heights",()=>{height.SelectedItem="Suggested by aircraft";UpdateSchedule();}));apply.Children.Add(Button("Refresh inventory",Scan));setup.Children.Add(apply);library.Children.Add(summary);
        var find=new UniformGrid{Columns=3};find.Children.Add(Field("Search cue / source / licence",search));find.Children.Add(Field("Show",filter));find.Children.Add(Field("Compare voice B",compare));library.Children.Add(find);
        foreach(var (header,path,width) in new[]{("Cue","Cue",170d),("Role","Role",80d),("Recording source","Source",180d),("Coverage","Coverage",220d),("Licence","License",145d),("Seconds","Duration",70d)})grid.Columns.Add(new DataGridTextColumn{Header=header,Binding=new System.Windows.Data.Binding(path),Width=width});
        library.Children.Add(grid);library.Children.Add(detail);
        var actions=new WrapPanel();actions.Children.Add(Button("A · play resolved source",()=>Play(false)));actions.Children.Add(Button("B · play comparison",()=>Play(true)));actions.Children.Add(Button("Export coverage CSV",Export));actions.Children.Add(Button("Create pack template",Template));actions.Children.Add(Button("Import into browsed model",Import));actions.Children.Add(Button("Undo latest import",Undo));library.Children.Insert(2,actions);
        body.Children.Add(Text("Imports accept authorised WAV recordings and optional pack.json / pack-info.json. Every file is decoded before switching packs; an undo backup stays in Data. Original files remain unchanged: playback trims dead air and balances peaks. No Windows speech. Exact OEM coverage remains incomplete."));
        aircraft.SelectedItem="Boeing 757";compare.SelectedItem="Boeing 787";filter.SelectedIndex=0;
        aircraft.SelectionChanged+=(_,_)=>{if(!loading)Scan();};filter.SelectionChanged+=(_,_)=>Filter();search.TextChanged+=(_,_)=>Filter();height.SelectionChanged+=(_,_)=>UpdateSchedule();grid.SelectionChanged+=(_,_)=>{if(grid.SelectedItem is VoiceAsset r)detail.Text=$"{r.Label} · requested {r.Requested} → {r.Source} · {r.Duration:0.00}s after {r.LeadingSilence:0}ms leading trim\n{r.License} · {r.Origin}\nSHA-256 {r.Sha256}";};Tab.GotFocus+=(_,_)=>{if(rows.Length==0&&!scanning)Scan();};
    }
    private static ComboBox Choice(string[] values)=>new(){ItemsSource=values,MinWidth=160,HorizontalAlignment=HorizontalAlignment.Stretch};
    private static Slider Level()=>new(){Minimum=0,Maximum=100,Value=100,TickFrequency=5,IsSnapToTickEnabled=true,ToolTip="Percentage of main audio volume"};
    private static TextBlock Text(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(148,171,199)),Margin=new(0,6,0,8),FontSize=12};
    private static StackPanel Field(string label,FrameworkElement control){var p=new StackPanel{Margin=new(0,0,12,10)};p.Children.Add(Text(label));p.Children.Add(control);return p;}
    private static Button Button(string label,Action action){var b=new Button{Content=label,Margin=new(0,4,8,8)};b.Click+=(_,_)=>action();return b;}
    public VoiceOptions Read(){var v=new VoiceOptions{GpwsVoice=gpws.SelectedItem?.ToString()??"Auto",CrewVoice=crew.SelectedItem?.ToString()??"Auto",CabinVoice=cabin.SelectedItem?.ToString()??"Auto",GpwsVolume=(int)gpwsVolume.Value,CrewVolume=(int)crewVolume.Value,HeightMode=height.SelectedItem?.ToString()??"All existing",CustomHeights=custom.Text,SuppressRoutineCrewOnFinal=quiet.IsChecked==true};v.Validate();return v;}
    public void Load(VoiceOptions v){loading=true;gpws.SelectedItem=v.GpwsVoice;crew.SelectedItem=v.CrewVoice;cabin.SelectedItem=v.CabinVoice;height.SelectedItem=v.HeightMode;custom.Text=v.CustomHeights;gpwsVolume.Value=v.GpwsVolume;crewVolume.Value=v.CrewVolume;quiet.IsChecked=v.SuppressRoutineCrewOnFinal;loading=false;rows=Array.Empty<VoiceAsset>();UpdateSchedule();}
    public void SetAircraft(string profile){if(aircraft.SelectedItem?.ToString()!=profile)aircraft.SelectedItem=profile;}
    private void UpdateSchedule(){try{var v=Read();schedule.Text="Schedule: "+string.Join(" · ",v.Schedule(aircraft.SelectedItem?.ToString()??"Generic"))+" ft. Suggested templates are editable simulator choices; airline/OEM configuration is not verified. Minimums remain separate.";}catch(Exception ex){schedule.Text=ex.Message;}}
    public void Scan()=>_=ScanAsync();
    private async Task ScanAsync(){int generation=++scanGeneration;scanning=true;try{string profile=aircraft.SelectedItem?.ToString()??"Boeing 757";var s=Settings.Copy(settings());s.Voices=Read();summary.Text="Checking recordings in the background…";var result=await Task.Run(()=>new VoiceInventory(AppContext.BaseDirectory).Scan(s,profile));if(generation!=scanGeneration)return;rows=result;summary.Text=VoiceInventory.Summary(rows);Filter();UpdateSchedule();}catch(Exception ex){notice("Voice inventory: "+ex.Message);}finally{if(generation==scanGeneration)scanning=false;}}
    public void RenderPage(int page){scanGeneration++;var s=Settings.Copy(settings());s.Voices=Read();rows=new VoiceInventory(AppContext.BaseDirectory).Scan(s,aircraft.SelectedItem?.ToString()??"Boeing 757");summary.Text=VoiceInventory.Summary(rows);Filter();pages.SelectedIndex=page;}
    private void Filter(){string q=search.Text.Trim(),f=filter.SelectedItem?.ToString()??"All cues";grid.ItemsSource=rows.Where(r=>(q==""||$"{r.Cue} {r.Label} {r.Source} {r.License}".Contains(q,StringComparison.OrdinalIgnoreCase))&&(f=="All cues"||f==r.Role||f=="Missing / invalid"&&(r.Duration<=0)||f=="Shared fallback"&&r.Coverage.StartsWith("Shared"))).ToArray();}
    private void Play(bool b){if(grid.SelectedItem is not VoiceAsset r){notice("Select a cue first.");return;}string profile=b?compare.SelectedItem?.ToString()??"Generic":r.Source;if(profile=="Missing"||r.Duration<=0&&!b){notice("No usable recording for this selection.");return;}preview(Catalog.Get(r.Cue),profile);}
    private string? Pick(string description){using var dialog=new System.Windows.Forms.FolderBrowserDialog{Description=description};return dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK?dialog.SelectedPath:null;}
    private async void Export(){try{string profile=aircraft.SelectedItem?.ToString()??"Generic";var snapshot=Settings.Copy(settings());snapshot.Voices=Read();var report=await Task.Run(()=>new VoiceInventory(AppContext.BaseDirectory).Scan(snapshot,profile));string folder=Path.Combine(Settings.DataFolder,"VoiceReports");Directory.CreateDirectory(folder);string path=Path.Combine(folder,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+profile+"-coverage.csv");VoiceInventory.Export(path,report);notice("Saved voice coverage to Data/VoiceReports.");}catch(Exception ex){notice(ex.Message);}}
    private void Template(){try{string? folder=Pick("Choose an empty folder for your WAV pack template");if(folder==null)return;PersonalVoicePacks.Template(folder,aircraft.SelectedItem?.ToString()??"Generic");notice("Pack template created. Fill source/licence details before sharing recordings.");}catch(Exception ex){notice(ex.Message);}}
    private void Import(){if(!canEdit()){notice("Disconnect live/demo/replay before changing recordings.");return;}string? folder=Pick("Choose authorised WAV recordings with optional pack.json and pack-info.json");if(folder==null)return;try{int n=PersonalVoicePacks.Import(folder,aircraft.SelectedItem?.ToString()??"Generic",AppContext.BaseDirectory);refresh();Scan();notice($"Imported {n} recordings with undo backup.");}catch(Exception ex){notice("Voice import: "+ex.Message);}}
    private void Undo(){if(!canEdit()){notice("Disconnect live/demo/replay before restoring recordings.");return;}try{PersonalVoicePacks.Undo(aircraft.SelectedItem?.ToString()??"Generic",AppContext.BaseDirectory);refresh();Scan();notice("Latest personal import undone; previous pack restored.");}catch(Exception ex){notice("Undo import: "+ex.Message);}}
}
