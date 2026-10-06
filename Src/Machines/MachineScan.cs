using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;

// The overlap planner's conflict check (FindConflict) as a RESUMABLE scan (budget: no single-frame spike > 2 ms). A whole
// check (up to kPlanCheckMax = 180 s: 361 grid steps x every near neighbour) used to run inside one call,
// and the planning budget was only consulted before an attempt started, so one truck attempt at the dig site (excavator, other
// truck, grader, crew truck all within reach for the whole horizon) cost 6.7 ms in one frame. Now:
//  * a scan keeps its position (grid step, running conflict, per-neighbour next step) on the scan object and every Run advances it
//    until the scan ends or a Stopwatch deadline passes (checked inside the step loop, every kScanCheckEvery steps);
//  * the truck trips and the leavers own a PlanPending: the candidate plan + what the commit needs. The puppet keeps its committed
//    plan while the scan runs (an intended wait of a few frames, never counted as stuck); the candidate is committed only once the
//    scan comes back clear AND the candidate still continues the committed plan at the commit time (CommitBy: the candidate and the
//    committed plan agree up to the candidate's cut, and on through a common Hold). A scan that outlives its candidate is dropped and
//    the next candidate holds a departure lead (Puppet.ScanLead) so it can be committed a few updates later;
//  * a resumed scan restarts from its first step when a neighbour's committed plan changed meanwhile (sample cache generation);
//  * the synchronous callers (release check, back-out candidates) run under the per-update check budget (MxBudget.Check*): a check
//    that does not finish inside it counts as a conflict and is retried by its caller later.
namespace RealisticRoadWorks.V3.Machines
{
    public enum ScanState : byte { Idle = 0, Running, Clear, Conflict }
    public enum ScanFor : byte { None = 0, TruckOut, TruckRet, Leave }

    public sealed class ConflictScan
    {
        public ScanState State;
        public Puppet Me, Ignore, Who;
        public MachinePlan Plan;               // the candidate (a copy: the puppet keeps its committed plan meanwhile)
        public double From, To, Tc, Dur, TLast;
        public long K0, K1, K;
        public int Stride;
        public bool InConf;
        public float Margin;
        public int Runs;                       // Run calls (> 1: resumed in a later update)
        public readonly List<Puppet> Near = new List<Puppet>(16);
        public readonly List<float> NearMargin = new List<float>(16);
        public readonly List<long> NearNext = new List<long>(16);    // next grid step each neighbour is tested at (skip-ahead)
        public readonly List<float> NearV = new List<float>(16);     // closing speed bound of the pair
        public readonly List<int> NearGen = new List<int>(16);       // neighbour sample cache generation the scan started on
        // A LEAVER that already touches a neighbour at the scan start (head-on jam, two machines stacked): that contact is no
        // conflict while it does not deepen (true-box gap never below the last one by > kSepTolerance) for kSepAllowSeconds, until the
        // pair is clear of the margins once (normal rules from then on). Without it every drive-off of a touching leaver was a conflict.
        public readonly List<float> NearGap = new List<float>(16);   // last true-box gap of a touching neighbour (+inf = not touching)
        public readonly List<bool> NearSep = new List<bool>(16);     // ... the pair has been clear of the margins since
    }

    // A candidate plan waiting for its conflict scan (one per puppet: a truck plans one half of its cycle at a time; a leaver its drive-off).
    public sealed class PlanPending
    {
        public readonly ConflictScan Scan = new ConflictScan();
        public ScanFor For;
        public int Attempt;
        public double CommitBy;                // machine time up to which the candidate may still replace the committed plan
        public LegBuilder B;                   // truck trips: the candidate's builder (committed as is)
        public Choreo.Route R;                 // outbound: route times (zone, loads)
        public TruckCfg Cfg;
        public double Depart;                  // outbound: departure of this attempt
        public bool Waiter;                    // outbound: C3 waiting feed truck (the one-truck front zone rule does not apply)
        public int GridEpoch;                  // outbound: CrewState.GridEpoch the candidate was planned on
        public bool Wait;                      // return: base wait after arrival
        public bool More;                      // leaver: the plan needs a continuation
        public double ReplanAt;                // leaver: ReplanAt the candidate set

        public double BaseEpoch;               // identity of the committed plan the candidate continues (Epoch + Count: every
        public int BaseCount;                  // Begin-based plan has a new Epoch; restores of a held plan keep the old one)

        public bool Holds(ScanFor f, int attempt) => For == f && Attempt == attempt && Scan.State == ScanState.Running;

        public FrontRef BaseA;                 // the committed plan's FrontA at build time (models re-anchored meanwhile: RefreshModels)

        public void SetBase(in MachinePlan cur) { BaseEpoch = cur.Epoch; BaseCount = cur.Count; BaseA = cur.FrontA; }

        // the committed plan is still the one the candidate was built from (no guard brake / back-out / restore in between)
        public bool BaseIs(in MachinePlan cur) => cur.Epoch == BaseEpoch && cur.Count == BaseCount;

        public void Drop()
        {
            For = ScanFor.None;
            Scan.State = ScanState.Idle;
            Scan.Me = Scan.Ignore = Scan.Who = null;
            Scan.Near.Clear();
        }
    }

    public static partial class Choreo
    {
        const int kScanCheckEvery = 4;          // grid steps between two deadline checks
        const float kSepTolerance = 0.05f;      // a touching leaver's gap may shrink this much between two steps (sampling)
        const double kSepAllowSeconds = 15.0;   // ... and stay in contact at most this long (it must move apart)
        public const long NoDeadline = long.MaxValue;
        static readonly ConflictScan s_Sync = new ConflictScan();

        // ------------------------------------------------------------ synchronous checks

        public static bool FindConflict(Puppet me, in MachinePlan plan, PlanContext c, double from, double to, out double tc, out double dur) =>
            FindConflict(me, plan, c, from, to, out tc, out dur, out _, MxConst.kOverlapMargin, null);

        // First interval in [from, to] where the plan's box overlaps any other puppet's planned box (see ScanStart). Synchronous: bounded
        // by the per-update check budget. A check the budget cuts returns true ("unknown" = a conflict at `from` for kGuardRetry s,
        // who = null): callers never commit a plan that was not checked to its end and retry later.
        public static bool FindConflict(Puppet me, in MachinePlan plan, PlanContext c, double from, double to, out double tc, out double dur,
                                        out Puppet who, float margin, Puppet ignore)
        {
            var s = s_Sync;
            if (MxBudget.CheckExhausted)
            {
                MxPerf.Count(MxC.ScanAborted);
                tc = from; dur = MxConst.kGuardRetry; who = null;
                return true;
            }
            long t0 = MxBudget.Start();
            ScanStart(s, me, plan, c, from, to, margin, ignore);
            var st = ScanRun(s, c, MxBudget.CheckDeadline(t0));
            MxBudget.StopCheck(t0);
            tc = s.Tc; dur = s.Dur; who = s.Who;
            s.Me = s.Ignore = s.Who = null;
            s.Near.Clear();
            if (st == ScanState.Running)
            {
                s.State = ScanState.Idle;
                MxPerf.Count(MxC.ScanAborted);
                tc = from; dur = MxConst.kGuardRetry; who = null;
                return true;
            }
            return st == ScanState.Conflict;
        }

        // ------------------------------------------------------------ resumable scan

        // Starts a scan of candidate `plan` of `me` over [from, to] (no step is run yet). A neighbour that is already inside the margin at
        // `from` (two machines parked a little closer than 2 x margin) is checked with the guard margin instead, so the pre-existing
        // closeness never blocks every plan - true contact still counts.
        public static void ScanStart(ConflictScan s, Puppet me, in MachinePlan plan, PlanContext c, double from, double to, float margin, Puppet ignore)
        {
            MxPerf.Count(MxC.ScanStarts);
            s.Me = me; s.Plan = plan; s.From = from; s.To = to; s.Margin = margin; s.Ignore = ignore;
            s.Runs = 0;
            ScanInit(s, c);
        }

        // (Re)initialises the scan at its first step: prefilter of the neighbours (machines that stay far from the route: the segment
        // between the start, middle and end poses + 150 m), their margins, skip-ahead state and cache generations.
        static void ScanInit(ConflictScan s, PlanContext c)
        {
            s.Tc = 0; s.Dur = 0; s.Who = null; s.InConf = false; s.TLast = s.From;
            s.Near.Clear(); s.NearMargin.Clear(); s.NearNext.Clear(); s.NearV.Clear(); s.NearGen.Clear(); s.NearGap.Clear(); s.NearSep.Clear();
            s.State = ScanState.Clear;
            var me = s.Me;
            if (s.To <= s.From || c.Others.Count == 0) return;
            if (!BoxAt(me, s.Plan, c, s.From, out var a0, out var sa0) || !BoxAt(me, s.Plan, c, s.To, out var a1, out _)) return;
            if (!BoxAt(me, s.Plan, c, 0.5 * (s.From + s.To), out var am, out _)) am = a0;
            // The other puppets' committed plans come from their sample caches on the absolute kSampleStep grid
            // (filled once per plan, shared by every attempt / caller); only the candidate plan is evaluated here
            long k0 = PlanSamples.Floor(s.From);
            float vMe = VBound(me);
            for (int i = 0; i < c.Others.Count; i++)
            {
                var o = c.Others[i];
                if (o == me || o == s.Ignore || o.Plan.Count <= 0) continue;
                PlanSamples.Validate(o, c.Clk, c.Now);
                if (!PlanSamples.BoxAt(o, k0, c, out var ob, out var sb0)) continue;
                if (math.min(DistToSegment(ob.C, a0.C, am.C), DistToSegment(ob.C, am.C, a1.C)) >= 150f) continue;
                s.Near.Add(o);
                s.NearMargin.Add(s.Margin > MxConst.kGuardMargin && PairHit(me, sa0, a0, o, sb0, ob, s.Margin) ? MxConst.kGuardMargin : s.Margin);
                s.NearNext.Add(k0);
                s.NearV.Add(vMe + VBound(o));
                s.NearGen.Add(o.Samples.Gen);
                float g0 = me.PostSite ? Gap(a0, ob) : float.PositiveInfinity;
                s.NearGap.Add(g0 < 0f ? g0 : float.PositiveInfinity);
                s.NearSep.Add(!(g0 < 0f));
            }
            if (s.Near.Count == 0) return;
            MxPerf.Count(MxC.FcNear, s.Near.Count);
            s.K0 = k0;
            s.K1 = PlanSamples.Ceil(s.To);
            s.K = k0;
            s.Stride = math.max(1, (int)math.ceil((s.To - s.From) / 480.0 / PlanSamples.Step - 1e-9));
            s.State = ScanState.Running;
        }

        // Advances the scan until it ends (Clear / Conflict: Tc, Dur, Who) or the Stopwatch passes `deadline` (Running: call again in a
        // later update). Skip-ahead: a neighbour whose box is farther away than the two can close before step n is tested
        // again at step n only (no candidate box, no sample fill of the neighbour in between); when no neighbour is due the candidate
        // jumps to the earliest due step. Exact: a skipped step can never hold a contact (VBound / SkipSteps).
        public static ScanState ScanRun(ConflictScan s, PlanContext c, long deadline)
        {
            if (s.State != ScanState.Running) return s.State;
            MxPerf.Begin(MxT.X_FindConflict);
            s.Runs++;
            if (s.Runs > 1 && !NeighboursUnchanged(s, c))
            {
                MxPerf.Count(MxC.ScanRestarts);
                ScanInit(s, c);
                if (s.State != ScanState.Running) { MxPerf.End(MxT.X_FindConflict); return s.State; }
            }
            // a resumed scan does not test the steps that are past by now (no contact can happen there any more)
            if (s.Runs > 1 && !s.InConf)
            {
                long kn = PlanSamples.Floor(c.Now);
                if (kn > s.K) s.K = s.K0 + (kn - s.K0) / s.Stride * s.Stride;
            }
            var me = s.Me;
            int steps = 0;
            while (s.K <= s.K1)
            {
                // (the first check comes after kScanCheckEvery steps: every Run makes progress, so a scan always ends)
                if (deadline != NoDeadline && ++steps % kScanCheckEvery == 0 && Stopwatch.GetTimestamp() >= deadline)
                {
                    MxPerf.Count(MxC.ScanSlices);
                    MxPerf.End(MxT.X_FindConflict);
                    return ScanState.Running;
                }
                long k = s.K;
                long due = long.MaxValue;
                for (int i = 0; i < s.NearNext.Count; i++) due = math.min(due, s.NearNext[i]);
                if (due > k)
                {
                    // no neighbour can touch at k: no contact here (ends a running conflict)
                    if (s.InConf) { s.Dur = PlanSamples.Time(k) - s.Tc; return Finish(s, ScanState.Conflict); }
                    if (due > s.K1) break;
                    s.K = math.max(k + 1, s.K0 + (due - s.K0 + s.Stride - 1) / s.Stride * s.Stride);
                    continue;
                }
                MxPerf.Count(MxC.FcSteps);
                double t = PlanSamples.Time(k);
                s.TLast = t;
                if (!BoxAt(me, s.Plan, c, t, out var a, out var sa)) { s.K += s.Stride; continue; }
                bool hit = false;
                for (int i = 0; i < s.Near.Count && !hit; i++)
                {
                    if (s.NearNext[i] > k) continue;
                    MxPerf.Count(MxC.FcPairTests);
                    var o = s.Near[i];
                    if (!PlanSamples.BoxAt(o, k, c, out var bb, out var sb)) { s.NearNext[i] = k + s.Stride; continue; }
                    if (PairHit(me, sa, a, o, sb, bb, s.NearMargin[i]))
                    {
                        if (!s.NearSep[i])
                        {
                            // a pre-existing contact of a leaver: allowed while it does not deepen (and for at most kSepAllowSeconds)
                            float g = Gap(a, bb);
                            if (g >= s.NearGap[i] - kSepTolerance && t - s.From <= kSepAllowSeconds) { s.NearGap[i] = math.max(s.NearGap[i], g); s.NearNext[i] = k + s.Stride; continue; }
                        }
                        hit = true; if (!s.InConf) s.Who = o; s.NearNext[i] = k + s.Stride; continue;
                    }
                    s.NearSep[i] = true;
                    s.NearNext[i] = k + math.max(s.Stride, SkipSteps(a, bb, s.NearMargin[i], s.NearV[i]));
                }
                if (hit && !s.InConf) { s.InConf = true; s.Tc = t; }
                else if (!hit && s.InConf) { s.Dur = t - s.Tc; return Finish(s, ScanState.Conflict); }
                s.K += s.Stride;
            }
            if (s.InConf) { s.Dur = math.max(MxConst.kOverlapStep, math.max(s.To, s.TLast) - s.Tc); return Finish(s, ScanState.Conflict); }
            return Finish(s, ScanState.Clear);
        }

        static ScanState Finish(ConflictScan s, ScanState st)
        {
            s.State = st;
            MxPerf.End(MxT.X_FindConflict);
            MxPerf.CountIf(st == ScanState.Conflict, MxC.FcConflicts);
            return st;
        }

        // A resumed scan is valid only against the committed plans it started on: every neighbour still registered, its sample cache
        // not rebuilt (Gen: a new plan, track / clock change or a full re-anchor drop). A partial re-anchor drop keeps the generation
        // (the kept samples are within 2 x kReanchorKeepDrift).
        static bool NeighboursUnchanged(ConflictScan s, PlanContext c)
        {
            var all = MachineRegistry.All;
            for (int i = 0; i < s.Near.Count; i++)
            {
                var o = s.Near[i];
                if (o.Plan.Count <= 0 || !all.Contains(o)) return false;
                PlanSamples.Validate(o, c.Clk, c.Now);
                if (o.Samples.Gen != s.NearGen[i]) return false;
            }
            return true;
        }

        // Machine time up to which candidate `cand` (built at cand.Epoch from the committed plan `old`) may still replace `old`: both
        // agree up to the candidate's cut (Begin copied the running leg up to its natural end) and on while both hold the same pose.
        public static double CommitBy(in MachinePlan cand, in MachinePlan old)
        {
            double now = cand.Epoch;
            if (cand.Count <= 0) return now;
            int i = MachineMotion.ActiveLeg(cand, 0.0);
            var l = cand.Get(i);
            double cut = now;
            if (cand.Epoch + l.T0 < now - 1e-4)
            {
                // the copied running leg: the candidate departs from the committed plan at its end
                if (l.OpenEnded) return double.PositiveInfinity;
                cut = cand.Epoch + l.T1;
                if (i + 1 >= cand.Count) return cut;
                l = cand.Get(i + 1);
            }
            if (old.Count <= 0 || l.Kind != (byte)LegKind.Hold) return cut;
            var lo = old.Get(MachineMotion.ActiveLeg(old, cut - old.Epoch + 1e-4));
            if (lo.Kind != (byte)LegKind.Hold || lo.Facing != l.Facing || math.abs(lo.U0 - l.U0) > 0.01f || math.abs(lo.L0 - l.L0) > 0.01f) return cut;
            double e0 = lo.OpenEnded ? double.PositiveInfinity : old.Epoch + lo.T1;
            double e1 = l.OpenEnded ? double.PositiveInfinity : cand.Epoch + l.T1;
            return math.max(cut, math.min(e0, e1) - 0.02);
        }

        // The committed plan's FrontA model was re-anchored in place while the candidate's scan ran: the candidate's references to that
        // model (FrontB of the copied running leg, its own FrontA when it was the same) take the new one, so the copied leg continues
        // exactly (the candidate's own legs move by far less than the re-anchor tolerance).
        public static void RefreshModels(ref MachinePlan cand, PlanPending pd, in MachinePlan cur)
        {
            if (pd.BaseA.SameAs(cur.FrontA)) return;
            if (cand.FrontB.SameAs(pd.BaseA)) cand.FrontB.Model = cur.FrontA.Model;
            if (cand.FrontA.SameAs(pd.BaseA)) cand.FrontA.Model = cur.FrontA.Model;
        }

        // A candidate whose scan outlived it: the next one holds a departure lead (0.5 s, doubling up to 4 s; reset on a commit).
        public static void ScanExpired(Puppet p)
        {
            MxPerf.Count(MxC.ScanExpired);
            p.Pend.Drop();
            p.ScanLead = math.min(4f, math.max(0.5f, p.ScanLead * 2f));
        }
    }
}
