// Picks whichever supported game is live, and the demo data used by the Settings preview.
using System;
using System.Collections.Generic;
using System.Text;

namespace SimracingBR
{
    public class GameSources
    {
        readonly List<ITelemetrySource> all = new List<ITelemetrySource>();
        ITelemetrySource active;

        public GameSources(Settings cfg)
        {
            all.Add(new AcEvoSource());
            all.Add(new AccSource(cfg));
            all.Add(new Ams2Source(cfg));
            all.Add(new LmuSource(cfg));
        }

        public ITelemetrySource Active { get { return active; } }
        public IEnumerable<ITelemetrySource> All { get { return all; } }

        // Sticks to the game that was live last; while it isn't, looks at the others (each retries opening
        // its shared memory at most once a second, so this is cheap).
        public bool Read(Tele t)
        {
            if (active != null && active.Read(t) && t.Live) return true;
            foreach (ITelemetrySource s in all)
            {
                if (s == active) continue;
                if (s.Read(t) && t.Live) { active = s; return true; }
            }
            if (active != null) active.Read(t);       // keep the last game's values / note on screen
            else { t.Live = false; t.Note = "WAITING FOR GAME"; }
            return false;
        }

        // one probe line per game whose shared memory is open
        public string Probe(Tele t)
        {
            var sb = new StringBuilder();
            foreach (ITelemetrySource s in all)
            {
                if (sb.Length > 0) sb.Append("\r\n             ");
                sb.Append(s.Name).Append(": ");
                sb.Append(s.Read(t) ? (t.Live ? "LIVE " : "STALE ") + s.Probe() : "not connected (" + s.Error + ")");
            }
            return sb.ToString();
        }
    }

    public class DemoSource
    {
        readonly DateTime t0 = DateTime.UtcNow;
        public const double Lap = 60.0, WarmUp = 3 * Lap;     // the live demo starts after 3 simulated laps (see Prime)

        public void Read(Tele t) { Fill(t, (DateTime.UtcNow - t0).TotalSeconds + WarmUp); }

        // Runs the demo lap by lap without waiting, so the sectors preview already has colors to show.
        public static void Prime(SectorTracker tracker)
        {
            var t = new Tele();
            for (double s = 0; s < WarmUp; s += 1.0 / 30) { Fill(t, s); tracker.Update(t); }
        }

        // The demo is a pure function of the time in seconds. Sub-sector times change from lap to lap: the position on the
        // lap runs a little ahead / behind a steady pace, differently every lap.
        static void Fill(Tele t, double s)
        {
            double lap = s % Lap;
            int lapIdx = (int)(s / Lap);
            double u = lap / Lap, amp = 0.045 * Math.Sin(lapIdx * 2.1 + 1.0);
            t.TrackPos = (float)Math.Max(0, Math.Min(1, u + amp * Math.Sin(6 * Math.PI * u)));
            t.Sector = -1; t.LapValid = true; t.InPitLane = false; t.TrackId = "demo";
            t.HasLeader = true; t.LeaderBestMs = 59412;
            double ph = s * 1.1;
            double gas = Math.Max(0, Math.Min(1, 0.55 + 0.6 * Math.Sin(ph) + 0.15 * Math.Sin(ph * 3.1)));
            double brk = Math.Max(0, Math.Min(1, 0.9 * Math.Sin(ph + 2.4) - 0.15));
            if (brk > 0.05) gas *= 0.1;
            double clu = Math.Max(0, Math.Sin(ph * 0.5 + 1.0) - 0.8) * 4.0;
            t.Live = true; t.Note = "DEMO";
            t.Gas = (float)gas; t.Brake = (float)brk; t.Clutch = (float)Math.Min(1, clu);
            t.SpeedKmh = (float)(110 + 90 * Math.Sin(ph * 0.9));
            t.Gear = 2 + (int)Math.Max(0, Math.Min(5, t.SpeedKmh / 40.0));
            t.MaxRpm = 8500;
            t.Rpm = (int)(4000 + 4400 * ((t.SpeedKmh % 40.0) / 40.0) * (0.5 + 0.5 * gas));
            t.SteerDeg = (float)(220 * Math.Sin(ph * 0.7));
            t.AbsOn = brk > 0.85; t.TcOn = gas > 0.93;
            t.FuelPerLap = 2.35f;
            t.Fuel = (float)(48.0 - 2.35 * (s / 60.0));
            if (t.Fuel < 0) t.Fuel = 0;
            t.LapMs = (int)(lap * 1000);
            t.TcLevel = 3; t.TcCutLevel = 2; t.AbsLevel = 4; t.HasTcCut = true;
            t.ShiftUp = t.Rpm >= t.MaxRpm * 0.95;
            t.Status = 2; t.InPitBox = false;
        }
    }
}
