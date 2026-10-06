using System;
using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using NetSearchSystem = Game.Net.SearchSystem;
using ObjSearchSystem = Game.Objects.SearchSystem;

// Choreography: turns a role's CrewSlot into a leg plan, starting from the puppet's current motion state so a
// re-plan never moves a running machine ("finish the current leg"). Plans are pure data; MachineMotion
// evaluates them. Main thread only (Modification1).
namespace RealisticRoadWorks.V3.Machines
{
    // Everything a planner needs about one project this frame.
    public sealed class PlanContext
    {
        public EntityManager Em;
        public ProjectRecord Project;
        public ProjectView View;
        public CrewPlan Crew;
        public MachineProjectState State;
        public TrackData Track;
        public TrackView Tv;
        public ClockData Clk;
        public double Now;
        public FrontRef Front;
        public bool Working;
        public bool Narrow;
        public float FlatHalfMin = 6f;
        public float Trim0, Trim1;
        public bool CanTurn;
        public readonly List<Puppet> Others = new List<Puppet>(16);
        public ObjSearchSystem ObjSearch;
        public NetSearchSystem NetSearch;
        public bool Verbose;
        // ---- lane policy (visible-road lanes, open lanes), set by MachineDirectorSystem.Context
        public bool Visible;          // visible road (construction C3/C4/release, or any open group): boxes stay inside the
                                      // carriageway (sidewalks free), no verge slots, lateral fractions map onto the carriageway
        public bool KeepLeft, KeepRight; // keep the chain-left / chain-right half free (open, or about to open in C4)
        public RoadZones Open;        // ProjectRecord.OpenLanes this frame (the Director wrote it earlier in the frame)
        public bool Releasing;        // the project hands the road back (completion / D0 call-off): leave, never spawn
        public float CarriageHalfMin; // smallest centre-line -> carriageway-edge distance over the chain
        // ---- lane policy per car half / group, set by MachineDirectorSystem.SetLanePolicy
        public RoadZones Ready;       // groups machines may use: ProjectRecord.WorkZonesReady (AllLanes & ~Open with dev rrw.mx.ready legacy)
        public RoadZones Keep;        // groups every box keeps out of: not ready, open / wanted open, or draining (stage.Open | stage.Soft)
        public bool ReadyOk;          // at least one car half is ready: new role plans / spawns are allowed (else committed plans run on)
        public bool NoSpawn;          // stage switch running (Vacate / Swap / Drain) or nothing ready: no new puppet
        public string NoSpawnWhy = "";
        public bool KeepParkL, KeepParkR; // the parking strip on that chain side is not ready: boxes stay on the drive lanes
        public bool FenceL, FenceR;   // a kerb fence stands (or will stand: the sidewalk is open / wanted open) on that chain side
        public bool FenceInset;       // RRWGates.FenceLateral == Inset: the fence stands kKerbFenceInset INSIDE the carriageway
        public StageInfo Stage;       // PhasePlan.Stage(View) this frame
        public bool HalvesActive;     // C4 per-half layout (View.HalvesActive): RMV01-only roster
        public float3 Cam = new float3(float.NaN); // camera pivot (leavers prefer the connected end farther from the camera)
        // ---- the crew this context plans (MachineDirectorSystem.SetCrew)
        public int CrewIndex, Crews = 1;
        public CrewState CS;          // roster + dig grid of the crew
        public float SecLo, SecHi;    // the crew's working range (CrewPlan.SecLo / SecHi)
        public float FrontSpeed;      // crew front speed now (m per machine s, MachineMotion.FrontLin slope)
        // ---- road rollers and IK digging
        public Game.Prefabs.PrefabSystem Ps; // rig reads (DigArms) for the IK schedule decision
        public bool DetailedDigging;  // RRWSetting.DetailedDiggingOn this update
        public bool RollersOn;        // RRWSetting.RollersOn this update (live gate of every roller spawn)

        public float FrontAt(double tau) => MachineMotion.Front(Front, Clk, tau);
    }

    // Running builder: the new plan + the motion state at its end.
    public struct LegBuilder
    {
        public MachinePlan Plan;
        public float T;           // end time of the last leg (relative to Plan.Epoch)
        public float U, Lat;
        public sbyte Facing;
        public float Odo;
        public bool Overflow;
        public byte LastKind;
        public byte LastAnim;
        public float MergeSlope;  // average |dlat/ds| of lane changes (Begin: gentler on a visible road)
        // ---- realistic front-relative motion
        public float V;           // du/dt at T (a moving front-relative leg cut by the re-plan); rest-start legs brake it first (Settle)
        public MachineLimits Lim; // the puppet's speed table row (Begin)
        public bool Leaving;      // leave plan: forward drives at VLeave

        public double Abs(float t) => Plan.Epoch + t;
        public float Rel(double tau) => (float)(tau - Plan.Epoch);

        public bool Add(MachineLeg l)
        {
            if (Plan.Count >= MachinePlan.MaxLegs) { Overflow = true; return false; }
            l.Odo0 = Odo;
            if (Plan.Count > 0 && l.Anim != LastAnim && l.Blend <= 0f)
            {
                // automatic bone blend between anims (dig -> rest 3 s; everything else 1.5 s)
                l.FromAnim = LastAnim;
                l.Blend = (LastAnim == (byte)AnimKind.Dig || LastAnim == (byte)AnimKind.Break) ? MxConst.kRestBlendSeconds : MxConst.kDigBlendIn;
            }
            Plan.Set(Plan.Count, l);
            Plan.Count++;
            LastKind = l.Kind;
            LastAnim = l.Anim;
            return true;
        }
    }

    // Configuration of a truck cycle in the current phase.
    public struct TruckCfg
    {
        public int Mode;           // 1 = Load at the excavator, 2 = Tip at the dump slot, 3 = Feed the paver (C3)
        public sbyte Dir;          // direction of the front seen from the truck base (+1: base on the start side)
        public float BaseU, BaseLat;
        public float WorkLat;
        public float TurnStation;  // chain u of the base turn station
        public LoadKind Load;
        public float Wait;         // base wait ("trip")
        public float LoadOffset;   // Load: truck centre relative to the excavator anchor
        public bool LaneSwapped;   // TruckB waits in the right lane (its left-lane base lay in the excavator's path)
    }

    public static partial class Choreo
    {
        // ------------------------------------------------------------ geometry helpers

        public static float LatOf(PlanContext c, float v, float u, Puppet p)
        {
            var smp = TrackBuilder.At(c.Track, u);
            float flat = smp.FlatHalf > 0f ? smp.FlatHalf : 4f;
            float hw = smp.HalfWidth > 0f ? smp.HalfWidth : 6f;
            if (c.Visible)
            {
                // On a visible road the fractions map onto the CARRIAGEWAY (|v| = 0.5 = its edge; verge
                // requests become the carriageway edge) and ClampLat keeps the box inside it / inside the works half
                TrackBuilder.CarriageAt(c.Track, u, out float lo, out float hi);
                float vv = math.clamp(v, -0.5f, 0.5f);
                if (c.KeepLeft != c.KeepRight)
                {
                    // One car half is kept free: the fraction maps onto the WORKS half relative to the direction
                    // split (Core mirrors the laterals in C4b; |v| = 0.5 = the carriageway edge, 0 = the split)
                    float sp = SplitAt(c, u);
                    if (float.IsNaN(sp)) sp = 0f;
                    float w = c.KeepLeft ? hi - sp : sp - lo;
                    if (w > 0.5f) return ClampLat(c, p, u, c.KeepLeft ? sp + 2f * math.abs(vv) * w : sp - 2f * math.abs(vv) * w);
                }
                return ClampLat(c, p, u, 0.5f * (lo + hi) + vv * (hi - lo));
            }
            if (math.abs(v) <= 0.51f)
            {
                float lim = math.max(0f, flat - p.BoxHalfWid - 0.3f);
                return ClampLat(c, p, u, math.clamp(v * 2f * flat, -lim, lim));
            }
            return math.sign(v) * (hw + RRWConst.kVergeOffset);
        }

        // Direction split (chain frame) of the edge under u: EdgeSection.DirSplit via RoadZoneMath.DirSplitChain; NaN = one
        // travel direction or no section (callers fall back to the centre line).
        public static float SplitAt(PlanContext c, float u)
        {
            if (!TrackBuilder.SectionAt(c.Track, u, out var sec, out bool rev)) return float.NaN;
            return RoadZoneMath.DirSplitChain(sec, rev);
        }

        // Drive-lane edges (chain frame) under u (parking strips lie outside them); the carriageway when not measured.
        static void DriveAt(PlanContext c, float u, float lo, float hi, out float dlo, out float dhi)
        {
            dlo = lo; dhi = hi;
            if (!TrackBuilder.SectionAt(c.Track, u, out var sec, out bool rev)) return;
            RoadZoneMath.DriveChain(sec, rev, out float a, out float b);
            if (b - a > 0.5f) { dlo = math.max(lo, a); dhi = math.min(hi, b); }
        }

        // Lane rules on a visible road, plus the per-half clamp: the lateral range a box centre may use at u.
        //  * visible road (or any kept group): inside the carriageway, >= kCarriageClearance off its edges;
        //  * a kept car half (not in WorkZonesReady, open, wanted open or draining): the box stays in the other half,
        //    lMin >= DirSplitChain + kZoneCentreTolerance (+ kHalfClearance) for the chain-left half, mirrored for the right;
        //  * a parking strip that is not ready: the box stays on the drive lanes on that side;
        //  * a standing / coming kerb fence: >= kKerbFenceInset + kFenceMachineClearance off that carriageway edge when the
        //    fence is inset (RRWGates.FenceLateral == Inset), the kKerbFenceClearance preference otherwise.
        // Tiers: the fence clearance is dropped before the split tolerance (never into a kept half). False when the box does not
        // fit at all: then the range is the carriageway as far from the kept half as possible (the machine report keeps the
        // half busy: it can never open while the machine is there).
        public static bool LatRange(PlanContext c, Puppet p, float u, out float a, out float b)
        {
            a = float.NegativeInfinity; b = float.PositiveInfinity;
            if (!c.Visible && !c.KeepLeft && !c.KeepRight && !c.KeepParkL && !c.KeepParkR) return true;
            TrackBuilder.CarriageAt(c.Track, u, out float lo, out float hi);
            float hwb = p.BoxHalfWid;
            float eL = lo, eR = hi;
            if (c.KeepParkL || c.KeepParkR)
            {
                DriveAt(c, u, lo, hi, out float dlo, out float dhi);
                if (c.KeepParkL && dlo > lo + 0.3f) eL = dlo;
                if (c.KeepParkR && dhi < hi - 0.3f) eR = dhi;
            }
            float sp = c.KeepLeft || c.KeepRight ? SplitAt(c, u) : 0f;
            if (float.IsNaN(sp)) sp = 0f;   // one direction: the centre line; no half can open there anyway
            float tol = RRWConst.kZoneCentreTolerance + MxConst.kHalfClearance + hwb;
            float ha = c.KeepLeft ? sp + tol : float.NegativeInfinity, hb = c.KeepRight ? sp - tol : float.PositiveInfinity;
            float fence = c.FenceInset ? RRWConst.kKerbFenceInset + RRWConst.kFenceMachineClearance : MxConst.kKerbFenceClearance;
            float ca, cb;
            if (c.FenceL || c.FenceR)
            {
                ca = eL + hwb + (c.FenceL ? fence : MxConst.kCarriageClearance);
                cb = eR - hwb - (c.FenceR ? fence : MxConst.kCarriageClearance);
                a = math.max(ca, ha); b = math.min(cb, hb);
                if (a <= b) return true;
                if (c.FenceInset && s_FenceFitLogged.Add(((long)(c.Project != null ? c.Project.Id : 0u) << 8) | (long)p.Role))
                    RRWLog.Info("machines: project #" + (c.Project != null ? c.Project.Id : 0u) + " " + p.Role + " (" + RRWLog.F(2f * hwb) +
                                " m) cannot keep " + RRWLog.F(fence) + " m from the inset kerb fence at u=" + RRWLog.F(u) +
                                ": kerb clearance " + RRWLog.F(MxConst.kCarriageClearance) + " m only (rrw.gate fencelat sidewalk if the fence is clipped)");
            }
            ca = eL + hwb + MxConst.kCarriageClearance; cb = eR - hwb - MxConst.kCarriageClearance;
            a = math.max(ca, ha); b = math.min(cb, hb);
            if (a <= b) return true;
            // does not fit: the carriageway (sidewalks first - pedestrian priority) as far from the kept half as possible;
            // the machine report then keeps that half busy (it can never open while the machine is there)
            if (ca <= cb)
            {
                if (c.KeepLeft && !c.KeepRight) { a = b = cb; }
                else if (c.KeepRight && !c.KeepLeft) { a = b = ca; }
                else { a = ca; b = cb; }
            }
            else a = b = 0.5f * (lo + hi);
            return false;
        }

        private static readonly HashSet<long> s_FenceFitLogged = new HashSet<long>();   // perf: log once without a key string
        private static readonly HashSet<long> s_StandStillLogged = new HashSet<long>();

        public static float ClampLat(PlanContext c, Puppet p, float u, float lat)
        {
            LatRange(c, p, u, out float a, out float b);
            return math.clamp(lat, a, b);
        }

        public static float AnchorLo(PlanContext c, Puppet p) => c.Trim0 + 1.5f + RRWConst.kBarrierClearance + EndDeviceExtra(c) + p.BoxHalfLen;
        public static float AnchorHi(PlanContext c, Puppet p) => c.Trim1 - 1.5f - RRWConst.kBarrierClearance - EndDeviceExtra(c) - p.BoxHalfLen;

        // Front-relative anchors of a crew stay in its section (+- kSecSlack); one crew: AnchorLo / AnchorHi.
        public static float CrewLo(PlanContext c, Puppet p) => c.Crews > 1 ? math.max(AnchorLo(c, p), c.SecLo - MxConst.kSecSlack) : AnchorLo(c, p);
        public static float CrewHi(PlanContext c, Puppet p)
        {
            float hi = AnchorHi(c, p);
            if (c.Crews > 1) hi = math.min(hi, c.SecHi + MxConst.kSecSlack);
            return math.max(hi, CrewLo(c, p));
        }

        // Crews meeting at section boundaries: the upper bound of a SUPPORT role (C3 feed truck / waiter)
        // of a crew that is not the last one: its box stays out of [SecHi - 2, ...], the hand-off zone where the next crew's crew
        // truck comes home and this crew's paver finishes its section. The last crew (and one crew) keeps CrewHi.
        public static float SupportHi(PlanContext c, Puppet p)
        {
            float hi = CrewHi(c, p);
            if (c.Crews > 1 && c.CrewIndex < c.Crews - 1) hi = math.max(CrewLo(c, p), math.min(hi, c.SecHi - 2f - p.BoxHalfLen));
            return hi;
        }

        // Props integration: in the C4 per-half layout the closed-end devices stand inside the barrier line
        // (one-way / no-entry sign and amber head at line + kSignInsetBehindLine, speed plate + 1 m behind it, the 45-degree cone
        // pair +-0.55 m). Boxes keep kEndDeviceClearance (3.5 m) from the line: line inset (<= 1 m) + 3.5 = 4.5 m from the trim.
        public const float kEndDeviceClearance = 3.5f;
        static float EndDeviceExtra(PlanContext c) =>
            c.HalvesActive ? math.max(0f, 1f + kEndDeviceClearance - 1.5f - RRWConst.kBarrierClearance) : 0f;

        public static float ClampU(PlanContext c, Puppet p, float u)
        {
            float lo = AnchorLo(c, p), hi = AnchorHi(c, p);
            if (lo > hi) return 0.5f * (c.Trim0 + c.Trim1);
            return math.clamp(u, lo, hi);
        }

        // Slots near a chain end shared with another project move inward by 25 m (cross-project planning).
        public static float ShiftShared(PlanContext c, float u)
        {
            if (c.Project.StartShared && u < c.Trim0 + RRWConst.kSharedNodeSlotShift) u += RRWConst.kSharedNodeSlotShift;
            if (c.Project.EndShared && u > c.Trim1 - RRWConst.kSharedNodeSlotShift) u -= RRWConst.kSharedNodeSlotShift;
            return u;
        }

        static float TurnLo(PlanContext c) => c.Trim0 + 9f;
        static float TurnHi(PlanContext c) => c.Trim1 - 9f;
        // A straight run-up before a lane change keeps the box on the chain
        static float RunLo(PlanContext c, Puppet p) => c.Trim0 + p.BoxHalfLen + math.abs(p.BoxOffZ) + 0.5f;
        static float RunHi(PlanContext c, Puppet p) => c.Trim1 - p.BoxHalfLen - math.abs(p.BoxOffZ) - 0.5f;

        // ------------------------------------------------------------ begin (finish the current leg)

        // Starts a new plan at c.Now. The leg running now is kept up to its natural end (a dig cycle boundary, the end of a
        // drive or turn, a shuttle stop), together with the legs that ran in the last 0.6 s (x clock scale, max 2), so the
        // TransformFrame history (up to ~31 frames in the past) evaluates exactly as before. Copied front-relative legs keep
        // their front model (FrontB).
        // The puppet's speed table row. A role plan uses the phase it is planned for; a leaver keeps the row of the
        // phase it last worked in (the C3 paver drives off as a paver, not as the C4 painter).
        public static void RefreshLimits(Puppet p, PlanContext c)
        {
            bool leaving = p.PostSite || p.Outgoing;
            var ph = p.LimPhase;
            if (!leaving && c != null && c.Project != null && c.View.Phase != WorksPhase.None) ph = c.View.Phase;
            p.LimPhase = ph;
            p.Lim = MachineLimits.Of(p.Role, ph);
            // A leaving RMV01 (the C3 paver proxy, the C4 painter) drives off as the van it is - the crew truck row of the same
            // vehicle (6 m/s, reverse 2 m/s, 1.5 m/s^2) - never at the paving / painting speed (0.46 m/s reversing over 220 m was seen)
            if (leaving && p.Kind == MachineKind.Rmv && !p.IsRoller) p.Lim = MachineLimits.Of(MachineRole.CrewTruck, ph);
            p.Speed = p.Lim.VFwd;
            p.Accel = p.Lim.Accel;
        }

        public static LegBuilder Begin(Puppet p, PlanContext c, bool finishLeg = true, double cutAt = double.NaN)
        {
            RefreshLimits(p, c);
            var b = new LegBuilder { Lim = p.Lim };
            var old = p.Plan;
            // The new plan's FrontB is the model the RUNNING leg uses. A leg copied by an earlier re-plan references the
            // old FrontB; keeping only old.FrontA as FrontB and turning such a leg into a Hold whenever the two differed (every
            // re-plan after a Director re-anchor) froze the excavator mid-cycle on a second re-plan inside one dig cycle (an instant
            // stop mid-hop) and cost the cycle, so with re-plans every second and re-anchors every few seconds the DigHop never ran.
            bool curOnB = false;
            if (old.Count > 0)
            {
                var cur0 = old.Get(MachineMotion.ActiveLeg(old, c.Now - old.Epoch));
                curOnB = FrontDep(cur0) && cur0.Front != 0 && !old.FrontA.SameAs(old.FrontB);
            }
            var plan = new MachinePlan
            {
                Epoch = c.Now,
                FrontA = c.Front,
                FrontB = old.Count > 0 ? (curOnB ? old.FrontB : old.FrontA) : c.Front,
                Track = c.Track != null ? c.Track.Slot : -1,
                HalfLen = old.HalfLen, HalfWid = old.HalfWid, FootOff = old.FootOff,
                Flags = old.Flags,
                Role = (byte)p.Role,
                MKind = (byte)p.Kind,
                SlewDeg = old.SlewDeg > 0f ? old.SlewDeg : 90f,
            };
            if (plan.HalfLen <= 0f) PuppetFactory.Footprint(p, ref plan);
            b.Plan = plan;
            // A box rotating through a lane change swings its corners out; on a visible road the merges are
            // gentle enough that a box clamped to the kerb / centre-line clearance stays within it (rrw test: < 0.08 m)
            b.MergeSlope = c.Visible ? MxConst.kVisibleMergeSlope : MxConst.kMergeSlope;
            if (old.Count <= 0)
            {
                b.T = 0f;
                b.Facing = 1;
                return b;
            }
            double tOld = c.Now - old.Epoch;
            int iNow = MachineMotion.ActiveLeg(old, tOld);
            // TransformFrame history reaches ~31 frames back: 0.6 s of machine time per unit of clock scale
            int iPast = MachineMotion.ActiveLeg(old, tOld - 0.6 * math.max(1f, c.Clk.Scale));
            iPast = math.max(iPast, iNow - 2);
            var cur = old.Get(iNow);
            // cutAt (old-plan leg time) overrides: the separation guard cuts a K-turn at its next cusp
            double end = !double.IsNaN(cutAt) ? cutAt : finishLeg ? NaturalEnd(old, cur, tOld, c) : tOld;
            if (!cur.OpenEnded && end > cur.T1) end = cur.T1;
            if (end < tOld) end = tOld;
            float shift = (float)(old.Epoch - c.Now);
            for (int i = math.max(0, iPast); i <= iNow; i++)
            {
                var l = old.Get(i);
                if (!CopyLeg(old, ref l, c, curOnB)) continue;
                l.T0 += shift;
                if (i == iNow) l.T1 = (float)(end + shift);
                else if (!l.OpenEnded) l.T1 += shift;
                else l.T1 = (float)(old.Get(i + 1).T0 + shift);
                if (b.Plan.Count < MachinePlan.MaxLegs) { b.Plan.Set(b.Plan.Count, l); b.Plan.Count++; b.LastKind = l.Kind; b.LastAnim = l.Anim; }
            }
            // The end state of the COPIED leg (just before `end`): at a leg boundary the old plan's next leg may already
            // run on a re-anchored model (in-place re-anchor) and start a few cm elsewhere - the new plan continues the copied motion
            double endEval = end > tOld + 2e-4 ? end - 1e-4 : end;
            MachineMotion.State(old, c.Clk, old.Epoch + endEval, out var s);
            b.T = (float)(end + shift);
            if (b.T < 0f) b.T = 0f;
            b.U = s.U;
            b.Lat = s.Lat;
            b.Facing = s.Hu >= 0f ? (sbyte)1 : (sbyte)-1;
            b.Odo = s.Odo;
            // Velocity along u where the copied motion ends. A drive / turn / brake that runs to its end stops there;
            // a front-relative leg cut now is still moving (the next leg matches it, or Settle brakes it within Accel).
            bool finished = !cur.OpenEnded && end >= cur.T1 - 1e-4;
            bool exact = cur.Kind == (byte)LegKind.Drive || cur.Kind == (byte)LegKind.Turn || cur.Kind == (byte)LegKind.Brake || cur.Kind == (byte)LegKind.Hold;
            if (finished && exact) b.V = 0f;
            else
            {
                double ta = math.max(old.Epoch + cur.T0, old.Epoch + end - 0.05);
                double tb = old.Epoch + endEval;
                if (tb - ta > 1e-3)
                {
                    MachineMotion.State(old, c.Clk, ta, out var sa);
                    b.V = (float)((s.U - sa.U) / (tb - ta));
                }
                if (math.abs(b.V) < 0.02f) b.V = 0f;
            }
            return b;
        }

        // Bring a moving builder state to rest within the role's acceleration (Brake leg) before a leg that starts
        // at rest (Drive, Turn, Hold).
        public static void Settle(ref LegBuilder b)
        {
            if (math.abs(b.V) < 0.02f) { b.V = 0f; return; }
            float v0 = math.abs(b.V);
            float a = math.max(0.05f, b.Lim.Accel * MxConst.kSettleAccelShare);
            float T = v0 / a, D = 0.5f * v0 * T;
            sbyte mv = b.V >= 0f ? (sbyte)1 : (sbyte)-1;
            var l = new MachineLeg
            {
                T0 = b.T, T1 = b.T + T, Kind = (byte)LegKind.Brake, Anim = b.LastAnim, Facing = b.Facing, Move = mv,
                U0 = b.U, L0 = b.Lat, Vmax = v0, Acc = a, P0 = 0f,
            };
            b.V = 0f;
            if (!b.Add(l)) return;
            b.T = l.T1;
            b.U += mv * D;
            b.Odo += D * (mv == b.Facing ? 1f : -1f);
        }

        // Copied legs referencing the old FrontA now reference the new FrontB. A leg that referenced the old FrontB (two
        // generations back) keeps working only if that model equals the old FrontA; otherwise it becomes a Hold.
        internal static bool FrontDep(in MachineLeg l) =>
            l.Kind == (byte)LegKind.Follow || l.Kind == (byte)LegKind.DigHop || l.Kind == (byte)LegKind.Shuttle || l.Kind == (byte)LegKind.Scrape;

        // onB: the new FrontB is the old FrontB (the running leg references it), else the old FrontA.
        static bool CopyLeg(in MachinePlan old, ref MachineLeg l, PlanContext c, bool onB = false)
        {
            if (!FrontDep(l)) { l.Front = 1; return true; }
            bool refB = l.Front != 0;
            if (refB == onB) { l.Front = 1; return true; }
            if (old.FrontA.SameAs(old.FrontB)) { l.Front = 1; return true; }
            MachineMotion.State(old, c.Clk, c.Now, out var s);
            l.Kind = (byte)LegKind.Hold;
            l.U0 = s.U;
            l.L0 = s.Lat;
            l.Facing = s.Hu >= 0f ? (sbyte)1 : (sbyte)-1;
            return true;
        }

        static double NaturalEnd(in MachinePlan old, in MachineLeg leg, double tOld, PlanContext c)
        {
            double lt = tOld - leg.T0;
            switch ((LegKind)leg.Kind)
            {
                case LegKind.Drive:
                case LegKind.Turn:
                case LegKind.Brake:
                    return leg.T1;
                case LegKind.DigHop:
                {
                    double C = math.max(1f, leg.P0);
                    double k = math.ceil(lt / C - 1e-6);
                    return leg.T0 + k * C;
                }
                case LegKind.Shuttle:
                {
                    float R = math.max(0.5f, leg.P1);
                    float tLeg = MachineMotion.TrapDur(R, leg.Vmax, leg.Acc);
                    float w = math.max(0f, leg.P0);
                    double period = 2.0 * tLeg + 2.0 * w;
                    if (period < 1e-3) return tOld;
                    double k = math.floor(lt / period);
                    double tm = lt - k * period;
                    double baseT = leg.T0 + k * period;
                    if (tm < tLeg) return baseT + tLeg;                       // finish the forward stroke
                    if (tm < tLeg + w) return tOld;                          // stationary
                    if (tm < 2 * tLeg + w) return baseT + 2 * tLeg + w;      // finish the return stroke
                    return tOld;
                }
                case LegKind.Scrape:
                {
                    float P = math.max(8f, leg.P0);
                    double k = math.floor(lt / P);
                    double cc = lt - k * P;
                    if (cc > P - 6f) return leg.T0 + (k + 1) * P;            // finish the reverse bump
                    return tOld;
                }
                default:
                    return tOld;
            }
        }

        // ------------------------------------------------------------ primitive legs

        public static void Hold(ref LegBuilder b, float until, AnimKind anim)
        {
            Settle(ref b);
            var l = new MachineLeg
            {
                T0 = b.T, T1 = float.IsPositiveInfinity(until) ? float.PositiveInfinity : math.max(b.T, until),
                Kind = (byte)LegKind.Hold, Anim = (byte)anim, Facing = b.Facing,
                U0 = b.U, L0 = b.Lat,
            };
            if (!float.IsPositiveInfinity(until) && until <= b.T + 1e-3f) return;
            if (b.Add(l) && !l.OpenEnded) b.T = l.T1;
        }

        // Drive (forward or reversing) to u1 with a lane merge at the start (average slope <= 0.25).
        // A lateral change needs room along the chain. The merge is a function of u (lat(u) over the first w metres), so a
        // short drive with a large lateral change (parking beside / across, a K-turn spot clamped at a chain end) became a crab-walk:
        // u-speed and u-acceleration scaled down by the path factor so far that Trap's floors (0.05) took over, and the path ran at
        // up to 8x the role's acceleration (rrw.mx.check: crew truck a = 2.2-2.6 vs 1.5 while parking).
        // The run-up is taken ONLY when the merge slope the drive actually needs is too steep
        // (dLat > kMaxMergeSlope x D, and dLat > kLatSnap): the drive first runs straight to a run-up point wReq = max(2, dLat /
        // kMaxMergeSlope) before the target (pull-in forwards; backwards when that point lies outside [lo, hi]) and merges over the
        // full wReq. Every other drive is a single leg (merge over min(D, w)). A lateral residual of at most kLatSnap is
        // never a drive of its own (an earlier version dropped every pure lateral shift; always running up turned a 3 cm correction
        // into a 2 m reverse + 2 m forward shuffle); a larger pure lateral shift (D < 3 cm) is a run-up drive.
        public static void Drive(ref LegBuilder b, Puppet p, float u1, float lat1, float vmax = -1f, float lo = float.NegativeInfinity, float hi = float.PositiveInfinity)
        {
            float D0 = math.abs(u1 - b.U), dLat0 = math.abs(lat1 - b.Lat);
            if (D0 < 0.03f && dLat0 <= MxConst.kLatSnap) return;
            if (dLat0 > MxConst.kLatSnap && dLat0 > MxConst.kMaxMergeSlope * D0)
            {
                float wReq = math.max(2f, dLat0 / MxConst.kMaxMergeSlope);
                if (D0 < wReq - 0.01f)
                {
                    float run = u1 - b.Facing * wReq;
                    if (run < lo || run > hi)
                    {
                        float alt = u1 + b.Facing * wReq;
                        run = alt >= lo && alt <= hi ? alt : math.clamp(run, lo, hi);
                    }
                    MxSpeed.RunUps++;
                    DriveLeg(ref b, p, run, b.Lat, vmax);
                    DriveLeg(ref b, p, u1, lat1, vmax);
                    return;
                }
            }
            DriveLeg(ref b, p, u1, lat1, vmax);
        }

        static void DriveLeg(ref LegBuilder b, Puppet p, float u1, float lat1, float vmax)
        {
            float D = math.abs(u1 - b.U);
            if (D < 0.03f) return;
            Settle(ref b);
            D = math.abs(u1 - b.U);
            if (D < 0.03f) return;
            sbyte mv = u1 >= b.U ? (sbyte)1 : (sbyte)-1;
            bool reverse = mv != b.Facing;
            // The role's limit for this kind of leg (forward / reversing / leaving); a requested speed
            // above it is clamped (counted), and every drive is a rest-to-rest trapezoid at the role's acceleration
            float cap = b.Lim.VFor(reverse, false, b.Leaving) * MxConst.kSpeedMargin;
            if (!(cap > 0.05f)) cap = reverse ? MxConst.kReverseSpeed : p.Speed;
            float v = vmax > 0f ? math.min(vmax, cap) : cap;
            if (vmax > cap + 0.01f) MxSpeed.Clamp(p, "drive m/s", vmax, cap);
            float a = b.Lim.Accel > 0.05f ? b.Lim.Accel : p.Accel;
            float dLat = math.abs(lat1 - b.Lat);
            float slope = b.MergeSlope > 0f ? b.MergeSlope : MxConst.kMergeSlope;
            float w = 0f;
            if (dLat > 0.02f)
            {
                w = math.max(2f, dLat / slope);
                // visible road: the rotating box's corner swing (~3 dLat (L / w)^2 for a box of half length L) stays < ~4 cm,
                // so a box clamped to the kerb / centre-line clearance never touches the sidewalk / the open half
                if (b.MergeSlope > 0f && b.MergeSlope < MxConst.kMergeSlope)
                    w = math.max(w, (p.BoxHalfLen + math.abs(p.BoxOffZ)) * math.sqrt(80f * dLat));
                w = math.min(D, w);
                // The lane merge adds a lateral component (|dlat/du| <= 1.5 dLat / w): the speed along the path stays
                // within the limit, and so does the path acceleration (the merge runs while the drive speeds up)
                float k = 1.5f * dLat / math.max(0.05f, w);
                float g = math.sqrt(1f + k * k);
                v /= g;
                a *= 0.85f / (g * (1f + 2f * k * k));
            }
            var l = new MachineLeg
            {
                T0 = b.T, T1 = b.T + MachineMotion.TrapDur(D, v, a),
                Kind = (byte)LegKind.Drive, Anim = (byte)AnimKind.Rest, Facing = b.Facing, Move = mv,
                U0 = b.U, U1 = u1, L0 = b.Lat, L1 = dLat > 0.02f ? lat1 : b.Lat, Vmax = v, Acc = a,
                P0 = 0f, P1 = math.max(0.05f, w),
            };
            if (!b.Add(l)) return;
            b.T = l.T1;
            b.Odo += D * (reverse ? -1f : 1f);
            b.U = u1;
            b.Lat = l.L1;
        }

        // Three-move K-turn about the current position (flips the facing; lateral excursion ~ +-R/2 + body). Caller merges
        // to the centre first.
        public static void Turn(ref LegBuilder b, Puppet p, float side) => Turn(ref b, p, side, MxConst.kTurnRadius);

        // Radius R (leavers' compact K-turn, LeaveTurnFit); the leg carries it in P0 (MachineMotion evaluates any R >= 1).
        public static void Turn(ref LegBuilder b, Puppet p, float side, float radius)
        {
            Settle(ref b);
            // Forward arcs at VTurn, the reverse arc at min(VTurn, VRev), the role's acceleration (not fixed speeds)
            float vF = math.max(0.2f, b.Lim.VFor(false, true, false) * MxConst.kSpeedMargin);
            float vR = math.max(0.2f, b.Lim.VFor(true, true, false) * MxConst.kSpeedMargin);
            float a = b.Lim.Accel > 0.05f ? b.Lim.Accel : p.Accel;
            float R = math.max(1f, radius);
            var l = new MachineLeg
            {
                T0 = b.T, T1 = b.T + MachineMotion.TurnDuration(R, vF, vR, a),
                Kind = (byte)LegKind.Turn, Anim = (byte)AnimKind.Rest, Facing = b.Facing,
                U0 = b.U, L0 = b.Lat, Vmax = vF, Acc = a, P0 = R, P1 = side, P2 = vR,
            };
            if (!b.Add(l)) return;
            b.T = l.T1;
            b.Odo += R * math.PI / 3f;
            b.Facing = (sbyte)-b.Facing;
        }

        // Moves to (u, lat). Behind the nose by more than 35 m: reverse a little while merging to the centre, K-turn,
        // then drive forward (no reverse leg longer than 35 m). needFacing: also end with the given facing.
        public static void GoTo(ref LegBuilder b, Puppet p, PlanContext c, float u, float lat, sbyte facing, bool needFacing)
        {
            float du = u - b.U;
            float dist = math.abs(du);
            sbyte mv = du >= 0f ? (sbyte)1 : (sbyte)-1;
            if (dist >= 0.03f || math.abs(lat - b.Lat) > MxConst.kLatSnap)   // a pure lateral shift > kLatSnap is a drive too (run-up)
            {
                // A roller never K-turns and has no reverse limit (symmetric machine, swivel seat)
                if (mv == b.Facing || dist <= RRWConst.kMaxReverseLeg || !c.CanTurn || p.IsRoller) Drive(ref b, p, u, lat, -1f, RunLo(c, p), RunHi(c, p));
                else
                {
                    float rev = math.min(dist * 0.5f, math.max(4f * math.abs(b.Lat) + 2f, 6f));
                    float tu = math.clamp(b.U + mv * rev, TurnLo(c), TurnHi(c));
                    Drive(ref b, p, tu, 0f, -1f, RunLo(c, p), RunHi(c, p));
                    Turn(ref b, p, TurnSide(b));
                    Drive(ref b, p, u, lat, -1f, RunLo(c, p), RunHi(c, p));
                }
            }
            if (needFacing && b.Facing != facing && c.CanTurn && !p.IsRoller)
            {
                float m = math.max(4f * math.abs(b.Lat), 4f) + 2f;
                float tu = math.clamp(b.U + b.Facing * m, TurnLo(c), TurnHi(c));
                Drive(ref b, p, tu, 0f, -1f, RunLo(c, p), RunHi(c, p));
                Turn(ref b, p, TurnSide(b));
                Drive(ref b, p, u, lat, -1f, RunLo(c, p), RunHi(c, p));
            }
        }

        static float TurnSide(in LegBuilder b) => (b.Odo % 2f) < 1f ? 1f : -1f;   // deterministic variety

        // ------------------------------------------------------------ commit

        public static void Commit(Puppet p, ref LegBuilder b, PlanContext c, double replanAt)
        {
            if (b.Plan.Count == 0) Hold(ref b, float.PositiveInfinity, AnimKind.Rest);
            // the last leg must be open-ended or the plan ends in a stationary clamp
            p.Plan = b.Plan;
            p.PlanDirty = true;
            p.ReplanAt = replanAt;
            p.PlannedPhase = c.View.Phase;
            p.PlannedWorking = c.Working;
            p.PlannedTrackRevision = c.Track != null ? c.Track.Revision : -1;
            if (b.Overflow) RRWLog.Once("machines-overflow-" + p.Role, "machines: plan for " + p + " exceeded " + MachinePlan.MaxLegs + " legs (tail dropped)");
        }

        // ------------------------------------------------------------ role planners

        // Parked / DriveOut / None (stand still) / not working.
        public static void PlanStatic(Puppet p, PlanContext c, float u, float lat, sbyte facing, bool needFacing, AnimKind anim)
        {
            var b = Begin(p, c);
            GoTo(ref b, p, c, u, lat, facing, needFacing);
            Hold(ref b, float.PositiveInfinity, anim);
            Commit(p, ref b, c, double.PositiveInfinity);
        }

        // Stop where the current leg ends (idle crews).
        public static void PlanIdle(Puppet p, PlanContext c)
        {
            var b = Begin(p, c);
            Hold(ref b, float.PositiveInfinity, AnimKind.Rest);
            Commit(p, ref b, c, double.PositiveInfinity);
        }

        // ------------------------------------------------------------ front-relative dynamics

        // Return-aware anchor: the anchor is back at Home (u) by machine time By, moving back at Speed (0 = off).
        public struct RetSpec
        {
            public float Home, Speed;
            public double By;
            public readonly bool On => Speed > 0f && !double.IsNaN(By);
        }

        // Target of a front-relative leg prototype that would start at absolute time t0 (no catch-up / velocity terms, no lag).
        static float ProtoTarget(in LegBuilder b, PlanContext c, MachineLeg l, double t0, in RetSpec ret, float hiCut = 0f)
        {
            if (ret.On) { l.RetU = ret.Home; l.RetV = ret.Speed; l.RetT = (float)(ret.By - t0); }
            l.Rate = 0f;
            if (!(l.Acc > 0f)) l.Acc = math.max(0.05f, b.Lim.Accel);
            return MachineMotion.FollowU(b.Plan, l, c.Clk, t0, 0f, hiCut);
        }

        // Fills the dynamics of a front-relative leg l that starts at b.T (Kind, Front 0, U0, L0, Lo, Hi set; Acc = 0 -> the role's):
        // the role's acceleration (soft anchor corners), the return-aware cap, the lag cap when the crew front outruns VFwd
        // (counted frontLag), the start velocity match with the motion it continues (Cv) and a catch-up offset whose speed
        // and acceleration stay within the role's limits (Cu / Cl / CatchDur; counted clampedLegs when longer than the simple, unlimited one).
        // Returns the anchor at the leg start.
        static float FrontDynamics(ref MachineLeg l, ref LegBuilder b, Puppet p, PlanContext c, in RetSpec ret, float hiCut = 0f)
        {
            var lim = b.Lim;
            double t0 = b.Abs(b.T);
            if (!(l.Acc > 0f)) l.Acc = math.max(0.05f, lim.Accel);
            if (ret.On) { l.RetU = ret.Home; l.RetV = ret.Speed; l.RetT = (float)(ret.By - t0); }
            l.Rate = 0f;
            float vF = MachineMotion.FrontSpeed(c.Front, c.Clk, t0);
            float vMax = lim.VFwd * MxConst.kSpeedMargin;
            if (math.abs(vF) > vMax && vMax > 0.05f)
            {
                // front lag: the crew front is faster than the role can drive: follow a slowed copy of it (pivot = the front now, or its
                // section start in the lead-in) at 0.95 x the role's margin speed; it lags its front (counted frontLag)
                l.Rate = 0.95f * vMax / math.abs(vF);
                l.RateU = MachineMotion.LagPivot(c.Front, c.Clk, t0);
                CountLag(p, c, vF, 0.95f * vMax);
            }
            float x0 = MachineMotion.FollowU(b.Plan, l, c.Clk, t0, 0f, hiCut);
            const float h = 0.25f;
            float x1 = MachineMotion.FollowU(b.Plan, l, c.Clk, t0, h, hiCut);
            float vT = (x1 - x0) / h;
            float cv = b.V - vT;
            if (math.abs(cv) < 0.02f) cv = 0f;
            l.Cv = cv;
            l.VDur = cv != 0f ? math.max(0.3f, math.abs(cv) / (MxConst.kVelMatchAccelShare * l.Acc)) : 0f;
            b.V = 0f;
            l.Cu = b.U - x0;
            l.Cl = b.Lat - l.L0;
            float acu = math.abs(l.Cu), acl = math.abs(l.Cl);
            if (acu > 0.01f || acl > 0.01f)
            {
                float vRoom = math.max(0.05f * lim.VFwd, MxConst.kSpeedMargin * math.min(lim.VFwd, lim.VRev) - math.abs(vT) - math.abs(cv));
                float aRoom = MxConst.kCatchAccelShare * l.Acc;
                float T = math.max(2f, math.max(1.5f * acu / vRoom, math.sqrt(6f * acu / aRoom)));
                T = math.max(T, math.max(1.5f * acl / math.max(0.1f, 0.3f * lim.VFwd), math.sqrt(6f * acl / aRoom)));
                float r3 = math.clamp(math.max(acu, acl) / 0.7f, 2f, 12f);   // simple catch-up duration (no limits)
                if (T > r3 + 0.05f) MxSpeed.Clamp(p, "catch-up s", r3, T);
                l.CatchDur = T;
            }
            else { l.Cu = l.Cl = 0f; l.CatchDur = 0f; }
            return x0;
        }

        // Catch-up of a DigHop leg: the residual decays over at least one cycle within the role's limits.
        static float HopCatchDur(float cu, float C, in MachineLimits lim)
        {
            float a = math.abs(cu);
            if (a <= 0.01f) return 0f;
            float aRoom = MxConst.kCatchAccelShare * math.max(0.05f, lim.Accel);
            float vRoom = math.max(0.02f, (1f - MxConst.kSpeedMargin) * lim.VFwd + 0.05f);
            return math.max(C, math.max(1.5f * a / vRoom, math.sqrt(6f * a / aRoom)));
        }

        // Front lag: the crew front outruns the role (counted once per puppet and phase; one Info line per project and phase).
        static void CountLag(Puppet p, PlanContext c, float vF, float rate)
        {
            if (p.FrontLagPhase == c.View.Phase) return;
            p.FrontLagPhase = c.View.Phase;
            MxSpeed.FrontLag++;
            var st = c.State;
            if (st == null || c.Project == null || st.FrontLagLogged == c.View.Phase) return;
            st.FrontLagLogged = c.View.Phase;
            MxSpeed.LastLag = p + " front " + RRWLog.F(vF) + " m/s > " + RRWLog.F(rate) + " m/s (" + c.View.Phase + ", crews " + c.Crews + ")";
            RRWLog.Info("machines: project #" + c.Project.Id + " " + c.View.Phase + ": crew front " + RRWLog.F(vF) + " m/s outruns " + p.Role +
                        " (follows at " + RRWLog.F(rate) + " m/s and lags its front; crews=" + c.Crews + ", frontLag)");
        }

        // Over work: the front owner works faster than its realistic working pace (crew cap reached; allowed up to VFwd, counted).
        static void CountOverWork(Puppet p, PlanContext c, float vF)
        {
            if (!(p.Lim.VWork > 0f) || p.PostSite || p.Role != c.Crew.FrontOwner(c.View.Phase)) return;
            if (math.abs(vF) <= p.Lim.VWork * 1.02f || p.OverWorkPhase == c.View.Phase) return;
            p.OverWorkPhase = c.View.Phase;
            MxSpeed.OverWork++;
            if (RRWLog.VerboseEnabled)
                RRWLog.Verbose("machines: " + p + " works above its pace: crew front " + RRWLog.F(vF) + " > " + RRWLog.F(p.Lim.VWork) + " m/s (overWork, crews=" + c.Crews + ")");
        }

        // Hop window and lag line of a step-and-work cycle of C s at crew front speed vF: the hop (rest-to-rest
        // trapezoid at kHopAccelShare x Accel and kSpeedMargin x VFwd) must cover vF x C inside the window; the window grows into
        // the work part down to kMinDigSeconds of work; beyond that the anchors lag on a line of slope `rate` (frontLag).
        public static void HopWindow(in MachineLimits lim, float vF, float C, float defaultDur, out float dur, out float vmax, out float acc, out float rate, out bool grown) =>
            HopWindow(lim, vF, C, defaultDur, C - MxConst.kMinDigSeconds, out dur, out vmax, out acc, out rate, out grown);

        // The IK schedule grows the window up to DigSchedule.HopWindowMax (the root stands still in [0, StationaryEnd]).
        public static void HopWindow(in MachineLimits lim, float vF, float C, float defaultDur, float maxDur, out float dur, out float vmax, out float acc, out float rate, out bool grown)
        {
            vmax = math.max(0.1f, lim.VFwd * MxConst.kSpeedMargin);
            acc = math.max(0.05f, lim.Accel * MxConst.kHopAccelShare);
            float d = math.abs(vF) * C;
            float wMax = math.max(defaultDur, maxDur);
            dur = defaultDur;
            while (dur < wMax - 1e-3f && MachineLimits.MaxReach(dur, vmax, acc) < d) dur = math.min(wMax, dur + 0.5f);
            grown = dur > defaultDur + 1e-3f;
            float reach = MachineLimits.MaxReach(dur, vmax, acc);
            rate = reach < d - 1e-3f ? 0.97f * reach / d : 0f;   // time scale of the slowed front the anchors follow
        }

        // The crew's dig grid hop (excavator + its loading trucks hop together with exactly these parameters).
        public static void DigGrid(PlanContext c, CrewState cs, in MachineLimits lim, float C, out bool grown) =>
            DigGrid(c, cs, lim, C, MxConst.kHopDur, C - MxConst.kMinDigSeconds, out grown);

        public static void DigGrid(PlanContext c, CrewState cs, in MachineLimits lim, float C, float defaultDur, float maxDur, out bool grown)
        {
            HopWindow(lim, c.FrontSpeed, C, defaultDur, maxDur, out float dur, out float vmax, out float acc, out float rate, out grown);
            cs.DigHopDur = dur;
            cs.DigHopStart = C - dur;
            cs.DigVmax = vmax;
            cs.DigAcc = acc;
            if (!(rate > 0f)) cs.ResetLag();
            else
            {
                // keep the pivot of a lag already running in this layout (the anchors stay on one curve across re-plans)
                if (!(cs.DigRate > 0f) || float.IsNaN(cs.DigRateU)) cs.DigRateU = MachineMotion.LagPivot(c.Front, c.Clk, c.Now);
                cs.DigRate = rate;
            }
        }

        // Anchor of the crew dig grid at machine time t (the excavator's DigHop anchor; slowed front included).
        public static float DigAnchor(PlanContext c, CrewState cs, double t)
        {
            float F;
            if (cs.DigRate > 0f && cs.DigRate < 1f && !float.IsNaN(cs.DigRateU))
            {
                MachineMotion.FrontLin(c.Front, c.Clk, t, out float flin, out _, out float fmin, out float fmax);
                F = math.clamp(cs.DigRateU + cs.DigRate * (flin - cs.DigRateU), fmin, fmax);
            }
            else F = c.FrontAt(t);
            return math.clamp(F + cs.DigOffset, cs.DigLo, math.max(cs.DigLo, cs.DigHi));
        }

        // Creep with the front (Follow, Pave, Paint, crew truck, compaction loader).
        public static void PlanFollow(Puppet p, PlanContext c, float offset, float lo, float hi, float lat, sbyte facing, AnimKind anim) =>
            PlanFollow(p, c, offset, lo, hi, lat, facing, anim, default);

        public static void PlanFollow(Puppet p, PlanContext c, float offset, float lo, float hi, float lat, sbyte facing, AnimKind anim, in RetSpec ret)
        {
            var b = Begin(p, c);
            if (hi < lo) hi = lo;
            var proto = new MachineLeg
            {
                T1 = float.PositiveInfinity, Kind = (byte)LegKind.Follow, Anim = (byte)anim, Facing = facing,
                U0 = offset, L0 = lat, Lo = lo, Hi = hi, Acc = b.Lim.Accel,
            };
            if (ret.On && !p.ExcavatorRig && c.CanTurn && PlanReturnTrip(p, c, ref b, proto, lat, facing, anim, ret)) return;
            float tgtNow = ProtoTarget(b, c, proto, b.Abs(b.T), ret);
            if (math.abs(tgtNow - b.U) > 5f || b.Facing != facing)
            {
                // drive to where the anchor will be on arrival (two passes)
                float est = tgtNow;
                for (int it = 0; it < 2; it++)
                {
                    var probe = b;
                    GoTo(ref probe, p, c, est, lat, facing, true);
                    est = ProtoTarget(probe, c, proto, probe.Abs(probe.T), ret);
                }
                GoTo(ref b, p, c, est, lat, facing, true);
            }
            var l = proto;
            l.T0 = b.T;
            l.U1 = b.U;
            FrontDynamics(ref l, ref b, p, c, ret);
            CountOverWork(p, c, c.FrontSpeed);
            b.Add(l);
            Commit(p, ref b, c, double.PositiveInfinity);
        }

        // A WHEELED return-aware follower (crew truck) whose way home is longer than kMaxReverseLeg does
        // not reverse all the way: it follows the front until the return line meets its anchor, then drives home with K-turns
        // (GoTo: reverse a little, turn, drive, turn back to its working facing) and waits there. False = the continuous
        // return-aware follow applies (short way back, no K-turn possible, or home is never needed before the deadline).
        static bool PlanReturnTrip(Puppet p, PlanContext c, ref LegBuilder b, in MachineLeg proto, float lat, sbyte facing, AnimKind anim, in RetSpec ret)
        {
            double t0 = b.Abs(b.T);
            double tMeet = double.NaN;
            float xMeet = 0f;
            if (ret.By > t0)
            {
                for (double t = t0; t <= ret.By + 1e-6; t += 1.0)
                {
                    float x = ProtoTarget(b, c, proto, t, default);
                    float R = ret.Home + ret.Speed * (float)(ret.By - t);
                    if (R <= x) { tMeet = t; xMeet = x; break; }
                }
                if (double.IsNaN(tMeet)) return false;
            }
            else tMeet = t0;
            if (tMeet <= t0 + 1.0)
            {
                // already on the way home: a long way drives (K-turns), a short one reverses with the return-aware anchor
                if (b.U - ret.Home <= RRWConst.kMaxReverseLeg) return false;
            }
            else
            {
                if (xMeet - ret.Home <= RRWConst.kMaxReverseLeg) return false;
                float tgtNow = ProtoTarget(b, c, proto, t0, default);
                if (math.abs(tgtNow - b.U) > 5f || b.Facing != facing) GoTo(ref b, p, c, tgtNow, lat, facing, true);
                if (b.Abs(b.T) < tMeet - 1.0) AddFollow(ref b, p, c, proto.U0, proto.Lo, proto.Hi, lat, facing, b.Rel(tMeet), anim);
            }
            // home with the facing the drive arrives with (a second K-turn would not fit the leg budget; the next phase's plan
            // turns it if it needs the other facing)
            GoTo(ref b, p, c, ret.Home, lat, facing, false);
            Hold(ref b, float.PositiveInfinity, AnimKind.Rest);
            CountOverWork(p, c, c.FrontSpeed);
            Commit(p, ref b, c, double.PositiveInfinity);
            return true;
        }

        // Excavator step-and-dig / breaker. Aligned to the crew's dig grid so loading trucks hop with it.
        // With Detailed digging (and a readable rig) the grid uses the IK schedule: C = DigSchedule.Period
        // (20 s, fixed), default hop window = HopDefault (return + hold), growing up to HopWindowMax (the root stands still while the
        // bucket digs); the breaker uses the same grid. Otherwise the fixed keyframe cycle (kDigCycleSeconds / kBreakCycle).
        public static bool IkDigging(Puppet p, PlanContext c)
        {
            if (!c.DetailedDigging || p.Kind != MachineKind.Excavator || c.Ps == null) return false;
            if (p.Dig != null && p.Dig.Arm != null) return true;
            var arm = DigArms.Get(c.Em, c.Ps, p.Prefab, p.Scale);
            if (arm == null) return false;
            if (p.Dig == null) p.Dig = new DigState();
            p.Dig.Arm = arm;
            return true;
        }

        public static void PlanDig(Puppet p, PlanContext c, float offset, float lat, AnimKind anim)
        {
            var cs = c.CS;
            bool ik = IkDigging(p, c);
            var mode = anim == AnimKind.Break ? DigMode.Break : DigMode.Dig;
            var sched = DigSchedule.Nominal(mode);
            float C = ik ? sched.Period : anim == AnimKind.Break ? MxConst.kBreakCycle : RRWConst.kDigCycleSeconds;
            // a new cycle length starts a new grid (the trucks re-plan onto it with their next trip)
            if (cs.DigC > 0f && math.abs(cs.DigC - C) > 0.01f) cs.DigOrigin = double.NaN;
            cs.DigC = C;
            cs.DigIk = ik;
            if (ik && !(cs.DigSched.Period > 0f)) cs.DigSched = sched;
            float lo = CrewLo(c, p), hi = CrewHi(c, p);
            if (hi < lo) hi = lo;
            cs.DigOffset = offset; cs.DigLo = lo; cs.DigHi = hi;
            var b = Begin(p, c);
            float defDur = ik ? sched.HopDefault : MxConst.kHopDur;
            DigGrid(c, cs, b.Lim, C, defDur, ik ? sched.HopWindowMax : C - MxConst.kMinDigSeconds, out bool grown);
            if (grown) MxSpeed.Clamp(p, "hop window s", defDur, cs.DigHopDur);
            // A re-plan must never cost the excavator a dig cycle, and its first cycle starts on arrival:
            //  * Begin finishes a running DigHop at its cycle boundary (NaturalEnd). When the excavator then stands within kDigAbsorb of the
            //    anchor of the next grid cycle (always, unless the front model jumped), that cycle starts right there: the small residual is
            //    absorbed by the DigHop catch-up (Cu, within the role's limits), never by a short drive. Driving the few cm a re-anchor
            //    moved the anchor pushed the arrival past the cycle start and lost a whole 20 s cycle (a 12.6 s hold was seen).
            //  * Otherwise (spawn, first plan, after a drive-across / guard hold / hand-over) the excavator drives to the anchor of its
            //    ARRIVAL and the grid is re-phased so the first cycle starts on arrival (instead of waiting up to a full period on the
            //    grid's seeded phase). Not while a loading truck already holds dumps on the old phase (GridLocked): then the grid slot
            //    search runs. Trucks planned on the old phase and still empty re-plan their outbound onto the new one (UpdateTruck, GridEpoch).
            bool fresh = double.IsNaN(cs.DigOrigin);
            double t0 = b.Abs(b.T);
            double Tk = double.NaN;
            // (a DigHop finished at its boundary may still carry its catch-up / lag creep of a few cm/s: absorbed like the residual)
            // A lateral change within kDigAbsorbLat (re-plans with a slightly different LatOf / DiggerLat:
            // D0 -> C1 lane mapping, width changes along u) is absorbed by the DigHop's lateral catch-up (Cl) like the grader's, never by
            // a drive: it must not push a digging excavator out of the continue path into a re-phase (and the trucks into re-plans)
            bool atRest = math.abs(b.V) < 0.08f && b.Facing == 1 && math.abs(lat - b.Lat) <= MxConst.kDigAbsorbLat;
            if (!fresh && atRest)
            {
                double tg = NextSlot(cs.DigOrigin, C, t0);
                bool rephaseWanted = tg - t0 > MxConst.kDigStartMaxWait && !GridLocked(c, p);
                if (!rephaseWanted && math.abs(DigAnchor(c, cs, tg + 0.5 * C) - b.U) <= MxConst.kDigAbsorb)
                {
                    Tk = tg;   // the next grid cycle starts where it stands (continuing digger: at its boundary, no hold)
                    b.V = 0f;
                    if (tg - t0 < 1e-3) cs.Continued++;
                }
            }
            if (double.IsNaN(Tk) && (fresh || !GridLocked(c, p)))
            {
                // re-phase onto the arrival: drive to the anchor of the cycle that starts on arrival (iterated: anchor <-> drive time)
                float A = DigAnchor(c, cs, t0 + 0.5 * C);
                if (atRest && math.abs(A - b.U) <= MxConst.kDigAbsorb) b.V = 0f;
                else
                {
                    for (int it = 0; it < 5; it++)
                    {
                        var probe = b;
                        GoTo(ref probe, p, c, A, lat, 1, true);
                        float An = DigAnchor(c, cs, probe.Abs(probe.T) + 0.5 * C);
                        if (math.abs(An - A) < 0.05f) { A = An; break; }
                        A = An;
                    }
                    GoTo(ref b, p, c, A, lat, 1, true);
                }
                double arr = b.Abs(b.T);
                double tg = fresh ? double.PositiveInfinity : NextSlot(cs.DigOrigin, C, arr);
                if (tg - arr > MxConst.kDigStartMaxWait)
                {
                    cs.DigOrigin = arr;
                    cs.GridEpoch++;
                    if (!fresh) cs.Rephases++;
                    Tk = arr;
                }
                else Tk = tg;
            }
            if (double.IsNaN(Tk))
            {
                // The grid is locked by a loading truck. Pick the first grid cycle the excavator can reach (it then stands still at
                // A(k) for the whole first dig: no catch-up while digging). A(k) depends on the cycle and the cycle on the drive: iterate.
                float A = DigAnchor(c, cs, b.Abs(b.T));
                Tk = cs.DigOrigin;
                for (int it = 0; it < 5; it++)
                {
                    var probe = b;
                    GoTo(ref probe, p, c, A, lat, 1, true);
                    double arr = probe.Abs(probe.T);
                    Tk = NextSlot(cs.DigOrigin, C, arr);
                    float An = DigAnchor(c, cs, Tk + 0.5 * C);
                    if (math.abs(An - A) < 0.05f) { A = An; break; }
                    A = An;
                }
                if (atRest && math.abs(A - b.U) <= MxConst.kDigAbsorb && b.Abs(b.T) <= Tk + 1e-3) b.V = 0f;
                else GoTo(ref b, p, c, A, lat, 1, true);
                for (int it = 0; it < 3 && b.Abs(b.T) > Tk + 1e-3; it++)
                {
                    // arrived after the chosen cycle start (the last iteration lengthened the drive): next reachable slot
                    Tk = NextSlot(cs.DigOrigin, C, b.Abs(b.T));
                    float An = DigAnchor(c, cs, Tk + 0.5 * C);
                    if (math.abs(An - b.U) > MxConst.kDigAbsorb) Drive(ref b, p, An, lat);
                }
                if (b.Abs(b.T) > Tk + 1e-3) Tk = NextSlot(cs.DigOrigin, C, b.Abs(b.T));
            }
            Hold(ref b, b.Rel(Tk), AnimKind.Rest);
            float A0 = DigAnchor(c, cs, Tk + 0.5 * C);
            // the crew front is faster than the hop can follow: the anchors follow a slowed copy of the front (lag line, frontLag)
            if (cs.DigRate > 0f) CountLag(p, c, c.FrontSpeed, cs.DigRate * math.abs(c.FrontSpeed));
            var l = new MachineLeg
            {
                T0 = b.Rel(Tk), T1 = float.PositiveInfinity, Kind = (byte)LegKind.DigHop, Anim = (byte)anim, Facing = 1,
                U0 = offset, U1 = b.U, L0 = lat, Lo = lo, Hi = hi,
                P0 = C, P1 = cs.DigHopStart, P2 = cs.DigHopDur, Vmax = cs.DigVmax, Acc = cs.DigAcc,
                Rate = cs.DigRate, RateU = float.IsNaN(cs.DigRateU) ? 0f : cs.DigRateU,
                Cu = b.U - A0, Cl = b.Lat - lat, CatchDur = 0f,
            };
            // a residual (planning drift, a small lateral change) is absorbed within the role's limits (at least one cycle)
            l.CatchDur = math.max(HopCatchDur(l.Cu, C, b.Lim), HopCatchDur(l.Cl, C, b.Lim));
            if (l.CatchDur <= 0f) { l.Cu = 0f; l.Cl = 0f; }
            b.T = l.T0;
            b.Add(l);
            b.Plan.SlewDeg = c.Narrow ? 180f : 90f;
            CountOverWork(p, c, c.FrontSpeed);
            Commit(p, ref b, c, double.PositiveInfinity);
        }

        // First cycle start of the grid (origin, period C) at or after t (a boundary within 1 ms counts as reached).
        public static double NextSlot(double origin, float C, double t) => origin + math.ceil((t - origin) / C - 1e-4) * C;

        // The crew's dig grid phase is held by a loading truck that already got dumps on it (re-phasing would strand it).
        static bool GridLocked(PlanContext c, Puppet digger)
        {
            var cs = c.CS;
            if (cs == null) return false;
            for (int r = (int)MachineRole.TruckA; r <= (int)MachineRole.TruckB; r++)
            {
                var t = cs.Roles[r];
                if (t == null || t == digger || t.PostSite || !t.IsTruck) continue;
                if (t.Stage == TruckStage.Outbound && (t.Buckets > 0 || t.AmountPct > 0)) return true;
            }
            return false;
        }

        // Loader spreading the dump heaps (C2, D2): shuttle F-6 .. F+8 with 2 s waits. The strokes run at
        // <= min(1.5, VFwd) over ground (front speed included) and <= VRev reversing, at half the role's acceleration.
        public static void PlanSpread(Puppet p, PlanContext c, float anchorOffset, float lat)
        {
            const float range = 14f;
            float U0 = anchorOffset - 7f;
            float lo = CrewLo(c, p), hi = CrewHi(c, p);
            if (hi < lo + range) hi = lo + range;
            var b = Begin(p, c);
            float vF = math.abs(c.FrontSpeed);
            float m = MxConst.kSpeedMargin;
            float vs = math.max(0.2f, math.min(math.min(1.5f, b.Lim.VFwd) * m - vF, b.Lim.VRev * m + vF));
            float acc = math.max(0.05f, 0.5f * b.Lim.Accel);
            if (vs < 1.5f - 0.01f) MxSpeed.Clamp(p, "spread stroke m/s", 1.5f, vs);
            var proto = new MachineLeg
            {
                T1 = float.PositiveInfinity, Kind = (byte)LegKind.Shuttle, Anim = (byte)AnimKind.Spread, Facing = 1,
                U0 = U0, L0 = lat, Lo = lo, Hi = hi, Vmax = vs, Acc = acc, P0 = 2f, P1 = range,
            };
            float est = ProtoTarget(b, c, proto, b.Abs(b.T), default, range);
            for (int it = 0; it < 2; it++)
            {
                var probe = b;
                GoTo(ref probe, p, c, est, lat, 1, true);
                est = ProtoTarget(probe, c, proto, probe.Abs(probe.T), default, range);
            }
            GoTo(ref b, p, c, est, lat, 1, true);
            var l = proto;
            l.T0 = b.T;
            l.U1 = b.U;
            FrontDynamics(ref l, ref b, p, c, default, range);
            b.Add(l);
            Commit(p, ref b, c, double.PositiveInfinity);
        }

        // Topsoil loader (C0): creep at F - 5, every P0 s reverse 6 m and come back. The bump is two rest-to-rest
        // trapezoids within VRev / VFwd (front speed included) at half the role's acceleration (not a fixed 6 m in 3 s each way).
        public static void PlanScrape(Puppet p, PlanContext c, float offset, float lat)
        {
            float lo = CrewLo(c, p), hi = CrewHi(c, p);
            if (hi < lo) hi = lo;
            var b = Begin(p, c);
            float vF = math.abs(c.FrontSpeed);
            float m = MxConst.kSpeedMargin;
            float vb = math.max(0.2f, math.min(b.Lim.VRev * m + vF, b.Lim.VFwd * m - vF));
            float ab = math.max(0.05f, 0.5f * b.Lim.Accel);
            float half = MachineMotion.TrapDur(6f, vb, ab);
            float P = math.max(25f, 2f * half + 4f);
            if (half > 3f + 0.01f) MxSpeed.Clamp(p, "scrape bump s", 3f, half);
            var proto = new MachineLeg
            {
                T1 = float.PositiveInfinity, Kind = (byte)LegKind.Scrape, Anim = (byte)AnimKind.Scrape, Facing = 1,
                U0 = offset, L0 = lat, Lo = lo, Hi = hi, P0 = P, P1 = 6f, P2 = half, Vmax = vb, Acc = ab,
            };
            float est = ProtoTarget(b, c, proto, b.Abs(b.T), default);
            for (int it = 0; it < 2; it++)
            {
                var probe = b;
                GoTo(ref probe, p, c, est, lat, 1, true);
                est = ProtoTarget(probe, c, proto, probe.Abs(probe.T), default);
            }
            GoTo(ref b, p, c, est, lat, 1, true);
            var l = proto;
            l.T0 = b.T;
            l.U1 = b.U;
            FrontDynamics(ref l, ref b, p, c, default);
            b.Add(l);
            CountOverWork(p, c, c.FrontSpeed);
            Commit(p, ref b, c, double.PositiveInfinity);
        }

        // Grader (excavator clone in the Loader role): step-and-work on the 10 s grid like the digger, but with its
        // own front offset / clamps and WITHOUT touching the crew's dig grid (trucks load at the digger only). Stationary
        // for [0, hopStart) while the bucket works (AnimKind.Spread: reach - drag flat - lift; AnimKind.Scrape: strip - cast
        // left), then steps forward to the next anchor during [hopStart, 10). The hop is a trapezoid at the
        // grader's limits; the window grows into the work part (>= kMinDigSeconds of work); a crew front that even the longest
        // window cannot follow (C0 topsoil strip at ~1 m/s) makes the grader CRAWL with the front while its arm works (Follow leg
        // with the work anim cycling), at the front speed (<= VFwd; beyond it the follower lags, frontLag).
        public static void PlanGrade(Puppet p, PlanContext c, float offset, float lo, float hi, float lat, AnimKind anim, float hopStart, float hopDur)
        {
            float C = RRWConst.kDigCycleSeconds;
            if (hi < lo) hi = lo;
            RefreshLimits(p, c);
            HopWindow(p.Lim, c.FrontSpeed, C, hopDur, out float dur, out float vmax, out float acc, out float rate, out bool grown);
            if (rate > 0f) { PlanFollow(p, c, offset, lo, hi, lat, 1, anim); return; }
            if (grown) MxSpeed.Clamp(p, "grade hop window s", hopDur, dur);
            var b = Begin(p, c);
            // A re-plan that finds the grader at (within kDigAbsorb of) its next anchor starts the next step-and-work
            // cycle right there (Begin finished the running cycle at its boundary); the residual goes into the catch-up (Cu / Cl)
            float Ar = math.clamp(c.FrontAt(b.Abs(b.T) + 0.5 * C) + offset, lo, hi);
            if (math.abs(b.V) < 0.08f && b.Facing == 1 && math.abs(Ar - b.U) <= MxConst.kDigAbsorb && math.abs(b.Lat - lat) <= MxConst.kDigAbsorbLat) b.V = 0f;
            else
            {
                float A = math.clamp(c.FrontAt(b.Abs(b.T)) + offset, lo, hi);
                for (int it = 0; it < 3; it++)
                {
                    var probe = b;
                    GoTo(ref probe, p, c, A, lat, 1, true);
                    float An = math.clamp(c.FrontAt(probe.Abs(probe.T) + 0.5 * C) + offset, lo, hi);
                    if (math.abs(An - A) < 0.05f) { A = An; break; }
                    A = An;
                }
                GoTo(ref b, p, c, A, lat, 1, true);
            }
            Settle(ref b);
            double t0 = b.Abs(b.T);
            var l = new MachineLeg
            {
                T0 = b.T, T1 = float.PositiveInfinity, Kind = (byte)LegKind.DigHop, Anim = (byte)anim, Facing = 1,
                U0 = offset, U1 = b.U, L0 = lat, Lo = lo, Hi = hi,
                P0 = C, P1 = C - dur, P2 = dur, Vmax = vmax, Acc = acc,
                Cu = b.U - math.clamp(c.FrontAt(t0 + 0.5 * C) + offset, lo, hi), Cl = b.Lat - lat,
            };
            l.CatchDur = math.max(HopCatchDur(l.Cu, C, b.Lim), HopCatchDur(l.Cl, C, b.Lim));
            if (l.CatchDur <= 0f) { l.Cu = 0f; l.Cl = 0f; }
            b.Add(l);
            CountOverWork(p, c, c.FrontSpeed);
            Commit(p, ref b, c, double.PositiveInfinity);
        }

        // ------------------------------------------------------------ excavator <-> grader guard

        // Separation along the chain that keeps two boxes apart whatever their facing (box offsets included).
        public static float SepU(Puppet a, Puppet b) =>
            a.BoxHalfLen + math.abs(a.BoxOffZ) + b.BoxHalfLen + math.abs(b.BoxOffZ) + 2f * MxConst.kOverlapMargin + 0.5f;

        public static float SepLat(Puppet a, Puppet b) => a.BoxHalfWid + b.BoxHalfWid + 2f * MxConst.kOverlapMargin + 0.2f;

        // First time in [from, to] where plan A of puppet a and the CURRENT plan of b put their boxes on top of each other.
        // aCommitted = planA is a's committed plan (the guard's periodic check): both sides come from the sample caches.
        // Steps where the boxes are farther apart than they can close (SkipSteps) are skipped (no box evaluation, no cache fill).
        public static bool PairConflict(Puppet a, in MachinePlan planA, Puppet b, PlanContext c, double from, double to, out double tc, bool aCommitted = false) =>
            PairConflict(a, planA, b, c, from, to, out tc, aCommitted, NoDeadline, out _);

        // With a Stopwatch deadline (checked every few steps): `aborted` = the check did not finish (no
        // result; the caller retries in a later update).
        public static bool PairConflict(Puppet a, in MachinePlan planA, Puppet b, PlanContext c, double from, double to, out double tc, bool aCommitted,
                                        long deadline, out bool aborted)
        {
            tc = double.PositiveInfinity;
            aborted = false;
            if (b == null || b.Plan.Count <= 0 || planA.Count <= 0) return false;
            MxPerf.Begin(MxT.X_PairConflict);
            bool hit = false;
            int steps = 0;
            // b's committed plan comes from its sample cache (absolute kSampleStep grid)
            PlanSamples.Validate(b, c.Clk, c.Now);
            if (aCommitted) PlanSamples.Validate(a, c.Clk, c.Now);
            float vRel = VBound(a) + VBound(b);
            float m = MxConst.kOverlapMargin;
            long k1 = PlanSamples.Ceil(to);
            for (long k = PlanSamples.Floor(from); k <= k1;)
            {
                if (deadline != NoDeadline && ++steps % 4 == 0 && System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                {
                    aborted = true;
                    MxPerf.Count(MxC.ScanAborted);
                    break;
                }
                MxPerf.Count(MxC.PcSteps);
                double t = PlanSamples.Time(k);
                Obb oa;
                bool okA = aCommitted ? PlanSamples.BoxAt(a, k, c, out oa, out _) : BoxAt(a, planA, c, t, out oa, out _);
                if (!okA || !PlanSamples.BoxAt(b, k, c, out var ob, out _)) { k++; continue; }
                oa.HL += m; oa.HW += m;
                ob.HL += m; ob.HW += m;
                if (Overlap(oa, ob)) { tc = t; hit = true; break; }
                k += math.max(1L, SkipSteps(oa, ob, 0f, vRel));
            }
            MxPerf.End(MxT.X_PairConflict);
            return hit;
        }

        // The fastest a puppet's box can move in the world (planner skip-ahead): its fastest limit x kSkipSpeedFactor
        // (curves move an outer lateral faster than the chain, lane merges add their lateral part) + 0.5 m/s.
        public static float VBound(Puppet p)
        {
            var l = p.Lim;
            float v = math.max(math.max(l.VFwd, l.VRev), math.max(l.VTurn, l.VLeave));
            if (!(v > 0.05f)) v = 15f;
            return MxConst.kSkipSpeedFactor * v + 0.5f;
        }

        // Grid steps (kSampleStep) during which two boxes (margins already added, + extra) cannot touch: their centres are
        // farther apart than both circumscribed radii plus the distance they can close at vRel. 0 = test the next step.
        public static long SkipSteps(in Obb a, in Obb b, float extra, float vRel)
        {
            float ra = math.sqrt((a.HL + extra) * (a.HL + extra) + (a.HW + extra) * (a.HW + extra));
            float rb = math.sqrt((b.HL + extra) * (b.HL + extra) + (b.HW + extra) * (b.HW + extra));
            float gap = math.distance(a.C, b.C) - ra - rb - 1f;   // 1 m slack: a chassis / footprint box (PairHit) centred off C
            if (!(gap > 0f) || !(vRel > 0f)) return 0L;
            return (long)math.min(4096f, math.floor(gap / (vRel * PlanSamples.Step)));
        }

        // The grader stands aside for the digger: the first candidate whose path + hold never meets the digger's plan within
        // the horizon. Candidates: a clear verge at its own u (left, right), behind the digger's whole sweep, ahead of it.
        // The digger only moves forward (DigHop anchors are monotonic), so "behind" stays valid for good. When no candidate is
        // conflict-free the least bad one (latest first contact) is committed anyway.
        // ONE candidate per update (PlanStatic + a 45 s PairConflict each, four of them used to run in one
        // update on top of the guard check). YieldBegin fixes the search inputs; YieldStep evaluates the next candidate inside the
        // per-update check budget and returns true once the yield is decided (committed: clean = a conflict-free one). The grader keeps
        // its running plan for the few updates the search takes (the contact is seconds ahead; the separation guard still brakes).
        public static void YieldBegin(Puppet g, Puppet e, PlanContext c, double tc)
        {
            double now = c.Now, to = now + MxConst.kGuardHorizon;
            MachineMotion.State(g.Plan, c.Clk, now, out var gs);
            MachineMotion.State(e.Plan, c.Clk, now, out var es);
            float eMin = es.U, eMax = es.U;
            PlanSamples.Validate(e, c.Clk, now);
            long kEnd = PlanSamples.Ceil(to);
            for (long k = PlanSamples.Ceil(now); k <= kEnd; k += 4)
            {
                PlanSamples.BoxAt(e, k, c, out _, out var x);
                eMin = math.min(eMin, x.U); eMax = math.max(eMax, x.U);
            }
            g.YieldK = 0;
            g.YieldTc = tc;
            g.YieldEMin = eMin; g.YieldEMax = eMax;
            g.YieldGU = gs.U; g.YieldGLat = gs.Lat; g.YieldELat = es.Lat;
            g.YieldFacing = gs.Hu >= 0f ? (sbyte)1 : (sbyte)-1;
            g.YieldBestT = double.NegativeInfinity;
            g.YieldBestHow = "none";
        }

        public static bool YieldStep(Puppet g, Puppet e, PlanContext c, long deadline, out bool clean, out string how)
        {
            MxPerf.Begin(MxT.X_PlanYield);
            bool done = YieldStepCore(g, e, c, deadline, out clean, out how);
            MxPerf.End(MxT.X_PlanYield);
            return done;
        }

        static bool YieldStepCore(Puppet g, Puppet e, PlanContext c, long deadline, out bool clean, out string how)
        {
            clean = false;
            how = "none";
            double now = c.Now, to = now + MxConst.kGuardHorizon;
            float sep = SepU(g, e), sepLat = SepLat(g, e);
            float lo = AnchorLo(c, g), hi = AnchorHi(c, g);
            for (; g.YieldK < 4; g.YieldK++)
            {
                int k = g.YieldK;
                float u = g.YieldGU, lat = g.YieldGLat;
                string name;
                if (k < 2)
                {
                    if (c.Visible) continue;   // no verge on a visible road
                    lat = LatOf(c, k == 0 ? -1f : 1f, u, g);
                    if (math.abs(lat - g.YieldELat) < sepLat || !VergeClear(c, g, ClampU(c, g, u), lat)) continue;
                    u = ClampU(c, g, u);
                    name = k == 0 ? "left verge" : "right verge";
                }
                else if (k == 2)
                {
                    u = g.YieldEMin - sep;
                    if (u < lo) continue;
                    name = "behind";
                }
                else
                {
                    u = g.YieldEMax + sep;
                    if (u > hi) continue;
                    name = "ahead";
                }
                MxPerf.Count(MxC.YieldSteps);
                var saved = g.Plan;
                PlanStatic(g, c, u, lat, g.YieldFacing, false, AnimKind.Rest);
                var cand = g.Plan;
                g.Plan = saved;
                bool hit = PairConflict(g, cand, e, c, now, to, out double tc, false, deadline, out bool aborted);
                if (aborted) { g.CheckAborts++; return false; }   // the same candidate again next update
                g.CheckAborts = 0;
                if (!hit)
                {
                    CommitYield(g, c, cand);
                    how = name;
                    clean = true;
                    g.YieldK = -1;
                    return true;
                }
                if (tc > g.YieldBestT)
                {
                    g.YieldBestT = tc; g.YieldBestU = u; g.YieldBestLat = lat;
                    g.YieldBestHow = name + " (contact in " + RRWLog.F((float)(tc - now)) + " s)";
                }
                g.YieldK++;
                if (g.YieldK < 4) return false;            // next candidate next update
                break;
            }
            g.YieldK = -1;
            if (double.IsNegativeInfinity(g.YieldBestT))
            {
                // nothing reachable: stop where the current leg ends
                PlanIdle(g, c);
                how = "idle (no slot)";
                g.ReplanAt = now + MxConst.kGuardYieldHold;
                return true;
            }
            // the least bad candidate (rebuilt now: the stored one was planned from an earlier update's state)
            var keep = g.Plan;
            PlanStatic(g, c, g.YieldBestU, g.YieldBestLat, g.YieldFacing, false, AnimKind.Rest);
            var best = g.Plan;
            g.Plan = keep;
            CommitYield(g, c, best);
            how = g.YieldBestHow;
            return true;
        }

        static void CommitYield(Puppet g, PlanContext c, in MachinePlan plan)
        {
            g.Plan = plan;
            g.PlanDirty = true;
            g.ReplanAt = c.Now + MxConst.kGuardYieldHold;
            g.PlannedPhase = c.View.Phase;
            g.PlannedWorking = c.Working;
            g.PlannedTrackRevision = c.Track != null ? c.Track.Revision : -1;
        }

        // ------------------------------------------------------------ parking (drive-out / post-site)

        // Final resting box of another puppet: where its open-ended Hold stands, else where it is now.
        static bool RestBox(Puppet o, PlanContext c, out Obb box)
        {
            double t = c.Now;
            if (o.Plan.Count > 0)
            {
                var last = o.Plan.Get(o.Plan.Count - 1);
                if (last.Kind == (byte)LegKind.Hold && last.OpenEnded) t = math.max(c.Now, o.Plan.Epoch + last.T0);
            }
            return Box(o, o.Plan, c, t, out box);
        }

        // Box of p standing at (u, lat), facing-agnostic (the box offset is folded into the half length).
        static bool StandBox(Puppet p, PlanContext c, float u, float lat, out Obb box)
        {
            box = default;
            if (c.Track == null || !c.Tv.Point(c.Track.Slot, u, out var tp)) return false;
            float2 f = tp.Dir;
            box.F = math.normalizesafe(f, new float2(0f, 1f));
            box.R = new float2(box.F.y, -box.F.x);
            box.C = tp.Pos.xz + tp.Right * lat;
            box.HL = p.BoxHalfLen + math.abs(p.BoxOffZ) + MxConst.kOverlapMargin;
            box.HW = p.BoxHalfWid + MxConst.kOverlapMargin;
            return true;
        }

        public static bool SlotFree(Puppet p, PlanContext c, float u, float lat)
        {
            MxPerf.Count(MxC.SlotFreeTests);
            if (!StandBox(p, c, u, lat, out var a)) return false;
            for (int i = 0; i < c.Others.Count; i++)
            {
                var o = c.Others[i];
                if (o == p || o.Plan.Count <= 0 || !Live(o, c)) continue;
                if (RestBox(o, c, out var b) && Overlap(a, b)) return false;
            }
            return true;
        }

        // A spawn / stand box at (u, lat) that is clear of every other puppet's box now AND its resting box.
        public static bool SpotFree(Puppet p, PlanContext c, float u, float lat)
        {
            MxPerf.Count(MxC.SlotFreeTests);
            if (!StandBox(p, c, u, lat, out var a)) return false;
            for (int i = 0; i < c.Others.Count; i++)
            {
                var o = c.Others[i];
                if (o == p || o.Plan.Count <= 0 || !Live(o, c)) continue;
                if (RestBox(o, c, out var b) && Overlap(a, b)) return false;
                if (Box(o, o.Plan, c, c.Now, out var bn) && Overlap(a, bn)) return false;
            }
            return true;
        }

        // Drive-out / post-site parking slot: searched inward from the chain end in kParkStep steps; at each u the left verge,
        // the right verge (both validated: no trees / buildings / other roads, no steep step), then the left and right floor
        // lanes; the first slot whose box is clear of every other puppet's resting box (this project + nearby projects) wins.
        // The old fixed "Trim1 - 6 - 11 * role" slots were closer than two machine lengths (13.5 m excavator boxes) once
        // ClampU pulled them in, and VergeSlot slid each one independently onto the same floor lane.
        public static bool ParkSlot(PlanContext c, Puppet p, out float u, out float lat, out string where)
        {
            float start = ClampU(c, p, ShiftShared(c, c.Trim1 - 6f));
            float lo = AnchorLo(c, p);
            float u0 = start, l0 = LatOf(c, -0.45f, start, p);
            for (int i = 0; i < MxConst.kParkTries; i++)
            {
                float uu = start - i * MxConst.kParkStep;
                if (uu < lo - 0.01f) break;   // reached the start barrier
                for (int k = 0; k < 4; k++)
                {
                    if (k < 2 && c.Visible) continue;   // no verge on a visible road (the sidewalks may be open)
                    float ll = k < 2 ? LatOf(c, k == 0 ? -1f : 1f, uu, p) : LatOf(c, k == 2 ? -0.45f : 0.45f, uu, p);
                    if (!SlotFree(p, c, uu, ll)) continue;                     // cheap box test first
                    if (k < 2 && !VergeClear(c, p, uu, ll)) continue;         // then the search trees
                    u = uu; lat = ll;
                    where = (k == 0 ? "left verge" : k == 1 ? "right verge" : k == 2 ? "left floor" : "right floor") + " slot " + i;
                    return true;
                }
            }
            u = u0; lat = l0;
            where = "no free slot (left floor at the end)";
            return false;
        }

        // ------------------------------------------------------------ trucks

        public static TruckCfg TruckConfig(PlanContext c, Puppet p, MachineRole role, CrewSlot slot)
        {
            var cfg = new TruckCfg { Load = slot.Load };
            var ph = c.View.Phase;
            bool loadPhase = ph == WorksPhase.Excavation || ph == WorksPhase.BreakUp || ph == WorksPhase.Removal;
            cfg.Mode = loadPhase ? 1 : ph == WorksPhase.Paving ? 3 : 2;
            cfg.Dir = loadPhase ? (sbyte)1 : (sbyte)-1;
            cfg.Wait = cfg.Mode == 1 ? MxConst.kTruckTripWait : MxConst.kDumpTripWait;
            float baseA = ClampU(c, p, ShiftShared(c, c.Crew.BaseSlotU));
            float baseB = ClampU(c, p, ShiftShared(c, c.Crew.BaseSlotUB + cfg.Dir * 6f));
            bool isB = role == MachineRole.TruckB;
            cfg.BaseU = isB ? baseB : baseA;
            cfg.BaseLat = LatOf(c, isB ? c.Crew.BaseLateralB : c.Crew.BaseLateralA, cfg.BaseU, p);
            cfg.WorkLat = cfg.Mode == 3 ? LatOf(c, 0f, cfg.BaseU, p) : LatOf(c, PhasePlan.kRightLane, cfg.BaseU, p);
            // the base turn station stays near the crew's section
            float tlo = TurnLo(c), thi = TurnHi(c);
            if (c.Crews > 1) { tlo = math.max(tlo, c.SecLo - MxConst.kSecSlack); thi = math.max(tlo, math.min(thi, c.SecHi + MxConst.kSecSlack)); }
            cfg.TurnStation = math.clamp(cfg.Dir > 0 ? math.max(baseA, baseB) + MxConst.kTurnStation : math.min(baseA, baseB) - MxConst.kTurnStation, tlo, thi);
            cfg.LoadOffset = -1.5f;
            if (cfg.Mode == 1 && !c.Narrow && !c.Visible)
            {
                // Beside the digger the truck clears its CHASSIS by the planner margins (it used to overlap it
                // by up to 0.5 m on 12 m roads - hidden by the old blanket loading exemption)
                float F = c.FrontAt(c.Now);
                var smp = TrackBuilder.At(c.Track, F);
                float flat = smp.FlatHalf > 0f ? smp.FlatHalf : 4f;
                var e = c.CS != null ? c.CS.Roles[(int)MachineRole.Excavator] : null;
                float exHw = e != null ? e.BoxHalfWid * 0.85f : 2.35f;
                float exLat;
                if (e != null && e.Plan.Count > 0) exLat = e.Plan.Get(e.Plan.Count - 1).L0;
                else exLat = -math.min(0.7f * flat, math.max(0f, flat - exHw - 0.3f));
                float need = exLat + exHw + p.BoxHalfWid + 2f * MxConst.kOverlapMargin + 0.05f;
                float maxLat = math.max(0f, flat - p.BoxHalfWid - 0.1f);
                cfg.WorkLat = math.clamp(math.max(cfg.WorkLat, need), -maxLat, maxLat);
            }
            if (c.Narrow)
            {
                cfg.WorkLat = LatOf(c, 0f, cfg.BaseU, p);
                cfg.BaseLat = cfg.WorkLat;
                // inline behind the excavator (it slews 180). Clear of its TRUE body box (InlineLoadOffset), not 3.5 m
                cfg.LoadOffset = InlineLoadOffset(c, p);
            }
            // C1: while the floor step behind the current edge is > 0.25 m, trucks wait on the current edge
            if (ph == WorksPhase.Excavation && c.Track != null)
            {
                float F = c.FrontAt(c.Now);
                float step = TrackBuilder.StepBehind(c.Track, F, out float edgeLo);
                if (step > 0.25f)
                {
                    float roll = math.max(edgeLo + 12f, F - 40f) + (isB ? 12f : 0f);
                    cfg.BaseU = ClampU(c, p, math.max(cfg.BaseU, roll));
                }
            }
            if (cfg.Mode == 1 && isB && !c.Narrow && c.Track != null && InWorkPath(c, p, cfg.BaseU, cfg.BaseLat) != null)
            {
                // TruckB's left-lane base lies in the excavator's lane ahead of it (start of C1 / D1, after a jump, a re-latch):
                // the excavator would dig (or drive to its front) into the waiting truck. It waits in the right lane at the same u when
                // that is clear of every resting box (TruckA's base) and of every working path; else the guard's make-way moves it.
                float latR = LatOf(c, PhasePlan.kRightLane, cfg.BaseU, p);
                if (InWorkPath(c, p, cfg.BaseU, latR) == null && SlotFree(p, c, cfg.BaseU, latR))
                {
                    cfg.BaseLat = latR;
                    cfg.LaneSwapped = true;
                }
            }
            return cfg;
        }

        // Narrow sites: the waiting point rolls with the work (reverse inline <= 35 m).
        public static float RollingBase(PlanContext c, Puppet p, in TruckCfg cfg, float stop) =>
            ClampU(c, p, stop - cfg.Dir * (RRWConst.kMaxReverseLeg - 2f));

        public struct Route
        {
            public float Stop;
            public double Arrive, WorkStart, WorkEnd, Tk;
            // Inline loading: no room behind the excavator within kInlineRoomCycles / the first cycle with room lies this
            // many s after the one the arrival reaches (the caller delays the departure instead of committing)
            public bool NoRoom;
            public double RoomDelay;
        }

        // Approach from the base to the stop: reverse straight in when within 35 m, else drive forward past the stop's
        // turn point (stop - 25 m), K-turn and reverse <= 31 m.
        static void Approach(ref LegBuilder b, Puppet p, PlanContext c, in TruckCfg cfg, float stop, float workLat)
        {
            sbyte dir = cfg.Dir;
            float d = (stop - b.U) * dir;
            if (!c.CanTurn) { DriveNoTurn(ref b, p, c, stop, workLat); return; }   // also when facing the work (no K-turn)
            if (b.Facing == -dir)
            {
                if (d <= RRWConst.kMaxReverseLeg || !c.CanTurn) { Drive(ref b, p, stop, workLat); return; }
                float rev = math.min(12f, math.max(4f * math.abs(b.Lat) + 2f, 6f));
                Drive(ref b, p, math.clamp(b.U + dir * rev, TurnLo(c), TurnHi(c)), 0f);
                Turn(ref b, p, 1f);
            }
            if (b.Facing == dir)
            {
                float tp = stop - dir * 25f;
                float minAhead = 4f * math.abs(b.Lat) + 2f;
                if ((tp - b.U) * dir < minAhead) tp = b.U + dir * minAhead;
                tp = math.clamp(tp, TurnLo(c), TurnHi(c));
                Drive(ref b, p, tp, 0f);
                Turn(ref b, p, -1f);
            }
            Drive(ref b, p, stop, workLat);
        }

        // Without K-turns a truck reverses to its stop (rolling base). No reverse leg is longer than kMaxReverseLeg - a
        // longer one (the stop moved on while the truck waited, a base outside its crew's range) runs in chunks of <= kLeaveReverseChunk
        // with a short pause, as far as the leg budget allows (the work legs keep 4 legs); the last chunk merges to the work lane.
        static void DriveNoTurn(ref LegBuilder b, Puppet p, PlanContext c, float stop, float lat)
        {
            float d = stop - b.U;
            sbyte mv = d >= 0f ? (sbyte)1 : (sbyte)-1;
            float dist = math.abs(d);
            if (mv != b.Facing && dist > RRWConst.kMaxReverseLeg && !p.IsRoller)
            {
                int chunks = (int)math.ceil(dist / MxConst.kLeaveReverseChunk);
                if (b.Plan.Count + 2 * chunks + 4 <= MachinePlan.MaxLegs)
                {
                    float step = dist / chunks;
                    for (int i = 0; i < chunks - 1; i++)
                    {
                        Drive(ref b, p, b.U + mv * step, b.Lat);
                        Hold(ref b, b.T + MxConst.kLeaveReversePause, AnimKind.Rest);
                    }
                }
            }
            Drive(ref b, p, stop, lat);
        }

        // Outbound half of a truck cycle: (hold at base) -> approach -> work. depart = earliest departure (machine time).
        public static void PlanTruckOutbound(Puppet p, PlanContext c, in TruckCfg cfg0, double departMin, Puppet other)
        {
            MxPerf.Begin(MxT.B_TruckOut);
            TruckOutbound(p, c, cfg0, departMin, other);
            MxPerf.End(MxT.B_TruckOut);
        }

        // ONE attempt per truck per update, inside the global planning budget; a conflicting attempt
        // moves the departure and the next update tries again (kOverlapTries in total, spread over updates). Meanwhile the truck
        // holds its current leg end (an intended wait, not a stand-still: it never counts as stuck).
        // The attempt's conflict scan is resumable (MachineScan.cs): it runs only while the planning
        // budget lasts (deadline checked inside the scan) and continues in the next updates; the candidate is committed when the scan
        // comes back clear, the truck keeps its committed plan meanwhile.
        static void TruckOutbound(Puppet p, PlanContext c, in TruckCfg cfg0, double departMin, Puppet other)
        {
            var pd = p.Pend;
            if (p.OutAttempt < 0)
            {
                p.OutAttempt = 0;
                p.OutDepart = math.max(departMin, c.Now);
                if (pd.For == ScanFor.TruckOut) pd.Drop();
            }
            if (MxBudget.Exhausted) { MxBudget.Defer(); return; }
            long t0 = MxBudget.Start();
            try
            {
                if (pd.Holds(ScanFor.TruckOut, p.OutAttempt))
                {
                    // a candidate planned on another dig grid phase (the excavator re-phased meanwhile) is rebuilt
                    if ((c.CS != null && pd.GridEpoch != c.CS.GridEpoch) || !pd.BaseIs(p.Plan)) pd.Drop();
                    else if (c.Now <= pd.CommitBy) { OutboundScan(p, c, t0, other); return; }
                    else ScanExpired(p);
                }
                OutboundAttempt(p, c, cfg0, other, t0);
            }
            finally { MxBudget.Stop(t0); }
        }

        static void OutboundAttempt(Puppet p, PlanContext c, in TruckCfg cfg0, Puppet other, long t0)
        {
            var cfg = cfg0;
            int attempt = p.OutAttempt;
            // (a departure lead after a scan outlived its candidate: the candidate holds where the truck stands meanwhile)
            double depart = math.max(p.OutDepart, c.Now + p.ScanLead);
            bool waiter = false;
            double feederEnd = 0;
            if (cfg.Mode == 3 && other != null && other.Feeding && other.WorkEnd > c.Now + 5.0) { waiter = true; feederEnd = other.WorkEnd; }
            MxPerf.Count(MxC.TruckOutAttempts);
            var b = Begin(p, c);
            if (b.Abs(b.T) < depart) Hold(ref b, b.Rel(depart), AnimKind.Load);
            var r = BuildOutbound(ref b, p, c, cfg, waiter, feederEnd);
            if (r.NoRoom)
            {
                // Inline loading: no cycle within kInlineRoomCycles leaves room behind the excavator (it stands at its clamp
                // at the section start): the truck keeps waiting where it is; the outbound is tried again in 10 s
                p.OutAttempt = -1;
                p.ReplanAt = c.Now + 10.0;
                MachineDebug.InlineWaits++;
                return;
            }
            if (r.RoomDelay > 0.5)
            {
                // ... the first cycle with room comes later: depart in time for it (the next attempt, no attempt used)
                p.OutDepart = depart + r.RoomDelay;
                if (r.RoomDelay > 5.0) MachineDebug.InlineWaits++;
                return;
            }
            // one truck in the front zone at a time (LoadAtFront / DumpAtFront phases)
            double zs = waiter ? 0.0 : ZoneShift(c, r, other);
            if (zs > 0.05 && attempt < MxConst.kOverlapTries) { p.OutDepart = depart + zs; p.OutAttempt = attempt + 1; return; }
            // The WHOLE plan is checked - the hold before departure, the route, the work legs - and a plan with a
            // known conflict is never committed
            double from = c.Now, to = math.min(MachineMotion.MotionEnd(b.Plan) + 1.0, c.Now + MxConst.kPlanCheckMax);
            var pd = p.Pend;
            pd.For = ScanFor.TruckOut;
            pd.Attempt = attempt;
            pd.B = b;
            pd.R = r;
            pd.Cfg = cfg;
            pd.Depart = depart;
            pd.Waiter = waiter;
            pd.GridEpoch = c.CS != null ? c.CS.GridEpoch : -1;
            pd.CommitBy = CommitBy(b.Plan, p.Plan);
            pd.SetBase(p.Plan);
            ScanStart(pd.Scan, p, b.Plan, c, from, to, MxConst.kOverlapMargin, null);
            OutboundScan(p, c, t0, other);
        }

        // Rule 1 (front zone, one truck at a time): the departure shift a route with times r needs against the other truck (0 = none).
        static double ZoneShift(PlanContext c, in Route r, Puppet other)
        {
            if (other == null || !other.Alive(c.Em) || !(other.ZoneExit > c.Now)) return 0.0;
            double enter = r.Arrive - 10.0;
            if (enter < other.ZoneExit + MxConst.kZoneMargin && r.WorkEnd + 8.0 > other.ZoneEnter)
                return math.max(0.0, other.ZoneExit + MxConst.kZoneMargin - enter);
            return 0.0;
        }

        // Runs the pending outbound scan inside the planning budget; commits / delays / stands still once it has finished.
        static void OutboundScan(Puppet p, PlanContext c, long t0, Puppet other)
        {
            var pd = p.Pend;
            var s = pd.Scan;
            var st = ScanRun(s, c, MxBudget.Deadline(t0));
            if (st == ScanState.Running) return;   // continues next update (the truck keeps its committed plan)
            int attempt = pd.Attempt;
            pd.For = ScanFor.None;
            if (st == ScanState.Conflict)
            {
                if (attempt < MxConst.kOverlapTries)
                {
                    if (c.Verbose) RRWLog.Verbose("machines: " + p + " outbound conflict with " + s.Who + " at +" + RRWLog.F((float)(s.Tc - c.Now)) + "s for " + RRWLog.F((float)s.Dur) + "s: delaying");
                    p.OutDepart = pd.Depart + s.Dur + MxConst.kZoneMargin;
                    p.OutAttempt = attempt + 1;
                    return;
                }
                p.OutAttempt = -1;
                p.Wanted = pd.B.Plan; p.WantedTau = c.Now;   // the trip it wanted (make-way path)
                StandStill(p, c, "outbound", s.Who, s.Tc);
                return;
            }
            var r = pd.R;
            var cfg = pd.Cfg;
            // the other truck may have committed its trip while this scan ran: one-truck front zone again (a shift is the next attempt)
            double zs = pd.Waiter ? 0.0 : ZoneShift(c, r, other);
            if (zs > 0.05 && attempt < MxConst.kOverlapTries) { p.OutDepart = pd.Depart + zs; p.OutAttempt = attempt + 1; pd.Drop(); return; }
            p.OutAttempt = -1;
            p.ScanLead = 0f;
            ClearBlocked(p);
            p.Stage = TruckStage.Outbound;
            p.GridEpoch = c.CS != null ? c.CS.GridEpoch : -1;   // the dig grid phase this trip is planned on
            p.WorkStart = r.WorkStart;
            p.WorkEnd = r.WorkEnd;
            p.ZoneEnter = r.Arrive - 10.0;
            p.ZoneExit = r.WorkEnd + 8.0;
            p.Feeding = cfg.Mode == 3;
            // load steps
            p.Loads.Clear();
            p.EventLoads = false;
            if (cfg.Mode == 1)
            {
                var cs = c.CS;
                p.Buckets = 0;
                if (cs != null && cs.DigIk && math.abs(cs.DigC - RRWConst.kDigCyclePeriod) < 0.01f)
                    p.EventLoads = true;   // +25 % per Dump event that targets this truck (MachineDigEvents)
                else
                {
                    float C = cs != null && cs.DigC > 0f ? cs.DigC : RRWConst.kDigCycleSeconds;
                    for (int i = 0; i < 4; i++) p.Loads.Add(new LoadStep { Tau = r.Tk + 0.75 * C + i * C, Pct = 25 * (i + 1), Kind = cfg.Load });
                }
            }
            else
            {
                double s0 = r.WorkStart, dd = r.WorkEnd - r.WorkStart;
                for (int i = 1; i <= 4; i++) p.Loads.Add(new LoadStep { Tau = s0 + dd * i / 4.0, Pct = 100 - 25 * i, Kind = cfg.Load });
            }
            RefreshModels(ref pd.B.Plan, pd, p.Plan);
            Commit(p, ref pd.B, c, r.WorkEnd);
            pd.Drop();
        }

        static Route BuildOutbound(ref LegBuilder b, Puppet p, PlanContext c, in TruckCfg cfg, bool waiter, double feederEnd)
        {
            var r = new Route();
            var st = c.CS;   // the crew's dig grid
            // the cycle the excavator's grid runs (IK schedule 20 s or the 10 s keyframe cycle)
            float C = st.DigC > 0f ? st.DigC : c.View.Phase == WorksPhase.BreakUp ? MxConst.kBreakCycle : RRWConst.kDigCycleSeconds;
            if (cfg.Mode == 1 && !(st.DigVmax > 0f))
                DigGrid(c, st, MachineLimits.Of(MachineRole.Excavator, c.View.Phase), C, out _);
            sbyte workFacing = (sbyte)-cfg.Dir;
            double arrive = b.Abs(b.T) + math.abs(b.U - c.FrontAt(c.Now)) / math.max(1f, p.Speed) + 15.0;
            float stop = b.U;
            LegBuilder probe = b;
            bool inline = cfg.Mode == 1 && cfg.LoadOffset < -2f;   // narrow sites load inline behind the excavator
            double stopAt = arrive;
            for (int it = 0; it < 4; it++)
            {
                if (cfg.Mode == 1)
                {
                    if (double.IsNaN(st.DigOrigin)) st.DigOrigin = c.Now;
                    double k = math.ceil((arrive - st.DigOrigin) / C);
                    r.Tk = st.DigOrigin + k * C;
                    if (inline)
                    {
                        // Breaker and truck must not stack at lat 0 (seen in D0): first room behind the excavator - a stop clamped up to the
                        // truck's AnchorLo while the excavator still stands at its own clamp (crew 0, front behind the junction trim) put the
                        // truck inside it - then a stop clear of where the excavator STANDS on arrival: it hops forward at the end of every
                        // cycle, so from the arrival on it never stands behind its anchor half a cycle before (a truck that arrived a cycle
                        // early used to stand inside its body until Tk)
                        double room = LoadRoomFrom(c, p, cfg, r.Tk, C);
                        if (double.IsNaN(room)) { r.NoRoom = true; break; }
                        if (room > r.Tk + 1e-3) { r.RoomDelay = room - r.Tk; r.Tk = room; }
                        stopAt = math.min(arrive, r.Tk);
                        stop = DigAnchor(c, st, stopAt - 0.5 * C) + cfg.LoadOffset;
                    }
                    else stop = DigAnchor(c, st, r.Tk + 0.5 * C) + cfg.LoadOffset;
                }
                else if (cfg.Mode == 2)
                {
                    float F = c.FrontAt(arrive);
                    var v = c.View;
                    v.F = FractionOf(c, arrive);
                    float slot = PhasePlan.ActiveDumpSlot(v, c.CrewIndex);   // this crew's heap
                    if (float.IsNaN(slot)) slot = F + 6f;
                    stop = slot - cfg.Dir * p.BoxHalfLen;
                }
                else
                {
                    // Relative to the PAVER's anchor, which waits at its clamp at the section start (crew 0 of a trimmed road):
                    // both used to clamp to their own AnchorLo, the feed truck on top of the paver
                    float off = waiter ? 25f : RRWConst.kFinisherLead + 3.7f + 0.4f + 4.5f;
                    stop = math.max(c.FrontAt(arrive) + RRWConst.kFinisherLead, PaverLo(c)) + off - RRWConst.kFinisherLead;
                }
                stop = ClampU(c, p, stop);
                if (cfg.Mode == 3) stop = math.min(stop, SupportHi(c, p));   // out of the hand-off zone
                probe = b;
                float lat = waiter ? LatOf(c, PhasePlan.kRightLane, stop, p) : cfg.WorkLat;
                Approach(ref probe, p, c, cfg, stop, lat);
                double arr = probe.Abs(probe.T);
                if (cfg.Mode == 1)
                {
                    if (arr <= r.Tk - 0.2)
                    {
                        // Inline: arriving before the time its stop was taken for: take the stop for the actual arrival
                        if (inline && arr < stopAt - 0.05 && it < 3) { arrive = arr; continue; }
                        arrive = arr;
                        break;
                    }
                    arrive = arr + 0.5;
                }
                else
                {
                    if (math.abs(arr - arrive) < 1.0) { arrive = arr; break; }
                    arrive = arr;
                }
            }
            if (r.NoRoom || r.RoomDelay > 0.5) return r;   // the caller holds the truck at its base (no trip planned now)
            b = probe;
            r.Arrive = b.Abs(b.T);
            r.Stop = stop;
            if (!c.CanTurn) workFacing = b.Facing;   // no K-turn: work with the facing it arrived with (no 180 degree pose pop)
            switch (cfg.Mode)
            {
                case 1:
                {
                    if (r.Tk < r.Arrive) r.Tk = st.DigOrigin + math.ceil((r.Arrive - st.DigOrigin) / C) * C;
                    Hold(ref b, b.Rel(r.Tk), AnimKind.Load);
                    // With the IK schedule the truck leaves kTruckLeaveAfterShake after the shake that follows its
                    // kBucketsPerLoad-th dump (NthDumpAfter on the grid); its DigHop leg ends at rest (before that cycle's hop starts, else
                    // a short hold covers the rest). Keyframe cycle: four cycles.
                    double depart = r.Tk + 4.0 * C, legEnd = depart;
                    if (st.DigIk && math.abs(C - RRWConst.kDigCyclePeriod) < 0.01f)
                    {
                        var nom = DigSchedule.Nominal(c.View.Phase == WorksPhase.BreakUp ? DigMode.Break : DigMode.Dig);
                        var sc = st.DigSched.Period > 0f ? st.DigSched : nom;
                        // The 4th cycle's keys may be fitted differently from sc (another truck's / a spoil fit); the
                        // truck stands at least to the later of the two shakes, so the excavator's TruckTarget (and the dump) never
                        // misses it - at most kDigSwingNominal - kDigSwingMin longer than the fit needs
                        double want = sc.NthDumpAfter(r.Tk, st.DigOrigin, RRWConst.kBucketsPerLoad) + (math.max(sc.TShake, nom.TShake) - sc.DumpT) + RRWConst.kTruckLeaveAfterShake;
                        double lastCycle = r.Tk + (RRWConst.kBucketsPerLoad - 1) * (double)C;
                        double hopStart = lastCycle + math.max(0.0, C - st.DigHopDur);
                        if (hopStart >= lastCycle + sc.TShake - 1e-3)
                        {
                            // the shared hop of the last cycle starts after the 4th shake: stop hopping there (at rest), leave 1 s later
                            legEnd = math.min(want, hopStart);
                            depart = math.max(want, legEnd);
                        }
                        else legEnd = depart = r.Tk + 4.0 * C;   // a grown hop window: the bed stays under the bucket to the cycle end
                    }
                    // the truck hops with the excavator's trapezoid hop (same window, speed, lag line)
                    var l = new MachineLeg
                    {
                        T0 = b.Rel(r.Tk), T1 = b.Rel(legEnd), Kind = (byte)LegKind.DigHop, Anim = (byte)AnimKind.Load,
                        Facing = workFacing, U0 = st.DigOffset + cfg.LoadOffset, U1 = b.U, L0 = b.Lat,
                        Lo = st.DigLo + cfg.LoadOffset, Hi = math.max(st.DigLo, st.DigHi) + cfg.LoadOffset,
                        P0 = C, P1 = st.DigHopStart, P2 = st.DigHopDur, Vmax = st.DigVmax, Acc = st.DigAcc,
                    };
                    if (st.DigRate > 0f && !float.IsNaN(st.DigRateU)) { l.Rate = st.DigRate; l.RateU = st.DigRateU; }   // same slowed front
                    float A0 = DigAnchor(c, st, r.Tk + 0.5 * C) + cfg.LoadOffset;
                    l.Cu = b.U - A0;
                    l.CatchDur = HopCatchDur(l.Cu, C, b.Lim);
                    if (l.CatchDur <= 0f) l.Cu = 0f;
                    b.T = l.T0;
                    b.Add(l);
                    b.T = l.T1;
                    if (depart > legEnd + 1e-3)
                    {
                        // stand at the stop until the 4th bucket is shaken out (the excavator steps ahead meanwhile)
                        MachineMotion.State(b.Plan, c.Clk, b.Abs(b.T), out var se);
                        b.U = se.U; b.Lat = se.Lat; b.Odo = se.Odo; b.V = 0f;
                        Hold(ref b, b.Rel(depart), AnimKind.Load);
                    }
                    r.WorkStart = r.Arrive;
                    r.WorkEnd = depart;
                    break;
                }
                case 2:
                {
                    Hold(ref b, b.T + MxConst.kTipSeconds, AnimKind.Load);
                    r.WorkStart = r.Arrive;
                    r.WorkEnd = b.Abs(b.T);
                    break;
                }
                default:
                {
                    float feedOff = RRWConst.kFinisherLead + 3.7f + 0.4f + 4.5f;
                    // support roles of a non-last crew stay out of the boundary hand-off zone (SupportHi),
                    // and the feed (waiting) spot keeps its distance to the paver's clamped anchor
                    float lo = math.max(CrewLo(c, p), PaverLo(c) + feedOff - RRWConst.kFinisherLead), hi = SupportHi(c, p);
                    if (hi < lo) hi = lo;
                    bool supportClamped = hi < CrewHi(c, p) - 0.01f;
                    if (waiter)
                    {
                        // creep at F + 25 (right lane) until the feeder has left, then reverse-merge to the hopper
                        double until = math.max(b.Abs(b.T) + 2.0, feederEnd + 6.0);
                        float wl = b.Lat;
                        AddFollow(ref b, p, c, 25f, lo, hi, wl, 1, (float)(until - b.Abs(0)), AnimKind.Load);
                        double est = until;
                        float tgt = math.clamp(c.FrontAt(est) + feedOff, lo, hi);
                        for (int it = 0; it < 2; it++)
                        {
                            var pr = b;
                            Drive(ref pr, p, tgt, cfg.WorkLat);
                            tgt = math.clamp(c.FrontAt(pr.Abs(pr.T)) + feedOff, lo, hi);
                        }
                        Drive(ref b, p, tgt, cfg.WorkLat);
                    }
                    r.WorkStart = b.Abs(b.T);
                    float feedSeconds = MxConst.kFeedSeconds;
                    if (supportClamped)
                    {
                        // the feed ends before the hopper (F + feedOff) would pass the support bound - the paver
                        // finishes the last metres of its section from its hopper; the truck never stands still in its way
                        double t0 = r.WorkStart, t1 = t0 + MxConst.kFeedSeconds;
                        if (c.FrontAt(t0) + feedOff >= hi) feedSeconds = 0f;
                        else if (c.FrontAt(t1) + feedOff > hi)
                        {
                            for (int it = 0; it < 16; it++)
                            {
                                double tm = 0.5 * (t0 + t1);
                                if (c.FrontAt(tm) + feedOff > hi) t1 = tm; else t0 = tm;
                            }
                            feedSeconds = (float)(t0 - r.WorkStart);
                        }
                        if (feedSeconds < MxConst.kFeedSeconds) MxSpeed.FeedCutAtBoundary++;
                    }
                    if (feedSeconds >= 0.5f)
                    {
                        AddFollow(ref b, p, c, feedOff, lo, hi, cfg.WorkLat, workFacing, b.T + feedSeconds, AnimKind.Load);
                        Settle(ref b);   // the feed ends at rest (the plan never stops dead while creeping)
                    }
                    r.WorkEnd = b.Abs(b.T);
                    break;
                }
            }
            return r;
        }

        static void AddFollow(ref LegBuilder b, Puppet p, PlanContext c, float offset, float lo, float hi, float lat, sbyte facing, float until, AnimKind anim)
        {
            if (hi < lo) hi = lo;
            var l = new MachineLeg
            {
                T0 = b.T, T1 = math.max(b.T + 0.5f, until), Kind = (byte)LegKind.Follow, Anim = (byte)anim, Facing = facing,
                U0 = offset, U1 = b.U, L0 = lat, Lo = lo, Hi = hi, Acc = b.Lim.Accel,
            };
            FrontDynamics(ref l, ref b, p, c, default);   // limits, velocity match, catch-up within the role's limits
            if (!b.Add(l)) return;
            // end state: the leg evaluated at its end (anchor + catch-up + velocity terms), still moving with the front
            double te = b.Abs(l.T1);
            MachineMotion.State(b.Plan, c.Clk, te, out var se);
            MachineMotion.State(b.Plan, c.Clk, te - 0.05, out var sm);
            b.T = l.T1;
            b.U = se.U;
            b.Lat = se.Lat;
            b.Facing = facing;
            b.V = (se.U - sm.U) / 0.05f;
            if (math.abs(b.V) < 0.02f) b.V = 0f;
        }

        // Phase fraction f at a future machine time (for ActiveDumpSlot).
        static float FractionOf(PlanContext c, double tau)
        {
            var fr = c.Front;
            double frames = (tau - c.Clk.Tau0) * MachineMotion.kFps / math.max(1e-4f, c.Clk.Scale);
            double df = (double)unchecked((int)(c.Clk.Frame0 - fr.Model.Frame0)) + frames;
            double pr = fr.Model.P0 + fr.Model.PPerFrame * df;
            if (fr.PEnd <= fr.PStart + 1e-6f) return c.View.F;
            return math.saturate((float)((pr - fr.PStart) / (fr.PEnd - fr.PStart)));
        }

        // Return half: drive back to the base (close: forward straight in, parked facing away; far: via the turn station,
        // reversing into the base so the next trip starts forward). Then the base wait ("trip").
        // ONE attempt per update inside the planning budget (the truck stands at its work spot meanwhile).
        // Every return is time-sliced, the forced ones (phase / intent change, not working) too
        // (running up to kOverlapTries + 1 full-horizon checks inline for them caused frame spikes), and each attempt's conflict scan is resumable
        // inside the budget. Returns true when the return is decided (committed or stood still); false while it is pending (the
        // truck keeps its committed plan) or deferred by the budget.
        public static bool PlanTruckReturn(Puppet p, PlanContext c, in TruckCfg cfg, bool wait)
        {
            if (MxBudget.Exhausted) { MxBudget.Defer(); return false; }
            MxPerf.Begin(MxT.B_TruckReturn);
            long t0 = MxBudget.Start();
            bool done;
            try { done = TruckReturn(p, c, cfg, wait, t0); }
            finally { MxBudget.Stop(t0); }
            MxPerf.End(MxT.B_TruckReturn);
            return done;
        }

        static bool TruckReturn(Puppet p, PlanContext c, in TruckCfg cfg, bool wait, long t0)
        {
            var pd = p.Pend;
            if (p.RetAttempt < 0)
            {
                p.RetAttempt = 0;
                p.RetExtra = 0;
                if (pd.For == ScanFor.TruckRet) pd.Drop();
            }
            if (pd.Holds(ScanFor.TruckRet, p.RetAttempt))
            {
                if (pd.Wait != wait || !pd.BaseIs(p.Plan)) pd.Drop();
                else if (c.Now <= pd.CommitBy) return ReturnScan(p, c, t0);
                else ScanExpired(p);
            }
            float nextStop = WorkStopAt(c, cfg, c.Now + 90.0);   // C3 feed: the hopper of the (clamped) paver
            float baseU = cfg.BaseU, baseLat = cfg.BaseLat;
            // no K-turn (narrow floor, visible road): the waiting point rolls with the work so no reverse is longer than 35 m.
            // An inline truck's waiting point never lies closer to the excavator than the inline clearance
            if (c.Narrow || !c.CanTurn) { baseU = InlineBaseCap(c, p, cfg, RollingBase(c, p, cfg, ClampU(c, p, nextStop))); baseLat = cfg.WorkLat; }
            bool close = c.Narrow || !c.CanTurn || math.abs(nextStop - baseU) <= RRWConst.kMaxReverseLeg - 2f;
            MxPerf.Count(MxC.TruckReturnAttempts);
            var b = Begin(p, c);
            double extra = p.RetExtra + p.ScanLead;
            if (extra > 0) Hold(ref b, b.T + (float)extra, AnimKind.Load);
            if (close || b.Facing == cfg.Dir)
            {
                GoTo(ref b, p, c, baseU, baseLat, (sbyte)-cfg.Dir, false);
            }
            else
            {
                float ts = cfg.TurnStation;
                if ((b.U - ts) * cfg.Dir < 6f) ts = b.U - cfg.Dir * 6f;
                Drive(ref b, p, ts, 0f);
                Turn(ref b, p, 1f);
                Drive(ref b, p, baseU, baseLat);
            }
            // Checked from NOW (the extra hold at the work spot included - the waiting truck used to stand
            // unchecked at the hopper while the next one reversed into it) to the arrival + 5 s; never commit a conflict
            double to = math.min(b.Abs(b.T) + 5.0, c.Now + MxConst.kPlanCheckMax);
            pd.For = ScanFor.TruckRet;
            pd.Attempt = p.RetAttempt;
            pd.B = b;
            pd.Cfg = cfg;
            pd.Wait = wait;
            pd.CommitBy = CommitBy(b.Plan, p.Plan);
            pd.SetBase(p.Plan);
            ScanStart(pd.Scan, p, b.Plan, c, c.Now, to, MxConst.kOverlapMargin, null);
            return ReturnScan(p, c, t0);
        }

        static bool ReturnScan(Puppet p, PlanContext c, long t0)
        {
            var pd = p.Pend;
            var s = pd.Scan;
            var st = ScanRun(s, c, MxBudget.Deadline(t0));
            if (st == ScanState.Running) return false;   // continues next update
            int attempt = pd.Attempt;
            pd.For = ScanFor.None;
            if (st == ScanState.Conflict)
            {
                if (attempt < MxConst.kOverlapTries)
                {
                    p.RetExtra += s.Dur + MxConst.kZoneMargin;   // next attempt next update
                    p.RetAttempt = attempt + 1;
                    return false;
                }
                p.RetAttempt = -1;
                p.Wanted = pd.B.Plan; p.WantedTau = c.Now;   // the return it wanted (make-way path)
                StandStill(p, c, "return", s.Who, s.Tc);
                return true;
            }
            p.RetAttempt = -1;
            p.ScanLead = 0f;
            ClearBlocked(p);
            var cfg = pd.Cfg;
            if (cfg.LaneSwapped) MachineDebug.BaseLaneSwaps++;
            bool wait = pd.Wait;
            ref var best = ref pd.B;
            double arrive = best.Abs(best.T);
            Hold(ref best, float.PositiveInfinity, AnimKind.Load);
            p.Stage = TruckStage.Return;
            p.Feeding = false;
            p.ZoneEnter = p.ZoneExit = double.NegativeInfinity;
            p.WorkStart = p.WorkEnd = double.NegativeInfinity;
            p.Loads.Clear();
            double ready = arrive + (wait ? cfg.Wait : 1.0);
            int pct = cfg.Mode == 1 ? 0 : 100;   // loaders leave full and come back empty; dumpers come back empty and reload
            if (wait) p.Loads.Add(new LoadStep { Tau = ready - 0.5, Pct = pct, Kind = cfg.Load });
            RefreshModels(ref best.Plan, pd, p.Plan);
            Commit(p, ref best, c, wait ? ready : double.PositiveInfinity);
            if (!wait) p.ReplanAt = double.PositiveInfinity;
            pd.Drop();
            return true;
        }

        // ------------------------------------------------------------ overlap planner

        public struct Obb
        {
            public float2 C, F, R;      // centre, forward axis, right axis
            public float HL, HW;
            public float2 O;            // machine origin (pivot) - the chassis box of the excavator rig is centred here
        }

        // TRUE box (bounds, no margin) at tau, plus the chain state there.
        public static bool BoxAt(Puppet p, in MachinePlan plan, PlanContext c, double tau, out Obb o, out ChainState s)
        {
            o = default;
            s = default;
            if (plan.Count <= 0) return false;
            MxPerf.Count(MxC.BoxAt);
            MachineMotion.State(plan, c.Clk, tau, out s);
            // the box is 2D; Pose2D skips the four floor-height lookups of the full pose (same xz, same forward)
            if (!MachineMotion.Pose2D(plan, c.Tv, s, out float2 pos, out float2 f)) return false;
            o.F = f;
            o.R = new float2(f.y, -f.x);
            o.O = pos;
            o.C = pos + f * p.BoxOffZ;
            o.HL = p.BoxHalfLen;
            o.HW = p.BoxHalfWid;
            return true;
        }

        // Planner box: true box + kOverlapMargin.
        public static bool Box(Puppet p, in MachinePlan plan, PlanContext c, double tau, out Obb o)
        {
            if (!BoxAt(p, plan, c, tau, out o, out _)) return false;
            o.HL += MxConst.kOverlapMargin;
            o.HW += MxConst.kOverlapMargin;
            return true;
        }

        public static bool Overlap(in Obb a, in Obb b)
        {
            MxPerf.Count(MxC.ObbOverlap);
            float2 d = b.C - a.C;
            return !Sep(a.F, a, b, d) && !Sep(a.R, a, b, d) && !Sep(b.F, a, b, d) && !Sep(b.R, a, b, d);
        }

        // Clearance between two boxes (m): > 0 separated by at least that along some axis, <= 0 overlapping (depth).
        public static float Gap(in Obb a, in Obb b)
        {
            float2 d = b.C - a.C;
            float g = float.NegativeInfinity;
            g = math.max(g, AxisGap(a.F, a, b, d));
            g = math.max(g, AxisGap(a.R, a, b, d));
            g = math.max(g, AxisGap(b.F, a, b, d));
            g = math.max(g, AxisGap(b.R, a, b, d));
            return g;
        }

        static float AxisGap(float2 axis, in Obb a, in Obb b, float2 d)
        {
            float ra = a.HL * math.abs(math.dot(a.F, axis)) + a.HW * math.abs(math.dot(a.R, axis));
            float rb = b.HL * math.abs(math.dot(b.F, axis)) + b.HW * math.abs(math.dot(b.R, axis));
            return math.abs(math.dot(d, axis)) - ra - rb;
        }

        static bool Sep(float2 axis, in Obb a, in Obb b, float2 d)
        {
            float ra = a.HL * math.abs(math.dot(a.F, axis)) + a.HW * math.abs(math.dot(a.R, axis));
            float rb = b.HL * math.abs(math.dot(b.F, axis)) + b.HW * math.abs(math.dot(b.R, axis));
            return math.abs(math.dot(d, axis)) > ra + rb;
        }

        // ---- pair rules (instead of a blanket "LoadingPair" exemption; shared by planner + guard + rrw.mx.check)

        // The digger at work (Excavator ROLE of a live crew, digging / breaking): a truck of its own project is tested
        // against its CHASSIS only (the boom legitimately swings over the bed it loads). Never a parked / retired /
        // leaving excavator, never the grader, never another project's truck.
        public static bool DiggerAtWork(Puppet d) =>
            d.Role == MachineRole.Excavator && !d.PostSite && !d.Outgoing &&
            (d.Activity == MachineActivity.Dig || d.Activity == MachineActivity.Break);

        public static void Chassis(Puppet d, ref Obb o)
        {
            o.C = o.O;
            if (d.ExcavatorRig) { o.HL = d.BoxHalfLen * 0.5f; o.HW = d.BoxHalfWid * 0.85f; }
            else if (d.FootHalfLen > 0f && d.FootHalfWid > 0f) { o.C = o.O + o.F * d.FootOff; o.HL = d.FootHalfLen; o.HW = d.FootHalfWid; }
            else { o.HL = d.BoxHalfLen * 0.6f; o.HW = d.BoxHalfWid * 0.85f; }
        }

        // C3 feed truck in its hopper Follow leg <-> the paver (Finisher in its Follow leg): designed 0.4 m apart, so the
        // pair is tested with kFeedPairMargin (true contact only).
        static bool FeedPair(Puppet t, in ChainState st, Puppet f, in ChainState sf) =>
            t.IsTruck && f.Role == MachineRole.Finisher && !f.PostSite &&
            st.LegKind == (byte)LegKind.Follow && st.Anim == (byte)AnimKind.Load && sf.LegKind == (byte)LegKind.Follow;

        // True boxes oa / ob (BoxAt) of a and b at the same instant: do they collide with `margin` on each box?
        public static bool PairHit(Puppet a, in ChainState sa, Obb oa, Puppet b, in ChainState sb, Obb ob, float margin)
        {
            if (a.ProjectId == b.ProjectId)
            {
                if (b.IsTruck && DiggerAtWork(a)) Chassis(a, ref oa);
                else if (a.IsTruck && DiggerAtWork(b)) Chassis(b, ref ob);
                if (FeedPair(a, sa, b, sb) || FeedPair(b, sb, a, sa)) margin = math.min(margin, MxConst.kFeedPairMargin);
            }
            oa.HL += margin; oa.HW += margin;
            ob.HL += margin; ob.HW += margin;
            return Overlap(oa, ob);
        }

        // Pair test of two plans at tau.
        public static bool PairOverlapAt(Puppet a, in MachinePlan pa, Puppet b, in MachinePlan pb, PlanContext c, double tau, float margin)
        {
            if (!BoxAt(a, pa, c, tau, out var oa, out var sa) || !BoxAt(b, pb, c, tau, out var ob, out var sb)) return false;
            return PairHit(a, sa, oa, b, sb, ob, margin);
        }

        // (FindConflict is the resumable scan of MachineScan.cs)

        static float DistToSegment(float2 p, float2 a, float2 b)
        {
            float2 ab = b - a;
            float l2 = math.lengthsq(ab);
            float t = l2 > 1e-6f ? math.saturate(math.dot(p - a, ab) / l2) : 0f;
            return math.distance(p, a + ab * t);
        }

        // ------------------------------------------------------------ stand-still, leave, brake, back-out

        // Every candidate of a truck plan met another puppet: stand still where the current leg ends and retry in
        // kStandStillRetry s (a plan with a known conflict is never committed). Stage is kept, so the same half of the
        // cycle is retried.
        public static void StandStill(Puppet p, PlanContext c, string what, Puppet who, double tc)
        {
            var b = Begin(p, c);
            Hold(ref b, float.PositiveInfinity, (AnimKind)b.LastAnim == AnimKind.Load ? AnimKind.Load : AnimKind.Rest);
            Commit(p, ref b, c, c.Now + MxConst.kStandStillRetry);
            if (!p.StandStill && !p.GuardHeld) p.GuardHeldSince = c.Now;
            if (double.IsNaN(p.WaitSince)) p.WaitSince = c.Now;
            if (!p.StandStill) MachineDebug.StandStillEpisodes++;
            p.StandStill = true;
            p.GuardBlocker = who;
            p.Loads.Clear();
            p.Feeding = false;
            p.ZoneEnter = p.ZoneExit = double.NegativeInfinity;
            p.WorkStart = p.WorkEnd = double.NegativeInfinity;
            MachineDebug.StandStills++;
            MxPerf.Count(MxC.StandStills);
            bool first = s_StandStillLogged.Add(((long)p.ProjectId << 8) | (long)p.Role);
            if (first || c.Verbose)
            {
                string msg = "machines: " + p + " stands still: every " + what + " candidate meets " + (who != null ? who.ToString() : "?") +
                             " (first contact in " + RRWLog.F((float)(tc - c.Now)) + " s); retry every " + RRWLog.F(MxConst.kStandStillRetry) + " s";
                if (first) RRWLog.Info(msg);
                else RRWLog.Verbose(msg);
            }
        }

        public static void ClearBlocked(Puppet p)
        {
            p.StandStill = false;
            p.GuardHeld = false;
            p.GuardBlocker = null;
            p.GuardBackOuts = 0;
            p.WaitSince = double.NaN;   // the wait episode ends
            p.AsideSince = double.NaN;  // an aside wait (make-way) ends with it
        }

        // Does the chain end (Trim0 / Trim1) connect to the road network (ProjectRecord.ExitAtStart / ExitAtEnd,
        // Director)? While the Director has not marked either end (both false: not computed / an isolated chain) both count,
        // as if no exit information existed.
        public static bool EndConnected(PlanContext c, bool atEnd)
        {
            var v = c.View;
            if (!v.ExitAtStart && !v.ExitAtEnd) return true;
            return atEnd ? v.ExitAtEnd : v.ExitAtStart;
        }

        // Chain end a leaving puppet heads for (role ended / release / drive-out):
        //  * only one end connects: that end (never towards a dead end; a far exit behind the nose is reached with a K-turn
        //    where the road allows it, else by reversing in kLeaveReverseChunk steps);
        //  * both connect: of the ends reachable without a long reverse (ahead; behind within kMaxReverseLeg or with a K-turn),
        //    the one FARTHER FROM THE CAMERA (the puppet then disappears out of view instead of at the visible cap);
        //  * exits unknown (Director has not marked them) or neither connects: the DriveOut slot, else PhasePlan.ExitFrom.
        public static float ChooseExit(PlanContext c, float u, sbyte facing, float slotU, out string why) => ChooseExit(c, u, facing, slotU, null, out why);

        public static float ChooseExit(PlanContext c, float u, sbyte facing, float slotU, Puppet self, out string why)
        {
            var v = c.View;
            bool known = v.ExitAtStart || v.ExitAtEnd;
            float baseU = !float.IsNaN(slotU) ? slotU : PhasePlan.ExitFrom(v, u, facing);
            if (!known) { why = "exits unknown (single-exit rule)"; return baseU; }
            if (v.ExitAtStart != v.ExitAtEnd) { why = "only connected end"; return v.ExitAtEnd ? c.Trim1 : c.Trim0; }
            // C2 / C3 leavers (grader, excavator, feed trucks) never head for the chain start, where the C3
            // paver starts (Core: Paving front from Trim0) - not even when the camera is at the far end
            if (v.Kind == WorksKind.Construction && (v.Phase == WorksPhase.Foundation || v.Phase == WorksPhase.Paving))
            { why = "both connect: chain end (the C3 paver starts at the chain start)"; return c.Trim1; }
            float ahead = facing >= 0 ? c.Trim1 : c.Trim0, behind = facing >= 0 ? c.Trim0 : c.Trim1;
            bool behindOk = math.abs(behind - u) <= RRWConst.kMaxReverseLeg || c.CanTurn;
            if (self != null && c.Project != null && c.Crews > 1 && behindOk)
            {
                // with several crews, the exit whose route crosses fewer working crews
                int na = CrewsOnRoute(c, c.Project.Id, self, u, ahead), nb = CrewsOnRoute(c, c.Project.Id, self, u, behind);
                if (na != nb)
                {
                    why = "both connect: " + (na < nb ? "ahead" : "behind") + " crosses fewer working crews (" + math.min(na, nb) + " vs " + math.max(na, nb) + ")";
                    return na < nb ? ahead : behind;
                }
            }
            if (!behindOk || math.any(math.isnan(c.Cam)) || c.Track == null) { why = "both connect: ahead"; return ahead; }
            float da = CamDist(c, ahead), db = CamDist(c, behind);
            if (float.IsNaN(da) || float.IsNaN(db) || db <= da + 5f) { why = "both connect: ahead (" + RRWLog.F(da) + " m from the camera)"; return ahead; }
            why = "both connect: behind, farther from the camera (" + RRWLog.F(db) + " vs " + RRWLog.F(da) + " m)";
            return behind;
        }

        // A leaver whose way to the ONLY connected end runs through a working machine of its project (the
        // next phase's crew starts at the chain start; a C4b drive-out meets the crew truck picking up the start barriers) parks at
        // the other (dead) end instead and is removed there once out of view: the crew keeps working, nobody reverses head-on
        // through a single works half. Both or neither ends connected (ChooseExit already chose by distance / camera) or the whole
        // crew leaving (release): unchanged. Counted in rrw.mx.check (LeaverDeadEndYields / LeaverCrewCrossings).
        public static float YieldToCrew(PlanContext c, Puppet p, float u, float lat, float end, ref string why)
        {
            var v = c.View;
            if (c.Releasing || v.ExitAtStart == v.ExitAtEnd || c.Track == null) return end;
            var smp = TrackBuilder.At(c.Track, u);
            if (math.abs(lat) - p.BoxHalfWid >= (smp.HalfWidth > 0f ? smp.HalfWidth : 6f)) return end;   // on the verge: PlanLeave keeps it there
            var q = CrewOnRoute(c, p, u, lat, end);
            if (q == null) return end;
            float other = math.abs(end - c.Trim1) < 0.5f ? c.Trim0 : c.Trim1;
            if (CrewOnRoute(c, p, u, lat, other) != null)
            {
                MachineDebug.LeaverCrewCrossings++;
                why += "; crosses working " + q.Role + " (crew on both sides)";
                return end;
            }
            MachineDebug.LeaverDeadEndYields++;
            why = "dead end: the connected exit lies behind working " + q.Role;
            return other;
        }

        // A working (not leaving) puppet of p's project whose box lies on p's way from u to `end` in p's lane; null = none.
        public static Puppet CrewOnRoute(PlanContext c, Puppet p, float u, float lat, float end)
        {
            float lo = math.min(u, end), hi = math.max(u, end);
            float dir = end >= u ? 1f : -1f;
            foreach (var q in MachineRegistry.All)
            {
                if (q == p || q.ProjectId != p.ProjectId || q.PostSite || q.Outgoing || q.Plan.Count <= 0) continue;
                var qs = SlotOf(c, q);
                if (!qs.Active || qs.Activity == MachineActivity.DriveOut) continue;   // leaves too
                MachineMotion.State(q.Plan, c.Clk, c.Now, out var s);
                if ((s.U - u) * dir < -(p.BoxHalfLen + q.BoxHalfLen)) continue;          // behind p
                if (s.U + q.BoxHalfLen < lo || s.U - q.BoxHalfLen > hi) continue;
                if (math.abs(s.Lat - lat) >= p.BoxHalfWid + q.BoxHalfWid + 0.3f) continue; // another lane: p passes it
                return q;
            }
            return null;
        }

        // The crew slot of puppet q (its own crew's plan; working crews of the whole project count).
        public static CrewSlot SlotOf(PlanContext c, Puppet q)
        {
            if (q.IsRoller)
            {
                // a roller's own slot (Roller0 / Roller1)
                if (q.Crew == c.CrewIndex) return c.Crew.Roller(q.RollerIndex).Slot;
                if (q.Crew < 0 || q.Crew >= c.View.CrewCount) return default;
                return PhasePlan.Crew(c.View, q.Crew).Roller(q.RollerIndex).Slot;
            }
            if (q.Crew == c.CrewIndex) return c.Crew.Get(q.Role);
            if (q.Crew < 0 || q.Crew >= c.View.CrewCount) return default;
            return PhasePlan.Crew(c.View, q.Crew).Get(q.Role);
        }

        // Distinct crews of the project with a working puppet between u and `end` (any lane).
        static int CrewsOnRoute(PlanContext c, uint projectId, Puppet self, float u, float end)
        {
            float lo = math.min(u, end), hi = math.max(u, end);
            int mask = 0;
            foreach (var q in MachineRegistry.All)
            {
                if (q == self || q.ProjectId != projectId || q.PostSite || q.Outgoing || q.Plan.Count <= 0 || float.IsNaN(q.NowU)) continue;
                if (q.NowU + q.BoxHalfLen < lo || q.NowU - q.BoxHalfLen > hi) continue;
                mask |= 1 << math.clamp(q.Crew, 0, 30);
            }
            return math.countbits(mask);
        }

        static float CamDist(PlanContext c, float u) =>
            c.Tv.Point(c.Track.Slot, u, out var tp) ? math.distance(tp.Pos.xz, c.Cam.xz) : float.NaN;

        // Compatibility helper (completion ordering): the end a leaver at (u, facing) heads for.
        public static float LeaveEnd(PlanContext c, float u, sbyte facing) => ChooseExit(c, u, facing, float.NaN, out _);

        // Leave / release: drive to the chain end INSIDE the trims in the current lane - never into an open /
        // not-ready group (ClampLat), never outside the footprint (ClampU) - to the end-most free slot, and stand there until the
        // director removes the puppet (out of sight, or kPostSiteMaxSimFrames after it began leaving). A puppet standing on a
        // verge, wholly outside the road composition, stays there: it blocks no lane and never crosses a sidewalk.
        // An exit behind the nose beyond kMaxReverseLeg is reached with a K-turn where the road allows one, else
        // by reversing in chunks of kLeaveReverseChunk with short pauses (max 4 per plan: `more` asks for a continuation plan).
        public static void PlanLeave(Puppet p, PlanContext c, float endU, out string where) => PlanLeave(p, c, endU, out where, out _);

        // lead: s the leaver holds first (a candidate whose resumable conflict scan may take a few updates)
        // vmax: the leave drives at most this fast (a convoy behind a slower leaver heading for the same exit; -1 = the role's)
        public static void PlanLeave(Puppet p, PlanContext c, float endU, out string where, out bool more, float lead = 0f, float vmax = -1f)
        {
            MxPerf.Begin(MxT.X_PlanLeave);
            PlanLeaveCore(p, c, endU, out where, out more, lead, vmax);
            MxPerf.End(MxT.X_PlanLeave);
        }

        static void PlanLeaveCore(Puppet p, PlanContext c, float endU, out string where, out bool more, float lead, float vmax)
        {
            more = false;
            var b = Begin(p, c);
            b.Leaving = true;   // forward drives at the role's VLeave
            if (lead > 0f) Hold(ref b, b.T + lead, AnimKind.Rest);
            var smp = TrackBuilder.At(c.Track, b.U);
            float hw = smp.HalfWidth > 0f ? smp.HalfWidth : 6f;
            if (math.abs(b.Lat) - p.BoxHalfWid >= hw)
            {
                Hold(ref b, float.PositiveInfinity, AnimKind.Rest);
                Commit(p, ref b, c, double.PositiveInfinity);
                where = "stays on the verge";
                return;
            }
            float lat = ClampLat(c, p, b.U, b.Lat);
            float target = LeaveSlot(c, p, endU, b.U, lat, out where);
            float dist = math.abs(target - b.U);
            sbyte mv = target >= b.U ? (sbyte)1 : (sbyte)-1;
            if (dist > 0.05f || math.abs(lat - b.Lat) > 0.02f)
            {
                if (mv != b.Facing && dist > RRWConst.kMaxReverseLeg && !p.IsRoller)
                {
                    if (c.CanTurn) { GoTo(ref b, p, c, target, lat, mv, false); where += " (K-turn)"; }
                    else if (LeaveTurnFit(c, p, b.U, mv, out float tu, out float latC, out float tr))
                    {
                        // A compact K-turn sized to the road (the standard one needs kTurnSweepHalf each side of the centre
                        // line): a short reverse towards the exit while merging to the band centre, the turn, then forward to the exit
                        Drive(ref b, p, tu, latC, -1f, RunLo(c, p), RunHi(c, p));
                        Turn(ref b, p, TurnSide(b), tr);
                        Drive(ref b, p, target, lat, -1f, RunLo(c, p), RunHi(c, p));
                        MachineDebug.CompactTurns++;
                        where += " (compact K-turn R=" + RRWLog.F(tr) + " at u=" + RRWLog.F(tu) + ")";
                    }
                    else
                    {
                        float cur = b.U;
                        int chunks = 0;
                        while (math.abs(target - cur) > 0.05f && chunks < 4 && !b.Overflow)
                        {
                            float step = math.min(MxConst.kLeaveReverseChunk, math.abs(target - cur));
                            cur += mv * step;
                            Drive(ref b, p, cur, lat, vmax);
                            chunks++;
                            if (math.abs(target - cur) > 0.05f) Hold(ref b, b.T + MxConst.kLeaveReversePause, AnimKind.Rest);
                        }
                        more = math.abs(target - cur) > 0.05f;
                        where += " (reversing in " + chunks + " chunk(s)" + (more ? ", continues" : "") + ")";
                    }
                }
                else Drive(ref b, p, target, lat, vmax);
            }
            Hold(ref b, float.PositiveInfinity, AnimKind.Rest);
            Commit(p, ref b, c, double.PositiveInfinity);
            if (more) p.ReplanAt = MachineMotion.MotionEnd(p.Plan) + 0.5;
        }

        // Interior crews' leavers must not vanish in view at the 120 s cap: machine seconds a leaver needs from
        // fromU to its exit at the role's LEAVING limits (planning margin, K-turn / reverse chunks included) plus a guard margin;
        // at least the committed plan's motion end. Never below kPostSiteMaxSeconds.
        public static double LeaveNeedSeconds(Puppet p, PlanContext c, float fromU, sbyte facing, float endU, double now)
        {
            var lim = p.Lim;
            float dist = math.abs(endU - fromU);
            bool behind = (endU - fromU) * facing < 0f && dist > RRWConst.kMaxReverseLeg && !p.IsRoller;   // rollers just reverse
            float v;
            double extra = 30.0;   // guard waits / yields on the way
            if (behind && !c.CanTurn)
            {
                v = lim.VFor(true, false, true);
                extra += (dist / MxConst.kLeaveReverseChunk + 1f) * (MxConst.kLeaveReversePause + 2.0);
            }
            else
            {
                v = lim.VFor(false, false, true);
                if (behind) extra += 60.0;   // K-turn
            }
            double need = MachineLimits.MinLegSeconds(dist, math.max(0.1f, v * MxConst.kSpeedMargin), math.max(0.05f, lim.Accel)) + extra;
            if (p.Plan.Count > 0)
            {
                double end = MachineMotion.MotionEnd(p.Plan);
                if (!double.IsInfinity(end) && !double.IsNaN(end)) need = math.max(need, end - now + 30.0);
            }
            if (double.IsNaN(need) || double.IsInfinity(need)) need = RRWConst.kPostSiteMaxSeconds;
            return math.max(RRWConst.kPostSiteMaxSeconds, need);
        }

        // The end slot search runs inward from the exit end in kParkStep steps and may go on PAST the leaver's own position: two
        // leavers clamped to the same exit slot (both at AnchorLo of a trimmed chain start, e.g. the C3 grader and excavator) used to end
        // with the second one "stopping where it is" - on top of the first. Pass 0: a slot free of every resting box AND out of every
        // working machine's path (InWorkPath: the paver starting at the section start, the excavator digging forward) between the exit
        // and kLeaveSlotPast beyond the leaver; pass 1: any free slot in that range; pass 2: any free slot further inward. Standing where it
        // is remains the last resort (the guard's make-way / blocking-leaver removal resolves a spot that is not free).
        public const float kLeaveSlotPast = 12f;

        static float LeaveSlot(PlanContext c, Puppet p, float endU, float fromU, float lat, out string where)
        {
            float end = ClampU(c, p, endU);
            float dir = end >= fromU ? 1f : -1f;
            float lo = AnchorLo(c, p) - 0.01f, hi = AnchorHi(c, p) + 0.01f;
            for (int pass = 0; pass < 3; pass++)
            {
                for (int k = 0; k <= MxConst.kParkTries; k++)
                {
                    float uu = end - dir * k * MxConst.kParkStep;
                    if (uu < lo || uu > hi) break;
                    float past = (fromU - uu) * dir;   // > 0: inward beyond the leaver's position
                    if (pass < 2 && past > kLeaveSlotPast) break;
                    if (pass == 2 && past <= kLeaveSlotPast) continue;
                    if (!SlotFree(p, c, uu, lat)) continue;
                    if (pass == 0 && InWorkPath(c, p, uu, lat) != null) continue;
                    if (past > 0.01f) MachineDebug.LeaveSlotShifts++;
                    where = "end slot " + k + " u=" + RRWLog.F(uu) + (pass >= 1 && InWorkPath(c, p, uu, lat) != null ? " (in a working path)" : "");
                    return uu;
                }
            }
            where = SlotFree(p, c, fromU, lat) ? "no free end slot: stops where it is" : "no free end slot: stops where it is (NOT free: make-way)";
            return fromU;
        }

        // Separation guard: stop now. A drive brakes at kGuardDecelFactor x its acceleration (Brake leg, no
        // pose jump), a K-turn is cut at its next cusp (speed 0 there; the truncated turn leg ends the plan and holds its
        // pose), front-relative legs and holds stop where they are. contactAt (machine time) is the predicted contact: a brake
        // that would not stop before it decelerates harder, a turn whose next cusp comes after it stops in place.
        // Returns false when the puppet already brakes or stands at the end of its plan.
        public static bool BrakeSplice(Puppet p, PlanContext c, double contactAt)
        {
            if (p.Plan.Count <= 0) return false;
            var old = p.Plan;
            double tOld = c.Now - old.Epoch;
            int i = MachineMotion.ActiveLeg(old, tOld);
            var cur = old.Get(i);
            double lt = tOld - cur.T0;
            if (cur.Kind == (byte)LegKind.Brake || (cur.Kind == (byte)LegKind.Hold && cur.OpenEnded && i == old.Count - 1)) return false;
            if (i == old.Count - 1 && !cur.OpenEnded && tOld >= cur.T1 - 1e-4) return false;   // a finished last leg holds its pose
            if (cur.Kind == (byte)LegKind.Turn)
            {
                MachineMotion.TurnTimes(math.max(1f, cur.P0), cur.Vmax, cur.P2, cur.Acc, out float t1, out float t2, out float total);
                float pz = MxConst.kTurnPause;
                double cusp;
                if (lt < t1) cusp = t1;
                else if (lt < t1 + pz) cusp = lt;
                else if (lt < t1 + pz + t2) cusp = t1 + pz + t2;
                else if (lt < t1 + 2f * pz + t2) cusp = lt;
                else cusp = total;
                if (old.Epoch + cur.T0 + cusp > contactAt - 0.1)
                {
                    cusp = lt;   // the contact comes first: stop in place (pose kept) - an emergency stop (counted)
                    MxSpeed.EmergencyBrakes++;
                    MxSpeed.Restart(p);
                }
                var bt = Begin(p, c, false, cur.T0 + cusp);
                bt.V = 0f;
                Commit(p, ref bt, c, double.PositiveInfinity);
                return true;
            }
            MachineMotion.State(old, c.Clk, c.Now, out var s);
            var b = Begin(p, c, false);
            float v0 = math.abs(s.Speed);
            if (cur.Kind == (byte)LegKind.Drive && v0 > 0.05f)
            {
                b.V = 0f;   // this Brake leg stops the drive (no Settle brake on top)
                // The guard's emergency brake decelerates harder than a planned leg, but never beyond kEmergencyDecel
                float a = math.min(RRWConst.kEmergencyDecel, math.max(0.3f, p.Lim.Accel * MxConst.kGuardDecelFactor));
                double room = contactAt - c.Now - 0.1;
                if (v0 / a > room) a = math.min(RRWConst.kEmergencyDecel, v0 / (float)math.max(0.15, room));   // late detection: brake harder
                if (a > p.Lim.Accel * 1.001f) MxSpeed.EmergencyBrakes++;
                sbyte mv = cur.Move >= 0 ? (sbyte)1 : (sbyte)-1;
                float s0 = MachineMotion.DriveSlope(cur, (float)lt);
                float T = v0 / a, D = v0 * T * 0.5f;
                var l = new MachineLeg
                {
                    T0 = b.T, T1 = b.T + T, Kind = (byte)LegKind.Brake, Anim = (byte)AnimKind.Rest, Facing = b.Facing, Move = mv,
                    U0 = b.U, L0 = b.Lat, Vmax = v0, Acc = a, P0 = s0,
                };
                if (b.Add(l))
                {
                    float nose = mv == b.Facing ? 1f : -1f;
                    b.T = l.T1;
                    b.U = l.U0 + mv * D;
                    b.Lat = l.L0 + s0 * D * 0.5f;
                    b.Odo += D * nose;
                }
            }
            Hold(ref b, float.PositiveInfinity, AnimKind.Rest);
            Commit(p, ref b, c, double.PositiveInfinity);
            return true;
        }

        // Deadlock: a standing puppet backs out along its own lane, away from `from` (sign of du), to the
        // nearest slot that is free of every other resting box and whose route meets nobody (the blocker it leaves is
        // ignored). ClampU / the current lateral keep it inside the works footprint and out of open lanes. Returns false when no slot works.
        public static bool BackOut(Puppet p, PlanContext c, float away, Puppet blocker, out string where)
        {
            MxPerf.Begin(MxT.X_BackOut);
            bool r = BackOutCore(p, c, away, blocker, out where);
            MxPerf.End(MxT.X_BackOut);
            return r;
        }

        static bool BackOutCore(Puppet p, PlanContext c, float away, Puppet blocker, out string where)
        {
            where = "no slot";
            if (p.Plan.Count <= 0 || c.Track == null) return false;
            MachineMotion.State(p.Plan, c.Clk, c.Now, out var s);
            float lat = ClampLat(c, p, s.U, s.Lat);
            float dir = away >= 0f ? 1f : -1f;
            var saved = p.Plan;
            int n = (int)math.ceil(MxConst.kGuardBackOutMax / MxConst.kParkStep);
            for (int k = 1; k <= n; k++)
            {
                if (MxBudget.CheckExhausted) { MxPerf.Count(MxC.ScanAborted); break; }   // retried later
                float want = s.U + dir * k * MxConst.kParkStep;
                float uu = ClampU(c, p, want);
                if (math.abs(uu - want) > 0.5f) break;          // reached the chain end (footprint)
                if (!SlotFree(p, c, uu, lat)) continue;
                var b = Begin(p, c, false);
                Drive(ref b, p, uu, lat, b.Lim.VRev * MxConst.kSpeedMargin);   // VFor(reverse) of the role (not a fixed 2 m/s)
                double arrive = b.Abs(b.T);
                Hold(ref b, float.PositiveInfinity, AnimKind.Rest);
                if (FindConflict(p, b.Plan, c, c.Now, arrive + 1.0, out _, out _, out _, MxConst.kGuardMargin, blocker)) continue;
                Commit(p, ref b, c, arrive + MxConst.kGuardRetry);
                where = "backs out " + RRWLog.F(math.abs(uu - s.U)) + " m to u=" + RRWLog.F(uu);
                return true;
            }
            p.Plan = saved;
            return false;
        }

        // ------------------------------------------------------------ verge validation (static + net search trees)

        struct BoundsIter : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds2 Box;
            public NativeList<Entity> Hits;
            public bool Intersect(QuadTreeBoundsXZ bounds) => MathUtils.Intersect(bounds.m_Bounds.xz, Box);
            public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
            {
                if (MathUtils.Intersect(bounds.m_Bounds.xz, Box) && Hits.Length < 64) Hits.Add(item);
            }
        }

        // True when a machine box at (u, lat) on the verge is free of buildings, trees, other objects and other roads,
        // and the verge ground is not a steep step from the floor.
        public static bool VergeClear(PlanContext c, Puppet p, float u, float lat)
        {
            MxPerf.Begin(MxT.X_VergeClear);
            MxPerf.Count(MxC.VergeClears);
            bool r = VergeClearCore(c, p, u, lat);
            MxPerf.End(MxT.X_VergeClear);
            return r;
        }

        static bool VergeClearCore(PlanContext c, Puppet p, float u, float lat)
        {
            try
            {
                var smp = TrackBuilder.At(c.Track, u);
                float vr = lat < 0f ? smp.VergeL : smp.VergeR;
                if (float.IsNaN(vr)) return false;
                if (math.abs(vr - smp.FloorRel) > MxConst.kVergeMaxStep && smp.FloorRel < -0.3f) return false;
                if (!c.Tv.Point(c.Track.Slot, u, out var tp)) return false;
                float2 ctr = tp.Pos.xz + tp.Right * lat;
                float r = math.max(p.BoxHalfLen, p.BoxHalfWid) + 0.5f;
                var box = new Bounds2(ctr - r, ctr + r);
                var em = c.Em;
                var hits = new NativeList<Entity>(16, Allocator.Temp);
                try
                {
                    if (c.ObjSearch != null)
                    {
                        MxPerf.Begin(MxT.X_SearchTreeSync);
                        var tree = c.ObjSearch.GetStaticSearchTree(true, out JobHandle dep);
                        dep.Complete();
                        MxPerf.End(MxT.X_SearchTreeSync);
                        MxPerf.Count(MxC.SearchTreeSyncs);
                        var it = new BoundsIter { Box = box, Hits = hits };
                        tree.Iterate(ref it);
                        for (int i = 0; i < hits.Length; i++)
                        {
                            Entity e = hits[i];
                            if (!em.Exists(e) || em.HasComponent<Deleted>(e) || em.HasComponent<Game.Tools.Temp>(e)) continue;
                            if (em.HasComponent<RRWMachine>(e)) continue;
                            return false;   // building, tree, prop, street furniture
                        }
                    }
                    hits.Clear();
                    if (c.NetSearch != null)
                    {
                        MxPerf.Begin(MxT.X_SearchTreeSync);
                        var tree = c.NetSearch.GetNetSearchTree(true, out JobHandle dep);
                        dep.Complete();
                        MxPerf.End(MxT.X_SearchTreeSync);
                        MxPerf.Count(MxC.SearchTreeSyncs);
                        var it = new BoundsIter { Box = box, Hits = hits };
                        tree.Iterate(ref it);
                        for (int i = 0; i < hits.Length; i++)
                        {
                            Entity e = hits[i];
                            if (!em.Exists(e) || em.HasComponent<Deleted>(e) || em.HasComponent<Game.Tools.Temp>(e)) continue;
                            if (c.Project.Edges.Contains(e)) continue;
                            if (em.HasComponent<Edge>(e))
                            {
                                if (!em.HasComponent<Curve>(e)) continue;
                                var curve = em.GetComponentData<Curve>(e).m_Bezier;
                                float dist = MathUtils.Distance(curve.xz, ctr, out _);
                                float hw = EcsUtil.CompositionWidth(em, e) * 0.5f;
                                if (dist < hw + r) return false;
                            }
                            else if (em.HasComponent<Game.Net.Node>(e))
                            {
                                var ce = em.HasBuffer<ConnectedEdge>(e) ? em.GetBuffer<ConnectedEdge>(e, true) : default;
                                bool ours = false;
                                if (ce.IsCreated)
                                    for (int k = 0; k < ce.Length; k++) if (c.Project.Edges.Contains(ce[k].m_Edge)) { ours = true; break; }
                                if (!ours) return false;
                            }
                        }
                    }
                }
                finally { hits.Dispose(); }
                return true;
            }
            catch (Exception e)
            {
                RRWLog.ErrorOnce("machines verge check", e);
                return false;
            }
        }

        // Verge slot: slides inward along u in 4 m steps until clear (6 tries), else falls back to the floor lane.
        // Returns false when no verge slot was found (the floor fallback lane is used).
        public static bool VergeSlot(PlanContext c, Puppet p, ref float u, ref float lat, float floorFallbackV)
        {
            if (math.abs(lat) < 0.01f) return true;
            if (c.Visible) { lat = ClampLat(c, p, u, lat); return true; }   // no verge slots on a visible road (sidewalks)
            float mid = 0.5f * (c.Trim0 + c.Trim1);
            float step = u < mid ? 4f : -4f;
            float u0 = u;
            for (int i = 0; i < 6; i++)
            {
                float uu = ClampU(c, p, u0 + step * i);
                if (VergeClear(c, p, uu, lat)) { u = uu; return true; }
            }
            lat = LatOf(c, floorFallbackV, u, p);
            return false;
        }

        // Lateral of the digger on the trench floor, clamped with its CHASSIS width (the rest-pose bounds
        // include the upper-body overhang): it hugs its floor edge so a truck fits beside it (loading pair test = chassis).
        public static float DiggerLat(PlanContext c, float v, float u, Puppet p)
        {
            if (c.Visible || math.abs(v) > 0.51f || !p.ExcavatorRig) return LatOf(c, v, u, p);
            var smp = TrackBuilder.At(c.Track, u);
            float flat = smp.FlatHalf > 0f ? smp.FlatHalf : 4f;
            float lim = math.max(0f, flat - p.BoxHalfWid * 0.85f - 0.3f);
            return math.clamp(v * 2f * flat, -lim, lim);
        }
    }
}
