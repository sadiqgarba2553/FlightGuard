using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
namespace FlightGuard;
public static class MediaVerification
{
    // Decode every bundled WAV through the same Windows media stack used by the application.
    // Volume is zero: this checks decodability without playing 78 cockpit sounds to the user.
    public static int Run(string root, string report)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var files = new Queue<string>(Directory.GetFiles(Path.Combine(root, "Sounds"), "*.wav", SearchOption.AllDirectories));
        var lines = new List<string>(); int failed = 0, passed = 0;
        MediaPlayer? player = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        string current = "";
        Action? next = null;
        void Done(bool success, string detail)
        {
            timeout.Stop(); if (success) passed++; else failed++;
            lines.Add((success ? "PASS  " : "FAIL  ") + Path.GetRelativePath(root,current) + "  " + detail);
            player?.Close(); player = null; app.Dispatcher.BeginInvoke(next!);
        }
        next = () => {
            if (files.Count == 0) {
                lines.Insert(0, $"Windows media decode: {passed} passed, {failed} failed");
                File.WriteAllLines(report,lines); app.Shutdown(failed == 0 ? 0 : 1); return;
            }
            current=files.Dequeue();player=new MediaPlayer { Volume=0 };
            player.MediaOpened += (_,_) => Done(player.NaturalDuration.HasTimeSpan && player.NaturalDuration.TimeSpan.TotalSeconds>.05, player.NaturalDuration.ToString());
            player.MediaFailed += (_,e) => Done(false,e.ErrorException.Message);
            player.Open(new Uri(current)); timeout.Start();
        };
        timeout.Tick += (_,_) => Done(false,"Decode timed out");
        app.Startup += (_,_) => next();
        return app.Run();
    }
}
