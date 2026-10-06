using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Director
{
    // Director-private tuning. Shared constants live in RRWConst; these only steer Director housekeeping.
    internal static class DirConst
    {
        public const int kGeometryRoundRobin = 4;          // records whose geometry hash is re-checked per update (besides Updated edges)
        public const int kSectionRetryUpdates = 120;       // a new edge without lanes yet re-measures its section until lanes exist
        public const int kTrimRefreshUpdates = 64;         // trims / shared-node marks are refreshed at least this often per project
        public const int kIconSelfHealUpdates = 256;       // icon self-heal interval
        public const int kClearIntervalUpdates = 30;       // tree clearing cadence per project
        public const float kClearCanopyAllowance = 1.5f;   // tree trunks within HalfWidth + kTopsoilMargin (FullDig) + this are cleared
        // Overhang = the canopy reaches more than kClearOverhangTolerance over the STRIP (S = HalfWidth +
        // kTopsoilMargin in FullDig, HalfWidth otherwise): trunkDist - c < S - tolerance and trunkDist <= S + c, with the canopy radius
        // c = min(rendered canopy radius, kClearCanopyMax). The search reach is S + kClearCanopyMax (trunks up to there).
        public const float kClearOverhangTolerance = 0.5f;
        public const float kClearCanopyMax = 8f;           // canopy radius cap
        public const int kCompletionTimeoutUpdates = 900;  // completion waits at most this long for Ground (then finishes anyway, logged)
        public const int kGroundUnknownGraceUpdates = 120; // Ground never reported (module missing / faulted): finish after this
        public const float kModelDriftReset = 0.01f;       // ProgressModel hard reset when it drifted this far from the accrued p
        public const float kRateEpsilon = 1e-12f;
        public const float kCancelInstantSlack = 0.005f;   // a CancelInstant above kInstantCancelProgress + this is a normal cancel
        public const float kFocusHysteresis = 20f;         // FocusCrew switches only when another crew front is this much nearer
        public const int kUwDetourGraceUpdates = 120;      // a started one-way window keeps its closure this long while Traffic re-checks
                                                           // the detour for a new project revision (then it weakens without one)
        public const float kUwMinSafeWidth = 0.1f;         // a band's machine-safe range narrower than this counts as none (allowance)
        public const float kUwHandOverReach = 2f;          // a puppet of the previous project within the road half width + this of a
                                                           // taken-over edge (between its ends) keeps the carried lane groups closed
    }

    // Director slot of an EdgeRecord (ModuleSlot.Director). Main thread only.
    internal sealed class DirEdgeState
    {
        public uint AddedUpdate;            // RRWClock.UpdateIndex the record was created
        public uint PreRollUntil;           // Replaced roads: HideWanted held false until UpdateIndex >= this (pre-roll)
        public RuntimeFlags PendingOnce;    // flags raised for exactly one frame (NeedsRebuild, ModelReset)
        public WorksPhase LastPhase = WorksPhase.None;
        public bool LastWanted;
        public Entity IconPrefab;           // icon currently requested for this edge (Entity.Null = none)
        public uint IconCheckedUpdate;
        public bool SectionFromLanes;       // EdgeSection was measured with car lanes present
        public uint SectionRetryUntil;
        public bool RecheckGeometry;        // the edge carried Updated last frame: re-check the hash once more
        public Entity Prefab;               // PrefabRef of the edge (preview matching)
        public float4 BoundsXZ;             // (minX, minZ, maxX, maxZ) of the curve control hull (preview matching prefilter)
        public float CurveLength;
        public uint CornerHash;             // EdgeGeometry end corners: a road drawn into a works node moves the trims
        public uint EligKey;                // prefab / Upgraded / Elevation / composition flags: re-run DigEligible on change
        public uint LaneSig;                // EcsUtil.LaneSignature at the last section measurement: a changed lane set re-measures
        // ---- upgrade works (mode H)
        public int UwLayoutTried = int.MinValue;  // GeometryRevision the lane layout of the new road was last read for
        public uint UwKeepOutKey;           // geometry revision, building accesses and chain coordinates of the driveway keep-outs
        public uint UwChainIndexOk;         // last update UpgradeEdgeState.ChainIndex belonged to the project's runtime (rrw.check)
    }

    // Director slot of a ProjectRecord.
    internal sealed class DirProjectState
    {
        public double Carry;                // fractional work frames not yet added to m_WorkDone
        public bool HaveModel;
        public bool ResetModel;             // hard-reset the ProgressModel in this frame's accrual (+ RuntimeFlags.ModelReset)
        public bool ModelResetThisFrame;
        public bool HardResetThisFrame;     // ModelReset from ResetModel (new / load / SetProgress / cancel), not a drift reset
        public bool LastWorking;
        public float LastRatePF;
        public uint AnchorFrame;            // sim frame of the last (re)anchor
        public bool GateInit;               // demolition mobilisation gate decided
        public bool GateActive;
        public uint GateSimFrames;
        public bool ClearInit;
        public float ClearedU;              // ONE-crew-equivalent clearing front (chain u): trees were cleared in every crew section
                                            // [B_i, B_i + (B_i+1 - B_i) * ClearedU / U] of the ClearCrews layout (one crew = [0, ClearedU])
        public int ClearCrews = 1;          // crew layout ClearedU refers to (the C0 latch; a re-latch re-scans the new sections)
        public uint LastClearUpdate;
        public bool ClearSkipped;           // replaced / cancelled / migrated / loaded in C3+: no clearing at all
        public bool FinalSweepDone;         // full-chain sweep at the first C3+ frame (the reveal)
        public bool C1SweepDone;            // full-chain catch-up sweep (footprint + strip overhangs) on the update the
                                            // project enters C1 / C2 (also a load or dev jump into them), before the C1 machines spawn
        public int C1SweepDeleted;          // trees that sweep removed (dev: must be ~0 when the C0 sweeps worked)
        public int TreesDeleted;            // trees this project removed (footprint + overhang)
        public int TreesOverhang;           // ... of which only the canopy reached over the road surface
        public int TreesOwnedKept;          // owned trees inside the footprint left alone (progressive sweeps)
        public HashSet<Entity> TreeSnapshot;  // free-standing trees in the footprint when clearing started (ClearInit, also after a
                                              // load): the only trees the C3 reveal sweep may delete (player trees placed later are kept)
        public int TreesLateKept;           // trees in the footprint not in TreeSnapshot (placed after the start), left alone
        public bool WasAllowed;             // machine allowance hysteresis
        public bool SortDirty = true;       // Edges must be re-sorted by ChainLo
        public uint TrimCheckedUpdate;
        public int TrimKey = int.MinValue;  // revision snapshot the trims were computed for
        public bool CompletingSeen;
        public uint CompletingSince;        // UpdateIndex of frame N (first Complete frame)
        public bool CompletionTimeoutLogged;
        // ---- release gate. ProjectRecord.ReleaseSince is the shared value; these are private.
        public bool Hold;                   // PhasePlan.MachinesHoldRoad this frame (after the sticky rule): carriageway stays Closed
        public bool OpenedAfterRelease;     // sticky: the gate cleared once -> never Open -> Closed -> Open again
        public uint OpenedAt;               // UpdateIndex the gate cleared (the Ground wait of TryFinish counts from here)
        public bool HoldLogged;             // "waiting for machines" logged once
        public bool CapWarned;              // "machines did not leave within the cap" warned once
        public bool CallOff;                // demolition called off in D0: no accrual, sites removed once the gate clears
        public RoadZones LoggedOpenLanes;   // last OpenLanes logged (Info on every change)
        public bool LegacyPausedLogged;
        public bool MaintenanceLogged;
        public int LastEdgeCount;

        // ---- staged traffic management. Runtime only: after a load everything here starts
        //      fresh and is re-derived from p: at p = SwitchP the switch re-runs from Vacate, past it from Drain.
        public float C4FrontMax;               // highest C4a painter front of this C4 window (StageContext.FrontFloor)
        public float SwitchDoneP = float.NaN;  // SwitchP of the last COMPLETED switch (Ready -> None); NaN = none in this window
        public bool SwitchSkipped;             // C4: the side swap was skipped for this window (car half blocked / gate off when the
                                               // switch point was reached, or a Track/Stop/Narrow block while a car half was open)
        public bool HoldAtSwitch;              // C4: p is clamped AT SwitchP while the switch runs (entered via Vacate, not past it)
        public uint HoldSinceSim;              // RRWClock.SimFrame the accrual hold began (0 = no hold); rrw.check "p held too long"
        public uint StepStartUpdate;           // RRWClock.UpdateIndex the current switch step began
        public uint StepStartSim;              // RRWClock.SimFrame the current switch step began (the caps run on game time)
        public uint SwitchStartSim;            // RRWClock.SimFrame the running switch began (log: duration)
        public bool ProgressJumped;            // dev SetProgress / JumpPhase / cancel this frame: the switch is re-derived from p
        public StageSwitch ForceStep;          // rrw.stage.step: step requested by the dev command (consumed by the next update)
        public bool ForceSkip, ForceReset;     // rrw.stage.step skip / reset
        public bool ForcePending;
        // car half verdict + latch
        public bool CarHalfLatched;            // a car half was APPLIED open in this C4 (EdgeRecord.OpenLanesApplied): Track / Stop / Narrow
                                               // keep it open (the swap is skipped instead), OneWay / DeadEnd close it (re-close path)
        public bool CarHalfWaiting;            // an edge is not classified yet (Traffic Classify / CarHalves stale): no new car half
        public bool LoggedCarHalfInit;
        public bool LoggedCarHalf;             // last logged verdict
        public StageBlockReason LoggedReason;
        public bool LoggedWaiting;
        public RoadZones LoggedSoft;
        public RoadZones LoggedReady;
        public bool LoggedExitsInit;
        public bool LoggedExitStart, LoggedExitEnd;
        // The chain-level car half verdict. ExitsKnown = ComputeTrims set ExitAtStart / ExitAtEnd for this
        // record (false until StepTrims ran for it after a load / rebuild / new project: the verdict is "waiting" until then).
        public bool ExitsKnown;
        public StageBlockReason ChainBlock;    // PhasePlan.ChainCarHalfBlock(ExitAtStart, ExitAtEnd, RRWGates.DeadEndRule) (None while unknown)
        public uint ChainBlockSince;           // RRWClock.UpdateIndex ChainBlock became != None (0 = none): rrw.check grace for the re-close
        public uint ChainBlockSinceSim;        // RRWClock.SimFrame of the same moment (the SOFT re-close drains on game time)
        public int ChainBlocks;                // car-half evaluations blocked by the chain rule (dev)
        // ProjectRecord.Rollers latch bookkeeping (rrw.check: Rollers > 0 while RollersOn was off for more than one phase)
        public WorksPhase RollersOffPhase = WorksPhase.None;   // phase in which RRWSetting.RollersOn was first seen off (None = on)
        public bool RollersLatched;            // the roller latch ran at least once for this record
        // re-close path: groups that left OpenLanes for safety (not through a switch: OneWay / DeadEnd verdict, staged opening
        // switched off, ...) stay SOFT until every edge reports them drained (or kDrainTimeoutFrames), so machines never enter them
        // while vehicles are still on them
        public RoadZones ReClose;
        public readonly uint[] ReCloseSinceUpdate = new uint[16];
        public readonly uint[] ReCloseSinceSim = new uint[16];
        // anti-churn, per group bit (project level: every visible Closed edge of a project carries the same groups)
        public readonly uint[] LastCloseUpdate = new uint[16];
        public readonly uint[] LastOpenUpdate = new uint[16];
        public RoadZones ChurnLogged;          // groups whose held re-opening was logged in this window
        public readonly uint[] SoftSinceSim = new uint[16];   // SimFrame a group entered SoftZones (rrw.check: soft too old)
        public uint ReadyComputedUpdate;       // update WorkZonesReady was last computed (ProjectGate)
        public bool LastHeld;                  // accrual held last update (log on change)
        public int LoggedStuck;                // last logged ProjectRecord.MachinesStuck state (> 0 = stuck)
        public uint StuckSinceSim;

        // ---- crew sections (latch). ProjectRecord.Crews / WorkSeconds / FocusCrew are the shared values.
        public bool CrewsLatched;              // the latch ran at least once for this record (false after a load / registry rebuild)
        public WorksPhase CrewsPhase = WorksPhase.None;  // phase the latched count belongs to
        public int CrewsRevision = int.MinValue;         // ProjectRecord.Revision at the last latch (or the last revision checked)
        public float CrewsU = -1f;                       // ChainLength at the last latch (sections are a function of n and U only)
        public bool CrewsRelatch;              // C4 stage switch Vacate -> Swap happened this update: re-latch (layout change invisible)
        public int CrewsOverride;              // dev rrw.crews: forced count (0 = the PhasePlan.CrewCount rule); applied at the next latch
        public int CrewsOverrideApplied;       // override value the last latch used (a change re-latches at once)
        public string CrewsWhy = "";           // reason of the last latch (dev)
        public uint CrewsLatchedUpdate;        // RRWClock.UpdateIndex of the last latch
        public int CrewsSpawnedLogged = -1;    // last logged ProjectRecord.CrewsSpawned (Machines report; Verbose on change)
        public int NearCrews;                  // crew fronts within kMachineSpawnRadius of the camera (allowance reservation)
        public bool AllowanceDegraded;         // the allowance reserved only one crew budget this update (global cap)
        // The full focus scan (every crew front -> ChainMap.Point) runs every 8 updates per project
        // (staggered by id) or when the crew count changed; in between only the focus crew's front is evaluated.
        public int FocusScanCrews = -1;
        // Cached chain centre-line samples for ChainDistance (17 per edge), rebuilt when an edge's
        // EdgeArc object changes (RefreshGeometry allocates a new one on every geometry change) or the edge list changes.
        public Unity.Mathematics.float2[] ChainSamples = new Unity.Mathematics.float2[0];
        public int ChainSampleCount;
        public object[] ChainSampleArcs = new object[0];
        public int ChainSampleEdges = -1;

        // ---- upgrade works (mode H). Runtime only: after a load the runtime is rebuilt from the saved tails and the window
        //      switch is re-derived from p (at a window start it re-runs from Vacate, past it from Drain).
        public uint UwTailKey;                  // hash of the tails (and edges) the UpgradeRuntime was built from (0 = not built)
        public bool UwRebuilt;                  // registered by a registry rebuild (load): no adoption-time tree clearing
        public bool UwFresh;                    // adopted as a new project in this session (not after a load)
        public int UwStartDone = int.MinValue;  // window whose start switch completed (or that started with nothing to switch)
        public int UwSwapDone = int.MinValue;   // re-marking window whose half swap completed
        public int UwSwitchWindow = -1;         // window the running switch belongs to
        public bool UwSwitchIsSwap;             // the running switch is the half swap inside the re-marking window
        public bool UwSwapOn;                   // StageContext.SwapOn of the re-marking window this update
        public int UwLoggedWindow = int.MinValue;   // layout window / primitive / reason last logged
        public BandTraffic UwLoggedPrim = BandTraffic.Undecided;
        public StageBlockReason UwLoggedReason;
        public uint UwDetourStaleSince;         // update a started Half window began waiting for a detour verdict of the current revision
        public uint UwDerivedOk;                // last update the band data (HasCar, chain indices) was current (rrw.check)
        public int CrewsWindow = int.MinValue;  // layout window the latched crew count belongs to (upgrade works: one crew per band)
        // Hand-over: lane groups that were closed on edges this project took over from another running upgrade project (a
        // re-upgrade, an undo, a split; CHAIN frame of this project). They stay closed (and the road Closed) until no machine of
        // the previous project is left in them, or kCompletionMachineWaitSimFrames after the hand-over.
        public RoadZones UwCarry;
        public readonly List<uint> UwCarryFrom = new List<uint>(2);   // the previous project(s)
        public uint UwCarrySinceSim;            // RRWClock.SimFrame of the (latest) hand-over
        public uint UwCarrySinceUpdate;         // RRWClock.UpdateIndex of the same moment (machine reports must be newer)
    }

    // State shared between the Director (Mod1), PreviewHideSystem (Mod2B) and its catch-up (Mod5); Tools reads PreviewHidden
    // (WorksTagSystem). Main thread only; cleared on preload.
    internal static class DirectorShared
    {
        // Nodes whose Hidden the Director applied (transition rule: Hidden is removed only by us and only once).
        public static readonly HashSet<Entity> HiddenNodes = new HashSet<Entity>();
        // Temp edges/nodes PreviewHide marked with TempFlags.Hidden (never the Hidden component on a Temp). When
        // ApplyNetSystem turns such a temp into a permanent entity (Create path: only Temp removed), the Director adopts
        // it by SET MEMBERSHIP (works edge -> HiddenApplied; works node -> node pass) or releases it.
        public static readonly HashSet<Entity> PreviewHidden = new HashSet<Entity>();
        // The Temps PreviewHide flagged in ITS LAST RUN (this frame from Mod2B on; cleared at the start of every run).
        public static readonly HashSet<Entity> PreviewHiddenNow = new HashSet<Entity>();
        public static int HiddenEdgeCount;          // registry edges with Hidden applied (last Director frame)
        public static int PreviewEdgesHidden;       // PreviewHide stats of its last run
        public static int PreviewNodesHidden;
        public static long PreviewHideTotal;
        public static int PreviewAdopted, PreviewReleased;
        public static int PreviewReleasedTemps;     // stale Temps handed back to vanilla (flag cleared)
        public static int PreviewComponentStripped; // old-version Hidden components removed from Temps (must stay 0 in a fresh session)
        public static long PreviewCatchUpTotal;     // children flagged by the Mod5 catch-up
        public static int ProbeUnhiddenWorks;       // preview probe (Mod5, while Temp edges exist): hidden works edges/nodes NOT Hidden
        public static long ProbeUnhiddenTotal;
        public static uint ProbeUnhiddenLastUpdate;
        public static int ReleasesOpened, ReleasesCapped;   // release gate: opened on a clear report / on the safety cap
        // stage switch counters (rrw.director.state / rrw.stage header)
        public static int SwitchesStarted, SwitchesDone, SwitchesAborted, SwitchesSkipped;
        public static int CrewLatchesRestored;   // latches taken from the saved SiteFlags bits 12..15
        public static int VacateCaps, DrainTimeouts, ReadyTimeouts, HoldCaps, ReCloses, ReCloseTimeouts, ChurnHeld;
        // crew latch counters (rrw.director.state / rrw.crews header)
        public static int CrewLatches, CrewChanges, CrewChangesMidPhase, FocusSwitches, AllowanceDegraded;
        // upgrade works counters (rrw.director.state / rrw.uw.dir header)
        public static int UpgradesAdopted, UpgradeRebuilds, UpgradeStamps, UpgradeCancelsRefused, UpgradesEnded, UpgradeEdgesLeft, UpgradeMixes;

        public static void Reset()
        {
            SwitchesStarted = SwitchesDone = SwitchesAborted = SwitchesSkipped = 0;
            VacateCaps = DrainTimeouts = ReadyTimeouts = HoldCaps = ReCloses = ReCloseTimeouts = ChurnHeld = 0;
            CrewLatches = CrewChanges = CrewChangesMidPhase = FocusSwitches = AllowanceDegraded = CrewLatchesRestored = 0;
            UpgradesAdopted = UpgradeRebuilds = UpgradeStamps = UpgradeCancelsRefused = UpgradesEnded = UpgradeEdgesLeft = UpgradeMixes = 0;
            HiddenNodes.Clear();
            PreviewHidden.Clear();
            PreviewHiddenNow.Clear();
            HiddenEdgeCount = 0;
            PreviewEdgesHidden = 0;
            PreviewNodesHidden = 0;
            PreviewHideTotal = 0;
            PreviewAdopted = 0;
            PreviewReleased = 0;
            PreviewReleasedTemps = 0;
            PreviewComponentStripped = 0;
            PreviewCatchUpTotal = 0;
            ProbeUnhiddenWorks = 0;
            ProbeUnhiddenTotal = 0;
            ProbeUnhiddenLastUpdate = 0;
            ReleasesOpened = 0;
            ReleasesCapped = 0;
        }
    }
}
