#if DEVTOOLS
using System.Collections.Generic;
using System.Globalization;
using Game.Common;
using Game.Net;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;

namespace RealisticRoadWorks.V3.Director
{
    // Module dev commands: rrw.director.* . Output lines start with "dev rrw director".

    public sealed class DirectorStateCommand : IDevCommand
    {
        public string Name => "rrw.director.state";
        public string Help => "rrw.director.state - per project: phase, p, working, carry, model, gate, allowance, trims, completion wait";
        public void Run(DevContext ctx, string[] a)
        {
            var list = new List<ProjectRecord>(SiteRegistry.Projects.Values);
            list.Sort((x, y) => x.Id.CompareTo(y.Id));
            ctx.Log("rrw director projects=" + list.Count + " edges=" + SiteRegistry.Edges.Count + " hiddenEdges=" + DirectorShared.HiddenEdgeCount
                    + " hiddenNodes=" + DirectorShared.HiddenNodes.Count + " loaded=" + SiteRegistry.Loaded + " update=" + RRWClock.UpdateIndex
                    + " sim=" + RRWClock.SimFrame + " gcDeleted=" + WorksDirectorSystem.GcDeletedTotal
                    + " treesCleared=" + WorksDirectorSystem.TreesClearedTotal + " completionTimeouts=" + WorksDirectorSystem.CompletionTimeouts
                    + " demoFinalMatches=" + WorksDirectorSystem.DemolitionFinalMatches
                    + " legacyPausedCleared=" + WorksDirectorSystem.LegacyPausedCleared
                    + " releasesOpened=" + DirectorShared.ReleasesOpened + " releasesCapped=" + DirectorShared.ReleasesCapped
                    + " crewLatches=" + DirectorShared.CrewLatches + " restored=" + DirectorShared.CrewLatchesRestored + " crewChanges=" + DirectorShared.CrewChanges + " midPhase=" + DirectorShared.CrewChangesMidPhase
                    + " focusSwitches=" + DirectorShared.FocusSwitches + " allowanceDegraded=" + DirectorShared.AllowanceDegraded
                    + " upgrades=" + DirectorShared.UpgradesAdopted + " upgradeCancelsRefused=" + DirectorShared.UpgradeCancelsRefused);
            foreach (var p in list)
            {
                var ps = p.Get<DirProjectState>(ModuleSlot.Director);
                string s = "rrw director p" + p.Id + " " + p.Kind + " mode=" + p.Mode + " " + p.Phase + " f=" + RRWLog.F(p.PhaseFraction)
                           + " p=" + RRWLog.F(p.Progress) + " F=" + RRWLog.F(p.FrontU) + "/" + RRWLog.F(p.ChainLength)
                           + " trim=[" + RRWLog.F(p.TrimU0) + "," + RRWLog.F(p.TrimU1) + "] working=" + p.Working + " closure=" + p.Closure
                           + " machines=" + p.AllowMachines + " cam=" + RRWLog.F(p.CameraDistance) + "m camChain=" + RRWLog.F(p.CameraChainDistance) + "m"
                           + " model=" + RRWLog.F(p.Model.P0) + "@" + p.Model.Frame0 + "+" + p.Model.PPerFrame.ToString("E3", CultureInfo.InvariantCulture)
                           + " flags=" + p.Flags;
                if (ps != null)
                    s += " carry=" + RRWLog.F((float)ps.Carry) + " gate=" + ps.GateActive + "/" + ps.GateSimFrames + " clearedU=" + RRWLog.F(ps.ClearedU)
                         + " trees=" + ps.TreesDeleted + (ps.ClearSkipped ? "(skipped)" : "") + " overhang=" + ps.TreesOverhang + " ownedKept=" + ps.TreesOwnedKept + " lateKept=" + ps.TreesLateKept + " snapshot=" + (ps.TreeSnapshot != null ? ps.TreeSnapshot.Count.ToString() : "-")
                         + " finalSweep=" + ps.FinalSweepDone
                         + (ps.CompletingSeen ? " completing " + (RRWClock.UpdateIndex - ps.CompletingSince) + " updates" : "");
                var sb = new System.Text.StringBuilder(160);
                DirectorIntrospection.AppendRound2(sb, p, ps);
                DirectorIntrospection.AppendRound3(sb, p, ps);
                DirectorIntrospection.AppendRound4(sb, p, ps);
                DirectorIntrospection.AppendRound5(sb, p, ps);
                s += sb.ToString().Replace("\ndirector r2 #" + p.Id, " |").Replace("\ndirector r3 #" + p.Id, " |").Replace("\ndirector r4 #" + p.Id, " |")
                     .Replace("\ndirector r5 #" + p.Id, " |");
                ctx.Log(s);
            }
        }
    }

    // Stage of every project (or one): stage, switch step + age, open / soft / ready,
    // car half + reason, hold age, exits; a per-edge line with Traffic's drain report and classification.
    public sealed class StageCommand : IDevCommand
    {
        public string Name => "rrw.stage";
        public string Help => "rrw.stage [projectId] - stage, switch step, open/soft/ready, carHalf + reason, p hold age, exits, per-edge drain report";
        public void Run(DevContext ctx, string[] a)
        {
            uint only = 0;
            if (a.Length > 0) uint.TryParse(a[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out only);
            ctx.Log("rrw stage city=" + (RRWCity.Known ? (RRWCity.LeftHandTraffic ? "LHT" : "RHT") + (RRWCity.NaTheme ? "/NA" : "/EU") : "unknown")
                    + " gates: " + RRWGates.Describe()
                    + " | switches started=" + DirectorShared.SwitchesStarted + " done=" + DirectorShared.SwitchesDone + " aborted=" + DirectorShared.SwitchesAborted
                    + " skipped=" + DirectorShared.SwitchesSkipped + " vacateCaps=" + DirectorShared.VacateCaps + " drainTimeouts=" + DirectorShared.DrainTimeouts
                    + " readyTimeouts=" + DirectorShared.ReadyTimeouts + " holdCaps=" + DirectorShared.HoldCaps + " reCloses=" + DirectorShared.ReCloses
                    + " reCloseTimeouts=" + DirectorShared.ReCloseTimeouts + " churnHeld=" + DirectorShared.ChurnHeld);
            var list = new List<ProjectRecord>(SiteRegistry.Projects.Values);
            list.Sort((x, y) => x.Id.CompareTo(y.Id));
            int n = 0;
            uint now = RRWClock.UpdateIndex;
            foreach (var p in list)
            {
                if (only != 0 && p.Id != only) continue;
                n++;
                var ps = p.Get<DirProjectState>(ModuleSlot.Director);
                var st = PhasePlan.Stage(p.View(), p.StageCtx);
                var sb = new System.Text.StringBuilder(256);
                sb.Append("rrw stage p").Append(p.Id).Append(' ').Append(p.Kind).Append(" mode=").Append(p.Mode).Append(' ').Append(p.Phase)
                  .Append(" f=").Append(RRWLog.F(p.PhaseFraction)).Append(" p=").Append(RRWLog.F(p.Progress))
                  .Append(" works=").Append(st.Works).Append(" next=").Append(st.NextWorks).Append(" want=").Append(st.Open)
                  .Append(" stageSoft=").Append(st.Soft).Append(" holdP=").Append(RRWLog.F(st.HoldP))
                  .Append(" halves=").Append(p.View().HalvesActive).Append(" swapActive=").Append(p.View().SwapActive)
                  .Append(" front=").Append(RRWLog.F(p.FrontU))
                  .Append(" machines zones=").Append(p.MachineZones).Append(p.MachinesReportFresh(now) ? "(fresh)" : "(stale)");
                DirectorIntrospection.AppendRound3(sb, p, ps);
                DirectorIntrospection.AppendRound5(sb, p, ps);
                ctx.Log(sb.ToString().Replace("\ndirector r3 #" + p.Id, " |").Replace("\ndirector r5 #" + p.Id, " |"));
                for (int i = 0; i < p.Edges.Count; i++)
                {
                    if (!SiteRegistry.TryGetEdge(p.Edges[i], out var r)) continue;
                    var em = ctx.EntityManager;
                    var rt = em.Exists(r.Edge) && em.HasComponent<RoadWorksRuntime>(r.Edge) ? em.GetComponentData<RoadWorksRuntime>(r.Edge) : default;
                    ctx.Log("rrw stage p" + p.Id + " edge " + RRWLog.E(r.Edge) + " #" + i + " target=" + rt.m_ClosureTarget + " rtOpen=" + rt.m_OpenLanes + " rtSoft=" + rt.m_SoftZones
                            + " applied=" + r.ClosureApplied + "/" + r.OpenLanesApplied + " present=" + r.ZonesPresent + " drained=" + r.ZonesDrained
                            + " softApplied=" + r.SoftApplied + " closedB=" + r.ClosedBApplied
                            + " report=" + (r.ZonesReportUpdate == 0 ? "never" : (now - r.ZonesReportUpdate) + " updates old")
                            + " classified=" + r.Classified + (r.ClassifyRevision >= 0 ? "" : "(never)") + " cut=" + r.CutEdge + " track=" + r.HasTrack + " stops=" + r.StopZones
                            + " houses=" + r.BuildingZones + "/" + r.BuildingCount + " carHalves=" + r.CarHalves + (r.CarHalvesRevision == r.GeometryRevision ? "" : "(stale)")
                            + " dirSplit=" + RRWLog.F(r.Section.DirSplit) + " hidden=" + r.HiddenApplied);
                }
            }
            if (n == 0) ctx.Log("rrw stage: no project" + (only != 0 ? " #" + only : ""));
        }
    }

    // Force a switch step (dev). The Director applies it in its next update (main thread, Mod1).
    public sealed class StageStepCommand : IDevCommand
    {
        public string Name => "rrw.stage.step";
        public string Help => "rrw.stage.step <projectId> <vacate|swap|drain|ready|skip|reset> - force a stage switch step (vacate moves a C4 p to the switch point)";
        public void Run(DevContext ctx, string[] a)
        {
            if (a.Length < 2 || !uint.TryParse(a[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id)) { ctx.Log("rrw stage.step: usage " + Help); return; }
            if (!SiteRegistry.TryGetProject(id, out var p)) { ctx.Log("rrw stage.step: no project #" + id); return; }
            var ps = p.GetOrCreate<DirProjectState>(ModuleSlot.Director);
            string w = a[1].ToLowerInvariant();
            ps.ForceSkip = ps.ForceReset = false;
            ps.ForceStep = StageSwitch.None;
            switch (w)
            {
                case "vacate": ps.ForceStep = StageSwitch.Vacate; break;
                case "swap": ps.ForceStep = StageSwitch.Swap; break;
                case "drain": ps.ForceStep = StageSwitch.Drain; break;
                case "ready": ps.ForceStep = StageSwitch.Ready; break;
                case "skip": ps.ForceSkip = true; break;
                case "reset": ps.ForceReset = true; break;
                default: ctx.Log("rrw stage.step: unknown step '" + a[1] + "' (" + Help + ")"); return;
            }
            ps.ForcePending = true;
            ctx.Log("rrw stage.step p" + id + " " + w + " requested (current switch " + p.Switch + ", p=" + RRWLog.F(p.Progress) + "): applied by the Director's next update");
        }
    }

    // Clearing check: free-standing trees still inside a construction project's clearing footprint. After C1 starts
    // (clearedU = U) "left" must be 0; ahead = trees beyond the clearing front (C0 only). "list" prints the trees.
    // "left" uses the strip / canopy rule (trunk in the footprint, or the rendered canopy reaching more than
    // kClearOverhangTolerance over the strip S = HalfWidth + kTopsoilMargin). In C1 / C2 left must be 0 (FAIL otherwise); in C3+
    // the count is informative (trees the player planted after the start are kept on purpose).
    public sealed class DirectorTreesCommand : IDevCommand
    {
        public string Name => "rrw.director.trees";
        public string Help => "rrw.director.trees [projectId] [list] - free trees left in the clearing footprint or with a canopy over the strip (behind the front / C1-C2: must be 0) + ahead";
        public void Run(DevContext ctx, string[] a)
        {
            uint only = 0;
            bool list = false;
            foreach (var arg in a)
            {
                if (arg == "list") list = true;
                else if (uint.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id)) only = id;
            }
            var search = ctx.System<Game.Objects.SearchSystem>();
            var projects = new List<ProjectRecord>(SiteRegistry.Projects.Values);
            projects.Sort((x, y) => x.Id.CompareTo(y.Id));
            int n = 0;
            foreach (var p in projects)
            {
                if (only != 0 && p.Id != only) continue;
                if (p.Kind != WorksKind.Construction) continue;
                n++;
                var ps = p.Get<DirProjectState>(ModuleSlot.Director);
                float cu = ps != null ? ps.ClearedU : 0f;
                int nc = ps != null ? System.Math.Max(1, ps.ClearCrews) : 1;
                var behind = default(TreeScanStats);
                var ahead = default(TreeScanStats);
                var hits = list ? new List<TreeHit>() : null;
                // Per crew section [B_i, B_i + len_i * clearedU / U] is cleared (one crew: [0, clearedU])
                float U = p.ChainLength, sc = U > 0f ? Unity.Mathematics.math.saturate(cu / U) : 1f;
                for (int c = 0; c < nc; c++)
                {
                    float sa = PhasePlan.SectionStart(c, nc, U), sb2 = PhasePlan.SectionStart(c + 1, nc, U);
                    float mid = nc <= 1 ? cu : sa + (sb2 - sa) * sc;
                    if (mid > sa + 1e-3f) TreeClearing.Scan(ctx.EntityManager, search, p, sa, mid, false, ref behind, hits);
                    if (mid < sb2 - 0.01f) TreeClearing.Scan(ctx.EntityManager, search, p, mid, sb2, false, ref ahead, null);
                }
                ctx.Log("rrw director trees p" + p.Id + " " + p.Phase + " f=" + RRWLog.F(p.PhaseFraction) + " clearedU=" + RRWLog.F(cu) + "/" + RRWLog.F(p.ChainLength)
                        + " sections=" + nc
                        + " footprint=+-" + RRWLog.F(TreeClearing.MaxFootprint(p)) + "m left=" + behind.Found + " (footprint " + behind.Footprint + ", overhang " + behind.Overhang + ")"
                        + " ownedInside=" + behind.OwnedKept + " ahead=" + ahead.Found + " candidates=" + behind.Candidates
                        + " removed=" + (ps != null ? ps.TreesDeleted : 0) + (ps != null && ps.ClearSkipped ? " (clearing skipped: replaced/cancelled/migrated/loaded C3+)" : "")
                        + " strip=+-" + RRWLog.F(TreeClearing.MaxStrip(p)) + "m canopyMax=" + RRWLog.F(DirConst.kClearCanopyMax)
                        + " c1Sweep=" + (ps != null && ps.C1SweepDone ? "done(" + ps.C1SweepDeleted + ")" : "no")
                        + (behind.Found == 0 ? " OK" : p.Phase >= WorksPhase.Paving && p.Phase != WorksPhase.None ? " (C3+: info, player trees kept)" : " FAIL"));
                if (hits != null)
                    foreach (var h in hits)
                        ctx.Log("rrw director tree " + RRWLog.E(h.Tree) + " pos=" + RRWLog.F3(h.Position) + " lateral=" + RRWLog.F(h.Lateral) + " canopy=" + RRWLog.F(h.Canopy)
                                + " canopyReach=" + RRWLog.F(h.Lateral - h.Canopy) + " strip=" + RRWLog.F(h.Strip)
                                + (h.Overhang ? " overhang" : " footprint") + (h.Owned ? " OWNED(kept)" : "") + (h.Overridden ? " overridden" : ""));
            }
            if (n == 0) ctx.Log("rrw director trees: no construction project" + (only != 0 ? " #" + only : ""));
        }
    }

    // Previews of hidden works roads are hidden with TempFlags.Hidden; the Hidden COMPONENT on a Temp must be 0.
    public sealed class DirectorPreviewCommand : IDevCommand
    {
        public string Name => "rrw.director.preview";
        public string Help => "rrw.director.preview [list] - PreviewHide stats: visibleOfHiddenWorks and tempWithHiddenComponent must be 0";
        public void Run(DevContext ctx, string[] a)
        {
            bool list = a.Length > 0 && a[0] == "list";
            var em = ctx.EntityManager;
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Edge>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            var arr = q.ToEntityArray(Allocator.Temp);
            int temps = arr.Length, visibleOfHidden = 0, hidden = 0, withComponent = 0, ours = 0;
            foreach (var e in arr)
            {
                var t = em.GetComponentData<Temp>(e);
                bool h = (t.m_Flags & TempFlags.Hidden) != 0;
                bool comp = em.HasComponent<Hidden>(e);
                bool mine = DirectorShared.PreviewHidden.Contains(e);
                if (h) hidden++;
                if (comp) withComponent++;
                if (mine) ours++;
                bool ofHidden = SiteRegistry.TryGetEdge(t.m_Original, out var r) && r.HiddenApplied;
                if (!h && (t.m_Flags & TempFlags.Delete) == 0 && ofHidden) visibleOfHidden++;
                if (list)
                    ctx.Log("rrw director preview temp " + RRWLog.E(e) + " original=" + RRWLog.E(t.m_Original) + " flags=" + t.m_Flags
                            + " hiddenComponent=" + comp + " ours=" + mine + (ofHidden ? " copyOfHiddenWorks" : t.m_Original == Entity.Null ? " new/piece" : ""));
            }
            arr.Dispose();
            int tempWithComponent = DirectorIntrospection.CountTempWithHiddenComponent(em, out Entity _);
            ctx.Log("rrw director preview layer=" + (RRWDebug.On(DebugLayers.PreviewHide) ? "on" : "OFF") + " tempEdges=" + temps
                    + " hidden(TempFlags)=" + hidden + " ours=" + ours + " visibleOfHiddenWorks=" + visibleOfHidden
                    + " edgesWithHiddenComponent=" + withComponent + " tempWithHiddenComponent=" + tempWithComponent
                    + " lastRun edges=" + DirectorShared.PreviewEdgesHidden + " nodes=" + DirectorShared.PreviewNodesHidden
                    + " total=" + DirectorShared.PreviewHideTotal + " tracked=" + DirectorShared.PreviewHidden.Count
                    + " adopted=" + DirectorShared.PreviewAdopted + " released=" + DirectorShared.PreviewReleased
                    + " staleTemps=" + DirectorShared.PreviewReleasedTemps + " componentStripped=" + DirectorShared.PreviewComponentStripped
                    + " catchUp=" + DirectorShared.PreviewCatchUpTotal
                    + " probeUnhidden=" + DirectorShared.ProbeUnhiddenWorks + "/" + DirectorShared.ProbeUnhiddenTotal
                    + ((visibleOfHidden == 0 && tempWithComponent == 0) ? " OK" : " FAIL"));
        }
    }

    // Upgrade works: one line per project (window, primitive, zones, switch bookkeeping, runtime), one per chain band (its view,
    // g, state) and one per edge (tail, chain indices, sub-strips, keep-outs, Traffic's drop report).
    public sealed class UwCommand : IDevCommand
    {
        public string Name => "rrw.uw.dir";
        public string Help => "rrw.uw.dir [projectId] - Director view of upgrade works: windows, primitives (saved ceilings), zones, machine-safe zones, corridor, detour, bands, edges";
        public void Run(DevContext ctx, string[] a)
        {
            uint only = 0;
            if (a.Length > 0) uint.TryParse(a[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out only);
            var list = new List<ProjectRecord>();
            foreach (var p in SiteRegistry.Projects.Values) if (p.Upgrade != null && (only == 0 || p.Id == only)) list.Add(p);
            list.Sort((x, y) => x.Id.CompareTo(y.Id));
            ctx.Log("rrw uw.dir projects=" + list.Count + " adopted=" + DirectorShared.UpgradesAdopted + " rebuilds=" + DirectorShared.UpgradeRebuilds
                    + " stamps=" + DirectorShared.UpgradeStamps + " cancelsRefused=" + DirectorShared.UpgradeCancelsRefused + " ended=" + DirectorShared.UpgradesEnded
                    + " edgesLeft=" + DirectorShared.UpgradeEdgesLeft + " mixes=" + DirectorShared.UpgradeMixes + " gates: " + RRWGates.Describe());
            foreach (var p in list)
            {
                var ps = p.Get<DirProjectState>(ModuleSlot.Director);
                var sb = new System.Text.StringBuilder(256);
                sb.Append("rrw uw.dir p").Append(p.Id).Append(' ').Append(p.Kind).Append(" mode=").Append(p.Mode).Append(" p=").Append(RRWLog.F(p.Progress))
                  .Append(' ').Append(p.Phase).Append(" f=").Append(RRWLog.F(p.PhaseFraction)).Append(" crews=").Append(p.Crews)
                  .Append(" machines=").Append(p.AllowMachines).Append(" closure=").Append(p.Closure).Append(" switch=").Append(p.Switch)
                  .Append(" open=").Append(p.OpenLanes).Append(" soft=").Append(p.SoftZones).Append(" ready=").Append(p.WorkZonesReady);
                DirectorIntrospection.AppendUpgrade(sb, p, ps);
                ctx.Log(sb.ToString().Replace("\ndirector upgrade #" + p.Id, " |"));
                var u = p.View().Upgrade;
                for (int i = 0; i < u.BandCount; i++)
                {
                    int crew = u.CrewOfBand(i);
                    ctx.Log("rrw uw.dir p" + p.Id + " band " + i + " " + u.Band(i).ToString() + " g=" + RRWLog.F(u.BandG(i)) + " state=" + u.StateOf(i)
                            + (crew >= 0 ? " crew " + crew : ""));
                }
                for (int i = 0; i < p.Edges.Count; i++)
                    if (SiteRegistry.TryGetEdge(p.Edges[i], out var r))
                        ctx.Log("rrw uw.dir p" + p.Id + " edge " + RRWLog.E(r.Edge) + " #" + i + " " + DirectorIntrospection.DescribeUpgradeEdge(r, p.Upgrade));
            }
            if (list.Count == 0) ctx.Log("rrw uw.dir: no upgrade project" + (only != 0 ? " #" + only : ""));
        }
    }

    // Jump an upgrade project to a window (dev), like rrw.phase for the other works: SetProgress to the window's start + g of its
    // length. The window switch re-runs from p (at a window start from Vacate, past it from Drain).
    public sealed class UwJumpCommand : IDevCommand
    {
        public string Name => "rrw.uw.jump";
        public string Help => "rrw.uw.jump <projectId> window=<1..N|setup|teardown> [g=0] - jump an upgrade project into a window (g = fraction of it); "
                              + "then rrw.rebuild so the machines respawn at their plan positions";
        public void Run(DevContext ctx, string[] a)
        {
            uint id = 0;
            string win = null;
            float g = 0f;
            foreach (var arg in a)
            {
                if (arg.StartsWith("window=")) win = arg.Substring(7).ToLowerInvariant();
                else if (arg.StartsWith("g=")) float.TryParse(arg.Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out g);
                else uint.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
            }
            if (id == 0 || win == null) { ctx.Log("rrw uw.jump: usage " + Help); return; }
            if (!SiteRegistry.TryGetProject(id, out var p) || p.Upgrade == null) { ctx.Log("rrw uw.jump: no upgrade project #" + id); return; }
            var sch = p.Upgrade.Schedule;
            int n = sch.N;
            g = Unity.Mathematics.math.saturate(g);
            int w;
            if (win == "setup") w = 0;
            else if (win == "teardown") w = n + 1;
            else if (!int.TryParse(win, NumberStyles.Integer, CultureInfo.InvariantCulture, out w) || w < 0 || w > n + 1)
            {
                ctx.Log("rrw uw.jump: window must be 1.." + n + ", setup or teardown (got '" + win + "')");
                return;
            }
            float p0, p1;
            if (w == 0) { p0 = 0f; p1 = sch.SetupP; }
            else if (w > n) { p0 = sch.TeardownStart; p1 = 1f; }
            else { p0 = sch.P0(w - 1); p1 = sch.P1(w - 1); }
            float target = Unity.Mathematics.math.min(p0 + g * (p1 - p0), 0.9999f);
            WorksRequests.Enqueue(new WorksRequest { Type = WorksRequestType.SetProgress, ProjectId = id, Value = target });
            ctx.Log("rrw uw.jump p" + id + " " + (w == 0 ? "setup" : w > n ? "teardown" : "window " + w + "/" + n) + " g=" + RRWLog.F(g)
                    + " p " + RRWLog.F(p.Progress) + " -> " + RRWLog.F(target) + " (applied by the Director's next update; then rrw.rebuild)");
        }
    }

    public sealed class DirectorNodesCommand : IDevCommand
    {
        public string Name => "rrw.director.nodes";
        public string Help => "rrw.director.nodes - every works edge end node: hidden?, ours?, connected edges (works+hidden / other)";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            var seen = new HashSet<Entity>();
            foreach (var rec in SiteRegistry.Edges.Values)
            {
                foreach (var n in new[] { rec.StartNode, rec.EndNode })
                {
                    if (n == Entity.Null || !seen.Add(n)) continue;
                    if (!em.Exists(n)) { ctx.Log("rrw director node " + RRWLog.E(n) + " gone"); continue; }
                    int works = 0, worksHidden = 0, other = 0;
                    if (em.HasBuffer<ConnectedEdge>(n))
                        foreach (var ce in em.GetBuffer<ConnectedEdge>(n, true))
                        {
                            if (!em.Exists(ce.m_Edge) || em.HasComponent<Deleted>(ce.m_Edge)) continue;
                            if (SiteRegistry.TryGetEdge(ce.m_Edge, out var r)) { works++; if (r.HiddenApplied) worksHidden++; }
                            else other++;
                        }
                    ctx.Log("rrw director node " + RRWLog.E(n) + " hidden=" + em.HasComponent<Hidden>(n) + " ours=" + DirectorShared.HiddenNodes.Contains(n)
                            + " works=" + works + " worksHidden=" + worksHidden + " other=" + other + " owner=" + em.HasComponent<Owner>(n));
                }
            }
        }
    }
}
#endif
