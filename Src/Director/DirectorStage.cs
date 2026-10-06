using System;
using System.Collections.Generic;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using NetSubLane = Game.Net.SubLane;

namespace RealisticRoadWorks.V3.Director
{
    // Staged traffic management, Director part:
    //  * RRWCity (left-hand traffic, NA theme) from CityConfigurationSystem, once per update;
    //  * ProjectRecord.ExitAtStart / ExitAtEnd (leavers drive to a chain end that connects to the road network);
    //  * CarHalfAllowed + StageBlockReason with the car-half latch, BuildingSidewalks (D0);
    //  * StageContext, the stage switch state machine (None -> Vacate -> Swap -> Drain -> Ready -> None) and the accrual hold
    //    (C4: p clamped AT SwitchP from Vacate to Ready -> None; D0: p kept below kD1 until the house sidewalks are drained);
    //  * SoftZones (stage soft + the re-close path), OpenLanes with the anti-churn rule, WorkZonesReady.
    // Everything fails closed: no drain report -> WorkZonesReady = None (machines plan nothing new); no classification -> no car
    // half; a switch that cannot complete runs into its game-time caps (Warn) instead of holding p forever.
    public partial class WorksDirectorSystem
    {
        private CityConfigurationSystem m_CityConfig;
        private Entity m_LastTheme = Entity.Null;
        private bool m_LastThemeNa;

        // ------------------------------------------------------------------ RRWCity (once per update)

        private void UpdateCity()
        {
            try
            {
                if (m_CityConfig == null) m_CityConfig = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
                if (m_CityConfig == null) return;
                bool lht = m_CityConfig.leftHandTraffic;
                Entity theme = m_CityConfig.defaultTheme;
                if (theme != m_LastTheme)
                {
                    m_LastTheme = theme;
                    m_LastThemeNa = false;
                    if (theme != Entity.Null && m_PrefabSystem.TryGetPrefab<PrefabBase>(theme, out var pb) && pb != null)
                    {
                        // prefab dump: ThemePrefab "European" / "North American" (assetPrefix EU / NA)
                        string prefix = pb is ThemePrefab tp ? tp.assetPrefix : null;
                        m_LastThemeNa = (prefix != null && prefix.StartsWith("NA", StringComparison.OrdinalIgnoreCase))
                                        || (pb.name != null && pb.name.StartsWith("North", StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (!RRWCity.Known || RRWCity.LeftHandTraffic != lht || RRWCity.NaTheme != m_LastThemeNa)
                    RRWLog.Info("director: city leftHandTraffic=" + lht + " theme=" + (m_LastThemeNa ? "NA" : "EU"));
                RRWCity.LeftHandTraffic = lht;
                RRWCity.NaTheme = m_LastThemeNa;
                RRWCity.Known = true;
            }
            catch (Exception e) { RRWLog.ErrorOnce("director city configuration", e); }
        }

        // ------------------------------------------------------------------ exits

        // A chain end connects to the road network when its node has a live edge that is not part of this project, has car lanes
        // and is not a hidden works edge (a VISIBLE works road of another project counts). Called from ComputeTrims.
        private bool IsExitNode(Entity node, uint projectId)
        {
            var em = EntityManager;
            if (!EcsUtil.Alive(em, node) || !em.HasBuffer<ConnectedEdge>(node)) return false;
            var buf = em.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < buf.Length; i++)
            {
                Entity ce = buf[i].m_Edge;
                if (ce == Entity.Null || !em.Exists(ce) || em.HasComponent<Deleted>(ce) || em.HasComponent<Temp>(ce)) continue;
                if (SiteRegistry.TryGetEdge(ce, out var r) && (r.ProjectId == projectId || r.HiddenApplied)) continue;
                if (!em.HasBuffer<NetSubLane>(ce)) continue;
                var lanes = em.GetBuffer<NetSubLane>(ce, true);
                for (int k = 0; k < lanes.Length; k++)
                    if (em.HasComponent<NetCarLane>(lanes[k].m_SubLane)) return true;
            }
            return false;
        }

        private void LogExits(ProjectRecord proj, DirProjectState ps)
        {
            if (ps.LoggedExitsInit && ps.LoggedExitStart == proj.ExitAtStart && ps.LoggedExitEnd == proj.ExitAtEnd) return;
            ps.LoggedExitsInit = true;
            ps.LoggedExitStart = proj.ExitAtStart;
            ps.LoggedExitEnd = proj.ExitAtEnd;
            RRWLog.Info("director: project #" + proj.Id + " exits start=" + (proj.ExitAtStart ? "connected" : "dead end")
                        + " end=" + (proj.ExitAtEnd ? "connected" : "dead end") + " -> leavers drive to u=" + RRWLog.F(PhasePlan.ExitU(proj.View())));
        }

        // ------------------------------------------------------------------ car half verdict + latch

        private static bool TrapReason(StageBlockReason r) => r == StageBlockReason.OneWay || r == StageBlockReason.DeadEnd || r == StageBlockReason.LaneLayout;

        // m_PE / m_PS hold the project's site edges (Accrue). Writes proj.CarHalfAllowed / StageBlockReason / BuildingSidewalks.
        private void EvaluateCarHalf(ProjectRecord proj, DirProjectState ps, WorksKind kind, VisualMode mode, WorksPhase phase)
        {
            // BuildingSidewalks: union of the classified edges' house sides (chain frame)
            RoadZones houses = RoadZones.None;
            bool construction = kind == WorksKind.Construction && mode == VisualMode.FullDig;
            bool waiting = false;
            StageBlockReason first = StageBlockReason.None, trap = StageBlockReason.None;
            Entity firstEdge = Entity.Null;
            bool anyApplied = false;
            // The CHAIN-level dead-end rule, before the per-edge loop. The per-edge DeadEnd reason
            // only comes from EdgeRecord.CutEdge ("removing the edge disconnects buildings"), which an isolated road never sets. A car
            // half may open only when BOTH chain ends connect to the road network; it is a TRAP reason (an applied half re-closes
            // through SOFT). Until StepTrims computed this record's exits (after a load / rebuild / new project) the verdict waits.
            bool exitsKnown = ps.ExitsKnown;
            var chainBlock = exitsKnown ? PhasePlan.ChainCarHalfBlock(proj.ExitAtStart, proj.ExitAtEnd, RRWGates.DeadEndRule) : StageBlockReason.None;
            if (chainBlock != StageBlockReason.None)
            {
                if (ps.ChainBlockSince == 0) { ps.ChainBlockSince = m_Now == 0 ? 1u : m_Now; ps.ChainBlockSinceSim = RRWClock.SimFrame; }
            }
            else ps.ChainBlockSince = 0;
            ps.ChainBlock = chainBlock;
            if (construction && chainBlock != StageBlockReason.None)
            {
                trap = chainBlock;   // wins over every per-edge reason (the per-edge loop below never replaces a set trap)
                first = chainBlock;
                ps.ChainBlocks++;
            }
            for (int i = 0; i < m_PE.Count; i++)
            {
                if (!SiteRegistry.TryGetEdge(m_PE[i], out var rec)) { waiting = true; continue; }
                bool reversed = RoadZoneMath.ChainReversed(m_PS[i].m_ChainU0, m_PS[i].m_ChainU1);
                bool classified = rec.Classified && rec.ClassifyReversed == reversed;
                if (classified) houses |= rec.BuildingZones & RoadZones.Sidewalks;
                if ((rec.OpenLanesApplied & RoadZones.Carriageway) != 0) anyApplied = true;
                if (!construction) continue;
                if (!classified || rec.CarHalvesRevision != rec.GeometryRevision || rec.CarHalvesReversed != reversed) { waiting = true; continue; }
                var r = PhasePlan.CarHalfBlock(rec.Section, reversed, rec.CarHalves, rec.HasTrack, rec.StopZones, rec.CutEdge,
                                               RRWGates.C4Swap, RRWGates.DeadEndRule);
                if (r == StageBlockReason.None) continue;
                if (first == StageBlockReason.None) { first = r; firstEdge = m_PE[i]; }
                if (trap == StageBlockReason.None && TrapReason(r)) trap = r;
            }
            if (m_PE.Count == 0) waiting = true;
            if (construction && !exitsKnown) waiting = true;   // no new car half before the chain verdict is known
            proj.BuildingSidewalks = houses;
            if (!construction)
            {
                proj.CarHalfAllowed = false;
                proj.StageBlockReason = StageBlockReason.None;
                ps.CarHalfWaiting = false;
                ps.CarHalfLatched = false;
                return;
            }
            // latch: a car half APPLIED open in this C4
            if (phase != WorksPhase.Finishing) ps.CarHalfLatched = false;
            else if (anyApplied) ps.CarHalfLatched = true;

            // a trap reason (vehicles could be stuck) wins over the others on any edge
            var reason = trap != StageBlockReason.None ? trap : first;
            bool allowed;
            bool latchedKeep = false;
            bool chainTrap = chainBlock != StageBlockReason.None;
            // A known chain trap closes even while an edge waits for its classification (a latched half re-closes)
            if (chainTrap) { allowed = false; reason = chainBlock; waiting = false; }
            else if (waiting) { allowed = ps.CarHalfLatched; reason = StageBlockReason.None; }   // never open a new half unclassified
            else if (reason == StageBlockReason.None) allowed = true;
            else if (ps.CarHalfLatched && !TrapReason(reason)) { allowed = true; latchedKeep = true; }
            else allowed = false;

            if (latchedKeep && !ps.SwitchSkipped && float.IsNaN(ps.SwitchDoneP)
                && (proj.Switch == StageSwitch.None || proj.Switch == StageSwitch.Vacate))
            {
                // Latch: keep the open half, skip the swap (a Vacate in progress is aborted: the old layout still stands)
                ps.SwitchSkipped = true;
                DirectorShared.SwitchesSkipped++;
                if (proj.Switch == StageSwitch.Vacate) AbortSwitch(proj, ps, "car half latched open, " + reason + " block");
                RRWLog.Info("director: project #" + proj.Id + " C4 side swap skipped: " + reason + " block while a car half is open (latched, edge " + RRWLog.E(firstEdge) + ")");
            }

            proj.CarHalfAllowed = allowed;
            proj.StageBlockReason = reason;
            ps.CarHalfWaiting = waiting;
            if (!ps.LoggedCarHalfInit || ps.LoggedCarHalf != allowed || ps.LoggedReason != reason || ps.LoggedWaiting != waiting)
            {
                ps.LoggedCarHalfInit = true;
                ps.LoggedCarHalf = allowed;
                ps.LoggedReason = reason;
                ps.LoggedWaiting = waiting;
                RRWLog.Info("director: project #" + proj.Id + " car half " + (allowed ? "allowed" : "blocked")
                            + " (" + (waiting ? (exitsKnown ? "waiting for classification" : "waiting for the chain exits") : reason == StageBlockReason.None ? "ok"
                               : chainTrap ? reason + " chain: exits start=" + (proj.ExitAtStart ? "yes" : "no") + " end=" + (proj.ExitAtEnd ? "yes" : "no")
                                             + (proj.ExitAtStart || proj.ExitAtEnd ? " (RRWGates.DeadEndRule)" : " (isolated road)")
                               : reason.ToString() + (firstEdge != Entity.Null ? " edge " + RRWLog.E(firstEdge) : ""))
                            + (latchedKeep ? ", latched open: swap skipped" : "") + (ps.CarHalfLatched ? ", latched" : "") + ") in " + phase);
            }
        }

        // ------------------------------------------------------------------ stage context + switch machine + accrual hold

        // Smallest WorkDone whose p = done / req is >= x (C4 hold: p sits exactly at / just above SwitchP).
        private static uint DoneAtOrAbove(float x, uint req)
        {
            double d = math.ceil(x * (double)req);
            uint u = (uint)math.clamp(d, 0.0, req);
            while (u > 0 && (u - 1) / (float)req >= x) u--;
            while (u < req && u / (float)req < x) u++;
            return u;
        }

        // Largest WorkDone whose p = done / req is < x (D0 hold: p stays inside D0, the hide never starts).
        private static uint DoneBelow(float x, uint req)
        {
            double d = math.floor(x * (double)req);
            uint u = (uint)math.clamp(d, 0.0, req);
            while (u > 0 && u / (float)req >= x) u--;
            while (u + 1 < req && (u + 1) / (float)req < x) u++;
            return u;
        }

        private static float SwitchTol(uint req) => math.max(1e-5f, 1.5f / math.max(1u, req));

        private bool SwitchDone(DirProjectState ps, float sp) => !float.IsNaN(ps.SwitchDoneP) && math.abs(ps.SwitchDoneP - sp) <= 1e-5f;

        private ProjectView StageView(ProjectRecord proj, WorksKind kind, VisualMode mode, float p, in StageContext ctx)
        {
            var v = proj.View();
            v.Kind = kind;
            v.Mode = mode;
            v.Phase = PhasePlan.PhaseOf(kind, p, out float f);
            v.F = f;
            v.Ctx = ctx;
            v.Switch = proj.Switch;
            v.BuildingSidewalks = proj.BuildingSidewalks;
            return v;
        }

        private StageContext BuildCtx(ProjectRecord proj, DirProjectState ps)
        {
            bool started = proj.Switch != StageSwitch.None || !float.IsNaN(ps.SwitchDoneP);
            return new StageContext
            {
                Staged = m_S.StagedOpeningOn,
                D0Sidewalks = RRWGates.D0Sidewalks,
                FrontFloor = ps.C4FrontMax,
                CarHalfAllowed = proj.CarHalfAllowed,
                // the gate decides only before the switch starts; once it ran, the C4b layout stays (no flip back on a gate change)
                SwapOn = (RRWGates.C4Swap || started) && proj.CarHalfAllowed && !ps.SwitchSkipped,
            };
        }

        private void StepTo(ProjectRecord proj, DirProjectState ps, StageSwitch next, string why)
        {
            var prev = proj.Switch;
            uint simAge = unchecked(RRWClock.SimFrame - ps.StepStartSim);
            proj.Switch = next;
            ps.StepStartUpdate = m_Now;
            ps.StepStartSim = RRWClock.SimFrame;
            // Every C4a crew has left the works half and the C4b fronts start at their section starts, so the
            // crew layout may change invisibly here; the latch in Accrue (after StageStep) consumes the flag (C4 only)
            if (prev == StageSwitch.Vacate && next == StageSwitch.Swap) ps.CrewsRelatch = true;
            RRWLog.Info("director: project #" + proj.Id + " " + proj.Kind + " switch " + prev + " -> " + next + " (" + why + ") p=" + RRWLog.F(proj.Progress)
                        + " switchP=" + RRWLog.F(proj.SwitchP) + (prev != StageSwitch.None ? " step took " + simAge + " sim frames" : "")
                        + (ps.HoldSinceSim != 0 ? " hold " + unchecked(RRWClock.SimFrame - ps.HoldSinceSim) + " sim frames" : ""));
        }

        private void AbortSwitch(ProjectRecord proj, DirProjectState ps, string why)
        {
            if (proj.Switch == StageSwitch.None) return;
            RRWLog.Info("director: project #" + proj.Id + " switch " + proj.Switch + " aborted (" + why + ") p=" + RRWLog.F(proj.Progress));
            DirectorShared.SwitchesAborted++;
            proj.Switch = StageSwitch.None;
            ps.HoldAtSwitch = false;
            ps.HoldSinceSim = 0;
        }

        private void FinishSwitch(ProjectRecord proj, DirProjectState ps, string why)
        {
            uint dur = unchecked(RRWClock.SimFrame - ps.SwitchStartSim);
            RRWLog.Info("director: project #" + proj.Id + " " + proj.Kind + " switch Ready -> None (" + why + "): done in " + dur + " sim frames, accrual resumes at p="
                        + RRWLog.F(proj.Progress));
            DirectorShared.SwitchesDone++;
            ps.SwitchDoneP = proj.SwitchP;
            proj.Switch = StageSwitch.None;
            ps.HoldAtSwitch = false;
            ps.HoldSinceSim = 0;
        }

        // Every project edge has a fresh drain report newer than `since`, and every edge that has lanes of `groups` reports them in
        // ZonesDrained. An edge without a fresh report -> false (fails closed: the caller's timeout decides).
        private bool GroupsDrained(ProjectRecord proj, RoadZones groups, uint since)
        {
            groups &= RoadZones.AllLanes;
            if (groups == RoadZones.None) return true;
            int seen = 0;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                if (!SiteRegistry.TryGetEdge(proj.Edges[i], out var rec)) continue;
                seen++;
                if (rec.ZonesReportUpdate == 0 || unchecked(m_Now - rec.ZonesReportUpdate) > (uint)RRWConst.kZonesReportStaleUpdates) return false;
                if (unchecked((int)(rec.ZonesReportUpdate - since)) <= 0) return false;
                var present = rec.ZonesPresent & groups;
                if ((rec.ZonesDrained & present) != present) return false;
            }
            return seen > 0;
        }

        // Groups of `groups` that some project edge reports drained only by the timeout (traffic still on them).
        private static RoadZones TimedOutOn(ProjectRecord proj, RoadZones groups)
        {
            RoadZones r = RoadZones.None;
            for (int i = 0; i < proj.Edges.Count; i++)
                if (SiteRegistry.TryGetEdge(proj.Edges[i], out var rec)) r |= rec.ZonesTimedOut & groups;
            return r;
        }

        // Accrue, after the accrual added work to `done` and before it is written back: StageContext, the switch machine and the
        // accrual hold. Returns true when p is held this update (ProgressModel rate 0, carry dropped). p0 = p before this update's
        // accrual (dev SetProgress / cancel already applied), so a crossing is always accrual, never a jump.
        private bool StageStep(ProjectRecord proj, DirProjectState ps, WorksKind kind, VisualMode mode, uint req, float p0, ref uint done)
        {
            var phase0 = PhasePlan.PhaseOf(kind, p0, out float f0);
            EvaluateCarHalf(proj, ps, kind, mode, phase0);

            bool c4Window = kind == WorksKind.Construction && mode == VisualMode.FullDig && phase0 == WorksPhase.Finishing;
            bool d0Window = kind == WorksKind.Demolition && mode == VisualMode.FullDig && phase0 == WorksPhase.BreakUp;
            float tolP = SwitchTol(req);

            // ---- dev SetProgress / JumpPhase / cancel: the switch is re-derived from p (like a load)
            if (ps.ProgressJumped)
            {
                ps.ProgressJumped = false;
                if (proj.Switch != StageSwitch.None) AbortSwitch(proj, ps, "progress set");
                ps.SwitchDoneP = float.NaN;
                ps.SwitchSkipped = false;
                ps.C4FrontMax = 0f;
            }
            if (!c4Window) { ps.SwitchSkipped = false; ps.C4FrontMax = 0f; }
            if (!c4Window && !d0Window) { ps.SwitchDoneP = float.NaN; ps.ChurnLogged = RoadZones.None; }

            // ---- dev rrw.stage.step skip / reset
            if (ps.ForcePending && (ps.ForceSkip || ps.ForceReset))
            {
                if (ps.ForceReset)
                {
                    AbortSwitch(proj, ps, "rrw.stage.step reset");
                    ps.SwitchDoneP = float.NaN;
                    ps.SwitchSkipped = false;
                    RRWLog.Info("director: project #" + proj.Id + " rrw.stage.step reset: the switch re-runs from p");
                }
                else
                {
                    AbortSwitch(proj, ps, "rrw.stage.step skip");
                    ps.SwitchSkipped = true;
                    DirectorShared.SwitchesSkipped++;
                    RRWLog.Info("director: project #" + proj.Id + " rrw.stage.step skip: C4 side swap skipped for this window");
                }
                ps.ForcePending = ps.ForceSkip = ps.ForceReset = false;
                ps.ForceStep = StageSwitch.None;
            }

            var ctx = BuildCtx(proj, ps);

            // ---- skip: the C4 switch point is reached while no swap may run (definitive car-half block or the gate off)
            if (c4Window && ctx.Staged && proj.Switch == StageSwitch.None && !ps.SwitchSkipped && float.IsNaN(ps.SwitchDoneP)
                && f0 >= RRWConst.kC4SwapF - RRWConst.kStageSwitchTolF && !ps.CarHalfWaiting && !ctx.SwapOn)
            {
                ps.SwitchSkipped = true;
                DirectorShared.SwitchesSkipped++;
                RRWLog.Info("director: project #" + proj.Id + " C4 side swap skipped at f=" + RRWLog.F(f0) + " ("
                            + (!RRWGates.C4Swap ? "gate c4swap off" : "car half " + (proj.CarHalfAllowed ? "allowed" : "blocked: " + proj.StageBlockReason)) + "): C4a lasts until teardown");
                ctx = BuildCtx(proj, ps);
            }

            // ---- pre-clamp: accrual never jumps over an undone switch point in one update (time-lapse); D0 never over kD1
            var stage0 = PhasePlan.Stage(StageView(proj, kind, mode, p0, ctx), ctx);
            float sp0 = stage0.SwitchP;
            if (!float.IsNaN(sp0) && proj.Switch == StageSwitch.None && !SwitchDone(ps, sp0) && p0 < sp0 - tolP)
            {
                uint cap = d0Window ? DoneBelow(stage0.HoldP, req) : DoneAtOrAbove(sp0, req);
                if (done > cap) done = cap;
            }

            float p = done / (float)req;
            var view = StageView(proj, kind, mode, p, ctx);
            var stage = PhasePlan.Stage(view, ctx);
            float sp = stage.SwitchP;
            bool hasSwitch = !float.IsNaN(sp) && ctx.Staged && !ps.CallOff && !proj.Releasing && view.Phase != WorksPhase.Complete;
            bool c4 = kind == WorksKind.Construction && view.Phase == WorksPhase.Finishing;
            bool d0 = kind == WorksKind.Demolition && view.Phase == WorksPhase.BreakUp;

            // ---- a running switch that no longer applies (phase left, setting / gate / verdict changed, cancel, release)
            if (proj.Switch != StageSwitch.None && (!hasSwitch || float.IsNaN(proj.SwitchP) || math.abs(proj.SwitchP - sp) > 1e-5f))
                AbortSwitch(proj, ps, !hasSwitch ? "no switch in this stage any more" : "switch point moved");
            if (hasSwitch && proj.Switch == StageSwitch.None && p < sp - tolP && !float.IsNaN(ps.SwitchDoneP))
            {
                ps.SwitchDoneP = float.NaN;   // p back below a completed switch point (dev): the switch runs again
                ps.SwitchSkipped = false;
            }

            // ---- dev rrw.stage.step <vacate|swap|drain|ready>
            if (ps.ForcePending)
            {
                var want = ps.ForceStep;
                ps.ForcePending = false;
                ps.ForceStep = StageSwitch.None;
                if (!hasSwitch) RRWLog.Info("director: project #" + proj.Id + " rrw.stage.step " + want + " ignored: no switch in this stage (" + view.Phase + ")");
                else
                {
                    if (proj.Switch == StageSwitch.None)
                    {
                        DirectorShared.SwitchesStarted++;
                        ps.SwitchStartSim = RRWClock.SimFrame;
                        ps.SwitchDoneP = float.NaN;
                    }
                    proj.SwitchP = sp;
                    if (c4 && want == StageSwitch.Vacate) { done = DoneAtOrAbove(sp, req); p = done / (float)req; }
                    ps.HoldAtSwitch = c4 && math.abs(p - sp) <= tolP;
                    if (d0 && want == StageSwitch.Vacate) want = StageSwitch.Swap;   // D0 has no machine vacate
                    StepTo(proj, ps, want, "rrw.stage.step");
                }
            }

            // ---- entry: crossing (accrual) or at the point -> Vacate; past it (load, dev jump, late verdict) -> Drain
            if (hasSwitch && proj.Switch == StageSwitch.None && !SwitchDone(ps, sp) && !(c4 && ps.SwitchSkipped))
            {
                bool at = math.abs(p0 - sp) <= tolP;
                bool crossed = p0 < sp - tolP && p >= sp - tolP;
                bool past = !at && !crossed && p0 > sp + tolP;
                if (at || crossed || past)
                {
                    proj.SwitchP = sp;
                    ps.SwitchStartSim = RRWClock.SimFrame;
                    DirectorShared.SwitchesStarted++;
                    if (past)
                    {
                        ps.HoldAtSwitch = false;
                        StepTo(proj, ps, StageSwitch.Drain, (c4 ? "C4b" : "D0 sidewalks") + " entered past the switch point (load / dev): drain first");
                    }
                    else if (d0)
                    {
                        ps.HoldAtSwitch = false;
                        StepTo(proj, ps, StageSwitch.Swap, "D0 f=" + RRWLog.F(RRWConst.kPedCloseF) + ": house sidewalks close, accrual held below kD1 until drained");
                    }
                    else
                    {
                        ps.HoldAtSwitch = true;
                        StepTo(proj, ps, StageSwitch.Vacate, "C4a done at f=" + RRWLog.F(RRWConst.kC4SwapF) + ": machines leave the works half");
                    }
                }
            }

            // ---- transitions (one step per update at most)
            if (proj.Switch != StageSwitch.None)
            {
                uint stepAge = unchecked(RRWClock.SimFrame - ps.StepStartSim);
                uint holdAge = ps.HoldSinceSim != 0 ? unchecked(RRWClock.SimFrame - ps.HoldSinceSim) : 0u;
                bool holdCap = holdAge >= (uint)(RRWConst.kCompletionMachineWaitSimFrames + 300);
                var stageNow = PhasePlan.Stage(StageView(proj, kind, mode, p, ctx), ctx);
                switch (proj.Switch)
                {
                    case StageSwitch.Vacate:
                        if (ps.StepStartUpdate == m_Now) break;
                        if (d0) { StepTo(proj, ps, StageSwitch.Swap, "D0: nothing to vacate"); break; }
                        {
                            bool fresh = proj.MachinesReportFresh(m_Now) && unchecked((int)(proj.MachinesReportUpdate - ps.StepStartUpdate)) > 0;
                            var oldWorks = stageNow.Works & RoadZones.AllLanes;
                            // MachineZones is the union over EVERY crew's puppets (Machines' report), so this waits for all
                            // crews of the C4a works half; crews without puppets (LOD) have nothing there
                            if (fresh && (proj.MachineZones & oldWorks) == RoadZones.None)
                                StepTo(proj, ps, StageSwitch.Swap, "fresh machine report: " + oldWorks + " clear of all " + proj.Crews + " crew(s), "
                                       + proj.CrewsSpawned + " with puppets, " + proj.MachineCount + " puppets");
                            else if (stepAge >= (uint)RRWConst.kCompletionMachineWaitSimFrames)
                            {
                                DirectorShared.VacateCaps++;
                                RRWLog.Warn("director: project #" + proj.Id + " machines did not vacate " + oldWorks + " within kCompletionMachineWaitSimFrames ("
                                            + stepAge + " sim frames, report " + (proj.MachinesReportFresh(m_Now) ? "zones " + proj.MachineZones + " stuck " + proj.MachinesStuck : "missing/stale")
                                            + "): swapping anyway (machines broke the leave contract)");
                                StepTo(proj, ps, StageSwitch.Swap, "vacate cap");
                            }
                        }
                        break;
                    case StageSwitch.Swap:
                        if (ps.StepStartUpdate != m_Now) StepTo(proj, ps, StageSwitch.Drain, "next update");
                        break;
                    case StageSwitch.Drain:
                        {
                            if (ps.StepStartUpdate == m_Now) break;
                            var soft = stageNow.Soft;
                            if (GroupsDrained(proj, soft, ps.StepStartUpdate)) StepTo(proj, ps, StageSwitch.Ready, soft + " drained on every edge");
                            else if (stepAge >= (uint)RRWConst.kDrainTimeoutFrames || holdCap)
                            {
                                DirectorShared.DrainTimeouts++;
                                if (stepAge < (uint)RRWConst.kDrainTimeoutFrames) DirectorShared.HoldCaps++;
                                RRWLog.Warn("director: project #" + proj.Id + " " + soft + " not reported drained after " + stepAge + " sim frames"
                                            + (holdCap ? " (hold cap)" : " (kDrainTimeoutFrames)") + ": ready anyway");
                                StepTo(proj, ps, StageSwitch.Ready, "drain timeout");
                            }
                        }
                        break;
                    case StageSwitch.Ready:
                        {
                            if (ps.StepStartUpdate == m_Now) break;
                            if (d0)
                            {
                                // The hide (D1) waits until the house sidewalks are really empty; a timed-out drain
                                // keeps p below kD1 until Traffic sees them clean (or releases them at kDrainReadyCapFrames)
                                var busySw = TimedOutOn(proj, RoadZones.Sidewalks);
                                if (busySw == RoadZones.None) { FinishSwitch(proj, ps, "D0 house sidewalks closed"); break; }
                                if (holdCap)
                                {
                                    DirectorShared.HoldCaps++;
                                    RRWLog.Warn("director: project #" + proj.Id + " D0 house sidewalks " + busySw + " still had pedestrians after the hold cap: the hide starts anyway");
                                    FinishSwitch(proj, ps, "D0 hold cap");
                                }
                                break;
                            }
                            var works = stageNow.Works & RoadZones.AllLanes;
                            if (ps.ReadyComputedUpdate != 0 && unchecked((int)(ps.ReadyComputedUpdate - ps.StepStartUpdate)) >= 0
                                && (proj.WorkZonesReady & works) == works)
                                FinishSwitch(proj, ps, works + " in WorkZonesReady");
                            else if (stepAge >= (uint)RRWConst.kDrainTimeoutFrames || holdCap)
                            {
                                DirectorShared.ReadyTimeouts++;
                                if (stepAge < (uint)RRWConst.kDrainTimeoutFrames) DirectorShared.HoldCaps++;
                                RRWLog.Warn("director: project #" + proj.Id + " " + works + " not in WorkZonesReady (" + proj.WorkZonesReady + ") after " + stepAge
                                            + " sim frames" + (holdCap ? " (hold cap)" : "") + ": accrual resumes, machines keep waiting for it");
                                FinishSwitch(proj, ps, "ready timeout");
                            }
                        }
                        break;
                }
            }

            // ---- accrual hold
            bool held = false;
            // D0: the hide (D1) never starts while house sidewalks are draining: a running switch, or sidewalks in the re-close
            // path (e.g. the d0sidewalks gate switched off mid-D0); p0's window too, so a time-lapse step never jumps into D1
            bool d0Hold = (d0 || d0Window) && (proj.Switch != StageSwitch.None || (ps.ReClose & RoadZones.Sidewalks) != RoadZones.None);
            if (d0Hold)
            {
                uint cap = DoneBelow(PhasePlan.PhaseEnd(WorksPhase.BreakUp), req);
                if (done >= cap) { done = cap; held = true; }
            }
            else if (c4 && proj.Switch != StageSwitch.None && ps.HoldAtSwitch)
            {
                done = DoneAtOrAbove(proj.SwitchP, req);
                held = true;
            }
            if (held) { if (ps.HoldSinceSim == 0) ps.HoldSinceSim = math.max(1u, RRWClock.SimFrame); }
            else ps.HoldSinceSim = 0;

            // C4 front floor = the highest C4a painter front of this window (monotonic C4 front, StageContext.FrontFloor)
            if (c4)
            {
                var vf = StageView(proj, kind, mode, done / (float)req, BuildCtx(proj, ps));
                if (vf.SwapActive && PhasePlan.Stage(vf).Index == 0) ps.C4FrontMax = math.max(ps.C4FrontMax, PhasePlan.MainFront(vf));
            }
            proj.StageCtx = BuildCtx(proj, ps);
            return held;
        }

        // ------------------------------------------------------------------ zones (ProjectGate, once per project per update)

        // SoftZones, OpenLanes, anti-churn, re-close path, WorkZonesReady, StageIndex/Count. proj.Switch / StageCtx are
        // this update's (StageStep ran in Accrue).
        private void StageZones(ProjectRecord proj, DirProjectState ps)
        {
            var s = m_S;
            bool closureOn = RRWDebug.On(DebugLayers.Closure);
            var view = proj.View();
            var stage = PhasePlan.Stage(view, proj.StageCtx);
            proj.StageIndex = stage.Index;
            proj.StageCount = stage.Count;
            var before = proj.OpenLanes;
            bool fresh = proj.MachinesReportFresh(m_Now);
            RoadZones open, soft;
            if (!closureOn)
            {
                open = RoadZones.None;
                soft = RoadZones.None;
                ps.ReClose = RoadZones.None;
            }
            else
            {
                PruneReClose(proj, ps);
                if (proj.Phase == WorksPhase.Complete) ps.ReClose = RoadZones.None;   // the release opens the whole road
                var softWant = (stage.Soft | ps.ReClose) & RoadZones.AllLanes;
                open = PhasePlan.OpenLanes(view, stage, s.StagedOpeningOn, proj.MachineZones, fresh, before, softWant);
                open = AntiChurn(proj, ps, before, open);
                // releasing (completion, D0 call-off): nothing closes any more, the release gate opens the whole road
                if (proj.Releasing) open |= before & RoadZones.AllLanes & ~RoadZones.Parking & ~softWant;
                var closing = before & ~open & RoadZones.AllLanes;
                var re = closing & ~stage.Soft;
                if (re != RoadZones.None && proj.Phase != WorksPhase.Complete)
                {
                    for (int b = 0; b < 16; b++)
                    {
                        var bit = (RoadZones)(1 << b);
                        if ((re & bit) == 0) continue;
                        ps.ReCloseSinceUpdate[b] = m_Now;
                        ps.ReCloseSinceSim[b] = RRWClock.SimFrame;
                    }
                    ps.ReClose |= re;
                    DirectorShared.ReCloses++;
                    RRWLog.Info("director: project #" + proj.Id + " " + proj.Phase + " re-close " + re + " (no longer wanted: carHalf="
                                + proj.CarHalfAllowed + " reason=" + proj.StageBlockReason + " staged=" + s.StagedOpeningOn + "): SOFT until drained");
                }
                soft = (stage.Soft | ps.ReClose) & RoadZones.AllLanes & ~open;
            }
            proj.OpenLanes = open;

            // flip bookkeeping (anti-churn) + soft ages
            for (int b = 0; b < 16; b++)
            {
                var bit = (RoadZones)(1 << b);
                bool wasOpen = (before & bit) != 0, isOpen = (open & bit) != 0;
                if (wasOpen && !isOpen) ps.LastCloseUpdate[b] = math.max(1u, m_Now);
                else if (!wasOpen && isOpen) ps.LastOpenUpdate[b] = math.max(1u, m_Now);
                bool wasSoft = (proj.SoftZones & bit) != 0, isSoft = (soft & bit) != 0;
                if (isSoft && !wasSoft) ps.SoftSinceSim[b] = math.max(1u, RRWClock.SimFrame);
                else if (!isSoft) ps.SoftSinceSim[b] = 0;
            }
            proj.SoftZones = soft;

            // WorkZonesReady: closed groups drained on every edge that has lanes of them; None on any stale report
            RoadZones ready;
            if (!closureOn) ready = RoadZones.AllLanes & ~open;                 // dev: Closure layer off
            else
            {
                ready = RoadZones.AllLanes;
                bool stale = proj.Edges.Count == 0;
                for (int i = 0; i < proj.Edges.Count && !stale; i++)
                {
                    if (!SiteRegistry.TryGetEdge(proj.Edges[i], out var rec)) continue;
                    if (rec.ZonesReportUpdate == 0 || unchecked(m_Now - rec.ZonesReportUpdate) > (uint)RRWConst.kZonesReportStaleUpdates) { stale = true; break; }
                    // A group reported drained only by the timeout (traffic still on it) is not ready for the
                    // machines until Traffic sees it clean (design rule: no traffic among machines). Parked cars: Machines keep out of
                    // EdgeRecord.ZonesParked on their own (lateral preference, no spawn gate)
                    ready &= (rec.ZonesDrained & ~rec.ZonesTimedOut) | (RoadZones.AllLanes & ~rec.ZonesPresent);
                }
                ready = stale ? RoadZones.None : ready & ~open & ~soft & RoadZones.AllLanes;
            }
            proj.WorkZonesReady = ready;
            ps.ReadyComputedUpdate = m_Now;

            if (soft != ps.LoggedSoft)
            {
                RRWLog.Info("director: project #" + proj.Id + " " + proj.Phase + " soft " + ps.LoggedSoft + " -> " + soft + " (switch " + proj.Switch + ")");
                ps.LoggedSoft = soft;
            }
            // Machines' stuck report (Director only logs it; resolution is Machines' job)
            int stuck = fresh ? proj.MachinesStuck : 0;
            if ((stuck > 0) != (ps.LoggedStuck > 0))
            {
                if (stuck > 0) { ps.StuckSinceSim = math.max(1u, RRWClock.SimFrame); RRWLog.Info("director: project #" + proj.Id + " machines report " + stuck + " stuck puppet(s) (switch " + proj.Switch + ", releasing " + proj.Releasing + ")"); }
                else RRWLog.Info("director: project #" + proj.Id + " machines no longer stuck (after " + unchecked(RRWClock.SimFrame - ps.StuckSinceSim) + " sim frames)");
                ps.LoggedStuck = stuck;
            }
            // Machines' crews-with-puppets report (crew LOD); Verbose on change
            if (fresh && proj.CrewsSpawned != ps.CrewsSpawnedLogged)
            {
                RRWLog.Verbose("director: project #" + proj.Id + " machines report " + proj.CrewsSpawned + "/" + proj.Crews + " crews with puppets (" + proj.MachineCount + " puppets)");
                ps.CrewsSpawnedLogged = proj.CrewsSpawned;
            }
            if (ready != ps.LoggedReady)
            {
                RRWLog.Verbose("director: project #" + proj.Id + " work zones ready " + ps.LoggedReady + " -> " + ready);
                ps.LoggedReady = ready;
            }
        }

        // Re-close groups leave SoftZones once every edge reports them drained (report newer than the close) or after
        // kDrainTimeoutFrames (Warn).
        private void PruneReClose(ProjectRecord proj, DirProjectState ps)
        {
            if (ps.ReClose == RoadZones.None) return;
            for (int b = 0; b < 16; b++)
            {
                var bit = (RoadZones)(1 << b);
                if ((ps.ReClose & bit) == 0) continue;
                if (GroupsDrained(proj, bit, ps.ReCloseSinceUpdate[b]))
                {
                    ps.ReClose &= ~bit;
                    RRWLog.Info("director: project #" + proj.Id + " re-closed " + bit + " drained: hard closed");
                }
                else if (unchecked(RRWClock.SimFrame - ps.ReCloseSinceSim[b]) >= (uint)RRWConst.kDrainTimeoutFrames)
                {
                    ps.ReClose &= ~bit;
                    DirectorShared.ReCloseTimeouts++;
                    RRWLog.Warn("director: project #" + proj.Id + " re-closed " + bit + " not reported drained after kDrainTimeoutFrames: hard closed anyway");
                }
            }
        }

        // Anti-churn: a group that would open again less than kStageMinUpdates after it closed is held closed (log once per window).
        // Closings are never held: every close is a stage close (switch) or a safety close (verdict, setting).
        private RoadZones AntiChurn(ProjectRecord proj, DirProjectState ps, RoadZones before, RoadZones open)
        {
            var opening = open & ~before & RoadZones.AllLanes;
            if (opening == RoadZones.None) return open;
            for (int b = 0; b < 16; b++)
            {
                var bit = (RoadZones)(1 << b);
                if ((opening & bit) == 0) continue;
                uint lastClose = ps.LastCloseUpdate[b];
                if (lastClose == 0 || unchecked(m_Now - lastClose) >= (uint)RRWConst.kStageMinUpdates) continue;
                open &= ~bit;
                if ((ps.ChurnLogged & bit) == 0)
                {
                    ps.ChurnLogged |= bit;
                    DirectorShared.ChurnHeld++;
                    RRWLog.Info("director: project #" + proj.Id + " " + bit + " re-opening held (closed " + (m_Now - lastClose) + " updates ago < kStageMinUpdates "
                                + RRWConst.kStageMinUpdates + ")");
                }
            }
            return open;
        }
    }
}
