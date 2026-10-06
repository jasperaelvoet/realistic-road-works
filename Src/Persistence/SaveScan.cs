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

    // Upgrade tails (mode H) of the sites at one moment: what a save writes, or what a load read. Hash = the sum of
    // RoadWorksSite.TailHash over every tail that is written (or was read) - bands, windows and flags, keyed by project and chain
    // position, progress excluded - so a reload of the last save shows the same hash. A running project changes it on purpose
    // (a window start stamps its primitive, a cleared AllAtOnce bit), so only a save and the load of that save compare.
    internal struct TailCensus
    {
        public bool Taken;
        public int Upgrade, Reconstruction;   // tails written / read, by plan kind
        public int Invalid;                   // save: tails that fail validation (written, dropped on load); load: tails dropped
        public int HalfNoPlan;                // save: mode 2 constructions without a plan (written as finished); load: such sites read
        public int HalfOther;                 // mode 2 sites that are not constructions (a cancelled mode H site; reload as mode D)
        public uint Hash;
        public string When;

        public int Tails => Upgrade + Reconstruction;

        public string Text() =>
            "tails=" + Tails + "(upgrade " + Upgrade + " reconstruction " + Reconstruction + ") hash=" + Hash.ToString("X8")
            + " invalid=" + Invalid + " mode2NoPlan=" + HalfNoPlan + " mode2NotConstruction=" + HalfOther;
    }

    internal static class UpgradeTails
    {
        // Process-wide, never reset on load: the census of the last save is compared with the next load.
        public static TailCensus LastSave, Load;
        public static readonly List<Entity> LoadBadTails = new List<Entity>(8);   // edges whose tail the last load dropped (max 8)

        // atLoad: the sites were just read (invalid / no-plan counts come from RoadWorksSite.m_LoadNote); otherwise the census of
        // what a save writes now. invalidOut / noPlanOut (optional, at most 8 each) receive the edges of those cases.
        public static TailCensus Take(EntityQuery sites, bool atLoad, List<Entity> invalidOut = null, List<Entity> noPlanOut = null)
        {
            var c = new TailCensus { Taken = true, When = "update " + RRWClock.UpdateIndex + " sim " + RRWClock.SimFrame };
            invalidOut?.Clear();
            noPlanOut?.Clear();
            if (sites.IsEmptyIgnoreFilter) return c;
            var ents = sites.ToEntityArray(Allocator.Temp);
            var arr = sites.ToComponentDataArray<RoadWorksSite>(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var s = arr[i];
                    bool half = s.Mode == VisualMode.HalfWidth;
                    if (half && s.Kind != WorksKind.Construction) c.HalfOther++;
                    bool invalid, noPlan;
                    if (atLoad)
                    {
                        invalid = s.m_LoadNote == RoadWorksSite.kNoteBadTail;
                        noPlan = s.m_LoadNote == RoadWorksSite.kNoteHalfWidthNoPlan;
                    }
                    else
                    {
                        invalid = s.WritesTail && !s.TailValid();
                        noPlan = s.WritesFinished && s.Plan == PlanKind.None;
                    }
                    if (invalid) { c.Invalid++; if (invalidOut != null && invalidOut.Count < 8) invalidOut.Add(ents[i]); }
                    if (noPlan) { c.HalfNoPlan++; if (noPlanOut != null && noPlanOut.Count < 8) noPlanOut.Add(ents[i]); }
                    // a load keeps only valid tails; a save writes the tail of every mode 2 construction with a plan kind
                    bool kept = atLoad ? s.Plan != PlanKind.None : s.WritesTail && s.TailValid();
                    if (!kept) continue;
                    if (s.Plan == PlanKind.Upgrade) c.Upgrade++; else c.Reconstruction++;
                    unchecked { c.Hash += s.TailHash(); }
                }
            }
            finally
            {
                ents.Dispose();
                arr.Dispose();
            }
            return c;
        }

        // "same" / "DIFFERENT" when the last save of this session is known, else "".
        public static string CompareWithLastSave(in TailCensus now)
        {
            if (!LastSave.Taken) return "";
            bool same = LastSave.Tails == now.Tails && LastSave.Hash == now.Hash;
            return same ? "same as the last save" : "DIFFERENT from the last save (" + LastSave.Text() + "; another city, or the tails changed)";
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
