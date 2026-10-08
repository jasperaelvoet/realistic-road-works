using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using ObjTransform = Game.Objects.Transform;

namespace RealisticRoadWorks.V3.Props
{
    // Upgrade works (mode H): the band devices and the band heaps, laid out from PhasePlan.Props(view) (PropPlan.Devices, BandMask,
    // WaitingMask, BandDivider) and the bands' sub-strips (UpgradeEdgeState.SubStrips / CrossSection, cut by the Director).
    //  - Every strip of a works edge gets a traffic state (StateOf): open, draining, ready (closed and drained, or a dropped lane
    //    whose blockers are registered) or free (verge, median, terrain). In-lane devices stand only on ready strips, so they follow
    //    the in-lane rule by construction; a dropped lane counts only between its blockers on its edge (the range Traffic reports,
    //    else kUwBlockerNodeSetback from both nodes), where no car can be. The states come from the shared sub-strip rules
    //    (UpgradeZones.SubStripClosed / SubStripParkingOff) and follow the APPLIED window, like the plan.
    //  - Divider along the inner edge of the closed part (next to an open lane): cones every kUwDividerSpacing, barriers for lane
    //    drops and from 60 km/h. A closed part whose band ends short of the open lanes (a one-sided build band under Half, the
    //    rest of its closed half outside the band) reaches over the closed, drained lanes beside it up to the first open lane,
    //    so the divider stands where traffic passes. Taper at the approach end where the closed lanes merge into an open lane of
    //    the same direction; closed-end barrier line across the closed part at both ends (at the taper end on the approach side).
    //  - Kerb fence between an open sidewalk and the closed part (or between a closed walk and an open lane); verge fence on the
    //    new road edge of a removed strip. kUwVergeFenceGap gaps at building accesses. Sides the staged kerb fence already covers
    //    (PropPlan.FenceSides) are skipped.
    //  - Cones along the lane edges of bands that carry traffic (waiting build bands, Dressing), centre-line cones while re-marking
    //    under Dressing.
    //  - Heaps per band (PhasePlan.UpgradeBandPropFill) on the outermost machine-safe strip of the band, never on a sidewalk,
    //    outside the driveway keep-outs.
    // The device list is cached per project (key: the diff key + the edge stamps) and applied like the kerb fences; heaps take the
    // front-only diffs like the slot groups. A Dressing window never gets an in-lane device (no strip is closed then).
    public partial class PropSystem
    {
        private const int kBandHeapSub = 16;          // band heaps: sub = kBandHeapSub + chain band (project heaps use sub 0)
        private const int kBandBarrierSub = 32;       // divider barriers: sub = kBandBarrierSub + band * 2 + side
        private const int kCentreConeSub = 32;        // centre-line cones (BandCone)
        private const float kStripProbe = 0.05f;      // neighbour lookup this far beyond a strip boundary

        private static readonly PropGroup[] s_BandHeapGroups = { PropGroup.SpoilHeaps, PropGroup.DumpHeaps, PropGroup.StoneWindrow, PropGroup.Rubble };

        // One strip of an upgrade works edge, CHAIN frame, with its traffic state now.
        private struct HStrip
        {
            public float Lo, Hi;
            public SubKind Kind;
            public StripState State;
            public bool Drop;     // a lane the window drops with blockers (its state follows the edge's blocker report)
            public sbyte Dir;     // drive lane: +1 travels towards +u, -1 towards -u, 0 unknown / not a drive lane
            public float Centre => (Lo + Hi) * 0.5f;
            public float Width => Hi - Lo;
        }

        // The device features of one chain band on one edge (chain frame laterals; NaN = none).
        private sealed class HEdge
        {
            public bool Valid;
            public readonly List<HStrip> Strips = new List<HStrip>(8);
            public readonly float[] DivX = new float[2];        // boundary with the open lane below (0) / above (1) the closed part
            public readonly sbyte[] DivDir = new sbyte[2];      // travel direction of that open lane
            public readonly float[] Kerb = new float[2];        // kerb fence lateral, chain-left (0) / chain-right (1) side
            public readonly YMode[] KerbY = new YMode[2];
            public readonly float[] Verge = new float[2];       // verge fence lateral (remove bands)
            public readonly YMode[] VergeY = new YMode[2];
            public int Cones;                                   // lane-edge cone lines of the band
            public readonly float[] Cone = new float[4];
            public readonly YMode[] ConeY = new YMode[4];
            public int Runs;                                    // closed parts (ready lanes, or a ready closed walk), low to high
            public readonly float[] RunLo = new float[2], RunHi = new float[2];
            public readonly bool[] RunDrop = new bool[2];
            public readonly sbyte[] RunDir = new sbyte[2];
            public readonly int[] TaperSide = new int[2];       // divider side the closed lanes merge into (-1 = no taper)
            public readonly float[] ExtFrom = new float[2];     // closed part extended beyond the band: from this band strip edge
            public readonly float[] ExtTo = new float[2];       // to the open lane below (0) / above (1) (NaN = not extended)

            public void Reset()
            {
                Valid = false;
                Strips.Clear();
                for (int i = 0; i < 2; i++)
                {
                    DivX[i] = Kerb[i] = Verge[i] = float.NaN;
                    DivDir[i] = 0;
                    KerbY[i] = VergeY[i] = YMode.Panel;
                    RunLo[i] = RunHi[i] = float.NaN;
                    RunDrop[i] = false;
                    RunDir[i] = 0;
                    TaperSide[i] = -1;
                    ExtFrom[i] = ExtTo[i] = float.NaN;
                }
                Cones = 0;
                Runs = 0;
            }
        }

        // A stretch of one closed part along the chain (a lane drop: per edge inside the blockers' range; a closed group: the
        // edges it spans, joined).
        private struct HSeg
        {
            public float A, B;          // chain u
            public int Edge0, Edge1;    // first / last chain edge index
            public bool TaperA, TaperB; // taper at the low-u / high-u end
            public float Lt;            // taper length
            public sbyte DirA, DirB;    // travel direction of the closed lanes at both ends (approach side: +1 -> A, -1 -> B)
        }

        private readonly List<HEdge> m_HEdges = new List<HEdge>(8);
        private readonly List<HStrip> m_HCross = new List<HStrip>(16);
        private readonly List<HStrip> m_HTmp = new List<HStrip>(8);
        private readonly List<HSeg>[] m_Segs = { new List<HSeg>(4), new List<HSeg>(4) };
        private readonly Dictionary<uint, List<Want>> m_BandCache = new Dictionary<uint, List<Want>>();
        private float[] m_Line = new float[8];

        // ------------------------------------------------------------------ strips and states

        private static bool IsLane(SubKind k) => k == SubKind.DriveLane || k == SubKind.NewParking || k == SubKind.Bike;
        private static bool Raised(SubKind k) => k == SubKind.Sidewalk || k == SubKind.Verge || k == SubKind.Median;
        private static bool CarriesTraffic(StripState s) => s == StripState.Open || s == StripState.Draining;

        // Y of a prop standing on a strip of this kind: the road surface on lanes, the raised surface on walks / verges / medians,
        // the terrain outside the new road.
        private static YMode YOf(SubKind k) => k == SubKind.Terrain ? YMode.Ground : Raised(k) ? YMode.Sidewalk : YMode.Curve;

        // The edge's upgrade state when its sub-strips belong to the current geometry, tail and chain bands; null otherwise (the
        // edge gets no band device until the Director has re-cut it).
        private static UpgradeEdgeState CurrentState(ProjectRecord p, ChainEdge ce)
        {
            var es = ce.Record.Upgrade;
            var rt = p.Upgrade;
            if (es == null || rt == null || !es.ChainIndexCurrent(rt)) return null;
            if (es.SubStripsRevision != ce.Record.GeometryRevision || es.SubStripsTail != es.TailRevision) return null;
            return es;
        }

        // The whole lane (EDGE frame) a drive-lane or bike sub-strip lies in: sub-strips are clipped to their band, while a lane is
        // dropped or closed as a whole. The drop rules then judge the lane by its own centre, never a clipped piece of an open lane
        // by the centre of the piece. Other kinds come back unchanged.
        private static SubStrip WholeLane(UpgradeEdgeState es, in SubStrip s)
        {
            if (s.Kind != SubKind.DriveLane && s.Kind != SubKind.Bike) return s;
            float c = (s.Lo + s.Hi) * 0.5f;
            var cs = es.CrossSection;
            for (int i = 0; i < cs.Count; i++)
                if (cs[i].Kind == s.Kind && c >= cs[i].Lo - 0.01f && c <= cs[i].Hi + 0.01f)
                    return new SubStrip { Lo = cs[i].Lo, Hi = cs[i].Hi, Kind = s.Kind, Dir = s.Dir };
            return s;
        }

        // Traffic state of an EDGE-frame strip now, from the shared sub-strip rules (UpgradeZones.SubStripClosed /
        // SubStripParkingOff), so Props, Surfaces and Machines agree about every strip, including during the Vacate step of a
        // window start, when the previous window's layout is still applied. While the project does not control lane groups
        // (Closure below Closed) every lane is open.
        //  - A lane of the edge's current drop report (applied or layout window): ready once Traffic registered its blockers,
        //    draining before that.
        //  - A new parking lane Traffic switched off while empty: ready.
        //  - Any other lane or sidewalk: open until its group is closed on this edge (applied zones, closure applied by Traffic),
        //    then draining until the group is drained (WorkZonesReady), then ready.
        private static StripState StateOf(in ProjectView v, ChainEdge ce, UpgradeEdgeState es, in SubStrip s, out bool drop)
        {
            drop = false;
            if (s.Kind == SubKind.Verge || s.Kind == SubKind.Median || s.Kind == SubKind.Terrain) return StripState.Free;
            if (v.Closure != ClosureLevel.Closed) return StripState.Open;
            var u = v.Upgrade;
            var rec = ce.Record;
            int geo = rec.GeometryRevision;
            var lane = WholeLane(es, s);
            if (es.NotLiveYet(lane)) return StripState.Ready;   // deferred: a lane of the new road that does not exist yet
            if ((lane.Kind == SubKind.DriveLane || lane.Kind == SubKind.Bike) && es.DroppedIn(geo, u, lane.Lo, lane.Hi, -1, false))
            {
                drop = true;
                return es.DroppedIn(geo, u, lane.Lo, lane.Hi, -1) ? StripState.Ready : StripState.Draining;
            }
            if (UpgradeZones.SubStripParkingOff(rec, s)) return StripState.Ready;
            if (!UpgradeZones.SubStripClosed(rec, u, lane, -1)) return StripState.Open;
            RoadZones zone = UpgradeZones.OfSubStrip(lane, es.ChainReversed, RoadZoneMath.DirSplitChain(rec.Section, es.ChainReversed)) & RoadZones.AllLanes;
            return (zone & ~v.WorkZonesReady) == 0 ? StripState.Ready : StripState.Draining;
        }

        private static HStrip ToChain(in ProjectView v, ChainEdge ce, UpgradeEdgeState es, in SubStrip s)
        {
            bool rev = ce.DirSign < 0f;
            var h = new HStrip
            {
                Lo = rev ? -s.Hi : s.Lo,
                Hi = rev ? -s.Lo : s.Hi,
                Kind = s.Kind,
                Dir = s.Kind == SubKind.DriveLane ? (sbyte)(s.Dir * (rev ? -1 : 1)) : (sbyte)0,
            };
            h.State = StateOf(v, ce, es, s, out h.Drop);
            return h;
        }

        private static void SortStrips(List<HStrip> list) => list.Sort((a, b) => a.Lo.CompareTo(b.Lo));

        // The whole road of edge ce with states, chain frame, sorted. False when the edge's upgrade data is not current.
        private static bool CrossStrips(ProjectRecord p, in ProjectView v, ChainEdge ce, List<HStrip> output)
        {
            output.Clear();
            var es = CurrentState(p, ce);
            if (es == null) return false;
            for (int i = 0; i < es.CrossSection.Count; i++) output.Add(ToChain(v, ce, es, es.CrossSection[i]));
            SortStrips(output);
            return true;
        }

        // The strips of chain band `band` on edge ce (every saved band of the edge that maps to it), chain frame, sorted.
        private static bool BandStrips(ProjectRecord p, in ProjectView v, ChainEdge ce, int band, List<HStrip> output)
        {
            output.Clear();
            var es = CurrentState(p, ce);
            if (es == null) return false;
            for (int j = 0; j < es.BandCount && j < es.SubStrips.Length; j++)
            {
                if (es.ChainIndex[j] != band) continue;
                var l = es.SubStrips[j];
                for (int i = 0; i < l.Count; i++) output.Add(ToChain(v, ce, es, l[i]));
            }
            SortStrips(output);
            return true;
        }

        private static bool StripAt(List<HStrip> list, float x, out HStrip s)
        {
            for (int i = 0; i < list.Count; i++)
                if (x >= list[i].Lo - 0.001f && x <= list[i].Hi + 0.001f) { s = list[i]; return true; }
            s = default;
            return false;
        }

        // A dropped lane is closed only between its blockers on its edge: the chain-u range Traffic reports with the drop
        // (UpgradeEdgeState.DropSafeRange), clipped to the edge; without a usable report, kUwBlockerNodeSetback from both nodes,
        // where Traffic places them.
        private static void BlockerRange(ChainEdge ce, out float a, out float b)
        {
            a = ce.Lo + RRWConst.kUwBlockerNodeSetback;
            b = ce.Hi - RRWConst.kUwBlockerNodeSetback;
            var es = ce.Record.Upgrade;
            if (es == null || !es.DropSafeRange(out float u0, out float u1)) return;
            float lo = math.max(ce.Lo, u0), hi = math.min(ce.Hi, u1);
            if (hi - lo >= 1f) { a = lo; b = hi; }
        }

        // slack: the check measures u back from a world position.
        private static bool InBlockerRange(ChainEdge ce, float u, float slack = 0f)
        {
            BlockerRange(ce, out float a, out float b);
            return u >= a - slack && u <= b + slack;
        }

        // Centre of the carriageway (chain frame): splits the chain-left from the chain-right fence lines.
        private static float RoadCentre(ChainEdge ce)
        {
            RoadZoneMath.CarriageChain(ce.Record.Section, ce.DirSign < 0f, out float lo, out float hi);
            return (lo + hi) * 0.5f;
        }

        // ------------------------------------------------------------------ band features per edge

        private void EnsureEdges(int n)
        {
            while (m_HEdges.Count < n) m_HEdges.Add(new HEdge());
            if (m_Line.Length < n) m_Line = new float[math.max(n, m_Line.Length * 2)];
        }

        // Features of chain band `band` on edge ce: the boundaries of its strips against their neighbours (band strips, or the
        // cross-section beyond the band) decide dividers, fences and cone lines; contiguous ready strips form the closed parts.
        private void BandEdge(ProjectRecord p, in ProjectView v, ChainEdge ce, int band, HEdge h)
        {
            h.Reset();
            if (!BandStrips(p, v, ce, band, h.Strips) || h.Strips.Count == 0) return;
            if (!CrossStrips(p, v, ce, m_HCross)) return;
            h.Valid = true;
            float centre = RoadCentre(ce);
            var st = h.Strips;
            for (int i = 0; i < st.Count; i++)
            {
                // lower boundary of strip i (against the band strip below it, or the road beyond the band)
                bool adjBelow = i > 0 && st[i].Lo - st[i - 1].Hi < PropLayout.kRoadEdgeTol;
                if (adjBelow) Boundary(h, true, st[i - 1], true, true, st[i], true, (st[i - 1].Hi + st[i].Lo) * 0.5f, centre);
                else
                {
                    bool has = StripAt(m_HCross, st[i].Lo - kStripProbe, out var below);
                    Boundary(h, has, below, false, true, st[i], true, st[i].Lo, centre);
                }
                // upper boundary only where no band strip follows directly (else the next strip's lower boundary covers it)
                bool adjAbove = i + 1 < st.Count && st[i + 1].Lo - st[i].Hi < PropLayout.kRoadEdgeTol;
                if (!adjAbove)
                {
                    bool has = StripAt(m_HCross, st[i].Hi + kStripProbe, out var above);
                    Boundary(h, true, st[i], true, has, above, false, st[i].Hi, centre);
                }
            }
            ExtendToOpenLane(h, 0);
            ExtendToOpenLane(h, 1);
            ClosedParts(h);
        }

        // A band whose ready lanes do not reach an open lane on side sd (0 below, 1 above) while the road beside them is closed and
        // drained up to one (a one-sided build band under Half ends short of the direction split, the rest of its closed half lies
        // outside the band): the divider of that side stands where the closed lanes meet the first lane that carries traffic, and
        // the closed part reaches that far. Dropped lanes never join (they are closed only between their blockers), and nothing
        // is extended while a lane on the way still drains (in-lane devices stand only on ready lanes).
        private void ExtendToOpenLane(HEdge h, int sd)
        {
            if (!float.IsNaN(h.DivX[sd])) return;
            var st = h.Strips;
            int edge = -1;
            for (int i = 0; i < st.Count; i++)
            {
                if (!IsLane(st[i].Kind)) continue;
                if (edge < 0 || sd == 1) edge = i;
                if (sd == 0) break;
            }
            if (edge < 0 || st[edge].State != StripState.Ready || st[edge].Drop) return;
            float start = sd == 1 ? st[edge].Hi : st[edge].Lo;
            float x = start;
            for (int guard = 0; guard < 16; guard++)
            {
                if (!StripAt(m_HCross, sd == 1 ? x + kStripProbe : x - kStripProbe, out var next) || !IsLane(next.Kind)) return;
                if (CarriesTraffic(next.State))
                {
                    if (math.abs(x - start) < PropLayout.kRoadEdgeTol) return;   // the band's own boundary (Boundary decides there)
                    h.DivX[sd] = x;
                    h.DivDir[sd] = next.Dir;
                    h.ExtFrom[sd] = start;
                    h.ExtTo[sd] = x;
                    return;
                }
                if (next.State != StripState.Ready || next.Drop) return;
                float nx = sd == 1 ? next.Hi : next.Lo;
                if (sd == 1 ? nx <= x + 1e-3f : nx >= x - 1e-3f) return;
                x = nx;
            }
        }

        // One boundary at chain lateral x between `low` (below) and `high` (above); the *Band flags say which side is a strip of
        // the band itself (the other one is the road beyond the band).
        private static void Boundary(HEdge h, bool hasLow, in HStrip low, bool lowBand, bool hasHigh, in HStrip high, bool highBand, float x, float centre)
        {
            int side = x < centre ? 0 : 1;
            // divider: a ready lane of the band beside a lane that still carries traffic
            if (hasLow && hasHigh && IsLane(low.Kind) && IsLane(high.Kind))
            {
                if (highBand && high.State == StripState.Ready && CarriesTraffic(low.State) && float.IsNaN(h.DivX[0])) { h.DivX[0] = x; h.DivDir[0] = low.Dir; }
                else if (lowBand && low.State == StripState.Ready && CarriesTraffic(high.State) && float.IsNaN(h.DivX[1])) { h.DivX[1] = x; h.DivDir[1] = high.Dir; }
            }
            // kerb fence: (a) an open sidewalk beside the works part of the band; (b) a closed walk of the band beside a lane that
            // carries traffic. Inset: on the works side; Sidewalk mode: on the walk.
            if (hasLow && hasHigh && float.IsNaN(h.Kerb[side]))
            {
                bool inset = RRWGates.FenceLateral == RealisticRoadWorks.V3.FenceLateral.Inset;
                if (OpenWalk(low) && highBand && WorksForFence(high)) SetKerb(h, side, x, +1f, inset ? high.Kind : low.Kind, inset);
                else if (OpenWalk(high) && lowBand && WorksForFence(low)) SetKerb(h, side, x, -1f, inset ? low.Kind : high.Kind, inset);
                else if (highBand && ClosedWalk(high) && IsLane(low.Kind) && CarriesTraffic(low.State)) SetKerb(h, side, x, +1f, high.Kind, true);
                else if (lowBand && ClosedWalk(low) && IsLane(high.Kind) && CarriesTraffic(high.State)) SetKerb(h, side, x, -1f, low.Kind, true);
            }
            // verge fence: the removed strip (terrain) against the new road, on the road edge
            if (float.IsNaN(h.Verge[side]))
            {
                if (lowBand && low.Kind == SubKind.Terrain && hasHigh && high.Kind != SubKind.Terrain)
                {
                    h.Verge[side] = x + PropLayout.kBandFenceInset;
                    h.VergeY[side] = Raised(high.Kind) ? YMode.PanelSidewalk : YMode.Panel;
                }
                else if (highBand && high.Kind == SubKind.Terrain && hasLow && low.Kind != SubKind.Terrain)
                {
                    h.Verge[side] = x - PropLayout.kBandFenceInset;
                    h.VergeY[side] = Raised(low.Kind) ? YMode.PanelSidewalk : YMode.Panel;
                }
            }
            // lane-edge cones: a lane of the band beside a walk, verge, median, terrain or the end of the road (on that side)
            if (h.Cones < h.Cone.Length)
            {
                if (highBand && IsLane(high.Kind) && (!hasLow || !IsLane(low.Kind)))
                {
                    h.Cone[h.Cones] = x - PropLayout.kBandConeOffset;
                    h.ConeY[h.Cones++] = hasLow ? YOf(low.Kind) : YMode.Ground;
                }
                else if (lowBand && IsLane(low.Kind) && (!hasHigh || !IsLane(high.Kind)))
                {
                    h.Cone[h.Cones] = x + PropLayout.kBandConeOffset;
                    h.ConeY[h.Cones++] = hasHigh ? YOf(high.Kind) : YMode.Ground;
                }
            }
        }

        private static bool OpenWalk(in HStrip s) => s.Kind == SubKind.Sidewalk && s.State == StripState.Open;
        private static bool ClosedWalk(in HStrip s) => s.Kind == SubKind.Sidewalk && (s.State == StripState.Ready || s.State == StripState.Draining);

        // The works part a kerb fence separates from an open sidewalk: a ready strip, a lane whose group is closed and draining (the
        // fence follows the closure at once, like the staged kerb fence; a dropped lane waits for its blockers), a verge or median.
        private static bool WorksForFence(in HStrip s)
        {
            if (s.Kind == SubKind.Terrain || OpenWalk(s)) return false;
            if (s.State == StripState.Ready || s.State == StripState.Free) return true;
            return s.State == StripState.Draining && !s.Drop;
        }

        // worksDir: +1 = the works side lies above x, -1 below; onWorksSide: the fence stands on the works side (else on the other
        // side, the open walk); standKind: the strip it stands on.
        private static void SetKerb(HEdge h, int side, float x, float worksDir, SubKind standKind, bool onWorksSide)
        {
            float d = onWorksSide ? worksDir * PropLayout.kBandFenceInset : -worksDir * RRWConst.kKerbFenceOutset;
            h.Kerb[side] = x + d;
            h.KerbY[side] = IsLane(standKind) ? YMode.Panel : YMode.PanelSidewalk;
        }

        // Closed parts: contiguous ready lanes of the band, or a contiguous ready closed walk (sidewalk works). At most two.
        private static void ClosedParts(HEdge h)
        {
            var st = h.Strips;
            int i = 0;
            while (i < st.Count && h.Runs < 2)
            {
                var s = st[i];
                bool lane = IsLane(s.Kind);
                if (s.State != StripState.Ready || !(lane || s.Kind == SubKind.Sidewalk)) { i++; continue; }
                int r = h.Runs++;
                h.RunLo[r] = s.Lo;
                h.RunHi[r] = s.Hi;
                h.RunDrop[r] = s.Drop;
                h.RunDir[r] = s.Dir;
                int j = i + 1;
                while (j < st.Count && st[j].State == StripState.Ready && IsLane(st[j].Kind) == lane && (lane || st[j].Kind == SubKind.Sidewalk)
                       && st[j].Lo - h.RunHi[r] < PropLayout.kRoadEdgeTol)
                {
                    h.RunHi[r] = st[j].Hi;
                    h.RunDrop[r] |= st[j].Drop;
                    if (h.RunDir[r] == 0) h.RunDir[r] = st[j].Dir;
                    j++;
                }
                // the closed part reaches over the closed lanes beside the band up to the open lane (ExtendToOpenLane)
                if (lane && !float.IsNaN(h.ExtTo[0]) && math.abs(h.RunLo[r] - h.ExtFrom[0]) < PropLayout.kRoadEdgeTol) h.RunLo[r] = h.ExtTo[0];
                if (lane && !float.IsNaN(h.ExtTo[1]) && math.abs(h.RunHi[r] - h.ExtFrom[1]) < PropLayout.kRoadEdgeTol) h.RunHi[r] = h.ExtTo[1];
                // taper: the closed lanes merge into an open lane of the same direction beside the closed part
                for (int sd = 0; sd < 2; sd++)
                {
                    float x = h.DivX[sd];
                    if (float.IsNaN(x) || h.RunDir[r] == 0 || h.DivDir[sd] != h.RunDir[r]) continue;
                    float edge = sd == 0 ? h.RunLo[r] : h.RunHi[r];
                    if (math.abs(edge - x) > PropLayout.kRoadEdgeTol) continue;
                    h.TaperSide[r] = sd;
                    break;
                }
                i = j;
            }
        }

        // Closed part of edge h that a divider boundary x belongs to (-1 = none).
        private static int RunOf(HEdge h, float x)
        {
            for (int r = 0; r < h.Runs; r++)
                if (math.abs(h.RunLo[r] - x) < PropLayout.kRoadEdgeTol || math.abs(h.RunHi[r] - x) < PropLayout.kRoadEdgeTol) return r;
            return -1;
        }

        // ------------------------------------------------------------------ layout

        // Band device layout of this update (cached; re-laid when the diff key or an edge stamp changes) applied slot by slot.
        private void UpgradeBands(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan, bool limited)
        {
            if (!m_BandCache.TryGetValue(p.Id, out var list))
            {
                list = new List<Want>(128);
                m_BandCache[p.Id] = list;
                pp.BandKey = 0;
            }
            uint key = Mix(pp.DiffKey, (uint)pp.Thin);
            for (int i = 0; i < pp.Chain.Count; i++) key = Mix(key, pp.Chain[i].GeoStamp);
            key = Mix(key, Mix((uint)(m_KerbFence.Length > 0 ? m_KerbFence[0].Index : 0), (uint)m_FenceCone.Index));
            if (key == 0) key = 1;
            if (pp.BandKey != key)
            {
                BuildBands(p, pp, v, plan, limited, list);
                pp.BandKey = key;
            }
            for (int i = 0; i < list.Count; i++) Apply(p, pp, list[i]);
        }

        private void BuildBands(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan, bool limited, List<Want> list)
        {
            list.Clear();
            pp.BandDividers = pp.BandTapers = pp.BandEnds = pp.BandFencePanels = pp.BandFenceGaps = pp.BandVergePanels = pp.BandEdgeCones = pp.BandCentreCones = 0;
            var u = v.Upgrade;
            var dev = plan.Devices;
            byte active = PropLayout.UpgradeActiveMask(v, plan);
            // the project-level edge cones stand (same rule as their slot fill: the applied window is Dressing or not decided yet)
            bool dressingCones = PhasePlan.UpgradeDressed(u.AppliedTraffic) && !u.InTeardown;
            byte coneBands = (dev & BandDevices.EdgeCones) != 0 ? (byte)(plan.WaitingMask | (dressingCones ? active : 0)) : (byte)0;
            pp.RoadKmh = RoadSpeedKmh(pp);
            EnsureEdges(pp.Chain.Count);
            for (int b = 0; b < u.BandCount && b < RRWConst.kUwMaxChainBands; b++)
            {
                bool act = (active & (1 << b)) != 0, cones = (coneBands & (1 << b)) != 0;
                if (!act && !cones) continue;
                for (int ci = 0; ci < pp.Chain.Count; ci++) BandEdge(p, v, pp.Chain[ci], b, m_HEdges[ci]);
                if (act)
                {
                    BuildSegments(pp, v, 0);
                    BuildSegments(pp, v, 1);
                    bool ends = (dev & BandDevices.ClosedEnd) != 0;
                    if ((dev & BandDevices.Taper) != 0) Tapers(p, pp, b, list);
                    if (ends) ClosedEnds(p, pp, v, plan, b, list);
                    if ((dev & BandDevices.Divider) != 0) Dividers(p, pp, v, plan, b, limited, list);
                    if ((dev & BandDevices.KerbFence) != 0 && pp.FencesFit)
                        for (int side = 0; side < 2; side++)
                        {
                            RoadZones walk = side == 0 ? RoadZones.SidewalkLeft : RoadZones.SidewalkRight;
                            if (plan.Fence == FenceStyle.Kerbs && (plan.FenceSides & walk) != 0) continue;   // the staged kerb fence stands there
                            BandFence(p, pp, v, b, 0, side, ends, list);
                        }
                    if ((dev & BandDevices.VergeFence) != 0 && pp.FencesFit && u.Band(b).Kind == BandKind.Remove)
                        for (int side = 0; side < 2; side++) BandFence(p, pp, v, b, 1, side, false, list);
                }
                if (cones) EdgeConeLines(p, pp, v, b, dressingCones, list);
            }
            if ((dev & BandDevices.CentreCones) != 0) CentreCones(p, pp, v, list);
        }

        // Stretches of closed part r along the chain: per edge (a lane drop only inside the blockers' range), joined across edges
        // when they touch and overlap laterally. Tapers at the approach ends where the edge there has one.
        private void BuildSegments(ProjectProps pp, in ProjectView v, int r)
        {
            var segs = m_Segs[r];
            segs.Clear();
            bool open = false;
            var cur = default(HSeg);
            for (int ci = 0; ci < pp.Chain.Count; ci++)
            {
                var ce = pp.Chain[ci];
                var h = m_HEdges[ci];
                if (!h.Valid || h.Runs <= r) { if (open) { segs.Add(cur); open = false; } continue; }
                float a = math.max(ce.Lo, v.Trim0), b = math.min(ce.Hi, v.Trim1);
                if (h.RunDrop[r])
                {
                    BlockerRange(ce, out float ba, out float bb);
                    a = math.max(a, ba);
                    b = math.min(b, bb);
                }
                if (b - a < 1f) { if (open) { segs.Add(cur); open = false; } continue; }
                if (open)
                {
                    var prev = m_HEdges[cur.Edge1];
                    bool overlap = h.RunLo[r] < prev.RunHi[r] && h.RunHi[r] > prev.RunLo[r];
                    if (a - cur.B < PropLayout.kBandSegmentJoin && overlap) { cur.B = b; cur.Edge1 = ci; cur.DirB = h.RunDir[r]; continue; }
                    segs.Add(cur);
                }
                cur = new HSeg { A = a, B = b, Edge0 = ci, Edge1 = ci, DirA = h.RunDir[r], DirB = h.RunDir[r] };
                open = true;
            }
            if (open) segs.Add(cur);
            float lt = PropLayout.TaperLength(pp.RoadKmh);
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                s.Lt = math.min(lt, (s.B - s.A) / 3f);
                s.TaperA = s.DirA > 0 && m_HEdges[s.Edge0].TaperSide[r] >= 0 && s.Lt >= RRWConst.kTaperConeSpacing;
                s.TaperB = s.DirB < 0 && m_HEdges[s.Edge1].TaperSide[r] >= 0 && s.Lt >= RRWConst.kTaperConeSpacing;
                segs[i] = s;
            }
        }

        // Diagonal cones from the outer edge of the closed part at the approach end to the divider line, kTaperConeSpacing apart.
        private void Tapers(ProjectRecord p, ProjectProps pp, int b, List<Want> list)
        {
            if (m_TallCone == Entity.Null) return;
            for (int r = 0; r < 2; r++)
            {
                var segs = m_Segs[r];
                for (int si = 0; si < segs.Count; si++)
                {
                    var seg = segs[si];
                    for (int end = 0; end < 2; end++)
                    {
                        if (end == 0 ? !seg.TaperA : !seg.TaperB) continue;
                        var h = m_HEdges[end == 0 ? seg.Edge0 : seg.Edge1];
                        int sd = h.TaperSide[r];
                        float to = sd == 0 ? h.DivX[0] + RRWConst.kDividerInset : h.DivX[1] - RRWConst.kDividerInset;
                        float from = sd == 0 ? h.RunHi[r] - PropLayout.kBandFenceInset : h.RunLo[r] + PropLayout.kBandFenceInset;
                        float u0 = end == 0 ? seg.A : seg.B, inward = end == 0 ? 1f : -1f;
                        int n = math.min(63, (int)math.floor(seg.Lt / RRWConst.kTaperConeSpacing) + 1);
                        for (int i = 0; i < n; i++)
                        {
                            float t = n > 1 ? i / (float)(n - 1) : 0f;
                            list.Add(new Want
                            {
                                Key = PropKeys.Make(PropKind.BandTaper, b * 4 + r * 2 + end, si * 64 + i),
                                Kind = PropKind.BandTaper,
                                U = u0 + inward * seg.Lt * t,
                                Lateral = math.lerp(from, to, t),
                                LatChain = true,
                                Y = YMode.Curve,
                                Rot = RotMode.Random,
                                Fill = 255,
                                Prefab = m_TallCone,
                                Group = DerivedGroup.PropStatic,
                            });
                            pp.BandTapers++;
                        }
                    }
                }
            }
        }

        // Barrier line across each closed part at both ends of every stretch: at the taper end on the approach side, else just
        // inside the stretch end. Picked up from the outer end first (PropPlan.StartBarrierKeep / EndBarrierKeep). The line facing
        // the approaching traffic gets chase beacons, the other one beacon on its inner end.
        private void ClosedEnds(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan, int b, List<Want> list)
        {
            if (m_Safety == null || m_Safety.Length == 0) return;
            bool beacons = m_BeaconsOn && m_Beacon != Entity.Null;
            float pitch = m_SafetyPitch;
            for (int r = 0; r < 2; r++)
            {
                var segs = m_Segs[r];
                for (int si = 0; si < segs.Count; si++)
                {
                    var seg = segs[si];
                    for (int end = 0; end < 2; end++)
                    {
                        bool start = end == 0;
                        float keep = start ? plan.StartBarrierKeep : plan.EndBarrierKeep;
                        if (keep <= 0f) continue;
                        bool chainEnd = start ? seg.A <= v.Trim0 + 0.5f : seg.B >= v.Trim1 - 0.5f;
                        if (chainEnd && (start ? p.StartShared : p.EndShared)) continue;   // the neighbouring project's line stands there
                        bool junction = chainEnd && (start ? p.StartIsJunction : p.EndIsJunction);
                        bool taper = start ? seg.TaperA : seg.TaperB;
                        float inset = taper ? seg.Lt : junction ? PropLayout.kJunctionInset : PropLayout.kOtherEndInset;
                        float uLine = start ? seg.A + inset : seg.B - inset;
                        var h = m_HEdges[start ? seg.Edge0 : seg.Edge1];
                        float lo = h.RunLo[r] + PropLayout.kBandEndMargin, hi = h.RunHi[r] - PropLayout.kBandEndMargin;
                        if (hi - lo < 0.2f) { float c = (h.RunLo[r] + h.RunHi[r]) * 0.5f; lo = c - 0.1f; hi = c + 0.1f; }
                        // inner end: next to the open lane (divider side); else towards the road centre
                        bool innerHigh = (!float.IsNaN(h.DivX[1]) && math.abs(h.DivX[1] - h.RunHi[r]) < PropLayout.kRoadEdgeTol)
                                         || (float.IsNaN(h.DivX[0]) && math.abs(h.RunHi[r]) < math.abs(h.RunLo[r]));
                        int n = math.max(1, (int)math.round((hi - lo) / pitch));
                        int nKeep = (int)math.round(keep * n);
                        bool chase = beacons && (start ? seg.DirA > 0 : seg.DirB < 0);   // traffic arrives at this line
                        float step = (hi - lo) / n;
                        for (int i = 0; i < n && i < nKeep && i < 64; i++)   // i = 0 at the inner end: the outer barriers go first
                        {
                            float lat = innerHigh ? hi - step * (i + 0.5f) : lo + step * (i + 0.5f);
                            int j = n - 1 - i;   // index from the outer end
                            var w = new Want
                            {
                                Key = PropKeys.Make(PropKind.BandEnd, b * 4 + r * 2 + end, si * 64 + i),
                                Kind = PropKind.BandEnd,
                                U = uLine,
                                Lateral = lat,
                                LatChain = true,
                                Y = StripAt(h.Strips, lat, out var sAt) ? YOf(sAt.Kind) : YMode.Curve,
                                Rot = RotMode.Across,
                                JitterDeg = 2f,
                                Fill = 255,
                                Group = DerivedGroup.PropStatic,
                            };
                            bool isBeacon = chase ? j % 2 == 0 : beacons && i == 0;
                            if (isBeacon)
                            {
                                w.Prefab = m_Beacon;
                                w.Seed = chase ? BeaconPhase.Chase(j / 2) : BeaconPhase.Random(p.Seed, w.Key);
                            }
                            else w.Prefab = Pick(m_Safety, i);
                            list.Add(w);
                            pp.BandEnds++;
                        }
                    }
                }
            }
        }

        // Divider along the inner edge of each closed part, on a fixed chain grid (stable keys while the stretches change):
        // cones every kUwDividerSpacing kDividerInset inside the closed part, or a barrier line kDividerBarrierInset inside it
        // (lane drops, roads from 60 km/h: concrete barriers), every kDividerBeaconEvery-th a beacon. From the taper end (or
        // kDividerEndGap) to kDividerEndGap before the stretch end.
        private void Dividers(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan, int b, bool far, List<Want> list)
        {
            bool barriers = PropLayout.BandDividerBarriers(plan.BandDivider, pp.RoadKmh);
            bool concrete = pp.RoadKmh >= PropLayout.kDividerBarrierKmh;
            Entity[] fam = concrete ? m_Concrete : m_Safety;
            float spacing = barriers ? (concrete ? m_ConcretePitch : m_SafetyPitch) : RRWConst.kUwDividerSpacing;
            if (barriers && (fam == null || fam.Length == 0)) return;
            if (!barriers && m_TallCone == Entity.Null) return;
            float inset = barriers ? PropLayout.kDividerBarrierInset : RRWConst.kDividerInset;
            int factor = barriers ? 1 : far ? 2 : PropLayout.ThinFactor(PropKind.BandDivider, pp.Thin);
            int n = math.min(0xFFFF, (int)math.floor(v.TrimmedLength / spacing) + 1);
            bool beacons = barriers && m_BeaconsOn && m_Beacon != Entity.Null;
            for (int k = 0; k < n; k++)
            {
                if (factor > 1 && k % factor != 0) continue;
                float uu = v.Trim0 + spacing * (k + 0.5f);
                var ce = PropLayout.Locate(pp.Chain, uu, out float _, out float gap);
                if (ce == null || gap > PropLayout.kCoverTolerance) continue;
                var h = m_HEdges[ce.Index];
                if (!h.Valid) continue;
                for (int sd = 0; sd < 2; sd++)
                {
                    float x = h.DivX[sd];
                    if (float.IsNaN(x)) continue;
                    int r = RunOf(h, x);
                    if (r < 0 || !InDividerRange(r, uu)) continue;
                    bool beacon = beacons && k % PropLayout.kDividerBeaconEvery == 0;
                    list.Add(new Want
                    {
                        Key = PropKeys.Make(PropKind.BandDivider, (barriers ? kBandBarrierSub : 0) + b * 2 + sd, k),
                        Kind = PropKind.BandDivider,
                        U = uu,
                        Lateral = sd == 0 ? x + inset : x - inset,
                        LatChain = true,
                        Y = YMode.Curve,
                        Rot = barriers ? RotMode.Tangent : RotMode.Random,
                        JitterDeg = barriers ? 1f : 0f,
                        Fill = 255,
                        Prefab = !barriers ? m_TallCone : beacon ? m_Beacon : Pick(fam, k),
                        Seed = beacon ? BeaconPhase.Chase(k / PropLayout.kDividerBeaconEvery) : (ushort)0,
                        Group = DerivedGroup.PropStatic,
                    });
                    pp.BandDividers++;
                }
            }
        }

        private bool InDividerRange(int r, float uu)
        {
            var segs = m_Segs[r];
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                if (uu < s.A || uu > s.B) continue;
                float lo = s.A + (s.TaperA ? s.Lt + PropLayout.kOtherEndInset : RRWConst.kDividerEndGap);
                float hi = s.B - (s.TaperB ? s.Lt + PropLayout.kOtherEndInset : RRWConst.kDividerEndGap);
                return uu >= lo && uu <= hi;
            }
            return false;
        }

        // One band fence line (kind 0 = kerb fence, 1 = verge fence) on a chain side: chord-placed panels like the staged kerb
        // fence, over the edges that have the line; breaks where the line jumps between edges, at building accesses
        // (kUwVergeFenceGap) and around the band's closed-end lines.
        private void BandFence(ProjectRecord p, ProjectProps pp, in ProjectView v, int b, int kind, int side, bool ends, List<Want> list)
        {
            Entity[] main = m_FenceFallback ? m_Safety : m_KerbFence;
            Entity[] shortF = m_FenceFallback ? new Entity[0] : m_KerbFenceShort;
            float L = m_FenceFallback ? m_SafetyPitch : m_KerbFencePitch;
            float Ls = m_KerbShortLen;
            bool haveShort = shortF.Length > 0 && Ls >= 0.5f;
            if (main == null || main.Length == 0 || L < 0.5f) return;
            int count = pp.Chain.Count;
            bool any = false;
            YMode ym = YMode.Panel;
            for (int ci = 0; ci < count; ci++)
            {
                var h = m_HEdges[ci];
                float x = !h.Valid ? float.NaN : kind == 0 ? h.Kerb[side] : h.Verge[side];
                m_Line[ci] = x;
                if (!float.IsNaN(x) && !any) { any = true; ym = kind == 0 ? h.KerbY[side] : h.VergeY[side]; }
            }
            if (!any) return;
            var line = m_Line;
            m_Excl.Clear();
            for (int ci = 0; ci < count; ci++)
            {
                var ce = pp.Chain[ci];
                if (float.IsNaN(line[ci])) { m_Excl.Add(new float2(ce.Lo, ce.Hi)); continue; }
                if (ci > 0 && !float.IsNaN(line[ci - 1]) && math.abs(line[ci] - line[ci - 1]) > PropLayout.kBandLineJump)
                    m_Excl.Add(new float2(ce.Lo - PropLayout.kBandJumpGap, ce.Lo + PropLayout.kBandJumpGap));
            }
            int gaps = AccessGaps(pp, side);
            if (ends)
                for (int r = 0; r < 2; r++)
                {
                    var segs = m_Segs[r];
                    for (int si = 0; si < segs.Count; si++)
                    {
                        var s = segs[si];
                        float ua = s.A + (s.TaperA ? s.Lt : PropLayout.kOtherEndInset), ub = s.B - (s.TaperB ? s.Lt : PropLayout.kOtherEndInset);
                        m_Excl.Add(new float2(ua - PropLayout.kFenceLineClear, ua + PropLayout.kFenceLineClear));
                        m_Excl.Add(new float2(ub - PropLayout.kFenceLineClear, ub + PropLayout.kFenceLineClear));
                    }
                }
            BuildRuns(v.Trim0 + RRWConst.kFenceEndGap, v.Trim1 - RRWConst.kFenceEndGap);
            // the panel counters belong to the staged kerb fence: lay the band line on its own counters
            int panels = pp.FencePanels, shorts = pp.FenceShorts, cones = pp.FenceCones;
            float delta = pp.FenceDelta;
            int sub = b * 4 + kind * 2 + side;
            for (int ri = 0; ri < m_Runs.Count && ri < 256; ri++)
                LayRun(p, pp, list, side, ri, m_Runs[ri].x, m_Runs[ri].y, main, shortF, L, Ls, haveShort, ym, line, PropKind.BandFence, sub, PropKind.BandFenceCone, sub);
            int laid = pp.FencePanels - panels;
            if (kind == 0) pp.BandFencePanels += laid; else pp.BandVergePanels += laid;
            if (m_Runs.Count > 0) pp.BandFenceGaps += gaps;
            pp.FencePanels = panels;
            pp.FenceShorts = shorts;
            pp.FenceCones = cones;
            pp.FenceDelta = delta;
        }

        // kUwVergeFenceGap gaps around every building access on a chain side: the access points of the chain's buildings and the
        // Director's driveway keep-outs. Returns how many were added.
        private int AccessGaps(ProjectProps pp, int side)
        {
            int n = 0;
            float half = RRWConst.kUwVergeFenceGap * 0.5f;
            for (int i = 0; i < pp.Access.Count; i++)
            {
                if (pp.Access[i].Side != side) continue;
                m_Excl.Add(new float2(pp.Access[i].U - half, pp.Access[i].U + half));
                n++;
            }
            RoadZones walk = side == 0 ? RoadZones.SidewalkLeft : RoadZones.SidewalkRight;
            for (int ci = 0; ci < pp.Chain.Count; ci++)
            {
                var es = pp.Chain[ci].Record.Upgrade;
                if (es == null || es.DrivewayRevision != pp.Chain[ci].Record.GeometryRevision) continue;
                for (int k = 0; k < es.DrivewayKeepOut.Count; k++)
                {
                    var w = es.DrivewayKeepOut[k];
                    if ((w.Side & walk) == 0) continue;
                    float c = (w.U0 + w.U1) * 0.5f;
                    m_Excl.Add(new float2(c - half, c + half));
                    if (pp.Access.Count == 0) n++;
                }
            }
            return n;
        }

        // Cones along the lane edges of a band that carries traffic (a waiting build band, or the window runs as Dressing), every
        // kMinimalConeSpacing; where the project-level edge cones stand (Dressing, the carriageway edges) the band adds none.
        private void EdgeConeLines(ProjectRecord p, ProjectProps pp, in ProjectView v, int b, bool dressingCones, List<Want> list)
        {
            if (m_Cones == null || m_Cones.Length == 0) return;
            float sp = RRWConst.kMinimalConeSpacing;
            int factor = PropLayout.ThinFactor(PropKind.BandCone, pp.Thin);
            int n = math.min(0xFFFF, (int)math.floor(math.max(0f, v.TrimmedLength - 2f) / sp) + 1);
            for (int k = 0; k < n; k++)
            {
                if (factor > 1 && k % factor != 0) continue;
                float uu = v.Trim0 + 1f + sp * k;
                if (uu - v.Trim0 < PropLayout.kEndClearance || v.Trim1 - uu < PropLayout.kEndClearance) continue;
                var ce = PropLayout.Locate(pp.Chain, uu, out float _, out float gap);
                if (ce == null || gap > PropLayout.kCoverTolerance) continue;
                var h = m_HEdges[ce.Index];
                if (!h.Valid) continue;
                RoadZoneMath.CarriageChain(ce.Record.Section, ce.DirSign < 0f, out float clo, out float chi);
                for (int c = 0; c < h.Cones; c++)
                {
                    float lat = h.Cone[c];
                    if (dressingCones && (math.abs(lat - (clo - PropLayout.kEdgeConeOffset)) < 0.6f || math.abs(lat - (chi + PropLayout.kEdgeConeOffset)) < 0.6f)) continue;
                    int key = PropKeys.Make(PropKind.BandCone, b * 4 + c, k);
                    list.Add(new Want
                    {
                        Key = key,
                        Kind = PropKind.BandCone,
                        U = uu,
                        Lateral = lat,
                        LatChain = true,
                        Y = h.ConeY[c],
                        Rot = RotMode.Random,
                        Fill = 255,
                        Prefab = m_Cones[PropLayout.Pick(p.Seed, key, m_Cones.Length)],
                        Group = DerivedGroup.PropStatic,
                    });
                    pp.BandEdgeCones++;
                }
            }
        }

        // Re-marking under Dressing: tall cones on the direction split, every kDividerFarSpacing (edges without a split: none).
        private void CentreCones(ProjectRecord p, ProjectProps pp, in ProjectView v, List<Want> list)
        {
            if (m_TallCone == Entity.Null) return;
            float sp = RRWConst.kDividerFarSpacing;
            int factor = PropLayout.ThinFactor(PropKind.BandCone, pp.Thin);
            float len = v.TrimmedLength - 2f * RRWConst.kDividerEndGap;
            int n = len < 0f ? 0 : math.min(0xFFFF, (int)math.floor(len / sp) + 1);
            for (int k = 0; k < n; k++)
            {
                if (factor > 1 && k % factor != 0) continue;
                float uu = v.Trim0 + RRWConst.kDividerEndGap + sp * k;
                var ce = PropLayout.Locate(pp.Chain, uu, out float _, out float gap);
                if (ce == null || gap > PropLayout.kCoverTolerance) continue;
                float split = RoadZoneMath.DirSplitChain(ce.Record.Section, ce.DirSign < 0f);
                if (float.IsNaN(split)) continue;
                list.Add(new Want
                {
                    Key = PropKeys.Make(PropKind.BandCone, kCentreConeSub, k),
                    Kind = PropKind.BandCone,
                    U = uu,
                    Lateral = split,
                    LatChain = true,
                    Y = YMode.Curve,
                    Rot = RotMode.Random,
                    Fill = 255,
                    Prefab = m_TallCone,
                    Group = DerivedGroup.PropStatic,
                });
                pp.BandCentreCones++;
            }
        }

        // ------------------------------------------------------------------ heaps

        // The heap caches fit the current layout (a front-only diff needs them; else the caller runs the full diff).
        private bool BandHeapCacheFits(ProjectProps pp, in ProjectView v)
        {
            if (!pp.BandFillValid) return false;
            int bands = v.Upgrade.BandCount;
            for (int b = 0; b < RRWConst.kUwMaxChainBands; b++)
                for (int gi = 0; gi < s_BandHeapGroups.Length; gi++)
                {
                    int n = b < bands ? PhasePlan.SlotCount(s_BandHeapGroups[gi], v) : 0;
                    var c = pp.BandFillCache[b * 4 + gi];
                    if ((c == null ? 0 : c.Length) != n) return false;
                }
            return true;
        }

        // Band heaps. full: every heap with fill >= 1 is applied and the cache rebuilt (the caller deletes what was not seen);
        // front-only: only heaps whose fill changed are touched. Far / budget-limited projects show none.
        private void UpgradeHeaps(ProjectRecord p, ProjectProps pp, in ProjectView v, bool limited, bool full)
        {
            var u = v.Upgrade;
            if (full) pp.BandHeaps = 0;
            for (int b = 0; b < RRWConst.kUwMaxChainBands; b++)
            {
                bool inWindow = b < u.BandCount && u.Band(b).Window == u.Window;
                for (int gi = 0; gi < s_BandHeapGroups.Length; gi++)
                {
                    var g = s_BandHeapGroups[gi];
                    var kind = (PropKind)(int)g;
                    int n = b < u.BandCount ? PhasePlan.SlotCount(g, v) : 0;
                    int ci = b * 4 + gi;
                    var cache = pp.BandFillCache[ci];
                    if (full)
                    {
                        if (cache == null || cache.Length != n) pp.BandFillCache[ci] = cache = new int[n];
                        if (limited || !inWindow) { for (int k = 0; k < n; k++) cache[k] = -3; continue; }
                    }
                    else if (limited || !inWindow || cache == null) continue;
                    int factor = PropLayout.ThinFactor(kind, pp.Thin);
                    for (int k = 0; k < n; k++)
                    {
                        if (factor > 1 && k % factor != 0) { if (full) cache[k] = -2; continue; }
                        int fill = PhasePlan.UpgradeBandPropFill(g, k, v, b);
                        PxPerf.Count(PxC.FillEvals);
                        if (fill < 1) fill = -1;
                        if (full)
                        {
                            cache[k] = fill;
                            if (fill >= 1 && WantBandHeap(p, pp, v, b, kind, k, fill)) pp.BandHeaps++;
                            continue;
                        }
                        if (cache[k] == fill) continue;
                        cache[k] = fill;
                        pp.FrontApplied++;
                        int key = PropKeys.Make(kind, kBandHeapSub + b, k);
                        if (fill < 1 || !WantBandHeap(p, pp, v, b, kind, k, fill)) RemoveKey(pp, key);
                    }
                }
            }
            if (full) pp.BandFillValid = true;
        }

        // One band heap at slot k of its group: on the outermost strip of the band that is ready or free and not a sidewalk (the
        // outer side = away from the road centre; a removed strip: its terrain part), against that strip's outer edge, outside the
        // driveway keep-outs; a dropped lane only inside the blockers' range. False when no strip qualifies (no heap there now).
        private bool WantBandHeap(ProjectRecord p, ProjectProps pp, in ProjectView v, int band, PropKind kind, int k, int fill)
        {
            var g = (PropGroup)(int)kind;
            float uu = PhasePlan.SlotU(g, k, v);
            if (!HeapSpot(p, pp, v, band, uu, out var ce, out float lat, out SubKind on)) return false;
            var bv = v.Upgrade.Band(band);
            var w = new Want
            {
                Key = PropKeys.Make(kind, kBandHeapSub + band, k),
                Kind = kind,
                U = uu,
                Lateral = lat,
                LatChain = true,
                Y = YOf(on),
                Rot = RotMode.Tangent,
                Fill = fill,
                DustSlot = IsDustSlot(k),
                Group = DerivedGroup.PropWork,
            };
            switch (kind)
            {
                case PropKind.SpoilHeap:
                    w.Prefab = m_Ore; w.DustPrefab = m_DustOre; w.JitterDeg = 15f;
                    break;
                case PropKind.DumpHeap:
                {
                    bool ore = bv.Kind == BandKind.Remove;   // topsoil back on a removed strip, base course on a built one
                    w.Prefab = ore ? m_Ore : m_Stone; w.DustPrefab = ore ? m_DustOre : m_DustStone;
                    break;
                }
                case PropKind.Windrow:
                    w.Prefab = m_Stone; w.DustPrefab = m_DustStone; w.JitterDeg = 10f;
                    break;
                default:
                    w.Prefab = m_Rubble.Length > 0 ? m_Rubble[PropLayout.Pick(p.Seed, w.Key, m_Rubble.Length)] : Entity.Null;
                    w.JitterDeg = 5f;
                    w.DustSlot = false;
                    break;
            }
            if (w.Prefab == Entity.Null) return false;
            Apply(p, pp, w);
            return pp.Slots.TryGetValue(w.Key, out var sl) && sl.Seen == pp.Pass;
        }

        private bool HeapSpot(ProjectRecord p, ProjectProps pp, in ProjectView v, int band, float uu, out ChainEdge ce, out float lat, out SubKind on)
        {
            lat = 0f;
            on = SubKind.Verge;
            ce = PropLayout.Locate(pp.Chain, uu, out float _, out float gap);
            if (ce == null || gap > PropLayout.kCoverTolerance) return false;
            if (!BandStrips(p, v, ce, band, m_HTmp) || m_HTmp.Count == 0) return false;
            float roadC = RoadCentre(ce);
            var bv = v.Upgrade.Band(band);
            float outer = bv.Centre >= roadC ? 1f : -1f;
            bool removed = bv.Kind == BandKind.Remove;   // broken asphalt, rubble and topsoil stay on the removed strip itself
            int best = -1;
            float bestEdge = float.MinValue;
            for (int i = 0; i < m_HTmp.Count; i++)
            {
                var s = m_HTmp[i];
                if (s.Kind == SubKind.Sidewalk || s.Width < PropLayout.kBandHeapMinWidth) continue;
                if (removed && s.Kind != SubKind.Terrain) continue;
                if (s.State != StripState.Ready && s.State != StripState.Free) continue;
                if (s.Drop && !InBlockerRange(ce, uu)) continue;
                float e = outer > 0f ? s.Hi : -s.Lo;
                if (e > bestEdge) { bestEdge = e; best = i; }
            }
            if (best < 0) return false;
            var st = m_HTmp[best];
            float half = math.min(PropLayout.kBandHeapHalf, st.Width * 0.5f);
            lat = outer > 0f ? st.Hi - half : st.Lo + half;
            on = st.Kind;
            return !InKeepOut(pp, ce, uu - PropLayout.kBandHeapHalf, uu + PropLayout.kBandHeapHalf, lat < roadC ? 0 : 1);
        }

        // A chain-u range on a chain side overlaps a building access keep-out (the Director's windows, or the chain's own access
        // points +- kUwDrivewayKeepOut).
        private static bool InKeepOut(ProjectProps pp, ChainEdge ce, float u0, float u1, int side)
        {
            var es = ce.Record.Upgrade;
            RoadZones walk = side == 0 ? RoadZones.SidewalkLeft : RoadZones.SidewalkRight;
            if (es != null && es.DrivewayRevision == ce.Record.GeometryRevision && es.InKeepOut(u0, u1, walk)) return true;
            for (int i = 0; i < pp.Access.Count; i++)
            {
                var a = pp.Access[i];
                if (a.Side != side) continue;
                if (u1 >= a.U - RRWConst.kUwDrivewayKeepOut && u0 <= a.U + RRWConst.kUwDrivewayKeepOut) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ crew props

        // Crew props of an upgrade project: in a row along the road (long side along it) on a free strip of the lead band (verge,
        // median, terrain) at least kBandCrewMinWidth wide, outside the keep-outs. False: the mode D place on the sidewalk is used.
        private bool UpgradeCrewSpot(ProjectRecord p, ProjectProps pp, in ProjectView v, int k, out float uu, out float lat, out YMode y)
        {
            uu = v.Trim0 + 14f + PropLayout.kBandCrewSpacing * k;
            lat = 0f;
            y = YMode.Curve;
            var u = v.Upgrade;
            int lead = u.LeadBand(u.LayoutWindow);
            if (lead < 0) return false;
            var ce = PropLayout.Locate(pp.Chain, uu, out float _, out float gap);
            if (ce == null || gap > PropLayout.kCoverTolerance) return false;
            if (!BandStrips(p, v, ce, lead, m_HTmp)) return false;
            float roadC = RoadCentre(ce);
            int best = -1;
            for (int i = 0; i < m_HTmp.Count; i++)
            {
                var s = m_HTmp[i];
                if (s.State != StripState.Free || s.Width < PropLayout.kBandCrewMinWidth) continue;
                if (best < 0 || s.Width > m_HTmp[best].Width) best = i;
            }
            if (best < 0) return false;
            lat = m_HTmp[best].Centre;
            y = YOf(m_HTmp[best].Kind);
            return !InKeepOut(pp, ce, uu - 2.5f, uu + 2.5f, lat < roadC ? 0 : 1);
        }

        // ------------------------------------------------------------------ keys and speed

        // Structural inputs of the band layout (part of the diff key): runtime revision, window, the applied window and primitive,
        // device plan, closure, the applied, closed and ready groups, and per edge the sub-strip / tail revisions, the closure
        // Traffic applied, the blocker report (its window, lanes and range), switched-off parking and keep-outs.
        private static uint UpgradeKeyOf(ProjectRecord p, in ProjectView v, in PropPlan plan, ProjectProps pp)
        {
            var u = v.Upgrade;
            var rt = p.Upgrade;
            uint h = Mix((uint)(rt != null ? rt.Revision : 0), (uint)(byte)u.Window | ((uint)u.AppliedTraffic << 8) | ((uint)plan.Devices << 16));
            h = Mix(h, (uint)plan.BandMask | ((uint)plan.WaitingMask << 8) | ((uint)plan.BandDivider << 16) | (u.AllAtOnce ? 1u << 20 : 0u)
                       | (u.InTeardown ? 1u << 21 : 0u) | (u.InSetup ? 1u << 22 : 0u) | ((uint)(u.AppliedWindow & 0xFF) << 23));
            h = Mix(h, (uint)u.AppliedZones | ((uint)v.Closure << 16));
            h = Mix(h, (uint)v.WorkZonesReady | ((uint)v.SoftZones << 16));
            for (int i = 0; i < pp.Chain.Count; i++)
            {
                var ce = pp.Chain[i];
                var es = ce.Record.Upgrade;
                if (es == null) { h = Mix(h, 0x51EDu); continue; }
                int geo = ce.Record.GeometryRevision;
                h = Mix(h, Mix((uint)es.TailRevision, (uint)es.SubStripsRevision ^ ((uint)es.SubStripsTail << 12)));
                int dw = es.DropWindowFor(geo, u);
                h = Mix(h, (es.ChainIndexCurrent(rt) ? 1u : 0u) | (es.BlockersRegisteredFor(geo, u) ? 2u : 0u)
                           | (dw >= 0 ? 4u : 0u) | ((uint)es.ParkingOffCentres.Count << 4) | ((uint)es.DrivewayKeepOut.Count << 12)
                           | ((uint)(dw + 2) << 20) | ((uint)es.DropLanes.Count << 26) | (es.DropCentresWritten ? 1u << 31 : 0u));
                h = Mix(h, Mix((uint)es.ParkingOffRevision, (uint)es.DrivewayRevision));
                h = Mix(h, (uint)ce.Record.ClosureApplied | ((uint)ce.Record.OpenLanesApplied << 8));
                if (es.DropSafeRange(out float su0, out float su1)) h = Mix(h, Mix((uint)(int)math.round(su0 * 4f), (uint)(int)math.round(su1 * 4f)));
                h = Mix(h, (uint)geo);
            }
            return h;
        }

        // Front component of the diff key: each band front of the layout window in 0.25 m steps and the window fraction (heaps).
        private static uint UpgradeFrontKey(in ProjectView v)
        {
            var u = v.Upgrade;
            uint h = (uint)math.floor(u.Gw * 500f);
            if (u.InTeardown) return h;
            for (int i = 0; i < u.BandCount; i++)
                if (u.Band(i).Window == u.Window) h = Mix(h, (uint)math.floor(PhasePlan.UpgradeBandFront(v, i) * 4f));
            return h;
        }

        // Posted speed of the chain: the highest default speed of its car lanes (km/h; 0 = unknown). Read once per edge geometry.
        private float RoadSpeedKmh(ProjectProps pp)
        {
            float best = 0f;
            for (int i = 0; i < pp.Chain.Count; i++) best = math.max(best, EdgeSpeedKmh(pp.Chain[i]));
            return best;
        }

        private float EdgeSpeedKmh(ChainEdge ce)
        {
            var st = ce.Record.GetOrCreate<PropEdgeState>(ModuleSlot.Props);
            if (st.SpeedRevision == ce.Record.GeometryRevision) return st.SpeedKmh;
            st.SpeedRevision = ce.Record.GeometryRevision;
            st.SpeedKmh = 0f;
            var em = EntityManager;
            if (!em.Exists(ce.Edge) || !em.HasBuffer<Game.Net.SubLane>(ce.Edge)) return 0f;
            var buf = em.GetBuffer<Game.Net.SubLane>(ce.Edge, true);
            float ms = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                Entity l = buf[i].m_SubLane;
                if (!em.HasComponent<Game.Net.CarLane>(l)) continue;
                ms = math.max(ms, em.GetComponentData<Game.Net.CarLane>(l).m_DefaultSpeedLimit);
            }
            st.SpeedKmh = float.IsNaN(ms) ? 0f : ms * 3.6f;
            return st.SpeedKmh;
        }

        // ------------------------------------------------------------------ summary (log + dev)

        private void UpgradeSummary(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan)
        {
            var u = v.Upgrade;
            bool barriers = PropLayout.BandDividerBarriers(plan.BandDivider, pp.RoadKmh);
            uint sig = Mix(Mix((uint)(byte)u.Window | ((uint)u.AppliedTraffic << 8) | ((uint)plan.Devices << 16) | ((uint)(u.AppliedWindow & 0xFF) << 24),
                               (uint)plan.BandMask | ((uint)plan.WaitingMask << 8)),
                           (pp.BandDividers > 0 ? 1u : 0u) | (pp.BandTapers > 0 ? 2u : 0u) | (pp.BandEnds > 0 ? 4u : 0u) | (pp.BandEdgeCones > 0 ? 8u : 0u)
                           | (pp.BandCentreCones > 0 ? 16u : 0u) | (barriers ? 32u : 0u) | ((uint)(pp.CrewOnBand + 1) << 6));
            sig = Mix(sig, Mix((uint)pp.BandFencePanels, (uint)pp.BandVergePanels | ((uint)pp.BandFenceGaps << 16)));
            bool changed = sig != pp.UpgradeSig;
            if (!changed && pp.UpgradeLine.Length > 0 && (m_Update & 63u) != 0u) return;
            string win = u.InSetup ? "setup" : u.InTeardown ? "teardown" : (u.Window + 1) + "/" + u.WindowCount;
            pp.UpgradeLine = "window=" + win + (u.Vacating ? " applied=" + (u.AppliedWindow + 1) + "/" + u.WindowCount : "") + " traffic=" + u.AppliedTraffic + " devices=" + plan.Devices + " bands=" + MaskText(PropLayout.UpgradeActiveMask(v, plan)) +
                             " waiting=" + MaskText(plan.WaitingMask) + " divider=" + (barriers ? "barriers" : "cones") + "(" + pp.BandDividers + ")" +
                             " taper=" + pp.BandTapers + " ends=" + pp.BandEnds + " kerbFence=" + pp.BandFencePanels + " vergeFence=" + pp.BandVergePanels +
                             " gaps=" + pp.BandFenceGaps + " edgeCones=" + pp.BandEdgeCones + " centreCones=" + pp.BandCentreCones + " heaps=" + pp.BandHeaps +
                             " crew=" + (pp.CrewOnBand == 1 ? "band" : pp.CrewOnBand == 0 ? "sidewalk" : "-") + " speed=" + RRWLog.F(pp.RoadKmh);
            if (!changed) return;
            pp.UpgradeSig = sig;
            RRWLog.Info("props: project #" + p.Id + " upgrade devices " + pp.UpgradeLine);
        }

        private static string MaskText(byte m)
        {
            if (m == 0) return "-";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 8; i++)
                if ((m & (1 << i)) != 0) { if (sb.Length > 0) sb.Append(','); sb.Append(i); }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ rrw.check

        // Upgrade works invariants per standing slot (all must be 0):
        //  (1) a band divider, taper cone or closed-end barrier on a lane that carries traffic (open or still draining; a dropped lane
        //      outside its blockers' range counts as open) or on an open sidewalk;
        //  (2) a band heap on a sidewalk, on a lane that carries traffic, or inside a driveway keep-out;
        //  (3) a band fence panel on an open lane.
        private void CheckUpgradeSlot(EntityManager em, ProjectProps pp, ProjectRecord p, in ProjectView v, PropSlot sl, List<string> problems,
                                      ref int inLane, ref int heaps, ref int fences, ref int checkedN)
        {
            var kind = PropKeys.Kind(sl.Key);
            int sub = PropKeys.Sub(sl.Key);
            bool device = kind == PropKind.BandDivider || kind == PropKind.BandTaper || kind == PropKind.BandEnd;
            bool heap = (kind == PropKind.SpoilHeap || kind == PropKind.DumpHeap || kind == PropKind.Windrow || kind == PropKind.Rubble) && sub >= kBandHeapSub;
            bool fence = kind == PropKind.BandFence;
            if (!device && !heap && !fence) return;
            if (!em.HasComponent<ObjTransform>(sl.Entity)) return;
            ChainEdge ce = null;
            for (int i = 0; i < pp.Chain.Count; i++) if (pp.Chain[i].Edge == sl.SiteEdge) { ce = pp.Chain[i]; break; }
            if (ce == null || ce.Record.Arc == null) return;
            if (!CrossStrips(p, v, ce, m_HCross)) return;   // the edge is being re-cut: nothing to compare against
            var arc = ce.Record.Arc;
            float3 pos = fence ? (sl.FootA + sl.FootB) * 0.5f : em.GetComponentData<ObjTransform>(sl.Entity).m_Position;
            float s = arc.Project(pos);
            float lat = math.dot((pos - arc.Position(s)).xz, arc.Right(s).xz) * ce.DirSign;
            float uu = ce.U0 + (ce.U1 - ce.U0) * (s / math.max(0.001f, arc.Length));
            checkedN++;
            if (!StripAt(m_HCross, lat, out var st)) return;   // beyond the new road: a removed strip (terrain, no traffic)
            var state = st.State;
            if (st.Drop && state == StripState.Ready && !InBlockerRange(ce, uu, 0.5f)) state = StripState.Draining;
            string where = "props #" + pp.Id + " " + PropKeys.Name(sl.Key);
            string what = st.Kind + "/" + state + (st.Drop ? "/drop" : "") + " lateral " + RRWLog.F(lat) + " u " + RRWLog.F(uu);
            if (device && ((IsLane(st.Kind) && state != StripState.Ready) || OpenWalk(st)))
            {
                if (++inLane <= 3) problems.Add(where + " band device on a strip that carries traffic (" + what + ")");
            }
            else if (heap)
            {
                bool bad = st.Kind == SubKind.Sidewalk || (IsLane(st.Kind) && state != StripState.Ready);
                bool keep = !bad && InKeepOut(pp, ce, uu - 0.5f, uu + 0.5f, lat < RoadCentre(ce) ? 0 : 1);
                if ((bad || keep) && ++heaps <= 3) problems.Add(where + " band heap " + (keep ? "inside a driveway keep-out" : "on a sidewalk or a lane with traffic") + " (" + what + ")");
            }
            else if (fence && IsLane(st.Kind) && state == StripState.Open)
            {
                if (++fences <= 3) problems.Add(where + " band fence panel on an open lane (" + what + ")");
            }
        }

#if DEVTOOLS
        // rrw.props.uw: the band device state of a mode H project, one line per band and edge.
        internal void DescribeUpgrade(uint id, List<string> lines)
        {
            if (!SiteRegistry.TryGetProject(id, out var p) || !PropState.Projects.TryGetValue(id, out var pp)) { lines.Add("p" + id + " no project"); return; }
            var v = p.View();
            if (!v.IsUpgrade) { lines.Add("p" + id + " not upgrade works (mode " + v.Mode + ")"); return; }
            var plan = PhasePlan.Props(v);
            var u = v.Upgrade;
            lines.Add("p" + id + " " + pp.UpgradeLine + " | closure=" + v.Closure + " open=" + RoadZoneMath.Describe(v.OpenLanes) + " ready=" + RoadZoneMath.Describe(v.WorkZonesReady) +
                      " zones=" + RoadZoneMath.Describe(u.Zones) + " applied=" + RoadZoneMath.Describe(u.AppliedZones) + " preClosed=" + RoadZoneMath.Describe(u.PreClosed) + " allAtOnce=" + u.AllAtOnce +
                      " bandDivider=" + plan.BandDivider + " fenceSides=" + RoadZoneMath.Describe(plan.FenceSides) + " inLaneReady=" + plan.InLaneReady +
                      " keeps=" + RRWLog.F(plan.StartBarrierKeep) + "/" + RRWLog.F(plan.EndBarrierKeep) + " access=" + pp.Access.Count);
            byte active = PropLayout.UpgradeActiveMask(v, plan);
            var h = new HEdge();
            for (int b = 0; b < u.BandCount; b++)
            {
                var bv = u.Band(b);
                string role = (active & (1 << b)) != 0 ? "active" : (plan.WaitingMask & (1 << b)) != 0 ? "waiting" : "idle";
                lines.Add("p" + id + " band " + b + " " + bv + " " + role + " phase=" + (PhasePlan.UpgradeBandPhase(v, b, out var ph, out float f, out _) ? ph + " f=" + RRWLog.F(f) : "-") +
                          " front=" + RRWLog.F(PhasePlan.UpgradeBandFront(v, b)));
                for (int ci = 0; ci < pp.Chain.Count; ci++)
                {
                    var ce = pp.Chain[ci];
                    BandEdge(p, v, ce, b, h);
                    if (!h.Valid) { lines.Add("p" + id + "  b" + b + " " + RRWLog.E(ce.Edge) + " no current sub-strips (" + (ce.Record.Upgrade == null ? "no upgrade state" : "stale") + ")"); continue; }
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < h.Strips.Count; i++)
                    {
                        var s = h.Strips[i];
                        sb.Append(' ').Append(s.Kind).Append('[').Append(RRWLog.F(s.Lo)).Append(',').Append(RRWLog.F(s.Hi)).Append(']').Append(s.State);
                        if (s.Drop) sb.Append("/drop");
                    }
                    lines.Add("p" + id + "  b" + b + " " + RRWLog.E(ce.Edge) + " u=[" + RRWLog.F(ce.Lo) + "," + RRWLog.F(ce.Hi) + "] strips" + sb +
                              " | div=" + RRWLog.F(h.DivX[0]) + "/" + RRWLog.F(h.DivX[1]) + " kerb=" + RRWLog.F(h.Kerb[0]) + "/" + RRWLog.F(h.Kerb[1]) +
                              " verge=" + RRWLog.F(h.Verge[0]) + "/" + RRWLog.F(h.Verge[1]) + " cones=" + h.Cones + " closed=" + h.Runs +
                              (h.Runs > 0 ? "[" + RRWLog.F(h.RunLo[0]) + "," + RRWLog.F(h.RunHi[0]) + "]" + (h.RunDrop[0] ? "drop" : "") + " dir=" + h.RunDir[0] + " taper=" + h.TaperSide[0] : ""));
                }
            }
        }
#endif
    }
}
