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
            var win = new HudWindow(cfg, dir, demo);
            win.Show();
            return app.Run();
        }

        // Writes probe.log for ~40 s: raw values from every supported game so offsets/units can be verified.
        static int Probe(string dir)
        {
            var games = new GameSources(new Settings(Path.Combine(dir, "settings.ini"))); var t = new Tele();
            using (var w = new StreamWriter(Path.Combine(dir, "probe.log"), false))
            {
                var end = DateTime.UtcNow.AddSeconds(40);
                while (DateTime.UtcNow < end)
                {
                    w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + games.Probe(t));
                    w.Flush();
                    System.Threading.Thread.Sleep(250);
                }
            }
            return 0;
        }
    }
}
