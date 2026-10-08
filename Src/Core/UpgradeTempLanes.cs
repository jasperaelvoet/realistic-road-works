using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Mathematics;

// Temporary lanes of mode H: the asphalt that stays drivable while a window works, and the lanes traffic uses on it.
//
// Drivable asphalt of a window = the new carriageway (drive lanes, parking, bike lanes of the new layout) minus every build /
// rebuild band that is not finished yet (its window or a later one: dug, or still sidewalk / grass) minus the closed part of
// the window's re-marking (a half while it is resurfaced) and a works buffer beside every strip worked on now. Old and new
// asphalt that exists is drivable whatever it becomes later: an old lane that the new layout turns into the other direction,
// or a parking strip, carries a temporary lane.
//
// Traffic on it, per carriageway (a median separates carriageways):
//   * every lane that fits on the drivable asphalt at its own position stays open where it is (no shift) when that gives
//     every direction a lane;
//   * otherwise the lanes are packed into temporary lanes on the widest drivable piece: kTempLaneMin..kTempLaneMax wide,
//     directions in their usual order, the lanes nearest to the piece kept, the rest dropped;
//   * room for one lane only and two directions: one shared lane, the directions alternate (portable signals). The runtime
//     falls back to one direction with a detour when it cannot signal the section;
//   * no room: closed.
// Dropped lanes move into the closed strip (their blockers then stand in the works, never under an open temporary lane).
// All laterals EDGE frame (+ = right of the edge curve). Pure.
namespace RealisticRoadWorks.V3
{
    public enum TempArrangement : byte
    {
        Untouched = 0,   // no lane is dropped or moved
        Open = 1,        // every direction keeps at least one lane (some may be dropped or moved)
        Shuttle = 2,     // one shared lane, the directions alternate
        Closed = 3,      // a direction (or the road) has no lane
    }

    public struct TempSlot
    {
        public float Lo, Hi;
        public sbyte Dir;
        public float Centre => (Lo + Hi) * 0.5f;
        public float Width => Hi - Lo;
    }

    public sealed class TempLanePlan
    {
        public const int kMaxLanes = 12, kMaxSlots = 12;
        public TempArrangement Arrangement;
        public bool ShuttleDirs;                 // Shuttle: both directions use slot 0
        public int SlotCount;
        public readonly TempSlot[] Slots = new TempSlot[kMaxSlots];
        public int LaneCount;                    // drive lanes of the cross-section, by lateral
        public readonly float[] LaneCentre = new float[kMaxLanes];
        public readonly float[] LaneWidth = new float[kMaxLanes];
        public readonly sbyte[] LaneDir = new sbyte[kMaxLanes];
        public readonly int[] LaneSlot = new int[kMaxLanes];      // -1 = dropped
        public readonly float[] LaneTarget = new float[kMaxLanes]; // centre the lane is moved to (= LaneCentre: not moved)
        public bool ClosedF, ClosedB;            // that direction has no lane (Closed)

        public void Clear()
        {
            Arrangement = TempArrangement.Untouched;
            ShuttleDirs = false;
            SlotCount = 0;
            LaneCount = 0;
            ClosedF = ClosedB = false;
        }

        public bool AnyMoved
        {
            get
            {
                for (int i = 0; i < LaneCount; i++) if (math.abs(LaneTarget[i] - LaneCentre[i]) >= UpgradeTempLanes.kMinShift) return true;
                return false;
            }
        }

        // Index of the drive lane with this EDGE-frame centre (within kMatchTol), -1 none.
        public int LaneAt(float centre)
        {
            int best = -1;
            float bd = UpgradeTempLanes.kMatchTol;
            for (int i = 0; i < LaneCount; i++)
            {
                float d = math.abs(LaneCentre[i] - centre);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        public bool Dropped(float laneCentre)
        {
            int i = LaneAt(laneCentre);
            return i >= 0 && LaneSlot[i] < 0;
        }

        // Lateral shift for the lane with this centre (0 = stays).
        public float ShiftOf(float laneCentre)
        {
            int i = LaneAt(laneCentre);
            if (i < 0) return 0f;
            float d = LaneTarget[i] - LaneCentre[i];
            return math.abs(d) < UpgradeTempLanes.kMinShift ? 0f : d;
        }

        public string Describe()
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(Arrangement.ToString());
            if (ClosedF) sb.Append(" closedF");
            if (ClosedB) sb.Append(" closedB");
            sb.Append(" slots");
            for (int i = 0; i < SlotCount; i++)
                sb.Append(' ').Append(Slots[i].Dir > 0 ? '+' : Slots[i].Dir < 0 ? '-' : '=').Append('[').Append(Slots[i].Lo.ToString("0.##", ic))
                  .Append(',').Append(Slots[i].Hi.ToString("0.##", ic)).Append(']');
            sb.Append(" lanes");
            for (int i = 0; i < LaneCount; i++)
            {
                sb.Append(' ').Append(LaneCentre[i].ToString("0.##", ic)).Append(LaneDir[i] > 0 ? "+" : "-");
                if (LaneSlot[i] < 0) sb.Append("x");
                if (math.abs(LaneTarget[i] - LaneCentre[i]) >= UpgradeTempLanes.kMinShift) sb.Append("->").Append(LaneTarget[i].ToString("0.##", ic));
            }
            return sb.ToString();
        }
    }

    public static class UpgradeTempLanes
    {
        public const float kTempLaneMin = 2.75f;   // narrowest temporary lane (cars and vans at works speed)
        public const float kTempLaneMax = 3.5f;    // widest; wider pieces leave a margin
        public const float kWorksBuffer = 0.5f;    // cones / barrier strip beside a strip worked on now
        public const float kMinShift = 0.15f;      // smaller moves are not applied
        public const float kMatchTol = 0.6f;       // a lane centre matches a plan lane within this
        public const float kFitTol = 0.1f;         // a lane fits on a piece when it sticks out less than this

        private static readonly List<float2> s_Carriage = new List<float2>(4), s_Drive = new List<float2>(8), s_Closed = new List<float2>(8);
        private static readonly List<float2> s_Pieces = new List<float2>(8);
        private static readonly List<int> s_Idx = new List<int>(12), s_Keep = new List<int>(12);

        // Bands of `window` worked on now and bands not built yet. allAtOnce: every unfinished band is closed already.
        // extraClosed (optional, lo < hi): a part of the carriageway closed in this window (the half being resurfaced).
        public static void Plan(List<SubStrip> crossSection, IList<UpgradeBand> bands, int bandCount, int window, bool allAtOnce,
                                TempLanePlan output, float extraLo = float.NaN, float extraHi = float.NaN)
        {
            output.Clear();
            if (crossSection == null) return;

            // drive lanes and carriageways (contiguous lane-type strips)
            s_Carriage.Clear();
            for (int k = 0; k < crossSection.Count; k++)
            {
                var s = crossSection[k];
                bool carriage = s.Kind == SubKind.DriveLane || s.Kind == SubKind.NewParking || s.Kind == SubKind.Bike;
                if (!carriage) continue;
                if (s.Kind == SubKind.DriveLane && s.Dir != 0 && output.LaneCount < TempLanePlan.kMaxLanes)
                {
                    int n = output.LaneCount++;
                    output.LaneCentre[n] = (s.Lo + s.Hi) * 0.5f;
                    output.LaneWidth[n] = s.Hi - s.Lo;
                    output.LaneDir[n] = s.Dir;
                    output.LaneSlot[n] = -1;
                    output.LaneTarget[n] = output.LaneCentre[n];
                }
                int last = s_Carriage.Count - 1;
                if (last >= 0 && s.Lo - s_Carriage[last].y < 0.1f) s_Carriage[last] = new float2(s_Carriage[last].x, math.max(s_Carriage[last].y, s.Hi));
                else s_Carriage.Add(new float2(s.Lo, s.Hi));
            }
            if (output.LaneCount == 0) return;

            // closed now: unfinished build / rebuild bands, plus the buffer beside the ones worked on now, plus the extra interval
            s_Closed.Clear();
            bool anyWork = false;
            for (int i = 0; i < bandCount && i < bands.Count; i++)
            {
                var b = bands[i];
                if (b.Kind != BandKind.Build && b.Kind != BandKind.Rebuild) continue;
                if (b.Window < window) continue;                       // finished: new asphalt, drivable
                bool now = b.Window == window || allAtOnce;
                float buf = now ? kWorksBuffer : 0f;
                s_Closed.Add(new float2(b.Lo - buf, b.Hi + buf));
                anyWork |= b.Window == window;
            }
            if (extraHi > extraLo) { s_Closed.Add(new float2(extraLo - kWorksBuffer, extraHi + kWorksBuffer)); anyWork = true; }
            if (!anyWork) return;   // nothing worked on in this window: lanes as they are

            int lanesF = 0, lanesB = 0;
            float meanF = 0f, meanB = 0f;
            for (int i = 0; i < output.LaneCount; i++)
            {
                if (output.LaneDir[i] > 0) { lanesF++; meanF += output.LaneCentre[i]; }
                else { lanesB++; meanB += output.LaneCentre[i]; }
            }
            bool fRight = lanesF == 0 || lanesB == 0 || meanF / math.max(1, lanesF) > meanB / math.max(1, lanesB);

            bool anyMovedOrDropped = false, shuttle = false;
            for (int c = 0; c < s_Carriage.Count; c++)
            {
                float2 car = s_Carriage[c];
                // drivable pieces of this carriageway
                s_Pieces.Clear();
                s_Pieces.Add(car);
                for (int k = 0; k < s_Closed.Count; k++) Subtract(s_Pieces, s_Closed[k].x, s_Closed[k].y);

                // lanes of this carriageway
                s_Idx.Clear();
                bool hasF = false, hasB = false;
                for (int i = 0; i < output.LaneCount; i++)
                {
                    if (output.LaneCentre[i] < car.x || output.LaneCentre[i] > car.y) continue;
                    s_Idx.Add(i);
                    if (output.LaneDir[i] > 0) hasF = true; else hasB = true;
                }
                if (s_Idx.Count == 0) continue;

                // 1. lanes that fit where they are
                bool fitF = false, fitB = false, allFit = true;
                for (int k = 0; k < s_Idx.Count; k++)
                {
                    int i = s_Idx[k];
                    float lo = output.LaneCentre[i] - output.LaneWidth[i] * 0.5f, hi = output.LaneCentre[i] + output.LaneWidth[i] * 0.5f;
                    bool fits = FitsOn(s_Pieces, lo, hi);
                    if (fits) { if (output.LaneDir[i] > 0) fitF = true; else fitB = true; }
                    else allFit = false;
                }
                if (allFit)
                {
                    for (int k = 0; k < s_Idx.Count; k++) AddSlotAt(output, s_Idx[k]);
                    continue;
                }
                if ((!hasF || fitF) && (!hasB || fitB))
                {
                    for (int k = 0; k < s_Idx.Count; k++)
                    {
                        int i = s_Idx[k];
                        float lo = output.LaneCentre[i] - output.LaneWidth[i] * 0.5f, hi = output.LaneCentre[i] + output.LaneWidth[i] * 0.5f;
                        if (FitsOn(s_Pieces, lo, hi)) AddSlotAt(output, i);
                    }
                    anyMovedOrDropped = true;
                    continue;
                }

                // 2. pack temporary lanes on the widest piece
                anyMovedOrDropped = true;
                float2 piece = default;
                float W = 0f;
                for (int k = 0; k < s_Pieces.Count; k++)
                    if (s_Pieces[k].y - s_Pieces[k].x > W) { W = s_Pieces[k].y - s_Pieces[k].x; piece = s_Pieces[k]; }
                int nMax = (int)math.floor((W + 1e-3f) / kTempLaneMin);
                int cntF = 0, cntB = 0;
                for (int k = 0; k < s_Idx.Count; k++) { if (output.LaneDir[s_Idx[k]] > 0) cntF++; else cntB++; }
                int nF = 0, nB = 0;
                if (hasF && hasB)
                {
                    if (nMax >= 2)
                    {
                        nF = 1; nB = 1;
                        int left = nMax - 2;
                        while (left > 0 && (nF < cntF || nB < cntB))
                        {
                            bool f = nF < cntF && (nB >= cntB || cntF - nF >= cntB - nB);
                            if (f) nF++; else nB++;
                            left--;
                        }
                    }
                    else if (nMax == 1)
                    {
                        shuttle = true;
                        float w = math.min(W, kTempLaneMax);
                        float centre = Clamp(ClosestCentre(output, s_Idx, piece), piece.x + w * 0.5f, piece.y - w * 0.5f);
                        int slot = AddSlot(output, centre - w * 0.5f, centre + w * 0.5f, 0);
                        // the lane of each direction nearest to the shared lane uses it
                        int bf = Nearest(output, s_Idx, 1, centre), bb = Nearest(output, s_Idx, -1, centre);
                        if (bf >= 0) { output.LaneSlot[bf] = slot; output.LaneTarget[bf] = centre; }
                        if (bb >= 0) { output.LaneSlot[bb] = slot; output.LaneTarget[bb] = centre; }
                        continue;
                    }
                    else { output.ClosedF |= hasF; output.ClosedB |= hasB; continue; }
                }
                else
                {
                    int cnt = hasF ? cntF : cntB;
                    int n = math.min(cnt, nMax);
                    if (n == 0) { output.ClosedF |= hasF; output.ClosedB |= hasB; continue; }
                    if (hasF) nF = n; else nB = n;
                }

                int total = nF + nB;
                float sw = math.min(W / total, kTempLaneMax);
                float block = sw * total;
                // keep the block near the lanes it carries
                float want = ClosestCentre(output, s_Idx, piece);
                float bLo = Clamp(want - block * 0.5f, piece.x, piece.y - block);
                // slots by lateral: the direction on the left first
                sbyte leftDir = (sbyte)(fRight ? -1 : 1);
                int nLeft = leftDir > 0 ? nF : nB;
                for (int s = 0; s < total; s++)
                {
                    sbyte d = s < nLeft ? leftDir : (sbyte)(-leftDir);
                    AddSlot(output, bLo + s * sw, bLo + (s + 1) * sw, d);
                }
                // map: per direction, the lanes nearest to its slots, in lateral order
                for (int dd = 0; dd < 2; dd++)
                {
                    sbyte d = (sbyte)(dd == 0 ? 1 : -1);
                    int n = d > 0 ? nF : nB;
                    if (n == 0) continue;
                    float sLo = float.MaxValue, sHi = float.MinValue;
                    for (int s = 0; s < output.SlotCount; s++)
                        if (output.Slots[s].Dir == d && output.Slots[s].Lo >= piece.x - 1e-3f && output.Slots[s].Hi <= piece.y + 1e-3f)
                        { sLo = math.min(sLo, output.Slots[s].Lo); sHi = math.max(sHi, output.Slots[s].Hi); }
                    float sc = (sLo + sHi) * 0.5f;
                    s_Keep.Clear();
                    for (int k = 0; k < s_Idx.Count; k++) if (output.LaneDir[s_Idx[k]] == d) s_Keep.Add(s_Idx[k]);
                    s_Keep.Sort((p, q) => math.abs(output.LaneCentre[p] - sc).CompareTo(math.abs(output.LaneCentre[q] - sc)));
                    if (s_Keep.Count > n) s_Keep.RemoveRange(n, s_Keep.Count - n);
                    s_Keep.Sort((p, q) => output.LaneCentre[p].CompareTo(output.LaneCentre[q]));
                    int m = 0;
                    for (int s = 0; s < output.SlotCount && m < s_Keep.Count; s++)
                    {
                        if (output.Slots[s].Dir != d || output.Slots[s].Lo < piece.x - 1e-3f || output.Slots[s].Hi > piece.y + 1e-3f) continue;
                        int i = s_Keep[m++];
                        output.LaneSlot[i] = s;
                        output.LaneTarget[i] = output.Slots[s].Centre;
                    }
                }
            }

            // dropped lanes move into the nearest closed strip (blockers in the works, not under an open lane)
            for (int i = 0; i < output.LaneCount; i++)
            {
                if (output.LaneSlot[i] >= 0) continue;
                if (!OverlapsSlot(output, output.LaneCentre[i] - output.LaneWidth[i] * 0.5f, output.LaneCentre[i] + output.LaneWidth[i] * 0.5f)) continue;
                float best = float.NaN, bd = float.MaxValue;
                for (int k = 0; k < s_Closed.Count; k++)
                {
                    float lo = s_Closed[k].x, hi = s_Closed[k].y;
                    float c = Clamp(output.LaneCentre[i], lo + output.LaneWidth[i] * 0.5f, hi - output.LaneWidth[i] * 0.5f);
                    if (hi - lo < output.LaneWidth[i]) c = (lo + hi) * 0.5f;
                    if (OverlapsSlot(output, c - output.LaneWidth[i] * 0.5f, c + output.LaneWidth[i] * 0.5f)) continue;
                    float d = math.abs(c - output.LaneCentre[i]);
                    if (d < bd) { bd = d; best = c; }
                }
                if (!float.IsNaN(best)) output.LaneTarget[i] = best;
            }

            if (output.ClosedF || output.ClosedB) output.Arrangement = TempArrangement.Closed;
            else if (shuttle) { output.Arrangement = TempArrangement.Shuttle; output.ShuttleDirs = true; }
            else output.Arrangement = anyMovedOrDropped || output.AnyMoved ? TempArrangement.Open : TempArrangement.Untouched;
        }

        // The temporary yellow lines of a plan (EDGE frame laterals): edge lines at the outer side of every slot run, a centre
        // line between opposing slots (double in a North American theme, the caller decides), lane lines between same-direction
        // slots. kind: 0 edge, 1 centre, 2 lane line.
        public static void Lines(TempLanePlan p, List<float2> output)
        {
            output.Clear();
            if (p.SlotCount == 0) return;
            s_Idx.Clear();
            for (int s = 0; s < p.SlotCount; s++) s_Idx.Add(s);
            s_Idx.Sort((a, b) => p.Slots[a].Lo.CompareTo(p.Slots[b].Lo));
            for (int k = 0; k < s_Idx.Count; k++)
            {
                var s = p.Slots[s_Idx[k]];
                bool joinPrev = k > 0 && math.abs(p.Slots[s_Idx[k - 1]].Hi - s.Lo) < 0.05f;
                if (!joinPrev) output.Add(new float2(s.Lo, 0));
                else
                {
                    var prev = p.Slots[s_Idx[k - 1]];
                    output.Add(new float2(s.Lo, prev.Dir == s.Dir ? 2 : 1));
                }
                bool joinNext = k + 1 < s_Idx.Count && math.abs(p.Slots[s_Idx[k + 1]].Lo - s.Hi) < 0.05f;
                if (!joinNext) output.Add(new float2(s.Hi, 0));
            }
        }

        // Lateral shift of the plan at x (a line between temporary lanes): the shifts of the open lanes interpolated between their
        // targets, the nearest lane's beyond them. x - ShiftAt(x) is where that line meets the road's own markings, at the edge ends
        // where the lanes are back (TrafficLaneShift tapers).
        public static float ShiftAt(TempLanePlan p, float x)
        {
            int lo = -1, hi = -1;
            float tl = float.MinValue, th = float.MaxValue;
            for (int i = 0; i < p.LaneCount; i++)
            {
                if (p.LaneSlot[i] < 0) continue;
                float t = p.LaneTarget[i];
                if (t <= x && t > tl) { tl = t; lo = i; }
                if (t >= x && t < th) { th = t; hi = i; }
            }
            if (lo < 0 && hi < 0) return 0f;
            float sl = lo >= 0 ? Shift(p, lo) : 0f, sh = hi >= 0 ? Shift(p, hi) : 0f;
            if (lo < 0) return sh;
            if (hi < 0 || th - tl < 1e-3f) return lo >= 0 && hi >= 0 ? (sl + sh) * 0.5f : sl;
            return math.lerp(sl, sh, (x - tl) / (th - tl));
        }

        private static float Shift(TempLanePlan p, int i)
        {
            float d = p.LaneTarget[i] - p.LaneCentre[i];
            return math.abs(d) < kMinShift ? 0f : d;
        }

        private static void AddSlotAt(TempLanePlan p, int lane)
        {
            float w = p.LaneWidth[lane];
            int s = AddSlot(p, p.LaneCentre[lane] - w * 0.5f, p.LaneCentre[lane] + w * 0.5f, p.LaneDir[lane]);
            p.LaneSlot[lane] = s;
            p.LaneTarget[lane] = p.LaneCentre[lane];
        }

        private static int AddSlot(TempLanePlan p, float lo, float hi, sbyte dir)
        {
            if (p.SlotCount >= TempLanePlan.kMaxSlots) return -1;
            p.Slots[p.SlotCount] = new TempSlot { Lo = lo, Hi = hi, Dir = dir };
            return p.SlotCount++;
        }

        private static bool OverlapsSlot(TempLanePlan p, float lo, float hi)
        {
            for (int s = 0; s < p.SlotCount; s++)
                if (math.min(hi, p.Slots[s].Hi) - math.max(lo, p.Slots[s].Lo) > 0.05f) return true;
            return false;
        }

        private static bool FitsOn(List<float2> pieces, float lo, float hi)
        {
            for (int k = 0; k < pieces.Count; k++)
                if (lo >= pieces[k].x - kFitTol && hi <= pieces[k].y + kFitTol) return true;
            return false;
        }

        // Mean centre of the carriageway's lanes, clamped into the piece (the block of temporary lanes stays near them).
        private static float ClosestCentre(TempLanePlan p, List<int> idx, float2 piece)
        {
            float sum = 0f;
            for (int k = 0; k < idx.Count; k++) sum += p.LaneCentre[idx[k]];
            return Clamp(sum / math.max(1, idx.Count), piece.x, piece.y);
        }

        private static int Nearest(TempLanePlan p, List<int> idx, int dir, float c)
        {
            int best = -1;
            float bd = float.MaxValue;
            for (int k = 0; k < idx.Count; k++)
            {
                int i = idx[k];
                if (p.LaneDir[i] != dir) continue;
                float d = math.abs(p.LaneCentre[i] - c);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        private static float Clamp(float x, float lo, float hi) => hi < lo ? (lo + hi) * 0.5f : math.clamp(x, lo, hi);

        // Removes [lo, hi] from a list of intervals (an interval may split in two).
        private static void Subtract(List<float2> l, float lo, float hi)
        {
            for (int i = l.Count - 1; i >= 0; i--)
            {
                var v = l[i];
                if (hi <= v.x || lo >= v.y) continue;
                l.RemoveAt(i);
                if (lo > v.x + 0.01f) l.Add(new float2(v.x, lo));
                if (hi < v.y - 0.01f) l.Add(new float2(hi, v.y));
            }
            l.Sort((a, b) => a.x.CompareTo(b.x));
        }
    }
}
