using System;
using System.Collections.Generic;
using System.Text;
using Unity.Entities;
using Unity.Mathematics;

// Runtime state of running upgrade works (mode H). Never saved: rebuilt from the saved upgrade tails of the sites after every
// load, registry rebuild and tail change. The Director is the only writer unless a field says otherwise.
namespace RealisticRoadWorks.V3
{
    // How a sub-strip of a band may be dressed (PhasePlan.CoverOf -> PhasePlan.UpgradeSpans). Surfaces picks it per sub-strip.
    public enum SubStripCover : byte
    {
        Works = 0,       // a lane-type sub-strip (drive lane, new parking, bike) that carries no car now: dirt, gravel, asphalt
        Traffic = 1,     // a lane-type sub-strip that carries cars (Dressing, waiting band, visual drop): Fresh Asphalt Cover only
        OpenWalk = 2,    // a sidewalk that stays open: Base Course Cover until the paver passed, then nothing
        WorksWalk = 3,   // a closed sidewalk, a verge or a median of a build band: dirt and gravel, no asphalt
        Terrain = 4,     // remove band, outside the new road: old asphalt / gravel, subgrade, topsoil
        Inside = 5,      // remove band, inside the new road (the new sidewalk or verge): gravel until restored
    }

    // Band devices of a mode H prop plan (PropPlan.Devices). Props lays each one out from the bands' sub-strips.
    [Flags]
    public enum BandDevices : ushort
    {
        None = 0,
        Divider = 1 << 0,     // along the inner edge of the closed part of every active band, where it meets a lane that carries
                              // traffic (lane drops, and build windows under Half: the closed half) (PropPlan.BandDivider: cones
                              // every kUwDividerSpacing, barriers for lane drops; Props also uses barriers from 60 km/h)
        Taper = 1 << 1,       // cone taper where traffic meets the closed part (approach end of each active band)
        ClosedEnd = 1 << 2,   // barrier line across the closed part of each active band at both ends (in-lane: needs InLaneReady, or
                              // for lane drops the edge's UpgradeEdgeState.BlockersRegistered)
        KerbFence = 1 << 3,   // fence between an open sidewalk sub-strip and the closed part
        VergeFence = 1 << 4,  // remove bands: fence on the outer edge of the new road (new sidewalk side), kUwVergeFenceGap gaps at
                              // building accesses (UpgradeEdgeState.DrivewayKeepOut)
        EdgeCones = 1 << 5,   // cones along the edge of a band whose strip carries traffic (Dressing, a waiting build band)
        CentreCones = 1 << 6, // cones along the direction split (re-marking under Dressing)
    }

    // A building access window along one side of a works edge (no standing machine, no heap, a gap in the verge fence).
    // CHAIN frame: U0 < U1 in chain u, Side = SidewalkLeft or SidewalkRight of the chain.
    public struct DrivewayWindow
    {
        public float U0, U1;
        public RoadZones Side;

        public static DrivewayWindow Around(float u, RoadZones side) =>
            new DrivewayWindow { U0 = u - RRWConst.kUwDrivewayKeepOut, U1 = u + RRWConst.kUwDrivewayKeepOut, Side = side };
    }

    // Rates and switches SiteFactory.CreateUpgradeProjects needs (From(RRWSetting) in the game; tests fill it by hand).
    public struct UpgradeRates
    {
        public float ConstructionHoursPerKm;
        public float ConstructionMinHours;
        public float DemolitionRatio;        // demolition hours per km / construction hours per km
        public bool DropGate;                // RRWGates.UpgradeDrop at creation
        public bool PreCoverGate;            // RRWGates.UpgradePreCover at creation

        public static UpgradeRates From(RRWSetting s) => new UpgradeRates
        {
            ConstructionHoursPerKm = s.ConstructionHoursPerKm,
            ConstructionMinHours = s.ConstructionMinHours,
            DemolitionRatio = WorkTime.DemolitionRatio(s),
            DropGate = RRWGates.UpgradeDrop,
            PreCoverGate = RRWGates.UpgradePreCover,
        };
    }

    // Lane helpers of upgrade works. Pure.
    public static class UpgradeLanes
    {
        // A lane whose centre lies inside [Lo + kUwDropInset, Hi - kUwDropInset] of a band (same frame) is a lane of that band:
        // dropped while the band works under the Drop primitive.
        public static bool InBand(float laneCentre, float lo, float hi) =>
            laneCentre > lo + RRWConst.kUwDropInset && laneCentre < hi - RRWConst.kUwDropInset;

        // On a cross-section (sub-strips over the whole road, PhasePlan / EdgeSection.CrossSection or UpgradeDiff.SubStrips of the
        // new layout over its outline), does every travel direction that has a drive lane keep one outside the build / rebuild
        // bands of `window` (window < 0: of every window)? Bands and cross-section in the same frame.
        public static bool EveryDirectionKeepsLane(List<SubStrip> crossSection, IList<UpgradeBand> bands, int bandCount, int window)
        {
            if (crossSection == null) return false;
            var plan = s_Plan;
            // window < 0: every build band at once
            UpgradeTempLanes.Plan(crossSection, bands, bandCount, window < 0 ? 0 : window, window < 0, plan);
            if (plan.LaneCount == 0) return false;
            return plan.Arrangement == TempArrangement.Open || plan.Arrangement == TempArrangement.Untouched;
        }

        private static readonly TempLanePlan s_Plan = new TempLanePlan();

        // A build / rebuild band that works in `window` holds this lane centre (window < 0: any window; allAtOnce: the window's
        // bands and every later one, which drop their lanes from the start).
        public static bool InWorkBands(float laneCentre, IList<UpgradeBand> bands, int bandCount, int window, bool allAtOnce)
        {
            for (int i = 0; i < bandCount && i < bands.Count; i++)
            {
                var b = bands[i];
                if (b.Kind != BandKind.Build && b.Kind != BandKind.Rebuild) continue;
                if (window >= 0 && (allAtOnce ? b.Window < window : b.Window != window)) continue;
                if (InBand(laneCentre, b.Lo, b.Hi)) return true;
            }
            return false;
        }

        // Same from a classifier result and the NEW layout (Tools, at creation: SiteFactoryEdge.KeepsLanes over every build band).
        public static bool EveryDirectionKeepsLane(CompositionLayout neu, in UpgradeSpec spec)
        {
            if (neu == null || !(neu.OuterR > neu.OuterL)) return false;
            var cs = new List<SubStrip>(16);
            UpgradeDiff.SubStrips(neu, neu.OuterL, neu.OuterR, cs);
            var bands = new List<UpgradeBand>(4);
            for (int i = 0; i < spec.BandCount; i++) bands.Add(spec.Band(i));
            return EveryDirectionKeepsLane(cs, bands, bands.Count, -1);
        }

        // Does a list of sub-strips hold a lane-type piece (drive lane, new parking, bike)?
        public static bool HasCar(List<SubStrip> strips)
        {
            if (strips == null) return false;
            for (int k = 0; k < strips.Count; k++)
                if (strips[k].Kind == SubKind.DriveLane || strips[k].Kind == SubKind.NewParking || strips[k].Kind == SubKind.Bike) return true;
            return false;
        }
    }

    // Shared sub-strip rules of mode H that read the runtime edge state. Machines, Props, Surfaces and the dev checks use these so
    // they never disagree about a strip.
    public static partial class UpgradeZones
    {
        // Does sub-strip `s` (EDGE frame, of saved band `savedBand`; -1 = any band) carry no traffic now? Either its lane group is
        // closed now (in UpgradeView.AppliedZones: the applied window's groups, PreClosed, the half after the swap) AND Traffic
        // applied the closure on this edge (EdgeRecord.ClosureApplied == Closed and the group not in OpenLanesApplied), or it is
        // a drive-lane / bike sub-strip holding a lane of the current drop report for that band with the blockers registered
        // (UpgradeEdgeState.DroppedIn). New parking switched off is separate (SubStripParkingOff). Anything else carries traffic.
        public static bool SubStripClosed(EdgeRecord rec, in UpgradeView u, in SubStrip s, int savedBand)
        {
            var ue = rec?.Upgrade;
            if (ue == null) return false;
            bool rev = ue.ChainReversed;
            var zone = OfSubStrip(s, rev, RoadZoneMath.DirSplitChain(rec.Section, rev)) & RoadZones.AllLanes;
            if (zone != RoadZones.None && (zone & ~u.AppliedZones) == 0 && rec.ClosureApplied == ClosureLevel.Closed
                && (rec.OpenLanesApplied & zone) == 0) return true;
            if (s.Kind != SubKind.DriveLane && s.Kind != SubKind.Bike) return false;
            return ue.DroppedIn(rec.GeometryRevision, u, s.Lo, s.Hi, savedBand) || ue.TempLaneFree(rec.GeometryRevision, u, s.Lo, s.Hi);
        }

        // A new parking sub-strip whose (empty) lane Traffic switched off, list current for this geometry.
        public static bool SubStripParkingOff(EdgeRecord rec, in SubStrip s) =>
            s.Kind == SubKind.NewParking && rec?.Upgrade != null && rec.Upgrade.ParkingOffAt(rec.GeometryRevision, s.Lo, s.Hi);

        // Carries no traffic now: closed (SubStripClosed) or switched-off new parking.
        public static bool SubStripIdle(EdgeRecord rec, in UpgradeView u, in SubStrip s, int savedBand) =>
            SubStripClosed(rec, u, s, savedBand) || SubStripParkingOff(rec, s);

        // The machine-safety state of sub-strip `s` of saved band `savedBand` now (PhasePlan.MachineSafe input): its lane group in
        // UpgradeView.MachineSafe (closed and drained, and no parked cars left in a parking group), a drop lane of that band with DropReadyFor, the intrusion flag, a
        // switched-off new parking lane, and the caller's driveway keep-out test.
        public static SubStripState SubStripStateOf(EdgeRecord rec, in UpgradeView u, in SubStrip s, int savedBand, bool inDrivewayKeepOut)
        {
            var st = new SubStripState { Kind = s.Kind, InDrivewayKeepOut = inDrivewayKeepOut };
            var ue = rec?.Upgrade;
            if (ue == null) return st;
            bool rev = ue.ChainReversed;
            var lanes = OfSubStrip(s, rev, RoadZoneMath.DirSplitChain(rec.Section, rev)) & RoadZones.AllLanes;
            st.GroupClosedReady = lanes != RoadZones.None && (lanes & ~u.MachineSafe) == 0
                                  && (lanes & rec.ZonesParked & RoadZones.Parking) == 0;   // parked cars stay in a closed parking group
            if (s.Kind == SubKind.DriveLane && ue.DroppedIn(rec.GeometryRevision, u, s.Lo, s.Hi, savedBand))
            {
                st.DropReady = ue.DroppedIn(rec.GeometryRevision, u, s.Lo, s.Hi, savedBand, true, true);
                st.DropIntrusion = ue.DropIntrusion;
            }
            else if ((s.Kind == SubKind.DriveLane || s.Kind == SubKind.NewParking) && ue.TempLaneFree(rec.GeometryRevision, u, s.Lo, s.Hi))
            {
                // the lane that runs here moved onto its temporary lane
                st.DropReady = true;
                st.DropIntrusion = ue.DropIntrusion;
            }
            st.ParkingOff = SubStripParkingOff(rec, s);
            return st;
        }
    }

    // Upgrade state of one works edge (EdgeRecord.Upgrade; null on every other edge). Edge frame unless a field says chain frame.
    public sealed class UpgradeEdgeState
    {
        // ---- the saved tail (Director: Load at adopt, registry rebuild and whenever the site's tail changed)
        public PlanKind Plan;
        public UpgradeClass Class;           // the edge's own class (the project class is UpgradeRuntime.Class)
        public bool ChainReversed;           // the edge curve runs against +u (RoadZoneMath.ChainReversed of its chain coordinates)
        public int BandCount;
        public readonly UpgradeBand[] Bands = new UpgradeBand[RRWConst.kUwMaxBands];      // EDGE frame, as saved
        public readonly UpgradeBand[] ChainBands = new UpgradeBand[RRWConst.kUwMaxBands]; // CHAIN frame (UpgradePlan.ToChain)
        public readonly int[] ChainIndex = { -1, -1, -1, -1 };  // index into UpgradeRuntime.Bands (SyncChainIndex; readers check
                                                                // ChainIndexCurrent(project.Upgrade) first)
        public int ChainIndexRevision = -1;  // UpgradeRuntime.Revision ChainIndex was computed for
        public int ChainIndexTail = -1;      // TailRevision ChainIndex was computed for
        public int TailRevision;             // ++ whenever Load saw different bands (sub-strips are re-cut)

        // ---- sub-strips (Director: RefreshSubStrips whenever GeometryRevision or TailRevision changed). Readers: Surfaces, Props,
        //      Traffic, Machines. EDGE frame, per band, sorted by Lo.
        public readonly List<SubStrip>[] SubStrips =
        {
            new List<SubStrip>(8), new List<SubStrip>(8), new List<SubStrip>(8), new List<SubStrip>(8),
        };
        public int SubStripsRevision = -1;   // EdgeRecord.GeometryRevision they were cut for
        public int SubStripsTail = -1;       // TailRevision they were cut for
        public bool SubStripsFromLayout;     // cut from Layout (else from the EdgeSection approximation)
        public readonly List<SubStrip> CrossSection = new List<SubStrip>(16);  // the whole road, same source (lane counts per direction)
        // Optional: the live layout of the new road (Director: UpgradeLayoutReader.Read of the edge in its own curve's frame, once
        // per GeometryRevision). When it is set for the current revision, sub-strips come from it (sidewalks, verges, bike lanes
        // exact); otherwise from the section.
        public CompositionLayout Layout;
        public int LayoutRevision = -1;

        // ---- drop report (Traffic is the ONLY writer; Director, Machines, Props, Surfaces, UI, Dev read). The report belongs to
        //      one key: the GeometryRevision, the layout window, AllAtOnce and the TailRevision it was resolved for. Traffic
        //      re-resolves whenever any of them changes (DropCurrent false) and resets DropApplied, BlockersRegistered and
        //      DropCleanChecks when the lane set changes; it sets DropWindow = -2 (no drops) when it releases every drop lane.
        //      Readers use DropReadyFor / BlockersRegisteredFor, never the raw flags.
        public readonly List<Entity> DropLanes = new List<Entity>(4);   // car lanes dropped NOW: centre inside a dropping band
                                                                       // (UpgradeLanes.InBand; DropBandOf)
        public readonly List<float> DropLaneCentres = new List<float>(4); // EDGE-frame lateral centre of DropLanes[i] (EcsUtil.LaneCentre),
                                                                       // written together with DropLanes, same order and count.
                                                                       // Readers: DropLaneIn / DroppedIn (a count that differs from
                                                                       // DropLanes = not written: no lane counts as dropped)
        public float DropSafeU0 = float.NaN; // CHAIN-frame u range between the first and the last blocker of this edge's drop lanes
        public float DropSafeU1 = float.NaN; // (written with the drop report; NaN = not written). Machines keep lane-drop crews inside it
        public int DropLanesRevision = -1;   // GeometryRevision DropLanes were resolved for
        public int DropWindow = -2;          // layout window DropLanes were resolved for (-2 = no drops)
        public bool DropAllAtOnce;           // the AllAtOnce state DropLanes were resolved with
        public int DropTail = -1;            // TailRevision DropLanes were resolved for
        public bool DropApplied;             // every lane of DropLanes carries its blockers (kUwBlockerNodeSetback from both nodes)
        public bool BlockersRegistered;      // the game registered the blockers on every drop lane, and on no other lane
        public bool DropIntrusion;           // the latest drain check saw a vehicle on a drop lane outside the approach zones
        public int DropCleanChecks;          // consecutive clean drain checks since the blockers were registered
        public uint DropReportUpdate;        // RRWClock.UpdateIndex of the report (0 = never)

        // The drop report belongs to this geometry, layout window and AllAtOnce state (and the current tail).
        public bool DropCurrent(int geometryRevision, int layoutWindow, bool allAtOnce) =>
            DropWindow >= 0 && DropWindow == layoutWindow && DropLanesRevision == geometryRevision && DropAllAtOnce == allAtOnce
            && DropTail == TailRevision;

        // Machines may stand on the drop lanes (PhasePlan.MachineSafe SubStripState.DropReady): the current report has the
        // blockers applied and registered, no intrusion and at least two clean checks.
        public bool DropReadyFor(int geometryRevision, int layoutWindow, bool allAtOnce) =>
            DropCurrent(geometryRevision, layoutWindow, allAtOnce) && DropApplied && BlockersRegistered && !DropIntrusion && DropCleanChecks >= 2;

        // The blockers of the current report are registered (CoverOf `closed` for drop lanes, in-lane devices of lane drops).
        public bool BlockersRegisteredFor(int geometryRevision, int layoutWindow, bool allAtOnce) =>
            DropCurrent(geometryRevision, layoutWindow, allAtOnce) && BlockersRegistered;

        // The window the drop report belongs to when it is current for this view: the applied window (UpgradeView.AppliedWindow)
        // or the layout window - they differ only during the Vacate step of a window start. -2 = no current report. The view
        // forms below use it, so readers agree with Traffic whichever of the two it resolved the drops for.
        public int DropWindowFor(int geometryRevision, in UpgradeView u)
        {
            int w = DropWindow;
            if (w < 0 || (w != u.AppliedWindow && w != u.LayoutWindow)) return -2;
            return DropCurrent(geometryRevision, w, u.AllAtOnce) ? w : -2;
        }

        public bool DropReadyFor(int geometryRevision, in UpgradeView u)
        {
            int w = DropWindowFor(geometryRevision, u);
            return w >= 0 && DropReadyFor(geometryRevision, w, u.AllAtOnce);
        }

        public bool BlockersRegisteredFor(int geometryRevision, in UpgradeView u) =>
            DropWindowFor(geometryRevision, u) >= 0 && BlockersRegistered;

        // DropLaneCentres belongs to DropLanes (Traffic wrote both).
        public bool DropCentresWritten => DropLaneCentres.Count == DropLanes.Count;

        // The lane is in the drop report (raw list: check DropCurrent / DropWindowFor for the report's validity).
        public bool IsDropLane(Entity lane) => lane != Entity.Null && DropLanes.Contains(lane);

        // EDGE-frame centre of a lane of the drop report (false when it is not listed or the centres were not written).
        public bool DropLaneCentre(Entity lane, out float centre)
        {
            centre = 0f;
            int i = lane == Entity.Null ? -1 : DropLanes.IndexOf(lane);
            if (i < 0 || !DropCentresWritten) return false;
            centre = DropLaneCentres[i];
            return true;
        }

        // A lane of the drop report has its centre inside (lo, hi) (EDGE frame, e.g. a sub-strip). Raw list, like IsDropLane.
        public bool DropLaneAt(float lo, float hi)
        {
            if (!DropCentresWritten) return false;
            for (int i = 0; i < DropLaneCentres.Count; i++)
                if (DropLaneCentres[i] > lo && DropLaneCentres[i] < hi) return true;
            return false;
        }

        // Is (lo, hi) (EDGE frame, a sub-strip) a lane dropped by saved band `band` (-1 = any band) under the current report for
        // this view? A listed lane's centre lies inside and DropBandOf maps it to that band. Without the centres nothing counts
        // as dropped: a sub-strip clipped from an open lane must never pass for a dropped one. registered: also require
        // registered blockers; ready: require DropReadyFor (blockers drained, no intrusion).
        public bool DroppedIn(int geometryRevision, in UpgradeView u, float lo, float hi, int band, bool registered = true, bool ready = false)
        {
            int w = DropWindowFor(geometryRevision, u);
            if (w < 0) return false;
            if (registered && !BlockersRegistered) return false;
            if (ready && !DropReadyFor(geometryRevision, w, u.AllAtOnce)) return false;
            if (DropCentresWritten)
            {
                for (int i = 0; i < DropLaneCentres.Count; i++)
                {
                    float c = DropLaneCentres[i];
                    if (!(c > lo && c < hi)) continue;
                    int b = DropBandOf(c, w, u.AllAtOnce);
                    if (b >= 0 && (band < 0 || b == band)) return true;
                }
            }
            return false;
        }

        // The chain-u range between the first and last blocker (false when Traffic has not written one).
        public bool DropSafeRange(out float u0, out float u1)
        {
            u0 = DropSafeU0; u1 = DropSafeU1;
            return !float.IsNaN(u0) && !float.IsNaN(u1) && u1 > u0;
        }

        // ---- new parking lanes switched off (Traffic is the ONLY writer; Surfaces, Dev read). Traffic disables only EMPTY new
        //      parking lanes inside build bands; a lane that held a car stays in use and is not listed.
        public readonly List<Entity> ParkingOffLanes = new List<Entity>(2);
        public readonly List<float> ParkingOffCentres = new List<float>(2);   // EDGE-frame lateral centre of each listed lane
        public int ParkingOffRevision = -1;  // GeometryRevision the list belongs to

        // A disabled new parking lane lies in [lo, hi] (EDGE frame, e.g. a NewParking sub-strip): PhasePlan.CoverOf parkingOff.
        public bool ParkingOffAt(float lo, float hi)
        {
            for (int i = 0; i < ParkingOffCentres.Count; i++)
                if (ParkingOffCentres[i] > lo && ParkingOffCentres[i] < hi) return true;
            return false;
        }

        // Same, only while the list belongs to this geometry (ParkingOffRevision == geometryRevision).
        public bool ParkingOffAt(int geometryRevision, float lo, float hi) => ParkingOffRevision == geometryRevision && ParkingOffAt(lo, hi);

        // ---- driveway keep-outs (Director, from the edge's ConnectedBuilding access positions; CHAIN frame). Readers: Machines,
        //      Props, Surfaces (heaps), Dev.
        public readonly List<DrivewayWindow> DrivewayKeepOut = new List<DrivewayWindow>(8);
        public int DrivewayRevision = -1;    // GeometryRevision they were computed for

        // Copies the tail of a site (bands, chain frame). True when the bands changed (TailRevision then moved on). The primitive
        // saved at a window start (WinPrim bits 3-5) is copied but is no tail change: sub-strips, chain indices and the drop
        // report do not depend on it, so a stamp leaves them current.
        public bool Load(in RoadWorksSite site)
        {
            bool rev = RoadZoneMath.ChainReversed(site.m_ChainU0, site.m_ChainU1);
            int n = site.IsUpgrade ? math.min(site.m_BandCount, RRWConst.kUwMaxBands) : 0;
            bool changed = n != BandCount || rev != ChainReversed || Plan != site.Plan;
            for (int i = 0; i < n && !changed; i++) changed = !Bands[i].SameShape(site.Band(i));
            Plan = site.Plan;
            Class = site.UpClass;
            ChainReversed = rev;
            BandCount = n;
            for (int i = 0; i < RRWConst.kUwMaxBands; i++)
            {
                Bands[i] = i < n ? site.Band(i) : default;
                ChainBands[i] = i < n ? UpgradePlan.ToChain(Bands[i], rev) : default;
                if (i >= n) ChainIndex[i] = -1;
            }
            if (changed) TailRevision++;
            return changed;
        }

        // Points ChainIndex at the project's chain bands. Recomputes when the runtime was rebuilt (another edge's tail can re-order
        // the union) or this edge's tail changed; cheap, the Director calls it for every edge every update. True when recomputed.
        public bool SyncChainIndex(UpgradeRuntime rt)
        {
            if (rt == null || ChainIndexCurrent(rt)) return false;
            for (int i = 0; i < RRWConst.kUwMaxBands; i++) ChainIndex[i] = i < BandCount ? rt.ChainIndexOf(ChainBands[i]) : -1;
            ChainIndexRevision = rt.Revision;
            ChainIndexTail = TailRevision;
            return true;
        }

        // ChainIndex belongs to this runtime and this tail.
        public bool ChainIndexCurrent(UpgradeRuntime rt) => rt != null && ChainIndexRevision == rt.Revision && ChainIndexTail == TailRevision;

        // Re-cuts the sub-strips when the geometry or the tail changed (or force). From Layout when it belongs to this revision,
        // else from the measured section (sidewalkLeft / sidewalkRight: EDGE-frame sides whose raised strip is a sidewalk; a
        // verge otherwise). True when something was recomputed.
        // A layout that arrives after a cut from the section in the same revision re-cuts too.
        public bool RefreshSubStrips(in EdgeSection sec, int geometryRevision, bool leftHandTraffic, bool sidewalkLeft = true,
                                     bool sidewalkRight = true, bool force = false)
        {
            bool fromLayout = Layout != null && LayoutRevision == geometryRevision && Layout.Readable && Layout.OuterR > Layout.OuterL;
            if (!force && SubStripsRevision == geometryRevision && SubStripsTail == TailRevision && SubStripsFromLayout == fromLayout) return false;
            if (fromLayout) UpgradeDiff.SubStrips(Layout, Layout.OuterL, Layout.OuterR, CrossSection);
            else sec.CrossSection(leftHandTraffic, CrossSection, sidewalkLeft, sidewalkRight);
            for (int i = 0; i < RRWConst.kUwMaxBands; i++)
            {
                var l = SubStrips[i];
                l.Clear();
                if (i >= BandCount) continue;
                if (fromLayout) UpgradeDiff.SubStrips(Layout, Bands[i], l);
                else sec.SubStrips(Bands[i], leftHandTraffic, l, sidewalkLeft, sidewalkRight);
            }
            SubStripsFromLayout = fromLayout;
            SubStripsRevision = geometryRevision;
            SubStripsTail = TailRevision;
            return true;
        }

        // ---- temporary lanes (UpgradeTempLanes) of a window, cached per geometry / tail / window / AllAtOnce. Readers: Traffic
        //      (drops, lane shifts), Surfaces (yellow lines), Machines (moved-away lanes), Dev.
        public readonly TempLanePlan TempPlan = new TempLanePlan();
        private int m_PlanGeo = -1, m_PlanTail = -1, m_PlanWindow = -9, m_PlanCs = -1;
        private bool m_PlanAao;
        private float m_PlanXLo = float.NaN, m_PlanXHi = float.NaN;

        // The plan for `window` (extraLo / extraHi: a closed part of the carriageway, e.g. the half resurfaced now).
        public TempLanePlan PlanFor(int window, bool allAtOnce, float extraLo = float.NaN, float extraHi = float.NaN)
        {
            if (m_PlanGeo == SubStripsRevision && m_PlanTail == TailRevision && m_PlanWindow == window && m_PlanAao == allAtOnce
                && m_PlanCs == CrossSection.Count && SameF(m_PlanXLo, extraLo) && SameF(m_PlanXHi, extraHi)) return TempPlan;
            UpgradeTempLanes.Plan(CrossSection, Bands, BandCount, window, allAtOnce, TempPlan, extraLo, extraHi);
            m_PlanGeo = SubStripsRevision; m_PlanTail = TailRevision; m_PlanWindow = window; m_PlanAao = allAtOnce;
            m_PlanCs = CrossSection.Count; m_PlanXLo = extraLo; m_PlanXHi = extraHi;
            return TempPlan;
        }

        private static bool SameF(float a, float b) => (float.IsNaN(a) && float.IsNaN(b)) || a == b;

        // EDGE-frame lateral range of one carriageway half of the cross-section (edgeLeft: the half left of the direction split).
        // False without drive lanes of both directions.
        public bool HalfInterval(bool edgeLeft, out float lo, out float hi)
        {
            lo = hi = 0f;
            float cLo = float.MaxValue, cHi = float.MinValue, sumF = 0f, sumB = 0f;
            int nF = 0, nB = 0;
            for (int k = 0; k < CrossSection.Count; k++)
            {
                var s = CrossSection[k];
                if (s.Kind != SubKind.DriveLane && s.Kind != SubKind.NewParking && s.Kind != SubKind.Bike) continue;
                cLo = math.min(cLo, s.Lo); cHi = math.max(cHi, s.Hi);
                if (s.Kind != SubKind.DriveLane || s.Dir == 0) continue;
                if (s.Dir > 0) { nF++; sumF += (s.Lo + s.Hi) * 0.5f; } else { nB++; sumB += (s.Lo + s.Hi) * 0.5f; }
            }
            if (nF == 0 || nB == 0) return false;
            int leftDir = sumF / nF < sumB / nB ? 1 : -1;
            float leftHi = float.MinValue, rightLo = float.MaxValue;
            for (int k = 0; k < CrossSection.Count; k++)
            {
                var s = CrossSection[k];
                if (s.Kind != SubKind.DriveLane || s.Dir == 0) continue;
                if (s.Dir == leftDir) leftHi = math.max(leftHi, s.Hi); else rightLo = math.min(rightLo, s.Lo);
            }
            float split = (leftHi + rightLo) * 0.5f;
            if (edgeLeft) { lo = cLo; hi = split; } else { lo = split; hi = cHi; }
            return hi > lo;
        }

        // The EDGE-frame half of a chain-frame car half (LeftHalf / RightHalf) on this edge.
        public bool ChainHalfInterval(RoadZones chainHalf, out float lo, out float hi)
        {
            bool chainLeft = (chainHalf & RoadZones.LeftHalf) != 0;
            return HalfInterval(chainLeft != ChainReversed, out lo, out hi);
        }

        // Re-marking window: both directions fit on temporary lanes over either half while the other one is resurfaced.
        public bool RemarkTwoWay(int window, bool allAtOnce)
        {
            for (int h = 0; h < 2; h++)
            {
                if (!HalfInterval(h == 0, out float lo, out float hi)) return false;
                var plan = PlanFor(window, allAtOnce, lo, hi);
                if (plan.Arrangement != TempArrangement.Open) return false;
            }
            return true;
        }

        // Re-marking window: at least one shared lane (alternating) over either half while the other one is resurfaced.
        public bool RemarkShuttle(int window, bool allAtOnce)
        {
            for (int h = 0; h < 2; h++)
            {
                if (!HalfInterval(h == 0, out float lo, out float hi)) return false;
                var a = PlanFor(window, allAtOnce, lo, hi).Arrangement;
                if (a != TempArrangement.Open && a != TempArrangement.Shuttle) return false;
            }
            return true;
        }

        // Build window: the plan keeps traffic moving (own lanes or one shared lane); shared: it uses the shared lane.
        public bool KeepsTraffic(int window, bool allAtOnce, out bool shared)
        {
            var a = PlanFor(window, allAtOnce).Arrangement;
            shared = a == TempArrangement.Shuttle;
            return a != TempArrangement.Closed && PlanFor(window, allAtOnce).LaneCount > 0;
        }

        // ---- temporary lanes on a half (Traffic is the ONLY writer): the closed half of a re-marking window whose lanes moved
        //      onto the other half. NaN = none. Surfaces draws the lines of PlanFor(TempHalfWindow, AllAtOnce, lo, hi).
        public int TempHalfWindow = -2;
        public float TempHalfLo = float.NaN, TempHalfHi = float.NaN;

        // ---- temporary lane report (Traffic is the ONLY writer): every lane of the plan of ShiftWindow reached its temporary lane
        //      (or its strip in the works) for this geometry.
        public bool ShiftSettled;
        public int ShiftWindow = -2;
        public int ShiftRevision = -1;

        // Is (lo, hi) (EDGE frame, a drive-lane sub-strip) free of traffic because the lanes moved: no open temporary lane
        // overlaps it, every lane of the plan moved where it belongs, and every dropped lane whose old or new strip overlaps it is
        // drop-ready (markers registered and drained).
        public bool TempLaneFree(int geometryRevision, in UpgradeView u, float lo, float hi)
        {
            int w = DropWindowFor(geometryRevision, u);
            if (w < 0 || !ShiftSettled || ShiftWindow != w || ShiftRevision != geometryRevision) return false;
            var plan = PlanFor(w, u.AllAtOnce);
            if (plan.LaneCount == 0 || plan.Arrangement == TempArrangement.Untouched) return false;
            for (int k = 0; k < plan.SlotCount; k++)
                if (math.min(hi, plan.Slots[k].Hi) - math.max(lo, plan.Slots[k].Lo) > 0.05f) return false;
            bool needReady = false;
            for (int i = 0; i < plan.LaneCount; i++)
            {
                if (plan.LaneSlot[i] >= 0) continue;
                float hw = plan.LaneWidth[i] * 0.5f;
                bool oldOn = math.min(hi, plan.LaneCentre[i] + hw) - math.max(lo, plan.LaneCentre[i] - hw) > 0.05f;
                bool newOn = math.min(hi, plan.LaneTarget[i] + hw) - math.max(lo, plan.LaneTarget[i] - hw) > 0.05f;
                needReady |= oldOn || newOn;
            }
            return !needReady || DropReadyFor(geometryRevision, w, u.AllAtOnce);
        }

        // Saved band (index into Bands) whose lanes are dropped for a car lane with this EDGE-frame centre: the temporary lane
        // plan of `window` drops the lane (it is in a build / rebuild band of the window, or has no room on the drivable asphalt);
        // the band is the window's build / rebuild band holding the lane, else the nearest one. -1 = not dropped.
        public int DropBandOf(float laneCentreEdge, int window, bool allAtOnce)
        {
            var plan = PlanFor(window, allAtOnce);
            bool planned = plan.LaneCount > 0;   // no cross-section yet: the lanes inside the window's bands
            if (planned && !plan.Dropped(laneCentreEdge)) return -1;
            int best = -1;
            float bd = float.MaxValue;
            for (int i = 0; i < BandCount; i++)
            {
                var b = Bands[i];
                if (b.Kind != BandKind.Build && b.Kind != BandKind.Rebuild) continue;
                if (allAtOnce ? b.Window < window : b.Window != window) continue;
                if (UpgradeLanes.InBand(laneCentreEdge, b.Lo, b.Hi)) return i;
                if (!planned) continue;
                float d = math.min(math.abs(laneCentreEdge - b.Lo), math.abs(laneCentreEdge - b.Hi));
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        // The edge's bands in `window` have a lane-type sub-strip.
        public bool HasCarIn(int window)
        {
            for (int i = 0; i < BandCount; i++)
                if (Bands[i].Window == window && Bands[i].Kind != BandKind.Remove && UpgradeLanes.HasCar(SubStrips[i])) return true;
            return false;
        }

        // Every direction keeps a drive lane outside the build bands of `window` (on this edge's cross-section).
        public bool KeepsLaneEachDirection(int window) =>
            EveryDirectionKeepsLaneOf(CrossSection, Bands, BandCount, window);

        private static bool EveryDirectionKeepsLaneOf(List<SubStrip> cs, UpgradeBand[] bands, int n, int window) =>
            UpgradeLanes.EveryDirectionKeepsLane(cs, bands, n, window);

        // A chain-u range on the given chain sides (SidewalkLeft / SidewalkRight bits) overlaps a driveway keep-out.
        public bool InKeepOut(float u0, float u1, RoadZones sides)
        {
            float a = math.min(u0, u1), b = math.max(u0, u1);
            for (int i = 0; i < DrivewayKeepOut.Count; i++)
            {
                var k = DrivewayKeepOut[i];
                if ((k.Side & sides) == 0) continue;
                if (b >= k.U0 && a <= k.U1) return true;
            }
            return false;
        }
    }

    // Runtime state of one mode H project (ProjectRecord.Upgrade; null on every other project). Built by the Director from the
    // edges' saved tails: Begin(first site), AddEdge(site) for every edge, Finish(). The schedule, the band windows, the setup
    // share and AllAtOnce come from the saves only (fixed at creation, never recomputed; AllAtOnce may only be cleared).
    // A rebuild of the same project (same plan and schedule, e.g. after StampPrimitive) keeps the runtime decisions: the last
    // primitives (under the saved ceilings), the AllAtOnce clear, and HasCar / SafeLo / SafeHi of every chain band that did not
    // change. Any other rebuild starts from scratch.
    public sealed class UpgradeRuntime
    {
        private static int s_Revision;

        // ---- from the saved tails (Begin / AddEdge / Finish)
        public PlanKind Plan;
        public UpgradeClass Class;           // project class (UpgradePlan.ClassOf over the chain bands)
        public UpgradeSchedule Schedule;     // from the first edge; edges that disagree count in Mismatch (Director logs a Warn)
        public UpgradeFlags SavedFlags;      // from the first edge
        public int Mismatch;                 // edges whose schedule or flags (AllAtOnce aside) differ from the first edge's
        public int EdgeCount;
        public readonly List<ChainBand> Bands = new List<ChainBand>(RRWConst.kUwMaxChainBands);   // CHAIN frame, UpgradePlan.Union order
        public readonly BandTraffic[] Saved = new BandTraffic[RRWConst.kUwMaxWindows];  // ceiling per window (Undecided = not started)
        public int Revision;                 // new value on every Finish, unique across runtimes (modules re-layout when it differs
                                             // from their copy; UpgradeEdgeState.SyncChainIndex)

        // ---- Director writes every update (the per-window decisions)
        public readonly BandTraffic[] Primitive = new BandTraffic[RRWConst.kUwMaxWindows];   // started windows: effective (PhasePlan.
                                             // WindowPrimitive with Saved as ceiling); later windows: expected (no ceiling)
        public readonly StageBlockReason[] WindowReason = new StageBlockReason[RRWConst.kUwMaxWindows]; // why a window fell to Dressing
        public readonly RoadZones[] Zones = new RoadZones[RRWConst.kUwMaxWindows];  // lane groups each window closes (PhasePlan.WindowZones)
        public readonly float[] SafeLo = new float[RRWConst.kUwMaxChainBands];      // machine-safe range per chain band (chain frame;
        public readonly float[] SafeHi = new float[RRWConst.kUwMaxChainBands];      // SafeHi <= SafeLo = none)
        public readonly bool[] HasCar = new bool[RRWConst.kUwMaxChainBands];        // some edge has a lane-type sub-strip in the band
        public int DerivedRevision = -1;     // Revision HasCar / SafeLo / SafeHi were filled for (MarkDerived); a rebuild that changed
                                             // a chain band leaves them stale until the Director fills them again
        public bool AllAtOnce;               // saved flag (every edge); cleared for good by ClearAllAtOnce or when a started window
                                             // with a build band was saved with a car closure other than a lane drop
        public bool AllAtOnceCleared;        // ClearAllAtOnce ran: kept through every rebuild of this project
        public RoadZones PreClosed;          // groups of later windows already closed (AllAtOnce)
        public RoadZones MachineSafe;        // PhasePlan.UpgradeSafeZones of this update
        public uint CorridorPrev, CorridorNext; // linked projects of the same drag (adopt: SiteRegistry.TakeCorridor; 0 = none).
                                             // Runtime only: after a load the Director re-links mode H projects whose chain ends share a node
        public uint ReclassifyUpdate;        // RRWClock.UpdateIndex when the primitives must be re-picked (geometry / classification
                                             // change, gate change); 0 = nothing pending

        // ---- detour verdict (Traffic is the ONLY writer; the Director reads it for WindowPrimitiveInput.DetourExists). Kept
        //      through rebuilds; it belongs to the ProjectRecord.Revision it was computed for. No verdict yet = no detour (Half
        //      is not offered until Traffic found one).
        public RoadZones DetourHalves;       // car halves (LeftHalf / RightHalf, CHAIN frame) whose traffic has a directed detour
                                             // when that half is closed
        public int DetourRevision = -1;      // ProjectRecord.Revision the verdict belongs to
        public uint DetourUpdate;            // RRWClock.UpdateIndex of the verdict (0 = never)

        private readonly List<UpgradeBand> m_Collect = new List<UpgradeBand>(16);
        private readonly List<ChainBand> m_PrevBands = new List<ChainBand>(RRWConst.kUwMaxChainBands);
        private readonly float[] m_PrevLo = new float[RRWConst.kUwMaxChainBands];
        private readonly float[] m_PrevHi = new float[RRWConst.kUwMaxChainBands];
        private readonly bool[] m_PrevCar = new bool[RRWConst.kUwMaxChainBands];
        private readonly BandTraffic[] m_PrevPrim = new BandTraffic[RRWConst.kUwMaxWindows];
        private bool m_Built, m_Same, m_PrevDerived, m_EveryAllAtOnce;

        public bool PreCover => (SavedFlags & UpgradeFlags.PreCover) != 0;

        // HasCar / SafeLo / SafeHi belong to the current bands.
        public bool DerivedFresh => DerivedRevision == Revision;

        public void Begin(in RoadWorksSite first)
        {
            var sch = first.Schedule;
            m_Same = m_Built && Plan == first.Plan && Schedule.N == sch.N && Schedule.SetupP8 == sch.SetupP8 && Schedule.Ends == sch.Ends;
            m_PrevBands.Clear();
            m_PrevDerived = m_Same && DerivedFresh;
            if (m_Same)
            {
                m_PrevBands.AddRange(Bands);
                for (int i = 0; i < RRWConst.kUwMaxChainBands; i++) { m_PrevLo[i] = SafeLo[i]; m_PrevHi[i] = SafeHi[i]; m_PrevCar[i] = HasCar[i]; }
                for (int w = 0; w < RRWConst.kUwMaxWindows; w++) m_PrevPrim[w] = Primitive[w];
            }
            else
            {
                AllAtOnceCleared = false;
                PreClosed = MachineSafe = RoadZones.None;
                for (int w = 0; w < RRWConst.kUwMaxWindows; w++) WindowReason[w] = StageBlockReason.None;
            }
            Plan = first.Plan;
            Schedule = sch;
            SavedFlags = first.UpFlags;
            m_EveryAllAtOnce = true;
            Mismatch = 0;
            EdgeCount = 0;
            Class = UpgradeClass.None;
            Bands.Clear();
            m_Collect.Clear();
            for (int w = 0; w < RRWConst.kUwMaxWindows; w++)
            {
                Saved[w] = BandTraffic.Undecided;
                Primitive[w] = BandTraffic.Undecided;
                Zones[w] = RoadZones.None;
            }
            for (int i = 0; i < RRWConst.kUwMaxChainBands; i++) { SafeLo[i] = 0f; SafeHi[i] = 0f; HasCar[i] = false; }
        }

        // Adds the chain-frame bands of one edge and its saved ceilings. The AllAtOnce bit may differ between edges while the
        // Director clears it edge by edge (ClearAllAtOnce(ref site)); it counts only when every edge carries it.
        public void AddEdge(in RoadWorksSite site)
        {
            EdgeCount++;
            const byte noAll = unchecked((byte)~(byte)UpgradeFlags.AllAtOnce);
            if (site.m_WindowCount != Schedule.N || site.m_SetupP8 != Schedule.SetupP8 || site.m_WinEnds != Schedule.Ends
                || (site.m_UpFlags & noAll) != ((byte)SavedFlags & noAll))
                Mismatch++;
            if ((site.UpFlags & UpgradeFlags.AllAtOnce) == 0) m_EveryAllAtOnce = false;
            if (!site.IsUpgrade) return;
            bool rev = RoadZoneMath.ChainReversed(site.m_ChainU0, site.m_ChainU1);
            int n = math.min(site.m_BandCount, RRWConst.kUwMaxBands);
            for (int i = 0; i < n; i++)
            {
                var b = UpgradePlan.ToChain(site.Band(i), rev);
                m_Collect.Add(b);
                int w = b.Window;
                if (b.Traffic != BandTraffic.Undecided && w < RRWConst.kUwMaxWindows) Saved[w] = WeakerCeiling(Saved[w], b.Traffic);
            }
        }

        // Clusters the collected bands into chain bands (window from the saved bands), the class, the primitives (the last
        // decision under the saved ceiling on a rebuild of the same project, else the saved one) and their zones.
        public void Finish()
        {
            var map = new int[m_Collect.Count];
            UpgradePlan.Union(m_Collect, Bands, map);
            var set = new bool[Bands.Count];
            for (int k = 0; k < m_Collect.Count; k++)
            {
                int c = map[k];
                if (c < 0) continue;
                var cb = Bands[c];
                int w = m_Collect[k].Window;
                if (!set[c]) { cb.Window = w; set[c] = true; }
                else if (cb.Window != w) { Mismatch++; cb.Window = math.min(cb.Window, w); }
                Bands[c] = cb;
            }
            while (Bands.Count > RRWConst.kUwMaxChainBands) { Bands.RemoveAt(Bands.Count - 1); Mismatch++; }
            var asBands = new List<UpgradeBand>(Bands.Count);
            for (int i = 0; i < Bands.Count; i++) asBands.Add(UpgradeBand.Make(Bands[i].Kind, Bands[i].Side, Bands[i].Lo, Bands[i].Hi));
            Class = UpgradePlan.ClassOf(asBands, asBands.Count);
            Revision = ++s_Revision;

            // HasCar / SafeLo / SafeHi of chain bands that did not change
            bool carried = m_PrevDerived;
            for (int i = 0; i < Bands.Count; i++)
            {
                int j = SameBand(m_PrevBands, Bands[i]);
                if (j < 0) { carried = false; continue; }
                SafeLo[i] = m_PrevLo[j]; SafeHi[i] = m_PrevHi[j]; HasCar[i] = m_PrevCar[j];
            }
            if (carried) DerivedRevision = Revision;

            // primitives and zones
            bool buildSavedOtherwise = false;
            for (int w = 0; w < RRWConst.kUwMaxWindows; w++)
            {
                var prev = m_Same ? m_PrevPrim[w] : BandTraffic.Undecided;
                Primitive[w] = prev != BandTraffic.Undecided ? PhasePlan.Ceiling(Saved[w], prev) : Saved[w];
                Zones[w] = PhasePlan.WindowZones(Primitive[w], Bands, w);
                var sv = Saved[w];
                if ((sv == BandTraffic.Half || sv == BandTraffic.Carriageway || sv == BandTraffic.Dressing) && HasBuildIn(w)) buildSavedOtherwise = true;
            }

            // AllAtOnce: saved on every edge, never cleared before, and no started build window that ran without lane drops
            AllAtOnce = m_EveryAllAtOnce && (SavedFlags & UpgradeFlags.AllAtOnce) != 0 && !AllAtOnceCleared && !buildSavedOtherwise;
            if (!AllAtOnce) PreClosed = RoadZones.None;
            m_Built = true;
            m_PrevBands.Clear();
        }

        private static int SameBand(List<ChainBand> list, in ChainBand b)
        {
            for (int j = 0; j < list.Count && j < RRWConst.kUwMaxChainBands; j++)
            {
                var o = list[j];
                if (o.Kind == b.Kind && o.Side == b.Side && o.Window == b.Window && math.abs(o.Lo - b.Lo) < 0.01f && math.abs(o.Hi - b.Hi) < 0.01f) return j;
            }
            return -1;
        }

        private bool HasBuildIn(int w)
        {
            for (int i = 0; i < Bands.Count; i++)
                if (Bands[i].Window == w && (Bands[i].Kind == BandKind.Build || Bands[i].Kind == BandKind.Rebuild)) return true;
            return false;
        }

        // The Director filled HasCar / SafeLo / SafeHi for the current bands.
        public void MarkDerived() => DerivedRevision = Revision;

        // Lane drops are no longer available for the rest of the works: AllAtOnce goes off for good (later build bands wait as
        // open road). The Director also clears the flag in every edge's tail (ClearAllAtOnce(ref site)) so it stays off after a load.
        public void ClearAllAtOnce()
        {
            AllAtOnceCleared = true;
            AllAtOnce = false;
            PreClosed = RoadZones.None;
        }

        // Clears the AllAtOnce bit of a site's tail. True when the site changed (write the component back).
        public static bool ClearAllAtOnce(ref RoadWorksSite site)
        {
            if ((site.UpFlags & UpgradeFlags.AllAtOnce) == 0) return false;
            site.UpFlags &= ~UpgradeFlags.AllAtOnce;
            return true;
        }

        // Traffic's detour verdict covers every car half in `halves` for this project revision.
        public bool DetourFor(RoadZones halves, int projectRevision) =>
            DetourUpdate != 0 && DetourRevision == projectRevision && halves != RoadZones.None && (DetourHalves & halves) == halves;

        // Car halves (CHAIN frame) window `w` closes under Half: the build bands' sides; the re-marking window the C4a works half,
        // plus the other half when the halves swap (swapOn).
        public RoadZones HalfClosesOf(int w, bool swapOn)
        {
            var z = PhasePlan.WindowZones(BandTraffic.Half, Bands, w) & RoadZones.Carriageway;
            for (int i = 0; i < Bands.Count; i++)
                if (Bands[i].Window == w && Bands[i].Kind == BandKind.Remark && swapOn) z |= RRWConst.kStagedOpenHalf;
            return z;
        }

        // The chain band a chain-frame band of an edge belongs to (the Union rule: every re-marking band joins the one re-marking
        // chain band; other kinds: same kind and side, within kUwUnionGap). -1 none.
        public int ChainIndexOf(in UpgradeBand chainFrameBand)
        {
            for (int k = 0; k < Bands.Count; k++)
            {
                var c = Bands[k];
                if (UpgradePlan.Joins(c.Kind, c.Side, c.Lo, c.Hi, chainFrameBand.Kind, chainFrameBand.Side, chainFrameBand.Lo, chainFrameBand.Hi)) return k;
            }
            return -1;
        }

        // Fills the band-derived fields of a window's primitive input (RemarkWindow, HasBuild, CarSubStrips from HasCar, BothSides).
        // The Director sets the rest (buildings, CarHalfBlock, DetourExists from DetourFor(HalfClosesOf(w, swapOn), revision),
        // corridor, policy, gates, EveryDirectionKeepsLane) and Saved = window started ? Saved[w] : Undecided.
        // False while HasCar is stale (DerivedFresh false): the Director then keeps the window's last primitive and stamps nothing.
        public bool FillWindowInput(int w, ref WindowPrimitiveInput input)
        {
            bool remark = false, build = false, car = false, left = false, right = false, middle = false;
            for (int i = 0; i < Bands.Count; i++)
            {
                var b = Bands[i];
                if (b.Window != w) continue;
                if (b.Kind == BandKind.Remark) { remark = true; continue; }
                if (b.Kind == BandKind.Remove) continue;
                build = true;
                if (i < HasCar.Length && HasCar[i]) car = true;
                if (b.Side == BandSide.Left) left = true;
                else if (b.Side == BandSide.Right) right = true;
                else middle = true;
            }
            input.RemarkWindow = remark;
            input.HasBuild = build;
            input.CarSubStrips = car;
            input.BothSides = (left && right) || middle;
            return DerivedFresh;
        }

        // The window whose lane groups are closed at progress p (setup -> 0, teardown -> last).
        public int LayoutWindowAt(float p)
        {
            UpgradePlan.At(Schedule, p, out int w, out _);
            return Schedule.N == 0 ? 0 : math.clamp(w, 0, Schedule.N - 1);
        }

        // Phase / fraction ProjectRecord.Phase / PhaseFraction show for mode H (the lead band of the current window).
        public bool LeadPhase(float p, out WorksPhase ph, out float f) => UpgradePlan.LeadPhase(Schedule, Bands, p, out ph, out f);

        // The pure view at progress p (ProjectRecord.View()).
        public UpgradeView View(float p)
        {
            UpgradePlan.At(Schedule, p, out int w, out float gw);
            var v = new UpgradeView
            {
                Class = Class,
                Window = (sbyte)math.clamp(w, -1, 127),
                WindowCount = Schedule.N,
                Gw = gw,
                P = p,
                Schedule = Schedule,
                AllAtOnce = AllAtOnce,
                PreCover = PreCover,
                PreClosed = PreClosed,
                MachineSafe = MachineSafe,
                BandCount = (byte)math.min(Bands.Count, RRWConst.kUwMaxChainBands),
            };
            for (int i = 0; i < v.BandCount; i++)
            {
                var b = Bands[i];
                v.SetBand(i, new UpgradeBandView
                {
                    Kind = b.Kind, Side = b.Side, Lo = b.Lo, Hi = b.Hi, G0 = b.G0, Window = (sbyte)b.Window,
                    SafeLo = SafeLo[i], SafeHi = SafeHi[i], HasCar = HasCar[i],
                });
                if (b.Window == w) v.BandMask |= (byte)(1 << i);
            }
            for (int k = 0; k < Schedule.N && k < 8; k++) { v.SetPrim(k, Primitive[k]); v.SetZones(k, Zones[k]); }
            int lw = v.LayoutWindow;
            if (Schedule.N > 0)
            {
                v.Traffic = Primitive[lw];
                v.Reason = WindowReason[lw];
                v.Zones = Zones[lw];
                v.NextZones = lw + 1 < Schedule.N ? Zones[lw + 1] : RoadZones.None;
            }
            // no switch known here: the layout window is the applied one (ProjectRecord.View refines it from the switch step)
            v.SetApplied(lw, v.Traffic, v.Zones | v.PreClosed);
            return v;
        }

        // Writes the primitive chosen at the start of `window` into the site's bands of that window (WinPrim bits 3-5). The Director
        // calls it for every edge of the project at every window start (not when the primitive weakens later: the saved value
        // stays the ceiling). True when the site changed.
        public static bool StampPrimitive(ref RoadWorksSite site, int window, BandTraffic t)
        {
            bool changed = false;
            int n = math.min(site.m_BandCount, RRWConst.kUwMaxBands);
            for (int i = 0; i < n; i++)
            {
                var b = site.Band(i);
                if (b.Window != window || b.Traffic == t) continue;
                b.SetTraffic(t);
                site.SetBand(i, b);
                changed = true;
            }
            return changed;
        }

        // Of two saved ceilings the one that closes less (Dressing over None when both close nothing).
        public static BandTraffic WeakerCeiling(BandTraffic a, BandTraffic b)
        {
            if (a == BandTraffic.Undecided) return b;
            if (b == BandTraffic.Undecided) return a;
            int sa = PhasePlan.ClosureStrength(a), sb = PhasePlan.ClosureStrength(b);
            if (sa != sb) return sa < sb ? a : b;
            return a == BandTraffic.Dressing || b == BandTraffic.Dressing ? BandTraffic.Dressing : a;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Plan).Append(' ').Append(Class).Append(" sched ").Append(Schedule.ToString()).Append(" flags=").Append(SavedFlags)
              .Append(AllAtOnce ? " allAtOnce" : "").Append(" edges=").Append(EdgeCount);
            if (Mismatch > 0) sb.Append(" MISMATCH=").Append(Mismatch);
            sb.Append(" bands: ").Append(UpgradePlan.Describe(Bands));
            sb.Append(" | w:");
            for (int k = 0; k < Schedule.N; k++)
            {
                sb.Append(' ').Append(k).Append('=').Append(Primitive[k]);
                if (Saved[k] != BandTraffic.Undecided) sb.Append("(saved ").Append(Saved[k]).Append(')');
                if (WindowReason[k] != StageBlockReason.None) sb.Append('[').Append(WindowReason[k]).Append(']');
                sb.Append(' ').Append(RoadZoneMath.Describe(Zones[k]));
            }
            if (CorridorPrev != 0 || CorridorNext != 0) sb.Append(" corridor ").Append(CorridorPrev).Append('/').Append(CorridorNext);
            if (AllAtOnceCleared) sb.Append(" allAtOnceCleared");
            if (!DerivedFresh) sb.Append(" derived-stale");
            if (DetourUpdate != 0) sb.Append(" detour=").Append(RoadZoneMath.Describe(DetourHalves));
            return sb.ToString();
        }
    }
}
