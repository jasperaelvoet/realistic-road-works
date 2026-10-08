using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Tooling
{
    // ApplyTool, right before ApplyNetSystem turns the tool's Temp edges into real roads (update order 110).
    // ApplyNetSystem: Delete -> original deleted; Replace|Combine -> original Deleted and the
    // TEMP entity Created; other temps with an original -> original updated, temp deleted; no original -> temp Created.
    // So RoadWorksSite goes on the temp whenever the temp will be Created (the previous mod version's tagging path,
    // verified in game); only rule 5 (upgrade keeping the original) writes to an original.
    //   1 split remnant   (Create, no original, lies on a same-prefab original)  -> copy of the source's site, re-mapped
    //   2 combine         (Combine + original; node reduction)                    -> merged site (same project) or a new single-edge project
    //   3 replace         (Replace + original)                                    -> inherit the works, re-upgrade a mode H site, or classify
    //                                                                                the new road type (UpgradeTagger, site on the temp)
    //   4 new             (Create, no original)                                   -> new construction (SiteFactory, one project per chain)
    //   5 modify/upgrade  (original kept)                                         -> demolition: the cost is credited back; mode H: re-upgrade;
    //                                                                                other works: cost added to m_PaidCost; no works: classify
    //                                                                                (UpgradeTagger, site on the original)
    // Classified changes follow the "Road upgrades" setting: partial upgrade works (mode H, one project per drag chain), a full
    // rebuild (Replaced construction) or nothing (cosmetic / instant). Drawing a road along an existing one creates nothing in
    // the base game, so new pieces stay rule 4. A mode H site is never hidden.
    // The Director's PreviewHideSystem hides Temp copies of hidden works roads with TempFlags.Hidden
    // (never the Hidden component, which would drop them out of vanilla's junction topology). Such temps are classified
    // like any other (vanilla only makes Delete|Hidden net temps: the node-reduction partner, skipped with Delete). A
    // preview-hidden temp that ApplyNet will CREATE (split remnant, replace, combine, new split node) gets the Hidden
    // component (+BatchesUpdated) here, right before ApplyNetSystem, so the permanent entity is hidden in the apply frame
    // exactly as before (ApplyNetSystem.Create only removes Temp; LaneHidden/SubObjectHidden follow at Mod5). The
    // Director adopts it at Mod1 (DirectorShared.PreviewHidden membership + Hidden).
    [RegisterSystem(SystemUpdatePhase.ApplyTool, Before = typeof(ApplyNetSystem), Order = RRWOrder.WorksTag)]
    public partial class WorksTagSystem : GameSystemBase
    {
        private ToolSystem m_ToolSystem;
        private CitySystem m_CitySystem;
        private EntityQuery m_TempEdges;
        private EntityQuery m_TempNodes;
        private EntityQuery m_Sites;
        private readonly RRWGuard m_Guard = new RRWGuard("tools WorksTag");

        // per-apply scratch (reused, never per frame: this only runs on apply frames)
        private readonly Dictionary<Entity, EdgeArc> m_Arcs = new Dictionary<Entity, EdgeArc>();
        private readonly List<Entity> m_Sources = new List<Entity>();
        private readonly List<SiteFactoryEdge> m_New = new List<SiteFactoryEdge>();
        private readonly List<SiteFactoryEdge> m_Replaced = new List<SiteFactoryEdge>();
        private readonly List<SiteFactoryResult> m_Results = new List<SiteFactoryResult>();
        private readonly List<Entity> m_CombineOriginals = new List<Entity>();
        private readonly List<RoadWorksSite> m_CombineSites = new List<RoadWorksSite>();
        private readonly List<Entity> m_CombineSiteEdges = new List<Entity>();
        private readonly List<Entity> m_HideOnApply = new List<Entity>();
        private readonly UpgradeTagger m_Upgrades = new UpgradeTagger();

        // dev: last apply summary (rrw.tools.last)
        public static string LastSummary = "none";

        private int m_Split, m_SplitPlain, m_Combined, m_CombinedNew, m_ReplaceInherit, m_ReplaceUpgrade, m_UpgradePaid, m_SkippedOutside;
        private int m_PreviewHiddenTemps, m_HiddenOnApplyEdges, m_HiddenOnApplyNodes, m_PreviewHiddenNew;
        private int m_DemolitionCredit, m_CombineEndedH, m_CombineRefund;

        // dev counters (rrw.tools.last): totals since load of preview-hidden temps classified / hidden on apply
        public static int TotalPreviewHiddenTemps, TotalHiddenOnApply;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_TempEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Road>(),
                    ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Owner>() },
            });
            m_TempNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Net.Node>() },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_Sites = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadWorksSite>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
            RequireForUpdate(m_TempEdges);
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            TotalPreviewHiddenTemps = TotalHiddenOnApply = 0;
        }

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted) return;
            if (!ToolUtil.InGame() || !m_ToolSystem.actionMode.IsGame() || m_TempEdges.IsEmptyIgnoreFilter) return;
            long t0 = RRWPerf.Start();
            try
            {
                Process();
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
            }
            finally
            {
                m_Arcs.Clear();
                m_HideOnApply.Clear();
                RRWPerf.Stop(PerfSlot.Tools, t0);
            }
        }

        private void Process()
        {
            var settings = RRWSettings.Current;
            if (settings == null) { RRWLog.Once("tools-nosettings", "tools: settings not registered, new roads are built instantly"); return; }
            var em = EntityManager;
            var entities = m_TempEdges.ToEntityArray(Allocator.Temp);
            var temps = m_TempEdges.ToComponentDataArray<Temp>(Allocator.Temp);
            try
            {
                m_New.Clear();
                m_Replaced.Clear();
                m_Split = m_SplitPlain = m_Combined = m_CombinedNew = m_ReplaceInherit = m_ReplaceUpgrade = m_UpgradePaid = m_SkippedOutside = 0;
                m_PreviewHiddenTemps = m_HiddenOnApplyEdges = m_HiddenOnApplyNodes = m_PreviewHiddenNew = 0;
                m_DemolitionCredit = m_CombineEndedH = m_CombineRefund = 0;
                m_HideOnApply.Clear();
                m_Upgrades.Begin();
                CollectSources(entities, temps);

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    Temp t = temps[i];
                    TempFlags fl = t.m_Flags;
                    // Delete|Hidden = node-reduction partner (vanilla); a non-Delete Hidden road temp is a preview the
                    // Director hid and is classified normally (skipping it made split remnants lose their site).
                    if (ToolUtil.Has(fl, TempFlags.Cancel | TempFlags.Delete)) continue;
                    bool previewHidden = ToolUtil.Has(fl, TempFlags.Hidden);
                    if (previewHidden) m_PreviewHiddenTemps++;
                    if (em.HasComponent<RoadWorksSite>(e)) continue;   // already classified (never expected)
                    bool hasOrig = t.m_Original != Entity.Null && em.Exists(t.m_Original);
                    if (ToolUtil.TouchesOutsideConnection(em, e)) { m_SkippedOutside++; continue; }

                    if (ToolUtil.Has(fl, TempFlags.Combine) && hasOrig)
                    {
                        if (HandleCombine(e, t, entities, temps, settings) && previewHidden) m_HideOnApply.Add(e);
                        continue;
                    }
                    if (ToolUtil.Has(fl, TempFlags.Replace) && hasOrig)
                    {
                        if (HandleReplace(e, t, settings) && previewHidden) m_HideOnApply.Add(e);
                        continue;
                    }
                    if (!hasOrig)
                    {
                        if (TryFindSplitSource(e, out Entity source))
                        {
                            if (HandleSplit(e, source) && previewHidden) m_HideOnApply.Add(e);
                            continue;
                        }
                        if (ToolUtil.Has(fl, TempFlags.Create))
                        {
                            // A preview-hidden temp without a works source: the PreviewHide matcher took the player's own
                            // new road for a remnant (an over-match). It is still a new construction and stays visible.
                            if (previewHidden) m_PreviewHiddenNew++;
                            m_New.Add(ToolUtil.FactoryEdge(em, e, t.m_Cost, false));
                        }
                        continue;
                    }
                    HandleModify(e, t, settings);
                }

                HideOnApply();

                int created = Create(m_New, SiteFlags.None, settings);
                int replaced = Create(m_Replaced, SiteFlags.Replaced, settings);
                m_Upgrades.Flush(EntityManager, settings, m_Sites);
                if (RRWGates.UpgradeDefer && m_Upgrades.DeferTargets.Count > 0) SnapshotForDefer(entities, temps);

                if (created + replaced + m_Split + m_Combined + m_CombinedNew + m_ReplaceInherit + m_UpgradePaid > 0 || m_SkippedOutside > 0
                    || m_PreviewHiddenTemps > 0 || m_Upgrades.Any || m_DemolitionCredit + m_CombineEndedH > 0)
                {
                    LastSummary = "new=" + created + " replaced=" + replaced + " split=" + m_Split + " splitPlain=" + m_SplitPlain
                                  + " combine=" + m_Combined + " combineNew=" + m_CombinedNew + " replaceInherit=" + m_ReplaceInherit
                                  + " upgradeNoWorks=" + m_ReplaceUpgrade + " upgradePaid=" + m_UpgradePaid + m_Upgrades.Summary()
                                  + ToolUtil.Summary(m_DemolitionCredit, "demolitionUpgradeCredited")
                                  + ToolUtil.Summary(m_CombineEndedH, "combineEndedUpgrade") + ToolUtil.Summary(m_CombineRefund, "combineRefund")
                                  + " outside=" + m_SkippedOutside
                                  + " previewHidden=" + m_PreviewHiddenTemps + " hiddenOnApply=" + m_HiddenOnApplyEdges + "e/" + m_HiddenOnApplyNodes + "n"
                                  + (m_PreviewHiddenNew > 0 ? " previewHiddenNewRoad=" + m_PreviewHiddenNew : "");
                    RRWLog.Info("tools: apply " + LastSummary);
                    if (m_PreviewHiddenNew > 0)
                        RRWLog.Warn("tools: " + m_PreviewHiddenNew + " new road temp(s) were preview-hidden without a works source (PreviewHide over-match); built as new constructions");
                }
            }
            finally
            {
                entities.Dispose();
                temps.Dispose();
            }
        }

        // Deferred upgrades: the old state of every road piece and node this apply modifies, with the state the tool gives them,
        // for DeferredNetSystem to put the old road back at the next Modification1 (the works keep it until their finishing).
        // Only a plain modification is deferred: no temp of the apply creates, deletes, replaces or combines anything.
        private void SnapshotForDefer(NativeArray<Entity> entities, NativeArray<Temp> temps)
        {
            var em = EntityManager;
            const TempFlags kStructural = TempFlags.Create | TempFlags.Delete | TempFlags.Replace | TempFlags.Combine;
            for (int i = 0; i < temps.Length; i++)
                if ((temps[i].m_Flags & kStructural) != 0 || temps[i].m_Original == Entity.Null) { RRWLog.Verbose("tools: upgrade not deferred (the apply changes the network structure)"); return; }
            var nodes = m_TempNodes.IsEmptyIgnoreFilter ? default : m_TempNodes.ToEntityArray(Allocator.Temp);
            var nt = m_TempNodes.IsEmptyIgnoreFilter ? default : m_TempNodes.ToComponentDataArray<Temp>(Allocator.Temp);
            try
            {
                if (nodes.IsCreated)
                    for (int i = 0; i < nt.Length; i++)
                        if ((nt[i].m_Flags & kStructural) != 0 || nt[i].m_Original == Entity.Null) { RRWLog.Verbose("tools: upgrade not deferred (a node is created / replaced)"); return; }
                int start = DeferredNet.PendingRevert.Count;
                Entity anySite = Entity.Null;
                foreach (var kv in m_Upgrades.DeferTargets) { anySite = kv.Key; break; }
                // an edge the tool turned round (its start / end swapped with the curve) is not deferred: the works and the chain
                // read start / end in the new road's direction, which the old curve does not have
                for (int i = 0; i < entities.Length; i++)
                {
                    var orig = temps[i].m_Original;
                    if (!em.HasComponent<Edge>(entities[i]) || !em.HasComponent<Edge>(orig)) continue;
                    var te = em.GetComponentData<Edge>(entities[i]);
                    var oe = em.GetComponentData<Edge>(orig);
                    Entity ts = em.HasComponent<Temp>(te.m_Start) ? em.GetComponentData<Temp>(te.m_Start).m_Original : te.m_Start;
                    Entity tEnd = em.HasComponent<Temp>(te.m_End) ? em.GetComponentData<Temp>(te.m_End).m_Original : te.m_End;
                    if (ts == oe.m_End && tEnd == oe.m_Start)
                    {
                        RRWLog.Info("tools: upgrade not deferred (the tool turned road piece " + RRWLog.E(orig) + " round): the new road is placed at once");
                        DeferredNet.PendingRevert.RemoveRange(start, DeferredNet.PendingRevert.Count - start);
                        return;
                    }
                }
                for (int i = 0; i < entities.Length; i++)
                {
                    var temp = entities[i];
                    var orig = temps[i].m_Original;
                    if (!em.Exists(orig) || !em.HasComponent<Curve>(orig) || !em.HasComponent<Curve>(temp)) continue;
                    var snap = Snap(em, orig, temp, DeferredKind.Edge);
                    snap.OldCurve = em.GetComponentData<Curve>(orig).m_Bezier;
                    snap.Target.m_Curve = em.GetComponentData<Curve>(temp).m_Bezier;
                    if (m_Upgrades.DeferTargets.TryGetValue(orig, out var layout)) snap.Target.SetLayout(layout);
                    snap.SiteEdge = m_Upgrades.DeferTargets.ContainsKey(orig) ? orig : anySite;
                    DeferredNet.PendingRevert.Add(snap);
                }
                if (nodes.IsCreated)
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        var temp = nodes[i];
                        var orig = nt[i].m_Original;
                        if (!em.Exists(orig) || !em.HasComponent<Node>(orig) || !em.HasComponent<Node>(temp)) continue;
                        var snap = Snap(em, orig, temp, DeferredKind.Node);
                        var on = em.GetComponentData<Node>(orig);
                        var tn = em.GetComponentData<Node>(temp);
                        snap.OldPosition = on.m_Position; snap.OldRotation = on.m_Rotation;
                        snap.Target.m_Position = tn.m_Position; snap.Target.m_Rotation = tn.m_Rotation;
                        snap.SiteEdge = anySite;
                        DeferredNet.PendingRevert.Add(snap);
                    }
                RRWLog.Info("tools: upgrade deferred: " + (DeferredNet.PendingRevert.Count - start) + " road piece(s) / node(s) keep their old state until the works' finishing");
            }
            finally
            {
                if (nodes.IsCreated) nodes.Dispose();
                if (nt.IsCreated) nt.Dispose();
            }
        }

        private static DeferredNet.Snapshot Snap(EntityManager em, Entity orig, Entity temp, DeferredKind kind)
        {
            var s = new DeferredNet.Snapshot { Entity = orig, Kind = kind };
            s.OldPrefab = em.HasComponent<PrefabRef>(orig) ? em.GetComponentData<PrefabRef>(orig).m_Prefab : Entity.Null;
            s.OldHasUpgraded = em.HasComponent<Upgraded>(orig);
            if (s.OldHasUpgraded) s.OldUpgraded = em.GetComponentData<Upgraded>(orig).m_Flags;
            s.OldHasElevation = em.HasComponent<Game.Net.Elevation>(orig);
            if (s.OldHasElevation) s.OldElevation = em.GetComponentData<Game.Net.Elevation>(orig).m_Elevation;
            s.Target.m_Kind = (byte)kind;
            s.Target.m_Prefab = em.HasComponent<PrefabRef>(temp) ? em.GetComponentData<PrefabRef>(temp).m_Prefab : s.OldPrefab;
            s.Target.m_HasUpgraded = em.HasComponent<Upgraded>(temp);
            if (s.Target.m_HasUpgraded) s.Target.m_Upgraded = em.GetComponentData<Upgraded>(temp).m_Flags;
            s.Target.m_HasElevation = em.HasComponent<Game.Net.Elevation>(temp);
            if (s.Target.m_HasElevation) s.Target.m_Elevation = em.GetComponentData<Game.Net.Elevation>(temp).m_Elevation;
            return s;
        }

        // Originals touched by this apply (split / combine sources): every temp edge's m_Original that is a live road edge,
        // plus every Replace temp node's edge original (vanilla mid-edge split).
        private void CollectSources(NativeArray<Entity> entities, NativeArray<Temp> temps)
        {
            var em = EntityManager;
            m_Sources.Clear();
            for (int i = 0; i < temps.Length; i++)
            {
                Entity o = temps[i].m_Original;
                if (o == Entity.Null || !em.Exists(o) || !em.HasComponent<Curve>(o) || !em.HasComponent<Road>(o) || !em.HasComponent<Edge>(o)) continue;
                if (em.HasComponent<Temp>(o)) continue;
                if (!m_Sources.Contains(o)) m_Sources.Add(o);
            }
            // Mid-edge split: vanilla's split makes no temp edge copy of the split edge. The source is the
            // m_Original of a Temp NODE with TempFlags.Replace (GenerateNodesSystem); the pieces carry only
            // TempFlags.Essential and no original (GenerateEdgesSystem) and are CREATED by ApplyNetSystem
            // while the Replace node deletes the source. Without this the works on the split edge vanished.
            if (m_TempNodes.IsEmptyIgnoreFilter) return;
            var nodes = m_TempNodes.ToEntityArray(Allocator.Temp);
            var nt = m_TempNodes.ToComponentDataArray<Temp>(Allocator.Temp);
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (!ToolUtil.Has(nt[i].m_Flags, TempFlags.Replace)) continue;
                    Entity o = nt[i].m_Original;
                    if (o == Entity.Null || !em.Exists(o) || !em.HasComponent<Curve>(o) || !em.HasComponent<Road>(o) || !em.HasComponent<Edge>(o)) continue;
                    if (em.HasComponent<Temp>(o)) continue;
                    if (!m_Sources.Contains(o)) m_Sources.Add(o);
                }
            }
            finally
            {
                nodes.Dispose();
                nt.Dispose();
            }
        }

        private EdgeArc ArcOf(Entity original)
        {
            if (!m_Arcs.TryGetValue(original, out var arc))
            {
                arc = new EdgeArc(EntityManager.GetComponentData<Curve>(original).m_Bezier);
                m_Arcs.Add(original, arc);
            }
            return arc;
        }

        // ---------------------------------------------------------------- rule 1: split remnants

        // A new piece lying on an original of the same prefab (3 curve points within kSplitMatchTolerance). Originals
        // with a site win over plain ones (a remnant can only lie on one road anyway).
        private bool TryFindSplitSource(Entity created, out Entity source)
        {
            var em = EntityManager;
            source = Entity.Null;
            var curve = em.GetComponentData<Curve>(created).m_Bezier;
            var prefab = em.GetComponentData<PrefabRef>(created).m_Prefab;
            for (int j = 0; j < m_Sources.Count; j++)
            {
                Entity o = m_Sources[j];
                if (!em.HasComponent<PrefabRef>(o) || em.GetComponentData<PrefabRef>(o).m_Prefab != prefab) continue;
                if (!CurveMatch.LiesOn(curve, ArcOf(o), RRWConst.kSplitMatchTolerance)) continue;
                source = o;
                if (em.HasComponent<RoadWorksSite>(o)) return true;
            }
            return source != Entity.Null;
        }

        // True when the piece inherited a site from a works road that is hidden now (it must stay hidden after apply).
        private bool HandleSplit(Entity piece, Entity source)
        {
            var em = EntityManager;
            if (!em.HasComponent<RoadWorksSite>(source)) { m_SplitPlain++; return false; }   // remnant of a finished road: stays open
            var src = em.GetComponentData<RoadWorksSite>(source);
            var arc = ArcOf(source);
            var curve = em.GetComponentData<Curve>(piece).m_Bezier;
            var site = src;   // same project, WorkDone/Required, seed, natural, restore, cancel, mode and flags
            ToolUtil.MapOnto(arc, src, curve, out site.m_ChainU0, out site.m_ChainU1);
            float len = ToolUtil.CurveLength(em, piece);
            float srcLen = math.max(0.01f, arc.Length);
            site.m_PaidCost = (int)math.round(src.m_PaidCost * math.saturate(len / srcLen));
            // upgrade bands are laterals of the edge's own curve: a piece running against its source mirrors them
            bool flipped = (site.m_ChainU1 >= site.m_ChainU0) != (src.m_ChainU1 >= src.m_ChainU0);
            if (flipped && site.IsUpgrade) MirrorTail(ref site);
            ToolUtil.PutSite(em, piece, site);
            m_Split++;
            bool hidden = !UpgradeRules.IsModeH(src) && SourceHidden(source);
            RRWLog.Verbose("tools: split remnant " + RRWLog.E(piece) + " of " + RRWLog.E(source) + " p" + src.m_ProjectId
                           + " u=[" + RRWLog.F(site.m_ChainU0) + "," + RRWLog.F(site.m_ChainU1) + "] paid=" + site.m_PaidCost
                           + (hidden ? " (source hidden)" : ""));
            return hidden;
        }

        // A works edge the Director keeps hidden right now (registry state, not the Hidden component: vanilla also hides
        // every split / copied original while the tool previews).
        private static bool SourceHidden(Entity source) => SiteRegistry.TryGetEdge(source, out var rec) && rec.HiddenApplied;

        // The saved upgrade bands of a site whose edge now runs the other way (sides swapped, laterals negated).
        private static void MirrorTail(ref RoadWorksSite site)
        {
            int n = math.min(site.m_BandCount, RRWConst.kUwMaxBands);
            for (int i = 0; i < n; i++) site.SetBand(i, UpgradePlan.Mirror(site.Band(i)));
        }

        // ---------------------------------------------------------------- keep preview-hidden pieces hidden on apply

        // Edges collected in the loop (preview-hidden temps that inherit a hidden works site and will be CREATED by
        // ApplyNetSystem), plus preview-hidden temp nodes without an original (new split nodes that only join hidden
        // pieces; the Director's PreviewHide never flags a node that also joins a visible new road). Temps with an
        // original are updated into that original and deleted: nothing to do for them.
        private void HideOnApply()
        {
            var em = EntityManager;
            for (int i = 0; i < m_HideOnApply.Count; i++)
                if (EcsUtil.SetHidden(em, m_HideOnApply[i], true)) m_HiddenOnApplyEdges++;
            if (m_HideOnApply.Count == 0 || m_TempNodes.IsEmptyIgnoreFilter) { Count(); return; }
            var nodes = m_TempNodes.ToEntityArray(Allocator.Temp);
            var nt = m_TempNodes.ToComponentDataArray<Temp>(Allocator.Temp);
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    TempFlags fl = nt[i].m_Flags;
                    if (!ToolUtil.Has(fl, TempFlags.Hidden) || ToolUtil.Has(fl, TempFlags.Delete | TempFlags.Cancel)) continue;
                    Entity o = nt[i].m_Original;
                    bool created = o == Entity.Null || ToolUtil.Has(fl, TempFlags.Replace | TempFlags.Combine);
                    if (!created) continue;
                    if (EcsUtil.SetHidden(em, nodes[i], true)) m_HiddenOnApplyNodes++;
                }
            }
            finally
            {
                nodes.Dispose();
                nt.Dispose();
            }
            Count();
        }

        private void Count()
        {
            TotalPreviewHiddenTemps += m_PreviewHiddenTemps;
            TotalHiddenOnApply += m_HiddenOnApplyEdges + m_HiddenOnApplyNodes;
            if (m_HiddenOnApplyEdges + m_HiddenOnApplyNodes > 0)
                RRWLog.Verbose("tools: preview-hidden pieces kept hidden on apply: edges=" + m_HiddenOnApplyEdges + " nodes=" + m_HiddenOnApplyNodes);
        }

        // ---------------------------------------------------------------- rule 2: combine (node reduction)

        // NodeReductionSystem: the kept temp gets Combine + the merged curve; the other merged edge becomes a
        // Delete|Hidden temp. Its original lies on the merged curve (kCombineMatchTolerance), which also catches chains
        // of reductions.
        // True when the merged edge inherited the works of a hidden works edge (single project only: a new project across
        // projects starts its own visibility in the Director).
        private bool HandleCombine(Entity merged, Temp t, NativeArray<Entity> entities, NativeArray<Temp> temps, RRWSetting settings)
        {
            var em = EntityManager;
            var mergedCurve = em.GetComponentData<Curve>(merged).m_Bezier;
            var mergedArc = new EdgeArc(mergedCurve);
            m_CombineOriginals.Clear();
            m_CombineOriginals.Add(t.m_Original);
            for (int j = 0; j < temps.Length; j++)
            {
                Temp tj = temps[j];
                if (!ToolUtil.Has(tj.m_Flags, TempFlags.Delete)) continue;
                Entity o = tj.m_Original;
                if (o == Entity.Null || o == t.m_Original || m_CombineOriginals.Contains(o)) continue;
                if (!em.Exists(o) || !em.HasComponent<Curve>(o) || !em.HasComponent<Road>(o)) continue;
                if (!CurveMatch.LiesOn(em.GetComponentData<Curve>(o).m_Bezier, mergedArc, RRWConst.kCombineMatchTolerance)) continue;
                m_CombineOriginals.Add(o);
            }

            m_CombineSites.Clear();
            m_CombineSiteEdges.Clear();
            for (int k = 0; k < m_CombineOriginals.Count; k++)
            {
                Entity o = m_CombineOriginals[k];
                if (!em.HasComponent<RoadWorksSite>(o)) continue;
                m_CombineSites.Add(em.GetComponentData<RoadWorksSite>(o));
                m_CombineSiteEdges.Add(o);
            }
            if (m_CombineSites.Count == 0) return false;   // plain roads merged: nothing to do

            bool oneProject = m_CombineSites.Count == m_CombineOriginals.Count;
            for (int k = 1; k < m_CombineSites.Count; k++)
            {
                var s = m_CombineSites[k];
                if (s.m_ProjectId != m_CombineSites[0].m_ProjectId || s.m_Kind != m_CombineSites[0].m_Kind
                    || UpgradeRules.IsModeH(s) != UpgradeRules.IsModeH(m_CombineSites[0]))
                    oneProject = false;
            }
            // Across projects (or with plain roads), mode H parts are never adopted: their works end here with the unbuilt
            // share refunded, and only the other works carry over. With no other works, the merged edge takes over one ended
            // part in its old project, where the Director ends it through the normal teardown.
            if (!oneProject && EndUpgradeParts(merged, mergedCurve)) return false;

            int best = 0;
            float bestP = -1f;
            int paid = 0;
            for (int k = 0; k < m_CombineSites.Count; k++)
            {
                var s = m_CombineSites[k];
                paid += math.max(0, s.m_PaidCost);
                if (s.Progress > bestP) { bestP = s.Progress; best = k; }
            }
            var b = m_CombineSites[best];
            RoadWorksSite site;
            if (oneProject)
            {
                site = b;
                bool modeH = UpgradeRules.IsModeH(b);
                if (b.IsUpgrade && MergedRunsAgainst(m_CombineSiteEdges[best], mergedCurve)) MirrorTail(ref site);
                site.m_ChainU0 = ChainUOnOriginals(mergedCurve.a);
                site.m_ChainU1 = ChainUOnOriginals(mergedCurve.d);
                float lo = float.MaxValue, hi = float.MinValue;
                for (int k = 0; k < m_CombineSites.Count; k++) { lo = math.min(lo, m_CombineSites[k].ChainLo); hi = math.max(hi, m_CombineSites[k].ChainHi); }
                site.m_ChainU0 = math.clamp(site.m_ChainU0, lo, hi);
                site.m_ChainU1 = math.clamp(site.m_ChainU1, lo, hi);
                site.m_WorkDone = math.min(site.m_WorkRequired, (uint)math.round(bestP * site.m_WorkRequired));
                site.m_PaidCost = paid;
                ToolUtil.MergeNatural(ref site, m_CombineSites);
                ToolUtil.PutSite(em, merged, site);
                m_Combined++;
                RRWLog.Info("tools: combine " + m_CombineOriginals.Count + " works edges of p" + site.m_ProjectId + " into " + RRWLog.E(merged)
                            + " u=[" + RRWLog.F(site.m_ChainU0) + "," + RRWLog.F(site.m_ChainU1) + "] p=" + RRWLog.F(bestP) + " paid=" + paid
                            + (modeH ? " (upgrade works)" : ""));
                if (modeH) return false;   // never hidden
                for (int k = 0; k < m_CombineSiteEdges.Count; k++)
                    if (SourceHidden(m_CombineSiteEdges[k])) return true;
                return false;
            }

            // Different projects, or only some originals under works: one consistent new single-edge project.
            m_Results.Clear();
            var one = new List<SiteFactoryEdge>(1) { ToolUtil.FactoryEdge(em, merged, 0, ToolUtil.HasDependants(em, t.m_Original)) };
            ToolUtil.EnsureProjectIds(em, m_Sites);
            // Only Rushed and Migrated carry over.
            SiteFlags keep = b.Flags & (SiteFlags.Rushed | SiteFlags.Migrated);
            SiteFactory.CreateProjects(one, b.Kind, EcsUtil.RoadClass(em, merged), settings, keep, m_Results);
            if (m_Results.Count == 0) return false;
            site = m_Results[0].Site;
            site.Mode = b.Mode;
            // The cancel crew layout (SiteFlags.CancelCrewsMask) is part of the cancel data: dropped with CancelledBuild.
            site.Flags = (site.Flags & ~(SiteFlags.CancelledBuild | SiteFlags.CancelCrewsMask)) | keep;
            site.m_WorkDone = math.min(site.m_WorkRequired, (uint)math.round(bestP * site.m_WorkRequired));
            site.m_PaidCost = paid;
            site.m_RestoreT = b.m_RestoreT;
            site.m_RestoreY16 = b.m_RestoreY16;
            site.m_CancelPhase = (byte)WorksPhase.None;
            ToolUtil.MergeNatural(ref site, m_CombineSites);
            ToolUtil.PutSite(em, merged, site);
            m_CombinedNew++;
            RRWLog.Info("tools: combine across projects/plain roads -> new single-edge project p" + site.m_ProjectId + " " + RRWLog.E(merged)
                        + " kind=" + site.Kind + " mode=" + site.Mode + " p=" + RRWLog.F(bestP) + " paid=" + paid
                        + (b.Has(SiteFlags.CancelledBuild) ? " (cancel data dropped)" : ""));
            return false;
        }

        // Removes the mode H parts of a combine across projects from m_CombineSites (their works end: the original is deleted
        // by the apply, the unbuilt share of what they paid is credited). True when no other works remain; then the merged
        // edge carries the ended part with the most progress in its old project and the Director ends it there (EndUpgrade:
        // p = 1), so the lane groups it had closed stay closed until its machines have left. Without that site the record
        // would only be dropped with the deleted original and the merged road would open under the machines.
        private bool EndUpgradeParts(Entity merged, Bezier4x3 mergedCurve)
        {
            var em = EntityManager;
            bool any = false;
            var keep = default(RoadWorksSite);
            Entity keepEdge = Entity.Null;
            int keepRefund = 0;
            for (int k = m_CombineSites.Count - 1; k >= 0; k--)
            {
                var s = m_CombineSites[k];
                if (!UpgradeRules.IsModeH(s)) continue;
                int refund = ToolUtil.RefundUnbuilt(em, m_CitySystem.City, s);
                m_CombineEndedH++;
                m_CombineRefund += refund;
                RRWLog.Info("tools: combine into " + RRWLog.E(merged) + " ends the upgrade works of " + RRWLog.E(m_CombineSiteEdges[k]) + " (p" + s.m_ProjectId
                            + " p=" + RRWLog.F(s.Progress) + "): refund " + refund);
                if (s.IsUpgrade && (!any || s.Progress > keep.Progress))
                {
                    any = true;
                    keep = s;
                    keepEdge = m_CombineSiteEdges[k];
                    keepRefund = refund;
                }
                m_CombineSites.RemoveAt(k);
                m_CombineSiteEdges.RemoveAt(k);
            }
            if (m_CombineSites.Count > 0) return false;
            if (any) CarryEndedUpgrade(merged, mergedCurve, keepEdge, keep, keepRefund);
            return true;
        }

        // The merged edge takes over the ended upgrade site of `source` (its project, progress and bands) for the teardown.
        private void CarryEndedUpgrade(Entity merged, Bezier4x3 mergedCurve, Entity source, RoadWorksSite src, int refund)
        {
            var em = EntityManager;
            var site = src;
            ToolUtil.MapOnto(ArcOf(source), src, mergedCurve, out site.m_ChainU0, out site.m_ChainU1);
            if ((site.m_ChainU1 >= site.m_ChainU0) != (src.m_ChainU1 >= src.m_ChainU0)) MirrorTail(ref site);
            site.m_PaidCost = math.max(0, src.m_PaidCost - refund);
            ToolUtil.PutSite(em, merged, site);
            WorksRequests.Enqueue(new WorksRequest { Type = WorksRequestType.EndUpgrade, Edge = merged, Aux = src.m_ProjectId });
            RRWLog.Verbose("tools: combine " + RRWLog.E(merged) + " keeps the ended upgrade works of " + RRWLog.E(source) + " in p" + src.m_ProjectId
                           + " until the release opens the road");
        }

        // The merged curve runs against the original edge's curve.
        private bool MergedRunsAgainst(Entity original, Bezier4x3 mergedCurve)
        {
            var arc = ArcOf(original);
            return arc.ProjectExtended(mergedCurve.d) < arc.ProjectExtended(mergedCurve.a);
        }

        // Chain u of a merged-curve end: projected onto the original (with a site) whose arc is nearest.
        private float ChainUOnOriginals(float3 p)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int k = 0; k < m_CombineSiteEdges.Count; k++)
            {
                float d = ArcOf(m_CombineSiteEdges[k]).DistanceXZ(p);
                if (d < bestD) { bestD = d; best = k; }
            }
            if (best < 0) return 0f;
            return ToolUtil.ChainUOf(ArcOf(m_CombineSiteEdges[best]), m_CombineSites[best], p);
        }

        // ---------------------------------------------------------------- rule 3: replace

        // True when the replacing edge inherited the works of a hidden works edge.
        private bool HandleReplace(Entity piece, Temp t, RRWSetting settings)
        {
            var em = EntityManager;
            Entity orig = t.m_Original;
            if (em.HasComponent<RoadWorksSite>(orig))
            {
                var src = em.GetComponentData<RoadWorksSite>(orig);
                if (UpgradeRules.IsModeH(src))
                {
                    // upgrade works upgraded again: merge on the replacing entity (the original is deleted by the apply); when
                    // nothing is left to build, the replacing entity carries the old site until the Director has ended it
                    var r = m_Upgrades.OnUpgradeSite(em, piece, t, src, false, settings, m_Replaced, m_Sites);
                    if (r == UpgradeTagger.ReUpgrade.PaidOnly) Inherit(piece, t, src);
                    return false;   // never hidden
                }
                // An upgrade that changes zoning / electricity (GenerateEdgesSystem) of a works road:
                // the works continue unchanged on the replacing entity; the upgrade cost is added to paid.
                return Inherit(piece, t, src);
            }
            // Same prefab: a Replace-flagged upgrade (electricity connection changed) of a finished road -> no new works
            // (upgrades start nothing new). A different prefab: the player replaced the road -> classified like any other
            // change of road type (partial upgrade works, a Replaced construction, or nothing, by the setting).
            Entity pPrefab = em.GetComponentData<PrefabRef>(piece).m_Prefab;
            Entity oPrefab = em.HasComponent<PrefabRef>(orig) ? em.GetComponentData<PrefabRef>(orig).m_Prefab : Entity.Null;
            if (pPrefab == oPrefab) { m_ReplaceUpgrade++; return false; }
            m_Upgrades.OnPlain(em, piece, t, false, settings, m_Replaced);
            return false;
        }

        // The works of the original continue unchanged on the replacing entity; the upgrade cost is added to paid.
        // True when the source is a hidden works edge.
        private bool Inherit(Entity piece, Temp t, RoadWorksSite src)
        {
            var em = EntityManager;
            Entity orig = t.m_Original;
            var site = src;
            if (em.HasComponent<Curve>(orig))
                ToolUtil.MapOnto(ArcOf(orig), src, em.GetComponentData<Curve>(piece).m_Bezier, out site.m_ChainU0, out site.m_ChainU1);
            bool modeH = UpgradeRules.IsModeH(src);
            site.m_PaidCost = modeH ? UpgradePlan.MergedPaid(src.m_PaidCost, t.m_Cost) : src.m_PaidCost + math.max(0, t.m_Cost);
            if (site.IsUpgrade && (site.m_ChainU1 >= site.m_ChainU0) != (src.m_ChainU1 >= src.m_ChainU0)) MirrorTail(ref site);
            ToolUtil.PutSite(em, piece, site);
            m_ReplaceInherit++;
            RRWLog.Verbose("tools: replace of works edge " + RRWLog.E(orig) + " -> " + RRWLog.E(piece) + " keeps p" + src.m_ProjectId + " paid=" + site.m_PaidCost);
            return !modeH && SourceHidden(orig);
        }

        // ---------------------------------------------------------------- rule 5: modify / upgrade keeping the original

        private void HandleModify(Entity temp, Temp t, RRWSetting settings)
        {
            var em = EntityManager;
            Entity orig = t.m_Original;
            if (em.HasComponent<RoadWorksSite>(orig))
            {
                var site = em.GetComponentData<RoadWorksSite>(orig);
                if (site.Kind == WorksKind.Demolition)
                {
                    // the road is being demolished: the upgrade the player paid for is never built, so it is credited back
                    if (t.m_Cost > 0)
                    {
                        EcsUtil.Credit(em, m_CitySystem.City, t.m_Cost);
                        m_DemolitionCredit++;
                        RRWLog.Verbose("tools: upgrade of demolition edge " + RRWLog.E(orig) + " credited back " + t.m_Cost);
                    }
                    return;
                }
                if (UpgradeRules.IsModeH(site) && UpgradeRules.IsChange(em, temp, t))
                {
                    // re-upgrade of upgrade works, also for a revert (negative cost)
                    if (m_Upgrades.OnUpgradeSite(em, temp, t, site, true, settings, m_Replaced, m_Sites) == UpgradeTagger.ReUpgrade.PaidOnly)
                    {
                        site.m_PaidCost = UpgradePlan.MergedPaid(site.m_PaidCost, t.m_Cost);
                        em.SetComponentData(orig, site);
                        m_UpgradePaid++;
                    }
                    return;
                }
                if (t.m_Cost <= 0) return;
                site.m_PaidCost += t.m_Cost;
                em.SetComponentData(orig, site);
                m_UpgradePaid++;
                RRWLog.Verbose("tools: upgrade of works edge " + RRWLog.E(orig) + " +" + t.m_Cost + " paid=" + site.m_PaidCost);
                return;
            }
            // a finished road whose type or upgrades change (neighbours the tool only regenerated are left alone)
            if (!UpgradeRules.IsChange(em, temp, t) || !em.HasComponent<Curve>(orig)) return;
            m_Upgrades.OnPlain(em, temp, t, true, settings, m_Replaced);
        }

        // ---------------------------------------------------------------- rules 3/4: new projects

        private int Create(List<SiteFactoryEdge> edges, SiteFlags flags, RRWSetting settings)
        {
            if (edges.Count == 0) return 0;
            var em = EntityManager;
            ToolUtil.EnsureProjectIds(em, m_Sites);
            m_Results.Clear();
            SiteFactory.CreateProjects(edges, WorksKind.Construction, EcsUtil.RoadClass(em, edges[0].Edge), settings, flags, m_Results);
            int n = 0;
            for (int i = 0; i < m_Results.Count; i++)
            {
                var r = m_Results[i];
                if (!em.Exists(r.Edge)) continue;
                ToolUtil.PutSite(em, r.Edge, r.Site);
                n++;
            }
            float len = 0f;
            for (int i = 0; i < edges.Count; i++) len += edges[i].Length;
            RRWLog.Info("tools: construction " + ((flags & SiteFlags.Replaced) != 0 ? "(replaced) " : "") + "edges=" + n + " len=" + RRWLog.F(len) + "m");
            return n;
        }
    }
}
