#if DEVTOOLS
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Game.Net;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Game.UI.InGame;
using RealisticRoadWorks.Dev;
using Unity.Entities;

namespace RealisticRoadWorks.V3.UI
{
    // UI dev commands (module dev commands, DEVTOOLS only). Site addressing (own copy, modules never use Src/Dev):
    //   p<id> = project id, e<index> = edge entity index, r<n> = test road index -> its project,
    //   #<n> or <n> = n-th project in ascending id order, "sel" (default) = the current selection.
    internal static class UiDevUtil
    {
        public static Entity ResolveTarget(DevContext ctx, string arg)
        {
            var em = ctx.EntityManager;
            if (string.IsNullOrEmpty(arg) || arg == "sel") return ctx.System<ToolSystem>().selected;
            if (arg[0] == 'p' && uint.TryParse(arg.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid))
                return WorksPanelState.FirstEdgeOfProject(em, pid);
            if (arg[0] == 'e' && int.TryParse(arg.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
            {
                foreach (var kv in SiteRegistry.Edges) if (kv.Key.Index == idx) return kv.Key;
                return Entity.Null;
            }
            if (arg[0] == 'r' && arg.Length > 1)
            {
                Entity road = ctx.Road(arg.Substring(1));
                return em.HasComponent<RoadWorksSite>(road) ? road : Entity.Null;
            }
            string n = arg[0] == '#' ? arg.Substring(1) : arg;
            if (int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k))
            {
                var ids = new List<uint>(SiteRegistry.Projects.Keys);
                ids.Sort();
                if (k >= 0 && k < ids.Count) return WorksPanelState.FirstEdgeOfProject(em, ids[k]);
            }
            return Entity.Null;
        }

        public static string Describe(DevContext ctx, Entity target)
        {
            var st = new WorksPanelState();
            if (!st.ResolveSelection(ctx.EntityManager, target)) return "no works for " + RRWLog.E(target);
            float now = ctx.System<TimeSystem>().normalizedTime;
            st.Compute(ctx.EntityManager, ctx.System<CitySystem>().City, now, RRWSettings.Current);
            var tx = new PanelTexts();
            tx.Build(st, RRWSettings.Current);
            var sb = new StringBuilder();
            sb.Append("p").Append(st.ProjectId).Append(' ').Append(st.Kind).Append(" phase=").Append(st.Phase)
              .Append(" f=").Append(RRWLog.F(st.F)).Append(" p=").Append(RRWLog.F(st.P))
              .Append(" step=").Append(tx.StepIndex).Append('/').Append(tx.StepCount)
              .Append(" eta=").Append(st.Eta).Append(" closure=").Append(st.Closure).Append(" openLanes=").Append(RoadZoneMath.Describe(st.OpenLanes))
              .Append(st.Releasing ? " releasing" : "")
              .Append(" paid=").Append(st.Paid).Append(" refund=").Append(st.Refund).Append(st.InstantCancel ? "(instant)" : "")
              .Append(" rush=").Append(st.RushCost).Append(st.CanRush ? "" : "(n/a)").Append(st.CanAfford ? "" : "(cannot afford)")
              .Append(" cancel=").Append(st.CanCancel ? "yes" : "no")
              .Append(" segments=").Append(st.SelectedInProject).Append('/').Append(st.SelectedTotal)
              .Append("\n  title='").Append(tx.Title).Append("' kind='").Append(tx.KindText).Append("'")
              .Append("\n  steps='").Append(string.Join(" | ", tx.Steps, 0, tx.StepCount)).Append("'")
              .Append("\n  phase='").Append(tx.PhaseText).Append("'")
              .Append("\n  eta='").Append(tx.EtaText).Append("'")
              .Append("\n  traffic='").Append(tx.TrafficText).Append("' state='").Append(PanelTexts.TrafficState(st, RRWSettings.Current))
              .Append("' pip=").Append(st.DisplayClosure)
              .Append("\n  stage switch=").Append(st.Switch).Append(" switchingSides=").Append(st.SwitchingSides)
              .Append(" stage=").Append(st.StageIndex).Append('/').Append(st.StageCount).Append(" stagedC4=").Append(st.StagedC4)
              .Append(" carHalf=").Append(st.CarHalfAllowed ? "yes" : "no").Append(" reason=").Append(st.StageReason)
              .Append(" appliedOpen=").Append(RoadZoneMath.Describe(st.OpenLanes)).Append(" softApplied=").Append(RoadZoneMath.Describe(st.SoftApplied))
              .Append(" closedB=").Append(RoadZoneMath.Describe(st.ClosedBApplied)).Append(" carAccessKept=").Append(st.CarAccessKept)
              .Append(" waiting=").Append(st.BuildingsWaiting)
              .Append("\n  money='").Append(tx.PaidText).Append(" / ").Append(tx.RefundText).Append("'")
              .Append("\n  buttons='").Append(tx.RushText).Append("' '").Append(tx.CancelText).Append("' '").Append(tx.FocusText).Append("'")
              .Append(tx.SegmentsText.Length > 0 ? "\n  segments='" + tx.SegmentsText + "'" : "")
              .Append("\n  crews=").Append(st.Crews).Append(" focus=").Append(st.FocusCrew).Append(" spawned=").Append(st.CrewsSpawned)
              .Append(" section~").Append(RRWLog.F(st.SectionLength)).Append("m show=").Append(st.ShowCrews)
              .Append(" text='").Append(tx.CrewsText).Append("' detail='").Append(tx.CrewsDetail).Append("' fills=");
            for (int i = 0; i < st.Crews && i < st.CrewFill.Length; i++) sb.Append(i > 0 ? "," : "").Append(RRWLog.F(st.CrewFill[i]));
            if (st.ModeH) sb.Append("\n  ").Append(UpgradeLine(st, tx));
            // Rollers are GameObjects, never selectable; a click on one lands on the works corridor (this panel)
            if (SiteRegistry.TryGetProject(st.ProjectId, out var pr))
            {
                var s = RRWSettings.Current;
                sb.Append("\n  rollers latched=").Append(pr.Rollers).Append(" spawned=").Append(pr.RollersSpawned)
                  .Append(" setting=").Append(s != null && s.RollersOn ? "on" : "off")
                  .Append(" detailedDigging=").Append(s != null && s.DetailedDiggingOn ? "on" : "off")
                  .Append(" digDust=").Append(s != null && s.DigDustOn ? "on" : "off")
                  .Append(" machines=").Append(pr.MachineCount).Append(" (rollers included)");
            }
            return sb.ToString();
        }

        // Upgrade works part of the dump: the view the panel used and the texts it built from it.
        public static string UpgradeLine(WorksPanelState st, PanelTexts tx)
        {
            var sb = new StringBuilder();
            var u = st.View.Upgrade;
            sb.Append("upgrade view=").Append(st.IsUpgrade ? "yes" : "no").Append(" class=").Append(st.UpClass);
            if (st.IsUpgrade)
                sb.Append(" window=").Append(u.Window).Append('/').Append(u.WindowCount).Append(" gw=").Append(RRWLog.F(u.Gw))
                  .Append(" traffic=").Append(u.Traffic).Append(" reason=").Append(u.Reason)
                  .Append(" applied=w").Append(u.AppliedWindow).Append('/').Append(u.AppliedTraffic).Append(u.Vacating ? " vacating" : "")
                  .Append(" bands=").Append(u.BandCount)
                  .Append(u.AllAtOnce ? " allAtOnce" : "").Append(" remarkHalf=").Append(u.RemarkHalf);
            sb.Append(" cancel=").Append(st.CanCancel ? "yes" : "no").Append(" refundText='").Append(tx.RefundText).Append("'")
              .Append(" stepFraction=").Append(RRWLog.F(tx.StepFraction))
              .Append(" bandLines='").Append(string.Join(" | ", tx.BandLines, 0, tx.BandLineCount)).Append("'")
              .Append(" note='").Append(tx.TrafficNote).Append("'");
            return sb.ToString();
        }
    }

    // rrw.ui.select <site> - select the works road (its street when it has one) so the info panel opens on it.
    public sealed class UiSelectCommand : IDevCommand
    {
        public string Name => "rrw.ui.select";
        public string Help => "rrw.ui.select <p<id>|e<index>|r<n>|#n> [edge] - select the site's road (street aggregate unless 'edge') to open the info panel";

        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            Entity edge = UiDevUtil.ResolveTarget(ctx, a.Length > 0 ? a[0] : "0");
            if (edge == Entity.Null) { ctx.Log("rrw ui select: no such site"); return; }
            Entity target = edge;
            bool edgeOnly = a.Length > 1 && a[1] == "edge";
            if (!edgeOnly && em.HasComponent<Aggregated>(edge))
            {
                Entity agg = em.GetComponentData<Aggregated>(edge).m_Aggregate;
                if (agg != Entity.Null && em.Exists(agg)) target = agg;
            }
            ctx.System<ToolSystem>().selected = target;
            ctx.Log("rrw ui select " + RRWLog.E(target) + (target != edge ? " (street of edge " + RRWLog.E(edge) + ")" : ""));
        }
    }

    // rrw.ui.click [stats|probe|reset] - the click path into the panel (WorksClickSelectSystem): counters + last click,
    // or a probe of what a click at the current mouse position would select (no selection change).
    public sealed class UiClickCommand : IDevCommand
    {
        public string Name => "rrw.ui.click";
        public string Help => "rrw.ui.click [stats|probe|reset] - click-to-select path: counters and last click / what a click at the mouse would select";

        public void Run(DevContext ctx, string[] a)
        {
            string mode = a.Length > 0 ? a[0] : "stats";
            var sys = ctx.System<WorksClickSelectSystem>();
            if (mode == "probe")
            {
                ctx.Log("rrw ui click probe: " + sys.Probe());
                return;
            }
            if (mode == "reset")
            {
                WorksClickSelectSystem.Clicks = WorksClickSelectSystem.Hits = WorksClickSelectSystem.Selected = 0;
                WorksClickSelectSystem.Redirects = WorksClickSelectSystem.KeptVanilla = WorksClickSelectSystem.Misses = 0;
                WorksClickSelectSystem.LastClick = "none";
            }
            Entity sel = ctx.System<ToolSystem>().selected;
            var st = new WorksPanelState();
            bool works = st.ResolveSelection(ctx.EntityManager, sel);
            ctx.Log("rrw ui click: actions " + WorksClickSelectSystem.BindInfo
                    + " clicks=" + WorksClickSelectSystem.Clicks + " hits=" + WorksClickSelectSystem.Hits
                    + " selected=" + WorksClickSelectSystem.Selected + " keptVanilla=" + WorksClickSelectSystem.KeptVanilla
                    + " misses=" + WorksClickSelectSystem.Misses + " redirects=" + WorksClickSelectSystem.Redirects
                    + "\n  last: " + WorksClickSelectSystem.LastClick
                    + "\n  selection now: " + RRWLog.E(sel) + " index=" + ctx.System<ToolSystem>().selectedIndex
                    + (works ? " -> works panel p" + st.ProjectId : " (no works panel)"));
        }
    }

    // rrw.ui.dump [site|sel] - what the info section would show (numbers + every text).
    public sealed class UiDumpCommand : IDevCommand
    {
        public string Name => "rrw.ui.dump";
        public string Help => "rrw.ui.dump [sel|p<id>|e<index>|r<n>|#n] - the info section's numbers and texts for the selection or a site";

        public void Run(DevContext ctx, string[] a)
        {
            Entity target = UiDevUtil.ResolveTarget(ctx, a.Length > 0 ? a[0] : "sel");
            ctx.Log("rrw ui " + UiDevUtil.Describe(ctx, target));
        }
    }

    // rrw.ui.texts - every traffic-row variant of the staged opening built from synthetic panel states with the
    // current settings and language, so all texts can be reviewed without driving a site through every state.
    public sealed class UiTextsCommand : IDevCommand
    {
        public string Name => "rrw.ui.texts";
        public string Help => "rrw.ui.texts - every staged traffic-row text from synthetic states: one direction, switching sides, pedestrians + each reason, D0, release";

        public void Run(DevContext ctx, string[] a)
        {
            var s = RRWSettings.Current;
            var st = new WorksPanelState();
            void Case(string label, System.Action<WorksPanelState> set)
            {
                st.Clear();
                st.Kind = WorksKind.Construction;
                st.Mode = VisualMode.FullDig;
                st.Phase = WorksPhase.Finishing;
                st.Closure = ClosureLevel.Closed;
                st.StagedC4 = true;
                set(st);
                ctx.Log("rrw ui text " + label + ": '" + PanelTexts.Traffic(st, s) + "' pip=" + st.DisplayClosure);
            }
            Case("C4a one direction", x => { x.CarHalfAllowed = true; x.OpenLanes = RoadZones.LeftHalf | RoadZones.Sidewalks; });
            Case("C4a one direction + 3 waiting (CLOSED-S works half)", x => { x.CarHalfAllowed = true; x.OpenLanes = RoadZones.LeftHalf | RoadZones.Sidewalks; x.BuildingsWaiting = 3; });
            Case("C4a one direction + 3 waiting (CLOSED-B works half)", x => { x.CarHalfAllowed = true; x.OpenLanes = RoadZones.LeftHalf | RoadZones.Sidewalks; x.ClosedBApplied = RoadZones.RightHalf; x.CarAccessKept = true; x.BuildingsWaiting = 3; });
            Case("switch Vacate (old layout)", x => { x.CarHalfAllowed = true; x.Switch = StageSwitch.Vacate; x.OpenLanes = RoadZones.LeftHalf | RoadZones.Sidewalks; });
            Case("switch Swap", x => { x.CarHalfAllowed = true; x.Switch = StageSwitch.Swap; x.OpenLanes = RoadZones.RightHalf | RoadZones.Sidewalks; x.SoftApplied = RoadZones.LeftHalf; });
            Case("switch Drain", x => { x.CarHalfAllowed = true; x.Switch = StageSwitch.Drain; x.OpenLanes = RoadZones.RightHalf | RoadZones.Sidewalks; x.SoftApplied = RoadZones.LeftHalf; });
            Case("re-close (car half soft, no switch)", x => { x.CarHalfAllowed = false; x.StageReason = StageBlockReason.OneWay; x.OpenLanes = RoadZones.Sidewalks; x.SoftApplied = RoadZones.LeftHalf; });
            Case("C4b one direction", x => { x.CarHalfAllowed = true; x.StageIndex = 1; x.StageCount = 2; x.OpenLanes = RoadZones.RightHalf | RoadZones.Sidewalks; });
            Case("C4 waiting for classification", x => { x.CarHalfAllowed = false; x.OpenLanes = RoadZones.Sidewalks; });
            foreach (StageBlockReason r in System.Enum.GetValues(typeof(StageBlockReason)))
            {
                if (r == StageBlockReason.None) continue;
                Case("C4 no car half (" + r + ")", x => { x.CarHalfAllowed = false; x.StageReason = r; x.OpenLanes = RoadZones.Sidewalks; x.BuildingsWaiting = 2; });
            }
            Case("C3 sidewalks", x => { x.Phase = WorksPhase.Paving; x.StagedC4 = false; x.OpenLanes = RoadZones.Sidewalks; });
            Case("D0 house sidewalks open", x => { x.Kind = WorksKind.Demolition; x.Phase = WorksPhase.BreakUp; x.StagedC4 = false; x.OpenLanes = RoadZones.SidewalkRight; });
            Case("D0 house sidewalks draining", x => { x.Kind = WorksKind.Demolition; x.Phase = WorksPhase.BreakUp; x.StagedC4 = false; x.Switch = StageSwitch.Drain; x.SoftApplied = RoadZones.SidewalkRight; });
            Case("released, machines leaving", x => { x.Phase = WorksPhase.Complete; x.Releasing = true; x.OpenLanes = RoadZones.RightHalf | RoadZones.Sidewalks; });
            Case("hidden phase closed", x => { x.Phase = WorksPhase.Excavation; x.StagedC4 = false; });
            // The crews row for 2..kMaxCrewsPerProject crews on a 1 km road (one crew: no row).
            var tx = new PanelTexts();
            for (int n = 1; n <= RRWConst.kMaxCrewsPerProject; n++)
            {
                st.Clear();
                st.Kind = WorksKind.Construction; st.Mode = VisualMode.FullDig; st.Phase = WorksPhase.Paving; st.Eta = EtaState.Working;
                st.Crews = n; st.SectionLength = 1000f / n;
                tx.Build(st, s);
                ctx.Log("rrw ui text crews " + n + ": '" + tx.CrewsText + "' detail='" + tx.CrewsDetail + "'");
            }
        }
    }

    // rrw.ui.tip - the last upgrade estimate of the road / upgrade tool tooltip and the last bulldozer hover over upgrade works.
    public sealed class UiTipCommand : IDevCommand
    {
        public string Name => "rrw.ui.tip";
        public string Help => "rrw.ui.tip - last replace / upgrade tool tooltip estimate (pieces, class, hours, primitive per window, texts) and bulldozer refund";

        public void Run(DevContext ctx, string[] a)
        {
            ctx.Log("rrw ui tip upgrade: " + WorksTooltipSystem.LastUpgrade + " | bulldoze: " + WorksTooltipSystem.LastUpgradeBulldoze);
        }
    }

    // rrw.ui.uwtexts - the upgrade works panel texts from synthetic views (a one-sided widening with a re-marking step: setup, each
    // step, teardown, complete; then the traffic row of every primitive), so all texts can be reviewed without a site.
    public sealed class UiUpgradeTextsCommand : IDevCommand
    {
        public string Name => "rrw.ui.uwtexts";
        public string Help => "rrw.ui.uwtexts - upgrade works panel texts from synthetic views: steps, bar, band lines, traffic row and note per primitive";

        public void Run(DevContext ctx, string[] a)
        {
            var s = RRWSettings.Current;
            var st = new WorksPanelState();
            var tx = new PanelTexts();
            void Case(string label, sbyte window, float gw, BandTraffic prim, StageBlockReason reason, StageSwitch sw = StageSwitch.None, bool complete = false,
                      int appliedWindow = -1, BandTraffic appliedPrim = BandTraffic.Undecided)
            {
                st.Clear();
                st.Kind = WorksKind.Construction;
                st.Mode = VisualMode.HalfWidth;
                st.Phase = complete ? WorksPhase.Complete : WorksPhase.Excavation;
                st.Closure = PhasePlan.Closes(prim) ? ClosureLevel.Closed : ClosureLevel.SlowZone;
                st.Switch = sw;
                st.Eta = EtaState.Working;
                st.IsUpgrade = true;
                st.UpClass = UpgradeClass.Mixed;
                st.View = Synthetic(window, gw, prim, reason);
                if (appliedWindow >= 0) st.View.Upgrade.SetApplied(appliedWindow, appliedPrim, RoadZones.LeftHalf);
                tx.Build(st, s);
                ctx.Log("rrw ui uw " + label + ": kind='" + tx.KindText + "' steps='" + string.Join(" | ", tx.Steps, 0, tx.StepCount) + "' step="
                        + tx.StepIndex + "/" + tx.StepCount + " f=" + RRWLog.F(tx.StepFraction) + " bar='" + tx.PhaseText + "' bands='"
                        + string.Join(" | ", tx.BandLines, 0, tx.BandLineCount) + "' traffic='" + tx.TrafficText + "' note='" + tx.TrafficNote
                        + "' cancelButton=" + (st.ModeH ? "hidden" : "shown"));
            }
            Case("setup", -1, 0.5f, BandTraffic.Drop, StageBlockReason.None);
            Case("step 1 at 30%", 0, 0.3f, BandTraffic.Drop, StageBlockReason.None);
            Case("step 1 at 80%", 0, 0.8f, BandTraffic.Drop, StageBlockReason.None);
            Case("step 2 at 60%", 1, 0.6f, BandTraffic.Half, StageBlockReason.None);
            Case("teardown", 2, 0.5f, BandTraffic.Half, StageBlockReason.None);
            Case("complete", 2, 1f, BandTraffic.Half, StageBlockReason.None, StageSwitch.None, true);
            Case("one direction", 0, 0.5f, BandTraffic.Half, StageBlockReason.None);
            Case("carriageway", 0, 0.5f, BandTraffic.Carriageway, StageBlockReason.None);
            Case("sidewalk", 0, 0.5f, BandTraffic.Sidewalk, StageBlockReason.None);
            Case("none", 0, 0.5f, BandTraffic.None, StageBlockReason.None);
            Case("window switch", 1, 0f, BandTraffic.Half, StageBlockReason.None, StageSwitch.Swap);
            Case("vacating step 1 (slow zone) into step 2", 1, 0f, BandTraffic.Half, StageBlockReason.None, StageSwitch.None, false, 0, BandTraffic.Dressing);
            Case("slow zone, no reason", 0, 0.5f, BandTraffic.Dressing, StageBlockReason.None);
            foreach (StageBlockReason r in System.Enum.GetValues(typeof(StageBlockReason)))
            {
                if (r == StageBlockReason.None) continue;
                Case("slow zone (" + r + ")", 0, 0.5f, BandTraffic.Dressing, r);
            }
            Case("undecided, waiting", 0, 0.5f, BandTraffic.Undecided, StageBlockReason.Waiting);

            // The replace / upgrade tool tooltip line of every class with each expected primitive, and the full rebuild line.
            var prims = new[] { BandTraffic.Drop, BandTraffic.Half, BandTraffic.Carriageway, BandTraffic.Dressing, BandTraffic.None };
            foreach (var cls in new[] { UpgradeClass.Widen, UpgradeClass.Narrow, UpgradeClass.Remark, UpgradeClass.Mixed })
            {
                var sb = new StringBuilder("rrw ui uw tip ").Append(cls).Append(':');
                foreach (var p in prims) sb.Append(' ').Append(p).Append("='").Append(UpgradeTip.TipText(cls, 7.8f, p)).Append('\'');
                ctx.Log(sb.ToString());
            }
            ctx.Log("rrw ui uw tip rebuild: '" + RRWText.Format(RRWText.UpgradeTipHoursFullRebuild, UiFormat.Hours(19f),
                    RRWText.Get(RRWText.StructuralKey(UpgradeStructural.Elevation))) + "'");
        }

        // Mixed one-sided widening: window 0 = new strip on the left + kerb move on the right, window 1 = the markings.
        private static ProjectView Synthetic(sbyte window, float gw, BandTraffic prim, StageBlockReason reason)
        {
            var u = new UpgradeView
            {
                Class = UpgradeClass.Mixed,
                Window = window,
                WindowCount = 2,
                Gw = gw,
                Traffic = prim,
                Reason = reason,
                BandCount = 3,
            };
            u.SetBand(0, new UpgradeBandView { Kind = BandKind.Build, Side = BandSide.Left, Lo = -12f, Hi = -1f, Window = 0 });
            u.SetBand(1, new UpgradeBandView { Kind = BandKind.Rebuild, Side = BandSide.Right, Lo = 8f, Hi = 9f, Window = 0 });
            u.SetBand(2, new UpgradeBandView { Kind = BandKind.Remark, Side = BandSide.Both, Lo = -8f, Hi = 8f, Window = 1 });
            u.SetPrim(0, prim);
            u.SetPrim(1, prim);
            var v = ProjectView.Simple(WorksKind.Construction, VisualMode.HalfWidth, WorksPhase.Excavation, 0f, 200f);
            v.Upgrade = u;
            return v;
        }
    }

    // rrw.ui.focus [site|sel] - the "Show work front" button without the UI.
    public sealed class UiFocusCommand : IDevCommand
    {
        public string Name => "rrw.ui.focus";
        public string Help => "rrw.ui.focus [sel|p<id>|e<index>|r<n>|#n] [crew=i] - camera to the work front exactly like the Show work front button " +
                              "(repeat = next crew); crew=i shows crew i's front";

        public void Run(DevContext ctx, string[] a)
        {
            string site = "sel";
            int crew = -1;
            foreach (var arg in a)
            {
                if (arg.StartsWith("crew=", System.StringComparison.OrdinalIgnoreCase))
                    int.TryParse(arg.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out crew);
                else site = arg;
            }
            Entity target = UiDevUtil.ResolveTarget(ctx, site);
            var st = new WorksPanelState();
            if (!st.ResolveSelection(ctx.EntityManager, target)) { ctx.Log("rrw ui focus: no works for " + RRWLog.E(target)); return; }
            var cs = ctx.System<CameraUpdateSystem>();
            bool ok = WorksInfoSection.FocusFront(ctx.EntityManager, cs, ctx.System<SelectedInfoUISystem>(), st, crew);
            string where = "";
            if (ok && cs.gamePlayController != null)
            {
                UnityEngine.Vector3 pv = cs.gamePlayController.pivot;
                where = " pivot=(" + RRWLog.F(pv.x) + "," + RRWLog.F(pv.y) + "," + RRWLog.F(pv.z) + ")";
            }
            ctx.Log("rrw ui focus p" + st.ProjectId + (crew >= 0 ? " crew=" + crew : "") + (ok ? " ok" + where : " FAILED (no gameplay camera or no front)"));
        }
    }
}
#endif
