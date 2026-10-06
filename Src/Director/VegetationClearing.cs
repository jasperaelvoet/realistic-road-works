using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Director
{
    // Tree clearing (based on the previous mod version's RoadWorksSiteSystem.ClearVegetation).
    //
    // Footprint (found in game testing): an early version cleared only trunks within HalfWidth + 1.5 m of the centre line,
    // i.e. the road itself. The trees left standing stood in the topsoil strip (HalfWidth + kTopsoilMargin) and their
    // canopies hung over the trench and, after completion, over the sidewalks and the carriageway (vanilla only
    // overrides a tree whose COLLISION shape touches the road, so an overhanging canopy stays visible). Now, with the strip
    // half width S = HalfWidth + kTopsoilMargin (FullDig; HalfWidth otherwise):
    //   - footprint: trunk within S + kClearCanopyAllowance of the centre line;
    //   - overhang: trunkDist - c < S - kClearOverhangTolerance and trunkDist <= S + c, where c is the
    //     canopy radius, capped at kClearCanopyMax (8 m). An earlier rule only counted canopies over the ROAD SURFACE (|lat| < HalfWidth)
    //     and estimated c from the SMALLER search-tree half extent, so canopies over the strip / trench edge stayed in C1.
    //   Canopy radius (verified in the game code, ObjectInitializeSystem): a plant prefab's ObjectGeometryData (and therefore the
    //   static search-tree bounds) is the CHILD mesh size shrunk by 50 % (PlantObject pot coverage rule), i.e. a fraction of the
    //   rendered canopy. c is therefore taken from the rendered mesh: GrowthScaleData.<state>Size (mesh bounds of the tree's
    //   current state) x BatchDataHelpers.CalculateTreeSubMeshData scale (the growth scale the renderer uses), half of the larger
    //   XZ extent; the search-tree half extent (larger axis) is only the fallback / lower bound.
    //   Free-standing trees only (no Owner: building / net / planted sub-objects are never touched), no Temp.
    // Timeline:
    //   C0: every kClearIntervalUpdates per project, [cleared u, PhasePlan.ClearingFront] (the clearing front).
    //   C1/C2: whatever is left up to U in one sweep (also after a load mid-C1), then on the update the project enters C1
    //     or C2 (also a load / dev jump into them) a FULL-CHAIN catch-up sweep with the strip / canopy rule, in that same update
    //     (Director Mod1 210 runs before MachineDirector 240, so before the C1 machines spawn). It deletes only trees of the start
    //     snapshot (a tree the player planted beside the cleared part during C0 is kept, as in the reveal sweep).
    //   First frame at C3+ (the reveal frame, before the road shows): one final full-chain sweep that deletes ONLY the
    //     trees of the start snapshot (ps.TreeSnapshot, taken at ClearInit): a tree the player plants beside the works
    //     after clearing started (e.g. street trees along the new sidewalk during C1-C4) is never deleted here, only
    //     counted and logged.
    //   Completion (construction): count-only check (road already visible: deleting now would pop, and anything left
    //     there was placed by the player or missed); logs a WARN when trees remain, never deletes.
    // Counts are logged at Info (summary lines) so they show without verbose logging; rrw.director.trees
    // (DevTools) lists what is still inside the footprint or reaching the strip (C1 / C2: must be 0).
    public partial class WorksDirectorSystem
    {
        public static long TreesClearedTotal;       // process-wide count (dev state line)

        private void StepClearing()
        {
            var em = EntityManager;
            for (int i = 0; i < m_Projects.Count; i++)
            {
                var proj = m_Projects[i];
                if (proj.Kind != WorksKind.Construction || proj.Edges.Count == 0) continue;
                var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
                float U = proj.ChainLength;
                if (!ps.ClearInit)
                {
                    ps.ClearInit = true;
                    // replaced roads were cleared when the old road was built; after a load in C3+ the road is visible
                    // and the trench long gone: never sweep those
                    bool skip = (proj.Flags & (SiteFlags.Replaced | SiteFlags.CancelledBuild | SiteFlags.Migrated)) != 0
                                || proj.Phase >= WorksPhase.Paving;
                    ps.ClearSkipped = skip;
                    ps.ClearedU = skip ? U : 0f;
                    ps.ClearCrews = 1;
                    ps.FinalSweepDone = skip;
                    ps.LastClearUpdate = m_Now - DirConst.kClearIntervalUpdates;
                    ps.TreeSnapshot = null;
                    if (!skip && U > 0.01f)
                    {
                        // trees that exist now (project start, or the load): the reveal sweep's whitelist
                        ps.TreeSnapshot = new HashSet<Entity>();
                        var ss = default(TreeScanStats);
                        TreeClearing.Scan(em, m_ObjectSearch, proj, 0f, U, false, ref ss, null, ps.TreeSnapshot);
                        RRWLog.Verbose("director: project #" + proj.Id + " tree snapshot: " + ps.TreeSnapshot.Count + " free-standing trees in the footprint");
                    }
                    if (skip) RRWLog.Verbose("director: project #" + proj.Id + " tree clearing skipped (" + proj.Flags + ", " + proj.Phase + ")");
                }
                if (ps.ClearSkipped || U <= 0.01f) continue;

                if (proj.Phase >= WorksPhase.Paving)
                {
                    if (ps.FinalSweepDone) continue;
                    // Reveal frame (or the first frame a dev jump lands in C3+): last full sweep while the road has
                    // not been rendered yet
                    ps.FinalSweepDone = true;
                    var fs = default(TreeScanStats);
                    // delete only trees of the start snapshot; without one (should not happen) count only
                    var snap = ps.TreeSnapshot;
                    TreeClearing.Scan(em, m_ObjectSearch, proj, 0f, U, snap != null, ref fs, null, null, snap);
                    AddCleared(ps, fs, false);
                    ps.TreesLateKept = fs.LateKept + (snap == null ? fs.Found : 0);
                    ps.TreeSnapshot = null;   // no further deletes for this project
                    ps.ClearedU = U;
                    RRWLog.Info("director: project #" + proj.Id + " final tree sweep before the reveal: " + fs.Deleted + " more removed ("
                                + fs.Overhang + " overhanging), total " + ps.TreesDeleted + " trees, " + fs.OwnedKept + " owned trees kept, "
                                + ps.TreesLateKept + " trees placed after the start kept" + (snap == null ? " (no start snapshot: count only)" : ""));
                    continue;
                }

                if (proj.Phase == WorksPhase.Survey)
                {
                    ps.C1SweepDone = false;   // (a dev jump back to C0 re-arms the C1 entry sweep)
                    if (ps.ClearedU >= U - 0.01f || m_Now - ps.LastClearUpdate < DirConst.kClearIntervalUpdates) continue;
                    ProgressiveSweep(proj, ps, U, PhasePlan.ClearingFront(proj.PhaseFraction, U));
                    continue;
                }
                // C1 / C2 (hidden): everything left of the progressive clearing, then once the full-chain catch-up sweep
                if (ps.ClearedU < U - 0.01f) ProgressiveSweep(proj, ps, U, U);
                if (!ps.C1SweepDone && (proj.Phase == WorksPhase.Excavation || proj.Phase == WorksPhase.Foundation))
                {
                    ps.C1SweepDone = true;
                    var cs = default(TreeScanStats);
                    TreeClearing.Scan(em, m_ObjectSearch, proj, 0f, U, true, ref cs, null, null, ps.TreeSnapshot);
                    AddCleared(ps, cs, false);
                    ps.C1SweepDeleted = cs.Deleted;
                    RRWLog.Info("director: project #" + proj.Id + " " + proj.Phase + " entry tree sweep (full chain, strip + canopy rule): " + cs.Deleted
                                + " more removed (" + cs.Overhang + " overhanging the strip), " + cs.LateKept + " placed after the start kept, "
                                + cs.OwnedKept + " owned kept, total " + ps.TreesDeleted + " trees, " + cs.Candidates + " candidates");
                }
            }
        }

        // Progressive clearing of [ClearedU, target] (one-crew-equivalent front per crew section). C0 at the clearing front,
        // C1 / C2 everything left (target = U). Deletes every free-standing match (the span was never cleared before).
        private void ProgressiveSweep(ProjectRecord proj, DirProjectState ps, float U, float target)
        {
            var em = EntityManager;
            // The clearing front stays a chain-level TIMELINE (ClearingFront), but with crew sections every
            // section clears its own [B_i, B_i + len_i * s] (s = target / U), so trees are always gone ahead of each crew's topsoil
            // front (C0 F_i = B_i + len_i * Sweep(f, .15, .95) <= that). The C0 layout is the latched ProjectRecord.Crews; a
            // re-latch mid-C0 re-scans the new sections from their starts once (deleted trees are gone: only the cost repeats).
            // "Everything left" (C1/C2) scans the rest of the layout that was cleared so far.
            bool rest = target >= U - 0.01f;
            int n = rest ? ps.ClearCrews : math.clamp(proj.Crews, 1, RRWConst.kMaxCrewsPerProject);
            bool relayout = n != ps.ClearCrews && ps.ClearedU > 0.01f;
            if (!relayout && target <= ps.ClearedU + 0.5f && !rest) return;
            var st = default(TreeScanStats);
            if (n <= 1 && !relayout) TreeClearing.Scan(em, m_ObjectSearch, proj, ps.ClearedU, target, true, ref st, null);   // one section: a single span
            else
            {
                float s0 = math.saturate(ps.ClearedU / U), s1 = math.saturate(target / U);
                for (int c = 0; c < n; c++)
                {
                    float a = PhasePlan.SectionStart(c, n, U), b = PhasePlan.SectionStart(c + 1, n, U);
                    float from = relayout ? a : a + (b - a) * s0;
                    float to = rest ? b : a + (b - a) * s1;
                    if (to > from + 1e-3f) TreeClearing.Scan(em, m_ObjectSearch, proj, from, to, true, ref st, null);
                }
                if (relayout)
                    RRWLog.Verbose("director: project #" + proj.Id + " tree clearing re-laid out " + ps.ClearCrews + " -> " + n + " crew sections at s=" + RRWLog.F(s1));
            }
            AddCleared(ps, st, !relayout);
            if (st.Deleted > 0 && RRWLog.VerboseEnabled)
                RRWLog.Verbose("director: project #" + proj.Id + " cleared " + st.Deleted + " trees (" + st.Overhang + " overhanging) u=["
                               + RRWLog.F(ps.ClearedU) + "," + RRWLog.F(target) + "]" + (n > 1 ? " in each of " + n + " crew sections (unit)" : ""));
            ps.ClearedU = target;
            ps.ClearCrews = n;
            ps.LastClearUpdate = m_Now;
            if (target >= U - 0.01f)
                RRWLog.Info("director: project #" + proj.Id + " tree clearing reached the chain end (" + proj.Phase + " f=" + RRWLog.F(proj.PhaseFraction)
                            + "): " + ps.TreesDeleted + " trees removed (" + ps.TreesOverhang + " overhanging), " + ps.TreesOwnedKept
                            + " owned trees kept, footprint +-" + RRWLog.F(TreeClearing.MaxFootprint(proj)) + " m, strip +-" + RRWLog.F(TreeClearing.MaxStrip(proj)) + " m");
        }

        // countOwned: only the progressive sweeps (disjoint spans) count owned trees; full re-scans would double them
        private static void AddCleared(DirProjectState ps, in TreeScanStats s, bool countOwned = true)
        {
            ps.TreesDeleted += s.Deleted;
            ps.TreesOverhang += s.Overhang;
            if (countOwned) ps.TreesOwnedKept += s.OwnedKept;
            TreesClearedTotal += s.Deleted;
        }

        // Construction completion (frame N + 1, road visible since the reveal): count-only. A tree still in the footprint now was
        // placed by the player after the start (kept on purpose) or missed by the sweeps; deleting it on a visible road
        // would pop and could remove player content, so it is only logged.
        private void CompletionTreeCheck(ProjectRecord proj)
        {
            if (proj.Kind != WorksKind.Construction) return;
            var ps = proj.Get<DirProjectState>(ModuleSlot.Director);
            if (ps == null || ps.ClearSkipped || !ps.ClearInit) return;
            ps.TreeSnapshot = null;
            var s = default(TreeScanStats);
            TreeClearing.Scan(EntityManager, m_ObjectSearch, proj, 0f, proj.ChainLength, false, ref s, null);
            if (s.Found > 0)
                RRWLog.Warn("director: project #" + proj.Id + " completion tree check: " + s.Found + " free-standing trees in the footprint ("
                            + s.Overhang + " overhanging) left alone (" + ps.TreesLateKept + " known as placed after the start); total removed " + ps.TreesDeleted);
            else
                RRWLog.Info("director: project #" + proj.Id + " completion tree check: 0 trees in the footprint (total removed " + ps.TreesDeleted + ")");
        }
    }

    internal struct TreeScanStats
    {
        public int Candidates;   // search-tree items in the span boxes
        public int Footprint;    // matched: trunk inside the footprint
        public int Overhang;     // matched: trunk outside the footprint, canopy over the strip (overhang rule)
        public int OwnedKept;    // would match but has an Owner (never touched)
        public int Deleted;      // marked Deleted (delete mode)
        public int Found;        // matched and left alone (count mode)
        public int LateKept;     // matched in delete mode but not in the onlyListed set (placed after the snapshot): kept
    }

    internal struct TreeHit
    {
        public Entity Tree;
        public float3 Position;
        public float Lateral;    // distance of the trunk from the centre line
        public float Canopy;     // canopy radius (rendered mesh of the tree's state x growth scale, capped)
        public float Strip;      // strip half width S of the edge (HalfWidth + kTopsoilMargin in FullDig)
        public bool Owned, Overridden, Overhang;
        public uint ProjectId;
    }

    // Main-thread tree scan over a project's chain span (Director clearing + dev command). Uses the static object
    // search tree (trees are Static objects; Overridden ones stay in the tree with their bounds).
    internal static class TreeClearing
    {
        private struct TreeCollector : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds2 m_Bounds;
            public NativeList<Entity> m_Result;
            public NativeList<float> m_Canopy;
            public bool Intersect(QuadTreeBoundsXZ bounds) => MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds);
            public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
            {
                if (!MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds)) return;
                m_Result.Add(item);
                float2 ext = (bounds.m_Bounds.max.xz - bounds.m_Bounds.min.xz) * 0.5f;
                m_Canopy.Add(math.cmax(ext));   // the LARGER half extent (fallback / lower bound of the canopy radius)
            }
        }

        private static readonly HashSet<Entity> s_Seen = new HashSet<Entity>();

        // Trunk footprint half width of an edge (from the centre line).
        public static float Footprint(in EdgeSection sec, VisualMode mode) =>
            sec.HalfWidth + (mode == VisualMode.FullDig ? RRWConst.kTopsoilMargin : 0f) + DirConst.kClearCanopyAllowance;

        // Strip half width S (the topsoil strip / trench edge in FullDig; the road surface otherwise).
        public static float Strip(in EdgeSection sec, VisualMode mode) =>
            math.max(1f, sec.HalfWidth) + (mode == VisualMode.FullDig ? RRWConst.kTopsoilMargin : 0f);

        public static float MaxStrip(ProjectRecord proj)
        {
            float w = 0f;
            for (int i = 0; i < proj.Edges.Count; i++)
                if (SiteRegistry.TryGetEdge(proj.Edges[i], out var rec)) w = math.max(w, Strip(rec.Section, proj.Mode));
            return w;
        }

        // Canopy radius of a tree as RENDERED. The search-tree bounds of a plant are its prefab's ObjectGeometryData
        // = the CHILD mesh bounds shrunk by 50 % (ObjectInitializeSystem, PlantObject pot-coverage rule, as in the game code), far
        // smaller than a grown canopy. The renderer picks the sub-mesh of the tree's state (Tree.m_State) and scales it by
        // BatchDataHelpers.CalculateTreeSubMeshData(tree, GrowthScaleData) (the growth lerp); GrowthScaleData.<state>Size is that
        // state's mesh bounds size. Radius = half the larger XZ extent x scale; the search-tree half extent is the lower bound.
        public static float CanopyRadius(EntityManager em, Entity t, float boundsHalfExtent)
        {
            float r = float.IsNaN(boundsHalfExtent) ? 0f : math.max(0f, boundsHalfExtent);
            try
            {
                if (em.HasComponent<Game.Objects.Tree>(t) && em.HasComponent<PrefabRef>(t))
                {
                    Entity prefab = em.GetComponentData<PrefabRef>(t).m_Prefab;
                    if (prefab != Entity.Null && em.HasComponent<GrowthScaleData>(prefab))
                    {
                        var tree = em.GetComponentData<Game.Objects.Tree>(t);
                        var g = em.GetComponentData<GrowthScaleData>(prefab);
                        Game.Rendering.BatchDataHelpers.CalculateTreeSubMeshData(tree, g, out float3 scale);
                        float3 size;
                        switch (tree.m_State & (Game.Objects.TreeState.Teen | Game.Objects.TreeState.Adult | Game.Objects.TreeState.Elderly | Game.Objects.TreeState.Dead | Game.Objects.TreeState.Stump))
                        {
                            case Game.Objects.TreeState.Teen: size = g.m_TeenSize; break;
                            case Game.Objects.TreeState.Adult: size = g.m_AdultSize; break;
                            case Game.Objects.TreeState.Elderly: size = g.m_ElderlySize; break;
                            case Game.Objects.TreeState.Dead: size = g.m_DeadSize; break;
                            case Game.Objects.TreeState.Stump: size = default; break;   // no canopy (the trunk / footprint rule still applies)
                            default: size = g.m_ChildSize; break;
                        }
                        if ((tree.m_State & Game.Objects.TreeState.Stump) == 0 && !(math.cmax(size.xz) > 0f)) { size = g.m_AdultSize; scale = 1f; }
                        if (!math.all(math.isfinite(scale)) || math.cmin(scale.xz) <= 0f) scale = 1f;
                        float m = 0.5f * math.cmax(size.xz * scale.xz);
                        if (m > r && !float.IsInfinity(m)) r = m;
                    }
                }
            }
            catch (System.Exception e) { RRWLog.ErrorOnce("director tree canopy", e); }
            return math.min(r, DirConst.kClearCanopyMax);
        }

        public static float MaxFootprint(ProjectRecord proj)
        {
            float w = 0f;
            for (int i = 0; i < proj.Edges.Count; i++)
                if (SiteRegistry.TryGetEdge(proj.Edges[i], out var rec)) w = math.max(w, Footprint(rec.Section, proj.Mode));
            return w;
        }

        // Scans [u0, u1] of the project chain. delete: mark matching free-standing trees Deleted; else count them
        // (stats.Found) and optionally report them (hits). collect: receives every matching free-standing tree.
        // onlyListed (delete mode): only trees in this set are deleted; other matches are kept and counted (stats.LateKept).
        public static void Scan(EntityManager em, Game.Objects.SearchSystem search, ProjectRecord proj, float u0, float u1, bool delete,
                                ref TreeScanStats stats, List<TreeHit> hits, HashSet<Entity> collect = null, HashSet<Entity> onlyListed = null)
        {
            if (search == null || proj == null || u1 < u0) return;
            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = default;
            bool haveTree = false;
            s_Seen.Clear();
            try
            {
                for (int i = 0; i < proj.Edges.Count; i++)
                {
                    Entity e = proj.Edges[i];
                    if (!SiteRegistry.TryGetEdge(e, out var rec) || rec.Arc == null || !em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                    var site = em.GetComponentData<RoadWorksSite>(e);
                    float lo = site.ChainLo, hi = site.ChainHi;
                    if (u1 < lo || u0 > hi) continue;
                    var arc = rec.Arc;
                    float len = arc.Length;
                    float sa = PhasePlan.EdgeS(math.max(u0, lo), site.m_ChainU0, site.m_ChainU1, len);
                    float sb = PhasePlan.EdgeS(math.min(u1, hi), site.m_ChainU0, site.m_ChainU1, len);
                    // edge-local window (EdgeS clamps to [0, len]; trees around a node project onto the end and are
                    // matched within reach of it)
                    float s0 = math.min(sa, sb), s1 = math.max(sa, sb);
                    float foot = Footprint(rec.Section, proj.Mode);
                    // Overhang over the STRIP (S), trunks searched up to S + kClearCanopyMax (+ 1 m: the collector
                    // matches the item bounds, which are centred on the trunk)
                    float strip = Strip(rec.Section, proj.Mode);
                    float reach = math.max(foot, strip + DirConst.kClearCanopyMax) + 1f;

                    float2 mn = new float2(float.MaxValue), mx = new float2(float.MinValue);
                    for (float s = s0; ; s += 4f)
                    {
                        float sc = math.min(s, s1);
                        float3 p = arc.Position(sc);
                        mn = math.min(mn, p.xz);
                        mx = math.max(mx, p.xz);
                        if (sc >= s1) break;
                    }
                    if (!haveTree)
                    {
                        tree = search.GetStaticSearchTree(true, out JobHandle deps);
                        deps.Complete();
                        haveTree = true;
                    }
                    var col = new TreeCollector
                    {
                        // items are matched by their (canopy) bounds; the trunk test below decides
                        m_Bounds = new Bounds2(mn - reach, mx + reach),
                        m_Result = new NativeList<Entity>(64, Allocator.Temp),
                        m_Canopy = new NativeList<float>(64, Allocator.Temp),
                    };
                    try
                    {
                        tree.Iterate(ref col);
                        stats.Candidates += col.m_Result.Length;
                        for (int k = 0; k < col.m_Result.Length; k++)
                        {
                            Entity t = col.m_Result[k];
                            if (s_Seen.Contains(t)) continue;
                            if (!em.Exists(t) || !em.HasComponent<Game.Objects.Tree>(t) || em.HasComponent<Deleted>(t) || em.HasComponent<Game.Tools.Temp>(t)) continue;
                            if (!em.HasComponent<Game.Objects.Transform>(t)) continue;
                            float3 pos = em.GetComponentData<Game.Objects.Transform>(t).m_Position;
                            float st = arc.Project(pos);
                            if (st < s0 - 0.5f || st > s1 + 0.5f) continue;
                            float d = math.distance(arc.Position(st).xz, pos.xz);
                            bool inFoot = d <= foot;
                            if (!inFoot && d > strip + DirConst.kClearCanopyMax) continue;   // cannot reach the strip with any canopy
                            float canopy = inFoot ? math.min(col.m_Canopy[k], DirConst.kClearCanopyMax) : CanopyRadius(em, t, col.m_Canopy[k]);
                            bool overhang = !inFoot && d <= strip + canopy && d - canopy < strip - DirConst.kClearOverhangTolerance;
                            if (!inFoot && !overhang) continue;
                            s_Seen.Add(t);
                            bool owned = em.HasComponent<Owner>(t);
                            if (hits != null && hits.Count < 64)
                                hits.Add(new TreeHit
                                {
                                    Tree = t, Position = pos, Lateral = d, Canopy = inFoot ? CanopyRadius(em, t, col.m_Canopy[k]) : canopy, Strip = strip, Owned = owned,
                                    Overridden = em.HasComponent<Overridden>(t), Overhang = overhang, ProjectId = proj.Id,
                                });
                            if (owned) { stats.OwnedKept++; continue; }
                            collect?.Add(t);
                            if (delete && onlyListed != null && !onlyListed.Contains(t))
                            {
                                stats.LateKept++;   // not in the start snapshot: placed later (player content), never deleted
                                continue;
                            }
                            if (inFoot) stats.Footprint++; else stats.Overhang++;
                            if (delete)
                            {
                                em.AddComponent<Deleted>(t);
                                stats.Deleted++;
                            }
                            else stats.Found++;
                        }
                    }
                    finally
                    {
                        col.m_Result.Dispose();
                        col.m_Canopy.Dispose();
                    }
                }
            }
            finally
            {
                if (haveTree) search.AddStaticSearchTreeReader(default(JobHandle));
                s_Seen.Clear();
            }
        }
    }
}
