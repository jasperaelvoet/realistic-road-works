using System;
using Game;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

// SurfaceTagSystem (Modification4, order 400). Port of the area tagging prototype (verified in game): every area
// created this frame whose prefab is one of our clones gets LivePath (never saved) + RRWDerived(site, project, group)
// and is bound to the pending CreationDefinition request by its first node (XZ < 0.6 m, same prefab).
// Unmatched RRW areas are orphans and are deleted.
namespace RealisticRoadWorks.V3.Surfaces
{
    [RegisterSystem(SystemUpdatePhase.Modification4, Order = RRWOrder.SurfaceTag)]
    public partial class SurfaceTagSystem : GameSystemBase
    {
        const float kBindDistance = 0.6f;

        private EntityQuery m_Created;
        private readonly RRWGuard m_Guard = new RRWGuard("surfaces tag");

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Created = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Area>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Created>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Guard.Reset();
            SurfaceState.TagFaulted = false;
        }

        protected override void OnUpdate()
        {
            if (SurfaceState.CloneEntities.Count == 0 || m_Created.IsEmptyIgnoreFilter) return;
            var gm = Game.SceneFlow.GameManager.instance;
            if (gm == null || gm.gameMode != GameMode.Game) return;
            if (m_Guard.Faulted)
            {
                // Never leave an untagged RRW area behind (it would be saved); the area system stops spawning.
                SurfaceState.TagFaulted = true;
                TagOnlyFallback();
                return;
            }
            long t0 = RRWPerf.Start();
            try
            {
                RRWClock.Update(World);
                TagCreated();
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
                if (m_Guard.Faulted) SurfaceState.TagFaulted = true;
                try { TagOnlyFallback(); } catch { }
            }
            finally { RRWPerf.Stop(PerfSlot.Surfaces, t0); }
        }

        private void TagCreated()
        {
            var em = EntityManager;
            uint now = RRWClock.UpdateIndex;
            var arr = m_Created.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var e = arr[i];
                    var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (!SurfaceState.IsOurPrefab(prefab)) continue;
                    if (!em.HasComponent<LivePath>(e)) em.AddComponent<LivePath>(e);
                    float2 first = float2.zero;
                    if (em.HasBuffer<AreaNode>(e))
                    {
                        var nodes = em.GetBuffer<AreaNode>(e, true);
                        if (nodes.Length > 0) first = nodes[0].m_Position.xz;
                    }
                    TrackedArea match = null;
                    float best = kBindDistance;
                    var pending = SurfaceState.Pending;
                    for (int k = 0; k < pending.Count; k++)
                    {
                        var t = pending[k];
                        if (t.Area != Entity.Null || t.Def == Entity.Null || t.Prefab != prefab) continue;
                        float d = math.distance(t.FirstXZ, first);
                        if (d < best) { best = d; match = t; }
                    }
                    if (match != null)
                    {
                        match.Area = e;
                        match.Def = Entity.Null;
                        match.BoundUpdate = now;
                        pending.Remove(match);
                        EcsUtil.TagDerived(em, e, match.Site, match.ProjectId, match.Group);
                        SurfaceState.Bound++;
                    }
                    else
                    {
                        EcsUtil.TagDerived(em, e, Entity.Null, 0u, DerivedGroup.Area);
                        EcsUtil.MarkDeleted(em, e);
                        SurfaceState.Unmatched++;
                        RRWLog.Verbose("surfaces: unmatched RRW area " + RRWLog.E(e) + " (" + SurfaceState.NameOf(prefab) + ") deleted");
                    }
                }
            }
            finally { arr.Dispose(); }
        }

        private void TagOnlyFallback()
        {
            var em = EntityManager;
            var arr = m_Created.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var e = arr[i];
                    if (!SurfaceState.IsOurPrefab(em.GetComponentData<PrefabRef>(e).m_Prefab)) continue;
                    if (!em.HasComponent<LivePath>(e)) em.AddComponent<LivePath>(e);
                    EcsUtil.MarkDeleted(em, e);
                }
            }
            finally { arr.Dispose(); }
        }
    }
}
