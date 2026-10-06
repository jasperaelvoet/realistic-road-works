using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace RealisticRoadWorks.Dev
{
    // Remembers road edges in the order they were created this session, so dev commands can address
    // "the road I just drew" (entity indices are recycled, so index order says nothing about age).
    [RegisterSystem(SystemUpdatePhase.ModificationEnd)]
    public partial class RoadTrackerSystem : GameSystemBase
    {
        public static readonly List<Entity> Created = new List<Entity>();
        private EntityQuery m_Query;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Road>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Owner>() },
            });
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            Created.Clear();
        }

        protected override void OnUpdate()
        {
            if (m_Query.IsEmptyIgnoreFilter) return;
            var arr = m_Query.ToEntityArray(Allocator.Temp);
            foreach (var e in arr)
            {
                if (Created.Contains(e)) continue;
                Created.Add(e);
                Mod.Log.Info($"dev tracker: new road {e.Index}:{e.Version} (#{Created.Count})");
            }
            arr.Dispose();
        }
    }
}
