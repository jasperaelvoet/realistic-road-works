using System;
using Game;
using Game.Common;
using Game.SceneFlow;
using RealisticRoadWorks.Dev;
using Unity.Entities;

namespace RealisticRoadWorks.V3.Props
{
    // PropOwnerSystem (Modification4, order 410, UpdateAt). Port of the effects prototype's late-owner system
    // (verified in game): Owner(node) is added to props created THIS update after SubObjectReferencesSystem
    // (Modification3) ran, so the prop is never registered in the node's SubObject buffer (no deletion when the node is
    // Updated, no Hidden with the node) yet OverrideSystem (Modification5) ignores collisions with the node's edges.
    // Also re-owns props whose owner node died this update (request owner Null = remove the Owner component), so no
    // prop carries a dangling Owner across frames.
    [RegisterSystem(SystemUpdatePhase.Modification4, Order = RRWOrder.PropOwner)]
    public partial class PropOwnerSystem : GameSystemBase
    {
        private readonly RRWGuard m_Guard = new RRWGuard("props owner");
        public static int Applied, Removed, Skipped;

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            PropState.OwnerRequests.Clear();
        }

        protected override void OnUpdate()
        {
            var list = PropState.OwnerRequests;
            if (list.Count == 0) return;
            if (GameManager.instance == null || GameManager.instance.gameMode != GameMode.Game || m_Guard.Faulted)
            {
                list.Clear();
                return;
            }
            long t = RRWPerf.Start();
            try
            {
                var em = EntityManager;
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    if (!EcsUtil.Alive(em, r.Prop)) { Skipped++; continue; }
                    bool has = em.HasComponent<Owner>(r.Prop);
                    if (r.Owner != Entity.Null && PropSystem.ValidOwner(em, r.Owner))
                    {
                        if (has) em.SetComponentData(r.Prop, new Owner(r.Owner));
                        else em.AddComponentData(r.Prop, new Owner(r.Owner));
                        Applied++;
                        continue;
                    }
                    // no live owner: never leave a dangling one
                    if (has && !EcsUtil.Alive(em, em.GetComponentData<Owner>(r.Prop).m_Owner)) { em.RemoveComponent<Owner>(r.Prop); Removed++; }
                    else Skipped++;
                }
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
            }
            finally
            {
                list.Clear();
                RRWPerf.Stop(PerfSlot.Props, t);
            }
        }
    }
}
