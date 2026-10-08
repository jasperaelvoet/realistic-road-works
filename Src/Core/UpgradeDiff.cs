using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Prefabs;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // The upgrade classifier, pure part: the flags-first test, the geometry check and the lateral diff of two layouts that are
    // measured in one shared frame (the NEW curve's edge frame). See UpgradeTypes.cs for the types.
    public static class UpgradeDiff
    {
        // Decoration flags: changing only these never forms a band (vanilla creates trees, lamps etc. at once).
        public const CompositionFlags.General kCosmeticGeneral =
            CompositionFlags.General.Lighting | CompositionFlags.General.Crosswalk | CompositionFlags.General.TrafficLights
            | CompositionFlags.General.RemoveTrafficLights | CompositionFlags.General.AllWayStop
            | CompositionFlags.General.PrimaryMiddleBeautification | CompositionFlags.General.SecondaryMiddleBeautification;
        public const CompositionFlags.Side kCosmeticSide =
            CompositionFlags.Side.PrimaryBeautification | CompositionFlags.Side.SecondaryBeautification | CompositionFlags.Side.SoundBarrier
            | CompositionFlags.Side.Fence | CompositionFlags.Side.AddCrosswalk | CompositionFlags.Side.RemoveCrosswalk
            | CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidRightTurn | CompositionFlags.Side.ForbidStraight
            | CompositionFlags.Side.ForbidSecondary;
        // Quay / retaining wall flags.
        public const CompositionFlags.Side kWallSide =
            CompositionFlags.Side.Raised | CompositionFlags.Side.Lowered | CompositionFlags.Side.LowTransition | CompositionFlags.Side.HighTransition;

        // Decided before any layout is read. samePrefab: the road type stays; oldUp / newUp: the edge's upgrade flags before and
        // after. Same type with only decoration changes (or none) -> Cosmetic; same type with wall flags changing -> Instant (the
        // base game behaviour); a type change with wall flags changing -> Wall (full rebuild); otherwise the diff decides.
        public static FlagsVerdict FlagsFirst(bool samePrefab, CompositionFlags oldUp, CompositionFlags newUp)
        {
            var dg = oldUp.m_General ^ newUp.m_General;
            var ds = (oldUp.m_Left ^ newUp.m_Left) | (oldUp.m_Right ^ newUp.m_Right);
            bool wall = (ds & kWallSide) != 0;
            if (!samePrefab) return wall ? FlagsVerdict.Wall : FlagsVerdict.Diff;
            if (wall) return FlagsVerdict.Instant;
            bool other = (dg & ~kCosmeticGeneral) != 0 || (ds & ~kCosmeticSide) != 0;
            return other ? FlagsVerdict.Diff : FlagsVerdict.Cosmetic;
        }

        // ------------------------------------------------------------------ geometry check

        private static readonly float[] s_AlignT = { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f };

        // Is the new curve the old curve shifted sideways? Laterals of 5 samples of the new curve from the old arc: Shift = mean,
        // Reversed = the tangents oppose. Fails when a sample deviates more than kUwShiftTol from the mean, an end point lies more
        // than kUwEndTol along the arc from the old end, or the length differs from the offset curve's length (old length minus
        // shift x right-turn angle) by more than kUwLengthTol.
        public static bool Align(EdgeArc oldArc, Bezier4x3 newCurve, out UpgradeAlign a)
        {
            a = default;
            if (oldArc == null || !(oldArc.Length > 0.01f)) return false;
            float L = oldArc.Length;
            float sum = 0f, dirSum = 0f;
            var lat = new float[s_AlignT.Length];
            for (int k = 0; k < s_AlignT.Length; k++)
            {
                float t = s_AlignT[k];
                float3 p = MathUtils.Position(newCurve, t);
                float s = oldArc.Project(p);
                lat[k] = math.dot((p - oldArc.Position(s)).xz, oldArc.Right(s).xz);
                sum += lat[k];
                dirSum += math.dot(math.normalizesafe(MathUtils.Tangent(newCurve, t).xz), math.normalizesafe(oldArc.Direction(s).xz));
            }
            a.Shift = sum / s_AlignT.Length;
            a.Reversed = dirSum < 0f;
            for (int k = 0; k < lat.Length; k++) a.MaxDeviation = math.max(a.MaxDeviation, math.abs(lat[k] - a.Shift));

            float3 p0 = MathUtils.Position(newCurve, 0f), p1 = MathUtils.Position(newCurve, 1f);
            if (a.Reversed) { var t3 = p0; p0 = p1; p1 = t3; }
            a.EndOffset = math.max(math.abs(oldArc.ProjectExtended(p0)), math.abs(oldArc.ProjectExtended(p1) - L));

            // signed right-turn angle of the old arc (sum of the heading changes)
            const int kN = 32;
            float turnRight = 0f, newLen = 0f;
            float2 prevDir = math.normalizesafe(oldArc.Direction(0f).xz);
            float3 prevP = MathUtils.Position(newCurve, 0f);
            for (int i = 1; i <= kN; i++)
            {
                float2 d = math.normalizesafe(oldArc.Direction(L * i / kN).xz);
                float cross = prevDir.x * d.y - prevDir.y * d.x;      // > 0: counter-clockwise seen from above (a left turn)
                turnRight -= math.atan2(cross, math.dot(prevDir, d));
                prevDir = d;
                float3 q = MathUtils.Position(newCurve, i / (float)kN);
                newLen += math.distance(prevP.xz, q.xz);
                prevP = q;
            }
            float expected = L - a.Shift * turnRight;
            a.LengthError = math.abs(newLen - expected) / L;
            return a.MaxDeviation <= RRWConst.kUwShiftTol && a.EndOffset <= RRWConst.kUwEndTol && a.LengthError <= RRWConst.kUwLengthTol;
        }

        // ------------------------------------------------------------------ diff

        // RemarkLine: re-marking around a lane line that appears, disappears or changes between edge line and lane line; it is
        // exempt from the noise filter (kUwTol) and becomes Remark after it.
        private enum Chg : byte { None = 0, Build = 1, Rebuild = 2, Remove = 3, Remark = 4, RemarkLine = 5 }

        private struct Run
        {
            public Chg C;
            public float Lo, Hi;
            public BandSide Side;
            public float Width => Hi - Lo;
        }

        private const float kWidthEps = 0.02f;   // measured lane positions carry float noise; a 1 m strip must count as 1 m
        private static readonly List<float> s_Cuts = new List<float>(64), s_Unmatched = new List<float>(8);
        private static readonly List<Run> s_Runs = new List<Run>(16);
        private static readonly List<Run> s_Entries = new List<Run>(8);
        private static readonly List<float> s_LinesOld = new List<float>(16), s_LinesNew = new List<float>(16);
        private static readonly List<float> s_EdgesOld = new List<float>(2), s_EdgesNew = new List<float>(2);

        public static UpgradeClass Classify(CompositionLayout old, CompositionLayout neu, out UpgradeSpec spec) => Classify(old, neu, out spec, null);

        // Lateral diff of the old and the new layout (both Normalise()d, shared frame). Fills spec (edge-frame bands, window 0,
        // traffic undecided) and returns its class. trace (optional) receives one line per step for logs and dev commands.
        public static UpgradeClass Classify(CompositionLayout old, CompositionLayout neu, out UpgradeSpec spec, List<string> trace)
        {
            spec = default;
            if (old == null || neu == null || !old.Readable || !neu.Readable
                || old.CalibResidual > RRWConst.kUwCalibMaxResidual || neu.CalibResidual > RRWConst.kUwCalibMaxResidual
                || float.IsNaN(old.CarriageLo) || float.IsNaN(neu.CarriageLo)
                || !(old.OuterR > old.OuterL) || !(neu.OuterR > neu.OuterL))
                return Structural(ref spec, UpgradeStructural.Unreadable, trace);
            if (old.Elevated || old.Tunnel || neu.Elevated || neu.Tunnel) return Structural(ref spec, UpgradeStructural.Elevation, trace);
            if (TracksDiffer(old, neu)) return Structural(ref spec, UpgradeStructural.Track, trace);

            // 1. intervals: the cross-section is cut at every strip boundary and outer edge of both layouts and at the edges of the
            // re-marking strips around lane / edge lines without a partner; each piece is classified at its midpoint. Run widths are
            // exact, so mirrored layouts give mirrored runs (a sampled diff decided bands of exactly kUwBandMinWidth by alignment).
            old.LaneLines(s_LinesOld);
            neu.LaneLines(s_LinesNew);
            old.EdgeLines(s_EdgesOld);
            neu.EdgeLines(s_EdgesNew);
            var lines = s_Unmatched;
            lines.Clear();
            Unmatched(s_LinesOld, s_LinesNew, lines);
            Unmatched(s_LinesNew, s_LinesOld, lines);
            Unmatched(s_EdgesOld, s_EdgesNew, lines);
            Unmatched(s_EdgesNew, s_EdgesOld, lines);
            var cuts = s_Cuts;
            cuts.Clear();
            AddCuts(old, cuts);
            AddCuts(neu, cuts);
            for (int k = 0; k < lines.Count; k++) { cuts.Add(lines[k] - RRWConst.kUwBoundaryTol); cuts.Add(lines[k] + RRWConst.kUwBoundaryTol); }
            cuts.Sort();

            // 2. runs
            var runs = s_Runs;
            runs.Clear();
            for (int k = 0; k + 1 < cuts.Count; k++)
            {
                float lo = cuts[k], hi = cuts[k + 1];
                if (hi - lo < 1e-4f) continue;
                Chg c = ChangeAt(old, neu, (lo + hi) * 0.5f, lines);
                if (c == Chg.None) continue;
                int last = runs.Count - 1;
                if (last >= 0 && runs[last].C == c && lo - runs[last].Hi < 1e-4f) { var r = runs[last]; r.Hi = hi; runs[last] = r; }
                else runs.Add(new Run { C = c, Lo = lo, Hi = hi });
            }
            MergeSameRuns(runs);
            float oLo = old.CarriageLo, oHi = old.CarriageHi, nLo = neu.CarriageLo, nHi = neu.CarriageHi;
            for (int i = runs.Count - 1; i >= 0; i--)
            {
                var r = runs[i];
                if (r.C == Chg.Remark || r.C == Chg.RemarkLine || r.Width >= RRWConst.kUwBandMinWidth - kWidthEps) continue;
                bool touches = Touches(r.Lo, r.Hi, oLo, oHi) || Touches(r.Lo, r.Hi, nLo, nHi);
                if (touches) { r.C = Chg.Remark; runs[i] = r; }
                else runs.RemoveAt(i);
            }
            MergeSameRuns(runs);
            for (int i = runs.Count - 1; i >= 0; i--)
                if (runs[i].C == Chg.Remark && runs[i].Width < RRWConst.kUwTol) runs.RemoveAt(i);
            for (int i = 0; i < runs.Count; i++)
                if (runs[i].C == Chg.RemarkLine) { var r = runs[i]; r.C = Chg.Remark; runs[i] = r; }
            MergeSameRuns(runs);
            if (trace != null) trace.Add("runs " + DescribeRuns(runs, false));

            // 3. sides (NEW direction split), the one Remark entry
            float split = neu.DirSplit(out float midLo, out float midHi);
            bool oneWay = float.IsNaN(split);
            if (oneWay) split = midLo;
            var entries = s_Entries;
            entries.Clear();
            bool remark = false, touchL = false, touchR = false;
            for (int i = 0; i < runs.Count; i++)
            {
                var r = runs[i];
                if (r.C == Chg.Remark)
                {
                    remark = true;
                    if (r.Lo < split - 0.05f) touchL = true;
                    if (r.Hi > split + 0.05f) touchR = true;
                    continue;
                }
                if (!oneWay && midHi - midLo > 2f * RRWConst.kUwMiddleTol && r.Lo >= midLo - RRWConst.kUwMiddleTol && r.Hi <= midHi + RRWConst.kUwMiddleTol)
                    r.Side = BandSide.Middle;
                else r.Side = (r.Lo + r.Hi) * 0.5f < split ? BandSide.Left : BandSide.Right;
                entries.Add(r);
            }

            // 4. a rebuild next to a build joins the build band, next to a remove the remove band: same side, or touching across the
            // painted direction split (an old sidewalk that becomes the first lane of the other direction is one strip of works
            // with the new land beside it); never across a median. The merged band keeps the build / remove band's side.
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < entries.Count && !changed; i++)
                {
                    if (entries[i].C != Chg.Rebuild) continue;
                    int target = -1;
                    for (int pass = 0; pass < 2 && target < 0; pass++)
                    {
                        var want = pass == 0 ? Chg.Build : Chg.Remove;
                        for (int j = 0; j < entries.Count; j++)
                            if (j != i && entries[j].C == want && SameWorksStrip(entries[i], entries[j]) && Gap(entries[i], entries[j]) < RRWConst.kUwMergeGap) { target = j; break; }
                    }
                    if (target < 0) continue;
                    var t = entries[target];
                    t.Lo = math.min(t.Lo, entries[i].Lo);
                    t.Hi = math.max(t.Hi, entries[i].Hi);
                    entries[target] = t;
                    entries.RemoveAt(i);
                    changed = true;
                }
            }
            // 4b. a narrow rebuild left on its own (a kerb that moves by a metre: a sidewalk a little wider or narrower) is no works
            // strip of its own: nobody fences off a lane to move a kerb by that much; the new kerb comes with the finishing
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].C == Chg.Rebuild && entries[i].Width < RRWConst.kUwKerbShiftMax)
                {
                    if (trace != null) trace.Add("narrow rebuild [" + entries[i].Lo.ToString("0.##") + "," + entries[i].Hi.ToString("0.##") + "] left to the finishing");
                    entries.RemoveAt(i);
                }
            // 5. adjacent entries of the same kind and side merge
            changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < entries.Count && !changed; i++)
                    for (int j = i + 1; j < entries.Count && !changed; j++)
                    {
                        if (entries[i].C != entries[j].C || entries[i].Side != entries[j].Side || Gap(entries[i], entries[j]) >= RRWConst.kUwMergeGap) continue;
                        var t = entries[i];
                        t.Lo = math.min(t.Lo, entries[j].Lo);
                        t.Hi = math.max(t.Hi, entries[j].Hi);
                        entries[i] = t;
                        entries.RemoveAt(j);
                        changed = true;
                    }
            }
            entries.Sort((p, q) => p.Lo.CompareTo(q.Lo));
            // every change of the carriageway is re-marked: a strip built or rebuilt into the new carriageway, or removed from the old
            // one, leaves lines to paint even where the lane layout itself stays
            bool repaint = remark;
            for (int i = 0; i < entries.Count && !repaint; i++)
            {
                var e = entries[i];
                if (e.C == Chg.Remark) continue;
                float cLo = e.C == Chg.Remove ? oLo : nLo, cHi = e.C == Chg.Remove ? oHi : nHi;
                repaint = !float.IsNaN(cLo) && math.min(e.Hi, cHi) - math.max(e.Lo, cLo) > 0.05f;
            }
            if (repaint && !remark)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e.C != Chg.Remove) continue;
                    if (e.Side == BandSide.Left || e.Side == BandSide.Both || e.Side == BandSide.Middle) touchL = true;
                    if (e.Side == BandSide.Right || e.Side == BandSide.Both || e.Side == BandSide.Middle) touchR = true;
                }
                if (trace != null) trace.Add("repaint: the carriageway changes, re-marking added");
            }
            if (repaint)
            {
                // the re-marking also paints the new lanes of build / rebuild bands (they have no finishing of their own)
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e.C == Chg.Remove) continue;
                    float lo = math.max(e.Lo, nLo), hi = math.min(e.Hi, nHi);
                    if (hi - lo < 0.05f) continue;
                    if (lo < split - 0.05f) touchL = true;
                    if (hi > split + 0.05f) touchR = true;
                }
                var r = new Run { C = Chg.Remark };
                if (oneWay || (touchL && touchR)) { r.Side = BandSide.Both; r.Lo = nLo; r.Hi = nHi; }
                else if (touchL) { r.Side = BandSide.Left; r.Lo = nLo; r.Hi = split; }
                else { r.Side = BandSide.Right; r.Lo = split; r.Hi = nHi; }
                entries.Add(r);
            }
            if (trace != null) trace.Add("entries " + DescribeRuns(entries, true) + " split=" + RRWLog.F(oneWay ? float.NaN : split));

            // 6. count, 7. class
            if (entries.Count > RRWConst.kUwMaxBands) return Structural(ref spec, UpgradeStructural.Bands, trace);
            bool build = false, remove = false;
            BandSide strip = BandSide.Both;
            bool oneSided = true;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.C == Chg.Remark) continue;
                if (e.C == Chg.Remove) remove = true; else build = true;
                if (e.Side != BandSide.Left && e.Side != BandSide.Right) oneSided = false;
                else if (strip == BandSide.Both) strip = e.Side;
                else if (strip != e.Side) oneSided = false;
            }
            UpgradeClass cls;
            if (!build && !remove && !remark) cls = UpgradeClass.Cosmetic;
            else if (build && remove) cls = UpgradeClass.Mixed;
            else if ((build || remove) && remark && oneSided) cls = UpgradeClass.Mixed;
            else if (build) cls = UpgradeClass.Widen;
            else if (remove) cls = UpgradeClass.Narrow;
            else cls = UpgradeClass.Remark;

            spec.Class = cls;
            spec.BandCount = entries.Count;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                float inside = math.max(0f, math.min(e.Hi, neu.OuterR) - math.max(e.Lo, neu.OuterL));
                bool partly = inside > 0.05f && inside < e.Width - 0.05f;
                spec.SetBand(i, UpgradeBand.Make(KindOf(e.C), e.Side, e.Lo, e.Hi, 0, BandTraffic.Undecided, 0f, partly));
            }
            if (trace != null) trace.Add("class " + spec);
            return cls;
        }

        private static bool SameWorksStrip(in Run a, in Run b) =>
            a.Side == b.Side || (a.Side != BandSide.Middle && b.Side != BandSide.Middle);

        // The final verdict of one apply: flags first, then the alignment, then the diff (spec as Classify filled it).
        // Decoration flags alone stay Cosmetic unless the layout moved a kerb: grass on a road with parking turns a parking lane
        // into a planted verge, which is kerb work (the diff's build / rebuild / remove bands then decide). instant: same road
        // type with quay / retaining wall changes, applied at once like the base game (class Cosmetic, no bands).
        public static UpgradeClass Decide(FlagsVerdict fv, bool aligned, UpgradeClass diff, ref UpgradeSpec spec, out bool instant)
        {
            instant = false;
            if (fv == FlagsVerdict.Instant) { instant = true; return Cosmetic(ref spec); }
            if (fv == FlagsVerdict.Wall) return Structural(ref spec, UpgradeStructural.Wall, null);
            if (!aligned) return fv == FlagsVerdict.Cosmetic ? Cosmetic(ref spec) : Structural(ref spec, UpgradeStructural.Geometry, null);
            if (fv == FlagsVerdict.Cosmetic && !HasGeometryBand(spec)) return Cosmetic(ref spec);
            return diff;
        }

        public static bool HasGeometryBand(in UpgradeSpec spec)
        {
            if (spec.Class == UpgradeClass.Structural) return true;
            for (int i = 0; i < spec.BandCount; i++) if (spec.Band(i).Kind != BandKind.Remark) return true;
            return false;
        }

        private static UpgradeClass Cosmetic(ref UpgradeSpec spec)
        {
            spec = default;
            spec.Class = UpgradeClass.Cosmetic;
            return UpgradeClass.Cosmetic;
        }

        private static UpgradeClass Structural(ref UpgradeSpec spec, UpgradeStructural why, List<string> trace)
        {
            spec.Class = UpgradeClass.Structural;
            spec.Why = why;
            spec.BandCount = 0;
            if (trace != null) trace.Add("structural " + why);
            return UpgradeClass.Structural;
        }

        private static BandKind KindOf(Chg c) =>
            c == Chg.Build ? BandKind.Build : c == Chg.Rebuild ? BandKind.Rebuild : c == Chg.Remove ? BandKind.Remove : BandKind.Remark;

        public static bool TracksDiffer(CompositionLayout a, CompositionLayout b)
        {
            if (a.TrackLanes != b.TrackLanes) return true;
            for (int i = 0; i < a.TrackLanes; i++) if (math.abs(a.Tracks[i] - b.Tracks[i]) > RRWConst.kUwLaneTol) return true;
            return false;
        }

        // Lines of `from` without a partner in `to` within kUwLaneTol (appear, disappear, move, or turn from an edge line into a
        // lane line) are added to output.
        private static void Unmatched(List<float> from, List<float> to, List<float> output)
        {
            for (int k = 0; k < from.Count; k++)
            {
                bool partner = false;
                for (int m = 0; m < to.Count && !partner; m++) partner = math.abs(to[m] - from[k]) <= RRWConst.kUwLaneTol;
                if (!partner) output.Add(from[k]);
            }
        }

        private static void AddCuts(CompositionLayout l, List<float> cuts)
        {
            cuts.Add(l.OuterL);
            cuts.Add(l.OuterR);
            for (int i = 0; i < l.Count; i++) { cuts.Add(l.Strips[i].Lo); cuts.Add(l.Strips[i].Hi); }
        }

        // The change at x. Re-marking strips around unmatched lines only where both layouts have carriageway and nothing else
        // changed.
        private static Chg ChangeAt(CompositionLayout old, CompositionLayout neu, float x, List<float> unmatchedLines)
        {
            var so = old.SurfaceAt(x);
            var sn = neu.SurfaceAt(x);
            if (so == SurfaceClass.Outside && sn != SurfaceClass.Outside) return Chg.Build;
            if (so != SurfaceClass.Outside && sn == SurfaceClass.Outside) return Chg.Remove;
            if (so != sn) return Chg.Rebuild;    // carriageway <-> raised
            if (so != SurfaceClass.Carriageway) return Chg.None;
            var ko = old.KindAt(x, out sbyte dO);
            var kn = neu.KindAt(x, out sbyte dN);
            if (ko != kn || dO != dN) return Chg.Remark;
            for (int k = 0; k < unmatchedLines.Count; k++)
                if (math.abs(x - unmatchedLines[k]) <= RRWConst.kUwBoundaryTol) return Chg.RemarkLine;
            return Chg.None;
        }

        private static bool Touches(float lo, float hi, float cLo, float cHi) =>
            !float.IsNaN(cLo) && hi >= cLo - 0.05f && lo <= cHi + 0.05f;

        private static float Gap(in Run a, in Run b) => math.max(a.Lo, b.Lo) - math.min(a.Hi, b.Hi);

        // Consecutive runs of the same change separated by less than kUwMergeGap (no other run between them) merge.
        private static void MergeSameRuns(List<Run> runs)
        {
            runs.Sort((p, q) => p.Lo.CompareTo(q.Lo));
            for (int i = 0; i + 1 < runs.Count;)
            {
                var a = runs[i]; var b = runs[i + 1];
                if (a.C == b.C && b.Lo - a.Hi < RRWConst.kUwMergeGap)
                {
                    a.Hi = math.max(a.Hi, b.Hi);
                    runs[i] = a;
                    runs.RemoveAt(i + 1);
                    continue;
                }
                i++;
            }
        }

        private static string DescribeRuns(List<Run> l, bool sides)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < l.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(l[i].C);
                if (sides) sb.Append('/').Append(l[i].Side);
                sb.Append('[').Append(RRWLog.F(l[i].Lo)).Append(',').Append(RRWLog.F(l[i].Hi)).Append(']');
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        // ------------------------------------------------------------------ sub-strips

        // Cuts [lo, hi] at every strip boundary and outer edge of the NEW layout. One sub-strip per lane; verge, terrain,
        // sidewalk and median pieces merge with same-kind neighbours. Pieces narrower than 5 cm are dropped.
        public static int SubStrips(CompositionLayout neu, float lo, float hi, List<SubStrip> output)
        {
            output.Clear();
            if (neu == null || !(hi > lo)) return 0;
            var cuts = new List<float>(neu.Count * 2 + 4) { lo, hi };
            for (int i = 0; i < neu.Count; i++)
            {
                if (neu.Strips[i].Lo > lo && neu.Strips[i].Lo < hi) cuts.Add(neu.Strips[i].Lo);
                if (neu.Strips[i].Hi > lo && neu.Strips[i].Hi < hi) cuts.Add(neu.Strips[i].Hi);
            }
            if (neu.OuterL > lo && neu.OuterL < hi) cuts.Add(neu.OuterL);
            if (neu.OuterR > lo && neu.OuterR < hi) cuts.Add(neu.OuterR);
            cuts.Sort();
            for (int k = 0; k + 1 < cuts.Count; k++)
            {
                float a = cuts[k], b = cuts[k + 1];
                if (b - a < 0.05f) continue;
                float mid = (a + b) * 0.5f;
                var s = new SubStrip { Lo = a, Hi = b };
                var sc = neu.SurfaceAt(mid);
                if (sc == SurfaceClass.Outside) s.Kind = SubKind.Terrain;
                else
                {
                    int i = neu.StripAt(mid);
                    if (i >= 0)
                    {
                        s.Kind = UpgradeLayoutUtil.SubKindOf(neu.Strips[i].Kind);
                        if (s.Kind == SubKind.DriveLane) s.Dir = neu.Strips[i].Dir;
                    }
                    else s.Kind = sc == SurfaceClass.Carriageway ? SubKind.DriveLane : SubKind.Verge;
                }
                int last = output.Count - 1;
                bool mergeable = s.Kind == SubKind.Verge || s.Kind == SubKind.Terrain || s.Kind == SubKind.Sidewalk || s.Kind == SubKind.Median;
                if (mergeable && last >= 0 && output[last].Kind == s.Kind && a - output[last].Hi < 0.06f)
                {
                    var m = output[last];
                    m.Hi = b;
                    output[last] = m;
                    continue;
                }
                output.Add(s);
            }
            return output.Count;
        }

        public static int SubStrips(CompositionLayout neu, in UpgradeBand band, List<SubStrip> output) => SubStrips(neu, band.Lo, band.Hi, output);
    }
}
