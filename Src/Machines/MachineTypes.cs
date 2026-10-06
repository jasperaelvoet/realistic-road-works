using Game.Objects;
using Unity.Entities;
using Unity.Mathematics;

// Shared types of the Machines module. Everything here is runtime-only: puppets carry LivePath and are never saved.
namespace RealisticRoadWorks.V3.Machines
{
    // What a leg does with the machine's chain position u(t) and lateral lat(t).
    public enum LegKind : byte
    {
        Hold = 0,     // stand at (U0, L0)
        Drive = 1,    // trapezoid speed profile U0 -> U1 along the chain, lateral merge L0 -> L1 inside [P0, P1] (travelled metres)
        Follow = 2,   // creep with the front: u = clamp(F(t) + U0, Lo, Hi) (+ decaying catch-up offset)
        DigHop = 3,   // step-and-dig grid: stationary for P1 s of every P0 s cycle, then hop to the next anchor A(k+1) in P2 s
        Shuttle = 4,  // loader spread: u = F(t) + U0 + stroke(t), strokes over [0, P1] m at Vmax with P0 s waits
        Scrape = 5,   // topsoil loader: Follow + every P0 s a P1 m reverse-and-return bump
        Turn = 6,     // three-move K-turn about (U0, L0): flips the facing, ends where it started; radius P0, side P1
        Brake = 7,    // separation guard: decelerate from Vmax at Acc along Move from (U0, L0); the lateral keeps
                      // the slope P0 (dlat per travelled metre) of the cut leg and straightens out by the stop (no heading pop)
    }

    // Bone animation selected by a leg (MachineBoneSystem).
    public enum AnimKind : byte
    {
        Rest = 0,     // travel / parked pose
        Dig = 1,      // excavator dig cycle, loader-digger fallback bucket cycle
        Break = 2,    // excavator breaker chops (D0)
        Spread = 3,   // loader: bucket low forward, raised reversing; grader (excavator clone): reach - drag the bucket flat - lift
        Scrape = 4,   // loader: bucket low; grader: reach - strip - curl - cast the topsoil to the left - slew back
        Load = 5,     // truck standing at the excavator (no bones)
        Carry = 6,    // loader / excavator carrying pose while following
        Compact = 7,  // roller compaction pass of a vibrating duty (vibration while |v| > 0.15 m/s; no bones)
    }

    // Puppet families (prefab + rig behaviour).
    public enum MachineKind : byte
    {
        Excavator = 0,     // "RRW Road Excavator(/Quiet)" clone at root scale 0.4 (MiningExcavator01 rig)
        Loader = 1,        // FrontendLoader01 front half (no trailer)
        Truck = 2,         // CoalTruck01 with DeliveryTruck loads
        Rmv = 3,           // RoadMaintenanceVehicle01 (crew truck, paver proxy, line painter)
        LoaderDigger = 4,  // fallback: FrontendLoader01 used as the digger
        Grader = 5,        // Loader ROLE on the "RRW Road Excavator Quiet" clone (root scale 0.4): bucket grading / spreading /
                           // topsoil strip. FrontendLoader01 is a forestry grapple loader (Claw bone) and looked wrong on a road
                           // site (seen in play testing); FrontendLoader01 stays only as the fallback (no clone).
        Roller = 6,        // procedural tandem roller (GameObject unit, MachineRole.Roller; no entity, no prefab)
    }

    // One leg of a machine plan. Times are machine seconds relative to MachinePlan.Epoch.
    public struct MachineLeg
    {
        public float T0, T1;          // start / end (T1 = +inf for open-ended legs)
        public byte Kind;             // LegKind
        public byte Anim;             // AnimKind
        public sbyte Facing;          // nose direction: +1 = +u, -1 = -u (Turn: facing at the START)
        public sbyte Move;            // Drive: direction of travel along u
        public float U0, U1;          // Drive: start / end u. Front-relative legs: U0 = offset from F, U1 = u at the leg start
                                      // (odometer reference). Hold: U0 = u. Turn: U0 = pivot u.
        public float L0, L1;          // lateral metres (+ = right of the chain direction)
        public float Vmax, Acc;       // m/s, m/s^2
        public float Lo, Hi;          // clamp range of front-relative anchors (chain u)
        public float P0, P1, P2;      // kind-specific parameters
        public float Odo0;            // odometer (signed wheel distance) at the leg start
        public float Cu, Cl, CatchDur;// catch-up offsets (u, lat) at T0 that decay to 0 over CatchDur seconds
        public float Blend;           // seconds over which the bones blend from FromAnim's canonical pose into this leg's anim
        public byte FromAnim;         // AnimKind of the previous leg (blend source)
        public byte Front;            // 0 = MachinePlan.FrontA, 1 = FrontB
        // ---- realistic front-relative motion. Front-relative legs (Follow / Shuttle base / Scrape base)
        // use Acc as the role's acceleration: the anchor clamps (Lo / Hi, section start / end) are SOFT (polynomial smooth
        // min / max sized from the front speed and kTargetAccelShare x Acc), so the velocity never jumps where the front starts,
        // stops or reaches a clamp.
        public float Cv, VDur;        // velocity matching at the leg start: u += Cv * (t - t^2 / 2 VDur) for t < VDur (VDur / 2 after):
                                      // the start velocity equals the previous leg's end velocity, |dv/dt| = |Cv| / VDur
        public float RetU, RetV, RetT;// return-aware anchor: target <= RetU + RetV * max(0, RetT - t) (RetT leg-relative s;
                                      // RetV 0 = off). The anchor moves back to RetU (home) at RetV and is home by RetT
        public float Rate, RateU;     // front lag: the leg follows a SLOWED front Flin' = RateU + Rate x (Flin - RateU)
                                      // (Rate = time scale 0..1, RateU = the front value at the pivot: where the front starts moving or
                                      // stands at the leg start); clamped to the section like the real front, so the machine also
                                      // finishes its section after the real front stopped. 0 = off (the crew front is within VFwd)

        public readonly bool OpenEnded => float.IsPositiveInfinity(T1);
    }

    // Front model of one phase: F(tau) = PhasePlan.MainFront(Phase, f(p(tau)), U, Swap) with p from the ProgressModel (sim-frame
    // domain). Swap = ProjectView.SwapActive (C4a painter sweeps U*Sweep(f, .03, .40), C4b U*Sweep(f, .47, .86)), so the
    // job-side front is the same stage-aware front the Director publishes (PhasePlan.MainFront(view)).
    public struct FrontRef
    {
        public ProgressModel Model;
        public byte Phase;                // WorksPhase
        public float PStart, PEnd;        // progress bounds of Phase
        public float U;                   // chain length
        public byte Swap;                 // 1 = the C4 side swap is active (stage-aware C4 front)
        public float Floor;               // C4 front floor without the swap (StageContext.FrontFloor; 0 = none)
        public byte Crews, Crew;          // section `Crew` of `Crews` (0/1 crews = the whole chain):
                                          // F = PhasePlan.SectionFront(Phase, f, U, Crews, Crew, Swap, Floor)

        public readonly bool SameAs(in FrontRef o) =>
            Phase == o.Phase && PStart == o.PStart && PEnd == o.PEnd && U == o.U && Swap == o.Swap && Floor == o.Floor &&
            Crews == o.Crews && Crew == o.Crew &&
            Model.P0 == o.Model.P0 && Model.Frame0 == o.Model.Frame0 && Model.PPerFrame == o.Model.PPerFrame;
    }

    // Machine clock: tau = Tau0 + (frame - Frame0 + frac) / 60 * Scale. Re-anchored when Scale changes,
    // so a time-scale switch never jumps. Frozen while paused (RenderingSystem.frameIndex stops).
    public struct ClockData
    {
        public double Tau0;
        public uint Frame0;
        public float Scale;

        public readonly double Tau(uint frame, float frac) => Tau0 + ((double)unchecked((int)(frame - Frame0)) + frac) / 60.0 * Scale;
    }

    // Job-safe plan of one puppet (fixed size; the pose is a pure function of this + the clock + the project track).
    public struct MachinePlan : IComponentData
    {
        public const int MaxLegs = 12;

        public double Epoch;              // machine seconds of leg time 0
        public int Count;
        public MachineLeg L0, L1, L2, L3, L4, L5, L6, L7, L8, L9, L10, L11;

        // Front models: A = the project's ProgressModel + phase this plan was made for; B = the previous plan's A, used by
        // legs copied from the previous plan (MachineLeg.Front = 1) so a re-plan never moves a running leg.
        public FrontRef FrontA, FrontB;

        public int Track;                 // MachineTrackStore slot (-1 = none: the mover leaves the puppet alone)
        public float HalfLen, HalfWid;    // tilt sampling footprint (m)
        public TransformFlags Flags;      // light / signal flags decided at Modification1
        public byte Role;                 // MachineRole
        public byte MKind;                // MachineKind
        public float SlewDeg;             // excavator dump slew (90 beside, 180 inline)
        public float FootOff;             // forward offset (m, scaled) of the footprint centre (axle midpoint) from the origin

        public readonly MachineLeg Get(int i)
        {
            switch (i)
            {
                case 0: return L0; case 1: return L1; case 2: return L2; case 3: return L3;
                case 4: return L4; case 5: return L5; case 6: return L6; case 7: return L7;
                case 8: return L8; case 9: return L9; case 10: return L10; default: return L11;
            }
        }

        public void Set(int i, MachineLeg l)
        {
            switch (i)
            {
                case 0: L0 = l; break; case 1: L1 = l; break; case 2: L2 = l; break; case 3: L3 = l; break;
                case 4: L4 = l; break; case 5: L5 = l; break; case 6: L6 = l; break; case 7: L7 = l; break;
                case 8: L8 = l; break; case 9: L9 = l; break; case 10: L10 = l; break; default: L11 = l; break;
            }
        }
    }

    // Result of evaluating a plan at one instant (chain space).
    public struct ChainState
    {
        public float U, Lat;
        public float Hu, Hl;          // unit nose heading in chain space (du, dlat)
        public float Speed;           // m/s along the nose (negative = reversing)
        public bool Braking;
        public byte Anim;             // AnimKind
        public float AnimTime;        // seconds into the anim cycle (Dig/Break: cycle seconds; Hold: seconds since hold start)
        public float AnimParam;       // Spread: raised weight 0..1
        public float Blend;           // 0..1 weight of Anim against FromAnim (1 = fully this leg's anim)
        public byte FromAnim;
        public float Odo;             // signed wheel distance
        public int Leg;
        public byte LegKind;
    }

    // One sample of a project's chain track (MachineTrackStore). Y values are relative to the centre-line curve Y.
    public struct TrackSample
    {
        public float3 Pos;            // centre-line position (curve Y)
        public float2 Right;          // unit right of the chain direction (xz)
        public float FloorRel;        // floor (GroundMath.FloorWorldY) - curve Y
        public float VergeL, VergeR;  // CPU terrain at -/+(HalfWidth + 3.5) - curve Y
        public float HalfWidth, FlatHalf;
        public byte Flags;            // 1 = mode-D (Minimal) edge, 2 = left verge blocked, 4 = right verge blocked, 8 = outside the chain
    }

    public struct TrackHeader
    {
        public int Offset, Count;
        public float U0, Ds;          // u of sample 0, spacing
        public int Valid;
    }

    // Interpolated track point.
    public struct TrackPoint
    {
        public float3 Pos;
        public float2 Right;
        public float FloorRel, VergeL, VergeR, HalfWidth, FlatHalf;
        public byte Flags;

        public readonly float2 Dir => new float2(-Right.y, Right.x);

        // Ground height relative to the curve Y at lateral `lat` (floor for |lat| <= FlatHalfWidth,
        // linear across [FlatHalfWidth, HalfWidth], verge beyond).
        public readonly float HeightRel(float lat)
        {
            float a = math.abs(lat);
            float side = lat < 0f ? VergeL : VergeR;
            if (float.IsNaN(side)) side = FloorRel;
            if (a <= FlatHalf) return FloorRel;
            if (a >= HalfWidth || HalfWidth - FlatHalf < 0.05f) return side;
            return math.lerp(FloorRel, side, (a - FlatHalf) / (HalfWidth - FlatHalf));
        }
    }

    // Constants of the module (tuning beyond RRWConst).
    public static class MxConst
    {
        public const float kTrackStep = 2f;           // metres between track samples
        public const float kTrackExtra = 40f;         // track extends this far beyond both chain ends
        public const int kMaxTrackSamples = 4096;     // per project (ds grows for very long chains)
        public const float kReverseSpeed = 2f;        // unused by planning (MachineLimits.VRev per role); legacy K-turn legs only
        public const float kTurnRadius = 5f;          // K-turn arc radius (m)
        public const float kTurnPause = 0.8f;         // gear change pause at each cusp (s)
        public const float kHopStart = 5f, kHopDur = 5f;  // dig cycle: stationary 0-5 s, hop 5-10 s
        public const float kBreakCycle = 10f;         // breaker uses the same hop grid (3 s chops while stationary)
        public const float kLoadBuckets = 4;
        public const float kTruckTripWait = 40f;      // LoadAtFront "trip" wait at the base slot
        public const float kDumpTripWait = 30f;       // DumpAtFront reload wait
        public const float kTipSeconds = 20f;
        public const float kFeedSeconds = 45f;        // C3 hopper feed per load
        public const float kZoneHalf = 25f;           // front zone [F - 25, F + 25]: one truck at a time
        public const float kZoneMargin = 2f;
        public const float kReverseMax = 31f;         // reverse-in length after a turn (<= RRWConst.kMaxReverseLeg)
        public const float kTurnStation = 12f;        // base turn station beyond the farther base slot
        public const float kMergeSlope = 0.25f;       // |dlat/ds| average limit of lane changes
        public const float kOverlapMargin = 0.35f;    // OBB margin for the overlap planner (m)
        public const float kOverlapStep = 0.5f;       // s
        public const float kOverlapHorizon = 45f;     // s (truck approaches are checked to their work start, max 45 s)
        public const int kOverlapTries = 6;
        public const float kRestBlendSeconds = 3f;    // excavator returns to rest pose over 3 s
        public const float kDigBlendIn = 1.5f;
        public const float kVergeMaxStep = 2.5f;      // a verge slot whose ground differs more than this from the floor is unusable
        public const float kPostSiteParkUpdates = RRWConst.kPostSiteParkSeconds * 60f; // 120 s real time at 60 fps, in updates
        public const float kNoneDriveOutDistance = 40f;
        public const float kMinimalCrossSpeed = 3f;
        // grader (excavator clone in the Loader role) cycles on the 10 s grid: stationary [0, hopStart), hop [hopStart, 10)
        public const float kGradeHopStart = 6f, kGradeHopDur = 4f;     // Spread: reach / drag / lift, then step forward
        public const float kStripHopStart = 8.5f, kStripHopDur = 1.5f; // Scrape: strip / curl / cast left / slew back, short step
        public const float kGraderSpeed = 1.5f, kGraderAccel = 0.5f;   // unused (MachineLimits Loader row)
        // excavator <-> grader guard: the grader yields whenever the pair's planned boxes meet within the horizon
        public const float kGuardHorizon = 45f;       // s of machine time checked ahead
        public const float kGuardYieldHold = 15f;     // s a yield plan stands before the role plan is tried again
        public const float kParkStep = 3f;            // post-site / drive-out park slot search step along u (m)
        public const int kParkTries = 40;             // slots searched inward from the chain end
        public const uint kYResnapEvery = 12u, kYResnapWindow = 300u; // track Y re-refresh after a terrain step (CPU heightmap readback lag)

        // ---- visible-road lanes, machine report, separation guard
        // Lanes on a visible road (C3/C4): every box keeps this clearance inside the carriageway edge, so the
        // sidewalks can open; in C4 the box also keeps kZoneCentreTolerance + kHalfClearance off the centre line.
        public const float kCarriageClearance = 0.1f;
        // While the sidewalks are open boxes keep this much off the carriageway edge when they fit (a little extra room from
        // the kerb fences, which stand RRWConst.kKerbFenceOutset OUTSIDE the edge on the sidewalk); otherwise
        // kCarriageClearance applies, which is still clear of the fences.
        public const float kKerbFenceClearance = 0.3f;
        public const float kVisibleMergeSlope = 0.05f;  // lane changes on a visible road (OBB corner swing stays < kCarriageClearance)
        public const float kHalfClearance = 0.05f;
        // K-turn sweep half width about the turn lateral (R/2 + truck HL*sin60 + HW*cos60 + margin): a visible-road K-turn
        // is only planned when the carriageway is at least this wide on each side of the centre line (never in C4).
        public const float kTurnSweepHalf = 7.5f;
        // Machine report: samples of the committed plan.
        public const float kReportStep = 0.5f;          // s machine time (contract: <= 0.5 s)
        public const int kReportMaxSamples = 400;       // per puppet per update (the step grows beyond that)
        public const float kReportFrontLook = 20f;      // s looked ahead on an open-ended front-relative leg (lateral is constant there)
        // Runtime separation guard: predictive OBB check over [now, now + horizon] every update, all puppets of all
        // projects (post-site included). Horizon = stopping time + kGuardLatency.
        public const float kGuardStep = 0.25f;          // s
        public const float kGuardMargin = 0.15f;        // m added to the TRUE boxes (the planner uses kOverlapMargin)
        public const float kGuardLatency = 1.2f;        // s: guard reaction + mover write-ahead + safety
        public const float kGuardDecelFactor = 1.5f;    // brake deceleration = this x the puppet's acceleration
        public const float kGuardRetry = 2f;            // s a held puppet waits before its role plan is tried again
        public const float kGuardReleaseWindow = 8f;    // s checked ahead before a held puppet may move again
        public const float kGuardDeadlockSeconds = 10f; // two puppets holding for each other this long -> the lower rank backs out
        public const float kGuardBackOutMax = 30f;      // m searched along the own lane for a back-out slot
        public const float kStandStillRetry = 3f;       // s: a truck whose every route candidate conflicts stands still and retries
        public const float kPlanCheckMax = 180f;        // s: the longest window a plan is overlap-checked over
        public const float kFeedPairMargin = 0f;        // C3 feed truck <-> paver: true contact only (designed 0.4 m gap)

        // ---- no frozen machines
        // A puppet is STUCK when the guard holds it (GuardHeld / StandStill: its own plan wants to move) and it has not moved
        // for RRWConst.kMachineStuckSeconds of machine time. Every stuck episode runs one deterministic resolution step
        // (yield / back-out / re-plan or other exit); after kStuckEpisodesMax failed episodes the lower rank is removed
        // (made a leaver: removed when culled, else at its leave cap).
        public const float kStuckMoveSpeed = 0.1f;      // m/s: slower than this counts as "not moving"
        public const int kStuckEpisodesMax = 3;
        public const float kStuckEpisodeReset = 60f;    // s free of any stuck episode -> the episode count starts again
        public const float kStuckBackOutSpeed = 2f;     // unused (back-outs drive at MachineLimits.VRev of the role)
        public const float kLeaveReverseChunk = 30f;    // m: a leaver that must reverse further than kMaxReverseLeg to its exit
                                                        // reverses in chunks of this length with a short pause (no K-turn on a visible road)
        public const float kLeaveReversePause = 1f;     // s

        // ---- realistic front-relative motion, plan sample caches, planning budgets
        // Acceleration budget shares of front-relative legs (sum < 1): the soft anchor clamps / return / lag corners, the start
        // velocity match (Cv) and the catch-up offset (Cu) each get a share of the role's Accel, so their sum never exceeds it.
        public const float kTargetAccelShare = 0.45f;
        public const float kVelMatchAccelShare = 0.25f;
        public const float kCatchAccelShare = 0.2f;
        public const float kSpeedMargin = 0.92f;        // planned speeds stay this fraction below the role's limit (check tolerance)
        public const float kSettleAccelShare = 0.8f;    // a moving front-relative leg cut by a re-plan brakes at this x Accel
        public const float kHopAccelShare = 0.8f;       // DigHop hops (excavator / grader / loading truck): trapezoid at this x Accel
        public const float kMinDigSeconds = 3f;         // the hop window may grow into the dig half down to this
        public const float kSecSlack = 6f;              // a crew's front-relative anchors stay within [SecLo - this, SecHi + this]
        public const float kOutOfRangeCheck = 10f;      // rrw.check: a working puppet farther than this outside its crew range
        public const float kSampleStep = 0.5f;          // plan sample cache grid (machine seconds, absolute)
        public const int kSampleCap = 512;              // cached grid samples per puppet (256 s)
        public const double kPlanBudgetMs = 0.75;       // global truck planning budget per update (lowered from 1.0)
        public const int kTrackSliceSamples = 128;      // track Y refresh: samples per update outside the puppets' edges
        public const float kGuardCell = 32f;            // separation guard broad phase: world grid cell (m)
        public const float kMonSpeedWindow = 0.1f;      // rrw.mx.check speed invariant: chord window (machine s)
        public const float kMonAccelWindow = 0.25f;     // ... acceleration: change of the signed speed over this window

        // ---- road rollers and IK digging
        // Bucket tip calibration of the "RRW Road Excavator" rig (verified in game with reachedErr 0.000 and a
        // 0.02 m floor gap): the DEFAULT estimate was kept - the stick line continued from the bucket pin by 0.5 x the stick length,
        // 0 deg off the line (dg.tip was not needed). Recorded here so a later in-game calibration changes one constant.
        public const float kDigTipStickFactor = 0.5f;
        public const float kDigTipDeg = 0f;
        public const float kDigRateNominal = 23f;       // deg/s average slew of the fit (= RRWConst.kDigSlewRateAvg)
        public const float kRollerJointDrop = 0.01f;    // roller root 1 cm into the surface (verified contact, no gap)
        public const float kRollerViewPad = 3f;         // frustum test box: pivot +- this (m) horizontally, 0..kRollerHeight vertically
        public const float kRollerLatYawMargin = 0.35f; // kRollerLength/2 x sin(max pass yaw ~8.5 deg): taken off both lateral ends

        // ---- dig grid re-phasing, cache reuse after re-anchors, check budgets
        public const float kDigAbsorb = 1.0f;           // a dig / grade re-plan within this of its anchor starts the DigHop there (catch-up Cu), no drive
        public const float kDigAbsorbLat = 0.3f;        // ... and within this laterally (Cl for the grader; the digger keeps its lateral)
        public const float kDigStartMaxWait = 2.0f;     // an excavator that would wait longer for the next grid cycle re-phases the grid (trucks re-plan)
        // in-place re-anchor: plan samples whose front moved less than this against the cache's reference model
        // are kept. A kept sample is at most 2x this off the current plan, which stays inside the guard margin (planner margin 0.35 m).
        public const float kReanchorKeepDrift = kGuardMargin / 3f;
        // ... when the kept part is shorter than this (the guard / release-check horizon) every sample is dropped and the next
        // Validate takes the new model as the reference (the report cache is marked for a rate-limited rebuild)
        public const float kReanchorKeepMin = kGuardReleaseWindow;
        public const int kReportSoftRebuilds = 2;       // report caches rebuilt per update after re-anchors (stale ones keep their zones meanwhile)
        public const double kCheckBudgetMs = 0.5;       // per-update budget of the synchronous checks (release, back-out,
                                                        // leavers, grader guard / yield); an unfinished check counts as a conflict (retried)
        public const float kMaxMergeSlope = 0.35f;      // a drive's lane change needs >= dLat / this metres along the chain (else a run-up)
        public const float kLatSnap = 0.05f;            // a lateral change <= this never starts a drive / run-up of its own
        public const float kSkipSpeedFactor = 1.6f;     // planner skip-ahead: world speed <= this x the role's fastest limit (curves, merges)
        public const float kRollerHiddenMargin = 0.3f;  // hidden floor: |lat| <= FlatHalf - kRollerWidth/2 - this
        public const float kRollerPassMergeSlope = 0.1f;// lateral shift <= 0.8 m over >= 8 m (yaw <= ~6 deg), forward passes only
        public const int kRollerPassesPerPlan = 4;      // forward / reverse passes committed per plan (re-plan at their end)
        public const float kDeadEndVacateSeconds = 30f; // C4 Vacate leaver that needs longer than this to a connected exit: dead-end rule
        public const float kNodeRampHalf = 2.5f;        // rrw.mx.check: |u - node| <= this = on a node ramp (floor steps blend there)
        public const float kFloorErrorRampMax = 0.15f;  // ... the allowed floor error on a ramp

        // ---- static slots never overlap, make-way, prompt leavers (ChoreoSeparation.cs)
        public const float kMakeWaySeconds = 3f;        // a mover held this long by a STANDING blocker of lower make-way priority: the blocker moves aside
        public const float kMakeWayStillWindow = 3f;    // ... "standing": no motion over [now, now + this]
        public const float kMakeWayRetry = 4f;          // s between two make-way attempts of the same blocker
        public const float kMakeWayReach = 24f;         // m searched along u (both ways, kParkStep) for a make-way spot
        public const float kMakeWayHorizon = 40f;       // s of the mover's wanted path a make-way spot stays clear of
        public const float kMakeWayHold = 12f;          // s a role that stood aside waits before its role plan is tried again (release-checked)
        public const float kAsideMaxSeconds = 60f;      // an aside wait does not count as stuck for this long
        public const float kWantedMaxAge = 30f;         // s a captured wanted plan (guard brake / rejected re-plan) stays the mover's path
        public const int kMakeWayFailsMax = 3;          // failed make-way attempts before a standing non-owner that blocks the front owner leaves
        public const int kMakeWayCandidates = 24;       // candidates route-checked per attempt (cheap box tests first)
        public const float kStaticShiftMax = 24f;       // m a Parked slot may move along u to clear another resting box / a working path
        public const float kInlineClear = 1.0f;         // narrow sites: gap between the excavator's and the inline truck's TRUE boxes (2 x planner margin + 0.3)
        public const int kInlineRoomCycles = 240;       // dig cycles searched ahead for room behind a clamped excavator (narrow inline loading)
        public const float kExitParkRemoveSeconds = 3f; // a leaver standing at its CONNECTED exit (first in the queue) is removed this long after it stopped
        public const float kExitReach = 30f;            // ... "at its exit": its final stop within this of the exit end
        public const float kLeadEndSeconds = 20f;       // a leading C4 crew truck that cannot stay ahead to the end drives off this long before
        public const float kSpawnPathHorizon = 90f;     // s of its own crew's path owner's path a spawn spot stays out of (InWorkPath horizon)
        public const float kSpawnPathHorizonOther = 20f;// ... of another crew's path owner (the new machine leaves its spawn spot within seconds)
        public const float kLeaveTurnSearch = 24f;      // m searched towards the exit for a compact K-turn spot (leavers, no standard K-turn)
        public static readonly float[] kLeaveTurnRadii = { 5f, 4f, 3.5f, 3f, 2.5f, 2f };   // compact K-turn radii tried, largest first
    }
}
