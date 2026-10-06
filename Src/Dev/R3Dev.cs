#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Game;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;

// Staged-opening dev aids and the experimental switches (RRWGates). Core + dev framework only (Src/Dev is compiled
// into every module build), so nothing here references another module's types; the Director's rrw.stage.step is reached
// through the dev command table by name.
//
//   rrw.gate [name value | defaults]             the RRWGates selectors (single writer)
//   rrw.stage.gates [site]                       switch cockpit: every switch's selector, live readouts and the commands to run
//   rrw.stage.info <site|all>                    Core view of the stage: Stage() vs what the Director published, per-edge drain
//                                                report, classification and the CarHalfBlock verdict (works without the Director's rrw.stage)
//   rrw.stage.force <site> <c4a|c4b|switch|teardown|d0|d0close>   jump p to that point (Director JumpPhase)
//   rrw.stage.force <site> <vacate|swap|drain|ready|skip>         forwards to the Director's rrw.stage.step
//   rrw.stage.watch [on|off|zones|reset]         transition log of every project (switch steps, car-half verdict, stage index,
//                                                optional zone changes) + the ages rrw.check uses
//
// The Director owns `rrw.stage` and `rrw.stage.step`; this family never registers those two names.
namespace RealisticRoadWorks.V3.DevCmds
{
    // ------------------------------------------------------------------ stage watch (sampled every frame)

    internal sealed class StageTrack
    {
        public bool Seen;
        public StageSwitch Switch;
        public uint SwitchSinceUpdate, SwitchSinceSim;
        public RoadZones Soft;
        public uint SoftSinceSim, SoftSinceUpdate;
        public float LastP = -1f;
        public bool Held;
        public uint HeldSinceSim;
        public int Stuck;
        public uint StuckSinceSim;
        public bool CarHalf;
        public StageBlockReason Reason;
        public byte StageIndex, StageCount;
        public RoadZones Open, Ready, Applied;
        // maxima since load / reset (rrw.stage.watch)
        public uint MaxHoldSim, MaxSoftSim, MaxStuckSim, MaxSwitchSim;
        public int Switches, Verdicts;
    }

    internal static class StageWatch
    {
        public static readonly Dictionary<uint, StageTrack> Tracks = new Dictionary<uint, StageTrack>();
        public static bool LogTransitions = true;   // switch steps, car-half verdict, stage index (rare lines)
        public static bool LogZones;                // + OpenLanes / SoftZones / WorkZonesReady / applied-open changes (chatty)
        public static uint LastSampleUpdate;
        private static readonly List<uint> s_Gone = new List<uint>();

        public static uint Age(uint since, uint now) => since == 0 ? 0u : unchecked(now - since);

        public static void Reset() { Tracks.Clear(); LastSampleUpdate = 0; }

        public static void Sample()
        {
            uint upd = RRWClock.UpdateIndex, sim = RRWClock.SimFrame;
            LastSampleUpdate = upd;
            foreach (var p in SiteRegistry.Projects.Values)
            {
                if (!Tracks.TryGetValue(p.Id, out var t)) { t = new StageTrack(); Tracks.Add(p.Id, t); }
                RoadZones applied = AppliedOpen(p);
                if (!t.Seen)
                {
                    t.Seen = true;
                    t.Switch = p.Switch; t.SwitchSinceUpdate = upd; t.SwitchSinceSim = sim;
                    t.Soft = p.SoftZones; t.SoftSinceSim = sim; t.SoftSinceUpdate = upd;
                    t.LastP = p.Progress;
                    t.CarHalf = p.CarHalfAllowed; t.Reason = p.StageBlockReason;
                    t.StageIndex = p.StageIndex; t.StageCount = p.StageCount;
                    t.Open = p.OpenLanes; t.Ready = p.WorkZonesReady; t.Applied = applied;
                    t.Stuck = p.MachinesStuck; t.StuckSinceSim = sim;
                    continue;
                }

                // switch step
                if (p.Switch != t.Switch)
                {
                    uint du = Age(t.SwitchSinceUpdate, upd), ds = Age(t.SwitchSinceSim, sim);
                    if (t.Switch != StageSwitch.None) t.MaxSwitchSim = math.max(t.MaxSwitchSim, ds);
                    if (LogTransitions)
                        RRWLog.Info("dev stage p" + p.Id + " switch " + t.Switch + " -> " + p.Switch + " after " + du + "u/" + ds + " sim"
                                    + " p=" + RRWLog.F(p.Progress) + " switchP=" + RRWLog.F(p.SwitchP) + " soft=" + R2Dev.Zones(p.SoftZones)
                                    + " open=" + R2Dev.Zones(p.OpenLanes) + " ready=" + R2Dev.Zones(p.WorkZonesReady)
                                    + " machineZones=" + R2Dev.Zones(p.MachineZones) + " stuck=" + p.MachinesStuck);
                    t.Switch = p.Switch; t.SwitchSinceUpdate = upd; t.SwitchSinceSim = sim;
                    t.Switches++;
                }

                // soft set (age resets when the set changes)
                if (p.SoftZones != t.Soft)
                {
                    if (t.Soft != RoadZones.None) t.MaxSoftSim = math.max(t.MaxSoftSim, Age(t.SoftSinceSim, sim));
                    t.Soft = p.SoftZones; t.SoftSinceSim = sim; t.SoftSinceUpdate = upd;
                }

                // accrual hold: a switch runs and p did not move
                bool held = p.Switch != StageSwitch.None && t.LastP >= 0f && p.Progress <= t.LastP + 1e-7f;
                if (held && !t.Held) t.HeldSinceSim = sim;
                if (!held && t.Held) t.MaxHoldSim = math.max(t.MaxHoldSim, Age(t.HeldSinceSim, sim));
                t.Held = held;
                t.LastP = p.Progress;

                // stuck machines (Machines report)
                if ((p.MachinesStuck > 0) != (t.Stuck > 0))
                {
                    if (t.Stuck > 0) t.MaxStuckSim = math.max(t.MaxStuckSim, Age(t.StuckSinceSim, sim));
                    else t.StuckSinceSim = sim;
                    if (LogTransitions) RRWLog.Info("dev stage p" + p.Id + " machines stuck " + t.Stuck + " -> " + p.MachinesStuck);
                }
                t.Stuck = p.MachinesStuck;

                // car-half verdict and stage index
                if (p.CarHalfAllowed != t.CarHalf || p.StageBlockReason != t.Reason)
                {
                    if (LogTransitions)
                        RRWLog.Info("dev stage p" + p.Id + " carHalf " + (t.CarHalf ? "yes" : "no(" + t.Reason + ")") + " -> "
                                    + (p.CarHalfAllowed ? "yes" : "no(" + p.StageBlockReason + ")") + " phase=" + p.Phase + " f=" + RRWLog.F(p.PhaseFraction));
                    t.CarHalf = p.CarHalfAllowed; t.Reason = p.StageBlockReason;
                    t.Verdicts++;
                }
                if (p.StageIndex != t.StageIndex || p.StageCount != t.StageCount)
                {
                    if (LogTransitions)
                        RRWLog.Info("dev stage p" + p.Id + " stage " + t.StageIndex + "/" + t.StageCount + " -> " + p.StageIndex + "/" + p.StageCount
                                    + " (" + StageName(p) + ") p=" + RRWLog.F(p.Progress));
                    t.StageIndex = p.StageIndex; t.StageCount = p.StageCount;
                }

                if (LogZones && (p.OpenLanes != t.Open || p.WorkZonesReady != t.Ready || applied != t.Applied))
                    RRWLog.Info("dev stage p" + p.Id + " zones open " + R2Dev.Zones(t.Open) + "->" + R2Dev.Zones(p.OpenLanes)
                                + " ready " + R2Dev.Zones(t.Ready) + "->" + R2Dev.Zones(p.WorkZonesReady)
                                + " appliedOpen " + R2Dev.Zones(t.Applied) + "->" + R2Dev.Zones(applied)
                                + " soft=" + R2Dev.Zones(p.SoftZones) + " switch=" + p.Switch);
                t.Open = p.OpenLanes; t.Ready = p.WorkZonesReady; t.Applied = applied;
            }
            if (Tracks.Count > SiteRegistry.Projects.Count)
            {
                s_Gone.Clear();
                foreach (var id in Tracks.Keys) if (!SiteRegistry.Projects.ContainsKey(id)) s_Gone.Add(id);
                for (int i = 0; i < s_Gone.Count; i++) Tracks.Remove(s_Gone[i]);
            }
        }

        // Union of the lane groups Traffic applied open on the project's Closed edges.
        public static RoadZones AppliedOpen(ProjectRecord p)
        {
            RoadZones z = RoadZones.None;
            for (int i = 0; i < p.Edges.Count; i++)
                if (SiteRegistry.TryGetEdge(p.Edges[i], out var er) && er.ClosureApplied == ClosureLevel.Closed) z |= er.OpenLanesApplied;
            return z;
        }

        public static string StageName(ProjectRecord p)
        {
            if (p.Kind == WorksKind.Construction && p.Mode == VisualMode.FullDig && p.Phase == WorksPhase.Finishing)
                return p.StageCount >= 2 ? (p.StageIndex == 0 ? "C4a" : "C4b") : "C4";
            if (p.Kind == WorksKind.Demolition && p.Phase == WorksPhase.BreakUp) return "D0";
            return DevSites.Code(p.Phase);
        }
    }

    // DEVTOOLS only: samples the registry once per frame (UIUpdate runs while paused too) for the transition log and the
    // time-based rrw.check lines. Read-only on the registry.
    [RegisterSystem(SystemUpdatePhase.UIUpdate, Order = RRWOrder.DevCamera + 1)]
    public partial class StageWatchSystem : GameSystemBase
    {
        private readonly RRWGuard m_Guard = new RRWGuard("dev stage watch");

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            StageWatch.Reset();
            m_Guard.Reset();
        }

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted || !SiteRegistry.Loaded) return;
            try
            {
                RRWClock.Update(World);
                if (StageWatch.LastSampleUpdate == RRWClock.UpdateIndex) return;
                StageWatch.Sample();
                m_Guard.Ok();
            }
            catch (Exception e) { m_Guard.Fail(e); }
        }
    }

    // ------------------------------------------------------------------ dump / check / list lines

    internal static class R3Dev
    {
        // rrw.check slack on the time-based lines (sim frames).
        public const int kSoftSlackFrames = 60;
        public const int kHoldSlackFrames = 600;
        public const float kSimFramesPerSecond = 60f;   // game seconds -> sim frames (kMachineStuckSeconds is in game/machine s)
        public static uint StuckLimitFrames => (uint)(2f * RRWConst.kMachineStuckSeconds * kSimFramesPerSecond);

        public static string CityText() =>
            "city known=" + RRWCity.Known + " lht=" + RRWCity.LeftHandTraffic + " na=" + RRWCity.NaTheme;

        public static string Yn(bool b) => b ? "yes" : "no";

        // Drain report of every edge of the project fresh (Traffic).
        public static bool DrainFresh(ProjectRecord p, uint now, out uint oldest)
        {
            oldest = 0;
            bool fresh = p.Edges.Count > 0;
            for (int i = 0; i < p.Edges.Count; i++)
            {
                if (!SiteRegistry.TryGetEdge(p.Edges[i], out var er)) { fresh = false; continue; }
                uint age = er.ZonesReportUpdate == 0 ? uint.MaxValue : unchecked(now - er.ZonesReportUpdate);
                if (age > oldest) oldest = age;
                if (age > (uint)RRWConst.kZonesReportStaleUpdates) fresh = false;
            }
            return fresh;
        }

        public static string AgeU(uint age) => age == uint.MaxValue ? "never" : age + "u";

        public static string ListFlags(ProjectRecord p)
        {
            var sb = new StringBuilder();
            if (p.StageCount > 0 && (p.Phase == WorksPhase.Finishing || p.Phase == WorksPhase.BreakUp))
                sb.Append(" stage=").Append(StageWatch.StageName(p));
            if (p.Switch != StageSwitch.None) sb.Append(" switch=").Append(p.Switch);
            if (p.SoftZones != RoadZones.None) sb.Append(" soft=").Append(R2Dev.Zones(p.SoftZones));
            if (p.WorkZonesReady != RoadZones.None) sb.Append(" ready=").Append(R2Dev.Zones(p.WorkZonesReady));
            if (p.Kind == WorksKind.Construction && p.Mode == VisualMode.FullDig && p.Phase >= WorksPhase.Paving && p.Phase != WorksPhase.Complete)
                sb.Append(" carHalf=").Append(p.CarHalfAllowed ? "yes" : "no(" + p.StageBlockReason + ")");
            if (p.MachinesStuck > 0) sb.Append(" STUCK=").Append(p.MachinesStuck);
            if (p.Mode == VisualMode.HalfWidth) sb.Append(UwDev.ListFlags(p));
            return sb.ToString();
        }

        // rrw.dump: staged-opening project lines (stage, switch, soft, ready, car half + reason, exits, gates revision, watch ages).
        public static void DumpProject(EntityManager em, ProjectRecord p, StringBuilder sb)
        {
            uint now = RRWClock.UpdateIndex, sim = RRWClock.SimFrame;
            var v = p.View();
            var st = PhasePlan.Stage(v);
            bool drainFresh = DrainFresh(p, now, out uint oldest);
            var c = p.StageCtx;
            sb.Append("  r3 stage=").Append(StageWatch.StageName(p)).Append(' ').Append(p.StageIndex).Append('/').Append(p.StageCount)
              .Append(" switch=").Append(p.Switch).Append(" switchP=").Append(DevSites.F(p.SwitchP))
              .Append(" ctx{staged=").Append(c.Staged ? 1 : 0).Append(" swap=").Append(c.SwapOn ? 1 : 0).Append(" carHalf=").Append(c.CarHalfAllowed ? 1 : 0)
              .Append(" d0walks=").Append(c.D0Sidewalks ? 1 : 0).Append('}')
              .Append(" carHalf=").Append(p.CarHalfAllowed ? "yes" : "no").Append(" reason=").Append(p.StageBlockReason)
              .Append(" (").Append(RRWText.Get(PhasePlan.StageReasonKey(p.StageBlockReason))).Append(")\n");
            sb.Append("  r3 open=").Append(R2Dev.Zones(p.OpenLanes)).Append(" soft=").Append(R2Dev.Zones(p.SoftZones))
              .Append(" ready=").Append(R2Dev.Zones(p.WorkZonesReady)).Append(" appliedOpen=").Append(R2Dev.Zones(StageWatch.AppliedOpen(p)))
              .Append(" buildingSidewalks=").Append(R2Dev.Zones(p.BuildingSidewalks))
              .Append(" exits start=").Append(Yn(p.ExitAtStart)).Append(" end=").Append(Yn(p.ExitAtEnd))
              .Append(" exitU=").Append(DevSites.F1(PhasePlan.ExitU(v)))
              .Append(" stuck=").Append(p.MachinesStuck)
              .Append(" drainReport=").Append(drainFresh ? "fresh" : "STALE").Append("(oldest ").Append(AgeU(oldest)).Append(')')
              .Append(" gatesRev=").Append(RRWGates.Revision).Append('\n');
            sb.Append("  r3 core Stage(): idx=").Append(st.Index).Append('/').Append(st.Count).Append(" works=").Append(R2Dev.Zones(st.Works))
              .Append(" next=").Append(R2Dev.Zones(st.NextWorks)).Append(" open=").Append(R2Dev.Zones(st.Open)).Append(" soft=").Append(R2Dev.Zones(st.Soft))
              .Append(" switchP=").Append(DevSites.F(st.SwitchP)).Append(" holdP=").Append(DevSites.F(st.HoldP))
              .Append(" halves=").Append(v.HalvesActive ? 1 : 0).Append(" swapActive=").Append(v.SwapActive ? 1 : 0);
            if (StageWatch.Tracks.TryGetValue(p.Id, out var t))
                sb.Append(" | watch switchAge=").Append(StageWatch.Age(t.SwitchSinceSim, sim)).Append("sim")
                  .Append(" softAge=").Append(p.SoftZones != RoadZones.None ? StageWatch.Age(t.SoftSinceSim, sim) + "sim" : "-")
                  .Append(" holdAge=").Append(t.Held ? StageWatch.Age(t.HeldSinceSim, sim) + "sim" : "-")
                  .Append(" stuckAge=").Append(t.Stuck > 0 ? StageWatch.Age(t.StuckSinceSim, sim) + "sim" : "-")
                  .Append(" max hold/soft/stuck/switch=").Append(t.MaxHoldSim).Append('/').Append(t.MaxSoftSim).Append('/').Append(t.MaxStuckSim)
                  .Append('/').Append(t.MaxSwitchSim).Append(" switches=").Append(t.Switches);
            sb.Append('\n');
        }

        // rrw.dump: staged-opening edge lines (drain report, classification, geometry split, the CarHalfBlock verdict of this edge).
        public static void DumpEdge(EntityManager em, Entity e, EdgeRecord rec, StringBuilder sb)
        {
            uint now = RRWClock.UpdateIndex;
            bool rev = ChainReversed(em, e);
            uint age = rec.ZonesReportUpdate == 0 ? uint.MaxValue : unchecked(now - rec.ZonesReportUpdate);
            sb.Append("  r3 drain present=").Append(R2Dev.Zones(rec.ZonesPresent)).Append(" drained=").Append(R2Dev.Zones(rec.ZonesDrained))
              .Append(" report=").Append(AgeU(age)).Append(age <= (uint)RRWConst.kZonesReportStaleUpdates ? "" : "(STALE)")
              .Append(" softApplied=").Append(R2Dev.Zones(rec.SoftApplied)).Append(" closedB=").Append(R2Dev.Zones(rec.ClosedBApplied))
              .Append(" carHalves=").Append(R2Dev.Zones(rec.CarHalves)).Append(rec.CarHalvesRevision == rec.GeometryRevision ? "" : "(stale)").Append('\n');
            sb.Append("  r3 classify ").Append(rec.Classified ? "ok" : "PENDING").Append(" cut=").Append(rec.CutEdge ? 1 : 0)
              .Append(" track=").Append(rec.HasTrack ? 1 : 0).Append(" stops=").Append(R2Dev.Zones(rec.StopZones))
              .Append(" buildings=").Append(R2Dev.Zones(rec.BuildingZones)).Append('(').Append(rec.BuildingCount).Append(')')
              .Append(" rev=").Append(rec.ClassifyRevision).Append(rec.ClassifyReversed != rev ? "(chain flipped)" : "")
              .Append(" at=").Append(rec.ClassifyUpdate).Append('\n');
            var sec = rec.Section;
            RoadZoneMath.DriveChain(sec, rev, out float dlo, out float dhi);
            var block = PhasePlan.CarHalfBlock(sec, rev, rec.CarHalves, rec.HasTrack, rec.StopZones, rec.CutEdge, RRWGates.C4Swap, RRWGates.DeadEndRule);
            sb.Append("  r3 section measured=").Append(sec.LanesMeasured ? 1 : 0).Append(" chainRev=").Append(rev ? 1 : 0)
              .Append(" dirSplit edge=").Append(DevSites.F(sec.DirSplit)).Append(" chain=").Append(DevSites.F(RoadZoneMath.DirSplitChain(sec, rev)))
              .Append(" drive=[").Append(DevSites.F(dlo)).Append(',').Append(DevSites.F(dhi)).Append(']')
              .Append(" halfW L=").Append(DevSites.F(RoadZoneMath.HalfWidthChain(sec, rev, RoadZones.LeftHalf)))
              .Append(" R=").Append(DevSites.F(RoadZoneMath.HalfWidthChain(sec, rev, RoadZones.RightHalf)))
              .Append(" (min ").Append(DevSites.F(RRWConst.kMinWorksHalfWidth)).Append(")")
              .Append(" carHalfBlock=").Append(block)
              .Append(" verdict=").Append(sec.Verdict).Append(" through=").Append(sec.ThroughF).Append('>').Append(sec.ThroughB).Append('<')
              .Append(" ignored=").Append(sec.LanesIgnored).Append('\n');
            DumpLanes(em, e, rec, sb);
        }

        // rrw.dump / rrw.stage.info: the edge's car / parking / track lanes measured NOW against the record's arc (LaneSection), next to
        // the stored section: "centre/width kind dir" per lane (> with the edge curve, < against, x across with its |cos|; @pt = a
        // connector at one edge position, 2way = shared two-way lane). A live verdict / split that differs from the stored one means a
        // re-measure is pending (Director lane signature).
        private static readonly List<LaneProbe> s_DumpProbes = new List<LaneProbe>(24);
        public static void DumpLanes(EntityManager em, Entity e, EdgeRecord rec, StringBuilder sb)
        {
            if (rec.Arc == null) { sb.Append("  r3 lanes (no arc yet)\n"); return; }
            EcsUtil.ProbeLanes(em, e, rec.Arc, s_DumpProbes, null);
            var live = new EdgeSection { HalfWidth = rec.Section.HalfWidth };
            LaneSection.Apply(ref live, s_DumpProbes);
            bool same = live.Verdict == rec.Section.Verdict && (float.IsNaN(live.DirSplit) ? float.IsNaN(rec.Section.DirSplit)
                        : !float.IsNaN(rec.Section.DirSplit) && math.abs(live.DirSplit - rec.Section.DirSplit) < 0.05f);
            sb.Append("  r3 lanes live verdict=").Append(live.Verdict).Append(" split=").Append(DevSites.F(live.DirSplit))
              .Append(" carriage=[").Append(DevSites.F(live.CarriageLo)).Append(',').Append(DevSites.F(live.CarriageHi)).Append(']')
              .Append(" drive=[").Append(DevSites.F(live.DriveLoE)).Append(',').Append(DevSites.F(live.DriveHiE)).Append(']')
              .Append(same ? " (= stored)" : " (STORED " + rec.Section.Verdict + " split=" + DevSites.F(rec.Section.DirSplit) + ": re-measure pending)")
              .Append(" n=").Append(s_DumpProbes.Count).Append(':');
            for (int i = 0; i < s_DumpProbes.Count; i++) sb.Append(' ').Append(LaneSection.Describe(s_DumpProbes[i]));
            sb.Append('\n');
        }

        public static bool ChainReversed(EntityManager em, Entity e)
        {
            if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) return false;
            var s = em.GetComponentData<RoadWorksSite>(e);
            return RoadZoneMath.ChainReversed(s.m_ChainU0, s.m_ChainU1);
        }

        // rrw.check staged-opening lines (must all be 0) + cheap contract cross-checks between the writers.
        public static void Invariants(EntityManager em, List<string> problems)
        {
            uint now = RRWClock.UpdateIndex, sim = RRWClock.SimFrame;
            int staleEdges = 0, staleProjects = 0;
            bool watching = SiteRegistry.Projects.Count == 0 || unchecked(now - StageWatch.LastSampleUpdate) <= 2u;
            if (!watching) problems.Add("dev stage watch did not sample this frame (StageWatchSystem not running?): the time-based staged-opening lines below are skipped");

            foreach (var p in SiteRegistry.Projects.Values)
            {
                string n = "p" + p.Id;
                // upgrade works (mode H) keep parking open by design (only the parking their applied window closes is checked) and
                // let machines onto ready drop lanes inside open groups; their own lines are in UwCheck
                bool upgrade = p.Mode == VisualMode.HalfWidth && p.Upgrade != null;
                // a group in both OpenLanes and SoftZones (Director decision)
                if ((p.OpenLanes & p.SoftZones) != RoadZones.None)
                    problems.Add(n + " group(s) " + R2Dev.Zones(p.OpenLanes & p.SoftZones) + " both open and soft (open∩soft)");
                // WorkZonesReady must never contain an open or soft group
                if ((p.WorkZonesReady & (p.OpenLanes | p.SoftZones)) != RoadZones.None)
                    problems.Add(n + " WorkZonesReady " + R2Dev.Zones(p.WorkZonesReady) + " contains open/soft group(s) " + R2Dev.Zones(p.WorkZonesReady & (p.OpenLanes | p.SoftZones)));
                // design decision: no parking group opens during works (FullDig, Minimal); upgrade works keep parking open by design
                // and only the parking their applied window closes (Half, Carriageway) must stay closed
                bool modeH = p.Mode == VisualMode.HalfWidth;
                var parkingClosed = modeH ? PhasePlan.ParkingKeptClosed(p.View()) : RoadZones.Parking;
                if (p.Phase != WorksPhase.Complete && (p.OpenLanes & parkingClosed) != RoadZones.None)
                    problems.Add(n + " parking group(s) " + R2Dev.Zones(p.OpenLanes & parkingClosed) + " open during works"
                                 + (modeH ? " (the upgrade window closes them)" : ""));
                // trap rule: no car half stays decided open on a one-way road or dead end
                if ((p.OpenLanes & RoadZones.Carriageway) != RoadZones.None && !p.CarHalfAllowed
                    && (p.StageBlockReason == StageBlockReason.OneWay || p.StageBlockReason == StageBlockReason.DeadEnd
                        || p.StageBlockReason == StageBlockReason.LaneLayout))
                    problems.Add(n + " car half " + R2Dev.Zones(p.OpenLanes & RoadZones.Carriageway) + " open although blocked (" + p.StageBlockReason + "): must re-close (trap risk)");

                // puppet boxes only in WorkZonesReady (fresh machine report and fresh drain view)
                bool drainFresh = DrainFresh(p, now, out _);
                if (!drainFresh && p.Phase != WorksPhase.Complete) staleProjects++;
                RoadZones outside = p.MachineZones & RoadZones.AllLanes & ~p.WorkZonesReady;
                if (upgrade) outside &= ~UwCheck.MachineAllowance(p);
                if (p.MachinesReportFresh(now) && drainFresh && outside != RoadZones.None)
                    problems.Add(n + " machines in group(s) " + R2Dev.Zones(outside) + " outside WorkZonesReady " + R2Dev.Zones(p.WorkZonesReady)
                                 + " (switch=" + p.Switch + ", re-run once if a switch step just changed)");

                for (int i = 0; i < p.Edges.Count; i++)
                {
                    Entity e = p.Edges[i];
                    if (!SiteRegistry.TryGetEdge(e, out var er)) continue;
                    string en = "edge " + RRWLog.E(e) + " of " + n;
                    if (er.ZonesReportUpdate == 0 || unchecked(now - er.ZonesReportUpdate) > (uint)RRWConst.kZonesReportStaleUpdates) staleEdges++;
                    // Traffic contract: ZonesDrained never contains an applied-open group
                    if ((er.ZonesDrained & er.OpenLanesApplied) != RoadZones.None)
                        problems.Add(en + " reports drained group(s) that are applied open: " + R2Dev.Zones(er.ZonesDrained & er.OpenLanesApplied));
                    if ((er.SoftApplied & er.OpenLanesApplied) != RoadZones.None)
                        problems.Add(en + " lanes carry soft and open at once: " + R2Dev.Zones(er.SoftApplied & er.OpenLanesApplied));
                    if (!modeH && p.Phase != WorksPhase.Complete && !p.Releasing && (er.OpenLanesApplied & RoadZones.Parking) != RoadZones.None)
                        problems.Add(en + " parking applied open during works: " + R2Dev.Zones(er.OpenLanesApplied & RoadZones.Parking));
                    if (em.Exists(e) && em.HasComponent<RoadWorksRuntime>(e))
                    {
                        var rt = em.GetComponentData<RoadWorksRuntime>(e);
                        if ((rt.m_OpenLanes & rt.m_SoftZones) != RoadZones.None)
                            problems.Add(en + " runtime m_OpenLanes ∩ m_SoftZones = " + R2Dev.Zones(rt.m_OpenLanes & rt.m_SoftZones));
                        if (rt.Hidden && (rt.m_OpenLanes | rt.m_SoftZones) != RoadZones.None)
                            problems.Add(en + " is hidden but its runtime offers open/soft groups " + R2Dev.Zones(rt.m_OpenLanes | rt.m_SoftZones));
                    }
                }

                if (!watching || !StageWatch.Tracks.TryGetValue(p.Id, out var t)) continue;
                // a Soft group older than kDrainTimeoutFrames + 60 (game time)
                if (p.SoftZones != RoadZones.None)
                {
                    uint a = StageWatch.Age(t.SoftSinceSim, sim);
                    if (a > (uint)(RRWConst.kDrainTimeoutFrames + kSoftSlackFrames))
                        problems.Add(n + " soft group(s) " + R2Dev.Zones(p.SoftZones) + " for " + a + " sim frames (> kDrainTimeoutFrames+" + kSoftSlackFrames + "; switch=" + p.Switch + ")");
                }
                // p held longer than kCompletionMachineWaitSimFrames + 600 (game time)
                if (t.Held)
                {
                    uint a = StageWatch.Age(t.HeldSinceSim, sim);
                    if (a > (uint)(RRWConst.kCompletionMachineWaitSimFrames + kHoldSlackFrames))
                        problems.Add(n + " progress held at p=" + RRWLog.F(p.Progress) + " for " + a + " sim frames (switch=" + p.Switch
                                     + ", cap kCompletionMachineWaitSimFrames+" + kHoldSlackFrames + ")");
                }
                // Integration: "a puppet stuck longer than 2 x kMachineStuckSeconds" is checked by the Machines checker per puppet on
                // the machine clock (rrw.timescale / MachineClockScale safe); the project-level age here is shown by rrw.stage.watch only.
            }
            if (staleEdges > 0)
                problems.Add(staleEdges + " works edge(s) without a fresh Traffic drain report (ZonesReportUpdate older than " + RRWConst.kZonesReportStaleUpdates
                             + " updates; " + staleProjects + " active project(s) affected): WorkZonesReady stays None and machines plan nothing new");
        }
    }

    // ------------------------------------------------------------------ rrw.gate

    public sealed class GateCommand : IDevCommand
    {
        public string Name => "rrw.gate";
        public string Help => "rrw.gate [<name> <value> | defaults] - RRWGates experimental switches (no args: print). Names: "
                              + string.Join(" ", RRWGates.Names) + " (rrw.stage.gates: live readouts)";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count == 0) { Print(ctx); return; }
            if (pos[0].Equals("defaults", StringComparison.OrdinalIgnoreCase) || pos[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                Defaults();
                RRWGates.Changed();
                RRWLog.Info("dev gate defaults restored: " + RRWGates.Describe());
                Print(ctx);
                return;
            }
            if (pos.Count < 2) { DevSites.Out(ctx, "gate usage: " + Help); return; }
            if (!Set(pos[0], pos[1], out string err, out string what))
            {
                DevSites.Out(ctx, "gate " + pos[0] + ": " + err);
                return;
            }
            RRWGates.Changed();
            RRWLog.Info("dev gate " + what + " -> " + RRWGates.Describe());
            DevSites.Out(ctx, "gate " + what + " (rev " + RRWGates.Revision + "; modules re-layout within one update)");
            DevSites.Out(ctx, "gate " + RRWGates.Describe());
        }

        private static void Print(DevContext ctx)
        {
            DevSites.Out(ctx, "gate " + RRWGates.Describe());
            DevSites.Out(ctx, "gate " + R3Dev.CityText() + " fencePanels=" + FenceName() + " settings tempMarkings=" + Setting(s => s.TempMarkingsOn)
                              + " approachSignals=" + Setting(s => s.ApproachSignalsOn) + " staged=" + Setting(s => s.StagedOpeningOn));
            DevSites.Out(ctx, "gate names: " + string.Join(" ", RRWGates.Names) + " | rrw.gate defaults");
        }

        private static string Setting(Func<RRWSetting, bool> f)
        {
            var s = RRWSettings.Current;
            return s == null ? "?" : f(s) ? "on" : "off";
        }

        public static string FenceName()
        {
            string full = PrefabNames.KerbFence(RRWGates.KerbFenceVariant, false), sh = PrefabNames.KerbFence(RRWGates.KerbFenceVariant, true);
            return full == null ? "SafetyBarrier(fallback)" : full + "/" + sh;
        }

        private static void Defaults()
        {
            RRWGates.ClosedB = RRWConst.kClosedBOn;
            RRWGates.Soft = RRWConst.kSoftMode;
            RRWGates.C4Swap = RRWConst.kC4SwapOn;
            RRWGates.D0Sidewalks = RRWConst.kD0SidewalksOn;
            RRWGates.DeadEndRule = true;
            RRWGates.KerbFenceVariant = RRWConst.kKerbFenceVariant;
            RRWGates.FenceLateral = RRWConst.kFenceLateral;
            RRWGates.Divider = DividerStyle.Cones;
            RRWGates.Signs = RRWConst.kSignsOn;
            RRWGates.SignYawFlip = RRWConst.kSignYawFlip;
            RRWGates.AmberHead = RRWConst.kAmberHeadOn;
            RRWGates.SignalYawFlip = RRWConst.kSignalYawFlip;
            RRWGates.ConeLamps = false;
            RRWGates.ArrowBoard = ArrowBoardMode.Auto;
            RRWGates.TempLineSource = RRWConst.kTempLineSource;
            RRWGates.TempLineWidth = RRWConst.kTempMarkingWidth;
            RRWGates.TempLineRoundness = RRWConst.kTempMarkingRoundness;
            RRWGates.TempLineQueueRaise = RRWConst.kTempMarkingQueueRaise;
            RRWGates.TempLineLodBias = false;
            RRWGates.HalfCovers = true;
            RRWGates.UpgradeDrop = RRWConst.kUwDropOn;
            RRWGates.UpgradeVisualDrop = RRWConst.kUwVisualDropOn;
            RRWGates.UpgradePreCover = RRWConst.kUwPreCoverOn;
            RRWGates.UpgradeNewParkingOff = RRWConst.kUwNewParkingOffOn;
            RRWGates.UpgradeFootprintHold = RRWConst.kUwFootprintHoldOn;
            RRWGates.UpgradeOldAsphalt = RRWConst.kUwOldAsphaltOn;
        }

        private static bool Bool(string v, out bool b)
        {
            switch (v.ToLowerInvariant())
            {
                case "1": case "on": case "true": case "yes": b = true; return true;
                case "0": case "off": case "false": case "no": b = false; return true;
                default: b = false; return false;
            }
        }

        // Parses and applies one selector. Values are range-checked; nothing changes on an error.
        public static bool Set(string name, string value, out string error, out string what)
        {
            error = null;
            what = null;
            string n = name.ToLowerInvariant(), v = value.ToLowerInvariant();
            bool b;
            float f;
            switch (n)
            {
                case "closedb": if (!Bool(v, out b)) break; RRWGates.ClosedB = b; what = "closedb=" + b + (b ? " (CLOSED-B after drain on building edges)" : " (CLOSED-S sentinel)"); return true;
                case "soft":
                    if (v == "sentinel" || v == "0") RRWGates.Soft = SoftMode.Sentinel;
                    else if (v == "forbidden" || v == "1") RRWGates.Soft = SoftMode.Forbidden;
                    else break;
                    what = "soft=" + RRWGates.Soft; return true;
                case "c4swap": if (!Bool(v, out b)) break; RRWGates.C4Swap = b; what = "c4swap=" + b; return true;
                case "d0sidewalks": if (!Bool(v, out b)) break; RRWGates.D0Sidewalks = b; what = "d0sidewalks=" + b; return true;
                case "deadend": if (!Bool(v, out b)) break; RRWGates.DeadEndRule = b; what = "deadend=" + b + (b ? "" : " (reproduces the old trap: car half may open on a dead end)"); return true;
                case "fence":
                    if (!DevSites.TryInt(v, out int fv) || fv < -1 || fv >= PrefabNames.KerbFenceVariants.Length) { error = "fence -1.." + (PrefabNames.KerbFenceVariants.Length - 1) + " (-1 = barrier fallback)"; return false; }
                    RRWGates.KerbFenceVariant = fv; what = "fence=" + fv + " (" + FenceName() + ")"; return true;
                case "fencelat":
                    if (v == "inset" || v == "0") RRWGates.FenceLateral = FenceLateral.Inset;
                    else if (v == "sidewalk" || v == "1") RRWGates.FenceLateral = FenceLateral.Sidewalk;
                    else break;
                    what = "fencelat=" + RRWGates.FenceLateral; return true;
                case "divider":
                    if (v == "none" || v == "0") RRWGates.Divider = DividerStyle.None;
                    else if (v == "cones" || v == "1") RRWGates.Divider = DividerStyle.Cones;
                    else if (v == "barriers" || v == "2") RRWGates.Divider = DividerStyle.Barriers;
                    else break;
                    what = "divider=" + RRWGates.Divider; return true;
                case "signs": if (!Bool(v, out b)) break; RRWGates.Signs = b; what = "signs=" + b; return true;
                case "signflip": if (!Bool(v, out b)) break; RRWGates.SignYawFlip = b; what = "signflip=" + b; return true;
                case "amber": if (!Bool(v, out b)) break; RRWGates.AmberHead = b; what = "amber=" + b + " (the ApproachSignals setting shows only while on)"; return true;
                case "signalflip": if (!Bool(v, out b)) break; RRWGates.SignalYawFlip = b; what = "signalflip=" + b; return true;
                case "conelamps": if (!Bool(v, out b)) break; RRWGates.ConeLamps = b; what = "conelamps=" + b; return true;
                case "arrow":
                    if (v == "auto" || v == "0") RRWGates.ArrowBoard = ArrowBoardMode.Auto;
                    else if (v == "flip" || v == "1") RRWGates.ArrowBoard = ArrowBoardMode.Flip;
                    else if (v == "off" || v == "2") RRWGates.ArrowBoard = ArrowBoardMode.Off;
                    else if (v == "both" || v == "3") RRWGates.ArrowBoard = ArrowBoardMode.Both;
                    else break;
                    what = "arrow=" + RRWGates.ArrowBoard; return true;
                case "linesrc":
                    if (!DevSites.TryInt(v, out int src) || src < 0 || src > 2) { error = "lineSrc 0 (Y1 Concrete) | 1 (Y2 Sand) | 2 (Y3 Pavement)"; return false; }
                    RRWGates.TempLineSource = src; what = "lineSrc=" + src + " (" + PrefabNames.TempMarkingVariants[src] + ")"; return true;
                case "linew":
                    if (!DevSites.TryFloat(v, out f) || f < 0.05f || f > 0.5f) { error = "lineW 0.05..0.5 m"; return false; }
                    RRWGates.TempLineWidth = f; what = "lineW=" + RRWLog.F(f); return true;
                case "lineround":
                    if (!DevSites.TryFloat(v, out f) || f < 0.01f || f > 0.99f) { error = "lineRound 0.01..0.99"; return false; }
                    RRWGates.TempLineRoundness = f; what = "lineRound=" + RRWLog.F(f); return true;
                case "linequeue":
                    if (!DevSites.TryInt(v, out int q) || q < 0 || q > 500) { error = "lineQueue 0..500 (2000 + raise, capped at 2500)"; return false; }
                    RRWGates.TempLineQueueRaise = q; what = "lineQueue=" + q; return true;
                case "linelod": if (!Bool(v, out b)) break; RRWGates.TempLineLodBias = b; what = "lineLod=" + b; return true;
                case "halfcovers": if (!Bool(v, out b)) break; RRWGates.HalfCovers = b; what = "halfcovers=" + b; return true;
                default:
                    // the upgrade-works switches (uwdrop, uwvdrop, ...) belong to RRWGates itself
                    if (RRWGates.TrySetUpgrade(n, v, out what, out error)) return true;
                    if (error != null) return false;
                    error = "unknown switch (names: " + string.Join(" ", RRWGates.Names) + ")";
                    return false;
            }
            error = "bad value '" + value + "'";
            return false;
        }
    }

    // ------------------------------------------------------------------ rrw.stage.* family

    internal static class StageDev
    {
        // Director's command by name (rrw.stage / rrw.stage.step), or null when that module is not in this build.
        private static readonly Dictionary<string, IDevCommand> s_ByName = new Dictionary<string, IDevCommand>(StringComparer.OrdinalIgnoreCase);
        private static bool s_Scanned;

        public static IDevCommand Find(string name)
        {
            if (!s_Scanned)
            {
                s_Scanned = true;
                try
                {
                    foreach (var t in typeof(SiteRegistry).Assembly.GetTypes())
                    {
                        if (t.IsAbstract || t.IsInterface || !typeof(IDevCommand).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) == null) continue;
                        if (t.Namespace == typeof(StageDev).Namespace) continue;   // never our own commands
                        try
                        {
                            var c = (IDevCommand)Activator.CreateInstance(t);
                            if (c.Name != null && c.Name.StartsWith("rrw.stage", StringComparison.OrdinalIgnoreCase) && !s_ByName.ContainsKey(c.Name)) s_ByName.Add(c.Name, c);
                        }
                        catch { }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }
            return s_ByName.TryGetValue(name, out var cmd) ? cmd : null;
        }

        public static bool IsC4(ProjectRecord p) => p.Kind == WorksKind.Construction && p.Mode == VisualMode.FullDig;
        public static bool IsD0(ProjectRecord p) => p.Kind == WorksKind.Demolition && p.Mode == VisualMode.FullDig;
    }

    public sealed class StageForceCommand : IDevCommand
    {
        public string Name => "rrw.stage.force";
        public string Help => "rrw.stage.force <site> <c4a|c4b|switch|teardown|d0|d0close> [f=<fraction>] - jump p to that stage point (JumpPhase); "
                              + "<vacate|swap|drain|ready|skip|reset> - forward to the Director's rrw.stage.step (site resolved to the project id)";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 2) { DevSites.Out(ctx, "stage.force usage: " + Help); return; }
            string what = pos[1].ToLowerInvariant();
            var list = new List<ProjectRecord>();
            if (!DevSites.Resolve(ctx, pos[0], list, out _, out string err)) { DevSites.Out(ctx, "stage.force: " + err); return; }
            if (what == "vacate" || what == "swap" || what == "drain" || what == "ready" || what == "skip" || what == "reset")
            {
                var step = StageDev.Find("rrw.stage.step");
                if (step == null) { DevSites.Out(ctx, "stage.force: the Director's rrw.stage.step is not in this build (Src/Director)"); return; }
                foreach (var p in list)
                {
                    string id = p.Id.ToString(CultureInfo.InvariantCulture);
                    DevSites.Out(ctx, "stage.force -> rrw.stage.step " + id + " " + what + " (switch now " + p.Switch + ")");
                    RRWLog.Info("dev stage.force p" + p.Id + " step " + what + " (switch " + p.Switch + ", p=" + RRWLog.F(p.Progress) + ")");
                    step.Run(ctx, new[] { id, what });
                }
                return;
            }
            float fo = DevSites.OptF(a, "f", float.NaN);
            foreach (var p in list)
            {
                WorksPhase ph;
                float f;
                string note = "";
                switch (what)
                {
                    case "c4a": ph = WorksPhase.Finishing; f = 0.05f; note = "C4a: left half opens once a fresh report shows it clear"; break;
                    case "switch": ph = WorksPhase.Finishing; f = RRWConst.kC4SwapF; note = "p exactly at the switch point: the Director runs Vacate -> Swap -> Drain -> Ready"; break;
                    case "c4b": ph = WorksPhase.Finishing; f = RRWConst.kC4SwapF + 0.05f; note = "past the switch point: the Director treats it like a load inside C4b (switch enters at Drain: left half SOFT until drained, no vacate, no hold)"; break;
                    case "teardown": ph = WorksPhase.Finishing; f = RRWConst.kC4TeardownF + 0.01f; note = "teardown (pick-up; tape-pulled lines with the swap off)"; break;
                    case "d0": ph = WorksPhase.BreakUp; f = 0.1f; note = "D0: house-side sidewalks open behind the fence"; break;
                    case "d0close": ph = WorksPhase.BreakUp; f = RRWConst.kPedCloseF; note = "D0 at kPedCloseF: house sidewalks go SOFT, p held at kD1 until drained"; break;
                    default: DevSites.Out(ctx, "stage.force usage: " + Help); return;
                }
                if (!float.IsNaN(fo)) f = math.saturate(fo);
                if (PhasePlan.KindOf(ph) != p.Kind) { DevSites.Out(ctx, "stage.force p" + p.Id + " is a " + p.Kind + ": " + what + " does not apply"); continue; }
                if (p.Mode != VisualMode.FullDig) note += " (WARNING: mode " + p.Mode + " has no staged C4 / D0 sidewalks)";
                if (ph == WorksPhase.Finishing && (what == "c4b" || what == "switch") && !RRWGates.C4Swap) note += " (WARNING: rrw.gate c4swap is 0: no switch)";
                if (ph == WorksPhase.Finishing && !p.CarHalfAllowed && p.Phase >= WorksPhase.Paving) note += " (carHalf=no " + p.StageBlockReason + ": sidewalks only)";
                if (ph == WorksPhase.BreakUp && (p.BuildingSidewalks & RoadZones.Sidewalks) == RoadZones.None) note += " (no house-side sidewalks classified)";
                Req.Enqueue(p, WorksRequestType.JumpPhase, f, ph);
                DevSites.Out(ctx, "stage.force p" + p.Id + " " + what + " -> " + DevSites.Code(ph) + " f=" + DevSites.F(f)
                                  + " p=" + DevSites.F(PhasePlan.ProgressAt(ph, f)) + " | " + note);
                RRWLog.Info("dev stage.force p" + p.Id + " " + what + " f=" + RRWLog.F(f));
            }
        }
    }

    public sealed class StageWatchCommand : IDevCommand
    {
        public string Name => "rrw.stage.watch";
        public string Help => "rrw.stage.watch [on|off|zones|nozones|reset] - stage transition log (switch steps, car-half verdict, stage index; zones = also open/soft/ready changes) + ages";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count > 0)
            {
                switch (pos[0].ToLowerInvariant())
                {
                    case "on": case "1": StageWatch.LogTransitions = true; break;
                    case "off": case "0": StageWatch.LogTransitions = false; StageWatch.LogZones = false; break;
                    case "zones": StageWatch.LogTransitions = true; StageWatch.LogZones = true; break;
                    case "nozones": StageWatch.LogZones = false; break;
                    case "reset": StageWatch.Reset(); break;
                    default: DevSites.Out(ctx, "stage.watch usage: " + Help); return;
                }
            }
            uint sim = RRWClock.SimFrame;
            DevSites.Out(ctx, "stage.watch log=" + (StageWatch.LogTransitions ? "on" : "off") + " zones=" + (StageWatch.LogZones ? "on" : "off")
                              + " tracked=" + StageWatch.Tracks.Count + " lastSample=" + StageWatch.LastSampleUpdate + " now=" + RRWClock.UpdateIndex
                              + " limits soft>" + (RRWConst.kDrainTimeoutFrames + R3Dev.kSoftSlackFrames) + " hold>" + (RRWConst.kCompletionMachineWaitSimFrames + R3Dev.kHoldSlackFrames)
                              + " stuck>" + R3Dev.StuckLimitFrames + " sim frames");
            foreach (var p in DevSites.Sorted())
            {
                if (!StageWatch.Tracks.TryGetValue(p.Id, out var t)) continue;
                DevSites.Out(ctx, "stage.watch p" + p.Id + " " + StageWatch.StageName(p) + " switch=" + p.Switch + " age=" + StageWatch.Age(t.SwitchSinceSim, sim)
                                  + " soft=" + R2Dev.Zones(p.SoftZones) + (p.SoftZones != RoadZones.None ? "(" + StageWatch.Age(t.SoftSinceSim, sim) + ")" : "")
                                  + " held=" + (t.Held ? StageWatch.Age(t.HeldSinceSim, sim).ToString(CultureInfo.InvariantCulture) : "-")
                                  + " stuck=" + p.MachinesStuck + (p.MachinesStuck > 0 ? "(" + StageWatch.Age(t.StuckSinceSim, sim) + ")" : "")
                                  + " max hold/soft/stuck/switch=" + t.MaxHoldSim + "/" + t.MaxSoftSim + "/" + t.MaxStuckSim + "/" + t.MaxSwitchSim
                                  + " switches=" + t.Switches + " verdictChanges=" + t.Verdicts);
            }
        }
    }

    public sealed class StageInfoCommand : IDevCommand
    {
        public string Name => "rrw.stage.info";
        public string Help => "rrw.stage.info <site|all> - Core view of the staged traffic state (Stage() vs Director fields, drain report, classification, CarHalfBlock per edge)";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            var list = new List<ProjectRecord>();
            if (!DevSites.Resolve(ctx, pos.Count > 0 ? pos[0] : "all", list, out Entity only, out string err)) { DevSites.Out(ctx, "stage.info: " + err); return; }
            var em = ctx.EntityManager;
            var sb = new StringBuilder();
            foreach (var p in list)
            {
                sb.Clear();
                sb.Append("stage.info p").Append(p.Id).Append(' ').Append(p.Kind).Append(" mode=").Append(p.Mode).Append(" phase=").Append(p.Phase)
                  .Append(" f=").Append(DevSites.F(p.PhaseFraction)).Append(" p=").Append(DevSites.F(p.Progress)).Append(" closure=").Append(p.Closure)
                  .Append(p.Releasing ? " releasing" : "")
                  .Append(" chain=").Append(p.ExitsKnown ? PhasePlan.ChainCarHalfBlock(p.ExitAtStart, p.ExitAtEnd, RRWGates.DeadEndRule).ToString() : "exits-unknown")
                  .Append(" exits=").Append(p.ExitAtStart ? 'S' : '-').Append(p.ExitAtEnd ? 'E' : '-').Append('\n');
                R3Dev.DumpProject(em, p, sb);
                for (int i = 0; i < p.Edges.Count; i++)
                {
                    Entity e = p.Edges[i];
                    if (only != Entity.Null && e != only) continue;
                    if (!SiteRegistry.TryGetEdge(e, out var rec)) { sb.Append(" edge ").Append(RRWLog.E(e)).Append(" no record\n"); continue; }
                    sb.Append(" edge[").Append(i).Append("] ").Append(RRWLog.E(e)).Append(" closureApplied=").Append(rec.ClosureApplied)
                      .Append(" hidden=").Append(rec.HiddenApplied).Append(" openApplied=").Append(R2Dev.Zones(rec.OpenLanesApplied)).Append('\n');
                    R3Dev.DumpEdge(em, e, rec, sb);
                }
                DevSites.Lines(ctx, sb);
            }
            if (StageDev.Find("rrw.stage") == null) DevSites.Out(ctx, "stage.info note: the Director's rrw.stage is not in this build");
        }
    }

    // Switch cockpit: per experimental switch the selector, live readouts from the registry and the commands.
    public sealed class StageGatesCommand : IDevCommand
    {
        public string Name => "rrw.stage.gates";
        public string Help => "rrw.stage.gates [site|all] - experimental switch cockpit: selectors, live readouts, commands to run";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            var list = new List<ProjectRecord>();
            if (pos.Count > 0) { if (!DevSites.Resolve(ctx, pos[0], list, out _, out string err)) { DevSites.Out(ctx, "stage.gates: " + err); return; } }
            else list.AddRange(DevSites.Sorted());
            var s = RRWSettings.Current;

            int buildingEdges = 0, closedBEdges = 0, cutEdges = 0, trackEdges = 0, stopEdges = 0, unclassified = 0, p4 = 0, halfOpenEdges = 0, softEdges = 0;
            var e3 = new StringBuilder();
            var e9 = new StringBuilder();
            var i1 = new StringBuilder();
            foreach (var p in list)
            {
                for (int i = 0; i < p.Edges.Count; i++)
                {
                    if (!SiteRegistry.TryGetEdge(p.Edges[i], out var er)) continue;
                    if (!er.Classified) unclassified++;
                    if (er.BuildingCount > 0) buildingEdges++;
                    if (er.ClosedBApplied != RoadZones.None) closedBEdges++;
                    if (er.CutEdge) cutEdges++;
                    if (er.HasTrack) trackEdges++;
                    if (er.StopZones != RoadZones.None) stopEdges++;
                    if (er.SoftApplied != RoadZones.None) softEdges++;
                    RoadZones openCar = er.OpenLanesApplied & RoadZones.Carriageway;
                    if (er.ClosureApplied == ClosureLevel.Closed && openCar != RoadZones.None)
                    {
                        halfOpenEdges++;
                        RoadZones closedCar = er.CarHalves & RoadZones.Carriageway & ~openCar;
                        if (er.BuildingCount > 0 && closedCar != RoadZones.None && (er.ClosedBApplied & closedCar) != closedCar) p4++;
                    }
                }
                string ph = StageWatch.StageName(p);
                if (StageDev.IsC4(p) && p.Phase >= WorksPhase.Paving && p.Phase != WorksPhase.Complete)
                {
                    e3.Append(" p").Append(p.Id).Append(':').Append(p.CarHalfAllowed ? "carHalf=yes" : "carHalf=no reason=" + p.StageBlockReason);
                    if (p.Phase == WorksPhase.Finishing)
                        i1.Append(" p").Append(p.Id).Append(':').Append(ph).Append(" switch=").Append(p.Switch).Append(" open=").Append(R2Dev.Zones(p.OpenLanes))
                          .Append(" soft=").Append(R2Dev.Zones(p.SoftZones)).Append(" ready=").Append(R2Dev.Zones(p.WorkZonesReady));
                }
                if (StageDev.IsD0(p) && p.Phase == WorksPhase.BreakUp)
                    e9.Append(" p").Append(p.Id).Append(": houses=").Append(R2Dev.Zones(p.BuildingSidewalks)).Append(" open=").Append(R2Dev.Zones(p.OpenLanes))
                      .Append(" soft=").Append(R2Dev.Zones(p.SoftZones)).Append(" f=").Append(DevSites.F(p.PhaseFraction)).Append(" switch=").Append(p.Switch);
            }

            DevSites.Out(ctx, "gates " + RRWGates.Describe());
            DevSites.Out(ctx, "gates " + R3Dev.CityText() + " | settings staged=" + (s != null && s.StagedOpeningOn) + " tempMarkings=" + (s != null && s.TempMarkingsOn)
                              + " approachSignals=" + (s != null && s.ApproachSignalsOn) + " | projects=" + list.Count + " unclassifiedEdges=" + unclassified);
            DevSites.Out(ctx, "gates closedb=" + RRWGates.ClosedB + ": building edges=" + buildingEdges + " CLOSED-B edges=" + closedBEdges
                              + " half-open edges=" + halfOpenEdges + " P4 candidates (building edge, closed car half not CLOSED-B)=" + p4
                              + " | rrw.gate closedb 1; rrw.tr.close <edge> closed dir=<d> lanes=car -> rrw.tr.paths <edge> dir=<d> until moving=0 -> rrw.tr.close <edge> blocked dir=<d> lanes=car; rrw.tr.watch <edge> 600; rrw.tr.open <edge>");
            DevSites.Out(ctx, "gates deadend=" + RRWGates.DeadEndRule + ": cut edges=" + cutEdges + " track=" + trackEdges + " stop=" + stopEdges
                              + " |" + (e3.Length > 0 ? e3.ToString() : " no C3/C4 project") + " | rrw.stage.force <site> c4a; rrw.stage <id> (carHalf=no reason=DeadEnd) / rrw.stage.info <site>; rrw.tr.paths <edges> dir=+/- (stuck=)");
            DevSites.Out(ctx, "gates lineSrc=" + RRWGates.TempLineSource + "(" + PrefabNames.TempMarkingVariants[math.clamp(RRWGates.TempLineSource, 0, 2)] + ") lineW=" + RRWLog.F(RRWGates.TempLineWidth)
                              + " lineRound=" + RRWLog.F(RRWGates.TempLineRoundness) + " lineQueue=" + RRWGates.TempLineQueueRaise + " lineLod=" + RRWGates.TempLineLodBias
                              + " halfcovers=" + RRWGates.HalfCovers + " | rrw.surf.line <edge#> <Y1|Y2|Y3|BLK> lat=<m> w=<m> [dash=3,6] [round=] [queue=]; rrw.surf.list");
            DevSites.Out(ctx, "gates fence=" + RRWGates.KerbFenceVariant + "(" + GateCommand.FenceName() + ") fencelat=" + RRWGates.FenceLateral
                              + " | rrw.props.fx <prefab> <edge#> lat=<m> chord=1; rrw.props.list; save + rrw.save.scan");
            DevSites.Out(ctx, "gates signs=" + RRWGates.Signs + " signflip=" + RRWGates.SignYawFlip + " theme=" + (RRWCity.NaTheme ? "NA" : "EU")
                              + " | rrw.props.fx " + PrefabNames.Oneway(RRWCity.NaTheme) + " <edge#> lat=<m> n=1 yaw=0|180");
            DevSites.Out(ctx, "gates amber=" + RRWGates.AmberHead + " signalflip=" + RRWGates.SignalYawFlip + " setting=" + (s != null && s.ApproachSignals)
                              + " effective=" + (s != null && s.ApproachSignalsOn) + " | rrw.gate amber 1; rrw.props.sig <prop|tag> 10 / watch");
            DevSites.Out(ctx, "gates arrow=" + RRWGates.ArrowBoard + " | rrw.mx.sig <puppet> 0|1|2|3");
            DevSites.Out(ctx, "gates d0sidewalks=" + RRWGates.D0Sidewalks + " soft edges=" + softEdges + " |" + (e9.Length > 0 ? e9.ToString() : " no D0 project")
                              + " | rrw.stage.force <site> d0 / d0close; rrw.tr.close <edge> closed lanes=ped side=L|R; rrw.tr.inval <edges> humans; rrw.tr.paths <edges> humans");
            DevSites.Out(ctx, "gates save: rrw.tr.probe <edges>; rrw.save.scan (load snapshot: Forbidden / blockage / RRW restrictions right after load, before Traffic re-applies) + save audit lines in the log");
            DevSites.Out(ctx, "gates c4swap=" + RRWGates.C4Swap + " |" + (i1.Length > 0 ? i1.ToString() : " no C4 project")
                              + " | rrw.stage.force <site> switch; rrw.stage.watch zones; rrw.stage <id>; rrw.stage.force <site> vacate|swap|drain|ready|skip|reset; rrw.mx.check; rrw.check");
        }
    }
}
#endif
