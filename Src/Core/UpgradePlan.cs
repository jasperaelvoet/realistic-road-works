using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // One band of a project in the CHAIN frame (the union of the edges' bands, UpgradePlan.Union).
    public struct ChainBand
    {
        public BandKind Kind;
        public BandSide Side;
        public float Lo, Hi;         // chain-frame laterals
        public float G0;             // start fraction of its own progress (re-upgrade carry), 0 normally
        public int Window;           // window index (UpgradePlan.Windows)
        public float Width => Hi - Lo;

        public override string ToString() =>
            Kind + "/" + Side + "[" + RRWLog.F(Lo) + "," + RRWLog.F(Hi) + "]w" + Window + (G0 > 0f ? " g0=" + RRWLog.F(G0) : "");
    }

    // The schedule of a mode H project, fixed at creation and saved (project-uniform on every edge):
    //   setup     [0, SetupP)
    //   window k  [P0(k), P1(k))   P0(0) = SetupP, P0(k) = P1(k - 1)
    //   teardown  [P1(N - 1), 1)
    // Window ends are saved in 1/255 of p; rush only changes the accrual rate, never the schedule.
    public struct UpgradeSchedule
    {
        public byte N;               // windows 1..kUwMaxWindows (0 = no schedule)
        public byte SetupP8;         // setup share, 1/255
        public ulong Ends;           // byte k = end of window k, 1/255

        public byte End8(int k) => (byte)((Ends >> (8 * math.clamp(k, 0, 7))) & 0xFF);

        public void SetEnd8(int k, byte v)
        {
            int sh = 8 * math.clamp(k, 0, 7);
            Ends = (Ends & ~(0xFFUL << sh)) | ((ulong)v << sh);
        }

        public float SetupP => SetupP8 / 255f;
        public float P0(int k) => k <= 0 ? SetupP : End8(k - 1) / 255f;
        public float P1(int k) => End8(k) / 255f;
        public float TeardownStart => N > 0 ? P1(N - 1) : 1f;

        // Windows 1..8, setup before the first end, ends strictly increasing.
        public bool Valid
        {
            get
            {
                if (N < 1 || N > RRWConst.kUwMaxWindows) return false;
                int prev = SetupP8;
                for (int k = 0; k < N; k++)
                {
                    int e = End8(k);
                    if (e <= prev) return false;
                    prev = e;
                }
                return true;
            }
        }

        public static UpgradeSchedule Of(in RoadWorksSite s) => new UpgradeSchedule { N = s.m_WindowCount, SetupP8 = s.m_SetupP8, Ends = s.m_WinEnds };

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("N=").Append(N).Append(" setup=").Append(SetupP8).Append(" ends=");
            for (int k = 0; k < N; k++) { if (k > 0) sb.Append(','); sb.Append(End8(k)); }
            return sb.ToString();
        }
    }

    // Outcome of a re-upgrade merge (UpgradePlan.Merge).
    public enum MergeOutcome : byte
    {
        Ok = 0,          // bands in the output
        End = 1,         // nothing left to build: the works end now (the revert undo)
        Structural = 2,  // more than kUwMaxBands entries: full rebuild
    }

    // An old band of a site that is upgraded again, in the frame of the NEW curve (UpgradePlan.Reframe), with its progress state.
    public struct MergeBand
    {
        public UpgradeBand Band;
        public BandState State;
        public float G;              // its progress now (BandGAt)
    }

    // The schedule, the band frames, the re-upgrade merge and the equivalent phases of mode H. Pure, main thread or jobs.
    public static class UpgradePlan
    {
        // ------------------------------------------------------------------ frames

        public static BandSide MirrorSide(BandSide s) => s == BandSide.Left ? BandSide.Right : s == BandSide.Right ? BandSide.Left : s;

        // Edge frame -> chain frame: an edge that runs against the chain flips its sides and negates its laterals.
        public static UpgradeBand ToChain(UpgradeBand b, bool chainReversed) => chainReversed ? Mirror(b) : b;

        // Mirror into the opposite direction (also: the tail of an edge whose merged curve runs against it).
        public static UpgradeBand Mirror(UpgradeBand b)
        {
            var m = b;
            m.Lo16 = (short)math.clamp(-b.Hi16, short.MinValue, short.MaxValue);
            m.Hi16 = (short)math.clamp(-b.Lo16, short.MinValue, short.MaxValue);
            m.SetKindSide(b.Kind, MirrorSide(b.Side), b.PartlyOutside);
            return m;
        }

        // A band of the OLD curve's frame in the frame of a new curve that lies `shift` metres to the right of the old one
        // (UpgradeAlign.Shift) and may run against it.
        public static UpgradeBand Reframe(UpgradeBand b, float shift, bool reversed)
        {
            float lo = b.Lo - shift, hi = b.Hi - shift;
            var r = b;
            r.SetLaterals(reversed ? -hi : lo, reversed ? -lo : hi);
            if (reversed) r.SetKindSide(b.Kind, MirrorSide(b.Side), b.PartlyOutside);
            return r;
        }

        // ------------------------------------------------------------------ union over a chain

        // Clusters chain-frame bands of all edges of one chain: every re-marking band joins one re-marking chain band (the hull,
        // Side Both when the sides differ: one crew re-marks the road); other kinds join on the same kind and side, overlapping or
        // within kUwUnionGap. map[i] (optional, length >= input count) receives the chain band index of input band i. G0 of a
        // chain band = the smallest G0 of its members (the most work left). Returns the chain band count; more than
        // kUwMaxChainBands means the chain has to be split (the caller does that).
        public static int Union(IList<UpgradeBand> chainFrameBands, List<ChainBand> output, int[] map = null)
        {
            output.Clear();
            if (chainFrameBands == null) return 0;
            for (int i = 0; i < chainFrameBands.Count; i++)
            {
                var b = chainFrameBands[i];
                int hit = -1;
                for (int k = 0; k < output.Count && hit < 0; k++)
                    if (Joins(output[k].Kind, output[k].Side, output[k].Lo, output[k].Hi, b.Kind, b.Side, b.Lo, b.Hi)) hit = k;
                if (hit < 0) { output.Add(new ChainBand { Kind = b.Kind, Side = b.Side, Lo = b.Lo, Hi = b.Hi, G0 = b.G0f }); continue; }
                var c = output[hit];
                c.Lo = math.min(c.Lo, b.Lo); c.Hi = math.max(c.Hi, b.Hi); c.G0 = math.min(c.G0, b.G0f);
                if (c.Side != b.Side) c.Side = BandSide.Both;
                output[hit] = c;
            }
            // clusters that grew into each other
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < output.Count && !changed; i++)
                    for (int j = i + 1; j < output.Count && !changed; j++)
                    {
                        var a = output[i]; var c = output[j];
                        if (!Joins(a.Kind, a.Side, a.Lo, a.Hi, c.Kind, c.Side, c.Lo, c.Hi)) continue;
                        a.Lo = math.min(a.Lo, c.Lo); a.Hi = math.max(a.Hi, c.Hi); a.G0 = math.min(a.G0, c.G0);
                        if (a.Side != c.Side) a.Side = BandSide.Both;
                        output[i] = a;
                        output.RemoveAt(j);
                        changed = true;
                    }
            }
            output.Sort(CompareChainBands);
            if (map != null)
                for (int i = 0; i < chainFrameBands.Count && i < map.Length; i++)
                {
                    var b = chainFrameBands[i];
                    map[i] = -1;
                    for (int k = 0; k < output.Count; k++)
                        if (Joins(output[k].Kind, output[k].Side, output[k].Lo, output[k].Hi, b.Kind, b.Side, b.Lo, b.Hi)) { map[i] = k; break; }
                }
            return output.Count;
        }

        // Do two chain-frame bands belong to one chain band? Re-marking: always (one per chain). Other kinds: same kind and side,
        // overlapping or within kUwUnionGap.
        public static bool Joins(BandKind aKind, BandSide aSide, float aLo, float aHi, BandKind bKind, BandSide bSide, float bLo, float bHi)
        {
            if (aKind != bKind) return false;
            if (aKind == BandKind.Remark) return true;
            return aSide == bSide && Gap(aLo, aHi, bLo, bHi) <= RRWConst.kUwUnionGap;
        }

        // Remark last, then by lateral.
        private static int CompareChainBands(ChainBand a, ChainBand b)
        {
            bool ra = a.Kind == BandKind.Remark, rb = b.Kind == BandKind.Remark;
            if (ra != rb) return ra ? 1 : -1;
            return a.Lo.CompareTo(b.Lo);
        }

        private static float Gap(float aLo, float aHi, float bLo, float bHi) => math.max(aLo, bLo) - math.min(aHi, bHi);

        // ------------------------------------------------------------------ windows

        // Duration weight per metre of band width (relative to a full rebuild of that width).
        public static float KindFactor(BandKind kind, bool remarkFollows, float demolitionRatio, bool gravel = false)
        {
            switch (kind)
            {
                case BandKind.Build: return gravel ? RRWConst.kUwKindBuildGravel : remarkFollows ? RRWConst.kUwKindBuildRemark : RRWConst.kUwKindBuild;
                case BandKind.Rebuild: return gravel ? RRWConst.kUwKindRebuildGravel : RRWConst.kUwKindRebuild;
                case BandKind.Remove: return RRWConst.kUwKindRemove * math.max(0f, demolitionRatio);
                default: return RRWConst.kUwKindRemark;
            }
        }

        public static bool HasRemark(IList<ChainBand> bands)
        {
            for (int i = 0; i < bands.Count; i++) if (bands[i].Kind == BandKind.Remark) return true;
            return false;
        }

        // Weight of one band (metres x kind factor x work left).
        public static float BandWeight(in ChainBand b, bool remarkFollows, float demolitionRatio, bool gravel = false) =>
            math.max(0f, b.Width) * KindFactor(b.Kind, remarkFollows, demolitionRatio, gravel) * (1f - math.saturate(b.G0));

        // Assigns every chain band to a window (ChainBand.Window) and fixes the window ends. Order: build / rebuild windows (with
        // the remove bands as extra crews in the first one), then the re-marking window last.
        //  * parallelBuild: build bands on both sides share one window (two crews) - the caller passes true when a common
        //    traffic primitive exists for both sides (lane drops on both, or a carriageway closure on a chain without buildings);
        //    otherwise one window per side (left, middle, right).
        //  * remove bands join the first build window; without build bands they share one window.
        //  * window weight = the largest band weight in it (parallel bands cost the widest); ends proportional to the weights over
        //    [SetupP, 1 - kUwTeardownP], at least 1/255 per window.
        //  * rushedAtCreation: no setup share.
        //  * gravel: every band in one window (a gravel road is dug and gravelled in one step).
        public static UpgradeSchedule Windows(List<ChainBand> bands, bool parallelBuild, bool rushedAtCreation, float demolitionRatio, bool gravel = false)
        {
            if (gravel) parallelBuild = true;
            var s = new UpgradeSchedule();
            if (bands == null) return s;
            bool anyL = false, anyM = false, anyR = false, anyRemove = false, anyRemark = false;
            for (int i = 0; i < bands.Count; i++)
            {
                var b = bands[i];
                switch (b.Kind)
                {
                    case BandKind.Remark: anyRemark = true; break;
                    case BandKind.Remove: anyRemove = true; break;
                    default:
                        if (b.Side == BandSide.Left) anyL = true;
                        else if (b.Side == BandSide.Right) anyR = true;
                        else anyM = true;
                        break;
                }
            }
            int wL = -1, wM = -1, wR = -1, wRemove = -1, wRemark = -1, n = 0;
            int sides = (anyL ? 1 : 0) + (anyM ? 1 : 0) + (anyR ? 1 : 0);
            if (sides > 0)
            {
                if (parallelBuild || sides == 1) { wL = wM = wR = n++; }
                else
                {
                    if (anyL) wL = n++;
                    if (anyM) wM = n++;
                    if (anyR) wR = n++;
                }
                wRemove = 0;
            }
            else if (anyRemove) wRemove = n++;
            if (anyRemark) wRemark = n++;
            if (n == 0) n = 1;   // nothing to build: one empty window (devices only)

            var weight = new float[RRWConst.kUwMaxWindows];
            for (int i = 0; i < bands.Count; i++)
            {
                var b = bands[i];
                int w;
                switch (b.Kind)
                {
                    case BandKind.Remark: w = wRemark; break;
                    case BandKind.Remove: w = wRemove; break;
                    default: w = b.Side == BandSide.Left ? wL : b.Side == BandSide.Right ? wR : wM; break;
                }
                if (w < 0) w = 0;
                b.Window = w;
                bands[i] = b;
                weight[w] = math.max(weight[w], BandWeight(b, anyRemark && w != wRemark, demolitionRatio, gravel));
            }
            float total = 0f;
            for (int k = 0; k < n; k++) { weight[k] = math.max(weight[k], RRWConst.kUwMinWindowWeight); total += weight[k]; }

            s.N = (byte)n;
            s.SetupP8 = rushedAtCreation ? (byte)0 : UpgradeBand.Q255(RRWConst.kUwSetupP);
            int end8 = UpgradeBand.Q255(1f - RRWConst.kUwTeardownP);
            int span = end8 - s.SetupP8;
            float cum = 0f;
            int prev = s.SetupP8;
            for (int k = 0; k < n; k++)
            {
                cum += weight[k];
                int e = k == n - 1 ? end8 : s.SetupP8 + (int)math.round(span * cum / total);
                e = math.max(e, prev + 1);
                e = math.min(e, end8 - (n - 1 - k));
                s.SetEnd8(k, (byte)e);
                prev = e;
            }
            return s;
        }

        // Window and window fraction at project progress p. Setup: window -1 (gw = fraction of the setup); teardown: window N.
        public static void At(in UpgradeSchedule s, float p, out int window, out float gw)
        {
            if (s.N == 0) { window = -1; gw = 0f; return; }
            float setup = s.SetupP;
            if (p < setup) { window = -1; gw = setup > 0f ? math.saturate(p / setup) : 1f; return; }
            for (int k = 0; k < s.N; k++)
            {
                float p1 = s.P1(k);
                if (p < p1)
                {
                    float p0 = s.P0(k);
                    window = k;
                    gw = p1 - p0 > 1e-6f ? math.saturate((p - p0) / (p1 - p0)) : 1f;
                    return;
                }
            }
            float t = s.TeardownStart;
            window = s.N;
            gw = t < 1f ? math.saturate((p - t) / (1f - t)) : 1f;
        }

        // The band's own progress in its window: G0 + (1 - G0) x gw.
        public static float BandG(in UpgradeBand b, float gw) => BandG(b.G0f, gw);
        public static float BandG(float g0, float gw) => math.saturate(g0 + (1f - math.saturate(g0)) * math.saturate(gw));

        // Progress and state of a band of window `window` at project progress p.
        public static BandState StateOf(in UpgradeSchedule s, int window, float g0, float p, out float g)
        {
            At(s, p, out int w, out float gw);
            if (w > window && w >= 0) { g = 1f; return BandState.Done; }
            if (w == window) g = BandG(g0, gw);
            else g = math.saturate(g0);
            return g >= 1f ? BandState.Done : g > 0f ? BandState.Partial : BandState.Unstarted;
        }

        // Equivalent phase of a band at its own progress g. Build / rebuild with a re-marking window after it: excavation,
        // foundation, paving (the markings come in their own window); without: plus finishing. Remove: break-up, removal,
        // restore. Remark: finishing with f = g (the half swap at RRWConst.kC4SwapF like the staged markings of new roads).
        // gravel: a build / rebuild band is dug, then gravelled (Excavation, Foundation), nothing after.
        public static void Equivalent(BandKind k, bool remarkFollows, float g, out WorksPhase ph, out float f, bool gravel = false)
        {
            g = math.saturate(g);
            if (gravel && (k == BandKind.Build || k == BandKind.Rebuild))
            {
                if (g < RRWConst.kUwGravelDig) { ph = WorksPhase.Excavation; f = g / RRWConst.kUwGravelDig; }
                else { ph = WorksPhase.Foundation; f = math.saturate((g - RRWConst.kUwGravelDig) / (1f - RRWConst.kUwGravelDig)); }
                return;
            }
            switch (k)
            {
                case BandKind.Remark:
                    ph = WorksPhase.Finishing; f = g; return;
                case BandKind.Remove:
                    Pick3(g, 0.35f, 0.75f, WorksPhase.BreakUp, WorksPhase.Removal, WorksPhase.Restore, out ph, out f); return;
                default:
                    if (remarkFollows) { Pick3(g, 0.35f, 0.65f, WorksPhase.Excavation, WorksPhase.Foundation, WorksPhase.Paving, out ph, out f); return; }
                    if (g < 0.30f) { ph = WorksPhase.Excavation; f = g / 0.30f; }
                    else if (g < 0.55f) { ph = WorksPhase.Foundation; f = (g - 0.30f) / 0.25f; }
                    else if (g < 0.82f) { ph = WorksPhase.Paving; f = (g - 0.55f) / 0.27f; }
                    else { ph = WorksPhase.Finishing; f = (g - 0.82f) / 0.18f; }
                    f = math.saturate(f);
                    return;
            }
        }

        private static void Pick3(float g, float a, float b, WorksPhase p0, WorksPhase p1, WorksPhase p2, out WorksPhase ph, out float f)
        {
            if (g < a) { ph = p0; f = g / a; }
            else if (g < b) { ph = p1; f = (g - a) / (b - a); }
            else { ph = p2; f = (g - b) / (1f - b); }
            f = math.saturate(f);
        }

        // A re-marking window follows the band's window.
        public static bool RemarkFollows(IList<ChainBand> bands, int window)
        {
            for (int i = 0; i < bands.Count; i++) if (bands[i].Kind == BandKind.Remark && bands[i].Window > window) return true;
            return false;
        }

        // The widest band of a window (-1 none).
        public static int LeadBand(IList<ChainBand> bands, int window)
        {
            int best = -1;
            for (int i = 0; i < bands.Count; i++)
                if (bands[i].Window == window && (best < 0 || bands[i].Width > bands[best].Width)) best = i;
            return best;
        }

        // Phase / fraction the project shows: the lead band of the current window (setup: window 0 at g = 0; teardown: the last
        // window at g = 1). False when there is no band.
        public static bool LeadPhase(in UpgradeSchedule s, IList<ChainBand> bands, float p, out WorksPhase ph, out float f, bool gravel = false)
        {
            ph = WorksPhase.Survey; f = 0f;
            if (bands == null || bands.Count == 0 || s.N == 0) return false;
            At(s, p, out int w, out float gw);
            int win = math.clamp(w, 0, s.N - 1);
            int lead = LeadBand(bands, win);
            if (lead < 0) return false;
            float g = w < 0 ? math.saturate(bands[lead].G0) : w >= s.N ? 1f : BandG(bands[lead].G0, gw);
            Equivalent(bands[lead].Kind, RemarkFollows(bands, win), g, out ph, out f, gravel);
            return true;
        }

        // Pure part of the runtime view: window, window fraction, active chain bands. Zones are the Director's.
        public static UpgradeView ViewAt(in UpgradeSchedule s, UpgradeClass cls, IList<ChainBand> bands, float p, BandTraffic primitive, bool allAtOnce)
        {
            At(s, p, out int w, out float gw);
            var v = new UpgradeView
            {
                Class = cls,
                Window = (sbyte)math.clamp(w, -1, 127),
                WindowCount = s.N,
                Gw = gw,
                Traffic = primitive,
                AllAtOnce = allAtOnce,
            };
            if (bands != null)
                for (int i = 0; i < bands.Count && i < 8; i++)
                    if (bands[i].Window == w) v.BandMask |= (byte)(1 << i);
            v.SetApplied(v.LayoutWindow, primitive, RoadZones.None);
            return v;
        }

        // ------------------------------------------------------------------ class of a band list

        // Same rule as the classifier: build and remove -> Mixed; a one-sided strip with re-marking -> Mixed; else Widen /
        // Narrow / Remark; nothing -> Cosmetic.
        public static UpgradeClass ClassOf(IList<UpgradeBand> bands, int count)
        {
            bool build = false, remove = false, remark = false, oneSided = true;
            BandSide strip = BandSide.Both;
            for (int i = 0; i < count; i++)
            {
                var b = bands[i];
                if (b.Kind == BandKind.Remark) { remark = true; continue; }
                if (b.Kind == BandKind.Remove) remove = true; else build = true;
                if (b.Side != BandSide.Left && b.Side != BandSide.Right) oneSided = false;
                else if (strip == BandSide.Both) strip = b.Side;
                else if (strip != b.Side) oneSided = false;
            }
            if (!build && !remove && !remark) return UpgradeClass.Cosmetic;
            if (build && remove) return UpgradeClass.Mixed;
            if ((build || remove) && remark && oneSided) return UpgradeClass.Mixed;
            if (build) return UpgradeClass.Widen;
            if (remove) return UpgradeClass.Narrow;
            return UpgradeClass.Remark;
        }

        // ------------------------------------------------------------------ re-upgrade merge

        private struct MB
        {
            public BandKind Kind;
            public BandSide Side;
            public float Lo, Hi, G0;
            public bool Started;
            public float Width => Hi - Lo;
        }

        private const int kMaskBuild = 1 << (int)BandKind.Build, kMaskRebuild = 1 << (int)BandKind.Rebuild, kMaskRemove = 1 << (int)BandKind.Remove;
        private const int kMaskAll = kMaskBuild | kMaskRebuild | kMaskRemove;

        // A site with saved bands is upgraded again. old: its bands in the NEW curve's frame (Reframe) with their state at the
        // current progress; n: the classification of the live layout -> the new layout; [outerL, outerR]: the new road outline.
        // An "undo" of an old band is a new band that changes the same lateral back (a new remove over an old build / rebuild, a
        // new build / rebuild over an old remove, and a new rebuild over the same laterals as an old rebuild: the kerb moving
        // back, within kUwMergeGap at both ends). Per lateral:
        //  * an old band that never started (state Unstarted, G0 = 0) cancels against its undo (neither is built: the strip was
        //    never touched); where no undo covers it, it survives unstarted;
        //  * a finished old band drops out (the new bands stay);
        //  * a started old band (partial, or carried over with G0 > 0): a build / rebuild survives inside the new outline with
        //    G0 = its progress (no dirt over fresh asphalt), the new remove outside stays (g0 = 0); a remove keeps its progress
        //    outside the outline and where no new build covers it inside;
        //  * new bands start at 0 and never overlap a surviving old band;
        //  * two adjacent same-kind same-side bands merge only when both are unstarted;
        //  * all re-marking unifies into one entry at g0 = 0 (clipped to the outline), except a revert: when nothing else
        //    survives and the old re-marking had not started, the new re-marking cancels against it.
        // output: the merged bands (by lateral, re-marking last, window 0, traffic undecided). End: nothing left (the works end
        // now). Structural: more than kUwMaxBands.
        public static MergeOutcome Merge(IList<MergeBand> old, in UpgradeSpec n, float outerL, float outerR, List<UpgradeBand> output)
        {
            output.Clear();
            var nb = new List<MB>(8);
            var keep = new List<MB>(8);
            bool nRemark = false;
            float remLo = float.MaxValue, remHi = float.MinValue;
            BandSide remSide = BandSide.Both;
            bool remSideSet = false, remMixed = false;
            for (int i = 0; i < n.BandCount; i++)
            {
                var b = n.Band(i);
                if (b.Kind == BandKind.Remark) { nRemark = true; AddRemark(b, ref remLo, ref remHi, ref remSide, ref remSideSet, ref remMixed); continue; }
                nb.Add(new MB { Kind = b.Kind, Side = b.Side, Lo = b.Lo, Hi = b.Hi });
            }
            var nOriginal = new List<MB>(nb);
            bool oldRemarkPartial = false, oldRemarkUnstarted = false;
            float oRemLo = float.MaxValue, oRemHi = float.MinValue;
            BandSide oRemSide = BandSide.Both;
            bool oRemSideSet = false, oRemMixed = false;
            var pieces = new List<float2>(4);
            if (old != null)
                for (int i = 0; i < old.Count; i++)
                {
                    var ob = old[i].Band;
                    var st = old[i].State;
                    if (ob.Kind == BandKind.Remark)
                    {
                        if (st == BandState.Done) continue;
                        if (st == BandState.Partial || ob.G0 > 0) oldRemarkPartial = true; else oldRemarkUnstarted = true;
                        AddRemark(ob, ref oRemLo, ref oRemHi, ref oRemSide, ref oRemSideSet, ref oRemMixed);
                        continue;
                    }
                    if (st == BandState.Done) continue;
                    bool isRemove = ob.Kind == BandKind.Remove;
                    int undoMask = isRemove ? kMaskBuild | kMaskRebuild : kMaskRemove;
                    float lo = ob.Lo, hi = ob.Hi;
                    if (st == BandState.Unstarted && ob.G0 == 0)
                    {
                        // never touched: cancel against the undo, the rest survives unstarted
                        pieces.Clear();
                        pieces.Add(new float2(isRemove ? lo : math.max(lo, outerL), isRemove ? hi : math.min(hi, outerR)));
                        for (int k = 0; k < nOriginal.Count; k++)
                            if ((undoMask & (1 << (int)nOriginal[k].Kind)) != 0) Cut(pieces, nOriginal[k].Lo, nOriginal[k].Hi);
                        Subtract(nb, undoMask, lo, hi);
                        if (ob.Kind == BandKind.Rebuild)
                            for (int k = 0; k < nOriginal.Count; k++)
                            {
                                var nr = nOriginal[k];
                                if (!KerbMovesBack(nr.Kind, nr.Lo, nr.Hi, lo, hi)) continue;
                                Cut(pieces, nr.Lo, nr.Hi);
                                Subtract(nb, kMaskRebuild, nr.Lo, nr.Hi);
                            }
                        for (int k = 0; k < pieces.Count; k++)
                        {
                            keep.Add(new MB { Kind = ob.Kind, Side = ob.Side, Lo = pieces[k].x, Hi = pieces[k].y });
                            Subtract(nb, kMaskAll, pieces[k].x, pieces[k].y);
                        }
                        continue;
                    }
                    float carry = st == BandState.Unstarted ? ob.G0f : math.saturate(old[i].G);
                    float inLo = math.max(lo, outerL), inHi = math.min(hi, outerR);
                    pieces.Clear();
                    if (isRemove)
                    {
                        if (lo < outerL) pieces.Add(new float2(lo, math.min(hi, outerL)));
                        if (hi > outerR) pieces.Add(new float2(math.max(lo, outerR), hi));
                        if (inHi > inLo)
                        {
                            var inside = new List<float2> { new float2(inLo, inHi) };
                            for (int k = 0; k < nOriginal.Count; k++)
                                if ((undoMask & (1 << (int)nOriginal[k].Kind)) != 0) Cut(inside, nOriginal[k].Lo, nOriginal[k].Hi);
                            pieces.AddRange(inside);
                        }
                    }
                    else if (inHi > inLo) pieces.Add(new float2(inLo, inHi));
                    for (int k = 0; k < pieces.Count; k++)
                    {
                        if (pieces[k].y - pieces[k].x <= 1e-3f) continue;
                        keep.Add(new MB { Kind = ob.Kind, Side = ob.Side, Lo = pieces[k].x, Hi = pieces[k].y, G0 = carry, Started = true });
                        Subtract(nb, kMaskAll, pieces[k].x, pieces[k].y);
                    }
                }

            var all = new List<MB>(keep.Count + nb.Count);
            all.AddRange(keep);
            all.AddRange(nb);
            for (int i = all.Count - 1; i >= 0; i--) if (all[i].Width < RRWConst.kUwMergeGap) all.RemoveAt(i);
            // adjacent unstarted same kind / side merge
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < all.Count && !changed; i++)
                    for (int j = i + 1; j < all.Count && !changed; j++)
                    {
                        var a = all[i]; var c = all[j];
                        if (a.Started || c.Started || a.Kind != c.Kind || a.Side != c.Side || Gap(a.Lo, a.Hi, c.Lo, c.Hi) >= RRWConst.kUwMergeGap) continue;
                        a.Lo = math.min(a.Lo, c.Lo); a.Hi = math.max(a.Hi, c.Hi); a.G0 = math.min(a.G0, c.G0);
                        all[i] = a;
                        all.RemoveAt(j);
                        changed = true;
                    }
            }
            all.Sort((p, q) => p.Lo.CompareTo(q.Lo));

            bool others = all.Count > 0;
            bool oldRemark = oldRemarkPartial || oldRemarkUnstarted;
            bool needRemark = oldRemarkPartial || (oldRemarkUnstarted && others) || (nRemark && !(oldRemarkUnstarted && !others));
            int count = all.Count + (needRemark ? 1 : 0);
            if (count == 0) return MergeOutcome.End;
            if (count > RRWConst.kUwMaxBands) return MergeOutcome.Structural;
            for (int i = 0; i < all.Count; i++)
            {
                var m = all[i];
                float inside = math.max(0f, math.min(m.Hi, outerR) - math.max(m.Lo, outerL));
                bool partly = inside > 0.05f && inside < m.Width - 0.05f;
                output.Add(UpgradeBand.Make(m.Kind, m.Side, m.Lo, m.Hi, 0, BandTraffic.Undecided, m.G0, partly));
            }
            if (needRemark)
            {
                if (oldRemark) AddRemarkRange(oRemLo, oRemHi, oRemSide, oRemMixed, ref remLo, ref remHi, ref remSide, ref remSideSet, ref remMixed);
                var side = remMixed ? BandSide.Both : remSide;
                output.Add(UpgradeBand.Make(BandKind.Remark, side, math.max(remLo, outerL), math.min(remHi, outerR), 0, BandTraffic.Undecided, 0f, false));
            }
            return MergeOutcome.Ok;
        }

        // A new rebuild band over the same laterals as an unstarted old rebuild (both ends within kUwMergeGap): the second upgrade
        // moves the kerb back where it was, so neither is built. A rebuild elsewhere (the kerb moving on) is not an undo.
        public static bool KerbMovesBack(BandKind newKind, float newLo, float newHi, float oldLo, float oldHi) =>
            newKind == BandKind.Rebuild && math.abs(newLo - oldLo) <= RRWConst.kUwMergeGap && math.abs(newHi - oldHi) <= RRWConst.kUwMergeGap;

        // Removes [lo, hi] from a list of intervals.
        private static void Cut(List<float2> l, float lo, float hi)
        {
            for (int i = l.Count - 1; i >= 0; i--)
            {
                var v = l[i];
                if (v.y <= lo || v.x >= hi) continue;
                l.RemoveAt(i);
                if (v.x < lo) l.Add(new float2(v.x, lo));
                if (v.y > hi) l.Add(new float2(hi, v.y));
            }
            for (int i = l.Count - 1; i >= 0; i--) if (l[i].y - l[i].x <= 1e-3f) l.RemoveAt(i);
        }

        private static void AddRemark(in UpgradeBand b, ref float lo, ref float hi, ref BandSide side, ref bool set, ref bool mixed) =>
            AddRemarkRange(b.Lo, b.Hi, b.Side, false, ref lo, ref hi, ref side, ref set, ref mixed);

        private static void AddRemarkRange(float bLo, float bHi, BandSide bSide, bool bMixed, ref float lo, ref float hi, ref BandSide side, ref bool set, ref bool mixed)
        {
            lo = math.min(lo, bLo);
            hi = math.max(hi, bHi);
            if (bMixed || bSide == BandSide.Both) mixed = true;
            if (!set) { side = bSide; set = true; }
            else if (side != bSide) mixed = true;
        }

        // Cuts [lo, hi] out of every entry whose kind is in mask (an entry may split in two).
        private static void Subtract(List<MB> list, int mask, float lo, float hi)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var m = list[i];
                if ((mask & (1 << (int)m.Kind)) == 0 || m.Hi <= lo || m.Lo >= hi) continue;
                list.RemoveAt(i);
                if (m.Lo < lo) { var a = m; a.Hi = lo; list.Add(a); }
                if (m.Hi > hi) { var b = m; b.Lo = hi; list.Add(b); }
            }
        }

        // What a merged site has paid: never below 0 (a revert upgrade has a negative cost).
        public static int MergedPaid(int oldPaid, int cost) => (int)math.min((long)int.MaxValue, math.max(0L, (long)oldPaid + cost));

        public static string Describe(IList<ChainBand> bands)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < bands.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(bands[i].ToString()); }
            return sb.ToString();
        }

        public static string F255(byte v) => (v / 255f).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
