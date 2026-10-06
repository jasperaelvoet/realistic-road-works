// The ECS reader of upgrade works: fills a CompositionLayout (pure, UpgradeTypes) from the live or temporary lanes and the edge
// outline of one road edge, measured in a SHARED frame (the edge frame of the NEW curve), so the old and the new layout of one
// upgrade diff without any shift mapping. Tools (classification at apply), UI (tooltips) and the Director (exact sub-strips,
// UpgradeEdgeState.Layout) all read through it. Main thread.
//
// Frame: every lateral is metres right (+) of the frame curve. A lane is measured from its own curve (LaneSection.Probe: samples
// projected onto the frame curve, median centre), extent = centre +- lane width / 2 (NetLaneData).
// Kinds: car lanes -> Car (Bike when the lane carries no cars), parking -> Parking, track -> Track, pedestrian lanes along the
// road -> Sidewalk; utility, connection, secondary, master and across-the-road lanes are skipped. Outline: the edge geometry's
// left and right boundaries at the same stations. Raised medians: composition pieces (BlockTraffic, or a raised median section
// without road lanes) placed through a calibration of the composition lane positions against the measured lanes
// (x_world = s * x_comp + o, s = +-1); a residual above kUwCalibMaxResidual makes the layout unreadable.
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using NetCurve = Game.Net.Curve;
using NetSubLane = Game.Net.SubLane;
using NetCarLane = Game.Net.CarLane;
using NetParkingLane = Game.Net.ParkingLane;
using NetTrackLane = Game.Net.TrackLane;
using NetPedestrianLane = Game.Net.PedestrianLane;
using NetMasterLane = Game.Net.MasterLane;
using NetUtilityLane = Game.Net.UtilityLane;
using NetConnectionLane = Game.Net.ConnectionLane;
using NetSecondaryLane = Game.Net.SecondaryLane;
using NetEdgeLane = Game.Net.EdgeLane;
using NetComposition = Game.Net.Composition;
using NetEdgeGeometry = Game.Net.EdgeGeometry;
using NetUpgraded = Game.Net.Upgraded;
using TempComp = Game.Tools.Temp;
using DeletedComp = Game.Common.Deleted;

namespace RealisticRoadWorks.V3
{
    // Calibration of a composition against measured lanes: x_world = S * x_comp + O.
    public struct UpgradeCalib
    {
        public bool Ok;
        public float S, O, Residual;
        public int N, Unmatched;
        public bool Ordered;          // the ordered fit won (else nearest neighbour)

        public float Map(float xComp) => S * xComp + O;

        public override string ToString() => Ok ? ("s=" + (S > 0 ? "+1" : "-1") + " o=" + RRWLog.F(O) + " res=" + RRWLog.F(Residual) + " n=" + N
                                                   + (Unmatched > 0 ? " unmatched=" + Unmatched : "") + (Ordered ? " ordered" : " nearest"))
                                                : "none(n=" + N + ")";
    }

    // Result of one read. Reuse one instance per caller (Clear on every read).
    public sealed class UpgradeLayoutRead
    {
        public struct CompLane { public float X, Width; public int Class; public LaneFlags Flags; }   // class 0 road, 1 parking, 2 walk
        public struct CompPiece { public float X, Width, Y; public NetSectionFlags Section; public NetPieceFlags Piece; }

        public readonly CompositionLayout L = new CompositionLayout();
        public bool FromComposition;              // ReadFromComposition placed the strips (no usable lanes)
        public int Cars, Bikes, Parks, Tracks, Peds, Medians;
        public bool HasGeometry;
        public Entity Comp;
        public float MiddleOffset = float.NaN;
        public CompositionFlags CompFlags;
        public bool CalibUsed;
        public UpgradeCalib Calib;
        public readonly List<CompLane> CompLanes = new List<CompLane>();
        public readonly List<CompPiece> CompPieces = new List<CompPiece>();

        public void Clear()
        {
            L.Clear();
            FromComposition = false;
            Cars = Bikes = Parks = Tracks = Peds = Medians = 0;
            HasGeometry = false;
            Comp = Entity.Null;
            MiddleOffset = float.NaN;
            CompFlags = default;
            CalibUsed = false;
            Calib = default;
            CompLanes.Clear();
            CompPieces.Clear();
        }

        public string Describe() => (FromComposition ? "composition " : "lanes ") + L.Describe() + " calib=" + (CalibUsed ? Calib.ToString() : "-");
    }

    // Game-free helpers of the reader.
    public static class UpgradeLayoutMath
    {
        // Calibrates composition lane positions comp[i] (class compClass[i]) against measured lane centres meas[j] (class
        // measClass[j]): x_world = s * x_comp + o. When both lists have the same length and the same class sequence (in order for
        // s = +1, reversed for s = -1) the ordered least-squares fit is used; otherwise (and as a cross-check) every composition
        // lane is paired with the nearest measured lane of its class and o is refitted a few times. The better fit wins.
        // residual = largest |error| of the chosen fit; unmatched = composition lanes whose class has no measured lane.
        public static bool Calibrate(IList<float> comp, IList<int> compClass, IList<float> meas, IList<int> measClass,
                                     out float s, out float o, out float residual, out int n, out int unmatched, out bool ordered)
        {
            s = 1f; o = 0f; residual = float.PositiveInfinity; n = 0; unmatched = 0; ordered = false;
            if (comp == null || meas == null || comp.Count == 0 || meas.Count == 0) return false;
            var cs = Sorted(comp, compClass);
            var ms = Sorted(meas, measClass);

            // ordered fit
            if (cs.Count == ms.Count)
            {
                for (int sign = 1; sign >= -1; sign -= 2)
                {
                    bool same = true;
                    for (int i = 0; i < cs.Count && same; i++) same = cs[i].y == ms[sign > 0 ? i : ms.Count - 1 - i].y;
                    if (!same) continue;
                    float o1 = OrderedOffset(cs, ms, sign);
                    float r1 = Residual(cs, ms, sign, o1);
                    if (r1 < residual) { residual = r1; s = sign; o = o1; n = cs.Count; unmatched = 0; ordered = true; }
                }
            }

            // nearest-neighbour fit per sign
            for (int sign = 1; sign >= -1; sign -= 2)
            {
                float off = Mean(ms) - sign * Mean(cs);
                float res = float.PositiveInfinity;
                int pairs = 0, un = 0;
                for (int it = 0; it < 6; it++)
                {
                    double sum = 0; pairs = 0; un = 0;
                    res = 0f;
                    for (int i = 0; i < cs.Count; i++)
                    {
                        float want = sign * cs[i].x + off;
                        int best = -1; float bd = float.MaxValue;
                        for (int j = 0; j < ms.Count; j++)
                        {
                            if (ms[j].y != cs[i].y) continue;
                            float d = math.abs(ms[j].x - want);
                            if (d < bd) { bd = d; best = j; }
                        }
                        if (best < 0) { un++; continue; }
                        sum += ms[best].x - sign * cs[i].x;
                        pairs++;
                    }
                    if (pairs == 0) break;
                    off = (float)(sum / pairs);
                }
                if (pairs == 0) continue;
                for (int i = 0; i < cs.Count; i++)
                {
                    float want = sign * cs[i].x + off;
                    float bd = float.MaxValue;
                    for (int j = 0; j < ms.Count; j++) if (ms[j].y == cs[i].y) bd = math.min(bd, math.abs(ms[j].x - want));
                    if (bd < float.MaxValue) res = math.max(res, bd);
                }
                if (res < residual - 1e-5f) { residual = res; s = sign; o = off; n = pairs; unmatched = un; ordered = false; }
            }
            return n > 0;
        }

        private static List<float2> Sorted(IList<float> x, IList<int> cls)
        {
            var l = new List<float2>(x.Count);
            for (int i = 0; i < x.Count; i++) l.Add(new float2(x[i], cls != null && i < cls.Count ? cls[i] : 0));
            l.Sort((a, b) => a.x.CompareTo(b.x));
            return l;
        }

        private static float Mean(List<float2> l)
        {
            double s = 0;
            for (int i = 0; i < l.Count; i++) s += l[i].x;
            return l.Count > 0 ? (float)(s / l.Count) : 0f;
        }

        private static float OrderedOffset(List<float2> cs, List<float2> ms, int sign)
        {
            double sum = 0;
            for (int i = 0; i < cs.Count; i++) sum += ms[sign > 0 ? i : ms.Count - 1 - i].x - sign * cs[i].x;
            return (float)(sum / cs.Count);
        }

        private static float Residual(List<float2> cs, List<float2> ms, int sign, float off)
        {
            float r = 0f;
            for (int i = 0; i < cs.Count; i++) r = math.max(r, math.abs(ms[sign > 0 ? i : ms.Count - 1 - i].x - (sign * cs[i].x + off)));
            return r;
        }

        // Moves a layout measured in another edge's frame into the frame of a curve that lies `shift` metres right of that edge
        // (x' = x - shift), mirrored when the curve runs the other way.
        public static void Reframe(CompositionLayout l, float shift, bool mirror)
        {
            l.OuterL -= shift;
            l.OuterR -= shift;
            for (int i = 0; i < l.Count; i++)
            {
                l.Strips[i].Lo -= shift;
                l.Strips[i].Hi -= shift;
            }
            for (int i = 0; i < l.TrackLanes; i++) l.Tracks[i] -= shift;
            if (mirror) l.MirrorInPlace();
        }

        // Clips a raised piece [lo, hi] to the carriageway interior of a normalised layout: the part between the outermost lane
        // strips. False when nothing is left (the piece lies outside the carriageway: sidewalk / verge are raised anyway).
        public static bool ClipToCarriageway(CompositionLayout l, ref float lo, ref float hi)
        {
            float cLo = l.CarriageLo, cHi = l.CarriageHi;
            if (float.IsNaN(cLo)) return false;
            lo = math.max(lo, cLo);
            hi = math.min(hi, cHi);
            return hi - lo >= 0.05f;
        }

        // Mean lateral of a boundary curve against a frame (stations t = .1 .. .9 of the whole boundary: a covers the first half,
        // b the second, as the edge geometry's start and end halves).
        public static float BoundaryLateral(EdgeArc frame, Bezier4x3 a, Bezier4x3 b)
        {
            float sum = 0f;
            for (int k = 0; k < 5; k++)
            {
                float t = 0.1f + 0.2f * k;
                float3 p = t < 0.5f ? MathUtils.Position(a, t * 2f) : MathUtils.Position(b, t * 2f - 1f);
                float s = frame.Project(p);
                sum += math.dot((p - frame.Position(s)).xz, frame.Right(s).xz);
            }
            return sum / 5f;
        }
    }

    public static class UpgradeLayoutReader
    {
        // Reads the layout of `edge` in `frame` (the NEW curve's arc). Live lanes for a live edge, temporary lanes for a temp edge.
        // Normalised on return. False when the result is unreadable (no road lane, no outline); r.L.Readable says the same.
        public static bool Read(EntityManager em, Entity edge, EdgeArc frame, UpgradeLayoutRead r)
        {
            r.Clear();
            if (frame == null || !em.Exists(edge)) { r.L.Readable = false; return false; }
            ReadComposition(em, edge, r);
            r.HasGeometry = ReadOutline(em, edge, frame, r.L);
            var meas = new List<float>();
            var measCls = new List<int>();
            if (em.HasBuffer<NetSubLane>(edge))
            {
                var buf = em.GetBuffer<NetSubLane>(edge, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    Entity lane = buf[i].m_SubLane;
                    if (lane == Entity.Null || !em.Exists(lane) || em.HasComponent<DeletedComp>(lane)) continue;
                    if (!em.HasComponent<NetCurve>(lane)) continue;
                    if (em.HasComponent<NetUtilityLane>(lane) || em.HasComponent<NetConnectionLane>(lane) || em.HasComponent<NetSecondaryLane>(lane)) continue;
                    bool car = em.HasComponent<NetCarLane>(lane), park = em.HasComponent<NetParkingLane>(lane);
                    bool track = em.HasComponent<NetTrackLane>(lane), ped = em.HasComponent<NetPedestrianLane>(lane);
                    if (!car && !park && !track && !ped) continue;
                    if (em.HasComponent<NetMasterLane>(lane)) continue;
                    if (em.HasComponent<NetEdgeLane>(lane))
                    {
                        float2 dl = em.GetComponentData<NetEdgeLane>(lane).m_EdgeDelta;
                        if (math.abs(dl.x - dl.y) < 1e-4f) continue;   // a connector at one position of the edge
                    }
                    float width = park ? 2.5f : ped ? 2f : 3f;
                    bool bikeOnly = false;
                    if (em.HasComponent<PrefabRef>(lane))
                    {
                        var lp = em.GetComponentData<PrefabRef>(lane).m_Prefab;
                        if (em.HasComponent<NetLaneData>(lp)) width = em.GetComponentData<NetLaneData>(lp).m_Width;
                        if (car && em.HasComponent<CarLaneData>(lp) && (em.GetComponentData<CarLaneData>(lp).m_RoadTypes & Game.Net.RoadTypes.Car) == 0) bikeOnly = true;
                    }
                    var p = LaneSection.Probe(frame, em.GetComponentData<NetCurve>(lane).m_Bezier, width * 0.5f, LaneBits.None);
                    if (p.Dir == 0) continue;
                    bool twoway = (car && (em.GetComponentData<NetCarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Twoway) != 0)
                                  || (track && (em.GetComponentData<NetTrackLane>(lane).m_Flags & Game.Net.TrackLaneFlags.Twoway) != 0);
                    float hw = width * 0.5f;
                    int cls;
                    if (car)
                    {
                        var k = bikeOnly ? StripKind.Bike : StripKind.Car;
                        sbyte d = twoway ? (sbyte)0 : p.Dir;
                        r.L.Add(p.Centre - hw, p.Centre + hw, k, d);
                        if (bikeOnly) r.Bikes++; else r.Cars++;
                        if (track) { r.L.Add(p.Centre - hw, p.Centre + hw, StripKind.Track, d); r.Tracks++; }
                        cls = 0;
                    }
                    else if (track)
                    {
                        r.L.Add(p.Centre - hw, p.Centre + hw, StripKind.Track, twoway ? (sbyte)0 : p.Dir);
                        r.Tracks++;
                        cls = 0;
                    }
                    else if (park)
                    {
                        r.L.Add(p.Centre - hw, p.Centre + hw, StripKind.Parking, p.Dir);
                        r.Parks++;
                        cls = 1;
                    }
                    else
                    {
                        r.L.Add(p.Centre - hw, p.Centre + hw, StripKind.Sidewalk, 0);
                        r.Peds++;
                        cls = 2;
                    }
                    meas.Add(p.Centre);
                    measCls.Add(cls);
                }
            }
            r.L.Normalise();

            // calibration of the composition against the measured lanes, raised medians from the composition pieces
            if (r.CompLanes.Count > 0 && meas.Count > 0)
            {
                var cx = new List<float>(r.CompLanes.Count);
                var cc = new List<int>(r.CompLanes.Count);
                for (int i = 0; i < r.CompLanes.Count; i++) { cx.Add(r.CompLanes[i].X); cc.Add(r.CompLanes[i].Class); }
                if (UpgradeLayoutMath.Calibrate(cx, cc, meas, measCls, out float s, out float o, out float res, out int n, out int un, out bool ordered))
                {
                    r.CalibUsed = true;
                    r.Calib = new UpgradeCalib { Ok = true, S = s, O = o, Residual = res, N = n, Unmatched = un, Ordered = ordered };
                    r.L.CalibResidual = math.max(res, 1e-4f);
                    if (res <= RRWConst.kUwCalibMaxResidual) AddMedians(r);   // a bad fit would place the medians anywhere
                }
            }
            Finish(r);
            return r.L.Readable;
        }

        // Fallback when an edge has no usable lanes (temp lanes missing): strips from the composition lanes, placed with a
        // calibration fitted elsewhere (normally on the old edge of the same upgrade, moved by the shift).
        public static bool ReadFromComposition(EntityManager em, Entity edge, EdgeArc frame, in UpgradeCalib calib, UpgradeLayoutRead r)
        {
            r.Clear();
            if (frame == null || !em.Exists(edge)) { r.L.Readable = false; return false; }
            ReadComposition(em, edge, r);
            r.HasGeometry = ReadOutline(em, edge, frame, r.L);
            r.FromComposition = true;
            for (int i = 0; i < r.CompLanes.Count; i++)
            {
                var c = r.CompLanes[i];
                float x = calib.Map(c.X), hw = c.Width * 0.5f;
                bool twoway = (c.Flags & LaneFlags.Twoway) != 0;
                sbyte dir = twoway ? (sbyte)0 : (sbyte)(((c.Flags & LaneFlags.Invert) != 0 ? -1 : 1) * (calib.S >= 0f ? 1 : -1));
                if (c.Class == 2) { r.L.Add(x - hw, x + hw, StripKind.Sidewalk, 0); r.Peds++; }
                else if (c.Class == 1) { r.L.Add(x - hw, x + hw, StripKind.Parking, dir); r.Parks++; }
                else if ((c.Flags & LaneFlags.Track) != 0 && (c.Flags & LaneFlags.Road) == 0) { r.L.Add(x - hw, x + hw, StripKind.Track, dir); r.Tracks++; }
                else
                {
                    bool bike = (c.Flags & LaneFlags.BicyclesOnly) != 0;
                    r.L.Add(x - hw, x + hw, bike ? StripKind.Bike : StripKind.Car, dir);
                    if ((c.Flags & LaneFlags.Track) != 0) { r.L.Add(x - hw, x + hw, StripKind.Track, dir); r.Tracks++; }
                    if (bike) r.Bikes++; else r.Cars++;
                }
            }
            r.L.Normalise();
            r.CalibUsed = calib.Ok;
            r.Calib = calib;
            if (calib.Ok)
            {
                r.L.CalibResidual = math.max(calib.Residual, 1e-4f);
                if (calib.Residual <= RRWConst.kUwCalibMaxResidual) AddMedians(r);
            }
            Finish(r);
            return r.L.Readable;
        }

        // Classifies the change of one edge: newEdge = the edge with the new road (a Temp edge while the tool runs, or the live edge),
        // oldEdge = the road it replaces at the same place (Temp.m_Original of a Modify / Upgrade / Replace temp). Both layouts are
        // read in the NEW curve's frame; when the new edge has no usable lanes its composition is placed with the old edge's
        // calibration, moved by the shift and the change of the composition's middle offset. Flags first (UpgradeDiff.FlagsFirst),
        // then the alignment (UpgradeDiff.Align), the diff (UpgradeDiff.Classify) and the verdict (UpgradeDiff.Decide). spec: the
        // bands in the new edge's frame; instant: same road type with wall changes (applied at once); aligned: the new curve is
        // the old curve shifted sideways. spec.Shift / spec.Reversed carry the alignment (UpgradeAlign.Shift / Reversed: the new
        // curve lies Shift metres right of the old one, the frame UpgradePlan.Reframe takes; meaningful when aligned). neu / old:
        // caller-owned buffers (the reads stay there for logs). Returns the final class; Structural (Unreadable) when either edge
        // has no curve.
        public static UpgradeClass Classify(EntityManager em, Entity newEdge, Entity oldEdge, UpgradeLayoutRead neu, UpgradeLayoutRead old,
                                            out UpgradeSpec spec, out bool instant, out bool aligned, List<string> trace = null) =>
            Classify(em, newEdge, oldEdge, neu, old, out spec, out instant, out aligned, out _, trace);

        // Same, also handing out the whole alignment result (default when either edge has no curve).
        public static UpgradeClass Classify(EntityManager em, Entity newEdge, Entity oldEdge, UpgradeLayoutRead neu, UpgradeLayoutRead old,
                                            out UpgradeSpec spec, out bool instant, out bool aligned, out UpgradeAlign align, List<string> trace = null)
        {
            spec = default;
            instant = false;
            aligned = false;
            align = default;
            if (neu == null || old == null || !em.Exists(newEdge) || !em.Exists(oldEdge)
                || !em.HasComponent<NetCurve>(newEdge) || !em.HasComponent<NetCurve>(oldEdge))
            {
                spec.Class = UpgradeClass.Structural;
                spec.Why = UpgradeStructural.Unreadable;
                return UpgradeClass.Structural;
            }
            var curve = em.GetComponentData<NetCurve>(newEdge).m_Bezier;
            var oldCurve = em.GetComponentData<NetCurve>(oldEdge).m_Bezier;
            var arc = new EdgeArc(curve);
            Read(em, newEdge, arc, neu);
            Read(em, oldEdge, arc, old);
            aligned = UpgradeDiff.Align(new EdgeArc(oldCurve), curve, out var al);
            align = al;

            // no usable new lanes: the new composition placed with the old edge's calibration
            if (neu.Cars + neu.Bikes + neu.Tracks == 0 && old.CalibUsed)
            {
                var c = old.Calib;
                float mOld = old.MiddleOffset, mNew = float.NaN;
                if (em.HasComponent<NetComposition>(newEdge))
                {
                    var tc = em.GetComponentData<NetComposition>(newEdge).m_Edge;
                    if (EcsUtil.ValidComposition(em, tc)) mNew = em.GetComponentData<NetCompositionData>(tc).m_MiddleOffset;
                }
                float dm = float.IsNaN(mOld) || float.IsNaN(mNew) ? 0f : mNew - mOld;
                c.O = c.O + al.Shift + c.S * dm;
                ReadFromComposition(em, newEdge, arc, c, neu);
            }

            bool samePrefab = em.HasComponent<PrefabRef>(newEdge) && em.HasComponent<PrefabRef>(oldEdge)
                              && em.GetComponentData<PrefabRef>(newEdge).m_Prefab == em.GetComponentData<PrefabRef>(oldEdge).m_Prefab;
            var oldUp = em.HasComponent<NetUpgraded>(oldEdge) ? em.GetComponentData<NetUpgraded>(oldEdge).m_Flags : default;
            var newUp = em.HasComponent<NetUpgraded>(newEdge) ? em.GetComponentData<NetUpgraded>(newEdge).m_Flags : default;
            var fv = UpgradeDiff.FlagsFirst(samePrefab, oldUp, newUp);
            var cls = UpgradeDiff.Classify(old.L, neu.L, out spec, trace);
            var verdict = UpgradeDiff.Decide(fv, aligned, cls, ref spec, out instant);
            spec.Shift = al.Shift;
            spec.Reversed = al.Reversed;
            return verdict;
        }

        // Junction trims of an edge in a frame (arc distance from each curve end to where the edge geometry starts / ends).
        public static void Trims(EntityManager em, Entity edge, EdgeArc frame, out float trimStart, out float trimEnd)
        {
            trimStart = trimEnd = 0f;
            if (frame == null || !em.HasComponent<NetEdgeGeometry>(edge)) return;
            var g = em.GetComponentData<NetEdgeGeometry>(edge);
            float a = math.max(frame.Project(g.m_Start.m_Left.a), frame.Project(g.m_Start.m_Right.a));
            float d = math.min(frame.Project(g.m_End.m_Left.d), frame.Project(g.m_End.m_Right.d));
            if (d < a) { float x = a; a = frame.Length - d; d = frame.Length - x; }
            trimStart = math.clamp(a, 0f, frame.Length * 0.5f);
            trimEnd = math.clamp(frame.Length - d, 0f, frame.Length * 0.5f);
        }

        private static void Finish(UpgradeLayoutRead r)
        {
            r.L.Elevated = (r.CompFlags.m_General & CompositionFlags.General.Elevated) != 0;
            r.L.Tunnel = (r.CompFlags.m_General & CompositionFlags.General.Tunnel) != 0;
            r.L.Normalise();
            r.L.Readable = r.HasGeometry && r.Cars + r.Bikes + r.Tracks > 0 && !float.IsNaN(r.L.CarriageLo);
        }

        private static void AddMedians(UpgradeLayoutRead r)
        {
            int added = 0;
            for (int i = 0; i < r.CompPieces.Count; i++)
            {
                var pc = r.CompPieces[i];
                bool block = (pc.Piece & NetPieceFlags.BlockTraffic) != 0;
                bool raisedMedian = (pc.Section & NetSectionFlags.Median) != 0 && (pc.Piece & NetPieceFlags.HasRoadLanes) == 0 && pc.Y > 0.05f;
                if (!block && !raisedMedian) continue;
                float a = r.Calib.Map(pc.X - pc.Width * 0.5f), b = r.Calib.Map(pc.X + pc.Width * 0.5f);
                float lo = math.min(a, b), hi = math.max(a, b);
                if (!UpgradeLayoutMath.ClipToCarriageway(r.L, ref lo, ref hi)) continue;
                if (r.L.Add(lo, hi, StripKind.Median, 0)) added++;
            }
            r.Medians = added;
            if (added > 0) r.L.Normalise();
        }

        private static void ReadComposition(EntityManager em, Entity edge, UpgradeLayoutRead r)
        {
            if (!em.HasComponent<NetComposition>(edge)) return;
            Entity c = em.GetComponentData<NetComposition>(edge).m_Edge;
            if (!EcsUtil.ValidComposition(em, c)) return;
            r.Comp = c;
            var cd = em.GetComponentData<NetCompositionData>(c);
            r.MiddleOffset = cd.m_MiddleOffset;
            r.CompFlags = cd.m_Flags;
            if (em.HasBuffer<NetCompositionLane>(c))
            {
                var lanes = em.GetBuffer<NetCompositionLane>(c, true);
                for (int i = 0; i < lanes.Length; i++)
                {
                    var l = lanes[i];
                    var f = l.m_Flags;
                    if ((f & (LaneFlags.Master | LaneFlags.Utility | LaneFlags.Virtual | LaneFlags.CrossRoad | LaneFlags.Secondary)) != 0) continue;
                    int cls;
                    if ((f & LaneFlags.Pedestrian) != 0) cls = 2;
                    else if ((f & LaneFlags.Parking) != 0) cls = 1;
                    else if ((f & (LaneFlags.Road | LaneFlags.Track)) != 0) cls = 0;
                    else continue;
                    float w = cls == 1 ? 2.5f : cls == 2 ? 2f : 3f;
                    if (l.m_Lane != Entity.Null && em.HasComponent<NetLaneData>(l.m_Lane)) w = em.GetComponentData<NetLaneData>(l.m_Lane).m_Width;
                    r.CompLanes.Add(new UpgradeLayoutRead.CompLane { X = l.m_Position.x, Width = w, Class = cls, Flags = f });
                }
            }
            if (em.HasBuffer<NetCompositionPiece>(c))
            {
                var pieces = em.GetBuffer<NetCompositionPiece>(c, true);
                for (int i = 0; i < pieces.Length; i++)
                    r.CompPieces.Add(new UpgradeLayoutRead.CompPiece { X = pieces[i].m_Offset.x, Y = pieces[i].m_Offset.y, Width = pieces[i].m_Size.x,
                                                                        Section = pieces[i].m_SectionFlags, Piece = pieces[i].m_PieceFlags });
            }
        }

        // Road outline from the edge geometry: mean lateral of the left and right boundaries at the frame stations.
        private static bool ReadOutline(EntityManager em, Entity edge, EdgeArc frame, CompositionLayout l)
        {
            if (!em.HasComponent<NetEdgeGeometry>(edge)) return false;
            var g = em.GetComponentData<NetEdgeGeometry>(edge);
            float a = UpgradeLayoutMath.BoundaryLateral(frame, g.m_Start.m_Left, g.m_End.m_Left);
            float b = UpgradeLayoutMath.BoundaryLateral(frame, g.m_Start.m_Right, g.m_End.m_Right);
            if (float.IsNaN(a) || float.IsNaN(b) || math.abs(a - b) < 0.5f) return false;
            l.OuterL = math.min(a, b);
            l.OuterR = math.max(a, b);
            return true;
        }
    }
}
