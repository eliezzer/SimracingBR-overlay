// Entry point: SimracingBR.exe [--demo] [--settings] [--probe]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace SimracingBR
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            if (Array.IndexOf(args, "--probe") >= 0) return Probe(dir);
            bool demo = Array.IndexOf(args, "--demo") >= 0;
            var cfg = new Settings(Path.Combine(dir, "settings.ini"));
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // the overlays can all be off: the tray icon keeps the app alive
            var ui = new OverlayApp(cfg, dir, demo);

            int rc = app.Run();
            GC.KeepAlive(ui);
            return rc;
        }

        // Writes probe.log for 5 minutes: raw values from every supported game so offsets/units can be verified,
        // plus what the sectors tracker makes of them (drive a few laps while it runs).
        static int Probe(string dir)
        {
            var cfg = new Settings(Path.Combine(dir, "settings.ini"));
            var games = new GameSources(cfg); var t = new Tele();
            var trk = new SectorTracker(null, 8);
            using (var w = new StreamWriter(Path.Combine(dir, "probe.log"), false))
            {
                var end = DateTime.UtcNow.AddSeconds(300);
                int n = 0;
                while (DateTime.UtcNow < end)
                {
                    if (n++ % 5 == 0) w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + games.Probe(t));
                    games.Read(t);
                    trk.Update(t);
                    if (n % 5 == 1)
                    {
                        w.WriteLine("             tracker: pos=" + t.TrackPos.ToString("0.000", CultureInfo.InvariantCulture) + " lapMs=" + t.LapMs +
                            " seg=" + trk.CurSeg + " sec=" + trk.CurSec + " pitLane=" + t.InPitLane + " valid=" + t.LapValid +
                            " clean=" + (trk.Why.Length == 0) + " why='" + trk.Why + "' lapsRecorded=" + trk.LapsDone +
                            " ownBest=" + trk.OwnBestMs + " ownLast=" + trk.OwnLastMs + " gameBest=" + t.BestLapMs + " gameLast=" + t.LastLapMs +
                            " bounds=" + (trk.BoundsKnown ? "learned" : "thirds"));
                        w.Flush();
                    }
                    System.Threading.Thread.Sleep(50);
                }
            }
            return 0;
        }
    }
}
