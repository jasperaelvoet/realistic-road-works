#if DEVTOOLS
using System.Collections.Generic;
using System.Globalization;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;
using NetNode = Game.Net.Node;
using ObjTransform = Game.Objects.Transform;

// rrw.uw.near: what the mod still has around a road end or a point (props, surface decals, scars, machines, dust puffs, lane
// blockers), to trace something left behind after the works to the module that owns it.
namespace RealisticRoadWorks.V3.DevCmds
{
    public sealed class UwNearCommand : IDevCommand
    {
        public string Name => "rrw.uw.near";
        public string Help => "rrw.uw.near <road index|e<entity index>|x z> [r=12] - every entity of the mod within r metres of the road's end nodes (or of the "
                              + "point x z): kind (object / decal), group, project, its works edge (still a works edge or not), prefab, distance. "
                              + "Groups: Area = surface decal of running works, Scar = curing / scar decal, PropStatic / PropWork = props, "
                              + "Machine / MachinePart / PostSite = machines, DustPuff, LaneBlocker";

        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            var pos = DevSites.Positional(a);
            float r = math.max(0.5f, DevSites.OptF(a, "r", 12f));
            var points = new List<float3>();
            var names = new List<string>();
            if (pos.Count >= 2 && DevSites.TryFloat(pos[0], out float x) && DevSites.TryFloat(pos[1], out float z))
            {
                points.Add(new float3(x, 0f, z));
                names.Add("point (" + DevSites.F1(x) + "," + DevSites.F1(z) + ")");
            }
            else if (pos.Count == 1)
            {
                Entity road = FindRoad(ctx, pos[0]);
                if (road == Entity.Null || !em.HasComponent<Edge>(road)) { DevSites.Out(ctx, "uw near: no road '" + pos[0] + "'"); return; }
                var ed = em.GetComponentData<Edge>(road);
                foreach (var n in new[] { ed.m_Start, ed.m_End })
                {
                    if (!em.Exists(n) || !em.HasComponent<NetNode>(n)) continue;
                    points.Add(em.GetComponentData<NetNode>(n).m_Position);
                    int degree = em.HasBuffer<ConnectedEdge>(n) ? em.GetBuffer<ConnectedEdge>(n, true).Length : 0;
                    names.Add("node e" + n.Index + (degree <= 1 ? " (dead end)" : " (" + degree + " roads)"));
                }
                DevSites.Out(ctx, "uw near road e" + road.Index + (SiteRegistry.TryGetEdge(road, out var rec) ? " (works edge of project #" + rec.ProjectId + ")" : " (no works on it)"));
            }
            if (points.Count == 0) { DevSites.Out(ctx, "uw near: give a road index, e<entity index> or a point x z"); return; }

            var ps = ctx.System<PrefabSystem>();
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RRWDerived>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            var arr = q.ToEntityArray(Allocator.Temp);
            var counts = new Dictionary<DerivedGroup, int>();
            int shown = 0;
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    Entity e = arr[i];
                    bool area = em.HasBuffer<AreaNode>(e);
                    if (!Distance(em, e, area, points, out float d, out int at) || d > r) continue;
                    var tag = em.GetComponentData<RRWDerived>(e);
                    var g = (DerivedGroup)tag.m_Group;
                    counts[g] = counts.TryGetValue(g, out int c) ? c + 1 : 1;
                    if (shown++ >= 40) continue;
                    string site = tag.m_Site == Entity.Null ? "none"
                                : "e" + tag.m_Site.Index + (SiteRegistry.Edges.ContainsKey(tag.m_Site) ? " (works edge)" : EcsUtil.Alive(em, tag.m_Site) ? " (not a works edge)" : " (gone)");
                    DevSites.Out(ctx, "uw near e" + e.Index + " " + (area ? "decal" : "object") + " group=" + g + " project=#" + tag.m_ProjectId
                                      + " site=" + site + " prefab=" + EcsUtil.PrefabName(ps, em, e) + " dist=" + DevSites.F1(d) + " m from " + names[at]
                                      + (em.HasComponent<Game.Tools.Hidden>(e) ? " hidden" : "") + (em.HasComponent<Owner>(e) ? " owned" : ""));
                }
            }
            finally { arr.Dispose(); }
            var parts = new List<string>();
            foreach (var kv in counts) parts.Add(kv.Key + "=" + kv.Value);
            DevSites.Out(ctx, "uw near: " + shown + " entit" + (shown == 1 ? "y" : "ies") + " of the mod within " + DevSites.F1(r) + " m of " + string.Join(", ", names)
                              + (parts.Count > 0 ? " [" + string.Join(" ", parts) + "]" : " (nothing: what is there is not ours)"));
        }

        // A road by its list index (the order of every road command) or by "e<entity index>".
        private static Entity FindRoad(DevContext ctx, string arg)
        {
            var roads = ctx.Roads();
            if (arg.Length > 1 && (arg[0] == 'e' || arg[0] == 'E') && int.TryParse(arg.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
            {
                foreach (var e in roads) if (e.Index == idx) return e;
                return Entity.Null;
            }
            if (!DevSites.TryInt(arg, out int k) || roads.Count == 0) return Entity.Null;
            return roads[math.clamp(k, 0, roads.Count - 1)];
        }

        // Horizontal distance of an object (its position) or a decal (its nearest corner or its centre) to the nearest point.
        private static bool Distance(EntityManager em, Entity e, bool area, List<float3> points, out float best, out int at)
        {
            best = float.MaxValue;
            at = 0;
            if (area)
            {
                var buf = em.GetBuffer<AreaNode>(e, true);
                if (buf.Length == 0) return false;
                float2 sum = float2.zero;
                for (int k = 0; k < buf.Length; k++)
                {
                    float2 p = buf[k].m_Position.xz;
                    sum += p;
                    Near(p, points, ref best, ref at);
                }
                Near(sum / buf.Length, points, ref best, ref at);
                return true;
            }
            if (!em.HasComponent<ObjTransform>(e)) return false;
            Near(em.GetComponentData<ObjTransform>(e).m_Position.xz, points, ref best, ref at);
            return true;
        }

        private static void Near(float2 p, List<float3> points, ref float best, ref int at)
        {
            for (int i = 0; i < points.Count; i++)
            {
                float d = math.distance(p, points[i].xz);
                if (d < best) { best = d; at = i; }
            }
        }
    }
}
#endif
