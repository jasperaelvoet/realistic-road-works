using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.Dev
{
    public sealed class ListCommand : IDevCommand
    {
        public string Name => "list";
        public string Help => "list [n] - newest road edges with index, entity, length, hidden";
        public void Run(DevContext ctx, string[] a)
        {
            var roads = ctx.Roads();
            int n = a.Length > 0 ? DevContext.I(a[0]) : 8;
            for (int i = 0; i < roads.Count && i < n; i++)
            {
                var e = roads[i];
                var c = ctx.EntityManager.GetComponentData<Curve>(e);
                ctx.Log($"road {i}: entity {e.Index}:{e.Version} len {c.m_Length:F1} mid {MathUtils.Position(c.m_Bezier, 0.5f)} hidden={ctx.EntityManager.HasComponent<Hidden>(e)}");
            }
        }
    }

    public sealed class FocusCommand : IDevCommand
    {
        public string Name => "focus";
        public string Help => "focus <road#> <zoom> <pitch> <yaw> [t=0.5] - gameplay camera to a point on a road";
        public void Run(DevContext ctx, string[] a)
        {
            var e = ctx.Road(a[0]);
            float t = a.Length > 4 ? DevContext.F(a[4]) : 0.5f;
            var p = MathUtils.Position(ctx.EntityManager.GetComponentData<Curve>(e).m_Bezier, t);
            var cam = ctx.System<CameraUpdateSystem>().gamePlayController;
            cam.pivot = new UnityEngine.Vector3(p.x, p.y, p.z);
            cam.zoom = DevContext.F(a[1]);
            cam.rotation = new UnityEngine.Vector3(DevContext.F(a[2]), DevContext.F(a[3]), 0f);
            ctx.Log($"focus road {a[0]} at {p}");
        }
    }

    public sealed class HideCommand : IDevCommand
    {
        public string Name => "hide";
        public string Help => "hide <road#> <0|1> - toggle Game.Tools.Hidden on a road edge (lanes/sub-objects follow)";
        public void Run(DevContext ctx, string[] a)
        {
            var e = ctx.Road(a[0]);
            bool hide = a.Length < 2 || a[1] != "0";
            var em = ctx.EntityManager;
            if (hide && !em.HasComponent<Hidden>(e)) em.AddComponent<Hidden>(e);
            if (!hide && em.HasComponent<Hidden>(e)) em.RemoveComponent<Hidden>(e);
            if (!em.HasComponent<BatchesUpdated>(e)) em.AddComponent<BatchesUpdated>(e);
            ctx.Log($"road {a[0]} hidden={hide}");
        }
    }

    public sealed class SpeedCommand : IDevCommand
    {
        public string Name => "speed";
        public string Help => "speed <0|1|2|3> - simulation speed (0 = pause)";
        public void Run(DevContext ctx, string[] a) { ctx.System<SimulationSystem>().selectedSpeed = DevContext.F(a[0]); ctx.Log($"speed {a[0]}"); }
    }

    public sealed class PauseCommand : IDevCommand
    {
        public string Name => "pause";
        public string Help => "pause - pause simulation";
        public void Run(DevContext ctx, string[] a) { ctx.System<SimulationSystem>().selectedSpeed = 0f; ctx.Log("paused"); }
    }
}
