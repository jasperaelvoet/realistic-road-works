using Game.Common;
using Game.Notifications;
using Game.Prefabs;
using Unity.Entities;
using Unity.Jobs;

namespace RealisticRoadWorks.V3.Director
{
    // Notification icons (as in the previous mod version): "Road Maintenance Vehicle" while Closed, "Traffic Bottleneck
    // Notification" while SlowZone (setting) or Closed with an open car half (staged opening, same setting; also while
    // a C4 side swap drains a car half), none when Open or complete.
    // IconCommandSystem de-duplicates by (owner, prefab), so a self-heal re-Add is harmless.
    public partial class WorksDirectorSystem
    {
        private Entity m_IconClosed, m_IconSlow;
        private bool m_IconsResolved;
        private IconCommandBuffer m_IconBuffer;
        private bool m_IconBufferOpen;

        private void ResolveIcons()
        {
            if (m_IconsResolved) return;
            m_IconsResolved = true;
            m_IconClosed = PrefabCatalog.Icon(m_PrefabSystem, PrefabNames.IconClosed);
            m_IconSlow = PrefabCatalog.Icon(m_PrefabSystem, PrefabNames.IconSlowZone);
            RRWLog.Info("director: icons closed=" + (m_IconClosed != Entity.Null) + " slow=" + (m_IconSlow != Entity.Null));
        }

        private IconCommandBuffer IconBuffer()
        {
            if (!m_IconBufferOpen)
            {
                m_IconBuffer = m_IconSystem.CreateCommandBuffer();
                m_IconBufferOpen = true;
            }
            return m_IconBuffer;
        }

        private void CloseIconBuffer()
        {
            if (!m_IconBufferOpen) return;
            m_IconBufferOpen = false;
            try { m_IconSystem.AddCommandBufferWriter(default(JobHandle)); }
            catch (global::System.Exception e) { RRWLog.ErrorOnce("director icon buffer", e); }
        }

        internal void RemoveIcon(Entity owner, Entity prefab)
        {
            if (prefab == Entity.Null || owner == Entity.Null) return;
            try { IconBuffer().Remove(owner, prefab); }
            catch (global::System.Exception e) { RRWLog.ErrorOnce("director icon remove", e); }
        }

        private void StepIcons()
        {
            if (m_Records.Count == 0) return;
            ResolveIcons();
            var em = EntityManager;
            var s = m_S;
            bool on = s.IconsOn;
            for (int i = 0; i < m_Records.Count; i++)
            {
                var rec = m_Records[i];
                Entity e = rec.Edge;
                if (!em.Exists(e) || em.HasComponent<Deleted>(e) || !em.HasComponent<RoadWorksRuntime>(e)) continue;
                var st = rec.GetOrCreate<DirEdgeState>(ModuleSlot.Director);
                var rt = em.GetComponentData<RoadWorksRuntime>(e);
                Entity want = Entity.Null;
                if (on && rt.m_Phase != WorksPhase.Complete)
                {
                    // A Closed edge with an open car half (staged opening) reads as a slow zone when that icon is on.
                    // Also while the halves switch sides (Swap / Drain: the old works half may still be waiting for its
                    // machines while the new works half drains), so the icon never flips slow -> closed -> slow during a swap.
                    if (rt.m_ClosureTarget == ClosureLevel.Closed)
                    {
                        bool halfOpen = (rt.m_OpenLanes & RoadZones.Carriageway) != 0
                                        || ((rt.m_Flags & RuntimeFlags.StageSwitch) != 0 && (rt.m_SoftZones & RoadZones.Carriageway) != 0);
                        want = halfOpen && s.ShowSlowZoneIcon ? m_IconSlow : m_IconClosed;
                    }
                    else if (rt.m_ClosureTarget == ClosureLevel.SlowZone && s.ShowSlowZoneIcon) want = m_IconSlow;
                }
                if (want != st.IconPrefab)
                {
                    var buf = IconBuffer();
                    if (st.IconPrefab != Entity.Null) buf.Remove(e, st.IconPrefab);
                    if (want != Entity.Null) buf.Add(e, want, IconPriority.Info);
                    st.IconPrefab = want;
                    st.IconCheckedUpdate = m_Now;
                    continue;
                }
                if (want == Entity.Null || m_Now - st.IconCheckedUpdate < DirConst.kIconSelfHealUpdates) continue;
                st.IconCheckedUpdate = m_Now;
                if (!HasIcon(e, want)) IconBuffer().Add(e, want, IconPriority.Info);
            }
        }

        private bool HasIcon(Entity owner, Entity prefab)
        {
            var em = EntityManager;
            if (!em.HasBuffer<IconElement>(owner)) return false;
            var buf = em.GetBuffer<IconElement>(owner, true);
            for (int i = 0; i < buf.Length; i++)
            {
                Entity icon = buf[i].m_Icon;
                if (icon == Entity.Null || !em.Exists(icon) || em.HasComponent<Deleted>(icon) || !em.HasComponent<PrefabRef>(icon)) continue;
                if (em.GetComponentData<PrefabRef>(icon).m_Prefab == prefab) return true;
            }
            return false;
        }
    }
}
