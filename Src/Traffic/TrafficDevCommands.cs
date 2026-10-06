#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using NetCarLane = Game.Net.CarLane;
using NetParkingLane = Game.Net.ParkingLane;
using NetPedestrianLane = Game.Net.PedestrianLane;
using NetTrackLane = Game.Net.TrackLane;

// Traffic module dev commands: rrw.tr.*. Output lines start with "dev rrw tr".
// Site addressing (local copy of the rrw.* site rules in Src/Dev/DevSites.cs; Src/Dev is not referenced):
//   all | p<id> | #<n> (n-th project by id) | e<index> (that edge; any road edge) | r<n> (road index of the dev "list" -> its project,
//   or the road itself when it is not a works road). Several sites: comma separated ("e123,e456", "r3,r4").
// Lane filters (rrw.tr.close / paths / inval / probe): dir=+|- (car / track lanes whose curve tangent agrees / disagrees with
//   the edge tangent), lanes=car|ped|park|track|all (default all), side=L|R (pedestrian / parking lanes by the sign of the lane
//   midpoint lateral in the EDGE frame), humans (paths / inval: only human path owners; implies the pedestrian lanes).
namespace RealisticRoadWorks.V3.Traffic
{
    internal static class TrafficDevSites
    {
        public static List<Entity> Edges(DevContext ctx, string spec)
        {
            if (string.IsNullOrEmpty(spec)) throw new ArgumentException("missing <site> (all | p<id> | #n | e<index> | r<n>, comma separated)");
            var list = new List<Entity>();
            foreach (var part in spec.Split(','))
            {
                if (part.Length == 0) continue;
                foreach (var e in One(ctx, part)) if (!list.Contains(e)) list.Add(e);
            }
            return list;
        }

        private static List<Entity> One(DevContext ctx, string spec)
        {
            var em = ctx.EntityManager;
            var list = new List<Entity>();
            if (spec == "all")
            {
                foreach (var e in SiteRegistry.Edges.Keys) list.Add(e);
                return list;
            }
            char c = char.ToLowerInvariant(spec[0]);
            if (c == 'p' || c == '#')
            {
                ProjectRecord proj = null;
                int n = int.Parse(spec.Substring(1), CultureInfo.InvariantCulture);
                if (c == 'p') SiteRegistry.TryGetProject((uint)n, out proj);
                else
                {
                    var ids = new List<uint>(SiteRegistry.Projects.Keys);
                    ids.Sort();
                    if (n >= 0 && n < ids.Count) SiteRegistry.TryGetProject(ids[n], out proj);
                }
                if (proj == null) throw new ArgumentException("no project " + spec + " (rrw.list)");
                list.AddRange(proj.Edges);
                return list;
            }
            if (c == 'e' || c == 'r')
            {
                Entity edge = Entity.Null;
                if (c == 'e')
                {
                    int index = int.Parse(spec.Substring(1).Split(':')[0], CultureInfo.InvariantCulture);
                    foreach (var e in SiteRegistry.Edges.Keys) if (e.Index == index) edge = e;
                    if (edge == Entity.Null) foreach (var e in ctx.Roads()) if (e.Index == index) { edge = e; break; }
                }
                else
                {
                    var roads = ctx.Roads();
                    int n = int.Parse(spec.Substring(1), CultureInfo.InvariantCulture);
                    if (n >= 0 && n < roads.Count) edge = roads[n];
                }
                if (edge == Entity.Null || !em.Exists(edge)) throw new ArgumentException("no road edge " + spec);
                if (c == 'r' && SiteRegistry.TryGetEdge(edge, out var rec) && SiteRegistry.TryGetProject(rec.ProjectId, out var p)) list.AddRange(p.Edges);
                else list.Add(edge);
                return list;
            }
            throw new ArgumentException("bad site '" + spec + "' (all | p<id> | #n | e<index> | r<n>)");
        }

        public static string Arg(string[] a, int i) => a != null && a.Length > i ? a[i] : null;

        public static string Opt(string[] a, string key, string def = null)
        {
            if (a == null) return def;
            string k = key + "=";
            foreach (var s in a) if (s != null && s.StartsWith(k, StringComparison.OrdinalIgnoreCase)) return s.Substring(k.Length);
            return def;
        }

        public static bool Flag(string[] a, string name)
        {
            if (a == null) return false;
            foreach (var s in a) if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Lane groups of any edge: the works edge's own (registry) or a fresh measurement (edge frame = chain frame).
        public static LaneGroups GroupsOf(EntityManager em, Entity edge)
        {
            if (TrafficState.Closures.TryGetValue(edge, out var cl) && cl.Groups != null) return cl.Groups;
            if (SiteRegistry.TryGetEdge(edge, out var rec))
            {
                var slot = rec.Get<TrafficEdgeSlot>(ModuleSlot.Traffic);
                if (slot != null && slot.Groups.Ready) return slot.Groups;
            }
            var g = new LaneGroups();
            g.SyncFromEdge(em, edge, RRWCity.LeftHandTraffic);
            return g;
        }
    }

    // dir= / lanes= / side= / humans lane filter (see the header).
    internal sealed class TrafficLaneFilter
    {
        public int Dir;                 // 0 any, +1 with the edge, -1 against
        public bool Car = true, Ped = true, Park = true, Track = true;
        public int Side;                // 0 any, -1 left, +1 right (pedestrian / parking lanes)
        public bool Humans;
        public bool Any => Dir != 0 || Side != 0 || !(Car && Ped && Park && Track);

        public static TrafficLaneFilter Parse(string[] a)
        {
            var f = new TrafficLaneFilter();
            string dir = TrafficDevSites.Opt(a, "dir");
            if (dir == "+") f.Dir = 1; else if (dir == "-") f.Dir = -1; else if (dir != null) throw new ArgumentException("dir=+|-");
            string lanes = TrafficDevSites.Opt(a, "lanes", "all");
            if (lanes != "all")
            {
                f.Car = f.Ped = f.Park = f.Track = false;
                foreach (var k in lanes.Split('+', '|'))
                {
                    if (k == "car") f.Car = true; else if (k == "ped") f.Ped = true; else if (k == "park") f.Park = true;
                    else if (k == "track") f.Track = true; else throw new ArgumentException("lanes=car|ped|park|track|all");
                }
            }
            string side = TrafficDevSites.Opt(a, "side");
            if (side != null)
            {
                side = side.ToUpperInvariant();
                if (side == "L") f.Side = -1; else if (side == "R") f.Side = 1; else throw new ArgumentException("side=L|R");
            }
            f.Humans = TrafficDevSites.Flag(a, "humans");
            if (f.Humans && lanes == "all") { f.Car = f.Park = f.Track = false; f.Ped = true; }
            return f;
        }

        public bool Matches(EntityManager em, LaneGroups g, Entity lane)
        {
            if (!TrafficUtil.Alive(em, lane) || em.HasComponent<Temp>(lane)) return false;
            bool car = em.HasComponent<NetCarLane>(lane), ped = em.HasComponent<NetPedestrianLane>(lane);
            bool park = em.HasComponent<NetParkingLane>(lane), track = em.HasComponent<NetTrackLane>(lane);
            bool kind = (car && Car) || (ped && Ped) || (park && Park) || (track && Track);
            if (!kind) return false;
            if (Dir != 0 && (car || track))
            {
                int d = g != null ? g.DirOf(em, lane) : 0;
                if (d != Dir) return false;
            }
            if (Side != 0 && (ped || park))
            {
                if (g == null || !g.LateralOf(em, lane, out float lat)) return false;
                if (Side < 0 ? lat >= 0f : lat <= 0f) return false;
            }
            return true;
        }

        public override string ToString() =>
            "dir=" + (Dir == 0 ? "any" : Dir > 0 ? "+" : "-") + " lanes=" + (Car ? "car" : "") + (Ped ? "ped" : "") + (Park ? "park" : "") + (Track ? "track" : "") +
            " side=" + (Side == 0 ? "any" : Side < 0 ? "L" : "R") + (Humans ? " humans" : "");
    }

    public sealed class TrafficClassifyCommand : IDevCommand
    {
        public string Name => "rrw.tr.classify";
        public string Help => "rrw.tr.classify <site> - classification of each edge: dependants (buildings, stops, owner edge, cut-edge BFS) + zone classification cutEdge/track/stopZones/buildingZones";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            var edges = TrafficDevSites.Edges(ctx, TrafficDevSites.Arg(a, 0));
            foreach (var e in edges)
            {
                var r = TrafficAnalysis.Classify(em, e);
                bool cut = TrafficAnalysis.CutEdgeOf(r);
                string desc = r.Describe();
                var g = TrafficDevSites.GroupsOf(em, e);
                var z = TrafficAnalysis.ClassifyZones(em, e, g);
                string stored = "";
                if (SiteRegistry.TryGetEdge(e, out var rec))
                {
                    var slot = rec.Get<TrafficEdgeSlot>(ModuleSlot.Traffic);
                    stored = " stored: dependants=" + (slot != null && slot.Classified ? slot.ClassifyReasons : "pending") + " recordDependants=" + rec.Dependants +
                             " classified=" + rec.Classified + " cutEdge=" + rec.CutEdge + " track=" + rec.HasTrack + " stopZones=" + TrafficUtil.Bits(rec.StopZones) +
                             " buildingZones=" + TrafficUtil.Bits(rec.BuildingZones) + " buildingCount=" + rec.BuildingCount + " classifyUpdate=" + rec.ClassifyUpdate;
                }
                ctx.Log("rrw tr classify " + desc + " | zones now: cutEdge=" + cut + " track=" + z.HasTrack + " stops=" + z.Stops + " stopZones=" + TrafficUtil.Bits(z.StopZones) +
                        " buildingZones=" + TrafficUtil.Bits(z.BuildingZones) + " buildings=" + z.BuildingCount +
                        " split=" + RRWLog.F(RoadZoneMath.DirSplitChain(g.Section, g.ChainReversed)) + "(" + g.Section.Verdict + ")" +
                        " chainReversed=" + g.ChainReversed + stored);
            }
        }
    }

    public sealed class TrafficInvalCommand : IDevCommand
    {
        public string Name => "rrw.tr.inval";
        public string Help => "rrw.tr.inval <0|1> - switch in-flight path invalidation (product switch: RerouteInFlight) | " +
                              "rrw.tr.inval <site> [dir=+|-] [lanes=..] [side=L|R] [humans] - one-shot lane-keyed invalidation of the paths over that " +
                              "lane subset now (e.g. drain one sidewalk: rrw.tr.inval <edge> humans side=L)";
        public void Run(DevContext ctx, string[] a)
        {
            var v = TrafficDevSites.Arg(a, 0);
            if (v == null || v == "0" || v == "1" || v == "on" || v == "off" || v == "true" || v == "false")
            {
                if (v != null) TrafficState.InvalidationOn = v == "1" || v == "on" || v == "true";
                ctx.Log("rrw tr inval=" + (TrafficState.InvalidationOn ? 1 : 0) + " totalInvalidated=" + TrafficState.InvalidatedTotal +
                        " reinvalidated=" + TrafficState.Reinvalidated + " passes=" + TrafficState.Passes + " eventPasses=" + TrafficState.EventPasses);
                return;
            }
            var em = ctx.EntityManager;
            var f = TrafficLaneFilter.Parse(a);
            var map = new Dictionary<Entity, uint>();
            var lanes = new List<Entity>();
            var edges = TrafficDevSites.Edges(ctx, v);
            foreach (var e in edges)
            {
                var g = TrafficDevSites.GroupsOf(em, e);
                uint pid = SiteRegistry.TryGetEdge(e, out var rec) ? rec.ProjectId : 0u;
                TrafficUtil.LanesInto(em, e, lanes);
                foreach (var l in lanes) if (f.Matches(em, g, l)) map[l] = pid;
            }
            em.CompleteAllTrackedJobs();
            var r = new PathScanResult();
            int cursor = 0;
            TrafficPaths.ScanSlice(em, TrafficDevPathQuery.Get(ctx), map, PathScanMode.Invalidate, TrafficConst.kScanAhead, ref cursor, 0, r, null,
                                   f.Humans ? 2 : 7);
            TrafficState.InvalidatedTotal += r.Invalidated;
            ctx.Log("rrw tr inval now edges=" + edges.Count + " lanes=" + map.Count + " " + f + " invalidated=" + r.Invalidated +
                    " reinvalidated=" + r.Reinvalidated + " skippedOnLane=" + r.SkippedOnLane + " skippedDest=" + r.SkippedDest + " | " + r.Summary());
        }
    }

    internal static class TrafficDevPathQuery
    {
        private static EntityQuery s_Query;
        private static World s_World;
        public static EntityQuery Get(DevContext ctx)
        {
            if (s_World != ctx.World) { s_World = ctx.World; s_Query = TrafficPaths.CreateQuery(ctx.EntityManager); }
            return s_Query;
        }
    }

    public sealed class TrafficStatusCommand : IDevCommand
    {
        public string Name => "rrw.tr.status";
        public string Help => "rrw.tr.status - closures, sentinel, experimental switches, invalidation and relocation counters; per project the drain report";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            int slow = 0, closed = 0, hard = 0, parkClosed = 0, unconfirmed = 0, dev = 0, withB = 0, withSoft = 0;
            foreach (var cl in TrafficState.Closures.Values)
            {
                if (cl.IsDev) { dev++; continue; }
                if (cl.Level == ClosureLevel.Closed) closed++; else slow++;
                if (cl.HardActive) hard++;
                if (cl.ParkClosed) parkClosed++;
                if (!cl.LanesConfirmed) unconfirmed++;
                if (cl.ClosedB != RoadZones.None) withB++;
                if (cl.Soft != RoadZones.None) withSoft++;
            }
            var s = TrafficSettings.Read();
            ctx.Log("rrw tr status closures=" + TrafficState.Closures.Count + " closed=" + closed + " slow=" + slow + " hard=" + hard + " closedB=" + withB +
                    " soft=" + withSoft + " dev=" + dev + " parkingDisabled=" + parkClosed + " unconfirmed=" + unconfirmed + " snaps=" + TrafficState.Snaps.Count +
                    " forbiddenSnaps=" + TrafficState.ForbiddenSnap.Count + " forbiddenEverUsed=" + TrafficState.ForbiddenEverUsed +
                    " sentinel=" + (TrafficState.SentinelValid(em) ? RRWLog.E(TrafficState.Sentinel) : "none") +
                    " disabled=" + TrafficState.Disabled + " inval=" + TrafficState.InvalidationOn + " reroute=" + s.Reroute +
                    " relocate=" + s.Relocate + " hardSetting=" + s.HardClose + " workZone=" + TrafficUtil.Kmh(s.SlowSpeed) + "km/h" +
                    " gates(closedb=" + RRWGates.ClosedB + " soft=" + RRWGates.Soft + " rev=" + RRWGates.Revision + ") lht=" + RRWCity.LeftHandTraffic +
                    " cityKnown=" + RRWCity.Known + " totalInvalidated=" + TrafficState.InvalidatedTotal + " reinvalidated=" + TrafficState.Reinvalidated +
                    " passes=" + TrafficState.Passes + " eventPasses=" + TrafficState.EventPasses + " relocated=" + TrafficState.RelocatedTotal);
            uint upd = RRWClock.UpdateIndex;
            foreach (var p in SiteRegistry.Projects.Values)
            {
                int drained = 0, applied = 0, waiting = 0, dep = 0, halfOpen = 0, stale = 0;
                RoadZones openApplied = RoadZones.None, softApplied = RoadZones.None, bApplied = RoadZones.None, ready = RoadZones.AllLanes;
                string u3 = null;
                foreach (var e in p.Edges)
                {
                    if (!SiteRegistry.TryGetEdge(e, out var rec)) continue;
                    if (rec.TrafficDrained) drained++;
                    if (rec.ClosureApplied == p.Closure) applied++;
                    waiting += rec.BuildingsWaiting;
                    if (rec.Dependants) dep++;
                    openApplied |= rec.OpenLanesApplied;
                    softApplied |= rec.SoftApplied;
                    bApplied |= rec.ClosedBApplied;
                    ready &= (rec.ZonesDrained & ~rec.ZonesTimedOut) | (RoadZones.AllLanes & ~rec.ZonesPresent);
                    if (unchecked(upd - rec.ZonesReportUpdate) > (uint)RRWConst.kZonesReportStaleUpdates) stale++;
                    if (rec.OpenLanesApplied != RoadZones.None) halfOpen++;
                    if (u3 == null && em.Exists(e) && em.HasComponent<RoadWorksRuntime>(e))
                    {
                        var rt = em.GetComponentData<RoadWorksRuntime>(e);
                        u3 = TrafficRequestSystem.U3Violation(p, rt.m_Phase, rt.m_ClosureTarget, upd);
                    }
                }
                ctx.Log("rrw tr p" + p.Id + " " + p.Kind + " mode=" + p.Mode + " phase=" + p.Phase + " closure=" + p.Closure + " applied=" + applied + "/" + p.Edges.Count +
                        " openLanes=" + TrafficUtil.Bits(p.OpenLanes) + " softZones=" + TrafficUtil.Bits(p.SoftZones) + " openApplied=" + TrafficUtil.Bits(openApplied) +
                        " (" + halfOpen + " edges) softApplied=" + TrafficUtil.Bits(softApplied) + " closedBApplied=" + TrafficUtil.Bits(bApplied) +
                        " drainedOnEveryEdge=" + TrafficUtil.Bits(ready & RoadZones.AllLanes) + " staleReports=" + stale +
                        " workZonesReady(Director)=" + TrafficUtil.Bits(p.WorkZonesReady) +
                        " clearing=" + p.ClearingTraffic + " trafficDrained=" + drained + "/" + p.Edges.Count + " dependants=" + dep +
                        " buildingsWaiting=" + waiting + " rerouted=" + p.Rerouted + " parkedMoved=" + p.ParkedMoved +
                        TrafficRequestSystem.MachineText(p, upd) + " u3=" + (u3 ?? "ok"));
            }
        }
    }

    // Per-lane check of the applied closure (group / state / drained / dir per lane). MATCH when every lane
    // carries what its state requires; on an edge without closure: VANILLA (no sentinel, no Forbidden of ours).
    public sealed class TrafficProbeCommand : IDevCommand
    {
        public string Name => "rrw.tr.probe";
        public string Help => "rrw.tr.probe <site> [v] [dir=..] [lanes=..] [side=..] - lane fields vs the applied state per lane group " +
                              "(OPEN/SOFT/CLOSED-S/CLOSED-B, drained, dir) => MATCH|MISMATCH (no closure: VANILLA|NOT_VANILLA); v: one line per lane";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            bool verbose = TrafficDevSites.Flag(a, "v");
            var f = TrafficLaneFilter.Parse(a);
            var sentinel = TrafficState.Sentinel;
            foreach (var edge in TrafficDevSites.Edges(ctx, TrafficDevSites.Arg(a, 0)))
            {
                TrafficState.Closures.TryGetValue(edge, out var cl);
                var g = TrafficDevSites.GroupsOf(em, edge);
                SiteRegistry.TryGetEdge(edge, out var rec);
                int checks = 0, ok = 0, forbidden = 0, blocked = 0, sentinelRefs = 0, stale = 0;
                int[] states = new int[5];
                var lanes = new List<Entity>();
                TrafficUtil.LanesInto(em, edge, lanes);
                foreach (var lane in lanes)
                {
                    if (!f.Matches(em, g, lane)) continue;
                    var st = cl != null ? cl.StateOf(em, lane) : LaneState.Vanilla;
                    states[(int)st]++;
                    var sb = new StringBuilder();
                    var grp = g != null ? g.Of(em, lane) : RoadZones.None;
                    sbyte dir = g != null ? g.DirOf(em, lane) : (sbyte)0;
                    bool drained = rec != null && (grp & RoadZones.AllLanes) != 0 && ((grp & RoadZones.AllLanes) & ~rec.ZonesDrained) == 0;
                    sb.Append(" group=").Append(TrafficUtil.Bits(grp)).Append(" state=").Append(StateName(st, cl))
                      .Append(" drained=").Append(drained).Append(" dir=").Append(dir > 0 ? "+" : dir < 0 ? "-" : "?");
                    if (g != null && g.LateralOf(em, lane, out float lat)) sb.Append(" lat=").Append(RRWLog.F(lat));
                    bool pass = TrafficApply.LaneMatches(em, lane, cl, sentinel, sb);
                    checks++; if (pass) ok++;
                    if (TrafficApply.ReferencesSentinel(em, lane, sentinel)) sentinelRefs++;
                    if (em.HasComponent<NetCarLane>(lane))
                    {
                        var c = em.GetComponentData<NetCarLane>(lane);
                        if ((c.m_Flags & Game.Net.CarLaneFlags.Forbidden) != 0) forbidden++;
                        if (TrafficApply.FullBlock(c)) blocked++;
                        // A restriction pointing at an entity that no longer exists (e.g. a stale sentinel after a mod-less load).
                        if (c.m_AccessRestriction != Entity.Null && !em.Exists(c.m_AccessRestriction)) stale++;
                    }
                    if (verbose) ctx.Log("rrw tr probe   lane " + RRWLog.E(lane) + sb);
                }
                string report = rec != null
                    ? " present=" + TrafficUtil.Bits(rec.ZonesPresent) + " drained=" + TrafficUtil.Bits(rec.ZonesDrained) + " timedOut=" + TrafficUtil.Bits(rec.ZonesTimedOut) +
                      " parked=" + TrafficUtil.Bits(rec.ZonesParked) + " openApplied=" + TrafficUtil.Bits(rec.OpenLanesApplied) +
                      " softApplied=" + TrafficUtil.Bits(rec.SoftApplied) + " closedBApplied=" + TrafficUtil.Bits(rec.ClosedBApplied) +
                      " reportAge=" + (rec.ZonesReportUpdate == 0 ? "never" : unchecked(RRWClock.UpdateIndex - rec.ZonesReportUpdate).ToString())
                    : " (not a works edge)";
                string verdict = cl != null ? (ok == checks ? "MATCH" : "MISMATCH")
                                            : (ok == checks && stale == 0 ? "VANILLA" : "NOT_VANILLA");
                ctx.Log("rrw tr probe e" + edge.Index + " level=" + (cl != null ? cl.State : "Open") + " parking=" + (cl != null && cl.ParkClosed ? "disabled" : "untouched") +
                        " lanes(open/slow/soft/closedS/closedB)=" + states[0] + "/" + states[1] + "/" + states[2] + "/" + states[3] + "/" + states[4] +
                        " sentinelRefs=" + sentinelRefs + " fullBlockage=" + blocked + " forbidden=" + forbidden + " staleRestrictions=" + stale +
                        (f.Any ? " filter(" + f + ")" : "") + report + " checks=" + ok + "/" + checks + " => " + verdict);
            }
        }

        private static string StateName(LaneState st, EdgeClosure cl)
        {
            switch (st)
            {
                case LaneState.Slow: return cl != null && cl.Level == ClosureLevel.SlowZone ? "SLOW" : "OPEN";
                case LaneState.Soft: return "SOFT(" + (cl != null ? cl.SoftModeApplied.ToString() : "?") + ")";
                case LaneState.ClosedS: return "CLOSED-S";
                case LaneState.ClosedB: return "CLOSED-B";
                default: return "VANILLA";
            }
        }
    }

    // Path users of the site's lanes: onLane / nav / future / dest / stale / stuck per car/human/train.
    public sealed class TrafficPathsCommand : IDevCommand
    {
        public string Name => "rrw.tr.paths";
        public string Help => "rrw.tr.paths <site> [closed|open] [dir=+|-] [lanes=..] [side=L|R] [humans] - path users of the site's lanes " +
                              "(activeThrough must drop to 0 after a closure); closed/open: only lanes that carry a closed / open state; " +
                              "stuckOnLane = PathFlags.Stuck owners on the lanes; stoppedVehicles = non-moving, non-parked vehicles on the car lanes";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            var map = new Dictionary<Entity, uint>();
            var lanes = new List<Entity>();
            var edges = TrafficDevSites.Edges(ctx, TrafficDevSites.Arg(a, 0));
            string filter = TrafficDevSites.Flag(a, "closed") ? "closed" : TrafficDevSites.Flag(a, "open") ? "open" : null;
            var f = TrafficLaneFilter.Parse(a);
            int stopped = 0, moving = 0;
            var seen = new HashSet<Entity>();
            foreach (var e in edges)
            {
                TrafficUtil.LanesInto(em, e, lanes);
                TrafficState.Closures.TryGetValue(e, out var cl);
                var g = TrafficDevSites.GroupsOf(em, e);
                foreach (var l in lanes)
                {
                    if (!f.Matches(em, g, l)) continue;
                    bool open = cl == null || cl.LaneIsOpen(em, l);
                    if (filter == "closed" && open) continue;
                    if (filter == "open" && !open) continue;
                    map[l] = 0u;
                    if (!em.HasComponent<NetCarLane>(l) || !em.HasBuffer<LaneObject>(l)) continue;
                    var buf = em.GetBuffer<LaneObject>(l, true);
                    for (int i = 0; i < buf.Length; i++)
                    {
                        var o = buf[i].m_LaneObject;
                        if (!seen.Add(o) || !em.Exists(o) || em.HasComponent<Game.Vehicles.ParkedCar>(o)) continue;
                        if (em.HasComponent<Game.Objects.Moving>(o)) moving++; else stopped++;
                    }
                }
            }
            em.CompleteAllTrackedJobs();
            var r = new PathScanResult();
            int cursor = 0;
            TrafficPaths.ScanSlice(em, TrafficDevPathQuery.Get(ctx), map, PathScanMode.Count, TrafficConst.kScanAhead, ref cursor, 0, r, null, f.Humans ? 2 : 7);
            ctx.Log("rrw tr paths edges=" + edges.Count + " lanes=" + map.Count + (filter != null ? " state=" + filter : "") + (f.Any ? " " + f : "") +
                    " movingVehicles=" + moving + " stoppedVehicles=" + stopped + " " + r.Summary());
        }
    }

    // ---------------------------------------------------------------------------------------------- rrw.tr.close / open
    // Dev closures on the shipped closure path, for testing CLOSED-B, sidewalk drain and soft modes on NON-works edges. The closure is
    // an EdgeClosure with explicit per-lane states; it goes through the same LaneClosure / ParkingClosure systems, the same
    // save guard (sentinel, blockage, Forbidden snapshot, parking free space) and the same invalidation event as works closures.
    // Works edges are refused (the Director owns them; a dev closure on an edge that becomes a works road is dropped).
    internal static class TrafficDevClose
    {
        public static LaneState Parse(string s)
        {
            switch (s)
            {
                case "open": return LaneState.Vanilla;
                case "slow": return LaneState.Slow;
                case "soft": return LaneState.Soft;
                case "closed": return LaneState.ClosedS;
                case "blocked": return LaneState.ClosedB;
                default: throw new ArgumentException("state open|slow|soft|closed|blocked");
            }
        }

        public static bool Narrows(LaneState from, LaneState to) =>
            TrafficApply.Closes(to) && (from == LaneState.Vanilla || from == LaneState.Slow || (from == LaneState.Soft && to != LaneState.Soft));

        public static void Apply(EntityManager em, uint upd, List<Entity> edges, LaneState state, TrafficLaneFilter f, float slowMs, string tag)
        {
            var lanes = new List<Entity>();
            int changedTotal = 0, ev = 0;
            foreach (var edge in edges)
            {
                if (!TrafficUtil.Alive(em, edge)) continue;
                if (SiteRegistry.Edges.ContainsKey(edge))
                {
                    RRWLog.Dev("dev rrw tr close " + tag + " e" + edge.Index + " REFUSED: works edge of p" + SiteRegistry.Edges[edge].ProjectId + " (the Director owns it)");
                    continue;
                }
                TrafficState.Closures.TryGetValue(edge, out var cl);
                if (cl != null && !cl.IsDev) { RRWLog.Dev("dev rrw tr close " + tag + " e" + edge.Index + " REFUSED: product closure"); continue; }
                bool isNew = cl == null;
                if (isNew)
                {
                    if (state == LaneState.Vanilla) { RRWLog.Dev("dev rrw tr close " + tag + " e" + edge.Index + " nothing to open"); continue; }
                    cl = new EdgeClosure { Edge = edge, Level = ClosureLevel.Closed, DevLanes = new Dictionary<Entity, LaneState>(), Groups = new LaneGroups() };
                }
                cl.Groups.SyncFromEdge(em, edge, RRWCity.LeftHandTraffic);
                cl.SlowSpeed = slowMs;
                cl.Caution = true;
                cl.SoftModeApplied = RRWGates.Soft;
                TrafficUtil.LanesInto(em, edge, lanes);
                int changed = 0, matched = 0;
                var narrowed = new List<Entity>();
                foreach (var lane in lanes)
                {
                    if (!f.Matches(em, cl.Groups, lane)) continue;
                    matched++;
                    var from = cl.DevLanes.TryGetValue(lane, out var cur) ? cur : LaneState.Vanilla;
                    if (from == state) continue;
                    if (state == LaneState.Vanilla) cl.DevLanes.Remove(lane); else cl.DevLanes[lane] = state;
                    if (Narrows(from, state)) narrowed.Add(lane);
                    changed++;
                }
                cl.RecountDevBlocked();
                changedTotal += changed;
                if (cl.DevLanes.Count == 0)
                {
                    if (!isNew) TrafficUtil.OpenClosure(em, edge, true, upd, "dev open " + tag);
                    RRWLog.Dev("dev rrw tr close " + tag + " e" + edge.Index + " -> all lanes vanilla (closure removed; verify in " + TrafficConst.kVerifyDelayUpdates + " updates)");
                    continue;
                }
                if (isNew) TrafficState.Closures[edge] = cl;
                TrafficState.EnsureSentinel(em);
                if (changed > 0 || isNew)
                {
                    string detail = TrafficUtil.RefreshClosure(em, cl, false, upd);
                    TrafficState.Recount();
                    foreach (var l in narrowed) { TrafficState.EventLanes[l] = 0u; ev++; }
                    if (narrowed.Count > 0) TrafficState.InvalidationEventPending = true;
                    RRWLog.Dev("dev rrw tr close " + tag + " e" + edge.Index + " " + state + " " + f + " matched=" + matched + " changed=" + changed +
                               " devLanes=" + cl.DevLanes.Count + " narrowed=" + narrowed.Count + " soft=" + cl.SoftModeApplied + " refreshed " + detail);
                }
            }
            RRWLog.Dev("dev rrw tr close " + tag + " done: lanes changed=" + changedTotal + " eventLanes=" + ev +
                       " (rrw.tr.probe <edges> v shows the states; rrw.tr.open <edges> restores)");
        }

        public static void Open(EntityManager em, uint upd, List<Entity> edges, string tag)
        {
            int n = 0;
            foreach (var edge in edges)
            {
                if (!TrafficState.Closures.TryGetValue(edge, out var cl) || !cl.IsDev) continue;
                TrafficUtil.OpenClosure(em, edge, TrafficUtil.Alive(em, edge), upd, "dev open " + tag);
                n++;
            }
            RRWLog.Dev("dev rrw tr open " + tag + " opened " + n + " dev closures (verify " + TrafficConst.kVerifyDelayUpdates +
                       " updates later: 'traffic restore check ... VANILLA_OK' with verbose logging, or rrw.tr.probe <edges> => VANILLA)");
        }
    }

    public sealed class TrafficCloseCommand : IDevCommand
    {
        public string Name => "rrw.tr.close";
        public string Help => "rrw.tr.close <edges> <open|slow|soft|closed|blocked> [dir=+|-] [lanes=car|ped|park|track|all] [side=L|R] [kmh=30] [tag=] - " +
                              "dev closure of a lane subset on NON-works edges: closed = sentinel (CLOSED-S), blocked = blockage 0..255 without " +
                              "sentinel (CLOSED-B; close + wait for 'rrw.tr.paths <e> dir=.. ' moving=0 first), soft = SOFT per 'rrw.gate soft', slow = SlowZone, " +
                              "open = vanilla. Applied at the next Mod1; save-guarded like works closures";
        public void Run(DevContext ctx, string[] a)
        {
            if (a.Length < 2) { ctx.Log("usage: " + Help); return; }
            var edges = TrafficDevSites.Edges(ctx, a[0]);
            var state = TrafficDevClose.Parse(a[1]);
            var f = TrafficLaneFilter.Parse(a);
            float kmh = float.Parse(TrafficDevSites.Opt(a, "kmh", "30"), CultureInfo.InvariantCulture);
            string tag = TrafficDevSites.Opt(a, "tag", "TR");
            TrafficState.DevOps.Add((em, upd) => TrafficDevClose.Apply(em, upd, edges, state, f, kmh / 3.6f, tag));
            ctx.Log("rrw tr close queued edges=" + edges.Count + " state=" + state + " " + f + " (applied at the next Modification1)");
        }
    }

    public sealed class TrafficOpenCommand : IDevCommand
    {
        public string Name => "rrw.tr.open";
        public string Help => "rrw.tr.open <edges|all> [tag=] - remove dev closures (rrw.tr.close): vanilla refresh, Forbidden restore, verify 3 updates later";
        public void Run(DevContext ctx, string[] a)
        {
            if (a.Length < 1) { ctx.Log("usage: " + Help); return; }
            List<Entity> edges;
            if (a[0] == "all")
            {
                edges = new List<Entity>();
                foreach (var kv in TrafficState.Closures) if (kv.Value.IsDev) edges.Add(kv.Key);
            }
            else edges = TrafficDevSites.Edges(ctx, a[0]);
            string tag = TrafficDevSites.Opt(a, "tag", "TR");
            TrafficState.DevOps.Add((em, upd) => TrafficDevClose.Open(em, upd, edges, tag));
            ctx.Log("rrw tr open queued edges=" + edges.Count);
        }
    }
}
#endif
