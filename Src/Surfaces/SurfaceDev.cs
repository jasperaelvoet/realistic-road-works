#if DEVTOOLS
using System.Collections.Generic;
using System.Globalization;
using RealisticRoadWorks.Dev;
using Unity.Entities;

// Surfaces dev commands (DEVTOOLS builds only). They only flip module flags or read
// state: every structural change happens in SurfaceAreaSystem at Modification1.
namespace RealisticRoadWorks.V3.Surfaces
{
    internal static class SurfaceDevUtil
    {
        public static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public sealed class SurfAgriCommand : IDevCommand
    {
        public string Name => "rrw.surf.agri";
        public string Help => "rrw.surf.agri <0|1> - topsoil from the dev-only 'RRW Topsoil Agri' clone (Agriculture Surface 01) instead of the Ore recipe";
        public void Run(DevContext ctx, string[] a)
        {
            bool on = a.Length == 0 || a[0] != "0";
            if (on && !SurfaceState.Clones.ContainsKey(PrefabNames.TopsoilAgriDev)) { ctx.Log("rrw surf agri: clone not registered"); return; }
            SurfaceState.DevAgri = on;
            ctx.Log("rrw surf agri=" + (on ? 1 : 0) + " (topsoil strips are replaced on the next frame, spawn-first)");
        }
    }

    public sealed class SurfFadeCommand : IDevCommand
    {
        public string Name => "rrw.surf.fade";
        public string Help => "rrw.surf.fade <spawn|swap> - scar alpha steps: spawn the next variant first (default) or swap PrefabRef in place (experimental)";
        public void Run(DevContext ctx, string[] a)
        {
            if (a.Length > 0) SurfaceState.FadeBySwap = a[0] == "swap";
            ctx.Log("rrw surf fade=" + (SurfaceState.FadeBySwap ? "swap" : "spawn"));
        }
    }

    public sealed class SurfAgeCommand : IDevCommand
    {
        public string Name => "rrw.surf.age";
        public string Help => "rrw.surf.age <hours> - age every scar / curing area by this many in-game hours (next frame)";
        public void Run(DevContext ctx, string[] a)
        {
            float h = a.Length > 0 ? DevContext.F(a[0]) : 6f;
            SurfaceState.DevScarAgeBonus += h;
            ctx.Log("rrw surf age +" + SurfaceDevUtil.F(h) + "h for " + SurfaceState.Scars.Count + " scars");
        }
    }

    public sealed class SurfRebuildCommand : IDevCommand
    {
        public string Name => "rrw.surf.rebuild";
        public string Help => "rrw.surf.rebuild - replace every work-site strip and node cap once (new polygons, old ones retired after 1 update)";
        public void Run(DevContext ctx, string[] a)
        {
            SurfaceState.DevRebuildAll = true;
            ctx.Log("rrw surf rebuild queued");
        }
    }

    public sealed class SurfListCommand : IDevCommand
    {
        public string Name => "rrw.surf.list";
        public string Help => "rrw.surf.list [edges] - Surfaces summary, cover queues, per-project layer spans (edges: per-edge dump)";
        static string HalfPieces(in ProjectView v, RoadZones half)
        {
            PhasePlan.SurfaceSpansHalf(SurfaceLayer.FreshAsphaltCover, v, half, out SpanSet set);
            return set.ToString();
        }

        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            ctx.Log("rrw " + SurfaceIntrospection.Summary(em));
            ctx.Log("rrw surf queues base=" + SurfaceMaterialSystem.CurrentQueue(ctx.World, PrefabNames.BaseCourseCover)
                    + " asphaltCover=" + SurfaceMaterialSystem.CurrentQueue(ctx.World, PrefabNames.FreshAsphaltCover)
                    + " tempMarking=" + SurfaceMaterialSystem.CurrentQueue(ctx.World, PrefabNames.TempMarking)
                    + (SurfacePalette.kTempMarkingOn ? "" : "(off)")
                    + " agri=" + (SurfaceState.DevAgri ? 1 : 0) + " fade=" + (SurfaceState.FadeBySwap ? "swap" : "spawn"));
            // The yellow-line clones as the renderer sees them, the experimental switch values and the setting
            var setting = RRWSettings.Current;
            ctx.Log("rrw surf lines in use=\"" + SurfaceState.NameOf(SurfaceState.Prefab(SurfaceLayer.TempMarking, 100)) + "\" setting TempMarkings="
                    + (setting != null && setting.TempMarkingsOn) + " theme=" + (RRWCity.NaTheme ? "NA" : "EU") + (RRWCity.Known ? "" : "(city unknown)")
                    + " gates: lineSrc=" + RRWGates.TempLineSource + " lineW=" + SurfaceDevUtil.F(RRWGates.TempLineWidth) + " lineRound=" + SurfaceDevUtil.F(RRWGates.TempLineRoundness)
                    + " lineQueue=+" + RRWGates.TempLineQueueRaise + " lineLod=" + RRWGates.TempLineLodBias + " halfcovers=" + RRWGates.HalfCovers + " rev=" + RRWGates.Revision);
            foreach (var vn in PrefabNames.TempMarkingVariants)
                ctx.Log("rrw surf   \"" + vn + "\" " + SurfaceMaterialSystem.Describe(ctx.World, vn));
            bool edges = a.Length > 0 && a[0] == "edges";
            var ids = new List<uint>(SiteRegistry.Projects.Keys);
            ids.Sort();
            foreach (var id in ids)
            {
                var p = SiteRegistry.Projects[id];
                var v = p.View();
                // A layer is a set of per-crew-section pieces (SpanSet.ToString = "[a,b][c,d]...")
                string spans = "";
                for (int l = 0; l < (int)SurfaceLayer.Count; l++)
                {
                    PhasePlan.SurfaceSpans((SurfaceLayer)l, v, out SpanSet set);
                    spans += " " + (SurfaceLayer)l + "=" + set.ToString();
                }
                ctx.Log("rrw surf p" + id + " " + p.Kind + " mode=" + p.Mode + " phase=" + p.Phase + " f=" + SurfaceDevUtil.F(p.PhaseFraction)
                        + " U=" + SurfaceDevUtil.F(p.ChainLength) + " crews=" + v.CrewCount + (v.Cancelled ? " cancelCrews=" + v.CancelCrewCount : "")
                        + " open=" + p.OpenLanes + spans);
                int multi = 0, outgoing = 0, maxPieces = 0;
                foreach (var e in p.Edges)
                    if (SurfaceState.Edges.TryGetValue(e, out var es2))
                    {
                        outgoing += es2.Outgoing.Count;
                        for (int l = 0; l < (int)SurfaceLayer.Count; l++)
                            for (int b = 0; b < EdgeSection.kMaxIntervals; b++)
                            {
                                int n = es2.PieceCount(l, b);
                                if (n > 1) multi++;
                                if (n > maxPieces) maxPieces = n;
                            }
                    }
                ctx.Log("rrw surf p" + id + " pieces: rows with >1 piece=" + multi + " max pieces/row=" + maxPieces + " outgoing=" + outgoing
                        + (SurfaceState.Projects.TryGetValue(id, out var psd) && psd.BypassWaitSince != 0 ? " bypassDeferredFor=" + (RRWClock.UpdateIndex - psd.BypassWaitSince) + "u" : ""));
                // C4 per-half covers and yellow lines of this project
                var fc = PhasePlan.BandOf(SurfaceLayer.FreshAsphaltCover, v);
                int lineEdges = 0, lineAreas = 0;
                string none = "";
                foreach (var e in p.Edges)
                    if (SurfaceState.Edges.TryGetValue(e, out var es))
                    {
                        if (es.TempRows.Count > 0) { lineEdges++; lineAreas += es.TempPieceCount(); }
                        else if (none.Length == 0 && es.TempReason.Length > 0) none = es.TempReason;
                    }
                ctx.Log("rrw surf p" + id + " staged: halves=" + v.HalvesActive + " swap=" + v.SwapActive + " stage=" + (v.HalvesActive ? PhasePlan.StageIndexOf(v).ToString() : "-")
                        + " switch=" + p.Switch + " coverBand=" + fc
                        + (fc == SurfaceBand.CarriageHalf ? " coverLeft=" + HalfPieces(v, RoadZones.LeftHalf) + " coverRight=" + HalfPieces(v, RoadZones.RightHalf) : "")
                        + " tape=" + SurfaceDevUtil.F(PhasePlan.TapeFront(v)) + " lines: edges=" + lineEdges + "/" + p.Edges.Count + " areas=" + lineAreas
                        + (lineEdges < p.Edges.Count && none.Length > 0 ? " (none: " + none + ")" : ""));
                if (!edges) continue;
                foreach (var e in p.Edges) ctx.Log("rrw surf  e" + e.Index + " " + SurfaceIntrospection.Dump(em, e).Replace("\n", "\n    "));
            }
            foreach (var cap in SurfaceState.Caps.Values)
                ctx.Log("rrw surf cap node=" + cap.Node.Index + " stage=" + cap.Stage + " cover=" + (cap.Cover != null ? cap.Cover.Layer.ToString() : "-")
                        + " asphalt=" + (cap.Asphalt != null ? "yes" : "-"));
            var byPct = new SortedDictionary<int, int>();
            foreach (var s in SurfaceState.Scars)
            {
                int pct = s.Area != null ? s.Area.FadePct : 0;
                byPct.TryGetValue(pct, out int n);
                byPct[pct] = n + 1;
            }
            string st = "";
            foreach (var kv in byPct) st += " a" + kv.Key + "=" + kv.Value;
            ctx.Log("rrw surf scars " + SurfaceState.Scars.Count + ":" + st + " dropped=" + SurfaceState.ScarsDropped + " rekeyed=" + SurfaceState.ScarsRekeyed
                    + " last=[" + SurfaceState.LastScarDrop + "]");
        }
    }

    // Scar verification: every scar / curing decal with its layer, alpha step, age, anchors and overlap conflicts.
    public sealed class SurfScarsCommand : IDevCommand
    {
        public string Name => "rrw.surf.scars";
        public string Help => "rrw.surf.scars - list curing / scar decals (layer, alpha, age h, anchor road/node, overlapping works) + the curing / scar placement check";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            ctx.Log("rrw surf completion: edges=" + SurfaceState.CompletedEdges + " roadLayersRemoved=" + SurfaceState.CompletedRoadLayers
                    + " vergeScars=" + SurfaceState.CompletedVergeScars + " vergeScarsEnded=" + SurfaceState.VergeScarsEnded
                    + " strayCuringRemoved=" + SurfaceState.StrayCuringRemoved + " vergeEnd=" + SurfaceDevUtil.F(RRWConst.kVergeEndHours) + "h"
                    + " last=[" + SurfaceState.LastCompletion + "]");
            ctx.Log("rrw surf scars n=" + SurfaceState.Scars.Count + " dropped=" + SurfaceState.ScarsDropped + " rekeyed=" + SurfaceState.ScarsRekeyed
                    + " last=[" + SurfaceState.LastScarDrop + "] topsoilTint=" + SurfacePalette.TopsoilTint + " topsoilAlpha=" + SurfaceDevUtil.F(SurfacePalette.TopsoilAlpha));
            int i = 0;
            foreach (var s in SurfaceState.Scars)
            {
                var t = s.Area;
                float h = (float)(s.AgeFrames / RRWConst.kFramesPerHour);
                string anchors = "";
                foreach (var an in s.Anchors)
                    anchors += (an.IsNode ? " node" : " road") + RRWLog.E(an.Entity) + (EcsUtil.Alive(em, an.Entity) ? "" : "(GONE)")
                               + (SiteRegistry.Edges.ContainsKey(an.Entity) ? "(WORKS)" : "");
                string conf = "";
                foreach (var c in s.Conflicts) conf += " " + RRWLog.E(c);
                ctx.Log("rrw surf scar#" + (i++) + " " + (t != null ? t.Layer + " a" + t.FadePct + " " + (t.Pending ? "pending" : t.Live(em) ? RRWLog.E(t.Area) : "DEAD") : "?")
                        + (s.Construction ? " verge" : " scar") + " age=" + SurfaceDevUtil.F(h) + "h/" + SurfaceDevUtil.F(SurfaceAreaSystem.ScarEndHours(s.Construction)) + "h stage=" + s.Stage
                        + (s.Next != null ? " (swapping)" : "")
                        + " anchors=" + (s.Anchored ? (anchors.Length > 0 ? anchors : " none") : " free")
                        + " overlaps=" + (conf.Length > 0 ? conf : " none")
                        + (s.WaitSince != 0 ? " waitingHandOver=" + (RRWClock.UpdateIndex - s.WaitSince) + "u" : ""));
            }
            var problems = new List<string>();
            SurfaceIntrospection.CheckScars(em, problems);
            SurfaceIntrospection.CheckRound2(em, problems);
            if (problems.Count == 0) ctx.Log("rrw surf scars check ok");
            else foreach (var p in problems) ctx.Log("rrw surf scars FAIL " + p);
        }
    }

    // Look tests for the yellow temporary lines: test lines on any road, outside the works.
    public sealed class SurfLineCommand : IDevCommand
    {
        public string Name => "rrw.surf.line";
        public string Help => "rrw.surf.line <edge#> <Y1|Y2|Y3|BLK> lat=<m> w=<m> [t0] [t1] [dash=3,6] [round=<r>] [queue=<raise>] [prio=<n>] [tag=<t>]"
                              + " | rrw.surf.line info <edge#> | rrw.surf.line list | rrw.surf.line clear [tag]"
                              + " - yellow-line look tests (own DEVTOOLS clones: round 0.5 / 0.01 side by side; queue / prio apply to the whole test clone)";

        public void Run(DevContext ctx, string[] a)
        {
            if (a.Length == 0) { ctx.Log(Help); return; }
            switch (a[0])
            {
                case "list": List(ctx); return;
                case "clear":
                    SurfaceState.DevLineClear = a.Length > 1 ? a[1] : "";
                    ctx.Log("rrw surf line clear queued" + (a.Length > 1 ? " tag=" + a[1] : " (all)"));
                    return;
                case "info":
                    if (a.Length < 2) { ctx.Log("rrw surf line info <edge#>"); return; }
                    Info(ctx, ctx.Road(a[1]));
                    return;
            }
            if (a.Length < 2) { ctx.Log(Help); return; }
            Entity edge = ctx.Road(a[0]);
            int kind = -1;
            for (int k = 0; k < SurfaceState.DevLineKindCount; k++)
                if (string.Equals(a[1], SurfaceState.DevLineKindName(k), System.StringComparison.OrdinalIgnoreCase)) kind = k;
            if (kind < 0) { ctx.Log("rrw surf line: kind must be Y1, Y2, Y3 or BLK"); return; }
            var q = new DevLineRequest { Edge = edge, Kind = kind, Lat = 0f, Width = RRWGates.TempLineWidth, T0 = 0f, T1 = 1f, Tag = "" };
            int bare = 0;
            for (int i = 2; i < a.Length; i++)
            {
                string arg = a[i];
                int eq = arg.IndexOf('=');
                if (eq < 0)
                {
                    float f = DevContext.F(arg);
                    if (bare++ == 0) q.T0 = f; else q.T1 = f;
                    continue;
                }
                string k = arg.Substring(0, eq), val = arg.Substring(eq + 1);
                switch (k)
                {
                    case "lat": q.Lat = DevContext.F(val); break;
                    case "w": q.Width = DevContext.F(val); break;
                    case "t0": q.T0 = DevContext.F(val); break;
                    case "t1": q.T1 = DevContext.F(val); break;
                    case "dash":
                    {
                        var parts = val.Split(',');
                        q.Dash = DevContext.F(parts[0]);
                        q.Gap = parts.Length > 1 ? DevContext.F(parts[1]) : RRWConst.kTempGap;
                        break;
                    }
                    case "round": q.Round = DevContext.F(val); break;
                    case "queue": q.Queue = DevContext.I(val.TrimStart('+')); break;
                    case "prio": q.Prio = DevContext.I(val); break;
                    case "tag": q.Tag = val; break;
                    default: ctx.Log("rrw surf line: unknown argument " + arg); return;
                }
            }
            SurfaceState.DevLineRequests.Add(q);
            ctx.Log("rrw surf line queued edge=" + RRWLog.E(edge) + " " + SurfaceState.DevLineKindName(kind) + " lat=" + SurfaceDevUtil.F(q.Lat) + " w=" + SurfaceDevUtil.F(q.Width)
                    + " t=[" + SurfaceDevUtil.F(q.T0) + "," + SurfaceDevUtil.F(q.T1) + "]" + (q.Dash > 0f ? " dash=" + SurfaceDevUtil.F(q.Dash) + "," + SurfaceDevUtil.F(q.Gap) : "")
                    + (float.IsNaN(q.Round) ? "" : " round=" + SurfaceDevUtil.F(q.Round)) + (q.Queue != int.MinValue ? " queue=+" + q.Queue : "")
                    + (q.Prio != int.MinValue ? " prio=" + q.Prio : "") + " (spawned next frame; result in the log as 'surfaces: rrw.surf.line ...')");
        }

        static void List(DevContext ctx)
        {
            var em = ctx.EntityManager;
            ctx.Log("rrw surf line " + SurfaceState.DevLines.Count + " test line(s)");
            foreach (var dl in SurfaceState.DevLines)
            {
                int live = 0, pend = 0;
                foreach (var t in dl.Pieces) { if (t.Pending) pend++; else if (t.Live(em)) live++; }
                ctx.Log("rrw surf line tag=" + dl.Tag + " edge=" + RRWLog.E(dl.Edge) + " \"" + dl.Clone + "\" " + dl.What + " areas=" + dl.Pieces.Count
                        + " live=" + live + " pending=" + pend);
            }
            for (int k = 0; k < SurfaceState.DevLineKindCount; k++)
                for (int r = 0; r < 2; r++)
                {
                    string n = SurfaceState.DevLineClone(k, r == 0);
                    ctx.Log("rrw surf line clone \"" + n + "\" " + SurfaceMaterialSystem.Describe(ctx.World, n));
                }
        }

        // Where the product's yellow lines would lie on this road (edge frame, + = right of the curve direction): the numbers
        // to use as lat= for the test lines, plus the measured section (DirSplit, drive lanes, lane lines).
        static void Info(DevContext ctx, Entity edge)
        {
            var em = ctx.EntityManager;
            if (!em.HasComponent<Game.Net.Curve>(edge)) { ctx.Log("rrw surf line info: no curve"); return; }
            var arc = new EdgeArc(em.GetComponentData<Game.Net.Curve>(edge).m_Bezier);
            var sec = EcsUtil.MeasureSection(em, edge, arc);
            ctx.Log("rrw surf line info edge=" + RRWLog.E(edge) + " L=" + SurfaceDevUtil.F(arc.Length) + " carriage=[" + SurfaceDevUtil.F(sec.CarriageLo) + "," + SurfaceDevUtil.F(sec.CarriageHi)
                    + "] lanesMeasured=" + sec.LanesMeasured + " dirSplit=" + RRWLog.F(sec.DirSplit) + " drive=[" + SurfaceDevUtil.F(sec.DriveLoE) + "," + SurfaceDevUtil.F(sec.DriveHiE)
                    + "] laneLinesLeft=" + RRWLog.F(sec.LaneLinesLeft.x) + "," + RRWLog.F(sec.LaneLinesLeft.y) + " laneLinesRight=" + RRWLog.F(sec.LaneLinesRight.x) + "," + RRWLog.F(sec.LaneLinesRight.y));
            for (int h = 0; h < 2; h++)
            {
                var half = h == 0 ? RoadZones.LeftHalf : RoadZones.RightHalf;
                for (int na = 0; na < 2; na++)
                {
                    int n = sec.TempLineCount(half, false, na == 1);
                    string lines = "";
                    for (int i = 0; i < n; i++)
                        if (sec.TempLine(i, false, half, na == 1, out float l, out float r))
                            lines += " line" + i + (EdgeSection.TempLineDashed(i) ? "(dashed)" : "") + " lat=" + SurfaceDevUtil.F((l + r) * 0.5f) + " w=" + SurfaceDevUtil.F(r - l);
                    ctx.Log("rrw surf line info open=" + (h == 0 ? "LeftHalf" : "RightHalf") + " " + (na == 1 ? "NA" : "EU") + ": " + n + " line(s)" + lines);
                }
            }
        }
    }

    public sealed class SurfCheckCommand : IDevCommand
    {
        public string Name => "rrw.surf.check";
        public string Help => "rrw.surf.check - the Surfaces part of rrw.check (LivePath, tracked areas, cover holes sampled every 0.25 m, piece overlaps / stuck replaced pieces)";
        public void Run(DevContext ctx, string[] a)
        {
            var problems = new List<string>();
            SurfaceIntrospection.Check(ctx.EntityManager, problems);
            if (problems.Count == 0) { ctx.Log("rrw surf check ok"); return; }
            ctx.Log("rrw surf check FAIL " + problems.Count + ":");
            foreach (var p in problems) ctx.Log("rrw surf   " + p);
        }
    }
}
#endif
