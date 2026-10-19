// Sector / sub-sector timing, computed by the overlay itself.
// The games only publish the 3 main sectors (some not even that), but they all publish where the car is on the lap
// (0..1) and the current lap time. So the lap is cut into 3 main sectors x N sub-sectors and every crossing is timed.
//
// Colors:
//   purple = the best time anyone did on that stretch (all drivers where the game publishes it, else your own best),
//   green = faster than the same stretch of your best lap, yellow = slower, gray = nothing to compare with yet.
using System;
using System.Diagnostics;
using System.Globalization;

namespace SimracingBR
{
    public enum SegState { None, Yellow, Green, Purple }

    public class SectorTracker
    {
        public const int Main = 3;
        const double Nan = double.NaN;

        readonly Settings cfg;                 // learned sector boundaries are remembered per track here (may be null)
        string trackId = "";

        public int Subs { get; private set; }
        public int Count { get { return Main * Subs; } }

        // what the view shows (indexes: sub-segment 0..Count-1, main sector 0..2)
        public SegState[] Seg, Sec;
        public double[] SegMs, SecMs;          // time of the last crossing, NaN = none
        public int[] SegStamp, SecStamp;       // Environment.TickCount of the last crossing (for the flash)
        public int CurSeg, CurSec;             // where the car is now
        public float Progress;                 // 0..1 through the current sub-segment
        public bool HasPosition;               // the game gives a lap position at all
        public int OwnBestMs, OwnLastMs;       // lap times seen by the tracker (used when the game doesn't publish them)

        // references
        int[] othersSec = new int[Main];       // best main-sector times of all drivers (ms, 0 = the game doesn't say)
        double[] secT, secOwn, secPrev; bool[] secValid;   // what each main sector was scored with (to score it again)
        int wrapTick; bool pendingClear;       // crossing of the finish line: colors go back to gray after HoldMs
        public int HoldMs = 2500;
        double[] lastSeg, lastSec;             // the previous time through each stretch (reference until a best lap exists)
        bool[] provSeg, provSec;               // the best time comes from a lap that was not valid (placeholder until a valid one)
        double[] bestSeg, refSeg, bestSec, refSec, refCross;   // refCross: lap time at each boundary on the best lap
        public bool Ahead;                     // the lap in progress is ahead of the best lap so far
        double refTotal = Nan;
        // boundaries of the main sectors on the lap (0..1); the first two are learned from the game's sector changes
        double e1 = 1.0 / 3, e2 = 2.0 / 3;
        public bool BoundsKnown { get; private set; }
        double[] bound;

        // the lap in progress
        double[] cross;                        // lap time at each boundary, NaN = not timed
        int nextIdx;
        bool clean, lapValid, sawWrap;
        double prevPos = -1, prevMs;
        int prevSector = -1, transCount;
        double tr1;

        public SectorTracker(Settings cfg, int subs)
        {
            this.cfg = cfg;
            Alloc(Math.Max(1, Math.Min(12, subs)));
        }

        void Alloc(int subs)
        {
            Subs = subs;
            int n = Count;
            Seg = new SegState[n]; Sec = new SegState[Main];
            SegMs = Fill(n); SecMs = Fill(Main);
            SegStamp = new int[n]; SecStamp = new int[Main];
            refCross = Fill(n + 1); Ahead = false; provSeg = new bool[n]; provSec = new bool[Main]; lastSeg = Fill(n); lastSec = Fill(Main); secT = Fill(Main); secOwn = Fill(Main); secPrev = Fill(Main); secValid = new bool[Main];
            bestSeg = Fill(n); refSeg = Fill(n); bestSec = Fill(Main); refSec = Fill(Main); refTotal = Nan;
            cross = Fill(n + 1);
            bound = new double[n + 1];
            Bounds();
            clean = false; nextIdx = Math.Max(1, IndexAfter((float)Math.Max(0, prevPos)));
        }

        static double[] Fill(int n) { var a = new double[n]; for (int i = 0; i < n; i++) a[i] = Nan; return a; }

        void Bounds()
        {
            double[] sb = { 0, e1, e2, 1 };
            for (int m = 0; m < Main; m++)
                for (int j = 0; j < Subs; j++) bound[m * Subs + j] = sb[m] + (sb[m + 1] - sb[m]) * j / Subs;
            bound[Count] = 1;
        }

        // first boundary beyond p (the next one to cross), 1..Count
        int IndexAfter(float p)
        {
            for (int i = 1; i <= Count; i++) if (bound[i] > p) return i;
            return Count;
        }

        public void Configure(int subs)
        {
            subs = Math.Max(1, Math.Min(12, subs));
            if (subs != Subs) Alloc(subs);
        }

        // forget every record (new track or car, new sector boundaries, changed number of sub-sectors)
        public void ResetRecords()
        {
            for (int i = 0; i < Count; i++) { Seg[i] = SegState.None; SegMs[i] = Nan; bestSeg[i] = Nan; refSeg[i] = Nan; lastSeg[i] = Nan; provSeg[i] = false; }
            for (int m = 0; m < Main; m++) { Sec[m] = SegState.None; SecMs[m] = Nan; secT[m] = Nan; bestSec[m] = Nan; refSec[m] = Nan; lastSec[m] = Nan; provSec[m] = false; }
            refTotal = Nan; OwnBestMs = 0; OwnLastMs = 0; Ahead = false;
            for (int i = 0; i <= Count; i++) refCross[i] = Nan;
            clean = false;
        }

        // ---- per frame ---------------------------------------------------------------------
        public string Why = "";                   // why the current lap won't count (shown by --probe)
        public int LapsDone;                      // laps recorded as references
        double lapStart;                          // clock value at the start line

        public void Update(Tele t) { Update(t, Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency); }

        // Segment times are measured with our own clock between frames (the position and the game's lap timer are not
        // always updated in the same frame, which made laps look broken); the game's lap times are only displayed.
        public void Update(Tele t, double ms)
        {
            if (!t.Live) return;                      // paused / frozen: keep the lap as it is
            if (t.TrackPos < 0) { HasPosition = false; prevPos = -1; return; }
            HasPosition = true;
            if (t.TrackId.Length > 0 && t.TrackId != trackId)
            {
                if (trackId.Length == 0) trackId = t.TrackId;     // the name only became known: keep what we have
                else NewTrack(t.TrackId);
            }

            float pos = t.TrackPos;
            for (int m = 0; m < Main; m++) othersSec[m] = t.SessionBestSecMs[m];
            if (!t.LapValid) lapValid = false;
            if (t.InPitLane) Unclean("pit lane");

            if (prevPos < 0)
            {
                BeginLap(pos < 0.03f && !t.InPitLane, pos, t, ms);
                if (!clean && Why.Length == 0) Why = "joined mid-lap";
            }
            else
            {
                bool wrap = prevPos > 0.85 && pos < 0.15;
                if (ms - prevMs > 1000) Unclean("pause or frozen game");
                if (!wrap) LearnBounds(t, pos);
                if (wrap)
                {
                    double frac = (1 - prevPos) / ((1 - prevPos) + pos), tw = prevMs + frac * (ms - prevMs);
                    if (nextIdx <= Count)
                    {
                        cross[Count] = tw - lapStart;
                        if (segOk) EvalSeg(Count - 1);
                        if (clean) FinishLap(tw - lapStart);
                    }
                    BeginLap(!t.InPitLane, pos, t, tw);
                    wrapTick = Environment.TickCount; pendingClear = true;
                    if (!clean) Why = "pit lane";
                }
                else if (pos < prevPos - 0.02 || pos > prevPos + 0.2)
                {
                    Unclean("position jump");         // teleport, rewind, restart: this lap won't count
                    nextIdx = IndexAfter(pos); cross[nextIdx - 1] = Nan;
                }
                else
                {
                    if (pos < prevPos) pos = (float)prevPos;   // ignore small backwards jitter
                    while (nextIdx < Count && pos >= bound[nextIdx])
                    {
                        double f = pos > prevPos ? (bound[nextIdx] - prevPos) / (pos - prevPos) : 1;
                        f = Math.Max(0, Math.Min(1, f));
                        cross[nextIdx] = prevMs + f * (ms - prevMs) - lapStart;
                        if (!segOk) secBad = true;
                        if (segOk) EvalSeg(nextIdx - 1);
                        if (nextIdx % Subs == 0) secBad = false;
                        if (clean && !double.IsNaN(refCross[nextIdx])) Ahead = cross[nextIdx] < refCross[nextIdx];
                        segOk = !t.InPitLane;
                        nextIdx++;
                    }
                }
            }
            prevPos = pos; prevMs = ms;

            Rescore();
            CurSeg = Math.Max(0, Math.Min(Count - 1, IndexAfter(pos) - 1));
            CurSec = CurSeg / Subs;
            double a = bound[CurSeg], b = bound[CurSeg + 1];
            Progress = b > a ? (float)Math.Max(0, Math.Min(1, (pos - a) / (b - a))) : 0;
        }

        // clean = the whole lap can become the reference; segOk = the segment in progress can still be scored
        bool segOk = true, secBad;     // secBad: part of the main sector in progress was not driven normally
        void Unclean(string why) { if (clean) Why = why; clean = false; segOk = false; }

        void BeginLap(bool fromLine, float pos, Tele t, double start)
        {
            for (int i = 0; i <= Count; i++) cross[i] = Nan;
            cross[0] = fromLine ? 0 : Nan;
            lapStart = start;
            nextIdx = fromLine ? 1 : IndexAfter(pos);
            clean = fromLine; sawWrap = fromLine; lapValid = t.LapValid; transCount = 0; Ahead = false; segOk = !t.InPitLane; secBad = false;
            if (fromLine) Why = "";
            prevSector = t.Sector;
        }

        // sector changes at positions near the line are the S3 -> S1 flip, not a boundary
        void LearnBounds(Tele t, float pos)
        {
            int s = t.Sector;
            if (s >= 0 && prevSector >= 0 && s != prevSector && sawWrap && pos > 0.03f && pos < 0.97f)
            {
                transCount++;
                if (transCount == 1) tr1 = pos;
                else if (transCount == 2)
                {
                    if (tr1 > 0.05 && pos - tr1 > 0.05 && pos < 0.95) SetBounds(tr1, pos, true);
                }
            }
            prevSector = s;
        }

        void SetBounds(double a, double b, bool store)
        {
            bool known = BoundsKnown;
            BoundsKnown = true;
            if (known && Math.Abs(a - e1) < 0.004 && Math.Abs(b - e2) < 0.004) return;
            e1 = a; e2 = b;
            Bounds();
            ResetRecords();                           // records were cut at other places
            nextIdx = IndexAfter((float)Math.Max(0, prevPos));
            if (store && cfg != null && trackId.Length > 0)
            {
                cfg.Set(BoundsKey(), a.ToString("0.0000", CultureInfo.InvariantCulture) + "," + b.ToString("0.0000", CultureInfo.InvariantCulture));
                try { cfg.Save(); } catch { }
            }
        }

        string BoundsKey()
        {
            uint h = 2166136261;
            foreach (char c in trackId) { h ^= c; h *= 16777619; }
            return "SecBounds_" + h.ToString("x8");
        }

        void NewTrack(string id)
        {
            trackId = id;
            e1 = 1.0 / 3; e2 = 2.0 / 3; BoundsKnown = false;
            Bounds();
            ResetRecords();
            if (cfg != null && id.Length > 0)
            {
                string[] p = cfg.Str(BoundsKey(), "").Split(',');
                double a, b;
                if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                    && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b) && a > 0.05 && b > a + 0.05 && b < 0.95)
                { e1 = a; e2 = b; BoundsKnown = true; Bounds(); }
            }
            prevPos = -1; prevSector = -1;
        }

        // ---- scoring ------------------------------------------------------------------------
        // purple = faster than your best there AND than every other driver's best (where the game says; otherwise just yours),
        // green  = not purple, but better than your previous time through (or you have none: someone else is faster),
        // yellow = slower than your previous time through.
        // `others` = the best time of all drivers, 0 when the game doesn't publish it.
        static SegState Classify(double t, double ownBest, double prev, double others, bool valid)
        {
            bool beatsOwn = double.IsNaN(ownBest) || t <= ownBest + 2;          // within 2 ms counts as equal
            bool beatsOthers = others <= 0 || t <= others + 30;                  // our clock and the game's differ by a few ms
            if (beatsOwn && beatsOthers) return valid ? SegState.Purple : SegState.Green;
            if (double.IsNaN(prev)) return SegState.Green;
            return t <= prev + 2 ? SegState.Green : SegState.Yellow;
        }

        // a valid lap sets the record; an invalid one only leaves a placeholder where there is nothing yet
        void Record(ref double best, ref bool prov, double t)
        {
            if (lapValid) { if (double.IsNaN(best) || prov || t < best) { best = t; prov = false; } }
            else if (double.IsNaN(best)) { best = t; prov = true; }
        }

        // segment i has just been crossed
        void EvalSeg(int i)
        {
            double a = cross[i], b = cross[i + 1];
            if (double.IsNaN(a) || double.IsNaN(b) || b <= a) return;
            int now = Environment.TickCount;
            SegMs[i] = b - a; Seg[i] = Classify(b - a, provSeg[i] ? Nan : bestSeg[i], lastSeg[i], 0, lapValid); SegStamp[i] = now;
            lastSeg[i] = b - a;
            Record(ref bestSeg[i], ref provSeg[i], b - a);   // records are kept as soon as the stretch is driven
            if ((i + 1) % Subs == 0)
            {
                int m = i / Subs;
                double s0 = cross[m * Subs];
                if (secBad || double.IsNaN(s0) || b <= s0) return;
                secT[m] = b - s0; secOwn[m] = provSec[m] ? Nan : bestSec[m]; secPrev[m] = lastSec[m]; secValid[m] = lapValid;
                SecMs[m] = secT[m]; Sec[m] = Classify(secT[m], secOwn[m], secPrev[m], othersSec[m], lapValid); SecStamp[m] = now;
                lastSec[m] = secT[m];
                Record(ref bestSec[m], ref provSec[m], secT[m]);
            }
        }

        // Main sectors are scored again while the lap goes on: if another driver beats your time, your purple turns green.
        void Rescore()
        {
            for (int m = 0; m < Main; m++)
                if (!double.IsNaN(secT[m]) && Sec[m] != SegState.None) Sec[m] = Classify(secT[m], secOwn[m], secPrev[m], othersSec[m], secValid[m]);
            // after the finish line the colors stay for a moment (so the last stretch can be seen), then go back to gray
            if (pendingClear && Environment.TickCount - wrapTick >= HoldMs)
            {
                pendingClear = false;
                for (int i = 0; i < Count; i++) if (SegStamp[i] - wrapTick <= 0) { Seg[i] = SegState.None; SegMs[i] = Nan; }
                for (int m = 0; m < Main; m++) if (SecStamp[m] - wrapTick <= 0) { Sec[m] = SegState.None; SecMs[m] = Nan; secT[m] = Nan; }
            }
        }

        // a whole lap was timed: update the records
        void FinishLap(double total)
        {
            if (total <= 0) return;
            if (lapValid && clean) LapsDone++;
            OwnLastMs = (int)total;
            if (!lapValid) return;
            for (int i = 0; i <= Count; i++) if (double.IsNaN(cross[i])) return;
            if (OwnBestMs == 0 || total < OwnBestMs) OwnBestMs = (int)total;
            bool newRef = double.IsNaN(refTotal) || total < refTotal;
            for (int i = 0; i < Count; i++)
            {
                double s = cross[i + 1] - cross[i];
                if (s <= 0) continue;
                Record(ref bestSeg[i], ref provSeg[i], s);
                if (newRef) refSeg[i] = s;
            }
            for (int m = 0; m < Main; m++)
            {
                double s = cross[(m + 1) * Subs] - cross[m * Subs];
                if (s <= 0) continue;
                Record(ref bestSec[m], ref provSec[m], s);
                if (newRef) refSec[m] = s;
            }
            if (newRef) { refTotal = total; Array.Copy(cross, refCross, cross.Length); }
        }
    }
}
