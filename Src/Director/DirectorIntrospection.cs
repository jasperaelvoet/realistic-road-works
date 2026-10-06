using System.Collections.Generic;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Entities;

namespace RealisticRoadWorks.V3.Director
{
    // rrw.dump / rrw.check hooks. Compiled in every build (Core extension point); cheap, only run on demand.
    internal static class DirectorIntrospection
    {
        public static void Register()
        {
            RRWIntrospection.RegisterDumper("Director", Dump);
            RRWIntrospection.RegisterChecker("Director", Check);
        }

        private static string Dump(EntityManager em, Entity edge)
        {
            var sb = new StringBuilder(256);
            if (!SiteRegistry.TryGetEdge(edge, out var rec)) return "director: no record";
            var st = rec.Get<DirEdgeState>(ModuleSlot.Director);
            sb.Append("director edge: hiddenApplied=").Append(rec.HiddenApplied)
              .Append(" chainIndex=").Append(rec.ChainIndex)
              .Append(" nodes=").Append(RRWLog.E(rec.StartNode)).Append('/').Append(RRWLog.E(rec.EndNode))
              .Append(" geomRev=").Append(rec.GeometryRevision).Append(" geomChanged@").Append(rec.GeometryChangedUpdate)
              .Append(" dSub=").Append(RRWLog.F(rec.SubgradeDepth))
              .Append(" grade=").Append(RRWLog.F(rec.Section.GradeOffset))
              .Append(" trims=").Append(RRWLog.F(rec.Section.TrimStart)).Append('/').Append(RRWLog.F(rec.Section.TrimEnd))
              .Append(" carriage=[").Append(RRWLog.F(rec.Section.CarriageLo)).Append(',').Append(RRWLog.F(rec.Section.CarriageHi)).Append("]x").Append(rec.Section.IntervalCount);
            if (st != null)
            {
                sb.Append(" added@").Append(st.AddedUpdate)
                  .Append(" preRollUntil=").Append(st.PreRollUntil)
                  .Append(" lastPhase=").Append(st.LastPhase)
                  .Append(" icon=").Append(st.IconPrefab != Entity.Null)
                  .Append(" sectionFromLanes=").Append(st.SectionFromLanes)
                  .Append(" len=").Append(RRWLog.F(st.CurveLength));
            }
            AppendNode(sb, em, " startNodeHidden=", rec.StartNode);
            AppendNode(sb, em, " endNodeHidden=", rec.EndNode);
            if (SiteRegistry.TryGetProject(rec.ProjectId, out var proj))
            {
                var ps = proj.Get<DirProjectState>(ModuleSlot.Director);
                sb.Append("\ndirector project #").Append(proj.Id)
                  .Append(" phase=").Append(proj.Phase).Append(" f=").Append(RRWLog.F(proj.PhaseFraction))
                  .Append(" p=").Append(RRWLog.F(proj.Progress))
                  .Append(" F=").Append(RRWLog.F(proj.FrontU))
                  .Append(" trim=[").Append(RRWLog.F(proj.TrimU0)).Append(',').Append(RRWLog.F(proj.TrimU1)).Append(']')
                  .Append(" junction=").Append(proj.StartIsJunction ? 'S' : '-').Append(proj.EndIsJunction ? 'E' : '-')
                  .Append(" shared=").Append(proj.StartShared ? 'S' : '-').Append(proj.EndShared ? 'E' : '-')
                  .Append(" closure=").Append(proj.Closure)
                  .Append(" working=").Append(proj.Working)
                  .Append(" machines=").Append(proj.AllowMachines).Append(" cam=").Append(RRWLog.F(proj.CameraDistance)).Append(" camChain=").Append(RRWLog.F(proj.CameraChainDistance))
                  .Append(" model=(").Append(RRWLog.F(proj.Model.P0)).Append(" @").Append(proj.Model.Frame0).Append(" +").Append(proj.Model.PPerFrame.ToString("E3", System.Globalization.CultureInfo.InvariantCulture)).Append(')');
                if (ps != null)
                {
                    sb.Append(" carry=").Append(RRWLog.F((float)ps.Carry))
                      .Append(" gate=").Append(ps.GateActive).Append('/').Append(ps.GateSimFrames)
                      .Append(" clearedU=").Append(RRWLog.F(ps.ClearedU)).Append(" trees=").Append(ps.TreesDeleted)
                      .Append(" completing=").Append(ps.CompletingSeen ? "since " + ps.CompletingSince : "no");
                }
                AppendRound2(sb, proj, ps);
                AppendRound3(sb, proj, ps);
                AppendRound4(sb, proj, ps);
                AppendRound5(sb, proj, ps);
                AppendUpgrade(sb, proj, ps);
            }
            if (rec.Upgrade != null) sb.Append("\ndirector upgrade edge: ").Append(DescribeUpgradeEdge(rec, SiteRegistry.TryGetProject(rec.ProjectId, out var up) ? up.Upgrade : null));
            if (em.Exists(edge) && em.HasComponent<RoadWorksRuntime>(edge))
            {
                var rt = em.GetComponentData<RoadWorksRuntime>(edge);
                sb.Append("\ndirector runtime: target=").Append(rt.m_ClosureTarget).Append(" openLanes=").Append(rt.m_OpenLanes)
                  .Append(" softZones=").Append(rt.m_SoftZones)
                  .Append(" report: present=").Append(rec.ZonesPresent).Append(" drained=").Append(rec.ZonesDrained)
                  .Append(" timedOut=").Append(rec.ZonesTimedOut).Append(" parked=").Append(rec.ZonesParked)
                  .Append(" soft=").Append(rec.SoftApplied).Append(" closedB=").Append(rec.ClosedBApplied)
                  .Append(" age=").Append(rec.ZonesReportUpdate == 0 ? "never" : (RRWClock.UpdateIndex - rec.ZonesReportUpdate).ToString())
                  .Append(" classified=").Append(rec.Classified).Append(" cut=").Append(rec.CutEdge).Append(" track=").Append(rec.HasTrack)
                  .Append(" stops=").Append(rec.StopZones).Append(" houses=").Append(rec.BuildingZones).Append('/').Append(rec.BuildingCount)
                  .Append(" carHalves=").Append(rec.CarHalves).Append(rec.CarHalvesRevision == rec.GeometryRevision ? "" : "(stale)")
                  .Append(" dirSplit=").Append(RRWLog.F(rec.Section.DirSplit))
                  .Append(" applied=").Append(rec.ClosureApplied).Append('/').Append(rec.OpenLanesApplied)
                  .Append(" releasing=").Append((rt.m_Flags & RuntimeFlags.Releasing) != 0)
                  .Append(" crews=").Append(rt.m_Crews)
                  .Append(" edgeDone=").Append(RRWLog.F(PhasePlan.EdgeDoneFraction(rt.m_Phase, rt.m_PhaseFraction, rt.m_ChainLength, rt.m_Crews,
                                                                                   System.Math.Min(rt.m_ChainU0, rt.m_ChainU1), System.Math.Max(rt.m_ChainU0, rt.m_ChainU1))))
                  .Append(" flags=").Append(rt.m_Flags);
            }
            return sb.ToString();
        }

        // Release state of a project (release gate, machine report, staged opening): one line, also used by rrw.director.state.
        internal static void AppendRound2(StringBuilder sb, ProjectRecord proj, DirProjectState ps)
        {
            uint now = RRWClock.UpdateIndex;
            sb.Append("\ndirector r2 #").Append(proj.Id)
              .Append(" releaseSince=").Append(proj.ReleaseSince)
              .Append(proj.Releasing ? " (" + (now - proj.ReleaseSince) + " updates ago)" : "")
              .Append(" hold=").Append(ps != null && ps.Hold)
              .Append(" openedAfterRelease=").Append(ps != null && ps.OpenedAfterRelease)
              .Append(" callOff=").Append(ps != null && ps.CallOff)
              .Append(" openLanes=").Append(proj.OpenLanes)
              .Append(" machineZones=").Append(proj.MachineZones)
              .Append(" onCarriageway=").Append(proj.MachinesOnCarriageway)
              .Append(" report=").Append(proj.MachinesReportUpdate == 0 ? "never" : (now - proj.MachinesReportUpdate) + " updates old")
              .Append(proj.MachinesReportFresh(now) ? " (fresh)" : " (stale)");
        }

        // Staged-traffic state of a project (stage, switch, zones, verdict, exits): one line, also used by rrw.director.state / rrw.stage.
        internal static void AppendRound3(StringBuilder sb, ProjectRecord proj, DirProjectState ps)
        {
            uint sim = RRWClock.SimFrame;
            sb.Append("\ndirector r3 #").Append(proj.Id)
              .Append(" stage=").Append(proj.StageIndex + 1).Append('/').Append(proj.StageCount)
              .Append(" switch=").Append(proj.Switch)
              .Append(proj.Switch != StageSwitch.None && ps != null ? " (step " + unchecked(sim - ps.StepStartSim) + " sim frames)" : "")
              .Append(" switchP=").Append(RRWLog.F(proj.SwitchP))
              .Append(" doneP=").Append(ps != null ? RRWLog.F(ps.SwitchDoneP) : "-")
              .Append(" skipped=").Append(ps != null && ps.SwitchSkipped)
              .Append(" hold=").Append(ps != null && ps.HoldSinceSim != 0 ? unchecked(sim - ps.HoldSinceSim) + " sim frames" : "no")
              .Append(" open=").Append(proj.OpenLanes)
              .Append(" soft=").Append(proj.SoftZones)
              .Append(" ready=").Append(proj.WorkZonesReady)
              .Append(" reClose=").Append(ps != null ? ps.ReClose : RoadZones.None)
              .Append(" carHalf=").Append(proj.CarHalfAllowed ? "yes" : "no")
              .Append(" reason=").Append(proj.StageBlockReason)
              .Append(ps != null && ps.CarHalfWaiting ? " (waiting for classification)" : "")
              .Append(ps != null && ps.CarHalfLatched ? " latched" : "")
              .Append(" ctx=").Append(proj.StageCtx.Staged ? "staged" : "unstaged").Append(proj.StageCtx.SwapOn ? "+swap" : "")
              .Append(proj.StageCtx.CarHalfAllowed ? "+carHalf" : "").Append(proj.StageCtx.D0Sidewalks ? "+d0" : "")
              .Append(" houses=").Append(proj.BuildingSidewalks)
              .Append(" exits=").Append(proj.ExitAtStart ? 'S' : '-').Append(proj.ExitAtEnd ? 'E' : '-')
              .Append(" exitU=").Append(RRWLog.F(PhasePlan.ExitU(proj.View())))
              .Append(" stuck=").Append(proj.MachinesStuck)
              .Append(" city=").Append(RRWCity.Known ? (RRWCity.LeftHandTraffic ? "LHT" : "RHT") + (RRWCity.NaTheme ? "/NA" : "/EU") : "unknown")
              .Append(" gates=").Append(RRWGates.Revision);
        }

        // Crew sections: latch, focus, WorkSeconds, Machines' crews report; one line per crew (section, working
        // range, front, front speed vs the front owner's working pace). Also used by rrw.director.state.
        internal static void AppendRound4(StringBuilder sb, ProjectRecord proj, DirProjectState ps)
        {
            var v = proj.View();
            int n = v.CrewCount;
            float vCrew = PhasePlan.CrewFrontSpeed(v), vWork = MachineLimits.WorkSpeed(proj.Kind, proj.Phase);
            sb.Append("\ndirector r4 #").Append(proj.Id)
              .Append(" crews=").Append(proj.Crews)
              .Append(" latched=").Append(ps != null && ps.CrewsLatched ? ps.CrewsPhase + "@" + ps.CrewsLatchedUpdate + " (" + ps.CrewsWhy + ")" : "never")
              .Append(ps != null && ps.CrewsOverride > 0 ? " override=" + ps.CrewsOverride : "")
              .Append(" ruleNow=").Append(RRWSettings.Current != null ? PhasePlan.CrewCount(v, RRWSettings.Current.CrewsMax).ToString() : "?")
              .Append(" focus=").Append(proj.FocusCrew)
              .Append(" spawned=").Append(proj.CrewsSpawned)
              .Append(" workSeconds=").Append(RRWLog.F(proj.WorkSeconds))
              .Append(" phaseSeconds=").Append(RRWLog.F(v.PhaseSeconds))
              .Append(" vCrew=").Append(RRWLog.F(vCrew)).Append(" vWork=").Append(RRWLog.F(vWork))
              .Append(vWork > 0f && vCrew > vWork * 1.001f ? " OVERWORK" : "")
              .Append(" nearCrews=").Append(ps != null ? ps.NearCrews : 0)
              .Append(ps != null && ps.AllowanceDegraded ? " allowanceDegraded" : "");
            if (v.Cancelled) sb.Append(" cancelCrews=").Append(v.CancelCrewCount).Append(" cancelFront=").Append(RRWLog.F(proj.CancelFront));
            if (n <= 1) return;
            for (int i = 0; i < n; i++)
            {
                float a = PhasePlan.SectionStart(i, n, v.U), b = PhasePlan.SectionStart(i + 1, n, v.U);
                float fu = PhasePlan.CrewFront(v, i);
                sb.Append("\ndirector r4 crew ").Append(i).Append(i == proj.FocusCrew ? "*" : "")
                  .Append(" section=[").Append(RRWLog.F(a)).Append(',').Append(RRWLog.F(b)).Append("] len=").Append(RRWLog.F(b - a))
                  .Append(" front=").Append(RRWLog.F(fu)).Append(" q=").Append(RRWLog.F(PhasePlan.QuantizeSection(fu, a, b)));
            }
        }

        // Roller and chain state of a project: roller latch + Machines' roller report, the chain car-half verdict
        // and the C1 entry tree sweep. One line, also used by rrw.director.state and rrw.stage.
        internal static void AppendRound5(StringBuilder sb, ProjectRecord proj, DirProjectState ps)
        {
            var s = RRWSettings.Current;
            sb.Append("\ndirector r5 #").Append(proj.Id)
              .Append(" rollers=").Append(proj.Rollers).Append(" spawned=").Append(proj.RollersSpawned)
              .Append(" rollersOn=").Append(s != null && s.RollersOn ? "yes" : "no")
              .Append(ps != null && ps.RollersOffPhase != WorksPhase.None ? " (off since " + ps.RollersOffPhase + ")" : "")
              .Append(" chain=").Append(ps == null || !ps.ExitsKnown ? "waiting(exits unknown)" : ChainVerdictText(proj))
              .Append(" exits=").Append(proj.ExitAtStart ? 'S' : '-').Append(proj.ExitAtEnd ? 'E' : '-')
              .Append(" deadEndRule=").Append(RRWGates.DeadEndRule)
              .Append(" chainBlocks=").Append(ps != null ? ps.ChainBlocks : 0)
              .Append(ps != null && ps.ChainBlockSince != 0 ? " blockedFor=" + (RRWClock.UpdateIndex - ps.ChainBlockSince) + " updates" : "")
              .Append(" c1TreeSweep=").Append(ps != null && ps.C1SweepDone ? "done(" + ps.C1SweepDeleted + " removed)" : "no");
        }

        // Upgrade works state of a project (runtime, window, switch bookkeeping): one line, also used by rrw.uw.dir.
        internal static void AppendUpgrade(StringBuilder sb, ProjectRecord proj, DirProjectState ps)
        {
            var rt = proj.Upgrade;
            if (rt == null) return;
            var u = proj.View().Upgrade;
            sb.Append("\ndirector upgrade #").Append(proj.Id)
              .Append(" window=").Append(u.InSetup ? "setup" : u.InTeardown ? "teardown" : (u.Window + 1) + "/" + u.WindowCount)
              .Append(" gw=").Append(RRWLog.F(u.Gw)).Append(" layout=").Append(u.LayoutWindow + 1)
              .Append(" traffic=").Append(u.Traffic).Append(u.Reason != StageBlockReason.None ? "(" + u.Reason + ")" : "")
              .Append(u.Vacating ? " applied=" + (u.AppliedWindow + 1) + ":" + u.AppliedTraffic + " " + RoadZoneMath.Describe(u.AppliedZones) : "")
              .Append(ps != null && ps.UwCarry != RoadZones.None ? " handOver=" + RoadZoneMath.Describe(ps.UwCarry) + " from #" + string.Join(",#", ps.UwCarryFrom) : "")
              .Append(" zones=").Append(RoadZoneMath.Describe(u.Zones)).Append(" next=").Append(RoadZoneMath.Describe(u.NextZones))
              .Append(" preClosed=").Append(RoadZoneMath.Describe(u.PreClosed)).Append(" machineSafe=").Append(RoadZoneMath.Describe(u.MachineSafe))
              .Append(u.AllAtOnce ? " allAtOnce" : "").Append(u.PreCover ? " preCover" : "")
              .Append(" swapOn=").Append(ps != null && ps.UwSwapOn)
              .Append(" startDone=").Append(ps == null || ps.UwStartDone == int.MinValue ? "-" : (ps.UwStartDone + 1).ToString())
              .Append(" swapDone=").Append(ps == null || ps.UwSwapDone == int.MinValue ? "-" : (ps.UwSwapDone + 1).ToString())
              .Append(" switchFor=").Append(ps != null && proj.Switch != StageSwitch.None ? (ps.UwSwitchIsSwap ? "swap " : "start ") + (ps.UwSwitchWindow + 1) : "-")
              .Append(" | ").Append(rt.Describe());
        }

        // Upgrade state of one edge: tail, chain indices, sub-strips, keep-outs and Traffic's drop / parking report.
        internal static string DescribeUpgradeEdge(EdgeRecord rec, UpgradeRuntime rt)
        {
            var eu = rec.Upgrade;
            if (eu == null) return "none";
            var sb = new StringBuilder(160);
            sb.Append(eu.Plan).Append(' ').Append(eu.Class).Append(eu.ChainReversed ? " reversed" : "").Append(" bands=").Append(eu.BandCount)
              .Append(" tail=").Append(eu.TailRevision).Append(" chainIndex=[");
            for (int i = 0; i < eu.BandCount; i++) sb.Append(i > 0 ? "," : "").Append(eu.ChainIndex[i]);
            sb.Append(']').Append(rt != null && eu.ChainIndexCurrent(rt) ? "" : "(stale)")
              .Append(" subStrips=[");
            for (int i = 0; i < eu.BandCount; i++) sb.Append(i > 0 ? "," : "").Append(eu.SubStrips[i].Count);
            sb.Append("] from=").Append(eu.SubStripsFromLayout ? "layout" : "section").Append(eu.SubStripsRevision == rec.GeometryRevision ? "" : "(stale)")
              .Append(" lanes=").Append(eu.CrossSection.Count)
              .Append(" keepOuts=").Append(eu.DrivewayKeepOut.Count)
              .Append(" drop=").Append(eu.DropWindow == -2 ? "none" : "w" + (eu.DropWindow + 1) + " lanes=" + eu.DropLanes.Count + (eu.DropApplied ? " applied" : "")
                                       + (eu.BlockersRegistered ? " registered" : "") + (eu.DropIntrusion ? " INTRUSION" : "") + " clean=" + eu.DropCleanChecks
                                       + (eu.DropCentresWritten ? "" : " noCentres"))
              .Append(" parkingOff=").Append(eu.ParkingOffLanes.Count);
            for (int i = 0; i < eu.BandCount; i++) sb.Append(" | ").Append(eu.Bands[i].ToString());
            return sb.ToString();
        }

        internal static string ChainVerdictText(ProjectRecord proj)
        {
            var r = PhasePlan.ChainCarHalfBlock(proj.ExitAtStart, proj.ExitAtEnd, RRWGates.DeadEndRule);
            if (r == StageBlockReason.None) return "through(None)";
            return r + (proj.ExitAtStart || proj.ExitAtEnd ? "(one connected end)" : "(isolated)");
        }

        private static void AppendNode(StringBuilder sb, EntityManager em, string label, Entity node)
        {
            sb.Append(label);
            if (node == Entity.Null || !em.Exists(node)) { sb.Append("gone"); return; }
            sb.Append(em.HasComponent<Hidden>(node)).Append(DirectorShared.HiddenNodes.Contains(node) ? "(ours)" : "");
        }

        private static readonly List<Entity> s_Tmp = new List<Entity>(16);

        private static void Check(EntityManager em, List<string> problems)
        {
            // nodes we hid must still be hidden, and must still qualify
            foreach (var node in DirectorShared.HiddenNodes)
            {
                if (!em.Exists(node) || em.HasComponent<Deleted>(node)) continue;
                if (!em.HasComponent<Hidden>(node)) problems.Add("director: node " + RRWLog.E(node) + " is in the hidden-node set but not Hidden");
                if (em.HasBuffer<ConnectedEdge>(node))
                {
                    var buf = em.GetBuffer<ConnectedEdge>(node, true);
                    for (int i = 0; i < buf.Length; i++)
                    {
                        Entity ce = buf[i].m_Edge;
                        if (!em.Exists(ce) || em.HasComponent<Deleted>(ce)) continue;
                        if (!SiteRegistry.TryGetEdge(ce, out var r) || !r.HiddenApplied)
                        { problems.Add("director: hidden node " + RRWLog.E(node) + " touches visible edge " + RRWLog.E(ce)); break; }
                    }
                }
            }
            foreach (var rec in SiteRegistry.Edges.Values)
            {
                Entity e = rec.Edge;
                if (!em.Exists(e) || !em.HasComponent<RoadWorksRuntime>(e)) continue;
                var rt = em.GetComponentData<RoadWorksRuntime>(e);
                if (rt.HideWanted && !rt.Hidden && !RRWDebug.HideAfterGround)
                    problems.Add("director: edge " + RRWLog.E(e) + " wants hidden but Hidden was not applied");
                if (rt.Hidden != rec.HiddenApplied)
                    problems.Add("director: edge " + RRWLog.E(e) + " runtime Hidden " + rt.Hidden + " != record " + rec.HiddenApplied);
                if (SiteRegistry.TryGetProject(rec.ProjectId, out var proj))
                {
                    var ps = proj.Get<DirProjectState>(ModuleSlot.Director);
                    uint now = RRWClock.UpdateIndex;
                    if (ps != null && ps.CompletingSeen && now - ps.CompletingSince > DirConst.kCompletionTimeoutUpdates + RRWConst.kCompletionMachineWaitUpdatesHard + 60)
                        problems.Add("director: project #" + proj.Id + " stuck in completion for " + (now - ps.CompletingSince) + " updates");
                    // A mode-A project is Closed for the whole works and until its machines have left
                    if (RRWDebug.On(DebugLayers.Closure) && proj.Mode == VisualMode.FullDig && rt.m_Phase != WorksPhase.Complete && rt.m_ClosureTarget != ClosureLevel.Closed)
                        problems.Add("director: U3 mode-A project #" + proj.Id + " edge " + RRWLog.E(e) + " is " + rt.m_ClosureTarget + " in " + rt.m_Phase + " (must stay Closed mid-works)");
                    // (upgrade works with every lane group open again have nothing left to hold)
                    bool upgradeAllOpen = WorksDirectorSystem.IsUpgradeProject(proj) && (proj.OpenLanes & RoadZones.AllLanes) == RoadZones.AllLanes;
                    if (ps != null && ps.Hold && !upgradeAllOpen && rt.m_ClosureTarget != ClosureLevel.Closed && RRWDebug.On(DebugLayers.Closure))
                        problems.Add("director: U3 project #" + proj.Id + " edge " + RRWLog.E(e) + " target " + rt.m_ClosureTarget + " while machines hold the road");
                    if (proj.Releasing && ps != null && !ps.OpenedAfterRelease
                        && (proj.ReleaseSimAge > RRWConst.kCompletionMachineWaitSimFrames + 60 || now - proj.ReleaseSince > RRWConst.kCompletionMachineWaitUpdatesHard + 60))
                        problems.Add("director: project #" + proj.Id + " release gate still holding " + proj.ReleaseSimAge + " sim frames after the release began");
                    if (proj.Releasing && (rt.m_Flags & RuntimeFlags.Releasing) == 0)
                        problems.Add("director: project #" + proj.Id + " releases but edge " + RRWLog.E(e) + " lacks RuntimeFlags.Releasing");
                }
                else problems.Add("director: edge " + RRWLog.E(e) + " belongs to missing project #" + rec.ProjectId);
                // Staged opening: open lane groups only on a Closed, visible edge
                if (rt.m_OpenLanes != RoadZones.None && (rt.m_ClosureTarget != ClosureLevel.Closed || rec.HiddenApplied))
                    problems.Add("director: edge " + RRWLog.E(e) + " has open lanes " + rt.m_OpenLanes + " while " + rt.m_ClosureTarget + (rec.HiddenApplied ? " and hidden" : ""));
            }
            foreach (var proj in SiteRegistry.Projects.Values)
            {
                if (proj.Edges.Count == 0) problems.Add("director: project #" + proj.Id + " has no edges");
                for (int i = 0; i < proj.Edges.Count; i++)
                    if (!SiteRegistry.Edges.ContainsKey(proj.Edges[i]))
                        problems.Add("director: project #" + proj.Id + " lists non-registry edge " + RRWLog.E(proj.Edges[i]));
            }
            // permanent entities we hid as previews and nobody adopted
            s_Tmp.Clear();
            foreach (var e in DirectorShared.PreviewHidden) s_Tmp.Add(e);
            for (int i = 0; i < s_Tmp.Count; i++)
            {
                Entity e = s_Tmp[i];
                if (em.Exists(e) && !em.HasComponent<Deleted>(e) && !em.HasComponent<Temp>(e))
                    problems.Add("director: permanent entity " + RRWLog.E(e) + " still in the preview-hidden set");
            }
            s_Tmp.Clear();
            // A Temp never carries the Hidden COMPONENT (it removes the works road from vanilla's junction
            // topology -> false collision errors); previews of hidden works roads are hidden with TempFlags.Hidden
            int tempHidden = CountTempWithHiddenComponent(em, out Entity firstTempHidden);
            if (tempHidden > 0)
                problems.Add("director: U1 " + tempHidden + " Temp entities carry the Hidden component (first " + RRWLog.E(firstTempHidden) + "; must be 0)");
            // Preview probe (PreviewHideCatchUpSystem, Mod5, while previews exist)
            if (DirectorShared.ProbeUnhiddenWorks > 0)
                problems.Add("director: U1 probe: " + DirectorShared.ProbeUnhiddenWorks + " hidden works edges/nodes were not Hidden at Mod5 while tool previews existed (update "
                             + DirectorShared.ProbeUnhiddenLastUpdate + ")");
            // The removed pause works feature leaves no trace
            foreach (var proj in SiteRegistry.Projects.Values)
            {
                if ((proj.Flags & SiteFlags.LegacyPaused) != 0) problems.Add("director: U2 project #" + proj.Id + " still carries the removed paused flag");
            }
            CheckRound3(em, problems);
            CheckRound4(em, problems);
            CheckRound5(em, problems);
            CheckUpgrade(em, problems);
        }

        // Upgrade works (mode H), Director-owned rules: every line must be 0.
        private static void CheckUpgrade(EntityManager em, List<string> problems)
        {
            uint now = RRWClock.UpdateIndex;
            foreach (var proj in SiteRegistry.Projects.Values)
            {
                bool anyH = false, anyA = false;
                for (int i = 0; i < proj.Edges.Count; i++)
                {
                    Entity e = proj.Edges[i];
                    if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                    var s = em.GetComponentData<RoadWorksSite>(e);
                    anyH |= WorksDirectorSystem.IsUpgradeSite(s);
                    anyA |= s.Mode == VisualMode.FullDig;
                }
                if (anyH && anyA) problems.Add("director: upgrade project #" + proj.Id + " mixes excavation (FullDig) and upgrade sites");
                if (anyH && !anyA && proj.Upgrade == null) problems.Add("director: upgrade project #" + proj.Id + " has upgrade sites but no upgrade runtime");
                var rt = proj.Upgrade;
                if (rt == null) continue;
                var ps = proj.Get<DirProjectState>(ModuleSlot.Director);
                if (rt.Mismatch > 0) problems.Add("director: upgrade project #" + proj.Id + " " + rt.Mismatch + " edge(s) disagree with the first edge's schedule or bands");
                for (int w = 0; w < rt.Schedule.N && w < RRWConst.kUwMaxWindows; w++)
                {
                    if (rt.Saved[w] == BandTraffic.Undecided) continue;
                    if (PhasePlan.ClosureStrength(rt.Primitive[w]) > PhasePlan.ClosureStrength(rt.Saved[w]))
                        problems.Add("director: upgrade project #" + proj.Id + " window " + (w + 1) + " runs " + rt.Primitive[w] + ", stronger than its saved " + rt.Saved[w]);
                }
                var open = proj.OpenLanes & RoadZones.AllLanes;
                if ((rt.MachineSafe & open) != RoadZones.None)
                    problems.Add("director: upgrade project #" + proj.Id + " machine-safe zones " + (rt.MachineSafe & open) + " are open to traffic");
                if (!rt.DerivedFresh && ps != null && now - ps.UwDerivedOk > 1)
                    problems.Add("director: upgrade project #" + proj.Id + " band data (HasCar / safe ranges) stale for " + (now - ps.UwDerivedOk) + " updates");
                if (rt.AllAtOnce && !RRWGates.UpgradeDrop)
                    problems.Add("director: upgrade project #" + proj.Id + " still drops every new lane at once while lane drops are switched off");
            }
            foreach (var rec in SiteRegistry.Edges.Values)
            {
                Entity e = rec.Edge;
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var s = em.GetComponentData<RoadWorksSite>(e);
                if (s.IsUpgrade && (s.Kind != WorksKind.Construction || s.Has(SiteFlags.CancelledBuild)))
                    problems.Add("director: edge " + RRWLog.E(e) + " carries an upgrade plan on a " + s.Kind + (s.Has(SiteFlags.CancelledBuild) ? " (cancelled)" : "")
                                 + " site: an upgrade was cancelled");
                bool h = WorksDirectorSystem.IsUpgradeSite(s);
                if (!h) continue;
                if (rec.HiddenApplied || em.HasComponent<Hidden>(e))
                    problems.Add("director: upgrade edge " + RRWLog.E(e) + " is Hidden (upgrade works are never hidden)");
                if (rec.Upgrade == null) { problems.Add("director: upgrade edge " + RRWLog.E(e) + " has no upgrade edge state"); continue; }
                if (!SiteRegistry.TryGetProject(rec.ProjectId, out var proj) || proj.Upgrade == null) continue;
                var st = rec.Get<DirEdgeState>(ModuleSlot.Director);
                if (!rec.Upgrade.ChainIndexCurrent(proj.Upgrade) && st != null && now - st.UwChainIndexOk > 1)
                    problems.Add("director: upgrade edge " + RRWLog.E(e) + " chain index stale for " + (now - st.UwChainIndexOk) + " updates (project #" + proj.Id + ")");
            }
        }

        // Roller latch and chain car-half checks: every line must be 0.
        private static void CheckRound5(EntityManager em, List<string> problems)
        {
            uint now = RRWClock.UpdateIndex;
            var s = RRWSettings.Current;
            foreach (var proj in SiteRegistry.Projects.Values)
            {
                var ps = proj.Get<DirProjectState>(ModuleSlot.Director);
                if (proj.Rollers < 0 || proj.Rollers > RRWConst.kMaxRollersPerCrew)
                    problems.Add("director: R5 project #" + proj.Id + " Rollers=" + proj.Rollers + " outside [0, " + RRWConst.kMaxRollersPerCrew + "]");
                if (ps == null) continue;
                // the latch follows the setting at the next latch event (at the latest the next phase change)
                if (proj.Rollers > 0 && s != null && !s.RollersOn && ps.RollersOffPhase != WorksPhase.None && ps.RollersOffPhase != proj.Phase)
                    problems.Add("director: R5 project #" + proj.Id + " Rollers=" + proj.Rollers + " in " + proj.Phase + " but RollersOn has been off since " + ps.RollersOffPhase);
                // No car half on a chain without two connected ends (once the exits are known; the re-close runs through SOFT)
                if (!ps.ExitsKnown || proj.Kind != WorksKind.Construction || proj.Mode != VisualMode.FullDig) continue;
                if (proj.Phase == WorksPhase.Complete || proj.Releasing) continue;   // the release opens the whole road
                var chain = PhasePlan.ChainCarHalfBlock(proj.ExitAtStart, proj.ExitAtEnd, RRWGates.DeadEndRule);
                if (chain == StageBlockReason.None) continue;
                if (proj.CarHalfAllowed)
                    problems.Add("director: R5 project #" + proj.Id + " CarHalfAllowed while the chain verdict is " + ChainVerdictText(proj));
                if ((proj.OpenLanes & RoadZones.Carriageway) != RoadZones.None && ps.ChainBlockSince != 0 && now - ps.ChainBlockSince > 2)
                    problems.Add("director: R5 project #" + proj.Id + " car half " + (proj.OpenLanes & RoadZones.Carriageway) + " decided open on a " + ChainVerdictText(proj) + " chain");
                if (ps.ChainBlockSince != 0 && unchecked(RRWClock.SimFrame - ps.ChainBlockSinceSim) > (uint)(RRWConst.kDrainTimeoutFrames + 120))
                {
                    for (int i = 0; i < proj.Edges.Count; i++)
                    {
                        if (!SiteRegistry.TryGetEdge(proj.Edges[i], out var rec)) continue;
                        if ((rec.OpenLanesApplied & RoadZones.Carriageway) == RoadZones.None) continue;
                        problems.Add("director: R5 project #" + proj.Id + " edge " + RRWLog.E(rec.Edge) + " still has car lanes " + (rec.OpenLanesApplied & RoadZones.Carriageway)
                                     + " applied open " + unchecked(RRWClock.SimFrame - ps.ChainBlockSinceSim) + " sim frames after the chain verdict " + ChainVerdictText(proj));
                        break;
                    }
                }
            }
        }

        // Crew latch contract: every line must be 0.
        private static void CheckRound4(EntityManager em, List<string> problems)
        {
            uint now = RRWClock.UpdateIndex;
            foreach (var proj in SiteRegistry.Projects.Values)
            {
                var ps = proj.Get<DirProjectState>(ModuleSlot.Director);
                if (proj.Crews < 1 || proj.Crews > RRWConst.kMaxCrewsPerProject)
                    problems.Add("director: project #" + proj.Id + " Crews=" + proj.Crews + " outside [1, " + RRWConst.kMaxCrewsPerProject + "]");
                if (proj.FocusCrew < 0 || proj.FocusCrew >= System.Math.Max(1, proj.Crews))
                    problems.Add("director: project #" + proj.Id + " FocusCrew=" + proj.FocusCrew + " outside the " + proj.Crews + " crews");
                // upgrade works: one crew per band of the layout window, side by side (no sections)
                bool upgrade = WorksDirectorSystem.IsUpgradeProject(proj);
                if (proj.Crews > 1 && ((proj.Mode != VisualMode.FullDig && !upgrade) || proj.Phase == WorksPhase.Complete))
                    problems.Add("director: project #" + proj.Id + " has " + proj.Crews + " crews in mode " + proj.Mode + " / " + proj.Phase + " (must be 1)");
                if (proj.Crews > 1 && !upgrade)
                {
                    float L = System.Math.Max(0f, proj.TrimU1 - proj.TrimU0);
                    if (L > 0f && L / proj.Crews < RRWConst.kMinSectionLength - 2f * RRWConst.kFrontQuantum)
                        problems.Add("director: project #" + proj.Id + " " + proj.Crews + " crews on " + RRWLog.F(L) + " m: sections shorter than kMinSectionLength");
                }
                if (ps == null) continue;
                if (ps.CrewsLatched && ps.CrewsPhase != proj.Phase && ps.CrewsLatchedUpdate != 0 && now - ps.CrewsLatchedUpdate > 2)
                    problems.Add("director: project #" + proj.Id + " crews latched for " + ps.CrewsPhase + " but the project is in " + proj.Phase);
                if (proj.Working && !(proj.WorkSeconds > 0f))
                    problems.Add("director: project #" + proj.Id + " works but WorkSeconds=" + RRWLog.F(proj.WorkSeconds) + " (return-aware anchors fall back to the single-front model)");
            }
            foreach (var rec in SiteRegistry.Edges.Values)
            {
                Entity e = rec.Edge;
                if (!em.Exists(e) || !em.HasComponent<RoadWorksRuntime>(e) || !SiteRegistry.TryGetProject(rec.ProjectId, out var proj)) continue;
                var rt = em.GetComponentData<RoadWorksRuntime>(e);
                int rc = rt.m_Crews <= 1 ? 1 : rt.m_Crews;
                if (rc != proj.Crews)
                    problems.Add("director: R4 edge " + RRWLog.E(e) + " RoadWorksRuntime.m_Crews=" + rt.m_Crews + " but project #" + proj.Id + " Crews=" + proj.Crews);
            }
        }

        // Staged traffic checks: every line must be 0.
        private static void CheckRound3(EntityManager em, List<string> problems)
        {
            uint sim = RRWClock.SimFrame;
            foreach (var proj in SiteRegistry.Projects.Values)
            {
                var ps = proj.Get<DirProjectState>(ModuleSlot.Director);
                if ((proj.OpenLanes & proj.SoftZones) != RoadZones.None)
                    problems.Add("director: R3 project #" + proj.Id + " group(s) " + (proj.OpenLanes & proj.SoftZones) + " both open and soft");
                if ((proj.WorkZonesReady & (proj.OpenLanes | proj.SoftZones)) != RoadZones.None)
                    problems.Add("director: R3 project #" + proj.Id + " WorkZonesReady " + proj.WorkZonesReady + " contains open/soft group(s) " + (proj.WorkZonesReady & (proj.OpenLanes | proj.SoftZones)));
                // upgrade works keep parking open (empty new parking lanes are switched off per lane by Traffic); only the parking
                // their applied window closes (Half, Carriageway) must stay closed
                bool upgrade = WorksDirectorSystem.IsUpgradeProject(proj);
                var parkingClosed = proj.Mode == VisualMode.HalfWidth ? PhasePlan.ParkingKeptClosed(proj.View()) : RoadZones.Parking;
                if ((proj.OpenLanes & parkingClosed) != RoadZones.None && proj.Phase != WorksPhase.Complete)
                    problems.Add("director: R3 project #" + proj.Id + " parking " + (proj.OpenLanes & parkingClosed) + " open during the works");
                if (proj.Switch != StageSwitch.None && proj.Phase != WorksPhase.Finishing && proj.Phase != WorksPhase.BreakUp && !upgrade)
                    problems.Add("director: R3 project #" + proj.Id + " switch " + proj.Switch + " running in " + proj.Phase);
                if (ps == null) continue;
                for (int b = 0; b < 16; b++)
                {
                    var bit = (RoadZones)(1 << b);
                    if ((proj.SoftZones & bit) == 0 || ps.SoftSinceSim[b] == 0) continue;
                    uint age = unchecked(sim - ps.SoftSinceSim[b]);
                    if (age > (uint)(RRWConst.kDrainTimeoutFrames + 60))
                        problems.Add("director: R3 project #" + proj.Id + " group " + bit + " soft for " + age + " sim frames (> kDrainTimeoutFrames + 60)");
                }
                if (ps.HoldSinceSim != 0)
                {
                    uint held = unchecked(sim - ps.HoldSinceSim);
                    if (held > (uint)(RRWConst.kCompletionMachineWaitSimFrames + 600))
                        problems.Add("director: R3 project #" + proj.Id + " p held at " + RRWLog.F(proj.Progress) + " for " + held + " sim frames (switch " + proj.Switch
                                     + ", > kCompletionMachineWaitSimFrames + 600)");
                }
                if (proj.Switch == StageSwitch.None && ps.HoldSinceSim != 0 && (ps.ReClose & RoadZones.Sidewalks) == RoadZones.None)
                    problems.Add("director: R3 project #" + proj.Id + " accrual hold without a running switch");
            }
            foreach (var rec in SiteRegistry.Edges.Values)
            {
                Entity e = rec.Edge;
                if (!em.Exists(e) || !em.HasComponent<RoadWorksRuntime>(e)) continue;
                var rt = em.GetComponentData<RoadWorksRuntime>(e);
                if ((rt.m_OpenLanes & rt.m_SoftZones) != RoadZones.None)
                    problems.Add("director: R3 edge " + RRWLog.E(e) + " runtime open " + rt.m_OpenLanes + " overlaps soft " + rt.m_SoftZones);
                if (rt.m_SoftZones != RoadZones.None && (rt.m_ClosureTarget != ClosureLevel.Closed || rec.HiddenApplied))
                    problems.Add("director: R3 edge " + RRWLog.E(e) + " has soft zones " + rt.m_SoftZones + " while " + rt.m_ClosureTarget + (rec.HiddenApplied ? " and hidden" : ""));
                if (SiteRegistry.TryGetProject(rec.ProjectId, out var proj))
                {
                    bool flag = (rt.m_Flags & RuntimeFlags.StageSwitch) != 0;
                    if (flag != (proj.Switch != StageSwitch.None))
                        problems.Add("director: R3 edge " + RRWLog.E(e) + " RuntimeFlags.StageSwitch=" + flag + " but project #" + proj.Id + " switch " + proj.Switch);
                }
            }
        }

        internal static int CountTempWithHiddenComponent(EntityManager em, out Entity first)
        {
            first = Entity.Null;
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Hidden>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            var arr = q.ToEntityArray(Unity.Collections.Allocator.Temp);
            int n = arr.Length;
            if (n > 0) first = arr[0];
            arr.Dispose();
            return n;
        }
    }
}
