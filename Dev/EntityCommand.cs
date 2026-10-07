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
