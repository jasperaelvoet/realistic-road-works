using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.SceneFlow;
using Game.Tools;
using Game.UI.Localization;
using Game.UI.Tooltip;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using NetEdge = Game.Net.Edge;

namespace RealisticRoadWorks.V3.UI
{
    // Mouse tooltips for the road and bulldozer tools, setting ShowTooltips:
    //  * road tool drawing new road:      "Construction ≈ 7.7 h"
    //  * bulldozer over plain roads:      "Demolition ≈ 3.0 h · road closes"  (Realistic policy; "Demolition ≈ 3.0 h" otherwise)
    //  * bulldozer over roads being built: "Cancels construction · refund ¢12,000" (full paid below 5 % with instant cancel)
    // Reads Temp entities only; the texts are recomputed every few updates and the cached widgets re-added every frame
    // (TooltipUISystem clears its mouse group each frame).
    [RegisterSystem(SystemUpdatePhase.UITooltip, Order = RRWOrder.Tooltips)]
    public partial class WorksTooltipSystem : TooltipSystemBase
    {
        private const int kRecomputeEveryUpdates = 6;
        private const string kIcon = "Media/Game/Icons/RoadMaintenanceDepot.svg";

        private ToolSystem m_ToolSystem;
        private BulldozeToolSystem m_Bulldoze;
        private NetToolSystem m_NetTool;
        private EntityQuery m_TempEdges;
        private readonly RRWGuard m_Guard = new RRWGuard("ui tooltips");

        private StringTooltip m_Construction, m_Demolition, m_Cancel;
        private bool m_ShowConstruction, m_ShowDemolition, m_ShowCancel;
        private uint m_LastCompute;
        private uint m_LastRun;            // the system does not run without Temp edges: a gap means the cache is stale
        private bool m_HaveCompute;

        private readonly List<EdgeArc> m_ArcPool = new List<EdgeArc>();
        private int m_ArcCount;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_Bulldoze = World.GetOrCreateSystemManaged<BulldozeToolSystem>();
            m_NetTool = World.GetOrCreateSystemManaged<NetToolSystem>();
            m_TempEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<NetEdge>(), ComponentType.ReadOnly<Curve>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_Construction = new StringTooltip { path = "rrwConstruction", icon = kIcon, color = TooltipColor.Info };
            m_Demolition = new StringTooltip { path = "rrwDemolition", icon = kIcon, color = TooltipColor.Warning };
            m_Cancel = new StringTooltip { path = "rrwCancel", icon = kIcon, color = TooltipColor.Warning };
            RequireForUpdate(m_TempEdges);
        }

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted) return;
            try
            {
                var s = RRWSettings.Current;
                if (s == null || !s.ShowTooltips || GameManager.instance == null || GameManager.instance.gameMode != GameMode.Game)
                { m_HaveCompute = false; return; }
                var tool = m_ToolSystem.activeTool;
                bool bulldoze = tool == m_Bulldoze;
                bool net = tool == m_NetTool;
                if (!bulldoze && !net) { m_HaveCompute = false; return; }

                RRWClock.Update(World);
                if (RRWClock.UpdateIndex - m_LastRun > 1) m_HaveCompute = false;
                m_LastRun = RRWClock.UpdateIndex;
                if (!m_HaveCompute || RRWClock.UpdateIndex - m_LastCompute >= kRecomputeEveryUpdates)
                {
                    long t = RRWPerf.Start();
                    Recompute(s, bulldoze);
                    RRWPerf.Stop(PerfSlot.UI, t);
                    m_LastCompute = RRWClock.UpdateIndex;
                    m_HaveCompute = true;
                }
                if (m_ShowConstruction) AddMouseTooltip(m_Construction);
                if (m_ShowDemolition) AddMouseTooltip(m_Demolition);
                if (m_ShowCancel) AddMouseTooltip(m_Cancel);
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_ShowConstruction = m_ShowDemolition = m_ShowCancel = false;
                m_Guard.Fail(e);
            }
        }

        private void Recompute(RRWSetting s, bool bulldoze)
        {
            m_ShowConstruction = m_ShowDemolition = m_ShowCancel = false;
            var em = EntityManager;
            float buildLen = 0f, demolishLen = 0f;
            long refund = 0;
            bool anyCancel = false;
            Entity buildClass = Entity.Null, demolishClass = Entity.Null;

            var temps = m_TempEdges.ToEntityArray(Allocator.Temp);
            try
            {
                // Originals the road tool is splitting / deleting: Create pieces lying on them are remnants, not new road.
                m_ArcCount = 0;
                if (!bulldoze)
                {
                    for (int i = 0; i < temps.Length; i++)
                    {
                        var tmp = em.GetComponentData<Temp>(temps[i]);
                        if ((tmp.m_Flags & TempFlags.Cancel) != 0 || tmp.m_Original == Entity.Null) continue;
                        if (!em.Exists(tmp.m_Original) || !em.HasComponent<Curve>(tmp.m_Original)) continue;
                        AddArc(em.GetComponentData<Curve>(tmp.m_Original).m_Bezier);
                    }
                }

                for (int i = 0; i < temps.Length; i++)
                {
                    Entity e = temps[i];
                    var tmp = em.GetComponentData<Temp>(e);
                    if ((tmp.m_Flags & TempFlags.Cancel) != 0) continue;
                    if (em.HasComponent<Owner>(e)) continue;       // sub-nets of buildings
                    if (bulldoze)
                    {
                        if ((tmp.m_Flags & TempFlags.Delete) == 0 || (tmp.m_Flags & TempFlags.Hidden) != 0) continue;
                        Entity orig = tmp.m_Original;
                        if (orig == Entity.Null || !em.Exists(orig) || !em.HasComponent<Road>(orig) || em.HasComponent<Owner>(orig)) continue;
                        if (em.HasComponent<RoadWorksSite>(orig))
                        {
                            var site = em.GetComponentData<RoadWorksSite>(orig);
                            if (site.Kind != WorksKind.Construction || site.Has(SiteFlags.Incompatible)) continue;
                            float p = site.Progress;
                            bool instant = p < RRWConst.kInstantCancelProgress && s.InstantCancelUnfinished;
                            refund += WorkTime.CancelRefund(math.max(0, site.m_PaidCost), p, instant);
                            anyCancel = true;
                            continue;
                        }
                        demolishLen += em.GetComponentData<Curve>(orig).m_Length;
                        if (demolishClass == Entity.Null) demolishClass = orig;
                    }
                    else
                    {
                        if ((tmp.m_Flags & TempFlags.Create) == 0 || tmp.m_Original != Entity.Null) continue;
                        if ((tmp.m_Flags & TempFlags.Hidden) != 0) continue;
                        if (!em.HasComponent<Road>(e)) continue;
                        var curve = em.GetComponentData<Curve>(e);
                        if (IsRemnant(curve.m_Bezier)) continue;
                        buildLen += curve.m_Length;
                        if (buildClass == Entity.Null) buildClass = e;
                    }
                }
            }
            finally { temps.Dispose(); }

            if (buildLen > 0.5f && buildClass != Entity.Null)
            {
                float hours = WorkTime.WorkHours(WorksKind.Construction, buildLen, EcsUtil.RoadClass(em, buildClass), s);
                m_Construction.value = LocalizedString.Value(RRWText.Format(RRWText.TipConstruction, UiFormat.Hours(hours)));
                m_ShowConstruction = true;
            }
            if (demolishLen > 0.5f && demolishClass != Entity.Null)
            {
                float hours = WorkTime.WorkHours(WorksKind.Demolition, demolishLen, EcsUtil.RoadClass(em, demolishClass), s);
                string text = s.Policy == ClosurePolicy.Realistic
                    ? RRWText.Format(RRWText.TipDemolition, UiFormat.Hours(hours))
                    : UiFormat.Format(UiFormat.KTipDemolitionOpen, UiFormat.Hours(hours));
                m_Demolition.value = LocalizedString.Value(text);
                m_ShowDemolition = true;
            }
            if (anyCancel)
            {
                m_Cancel.value = LocalizedString.Value(UiFormat.Format(UiFormat.KTipCancelBuild, UiFormat.Money(refund)));
                m_ShowCancel = true;
            }
        }

        private void AddArc(Colossal.Mathematics.Bezier4x3 curve)
        {
            if (m_ArcCount < m_ArcPool.Count) m_ArcPool[m_ArcCount].Rebuild(curve);
            else m_ArcPool.Add(new EdgeArc(curve));
            m_ArcCount++;
        }

        private bool IsRemnant(Colossal.Mathematics.Bezier4x3 piece)
        {
            for (int i = 0; i < m_ArcCount; i++)
                if (CurveMatch.LiesOn(piece, m_ArcPool[i], RRWConst.kSplitMatchTolerance)) return true;
            return false;
        }
    }
}
