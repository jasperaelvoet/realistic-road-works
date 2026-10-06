using Unity.Mathematics;

// Road roller plans. A compaction plan is a chain of ordinary rest-to-rest legs - an approach
// Drive to the window bottom, then passes: Drive forward to the window top (lateral shift <= kRollerPassShift smoothstepped over the
// pass, vibration anim on vibrating duties), Hold kRollerReversePause, Drive back to the window bottom, Hold, ... The end of every pass
// is PhasePlan.RollerWindow at the crew front of that leg's PLANNED END TIME (the whole Core crew plan evaluated at the phase fraction
// of that time), so the passes keep up with the front while each leg stays a plain trapezoid at the pass speed and the roller's
// Accel. Rollers never K-turn and have no reverse limit. Laterals: PhasePlan.RollerPassLateral over the roller CENTRE range (hidden
// floor: the flat floor minus half the roller and kRollerHiddenMargin; visible road: Choreo.LatRange = carriageway lanes, fences, parked strips,
// kept halves; WorksHalfOnly: the kStagedWorksHalf side of the split), minus the pass yaw swing at both ends.
namespace RealisticRoadWorks.V3.Machines
{
    public static partial class Choreo
    {
        // The roller slot of crew `crew`, index `idx` at machine time `tau` (the Core plan at the phase fraction of that time).
        public static RollerSlot RollerSlotAt(PlanContext c, int crew, int idx, double tau)
        {
            var v = c.View;
            v.F = FractionOf(c, tau);
            return PhasePlan.Crew(v, crew).Roller(idx);
        }

        // Centre lateral range of a roller box at chain u (chain frame). False when nothing fits (then the middle of what is there).
        public static bool RollerLatRange(PlanContext c, Puppet p, float u, bool worksHalfOnly, out float lo, out float hi)
        {
            lo = float.NegativeInfinity; hi = float.PositiveInfinity;
            bool ok = true;
            if (!c.Visible)
            {
                var smp = TrackBuilder.At(c.Track, u);
                float flat = smp.FlatHalf > 0f ? smp.FlatHalf : 3f;
                float lim = math.max(0f, flat - 0.5f * RRWConst.kRollerWidth - MxConst.kRollerHiddenMargin);
                lo = -lim; hi = lim;
            }
            if (LatRange(c, p, u, out float a, out float b))
            {
                lo = math.max(lo, a);
                hi = math.min(hi, b);
            }
            else { lo = a; hi = b; ok = false; }
            if (worksHalfOnly)
            {
                // C3 f >= kRollerC3WorksHalfF: the C4a works half only (relative to the direction split)
                float sp = SplitAt(c, u);
                if (float.IsNaN(sp)) sp = 0f;
                float tol = RRWConst.kZoneCentreTolerance + MxConst.kHalfClearance + p.BoxHalfWid;
                RoadZones works = RRWConst.kStagedWorksHalf;
                if ((works & RoadZones.RightHalf) != 0) lo = math.max(lo, sp + tol);
                else hi = math.min(hi, sp - tol);
            }
            if (float.IsInfinity(lo) || float.IsInfinity(hi)) { lo = -1f; hi = 1f; }
            // the pass yaw swing (a 0.8 m shift over >= 8 m) is taken off both ends
            lo += MxConst.kRollerLatYawMargin;
            hi -= MxConst.kRollerLatYawMargin;
            if (hi < lo) { float m = 0.5f * (lo + hi); lo = hi = m; ok = false; }
            return ok;
        }

        // Range valid over the whole window [u0, u1] (both ends and the middle).
        static void WindowLatRange(PlanContext c, Puppet p, float u0, float u1, bool worksHalfOnly, out float lo, out float hi)
        {
            RollerLatRange(c, p, u0, worksHalfOnly, out lo, out hi);
            RollerLatRange(c, p, 0.5f * (u0 + u1), worksHalfOnly, out float a, out float b);
            lo = math.max(lo, a); hi = math.min(hi, b);
            RollerLatRange(c, p, u1, worksHalfOnly, out a, out b);
            lo = math.max(lo, a); hi = math.min(hi, b);
            if (hi < lo) { float m = 0.5f * (lo + hi); lo = hi = m; }
        }

        static int PassSeed(Puppet p) => (int)(p.Seed % 7u) + p.RollerIndex * 3;

        // Mode H band crews: the pass window stays inside the crew's stretch of the chain (its anchor range).
        static void UwClampWindow(PlanContext c, Puppet p, ref float lo, ref float hi)
        {
            if (!c.LateralMetres) return;
            float a = AnchorLo(c, p), b = AnchorHi(c, p);
            if (b < a) return;
            lo = math.clamp(lo, a, b);
            hi = math.clamp(hi, a, b);
        }

        // Plans one roller from its slot (Compact: passes; Parked / relay: drive there and stand; anything else: stand still).
        public static void PlanRoller(Puppet p, PlanContext c, in RollerSlot rs)
        {
            MxPerf.Count(MxC.RollerPlans);
            p.Duty = rs.Duty;
            if (rs.Slot.Activity == MachineActivity.Compact) { PlanCompact(p, c, rs); return; }
            p.PassStart = double.NaN;
            var b = Begin(p, c);
            b.MergeSlope = math.min(b.MergeSlope, MxConst.kRollerPassMergeSlope);
            if (rs.Slot.Activity == MachineActivity.Parked)
            {
                float u = ClampU(c, p, rs.Slot.U);
                RollerLatRange(c, p, u, rs.WorksHalfOnly, out float lo, out float hi);
                float lat = math.clamp(b.Lat, lo, hi);
                Drive(ref b, p, u, lat);   // forward or reversing, never a K-turn
            }
            Hold(ref b, float.PositiveInfinity, AnimKind.Rest);
            Commit(p, ref b, c, double.PositiveInfinity);
        }

        // Compaction passes. Commits up to kRollerPassesPerPlan passes; re-plans at the start of the final hold.
        static void PlanCompact(Puppet p, PlanContext c, in RollerSlot rs0)
        {
            var b = Begin(p, c);
            b.MergeSlope = math.min(b.MergeSlope, MxConst.kRollerPassMergeSlope);
            int crew = c.CrewIndex, idx = p.RollerIndex;
            float speed = math.max(0.3f, rs0.PassSpeed);
            bool vib = rs0.Vibrate;
            float acc = math.max(0.05f, b.Lim.Accel);
            // the window now; the approach drives to its bottom (VFwd) unless the roller already stands inside it
            float lo = rs0.WinLo, hi = rs0.WinHi;
            if (float.IsNaN(lo) || float.IsNaN(hi)) { Hold(ref b, float.PositiveInfinity, AnimKind.Rest); Commit(p, ref b, c, c.Now + 5.0); return; }
            UwClampWindow(c, p, ref lo, ref hi);
            WindowLatRange(c, p, lo, hi, rs0.WorksHalfOnly, out float latLo, out float latHi);
            int seed = PassSeed(p);
            float lat = PhasePlan.RollerPassLateral(p.PassK + seed, latLo, latHi, RRWConst.kRollerPassShift);
            bool inside = b.U >= lo - 0.5f && b.U <= hi + 0.5f && math.abs(b.Lat - lat) < 1.5f;
            if (!inside)
            {
                // approach: the window bottom at the arrival time (two passes of the estimate)
                float tgt = lo;
                for (int it = 0; it < 2; it++)
                {
                    var probe = b;
                    Drive(ref probe, p, tgt, lat);
                    var sl = RollerSlotAt(c, crew, idx, probe.Abs(probe.T));
                    if (sl.Slot.Activity != MachineActivity.Compact || float.IsNaN(sl.WinLo)) break;
                    tgt = sl.WinLo;
                }
                Drive(ref b, p, tgt, lat);
                Hold(ref b, b.T + RRWConst.kRollerReversePause, AnimKind.Rest);
            }
            // passes: forward to the window top (lateral shift), reverse to the window bottom
            p.PassStart = b.Abs(b.T);
            int passes = 0;
            bool forward = b.U < 0.5f * (lo + hi);
            double replan = double.NaN;
            while (passes < MxConst.kRollerPassesPerPlan && b.Plan.Count + 3 <= MachinePlan.MaxLegs)
            {
                // the leg's end: the window at its planned end time (iterated: the end depends on the length)
                float end = forward ? hi : lo;
                bool open = true;
                for (int it = 0; it < 3; it++)
                {
                    float dist = math.abs(end - b.U);
                    double tEnd = b.Abs(b.T) + MachineMotion.TrapDur(dist, speed, acc) + 0.5;
                    var sl = RollerSlotAt(c, crew, idx, tEnd);
                    if (sl.Slot.Activity != MachineActivity.Compact || float.IsNaN(sl.WinLo)) { open = false; break; }
                    lo = sl.WinLo; hi = sl.WinHi;
                    UwClampWindow(c, p, ref lo, ref hi);
                    end = forward ? hi : lo;
                }
                if (!open) { replan = b.Abs(b.T) + 5.0; break; }   // window closed at the leg end: hold, look again soon
                if (math.abs(end - b.U) < 1.0f)
                {
                    // already at that end (window not moved yet): turn round
                    forward = !forward;
                    end = forward ? hi : lo;
                    if (math.abs(end - b.U) < 1.0f) { replan = b.Abs(b.T) + 3.0; break; }
                }
                float latNext = b.Lat;
                if (end > b.U)
                {
                    // forward pass: the next lateral of the pattern (only forward passes shift)
                    WindowLatRange(c, p, math.min(b.U, end), math.max(b.U, end), rs0.WorksHalfOnly, out latLo, out latHi);
                    p.PassK++;
                    latNext = PhasePlan.RollerPassLateral(p.PassK + seed, latLo, latHi, RRWConst.kRollerPassShift);
                }
                else
                {
                    WindowLatRange(c, p, math.min(b.U, end), math.max(b.U, end), rs0.WorksHalfOnly, out latLo, out latHi);
                    latNext = math.clamp(b.Lat, latLo, latHi);
                }
                int before = b.Plan.Count;
                Drive(ref b, p, end, latNext, speed);
                if (b.Plan.Count > before && end > b.Plan.Get(b.Plan.Count - 1).U0 - 1e-3f && vib && b.Plan.Get(b.Plan.Count - 1).Move == b.Facing)
                {
                    // vibrating duty, forward pass: the Compact anim (the renderer shimmers the drums while |v| > 0.15 m/s)
                    var l = b.Plan.Get(b.Plan.Count - 1);
                    l.Anim = (byte)AnimKind.Compact;
                    b.Plan.Set(b.Plan.Count - 1, l);
                    b.LastAnim = l.Anim;
                }
                Hold(ref b, b.T + RRWConst.kRollerReversePause, AnimKind.Rest);
                forward = !forward;
                passes++;
            }
            double holdAt = b.Abs(b.T);
            Hold(ref b, float.PositiveInfinity, AnimKind.Rest);
            Commit(p, ref b, c, double.IsNaN(replan) ? holdAt : math.max(holdAt, replan));
        }
    }
}
