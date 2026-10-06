using System.Linq;
using Colossal.Mathematics;
using Game.Net;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.Dev
{
    public sealed class InspectCommand : IDevCommand
    {
        public string Name => "inspect";
        public string Help => "inspect [n=3] [built] - the n edges nearest the camera pivot (built = skip map-native roads) with all their component types";
        public void Run(DevContext ctx, string[] a)
        {
            int n = a.Length > 0 ? DevContext.I(a[0]) : 3;
            var em = ctx.EntityManager;
            var pivot = ctx.System<CameraUpdateSystem>().gamePlayController.pivot;
            var p = new float3(pivot.x, pivot.y, pivot.z);
            var q = em.CreateEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>());
            var arr = q.ToEntityArray(Allocator.Temp);
            bool builtOnly = a.Length > 1 && a[1] == "built";
            var near = arr.ToArray().Where(e => !builtOnly || !em.HasComponent<Game.Common.Native>(e))
                .Select(e => (e, d: MathUtils.Distance(em.GetComponentData<Curve>(e).m_Bezier.xz, p.xz, out _)))
                .OrderBy(x => x.d).Take(n).ToList();
            arr.Dispose();
            ctx.Log($"inspect pivot {p} tracked={RoadTrackerSystem.Created.Count}");
            foreach (var (e, d) in near)
            {
                var types = em.GetComponentTypes(e, Allocator.Temp);
                string s = string.Join(",", types.ToArray().Select(t => t.GetManagedType().Name));
                types.Dispose();
                ctx.Log($"inspect edge {e.Index}:{e.Version} dist {d:F1} len {em.GetComponentData<Curve>(e).m_Length:F1}: {s}");
            }
        }
    }
}
