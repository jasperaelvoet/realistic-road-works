using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // Everything Ground / Director need to know about ONE works edge to compute its terrain target and wear. Pure data.
    public struct PlanInput
    {
        public WorksKind Kind;
        public VisualMode Mode;
        public SiteFlags SiteFlags;
        public float P;                 // progress 0..1
        public float ChainU0, ChainU1;  // chain coordinates of curve t=0 / t=1
        public float ChainLength;       // U
        public float EdgeLength;        // Curve.m_Length (may differ slightly from |U1-U0| after geometry edits)
        public float SubgradeDepth;     // dSub (0.75 / 1.0)
        public float NatAbove, NatBelow;// NaN = unsampled
        public float RestoreT, RestoreY;
        public int Crews;               // crew sections this phase (RoadWorksRuntime.m_Crews; 0/1 = one front)

        public float ChainLo => math.min(ChainU0, ChainU1);
        public float ChainHi => math.max(ChainU0, ChainU1);
        public bool Replaced => (SiteFlags & SiteFlags.Replaced) != 0;

        public static PlanInput From(in RoadWorksSite s, float p, float edgeLength, float subgradeDepth) => new PlanInput
        {
            Kind = s.Kind,
            Mode = s.Mode,
            SiteFlags = s.Flags,
            P = p,
            ChainU0 = s.m_ChainU0,
            ChainU1 = s.m_ChainU1,
            ChainLength = s.m_ChainLength,
            EdgeLength = edgeLength,
            SubgradeDepth = subgradeDepth,
            NatAbove = s.NaturalValid ? s.m_NatAbove : float.NaN,
            NatBelow = s.NaturalValid ? s.m_NatBelow : float.NaN,
            RestoreT = s.RestoreT,
            RestoreY = s.RestoreY,
        };

        public static PlanInput From(in RoadWorksSite s, in RoadWorksRuntime r)
        {
            var i = From(s, r.m_Progress, r.m_EdgeLength, r.m_SubgradeDepth);
            i.Crews = r.m_Crews;   // per-section fronts (Terrain C1/D2 depth, D0 wear)
            return i;
        }
    }

    // Project-level state for the span / prop / crew plans (ProjectRecord.View()). Pure data, job-safe.
    public struct ProjectView
    {
        public WorksKind Kind;
        public VisualMode Mode;
        public WorksPhase Phase;
        public float F;                 // phase fraction f
        public float U;                 // chain length
        public float Trim0, Trim1;      // chain u where the works geometry starts / ends (visible junction trims)
        public SiteFlags Flags;
        public WorksPhase CancelPhase;  // None unless CancelledBuild
        public float CancelFront;       // chain u of the main front at cancel
        public ClosureLevel Closure;
        public RoadZones OpenLanes;     // staged opening the Director decided (ProjectRecord.OpenLanes); None in Simple()
        public RoadZones SeparableHalves; // halves every edge can open on its own (ProjectRecord.SeparableHalves); None in Simple()
        // ---- staged traffic (ProjectRecord.View() copies them; default in Simple(): no staging)
        public RoadZones SoftZones;     // groups being drained (ProjectRecord.SoftZones)
        public RoadZones WorkZonesReady;// closed + drained groups machines may use (ProjectRecord.WorkZonesReady)
        public StageSwitch Switch;      // running stage switch step (ProjectRecord.Switch)
        public StageContext Ctx;        // the Director's StageContext of this frame (ProjectRecord.StageCtx)
        public RoadZones BuildingSidewalks; // D0 house-side sidewalks (ProjectRecord.BuildingSidewalks, chain frame)
        public bool ExitAtStart, ExitAtEnd; // chain ends that connect to the road network (ProjectRecord.ExitAtStart/End)
        public bool ExitsKnown;         // the exits above were computed (ProjectRecord.ExitsKnown; false in Simple())
        public bool LeftHandTraffic;    // RRWCity.LeftHandTraffic
        // ---- crew sections (ProjectRecord.View() copies them; Simple(): one crew, unknown duration = the single-crew plans)
        public int Crews;               // crew sections of this phase (ProjectRecord.Crews, latched per phase by the Director); 0/1 = one
        public float WorkSeconds;       // machine seconds for p 0 -> 1 at the current nominal rate (ProjectRecord.WorkSeconds;
                                        // 0 = unknown: no return-aware anchors, crew count from the length only)
        // ---- road rollers (ProjectRecord.View() copies it; Simple(): 0 = no rollers = the plans without rollers, bit for bit)
        public int Rollers;             // most rollers per crew this phase (ProjectRecord.Rollers, latched with Crews: RRWSetting.RollersOn ?
                                        // kMaxRollersPerCrew : 0). Crew() plans roller slots only when > 0 (and moves the C3 crew truck back)

        // Number of crew sections (>= 1, <= kMaxCrewsPerProject).
        public int CrewCount => Crews <= 1 ? 1 : math.min(Crews, RRWConst.kMaxCrewsPerProject);
        // Crew count of the cancelled construction phase (SiteFlags.CancelCrewsMask; 1 for older saves).
        public int CancelCrewCount => PhasePlan.CancelCrewsOf(Flags);
        // Front of crew i (section i). One crew: exactly Front.
        public float CrewFront(int i) => PhasePlan.CrewFront(this, i);
        // Machine seconds of the current phase (0 = unknown).
        public float PhaseSeconds => WorkSeconds > 0f ? WorkSeconds * (PhasePlan.PhaseEnd(Phase) - PhasePlan.PhaseStart(Phase)) : 0f;

        // Main front F (stage-aware in C4 when the side swap runs).
        public float Front => PhasePlan.MainFront(this);
        // The C4 per-half layout is in use (staged construction, mode A, C4, a car half allowed).
        public bool HalvesActive => Kind == WorksKind.Construction && Mode == VisualMode.FullDig && Phase == WorksPhase.Finishing
                                    && Ctx.Staged && Ctx.CarHalfAllowed;
        // C4a -> C4b side swap in use.
        public bool SwapActive => HalvesActive && Ctx.SwapOn;
        public bool Cancelled => (Flags & SiteFlags.CancelledBuild) != 0 && CancelPhase != WorksPhase.None;
        public bool Replaced => (Flags & SiteFlags.Replaced) != 0;
        public float TrimmedLength => math.max(0f, Trim1 - Trim0);

        public static ProjectView Simple(WorksKind kind, VisualMode mode, WorksPhase ph, float f, float U) => new ProjectView
        {
            Kind = kind, Mode = mode, Phase = ph, F = f, U = U, Trim0 = 0f, Trim1 = U,
            CancelPhase = WorksPhase.None, Closure = ClosureLevel.Closed,
        };
    }

    // What the Director passes to PhasePlan.Stage (and stores in ProjectRecord.StageCtx so
    // every module computes the same stage from ProjectView). Pure data.
    public struct StageContext
    {
        public bool Staged;          // RRWSetting.StagedOpeningOn
        public bool SwapOn;          // RRWGates.C4Swap && CarHalfAllowed && switch not skipped for this project (switch safety rules)
        public bool CarHalfAllowed;  // ProjectRecord.CarHalfAllowed (latched while a car half is open)
        public bool D0Sidewalks;     // RRWGates.D0Sidewalks (demolition D0 house-side sidewalks)
        public float FrontFloor;     // highest C4a painter front of this C4 window (chain u; 0 = none). The C4 front
                                     // never drops below it once the side swap is off (skipped mid-C4a / car half lost / waiting
                                     // for classification), so painted markings are never covered again (MainFront)
    }

    // The stage of a project this frame (PhasePlan.Stage). Chain-frame zones.
    public struct StageInfo
    {
        public byte Index, Count;    // C4a = 0 / C4b = 1 of 2 (swap on); otherwise 0 of 1
        public RoadZones Works;      // groups the crew works in now (closed; machines plan into them once in WorkZonesReady)
        public RoadZones NextWorks;  // groups that become works at the next switch (C4a: the open half; D0: the house sidewalks)
        public RoadZones Open;       // groups that SHOULD carry traffic now (wanted; PhasePlan.OpenLanes adds the machine check)
        public RoadZones Soft;       // groups that must drain now (switch steps Swap / Drain; never in Open)
        public float SwitchP;        // p where the next / running switch starts (NaN = none)
        public float HoldP;          // p where accrual is held until the switch reaches Ready (C4: = SwitchP; D0: kD1) (NaN = none)
        public bool Switching => !float.IsNaN(SwitchP) && Soft != RoadZones.None;
    }

    // Prop groups laid out in fixed chain slots. Fill: PhasePlan.PropFill.
    // Divider / Signs are laid out by Props from PropPlan (laterals from EdgeSection.DirSplit), NOT as fixed slots:
    // SlotCount returns 0 for them (Props' slot loop skips them; PropKind 7/8 stay BarrierStart/End).
    public enum PropGroup : byte
    {
        SurveyCones = 0,   // cone pairs on both footprint edges (construction mode A)
        EdgeCones = 1,     // cones on the carriageway edges (mode D, demolition; on the verge while hidden)
        SpoilHeaps = 2,    // CoalTruck01_OrePile01 on the right verge (construction; backfill of cancelled builds)
        DumpHeaps = 3,     // stone (C2) / ore (D2) heaps on the floor in the truck lane, grow when tipped, shrink when spread
        StoneWindrow = 4,  // CoalTruck01_StonePile01 broken asphalt beside the breaker (D0, loaded away in D1)
        Rubble = 5,        // RubblePile* behind the breaker (D0), loaded in D1
        CrewProps = 6,     // RoadMaintenanceVehicle01_Props01-03 + tall cones near the chain start
        Divider = 7,       // divider cones between the open and the works half (PropPlan.Divider; laid out by Props)
        Signs = 8,         // one-way / speed / no-entry plates + amber head at the closed ends (PropPlan.Signs; Props)
        Count = 9,         // RESERVED for later: Taper = 9, Count = 10
    }

    // Divider device between the open half and the works half.
    public enum DividerStyle : byte
    {
        None = 0,
        Cones = 1,         // default: SafetyCone03 every kCentreConeSpacing at DirSplitC + kDividerInset into the works half
        Barriers = 2,      // planned for later (lane drops / >= 60 km/h): continuous SafetyBarrier / ConcreteBarrier line, never BlockedLane
    }

    public enum BarrierStyle : byte
    {
        None = 0,
        AcrossCarriageway = 1,   // Closed: barrier line across the road at each chain end
        KerbLine = 2,            // SlowZone / Open: barriers along the kerbs only (traffic keeps flowing)
        WorksHalf = 3,           // Closed with the open half carrying traffic: barrier line across the WORKS half only,
                                 // at each chain end, with a beacon on the inner end (the taper towards the centre cones)
    }

    // Fencing (user request: "proper fencing").
    public enum FenceStyle : byte
    {
        None = 0,
        Footprint = 1,           // hidden phases (trench): site fence panels along both footprint sides (HalfWidth +
                                 // kFootprintFenceOffset, i.e. just inside the old footprint), only along edges with
                                 // buildings (Props filters per edge)
        Kerbs = 2,               // sidewalks open beside a closed carriageway: low fence panels on the sidewalks
                                 // (kKerbFenceOutset outside the carriageway edge) so pedestrians are separated from the works
    }

    // Static props a phase wants. Props turns this + PropFill into slot entities.
    public struct PropPlan
    {
        public BarrierStyle Barriers;
        public float StartBarrierKeep;   // 0..1 fraction of the start barrier line still standing (pick-up choreography)
        public float EndBarrierKeep;
        public bool EdgeConesOnVerge;    // D1/D2: cones move to the verge (the road is gone)
        public LoadKind DumpHeapLoad;    // Stone (C2) / Ore (D2)
        // fencing / staged opening
        public FenceStyle Fence;
        public bool CentreCones;         // legacy alias of Divider == Cones (kept for one release)
        public bool ApproachSignals;     // flashing-amber head (PrefabNames.PortableSignals) at the OPEN-direction entry, in the
                                         // works-half corner; Props also needs RRWGates.AmberHead && RRWSetting.ApproachSignalsOn.
                                         // Never a red/green shuttle signal (CS2 lanes are one-way)
        // staged traffic devices
        public RoadZones FenceSides;     // SidewalkLeft / SidewalkRight: kerbs whose sidewalk is OPEN while the car half next to it is
                                         // not (C3 both, C4a works kerb, C4b the other, D0 house sides, mode D both). Fence = Kerbs iff != None
        public DividerStyle Divider;     // wanted divider (Props maps it through RRWGates.Divider); in-lane: only when InLaneReady
        public RoadZones WorksHalf;      // the car half the closed-end barrier lines span (BarrierStyle.WorksHalf), None otherwise
        public bool Signs;               // one-way + speed plate at the open-direction entry, no-entry at the closed-direction entry
                                         // (Props also needs RRWGates.Signs); in-lane: only when InLaneReady
        public bool InLaneReady;         // the works car half is closed AND drained (Works & Carriageway within WorkZonesReady):
                                         // in-lane devices (divider, closed-end lines, signs, amber head) may stand
        public bool OpenEntryAtEnd;      // the open half's traffic enters the works at the chain END (Trim1) (else at Trim0); the
                                         // closed direction enters at the other end (chase beacons on that barrier line)
    }

    // What one crew role should be doing. U is the PIVOT anchor in chain u (Machines clamps it inside the
    // trimmed chain); Lateral: |v| <= 0.5 = fraction of the flat floor WIDTH (+ = right of the CHAIN direction), +-1 = verge.
    public struct CrewSlot
    {
        public MachineActivity Activity;
        public float U;
        public float Lateral;
        public sbyte Facing;       // +1 towards +u, -1 towards -u
        public LoadKind Load;
        public bool Active => Activity != MachineActivity.None;
    }

    // One road roller of a crew. Slot.Activity: Compact (passes over [WinLo, WinHi]), Parked (C2 hand-off
    // spot / C3 relay slot), DriveOut, or None (window not open yet: no roller). Slot.U = spawn / hand-over anchor (WinLo while
    // compacting); Slot.Lateral = 0: Machines lays the passes over the whole floor (hidden) / carriageway (visible) with
    // PhasePlan.RollerPassLateral. WinLo / WinHi are the PIVOT range of this update; Machines re-evaluates them with RollerWindow at
    // every reversal (front-relative, at the leg's planned end time), so the passes keep up with the front.
    public struct RollerSlot
    {
        public CrewSlot Slot;
        public RollerDuty Duty;
        public float WinLo, WinHi;   // chain u of the pass window (NaN unless Compact)
        public float PassSpeed;      // m/s on compaction passes (<= 0.92 x MachineLimits.Of(Roller).VFwd)
        public bool Vibrate;         // vibration on forward passes (off while stopped / reversing, as verified in game)
        public bool RelayOnly;       // Parked slot that only a hand-over fills (a live roller of the project); never spawned fresh
        public bool WorksHalfOnly;   // passes stay inside kStagedWorksHalf (C3 f >= kRollerC3WorksHalfF): leave at C4 without crossing
                                     // the half that opens first
        public bool Active => Slot.Active;
    }

    public struct CrewPlan
    {
        public CrewSlot Excavator, Loader, TruckA, TruckB, CrewTruck, Finisher;
        // Up to kMaxRollersPerCrew roller slots (not puppets; MachineRole.Roller). Rollers = slots planned (0..2).
        public RollerSlot Roller0, Roller1;
        public int Rollers;
        public RollerSlot Roller(int i) => i == 0 ? Roller0 : Roller1;
        public float FrontU;       // front of THIS crew (its section front PhasePlan.CrewFront(view, Crew); unquantised)
        public int Crew, Crews;    // crew index and crew count (one crew: 0 / 1)
        public float SecLo, SecHi; // the crew's working range = section [B_i, B_i+1] clipped to [Trim0, Trim1]; its parking /
                                   // base slots and section-relative anchors lie inside it (one crew: Trim0 / Trim1)
        public int MaxTrucks;      // 0..2 by trimmed chain length
        public float BaseSlotU;    // TruckA base slot (start side for LoadAtFront phases, end side for DumpAtFront phases)
        public float BaseSlotUB;   // TruckB base slot (separate lane)
        public float BaseLateralA, BaseLateralB;

        public CrewSlot Get(MachineRole r)
        {
            switch (r)
            {
                case MachineRole.Excavator: return Excavator;
                case MachineRole.Loader: return Loader;
                case MachineRole.TruckA: return TruckA;
                case MachineRole.TruckB: return TruckB;
                case MachineRole.CrewTruck: return CrewTruck;
                case MachineRole.Roller: return Roller0.Slot;   // the first roller slot (use Roller(i) for both)
                default: return Finisher;
            }
        }

        // The role that owns the visible work front (budget priority).
        public MachineRole FrontOwner(WorksPhase ph)
        {
            switch (ph)
            {
                case WorksPhase.Survey: return MachineRole.Loader;
                case WorksPhase.Excavation: return MachineRole.Excavator;
                case WorksPhase.Foundation: return MachineRole.Loader;
                case WorksPhase.Paving:
                case WorksPhase.Finishing: return MachineRole.Finisher;
                case WorksPhase.BreakUp:
                case WorksPhase.Removal: return MachineRole.Excavator;
                default: return MachineRole.Loader;
            }
        }
    }

    // The v3 timeline as pure functions. No ECS, no allocation: safe in jobs.
    // Chain coordinate u runs 0..U along the project chain; every per-edge function maps it through
    // the edge's [ChainU0, ChainU1] range.
    public static class PhasePlan
    {
        // ------------------------------------------------------------------ phases

        public static WorksPhase FirstPhase(WorksKind kind) => kind == WorksKind.Construction ? WorksPhase.Survey : WorksPhase.BreakUp;

        // Progress a new site starts at (Replaced roads skip C0: they start from the old road bed).
        public static float StartProgress(WorksKind kind, SiteFlags flags) =>
            kind == WorksKind.Construction && (flags & SiteFlags.Replaced) != 0 ? RRWConst.kC1 : 0f;

        public static WorksPhase PhaseOf(WorksKind kind, float p, out float f)
        {
            if (p >= 1f) { f = 1f; return WorksPhase.Complete; }
            p = math.max(0f, p);
            if (kind == WorksKind.Construction)
            {
                if (p < RRWConst.kC1) { f = p / RRWConst.kC1; return WorksPhase.Survey; }
                if (p < RRWConst.kC2) { f = (p - RRWConst.kC1) / (RRWConst.kC2 - RRWConst.kC1); return WorksPhase.Excavation; }
                if (p < RRWConst.kC3) { f = (p - RRWConst.kC2) / (RRWConst.kC3 - RRWConst.kC2); return WorksPhase.Foundation; }
                if (p < RRWConst.kC4) { f = (p - RRWConst.kC3) / (RRWConst.kC4 - RRWConst.kC3); return WorksPhase.Paving; }
                f = (p - RRWConst.kC4) / (1f - RRWConst.kC4); return WorksPhase.Finishing;
            }
            if (p < RRWConst.kD1) { f = p / RRWConst.kD1; return WorksPhase.BreakUp; }
            if (p < RRWConst.kD2) { f = (p - RRWConst.kD1) / (RRWConst.kD2 - RRWConst.kD1); return WorksPhase.Removal; }
            f = (p - RRWConst.kD2) / (1f - RRWConst.kD2); return WorksPhase.Restore;
        }

        public static float PhaseStart(WorksPhase ph)
        {
            switch (ph)
            {
                case WorksPhase.Survey: return 0f;
                case WorksPhase.Excavation: return RRWConst.kC1;
                case WorksPhase.Foundation: return RRWConst.kC2;
                case WorksPhase.Paving: return RRWConst.kC3;
                case WorksPhase.Finishing: return RRWConst.kC4;
                case WorksPhase.BreakUp: return 0f;
                case WorksPhase.Removal: return RRWConst.kD1;
                case WorksPhase.Restore: return RRWConst.kD2;
                default: return 1f;
            }
        }

        public static float PhaseEnd(WorksPhase ph)
        {
            switch (ph)
            {
                case WorksPhase.Survey: return RRWConst.kC1;
                case WorksPhase.Excavation: return RRWConst.kC2;
                case WorksPhase.Foundation: return RRWConst.kC3;
                case WorksPhase.Paving: return RRWConst.kC4;
                case WorksPhase.BreakUp: return RRWConst.kD1;
                case WorksPhase.Removal: return RRWConst.kD2;
                default: return 1f;
            }
        }

        public static float ProgressAt(WorksPhase ph, float f) => math.lerp(PhaseStart(ph), PhaseEnd(ph), math.saturate(f));

        public static WorksKind KindOf(WorksPhase ph) => ph >= WorksPhase.BreakUp && ph <= WorksPhase.Restore ? WorksKind.Demolition : WorksKind.Construction;

        // UI stepper: index within the kind's phases (construction 0..4, demolition 0..2); Complete = count.
        public static int StepIndex(WorksPhase ph) => ph == WorksPhase.Complete ? 5 : ph >= WorksPhase.BreakUp ? (int)ph - (int)WorksPhase.BreakUp : (int)ph;
        public static int StepCount(WorksKind kind) => kind == WorksKind.Construction ? 5 : 3;

        // Localisation key of a phase name (Settings.cs LocaleEN has the English text).
        public static string PhaseKey(WorksPhase ph) => "RealisticRoadWorks.Phase." + ph;

        // Timeline wants the road hidden (RuntimeFlags.HideWanted). The Director applies Hidden from it.
        public static bool IsHidden(WorksKind kind, VisualMode mode, WorksPhase ph)
        {
            if (mode != VisualMode.FullDig) return false;
            if (kind == WorksKind.Construction) return ph == WorksPhase.Survey || ph == WorksPhase.Excavation || ph == WorksPhase.Foundation;
            return ph == WorksPhase.Removal || ph == WorksPhase.Restore || ph == WorksPhase.Complete;
        }

        // ------------------------------------------------------------------ fronts

        public static float Sweep(float f, float a, float b) => math.saturate((f - a) / (b - a));

        // Main work front of a phase, unquantised (machines follow this).
        public static float MainFront(WorksPhase ph, float f, float U)
        {
            switch (ph)
            {
                case WorksPhase.Survey: return U * Sweep(f, 0.15f, 0.95f);     // topsoil strip / loader
                case WorksPhase.Excavation: return U * Sweep(f, 0f, 0.95f);    // excavator face
                case WorksPhase.Foundation: return U * Sweep(f, 0f, 0.95f);    // gravel placed up to here
                case WorksPhase.Paving: return U * Sweep(f, 0.03f, 0.95f);     // paver
                case WorksPhase.Finishing: return U * Sweep(f, 0f, 0.9f);      // line painter; last 10 % demobilisation
                case WorksPhase.BreakUp: return U * Sweep(f, 0f, 0.9f);        // breaker; base exposed behind it
                case WorksPhase.Removal: return U * Sweep(f, 0f, 0.9f);        // base dug out up to here
                case WorksPhase.Restore: return U * Sweep(f, 0f, 0.9f);        // backfill / topsoil
                case WorksPhase.Complete: return U;
                default: return 0f;
            }
        }

        // C4 front with the side swap (C4a painter U*Sweep(f, .03, .40), C4b U*Sweep(f, .47, .86)); the stage index is
        // taken from f (f >= kC4SwapF -> C4b). Use this form where only p is known (machine jobs); prefer MainFront(view).
        public static float MainFront(WorksPhase ph, float f, float U, bool c4Swap) => MainFront(ph, f, U, c4Swap, 0f);

        // With the C4 front floor (StageContext.FrontFloor, applied when the swap is off)
        public static float MainFront(WorksPhase ph, float f, float U, bool c4Swap, float floor)
        {
            if (ph == WorksPhase.Finishing && !c4Swap) return math.max(MainFront(ph, f, U), math.clamp(floor, 0f, U));
            if (ph != WorksPhase.Finishing || !c4Swap) return MainFront(ph, f, U);
            return f < RRWConst.kC4SwapF - RRWConst.kStageSwitchTolF ? U * Sweep(f, RRWConst.kC4aPaintF0, RRWConst.kC4aPaintF1)
                                                                     : U * Sweep(f, RRWConst.kC4bPaintF0, RRWConst.kC4bPaintF1);
        }

        // Main front of a project view (stage-aware: during Vacate the C4a front stays at U).
        public static float MainFront(in ProjectView v)
        {
            if (v.Phase != WorksPhase.Finishing) return MainFront(v.Phase, v.F, v.U);
            // without the swap the plain sweep, never below the C4a front already painted (monotonic in C4)
            if (!v.SwapActive) return math.max(MainFront(v.Phase, v.F, v.U), math.clamp(v.Ctx.FrontFloor, 0f, v.U));
            return StageIndexOf(v) == 0 ? v.U * Sweep(v.F, RRWConst.kC4aPaintF0, RRWConst.kC4aPaintF1)
                                        : v.U * Sweep(v.F, RRWConst.kC4bPaintF0, RRWConst.kC4bPaintF1);
        }

        // Front that props / heaps picked up behind the painter use in C4: with the side swap the C4a front (U in C4b),
        // so nothing the C4a painter's crew picked up reappears when the C4b front restarts at 0.
        public static float PickupFront(in ProjectView v)
        {
            if (v.Phase == WorksPhase.Finishing && v.SwapActive && StageIndexOf(v) == 1) return v.U;
            return MainFront(v);
        }

        // Tape-pulled front (C4 teardown with the swap off): yellow lines and the open half's cover go together.
        public static float TapeFront(in ProjectView v)
        {
            if (v.Phase != WorksPhase.Finishing || v.F < RRWConst.kC4TeardownF) return 0f;
            float t = v.Trim0 + v.TrimmedLength * Sweep(v.F, RRWConst.kC4TeardownF, RRWConst.kC4TapeEndF);
            return t <= v.Trim0 + 1e-3f ? 0f : QuantizeFront(t, v.U);
        }

        public static float SurveyStakeFront(float f, float U) => U * Sweep(f, 0f, 0.4f);   // C0 setting-out cones
        public static float ClearingFront(float f, float U) => U * Sweep(f, 0.05f, 0.6f);   // C0 tree clearing

        // Decal fronts move in kFrontQuantum steps; every layer uses the same quantised value so hand-overs line up.
        public static float QuantizeFront(float u, float U)
        {
            if (u >= U - 1e-3f) return U;
            if (u <= 0f) return 0f;
            return math.floor(u / RRWConst.kFrontQuantum) * RRWConst.kFrontQuantum;
        }

        // Fraction of an edge the front has passed (g): 0 ahead of the edge, 1 behind it.
        public static float LocalFraction(float u, float lo, float hi) => hi - lo <= 1e-3f ? (u >= hi ? 1f : 0f) : math.saturate((u - lo) / (hi - lo));

        // ------------------------------------------------------------------ terrain

        public static float SubgradeDepth(float compositionWidth) =>
            compositionWidth > RRWConst.kLargeRoadWidth ? RRWConst.kSubgradeDepthLarge : RRWConst.kSubgradeDepthSmall;

        // TARGET profile of a HIDDEN mode-A edge. For visible phases it returns Vanilla: Ground does NOT switch to it
        // directly - it runs the reveal hold verified in game (clones kept at kRevealBedY for kRevealHoldUpdates, then the vanilla
        // values on the clones, then the repoint). Never use this to decide the clip on a visible road.
        public static TerrainProfile Terrain(in PlanInput s)
        {
            if (s.Mode != VisualMode.FullDig) return TerrainProfile.Vanilla;
            var ph = PhaseOf(s.Kind, s.P, out float f);
            float U = s.ChainLength;
            float g = EdgeDoneFraction(ph, f, U, s.Crews, s.ChainLo, s.ChainHi);   // per-section fronts
            float dSub = math.min(s.SubgradeDepth, RRWConst.kMaxDepth);
            if (s.Kind == WorksKind.Construction)
            {
                switch (ph)
                {
                    case WorksPhase.Survey:
                        return s.Replaced ? TerrainProfile.Bed(0f) : TerrainProfile.Natural();
                    case WorksPhase.Excavation:
                    {
                        // per edge, eased so the far end of a long edge stays shallow longer and is done when the excavator leaves
                        float e = math.smoothstep(RRWConst.kC1DepthEase0, RRWConst.kC1DepthEase1, g);
                        return s.Replaced ? TerrainProfile.Bed(math.lerp(0f, -dSub, e)) : TerrainProfile.Morph(e, -dSub, s.NatAbove, s.NatBelow);
                    }
                    case WorksPhase.Foundation:
                        // chain-uniform lift: no floor steps at nodes; the gravel front and dump heaps tell the progress
                        return TerrainProfile.Bed(math.lerp(-dSub, RRWConst.kBaseTopY, Sweep(f, 0f, 0.95f)));
                    default: return TerrainProfile.Vanilla;
                }
            }
            switch (ph)
            {
                case WorksPhase.BreakUp: return TerrainProfile.Vanilla;
                case WorksPhase.Removal: return TerrainProfile.Bed(math.lerp(RRWConst.kBaseTopY, -RRWConst.kDemolitionDepth, Sweep(f, 0f, 0.9f))); // chain-uniform
                case WorksPhase.Restore: return TerrainProfile.Morph((1f - g) * s.RestoreT, s.RestoreY, s.NatAbove, s.NatBelow);
                case WorksPhase.Complete: return TerrainProfile.Morph(0f, s.RestoreY, s.NatAbove, s.NatBelow);
                default: return TerrainProfile.Vanilla;
            }
        }

        // ------------------------------------------------------------------ surfaces

        public static SurfaceBand BandOf(SurfaceLayer l) =>
            l == SurfaceLayer.TempMarking ? SurfaceBand.TempLines
            : l == SurfaceLayer.FreshAsphaltCover || l == SurfaceLayer.FreshAsphalt ? SurfaceBand.Carriageway : SurfaceBand.Footprint;

        // Band of a layer for a project view: FreshAsphalt / FreshAsphaltCover use the per-half bands in C4 when the car
        // halves are staged (ProjectView.HalvesActive, dev switch RRWGates.HalfCovers); spans from SurfaceSpanHalf.
        public static SurfaceBand BandOf(SurfaceLayer l, in ProjectView v)
        {
            if ((l == SurfaceLayer.FreshAsphalt || l == SurfaceLayer.FreshAsphaltCover) && v.HalvesActive && RRWGates.HalfCovers)
                return SurfaceBand.CarriageHalf;
            return BandOf(l);
        }

        public static float MarginOf(SurfaceLayer l)
        {
            switch (l)
            {
                case SurfaceLayer.TopsoilStrip: return RRWConst.kTopsoilMargin;
                case SurfaceLayer.Subgrade: return RRWConst.kSubgradeMargin;
                case SurfaceLayer.BaseCourseCover: return RRWConst.kBaseCourseMargin;
                default: return RRWConst.kAsphaltMargin;
            }
        }

        public static bool IsRaisedQueue(SurfaceLayer l) => l == SurfaceLayer.BaseCourseCover || l == SurfaceLayer.FreshAsphaltCover || l == SurfaceLayer.TempMarking;
        public static bool IsRoadsLayer(SurfaceLayer l) => l == SurfaceLayer.BaseCourseCover || l == SurfaceLayer.FreshAsphalt || l == SurfaceLayer.FreshAsphaltCover || l == SurfaceLayer.TempMarking;

        // Layers that become fading scars at Completing (Surfaces re-tags them RRWDerived{site = Null, group Scar}).
        // Construction keeps only the VERGE layers (topsoil, subgrade), fading out within kVergeEndHours (~1 h).
        // Fresh asphalt is NOT kept: the finished road looks vanilla the moment it opens (no dark curing layer).
        public static bool BecomesScar(WorksKind kind, SurfaceLayer l) =>
            kind == WorksKind.Construction
                ? l == SurfaceLayer.TopsoilStrip || l == SurfaceLayer.Subgrade
                : l == SurfaceLayer.TopsoilStrip;

        // Chain span a layer covers this frame (quantised fronts). Mode D: nothing.
        // With more than one crew section a layer is a UNION of per-section pieces (SurfaceSpans); this returns its hull,
        // which is only right for one crew. Surfaces / Dev must move to SurfaceSpans.
        [System.Obsolete("A layer is a union of per-crew pieces; use PhasePlan.SurfaceSpans(layer, view, out SpanSet)")]
        public static Span SurfaceSpan(SurfaceLayer layer, in ProjectView v)
        {
            if (!MultiSection(v)) return SurfaceSpanSingle(layer, v);
            SurfaceSpans(layer, v, out SpanSet set);
            return set.Hull;
        }

        // One crew (and a one-crew cancel layout): the single-front span.
        static Span SurfaceSpanSingle(SurfaceLayer layer, in ProjectView v)
        {
            float U = v.U;
            if (v.Mode != VisualMode.FullDig || U <= 0f) return Span.Empty;
            var ph = v.Phase;
            float F = QuantizeFront(MainFront(v), U);
            float o = RRWConst.kSpanOverlap;
            if (v.Kind == WorksKind.Construction)
            {
                float Fsub = math.max(0f, QuantizeFront(MainFront(ph, v.F, U) - 2f, U));
                switch (layer)
                {
                    case SurfaceLayer.TopsoilStrip:
                        if (ph == WorksPhase.Survey) return new Span(0f, F);
                        if (ph == WorksPhase.Excavation && v.Replaced) return new Span(0f, F);   // verge disturbed behind the excavator
                        return new Span(0f, U);                                              // C1..C4, Complete (-> scar)
                    case SurfaceLayer.Subgrade:
                        if (ph == WorksPhase.Survey) return Span.Empty;
                        if (ph == WorksPhase.Excavation) return new Span(0f, Fsub);
                        return new Span(0f, U);                                              // C2..C4 (verge band), Complete (-> scar)
                    case SurfaceLayer.BaseCourseCover:
                        if (ph == WorksPhase.Excavation && v.Replaced) return new Span(math.max(0f, Fsub - o), U); // old road bed ahead of the dig
                        if (ph == WorksPhase.Survey && v.Replaced) return new Span(0f, U);
                        if (ph == WorksPhase.Foundation) return new Span(0f, F);
                        if (ph == WorksPhase.Paving) return new Span(math.min(math.max(0f, F - o), U - RRWConst.kFrontQuantum), U); // never empty in C3
                        return Span.Empty;
                    case SurfaceLayer.FreshAsphalt:
                        if (ph == WorksPhase.Paving) return new Span(0f, F);
                        // Shrinks with the Cover at the painter front: behind the painter the road is vanilla
                        // (markings and colour), so nothing changes when the road opens. Complete: nothing (no curing scar).
                        // With staged halves the per-half spans apply (SurfaceSpanHalf); this is their envelope.
                        if (ph == WorksPhase.Finishing)
                            return v.HalvesActive ? Union(SurfaceSpanHalfSingle(layer, v, RoadZones.LeftHalf), SurfaceSpanHalfSingle(layer, v, RoadZones.RightHalf))
                                                  : new Span(F, U);
                        return Span.Empty;
                    case SurfaceLayer.FreshAsphaltCover:
                        if (ph == WorksPhase.Paving) return new Span(0f, F);
                        if (ph == WorksPhase.Finishing)                                      // markings appear behind the painter
                            return v.HalvesActive ? Union(SurfaceSpanHalfSingle(layer, v, RoadZones.LeftHalf), SurfaceSpanHalfSingle(layer, v, RoadZones.RightHalf))
                                                  : new Span(F, U);
                        return Span.Empty;
                    case SurfaceLayer.TempMarking:
                    {
                        // Yellow temporary lines along the whole trimmed chain while the C4a open half carries
                        // traffic. None in C4b (the open half has valid white markings); with the swap off they go with
                        // the tape-pulled front at teardown. Surfaces also requires the half APPLIED open (OpenLanesApplied) and
                        // RRWSetting.TempMarkingsOn.
                        if (ph != WorksPhase.Finishing || (v.OpenLanes & RRWConst.kStagedOpenHalf) == 0) return Span.Empty;
                        if (v.SwapActive && StageIndexOf(v) != 0) return Span.Empty;
                        float t0 = math.max(0f, v.Trim0), t1 = math.min(U, v.Trim1 > 0f ? v.Trim1 : U);
                        if (!v.SwapActive) t0 = math.max(t0, TapeFront(v));
                        return new Span(t0, t1);
                    }
                }
                return Span.Empty;
            }

            // ---- demolition
            if (v.Cancelled)
            {
                float cf = QuantizeFront(math.clamp(v.CancelFront, 0f, U), U);
                var cp = v.CancelPhase;
                if (cp == WorksPhase.Paving || cp == WorksPhase.Finishing)
                {
                    // the built road is broken up from the covers that were there at cancel time (no pop)
                    bool fromC3 = cp == WorksPhase.Paving;
                    switch (layer)
                    {
                        case SurfaceLayer.TopsoilStrip: return new Span(0f, U);
                        case SurfaceLayer.Subgrade: return ph == WorksPhase.Restore ? new Span(F, U) : new Span(0f, U);
                        case SurfaceLayer.BaseCourseCover:
                            if (ph == WorksPhase.BreakUp) return fromC3 ? new Span(0f, U) : new Span(0f, F);
                            if (ph == WorksPhase.Removal) return new Span(F, U);
                            return Span.Empty;
                        case SurfaceLayer.FreshAsphalt:
                            // in C4 fresh asphalt only lay ahead of the painter ([cancel front, U]), like the Cover
                            if (ph != WorksPhase.BreakUp) return Span.Empty;
                            return fromC3 ? new Span(F, cf) : new Span(math.max(F, cf), U);
                        case SurfaceLayer.FreshAsphaltCover:
                            if (ph != WorksPhase.BreakUp) return Span.Empty;
                            return fromC3 ? new Span(F, cf) : new Span(math.max(F, cf), U);
                    }
                    return Span.Empty;
                }
                // cancelled while hidden (C0..C2): straight to D2 with exactly the layers that existed at cancel
                switch (layer)
                {
                    case SurfaceLayer.TopsoilStrip:
                        return cp == WorksPhase.Survey ? new Span(0f, cf) : new Span(0f, U);
                    case SurfaceLayer.Subgrade:
                        if (cp == WorksPhase.Survey || ph == WorksPhase.Complete) return Span.Empty;
                        if (cp == WorksPhase.Excavation) return new Span(F, math.max(0f, QuantizeFront(v.CancelFront - 2f, U)));
                        return new Span(F, U);
                    case SurfaceLayer.BaseCourseCover:
                        return cp == WorksPhase.Foundation && ph != WorksPhase.Complete ? new Span(F, cf) : Span.Empty;
                }
                return Span.Empty;
            }
            switch (layer)
            {
                case SurfaceLayer.BaseCourseCover:
                    if (ph == WorksPhase.BreakUp) return new Span(0f, F);
                    if (ph == WorksPhase.Removal) return new Span(F, U);
                    return Span.Empty;
                case SurfaceLayer.Subgrade:
                    if (ph == WorksPhase.BreakUp) return v.F >= 0.92f ? new Span(0f, U) : Span.Empty; // under the road, ready before the hide frame
                    if (ph == WorksPhase.Removal) return new Span(0f, U);
                    if (ph == WorksPhase.Restore) return new Span(F, U);
                    return Span.Empty;
                case SurfaceLayer.TopsoilStrip:
                    if (ph == WorksPhase.Restore) return new Span(0f, math.min(U, F + o));
                    if (ph == WorksPhase.Complete) return new Span(0f, U);   // scar, kept after deletion
                    return Span.Empty;
            }
            return Span.Empty;
        }

        // Legacy convenience (no trims, no cancel data).
        public static Span SurfaceSpan(SurfaceLayer layer, WorksKind kind, VisualMode mode, WorksPhase ph, float f, float U)
        {
            var v = ProjectView.Simple(kind, mode, ph, f, U);
            return SurfaceSpanSingle(layer, v);
        }

        // Chain span of a C4 per-half layer (SurfaceBand.CarriageHalf) on a CHAIN half (LeftHalf / RightHalf).
        //  * FreshAsphalt / FreshAsphaltCover, swap on: C4a works half (kStagedWorksHalf, incl. the centre strip) [F_a, U] shrinking
        //    behind the painter, open half [0, U]; C4b open-first half [F_b, U], the other half nothing (painted).
        //    Swap off: works half [F, U] (plain front), open half [tape front, U] (removed with the yellow lines at teardown).
        //  * TempMarking: SurfaceSpan(TempMarking) on the C4a open half (kStagedOpenHalf), nothing on the other.
        //  Empty unless ProjectView.HalvesActive.
        // Hull of SurfaceSpansHalf with more than one crew (only right for one crew; Surfaces must use SurfaceSpansHalf).
        [System.Obsolete("Use PhasePlan.SurfaceSpansHalf(layer, view, half, out SpanSet)")]
        public static Span SurfaceSpanHalf(SurfaceLayer layer, in ProjectView v, RoadZones half)
        {
            if (!MultiSection(v)) return SurfaceSpanHalfSingle(layer, v, half);
            SurfaceSpansHalf(layer, v, half, out SpanSet set);
            return set.Hull;
        }

        static Span SurfaceSpanHalfSingle(SurfaceLayer layer, in ProjectView v, RoadZones half)
        {
            if (!v.HalvesActive || v.U <= 0f) return Span.Empty;
            float U = v.U;
            bool firstWorks = (half & RRWConst.kStagedWorksHalf) != 0;
            switch (layer)
            {
                case SurfaceLayer.FreshAsphalt:
                case SurfaceLayer.FreshAsphaltCover:
                {
                    float F = QuantizeFront(MainFront(v), U);
                    if (v.SwapActive)
                    {
                        if (StageIndexOf(v) == 0) return firstWorks ? new Span(F, U) : new Span(0f, U);
                        return firstWorks ? Span.Empty : new Span(F, U);
                    }
                    return firstWorks ? new Span(F, U) : new Span(TapeFront(v), U);
                }
                case SurfaceLayer.TempMarking:
                    return (half & RRWConst.kStagedOpenHalf) != 0 ? SurfaceSpanSingle(SurfaceLayer.TempMarking, v) : Span.Empty;
            }
            return Span.Empty;
        }

        static Span Union(Span a, Span b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            return new Span(math.min(a.A, b.A), math.max(a.B, b.B));
        }

        // Maps a chain span onto one edge: s0..s1 in metres from the edge's curve start (t = 0).
        // NEVER drops a piece that touches the edge: a remainder < kMinSpan between the piece and an edge end is
        // absorbed (piece snapped to the end), and a piece shorter than kMinSpan is widened to kMinSpan. Both only enlarge
        // spans, so hand-overs can only overlap more, never open a gap. Returns false when nothing falls on the edge.
        public static bool ToEdgeLocal(Span chain, float u0, float u1, float edgeLength, out float s0, out float s1)
        {
            s0 = s1 = 0f;
            if (chain.IsEmpty) return false;
            float lo = math.min(u0, u1), hi = math.max(u0, u1);
            float a = math.max(chain.A, lo), b = math.min(chain.B, hi);
            if (b - a <= 1e-3f) return false;
            float m = RRWConst.kMinSpan;
            if (hi - lo <= m) { a = lo; b = hi; }
            else
            {
                if (a - lo < m) a = lo;
                if (hi - b < m) b = hi;
                if (b - a < m)
                {
                    b = math.min(hi, a + m);
                    if (b - a < m) a = math.max(lo, b - m);
                }
            }
            float scale = hi - lo > 1e-3f ? edgeLength / (hi - lo) : 1f;
            if (u1 >= u0) { s0 = (a - u0) * scale; s1 = (b - u0) * scale; }
            else { s0 = (u0 - b) * scale; s1 = (u0 - a) * scale; }
            s0 = math.clamp(s0, 0f, edgeLength);
            s1 = math.clamp(s1, 0f, edgeLength);
            return s1 - s0 > 0.05f;
        }

        // Chain coordinate of an edge-local distance s (inverse of ToEdgeLocal for points). s may lie outside [0, len].
        public static float ChainU(float s, float u0, float u1, float edgeLength)
        {
            float len = math.abs(u1 - u0);
            float k = edgeLength > 1e-3f ? s / edgeLength : 0f;
            return u1 >= u0 ? u0 + k * len : u0 - k * len;
        }

        // Edge-local distance of a chain coordinate (clamped to the edge).
        public static float EdgeS(float u, float u0, float u1, float edgeLength)
        {
            float len = math.abs(u1 - u0);
            if (len <= 1e-3f) return 0f;
            float k = u1 >= u0 ? (u - u0) / len : (u0 - u) / len;
            return math.saturate(k) * edgeLength;
        }

        // ------------------------------------------------------------------ wear

        // LaneCondition.m_Wear target for the edge's car sublanes, or -1 to leave it alone. Integer steps only.
        // The Director compares it with the current value every kWearCheckInterval and rewrites ANY difference
        // (vanilla maintenance may repair it in between).
        public static float Wear(in PlanInput s)
        {
            var ph = PhaseOf(s.Kind, s.P, out float f);
            if (s.Mode == VisualMode.Minimal)
            {
                if (s.Kind == WorksKind.Construction)
                {
                    if (ph <= WorksPhase.Foundation) return 6f;
                    if (ph == WorksPhase.Paving) return math.round(6f * (1f - f));
                    return 0f;
                }
                return math.round(10f * math.saturate(s.P));
            }
            if (s.Kind == WorksKind.Construction)
                return ph == WorksPhase.Paving || ph == WorksPhase.Finishing || ph == WorksPhase.Complete ? 0f : -1f;
            if (ph == WorksPhase.BreakUp)
                return math.round(10f * EdgeDoneFraction(ph, f, s.ChainLength, s.Crews, s.ChainLo, s.ChainHi));
            return 10f;
        }

        // ------------------------------------------------------------------ props

        // Device plan. In-lane devices (divider, closed-end lines across the works half, signs,
        // amber head) only while the works car half is closed and drained (InLaneReady); out-of-lane devices (kerb fences) follow
        // OpenLanes at once. During a switch (Swap / Drain) the closed-end lines are down (BarrierStyle.None).
        public static PropPlan Props(in ProjectView v)
        {
            var pp = new PropPlan();
            var ph = v.Phase;
            if (ph == WorksPhase.Complete) return pp;
            var st = Stage(v);
            bool closed = v.Closure == ClosureLevel.Closed;
            RoadZones openCar = v.OpenLanes & RoadZones.Carriageway;
            bool halfOpen = closed && openCar != RoadZones.None && openCar != RoadZones.Carriageway;
            RoadZones worksCar = st.Works & RoadZones.Carriageway & ~v.OpenLanes;
            pp.InLaneReady = worksCar != RoadZones.None && (worksCar & ~v.WorkZonesReady) == 0;
            pp.WorksHalf = halfOpen ? worksCar : RoadZones.None;
            pp.Barriers = !closed ? BarrierStyle.KerbLine
                        : halfOpen ? (pp.InLaneReady ? BarrierStyle.WorksHalf : BarrierStyle.None)
                        : BarrierStyle.AcrossCarriageway;
            // Fences. Trench perimeter while the road is hidden (mode A); kerb fences beside open sidewalks.
            // Per side (FenceSides). The swap happens in the frame OpenLanes changes (same-frame replacement).
            if (v.Mode == VisualMode.FullDig && IsHidden(v.Kind, v.Mode, ph)) pp.Fence = FenceStyle.Footprint;
            else if (closed)
            {
                if ((v.OpenLanes & RoadZones.SidewalkLeft) != 0 && (v.OpenLanes & RoadZones.LeftHalf) == 0) pp.FenceSides |= RoadZones.SidewalkLeft;
                if ((v.OpenLanes & RoadZones.SidewalkRight) != 0 && (v.OpenLanes & RoadZones.RightHalf) == 0) pp.FenceSides |= RoadZones.SidewalkRight;
                if (pp.FenceSides != RoadZones.None) pp.Fence = FenceStyle.Kerbs;
            }
            // Divider cones: staged C4 (also without a car half: they mark the painter's half), in-lane rule
            bool stagedC4 = closed && v.Kind == WorksKind.Construction && v.Mode == VisualMode.FullDig && ph == WorksPhase.Finishing && v.Ctx.Staged;
            pp.Divider = (halfOpen || stagedC4) && pp.InLaneReady ? DividerStyle.Cones : DividerStyle.None;
            pp.CentreCones = pp.Divider == DividerStyle.Cones;
            pp.Signs = halfOpen && pp.InLaneReady;
            pp.ApproachSignals = pp.Signs;
            pp.OpenEntryAtEnd = halfOpen && !RoadZoneMath.TravelsWithChain(openCar, v.LeftHandTraffic);
            pp.StartBarrierKeep = 1f;
            pp.EndBarrierKeep = 1f;
            if (ph == WorksPhase.Finishing || ph == WorksPhase.Restore)
            {
                // pick-up: the crew truck stops at the start line (f .90-.94), then at the end line (f .94-.98)
                pp.StartBarrierKeep = 1f - Sweep(v.F, 0.90f, 0.94f);
                pp.EndBarrierKeep = 1f - Sweep(v.F, 0.94f, 0.98f);
            }
            pp.EdgeConesOnVerge = v.Mode == VisualMode.FullDig && (ph == WorksPhase.Removal || ph == WorksPhase.Restore);
            pp.DumpHeapLoad = ph == WorksPhase.Foundation ? LoadKind.Stone : ph == WorksPhase.Restore ? LoadKind.Ore : LoadKind.None;
            return pp;
        }

        // Slot positions (chain u) of a group: u_k = Trim0 + offset + spacing * k, for u_k <= Trim1 - endGap.
        public static void SlotLayout(PropGroup g, out float offset, out float spacing, out float endGap)
        {
            switch (g)
            {
                case PropGroup.SurveyCones: offset = 2f; spacing = RRWConst.kSurveyConeSpacing; endGap = 2f; return;
                case PropGroup.EdgeCones: offset = 1f; spacing = RRWConst.kMinimalConeSpacing; endGap = 1f; return;
                case PropGroup.SpoilHeaps: offset = 3f; spacing = RRWConst.kSpoilHeapSpacing; endGap = 3f; return;
                case PropGroup.DumpHeaps: offset = 9f; spacing = RRWConst.kDumpHeapSpacing; endGap = 9f; return;
                case PropGroup.StoneWindrow: offset = 3f; spacing = RRWConst.kStoneWindrowSpacing; endGap = 3f; return;
                case PropGroup.Rubble: offset = 8f; spacing = RRWConst.kRubbleSpacing; endGap = 4f; return;
                default: offset = 14f; spacing = 2.5f; endGap = 0f; return;   // CrewProps (3 slots, mode A construction)
            }
        }

        public static int SlotCount(PropGroup g, in ProjectView v)
        {
            if (g == PropGroup.CrewProps) return v.TrimmedLength >= 24f ? 3 : 0;
            if (g >= PropGroup.Divider) return 0;   // laid out by Props from PropPlan (not fixed slots)
            SlotLayout(g, out float off, out float sp, out float gap);
            float len = v.TrimmedLength - off - gap;
            return len < 0f ? 0 : (int)math.floor(len / sp) + 1;
        }

        public static float SlotU(PropGroup g, int k, in ProjectView v)
        {
            SlotLayout(g, out float off, out float sp, out float _);
            if (g == PropGroup.CrewProps && (v.Mode == VisualMode.Minimal || v.Kind == WorksKind.Demolition)) off = 3f;
            return v.Trim0 + off + sp * k;
        }

        // Fill of slot k: -1 = no entity; 1..255 = entity with this Quantity fullness (255 for non-quantity props).
        // Heaps never pop: they grow / shrink through their fill (HeapFill writes Quantity.m_Fullness).
        // Only the C4 survey cones and the crew props read the PropPlan; every other group gets a default plan (no Stage(v)).
        public static int PropFill(PropGroup g, int k, in ProjectView v) =>
            PropFill(g, k, v, g == PropGroup.CrewProps || (g == PropGroup.SurveyCones && v.Phase == WorksPhase.Finishing) ? Props(v) : default(PropPlan));

        // Same fill with the view's PropPlan precomputed (plan MUST equal Props(v)); Props evaluates
        // it once per update instead of once per survey-cone / crew-prop slot (Props(v) calls Stage(v)).
        public static int PropFill(PropGroup g, int k, in ProjectView v, in PropPlan plan)
        {
            if (v.Phase == WorksPhase.Complete) return -1;
            float u = SlotU(g, k, v);
            // = v.Front except C4b with the swap (C4a pick-ups stay picked up). With crews: the per-section fronts of that
            // front (FrontSet): "the front passed x" = the front of the section holding x passed it; one crew = the single-front numbers.
            var F = FrontSet.Pickup(v);
            var ph = v.Phase;
            bool A = v.Mode == VisualMode.FullDig;
            bool constr = v.Kind == WorksKind.Construction;
            float fill = -1f;
            switch (g)
            {
                case PropGroup.SurveyCones:
                    if (!A || !constr) break;
                    if (ph == WorksPhase.Survey) fill = u <= QuantizeFront(SurveyStakeFront(v.F, v.U), v.U) ? 255f : -1f;   // global stake front (no machine)
                    else if (ph == WorksPhase.Finishing) fill = !F.Done(u + 19.7f) && plan.EndBarrierKeep > 0f ? 255f : -1f; // picked up behind the crew
                    else fill = 255f;
                    break;
                case PropGroup.EdgeCones:
                    if (A && constr) break;
                    fill = 255f;
                    break;
                case PropGroup.SpoilHeaps:
                {
                    if (!A) break;
                    // The slots near the chain end only reach a partial size by the end of C1, so
                    // C2/C3 keep the end-of-C1 size (no pop to full size later), and C4 also shrinks every remaining heap over
                    // f .90-.97 (the F-15 sweep never reaches the last ~12 m: they would vanish at full size).
                    // With crews: at the end of C1 every section is done, so the end-of-C1 size does not depend on the layout.
                    float endOfDig = 255f * Sweep(MainFront(WorksPhase.Excavation, 1f, v.U), u + 6f, u + 12f);
                    if (constr)
                    {
                        if (ph == WorksPhase.Excavation) fill = 255f * F.Covered(u + 6f, u + 12f);
                        else if (ph == WorksPhase.Foundation || ph == WorksPhase.Paving) fill = endOfDig;
                        else if (ph == WorksPhase.Finishing) fill = endOfDig * (1f - F.Covered(u + 12f, u + 18f)) * (1f - Sweep(v.F, 0.90f, 0.97f));
                    }
                    else if (v.Cancelled)
                    {
                        var C = FrontSet.Cancel(v);
                        float atCancel = v.CancelPhase == WorksPhase.Survey ? 0f
                                       : v.CancelPhase == WorksPhase.Excavation ? 255f * C.Covered(u + 6f, u + 12f)
                                       : v.CancelPhase == WorksPhase.Finishing ? endOfDig * (1f - C.Covered(u + 12f, u + 18f))
                                       : endOfDig;
                        fill = ph == WorksPhase.Restore ? atCancel * (1f - F.Covered(u - 3f, u + 3f)) : atCancel; // spoil is the backfill
                    }
                    break;
                }
                case PropGroup.DumpHeaps:
                    if (!A) break;
                    if ((constr && ph == WorksPhase.Foundation) || (!constr && !v.Cancelled && ph == WorksPhase.Restore))
                        fill = 255f * F.Covered(u - 12f, u - 6f) * (1f - F.Covered(u - 3f, u + 3f));
                    break;
                case PropGroup.StoneWindrow:
                    if (!A || constr) break;
                    if (ph == WorksPhase.BreakUp) fill = RRWConst.kWindrowFill * F.Covered(u - 2f, u + 4f);
                    else if (ph == WorksPhase.Removal) fill = RRWConst.kWindrowFill * (1f - F.Covered(u - 4f, u));
                    break;
                case PropGroup.Rubble:
                    if (!A || constr) break;
                    if (ph == WorksPhase.BreakUp) fill = F.Done(u + 9f) ? 255f : -1f;
                    // Only the piles that existed at the end of D0 (MainFront(BreakUp, 1) >= u + 9);
                    // the slots in the last ~9 m never existed in D0 and popped IN in the first D1 frame (rubble cannot grow)
                    else if (ph == WorksPhase.Removal) fill = !F.Done(u - 2f) && MainFront(WorksPhase.BreakUp, 1f, v.U) >= u + 9f ? 255f : -1f;
                    break;
                case PropGroup.CrewProps:
                    fill = plan.StartBarrierKeep > 0f ? 255f : -1f;
                    break;
            }
            if (fill < 1f) return -1;
            return (int)math.min(255f, math.round(fill));
        }

        // C2/D2: the dump-heap slot the next truck should tip at (the first slot that is growing), NaN = none.
        // Crew 0's slot; with more than one crew use ActiveDumpSlot(view, crew).
        [System.Obsolete("Use PhasePlan.ActiveDumpSlot(view, crew)")]
        public static float ActiveDumpSlot(in ProjectView v) => ActiveDumpSlot(v, 0);

        // The slot crew `crew` tips at: the first slot ahead of its front (u >= F_i + 6) whose shrink window
        // [u - 3, u + 3] still lies in its section (one crew: any slot, the single-crew rule). NaN = none (Machines: F_i + 6).
        public static float ActiveDumpSlot(in ProjectView v, int crew)
        {
            int nC = v.CrewCount;
            crew = math.clamp(crew, 0, nC - 1);
            float F = CrewFront(v, crew);
            SlotLayout(PropGroup.DumpHeaps, out float off, out float sp, out float _);
            int n = SlotCount(PropGroup.DumpHeaps, v);
            float first = v.Trim0 + off;
            int k = (int)math.ceil((F + 6f - first) / sp);
            if (k < 0) k = 0;
            if (k >= n) return float.NaN;
            float u = first + sp * k;
            if (nC > 1 && crew < nC - 1 && u + 3f > SectionStart(crew + 1, nC, v.U)) return float.NaN;
            return u;
        }

        // ------------------------------------------------------------------ crews

        static CrewSlot Slot(MachineActivity a, float u, float lat, sbyte facing, LoadKind load = LoadKind.None) =>
            new CrewSlot { Activity = a, U = u, Lateral = lat, Facing = facing, Load = load };

        // Parking slots inside the trimmed chain: slot k at Trim0 + 6 + 12 k (capped to the first half of short chains).
        public static float StartSlot(int k, in ProjectView v) => v.Trim0 + math.min(6f + 12f * k, math.max(2f, v.TrimmedLength * 0.5f));
        // Mirror image at the chain end (trucks that feed the front from ahead wait here).
        public static float EndSlot(int k, in ProjectView v) => v.Trim1 - math.min(6f + 12f * k, math.max(2f, v.TrimmedLength * 0.5f));

        // Lateral encoding (CrewSlot.Lateral): |v| <= 0.5 = fraction of the flat floor WIDTH (+0.5 = right floor edge);
        // v = -1 / +1 = on the left / right verge (HalfWidth + kVergeOffset, ground Y). Lanes:
        //   right lane +0.35: trucks;  left lane -0.35: excavator, TruckB travel;  centre 0: finisher, topsoil loader, C3 feeding truck
        public const float kRightLane = 0.35f, kLeftLane = -0.35f, kLeftVerge = -1f, kRightVerge = 1f;
        // Staged opening: lane of the visible-road crew (C3/C4) inside the chain-RIGHT (works) half, chosen so a
        // CoalTruck01 / RoadMaintenanceVehicle01 box stays inside the carriageway on a 12 m road (sidewalks can open) and clear
        // of the centre line (the chain-left half can open in C4). Machines maps it to the carriageway and may tighten it.
        public const float kWorksLane = 0.22f;

        // Slots inside a crew's working range [lo, hi] (the section clipped to the trims); one crew: StartSlot / EndSlot.
        public static float StartSlotIn(int k, float lo, float hi) => lo + math.min(6f + 12f * k, math.max(2f, (hi - lo) * 0.5f));
        public static float EndSlotIn(int k, float lo, float hi) => hi - math.min(6f + 12f * k, math.max(2f, (hi - lo) * 0.5f));

        // A follow anchor that is back at `home` (u) by phase fraction `fEnd` (minus kCrewReturnMarginSeconds) when the
        // role drives back at kCrewReturnSpeedFactor x its VFwd. The anchor itself therefore never moves back faster than that, and
        // the role is where the next step needs it (C2 spread start, C4 start, the start-barrier pick-up). Unknown duration
        // (WorkSeconds 0): `follow` unchanged.
        public static float ReturnAware(in ProjectView v, float follow, float home, float fEnd, MachineRole role)
        {
            float ps = v.PhaseSeconds;
            if (ps <= 0f || follow <= home) return follow;
            float tLeft = math.max(0f, (fEnd - v.F) * ps - RRWConst.kCrewReturnMarginSeconds);
            var lim = MachineLimits.Of(role, v.Phase);
            // A follow-back is usually driven in reverse (no room to turn on a works lane), so the anchor
            // never moves back faster than kCrewReturnRevFactor x the role's reversing limit (crew truck: 1.656 m/s, not
            // 0.5 x 6 = 3 m/s); Machines' Ret leg cap uses the same value.
            float vRet = math.min(RRWConst.kCrewReturnSpeedFactor * lim.VFwd, RRWConst.kCrewReturnRevFactor * lim.VRev);
            return math.min(follow, home + tLeft * vRet);
        }

        // One-crew plan (crew 0). Obsolete: plan every crew with Crew(view, crew) for crew in 0..view.CrewCount-1.
        [System.Obsolete("Plan each crew with PhasePlan.Crew(view, crew), crew < view.CrewCount")]
        public static CrewPlan Crew(in ProjectView v) => Crew(v, 0);

        // The plan of crew `crew` (section `crew` of view.CrewCount). Every front-relative anchor uses the crew's
        // section front F_i; parking / base / end slots are section-relative (StartSlotIn / EndSlotIn over [SecLo, SecHi]); DriveOut
        // anchors stay the connected chain exit (ExitU). Project-level duties: crew 0's crew truck picks up the START barriers, the
        // LAST crew's finisher the END barriers (C4 teardown). With one crew and an unknown duration this is the single-crew plan, except
        // for the C4 teardown (the painter, not the crew truck, stands at the end barriers; one vehicle cannot cover both
        // chain ends within its speed limit).
        public static CrewPlan Crew(in ProjectView v, int crew)
        {
            var c = new CrewPlan();
            var ph = v.Phase;
            int n = v.CrewCount;
            crew = math.clamp(crew, 0, n - 1);
            c.Crew = crew; c.Crews = n;
            float lo = v.Trim0, hi = v.Trim1;
            if (n > 1)
            {
                lo = math.max(lo, SectionStart(crew, n, v.U));
                hi = math.min(hi, SectionStart(crew + 1, n, v.U));
            }
            c.SecLo = lo; c.SecHi = hi;
            float L = hi - lo;
            bool first = crew == 0, last = crew == n - 1;
            if (v.Mode != VisualMode.FullDig || ph == WorksPhase.Complete || L < RRWConst.kShortEdge) return c;
            float F = CrewFront(v, crew);   // stage-aware (C4a / C4b painter sweeps); this crew's section front
            float f = v.F;
            bool constr = v.Kind == WorksKind.Construction;
            c.FrontU = F;
            c.MaxTrucks = L >= 60f ? 2 : L >= 20f ? 1 : 0;
            c.BaseSlotU = StartSlotIn(1, lo, hi); c.BaseLateralA = kRightLane;
            c.BaseSlotUB = StartSlotIn(2, lo, hi); c.BaseLateralB = kLeftLane;
            // Drive-outs end at the chain end INSIDE the trimmed chain (Machines removes the puppet there: out of
            // sight, or at the latest kPostSiteMaxSimFrames later). Nothing is ever sent onto the junction or a finished road.
            // The end that CONNECTS to the road network (ExitU), never a dead end; facing towards it.
            float exit = ExitU(v);
            sbyte exitFacing = ExitFacing(v);
            if (L < RRWConst.kMachineMinChain)
            {
                if (constr) c.CrewTruck = Slot(MachineActivity.Parked, StartSlotIn(0, lo, hi), kLeftVerge, 1);
                return c;
            }
            float lead = RRWConst.kFinisherLead;
            float back = RRWConst.kExcavatorBack;
            if (constr)
            {
                c.CrewTruck = Slot(MachineActivity.Parked, StartSlotIn(0, lo, hi), kLeftVerge, 1);
                switch (ph)
                {
                    case WorksPhase.Survey:
                        c.Loader = f < 0.15f ? Slot(MachineActivity.Parked, StartSlotIn(1, lo, hi), 0f, 1) : Slot(MachineActivity.Scrape, F - 5f, 0f, 1);
                        break;
                    case WorksPhase.Excavation:
                    {
                        c.Excavator = Slot(MachineActivity.Dig, F - back, kLeftLane, 1);
                        c.TruckA = Slot(MachineActivity.LoadAtFront, F - back, kRightLane, -1, LoadKind.Ore);
                        c.TruckB = Slot(MachineActivity.Shuttle, F - back, kRightLane, -1, LoadKind.Ore);
                        // The grader follows on the left verge but is back near the section start when C2 begins, where it
                        // spreads from F + 1. Its home is StartSlot 2 (+30 m), NOT StartSlot 1
                        // (+18 m): the crew props / crew depots stand on the same left verge at +12.5..+20.5 m (cones included).
                        float loaderU = v.PhaseSeconds > 0f
                            ? math.max(StartSlotIn(2, lo, hi), ReturnAware(v, F - 30f, StartSlotIn(2, lo, hi), 1f, MachineRole.Loader))
                            : math.max(StartSlotIn(2, lo, hi), F - 30f);
                        c.Loader = Slot(MachineActivity.Follow, loaderU, kLeftVerge, 1);
                        break;
                    }
                    case WorksPhase.Foundation:
                    {
                        // parked at the far end on the LEFT VERGE (on the floor lane it was a wall the grader's
                        // Spread could not pass, GraderClamp stopped it ~14.5 m short). Verge blocked -> Machines' VergeSlot
                        // slides inward / falls back to the floor lane -0.45 (then GraderClamp applies again).
                        // With crews: at the end of its own section (where its C1 dig ended).
                        c.Excavator = Slot(MachineActivity.Parked, hi - 8f, kLeftVerge, -1);
                        // From f = 0.95 the grader parks OFF the carriageway on the left verge, 40 m
                        // before the section end (never past the section middle) and stays there in C3 until out of view.
                        c.Loader = f >= 0.95f ? Slot(MachineActivity.Parked, math.max(0.5f * (lo + hi), hi - 40f), kLeftVerge, -1)
                                              : Slot(MachineActivity.Spread, F + 1f, -0.15f, 1);
                        float dump = ActiveDumpSlot(v, crew);
                        float a = float.IsNaN(dump) ? F + 6f : dump;
                        c.TruckA = Slot(MachineActivity.DumpAtFront, a, kRightLane, 1, LoadKind.Stone);
                        c.TruckB = Slot(MachineActivity.Shuttle, a, kRightLane, 1, LoadKind.Stone);
                        c.BaseSlotU = EndSlotIn(1, lo, hi); c.BaseSlotUB = EndSlotIn(2, lo, hi);
                        // Base-course roller behind the grader; at f >= kRollerC2HandOffF it parks at the hand-off spot at
                        // its section end (crew i + 1's C3 breakdown roller takes it over) or, for the last crew, drives out
                        if (RollerCountFor(v, L) > 0)
                        {
                            c.Rollers = 1;
                            if (f >= RRWConst.kRollerC2HandOffF)
                                c.Roller0 = last ? RollerParked(RollerDuty.BaseCourse, MachineActivity.DriveOut, exit, exitFacing, false)
                                                 : RollerParked(RollerDuty.BaseCourse, MachineActivity.Parked, hi - RRWConst.kRollerHandOffBack, 1, false);
                            else c.Roller0 = RollerCompact(RollerDuty.BaseCourse, F, lo, hi, float.NaN, false);
                        }
                        break;
                    }
                    case WorksPhase.Paving:
                    {
                        // Every box stays inside the carriageway (the sidewalks open from the reveal)
                        c.Finisher = Slot(MachineActivity.Pave, F + lead, 0f, 1);
                        c.TruckA = Slot(MachineActivity.DumpAtFront, F + lead + 3.7f + 0.4f + 4.5f, 0f, 1, LoadKind.Coal); // feeding the hopper
                        c.TruckB = Slot(MachineActivity.Wait, F + 25f, kWorksLane, 1, LoadKind.Coal);
                        // The grader (Loader = excavator clone) never drives on fresh asphalt: it leaves at the C2 -> C3
                        // hand-over (DriveOut roles are never spawned; a live one is made a leaver by Machines).
                        c.Loader = Slot(MachineActivity.DriveOut, exit, 0f, exitFacing);
                        // The crew truck is back at its C4 start slot when C3 ends (return-aware follow). A
                        // crew > 0 comes home kC3InteriorHomeShift further in (crew i-1's paver ends its section at B_i + 1.7)
                        float s0 = StartSlotIn(0, lo, hi) + (crew > 0 ? RRWConst.kC3InteriorHomeShift : 0f);
                        // With rollers between the paver and the crew truck, the crew truck follows further back
                        int nr = RollerCountFor(v, L);
                        float ctBack = nr >= 2 ? RRWConst.kC3CrewTruckBackTwoRollers : nr == 1 ? RRWConst.kC3CrewTruckBackOneRoller : 32f;
                        float ctU = math.max(s0, ReturnAware(v, F - ctBack, s0, 1f, MachineRole.CrewTruck));
                        c.CrewTruck = Slot(MachineActivity.Follow, ctU, kWorksLane, 1);
                        c.BaseSlotU = EndSlotIn(1, lo, hi); c.BaseSlotUB = EndSlotIn(2, lo, hi);
                        if (nr > 0)
                        {
                            // breakdown roller right behind the paver (>= ~6 m behind its screed), finish roller further back; both
                            // stay ahead of the crew truck. Before its window opens: crew 0 has none (it spawns at the chain gate
                            // when it opens), a crew > 0 keeps a RELAY slot at the hand-off spot where crew i - 1's C2 roller parked.
                            bool halfOnly = f >= RRWConst.kRollerC3WorksHalfF;
                            c.Rollers = nr;
                            c.Roller0 = RollerCompact(RollerDuty.Breakdown, F, lo, hi, ctU, halfOnly);
                            if (!c.Roller0.Active && crew > 0 && f < RRWConst.kRollerC3WorksHalfF)
                            {
                                c.Roller0 = RollerParked(RollerDuty.Breakdown, MachineActivity.Parked, lo - RRWConst.kRollerHandOffBack, 1, false);
                                c.Roller0.RelayOnly = true;
                            }
                            if (nr >= 2) c.Roller1 = RollerCompact(RollerDuty.Finish, F, lo, hi, ctU, halfOnly);
                        }
                        break;
                    }
                    case WorksPhase.Finishing:
                    {
                        // The whole C4 crew works in the works half (kStagedWorksHalf), so the other half opens
                        // to traffic as soon as Machines report it clear (PhasePlan.OpenLanes). The early leavers drive to the
                        // connected chain end along the works half; the painter paints from the works half.
                        // With the side swap, C4a = works half kStagedWorksHalf; the C4a crew drives out at
                        // f in [kC4aPaintF1, kC4SwapF) (Vacate); C4b mirrors the laterals (-kWorksLane) and paints U*Sweep(f, .47, .86).
                        // Machines map the fraction onto the half relative to EdgeSection.DirSplit.
                        // Teardown (f >= kC4TeardownF): crew 0's crew truck stands at the start barriers (lo + 4, f .90-.98;
                        // it returns in time: ReturnAware towards StartSlot 0), the LAST crew's painter at the end barriers
                        // (hi - 14, f .90-.98; it finished its sweep right there); everyone else drives out; all leave at f .98.
                        int idx = v.SwapActive ? StageIndexOf(v) : 0;
                        float lat0 = (RRWConst.kStagedWorksHalf & RoadZones.RightHalf) != 0 ? kWorksLane : -kWorksLane;
                        float lat = idx == 1 ? -lat0 : lat0;
                        bool vacate = v.SwapActive && idx == 0 && f >= RRWConst.kC4aPaintF1;
                        bool teardown = f >= RRWConst.kC4TeardownF;
                        bool gone = f >= 0.98f;
                        float s0 = StartSlotIn(0, lo, hi);
                        if (vacate)
                        {
                            c.Finisher = Slot(MachineActivity.DriveOut, exit, lat, exitFacing);
                            c.CrewTruck = Slot(MachineActivity.DriveOut, exit, lat, exitFacing);
                        }
                        else if (!teardown)
                        {
                            c.Finisher = Slot(MachineActivity.Paint, F + lead, lat, 1);
                            float follow = math.max(s0, F - 15f);
                            if (first) follow = math.max(s0, ReturnAware(v, F - 15f, s0, RRWConst.kC4TeardownF, MachineRole.CrewTruck));
                            c.CrewTruck = Slot(MachineActivity.Follow, follow, lat, 1);
                        }
                        else
                        {
                            c.Finisher = last && !gone ? Slot(MachineActivity.Parked, hi - 14f, lat, 1)        // end barriers
                                                       : Slot(MachineActivity.DriveOut, exit, lat, exitFacing);
                            c.CrewTruck = first && !gone ? Slot(MachineActivity.Parked, lo + 4f, lat, -1)       // start barriers
                                                         : Slot(MachineActivity.DriveOut, exit, lat, exitFacing);
                        }
                        if (f < 0.1f && idx == 0)
                        {
                            // the C3 feed trucks leave (the grader already left in C3)
                            c.TruckA = Slot(MachineActivity.DriveOut, exit, lat, exitFacing);
                            c.TruckB = Slot(MachineActivity.DriveOut, exit, lat, exitFacing);
                        }
                        c.BaseSlotU = EndSlotIn(1, lo, hi); c.BaseSlotUB = EndSlotIn(2, lo, hi);
                        break;
                    }
                }
            }
            else
            {
                c.MaxTrucks = math.min(c.MaxTrucks, 1);   // demolition: single truck lane, no crew truck
                bool leaving = f >= 0.9f && ph == WorksPhase.Restore;
                switch (ph)
                {
                    case WorksPhase.BreakUp:
                        c.Excavator = Slot(MachineActivity.Break, F - back, kLeftLane, 1);
                        c.TruckA = Slot(MachineActivity.LoadAtFront, F - back, kRightLane, -1, LoadKind.Stone);
                        break;
                    case WorksPhase.Removal:
                        c.Excavator = Slot(MachineActivity.Dig, F - back, kLeftLane, 1);
                        c.TruckA = Slot(MachineActivity.LoadAtFront, F - back, kRightLane, -1, LoadKind.Stone);
                        break;
                    case WorksPhase.Restore:
                    {
                        c.Excavator = f < 0.1f ? Slot(MachineActivity.DriveOut, exit, kLeftLane, exitFacing) : default;
                        c.Loader = leaving ? Slot(MachineActivity.DriveOut, exit, kLeftLane, exitFacing)
                                           : Slot(MachineActivity.Spread, F + 1f, -0.15f, 1);
                        if (!leaving && !v.Cancelled)
                        {
                            float dump = ActiveDumpSlot(v, crew);
                            c.TruckA = Slot(MachineActivity.DumpAtFront, float.IsNaN(dump) ? F + 6f : dump, kRightLane, 1, LoadKind.Ore);
                        }
                        c.BaseSlotU = EndSlotIn(1, lo, hi); c.BaseSlotUB = EndSlotIn(2, lo, hi);
                        // Optional duty: backfill compaction behind the loader; leaves with the others at f >= .9
                        if (RollerCountFor(v, L) > 0)
                        {
                            c.Rollers = 1;
                            c.Roller0 = leaving ? RollerParked(RollerDuty.Backfill, MachineActivity.DriveOut, exit, exitFacing, false)
                                                : RollerCompact(RollerDuty.Backfill, F, lo, hi, float.NaN, false);
                        }
                        break;
                    }
                }
            }
            if (c.MaxTrucks < 2) c.TruckB = default;
            if (c.MaxTrucks < 1) c.TruckA = default;
            return c;
        }

        // ------------------------------------------------------------------ road rollers

        // Rollers a crew of section length L plans this phase (0 = none): C2 one, C3 one or two (two from kRollerTwoMinSection), D2
        // one (kRollerInRestore), never in other phases, mode D, short sections or with view.Rollers == 0 (setting off).
        public static int RollerCountFor(in ProjectView v, float sectionLength)
        {
            if (v.Rollers <= 0 || v.Mode != VisualMode.FullDig || sectionLength < RRWConst.kMachineMinChain) return 0;
            int max = math.min(v.Rollers, RRWConst.kMaxRollersPerCrew);
            switch (v.Phase)
            {
                case WorksPhase.Foundation: return 1;
                case WorksPhase.Paving: return sectionLength >= RRWConst.kRollerTwoMinSection ? max : 1;
                case WorksPhase.Restore: return RRWConst.kRollerInRestore && v.Kind == WorksKind.Demolition ? 1 : 0;
                default: return 0;
            }
        }

        // Pass window (pivot range, chain u) of a roller duty for crew front F in section [secLo, secHi] (trimmed). crewTruckU: the C3
        // crew truck anchor (NaN = none on the floor): the window stays kRollerCrewTruckGap ahead of it. Returns false (lo / hi still
        // set) when the window is shorter than kRollerMinPass: "not open". Job form: Machines calls it with the front at a leg's
        // planned end time.
        public static bool RollerWindow(RollerDuty duty, float F, float secLo, float secHi, float crewTruckU, out float lo, out float hi)
        {
            float back, len, top;
            switch (duty)
            {
                case RollerDuty.BaseCourse:
                case RollerDuty.Backfill:
                    back = RRWConst.kRollerC2Back; len = RRWConst.kRollerC2PassLength; top = secHi - RRWConst.kRollerC2EndClear; break;
                case RollerDuty.Breakdown:
                    back = RRWConst.kRollerC3Back0; len = RRWConst.kRollerC3Pass0; top = secHi - RRWConst.kRollerEndClear; break;
                case RollerDuty.Finish:
                    back = RRWConst.kRollerC3Back1; len = RRWConst.kRollerC3Pass1; top = secHi - RRWConst.kRollerEndClear; break;
                default:
                    lo = hi = float.NaN; return false;
            }
            float bottom = secLo + RRWConst.kRollerEndClear;
            if (!float.IsNaN(crewTruckU)) bottom = math.max(bottom, crewTruckU + RRWConst.kRollerCrewTruckGap);
            hi = math.min(F - back, top);
            lo = math.max(bottom, hi - len);
            return hi - lo >= RRWConst.kRollerMinPass - 1e-3f;
        }

        // Pass speed / vibration of a duty.
        public static float RollerPassSpeed(RollerDuty duty) =>
            duty == RollerDuty.Breakdown ? RRWConst.kRollerSpeedBreakdown : duty == RollerDuty.Finish ? RRWConst.kRollerSpeedFinish
          : duty == RollerDuty.Backfill ? RRWConst.kRollerSpeedBackfill : RRWConst.kRollerSpeedBase;
        public static bool RollerVibrates(RollerDuty duty) => duty != RollerDuty.Finish && duty != RollerDuty.None;

        static RollerSlot RollerCompact(RollerDuty duty, float F, float lo, float hi, float crewTruckU, bool worksHalfOnly)
        {
            var r = new RollerSlot { Duty = duty, PassSpeed = RollerPassSpeed(duty), Vibrate = RollerVibrates(duty), WorksHalfOnly = worksHalfOnly,
                                     WinLo = float.NaN, WinHi = float.NaN };
            if (!RollerWindow(duty, F, lo, hi, crewTruckU, out float a, out float b)) return r;   // not open: Activity None
            r.WinLo = a; r.WinHi = b;
            r.Slot = Slot(MachineActivity.Compact, a, 0f, 1);
            return r;
        }

        static RollerSlot RollerParked(RollerDuty duty, MachineActivity act, float u, sbyte facing, bool worksHalfOnly) => new RollerSlot
        {
            Slot = Slot(act, u, 0f, facing), Duty = duty, PassSpeed = RollerPassSpeed(duty), Vibrate = false, WorksHalfOnly = worksHalfOnly,
            WinLo = float.NaN, WinHi = float.NaN,
        };

        // Lateral (chain frame, m) of outbound pass k inside the roller CENTRE range [lo, hi] (Machines derives it from the floor /
        // carriageway minus half the roller width and its clearances; WorksHalfOnly: the works half only). A triangle wave over evenly
        // spaced positions <= kRollerPassShift apart, bouncing at both ends: overlapping passes over the whole width. Seed the start
        // with k += seed. Degenerate range: its middle. Job-safe.
        public static float RollerPassLateral(int k, float lo, float hi, float shift)
        {
            if (!(hi - lo > 1e-3f) || !(shift > 1e-3f)) return 0.5f * (lo + hi);
            int m = (int)math.ceil((hi - lo) / shift - 1e-4f) + 1;   // positions, >= 2
            float step = (hi - lo) / (m - 1);
            int period = 2 * (m - 1);
            int j = ((k % period) + period) % period;
            int idx = j < m ? j : period - j;
            return lo + step * idx;
        }

        // ------------------------------------------------------------------ crew sections
        //
        // A project of n crews is split into sections [B_i, B_i+1] of [0, U] (B_0 = 0, B_n = U, interior B_i = U*i/n rounded to the
        // kFrontQuantum grid). All sections share the phase's unit sweep s = MainFront(view) / U (stage-aware, FrontFloor included);
        // crew i's front is F_i = B_i + (B_i+1 - B_i) * s, its quantised decal front QuantizeSection(F_i, B_i, B_i+1). A point x is
        // DONE when the front of the section holding it passed it (x in (B_i, B_i+1] belongs to section i; x <= 0 is always done).
        // One crew: F_0 = MainFront(view) exactly, and every function below returns the single-front value bit for bit.

        // Chain u of section boundary i (0..n).
        public static float SectionStart(int i, int n, float U)
        {
            if (i <= 0 || n <= 1) return i <= 0 ? 0f : U;
            if (i >= n) return U;
            float q = RRWConst.kFrontQuantum;
            return math.clamp(q * math.round(U * i / (n * q)), 0f, U);
        }

        // Section holding chain u ((B_i, B_i+1] convention; u <= 0 -> 0, u >= U -> n - 1).
        public static int SectionOf(float u, int n, float U)
        {
            if (n <= 1 || u <= 0f) return 0;
            for (int i = 0; i < n - 1; i++) if (u <= SectionStart(i + 1, n, U)) return i;
            return n - 1;
        }

        public static int CrewOf(in ProjectView v, float u) => SectionOf(u, v.CrewCount, v.U);

        // Section-relative quantisation (interior boundaries lie on the grid, so this equals QuantizeFront clamped into [a, b]).
        public static float QuantizeSection(float u, float a, float b)
        {
            if (u >= b - 1e-3f) return b;
            if (u <= a) return a;
            return math.max(a, math.floor(u / RRWConst.kFrontQuantum) * RRWConst.kFrontQuantum);
        }

        // Front of crew i (unquantised). One crew: MainFront(view).
        public static float CrewFront(in ProjectView v, int i)
        {
            int n = v.CrewCount;
            float mf = MainFront(v);
            if (n <= 1) return mf;
            i = math.clamp(i, 0, n - 1);
            float a = SectionStart(i, n, v.U), b = SectionStart(i + 1, n, v.U);
            return a + (b - a) * (v.U > 0f ? math.saturate(mf / v.U) : 0f);
        }

        // Job form (Machines' FrontRef): crew `crew` of `crews` with the primitive arguments of the single-front form.
        public static float SectionFront(WorksPhase ph, float f, float U, int crews, int crew, bool c4Swap, float floor)
        {
            float mf = MainFront(ph, f, U, c4Swap, floor);
            if (crews <= 1 || !(U > 0f)) return mf;
            crews = math.min(crews, RRWConst.kMaxCrewsPerProject);
            crew = math.clamp(crew, 0, crews - 1);
            float a = SectionStart(crew, crews, U), b = SectionStart(crew + 1, crews, U);
            return a + (b - a) * math.saturate(mf / U);
        }

        // Fraction of the chain interval [lo, hi] (an edge) that is done (generalised LocalFraction; Ground C1 / D2 depth, D0 wear).
        public static float EdgeDoneFraction(WorksPhase ph, float f, float U, int crews, float lo, float hi)
        {
            float F = MainFront(ph, f, U);
            if (crews <= 1) return LocalFraction(F, lo, hi);
            var fs = FrontSet.Of(F, crews, U);
            if (hi - lo <= 1e-3f) return fs.Done(hi) ? 1f : 0f;
            return fs.Covered(lo, hi);
        }

        // More than one section anywhere in the view (current layout or the cancel layout).
        public static bool MultiSection(in ProjectView v) => v.CrewCount > 1 || (v.Cancelled && v.CancelCrewCount > 1);

        // ---- crew count

        // Fraction of the phase over which the front crosses its section (the MainFront sweep window).
        public static float FrontSweepFraction(WorksPhase ph, bool c4Swap)
        {
            switch (ph)
            {
                case WorksPhase.Survey: return 0.95f - 0.15f;
                case WorksPhase.Excavation:
                case WorksPhase.Foundation: return 0.95f;
                case WorksPhase.Paving: return 0.95f - 0.03f;
                case WorksPhase.Finishing:
                    return c4Swap ? math.min(RRWConst.kC4aPaintF1 - RRWConst.kC4aPaintF0, RRWConst.kC4bPaintF1 - RRWConst.kC4bPaintF0) : 0.9f;
                case WorksPhase.BreakUp:
                case WorksPhase.Removal:
                case WorksPhase.Restore: return 0.9f;
                default: return 0f;
            }
        }

        // Front speed (m/s machine time) ONE crew would need over the whole chain U (0 = unknown duration).
        public static float SingleFrontSpeed(WorksPhase ph, float U, float workSeconds, bool c4Swap)
        {
            float ps = workSeconds * (PhaseEnd(ph) - PhaseStart(ph));
            float w = FrontSweepFraction(ph, c4Swap);
            return ps > 0f && w > 0f && U > 0f ? U / (w * ps) : 0f;
        }

        // Crew count of a phase (the Director latches it per phase in ProjectRecord.Crews):
        //   n = max(ceil(vSingle / vWork(front owner)), ceil(trimmed / kMaxSectionLength)),
        //   capped by maxCrews (setting), kMaxCrewsPerProject and floor(trimmed / kMinSectionLength) (>= 1).
        // Mode D / Complete / no chain: 1. Unknown duration (workSeconds 0): the length rule only.
        public static int CrewCountFor(WorksKind kind, VisualMode mode, WorksPhase ph, float U, float trimmedLength, float workSeconds,
                                       bool c4Swap, int maxCrews)
        {
            if (mode != VisualMode.FullDig || !(U > 0f) || ph == WorksPhase.Complete || ph == WorksPhase.None) return 1;
            float L = trimmedLength > 0f ? math.min(trimmedLength, U) : U;
            int cap = math.min(math.clamp(maxCrews, 1, RRWConst.kMaxCrewsPerProject), math.max(1, (int)math.floor(L / RRWConst.kMinSectionLength)));
            int n = (int)math.ceil(L / RRWConst.kMaxSectionLength - 1e-4f);
            float v1 = SingleFrontSpeed(ph, U, workSeconds, c4Swap);
            float vw = MachineLimits.WorkSpeed(kind, ph);
            if (v1 > 0f && vw > 0f) n = math.max(n, (int)math.ceil(v1 / vw - 1e-4f));
            return math.clamp(n, 1, cap);
        }

        public static int CrewCount(in ProjectView v, int maxCrews) =>
            CrewCountFor(v.Kind, v.Mode, v.Phase, v.U, v.TrimmedLength, v.WorkSeconds, v.Phase == WorksPhase.Finishing && v.SwapActive, maxCrews);

        // Front speed of ONE crew of the view's layout (m/s; 0 = unknown). Machines compares it with MachineLimits (overWork / frontLag).
        public static float CrewFrontSpeed(in ProjectView v) =>
            SingleFrontSpeed(v.Phase, v.U, v.WorkSeconds, v.Phase == WorksPhase.Finishing && v.SwapActive) / v.CrewCount;

        // Machine seconds from the phase start until the fronts leave their section starts (lead-in of the sweep window).
        public static float LeadInSeconds(in ProjectView v)
        {
            float ps = v.PhaseSeconds;
            if (ps <= 0f) return 0f;
            switch (v.Phase)
            {
                case WorksPhase.Survey: return 0.15f * ps;
                case WorksPhase.Paving: return 0.03f * ps;
                case WorksPhase.Finishing:
                    if (!v.SwapActive) return 0f;
                    return StageIndexOf(v) == 0 ? RRWConst.kC4aPaintF0 * ps : (RRWConst.kC4bPaintF0 - RRWConst.kC4SwapF) * ps;
                default: return 0f;
            }
        }

        // Phase hand-over budget: a live puppet keeps (or is re-mapped to) a role slot it can reach within this many
        // machine seconds at its MachineLimits; otherwise it leaves and the role spawns at its anchor.
        public static float HandOverSeconds(in ProjectView v) => LeadInSeconds(v) + RRWConst.kHandOverSlackSeconds;

        // ---- latched crew count of the running phase (saved in SiteFlags bits 12..15)
        // 0 = nothing saved for this phase (older save, or bits of a different phase: the parity bit tells adjacent phases apart).
        public static int LatchedCrewsOf(SiteFlags f, WorksPhase ph)
        {
            int n = ((int)f >> 12) & 7;
            if (n == 0 || ph == WorksPhase.None || ph == WorksPhase.Complete) return 0;
            bool odd = (f & SiteFlags.LatchedCrewsPhaseOdd) != 0;
            return odd == ((((int)ph) & 1) != 0) ? math.min(n, RRWConst.kMaxCrewsPerProject) : 0;
        }
        public static SiteFlags EncodeLatchedCrews(SiteFlags f, int crews, WorksPhase ph)
        {
            f &= ~(SiteFlags.LatchedCrewsMask | SiteFlags.LatchedCrewsPhaseOdd);
            if (ph == WorksPhase.None || ph == WorksPhase.Complete) return f;
            f |= (SiteFlags)(math.clamp(crews, 1, 7) << 12);
            if ((((int)ph) & 1) != 0) f |= SiteFlags.LatchedCrewsPhaseOdd;
            return f;
        }

        // ---- cancel layout (saved in SiteFlags bits 9..11)
        public static int CancelCrewsOf(SiteFlags f) => math.min(RRWConst.kMaxCrewsPerProject, 1 + (((int)f >> 9) & 7));
        public static SiteFlags EncodeCancelCrews(SiteFlags f, int crews) =>
            (f & ~SiteFlags.CancelCrewsMask) | (SiteFlags)((math.clamp(crews, 1, 8) - 1) << 9);

        // ---- per-section surface spans

        // Chain pieces a layer covers this frame: the union of per-section pieces, sorted and merged. One crew (and a one-crew
        // cancel layout): exactly the single-front span. Pieces that start at an interior section boundary and grow behind a front
        // ("done" pieces) reach kSpanOverlap back into the previous section, so two Roads layers meeting there overlap like at a
        // front (C3: fresh asphalt of section i over the base cover tail of section i-1); hand-overs stay in one frame because
        // every layer uses the same quantised section fronts.
        public static void SurfaceSpans(SurfaceLayer layer, in ProjectView v, out SpanSet set)
        {
            set = default;
            if (!MultiSection(v)) { set.Add(SurfaceSpanSingle(layer, v)); return; }
            float U = v.U;
            if (v.Mode != VisualMode.FullDig || U <= 0f) return;
            var ph = v.Phase;
            var M = FrontSet.Main(v);
            float o = RRWConst.kSpanOverlap;
            var full = new Span(0f, U);
            if (v.Kind == WorksKind.Construction)
            {
                switch (layer)
                {
                    case SurfaceLayer.TopsoilStrip:
                        if (ph == WorksPhase.Survey || (ph == WorksPhase.Excavation && v.Replaced)) M.AddDone(ref set, 0f);
                        else set.Add(full);
                        return;
                    case SurfaceLayer.Subgrade:
                        if (ph == WorksPhase.Survey) return;
                        if (ph == WorksPhase.Excavation) M.AddDone(ref set, 2f);
                        else set.Add(full);
                        return;
                    case SurfaceLayer.BaseCourseCover:
                        if (ph == WorksPhase.Excavation && v.Replaced)
                        {
                            for (int i = 0; i < M.N; i++)
                            {
                                M.Section(i, out float a, out float b);
                                float fs = math.max(a, M.QAt(i, -2f));
                                set.Add(new Span(math.max(a, fs - o), b));
                            }
                        }
                        else if (ph == WorksPhase.Survey && v.Replaced) set.Add(full);
                        else if (ph == WorksPhase.Foundation) M.AddDone(ref set, 0f);
                        else if (ph == WorksPhase.Paving)
                        {
                            for (int i = 0; i < M.N; i++)
                            {
                                M.Section(i, out float a, out float b);
                                set.Add(new Span(math.min(math.max(a, M.Q(i) - o), b - RRWConst.kFrontQuantum), b));   // never empty in C3
                            }
                        }
                        return;
                    case SurfaceLayer.FreshAsphalt:
                    case SurfaceLayer.FreshAsphaltCover:
                        if (ph == WorksPhase.Paving) M.AddDone(ref set, 0f);
                        else if (ph == WorksPhase.Finishing)
                        {
                            if (v.HalvesActive)
                            {
                                SurfaceSpansHalf(layer, v, RoadZones.LeftHalf, out SpanSet l);
                                SurfaceSpansHalf(layer, v, RoadZones.RightHalf, out SpanSet r);
                                set.Union(l); set.Union(r);
                            }
                            else M.AddNotDone(ref set);
                        }
                        return;
                    case SurfaceLayer.TempMarking:
                        set.Add(SurfaceSpanSingle(SurfaceLayer.TempMarking, v));   // chain-level (trims, tape front), no section fronts
                        return;
                }
                return;
            }

            // ---- demolition
            if (v.Cancelled)
            {
                var C = FrontSet.Cancel(v);
                var cp = v.CancelPhase;
                SpanSet notDone = default, cancelSet = default;
                M.AddNotDone(ref notDone);
                if (cp == WorksPhase.Paving || cp == WorksPhase.Finishing)
                {
                    bool fromC3 = cp == WorksPhase.Paving;
                    switch (layer)
                    {
                        case SurfaceLayer.TopsoilStrip: set.Add(full); return;
                        case SurfaceLayer.Subgrade: if (ph == WorksPhase.Restore) set = notDone; else set.Add(full); return;
                        case SurfaceLayer.BaseCourseCover:
                            if (ph == WorksPhase.BreakUp) { if (fromC3) set.Add(full); else M.AddDone(ref set, 0f); }
                            else if (ph == WorksPhase.Removal) set = notDone;
                            return;
                        case SurfaceLayer.FreshAsphalt:
                        case SurfaceLayer.FreshAsphaltCover:
                            if (ph != WorksPhase.BreakUp) return;
                            if (fromC3) C.AddDone(ref cancelSet, 0f); else C.AddNotDone(ref cancelSet);
                            SpanSet.Intersect(notDone, cancelSet, out set);
                            return;
                    }
                    return;
                }
                switch (layer)
                {
                    case SurfaceLayer.TopsoilStrip:
                        if (cp == WorksPhase.Survey) C.AddDone(ref set, 0f); else set.Add(full);
                        return;
                    case SurfaceLayer.Subgrade:
                        if (cp == WorksPhase.Survey || ph == WorksPhase.Complete) return;
                        if (cp == WorksPhase.Excavation) { C.AddDone(ref cancelSet, 2f); SpanSet.Intersect(notDone, cancelSet, out set); }
                        else set = notDone;
                        return;
                    case SurfaceLayer.BaseCourseCover:
                        if (cp == WorksPhase.Foundation && ph != WorksPhase.Complete) { C.AddDone(ref cancelSet, 0f); SpanSet.Intersect(notDone, cancelSet, out set); }
                        return;
                }
                return;
            }
            switch (layer)
            {
                case SurfaceLayer.BaseCourseCover:
                    if (ph == WorksPhase.BreakUp) M.AddDone(ref set, 0f);
                    else if (ph == WorksPhase.Removal) M.AddNotDone(ref set);
                    return;
                case SurfaceLayer.Subgrade:
                    if (ph == WorksPhase.BreakUp) { if (v.F >= 0.92f) set.Add(full); }
                    else if (ph == WorksPhase.Removal) set.Add(full);
                    else if (ph == WorksPhase.Restore) M.AddNotDone(ref set);
                    return;
                case SurfaceLayer.TopsoilStrip:
                    if (ph == WorksPhase.Restore)
                    {
                        for (int i = 0; i < M.N; i++)
                        {
                            M.Section(i, out float a, out float b);
                            set.Add(new Span(a, math.min(b, M.Q(i) + o)));
                        }
                    }
                    else if (ph == WorksPhase.Complete) set.Add(full);
                    return;
            }
        }

        // Per-half pieces of a C4 CarriageHalf layer (generalised SurfaceSpanHalf). Empty unless ProjectView.HalvesActive.
        public static void SurfaceSpansHalf(SurfaceLayer layer, in ProjectView v, RoadZones half, out SpanSet set)
        {
            set = default;
            if (!MultiSection(v)) { set.Add(SurfaceSpanHalfSingle(layer, v, half)); return; }
            if (!v.HalvesActive || v.U <= 0f) return;
            bool firstWorks = (half & RRWConst.kStagedWorksHalf) != 0;
            var M = FrontSet.Main(v);
            switch (layer)
            {
                case SurfaceLayer.FreshAsphalt:
                case SurfaceLayer.FreshAsphaltCover:
                    if (v.SwapActive)
                    {
                        if (StageIndexOf(v) == 0) { if (firstWorks) M.AddNotDone(ref set); else set.Add(new Span(0f, v.U)); }
                        else if (!firstWorks) M.AddNotDone(ref set);
                        return;
                    }
                    if (firstWorks) M.AddNotDone(ref set);
                    else set.Add(new Span(TapeFront(v), v.U));
                    return;
                case SurfaceLayer.TempMarking:
                    if ((half & RRWConst.kStagedOpenHalf) != 0) set.Add(SurfaceSpanSingle(SurfaceLayer.TempMarking, v));
                    return;
            }
        }

        // ------------------------------------------------------------------ gameplay

        // Closure the works START with (mode choice). VisualOnly: no traffic effect; AlwaysSlowZone: slow zone;
        // Realistic: constructions with dependants stay open as a slow zone, everything else closes.
        public static ClosureLevel StartClosure(WorksKind kind, ClosurePolicy policy, bool dependants)
        {
            switch (policy)
            {
                case ClosurePolicy.VisualOnly: return ClosureLevel.Open;
                case ClosurePolicy.AlwaysSlowZone: return ClosureLevel.SlowZone;
            }
            if (kind == WorksKind.Construction && dependants) return ClosureLevel.SlowZone;
            return ClosureLevel.Closed;
        }

        // Closure target this frame:
        //  * carriagewayBusy (PhasePlan.MachinesHoldRoad: a puppet of the project is on / bound for the carriageway while the
        //    project releases the road) -> Closed;
        //  * Complete -> Open, except a demolition whose road is still hidden (it stays Closed until the delete);
        //  * a mode-A (FullDig) PROJECT is Closed for the whole works: machines are on the lanes, buildings that spawn along
        //    it wait (they get the staged-opening lane groups, OpenLanes), the policy applies to new works only. Pass the
        //    PROJECT mode (ProjectRecord.Mode), not the per-edge mode, so a mode-D bridge inside an A chain stays Closed too;
        //  * mode D (no machines): the start closure of the current policy / dependants, as before.
        public static ClosureLevel Closure(WorksKind kind, ClosurePolicy policy, bool dependants, WorksPhase ph, VisualMode projectMode, bool hideWanted, bool carriagewayBusy)
        {
            if (carriagewayBusy) return ClosureLevel.Closed;
            if (ph == WorksPhase.Complete) return kind == WorksKind.Demolition && hideWanted ? ClosureLevel.Closed : ClosureLevel.Open;
            if (projectMode == VisualMode.FullDig) return ClosureLevel.Closed;
            return StartClosure(kind, policy, dependants);
        }

        // Legacy signature (kept so callers compile): same rules with carriagewayBusy = false. The Director must switch
        // to the 7-argument overload with the PROJECT mode and MachinesHoldRoad.
        public static ClosureLevel Closure(WorksKind kind, ClosurePolicy policy, bool dependants, WorksPhase ph, VisualMode mode, bool hideWanted) =>
            Closure(kind, policy, dependants, ph, mode, hideWanted, false);

        // While a project releases the road (releaseSince != 0: completion frame N, or a D0 call-off), its
        // carriageway stays Closed until Machines report it clear in a report taken AFTER the release began. Mode D never has
        // machines. No trustworthy report -> hold. Safety cap (capped = ProjectRecord.ReleaseCapped: kCompletionMachineWaitSimFrames
        // of GAME time, so never while paused, or the last-resort update cap): the road opens anyway (the Director logs one
        // Warn: Machines broke the kPostSiteMaxSimFrames contract). releaseSince is an RRWClock.UpdateIndex >= 1. Not
        // releasing -> false (a mode-A project is Closed through Closure() anyway).
        public static bool MachinesHoldRoad(VisualMode projectMode, uint releaseSince, int machinesOnCarriageway, uint reportUpdate, uint now, bool capped)
        {
            if (projectMode != VisualMode.FullDig || releaseSince == 0) return false;
            if (capped) return false;
            bool fresh = reportUpdate != 0 && unchecked(now - reportUpdate) <= (uint)RRWConst.kMachinesReportStaleUpdates;
            if (!fresh) return true;
            if (unchecked((int)(reportUpdate - releaseSince)) <= 0) return true;   // report predates the release: wait one more
            return machinesOnCarriageway > 0;
        }

        // Staged opening (user request): lane groups of a CLOSED works road that carry traffic anyway, project
        // level (the Director gives hidden edges None). Rules:
        //  * construction only (a demolition or a cancelled build stays fully closed); staged = RRWSetting.StagedOpeningOn;
        //  * mode D (no machines): the sidewalks stay open (pedestrians first), the carriageway is the works;
        //  * mode A: sidewalks from the reveal (C3, C4); the chain-left half (kStagedOpenHalf) in C4 (Finishing), where the
        //    crew works in the chain-right half (Crew: kWorksLane);
        //  * a group opens only when a FRESH machine report shows no puppet in it (now or planned), and then stays open
        //    (sticky via openBefore = last frame's ProjectRecord.OpenLanes): Machines never plan into an open group, so the
        //    road never flips Closed -> open -> Closed (no reroute churn);
        //  * Complete: keep what is open; the whole road opens through Closure() once MachinesHoldRoad clears.
        [System.Obsolete("Use OpenLanes(view, PhasePlan.Stage(view, ctx), staged, machineZones, fresh, openBefore, softZones)")]
        public static RoadZones OpenLanes(in ProjectView v, bool staged, RoadZones machineZones, bool machinesReportFresh, RoadZones openBefore)
        {
            if (!staged || v.Kind != WorksKind.Construction || v.Cancelled) return RoadZones.None;
            if (v.Phase == WorksPhase.Complete) return openBefore & RoadZones.AllLanes;
            RoadZones want = RoadZones.None;
            if (v.Mode != VisualMode.FullDig) want = RoadZones.Sidewalks;
            else
            {
                if (v.Phase == WorksPhase.Paving || v.Phase == WorksPhase.Finishing) want |= RoadZones.Sidewalks;
                // Only when every edge has a car-lane group wholly inside that half (not on one-way roads,
                // whose single direction group spans both halves and would never carry traffic: the 'half open' dressing
                // - yellow lines, half barrier, centre cones, crew squeezed into one half - would lie to the player)
                if (v.Phase == WorksPhase.Finishing && (v.SeparableHalves & RRWConst.kStagedOpenHalf) == RRWConst.kStagedOpenHalf)
                    want |= RRWConst.kStagedOpenHalf;
            }
            RoadZones blocked = machinesReportFresh ? machineZones
                              : v.Mode == VisualMode.FullDig ? RoadZones.AllLanes : RoadZones.None;
            return ((openBefore & want) | (want & ~blocked)) & RoadZones.AllLanes;
        }

        // ------------------------------------------------------------------ staged traffic management

        // Stage of a project. Pure, O(1). The stage switch step (v.Switch) decides the layout
        // at a boundary: Vacate keeps the old layout; Swap / Drain / Ready already use the new one (Soft = the new works band
        // while Swap / Drain). Without a running switch the stage follows f (after a load at the switch point: the new stage;
        // the Director then re-runs the switch from Vacate).
        //  * construction mode A: hidden C0-C2 -> everything works; C3 -> sidewalks open; C4 -> sidewalks + (car half allowed)
        //    kStagedOpenHalf open while the crew works in kStagedWorksHalf; with SwapOn C4a/C4b at kC4SwapF (2 stages);
        //  * construction mode D (and the reserved HalfWidth): sidewalks open;
        //  * demolition mode A, D0 (BreakUp): house-side sidewalks (BuildingSidewalks) open until kPedCloseF, then SOFT (switch
        //    Swap / Drain) with accrual held at kD1 (HoldP) until drained; hidden phases: everything works;
        //  * Complete: keep what is open (the release gate opens the road).
        public static StageInfo Stage(in ProjectView v) => Stage(v, v.Ctx);

        public static StageInfo Stage(in ProjectView v, in StageContext c)
        {
            var st = new StageInfo { Count = 1, SwitchP = float.NaN, HoldP = float.NaN };
            var ph = v.Phase;
            if (ph == WorksPhase.Complete) { st.Open = v.OpenLanes & RoadZones.AllLanes & ~RoadZones.Parking; return st; }
            bool A = v.Mode == VisualMode.FullDig;
            float tol = RRWConst.kStageSwitchTolF;
            bool newLayout = v.Switch == StageSwitch.Swap || v.Switch == StageSwitch.Drain || v.Switch == StageSwitch.Ready;
            bool switching = v.Switch == StageSwitch.Swap || v.Switch == StageSwitch.Drain;
            if (A && IsHidden(v.Kind, v.Mode, ph)) { st.Works = RoadZones.AllLanes; return st; }
            if (v.Kind == WorksKind.Construction)
            {
                st.Works = RoadZones.Carriageway;
                if (!c.Staged) return st;
                st.Open = RoadZones.Sidewalks;
                if (!A || ph != WorksPhase.Finishing) return st;                  // mode D / C3: sidewalks only
                st.Works = RRWConst.kStagedWorksHalf;                             // the C4 crew's half (also without a car half)
                if (!c.CarHalfAllowed) return st;
                st.Open |= RRWConst.kStagedOpenHalf;
                st.NextWorks = RRWConst.kStagedOpenHalf;
                if (!c.SwapOn) return st;
                st.Count = 2;
                st.SwitchP = st.HoldP = ProgressAt(WorksPhase.Finishing, RRWConst.kC4SwapF);
                bool second = newLayout || (v.Switch == StageSwitch.None && v.F >= RRWConst.kC4SwapF - tol);
                if (second)
                {
                    st.Index = 1;
                    st.Works = RRWConst.kStagedOpenHalf;
                    st.NextWorks = RoadZones.None;
                    st.Open = RoadZones.Sidewalks | RRWConst.kStagedWorksHalf;
                    if (switching) st.Soft = RRWConst.kStagedOpenHalf;
                }
                return st;
            }
            // demolition
            st.Works = RoadZones.Carriageway;
            if (!A || ph != WorksPhase.BreakUp || !c.Staged || !c.D0Sidewalks) return st;
            RoadZones houses = v.BuildingSidewalks & RoadZones.Sidewalks;
            if (houses == RoadZones.None) return st;
            st.SwitchP = ProgressAt(WorksPhase.BreakUp, RRWConst.kPedCloseF);
            st.HoldP = PhaseEnd(WorksPhase.BreakUp);
            bool closing = newLayout || (v.Switch == StageSwitch.None && v.F >= RRWConst.kPedCloseF - tol);
            if (!closing) { st.Open = houses; st.NextWorks = houses; }
            else if (switching) st.Soft = houses;
            return st;
        }

        public static int StageIndexOf(in ProjectView v) => Stage(v).Index;

        // p is held exactly at a switch point (Director accrual hold).
        public static bool AtSwitch(float p, float switchP) => !float.IsNaN(switchP) && math.abs(p - switchP) <= 1e-5f;

        // Lane groups of a CLOSED works road that carry traffic, from the stage. Rules:
        //  * want = st.Open (never a Parking bit during works; never a group of st.Works / st.Soft / softZones);
        //  * a group opens only when a FRESH machine report shows no puppet in it (mode A without a fresh report: nothing opens),
        //    then stays open (sticky via openBefore = last frame's ProjectRecord.OpenLanes) as long as it is wanted. A group that
        //    stops being wanted (switch: it becomes works / soft) closes in the same update (re-close path: Traffic drains it);
        //  * Complete: keep what is open (the whole road opens through Closure() once MachinesHoldRoad clears);
        //  * demolitions: only the D0 house sides the stage offers.
        public static RoadZones OpenLanes(in ProjectView v, in StageInfo st, bool staged, RoadZones machineZones, bool machinesReportFresh,
                                          RoadZones openBefore, RoadZones softZones)
        {
            if (!staged) return RoadZones.None;
            if (v.Phase == WorksPhase.Complete) return openBefore & RoadZones.AllLanes & ~RoadZones.Parking;
            RoadZones want = st.Open & RoadZones.AllLanes & ~RoadZones.Parking & ~(st.Works | st.Soft | softZones);
            RoadZones blocked = machinesReportFresh ? machineZones
                              : v.Mode == VisualMode.FullDig ? RoadZones.AllLanes : RoadZones.None;
            return ((openBefore & want) | (want & ~blocked)) & RoadZones.AllLanes & ~RoadZones.Parking;
        }

        // Why no car half may open on ONE edge (None = allowed). Order: OneWay / LaneLayout, Track, Stop, Narrow,
        // DeadEnd. No split: OneWay only when the section saw ONE travel direction (EdgeSection.Verdict OneDirection, or a section
        // built without a verdict); shared two-way lanes, interleaved directions or no lane along the edge -> LaneLayout. A split
        // whose halves Traffic cannot fill with whole car groups (carHalves) -> LaneLayout (both directions exist).
        //  sec: the edge's EdgeSection (DirSplit); carHalves: EdgeRecord.CarHalves (whole direction groups per half);
        //  hasTrack / stopZones / cutEdge: EdgeRecord classification; bothHalvesWorked: the swap will make the other half a works
        //  half too (then both halves must be wide enough); deadEndRule: RRWGates.DeadEndRule.
        public static StageBlockReason CarHalfBlock(in EdgeSection sec, bool chainReversed, RoadZones carHalves, bool hasTrack,
                                                    RoadZones stopZones, bool cutEdge, bool bothHalvesWorked, bool deadEndRule)
        {
            if (float.IsNaN(RoadZoneMath.DirSplitChain(sec, chainReversed)))
                return sec.Verdict == SplitVerdict.OneDirection || sec.Verdict == SplitVerdict.Unmeasured || sec.Verdict == SplitVerdict.Split
                    ? StageBlockReason.OneWay : StageBlockReason.LaneLayout;
            if ((carHalves & RoadZones.Carriageway) != RoadZones.Carriageway) return StageBlockReason.LaneLayout;
            if (hasTrack) return StageBlockReason.Track;
            // A stop in a half that becomes a works half: with the side swap both halves are worked (C4a right, C4b left).
            if ((stopZones & (bothHalvesWorked ? RoadZones.Carriageway : RRWConst.kStagedWorksHalf)) != 0) return StageBlockReason.Stop;
            if (RoadZoneMath.HalfWidthChain(sec, chainReversed, RRWConst.kStagedWorksHalf) < RRWConst.kMinWorksHalfWidth
                || (bothHalvesWorked && RoadZoneMath.HalfWidthChain(sec, chainReversed, RRWConst.kStagedOpenHalf) < RRWConst.kMinWorksHalfWidth))
                return StageBlockReason.Narrow;
            if (deadEndRule && cutEdge) return StageBlockReason.DeadEnd;
            return StageBlockReason.None;
        }

        // The CHAIN-level dead-end rule. A car half may open only on a through chain: both chain ends connect
        // to the road network (ProjectRecord.ExitAtStart / ExitAtEnd). Neither end connected (an isolated road): always DeadEnd (the
        // per-edge CutEdge test misses it: removing an isolated edge disconnects nothing). One end connected (a dead-end chain):
        // DeadEnd under deadEndRule (RRWGates.DeadEndRule, default on): vehicles entering on the open half could never leave. The
        // Director treats it as a TRAP reason (an applied half re-closes through SOFT) and only once the exits are known.
        public static StageBlockReason ChainCarHalfBlock(bool exitAtStart, bool exitAtEnd, bool deadEndRule)
        {
            if (!exitAtStart && !exitAtEnd) return StageBlockReason.DeadEnd;
            if (deadEndRule && !(exitAtStart && exitAtEnd)) return StageBlockReason.DeadEnd;
            return StageBlockReason.None;
        }

        // Localisation key of a block reason (Settings.cs RRWText).
        public static string StageReasonKey(StageBlockReason r) => RRWText.P + "UI.StageReason." + r;

        // The chain end leavers drive to: one that connects to the road network (ProjectRecord.ExitAtStart/End);
        // Trim1 when both or neither connect.
        public static float ExitU(in ProjectView v) => v.ExitAtEnd || !v.ExitAtStart ? v.Trim1 : v.Trim0;
        public static sbyte ExitFacing(in ProjectView v) => v.ExitAtEnd || !v.ExitAtStart ? (sbyte)1 : (sbyte)-1;

        // Exit for a puppet at chain u heading `facing` (+1 = +u) that leaves without a DriveOut slot: the only
        // connected end; with both (or neither) connected the end ahead of the nose, unless the end behind is closer and within
        // kMaxReverseLeg (the PlanLeave rule). Never a dead end when the other end connects.
        public static float ExitFrom(in ProjectView v, float u, int facing)
        {
            if (v.ExitAtStart && !v.ExitAtEnd) return v.Trim0;
            if (v.ExitAtEnd && !v.ExitAtStart) return v.Trim1;
            float ahead = facing >= 0 ? v.Trim1 : v.Trim0, behind = facing >= 0 ? v.Trim0 : v.Trim1;
            float da = math.abs(ahead - u), db = math.abs(u - behind);
            return db < da && db <= RRWConst.kMaxReverseLeg ? behind : ahead;
        }

        // Mode is chosen once at start (Tools / SiteFactory) and saved. Anything that does not start Closed is mode D,
        // so traffic never drives over a hidden road.
        public static VisualMode ChooseMode(bool digEligible, float projectLength, QualityPreset quality, bool terrainEnabled, ClosureLevel startClosure)
        {
            if (!digEligible || projectLength < RRWConst.kShortEdge || quality == QualityPreset.Low || !terrainEnabled || startClosure != ClosureLevel.Closed)
                return VisualMode.Minimal;
            return VisualMode.FullDig;
        }
    }

    // The per-section fronts of one front value. Pure, job-safe. One section: exactly the single front
    // (Done = F >= x, Covered = Sweep(F, lo, hi), Q = QuantizeFront(F, U)).
    public struct FrontSet
    {
        public int N;          // sections (>= 1)
        public float U;        // chain length
        public float Single;   // the one-crew front (chain u): MainFront / PickupFront / CancelFront
        public float Unit;     // Single / U (the shared unit sweep)

        public static FrontSet Of(float singleFront, int n, float U) => new FrontSet
        {
            N = math.clamp(n, 1, RRWConst.kMaxCrewsPerProject), U = U, Single = singleFront,
            Unit = U > 0f ? math.saturate(singleFront / U) : 0f,
        };
        public static FrontSet Main(in ProjectView v) => Of(PhasePlan.MainFront(v), v.CrewCount, v.U);
        public static FrontSet Pickup(in ProjectView v) => Of(PhasePlan.PickupFront(v), v.CrewCount, v.U);
        public static FrontSet Cancel(in ProjectView v) => Of(math.clamp(v.CancelFront, 0f, v.U), v.CancelCrewCount, v.U);

        public void Section(int i, out float a, out float b)
        {
            if (N <= 1) { a = 0f; b = U; return; }
            a = PhasePlan.SectionStart(i, N, U);
            b = PhasePlan.SectionStart(i + 1, N, U);
        }

        // Unquantised front of section i.
        public float Front(int i)
        {
            if (N <= 1) return Single;
            Section(i, out float a, out float b);
            return a + (b - a) * Unit;
        }

        // Quantised decal front of section i.
        public float Q(int i)
        {
            if (N <= 1) return PhasePlan.QuantizeFront(Single, U);
            Section(i, out float a, out float b);
            return PhasePlan.QuantizeSection(Front(i), a, b);
        }

        // Quantised front of section i shifted by `offset` (C1 subgrade: -2), never below the section start.
        public float QAt(int i, float offset)
        {
            if (N <= 1) return math.max(0f, PhasePlan.QuantizeFront(Single + offset, U));
            Section(i, out float a, out float b);
            float fi = Front(i);
            if (fi >= b - 1e-3f) return b;   // a finished section is covered to its end (no strip pops in at the next boundary)
            return math.max(a, PhasePlan.QuantizeSection(fi + offset, a, b));
        }

        // The front of the section holding x passed x (x <= 0: always).
        public bool Done(float x)
        {
            if (N <= 1) return Single >= x;
            if (x <= 0f) return true;
            return Front(PhasePlan.SectionOf(x, N, U)) >= x;
        }

        // Done fraction of [lo, hi] (generalised Sweep(F, lo, hi); the part below 0 counts as done, like the single sweep).
        public float Covered(float lo, float hi)
        {
            if (N <= 1) return PhasePlan.Sweep(Single, lo, hi);
            if (hi - lo <= 1e-5f) return Done(hi) ? 1f : 0f;
            float m = math.max(0f, math.min(hi, 0f) - lo);
            for (int i = 0; i < N; i++)
            {
                Section(i, out float a, out float _);
                float s0 = math.max(a, lo), s1 = math.min(Front(i), hi);
                if (s1 > s0) m += s1 - s0;
            }
            return math.saturate(m / (hi - lo));
        }

        // Adds the done pieces [B_i, Q_i] (sub > 0: [B_i, Q(F_i - sub)]); pieces of sections i > 0 reach kSpanOverlap back.
        public void AddDone(ref SpanSet set, float sub)
        {
            for (int i = 0; i < N; i++)
            {
                Section(i, out float a, out float _);
                float e = sub > 0f ? QAt(i, -sub) : Q(i);
                if (e - a <= 1e-4f) continue;
                set.Add(new Span(i > 0 ? math.max(0f, a - RRWConst.kSpanOverlap) : a, e));
            }
        }

        // Adds the not-yet-done pieces [Q_i, B_i+1].
        public void AddNotDone(ref SpanSet set)
        {
            for (int i = 0; i < N; i++)
            {
                Section(i, out float _, out float b);
                set.Add(new Span(Q(i), b));
            }
        }
    }

    // A sorted, merged set of up to Capacity chain pieces (fixed size, job-safe, no allocation). Pieces that
    // overlap or touch (gap <= 1e-3 m) merge; empty pieces are ignored; when full, the last piece absorbs the overflow (never drops).
    public struct SpanSet
    {
        public const int Capacity = 12;
        public int Count;
        float4 m_A0, m_A1, m_A2, m_B0, m_B1, m_B2;

        public Span this[int i] => i < 0 || i >= Count ? Span.Empty : new Span(GetA(i), GetB(i));
        public bool IsEmpty => Count == 0;
        public Span Hull => Count == 0 ? Span.Empty : new Span(GetA(0), GetB(Count - 1));
        public float Length { get { float l = 0f; for (int i = 0; i < Count; i++) l += GetB(i) - GetA(i); return l; } }
        public bool Contains(float u) { for (int i = 0; i < Count; i++) if (u >= GetA(i) && u <= GetB(i)) return true; return false; }

        float GetA(int i) { int k = i & 3; return i < 4 ? m_A0[k] : i < 8 ? m_A1[k] : m_A2[k]; }
        float GetB(int i) { int k = i & 3; return i < 4 ? m_B0[k] : i < 8 ? m_B1[k] : m_B2[k]; }
        void SetA(int i, float v) { int k = i & 3; if (i < 4) m_A0[k] = v; else if (i < 8) m_A1[k] = v; else m_A2[k] = v; }
        void SetB(int i, float v) { int k = i & 3; if (i < 4) m_B0[k] = v; else if (i < 8) m_B1[k] = v; else m_B2[k] = v; }

        public void Clear() { this = default; }

        public void Add(Span s)
        {
            if (s.IsEmpty) return;
            float a = s.A, b = s.B;
            // find insertion point
            int pos = 0;
            while (pos < Count && GetA(pos) < a) pos++;
            // merge with the previous piece?
            if (pos > 0 && GetB(pos - 1) >= a - 1e-3f)
            {
                pos--;
                a = GetA(pos);
                b = math.max(b, GetB(pos));
                SetB(pos, b);
            }
            else
            {
                if (Count >= Capacity)
                {
                    // full: widen the nearest piece to cover the new one (never drop)
                    int j = math.min(pos, Count - 1);
                    SetA(j, math.min(GetA(j), a)); SetB(j, math.max(GetB(j), b));
                    pos = j;
                }
                else
                {
                    for (int i = Count; i > pos; i--) { SetA(i, GetA(i - 1)); SetB(i, GetB(i - 1)); }
                    SetA(pos, a); SetB(pos, b);
                    Count++;
                }
            }
            // absorb following pieces that now overlap
            while (pos + 1 < Count && GetA(pos + 1) <= GetB(pos) + 1e-3f)
            {
                SetB(pos, math.max(GetB(pos), GetB(pos + 1)));
                for (int i = pos + 1; i < Count - 1; i++) { SetA(i, GetA(i + 1)); SetB(i, GetB(i + 1)); }
                Count--;
            }
        }

        public void Union(in SpanSet o) { for (int i = 0; i < o.Count; i++) Add(o[i]); }

        public static void Intersect(in SpanSet x, in SpanSet y, out SpanSet r)
        {
            r = default;
            for (int i = 0; i < x.Count; i++)
                for (int j = 0; j < y.Count; j++)
                {
                    float a = math.max(x.GetA(i), y.GetA(j)), b = math.min(x.GetB(i), y.GetB(j));
                    if (b - a > 1e-4f) r.Add(new Span(a, b));
                }
        }

        public override string ToString()
        {
            if (Count == 0) return "[]";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Count; i++) sb.Append(this[i].ToString());
            return sb.ToString();
        }
    }
}
