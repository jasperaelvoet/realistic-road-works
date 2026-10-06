using System;
using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using ObjTransform = Game.Objects.Transform;

// The Modification1 side of the IK dig cycle.
//  * Cycle builds: for every excavator whose running leg is an IK DigHop (P0 = kDigCyclePeriod), the keys of the current cycle (at
//    its boundary, from the floor under the bite, the loading truck that holds at its stop for the whole cycle, else the spoil side)
//    and, in the last part of the cycle, the keys of the next one (the bone pass switches at the boundary: no pose pop).
//  * Events: DigSchedule's Breakout / Dump times on the excavator's own dig grid in (lastT, now] - exact for any frame step, one per
//    crossed cycle in a time-lapse, none while paused (machine clock).
//  * Truck loads: each Dump event of a cycle that targeted truck T sets T's DeliveryTruck amount to DigLoad.Amount(++T.Buckets).
//  * Dust puffs (DigDustOn and RRWPrefabRegistry.DustPuffOk): one "RRW Dust Puff" emitter per Breakout (tip on the floor) and Dump
//    (tip over the bed / spoil), within the budget; deleted kDustPuffLifeSeconds machine s later (LivePath, DerivedGroup.DustPuff).
namespace RealisticRoadWorks.V3.Machines
{
    // IK state of one excavator rig puppet (Puppet.Dig).
    public sealed class DigState
    {
        public DigArm Arm;
        public DigKeys Cur, Next;               // keys of the cycle starting at Cur.CycleStart / the following one
        public double LastEventT = double.NaN;  // machine time the event pass last looked at (NaN = start: no events)
        public double LastPuffT = double.NegativeInfinity;
        public bool UsedIk;                     // the bone pass solved the IK in the last frame (blend source = ReadyPoseLast)
        public float4 ReadyPose;                // bone angles of the ready pose
        public bool HaveReady;
        public float4 ReadyPoseLast;            // the last IK pose drawn (blend source when the IK leg ends / a new leg blends in)
        public bool HaveLast => UsedIk || LastPoseValid;
        public bool LastPoseValid;
        public float BiteR = float.NaN;         // planar reach of the bite point (floor sample) for this arm
        public DigKeys LastTruckFit, LastSpoilFit;   // the last full builds (their dump pick / swing fit is reused while the layout holds)
        // stats (rrw.mx.dig / rrw.mx.check)
        public float LastErr, MaxErr, TipBelowFloor, TruckClearMin = float.PositiveInfinity, LastFloorH = float.NaN;
        public int Breakouts, Dumps, TruckDumps, SpoilDumps, Builds, Reuses, Strikes;
        public int Provisionals, SpoilOutsideCycles;    // budget-deferred cycle builds; spoil dumps with no point inside
        public float WorldFloorGap;                     // DEVTOOLS: max |tip world Y - (track floor - bite + clear)| in the drag
        public string LastSpoil = "";
        public long Frames;
        public DigTarget LastTarget;
        public DigIkResult LastRes;
        public float LastCycleT;
        public string LastWhy = "";
    }

    public partial class MachineDirectorSystem
    {
        // ---- dust puffs
        private struct Puff { public Entity E; public double Die; public uint ProjectId; }
        private static readonly List<Puff> s_Puffs = new List<Puff>(32);
        private static ushort[] s_PhaseSeeds;   // seed whose kLightState random puts the effect phase at 0 (per wanted offset)
        private static uint s_PhaseDur;
        public static int PuffsLive => s_Puffs.Count;
        public static int PuffsSpawned, PuffsSkipped, PuffsDeleted, PuffsTooOld;
        public static string LastPuffSkip = "";
        // ---- dev / checks
        public static int DigBuildsFrame, LoadMismatch;
        public static string LastLoadMismatch = "";
        private static long s_DigBudgetTicks;
        private const double kDigBuildBudgetMs = 1.5;   // cycle builds per update beyond the first (current-cycle builds too)
        private const double kProvisionalForceSeconds = 1.0;   // provisional keys get their full build at the latest this long before TLift
        private static double BudgetMs() => s_DigBudgetTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static readonly Dictionary<Entity, TruckGeo> s_TruckGeo = new Dictionary<Entity, TruckGeo>();

        private struct TruckGeo { public float3 Min, Max, Bed; public float Top; }

        internal static void ClearDigStatics()
        {
            s_Puffs.Clear();
            s_TruckGeo.Clear();
            DigArms.Clear();
        }

        // Every update (after the per-puppet step): cycle builds, events, truck loads, dust puffs, puff expiry.
        private void DigPass(EntityManager em, RRWSetting settings, double now)
        {
            MxPerf.Begin(MxT.E_Events);
            try
            {
                ExpirePuffs(em, now);
                s_DigBudgetTicks = 0;
                DigBuildsFrame = 0;
                var tv = MachineTrackStore.View();
                if (!tv.IsCreated) return;
                var all = MachineRegistry.All;
                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i];
                    if (p.IsRoller || p.Kind != MachineKind.Excavator || p.Plan.Count <= 0) continue;
                    try { DigPuppet(em, p, settings, tv, now); }
                    catch (Exception e) { RRWLog.ErrorOnce("machines dig events", e); }
                }
            }
            finally { MxPerf.End(MxT.E_Events); }
        }

        // The IK DigHop leg running at `now` (P0 = kDigCyclePeriod, anim Dig / Break), or false.
        internal static bool IkLeg(in MachinePlan plan, double now, out MachineLeg leg, out int index)
        {
            leg = default;
            index = -1;
            if (plan.Count <= 0) return false;
            int i = MachineMotion.ActiveLeg(plan, now - plan.Epoch);
            var l = plan.Get(i);
            if (l.Kind != (byte)LegKind.DigHop || (l.Anim != (byte)AnimKind.Dig && l.Anim != (byte)AnimKind.Break)) return false;
            if (math.abs(l.P0 - RRWConst.kDigCyclePeriod) > 0.01f) return false;
            leg = l;
            index = i;
            return true;
        }

        private void DigPuppet(EntityManager em, Puppet p, RRWSetting settings, in TrackView tv, double now)
        {
            if (!IkLeg(p.Plan, now, out var leg, out _))
            {
                if (p.Dig != null) p.Dig.LastEventT = double.NaN;
                return;
            }
            if (p.Dig == null) p.Dig = new DigState();
            var d = p.Dig;
            if (d.Arm == null) d.Arm = DigArms.Get(em, m_PrefabSystem, p.Prefab, p.Scale);
            if (d.Arm == null) return;
            double origin = p.Plan.Epoch + leg.T0;
            float C = leg.P0;
            double k = math.floor((now - origin) / C);
            double start = origin + k * C;
            // current cycle keys (at the boundary: promote the prepared next keys, else build now). One full build per
            // update always runs; beyond kDigBuildBudgetMs the cycle starts with PROVISIONAL keys (Ready -> Lower -> ... -> Lift do not depend
            // on the dump target, so the keys of segments 0..5 are identical) and the full build follows within the budget before the swing.
            if (d.Cur == null || math.abs(d.Cur.CycleStart - start) > 0.01)
            {
                if (d.Next != null && math.abs(d.Next.CycleStart - start) <= 0.01) { d.Cur = d.Next; d.Next = null; }
                else d.Cur = BuildKeys(em, p, d, tv, leg, start, d.Cur, s_DigBudgetTicks > 0 && BudgetMs() >= kDigBuildBudgetMs);
            }
            double tk = now - start;
            if (d.Cur != null && d.Cur.Provisional && math.abs(d.Cur.CycleStart - start) <= 0.01 &&
                (tk >= d.Cur.Sched.TLift - kProvisionalForceSeconds || BudgetMs() < kDigBuildBudgetMs))
            {
                var full = BuildKeys(em, p, d, tv, leg, start, d.Cur, false);
                if (full != null && !full.Provisional) d.Cur = full;
            }
            // next cycle keys, prepared in the last part of this cycle (budgeted; a miss is built at the boundary)
            if (d.Cur != null && tk >= math.min(d.Cur.Sched.TShake, C - 3.0) && (d.Next == null || math.abs(d.Next.CycleStart - (start + C)) > 0.01)
                && BudgetMs() < kDigBuildBudgetMs)
                d.Next = BuildKeys(em, p, d, tv, leg, start + C, d.Cur, false);
            // events in (lastT, now]
            double lastT = d.LastEventT;
            d.LastEventT = now;
            if (double.IsNaN(lastT) || !(now > lastT)) return;
            double k0 = math.floor((lastT - origin) / C);
            for (double j = math.max(k0, -1.0); j <= k && j - k0 < 64; j++)
            {
                double cs = origin + j * C;
                if (cs + C <= origin) continue;
                var keys = d.Cur != null && math.abs(d.Cur.CycleStart - cs) <= 0.01 ? d.Cur : d.Next != null && math.abs(d.Next.CycleStart - cs) <= 0.01 ? d.Next : null;
                var sched = keys != null ? keys.Sched : DigSchedule.Nominal(leg.Anim == (byte)AnimKind.Break ? DigMode.Break : DigMode.Dig);
                double tb = cs + sched.BreakoutT, td = cs + sched.DumpT;
                if (tb > lastT && tb <= now && tb >= origin) OnBreakout(em, p, d, keys, tv, tb, cs, settings);
                if (td > lastT && td <= now && td >= origin) OnDump(em, p, d, keys, tv, td, cs, settings);
                if (sched.Mode == DigMode.Break)
                    for (int s = 0; s < RRWConst.kBreakStrikes; s++)
                    {
                        double ts = cs + sched.StrikeT(s);
                        if (ts > lastT && ts <= now && ts >= origin) d.Strikes++;
                    }
            }
        }

        // Keys of the cycle starting at `start`: floor under the bite, grade, the truck that holds for the whole cycle (or the spoil
        // side), reusing the previous fit when nothing relevant changed (the truck hops with the excavator: the same relative box).
        // provisional: no truck / spoil search and no fit with a box: a plain spoil cycle whose segments 0..5 equal the
        // full build's (DigPuppet replaces it before TLift); it never sets the crew's DigSched.
        private DigKeys BuildKeys(EntityManager em, Puppet p, DigState d, in TrackView tv, in MachineLeg leg, double start, DigKeys prev, bool provisional)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var arm = d.Arm;
                var mode = leg.Anim == (byte)AnimKind.Break ? DigMode.Break : DigMode.Dig;
                MachineMotion.State(p.Plan, MachineRegistry.Clock, start + 0.05, out var s);
                if (!MachineMotion.Pose(p.Plan, tv, s, out float3 root, out quaternion rot)) return prev;
                if (float.IsNaN(d.BiteR)) d.BiteR = math.max(arm.FrontClear + 1.0f, 0.6f * arm.ReachAt(0f, DigKeys.kAttPen));
                float2 h = math.normalizesafe(new float2(s.Hu, s.Hl), new float2(1f, 0f));
                float reach = arm.C.z + d.BiteR;
                float ub = s.U + h.x * reach, lb = s.Lat + h.y * reach;
                int slot = p.Plan.Track;
                // Heights in the machine's OBJECT space. The rig is pitched / rolled with the floor (MachineMotion.Pose),
                // so a world-Y difference at the bite several metres ahead is off by slope x distance; the world point goes through the
                // inverse root rotation instead (DigFloor.ObjH).
                float gradeH = 0f, floorH = 0f;
                if (tv.Point(slot, s.U, out var tpm)) gradeH = math.max(0f, DigFloor.ObjH(DigFloor.AtLat(tpm, s.Lat, tpm.Pos.y), root, rot, arm.S));
                if (mode == DigMode.Break)
                {
                    floorH = tv.Point(slot, ub, out var tpb) ? DigFloor.ObjH(DigFloor.AtLat(tpb, lb, tpb.Pos.y), root, rot, arm.S) : gradeH;   // the road surface (curve Y)
                    gradeH = math.max(gradeH, floorH);
                }
                else if (DigFloor.World(tv, slot, ub, lb, out float3 fw)) floorH = DigFloor.ObjH(fw, root, rot, arm.S);
                float bite = !float.IsNaN(MachineDebug.DigBite) ? MachineDebug.DigBite : RRWConst.kDigBite;
                // mode H band crews work a visible road: a surface scrape / break cycle at grade (no trench), dumping to the front
                bool uw = p.Upgrade && p.UwBand >= 0;
                float farShare = uw ? MxConst.kUwDragShare : 1f;
                if (uw) bite = 0f;
                float bite0 = mode == DigMode.Break ? 0f : bite;
                // the truck: holds at its LoadAtFront stop (its Load DigHop leg and the Load hold after it) from the cycle start to the end
                // of the shake, the same dig grid. Checked against the shake of the schedule the planner used for the
                // trucks (CrewState.DigSched, never later than nominal) and, after the build, against the shake of THESE keys.
                var crew = CrewOf(p);
                var nominal = DigSchedule.Nominal(mode);
                float reqShake = nominal.TShake;
                if (crew != null && crew.DigSched.Period > 0f) reqShake = math.min(reqShake, crew.DigSched.TShake);
                string why = "provisional (build budget)";
                double present = double.NegativeInfinity;
                Puppet truck = provisional ? null : TruckTarget(p, start, reqShake, out why, out present);
                bool haveTruck = false;
                float tSlew = 0f, tR = 0f, tH = 0f, topW = float.NaN;
                DigTruckBox box = default;
                if (truck != null && TruckGeometry(em, truck, tv, start + 0.05, root, rot, arm, out box, out tSlew, out tR, out tH, out topW, uw)) haveTruck = true;
                else if (truck != null) { why = "truck pose unavailable"; truck = null; }
                if (haveTruck && uw && math.abs(tSlew) > MxConst.kUwPlanSlewDeg)
                {
                    why = truck.Role + " outside the front dump (slew " + RRWLog.F(tSlew) + " deg)";
                    haveTruck = false;
                    truck = null;
                }
                DigKeys keys = null;
                if (haveTruck)
                {
                    keys = Reuse(prev, mode, true, floorH, gradeH, bite0, true, box, tH, float.NaN, float.NaN);
                    if (keys != null) d.Reuses++;
                    else
                    {
                        keys = DigKeys.Build(arm, mode, floorH, gradeH, bite, 1f, true, tSlew, tR, tH, box, d.LastTruckFit, farShare: farShare);
                        d.LastTruckFit = keys;
                        d.Builds++;
                        MxPerf.Count(MxC.DigCycleBuilds);
                    }
                    if (keys.TruckOutOfReach || !keys.DumpFeasible)
                        why = keys.TruckOutOfReach ? "truck out of reach (dump shifted)" : "dump over the truck infeasible";
                    else if (present < start + keys.Sched.TShake - 0.02)
                        why = truck.Role + " departs " + RRWLog.F((float)(present - start)) + " s into the cycle, before its shake ends (" + RRWLog.F(keys.Sched.TShake) + " s)";
                    else why = null;
                    if (why != null) { keys = null; haveTruck = false; truck = null; }
                }
                if (keys == null)
                {
                    // the spoil side: away from the crew's trucks near this cycle, the tip inside the carriageway / floor
                    float sSlew = float.NaN, sR = float.NaN;
                    bool sBox = false, outside = false;
                    DigTruckBox sb = default;
                    string swhy;
                    if (provisional) { sSlew = uw ? 0f : (h.x >= 0f ? -1f : 1f) * 100f; swhy = "provisional"; }
                    else SpoilTarget(em, p, d, tv, start, s, root, rot, h, mode, floorH, gradeH, bite, out sSlew, out sR, out sBox, out sb, out outside, out swhy, uw);
                    keys = provisional ? null : Reuse(prev, mode, false, floorH, gradeH, bite0, sBox, sb, gradeH + 1f, sSlew, sR);
                    if (keys != null) d.Reuses++;
                    else
                    {
                        keys = DigKeys.Build(arm, mode, floorH, gradeH, bite, sSlew, false, 0f, 0f, 0f, sb, d.LastSpoilFit, sSlew, sR, sBox, farShare);
                        if (!provisional) d.LastSpoilFit = keys;
                        d.Builds++;
                        MxPerf.Count(MxC.DigCycleBuilds);
                    }
                    keys.SpoilOutside = outside;
                    if (outside && !provisional) d.SpoilOutsideCycles++;
                    d.LastSpoil = swhy;
                    why = why + "; " + swhy;
                }
                keys.Provisional = provisional;
                if (provisional) d.Provisionals++;
                if (uw && !provisional) { if (haveTruck) MxUpgradeStats.FrontLoads++; else MxUpgradeStats.FrontDumpsSpoil++; }
                keys.CycleStart = start;
                keys.Truck = haveTruck ? truck : null;
                keys.BedTopWorldY = haveTruck ? topW : float.NaN;
                keys.TargetWhy = haveTruck ? "truck " + truck : "spoil (" + why + ")";
                d.LastFloorH = floorH;
                d.LastWhy = keys.TargetWhy;
                if (!d.HaveReady)
                {
                    var rr = arm.SolveArm(keys.Keys[0].Slew, keys.Keys[0].R, keys.Keys[0].H, keys.Keys[0].Att, float.NegativeInfinity);
                    d.ReadyPose = rr.Pose.F4;
                    d.HaveReady = true;
                }
                // the crew's truck departures follow the fitted schedule; provisional keys carry no fit
                if (!provisional && crew != null && crew.Roles[(int)MachineRole.Excavator] == p) crew.DigSched = keys.Sched;
                DigBuildsFrame++;
                return keys;
            }
            finally { s_DigBudgetTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        }

        private static CrewState CrewOf(Puppet p) =>
            MachineRegistry.States.TryGetValue(p.ProjectId, out var st) && p.Crew >= 0 && p.Crew < st.Crews.Length ? st.Crews[p.Crew] : null;

        // An exact repeat of prev (same mode, target kind, floor, grade, bite, box, bed height and - spoil - dump point): a copy.
        private static DigKeys Reuse(DigKeys prev, DigMode mode, bool haveTruck, float floorH, float gradeH, float bite0, bool hasBox, in DigTruckBox box,
                                     float bedH, float spoilSlew, float spoilR)
        {
            if (prev == null || prev.Provisional || prev.Mode != mode || prev.HaveTruck != haveTruck || prev.HasBox != hasBox || prev.TruckOutOfReach) return null;
            if (math.abs(prev.FloorH + prev.Bite - floorH) >= 0.05f || math.abs(prev.GradeH - gradeH) >= 0.05f || math.abs(prev.Bite - bite0) >= 1e-3f) return null;
            if (hasBox && (!prev.Box.SameAs(box, 0.08f) || math.abs(prev.BedH - prev.DumpRaised - bedH) >= 0.08f)) return null;
            if (!haveTruck && !float.IsNaN(spoilSlew) && math.abs(DigArm.Wrap180(prev.DumpSlew - spoilSlew)) >= 1f) return null;
            if (!haveTruck && !float.IsNaN(spoilR) && math.abs(prev.BedR - spoilR) >= 0.3f) return null;
            return Clone(prev);
        }

        private static readonly float[] s_SpoilSlews = { 100f, 85f, 70f, 55f, 40f, 25f, 10f, 0f };
        private static readonly float[] s_FrontSlews = { 0f };
        private const float kSpoilEdge = 0.6f;       // the dump tip stays this far inside the lateral bounds (half a bucket)
        private const float kSpoilTruckClear = 1.5f; // ... and this far from a near truck's box

        // The spoil dump point of a cycle without a truck target.
        //  * Side: away from every truck of the crew that comes within RFar + 1.5 m of the excavator during this cycle (arriving, waiting
        //    for the next cycle boundary, leaving early), else chain-left (the trucks load on the chain-right lane; D0 windrow chain-left).
        //  * Slew 100 deg down to 0 deg (straight ahead) and radius from 0.8 x RFar down to the chassis front + 1.5 m: the first whose tip
        //    lies inside the lateral bounds (TipBounds) by kSpoilEdge and >= kSpoilTruckClear from the near truck's box. The other side is
        //    tried only when no truck is near. Nothing fits: the smallest dump straight ahead, counted (rrw.mx.check spoil outside).
        //  * The nearest near truck's box goes into the fit (hasBox): dump pick, DumpClearance and the swing / return lags keep clear of it.
        private void SpoilTarget(EntityManager em, Puppet p, DigState d, in TrackView tv, double start, in ChainState s, float3 root, quaternion rot,
                                 float2 h, DigMode mode, float floorH, float gradeH, float bite,
                                 out float slew, out float r, out bool hasBox, out DigTruckBox box, out bool outside, out string why, bool front = false)
        {
            var arm = d.Arm;
            DigKeys.SpoilReach(arm, mode, floorH, gradeH, bite, out float rDef, out float rMin, out float rFar, front ? MxConst.kUwDragShare : 1f);
            float bedH = gradeH + 1f;
            hasBox = false;
            box = default;
            outside = false;
            quaternion inv = math.inverse(rot);
            float sumX = 0f, bestGap = float.MaxValue;
            int near = 0;
            var crew = CrewOf(p);
            if (crew != null)
            {
                var nom = DigSchedule.Nominal(mode);
                for (int ri = (int)MachineRole.TruckA; ri <= (int)MachineRole.TruckB; ri++)
                {
                    var t = crew.Roles[ri];
                    if (t == null || t.PostSite || !t.IsTruck || t.Plan.Count <= 0 || !t.Alive(em)) continue;
                    var g = GeoOf(em, t.Prefab);
                    float hx = 0.5f * (g.Max.x - g.Min.x), hz = 0.5f * (g.Max.z - g.Min.z);
                    bool counted = false;
                    for (int k = 0; k < 5; k++)
                    {
                        double tk = start + (k == 0 ? 0.05 : k == 1 ? nom.TLift : k == 2 ? nom.TSwing : k == 3 ? nom.TShake : nom.TReturn);
                        MachineMotion.State(t.Plan, MachineRegistry.Clock, tk, out var ts);
                        if (!MachineMotion.Pose(t.Plan, tv, ts, out float3 tp, out _)) continue;
                        float3 o = math.rotate(inv, tp - root);
                        float gap = math.max(math.abs(o.x) - hx, math.abs(o.z) - hz);   // rough box gap (the trucks run along the chain)
                        if (gap > rFar + 1.5f) continue;
                        sumX += o.x;
                        if (!counted) { near++; counted = true; }
                        if (gap < bestGap && TruckGeometry(em, t, tv, tk, root, rot, arm, out var b, out _, out _, out _, out _)) { bestGap = gap; box = b; hasBox = true; }
                    }
                }
            }
            int truckSide = near > 0 && math.abs(sumX) > 0.3f ? (sumX > 0f ? 1 : -1) : 0;
            int pref = truckSide != 0 ? -truckSide : (h.x >= 0f ? -1 : 1);   // object -x = chain-left when facing +u
            TipBounds(em, p, s.U, start, out float lo, out float hi, out string bw);
            // mode H: the front dump only (straight ahead, inside the band; the boom never swings over the open lanes)
            var slews = front ? s_FrontSlews : s_SpoilSlews;
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1 && (near > 0 || front)) break;   // never towards a near truck
                int sg = pass == 0 ? pref : -pref;
                for (int ia = 0; ia < slews.Length; ia++)
                {
                    float th = slews[ia];
                    for (int ir = 0; ir < 12; ir++)
                    {
                        float rr = math.max(rMin, rDef - 0.5f * ir);
                        float3 o = arm.ArmToObj(sg * th, rr, bedH);
                        float tl = s.Lat + h.y * o.z + h.x * o.x;
                        bool ok = tl >= lo + kSpoilEdge && tl <= hi - kSpoilEdge && (!hasBox || box.Sdf(o) >= kSpoilTruckClear);
                        if (ok)
                        {
                            slew = sg * th;
                            r = rr;
                            why = "spoil slew " + RRWLog.F(slew) + " r " + RRWLog.F(r) + " tipLat " + RRWLog.F(tl) + " in [" + RRWLog.F(lo) + "," + RRWLog.F(hi) + "] (" + bw + ")" +
                                  (near > 0 ? " away from " + near + " near truck(s)" : " chain-left default") + (hasBox ? " +truck box" : "");
                            return;
                        }
                        if (rr <= rMin) break;
                    }
                }
            }
            slew = 0f;
            r = rMin;
            outside = true;
            why = "spoil OUTSIDE the bounds [" + RRWLog.F(lo) + "," + RRWLog.F(hi) + "] (" + bw + "): straight ahead r " + RRWLog.F(r) + (near > 0 ? ", " + near + " near truck(s)" : "");
        }

        // Lateral bounds (chain lateral) of a dump tip at u: a visible road / kept half / fence -> Choreo.LatRange widened by the machine's
        // half width (the carriageway minus its clearances, the works half); a hidden road -> the works footprint (+-HalfWidth); else the
        // carriageway (D0: never over the sidewalks, fences, street lights or trees).
        private void TipBounds(EntityManager em, Puppet p, float u, double now, out float lo, out float hi, out string src)
        {
            lo = -3f; hi = 3f; src = "default +-3 m";
            if (!SiteRegistry.TryGetProject(p.ProjectId, out var pr) || !MachineTrackStore.TryGet(pr.Id, out var td) || td == null || !td.Valid) return;
            var c = GuardContext(em, p, now);
            if (c != null)
            {
                Choreo.LatRange(c, p, u, out float a, out float b);
                if (!float.IsInfinity(a) || !float.IsInfinity(b))
                {
                    var smp0 = TrackBuilder.At(td, u);
                    float hw0 = smp0.HalfWidth > 0f ? smp0.HalfWidth : 6f;
                    lo = float.IsInfinity(a) ? -hw0 : a - p.BoxHalfWid;
                    hi = float.IsInfinity(b) ? hw0 : b + p.BoxHalfWid;
                    src = "lane policy";
                    return;
                }
            }
            var smp = TrackBuilder.At(td, u);
            if (PhasePlan.IsHidden(pr.Kind, pr.Mode, pr.Phase))
            {
                float hw = smp.HalfWidth > 0f ? smp.HalfWidth : 6f;
                lo = -hw; hi = hw; src = "works footprint";
                return;
            }
            if (TrackBuilder.CarriageAt(td, u, out float clo, out float chi)) { lo = clo; hi = chi; src = "carriageway"; return; }
            float fl = smp.FlatHalf > 0f ? smp.FlatHalf : 4f;
            lo = -fl; hi = fl; src = "floor";
        }

        static DigKeys Clone(DigKeys a) => a.Copy();

        // A loading truck of p's crew that, at the cycle start, holds at its LoadAtFront stop on the same dig grid (its DigHop leg with
        // the Load anim, then the Load hold that may follow it: the truck stops hopping at the last cycle's hop start and stands until it
        // departs) until at least start + tShake. Null = none (arriving, leaving, other grid). presentUntil: machine time it leaves.
        internal static Puppet TruckTarget(Puppet p, double start, float tShake, out string why) => TruckTarget(p, start, tShake, out why, out _);

        internal static Puppet TruckTarget(Puppet p, double start, float tShake, out string why, out double presentUntil)
        {
            why = "no loading truck at its stop";
            presentUntil = double.NegativeInfinity;
            if (!MachineRegistry.States.TryGetValue(p.ProjectId, out var st) || p.Crew < 0 || p.Crew >= st.Crews.Length) { why = "no crew"; return null; }
            var cs = st.Crews[p.Crew];
            Puppet best = null;
            float bestD = float.MaxValue;
            for (int r = (int)MachineRole.TruckA; r <= (int)MachineRole.TruckB; r++)
            {
                var t = cs.Roles[r];
                if (t == null || t.PostSite || t.Outgoing || t.Plan.Count <= 0 || !t.IsTruck) continue;
                double ta = start + 0.02, tb = start + tShake - 0.02;   // (a last-cycle hold starts exactly at the shake end)
                int ia = MachineMotion.ActiveLeg(t.Plan, ta - t.Plan.Epoch);
                var l = t.Plan.Get(ia);
                if (l.Kind != (byte)LegKind.DigHop || l.Anim != (byte)AnimKind.Load) { why = t.Role + " not at its loading stop (arriving / leaving)"; continue; }
                if (math.abs(l.P0 - RRWConst.kDigCyclePeriod) > 0.01f) { why = t.Role + " on another dig grid"; continue; }
                // the same grid PHASE too (a truck still on a re-phased grid hops at other times than the excavator)
                double ph = (start - (t.Plan.Epoch + l.T0)) / l.P0;
                if (math.abs(ph - math.round(ph)) * l.P0 > 0.05) { why = t.Role + " on another dig grid phase (re-planning)"; continue; }
                double until = l.OpenEnded ? double.PositiveInfinity : t.Plan.Epoch + l.T1;
                for (int j = ia + 1; j < t.Plan.Count && !double.IsPositiveInfinity(until) && until < tb; j++)
                {
                    var lj = t.Plan.Get(j);
                    if (lj.Kind != (byte)LegKind.Hold || lj.Anim != (byte)AnimKind.Load) break;
                    until = lj.OpenEnded ? double.PositiveInfinity : t.Plan.Epoch + lj.T1;
                }
                if (until < tb - 1e-3) { why = t.Role + " departs before the shake ends"; continue; }
                MachineMotion.State(t.Plan, MachineRegistry.Clock, ta, out var s);
                MachineMotion.State(p.Plan, MachineRegistry.Clock, ta, out var e);
                float dd = math.abs(s.U - e.U) + math.abs(s.Lat - e.Lat);
                if (dd < bestD) { bestD = dd; best = t; presentUntil = until; }
            }
            return best;
        }

        // The truck at machine time tAt as a box in the excavator's object space + its bed point in arm space (slew, r, top + 0.45 m).
        private bool TruckGeometry(EntityManager em, Puppet t, in TrackView tv, double tAt, float3 root, quaternion rot, DigArm arm,
                                   out DigTruckBox box, out float slew, out float r, out float h, out float topW, bool tail = false)
        {
            box = default; slew = r = h = 0f; topW = float.NaN;
            MachineMotion.State(t.Plan, MachineRegistry.Clock, tAt, out var ts);
            if (!MachineMotion.Pose(t.Plan, tv, ts, out float3 tp, out quaternion trot)) return false;
            var g = GeoOf(em, t.Prefab);
            quaternion inv = math.inverse(rot);
            float3 cl = 0.5f * (g.Min + g.Max);
            quaternion toObj = math.mul(inv, trot);
            float3 ax = math.rotate(toObj, new float3(1f, 0f, 0f)), az = math.rotate(toObj, new float3(0f, 0f, 1f));
            ax.y = 0f; az.y = 0f;
            float baseY = math.rotate(inv, tp - root).y;
            box = new DigTruckBox
            {
                C = math.rotate(inv, tp + math.rotate(trot, new float3(cl.x, 0f, cl.z)) - root),
                Ax = math.normalizesafe(ax, new float3(1f, 0f, 0f)), Az = math.normalizesafe(az, new float3(0f, 0f, 1f)),
                Hx = 0.5f * (g.Max.x - g.Min.x), Hz = 0.5f * (g.Max.z - g.Min.z), Y0 = baseY + g.Min.y, Y1 = baseY + g.Max.y,
            };
            var bed = g.Bed;
            if (tail) bed.z = g.Min.z + MxConst.kUwTailgateInset;   // front loading: the bucket empties just inside the tailgate
            float3 bedW = tp + math.rotate(trot, bed);
            topW = tp.y + g.Top;
            arm.ObjToArm(math.rotate(inv, bedW - root), out slew, out r, out _);
            // the bed top in the arm's h axis (object space), not a world-Y difference (the rig rolls / pitches)
            h = DigFloor.ObjH(new float3(bedW.x, topW, bedW.z), root, rot, arm.S) + 0.45f;
            return true;
        }

        // CoalTruck01 bounds (the clearance box) and its bed point: the prefab's load-pile sub-object, else the rear third (verified).
        private TruckGeo GeoOf(EntityManager em, Entity prefab)
        {
            if (s_TruckGeo.TryGetValue(prefab, out var g)) return g;
            var b = em.HasComponent<ObjectGeometryData>(prefab) ? em.GetComponentData<ObjectGeometryData>(prefab).m_Bounds : default;
            bool ok = b.max.y > 0.5f && b.max.x - b.min.x > 0.5f && b.max.z - b.min.z > 1f;
            g.Min = ok ? b.min : new float3(-1.475f, 0f, -4.52f);   // CoalTruck01 2.95 x 3.6 x 9.04 fallback
            g.Max = ok ? b.max : new float3(1.475f, 3.6f, 4.52f);
            g.Top = g.Max.y;
            g.Bed = new float3(0f, g.Top * 0.7f, math.lerp(g.Min.z, g.Max.z, 0.3f));
            try
            {
                if (em.HasBuffer<Game.Prefabs.SubObject>(prefab))
                {
                    var subs = em.GetBuffer<Game.Prefabs.SubObject>(prefab, true);
                    for (int i = 0; i < subs.Length; i++)
                    {
                        string n = EcsUtil.PrefabName(m_PrefabSystem, em, subs[i].m_Prefab);
                        if (n != null && n.IndexOf("Pile", StringComparison.OrdinalIgnoreCase) >= 0) { g.Bed = subs[i].m_Position; break; }
                    }
                }
            }
            catch (Exception e) { RRWLog.ErrorOnce("machines truck bed", e); }
            s_TruckGeo[prefab] = g;
            return g;
        }

        // Tip (world) of the solved pose at machine time t of the cycle starting at cs.
        private static bool TipWorld(Puppet p, DigState d, DigKeys keys, in TrackView tv, double t, double cs, out float3 tip, out float3 root)
        {
            tip = root = default;
            if (keys == null || d.Arm == null) return false;
            MachineMotion.State(p.Plan, MachineRegistry.Clock, t, out var s);
            if (!MachineMotion.Pose(p.Plan, tv, s, out root, out quaternion rot)) return false;
            var tg = keys.Eval((float)(t - cs));
            var res = d.Arm.SolveArm(tg.Slew, tg.R, tg.H, tg.Att, keys.FloorH);
            tip = root + math.rotate(rot, res.TipObj);
            return true;
        }

        private void OnBreakout(EntityManager em, Puppet p, DigState d, DigKeys keys, in TrackView tv, double t, double cs, RRWSetting settings)
        {
            d.Breakouts++;
            MxPerf.Count(MxC.DigEvents);
            if (!TipWorld(p, d, keys, tv, t, cs, out float3 tip, out _)) return;
            TryPuff(em, p, d, tip, t, settings, "breakout");
        }

        private void OnDump(EntityManager em, Puppet p, DigState d, DigKeys keys, in TrackView tv, double t, double cs, RRWSetting settings)
        {
            d.Dumps++;
            MxPerf.Count(MxC.DigEvents);
            Puppet truck = keys != null ? keys.Truck : TruckTarget(p, cs, DigSchedule.Nominal(DigMode.Dig).TShake, out _);
            if (truck != null && MachineRegistry.All.Contains(truck) && !truck.PostSite && truck.Alive(em))
            {
                truck.Buckets++;
                int pct = (int)math.round(DigLoad.Share(truck.Buckets) * 100f);
                if (pct != truck.AmountPct) PuppetFactory.SetAmount(em, truck, pct);
                d.TruckDumps++;
                if (RRWLog.VerboseEnabled) RRWLog.Verbose("machines: " + p + " dumps bucket " + truck.Buckets + " into " + truck + " -> " + pct + "% (" + DigLoad.Amount(truck.Buckets) + ")");
            }
            else d.SpoilDumps++;
            if (!TipWorld(p, d, keys, tv, t, cs, out float3 tip, out float3 root)) return;
            float y = keys != null && keys.Truck != null && !float.IsNaN(keys.BedTopWorldY) ? keys.BedTopWorldY + 0.1f : root.y + (keys != null ? keys.GradeH : 0f);
            float3 at = new float3(tip.x, math.clamp(y, tip.y - 0.3f, tip.y), tip.z);
            TryPuff(em, p, d, at, t, settings, "dump");
        }

        // ------------------------------------------------------------ dust puffs (recipe verified in the dust prototype)

        private void TryPuff(EntityManager em, Puppet p, DigState d, float3 pos, double t, RRWSetting settings, string what)
        {
            if (!settings.DigDustOn || !RRWPrefabRegistry.DustPuffOk) return;
            string skip = null;
            if (p.CulledUpdates > 0) skip = "excavator culled";
            else if (p.CamDist > RRWConst.kDustPuffMaxDistance) skip = "farther than " + RRWConst.kDustPuffMaxDistance + " m";
            else if (t - d.LastPuffT < RRWConst.kDustPuffMinInterval) skip = "interval";
            else if (s_Puffs.Count >= RRWConst.kDustPuffMaxLive) skip = "global budget";
            else
            {
                int n = 0;
                for (int i = 0; i < s_Puffs.Count; i++) if (s_Puffs[i].ProjectId == p.ProjectId) n++;
                if (n >= RRWConst.kDustPuffMaxPerProject) skip = "project budget";
            }
            if (skip != null) { PuffsSkipped++; LastPuffSkip = what + ": " + skip; MxPerf.Count(MxC.DustPuffsSkipped); return; }
            Entity pe = PrefabCatalog.Static(m_PrefabSystem, PrefabNames.DustPuff);
            if (pe == Entity.Null || !em.HasComponent<ObjectData>(pe)) return;
            var od = em.GetComponentData<ObjectData>(pe);
            if (!od.m_Archetype.Valid) { RRWLog.Once("machines-puff-arch", "machines: dust puff archetype not valid yet"); return; }
            uint dur = 240u;
            if (em.HasBuffer<EffectAnimation>(pe))
            {
                var anims = em.GetBuffer<EffectAnimation>(pe, true);
                if (anims.Length > 0) dur = math.max(1u, anims[0].m_DurationFrames);
            }
            ushort seed = PhaseSeed(RRWClock.RenderFrame + 1u, dur);
            var arr = em.CreateEntity(od.m_Archetype, 1, Allocator.Temp);
            Entity e = arr[0];
            arr.Dispose();
            em.SetComponentData(e, new PrefabRef { m_Prefab = pe });
            em.SetComponentData(e, new ObjTransform(pos, quaternion.identity));
            if (em.HasComponent<PseudoRandomSeed>(e)) em.SetComponentData(e, new PseudoRandomSeed(seed));
            if (!em.HasComponent<Created>(e)) em.AddComponent<Created>(e);
            if (!em.HasComponent<Updated>(e)) em.AddComponent<Updated>(e);
            if (!em.HasComponent<Game.Tools.Hidden>(e)) em.AddComponent<Game.Tools.Hidden>(e);   // heap mesh hidden; the VFX renders (verified in game)
            if (em.HasComponent<Elevation>(e)) em.SetComponentData(e, new Elevation(0f, (ElevationFlags)0));
            else em.AddComponentData(e, new Elevation(0f, (ElevationFlags)0));
            Entity site = Entity.Null;
            if (MachineTrackStore.TryGet(p.ProjectId, out var td)) site = TrackBuilder.EdgeAt(td, float.IsNaN(p.NowU) ? 0f : p.NowU);
            EcsUtil.TagDerived(em, e, site, p.ProjectId, DerivedGroup.DustPuff);   // LivePath + RRWDerived (no Owner)
            s_Puffs.Add(new Puff { E = e, Die = t + RRWConst.kDustPuffLifeSeconds, ProjectId = p.ProjectId });
            d.LastPuffT = t;
            PuffsSpawned++;
            MxPerf.Count(MxC.DustPuffs);
        }

        // Deleted after kDustPuffLifeSeconds machine s (deleting ends the particles, so never earlier). Paused: nothing changes.
        private static void ExpirePuffs(EntityManager em, double now)
        {
            for (int i = s_Puffs.Count - 1; i >= 0; i--)
            {
                var pf = s_Puffs[i];
                bool gone = !em.Exists(pf.E) || em.HasComponent<Deleted>(pf.E);
                if (!gone && now < pf.Die) continue;
                if (!gone) { em.AddComponent<Deleted>(pf.E); PuffsDeleted++; }
                s_Puffs.RemoveAt(i);
            }
        }

        // Live puffs older than their life + 1 s (rrw.mx.check: must be 0).
        internal static int PuffsOverAge(double now)
        {
            int n = 0;
            for (int i = 0; i < s_Puffs.Count; i++) if (now > s_Puffs[i].Die + 1.0) n++;
            return n;
        }

        // Seed whose kLightState random puts the effect animation phase at 0 on frame f0 (EffectTransformSystem: phase =
        // (frame + random.NextUInt(dur)) % dur, as in the game code). The table is built once per duration (one pass over the
        // 65535 seeds), then every puff is O(1).
        private static ushort PhaseSeed(uint f0, uint dur)
        {
            if (s_PhaseSeeds == null || s_PhaseDur != dur)
            {
                s_PhaseDur = dur;
                s_PhaseSeeds = new ushort[dur];
                int filled = 0;
                for (int s = 1; s < 65536 && filled < dur; s++)
                {
                    var rnd = new PseudoRandomSeed((ushort)s).GetRandom(PseudoRandomSeed.kLightState);
                    uint r = rnd.NextUInt(dur);
                    if (s_PhaseSeeds[r] == 0) { s_PhaseSeeds[r] = (ushort)s; filled++; }
                }
            }
            uint want = (dur - (f0 % dur)) % dur;
            for (uint dd = 0; dd < dur; dd++)
            {
                ushort a = s_PhaseSeeds[(want + dd) % dur];
                if (a != 0) return a;
            }
            return 1;
        }
    }

    // Floor heights in a pitched / rolled machine's object space (excavator bite, grade, D0 surface, grader bucket).
    internal static class DigFloor
    {
        // World point of the chain sample tp at lateral lat with height y.
        public static float3 AtLat(in TrackPoint tp, float lat, float y)
        {
            float2 xz = tp.Pos.xz + tp.Right * lat;
            return new float3(xz.x, y, xz.y);
        }

        // World point of the track floor (TrackView.Height) at chain (u, lat).
        public static bool World(in TrackView tv, int slot, float u, float lat, out float3 w)
        {
            w = default;
            if (!tv.Point(slot, u, out var tp) || !tv.Height(slot, u, lat, out float y)) return false;
            w = AtLat(tp, lat, y);
            return true;
        }

        // Height of a world point in the arm's h axis: the object-space point (inverse root rotation) along the slew axis `up`
        // (DigArm.S: ArmToObj puts h along it).
        public static float ObjH(float3 world, float3 root, quaternion rot, float3 up) => math.dot(math.rotate(math.inverse(rot), world - root), up);
    }
}
