using System.Collections.Generic;
using Game.Common;
using Unity.Collections;
using Unity.Entities;

namespace RealisticRoadWorks.V3.Director
{
    // Orphan sweep of derived entities, every kDerivedGcInterval updates:
    // - machines and machine parts: orphaned when their project is gone, unless post-site; a part whose owner chain
    //   still reaches a live puppet root is left to that root (vanilla deletes sub-objects with their owner);
    // - props and areas: orphaned when m_Site is not a registry edge, unless m_Site == Null (scars);
    // - an orphan is Deleted only on the second consecutive sweep (the owning module's own diff deletes first).
    public partial class WorksDirectorSystem
    {
        private HashSet<Entity> m_GcSuspects = new HashSet<Entity>();
        private HashSet<Entity> m_GcNext = new HashSet<Entity>();
        private uint m_LastGcUpdate;
        public static int GcDeletedTotal;

        private void StepGc()
        {
            if (!SiteRegistry.Loaded) return;
            if (m_Now - m_LastGcUpdate < (uint)RRWConst.kDerivedGcInterval) return;
            m_LastGcUpdate = m_Now;
            if (m_DerivedQuery.IsEmptyIgnoreFilter) { m_GcSuspects.Clear(); return; }
            var em = EntityManager;
            m_GcNext.Clear();
            int deleted = 0;
            var arr = m_DerivedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    Entity e = arr[i];
                    if (!IsOrphan(e)) continue;
                    if (m_GcSuspects.Contains(e))
                    {
                        EcsUtil.MarkDeleted(em, e);
                        deleted++;
                    }
                    else m_GcNext.Add(e);
                }
            }
            finally { arr.Dispose(); }
            var t = m_GcSuspects;
            m_GcSuspects = m_GcNext;
            m_GcNext = t;
            m_GcNext.Clear();
            if (deleted > 0)
            {
                GcDeletedTotal += deleted;
                RRWLog.Info("director: derived gc deleted " + deleted + " orphan(s) (" + m_GcSuspects.Count + " new suspects)");
            }
        }

        private bool IsOrphan(Entity e)
        {
            var em = EntityManager;
            var d = em.GetComponentData<RRWDerived>(e);
            var group = (DerivedGroup)d.m_Group;
            if (group == DerivedGroup.PostSite || group == DerivedGroup.Scar || group == DerivedGroup.Unknown) return false;
            bool machine = group == DerivedGroup.Machine || group == DerivedGroup.MachinePart || em.HasComponent<RRWMachine>(e);
            if (machine)
            {
                if (group == DerivedGroup.MachinePart && HasLiveDerivedRoot(e)) return false;
                uint pid = d.m_ProjectId;
                if (pid == 0 && em.HasComponent<RRWMachine>(e)) pid = em.GetComponentData<RRWMachine>(e).m_ProjectId;
                return !SiteRegistry.Projects.ContainsKey(pid);
            }
            if (d.m_Site == Entity.Null) return false;
            return !SiteRegistry.Edges.ContainsKey(d.m_Site);
        }

        // Owner chain (<= 4 levels) reaches a live, non-deleted puppet root or derived entity.
        private bool HasLiveDerivedRoot(Entity e)
        {
            var em = EntityManager;
            Entity cur = e;
            for (int k = 0; k < 4; k++)
            {
                if (!em.HasComponent<Owner>(cur)) return false;
                cur = em.GetComponentData<Owner>(cur).m_Owner;
                if (cur == Entity.Null || !em.Exists(cur) || em.HasComponent<Deleted>(cur)) return false;
                if (em.HasComponent<RRWMachine>(cur)) return true;
                if (em.HasComponent<RRWDerived>(cur) && ((DerivedGroup)em.GetComponentData<RRWDerived>(cur).m_Group == DerivedGroup.Machine
                    || (DerivedGroup)em.GetComponentData<RRWDerived>(cur).m_Group == DerivedGroup.PostSite)) return true;
            }
            return false;
        }
    }
}
