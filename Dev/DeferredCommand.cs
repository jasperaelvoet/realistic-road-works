using System.Text;
using Game.Net;
using RealisticRoadWorks.V3;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.Dev
{
    // rrw.defer - deferred upgrades: per road piece, where the live (old) road lies in the works frame (target curve).
    public sealed class DeferredCommand : IDevCommand
    {
        public string Name => "rrw.defer";
        public string Help => "rrw.defer - deferred road pieces: live curve vs target, old lanes and edge outline in the target frame, works bands";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            var q = em.CreateEntityQuery(ComponentType.ReadOnly<RRWDeferredNet>());
            var arr = q.ToEntityArray(Allocator.Temp);
            ctx.Log("rrw defer: " + arr.Length + " deferred entities, reverted=" + DeferredNet.Reverted + " switched=" + DeferredNet.Switched + " dropped=" + DeferredNet.Dropped);
            var probes = new System.Collections.Generic.List<LaneProbe>();
            foreach (var e in arr)
            {
                var d = em.GetComponentData<RRWDeferredNet>(e);
                var sb = new StringBuilder("rrw defer e" + e.Index + " p" + d.m_ProjectId + " " + d.Kind);
                if (d.Kind == DeferredKind.Edge && em.HasComponent<Curve>(e))
                {
                    var arc = new EdgeArc(d.m_Curve);
                    var live = em.GetComponentData<Curve>(e).m_Bezier;
                    float s = arc.Length * 0.5f;
                    float3 mid = Colossal.Mathematics.MathUtils.Position(live, 0.5f);
                    float3 c = arc.Offset(s, 0f), r = arc.Offset(s, 1f) - c;
                    float lat = math.dot((mid - c).xz, math.normalizesafe(r.xz));
                    sb.Append(" liveMidLateral=").Append(lat.ToString("0.00"));
                    if (em.HasComponent<EdgeGeometry>(e))
                    {
                        var g = em.GetComponentData<EdgeGeometry>(e);
                        float3 lm = Colossal.Mathematics.MathUtils.Position(g.m_Start.m_Left, 0.5f), rm = Colossal.Mathematics.MathUtils.Position(g.m_Start.m_Right, 0.5f);
                        sb.Append(" outline=[").Append(Lat(arc, lm).ToString("0.00")).Append(',').Append(Lat(arc, rm).ToString("0.00")).Append(']');
                    }
                    EcsUtil.ProbeLanes(em, e, arc, probes, null);
                    sb.Append(" lanes:");
                    foreach (var p in probes) sb.Append(' ').Append(p.Centre.ToString("0.0")).Append(p.Dir > 0 ? "+" : p.Dir < 0 ? "-" : "=");
                    sb.Append(" targetOuter=[").Append(d.m_OuterL.ToString("0.0")).Append(',').Append(d.m_OuterR.ToString("0.0")).Append(']');
                    if (em.HasComponent<RoadWorksSite>(e))
                    {
                        var site = em.GetComponentData<RoadWorksSite>(e);
                        sb.Append(" bands:");
                        for (int i = 0; i < site.m_BandCount; i++) { var b = site.Band(i); sb.Append(" [").Append(b.Lo.ToString("0.0")).Append(",").Append((b.Lo + b.Width).ToString("0.0")).Append("]"); }
                    }
                }
                ctx.Log(sb.ToString());
            }
            arr.Dispose();
        }

        private static float Lat(EdgeArc arc, float3 p)
        {
            float s = math.clamp(arc.Project(p), 0f, arc.Length);
            float3 c = arc.Offset(s, 0f), r = arc.Offset(s, 1f) - c;
            return math.dot((p - c).xz, math.normalizesafe(r.xz));
        }
    }
}

namespace RealisticRoadWorks.Dev
{
    // rrw.roads - the road list the dev commands index (road#): prefab, length, upgraded flags.
    public sealed class RoadsCommand : IDevCommand
    {
        public string Name => "rrw.roads";
        public string Help => "rrw.roads - every road (road# for focus / w2s / uwapply): prefab and length";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            var ps = ctx.System<Game.Prefabs.PrefabSystem>();
            var roads = ctx.Roads();
            for (int i = 0; i < roads.Count; i++)
            {
                var e = roads[i];
                var pr = em.GetComponentData<Game.Prefabs.PrefabRef>(e).m_Prefab;
                string n = ps.TryGetPrefab<Game.Prefabs.PrefabBase>(pr, out var pb) ? pb.name : "?";
                float len = em.GetComponentData<Curve>(e).m_Length;
                ctx.Log("rrw road " + i + " e" + e.Index + " " + n + " len=" + len.ToString("0.0") + (em.HasComponent<RoadWorksSite>(e) ? " works" : "")
                        + (em.HasComponent<RRWDeferredNet>(e) ? " deferred" : ""));
            }
        }
    }
}
