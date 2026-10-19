// Fuel maths for the fuel overlay: consumption per lap, laps left on the tank and the fuel needed for the WHOLE race.
//
//   per lap      = what the game says, otherwise the average of the last clean laps measured here
//   laps left    = fuel in the tank / per lap
//   race laps    = the race's total laps                                           (lap race)
//                = total race time / fastest valid lap + 0.5                         (timed race; the last lap isn't over when time runs out)
//   race fuel    = (race laps + margin) * per lap                                    <- what the overlay suggests loading
//
// It never looks at the time left or at what is in the tank: the suggestion is for the full race distance.
// The race length is the "race laps" / "race minutes" setting, or, in a race session, what the game says (laps, or the highest
// session clock seen, i.e. the start of a timed race). The lap time is the fastest valid lap seen on this track and car in any
// session (so the qualifying lap counts), and it, like the consumption, is remembered between runs.
using System;
using System.Globalization;

namespace SimracingBR
{
    public class FuelModel
    {
        readonly Settings cfg;                     // where the fastest lap and the consumption are remembered (may be null)

        // settings
        public double MarginLaps = 1.0;            // extra laps of fuel on top of the estimate
        public int ManualRaceLaps;                 // 0 = use what the game says
        public int ManualRaceMinutes;              // 0 = use what the game says

        // results
        public double PerLap;                      // litres per lap, 0 = unknown
        public double LapsLeft;                    // laps the tank lasts, 0 = unknown
        public int BestValidLapMs;                 // fastest valid lap on this track and car, 0 = none yet
        public double RaceLaps = -1;               // laps of the whole race, -1 = unknown
        public int RaceMinutes;                    // total time of a timed race, 0 = lap race / unknown
        public double RaceFuel = -1;               // litres for the whole race, -1 = unknown
        public bool TimedRace;                     // the race laps came from the clock

        // lap in progress
        int prevLapMs, prevLapCount = -1;
        float lapStartFuel;
        bool observedFromStart, lapValid = true, lapPit;
        readonly double[] recent = new double[3];
        int recentN;
        double storedPerLap;
        int raceStartClockMs;                      // highest session clock seen in a race session
        string trackId = "";

        public FuelModel(Settings cfg) { this.cfg = cfg; }

        public void Update(Tele t)
        {
            if (!t.Live) return;
            if (t.TrackId.Length > 0 && t.TrackId != trackId) { trackId = t.TrackId; Reset(); Load(); }

            if (!t.LapValid) lapValid = false;
            if (t.InPitLane || t.InPitBox) lapPit = true;
            if (lapStartFuel <= 0 && t.Fuel > 0) lapStartFuel = t.Fuel;

            bool rollover = (t.LapCount >= 0 && prevLapCount >= 0 && t.LapCount > prevLapCount)
                         || (t.LapCount < 0 && prevLapMs > 5000 && t.LapMs < prevLapMs - 2000);
            if (rollover) EndLap(t);
            prevLapMs = t.LapMs; prevLapCount = t.LapCount;

            // a new race session starts its clock over: forget the old one
            if (t.SessionIsRace && t.SessionTimeLeftMs > raceStartClockMs) raceStartClockMs = t.SessionTimeLeftMs;
            if (!t.SessionIsRace) raceStartClockMs = 0;

            PerLap = t.FuelPerLap > 0.01f ? t.FuelPerLap : Measured();
            LapsLeft = PerLap > 0.01 && t.Fuel > 0 ? t.Fuel / PerLap : 0;
            Race(t);
        }

        void Reset()
        {
            recentN = 0; BestValidLapMs = 0; storedPerLap = 0; observedFromStart = false; lapValid = true; lapPit = false;
            lapStartFuel = 0; prevLapMs = 0; prevLapCount = -1; raceStartClockMs = 0;
        }

        double Measured()
        {
            if (recentN == 0) return storedPerLap;
            double s = 0; int n = Math.Min(recentN, recent.Length);
            for (int i = 0; i < n; i++) s += recent[i];
            return s / n;
        }

        void EndLap(Tele t)
        {
            if (observedFromStart)
            {
                float used = lapStartFuel - t.Fuel;
                if (!lapPit && used > 0.05f && used < 80f) { recent[recentN % recent.Length] = used; recentN++; Save(); }
                int lapTime = t.LastLapMs > 0 && Math.Abs(t.LastLapMs - prevLapMs) < 1500 ? t.LastLapMs : prevLapMs;   // the game's own time when it already has it
                if (!lapPit && lapValid && lapTime > 20000 && lapTime < 3600000 && (BestValidLapMs == 0 || lapTime < BestValidLapMs))
                { BestValidLapMs = lapTime; Save(); }
            }
            observedFromStart = true; lapValid = true; lapPit = false; lapStartFuel = t.Fuel;
        }

        void Race(Tele t)
        {
            RaceLaps = -1; RaceFuel = -1; RaceMinutes = 0; TimedRace = false;
            int lap = BestValidLapMs > 0 ? BestValidLapMs : (t.BestLapMs > 0 ? t.BestLapMs : 0);

            if (ManualRaceLaps > 0) RaceLaps = ManualRaceLaps;
            else if (ManualRaceMinutes > 0 && lap > 0) { RaceLaps = ManualRaceMinutes * 60000.0 / lap + 0.5; TimedRace = true; RaceMinutes = ManualRaceMinutes; }
            else if (ManualRaceMinutes > 0) { TimedRace = true; RaceMinutes = ManualRaceMinutes; }
            else if (t.SessionIsRace && t.SessionLaps > 0) RaceLaps = t.SessionLaps;
            else if (t.SessionIsRace && raceStartClockMs > 0)
            {
                TimedRace = true; RaceMinutes = (int)Math.Round(raceStartClockMs / 60000.0);
                if (lap > 0) RaceLaps = raceStartClockMs / (double)lap + 0.5;
            }
            if (RaceLaps >= 0 && PerLap > 0.01) RaceFuel = (RaceLaps + MarginLaps) * PerLap;
        }

        // ---- remembered between runs (per track + car) -----------------------------------------
        string Key(string what)
        {
            uint h = 2166136261;
            foreach (char c in trackId) { h ^= c; h *= 16777619; }
            return what + h.ToString("x8");
        }

        void Load()
        {
            if (cfg == null || trackId.Length == 0) return;
            BestValidLapMs = (int)cfg.Num(Key("FuelBest_"), 0);
            storedPerLap = cfg.Num(Key("FuelPer_"), 0);
        }

        void Save()
        {
            if (cfg == null || trackId.Length == 0) return;
            if (BestValidLapMs > 0) cfg.Set(Key("FuelBest_"), BestValidLapMs.ToString(CultureInfo.InvariantCulture));
            double per = Measured();
            if (per > 0) cfg.Set(Key("FuelPer_"), per.ToString("0.000", CultureInfo.InvariantCulture));
            try { cfg.Save(); } catch { }
        }
    }
}
