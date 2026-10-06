#if DEVTOOLS
using System.Collections.Generic;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Tooling
{
    // rrw.tools.last - what the last road-tool apply and the last bulldozer click did.
    public sealed class ToolsLastCommand : IDevCommand
    {
        public string Name => "rrw.tools.last";
        public string Help => "rrw.tools.last - classification of the last tool apply (new/replaced/split/combine/upgrade) and the last bulldozer intercept";
        public void Run(DevContext ctx, string[] args)
        {
            ctx.Log("rrw tools apply: " + WorksTagSystem.LastSummary);
            ctx.Log("rrw tools preview-hidden temps classified since load=" + WorksTagSystem.TotalPreviewHiddenTemps
                    + " hidden on apply=" + WorksTagSystem.TotalHiddenOnApply + " (remnants of hidden works roads keep their site and stay hidden)");
            ctx.Log("rrw tools bulldoze: " + BulldozeInterceptSystem.LastSummary);
        }
    }

    // rrw.tools.classify [road index] [trace] - what an apply would make of the road preview on screen (replace mode of the
    // road tool, or an upgrade tool hovering a road): one line per changed piece with the shared frame, both layouts and their
    // calibration residuals, the class and bands, the decision by the "Road upgrades" setting and a dry-run estimate (hours,
    // windows, the traffic primitive each window would get with and without a detour). With a road index: only the preview of
    // that road, or that road's live layout (and its upgrade works, if any) when nothing previews it. Nothing is written.
    public sealed class ToolsClassifyCommand : IDevCommand
    {
        private static readonly UpgradeLayoutRead s_Neu = new UpgradeLayoutRead();
        private static readonly UpgradeLayoutRead s_Old = new UpgradeLayoutRead();
        private static readonly List<string> s_Trace = new List<string>();
        private static readonly List<SubStrip> s_Sub = new List<SubStrip>();
        private static readonly List<SiteFactoryResult> s_Results = new List<SiteFactoryResult>();

        public string Name => "rrw.tools.classify";
        public string Help => "rrw.tools.classify [road index] [trace] - classify the road preview under the cursor (replace / upgrade tool) like an apply: frame, strips, residual, class, bands, decision, estimate (hours, windows, primitive per window); with a road index: that road's preview or live layout";

        public void Run(DevContext ctx, string[] args)
        {
            var em = ctx.EntityManager;
            var s = RRWSettings.Current;
            Entity road = Entity.Null;
            bool trace = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "trace") trace = true;
                else road = ctx.Road(args[i]);
            }
            var prefabs = ctx.System<PrefabSystem>();
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Road>(),
                    ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Owner>() },
            });
            var temps = q.ToEntityArray(Allocator.Temp);
            int shown = 0, skipped = 0;
            try
            {
                for (int i = 0; i < temps.Length; i++)
                {
                    var e = temps[i];
                    var t = em.GetComponentData<Temp>(e);
                    if ((t.m_Flags & (TempFlags.Delete | TempFlags.Cancel | TempFlags.Combine)) != 0) continue;
                    Entity orig = t.m_Original;
                    if (orig == Entity.Null || !em.Exists(orig) || em.HasComponent<Temp>(orig) || !em.HasComponent<Curve>(orig)) continue;
                    if (road != Entity.Null && orig != road) continue;
                    bool replace = (t.m_Flags & TempFlags.Replace) != 0;
                    if (!replace && !UpgradeRules.IsChange(em, e, t)) { skipped++; continue; }
                    ctx.Log(Describe(em, prefabs, s, e, t, replace, trace));
                    shown++;
                }
            }
            finally { temps.Dispose(); }
            if (shown > 0)
            {
                ctx.Log("rrw tools classify: " + shown + " changed piece(s)" + (skipped > 0 ? ", " + skipped + " regenerated neighbour(s) skipped" : "")
                        + " setting=" + (s != null ? s.UpgradeMode.ToString() : "-"));
                return;
            }
            if (road == Entity.Null)
            {
                ctx.Log("rrw tools classify: no road preview changes a road (hover a road with the road tool in replace mode or an upgrade tool, or pass a road index)");
                return;
            }
            ctx.Log(DescribeLive(em, prefabs, road));
        }

        private static string Describe(EntityManager em, PrefabSystem prefabs, RRWSetting s, Entity temp, Temp t, bool replace, bool trace)
        {
            Entity orig = t.m_Original;
            var sb = new StringBuilder(1024);
            string rule = replace ? "replace" : (t.m_Flags & TempFlags.Upgrade) != 0 ? "upgrade" : (t.m_Flags & TempFlags.Modify) != 0 ? "modify" : "changed";
            var curve = em.GetComponentData<Curve>(temp).m_Bezier;
            var arc = new EdgeArc(curve);
            sb.Append("rrw tools classify temp=").Append(RRWLog.E(temp)).Append(" orig=").Append(RRWLog.E(orig)).Append(" rule=").Append(rule)
              .Append(" \"").Append(EcsUtil.PrefabName(prefabs, em, orig)).Append("\" -> \"").Append(EcsUtil.PrefabName(prefabs, em, temp))
              .Append("\" L=").Append(RRWLog.F(arc.Length)).Append(" cost=").Append(t.m_Cost);

            s_Trace.Clear();
            var cls = UpgradeLayoutReader.Classify(em, temp, orig, s_Neu, s_Old, out var spec, out bool instant, out bool aligned, out var al,
                                                   trace ? s_Trace : null);
            sb.Append(" frame=new-curve align=").Append(aligned ? "ok" : "FAIL").Append(" shift=").Append(RRWLog.F(al.Shift)).Append(al.Reversed ? " reversed" : "")
              .Append(" dev=").Append(RRWLog.F(al.MaxDeviation))
              .Append(" old={").Append(s_Old.Describe()).Append("} new={").Append(s_Neu.Describe()).Append('}')
              .Append(" residual old=").Append(RRWLog.F(s_Old.L.CalibResidual)).Append(" new=").Append(RRWLog.F(s_Neu.L.CalibResidual))
              .Append(" class=").Append(cls).Append(" spec=").Append(spec).Append(instant ? " instant(wall)" : "");

            bool prefabDiffers = UpgradeRules.PrefabDiffers(em, temp, orig);
            int buildings = em.HasBuffer<Game.Buildings.ConnectedBuilding>(orig) ? em.GetBuffer<Game.Buildings.ConnectedBuilding>(orig, true).Length : 0;
            bool keeps = s_Neu.L.Readable && UpgradeLanes.EveryDirectionKeepsLane(s_Neu.L, spec);
            sb.Append(" buildings=").Append(buildings).Append(" keepsLanes=").Append(keeps ? 1 : 0);
            if (em.HasComponent<RoadWorksSite>(orig))
            {
                var site = em.GetComponentData<RoadWorksSite>(orig);
                sb.Append(" works=p").Append(site.m_ProjectId).Append('/').Append(site.Kind).Append('/').Append(site.Mode)
                  .Append(UpgradeRules.IsModeH(site) ? " (an apply merges into these upgrade works)" : " (an apply keeps these works)");
            }
            if (s == null) { sb.Append(" decision=- (settings not registered)"); return Finish(sb, trace); }
            var action = UpgradeRules.Decide(s.UpgradeMode, prefabDiffers, cls, spec.Why, instant);
            if (replace && !prefabDiffers) action = UpgradeAction.Instant;   // a same-type Replace temp starts nothing
            sb.Append(" decision=").Append(action).Append(" (").Append(UpgradeTagger.Reason(s.UpgradeMode, cls, spec, instant)).Append(')');
            if (action == UpgradeAction.Upgrade) Estimate(em, s, temp, spec, buildings > 0, keeps, sb);
            return Finish(sb, trace);
        }

        private static string Finish(StringBuilder sb, bool trace)
        {
            if (trace && s_Trace.Count > 0) sb.Append(" trace{").Append(string.Join(" ; ", s_Trace)).Append('}');
            return sb.ToString();
        }

        // Dry run of the factory for this piece alone, then the ladder each window would climb (the Director decides at the
        // window start with the live detour verdict, cut-edge state and saved ceilings; this is the expectation).
        private static void Estimate(EntityManager em, RRWSetting s, Entity temp, in UpgradeSpec spec, bool buildings, bool keeps, StringBuilder sb)
        {
            var fe = ToolUtil.FactoryEdge(em, temp, 0, buildings);
            fe.Buildings = buildings;
            fe.KeepsLanes = keeps;
            s_Results.Clear();
            SiteFactory.CreateUpgradeProjects(new List<SiteFactoryEdge> { fe }, new List<UpgradeSpec> { spec }, EcsUtil.RoadClass(em, temp),
                                              UpgradeRates.From(s), SiteFlags.None, s_Results, null, true);
            if (s_Results.Count == 0) { sb.Append(" est{none}"); return; }
            var site = s_Results[0].Site;
            var sched = site.Schedule;
            sb.Append(" est{hours=").Append(RRWLog.F(WorkTime.HoursFromFrames(site.m_WorkRequired))).Append(" windows ").Append(sched)
              .Append(" flags=").Append(site.UpFlags).Append(" prims=");
            for (int w = 0; w < sched.N; w++)
            {
                var input = new WindowPrimitiveInput
                {
                    EveryDirectionKeepsLane = keeps,
                    Buildings = buildings,
                    SideBuildings = buildings,
                    CarHalfBlock = StageBlockReason.None,
                    Policy = s.Policy,
                    DropGate = RRWGates.UpgradeDrop,
                    ClosedBGate = RRWGates.ClosedB,
                    Saved = BandTraffic.Undecided,
                };
                bool left = false, right = false, middle = false;
                for (int i = 0; i < site.m_BandCount; i++)
                {
                    var b = site.Band(i);
                    if (b.Window != w) continue;
                    if (b.Kind == BandKind.Remark) { input.RemarkWindow = true; continue; }
                    if (b.Kind == BandKind.Remove) continue;
                    input.HasBuild = true;
                    if (b.Side == BandSide.Left) left = true; else if (b.Side == BandSide.Right) right = true; else middle = true;
                    UpgradeDiff.SubStrips(s_Neu.L, b, s_Sub);
                    for (int k = 0; k < s_Sub.Count; k++)
                        if (s_Sub[k].Kind == SubKind.DriveLane || s_Sub[k].Kind == SubKind.NewParking || s_Sub[k].Kind == SubKind.Bike) input.CarSubStrips = true;
                }
                input.BothSides = middle || (left && right);
                input.DetourExists = true;
                var with = PhasePlan.WindowPrimitive(input, out _);
                input.DetourExists = false;
                var without = PhasePlan.WindowPrimitive(input, out var why);
                if (w > 0) sb.Append(',');
                sb.Append('w').Append(w).Append('=').Append(with);
                if (without != with) sb.Append("|noDetour:").Append(without).Append(why != StageBlockReason.None ? "(" + why + ")" : "");
            }
            sb.Append('}');
        }

        private static string DescribeLive(EntityManager em, PrefabSystem prefabs, Entity road)
        {
            var sb = new StringBuilder(512);
            var curve = em.GetComponentData<Curve>(road).m_Bezier;
            var arc = new EdgeArc(curve);
            bool ok = UpgradeLayoutReader.Read(em, road, arc, s_Neu);
            sb.Append("rrw tools classify road=").Append(RRWLog.E(road)).Append(" \"").Append(EcsUtil.PrefabName(prefabs, em, road)).Append("\" L=")
              .Append(RRWLog.F(arc.Length)).Append(" no preview: live layout (own curve) ").Append(ok ? "" : "UNREADABLE ").Append('{').Append(s_Neu.Describe()).Append('}')
              .Append(" residual=").Append(RRWLog.F(s_Neu.L.CalibResidual));
            if (!em.HasComponent<RoadWorksSite>(road)) return sb.Append(" works=none").ToString();
            var site = em.GetComponentData<RoadWorksSite>(road);
            sb.Append(" works=p").Append(site.m_ProjectId).Append('/').Append(site.Kind).Append('/').Append(site.Mode).Append(" p=").Append(RRWLog.F(site.Progress));
            if (!site.IsUpgrade) return sb.ToString();
            sb.Append(" upgrade class=").Append(site.UpClass).Append(" windows ").Append(site.Schedule).Append(" flags=").Append(site.UpFlags).Append(" bands:");
            for (int i = 0; i < math.min(site.m_BandCount, RRWConst.kUwMaxBands); i++)
            {
                var b = site.Band(i);
                var st = UpgradePlan.StateOf(site.Schedule, b.Window, b.G0f, site.Progress, out float g);
                sb.Append(' ').Append(b).Append(' ').Append(st).Append(" g=").Append(RRWLog.F(g));
            }
            return sb.ToString();
        }
    }
}
#endif
