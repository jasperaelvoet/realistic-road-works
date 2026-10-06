using System;
using System.Diagnostics;
using Unity.Mathematics;

// Shared caches and counters of the Machines module.
//  * PlanSampleCache / PlanSamples: per puppet, the TRUE box + chain state of its COMMITTED plan on the absolute machine-time
//    grid k * kSampleStep, filled lazily and keyed by a fingerprint of everything the pose reads (plan legs + front models,
//    clock, track slot / revision, box). The overlap planner (FindConflict / PairConflict), the grader yield, the separation
//    guard's broad phase and the machine report read other puppets' plans from it instead of re-evaluating State + Pose for
//    every candidate attempt. Plans are deterministic functions of time, so a sample stays valid until the fingerprint changes.
//  * MxBudget: the global truck planning budget per update (time-sliced PlanTruckOutbound / PlanTruckReturn).
//  * MxSpeed: the speed / acceleration invariant (observed chain-space motion per puppet vs MachineLimits) and the motion
//    counters (clampedLegs, overWork, frontLag, emergencyBrakes, violations, hand-overs) for rrw.mx.check / rrw.check.
namespace RealisticRoadWorks.V3.Machines
{
    public sealed class PlanSampleCache
    {
        public ulong Key;
        public bool Valid;
        public long Base;                       // grid index of slot 0
        public readonly Choreo.Obb[] Box = new Choreo.Obb[MxConst.kSampleCap];
        public readonly float[] U = new float[MxConst.kSampleCap], Lat = new float[MxConst.kSampleCap];
        public readonly float[] Hu = new float[MxConst.kSampleCap], Hl = new float[MxConst.kSampleCap], Spd = new float[MxConst.kSampleCap];
        public readonly byte[] Flag = new byte[MxConst.kSampleCap];   // 1 = filled, 2 = box ok, 4 = zones filled
        public readonly byte[] Kind = new byte[MxConst.kSampleCap], Anim = new byte[MxConst.kSampleCap];
        public readonly RoadZones[] Zones = new RoadZones[MxConst.kSampleCap];
        public float ZTrim0 = float.NaN, ZTrim1 = float.NaN;    // trims the cached zones were classified with
        // An in-place front re-anchor keeps the near samples (the plan moves < kReanchorKeepDrift there) and drops only
        // the samples from this machine time on (NaN = nothing pending; -inf = every sample)
        public double StaleFrom = double.NaN;
        // The front model (FrontA) the cache's reference samples were computed with (set at every full
        // rebuild). Every kept sample before FarFrom was computed with a model within kReanchorKeepDrift of Ref at its time, so a
        // kept sample is never more than 2 x kReanchorKeepDrift off the current model (no drift build-up over many re-anchors).
        // Samples from FarFrom on were refilled after a partial drop (another model): the next re-anchor drops them again.
        public FrontRef Ref;
        public bool HasRef;
        public double FarFrom = double.NaN;
        // generation of the cache contents: bumped by every full clear (resumable conflict scans restart when a neighbour's changes)
        public int Gen;
    }

    public static class PlanSamples
    {
        public const float Step = MxConst.kSampleStep;
        public const int Cap = MxConst.kSampleCap;

        public static long Ceil(double tau) => (long)math.ceil(tau / Step - 1e-9);
        public static long Floor(double tau) => (long)math.floor(tau / Step + 1e-9);
        public static double Time(long k) => k * (double)Step;

        // FNV-1a style fingerprint of every input of a plan's pose except the time (no trims: see MachineReport).
        public static ulong Fingerprint(Puppet p, in MachinePlan plan, TrackData td, in ClockData clk)
        {
            ulong h = 1469598103934665603UL;
            Mix(ref h, (ulong)math.asulong(plan.Epoch));
            Mix(ref h, (uint)plan.Count | ((uint)(plan.Track & 0xFFFF) << 8));
            for (int i = 0; i < plan.Count; i++)
            {
                var l = plan.Get(i);
                Mix(ref h, l.T0); Mix(ref h, l.T1);
                Mix(ref h, (uint)l.Kind | ((uint)l.Anim << 8) | ((uint)(byte)l.Facing << 16) | ((uint)(byte)l.Move << 24));
                Mix(ref h, l.U0); Mix(ref h, l.U1); Mix(ref h, l.L0); Mix(ref h, l.L1);
                Mix(ref h, l.Vmax); Mix(ref h, l.Acc); Mix(ref h, l.Lo); Mix(ref h, l.Hi);
                Mix(ref h, l.P0); Mix(ref h, l.P1); Mix(ref h, l.P2); Mix(ref h, l.Odo0);
                Mix(ref h, l.Cu); Mix(ref h, l.Cl); Mix(ref h, l.CatchDur); Mix(ref h, (uint)l.Front);
                Mix(ref h, l.Cv); Mix(ref h, l.VDur); Mix(ref h, l.RetU); Mix(ref h, l.RetV); Mix(ref h, l.RetT);
                Mix(ref h, l.Rate); Mix(ref h, l.RateU);
            }
            MixFront(ref h, plan.FrontA);
            MixFront(ref h, plan.FrontB);
            // The front MODELS are not part of the key. The Director re-anchors them every few seconds (in place, the
            // plan moves by ~0); Reanchored drops the samples the re-anchor moved (StaleFrom, measured against the cache's Ref model)
            Mix(ref h, (uint)p.ModelGen);
            Mix(ref h, plan.HalfLen); Mix(ref h, plan.HalfWid); Mix(ref h, plan.FootOff);
            Mix(ref h, (ulong)math.asulong(clk.Tau0)); Mix(ref h, clk.Frame0); Mix(ref h, clk.Scale);
            Mix(ref h, td == null ? 0xFFFFFFFFu : (uint)td.Revision);
            Mix(ref h, td == null ? 0xFFFFFFFFu : (uint)td.Slot);
            Mix(ref h, td == null ? 0u : td.ProjectId);
            Mix(ref h, td != null && td.Valid ? 1u : 0u);
            Mix(ref h, p.BoxOffZ); Mix(ref h, p.BoxHalfLen); Mix(ref h, p.BoxHalfWid);
            return h;
        }

        static void MixFront(ref ulong h, in FrontRef f)
        {
            Mix(ref h, (uint)f.Phase | ((uint)f.Swap << 8) | ((uint)f.Crews << 16) | ((uint)f.Crew << 24));
            Mix(ref h, f.PStart); Mix(ref h, f.PEnd); Mix(ref h, f.U); Mix(ref h, f.Floor);
            if (f.Upgrade != 0) { Mix(ref h, 0x5577u); Mix(ref h, f.Base); Mix(ref h, f.PMin); Mix(ref h, (uint)f.C4Stage); }   // a mode H band front
        }

        public static void Mix(ref ulong h, float v) => Mix(ref h, math.asuint(v));
        public static void Mix(ref ulong h, uint v) => Mix(ref h, (ulong)v);
        public static void Mix(ref ulong h, ulong v)
        {
            h ^= v;
            h *= 1099511628211UL;
            h ^= h >> 29;
        }

        static TrackData TrackOf(Puppet p)
        {
            var td = p.Track;
            if (td == null || !td.Valid) MachineTrackStore.TryGet(p.ProjectId, out td);
            return td;
        }

        // Validates p's cache against its committed plan (call once per query session: per FindConflict neighbour, per guard /
        // report pass). `now` keeps the window anchored: slot 0 stays <= 8 s before now (older samples are dropped by a shift).
        public static void Validate(Puppet p, in ClockData clk, double now)
        {
            var sc = p.Samples;
            ulong key = Fingerprint(p, p.Plan, TrackOf(p), clk);
            long kn = Floor(now) - 16;
            if (!sc.Valid || sc.Key != key || double.IsNegativeInfinity(sc.StaleFrom))
            {
                MxPerf.Count(MxC.SampleRebuilds);
                sc.Key = key;
                sc.Valid = true;
                sc.Base = kn;
                sc.StaleFrom = double.NaN;
                sc.Ref = p.Plan.FrontA;
                sc.HasRef = true;
                sc.FarFrom = double.NaN;
                sc.Gen++;
                Array.Clear(sc.Flag, 0, Cap);
                return;
            }
            long shift = kn - sc.Base;
            if (shift >= 64) Shift(sc, shift);
            else if (shift < -Cap / 2) { sc.Base = kn; Array.Clear(sc.Flag, 0, Cap); }
            if (!double.IsNaN(sc.StaleFrom))
            {
                // Partial invalidation after an in-place re-anchor (only the far samples moved); the samples from here on
                // are refilled with the current model (see FarFrom)
                long ks = Ceil(sc.StaleFrom) - sc.Base;
                if (ks < 0) ks = 0;
                if (ks < Cap) { Array.Clear(sc.Flag, (int)ks, Cap - (int)ks); MxPerf.Count(MxC.SamplePartialDrops); }
                sc.FarFrom = double.IsNaN(sc.FarFrom) ? sc.StaleFrom : math.min(sc.FarFrom, sc.StaleFrom);
                sc.StaleFrom = double.NaN;
            }
        }

        // The Director re-anchored p's front model in place (p.Plan.FrontA.Model is the new one).
        //  * no leg from now on reads FrontA (trucks on Drive / Turn / Hold legs, legs copied onto FrontB): the plan cannot move -> kept;
        //  * else the samples stay up to the time the new model leaves kReanchorKeepDrift of the cache's REFERENCE model (Ref, the
        //    model of the last full rebuild; not the previous re-anchor's model, so the drift never builds up) and before FarFrom (the
        //    samples refilled after an earlier partial drop were computed with another model) -> partial;
        //  * that near part shorter than kReanchorKeepMin: every sample is dropped and the next Validate takes the new model as its
        //    reference (a real speed change; hopping roles re-plan then anyway) -> full.
        // Nothing is refilled here: the consumers refill on demand (the conflict scans inside their time budgets). A report cache
        // holding samples of finite FrontA legs past the kept part is marked for a (rate-limited) rebuild.
        public static void Reanchored(Puppet p, in ClockData clk, double now)
        {
            var plan = p.Plan;
            if (!ReadsFrontA(plan, now)) { MxPerf.Count(MxC.ReanchorKept); return; }
            var sc = p.Samples;
            double drop;
            if (!sc.Valid || !sc.HasRef) drop = double.NegativeInfinity;
            else
            {
                double keep = KeepUntil(sc.Ref, plan.FrontA, clk, now);
                drop = double.IsNaN(sc.FarFrom) ? keep : math.min(keep, sc.FarFrom);
                if (drop - now < MxConst.kReanchorKeepMin) drop = double.NegativeInfinity;
            }
            if (double.IsNegativeInfinity(drop))
            {
                MxPerf.Count(MxC.ReanchorFull);
                sc.StaleFrom = double.NegativeInfinity;
            }
            else if (drop >= now + KeepHorizon - 1.0)
            {
                MxPerf.Count(MxC.ReanchorKept);
                return;
            }
            else
            {
                MxPerf.Count(MxC.ReanchorPartial);
                sc.StaleFrom = double.IsNaN(sc.StaleFrom) ? drop : math.min(sc.StaleFrom, drop);
            }
            if (p.ReportCacheValid && p.ReportFrontUntil > drop) p.ReportSoftStale = true;
        }

        const double KeepHorizon = 512.0;

        // Does any leg of the plan from `now` on read the FrontA model (front-relative leg with Front == 0)?
        public static bool ReadsFrontA(in MachinePlan plan, double now)
        {
            if (plan.Count <= 0) return false;
            for (int i = MachineMotion.ActiveLeg(plan, now - plan.Epoch); i < plan.Count; i++)
            {
                var l = plan.Get(i);
                if (l.Front == 0 && Choreo.FrontDep(l)) return true;
            }
            return false;
        }

        // Machine time up to which the front of model b stays within kReanchorKeepDrift of model a (checked at now and at doubling
        // steps up to KeepHorizon s: the difference of two re-anchored linear models is affine in time, so the set where it stays
        // within the tolerance is an interval).
        public static double KeepUntil(in FrontRef a, in FrontRef b, in ClockData clk, double now)
        {
            if (math.abs(MachineMotion.Front(a, clk, now) - MachineMotion.Front(b, clk, now)) > MxConst.kReanchorKeepDrift) return now;
            double keep = now;
            for (double h = 1.0; h <= KeepHorizon; h *= 2.0)
            {
                double t = now + h;
                if (math.abs(MachineMotion.Front(a, clk, t) - MachineMotion.Front(b, clk, t)) > MxConst.kReanchorKeepDrift) break;
                keep = t;
            }
            return keep;
        }

        static void Shift(PlanSampleCache sc, long shift)
        {
            if (shift >= Cap) { sc.Base += shift; Array.Clear(sc.Flag, 0, Cap); return; }
            int d = (int)shift, n = Cap - d;
            Array.Copy(sc.Box, d, sc.Box, 0, n);
            Array.Copy(sc.U, d, sc.U, 0, n); Array.Copy(sc.Lat, d, sc.Lat, 0, n);
            Array.Copy(sc.Hu, d, sc.Hu, 0, n); Array.Copy(sc.Hl, d, sc.Hl, 0, n); Array.Copy(sc.Spd, d, sc.Spd, 0, n);
            Array.Copy(sc.Flag, d, sc.Flag, 0, n);
            Array.Copy(sc.Kind, d, sc.Kind, 0, n); Array.Copy(sc.Anim, d, sc.Anim, 0, n);
            Array.Copy(sc.Zones, d, sc.Zones, 0, n);
            Array.Clear(sc.Flag, n, d);
            sc.Base += shift;
        }

        // Slot of grid index k (filled on demand). -1 when k lies outside the cached window (caller evaluates directly).
        static int Slot(Puppet p, long k, PlanContext c)
        {
            var sc = p.Samples;
            long i = k - sc.Base;
            if (i < 0 || i >= Cap)
            {
                if (i < 0) return -1;
                Shift(sc, i - (Cap - 1));
                i = k - sc.Base;
                if (i < 0 || i >= Cap) return -1;
            }
            int s = (int)i;
            if ((sc.Flag[s] & 1) == 0)
            {
                MxPerf.Count(MxC.SampleFills);
                MxPerf.Begin(MxT.P_Cache);
                bool ok = Choreo.BoxAt(p, p.Plan, c, Time(k), out var box, out var st);
                if (!ok) MachineMotion.State(p.Plan, c.Clk, Time(k), out st);
                MxPerf.End(MxT.P_Cache);
                sc.Box[s] = box;
                sc.U[s] = st.U; sc.Lat[s] = st.Lat; sc.Hu[s] = st.Hu; sc.Hl[s] = st.Hl; sc.Spd[s] = st.Speed;
                sc.Kind[s] = st.LegKind; sc.Anim[s] = st.Anim;
                sc.Flag[s] = (byte)(1 | (ok ? 2 : 0));
            }
            else MxPerf.Count(MxC.SampleHits);
            return s;
        }

        // True box + the chain state fields the pair rules read (LegKind, Anim, Speed, U, Lat, heading) at grid index k of p's
        // committed plan. Validate(p) first.
        public static bool BoxAt(Puppet p, long k, PlanContext c, out Choreo.Obb box, out ChainState st)
        {
            st = default;
            int s = Slot(p, k, c);
            if (s < 0) return Choreo.BoxAt(p, p.Plan, c, Time(k), out box, out st);
            var sc = p.Samples;
            box = sc.Box[s];
            st.U = sc.U[s]; st.Lat = sc.Lat[s]; st.Hu = sc.Hu[s]; st.Hl = sc.Hl[s]; st.Speed = sc.Spd[s];
            st.LegKind = sc.Kind[s]; st.Anim = sc.Anim[s];
            return (sc.Flag[s] & 2) != 0;
        }

        // Machine-report zones of p's committed plan at grid index k (MachineReport.ZoneOf of the cached chain state).
        public static RoadZones ZonesAt(Puppet p, long k, PlanContext c, TrackData td, float trim0, float trim1)
        {
            var sc = p.Samples;
            if (sc.ZTrim0 != trim0 || sc.ZTrim1 != trim1)
            {
                for (int i = 0; i < Cap; i++) sc.Flag[i] &= 3;
                sc.ZTrim0 = trim0; sc.ZTrim1 = trim1;
            }
            int s = Slot(p, k, c);
            if (s < 0)
            {
                MachineMotion.State(p.Plan, c.Clk, Time(k), out var st);
                return MachineReport.ZoneOf(p, st.U, st.Lat, st.Hu, st.Hl, td, trim0, trim1);
            }
            if ((sc.Flag[s] & 4) == 0)
            {
                sc.Zones[s] = MachineReport.ZoneOf(p, sc.U[s], sc.Lat[s], sc.Hu[s], sc.Hl[s], td, trim0, trim1);
                sc.Flag[s] |= 4;
            }
            return sc.Zones[s];
        }
    }

    // Global planning budget of one Director update: truck planning attempts stop once kPlanBudgetMs is used
    // and continue next update (the truck holds its current leg end: an intended wait, not stuck).
    // The budget is a DEADLINE inside the work too (Deadline: the resumable conflict scans stop on it
    // mid-scan), and a second pool bounds the synchronous checks (release check, back-outs, leavers, grader guard / yield).
    public static class MxBudget
    {
        static long s_Used, s_CheckUsed;
        static readonly long s_Limit = (long)(MxConst.kPlanBudgetMs * Stopwatch.Frequency / 1000.0);
        static readonly long s_CheckLimit = (long)(MxConst.kCheckBudgetMs * Stopwatch.Frequency / 1000.0);
        public static int Deferred, DeferredTotal;

        public static void Reset() { s_Used = 0; s_CheckUsed = 0; Deferred = 0; }
        public static bool Exhausted => s_Used >= s_Limit;
        public static long Start() => Stopwatch.GetTimestamp();
        public static void Stop(long t0) => s_Used += Stopwatch.GetTimestamp() - t0;
        // Stopwatch time at which the planning budget runs out for work started at t0 (Start)
        public static long Deadline(long t0) => t0 + math.max(0L, s_Limit - s_Used);
        public static bool CheckExhausted => s_CheckUsed >= s_CheckLimit;
        public static long CheckDeadline(long t0) => t0 + math.max(0L, s_CheckLimit - s_CheckUsed);
        public static void StopCheck(long t0) => s_CheckUsed += Stopwatch.GetTimestamp() - t0;
        public static double UsedMs => s_Used * 1000.0 / Stopwatch.Frequency;
        public static void Defer() { Deferred++; DeferredTotal++; MxPerf.Count(MxC.PlanDeferred); }
    }

    // Observed motion per puppet against its MachineLimits, plus the motion counters.
    public static class MxSpeed
    {
        public static long ClampedLegs, OverWork, FrontLag, EmergencyBrakes, SpeedViolations, AccelViolations, Samples;
        public static long HandKept, HandRemapped, HandLeft, HandAnchorSpawns, CrewLodSkips, HandLate, FeedCutAtBoundary;
        public static long RunUps;              // drives that took a straight run-up before a lane change (short u distance)   // HandLate: late relays (anchor in view)
        // runaway guard (MxConst.kRunawayFactor): puppets stopped because they moved far faster than their role ever may, and
        // speeds above that limit that were not carried into a new plan (a jump in the old plan). Both must stay 0.
        public static long Runaways, RunawayClamps;
        public static string FirstSpeed = "", FirstAccel = "", LastSpeed = "", LastAccel = "", LastLag = "", LastClamp = "";
        public static string FirstRunaway = "", FirstRunawayClamp = "";
        public static readonly float[] RoleMaxV = new float[(int)MachineRole.Count + 1];
        public static readonly float[] RoleMaxA = new float[(int)MachineRole.Count + 1];
        public static readonly float[] RoleMaxVRatio = new float[(int)MachineRole.Count + 1];
        public static readonly int[] RoleViolV = new int[(int)MachineRole.Count + 1];
        public static readonly int[] RoleViolA = new int[(int)MachineRole.Count + 1];

        public static void Reset()
        {
            ClampedLegs = OverWork = FrontLag = EmergencyBrakes = SpeedViolations = AccelViolations = Samples = 0;
            HandKept = HandRemapped = HandLeft = HandAnchorSpawns = CrewLodSkips = HandLate = FeedCutAtBoundary = 0;
            RunUps = 0;
            Runaways = RunawayClamps = 0;
            FirstSpeed = FirstAccel = LastSpeed = LastAccel = LastLag = LastClamp = "";
            FirstRunaway = FirstRunawayClamp = "";
            Array.Clear(RoleMaxV, 0, RoleMaxV.Length);
            Array.Clear(RoleMaxA, 0, RoleMaxA.Length);
            Array.Clear(RoleMaxVRatio, 0, RoleMaxVRatio.Length);
            Array.Clear(RoleViolV, 0, RoleViolV.Length);
            Array.Clear(RoleViolA, 0, RoleViolA.Length);
            foreach (var p in MachineRegistry.All) { p.MaxV = p.MaxA = 0f; p.ViolV = p.ViolA = 0; }
        }

        // A leg that needed more than the role's limits was stretched / slowed (counted; the text is built only when verbose).
        public static void Clamp(Puppet p, string what, float from, float to)
        {
            ClampedLegs++;
            if (RRWLog.VerboseEnabled)
            {
                LastClamp = p + " " + what + " " + RRWLog.F(from) + " -> " + RRWLog.F(to);
                RRWLog.Verbose("machines: clamped leg " + LastClamp);
            }
        }

        // Speed limit of the motion state s of p (the leg's limit: reverse / K-turn / leaving).
        public static float VLimit(Puppet p, in ChainState s, bool reversing)
        {
            bool turning = s.LegKind == (byte)LegKind.Turn;
            bool leaving = p.PostSite || p.Outgoing;
            return p.Lim.VFor(reversing, turning, leaving);
        }

        public static float ALimit(Puppet p, in ChainState s) =>
            s.LegKind == (byte)LegKind.Brake ? math.max(p.Lim.Accel, RRWConst.kEmergencyDecel) : p.Lim.Accel;

        // Forget the motion history (spawn, respawn at another pose): the next sample starts a new chord.
        public static void Restart(Puppet p) { p.MonTau = double.NaN; p.MonVTau = double.NaN; p.RunawaySamples = 0; }

        // Fastest the role may ever move (forward, reversing or leaving) x kRunawayFactor: anything faster is a jump in a plan.
        public static float RunawaySpeed(in MachineLimits lim) =>
            MxConst.kRunawayFactor * math.max(0.1f, math.max(math.max(lim.VFwd, lim.VRev), lim.VLeave));

        // A speed carried from the motion a new plan continues (velocity match, settle brake). Above RunawaySpeed (or NaN) it is
        // not motion but a jump in the old plan: the new plan starts from rest instead (counted, logged once per puppet).
        public static float Carried(Puppet p, float v, in MachineLimits lim, string where)
        {
            float cap = RunawaySpeed(lim);
            if (!float.IsNaN(v) && math.abs(v) <= cap) return v;
            RunawayClamps++;
            string what = (p != null ? p.ToString() : "a puppet") + " v=" + RRWLog.F(v) + " above " + RRWLog.F(cap) + " m/s (" + where + ")";
            if (FirstRunawayClamp.Length == 0) FirstRunawayClamp = what;
            if (p == null || !p.RunawayLogged)
            {
                if (p != null) p.RunawayLogged = true;
                RRWLog.Info("machines: carried speed of " + what + " is not real motion: the new plan starts from rest");
            }
            return 0f;
        }

        // One observation per puppet per Director update (after the guard, i.e. the motion the mover will write). Chain-space
        // chord speed over >= kMonSpeedWindow and the change of the signed (nose) speed over >= kMonAccelWindow; the limit is the
        // larger of the two leg limits at the chord ends (a chord may straddle a leg boundary).
        // Returns true when p moved faster than RunawaySpeed in two samples in a row (the caller stops it).
        public static bool Observe(Puppet p, double now)
        {
            if (p.Plan.Count <= 0) return false;
            MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
            p.NowU = s.U; p.NowLat = s.Lat;
            if (double.IsNaN(p.MonTau))
            {
                p.MonTau = now; p.MonU = s.U; p.MonLat = s.Lat; p.MonLeg = s.LegKind;
                p.MonVLimit = VLimit(p, s, s.Speed < -0.05f);
                p.MonALimit = ALimit(p, s);
                return false;
            }
            double dt = now - p.MonTau;
            if (dt < MxConst.kMonSpeedWindow) return false;
            float du = s.U - p.MonU, dl = s.Lat - p.MonLat;
            float dist = math.sqrt(du * du + dl * dl);
            float v = (float)(dist / dt);
            bool rev = du * s.Hu + dl * s.Hl < 0f;
            float vlimNow = VLimit(p, s, rev);
            float vlim = math.max(vlimNow, p.MonVLimit);
            float alimNow = ALimit(p, s);
            float alim = math.max(alimNow, p.MonALimit);
            Samples++;
            p.LastV = v; p.LastVLimit = vlim;
            p.MaxV = math.max(p.MaxV, v);
            int r = math.clamp((int)p.Role, 0, (int)MachineRole.Count);
            RoleMaxV[r] = math.max(RoleMaxV[r], v);
            if (vlim > 0f) RoleMaxVRatio[r] = math.max(RoleMaxVRatio[r], v / vlim);
            if (MachineLimits.SpeedViolates(v, vlim))
            {
                SpeedViolations++; p.ViolV++; RoleViolV[r]++;
                LastSpeed = p + " v=" + RRWLog.F(v) + " limit=" + RRWLog.F(vlim) + " leg=" + (LegKind)s.LegKind + " u=" + RRWLog.F(s.U) +
                            (p.PostSite ? " leaving" : " act=" + p.Activity);
                if (FirstSpeed.Length == 0) FirstSpeed = LastSpeed;
                if (SpeedViolations <= 5 || (SpeedViolations & 255) == 0) RRWLog.Info("machines: SPEED violation #" + SpeedViolations + ": " + LastSpeed);
            }
            float vs = rev ? -v : v;
            // the chord speed belongs to the chord's MIDPOINT (chords of unequal length must not skew the acceleration)
            double mid = 0.5 * (now + p.MonTau);
            if (double.IsNaN(p.MonVTau)) { p.MonVTau = mid; p.MonV = vs; }
            else
            {
                double da = mid - p.MonVTau;
                if (da >= MxConst.kMonAccelWindow)
                {
                    float a = (float)((vs - p.MonV) / da);
                    p.LastA = a; p.LastALimit = alim;
                    p.MaxA = math.max(p.MaxA, math.abs(a));
                    RoleMaxA[r] = math.max(RoleMaxA[r], math.abs(a));
                    if (MachineLimits.AccelViolates(a, alim))
                    {
                        AccelViolations++; p.ViolA++; RoleViolA[r]++;
                        LastAccel = p + " a=" + RRWLog.F(a) + " limit=" + RRWLog.F(alim) + " v " + RRWLog.F(p.MonV) + "->" + RRWLog.F(vs) +
                                    " leg=" + (LegKind)s.LegKind + " u=" + RRWLog.F(s.U) + (p.PostSite ? " leaving" : " act=" + p.Activity);
                        if (FirstAccel.Length == 0) FirstAccel = LastAccel;
                        if (AccelViolations <= 5 || (AccelViolations & 255) == 0) RRWLog.Info("machines: ACCEL violation #" + AccelViolations + ": " + LastAccel);
                    }
                    p.MonV = vs; p.MonVTau = mid;
                }
            }
            p.MonTau = now; p.MonU = s.U; p.MonLat = s.Lat; p.MonLeg = s.LegKind;
            p.MonVLimit = vlimNow; p.MonALimit = alimNow;
            p.RunawaySamples = v > RunawaySpeed(p.Lim) ? p.RunawaySamples + 1 : 0;
            return p.RunawaySamples >= 2;
        }

        public static string Summary() =>
            "clampedLegs=" + ClampedLegs + " overWork=" + OverWork + " frontLag=" + FrontLag + " emergencyBrakes=" + EmergencyBrakes +
            " speedViolations=" + SpeedViolations + " accelViolations=" + AccelViolations + " samples=" + Samples +
            " handOver(kept=" + HandKept + " remapped=" + HandRemapped + " left=" + HandLeft + " anchorSpawns=" + HandAnchorSpawns + " late=" + HandLate + ")" + " feedCutAtBoundary=" + FeedCutAtBoundary +
            " planDeferred=" + MxBudget.DeferredTotal + " crewLodSkips=" + CrewLodSkips + " runUps=" + RunUps +
            " runawayStops=" + Runaways + " runawayClamps=" + RunawayClamps;
    }
}
