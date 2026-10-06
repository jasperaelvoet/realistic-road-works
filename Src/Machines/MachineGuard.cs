using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

// The runtime separation guard (keeps machines, trucks above all, from driving through each other).
// Motion is a pure function of the committed plans and the mover writes them blindly, so without a guard any planner gap would become a
// visible interpenetration. Every update, after all projects planned (step B) and before the plans are written to the
// entities (step D), the guard predicts every pair of puppets - all projects, post-site / leaving ones included - over
// [now, now + horizon] (horizon = the faster one's stopping time + kGuardLatency) with the TRUE boxes + kGuardMargin and the
// pair rules of Choreo.PairHit (digger chassis / C3 feed pair). On a predicted contact one puppet yields:
//   1. a puppet standing still over [now, contact] never yields; the moving one does;
//   2. otherwise the lower rank yields (front owner at work 6 > truck loading/tipping/feeding 5 > truck returning 4 >
//      truck approaching 3 > crew truck / grader / others 2 > leaving 1); ties (deterministic): the lower ProjectId,
//      then the lower role, then the lower entity index;
//   3. the yield splices a stop at now (Choreo.BrakeSplice: Brake leg / K-turn cut at its cusp / stop in place) and the
//      puppet tries its role plan again every kGuardRetry s, moving only when that plan meets nobody for
//      kGuardReleaseWindow s (MachineDirectorSystem.ReleaseCheck, the truck planners' conflict check);
//   4. if the other one still runs into the stopped yielder, it stops too;
//   5. two puppets waiting for each other for kGuardDeadlockSeconds (or touching while both stand): the lower rank backs out
//      along its own lane (Choreo.BackOut: never into an open group, never outside the trims).
// Back-outs and holds never enter an open group or stop outside the footprint (the lateral is kept, u stays inside ClampU).
// No frozen machines: a puppet the guard holds (GuardHeld / StandStill) that has not moved for
// RRWConst.kMachineStuckSeconds is STUCK (ProjectRecord.MachinesStuck). Each stuck episode runs ONE deterministic step on the
// lower rank of the stuck puppet and its blocker: (1) it yields / (2) backs out <= kGuardBackOutMax along its lane into a slot
// clear of every resting box, only into WorkZonesReady (the other one tries if it cannot); (3) else it re-plans its role from
// scratch (a leaver takes the other connected exit); (4) after kStuckEpisodesMax failed episodes it is removed: made a leaver
// (removed when culled, else at its leave cap), logged Warn. Counters: rrw.mx.check.
namespace RealisticRoadWorks.V3.Machines
{
    public partial class MachineDirectorSystem
    {
        private struct GuardItem
        {
            public Puppet P;
            public Choreo.Obb Box;
            public float Speed;
            public float Reach;
            public float Horizon;
            public float2 Min, Max;       // swept XZ bounds of the box over [now, now + the global horizon] (+ slack)
            public bool Static;           // at rest in an open-ended hold (its box cannot move within the horizon)
            public ChainState State;      // chain state now (pair rules of the narrow phase)
        }

        // Broad phase on a world grid (kGuardCell) instead of all pairs; candidate pairs are deduplicated.
        private readonly Dictionary<long, int> m_CellHead = new Dictionary<long, int>(128);
        private readonly List<int> m_CellNext = new List<int>(256), m_CellItem = new List<int>(256);
        private readonly HashSet<long> m_PairSeen = new HashSet<long>();
        private readonly List<long> m_Pairs = new List<long>(128);

        private static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;

        private readonly List<GuardItem> m_GItems = new List<GuardItem>(64);
        private readonly List<Puppet> m_GList = new List<Puppet>(64);
        private readonly Dictionary<uint, MachineRole> m_Owners = new Dictionary<uint, MachineRole>();
        private readonly PlanContext m_GCtx = new PlanContext();      // box evaluation only (clock + track view)
        private readonly PlanContext m_OrphanCtx = new PlanContext(); // puppets whose project vanished
        private static readonly CrewState s_OrphanCrew = new CrewState();

        private void SeparationGuard(EntityManager em, double now)
        {
            var all = MachineRegistry.All;
            m_GItems.Clear();
            m_Owners.Clear();
            var bc = m_GCtx;
            bc.Em = em;
            bc.Clk = MachineRegistry.Clock;
            bc.Tv = MachineTrackStore.View();
            bc.Now = now;
            if (!bc.Tv.IsCreated) return;
            MxPerf.Begin(MxT.G_Boxes);
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p.Plan.Count <= 0 || p.Plan.Track < 0 || !p.Alive(em)) continue;
                if (!Choreo.BoxAt(p, p.Plan, bc, now, out var box, out var s)) continue;
                float a = math.min(RRWConst.kEmergencyDecel, math.max(0.3f, p.Accel * MxConst.kGuardDecelFactor));
                float v = math.abs(s.Speed);
                var leg = p.Plan.Get(s.Leg);
                bool st = v < 0.01f && leg.Kind == (byte)LegKind.Hold && leg.OpenEnded;
                m_GItems.Add(new GuardItem
                {
                    P = p, Box = box, Speed = v, Static = st, State = s,
                    Reach = math.sqrt(box.HL * box.HL + box.HW * box.HW) + MxConst.kGuardMargin,
                    Horizon = v / a + MxConst.kGuardLatency,
                });
            }
            int n = m_GItems.Count;
            MxPerf.Count(MxC.GuardItems, n);
            // Perf: broad phase on each puppet's PLANNED path, not its top speed over the window (with a top-speed test, two
            // trucks at 10 m/s pass at any distance under ~160 m, so nearly every pair would run
            // FirstContact every update). Each puppet's box is sampled every kGuardBroadStep over [now, now + Hmax]
            // (O(n)); the swept bounds get the box reach plus the distance it can cover between two samples.
            float hMax = 0f;
            for (int i = 0; i < n; i++) hMax = math.max(hMax, m_GItems[i].Horizon);
            for (int i = 0; i < n; i++)
            {
                var it = m_GItems[i];
                float2 lo = it.Box.C, hi = it.Box.C;
                float vMax = math.max(it.Speed, it.P.Speed);
                PlanSamples.Validate(it.P, bc.Clk, now);
                if (!it.Static)
                {
                    // the planned path from the plan sample cache (absolute grid, filled once per plan)
                    long k1 = PlanSamples.Ceil(now + hMax + kGuardBroadStep);
                    for (long k = PlanSamples.Ceil(now + 1e-6); k <= k1; k++)
                    {
                        MxPerf.Count(MxC.GuardBroadSamples);
                        if (!PlanSamples.BoxAt(it.P, k, bc, out var bt, out var st))
                        {
                            // cannot evaluate: fall back to the top-speed reach over the whole window
                            lo = math.min(lo, it.Box.C - vMax * hMax);
                            hi = math.max(hi, it.Box.C + vMax * hMax);
                            break;
                        }
                        lo = math.min(lo, bt.C);
                        hi = math.max(hi, bt.C);
                        vMax = math.max(vMax, math.abs(st.Speed));
                    }
                }
                float pad = it.Reach + vMax * (float)kGuardBroadStep + math.abs(it.P.BoxOffZ) + 0.5f;   // + pivot swing of the box centre
                it.Min = lo - pad;
                it.Max = hi + pad;
                m_GItems[i] = it;
            }
            MxPerf.End(MxT.G_Boxes);
            // spatial hash of the swept bounds (world grid kGuardCell), candidate pairs only
            MxPerf.Begin(MxT.G_Hash);
            m_CellHead.Clear(); m_CellNext.Clear(); m_CellItem.Clear(); m_PairSeen.Clear(); m_Pairs.Clear();
            float inv = 1f / MxConst.kGuardCell;
            for (int i = 0; i < n; i++)
            {
                var it = m_GItems[i];
                int x0 = (int)math.floor(it.Min.x * inv), x1 = (int)math.floor(it.Max.x * inv);
                int z0 = (int)math.floor(it.Min.y * inv), z1 = (int)math.floor(it.Max.y * inv);
                if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 64) { x1 = math.min(x1, x0 + 7); z1 = math.min(z1, z0 + 7); }   // huge sweep: clipped (pairs re-tested below)
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long key = CellKey(x, z);
                        int head = m_CellHead.TryGetValue(key, out int h) ? h : -1;
                        for (int e = head; e >= 0; e = m_CellNext[e])
                        {
                            int j = m_CellItem[e];
                            long pk = ((long)math.min(i, j) << 32) | (uint)math.max(i, j);
                            if (m_PairSeen.Add(pk)) m_Pairs.Add(pk);
                        }
                        m_CellNext.Add(head);
                        m_CellItem.Add(i);
                        m_CellHead[key] = m_CellItem.Count - 1;
                    }
            }
            MxPerf.End(MxT.G_Hash);
            MxPerf.Begin(MxT.G_Pairs);
            MxPerf.Count(MxC.GuardPairs, m_Pairs.Count);
            for (int q = 0; q < m_Pairs.Count; q++)
            {
                int i = (int)(m_Pairs[q] >> 32), j = (int)(m_Pairs[q] & 0xFFFFFFFF);
                var A = m_GItems[i];
                var B = m_GItems[j];
                if (math.any(A.Min > B.Max) || math.any(B.Min > A.Max)) continue;   // swept bounds apart: no contact in the window
                MxPerf.Count(MxC.GuardBroadPass);
                float T = math.max(A.Horizon, B.Horizon);
                double tc;
                if (A.Static && B.Static)
                {
                    // two puppets at rest in open-ended holds - their boxes cannot change within the horizon: one test now
                    MxPerf.Count(MxC.GuardStaticPairs);
                    if (!Choreo.PairOverlapAt(A.P, A.P.Plan, B.P, B.P.Plan, bc, now, MxConst.kGuardMargin)) continue;
                    tc = now;
                }
                else if (!FirstContactCached(A, B, bc, now, now + T, out tc)) continue;
                MxPerf.Count(MxC.GuardContacts);
                MxPerf.Begin(MxT.G_Resolve);
                try { Resolve(em, A.P, B.P, bc, now, tc, T); }
                catch (Exception e) { RRWLog.ErrorOnce("machines guard resolve", e); }
                MxPerf.End(MxT.G_Resolve);
            }
            MxPerf.End(MxT.G_Pairs);
            MxPerf.Begin(MxT.G_Deadlocks);
            // leavers heading for each other in one lane switch exits BEFORE the guard's back-out loop (which re-planned them
            // into the same head-on conflict again and again)
            HeadOnPass(em, now);
            Deadlocks(em, now);
            // a mover held by a STANDING blocker of lower make-way priority (or two machines standing on top of each other)
            MakeWayPass(em, now);
            MxPerf.End(MxT.G_Deadlocks);
            MxPerf.Begin(MxT.G_RetryLeavers);
            RetryLeavers(em, now);
            MxPerf.End(MxT.G_RetryLeavers);
            MxPerf.Begin(MxT.G_Stuck);
            StuckPass(em, now);
            MxPerf.End(MxT.G_Stuck);
        }

        private const double kGuardBroadStep = 0.5;   // s between the broad-phase path samples of one puppet

        private static bool FirstContact(Puppet a, Puppet b, PlanContext bc, double from, double to, out double tc)
        {
            tc = double.PositiveInfinity;
            for (double t = from; t <= to + 1e-6; t += MxConst.kGuardStep)
            {
                MxPerf.Count(MxC.GuardContactSteps);
                if (Choreo.PairOverlapAt(a, a.Plan, b, b.Plan, bc, t, MxConst.kGuardMargin)) { tc = t; return true; }
            }
            return false;
        }

        // The narrow phase of the update pass on the plan sample caches: the exact boxes now, then the
        // absolute kSampleStep grid (boxes >= 4 m long at <= 11 m/s closing speed overlap for longer than one step). Resolve still
        // uses the exact FirstContact for the follow-up checks.
        private static bool FirstContactCached(in GuardItem A, in GuardItem B, PlanContext bc, double from, double to, out double tc)
        {
            tc = double.PositiveInfinity;
            MxPerf.Count(MxC.GuardContactSteps);
            if (Choreo.PairHit(A.P, A.State, A.Box, B.P, B.State, B.Box, MxConst.kGuardMargin)) { tc = from; return true; }
            long k1 = PlanSamples.Ceil(to);
            for (long k = PlanSamples.Ceil(from + 1e-6); k <= k1; k++)
            {
                MxPerf.Count(MxC.GuardContactSteps);
                if (!PlanSamples.BoxAt(A.P, k, bc, out var oa, out var sa) || !PlanSamples.BoxAt(B.P, k, bc, out var ob, out var sb)) continue;
                if (Choreo.PairHit(A.P, sa, oa, B.P, sb, ob, MxConst.kGuardMargin)) { tc = PlanSamples.Time(k); return true; }
            }
            return false;
        }

        private static float GapAt(Puppet a, Puppet b, PlanContext bc, double t)
        {
            if (!Choreo.BoxAt(a, a.Plan, bc, t, out var oa, out _) || !Choreo.BoxAt(b, b.Plan, bc, t, out var ob, out _)) return float.PositiveInfinity;
            return Choreo.Gap(oa, ob);
        }

        // Two touching puppets move apart over the next 0.5 s: the CENTRE distance grows (or the gap does). Gap alone is the
        // smallest-axis depth: two boxes overlapping sideways (the D0 breaker and its inline truck: -4.2 m across) keep that depth while
        // they separate lengthwise, so the guard would brake every back-out at its first frame and the pair would stay on top of each other.
        private static bool Separating(Puppet a, Puppet b, PlanContext bc, double now)
        {
            if (!Choreo.BoxAt(a, a.Plan, bc, now, out var a0, out _) || !Choreo.BoxAt(b, b.Plan, bc, now, out var b0, out _)) return false;
            if (!Choreo.BoxAt(a, a.Plan, bc, now + 0.5, out var a1, out _) || !Choreo.BoxAt(b, b.Plan, bc, now + 0.5, out var b1, out _)) return false;
            if (math.distance(a1.C, b1.C) > math.distance(a0.C, b0.C) + 0.01f) return true;
            return Choreo.Gap(a1, b1) > Choreo.Gap(a0, b0) + 0.01f;
        }

        private void Resolve(EntityManager em, Puppet a, Puppet b, PlanContext bc, double now, double tc, float horizon)
        {
            bool touching = tc <= now + 1e-6;
            double until = touching ? now + 1.0 : tc;
            bool sa = Stationary(a, bc, now, until), sb = Stationary(b, bc, now, until);
            if (touching)
            {
                // already in contact: braking cannot separate them. Moving apart (a back-out / make-way in progress): let them.
                if (Separating(a, b, bc, now)) return;
                MachineDebug.GuardContacts++;
                if (sa && sb)
                {
                    Puppet y = Yielder(a, b, now);
                    Puppet o = y == a ? b : a;
                    MarkWaiting(y, o, now);
                    MarkWaiting(o, y, now);
                    if (now - y.LastBackOut > 3.0) TryBackOut(em, y, o, now, "touching");
                    return;
                }
                Puppet mv = !sa && sb ? a : sa && !sb ? b : Yielder(a, b, now);
                Yield(em, mv, mv == a ? b : a, now, now, "touching");
                return;
            }
            // both (nearly) still until the contact - front-relative creep: the lower rank stops
            Puppet yl = sa && !sb ? b : sb && !sa ? a : Yielder(a, b, now);
            Puppet other = yl == a ? b : a;
            if (!Yield(em, yl, other, now, tc, "contact in " + RRWLog.F((float)(tc - now)) + " s")) return;
            // the other one still runs into the stopped yielder: it stops too (unless it stands still anyway)
            if (FirstContact(other, yl, bc, now, now + horizon, out double tc2) && !Stationary(other, bc, now, tc2))
                Yield(em, other, yl, now, tc2, "runs into the stopped " + yl.Role);
        }

        // Lower rank yields; ties (deterministic, no speed / random tie-break): the lower ProjectId,
        // then the lower role, then the lower entity index.
        private Puppet Yielder(Puppet a, Puppet b, double now)
        {
            int ra = Rank(a, now), rb = Rank(b, now);
            if (ra != rb) return ra < rb ? a : b;
            return TieLower(a, b) ? a : b;
        }

        private static bool TieLower(Puppet a, Puppet b)
        {
            if (a.ProjectId != b.ProjectId) return a.ProjectId < b.ProjectId;
            if (a.Role != b.Role) return (int)a.Role < (int)b.Role;
            return a.OrderKey <= b.OrderKey;   // rollers have no entity (unit id after every entity)
        }

        private int Rank(Puppet p, double now)
        {
            if (p.PostSite || p.Outgoing) return 1;
            MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
            var lk = (LegKind)s.LegKind;
            bool frontLeg = lk == LegKind.DigHop || lk == LegKind.Follow || lk == LegKind.Shuttle || lk == LegKind.Scrape;
            if (!p.IsTruck && frontLeg && p.Role == OwnerOf(p.ProjectId)) return 6;
            if (p.IsTruck)
            {
                if (s.Anim == (byte)AnimKind.Load && (lk == LegKind.Hold || lk == LegKind.DigHop || lk == LegKind.Follow)) return 5;
                return p.Stage == TruckStage.Return ? 4 : 3;
            }
            return 2;
        }

        private MachineRole OwnerOf(uint projectId)
        {
            if (m_Owners.TryGetValue(projectId, out var r)) return r;
            r = MachineRole.Count;
            if (SiteRegistry.TryGetProject(projectId, out var pr)) r = default(CrewPlan).FrontOwner(pr.Phase);   // the same role in every crew
            m_Owners[projectId] = r;
            return r;
        }

        // No motion over [from, to] (speed and pose).
        private static bool Stationary(Puppet p, PlanContext bc, double from, double to)
        {
            MachineMotion.State(p.Plan, bc.Clk, from, out var a);
            if (math.abs(a.Speed) > 0.15f) return false;
            MachineMotion.State(p.Plan, bc.Clk, 0.5 * (from + to), out var m);
            MachineMotion.State(p.Plan, bc.Clk, to, out var b);
            if (math.abs(m.Speed) > 0.15f || math.abs(b.Speed) > 0.15f) return false;
            return math.abs(b.U - a.U) < 0.1f && math.abs(b.Lat - a.Lat) < 0.1f && math.abs(m.U - a.U) < 0.1f && math.abs(m.Lat - a.Lat) < 0.1f;
        }

        // A full planning context for the puppet's project (or a minimal one on its own track when the project vanished).
        private PlanContext GuardContext(EntityManager em, Puppet p, double now)
        {
            if (SiteRegistry.TryGetProject(p.ProjectId, out var pr) && MachineTrackStore.TryGet(pr.Id, out var td) && td.Valid)
                return Context(em, pr, State(pr), td, now, p.Crew);
            var td2 = p.Track;
            if (td2 == null || !td2.Valid) return null;
            var c = m_OrphanCtx;
            c.Em = em;
            c.Project = null;
            c.View = default;
            c.Track = td2;
            c.Tv = MachineTrackStore.View();
            c.Clk = MachineRegistry.Clock;
            c.Now = now;
            c.Front = p.Plan.FrontA;
            c.Working = false;
            c.Trim0 = 0f;
            c.Trim1 = td2.U;
            c.Visible = c.KeepLeft = c.KeepRight = false;
            c.KeepParkL = c.KeepParkR = c.FenceL = c.FenceR = c.HalvesActive = false;
            c.Open = RoadZones.None;
            c.Ready = RoadZones.AllLanes;
            c.Keep = RoadZones.None;
            c.ReadyOk = true;
            c.NoSpawn = true;
            c.NoSpawnWhy = "project gone";
            c.Stage = default;
            c.Cam = m_Cam;
            c.Others.Clear();
            c.State = null;
            c.CS = s_OrphanCrew;
            c.CrewIndex = 0; c.Crews = 1;
            c.SecLo = 0f; c.SecHi = td2.U;
            c.FrontSpeed = 0f;
            return c;
        }

        private static void MarkWaiting(Puppet p, Puppet blocker, double now)
        {
            if (!p.GuardHeld && !p.StandStill) { p.GuardHeldSince = now; p.GuardBackOuts = 0; }
            if (double.IsNaN(p.WaitSince)) p.WaitSince = now;
            p.GuardHeld = true;
            p.GuardBlocker = blocker;
        }

        // Stop y now for `other`. False when y already stands / brakes (nothing changed).
        private bool Yield(EntityManager em, Puppet y, Puppet other, double now, double tc, string why)
        {
            var c = GuardContext(em, y, now);
            if (c == null) return false;
            var wanted = y.Plan;
            if (!Choreo.BrakeSplice(y, c, tc)) { MarkWaiting(y, other, now); return false; }
            // the motion the guard stopped is the path a standing blocker has to clear (make-way)
            y.Wanted = wanted;
            y.WantedTau = now;
            MxPerf.Count(MxC.GuardBrakes);
            MarkWaiting(y, other, now);
            y.ReplanAt = now + MxConst.kGuardRetry;
            if (y.IsTruck && !y.PostSite) StopTruckCycle(y);
            MachineDebug.GuardBrakes++;
            string msg = "machines: guard stops " + y + " for " + other + " (" + why + ")";
            MachineDebug.LastGuard = msg;
            if (MachineDebug.LogDespawns) RRWLog.Info(msg);
            else RRWLog.Verbose(msg);
            return true;
        }

        private static void StopTruckCycle(Puppet t)
        {
            t.OutAttempt = t.RetAttempt = -1;
            t.RetForced = 0;
            t.Pend.Drop();
            t.Stage = TruckStage.Home;
            t.Loads.Clear();
            t.Feeding = false;
            t.ZoneEnter = t.ZoneExit = double.NegativeInfinity;
            t.WorkStart = t.WorkEnd = double.NegativeInfinity;
        }

        private bool TryBackOut(EntityManager em, Puppet y, Puppet o, double now, string why)
        {
            if (y.GuardBackOuts >= 3) return false;
            MxPerf.Count(MxC.GuardBackOutTries);
            var c = GuardContext(em, y, now);
            if (c == null || c.Project == null || c.Track == null) return false;
            y.LastBackOut = now;
            // direction along y's own chain away from o (world space: o may belong to another project / chain)
            float away = -1f;
            if (Choreo.BoxAt(y, y.Plan, m_GCtx, now, out var by, out var sy) && Choreo.BoxAt(o, o.Plan, m_GCtx, now, out var bo, out _) &&
                c.Tv.Point(c.Track.Slot, sy.U, out var tp))
            {
                float along = math.dot(bo.C - by.C, tp.Dir);
                away = along > 0f ? -1f : 1f;
            }
            if (!Choreo.BackOut(y, c, away, o, out string where))
            {
                RRWLog.Once("machines-backout-fail-" + y.ProjectId + "-" + y.Role, "machines: guard cannot back " + y + " out of " + o + " (" + why + "): no free slot within " +
                            RRWLog.F(MxConst.kGuardBackOutMax) + " m along its lane");
                return false;
            }
            y.GuardBackOuts++;
            MarkWaiting(y, o, now);
            y.GuardHeldSince = now;
            if (y.IsTruck && !y.PostSite) StopTruckCycle(y);
            MachineDebug.GuardBackOuts++;
            string msg = "machines: guard backs " + y + " out of " + o + " (" + why + "): " + where;
            MachineDebug.LastGuard = msg;
            RRWLog.Once("machines-backout-" + y.ProjectId + "-" + y.Role, msg);
            if (MachineDebug.LogDespawns) RRWLog.Info(msg);
            else RRWLog.Verbose(msg);
            return true;
        }

        // Two puppets waiting for each other (guard hold or truck stand-still) for kGuardDeadlockSeconds: the lower rank backs out.
        private void Deadlocks(EntityManager em, double now)
        {
            m_GList.Clear();
            m_GList.AddRange(MachineRegistry.All);
            foreach (var p in m_GList)
            {
                if (!(p.GuardHeld || p.StandStill) || p.GuardBlocker == null) continue;
                var q = p.GuardBlocker;
                if (!MachineRegistry.All.Contains(q)) { p.GuardBlocker = null; continue; }
                if (now - p.GuardHeldSince < MxConst.kGuardDeadlockSeconds) continue;
                if (!(q.GuardHeld || q.StandStill)) continue;
                MachineDebug.GuardDeadlocks++;
                if (!(p.DeadlockWait == p.WaitSince) && !(q.DeadlockWait == q.WaitSince))
                {
                    MachineDebug.DeadlockEpisodes++;
                    RRWLog.Verbose("machines: deadlock " + p + " <-> " + q + " (waiting " + RRWLog.F((float)(now - p.GuardHeldSince)) + " s)");
                }
                p.DeadlockWait = p.WaitSince;
                q.DeadlockWait = q.WaitSince;
                Puppet y = Yielder(p, q, now);
                Puppet o = y == p ? q : p;
                if (!TryBackOut(em, y, o, now, "deadlock " + RRWLog.F((float)(now - p.GuardHeldSince)) + " s"))
                {
                    // the other one tries instead (it may have room behind it)
                    TryBackOut(em, o, y, now, "deadlock (other side)");
                }
                p.GuardHeldSince = now;
                q.GuardHeldSince = now;
            }
            m_GList.Clear();
        }

        // Leaving puppets the guard stopped try their drive-off again (they never return to a role).
        // The candidate drive-off's conflict check (up to kPlanCheckMax s) is a resumable scan inside the
        // per-update check budget: the leaver keeps standing (its held plan) until the scan of its candidate has finished; the
        // candidate holds a lead (Puppet.ScanLead) after a scan outlived one.
        private void RetryLeavers(EntityManager em, double now)
        {
            m_GList.Clear();
            foreach (var p in MachineRegistry.All) if (p.PostSite && (p.GuardHeld || p.LeaveContinue) && now >= p.ReplanAt) m_GList.Add(p);
            foreach (var p in m_GList)
            {
                var c = GuardContext(em, p, now);
                if (c == null || c.Project == null || float.IsNaN(p.LeaveEndU)) { p.ReplanAt = double.PositiveInfinity; p.LeaveContinue = false; p.Pend.Drop(); continue; }
                var pd = p.Pend;
                if (pd.For == ScanFor.Leave && pd.Scan.State == ScanState.Running)
                {
                    if (!pd.BaseIs(p.Plan)) pd.Drop();               // the guard changed the held plan meanwhile: a new candidate
                    else if (now > pd.CommitBy) Choreo.ScanExpired(p);
                }
                if (!(pd.For == ScanFor.Leave && pd.Scan.State == ScanState.Running))
                {
                    if (MxBudget.CheckExhausted) continue;   // next update
                    Choreo.ConvoyRelease(p, now);            // full speed again once the leader is gone
                    var saved = p.Plan;
                    double savedAt = p.ReplanAt;
                    Choreo.PlanLeave(p, c, p.LeaveEndU, out _, out bool more, Choreo.LeaveLead(p, now), p.LeaveVmax);
                    var cand = p.Plan;
                    pd.For = ScanFor.Leave;
                    pd.Attempt = 0;
                    pd.More = more;
                    pd.ReplanAt = p.ReplanAt;
                    pd.CommitBy = Choreo.CommitBy(cand, saved);
                    pd.SetBase(saved);
                    p.Plan = saved;          // the leaver keeps its committed plan while the scan runs
                    p.PlanDirty = true;
                    p.ReplanAt = savedAt;
                    double to = math.min(MachineMotion.MotionEnd(cand) + 1.0, now + MxConst.kPlanCheckMax);
                    Choreo.ScanStart(pd.Scan, p, cand, c, now, math.max(to, now + 1.0), MxConst.kGuardMargin, null);
                }
                long t0 = MxBudget.Start();
                var st = Choreo.ScanRun(pd.Scan, c, MxBudget.CheckDeadline(t0));
                MxBudget.StopCheck(t0);
                if (st == ScanState.Running) continue;
                pd.For = ScanFor.None;
                if (st == ScanState.Conflict)
                {
                    var who = pd.Scan.Who;
                    p.Wanted = pd.Scan.Plan; p.WantedTau = now;   // the drive-off it wants (make-way path)
                    p.ReplanAt = now + MxConst.kGuardRetry;
                    // convoy: a slower leaver ahead heading for the same exit - the next candidate drives at its pace (it never
                    // overtakes in the lane; the whole-route scan of a full-speed candidate conflicted until the slow one was gone)
                    if (p.ConvoyLeader != who && Choreo.ConvoyFollow(p, who, pd.Scan.Plan, now)) { MachineDebug.ConvoyFollows++; p.ReplanAt = now + 0.5; }
                    if (who != null) { MarkWaiting(p, who, now); p.GuardBlocker = who; }
                    pd.Drop();
                    continue;
                }
                var plan = pd.Scan.Plan;
                Choreo.RefreshModels(ref plan, pd, p.Plan);
                p.Plan = plan;
                p.PlanDirty = true;
                p.ReplanAt = pd.ReplanAt;
                p.LeaveContinue = pd.More;
                if (!pd.More) p.ReplanAt = double.PositiveInfinity;   // (a continuation keeps the ReplanAt PlanLeave set)
                p.ScanLead = 0f;
                pd.Drop();
                Choreo.ClearBlocked(p);
            }
            m_GList.Clear();
        }

        // ------------------------------------------------------------ head-on leavers

        private readonly List<Puppet> m_HeadOn = new List<Puppet>(16);

        // Every pair of leavers of a project still on their way out that head for each other in one lane (Choreo.HeadOn): one of them takes
        // the other's exit (Choreo.HeadOnChoice: the cheaper new route; never into another head-on meeting; at most two switches per
        // leaver). Applied through RetryLeavers (re-plan + conflict scan). A pair without a switch is re-evaluated after kGuardRetry s and
        // falls back to the deadlock / stuck resolution. Leavers are few: O(L^2) per project, evaluated only for travelling leavers.
        private void HeadOnPass(EntityManager em, double now)
        {
            m_HeadOn.Clear();
            foreach (var p in MachineRegistry.All) if (Choreo.Travelling(p, now)) m_HeadOn.Add(p);
            for (int i = 0; i < m_HeadOn.Count; i++)
                for (int j = i + 1; j < m_HeadOn.Count; j++)
                {
                    var a = m_HeadOn[i]; var b = m_HeadOn[j];
                    if (a.ProjectId != b.ProjectId || now < a.NextHeadOnTau || now < b.NextHeadOnTau) continue;
                    if (!MachineRegistry.All.Contains(a) || !MachineRegistry.All.Contains(b)) continue;
                    // cheap test first (states only); the planning context is built only for a pair that meets head-on
                    MachineMotion.State(a.Plan, MachineRegistry.Clock, now, out var sa);
                    MachineMotion.State(b.Plan, MachineRegistry.Clock, now, out var sb);
                    if (!Choreo.HeadOn(sa.U, sa.Lat, a.BoxHalfWid, a.BoxHalfLen + math.abs(a.BoxOffZ), a.LeaveEndU,
                                       sb.U, sb.Lat, b.BoxHalfWid, b.BoxHalfLen + math.abs(b.BoxOffZ), b.LeaveEndU)) continue;
                    var c = GuardContext(em, a, now);
                    if (c == null || c.Project == null || c.Track == null) continue;
                    // (ties: the side of the lower rank switches - Yielder's deterministic order)
                    bool aFirst = Yielder(a, b, now) == a;
                    if (!Choreo.HeadOnChoice(c, a, b, aFirst, out bool switchA, out float end, out string why))
                    {
                        if (why.Length > 0)
                        {
                            MachineDebug.HeadOnUnresolved++;
                            a.NextHeadOnTau = b.NextHeadOnTau = now + MxConst.kGuardRetry;
                            RRWLog.Verbose("machines: head-on leavers " + a + " <-> " + b + " not resolved by an exit switch (" + why + ")");
                        }
                        continue;
                    }
                    var o = switchA ? b : a;
                    var g = Choreo.HeadOnGroup;
                    for (int k = 0; k < g.Count; k++)
                    {
                        var y = g[k];
                        var cy = y.ProjectId == c.Project.Id ? c : GuardContext(em, y, now);
                        if (cy == null || cy.Project == null) continue;
                        MachineMotion.State(y.Plan, cy.Clk, now, out var ys);
                        sbyte facing = ys.Hu >= 0f ? (sbyte)1 : (sbyte)-1;
                        y.LeaveEndU = end;
                        y.ExitWhy = why;
                        y.OtherExitTried = true;
                        y.HeadOnSwitches++;
                        y.LeaveVmax = -1f;
                        y.ConvoyLeader = null;
                        y.ConvoyDepart = double.NaN;
                        ClassifyDeadEnd(y, cy, ys.U, (end - ys.U) >= 0f ? (sbyte)1 : (sbyte)-1, end);
                        if (!double.IsNaN(y.LeaveTau)) y.LeaveCap = math.max(y.LeaveCap, (now - y.LeaveTau) + Choreo.LeaveNeedSeconds(y, cy, ys.U, facing, end, now));
                        y.Pend.Drop();
                        Choreo.ClearBlocked(y);   // (no deadlock back-out for this pair this update: the switch resolves it)
                        y.LeaveContinue = true;   // RetryLeavers re-plans the drive-off to the new exit (conflict-scanned)
                        // the member nearest the new exit re-plans first (it leads the convoy; the ones behind follow at its pace)
                        int rank = 0;
                        for (int m = 0; m < g.Count; m++)
                        {
                            if (m == k) continue;
                            MachineMotion.State(g[m].Plan, cy.Clk, now, out var ms);
                            if (math.abs(end - ms.U) < math.abs(end - ys.U)) rank++;
                        }
                        y.ReplanAt = now + 0.05 * rank;
                        y.NextHeadOnTau = now + MxConst.kGuardRetry;
                        MachineDebug.HeadOnSwitches++;
                    }
                    o.ReplanAt = math.min(o.ReplanAt, now + 0.5);
                    o.NextHeadOnTau = now + MxConst.kGuardRetry;
                    RRWLog.Info("machines: leaver " + g[0] + " " + why);
                }
            m_HeadOn.Clear();
        }

        // ------------------------------------------------------------ make-way

        // A puppet the guard holds (GuardHeld / StandStill) for kMakeWaySeconds by a blocker that STANDS (no motion over the next
        // kMakeWayStillWindow s): the blocker moves aside when the held one has the higher make-way priority (Choreo.MoverPriority >
        // BlockerPriority: front owner 5 > truck 4 > working role 3 > standing idle 2 > leaver 1). Two puppets standing ON each other
        // (true boxes overlap) separate: the lower priority one moves (ties: the deterministic tie-break). Everything else stays
        // with the stuck pass (the held one backs out / re-plans / takes the other exit). Rule 1 of the guard ("a standing puppet never
        // yields") made every static slot in a mover's way a deadlock: TruckB at its base in the excavator's lane, a leaver parked at
        // a crew's section start in front of the paver, the D0 truck clamped onto the breaker.
        private void MakeWayPass(EntityManager em, double now)
        {
            m_GList.Clear();
            foreach (var p in MachineRegistry.All) if ((p.GuardHeld || p.StandStill) && p.GuardBlocker != null) m_GList.Add(p);
            foreach (var p in m_GList)
            {
                var q = p.GuardBlocker;
                if (q == null || q == p || !MachineRegistry.All.Contains(p) || !MachineRegistry.All.Contains(q)) continue;
                if (!(p.GuardHeld || p.StandStill)) continue;   // released by an earlier step of this pass
                double since = double.IsNaN(p.WaitSince) ? p.GuardHeldSince : p.WaitSince;
                if (now - since < MxConst.kMakeWaySeconds) continue;
                bool overlap = GapAt(p, q, m_GCtx, now) < 0f;
                bool qStill = Stationary(q, m_GCtx, now, now + MxConst.kMakeWayStillWindow);
                if (!qStill && !overlap) continue;   // q moves: the guard's normal yield handles it
                int mp = Choreo.MoverPriority(p, OwnerOf(p.ProjectId)), bq = Choreo.BlockerPriority(q, OwnerOf(q.ProjectId));
                Puppet y, o;
                if (mp > bq && qStill) { y = q; o = p; }
                else if (overlap && qStill && Stationary(p, m_GCtx, now, now + MxConst.kMakeWayStillWindow))
                {
                    int bp = Choreo.BlockerPriority(p, OwnerOf(p.ProjectId));
                    y = bp < bq ? p : bq < bp ? q : (TieLower(p, q) ? p : q);
                    o = y == p ? q : p;
                }
                else continue;
                if (now < y.NextMakeWayTau) continue;
                y.NextMakeWayTau = now + MxConst.kMakeWayRetry;
                try { TryMakeWay(em, y, o, now, overlap); }
                catch (Exception e) { RRWLog.ErrorOnce("machines make-way", e); }
            }
            m_GList.Clear();
        }

        // y moves aside for o (Choreo.MakeWay); committed here with the follow-up state of its kind. No spot: a leaver that blocks a
        // working machine is removed (it was leaving anyway), a standing non-owner that blocks the front owner leaves after
        // kMakeWayFailsMax attempts; anything else stays with the stuck pass.
        private void TryMakeWay(EntityManager em, Puppet y, Puppet o, double now, bool overlap)
        {
            var c = GuardContext(em, y, now);
            if (c == null || c.Project == null || c.Track == null) return;
            var path = o.Plan;
            if (!double.IsNaN(o.WantedTau) && now - o.WantedTau <= MxConst.kWantedMaxAge && o.Wanted.Count > 0) path = o.Wanted;
            var r = Choreo.MakeWay(y, o, path, c, overlap, y.MakeWayAborts >= 2, out var plan, out string how);
            if (r == MakeWayResult.Budget) { y.MakeWayAborts++; y.NextMakeWayTau = now; return; }   // the check budget ran out: next update
            y.MakeWayAborts = 0;
            if (r == MakeWayResult.Done)
            {
                y.Plan = plan;
                y.PlanDirty = true;
                y.MakeWayFails = 0;
                Choreo.ClearBlocked(y);
                if (y.PostSite)
                {
                    // the leaver re-plans its drive-off from there (RetryLeavers, conflict-scanned) once the mover had its time
                    y.LeaveContinue = true;
                    y.ReplanAt = now + MxConst.kMakeWayHold;
                }
                else if (y.IsTruck)
                {
                    // its next trip starts from the aside spot (the planners begin from the current state; conflict-scanned)
                    y.OutAttempt = y.RetAttempt = -1;
                    y.Pend.Drop();
                    y.RetForced = 0;
                    if (y.Stage == TruckStage.Outbound) StopTruckCycle(y);
                    y.StandStill = false;
                    y.ReplanAt = math.max(double.IsInfinity(y.ReplanAt) ? now : y.ReplanAt, now + MxConst.kMakeWayHold);
                }
                else
                {
                    // a role stands aside: its role plan is tried again in kMakeWayHold s through the release check (it moves back only
                    // when that plan meets nobody); the aside wait is not counted as stuck for kAsideMaxSeconds
                    MarkWaiting(y, o, now);
                    y.ReplanAt = now + MxConst.kMakeWayHold;
                    y.AsideSince = now;
                }
                // the held one tries again at once
                o.ReplanAt = math.min(o.ReplanAt, now);
                if (o.IsTruck && !o.PostSite) { o.OutAttempt = o.RetAttempt = -1; o.Pend.Drop(); }
                MachineDebug.MakeWays++;
                string msg = "machines: make-way: " + y + " " + how + " for " + o + (overlap ? " (they stood on each other)" : "");
                MachineDebug.LastMakeWay = msg;
                if (MachineDebug.LogDespawns) RRWLog.Info(msg); else RRWLog.Verbose(msg);
                return;
            }
            MachineDebug.MakeWayFails++;
            y.MakeWayFails++;
            if (y.PostSite && (!o.PostSite || overlap))
            {
                // a leaver with no spot aside first drives off to the chain end AWAY from the machine it blocks (crew 1's grader in
                // front of crew 2's paver: on to the dead end, removed there 15 s after it stops) when that route is clear
                var lr = LeaveAway(y, o, c, now);
                if (lr == MakeWayResult.Budget) { y.NextMakeWayTau = now; return; }
                if (lr == MakeWayResult.Done) { o.ReplanAt = math.min(o.ReplanAt, now); return; }
                // a leaver in the way of a working machine (or on top of another machine) with no spot aside: gone
                MachineDebug.BlockingLeaverRemovals++;
                string why = "blocking leaver: in the way of " + o + (overlap ? " (overlapping)" : "") + ", no spot aside within " + RRWLog.F(MxConst.kMakeWayReach) + " m";
                RRWLog.Info("machines: " + y + " removed (" + why + ", dist=" + RRWLog.F(y.CamDist) + ")");
                MachineDebug.LastMakeWay = y + " removed: " + why;
                o.ReplanAt = math.min(o.ReplanAt, now);
                Despawn(em, y, why);
                return;
            }
            var owner = OwnerOf(o.ProjectId);
            if (!y.PostSite && !y.Outgoing && y.MakeWayFails >= MxConst.kMakeWayFailsMax && !o.PostSite && o.Role == owner && y.Role != owner)
            {
                // a standing non-owner blocks the front owner and has nowhere to go: it leaves (the next round removes it if it still blocks)
                MachineDebug.MakeWayLeaves++;
                y.MakeWayFails = 0;
                RRWLog.Info("machines: " + y + " leaves: it blocks " + o + " and has no spot aside");
                MakePostSite(em, y, c, now, "make-way failed: blocks the front owner " + o.Role);
                return;
            }
            RRWLog.Once("machines-makeway-none-" + y.ProjectId + "-" + y.Role, "machines: make-way: no spot aside for " + y + " (blocks " + o + "); the stuck pass resolves it");
        }

        // A blocking leaver re-plans its drive-off to the chain end on the far side of the machine it blocks (route conflict-checked).
        private MakeWayResult LeaveAway(Puppet y, Puppet o, PlanContext c, double now)
        {
            var r = Choreo.LeaveAway(y, o, c, y.MakeWayAborts >= 2, out var cand, out float end, out bool more, out string where);
            if (r == MakeWayResult.Budget) { y.MakeWayAborts++; return r; }
            if (r != MakeWayResult.Done) return r;
            MachineMotion.State(y.Plan, c.Clk, now, out var ys);
            y.MakeWayAborts = 0;
            y.Plan = cand;
            y.PlanDirty = true;
            y.LeaveEndU = end;
            y.ExitWhy = "away from " + o.Role + " it blocked (make-way)";
            y.OtherExitTried = true;
            y.LeaveContinue = more;
            y.ReplanAt = more ? MachineMotion.MotionEnd(cand) + 0.5 : double.PositiveInfinity;
            ClassifyDeadEnd(y, c, ys.U, end >= ys.U ? (sbyte)1 : (sbyte)-1, end);
            if (!double.IsNaN(y.LeaveTau)) y.LeaveCap = math.max(y.LeaveCap, (now - y.LeaveTau) + Choreo.LeaveNeedSeconds(y, c, ys.U, end >= ys.U ? (sbyte)1 : (sbyte)-1, end, now));
            Choreo.ClearBlocked(y);
            MachineDebug.MakeWays++;
            string msg = "machines: make-way: leaver " + y + " drives off away from " + o + ": " + where + (y.DeadEndLeave ? " (dead end: " + y.DeadEndWhy + ")" : "");
            MachineDebug.LastMakeWay = msg;
            if (MachineDebug.LogDespawns) RRWLog.Info(msg); else RRWLog.Verbose(msg);
            return MakeWayResult.Done;
        }

        // ------------------------------------------------------------ stuck detection and resolution

        private void StuckPass(EntityManager em, double now)
        {
            int stuckNow = 0;
            m_GList.Clear();
            m_GList.AddRange(MachineRegistry.All);
            foreach (var p in m_GList)
            {
                if (!MachineRegistry.All.Contains(p)) continue;   // removed by an earlier resolution this pass
                if (p.Plan.Count > 0)
                {
                    MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
                    if (math.abs(s.Speed) >= MxConst.kStuckMoveSpeed || double.IsNaN(p.LastMoveTau)) p.LastMoveTau = now;
                }
                else if (double.IsNaN(p.LastMoveTau)) p.LastMoveTau = now;
                bool waiting = p.GuardHeld || p.StandStill;
                // standing aside for a mover (make-way) is an intended wait, for kAsideMaxSeconds
                if (waiting && !double.IsNaN(p.AsideSince) && now - p.AsideSince < MxConst.kAsideMaxSeconds) { p.LastMoveTau = now; waiting = false; }
                if (!waiting)
                {
                    if (p.Stuck) RRWLog.Verbose("machines: " + p + " no longer stuck after " + RRWLog.F((float)(now - p.StuckSince)) + " s");
                    p.Stuck = false;
                    p.StuckSince = double.NaN;
                    if (p.StuckEpisodes > 0 && now - p.LastEpisodeTau > MxConst.kStuckEpisodeReset) p.StuckEpisodes = 0;
                    continue;
                }
                if (double.IsNaN(p.WaitSince)) p.WaitSince = now;
                double still = now - math.max(p.WaitSince, p.LastMoveTau);
                if (still < RRWConst.kMachineStuckSeconds)
                {
                    p.Stuck = false;
                    p.StuckSince = double.NaN;
                    continue;
                }
                if (!p.Stuck)
                {
                    p.Stuck = true;
                    p.StuckSince = now;
                    MachineDebug.LastStuck = p + " waits for " + (p.GuardBlocker != null ? p.GuardBlocker.ToString() : "?") + (p.StandStill ? " (stand-still)" : " (guard hold)");
                }
                stuckNow++;
                MachineDebug.StuckLongest = math.max(MachineDebug.StuckLongest, still);
                if (now < p.NextResolveTau) continue;
                try { ResolveStuck(em, p, now); }
                catch (Exception e) { RRWLog.ErrorOnce("machines stuck resolution", e); p.NextResolveTau = now + RRWConst.kMachineStuckSeconds; }
            }
            m_GList.Clear();
            MachineDebug.StuckNow = stuckNow;
            MachineDebug.StuckMax = math.max(MachineDebug.StuckMax, stuckNow);
        }

        // A stuck leaver takes the other chain end once: a connected one, or the dead end when the
        // blocker is a WORKING machine of its project (the crew has priority; the leaver is removed there out of view).
        private static bool LeaverOtherEnd(Puppet y, PlanContext cy, Puppet other, string pair)
        {
            if (y.OtherExitTried) return false;
            float alt = !float.IsNaN(y.LeaveEndU) && math.abs(y.LeaveEndU - cy.Trim1) < 0.5f ? cy.Trim0 : cy.Trim1;
            bool altOk = Choreo.EndConnected(cy, alt >= cy.Trim1 - 0.5f);
            bool yieldCrew = !altOk && other != null && !other.PostSite && !other.Outgoing && !cy.Releasing;
            if (!altOk && !yieldCrew) return false;
            y.OtherExitTried = true;
            y.LeaveEndU = alt;
            y.ExitWhy = altOk ? "other connected exit (stuck)" : "dead end: yields to working " + other.Role + " (stuck)";
            // a stop at a dead end is removed out of view / kDeadEndParkRemoveSeconds after stopping
            if (!float.IsNaN(y.NowU)) ClassifyDeadEnd(y, cy, y.NowU, alt >= y.NowU ? (sbyte)1 : (sbyte)-1, alt);
            if (!altOk) MachineDebug.LeaverDeadEndYields++;
            float fromU = y.NowU;
            Choreo.PlanLeave(y, cy, alt, out string where, out bool more);
            y.LeaveContinue = more;
            // the new route gets its own time (the cap never cuts a leaver short of the other exit)
            if (!double.IsNaN(y.LeaveTau) && !float.IsNaN(fromU))
                y.LeaveCap = math.max(y.LeaveCap, (cy.Now - y.LeaveTau) + Choreo.LeaveNeedSeconds(y, cy, fromU, alt >= fromU ? (sbyte)1 : (sbyte)-1, alt, cy.Now));
            if (!more) y.ReplanAt = double.PositiveInfinity;
            Choreo.ClearBlocked(y);
            MachineDebug.ResOtherExit++;
            RRWLog.Verbose("machines: stuck " + pair + ": takes the other end u=" + RRWLog.F(alt) + " (" + y.ExitWhy + "): " + where);
            return true;
        }

        // One deterministic resolution step for a stuck puppet (steps 1-4 in the header). The next step runs only if it is still
        // stuck kMachineStuckSeconds later (a back-out / re-plan moves it, which restarts the stuck clock).
        private void ResolveStuck(EntityManager em, Puppet p, double now)
        {
            p.NextResolveTau = now + RRWConst.kMachineStuckSeconds;
            p.StuckEpisodes++;
            p.LastEpisodeTau = now;
            MachineDebug.StuckEpisodes++;
            Puppet o = p.GuardBlocker;
            if (o != null && (!MachineRegistry.All.Contains(o) || o == p)) o = null;
            // (1) the lower rank of the pair acts (yields); a lone stuck puppet acts itself
            Puppet y = o != null ? Yielder(p, o, now) : p;
            Puppet other = y == p ? o : p;
            string pair = y + (other != null ? " (blocks / blocked by " + other + ")" : "");
            var cy = GuardContext(em, y, now);
            bool leaver = y.PostSite;
            if (cy == null || cy.Project == null)
            {
                MachineDebug.ResNone++;
                RRWLog.Verbose("machines: stuck " + pair + ": no project context (removed when out of sight / at the leave cap)");
                return;
            }
            if (!leaver && !cy.ReadyOk)
            {
                // fails closed: nothing ready to move into (stale drain report / switch): wait for the Director
                MachineDebug.ResNone++;
                RRWLog.Once("machines-stuck-notready-" + y.ProjectId, "machines: stuck " + pair + ": no WorkZonesReady car half to resolve into (" + cy.NoSpawnWhy + ")");
                return;
            }
            // a leaver blocked by a WORKING machine of its project goes to the other end at once (no
            // back-out loop against the crew until the leave cap)
            if (leaver && other != null && !other.PostSite && !other.Outgoing && LeaverOtherEnd(y, cy, other, pair)) return;
            // (4) too many failed episodes: the lower rank is removed (leaver: culled / leave cap)
            if (p.StuckEpisodes > MxConst.kStuckEpisodesMax)
            {
                MachineDebug.ResRemoved++;
                p.StuckEpisodes = 0;
                string msg = "machines: " + y + " removed after " + MxConst.kStuckEpisodesMax + " failed stuck episodes of " + p +
                             (other != null && other != y ? " (blocker " + other + ")" : "") + ": leaves, gone when out of sight or at the leave cap";
                RRWLog.Warn(msg);
                MachineDebug.LastGuard = msg;
                if (!leaver) MakePostSite(em, y, cy, now, "stuck (W2b)");
                return;
            }
            // (2) back out along its own lane (only into WorkZonesReady: Choreo.BackOut clamps with the lane policy)
            y.GuardBackOuts = 0;   // a new stuck episode may back out again (TryBackOut allows 3 per wait episode)
            if (other != null) other.GuardBackOuts = math.min(other.GuardBackOuts, 2);
            if (TryBackOut(em, y, other ?? y, now, "stuck " + RRWLog.F((float)(now - y.WaitSince)) + " s"))
            {
                MachineDebug.ResBackOut++;
                RRWLog.Verbose("machines: stuck " + pair + ": backs out (episode " + p.StuckEpisodes + ")");
                return;
            }
            if (other != null && !other.PostSite && TryBackOut(em, other, y, now, "stuck (other side)"))
            {
                MachineDebug.ResOtherBackOut++;
                RRWLog.Verbose("machines: stuck " + pair + ": the other one backs out (episode " + p.StuckEpisodes + ")");
                return;
            }
            // (3) no back-out route: a leaver takes the other connected exit; a role re-plans from scratch
            // (TryBackOut re-filled the shared planning context, possibly for the other puppet's project: rebuild y's)
            cy = GuardContext(em, y, now);
            if (cy == null || cy.Project == null) { MachineDebug.ResNone++; return; }
            if (leaver)
            {
                if (LeaverOtherEnd(y, cy, other, pair)) return;
                MachineDebug.ResNone++;
                RRWLog.Verbose("machines: stuck leaver " + pair + ": no other connected exit (removed when out of sight / at the leave cap)");
                return;
            }
            // role re-plan from scratch on the next director pass (forced: intent reset; trucks restart their cycle at home)
            if (y.IsTruck) StopTruckCycle(y);
            y.IntentKey = int.MinValue;
            y.ReplanAt = now;
            Choreo.ClearBlocked(y);
            y.WaitSince = now;   // keep counting the episode if the fresh plan is held again
            MarkWaiting(y, other, now);
            MachineDebug.ResReplan++;
            RRWLog.Verbose("machines: stuck " + pair + ": re-plans its role (episode " + p.StuckEpisodes + ")");
        }
    }
}
