using Unity.Entities;
using Unity.Mathematics;

// SurfaceAreaSystem, fourth part: per-crew-section PIECES of a layer on one edge.
//
// Desired pieces: the chain pieces of the layer (PhasePlan.SurfaceSpans / SurfaceSpansHalf), each clipped to the edge with
// ToEdgeLocal (never-drop widening), merged where they touch or overlap afterwards (a translucent layer such as the 0.35 alpha
// topsoil must never double), and kept only where something is left to draw (SurfaceGeom.GeometricRange). Each piece is one
// area, keyed (edge, layer, band, piece index); a row (layer, band) is compact and sorted by s.
//
// Row diff:
//   * same piece count and band kind -> piece i follows desired piece i like a single strip (write tick, global cap,
//     bypass, re-snap, replacement) with the hand-over clamp: a shrinking side never uncovers what the covering layer or the
//     row's other pieces should cover there but have not shown for >= 1 update (hand-over rule);
//   * piece count or band kind changed (a front enters / leaves the edge, pieces merge, C3 -> C4 halves) -> TOPOLOGY change:
//     the whole row is rewritten in this update (write tick or bypass; never held by the global cap): a desired piece that
//     only grows over an old piece rewrites it in place, every other desired piece spawns new, and every old area left over
//     moves to EdgeSurf.Outgoing. An outgoing area stays on screen until the row's current pieces and the covering layer have
//     covered everything it showed (each needed point settled >= 1 update), the split hold allows it, or kDeferTimeout.
namespace RealisticRoadWorks.V3.Surfaces
{
    public partial class SurfaceAreaSystem
    {
        const float kTol = 0.05f;
        const float kPieceMergeGap = 0.05f;    // edge-local pieces closer than this are one piece (numeric seams)
        const int kMaxPairs = 32;              // upgrade works pair every overlapping sub-strip layer (2 * kMaxIntervals otherwise)

        private readonly TrackedArea[] m_Row = new TrackedArea[EdgeSurf.kMaxPieces];
        private readonly TrackedArea[] m_NewRow = new TrackedArea[EdgeSurf.kMaxPieces];
        private readonly float[] m_TmpA = new float[SpanSet.Capacity];
        private readonly float[] m_TmpB = new float[SpanSet.Capacity];
        private SpanSet m_EdgeSet;
        // hand-over check: (need, have) pairs - every point of a checked range that lies in need[p] must lie in have[p]
        private readonly SpanSet[] m_Need = new SpanSet[kMaxPairs];
        private readonly SpanSet[] m_Have = new SpanSet[kMaxPairs];
        private int m_Pairs;

        // ------------------------------------------------------------------ desired pieces

        // m_DCount / m_DS0 / m_DS1 for every (layer, band slot) of the edge in c.
        private void DesiredPieces(EdgeCtx c)
        {
            if (c.Upgrade) { DesiredPiecesUpgrade(c); return; }
            var es = c.Es;
            for (int l = 0; l < (int)SurfaceLayer.Count; l++)
            {
                var layer = (SurfaceLayer)l;
                bool en = c.ModeA && layer != SurfaceLayer.TempMarking && (layer != SurfaceLayer.TopsoilStrip || m_TopsoilOn)
                          && SurfaceState.Prefab(layer, 100) != Entity.Null;
                var bandKind = m_BandKind[l];
                bool half = bandKind == SurfaceBand.CarriageHalf;
                int bands = en ? math.min(c.Rec.Section.BandCount(bandKind, c.Reversed, RoadZones.None, false), EdgeSection.kMaxIntervals) : 0;
                for (int b = 0; b < EdgeSection.kMaxIntervals; b++)
                {
                    int n = 0;
                    if (b < bands && (!half || b < 2))
                    {
                        // A C4 half has its own pieces (SurfaceSpansHalf); every other band kind shares the layer's pieces
                        m_EdgeSet.Clear();
                        if (half) AddEdgeLocal(c, in m_HalfSet[l, b]);
                        else AddEdgeLocal(c, in m_ProjSet[l]);
                        int cnt = 0;
                        for (int i = 0; i < m_EdgeSet.Count; i++)
                        {
                            var sp = m_EdgeSet[i];
                            if (cnt > 0 && sp.A - m_TmpB[cnt - 1] <= kPieceMergeGap) { m_TmpB[cnt - 1] = math.max(m_TmpB[cnt - 1], sp.B); continue; }
                            m_TmpA[cnt] = sp.A;
                            m_TmpB[cnt] = sp.B;
                            cnt++;
                        }
                        for (int i = 0; i < cnt; i++)
                        {
                            if (!SurfaceGeom.GeometricRange(layer, m_TmpA[i], m_TmpB[i], c.L, es.Ends, out _, out _, out _, out _, out _, out _, out _, out _))
                                continue;
                            m_DS0[l, b, n] = m_TmpA[i];
                            m_DS1[l, b, n] = m_TmpB[i];
                            n++;
                        }
                    }
                    m_DCount[l, b] = n;
                    if (n > c.Ps.MaxPieces) c.Ps.MaxPieces = n;
                }
            }
        }

        // Clips every chain piece to the edge (ToEdgeLocal) into m_EdgeSet (merges overlapping / touching pieces).
        private void AddEdgeLocal(EdgeCtx c, in SpanSet chain)
        {
            for (int i = 0; i < chain.Count; i++)
                if (PhasePlan.ToEdgeLocal(chain[i], c.Site.m_ChainU0, c.Site.m_ChainU1, c.L, out float s0, out float s1))
                    m_EdgeSet.Add(new Span(s0, s1));
        }

        // ------------------------------------------------------------------ row diff

        private void DiffRow(EdgeCtx c, SurfaceLayer layer, int band, SurfaceBand kind, bool rebuild, bool bypass, bool tickOpen,
                             ref bool wrote, ref bool deferred)
        {
            int l = (int)layer;
            int m = m_DCount[l, band];
            if (m == 0 && c.Es.RowN[l, band] == 0) return;
            int k = CompactRow(c, l, band, rebuild);
            bool kindSwitch = false;
            for (int i = 0; i < k; i++) if (m_Row[i].BandKind != kind) { kindSwitch = true; break; }
            // same count but a piece no longer overlaps "its" desired piece (one left the edge while another entered): a
            // topology change too, so pieces keep their identity (a section's piece never slides into another section's)
            bool disjoint = false;
            if (!kindSwitch && k == m)
                for (int i = 0; i < m && !disjoint; i++)
                    if (math.min(m_DS1[l, band, i], m_Row[i].LS1) - math.max(m_DS0[l, band, i], m_Row[i].LS0) <= 0f) disjoint = true;
            if (!kindSwitch && k == m && !disjoint)
            {
                for (int i = 0; i < m; i++)
                    DiffPiece(c, layer, band, kind, i, rebuild, bypass, tickOpen, ref wrote, ref deferred);
                return;
            }
            Topology(c, layer, band, kind, k, kindSwitch, rebuild, bypass, tickOpen, ref wrote, ref deferred);
        }

        // Drops dead / stale tracking, sorts the row by LS0 and stores it compact (also in m_Row). Returns the piece count.
        private int CompactRow(EdgeCtx c, int l, int band, bool rebuild)
        {
            var es = c.Es;
            int k = 0, rowN = es.RowN[l, band];
            for (int s = 0; s < rowN; s++)
            {
                var t = es.Areas[l, band, s];
                if (t == null) continue;
                es.Areas[l, band, s] = null;
                if (t.Pending && m_Now - t.DefUpdate > kPendingTimeout) { DeleteArea(t); continue; }
                if (!t.Pending && (c.Heal || rebuild) && !t.Live(m_Em)) continue;   // vanished: the row diff respawns it
                int j = k++;
                while (j > 0 && m_Row[j - 1].LS0 > t.LS0) { m_Row[j] = m_Row[j - 1]; j--; }
                m_Row[j] = t;
            }
            for (int i = 0; i < k; i++) { es.Areas[l, band, i] = m_Row[i]; m_Row[i].Piece = i; }
            es.RowN[l, band] = (byte)k;
            return k;
        }

        // Same topology: piece i follows desired piece i (the single-strip diff).
        private void DiffPiece(EdgeCtx c, SurfaceLayer layer, int band, SurfaceBand kind, int i, bool rebuild, bool bypass, bool tickOpen,
                               ref bool wrote, ref bool deferred)
        {
            var es = c.Es;
            int l = (int)layer;
            var t = es.Areas[l, band, i];
            if (t == null || t.Pending) return;   // created this frame or waiting for Mod4
            float ls0 = m_DS0[l, band, i], ls1 = m_DS1[l, band, i];

            // replacement: NeedsRebuild ("Rebuild visuals") or a different prefab (dev topsoil variant):
            // spawn the successor first, delete the old area once the successor has been live for an update
            Entity desired = SurfaceState.Prefab(layer, 100);
            if (rebuild || (desired != Entity.Null && t.Prefab != desired))
            {
                var n = NewStrip(c, layer, band, kind);
                if (BuildStrip(c, layer, band, kind, ls0, ls1, out float g0, out float g1) && SpawnArea(n, m_Poly))
                {
                    StampWrite(c, n, ls0, ls1, g0, g1);
                    n.Piece = i;
                    Retire(t.Area, n);
                    es.Areas[l, band, i] = n;
                }
                return;
            }

            // re-snap scheduling: the floor moved >= kAreaResnapDepthDelta since our last write
            if (t.ResnapDue == 0 && c.Gr.m_StepUpdate != t.StepAtWrite && math.abs(c.Floor - t.FloorAtWrite) >= RRWConst.kAreaResnapDepthDelta)
                t.ResnapDue = math.max(c.Gr.m_StepUpdate + (uint)kResnapDelay, m_Now + 1u);
            // upgrade works: the terrain beside the road changes in the update the works are applied (a narrowed road gives its
            // strip back) and the CPU height readback lags it, so every piece is written once more after it first appeared
            if (c.Upgrade && !t.SnapChecked && t.BoundUpdate != 0)
            {
                t.SnapChecked = true;
                if (t.ResnapDue == 0) t.ResnapDue = math.max(t.BoundUpdate + (uint)kResnapDelay, m_Now + 1u);
            }

            // hand-over clamp: a shrinking side never uncovers what the coverer (or the row's other pieces) has not covered
            // for >= 1 update yet
            float w0 = ls0, w1 = ls1;
            bool shrinks = ls0 > t.LS0 + kTol || ls1 < t.LS1 - kTol;
            if (shrinks)
            {
                // the re-keyed polygon of a split still covers the siblings: no write at all until their areas are on screen
                if (SplitHold(c, layer, band)) { deferred = true; return; }
                if (!DeferTimedOut(t)) ClampShrink(c, layer, band, i, t, ref w0, ref w1);
            }
            bool clamped = math.abs(w0 - ls0) > kTol || math.abs(w1 - ls1) > kTol;
            bool geomChanged = t.GeomRev != c.Rec.GeometryRevision || t.EndsHash != es.Ends.Hash || (c.Upgrade && UpgradeLatMoved(c, t));
            bool spanChanged = math.abs(w0 - t.LS0) >= kRewriteThreshold || math.abs(w1 - t.LS1) >= kRewriteThreshold
                               || Touches(w0, 0f) != Touches(t.LS0, 0f) || Touches(w1, c.L) != Touches(t.LS1, c.L);
            bool resnap = t.ResnapDue != 0 && m_Now >= t.ResnapDue;
            if (!geomChanged && !spanChanged && !resnap)
            {
                // a clamped shrink that cannot progress at all counts towards the wait timeout; otherwise nothing waits
                if (clamped) MarkDeferred(t); else t.DeferSince = 0;
                return;
            }
            bool force = bypass || geomChanged;
            if (!force && ((spanChanged && !tickOpen) || m_Budget <= 0)) { deferred = true; return; }
            WriteStrip(c, t, layer, band, i, kind, w0, w1, force, ref wrote);   // any progress resets the wait timeout
        }

        private void WriteStrip(EdgeCtx c, TrackedArea t, SurfaceLayer layer, int band, int piece, SurfaceBand kind, float s0, float s1, bool force, ref bool wrote)
        {
            if (!t.Live(m_Em)) { c.Es.Areas[(int)layer, band, piece] = null; return; }   // vanished: respawned next update
            if (BuildStrip(c, layer, band, kind, s0, s1, out float gs0, out float gs1))
            {
                bool spanMoved = math.abs(s0 - t.LS0) >= kTol || math.abs(s1 - t.LS1) >= kTol;
                if (RewriteArea(t, m_Poly))
                {
                    StampWrite(c, t, s0, s1, gs0, gs1);
                    if (!force) m_Budget--;
                    if (spanMoved) wrote = true;
                }
            }
            else
            {
                // nothing left to draw on this edge (e.g. entirely under a visible junction): treat as empty
                DeleteArea(t);
                c.Es.Areas[(int)layer, band, piece] = null;
            }
        }

        // Piece count or band kind changed: the whole row in this update ("same-frame swaps"). m_Row[0..k) = old row.
        private void Topology(EdgeCtx c, SurfaceLayer layer, int band, SurfaceBand kind, int k, bool kindSwitch, bool rebuild,
                              bool bypass, bool tickOpen, ref bool wrote, ref bool deferred)
        {
            var es = c.Es;
            int l = (int)layer;
            int m = m_DCount[l, band];
            // a piece appearing / leaving follows the project's write tick like any front step (a front crossing a node grows the
            // previous edge and starts the next one in the same update); a band-kind switch goes at once. Never held by the cap.
            if (!bypass && !tickOpen && !kindSwitch) { deferred = true; return; }
            Entity desired = SurfaceState.Prefab(layer, 100);
            int used = 0, nn = 0, kept = 0, spawned = 0, outgoing = 0;
            for (int j = 0; j < m; j++)
            {
                float d0 = m_DS0[l, band, j], d1 = m_DS1[l, band, j];
                TrackedArea keep = null;
                if (!kindSwitch && !rebuild)
                {
                    int best = -1;
                    float bestOv = kTol;
                    for (int i = 0; i < k; i++)
                    {
                        if ((used & (1 << i)) != 0) continue;
                        var o = m_Row[i];
                        float ov = math.min(d1, o.LS1) - math.max(d0, o.LS0);
                        if (ov > bestOv) { bestOv = ov; best = i; }
                    }
                    if (best >= 0)
                    {
                        var t = m_Row[best];
                        bool grows = d0 <= t.LS0 + kTol && d1 >= t.LS1 - kTol;   // growing never uncovers anything
                        if (grows && !t.Pending && t.Live(m_Em) && (desired == Entity.Null || t.Prefab == desired))
                        {
                            bool geomChanged = t.GeomRev != c.Rec.GeometryRevision || t.EndsHash != es.Ends.Hash || (c.Upgrade && UpgradeLatMoved(c, t));
                            bool moved = math.abs(d0 - t.LS0) >= kTol || math.abs(d1 - t.LS1) >= kTol;
                            if (!geomChanged && !moved) keep = t;
                            else if (BuildStrip(c, layer, band, kind, d0, d1, out float g0, out float g1) && RewriteArea(t, m_Poly))
                            {
                                StampWrite(c, t, d0, d1, g0, g1);
                                keep = t;
                            }
                            if (keep != null) { used |= 1 << best; kept++; }
                        }
                    }
                }
                if (keep == null)
                {
                    var n = NewStrip(c, layer, band, kind);
                    if (BuildStrip(c, layer, band, kind, d0, d1, out float g0, out float g1) && SpawnArea(n, m_Poly))
                    {
                        StampWrite(c, n, d0, d1, g0, g1);
                        keep = n;
                        spawned++;
                    }
                }
                if (keep != null) m_NewRow[nn++] = keep;
            }
            for (int i = 0; i < k; i++)
            {
                if ((used & (1 << i)) != 0) continue;
                var t = m_Row[i];
                if (t.Pending) { DeleteArea(t); continue; }   // never shown: cancel
                if (!t.Live(m_Em)) continue;
                t.OutgoingSince = m_Now;
                es.Outgoing.Add(t);
                outgoing++;
            }
            int rowN = es.RowN[l, band];
            for (int s = 0; s < rowN; s++) es.Areas[l, band, s] = null;
            for (int j = 0; j < nn; j++)
            {
                m_NewRow[j].Piece = j;
                es.Areas[l, band, j] = m_NewRow[j];
                m_NewRow[j] = null;
            }
            es.RowN[l, band] = (byte)nn;
            if (!bypass && m_Budget > 0) m_Budget--;
            if (spawned > 0 || kept > 0 || outgoing > 0) wrote = true;
            SurfaceState.TopologyChanges++;
            SurfaceState.TopologyKept += kept;
            SxPerf.Count(SxC.Topology);
            if (k > 1 || m > 1 || kindSwitch)
            {
                SurfaceState.LastTopology = "u" + m_Now + " e" + c.Edge.Index + " " + layer + "[" + band + "] " + k + "->" + m
                                            + (kindSwitch ? " kind->" + kind : "") + " kept=" + kept + " spawned=" + spawned + " outgoing=" + outgoing;
                RRWLog.Verbose("surfaces: pieces " + SurfaceState.LastTopology);
            }
        }

        // ------------------------------------------------------------------ outgoing (replaced) areas

        // Deletes replaced areas of this edge once nothing would show through (after the row diffs of this update).
        private void ProcessOutgoing(EdgeCtx c)
        {
            var list = c.Es.Outgoing;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var t = list[i];
                if (!t.Live(m_Em)) { list.RemoveAt(i); continue; }
                if (t.OutgoingSince >= m_Now) continue;   // replaced this update: its successors are not on screen yet
                bool timeout = m_Now - t.OutgoingSince > kDeferTimeout;
                if (!timeout && (SplitHold(c, t.Layer, t.Band) || !OutgoingCovered(c, t))) continue;
                if (timeout)
                {
                    SurfaceState.OutgoingTimeouts++;
                    RRWLog.Once("surf-outgoing-timeout", "surfaces: a replaced " + t.Layer + " piece waited " + kDeferTimeout
                                + " updates for its hand-over and was removed anyway (edge e" + c.Edge.Index + ", project #" + t.ProjectId + ")");
                }
                DeleteArea(t);
                list.RemoveAt(i);
                SurfaceState.OutgoingDeleted++;
            }
        }

        private bool OutgoingCovered(EdgeCtx c, TrackedArea t)
        {
            BuildPairs(c, t.Layer, t.Band, t.BandKind, -1, t.LatLo, t.LatHi);
            for (int p = 0; p < m_Pairs; p++)
                if (FirstGap(t.LS0, t.LS1, m_Need[p], m_Have[p], true, out _, out _)) return false;
            return true;
        }

        // Lost edge (split / combine): replaced areas stay a few updates more, then go.
        private void RetireOutgoing(EdgeSurf es)
        {
            for (int i = 0; i < es.Outgoing.Count; i++)
            {
                var t = es.Outgoing[i];
                if (!t.Pending && t.Live(m_Em)) Retire(t.Area, null, 3);
                else DeleteArea(t);
            }
            es.Outgoing.Clear();
        }

        // ------------------------------------------------------------------ hand-over rule

        private bool DeferTimedOut(TrackedArea t) => t.DeferSince != 0 && m_Now - t.DeferSince > kDeferTimeout;

        private void MarkDeferred(TrackedArea t)
        {
            if (t.DeferSince == 0) t.DeferSince = m_Now;
            SurfaceState.Deferred++;
        }

        // Split hand-over: the re-keyed remnant keeps its old (larger) polygons until every sibling's area of the same
        // (layer, band) row has been live for an update. In the split frame itself the siblings have not spawned yet.
        private bool SplitHold(EdgeCtx c, SurfaceLayer layer, int band)
        {
            var es = c.Es;
            if (es.SplitSiblings.Count == 0) return false;
            if (m_Now - es.SplitSince > kSplitHoldMax) { es.SplitSiblings.Clear(); return false; }
            if (es.SplitSince == m_Now) return true;
            int l = (int)layer;
            for (int i = 0; i < es.SplitSiblings.Count; i++)
            {
                if (!SurfaceState.Edges.TryGetValue(es.SplitSiblings[i], out var sib)) return true;   // not processed yet
                if (band >= sib.Slots) return true;   // its upgrade rows are not set up yet
                for (int k = 0; k < sib.RowN[l, band]; k++)
                {
                    var st = sib.Areas[l, band, k];
                    if (st != null && !st.Settled(m_Em, m_Now)) return true;
                }
            }
            return false;
        }

        // The layer whose presence makes it safe for `layer` to shrink (hand-over rule), -1 = none.
        private int CovererOf(WorksKind kind, WorksPhase ph, SurfaceLayer layer)
        {
            int cov = -1;
            bool constr = kind == WorksKind.Construction;
            switch (layer)
            {
                case SurfaceLayer.BaseCourseCover:
                    if (constr) cov = ph >= WorksPhase.Paving && ph <= WorksPhase.Finishing ? (int)SurfaceLayer.FreshAsphaltCover : (int)SurfaceLayer.Subgrade;
                    else cov = ph == WorksPhase.Restore ? (int)SurfaceLayer.TopsoilStrip : (int)SurfaceLayer.Subgrade;
                    break;
                case SurfaceLayer.Subgrade:
                    if (!constr) cov = (int)SurfaceLayer.TopsoilStrip;
                    break;
                case SurfaceLayer.FreshAsphalt:
                    if (!constr) cov = (int)SurfaceLayer.BaseCourseCover;
                    break;
                case SurfaceLayer.FreshAsphaltCover:
                    cov = constr ? (int)SurfaceLayer.FreshAsphalt : (int)SurfaceLayer.BaseCourseCover;
                    break;
            }
            if (cov == (int)SurfaceLayer.TopsoilStrip && !m_TopsoilOn) cov = -1;
            return cov;
        }

        // (need, have) pairs for a piece of `layer` in band slot `band` built for `pieceKind`:
        //   * the layer itself: the row's other desired pieces vs their settled spans (same band kind: this band only; after a
        //     band-kind switch: every band of the current kind, each must show where it is wanted);
        //   * the covering layer: per coverer band its desired pieces vs its settled spans (C4 halves pair half with half).
        // Settled = on screen for >= 1 update (TrackedArea.SettledSpan: the previous span when rewritten this update).
        private void BuildPairs(EdgeCtx c, SurfaceLayer layer, int band, SurfaceBand pieceKind, int exclude, float latLo = float.NaN, float latHi = float.NaN)
        {
            m_Pairs = 0;
            int l = (int)layer;
            var kind = m_BandKind[l];
            if (c.Upgrade && pieceKind == kind)
            {
                // upgrade works: every layer of every sub-strip that overlaps this one sideways (UpgradePairs)
                UpgradePairs(c, l, band, exclude, latLo, latHi);
                return;
            }
            int slots = c.Upgrade ? c.Es.Slots : EdgeSection.kMaxIntervals;
            if (pieceKind == kind) AddPair(c, l, band, kind, exclude);
            else for (int b = 0; b < slots; b++) AddPair(c, l, b, kind, -1);
            if (c.Upgrade) return;   // a band-kind switch of upgrade works waits for the new kind's rows only
            int cov = CovererOf(c.Project.Kind, c.Project.Phase, layer);
            if (cov < 0) return;
            var covKind = m_BandKind[cov];
            bool paired = covKind == SurfaceBand.CarriageHalf && kind == SurfaceBand.CarriageHalf && pieceKind == kind;
            if (paired) AddPair(c, cov, band, covKind, -1);
            else for (int b = 0; b < EdgeSection.kMaxIntervals; b++) AddPair(c, cov, b, covKind, -1);
        }

        private void AddPair(EdgeCtx c, int l, int b, SurfaceBand kind, int exclude)
        {
            if (m_Pairs >= kMaxPairs) return;
            int n = m_DCount[l, b];
            if (n == 0 || (n == 1 && exclude == 0)) return;
            ref var need = ref m_Need[m_Pairs];
            ref var have = ref m_Have[m_Pairs];
            need.Clear();
            have.Clear();
            for (int j = 0; j < n; j++)
                if (j != exclude) need.Add(new Span(m_DS0[l, b, j], m_DS1[l, b, j]));
            if (need.IsEmpty) return;
            var es = c.Es;
            int rowN = es.RowN[l, b];
            for (int k = 0; k < rowN; k++)
            {
                if (k == exclude) continue;
                var t = es.Areas[l, b, k];
                if (t == null || t.BandKind != kind || !t.SettledSpan(m_Em, m_Now, out float s0, out float s1)) continue;
                have.Add(new Span(s0, s1));
            }
            m_Pairs++;
        }

        // Shrinking piece `piece` from [t.LS0, t.LS1] towards [w0, w1]: keep whatever a pair still needs (the single-strip
        // start / end clamp generalised to piece sets): up to the start of a need piece that is not shown at all, or kSpanOverlap short of
        // where the shown part ends.
        private void ClampShrink(EdgeCtx c, SurfaceLayer layer, int band, int piece, TrackedArea t, ref float w0, ref float w1)
        {
            BuildPairs(c, layer, band, t.BandKind, piece, t.LatLo, t.LatHi);
            if (m_Pairs == 0) return;
            if (w0 > t.LS0 + kTol)
            {
                float x = w0;
                for (int p = 0; p < m_Pairs; p++)
                    if (FirstGap(t.LS0, w0, m_Need[p], m_Have[p], true, out float q, out bool edge))
                        x = math.min(x, edge ? q : q - RRWConst.kSpanOverlap);
                w0 = math.max(t.LS0, x);
            }
            if (w1 < t.LS1 - kTol)
            {
                float y = w1;
                for (int p = 0; p < m_Pairs; p++)
                    if (FirstGap(w1, t.LS1, m_Need[p], m_Have[p], false, out float q, out bool edge))
                        y = math.max(y, edge ? q : q + RRWConst.kSpanOverlap);
                w1 = math.min(t.LS1, y);
            }
            if (w1 < w0) { float mid = (w0 + w1) * 0.5f; w0 = w1 = mid; }
        }

        // First point of [a, b] (scanning from a, or backwards from b) that lies in `need` but not in `have` (tolerance kTol).
        // atNeedEdge: the point is where that need piece starts (scanning forward) / ends (backward), i.e. nothing of it shows.
        internal static bool FirstGap(float a, float b, in SpanSet need, in SpanSet have, bool fromStart, out float p, out bool atNeedEdge)
        {
            p = 0f;
            atNeedEdge = false;
            if (b - a <= kTol) return false;
            int nc = need.Count, hc = have.Count;
            if (fromStart)
            {
                for (int i = 0; i < nc; i++)
                {
                    var n = need[i];
                    float na = math.max(a, n.A), nb = math.min(b, n.B);
                    if (nb - na <= kTol) continue;
                    float cur = na;
                    for (int h = 0; h < hc; h++)
                    {
                        var hv = have[h];
                        if (hv.B <= cur) continue;
                        if (hv.A <= cur + kTol) cur = hv.B;
                        else break;
                    }
                    if (cur < nb - kTol) { p = cur; atNeedEdge = cur <= na; return true; }
                }
                return false;
            }
            for (int i = nc - 1; i >= 0; i--)
            {
                var n = need[i];
                float na = math.max(a, n.A), nb = math.min(b, n.B);
                if (nb - na <= kTol) continue;
                float cur = nb;
                for (int h = hc - 1; h >= 0; h--)
                {
                    var hv = have[h];
                    if (hv.A >= cur) continue;
                    if (hv.B >= cur - kTol) cur = hv.A;
                    else break;
                }
                if (cur > na + kTol) { p = cur; atNeedEdge = cur >= nb; return true; }
            }
            return false;
        }

        // Settled coverage of a row (union of its current pieces' settled spans).
        private void SettledRow(EdgeSurf es, int l, int b, ref SpanSet set)
        {
            set.Clear();
            int rowN = es.RowN[l, b];
            for (int k = 0; k < rowN; k++)
            {
                var t = es.Areas[l, b, k];
                if (t != null && t.SettledSpan(m_Em, m_Now, out float s0, out float s1)) set.Add(new Span(s0, s1));
            }
        }
    }
}
