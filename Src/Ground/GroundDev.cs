#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Simulation;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

// Ground dev commands (module commands). DEVTOOLS builds only. Output lines start with "dev rrw ground".
//   rrw.ground.xsec <site> [t=0.5|chain] [half=<hw+10>] [step=1] [v]   rendered cross-section / chain long-section
//   rrw.ground.status                                                   clone table, triggers, GC, sampling stats
//   rrw.ground.nat <site>                                               natural-ground estimates (CPU centre/verge, GPU base, saved)
//   rrw.ground.floor <site|all>                                         excavator floor diagnosis: applied vs target floor per edge, write lag
// <site>: #n (n-th project by id), p<id>, e<edge index>, r<n> (test road index), or a plain number (= #n).
namespace RealisticRoadWorks.V3.Ground
{
    internal static class GroundDevUtil
    {
        public static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        public static string Fm(float v) => RRWLog.F(v);

        public static bool Resolve(DevContext ctx, string arg, out ProjectRecord proj, out Entity edge, out string err)
        {
            proj = null; edge = Entity.Null; err = null;
            var em = ctx.EntityManager;
            try
            {
                if (string.IsNullOrEmpty(arg)) { err = "missing <site>"; return false; }
                char c = arg[0];
                if (c == 'p' || c == 'P')
                {
                    uint id = uint.Parse(arg.Substring(1), CultureInfo.InvariantCulture);
                    if (!SiteRegistry.TryGetProject(id, out proj)) { err = "no project " + arg; return false; }
                    edge = proj.Edges.Count > 0 ? proj.Edges[0] : Entity.Null;
                    return true;
                }
                if (c == 'e' || c == 'E')
                {
                    int idx = int.Parse(arg.Substring(1), CultureInfo.InvariantCulture);
                    foreach (var e in SiteRegistry.Edges.Keys) if (e.Index == idx) { edge = e; break; }
                    if (edge == Entity.Null) foreach (var e in GroundTable.Edges.Keys) if (e.Index == idx) { edge = e; break; }
                    if (edge == Entity.Null) foreach (var e in ctx.Roads()) if (e.Index == idx) { edge = e; break; }
                    if (edge == Entity.Null) { err = "no edge with index " + idx; return false; }
                    if (SiteRegistry.TryGetEdge(edge, out var rec)) SiteRegistry.TryGetProject(rec.ProjectId, out proj);
                    return true;
                }
                if (c == 'r' || c == 'R')
                {
                    edge = ctx.Road(arg.Substring(1));
                    if (SiteRegistry.TryGetEdge(edge, out var rec)) SiteRegistry.TryGetProject(rec.ProjectId, out proj);
                    return true;
                }
                string num = c == '#' ? arg.Substring(1) : arg;
                int n = int.Parse(num, CultureInfo.InvariantCulture);
                var list = SiteRegistry.Projects.Values.OrderBy(p => p.Id).ToList();
                if (n < 0 || n >= list.Count) { err = "no project #" + n + " (" + list.Count + " projects)"; return false; }
                proj = list[n];
                edge = proj.Edges.Count > 0 ? proj.Edges[0] : Entity.Null;
                return true;
            }
            catch (Exception e) { err = "bad site '" + arg + "': " + e.Message; return false; }
        }

        public static float GradeOffset(EntityManager em, Entity edge, EdgeRecord rec)
        {
            if (em.HasComponent<RoadWorksRuntime>(edge))
            {
                float g = em.GetComponentData<RoadWorksRuntime>(edge).m_GradeOffset;
                if (!float.IsNaN(g) && math.abs(g) < 5f) return g;
            }
            if (rec != null && rec.Arc != null) return rec.Section.GradeOffset;
            return EcsUtil.MeasureSection(em, edge, new EdgeArc(em.GetComponentData<Curve>(edge).m_Bezier)).GradeOffset;
        }

        public static EdgeArc Arc(EntityManager em, Entity edge, EdgeRecord rec) =>
            rec != null && rec.Arc != null ? rec.Arc : new EdgeArc(em.GetComponentData<Curve>(edge).m_Bezier);

        public static string Profile(Entity edge, out TerrainProfile applied, out bool known)
        {
            applied = TerrainProfile.Vanilla;
            known = false;
            if (!GroundTable.Edges.TryGetValue(edge, out var st)) return "vanilla(no state)";
            if (st.Stage == GroundStage.Vanilla) return "vanilla";
            applied = st.Applied[0];
            known = st.Written[0];
            return st.Stage + " " + applied;
        }
    }

    public sealed class GroundXsecCommand : IDevCommand
    {
        public string Name => "rrw.ground.xsec";
        public string Help => "rrw.ground.xsec <site> [t=0.5|chain] [half=<hw+10>] [step=1] [v] - rendered terrain (GPU finest cascade) vs grade and road surface: cross-section at chain fraction t, or a long-section along the whole chain (node ramps)";

        public void Run(DevContext ctx, string[] args)
        {
            if (args.Length < 1) { ctx.Log("rrw ground xsec usage: " + Help); return; }
            if (!GroundDevUtil.Resolve(ctx, args[0], out var proj, out var edge, out string err)) { ctx.Log("rrw ground xsec: " + err); return; }
            bool chain = false, verbose = false;
            float t = 0.5f, half = float.NaN, step = float.NaN;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "chain") chain = true;
                else if (a == "v") verbose = true;
                else if (a.StartsWith("t=")) t = GroundDevUtil.F(a.Substring(2));
                else if (a.StartsWith("half=")) half = GroundDevUtil.F(a.Substring(5));
                else if (a.StartsWith("step=")) step = GroundDevUtil.F(a.Substring(5));
                else if (a == "t") { }
                else t = GroundDevUtil.F(a);
            }
            var ts = ctx.System<TerrainSystem>();
            if (chain)
            {
                if (proj == null) { ctx.Log("rrw ground xsec chain: " + args[0] + " is not a works project"); return; }
                RunChain(ctx, ts, proj, float.IsNaN(step) ? 1f : step, verbose);
            }
            else RunCross(ctx, ts, proj, edge, math.saturate(t), half, float.IsNaN(step) ? 1f : step, verbose);
        }

        // ---------------------------------------------------------------- cross-section

        private static void RunCross(DevContext ctx, TerrainSystem ts, ProjectRecord proj, Entity edge, float t, float half, float step, bool verbose)
        {
            var em = ctx.EntityManager;
            float s;
            EdgeRecord rec = null;
            if (proj != null)
            {
                // chain fraction t -> the edge containing u = t * U
                float u = t * proj.ChainLength;
                Entity best = Entity.Null; float bestD = float.MaxValue, bu0 = 0f, bu1 = 0f;
                foreach (var e in proj.Edges)
                {
                    if (!EcsUtil.Alive(em, e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                    var site = em.GetComponentData<RoadWorksSite>(e);
                    float d = u < site.ChainLo ? site.ChainLo - u : u > site.ChainHi ? u - site.ChainHi : 0f;
                    if (d < bestD) { bestD = d; best = e; bu0 = site.m_ChainU0; bu1 = site.m_ChainU1; }
                }
                if (best == Entity.Null) { ctx.Log("rrw ground xsec: project has no live edge"); return; }
                edge = best;
                SiteRegistry.TryGetEdge(edge, out rec);
                var arc0 = GroundDevUtil.Arc(em, edge, rec);
                s = PhasePlan.EdgeS(u, bu0, bu1, arc0.Length);
            }
            else
            {
                if (!EcsUtil.Alive(em, edge) || !em.HasComponent<Curve>(edge)) { ctx.Log("rrw ground xsec: edge not alive"); return; }
                SiteRegistry.TryGetEdge(edge, out rec);
                s = GroundDevUtil.Arc(em, edge, rec).Length * t;
            }
            var arc = GroundDevUtil.Arc(em, edge, rec);
            float3 p = arc.Position(s);
            float3 rn = arc.Right(s);
            float go = GroundDevUtil.GradeOffset(em, edge, rec);
            float grade = p.y + go;
            float hw = rec != null && rec.Section.HalfWidth > 0.5f ? rec.Section.HalfWidth : EcsUtil.CompositionWidth(em, edge) * 0.5f;
            if (float.IsNaN(half)) half = hw + 10f;
            half = math.clamp(half, 1f, 60f);
            step = math.clamp(step, 0.25f, 5f);
            string prof = GroundDevUtil.Profile(edge, out var applied, out bool known);
            int n = (int)math.floor(2f * half / step) + 1;
            var offs = new float[n];
            var pts = new float3[n];
            var area = new Bounds2(new float2(float.MaxValue), new float2(float.MinValue));
            for (int i = 0; i < n; i++)
            {
                offs[i] = -half + i * step;
                pts[i] = p + rn * offs[i];
                area = area | new Bounds2(pts[i].xz, pts[i].xz);
            }
            int slice = NaturalSampler.FinestSlice(ts, area);
            ctx.Log("rrw ground xsec start edge=" + RRWLog.E(edge) + " s=" + GroundDevUtil.Fm(s) + "/" + GroundDevUtil.Fm(arc.Length) + " p=" + RRWLog.F3(p) +
                    " surfaceY=" + GroundDevUtil.Fm(p.y) + " gradeOffset=" + GroundDevUtil.Fm(go) + " halfWidth=" + GroundDevUtil.Fm(hw) +
                    " profile=[" + prof + "] slice=" + slice + " samples=" + n + " update=" + RRWClock.UpdateIndex);
            if (slice < 0) { ctx.Log("rrw ground xsec RESULT failed: no cascade slice contains the section (move the camera closer)"); return; }
            var cas = Fill(n);
            var nat = Fill(n);
            int pending = 2;
            uint startUpdate = RRWClock.UpdateIndex;
            Action done = () =>
            {
                if (--pending > 0) return;
                try { ReportCross(ctx, edge, prof, applied, known, offs, cas, nat, p.y, grade, hw, slice, verbose, startUpdate); }
                catch (Exception e) { ctx.Log("rrw ground xsec report failed: " + e.Message); }
            };
            var hso = ts.heightScaleOffset;
            NaturalSampler.Request(ts.GetCascadeTexture(), slice, NaturalSampler.CascadeRange(ts, slice), area, hso,
                g => { for (int i = 0; i < n; i++) cas[i] = g.Sample(pts[i]); done(); },
                m => { ctx.Log("rrw ground xsec cascade readback failed: " + m); done(); });
            NaturalSampler.Request(ts.heightmap, 0, NaturalSampler.BaseRange(ts), area, hso,
                g => { for (int i = 0; i < n; i++) nat[i] = g.Sample(pts[i]); done(); },
                m => { ctx.Log("rrw ground xsec base readback failed: " + m); done(); });
        }

        private static float[] Fill(int n) { var r = new float[n]; for (int i = 0; i < n; i++) r[i] = float.NaN; return r; }

        private static void ReportCross(DevContext ctx, Entity edge, string prof, TerrainProfile applied, bool known, float[] offs, float[] cas, float[] nat,
                                        float surfaceY, float grade, float hw, int slice, bool verbose, uint startUpdate)
        {
            float casMinIn = float.MaxValue, maxAboveSurfaceIn = float.MinValue, centre = float.NaN, natCentre = float.NaN, bestD = float.MaxValue;
            for (int i = 0; i < offs.Length; i++)
            {
                float o = offs[i];
                bool inside = math.abs(o) <= hw;
                float rel = cas[i] - grade;
                if (verbose || i % 2 == 0 || inside)
                    ctx.Log("rrw ground xsec o=" + GroundDevUtil.Fm(o) + " relGrade=" + GroundDevUtil.Fm(rel) + " relSurface=" + GroundDevUtil.Fm(cas[i] - surfaceY) +
                            " natRelGrade=" + GroundDevUtil.Fm(nat[i] - grade) + (inside ? " in" : ""));
                if (math.abs(o) < bestD) { bestD = math.abs(o); centre = rel; natCentre = nat[i] - grade; }
                if (!inside || float.IsNaN(cas[i])) continue;
                casMinIn = math.min(casMinIn, rel);
                maxAboveSurfaceIn = math.max(maxAboveSurfaceIn, cas[i] - surfaceY);
            }
            // floor extent: contiguous offsets around the centre within 0.1 m of the minimum
            float floorLo = float.NaN, floorHi = float.NaN;
            int mid = offs.Length / 2;
            if (casMinIn < float.MaxValue && !float.IsNaN(cas[mid]) && cas[mid] - grade <= casMinIn + 0.1f)
            {
                int lo = mid, hi = mid;
                while (lo > 0 && !float.IsNaN(cas[lo - 1]) && cas[lo - 1] - grade <= casMinIn + 0.1f) lo--;
                while (hi < offs.Length - 1 && !float.IsNaN(cas[hi + 1]) && cas[hi + 1] - grade <= casMinIn + 0.1f) hi++;
                floorLo = offs[lo]; floorHi = offs[hi];
            }
            string expect = "n/a";
            string ok = "n/a";
            if (known && (applied.Kind == TerrainProfileKind.Bed || applied.Kind == TerrainProfileKind.ClipOnly))
            {
                float exp = applied.MiddleFloor;
                expect = GroundDevUtil.Fm(exp);
                ok = math.abs(centre - exp) <= 0.07f ? "yes" : "NO";
                expect += " delta=" + GroundDevUtil.Fm(centre - exp);
            }
            else if (known && applied.Kind == TerrainProfileKind.Morph)
                expect = "[" + GroundDevUtil.Fm(applied.MiddleFloor) + "," + GroundDevUtil.Fm(applied.MiddleCap) + "] (morph clamp of natural)";
            else if (known && applied.Kind == TerrainProfileKind.Natural)
                expect = "natural " + GroundDevUtil.Fm(natCentre);
            bool below = maxAboveSurfaceIn <= 0.05f;
            ctx.Log("rrw ground xsec RESULT edge=" + RRWLog.E(edge) + " profile=[" + prof + "] slice=" + slice +
                    " centreRelGrade=" + GroundDevUtil.Fm(centre) + " expectedFloor=" + expect + " floorOk(+-0.07)=" + ok +
                    " casMinIn=" + GroundDevUtil.Fm(casMinIn == float.MaxValue ? float.NaN : casMinIn) +
                    " floorExtent=" + GroundDevUtil.Fm(floorLo) + ".." + GroundDevUtil.Fm(floorHi) +
                    " maxAboveSurfaceIn=" + GroundDevUtil.Fm(maxAboveSurfaceIn == float.MinValue ? float.NaN : maxAboveSurfaceIn) +
                    " terrainBelowSurface=" + (below ? "yes" : "NO") + " natCentreRelGrade=" + GroundDevUtil.Fm(natCentre) +
                    " updatesSinceRequest=" + unchecked(RRWClock.UpdateIndex - startUpdate));
        }

        // ---------------------------------------------------------------- long-section along the chain (node ramps)

        private struct ChainSample
        {
            public float U;
            public float3 P;
            public float Grade;
            public int EdgeIndex;
        }

        private static void RunChain(DevContext ctx, TerrainSystem ts, ProjectRecord proj, float step, bool verbose)
        {
            var em = ctx.EntityManager;
            float U = proj.ChainLength;
            if (U <= 0.5f) { ctx.Log("rrw ground xsec chain: empty chain"); return; }
            step = math.max(math.clamp(step, 0.25f, 10f), U / 1500f);
            var edges = new List<Entity>();
            var lo = new List<float>();
            var hi = new List<float>();
            var u0s = new List<float>();
            var u1s = new List<float>();
            var arcs = new List<EdgeArc>();
            var gos = new List<float>();
            var expect = new List<float>();
            foreach (var e in proj.Edges)
            {
                if (!EcsUtil.Alive(em, e) || !em.HasComponent<RoadWorksSite>(e) || !em.HasComponent<Curve>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                SiteRegistry.TryGetEdge(e, out var rec);
                edges.Add(e); lo.Add(site.ChainLo); hi.Add(site.ChainHi); u0s.Add(site.m_ChainU0); u1s.Add(site.m_ChainU1);
                arcs.Add(GroundDevUtil.Arc(em, e, rec));
                gos.Add(GroundDevUtil.GradeOffset(em, e, rec));
                GroundDevUtil.Profile(e, out var ap, out bool known);
                expect.Add(known && ap.Kind != TerrainProfileKind.Natural ? ap.MiddleFloor : float.NaN);
            }
            if (edges.Count == 0) { ctx.Log("rrw ground xsec chain: no live edge"); return; }
            var samples = new List<ChainSample>();
            var area = new Bounds2(new float2(float.MaxValue), new float2(float.MinValue));
            for (float u = 0f; u <= U + 1e-3f; u += step)
            {
                int k = 0; float bd = float.MaxValue;
                for (int i = 0; i < edges.Count; i++)
                {
                    float d = u < lo[i] ? lo[i] - u : u > hi[i] ? u - hi[i] : 0f;
                    if (d < bd) { bd = d; k = i; }
                }
                float s = PhasePlan.EdgeS(u, u0s[k], u1s[k], arcs[k].Length);
                float3 p = arcs[k].Position(s);
                samples.Add(new ChainSample { U = u, P = p, Grade = p.y + gos[k], EdgeIndex = k });
                area = area | new Bounds2(p.xz, p.xz);
            }
            int slice = NaturalSampler.FinestSlice(ts, area);
            ctx.Log("rrw ground xsec chain start project=" + proj.Id + " U=" + GroundDevUtil.Fm(U) + " edges=" + edges.Count + " samples=" + samples.Count +
                    " step=" + GroundDevUtil.Fm(step) + " slice=" + slice + " phase=" + proj.Phase + " f=" + GroundDevUtil.Fm(proj.PhaseFraction));
            if (slice < 0) { ctx.Log("rrw ground xsec chain RESULT failed: no cascade slice contains the chain (zoom out a little)"); return; }
            NaturalSampler.Request(ts.GetCascadeTexture(), slice, NaturalSampler.CascadeRange(ts, slice), area, ts.heightScaleOffset, g =>
            {
                try
                {
                    var rel = new float[samples.Count];
                    for (int i = 0; i < samples.Count; i++) rel[i] = g.Sample(samples[i].P) - samples[i].Grade;
                    ReportChain(ctx, proj.Id, samples, rel, edges, lo, hi, expect, step, verbose);
                }
                catch (Exception e) { ctx.Log("rrw ground xsec chain report failed: " + e.Message); }
            }, m => ctx.Log("rrw ground xsec chain RESULT failed: " + m));
        }

        private static void ReportChain(DevContext ctx, uint projectId, List<ChainSample> samples, float[] rel, List<Entity> edges, List<float> lo, List<float> hi,
                                        List<float> expect, float step, bool verbose)
        {
            int every = verbose ? 1 : math.max(1, (int)math.round(4f / step));
            for (int i = 0; i < samples.Count; i += every)
                ctx.Log("rrw ground xsec chain u=" + GroundDevUtil.Fm(samples[i].U) + " edge=" + RRWLog.E(edges[samples[i].EdgeIndex]) + " relGrade=" + GroundDevUtil.Fm(rel[i]));
            // per edge: mean floor over the middle half
            var mean = new float[edges.Count];
            for (int k = 0; k < edges.Count; k++)
            {
                float a = math.lerp(lo[k], hi[k], 0.25f), b = math.lerp(lo[k], hi[k], 0.75f);
                double sum = 0; int n = 0;
                for (int i = 0; i < samples.Count; i++)
                    if (samples[i].U >= a && samples[i].U <= b && !float.IsNaN(rel[i])) { sum += rel[i]; n++; }
                mean[k] = n > 0 ? (float)(sum / n) : float.NaN;
                ctx.Log("rrw ground xsec chain RESULT edge=" + RRWLog.E(edges[k]) + " u=[" + GroundDevUtil.Fm(lo[k]) + "," + GroundDevUtil.Fm(hi[k]) + "] meanFloorRelGrade=" + GroundDevUtil.Fm(mean[k]) +
                        " expected=" + GroundDevUtil.Fm(expect[k]) + (float.IsNaN(expect[k]) || float.IsNaN(mean[k]) ? "" : " delta=" + GroundDevUtil.Fm(mean[k] - expect[k])));
            }
            // per interior node: distance from the node until the floor is within 0.1 m of each side's mean
            var order = Enumerable.Range(0, edges.Count).OrderBy(k => lo[k]).ToList();
            for (int j = 0; j + 1 < order.Count; j++)
            {
                int a = order[j], b = order[j + 1];
                float un = 0.5f * (hi[a] + lo[b]);
                float dA = Ramp(samples, rel, un, -1, mean[a]);
                float dB = Ramp(samples, rel, un, +1, mean[b]);
                ctx.Log("rrw ground xsec chain RESULT node u=" + GroundDevUtil.Fm(un) + " floorBefore=" + GroundDevUtil.Fm(mean[a]) + " floorAfter=" + GroundDevUtil.Fm(mean[b]) +
                        " rampHalfWidth=" + GroundDevUtil.Fm(dA) + "/" + GroundDevUtil.Fm(dB) + " m");
            }
            ctx.Log("rrw ground xsec chain RESULT done project=" + projectId + " samples=" + samples.Count);
        }

        private static float Ramp(List<ChainSample> samples, float[] rel, float un, int dir, float floor)
        {
            if (float.IsNaN(floor)) return float.NaN;
            float best = float.NaN;
            // walk outward from the node; the ramp ends at the first sample that is within 0.1 m of the floor
            var idx = new List<int>();
            for (int i = 0; i < samples.Count; i++)
                if ((dir < 0 && samples[i].U <= un) || (dir > 0 && samples[i].U >= un)) idx.Add(i);
            idx.Sort((x, y) => math.abs(samples[x].U - un).CompareTo(math.abs(samples[y].U - un)));
            foreach (int i in idx)
            {
                if (float.IsNaN(rel[i])) continue;
                if (math.abs(rel[i] - floor) <= 0.1f) { best = math.abs(samples[i].U - un); break; }
            }
            return best;
        }
    }

    public sealed class GroundStatusCommand : IDevCommand
    {
        public string Name => "rrw.ground.status";
        public string Help => "rrw.ground.status - Ground clone table: states per stage, orphans, clones alive / pending GC, trigger rate, creation hits/misses, natural sampling stats";

        private static float s_LastTime = -1f;
        private static int s_LastTriggers;

        public void Run(DevContext ctx, string[] args)
        {
            int[] stages = new int[4];
            int orphans = 0, pending = 0, faulted = 0, ineligible = 0, demoComplete = 0;
            foreach (var st in GroundTable.Edges.Values)
            {
                stages[(int)st.Stage]++;
                if (st.IsOrphan) orphans++;
                if (st.Pending) pending++;
                if (st.Faulted) faulted++;
                if (st.Ineligible) ineligible++;
                if (st.CompleteSinceUpdate != 0) demoComplete++;
            }
            int clones = ExcavationSystem.Instance != null ? ExcavationSystem.Instance.CloneEntityCount : -1;
            float now = UnityEngine.Time.realtimeSinceStartup;
            string rate = "n/a";
            if (s_LastTime >= 0f && now > s_LastTime + 0.1f)
                rate = GroundDevUtil.Fm((GroundTable.TriggerUpdates - s_LastTriggers) / (now - s_LastTime)) + "/s over " + GroundDevUtil.Fm(now - s_LastTime) + " s";
            s_LastTime = now;
            s_LastTriggers = GroundTable.TriggerUpdates;
            ctx.Log("rrw ground status states=" + GroundTable.Edges.Count + " vanilla=" + stages[0] + " cloned=" + stages[1] + " hold=" + stages[2] + " restoring=" + stages[3] +
                    " orphans=" + orphans + " pendingWrites=" + pending + " faulted=" + faulted + " ineligible=" + ineligible +
                    " cloneEntities=" + clones + " pendingDestroy=" + GroundTable.PendingDestroy.Count + " abandoned=" + GroundTable.Abandoned.Count +
                    " made=" + GroundTable.ClonesMade + " destroyed=" + GroundTable.ClonesDestroyed +
                    " triggerUpdates=" + GroundTable.TriggerUpdates + " triggeredEdges=" + GroundTable.TriggeredEdges + " triggerRate=" + rate +
                    " demoCompleteNow=" + demoComplete + " demoReady=" + GroundTable.DemolitionReady + " demoReadyMaxWait=" + GroundTable.DemolitionReadyMaxWait +
                    " creationHits=" + GroundTable.CreationHits + " creationMisses=" + GroundTable.CreationMisses + " adoptions=" + GroundTable.Adoptions +
                    " crewChanges=" + GroundTable.CrewChanges + " maxBatchEdges=" + GroundTable.MaxBatchEdges + " maxPendingAge=" + GroundTable.MaxPendingAge +
                    " gpu(req=" + GroundTable.GpuRequests + ",ok=" + GroundTable.GpuResults + ",fail=" + GroundTable.GpuFailures + ",inFlight=" + NaturalSampler.GpuInFlight + ")" +
                    " systemFaulted=" + (ExcavationSystem.Faulted ? 1 : 0) + " update=" + RRWClock.UpdateIndex);
        }
    }

    public sealed class GroundNatCommand : IDevCommand
    {
        public string Name => "rrw.ground.nat";
        public string Help => "rrw.ground.nat <site> - natural ground per edge: CPU centre (valid only while NATURAL), CPU verge interpolation, GPU base heightmap, and the saved NatAbove/NatBelow";

        public void Run(DevContext ctx, string[] args)
        {
            if (args.Length < 1) { ctx.Log("rrw ground nat usage: " + Help); return; }
            if (!GroundDevUtil.Resolve(ctx, args[0], out var proj, out var edge0, out string err)) { ctx.Log("rrw ground nat: " + err); return; }
            var em = ctx.EntityManager;
            var ts = ctx.System<TerrainSystem>();
            var list = new List<Entity>();
            if (proj != null) list.AddRange(proj.Edges); else list.Add(edge0);
            foreach (var edge in list)
            {
                if (!EcsUtil.Alive(em, edge)) continue;
                SiteRegistry.TryGetEdge(edge, out var rec);
                float go = GroundDevUtil.GradeOffset(em, edge, rec);
                if (!NaturalSampler.TryGeometry(em, edge, rec, go, out var geo)) { ctx.Log("rrw ground nat edge=" + RRWLog.E(edge) + " no geometry"); continue; }
                bool okC = NaturalSampler.SampleCpu(ts, geo, true, false, out float ca, out float cb, out int cn);
                bool okV = NaturalSampler.SampleCpu(ts, geo, false, true, out float va, out float vb, out int vn);
                string saved = "none";
                if (em.HasComponent<RoadWorksSite>(edge))
                {
                    var site = em.GetComponentData<RoadWorksSite>(edge);
                    saved = "sampled=" + (site.Has(SiteFlags.NaturalSampled) ? 1 : 0) + " above=" + GroundDevUtil.Fm(site.m_NatAbove) + " below=" + GroundDevUtil.Fm(site.m_NatBelow);
                }
                string prof = GroundDevUtil.Profile(edge, out _, out _);
                ctx.Log("rrw ground nat edge=" + RRWLog.E(edge) + " len=" + GroundDevUtil.Fm(geo.Arc.Length) + " hw=" + GroundDevUtil.Fm(geo.HalfWidth) + " verge=" + GroundDevUtil.Fm(geo.VergeLateral) +
                        " gradeOffset=" + GroundDevUtil.Fm(geo.GradeOffset) + " profile=[" + prof + "]" +
                        " cpuCentre=" + (okC ? GroundDevUtil.Fm(ca) + "/" + GroundDevUtil.Fm(cb) + " n=" + cn : "n/a") +
                        " cpuVerge=" + (okV ? GroundDevUtil.Fm(va) + "/" + GroundDevUtil.Fm(vb) + " n=" + vn : "n/a") + " saved " + saved + " (above/below grade)");
                Entity e = edge;
                NaturalSampler.RequestGpuEstimate(ts, geo,
                    (a, b, n) => ctx.Log("rrw ground nat RESULT edge=" + RRWLog.E(e) + " gpuBase=" + GroundDevUtil.Fm(a) + "/" + GroundDevUtil.Fm(b) + " n=" + n +
                                         " cpuCentre=" + (okC ? GroundDevUtil.Fm(ca) + "/" + GroundDevUtil.Fm(cb) : "n/a") +
                                         " cpuVerge=" + (okV ? GroundDevUtil.Fm(va) + "/" + GroundDevUtil.Fm(vb) : "n/a")),
                    m => ctx.Log("rrw ground nat RESULT edge=" + RRWLog.E(e) + " gpu failed: " + m));
            }
        }
    }

    // The Ground side of the excavator floor-error diagnosis. Per edge: the edge-slot profile Ground
    // APPLIED (what RoadWorksGround / GroundMath.FloorWorldY and therefore the machine tracks use) vs the profile it targets this
    // update (PhasePlan.Terrain via the Ground target memo), the write lag (global trigger cap kTerrainTriggerMinUpdates), the
    // m_StepUpdate stamp and its age, the node-end slots (ramps) and a consistency check of the RoadWorksGround output against the
    // applied profile. "mismatch" > 0 would mean a missing output / m_StepUpdate bump (the Ground fix regressed); a large "lag" only
    // means the applied floor trails the plan by up to one trigger window (machines follow the applied floor, by design).
    public sealed class GroundFloorCommand : IDevCommand
    {
        public string Name => "rrw.ground.floor";
        public string Help => "rrw.ground.floor <site|all> - per edge: applied vs target edge-slot floor (cap / floor rel. to grade), write lag, step stamp age, node slots, output consistency (mismatch must be 0)";

        public void Run(DevContext ctx, string[] args)
        {
            var em = ctx.EntityManager;
            uint now = RRWClock.UpdateIndex;
            var list = new List<Entity>();
            if (args.Length < 1 || args[0] == "all") list.AddRange(GroundTable.Edges.Keys);
            else
            {
                if (!GroundDevUtil.Resolve(ctx, args[0], out var proj, out var edge0, out string err)) { ctx.Log("rrw ground floor: " + err); return; }
                if (proj != null) list.AddRange(proj.Edges); else list.Add(edge0);
            }
            int edges = 0, mismatches = 0, pending = 0;
            float worstLag = 0f;
            Entity worstLagEdge = Entity.Null;
            foreach (var edge in list)
            {
                if (!GroundTable.Edges.TryGetValue(edge, out var st)) { ctx.Log("rrw ground floor edge=" + RRWLog.E(edge) + " no Ground state"); continue; }
                edges++;
                var applied = st.Applied[0];
                bool written = st.Written[0];
                bool haveTarget = st.TargetUpdate != uint.MaxValue;
                var target = st.Target;
                float lagCap = Lag(applied.MiddleCap, target.MiddleCap), lagFloor = Lag(applied.MiddleFloor, target.MiddleFloor);
                float lag = math.max(lagCap, lagFloor);
                if (haveTarget && st.Stage == GroundStage.Cloned && lag > worstLag) { worstLag = lag; worstLagEdge = edge; }
                if (st.Pending) pending++;
                string outText = "no RoadWorksGround";
                if (EcsUtil.Alive(em, edge) && em.HasComponent<RoadWorksGround>(edge))
                {
                    var g = em.GetComponentData<RoadWorksGround>(edge);
                    bool mis = false;
                    bool inReg = SiteRegistry.Edges.ContainsKey(edge);   // Ground writes the output for registry edges only
                    if (!inReg) { }
                    else if (st.Stage != GroundStage.Vanilla && written)
                        mis = g.m_Kind != applied.Kind || !SameRel(g.m_FloorMinRel, applied.MiddleCap) || !SameRel(g.m_FloorMaxRel, applied.MiddleFloor);
                    if (inReg && g.m_StepUpdate != st.StepUpdate) mis = true;
                    if (mis) mismatches++;
                    outText = "out=" + g.m_Kind + " cap=" + GroundDevUtil.Fm(g.m_FloorMinRel) + " floor=" + GroundDevUtil.Fm(g.m_FloorMaxRel)
                              + " step@" + g.m_StepUpdate + (mis ? " MISMATCH" : "");
                }
                ctx.Log("rrw ground floor edge=" + RRWLog.E(edge) + (st.Record != null ? " p" + st.Record.ProjectId : "") + " " + st.SiteKind + " " + st.Phase
                        + " stage=" + st.Stage + " applied=" + (written ? applied.Kind + " cap=" + GroundDevUtil.Fm(applied.MiddleCap) + " floor=" + GroundDevUtil.Fm(applied.MiddleFloor) : "unwritten")
                        + " target=" + (haveTarget ? target.Kind + " cap=" + GroundDevUtil.Fm(target.MiddleCap) + " floor=" + GroundDevUtil.Fm(target.MiddleFloor)
                                        + " (" + unchecked(now - st.TargetUpdate) + " upd old)" : "none")
                        + " lag=" + GroundDevUtil.Fm(lag) + "m pending=" + st.Pending + (st.PendingSinceUpdate != 0 ? " for " + unchecked(now - st.PendingSinceUpdate) + " upd" : "")
                        + " step=" + unchecked(now - st.StepUpdate) + " upd ago t/y=" + GroundDevUtil.Fm(st.AppliedT) + "/" + GroundDevUtil.Fm(st.AppliedY)
                        + " done=" + GroundDevUtil.Fm(st.DoneFraction) + " crews=" + st.Crews
                        + " nodes start=" + Slot(st, 1) + " end=" + Slot(st, 2) + " " + outText);
            }
            ctx.Log("rrw ground floor edges=" + edges + " pendingWrites=" + pending + " worstLag=" + GroundDevUtil.Fm(worstLag) + "m"
                    + (worstLagEdge != Entity.Null ? " (edge " + RRWLog.E(worstLagEdge) + ")" : "") + " mismatch=" + mismatches
                    + " triggerCap=" + RRWConst.kTerrainTriggerMinUpdates + " upd maxPendingAge=" + GroundTable.MaxPendingAge + (mismatches == 0 ? " OK" : " FAIL"));
        }

        private static float Lag(float a, float b)
        {
            if (float.IsInfinity(a) && float.IsInfinity(b) && math.sign(a) == math.sign(b)) return 0f;
            if (float.IsNaN(a) || float.IsNaN(b) || float.IsInfinity(a) || float.IsInfinity(b)) return 0f;   // natural vs bracket: no floor value
            return math.abs(a - b);
        }

        private static bool SameRel(float a, float b) =>
            (float.IsInfinity(a) && float.IsInfinity(b) && math.sign(a) == math.sign(b)) || math.abs(a - b) <= 1e-4f;

        private static string Slot(GroundEdgeState st, int s) =>
            st.Clone[s] == Entity.Null ? "-" : st.Written[s] ? st.Applied[s].Kind + "(" + GroundDevUtil.Fm(st.Applied[s].MiddleCap) + "/" + GroundDevUtil.Fm(st.Applied[s].MiddleFloor) + ")" : "unwritten";
    }
}
#endif
