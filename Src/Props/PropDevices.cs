using System.Collections.Generic;
using Game.Buildings;
using Unity.Entities;
using Unity.Mathematics;
using ObjTransform = Game.Objects.Transform;

namespace RealisticRoadWorks.V3.Props
{
    // The staged-traffic devices of a project, laid out from PhasePlan.Props(view)
    // (PropPlan.FenceSides / Divider / WorksHalf / Signs / ApproachSignals / InLaneReady / OpenEntryAtEnd) and the chain
    // geometry (EdgeSection.DirSplit). Every device is a normal slot (Apply): a layout change replaces old and new in the same
    // diff (no frame without a device). The in-lane devices (divider, closed-end lines, signs, amber head) come from the plan only while
    // InLaneReady; the kerb fences (out-of-lane) follow FenceSides at once. Fences, closed-end lines and signs are never
    // thinned per site; divider cones go from 6 to 12 m at thinning level 3 and beyond kPropFarDistance.
    public partial class PropSystem
    {
        // ---- prefabs (resolved in ResolvePrefabs; [0] = EU, [1] = NA)
        private readonly Entity[] m_SignOneway = new Entity[2], m_SignNoEntry = new Entity[2], m_SignalHead = new Entity[2];
        private Entity m_FenceCone, m_ConeLamp;
        private bool m_FenceFallback;          // kerb fence = SafetyBarrier line (fence variant switch = -1 or the panel did not resolve)

        // ---- per-update settings snapshot
        private bool m_Na, m_ApproachOn;
        private int m_SpeedKmh = 30;
        private int m_LampSites;               // staged sites with cone lamps this update (nearest first, <= kStagedLampSites)

        // ---- kerb fence layout cache (chord placement is ~10 curve evaluations per panel: only rebuilt when its key changes)
        private readonly Dictionary<uint, List<Want>> m_FenceCache = new Dictionary<uint, List<Want>>();
        private readonly List<float2> m_Excl = new List<float2>(16);
        private readonly List<float2> m_Runs = new List<float2>(16);
        private readonly List<byte> m_PanelTypes = new List<byte>(128);

        private Entity Themed(Entity[] pair) => m_Na && pair[1] != Entity.Null ? pair[1] : pair[0];

        // ------------------------------------------------------------------ building access points (driveway gaps)

        // Driveway gaps exist only on a fenced side whose car half is CLOSED-B (cars cross the works strip there). Upgrade works
        // also need them for the band fences (gaps at every access) and the band heaps (keep-outs).
        private static bool NeedsAccess(ProjectProps pp, in PropPlan plan)
        {
            if (plan.Upgrade && (plan.BandMask != 0 || plan.WaitingMask != 0)) return true;
            if (plan.Fence != FenceStyle.Kerbs) return false;
            RoadZones halves = RoadZoneMath.HalfOfSide(plan.FenceSides);
            for (int i = 0; i < pp.Chain.Count; i++)
                if ((pp.Chain[i].Record.ClosedBApplied & halves) != 0) return true;
            return false;
        }

        private static uint ClosedBKeyOf(ProjectProps pp)
        {
            uint h = 0x7F4A7C15u;
            for (int i = 0; i < pp.Chain.Count; i++) h = Mix(h, (uint)pp.Chain[i].Record.ClosedBApplied + 1u);
            return h;
        }

        // Re-collects the access points when needed (CLOSED-B applied on a fenced side): on the first need, on a ClosedBApplied
        // change and on every self-heal pass (buildings spawn during the works). Cheap otherwise (one flag check per edge).
        private void UpdateAccess(ProjectRecord p, ProjectProps pp, in PropPlan plan, bool heal)
        {
            if (!NeedsAccess(pp, plan))
            {
                if (pp.Access.Count > 0 || pp.AccessKey != 0) { pp.Access.Clear(); pp.AccessKey = 0; pp.AccessUpdate = 0; }
                return;
            }
            uint cb = ClosedBKeyOf(pp);
            if (pp.AccessUpdate != 0 && !heal && cb == pp.ClosedBKey) return;
            pp.ClosedBKey = cb;
            pp.AccessUpdate = math.max(1u, m_Update);
            uint before = pp.AccessKey;
            CollectAccess(pp);
            if (pp.AccessKey != before)
                RRWLog.Info("props: project #" + p.Id + " building access points " + pp.Access.Count + (plan.Upgrade ? " (gaps in the band fences, heap keep-outs)" :
                            " (driveway gaps while CLOSED-B: " + RoadZoneMath.Describe(RoadZones.None | AppliedClosedB(pp)) + ")"));
        }

        private static RoadZones AppliedClosedB(ProjectProps pp)
        {
            RoadZones z = RoadZones.None;
            for (int i = 0; i < pp.Chain.Count; i++) z |= pp.Chain[i].Record.ClosedBApplied;
            return z;
        }

        // Building.m_RoadEdge = the edge, m_CurvePosition = curve t of the access; side from the building position (chain frame).
        private void CollectAccess(ProjectProps pp)
        {
            var em = EntityManager;
            pp.Access.Clear();
            uint h = 0x165667B1u;
            for (int i = 0; i < pp.Chain.Count; i++)
            {
                var ce = pp.Chain[i];
                var arc = ce.Record.Arc;
                if (arc == null || !em.Exists(ce.Edge) || !em.HasBuffer<ConnectedBuilding>(ce.Edge)) continue;
                var buf = em.GetBuffer<ConnectedBuilding>(ce.Edge, true);
                for (int k = 0; k < buf.Length; k++)
                {
                    Entity b = buf[k].m_Building;
                    if (!EcsUtil.Alive(em, b) || !em.HasComponent<Building>(b) || !em.HasComponent<ObjTransform>(b)) continue;
                    var bd = em.GetComponentData<Building>(b);
                    if (bd.m_RoadEdge != ce.Edge) continue;
                    float s = arc.SAt(bd.m_CurvePosition);
                    float u = ce.U0 + (ce.U1 - ce.U0) * (s / math.max(0.001f, arc.Length));
                    float3 bp = em.GetComponentData<ObjTransform>(b).m_Position;
                    float3 cp = arc.Position(s);
                    float latEdge = math.dot((bp - cp).xz, arc.Right(s).xz);
                    byte side = (byte)(latEdge * ce.DirSign < 0f ? 0 : 1);
                    pp.Access.Add(new AccessPoint { Building = b, Edge = ce.Edge, U = u, Side = side });
                    h = Mix(h, Mix(Mix((uint)b.Index, (uint)math.round(u * 10f)), side));
                }
            }
            pp.AccessKey = pp.Access.Count == 0 ? 0u : h;
        }

        // ------------------------------------------------------------------ kerb fences

        private uint FenceKeyOf(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan)
        {
            uint h = Mix(Mix((uint)p.Revision, Bits(v.Trim0)), Bits(v.Trim1));
            h = Mix(h, (uint)plan.FenceSides | ((uint)v.Closure << 16));
            h = Mix(h, (p.StartShared ? 1u : 0u) | (p.EndShared ? 2u : 0u) | (p.StartIsJunction ? 4u : 0u) | (p.EndIsJunction ? 8u : 0u));
            h = Mix(h, (uint)RRWGates.Revision);
            h = Mix(h, Mix(pp.AccessKey, pp.ClosedBKey));
            h = Mix(h, Mix((uint)(m_KerbFence.Length > 0 ? m_KerbFence[0].Index : 0), (uint)m_FenceCone.Index));
            for (int i = 0; i < pp.Chain.Count; i++)
            {
                var ce = pp.Chain[i];
                h = Mix(h, Mix(ce.GeoStamp, ce.HaveRuntime && ce.Runtime.Hidden ? 1u : 0u));
            }
            return h == 0 ? 1u : h;
        }

        private void KerbFences(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan)
        {
            if (!m_FenceCache.TryGetValue(p.Id, out var list))
            {
                list = new List<Want>(64);
                m_FenceCache[p.Id] = list;
                pp.FenceKey = 0;
            }
            uint key = FenceKeyOf(p, pp, v, plan);
            if (pp.FenceKey != key)
            {
                BuildKerbFences(p, pp, v, plan, list);
                pp.FenceKey = key;
                RRWLog.Verbose("props: project #" + p.Id + " kerb fence layout " + RoadZoneMath.Describe(plan.FenceSides) + ": runs=" + pp.FenceRuns +
                               " panels=" + pp.FencePanels + " (short " + pp.FenceShorts + ") cones=" + pp.FenceCones + " gaps=" + pp.FenceGaps +
                               " joint=" + RRWLog.F(pp.FenceDelta) + " lateral=" + RRWGates.FenceLateral + (m_FenceFallback ? " (barrier fallback)" : ""));
            }
            for (int i = 0; i < list.Count; i++) Apply(p, pp, list[i]);
        }

        // Chain lateral of a fence line on an edge: the staged kerb fence of a chain side (line == null), or a band fence line given
        // per chain edge (ChainEdge.Index; NaN = no fence on that edge).
        private static float LineLat(ChainEdge ce, int side, float[] line)
        {
            if (line == null) return PropLayout.FenceLat(ce.Record.Section, ce.DirSign < 0f, side, RRWGates.FenceLateral, out bool _);
            return ce.Index >= 0 && ce.Index < line.Length ? line[ce.Index] : float.NaN;
        }

        // A world point on the fence line of a chain side at chain u (curve Y). False when u lies on no project edge (or on an edge
        // without that band line).
        private bool FencePoint(ProjectProps pp, float u, int side, out float3 pos, out ChainEdge ce, out float latChain, float[] line = null)
        {
            pos = default;
            latChain = 0f;
            ce = PropLayout.Locate(pp.Chain, u, out float s, out float gap);
            if (ce == null || ce.Record.Arc == null || gap > PropLayout.kCoverTolerance) return false;
            latChain = LineLat(ce, side, line);
            if (float.IsNaN(latChain)) return false;
            pos = ce.Record.Arc.Offset(s, PropLayout.EdgeLateral(ce, latChain));
            return true;
        }

        // Chain u' > u whose fence point lies `len` (XZ chord) from the fence point at u.
        private float ChordStep(ProjectProps pp, float u, float len, int side, float[] line = null)
        {
            float uN = u + len;
            if (!FencePoint(pp, u, side, out float3 a, out _, out _, line)) return uN;
            for (int k = 0; k < 6; k++)
            {
                if (!FencePoint(pp, uN, side, out float3 b, out _, out _, line)) break;
                float d = math.distance(a.xz, b.xz);
                if (d < 1e-3f) break;
                float nu = u + (uN - u) * len / d;
                bool done = math.abs(nu - uN) < 1e-3f;
                uN = nu;
                if (done) break;
            }
            return uN;
        }

        // Curve radius at the fence line is below kFenceShortRadius (inner side: R - lateral).
        private bool TightAt(ProjectProps pp, float u, int side, float[] line = null)
        {
            var ce = PropLayout.Locate(pp.Chain, u, out float s, out float gap);
            if (ce == null || ce.Record.Arc == null) return false;
            var arc = ce.Record.Arc;
            float r = arc.SignedRadius(math.clamp(s, 0f, arc.Length));
            if (float.IsInfinity(r) || float.IsNaN(r)) return false;
            float lc = LineLat(ce, side, line);
            if (float.IsNaN(lc)) return false;
            float latEdge = PropLayout.EdgeLateral(ce, lc);
            float eff = math.abs(r) - math.sign(r) * latEdge;   // r > 0 turns right: the right side (lat > 0) is the inner side
            return eff < RRWConst.kFenceShortRadius;
        }

        // [a, b] minus the exclusions (sorted, merged); runs shorter than kFenceShortMin are dropped.
        private void BuildRuns(float a, float b)
        {
            m_Runs.Clear();
            m_Excl.Sort((x, y) => x.x.CompareTo(y.x));
            float cur = a;
            for (int i = 0; i < m_Excl.Count; i++)
            {
                float2 e = m_Excl[i];
                if (e.y <= cur) continue;
                if (e.x >= b) break;
                if (e.x > cur && e.x - cur >= PropLayout.kFenceShortMin) m_Runs.Add(new float2(cur, e.x));
                cur = math.max(cur, e.y);
            }
            if (b - cur >= PropLayout.kFenceShortMin) m_Runs.Add(new float2(cur, b));
        }

        private void BuildKerbFences(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan, List<Want> list)
        {
            list.Clear();
            pp.FencePanels = pp.FenceShorts = pp.FenceCones = pp.FenceGaps = pp.FenceRuns = 0;
            pp.FenceDelta = 0f;
            pp.FenceFallback = m_FenceFallback;
            Entity[] main = m_FenceFallback ? m_Safety : m_KerbFence;
            Entity[] shortF = m_FenceFallback ? new Entity[0] : m_KerbFenceShort;
            float L = m_FenceFallback ? m_SafetyPitch : m_KerbFencePitch;
            float Ls = m_KerbShortLen;
            bool haveShort = shortF.Length > 0 && Ls >= 0.5f;
            if (main == null || main.Length == 0 || L < 0.5f) return;
            var mode = RRWGates.FenceLateral;
            YMode ym = mode == FenceLateral.Inset ? YMode.Panel : YMode.PanelSidewalk;

            // run limits: kFenceEndGap inside the trims, and kFenceLineClear clear of the closed-end barrier line position. Always
            // while Closed (kerb fences exist only then), NOT from the transient BarrierStyle (None during a switch): the run must not
            // re-lay (and every panel move) when the lines go down for Swap / Drain and come back at Ready.
            bool lines = v.Closure == ClosureLevel.Closed;
            float a = v.Trim0 + RRWConst.kFenceEndGap, b = v.Trim1 - RRWConst.kFenceEndGap;
            if (lines && !p.StartShared) a = math.max(a, v.Trim0 + (p.StartIsJunction ? PropLayout.kJunctionInset : PropLayout.kOtherEndInset) + PropLayout.kFenceLineClear);
            if (lines && !p.EndShared) b = math.min(b, v.Trim1 - (p.EndIsJunction ? PropLayout.kJunctionInset : PropLayout.kOtherEndInset) - PropLayout.kFenceLineClear);
            if (b - a < PropLayout.kFenceShortMin) return;

            for (int side = 0; side < 2; side++)
            {
                RoadZones walkBit = side == 0 ? RoadZones.SidewalkLeft : RoadZones.SidewalkRight;
                if ((plan.FenceSides & walkBit) == 0) continue;
                RoadZones half = side == 0 ? RoadZones.LeftHalf : RoadZones.RightHalf;
                m_Excl.Clear();
                for (int i = 0; i < pp.Chain.Count; i++)
                {
                    var ce = pp.Chain[i];
                    PropLayout.FenceLat(ce.Record.Section, ce.DirSign < 0f, side, mode, out bool walk);
                    bool hidden = ce.HaveRuntime && ce.Runtime.Hidden;   // kerb fences stand on the visible road only
                    if (!walk || hidden) m_Excl.Add(new float2(ce.Lo, ce.Hi));
                }
                for (int i = 0; i < pp.Access.Count; i++)
                {
                    var ap = pp.Access[i];
                    if (ap.Side != side || !SiteRegistry.TryGetEdge(ap.Edge, out var rec) || (rec.ClosedBApplied & half) == 0) continue;
                    float hw = RRWConst.kAccessGapWidth * 0.5f;
                    m_Excl.Add(new float2(ap.U - hw, ap.U + hw));
                    pp.FenceGaps++;
                }
                BuildRuns(a, b);
                for (int ri = 0; ri < m_Runs.Count && ri < 256; ri++)
                    LayRun(p, pp, list, side, ri, m_Runs[ri].x, m_Runs[ri].y, main, shortF, L, Ls, haveShort, ym);
                pp.FenceRuns += m_Runs.Count;
            }
        }

        // One fence run [r0, r1] of a side. Pass 1 chooses the panel types (4 m; Short on R < kFenceShortRadius or |dY| >
        // kFenceStepMaxDY; one Short in a remainder >= kFenceShortMin); pass 2 spreads the rest over the joints (stretch <=
        // kFencePitchStretchMax, overlap <= kFenceOverlapMax) and places each panel on its chord, centred, pivot-corrected.
        // line / panelKind / coneKind: a band fence line (PropUpgrade.cs) with its own keys; default: the staged kerb fence.
        private void LayRun(ProjectRecord p, ProjectProps pp, List<Want> list, int side, int ri, float r0, float r1,
                            Entity[] main, Entity[] shortF, float L, float Ls, bool haveShort, YMode ym,
                            float[] line = null, PropKind panelKind = PropKind.Fence, int panelSub = -1, PropKind coneKind = PropKind.FenceCone, int coneSub = -1)
        {
            if (panelSub < 0) panelSub = 4 + side;
            if (coneSub < 0) coneSub = side;
            m_PanelTypes.Clear();
            float u = r0;
            for (int guard = 0; guard < 1024 && m_PanelTypes.Count < PropLayout.kMaxFencePanelsPerRun; guard++)
            {
                bool sh = haveShort && TightAt(pp, u, side, line);
                float uN = ChordStep(pp, u, sh ? Ls : L, side, line);
                if (uN > r1 + 1e-3f) break;
                if (!sh && haveShort && FencePoint(pp, u, side, out float3 pa, out _, out _, line) && FencePoint(pp, uN, side, out float3 pb, out _, out _, line)
                    && math.abs(pb.y - pa.y) > RRWConst.kFenceStepMaxDY)
                {
                    sh = true;
                    uN = ChordStep(pp, u, Ls, side, line);
                    if (uN > r1 + 1e-3f) break;
                }
                m_PanelTypes.Add(sh ? (byte)1 : (byte)0);
                u = uN;
            }
            float rest = 0f;
            if (FencePoint(pp, u, side, out float3 ru, out _, out _, line) && FencePoint(pp, r1, side, out float3 re, out _, out _, line))
                rest = math.distance(ru.xz, re.xz);
            if (haveShort && rest >= PropLayout.kFenceShortMin && m_PanelTypes.Count < PropLayout.kMaxFencePanelsPerRun)
            {
                m_PanelTypes.Add(1);
                rest -= Ls;
            }
            int n = m_PanelTypes.Count;
            if (n == 0) return;
            float delta = math.clamp(rest / n, -PropLayout.kFenceOverlapMax, RRWConst.kFencePitchStretchMax);
            pp.FenceDelta = delta;

            u = r0;
            for (int i = 0; i < n; i++)
            {
                bool sh = m_PanelTypes[i] == 1;
                float len = sh ? Ls : L;
                float uN = ChordStep(pp, u, len + delta, side, line);
                if (!FencePoint(pp, u, side, out float3 A, out var ce, out float latA, line) || !FencePoint(pp, uN, side, out float3 B, out _, out float latB, line))
                {
                    u = uN;
                    continue;
                }
                float lift = ym == YMode.PanelSidewalk ? SidewalkLift(ce.Edge) : 0f;
                float3 mid = (A + B) * 0.5f;
                mid.y = math.min(A.y, B.y) - PropLayout.kPanelDrop + lift;
                Entity prefab = sh ? Pick(shortF, i + side) : Pick(main, i + side);
                list.Add(new Want
                {
                    Key = PropKeys.Make(panelKind, panelSub, (ri << 8) | i),
                    Kind = panelKind,
                    U = (u + uN) * 0.5f,
                    Lateral = (latA + latB) * 0.5f,
                    LatChain = true,
                    Y = ym,
                    Rot = RotMode.Chord,
                    World = true,
                    WorldPos = mid,
                    WorldDir = B - A,
                    FootA = A,
                    FootB = B,
                    Lift = lift,
                    Pivot = true,
                    JitterDeg = PropLayout.kFenceJitterDeg,
                    Fill = 255,
                    Prefab = prefab,
                    Group = DerivedGroup.PropStatic,
                });
                pp.FencePanels++;
                if (sh) pp.FenceShorts++;
                u = uN;
            }
            // a SafetyCone02 at each run end (fence ends at the closed-end lines, both sides of a driveway gap)
            if (m_FenceCone == Entity.Null) return;
            for (int e = 0; e < 2; e++)
            {
                float uc = e == 0 ? r0 - PropLayout.kFenceConeOffset : r1 + PropLayout.kFenceConeOffset;
                var ce = PropLayout.Locate(pp.Chain, uc, out float _, out float gap);
                if (ce == null || gap > PropLayout.kCoverTolerance) continue;
                float lat = LineLat(ce, side, line);
                if (float.IsNaN(lat)) continue;
                list.Add(new Want
                {
                    Key = PropKeys.Make(coneKind, coneSub, ri * 2 + e),
                    Kind = coneKind,
                    U = uc,
                    Lateral = lat,
                    LatChain = true,
                    Y = ym == YMode.PanelSidewalk ? YMode.Sidewalk : YMode.Curve,
                    Rot = RotMode.Random,
                    Fill = 255,
                    Prefab = m_FenceCone,
                    Group = DerivedGroup.PropStatic,
                });
                pp.FenceCones++;
            }
        }

        // ------------------------------------------------------------------ divider

        // The car half the in-lane devices mark as the works half: PropPlan.WorksHalf while a half carries traffic; else the
        // stage's works car half (staged C4 without a car half: the painter's half).
        private static RoadZones WorksCarHalf(in ProjectView v, in PropPlan plan)
        {
            if (plan.WorksHalf != RoadZones.None) return plan.WorksHalf & RoadZones.Carriageway;
            var st = PhasePlan.Stage(v);
            return st.Works & RoadZones.Carriageway & ~v.OpenLanes;
        }

        private void Divider(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan, RoadZones works, bool far)
        {
            pp.DividerCones = pp.DividerLamps = 0;
            var style = RRWGates.Divider;   // PropPlan.Divider mapped through the dev selector
            if (style == DividerStyle.None || plan.Divider == DividerStyle.None) return;
            if (style == DividerStyle.Barriers) { DividerBarriers(p, pp, v, works); return; }
            if (m_TallCone == Entity.Null) return;
            int n = PropLayout.DividerCount(v);
            int factor = far ? 2 : PropLayout.ThinFactor(PropKind.CentreCone, pp.Thin);   // 6 m -> kDividerFarSpacing (12 m)
            int lamps = 0;
            for (int k = 0; k < n; k++)
            {
                if (factor > 1 && k % factor != 0) continue;
                float u = PropLayout.DividerU(k, v);
                if (!PropLayout.CentreConeStanding(u, v)) continue;
                var ce = PropLayout.Locate(pp.Chain, u, out float _, out float gap);
                if (ce == null || gap > PropLayout.kCoverTolerance) continue;
                float lat = PropLayout.DividerLateral(ce.Record.Section, ce.DirSign < 0f, works, RRWConst.kDividerInset);
                if (float.IsNaN(lat)) continue;   // an edge without a split (one direction): no divider there
                bool lamp = pp.LampSite && !far && m_ConeLamp != Entity.Null && k % PropLayout.kLampEvery == 0 && lamps < RRWConst.kStagedLampCap;
                Apply(p, pp, new Want
                {
                    Key = PropKeys.Make(PropKind.CentreCone, 1, k),
                    Kind = PropKind.CentreCone,
                    U = u,
                    Lateral = lat,
                    LatChain = true,
                    Y = YMode.Curve,
                    Rot = RotMode.Random,
                    Fill = 255,
                    Prefab = lamp ? m_ConeLamp : m_TallCone,
                    Seed = lamp ? BeaconPhase.Chase(lamps) : (ushort)0,   // lamps: sequential phase along the divider
                    Group = DerivedGroup.PropStatic,
                });
                pp.DividerCones++;
                if (lamp) { lamps++; pp.DividerLamps++; }
            }
        }

        // Dev selector `rrw.gate divider barriers` (alternative look): continuous SafetyBarrier line 0.45 m inside the works half,
        // every 5th a beacon (chase), long side along the road.
        private void DividerBarriers(ProjectRecord p, ProjectProps pp, in ProjectView v, RoadZones works)
        {
            float pitch = m_SafetyPitch;
            float a = v.Trim0 + RRWConst.kDividerEndGap, b = v.Trim1 - RRWConst.kDividerEndGap;
            if (b - a < pitch || m_Safety.Length == 0) return;
            int n = (int)math.floor((b - a) / pitch);
            bool beacons = m_BeaconsOn && m_Beacon != Entity.Null;
            for (int k = 0; k < n && k < 0xFFFF; k++)
            {
                float u = a + pitch * (k + 0.5f);
                if (!PropLayout.CentreConeStanding(u, v)) continue;
                var ce = PropLayout.Locate(pp.Chain, u, out float _, out float gap);
                if (ce == null || gap > PropLayout.kCoverTolerance) continue;
                float lat = PropLayout.DividerLateral(ce.Record.Section, ce.DirSign < 0f, works, PropLayout.kDividerBarrierInset);
                if (float.IsNaN(lat)) continue;
                bool beacon = beacons && k % PropLayout.kDividerBeaconEvery == 0;
                Apply(p, pp, new Want
                {
                    Key = PropKeys.Make(PropKind.CentreCone, 2, k),
                    Kind = PropKind.CentreCone,
                    U = u,
                    Lateral = lat,
                    LatChain = true,
                    Y = YMode.Curve,
                    Rot = RotMode.Tangent,
                    JitterDeg = 1f,
                    Fill = 255,
                    Prefab = beacon ? m_Beacon : Pick(m_Safety, k),
                    Seed = beacon ? BeaconPhase.Chase(k / PropLayout.kDividerBeaconEvery) : (ushort)0,
                    Group = DerivedGroup.PropStatic,
                });
                pp.DividerCones++;
            }
        }

        // ------------------------------------------------------------------ closed ends

        // WorksHalf: the barrier line across the WORKS half only (from kHalfLineCentreGap beyond the split to the works-side
        // carriageway edge). The line at the closed direction's entry (the end that is NOT OpenEntryAtEnd) gets chase beacons on
        // every other barrier (the light runs from the kerb towards the divider, i.e. towards the open half); the other line one
        // beacon on its inner end with a random phase. A 45-degree cone pair straddles the kerb end of each line (one cone in
        // front of it at the kerb, one behind it towards the divider). Own keys (sub 3), so a style change swaps in one diff.
        private void WorksHalfLine(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan, ChainEdge ce, PropKind kind, Entity node,
                                   float u, YMode y, float keep, bool constr, bool beacons, bool start)
        {
            PropLayout.WorksHalfEdges(ce.Record.Section, ce.DirSign < 0f, plan.WorksHalf, out float split, out float outer, out float sgn);
            outer = PropLayout.OuterBesideParking(ce.Record.Section, ce.DirSign < 0f, sgn, outer, ce.Record.BuildingCount);
            float inner = split + sgn * PropLayout.kHalfLineCentreGap;
            float width = math.abs(outer - inner);
            float pitch = constr ? m_SafetyPitch : m_ConcretePitch;
            int n = math.max(1, (int)math.round(width / pitch));
            int nKeep = (int)math.round(keep * n);
            bool chase = beacons && (start == plan.OpenEntryAtEnd);   // the closed direction enters the works here
            for (int i = 0; i < n && i < nKeep; i++)   // picked up from the outer end first
            {
                int j = n - 1 - i;                     // index from the kerb end
                var w = new Want
                {
                    Key = PropKeys.Make(kind, 3, i),
                    Kind = kind,
                    U = u,
                    Lateral = inner + sgn * pitch * (i + 0.5f),
                    LatChain = true,
                    Y = y,
                    Rot = RotMode.Across,
                    Fill = 255,
                    OwnerNode = node,
                    Group = DerivedGroup.PropStatic,
                    JitterDeg = 2f,
                };
                bool isBeacon = chase ? j % 2 == 0 : beacons && i == 0;
                if (isBeacon)
                {
                    w.Prefab = m_Beacon;
                    // explicit seed: a chase <-> random switch (OpenEntryAtEnd flips at the C4 swap) re-spawns the beacon (Apply)
                    w.Seed = chase ? BeaconPhase.Chase(j / 2) : BeaconPhase.Random(p.Seed, w.Key);
                }
                else w.Prefab = constr ? Pick(m_Safety, i) : Pick(m_Concrete, i);
                Apply(p, pp, w);
            }
            if (nKeep < n || m_TallCone == Entity.Null) return;   // the cone pair goes with the first barrier picked up
            float inward = start ? 1f : -1f;
            float kerb = outer - sgn * PropLayout.kConePairKerb;
            for (int c = 0; c < 2; c++)
            {
                float step = PropLayout.kConePairStep;
                Apply(p, pp, new Want
                {
                    Key = PropKeys.Make(PropKind.ConePair, start ? 0 : 1, c),
                    Kind = PropKind.ConePair,
                    U = c == 0 ? u - inward * step : u + inward * step,
                    Lateral = c == 0 ? kerb : kerb - sgn * 2f * step,
                    LatChain = true,
                    Y = y,
                    Rot = RotMode.Random,
                    Fill = 255,
                    Prefab = m_TallCone,
                    OwnerNode = node,
                    Group = DerivedGroup.PropStatic,
                });
            }
        }

        // ------------------------------------------------------------------ signs and the amber head

        // Chain u of an end's closed-end barrier line (the BarrierLine rule for a visible road).
        private static float LineU(ProjectRecord p, in ProjectView v, bool start)
        {
            bool junction = start ? p.StartIsJunction : p.EndIsJunction;
            float inset = junction ? PropLayout.kJunctionInset : PropLayout.kOtherEndInset;
            return start ? v.Trim0 + inset : v.Trim1 - inset;
        }

        // Open-direction entry (OpenEntryAtEnd ? Trim1 : Trim0): one-way sign + speed plate (+ the amber head beside it) in the
        // works-half corner kSignInsetBehindLine inside the closed-end line and kSignDividerOffset from the split; closed-direction
        // entry: no-entry sign in the middle of the works half. All face the traffic arriving at that end (start: -u, end: +u),
        // + the sign / signal yaw-flip switches (RRWGates.SignYawFlip / SignalYawFlip). Never on an open lane / sidewalk (rrw.check). Picked up with their end's barrier line.
        private void SignsAndHead(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan, bool signsOn, bool headOn)
        {
            pp.Signs = 0;
            for (int end = 0; end < 2; end++)
            {
                bool start = end == 0;
                if (start ? p.StartShared : p.EndShared) continue;
                float keep = start ? plan.StartBarrierKeep : plan.EndBarrierKeep;
                if (keep < 0.999f) continue;
                float uLine = LineU(p, v, start);
                float inward = start ? 1f : -1f;
                float uS = uLine + inward * RRWConst.kSignInsetBehindLine;
                var ce = PropLayout.Locate(pp.Chain, uS, out float _, out float gap);
                if (ce == null || gap > PropLayout.kCoverTolerance) continue;
                var sec = ce.Record.Section;
                bool rev = ce.DirSign < 0f;
                if (float.IsNaN(RoadZoneMath.DirSplitChain(sec, rev))) continue;   // no halves on this edge
                PropLayout.WorksHalfEdges(sec, rev, plan.WorksHalf, out float split, out float outer, out float sgn);
                Entity node = start ? StartNodeOf(pp) : EndNodeOf(pp);
                float face = start ? 180f : 0f;                     // Facing: local +Z along +u; start-end signs face -u
                bool openEntry = start != plan.OpenEntryAtEnd;      // the open half's traffic enters the works here
                if (openEntry)
                {
                    if (signsOn)
                    {
                        WantSign(p, pp, 0, end, uS, split + sgn * RRWConst.kSignDividerOffset, face, Themed(m_SignOneway), node);
                        WantSign(p, pp, 1, end, uS + inward * PropLayout.kSpeedPlateBehind, split + sgn * RRWConst.kSignDividerOffset, face, SpeedPlatePrefab(), node);
                    }
                    if (headOn)
                    {
                        Entity head = Themed(m_SignalHead);
                        if (head != Entity.Null)
                            Apply(p, pp, new Want
                            {
                                Key = PropKeys.Make(PropKind.Signal, end, 0),
                                Kind = PropKind.Signal,
                                U = uS,
                                Lateral = split + sgn * (RRWConst.kSignDividerOffset + PropLayout.kSignalBeside),
                                LatChain = true,
                                Y = YMode.Curve,
                                Rot = RotMode.Facing,
                                YawExtra = face + (RRWGates.SignalYawFlip ? 180f : 0f),
                                JitterDeg = 0.5f,
                                Fill = 255,
                                Prefab = head,
                                Group = DerivedGroup.PropStatic,
                                NoOwner = true,   // never a node sub-object: TrafficLightSystem writes only node SubObjects
                            });
                    }
                }
                else if (signsOn)
                    WantSign(p, pp, 2, end, uS, (split + outer) * 0.5f, face, Themed(m_SignNoEntry), node);
            }
        }

        // One-lane alternating operation (UpgradeShuttle.GreenAt, Traffic's cycle): a portable signal at each end of the section, on
        // the kerb to the right of the traffic entering there, red / green from the cycle. Never a node sub-object (the game's
        // TrafficLightSystem would drive it).
        // The portable signals of a one-lane section (red / green from the cycle), not the flashing amber head.
        private static bool ShuttleHead(int key) => key == PropKeys.Make(PropKind.Signal, 0, 1) || key == PropKeys.Make(PropKind.Signal, 1, 1);

        private void ShuttleHeads(ProjectRecord p, ProjectProps pp, in ProjectView v)
        {
            if (!RRWConst.kUwShuttleHeadsOn || !UpgradeShuttle.GreenAt.TryGetValue(p.Id, out var green)) return;
            Entity head = Themed(m_SignalHead);
            if (head == Entity.Null) return;
            bool lht = RRWCity.LeftHandTraffic;
            for (int end = 0; end < 2; end++)
            {
                bool start = end == 0;
                float uLine = LineU(p, v, start);
                float inward = start ? 1f : -1f;
                float u = uLine + inward * RRWConst.kSignInsetBehindLine;
                var ce = PropLayout.Locate(pp.Chain, u, out float _, out float gap);
                if (ce == null || gap > PropLayout.kCoverTolerance) continue;
                var sec = ce.Record.Section;
                float mag = math.max(sec.CarriageHi, -sec.CarriageLo) + 0.6f;
                // entering at the start drives +u: its right is +lateral (chain frame); at the end -u
                float lat = (start ? 1f : -1f) * (lht ? -1f : 1f) * mag;
                var key = PropKeys.Make(PropKind.Signal, end, 1);
                Apply(p, pp, new Want
                {
                    Key = key, Kind = PropKind.Signal, U = u, Lateral = lat, LatChain = true, Y = YMode.Curve, Rot = RotMode.Facing,
                    YawExtra = (start ? 180f : 0f) + (RRWGates.SignalYawFlip ? 180f : 0f), JitterDeg = 0.5f, Fill = 255, Prefab = head,
                    Group = DerivedGroup.PropStatic,   // owned by the works edge: the light's meshes take their draw layer from the owner
                });
                if (!pp.Slots.TryGetValue(key, out var slot) || slot.Entity == Entity.Null || !EntityManager.Exists(slot.Entity)) continue;
                if (!EntityManager.HasComponent<Game.Objects.TrafficLight>(slot.Entity)) continue;
                Entity node = start ? StartNodeOf(pp) : EndNodeOf(pp);
                if (EntityManager.HasComponent<Game.Objects.Transform>(slot.Entity))
                    RRWLog.Once("props-shuttle-head-p" + p.Id + "-" + end, "props: project #" + p.Id + " one-lane signal " + (start ? "start" : "end") + " at " +
                                RRWLog.F3(EntityManager.GetComponentData<Game.Objects.Transform>(slot.Entity).m_Position) + " u=" + RRWLog.F(u) + " lat=" + RRWLog.F(lat));
                var tl = EntityManager.GetComponentData<Game.Objects.TrafficLight>(slot.Entity);
                var want = green != Entity.Null && green == node ? Game.Objects.TrafficLightState.Green : Game.Objects.TrafficLightState.Red;
                if (tl.m_State != want) { tl.m_State = want; EntityManager.SetComponentData(slot.Entity, tl); }
            }
        }

        private void WantSign(ProjectRecord p, ProjectProps pp, int sub, int end, float u, float lat, float face, Entity prefab, Entity node)
        {
            if (prefab == Entity.Null) return;
            Apply(p, pp, new Want
            {
                Key = PropKeys.Make(PropKind.Sign, sub, end),
                Kind = PropKind.Sign,
                U = u,
                Lateral = lat,
                LatChain = true,
                Y = YMode.Curve,
                Rot = RotMode.Facing,
                YawExtra = face + (RRWGates.SignYawFlip ? 180f : 0f),
                JitterDeg = 1f,
                Fill = 255,
                Prefab = prefab,
                OwnerNode = node,
                Group = DerivedGroup.PropStatic,
            });
            pp.Signs++;
        }

        // EU_/NA_Speedlimit<plate> for the work-zone speed (PrefabNames.SpeedPlate); NA falls back to EU when missing.
        private Entity SpeedPlatePrefab()
        {
            Entity e = PrefabCatalog.Static(m_PrefabSystem, PrefabNames.SpeedPlate(m_SpeedKmh, m_Na));
            if (!Info(e).Valid && m_Na) e = PrefabCatalog.Static(m_PrefabSystem, PrefabNames.SpeedPlate(m_SpeedKmh, false));
            return Info(e).Valid ? e : Entity.Null;
        }

        // The amber head shows Yellow | Flashing (2 | 8), written at spawn and re-asserted on every diff that wants the slot
        // (every self-heal pass at the latest). Nothing vanilla writes it (unowned, never in a node's SubObject buffer).
        private void AssertSignal(ProjectProps pp, Entity e)
        {
            var em = EntityManager;
            if (!em.HasComponent<Game.Objects.TrafficLight>(e)) return;
            var want = Game.Objects.TrafficLightState.Yellow | Game.Objects.TrafficLightState.Flashing;
            var tl = em.GetComponentData<Game.Objects.TrafficLight>(e);
            if (tl.m_State == want) return;
            if (pp.SignalWrites > 0)
            {
                pp.SignalAsserts++;
                RRWLog.Once("props-signal-assert-" + pp.Id, "props: project #" + pp.Id + " amber head state was " + (int)tl.m_State + ", re-asserted to Yellow|Flashing (logged once)");
            }
            tl.m_State = want;
            em.SetComponentData(e, tl);
            pp.SignalWrites++;
        }

        // ------------------------------------------------------------------ rrw.check

        // Invariants per standing slot (all must be 0):
        //  (1) a kerb fence panel on a side whose sidewalk is not open (ProjectRecord.OpenLanes);
        //  (2) an in-lane device (divider, closed-end line across the works half, cone pair, sign, amber head) while the works
        //      car half is not in ProjectRecord.WorkZonesReady;
        //  (3) a sign / amber head whose footprint touches an open lane group or an open sidewalk.
        private void CheckDevice(EntityManager em, ProjectProps pp, ProjectRecord p, PropSlot sl, List<string> problems,
                                 ref int fence, ref int notReady, ref int onOpen, ref int checkedN)
        {
            var kind = PropKeys.Kind(sl.Key);
            int sub = PropKeys.Sub(sl.Key);
            string where = "props #" + pp.Id + " " + PropKeys.Name(sl.Key);
            if (kind == PropKind.Fence && sub >= 4)
            {
                checkedN++;
                RoadZones bit = sub == 4 ? RoadZones.SidewalkLeft : RoadZones.SidewalkRight;
                if ((p.OpenLanes & bit) == 0)
                {
                    fence++;
                    if (fence <= 3) problems.Add(where + " kerb fence on the " + (sub == 4 ? "left" : "right") + " side without an open sidewalk (openLanes=" + RoadZoneMath.Describe(p.OpenLanes) + ")");
                }
            }
            bool inLane = (kind == PropKind.CentreCone && sub >= 1) || kind == PropKind.Sign || kind == PropKind.Signal || kind == PropKind.ConePair
                          || ((kind == PropKind.BarrierStart || kind == PropKind.BarrierEnd) && sub == 3);
            if (inLane)
            {
                checkedN++;
                RoadZones works = pp.LastWorksHalf;
                if (works == RoadZones.None || (works & ~p.WorkZonesReady) != 0)
                {
                    notReady++;
                    if (notReady <= 3) problems.Add(where + " in-lane device while the works half " + RoadZoneMath.Describe(works) + " is not ready (workZonesReady=" +
                                                    RoadZoneMath.Describe(p.WorkZonesReady) + ", switch=" + p.Switch + ")");
                }
            }
            if ((kind == PropKind.Sign || kind == PropKind.Signal) && em.HasComponent<ObjTransform>(sl.Entity))
            {
                checkedN++;
                ChainEdge ce = null;
                for (int i = 0; i < pp.Chain.Count; i++) if (pp.Chain[i].Edge == sl.SiteEdge) { ce = pp.Chain[i]; break; }
                if (ce == null || ce.Record.Arc == null) return;
                var arc = ce.Record.Arc;
                float3 pos = em.GetComponentData<ObjTransform>(sl.Entity).m_Position;
                float s = arc.Project(pos);
                float lat = math.dot((pos - arc.Position(s)).xz, arc.Right(s).xz) * ce.DirSign;
                float half = math.max(0.1f, Info(sl.Prefab).Size.x * 0.5f);   // Facing: the plate / head width runs across the road
                RoadZones z = RoadZoneMath.OfLateral(lat - half, lat + half, ce.Record.Section, ce.DirSign < 0f);
                RoadZones hit = z & p.OpenLanes & RoadZones.AllLanes;
                if (hit != RoadZones.None)
                {
                    onOpen++;
                    if (onOpen <= 3) problems.Add(where + " stands on an open group " + RoadZoneMath.Describe(hit) + " (lateral " + RRWLog.F(lat) + " +- " + RRWLog.F(half) +
                                                  ", split " + RRWLog.F(RoadZoneMath.DirSplitChain(ce.Record.Section, ce.DirSign < 0f)) + ")");
                }
            }
        }

        // Curve Y under a world point (nearest chain edge): the road surface a panel foot stands on.
        private static bool CurveYAt(ProjectProps pp, float3 p, out float y)
        {
            y = 0f;
            float best = float.MaxValue;
            bool ok = false;
            for (int i = 0; i < pp.Chain.Count; i++)
            {
                var arc = pp.Chain[i].Record.Arc;
                if (arc == null) continue;
                float s = arc.Project(p);
                float3 c = arc.Position(s);
                float d = math.distance(c.xz, p.xz);
                if (d < best) { best = d; y = c.y; ok = true; }
            }
            return ok;
        }

        // ------------------------------------------------------------------ device summary (log + dev)

        private void DeviceSummary(ProjectRecord p, ProjectProps pp, in PropPlan plan, RoadZones works)
        {
            uint sig = Mix(Mix((uint)plan.FenceSides, (uint)plan.Barriers | ((uint)plan.Divider << 4) | ((uint)works << 8)),
                           (plan.Signs ? 1u : 0u) | (plan.InLaneReady ? 2u : 0u) | (plan.OpenEntryAtEnd ? 4u : 0u) | (plan.ApproachSignals && m_ApproachOn ? 8u : 0u)
                           | (pp.LampSite ? 16u : 0u) | (pp.FenceFallback ? 32u : 0u));
            sig = Mix(sig, Mix((uint)pp.FencePanels, (uint)pp.FenceGaps));
            sig = Mix(sig, Mix(pp.DividerCones > 0 ? 1u : 0u, (uint)pp.Signs));   // cone count changes during the pick-up: not logged
            sig = Mix(sig, (uint)RRWGates.Revision);
            bool changed = sig != pp.DeviceSig;
            if (!changed && pp.DeviceLine.Length > 0 && (m_Update & 63u) != 0u) return;   // counts refresh every 64 updates (dev text only)
            pp.DeviceLine = "fence=" + RoadZoneMath.Describe(plan.FenceSides) + "(" + pp.FencePanels + " panels, " + pp.FenceShorts + " short, " + pp.FenceGaps + " gaps, " +
                            pp.FenceCones + " cones" + (pp.FenceFallback ? ", BARRIER FALLBACK" : "") + ", " + RRWGates.FenceLateral + ")" +
                            " barriers=" + plan.Barriers + " worksHalf=" + RoadZoneMath.Describe(works) + " chaseAt=" + (plan.Barriers == BarrierStyle.WorksHalf ? (plan.OpenEntryAtEnd ? "start" : "end") : "-") +
                            " divider=" + plan.Divider + "/" + RRWGates.Divider + "(" + pp.DividerCones + (pp.DividerLamps > 0 ? ", " + pp.DividerLamps + " lamps" : "") + ")" +
                            " signs=" + (plan.Signs && RRWGates.Signs ? pp.Signs.ToString() : "off") + " amber=" + (plan.ApproachSignals && m_ApproachOn ? "on" : "off") +
                            " inLaneReady=" + plan.InLaneReady + " openEntry=" + (plan.OpenEntryAtEnd ? "end" : "start") + " theme=" + (m_Na ? "NA" : "EU") +
                            " access=" + pp.Access.Count;
            if (!changed) return;
            pp.DeviceSig = sig;
            RRWLog.Info("props: project #" + p.Id + " devices " + pp.DeviceLine);
        }
    }
}
