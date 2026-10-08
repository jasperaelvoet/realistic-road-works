using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;

namespace RealisticRoadWorks.V3
{
    // Deferred upgrades (see DeferredNet): Modification1, before the Director.
    //  * revert: the apply of this frame wrote the tool's state into the road pieces and nodes; the old state goes back
    //    and the tool's state waits in RRWDeferredNet (the game rebuilds composition, lanes and junctions from the old one);
    //  * switch: the works reached their finishing: the target state is written and the component removed;
    //  * drop: the works ended without their target (cancelled): the component goes, the old road stays.
    // Every entity touched gets Updated, with the nodes' connected road pieces, so the junctions are rebuilt.
    [RegisterSystem(SystemUpdatePhase.Modification1, Before = typeof(GenerateAreasSystem), Order = RRWOrder.DeferredNet)]
    public partial class DeferredNetSystem : GameSystemBase
    {
        private EntityQuery m_Deferred;
        private readonly HashSet<Entity> m_Touch = new HashSet<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Deferred = GetEntityQuery(ComponentType.ReadOnly<RRWDeferredNet>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate()
        {
            if (DeferredNet.PendingRevert.Count == 0 && DeferredNet.SwitchRequests.Count == 0 && DeferredNet.DropRequests.Count == 0) return;
            var em = EntityManager;
            m_Touch.Clear();
            if (DeferredNet.PendingRevert.Count > 0) Revert(em);
            if ((DeferredNet.SwitchRequests.Count > 0 || DeferredNet.DropRequests.Count > 0) && !m_Deferred.IsEmptyIgnoreFilter) SwitchOrDrop(em);
            DeferredNet.SwitchRequests.Clear();
            DeferredNet.DropRequests.Clear();
            foreach (var e in m_Touch) MarkUpdated(em, e);
            m_Touch.Clear();
        }

        private void Revert(EntityManager em)
        {
            int n = 0;
            foreach (var s in DeferredNet.PendingRevert)
            {
                var e = s.Entity;
                if (!em.Exists(e) || em.HasComponent<Deleted>(e)) continue;
                uint project = ProjectOf(em, s.SiteEdge);
                if (project == 0) project = ProjectOf(em, e);
                if (project == 0) { RRWLog.Verbose("deferred: e" + e.Index + " has no works project: keeps the tool's state"); continue; }
                var target = s.Target;
                target.m_ProjectId = project;
                // a piece already deferred by an earlier apply keeps its first old state; only the target moves on
                bool already = em.HasComponent<RRWDeferredNet>(e);
                if (!already)
                {
                    if (s.OldPrefab != Entity.Null) em.SetComponentData(e, new PrefabRef { m_Prefab = s.OldPrefab });
                    SetUpgraded(em, e, s.OldHasUpgraded, s.OldUpgraded);
                    SetElevation(em, e, s.OldHasElevation, s.OldElevation);
                    if (s.Kind == DeferredKind.Edge) em.SetComponentData(e, new Curve { m_Bezier = s.OldCurve, m_Length = Colossal.Mathematics.MathUtils.Length(s.OldCurve) });
                    else
                    {
                        var node = em.GetComponentData<Node>(e);
                        node.m_Position = s.OldPosition; node.m_Rotation = s.OldRotation;
                        em.SetComponentData(e, node);
                    }
                    em.AddComponentData(e, target);
                }
                else
                {
                    if (s.Kind == DeferredKind.Edge && target.m_Strips.Length == 0) target.m_Strips = em.GetComponentData<RRWDeferredNet>(e).m_Strips;
                    em.SetComponentData(e, target);
                }
                m_Touch.Add(e);
                n++;
            }
            DeferredNet.PendingRevert.Clear();
            DeferredNet.Reverted += n;
            if (n > 0) RRWLog.Info("deferred: " + n + " road piece(s) / node(s) keep the old road until the works' finishing");
        }

        private void SwitchOrDrop(EntityManager em)
        {
            var ents = m_Deferred.ToEntityArray(Allocator.Temp);
            try
            {
                int sw = 0, dr = 0;
                foreach (var e in ents)
                {
                    var d = em.GetComponentData<RRWDeferredNet>(e);
                    if (DeferredNet.SwitchRequests.Contains(d.m_ProjectId))
                    {
                        Apply(em, e, d);
                        em.RemoveComponent<RRWDeferredNet>(e);
                        m_Touch.Add(e);
                        sw++;
                    }
                    else if (DeferredNet.DropRequests.Contains(d.m_ProjectId))
                    {
                        em.RemoveComponent<RRWDeferredNet>(e);
                        dr++;
                    }
                }
                DeferredNet.Switched += sw;
                DeferredNet.Dropped += dr;
                if (sw > 0) RRWLog.Info("deferred: " + sw + " road piece(s) / node(s) switched to the upgraded road");
                if (dr > 0) RRWLog.Info("deferred: " + dr + " pending upgrade target(s) dropped (works ended without them)");
            }
            finally { ents.Dispose(); }
        }

        // Writes the stored target state into a live entity.
        public static void Apply(EntityManager em, Entity e, in RRWDeferredNet d)
        {
            if (d.m_Prefab != Entity.Null && em.Exists(d.m_Prefab)) em.SetComponentData(e, new PrefabRef { m_Prefab = d.m_Prefab });
            SetUpgraded(em, e, d.m_HasUpgraded, d.m_Upgraded);
            SetElevation(em, e, d.m_HasElevation, d.m_Elevation);
            if (d.Kind == DeferredKind.Edge && em.HasComponent<Curve>(e))
                em.SetComponentData(e, new Curve { m_Bezier = d.m_Curve, m_Length = Colossal.Mathematics.MathUtils.Length(d.m_Curve) });
            else if (d.Kind == DeferredKind.Node && em.HasComponent<Node>(e))
            {
                var node = em.GetComponentData<Node>(e);
                node.m_Position = d.m_Position; node.m_Rotation = d.m_Rotation;
                em.SetComponentData(e, node);
            }
        }

        private static void SetUpgraded(EntityManager em, Entity e, bool has, CompositionFlags flags)
        {
            if (has)
            {
                if (em.HasComponent<Upgraded>(e)) em.SetComponentData(e, new Upgraded { m_Flags = flags });
                else em.AddComponentData(e, new Upgraded { m_Flags = flags });
            }
            else if (em.HasComponent<Upgraded>(e)) em.RemoveComponent<Upgraded>(e);
        }

        private static void SetElevation(EntityManager em, Entity e, bool has, Unity.Mathematics.float2 elev)
        {
            if (has)
            {
                if (em.HasComponent<Elevation>(e)) em.SetComponentData(e, new Elevation { m_Elevation = elev });
                else em.AddComponentData(e, new Elevation { m_Elevation = elev });
            }
            else if (em.HasComponent<Elevation>(e)) em.RemoveComponent<Elevation>(e);
        }

        private static uint ProjectOf(EntityManager em, Entity e) =>
            e != Entity.Null && em.Exists(e) && em.HasComponent<RoadWorksSite>(e) ? em.GetComponentData<RoadWorksSite>(e).m_ProjectId : 0u;

        private static void AddUpdated(EntityManager em, Entity e)
        {
            if (em.Exists(e) && !em.HasComponent<Deleted>(e) && !em.HasComponent<Updated>(e)) em.AddComponent<Updated>(e);
        }

        // Updated on the entity; an edge also marks its nodes and their road pieces, a node its road pieces.
        private static void MarkUpdated(EntityManager em, Entity e)
        {
            if (!em.Exists(e)) return;
            AddUpdated(em, e);
            if (em.HasComponent<Edge>(e))
            {
                var ed = em.GetComponentData<Edge>(e);
                MarkNode(em, ed.m_Start);
                MarkNode(em, ed.m_End);
            }
            else if (em.HasComponent<Node>(e)) MarkNode(em, e);
        }

        private static void MarkNode(EntityManager em, Entity n)
        {
            if (!em.Exists(n)) return;
            AddUpdated(em, n);
            if (!em.HasBuffer<ConnectedEdge>(n)) return;
            // copied first: adding Updated is a structural change that invalidates the buffer
            var ce = em.GetBuffer<ConnectedEdge>(n, true).ToNativeArray(Allocator.Temp);
            for (int i = 0; i < ce.Length; i++) AddUpdated(em, ce[i].m_Edge);
            ce.Dispose();
        }
    }
}
