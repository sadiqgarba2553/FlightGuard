using System;
using System.IO;
using System.Windows;
namespace FlightGuard;
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"));
        if (args.Length > 2 && args[0] == "--verify-media") return MediaVerification.Run(args[1], args[2]);
        if (args.Length > 2 && args[0] == "--probe-host") return LiveVerification.Run(args[1],args[2]).GetAwaiter().GetResult();
        if (args.Length > 2 && args[0] == "--verify-recordings") return RecordingVerification.Run(args[1],args[2]).GetAwaiter().GetResult();
        if(args.Length>2 && args[0]=="--analyze-recording")return FlightRecorder.Analyze(args[1],args[2]);
        if(args.Length>4 && args[0]=="--verify-pack")return SoundPackManager.VerifyDownload(args[1],args[2],args[3],args[4]).GetAwaiter().GetResult();
        if(args.Length>1 && args[0]=="--make-demo-recording"){FeatureTests.WriteDemo(args[1]);return 0;}
        if(args.Length>1 && args[0]=="--verify-audio-priority")return AudioPriorityVerification.Run(args[1]);
        if(args.Length>2 && args[0]=="--export-coverage")return Coverage.Export(args[1],args[2]);
        if(args.Length>2 && args[0]=="--verify-voices")return VoiceVerification.Run(args[1],args[2]);
        if(args.Length>2 && args[0]=="--verify-terrain-data")return TerrainDataVerification.Run(args[1],args[2]).GetAwaiter().GetResult();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) => {
            try { Directory.CreateDirectory(Settings.DataFolder); File.AppendAllText(Path.Combine(Settings.DataFolder, "errors.log"), DateTime.Now + " " + e.Exception + Environment.NewLine); } catch { }
            MessageBox.Show("FlightGuard encountered an error: " + e.Exception.Message, "FlightGuard", MessageBoxButton.OK, MessageBoxImage.Error);
            // An unexpected UI exception cannot leave the warning engine running reliably.
            app.Shutdown(1); e.Handled = true;
        };
        try {
            var window = new MainWindow();
            if(args.Length>2 && args[0]=="--verify-replay") {
                int code=1;app.Startup+=async(_,_)=>{try{await window.VerifyReplayWorkflow(args[1],args[2]);code=0;}catch(Exception ex){File.WriteAllText(args[2],System.Text.Json.JsonSerializer.Serialize(new{Status="FAIL",Error=ex.ToString()}));}finally{window.Close();app.Shutdown(code);}};app.Run();return code;
            }
            if (args.Length > 2 && args[0] == "--render-snapshot") {
                var frame=System.Text.Json.JsonSerializer.Deserialize<Frame>(File.ReadAllText(args[1]))!;
                window.RenderSnapshot(frame,args[2]);window.CloseForRender();return 0;
            }
            if(args.Length>2 && args[0]=="--render-tools"){window.RenderTools(args[1],int.Parse(args[2]));window.CloseForRender();return 0;}
            if(args.Length>2 && args[0]=="--render-voices"){window.RenderVoices(args[1],int.Parse(args[2]));window.CloseForRender();return 0;}
            if (args.Length > 0 && args[0] == "--render") {
                window.Render(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "preview.png"), args.Length > 2 ? int.Parse(args[2]) : 0); window.CloseForRender(); return 0;
            }
            app.Run(window); return 0;
        } catch (Exception e) {
            try { Directory.CreateDirectory(Settings.DataFolder); File.WriteAllText(Path.Combine(Settings.DataFolder, "startup-error.txt"), e.ToString()); } catch { }
            return 1;
        }
    }
}
