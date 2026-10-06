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
        // The entity the site goes on (SiteFactoryResult.Edge). Entity.Null (default) = Edge. Tools set it when the chain is built
        // from Temp edges but the site belongs on the kept original (Modify / Upgrade temps: t.m_Original).
        public Entity Target;
        // ---- upgrade works only (CreateUpgradeProjects)
        public bool Buildings;        // the edge has ConnectedBuilding (lane drops and parallel build windows need none)
        public bool KeepsLanes;       // on the NEW layout every travel direction keeps a car lane outside the edge's build / rebuild
                                      // bands (UpgradeLanes.EveryDirectionKeepsLane(newLayout, spec)); false when unknown
    }

    public struct SiteFactoryResult
    {
        public Entity Edge;           // = SiteFactoryEdge.Target when set, else SiteFactoryEdge.Edge
        public RoadWorksSite Site;
        // Upgrade works: when one chain held more bands than one project can carry it is split into linked projects of the same
        // drag; the neighbours' project ids (0 = none). Runtime only. The factory also leaves them in SiteRegistry.PendingCorridors
        // (not on a dry run), where the Director takes them at adopt (neither may run Half while the other runs Half or Carriageway).
        public uint CorridorPrev, CorridorNext;
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
                    output.Add(new SiteFactoryResult { Edge = e.Target != Entity.Null ? e.Target : ce.Edge, Site = site });
                }
                RRWLog.Info("factory: " + kind + " project #" + id + " edges=" + chain.Edges.Count + " len=" + RRWLog.F(chain.Length)
                            + "m hours=" + RRWLog.F(hours) + " closure=" + closure + (dependants ? " dependants" : "")
                            + ((flags & SiteFlags.Replaced) != 0 ? " replaced" : ""));
            }
            return output;
        }

        // ------------------------------------------------------------------ upgrade works (mode H)

        public static List<SiteFactoryResult> CreateUpgradeProjects(IList<SiteFactoryEdge> edges, IList<UpgradeSpec> specs, RoadClassInfo rc,
                                                                    RRWSetting s, List<SiteFactoryResult> output) =>
            CreateUpgradeProjects(edges, specs, rc, s, SiteFlags.None, output);

        // extraFlags: only SiteFlags.Rushed is taken over (a project created rushed has no setup share).
        public static List<SiteFactoryResult> CreateUpgradeProjects(IList<SiteFactoryEdge> edges, IList<UpgradeSpec> specs, RoadClassInfo rc,
                                                                    RRWSetting s, SiteFlags extraFlags, List<SiteFactoryResult> output)
        {
            output = output ?? new List<SiteFactoryResult>();
            if (s == null) return output;
            return CreateUpgradeProjects(edges, specs, rc, UpgradeRates.From(s), extraFlags, output, null);
        }

        // Upgrade projects of one apply: edges[i] was classified as specs[i] (UpgradeDiff.Classify + Decide; bands in the edge's
        // own frame = laterals of its NEW curve). Edges whose class is not Remark / Widen / Narrow / Mixed or that carry no band are
        // skipped (Tools handle Cosmetic and Structural themselves). rc: road class of the NEW road (rc.CompositionWidth = new
        // width; the share of every band is measured against it).
        //  1. One project per drag chain (ChainBuilder.Build). A chain whose bands do not fit into kUwMaxChainBands chain bands is
        //     split where the union grows past it (ChainBuilder.SplitRuns); its projects are linked (CorridorPrev / CorridorNext,
        //     and SiteRegistry.PendingCorridors for the Director).
        //  2. The edges' bands go to the chain frame (UpgradePlan.ToChain), cluster into chain bands (UpgradePlan.Union) and get
        //     their windows (UpgradePlan.Windows): build bands of both sides share one window when the chain has no buildings and no
        //     dependants (a common primitive exists: lane drops or a carriageway closure); rushed at creation = no setup share.
        //  3. Hours: WorkTime.UpgradeHours (traffic keeps running beside the works); every edge gets the same required frames,
        //     done = 0, PaidCost = max(0, cost), Kind Construction, Mode HalfWidth, flags = its real Dependants (+ Rushed).
        //  4. Tail: plan kind Upgrade, the edge's own class, its own bands in its EDGE frame with their window (traffic undecided),
        //     the shared schedule; flags PreCover (gate at creation) and AllAtOnce (lane drops available for every build window at
        //     creation: drop gate on, no edge with buildings, every edge keeps a lane per direction).
        // log: null = RRWLog; else the lines go there (tests). dryRun: no project id is allocated (m_ProjectId 0) and nothing is
        // logged; the results are only read (tooltips: WorkTime.HoursFromFrames(Site.m_WorkRequired), the windows, the flags).
        public static List<SiteFactoryResult> CreateUpgradeProjects(IList<SiteFactoryEdge> edges, IList<UpgradeSpec> specs, RoadClassInfo rc,
                                                                    in UpgradeRates rates, SiteFlags extraFlags, List<SiteFactoryResult> output,
                                                                    List<string> log, bool dryRun = false)
        {
            if (dryRun) log = s_DryLog;
            output = output ?? new List<SiteFactoryResult>();
            if (edges == null || specs == null || edges.Count == 0) return output;
            var input = new List<ChainEdgeIn>(edges.Count);
            var index = new Dictionary<Entity, int>(edges.Count);
            for (int i = 0; i < edges.Count && i < specs.Count; i++)
            {
                var e = edges[i];
                var sp = specs[i];
                if (!UpgradeSpecUsable(sp))
                {
                    Log(log, "factory: upgrade edge " + RRWLog.E(e.Edge) + " skipped (" + sp.Class + (sp.Why != UpgradeStructural.None ? " " + sp.Why : "")
                             + " bands=" + sp.BandCount + ")");
                    continue;
                }
                if (index.ContainsKey(e.Edge)) continue;
                index[e.Edge] = i;
                input.Add(new ChainEdgeIn { Edge = e.Edge, StartNode = e.StartNode, EndNode = e.EndNode, Length = e.Length });
            }
            if (input.Count == 0) return output;
            bool rushed = (extraFlags & SiteFlags.Rushed) != 0;
            var trial = new List<UpgradeBand>(16);
            var acc = new List<UpgradeBand>(16);
            var scratch = new List<ChainBand>(16);
            var cuts = new List<int>(2);
            foreach (var chain in ChainBuilder.Build(input))
            {
                // 1. runs that fit into one project
                cuts.Clear();
                acc.Clear();
                for (int k = 0; k < chain.Edges.Count; k++)
                {
                    var ce = chain.Edges[k];
                    var sp = specs[index[ce.Edge]];
                    bool rev = ce.U1 < ce.U0;
                    trial.Clear();
                    trial.AddRange(acc);
                    for (int j = 0; j < sp.BandCount; j++) trial.Add(UpgradePlan.ToChain(sp.Band(j), rev));
                    if (k > 0 && UpgradePlan.Union(trial, scratch) > RRWConst.kUwMaxChainBands)
                    {
                        cuts.Add(k);
                        acc.Clear();
                        for (int j = 0; j < sp.BandCount; j++) acc.Add(UpgradePlan.ToChain(sp.Band(j), rev));
                    }
                    else { acc.Clear(); acc.AddRange(trial); }
                }
                var runs = cuts.Count == 0 ? new List<Chain> { chain } : ChainBuilder.SplitRuns(chain, cuts);
                var ids = new uint[runs.Count];
                for (int r = 0; r < runs.Count; r++) ids[r] = dryRun ? 0u : SiteRegistry.AllocateProjectId();
                if (!dryRun && runs.Count > 1)
                    for (int r = 0; r < runs.Count; r++)
                        SiteRegistry.PendingCorridors[ids[r]] = new CorridorLink { Prev = r > 0 ? ids[r - 1] : 0u, Next = r + 1 < runs.Count ? ids[r + 1] : 0u };
                for (int r = 0; r < runs.Count; r++)
                    CreateUpgradeRun(runs[r], ids[r], r > 0 ? ids[r - 1] : 0u, r + 1 < runs.Count ? ids[r + 1] : 0u, edges, specs, index, rc,
                                     rates, rushed, extraFlags, output, log);
            }
            return output;
        }

        // A classifier result that makes an upgrade site: Remark / Widen / Narrow / Mixed with 1..kUwMaxBands bands.
        public static bool UpgradeSpecUsable(in UpgradeSpec sp) =>
            sp.Class >= UpgradeClass.Remark && sp.Class <= UpgradeClass.Mixed && sp.BandCount > 0 && sp.BandCount <= RRWConst.kUwMaxBands;

        private static void CreateUpgradeRun(Chain run, uint id, uint prev, uint next, IList<SiteFactoryEdge> edges, IList<UpgradeSpec> specs,
                                             Dictionary<Entity, int> index, RoadClassInfo rc, in UpgradeRates rates, bool rushed, SiteFlags extraFlags,
                                             List<SiteFactoryResult> output, List<string> log)
        {
            // 2. chain-frame bands of every edge, union, windows
            var cfb = new List<UpgradeBand>(16);
            bool anyBuildings = false, anyDependants = false, allKeep = true, anyBuild = false;
            float maxLat = 0f;
            foreach (var ce in run.Edges)
            {
                int i = index[ce.Edge];
                var sp = specs[i];
                var e = edges[i];
                anyBuildings |= e.Buildings;
                anyDependants |= e.Dependants;
                allKeep &= e.KeepsLanes;
                for (int j = 0; j < sp.BandCount; j++)
                {
                    var b = sp.Band(j);
                    cfb.Add(UpgradePlan.ToChain(b, ce.U1 < ce.U0));
                    if (b.Kind == BandKind.Build || b.Kind == BandKind.Rebuild) anyBuild = true;
                    maxLat = math.max(maxLat, math.max(math.abs(b.Lo), math.abs(b.Hi)));
                }
            }
            var chainBands = new List<ChainBand>(8);
            var map = new int[cfb.Count];
            UpgradePlan.Union(cfb, chainBands, map);
            bool parallel = !anyBuildings && !anyDependants;
            var sched = UpgradePlan.Windows(chainBands, parallel, rushed, rates.DemolitionRatio);
            bool allAtOnce = rates.DropGate && anyBuild && !anyBuildings && allKeep;
            var upFlags = (allAtOnce ? UpgradeFlags.AllAtOnce : UpgradeFlags.None) | (rates.PreCoverGate ? UpgradeFlags.PreCover : UpgradeFlags.None);
            var asBands = new List<UpgradeBand>(chainBands.Count);
            for (int k = 0; k < chainBands.Count; k++) asBands.Add(UpgradeBand.Make(chainBands[k].Kind, chainBands[k].Side, chainBands[k].Lo, chainBands[k].Hi));
            var cls = UpgradePlan.ClassOf(asBands, asBands.Count);
            float newWidth = rc.CompositionWidth > 0.5f ? rc.CompositionWidth : math.max(4f, 2f * maxLat);

            // 3. hours
            float hours = WorkTime.UpgradeHours(run.Length, rc, rates.ConstructionHoursPerKm, rates.ConstructionMinHours, rates.DemolitionRatio,
                                                chainBands, sched.N, newWidth, cls, true);
            uint required = WorkTime.FramesFromHours(hours);
            ushort seed = (ushort)(math.hash(new uint2(id, (uint)run.Edges.Count)) & 0xFFFF);

            // 4. one site per edge
            int m = 0;
            foreach (var ce in run.Edges)
            {
                int i = index[ce.Edge];
                var sp = specs[i];
                var e = edges[i];
                var bands = new UpgradeBand[RRWConst.kUwMaxBands];
                for (int j = 0; j < sp.BandCount; j++, m++)
                {
                    var b = sp.Band(j);
                    int c = map[m];
                    b.SetWinPrim(c >= 0 ? chainBands[c].Window : 0, BandTraffic.Undecided);
                    bands[j] = b;
                }
                var site = RoadWorksSite.Create(WorksKind.Construction, VisualMode.HalfWidth, required, id, ce.U0, ce.U1, run.Length, seed,
                                                math.max(0, e.PaidCost));
                site.Flags = (extraFlags & SiteFlags.Rushed) | (e.Dependants ? SiteFlags.Dependants : SiteFlags.None);
                site.m_WorkDone = 0;
                site.SetTail(PlanKind.Upgrade, sp.Class, sched, upFlags, bands[0], bands[1], bands[2], bands[3], sp.BandCount);
                output.Add(new SiteFactoryResult
                {
                    Edge = e.Target != Entity.Null ? e.Target : ce.Edge,
                    Site = site,
                    CorridorPrev = prev,
                    CorridorNext = next,
                });
            }
            Log(log, "factory: Upgrade project #" + id + " " + cls + " edges=" + run.Edges.Count + " len=" + RRWLog.F(run.Length) + "m hours="
                     + RRWLog.F(hours) + " windows " + sched + " flags=" + upFlags + (parallel ? " parallel" : "")
                     + (anyBuildings ? " buildings" : "") + " bands: " + UpgradePlan.Describe(chainBands)
                     + (prev != 0 || next != 0 ? " corridor " + prev + "/" + next : ""));
        }

        private static void Log(List<string> log, string line)
        {
            if (log == s_DryLog) return;
            if (log != null) log.Add(line);
            else RRWLog.Info(line);
        }

        private static readonly List<string> s_DryLog = new List<string>(0);
    }
}
