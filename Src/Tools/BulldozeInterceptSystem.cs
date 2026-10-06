using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaComp = Game.Areas.Area;
using ObjectComp = Game.Objects.Object;

namespace RealisticRoadWorks.V3.Tooling
{
    // ToolUpdate, right after BulldozeToolSystem decided to apply (update order 100). Same approach as the previous mod
    // version (verified in game): when the bulldozer is about to delete roads, the apply is
    // turned into a Clear by reflection (the preview vanishes, vanilla deletes nothing) and the works system takes over:
    //   - road without a site      -> demolition works (SiteFactory, one project per chain); the vanilla Recent refund
    //                                 (Temp.m_Cost < 0, see the game's CostSystem) is credited here, once
    //   - road under construction  -> CancelEdge / CancelEdgeInstant per edge (the Director splits, cancels / deletes, refunds)
    //   - road being demolished    -> ignored
    //   - our decals / props / puppets (RRWDerived or "RRW " area prefabs) are never bulldozed.
    // Mixed selections (roads + other objects) lose the other objects in this action (as in the previous version, logged once).
    [RegisterSystem(SystemUpdatePhase.ToolUpdate, After = typeof(BulldozeToolSystem), Order = RRWOrder.BulldozeIntercept)]
    public partial class BulldozeInterceptSystem : GameSystemBase
    {
        private static readonly PropertyInfo s_ApplyMode = typeof(ToolBaseSystem).GetProperty(nameof(ToolBaseSystem.applyMode));

        private ToolSystem m_ToolSystem;
        private BulldozeToolSystem m_Bulldoze;
        private CitySystem m_CitySystem;
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_Temps;
        private EntityQuery m_Sites;
        private readonly RRWGuard m_Guard = new RRWGuard("tools BulldozeIntercept");

        private readonly List<Entity> m_Roads = new List<Entity>();
        private readonly List<int> m_RoadCost = new List<int>();
        private readonly List<SiteFactoryEdge> m_Demolish = new List<SiteFactoryEdge>();
        private readonly List<SiteFactoryResult> m_Results = new List<SiteFactoryResult>();

        public static string LastSummary = "none";

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_Bulldoze = World.GetOrCreateSystemManaged<BulldozeToolSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Temps = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>() },
                Any = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<AreaComp>(), ComponentType.ReadOnly<ObjectComp>() },
            });
            m_Sites = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadWorksSite>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
            if (s_ApplyMode == null || s_ApplyMode.GetSetMethod(true) == null)
                RRWLog.Warn("tools: ToolBaseSystem.applyMode setter not found - bulldozer intercept disabled");
        }

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted || s_ApplyMode == null) return;
            if (!ToolUtil.InGame() || !m_ToolSystem.actionMode.IsGame()) return;
            if (m_ToolSystem.activeTool != m_Bulldoze || m_Bulldoze.applyMode != ApplyMode.Apply) return;
            if (m_Temps.IsEmptyIgnoreFilter) return;
            long t0 = RRWPerf.Start();
            try
            {
                Process();
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
            }
            finally
            {
                RRWPerf.Stop(PerfSlot.Tools, t0);
            }
        }

        private void Process()
        {
            var em = EntityManager;
            m_Roads.Clear();
            m_RoadCost.Clear();
            int derived = 0, other = 0;
            var entities = m_Temps.ToEntityArray(Allocator.Temp);
            var temps = m_Temps.ToComponentDataArray<Temp>(Allocator.Temp);
            try
            {
                for (int i = 0; i < temps.Length; i++)
                {
                    Temp t = temps[i];
                    if ((t.m_Flags & TempFlags.Delete) == 0) continue;
                    // Delete|Hidden = the partner of a node reduction (NodeReductionSystem), not something the player picked
                    if ((t.m_Flags & TempFlags.Hidden) != 0) continue;
                    Entity orig = t.m_Original;
                    if (orig == Entity.Null || !em.Exists(orig) || em.HasComponent<Deleted>(orig)) continue;
                    if (em.HasComponent<RRWDerived>(orig) || em.HasComponent<RRWMachine>(orig)) { derived++; continue; }
                    bool owned = em.HasComponent<Owner>(entities[i]) || em.HasComponent<Owner>(orig);
                    if (em.HasComponent<Edge>(orig))
                    {
                        if (em.HasComponent<Road>(orig) && !em.HasComponent<Owner>(orig))
                        {
                            if (!m_Roads.Contains(orig)) { m_Roads.Add(orig); m_RoadCost.Add(t.m_Cost); }
                        }
                        else if (!owned) other++;
                        continue;
                    }
                    if (em.HasComponent<AreaComp>(orig))
                    {
                        if (IsRrwArea(orig)) derived++;
                        else if (!owned) other++;
                        continue;
                    }
                    if (!owned) other++;
                }
            }
            finally
            {
                entities.Dispose();
                temps.Dispose();
            }

            if (m_Roads.Count == 0 && derived == 0) return;   // nothing of ours: vanilla handles plain deletes

            if (m_Roads.Count == 0)
            {
                Clear();
                RRWLog.Once("tools-derived-bulldoze", "tools: bulldozing road-works decals, props or machines is ignored (they go away with the works)");
                LastSummary = "ignored works props/decals=" + derived;
                return;
            }

            var settings = RRWSettings.Current;
            if (settings == null) { RRWLog.Once("tools-nosettings-bulldoze", "tools: settings not registered, bulldozer left to vanilla"); return; }

            int cancel = 0, instant = 0, demolishing = 0, outside = 0, refund = 0;
            m_Demolish.Clear();
            for (int i = 0; i < m_Roads.Count; i++)
            {
                Entity orig = m_Roads[i];
                if (em.HasComponent<RoadWorksSite>(orig))
                {
                    var site = em.GetComponentData<RoadWorksSite>(orig);
                    if (site.Kind == WorksKind.Demolition) { demolishing++; continue; }
                    bool inst = settings.InstantCancelUnfinished && site.Progress < RRWConst.kInstantCancelProgress;
                    WorksRequests.Enqueue(inst ? WorksRequestType.CancelEdgeInstant : WorksRequestType.CancelEdge, orig);
                    if (inst) instant++; else cancel++;
                    continue;
                }
                if (ToolUtil.TouchesOutsideConnection(em, orig)) { outside++; continue; }
                m_Demolish.Add(ToolUtil.FactoryEdge(em, orig, 0, ToolUtil.HasDependants(em, orig)));
                if (m_RoadCost[i] < 0) refund += -m_RoadCost[i];   // vanilla Recent refund (decays with time): paid once, here
            }

            // Only plain roads at outside connections: nothing for us, vanilla decides.
            if (m_Demolish.Count == 0 && cancel + instant + demolishing + derived == 0) return;

            int started = 0;
            if (m_Demolish.Count > 0)
            {
                ToolUtil.EnsureProjectIds(em, m_Sites);
                m_Results.Clear();
                SiteFactory.CreateProjects(m_Demolish, WorksKind.Demolition, EcsUtil.RoadClass(em, m_Demolish[0].Edge), settings, SiteFlags.None, m_Results);
                for (int i = 0; i < m_Results.Count; i++)
                {
                    var r = m_Results[i];
                    if (!EcsUtil.Alive(em, r.Edge)) continue;
                    ToolUtil.PutSite(em, r.Edge, r.Site);
                    started++;
                }
                if (refund > 0) EcsUtil.Credit(em, m_CitySystem.City, refund);
            }

            // Only roads under demolition picked: still swallow the click (vanilla would delete them mid-works).
            Clear();

            if (other > 0)
                RRWLog.Once("tools-mixed-bulldoze", "tools: bulldozer selection mixed roads with other objects: the other objects were not removed in this action (bulldoze them again)");
            if (outside > 0)
                RRWLog.Once("tools-outside-bulldoze", "tools: roads at an outside connection are not demolished by road works");
            LastSummary = "demolition=" + started + " cancel=" + cancel + " instantCancel=" + instant + " alreadyDemolishing=" + demolishing
                          + " refund=" + refund + ToolUtil.Summary(derived, "worksProps") + ToolUtil.Summary(other, "otherSkipped")
                          + ToolUtil.Summary(outside, "outside");
            RRWLog.Info("tools: bulldoze intercepted: " + LastSummary);
        }

        private bool IsRrwArea(Entity area)
        {
            var em = EntityManager;
            if (!em.HasComponent<PrefabRef>(area)) return false;
            string name = EcsUtil.PrefabName(m_PrefabSystem, em, area);
            return name != null && name.StartsWith(PrefabNames.RrwPrefix, StringComparison.Ordinal);
        }

        private void Clear()
        {
            try { s_ApplyMode.SetValue(m_Bulldoze, ApplyMode.Clear); }
            catch (Exception e) { RRWLog.ErrorOnce("tools bulldoze clear", e); }
        }
    }
}
