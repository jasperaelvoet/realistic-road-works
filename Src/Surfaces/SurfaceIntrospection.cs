using System.Collections.Generic;
using System.Text;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

// rrw.dump / rrw.check hooks of the Surfaces module (Core RRWIntrospection; compiled in every build).
namespace RealisticRoadWorks.V3.Surfaces
{
    internal static class SurfaceIntrospection
    {
        public static void Register()
        {
            RRWIntrospection.RegisterDumper("Surfaces", Dump);
            RRWIntrospection.RegisterChecker("Surfaces", Check);
        }

        static string F(float v) => RRWLog.F(v);

        public static string Dump(EntityManager em, Entity edge)
        {
            var sb = new StringBuilder();
            if (!SurfaceState.Edges.TryGetValue(edge, out var es))
            {
                sb.Append("surfaces: no tracked areas");
                return sb.ToString();
            }
            sb.Append("surfaces: ends=").Append(es.Ends.Start).Append('/').Append(es.Ends.End)
              .Append(" trims=").Append(F(es.Ends.TrimStart)).Append('/').Append(F(es.Ends.TrimEnd))
              .Append(" completed=").Append(es.Completed);
            if (es.SplitSiblings.Count > 0) sb.Append(" splitHold=").Append(es.SplitSiblings.Count);
            for (int l = 0; l < es.Areas.GetLength(0); l++)
                for (int b = 0; b < es.Areas.GetLength(1); b++)
                {
                    int n = es.PieceCount(l, b);
                    if (n > 1) sb.Append("\n  ").Append((SurfaceLayer)l).Append('[').Append(b).Append("] ").Append(n).Append(" pieces");
                    for (int k = 0; k < es.RowN[l, b]; k++)
                {
                    var t = es.Areas[l, b, k];
                    if (t == null) continue;
                    sb.Append("\n  ").Append((SurfaceLayer)l).Append('[').Append(b).Append(']');
                    if (n > 1 || k > 0) sb.Append('#').Append(k);
                    sb.Append(' ')
                      .Append(t.BandKind).Append(' ')
                      .Append(t.Pending ? "pending" : t.Live(em) ? RRWLog.E(t.Area) : "DEAD")
                      .Append(" s=[").Append(F(t.LS0)).Append(',').Append(F(t.LS1)).Append(']')
                      .Append(" geom=[").Append(F(t.GS0)).Append(',').Append(F(t.GS1)).Append(']')
                      .Append(" nodes=").Append(t.NodeCount)
                      .Append(" written=").Append(RRWClock.UpdateIndex - t.WriteUpdate).Append("u ago");
                    if (t.BandKind == SurfaceBand.WorksBand && !float.IsNaN(t.LatLo))
                        sb.Append(" lat=[").Append(F(t.LatLo)).Append(',').Append(F(t.LatHi)).Append(']');
                    if (t.DeferSince != 0) sb.Append(" deferred=").Append(RRWClock.UpdateIndex - t.DeferSince).Append('u');
                    if (t.ResnapDue != 0) sb.Append(" resnapDue=").Append((int)(t.ResnapDue - RRWClock.UpdateIndex));
                }
                }
            // Replaced pieces still on screen until their hand-over (deleted once covered for >= 1 update)
            for (int i = 0; i < es.Outgoing.Count; i++)
            {
                var t = es.Outgoing[i];
                sb.Append("\n  outgoing ").Append(t.Layer).Append('[').Append(t.Band).Append("] ").Append(t.BandKind).Append(' ')
                  .Append(t.Live(em) ? RRWLog.E(t.Area) : "DEAD")
                  .Append(" s=[").Append(F(t.LS0)).Append(',').Append(F(t.LS1)).Append(']')
                  .Append(" waiting=").Append(RRWClock.UpdateIndex - t.OutgoingSince).Append('u');
            }
            // Yellow temporary lines (own store)
            sb.Append("\n  TempLines open=").Append(RoadZoneMath.Describe(es.TempOpenHalf)).Append(" rows=").Append(es.TempRows.Count)
              .Append(" areas=").Append(es.TempPieceCount());
            if (es.TempReason.Length > 0) sb.Append(" none: ").Append(es.TempReason);
            if (SiteRegistry.TryGetEdge(edge, out var rec)) sb.Append(" applied=").Append(RoadZoneMath.Describe(rec.OpenLanesApplied));
            for (int i = 0; i < es.TempRows.Count; i++)
            {
                var row = es.TempRows[i];
                int live = 0, pend = 0;
                float lo = 1e9f, hi = -1e9f;
                foreach (var t in row.Pieces)
                {
                    if (t.Pending) pend++; else if (t.Live(em)) live++;
                    lo = math.min(lo, t.GS0); hi = math.max(hi, t.GS1);
                }
                sb.Append("\n    line ").Append(row.Line).Append(row.Dashed ? " dashed" : " solid")
                  .Append(" lat=[").Append(F(row.Left)).Append(',').Append(F(row.Right)).Append("] w=").Append(F(row.Right - row.Left))
                  .Append(" s=[").Append(F(lo)).Append(',').Append(F(hi)).Append("] areas=").Append(row.Pieces.Count)
                  .Append(" live=").Append(live).Append(" pending=").Append(pend);
                if (row.Pieces.Count > 0) sb.Append(" clone=\"").Append(SurfaceState.NameOf(row.Pieces[0].Prefab)).Append('"');
            }
            return sb.ToString();
        }

        public static string Summary(EntityManager em)
        {
            int strips = 0, live = 0, pending = 0, outgoing = 0, multi = 0;
            foreach (var es in SurfaceState.Edges.Values)
            {
                outgoing += es.Outgoing.Count;
                for (int l = 0; l < es.Areas.GetLength(0); l++)
                    for (int b = 0; b < es.Areas.GetLength(1); b++)
                    {
                        if (es.PieceCount(l, b) > 1) multi++;
                        for (int k = 0; k < es.RowN[l, b]; k++)
                    {
                        var t = es.Areas[l, b, k];
                        if (t == null) continue;
                        strips++;
                        if (t.Pending) pending++; else if (t.Live(em)) live++;
                    }
                    }
            }
            int capsCover = 0, capsAsph = 0;
            foreach (var c in SurfaceState.Caps.Values) { if (c.Cover != null) capsCover++; if (c.Asphalt != null) capsAsph++; }
            return "surfaces edges=" + SurfaceState.Edges.Count + " strips=" + strips + " live=" + live + " pending=" + pending
                   + " caps=" + SurfaceState.Caps.Count + "(cover " + capsCover + ", asphalt " + capsAsph + ")"
                   + " scars=" + SurfaceState.Scars.Count + " retiring=" + SurfaceState.Retiring.Count
                   + " | spawns=" + SurfaceState.Spawns + " rewrites=" + SurfaceState.Rewrites + " deletes=" + SurfaceState.Deletes
                   + " deferred=" + SurfaceState.Deferred + " bound=" + SurfaceState.Bound + " unmatched=" + SurfaceState.Unmatched
                   + " capSpawns=" + SurfaceState.CapSpawns + " scarSteps=" + SurfaceState.ScarSteps
                   + " scarsDropped=" + SurfaceState.ScarsDropped + " scarsRekeyed=" + SurfaceState.ScarsRekeyed
                   + " lastScarDrop=[" + SurfaceState.LastScarDrop + "]"
                   + " | completedEdges=" + SurfaceState.CompletedEdges + " roadLayersRemoved=" + SurfaceState.CompletedRoadLayers
                   + " vergeScars=" + SurfaceState.CompletedVergeScars + " vergeScarsEnded=" + SurfaceState.VergeScarsEnded
                   + " strayCuringRemoved=" + SurfaceState.StrayCuringRemoved + " tempMarking=" + (SurfacePalette.kTempMarkingOn ? "on" : "off")
                   + " | R3 bandSwitches=" + SurfaceState.BandSwitches + " lineRowWrites=" + SurfaceState.TempRowWrites
                   + " lineSpawns=" + SurfaceState.TempPieceSpawns + " lineRewrites=" + SurfaceState.TempPieceRewrites + " lineDeletes=" + SurfaceState.TempPieceDeletes
                   + " styleSyncs=" + SurfaceState.StyleSyncs + " devLines=" + SurfaceState.DevLines.Count
                   + " lastLines=[" + SurfaceState.LastLines + "] lastHalves=[" + SurfaceState.LastHalves + "] lastStyle=[" + SurfaceState.LastStyle + "]"
                   + " lastCompletion=[" + SurfaceState.LastCompletion + "]"
                   + " | R4 multiPieceRows=" + multi + " outgoing=" + outgoing + " maxPieces=" + SurfaceState.MaxPiecesSeen
                   + " topologyChanges=" + SurfaceState.TopologyChanges + " keptInPlace=" + SurfaceState.TopologyKept
                   + " outgoingDeleted=" + SurfaceState.OutgoingDeleted + " outgoingTimeouts=" + SurfaceState.OutgoingTimeouts
                   + " bypassDeferred=" + SurfaceState.BypassDeferred
                   + " lastTopology=[" + SurfaceState.LastTopology + "] lastCrews=[" + SurfaceState.LastCrews + "]"
                   + " | upgrade polygons=" + SurfaceState.UpgradePolygons + " waits=" + SurfaceState.UpgradeWaits
                   + " scarsClipped=" + SurfaceState.ScarsClipped + " scarsKeptOutside=" + SurfaceState.ScarsKeptOutside
                   + " scarsInsideBands=" + SurfaceState.ScarsInsideBands + " lastUpgrade=[" + SurfaceState.LastUpgrade + "]"
                   + " lastScarClip=[" + SurfaceState.LastScarClip + "]"
                   + " | orphansRemoved=" + SurfaceState.OrphansRemoved + " lastOrphan=[" + SurfaceState.LastOrphan + "]"
                   + " | clones=" + SurfaceState.Clones.Count + " (" + SurfaceState.RegistrationInfo + ")"
                   + (SurfaceState.TagFaulted ? " TAG-FAULTED" : "");
        }

        // rrw.check: every RRW area has LivePath + RRWDerived; tracked areas exist; per edge, the cover layers leave no
        // longitudinal hole > 0.05 m where the phase plan says the edge is covered.
        public static void Check(EntityManager em, List<string> problems)
        {
            if (SurfaceState.CloneEntities.Count == 0) return;
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Area>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            var arr = q.ToEntityArray(Allocator.Temp);
            int noLive = 0, noDerived = 0, untracked = 0;
            string firstUntracked = null;
            var tracked = s_Tracked;
            SurfaceAreaSystem.CollectTracked(tracked);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var prefab = em.GetComponentData<PrefabRef>(arr[i]).m_Prefab;
                    if (!SurfaceState.IsOurPrefab(prefab)) continue;
                    if (!em.HasComponent<LivePath>(arr[i])) noLive++;
                    if (!em.HasComponent<RRWDerived>(arr[i])) noDerived++;
                    if (!tracked.Contains(arr[i]) && !em.HasComponent<Created>(arr[i]))
                    {
                        untracked++;
                        if (firstUntracked == null) firstUntracked = RRWLog.E(arr[i]) + " " + SurfaceAreaSystem.DescribeArea(em, arr[i], prefab);
                    }
                }
            }
            finally { arr.Dispose(); tracked.Clear(); }
            if (noLive > 0) problems.Add("surfaces: " + noLive + " RRW area(s) without LivePath (would be saved)");
            if (noDerived > 0) problems.Add("surfaces: " + noDerived + " RRW area(s) without RRWDerived");
            if (untracked > 0) problems.Add("surfaces: " + untracked + " work-site texture area(s) on screen that nothing tracks (the self-heal sweep removes them), first: " + firstUntracked);
            CheckScars(em, problems);
            CheckRound2(em, problems);
            CheckUpgrade(em, problems);

            foreach (var es in SurfaceState.Edges.Values)
            {
                if (es.Completed) continue;
                if (!SiteRegistry.TryGetEdge(es.Edge, out var rec) || rec.Arc == null) continue;
                if (!SiteRegistry.TryGetProject(rec.ProjectId, out var p) || p.Mode != VisualMode.FullDig) continue;
                if (!em.HasComponent<RoadWorksSite>(es.Edge) || em.GetComponentData<RoadWorksSite>(es.Edge).Mode != VisualMode.FullDig) continue;
                for (int l = 0; l < es.Areas.GetLength(0); l++)
                    for (int b = 0; b < es.Areas.GetLength(1); b++)
                    {
                        float prev1 = float.MinValue;
                        for (int k = 0; k < es.RowN[l, b]; k++)
                        {
                            var t = es.Areas[l, b, k];
                            if (t == null) continue;
                            if (!t.Pending && !t.Live(em))
                                problems.Add("surfaces: edge " + RRWLog.E(es.Edge) + " " + (SurfaceLayer)l + "[" + b + "]#" + k + " tracked area is gone");
                            // Pieces of one row never overlap (a translucent layer would double)
                            if (t.LS0 < prev1 - 0.05f)
                                problems.Add("surfaces: R4 edge " + RRWLog.E(es.Edge) + " " + (SurfaceLayer)l + "[" + b + "] pieces overlap: #" + (k - 1)
                                             + " ends at " + F(prev1) + ", #" + k + " starts at " + F(t.LS0));
                            prev1 = math.max(prev1, t.LS1);
                        }
                    }
                for (int i = 0; i < es.Outgoing.Count; i++)
                {
                    var t = es.Outgoing[i];
                    if (RRWClock.UpdateIndex - t.OutgoingSince > (uint)(SurfaceAreaSystem.kDeferTimeout + 10))
                        problems.Add("surfaces: R4 edge " + RRWLog.E(es.Edge) + " replaced " + t.Layer + " piece [" + F(t.LS0) + "," + F(t.LS1) + "] still on screen after "
                                     + (RRWClock.UpdateIndex - t.OutgoingSince) + " updates");
                }
                float L = rec.Arc.Length;
                var v = p.View();
                // which pair must cover the whole edge in this phase (after the per-project write throttle settles)
                SurfaceLayer a, c2;
                bool need = true;
                switch (p.Phase)
                {
                    case WorksPhase.Paving: a = SurfaceLayer.BaseCourseCover; c2 = SurfaceLayer.FreshAsphaltCover; break;
                    // In C4 FreshAsphalt shrinks with the Cover (behind the painter the road is vanilla): no
                    // whole-edge coverage to check; CheckRound2 checks that no fresh asphalt lies behind the cover instead
                    case WorksPhase.Finishing: a = c2 = SurfaceLayer.FreshAsphalt; need = false; break;
                    case WorksPhase.Removal: a = SurfaceLayer.BaseCourseCover; c2 = SurfaceLayer.Subgrade; break;
                    case WorksPhase.Restore: a = SurfaceLayer.Subgrade; c2 = SurfaceLayer.TopsoilStrip; need = RRWSettings.Current != null && RRWSettings.Current.TopsoilOn && !v.Cancelled; break;
                    default: a = c2 = SurfaceLayer.Subgrade; need = false; break;
                }
                if (!need) continue;
                // Both layers are piece sets (crew sections); what is on screen (current + replaced pieces) must cover
                // [0, L], sampled every 0.25 m (no gap at section boundaries)
                HoleCheck(em, es, p, L, a, c2, problems);
                if (p.Phase == WorksPhase.Paving) HoleCheck(em, es, p, L, SurfaceLayer.BaseCourseCover, SurfaceLayer.FreshAsphalt, problems);
            }
        }

        // Completion and staged-traffic checks - every line must be 0 in game:
        //   - a construction scar that is not verge soil (= a curing fresh-asphalt / cover layer after completion);
        //   - a construction verge scar older than kVergeEndHours (+ 0.1 h slack for the 8-steps-per-update cap);
        //   - a completed edge that still tracks an area (frame-N cleanup missed one);
        //   - a junction cap with fresh asphalt but no cover (the removed "curing cap" stage);
        //   - C4: fresh asphalt more than 2.5 m behind the Fresh Asphalt Cover (dark road behind the painter; per half in a staged C4);
        //   staged traffic:
        //   - a yellow line on an edge whose open half is not APPLIED open (EdgeRecord.OpenLanesApplied), outside C4, or while
        //     the TempMarkings setting is off;
        //   - a staged C4 half without its Fresh Asphalt Cover where its painter has not passed yet (white markings under traffic).
        public static void CheckRound2(EntityManager em, List<string> problems)
        {
            uint now = RRWClock.UpdateIndex;
            foreach (var sc in SurfaceState.Scars)
            {
                if (!sc.Construction || sc.Area == null) continue;
                float h = (float)(sc.AgeFrames / RRWConst.kFramesPerHour);
                if (!SurfaceAreaSystem.IsVergeLayer(sc.Area.Layer))
                    problems.Add("surfaces: U5 curing layer " + sc.Area.Layer + " still on finished road (project #" + sc.Area.ProjectId + ", age " + F(h) + " h)");
                else if (h > RRWConst.kVergeEndHours + 0.1f)
                    problems.Add("surfaces: U5 verge scar " + sc.Area.Layer + " a" + sc.Area.FadePct + " older than " + F(RRWConst.kVergeEndHours) + " h (" + F(h) + " h, project #" + sc.Area.ProjectId + ")");
            }
            foreach (var es in SurfaceState.Edges.Values)
            {
                if (es.Completed)
                {
                    int n = es.CountTracked();
                    if (n > 0) problems.Add("surfaces: U5 completed edge " + RRWLog.E(es.Edge) + " still tracks " + n + " area(s)");
                    continue;
                }
                if (!SiteRegistry.TryGetProject(es.ProjectId, out var p)) continue;
                CheckTempLines(em, es, p, now, problems);
                CheckHalfCovers(em, es, p, now, problems);
                if (p.Kind == WorksKind.Construction && p.Phase == WorksPhase.Finishing)
                {
                    // Every live fresh-asphalt piece must lie inside one live cover piece (2.5 m slack = one throttled
                    // write); per half when the asphalt uses the per-half bands (each half has its own painter fronts)
                    int fa = (int)SurfaceLayer.FreshAsphalt, fc = (int)SurfaceLayer.FreshAsphaltCover;
                    for (int b = 0; b < es.Areas.GetLength(1); b++)
                        for (int k = 0; k < es.RowN[fa, b]; k++)
                        {
                            var ta = es.Areas[fa, b, k];
                            if (ta == null || !ta.Live(em)) continue;
                            bool half = ta.BandKind == SurfaceBand.CarriageHalf;
                            if (InsideCover(em, es, fc, half ? b : -1, half, ta.LS0, ta.LS1)) continue;
                            problems.Add("surfaces: U5 fresh asphalt " + (half ? HalfName(b) + " half " : "") + "piece [" + F(ta.LS0) + "," + F(ta.LS1)
                                         + "] not under its cover on C4 edge " + RRWLog.E(es.Edge) + " (dark road behind the painter)");
                        }
                }
            }
            foreach (var cap in SurfaceState.Caps.Values)
                if (cap.Asphalt != null && !cap.Asphalt.Pending && cap.Asphalt.Live(em) && (cap.Cover == null || (!cap.Cover.Pending && !cap.Cover.Live(em))))
                    problems.Add("surfaces: U5 fresh asphalt without cover on junction node " + RRWLog.E(cap.Node));
        }

        static string HalfName(int b) => b == 0 ? "left" : "right";

        static bool Remarks(EdgeRecord rec)
        {
            var ue = rec.Upgrade;
            if (ue == null) return false;
            for (int i = 0; i < ue.BandCount; i++) if (ue.Bands[i].Kind == BandKind.Remark) return true;
            return false;
        }

        // Yellow lines exist only while their open half is applied open on that edge, in C4, with the setting on.
        static void CheckTempLines(EntityManager em, EdgeSurf es, ProjectRecord p, uint now, List<string> problems)
        {
            int live = 0;
            for (int i = 0; i < es.TempRows.Count; i++)
                foreach (var t in es.TempRows[i].Pieces)
                    if (!t.Pending && t.Live(em) && t.WriteUpdate < now) live++;
            if (live == 0) return;
            string where = "edge " + RRWLog.E(es.Edge) + " (project #" + p.Id + " " + p.Phase + ", " + live + " line area(s))";
            // upgrade works mark the lanes kept open beside the works in every window before the re-marking
            if (p.Mode == VisualMode.HalfWidth && es.TempOpenHalf == RoadZones.None)
            {
                var stu = RRWSettings.Current;
                if (stu == null || !stu.TempMarkingsOn) problems.Add("surfaces: yellow line while the TempMarkings setting is off on " + where);
                return;
            }
            if (p.Kind != WorksKind.Construction || p.Phase != WorksPhase.Finishing)
            {
                problems.Add("surfaces: R3 yellow line outside C4 on " + where);
                return;
            }
            var st = RRWSettings.Current;
            if (st == null || !st.TempMarkingsOn) problems.Add("surfaces: R3 yellow line while the TempMarkings setting is off on " + where);
            var open = es.TempOpenHalf;
            if (!SiteRegistry.TryGetEdge(es.Edge, out var rec)) return;
            if ((open != RoadZones.LeftHalf && open != RoadZones.RightHalf) || (rec.OpenLanesApplied & open) != open)
                problems.Add("surfaces: R3 yellow line on " + where + " whose open half " + RoadZoneMath.Describe(open)
                             + " is not applied open (applied " + RoadZoneMath.Describe(rec.OpenLanesApplied) + ", decided " + RoadZoneMath.Describe(p.OpenLanes) + ")");
        }

        // In a staged C4 every half keeps its Fresh Asphalt Cover until its painter has passed (Core SurfaceSpanHalf):
        // a missing / short cover shows white markings under traffic where nobody painted. Grace: 30 updates after the phase
        // change, a pending area, or 2.5 m (one throttled write).
        static void CheckHalfCovers(EntityManager em, EdgeSurf es, ProjectRecord p, uint now, List<string> problems)
        {
            if (p.Kind != WorksKind.Construction || p.Phase != WorksPhase.Finishing) return;
            var v = p.View();
            if (PhasePlan.BandOf(SurfaceLayer.FreshAsphaltCover, v) != SurfaceBand.CarriageHalf) return;
            if (SurfaceState.Prefab(SurfaceLayer.FreshAsphaltCover, 100) == Entity.Null) return;
            if (!SurfaceState.Projects.TryGetValue(p.Id, out var ps) || now - ps.PhaseSince < 30u) return;
            if (!em.HasComponent<RoadWorksSite>(es.Edge) || !SiteRegistry.TryGetEdge(es.Edge, out var rec) || rec.Arc == null) return;
            if (v.IsUpgrade && !Remarks(rec)) return;   // upgrade works: only edges with a re-marking band get the half covers
            var site = em.GetComponentData<RoadWorksSite>(es.Edge);
            int fc = (int)SurfaceLayer.FreshAsphaltCover;
            for (int b = 0; b < 2; b++)
            {
                bool pending = false;
                for (int k = 0; k < es.RowN[fc, b]; k++) if (es.Areas[fc, b, k] != null && es.Areas[fc, b, k].Pending) pending = true;
                if (pending) continue;
                // Every piece the half still needs (per crew section) must be under a live half cover piece
                PhasePlan.SurfaceSpansHalf(SurfaceLayer.FreshAsphaltCover, v, b == 0 ? RoadZones.LeftHalf : RoadZones.RightHalf, out SpanSet set);
                for (int i = 0; i < set.Count; i++)
                {
                    if (!PhasePlan.ToEdgeLocal(set[i], site.m_ChainU0, site.m_ChainU1, rec.Arc.Length, out float s0, out float s1)) continue;
                    if (InsideCover(em, es, fc, b, true, s0 + 2.5f, s1 - 2.5f)) continue;
                    problems.Add("surfaces: R3 C4 " + HalfName(b) + " half cover missing / short over [" + F(s0) + "," + F(s1) + "] on edge " + RRWLog.E(es.Edge)
                                 + " (project #" + p.Id + ", painter has not passed): white markings would show under traffic");
                }
            }
        }

        // No curing / scar decal may lie on a deleted road, on a road under construction, or on a demolition
        // whose road is hidden (D1/D2) - there it draws over the trench / subgrade. Demolition D0 is the hand-over window
        // (the scar waits under the growing Base Course Cover), so it is reported only once it waited > 600 updates.
        public static void CheckScars(EntityManager em, List<string> problems)
        {
            foreach (var sc in SurfaceState.Scars)
            {
                string what = sc.Area != null ? sc.Area.Layer + " a" + sc.Area.FadePct : "?";
                foreach (var a in sc.Anchors)
                {
                    if (!EcsUtil.Alive(em, a.Entity)) { problems.Add("surfaces: scar " + what + " on deleted " + (a.IsNode ? "node " : "road ") + RRWLog.E(a.Entity)); continue; }
                    if (!a.IsNode) CheckOver(em, sc, a.Entity, what, "on its road", problems);
                }
                if (sc.Anchored && sc.Anchors.Count == 0) problems.Add("surfaces: scar " + what + " lost every anchor but still exists");
                foreach (var e in sc.Conflicts) CheckOver(em, sc, e, what, "overlapping", problems);
            }
        }

        static void CheckOver(EntityManager em, ScarEntry sc, Entity edge, string what, string rel, List<string> problems)
        {
            // upgrade works keep the road in use: a scar may stay beside the bands, never inside them
            if (SurfaceAreaSystem.UpgradeScarApplies(em, edge, sc))
            {
                if (sc.Area != null && !sc.Area.Pending && SurfaceAreaSystem.ScarInsideUpgradeBands(em, sc.PolyXZ, sc.Bounds, edge, kUpgradeBleed))
                    problems.Add("surfaces: curing/scar " + what + " " + rel + " lies inside the upgrade works bands of edge " + RRWLog.E(edge)
                                 + " (it must be clipped to the outside)");
                return;
            }
            if (!SiteRegistry.TryGetEdge(edge, out var rec) || !SiteRegistry.TryGetProject(rec.ProjectId, out var p)) return;
            if (p.Phase == WorksPhase.Complete) return;
            if (em.HasComponent<RoadWorksRuntime>(edge) && em.GetComponentData<RoadWorksRuntime>(edge).Has(RuntimeFlags.Completing)) return;
            bool hidden = em.HasComponent<Hidden>(edge) || p.Phase == WorksPhase.Removal || p.Phase == WorksPhase.Restore;
            if (p.Kind != WorksKind.Demolition)
                problems.Add("surfaces: curing/scar " + what + " " + rel + " construction edge " + RRWLog.E(edge) + " (" + p.Phase + ")");
            else if (hidden)
                problems.Add("surfaces: curing/scar " + what + " " + rel + " hidden demolition edge " + RRWLog.E(edge) + " (" + p.Phase + ") - draws over the trench");
            else if (sc.WaitSince != 0 && RRWClock.UpdateIndex - sc.WaitSince > 600)
                problems.Add("surfaces: curing/scar " + what + " " + rel + " demolition edge " + RRWLog.E(edge) + " still waiting for the base-cover hand-over after " + (RRWClock.UpdateIndex - sc.WaitSince) + " updates");
        }

        const float kUpgradeBleed = 0.3f;   // a polygon may reach this far into an open lane (decal edge, rounding)

        // Upgrade works checks - every line must be 0 in game:
        //   - dirt or gravel (road dirt, subgrade, topsoil, base course) on a sub-strip that carries cars (no exemption: cars on a
        //     sub-strip get Fresh Asphalt Cover only);
        //   - a work-site polygon other than Fresh Asphalt Cover reaching more than kUpgradeBleed into a lane that carries cars
        //     (the per-half re-marking asphalt of the one-direction primitive is the new-road pattern verified in game and not checked here);
        //   - a tracked area that is gone.
        // Edges whose sub-strips do not belong to the current geometry and tail are skipped (nothing is redrawn for them then).
        static readonly HashSet<Entity> s_Tracked = new HashSet<Entity>();

        public static void CheckUpgrade(EntityManager em, List<string> problems)
        {
            foreach (var es in SurfaceState.Edges.Values)
            {
                CheckUpgradeEnds(em, es, problems);
                if (es.Completed) continue;
                if (!SiteRegistry.TryGetEdge(es.Edge, out var rec) || rec.Arc == null || rec.Upgrade == null) continue;
                if (!SiteRegistry.TryGetProject(rec.ProjectId, out var p) || p.Phase == WorksPhase.Complete) continue;
                if (!SurfaceAreaSystem.IsUpgradeSiteEdge(em, es.Edge, out _)) continue;
                var v = p.View();
                if (!v.IsUpgrade) continue;
                var ue = rec.Upgrade;
                if (ue.SubStripsRevision != rec.GeometryRevision || ue.SubStripsTail != ue.TailRevision) continue;
                int dirt = 0, bleed = 0;
                string firstDirt = null, firstBleed = null;
                for (int l = 0; l < es.Areas.GetLength(0); l++)
                    for (int b = 0; b < es.Slots; b++)
                        for (int k = 0; k < es.RowN[l, b]; k++)
                        {
                            var t = es.Areas[l, b, k];
                            if (t == null || t.Pending) continue;
                            var layer = (SurfaceLayer)l;
                            if (!t.Live(em))
                            {
                                problems.Add("surfaces: upgrade edge " + RRWLog.E(es.Edge) + " " + layer + "[" + b + "]#" + k + " tracked area is gone");
                                continue;
                            }
                            if (t.BandKind != SurfaceBand.WorksBand) continue;
                            SurfaceAreaSystem.SlotParts(b, out int bi, out int si);
                            if (SurfaceAreaSystem.IsDirtOrGravel(layer) && bi < ue.BandCount && si < ue.SubStrips[bi].Count)
                            {
                                var s = ue.SubStrips[bi][si];
                                if (SurfaceAreaSystem.CarriesCars(rec, v, s, bi))
                                {
                                    dirt++;
                                    if (firstDirt == null)
                                        firstDirt = layer + " on " + s.Kind + " [" + F(s.Lo) + "," + F(s.Hi) + "] of band " + bi + " s=[" + F(t.LS0) + "," + F(t.LS1) + "]";
                                }
                            }
                            if (layer == SurfaceLayer.FreshAsphaltCover || float.IsNaN(t.LatLo) || float.IsNaN(t.LatHi)) continue;
                            for (int j = 0; j < ue.CrossSection.Count; j++)
                            {
                                var cs = ue.CrossSection[j];
                                float ov = math.min(t.LatHi, cs.Hi) - math.max(t.LatLo, cs.Lo);
                                if (ov <= kUpgradeBleed || !SurfaceAreaSystem.CarriesCars(rec, v, cs, -1)) continue;
                                bleed++;
                                if (firstBleed == null)
                                    firstBleed = layer + " [" + F(t.LatLo) + "," + F(t.LatHi) + "] " + F(ov) + " m into the open " + cs.Kind + " [" + F(cs.Lo) + "," + F(cs.Hi) + "]";
                                break;
                            }
                        }
                if (dirt > 0)
                    problems.Add("surfaces: upgrade edge " + RRWLog.E(es.Edge) + " (project #" + p.Id + ") " + dirt
                                 + " dirt/gravel area(s) on sub-strips that carry cars, first: " + firstDirt);
                if (bleed > 0)
                    problems.Add("surfaces: upgrade edge " + RRWLog.E(es.Edge) + " (project #" + p.Id + ") " + bleed
                                 + " work-site area(s) more than " + F(kUpgradeBleed) + " m into an open lane, first: " + firstBleed);
            }
        }

        // Upgrade works never draw on a node: every live piece of an upgrade edge (strips, replaced pieces, yellow lines) lies inside
        // the edge's trimmed range [TrimStart, L - TrimEnd], and a dead end is clipped there (no round cap over the cul-de-sac).
        static void CheckUpgradeEnds(EntityManager em, EdgeSurf es, List<string> problems)
        {
            if (es.Completed || !es.EndsValid || !SurfaceAreaSystem.IsUpgradeSiteEdge(em, es.Edge, out _)) return;
            if (!SiteRegistry.TryGetEdge(es.Edge, out var rec) || rec.Arc == null) return;
            if (!SiteRegistry.TryGetProject(rec.ProjectId, out var p) || p.Phase == WorksPhase.Complete) return;
            const float tol = 0.1f;
            float lo = es.Ends.TrimStart - tol, hi = rec.Arc.Length - es.Ends.TrimEnd + tol;
            if (es.Ends.Start == EndKind.DeadEnd || es.Ends.End == EndKind.DeadEnd)
                problems.Add("surfaces: upgrade edge " + RRWLog.E(es.Edge) + " (project #" + p.Id + ") classified with a round dead-end cap (" + es.Ends.Start + "/" + es.Ends.End
                             + "): its work-site textures reach onto the cul-de-sac");
            int n = 0;
            string first = null;
            void Test(TrackedArea t, string what)
            {
                if (t == null || t.Pending || !t.Live(em)) return;
                if (t.GS0 >= lo && t.GS1 <= hi) return;
                n++;
                if (first == null) first = what + " s=[" + F(t.GS0) + "," + F(t.GS1) + "] outside [" + F(lo + tol) + "," + F(hi - tol) + "]";
            }
            for (int l = 0; l < es.Areas.GetLength(0); l++)
                for (int b = 0; b < es.Areas.GetLength(1); b++)
                    for (int k = 0; k < es.RowN[l, b]; k++)
                        Test(es.Areas[l, b, k], (SurfaceLayer)l + "[" + b + "]#" + k);
            for (int i = 0; i < es.Outgoing.Count; i++) Test(es.Outgoing[i], "replaced " + es.Outgoing[i].Layer + "[" + es.Outgoing[i].Band + "]");
            for (int i = 0; i < es.TempRows.Count; i++)
                foreach (var t in es.TempRows[i].Pieces) Test(t, "yellow line " + es.TempRows[i].Line);
            if (n > 0)
                problems.Add("surfaces: upgrade edge " + RRWLog.E(es.Edge) + " (project #" + p.Id + ") " + n
                             + " work-site area(s) drawn past the road end onto a node, first: " + first);
        }

        // Is [s0, s1] inside ONE live piece of `layer` (band `band`, or any band when band < 0; per-half kind when half)?
        static bool InsideCover(EntityManager em, EdgeSurf es, int layer, int band, bool half, float s0, float s1)
        {
            if (s1 - s0 <= 0.05f) return true;
            for (int b = 0; b < es.Areas.GetLength(1); b++)
            {
                if (band >= 0 && b != band) continue;
                for (int k = 0; k < es.RowN[layer, b]; k++)
                {
                    var t = es.Areas[layer, b, k];
                    if (t == null || !t.Live(em) || (half && t.BandKind != SurfaceBand.CarriageHalf)) continue;
                    if (t.LS0 <= s0 + 2.5f && t.LS1 >= s1 - 2.5f) return true;
                }
            }
            return false;
        }

        // On-screen coverage of a layer on an edge (current + replaced pieces, live): per band the union of its pieces,
        // intersected over the bands that have any piece. Empty when the layer has none.
        static void Coverage(EntityManager em, EdgeSurf es, SurfaceLayer layer, ref SpanSet result)
        {
            result.Clear();
            int l = (int)layer;
            bool first = true;
            for (int b = 0; b < es.Areas.GetLength(1); b++)
            {
                SpanSet band = default;
                for (int k = 0; k < es.RowN[l, b]; k++)
                {
                    var t = es.Areas[l, b, k];
                    if (t != null && t.Live(em)) band.Add(new Span(t.LS0, t.LS1));
                }
                for (int i = 0; i < es.Outgoing.Count; i++)
                {
                    var t = es.Outgoing[i];
                    if (t.Layer == layer && t.Band == b && t.Live(em)) band.Add(new Span(t.LS0, t.LS1));
                }
                if (band.IsEmpty) continue;
                if (first) { result = band; first = false; }
                else { SpanSet.Intersect(result, band, out SpanSet r); result = r; }
            }
        }

        // The union of two layers' on-screen pieces must cover [0, L]; sampled every 0.25 m.
        static void HoleCheck(EntityManager em, EdgeSurf es, ProjectRecord p, float L, SurfaceLayer a, SurfaceLayer c2, List<string> problems)
        {
            SpanSet ca = default, cb = default;
            Coverage(em, es, a, ref ca);
            Coverage(em, es, c2, ref cb);
            float holeStart = -1f, holeEnd = -1f;
            int holes = 0;
            for (float s = 0.05f; s <= L - 0.05f + 1e-4f; s += 0.25f)
            {
                bool covered = ca.Contains(s) || cb.Contains(s);
                if (covered) continue;
                if (holes == 0) holeStart = s;
                holeEnd = s;
                holes++;
            }
            float last = L - 0.05f;
            if (!(ca.Contains(last) || cb.Contains(last))) { if (holes == 0) holeStart = last; holeEnd = last; holes++; }
            if (holes > 0)
                problems.Add("surfaces: edge " + RRWLog.E(es.Edge) + " " + p.Phase + " hole ~[" + F(holeStart) + "," + F(holeEnd) + "] (" + holes + " sample(s) of 0.25 m; "
                             + a + "=" + ca.ToString() + " + " + c2 + "=" + cb.ToString() + ", L=" + F(L) + ")");
        }
    }
}
