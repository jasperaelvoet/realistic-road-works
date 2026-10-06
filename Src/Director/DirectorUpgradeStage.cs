using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Director
{
    // Window switches of upgrade works (mode H). Every window start is a stage switch with the protocol of the staged C4 swap
    // (None -> Vacate -> Swap -> Drain -> Ready -> None, accrual held at the window start while it runs); the re-marking window
    // under one-way operation also runs the C4a -> C4b half swap inside the window. PhasePlan.Stage gives the layouts:
    //  * Vacate keeps the previous window's groups closed; it ends once a fresh machine report shows no puppet in the groups the
    //    old window hands back (PhasePlan.MachinesBlockOpening), or at the vacate cap;
    //  * Swap -> Drain: the new window's groups close SOFT; Ready once every edge reports them drained (or the drain timeout);
    //  * Ready -> None: the new groups are in WorkZonesReady (or the ready timeout), accrual resumes.
    // A window whose old and new layouts close nothing starts at once. After a load (or a dev jump) the switch is re-derived from
    // p: exactly at a window start it re-runs from Vacate, past it from Drain (no hold). The switch point of a running switch is
    // fixed at its entry (PhasePlan.Stage already reports the next one once the new layout applies).
    public partial class WorksDirectorSystem
    {
        // Accrue for an upgrade project, after the accrual added work to `done`: the band data and primitives (UpgradeUpdate), the
        // stage context, the switch machine and the accrual hold. Returns true when p is held this update.
        private bool StageStepUpgrade(ProjectRecord proj, DirProjectState ps, WorksKind kind, uint req, float p0, ref uint done)
        {
            // house sidewalks (BuildingSidewalks) and the reset of the C4 car-half verdict; the upgrade verdict is per window
            EvaluateCarHalf(proj, ps, kind, VisualMode.HalfWidth, PhaseAt(proj, kind, p0, out float _));
            ps.CarHalfLatched = false;
            float tolP = SwitchTol(req);

            // ---- dev SetProgress / jump / finish: the switch is re-derived from p (like a load)
            if (ps.ProgressJumped)
            {
                ps.ProgressJumped = false;
                if (proj.Switch != StageSwitch.None) AbortSwitch(proj, ps, "progress set");
                ResetUpgradeSwitch(ps);
            }
            // ---- dev rrw.stage.step skip / reset
            if (ps.ForcePending && (ps.ForceSkip || ps.ForceReset))
            {
                if (ps.ForceReset)
                {
                    AbortSwitch(proj, ps, "rrw.stage.step reset");
                    ResetUpgradeSwitch(ps);
                    RRWLog.Info("director: project #" + proj.Id + " rrw.stage.step reset: the window switch re-runs from p");
                }
                else
                {
                    AbortSwitch(proj, ps, "rrw.stage.step skip");
                    ps.SwitchSkipped = true;
                    DirectorShared.SwitchesSkipped++;
                    RRWLog.Info("director: project #" + proj.Id + " rrw.stage.step skip: the half swap of the re-marking window is skipped");
                }
                ps.ForcePending = ps.ForceSkip = ps.ForceReset = false;
                ps.ForceStep = StageSwitch.None;
            }

            UpgradeUpdate(proj, ps, p0);
            var rt = proj.Upgrade;
            int N = rt.Schedule.N;
            var ctx = BuildCtxUpgrade(proj, ps);

            // ---- pre-clamp: accrual never jumps over the next switch point in one update (time-lapse)
            var v0 = UpgradeStageView(proj, kind, p0, ctx, StageSwitch.None);
            var st0 = PhasePlan.Stage(v0, ctx);
            int k0 = v0.Upgrade.Window;
            float ahead = st0.SwitchP;
            bool active = N > 0 && !ps.CallOff && !proj.Releasing && p0 < 1f;
            if (active && proj.Switch == StageSwitch.None && !float.IsNaN(ahead) && p0 < ahead - tolP)
            {
                uint cap = DoneAtOrAbove(ahead, req);
                if (done > cap) done = cap;
            }
            float p = done / (float)req;

            // ---- a running switch that no longer applies (release, completion)
            if (proj.Switch != StageSwitch.None && (!active || p >= 1f)) AbortSwitch(proj, ps, "the works hand the road back");

            // ---- dev rrw.stage.step <vacate|swap|drain|ready>: runs the switch into the current window
            if (ps.ForcePending)
            {
                var want = ps.ForceStep;
                ps.ForcePending = false;
                ps.ForceStep = StageSwitch.None;
                if (!active || k0 < 0 || k0 >= N) RRWLog.Info("director: project #" + proj.Id + " rrw.stage.step " + want + " ignored: no window switch now (window " + k0 + ")");
                else
                {
                    if (proj.Switch == StageSwitch.None)
                    {
                        DirectorShared.SwitchesStarted++;
                        ps.SwitchStartSim = RRWClock.SimFrame;
                        ps.SwitchDoneP = float.NaN;
                        proj.SwitchP = WindowStartP(rt.Schedule, k0);
                        ps.UwSwitchWindow = k0;
                        ps.UwSwitchIsSwap = false;
                    }
                    ps.HoldAtSwitch = false;
                    StepTo(proj, ps, want, "rrw.stage.step");
                }
            }

            // ---- entry: the start of the current window not switched yet (adoption at a window start, load, dev jump), the
            //      crossing of the next switch point by accrual, or the half swap of the re-marking window entered past it
            if (active && proj.Switch == StageSwitch.None)
            {
                if (k0 >= 0 && k0 < N && ps.UwStartDone < k0)
                {
                    float sp = WindowStartP(rt.Schedule, k0);
                    EnterUpgradeSwitch(proj, ps, kind, p, req, ctx, k0, false, sp, p0 <= sp + tolP);
                }
                else if (!float.IsNaN(ahead) && p0 < ahead - tolP && p >= ahead - tolP)
                {
                    bool swap = st0.Count == 2 && st0.Index == 0;
                    EnterUpgradeSwitch(proj, ps, kind, p, req, ctx, swap ? k0 : k0 + 1, swap, ahead, true);
                }
                else if (k0 >= 0 && k0 < N && st0.Count == 2 && st0.Index == 1 && ps.UwSwapDone != k0)
                {
                    float c4 = rt.Schedule.P0(k0) + RRWConst.kC4SwapF * (rt.Schedule.P1(k0) - rt.Schedule.P0(k0));
                    EnterUpgradeSwitch(proj, ps, kind, p, req, ctx, k0, true, c4, false);
                }
            }

            // ---- transitions (one step per update at most)
            if (proj.Switch != StageSwitch.None)
            {
                uint stepAge = unchecked(RRWClock.SimFrame - ps.StepStartSim);
                uint holdAge = ps.HoldSinceSim != 0 ? unchecked(RRWClock.SimFrame - ps.HoldSinceSim) : 0u;
                bool holdCap = holdAge >= (uint)(RRWConst.kCompletionMachineWaitSimFrames + 300);
                float pv = ps.HoldAtSwitch ? math.max(p, DoneAtOrAbove(proj.SwitchP, req) / (float)req) : p;
                string where = ps.UwSwitchIsSwap ? "half swap of window " + (ps.UwSwitchWindow + 1) : "window " + (ps.UwSwitchWindow + 1) + " start";
                switch (proj.Switch)
                {
                    case StageSwitch.Vacate:
                    {
                        if (ps.StepStartUpdate == m_Now) break;
                        var oldWorks = PhasePlan.Stage(UpgradeStageView(proj, kind, pv, ctx, StageSwitch.Vacate), ctx).Works & RoadZones.AllLanes;
                        var newWorks = PhasePlan.Stage(UpgradeStageView(proj, kind, pv, ctx, StageSwitch.Swap), ctx).Works & RoadZones.AllLanes;
                        var opening = oldWorks & ~newWorks;
                        // nothing to open never waits (MachinesBlockOpening)
                        if (!PhasePlan.MachinesBlockOpening(proj.MachineZones, opening, proj.MachinesReportUpdate, ps.StepStartUpdate, m_Now))
                            StepTo(proj, ps, StageSwitch.Swap, where + (opening == RoadZones.None ? ": nothing opens"
                                                                        : ": fresh machine report, " + opening + " clear of " + proj.MachineCount + " puppets"));
                        else if (stepAge >= (uint)RRWConst.kCompletionMachineWaitSimFrames)
                        {
                            DirectorShared.VacateCaps++;
                            RRWLog.Warn("director: project #" + proj.Id + " machines did not vacate " + opening + " within kCompletionMachineWaitSimFrames ("
                                        + stepAge + " sim frames, report " + (proj.MachinesReportFresh(m_Now) ? "zones " + proj.MachineZones + " stuck " + proj.MachinesStuck : "missing/stale")
                                        + "): " + where + " goes on (machines broke the leave contract)");
                            StepTo(proj, ps, StageSwitch.Swap, "vacate cap");
                        }
                        break;
                    }
                    case StageSwitch.Swap:
                        if (ps.StepStartUpdate != m_Now) StepTo(proj, ps, StageSwitch.Drain, "next update");
                        break;
                    case StageSwitch.Drain:
                    {
                        if (ps.StepStartUpdate == m_Now) break;
                        var soft = PhasePlan.Stage(UpgradeStageView(proj, kind, pv, ctx, StageSwitch.Drain), ctx).Soft;
                        if (GroupsDrained(proj, soft, ps.StepStartUpdate)) StepTo(proj, ps, StageSwitch.Ready, soft + " drained on every edge");
                        else if (stepAge >= (uint)RRWConst.kDrainTimeoutFrames || holdCap)
                        {
                            DirectorShared.DrainTimeouts++;
                            if (stepAge < (uint)RRWConst.kDrainTimeoutFrames) DirectorShared.HoldCaps++;
                            RRWLog.Warn("director: project #" + proj.Id + " " + soft + " not reported drained after " + stepAge + " sim frames"
                                        + (holdCap ? " (hold cap)" : " (kDrainTimeoutFrames)") + ": " + where + " ready anyway");
                            StepTo(proj, ps, StageSwitch.Ready, "drain timeout");
                        }
                        break;
                    }
                    case StageSwitch.Ready:
                    {
                        if (ps.StepStartUpdate == m_Now) break;
                        var works = PhasePlan.Stage(UpgradeStageView(proj, kind, pv, ctx, StageSwitch.Ready), ctx).Works & RoadZones.AllLanes;
                        if (ps.ReadyComputedUpdate != 0 && unchecked((int)(ps.ReadyComputedUpdate - ps.StepStartUpdate)) >= 0
                            && (proj.WorkZonesReady & works) == works)
                        {
                            FinishSwitch(proj, ps, where + ": " + works + " in WorkZonesReady");
                            MarkUpgradeSwitchDone(ps);
                        }
                        else if (stepAge >= (uint)RRWConst.kDrainTimeoutFrames || holdCap)
                        {
                            DirectorShared.ReadyTimeouts++;
                            if (stepAge < (uint)RRWConst.kDrainTimeoutFrames) DirectorShared.HoldCaps++;
                            RRWLog.Warn("director: project #" + proj.Id + " " + works + " not in WorkZonesReady (" + proj.WorkZonesReady + ") after " + stepAge
                                        + " sim frames" + (holdCap ? " (hold cap)" : "") + ": " + where + ", accrual resumes, machines keep waiting for it");
                            FinishSwitch(proj, ps, "ready timeout");
                            MarkUpgradeSwitchDone(ps);
                        }
                        break;
                    }
                }
            }

            // ---- accrual hold: p sits at the switch point while a switch entered there runs
            bool held = false;
            if (proj.Switch != StageSwitch.None && ps.HoldAtSwitch)
            {
                done = DoneAtOrAbove(proj.SwitchP, req);
                held = true;
            }
            if (held) { if (ps.HoldSinceSim == 0) ps.HoldSinceSim = math.max(1u, RRWClock.SimFrame); }
            else ps.HoldSinceSim = 0;

            // ---- re-marking window under one-way operation: the painter front never drops below the highest C4a front; outside
            //      it the half-swap skip and the floor reset
            var vf = UpgradeStageView(proj, kind, done / (float)req, ctx, proj.Switch);
            if (vf.Upgrade.RemarkWindow(vf.Upgrade.LayoutWindow) && !vf.Upgrade.InTeardown)
            {
                if (vf.SwapActive && PhasePlan.Stage(vf, ctx).Index == 0) ps.C4FrontMax = math.max(ps.C4FrontMax, PhasePlan.MainFront(vf));
            }
            else
            {
                ps.C4FrontMax = 0f;
                ps.SwitchSkipped = false;
            }
            proj.StageCtx = BuildCtxUpgrade(proj, ps);
            return held;
        }

        private static void ResetUpgradeSwitch(DirProjectState ps)
        {
            ps.SwitchDoneP = float.NaN;
            ps.SwitchSkipped = false;
            ps.C4FrontMax = 0f;
            ps.UwStartDone = ps.UwSwapDone = int.MinValue;
            ps.UwSwitchWindow = -1;
            ps.UwSwitchIsSwap = false;
        }

        private static void MarkUpgradeSwitchDone(DirProjectState ps)
        {
            if (ps.UwSwitchIsSwap) ps.UwSwapDone = ps.UwSwitchWindow;
            else ps.UwStartDone = math.max(ps.UwStartDone, ps.UwSwitchWindow);
        }

        // p where window k starts (window 0: the end of the setup).
        private static float WindowStartP(in UpgradeSchedule s, int k) => s.P0(k);

        // Starts a window switch at sp (at: entered at the point, accrual held there; else entered past it: drain first, no hold).
        // A switch whose old and new layouts close nothing is done at once.
        private void EnterUpgradeSwitch(ProjectRecord proj, DirProjectState ps, WorksKind kind, float p, uint req, in StageContext ctx, int window, bool swap,
                                        float sp, bool at)
        {
            float pv = at ? math.max(p, DoneAtOrAbove(sp, req) / (float)req) : p;
            ps.UwSwitchWindow = window;
            ps.UwSwitchIsSwap = swap;
            var oldWorks = PhasePlan.Stage(UpgradeStageView(proj, kind, pv, ctx, StageSwitch.Vacate), ctx).Works & RoadZones.AllLanes;
            var newWorks = PhasePlan.Stage(UpgradeStageView(proj, kind, pv, ctx, StageSwitch.Swap), ctx).Works & RoadZones.AllLanes;
            string what = swap ? "half swap of window " + (window + 1) : "window " + (window + 1) + "/" + proj.Upgrade.Schedule.N + " start";
            if (oldWorks == RoadZones.None && newWorks == RoadZones.None)
            {
                MarkUpgradeSwitchDone(ps);
                ps.SwitchDoneP = sp;
                RRWLog.Verbose("director: project #" + proj.Id + " " + what + " at p=" + RRWLog.F(sp) + ": no lane group closes, no switch");
                return;
            }
            proj.SwitchP = sp;
            ps.SwitchStartSim = RRWClock.SimFrame;
            DirectorShared.SwitchesStarted++;
            if (at)
            {
                ps.HoldAtSwitch = true;
                StepTo(proj, ps, StageSwitch.Vacate, what + " (" + oldWorks + " -> " + newWorks + "): machines leave what opens");
            }
            else
            {
                ps.HoldAtSwitch = false;
                StepTo(proj, ps, StageSwitch.Drain, what + " entered past the switch point (load / dev): drain first");
            }
        }

        // The stage context of an upgrade project: the half swap of the re-marking window (UwSwapOn, from UpgradeUpdate).
        private StageContext BuildCtxUpgrade(ProjectRecord proj, DirProjectState ps) => new StageContext
        {
            Staged = m_S.StagedOpeningOn,
            D0Sidewalks = RRWGates.D0Sidewalks,
            FrontFloor = ps.C4FrontMax,
            CarHalfAllowed = proj.CarHalfAllowed,
            SwapOn = ps.UwSwapOn,
        };

        // View of an upgrade project at progress p with a given switch step (this update's p, not the record's).
        private static ProjectView UpgradeStageView(ProjectRecord proj, WorksKind kind, float p, in StageContext ctx, StageSwitch sw)
        {
            var v = proj.View();
            v.Kind = kind;
            v.Mode = VisualMode.HalfWidth;
            v.Phase = PhaseAt(proj, kind, p, out float f);
            v.F = f;
            v.Ctx = ctx;
            v.Switch = sw;
            v.BuildingSidewalks = proj.BuildingSidewalks;
            v.Upgrade = proj.Upgrade.View(p);
            PhasePlan.FillUpgradeApplied(ref v);        // the applied window / traffic / zones of this switch step
            return v;
        }
    }
}
