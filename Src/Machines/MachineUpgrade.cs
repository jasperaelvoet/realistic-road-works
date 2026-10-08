using System;
using System.Collections.Generic;
using Unity.Mathematics;

// Upgrade works (mode H): where the machines of a band crew may stand. A band crew works one chain band of the layout window
// (CrewPlan.Band) with laterals in metres. Its boxes stay inside the band's machine-safe sub-strips on the edge under them
// (PhasePlan.MachineSafe of UpgradeZones.SubStripStateOf: a group the stage closed and drained, a lane Traffic dropped for this
// band with registered and drained blockers and no intrusion, or verge / terrain; never a parking group
// that still holds parked cars; remove bands: the removed strip outside the new road only), kUwBandMachineClear off every part
// that is not machine-safe and kUwFenceClear off a sidewalk (fence side). Dropped lanes carry traffic in the approach zones before
// the first blocker, so a crew that works in dropped lanes stays between the first and the last blocker of each edge (Traffic's
// drop range; kUwBlockerNodeSetback from the nodes until it is written), and a machine that worked there keeps that limit when
// it leaves, whatever the window's primitive is by then. A standing box keeps out of the driveway keep-outs on its side. Main
// thread only (Modification1); per-update caches in PlanContext.Uw.
namespace RealisticRoadWorks.V3.Machines
{
    // One machine-safe run of a band on one edge (CHAIN frame). ClrLo / ClrHi: the clearance a box keeps from its ends.
    internal struct UwRun
    {
        public bool Ok;
        public float Lo, Hi, ClrLo, ClrHi;
        public bool Drop;             // the run holds a dropped lane (machine-safe only through the lane drop)
        public float Width => Hi - Lo;
    }

    // Per-update cache of the runs and stretches of one project (PlanContext.Uw).
    internal sealed class UwCache
    {
        public uint Stamp = uint.MaxValue;
        public ProjectRecord Project;
        public TrackData Track;
        public int TrackRevision = int.MinValue;
        public readonly UwBandRuns[] Bands = NewBands();

        static UwBandRuns[] NewBands()
        {
            var a = new UwBandRuns[RRWConst.kUwMaxChainBands];
            for (int i = 0; i < a.Length; i++) a[i] = new UwBandRuns();
            return a;
        }

        public void Validate(PlanContext c)
        {
            uint ui = RRWClock.UpdateIndex;
            int rev = c.Track != null ? c.Track.Revision : int.MinValue;
            if (Stamp == ui && Project == c.Project && Track == c.Track && TrackRevision == rev) return;
            Stamp = ui;
            Project = c.Project;
            Track = c.Track;
            TrackRevision = rev;
            for (int i = 0; i < Bands.Length; i++) Bands[i].Reset(c.Track != null ? c.Track.Edges.Count : 0);
        }
    }

    internal sealed class UwBandRuns
    {
        public bool StretchDone, StretchOk, StretchDrops;
        public float S0, S1;
        public bool LatchDone, LatchOk;     // the lane-drop stretch for a machine that worked in dropped lanes (UwDropStretch)
        public float L0, L1;
        public UwRun[] Interior = new UwRun[0], Node = new UwRun[0];
        public byte[] Done = new byte[0];   // bit 0 interior, bit 1 node zone

        public void Reset(int edges)
        {
            StretchDone = false;
            LatchDone = false;
            if (Done.Length < edges) { Interior = new UwRun[edges]; Node = new UwRun[edges]; Done = new byte[edges]; }
            for (int i = 0; i < Done.Length; i++) Done[i] = 0;
        }
    }

    public static partial class Choreo
    {
        // Chain band whose sub-strips bound p: its own band (spawned by a band crew), else the context crew's band for a probe.
        // -1 = none (the re-marking crew, a project that is not mode H).
        internal static int UwBandOf(PlanContext c, Puppet p)
        {
            if (!c.Upgrade) return -1;
            if (p != null && p.UwBand >= 0) return p.UwBand;
            if (p != null && (p.PostSite || p.Outgoing)) return -1;
            return c.LateralMetres ? c.UwBand : -1;
        }

        // Track edge whose chain range holds u (nearest beyond the ends); -1 without a track.
        internal static int UwEdgeAt(TrackData td, float u)
        {
            if (td == null || !td.Valid || td.Edges.Count == 0) return -1;
            int best = 0;
            float bd = float.MaxValue;
            for (int i = 0; i < td.Edges.Count; i++)
            {
                var e = td.Edges[i];
                float d = u < e.Lo ? e.Lo - u : u > e.Hi ? u - e.Hi : 0f;
                if (d < bd) { bd = d; best = i; if (d <= 0f) break; }
            }
            return best;
        }

        // A box [u0, u1] reaches into the approach zone of a node of track edge ei (where dropped lanes still carry traffic):
        // outside Traffic's range between the first and the last blocker of the edge, else kUwBlockerNodeSetback from its nodes.
        internal static bool UwNearNode(TrackData td, int ei, float u0, float u1)
        {
            UwDropInterior(td.Edges[ei], out float a, out float b);
            return u0 < a || u1 > b;
        }

        // The part of track edge e where its dropped lanes carry no traffic (chain u): Traffic's range between the first and the
        // last blocker when it wrote one, else kUwBlockerNodeSetback inside both nodes.
        internal static void UwDropInterior(in TrackEdgeInfo e, out float a, out float b)
        {
            a = e.Lo + RRWConst.kUwBlockerNodeSetback;
            b = e.Hi - RRWConst.kUwBlockerNodeSetback;
            if (SiteRegistry.TryGetEdge(e.Edge, out var rec) && rec.Upgrade != null && rec.Upgrade.DropSafeRange(out float d0, out float d1))
            {
                a = math.max(e.Lo, d0);
                b = math.min(e.Hi, d1);
            }
        }

        // The lateral half width a box of p keeps clear of: the box, or for the excavator the house swept by the front-dump slew.
        internal static float UwHalfWidth(Puppet p) =>
            p.Kind == MachineKind.Excavator ? UwSweptHalfWid(p, MxConst.kUwPlanSlewDeg) : p.BoxHalfWid;

        // Lateral half width of an excavator box turned by +-deg about its origin (the swing axis): the corners of the house and
        // of the boom at rest swing out by their distance from the axis x sin(deg).
        internal static float UwSweptHalfWid(Puppet p, float deg)
        {
            float t = math.radians(math.clamp(math.abs(deg), 0f, 90f));
            float front = math.max(0f, p.BoxHalfLen + p.BoxOffZ), tail = math.max(0f, p.BoxHalfLen - p.BoxOffZ);
            return math.max(p.BoxHalfWid, p.BoxHalfWid * math.cos(t) + math.max(front, tail) * math.sin(t));
        }

        // LatRange of a mode H band crew (true = handled): the box centre range inside the band's machine-safe run at u. A puppet
        // that leaves keeps its own lateral (it drives along its closed sub-strip, never across onto another part of the road).
        internal static bool UwLatRange(PlanContext c, Puppet p, float u, out bool fit, ref float a, ref float b)
        {
            fit = true;
            if (p.PostSite || p.Outgoing)
            {
                if (p.UwBand < 0) return false;
                a = float.NegativeInfinity; b = float.PositiveInfinity;
                if (p.Plan.Count <= 0) return true;
                // pinned to the lateral it drives on now (and the end of its current merge): never a step aside out of its band
                MachineMotion.State(p.Plan, c.Clk, c.Now, out var s0);
                MachineMotion.State(p.Plan, c.Clk, c.Now + 2.0, out var s1);
                a = math.min(s0.Lat, s1.Lat) - 0.05f;
                b = math.max(s0.Lat, s1.Lat) + 0.05f;
                return true;
            }
            int band = UwBandOf(c, p);
            if (band < 0) return false;
            float hw = UwHalfWidth(p);
            float hl = p.BoxHalfLen + math.abs(p.BoxOffZ);
            int ei = UwEdgeAt(c.Track, u);
            if (ei < 0 || !UwRunAt(c, ei, band, UwNearNode(c.Track, ei, u - hl, u + hl), out var r))
            {
                // no machine-safe run here: nothing fits (the band's middle; the crew does not spawn, a live puppet vacates)
                var bv = c.View.Upgrade.Band(band);
                a = b = bv.Centre;
                fit = false;
                return true;
            }
            if (r.Drop && p.UwBand >= 0) p.UwDropStretch = true;   // planned on a dropped lane: keeps the lane-drop stretch
            a = r.Lo + hw + r.ClrLo;
            b = r.Hi - hw - r.ClrHi;
            if (a <= b) return true;
            a = b = 0.5f * (r.Lo + r.Hi);
            fit = false;
            return true;
        }

        // Cached machine-safe run of band `band` on track edge ei (interior or node-zone variant).
        internal static bool UwRunAt(PlanContext c, int ei, int band, bool nodeZone, out UwRun run)
        {
            run = default;
            if (c.Track == null || band < 0 || band >= RRWConst.kUwMaxChainBands) return false;
            c.Uw.Validate(c);
            var br = c.Uw.Bands[band];
            if (ei < 0 || ei >= br.Done.Length) return UwRunCore(c.Project, c.View, c.Track, ei, band, nodeZone, out run);
            byte bit = nodeZone ? (byte)2 : (byte)1;
            if ((br.Done[ei] & bit) == 0)
            {
                UwRunCore(c.Project, c.View, c.Track, ei, band, nodeZone, out var r);
                if (nodeZone) br.Node[ei] = r; else br.Interior[ei] = r;
                br.Done[ei] |= bit;
            }
            run = nodeZone ? br.Node[ei] : br.Interior[ei];
            return run.Ok;
        }

        // The machine-safe run of band `band` on track edge ei, from the edge's sub-strips (no cache; rrw.check uses it directly).
        // The run that overlaps the band's SafeLo..SafeHi (UpgradeBandView, the Director's range over all edges) the most, clipped to
        // it. nodeZone: dropped lanes do not count (approach zones). False when nothing is machine-safe (or the edge's upgrade
        // state is stale: fails closed).
        internal static bool UwRunCore(ProjectRecord pr, in ProjectView v, TrackData td, int ei, int band, bool nodeZone, out UwRun run)
        {
            run = default;
            var u = v.Upgrade;
            if (pr == null || td == null || ei < 0 || ei >= td.Edges.Count || band < 0 || band >= u.BandCount) return false;
            var bv = u.Band(band);
            if ((bv.Kind == BandKind.Remark && u.Prim(bv.Window) != BandTraffic.Paint) || !(bv.SafeHi > bv.SafeLo)) return false;
            var rt = pr.Upgrade;
            if (rt == null || !SiteRegistry.TryGetEdge(td.Edges[ei].Edge, out var rec) || rec.Upgrade == null) return false;
            var es = rec.Upgrade;
            if (!es.ChainIndexCurrent(rt) || es.SubStripsRevision != rec.GeometryRevision || es.SubStripsTail != es.TailRevision) return false;
            int k = UwEdgeBand(es, band);
            if (k < 0) return false;
            var subs = es.SubStrips[k];
            int n = subs.Count;
            if (n == 0) return false;
            bool rev = es.ChainReversed;
            float split = RoadZoneMath.DirSplitChain(rec.Section, rev);
            // dropped lanes count only while the band's own window still runs as a lane drop (a weakened window stops them at once,
            // before Traffic releases its report), outside the approach zones
            bool drops = !nodeZone && PhasePlan.LaneDrops(u.Prim(bv.Window));
            float bestOv = float.NegativeInfinity;
            int runA = -1;
            float runLo = 0f, runHi = 0f;
            bool runDrop = false;
            for (int j = 0; j <= n; j++)
            {
                bool safe = false, drop = false;
                float lo = 0f, hi = 0f;
                if (j < n)
                {
                    var s = subs[rev ? n - 1 - j : j];
                    lo = rev ? -s.Hi : s.Lo;
                    hi = rev ? -s.Lo : s.Hi;
                    safe = SubStripSafe(rec, u, s, k, rev, split, bv.Kind, drops, out drop);
                }
                if (safe && runA >= 0 && lo <= runHi + 0.05f) { runHi = math.max(runHi, hi); runDrop |= drop; continue; }
                if (runA >= 0)
                {
                    float ov = math.min(runHi, bv.SafeHi) - math.max(runLo, bv.SafeLo);
                    if (ov > bestOv && ov > 0.05f)
                    {
                        bestOv = ov;
                        run.Ok = true;
                        run.Lo = runLo; run.Hi = runHi; run.Drop = runDrop;
                        // clearances at the ends of the run: the neighbour beyond it
                        run.ClrLo = UwClearance(es, subs, rev, runA - 1, runLo - 0.1f);
                        run.ClrHi = UwClearance(es, subs, rev, j, runHi + 0.1f);
                    }
                    runA = -1;
                }
                if (safe) { runA = j; runLo = lo; runHi = hi; runDrop = drop; }
            }
            if (!run.Ok) return false;
            // the Director's range over all edges: a tighter end is a hard limit. This edge is machine-safe beyond it up to the run
            // end, so the box keeps only what is left of the clearance from that end (never less than the end clearance needs).
            if (bv.SafeLo > run.Lo + 1e-3f) { run.ClrLo = math.max(0f, run.ClrLo - (bv.SafeLo - run.Lo)); run.Lo = bv.SafeLo; }
            if (bv.SafeHi < run.Hi - 1e-3f) { run.ClrHi = math.max(0f, run.ClrHi - (run.Hi - bv.SafeHi)); run.Hi = bv.SafeHi; }
            run.Ok = run.Hi - run.Lo > 0.05f;
            return run.Ok;
        }

        // Saved band index of an edge's upgrade state that maps to chain band `band` (-1 = the edge has no part of it).
        internal static int UwEdgeBand(UpgradeEdgeState es, int band)
        {
            for (int i = 0; i < es.BandCount && i < RRWConst.kUwMaxBands; i++) if (es.ChainIndex[i] == band) return i;
            return -1;
        }

        // May a machine use sub-strip s (EDGE frame) of saved band savedBand (moving)? The shared rule (UpgradeZones.SubStripStateOf:
        // the stage's closed and drained groups without parked cars, the lanes Traffic dropped for this band by their lane
        // centres, verge / terrain), plus: dropped lanes only count when `drops` (the band's window runs as a lane drop, outside
        // the approach zones) and Traffic listed the lane centres. drop: it is machine-safe only through the lane drop.
        static bool SubStripSafe(EdgeRecord rec, in UpgradeView u, in SubStrip s, int savedBand, bool rev, float split, BandKind kind,
                                 bool drops, out bool drop)
        {
            drop = false;
            if (kind == BandKind.Remove && s.Kind != SubKind.Terrain) return false;   // remove bands: the removed strip only
            var st = UpgradeZones.SubStripStateOf(rec, u, s, savedBand, false);
            if (st.DropReady && (!drops || !rec.Upgrade.DropCentresWritten)) st.DropReady = false;
            bool ok = PhasePlan.MachineSafe(st, false);
            drop = ok && st.DropReady && !st.GroupClosedReady && s.Kind == SubKind.DriveLane;
            return ok;
        }

        // Clearance a box keeps from a run end: kUwFenceClear when the neighbour beyond it is a sidewalk (the kerb fence), else
        // kUwBandMachineClear. j = the neighbouring sub-strip in chain order (outside the band: the road's cross-section at x).
        static float UwClearance(UpgradeEdgeState es, List<SubStrip> subs, bool rev, int j, float xChain)
        {
            int n = subs.Count;
            SubKind kind = SubKind.Terrain;
            bool found = false;
            if (j >= 0 && j < n) { kind = subs[rev ? n - 1 - j : j].Kind; found = true; }
            else
            {
                float x = rev ? -xChain : xChain;
                var cs = es.CrossSection;
                for (int i = 0; i < cs.Count; i++)
                    if (x >= cs[i].Lo && x <= cs[i].Hi) { kind = cs[i].Kind; found = true; break; }
            }
            return found && kind == SubKind.Sidewalk ? RRWConst.kUwFenceClear : RRWConst.kUwBandMachineClear;
        }

        // The stretch of the chain [s0, s1] where p's band crew works (the longest run of edges that have the band; a crew whose
        // band drops lanes stays between the first and the last blocker of each edge (UwDropInterior), so then the longest edge
        // interior). A machine that worked under the lane drop (Puppet.UwDropStretch) keeps that limit, also as a leaver after the
        // window's primitive weakened: the dropped lane it drives on carries traffic in the approach zones. False = no
        // restriction (not a band crew).
        internal static bool UwStretchOf(PlanContext c, Puppet p, out float s0, out float s1)
        {
            s0 = float.NegativeInfinity; s1 = float.PositiveInfinity;
            int band = UwBandOf(c, p);
            if (band < 0 && p != null && (p.PostSite || p.Outgoing) && p.UwBand >= 0) band = p.UwBand;
            if (band < 0 || band >= RRWConst.kUwMaxChainBands || c.Track == null || !c.Track.Valid) return false;
            c.Uw.Validate(c);
            var br = c.Uw.Bands[band];
            if (!br.StretchDone)
            {
                br.StretchDone = true;
                br.StretchOk = UwStretchCore(c.Project, c.View, c.Track, band, false, out br.S0, out br.S1, out br.StretchDrops);
            }
            if (p != null && p.UwBand == band && !p.PostSite && !p.Outgoing && br.StretchOk && br.StretchDrops) p.UwDropStretch = true;
            if (p != null && p.UwDropStretch && !br.StretchDrops)
            {
                if (!br.LatchDone)
                {
                    br.LatchDone = true;
                    br.LatchOk = UwStretchCore(c.Project, c.View, c.Track, band, true, out br.L0, out br.L1, out _);
                }
                if (br.LatchOk) { s0 = br.L0; s1 = br.L1; return true; }
            }
            if (!br.StretchOk) return false;
            s0 = br.S0; s1 = br.S1;
            return true;
        }

        // forceDrop: the lane-drop limit whatever the band's window runs as now (a machine that worked in dropped lanes); edges that
        // no longer carry the band count too when none does.
        internal static bool UwStretchCore(ProjectRecord pr, in ProjectView v, TrackData td, int band, bool forceDrop, out float s0, out float s1,
                                           out bool drops)
        {
            s0 = s1 = 0f;
            drops = false;
            if (pr == null || pr.Upgrade == null || band < 0 || band >= v.Upgrade.BandCount) return false;
            var u = v.Upgrade;
            var bv = u.Band(band);
            // (the primitive of the band's own window: a leaver of a finished window keeps its band's rule)
            drops = forceDrop || (u.Prim(bv.Window) == BandTraffic.Drop && bv.IsBuild && bv.HasCar) || u.Prim(bv.Window) == BandTraffic.Paint;
            float bestLen = UwStretchPass(td, band, drops, true, ref s0, ref s1);
            if (bestLen <= 0f && forceDrop) bestLen = UwStretchPass(td, band, true, false, ref s0, ref s1);
            if (bestLen <= 0f) return false;
            s0 = math.max(s0, v.Trim0);
            s1 = math.min(s1, v.Trim1 > v.Trim0 ? v.Trim1 : v.U);
            return s1 > s0;
        }

        // Longest stretch of edges (with the band when needBand); under a lane drop the longest edge interior (UwDropInterior).
        static float UwStretchPass(TrackData td, int band, bool drops, bool needBand, ref float s0, ref float s1)
        {
            float bestLen = -1f, segLo = 0f, segHi = 0f;
            bool open = false;
            for (int ei = 0; ei <= td.Edges.Count; ei++)
            {
                bool has = false;
                float a = 0f, b = 0f;
                if (ei < td.Edges.Count)
                {
                    var e = td.Edges[ei];
                    if (SiteRegistry.TryGetEdge(e.Edge, out var rec) && rec.Upgrade != null && (!needBand || UwEdgeBand(rec.Upgrade, band) >= 0))
                    {
                        has = true;
                        if (drops) UwDropInterior(e, out a, out b);
                        else { a = e.Lo; b = e.Hi; }
                        if (b <= a) has = false;
                    }
                }
                if (has && open && !drops && a <= segHi + 0.5f) { segHi = b; continue; }
                if (open && segHi - segLo > bestLen) { bestLen = segHi - segLo; s0 = segLo; s1 = segHi; }
                open = has;
                if (has) { segLo = a; segHi = b; }
            }
            return bestLen;
        }

        // ------------------------------------------------------------ driveway keep-outs (standing boxes)

        // Chain side (SidewalkLeft / SidewalkRight) of a lateral on the edge under u: left of the direction split (centre line).
        static RoadZones UwSideOf(TrackData td, float u, float lat)
        {
            float split = TrackBuilder.SectionAt(td, u, out var sec, out bool rev) ? RoadZoneMath.DirSplitChain(sec, rev) : float.NaN;
            return lat < (float.IsNaN(split) ? 0f : split) ? RoadZones.SidewalkLeft : RoadZones.SidewalkRight;
        }

        // A box of p standing at (u, lat) touches a driveway keep-out window on its side (+ kUwKeepOutMargin along the road).
        internal static bool UwInKeepOut(PlanContext c, Puppet p, float u, float lat) =>
            UwInKeepOut(c.Track, u, lat, p.BoxHalfLen + math.abs(p.BoxOffZ) + MxConst.kUwKeepOutMargin);

        internal static bool UwInKeepOut(TrackData td, float u, float lat, float halfLen)
        {
            if (td == null || !td.Valid) return false;
            var side = UwSideOf(td, u, lat);
            float a = u - halfLen, b = u + halfLen;
            for (int i = 0; i < td.Edges.Count; i++)
            {
                var e = td.Edges[i];
                if (e.Hi < a - 1f || e.Lo > b + 1f) continue;
                if (!SiteRegistry.TryGetEdge(e.Edge, out var rec) || rec.Upgrade == null) continue;
                if (rec.Upgrade.InKeepOut(a, b, side)) return true;
            }
            return false;
        }

        // Moves a standing spot of a band-crew puppet along the road out of the driveway keep-outs (kParkStep steps up to
        // kUwKeepOutSearch, the preferred direction first). True when u changed.
        internal static bool UwShiftOutOfKeepOut(PlanContext c, Puppet p, ref float u, float lat, float dirPref)
        {
            if (!c.Upgrade || UwBandOf(c, p) < 0 || !UwInKeepOut(c, p, u, lat)) return false;
            float d = dirPref >= 0f ? 1f : -1f;
            int n = (int)math.ceil(MxConst.kUwKeepOutSearch / MxConst.kParkStep);
            for (int k = 1; k <= n; k++)
            {
                for (int s = 0; s < 2; s++)
                {
                    float want = u + (s == 0 ? d : -d) * k * MxConst.kParkStep;
                    float uu = ClampU(c, p, want);
                    if (math.abs(uu - want) > 0.5f) continue;
                    if (UwInKeepOut(c, p, uu, lat)) continue;
                    u = uu;
                    MxUpgradeStats.KeepOutShifts++;
                    return true;
                }
            }
            MxUpgradeStats.KeepOutNoSpot++;
            return false;
        }

        // The band of the crew has driveway keep-outs on its side anywhere along the chain.
        internal static bool UwKeepOutsOnSide(PlanContext c, int band)
        {
            if (band < 0 || band >= c.View.Upgrade.BandCount || c.Track == null) return false;
            var side = c.View.Upgrade.Band(band).Side;
            var sides = side == BandSide.Left ? RoadZones.SidewalkLeft : side == BandSide.Right ? RoadZones.SidewalkRight : RoadZones.Sidewalks;
            for (int i = 0; i < c.Track.Edges.Count; i++)
            {
                if (!SiteRegistry.TryGetEdge(c.Track.Edges[i].Edge, out var rec) || rec.Upgrade == null) continue;
                var ko = rec.Upgrade.DrivewayKeepOut;
                for (int k = 0; k < ko.Count; k++) if ((ko[k].Side & sides) != 0) return true;
            }
            return false;
        }

        private static readonly List<DrivewayWindow> s_KeepOuts = new List<DrivewayWindow>(16);
        private static readonly Comparison<DrivewayWindow> s_ByU0 = (x, y) => x.U0.CompareTo(y.U0);

        // The excavator of a band crew never stands inside a driveway keep-out on its side: its dig anchors clamp before a window
        // (hi) until the raw anchor passes the window's middle, then they start beyond it (lo) and the excavator drives through.
        // Returns a key of the clamp state (-1 = none), so a change re-plans the digger.
        internal static int UwDigClamp(PlanContext c, Puppet p, float offset, float lat, ref float lo, ref float hi)
        {
            if (!c.Upgrade || UwBandOf(c, p) < 0 || c.Track == null) return -1;
            var side = UwSideOf(c.Track, c.FrontAt(c.Now) + offset, lat);
            s_KeepOuts.Clear();
            for (int i = 0; i < c.Track.Edges.Count; i++)
            {
                if (!SiteRegistry.TryGetEdge(c.Track.Edges[i].Edge, out var rec) || rec.Upgrade == null) continue;
                var ko = rec.Upgrade.DrivewayKeepOut;
                for (int k = 0; k < ko.Count; k++) if ((ko[k].Side & side) != 0) s_KeepOuts.Add(ko[k]);
            }
            if (s_KeepOuts.Count == 0) return -1;
            s_KeepOuts.Sort(s_ByU0);
            float hl = p.BoxHalfLen + math.abs(p.BoxOffZ) + MxConst.kUwKeepOutMargin;
            float A = c.FrontAt(c.Now) + offset;
            int key = -1;
            for (int i = 0; i < s_KeepOuts.Count; i++)
            {
                var w = s_KeepOuts[i];
                if (A - hl >= w.U1) continue;                                   // behind the box
                if (A + hl <= w.U0 || A < 0.5f * (w.U0 + w.U1))
                {
                    hi = math.min(hi, w.U0 - hl);                               // dig up to the window, never into it
                    key = 2 * i;
                }
                else
                {
                    lo = math.max(lo, w.U1 + hl);                               // the anchor passed the middle: start beyond it
                    key = 2 * i + 1;
                    for (int j = i + 1; j < s_KeepOuts.Count; j++)
                    {
                        var x = s_KeepOuts[j];
                        if (w.U1 + hl >= x.U1) continue;
                        hi = math.min(hi, x.U0 - hl);
                        break;
                    }
                }
                break;
            }
            if (hi < lo) hi = lo;
            return key;
        }

        // ------------------------------------------------------------ front loading (the excavator dumps to the front)

        // Truck cycle of a mode H band crew. The band is one lane for the machines: the trucks wait at their base slots of the crew
        // plan (loading phases: the chain-end slots; Dir -1; without K-turns the waiting point rolls with the work,
        // Choreo.RollingBase), reverse up to their stop with the nose away from the work and drive off forwards, all at the slot's
        // lateral:
        //  * loading with front loading (CrewPlan.FrontLoad): in line in FRONT of the excavator at its lateral (it dumps to the
        //    front), the tail kUwTruckTailGap beyond its scrape; without an excavator (a narrow band) at the front like a hand
        //    crew's truck;
        //  * tipping / feeding: the stops of the crew plan ahead of the front (dump heap, the paver's hopper).
        internal static TruckCfg UwTruckConfig(PlanContext c, Puppet p, MachineRole role, CrewSlot slot)
        {
            var cfg = new TruckCfg { Load = slot.Load };
            var ph = c.View.Phase;
            bool loadPhase = ph == WorksPhase.Excavation || ph == WorksPhase.BreakUp || ph == WorksPhase.Removal;
            cfg.Mode = loadPhase ? 1 : ph == WorksPhase.Paving ? 3 : 2;
            cfg.Dir = -1;
            cfg.Wait = cfg.Mode == 1 ? MxConst.kTruckTripWait : MxConst.kDumpTripWait;
            bool isB = role == MachineRole.TruckB;
            cfg.BaseU = ClampU(c, p, ShiftShared(c, isB ? c.Crew.BaseSlotUB : c.Crew.BaseSlotU));
            bool front = cfg.Mode == 1 && c.Crew.FrontLoad && c.Crew.Excavator.Active;
            cfg.WorkLat = LatOf(c, slot.Lateral, cfg.BaseU, p);
            cfg.BaseLat = cfg.WorkLat;
            UwShiftOutOfKeepOut(c, p, ref cfg.BaseU, cfg.BaseLat, 1f);
            cfg.TurnStation = cfg.BaseU;
            cfg.LoadOffset = front ? UwFrontLoadOffset(c, p) : -1.5f;
            return cfg;
        }

        // Truck centre ahead of the excavator origin (chain u) for front loading: its tail kUwTruckTailGap beyond the far end of the
        // scrape (the arm's far reach x kUwDragShare from the swing axis), never closer than its box clear of the excavator's box.
        internal static float UwFrontLoadOffset(PlanContext c, Puppet truck)
        {
            var e = c.CS != null ? c.CS.Roles[(int)MachineRole.Excavator] : null;
            float exFront = e != null ? e.BoxHalfLen + e.BoxOffZ : 5.1f;
            float tTail = truck.BoxHalfLen - truck.BoxOffZ;
            float clear = exFront + tTail + 2f * MxConst.kOverlapMargin + 0.3f;
            float reach = clear;
            if (e != null && e.Dig != null && e.Dig.Arm != null)
            {
                var arm = e.Dig.Arm;
                DigKeys.SpoilReach(arm, DigMode.Dig, 0f, 0f, 0f, out _, out _, out float rFar, MxConst.kUwDragShare);
                reach = arm.C.z + rFar + MxConst.kUwTruckTailGap + tTail;
            }
            return math.max(clear, reach);
        }
    }

    // Counters of the mode H machine rules (rrw.mx.uw, rrw.mx.check).
    public static class MxUpgradeStats
    {
        public static int Vacates, WindowLeaves, KeepOutShifts, KeepOutNoSpot, DigKeepOutClamps, FrontLoads, FrontDumpsSpoil, NoFit;
        public static int RollerRemovals;   // leaving rollers removed when their drive-off stopped or at kUwRollerLeaveSeconds
        public static string LastVacate = "";

        public static string Summary() =>
            "uw(vacates=" + Vacates + " windowLeaves=" + WindowLeaves + " rollerRemovals=" + RollerRemovals + " keepOutShifts=" + KeepOutShifts + " keepOutNoSpot=" + KeepOutNoSpot +
            " digKeepOutClamps=" + DigKeepOutClamps + " frontLoads=" + FrontLoads + " frontSpoil=" + FrontDumpsSpoil + " noFit=" + NoFit +
            (LastVacate.Length > 0 ? " lastVacate=" + LastVacate : "") + ")";
    }
}
