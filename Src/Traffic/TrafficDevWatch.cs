#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using Game;
using Game.Common;
using Game.Net;
using Game.Simulation;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using ConnectedBuilding = Game.Buildings.ConnectedBuilding;
using CurrentBuilding = Game.Citizens.CurrentBuilding;
using CurrentTransport = Game.Citizens.CurrentTransport;
using GarbageProducer = Game.Buildings.GarbageProducer;
using HouseholdCitizen = Game.Citizens.HouseholdCitizen;
using Icon = Game.Notifications.Icon;
using Moving = Game.Objects.Moving;
using NetCarLane = Game.Net.CarLane;
using ParkedCar = Game.Vehicles.ParkedCar;
using PathFlags = Game.Pathfind.PathFlags;
using PathInformation = Game.Pathfind.PathInformation;
using PathOwner = Game.Pathfind.PathOwner;
using PrefabBase = Game.Prefabs.PrefabBase;
using PrefabRef = Game.Prefabs.PrefabRef;
using PrefabSystem = Game.Prefabs.PrefabSystem;
using Renter = Game.Buildings.Renter;
using Target = Game.Common.Target;
using Worker = Game.Citizens.Worker;
using CarCurrentLane = Game.Vehicles.CarCurrentLane;
using HumanCurrentLane = Game.Creatures.HumanCurrentLane;

// rrw.tr.watch / rrw.tr.home: measure what a closure does to traffic and residents (CLOSED-B, dead-end rule, sidewalk drain)
// on the shipped closure code; ported from the traffic prototype's watch tools. Dev builds only.
//  * Track() runs every render frame at the end of MainLoop (after PathfindResultSystem wrote this frame's results, before the
//    AIs consume them): new Failed path results (global and on the watched lanes), the watched residents' departure results
//    (citizen PathInformation: tripOk / tripFailed), and per watched car-lane vehicle the time it has been slower than 0.1 m/s
//    (stuck = longer than 60 s game time = 3600 sim frames).
//  * Sample() runs every <everySimFrames>: path users (activeThrough, onLane, stale), residents at home / left / arrived, workers,
//    garbage, Pathfind-failed / access icons (global). The final sample prints a RESULT line with the summary numbers.
namespace RealisticRoadWorks.V3.Traffic
{
    internal sealed class TrafficWatch
    {
        public const uint kStuckFrames = 3600;
        public string Tag = "TR";
        public string Spec = "";
        public readonly List<Entity> Edges = new List<Entity>();
        public uint Every = 600;
        public int Total = 20, Done;
        public uint StartSim, LastSim;

        private readonly HashSet<Entity> m_Lanes = new HashSet<Entity>();
        private readonly List<Entity> m_CarLanes = new List<Entity>();
        private readonly List<TrafficResidents.Resident> m_Residents = new List<TrafficResidents.Resident>();
        private HashSet<Entity> m_FailedPrev = new HashSet<Entity>(), m_FailedNow = new HashSet<Entity>();
        private readonly Dictionary<Entity, TrafficResidents.CitizenPath> m_CitPrev = new Dictionary<Entity, TrafficResidents.CitizenPath>();
        private readonly Dictionary<Entity, uint> m_SlowSince = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, bool> m_AtHome = new Dictionary<Entity, bool>();
        private readonly HashSet<Entity> m_StuckSeen = new HashSet<Entity>();
        public int FailEvents, FailedOnEdges, TripOk, TripFailed, SearchOk, SearchFailed, Left, Arrived;
        public int MaxActiveThrough, LastActiveThrough, MaxPf, MaxAccess, StuckNow, StuckMax;
        public int StartWorkers = -1, LastWorkers, StartGarbage = -1, LastGarbage;
        private EntityQuery m_Owners, m_Icons;
        private bool m_HaveQueries;
        private bool m_Primed;

        public void Refresh(EntityManager em)
        {
            m_Lanes.Clear();
            m_CarLanes.Clear();
            var tmp = new List<Entity>();
            foreach (var e in Edges)
            {
                TrafficUtil.LanesInto(em, e, tmp);
                foreach (var l in tmp)
                {
                    if (!TrafficUtil.Alive(em, l)) continue;
                    m_Lanes.Add(l);
                    if (em.HasComponent<NetCarLane>(l) && em.HasBuffer<LaneObject>(l)) m_CarLanes.Add(l);
                }
            }
            m_Residents.Clear();
            TrafficResidents.Collect(em, Edges, m_Residents, out _, out _);
        }

        private void EnsureQueries(EntityManager em)
        {
            if (m_HaveQueries) return;
            m_HaveQueries = true;
            m_Owners = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PathOwner>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            m_Icons = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Icon>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
        }

        public void Track(EntityManager em, uint sim, bool count)
        {
            EnsureQueries(em);
            // 1. Failed path results (global; Failed is only set by PathfindResultSystem).
            m_Owners.CompleteDependency();
            var entH = em.GetEntityTypeHandle();
            var ownerH = em.GetComponentTypeHandle<PathOwner>(true);
            m_FailedNow.Clear();
            var chunks = m_Owners.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    var owners = chunks[c].GetNativeArray(ref ownerH);
                    NativeArray<Entity> ents = default;
                    for (int i = 0; i < owners.Length; i++)
                    {
                        if ((owners[i].m_State & PathFlags.Failed) == 0) continue;
                        if (!ents.IsCreated) ents = chunks[c].GetNativeArray(entH);
                        m_FailedNow.Add(ents[i]);
                    }
                }
            }
            finally { chunks.Dispose(); }
            foreach (var e in m_FailedNow)
            {
                if (!count || m_FailedPrev.Contains(e)) continue;
                FailEvents++;
                Entity lane = Entity.Null;
                if (em.HasComponent<CarCurrentLane>(e)) lane = em.GetComponentData<CarCurrentLane>(e).m_Lane;
                else if (em.HasComponent<HumanCurrentLane>(e)) lane = em.GetComponentData<HumanCurrentLane>(e).m_Lane;
                if (lane != Entity.Null && m_Lanes.Contains(lane)) FailedOnEdges++;
            }
            var t = m_FailedPrev; m_FailedPrev = m_FailedNow; m_FailedNow = t;

            // 2. Departure / leisure-search results of the watched residents.
            foreach (var r in m_Residents)
            {
                if (!em.Exists(r.Citizen)) continue;
                var st = TrafficResidents.PathState(em, r.Citizen, out bool isTrip);
                m_CitPrev.TryGetValue(r.Citizen, out var prev);
                if (count && st != prev && (st == TrafficResidents.CitizenPath.Ok || st == TrafficResidents.CitizenPath.Failed))
                {
                    bool ok = st == TrafficResidents.CitizenPath.Ok;
                    if (isTrip) { if (ok) TripOk++; else TripFailed++; }
                    else { if (ok) SearchOk++; else SearchFailed++; }
                }
                m_CitPrev[r.Citizen] = st;
            }

            // 3. Stuck vehicles on the watched car lanes (slower than 0.1 m/s for > kStuckFrames).
            if (!count) return;
            int stuck = 0;
            var seen = new HashSet<Entity>();
            foreach (var l in m_CarLanes)
            {
                if (!em.Exists(l) || !em.HasBuffer<LaneObject>(l)) continue;
                var buf = em.GetBuffer<LaneObject>(l, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    var o = buf[i].m_LaneObject;
                    if (!seen.Add(o) || !em.Exists(o) || em.HasComponent<ParkedCar>(o)) continue;
                    float v = em.HasComponent<Moving>(o) ? math.length(em.GetComponentData<Moving>(o).m_Velocity) : 0f;
                    if (v >= 0.1f) { m_SlowSince.Remove(o); continue; }
                    if (!m_SlowSince.TryGetValue(o, out uint since)) { m_SlowSince[o] = sim; continue; }
                    if (sim - since > kStuckFrames) { stuck++; m_StuckSeen.Add(o); }
                }
            }
            if (m_SlowSince.Count > 4096) m_SlowSince.Clear();
            StuckNow = stuck;
            if (stuck > StuckMax) StuckMax = stuck;
        }

        public void Sample(EntityManager em, World world, uint sim, bool final, Func<Dictionary<Entity, uint>, PathScanResult> scan)
        {
            Refresh(em);
            if (!m_Primed) { m_Primed = true; Track(em, sim, false); }
            var map = new Dictionary<Entity, uint>();
            foreach (var l in m_Lanes) map[l] = 0u;
            var r = scan(map);
            int home = 0, travel = 0, workers = 0;
            foreach (var res in m_Residents)
            {
                if (!TrafficUtil.Alive(em, res.Citizen)) continue;
                bool atHome = em.HasComponent<CurrentBuilding>(res.Citizen) && em.GetComponentData<CurrentBuilding>(res.Citizen).m_CurrentBuilding == res.Building;
                if (atHome) home++;
                if (em.HasComponent<CurrentTransport>(res.Citizen) && TrafficUtil.Alive(em, em.GetComponentData<CurrentTransport>(res.Citizen).m_CurrentTransport)) travel++;
                if (em.HasComponent<Worker>(res.Citizen)) workers++;
                if (m_AtHome.TryGetValue(res.Citizen, out bool was))
                {
                    if (was && !atHome) Left++;
                    if (!was && atHome) Arrived++;
                }
                m_AtHome[res.Citizen] = atHome;
            }
            int garbage = TrafficResidents.Garbage(em, Edges);
            if (StartWorkers < 0) StartWorkers = workers;
            if (StartGarbage < 0) StartGarbage = garbage;
            LastWorkers = workers;
            LastGarbage = garbage;
            EnsureQueries(em);
            TrafficResidents.IconCounts(em, world, m_Icons, out int pf, out int acc);
            if (pf > MaxPf) MaxPf = pf;
            if (acc > MaxAccess) MaxAccess = acc;
            LastActiveThrough = r.ActiveThrough;
            if (r.ActiveThrough > MaxActiveThrough) MaxActiveThrough = r.ActiveThrough;
            RRWLog.Dev("dev rrw tr watch " + Tag + " i=" + Done + "/" + Total + " dFrame=" + (sim - StartSim) + " edges=" + Spec + " " + r.Summary() +
                       " | fails events=" + FailEvents + " onEdges=" + FailedOnEdges + " | stuck now=" + StuckNow + " max=" + StuckMax +
                       " | residents=" + m_Residents.Count + " home=" + home + " travelling=" + travel + " workers=" + workers +
                       " tripOk=" + TripOk + " tripFailed=" + TripFailed + " searchOk=" + SearchOk + " searchFailed=" + SearchFailed +
                       " left=" + Left + " arrived=" + Arrived + " garbage=" + garbage + " | icons pathfindFailed=" + pf + " access=" + acc);
            Done++;
            LastSim = sim;
            if (final)
                RRWLog.Dev("dev rrw tr watch RESULT " + Tag + " edges=" + Spec + " samples=" + Done + " frames=" + (sim - StartSim) +
                           " tripOk=" + TripOk + " tripFailed=" + TripFailed + " residentsLeft=" + Left + " residentsArrived=" + Arrived +
                           " workers start=" + StartWorkers + " end=" + LastWorkers + " garbage start=" + StartGarbage + " end=" + LastGarbage +
                           " maxActiveThrough=" + MaxActiveThrough + " lastActiveThrough=" + LastActiveThrough + " failEvents=" + FailEvents +
                           " failedOnEdges=" + FailedOnEdges + " stuckMax=" + StuckMax + " stuckSeen=" + m_StuckSeen.Count +
                           " maxPathfindFailedIcons=" + MaxPf + " maxAccessIcons=" + MaxAccess);
        }
    }

    internal static class TrafficResidents
    {
        public struct Resident { public Entity Citizen, Building; }
        public enum CitizenPath : byte { None = 0, Pending = 1, Ok = 2, Failed = 3 }

        public static void Collect(EntityManager em, List<Entity> edges, List<Resident> output, out int buildings, out int companies)
        {
            buildings = 0; companies = 0;
            var seen = new HashSet<Entity>();
            var bs = new List<Entity>();
            var hs = new List<Entity>();
            foreach (var edge in edges)
            {
                if (!TrafficUtil.Alive(em, edge) || !em.HasBuffer<ConnectedBuilding>(edge)) continue;
                bs.Clear();
                var cb = em.GetBuffer<ConnectedBuilding>(edge, true);
                for (int i = 0; i < cb.Length; i++) bs.Add(cb[i].m_Building);
                foreach (var b in bs)
                {
                    if (!seen.Add(b) || !TrafficUtil.Alive(em, b)) continue;
                    buildings++;
                    if (!em.HasBuffer<Renter>(b)) continue;
                    hs.Clear();
                    var renters = em.GetBuffer<Renter>(b, true);
                    for (int i = 0; i < renters.Length; i++) hs.Add(renters[i].m_Renter);
                    foreach (var h in hs)
                    {
                        if (!TrafficUtil.Alive(em, h)) continue;
                        if (!em.HasBuffer<HouseholdCitizen>(h)) { companies++; continue; }
                        var hc = em.GetBuffer<HouseholdCitizen>(h, true);
                        for (int i = 0; i < hc.Length; i++) output.Add(new Resident { Citizen = hc[i].m_Citizen, Building = b });
                    }
                }
            }
        }

        // Citizen-level path state (departures are pathfound on the Citizen entity: PathInformation, + Target for trips).
        public static CitizenPath PathState(EntityManager em, Entity citizen, out bool isTrip)
        {
            isTrip = false;
            if (!em.HasComponent<PathInformation>(citizen)) return CitizenPath.None;
            isTrip = em.HasComponent<Target>(citizen);
            var pi = em.GetComponentData<PathInformation>(citizen);
            if ((pi.m_State & PathFlags.Pending) != 0) return CitizenPath.Pending;
            return pi.m_Destination == Entity.Null ? CitizenPath.Failed : CitizenPath.Ok;
        }

        public static int Garbage(EntityManager em, List<Entity> edges)
        {
            int g = 0;
            var seen = new HashSet<Entity>();
            foreach (var edge in edges)
            {
                if (!TrafficUtil.Alive(em, edge) || !em.HasBuffer<ConnectedBuilding>(edge)) continue;
                var cb = em.GetBuffer<ConnectedBuilding>(edge, true);
                for (int i = 0; i < cb.Length; i++)
                {
                    var b = cb[i].m_Building;
                    if (!seen.Add(b) || !TrafficUtil.Alive(em, b) || !em.HasComponent<GarbageProducer>(b)) continue;
                    g += em.GetComponentData<GarbageProducer>(b).m_Garbage;
                }
            }
            return g;
        }

        private static readonly Dictionary<Entity, string> s_Names = new Dictionary<Entity, string>();

        public static void IconCounts(EntityManager em, World world, EntityQuery q, out int pathfind, out int access)
        {
            pathfind = 0; access = 0;
            var ps = world.GetExistingSystemManaged<PrefabSystem>();
            var refs = q.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            for (int i = 0; i < refs.Length; i++)
            {
                var prefab = refs[i].m_Prefab;
                if (!s_Names.TryGetValue(prefab, out var n))
                {
                    n = "?";
                    if (ps != null && ps.TryGetPrefab<PrefabBase>(new PrefabRef { m_Prefab = prefab }, out var p) && p != null) n = p.name;
                    if (s_Names.Count > 1024) s_Names.Clear();
                    s_Names[prefab] = n;
                }
                if (n.IndexOf("Pathfind", StringComparison.OrdinalIgnoreCase) >= 0) pathfind++;
                if (n.IndexOf("Access", StringComparison.OrdinalIgnoreCase) >= 0) access++;
            }
            refs.Dispose();
        }
    }

    // End of MainLoop (after PathfindResultSystem): Track every frame, Sample every <every> sim frames.
    [RegisterSystem(SystemUpdatePhase.MainLoop)]
    public partial class TrafficWatchSystem : GameSystemBase
    {
        internal static TrafficWatch Watch;
        private readonly RRWGuard m_Guard = new RRWGuard("traffic dev watch");
        private EntityQuery m_Paths;
        private bool m_HaveQuery;

        protected override void OnUpdate()
        {
            var w = Watch;
            if (w == null || m_Guard.Faulted) return;
            try
            {
                var em = EntityManager;
                var sim = World.GetExistingSystemManaged<SimulationSystem>();
                uint frame = sim != null ? sim.frameIndex : 0u;
                w.Track(em, frame, true);
                if (frame - w.LastSim >= w.Every)
                {
                    bool final = w.Done + 1 >= w.Total;
                    if (!m_HaveQuery) { m_Paths = TrafficPaths.CreateQuery(em); m_HaveQuery = true; }
                    w.Sample(em, World, frame, final, map =>
                    {
                        em.CompleteAllTrackedJobs();
                        var r = new PathScanResult();
                        int cursor = 0;
                        TrafficPaths.ScanSlice(em, m_Paths, map, PathScanMode.Count, TrafficConst.kScanAhead, ref cursor, 0, r, null);
                        return r;
                    });
                    if (final) Watch = null;
                }
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
                Watch = null;
                RRWLog.Warn("traffic dev watch aborted after an error (see above); start a new rrw.tr.watch");
            }
        }
    }

    public sealed class TrafficWatchCommand : IDevCommand
    {
        public string Name => "rrw.tr.watch";
        public string Help => "rrw.tr.watch <edges> <everySimFrames> [samples=20] [tag=TR] | rrw.tr.watch stop - periodic WATCH lines over the edges' lanes " +
                              "(path users, failed paths, stuck vehicles, residents tripOk/tripFailed/left/arrived, workers, garbage, icons) + a final RESULT line";
        public void Run(DevContext ctx, string[] a)
        {
            if (a.Length >= 1 && a[0] == "stop") { TrafficWatchSystem.Watch = null; ctx.Log("rrw tr watch stopped"); return; }
            if (a.Length < 2) { ctx.Log("usage: " + Help); return; }
            var em = ctx.EntityManager;
            var w = new TrafficWatch
            {
                Spec = a[0],
                Every = Math.Max(1u, uint.Parse(a[1], CultureInfo.InvariantCulture)),
                Total = Math.Max(1, int.Parse(TrafficDevSites.Opt(a, "samples", a.Length > 2 && char.IsDigit(a[2][0]) ? a[2] : "20"), CultureInfo.InvariantCulture)),
                Tag = TrafficDevSites.Opt(a, "tag", "TR"),
            };
            w.Edges.AddRange(TrafficDevSites.Edges(ctx, a[0]));
            var sim = ctx.World.GetExistingSystemManaged<SimulationSystem>();
            w.StartSim = w.LastSim = sim != null ? sim.frameIndex : 0u;
            em.CompleteAllTrackedJobs();
            w.Refresh(em);
            w.Track(em, w.StartSim, false);   // baseline: existing failures / results are not events
            TrafficWatchSystem.Watch = w;
            ctx.Log("rrw tr watch started edges=" + w.Edges.Count + " every=" + w.Every + " samples=" + w.Total + " tag=" + w.Tag);
        }
    }

    public sealed class TrafficHomeCommand : IDevCommand
    {
        public string Name => "rrw.tr.home";
        public string Help => "rrw.tr.home <edges> - buildings on the edges: residents at home / travelling / workers / citizen path pending+failed, garbage";
        public void Run(DevContext ctx, string[] a)
        {
            if (a.Length < 1) { ctx.Log("usage: " + Help); return; }
            var em = ctx.EntityManager;
            em.CompleteAllTrackedJobs();
            var edges = TrafficDevSites.Edges(ctx, a[0]);
            var res = new List<TrafficResidents.Resident>();
            TrafficResidents.Collect(em, edges, res, out int buildings, out int companies);
            int home = 0, travel = 0, workers = 0, pending = 0, failed = 0;
            foreach (var r in res)
            {
                if (!TrafficUtil.Alive(em, r.Citizen)) continue;
                if (em.HasComponent<CurrentBuilding>(r.Citizen) && em.GetComponentData<CurrentBuilding>(r.Citizen).m_CurrentBuilding == r.Building) home++;
                if (em.HasComponent<CurrentTransport>(r.Citizen) && TrafficUtil.Alive(em, em.GetComponentData<CurrentTransport>(r.Citizen).m_CurrentTransport)) travel++;
                if (em.HasComponent<Worker>(r.Citizen)) workers++;
                var st = TrafficResidents.PathState(em, r.Citizen, out _);
                if (st == TrafficResidents.CitizenPath.Pending) pending++;
                if (st == TrafficResidents.CitizenPath.Failed) failed++;
            }
            var sim = ctx.World.GetExistingSystemManaged<SimulationSystem>();
            ctx.Log("rrw tr home edges=" + a[0] + " buildings=" + buildings + " companies=" + companies + " residents=" + res.Count + " atHome=" + home +
                    " travelling=" + travel + " workers=" + workers + " citizenPathPending=" + pending + " citizenPathFailedNow=" + failed +
                    " garbage=" + TrafficResidents.Garbage(em, edges) + " simFrame=" + (sim != null ? sim.frameIndex : 0u) +
                    " (failures are transient: use rrw.tr.watch for counts)");
        }
    }
}
#endif
