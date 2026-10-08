using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Mathematics;

// Types of the partial upgrade works ("mode H": a visible road is widened, narrowed or re-marked band by band instead of
// being rebuilt as a whole). Everything here is pure data and pure math: no ECS reads. The game-facing reader that fills a
// CompositionLayout from the live and temporary lanes of an edge lives in the Tools module.
//
// Frames. Every lateral in this file is in metres, + = right of an edge curve (the EDGE frame of the edge the bands are
// saved on). Old and new layouts of one upgrade are both measured against the NEW curve, so they share one frame and no
// lateral shift has to be applied between them. The CHAIN frame is the edge frame flipped for edges that run against the
// project's chain direction (UpgradePlan.ToChain).
//
// Runtime state of a running upgrade project: UpgradeRuntime (ProjectRecord.Upgrade) and UpgradeEdgeState (EdgeRecord.Upgrade),
// UpgradeRuntime.cs. Projects are created by SiteFactory.CreateUpgradeProjects.
namespace RealisticRoadWorks.V3
{
    // What a works site's saved upgrade tail describes (RoadWorksSite.m_PlanKind). Saved.
    public enum PlanKind : byte
    {
        None = 0,            // every site that is not a mode H site (no tail)
        Upgrade = 1,         // partial upgrade works: bands of a road whose type changed
        Reconstruction = 2,  // RESERVED for a later version: band-by-band reconstruction of a street with houses
    }

    // Class of one upgrade. 0..4 are saved (RoadWorksSite.m_UpgradeClass); Cosmetic and Structural are classifier results only.
    public enum UpgradeClass : byte
    {
        None = 0,
        Remark = 1,      // same surface footprint, lane use / directions / lane lines change
        Widen = 2,       // surface added or rebuilt inside the new footprint, nothing removed
        Narrow = 3,      // surface removed beyond the new road edge
        Mixed = 4,       // build and remove bands, or a one-sided strip whose centre line moves
        Cosmetic = 5,    // nothing to build (trees, lights, small width noise): applied at once
        Structural = 6,  // the band model cannot express it: full rebuild
    }

    // Why an upgrade is Structural (log, tooltip). Runtime only.
    public enum UpgradeStructural : byte
    {
        None = 0,
        Elevation = 1,   // elevated / bridge / tunnel road (decals do not reach it)
        Track = 2,       // tram tracks are added, removed or moved
        Wall = 3,        // quay / retaining wall changes together with the road type
        Geometry = 4,    // the new curve is not the old curve shifted sideways
        Bands = 5,       // more than RRWConst.kUwMaxBands entries
        Unreadable = 6,  // no usable lanes / edge outline, or the composition calibration does not fit
        Policy = 7,      // the "Road upgrades" setting asks for a full rebuild
    }

    // Kind of one band. Saved in UpgradeBand.KindSide bits 0-1.
    public enum BandKind : byte
    {
        Remark = 0,      // repaint the markings of one or both carriageway halves
        Build = 1,       // new surface (dig, base, pave)
        Rebuild = 2,     // surface that changes between carriageway and raised (kerb moves)
        Remove = 3,      // surface beyond the new road edge: break up, remove, restore the ground
    }

    // Side of a band in the band's frame (edge frame when saved). Saved in UpgradeBand.KindSide bits 2-3.
    public enum BandSide : byte
    {
        Middle = 0,      // between the two direction groups (median)
        Left = 1,
        Right = 2,
        Both = 3,        // only the Remark entry: the whole carriageway
    }

    // Traffic primitive of a window, from the least to the most closing rung of the ladder, plus the fallback. Saved in
    // UpgradeBand.WinPrim bits 3-5 once the window started (UpgradeRuntime.StampPrimitive); after a load it is a ceiling
    // (PhasePlan.Ceiling, PhasePlan.WindowPrimitive).
    public enum BandTraffic : byte
    {
        None = 0,        // cars unaffected (removed strip outside the new road)
        Sidewalk = 1,    // that side's sidewalk group closed (no buildings on that side)
        Drop = 2,        // single lanes closed by blockers, every direction keeps a lane
        Half = 3,        // one direction group closed, one-way operation with a detour
        Carriageway = 4, // the whole carriageway closed, sidewalks open
        Dressing = 5,    // slow zone and devices only: no lane closed, no machine in a lane
        Paint = 6,       // re-marking on the move: the lanes next to the lines painted now close only around the painter (rolling
                         // blockers), every direction keeps a lane
        Undecided = 7,   // window not started yet
    }

    // Kind of a sub-strip: a band cut at the NEW layout's lane and sidewalk boundaries. Machines, heaps, dirt and fences use
    // sub-strips, never the raw band.
    public enum SubKind : byte
    {
        DriveLane = 0,
        NewParking = 1,
        Bike = 2,
        Sidewalk = 3,
        Verge = 4,
        Median = 5,
        Terrain = 6,     // outside the new road (Remove bands only)
    }

    // Kind of one lateral strip of a layout.
    public enum StripKind : byte
    {
        None = 0,
        Car = 1,
        Parking = 2,
        Bike = 3,
        Track = 4,
        Sidewalk = 5,
        Median = 6,
        Verge = 7,
    }

    // Surface class at one lateral.
    public enum SurfaceClass : byte
    {
        Outside = 0,     // no road surface
        Carriageway = 1, // lanes and painted areas between them
        Raised = 2,      // sidewalk, verge, raised median
    }

    // The "Road upgrades" setting.
    public enum UpgradeWorksMode : byte
    {
        Realistic = 0,   // only the changed part is built
        FullRebuild = 1, // any change of road type rebuilds the whole road
        Instant = 2,     // like the base game
    }

    // Result of the flags-first test (UpgradeDiff.FlagsFirst), decided before any layout is read.
    public enum FlagsVerdict : byte
    {
        Diff = 0,        // the layout diff decides
        Cosmetic = 1,    // only decoration flags change: no works
        Instant = 2,     // same road type, quay / retaining wall flags change: applied at once like the base game
        Wall = 3,        // quay / retaining wall flags change together with the road type: full rebuild
    }

    // Progress state of one saved band at the current project progress (UpgradePlan.StateOf).
    public enum BandState : byte
    {
        Unstarted = 0,
        Partial = 1,
        Done = 2,
    }

    // Saved upgrade flags (RoadWorksSite.m_UpFlags).
    [Flags]
    public enum UpgradeFlags : byte
    {
        None = 0,
        AllAtOnce = 1 << 0,  // every build window got lane drops at creation: all new lanes are dropped from the end of setup
        PreCover = 1 << 1,   // build bands are covered from the apply frame
    }

    // One band of an edge, 7 bytes when saved. Laterals in 1/16 m of the NEW curve's edge frame.
    public struct UpgradeBand : IEquatable<UpgradeBand>
    {
        public byte KindSide;    // bits 0-1 BandKind, 2-3 BandSide, 4 PartlyOutside, 5-7 reserved (0)
        public byte WinPrim;     // bits 0-2 window index (0..7), bits 3-5 BandTraffic of its window (7 = undecided), 6-7 reserved (0)
        public byte G0;          // start fraction of the band's own progress, 1/255 (carried over by a re-upgrade); 0 normally
        public short Lo16, Hi16; // laterals, 1/16 m, Lo16 < Hi16

        public BandKind Kind => (BandKind)(KindSide & 3);
        public BandSide Side => (BandSide)((KindSide >> 2) & 3);
        public bool PartlyOutside => (KindSide & 16) != 0;
        public int Window => WinPrim & 7;
        public BandTraffic Traffic => (BandTraffic)((WinPrim >> 3) & 7);
        public float Lo => Lo16 / 16f;
        public float Hi => Hi16 / 16f;
        public float Width => (Hi16 - Lo16) / 16f;
        public float Centre => (Lo16 + Hi16) / 32f;
        public float G0f => G0 / 255f;

        public static UpgradeBand Make(BandKind kind, BandSide side, float lo, float hi, int window = 0,
                                       BandTraffic traffic = BandTraffic.Undecided, float g0 = 0f, bool partlyOutside = false)
        {
            var b = new UpgradeBand { Lo16 = Q16(math.min(lo, hi)), Hi16 = Q16(math.max(lo, hi)), G0 = Q255(g0) };
            b.SetKindSide(kind, side, partlyOutside);
            b.SetWinPrim(window, traffic);
            return b;
        }

        public void SetKindSide(BandKind kind, BandSide side, bool partlyOutside) =>
            KindSide = (byte)(((int)kind & 3) | (((int)side & 3) << 2) | (partlyOutside ? 16 : 0));
        public void SetWinPrim(int window, BandTraffic traffic) =>
            WinPrim = (byte)((math.clamp(window, 0, 7) & 7) | (((int)traffic & 7) << 3));
        public void SetWindow(int window) => SetWinPrim(window, Traffic);
        public void SetTraffic(BandTraffic t) => SetWinPrim(Window, t);
        public void SetLaterals(float lo, float hi) { Lo16 = Q16(math.min(lo, hi)); Hi16 = Q16(math.max(lo, hi)); }

        public static short Q16(float m) => (short)math.clamp((int)math.round(m * 16f), short.MinValue, short.MaxValue);
        public static byte Q255(float f) => (byte)math.clamp((int)math.round(math.saturate(f) * 255f), 0, 255);

        public bool Equals(UpgradeBand o) => KindSide == o.KindSide && WinPrim == o.WinPrim && G0 == o.G0 && Lo16 == o.Lo16 && Hi16 == o.Hi16;
        // Same band apart from the primitive saved for its window (WinPrim bits 3-5).
        public bool SameShape(UpgradeBand o) =>
            KindSide == o.KindSide && (WinPrim & ~0x38) == (o.WinPrim & ~0x38) && G0 == o.G0 && Lo16 == o.Lo16 && Hi16 == o.Hi16;
        public override bool Equals(object obj) => obj is UpgradeBand o && Equals(o);
        public override int GetHashCode() => KindSide | (WinPrim << 8) | (G0 << 16) ^ (Lo16 * 397) ^ (Hi16 * 7919);

        public override string ToString()
        {
            var ic = CultureInfo.InvariantCulture;
            return Kind + "/" + Side + "[" + Lo.ToString("0.###", ic) + "," + Hi.ToString("0.###", ic) + "]w" + Window
                   + (Traffic == BandTraffic.Undecided ? "" : ":" + Traffic) + (G0 != 0 ? " g0=" + G0f.ToString("0.###", ic) : "")
                   + (PartlyOutside ? " partlyOutside" : "");
        }
    }

    // One lateral strip of a layout.
    public struct LayoutStrip
    {
        public float Lo, Hi;
        public StripKind Kind;
        public sbyte Dir;        // +1 travels with the curve, -1 against it, 0 none / both

        public float Width => Hi - Lo;
        public float Centre => (Lo + Hi) * 0.5f;
        public bool IsLane => UpgradeLayoutUtil.IsLaneKind(Kind);
    }

    // A sub-strip of a band (UpgradeDiff.SubStrips).
    public struct SubStrip
    {
        public float Lo, Hi;
        public SubKind Kind;
        public sbyte Dir;        // drive lanes: travel direction relative to the edge curve, +1 = with the curve, -1 = against it
                                 // (0 otherwise)
        public float Width => Hi - Lo;
    }

    // Lateral cross-section of one road layout in a shared frame: strips sorted by Lo after Normalise(), the road outline and
    // the facts the classifier needs. Managed (the classifier runs on the main thread); fixed capacity kMax.
    public sealed class CompositionLayout
    {
        public const int kMax = 24;
        public const int kMaxTracks = 8;

        public float OuterL, OuterR;        // road outer edges (edge outline)
        public bool Elevated, Tunnel;
        public bool Readable = true;        // false: the reader found no usable lanes or outline
        public int Count;                   // strips in use
        public readonly LayoutStrip[] Strips = new LayoutStrip[kMax];
        public int TrackLanes;              // tram track lanes (their centres in Tracks, sorted)
        public readonly float[] Tracks = new float[kMaxTracks];
        public int CarLanesF, CarLanesB;    // car lanes travelling with / against the curve (after Normalise)
        public float CalibResidual;         // composition calibration error (m); 0 when not used

        public void Clear()
        {
            OuterL = OuterR = 0f;
            Elevated = Tunnel = false;
            Readable = true;
            Count = 0;
            TrackLanes = 0;
            CarLanesF = CarLanesB = 0;
            CalibResidual = 0f;
        }

        public void CopyFrom(CompositionLayout o)
        {
            OuterL = o.OuterL; OuterR = o.OuterR; Elevated = o.Elevated; Tunnel = o.Tunnel; Readable = o.Readable;
            Count = o.Count; Array.Copy(o.Strips, Strips, kMax);
            TrackLanes = o.TrackLanes; Array.Copy(o.Tracks, Tracks, kMaxTracks);
            CarLanesF = o.CarLanesF; CarLanesB = o.CarLanesB; CalibResidual = o.CalibResidual;
        }

        // Adds a raw strip (any order, may overlap). False when full or invalid. Track strips are also recorded as tracks.
        public bool Add(float lo, float hi, StripKind kind, sbyte dir)
        {
            if (float.IsNaN(lo) || float.IsNaN(hi) || float.IsInfinity(lo) || float.IsInfinity(hi)) return false;
            if (hi < lo) { float t = lo; lo = hi; hi = t; }
            if (hi - lo < 0.01f) return false;
            if (kind == StripKind.Track && TrackLanes < kMaxTracks) Tracks[TrackLanes++] = (lo + hi) * 0.5f;
            if (Count >= kMax) return false;
            Strips[Count++] = new LayoutStrip { Lo = lo, Hi = hi, Kind = kind, Dir = dir };
            return true;
        }

        // Sorts the strips, merges duplicate pieces of one lane (same kind and direction, overlapping by more than half of the
        // narrower one), resolves the remaining overlaps by priority (Car > Track > Parking > Bike > Sidewalk > Median > Verge;
        // equal priority: the strip whose centre is nearer wins) and closes gaps < kUwStripGap between same-kind same-direction
        // neighbours at their midpoint. Lane boundaries are kept (they drive the re-marking test). Idempotent.
        public void Normalise()
        {
            int n = 0;
            for (int i = 0; i < Count; i++)
                if (Strips[i].Hi - Strips[i].Lo >= 0.01f && !float.IsNaN(Strips[i].Lo)) Strips[n++] = Strips[i];
            Count = n;
            SortStrips();

            // 1. duplicate pieces of one lane
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < Count && !merged; i++)
                    for (int j = i + 1; j < Count && !merged; j++)
                    {
                        var a = Strips[i]; var b = Strips[j];
                        if (a.Kind != b.Kind || a.Dir != b.Dir) continue;
                        float ov = math.min(a.Hi, b.Hi) - math.max(a.Lo, b.Lo);
                        if (ov <= 0.5f * math.min(a.Width, b.Width)) continue;
                        Strips[i] = new LayoutStrip { Lo = math.min(a.Lo, b.Lo), Hi = math.max(a.Hi, b.Hi), Kind = a.Kind, Dir = a.Dir };
                        RemoveAt(j);
                        merged = true;
                    }
            }

            // 2. overlaps: elementary intervals between all strip ends, each owned by the best covering strip
            if (Count > 1 && HasOverlap())
            {
                var xs = new List<float>(Count * 2);
                for (int i = 0; i < Count; i++) { xs.Add(Strips[i].Lo); xs.Add(Strips[i].Hi); }
                xs.Sort();
                var src = new LayoutStrip[Count];
                Array.Copy(Strips, src, Count);
                int srcN = Count;
                Count = 0;
                int lastOwner = -1;
                for (int k = 0; k + 1 < xs.Count; k++)
                {
                    float x0 = xs[k], x1 = xs[k + 1];
                    if (x1 - x0 < 1e-4f) continue;
                    float mid = (x0 + x1) * 0.5f;
                    int best = -1;
                    for (int i = 0; i < srcN; i++)
                    {
                        if (src[i].Lo > mid || src[i].Hi < mid) continue;
                        if (best < 0) { best = i; continue; }
                        int pa = Priority(src[i].Kind), pb = Priority(src[best].Kind);
                        if (pa > pb || (pa == pb && math.abs(src[i].Centre - mid) < math.abs(src[best].Centre - mid))) best = i;
                    }
                    if (best < 0) { lastOwner = -1; continue; }
                    if (best == lastOwner && Count > 0 && math.abs(Strips[Count - 1].Hi - x0) < 1e-4f)
                    {
                        Strips[Count - 1].Hi = x1;
                        continue;
                    }
                    if (Count >= kMax) break;
                    Strips[Count++] = new LayoutStrip { Lo = x0, Hi = x1, Kind = src[best].Kind, Dir = src[best].Dir };
                    lastOwner = best;
                }
                n = 0;
                for (int i = 0; i < Count; i++) if (Strips[i].Hi - Strips[i].Lo >= 0.01f) Strips[n++] = Strips[i];
                Count = n;
            }

            // 3. small gaps between same-kind same-direction neighbours: meet at the midpoint
            for (int i = 0; i + 1 < Count; i++)
            {
                var a = Strips[i]; var b = Strips[i + 1];
                float gap = b.Lo - a.Hi;
                if (gap <= 0f || gap >= RRWConst.kUwStripGap || a.Kind != b.Kind || a.Dir != b.Dir) continue;
                float m = (a.Hi + b.Lo) * 0.5f;
                Strips[i].Hi = m;
                Strips[i + 1].Lo = m;
            }

            // tracks sorted, car lane counts
            Array.Sort(Tracks, 0, TrackLanes);
            CarLanesF = CarLanesB = 0;
            for (int i = 0; i < Count; i++)
            {
                if (Strips[i].Kind != StripKind.Car) continue;
                if (Strips[i].Dir > 0) CarLanesF++; else if (Strips[i].Dir < 0) CarLanesB++;
            }
        }

        private bool HasOverlap()
        {
            for (int i = 0; i + 1 < Count; i++) if (Strips[i + 1].Lo < Strips[i].Hi - 1e-4f) return true;
            return false;
        }

        private void SortStrips()
        {
            for (int i = 1; i < Count; i++)
            {
                var s = Strips[i];
                int j = i - 1;
                while (j >= 0 && (Strips[j].Lo > s.Lo || (Strips[j].Lo == s.Lo && Strips[j].Hi > s.Hi))) { Strips[j + 1] = Strips[j]; j--; }
                Strips[j + 1] = s;
            }
        }

        private void RemoveAt(int j)
        {
            for (int k = j; k + 1 < Count; k++) Strips[k] = Strips[k + 1];
            Count--;
        }

        public static int Priority(StripKind k)
        {
            switch (k)
            {
                case StripKind.Car: return 7;
                case StripKind.Track: return 6;
                case StripKind.Parking: return 5;
                case StripKind.Bike: return 4;
                case StripKind.Sidewalk: return 3;
                case StripKind.Median: return 2;
                case StripKind.Verge: return 1;
                default: return 0;
            }
        }

        // Outer edges of the lane strips (carriageway); NaN when there is no lane.
        public float CarriageLo
        {
            get { for (int i = 0; i < Count; i++) if (Strips[i].IsLane) return Strips[i].Lo; return float.NaN; }
        }
        public float CarriageHi
        {
            get { for (int i = Count - 1; i >= 0; i--) if (Strips[i].IsLane) return Strips[i].Hi; return float.NaN; }
        }

        // Index of the strip that contains x (ends inclusive; the right strip wins on a shared boundary), -1 none.
        public int StripAt(float x)
        {
            int found = -1;
            for (int i = 0; i < Count; i++)
            {
                if (x < Strips[i].Lo || x > Strips[i].Hi) continue;
                found = i;
                if (x < Strips[i].Hi) break;
            }
            return found;
        }

        public SurfaceClass SurfaceAt(float x)
        {
            if (!(x >= OuterL && x <= OuterR)) return SurfaceClass.Outside;
            int i = StripAt(x);
            if (i >= 0) return Strips[i].IsLane ? SurfaceClass.Carriageway : SurfaceClass.Raised;
            float lo = CarriageLo, hi = CarriageHi;
            return x >= lo && x <= hi ? SurfaceClass.Carriageway : SurfaceClass.Raised;   // painted gap / plain verge
        }

        // Kind and direction at x (None in a gap).
        public StripKind KindAt(float x, out sbyte dir)
        {
            int i = StripAt(x);
            dir = i >= 0 ? Strips[i].Dir : (sbyte)0;
            return i >= 0 ? Strips[i].Kind : StripKind.None;
        }

        // A lane line (a boundary of a lane strip strictly inside the carriageway) lies within tol of x.
        public bool BoundaryNear(float x, float tol)
        {
            float lo = CarriageLo, hi = CarriageHi;
            if (float.IsNaN(lo)) return false;
            for (int i = 0; i < Count; i++)
            {
                if (!Strips[i].IsLane) continue;
                float a = Strips[i].Lo, b = Strips[i].Hi;
                if (a > lo + 0.05f && math.abs(a - x) <= tol) return true;
                if (b < hi - 0.05f && math.abs(b - x) <= tol) return true;
            }
            return false;
        }

        // Lane lines strictly inside the carriageway (deduplicated within 0.05 m), ascending.
        public void LaneLines(List<float> output)
        {
            output.Clear();
            float lo = CarriageLo, hi = CarriageHi;
            if (float.IsNaN(lo)) return;
            for (int i = 0; i < Count; i++)
            {
                if (!Strips[i].IsLane) continue;
                AddLine(output, Strips[i].Lo, lo, hi);
                AddLine(output, Strips[i].Hi, lo, hi);
            }
            output.Sort();
        }

        // The two carriageway edge lines (none without lanes).
        public void EdgeLines(List<float> output)
        {
            output.Clear();
            float lo = CarriageLo, hi = CarriageHi;
            if (float.IsNaN(lo)) return;
            output.Add(lo);
            output.Add(hi);
        }

        private static void AddLine(List<float> o, float x, float lo, float hi)
        {
            if (x <= lo + 0.05f || x >= hi - 0.05f) return;
            for (int k = 0; k < o.Count; k++) if (math.abs(o[k] - x) < 0.05f) return;
            o.Add(x);
        }

        // Direction split: midpoint between the innermost opposite-direction car / track strips (the first direction change
        // from the left). midLo / midHi = the facing edges (the median zone). NaN (and the carriageway centre in midLo/midHi)
        // when only one direction exists.
        public float DirSplit(out float midLo, out float midHi)
        {
            int first = -1;
            sbyte d0 = 0;
            for (int i = 0; i < Count; i++)
            {
                var s = Strips[i];
                if ((s.Kind != StripKind.Car && s.Kind != StripKind.Track) || s.Dir == 0) continue;
                if (first < 0) { first = i; d0 = s.Dir; continue; }
                if (s.Dir == d0) { first = i; continue; }
                midLo = Strips[first].Hi;
                midHi = s.Lo;
                if (midHi < midLo) { float m = (midLo + midHi) * 0.5f; midLo = midHi = m; }
                return (midLo + midHi) * 0.5f;
            }
            float c = (CarriageLo + CarriageHi) * 0.5f;
            if (float.IsNaN(c)) c = (OuterL + OuterR) * 0.5f;
            midLo = midHi = c;
            return float.NaN;
        }

        public float DirSplit() => DirSplit(out _, out _);

        // Mirror into the opposite frame (x -> -x, directions flipped). Used for reversed curves and tests.
        public void MirrorInPlace()
        {
            float l = OuterL; OuterL = -OuterR; OuterR = -l;
            for (int i = 0; i < Count; i++)
            {
                var s = Strips[i];
                Strips[i] = new LayoutStrip { Lo = -s.Hi, Hi = -s.Lo, Kind = s.Kind, Dir = (sbyte)(-s.Dir) };
            }
            for (int i = 0; i < TrackLanes; i++) Tracks[i] = -Tracks[i];
            SortStrips();
            Array.Sort(Tracks, 0, TrackLanes);
            int f = CarLanesF; CarLanesF = CarLanesB; CarLanesB = f;
        }

        // Calibration of composition piece positions against measured world positions: x_world = s * x_comp + o with s = +-1,
        // least squares over n matched pairs (both arrays ascending; s = -1 pairs comp[i] with measured[n-1-i]).
        // residual = largest |error| of the better fit. False when n == 0.
        public static bool Calibrate(float[] comp, float[] measured, int n, out float s, out float o, out float residual)
        {
            s = 1f; o = 0f; residual = float.PositiveInfinity;
            if (comp == null || measured == null || n <= 0 || comp.Length < n || measured.Length < n) return false;
            for (int sign = 1; sign >= -1; sign -= 2)
            {
                double sum = 0;
                for (int i = 0; i < n; i++) sum += measured[sign > 0 ? i : n - 1 - i] - sign * comp[i];
                float off = (float)(sum / n);
                float res = 0f;
                for (int i = 0; i < n; i++) res = math.max(res, math.abs(measured[sign > 0 ? i : n - 1 - i] - (sign * comp[i] + off)));
                if (res < residual - 1e-6f) { residual = res; s = sign; o = off; }
            }
            return true;
        }

        public string Describe()
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("outer=[").Append(OuterL.ToString("0.##", ic)).Append(',').Append(OuterR.ToString("0.##", ic)).Append(']');
            if (Elevated) sb.Append(" elevated");
            if (Tunnel) sb.Append(" tunnel");
            if (!Readable) sb.Append(" UNREADABLE");
            sb.Append(" cars=").Append(CarLanesF).Append('+').Append(CarLanesB).Append(" tracks=").Append(TrackLanes);
            if (CalibResidual > 0f) sb.Append(" calib=").Append(CalibResidual.ToString("0.###", ic));
            for (int i = 0; i < Count; i++)
            {
                var s = Strips[i];
                sb.Append(' ').Append(UpgradeLayoutUtil.Code(s.Kind)).Append(s.Dir > 0 ? "+" : s.Dir < 0 ? "-" : "")
                  .Append('[').Append(s.Lo.ToString("0.##", ic)).Append(',').Append(s.Hi.ToString("0.##", ic)).Append(']');
            }
            return sb.ToString();
        }
    }

    public static class UpgradeLayoutUtil
    {
        public static bool IsLaneKind(StripKind k) => k == StripKind.Car || k == StripKind.Track || k == StripKind.Parking || k == StripKind.Bike;

        public static string Code(StripKind k)
        {
            switch (k)
            {
                case StripKind.Car: return "C";
                case StripKind.Parking: return "P";
                case StripKind.Bike: return "B";
                case StripKind.Track: return "T";
                case StripKind.Sidewalk: return "S";
                case StripKind.Median: return "M";
                case StripKind.Verge: return "V";
                default: return "?";
            }
        }

        public static SubKind SubKindOf(StripKind k)
        {
            switch (k)
            {
                case StripKind.Car:
                case StripKind.Track: return SubKind.DriveLane;
                case StripKind.Parking: return SubKind.NewParking;
                case StripKind.Bike: return SubKind.Bike;
                case StripKind.Sidewalk: return SubKind.Sidewalk;
                case StripKind.Median: return SubKind.Median;
                default: return SubKind.Verge;
            }
        }
    }

    // Lane-group zones of sub-strips (CHAIN frame), for machine occupancy and device clipping. The shared "carries no traffic
    // now" rule (SubStripClosed) and the per-sub-strip machine state (SubStripStateOf) are in UpgradeRuntime.cs.
    public static partial class UpgradeZones
    {
        // split: the chain-frame direction split (NaN = one direction: car lanes are Carriageway).
        public static RoadZones OfSubStrip(in SubStrip s, bool chainReversed, float splitChain)
        {
            float lo = RoadZoneMath.ToChain(chainReversed ? s.Hi : s.Lo, chainReversed);
            float hi = RoadZoneMath.ToChain(chainReversed ? s.Lo : s.Hi, chainReversed);
            float c = (lo + hi) * 0.5f;
            bool left = float.IsNaN(splitChain) ? c < 0f : c < splitChain;
            switch (s.Kind)
            {
                case SubKind.DriveLane:
                case SubKind.Bike:
                    if (float.IsNaN(splitChain)) return RoadZones.Carriageway;
                    return left ? RoadZones.LeftHalf : RoadZones.RightHalf;
                case SubKind.NewParking: return left ? RoadZones.ParkingLeft : RoadZones.ParkingRight;
                case SubKind.Sidewalk: return left ? RoadZones.SidewalkLeft : RoadZones.SidewalkRight;
                case SubKind.Verge:
                case SubKind.Terrain: return left ? RoadZones.VergeLeft : RoadZones.VergeRight;
                default: return RoadZones.None;   // median: no lane group of its own
            }
        }
    }

    // The classifier's result for one edge.
    public struct UpgradeSpec
    {
        public UpgradeClass Class;
        public UpgradeStructural Why;
        public int BandCount;
        public UpgradeBand B0, B1, B2, B3;
        public float Shift;          // lateral shift of the new curve against the old one (log / tooltip)
        public bool Reversed;        // the new curve runs against the old one

        public UpgradeBand Band(int i) => i == 0 ? B0 : i == 1 ? B1 : i == 2 ? B2 : B3;

        public void SetBand(int i, UpgradeBand b)
        {
            if (i == 0) B0 = b; else if (i == 1) B1 = b; else if (i == 2) B2 = b; else B3 = b;
        }

        public bool HasRemark
        {
            get { for (int i = 0; i < BandCount; i++) if (Band(i).Kind == BandKind.Remark) return true; return false; }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Class);
            if (Why != UpgradeStructural.None) sb.Append('(').Append(Why).Append(')');
            for (int i = 0; i < BandCount; i++) sb.Append(' ').Append(Band(i).ToString());
            return sb.ToString();
        }
    }

    // Result of the geometry check (UpgradeDiff.Align).
    public struct UpgradeAlign
    {
        public float Shift;          // mean lateral of the new curve from the old arc (old frame)
        public bool Reversed;
        public float MaxDeviation;   // largest |lateral - Shift| over the samples
        public float EndOffset;      // largest along-arc offset of the new end points
        public float LengthError;    // |new length - expected offset length| / old length
    }

    // One chain band as every module sees it (ProjectView.Upgrade.Band(i)). CHAIN frame, metres from the chain-direction centre
    // line of the new road (+ = right of +u). Index i = the chain band index of UpgradeRuntime.Bands (Remark last, then by lateral).
    public struct UpgradeBandView
    {
        public BandKind Kind;
        public BandSide Side;        // chain frame
        public float Lo, Hi;         // union of the edges' bands (chain frame)
        public float G0;             // start fraction of the band's own progress (re-upgrade carry), 0 normally
        public sbyte Window;         // window of the band (saved, fixed at creation)
        public float SafeLo, SafeHi; // RAW machine-safe lateral range inside the band NOW (chain frame): the widest contiguous run of
                                     // machine-safe sub-strips, intersected over the edges that carry the band, with NO clearance
                                     // and no inset (a machine box may reach up to it; Machines adds its own open-lane / fence
                                     // clearance, and CrewPlan laterals keep kUwMachineLatInset from it). Director writes
                                     // UpgradeRuntime.SafeLo / SafeHi every update; SafeHi <= SafeLo = no machine-safe part.
        public bool HasCar;          // some edge has a drive-lane / parking / bike sub-strip in the band (Director, from sub-strips)

        public float Width => Hi - Lo;
        public float Centre => (Lo + Hi) * 0.5f;
        public float SafeWidth => math.max(0f, SafeHi - SafeLo);
        public bool IsBuild => Kind == BandKind.Build || Kind == BandKind.Rebuild;

        public override string ToString() =>
            Kind + "/" + Side + "[" + RRWLog.F(Lo) + "," + RRWLog.F(Hi) + "]w" + Window + (G0 > 0f ? " g0=" + RRWLog.F(G0) : "")
            + (SafeHi > SafeLo ? " safe[" + RRWLog.F(SafeLo) + "," + RRWLog.F(SafeHi) + "]" : " safe-") + (HasCar ? " car" : "");
    }

    // Runtime view of a mode H project (ProjectView.Upgrade). Filled by ProjectRecord.View() through UpgradeRuntime.View(p);
    // default (WindowCount 0) on every other project, so ProjectView.IsUpgrade is false there and no mode H branch runs.
    // Pure data, job-safe. Windows: -1 = setup (window 0 drains), 0..N-1, N = teardown (crews leave). The LAYOUT window
    // (LayoutWindow) is the window whose lane groups are closed: setup -> 0, teardown -> N - 1.
    public struct UpgradeView
    {
        public UpgradeClass Class;   // project class (UpgradePlan.ClassOf over the chain bands)
        public sbyte Window;         // -1 setup, WindowCount = teardown
        public byte WindowCount;
        public float Gw;             // fraction of the current window (setup / teardown: of that part)
        public float P;              // project progress the view was built for (ProjectRecord.Progress)
        public UpgradeSchedule Schedule;
        public byte BandMask;        // chain bands whose window is Window (bit i = chain band i; none in setup / teardown)
        public BandTraffic Traffic;  // primitive of the layout window (effective: saved ceiling, may only weaken)
        public StageBlockReason Reason; // why the layout window fell to Dressing (UpgradeRuntime.WindowReason; None otherwise)
        public RoadZones Zones;      // lane groups the layout window closes (UpgradeRuntime.Zones, PhasePlan.WindowZones)
        public RoadZones NextZones;  // lane groups the next window closes (None after the last)
        public RoadZones PreClosed;  // groups of later windows closed already (AllAtOnce; Director)
        public RoadZones MachineSafe;// zones (verge bits included) where machines may stand now (Director: PhasePlan.UpgradeSafeZones)
        public bool AllAtOnce;       // saved flag, cleared at runtime when lane drops are no longer available (never set later)
        public bool PreCover;        // saved flag: bands are covered from the apply frame (PhasePlan.UpgradeSpans); off while Deferred
        public bool Deferred;        // the old road is live until the works' finishing (RRWDeferredNet): no cover fakes it
        public uint Prims;           // 4 bits per window: effective primitive of started windows, expected primitive of later ones
        public ulong ZonesA, ZonesB; // 16 bits per window (windows 0-3, 4-7): lane groups each window closes (ZonesOf)
        // The layout APPLIED now (read it through AppliedWindow / AppliedTraffic / AppliedZones below; SetApplied writes it).
        // Equal to the layout window's values except during the Vacate step of a window start, when the previous window's groups
        // stay closed while its machines leave (PhasePlan.Stage); from Swap on they name the new window. ProjectRecord.View()
        // fills it (PhasePlan.FillUpgradeApplied, from the switch step and the stage context); UpgradeRuntime.View alone sets the
        // layout window's values; a view built by hand without SetApplied reads the layout window's values too.
        public bool AppliedSet;
        public sbyte AppliedW;
        public BandTraffic AppliedT;
        public RoadZones AppliedZ;
        public byte BandCount;       // chain bands (<= kUwMaxChainBands)
        public UpgradeBandView B0, B1, B2, B3, B4, B5, B6, B7;

        public bool Valid => WindowCount > 0;
        public bool InSetup => Window < 0;
        public bool InTeardown => WindowCount > 0 && Window >= WindowCount;
        public int LayoutWindow => WindowCount == 0 ? 0 : math.clamp((int)Window, 0, WindowCount - 1);
        // The window whose lane groups / lane drops are applied now (0..N-1).
        public int AppliedWindow => AppliedSet ? AppliedW : LayoutWindow;
        // Its primitive (Prim(AppliedWindow); Undecided until the Director decided).
        public BandTraffic AppliedTraffic => AppliedSet ? AppliedT : Traffic;
        // Lane groups closed now = PhasePlan.Stage(view).Works & AllLanes: the applied window's groups, PreClosed, and after the
        // half swap of the re-marking window the other half.
        public RoadZones AppliedZones => AppliedSet ? AppliedZ : (Zones | PreClosed) & RoadZones.AllLanes;
        // A window switch keeps the previous window's layout applied (Vacate).
        public bool Vacating => Valid && AppliedWindow != LayoutWindow;

        public void SetApplied(int window, BandTraffic traffic, RoadZones zones)
        {
            AppliedSet = true;
            AppliedW = (sbyte)math.clamp(window, -1, 127);
            AppliedT = traffic;
            AppliedZ = zones & RoadZones.AllLanes;
        }

        public UpgradeBandView Band(int i)
        {
            switch (i)
            {
                case 0: return B0; case 1: return B1; case 2: return B2; case 3: return B3;
                case 4: return B4; case 5: return B5; case 6: return B6; default: return B7;
            }
        }

        public void SetBand(int i, in UpgradeBandView b)
        {
            switch (i)
            {
                case 0: B0 = b; break; case 1: B1 = b; break; case 2: B2 = b; break; case 3: B3 = b; break;
                case 4: B4 = b; break; case 5: B5 = b; break; case 6: B6 = b; break; default: B7 = b; break;
            }
        }

        public BandTraffic Prim(int w) => w >= 0 && w < 8 ? (BandTraffic)((Prims >> (4 * w)) & 15) : BandTraffic.Undecided;
        public void SetPrim(int w, BandTraffic t)
        {
            if (w < 0 || w >= 8) return;
            int sh = 4 * w;
            Prims = (Prims & ~(15u << sh)) | (((uint)t & 15u) << sh);
        }

        public RoadZones ZonesOf(int w)
        {
            if (w < 0 || w >= 8) return RoadZones.None;
            ulong v = w < 4 ? ZonesA : ZonesB;
            return (RoadZones)(ushort)((v >> (16 * (w & 3))) & 0xFFFF);
        }

        public void SetZones(int w, RoadZones z)
        {
            if (w < 0 || w >= 8) return;
            int sh = 16 * (w & 3);
            ulong m = ~(0xFFFFUL << sh), b = (ulong)(ushort)z << sh;
            if (w < 4) ZonesA = (ZonesA & m) | b; else ZonesB = (ZonesB & m) | b;
        }

        // Chain bands of window w (bit i = band i).
        public byte MaskOf(int w)
        {
            byte m = 0;
            for (int i = 0; i < BandCount && i < 8; i++) if (Band(i).Window == w) m |= (byte)(1 << i);
            return m;
        }

        // Bands of the layout window (setup: window 0; teardown: none, the crews leave).
        public byte ActiveMask => InTeardown ? (byte)0 : MaskOf(LayoutWindow);
        public int BandsIn(int w) => math.countbits((int)MaskOf(w));

        public bool RemarkWindow(int w)
        {
            for (int i = 0; i < BandCount; i++) if (Band(i).Window == w && Band(i).Kind == BandKind.Remark) return true;
            return false;
        }

        // A re-marking window comes after window w.
        public bool RemarkFollows(int w)
        {
            for (int i = 0; i < BandCount; i++) if (Band(i).Kind == BandKind.Remark && Band(i).Window > w) return true;
            return false;
        }

        // The widest band of window w (-1 none).
        public int LeadBand(int w)
        {
            int best = -1;
            for (int i = 0; i < BandCount; i++)
                if (Band(i).Window == w && (best < 0 || Band(i).Width > Band(best).Width)) best = i;
            return best;
        }

        // Chain band of crew `crew` (crews = bands of the layout window in index order; -1 none or teardown).
        public int CrewBand(int crew)
        {
            if (InTeardown || crew < 0) return -1;
            int w = LayoutWindow, k = 0;
            for (int i = 0; i < BandCount; i++)
            {
                if (Band(i).Window != w) continue;
                if (k == crew) return i;
                k++;
            }
            return -1;
        }

        // Crew index of chain band i (-1 when the band is not in the layout window).
        public int CrewOfBand(int band)
        {
            if (InTeardown || band < 0 || band >= BandCount) return -1;
            int w = LayoutWindow;
            if (Band(band).Window != w) return -1;
            int k = 0;
            for (int i = 0; i < band; i++) if (Band(i).Window == w) k++;
            return k;
        }

        // The band's own progress now: before its window G0, in it G0 + (1 - G0) x Gw, after it 1.
        public float BandG(int i)
        {
            if (i < 0 || i >= BandCount) return 0f;
            var b = Band(i);
            if (Window > b.Window) return 1f;
            if (Window < b.Window) return math.saturate(b.G0);
            return UpgradePlan.BandG(b.G0, Gw);
        }

        public BandState StateOf(int i)
        {
            float g = BandG(i);
            return g >= 1f ? BandState.Done : g > 0f ? BandState.Partial : BandState.Unstarted;
        }

        // The layout window is the re-marking window and runs the Half primitive: the C4a / C4b layout of a new road applies
        // (ProjectView.HalvesActive).
        public bool RemarkHalf => Valid && RemarkWindow(AppliedWindow) && AppliedTraffic == BandTraffic.Half;
    }
}
