using System;
using Game;
using Game.Common;
using Game.Objects;
using Game.SceneFlow;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;

namespace RealisticRoadWorks.V3.Props
{
    // HeapFillSystem (ModificationEnd, after QuantityUpdateSystem, order 530). Ported from the effects prototype.
    // QuantityUpdateSystem resets Quantity to the owner's stock (0 for a road node) on every Updated/BatchesUpdated quantity
    // object; right after it we put our heaps back to their target fullness, in the same frame, before batching (no flicker).
    // Not a structural change: SetComponentData only.
    [RegisterSystem(SystemUpdatePhase.ModificationEnd, After = typeof(QuantityUpdateSystem), Order = RRWOrder.HeapFill)]
    public partial class HeapFillSystem : GameSystemBase
    {
        private readonly RRWGuard m_Guard = new RRWGuard("props heap fill");
        private EntityQuery m_Query;
        public static int Writes;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RRWHeapFill>(), ComponentType.ReadWrite<Quantity>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<BatchesUpdated>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            RequireForUpdate(m_Query);
        }

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted) return;
            if (GameManager.instance == null || GameManager.instance.gameMode != GameMode.Game) return;
            long t = RRWPerf.Start();
            NativeArray<Entity> arr = default;
            try
            {
                arr = m_Query.ToEntityArray(Allocator.Temp);
                var em = EntityManager;
                for (int i = 0; i < arr.Length; i++)
                {
                    byte want = em.GetComponentData<RRWHeapFill>(arr[i]).m_Fullness;
                    var q = em.GetComponentData<Quantity>(arr[i]);
                    if (q.m_Fullness == want) continue;
                    q.m_Fullness = want;
                    em.SetComponentData(arr[i], q);
                    Writes++;
                }
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
            }
            finally
            {
                if (arr.IsCreated) arr.Dispose();
                RRWPerf.Stop(PerfSlot.Props, t);
            }
        }
    }
}
