using Unity.Entities;
using Unity.Mathematics;

// SurfaceAreaSystem, fifth part: upgrade works (a road replaced by another variant while it stays in use).
//
// The road already has its new layout; only the bands that change are dressed. Each band of an edge is cut into sub-strips at
// the new lane and sidewalk boundaries (UpgradeEdgeState.SubStrips, EDGE frame), and every sub-strip is one polygon per layer:
//   * row slot = band index * EdgeSurf.kSubStripsPerBand + sub-strip index, so a sub-strip keeps its row while other bands
//     start and finish;
//   * the cover of a sub-strip (PhasePlan.CoverOf) says what it may show: a lane that carries cars gets Fresh Asphalt Cover
//     only, a closed lane dirt, gravel and asphalt behind the crew, an open sidewalk gravel until it is paved, the removed
//     strip outside the narrowed road gravel (or worn asphalt), subgrade and topsoil;
//   * spans along the road come from PhasePlan.UpgradeSpans per band and cover, mapped to the edge like every other layer;
//   * the re-marking band covers only what the other bands left or finished (PhasePlan.UpgradeOwnLaterals). Under the
//     one-direction-at-a-time primitive its asphalt layers use the per-half rows of a new road (CarriageHalf, slots 0 / 1),
//     clipped to the re-marking laterals of the edge.
// Nothing is drawn until the edge's chain index and sub-strips belong to the current runtime, tail and geometry: until then
// the areas on screen stay as they are. Hand-overs pair every layer of every sub-strip that overlaps the shrinking one sideways
// (a layer only shrinks where whatever else wants to be there has been on screen for an update).
namespace RealisticRoadWorks.V3.Surfaces
{
    public partial class SurfaceAreaSystem
    {
        const float kMinSubStrip = 0.2f;       // narrower sub-strips (numeric slivers at band edges) are not drawn
        const float kPairOverlap = 0.1f;       // sub-strips overlapping sideways by more than this hand over to each other
        const float kHalfClipSlack = 0.3f;     // the re-marking half is clipped only where its laterals stop short of the carriageway

        // Sub-strip slots of the edge being processed (DesiredPiecesUpgrade fills them, UpgradeLat reads them)
        private readonly bool[] m_SlotOn = new bool[EdgeSurf.kMaxSlots];
        private readonly float[] m_SlotLo = new float[EdgeSurf.kMaxSlots];
        private readonly float[] m_SlotHi = new float[EdgeSurf.kMaxSlots];
        private bool m_RemarkOn;               // the edge has re-marking laterals of its own now (EDGE-frame hull below)
        private float m_RemarkLo, m_RemarkHi;

        // Chain spans per (layer, chain band, cover) of the project being processed: the same for every edge and sub-strip, so
        // PhasePlan.UpgradeSpans runs once per combination and update
        const int kCovers = 6;
        private readonly SpanSet[] m_UwSpans = new SpanSet[(int)SurfaceLayer.Count * RRWConst.kUwMaxChainBands * kCovers];
        private readonly bool[] m_UwSpansHave = new bool[(int)SurfaceLayer.Count * RRWConst.kUwMaxChainBands * kCovers];

        // Per project, this update (log line)
        private int m_UwEdges, m_UwSlots, m_UwPolys, m_UwWaits;
        private readonly int[] m_UwCover = new int[kCovers];
        private string m_UwWaitWhy;

        // ------------------------------------------------------------------ context

        // Is this edge drawn as upgrade works, and is its band data current? The saved site decides (an upgrade plan on a
        // HalfWidth site); while the project's runtime, view or the edge's band state are missing or stale the edge waits with
        // what is on screen, so a registry rebuild never makes the dressing blink.
        private void UpgradeContext(EdgeCtx c)
        {
            c.Upgrade = false;
            c.UpgradeReady = false;
            c.Ue = null;
            if (c.Site.Mode != VisualMode.HalfWidth || !c.Site.IsUpgrade) return;
            c.Upgrade = true;
            c.Ue = c.Rec.Upgrade;
            c.Es.EnsureSlots(EdgeSurf.kMaxSlots);
            var rt = c.Project.Upgrade;
            c.UpgradeReady = c.View.IsUpgrade && rt != null && c.Ue != null && c.Ue.ChainIndexCurrent(rt)
                             && c.Ue.SubStripsRevision == c.Rec.GeometryRevision && c.Ue.SubStripsTail == c.Ue.TailRevision;
        }

        // The edge is no longer drawn as upgrade works (the works became a full rebuild, the plan was dropped): the sub-strip
        // rows beyond the slots every edge has go (a few updates later, so whatever replaces them is on screen first). Rows
        // 0..kMaxIntervals-1 are handed over by the normal row diff (band-kind switch).
        private void DropUpgradeRows(EdgeSurf es)
        {
            int dropped = 0;
            for (int l = 0; l < es.Areas.GetLength(0); l++)
                for (int b = EdgeSection.kMaxIntervals; b < es.Slots; b++)
                {
                    for (int k = 0; k < es.RowN[l, b]; k++)
                    {
                        var t = es.Areas[l, b, k];
                        if (t == null) continue;
                        if (!t.Pending && t.Live(m_Em)) Retire(t.Area, null, 3);
                        else DeleteArea(t);
                        es.Areas[l, b, k] = null;
                        dropped++;
                    }
                    es.RowN[l, b] = 0;
                }
            if (dropped > 0) RRWLog.Verbose("surfaces: edge e" + es.Edge.Index + " no longer has upgrade works: " + dropped + " sub-strip area(s) retired");
        }

        private void BeginUpgradeFrame()
        {
            m_UwEdges = m_UwSlots = m_UwPolys = m_UwWaits = 0;
            for (int i = 0; i < m_UwSpansHave.Length; i++) m_UwSpansHave[i] = false;
            for (int i = 0; i < m_UwCover.Length; i++) m_UwCover[i] = 0;
            m_UwWaitWhy = null;
        }

        // The edge waits for current band data: its areas keep the last state (logged once per wait and project).
        private void UpgradeWait(EdgeCtx c)
        {
            SurfaceState.UpgradeWaits++;
            m_UwWaits++;
            if (m_UwWaitWhy != null) return;
            var ue = c.Ue;
            if (!c.View.IsUpgrade || c.Project.Upgrade == null) m_UwWaitWhy = "edge e" + c.Edge.Index + " project has no upgrade runtime / view";
            else if (ue == null) m_UwWaitWhy = "edge e" + c.Edge.Index + " has no upgrade edge state";
            else m_UwWaitWhy = "edge e" + c.Edge.Index + " chainIndex=" + (ue.ChainIndexCurrent(c.Project.Upgrade) ? "current" : "stale")
                               + " subStrips rev=" + ue.SubStripsRevision + "/" + c.Rec.GeometryRevision + " tail=" + ue.SubStripsTail + "/" + ue.TailRevision;
        }

        // ------------------------------------------------------------------ desired pieces

        // m_DCount / m_DS0 / m_DS1 for every (layer, slot) of an upgrade works edge, plus the slot laterals.
        private void DesiredPiecesUpgrade(EdgeCtx c)
        {
            var es = c.Es;
            var ue = c.Ue;
            var v = c.View;
            var u = v.Upgrade;
            int slots = es.Slots;
            for (int l = 0; l < (int)SurfaceLayer.Count; l++)
                for (int b = 0; b < slots; b++) m_DCount[l, b] = 0;
            for (int s = 0; s < EdgeSurf.kMaxSlots; s++) m_SlotOn[s] = false;
            m_RemarkOn = false;
            m_RemarkLo = float.MaxValue;
            m_RemarkHi = float.MinValue;
            m_UwEdges++;

            for (int i = 0; i < ue.BandCount; i++)
            {
                int ci = ue.ChainIndex[i];
                if (ci < 0 || ci >= u.BandCount) continue;
                var bv = u.Band(ci);
                bool remark = bv.Kind == BandKind.Remark;
                var own = remark ? PhasePlan.UpgradeOwnLaterals(v, ci) : default;
                var strips = ue.SubStrips[i];
                int n = strips.Count;
                if (n > EdgeSurf.kSubStripsPerBand)
                {
                    RRWLog.Once("surf-uw-substrips", "surfaces: an upgrade band has " + n + " sub-strips; only the first "
                                + EdgeSurf.kSubStripsPerBand + " are dressed (edge e" + c.Edge.Index + ", project #" + c.Project.Id + ")");
                    n = EdgeSurf.kSubStripsPerBand;
                }
                for (int k = 0; k < n; k++)
                {
                    var s = strips[k];
                    float lo = s.Lo, hi = s.Hi;
                    if (remark && !OwnPart(own, c.Reversed, ref lo, ref hi)) continue;
                    if (hi - lo < kMinSubStrip) continue;
                    int slot = i * EdgeSurf.kSubStripsPerBand + k;
                    var cover = CoverNow(c.Rec, u, s, i, bv.Kind);
                    m_SlotOn[slot] = true;
                    m_SlotLo[slot] = lo;
                    m_SlotHi[slot] = hi;
                    m_UwSlots++;
                    m_UwCover[math.clamp((int)cover, 0, m_UwCover.Length - 1)]++;
                    if (remark)
                    {
                        m_RemarkOn = true;
                        m_RemarkLo = math.min(m_RemarkLo, lo);
                        m_RemarkHi = math.max(m_RemarkHi, hi);
                    }
                    for (int l = 0; l < (int)SurfaceLayer.Count; l++)
                    {
                        if (m_BandKind[l] != SurfaceBand.WorksBand || !UpgradeLayerOn((SurfaceLayer)l)) continue;
                        FillPieces(c, l, slot, in ChainSpans(v, l, ci, cover));
                    }
                }
            }
            // the re-marking under the one-direction primitive: the per-half rows of a new road, on edges that re-mark
            for (int l = 0; l < (int)SurfaceLayer.Count; l++)
            {
                if (m_BandKind[l] != SurfaceBand.CarriageHalf || !m_RemarkOn || !UpgradeLayerOn((SurfaceLayer)l)) continue;
                for (int b = 0; b < 2; b++) FillPieces(c, l, b, in m_HalfSet[l, b]);
            }
        }

        private ref SpanSet ChainSpans(in ProjectView v, int l, int chainBand, SubStripCover cover)
        {
            int key = (l * RRWConst.kUwMaxChainBands + math.clamp(chainBand, 0, RRWConst.kUwMaxChainBands - 1)) * kCovers
                      + math.clamp((int)cover, 0, kCovers - 1);
            if (!m_UwSpansHave[key])
            {
                PhasePlan.UpgradeSpans((SurfaceLayer)l, v, chainBand, cover, out m_UwSpans[key]);
                m_UwSpansHave[key] = true;
            }
            return ref m_UwSpans[key];
        }

        private bool UpgradeLayerOn(SurfaceLayer layer) =>
            layer != SurfaceLayer.TempMarking && (layer != SurfaceLayer.TopsoilStrip || m_TopsoilOn) && SurfaceState.Prefab(layer, 100) != Entity.Null;

        // Chain pieces of one (layer, slot) clipped to the edge, merged and kept where something is left to draw (the same rule
        // as the crew-section pieces of a new road).
        private void FillPieces(EdgeCtx c, int l, int slot, in SpanSet chain)
        {
            m_EdgeSet.Clear();
            AddEdgeLocal(c, in chain);
            int cnt = 0;
            for (int i = 0; i < m_EdgeSet.Count; i++)
            {
                var sp = m_EdgeSet[i];
                if (cnt > 0 && sp.A - m_TmpB[cnt - 1] <= kPieceMergeGap) { m_TmpB[cnt - 1] = math.max(m_TmpB[cnt - 1], sp.B); continue; }
                m_TmpA[cnt] = sp.A;
                m_TmpB[cnt] = sp.B;
                cnt++;
            }
            int n = 0;
            for (int i = 0; i < cnt; i++)
            {
                if (!SurfaceGeom.GeometricRange((SurfaceLayer)l, m_TmpA[i], m_TmpB[i], c.L, c.Es.Ends, out _, out _, out _, out _, out _, out _, out _, out _))
                    continue;
                m_DS0[l, slot, n] = m_TmpA[i];
                m_DS1[l, slot, n] = m_TmpB[i];
                n++;
            }
            m_DCount[l, slot] = n;
            m_UwPolys += n;
            if (n > c.Ps.MaxPieces) c.Ps.MaxPieces = n;
        }

        // The widest part of [lo, hi] (EDGE frame) inside the re-marking band's own laterals (CHAIN frame).
        private static bool OwnPart(in LatPieces own, bool reversed, ref float lo, ref float hi)
        {
            float bestLo = 0f, bestHi = 0f, best = 0.05f;
            for (int j = 0; j < own.Count; j++)
            {
                var p = own[j];
                float a = reversed ? -p.y : p.x, b = reversed ? -p.x : p.y;
                float x0 = math.max(lo, a), x1 = math.min(hi, b);
                if (x1 - x0 > best) { best = x1 - x0; bestLo = x0; bestHi = x1; }
            }
            if (best <= 0.05f) return false;
            lo = bestLo;
            hi = bestHi;
            return true;
        }

        // ------------------------------------------------------------------ laterals and hand-overs

        // Laterals (EDGE frame) of row `slot` of layer `l` drawn as `kind`: the sub-strip (WorksBand), or the re-marking half
        // clipped to the edge's re-marking laterals (CarriageHalf). False when that row has nothing to draw now.
        private bool UpgradeLat(EdgeCtx c, int l, int slot, SurfaceBand kind, out float left, out float right)
        {
            left = right = 0f;
            if (kind == SurfaceBand.CarriageHalf)
            {
                if (!m_RemarkOn || slot < 0 || slot > 1) return false;
                c.Rec.Section.Band(kind, (SurfaceLayer)l, slot, c.Reversed, RoadZones.None, false, out left, out right);
                float m = RRWConst.kAsphaltMargin;
                if (m_RemarkLo > left + m + kHalfClipSlack) left = m_RemarkLo;
                if (m_RemarkHi < right - m - kHalfClipSlack) right = m_RemarkHi;
                return right - left > kMinSubStrip;
            }
            if (slot < 0 || slot >= EdgeSurf.kMaxSlots || !m_SlotOn[slot]) return false;
            left = m_SlotLo[slot];
            right = m_SlotHi[slot];
            return true;
        }

        // The sub-strip of a tracked piece moved sideways since its last write (layout read after a cut from the section, a tail
        // change, a re-marking band that grew over a finished band).
        private bool UpgradeLatMoved(EdgeCtx c, TrackedArea t)
        {
            if (!UpgradeLat(c, (int)t.Layer, t.Band, t.BandKind, out float l, out float r)) return false;
            if (float.IsNaN(t.LatLo) || float.IsNaN(t.LatHi)) return true;
            return math.abs(l - t.LatLo) > kTol || math.abs(r - t.LatHi) > kTol;
        }

        // Hand-over pairs of a piece of (layer l, slot) with laterals [latLo, latHi]: the row's other pieces, then every layer of
        // every row that overlaps it sideways (the build band's covers hand over to the re-marking covers when the band finishes,
        // dirt to gravel to asphalt within one sub-strip, a pre-cover to the dirt behind the dig).
        private void UpgradePairs(EdgeCtx c, int l, int slot, int exclude, float latLo, float latHi)
        {
            var kind = m_BandKind[l];
            AddPair(c, l, slot, kind, exclude);
            float lo = latLo, hi = latHi;
            if ((float.IsNaN(lo) || float.IsNaN(hi)) && !UpgradeLat(c, l, slot, kind, out lo, out hi)) return;
            int slots = c.Es.Slots;
            for (int l2 = 0; l2 < (int)SurfaceLayer.Count; l2++)
            {
                if (l2 == (int)SurfaceLayer.TempMarking) continue;
                var k2 = m_BandKind[l2];
                for (int b2 = 0; b2 < slots; b2++)
                {
                    if ((l2 == l && b2 == slot) || m_DCount[l2, b2] == 0) continue;
                    if (!UpgradeLat(c, l2, b2, k2, out float lo2, out float hi2)) continue;
                    if (math.min(hi, hi2) - math.max(lo, lo2) <= kPairOverlap) continue;
                    AddPair(c, l2, b2, k2, -1);
                }
            }
        }

        // ------------------------------------------------------------------ log

        // One Info line per project when its window, primitive or sub-strip covers change (for in-game verification).
        private void LogUpgrade(ProjectRecord p, ProjSurf ps, in ProjectView v)
        {
            SurfaceState.UpgradePolygons = m_UwPolys;
            if (m_UwWaits > 0)
            {
                if (!ps.UpgradeWaiting)
                    RRWLog.Info("surfaces: project #" + p.Id + " upgrade works waiting for current band data (" + m_UwWaitWhy
                                + "); its work-site textures keep their last state");
                ps.UpgradeWaiting = true;
                return;
            }
            if (ps.UpgradeWaiting) RRWLog.Info("surfaces: project #" + p.Id + " upgrade works band data current again");
            ps.UpgradeWaiting = false;
            if (!v.IsUpgrade) return;
            var u = v.Upgrade;
            uint sig = math.hash(new int4(u.Window, (int)u.Traffic, m_UwSlots, m_UwEdges))
                       ^ math.hash(new int4(m_UwCover[0], m_UwCover[1], m_UwCover[2], m_UwCover[3]))
                       ^ math.hash(new int4(m_UwCover[4], m_UwCover[5], m_RemarkOn ? 1 : 0, u.PreCover ? 1 : 0)) * 31u
                       ^ math.hash(new int2(u.AppliedWindow, (int)u.AppliedTraffic)) * 17u;
            if (sig == ps.UpgradeSig) return;
            ps.UpgradeSig = sig;
            string line = "project #" + p.Id + " upgrade " + (u.InSetup ? "setup" : u.InTeardown ? "teardown" : "window " + u.Window + "/" + u.WindowCount)
                          + " " + u.Traffic + (u.Vacating ? " (closure still of window " + u.AppliedWindow + ", " + u.AppliedTraffic + ")" : "")
                          + ": " + m_UwEdges + " edge(s), " + m_UwSlots + " sub-strip(s) (works " + m_UwCover[(int)SubStripCover.Works]
                          + ", traffic " + m_UwCover[(int)SubStripCover.Traffic] + ", open walk " + m_UwCover[(int)SubStripCover.OpenWalk]
                          + ", works walk " + m_UwCover[(int)SubStripCover.WorksWalk] + ", terrain " + m_UwCover[(int)SubStripCover.Terrain]
                          + ", inside " + m_UwCover[(int)SubStripCover.Inside] + "), " + m_UwPolys + " polygon(s)"
                          + " preCover=" + (u.PreCover ? "on" : "off") + " oldAsphalt=" + (RRWGates.UpgradeOldAsphalt ? "on" : "off")
                          + " halves=" + (m_BandKind[(int)SurfaceLayer.FreshAsphaltCover] == SurfaceBand.CarriageHalf ? "on" : "off");
            SurfaceState.LastUpgrade = "u" + m_Now + " " + line;
            RRWLog.Info("surfaces: " + line);
        }

        // ------------------------------------------------------------------ shared rules (checks, dev)

        // Slot of a sub-strip / band and sub-strip of a slot.
        internal static int SlotOf(int band, int subStrip) => band * EdgeSurf.kSubStripsPerBand + subStrip;
        internal static void SlotParts(int slot, out int band, out int subStrip)
        {
            band = slot / EdgeSurf.kSubStripsPerBand;
            subStrip = slot % EdgeSurf.kSubStripsPerBand;
        }

        // Cover of sub-strip `s` of saved band `band` (kind `kind`) now: PhasePlan.CoverOf with the shared closed rule
        // (UpgradeZones.SubStripClosed: the lane group closed in the window whose closure is applied and applied on this edge, or a
        // lane dropped with this band, blockers registered) and the switched-off parking list of the edge's current geometry.
        internal static SubStripCover CoverNow(EdgeRecord rec, in UpgradeView u, in SubStrip s, int band, BandKind kind) =>
            PhasePlan.CoverOf(s, kind, UpgradeZones.SubStripClosed(rec, u, s, band), UpgradeZones.SubStripParkingOff(rec, s));

        // Does a lane-type sub-strip carry cars now (for the checks)? Not closed, and not a new parking lane switched off.
        internal static bool CarriesCars(EdgeRecord rec, in ProjectView v, in SubStrip s, int band)
        {
            if (s.Kind != SubKind.DriveLane && s.Kind != SubKind.Bike && s.Kind != SubKind.NewParking) return false;
            return !UpgradeZones.SubStripIdle(rec, v.Upgrade, s, band);
        }

        // Layers that are dirt or gravel (never on a sub-strip that carries cars).
        internal static bool IsDirtOrGravel(SurfaceLayer l) =>
            l == SurfaceLayer.RoadDirt || l == SurfaceLayer.Subgrade || l == SurfaceLayer.TopsoilStrip || l == SurfaceLayer.BaseCourseCover;
    }
}
