using Game.Objects;
using Unity.Collections;
using Unity.Mathematics;

// Pure, job-safe machine motion (the plan component). The pose of a puppet is a pure function of
// (MachinePlan, machine time tau, project track): no state, no allocation, no ECS access. MachineMoverSystem evaluates it
// in a job for the 4 TransformFrame slots + Transform; the MachineDirector evaluates the same code on the main thread
// for planning, overlap checks and spawn poses, so planned and rendered motion can never disagree.
namespace RealisticRoadWorks.V3.Machines
{
    // Read-only view of the packed project tracks (MachineTrackStore).
    public struct TrackView
    {
        [ReadOnly] public NativeArray<TrackSample> Samples;
        [ReadOnly] public NativeArray<TrackHeader> Headers;

        public readonly bool IsCreated => Samples.IsCreated && Headers.IsCreated;

        // Track point at chain u: linear between samples (positions extrapolate linearly beyond the ends, heights stay flat).
        public readonly bool Point(int slot, float u, out TrackPoint tp)
        {
            tp = default;
            if (!IsCreated || slot < 0 || slot >= Headers.Length) return false;
            var h = Headers[slot];
            if (h.Valid == 0 || h.Count < 2 || h.Offset < 0 || h.Offset + h.Count > Samples.Length || !(h.Ds > 0f)) return false;
            float x = (u - h.U0) / h.Ds;
            int i;
            float f;
            if (!(x > 0f)) { i = 0; f = float.IsNaN(x) ? 0f : x; }
            else if (x >= h.Count - 1) { i = h.Count - 2; f = x - i; }
            else { i = (int)math.floor(x); f = x - i; }
            var a = Samples[h.Offset + i];
            var b = Samples[h.Offset + i + 1];
            float fc = math.saturate(f);
            float3 p = a.Pos + (b.Pos - a.Pos) * f;
            p.y = math.lerp(a.Pos.y, b.Pos.y, fc);
            tp.Pos = p;
            tp.Right = math.normalizesafe(math.lerp(a.Right, b.Right, fc), new float2(1f, 0f));
            tp.FloorRel = math.lerp(a.FloorRel, b.FloorRel, fc);
            tp.VergeL = Lerp(a.VergeL, b.VergeL, fc);
            tp.VergeR = Lerp(a.VergeR, b.VergeR, fc);
            tp.HalfWidth = math.lerp(a.HalfWidth, b.HalfWidth, fc);
            tp.FlatHalf = math.lerp(a.FlatHalf, b.FlatHalf, fc);
            tp.Flags = fc < 0.5f ? a.Flags : b.Flags;
            return true;
        }

        static float Lerp(float a, float b, float t)
        {
            if (float.IsNaN(a)) return b;
            if (float.IsNaN(b)) return a;
            return math.lerp(a, b, t);
        }

        // World ground height at (u, lat).
        public readonly bool Height(int slot, float u, float lat, out float y)
        {
            y = 0f;
            if (!Point(slot, u, out var tp)) return false;
            float rel = tp.HeightRel(lat);
            y = tp.Pos.y + (float.IsNaN(rel) ? 0f : rel);
            return true;
        }
    }

    public static class MachineMotion
    {
        public const float kFps = 60f;

        public static float Smooth(float x) { x = math.saturate(x); return x * x * (3f - 2f * x); }
        public static float SmoothD(float x) { x = math.saturate(x); return 6f * x * (1f - x); }

        // ------------------------------------------------------------ trapezoid speed profile

        public static float TrapDur(float D, float v, float a)
        {
            if (!(D > 1e-4f)) return 0f;
            a = math.max(1e-3f, a);   // not 0.05: a floor above the planned value would break the leg's limits
            v = math.max(1e-3f, v);
            if (D >= v * v / a) return 2f * (v / a) + (D - v * v / a) / v;
            return 2f * math.sqrt(D / a);
        }

        public static void Trap(float D, float v, float a, float t, out float d, out float spd, out bool brk)
        {
            d = 0f; spd = 0f; brk = false;
            if (!(D > 1e-4f)) return;
            a = math.max(1e-3f, a);
            v = math.max(1e-3f, v);
            float tA, dA, T;
            if (D >= v * v / a) { tA = v / a; dA = 0.5f * v * tA; T = 2f * tA + (D - 2f * dA) / v; }
            else { v = math.sqrt(D * a); tA = v / a; dA = 0.5f * D; T = 2f * tA; }
            if (t <= 0f) return;
            if (t >= T) { d = D; brk = true; return; }
            if (t < tA) { d = 0.5f * a * t * t; spd = a * t; }
            else if (t < T - tA) { d = dA + v * (t - tA); spd = v; }
            else { float r = T - t; d = D - 0.5f * a * r * r; spd = a * r; brk = true; }
            d = math.clamp(d, 0f, D);
        }

        // ------------------------------------------------------------ K-turn timing

        // Forward arcs at vF (= MachineLimits.VTurn), the reverse arc at vR (= min(VTurn, VRev)); each arc is a
        // rest-to-rest trapezoid at the role's acceleration.
        public static void TurnTimes(float R, float vF, float vR, float a, out float t1, out float t2, out float total)
        {
            float arc = R * math.PI / 3f;
            t1 = TrapDur(arc, vF, a);
            t2 = TrapDur(arc, vR > 0f ? vR : MxConst.kReverseSpeed, a);
            total = 2f * t1 + t2 + 2f * MxConst.kTurnPause;
        }

        public static float TurnDuration(float R, float vF, float vR, float a)
        {
            TurnTimes(R, vF, vR, a, out _, out _, out float total);
            return total;
        }

        // ------------------------------------------------------------ front

        public static float Front(in FrontRef fr, in ClockData clk, double tau)
        {
            MxPerf.Motion(MxC.FrontEvals);   // dev rrw.mx.perf (main thread, Director update only; compiled away in release)
            var ph = (WorksPhase)fr.Phase;
            if (fr.Upgrade != 0) return UpgradeFront(fr, clk, tau);
            if (ph == WorksPhase.Complete) return fr.U;
            if (ph == WorksPhase.None || !(fr.U > 0f)) return 0f;
            double frames = (tau - clk.Tau0) * kFps / math.max(1e-4f, clk.Scale);
            double df = (double)unchecked((int)(clk.Frame0 - fr.Model.Frame0)) + frames;
            double p = fr.Model.P0 + fr.Model.PPerFrame * df;
            float ps = fr.PStart, pe = fr.PEnd;
            float f;
            if (pe > ps + 1e-6f)
            {
                if (p < ps) p = ps;
                if (p > pe) p = pe;
                f = (float)((p - ps) / (pe - ps));
            }
            else f = 0f;
            // this crew's section front (one crew: PhasePlan.MainFront exactly)
            return PhasePlan.SectionFront(ph, math.saturate(f), fr.U, fr.Crews, fr.Crew, fr.Swap != 0, fr.Floor);
        }

        // Front of a mode H band crew: the band's equivalent phase fraction from project progress (never below the band's window
        // start), swept over [Base, Base + U] like PhasePlan.UpgradeBandFront (the C4 fronts for the re-marking band under Half).
        static float UpgradeFront(in FrontRef fr, in ClockData clk, double tau)
        {
            var ph = (WorksPhase)fr.Phase;
            if (ph == WorksPhase.Complete) return fr.Base + fr.U;
            if (ph == WorksPhase.None || !(fr.U > 0f)) return fr.Base;
            double frames = (tau - clk.Tau0) * kFps / math.max(1e-4f, clk.Scale);
            double df = (double)unchecked((int)(clk.Frame0 - fr.Model.Frame0)) + frames;
            double p = math.max(fr.Model.P0 + fr.Model.PPerFrame * df, (double)fr.PMin);
            float ps = fr.PStart, pe = fr.PEnd;
            float f = pe > ps + 1e-6f ? (float)((math.clamp(p, ps, pe) - ps) / (pe - ps)) : 0f;
            if (ph == WorksPhase.Finishing && fr.Swap != 0 && fr.C4Stage != 0)
            {
                // the half the Director's stage paints now, never the other one (see FrontRef.C4Stage)
                SweepWindow(ph, f, true, fr.C4Stage, out float a, out float b);
                return fr.Base + fr.U * PhasePlan.Sweep(math.saturate(f), a, b);
            }
            return fr.Base + PhasePlan.MainFront(ph, math.saturate(f), fr.U, fr.Swap != 0, fr.Floor);
        }

        public static float Front(in MachinePlan p, byte sel, in ClockData clk, double tau) =>
            sel == 0 ? Front(p.FrontA, clk, tau) : Front(p.FrontB, clk, tau);

        // Sweep window [a, b] of the phase fraction over which the front crosses its section (mirror of PhasePlan.MainFront;
        // rrw.mx.check "frontModel" compares FrontLin with Front).
        public static void SweepWindow(WorksPhase ph, float f, bool swap, out float a, out float b) => SweepWindow(ph, f, swap, 0, out a, out b);

        // c4Stage (FrontRef.C4Stage): 1 / 2 = the first / second half's sweep with the swap whatever f is; 0 = the half from f.
        public static void SweepWindow(WorksPhase ph, float f, bool swap, byte c4Stage, out float a, out float b)
        {
            switch (ph)
            {
                case WorksPhase.Survey: a = 0.15f; b = 0.95f; return;
                case WorksPhase.Excavation:
                case WorksPhase.Foundation: a = 0f; b = 0.95f; return;
                case WorksPhase.Paving: a = 0.03f; b = 0.95f; return;
                case WorksPhase.Finishing:
                    if (!swap) { a = 0f; b = 0.9f; return; }
                    if (c4Stage == 1 || (c4Stage == 0 && f < RRWConst.kC4SwapF - RRWConst.kStageSwitchTolF)) { a = RRWConst.kC4aPaintF0; b = RRWConst.kC4aPaintF1; return; }
                    a = RRWConst.kC4bPaintF0; b = RRWConst.kC4bPaintF1; return;
                case WorksPhase.BreakUp:
                case WorksPhase.Removal:
                case WorksPhase.Restore: a = 0f; b = 0.9f; return;
                default: a = 0f; b = 1f; return;
            }
        }

        // The front as a clamp of a LINEAR function of machine time: F(tau) = clamp(Flin(tau), Fmin, Fmax) with
        // dFlin/dtau = v (m per machine second; 0 while the project does not progress). Front-relative legs smooth the clamp
        // corners with it (no velocity jump where the front starts / stops). Fmax = the section end, Fmin = the section start
        // (or the C4 front floor).
        public static void FrontLin(in FrontRef fr, in ClockData clk, double tau, out float flin, out float v, out float fmin, out float fmax)
        {
            MxPerf.Motion(MxC.FrontEvals);
            var ph = (WorksPhase)fr.Phase;
            v = 0f;
            if (fr.Upgrade != 0) { UpgradeFrontLin(fr, clk, tau, out flin, out v, out fmin, out fmax); return; }
            if (ph == WorksPhase.Complete) { flin = fmin = fmax = fr.U; return; }
            if (ph == WorksPhase.None || !(fr.U > 0f)) { flin = fmin = fmax = 0f; return; }
            float ps = fr.PStart, pe = fr.PEnd;
            if (!(pe > ps + 1e-6f)) { flin = fmin = fmax = Front(fr, clk, tau); return; }
            double scale = math.max(1e-4f, clk.Scale);
            double frames = (tau - clk.Tau0) * kFps / scale;
            double df = (double)unchecked((int)(clk.Frame0 - fr.Model.Frame0)) + frames;
            double p = fr.Model.P0 + fr.Model.PPerFrame * df;
            double fRaw = (p - ps) / (pe - ps);
            double dfdt = fr.Model.PPerFrame * kFps / scale / (pe - ps);
            bool swap = fr.Swap != 0;
            SweepWindow(ph, (float)math.clamp(fRaw, 0.0, 1.0), swap, out float a, out float b);
            double s = (fRaw - a) / (b - a);
            double dsdt = dfdt / (b - a);
            float sMin = ph == WorksPhase.Finishing && !swap ? math.saturate(math.clamp(fr.Floor, 0f, fr.U) / fr.U) : 0f;
            float sa = 0f, sb = fr.U;
            if (fr.Crews > 1)
            {
                int n = math.min((int)fr.Crews, RRWConst.kMaxCrewsPerProject);
                int i = math.clamp((int)fr.Crew, 0, n - 1);
                sa = PhasePlan.SectionStart(i, n, fr.U);
                sb = PhasePlan.SectionStart(i + 1, n, fr.U);
            }
            float L = sb - sa;
            flin = (float)(sa + L * s);
            v = (float)(L * dsdt);
            fmin = sa + L * sMin;
            fmax = sb;
        }

        // FrontLin of a mode H band front (UpgradeFront): one section [Base, Base + U]; the band's window start (PMin) raises the
        // lower clamp to the front at that progress.
        static void UpgradeFrontLin(in FrontRef fr, in ClockData clk, double tau, out float flin, out float v, out float fmin, out float fmax)
        {
            var ph = (WorksPhase)fr.Phase;
            v = 0f;
            if (ph == WorksPhase.Complete) { flin = fmin = fmax = fr.Base + fr.U; return; }
            if (ph == WorksPhase.None || !(fr.U > 0f)) { flin = fmin = fmax = fr.Base; return; }
            float ps = fr.PStart, pe = fr.PEnd;
            if (!(pe > ps + 1e-6f)) { flin = fmin = fmax = UpgradeFront(fr, clk, tau); return; }
            double scale = math.max(1e-4f, clk.Scale);
            double frames = (tau - clk.Tau0) * kFps / scale;
            double df = (double)unchecked((int)(clk.Frame0 - fr.Model.Frame0)) + frames;
            double p = fr.Model.P0 + fr.Model.PPerFrame * df;
            double fRaw = (p - ps) / (pe - ps);
            double dfdt = fr.Model.PPerFrame * kFps / scale / (pe - ps);
            bool swap = fr.Swap != 0;
            SweepWindow(ph, (float)math.clamp(fRaw, 0.0, 1.0), swap, fr.C4Stage, out float a, out float b);
            double s = (fRaw - a) / (b - a);
            double dsdt = dfdt / (b - a);
            float sMin = ph == WorksPhase.Finishing && !swap ? math.saturate(math.clamp(fr.Floor, 0f, fr.U) / fr.U) : 0f;
            if (fr.PMin > ps)
            {
                double fMin = (fr.PMin - ps) / (pe - ps);
                sMin = math.max(sMin, math.saturate((float)((fMin - a) / (b - a))));
            }
            float L = fr.U;
            flin = (float)(fr.Base + L * s);
            v = (float)(L * dsdt);
            fmin = fr.Base + L * sMin;
            fmax = fr.Base + L;
        }

        public static void FrontLin(in MachinePlan p, byte sel, in ClockData clk, double tau, out float flin, out float v, out float fmin, out float fmax)
        {
            if (sel == 0) FrontLin(p.FrontA, clk, tau, out flin, out v, out fmin, out fmax);
            else FrontLin(p.FrontB, clk, tau, out flin, out v, out fmin, out fmax);
        }

        // Sweep speed (m per machine second) of a front model: the slope of its linear part (also during the lead-in / after the
        // sweep, when the front itself stands; 0 while the project does not progress).
        public static float FrontSpeed(in FrontRef fr, in ClockData clk, double tau)
        {
            FrontLin(fr, clk, tau, out _, out float v, out _, out _);
            return v;
        }

        // Front lag pivot: the front value where a lagging follower starts (the front now, or its section start in the lead-in).
        public static float LagPivot(in FrontRef fr, in ClockData clk, double tau)
        {
            FrontLin(fr, clk, tau, out float flin, out _, out float fmin, out float fmax);
            return math.clamp(flin, fmin, fmax);
        }

        // ------------------------------------------------------------ soft corners

        // Polynomial smooth min / max: between two linear functions whose slopes differ by dv the corner has |d2/dt2| = dv^2 / 2k.
        public static float SMin(float x, float y, float k)
        {
            if (!(k > 1e-4f)) return math.min(x, y);
            float h = math.max(k - math.abs(x - y), 0f) / k;
            return math.min(x, y) - h * h * k * 0.25f;
        }

        public static float SMax(float x, float y, float k) => -SMin(-x, -y, k);

        // Corner width for a slope change dv at acceleration a.
        public static float SoftK(float dv, float a) => a > 1e-4f ? dv * dv / (2f * a) : 0f;

        // Velocity matching term (Cv, VDur): displacement of a velocity offset that decays linearly to 0 over VDur.
        public static float VelTerm(float t, float T)
        {
            if (!(T > 1e-4f) || t <= 0f) return 0f;
            if (t >= T) return 0.5f * T;
            return t - t * t / (2f * T);
        }

        // Anchor of a front-relative leg at leg time lt WITHOUT its catch-up / velocity-match terms: clamp(F + U0, Lo, Hi) with
        // soft corners, the return-aware cap (RetV) and the lag cap (Rate). hiCut lowers Hi (Shuttle stroke range).
        public static float TargetU(in MachineLeg leg, float flin, float vF, float fmin, float fmax, float lt, float hiCut = 0f)
        {
            if (leg.Rate > 0f && leg.Rate < 1f)
            {
                // lag cap: a slowed copy of the front (pivot RateU), clamped to the section like the real one
                flin = leg.RateU + leg.Rate * (flin - leg.RateU);
                vF *= leg.Rate;
            }
            float hiE = math.max(leg.Lo, leg.Hi - hiCut);
            float L = math.min(math.max(leg.Lo, fmin + leg.U0), hiE);
            float H = math.max(L, math.min(hiE, fmax + leg.U0));
            float raw = flin + leg.U0;
            if (!(leg.Acc > 0f)) return math.clamp(raw, L, H);   // legacy (hard clamp)
            float aT = leg.Acc * MxConst.kTargetAccelShare;
            float av = math.abs(vF);
            float k = math.min(SoftK(av, aT), 0.5f * (H - L));
            float x = SMax(SMin(raw, H, k), L, k);
            if (leg.RetV > 0f)
            {
                float kt = leg.RetV / (2f * aT);   // corner of the return line where it reaches home (time units)
                float R = leg.RetU + leg.RetV * SMax(leg.RetT - lt, 0f, kt);
                x = SMin(x, R, SoftK(av + leg.RetV, aT));
            }
            return x;
        }

        // Anchor of a front-relative leg of plan p at leg time lt (no catch-up / velocity terms).
        public static float FollowU(in MachinePlan p, in MachineLeg leg, in ClockData clk, double absStart, float lt, float hiCut = 0f)
        {
            FrontLin(p, leg.Front, clk, absStart + lt, out float flin, out float v, out float fmin, out float fmax);
            return TargetU(leg, flin, v, fmin, fmax, lt, hiCut);
        }

        // ------------------------------------------------------------ evaluation

        public static int ActiveLeg(in MachinePlan p, double t)
        {
            int idx = 0;
            for (int i = p.Count - 1; i >= 0; i--)
            {
                if (p.Get(i).T0 <= t) { idx = i; break; }
            }
            return idx;
        }

        // Chain state at machine time tau.
        public static void State(in MachinePlan p, in ClockData clk, double tau, out ChainState s)
        {
            s = default;
            s.Hu = 1f;
            if (p.Count <= 0) return;
            MxPerf.Motion(MxC.StateEvals);
            double t = tau - p.Epoch;
            int idx = ActiveLeg(p, t);
            var leg = p.Get(idx);
            double lt = t - leg.T0;
            if (lt < 0.0) lt = 0.0;
            if (!leg.OpenEnded)
            {
                double len = math.max(0f, leg.T1 - leg.T0);
                if (lt > len) lt = len;
            }
            s.Leg = idx;
            s.LegKind = leg.Kind;
            bool exact = Eval(p, leg, clk, lt, ref s);
            if (!exact)
            {
                MxPerf.Motion(MxC.StateFrontRel);
                // finite-difference speed along the nose (front-relative legs)
                const double h = 0.1;
                double la = lt - h < 0.0 ? 0.0 : lt - h;
                double lb = lt + h;
                if (!leg.OpenEnded) lb = math.min(lb, math.max(0f, leg.T1 - leg.T0));
                if (lb - la > 1e-4)
                {
                    var a = s; var b = s;
                    Eval(p, leg, clk, la, ref a);
                    Eval(p, leg, clk, lb, ref b);
                    float v = (float)(((b.U - a.U) * s.Hu + (b.Lat - a.Lat) * s.Hl) / (lb - la));
                    s.Speed = v;
                }
            }
        }

        // Fills u/lat/heading/anim (+ speed/braking/odometer when exact). Returns true when the speed is exact.
        static bool Eval(in MachinePlan p, in MachineLeg leg, in ClockData clk, double lt, ref ChainState s)
        {
            float flt = (float)lt;
            sbyte f = leg.Facing >= 0 ? (sbyte)1 : (sbyte)-1;
            s.Anim = leg.Anim;
            s.AnimTime = flt;
            s.AnimParam = 0f;
            s.FromAnim = leg.FromAnim;
            s.Blend = leg.Blend > 0f ? Smooth(flt / leg.Blend) : 1f;
            s.Hu = f;
            s.Hl = 0f;
            s.Lat = leg.L0;
            s.Speed = 0f;
            s.Braking = false;
            s.Odo = leg.Odo0;
            double absStart = p.Epoch + leg.T0;
            float catchK = leg.CatchDur > 0f ? 1f - Smooth(flt / leg.CatchDur) : 0f;
            switch ((LegKind)leg.Kind)
            {
                case LegKind.Hold:
                {
                    s.U = leg.U0;
                    s.Braking = flt < 2f;
                    return true;
                }
                case LegKind.Drive:
                {
                    float D = math.abs(leg.U1 - leg.U0);
                    Trap(D, leg.Vmax, leg.Acc, flt, out float d, out float spd, out bool brk);
                    sbyte mv = leg.Move >= 0 ? (sbyte)1 : (sbyte)-1;
                    s.U = leg.U0 + mv * d;
                    float dl = 0f;
                    if (math.abs(leg.L1 - leg.L0) > 1e-4f)
                    {
                        float w = math.max(0.05f, leg.P1 - leg.P0);
                        float x = (d - leg.P0) / w;
                        s.Lat = leg.L0 + (leg.L1 - leg.L0) * Smooth(x);
                        dl = (x > 0f && x < 1f) ? (leg.L1 - leg.L0) * SmoothD(x) / w : 0f;
                    }
                    else s.Lat = leg.L0;
                    float2 m = math.normalizesafe(new float2(mv, dl), new float2(mv, 0f));
                    float nose = mv == f ? 1f : -1f;
                    s.Hu = m.x * nose;
                    s.Hl = m.y * nose;
                    s.Speed = spd * nose;
                    s.Braking = brk;
                    s.Odo = leg.Odo0 + d * nose;
                    return true;
                }
                case LegKind.Brake:
                {
                    // u = U0 + mv * (v0 t - a t^2 / 2) until v0 / a; lat(d) = L0 + s0 (d - d^2 / 2D) (slope s0 -> 0 at the stop)
                    float v0 = math.max(0f, leg.Vmax), a = math.max(0.05f, leg.Acc);
                    float T = v0 / a;
                    float t = math.clamp(flt, 0f, T);
                    float D = v0 * T * 0.5f;
                    float d = math.clamp(v0 * t - 0.5f * a * t * t, 0f, D);
                    sbyte mv = leg.Move >= 0 ? (sbyte)1 : (sbyte)-1;
                    float s0 = leg.P0;
                    float slope = D > 1e-3f ? s0 * (1f - d / D) : 0f;
                    s.U = leg.U0 + mv * d;
                    s.Lat = leg.L0 + (D > 1e-3f ? s0 * (d - d * d / (2f * D)) : 0f);
                    float2 m = math.normalizesafe(new float2(mv, slope), new float2(mv, 0f));
                    float nose = mv == f ? 1f : -1f;
                    s.Hu = m.x * nose;
                    s.Hl = m.y * nose;
                    s.Speed = (v0 - a * t) * nose;
                    s.Braking = true;
                    s.Odo = leg.Odo0 + d * nose;
                    return true;
                }
                case LegKind.Turn:
                {
                    float R = math.max(1f, leg.P0);
                    float side = leg.P1 >= 0f ? 1f : -1f;
                    float arc = R * math.PI / 3f;
                    float vR = leg.P2 > 0f ? leg.P2 : MxConst.kReverseSpeed;   // reverse arc at min(VTurn, VRev)
                    TurnTimes(R, leg.Vmax, vR, leg.Acc, out float t1, out float t2, out _);
                    float pz = MxConst.kTurnPause;
                    float th, spd = 0f, odo, sign = 0f;
                    bool brk = false;
                    if (flt < t1) { Trap(arc, leg.Vmax, leg.Acc, flt, out float d, out spd, out brk); th = d / R; odo = d; sign = 1f; }
                    else if (flt < t1 + pz) { th = math.PI / 3f; odo = arc; brk = true; }
                    else if (flt < t1 + pz + t2) { Trap(arc, vR, leg.Acc, flt - t1 - pz, out float d, out spd, out brk); th = math.PI / 3f + d / R; odo = arc - d; sign = -1f; }
                    else if (flt < t1 + 2f * pz + t2) { th = 2f * math.PI / 3f; odo = 0f; brk = true; }
                    else { Trap(arc, leg.Vmax, leg.Acc, flt - t1 - 2f * pz - t2, out float d, out spd, out brk); th = 2f * math.PI / 3f + d / R; odo = d; sign = 1f; }
                    th = math.clamp(th, 0f, math.PI);
                    float A, X;
                    if (th <= math.PI / 3f) { A = R * math.sin(th); X = R * (1f - math.cos(th)); }
                    else if (th <= 2f * math.PI / 3f) { A = R * 1.7320508f - R * math.sin(th); X = R * math.cos(th); }
                    else { A = R * math.sin(th); X = -R - R * math.cos(th); }
                    s.U = leg.U0 + f * A;
                    s.Lat = leg.L0 - f * side * X;
                    s.Hu = f * math.cos(th);
                    s.Hl = -f * side * math.sin(th);
                    s.Speed = sign * spd;
                    s.Braking = brk;
                    s.Odo = leg.Odo0 + odo;
                    return true;
                }
                case LegKind.Follow:
                {
                    // soft-cornered anchor (+ return-aware / lag caps) + catch-up + start velocity match
                    float target = FollowU(p, leg, clk, absStart, flt);
                    s.U = target + leg.Cu * catchK + leg.Cv * VelTerm(flt, leg.VDur);
                    s.Lat = leg.L0 + leg.Cl * catchK;
                    s.Odo = leg.Odo0 + (s.U - leg.U1) * f;
                    // a crawling work activity (grader strip / spread too fast for step-and-work hops) cycles its arm anim
                    if (leg.Anim == (byte)AnimKind.Scrape || leg.Anim == (byte)AnimKind.Spread || leg.Anim == (byte)AnimKind.Dig)
                        s.AnimTime = flt % RRWConst.kDigCycleSeconds;
                    return false;
                }
                case LegKind.DigHop:
                {
                    float C = math.max(1f, leg.P0);
                    double k = math.floor(lt / C);
                    float c = (float)(lt - k * C);
                    float Ak = Anchor(p, leg, clk, absStart, (int)k);
                    float u = Ak;
                    if (c > leg.P1)
                    {
                        float Ak1 = Anchor(p, leg, clk, absStart, (int)k + 1);
                        if (Ak1 < Ak) Ak1 = Ak;
                        if (leg.Acc > 0f && leg.Vmax > 0f)
                        {
                            // rest-to-rest trapezoid at the role's limits (the planner sized the window so it fits)
                            Trap(Ak1 - Ak, leg.Vmax, leg.Acc, c - leg.P1, out float d, out _, out _);
                            u = Ak + d;
                        }
                        else u = Ak + (Ak1 - Ak) * Smooth((c - leg.P1) / math.max(0.1f, leg.P2));
                    }
                    s.U = u + leg.Cu * catchK;
                    s.Lat = leg.L0 + leg.Cl * catchK;
                    s.AnimTime = c;
                    s.AnimParam = leg.P1;   // hop start (the breaker chops until it)
                    s.Odo = leg.Odo0 + (s.U - leg.U1) * f;
                    return false;
                }
                case LegKind.Shuttle:
                {
                    float R = math.max(0.5f, leg.P1);
                    float b = FollowU(p, leg, clk, absStart, flt, R) + leg.Cv * VelTerm(flt, leg.VDur);
                    float w = math.max(0f, leg.P0);
                    float tLeg = TrapDur(R, leg.Vmax, leg.Acc);
                    float period = 2f * tLeg + 2f * w;
                    float off, raised;
                    if (period < 1e-3f) { off = 0f; raised = 0f; }
                    else
                    {
                        float tm = (float)(lt % period);
                        if (tm < tLeg) { Trap(R, leg.Vmax, leg.Acc, tm, out off, out _, out _); raised = 0f; }
                        else if (tm < tLeg + w) { off = R; raised = Smooth((tm - tLeg) / math.max(0.1f, w)); }
                        else if (tm < 2f * tLeg + w) { Trap(R, leg.Vmax, leg.Acc, tm - tLeg - w, out float d, out _, out _); off = R - d; raised = 1f; }
                        else { off = 0f; raised = 1f - Smooth((tm - 2f * tLeg - w) / math.max(0.1f, w)); }
                    }
                    s.U = b + off * f + leg.Cu * catchK;
                    s.Lat = leg.L0 + leg.Cl * catchK;
                    s.AnimParam = raised;
                    s.Odo = leg.Odo0 + (s.U - leg.U1) * f;
                    return false;
                }
                case LegKind.Scrape:
                {
                    float target = FollowU(p, leg, clk, absStart, flt) + leg.Cv * VelTerm(flt, leg.VDur);
                    float P = math.max(8f, leg.P0);
                    float c = (float)(lt % P);
                    float bump = 0f;
                    if (leg.P2 > 0f && leg.Vmax > 0f)
                    {
                        // reverse-and-return bump as two rest-to-rest trapezoids of P2 s each (role limits)
                        float h = leg.P2;
                        if (c > P - 2f * h && c <= P - h) { Trap(leg.P1, leg.Vmax, leg.Acc, c - (P - 2f * h), out bump, out _, out _); }
                        else if (c > P - h) { Trap(leg.P1, leg.Vmax, leg.Acc, c - (P - h), out float d, out _, out _); bump = leg.P1 - d; }
                    }
                    else if (c > P - 6f && c <= P - 3f) bump = leg.P1 * Smooth((c - (P - 6f)) / 3f);
                    else if (c > P - 3f) bump = leg.P1 * (1f - Smooth((c - (P - 3f)) / 3f));
                    s.U = target - bump * f + leg.Cu * catchK;
                    s.Lat = leg.L0 + leg.Cl * catchK;
                    s.Odo = leg.Odo0 + (s.U - leg.U1) * f;
                    return false;
                }
            }
            s.U = leg.U0;
            return true;
        }

        // DigHop anchor of cycle k: A(k) = clamp(F(start + kC + C/2) + offset, Lo, Hi). A crew front faster than the
        // hop can follow caps the anchors on a line of slope Rate (the machine lags, counted frontLag).
        public static float Anchor(in MachinePlan p, in MachineLeg leg, in ClockData clk, double absStart, int k)
        {
            float C = math.max(1f, leg.P0);
            double tm = k * (double)C + 0.5 * C;
            float F;
            if (leg.Rate > 0f && leg.Rate < 1f)
            {
                FrontLin(p, leg.Front, clk, absStart + tm, out float flin, out _, out float fmin, out float fmax);
                F = math.clamp(leg.RateU + leg.Rate * (flin - leg.RateU), fmin, fmax);
            }
            else F = Front(p, leg.Front, clk, absStart + tm);
            return math.clamp(F + leg.U0, leg.Lo, math.max(leg.Lo, leg.Hi));
        }

        // ------------------------------------------------------------ world pose

        // World pose of a chain state: position on the track (+ lateral); the four footprint contact points (tyres: axle
        // span x track width, centred FootOff ahead of the origin) define a plane; Y = that plane at the ORIGIN, pitch/roll
        // from the plane.
        // The horizontal part of Pose only (position xz and the forward direction xz), for the 2D planner / guard boxes.
        // Pose's pitch / roll never change them (forward = normalize(heading) in xz), so this skips its four floor-height lookups.
        public static bool Pose2D(in MachinePlan p, in TrackView tv, in ChainState s, out float2 pos, out float2 fwd)
        {
            pos = default;
            fwd = new float2(0f, 1f);
            MxPerf.Motion(MxC.PoseEvals);
            if (!tv.Point(p.Track, s.U, out var tp)) return false;
            float2 r = tp.Right;
            float2 d = tp.Dir;
            pos = tp.Pos.xz + r * s.Lat;
            float2 hch = math.normalizesafe(new float2(s.Hu, s.Hl), new float2(1f, 0f));
            fwd = math.normalizesafe(d * hch.x + r * hch.y, d);
            return !(math.any(math.isnan(pos)) || math.any(math.isnan(fwd)));
        }

        public static bool Pose(in MachinePlan p, in TrackView tv, in ChainState s, out float3 pos, out quaternion rot)
        {
            pos = default;
            rot = quaternion.identity;
            MxPerf.Motion(MxC.PoseEvals);
            if (!tv.Point(p.Track, s.U, out var tp)) return false;
            float2 r = tp.Right;
            float2 d = tp.Dir;
            float2 c = tp.Pos.xz + r * s.Lat;
            float2 hch = math.normalizesafe(new float2(s.Hu, s.Hl), new float2(1f, 0f));
            float2 h = math.normalizesafe(d * hch.x + r * hch.y, d);
            float a = math.max(0.5f, p.HalfLen), b = math.max(0.5f, p.HalfWid);
            float o = math.clamp(p.FootOff, -a, a);
            float2 fw = hch;                              // chain-space forward
            float2 rt = new float2(-hch.y, hch.x);        // chain-space vehicle right
            float yFL = H(p, tv, s, fw * (o + a) - rt * b, tp);
            float yFR = H(p, tv, s, fw * (o + a) + rt * b, tp);
            float yBL = H(p, tv, s, fw * (o - a) - rt * b, tp);
            float yBR = H(p, tv, s, fw * (o - a) + rt * b, tp);
            float slopeF = math.clamp(((yFL + yFR) - (yBL + yBR)) * 0.5f / (2f * a), -0.45f, 0.45f);
            float y = 0.25f * (yFL + yFR + yBL + yBR) - slopeF * o;
            float slopeR = math.clamp(((yFR + yBR) - (yFL + yBL)) * 0.5f / (2f * b), -0.45f, 0.45f);
            float3 f3 = math.normalizesafe(new float3(h.x, slopeF, h.y), new float3(0f, 0f, 1f));
            float3 r3 = math.normalizesafe(new float3(h.y, slopeR, -h.x), new float3(1f, 0f, 0f));
            float3 up = math.normalizesafe(math.cross(f3, r3), new float3(0f, 1f, 0f));
            if (up.y < 0.2f) up = new float3(0f, 1f, 0f);
            rot = quaternion.LookRotationSafe(f3, up);
            pos = new float3(c.x, y, c.y);
            return !(math.any(math.isnan(pos)) || math.any(math.isnan(rot.value)));
        }

        static float H(in MachinePlan p, in TrackView tv, in ChainState s, float2 off, in TrackPoint centre)
        {
            if (tv.Height(p.Track, s.U + off.x, s.Lat + off.y, out float y)) return y;
            return centre.Pos.y + centre.HeightRel(s.Lat);
        }

        // Full sample at a render frame: pose, velocity (m per sim second, central difference over one frame) and flags.
        public static bool Sample(in MachinePlan p, in TrackView tv, in ClockData clk, uint frame, float frac,
                                  out float3 pos, out quaternion rot, out float3 vel, out TransformFlags flags)
        {
            vel = float3.zero;
            flags = p.Flags;
            double tau = clk.Tau(frame, frac);
            State(p, clk, tau, out var s);
            if (!Pose(p, tv, s, out pos, out rot)) return false;
            double h = 0.5 / kFps * clk.Scale;
            if (h > 0.0)
            {
                State(p, clk, tau - h, out var sa);
                State(p, clk, tau + h, out var sb);
                if (Pose(p, tv, sa, out float3 pa, out _) && Pose(p, tv, sb, out float3 pb, out _))
                {
                    vel = (pb - pa) * kFps;
                    if (math.lengthsq(vel) < 1e-6f) vel = float3.zero;
                }
            }
            if (s.Speed < -0.05f) flags |= TransformFlags.Reversing;
            if (s.Braking && (s.LegKind == (byte)LegKind.Drive || s.LegKind == (byte)LegKind.Turn || s.LegKind == (byte)LegKind.Hold || s.LegKind == (byte)LegKind.Brake)) flags |= TransformFlags.Braking;
            return true;
        }

        // Lateral slope (dlat per travelled metre) of a Drive leg at leg time lt (0 outside its merge window).
        public static float DriveSlope(in MachineLeg leg, float lt)
        {
            if (leg.Kind != (byte)LegKind.Drive || math.abs(leg.L1 - leg.L0) <= 1e-4f) return 0f;
            float D = math.abs(leg.U1 - leg.U0);
            Trap(D, leg.Vmax, leg.Acc, lt, out float d, out _, out _);
            float w = math.max(0.05f, leg.P1 - leg.P0);
            float x = (d - leg.P0) / w;
            return (x > 0f && x < 1f) ? (leg.L1 - leg.L0) * SmoothD(x) / w : 0f;
        }

        // Absolute machine time the plan's motion ends: the end of its last finite leg, or the start of the final open-ended
        // leg (an open-ended Hold stands still; an open-ended front-relative leg creeps with the front).
        public static double MotionEnd(in MachinePlan p)
        {
            if (p.Count <= 0) return p.Epoch;
            var last = p.Get(p.Count - 1);
            return p.Epoch + (last.OpenEnded ? last.T0 : last.T1);
        }
    }
}
