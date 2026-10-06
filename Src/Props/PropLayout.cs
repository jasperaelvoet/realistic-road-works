using System.Collections.Generic;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Props
{
    // Pure placement helpers: chain u -> edge, lateral conversions, slot fills with the module's
    // local continuity fixes, thinning and budget estimates. No ECS access.
    internal static class PropLayout
    {
        public const float kBarrierGap = 0.05f;        // gap between barriers in a line
        public const float kJunctionInset = 0.75f;     // junction end: barrier line this far INSIDE the trimmed chain
                                                       // (never on the junction node, never out onto the intersection pavement)
        public const float kDeadEndGap = 1.5f;         // hidden dead end: barrier line this far beyond the excavation (cap)
        public const float kOtherEndInset = 1f;        // other ends: line this far inside the trimmed chain
        public const float kKerbOffset = 0.6f;         // KerbLine barriers outside the carriage interval edges
        public const float kKerbTaperDeg = 20f;
        public const int kKerbPerSide = 3;
        public const float kSurveyConeOffset = 0.3f;   // beyond the composition half width
        public const float kEdgeConeOffset = 0.3f;     // beyond the carriage interval edges
        public const float kVergeConeOffset = 0.5f;    // D1/D2: beyond the composition half width
        public const float kSidewalkInset = 1.0f;      // mode D crew props: HalfWidth - this
        public const float kEndClearance = 2.5f;       // edge cones keep this far from the chain ends (barrier lines stand there)
        public const float kCoverTolerance = 1f;       // a slot whose u is farther than this from every project edge is skipped

        // ---- Footprint, foreign roads, fences
        public const float kFootprintSlack = 0.05f;    // a slot's u may lie this far outside [TrimU0, TrimU1] (float noise)
        public const float kForeignClearance = 0.05f;  // a prop point closer than HalfWidth + this to a non-works road's curve is inside it
        public const float kForeignMaxDy = 4f;         // ... unless the road is this far above / below (bridge, tunnel)
        public const float kFenceEndGap = 2f;          // footprint (trench) fences start / end this far inside the trims
        public const float kHalfLineCentreGap = 0.15f; // WorksHalf barrier line: first barrier this far into the works half from the split
        public const float kCentreConeEndGap = 4f;     // legacy centre cones (estimate helpers only)
        public const int kTopoAgreeTimeout = 8;        // updates to wait for the Director's junction flags after an end-node change
        // Approach signals: PropPlan.ApproachSignals && RRWSetting.ApproachSignalsOn
        // (= setting && RRWGates.AmberHead (experimental switch, default off) && PropsOn). Game-code note: EU/NA_TrafficLightCar01
        // carry no NetObject component (prefab dump: SpawnableObject, TrafficLightObject, StandingObject, ThemeObject), so
        // ObjectInitializeSystem gives them GeometryFlags.Overridable like any static prop; check in game with the switch on
        // (rrw.props.fx logs the flags).

        // ---- Staged traffic devices (kerb fences, cone pairs, signs, divider)
        public const float kFenceLineClear = 0.9f;     // kerb fence runs keep this far (chain u) from the closed-end barrier line (barrier
                                                       // half depth <= 0.32 m + the run-end cone at kFenceConeOffset + its radius)
        public const float kFenceShortMin = 1.2f;      // run remainder >= this -> one Short panel (else the joints are stretched)
        public const float kFenceOverlapMax = 0.4f;    // max overlap per joint when a Short panel is squeezed into the remainder
        public const float kPanelDrop = 0.03f;         // panel Y = min(curve Y at both feet) - this (no floating foot)
        public const float kFenceJitterDeg = 0.7f;     // deterministic yaw jitter per panel
        public const float kFenceJitterLat = 0.03f;    // deterministic lateral jitter per panel (m)
        public const float kFenceConeOffset = 0.35f;   // fence end / gap cones stand this far beyond the run end (chain u)
        public const float kConePairStep = 0.55f;      // 45-degree cone pair: +-this (u) around the line, this * 2 (lateral) apart
        public const float kConePairKerb = 0.35f;      // ... the outer cone this far inside the carriageway edge
        public const float kSpeedPlateBehind = 1.0f;   // speed plate this far behind (inside) the one-way sign
        public const float kSignalBeside = 0.9f;       // amber head this far beside the one-way sign, deeper into the works half
        public const float kDividerBarrierInset = 0.45f; // dev divider style Barriers: 0.45 m inside the closed band
        public const int kDividerBeaconEvery = 5;      // ... every 5th a beacon
        public const int kLampEvery = 4;               // divider: every 4th cone a cone lamp (experimental switch RRWGates.ConeLamps)
        public const int kMaxFencePanelsPerRun = 255;  // key space (k = run << 8 | panel)

        // Edge of the chain that holds chain coordinate u (nearest edge if u is outside every edge's range) and the
        // UNCLAMPED edge-local distance s (EdgeArc.Offset extends linearly past the curve ends). gap = distance of u
        // outside the returned edge's chain range (0 inside).
        public static ChainEdge Locate(List<ChainEdge> chain, float u, out float s, out float gap)
        {
            s = 0f;
            gap = float.MaxValue;
            ChainEdge best = null;
            for (int i = 0; i < chain.Count; i++)
            {
                var ce = chain[i];
                float lo = ce.Lo, hi = ce.Hi;
                float d = u < lo ? lo - u : u > hi ? u - hi : 0f;
                if (d < gap) { gap = d; best = ce; }
                if (d <= 0f) break;
            }
            if (best == null) return null;
            float len = best.Record != null && best.Record.Arc != null ? best.Record.Arc.Length : 0f;
            float du = best.U1 - best.U0;
            s = math.abs(du) > 1e-3f ? (u - best.U0) / du * len : 0f;
            return best;
        }

        // Chain lateral (+ = right of the CHAIN direction) -> edge lateral (+ = right of the CURVE direction).
        public static float EdgeLateral(ChainEdge ce, float chainLateral) => chainLateral * ce.DirSign;

        // ---- Chain-frame laterals (EdgeSection.DirSplit / DriveLo/Hi; pure)

        // +1 when the works half is the chain-right half, -1 for the chain-left half (None / both: the Core default works half).
        public static float WorksSign(RoadZones worksHalf)
        {
            var h = worksHalf & RoadZones.Carriageway;
            if (h == RoadZones.RightHalf) return 1f;
            if (h == RoadZones.LeftHalf) return -1f;
            return (RRWConst.kStagedWorksHalf & RoadZones.RightHalf) != 0 ? 1f : -1f;
        }

        // Carriage intervals in the CHAIN frame (lo < hi); falls back to the outer carriageway edges.
        public static int ChainIntervals(in EdgeSection sec, bool rev, out float2 a, out float2 b, out float2 c)
        {
            a = b = c = default;
            int n = sec.IntervalCount;
            if (n <= 0)
            {
                RoadZoneMath.CarriageChain(sec, rev, out float lo, out float hi);
                a = new float2(lo, hi);
                return 1;
            }
            n = math.min(n, EdgeSection.kMaxIntervals);
            for (int i = 0; i < n; i++)
            {
                float2 iv = sec.Interval(i);
                float2 ch = rev ? new float2(-iv.y, -iv.x) : iv;
                if (i == 0) a = ch; else if (i == 1) b = ch; else c = ch;
            }
            return n;
        }

        // Divider device lateral (chain frame): DirSplitC + sgn * inset into the works half. On a divided road the split lies in
        // the median: the device moves to the works half's inner carriage edge + inset (it stays on the asphalt). NaN = no split.
        public static float DividerLateral(in EdgeSection sec, bool rev, RoadZones worksHalf, float inset)
        {
            float split = RoadZoneMath.DirSplitChain(sec, rev);
            if (float.IsNaN(split)) return float.NaN;
            float sgn = WorksSign(worksHalf);
            float lat = split + sgn * inset;
            int n = ChainIntervals(sec, rev, out float2 i0, out float2 i1, out float2 i2);
            float best = float.NaN, bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float2 iv = i == 0 ? i0 : i == 1 ? i1 : i2;
                if (lat >= iv.x && lat <= iv.y) return lat;                    // on the asphalt
                // the works-side interval's inner edge (the interval lies on the works side of the split)
                if (sgn > 0f && iv.y > split) { float e = math.max(iv.x, split) + inset; float d = math.abs(e - lat); if (d < bestD) { bestD = d; best = e; } }
                if (sgn < 0f && iv.x < split) { float e = math.min(iv.y, split) - inset; float d = math.abs(e - lat); if (d < bestD) { bestD = d; best = e; } }
            }
            return float.IsNaN(best) ? lat : best;
        }

        // Kerb fence lateral of a chain side (0 = left, 1 = right), chain frame. Inset: kKerbFenceInset INSIDE the carriageway
        // edge; Sidewalk: kKerbFenceOutset outside it. walk = that side has a sidewalk band (no pedestrians -> no fence).
        public static float FenceLat(in EdgeSection sec, bool rev, int side, FenceLateral mode, out bool walk)
        {
            RoadZoneMath.CarriageChain(sec, rev, out float lo, out float hi);
            float hw = sec.HalfWidth;
            float need = 0.3f + RRWConst.kKerbFenceOutset;
            walk = side == 0 ? lo > -hw + need : hi < hw - need;
            if (mode == RealisticRoadWorks.V3.FenceLateral.Sidewalk) return side == 0 ? lo - RRWConst.kKerbFenceOutset : hi + RRWConst.kKerbFenceOutset;
            return side == 0 ? lo + RRWConst.kKerbFenceInset : hi - RRWConst.kKerbFenceInset;
        }

        // Outer end of the closed-end barrier line on an edge whose parking stays vanilla (edges with buildings;
        // verified in game: parked cars may stand in the strip all through the works): the works side's drive-lane edge instead of the
        // carriageway edge, so the line never runs through parked cars. Unchanged without buildings / without a parking strip.
        public static float OuterBesideParking(in EdgeSection sec, bool rev, float sgn, float outer, int buildings)
        {
            if (buildings <= 0) return outer;
            RoadZoneMath.DriveChain(sec, rev, out float dlo, out float dhi);
            if (dhi - dlo <= 0.5f) return outer;
            if (sgn > 0f && dhi < outer - 0.3f) return dhi;
            if (sgn < 0f && dlo > outer + 0.3f) return dlo;
            return outer;
        }

        // Works-half carriage edges for the closed-end line / signs (chain frame): inner = split (NaN -> curve centre),
        // outer = the works side's carriageway edge.
        public static void WorksHalfEdges(in EdgeSection sec, bool rev, RoadZones worksHalf, out float inner, out float outer, out float sgn)
        {
            RoadZoneMath.CarriageChain(sec, rev, out float lo, out float hi);
            sgn = WorksSign(worksHalf);
            float split = RoadZoneMath.DirSplitChain(sec, rev);
            inner = float.IsNaN(split) ? 0f : split;
            outer = sgn > 0f ? hi : lo;
        }

        // Thinning factor of a group at a thinning level. Heaps first (level 1), then edge / survey cones
        // (level 2), then divider cones (level 3: 6 -> 12 m, never sparser); never fences, closed-end lines, signs, dump heaps
        // (truck targets), crew props or barriers.
        public static int ThinFactor(PropKind kind, int level)
        {
            if (level <= 0) return 1;
            switch (kind)
            {
                case PropKind.SpoilHeap:
                case PropKind.Windrow:
                case PropKind.Rubble:
                    return 1 << math.min(4, (level + 1) / 2);         // 1:2 2:2 3:4 4:4 5:8 ...
                case PropKind.SurveyCone:
                case PropKind.EdgeCone:
                    return level < 2 ? 1 : 1 << math.min(4, level / 2);   // 1:1 2:2 3:2 4:4 ...
                case PropKind.CentreCone:
                    return level < 3 ? 1 : 2;                         // divider: kCentreConeSpacing -> kDividerFarSpacing
                default:
                    return 1;
            }
        }

        // Divider cone slots over [Trim0 + kDividerEndGap, Trim1 - kDividerEndGap] on the kCentreConeSpacing grid.
        public static int DividerCount(in ProjectView v)
        {
            float len = v.TrimmedLength - 2f * RRWConst.kDividerEndGap;
            return len < 0f ? 0 : (int)math.floor(len / RRWConst.kCentreConeSpacing) + 1;
        }

        public static float DividerU(int k, in ProjectView v) => v.Trim0 + RRWConst.kDividerEndGap + RRWConst.kCentreConeSpacing * k;

        // Props that are never thinned per site: kerb fences (+ run / gap cones), closed-end extras, signs, head.
        public static int FixedEstimate(in ProjectView v, in PropPlan plan, float fencePitch, int gaps)
        {
            int n = 0;
            if (plan.Fence == FenceStyle.Kerbs)
            {
                int sides = ((plan.FenceSides & RoadZones.SidewalkLeft) != 0 ? 1 : 0) + ((plan.FenceSides & RoadZones.SidewalkRight) != 0 ? 1 : 0);
                float len = v.TrimmedLength - 2f * RRWConst.kFenceEndGap;
                if (len > 0f) n += sides * ((int)math.floor(len / math.max(0.5f, fencePitch)) + 2 + 2) + gaps * 3;
            }
            if (plan.Barriers == BarrierStyle.WorksHalf) n += 4 + 6;   // cone pairs + chase beacons
            if (plan.Signs) n += 3 + 1;                                // one-way, speed plate, no-entry (+ amber head)
            return n;
        }

        // Peak simultaneous prop count of a project's layout at a thinning level (overestimate, for the prop budget).
        // `fixedCount` (FixedEstimate) is added at every level, so thinning removes heaps / cones / divider cones only.
        public static int Estimate(in ProjectView v, int level, in PropPlan plan, int fixedCount)
        {
            int n = Estimate(v, level) + fixedCount;
            if (plan.Divider != DividerStyle.None) n += Ceil(DividerCount(v), ThinFactor(PropKind.CentreCone, level));
            return n;
        }

        public static int Estimate(in ProjectView v, int level)
        {
            bool A = v.Mode == VisualMode.FullDig;
            bool constr = v.Kind == WorksKind.Construction;
            int n = 16 + 5;   // two barrier lines + crew props
            if (A && constr)
            {
                n += 2 * Ceil(PhasePlan.SlotCount(PropGroup.SurveyCones, v), ThinFactor(PropKind.SurveyCone, level));
                n += Ceil(PhasePlan.SlotCount(PropGroup.SpoilHeaps, v), ThinFactor(PropKind.SpoilHeap, level));
                n += math.min(3, PhasePlan.SlotCount(PropGroup.DumpHeaps, v));
                return n;
            }
            n += 2 * Ceil(PhasePlan.SlotCount(PropGroup.EdgeCones, v), ThinFactor(PropKind.EdgeCone, level));
            if (A)
            {
                n += Ceil(PhasePlan.SlotCount(PropGroup.StoneWindrow, v), ThinFactor(PropKind.Windrow, level));
                n += Ceil(PhasePlan.SlotCount(PropGroup.Rubble, v), ThinFactor(PropKind.Rubble, level));
                n += math.min(3, PhasePlan.SlotCount(PropGroup.DumpHeaps, v));
                if (v.Cancelled) n += Ceil(PhasePlan.SlotCount(PropGroup.SpoilHeaps, v), ThinFactor(PropKind.SpoilHeap, level));
            }
            return n;
        }

        static int Ceil(int n, int f) => f <= 1 ? n : (n + f - 1) / f;

        // Footprint (trench) fence panels a plan needs, overestimated as if every edge had buildings. Kerb fences: FixedEstimate.
        public static int FenceEstimate(in ProjectView v, in PropPlan plan, float pitch)
        {
            if (plan.Fence != FenceStyle.Footprint) return 0;
            float len = v.TrimmedLength - 2f * kFenceEndGap;
            if (len <= 0f) return 0;
            int perSide = (int)math.floor(len / math.max(0.5f, pitch)) + 1;
            return 2 * perSide;
        }

        // C4 pick-up: the crew truck collects the divider cones between the start and the end barrier stops (f .92-.96).
        public static bool CentreConeStanding(float u, in ProjectView v)
        {
            if (v.Phase != WorksPhase.Finishing) return true;
            float pick = v.Trim0 + v.TrimmedLength * PhasePlan.Sweep(v.F, 0.92f, 0.96f);
            return v.F < 0.92f || u > pick;
        }

        // Smallest thinning level that keeps the project within kPropBudgetPerSite (the never-thinned devices count at
        // every level, so a long fenced site thins its heaps and cones first).
        public static int ThinLevel(in ProjectView v, in PropPlan plan, int fixedCount, out int estimate)
        {
            int level = 0;
            estimate = Estimate(v, 0, plan, fixedCount);
            while (estimate > RRWConst.kPropBudgetPerSite && level < 8)
            {
                level++;
                estimate = Estimate(v, level, plan, fixedCount);
            }
            return level;
        }

        // Slot fill (-1 = no entity, 1..255). The spoil-heap continuity fixes this module needs live in
        // Core's PhasePlan.PropFill, so Props, Machines and Dev all see the same fills.
        public static int Fill(PropGroup g, int k, in ProjectView v) => PhasePlan.PropFill(g, k, v);
        // plan = PhasePlan.Props(v) of this update (the per-update plan0).
        public static int Fill(PropGroup g, int k, in ProjectView v, in PropPlan plan) => PhasePlan.PropFill(g, k, v, plan);

        // PhasePlan fill (1..255) -> Quantity.m_Fullness percent (1..100). Floor, so the 170 windrow cap stays a
        // "Partial2" mesh (66) and a full heap is 100.
        public static byte Percent(int fill) => (byte)math.clamp(fill * 100 / 255, 1, 100);

        // Mesh step a QuantityObject shows for a fullness (BatchDataHelpers.CalculateQuantitySubMeshData).
        public static int MeshStep(uint stepMask, int fullness)
        {
            switch (stepMask & 6u)
            {
                case 6u: return fullness > 66 ? 3 : fullness > 33 ? 2 : fullness > 0 ? 1 : 0;
                case 4u: return fullness > 50 ? 3 : fullness > 0 ? 2 : 0;
                case 2u: return fullness > 50 ? 3 : fullness > 0 ? 1 : 0;
                default: return fullness == 0 ? 0 : 3;
            }
        }

        // Deterministic [-1, 1] jitter per (project seed, slot key, channel).
        public static float Jitter(uint seed, int key, uint channel)
        {
            uint h = BeaconPhase.Hash(seed ^ (channel * 0x27D4EB2Fu), (uint)key);
            return (h & 0xFFFFu) / 32767.5f - 1f;
        }

        public static int Pick(uint seed, int key, int count) => count <= 1 ? 0 : (int)(BeaconPhase.Hash(seed + 0x51u, (uint)key) % (uint)count);
    }
}
