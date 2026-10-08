using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // What the Director knows about one window of a mode H project when it picks the traffic primitive (all edges of the
    // chain folded together: the weakest edge decides). Pure data. UpgradeRuntime.FillWindowInput sets the band fields.
    public struct WindowPrimitiveInput
    {
        public bool RemarkWindow;            // the re-marking window
        public bool HasBuild;                // build / rebuild bands in the window
        public bool CarSubStrips;            // a build / rebuild band has drive-lane, parking or bike sub-strips
        public bool BothSides;               // build bands on both sides of the direction split (or in the middle) at once
        public bool EveryDirectionKeepsLane; // on every edge each direction keeps a lane outside the window's bands
        public bool Buildings;               // an edge of the chain has buildings along it (EdgeRecord.BuildingCount > 0)
        public bool SideBuildings;           // sidewalk-only window: the band's side has buildings
        public StageBlockReason CarHalfBlock;// why a direction group cannot close (None = it can)
        public bool DetourExists;            // a directed detour exists for the closed direction
        public bool CorridorConflict;        // a linked project of the same drag has a Half or Carriageway window now
        public ClosurePolicy Policy;
        public bool DropGate;                // lane drops with blockers available (RRWGates.UpgradeDrop)
        public bool ClosedBGate;             // a direction next to buildings may close (RRWGates.ClosedB)
        public BandTraffic Saved;            // primitive saved at the window start (Undecided before it)
        public bool CutEdge;                 // an edge of the chain is a cut edge (EdgeRecord.CutEdge): the carriageway never closes
        public bool RemarkTwoWay;            // re-marking window: on every edge both directions fit on temporary lanes over the open half
        public bool ShuttleOK;               // build window: every edge keeps a lane per direction or one shared lane, and the section
                                             // can be signalled (one-lane alternating operation with portable signals)
        public bool RemarkShuttleOK;         // re-marking window: one shared lane on the open half of every edge, signals possible
        public bool PaintOK;                 // re-marking window: on every edge every direction has two lanes or more (one closes at the
                                             // painter, one keeps driving)
    }

    // One sub-strip's traffic state for the machine-safety test. Pure data.
    public struct SubStripState
    {
        public SubKind Kind;
        public bool GroupClosedReady;        // inside a group closed by Half / Carriageway / Sidewalk and drained (WorkZonesReady)
        public bool DropReady;               // a dropped lane whose blockers are registered and drained (UpgradeEdgeState.DropReady)
        public bool DropIntrusion;           // the latest drain check saw a vehicle on that lane outside the approach zone
        public bool VisualDropOnly;          // only the soft visual drop applies to this lane
        public bool InDrivewayKeepOut;       // inside a building access keep-out window
        public bool ParkingOff;              // a new parking sub-strip whose lane Traffic switched off while empty
                                             // (UpgradeEdgeState.ParkingOffAt with ParkingOffRevision == GeometryRevision).
                                             // Reported for the dev checks; MachineSafe does not count it
    }

    // Lateral pieces of a band that it owns (PhasePlan.UpgradeOwnLaterals), CHAIN frame, at most 4, sorted.
    public struct LatPieces
    {
        public int Count;
        public float2 P0, P1, P2, P3;
        public float2 this[int i] => i == 0 ? P0 : i == 1 ? P1 : i == 2 ? P2 : P3;

        public void Add(float lo, float hi)
        {
            if (hi - lo < 0.05f || Count >= 4) return;
            var p = new float2(lo, hi);
            if (Count == 0) P0 = p; else if (Count == 1) P1 = p; else if (Count == 2) P2 = p; else P3 = p;
            Count++;
        }

        // Removes [lo, hi] from every piece.
        public void Cut(float lo, float hi)
        {
            var old = this;
            this = default;
            for (int i = 0; i < old.Count; i++)
            {
                var p = old[i];
                if (p.y <= lo || p.x >= hi) { Add(p.x, p.y); continue; }
                if (p.x < lo) Add(p.x, lo);
                if (p.y > hi) Add(hi, p.y);
            }
        }
    }

    // Mode H (upgrade works) rules of the phase plan. Pure, job-safe (no allocation). Every function here is only reached for a
    // mode H view (ProjectView.IsUpgrade) or is a mode H API of its own; the shared PhasePlan functions call them first and keep
    // their rules for every other mode.
    //
    // Fronts of a band run over the trimmed chain [Trim0, Trim1] with the band's own equivalent phase (UpgradePlan.Equivalent of
    // UpgradeView.BandG); the re-marking band under Half uses the C4a / C4b fronts of a new road over [0, U].
    public static partial class PhasePlan
    {
        // Modes whose crews bring machines: the release waits for them to leave the zones being opened.
        public static bool HasMachines(VisualMode mode) => mode == VisualMode.FullDig || mode == VisualMode.HalfWidth;

        // How much a primitive closes (0 = nothing for cars or pedestrians).
        public static int ClosureStrength(BandTraffic t)
        {
            switch (t)
            {
                case BandTraffic.Sidewalk: return 1;
                case BandTraffic.Drop: return 2;
                case BandTraffic.Paint: return 2;
                case BandTraffic.Half: return 3;
                case BandTraffic.Carriageway: return 4;
                default: return 0;
            }
        }

        // A primitive that controls lane groups or lanes (the project target is Closed).
        public static bool Closes(BandTraffic t) => ClosureStrength(t) > 0;

        // A primitive that closes single lanes with blockers (Traffic's lane drops): Drop over the window's bands, Paint around the
        // painter.
        public static bool LaneDrops(BandTraffic t) => t == BandTraffic.Drop || t == BandTraffic.Paint;

        // Rolling closure of a Paint window (chain u): kUwPaintBehind behind the re-marking band's front to kUwPaintAhead ahead of it,
        // inside the trimmed chain. False when the applied window is not a re-marking window running Paint.
        public static bool PaintZone(in ProjectView v, out float u0, out float u1)
        {
            u0 = u1 = 0f;
            var u = v.Upgrade;
            if (!u.Valid || u.InSetup || u.InTeardown || u.AppliedTraffic != BandTraffic.Paint) return false;
            int band = -1;
            for (int i = 0; i < u.BandCount; i++)
                if (u.Band(i).Kind == BandKind.Remark && u.Band(i).Window == u.AppliedWindow) band = i;
            if (band < 0) return false;
            Trims(v, out float t0, out float t1);
            float F = UpgradeBandFront(v, band);
            u0 = math.max(t0, F - RRWConst.kUwPaintBehind);
            u1 = math.min(t1, F + RRWConst.kUwPaintAhead);
            return u1 - u0 > 1f;
        }

        // A window whose primitive the Director has not decided yet (Traffic's classification, the detour verdict or the band
        // data still missing): slow zone, no lane group closed, no machine anywhere, the devices of Dressing, and its strips
        // covered like those of a window that closes (UpgradeSpans), so a closure decided a few updates later finds them covered.
        public static bool UpgradeUndecided(BandTraffic t) => t == BandTraffic.Undecided;

        // Slow zone with devices only: Dressing, or a window not decided yet.
        public static bool UpgradeDressed(BandTraffic t) => t == BandTraffic.Dressing || t == BandTraffic.Undecided;

        private static readonly BandTraffic[] s_CarLadder = { BandTraffic.Drop, BandTraffic.Half, BandTraffic.Carriageway };
        private static readonly BandTraffic[] s_RemarkLadder = { BandTraffic.Paint, BandTraffic.Half, BandTraffic.Carriageway };

        // Traffic primitive of a window, first rung that applies:
        //  * policy slow zone / visual only -> Dressing (the caller drops the slow zone under visual only);
        //  * remove bands only -> None (cars unaffected, the new sidewalk next to it stays open behind a fence);
        //  * only sidewalk / verge sub-strips -> Sidewalk when that side has no buildings, else None (machines on verge and
        //    terrain only);
        //  * build bands with car sub-strips: Drop (blockers available, every direction keeps a lane, and no buildings: next to
        //    houses cars reach a dropped lane from parking and driveways) -> Half (one side, a direction group can close, a
        //    detour exists, no corridor conflict, and no buildings or the buildings gate) -> Carriageway (no buildings, no cut
        //    edge) -> Dressing;
        //  * re-marking: Paint (every direction keeps a lane beside the painter: rolling blockers around it, no detour) -> Half ->
        //    Carriageway -> Dressing.
        // After the window started its saved primitive is a ceiling: it is kept while it still applies, otherwise only a rung
        // that closes less may replace it (Dressing at worst), never one that closes more.
        // why: the reason for Dressing (None otherwise).
        public static BandTraffic WindowPrimitive(in WindowPrimitiveInput w, out StageBlockReason why)
        {
            why = StageBlockReason.None;
            if (w.Policy != ClosurePolicy.Realistic) return BandTraffic.Dressing;
            if (!w.RemarkWindow && !w.HasBuild) return BandTraffic.None;
            if (!w.RemarkWindow && !w.CarSubStrips)
            {
                var side = w.SideBuildings ? BandTraffic.None : BandTraffic.Sidewalk;
                return Ceiling(w.Saved, side);
            }
            var ladder = w.RemarkWindow ? s_RemarkLadder : s_CarLadder;
            bool ceiling = w.Saved != BandTraffic.Undecided;
            if (ceiling && Applies(w, w.Saved)) return w.Saved;
            for (int i = 0; i < ladder.Length; i++)
            {
                var r = ladder[i];
                if (ceiling && ClosureStrength(r) >= ClosureStrength(w.Saved)) continue;
                if (Applies(w, r)) return r;
            }
            why = w.Buildings ? StageBlockReason.Houses
                : w.CarHalfBlock != StageBlockReason.None ? w.CarHalfBlock
                : !w.DetourExists ? StageBlockReason.Detour
                : StageBlockReason.Drop;
            return BandTraffic.Dressing;
        }

        private static bool Applies(in WindowPrimitiveInput w, BandTraffic r)
        {
            switch (r)
            {
                case BandTraffic.Drop:
                    return !w.RemarkWindow && w.DropGate && (w.EveryDirectionKeepsLane || w.ShuttleOK) && !w.Buildings;
                case BandTraffic.Paint:
                    return w.RemarkWindow && w.DropGate && w.PaintOK && !w.Buildings;
                case BandTraffic.Half:
                    // re-marking with temporary lanes: both directions keep driving on the open half, no detour needed
                    if (w.RemarkWindow && (w.RemarkTwoWay || w.RemarkShuttleOK) && !w.CorridorConflict && (!w.Buildings || w.ClosedBGate)) return true;
                    return (w.RemarkWindow || !w.BothSides) && w.CarHalfBlock == StageBlockReason.None && w.DetourExists
                           && !w.CorridorConflict && (!w.Buildings || w.ClosedBGate);
                case BandTraffic.Carriageway:
                    return !w.Buildings && !w.CutEdge;
                case BandTraffic.Sidewalk:
                    return !w.SideBuildings;
                case BandTraffic.None:
                case BandTraffic.Dressing:
                    return true;
                default:
                    return false;
            }
        }

        // A primitive wanted now against the saved ceiling: never one that closes more than the saved one.
        public static BandTraffic Ceiling(BandTraffic saved, BandTraffic wanted)
        {
            if (saved == BandTraffic.Undecided || wanted == saved) return wanted;
            if (ClosureStrength(wanted) <= ClosureStrength(saved)) return wanted;
            return saved == BandTraffic.None ? BandTraffic.None : BandTraffic.Dressing;
        }

        // May a machine stand in this sub-strip now? Verge and terrain (no lane): yes, outside driveway keep-outs. A lane only
        // when its group is closed and drained, or when it is a dropped lane with registered, drained blockers and no intrusion
        // in the latest check. A lane under the soft visual drop never. An empty new parking lane that Traffic switched off
        // (ParkingOff) counts too, outside driveway keep-outs. stationary: keep-outs only stop standing machines.
        public static bool MachineSafe(in SubStripState s, bool stationary)
        {
            bool keepOut = stationary && s.InDrivewayKeepOut;
            if (s.Kind == SubKind.Verge || s.Kind == SubKind.Terrain) return !keepOut;
            if (s.VisualDropOnly) return false;
            if (s.GroupClosedReady) return !keepOut;
            if (s.DropReady && !s.DropIntrusion) return !keepOut;
            // an empty new parking lane that Traffic switched off: nobody parks or drives there while it is off
            if (s.Kind == SubKind.NewParking && s.ParkingOff) return !keepOut;
            return false;
        }

        // ------------------------------------------------------------------ windows: lane groups, machine zones

        // Lane groups (CHAIN frame) a window closes with its primitive:
        //  * Carriageway: both halves and the parking of both sides (sidewalks stay open);
        //  * Half: the re-marking window -> the C4a works half (kStagedWorksHalf) and its parking; a build window -> the half of
        //    the bands' side and its parking (a band in the middle closes both);
        //  * Sidewalk: the sidewalk group of the bands' side;
        //  * Drop, None, Dressing: no group (lane drops close single lanes with blockers).
        public static RoadZones WindowZones(BandTraffic prim, System.Collections.Generic.IList<ChainBand> bands, int window)
        {
            if (prim == BandTraffic.Carriageway) return RoadZones.Carriageway | RoadZones.Parking;
            if (prim != BandTraffic.Half && prim != BandTraffic.Sidewalk) return RoadZones.None;
            var z = RoadZones.None;
            if (bands == null) return z;
            for (int i = 0; i < bands.Count; i++)
            {
                var b = bands[i];
                if (b.Window != window || b.Kind == BandKind.Remove) continue;
                z |= SideZones(prim, b.Kind, b.Side);
            }
            return z;
        }

        // Same from a view (any window of it).
        public static RoadZones WindowZones(BandTraffic prim, in UpgradeView u, int window)
        {
            if (prim == BandTraffic.Carriageway) return RoadZones.Carriageway | RoadZones.Parking;
            if (prim != BandTraffic.Half && prim != BandTraffic.Sidewalk) return RoadZones.None;
            var z = RoadZones.None;
            for (int i = 0; i < u.BandCount; i++)
            {
                var b = u.Band(i);
                if (b.Window != window || b.Kind == BandKind.Remove) continue;
                z |= SideZones(prim, b.Kind, b.Side);
            }
            return z;
        }

        private static RoadZones SideZones(BandTraffic prim, BandKind kind, BandSide side)
        {
            if (prim == BandTraffic.Half)
            {
                if (kind == BandKind.Remark) return RRWConst.kStagedWorksHalf | RoadZoneMath.ParkingOf(RRWConst.kStagedWorksHalf);
                if (side == BandSide.Left) return RoadZones.LeftHalf | RoadZones.ParkingLeft;
                if (side == BandSide.Right) return RoadZones.RightHalf | RoadZones.ParkingRight;
                return RoadZones.Carriageway | RoadZones.Parking;
            }
            if (kind == BandKind.Remark) return RoadZones.None;
            if (side == BandSide.Left) return RoadZones.SidewalkLeft;
            if (side == BandSide.Right) return RoadZones.SidewalkRight;
            return RoadZones.None;
        }

        // Parking groups that must not be open now (the staged-traffic checks of rrw.check and the Director): FullDig and Minimal
        // never open a parking group during works; upgrade works keep parking open by design (next to houses, under Dressing, lane
        // drops, sidewalk works, the side a one-sided closure leaves open) and close only the parking of the window applied now
        // (Half, Carriageway). A mode H project without upgrade data closes nothing (slow zone).
        public static RoadZones ParkingKeptClosed(in ProjectView v)
        {
            if (v.Mode != VisualMode.HalfWidth) return RoadZones.Parking;
            return v.IsUpgrade ? v.Upgrade.AppliedZones & RoadZones.Parking : RoadZones.None;
        }

        // Zones machines may stand in now (UpgradeRuntime.MachineSafe -> UpgradeView.MachineSafe): the layout window's closed
        // groups that are drained (WorkZonesReady), plus the verge / terrain bits. Dropped lanes are not a zone: Machines test them
        // per sub-strip (MachineSafe with SubStripState.DropReady = UpgradeEdgeState.DropReady).
        public static RoadZones UpgradeSafeZones(in UpgradeView u, RoadZones workZonesReady) =>
            (u.Zones & workZonesReady) | RoadZones.VergeLeft | RoadZones.VergeRight;

        // Same from the stage the crews work in now (Stage(v).Works): during the Vacate step of a window start the previous
        // window's groups, after the half swap of the re-marking window the other half. The Director writes
        // UpgradeRuntime.MachineSafe from this overload.
        public static RoadZones UpgradeSafeZones(in ProjectView v, RoadZones workZonesReady)
        {
            if (!v.IsUpgrade) return RoadZones.VergeLeft | RoadZones.VergeRight;
            return (Stage(v).Works & RoadZones.AllLanes & workZonesReady) | RoadZones.VergeLeft | RoadZones.VergeRight;
        }

        // ------------------------------------------------------------------ the layout applied now

        // The window whose lane groups are applied (closed) now: the layout window, except during the Vacate step of a window
        // start, which keeps the previous window's groups while its machines leave (the re-marking window only before its
        // half swap point). Stage uses the same rule.
        public static int UpgradeAppliedWindow(in UpgradeView u, StageSwitch sw)
        {
            int lw = u.LayoutWindow;
            int w = u.Window;
            bool atStart = !u.RemarkWindow(lw) || u.Gw < 0.5f * RRWConst.kC4SwapF;
            if (sw == StageSwitch.Vacate && w >= 1 && w < u.WindowCount && atStart) return w - 1;
            return lw;
        }

        // Fills UpgradeView.AppliedWindow / AppliedTraffic / AppliedZones from the view's switch step and stage context
        // (ProjectRecord.View calls it; a view built otherwise carries the layout window's values from UpgradeRuntime.View).
        public static void FillUpgradeApplied(ref ProjectView v)
        {
            if (!v.IsUpgrade) return;
            var u = v.Upgrade;
            int aw = UpgradeAppliedWindow(u, v.Switch);
            var t = u.Prim(aw);
            u.SetApplied(aw, t, u.ZonesOf(aw));
            v.Upgrade = u;
            u.SetApplied(aw, t, Stage(v).Works);
            v.Upgrade = u;
        }

        // ------------------------------------------------------------------ fronts

        private static void Trims(in ProjectView v, out float t0, out float t1)
        {
            t0 = v.Trim0;
            t1 = v.Trim1 > t0 ? v.Trim1 : math.max(t0, v.U);
        }

        // Equivalent phase of chain band `band` now (its own progress g = UpgradeView.BandG). False for a bad index.
        public static bool UpgradeBandPhase(in ProjectView v, int band, out WorksPhase ph, out float f, out float g)
        {
            ph = WorksPhase.Survey; f = 0f; g = 0f;
            var u = v.Upgrade;
            if (band < 0 || band >= u.BandCount) return false;
            var b = u.Band(band);
            g = u.BandG(band);
            UpgradePlan.Equivalent(b.Kind, u.RemarkFollows(b.Window), g, out ph, out f);
            return true;
        }

        // Front (chain u, unquantised) of chain band `band`: Trim0 + the equivalent phase's sweep over the trimmed chain; the
        // re-marking band of a Half window uses the C4a / C4b painter fronts over [0, U]. Before its window: at its start progress
        // (G0, normally Trim0); after it: Trim1.
        public static float UpgradeBandFront(in ProjectView v, int band)
        {
            Trims(v, out float t0, out float t1);
            if (!UpgradeBandPhase(v, band, out var ph, out float f, out float g)) return t0;
            var u = v.Upgrade;
            var b = u.Band(band);
            float L = t1 - t0;
            if (b.Kind == BandKind.Remark)
            {
                if (b.Window == u.LayoutWindow && v.HalvesActive)
                {
                    var t = v;
                    t.Phase = WorksPhase.Finishing;
                    t.F = g;
                    return C4Front(t);
                }
                return t0 + MainFront(WorksPhase.Finishing, g, L);
            }
            return t0 + MainFront(ph, f, L);
        }

        // Main front of a mode H view: the lead (widest) band of the layout window.
        public static float UpgradeMainFront(in ProjectView v)
        {
            int lead = v.Upgrade.LeadBand(v.Upgrade.LayoutWindow);
            return lead < 0 ? v.Trim0 : UpgradeBandFront(v, lead);
        }

        // The C4 painter front of a view in Finishing (the swap sweeps, the front floor without the swap).
        private static float C4Front(in ProjectView v)
        {
            if (!v.SwapActive) return math.max(MainFront(v.Phase, v.F, v.U), math.clamp(v.Ctx.FrontFloor, 0f, v.U));
            return StageIndexOf(v) == 0 ? v.U * Sweep(v.F, RRWConst.kC4aPaintF0, RRWConst.kC4aPaintF1)
                                        : v.U * Sweep(v.F, RRWConst.kC4bPaintF0, RRWConst.kC4bPaintF1);
        }

        // Job form of a band front (Machines' front reference at a render-time p): the band's equivalent phase over the trimmed
        // chain. g = UpgradePlan.BandG(G0, gw) with UpgradePlan.At(schedule, p, ...) for the band's window (1 after it, G0 before
        // it). The re-marking band under Half uses MainFront(Finishing, g, U, c4Swap, floor) instead (the C4a / C4b sweeps).
        public static float UpgradeFrontOf(BandKind kind, bool remarkFollows, float g, float trim0, float trim1)
        {
            float L = math.max(0f, trim1 - trim0);
            if (kind == BandKind.Remark) return trim0 + MainFront(WorksPhase.Finishing, g, L);
            UpgradePlan.Equivalent(kind, remarkFollows, g, out var ph, out float f);
            return trim0 + MainFront(ph, f, L);
        }

        // Share of a band's own progress its equivalent phase takes (the ranges of UpgradePlan.Equivalent).
        public static float UpgradePhaseShare(BandKind kind, bool remarkFollows, WorksPhase ph)
        {
            switch (kind)
            {
                case BandKind.Remark: return 1f;
                case BandKind.Remove:
                    return ph == WorksPhase.BreakUp ? 0.35f : ph == WorksPhase.Removal ? 0.40f : ph == WorksPhase.Restore ? 0.25f : 0f;
                default:
                    if (remarkFollows) return ph == WorksPhase.Excavation ? 0.35f : ph == WorksPhase.Foundation ? 0.30f : ph == WorksPhase.Paving ? 0.35f : 0f;
                    return ph == WorksPhase.Excavation ? 0.30f : ph == WorksPhase.Foundation ? 0.25f : ph == WorksPhase.Paving ? 0.27f
                         : ph == WorksPhase.Finishing ? 0.18f : 0f;
            }
        }

        // Range [gLo, gHi) of a band's own progress g its equivalent phase covers (the order and shares of UpgradePlan.Equivalent:
        // build / rebuild Excavation, Foundation, Paving (+ Finishing without a re-marking window after it); remove BreakUp,
        // Removal, Restore; re-marking Finishing over [0, 1]). False (0, 0) for a phase the kind does not run.
        public static bool UpgradePhaseRange(BandKind kind, bool remarkFollows, WorksPhase ph, out float gLo, out float gHi)
        {
            gLo = gHi = 0f;
            float share = UpgradePhaseShare(kind, remarkFollows, ph);
            if (!(share > 0f)) return false;
            if (kind == BandKind.Remark) { gHi = 1f; return true; }
            for (int i = 0; i < 4; i++)
            {
                var p = kind == BandKind.Remove ? (i == 0 ? WorksPhase.BreakUp : i == 1 ? WorksPhase.Removal : i == 2 ? WorksPhase.Restore : WorksPhase.None)
                                                : (i == 0 ? WorksPhase.Excavation : i == 1 ? WorksPhase.Foundation : i == 2 ? WorksPhase.Paving : WorksPhase.Finishing);
                if (p == ph) break;
                gLo += UpgradePhaseShare(kind, remarkFollows, p);
            }
            gHi = math.min(1f, gLo + share);
            return true;
        }

        // Machine seconds chain band `band`'s current equivalent phase takes from start to end at the window's rate (0 = unknown):
        // the band's g runs from G0 to 1 over its window, so a phase that is `share` of g takes share / (1 - G0) of the window:
        // WorkSeconds x window length x share / (1 - G0).
        public static float UpgradePhaseSeconds(in ProjectView v, int band)
        {
            var u = v.Upgrade;
            if (!(v.WorkSeconds > 0f) || band < 0 || band >= u.BandCount) return 0f;
            var b = u.Band(band);
            if (b.Window < 0 || b.Window >= u.WindowCount) return 0f;
            UpgradeBandPhase(v, band, out var ph, out _, out _);
            float win = u.Schedule.P1(b.Window) - u.Schedule.P0(b.Window);
            return v.WorkSeconds * win * UpgradePhaseShare(b.Kind, u.RemarkFollows(b.Window), ph) / math.max(1e-3f, 1f - math.saturate(b.G0));
        }

        // Front speed of the lead band's crew (m/s machine time; 0 = unknown): the trimmed chain over the sweep share of its phase.
        // The crews of a window work side by side, so it is not divided by the crew count.
        private static float UpgradeCrewFrontSpeed(in ProjectView v)
        {
            int lead = v.Upgrade.LeadBand(v.Upgrade.LayoutWindow);
            if (lead < 0) return 0f;
            float ps = UpgradePhaseSeconds(v, lead);
            UpgradeBandPhase(v, lead, out var ph, out _, out _);
            float w = FrontSweepFraction(ph, ph == WorksPhase.Finishing && v.SwapActive);
            Trims(v, out float t0, out float t1);
            float L = v.Upgrade.Band(lead).Kind == BandKind.Remark && v.HalvesActive ? v.U : t1 - t0;
            return ps > 0f && w > 0f && L > 0f ? L / (w * ps) : 0f;
        }

        // The dump-heap slot crew `crew` tips at: the first slot at least 6 m ahead of its band's front (no sections). NaN = none.
        private static float UpgradeDumpSlot(in ProjectView v, int crew)
        {
            int band = v.Upgrade.CrewBand(crew);
            if (band < 0) return float.NaN;
            float F = UpgradeBandFront(v, band);
            SlotLayout(PropGroup.DumpHeaps, out float off, out float sp, out float _);
            int n = SlotCount(PropGroup.DumpHeaps, v);
            float first = v.Trim0 + off;
            int k = math.max(0, (int)math.ceil((F + 6f - first) / sp));
            return k >= n ? float.NaN : first + sp * k;
        }

        // A band front quantised for decals inside [t0, t1].
        private static float QuantizeBand(float F, float t0, float t1)
        {
            if (F >= t1 - 1e-3f) return t1;
            if (F <= t0) return t0;
            return t0 + QuantizeFront(F - t0, t1 - t0);
        }

        // ------------------------------------------------------------------ surfaces

        // Band kind of a layer for a mode H view: the asphalt layers of the re-marking band under Half use the C4 per-half bands
        // (CarriageHalf, spans from SurfaceSpansHalf, exactly as for a new road); TempMarking the yellow lines; every other layer is
        // drawn per sub-strip of each band (WorksBand, spans from UpgradeSpans).
        private static SurfaceBand UpgradeBandOf(SurfaceLayer l, in ProjectView v)
        {
            if (l == SurfaceLayer.TempMarking) return SurfaceBand.TempLines;
            if ((l == SurfaceLayer.FreshAsphalt || l == SurfaceLayer.FreshAsphaltCover || l == SurfaceLayer.OldAsphaltCover) && v.HalvesActive && RRWGates.HalfCovers)
                return SurfaceBand.CarriageHalf;
            return SurfaceBand.WorksBand;
        }

        // How a sub-strip of a band may be dressed now. closed: the sub-strip carries no traffic now (UpgradeZones.SubStripClosed:
        // its zone is closed now and Traffic applied that closure, or it holds a dropped lane with registered blockers);
        // parkingOff: a new parking lane in the sub-strip is really switched off (UpgradeZones.SubStripParkingOff: Traffic
        // disabled it while it was empty). A new parking lane that kept a car stays Traffic.
        public static SubStripCover CoverOf(in SubStrip s, BandKind kind, bool closed, bool parkingOff)
        {
            if (kind == BandKind.Remove) return s.Kind == SubKind.Terrain ? SubStripCover.Terrain : SubStripCover.Inside;
            switch (s.Kind)
            {
                case SubKind.DriveLane:
                case SubKind.Bike:
                    return closed ? SubStripCover.Works : SubStripCover.Traffic;
                case SubKind.NewParking:
                    return closed || parkingOff ? SubStripCover.Works : SubStripCover.Traffic;
                case SubKind.Sidewalk:
                    return closed ? SubStripCover.WorksWalk : SubStripCover.OpenWalk;
                default:
                    return SubStripCover.WorksWalk;
            }
        }

        // Chain span of a layer on the sub-strips of band `band` that have cover `cover` (one piece; empty = none). Laterals are the
        // Surfaces module's: it intersects the sub-strips (and for the re-marking band UpgradeOwnLaterals) with the band.
        //  Build / Rebuild (front F of the band, equivalent phase):
        //    before its window   PreCover, and only when the band's window closes something, is not decided yet (Undecided) or
        //                        AllAtOnce drops it now: Fresh Asphalt Cover over lane-type sub-strips, Base Course Cover over
        //                        closed walks / verges, nothing on an open sidewalk (pedestrians use it until its window)
        //    Works      Excavation: Road Dirt [0, F] (+ Fresh Asphalt Cover ahead with PreCover); Foundation: Base Course Cover
        //               [0, F], Road Dirt ahead; Paving: Fresh Asphalt + Cover [0, F], Base Course Cover ahead; Finishing: Fresh
        //               Asphalt + Cover ahead (shrinking behind the painter)
        //    Traffic    Fresh Asphalt Cover over the whole band (cars drive on it; never dirt or gravel)
        //    OpenWalk   Base Course Cover until the paver passed (Excavation, Foundation: all; Paving: ahead)
        //    WorksWalk  Road Dirt [0, F] (Excavation); Base Course Cover [0, F] + Road Dirt ahead (Foundation); Base Course Cover ahead
        //               (Paving)
        //    after its window    nothing (the re-marking band, or the finished road)
        //  Remove:
        //    Terrain    before / BreakUp: Old Asphalt (RRWGates.UpgradeOldAsphalt) ahead + Base Course Cover behind, or Base Course
        //               Cover everywhere; Removal: Subgrade + Base Course Cover ahead; Restore: Subgrade ahead + Topsoil behind;
        //               after: Topsoil (a scar at completion)
        //    Inside     Base Course Cover before and in BreakUp / Removal, ahead of the front in Restore, then nothing
        //  Remark: under Half nothing here (SurfaceSpansHalf draws it); under Carriageway (or not decided yet) Fresh Asphalt Cover
        //    ahead of the painter; before its window Fresh Asphalt Cover over the whole band with PreCover when that window closes
        //    something or is not decided yet; Dressing: nothing (the new markings show).
        public static void UpgradeSpans(SurfaceLayer layer, in ProjectView v, int band, SubStripCover cover, out SpanSet set)
        {
            set = default;
            if (!v.IsUpgrade || layer == SurfaceLayer.TempMarking) return;
            var u = v.Upgrade;
            if (band < 0 || band >= u.BandCount) return;
            Trims(v, out float t0, out float t1);
            if (!(t1 - t0 > 1e-3f)) return;
            var b = u.Band(band);
            int w = v.Phase == WorksPhase.Complete ? u.WindowCount : u.Window;
            bool before = w < b.Window, after = w > b.Window;
            UpgradeBandPhase(v, band, out var ph, out float f, out float g);
            float F = QuantizeBand(UpgradeBandFront(v, band), t0, t1);
            var full = new Span(t0, t1);
            var done = new Span(t0, F);
            var ahead = new Span(F, t1);
            bool pre = u.PreCover;
            switch (b.Kind)
            {
                case BandKind.Remark:
                {
                    if (after) return;
                    var prim = u.Prim(b.Window);
                    bool covers = prim == BandTraffic.Half || prim == BandTraffic.Carriageway || UpgradeUndecided(prim) || prim == BandTraffic.Drop
                                  || prim == BandTraffic.Paint;
                    if (before)
                    {
                        // the old road (its own laterals leave out the new strips built already) carries traffic until its window
                        if (pre && covers && layer == SurfaceLayer.OldAsphaltCover) set.Add(full);
                        return;
                    }
                    // painting: the road ahead of the painter carries no markings yet (the old ones are ground off): the plain road
                    // surface; behind it the new markings show
                    if (v.HalvesActive || layer != SurfaceLayer.OldAsphaltCover) return;
                    if (covers) set.Add(ahead);
                    return;
                }
                case BandKind.Remove:
                {
                    var old = RRWGates.UpgradeOldAsphalt ? SurfaceLayer.OldAsphalt : SurfaceLayer.BaseCourseCover;
                    if (cover == SubStripCover.Terrain)
                    {
                        if (after) { if (layer == SurfaceLayer.TopsoilStrip) set.Add(full); return; }
                        if (before) { if (pre && layer == old) set.Add(full); return; }
                        switch (ph)
                        {
                            case WorksPhase.BreakUp:
                                if (old == SurfaceLayer.OldAsphalt)
                                {
                                    if (layer == SurfaceLayer.OldAsphalt) set.Add(ahead);
                                    else if (layer == SurfaceLayer.BaseCourseCover) set.Add(done);
                                }
                                else if (layer == SurfaceLayer.BaseCourseCover) set.Add(full);
                                return;
                            case WorksPhase.Removal:
                                if (layer == SurfaceLayer.Subgrade) set.Add(full);
                                else if (layer == SurfaceLayer.BaseCourseCover) set.Add(ahead);
                                return;
                            default:
                                if (layer == SurfaceLayer.Subgrade) set.Add(ahead);
                                else if (layer == SurfaceLayer.TopsoilStrip) set.Add(new Span(t0, math.min(t1, F + RRWConst.kSpanOverlap)));
                                return;
                        }
                    }
                    if (layer != SurfaceLayer.BaseCourseCover || after) return;
                    if (before) { if (pre) set.Add(full); return; }
                    set.Add(ph == WorksPhase.Restore ? ahead : full);
                    return;
                }
            }
            // build / rebuild
            bool walk = cover == SubStripCover.OpenWalk || cover == SubStripCover.WorksWalk || cover == SubStripCover.Inside
                        || cover == SubStripCover.Terrain;
            if (after)
            {
                // the new strip is paved: fresh asphalt (its markings stay covered) until the re-marking window, then until the
                // painter passed
                if (layer != SurfaceLayer.FreshAsphaltCover || walk) return;
                if (RemarkPending(u, w)) { set.Add(full); return; }
                int rb = RemarkBandIn(u, w);
                if (rb >= 0) set.Add(new Span(QuantizeBand(UpgradeBandFront(v, rb), t0, t1), t1));
                return;
            }
            // what the strip was before it is dug: the ground (a build band: grass, also under the new sidewalk) or the old
            // road / pavement (a rebuild band)
            var untouched = b.Kind == BandKind.Build ? SurfaceLayer.GroundCover : SurfaceLayer.OldAsphaltCover;
            if (before)
            {
                var wp = u.Prim(b.Window);
                if (!pre || !(u.AllAtOnce || Closes(wp) || UpgradeUndecided(wp)) || cover == SubStripCover.OpenWalk) return;
                if (!walk && layer == untouched) set.Add(full);
                else if (walk && layer == (b.Kind == BandKind.Build ? SurfaceLayer.GroundCover : SurfaceLayer.BaseCourseCover)) set.Add(full);
                return;
            }
            switch (cover)
            {
                case SubStripCover.Traffic:
                    if (layer == SurfaceLayer.OldAsphaltCover && !u.Deferred) set.Add(full);
                    return;
                case SubStripCover.OpenWalk:
                    if (layer != SurfaceLayer.BaseCourseCover) return;
                    if (ph == WorksPhase.Excavation || ph == WorksPhase.Foundation) set.Add(full);
                    else if (ph == WorksPhase.Paving) set.Add(ahead);
                    return;
                case SubStripCover.Works:
                    switch (ph)
                    {
                        case WorksPhase.Excavation:
                            if (layer == SurfaceLayer.RoadDirt) set.Add(done);
                            else if (layer == untouched && pre) set.Add(ahead);
                            return;
                        case WorksPhase.Foundation:
                            if (layer == SurfaceLayer.BaseCourseCover) set.Add(done);
                            else if (layer == SurfaceLayer.RoadDirt) set.Add(ahead);
                            return;
                        case WorksPhase.Paving:
                            if (layer == SurfaceLayer.FreshAsphalt || layer == SurfaceLayer.FreshAsphaltCover) set.Add(done);
                            else if (layer == SurfaceLayer.BaseCourseCover) set.Add(ahead);
                            return;
                        default:
                            if (layer == SurfaceLayer.FreshAsphalt || layer == SurfaceLayer.FreshAsphaltCover) set.Add(ahead);
                            return;
                    }
                default:   // a closed walk, a verge, a median
                    switch (ph)
                    {
                        case WorksPhase.Excavation:
                            if (layer == SurfaceLayer.RoadDirt) set.Add(done);
                            else if (layer == SurfaceLayer.GroundCover && b.Kind == BandKind.Build && pre) set.Add(ahead);
                            return;
                        case WorksPhase.Foundation:
                            if (layer == SurfaceLayer.BaseCourseCover) set.Add(done);
                            else if (layer == SurfaceLayer.RoadDirt) set.Add(ahead);
                            return;
                        case WorksPhase.Paving:
                            if (layer == SurfaceLayer.BaseCourseCover) set.Add(ahead);
                            return;
                        default:
                            return;
                    }
            }
        }

        // The re-marking band whose window is w (-1 none).
        private static int RemarkBandIn(in UpgradeView u, int w)
        {
            for (int i = 0; i < u.BandCount; i++)
                if (u.Band(i).Kind == BandKind.Remark && u.Band(i).Window == w) return i;
            return -1;
        }

        // A re-marking band's window is still to come at window w.
        private static bool RemarkPending(in UpgradeView u, int w)
        {
            for (int i = 0; i < u.BandCount; i++)
                if (u.Band(i).Kind == BandKind.Remark && u.Band(i).Window > w) return true;
            return false;
        }

        // The lateral pieces (CHAIN frame) band `band` owns: its range minus every build / rebuild / remove band that is not done
        // yet (those dress their own strip; the re-marking covers only what they left or finished).
        public static LatPieces UpgradeOwnLaterals(in ProjectView v, int band)
        {
            var p = new LatPieces();
            var u = v.Upgrade;
            if (!v.IsUpgrade || band < 0 || band >= u.BandCount) return p;
            var b = u.Band(band);
            p.Add(b.Lo, b.Hi);
            if (b.Kind != BandKind.Remark) return p;
            for (int i = 0; i < u.BandCount; i++)
            {
                if (i == band) continue;
                var o = u.Band(i);
                if (o.Kind == BandKind.Remark) continue;
                // not done: the band dresses its strip; a done build / rebuild band shows its fresh asphalt until the painter passed
                // (UpgradeSpans); a done remove band lies outside the new road
                if (u.StateOf(i) == BandState.Done && o.Kind == BandKind.Remove) continue;
                p.Cut(o.Lo, o.Hi);
            }
            return p;
        }

        // Yellow temporary lines of a mode H view: only the re-marking window under Half (exactly the C4a rule of a new road).
        private static Span UpgradeTempMarking(in ProjectView v)
        {
            if (!v.HalvesActive || v.Phase != WorksPhase.Finishing || (v.OpenLanes & RRWConst.kStagedOpenHalf) == 0) return Span.Empty;
            if (v.SwapActive && StageIndexOf(v) != 0) return Span.Empty;
            float U = v.U;
            float t0 = math.max(0f, v.Trim0), t1 = math.min(U, v.Trim1 > 0f ? v.Trim1 : U);
            if (!v.SwapActive) t0 = math.max(t0, TapeFront(v));
            return new Span(t0, t1);
        }

        // SurfaceSpans of a mode H view: the yellow lines, or the union over the bands (each with its default cover: Terrain for
        // remove bands, Works otherwise). For logs and dev output; Surfaces draws per band and sub-strip with UpgradeSpans.
        private static void UpgradeSpansAll(SurfaceLayer layer, in ProjectView v, out SpanSet set)
        {
            set = default;
            if (layer == SurfaceLayer.TempMarking) { set.Add(UpgradeTempMarking(v)); return; }
            var u = v.Upgrade;
            for (int i = 0; i < u.BandCount; i++)
            {
                UpgradeSpans(layer, v, i, u.Band(i).Kind == BandKind.Remove ? SubStripCover.Terrain : SubStripCover.Works, out SpanSet one);
                set.Union(one);
            }
        }

        private static Span UpgradeSpanHull(SurfaceLayer layer, in ProjectView v)
        {
            UpgradeSpansAll(layer, v, out SpanSet set);
            return set.Hull;
        }

        // ------------------------------------------------------------------ props

        // Device plan of a mode H view. Half / Carriageway windows (and the re-marking under Half) use the staged layout of a new
        // road (closed-end lines across the works half once it is drained, divider cones, signs, kerb fences where a sidewalk stays
        // open beside a closed half) plus the band devices; the other primitives only band devices:
        //   Drop: divider (barriers), taper, closed ends, kerb fence;  Sidewalk: kerb fence, closed ends;  None: verge fence;
        //   Dressing (and a window not decided yet): kerb-line barriers, cones along the bands (and the centre line while
        //   re-marking).
        // A one-sided build window under Half has the band divider along the closed half instead of the staged divider cones
        // (PropPlan.BandDivider Cones: Props stands barriers there from a posted 60 km/h, like on every band divider).
        // Build bands of later windows get cones along their edge (WaitingMask). Teardown picks the closed ends up.
        private static PropPlan UpgradeProps(in ProjectView v)
        {
            var pp = new PropPlan { Upgrade = true, StartBarrierKeep = 1f, EndBarrierKeep = 1f, BandDivider = DividerStyle.Cones };
            if (v.Phase == WorksPhase.Complete) return pp;
            var u = v.Upgrade;
            int lw = u.LayoutWindow;
            // the applied window: during the Vacate step of a window start the previous window's devices stay
            int aw = math.clamp(u.AppliedWindow, 0, math.max(0, u.WindowCount - 1));
            // teardown: the layout window's bands keep their devices until the crews picked the closed ends up
            pp.BandMask = u.MaskOf(aw);
            for (int i = 0; i < u.BandCount; i++)
            {
                var b = u.Band(i);
                if (!b.IsBuild || b.Window <= aw) continue;
                if (u.AllAtOnce) pp.BandMask |= (byte)(1 << i);
                else pp.WaitingMask |= (byte)(1 << i);
            }
            var prim = u.AppliedTraffic;
            bool closed = v.Closure == ClosureLevel.Closed;
            var st = Stage(v);
            RoadZones openCar = v.OpenLanes & RoadZones.Carriageway;
            RoadZones worksCar = st.Works & RoadZones.Carriageway & ~v.OpenLanes;
            pp.InLaneReady = worksCar != RoadZones.None && (worksCar & ~v.WorkZonesReady) == 0;
            bool remarkHere = u.RemarkWindow(aw);
            if (closed && (prim == BandTraffic.Half || prim == BandTraffic.Carriageway))
            {
                bool halfOpen = openCar != RoadZones.None && openCar != RoadZones.Carriageway;
                pp.WorksHalf = halfOpen ? worksCar : RoadZones.None;
                pp.Barriers = halfOpen ? (pp.InLaneReady ? BarrierStyle.WorksHalf : BarrierStyle.None) : BarrierStyle.AcrossCarriageway;
                if ((v.OpenLanes & RoadZones.SidewalkLeft) != 0 && (v.OpenLanes & RoadZones.LeftHalf) == 0) pp.FenceSides |= RoadZones.SidewalkLeft;
                if ((v.OpenLanes & RoadZones.SidewalkRight) != 0 && (v.OpenLanes & RoadZones.RightHalf) == 0) pp.FenceSides |= RoadZones.SidewalkRight;
                if (pp.FenceSides != RoadZones.None) pp.Fence = FenceStyle.Kerbs;
                bool stagedC4 = remarkHere && v.HalvesActive;
                // a build window under Half: the band divider along the closed half (barriers from a posted 60 km/h), not the
                // staged divider cones
                bool bandDivider = prim == BandTraffic.Half && !remarkHere;
                pp.Divider = !bandDivider && (halfOpen || stagedC4) && pp.InLaneReady ? DividerStyle.Cones : DividerStyle.None;
                pp.CentreCones = pp.Divider == DividerStyle.Cones;
                if (bandDivider) pp.Devices |= BandDevices.Divider;
                pp.Signs = halfOpen && pp.InLaneReady;
                pp.ApproachSignals = pp.Signs;
                pp.OpenEntryAtEnd = halfOpen && !RoadZoneMath.TravelsWithChain(openCar, v.LeftHandTraffic);
                if (!remarkHere) pp.Devices |= BandDevices.Taper | BandDevices.KerbFence;
            }
            else
            {
                switch (prim)
                {
                    case BandTraffic.Drop:
                        pp.Barriers = BarrierStyle.None;
                        pp.Devices |= BandDevices.Divider | BandDevices.Taper | BandDevices.ClosedEnd | BandDevices.KerbFence;
                        pp.BandDivider = DividerStyle.Barriers;
                        break;
                    case BandTraffic.Paint:
                        // a moving operation: cones along the closed lanes and a taper in front of them, no barriers or fences
                        pp.Barriers = BarrierStyle.None;
                        pp.Devices |= BandDevices.Divider | BandDevices.Taper | BandDevices.ClosedEnd;
                        pp.BandDivider = DividerStyle.Cones;
                        break;
                    case BandTraffic.Sidewalk:
                        pp.Barriers = BarrierStyle.None;
                        pp.Devices |= BandDevices.KerbFence | BandDevices.ClosedEnd;
                        break;
                    case BandTraffic.None:
                        pp.Barriers = BarrierStyle.None;
                        break;
                    default:
                        pp.Barriers = BarrierStyle.KerbLine;
                        pp.Devices |= BandDevices.EdgeCones;
                        if (remarkHere) pp.Devices |= BandDevices.CentreCones;
                        break;
                }
            }
            for (int i = 0; i < u.BandCount; i++)
                if (!u.InTeardown && (pp.BandMask & (1 << i)) != 0 && u.Band(i).Kind == BandKind.Remove) pp.Devices |= BandDevices.VergeFence;
            if (pp.WaitingMask != 0) pp.Devices |= BandDevices.EdgeCones;
            if (u.InTeardown)
            {
                pp.StartBarrierKeep = 1f - Sweep(u.Gw, 0.2f, 0.5f);
                pp.EndBarrierKeep = 1f - Sweep(u.Gw, 0.5f, 0.8f);
            }
            else if (remarkHere && v.Phase == WorksPhase.Finishing && u.Window == lw)
            {
                // the re-marking crew picks the closed ends up like the C4 crew of a new road
                pp.StartBarrierKeep = 1f - Sweep(v.F, 0.90f, 0.94f);
                pp.EndBarrierKeep = 1f - Sweep(v.F, 0.94f, 0.98f);
            }
            int lead = u.LeadBand(lw);
            if (lead >= 0 && !u.InTeardown && UpgradeBandPhase(v, lead, out var lph, out _, out _))
                pp.DumpHeapLoad = lph == WorksPhase.Foundation ? LoadKind.Stone : lph == WorksPhase.Restore ? LoadKind.Ore : LoadKind.None;
            return pp;
        }

        // Project-level slot fills of a mode H view: edge cones while the window is Dressing (or not decided yet), the crew props
        // as for a new road;
        // heaps are per band (UpgradeBandPropFill), survey cones never.
        private static int UpgradePropFill(PropGroup g, int k, in ProjectView v, in PropPlan plan)
        {
            switch (g)
            {
                case PropGroup.EdgeCones:
                    return UpgradeDressed(v.Upgrade.AppliedTraffic) && !v.Upgrade.InTeardown ? 255 : -1;
                case PropGroup.CrewProps:
                    return plan.StartBarrierKeep > 0f ? 255 : -1;
                default:
                    return -1;
            }
        }

        // Heap fill of slot k (SlotU(g, k, view): chain slots over the trimmed chain) for chain band `band`; -1 = no heap. Props
        // places the heap laterally on a machine-safe sub-strip of the band (the outer side, never on a sidewalk, outside driveway
        // keep-outs). Build / rebuild: spoil heaps grow behind the dig (Excavation), keep their size and go with the paving (with a
        // re-marking following: at the end of Paving; otherwise behind the painter); dump heaps in Foundation. Remove: stone windrow
        // and rubble in BreakUp, loaded away in Removal; dump heaps in Restore.
        public static int UpgradeBandPropFill(PropGroup g, int k, in ProjectView v, int band)
        {
            if (!v.IsUpgrade || v.Phase == WorksPhase.Complete) return -1;
            var u = v.Upgrade;
            if (band < 0 || band >= u.BandCount) return -1;
            var b = u.Band(band);
            if (u.Window != b.Window) return -1;
            UpgradeBandPhase(v, band, out var ph, out float f, out _);
            Trims(v, out float t0, out float t1);
            float L = t1 - t0;
            float F = UpgradeBandFront(v, band);
            float s = SlotU(g, k, v);
            float fill = -1f;
            if (b.Kind == BandKind.Remove)
            {
                switch (g)
                {
                    case PropGroup.StoneWindrow:
                        if (ph == WorksPhase.BreakUp) fill = RRWConst.kWindrowFill * Sweep(F, s - 2f, s + 4f);
                        else if (ph == WorksPhase.Removal) fill = RRWConst.kWindrowFill * (1f - Sweep(F, s - 4f, s));
                        break;
                    case PropGroup.Rubble:
                        if (ph == WorksPhase.BreakUp) fill = F >= s + 9f ? 255f : -1f;
                        else if (ph == WorksPhase.Removal) fill = F < s - 2f && t0 + MainFront(WorksPhase.BreakUp, 1f, L) >= s + 9f ? 255f : -1f;
                        break;
                    case PropGroup.DumpHeaps:
                        if (ph == WorksPhase.Restore) fill = 255f * Sweep(F, s - 12f, s - 6f) * (1f - Sweep(F, s - 3f, s + 3f));
                        break;
                }
            }
            else if (b.IsBuild)
            {
                bool remarkFollows = u.RemarkFollows(b.Window);
                float endOfDig = 255f * Sweep(t0 + MainFront(WorksPhase.Excavation, 1f, L), s + 6f, s + 12f);
                switch (g)
                {
                    case PropGroup.SpoilHeaps:
                        if (ph == WorksPhase.Excavation) fill = 255f * Sweep(F, s + 6f, s + 12f);
                        else if (ph == WorksPhase.Foundation) fill = endOfDig;
                        else if (ph == WorksPhase.Paving) fill = remarkFollows ? endOfDig * (1f - Sweep(f, 0.90f, 0.97f)) : endOfDig;
                        else if (ph == WorksPhase.Finishing) fill = endOfDig * (1f - Sweep(F, s + 12f, s + 18f)) * (1f - Sweep(f, 0.90f, 0.97f));
                        break;
                    case PropGroup.DumpHeaps:
                        if (ph == WorksPhase.Foundation) fill = 255f * Sweep(F, s - 12f, s - 6f) * (1f - Sweep(F, s - 3f, s + 3f));
                        break;
                }
            }
            if (fill < 1f) return -1;
            return (int)math.min(255f, math.round(fill));
        }

        // ------------------------------------------------------------------ crews

        // One crew per band of the layout window (teardown: 1), capped by maxCrews and kMaxCrewsPerProject.
        private static int UpgradeCrewCount(in ProjectView v, int maxCrews)
        {
            var u = v.Upgrade;
            int n = u.InTeardown ? 1 : u.BandsIn(u.LayoutWindow);
            int cap = math.min(math.clamp(maxCrews, 1, RRWConst.kMaxCrewsPerProject), RRWConst.kMaxCrewsPerProject);
            return math.clamp(n, 1, cap);
        }

        // Rollers of chain band `band` (section length L = the trimmed chain): base course in Foundation (one), fresh asphalt in
        // Paving (one, two from kRollerTwoMinSection), restored ground of a remove band (kRollerInRestore); only with a machine-safe
        // width of at least kUwRollerMinWidth and rollers on (view.Rollers > 0).
        private static int UpgradeRollerCount(in ProjectView v, int band, float sectionLength)
        {
            var u = v.Upgrade;
            if (v.Rollers <= 0 || band < 0 || band >= u.BandCount || sectionLength < RRWConst.kMachineMinChain) return 0;
            var b = u.Band(band);
            if (b.Kind == BandKind.Remark || b.SafeWidth < RRWConst.kUwRollerMinWidth) return 0;
            if (!UpgradeBandPhase(v, band, out var ph, out _, out _)) return 0;
            int max = math.min(v.Rollers, RRWConst.kMaxRollersPerCrew);
            switch (ph)
            {
                case WorksPhase.Foundation: return b.IsBuild ? 1 : 0;
                case WorksPhase.Paving: return sectionLength >= RRWConst.kRollerTwoMinSection ? max : 1;
                case WorksPhase.Restore: return RRWConst.kRollerInRestore ? 1 : 0;
                default: return 0;
            }
        }

        // The plan of crew `crew` of a mode H view: the crew works chain band UpgradeView.CrewBand(crew) over the trimmed chain.
        //  * re-marking band under Paint: a band crew (below) in the lanes closed around the painter, Finishing roster;
        //  * re-marking band (Half / Carriageway): the C4 crew of a new road (painter + crew truck in the works half, fraction
        //    laterals, LateralMetres = false); under Dressing no crew (nothing may stand in a lane);
        //  * build / rebuild / remove band: LateralMetres = true, every lateral in metres (chain frame) inside the band's machine-safe
        //    range (UpgradeBandView.SafeLo..SafeHi, kUwMachineLatInset from its edges); the roster by its width W:
        //      Excavation  W >= kUwExcavatorMinWidth: excavator (inner); W >= kUwMiniExcavatorMinWidth: mini excavator
        //                  (SmallMachines); the loading truck stands in line AHEAD of it (FrontLoad: the excavator dumps to the
        //                  front within its slew limit, tail towards it, kUwFrontLoadAhead / kUwFrontLoadAheadSmall from its pivot)
        //                  and waits at the chain-end slots (EndSlotIn); narrower: a truck and the crew truck
        //      Foundation  loader / grader spreading (W >= kUwLoaderMinWidth), dump truck, base-course roller
        //      Paving      paver, feeding truck, rollers (W >= kUwRollerMinWidth)
        //      Finishing   painter + crew truck (only without a re-marking window after it)
        //      BreakUp / Removal / Restore (remove band, on its terrain part): breaker / excavator + truck loading in line ahead
        //                  of it (as in Excavation), then loader + truck
        //  * setup: only the crew truck (parked); teardown: no roles (the crews leave along their closed strip).
        private static CrewPlan UpgradeCrew(in ProjectView v, int crew)
        {
            var u = v.Upgrade;
            var c = new CrewPlan { Band = -1, LateralMetres = true };
            int n = UpgradeCrewCount(v, RRWConst.kMaxCrewsPerProject);
            crew = math.clamp(crew, 0, n - 1);
            c.Crew = crew; c.Crews = n;
            Trims(v, out float lo, out float hi);
            c.SecLo = lo; c.SecHi = hi;
            float L = hi - lo;
            if (v.Phase == WorksPhase.Complete || u.InTeardown || L < RRWConst.kShortEdge) return c;
            int band = u.CrewBand(crew);
            if (band < 0) return c;
            c.Band = band;
            var b = u.Band(band);
            float exit = ExitU(v);
            sbyte exitFacing = ExitFacing(v);
            float lead = RRWConst.kFinisherLead, back = RRWConst.kExcavatorBack;
            // the re-marking band painting on the move (Paint) is a band crew: painter and crew truck inside its machine-safe run (the
            // lanes Traffic closes around the painter)
            if (b.Kind == BandKind.Remark && u.Traffic != BandTraffic.Paint)
            {
                c.LateralMetres = false;
                if (u.InSetup || !(u.Traffic == BandTraffic.Half || u.Traffic == BandTraffic.Carriageway) || L < RRWConst.kMachineMinChain) return c;
                float g = u.BandG(band);
                var t = v;
                t.Phase = WorksPhase.Finishing;
                t.F = g;
                float F = UpgradeBandFront(v, band);
                c.FrontU = F;
                c.MaxTrucks = 0;
                C4Crew(ref c, t, F, g, lo, hi, true, true, exit, exitFacing, lead);
                c.TruckA = default;
                c.TruckB = default;
                return c;
            }
            float W = b.SafeWidth;
            float safeLo = b.SafeLo, safeHi = b.SafeHi;
            float centre = (safeLo + safeHi) * 0.5f;
            if (W < 1f) return c;   // no room for a machine anywhere in the band now
            bool rightSide = b.Side == BandSide.Right || (b.Side != BandSide.Left && b.Centre > 0f);
            float inset = RRWConst.kUwMachineLatInset;
            bool twoLanes = W >= 2f * inset + 0.5f;
            float outer = twoLanes ? (rightSide ? safeHi - inset : safeLo + inset) : centre;
            float inner = twoLanes ? (rightSide ? safeLo + inset : safeHi - inset) : centre;
            UpgradeBandPhase(v, band, out var ph, out float f, out _);
            float Fb = UpgradeBandFront(v, band);
            c.FrontU = Fb;
            c.MaxTrucks = L >= 60f ? 2 : L >= 20f ? 1 : 0;
            c.BaseSlotU = StartSlotIn(1, lo, hi); c.BaseLateralA = outer;
            c.BaseSlotUB = StartSlotIn(2, lo, hi); c.BaseLateralB = outer;
            float s0 = StartSlotIn(0, lo, hi);
            c.CrewTruck = Slot(MachineActivity.Parked, s0, centre, 1);
            if (u.InSetup || L < RRWConst.kMachineMinChain) return c;
            bool wide = W >= RRWConst.kUwExcavatorMinWidth, mid = W >= RRWConst.kUwMiniExcavatorMinWidth;
            c.SmallMachines = !wide;
            int nr = UpgradeRollerCount(v, band, L);
            if (b.Kind == BandKind.Remove)
            {
                c.MaxTrucks = math.min(c.MaxTrucks, 1);
                switch (ph)
                {
                    case WorksPhase.BreakUp:
                    case WorksPhase.Removal:
                    {
                        var act = ph == WorksPhase.BreakUp ? MachineActivity.Break : MachineActivity.Dig;
                        LoadingCrew(ref c, act, mid, wide, Fb - back, wide ? inner : centre, centre, LoadKind.Stone, lo, hi);
                        break;
                    }
                    default:
                    {
                        bool leaving = f >= 0.9f;
                        c.Loader = leaving ? Slot(MachineActivity.DriveOut, exit, centre, exitFacing)
                                 : W >= RRWConst.kUwLoaderMinWidth ? Slot(MachineActivity.Spread, Fb + 1f, centre, 1) : default;
                        if (!leaving) c.TruckA = Slot(MachineActivity.DumpAtFront, Fb + 6f, wide ? outer : centre, 1, LoadKind.Ore);
                        c.BaseSlotU = EndSlotIn(1, lo, hi); c.BaseSlotUB = EndSlotIn(2, lo, hi);
                        if (nr > 0)
                        {
                            c.Rollers = 1;
                            c.Roller0 = leaving ? RollerParked(RollerDuty.Backfill, MachineActivity.DriveOut, exit, exitFacing, false)
                                                : RollerCompact(RollerDuty.Backfill, Fb, lo, hi, float.NaN, false);
                        }
                        break;
                    }
                }
            }
            else
            {
                switch (ph)
                {
                    case WorksPhase.Excavation:
                        LoadingCrew(ref c, MachineActivity.Dig, mid, wide, Fb - back, wide ? inner : centre, centre, LoadKind.Ore, lo, hi);
                        if (wide) c.TruckB = Slot(MachineActivity.Shuttle, c.TruckA.U, c.TruckA.Lateral, 1, LoadKind.Ore);
                        c.CrewTruck = Slot(MachineActivity.Follow, math.max(s0, Fb - 30f), centre, 1);
                        break;
                    case WorksPhase.Foundation:
                        if (W >= RRWConst.kUwLoaderMinWidth) c.Loader = Slot(MachineActivity.Spread, Fb + 1f, centre, 1);
                        c.TruckA = Slot(MachineActivity.DumpAtFront, Fb + 6f, wide ? outer : centre, 1, LoadKind.Stone);
                        c.BaseSlotU = EndSlotIn(1, lo, hi); c.BaseSlotUB = EndSlotIn(2, lo, hi);
                        if (nr > 0)
                        {
                            c.Rollers = 1;
                            c.Roller0 = RollerCompact(RollerDuty.BaseCourse, Fb, lo, hi, float.NaN, false);
                        }
                        break;
                    case WorksPhase.Paving:
                    {
                        c.Finisher = Slot(MachineActivity.Pave, Fb + lead, centre, 1);
                        c.TruckA = Slot(MachineActivity.DumpAtFront, Fb + lead + 3.7f + 0.4f + 4.5f, centre, 1, LoadKind.Coal);
                        if (wide) c.TruckB = Slot(MachineActivity.Wait, Fb + 25f, outer, 1, LoadKind.Coal);
                        float ctBack = nr >= 2 ? RRWConst.kC3CrewTruckBackTwoRollers : nr == 1 ? RRWConst.kC3CrewTruckBackOneRoller : 32f;
                        float ctU = math.max(s0, Fb - ctBack);
                        c.CrewTruck = Slot(MachineActivity.Follow, ctU, centre, 1);
                        c.BaseSlotU = EndSlotIn(1, lo, hi); c.BaseSlotUB = EndSlotIn(2, lo, hi);
                        if (nr > 0)
                        {
                            c.Rollers = nr;
                            c.Roller0 = RollerCompact(RollerDuty.Breakdown, Fb, lo, hi, ctU, false);
                            if (nr >= 2) c.Roller1 = RollerCompact(RollerDuty.Finish, Fb, lo, hi, ctU, false);
                        }
                        break;
                    }
                    default:
                        c.Finisher = Slot(MachineActivity.Paint, Fb + lead, centre, 1);
                        c.CrewTruck = Slot(MachineActivity.Follow, math.max(s0, Fb - 15f), centre, 1);
                        c.BaseSlotU = EndSlotIn(1, lo, hi); c.BaseSlotUB = EndSlotIn(2, lo, hi);
                        break;
                }
            }
            if (c.Roller0.Active) c.Roller0.Slot.Lateral = centre;
            if (c.Roller1.Active) c.Roller1.Slot.Lateral = centre;
            if (c.MaxTrucks < 2) c.TruckB = default;
            if (c.MaxTrucks < 1) c.TruckA = default;
            if (!c.TruckA.Active) c.FrontLoad = false;
            return c;
        }

        // Loading roles of a band crew (Excavation, BreakUp, Removal). With an excavator (mid: W >= kUwMiniExcavatorMinWidth) the
        // loading truck stands in line AHEAD of it at the excavator's lateral, tail towards it (front dump within the slew limit):
        // the truck centre kUwFrontLoadAhead (mini: kUwFrontLoadAheadSmall) beyond the pivot; Machines refines the gap from the
        // arm reach and the box lengths. Without one, a truck at the front like a hand crew's. The trucks wait at the chain-end
        // slots (EndSlotIn 1 / 2) and reverse up to the stop.
        private static void LoadingCrew(ref CrewPlan c, MachineActivity act, bool mid, bool wide, float exU, float exLat, float centre,
                                        LoadKind load, float lo, float hi)
        {
            if (mid)
            {
                c.Excavator = Slot(act, exU, exLat, 1);
                float ahead = wide ? RRWConst.kUwFrontLoadAhead : RRWConst.kUwFrontLoadAheadSmall;
                c.TruckA = Slot(MachineActivity.LoadAtFront, exU + ahead, exLat, 1, load);
                c.FrontLoad = true;
            }
            else c.TruckA = Slot(MachineActivity.LoadAtFront, exU, centre, -1, load);
            c.BaseSlotU = EndSlotIn(1, lo, hi); c.BaseSlotUB = EndSlotIn(2, lo, hi);
            c.BaseLateralA = c.BaseLateralB = c.TruckA.Lateral;
        }

        // ------------------------------------------------------------------ gameplay

        // Closure target of a mode H project (PhasePlan.Closure calls it first for HalfWidth; never the start closure, never
        // `dependants`): machines holding the road (carriagewayBusy) -> Closed; Complete -> Open; VisualOnly -> Open; AlwaysSlowZone
        // -> SlowZone; Sidewalk, Drop, Half, Carriageway -> Closed (the window's groups are controlled; UpgradeOpenLanes opens
        // every other group); None, Dressing, Undecided -> SlowZone. The Director passes the layout window's primitive
        // (UpgradeView.Traffic: setup = window 0's, teardown = the last window's).
        public static ClosureLevel UpgradeClosure(BandTraffic prim, ClosurePolicy policy, WorksPhase ph, bool carriagewayBusy)
        {
            if (carriagewayBusy) return ClosureLevel.Closed;
            if (ph == WorksPhase.Complete) return ClosureLevel.Open;
            if (policy == ClosurePolicy.VisualOnly) return ClosureLevel.Open;
            if (policy == ClosurePolicy.AlwaysSlowZone) return ClosureLevel.SlowZone;
            return Closes(prim) ? ClosureLevel.Closed : ClosureLevel.SlowZone;
        }

        // Open lane groups of a mode H project (independent of the staged-opening setting: the rest of the road always carries
        // traffic): every group the stage does not work in or drain, parking included (existing parking is never closed by
        // groups; empty new parking lanes are disabled per lane by Traffic). A group opens only when a fresh machine report shows
        // no puppet in it (no fresh report: nothing new opens, as for mode A); once open it stays open while wanted. Complete:
        // keep what is open (the release opens the rest). The Director seeds ProjectRecord.OpenLanes with every group when it
        // adopts the project (also after a load): the road was open, so groups no window closes never wait for a first report.
        private static RoadZones UpgradeOpenLanes(in ProjectView v, in StageInfo st, RoadZones machineZones, bool machinesReportFresh,
                                                  RoadZones openBefore, RoadZones softZones)
        {
            if (v.Phase == WorksPhase.Complete) return openBefore & RoadZones.AllLanes;
            RoadZones want = st.Open & RoadZones.AllLanes & ~(st.Works | st.Soft | softZones);
            RoadZones blocked = machinesReportFresh ? machineZones : RoadZones.AllLanes;
            return ((openBefore & want) | (want & ~blocked)) & RoadZones.AllLanes;
        }

        // Stage of a mode H project. The layout window's groups are the works (UpgradeView.ZonesOf + PreClosed); everything else
        // is open. Every window start is a stage switch (Director: Vacate -> Swap -> Drain -> Ready, accrual held at HoldP):
        //  * setup: window 0's groups; SwitchP = HoldP = the setup end (window 0 starts once they are drained);
        //  * window k: SwitchP = HoldP = the end of window k (the next window's start), NextWorks = its groups;
        //  * Vacate at a window start keeps the previous window's layout; Swap / Drain use the new one with Soft = its groups;
        //  * the re-marking window under Half runs C4a / C4b of a new road inside the window (Index 0 / 1 of Count 2 with the swap,
        //    SwitchP = HoldP = the window's start + kC4SwapF of its length); without the swap the works half stays;
        //  * teardown: the last window's groups (the release opens them); Complete: keep what is open.
        // Index / Count are the C4a / C4b stage of the re-marking window only (0 of 1 otherwise; the UI shows UpgradeView.Window).
        private static StageInfo UpgradeStage(in ProjectView v, in StageContext c)
        {
            var u = v.Upgrade;
            var st = new StageInfo { Count = 1, SwitchP = float.NaN, HoldP = float.NaN };
            if (v.Phase == WorksPhase.Complete) { st.Open = v.OpenLanes & RoadZones.AllLanes; return st; }
            int N = u.WindowCount, w = u.Window;
            float tol = RRWConst.kStageSwitchTolF;
            bool newLayout = v.Switch == StageSwitch.Swap || v.Switch == StageSwitch.Drain || v.Switch == StageSwitch.Ready;
            bool switching = v.Switch == StageSwitch.Swap || v.Switch == StageSwitch.Drain;
            int lw = UpgradeAppliedWindow(u, v.Switch);
            RoadZones works = u.ZonesOf(lw) | u.PreClosed;
            bool remarkHalf = u.RemarkWindow(lw) && u.Prim(lw) == BandTraffic.Half;
            if (remarkHalf)
            {
                RoadZones a = RRWConst.kStagedWorksHalf, b = RRWConst.kStagedOpenHalf;
                bool second = false;
                if (c.SwapOn && lw == w)
                {
                    st.Count = 2;
                    float p0 = u.Schedule.P0(lw), p1 = u.Schedule.P1(lw);
                    st.SwitchP = st.HoldP = p0 + RRWConst.kC4SwapF * (p1 - p0);
                    bool atSwap = u.Gw >= 0.5f * RRWConst.kC4SwapF;
                    second = atSwap && (newLayout || (v.Switch == StageSwitch.None && u.Gw >= RRWConst.kC4SwapF - tol));
                }
                if (second)
                {
                    st.Index = 1;
                    works = b | RoadZoneMath.ParkingOf(b);
                    st.NextWorks = RoadZones.None;
                    if (switching) st.Soft = b;
                    st.SwitchP = st.HoldP = lw + 1 < N ? u.Schedule.P1(lw) : float.NaN;
                }
                else
                {
                    works = a | RoadZoneMath.ParkingOf(a) | u.PreClosed;
                    st.NextWorks = c.SwapOn ? b : lw + 1 < N ? u.ZonesOf(lw + 1) : RoadZones.None;
                    if (switching) st.Soft = works;
                }
            }
            else
            {
                st.NextWorks = lw + 1 < N ? u.ZonesOf(lw + 1) : RoadZones.None;
                if (switching) st.Soft = works;
            }
            if (float.IsNaN(st.SwitchP))
            {
                if (w < 0) st.SwitchP = st.HoldP = u.Schedule.SetupP;
                else if (w < N && lw + 1 < N) st.SwitchP = st.HoldP = u.Schedule.P1(lw);
            }
            st.Works = works;
            st.Open = RoadZones.AllLanes & ~works & ~st.Soft;
            return st;
        }
    }
}
