#if DEVTOOLS
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Hidden = Game.Tools.Hidden;
using NetCarLane = Game.Net.CarLane;

// rrw.check rows of upgrade works (mode H). Every count must be 0:
//   * a mode H edge that is hidden (or wants to be);
//   * a drop lane outside its band, outside its window, while the window does not run a lane drop, with a stale key, or with
//     its lane centre missing from the report;
//   * edges of one project whose schedules disagree (UpgradeRuntime.Mismatch);
//   * a puppet outside the machine-safe zones (zone level: the Machines report against UpgradeView.MachineSafe, ready drop lanes
//     and the sub-strips the shared machine rule allows);
//   * dirt or gravel on a sub-strip that carries traffic (the Core cover rule evaluated with the live drop / zone state);
//   * a project that mixes FullDig and mode H sites;
//   * an edge whose chain band index is stale for more than one update;
//   * a mode H site with an accepted cancel (a mode 2 demolition, or CancelledBuild on a mode 2 construction).
// Conditions that a module resolves on its next update (window switches, drop re-resolution, puppets leaving a zone that just
// opened) are sampled every update by DevCameraSystem and count only after they persisted for their grace time.
namespace RealisticRoadWorks.V3.DevCmds
{
    internal static class UwCheck
    {
        private const uint kChainIndexGraceUpdates = 1;   // the Director re-syncs every update: stale in two samples in a row is a bug
        private const uint kDropGraceUpdates = 60;        // Traffic re-resolves the drop lanes on its next request update
        private const uint kPuppetGraceUpdates = 120;     // a puppet leaves a zone that stops being machine-safe at a window switch

        private static uint s_LastSample;
        private static readonly Dictionary<Entity, uint> s_ChainStaleSince = new Dictionary<Entity, uint>();
        private static readonly Dictionary<Entity, uint> s_DropKeySince = new Dictionary<Entity, uint>();
        private static readonly Dictionary<uint, uint> s_PuppetOutsideSince = new Dictionary<uint, uint>();
        private static readonly List<Entity> s_GoneEdges = new List<Entity>();
        private static readonly List<uint> s_GoneProjects = new List<uint>();

        // ------------------------------------------------------------------ sampling (DevCameraSystem, every rendered update)

        public static void Sample()
        {
            uint now = RRWClock.UpdateIndex;
            if (now == s_LastSample) return;
            s_LastSample = now;
            if (SiteRegistry.Projects.Count == 0)
            {
                s_ChainStaleSince.Clear();
                s_DropKeySince.Clear();
                s_PuppetOutsideSince.Clear();
                return;
            }
            foreach (var p in SiteRegistry.Projects.Values)
            {
                var rt = p.Upgrade;
                if (rt == null) { s_PuppetOutsideSince.Remove(p.Id); continue; }
                var v = p.View();
                Track(s_PuppetOutsideSince, p.Id, PuppetOutside(p, v, now) != RoadZones.None, now);
                for (int i = 0; i < p.Edges.Count; i++)
                {
                    if (!SiteRegistry.TryGetEdge(p.Edges[i], out var er) || er.Upgrade == null) continue;
                    Track(s_ChainStaleSince, er.Edge, !er.Upgrade.ChainIndexCurrent(rt), now);
                    Track(s_DropKeySince, er.Edge, DropKeyProblem(er, v) != null, now);
                }
            }
            if ((now & 63) == 0) Prune();
        }

        private static void Track<TKey>(Dictionary<TKey, uint> since, TKey key, bool on, uint now)
        {
            if (!on) { since.Remove(key); return; }
            if (!since.ContainsKey(key)) since[key] = now;
        }

        private static void Prune()
        {
            s_GoneEdges.Clear();
            foreach (var k in s_ChainStaleSince.Keys) if (!SiteRegistry.Edges.ContainsKey(k)) s_GoneEdges.Add(k);
            foreach (var k in s_DropKeySince.Keys) if (!SiteRegistry.Edges.ContainsKey(k)) s_GoneEdges.Add(k);
            foreach (var k in s_GoneEdges) { s_ChainStaleSince.Remove(k); s_DropKeySince.Remove(k); }
            s_GoneProjects.Clear();
            foreach (var k in s_PuppetOutsideSince.Keys) if (!SiteRegistry.Projects.ContainsKey(k)) s_GoneProjects.Add(k);
            foreach (var k in s_GoneProjects) s_PuppetOutsideSince.Remove(k);
        }

        private static uint Age<TKey>(Dictionary<TKey, uint> since, TKey key, uint now) =>
            since.TryGetValue(key, out uint s) ? unchecked(now - s) : 0u;

        // ------------------------------------------------------------------ shared rules

        // Lane groups a puppet may touch on a mode H project although the group is open: under a lane drop, the halves (and their
        // parking strips) that hold the dropping bands of edges whose drop report is ready for this view (the report's window, the
        // applied or the layout window). None for every other project.
        public static RoadZones DropAllowance(ProjectRecord p)
        {
            if (p == null || p.Upgrade == null) return RoadZones.None;
            var v = p.View();
            return v.IsUpgrade ? DropAllowance(p, v.Upgrade) : RoadZones.None;
        }

        private static RoadZones DropAllowance(ProjectRecord p, in UpgradeView u)
        {
            var z = RoadZones.None;
            for (int e = 0; e < p.Edges.Count; e++)
            {
                if (!SiteRegistry.TryGetEdge(p.Edges[e], out var er) || er.Upgrade == null) continue;
                var es = er.Upgrade;
                int w = es.DropWindowFor(er.GeometryRevision, u);
                if (w < 0 || !PhasePlan.LaneDrops(u.Prim(w)) || !es.DropReadyFor(er.GeometryRevision, u)) continue;
                for (int i = 0; i < u.BandCount; i++)
                {
                    var b = u.Band(i);
                    if (!b.IsBuild || (u.AllAtOnce ? b.Window < w : b.Window != w)) continue;
                    z |= RoadZoneMath.OfBand(b.Lo, b.Hi, er.Section, es.ChainReversed);
                }
            }
            return z & (RoadZones.Carriageway | RoadZones.Parking);
        }

        // Lane groups of the sub-strips the shared machine rule lets a moving machine into now (PhasePlan.MachineSafe of
        // UpgradeZones.SubStripStateOf): a ready drop lane, or an empty new parking lane Traffic switched off. Driveway keep-outs
        // only stop standing machines and are Machines' own check.
        private static RoadZones SubStripAllowance(ProjectRecord p, in UpgradeView u)
        {
            var z = RoadZones.None;
            for (int e = 0; e < p.Edges.Count; e++)
            {
                if (!SiteRegistry.TryGetEdge(p.Edges[e], out var er) || er.Upgrade == null) continue;
                var es = er.Upgrade;
                bool rev = es.ChainReversed;
                float split = RoadZoneMath.DirSplitChain(er.Section, rev);
                for (int i = 0; i < es.BandCount; i++)
                {
                    var strips = es.SubStrips[i];
                    for (int k = 0; k < strips.Count; k++)
                    {
                        var s = strips[k];
                        if (s.Kind != SubKind.DriveLane && s.Kind != SubKind.NewParking && s.Kind != SubKind.Bike) continue;
                        if (PhasePlan.MachineSafe(UpgradeZones.SubStripStateOf(er, u, s, i, false), false))
                            z |= UpgradeZones.OfSubStrip(s, rev, split);
                    }
                }
            }
            return z & RoadZones.AllLanes;
        }

        // Lane groups a puppet of a mode H project may touch beyond UpgradeView.MachineSafe although the group is open (ready
        // drop lanes, switched-off new parking). None for every other project.
        public static RoadZones MachineAllowance(ProjectRecord p)
        {
            if (p == null || p.Upgrade == null) return RoadZones.None;
            var v = p.View();
            if (!v.IsUpgrade) return RoadZones.None;
            return DropAllowance(p, v.Upgrade) | SubStripAllowance(p, v.Upgrade);
        }

        // Lane groups the project's puppets touch outside the machine-safe zones (fresh report, not releasing; Outside ignored:
        // leave plans run to the trimmed chain ends).
        private static RoadZones PuppetOutside(ProjectRecord p, in ProjectView v, uint now)
        {
            if (!v.IsUpgrade || p.Releasing || !p.MachinesReportFresh(now)) return RoadZones.None;
            var allowed = v.Upgrade.MachineSafe | DropAllowance(p, v.Upgrade) | SubStripAllowance(p, v.Upgrade);
            return p.MachineZones & RoadZones.AllLanes & ~allowed;
        }

        // The drop report of an edge belongs to no window of this view (neither the applied nor the layout window, or another
        // geometry / tail / AllAtOnce state), or drops lanes while its window runs no lane drop. Null = fine.
        private static string DropKeyProblem(EdgeRecord er, in ProjectView v)
        {
            var es = er.Upgrade;
            if (es == null || es.DropWindow == -2 || es.DropLanes.Count == 0 || !v.IsUpgrade) return null;
            var u = v.Upgrade;
            int w = es.DropWindowFor(er.GeometryRevision, u);
            if (w < 0)
                return "stale key (window " + es.DropWindow + "/" + WindowsOf(u) + " geo " + es.DropLanesRevision + "/" + er.GeometryRevision
                       + " allAtOnce " + es.DropAllAtOnce + "/" + u.AllAtOnce + " tail " + es.DropTail + "/" + es.TailRevision + ")";
            if (!PhasePlan.LaneDrops(u.Prim(w))) return "lanes dropped while window " + w + " runs " + u.Prim(w);
            return null;
        }

        // "applied" or "applied+layout" while a window start keeps the previous window applied.
        private static string WindowsOf(in UpgradeView u) =>
            u.Vacating ? u.AppliedWindow + "+" + u.LayoutWindow : u.AppliedWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ rrw.check

        public static void Invariants(EntityManager em, List<string> problems)
        {
            uint now = RRWClock.UpdateIndex;
            bool sampled = unchecked(now - s_LastSample) <= 2u;
            int hidden = 0, chainStale = 0, dropKey = 0, dropBand = 0, dropCentres = 0, dirt = 0, noRuntime = 0;
            string hiddenAt = null, chainAt = null, dropKeyAt = null, dropBandAt = null, dropCentresAt = null, dirtAt = null, noRuntimeAt = null;

            foreach (var p in SiteRegistry.Projects.Values)
            {
                var rt = p.Upgrade;
                var v = p.View();
                int a = 0, h = 0, upgradeSites = 0;
                for (int i = 0; i < p.Edges.Count; i++)
                {
                    Entity e = p.Edges[i];
                    if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                    var s = em.GetComponentData<RoadWorksSite>(e);
                    if (s.Mode == VisualMode.FullDig) a++;
                    if (s.Mode != VisualMode.HalfWidth) continue;
                    h++;
                    if (s.IsUpgrade) upgradeSites++;
                    // never hidden
                    bool rtHidden = false;
                    if (em.HasComponent<RoadWorksRuntime>(e))
                    {
                        var r = em.GetComponentData<RoadWorksRuntime>(e);
                        rtHidden = r.Hidden || r.HideWanted;
                    }
                    if (em.HasComponent<Hidden>(e) || rtHidden) { hidden++; hiddenAt = hiddenAt ?? RRWLog.E(e) + " p" + p.Id; }
                }
                if (a > 0 && h > 0)
                    problems.Add("uw: p" + p.Id + " mixes " + a + " FullDig and " + h + " mode H sites (the Director completes the mode H edges of a mix)");
                if (upgradeSites > 0 && p.Kind == WorksKind.Construction && !p.Releasing && (rt == null || !v.IsUpgrade))
                { noRuntime++; noRuntimeAt = noRuntimeAt ?? "p" + p.Id + (rt == null ? " (no UpgradeRuntime)" : " (view without windows)"); }
                if (rt == null) continue;

                if (rt.Mismatch > 0)
                    problems.Add("uw: p" + p.Id + " " + rt.Mismatch + " edges disagree with the first edge's schedule or flags (" + rt.Schedule + ")");

                // puppets outside the machine-safe zones
                RoadZones outside = PuppetOutside(p, v, now);
                uint pAge = Age(s_PuppetOutsideSince, p.Id, now);
                if (outside != RoadZones.None && (!sampled || pAge >= kPuppetGraceUpdates))
                    problems.Add("uw: p" + p.Id + " puppets in " + RoadZoneMath.Describe(outside) + " outside the machine-safe zones "
                                 + RoadZoneMath.Describe(v.Upgrade.MachineSafe) + " (window " + WindowsOf(v.Upgrade) + " " + v.Upgrade.AppliedTraffic
                                 + (sampled ? ", for " + pAge + " updates" : "") + ")");

                for (int i = 0; i < p.Edges.Count; i++)
                {
                    if (!SiteRegistry.TryGetEdge(p.Edges[i], out var er)) continue;
                    var es = er.Upgrade;
                    if (es == null)
                    {
                        if (em.Exists(er.Edge) && em.HasComponent<RoadWorksSite>(er.Edge) && em.GetComponentData<RoadWorksSite>(er.Edge).IsUpgrade)
                        { noRuntime++; noRuntimeAt = noRuntimeAt ?? RRWLog.E(er.Edge) + " (no UpgradeEdgeState)"; }
                        continue;
                    }
                    // chain band index
                    if (!es.ChainIndexCurrent(rt) && (!sampled || Age(s_ChainStaleSince, er.Edge, now) >= kChainIndexGraceUpdates))
                    { chainStale++; chainAt = chainAt ?? RRWLog.E(er.Edge) + " p" + p.Id; }
                    // drop key / window
                    string dk = DropKeyProblem(er, v);
                    if (dk != null && (!sampled || Age(s_DropKeySince, er.Edge, now) >= kDropGraceUpdates))
                    { dropKey++; dropKeyAt = dropKeyAt ?? RRWLog.E(er.Edge) + " p" + p.Id + " " + dk; }
                    // drop lanes inside a dropping band of the report's window, with their centres written (only for a current key)
                    if (dk == null && v.IsUpgrade && es.DropWindow != -2 && es.DropLanes.Count > 0)
                    {
                        string bad = DropLaneOutside(em, er, v.Upgrade);
                        if (bad != null) { dropBand++; dropBandAt = dropBandAt ?? RRWLog.E(er.Edge) + " p" + p.Id + " " + bad; }
                        string c = DropCentresProblem(em, er);
                        if (c != null) { dropCentres++; dropCentresAt = dropCentresAt ?? RRWLog.E(er.Edge) + " p" + p.Id + " " + c; }
                    }
                    // no dirt or gravel on a sub-strip that carries traffic
                    if (v.IsUpgrade && es.ChainIndexCurrent(rt))
                    {
                        string d = DirtOnTraffic(er, v);
                        if (d != null) { dirt++; dirtAt = dirtAt ?? RRWLog.E(er.Edge) + " p" + p.Id + " " + d; }
                    }
                }
            }

            if (hidden > 0) problems.Add("uw: " + hidden + " mode H edges are hidden or want to be (first " + hiddenAt + "; mode H never hides the road)");
            if (noRuntime > 0) problems.Add("uw: " + noRuntime + " mode H projects / edges without their runtime upgrade state (first " + noRuntimeAt + "; Director adopt)");
            if (chainStale > 0) problems.Add("uw: " + chainStale + " mode H edges with a stale chain band index for more than one update (first " + chainAt + "; Director SyncChainIndex)");
            if (dropKey > 0) problems.Add("uw: " + dropKey + " mode H edges whose drop report does not match the window (first " + dropKeyAt + "; Traffic)");
            if (dropBand > 0) problems.Add("uw: " + dropBand + " mode H edges drop a lane outside a dropping band of the window (first " + dropBandAt + "; Traffic)");
            if (dropCentres > 0) problems.Add("uw: " + dropCentres + " mode H edges whose drop lane centres are missing or wrong (first " + dropCentresAt + "; Traffic writes them with the drop lanes)");
            if (dirt > 0) problems.Add("uw: " + dirt + " mode H edges allow dirt / gravel on a sub-strip that carries traffic (first " + dirtAt + ")");
            if (!sampled && SiteRegistry.Projects.Count > 0)
                problems.Add("uw: no update sample for " + unchecked(now - s_LastSample) + " updates (DevCameraSystem): transient rows were checked without their grace time");

            CancelledUpgrades(em, problems);
        }

        // A drop lane (Traffic's list) whose measured centre lies in no build / rebuild band of the report's window (AllAtOnce: of it
        // or a later window). Null = every lane fine.
        private static string DropLaneOutside(EntityManager em, EdgeRecord er, in UpgradeView u)
        {
            var es = er.Upgrade;
            if (er.Arc == null) return null;
            int w = es.DropWindowFor(er.GeometryRevision, u);
            if (w < 0) return null;
            for (int i = 0; i < es.DropLanes.Count; i++)
            {
                Entity lane = es.DropLanes[i];
                if (!EcsUtil.Alive(em, lane) || !em.HasComponent<NetCarLane>(lane)) continue;
                if (!EcsUtil.ProbeLane(em, lane, er.Arc, out var probe)) continue;
                if (es.DropBandOf(probe.Centre, w, u.AllAtOnce) < 0)
                    return "lane " + RRWLog.E(lane) + " centre " + DevSites.F(probe.Centre) + " (window " + w + (u.AllAtOnce ? " all-at-once" : "") + ")";
            }
            return null;
        }

        // The stored centres of the drop lanes (UpgradeEdgeState.DropLaneCentres) are missing, or differ from the lanes measured
        // now by more than kDropCentreTolerance. Without them no reader counts any lane as dropped. Null = fine.
        private const float kDropCentreTolerance = 0.25f;
        private static string DropCentresProblem(EntityManager em, EdgeRecord er)
        {
            var es = er.Upgrade;
            if (!es.DropCentresWritten)
                return "centres=" + es.DropLaneCentres.Count + " lanes=" + es.DropLanes.Count;
            if (er.Arc == null) return null;
            for (int i = 0; i < es.DropLanes.Count; i++)
            {
                Entity lane = es.DropLanes[i];
                if (!EcsUtil.LaneCentre(em, lane, er.Arc, out float c)) continue;
                if (math.abs(c - es.DropLaneCentres[i]) > kDropCentreTolerance)
                    return "lane " + RRWLog.E(lane) + " centre stored " + DevSites.F(es.DropLaneCentres[i]) + " measured " + DevSites.F(c);
            }
            return null;
        }

        // Layers that must never lie on a sub-strip that carries cars.
        private static readonly SurfaceLayer[] s_DirtLayers =
        {
            SurfaceLayer.RoadDirt, SurfaceLayer.BaseCourseCover, SurfaceLayer.Subgrade, SurfaceLayer.TopsoilStrip, SurfaceLayer.OldAsphalt,
        };

        // The cover rule (PhasePlan.CoverOf + UpgradeSpans) with the live inputs Surfaces uses: the shared sub-strip rules
        // UpgradeZones.SubStripClosed (applied lane groups with the closure applied on the edge, or a dropped lane with registered
        // blockers) and SubStripParkingOff. Null = no dirt / gravel on a Traffic sub-strip.
        private static string DirtOnTraffic(EdgeRecord er, in ProjectView v)
        {
            var es = er.Upgrade;
            var u = v.Upgrade;
            for (int i = 0; i < es.BandCount; i++)
            {
                int cb = es.ChainIndex[i];
                if (cb < 0) continue;
                var kind = es.Bands[i].Kind;
                var strips = es.SubStrips[i];
                for (int k = 0; k < strips.Count; k++)
                {
                    var s = strips[k];
                    bool closed = UpgradeZones.SubStripClosed(er, u, s, i);
                    var cover = PhasePlan.CoverOf(s, kind, closed, UpgradeZones.SubStripParkingOff(er, s));
                    if (cover != SubStripCover.Traffic) continue;
                    for (int l = 0; l < s_DirtLayers.Length; l++)
                    {
                        PhasePlan.UpgradeSpans(s_DirtLayers[l], v, cb, cover, out var set);
                        if (!set.IsEmpty)
                            return "band " + i + " " + s.Kind + " [" + DevSites.F(s.Lo) + "," + DevSites.F(s.Hi) + "] " + s_DirtLayers[l];
                    }
                }
            }
            return null;
        }

        // Mode 2 sites that went through a cancel: a demolition in mode 2, or a mode 2 construction marked CancelledBuild. Every
        // cancel path refuses mode H (the bulldozer ends the upgrade with EndUpgrade instead).
        private static void CancelledUpgrades(EntityManager em, List<string> problems)
        {
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadWorksSite>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            var ents = q.ToEntityArray(Allocator.Temp);
            var sites = q.ToComponentDataArray<RoadWorksSite>(Allocator.Temp);
            int n = 0;
            string first = null;
            try
            {
                for (int i = 0; i < sites.Length; i++)
                {
                    var s = sites[i];
                    if (s.Mode != VisualMode.HalfWidth) continue;
                    if (s.Kind == WorksKind.Construction && !s.Has(SiteFlags.CancelledBuild)) continue;
                    n++;
                    first = first ?? RRWLog.E(ents[i]) + " p" + s.m_ProjectId + " " + s.Kind + (s.Has(SiteFlags.CancelledBuild) ? " CancelledBuild" : "");
                }
            }
            finally
            {
                ents.Dispose();
                sites.Dispose();
                q.Dispose();
            }
            if (n > 0) problems.Add("uw: " + n + " mode H sites went through an accepted cancel (first " + first + "; every cancel path must refuse mode H)");
        }
    }
}
#endif
