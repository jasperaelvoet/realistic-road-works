using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // What one sub lane of a works edge is for the section measurement. EcsUtil.ProbeLanes fills it from the lane's components;
    // everything below works on these bits only (game-free, see LaneSection).
    [System.Flags]
    public enum LaneBits : byte
    {
        None = 0,
        Car = 1 << 0,        // CarLane (bicycle lanes included: they are CarLanes)
        Track = 1 << 1,      // TrackLane
        Parking = 1 << 2,    // ParkingLane
        Master = 1 << 3,     // MasterLane: the group lane over its slave lanes (no position of its own)
        Twoway = 1 << 4,     // CarLaneFlags.Twoway / TrackLaneFlags.Twoway: both travel directions share the lane
        BikeOnly = 1 << 5,   // a CarLane whose prefab does not carry cars (bicycle lane)
        Point = 1 << 6,      // EdgeLane.m_EdgeDelta.x == y: a connector at ONE position of the edge (auxiliary lanes that cross the road)
    }

    // Why an edge has (no) direction split (EdgeSection.Verdict, set by LaneSection.Apply).
    public enum SplitVerdict : byte
    {
        Unmeasured = 0,   // default EdgeSection (split at the curve centre)
        Split = 1,        // two separable travel-direction groups of through lanes: DirSplit valid
        NoDriveLanes = 2, // no car / track lane along the edge (lanes not generated yet, pedestrian-only composition)
        OneDirection = 3, // every through car / track lane travels the same way (one-way road)
        Twoway = 4,       // the only through lanes are shared two-way lanes (alley, gravel road)
        Interleaved = 5,  // both directions present, but no single lateral separates them (contraflow lane, odd composition)
    }

    // One lane measured against the edge curve (EDGE frame: lateral + = right of the edge curve direction).
    public struct LaneProbe
    {
        public LaneBits Bits;
        public float Centre;     // median lateral of the interior samples (t = .3/.5/.7 of the lane curve)
        public float Lo, Hi;     // lateral extent over all samples (t = .1 .. .9) including the half width
        public float HalfWidth;
        public float Along;      // smallest |cos| between the lane tangent and the edge tangent over the interior samples
        public sbyte Dir;        // +1 travels with the edge curve, -1 against it, 0 across / undecided

        public bool Has(LaneBits b) => (Bits & b) != 0;
        // Runs along the edge (not a connector across it).
        public bool IsAlong => Dir != 0 && (Bits & LaneBits.Point) == 0;
        // A car / track lane with a position and a direction of its own (masters, parking and connectors excluded).
        public bool IsDrive => (Bits & (LaneBits.Car | LaneBits.Track)) != 0 && (Bits & (LaneBits.Master | LaneBits.Parking)) == 0 && IsAlong;
        // A drive lane that defines a travel direction for the half split: one-way, carries cars or trams (not a bicycle-only lane).
        public bool IsThrough => IsDrive && (Bits & (LaneBits.Twoway | LaneBits.BikeOnly)) == 0;
    }

    // The section measurement as pure math over LaneProbes (robust on curved / mixed-lane roads).
    //
    // Every lane is measured from its OWN geometry: at each sample point the lane is projected onto the edge curve and the lateral
    // and the tangent are compared with the edge frame at that projected point (never a chord, never one fixed point of the edge),
    // so straight, curved, S-curved and reversed edges measure the same. The direction split is decided by the THROUGH lanes only
    // (one-way car / track lanes along the edge): connectors that cross the road (auxiliary lanes at the edge ends), shared two-way
    // lanes, bicycle-only lanes and master lanes neither define nor veto the split. An edge is OneDirection only when every through
    // lane travels the same way. The two direction groups must be separable by their lane CENTRES; overlapping lane extents (lanes
    // that touch at the centre line, measurement noise on curves) still split, at the midpoint of the facing edges kept between the
    // facing centres.
    public static class LaneSection
    {
        public const float kAlongMin = 0.5f;        // |cos| below this at any interior sample: the lane crosses the edge (> 60 deg)
        public const float kCentreGap = 0.05f;      // the split stays at least this far from the facing lane centres
        public const float kSameLanePosition = 0.5f;// lanes whose centres lie within this are one lane position (tram on a car lane)

        private static readonly float[] s_T = { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f };

        // Measures one lane curve against the edge arc (EDGE frame). bits / halfWidth come from the lane's components.
        public static LaneProbe Probe(EdgeArc arc, Bezier4x3 lane, float halfWidth, LaneBits bits)
        {
            var p = new LaneProbe { Bits = bits, HalfWidth = halfWidth, Lo = float.MaxValue, Hi = float.MinValue, Along = 1f };
            float l1 = 0f, l2 = 0f, l3 = 0f;
            int pos = 0, neg = 0;
            for (int k = 0; k < s_T.Length; k++)
            {
                float t = s_T[k];
                float3 pt = MathUtils.Position(lane, t);
                float s = arc.Project(pt);
                float lat = math.dot((pt - arc.Position(s)).xz, arc.Right(s).xz);
                p.Lo = math.min(p.Lo, lat - halfWidth);
                p.Hi = math.max(p.Hi, lat + halfWidth);
                if (k == 0 || k == s_T.Length - 1) continue;          // ends: extent only (node trims / piece ends)
                if (k == 1) l1 = lat; else if (k == 2) l2 = lat; else l3 = lat;
                float2 lt = math.normalizesafe(MathUtils.Tangent(lane, t).xz);
                float2 et = math.normalizesafe(arc.Direction(s).xz);
                float c = math.dot(lt, et);
                p.Along = math.min(p.Along, math.abs(c));
                if (c >= kAlongMin) pos++;
                else if (c <= -kAlongMin) neg++;
            }
            p.Centre = math.max(math.min(l1, l2), math.min(math.max(l1, l2), l3));   // median of three
            p.Dir = pos == 3 ? (sbyte)1 : neg == 3 ? (sbyte)-1 : (sbyte)0;
            return p;
        }

        // Fills the lane-derived part of an EdgeSection: carriage intervals (car, parking and on-road track lanes along the edge,
        // merged across gaps < kCarriageMergeGap, at most kMaxIntervals), CarriageLo/Hi, DirSplit, DriveLo/Hi, lane lines, Verdict
        // and the lane counts. sec.HalfWidth must be set (fallback carriage when no lane qualifies).
        public static void Apply(ref EdgeSection sec, List<LaneProbe> lanes)
        {
            Intervals(ref sec, lanes);
            Directions(ref sec, lanes);
        }

        private static readonly List<float2> s_Raw = new List<float2>(16);
        private static readonly List<float2> s_Merged = new List<float2>(8);

        private static void Intervals(ref EdgeSection sec, List<LaneProbe> lanes)
        {
            var raw = s_Raw;
            raw.Clear();
            for (int i = 0; i < lanes.Count; i++)
            {
                var l = lanes[i];
                if ((l.Bits & (LaneBits.Car | LaneBits.Track | LaneBits.Parking)) == 0) continue;
                if (!l.IsAlong) continue;               // a connector across the road is not a carriage strip
                raw.Add(new float2(l.Lo, l.Hi));
            }
            raw.Sort((x, y) => x.x.CompareTo(y.x));
            var merged = s_Merged;
            merged.Clear();
            for (int i = 0; i < raw.Count; i++)
            {
                if (merged.Count > 0 && raw[i].x <= merged[merged.Count - 1].y + RRWConst.kCarriageMergeGap)
                {
                    var m = merged[merged.Count - 1];
                    m.y = math.max(m.y, raw[i].y);
                    merged[merged.Count - 1] = m;
                }
                else merged.Add(raw[i]);
            }
            // more than kMaxIntervals bands: merge the narrowest gaps
            while (merged.Count > EdgeSection.kMaxIntervals)
            {
                int best = 0; float bestGap = float.MaxValue;
                for (int i = 0; i + 1 < merged.Count; i++)
                {
                    float gap = merged[i + 1].x - merged[i].y;
                    if (gap < bestGap) { bestGap = gap; best = i; }
                }
                merged[best] = new float2(merged[best].x, math.max(merged[best].y, merged[best + 1].y));
                merged.RemoveAt(best + 1);
            }
            if (merged.Count == 0)
            {
                float half = math.max(2f, sec.HalfWidth - 3f);
                merged.Add(new float2(-half, half));
            }
            sec.IntervalCount = merged.Count;
            for (int i = 0; i < merged.Count; i++) sec.SetInterval(i, merged[i]);
            sec.CarriageLo = merged[0].x;
            sec.CarriageHi = merged[merged.Count - 1].y;
        }

        private static void Directions(ref EdgeSection sec, List<LaneProbe> lanes)
        {
            float nan = float.NaN;
            sec.LanesMeasured = true;
            sec.DirSplit = nan;
            sec.DriveLo = sec.CarriageLo;
            sec.DriveHi = sec.CarriageHi;
            sec.LaneLinesLeft = new float4(nan, nan, nan, nan);
            sec.LaneLinesRight = new float4(nan, nan, nan, nan);
            sec.ThroughF = 0; sec.ThroughB = 0; sec.LanesIgnored = 0;

            float dlo = float.MaxValue, dhi = float.MinValue;
            int drive = 0, twoway = 0;
            float cLoF = float.MaxValue, cHiF = float.MinValue, cLoB = float.MaxValue, cHiB = float.MinValue;   // centres
            float eLoF = float.MaxValue, eHiF = float.MinValue, eLoB = float.MaxValue, eHiB = float.MinValue;   // centre -+ half width
            int nF = 0, nB = 0, ignored = 0;
            for (int i = 0; i < lanes.Count; i++)
            {
                var l = lanes[i];
                if ((l.Bits & (LaneBits.Car | LaneBits.Track)) == 0 || (l.Bits & (LaneBits.Master | LaneBits.Parking)) != 0) continue;
                if (!l.IsDrive) { ignored++; continue; }                        // connector across the road / undecided direction
                drive++;
                dlo = math.min(dlo, l.Centre - l.HalfWidth);
                dhi = math.max(dhi, l.Centre + l.HalfWidth);
                if (!l.IsThrough)
                {
                    if (l.Has(LaneBits.Twoway)) twoway++;
                    ignored++;
                    continue;
                }
                if (l.Dir > 0)
                {
                    nF++;
                    cLoF = math.min(cLoF, l.Centre); cHiF = math.max(cHiF, l.Centre);
                    eLoF = math.min(eLoF, l.Centre - l.HalfWidth); eHiF = math.max(eHiF, l.Centre + l.HalfWidth);
                }
                else
                {
                    nB++;
                    cLoB = math.min(cLoB, l.Centre); cHiB = math.max(cHiB, l.Centre);
                    eLoB = math.min(eLoB, l.Centre - l.HalfWidth); eHiB = math.max(eHiB, l.Centre + l.HalfWidth);
                }
            }
            sec.ThroughF = (byte)math.min(255, nF);
            sec.ThroughB = (byte)math.min(255, nB);
            sec.LanesIgnored = (byte)math.min(255, ignored);
            if (drive > 0) { sec.DriveLo = dlo; sec.DriveHi = dhi; }
            if (nF == 0 && nB == 0)
            {
                sec.Verdict = drive == 0 ? SplitVerdict.NoDriveLanes : twoway > 0 ? SplitVerdict.Twoway : SplitVerdict.NoDriveLanes;
                return;
            }
            if (nF == 0 || nB == 0)
            {
                sec.Verdict = SplitVerdict.OneDirection;
                sec.LaneLinesLeft = LaneLines(lanes, 0);    // the only group, for reference
                return;
            }
            // left group by CENTRES (edge frame): every centre of one direction left of every centre of the other
            sbyte leftDir;
            float hiLeftC, loRightC, hiLeftE, loRightE;
            if (cHiB < cLoF) { leftDir = -1; hiLeftC = cHiB; loRightC = cLoF; hiLeftE = eHiB; loRightE = eLoF; }
            else if (cHiF < cLoB) { leftDir = 1; hiLeftC = cHiF; loRightC = cLoB; hiLeftE = eHiF; loRightE = eLoB; }
            else
            {
                sec.Verdict = SplitVerdict.Interleaved;
                sec.LaneLinesLeft = LaneLines(lanes, 0);
                return;
            }
            // midpoint of the facing lane edges (a median between the groups: its middle), kept between the facing centres
            float split = (hiLeftE + loRightE) * 0.5f;
            float a = hiLeftC + kCentreGap, b = loRightC - kCentreGap;
            split = a <= b ? math.clamp(split, a, b) : (hiLeftC + loRightC) * 0.5f;
            sec.DirSplit = split;
            sec.Verdict = SplitVerdict.Split;
            sec.LaneLinesLeft = LaneLines(lanes, leftDir);
            sec.LaneLinesRight = LaneLines(lanes, (sbyte)-leftDir);
        }

        private static readonly List<float2> s_Centres = new List<float2>(8);

        // Boundaries between adjacent THROUGH lanes of one direction (dir 0 = all through lanes): lanes whose centres lie within
        // kSameLanePosition are one lane position (tram track on a car lane); boundary = midpoint of the gap between neighbouring
        // lane extents. At most 4, ascending, NaN padded.
        private static float4 LaneLines(List<LaneProbe> lanes, sbyte dir)
        {
            var c = s_Centres;
            c.Clear();
            for (int i = 0; i < lanes.Count; i++)
            {
                var l = lanes[i];
                if (!l.IsThrough || (dir != 0 && l.Dir != dir)) continue;
                bool dup = false;
                for (int k = 0; k < c.Count; k++)
                    if (math.abs(c[k].x - l.Centre) < kSameLanePosition) { c[k] = new float2(c[k].x, math.max(c[k].y, l.HalfWidth)); dup = true; break; }
                if (!dup) c.Add(new float2(l.Centre, l.HalfWidth));
            }
            c.Sort((x, y) => x.x.CompareTo(y.x));
            float nan = float.NaN;
            var r = new float4(nan, nan, nan, nan);
            int n = 0;
            for (int k = 0; k + 1 < c.Count && n < 4; k++)
            {
                float lo = c[k].x + c[k].y, hi = c[k + 1].x - c[k + 1].y;
                r[n++] = (lo + hi) * 0.5f;
            }
            return r;
        }

        // Short text of a probe for dev lines: "+1.50/3.0 car>" (centre / width, kind, direction > with the edge, < against, x across).
        public static string Describe(in LaneProbe l)
        {
            string kind = l.Has(LaneBits.Master) ? "master" : l.Has(LaneBits.Parking) ? "park" : l.Has(LaneBits.Track) && !l.Has(LaneBits.Car) ? "track"
                        : l.Has(LaneBits.BikeOnly) ? "bike" : "car";
            if (l.Has(LaneBits.Twoway)) kind += "2way";
            if (l.Has(LaneBits.Point)) kind += "@pt";
            char d = l.Dir > 0 ? '>' : l.Dir < 0 ? '<' : 'x';
            return RRWLog.F(l.Centre) + "/" + RRWLog.F(l.HalfWidth * 2f) + " " + kind + d + (l.Dir == 0 ? "(" + RRWLog.F(l.Along) + ")" : "");
        }
    }
}
