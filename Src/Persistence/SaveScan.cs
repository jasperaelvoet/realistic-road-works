using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using IconComp = Game.Notifications.Icon;
using LayoutElement = Game.Vehicles.LayoutElement;
using ObjectComp = Game.Objects.Object;
using SubObject = Game.Objects.SubObject;

namespace RealisticRoadWorks.V3.Persistence
{
    // Counts of one save scan (the SaveSanitize cases a-c).
    internal struct SaveScanCounts
    {
        public int Icons;        // (a) our notification icons on works edges
        public int Derived;      // (b) RRWDerived without LivePath (should be 0)
        public int Descendants;  // (c) puppet / prop descendants without LivePath (should be 0)
        public int Total => Icons + Derived + Descendants;
    }

    // Finds every entity that must not reach a save: our icons and anything derived from a site that lacks LivePath
    // (LivePath keeps an entity out of the SerializerSystem query, verified in game; so does Temp). The scan is
    // shared by SaveSanitizeSystem, the rrw.check hook and the dev dry run. Main thread.
    internal sealed class SaveScan
    {
        private readonly EntityManager m_EM;
        private readonly EntityQuery m_Icons, m_Derived, m_Roots, m_Owned;
        private readonly HashSet<Entity> m_Seen = new HashSet<Entity>();
        private readonly HashSet<Entity> m_MachineRoots = new HashSet<Entity>();
        private readonly List<Entity> m_Stack = new List<Entity>();
        private const int kMaxDepth = 8;

        public SaveScan(EntityManager em, EntityQuery icons, EntityQuery derived, EntityQuery roots, EntityQuery owned)
        {
            m_EM = em;
            m_Icons = icons;
            m_Derived = derived;
            m_Roots = roots;
            m_Owned = owned;
        }

        public static EntityQueryDesc IconsDesc() => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<IconComp>(), ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<PrefabRef>() },
            None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
        };

        public static EntityQueryDesc DerivedDesc() => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<RRWDerived>() },
            None = new[] { ComponentType.ReadOnly<LivePath>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
        };

        public static EntityQueryDesc RootsDesc() => new EntityQueryDesc
        {
            Any = new[] { ComponentType.ReadOnly<RRWMachine>(), ComponentType.ReadOnly<RRWDerived>() },
            None = new[] { ComponentType.ReadOnly<Deleted>() },
        };

        // Owned objects (puppet piles etc.) that are not excluded from saves yet.
        public static EntityQueryDesc OwnedDesc() => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<ObjectComp>() },
            None = new[] { ComponentType.ReadOnly<LivePath>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
        };

        // Collects into `output` (when not null) and returns the counts. iconPrefabs: our notification icon prefabs.
        public SaveScanCounts Run(List<Entity> output, Entity iconClosed, Entity iconSlow, bool includeIcons)
        {
            var c = new SaveScanCounts();
            m_Seen.Clear();
            var em = m_EM;

            // (a) our icons: Owner = works edge, prefab = one of our icon prefabs
            if (includeIcons && !m_Icons.IsEmptyIgnoreFilter && (iconClosed != Entity.Null || iconSlow != Entity.Null))
            {
                var icons = m_Icons.ToEntityArray(Allocator.Temp);
                var owners = m_Icons.ToComponentDataArray<Owner>(Allocator.Temp);
                var prefabs = m_Icons.ToComponentDataArray<PrefabRef>(Allocator.Temp);
                for (int i = 0; i < icons.Length; i++)
                {
                    Entity pf = prefabs[i].m_Prefab;
                    if (pf == Entity.Null || (pf != iconClosed && pf != iconSlow)) continue;
                    Entity owner = owners[i].m_Owner;
                    if (!SiteRegistry.Edges.ContainsKey(owner) && !(em.Exists(owner) && em.HasComponent<RoadWorksSite>(owner))) continue;
                    if (Add(icons[i], output)) c.Icons++;
                }
                icons.Dispose();
                owners.Dispose();
                prefabs.Dispose();
            }

            // (b) derived entities without LivePath
            if (!m_Derived.IsEmptyIgnoreFilter)
            {
                var arr = m_Derived.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < arr.Length; i++) if (Add(arr[i], output)) c.Derived++;
                arr.Dispose();
            }

            // (c) descendants of puppet roots and derived props: SubObject / LayoutElement trees, then Owner chains
            if (!m_Roots.IsEmptyIgnoreFilter)
            {
                m_MachineRoots.Clear();
                var roots = m_Roots.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < roots.Length; i++)
                {
                    Entity r = roots[i];
                    if (em.HasComponent<RRWMachine>(r)) m_MachineRoots.Add(r);
                    c.Descendants += WalkChildren(r, output);
                }
                roots.Dispose();

                if (m_MachineRoots.Count > 0 && !m_Owned.IsEmptyIgnoreFilter)
                {
                    var owned = m_Owned.ToEntityArray(Allocator.Temp);
                    var owners = m_Owned.ToComponentDataArray<Owner>(Allocator.Temp);
                    for (int i = 0; i < owned.Length; i++)
                    {
                        if (owned[i] == Entity.Null || m_Seen.Contains(owned[i])) continue;
                        if (OwnedByMachine(owners[i].m_Owner) && Add(owned[i], output)) c.Descendants++;
                    }
                    owned.Dispose();
                    owners.Dispose();
                }
            }
            return c;
        }

        private int WalkChildren(Entity root, List<Entity> output)
        {
            var em = m_EM;
            int n = 0;
            m_Stack.Clear();
            m_Stack.Add(root);
            int guard = 0;
            while (m_Stack.Count > 0 && guard++ < 4096)
            {
                Entity e = m_Stack[m_Stack.Count - 1];
                m_Stack.RemoveAt(m_Stack.Count - 1);
                if (!em.Exists(e)) continue;
                if (em.HasBuffer<SubObject>(e))
                {
                    var buf = em.GetBuffer<SubObject>(e, true);
                    for (int i = 0; i < buf.Length; i++) Visit(buf[i].m_SubObject, root, output, ref n);
                }
                if (em.HasBuffer<LayoutElement>(e))
                {
                    var buf = em.GetBuffer<LayoutElement>(e, true);
                    for (int i = 0; i < buf.Length; i++) Visit(buf[i].m_Vehicle, root, output, ref n);
                }
            }
            return n;
        }

        private void Visit(Entity child, Entity root, List<Entity> output, ref int n)
        {
            if (child == Entity.Null || child == root || !m_EM.Exists(child)) return;
            if (!m_Seen.Add(child)) return;
            m_Stack.Add(child);
            // a child that is itself a derived root carries its own LivePath (case b covers it if not)
            if (m_EM.HasComponent<LivePath>(child) || m_EM.HasComponent<Temp>(child) || m_EM.HasComponent<Deleted>(child)) return;
            if (output != null) output.Add(child);
            n++;
        }

        private bool OwnedByMachine(Entity owner)
        {
            var em = m_EM;
            for (int d = 0; d < kMaxDepth && owner != Entity.Null; d++)
            {
                if (m_MachineRoots.Contains(owner)) return true;
                if (!em.Exists(owner) || !em.HasComponent<Owner>(owner)) return false;
                owner = em.GetComponentData<Owner>(owner).m_Owner;
            }
            return false;
        }

        private bool Add(Entity e, List<Entity> output)
        {
            if (e == Entity.Null || !m_Seen.Add(e)) return false;
            if (output != null) output.Add(e);
            return true;
        }
    }

    // Entities SaveSanitize temporarily marked Temp; SaveRestore (or the Mod1 watchdog) removes Temp again.
    internal static class SaveGuard
    {
        public static readonly List<Entity> Pending = new List<Entity>();
        public static int PendingFrame = -1;   // UnityEngine.Time.frameCount of the sanitize

        public static int Restore(EntityManager em)
        {
            int n = 0;
            for (int i = 0; i < Pending.Count; i++)
            {
                Entity e = Pending[i];
                if (!em.Exists(e) || !em.HasComponent<Temp>(e)) continue;
                em.RemoveComponent<Temp>(e);
                n++;
            }
            Pending.Clear();
            PendingFrame = -1;
            return n;
        }
    }
}
