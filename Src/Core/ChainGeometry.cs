using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // Arc-length parameterisation of one edge curve (64 segments, XZ+Y distance like SxGeom/MxChoreo).
    // s = metres from the curve start (t = 0). Lateral offsets: + = right of the curve direction
    // (x east, z north: right = (dir.z, 0, -dir.x)), matching TxUtil.RightNormal and MxChoreo.PathPos.
    // Managed class: build once per edge geometry (EdgeRecord.Arc), use on the main thread.
    // Jobs that need curve sampling carry their own copy (e.g. MxPuppet's 16-sample table).
    public sealed class EdgeArc
    {
        public const int kSegments = 64;
        public Bezier4x3 Curve;
        public float Length;
        private readonly float[] m_Cum = new float[kSegments + 1];

        public EdgeArc(Bezier4x3 curve) { Rebuild(curve); }

        public void Rebuild(Bezier4x3 curve)
        {
            Curve = curve;
            float3 prev = MathUtils.Position(curve, 0f);
            m_Cum[0] = 0f;
            for (int i = 1; i <= kSegments; i++)
            {
                float3 p = MathUtils.Position(curve, i / (float)kSegments);
                m_Cum[i] = m_Cum[i - 1] + math.distance(prev, p);
                prev = p;
            }
            Length = math.max(0.001f, m_Cum[kSegments]);
        }

        // Curve parameter t for arc distance s (clamped to [0, Length]).
        public float TAt(float s)
        {
            if (s <= 0f) return 0f;
            if (s >= Length) return 1f;
            int lo = 0, hi = kSegments;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (m_Cum[mid] <= s) lo = mid; else hi = mid;
            }
            float seg = m_Cum[hi] - m_Cum[lo];
            float f = seg > 1e-5f ? (s - m_Cum[lo]) / seg : 0f;
            return (lo + f) / kSegments;
        }

        // Arc distance of curve parameter t.
        public float SAt(float t)
        {
            t = math.saturate(t);
            float x = t * kSegments;
            int i = math.min(kSegments - 1, (int)math.floor(x));
            return math.lerp(m_Cum[i], m_Cum[i + 1], x - i);
        }

        public float3 Position(float s) => MathUtils.Position(Curve, TAt(s));

        public float3 Direction(float s) => math.normalizesafe(MathUtils.Tangent(Curve, TAt(s)), new float3(0f, 0f, 1f));

        public static float3 RightOf(float3 dir)
        {
            float2 d = math.normalizesafe(dir.xz, new float2(0f, 1f));
            return new float3(d.y, 0f, -d.x);
        }

        public float3 Right(float s) => RightOf(Direction(s));

        // Point at distance s with a horizontal lateral offset; Y is the curve's Y (road surface / grade reference).
        // Beyond the ends the curve is extended linearly along its end tangent.
        public float3 Offset(float s, float lateral)
        {
            float sc = math.clamp(s, 0f, Length);
            float t = TAt(sc);
            float3 p = MathUtils.Position(Curve, t);
            float3 dir = math.normalizesafe(MathUtils.Tangent(Curve, t), new float3(0f, 0f, 1f));
            p += RightOf(dir) * lateral;
            if (s != sc) p += new float3(dir.x, 0f, dir.z) * (s - sc);
            return p;
        }

        // Yaw-only rotation facing along the curve at s (facing = -1 turns it around).
        public quaternion Rotation(float s, int facing = 1)
        {
            float3 d = Direction(s) * (facing >= 0 ? 1f : -1f);
            return quaternion.LookRotationSafe(math.normalizesafe(new float3(d.x, 0f, d.z), new float3(0f, 0f, 1f)), math.up());
        }

        // Signed horizontal radius of curvature at s (+ = curve turns right). +inf on straights.
        public float SignedRadius(float s)
        {
            const float h = 1f;
            float a = math.max(0f, s - h), b = math.min(Length, s + h);
            if (b - a < 0.1f) return float.PositiveInfinity;
            float2 d0 = math.normalizesafe(Direction(a).xz, new float2(0f, 1f));
            float2 d1 = math.normalizesafe(Direction(b).xz, new float2(0f, 1f));
            float cross = d0.x * d1.y - d0.y * d1.x;            // + = turns left (counter-clockwise, x east z north)
            float ang = math.atan2(cross, math.dot(d0, d1));
            if (math.abs(ang) < 1e-5f) return float.PositiveInfinity;
            return -(b - a) / ang;                               // right turn => positive
        }

        // Node spacing that keeps the chord error <= kAreaMaxSagitta on the band's outer edge.
        public float AdaptiveStep(float s, float maxAbsLateral, float maxStep)
        {
            float r = math.abs(SignedRadius(s));
            if (float.IsInfinity(r)) return maxStep;
            r += maxAbsLateral;
            return math.clamp(math.sqrt(8f * r * RRWConst.kAreaMaxSagitta), RRWConst.kAreaMinNodeStep, maxStep);
        }

        // Closed polygon (no duplicate closing point) covering s0..s1 between lateral offsets latLeft (< latRight):
        // left side s0 -> s1, then right side s1 -> s0. Same winding as the surface prototype (verified in game with area CreationDefinitions).
        // - s0/s1 may lie outside [0, Length] (linear extension past a node, kNodeOverlap).
        // - Adaptive spacing (<= step, sagitta <= 2 cm on curves); vertices closer than kAreaMinNodeGap are dropped.
        // - Inner-side laterals are clamped to 0.85 R so tight curves never fold the polygon.
        // - cutStart / cutEnd: optional horizontal unit directions of a mitre line through the centre point at s0 / s1
        //   (e.g. the bisector of two edges' right vectors at a shared interior node). float3.zero = perpendicular end.
        //   Both neighbours compute the same line, so their polygons meet without wedge gaps or double cover.
        // Y = curve Y (callers snap / override).
        public void Strip(float s0, float s1, float latLeft, float latRight, float step, List<float3> output,
                          float3 cutStart = default, float3 cutEnd = default)
        {
            output.Clear();
            if (s1 < s0) { float x = s0; s0 = s1; s1 = x; }
            float len = s1 - s0;
            if (len < 0.05f) return;
            float maxLat = math.max(math.abs(latLeft), math.abs(latRight));
            float maxStep = math.max(RRWConst.kAreaMinNodeStep, step);
            var ss = s_Stations;
            ss.Clear();
            ss.Add(s0);
            float s = s0;
            while (s < s1 - 1e-3f)
            {
                float st = AdaptiveStep(math.clamp(s, 0f, Length), maxLat, maxStep);
                float next = math.min(s1, s + st);
                if (s1 - next < RRWConst.kAreaMinNodeGap) next = s1;
                ss.Add(next);
                s = next;
            }
            for (int i = 0; i < ss.Count; i++) output.Add(Point(ss[i], latLeft, i == 0 ? cutStart : i == ss.Count - 1 ? cutEnd : default));
            for (int i = ss.Count - 1; i >= 0; i--) output.Add(Point(ss[i], latRight, i == 0 ? cutStart : i == ss.Count - 1 ? cutEnd : default));
        }

        private static readonly List<float> s_Stations = new List<float>(64);

        private float3 Point(float s, float lateral, float3 cut)
        {
            float sc = math.clamp(s, 0f, Length);
            float r = SignedRadius(sc);
            // inner side of a right turn is +lateral; of a left turn -lateral (r < 0)
            if (!float.IsInfinity(r))
            {
                float lim = 0.85f * math.abs(r);
                if (r > 0f && lateral > lim) lateral = lim;
                if (r < 0f && lateral < -lim) lateral = -lim;
            }
            if (math.lengthsq(cut.xz) < 1e-6f) return Offset(s, lateral);
            float3 centre = Offset(s, 0f);
            float3 right = Right(sc);
            float2 c = math.normalizesafe(cut.xz, right.xz);
            float d = math.dot(c, right.xz);
            if (math.abs(d) < 0.3f) return Offset(s, lateral);      // degenerate mitre (> ~72 deg bend): perpendicular
            float k = lateral / d;
            return new float3(centre.x + c.x * k, centre.y, centre.z + c.y * k);
        }

        // Nearest arc distance of a world point (coarse 64-sample search + local refinement), XZ only. Clamped to [0, Length].
        public float Project(float3 p)
        {
            int best = 0;
            float bd = float.MaxValue;
            for (int i = 0; i <= kSegments; i++)
            {
                float3 q = MathUtils.Position(Curve, i / (float)kSegments);
                float d = math.distancesq(q.xz, p.xz);
                if (d < bd) { bd = d; best = i; }
            }
            float tLo = math.max(0, best - 1) / (float)kSegments, tHi = math.min(kSegments, best + 1) / (float)kSegments;
            float bt = best / (float)kSegments;
            for (int k = 0; k < 16; k++)
            {
                float t = math.lerp(tLo, tHi, k / 15f);
                float d = math.distancesq(MathUtils.Position(Curve, t).xz, p.xz);
                if (d < bd) { bd = d; bt = t; }
            }
            return SAt(bt);
        }

        // Like Project, but points beyond the ends get a signed distance along the end tangent (< 0 before the start,
        // > Length after the end). Used when an edge was extended (Director geometry change, Tools combine).
        public float ProjectExtended(float3 p)
        {
            float s = Project(p);
            if (s <= 1e-3f)
            {
                float3 d = Direction(0f);
                float along = math.dot((p - Position(0f)).xz, math.normalizesafe(d.xz));
                return math.min(0f, along);
            }
            if (s >= Length - 1e-3f)
            {
                float3 d = Direction(Length);
                float along = math.dot((p - Position(Length)).xz, math.normalizesafe(d.xz));
                return Length + math.max(0f, along);
            }
            return s;
        }

        // Horizontal distance of a point from the curve.
        public float DistanceXZ(float3 p) => math.distance(Position(Project(p)).xz, p.xz);
    }

    // Shared curve matching (Tools split/combine detection, Director PreviewHide).
    public static class CurveMatch
    {
        // True when the piece's start, middle and end lie within tol (XZ) of the source curve.
        public static bool LiesOn(Bezier4x3 piece, EdgeArc source, float tol)
        {
            return source.DistanceXZ(MathUtils.Position(piece, 0f)) <= tol
                && source.DistanceXZ(MathUtils.Position(piece, 0.5f)) <= tol
                && source.DistanceXZ(MathUtils.Position(piece, 1f)) <= tol;
        }
    }

    // Lateral cross-section of a works edge (metres from the centre line, + = right). Measured by EcsUtil.MeasureSection.
    public struct EdgeSection
    {
        public const int kMaxIntervals = 3;

        public float HalfWidth;       // composition width / 2 (incl. sidewalks)
        public float CarriageLo;      // outermost carriage-interval edges (lo < hi); falls back to +-(HalfWidth - 3)
        public float CarriageHi;
        public float FlatHalfWidth;   // flat trench floor half width = HalfWidth * saturate(1 - max(0.2 w, 3) / w) (TerrainSystem middleSize)
        public float CompositionWidth;
        public float GradeOffset;     // profile grade relative to curve Y = NetCompositionData.m_SurfaceHeight.min (fallback kProfileGradeBias)
        public float TrimStart;       // arc distance from the curve start (node centre) to where the edge geometry starts (max of left/right)
        public float TrimEnd;         // arc distance from the edge geometry end to the curve end
        public int IntervalCount;     // carriage intervals (car + parking + on-road track lanes, merged across gaps < 0.5 m)
        public float2 Interval0, Interval1, Interval2;   // (lo, hi) per interval, lo < hi, sorted left to right

        // ---- lane measurement, EDGE frame. Valid when LanesMeasured (EcsUtil.MeasureSection sets it); a default
        // EdgeSection behaves like an unmeasured one (split at the curve centre, drive lanes = carriageway, no lane lines).
        public bool LanesMeasured;
        public float DirSplit;        // lateral between the two travel-direction groups of THROUGH lanes (LaneSection: one-way car /
                                      // track lanes along the edge; midpoint of the facing lane edges, kept between the facing centres);
                                      // NaN = no split, Verdict says why (one direction, shared two-way lanes, interleaved, no lanes)
        public float DriveLo, DriveHi;// outer edges of the car / track lanes along the edge (parking and connectors excluded)
        public float4 LaneLinesLeft;  // boundaries between same-direction through lanes of the group on the EDGE-left of DirSplit
                                      // (ascending, NaN padded; a one-direction edge puts its only group here)
        public float4 LaneLinesRight; // same for the group on the edge-right of DirSplit
        public SplitVerdict Verdict;  // LaneSection.Apply: why DirSplit is (not) valid (Unmeasured on a default section)
        public byte ThroughF, ThroughB;   // through lanes travelling with / against the edge curve (dev and logs)
        public byte LanesIgnored;     // car / track lanes left out of the split (connectors across the road, two-way, bicycle-only)

        public float Centre => (CarriageLo + CarriageHi) * 0.5f;
        // Split used by the zone math: DirSplit when measured (may be NaN), else the curve centre.
        public float Split => LanesMeasured ? DirSplit : 0f;
        public bool TwoDirections => !float.IsNaN(Split);
        public float DriveLoE => LanesMeasured && DriveHi > DriveLo ? DriveLo : CarriageLo;
        public float DriveHiE => LanesMeasured && DriveHi > DriveLo ? DriveHi : CarriageHi;

        public float2 Interval(int i) => i == 0 ? Interval0 : i == 1 ? Interval1 : Interval2;

        public void SetInterval(int i, float2 v)
        {
            if (i == 0) Interval0 = v; else if (i == 1) Interval1 = v; else Interval2 = v;
        }

        // Number of lateral bands (polygons per edge span) a layer uses (legacy form: TempLines = 2, never CarriageHalf).
        public int BandCount(SurfaceLayer layer)
        {
            var b = PhasePlan.BandOf(layer);
            return b == SurfaceBand.Carriageway ? math.max(1, IntervalCount) : b == SurfaceBand.TempLines ? 2 : 1;
        }

        // Band count of an explicit band kind (PhasePlan.BandOf(layer, view)). openHalf / naTheme only matter for TempLines.
        public int BandCount(SurfaceBand band, bool chainReversed, RoadZones openHalf, bool naTheme)
        {
            switch (band)
            {
                case SurfaceBand.Carriageway: return math.max(1, IntervalCount);
                case SurfaceBand.TempLines: return TempLineCount(openHalf, chainReversed, naTheme);
                case SurfaceBand.CarriageHalf: return 2;
                default: return 1;
            }
        }

        // Lateral band [left, right] number i of a surface layer, EDGE frame. TempLines need the chain direction: use the
        // overload with chainReversed (this one assumes the edge runs along +u).
        public void Band(SurfaceLayer layer, int i, out float left, out float right) => Band(layer, i, false, out left, out right);

        // Legacy form: same, with the chain direction (chainReversed = the edge curve runs against +u, RoadZoneMath.ChainReversed).
        // TempLines here are the fixed pair (i = 0 divider side, i = 1 outer) of kStagedOpenHalf, EU theme.
        public void Band(SurfaceLayer layer, int i, bool chainReversed, out float left, out float right)
        {
            var b = PhasePlan.BandOf(layer);
            Band(b, layer, i, chainReversed, RRWConst.kStagedOpenHalf, false, out left, out right);
        }

        // Band i of an explicit band kind, EDGE frame.
        //  * Carriageway: interval i +- kAsphaltMargin;  * Footprint: +-(HalfWidth + margin);
        //  * CarriageHalf: i = 0 chain-LEFT half, i = 1 chain-RIGHT half (HalfBand);
        //  * TempLines: line i of the open half (TempLine); an index beyond TempLineCount gives an empty band at the split.
        public void Band(SurfaceBand band, SurfaceLayer layer, int i, bool chainReversed, RoadZones openHalf, bool naTheme, out float left, out float right)
        {
            switch (band)
            {
                case SurfaceBand.TempLines:
                    if (!TempLine(i, chainReversed, openHalf, naTheme, out left, out right))
                    {
                        float c = float.IsNaN(Split) ? Centre : Split;
                        left = right = c;
                    }
                    return;
                case SurfaceBand.CarriageHalf:
                    HalfBand(i == 0 ? RoadZones.LeftHalf : RoadZones.RightHalf, chainReversed, out left, out right);
                    return;
                case SurfaceBand.Carriageway:
                {
                    float2 iv = IntervalCount > 0 ? Interval(math.clamp(i, 0, IntervalCount - 1)) : new float2(CarriageLo, CarriageHi);
                    left = iv.x - RRWConst.kAsphaltMargin;
                    right = iv.y + RRWConst.kAsphaltMargin;
                    return;
                }
            }
            float m = PhasePlan.MarginOf(layer);
            left = -(HalfWidth + m);
            right = HalfWidth + m;
        }

        // Whole band (first to last interval) - legacy single-polygon form.
        public void Band(SurfaceLayer layer, out float left, out float right)
        {
            if (PhasePlan.BandOf(layer) == SurfaceBand.Carriageway)
            {
                left = CarriageLo - RRWConst.kAsphaltMargin;
                right = CarriageHi + RRWConst.kAsphaltMargin;
                return;
            }
            Band(layer, 0, out left, out right);
        }

        // The C4 per-half Fresh Asphalt (Cover) band of a CHAIN half, EDGE frame, asphalt margin included.
        // The centre-line strip DirSplitC -+ kCentreStripHalf belongs to the half painted first (kStagedWorksHalf); the other
        // half's band ends kHalfBandOverlap past the strip edge (no seam). A median between the direction groups is not covered
        // (each band stops at its own outermost interval edge + margin). No split (one direction): the whole carriage band.
        public void HalfBand(RoadZones half, bool chainReversed, out float left, out float right)
        {
            float m = RRWConst.kAsphaltMargin;
            float split = RoadZoneMath.DirSplitChain(this, chainReversed);
            RoadZoneMath.CarriageChain(this, chainReversed, out float lo, out float hi);
            float l, r;
            if (float.IsNaN(split)) { l = lo - m; r = hi + m; }
            else
            {
                // intervals in the chain frame: the inner edge of each side's outermost-reaching interval
                float hiLeft = float.MinValue, loRight = float.MaxValue;
                int n = math.max(1, IntervalCount);
                for (int k = 0; k < n; k++)
                {
                    float2 iv = IntervalCount > 0 ? Interval(k) : new float2(CarriageLo, CarriageHi);
                    float a = chainReversed ? -iv.y : iv.x, b = chainReversed ? -iv.x : iv.y;
                    if (a < split) hiLeft = math.max(hiLeft, b);
                    if (b > split) loRight = math.min(loRight, a);
                }
                if (hiLeft == float.MinValue) hiLeft = split;
                if (loRight == float.MaxValue) loRight = split;
                float strip = RRWConst.kCentreStripHalf, ov = RRWConst.kHalfBandOverlap;
                bool firstIsRight = (RRWConst.kStagedWorksHalf & RoadZones.RightHalf) != 0;
                bool wantLeft = (half & RoadZones.LeftHalf) != 0;
                if (wantLeft)
                {
                    l = lo - m;
                    float cut = firstIsRight ? split - strip + ov : split + strip;
                    r = math.min(cut, hiLeft + m);
                }
                else
                {
                    float cut = firstIsRight ? split - strip : split + strip - ov;
                    l = math.max(cut, loRight - m);
                    r = hi + m;
                }
                if (r < l) r = l;
            }
            if (chainReversed) { left = -r; right = -l; }
            else { left = l; right = r; }
        }

        // Number of yellow temporary lines of the open half. EU: divider-side line + outer edge line + one dashed
        // line per boundary between same-direction lanes of the open half; NA: the divider-side line only. 0 without a split.
        public int TempLineCount(RoadZones openHalf, bool chainReversed, bool naTheme)
        {
            var h = openHalf & RoadZones.Carriageway;
            if (float.IsNaN(Split) || h == RoadZones.None || h == RoadZones.Carriageway) return 0;
            if (naTheme) return 1;
            return 2 + LaneLineCount(h, chainReversed);
        }

        public static bool TempLineDashed(int i) => i >= 2;

        // Yellow line i of the open CHAIN half, EDGE frame [left, right] (width RRWGates.TempLineWidth). False when i is
        // beyond TempLineCount. i = 0 divider-side line at DirSplitC -+ (kTempLineDividerOffset + w/2) into the open half; i = 1
        // (EU) kTempLineEdgeInset inside the open half's outer DRIVE lane edge (parking excluded); i >= 2 (EU) dashed lane lines
        // (EdgeSection lane boundaries of the open half, kTempDash / kTempGap).
        public bool TempLine(int i, bool chainReversed, RoadZones openHalf, bool naTheme, out float left, out float right)
        {
            left = right = 0f;
            if (i < 0 || i >= TempLineCount(openHalf, chainReversed, naTheme)) return false;
            float w = math.max(0.02f, RRWGates.TempLineWidth);
            float hw = w * 0.5f;
            bool openLeft = (openHalf & RoadZones.LeftHalf) != 0;
            float split = RoadZoneMath.DirSplitChain(this, chainReversed);
            float c;
            if (i == 0) c = openLeft ? split - (RRWConst.kTempLineDividerOffset + hw) : split + (RRWConst.kTempLineDividerOffset + hw);
            else if (i == 1)
            {
                RoadZoneMath.DriveChain(this, chainReversed, out float dlo, out float dhi);
                c = openLeft ? dlo + RRWConst.kTempLineEdgeInset : dhi - RRWConst.kTempLineEdgeInset;
            }
            else
            {
                if (!LaneLineChain(openHalf, chainReversed, i - 2, out c)) return false;
            }
            float ce = chainReversed ? -c : c;   // back to the edge frame
            left = ce - hw;
            right = ce + hw;
            return true;
        }

        // Legacy form (kept for Surfaces until it moves to the band-kind overload): kStagedOpenHalf, EU theme; i = 0 divider, 1 outer.
        [System.Obsolete("Use TempLine(i, chainReversed, openHalf, naTheme, out left, out right)")]
        public void TempLine(int i, bool chainReversed, out float left, out float right)
        {
            if (!TempLine(i, chainReversed, RRWConst.kStagedOpenHalf, false, out left, out right))
            {
                float c = float.IsNaN(Split) ? Centre : Split;
                left = right = c;
            }
        }

        // Number of lane boundaries (same-direction lanes) of a CHAIN half.
        public int LaneLineCount(RoadZones half, bool chainReversed)
        {
            if (!LanesMeasured || float.IsNaN(DirSplit)) return 0;
            bool edgeLeft = ((half & RoadZones.LeftHalf) != 0) != chainReversed;
            float4 v = edgeLeft ? LaneLinesLeft : LaneLinesRight;
            int n = 0;
            for (int k = 0; k < 4; k++) if (!float.IsNaN(v[k])) n++;
            return n;
        }

        // k-th lane boundary of the open CHAIN half, chain frame.
        public bool LaneLineChain(RoadZones half, bool chainReversed, int k, out float c)
        {
            c = 0f;
            if (!LanesMeasured || float.IsNaN(DirSplit) || k < 0 || k > 3) return false;
            bool chainLeft = (half & RoadZones.LeftHalf) != 0;
            bool edgeLeft = chainLeft != chainReversed;
            float4 v = edgeLeft ? LaneLinesLeft : LaneLinesRight;
            float e = v[k];
            if (float.IsNaN(e)) return false;
            c = chainReversed ? -e : e;
            return true;
        }

        public static float FlatHalfWidthOf(float compositionWidth)
        {
            float w = compositionWidth;
            return w * 0.5f * math.saturate(1f - math.max(w * 0.2f, 3f) / math.max(1f, w));
        }
    }

    // The RoadZones geometry shared by Machines (occupancy report), Traffic (lane groups), Props and Surfaces.
    // Chain frame: lateral + = right of the chain direction (+u). The halves divide at EdgeSection.DirSplit (in the
    // chain frame: DirSplitChain), not at the curve centre; NaN (one direction) = no halves (Carriageway).
    // An EDGE-frame lateral (EdgeSection, lane offsets) converts with ToChain(l, ChainReversed(u0, u1)).
    public static class RoadZoneMath
    {
        public static bool ChainReversed(float chainU0, float chainU1) => chainU1 < chainU0;
        public static float ToChain(float lateralEdge, bool chainReversed) => chainReversed ? -lateralEdge : lateralEdge;

        // Carriageway edges in the CHAIN frame (lo < hi).
        public static void CarriageChain(in EdgeSection sec, bool chainReversed, out float lo, out float hi)
        {
            if (chainReversed) { lo = -sec.CarriageHi; hi = -sec.CarriageLo; }
            else { lo = sec.CarriageLo; hi = sec.CarriageHi; }
        }

        // Outer edges of the drive (car / track) lanes in the CHAIN frame (lo < hi); parking strips lie outside them.
        public static void DriveChain(in EdgeSection sec, bool chainReversed, out float lo, out float hi)
        {
            float a = sec.DriveLoE, b = sec.DriveHiE;
            if (chainReversed) { lo = -b; hi = -a; }
            else { lo = a; hi = b; }
        }

        // The half split in the CHAIN frame (NaN = one direction: no halves).
        public static float DirSplitChain(in EdgeSection sec, bool chainReversed)
        {
            float s = sec.Split;
            return float.IsNaN(s) ? float.NaN : ToChain(s, chainReversed);
        }

        // Half of a car / track lane by its TRAVEL DIRECTION. withChain = the lane runs along +u (lane tangent agrees
        // with the edge tangent XOR the chain is reversed). Right-hand traffic: with-chain lanes are on the chain-right.
        public static RoadZones OfCarLane(bool withChain, bool leftHandTraffic) =>
            withChain != leftHandTraffic ? RoadZones.RightHalf : RoadZones.LeftHalf;

        // Does traffic on this car half travel along +u (enters at the chain START)?
        public static bool TravelsWithChain(RoadZones half, bool leftHandTraffic) =>
            ((half & RoadZones.RightHalf) != 0) != leftHandTraffic;

        public static RoadZones Opposite(RoadZones half) =>
            half == RoadZones.LeftHalf ? RoadZones.RightHalf : half == RoadZones.RightHalf ? RoadZones.LeftHalf : half;

        // Sidewalk / parking group on the same chain side as a half.
        public static RoadZones SidewalkOf(RoadZones half) =>
            ((half & RoadZones.LeftHalf) != 0 ? RoadZones.SidewalkLeft : 0) | ((half & RoadZones.RightHalf) != 0 ? RoadZones.SidewalkRight : 0);
        public static RoadZones ParkingOf(RoadZones half) =>
            ((half & RoadZones.LeftHalf) != 0 ? RoadZones.ParkingLeft : 0) | ((half & RoadZones.RightHalf) != 0 ? RoadZones.ParkingRight : 0);
        // Car half on the same chain side as sidewalk / parking bits.
        public static RoadZones HalfOfSide(RoadZones side) =>
            ((side & (RoadZones.SidewalkLeft | RoadZones.ParkingLeft)) != 0 ? RoadZones.LeftHalf : 0)
            | ((side & (RoadZones.SidewalkRight | RoadZones.ParkingRight)) != 0 ? RoadZones.RightHalf : 0);

        // Width of a CHAIN half = carriageway edge to the split (drive lanes + parking of that half). 0 without a split.
        public static float HalfWidthChain(in EdgeSection sec, bool chainReversed, RoadZones half)
        {
            float split = DirSplitChain(sec, chainReversed);
            if (float.IsNaN(split)) return 0f;
            CarriageChain(sec, chainReversed, out float lo, out float hi);
            return (half & RoadZones.LeftHalf) != 0 ? math.max(0f, split - lo) : math.max(0f, hi - split);
        }

        // Zones a lateral interval [lMin, lMax] (chain frame, metres from the centre line) touches. Longitudinal Outside is
        // separate (OutsideFootprint): the caller ORs it in. Halves split at DirSplitChain (+-kZoneCentreTolerance counts
        // for both; NaN = both halves); sidewalks and parking per side (a box in a parking strip gets the half AND the parking bit).
        public static RoadZones OfLateral(float lMin, float lMax, in EdgeSection sec, bool chainReversed)
        {
            CarriageChain(sec, chainReversed, out float lo, out float hi);
            float tol = RRWConst.kZoneCentreTolerance;
            float hw = sec.HalfWidth;
            float split = DirSplitChain(sec, chainReversed);
            var z = RoadZones.None;
            if (float.IsNaN(split))
            {
                if (lMax > lo && lMin < hi) z |= RoadZones.Carriageway;
            }
            else
            {
                if (lMin < split + tol && lMax > lo) z |= RoadZones.LeftHalf;
                if (lMax > split - tol && lMin < hi) z |= RoadZones.RightHalf;
            }
            DriveChain(sec, chainReversed, out float dlo, out float dhi);
            if (dlo > lo + 0.3f && lMin < dlo && lMax > lo) z |= RoadZones.ParkingLeft;
            if (dhi < hi - 0.3f && lMax > dhi && lMin < hi) z |= RoadZones.ParkingRight;
            bool leftWalk = lo > -hw + 0.3f, rightWalk = hi < hw - 0.3f;     // a sidewalk band exists on that side
            if (leftWalk && lMin < lo && lMax > -hw) z |= RoadZones.SidewalkLeft;
            if (rightWalk && lMax > hi && lMin < hw) z |= RoadZones.SidewalkRight;
            return z;
        }

        // Group of one lane by GEOMETRY from its centre lateral (EDGE frame):
        //  * Car / Track: the half its centre lies in (split -+ kZoneCentreTolerance -> Carriageway; no split -> Carriageway);
        //  * Parking: ParkingLeft / ParkingRight by the side of the split (no split: of the carriage centre);
        //  * Pedestrian: centre on the carriageway interval (a path down the middle) -> the half(s) it lies in, as a car
        //    lane; otherwise SidewalkLeft / SidewalkRight by the side of the carriage centre.
        public static RoadZones OfLane(float lateralEdge, LaneKind kind, bool chainReversed, in EdgeSection sec)
        {
            float l = ToChain(lateralEdge, chainReversed);
            CarriageChain(sec, chainReversed, out float lo, out float hi);
            float split = DirSplitChain(sec, chainReversed);
            float mid = (lo + hi) * 0.5f;
            switch (kind)
            {
                case LaneKind.Parking:
                    return l < (float.IsNaN(split) ? mid : split) ? RoadZones.ParkingLeft : RoadZones.ParkingRight;
                case LaneKind.Pedestrian:
                    if (hi <= lo || l < lo || l > hi) return l < mid ? RoadZones.SidewalkLeft : RoadZones.SidewalkRight;
                    break;
            }
            if (float.IsNaN(split)) return RoadZones.Carriageway;
            float tol = RRWConst.kZoneCentreTolerance;
            if (l < split - tol) return RoadZones.LeftHalf;
            if (l > split + tol) return RoadZones.RightHalf;
            return RoadZones.Carriageway;
        }

        // Traffic lane groups: a car / track lane's half is decided by its DIRECTION (OfCarLane) and cross-checked by
        // geometry (OfLane); disagreement or no split -> Carriageway (opens only when both halves are open: fails closed).
        // laneWithEdge = the lane's curve tangent agrees with the edge curve tangent. Parking / pedestrian lanes: OfLane.
        public static RoadZones OfDirectedLane(float lateralEdge, LaneKind kind, bool laneWithEdge, bool chainReversed, bool leftHandTraffic, in EdgeSection sec)
        {
            var geo = OfLane(lateralEdge, kind, chainReversed, sec);
            if (kind == LaneKind.Parking || kind == LaneKind.Pedestrian) return geo;
            if (float.IsNaN(DirSplitChain(sec, chainReversed))) return RoadZones.Carriageway;
            var dir = OfCarLane(laneWithEdge != chainReversed, leftHandTraffic);
            return dir == geo ? dir : RoadZones.Carriageway;
        }

        // Legacy forms (centre split, both sidewalks as one group). Kept so Traffic compiles until it moves to OfDirectedLane.
        [System.Obsolete("Use OfDirectedLane / OfLane(lateral, LaneKind, chainReversed, section)")]
        public static RoadZones OfLane(float lateralEdge, bool pedestrian, bool chainReversed)
        {
            if (pedestrian) return RoadZones.Sidewalks;
            return OfCarriageLane(lateralEdge, chainReversed);
        }

        [System.Obsolete("Use OfDirectedLane / OfLane(lateral, LaneKind, chainReversed, section)")]
        public static RoadZones OfLane(float lateralEdge, bool pedestrian, bool chainReversed, float carriageLo, float carriageHi)
        {
            if (pedestrian && (lateralEdge < carriageLo || lateralEdge > carriageHi || carriageHi <= carriageLo)) return RoadZones.Sidewalks;
            return OfCarriageLane(lateralEdge, chainReversed);
        }

        private static RoadZones OfCarriageLane(float lateralEdge, bool chainReversed)
        {
            float l = ToChain(lateralEdge, chainReversed);
            float tol = RRWConst.kZoneCentreTolerance;
            if (l < -tol) return RoadZones.LeftHalf;
            if (l > tol) return RoadZones.RightHalf;
            return RoadZones.Carriageway;
        }

        // A lane of a Closed works edge carries traffic iff its whole group is open.
        public static bool LaneOpen(RoadZones laneGroup, RoadZones open) => laneGroup != RoadZones.None && (laneGroup & ~open) == 0;

        // A box spanning chain u [uMin, uMax] reaches outside the trimmed chain (junction nodes, finished roads).
        public static bool OutsideFootprint(float uMin, float uMax, float trim0, float trim1) =>
            uMin < trim0 - RRWConst.kZoneFootprintSlack || uMax > trim1 + RRWConst.kZoneFootprintSlack;

        // Short text of a zone set for logs and dev output ("L+R+walkL+parkR+out").
        public static string Describe(RoadZones z)
        {
            if (z == RoadZones.None) return "none";
            var sb = new System.Text.StringBuilder();
            void Add(RoadZones b, string n) { if ((z & b) != 0) { if (sb.Length > 0) sb.Append('+'); sb.Append(n); } }
            Add(RoadZones.LeftHalf, "L"); Add(RoadZones.RightHalf, "R");
            Add(RoadZones.SidewalkLeft, "walkL"); Add(RoadZones.SidewalkRight, "walkR");
            Add(RoadZones.ParkingLeft, "parkL"); Add(RoadZones.ParkingRight, "parkR");
            Add(RoadZones.Outside, "out");
            Add(RoadZones.LeftOuter | RoadZones.LeftInner | RoadZones.RightOuter | RoadZones.RightInner, "bands");
            return sb.ToString();
        }
    }

    // Chain coordinate -> world (main thread). Uses the registry's EdgeRecord.Arc and the saved chain coordinates of the
    // project's edges. Director (FrontPosition), UI focus and the dev camera share it, so they agree on "the front".
    public static class ChainMap
    {
        // pos = point on the road centre line at chain u (clamped to the chain); dir = unit chain direction (+u).
        public static bool Point(EntityManager em, ProjectRecord p, float u, out float3 pos, out float3 dir)
        {
            pos = default;
            dir = new float3(0f, 0f, 1f);
            if (p == null) return false;
            EdgeRecord best = null;
            float bu0 = 0f, bu1 = 0f, bestD = float.MaxValue;
            for (int i = 0; i < p.Edges.Count; i++)
            {
                Entity e = p.Edges[i];
                if (!SiteRegistry.TryGetEdge(e, out EdgeRecord r) || r.Arc == null || !em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                float lo = site.ChainLo, hi = site.ChainHi;
                float d = u < lo ? lo - u : u > hi ? u - hi : 0f;
                if (d < bestD) { bestD = d; best = r; bu0 = site.m_ChainU0; bu1 = site.m_ChainU1; }
                if (d <= 0f) break;
            }
            if (best == null) return false;
            float s = PhasePlan.EdgeS(u, bu0, bu1, best.Arc.Length);
            pos = best.Arc.Position(s);
            dir = best.Arc.Direction(s) * (bu1 >= bu0 ? 1f : -1f);
            return true;
        }

        // Main front F at render time from the project's ProgressModel (same domain as the machine jobs): smooth at any
        // time scale. Falls back to FrontU when the model has no slope.
        public static float RenderFront(ProjectRecord p, uint renderFrame, float frameTime)
        {
            if (p == null) return 0f;
            float prog = p.Model.PPerFrame != 0f ? p.Model.At(renderFrame, frameTime) : p.Progress;
            WorksPhase ph = PhasePlan.PhaseOf(p.Kind, prog, out float f);
            if (ph != p.Phase) return p.FrontU;   // never run ahead into the next phase's front before the Director switches
            // The C4 front depends on the stage (C4a / C4b painter sweeps when the side swap is on)
            var v = p.View();
            v.F = f;
            return PhasePlan.MainFront(v);
        }

        // Front of crew `crew` at render time (dev camera "follow crew i", UI focus). One crew: RenderFront.
        public static float RenderCrewFront(ProjectRecord p, uint renderFrame, float frameTime, int crew)
        {
            if (p == null) return 0f;
            float prog = p.Model.PPerFrame != 0f ? p.Model.At(renderFrame, frameTime) : p.Progress;
            WorksPhase ph = PhasePlan.PhaseOf(p.Kind, prog, out float f);
            var v = p.View();
            if (ph != p.Phase) return PhasePlan.CrewFront(v, crew);   // never run ahead into the next phase's front
            v.F = f;
            return PhasePlan.CrewFront(v, crew);
        }
    }
}
