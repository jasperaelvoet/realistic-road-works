using System;
using System.Collections.Generic;
using Game.Common;
using Game.Economy;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using ObjTransform = Game.Objects.Transform;
using ObjSubObject = Game.Objects.SubObject;

// Puppet records, rigs and the spawn / despawn primitives (ported from the machines prototype and verified in game:
// StripTypes, PuppetArchetype, GetRig, Spawn, Despawn). Structural changes happen only at Modification1 (callers).
namespace RealisticRoadWorks.V3.Machines
{
    public struct LoadStep
    {
        public double Tau;          // machine time the step applies from
        public int Pct;             // 0..100 % of the cargo capacity
        public LoadKind Kind;       // resource (a different kind respawns the truck while empty)
    }

    public enum TruckStage : byte { Home = 0, Outbound = 1, Return = 2 }

    // Managed record of one puppet. Plan is the authoritative copy; it is written to the entity when PlanDirty.
    public sealed class Puppet
    {
        public Entity E;
        public uint ProjectId;
        public MachineRole Role;
        public MachineKind Kind;
        public Entity Prefab;
        public ushort Seed;
        public float LightJitter;
        public float Scale = 1f;
        public float BoxHalfLen = 2f, BoxHalfWid = 1f, BoxOffZ;   // bounding box (scaled) for overlap tests
        public float Speed = 5f, Accel = 1.5f;                     // travel
        public MachinePlan Plan;
        public bool PlanDirty;
        public MachineActivity Activity;
        public int IntentKey = int.MinValue;
        public WorksPhase PlannedPhase = WorksPhase.None;
        public bool PlannedWorking;
        public int PlannedTrackRevision = -1;
        public double ReplanAt = double.PositiveInfinity;
        public TruckStage Stage;
        public double WorkStart = double.NegativeInfinity, WorkEnd = double.NegativeInfinity;
        public double ZoneEnter = double.NegativeInfinity, ZoneExit = double.NegativeInfinity;
        public bool Feeding;
        public LoadKind Load;
        public int AmountPct = -1;          // last written
        public int Capacity;
        public readonly List<LoadStep> Loads = new List<LoadStep>(8);
        public bool PostSite;
        public uint PostSiteSince;
        public bool LightsOff;
        public bool Outgoing;               // leaving: role None / budget-excluded / DriveOut
        public int CulledUpdates;
        public float CamDist;
        public uint SpawnUpdate;
        public uint LastCullCheck;
        public TrackData Track;
        public float3 LastPos;
        public bool HasPos;

        public float FootHalfLen, FootHalfWid, FootOff;            // ground contact footprint (tyres; scaled), 0 = use the box
        public string FootSource = "box";
        public double GuardNext;            // grader: next excavator-guard check (machine time)
        public bool Yielding;               // grader: standing aside for the excavator (guard plan)
        public bool YieldFailed;            // ... and no conflict-free slot was found (least-bad plan)
        // ---- leaving, machine report, separation guard
        public uint LeaveSince;             // RRWClock.UpdateIndex the puppet began leaving (DriveOut / role ended / release); 0 = not
        // Machine report cache (MachineReport.Zones): samples of the plan's finite parts under a fingerprint of the plan.
        public readonly System.Collections.Generic.List<ReportSample> ReportCache = new System.Collections.Generic.List<ReportSample>();
        public ulong ReportCacheKey;
        public double ReportCacheFrom;
        public bool ReportCacheValid;
        public double LeaveTau = double.NaN;    // machine time it began leaving (the leave cap runs on this clock: stops while paused)
        public double ReleaseTau = double.NaN;  // machine time it first saw its project releasing the road
        // the puppet's own leave cap (machine seconds after LeaveTau): max(kPostSiteMaxSeconds, the time its
        // leave route needs at the role's leaving limits + margin). A slow tracked machine of an interior crew is never
        // removed in view before it could reach its exit. The release cap (ReleaseTau + kPostSiteMaxSeconds) stays fixed.
        public double LeaveCap = RRWConst.kPostSiteMaxSeconds;
        public string LeaveWhy = "";
        public bool GuardHeld;              // separation guard: braked / standing for another puppet
        public double GuardHeldSince;       // machine time of the first hold of the current episode
        public Puppet GuardBlocker;         // the puppet it yielded to
        public int GuardBackOuts;           // back-outs in the current episode
        public RoadZones ZonesNow, ZonesPlan; // last machine report: zones of the box now / over the committed plan (dev)
        public bool StandStill;             // truck: the last plan is a stand-still (every route candidate conflicted)
        public double LastBackOut = double.NegativeInfinity; // machine time of the last guard back-out
        public float LeaveEndU = float.NaN; // chain u a leaving puppet heads for (guard retries)
        public bool VergeBlocked;           // the last plan wanted a verge slot and fell back to the floor lane
        // ---- stuck detection, exits
        public double WaitSince = double.NaN;   // machine time the current guard wait (GuardHeld / StandStill) began; NaN = not waiting
                                                // (never reset by the deadlock pass, unlike GuardHeldSince)
        public double LastMoveTau = double.NaN; // machine time the puppet last moved (|v| >= kStuckMoveSpeed)
        public bool Stuck;                      // waiting AND not moved for kMachineStuckSeconds (ProjectRecord.MachinesStuck counts these)
        public double StuckSince = double.NaN;  // machine time Stuck became true (rrw.check: > 2 x kMachineStuckSeconds is a problem)
        public int StuckEpisodes;               // resolution episodes without a kStuckEpisodeReset s break
        public double LastEpisodeTau = double.NegativeInfinity;
        public double NextResolveTau = double.NegativeInfinity;
        public bool OtherExitTried;             // leaver: the other connected exit was tried (stuck step 3)
        public int SigOverride = -1;            // dev rrw.mx.sig: -1 = automatic, 0..3 = SignalAnimation1/2 bits forced
        public string ExitWhy = "";             // dev: how the exit end was chosen
        public double DeadlockWait = double.NaN; // WaitSince of the wait episode last counted as a deadlock (MachineDebug.DeadlockEpisodes)
        public bool LeaveContinue;              // leaver: the leave plan reverses in chunks and needs a continuation plan
        // ---- crews, speed limits, time-sliced truck planning
        public int Crew;                        // crew (section) the puppet works for (RRWMachine.m_Crew); re-mapped at hand-overs
        public MachineLimits Lim = MachineLimits.Of(MachineRole.Excavator, WorksPhase.None); // speed table row of its role (Begin)
        public WorksPhase LimPhase = WorksPhase.None; // phase of its last ROLE plan (a leaver keeps that row: paver vs painter)
        public bool LodOut;                     // its crew is beyond the global crew LOD: removed once out of sight (like a leaver)
        public WorksPhase OverWorkPhase = WorksPhase.None, FrontLagPhase = WorksPhase.None; // counted once per phase (rrw.mx.check)
        public int OutAttempt = -1;             // time-sliced PlanTruckOutbound: attempt index (-1 = none running)
        public double OutDepart;                // ... departure (machine time) of the next attempt
        public int RetAttempt = -1;             // time-sliced PlanTruckReturn: attempt index (-1 = none running)
        public double RetExtra;                 // ... extra hold before the return of the next attempt
        public float NowU = float.NaN, NowLat; // chain state at the last report (track refresh priority, dev)
        // plan sample cache: true boxes of the committed plan on the absolute kSampleStep grid
        public readonly PlanSampleCache Samples = new PlanSampleCache();
        // speed / acceleration invariant
        public double MonTau = double.NaN, MonVTau = double.NaN;
        public float MonU, MonLat, MonV, MonVLimit, MonALimit;
        public float MaxV, MaxA, LastV, LastA, LastVLimit, LastALimit;
        public int ViolV, ViolA;
        public byte MonLeg;
        public int RunawaySamples;              // consecutive monitor samples above the runaway speed (MxConst.kRunawayFactor)
        public bool RunawayLogged;              // its runaway stop / speed clamp was logged (once per puppet)

        // ---- Road roller units. A roller is a Puppet with IsRoller set and E == Entity.Null: the plan, legs,
        // sample cache, report, guard, stuck detection, budget, LOD and leave logic are shared; every ECS read / write is skipped and
        // its pose goes to its GameObject (RollerUnit, drawn by RollerRenderSystem). Role = MachineRole.Roller (never in Roles[]).
        public bool IsRoller;
        public RollerUnit Unit;                 // the GameObject hierarchy (null once destroyed)
        public int RollerIndex;                 // 0 = Roller0 (breakdown / base course / backfill), 1 = Roller1 (finish)
        public RollerDuty Duty;                 // duty of its last plan
        public int UnitId;                      // unique roller id (GameObject name RRW_Roller_<id>, guard tie-break)
        public int PassK;                       // passes laid so far (lateral pattern index, seeded)
        public bool InView;                     // out-of-view test (frustum + distance) of the last update (spawn / removal)
        public float RootYErr;                  // dev: |pivot Y - track floor| of the last rendered frame (rrw.mx.check)
        public double PassStart = double.NaN;   // machine time its first compaction pass of the current plan starts (window check)
        // ---- IK dig cycle state of an excavator rig puppet (null = keyframed digging)
        public DigState Dig;
        public int Buckets;                     // loading truck: dumps counted since it arrived empty (DigLoad.Amount)
        public bool EventLoads;                 // loading truck: its load follows Dump events (no time-based Loads steps)
        public float GraderLiftW = -1f;         // grader bucket: lift weight 0 (skim) .. 1 (lifted), smoothed over kGraderBlendSeconds
        public double GraderLiftTau = double.NaN;
        // ---- leaver parked at a chain end that is not a connected exit
        public bool DeadEndLeave;               // its leave stop is a dead end (or a C4 Vacate leaver far from a connected exit)
        public string DeadEndWhy = "";
        // ---- dig grid re-phasing, front re-anchoring
        public bool PlannedAcross;              // Dig / Break / Spread role: its last plan is the mode-D drive-across (not the work plan)
        public int GridEpoch = -1;              // loading truck: CrewState.GridEpoch its outbound (Load DigHop) was planned on
        public int ModelGen;                    // plan sample / report cache generation (part of both fingerprints; not bumped by re-anchors)
        // ---- report cache after re-anchors, resumable scans, check budget
        public double ReportFrontUntil = double.NegativeInfinity; // report cache: last Until of a sample of a finite FrontA leg
        public bool ReportSoftStale;            // a re-anchor moved finite FrontA legs the report cache holds: rebuilt within kReportSoftRebuilds / update
        public readonly PlanPending Pend = new PlanPending();   // resumable conflict scan of a candidate plan (truck trips, leavers)
        public float ScanLead;                  // s of departure hold added to the next candidate after a scan outlived its candidate
        public byte RetForced;                  // truck: a forced return is pending (1 = not working, 2 = forced re-plan: ReplanAt = arrival + 1)
        public int YieldK = -1;                 // grader: next yield candidate (-1 = no yield search running; one candidate per update)
        public double YieldTc;                  // ... contact time that started it
        public float YieldEMin, YieldEMax, YieldGU, YieldGLat, YieldELat;
        public sbyte YieldFacing;
        public double YieldBestT;
        public float YieldBestU, YieldBestLat;
        public string YieldBestHow = "none";
        public int CheckAborts;                 // grader guard / yield: consecutive checks cut by the check budget (>= 2: run to the end)
        // ---- make-way (ChoreoSeparation.cs)
        public MachinePlan Wanted;              // the motion the guard last stopped / the last re-plan its release check rejected: the
        public double WantedTau = double.NaN;   // ... path a standing blocker must clear (MakeWay); machine time it was captured
        public double NextMakeWayTau = double.NegativeInfinity; // next make-way attempt of this puppet as a blocker
        public double AsideSince = double.NaN;  // standing aside for a mover (make-way): not counted as stuck for kAsideMaxSeconds
        public int MakeWayFails;                // consecutive failed make-way attempts as a blocker
        public int MakeWayAborts;               // consecutive make-way searches cut by the check budget (>= 2: the next one runs to its end)
        // ---- C4 swap: a C4 crew truck that stood ahead of its painter in a narrow works half leads it (Choreo.LeadParams)
        // for the rest of this phase / stage / crew
        public WorksPhase LeadPhase = WorksPhase.None;
        public int LeadStage = -1, LeadCrew = -1;
        // ---- head-on leavers: convoy speed behind a slower leaver heading for the same exit (-1 = none), exit switches made
        // to resolve a head-on meeting (ping-pong limit), next head-on evaluation of this leaver
        public float LeaveVmax = -1f;
        public Puppet ConvoyLeader;             // ... the leaver whose pace LeaveVmax follows (the cap ends when it is gone / standing)
        public double ConvoyDepart = double.NaN;// ... or: machine time it departs at full speed so it arrives right after that leader
        public int HeadOnSwitches;
        public double NextHeadOnTau = double.NegativeInfinity;
        // ---- upgrade works (mode H)
        public bool Upgrade;                    // a puppet of a mode H project: its machine report adds the verge bits (RoadZoneMath.OfBand)
        public int UwBand = -1;                 // chain band its crew works (its boxes stay in that band's machine-safe run; -1 = none)
        public bool UwDropStretch;              // it worked while its band dropped lanes: it keeps the lane-drop stretch (clear of the approach
                                                // zones at the nodes) until it is gone, whatever the window's primitive becomes (Choreo.UwStretchOf)

        public bool IsTruck => Kind == MachineKind.Truck;
        // The MiningExcavator01 clone rig (root scale 0.4, no sub-objects): the digger and the grader.
        public bool ExcavatorRig => Kind == MachineKind.Excavator || Kind == MachineKind.Grader;
        public bool Alive(EntityManager em) => IsRoller ? Unit != null && Unit.Root != null : E != Entity.Null && em.Exists(E) && !em.HasComponent<Deleted>(E);
        // Deterministic tie-break key (guard): entity index, rollers after every entity
        public long OrderKey => IsRoller ? (1L << 40) + UnitId : E.Index;
        public override string ToString() => "p" + ProjectId + "/c" + Crew + "/" + Role + (IsRoller ? RollerIndex + "(#" + UnitId + ")" : "(" + Kind + ") e=" + RRWLog.E(E));
    }

    // The roster and dig grid of ONE crew (section) of a project.
    public sealed class CrewState
    {
        public readonly Puppet[] Roles = new Puppet[(int)MachineRole.Count];
        public double DigOrigin = double.NaN; // machine time of a dig-cycle start (excavator + loading trucks share it)
        public float DigOffset = -RRWConst.kExcavatorBack, DigLo, DigHi = float.MaxValue;
        // the excavator's hop parameters; the loading trucks hop with exactly these
        public float DigHopStart = MxConst.kHopStart, DigHopDur = MxConst.kHopDur, DigVmax, DigAcc;
        public float DigRate;                 // lag time scale of the anchors (0 = none) ...
        public float DigRateU = float.NaN;    // ... and its front pivot (MachineLeg.Rate / RateU)
        public bool FeederIsB;
        // verge roles whose verge is blocked are not used in this phase (per crew: per section)
        public readonly WorksPhase[] NoVergePhase = { WorksPhase.None, WorksPhase.None, WorksPhase.None, WorksPhase.None, WorksPhase.None, WorksPhase.None };
        // crew LOD (step A2): may spawn / is beyond the global crew limit (despawn when out of sight) / camera distance of its front
        public bool SpawnOk = true, LodOut;
        public float CamDist = float.NaN;
        public int AnchorMask;                // roles whose puppet left at a hand-over: the next spawn of that role is at its anchor
        public int Live;                      // role puppets (budget / CrewsSpawned)
        // ---- roller units of the crew (CrewPlan.Roller0 / Roller1), hand-over anchor bits, in-view spawn deferral
        public readonly Puppet[] Rollers = new Puppet[RRWConst.kMaxRollersPerCrew];
        public int RollerAnchorMask;          // bit i: roller i left at a hand-over -> its next spawn is at the anchor
        public readonly double[] RollerDeferSince = { double.NaN, double.NaN };   // machine time a spawn first waited for an in-view anchor
        // ---- the dig grid's cycle (kDigCycleSeconds / kBreakCycle, or the IK DigSchedule period)
        public float DigC;                    // cycle length the grid was last planned with (0 = not yet)
        public bool DigIk;                    // the excavator runs the IK schedule (Detailed digging + rig ok): event-driven truck loads
        public DigSchedule DigSched;          // nominal schedule of the IK cycle (planner + truck departure)
        // ---- the excavator re-phases the grid so its first cycle starts on arrival; trucks planned on an older
        // epoch (and not loaded yet) re-plan their outbound onto the new phase
        public int GridEpoch;
        public int Rephases, Continued;       // dev: grid re-phases / re-plans that kept the running dig cycle

        // The dig grid state a Dig / Break plan writes (PlanDig / DigGrid). A plan that is thrown
        // away again (a guard-held excavator whose new plan the release check rejects) must not leave its re-phase behind: every
        // GridEpoch step re-plans the crew's empty loading trucks (UpdateTruck), so a rejected retry every kGuardRetry s would restart
        // them every 2 s. The Director snapshots the grid before such a re-plan and restores it when the plan is discarded.
        public struct GridSnap
        {
            public double DigOrigin;
            public float DigOffset, DigLo, DigHi, DigHopStart, DigHopDur, DigVmax, DigAcc, DigRate, DigRateU, DigC;
            public bool DigIk;
            public DigSchedule DigSched;
            public int GridEpoch, Rephases, Continued;
        }

        public GridSnap SnapGrid() => new GridSnap
        {
            DigOrigin = DigOrigin, DigOffset = DigOffset, DigLo = DigLo, DigHi = DigHi, DigHopStart = DigHopStart, DigHopDur = DigHopDur,
            DigVmax = DigVmax, DigAcc = DigAcc, DigRate = DigRate, DigRateU = DigRateU, DigC = DigC, DigIk = DigIk, DigSched = DigSched,
            GridEpoch = GridEpoch, Rephases = Rephases, Continued = Continued,
        };

        public void RestoreGrid(in GridSnap s)
        {
            DigOrigin = s.DigOrigin; DigOffset = s.DigOffset; DigLo = s.DigLo; DigHi = s.DigHi; DigHopStart = s.DigHopStart; DigHopDur = s.DigHopDur;
            DigVmax = s.DigVmax; DigAcc = s.DigAcc; DigRate = s.DigRate; DigRateU = s.DigRateU; DigC = s.DigC; DigIk = s.DigIk; DigSched = s.DigSched;
            GridEpoch = s.GridEpoch; Rephases = s.Rephases; Continued = s.Continued;
        }

        public bool Any()
        {
            for (int i = 0; i < Roles.Length; i++) if (Roles[i] != null) return true;
            for (int i = 0; i < Rollers.Length; i++) if (Rollers[i] != null) return true;
            return false;
        }

        public int RollerCount()
        {
            int n = 0;
            for (int i = 0; i < Rollers.Length; i++) if (Rollers[i] != null) n++;
            return n;
        }

        public void ResetGrid()
        {
            DigOrigin = double.NaN;
            ResetLag();
            ResetOwnerWait();
        }

        // ---- the crew's front owner waits ahead of a crew truck standing at its home in the owner's path (no room behind /
        // beside it at the section start): lower clamp of the owner's front-relative anchor for this phase (set by the crew truck's
        // FollowParams, reset with the layout / phase / model)
        public float OwnerLo = float.NegativeInfinity;
        public WorksPhase OwnerLoPhase = WorksPhase.None;
        public void ResetOwnerWait() { OwnerLo = float.NegativeInfinity; OwnerLoPhase = WorksPhase.None; }
        // A role whose spawn spot was taken / in a path owner's path is tried again at this machine time (no Puppet
        // allocation every update while it waits)
        public readonly double[] SpawnRetryAt = NewRetry();
        // A role that finished its part of the current phase / stage (a leading crew truck that drove off ahead of its
        // painter): not spawned again until the phase or stage changes (key = phase x 8 + stage + 1; 0 = none)
        public readonly int[] DoneKey = new int[(int)MachineRole.Count];
        public static int DoneKeyOf(WorksPhase ph, int stage) => (int)ph * 8 + stage + 1;
        static double[] NewRetry() { var a = new double[(int)MachineRole.Count]; for (int i = 0; i < a.Length; i++) a[i] = double.NegativeInfinity; return a; }

        public void ResetLag()
        {
            DigRate = 0f;
            DigRateU = float.NaN;
        }

        // ---- upgrade works (mode H): the chain band this crew worked in the last update (a new band = a new window: its
        // puppets leave along their old band, the crew starts again at the new band's gate)
        public int UwBand = -1;
        public int UwBandKey;                 // ... and that band's identity (kind, side, laterals, window: a re-upgrade may renumber the bands)
    }

    // Per-project machine state (ProjectRecord slot ModuleSlot.Machines).
    public sealed class MachineProjectState
    {
        // one roster per crew section (crew 0 = the first section)
        public readonly CrewState[] Crews = NewCrews();
        public int LastCrews = 1;            // crew count of the last update (re-latch -> hand-over)
        public int LastStage = -1;           // C4 stage index of the last update (C4a -> C4b -> hand-over)
        public WorksPhase LastPhase = WorksPhase.None;
        public bool LastWorking;
        public bool LastAllowed;
        public bool Initialised;
        public bool SpawnAtAnchor = true;    // first spawn after load / rebuild / ModelReset / AllowMachines gain
        public ProgressModel LastModel;
        public bool Completed;
        public uint ProjectId;
        // dev / log state of the readiness gate
        public MachineRole GateRole;         // why a crew did not spawn / re-plan this update (rrw.dump, rrw.mx.zones): role, reason, detail
        public string GatePrefix = "", GateDetail;
        public string GateWhy => GatePrefix.Length == 0 ? "" : GateRole + ": " + GatePrefix + GateDetail;
        public string LastGateLog = "", LastGateDetail;
        public void ClearGate() { GatePrefix = ""; GateDetail = null; }
        public string NoSpawnWhy = "";       // perf: cached gate text (rebuilt only when its inputs change)
        public StageSwitch WhySwitch = (StageSwitch)255;
        public RoadZones WhyReady = (RoadZones)0xFFFF;
        public WorksPhase FrontLagLogged = WorksPhase.None; // one Info line per project-phase (frontLag)
        public int NoFitLogged;              // bit per role: "box does not fit" logged

        static CrewState[] NewCrews()
        {
            var a = new CrewState[RRWConst.kMaxCrewsPerProject];
            for (int i = 0; i < a.Length; i++) a[i] = new CrewState();
            return a;
        }

        public bool Any()
        {
            for (int c = 0; c < Crews.Length; c++) if (Crews[c].Any()) return true;
            return false;
        }

        // rollers count (ProjectRecord.MachineCount)
        public int RoleCount()
        {
            int n = 0;
            for (int c = 0; c < Crews.Length; c++)
            {
                for (int i = 0; i < Crews[c].Roles.Length; i++) if (Crews[c].Roles[i] != null) n++;
                n += Crews[c].RollerCount();
            }
            return n;
        }

        public int RollerCount()
        {
            int n = 0;
            for (int c = 0; c < Crews.Length; c++) n += Crews[c].RollerCount();
            return n;
        }

        public void Detach(Puppet p)
        {
            for (int c = 0; c < Crews.Length; c++)
            {
                var r = Crews[c].Roles;
                for (int i = 0; i < r.Length; i++) if (r[i] == p) r[i] = null;
                var rl = Crews[c].Rollers;
                for (int i = 0; i < rl.Length; i++) if (rl[i] == p) rl[i] = null;
            }
        }
    }

    public static class MachineRegistry
    {
        public static readonly List<Puppet> All = new List<Puppet>(64);
        public static readonly Dictionary<Entity, Puppet> ByEntity = new Dictionary<Entity, Puppet>(64);
        public static readonly Dictionary<uint, MachineProjectState> States = new Dictionary<uint, MachineProjectState>();
        public static ClockData Clock = new ClockData { Tau0 = 0.0, Frame0 = 0, Scale = 1f };
        public static bool ClockInit;
        public static int Spawned, Despawned, Respawned;

        public static void Add(Puppet p)
        {
            All.Add(p);
            if (p.E != Entity.Null) ByEntity[p.E] = p;
            if (p.Track != null) p.Track.Users++;
        }

        public static void Remove(Puppet p)
        {
            All.Remove(p);
            if (p.E != Entity.Null && ByEntity.TryGetValue(p.E, out var cur) && cur == p) ByEntity.Remove(p.E);
            if (p.Track != null) { p.Track.Users--; p.Track = null; }
            if (MachineRegistry.States.TryGetValue(p.ProjectId, out var st)) st.Detach(p);
            // a roller unit's GameObjects go with it (never left behind)
            if (p.IsRoller) RollerWorld.Destroy(p);
        }

        public static int RollerUnits()
        {
            int n = 0;
            for (int i = 0; i < All.Count; i++) if (All[i].IsRoller) n++;
            return n;
        }

        public static void Rekey(Puppet p, Entity old)
        {
            if (old != Entity.Null && ByEntity.TryGetValue(old, out var cur) && cur == p) ByEntity.Remove(old);
            if (p.E != Entity.Null) ByEntity[p.E] = p;
        }

        public static double Now(uint renderFrame, float frameTime) => Clock.Tau(renderFrame, frameTime);

        public static void Clear()
        {
            // every roller GameObject (preload, shutdown) - plus the stray sweep
            try { RollerWorld.ClearAll("machines registry cleared"); }
            catch (Exception e) { RRWLog.ErrorOnce("machines roller clear", e); }
            All.Clear();
            ByEntity.Clear();
            States.Clear();
            ClockInit = false;
            Clock = new ClockData { Tau0 = 0.0, Frame0 = 0, Scale = 1f };
        }
    }

    // ---------------------------------------------------------------- rigs (bone names via ProceduralAnimationProperties)

    public sealed class RigSub
    {
        public int Sub;
        public Entity Mesh;
        public string[] Names;
        public int[] Parent;
        public BoneType[] Type;
        public quaternion[] Rest;
        public float3[] ObjPos;
        public float3[] RestScale;
        // The IK model (DigArm) needs the local rest positions, the raw rest scales and the sub mesh transform
        public float3[] Pos;
        public float3[] RawScale;
        public float3 SubPos;
        public quaternion SubRot = quaternion.identity;

        // Bone index by name in this sub mesh (-1 = none)
        public int Find(string name)
        {
            for (int i = 0; i < Names.Length; i++) if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public bool IsDescendant(int j, int ancestor)
        {
            int guard = 0;
            int k = j >= 0 && j < Parent.Length ? Parent[j] : -1;
            while (k >= 0 && guard++ < 64)
            {
                if (k == ancestor) return true;
                k = Parent[k];
            }
            return false;
        }
    }

    public struct PistonPair { public int Sub, A, B; public Entity Mesh; }

    public sealed class Rig
    {
        public Entity Prefab;
        public readonly List<RigSub> Subs = new List<RigSub>();
        public readonly List<PistonPair> Pistons = new List<PistonPair>();
        public bool Any => Subs.Count > 0;

        public bool Find(string name, out int sub, out int bone)
        {
            sub = -1; bone = -1;
            foreach (var s in Subs)
                for (int b = 0; b < s.Names.Length; b++)
                    if (string.Equals(s.Names[b], name, StringComparison.OrdinalIgnoreCase)) { sub = s.Sub; bone = b; return true; }
            return false;
        }

        public RigSub GetSub(int sub)
        {
            foreach (var s in Subs) if (s.Sub == sub) return s;
            return null;
        }
    }

    public static class Rigs
    {
        private static readonly Dictionary<Entity, Rig> s_Rigs = new Dictionary<Entity, Rig>();

        public static void Clear() => s_Rigs.Clear();

        public static Rig Get(EntityManager em, PrefabSystem ps, Entity prefab)
        {
            if (s_Rigs.TryGetValue(prefab, out var rig)) return rig;
            rig = new Rig { Prefab = prefab };
            try
            {
                if (em.HasBuffer<SubMesh>(prefab))
                {
                    var subs = em.GetBuffer<SubMesh>(prefab, true);
                    var meshes = new List<SubMesh>(subs.Length);
                    for (int j = 0; j < subs.Length; j++) meshes.Add(subs[j]);
                    for (int j = 0; j < meshes.Count; j++)
                    {
                        var mesh = meshes[j].m_SubMesh;
                        if (!em.HasBuffer<ProceduralBone>(mesh)) continue;
                        var pb = em.GetBuffer<ProceduralBone>(mesh, true);
                        int n = pb.Length;
                        var rs = new RigSub
                        {
                            Sub = j, Mesh = mesh, Names = new string[n], Parent = new int[n], Type = new BoneType[n],
                            Rest = new quaternion[n], ObjPos = new float3[n], RestScale = new float3[n],
                            Pos = new float3[n], RawScale = new float3[n],
                            // as the dig prototype's rig read it (verified in game: modelVsLive < 0.01 m): the sub mesh transform as stored
                            SubPos = meshes[j].m_Position, SubRot = meshes[j].m_Rotation,
                        };
                        if (math.lengthsq(rs.SubRot.value) < 0.5f) rs.SubRot = quaternion.identity;
                        ProceduralAnimationProperties pap = null;
                        try { if (ps.TryGetPrefab<RenderPrefab>(mesh, out var rp) && rp != null) rp.TryGet(out pap); }
                        catch { pap = null; }
                        for (int i = 0; i < n; i++)
                        {
                            var b = pb[i];
                            rs.Parent[i] = b.m_ParentIndex;
                            rs.Type[i] = b.m_Type;
                            rs.Rest[i] = b.m_Rotation;
                            rs.ObjPos[i] = b.m_ObjectPosition;
                            rs.RestScale[i] = math.all(math.abs(b.m_Scale) > 1e-4f) ? b.m_Scale : new float3(1f, 1f, 1f);
                            rs.Pos[i] = b.m_Position;
                            rs.RawScale[i] = b.m_Scale;
                            string name = null;
                            if (pap != null && pap.m_Bones != null && b.m_BindIndex >= 0 && b.m_BindIndex < pap.m_Bones.Length)
                                name = pap.m_Bones[b.m_BindIndex].name;
                            rs.Names[i] = string.IsNullOrEmpty(name) ? "bone" + i : name;
                        }
                        rig.Subs.Add(rs);
                    }
                }
                // hydraulic pairs "<prefix>Piston..01" + "<prefix>Piston..02" in the same submesh (verified in game)
                foreach (var s in rig.Subs)
                {
                    for (int a = 0; a < s.Names.Length; a++)
                    {
                        string na = s.Names[a];
                        if (na == null || na.IndexOf("Piston", StringComparison.OrdinalIgnoreCase) < 0 || !na.EndsWith("01", StringComparison.Ordinal)) continue;
                        string prefix = na.Substring(0, na.Length - 2);
                        for (int b = 0; b < s.Names.Length; b++)
                        {
                            if (b == a || !string.Equals(s.Names[b], prefix + "02", StringComparison.Ordinal)) continue;
                            if (s.IsDescendant(b, a) || s.IsDescendant(a, b)) continue;
                            rig.Pistons.Add(new PistonPair { Sub = s.Sub, A = a, B = b, Mesh = s.Mesh });
                            break;
                        }
                    }
                }
            }
            catch (Exception e) { RRWLog.ErrorOnce("machines rig", e); }
            s_Rigs[prefab] = rig;
            return rig;
        }
    }

    // ---------------------------------------------------------------- spawn / despawn

    public static class PuppetFactory
    {
        private static readonly Dictionary<long, EntityArchetype> s_Archetypes = new Dictionary<long, EntityArchetype>();
        private static ComponentType[] s_Strip;
        private static readonly List<Entity> s_Stack = new List<Entity>(16);

        public static void Clear() => s_Archetypes.Clear();

        static ComponentType[] StripTypes()
        {
            if (s_Strip != null) return s_Strip;
            s_Strip = new[]
            {
                ComponentType.ReadWrite<Game.Vehicles.Car>(),
                ComponentType.ReadWrite<Game.Vehicles.CarNavigation>(),
                ComponentType.ReadWrite<Game.Vehicles.CarNavigationLane>(),
                ComponentType.ReadWrite<Game.Vehicles.CarCurrentLane>(),
                ComponentType.ReadWrite<Game.Vehicles.Blocker>(),
                ComponentType.ReadWrite<Game.Vehicles.CarTrailer>(),
                ComponentType.ReadWrite<Game.Vehicles.Controller>(),
                ComponentType.ReadWrite<Game.Vehicles.CarTrailerLane>(),
                ComponentType.ReadWrite<Game.Vehicles.LayoutElement>(),
                ComponentType.ReadWrite<Game.Vehicles.MaintenanceVehicle>(),
                ComponentType.ReadWrite<Game.Vehicles.RoadMaintenanceVehicle>(),
                ComponentType.ReadWrite<Game.Vehicles.ParkMaintenanceVehicle>(),
                ComponentType.ReadWrite<Game.Vehicles.ParkedCar>(),
                ComponentType.ReadWrite<Game.Vehicles.WorkVehicle>(),
                ComponentType.ReadWrite<Game.Objects.BlockedLane>(),
                ComponentType.ReadWrite<Game.Objects.Stopped>(),
                ComponentType.ReadWrite<Game.Objects.TripSource>(),
                ComponentType.ReadWrite<Game.Objects.Unspawned>(),
                ComponentType.ReadWrite<Game.Pathfind.PathOwner>(),
                ComponentType.ReadWrite<Game.Pathfind.PathElement>(),
                ComponentType.ReadWrite<Game.Pathfind.PathInformation>(),
                ComponentType.ReadWrite<Game.Common.Target>(),
                ComponentType.ReadWrite<Game.Common.Owner>(),
                ComponentType.ReadWrite<Game.Simulation.ServiceDispatch>(),
            };
            return s_Strip;
        }

        // Filtered archetype: the vanilla moving archetype minus AI / navigation / path components, plus
        // LivePath (never saved) + MachinePlan + RRWMachine + RRWDerived. Null when the prefab cannot move.
        public static bool Archetype(EntityManager em, Entity prefab, bool noSub, out EntityArchetype arch)
        {
            arch = default;
            long key = ((long)prefab.Index << 20) ^ ((long)prefab.Version << 2) ^ (noSub ? 1L : 0L);
            if (s_Archetypes.TryGetValue(key, out arch) && arch.Valid) return true;
            if (!em.Exists(prefab) || !em.HasComponent<ObjectData>(prefab)) return false;
            var od = em.GetComponentData<ObjectData>(prefab);
            if (!od.m_Archetype.Valid) return false;
            var types = od.m_Archetype.GetComponentTypes(Allocator.Temp);
            var strip = StripTypes();
            var keep = new List<ComponentType>(types.Length + 6);
            bool hasMoving = false, hasFrames = false, hasUpdateFrame = false, hasCreated = false, hasUpdated = false;
            int subIdx = ComponentType.ReadWrite<ObjSubObject>().TypeIndex.Index;
            for (int i = 0; i < types.Length; i++)
            {
                var ct = types[i];
                bool drop = false;
                for (int k = 0; k < strip.Length; k++) if (strip[k].TypeIndex == ct.TypeIndex) { drop = true; break; }
                if (noSub && ct.TypeIndex.Index == subIdx) drop = true;
                if (drop) continue;
                keep.Add(ct);
                if (ct.TypeIndex == ComponentType.ReadWrite<Moving>().TypeIndex) hasMoving = true;
                if (ct.TypeIndex == ComponentType.ReadWrite<TransformFrame>().TypeIndex) hasFrames = true;
                if (ct.TypeIndex == ComponentType.ReadWrite<UpdateFrame>().TypeIndex) hasUpdateFrame = true;
                if (ct.TypeIndex == ComponentType.ReadWrite<Created>().TypeIndex) hasCreated = true;
                if (ct.TypeIndex == ComponentType.ReadWrite<Updated>().TypeIndex) hasUpdated = true;
            }
            types.Dispose();
            if (!(hasMoving && hasFrames && hasUpdateFrame)) return false;
            if (!hasCreated) keep.Add(ComponentType.ReadWrite<Created>());
            if (!hasUpdated) keep.Add(ComponentType.ReadWrite<Updated>());
            keep.Add(ComponentType.ReadWrite<LivePath>());
            keep.Add(ComponentType.ReadWrite<MachinePlan>());
            keep.Add(ComponentType.ReadWrite<RRWMachine>());
            keep.Add(ComponentType.ReadWrite<RRWDerived>());
            arch = em.CreateArchetype(keep.ToArray());
            s_Archetypes[key] = arch;
            return true;
        }

        public static Resource ResourceOf(LoadKind k)
        {
            switch (k)
            {
                case LoadKind.Ore: return Resource.Ore;
                case LoadKind.Coal: return Resource.Coal;
                default: return Resource.Stone;
            }
        }

        // Geometry of a prefab (scaled): box half extents + forward centre offset, travel speed defaults.
        public static void Measure(EntityManager em, Puppet p)
        {
            float s = p.Scale > 0f ? p.Scale : 1f;
            if (em.HasComponent<ObjectGeometryData>(p.Prefab))
            {
                var g = em.GetComponentData<ObjectGeometryData>(p.Prefab);
                var b = g.m_Bounds;
                float hl = (b.max.z - b.min.z) * 0.5f * s, hw = (b.max.x - b.min.x) * 0.5f * s;
                if (hl > 0.3f && hl < 40f) p.BoxHalfLen = hl;
                if (hw > 0.3f && hw < 20f) p.BoxHalfWid = hw;
                float off = (b.max.z + b.min.z) * 0.5f * s;
                p.BoxOffZ = math.abs(off) < 20f ? off : 0f;
            }
            // speeds come from the role's MachineLimits row (Choreo.Begin refreshes it per phase)
            p.Lim = MachineLimits.Of(p.Role, p.LimPhase);
            p.Speed = p.Lim.VFwd;
            p.Accel = p.Lim.Accel;
            if (em.HasComponent<DeliveryTruckData>(p.Prefab)) p.Capacity = em.GetComponentData<DeliveryTruckData>(p.Prefab).m_CargoCapacity;
        }

        // Ground footprint the pose samples (MachineMotion.Pose: Y = plane through the 4 contact points, evaluated at the
        // origin). Wheeled puppets use their TYRE bones (axle span, track width, axle midpoint offset): sampling the bumper
        // box corners (a 0.9 x box) puts the contact points 1-2 m beyond the axles, so on a sag / floor step the body
        // floats above the floor under its centre (seen in game: TruckB +0.174 m in C1). The excavator rig (tracks;
        // the box includes the boom) keeps the chassis box.
        public static void MeasureFootprint(EntityManager em, PrefabSystem ps, Puppet p)
        {
            p.FootHalfLen = p.FootHalfWid = p.FootOff = 0f;
            p.FootSource = "box";
            if (p.ExcavatorRig || p.Prefab == Entity.Null) return;
            try
            {
                var rig = Rigs.Get(em, ps, p.Prefab);
                if (!rig.Any || !em.HasBuffer<SubMesh>(p.Prefab)) return;
                var subs = em.GetBuffer<SubMesh>(p.Prefab, true);
                float zMin = float.MaxValue, zMax = float.MinValue, xMax = 0f;
                int n = 0;
                foreach (var rs in rig.Subs)
                {
                    if (rs.Sub < 0 || rs.Sub >= subs.Length) continue;
                    var sm = subs[rs.Sub];
                    bool hasT = (sm.m_Flags & SubMeshFlags.HasTransform) != 0;
                    for (int i = 0; i < rs.Type.Length; i++)
                    {
                        if (rs.Type[i] != BoneType.RollingTire && rs.Type[i] != BoneType.SteeringTire) continue;
                        float3 op = rs.ObjPos[i];
                        if (hasT) op = sm.m_Position + math.rotate(sm.m_Rotation, op);
                        zMin = math.min(zMin, op.z);
                        zMax = math.max(zMax, op.z);
                        xMax = math.max(xMax, math.abs(op.x));
                        n++;
                    }
                }
                float s = p.Scale > 0f ? p.Scale : 1f;
                if (n < 2 || zMax - zMin < 1f || xMax < 0.3f) return;
                p.FootHalfLen = 0.5f * (zMax - zMin) * s;
                p.FootOff = 0.5f * (zMax + zMin) * s;
                p.FootHalfWid = xMax * s;
                p.FootSource = "tyres(" + n + ")";
            }
            catch (Exception e) { RRWLog.ErrorOnce("machines footprint", e); }
        }

        public static void Footprint(Puppet p, ref MachinePlan plan)
        {
            if (p.FootHalfLen > 0f && p.FootHalfWid > 0f)
            {
                plan.HalfLen = math.clamp(p.FootHalfLen, 0.6f, 6f);
                plan.HalfWid = math.clamp(p.FootHalfWid, 0.5f, 3f);
                plan.FootOff = math.clamp(p.FootOff, -3f, 3f);
                return;
            }
            float hl = p.BoxHalfLen, hw = p.BoxHalfWid;
            if (p.ExcavatorRig) { hl *= 0.5f; hw *= 0.85f; }
            plan.HalfLen = math.clamp(hl * 0.9f, 0.6f, 6f);
            plan.HalfWid = math.clamp(hw * 0.9f, 0.5f, 3f);
            plan.FootOff = 0f;
        }

        // Creates the puppet entity at its plan pose (Modification1). Returns Entity.Null on failure.
        public static Entity Create(EntityManager em, Puppet p, in MachinePlan plan, float3 pos, quaternion rot, bool noSub, LoadKind load, int amountPct)
        {
            if (!Archetype(em, p.Prefab, noSub, out var arch)) return Entity.Null;
            MxPerf.Begin(MxT.X_EcsStructural);
            var created = em.CreateEntity(arch, 1, Allocator.Temp);
            MxPerf.End(MxT.X_EcsStructural);
            MxPerf.Count(MxC.EcsCreate);
            Entity e = created[0];
            created.Dispose();
            em.SetComponentData(e, new PrefabRef { m_Prefab = p.Prefab });
            em.SetComponentData(e, new ObjTransform(pos, rot));
            if (em.HasComponent<PseudoRandomSeed>(e)) em.SetComponentData(e, new PseudoRandomSeed(p.Seed));
            em.SetComponentData(e, plan);
            em.SetComponentData(e, new RRWMachine { m_ProjectId = p.ProjectId, m_Role = (byte)p.Role, m_IsTrailer = 0, m_Crew = (byte)math.clamp(p.Crew, 0, 255) });
            em.SetComponentData(e, new RRWDerived { m_Site = Entity.Null, m_ProjectId = p.ProjectId, m_Group = (byte)(p.PostSite ? DerivedGroup.PostSite : DerivedGroup.Machine) });
            if (em.HasComponent<Game.Vehicles.DeliveryTruck>(e))
            {
                int cap = p.Capacity;
                int amount = (int)math.round(cap * math.clamp(amountPct, 0, 100) / 100f);
                em.SetComponentData(e, new Game.Vehicles.DeliveryTruck
                {
                    m_State = Game.Vehicles.DeliveryTruckFlags.Loaded,
                    m_Resource = ResourceOf(load == LoadKind.None ? LoadKind.Stone : load),
                    m_Amount = amount,
                });
                p.AmountPct = math.clamp(amountPct, 0, 100);
            }
            p.Load = load;
            return e;
        }

        // Marks the puppet and every descendant Deleted (Modification1).
        public static void Delete(EntityManager em, Entity root)
        {
            if (root == Entity.Null || !em.Exists(root)) return;
            MxPerf.Begin(MxT.X_EcsStructural);
            s_Stack.Clear();
            s_Stack.Add(root);
            int guard = 0;
            while (s_Stack.Count > 0 && guard++ < 256)
            {
                Entity e = s_Stack[s_Stack.Count - 1];
                s_Stack.RemoveAt(s_Stack.Count - 1);
                if (!em.Exists(e)) continue;
                if (em.HasBuffer<ObjSubObject>(e))
                {
                    var buf = em.GetBuffer<ObjSubObject>(e, true);
                    for (int i = 0; i < buf.Length; i++) s_Stack.Add(buf[i].m_SubObject);
                }
                if (!em.HasComponent<Deleted>(e)) { em.AddComponent<Deleted>(e); MxPerf.Count(MxC.EcsDeleted); }
            }
            MxPerf.End(MxT.X_EcsStructural);
        }

        // Truck load (verified in game): DeliveryTruck amount + BatchesUpdated on the pile sub-objects (QuantityUpdateSystem re-reads it).
        public static void SetAmount(EntityManager em, Puppet p, int pct)
        {
            pct = math.clamp(pct, 0, 100);
            MxPerf.Count(MxC.SetAmounts);
            if (!p.Alive(em) || !em.HasComponent<Game.Vehicles.DeliveryTruck>(p.E)) { p.AmountPct = pct; return; }
            var d = em.GetComponentData<Game.Vehicles.DeliveryTruck>(p.E);
            d.m_State |= Game.Vehicles.DeliveryTruckFlags.Loaded;
            d.m_Amount = (int)math.round(p.Capacity * pct / 100f);
            em.SetComponentData(p.E, d);
            if (em.HasBuffer<ObjSubObject>(p.E))
            {
                var buf = em.GetBuffer<ObjSubObject>(p.E, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    var s = buf[i].m_SubObject;
                    if (em.Exists(s) && !em.HasComponent<Deleted>(s) && !em.HasComponent<BatchesUpdated>(s)) s_Stack.Add(s);
                }
                MxPerf.Begin(MxT.X_EcsStructural);
                foreach (var s in s_Stack) em.AddComponent<BatchesUpdated>(s);
                MxPerf.End(MxT.X_EcsStructural);
                MxPerf.Count(MxC.EcsBatchesUpdated, s_Stack.Count);
                s_Stack.Clear();
            }
            p.AmountPct = pct;
        }

        // Re-tags a puppet's descendants (post-site: the Director GC exempts group PostSite).
        public static void Retag(EntityManager em, Entity root, uint projectId, DerivedGroup rootGroup, DerivedGroup partGroup)
        {
            if (root == Entity.Null || !em.Exists(root)) return;
            MxPerf.Begin(MxT.X_EcsStructural);
            MxPerf.Count(MxC.EcsRetag);
            em.SetComponentData(root, new RRWDerived { m_Site = Entity.Null, m_ProjectId = projectId, m_Group = (byte)rootGroup });
            s_Stack.Clear();
            if (em.HasBuffer<ObjSubObject>(root))
            {
                var buf = em.GetBuffer<ObjSubObject>(root, true);
                for (int i = 0; i < buf.Length; i++) s_Stack.Add(buf[i].m_SubObject);
            }
            int guard = 0;
            while (s_Stack.Count > 0 && guard++ < 128)
            {
                var e = s_Stack[s_Stack.Count - 1];
                s_Stack.RemoveAt(s_Stack.Count - 1);
                if (!em.Exists(e) || em.HasComponent<Deleted>(e)) continue;
                EcsUtil.TagDerived(em, e, Entity.Null, projectId, partGroup);
                if (em.HasBuffer<ObjSubObject>(e))
                {
                    var buf = em.GetBuffer<ObjSubObject>(e, true);
                    for (int i = 0; i < buf.Length; i++) s_Stack.Add(buf[i].m_SubObject);
                }
            }
            MxPerf.End(MxT.X_EcsStructural);
        }
    }
}
