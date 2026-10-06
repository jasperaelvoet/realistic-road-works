#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Game.Prefabs;
using Game.Rendering;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;

// Roller and dig dev commands: rrw.mx.roller and rrw.mx.dig. They read state or flip managed tuning values;
// structural work (spawning / clearing rollers) is requested and done by MachineDirectorSystem at Modification1.
namespace RealisticRoadWorks.V3.Machines
{
    // rrw.mx.roller [p<id>] [list | spawn <crew> <0|1> | clear | lod <d0> <d1> | beacon <nits>]
    public sealed class MxRollerCommand : IDevCommand
    {
        public string Name => "rrw.mx.roller";
        public string Help => "rrw.mx.roller [p<id>] [list|spawn <crew> <0|1>|clear|lod <d0> <d1>|beacon <nits>] - road roller units: crew, duty, window, pass, lateral, v, vibration, LOD, in view, articulation, root Y error; spawn forces a roller at its anchor, clear removes them (the roster respawns), lod / beacon tune the renderer";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = new List<string>();
            MxDev.Kv(a, pos);
            uint only = 0;
            if (pos.Count > 0 && pos[0].Length > 1 && pos[0][0] == 'p' && uint.TryParse(pos[0].Substring(1), out only)) pos.RemoveAt(0);
            string what = pos.Count > 0 ? pos[0].ToLowerInvariant() : "list";
            switch (what)
            {
                case "spawn":
                {
                    int crew = pos.Count > 1 ? int.Parse(pos[1], CultureInfo.InvariantCulture) : 0;
                    int idx = pos.Count > 2 ? int.Parse(pos[2], CultureInfo.InvariantCulture) : 0;
                    MachineDebug.ForceRollerProject = only;
                    MachineDebug.ForceRollerCrew = crew;
                    MachineDebug.ForceRollerIndex = math.clamp(idx, 0, RRWConst.kMaxRollersPerCrew - 1);
                    MachineDebug.ForceRollerUntil = RRWClock.UpdateIndex + 600u;
                    ctx.Log("rrw mx roller spawn requested p" + (only == 0 ? "any" : only.ToString()) + " crew " + crew + " roller " + idx +
                            " (next update with an open Compact slot: at its anchor, no in-view deferral)");
                    return;
                }
                case "clear":
                    MachineDebug.ClearRollers = true;
                    ctx.Log("rrw mx roller clear requested (every roller unit removed next update; the roster respawns per its rules)");
                    return;
                case "lod":
                    if (pos.Count > 2)
                    {
                        RollerRenderSystem.Lod0 = MxDev.Fl(pos[1]);
                        RollerRenderSystem.Lod1 = math.max(RollerRenderSystem.Lod0, MxDev.Fl(pos[2]));
                    }
                    ctx.Log("rrw mx roller lod0<" + MxDev.F(RollerRenderSystem.Lod0) + " lod1<" + MxDev.F(RollerRenderSystem.Lod1) + " (defaults " +
                            MxDev.F(RRWConst.kRollerLod0Distance) + "/" + MxDev.F(RRWConst.kRollerLod1Distance) + ")");
                    return;
                case "beacon":
                    if (pos.Count > 1) { RollerMaterials.BeaconNits = math.max(0f, MxDev.Fl(pos[1])); RollerMaterials.ResetBeaconLevels(); }
                    ctx.Log("rrw mx roller beacon nits=" + MxDev.F(RollerMaterials.BeaconNits) + " (default " + MxDev.F(RRWConst.kRollerBeaconNits) + ")");
                    return;
            }
            double now = MxDev.Now();
            ctx.Log("rrw mx roller " + MachineChecks.Round5Summary() + " lod0<" + MxDev.F(RollerRenderSystem.Lod0) + " lod1<" + MxDev.F(RollerRenderSystem.Lod1) +
                    " liveRootObjects=" + RollerWorld.LiveRootObjects() + " rollersOn=" + (RRWSettings.Current != null && RRWSettings.Current.RollersOn) +
                    " shader=" + RollerMaterials.Source + " lastFrameMs=" + MxDev.F(RollerRenderSystem.LastFrameMs));
            foreach (var p in MachineRegistry.All)
            {
                if (!p.IsRoller || (only != 0 && p.ProjectId != only)) continue;
                MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
                var u = p.Unit;
                string win = "-";
                if (SiteRegistry.TryGetProject(p.ProjectId, out var pr) && p.Crew < pr.View().CrewCount)
                {
                    var rs = PhasePlan.Crew(pr.View(), p.Crew).Roller(p.RollerIndex);
                    win = rs.Slot.Activity + "[" + MxDev.F(rs.WinLo) + "," + MxDev.F(rs.WinHi) + "]" + (rs.RelayOnly ? " relay" : "") + (rs.WorksHalfOnly ? " worksHalf" : "");
                }
                ctx.Log("rrw mx roller " + p + " " + (p.PostSite ? "LEAVING(" + p.LeaveWhy + (p.DeadEndLeave ? ", DEAD-END " + p.DeadEndWhy : "") + ")" : p.Activity + " " + p.Duty) +
                        " window=" + win + " pass=" + p.PassK + " leg=" + s.Leg + "/" + p.Plan.Count + ":" + (LegKind)s.LegKind + " u=" + MxDev.F(s.U) + " lat=" + MxDev.F(s.Lat) +
                        " v=" + MxDev.F(s.Speed) + " vib=" + (s.Anim == (byte)AnimKind.Compact && math.abs(s.Speed) > 0.15f) +
                        " lod=" + (u != null ? u.Lod.ToString() : "-") + " inView=" + p.InView + " cam=" + MxDev.F(p.CamDist) + " culled=" + p.CulledUpdates +
                        " artic=" + (u != null ? MxDev.F(u.ArticulationDeg) : "-") + " rootYErr=" + MxDev.F(p.RootYErr) +
                        " flags=" + p.Plan.Flags + " vmax=" + MxDev.F(p.MaxV) + "/" + MxDev.F(p.Lim.VFwd) + " amax=" + MxDev.F(p.MaxA) + "/" + MxDev.F(p.Lim.Accel) +
                        (p.ViolV + p.ViolA > 0 ? " VIOLATIONS(" + p.ViolV + "/" + p.ViolA + ")" : "") + " replanIn=" +
                        (double.IsPositiveInfinity(p.ReplanAt) ? "inf" : MxDev.F((float)(p.ReplanAt - now))) + (p.GuardHeld ? " GUARD-HELD" : "") + (p.Stuck ? " STUCK" : ""));
            }
        }
    }

    // rrw.mx.dig <puppet|all> [bite=<m>|bite=default] [trace <s>] [pose]
    public sealed class MxDigCommand : IDevCommand
    {
        public string Name => "rrw.mx.dig";
        public string Help => "rrw.mx.dig <role|p<id>:<role>|#n|all> [bite=<m>|bite=default] [trace <seconds>] [pose] - IK dig cycle: schedule, key, events, dump target (truck / spoil, why), truck clearance, floorH, the live tip vs target error, limits, piston misalignment / twist";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = new List<string>();
            var kv = MxDev.Kv(a, pos);
            if (kv.TryGetValue("bite", out var bs))
            {
                MachineDebug.DigBite = bs.Equals("default", StringComparison.OrdinalIgnoreCase) ? float.NaN : math.max(0f, MxDev.Fl(bs));
                foreach (var q in MachineRegistry.All) if (q.Dig != null) q.Dig.Next = null;   // rebuilt at the next cycle boundary (no pose pop)
                ctx.Log("rrw mx dig bite=" + (float.IsNaN(MachineDebug.DigBite) ? "default " + MxDev.F(RRWConst.kDigBite) : MxDev.F(MachineDebug.DigBite)) +
                        " (applies from the next cycle of every IK excavator)");
            }
            var targets = new List<Puppet>();
            string sel = pos.Count > 0 ? pos[0] : "all";
            if (sel.Equals("all", StringComparison.OrdinalIgnoreCase)) { foreach (var q in MachineRegistry.All) if (q.ExcavatorRig) targets.Add(q); }
            else { var q = MxDev.Find(sel); if (q != null) targets.Add(q); }
            int ti = pos.IndexOf("trace");
            if (ti >= 0 && targets.Count > 0)
            {
                float secs = ti + 1 < pos.Count ? MxDev.Fl(pos[ti + 1]) : 20f;
                MachineDebug.DigTrace = targets[0];
                MachineDebug.DigTraceUntil = MxDev.Now() + secs;
                ctx.Log("rrw mx dig trace " + targets[0] + " for " + MxDev.F(secs) + " machine s (one line per rendered frame)");
            }
            bool pose = pos.Contains("pose");
            var em = ctx.EntityManager;
            double now = MxDev.Now();
            ctx.Log("rrw mx dig settings detailed=" + (RRWSettings.Current != null && RRWSettings.Current.DetailedDiggingOn) + " dust=" + (RRWSettings.Current != null && RRWSettings.Current.DigDustOn) +
                    " dustPuffOk=" + RRWPrefabRegistry.DustPuffOk + " limits(boom " + MxDev.F(DigLimits.BoomMin) + ".." + MxDev.F(DigLimits.BoomMax) + " stick " + MxDev.F(DigLimits.StickMin) + ".." +
                    MxDev.F(DigLimits.StickMax) + " bucket " + MxDev.F(DigLimits.BucketMin) + ".." + MxDev.F(DigLimits.BucketMax) + ")" + " " + MachineChecks.Round5Summary());
            foreach (var p in targets)
            {
                var d = p.Dig;
                MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
                var sb = new StringBuilder("rrw mx dig " + p + " leg=" + (LegKind)s.LegKind + " anim=" + (AnimKind)s.Anim + " t=" + MxDev.F(s.AnimTime));
                bool ik = MachineDirectorSystem.IkLeg(p.Plan, now, out var leg, out _);
                sb.Append(" ikLeg=").Append(ik).Append(" grid=").Append(MxDev.F(leg.P0)).Append("s hop=").Append(MxDev.F(leg.P1)).Append('+').Append(MxDev.F(leg.P2));
                // The crew dig grid (origin phase now, epoch, re-phases, re-plans that kept the running cycle, truck re-plans)
                if (MachineRegistry.States.TryGetValue(p.ProjectId, out var mst) && p.Crew >= 0 && p.Crew < mst.Crews.Length)
                {
                    var cs6 = mst.Crews[p.Crew];
                    double ph6 = double.IsNaN(cs6.DigOrigin) || !(cs6.DigC > 0f) ? double.NaN : (now - cs6.DigOrigin) % cs6.DigC;
                    if (ph6 < 0) ph6 += cs6.DigC;
                    sb.Append(" crewGrid(C=").Append(MxDev.F(cs6.DigC)).Append(" phase=").Append(double.IsNaN(ph6) ? "nan" : MxDev.F((float)ph6))
                      .Append(" epoch=").Append(cs6.GridEpoch).Append(" rephases=").Append(cs6.Rephases).Append(" continued=").Append(cs6.Continued)
                      .Append(" truckGridReplans=").Append(MachineDebug.TruckGridReplans).Append(')');
                    for (int r6 = (int)MachineRole.TruckA; r6 <= (int)MachineRole.TruckB; r6++)
                    {
                        var t6 = cs6.Roles[r6];
                        if (t6 == null) continue;
                        MachineMotion.State(t6.Plan, MachineRegistry.Clock, now, out var ts6);
                        sb.Append(' ').Append(t6.Role).Append("(stage=").Append(t6.Stage).Append(" leg=").Append((LegKind)ts6.LegKind).Append('/').Append((AnimKind)ts6.Anim)
                          .Append(" buckets=").Append(t6.Buckets).Append(" load=").Append(t6.AmountPct).Append("% epoch=").Append(t6.GridEpoch).Append(')');
                    }
                }
                if (d == null) { ctx.Log(sb.Append(" no IK state (keyframe animation)").ToString()); continue; }
                sb.Append(" arm=").Append(d.Arm != null ? "ok" : "none");
                if (d.Cur != null)
                {
                    var k = d.Cur;
                    float ct = (float)(now - k.CycleStart);
                    var key = k.Sched.KeyAt(ct, out float ku);
                    sb.Append(" | cycle t=").Append(MxDev.F(ct)).Append(" key=").Append(key).Append('@').Append(MxDev.F(ku)).Append(' ').Append(k.SummaryText())
                      .Append(" | schedule lower..return=").Append(MxDev.F(k.Sched.TLower)).Append('/').Append(MxDev.F(k.Sched.TPenetrate)).Append('/').Append(MxDev.F(k.Sched.TDrag)).Append('/')
                      .Append(MxDev.F(k.Sched.TCurl)).Append('/').Append(MxDev.F(k.Sched.TLift)).Append('/').Append(MxDev.F(k.Sched.TSwing)).Append('/').Append(MxDev.F(k.Sched.TDump)).Append('/')
                      .Append(MxDev.F(k.Sched.TShake)).Append('/').Append(MxDev.F(k.Sched.TReturn)).Append(" stationaryEnd=").Append(MxDev.F(k.Sched.StationaryEnd))
                      .Append(" hopDefault/max=").Append(MxDev.F(k.Sched.HopDefault)).Append('/').Append(MxDev.F(k.Sched.HopWindowMax));
                }
                if (d.Next != null) sb.Append(" next@").Append(MxDev.F((float)(d.Next.CycleStart - now))).Append("s ").Append(d.Next.TargetWhy);
                sb.Append(" | events breakouts=").Append(d.Breakouts).Append(" dumps=").Append(d.Dumps).Append(" (truck ").Append(d.TruckDumps).Append(" spoil ").Append(d.SpoilDumps)
                  .Append(") strikes=").Append(d.Strikes).Append(" builds=").Append(d.Builds).Append(" reuses=").Append(d.Reuses)
                  .Append(" provisional=").Append(d.Provisionals).Append(" spoilOutside=").Append(d.SpoilOutsideCycles).Append(" worldFloorGap=").Append(MxDev.F(d.WorldFloorGap))
                  .Append(" lastSpoil=[").Append(d.LastSpoil).Append(']')
                  .Append(" | floorH=").Append(MxDev.F(d.LastFloorH)).Append(" lastErr=").Append(MxDev.F(d.LastErr)).Append(" maxReachedErr=").Append(MxDev.F(d.MaxErr))
                  .Append(" tipBelowFloorMax=").Append(MxDev.F(d.TipBelowFloor)).Append(" truckClearMin=").Append(float.IsPositiveInfinity(d.TruckClearMin) ? "n/a" : MxDev.F(d.TruckClearMin))
                  .Append(" target(slew ").Append(MxDev.F(d.LastTarget.Slew)).Append(" r ").Append(MxDev.F(d.LastTarget.R)).Append(" h ").Append(MxDev.F(d.LastTarget.H))
                  .Append(" att ").Append(MxDev.F(d.LastTarget.Att)).Append(") limits=").Append(d.LastRes.LimitsText()).Append(d.LastRes.Unreachable ? " UNREACHABLE" : "")
                  .Append(d.LastRes.FloorRaised ? " floorRaised" : "");
                if (p.IsTruck == false && d.Arm != null)
                {
                    string live = LiveText(em, ctx.System<PrefabSystem>(), p, d, pose);
                    sb.Append(" | ").Append(live);
                }
                ctx.Log(sb.ToString());
            }
        }

        // Live rig readback: the rendered bucket tip vs the model tip (modelVsLive), and the pistons' misalignment / twist (< 2 deg).
        private static string LiveText(EntityManager em, PrefabSystem ps, Puppet p, DigState d, bool pose)
        {
            try
            {
                var rs = d.Arm.Rs;
                if (!p.Alive(em) || !em.HasBuffer<Skeleton>(p.E) || !em.HasBuffer<Bone>(p.E)) return "live: no skeleton";
                var skels = em.GetBuffer<Skeleton>(p.E, true);
                if (rs.Sub < 0 || rs.Sub >= skels.Length) return "live: no sub skeleton";
                var sk = skels[rs.Sub];
                if (sk.m_BoneOffset < 0) return "live: skeleton not allocated";
                var bones = em.GetBuffer<Bone>(p.E, true);
                int n = rs.Names.Length;
                if (sk.m_BoneOffset + n > bones.Length) return "live: bone range";
                var now = new float4x4[n];
                var rest = new float4x4[n];
                for (int i = 0; i < n; i++)
                {
                    var b = bones[sk.m_BoneOffset + i];
                    var local = float4x4.TRS(b.m_Position, b.m_Rotation, b.m_Scale);
                    int par = rs.Parent[i];
                    now[i] = par >= 0 && par < i ? math.mul(now[par], local) : local;
                    var lr = float4x4.TRS(rs.Pos[i], rs.Rest[i], rs.RawScale[i]);
                    rest[i] = par >= 0 && par < i ? math.mul(rest[par], lr) : lr;
                }
                float3 tip = d.Arm.TipOf(now);
                var sb = new StringBuilder("live tip=" + V(tip) + " model=" + V(d.LastRes.TipObj) + " modelVsLive=" + MxDev.F(math.distance(tip, d.LastRes.TipObj)));
                float worstMis = 0f, worstTwist = 0f;
                foreach (var pp in Rigs.Get(em, ps, p.Prefab).Pistons)
                {
                    if (pp.Sub != rs.Sub) continue;
                    int a = pp.A, bb = pp.B;
                    float3 rDir = rest[bb].c3.xyz - rest[a].c3.xyz;
                    float3 cDir = now[bb].c3.xyz - now[a].c3.xyz;
                    float3 axA = math.rotate(math.mul(RotOf(now[a]), math.inverse(RotOf(rest[a]))), rDir);
                    float3 axB = math.rotate(math.mul(RotOf(now[bb]), math.inverse(RotOf(rest[bb]))), -rDir);
                    float misA = Angle(axA, cDir), misB = Angle(axB, -cDir);
                    // twist about the cylinder axis vs the carried orientation (parent pose x rest local)
                    float twA = Twist(rs, now, a, cDir), twB = Twist(rs, now, bb, -cDir);
                    worstMis = math.max(worstMis, math.max(misA, misB));
                    worstTwist = math.max(worstTwist, math.max(math.abs(twA), math.abs(twB)));
                    sb.Append(" ").Append(rs.Names[a]).Append("(mis ").Append(MxDev.F(misA)).Append('/').Append(MxDev.F(misB)).Append(" twist ").Append(MxDev.F(twA)).Append('/').Append(MxDev.F(twB)).Append(')');
                }
                sb.Append(" worstMisalignDeg=").Append(MxDev.F(worstMis)).Append(" worstTwistDeg=").Append(MxDev.F(worstTwist)).Append(worstMis < 2f && worstTwist < 2f ? " PISTONS OK" : " PISTONS FAIL");
                if (pose) sb.Append(" pose=").Append(d.LastRes.Pose).Append(" boomElev=").Append(MxDev.F(d.LastRes.BoomElev)).Append(" stickRel=").Append(MxDev.F(d.LastRes.StickRel))
                             .Append(" bucketRel=").Append(MxDev.F(d.LastRes.BucketRel)).Append(" links: ").Append(d.Arm.LinksText());
                return sb.ToString();
            }
            catch (Exception e) { return "live: " + e.Message; }
        }

        static float Twist(RigSub rs, float4x4[] now, int i, float3 axis)
        {
            int par = rs.Parent[i];
            quaternion parRot = par >= 0 ? RotOf(now[par]) : quaternion.identity;
            quaternion carried = math.normalize(math.mul(parRot, rs.Rest[i]));
            quaternion cur = RotOf(now[i]);
            quaternion rel = math.mul(cur, math.inverse(carried));
            float3 n = math.normalizesafe(axis);
            float t = 2f * math.atan2(math.dot(rel.value.xyz, n), rel.value.w);
            return DigArm.Wrap180(math.degrees(t));
        }

        static quaternion RotOf(float4x4 m)
        {
            float3 c0 = m.c0.xyz, c1 = m.c1.xyz, c2 = m.c2.xyz;
            float l0 = math.length(c0), l1 = math.length(c1), l2 = math.length(c2);
            if (l0 < 1e-6f || l1 < 1e-6f || l2 < 1e-6f) return quaternion.identity;
            return math.normalize(new quaternion(new float3x3(c0 / l0, c1 / l1, c2 / l2)));
        }

        static float Angle(float3 a, float3 b)
        {
            float la = math.length(a), lb = math.length(b);
            if (la < 1e-6f || lb < 1e-6f) return 0f;
            return math.degrees(math.acos(math.clamp(math.dot(a, b) / (la * lb), -1f, 1f)));
        }

        static string V(float3 v) => "(" + MxDev.F(v.x) + "," + MxDev.F(v.y) + "," + MxDev.F(v.z) + ")";
    }
}
#endif
