using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // One edge handed to SiteFactory (Tools at ApplyTool / ToolUpdate, Director for dev Start* requests and
    // per-edge cancel splits, Persistence for migration). Main thread, pure apart from AllocateProjectId.
    public struct SiteFactoryEdge
    {
        public Entity Edge, StartNode, EndNode;
        public float Length;
        public bool DigEligible;      // EcsUtil.DigEligible (falls back to the prefab path for fresh temps)
        public int PaidCost;          // construction: max(0, Temp.m_Cost); demolition: 0
        public bool Dependants;       // the replaced original / the road has ConnectedBuilding etc.
    }

    public struct SiteFactoryResult
    {
        public Entity Edge;
        public RoadWorksSite Site;
    }

    // THE single place where a new project's sites are computed. Tools, the Director and
    // Persistence all use it so durations, closure, mode and money never diverge.
    public static class SiteFactory
    {
        // Splits `edges` into chains (ChainBuilder) and returns one RoadWorksSite per edge. The caller adds the components.
        // extraFlags: e.g. SiteFlags.Replaced for Replace temps. rc: road class of the first edge (rates).
        public static List<SiteFactoryResult> CreateProjects(IList<SiteFactoryEdge> edges, WorksKind kind, RoadClassInfo rc,
                                                             RRWSetting settings, SiteFlags extraFlags, List<SiteFactoryResult> output = null)
        {
            output = output ?? new List<SiteFactoryResult>();
            if (edges == null || edges.Count == 0 || settings == null) return output;
            var input = new List<ChainEdgeIn>(edges.Count);
            var byEdge = new Dictionary<Entity, SiteFactoryEdge>(edges.Count);
            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                input.Add(new ChainEdgeIn { Edge = e.Edge, StartNode = e.StartNode, EndNode = e.EndNode, Length = e.Length });
                byEdge[e.Edge] = e;
            }
            foreach (var chain in ChainBuilder.Build(input))
            {
                uint id = SiteRegistry.AllocateProjectId();
                float hours = WorkTime.WorkHours(kind, chain.Length, rc, settings);
                uint required = WorkTime.FramesFromHours(hours);
                bool dependants = false;
                foreach (var ce in chain.Edges) dependants |= byEdge[ce.Edge].Dependants;
                var closure = PhasePlan.StartClosure(kind, settings.Policy, dependants);
                SiteFlags flags = extraFlags;
                if (dependants) flags |= SiteFlags.Dependants;
                if (kind != WorksKind.Construction) flags &= ~SiteFlags.Replaced;
                uint done = (uint)math.round(PhasePlan.StartProgress(kind, flags) * required);
                ushort seed = (ushort)(math.hash(new uint2(id, (uint)chain.Edges.Count)) & 0xFFFF);
                foreach (var ce in chain.Edges)
                {
                    var e = byEdge[ce.Edge];
                    var mode = PhasePlan.ChooseMode(e.DigEligible, chain.Length, settings.Quality, settings.TerrainOn, closure);
                    var site = RoadWorksSite.Create(kind, mode, required, id, ce.U0, ce.U1, chain.Length, seed,
                                                    kind == WorksKind.Construction ? math.max(0, e.PaidCost) : 0);
                    site.Flags = flags;
                    site.m_WorkDone = math.min(done, site.m_WorkRequired);
                    output.Add(new SiteFactoryResult { Edge = ce.Edge, Site = site });
                }
                RRWLog.Info("factory: " + kind + " project #" + id + " edges=" + chain.Edges.Count + " len=" + RRWLog.F(chain.Length)
                            + "m hours=" + RRWLog.F(hours) + " closure=" + closure + (dependants ? " dependants" : "")
                            + ((flags & SiteFlags.Replaced) != 0 ? " replaced" : ""));
            }
            return output;
        }
    }
}
