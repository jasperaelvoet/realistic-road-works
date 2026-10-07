using System;
using System.Collections.Generic;
using Game.Common;
using Unity.Entities;
using Unity.Mathematics;

// SurfaceAreaSystem, third part: the yellow temporary lines, the C4 per-half log
// lines and the runtime restyle of the line clones.
//
// Yellow lines (layer TempMarking, band kind TempLines):
//   * which: EdgeSection.TempLine(i, chainReversed, openHalf, RRWCity.NaTheme) for i < TempLineCount (EU: divider-side line,
//     outer edge line, one dashed line per boundary between same-direction lanes of the open half; NA: the divider-side line);
//   * where along the road: PhasePlan.SurfaceSpans(TempMarking, view), always ONE chain-level piece (by design; Core: C4a only; with the swap off it starts at the
//     tape-pulled front from f = .90), per edge through the junction rules of the Roads layers (clipped at visible junctions,
//     mitred through interior nodes, stopped at works junctions);
//   * when: ONLY while the open half is APPLIED open on that edge (EdgeRecord.OpenLanesApplied, Traffic) and
//     RRWSetting.TempMarkingsOn - a line never tells drivers something the simulation does not do. They appear in the update
//     the half is applied open and disappear in the update that changes (swap, re-close, setting, phase) - no throttle, no
//     hand-over wait (the open half's Fresh Asphalt Cover lies under them, so nothing white shows).
//   * dashes: kTempDash / kTempGap in CHAIN u anchored at the project's Trim0 (the pattern runs on across nodes), one area per
//     dash, keyed by the dash index so a moving tape front only deletes / trims the dashes it passes. A row (all its pieces)
//     is always spawned or rewritten complete in one update and counted ONCE against the global rewrite cap.
namespace RealisticRoadWorks.V3.Surfaces
{
    public partial class SurfaceAreaSystem
    {
        const int kMaxDashesPerRow = 96;     // 864 m of one lane line per edge: far beyond any edge
        const float kMinPiece = 0.3f;        // = SurfaceGeom.kMinGeomLength

        private struct Piece
        {
            public int Key;                  // -1 solid, else dash index in chain u
            public float S0, S1;             // geometric edge-local range drawn
            public float L0, L1;             // logical span (solid: ToEdgeLocal output; dash: = S0/S1)
        }

        private readonly List<Piece> m_Desired = new List<Piece>(32);
        private readonly List<TrackedArea> m_NewPieces = new List<TrackedArea>(32);
        private int m_FrameLineEdges, m_FrameLineRows, m_FrameLinePieces;   // per project, this update
        private string m_FrameLineWhy;

        // DEVTOOLS: rrw.surf.line test lines (SurfaceDevLines.cs). Release builds compile the call away.
        partial void ProcessDevLines();

        // ------------------------------------------------------------------ yellow lines of one edge

        private void ProcessTempLines(EdgeCtx c, bool rebuild, bool bypass, bool tickOpen, ref bool wrote, ref bool deferred)
        {
            if (c.Upgrade && (!UpgradeRemarkNow(c.View.Upgrade) || HalfTempLanes(c)))
            {
                ProcessUpgradeTempLines(c, rebuild, bypass, tickOpen, ref wrote, ref deferred);
                return;
            }
            var es = c.Es;
            var v = c.View;
            RoadZones open = v.OpenLanes & RoadZones.Carriageway;
            bool single = open == RoadZones.LeftHalf || open == RoadZones.RightHalf;
            var st = RRWSettings.Current;
            Entity prefab = SurfaceState.Prefab(SurfaceLayer.TempMarking, 100);
            float ls0 = 0f, ls1 = 0f;
            string why = null;
            // upgrade works: only edges that re-mark (under one direction at a time the re-marking draws them like a new road's
            // first painting half)
            if (!c.ModeA && !(c.Upgrade && m_RemarkOn)) why = c.Upgrade ? "no re-marking on this edge" : "not mode A";
            else if (!SurfacePalette.kTempMarkingOn || prefab == Entity.Null) why = "line clone not registered";
            else if (st == null || !st.TempMarkingsOn) why = "setting TempMarkings off";
            else if (v.Kind != WorksKind.Construction || v.Phase != WorksPhase.Finishing) why = "not in C4";
            else if (!single) why = open == RoadZones.None ? "no car half open" : "both halves open";
            else if ((c.Rec.OpenLanesApplied & open) != open) why = "open half " + RoadZoneMath.Describe(open) + " not applied yet (applied " + RoadZoneMath.Describe(c.Rec.OpenLanesApplied) + ")";
            else if (!PhasePlan.ToEdgeLocal(m_ProjSpan[(int)SurfaceLayer.TempMarking], c.Site.m_ChainU0, c.Site.m_ChainU1, c.L, out ls0, out ls1))
                why = v.SwapActive && PhasePlan.StageIndexOf(v) != 0 ? "C4b (the open half has its white markings)" : "tape pulled / no span on this edge";
            int rows = why == null ? c.Rec.Section.TempLineCount(open, c.Reversed, RRWCity.NaTheme) : 0;
            if (why == null && rows == 0) why = "no direction split on this edge (EdgeSection.DirSplit NaN)";
            float gs0 = 0f, gs1 = 0f;
            float3 cutS = float3.zero, cutE = float3.zero;
            bool rs = false, re = false;
            if (why == null && !SurfaceGeom.GeometricRange(SurfaceLayer.TempMarking, ls0, ls1, c.L, es.Ends, out gs0, out gs1, out cutS, out cutE,
                                                          out rs, out re, out _, out _))
                why = "nothing left outside the junctions";
            if (why != null)
            {
                es.TempReason = why;
                if (es.TempRows.Count > 0) RemoveTempRows(es, why);
                es.TempOpenHalf = RoadZones.None;
                if (m_FrameLineWhy == null) m_FrameLineWhy = why;
                return;
            }
            es.TempReason = "";
            if (es.TempOpenHalf != open && es.TempRows.Count > 0) RemoveTempRows(es, "open half changed");
            es.TempOpenHalf = open;
            bool appear = es.TempRows.Count == 0;

            // rows beyond the count (theme / lane lines / RRWGates switch changed): gone now
            while (es.TempRows.Count > rows)
            {
                var last = es.TempRows[es.TempRows.Count - 1];
                for (int k = 0; k < last.Pieces.Count; k++) { DeleteArea(last.Pieces[k]); SurfaceState.TempPieceDeletes++; }
                es.TempRows.RemoveAt(es.TempRows.Count - 1);
            }
            int pieces = 0;
            for (int i = 0; i < rows; i++)
            {
                if (!c.Rec.Section.TempLine(i, c.Reversed, open, RRWCity.NaTheme, out float left, out float right)) continue;
                TempRow row;
                if (i < es.TempRows.Count) row = es.TempRows[i];
                else
                {
                    row = new TempRow { Line = i, Sig = 0u };
                    es.TempRows.Add(row);
                }
                row.Line = i;
                row.Dashed = EdgeSection.TempLineDashed(i);
                uint sig = math.hash(new float4(left, right, c.Rec.GeometryRevision, 0f)) ^ (c.Es.Ends.Hash * 2654435761u) ^ (row.Dashed ? 0x9E3779B9u : 0u);
                DesiredPieces(c, row.Dashed, ls0, ls1, gs0, gs1);
                DiffRow(c, row, i, left, right, sig, prefab, appear, rebuild, bypass, tickOpen, cutS, cutE, rs, re, ref wrote, ref deferred);
                pieces += row.Pieces.Count;
            }
            if (rows > 0)
            {
                m_FrameLineEdges++;
                m_FrameLineRows += rows;
                m_FrameLinePieces += pieces;
            }
        }

        // ------------------------------------------------------------------ upgrade works: lines of the lanes kept open
        //
        // While an upgrade's final markings are still covered (every window before the re-marking), the lanes that carry traffic
        // get yellow construction lines for the layout that is really open: a centre line between the directions (doubled in
        // the NA theme), dashed lines between lanes of one direction, and a solid edge line next to a dropped lane. Lanes under
        // a closed group (carriageway or one direction closed) carry no traffic and get none.

        private struct UwLine { public float C; public bool Dashed; }
        private struct UwLane { public float Lo, Hi; public sbyte Dir; public bool Open; }
        private readonly List<UwLine> m_UwLines = new List<UwLine>(8);
        private readonly List<UwLane> m_UwLanes = new List<UwLane>(8);

        // The re-marking band works now (its lines come from the one-direction re-marking path).
        private static bool UpgradeRemarkNow(in UpgradeView u)
        {
            if (!u.Valid || u.Window < 0 || u.Window >= u.WindowCount) return false;
            for (int i = 0; i < u.BandCount; i++)
                if (u.Band(i).Kind == BandKind.Remark && u.Band(i).Window == u.AppliedWindow) return true;
            return false;
        }

        // Traffic moved the closed half's lanes onto temporary lanes over the open half (re-marking one half at a time).
        private static bool HalfTempLanes(EdgeCtx c) =>
            c.Ue != null && c.Ue.TempHalfWindow >= 0 && c.Ue.TempHalfWindow == c.View.Upgrade.AppliedWindow && !float.IsNaN(c.Ue.TempHalfLo);

        // The re-marking is still to come, so the new white markings are covered.
        private static bool UpgradeRemarkPending(in UpgradeView u)
        {
            if (!u.Valid || u.Window >= u.WindowCount) return false;
            for (int i = 0; i < u.BandCount; i++)
                if (u.Band(i).Kind == BandKind.Remark && u.Band(i).Window > u.Window) return true;
            return false;
        }

        private void ProcessUpgradeTempLines(EdgeCtx c, bool rebuild, bool bypass, bool tickOpen, ref bool wrote, ref bool deferred)
        {
            if (!c.UpgradeReady) return;   // waits with what is on screen
            var es = c.Es;
            var v = c.View;
            var u = v.Upgrade;
            var st = RRWSettings.Current;
            Entity prefab = SurfaceState.Prefab(SurfaceLayer.TempMarking, 100);
            string why = null;
            if (!SurfacePalette.kTempMarkingOn || prefab == Entity.Null) why = "line clone not registered";
            else if (st == null || !st.TempMarkingsOn) why = "setting TempMarkings off";
            else if (HalfTempLanes(c)) why = PlanLineSet(c.Ue.PlanFor(c.Ue.TempHalfWindow, u.AllAtOnce, c.Ue.TempHalfLo, c.Ue.TempHalfHi));
            else if (!UpgradeRemarkPending(u)) why = "no covered markings";
            else if (u.AppliedTraffic == BandTraffic.Carriageway || u.AppliedTraffic == BandTraffic.Half) why = "lanes closed";
            else why = UpgradeLineSet(c);
            float ls0 = 0f, ls1 = 0f, gs0 = 0f, gs1 = 0f;
            float3 cutS = float3.zero, cutE = float3.zero;
            bool rs = false, re = false;
            if (why == null)
            {
                var span = new Span(math.min(v.Trim0, v.Trim1), math.max(v.Trim0, v.Trim1));
                m_ProjSpan[(int)SurfaceLayer.TempMarking] = span;
                if (!PhasePlan.ToEdgeLocal(span, c.Site.m_ChainU0, c.Site.m_ChainU1, c.L, out ls0, out ls1)) why = "no span on this edge";
            }
            if (why == null && !SurfaceGeom.GeometricRange(SurfaceLayer.TempMarking, ls0, ls1, c.L, es.Ends, out gs0, out gs1, out cutS, out cutE,
                                                          out rs, out re, out _, out _))
                why = "nothing left outside the junctions";
            if (why != null)
            {
                es.TempReason = why;
                if (es.TempRows.Count > 0) RemoveTempRows(es, why);
                if (m_FrameLineWhy == null) m_FrameLineWhy = why;
                return;
            }
            es.TempReason = "";
            bool appear = es.TempRows.Count == 0;
            int rows = m_UwLines.Count;
            while (es.TempRows.Count > rows)
            {
                var last = es.TempRows[es.TempRows.Count - 1];
                for (int k = 0; k < last.Pieces.Count; k++) { DeleteArea(last.Pieces[k]); SurfaceState.TempPieceDeletes++; }
                es.TempRows.RemoveAt(es.TempRows.Count - 1);
            }
            float hw = math.max(0.02f, RRWGates.TempLineWidth) * 0.5f;
            int pieces = 0;
            for (int i = 0; i < rows; i++)
            {
                var line = m_UwLines[i];
                float left = line.C - hw, right = line.C + hw;
                TempRow row;
                if (i < es.TempRows.Count) row = es.TempRows[i];
                else
                {
                    row = new TempRow { Line = i, Sig = 0u };
                    es.TempRows.Add(row);
                }
                if (row.Dashed != line.Dashed) row.Sig = 0u;
                row.Line = i;
                row.Dashed = line.Dashed;
                uint sig = math.hash(new float4(left, right, c.Rec.GeometryRevision, 1f)) ^ (c.Es.Ends.Hash * 2654435761u) ^ (row.Dashed ? 0x9E3779B9u : 0u);
                DesiredPieces(c, row.Dashed, ls0, ls1, gs0, gs1);
                DiffRow(c, row, i, left, right, sig, prefab, appear, rebuild, bypass, tickOpen, cutS, cutE, rs, re, ref wrote, ref deferred);
                pieces += row.Pieces.Count;
            }
            if (rows > 0)
            {
                m_FrameLineEdges++;
                m_FrameLineRows += rows;
                m_FrameLinePieces += pieces;
            }
        }

        // m_UwLines for the edge (EDGE frame line centres). Null when there are lines, else why not.
        private string UpgradeLineSet(EdgeCtx c)
        {
            m_UwLines.Clear();
            m_UwLanes.Clear();
            var cs = c.Ue.CrossSection;
            var u = c.View.Upgrade;
            int geo = c.Rec.GeometryRevision;
            // temporary lanes of the applied window: the lines follow them (lanes moved over the old asphalt)
            int dw = c.Ue.DropWindowFor(geo, u);
            if (dw >= 0)
            {
                var plan = c.Ue.PlanFor(dw, u.AllAtOnce);
                if (plan.SlotCount > 0 && plan.Arrangement != TempArrangement.Untouched) return PlanLineSet(plan);
            }
            int open = 0;
            for (int k = 0; k < cs.Count; k++)
            {
                var s = cs[k];
                if (s.Kind != SubKind.DriveLane || s.Dir == 0) continue;
                bool dropped = c.Ue.DroppedIn(geo, u, s.Lo, s.Hi, -1, false);
                m_UwLanes.Add(new UwLane { Lo = s.Lo, Hi = s.Hi, Dir = s.Dir, Open = !dropped });
                if (!dropped) open++;
            }
            if (open == 0) return "no lane open";
            m_UwLanes.Sort((a, b) => a.Lo.CompareTo(b.Lo));
            float hw = math.max(0.02f, RRWGates.TempLineWidth) * 0.5f;
            float inset = RRWConst.kTempLineEdgeInset + hw;
            for (int k = 0; k < m_UwLanes.Count; k++)
            {
                var a = m_UwLanes[k];
                if (!a.Open) continue;
                bool hasPrev = k > 0 && m_UwLanes[k - 1].Hi > a.Lo - 0.6f;
                bool hasNext = k + 1 < m_UwLanes.Count && m_UwLanes[k + 1].Lo < a.Hi + 0.6f;
                if (hasPrev && !m_UwLanes[k - 1].Open) m_UwLines.Add(new UwLine { C = a.Lo + inset });
                if (!hasNext) continue;
                var b = m_UwLanes[k + 1];
                if (!b.Open) { m_UwLines.Add(new UwLine { C = a.Hi - inset }); continue; }
                float x = (a.Hi + b.Lo) * 0.5f;
                if (a.Dir == b.Dir) m_UwLines.Add(new UwLine { C = x, Dashed = true });
                else if (RRWCity.NaTheme)
                {
                    m_UwLines.Add(new UwLine { C = x - RRWConst.kTempLineDividerOffset * 0.5f - hw });
                    m_UwLines.Add(new UwLine { C = x + RRWConst.kTempLineDividerOffset * 0.5f + hw });
                }
                else m_UwLines.Add(new UwLine { C = x });
            }
            return m_UwLines.Count > 0 ? null : "no lane boundary to mark";
        }

        private static readonly List<float2> s_PlanLines = new List<float2>(8);

        // Lines of a temporary lane plan: edge lines inside the outer slot edges, a centre line (double in the NA theme) between
        // opposing slots, dashed lines between slots of one direction.
        private string PlanLineSet(TempLanePlan plan)
        {
            m_UwLines.Clear();
            m_UwLanes.Clear();
            UpgradeTempLanes.Lines(plan, s_PlanLines);
            float hw = math.max(0.02f, RRWGates.TempLineWidth) * 0.5f;
            float inset = RRWConst.kTempLineEdgeInset + hw;
            for (int k = 0; k < s_PlanLines.Count; k++)
            {
                float x = s_PlanLines[k].x;
                int kind = (int)s_PlanLines[k].y;
                if (kind == 0)
                {
                    // edge line: inside the slot run (first line of a run is its left edge)
                    bool left = IsRunStart(k);
                    m_UwLines.Add(new UwLine { C = left ? x + inset : x - inset });
                }
                else if (kind == 2) m_UwLines.Add(new UwLine { C = x, Dashed = true });
                else if (RRWCity.NaTheme)
                {
                    m_UwLines.Add(new UwLine { C = x - RRWConst.kTempLineDividerOffset * 0.5f - hw });
                    m_UwLines.Add(new UwLine { C = x + RRWConst.kTempLineDividerOffset * 0.5f + hw });
                }
                else m_UwLines.Add(new UwLine { C = x });
            }
            return m_UwLines.Count > 0 ? null : "no lane boundary to mark";
        }

        // Edge lines come in pairs per slot run (start, end): an even count of edge lines before k = k starts a run.
        private static bool IsRunStart(int k)
        {
            int edges = 0;
            for (int i = 0; i < k; i++) if ((int)s_PlanLines[i].y == 0) edges++;
            return edges % 2 == 0;
        }

        // m_Desired = the pieces of one row on this edge (edge-local s, geometric).
        private void DesiredPieces(EdgeCtx c, bool dashed, float ls0, float ls1, float gs0, float gs1)
        {
            m_Desired.Clear();
            if (!dashed)
            {
                m_Desired.Add(new Piece { Key = -1, S0 = gs0, S1 = gs1, L0 = ls0, L1 = ls1 });
                return;
            }
            // dashes in CHAIN u, anchored at the trimmed chain start; clipped to the line's chain span, this edge and the drawn range
            var span = m_ProjSpan[(int)SurfaceLayer.TempMarking];
            float u0 = c.Site.m_ChainU0, u1 = c.Site.m_ChainU1;
            float lo = math.max(span.A, math.min(u0, u1)), hi = math.min(span.B, math.max(u0, u1));
            if (hi - lo < kMinPiece || math.abs(u1 - u0) < 1e-3f) return;
            float anchor = c.View.Trim0;
            float dash = RRWConst.kTempDash, period = RRWConst.kTempDash + RRWConst.kTempGap;
            float clip0 = math.max(gs0, 0f), clip1 = math.min(gs1, c.L);
            int k = (int)math.floor((lo - anchor) / period);
            for (int n = 0; n < kMaxDashesPerRow; n++, k++)
            {
                float d0 = anchor + k * period;
                if (d0 >= hi) break;
                float x0 = math.max(d0, lo), x1 = math.min(d0 + dash, hi);
                if (x1 - x0 < kMinPiece) continue;
                float a = (x0 - u0) / (u1 - u0) * c.L, b = (x1 - u0) / (u1 - u0) * c.L;
                float s0 = math.max(math.min(a, b), clip0), s1 = math.min(math.max(a, b), clip1);
                if (s1 - s0 < kMinPiece) continue;
                m_Desired.Add(new Piece { Key = k, S0 = s0, S1 = s1, L0 = s0, L1 = s1 });
            }
        }

        private void DiffRow(EdgeCtx c, TempRow row, int line, float left, float right, uint sig, Entity prefab, bool appear,
                             bool rebuild, bool bypass, bool tickOpen, float3 cutS, float3 cutE, bool rs, bool re,
                             ref bool wrote, ref bool deferred)
        {
            var list = row.Pieces;
            // vanished / stale tracking (staggered heal like the strips)
            for (int k = list.Count - 1; k >= 0; k--)
            {
                var t = list[k];
                bool dead = t.Pending ? m_Now - t.DefUpdate > kPendingTimeout : ((c.Heal || rebuild) && !t.Live(m_Em));
                if (dead) { DeleteArea(t); list.RemoveAt(k); }
            }
            bool sigChanged = row.Sig != sig || rebuild;
            // what has to happen
            bool any = false, moveOnly = true;
            for (int k = 0; k < list.Count && !any; k++)
                if (IndexOfKey(list[k].Key) < 0) any = true;
            for (int d = 0; d < m_Desired.Count; d++)
            {
                var t = FindKey(list, m_Desired[d].Key);
                if (t == null) { any = true; moveOnly = false; continue; }
                if (t.Prefab != prefab || t.Pending && (sigChanged || Moved(t, m_Desired[d]))) { any = true; moveOnly = false; continue; }
                if (sigChanged) { any = true; moveOnly = false; continue; }
                if (Moved(t, m_Desired[d])) any = true;
            }
            if (!any) { row.Sig = sig; return; }
            // pure front moves (tape-pulled teardown) follow the project's write tick and the global cap like the strips;
            // appearing rows, new pieces, geometry / clone changes go out at once (the whole row in this update)
            bool budgeted = moveOnly && !appear && !bypass;
            if (budgeted && (!tickOpen || m_Budget <= 0)) { deferred = true; return; }
            // deletions
            for (int k = list.Count - 1; k >= 0; k--)
                if (IndexOfKey(list[k].Key) < 0) { DeleteArea(list[k]); list.RemoveAt(k); SurfaceState.TempPieceDeletes++; }
            // spawns / rewrites / clone swaps
            bool moved = false;
            for (int d = 0; d < m_Desired.Count; d++)
            {
                var want = m_Desired[d];
                var t = FindKey(list, want.Key);
                bool solid = want.Key < 0;
                bool respawn = t == null || t.Prefab != prefab || (t.Pending && (sigChanged || Moved(t, want)));
                if (!respawn && (t.Pending || (!sigChanged && !Moved(t, want)))) continue;   // unchanged (or still binding)
                if (!BuildLine(c, left, right, want.S0, want.S1, solid ? cutS : float3.zero, solid ? cutE : float3.zero, solid && rs, solid && re)) continue;
                if (respawn)
                {
                    var n = new TrackedArea
                    {
                        Layer = SurfaceLayer.TempMarking, Band = line, BandKind = SurfaceBand.TempLines, Key = want.Key,
                        Prefab = prefab, Site = c.Edge, ProjectId = c.Project.Id, Group = DerivedGroup.Area,
                    };
                    if (!SpawnArea(n, m_Poly)) continue;
                    StampPiece(c, n, want);
                    if (t != null)
                    {
                        if (t.Pending) DeleteArea(t);
                        else Retire(t.Area, n);   // clone swap (RRWGates.TempLineSource): spawn-first, the old line goes once the new one shows
                        list.Remove(t);
                    }
                    list.Add(n);
                    SurfaceState.TempPieceSpawns++;
                    moved = true;
                    continue;
                }
                if (RewriteArea(t, m_Poly))
                {
                    StampPiece(c, t, want);
                    SurfaceState.TempPieceRewrites++;
                    moved = true;
                }
            }
            row.Sig = sig;
            row.Left = left;
            row.Right = right;
            SurfaceState.TempRowWrites++;
            if (budgeted) m_Budget--;
            if (moved) wrote = true;
        }

        private int IndexOfKey(int key)
        {
            for (int d = 0; d < m_Desired.Count; d++) if (m_Desired[d].Key == key) return d;
            return -1;
        }

        private static TrackedArea FindKey(List<TrackedArea> list, int key)
        {
            for (int k = 0; k < list.Count; k++) if (list[k].Key == key) return list[k];
            return null;
        }

        private static bool Moved(TrackedArea t, in Piece p) => math.abs(t.GS0 - p.S0) > 0.05f || math.abs(t.GS1 - p.S1) > 0.05f;

        private void StampPiece(EdgeCtx c, TrackedArea t, in Piece p)
        {
            t.HasPrev = t.BoundUpdate != 0 || t.Area != Entity.Null;
            t.PrevLS0 = t.LS0; t.PrevLS1 = t.LS1;
            t.LS0 = p.L0; t.LS1 = p.L1;
            t.GS0 = p.S0; t.GS1 = p.S1;
            t.GeomRev = c.Rec.GeometryRevision;
            t.EndsHash = c.Es.Ends.Hash;
        }

        // Thin polygon [left, right] (edge frame) over the drawn range [s0, s1]; Y by the vertex-Y rule (road surface on a
        // visible road). No margin, no taper: EdgeSection.TempLine is the exact line.
        private bool BuildLine(EdgeCtx c, float left, float right, float s0, float s1, float3 cutS, float3 cutE, bool rs, bool re)
        {
            var lat = new BandLat { Left = left, Right = right, Margin = 0f, MinWidth = SurfacePalette.kTempMarkingMinWidth };
            SurfaceGeom.Strip(c.Rec.Arc, s0, s1, lat, cutS, cutE, rs, re, m_Poly);
            return ApplyVertexY(c);
        }

        // Deletes every yellow line of an edge now (swap, re-close, setting off, completion). Returns the number of areas.
        private int RemoveTempRows(EdgeSurf es, string why)
        {
            int n = 0;
            for (int i = 0; i < es.TempRows.Count; i++)
            {
                var row = es.TempRows[i];
                for (int k = 0; k < row.Pieces.Count; k++) { DeleteArea(row.Pieces[k]); n++; }
            }
            es.TempRows.Clear();
            es.TempOpenHalf = RoadZones.None;
            SurfaceState.TempPieceDeletes += n;
            if (n > 0 && why != null) RRWLog.Verbose("surfaces: edge " + RRWLog.E(es.Edge) + " yellow lines removed (" + n + " area(s), " + why + ")");
            return n;
        }

        // Lost edge (split / combine): live lines stay a few updates while the remnant spawns its own (no blink); else deleted.
        private void RetireTempRows(EdgeSurf es, bool keepBriefly)
        {
            for (int i = 0; i < es.TempRows.Count; i++)
            {
                var row = es.TempRows[i];
                for (int k = 0; k < row.Pieces.Count; k++)
                {
                    var t = row.Pieces[k];
                    if (keepBriefly && !t.Pending && t.Live(m_Em)) { Retire(t.Area, null, 3); t.Area = Entity.Null; }
                    else DeleteArea(t);
                }
            }
            es.TempRows.Clear();
            es.TempOpenHalf = RoadZones.None;
        }

        // ------------------------------------------------------------------ restyle (experimental roundness / LOD bias switches)

        // The prefab-side style of these clones changed this update (SurfaceStyle.Sync): mark their live areas Updated so
        // AreaBatchSystem re-adds the triangles with the new expand amount and GeometrySystem recomputes the LOD.
        private void RestyleAreas(HashSet<Entity> prefabs)
        {
            int n = 0;
            foreach (var es in SurfaceState.Edges.Values)
            {
                for (int i = 0; i < es.TempRows.Count; i++)
                {
                    var row = es.TempRows[i];
                    for (int k = 0; k < row.Pieces.Count; k++) n += Touch(row.Pieces[k], prefabs);
                }
                for (int l = 0; l < es.Areas.GetLength(0); l++)
                    for (int b = 0; b < es.Areas.GetLength(1); b++)
                        for (int k = 0; k < es.RowN[l, b]; k++)
                            if (es.Areas[l, b, k] != null) n += Touch(es.Areas[l, b, k], prefabs);
            }
            foreach (var dl in SurfaceState.DevLines)
                for (int k = 0; k < dl.Pieces.Count; k++) n += Touch(dl.Pieces[k], prefabs);
            if (n > 0) RRWLog.Info("surfaces: " + n + " line area(s) re-batched for the new clone style");
        }

        private int Touch(TrackedArea t, HashSet<Entity> prefabs)
        {
            if (t == null || !prefabs.Contains(t.Prefab) || !t.Live(m_Em)) return 0;
            if (!m_Em.HasComponent<Updated>(t.Area)) m_Em.AddComponent<Updated>(t.Area);
            return 1;
        }

        // ------------------------------------------------------------------ log lines (for in-game verification)

        // One Info line when a project's C4 Fresh Asphalt (Cover) switches to / from the per-half bands, and at each stage change.
        private void LogHalves(ProjectRecord p, ProjSurf ps, in ProjectView v)
        {
            bool halves = m_BandKind[(int)SurfaceLayer.FreshAsphaltCover] == SurfaceBand.CarriageHalf;
            int stage = halves ? PhasePlan.StageIndexOf(v) : -1;
            if (halves == ps.HalvesShown && stage == ps.HalvesStage) return;
            string line;
            int ci = (int)SurfaceLayer.FreshAsphaltCover;
            if (halves)
                line = "project #" + p.Id + " C4 Fresh Asphalt (Cover) per half" + (v.SwapActive ? (stage == 0 ? " C4a" : " C4b") : " (swap off)")
                       + ": left=" + m_HalfSet[ci, 0].ToString() + " right=" + m_HalfSet[ci, 1].ToString() + " U=" + RRWLog.F(v.U)
                       + " open=" + RoadZoneMath.Describe(v.OpenLanes & RoadZones.Carriageway) + " (centre strip with the half painted first)";
            else if (ps.HalvesShown)
                line = "project #" + p.Id + " C4 Fresh Asphalt (Cover) back to full width (" + (v.Phase != WorksPhase.Finishing ? "phase " + v.Phase
                       : !RRWGates.HalfCovers ? "gate halfcovers 0" : !v.Ctx.CarHalfAllowed ? "no car half allowed" : !v.Ctx.Staged ? "staged opening off" : "halves inactive") + ")";
            else line = null;
            ps.HalvesShown = halves;
            ps.HalvesStage = stage;
            if (line == null) return;
            SurfaceState.LastHalves = "u" + m_Now + " " + line;
            RRWLog.Info("surfaces: " + line);
        }

        // One Info line when a project's yellow temporary lines appear or disappear (with the reason).
        private void LogLines(ProjectRecord p, ProjSurf ps, in ProjectView v)
        {
            bool shown = m_FrameLineEdges > 0;
            if (shown == ps.LinesShown) return;
            string line;
            if (shown)
                line = "project #" + p.Id + " yellow temporary lines ON over " + m_FrameLineEdges + " edge(s): open=" + RoadZoneMath.Describe(v.OpenLanes & RoadZones.Carriageway)
                       + " rows=" + m_FrameLineRows + " areas=" + m_FrameLinePieces + " span=" + m_ProjSpan[(int)SurfaceLayer.TempMarking]
                       + " src=" + SurfaceState.NameOf(SurfaceState.Prefab(SurfaceLayer.TempMarking, 100)) + " w=" + RRWLog.F(RRWGates.TempLineWidth)
                       + " round=" + RRWLog.F(RRWGates.TempLineRoundness) + " queue=+" + RRWGates.TempLineQueueRaise + " theme=" + (RRWCity.NaTheme ? "NA" : "EU");
            else
                line = "project #" + p.Id + " yellow temporary lines OFF (" + (m_FrameLineWhy ?? "no edge left") + ")";
            ps.LinesShown = shown;
            ps.LinesWhy = shown ? "" : (m_FrameLineWhy ?? "");
            SurfaceState.LastLines = "u" + m_Now + " " + line;
            RRWLog.Info("surfaces: " + line);
        }
    }
}
