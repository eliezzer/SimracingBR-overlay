// Fuel maths for the fuel overlay: consumption per lap, laps left on the tank, laps to go in the race and how much fuel to add.
//
//   per lap     = what the game says, otherwise the average of the last clean laps measured here
//   laps left   = fuel / per lap
//   laps to go  = race laps - laps done - progress on this lap                      (lap races)
//               = time left / fastest valid lap + 0.5                               (timed races; the last lap isn't over when time runs out)
//   to add      = (laps to go + margin) * per lap - fuel in the tank
//
// The race length comes from the game when it publishes it, or from the "race laps" setting.
using System;

namespace SimracingBR
{
    public class FuelModel
    {
        // settings
        public double MarginLaps = 1.0;            // extra laps of fuel on top of the estimate
        public int ManualRaceLaps;                 // 0 = use what the game says

        // results
        public double PerLap;                      // litres per lap, 0 = unknown
        public double LapsLeft;                    // laps the tank lasts, 0 = unknown
        public int BestValidLapMs;                 // fastest valid lap seen, 0 = none yet
        public double LapsToGo = -1;               // race laps still to drive (counting the current one), -1 = unknown
        public double Needed = -1;                 // litres to finish the race, -1 = unknown
        public double ToAdd = -1;                  // litres to add now, 0 = enough, -1 = unknown
        public bool TimedRace;                     // laps to go came from the clock

        // lap in progress
        int prevLapMs, prevLapCount = -1;
        float lapStartFuel;
        bool observedFromStart, lapValid = true, lapPit;
        readonly double[] recent = new double[3];
        int recentN;
        int ownLaps;
        string trackId = "";

        public void Update(Tele t)
        {
            if (!t.Live) return;
            if (t.TrackId.Length > 0 && t.TrackId != trackId) { trackId = t.TrackId; Reset(); }

            if (!t.LapValid) lapValid = false;
            if (t.InPitLane || t.InPitBox) lapPit = true;
            if (lapStartFuel <= 0 && t.Fuel > 0) lapStartFuel = t.Fuel;

            bool rollover = (t.LapCount >= 0 && prevLapCount >= 0 && t.LapCount > prevLapCount)
                         || (t.LapCount < 0 && prevLapMs > 5000 && t.LapMs < prevLapMs - 2000);
            if (t.LapCount >= 0 && prevLapCount >= 0 && t.LapCount < prevLapCount) Reset();   // session restarted
            if (rollover) EndLap(t);
            prevLapMs = t.LapMs; prevLapCount = t.LapCount;

            PerLap = t.FuelPerLap > 0.01f ? t.FuelPerLap : Measured();
            LapsLeft = PerLap > 0.01 && t.Fuel > 0 ? t.Fuel / PerLap : 0;
            int best = BestValidLapMs > 0 ? BestValidLapMs : 0;
            Race(t, best);
        }

        void Reset()
        {
            recentN = 0; BestValidLapMs = 0; observedFromStart = false; lapValid = true; lapPit = false;
            lapStartFuel = 0; ownLaps = 0; prevLapMs = 0; prevLapCount = -1;
        }

        double Measured()
        {
            if (recentN == 0) return 0;
            double s = 0; int n = Math.Min(recentN, recent.Length);
            for (int i = 0; i < n; i++) s += recent[i];
            return s / n;
        }

        void EndLap(Tele t)
        {
            ownLaps++;
            if (observedFromStart)
            {
                float used = lapStartFuel - t.Fuel;
                if (!lapPit && used > 0.05f && used < 80f) { recent[recentN % recent.Length] = used; recentN++; }
                int lapTime = t.LastLapMs > 0 && Math.Abs(t.LastLapMs - prevLapMs) < 1500 ? t.LastLapMs : prevLapMs;   // the game's own time when it already has it
                if (!lapPit && lapValid && lapTime > 20000 && lapTime < 3600000 && (BestValidLapMs == 0 || lapTime < BestValidLapMs))
                    BestValidLapMs = lapTime;
            }
            observedFromStart = true; lapValid = true; lapPit = false; lapStartFuel = t.Fuel;
        }

        void Race(Tele t, int bestLapMs)
        {
            LapsToGo = -1; Needed = -1; ToAdd = -1; TimedRace = false;
            double progress = t.TrackPos >= 0 ? t.TrackPos : 0;
            int totalLaps = ManualRaceLaps > 0 ? ManualRaceLaps : (t.SessionIsRace ? t.SessionLaps : 0);
            if (totalLaps > 0)
            {
                int done = t.LapCount >= 0 ? t.LapCount : ownLaps;
                LapsToGo = Math.Max(0, totalLaps - done - progress);
            }
            else if (t.SessionIsRace && t.SessionTimeLeftMs > 0)
            {
                int lap = bestLapMs > 0 ? bestLapMs : (t.BestLapMs > 0 ? t.BestLapMs : 0);
                if (lap > 0) { LapsToGo = t.SessionTimeLeftMs / (double)lap + 0.5; TimedRace = true; }
            }
            if (LapsToGo >= 0 && PerLap > 0.01)
            {
                Needed = (LapsToGo + MarginLaps) * PerLap;
                ToAdd = Math.Max(0, Needed - t.Fuel);
            }
        }
    }
}
