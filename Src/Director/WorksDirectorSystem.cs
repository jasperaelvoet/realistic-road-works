using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Director
{
    // The heart of the mod: accrual + ProgressModel, phases, HideWanted/Hidden (edges + nodes), wear, icons,
    // requests, completion/deletion, tree clearing, registry rebuild, derived GC and the machine allowance.
    // Single writer of RoadWorksRuntime and of the SiteRegistry records. Modification1, before every other RRW Mod1
    // system (order 210), so Props / Machines / Surfaces / Traffic read this frame's state; Ground reads it at ModEnd.
    //
    // Per-frame hand-over contract: Hidden is set in the SAME frame HideWanted turns on (Ground clones at
    // ModEnd of that frame); Hidden is removed only on the true->false transition while Ground keeps the clone
    // (reveal hold); a site is removed only after Ground reports Settled (construction) / Morph(0) (demolition).
    [RegisterSystem(SystemUpdatePhase.Modification1, Before = typeof(GenerateAreasSystem), Order = RRWOrder.Director)]
    public partial class WorksDirectorSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private IconCommandSystem m_IconSystem;
        private CitySystem m_CitySystem;
        private CameraUpdateSystem m_CameraSystem;
        private Game.Objects.SearchSystem m_ObjectSearch;

        private EntityQuery m_AllSitesQuery;   // RoadWorksSite + Edge, not Temp / Deleted
        private EntityQuery m_NewSitesQuery;   // ... without RoadWorksRuntime
        private EntityQuery m_DerivedQuery;    // RRWDerived, not Deleted
        private EntityQuery m_MachineQuery;    // RRWMachine puppet roots and parts with a Transform, not Deleted (upgrade hand-over)

        // The core guard disables the Director after 30 consecutive failures (RRWGuard). Optional features have their
        // own guards so a failing icon / wear / GC pass never stops progress, hiding or completion.
        private readonly RRWGuard m_Guard = new RRWGuard("director");
        private readonly RRWGuard m_GuardRequests = new RRWGuard("director requests");
        private readonly RRWGuard m_GuardAllowance = new RRWGuard("director machine allowance");
        private readonly RRWGuard m_GuardWear = new RRWGuard("director wear");
        private readonly RRWGuard m_GuardIcons = new RRWGuard("director icons");
        private readonly RRWGuard m_GuardClearing = new RRWGuard("director tree clearing");
        private readonly RRWGuard m_GuardGc = new RRWGuard("director derived gc");
        private readonly RRWGuard m_GuardTrims = new RRWGuard("director trims");
        private readonly RRWGuard m_GuardPreview = new RRWGuard("director preview adoption");

        private Action m_StepRequests, m_StepAllowance, m_StepWear, m_StepIcons, m_StepClearing, m_StepGc, m_StepTrims, m_StepPreview;

        // frame context
        private RRWSetting m_S;
        private uint m_Now;
        private bool m_RebuildAllThisFrame;

        // reusable containers (no per-frame GC allocations in the hot path)
        private readonly List<ProjectRecord> m_Projects = new List<ProjectRecord>(64);
        private readonly List<EdgeRecord> m_Records = new List<EdgeRecord>(256);
        private int m_RecordsRevision = int.MinValue;
        private readonly List<EdgeRecord> m_TmpRecords = new List<EdgeRecord>(32);
        private readonly List<Entity> m_TmpEntities = new List<Entity>(64);
        private readonly List<Entity> m_PE = new List<Entity>(32);            // project edges with a site (accrual)
        private readonly List<RoadWorksSite> m_PS = new List<RoadWorksSite>(32);
        private readonly List<float2> m_SortKeys = new List<float2>(32);      // (chainLo, index)
        private readonly List<Entity> m_SortTmp = new List<Entity>(32);
        private int m_GeomCursor;

        private static readonly Comparison<ProjectRecord> s_ByDistance = (a, b) => a.CameraDistance.CompareTo(b.CameraDistance);
        private static readonly Comparison<float2> s_ByX = (a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_IconSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_CameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_ObjectSearch = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();

            m_AllSitesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadWorksSite>(), ComponentType.ReadOnly<Edge>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_NewSitesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadWorksSite>(), ComponentType.ReadOnly<Edge>() },
                None = new[] { ComponentType.ReadOnly<RoadWorksRuntime>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_DerivedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RRWDerived>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_MachineQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RRWMachine>(), ComponentType.ReadOnly<Game.Objects.Transform>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });

            m_StepRequests = StepRequests;
            m_StepAllowance = StepAllowance;
            m_StepWear = StepWear;
            m_StepIcons = StepIcons;
            m_StepClearing = StepClearing;
            m_StepGc = StepGc;
            m_StepTrims = StepTrims;
            m_StepPreview = AdoptPreviewHidden;
            m_StepUpgrade = StepUpgrade;

            DirectorIntrospection.Register();
            RRWLog.Info("director: created (Modification1 before GenerateAreasSystem, order " + RRWOrder.Director + ")");
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            try
            {
                SiteRegistry.Clear("preload");
                RRWClock.Reset();
                PrefabCatalog.Clear();
                WorksRequests.Clear();
                DirectorShared.Reset();
                // RRWDebug.WorkTimeScale / MachineClockScale are deliberately kept (dev time-lapse survives loads).
                ResetManagedState();
            }
            catch (Exception e) { RRWLog.ErrorOnce("director preload", e); }
        }

        private void ResetManagedState()
        {
            m_Projects.Clear();
            m_Records.Clear();
            m_RecordsRevision = int.MinValue;
            m_GcSuspects.Clear();
            m_GcNext.Clear();
            m_IconClosed = Entity.Null;
            m_IconSlow = Entity.Null;
            m_IconsResolved = false;
            m_GeomCursor = 0;
            m_Guard.Reset();
            m_GuardRequests.Reset();
            m_GuardAllowance.Reset();
            m_GuardWear.Reset();
            m_GuardIcons.Reset();
            m_GuardClearing.Reset();
            m_GuardGc.Reset();
            m_GuardTrims.Reset();
            m_GuardPreview.Reset();
            m_GuardUpgrade.Reset();
            m_UwRelink = false;
        }

        private static bool IsGameMode()
        {
            var gm = GameManager.instance;
            return gm != null && gm.gameMode == GameMode.Game;
        }

        protected override void OnUpdate()
        {
            long t0 = RRWPerf.Start();
            try
            {
                RRWClock.Update(World);
                RRWPerf.Frames++;
                if (!IsGameMode()) return;
                m_S = RRWSettings.Current;
                if (m_S == null || m_Guard.Faulted) return;
                try
                {
                    Step();
                    m_Guard.Ok();
                }
                catch (Exception e) { m_Guard.Fail(e); }
            }
            catch (Exception e) { RRWLog.ErrorOnce("director update", e); }
            finally
            {
                CloseIconBuffer();
                RRWPerf.Stop(PerfSlot.Director, t0);
            }
        }

        private static void Run(RRWGuard g, Action a)
        {
            if (g.Faulted) return;
            try { a(); g.Ok(); }
            catch (Exception e) { g.Fail(e); }
        }

        // ------------------------------------------------------------------ frame

        private void Step()
        {
            m_Now = RRWClock.UpdateIndex;
            m_RebuildAllThisFrame = false;

            UpdateCity();                                               // RRWCity (left-hand traffic, NA theme)
            if (!SiteRegistry.Loaded) Rebuild();                       // first game frame after a load
            RegisterNewSites();                                         // sites created this frame
            Run(m_GuardPreview, m_StepPreview);                         // temps that PreviewHide hid became permanent
            DetectLostSites();                                          // lost / re-keyed sites
            RefreshRecordList();
            CheckGeometry();                                            // geometry changes
            Run(m_GuardRequests, m_StepRequests);                       // requests (may add / remove records)
            RefreshRecordList();
            SnapshotProjects();
            Run(m_GuardTrims, m_StepTrims);                             // trims and shared nodes
            Run(m_GuardUpgrade, m_StepUpgrade);                         // upgrade works: runtime from the saved tails

            for (int i = 0; i < m_Projects.Count; i++) Accrue(m_Projects[i]);   // accrual, demolition gate, project phase
            Run(m_GuardAllowance, m_StepAllowance);                     // machine allowance (needs this frame's fronts)

            int hidden = 0;
            for (int i = 0; i < m_Projects.Count; i++) hidden += EdgePass(m_Projects[i]);   // edge phase, Hidden, closure
            DirectorShared.HiddenEdgeCount = hidden;
            NodePass();                                                 // node Hidden

            Run(m_GuardWear, m_StepWear);                               // wear
            Run(m_GuardIcons, m_StepIcons);                             // icons
            Run(m_GuardClearing, m_StepClearing);                       // tree clearing
            StepCompletion();                                           // completion (removes records at the end of the frame)
            Run(m_GuardGc, m_StepGc);                                   // derived-entity GC
        }

        // ------------------------------------------------------------------ registry: rebuild / new sites

        private void Rebuild()
        {
            int n = 0;
            if (!m_AllSitesQuery.IsEmptyIgnoreFilter)
            {
                var arr = m_AllSitesQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        try { if (RegisterSite(arr[i], true)) n++; }
                        catch (Exception e) { RRWLog.ErrorOnce("director rebuild edge", e); }
                    }
                }
                finally { arr.Dispose(); }
            }
            SiteRegistry.Loaded = true;
            m_RecordsRevision = int.MinValue;
            m_UwRelink = true;   // corridor links of upgrade works are runtime only: re-linked from shared chain ends
            RRWLog.Info("director: registry rebuilt: " + n + " edges, " + SiteRegistry.Projects.Count + " projects, next project id " + SiteRegistry.NextProjectId);
        }

        private void RegisterNewSites()
        {
            if (m_NewSitesQuery.IsEmptyIgnoreFilter) return;
            var arr = m_NewSitesQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    try { RegisterSite(arr[i], false); }
                    catch (Exception e) { RRWLog.ErrorOnce("director new site", e); }
                }
            }
            finally { arr.Dispose(); }
        }

        // Adds (or refreshes) the record, the runtime and RoadWorksGround.Initial of one site edge. rebuild = after a load.
        internal bool RegisterSite(Entity edge, bool rebuild)
        {
            var em = EntityManager;
            if (!EcsUtil.Alive(em, edge) || !em.HasComponent<RoadWorksSite>(edge) || !em.HasComponent<Edge>(edge) || !em.HasComponent<Curve>(edge)) return false;
            var site = em.GetComponentData<RoadWorksSite>(edge);
            bool siteDirty = false;
            if (site.m_WorkRequired == 0) { site.m_WorkRequired = 1; siteDirty = true; }
            if (site.m_ProjectId == 0) { site.m_ProjectId = SiteRegistry.AllocateProjectId(); siteDirty = true; }
            if (site.Has(SiteFlags.LegacyPaused))
            {
                // Pause works was removed; a saved site that still carries the old bit resumes on load
                site.Set(SiteFlags.LegacyPaused, false);
                siteDirty = true;
                LegacyPausedCleared++;
                RRWLog.Once("director-legacy-paused-" + site.m_ProjectId, "director: project #" + site.m_ProjectId + " carried the removed 'paused' flag (edge "
                            + RRWLog.E(edge) + "): cleared, works resume");
            }
            if (siteDirty) em.SetComponentData(edge, site);

            bool existed = SiteRegistry.TryGetEdge(edge, out var rec) && rec.ProjectId == site.m_ProjectId;
            bool projectExisted = SiteRegistry.Projects.ContainsKey(site.m_ProjectId);
            var carry = RoadZones.None;
            uint carryFrom = 0;
            if (SiteRegistry.TryGetEdge(edge, out var old) && old.ProjectId != site.m_ProjectId)
            {
                carry = UpgradeHandOverZones(old, site);
                carryFrom = old.ProjectId;
                DetachFromProject(old);
            }
            rec = SiteRegistry.AddEdge(edge, site.m_ProjectId);
            var st = rec.GetOrCreate<DirEdgeState>(ModuleSlot.Director);
            if (!existed)
            {
                st.AddedUpdate = m_Now;
                st.SectionRetryUntil = m_Now + DirConst.kSectionRetryUpdates;
            }
            var ed = em.GetComponentData<Edge>(edge);
            rec.StartNode = ed.m_Start;
            rec.EndNode = ed.m_End;
            RefreshGeometry(rec, st);

            // A temp that PreviewHide hid (TempFlags.Hidden) and ApplyNetSystem turned into this permanent edge (a split
            // remnant of a hidden works edge; Tools gave it the Hidden component right before ApplyNetSystem): it is ours
            // (transition rule), so the edge pass keeps or removes Hidden. Keyed on set membership, not on Hidden.
            if (DirectorShared.PreviewHidden.Contains(edge) && !em.HasComponent<Temp>(edge))
            {
                rec.HiddenApplied = true;
                DirectorShared.PreviewHidden.Remove(edge);
                DirectorShared.PreviewAdopted++;
            }

            // Upgrade works: the edge's bands from its saved tail (the project runtime is built in StepUpgrade)
            bool upgrade = IsUpgradeSite(site);
            if (upgrade)
            {
                if (rec.Upgrade == null) rec.Upgrade = new UpgradeEdgeState();
                rec.Upgrade.Load(site);
            }
            else rec.Upgrade = null;

            var proj = SiteRegistry.GetOrAddProject(site.m_ProjectId);
            if (!proj.Edges.Contains(edge)) { proj.Edges.Add(edge); proj.Revision++; }
            var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
            ps.SortDirty = true;
            if (rebuild) ps.UwRebuilt = true;
            if (carry != RoadZones.None) BeginUpgradeHandOver(proj, ps, edge, carry, carryFrom);
            proj.Kind = site.Kind;
            proj.Seed = site.m_Seed;
            if (site.Mode == VisualMode.FullDig) proj.Mode = VisualMode.FullDig;
            else if (!projectExisted) proj.Mode = site.Mode;
            if (proj.ChainLength < site.m_ChainLength) proj.ChainLength = site.m_ChainLength;
            if (!projectExisted || rebuild) ps.ResetModel = true;   // remnants joining a running project keep its model

            float p = site.Progress;
            var phase = PhasePlan.PhaseOf(site.Kind, p, out float f);
            if (!em.HasComponent<RoadWorksRuntime>(edge))
            {
                // the only place a fresh RoadWorksRuntime struct is built (always added together with RoadWorksGround.Initial)
                em.AddComponentData(edge, new RoadWorksRuntime
                {
                    m_ProjectId = site.m_ProjectId,
                    m_Kind = site.Kind,
                    m_Mode = site.Mode,
                    m_Phase = phase,
                    m_Progress = p,
                    m_PhaseFraction = f,
                    m_ChainU0 = site.m_ChainU0,
                    m_ChainU1 = site.m_ChainU1,
                    m_ChainLength = site.m_ChainLength,
                    m_EdgeLength = st.CurveLength,
                    m_SubgradeDepth = rec.SubgradeDepth,
                    m_GradeOffset = rec.Section.GradeOffset,
                    m_Model = ProgressModel.Reset(RRWClock.SimFrame, p, 0f),
                    m_ClosureTarget = ClosureLevel.Open,
                    m_PhaseChangedFrame = RRWClock.SimFrame,
                });
            }
            if (!em.HasComponent<RoadWorksGround>(edge)) em.AddComponentData(edge, RoadWorksGround.Initial);

            // ModelReset is raised project-wide by the accrual when the model is hard-reset (new project, rebuild);
            // a split remnant joining a running project keeps the model and raises nothing (no machine re-plan pop).
            if (rebuild) st.PendingOnce |= RuntimeFlags.NeedsRebuild;

            // Replaced roads stay visible for kReplacePreRollUpdates while the gravel cover appears.
            // Only for a brand-new site still at its start progress: never after a load (no flicker on load). Upgrade works are
            // never hidden, so they have no pre-roll.
            if (!rebuild && !existed && !rec.HiddenApplied && !upgrade && site.Kind == WorksKind.Construction && site.Has(SiteFlags.Replaced)
                && site.m_WorkDone <= (uint)math.round(RRWConst.kC1 * site.m_WorkRequired) + 1u)
                st.PreRollUntil = m_Now + (uint)RRWConst.kReplacePreRollUpdates;

            if (!rebuild && !existed)
                RRWLog.Verbose("director: site edge " + RRWLog.E(edge) + " project #" + site.m_ProjectId + " " + site.Kind + " mode=" + site.Mode
                               + " p=" + RRWLog.F(p) + " u=[" + RRWLog.F(site.m_ChainU0) + "," + RRWLog.F(site.m_ChainU1) + "]/" + RRWLog.F(site.m_ChainLength));
            return true;
        }

        // Removes the record from its project (not from the registry).
        private void DetachFromProject(EdgeRecord rec)
        {
            if (!SiteRegistry.TryGetProject(rec.ProjectId, out var proj)) return;
            if (proj.Edges.Remove(rec.Edge))
            {
                proj.Revision++;
                proj.GetOrCreate<DirProjectState>(ModuleSlot.Director).SortDirty = true;
            }
            if (proj.Edges.Count == 0) SiteRegistry.RemoveProject(proj.Id);
        }

        internal void RemoveRecord(EdgeRecord rec)
        {
            DetachFromProject(rec);
            SiteRegistry.RemoveEdge(rec.Edge);
        }

        // ------------------------------------------------------------------ lost sites

        private void DetectLostSites()
        {
            if (SiteRegistry.Edges.Count == 0) return;
            var em = EntityManager;
            m_TmpRecords.Clear();
            foreach (var rec in SiteRegistry.Edges.Values)
            {
                Entity e = rec.Edge;
                if (!em.Exists(e) || em.HasComponent<Deleted>(e) || !em.HasComponent<RoadWorksSite>(e) || em.HasComponent<Temp>(e))
                {
                    m_TmpRecords.Add(rec);
                    continue;
                }
                // project id changed under us (another writer re-keyed the site): move the record
                uint pid = em.GetComponentData<RoadWorksSite>(e).m_ProjectId;
                if (pid != rec.ProjectId && pid != 0) m_TmpRecords.Add(rec);
            }
            for (int i = 0; i < m_TmpRecords.Count; i++)
            {
                var rec = m_TmpRecords[i];
                try
                {
                    Entity e = rec.Edge;
                    bool alive = em.Exists(e) && !em.HasComponent<Deleted>(e);
                    if (alive && em.HasComponent<RoadWorksSite>(e) && !em.HasComponent<Temp>(e))
                    {
                        // re-keyed: re-register under the new project (record keeps its Hidden state)
                        DetachFromProject(rec);
                        RegisterSite(e, false);
                        continue;
                    }
                    var st = rec.Get<DirEdgeState>(ModuleSlot.Director);
                    if (alive)
                    {
                        // transition rule: we applied Hidden, we remove it once. Ground's orphan path keeps the clone and
                        // restores the clip >= 45 updates later (never in this un-hide frame; reveal rule verified in game).
                        if (rec.HiddenApplied) EcsUtil.SetHidden(em, e, false);
                        if (st != null && st.IconPrefab != Entity.Null) RemoveIcon(e, st.IconPrefab);
                        if (!em.HasComponent<RoadWorksSite>(e))
                        {
                            if (em.HasComponent<RoadWorksRuntime>(e)) em.RemoveComponent<RoadWorksRuntime>(e);
                            if (em.HasComponent<RoadWorksGround>(e)) em.RemoveComponent<RoadWorksGround>(e);
                        }
                    }
                    else if (st != null && st.IconPrefab != Entity.Null && em.Exists(e)) RemoveIcon(e, st.IconPrefab);
                    RRWLog.Verbose("director: lost site edge " + RRWLog.E(e) + " project #" + rec.ProjectId + (alive ? " (edge kept)" : " (edge gone)"));
                    RemoveRecord(rec);
                }
                catch (Exception ex) { RRWLog.ErrorOnce("director lost site", ex); SiteRegistry.RemoveEdge(rec.Edge); }
            }
            m_TmpRecords.Clear();
        }

        private void RefreshRecordList()
        {
            if (m_RecordsRevision == SiteRegistry.Revision) return;
            m_Records.Clear();
            foreach (var rec in SiteRegistry.Edges.Values) m_Records.Add(rec);
            m_RecordsRevision = SiteRegistry.Revision;
        }

        private void SnapshotProjects()
        {
            m_Projects.Clear();
            foreach (var p in SiteRegistry.Projects.Values) m_Projects.Add(p);
            for (int i = 0; i < m_Projects.Count; i++)
            {
                var proj = m_Projects[i];
                var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
                if (ps.SortDirty || ps.LastEdgeCount != proj.Edges.Count) SortProjectEdges(proj, ps);
            }
        }

        // ProjectRecord.Edges in chain order (u increasing).
        private void SortProjectEdges(ProjectRecord proj, DirProjectState ps)
        {
            var em = EntityManager;
            m_SortKeys.Clear();
            m_SortTmp.Clear();
            for (int i = proj.Edges.Count - 1; i >= 0; i--)
                if (!SiteRegistry.Edges.ContainsKey(proj.Edges[i])) proj.Edges.RemoveAt(i);
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                float lo = em.Exists(e) && em.HasComponent<RoadWorksSite>(e) ? em.GetComponentData<RoadWorksSite>(e).ChainLo : float.MaxValue;
                m_SortKeys.Add(new float2(lo, i));
                m_SortTmp.Add(e);
            }
            m_SortKeys.Sort(s_ByX);
            for (int i = 0; i < m_SortKeys.Count; i++)
            {
                Entity e = m_SortTmp[(int)m_SortKeys[i].y];
                proj.Edges[i] = e;
                if (SiteRegistry.TryGetEdge(e, out var r)) r.ChainIndex = i;
            }
            ps.SortDirty = false;
            ps.LastEdgeCount = proj.Edges.Count;
        }

        // ------------------------------------------------------------------ accrual + project phase

        private void Accrue(ProjectRecord proj)
        {
            var em = EntityManager;
            var s = m_S;
            var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
            ps.ModelResetThisFrame = false;
            ps.HardResetThisFrame = false;

            m_PE.Clear();
            m_PS.Clear();
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                m_PE.Add(e);
                m_PS.Add(em.GetComponentData<RoadWorksSite>(e));
            }
            if (m_PE.Count == 0) return;

            // ---- harmonise (every edge of a project shares WorkDone / WorkRequired; after split / combine / migration
            //      they may disagree: all take the max p) + project-wide flags
            uint req = 1;
            float pMax = 0f;
            bool differ = false, rushed = false, legacyPaused = false, incompatible = false, anyA = false, anyH = false, calledOff = false;
            float U = 0f;
            for (int i = 0; i < m_PS.Count; i++)
            {
                var st = m_PS[i];
                if (st.m_WorkRequired > req) req = st.m_WorkRequired;
                pMax = math.max(pMax, st.Progress);
                if (i > 0 && (st.m_WorkRequired != m_PS[0].m_WorkRequired || st.m_WorkDone != m_PS[0].m_WorkDone)) differ = true;
                rushed |= st.Has(SiteFlags.Rushed);
                legacyPaused |= st.Has(SiteFlags.LegacyPaused);   // only read to clear it (written back below)
                incompatible |= st.Has(SiteFlags.Incompatible);
                calledOff |= st.Has(SiteFlags.CalledOff);
                anyA |= st.Mode == VisualMode.FullDig;
                anyH |= IsUpgradeSite(st);
                U = math.max(U, st.m_ChainLength);
            }
            var kind = m_PS[0].Kind;
            uint done = differ ? (uint)math.round(pMax * req) : m_PS[0].m_WorkDone;
            if (done > req) done = req;
            if (incompatible && done < req)
            {
                done = req;   // unknown save layout: finish at once, no money change
                RRWLog.Once("director-incompatible-" + proj.Id, "director: project #" + proj.Id + " has an incompatible site layout: finishing it immediately");
            }

            float p0 = done / (float)req;
            // upgrade works: the window and the lead band's phase from the saved schedule (UpgradePlan.At)
            bool upgrade = !anyA && anyH && proj.Upgrade != null;
            var phase0 = upgrade ? PhaseAt(proj, kind, p0, out float _) : PhasePlan.PhaseOf(kind, p0, out float _);

            // ---- A demolition saved while its D0 call-off was releasing the road (SiteFlags.CalledOff)
            //      re-enters the call-off after load: no accrual, the gate opens it once Machines report clear (no puppets
            //      after a load, so within a few updates), then StepCompletion removes the sites (FinishCallOff).
            if (calledOff && !ps.CallOff)
            {
                if (kind == WorksKind.Demolition && phase0 == WorksPhase.BreakUp)
                {
                    ps.CallOff = true;
                    ps.Carry = 0.0;
                    ps.GateActive = false;
                    ps.GateInit = true;
                    BeginRelease(proj, ps);
                    RRWLog.Info("director: demolition project #" + proj.Id + " was saved while being called off: the call-off resumes, "
                                + m_PE.Count + " edge(s) reopen once the machines have left");
                }
                else
                {
                    // not a D0 demolition any more (should not happen): drop the stray bit so it never sticks
                    for (int i = 0; i < m_PE.Count; i++)
                    {
                        var st = m_PS[i];
                        if (!st.Has(SiteFlags.CalledOff)) continue;
                        st.Set(SiteFlags.CalledOff, false);
                        m_PS[i] = st;
                        em.SetComponentData(m_PE[i], st);
                    }
                    RRWLog.Warn("director: project #" + proj.Id + " carried CalledOff outside a D0 demolition (" + kind + " " + phase0 + "): cleared");
                }
            }

            // ---- demolition mobilisation gate (decided once per project, when a demolition starts at p = 0)
            if (kind == WorksKind.Demolition && !ps.CallOff)
            {
                if (!ps.GateInit)
                {
                    ps.GateInit = true;
                    bool dependants = false;
                    for (int i = 0; i < m_PS.Count; i++) dependants |= m_PS[i].Has(SiteFlags.Dependants);
                    ps.GateActive = done == 0 && RRWDebug.On(DebugLayers.Closure)
                                    && PhasePlan.StartClosure(WorksKind.Demolition, s.Policy, dependants) == ClosureLevel.Closed;
                    ps.GateSimFrames = 0;
                    if (ps.GateActive) RRWLog.Info("director: demolition project #" + proj.Id + " waiting for traffic to clear");
                }
                if (ps.GateActive)
                {
                    ps.GateSimFrames += RRWClock.SimDelta;
                    bool drained = true;
                    for (int i = 0; i < m_PE.Count && drained; i++)
                        drained = SiteRegistry.TryGetEdge(m_PE[i], out var r) && r.TrafficDrained;
                    bool timeout = ps.GateSimFrames >= RRWConst.kClearTrafficTimeoutFrames;
                    if (drained || timeout || phase0 != WorksPhase.BreakUp || !RRWDebug.On(DebugLayers.Closure))
                    {
                        ps.GateActive = false;
                        RRWLog.Info("director: demolition project #" + proj.Id + " mobilising (" + (drained ? "traffic drained" : timeout ? "timeout " + ps.GateSimFrames + " frames" : "gate lifted") + ")");
                    }
                }
            }
            else if (kind != WorksKind.Demolition) { ps.GateInit = false; ps.GateActive = false; }

            // ---- accrual
            bool inShift = RRWDebug.IgnoreShift || WorkTime.InShift(RRWClock.NormalizedTime, WorkTime.Effective(s.Shift, rushed));
            // No pause any more; a demolition called off in D0 (CallOff) accrues nothing while its machines leave
            bool working = !incompatible && !ps.CallOff && phase0 != WorksPhase.Complete && !ps.GateActive && inShift;
            float rate = WorkTime.Rate(rushed, RRWDebug.WorkTimeScale);
            if (working && RRWClock.SimDelta > 0 && rate > 0f)
            {
                ps.Carry += RRWClock.SimDelta * (double)rate;
                if (ps.Carry >= 1.0)
                {
                    double whole = math.floor(ps.Carry);
                    ps.Carry -= whole;
                    ulong nd = done + (ulong)whole;
                    done = nd >= req ? req : (uint)nd;
                }
            }
            if (!working) ps.Carry = 0.0;

            // ---- stage context, stage switch machine and accrual hold (may clamp `done`)
            var modeNow = anyA ? VisualMode.FullDig : upgrade ? VisualMode.HalfWidth : m_PS[0].Mode;
            if (upgrade) proj.Mode = modeNow;
            bool held = false;
            if (upgrade)
            {
                try { held = StageStepUpgrade(proj, ps, kind, req, p0, ref done); }
                catch (Exception e)
                {
                    RRWLog.ErrorOnce("director upgrade stage step", e);
                    UpgradeFailClosed(proj);
                }
                // the step may have saved a window's primitive or cleared the lane-drop flag in the tails: write back from those
                for (int i = 0; i < m_PE.Count; i++)
                    if (em.Exists(m_PE[i]) && em.HasComponent<RoadWorksSite>(m_PE[i])) m_PS[i] = em.GetComponentData<RoadWorksSite>(m_PE[i]);
            }
            else
            {
                try { held = StageStep(proj, ps, kind, modeNow, req, p0, ref done); }
                catch (Exception e) { RRWLog.ErrorOnce("director stage step", e); }
            }
            if (held) ps.Carry = 0.0;

            // write back (read-modify-write of every edge's saved site) only when something changed
            if (legacyPaused && !ps.LegacyPausedLogged)
            {
                ps.LegacyPausedLogged = true;
                RRWLog.Info("director: project #" + proj.Id + " carried the removed 'paused' flag: cleared, works resume");
            }
            for (int i = 0; i < m_PE.Count; i++)
            {
                var st = m_PS[i];
                bool clearLegacy = st.Has(SiteFlags.LegacyPaused);
                if (st.m_WorkDone == done && st.m_WorkRequired == req && !clearLegacy) continue;
                if (clearLegacy) { st.Set(SiteFlags.LegacyPaused, false); LegacyPausedCleared++; }
                st.m_WorkDone = done;
                st.m_WorkRequired = req;
                m_PS[i] = st;
                em.SetComponentData(m_PE[i], st);
            }

            float p = done / (float)req;
            float f;
            var phase = upgrade ? PhaseAt(proj, kind, p, out f) : PhasePlan.PhaseOf(kind, p, out f);

            // ---- ProgressModel (domain: sim frames; anchored at SimFrame where the accrued p is exact)
            float ratePF = working && !held ? rate / req : 0f;   // a held p re-anchors with rate 0 (no ModelReset)
            uint simF = RRWClock.SimFrame;
            if (!ps.HaveModel || ps.ResetModel)
            {
                proj.Model = ProgressModel.Reset(simF, p, ratePF);
                ps.AnchorFrame = simF;
                ps.HaveModel = true;
                ps.ResetModel = false;
                ps.ModelResetThisFrame = true;
                ps.HardResetThisFrame = true;   // the crew latch re-runs (not on a drift reset)
            }
            else
            {
                float drift = math.abs(proj.Model.At(simF, 0f) - p);
                bool changed = working != ps.LastWorking || math.abs(ratePF - ps.LastRatePF) > DirConst.kRateEpsilon;
                bool due = unchecked(simF - ps.AnchorFrame) >= RRWConst.kModelWindow || simF < ps.AnchorFrame;
                if (drift > DirConst.kModelDriftReset)
                {
                    RRWLog.Verbose("director: project #" + proj.Id + " model drift " + RRWLog.F(drift) + " -> reset");
                    proj.Model = ProgressModel.Reset(simF, p, ratePF);
                    ps.AnchorFrame = simF;
                    ps.ModelResetThisFrame = true;
                }
                else if (changed || due)
                {
                    proj.Model = ProgressModel.Reanchor(proj.Model, simF, p, ratePF, RRWConst.kModelWindow);
                    ps.AnchorFrame = simF;
                }
            }
            ps.LastWorking = working;
            ps.LastRatePF = ratePF;
            if (held != ps.LastHeld)
            {
                // the hold re-anchors through the rate change above (no ModelReset, no machine re-plan pop)
                ps.LastHeld = held;
                RRWLog.Verbose("director: project #" + proj.Id + " accrual " + (held ? "held at p=" + RRWLog.F(p) : "resumes"));
            }

            // ---- project state
            var first = m_PS[0];
            proj.Kind = kind;
            proj.Mode = modeNow;
            proj.ChainLength = U;
            proj.Progress = p;
            proj.Phase = phase;
            proj.PhaseFraction = f;
            proj.Working = working;
            proj.Seed = first.m_Seed;
            proj.Flags = (first.Flags & ~SiteFlags.LegacyPaused) | (rushed ? SiteFlags.Rushed : SiteFlags.None);
            proj.CancelPhase = first.CancelPhase;
            proj.CancelFront = first.m_CancelFront;
            proj.ClearingTraffic = ps.GateActive;
            if (proj.TrimU1 <= 0f || proj.TrimU1 > U) proj.TrimU1 = U;
            // Machine seconds for p 0 -> 1 at the nominal rate (rush included, dev time scales excluded:
            // rrw.timescale scales the machine clock the same way), every update; then the crew latch (after this update's
            // StageCtx, so a C4 entry counts with the side swap) and the focus crew's front.
            proj.WorkSeconds = WorkSecondsOf(req, rushed);
            try { LatchCrews(proj, ps, phase); }
            catch (Exception e) { RRWLog.ErrorOnce("director crew latch", e); proj.Crews = 1; proj.FocusCrew = 0; }
            if (proj.FocusCrew >= proj.Crews || proj.FocusCrew < 0) proj.FocusCrew = 0;
            proj.FrontU = PhasePlan.CrewFront(proj.View(), proj.FocusCrew);   // stage-aware; one crew = MainFront(View())

            if (phase == WorksPhase.Complete)
            {
                if (!ps.CompletingSeen)
                {
                    ps.CompletingSeen = true;
                    ps.CompletingSince = m_Now;
                    ps.CompletionTimeoutLogged = false;
                    // Frame N starts the release: Completing + Releasing, Props tear down, Machines
                    // leave; the road opens once Machines report the carriageway clear (EdgePass, MachinesHoldRoad)
                    if (proj.ReleaseSince == 0) BeginRelease(proj, ps);
                    RRWLog.Info("director: " + kind + " project #" + proj.Id + " complete (" + m_PE.Count + " edges): tearing down, "
                                + (PhasePlan.HasMachines(proj.Mode) ? "road opens once the machines have left" : "no machines (mode D)"));
                }
            }
            else
            {
                ps.CompletingSeen = false;
                if (!ps.CallOff && proj.ReleaseSince != 0) EndRelease(proj, ps);   // dev SetProgress back from Complete
            }
        }

        public static int LegacyPausedCleared;      // saved sites whose removed 'paused' bit was cleared (dev state line)

        // Release gate: ReleaseSince is an UpdateIndex >= 1.
        internal void BeginRelease(ProjectRecord proj, DirProjectState ps)
        {
            proj.ReleaseSince = math.max(1u, m_Now);
            proj.ReleaseSimSince = RRWClock.SimFrame;
            ps.Hold = PhasePlan.HasMachines(proj.Mode);
            ps.OpenedAfterRelease = false;
            ps.OpenedAt = 0;
            ps.HoldLogged = false;
            ps.CapWarned = false;
        }

        private static void EndRelease(ProjectRecord proj, DirProjectState ps)
        {
            proj.ReleaseSince = 0;
            proj.ReleaseSimSince = 0;
            ps.Hold = false;
            ps.OpenedAfterRelease = false;
            ps.OpenedAt = 0;
            ps.HoldLogged = false;
            ps.CapWarned = false;
        }

        // Once per frame per project, before its edges: the release gate and the staged opening.
        private void ProjectGate(ProjectRecord proj, DirProjectState ps)
        {
            var s = m_S;
            // upgrade works hold only while a machine is in a lane group the release opens (the groups closed now); every other
            // mode keeps its rule (the zone overload hands them to the carriageway gate)
            var opening = proj.Mode == VisualMode.HalfWidth ? RoadZones.AllLanes & ~proj.OpenLanes : RoadZones.None;
            // lane groups taken over from another running upgrade project stay closed while its machines are still in them
            bool handOver = proj.Mode == VisualMode.HalfWidth && UpgradeHandOverHold(proj, ps);
            bool hold = PhasePlan.MachinesHoldRoad(proj.Mode, proj.ReleaseSince, proj.MachinesOnCarriageway, proj.MachinesReportUpdate, m_Now, proj.ReleaseCapped(m_Now),
                                                   proj.MachineZones, opening) || handOver;
            if (ps.OpenedAfterRelease) hold = handOver;                    // sticky: never Open -> Closed -> Open
            else if (proj.Releasing)
            {
                uint age = unchecked(m_Now - proj.ReleaseSince);
                if (!hold)
                {
                    ps.OpenedAfterRelease = true;
                    ps.OpenedAt = m_Now;
                    bool capped = PhasePlan.HasMachines(proj.Mode) && proj.ReleaseCapped(m_Now);
                    if (capped)
                    {
                        DirectorShared.ReleasesCapped++;
                        if (!ps.CapWarned)
                        {
                            ps.CapWarned = true;
                            RRWLog.Warn("director: project #" + proj.Id + ": machines did not leave within kPostSiteMaxSimFrames (" + proj.ReleaseSimAge + " sim frames / " + age + " updates, report "
                                        + (proj.MachinesReportFresh(m_Now) ? proj.MachinesOnCarriageway + " on the carriageway, zones " + proj.MachineZones : "missing/stale")
                                        + "): road opens anyway (safety cap)");
                        }
                    }
                    else DirectorShared.ReleasesOpened++;
                    if (PhasePlan.HasMachines(proj.Mode))
                        RRWLog.Info("director: project #" + proj.Id + ": machines clear, road opens (" + age + " updates after "
                                    + (ps.CallOff ? "the call-off" : "completion") + (capped ? ", safety cap" : "") + ")");
                    else
                        RRWLog.Verbose("director: project #" + proj.Id + " (mode D, no machines) releases the road at once");
                }
                else if (!ps.HoldLogged)
                {
                    ps.HoldLogged = true;
                    RRWLog.Info("director: project #" + proj.Id + " finished its work: road stays closed until every machine has left the carriageway");
                }
            }
            ps.Hold = hold;

            // Staged opening: stage-driven OpenLanes (sticky through last frame's value; Machines never
            // plan into an open group), SoftZones (switch drain + re-close path), anti-churn, WorkZonesReady (DirectorStage.cs)
            proj.SeparableHalves = SeparableHalves(proj);
            try { StageZones(proj, ps); }
            catch (Exception e)
            {
                // fail closed: nothing new opens, nothing is ready for machines
                RRWLog.ErrorOnce("director stage zones", e);
                proj.OpenLanes &= RoadZones.Sidewalks;
                proj.WorkZonesReady = RoadZones.None;
            }
            // Upgrade works: MachineSafe and the machine-safe range of every band from this update's ready zones
            if (IsUpgradeProject(proj))
            {
                try { UpgradeSafety(proj); }
                catch (Exception e)
                {
                    RRWLog.ErrorOnce("director upgrade machine safety", e);
                    UpgradeFailClosed(proj);
                }
            }
            if (proj.OpenLanes != ps.LoggedOpenLanes)
            {
                RRWLog.Info("director: project #" + proj.Id + " " + proj.Phase + " staged opening " + ps.LoggedOpenLanes + " -> " + proj.OpenLanes
                            + " (stage " + (proj.StageIndex + 1) + "/" + proj.StageCount + " switch " + proj.Switch + " soft " + proj.SoftZones
                            + " carHalf=" + (proj.CarHalfAllowed ? "yes" : "no reason=" + proj.StageBlockReason)
                            + ", machine zones " + proj.MachineZones + (proj.MachinesReportFresh(m_Now) ? "" : ", report stale") + ")");
                ps.LoggedOpenLanes = proj.OpenLanes;
            }
        }

        // One-way / asymmetric roads: halves EVERY edge of the project can open on its own (Traffic's
        // EdgeRecord.CarHalves for the current geometry). An edge Traffic has not classified yet -> None (wait).
        private static RoadZones SeparableHalves(ProjectRecord proj)
        {
            if (proj.Edges.Count == 0) return RoadZones.None;
            var sep = RoadZones.LeftHalf | RoadZones.RightHalf;
            for (int i = 0; i < proj.Edges.Count && sep != RoadZones.None; i++)
            {
                if (!SiteRegistry.TryGetEdge(proj.Edges[i], out var rec) || rec.CarHalvesRevision != rec.GeometryRevision) return RoadZones.None;
                sep &= rec.CarHalves;
            }
            return sep;
        }

        // ------------------------------------------------------------------ per-edge pass

        private int EdgePass(ProjectRecord proj)
        {
            var em = EntityManager;
            var s = m_S;
            var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
            int hidden = 0;
            ClosureLevel projClosure = ClosureLevel.Open;
            ProjectGate(proj, ps);
            bool upgrade = IsUpgradeProject(proj);
            var upgradePrim = UpgradeClosurePrimitive(proj);
            // upgrade works hold the road only while a lane group that opens is still closed (MachinesHoldRoad), or while lane
            // groups taken over from another project wait for its machines (UpgradeHandOverHold)
            bool busy = ps.Hold;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!SiteRegistry.TryGetEdge(e, out var rec)) continue;
                if (!em.Exists(e) || em.HasComponent<Deleted>(e) || !em.HasComponent<RoadWorksSite>(e) || !em.HasComponent<RoadWorksRuntime>(e)) continue;
                var st = rec.GetOrCreate<DirEdgeState>(ModuleSlot.Director);
                var site = em.GetComponentData<RoadWorksSite>(e);
                var rt = em.GetComponentData<RoadWorksRuntime>(e);
                var ground = em.HasComponent<RoadWorksGround>(e) ? em.GetComponentData<RoadWorksGround>(e) : RoadWorksGround.Initial;

                float p = site.Progress;
                float f;
                var phase = upgrade ? PhaseAt(proj, site.Kind, p, out f) : PhasePlan.PhaseOf(site.Kind, p, out f);

                // ---- HideWanted and Hidden (upgrade works are never hidden)
                bool wanted = !upgrade && PhasePlan.IsHidden(site.Kind, site.Mode, phase)
                              && s.TerrainOn
                              && RRWDebug.On(DebugLayers.Hide)
                              && !site.Has(SiteFlags.Incompatible)
                              && !(site.Kind == WorksKind.Construction && st.PreRollUntil != 0 && m_Now < st.PreRollUntil);
                bool apply = wanted && (!RRWDebug.HideAfterGround || (ground.Known && ground.m_Kind != TerrainProfileKind.Vanilla));
                bool prevApplied = rec.HiddenApplied;
                if (apply) EcsUtil.SetHidden(em, e, true);          // asserted every frame (no-op when already hidden)
                else if (prevApplied) EcsUtil.SetHidden(em, e, false); // removed ONLY on the true -> false transition
                rec.HiddenApplied = apply;
                if (apply) hidden++;

                // ---- closure target. The PROJECT mode decides (a mode-A project is Closed for the
                //      whole works, buildings that spawned along it wait; a mode-D edge inside an A chain too), and the
                //      release gate keeps it Closed until every machine has left the carriageway. Upgrade works (every HalfWidth
                //      project) follow the layout window's traffic primitive and never the start closure or `dependants`.
                bool dependants = rec.Dependants || site.Has(SiteFlags.Dependants);
                var closure = RRWDebug.On(DebugLayers.Closure)
                    ? PhasePlan.Closure(site.Kind, s.Policy, dependants, phase, proj.Mode, wanted, busy, upgradePrim)
                    : ClosureLevel.Open;
                if (closure > projClosure) projClosure = closure;
                // Staged opening: lane groups of a Closed, visible edge that carry traffic anyway (Traffic applies them per lane)
                var openLanes = closure == ClosureLevel.Closed && !apply ? proj.OpenLanes : RoadZones.None;
                // Closing groups (switch drain, re-close path), same rule; never overlapping the open ones
                var softLanes = closure == ClosureLevel.Closed && !apply ? proj.SoftZones & ~openLanes : RoadZones.None;

                // ---- runtime (read-modify-write; the Director owns every field)
                RuntimeFlags fl = RuntimeFlags.None;
                if (apply) fl |= RuntimeFlags.Hidden;
                if (wanted) fl |= RuntimeFlags.HideWanted;
                if (proj.Working) fl |= RuntimeFlags.Working;
                if (site.Has(SiteFlags.Rushed)) fl |= RuntimeFlags.Rushed;
                if (apply && !prevApplied) fl |= RuntimeFlags.HideFrame;
                if (!apply && prevApplied) fl |= RuntimeFlags.Revealed;
                if (phase == WorksPhase.Complete) fl |= RuntimeFlags.Completing;
                if (proj.Releasing) fl |= RuntimeFlags.Releasing;
                if (proj.AllowMachines) fl |= RuntimeFlags.Machines;
                if (dependants) fl |= RuntimeFlags.Dependants;
                if (ps.GateActive) fl |= RuntimeFlags.ClearingTraffic;
                if (ps.ModelResetThisFrame) fl |= RuntimeFlags.ModelReset;
                if (m_RebuildAllThisFrame) fl |= RuntimeFlags.NeedsRebuild;
                if (proj.Switch != StageSwitch.None) fl |= RuntimeFlags.StageSwitch;
                fl |= st.PendingOnce;
                st.PendingOnce = RuntimeFlags.None;

                rt.m_ProjectId = site.m_ProjectId;
                rt.m_Kind = site.Kind;
                rt.m_Mode = site.Mode;
                if (phase != st.LastPhase)
                {
                    if (st.LastPhase != WorksPhase.None)
                        RRWLog.Verbose("director: edge " + RRWLog.E(e) + " project #" + site.m_ProjectId + " " + st.LastPhase + " -> " + phase);
                    rt.m_PhaseChangedFrame = RRWClock.SimFrame;
                    st.LastPhase = phase;
                }
                rt.m_Phase = phase;
                rt.m_Flags = fl;
                rt.m_Progress = p;
                rt.m_PhaseFraction = f;
                rt.m_ChainU0 = site.m_ChainU0;
                rt.m_ChainU1 = site.m_ChainU1;
                rt.m_ChainLength = site.m_ChainLength;
                rt.m_EdgeLength = st.CurveLength > 0f ? st.CurveLength : (rec.Arc != null ? rec.Arc.Length : rt.m_EdgeLength);
                rt.m_SubgradeDepth = rec.SubgradeDepth;
                rt.m_GradeOffset = rec.Section.GradeOffset;
                rt.m_Model = proj.Model;
                rt.m_Crews = (byte)math.clamp(proj.Crews, 1, RRWConst.kMaxCrewsPerProject);   // every edge, every update
                rt.m_ClosureTarget = closure;
                rt.m_OpenLanes = openLanes;
                rt.m_SoftZones = softLanes;
                em.SetComponentData(e, rt);
                st.LastWanted = wanted;
            }
            proj.Closure = projClosure;
            if (projClosure == ClosureLevel.Closed && !ps.MaintenanceLogged)
            {
                ps.MaintenanceLogged = true;
                RRWLog.Verbose("director: project #" + proj.Id + " is Closed: maintenance vehicles may still be dispatched to it (known limitation)");
            }
            return hidden;
        }

        // ------------------------------------------------------------------ machine allowance

        private void StepAllowance()
        {
            var em = EntityManager;
            var s = m_S;
            float3 cam = CameraPivot(out bool haveCam);
            for (int i = 0; i < m_Projects.Count; i++)
            {
                var proj = m_Projects[i];
                var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
                // The focus crew (nearest crew front, hysteresis) gives FrontU / FrontPosition / CameraDistance
                ps.NearCrews = FocusCrew(proj, ps, cam, haveCam);
                proj.CameraChainDistance = haveCam ? ChainDistance(proj, ps, cam) : 0f;
            }
            if (m_Projects.Count > 1) m_Projects.Sort(s_ByDistance);   // nearest first (also the order of the edge pass)
            int used = 0;
            int need = math.max(1, s.MachineBudget);   // MachineBudget is PER CREW
            for (int i = 0; i < m_Projects.Count; i++)
            {
                var proj = m_Projects[i];
                var ps = proj.GetOrCreate<DirProjectState>(ModuleSlot.Director);
                float d = proj.CameraDistance;
                // Spawning needs the camera near the FRONT; keeping an allowance needs it near the CHAIN. At a phase boundary
                // the front jumps from u = U back to 0 (seen in testing: on an 816 m test road a camera at the chain end went
                // "allowMachines=False cam=816" in the C2->C3 reveal frame); the nearest-chain distance does not jump.
                float keep = math.min(d, proj.CameraChainDistance);
                // upgrade works: machines only while a band of the layout window has a machine-safe sub-strip
                bool allow = s.MachinesOn && PhasePlan.HasMachines(proj.Mode) && (proj.Mode != VisualMode.HalfWidth || UpgradeMachinesWanted(proj))
                             && !proj.ClearingTraffic && proj.Phase != WorksPhase.Complete && !proj.Releasing
                             && (d < RRWConst.kMachineSpawnRadius || (ps.WasAllowed && keep < RRWConst.kMachineDespawnRadius));
                bool degraded = false;
                if (allow)
                {
                    // Reserve one crew budget per crew front within the spawn radius (at least one); when the global cap
                    // cannot take them all, the project still gets one crew's budget and Machines' crew LOD (nearest crews first,
                    // kMaxActiveCrewsGlobal / kMaxMachinesGlobal) decides which crews show puppets (design rule: fronts never slow down).
                    int full = math.max(need * math.max(1, ps.NearCrews), proj.MachineCount);
                    if (used + full <= RRWConst.kMaxMachinesGlobal) used += full;
                    else if (proj.Crews > 1 && used + need <= RRWConst.kMaxMachinesGlobal) { used += need; degraded = true; }   // fall back to one crew's budget
                    else allow = false;
                }
                if (degraded && !ps.AllowanceDegraded)
                {
                    DirectorShared.AllowanceDegraded++;
                    RRWLog.Verbose("director: project #" + proj.Id + " machine allowance degraded to one crew budget (" + ps.NearCrews + " crew fronts near, "
                                   + used + "/" + RRWConst.kMaxMachinesGlobal + " reserved)");
                }
                ps.AllowanceDegraded = degraded;
                proj.AllowMachines = allow;
                ps.WasAllowed = allow;
            }
        }

        // FocusCrew: picks the crew whose front is nearest the camera pivot (switches only when another
        // front is kFocusHysteresis nearer), writes FrontU / FrontPosition / CameraDistance from it and returns the number of
        // crew fronts within kMachineSpawnRadius. One crew: FrontU = MainFront(View()).
        private int FocusCrew(ProjectRecord proj, DirProjectState ps, float3 cam, bool haveCam)
        {
            var em = EntityManager;
            int n = math.max(1, proj.Crews);
            if (proj.FocusCrew >= n || proj.FocusCrew < 0) proj.FocusCrew = 0;
            if (n <= 1 || !haveCam)
            {
                proj.FocusCrew = math.min(proj.FocusCrew, n - 1);
                if (n <= 1) proj.FocusCrew = 0;
                if (ChainMap.Point(em, proj, proj.FrontU, out float3 pos, out float3 _)) proj.FrontPosition = pos;
                proj.CameraDistance = haveCam ? math.distance(proj.FrontPosition.xz, cam.xz) : 0f;
                return haveCam ? (proj.CameraDistance < RRWConst.kMachineSpawnRadius ? 1 : 0) : 1;
            }
            var view = proj.View();
            int best = proj.FocusCrew, near = 0;
            float bestD = float.MaxValue, curD = float.MaxValue;
            float3 bestPos = proj.FrontPosition, curPos = proj.FrontPosition;
            float bestU = proj.FrontU, curU = proj.FrontU;
            // Scan every crew front only every 8 updates per project (staggered), after a crew-count
            // change or a latch this update; otherwise evaluate the focus crew alone (FrontU / FrontPosition stay exact every
            // update; a focus switch and NearCrews lag by at most 8 updates).
            bool fullScan = ps.FocusScanCrews != n || ps.CrewsLatchedUpdate == m_Now || ((m_Now + proj.Id) & 7u) == 0u;
            if (!fullScan)
            {
                float u = PhasePlan.CrewFront(view, proj.FocusCrew);
                if (ChainMap.Point(em, proj, u, out float3 p, out float3 _))
                {
                    proj.FrontU = u;
                    proj.FrontPosition = p;
                    proj.CameraDistance = math.distance(p.xz, cam.xz);
                    return ps.NearCrews;
                }
                fullScan = true;
            }
            ps.FocusScanCrews = n;
            for (int c = 0; c < n; c++)
            {
                float u = PhasePlan.CrewFront(view, c);
                if (!ChainMap.Point(em, proj, u, out float3 p, out float3 _)) continue;
                float d = math.distance(p.xz, cam.xz);
                if (d < RRWConst.kMachineSpawnRadius) near++;
                if (c == proj.FocusCrew) { curD = d; curPos = p; curU = u; }
                if (d < bestD) { bestD = d; best = c; bestPos = p; bestU = u; }
            }
            if (best != proj.FocusCrew && (curD == float.MaxValue || bestD < curD - DirConst.kFocusHysteresis))
            {
                RRWLog.Verbose("director: project #" + proj.Id + " focus crew " + proj.FocusCrew + " -> " + best + " (" + RRWLog.F(curD) + " -> " + RRWLog.F(bestD) + " m)");
                proj.FocusCrew = best;
                DirectorShared.FocusSwitches++;
                curD = bestD; curPos = bestPos; curU = bestU;
            }
            if (curD != float.MaxValue)
            {
                proj.FrontU = curU;
                proj.FrontPosition = curPos;
                proj.CameraDistance = curD;
            }
            else
            {
                if (ChainMap.Point(em, proj, proj.FrontU, out float3 pos, out float3 _)) proj.FrontPosition = pos;
                proj.CameraDistance = math.distance(proj.FrontPosition.xz, cam.xz);
            }
            return near;
        }

        // ------------------------------------------------------------------ crew latch

        // Machine seconds for p 0 -> 1: WorkRequired / WorkTime.Rate(rushed, 1) / 60 (the machine clock is sim frames / 60).
        internal static float WorkSecondsOf(uint req, bool rushed)
        {
            float r = WorkTime.Rate(rushed, 1f);
            return r > 0f ? req / r / 60f : 0f;
        }

        // Crew count to latch for the current view (dev override, else the Core rule).
        private int CrewCountNow(ProjectRecord proj, DirProjectState ps, in ProjectView view)
        {
            // upgrade works: one crew per band of the layout window (no dev override: the crews stand side by side)
            if (ps.CrewsOverride <= 0 || view.IsUpgrade) return PhasePlan.CrewCount(view, m_S.CrewsMax);
            if (view.Mode != VisualMode.FullDig || view.Phase == WorksPhase.Complete || view.Phase == WorksPhase.None || !(view.U > 0f)) return 1;
            float L = view.TrimmedLength > 0f ? math.min(view.TrimmedLength, view.U) : view.U;
            int cap = math.max(1, (int)math.floor(L / RRWConst.kMinSectionLength));
            return math.clamp(ps.CrewsOverride, 1, math.min(cap, RRWConst.kMaxCrewsPerProject));
        }

        // ProjectRecord.Crews is LATCHED: set at a phase change, the first update of the record (load / rebuild / new project),
        // a ModelReset (SetProgress, JumpPhase, cancel), "Rebuild visuals", a chain Revision change that changed U (or broke the
        // 60 m minimum section), the C4 stage switch Vacate -> Swap and a dev override change; never otherwise (a rush or a
        // settings change waits for the next phase).
        private void LatchCrews(ProjectRecord proj, DirProjectState ps, WorksPhase phase)
        {
            string why = null;
            if (!ps.CrewsLatched) why = "first update (new / load / rebuild)";
            else if (phase != ps.CrewsPhase) why = "phase " + ps.CrewsPhase + " -> " + phase;
            // hard resets only (SetProgress, JumpPhase, Finish, cancel, rebuild): a drift re-anchor of the model is no reason to
            // move the fronts mid-phase (a rush keeps the layout until the next phase)
            else if (ps.HardResetThisFrame) why = "model reset";
            else if (m_RebuildAllThisFrame) why = "rebuild visuals";
            if (why == null && proj.Revision != ps.CrewsRevision)
            {
                // Sections depend only on (n, U): a revision that keeps the chain length (trims moved by a road drawn into an end
                // node, an edge re-keyed, a split remnant joining) keeps the layout unless the 60 m minimum section is violated
                // now; re-latching there would move every front, decal and machine mid-phase for no reason.
                float L = math.max(0f, math.min(proj.TrimU1, proj.ChainLength) - proj.TrimU0);
                int cap = math.max(1, (int)math.floor((L > 0f ? L : proj.ChainLength) / RRWConst.kMinSectionLength));
                if (math.abs(proj.ChainLength - ps.CrewsU) > 0.01f || proj.Crews > cap)
                    why = "chain revision " + ps.CrewsRevision + " -> " + proj.Revision + " (U " + RRWLog.F(ps.CrewsU) + " -> " + RRWLog.F(proj.ChainLength) + ")";
                else ps.CrewsRevision = proj.Revision;
            }
            if (why == null && ps.CrewsRelatch && phase == WorksPhase.Finishing) why = "C4 switch Vacate -> Swap";
            if (why == null && ps.CrewsOverride != ps.CrewsOverrideApplied) why = "dev SetCrews " + (ps.CrewsOverride > 0 ? ps.CrewsOverride.ToString() : "auto");
            // upgrade works: one crew per band of the layout window, so a window change re-latches even when the phase stays
            int upgradeWindow = IsUpgradeProject(proj) ? proj.Upgrade.LayoutWindowAt(proj.Progress) : int.MinValue;
            if (why == null && upgradeWindow != ps.CrewsWindow) why = "upgrade window " + (ps.CrewsWindow + 1) + " -> " + (upgradeWindow + 1);
            ps.CrewsRelatch = false;
            // rrw.check bookkeeping for "Rollers > 0 while RollersOn has been off for more than one phase"
            bool rollersOn = m_S.RollersOn;
            if (rollersOn) ps.RollersOffPhase = WorksPhase.None;
            else if (ps.RollersOffPhase == WorksPhase.None) ps.RollersOffPhase = phase;
            if (why == null) return;
            LatchRollers(proj, ps, rollersOn, why);

            bool phaseChange = !ps.CrewsLatched || phase != ps.CrewsPhase;
            if (phaseChange && ps.CrewsLatched && ps.CrewsOverride > 0 && ps.CrewsOverride == ps.CrewsOverrideApplied)
            {
                // a dev SetCrews pins the count until the next phase change only
                RRWLog.Info("director: project #" + proj.Id + " dev crew override " + ps.CrewsOverride + " ends at the phase change");
                ps.CrewsOverride = 0;
            }
            var view = proj.View();
            // The first latch of a record (load / registry rebuild) and "Rebuild visuals" restore the SAVED
            // latch of this phase (SiteFlags bits 12..15) instead of re-deriving it from the current WorkSeconds / rush / setting /
            // swap verdict: the done set is not monotonic in n, so a different count would un-dig terrain and move decals / heaps.
            int n = 0;
            if ((!ps.CrewsLatched || why == "rebuild visuals") && ps.CrewsOverride <= 0 && upgradeWindow == int.MinValue)
            {
                int saved = PhasePlan.LatchedCrewsOf(proj.Flags, phase);
                if (saved > 0)
                {
                    float Ls = view.TrimmedLength > 0f ? math.min(view.TrimmedLength, view.U) : view.U;
                    int capS = math.max(1, (int)math.floor(Ls / RRWConst.kMinSectionLength));
                    n = math.clamp(saved, 1, math.min(capS, RRWConst.kMaxCrewsPerProject));
                    why += ", saved layout " + saved;
                    DirectorShared.CrewLatchesRestored++;
                }
            }
            if (n <= 0) n = math.clamp(CrewCountNow(proj, ps, view), 1, RRWConst.kMaxCrewsPerProject);
            // the upgrade crew count follows from the bands of the window: nothing to save
            if (upgradeWindow == int.MinValue)
            {
                try { WriteLatchedCrews(proj, n, phase); }
                catch (Exception e) { RRWLog.ErrorOnce("director crew latch save", e); }
            }
            int before = proj.Crews;
            ps.CrewsLatched = true;
            ps.CrewsPhase = phase;
            ps.CrewsRevision = proj.Revision;
            ps.CrewsU = proj.ChainLength;
            ps.CrewsOverrideApplied = ps.CrewsOverride;
            ps.CrewsWhy = why;
            ps.CrewsLatchedUpdate = m_Now;
            ps.CrewsWindow = upgradeWindow;
            DirectorShared.CrewLatches++;
            proj.Crews = n;
            if (proj.FocusCrew >= n) proj.FocusCrew = n - 1;
            if (n == before) return;
            DirectorShared.CrewChanges++;
            if (!phaseChange) DirectorShared.CrewChangesMidPhase++;
            view.Crews = n;
            float v1 = PhasePlan.SingleFrontSpeed(view.Phase, view.U, view.WorkSeconds, view.Phase == WorksPhase.Finishing && view.SwapActive);
            RRWLog.Info("director: project #" + proj.Id + " crews " + before + " -> " + n + " (" + why + ", " + proj.Kind + " " + phase
                        + " f=" + RRWLog.F(proj.PhaseFraction) + ", per-crew front " + RRWLog.F(PhasePlan.CrewFrontSpeed(view)) + " m/s, work limit "
                        + RRWLog.F(MachineLimits.WorkSpeed(proj.Kind, phase)) + " m/s, one crew would need " + RRWLog.F(v1) + " m/s, trimmed "
                        + RRWLog.F(view.TrimmedLength) + " m, workSeconds " + RRWLog.F(proj.WorkSeconds) + ", setting " + m_S.CrewsMax
                        + (ps.CrewsOverride > 0 ? ", dev override " + ps.CrewsOverride : "") + ")");
        }

        // ProjectRecord.Rollers is latched with Crews, at the same events (phase change,
        // first update / load / rebuild, hard ModelReset, "Rebuild visuals", a revision that changed U, C4 Vacate -> Swap, dev
        // SetCrews): RRWSetting.RollersOn ? kMaxRollersPerCrew : 0. Never saved (re-derived on the first update after a load). A
        // settings change mid-phase waits for the next latch; Machines also gates spawns on RollersOn live (off: rollers leave).
        private static void LatchRollers(ProjectRecord proj, DirProjectState ps, bool rollersOn, string why)
        {
            int before = proj.Rollers;
            int want = rollersOn ? RRWConst.kMaxRollersPerCrew : 0;
            bool first = !ps.RollersLatched;
            ps.RollersLatched = true;
            proj.Rollers = want;
            if (want == before) return;
            string line = "director: project #" + proj.Id + " rollers " + before + " -> " + want + " (" + why + ", " + proj.Kind + " " + proj.Phase
                          + ", setting " + (rollersOn ? "on" : "off") + ")";
            if (first && before == 0) RRWLog.Verbose(line);   // a fresh record starts at 0: the first latch is routine
            else RRWLog.Info(line);
        }

        // Saves the latched crew count of the running phase on every edge of the project (SiteFlags bits
        // 12..15). Only edges whose bits differ are written; proj.Flags (first edge's flags) follows at once.
        private void WriteLatchedCrews(ProjectRecord proj, int n, WorksPhase phase)
        {
            var em = EntityManager;
            for (int i = 0; i < proj.Edges.Count; i++)
            {
                Entity e = proj.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var s = em.GetComponentData<RoadWorksSite>(e);
                var want = PhasePlan.EncodeLatchedCrews(s.Flags, n, phase);
                if (want == s.Flags) continue;
                s.Flags = want;
                em.SetComponentData(e, s);
            }
            proj.Flags = PhasePlan.EncodeLatchedCrews(proj.Flags, n, phase);
        }

        // XZ distance from the camera pivot to the nearest centre-line sample of the chain (17 samples per edge, the same
        // sampling Props uses for its far rule: < 0.5 m error on a 164 m edge against the 600 / 800 m radii).
        // The samples are cached per project (DirProjectState.ChainSamples) and rebuilt only when an
        // edge's EdgeArc object or the edge list changed, so an update costs one registry lookup per edge plus 17 distances.
        private static float ChainDistance(ProjectRecord proj, DirProjectState ps, float3 cam)
        {
            const int n = 16;
            int edges = proj.Edges.Count;
            bool rebuild = ps.ChainSampleEdges != edges || ps.ChainSampleArcs.Length < edges;
            for (int i = 0; i < edges && !rebuild; i++)
            {
                object arc = SiteRegistry.TryGetEdge(proj.Edges[i], out var r0) ? r0.Arc : null;
                if (!ReferenceEquals(arc, ps.ChainSampleArcs[i])) rebuild = true;
            }
            if (rebuild)
            {
                if (ps.ChainSampleArcs.Length < edges) ps.ChainSampleArcs = new object[edges];
                if (ps.ChainSamples.Length < edges * (n + 1)) ps.ChainSamples = new float2[edges * (n + 1)];
                ps.ChainSampleEdges = edges;
                ps.ChainSampleCount = 0;
                for (int i = 0; i < edges; i++)
                {
                    EdgeArc arc = SiteRegistry.TryGetEdge(proj.Edges[i], out var r) ? r.Arc : null;
                    ps.ChainSampleArcs[i] = arc;
                    if (arc == null) continue;
                    for (int k = 0; k <= n; k++) ps.ChainSamples[ps.ChainSampleCount++] = arc.Position(arc.Length * k / n).xz;
                }
            }
            float best = float.MaxValue;
            float2 c = cam.xz;
            for (int i = 0; i < ps.ChainSampleCount; i++)
            {
                float dd = math.distancesq(ps.ChainSamples[i], c);
                if (dd < best) best = dd;
            }
            return best == float.MaxValue ? proj.CameraDistance : math.sqrt(best);
        }

        private float3 CameraPivot(out bool ok)
        {
            ok = false;
            try
            {
                var c = m_CameraSystem != null ? m_CameraSystem.gamePlayController : null;
                if (c != null)
                {
                    var v = c.pivot;
                    ok = true;
                    return new float3(v.x, v.y, v.z);
                }
                if (m_CameraSystem != null) { ok = true; return m_CameraSystem.position; }
            }
            catch (Exception e) { RRWLog.ErrorOnce("director camera", e); }
            return default;
        }
    }
}
