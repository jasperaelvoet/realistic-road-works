#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Game.Objects;
using Game.Rendering;
using Game.Simulation;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;

// Machines dev commands: rrw.mx.list, rrw.mx.check, rrw.mx.pose, rrw.mx.trace, rrw.mx.plan.
// They only read state or flip managed tuning values; structural work stays in MachineDirectorSystem.
namespace RealisticRoadWorks.V3.Machines
{
    internal static class MxDev
    {
        public static string F(float v) => RRWLog.F(v);

        public static float Fl(string s) => float.Parse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture);

        public static Dictionary<string, string> Kv(string[] a, List<string> pos)
        {
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in a)
            {
                int eq = s.IndexOf('=');
                if (eq > 0) kv[s.Substring(0, eq)] = s.Substring(eq + 1);
                else pos?.Add(s);
            }
            return kv;
        }

        public static bool TryRole(string s, out MachineRole role)
        {
            role = MachineRole.Excavator;
            switch ((s ?? "").ToLowerInvariant())
            {
                case "excavator": case "ex": case "digger": role = MachineRole.Excavator; return true;
                case "loader": case "lo": case "grader": case "gr": role = MachineRole.Loader; return true;
                case "trucka": case "a": case "truck": role = MachineRole.TruckA; return true;
                case "truckb": case "b": role = MachineRole.TruckB; return true;
                case "crew": case "crewtruck": role = MachineRole.CrewTruck; return true;
                case "finisher": case "paver": case "painter": role = MachineRole.Finisher; return true;
            }
            return Enum.TryParse(s, true, out role);
        }

        // "<role>" (nearest project first), "p<id>:<role>", "e<entityIndex>", "#<n>" (index in rrw.mx.list).
        public static Puppet Find(string arg)
        {
            var all = MachineRegistry.All;
            if (string.IsNullOrEmpty(arg)) return all.Count > 0 ? all[0] : null;
            if (arg[0] == '#' && int.TryParse(arg.Substring(1), out int n)) return n >= 0 && n < all.Count ? all[n] : null;
            if (arg[0] == 'e' && int.TryParse(arg.Substring(1), out int ei)) { foreach (var p in all) if (p.E.Index == ei) return p; return null; }
            uint pid = 0;
            string r = arg;
            int colon = arg.IndexOf(':');
            if (arg[0] == 'p' && colon > 1 && uint.TryParse(arg.Substring(1, colon - 1), out pid)) r = arg.Substring(colon + 1);
            if (!TryRole(r, out var role)) return null;
            Puppet best = null;
            foreach (var p in all)
            {
                if (p.Role != role || (pid != 0 && p.ProjectId != pid)) continue;
                if (best == null || (p.PostSite ? 1 : 0) < (best.PostSite ? 1 : 0) || p.CamDist < best.CamDist) best = p;
            }
            return best;
        }

        public static double Now() => MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);

        public static string Describe(Puppet p, bool legs)
        {
            var sb = new StringBuilder();
            double now = Now();
            MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
            sb.Append(p).Append(" act=").Append(p.Activity).Append(" stage=").Append(p.Stage)
              .Append(" leg=").Append(s.Leg).Append('/').Append(p.Plan.Count).Append(':').Append((LegKind)s.LegKind)
              .Append(" u=").Append(F(s.U)).Append(" lat=").Append(F(s.Lat)).Append(" v=").Append(F(s.Speed))
              .Append(" anim=").Append((AnimKind)s.Anim).Append(" t=").Append(F(s.AnimTime)).Append(" blend=").Append(F(s.Blend))
              .Append(" load=").Append(p.Load).Append(' ').Append(p.AmountPct).Append('%')
              .Append(" cam=").Append(F(p.CamDist)).Append(" culled=").Append(p.CulledUpdates)
              .Append(p.PostSite ? " POST-SITE" : "").Append(p.Outgoing ? " outgoing" : "").Append(p.LodOut ? " LOD-OUT" : "")
              .Append(" replanIn=").Append(double.IsPositiveInfinity(p.ReplanAt) ? "inf" : F((float)(p.ReplanAt - now)))
              // crew, observed speed / acceleration against the role's limits
              .Append(" crew=").Append(p.Crew).Append(" v=").Append(F(p.LastV)).Append('/').Append(F(p.LastVLimit))
              .Append(" vmax=").Append(F(p.MaxV)).Append(" a=").Append(F(p.LastA)).Append('/').Append(F(p.LastALimit)).Append(" amax=").Append(F(p.MaxA))
              .Append(" lim(fwd=").Append(F(p.Lim.VFwd)).Append(" rev=").Append(F(p.Lim.VRev)).Append(" turn=").Append(F(p.Lim.VTurn))
              .Append(" acc=").Append(F(p.Lim.Accel)).Append(" work=").Append(F(p.Lim.VWork)).Append(" leave=").Append(F(p.Lim.VLeave)).Append(')')
              .Append(p.ViolV + p.ViolA > 0 ? " VIOLATIONS(v=" + p.ViolV + " a=" + p.ViolA + ")" : "");
            if (legs)
            {
                for (int i = 0; i < p.Plan.Count; i++)
                {
                    var l = p.Plan.Get(i);
                    sb.Append("\ndev rrw mx   leg ").Append(i).Append(' ').Append((LegKind)l.Kind).Append(" t=[").Append(F((float)(p.Plan.Epoch + l.T0 - now)))
                      .Append(',').Append(l.OpenEnded ? "inf" : F((float)(p.Plan.Epoch + l.T1 - now))).Append("] f=").Append(l.Facing).Append(" mv=").Append(l.Move)
                      .Append(" u0=").Append(F(l.U0)).Append(" u1=").Append(F(l.U1)).Append(" lat=").Append(F(l.L0)).Append("->").Append(F(l.L1))
                      .Append(" lo/hi=").Append(F(l.Lo)).Append('/').Append(F(l.Hi)).Append(" p=").Append(F(l.P0)).Append('/').Append(F(l.P1)).Append('/').Append(F(l.P2))
                      .Append(" anim=").Append((AnimKind)l.Anim).Append(" front=").Append(l.Front).Append(" catch=").Append(F(l.Cu)).Append('/').Append(F(l.CatchDur))
                      .Append(" v/a=").Append(F(l.Vmax)).Append('/').Append(F(l.Acc)).Append(" cv=").Append(F(l.Cv)).Append('/').Append(F(l.VDur))
                      .Append(l.RetV > 0f ? " ret=" + F(l.RetU) + "@" + F(l.RetV) + " by+" + F((float)(p.Plan.Epoch + l.T0 + l.RetT - now)) : "")
                      .Append(l.Rate > 0f ? " LAG rate=" + F(l.Rate) : "");
                }
            }
            return sb.ToString();
        }
    }

    public sealed class MxListCommand : IDevCommand
    {
        public string Name => "rrw.mx.list";
        public string Help => "rrw.mx.list - every machine puppet (role, activity, leg, u/lat, load, camera distance)";

        public void Run(DevContext ctx, string[] a)
        {
            var all = MachineRegistry.All;
            ctx.Log("rrw mx list puppets=" + all.Count + " spawned=" + MachineRegistry.Spawned + " despawned=" + MachineRegistry.Despawned +
                    " respawned=" + MachineRegistry.Respawned + " tagged=" + MachineTagSystem.Tagged + " moverErr=" + MachineMoverSystem.JobErrors +
                    " boneErr=" + MachineBoneSystem.JobErrors + " clockScale=" + MxDev.F(MachineRegistry.Clock.Scale) + " excavatorClone=" + RRWPrefabRegistry.ExcavatorOk +
                    " crewsActive=" + MachineDebug.CrewsActive + " crewsLodOut=" + MachineDebug.CrewsLodOut);
            // one line per crew of every project (section, front, front speed vs the front owner's working pace, LOD)
            foreach (var pr in SiteRegistry.Projects.Values)
            {
                var v = pr.View();
                int n = v.CrewCount;
                if (!MachineRegistry.States.TryGetValue(pr.Id, out var st)) continue;
                float vc = PhasePlan.CrewFrontSpeed(v), vw = MachineLimits.WorkSpeed(v.Kind, v.Phase);
                for (int k = 0; k < n; k++)
                {
                    var cs = st.Crews[k];
                    var cp = PhasePlan.Crew(v, k);
                    ctx.Log("rrw mx crew p" + pr.Id + "/" + k + " of " + n + " " + pr.Phase + " section=[" + MxDev.F(cp.SecLo) + "," + MxDev.F(cp.SecHi) + "] front=" +
                            MxDev.F(PhasePlan.CrewFront(v, k)) + " v=" + MxDev.F(vc) + " work=" + MxDev.F(vw) + (vc > vw * 1.02f && vw > 0f ? " OVERWORK" : "") +
                            " live=" + cs.Live + " spawnOk=" + cs.SpawnOk + " lodOut=" + cs.LodOut + " cam=" + MxDev.F(cs.CamDist) +
                            " hop=" + MxDev.F(cs.DigHopStart) + "+" + MxDev.F(cs.DigHopDur) + "s" + (cs.DigRate > 0f ? " LAG " + MxDev.F(cs.DigRate) : ""));
                }
            }
            for (int i = 0; i < all.Count; i++) ctx.Log("rrw mx #" + i + " " + MxDev.Describe(all[i], false));
        }
    }

    // Fine-grained main-thread timing of Machines (group mx) and SurfaceAreaSystem (group sx): the rrw.perf window plus the
    // MxPerf / SxPerf sections and counters (MachinePerf.cs, SurfacePerf.cs) and the breakdown of the worst update (spike
    // attribution). syncprobe=1 completes every tracked job at the start of each Machines update in its own section
    // (mx SyncProbe): the job work still pending at Modification1, which the first EntityManager structural change / component
    // read of a vanilla-written type would otherwise pay inside our sections. It shifts that wait out of the other sections
    // (and away from Surfaces), so read the probe run next to a normal run, never alone.
    public sealed class MxPerfCommand : IDevCommand
    {
        public string Name => "rrw.mx.perf";
        public string Help => "rrw.mx.perf [seconds=10] [groups=mx,sx] [syncprobe=0|1] - rrw.perf window + Machines / Surfaces sections, counters, worst update";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = new List<string>();
            var kv = MxDev.Kv(a, pos);
            float secs = 10f;
            string sarg = pos.Count > 0 ? pos[0] : kv.TryGetValue("seconds", out var ss) ? ss : null;
            if (sarg != null && float.TryParse(sarg, NumberStyles.Float, CultureInfo.InvariantCulture, out float sv)) secs = sv;
            string[] groups = kv.TryGetValue("groups", out var gs) ? gs.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) : new[] { "mx", "sx" };
            bool probe = kv.TryGetValue("syncprobe", out var sp) && sp != "0";
            MxPerf.SyncProbeOn = probe && secs > 0f;
            int puppets0 = MachineRegistry.All.Count;
            global::RealisticRoadWorks.V3.DevCmds.PerfCommand.Measure(ctx, secs, groups, () =>
            {
                MxPerf.SyncProbeOn = false;
                int projects = 0, crews = 0;
                foreach (var st in MachineRegistry.States.Values)
                {
                    if (st.Any()) projects++;
                    for (int k = 0; k < st.Crews.Length; k++) if (st.Crews[k].Any()) crews++;
                }
                ctx.Log("rrw perfd mx context: projects with machines=" + projects + " active crews=" + crews + " puppets=" + MachineRegistry.All.Count + " (at start " + puppets0 +
                        ") postSite=" + CountPostSite() + " registry projects=" + SiteRegistry.Projects.Count + " syncprobe=" + (probe ? 1 : 0) +
                        " planDeferred=" + MxBudget.DeferredTotal +
                        " (targets: all RRW < 0.6 ms/frame with 10 projects, Machines < 0.05 ms per active crew, no spike > 2 ms, 0 B/frame)");
            });
        }

        private static int CountPostSite()
        {
            int n = 0;
            foreach (var p in MachineRegistry.All) if (p.PostSite) n++;
            return n;
        }
    }

    public sealed class MxPlanCommand : IDevCommand
    {
        public string Name => "rrw.mx.plan";
        public string Help => "rrw.mx.plan <role|p<id>:<role>|#n|e<index>> - dump a machine's plan legs (times relative to now)";

        public void Run(DevContext ctx, string[] a)
        {
            var p = MxDev.Find(a.Length > 0 ? a[0] : null);
            if (p == null) { ctx.Log("rrw mx plan: no such machine"); return; }
            ctx.Log("rrw mx plan " + MxDev.Describe(p, true));
        }
    }

    // Live invariants: overlaps, floor contact, reverse legs, LivePath; watch=1 repeats every 30
    // updates and logs every despawn with its distance and culling state.
    // Floor: floorError = |origin Y - independent floor under the centre| (GroundMath.FloorWorldY on the edge,
    // fresh CPU terrain), contactError = worst |contact point Y - independent floor there| over the 4 footprint contact
    // points (tyres), both for machines on the flat floor; the worst puppet is described (leg, u, lat, track floor vs
    // independent floor, updates since its edge's track Y refresh) so a stale track and a footprint problem can be told apart.
    // Pairs: exPairGap = smallest clearance between the digger and the grader of a project (m, < 0 = overlap),
    // postSiteOverlaps = overlapping pairs among parked post-site machines; guard counters.
    public sealed class MxCheckCommand : IDevCommand
    {
        public string Name => "rrw.mx.check";
        public string Help => "rrw.mx.check [watch=0|1] [reset=1] - overlaps (+ post-site, digger/grader gap), floor contact (centre and tyre contact points, target < 0.1 m), reverse legs <= 35 m, LivePath; observed speed / acceleration vs MachineLimits per crew and role, counters (clampedLegs, overWork, frontLag, emergencyBrakes, violations, hand-overs); reset=1 clears them; watch=1 repeats every 30 updates, logs despawns, parks and guard yields";

        private static float s_MinPairGap = float.PositiveInfinity;
        private static float s_MaxFloor, s_MaxContact;

        public void Run(DevContext ctx, string[] a)
        {
            var kv = MxDev.Kv(a, null);
            if (kv.TryGetValue("reset", out var rs) && rs != "0") { MxSpeed.Reset(); MxBudget.DeferredTotal = 0; ctx.Log("rrw mx check: speed counters reset"); }
            if (kv.TryGetValue("watch", out var w))
            {
                bool on = w == "1" || w.Equals("on", StringComparison.OrdinalIgnoreCase);
                MachineDebug.LogDespawns = on;
                MachineDebug.CheckEvery = on ? 30 : 0;
                MachineDebug.Watch = on ? (Action<EntityManager>)(em => Report(ctx, em, true)) : null;
                MachineDebug.OverlapFrames = MachineDebug.CheckedFrames = 0;
                s_MinPairGap = float.PositiveInfinity;
                s_MaxFloor = s_MaxContact = 0f;
                ctx.Log("rrw mx check watch=" + (on ? 1 : 0));
            }
            Report(ctx, ctx.EntityManager, false);
        }

        // Independent floor (world Y) at chain u of a project, and the flat half width there. False off the chain.
        private static bool Floor(EntityManager em, TerrainSystem terrain, ProjectRecord pr, float u, out float floor, out float flatHalf) =>
            Floor(em, terrain, pr, u, out floor, out flatHalf, out _);

        // nodeDist: distance (m) to the nearest node between two works edges (the track blends floor steps there).
        private static bool Floor(EntityManager em, TerrainSystem terrain, ProjectRecord pr, float u, out float floor, out float flatHalf, out float nodeDist)
        {
            floor = 0f; flatHalf = 0f; nodeDist = float.MaxValue;
            foreach (var e in pr.Edges)
            {
                if (!SiteRegistry.TryGetEdge(e, out var rec) || rec.Arc == null || !em.HasComponent<RoadWorksSite>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                if (u < site.ChainLo || u > site.ChainHi) continue;
                if (!em.HasComponent<RoadWorksRuntime>(e) || !em.HasComponent<RoadWorksGround>(e)) return false;
                float sEdge = PhasePlan.EdgeS(u, site.m_ChainU0, site.m_ChainU1, rec.Arc.Length);
                float3 c = rec.Arc.Position(sEdge);
                var rt = em.GetComponentData<RoadWorksRuntime>(e);
                var gd = em.GetComponentData<RoadWorksGround>(e);
                var planned = PhasePlan.Terrain(PlanInput.From(site, rt));
                // the SAME floor function the track uses (TrackBuilder.FloorRel), at the exact u
                floor = c.y + TrackBuilder.FloorRel(true, rt, gd, planned, c, EcsUtil.TerrainY(terrain, c), false);
                flatHalf = rec.Section.FlatHalfWidth;
                if (site.ChainLo > 0.01f) nodeDist = math.min(nodeDist, u - site.ChainLo);
                if (site.ChainHi < pr.ChainLength - 0.01f) nodeDist = math.min(nodeDist, site.ChainHi - u);
                return !float.IsNaN(floor);
            }
            return false;
        }

        private static void Report(DevContext ctx, EntityManager em, bool quiet)
        {
            MachineTrackStore.CompleteReaders();
            var problems = new List<string>();
            int overlaps = MachineChecks.CountOverlaps(out string first);
            if (overlaps > 0) problems.Add("overlaps=" + overlaps + " (" + first + ")");
            int postOverlaps = MachineChecks.CountPostSiteOverlaps(out string firstPost);
            if (postOverlaps > 0) problems.Add("postSiteOverlaps=" + postOverlaps + " (" + firstPost + ")");
            float pairGap = MachineChecks.MinDiggerGraderGap(out string pairWho);
            if (pairGap < s_MinPairGap) s_MinPairGap = pairGap;
            if (pairGap <= 0f) problems.Add("exPairGap=" + MxDev.F(pairGap) + " (" + pairWho + ")");
            float worstY = 0f, worstC = 0f, worstRamp = 0f;
            string worstYWho = "", worstCWho = "", worstRampWho = "";
            int longReverse = 0;
            var terrain = ctx.System<TerrainSystem>();
            double now = MxDev.Now();
            var tv = MachineTrackStore.View();
            foreach (var p in MachineRegistry.All)
            {
                for (int i = 0; i < p.Plan.Count; i++)
                {
                    var l = p.Plan.Get(i);
                    if (l.Kind == (byte)LegKind.Drive && l.Move != l.Facing && math.abs(l.U1 - l.U0) > RRWConst.kMaxReverseLeg + 0.01f) longReverse++;
                }
                if (!p.Alive(em) || p.PostSite) continue;
                MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
                if (s.LegKind == (byte)LegKind.Turn) continue;
                if (!MachineMotion.Pose(p.Plan, tv, s, out float3 pos, out quaternion rot)) continue;
                if (!SiteRegistry.TryGetProject(p.ProjectId, out var pr)) continue;
                // centre (machines on the flat floor only). Off node ramps (|u - node| > kNodeRampHalf) the
                // limit is kFloorErrorMax (0.10 m), on them kFloorErrorRampMax (0.15 m: the track blends floor steps there and a long
                // footprint bridges the step); the worst line carries the track diagnosis (applied vs planned profile, spacing, edge / node
                // distance, track age, the step stamp the track saw vs now)
                if (Floor(em, terrain, pr, s.U, out float floor, out float flat, out float nodeDist) && math.abs(s.Lat) <= flat - p.BoxHalfWid)
                {
                    float err = math.abs(pos.y - floor);
                    bool ramp = nodeDist <= MxConst.kNodeRampHalf;   // node ramp: |u - node| <= 2.5 m
                    if (ramp ? err > worstRamp : err > worstY)
                    {
                        float trackFloor = tv.Height(p.Plan.Track, s.U, s.Lat, out float ty) ? ty : float.NaN;
                        MachineTrackStore.TryGet(p.ProjectId, out var td);
                        string who = p + " leg=" + (LegKind)s.LegKind + " u=" + MxDev.F(s.U) + " lat=" + MxDev.F(s.Lat) + " y=" + MxDev.F(pos.y) +
                                     " floor=" + MxDev.F(floor) + " trackFloor=" + MxDev.F(trackFloor) + " (origin-track " + MxDev.F(pos.y - trackFloor) +
                                     ", track-floor " + MxDev.F(trackFloor - floor) + ") foot=" + p.FootSource + " " + MxDev.F(2f * p.Plan.HalfLen) + "m | " +
                                     TrackBuilder.FloorDiag(em, td, s.U);
                        if (ramp) { worstRamp = err; worstRampWho = who; }
                        else { worstY = err; worstYWho = who; }
                    }
                }
                // tyre / track contact points
                float a = math.max(0.5f, p.Plan.HalfLen), b = math.max(0.5f, p.Plan.HalfWid), o = math.clamp(p.Plan.FootOff, -a, a);
                float2 hch = math.normalizesafe(new float2(s.Hu, s.Hl), new float2(1f, 0f));
                for (int k = 0; k < 4; k++)
                {
                    float z = o + ((k & 1) == 0 ? a : -a), x = (k & 2) == 0 ? -b : b;
                    float du = hch.x * z - hch.y * x, dl = hch.y * z + hch.x * x;
                    // skip contacts on the node blend (+-2.5 m: the track ramps floor steps between edges there)
                    if (!Floor(em, terrain, pr, s.U + du, out float fl, out float fh, out float nd) || math.abs(s.Lat + dl) > fh || nd < 2.5f) continue;
                    float cy = pos.y + math.rotate(rot, new float3(x, 0f, z)).y;
                    float ce = math.abs(cy - fl);
                    if (ce > worstC) { worstC = ce; worstCWho = p + " contact " + ((k & 1) == 0 ? "front" : "rear") + ((k & 2) == 0 ? "L" : "R") + " u=" + MxDev.F(s.U + du); }
                }
            }
            s_MaxFloor = math.max(s_MaxFloor, worstY);
            s_MaxContact = math.max(s_MaxContact, worstC);
            if (worstY > RRWConst.kFloorErrorMax) problems.Add("floorError=" + MxDev.F(worstY) + " > " + MxDev.F(RRWConst.kFloorErrorMax) + " off node ramps (" + worstYWho + ")");
            if (worstRamp > MxConst.kFloorErrorRampMax) problems.Add("floorErrorRamp=" + MxDev.F(worstRamp) + " > " + MxDev.F(MxConst.kFloorErrorRampMax) + " on a node ramp (" + worstRampWho + ")");
            if (worstC > 0.15f) problems.Add("contactError=" + MxDev.F(worstC) + " (" + worstCWho + ")");
            if (longReverse > 0) problems.Add("reverseLegs>35m=" + longReverse);
            var lp = new List<string>();
            foreach (var kv in RRWIntrospection.Checkers) if (kv.Key == "Machines") kv.Value(em, lp);
            foreach (var x in lp) if (x.IndexOf("LivePath", StringComparison.Ordinal) >= 0) problems.Add(x);
            // lane invariants (open groups, release cap, outside, stale report) + readiness (not ready, C4 works half, stuck)
            var r2 = new List<string>();
            MachineChecks.Round2(em, r2);
            problems.AddRange(r2);
            if (MachineDebug.StuckNow > 0) problems.Add("stuckNow=" + MachineDebug.StuckNow + " (" + MachineDebug.LastStuck + ")");
            if (MachineDebug.CheckEvery > 0 && MachineDebug.OverlapFrames > 0)
                problems.Add("overlapSeconds=" + MxDev.F(MachineDebug.OverlapFrames / 60f) + " over " + MxDev.F(MachineDebug.CheckedFrames / 60f) + " s watched");
            string stats = " worstFloorErr=" + MxDev.F(worstY) + " worstFloorErrRamp=" + MxDev.F(worstRamp) + " worstContactErr=" + MxDev.F(worstC) +
                           " appliedOnlyTrackRefreshes=" + TrackBuilder.AppliedOnlyRefreshes +
                           " exPairGap=" + (float.IsPositiveInfinity(pairGap) ? "n/a" : MxDev.F(pairGap)) +
                           " guardYields=" + MachineDebug.GuardYields + " guardFailures=" + MachineDebug.GuardFailures +
                           " sepGuard(brakes=" + MachineDebug.GuardBrakes + " contacts=" + MachineDebug.GuardContacts + " backOuts=" + MachineDebug.GuardBackOuts +
                           " deadlocks=" + MachineDebug.GuardDeadlocks + ") standStills=" + MachineDebug.StandStills + " leaves=" + MachineDebug.Leaves +
                           " capDespawns=" + MachineDebug.CapDespawns +
                           // leave removals and frozen-machine resolution
                           " | removals(culled=" + MachineDebug.CulledRemovals + " far=" + MachineDebug.FarRemovals + " visibleCap=" + MachineDebug.VisibleCapRemovals + ")" +
                           " deadlockEpisodes=" + MachineDebug.DeadlockEpisodes + " standStillEpisodes=" + MachineDebug.StandStillEpisodes +
                           " stuck(now=" + MachineDebug.StuckNow + " max=" + MachineDebug.StuckMax + " episodes=" + MachineDebug.StuckEpisodes +
                           " longest=" + MxDev.F((float)MachineDebug.StuckLongest) + "s)" +
                           " resolutions(backOut=" + MachineDebug.ResBackOut + " otherBackOut=" + MachineDebug.ResOtherBackOut + " replan=" + MachineDebug.ResReplan +
                           " otherExit=" + MachineDebug.ResOtherExit + " removed=" + MachineDebug.ResRemoved + " none=" + MachineDebug.ResNone + ")" +
                           " leaversVsCrew(deadEndYields=" + MachineDebug.LeaverDeadEndYields + " crossings=" + MachineDebug.LeaverCrewCrossings + ")" +
                           (MachineDebug.LastStuck.Length > 0 ? " lastStuck=" + MachineDebug.LastStuck : "") +
                           (MachineDebug.LegacyReady ? " READY=LEGACY" : "") +
                           (MachineDebug.CheckEvery > 0 ? " overlapSeconds=" + MxDev.F(MachineDebug.OverlapFrames / 60f) + "/" + MxDev.F(MachineDebug.CheckedFrames / 60f) : "") +
                           (MachineDebug.CheckEvery > 0 ? " watchMax(floor=" + MxDev.F(s_MaxFloor) + " contact=" + MxDev.F(s_MaxContact) +
                                                          " minPairGap=" + (float.IsPositiveInfinity(s_MinPairGap) ? "n/a" : MxDev.F(s_MinPairGap)) + ")" : "");
            // speed table invariant, crews and counters
            var r4 = new List<string>();
            MachineChecks.Round4(em, r4);
            problems.AddRange(r4);
            stats += " | motion " + MxSpeed.Summary() + " crewsActive=" + MachineDebug.CrewsActive + " crewsLodOut=" + MachineDebug.CrewsLodOut;
            // rollers, IK digging, dust puffs, dead-end leavers
            var r5 = new List<string>();
            MachineChecks.Round5(em, r5, !quiet);   // (the object sweep allocates: not in watch mode)
            problems.AddRange(r5);
            stats += " | rollersDig " + MachineChecks.Round5Summary();
            stats += " | separation " + MachineDebug.Round7Summary();   // make-way, exit removals, compact turns, inline waits
            if (problems.Count == 0) { if (!quiet) ctx.Log("rrw mx check ok puppets=" + MachineRegistry.All.Count + stats + (worstY > 0f ? " worstFloor=" + worstYWho : "")); }
            else ctx.Log("rrw mx check FAIL puppets=" + MachineRegistry.All.Count + ": " + string.Join("; ", problems) + " |" + stats);
            if (!quiet)
            {
                // per role: max observed speed / acceleration vs the table, violations
                var sb = new StringBuilder("rrw mx check roles:");
                for (int r = 0; r <= (int)MachineRole.Count; r++)
                {
                    // index Count = the rollers (MxSpeed clamps MachineRole.Roller there)
                    var role = r < (int)MachineRole.Count ? (MachineRole)r : MachineRole.Roller;
                    var lim = MachineLimits.Of(role, WorksPhase.None);
                    sb.Append(' ').Append(role).Append("(vmax=").Append(MxDev.F(MxSpeed.RoleMaxV[r])).Append(" ratio=").Append(MxDev.F(MxSpeed.RoleMaxVRatio[r]))
                      .Append(" amax=").Append(MxDev.F(MxSpeed.RoleMaxA[r])).Append("/").Append(MxDev.F(lim.Accel))
                      .Append(" viol=").Append(MxSpeed.RoleViolV[r]).Append('/').Append(MxSpeed.RoleViolA[r]).Append(')');
                }
                ctx.Log(sb.ToString());
                // per crew / role: the live puppets (v / vmax, a / amax against the puppet's own row)
                foreach (var p in MachineRegistry.All)
                {
                    if (p.PostSite) continue;
                    ctx.Log("rrw mx check crew=" + p.Crew + " p" + p.ProjectId + " " + p.Role + " " + p.Activity + " v=" + MxDev.F(p.MaxV) + "/" + MxDev.F(p.Lim.VFwd) +
                            " a=" + MxDev.F(p.MaxA) + "/" + MxDev.F(p.Lim.Accel) + " viol=" + p.ViolV + "/" + p.ViolA + (p.LodOut ? " LOD-OUT" : ""));
                }
                if (MxSpeed.LastLag.Length > 0) ctx.Log("rrw mx check lastFrontLag: " + MxSpeed.LastLag);
                if (MxSpeed.LastClamp.Length > 0) ctx.Log("rrw mx check lastClamp: " + MxSpeed.LastClamp);
            }
        }
    }

    // rrw.mx.zones: the machine report per project (zones, onCarriageway, report age, OpenLanes,
    // release state) and per puppet (zones now / over the committed plan, guard state, leave state).
    public sealed class MxZonesCommand : IDevCommand
    {
        public string Name => "rrw.mx.zones";
        public string Help => "rrw.mx.zones [p<id>] - machine report: per project zones / onCarriageway / report age / OpenLanes / release; per puppet zones now/planned, guard and leave state";

        public void Run(DevContext ctx, string[] a)
        {
            uint only = 0;
            if (a.Length > 0 && a[0].Length > 1 && a[0][0] == 'p') uint.TryParse(a[0].Substring(1), out only);
            uint ui = RRWClock.UpdateIndex;
            double now = MxDev.Now();
            ctx.Log("rrw mx zones update=" + ui + " samples=" + MachineReport.LastSamples + " lastGuard=" + MachineDebug.LastGuard);
            foreach (var pr in SiteRegistry.Projects.Values)
            {
                if (only != 0 && pr.Id != only) continue;
                var st = pr.Get<MachineProjectState>(ModuleSlot.Machines);
                var v = pr.View();
                var stg = PhasePlan.Stage(v);
                ctx.Log("rrw mx zones p" + pr.Id + " ready=" + RoadZoneMath.Describe(pr.WorkZonesReady) + (MachineDebug.LegacyReady ? "(LEGACY)" : "") +
                        " soft=" + RoadZoneMath.Describe(pr.SoftZones) + " switch=" + pr.Switch + " stage=" + stg.Index + "/" + stg.Count +
                        " works=" + RoadZoneMath.Describe(stg.Works) + " halves=" + v.HalvesActive + " swap=" + v.SwapActive +
                        " exits=" + (pr.ExitAtStart ? "start" : "-") + "/" + (pr.ExitAtEnd ? "end" : "-") + " exitU=" + MxDev.F(PhasePlan.ExitU(v)) +
                        " stuck=" + pr.MachinesStuck + " fenceLat=" + RRWGates.FenceLateral + (st != null && st.GateWhy.Length > 0 ? " gate=" + st.GateWhy : ""));
                ctx.Log("rrw mx zones p" + pr.Id + " " + pr.Kind + " " + pr.Phase + " f=" + MxDev.F(pr.PhaseFraction) + " zones=" + pr.MachineZones +
                        " onCarriageway=" + pr.MachinesOnCarriageway + " reportAge=" + (pr.MachinesReportUpdate == 0 ? "never" : unchecked((int)(ui - pr.MachinesReportUpdate)).ToString()) +
                        (pr.MachinesReportFresh(ui) ? " fresh" : " STALE") + " openLanes=" + pr.OpenLanes + " releaseSince=" + pr.ReleaseSince +
                        (pr.Releasing ? " releasingFor=" + unchecked((int)(ui - pr.ReleaseSince)) : "") + " closure=" + pr.Closure);
                foreach (var p in MachineRegistry.All)
                {
                    if (p.ProjectId != pr.Id) continue;
                    MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
                    ctx.Log("rrw mx zones   " + p + " " + (p.PostSite ? "LEAVING(" + p.LeaveWhy + ", " + unchecked((int)(ui - p.LeaveSince)) + " upd)" : p.Activity.ToString()) +
                            " u=" + MxDev.F(s.U) + " lat=" + MxDev.F(s.Lat) + " v=" + MxDev.F(s.Speed) + " leg=" + (LegKind)s.LegKind +
                            " zonesNow=" + p.ZonesNow + " zonesPlan=" + p.ZonesPlan +
                            (p.GuardHeld ? " GUARD-HELD for " + MxDev.F((float)(now - p.GuardHeldSince)) + "s by " + p.GuardBlocker : "") +
                            (p.StandStill ? " STAND-STILL by " + p.GuardBlocker : "") + (p.Stuck ? " STUCK ep=" + p.StuckEpisodes : "") +
                            (p.PostSite ? " exit=" + MxDev.F(p.LeaveEndU) + "(" + p.ExitWhy + ")" : "") + " cam=" + MxDev.F(p.CamDist));
                }
            }
        }
    }

    // rrw.mx.sig (arrow-board evaluation): force the arrow-board bits of one puppet. 0 = none, 1 = SignalAnimation1,
    // 2 = SignalAnimation2, 3 = both; auto = back to the product rule (painter in Paint: RRWGates.ArrowBoard, `rrw.gate arrow`).
    // Screenshot front and back at 100 and 300 m and compare what 1, 2 and 3 show.
    public sealed class MxSigCommand : IDevCommand
    {
        public string Name => "rrw.mx.sig";
        public string Help => "rrw.mx.sig <role|p<id>:<role>|#n|e<index>|all> <0|1|2|3|auto> - force SignalAnimation1/2 on a puppet (arrow-board evaluation); no value prints the state";

        public void Run(DevContext ctx, string[] a)
        {
            var targets = new List<Puppet>();
            if (a.Length > 0 && a[0].Equals("all", StringComparison.OrdinalIgnoreCase)) targets.AddRange(MachineRegistry.All);
            else { var p = MxDev.Find(a.Length > 0 ? a[0] : null); if (p != null) targets.Add(p); }
            if (targets.Count == 0) { ctx.Log("rrw mx sig: no such machine"); return; }
            int val = int.MinValue;
            if (a.Length > 1)
            {
                if (a[1].Equals("auto", StringComparison.OrdinalIgnoreCase) || a[1] == "-1") val = -1;
                else if (int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0 && v <= 3) val = v;
                else { ctx.Log("rrw mx sig: value must be 0..3 or auto"); return; }
            }
            double now = MxDev.Now();
            foreach (var p in targets)
            {
                if (val != int.MinValue) { p.SigOverride = val; p.PlanDirty = true; }
                bool auto = p.Role == MachineRole.Finisher && p.Activity == MachineActivity.Paint && !p.PostSite;
                ctx.Log("rrw mx sig " + p + " override=" + (p.SigOverride < 0 ? "auto" : p.SigOverride.ToString()) + " flags=" + (p.Plan.Flags & (TransformFlags.SignalAnimation1 | TransformFlags.SignalAnimation2)) +
                        " (applied within 8 updates) gate arrow=" + RRWGates.ArrowBoard +
                        (auto ? " painterRule=" + (MachineDirectorSystem.ArrowWorksOnRight(p, now) ? "works on its right -> SignalAnimation1" : "works on its left -> SignalAnimation2") : " (not a painting Finisher: no automatic arrow)"));
            }
        }
    }

    // rrw.mx.ready (diagnosis): `legacy` plans with the original rule (WorkZonesReady := AllLanes & ~OpenLanes & ~SoftZones) while
    // the Director does not publish WorkZonesReady yet; `contract` (default) uses ProjectRecord.WorkZonesReady.
    public sealed class MxReadyCommand : IDevCommand
    {
        public string Name => "rrw.mx.ready";
        public string Help => "rrw.mx.ready [contract|legacy] - machines plan into ProjectRecord.WorkZonesReady (contract, default) or the original lane rule (legacy, diagnosis only)";

        public void Run(DevContext ctx, string[] a)
        {
            if (a.Length > 0) MachineDebug.LegacyReady = a[0].Equals("legacy", StringComparison.OrdinalIgnoreCase) || a[0] == "1";
            ctx.Log("rrw mx ready mode=" + (MachineDebug.LegacyReady ? "legacy (AllLanes & ~OpenLanes & ~SoftZones)" : "contract (WorkZonesReady)"));
            foreach (var pr in SiteRegistry.Projects.Values)
                ctx.Log("rrw mx ready p" + pr.Id + " " + pr.Phase + " ready=" + RoadZoneMath.Describe(pr.WorkZonesReady) + " open=" + RoadZoneMath.Describe(pr.OpenLanes) +
                        " soft=" + RoadZoneMath.Describe(pr.SoftZones) + " switch=" + pr.Switch + " machineZones=" + RoadZoneMath.Describe(pr.MachineZones));
        }
    }

    // rrw.mx.dust: evaluate the excavator bucket dust. Product = Quiet clone (no bucket dust); 1 = the digger
    // respawns as "RRW Road Excavator" (Props' RRW Dust VFX when EffectsOk, else vanilla DustcloudSmallVFX) when DustOn.
    public sealed class MxDustCommand : IDevCommand
    {
        public string Name => "rrw.mx.dust";
        public string Help => "rrw.mx.dust [0|1] - bucket dust on the digger (evaluation only; respawns the digger, product default 0)";

        public void Run(DevContext ctx, string[] a)
        {
            if (a.Length > 0)
            {
                bool on = a[0] == "1" || a[0].Equals("on", StringComparison.OrdinalIgnoreCase);
                if (on != MachineDebug.BucketDust) { MachineDebug.BucketDust = on; MachineDebug.RespawnDiggers = true; }
            }
            ctx.Log("rrw mx dust bucket=" + (MachineDebug.BucketDust ? 1 : 0) + " dustyClone=" + MachinePrefabSystem.DustyCloneOk + " dust=" + MachinePrefabSystem.DustyCloneDust +
                    " effectsDone=" + RRWPrefabRegistry.EffectsDone + " effectsOk=" + RRWPrefabRegistry.EffectsOk +
                    " dustCloneCheck=" + RRWPrefabRegistry.DustCloneState + (MachinePrefabSystem.DustyCloneUsesRrwVfx && RRWPrefabRegistry.DustCloneState < 0 ? "(INVALID: stays Quiet)" : "") +
                    " dustVanilla=" + RRWPrefabRegistry.DustVanilla + (MachinePrefabSystem.DustyCloneUsesRrwVfx && RRWPrefabRegistry.DustVanilla ? "(clone dust while the vanilla source is published: stays Quiet)" : "") +
                    " dustOn=" + (RRWSettings.Current != null && RRWSettings.Current.DustOn));
        }
    }

    // rrw.mx.pose: live tuning of the bone keyframes (no rebuild needed: MachineBoneSystem reads them every frame).
    public sealed class MxPoseCommand : IDevCommand
    {
        public string Name => "rrw.mx.pose";
        public string Help => "rrw.mx.pose show | dig|grade|strip <key> [t=] [slew=0..1] [boom=] [stick=] [bucket=] | rest [boom=] [stick=] [bucket=] | break [boom=] [stick=] [bucket=] [chop=] [chopbucket=] [period=] | loader [rest=arm,claw] [low=arm,claw] [high=arm,claw] | sign [slew=+-1] [loader=+-1]";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = new List<string>();
            var kv = MxDev.Kv(a, pos);
            string what = pos.Count > 0 ? pos[0].ToLowerInvariant() : "show";
            switch (what)
            {
                case "dig":
                case "grade":
                case "strip":
                {
                    int k = pos.Count > 1 ? int.Parse(pos[1], CultureInfo.InvariantCulture) : 0;
                    var d = what == "grade" ? MachinePoses.Grade : what == "strip" ? MachinePoses.Strip : MachinePoses.Dig;
                    if (k < 0 || k >= d.GetLength(0)) throw new Exception("key 0.." + (d.GetLength(0) - 1));
                    if (kv.TryGetValue("t", out var v)) d[k, 0] = MxDev.Fl(v);
                    if (kv.TryGetValue("slew", out v)) d[k, 1] = MxDev.Fl(v);
                    if (kv.TryGetValue("boom", out v)) d[k, 2] = MxDev.Fl(v);
                    if (kv.TryGetValue("stick", out v)) d[k, 3] = MxDev.Fl(v);
                    if (kv.TryGetValue("bucket", out v)) d[k, 4] = MxDev.Fl(v);
                    break;
                }
                case "rest":
                {
                    if (kv.TryGetValue("boom", out var v)) MachinePoses.Rest[1] = MxDev.Fl(v);
                    if (kv.TryGetValue("stick", out v)) MachinePoses.Rest[2] = MxDev.Fl(v);
                    if (kv.TryGetValue("bucket", out v)) MachinePoses.Rest[3] = MxDev.Fl(v);
                    break;
                }
                case "break":
                {
                    if (kv.TryGetValue("boom", out var v)) MachinePoses.BreakBase[1] = MxDev.Fl(v);
                    if (kv.TryGetValue("stick", out v)) MachinePoses.BreakBase[2] = MxDev.Fl(v);
                    if (kv.TryGetValue("bucket", out v)) MachinePoses.BreakBase[3] = MxDev.Fl(v);
                    if (kv.TryGetValue("chop", out v)) MachinePoses.BreakStick = MxDev.Fl(v);
                    if (kv.TryGetValue("chopbucket", out v)) MachinePoses.BreakBucket = MxDev.Fl(v);
                    if (kv.TryGetValue("period", out v)) MachinePoses.BreakPeriod = math.max(0.5f, MxDev.Fl(v));
                    break;
                }
                case "loader":
                {
                    if (kv.TryGetValue("rest", out var v)) { var x = Pair(v); MachinePoses.LoaderRestArm = x.x; MachinePoses.LoaderRestClaw = x.y; }
                    if (kv.TryGetValue("low", out v)) { var x = Pair(v); MachinePoses.LoaderLowArm = x.x; MachinePoses.LoaderLowClaw = x.y; }
                    if (kv.TryGetValue("high", out v)) { var x = Pair(v); MachinePoses.LoaderHighArm = x.x; MachinePoses.LoaderHighClaw = x.y; }
                    break;
                }
                case "sign":
                {
                    if (kv.TryGetValue("slew", out var v)) MachinePoses.SlewSign = math.sign(MxDev.Fl(v));
                    if (kv.TryGetValue("loader", out v)) MachinePoses.LoaderSign = math.sign(MxDev.Fl(v));
                    break;
                }
            }
            var sb = new StringBuilder("rrw mx pose dig=");
            var dd = MachinePoses.Dig;
            for (int i = 0; i < dd.GetLength(0); i++)
                sb.Append('[').Append(i).Append(" t=").Append(MxDev.F(dd[i, 0])).Append(" slew=").Append(MxDev.F(dd[i, 1])).Append(" boom=").Append(MxDev.F(dd[i, 2]))
                  .Append(" stick=").Append(MxDev.F(dd[i, 3])).Append(" bucket=").Append(MxDev.F(dd[i, 4])).Append("] ");
            foreach (var pair in new[] { ("grade", MachinePoses.Grade), ("strip", MachinePoses.Strip) })
            {
                sb.Append(pair.Item1).Append('=');
                var g = pair.Item2;
                for (int i = 0; i < g.GetLength(0); i++)
                    sb.Append('[').Append(i).Append(" t=").Append(MxDev.F(g[i, 0])).Append(" slew=").Append(MxDev.F(g[i, 1])).Append(" boom=").Append(MxDev.F(g[i, 2]))
                      .Append(" stick=").Append(MxDev.F(g[i, 3])).Append(" bucket=").Append(MxDev.F(g[i, 4])).Append("] ");
            }
            sb.Append("rest=(").Append(MxDev.F(MachinePoses.Rest[1])).Append(',').Append(MxDev.F(MachinePoses.Rest[2])).Append(',').Append(MxDev.F(MachinePoses.Rest[3])).Append(')');
            sb.Append(" break=(").Append(MxDev.F(MachinePoses.BreakBase[1])).Append(',').Append(MxDev.F(MachinePoses.BreakBase[2])).Append(',').Append(MxDev.F(MachinePoses.BreakBase[3]))
              .Append(") chop=").Append(MxDev.F(MachinePoses.BreakStick)).Append('/').Append(MxDev.F(MachinePoses.BreakBucket)).Append(" period=").Append(MxDev.F(MachinePoses.BreakPeriod));
            sb.Append(" loader rest=").Append(MxDev.F(MachinePoses.LoaderRestArm)).Append(',').Append(MxDev.F(MachinePoses.LoaderRestClaw))
              .Append(" low=").Append(MxDev.F(MachinePoses.LoaderLowArm)).Append(',').Append(MxDev.F(MachinePoses.LoaderLowClaw))
              .Append(" high=").Append(MxDev.F(MachinePoses.LoaderHighArm)).Append(',').Append(MxDev.F(MachinePoses.LoaderHighClaw))
              .Append(" signs slew=").Append(MxDev.F(MachinePoses.SlewSign)).Append(" loader=").Append(MxDev.F(MachinePoses.LoaderSign));
            ctx.Log(sb.ToString());
        }

        static float2 Pair(string v)
        {
            var parts = v.Split(',');
            return new float2(MxDev.Fl(parts[0]), parts.Length > 1 ? MxDev.Fl(parts[1]) : 0f);
        }
    }

    // rrw.mx.trace: per rendered frame, the exact plan pose vs vanilla's interpolation of our 4 TransformFrames (errSlot:
    // slot/clock maths, should be < 0.02 m) and vs the rendered InterpolatedTransform (err, includes vanilla sway); jerk
    // spikes at ph=0 would be a 16-frame stutter.
    public sealed class MxTraceCommand : IDevCommand
    {
        public string Name => "rrw.mx.trace";
        public string Help => "rrw.mx.trace <role|p<id>:<role>|#n|e<index>> [frames=300] - per-frame pose / interpolation / jerk trace with a summary";

        private sealed class State
        {
            public Puppet P;
            public int Left, N, Culled;
            public float MaxErrSlot, MaxErr, MaxJerk, MaxJerkPh0, SumErr, MaxY;
            public bool HavePrev;
            public float3 PrevInterp, PrevExpect;
            // chain-space speed / acceleration per traced frame against the leg's limit
            public double PrevTau = double.NaN, VTau = double.NaN;
            public float PrevU, PrevLat, PrevV, MaxV, MaxA;
            public int VViol, AViol;
        }

        public void Run(DevContext ctx, string[] a)
        {
            var pos = new List<string>();
            var kv = MxDev.Kv(a, pos);
            var p = MxDev.Find(pos.Count > 0 ? pos[0] : null);
            if (p == null) { ctx.Log("rrw mx trace: no such machine"); return; }
            int frames = kv.TryGetValue("frames", out var fv) ? int.Parse(fv, CultureInfo.InvariantCulture) : (pos.Count > 1 ? int.Parse(pos[1], CultureInfo.InvariantCulture) : 300);
            var st = new State { P = p, Left = math.max(1, frames) };
            var rendering = ctx.System<RenderingSystem>();
            MachineDebug.Trace = em => Step(ctx, em, rendering, st);
            ctx.Log("rrw mx trace start " + p + " frames=" + frames);
        }

        private static void Step(DevContext ctx, EntityManager em, RenderingSystem rs, State st)
        {
            var p = st.P;
            if (!p.Alive(em) || !em.HasComponent<InterpolatedTransform>(p.E)) { ctx.Log("rrw mx trace abort (machine gone)"); MachineDebug.Trace = null; return; }
            uint frame = rs.frameIndex;
            float ft = rs.frameTime;
            var it = em.GetComponentData<InterpolatedTransform>(p.E);
            var tv = MachineTrackStore.View();
            if (!MachineMotion.Sample(p.Plan, tv, MachineRegistry.Clock, frame, ft, out float3 pos, out quaternion rot, out float3 vel, out TransformFlags flags)) return;
            bool culled = !em.HasComponent<CullingInfo>(p.E) || em.GetComponentData<CullingInfo>(p.E).m_CullingIndex == 0;
            float errSlot = -1f;
            uint u = 0;
            if (em.HasBuffer<TransformFrame>(p.E))
            {
                try
                {
                    u = em.GetSharedComponent<UpdateFrame>(p.E).m_Index;
                    var fr = em.GetBuffer<TransformFrame>(p.E, true);
                    if (fr.Length == 4)
                    {
                        ObjectInterpolateSystem.CalculateUpdateFrames(frame, ft, u, out uint s1, out uint s2, out float fp);
                        var calc = ObjectInterpolateSystem.CalculateTransform(fr[(int)s1], fr[(int)s2], fp);
                        errSlot = math.distance(calc.m_Position, pos);
                    }
                }
                catch { errSlot = -1f; }
            }
            uint phase = unchecked(frame - u - 32u) & 15u;
            float err = math.distance(it.m_Position, pos);
            float3 dp = st.HavePrev ? it.m_Position - st.PrevInterp : float3.zero;
            float3 edp = st.HavePrev ? pos - st.PrevExpect : float3.zero;
            float jerk = math.length(dp - edp);
            st.PrevInterp = it.m_Position;
            st.PrevExpect = pos;
            st.HavePrev = !culled;
            if (!culled)
            {
                st.N++;
                st.SumErr += err;
                st.MaxErr = math.max(st.MaxErr, err);
                st.MaxErrSlot = math.max(st.MaxErrSlot, errSlot);
                if (st.N > 1) { st.MaxJerk = math.max(st.MaxJerk, jerk); if (phase == 0) st.MaxJerkPh0 = math.max(st.MaxJerkPh0, jerk); }
            }
            else st.Culled++;
            double tau = MachineRegistry.Clock.Tau(frame, ft);
            MachineMotion.State(p.Plan, MachineRegistry.Clock, tau, out var s);
            // chain-space speed over >= kMonSpeedWindow and the change of the signed speed over
            // >= kMonAccelWindow (machine seconds), against the leg's limit (reverse / K-turn / leaving; Brake: kEmergencyDecel)
            float v = float.NaN, acc = float.NaN;
            bool rev = s.Speed < -0.05f;
            float vlim = MxSpeed.VLimit(p, s, rev), alim = MxSpeed.ALimit(p, s);
            if (double.IsNaN(st.PrevTau)) { st.PrevTau = tau; st.PrevU = s.U; st.PrevLat = s.Lat; }
            else if (tau - st.PrevTau >= MxConst.kMonSpeedWindow)
            {
                float du = s.U - st.PrevU, dl = s.Lat - st.PrevLat;
                v = (float)(math.sqrt(du * du + dl * dl) / (tau - st.PrevTau));
                float vs = du * s.Hu + dl * s.Hl < 0f ? -v : v;
                st.MaxV = math.max(st.MaxV, v);
                if (MachineLimits.SpeedViolates(v, vlim)) st.VViol++;
                double mid = 0.5 * (tau + st.PrevTau);   // chord midpoint
                if (double.IsNaN(st.VTau)) { st.VTau = mid; st.PrevV = vs; }
                else if (mid - st.VTau >= MxConst.kMonAccelWindow)
                {
                    acc = (float)((vs - st.PrevV) / (mid - st.VTau));
                    st.MaxA = math.max(st.MaxA, math.abs(acc));
                    if (MachineLimits.AccelViolates(acc, alim)) st.AViol++;
                    st.VTau = mid; st.PrevV = vs;
                }
                st.PrevTau = tau; st.PrevU = s.U; st.PrevLat = s.Lat;
            }
            ctx.Log("rrw mx trace f=" + frame + " ph=" + phase + (culled ? " CULLED" : "") + " leg=" + (LegKind)s.LegKind + " u=" + MxDev.F(s.U) +
                    " lat=" + MxDev.F(s.Lat) + " vWorld=" + MxDev.F(math.length(vel)) + " v=" + MxDev.F(v) + "/" + MxDev.F(vlim) +
                    " a=" + MxDev.F(acc) + "/" + MxDev.F(alim) + " errSlot=" + MxDev.F(errSlot) + " err=" + MxDev.F(err) +
                    " jerk=" + MxDev.F(jerk) + " y=" + MxDev.F(pos.y) + " flags=" + flags);
            if (--st.Left <= 0)
            {
                ctx.Log("rrw mx trace summary " + p + " frames=" + (st.N + st.Culled) + " culled=" + st.Culled + " maxErrSlot=" + MxDev.F(st.MaxErrSlot) +
                        " maxErr=" + MxDev.F(st.MaxErr) + " meanErr=" + MxDev.F(st.SumErr / math.max(1, st.N)) + " maxJerk=" + MxDev.F(st.MaxJerk) +
                        " maxJerkAtPhase0=" + MxDev.F(st.MaxJerkPh0) + " maxV=" + MxDev.F(st.MaxV) + " maxA=" + MxDev.F(st.MaxA) +
                        " speedViol=" + st.VViol + " accelViol=" + st.AViol + " (pass: errSlot < 0.02, jerk@ph0 ~ jerk, 0 violations)");
                MachineDebug.Trace = null;
            }
        }
    }
}
#endif
