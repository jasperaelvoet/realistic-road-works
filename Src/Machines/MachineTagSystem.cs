using System;
using Game;
using Game.Common;
using Game.Routes;
using Game.SceneFlow;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;

// MachineTagSystem (order 420, Modification4 UpdateAt, EVERY frame): LivePath + RRWDerived on every Created
// descendant of a puppet. Vanilla SubObjectSystem (Mod2B) creates the truck piles / bed props of a puppet whenever it is
// created or Updated, long after any fixed tagging window (a pattern verified in game): filtering on Created keeps
// this cheap. Untagged descendants would be written to saves.
namespace RealisticRoadWorks.V3.Machines
{
    [RegisterSystem(SystemUpdatePhase.Modification4, Order = RRWOrder.MachineTag)]
    public partial class MachineTagSystem : GameSystemBase
    {
        private EntityQuery m_Created;
        private readonly RRWGuard m_Guard = new RRWGuard("machines tag");
        public static int Tagged;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Created = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Owner>() },
                None = new[] { ComponentType.ReadOnly<LivePath>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnUpdate()
        {
            if (GameManager.instance == null || GameManager.instance.gameMode != GameMode.Game) return;
            if (MachineRegistry.All.Count == 0 || m_Guard.Faulted) return;
            if (m_Created.IsEmptyIgnoreFilter) return;
            long t0 = RRWPerf.Start();
            MxPerf.Begin(MxT.Tag);
            try
            {
                var em = EntityManager;
                var arr = m_Created.ToEntityArray(Allocator.Temp);
                MxPerf.Count(MxC.TagScanned, arr.Length);
                try
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        Entity e = arr[i];
                        Entity root = e;
                        int depth = 0;
                        while (depth < 4 && em.HasComponent<Owner>(root))
                        {
                            Entity o = em.GetComponentData<Owner>(root).m_Owner;
                            if (o == Entity.Null || !em.Exists(o)) break;
                            root = o;
                            depth++;
                            if (em.HasComponent<RRWMachine>(root)) break;
                        }
                        if (root == e || !em.HasComponent<RRWMachine>(root)) continue;
                        var m = em.GetComponentData<RRWMachine>(root);
                        var group = DerivedGroup.MachinePart;
                        if (em.HasComponent<RRWDerived>(root) && em.GetComponentData<RRWDerived>(root).m_Group == (byte)DerivedGroup.PostSite) group = DerivedGroup.PostSite;
                        EcsUtil.TagDerived(em, e, Entity.Null, m.m_ProjectId, group);
                        Tagged++;
                        MxPerf.Count(MxC.TagTagged);
                    }
                }
                finally { arr.Dispose(); }
                m_Guard.Ok();
            }
            catch (Exception e) { m_Guard.Fail(e); }
            MxPerf.End(MxT.Tag);
            RRWPerf.Stop(PerfSlot.Machines, t0);
        }
    }
}
