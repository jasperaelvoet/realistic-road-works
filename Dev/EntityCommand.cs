using System;
using System.Text;
using Unity.Collections;
using Unity.Entities;

namespace RealisticRoadWorks.Dev
{
    // ent <index>[:version] - component types of one entity (dev inspection).
    public sealed class EntityCommand : IDevCommand
    {
        public string Name => "ent";
        public string Help => "ent <index>[:version] - every component type of an entity";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            var parts = a[0].Split(':');
            int idx = DevContext.I(parts[0]);
            var all = em.GetAllEntities(Allocator.Temp);
            Entity found = Entity.Null;
            foreach (var e in all) if (e.Index == idx && (parts.Length < 2 || e.Version == DevContext.I(parts[1]))) { found = e; break; }
            all.Dispose();
            if (found == Entity.Null) { ctx.Log("ent " + a[0] + ": none"); return; }
            var types = em.GetComponentTypes(found, Allocator.Temp);
            var sb = new StringBuilder("ent " + found.Index + ":" + found.Version + " ");
            foreach (var t in types) sb.Append(t.GetManagedType().Name).Append(' ');
            types.Dispose();
            if (em.HasComponent<Game.Objects.Transform>(found)) sb.Append("| pos=").Append(em.GetComponentData<Game.Objects.Transform>(found).m_Position);
            ctx.Log(sb.ToString());
        }
    }
}

namespace RealisticRoadWorks.Dev
{
    // entp <prefab name> [n=3] - entities whose PrefabRef is that prefab, with their component types.
    public sealed class EntityByPrefabCommand : IDevCommand
    {
        public string Name => "entp";
        public string Help => "entp <prefab name, _ for spaces> [n=3] - entities of a prefab with their component types and owner";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            string name = a[0].Replace('_', ' ');
            int n = a.Length > 1 ? DevContext.I(a[1]) : 3;
            var ps = ctx.System<Game.Prefabs.PrefabSystem>();
            var q = em.CreateEntityQuery(Unity.Entities.ComponentType.ReadOnly<Game.Prefabs.PrefabRef>());
            var all = q.ToEntityArray(Unity.Collections.Allocator.Temp);
            int shown = 0, total = 0;
            foreach (var e in all)
            {
                var pr = em.GetComponentData<Game.Prefabs.PrefabRef>(e).m_Prefab;
                if (!ps.TryGetPrefab<Game.Prefabs.PrefabBase>(pr, out var pb) || pb.name != name && pb.name != a[0]) continue;
                total++;
                if (shown >= n) continue;
                shown++;
                var types = em.GetComponentTypes(e, Unity.Collections.Allocator.Temp);
                var sb = new System.Text.StringBuilder("entp " + e.Index + ":" + e.Version + " ");
                foreach (var t in types) sb.Append(t.GetManagedType().Name).Append(' ');
                types.Dispose();
                if (em.HasComponent<Game.Common.Owner>(e)) sb.Append("| owner=").Append(em.GetComponentData<Game.Common.Owner>(e).m_Owner.Index);
                if (em.HasComponent<Game.Objects.Transform>(e)) sb.Append(" pos=").Append(em.GetComponentData<Game.Objects.Transform>(e).m_Position);
                if (em.HasComponent<Game.Objects.TrafficLight>(e)) sb.Append(" state=").Append((int)em.GetComponentData<Game.Objects.TrafficLight>(e).m_State);
                ctx.Log(sb.ToString());
            }
            all.Dispose();
            ctx.Log("entp " + name + ": " + total + " entities");
        }
    }
}

namespace RealisticRoadWorks.Dev
{
    // entfix <index> secondary|unsecondary|updated - render experiments on one object
    public sealed class EntityFixCommand : IDevCommand
    {
        public string Name => "entfix";
        public string Help => "entfix <index> secondary|unsecondary|updated - add / remove Game.Objects.Secondary, or mark Updated + BatchesUpdated";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            int idx = DevContext.I(a[0]);
            var all = em.GetAllEntities(Unity.Collections.Allocator.Temp);
            Entity e = Entity.Null;
            foreach (var x in all) if (x.Index == idx) { e = x; break; }
            all.Dispose();
            if (e == Entity.Null) { ctx.Log("entfix: none"); return; }
            switch (a[1])
            {
                case "secondary": if (!em.HasComponent<Game.Objects.Secondary>(e)) em.AddComponent<Game.Objects.Secondary>(e); break;
                case "unsecondary": if (em.HasComponent<Game.Objects.Secondary>(e)) em.RemoveComponent<Game.Objects.Secondary>(e); break;
            }
            if (!em.HasComponent<Game.Common.Updated>(e)) em.AddComponent<Game.Common.Updated>(e);
            if (!em.HasComponent<Game.Common.BatchesUpdated>(e)) em.AddComponent<Game.Common.BatchesUpdated>(e);
            ctx.Log("entfix " + e.Index + " " + a[1] + " done");
        }
    }
}
