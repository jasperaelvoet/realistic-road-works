using System.Collections.Generic;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CarCurrentLane = Game.Vehicles.CarCurrentLane;
using CarNavigationLane = Game.Vehicles.CarNavigationLane;
using CurrentVehicle = Game.Creatures.CurrentVehicle;
using GroupMember = Game.Creatures.GroupMember;
using HumanCurrentLane = Game.Creatures.HumanCurrentLane;
using PathElement = Game.Pathfind.PathElement;
using PathFlags = Game.Pathfind.PathFlags;
using PathOwner = Game.Pathfind.PathOwner;
using TrainCurrentLane = Game.Vehicles.TrainCurrentLane;
using TrainNavigationLane = Game.Vehicles.TrainNavigationLane;

// In-flight path invalidation (port of the traffic prototype; verified in game: 58/58 through paths rerouted,
// none re-invalidated). Main-thread chunk scan, time-sliced: a pass walks the path-owner chunks over as many updates as
// it needs, each slice bounded by TrafficConst.kScanSliceMs.
//
// Invalidation targets THROUGH traffic only:
// - entities on the closed lanes drive / walk out;
// - entities whose path ENDS on the edge (local access) are never invalidated (re-invalidating them would loop);
// - Pending / Failed / Stuck (RequireNewPath ignores Obsolete while Stuck) and already-truncated paths are skipped;
// - passengers and group members follow their vehicle / leader.
namespace RealisticRoadWorks.V3.Traffic
{
    public enum PathScanMode { Count, Invalidate }

    public sealed class PathScanResult
    {
        public int Scanned, Pending, Failed, Obsolete, SkippedGroupOrPassenger, Chunks;
        public readonly int[] OnLane = new int[3];   // car / human / train currently on the lanes
        public readonly int[] Nav = new int[3];      // through traffic: navigation lanes hit, path active
        public readonly int[] Future = new int[3];   // through traffic: only future PathElements hit, path active
        public readonly int[] Dest = new int[3];     // local access: path ends on the lanes
        public readonly int[] Stale = new int[3];    // hit, but path already Pending/Failed/Obsolete/Stuck or truncated
        public readonly int[] StuckOn = new int[3];  // on the lanes with PathFlags.Stuck (dev: "stuck" measurement)
        public int Invalidated, SkippedOnLane, SkippedPendingFailed, SkippedDest, Reinvalidated;
        public int NavAny;                           // not on the lanes, but navigation lanes lead into them
        public double Ms;

        public int ActiveThrough => Nav[0] + Nav[1] + Nav[2] + Future[0] + Future[1] + Future[2];

        public void Reset()
        {
            Scanned = Pending = Failed = Obsolete = SkippedGroupOrPassenger = Chunks = 0;
            for (int i = 0; i < 3; i++) { OnLane[i] = Nav[i] = Future[i] = Dest[i] = Stale[i] = StuckOn[i] = 0; }
            Invalidated = SkippedOnLane = SkippedPendingFailed = SkippedDest = Reinvalidated = NavAny = 0;
            Ms = 0;
        }

        private static string Tri(int[] a) => a[0] + "/" + a[1] + "/" + a[2];

        public string Summary() =>
            "pathUsers(c/h/t) onLane=" + Tri(OnLane) + " nav=" + Tri(Nav) + " future=" + Tri(Future) + " dest=" + Tri(Dest) +
            " stale=" + Tri(Stale) + " stuckOnLane=" + Tri(StuckOn) + " activeThrough=" + ActiveThrough + " | scanned=" + Scanned + " pending=" + Pending +
            " failed=" + Failed + " obsolete=" + Obsolete + " chunks=" + Chunks + " ms=" + RRWLog.F((float)Ms);
    }

    public static class TrafficPaths
    {
        private static readonly List<Entity> s_InvalidatedNow = new List<Entity>(64);
        private static readonly List<uint> s_InvalidatedProject = new List<uint>(64);

        public static EntityQuery CreateQuery(EntityManager em) => em.CreateEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadWrite<PathOwner>(), ComponentType.ReadWrite<PathElement>() },
            Any = new[] { ComponentType.ReadOnly<CarCurrentLane>(), ComponentType.ReadOnly<HumanCurrentLane>(), ComponentType.ReadOnly<TrainCurrentLane>() },
            None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
        });

        // Scans chunks [cursor, ...) until the budget is spent (budgetMs <= 0: no limit). Returns true when the pass
        // reached the last chunk. lanes: lane -> owning project id (0 when unknown). The caller completes jobs first.
        // reroutedByProject (optional) receives the number of NEWLY invalidated entities per project.
        // kindMask: bit 0 cars, bit 1 humans, bit 2 trains (dev filters, e.g. rrw.tr.inval .. humans to drain a sidewalk).
        public static bool ScanSlice(EntityManager em, EntityQuery q, Dictionary<Entity, uint> lanes, PathScanMode mode,
                                     int ahead, ref int cursor, double budgetMs, PathScanResult r, Dictionary<uint, int> reroutedByProject,
                                     int kindMask = 7)
        {
            long t0 = global::System.Diagnostics.Stopwatch.GetTimestamp();
            double tickMs = 1000.0 / global::System.Diagnostics.Stopwatch.Frequency;
            bool invalidate = mode == PathScanMode.Invalidate;
            var entH = em.GetEntityTypeHandle();
            var ownerH = em.GetComponentTypeHandle<PathOwner>(!invalidate);
            var elemH = em.GetBufferTypeHandle<PathElement>(true);
            var carCurH = em.GetComponentTypeHandle<CarCurrentLane>(true);
            var humCurH = em.GetComponentTypeHandle<HumanCurrentLane>(true);
            var trnCurH = em.GetComponentTypeHandle<TrainCurrentLane>(true);
            var carNavH = em.GetBufferTypeHandle<CarNavigationLane>(true);
            var trnNavH = em.GetBufferTypeHandle<TrainNavigationLane>(true);
            var groupH = em.GetComponentTypeHandle<GroupMember>(true);
            var curVehH = em.GetComponentTypeHandle<CurrentVehicle>(true);
            bool haveLanes = lanes != null && lanes.Count > 0;
            int limit = ahead > 0 ? ahead : 1;
            s_InvalidatedNow.Clear();
            s_InvalidatedProject.Clear();

            var chunks = q.ToArchetypeChunkArray(Allocator.Temp);
            bool done;
            try
            {
                if (cursor < 0) cursor = 0;
                while (cursor < chunks.Length)
                {
                    var ch = chunks[cursor++];
                    r.Chunks++;
                    var ents = ch.GetNativeArray(entH);
                    var owners = ch.GetNativeArray(ref ownerH);
                    var elems = ch.GetBufferAccessor(ref elemH);
                    var carCur = ch.GetNativeArray(ref carCurH);
                    var humCur = ch.GetNativeArray(ref humCurH);
                    var trnCur = ch.GetNativeArray(ref trnCurH);
                    var carNav = ch.GetBufferAccessor(ref carNavH);
                    var trnNav = ch.GetBufferAccessor(ref trnNavH);
                    bool skipChunk = ch.Has(ref groupH) || ch.Has(ref curVehH);
                    int kind = carCur.Length != 0 ? 0 : (humCur.Length != 0 ? 1 : 2);
                    if ((kindMask & (1 << kind)) == 0) continue;

                    for (int i = 0; i < ents.Length; i++)
                    {
                        r.Scanned++;
                        var po = owners[i];
                        bool failed = (po.m_State & PathFlags.Failed) != 0;
                        bool pending = (po.m_State & PathFlags.Pending) != 0;
                        bool obsolete = (po.m_State & PathFlags.Obsolete) != 0;
                        bool stuck = (po.m_State & PathFlags.Stuck) != 0;
                        if (failed) r.Failed++;
                        if (pending) r.Pending++;
                        if (obsolete) r.Obsolete++;
                        if (skipChunk) { r.SkippedGroupOrPassenger++; continue; }
                        if (!haveLanes) continue;

                        bool onLane;
                        if (kind == 0) onLane = lanes.ContainsKey(carCur[i].m_Lane) || (carCur[i].m_ChangeLane != Entity.Null && lanes.ContainsKey(carCur[i].m_ChangeLane));
                        else if (kind == 1) onLane = lanes.ContainsKey(humCur[i].m_Lane);
                        else onLane = lanes.ContainsKey(trnCur[i].m_Front.m_Lane) || lanes.ContainsKey(trnCur[i].m_Rear.m_Lane);

                        Entity hitLane = Entity.Null;
                        bool navHit = false;
                        if (kind == 0 && carNav.Length != 0)
                        {
                            var nav = carNav[i];
                            for (int k = 0; k < nav.Length && !navHit; k++)
                                if (lanes.ContainsKey(nav[k].m_Lane)) { navHit = true; hitLane = nav[k].m_Lane; }
                        }
                        else if (kind == 2 && trnNav.Length != 0)
                        {
                            var nav = trnNav[i];
                            for (int k = 0; k < nav.Length && !navHit; k++)
                                if (lanes.ContainsKey(nav[k].m_Lane)) { navHit = true; hitLane = nav[k].m_Lane; }
                        }

                        int firstFuture = -1;
                        bool truncated = false;   // a nulled element before the hit: CarNavigationSystem will repath there
                        var path = elems[i];
                        int start = po.m_ElementIndex > 0 ? po.m_ElementIndex : 0;
                        int end = path.Length < start + limit ? path.Length : start + limit;
                        for (int k = start; k < end; k++)
                        {
                            var t = path[k].m_Target;
                            if (t == Entity.Null) { truncated = true; break; }
                            if (lanes.ContainsKey(t)) { firstFuture = k; if (hitLane == Entity.Null) hitLane = t; break; }
                        }

                        bool hit = navHit || firstFuture >= 0;
                        bool dest = false;
                        if (hit)
                        {
                            int tail = path.Length - TrafficConst.kDestTail;
                            for (int k = tail > start ? tail : start; k < path.Length && !dest; k++)
                                dest = lanes.ContainsKey(path[k].m_Target);
                        }

                        bool stale = failed || pending || obsolete || stuck || (truncated && !navHit);
                        if (!onLane && navHit) r.NavAny++;
                        if (onLane) { r.OnLane[kind]++; if (stuck) r.StuckOn[kind]++; }
                        else if (hit)
                        {
                            if (stale) r.Stale[kind]++;
                            else if (dest) r.Dest[kind]++;
                            else if (navHit) r.Nav[kind]++;
                            else r.Future[kind]++;
                        }

                        if (!invalidate || !hit) continue;
                        if (onLane) { r.SkippedOnLane++; continue; }          // let them drive / walk out
                        if (failed || pending || stuck) { r.SkippedPendingFailed++; continue; }
                        if (obsolete || (truncated && !navHit)) continue;     // already repathing
                        if (dest) { r.SkippedDest++; continue; }              // local access, not through traffic
                        po.m_State |= PathFlags.Obsolete;
                        owners[i] = po;
                        r.Invalidated++;
                        s_InvalidatedNow.Add(ents[i]);
                        lanes.TryGetValue(hitLane, out uint pid);
                        s_InvalidatedProject.Add(pid);
                    }

                    if (budgetMs > 0 && (global::System.Diagnostics.Stopwatch.GetTimestamp() - t0) * tickMs >= budgetMs) break;
                }
                done = cursor >= chunks.Length;
            }
            finally
            {
                chunks.Dispose();
            }

            for (int i = 0; i < s_InvalidatedNow.Count; i++)
            {
                var e = s_InvalidatedNow[i];
                if (TrafficState.InvalidatedCount.Count >= TrafficConst.kInvalidatedTrackMax) TrafficState.InvalidatedCount.Clear();
                TrafficState.InvalidatedCount.TryGetValue(e, out int n);
                TrafficState.InvalidatedCount[e] = n + 1;
                if (n == 1) { r.Reinvalidated++; TrafficState.Reinvalidated++; }   // counted once per entity
                if (n == 0 && reroutedByProject != null)
                {
                    uint pid = s_InvalidatedProject[i];
                    reroutedByProject.TryGetValue(pid, out int c);
                    reroutedByProject[pid] = c + 1;
                }
            }
            s_InvalidatedNow.Clear();
            s_InvalidatedProject.Clear();
            r.Ms += (global::System.Diagnostics.Stopwatch.GetTimestamp() - t0) * tickMs;
            return done;
        }
    }
}
