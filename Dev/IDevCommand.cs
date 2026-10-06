using System.Collections.Generic;
using System.Globalization;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace RealisticRoadWorks.Dev
{
    // A dev/experiment command, invoked from ModsData/RealisticRoadWorks/cmd.txt ("name arg1 arg2").
    // Implementations are discovered by reflection (public parameterless constructor). Runs on the main thread in MainLoop.
    public interface IDevCommand
    {
        string Name { get; }
        string Help { get; }
        void Run(DevContext ctx, string[] args);
    }

    public sealed class DevContext
    {
        public World World;
        public EntityManager EntityManager;
        public T System<T>() where T : ComponentSystemBase => World.GetOrCreateSystemManaged<T>();
        public void Log(string message) => Mod.Log.Info("dev " + message);

        // Road edges (Edge + Road, not Temp/Deleted, no Owner): roads created this session first (newest first), then the rest. Commands address them as "edge index".
        public List<Entity> Roads()
        {
            var em = EntityManager;
            var list = new List<Entity>();
            // Roads created this session first (newest first) - tracked by RoadTrackerSystem.
            for (int i = RoadTrackerSystem.Created.Count - 1; i >= 0; i--)
            {
                var e = RoadTrackerSystem.Created[i];
                if (em.Exists(e) && em.HasComponent<Edge>(e) && !em.HasComponent<Deleted>(e) && !list.Contains(e)) list.Add(e);
            }
            // Then every other road edge (highest entity index first).
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Road>(), ComponentType.ReadOnly<Curve>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Owner>() },
            });
            var arr = q.ToEntityArray(Allocator.Temp);
            var rest = new List<Entity>();
            foreach (var e in arr) if (!list.Contains(e)) rest.Add(e);
            arr.Dispose();
            rest.Sort((x, y) => y.Index.CompareTo(x.Index));
            list.AddRange(rest);
            return list;
        }

        public Entity Road(string indexArg)
        {
            var roads = Roads();
            int i = int.Parse(indexArg, CultureInfo.InvariantCulture);
            if (roads.Count == 0) throw new global::System.Exception("no roads");
            return roads[global::System.Math.Max(0, global::System.Math.Min(i, roads.Count - 1))];
        }

        public static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        public static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    }
}
