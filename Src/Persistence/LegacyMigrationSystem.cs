using System;
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using LegacyKind = RealisticRoadWorks.RoadWorksKind;
using LegacyProp = RealisticRoadWorks.RoadWorksProp;
using LegacyPropElement = RealisticRoadWorks.RoadWorksPropElement;
using LegacyRequest = RealisticRoadWorks.RoadWorksRequest;
using LegacyWorks = RealisticRoadWorks.RoadWorks;
using NetElevation = Game.Net.Elevation;

namespace RealisticRoadWorks.V3.Persistence
{
    // Modification1, order 215: after the Director's first-frame registry rebuild (NextProjectId already above every saved
    // id), before GenerateAreasSystem. Converts v2 saves:
    //   v2 RoadWorks edge -> one single-edge v3 project via SiteFactory at the v2 progress (Migrated flag);
    //   v2 maintenance suspension undone, lanes refreshed, v2 icons removed, v2 props deleted, every v2 component stripped.
    // The Director picks the new sites up next frame (normal new-site path). Also hosts the save-guard watchdog: if SaveRestore never
    // ran after a save, Temp is taken off the marked entities here.
    [RegisterSystem(SystemUpdatePhase.Modification1, Before = typeof(GenerateAreasSystem), Order = RRWOrder.LegacyMigration)]
    public partial class LegacyMigrationSystem : GameSystemBase
    {
        private static readonly string[] s_V2IconNames = { "Road Maintenance Vehicle", "Road Maintenance Depot", "Traffic Accident" };

        private SimulationSystem m_Simulation;
        private PrefabSystem m_PrefabSystem;
        private IconCommandSystem m_IconCommandSystem;
        private EntityQuery m_LegacyWorks, m_LegacyProps, m_LegacyPropBuffers, m_LegacyRequests, m_Sites;
        private readonly RRWGuard m_Guard = new RRWGuard("persistence LegacyMigration");
        private readonly List<SiteFactoryEdge> m_One = new List<SiteFactoryEdge>(1);
        private readonly List<SiteFactoryResult> m_Results = new List<SiteFactoryResult>(1);

        // rrw.check: v2 components still present after a migration pass
        public static int LegacyLeft;

        // Pause works was removed. The Director clears SiteFlags.LegacyPaused on its first-frame
        // rebuild (single writer); Persistence only VERIFIES it after a load (read-only) and never sets the bit (the v2
        // migration below only passes Migrated | Rushed). LegacyPausedLeft feeds rrw.check / rrw.persist.
        private const int kPausedCheckUpdates = 180;   // watch ~3 s after the registry is loaded
        private int m_PausedCheckLeft;
        private int m_PausedFirstCount = -1;
        public static int LegacyPausedLeft;

        // Save safety: one read-only lane snapshot in the first update after a load, before Traffic re-applies
        // (this system runs at Mod1 215; Traffic's requests at 220 and its lane writes in ModificationEnd). Waits for the
        // Director's registry rebuild (210, same frame) or kLoadScanMaxWait updates in game mode.
        private const int kLoadScanMaxWait = 30;
        private bool m_LoadScanPending;
        private int m_LoadScanWait;
#if DEVTOOLS
        // Load audit: dust puff ages / LivePath / Owner and roller GameObjects through loads (R5Audit, dev only)
        private EntityQuery m_R5Derived;
#endif

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_IconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
            m_LegacyWorks = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<LegacyWorks>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
            m_LegacyProps = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<LegacyProp>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_LegacyPropBuffers = GetEntityQuery(ComponentType.ReadOnly<LegacyPropElement>());
            m_LegacyRequests = GetEntityQuery(ComponentType.ReadOnly<LegacyRequest>());
            m_Sites = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadWorksSite>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
#if DEVTOOLS
            m_R5Derived = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RRWDerived>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            R5AuditQuery = m_R5Derived;
#endif
        }

#if DEVTOOLS
        internal static EntityQuery R5AuditQuery;   // shared with the checker / rrw.save.scan (same World)
#endif

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // The upgrade tails exactly as the reader left them, before any system updates the sites (a window start stamps its
            // primitive, a cleared AllAtOnce bit): compared with the last save of this session in the load snapshot.
            try { UpgradeTails.Load = UpgradeTails.Take(m_Sites, true, UpgradeTails.LoadBadTails); }
            catch (Exception e) { RRWLog.ErrorOnce("persistence load tail census", e); }
#if DEVTOOLS
            R5Audit.OnLoaded(EntityManager, m_R5Derived, purpose, mode);
#endif
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            // entities of the previous city are gone (and their indices may be reused): never touch them again
            SaveGuard.Pending.Clear();
            SaveGuard.PendingFrame = -1;
            LegacyLeft = 0;
            LegacyPausedLeft = 0;
            m_PausedCheckLeft = kPausedCheckUpdates;
            m_PausedFirstCount = -1;
            LaneScan.ResetForLoad();
            UpgradeTails.Load = default;
            UpgradeTails.LoadBadTails.Clear();
            m_LoadScanPending = true;
            m_LoadScanWait = 0;
        }

        protected override void OnUpdate()
        {
            // save-guard watchdog (cheap): the frame after a save at the latest
            if (SaveGuard.Pending.Count > 0 && SaveGuard.PendingFrame != UnityEngine.Time.frameCount)
            {
                try
                {
                    int n = SaveGuard.Restore(EntityManager);
                    RRWLog.Warn("persistence: save restore did not run after the last save; removed Temp from " + n + " entities");
                }
                catch (Exception e) { RRWLog.ErrorOnce("persistence save watchdog", e); SaveGuard.Pending.Clear(); }
            }

            if (RRWGates.UpgradeVisualDrop) LaneScan.VisualDropEverOn = true;   // Forbidden on drop lanes is ours from now on
            if (m_LoadScanPending) LoadScan();
#if DEVTOOLS
            try { R5Audit.Tick(EntityManager, m_R5Derived); }
            catch (Exception e) { RRWLog.ErrorOnce("persistence r5 audit", e); }
#endif
            if (m_Guard.Faulted) return;
            if (m_PausedCheckLeft > 0 && SiteRegistry.Loaded) CheckLegacyPaused();
            if (m_LegacyWorks.IsEmptyIgnoreFilter && m_LegacyProps.IsEmptyIgnoreFilter && m_LegacyPropBuffers.IsEmptyIgnoreFilter && m_LegacyRequests.IsEmptyIgnoreFilter)
            {
                LegacyLeft = 0;
                return;
            }
            long t0 = RRWPerf.Start();
            try
            {
                Migrate();
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
            }
            finally
            {
                RRWPerf.Stop(PerfSlot.Persistence, t0);
            }
        }

        private void Migrate()
        {
            var em = EntityManager;
            var gm = GameManager.instance;
            bool game = gm != null && gm.gameMode == GameMode.Game;
            var settings = RRWSettings.Current;
            bool createSites = game && settings != null;
            uint now = m_Simulation.frameIndex;

            int converted = 0, skipped = 0, consumers = 0, icons = 0, props = 0;
            var edges = m_LegacyWorks.ToEntityArray(Allocator.Temp);
            var works = m_LegacyWorks.ToComponentDataArray<LegacyWorks>(Allocator.Temp);
            try
            {
                Entity iconPrefab = game ? V2IconPrefab() : Entity.Null;
                IconCommandBuffer iconBuffer = default;
                bool haveIconBuffer = false;
                if (iconPrefab != Entity.Null && edges.Length > 0)
                {
                    iconBuffer = m_IconCommandSystem.CreateCommandBuffer();
                    haveIconBuffer = true;
                }
                if (createSites && edges.Length > 0 && !SiteRegistry.Loaded) EnsureProjectIds();

                for (int i = 0; i < edges.Length; i++)
                {
                    Entity edge = edges[i];
                    var w = works[i];
                    bool isRoadEdge = EcsUtil.Alive(em, edge) && em.HasComponent<Edge>(edge) && em.HasComponent<Curve>(edge);
                    if (!isRoadEdge) { skipped++; continue; }

                    if ((w.m_Flags & LegacyWorks.kMaintenanceSuspended) != 0 && em.HasComponent<Road>(edge)
                        && !em.HasComponent<MaintenanceConsumer>(edge))
                    {
                        em.AddComponentData(edge, default(MaintenanceConsumer));
                        consumers++;
                    }
                    if (haveIconBuffer) { iconBuffer.Remove(edge, iconPrefab); icons++; }

                    if (createSites && !em.HasComponent<RoadWorksSite>(edge))
                    {
                        // v2 progress by sim frames (guard against a start frame ahead of the clock: unsigned wrap)
                        float p = now <= w.m_StartFrame || w.m_EndFrame <= w.m_StartFrame
                            ? (w.m_EndFrame <= w.m_StartFrame ? 1f : 0f)
                            : math.saturate((now - w.m_StartFrame) / (float)(w.m_EndFrame - w.m_StartFrame));
                        var kind = w.m_Kind == LegacyKind.Demolition ? WorksKind.Demolition : WorksKind.Construction;
                        SiteFlags extra = SiteFlags.Migrated | ((w.m_Flags & LegacyWorks.kRushed) != 0 ? SiteFlags.Rushed : SiteFlags.None);
                        m_One.Clear();
                        m_One.Add(new SiteFactoryEdge
                        {
                            Edge = edge,
                            StartNode = em.GetComponentData<Edge>(edge).m_Start,
                            EndNode = em.GetComponentData<Edge>(edge).m_End,
                            Length = math.max(0.01f, em.GetComponentData<Curve>(edge).m_Length),
                            DigEligible = EcsUtil.DigEligible(em, edge, out _),
                            PaidCost = kind == WorksKind.Construction ? EstimateConstructionCost(edge) : 0,
                            Dependants = em.HasBuffer<ConnectedBuilding>(edge) && em.GetBuffer<ConnectedBuilding>(edge, true).Length > 0,
                        });
                        m_Results.Clear();
                        SiteFactory.CreateProjects(m_One, kind, EcsUtil.RoadClass(em, edge), settings, extra, m_Results);
                        if (m_Results.Count > 0)
                        {
                            var site = m_Results[0].Site;
                            site.m_WorkDone = math.min(site.m_WorkRequired, (uint)math.round(math.saturate(p) * site.m_WorkRequired));
                            site.Set(SiteFlags.Migrated, true);
                            em.AddComponentData(edge, site);
                            converted++;
                            RRWLog.Info("persistence: migrated v2 " + kind + " edge " + RRWLog.E(edge) + " p=" + RRWLog.F(p)
                                        + " -> project p" + site.m_ProjectId + " mode=" + site.Mode);
                        }
                    }
                    EcsUtil.RefreshLanes(em, edge, false);
                }
                if (haveIconBuffer) m_IconCommandSystem.AddCommandBufferWriter(default(JobHandle));
            }
            finally
            {
                edges.Dispose();
                works.Dispose();
            }

            // v2 props: delete; then strip every v2 component (v3 never saves them again)
            if (!m_LegacyProps.IsEmptyIgnoreFilter)
            {
                var arr = m_LegacyProps.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < arr.Length; i++) { EcsUtil.MarkDeleted(em, arr[i]); props++; }
                arr.Dispose();
                em.RemoveComponent<LegacyProp>(m_LegacyProps);
            }
            int buffers = m_LegacyPropBuffers.CalculateEntityCount();
            if (buffers > 0) em.RemoveComponent<LegacyPropElement>(m_LegacyPropBuffers);
            int requests = m_LegacyRequests.CalculateEntityCount();
            if (requests > 0) em.RemoveComponent<LegacyRequest>(m_LegacyRequests);
            int stripped = m_LegacyWorks.CalculateEntityCount();
            if (stripped > 0) em.RemoveComponent<LegacyWorks>(m_LegacyWorks);

            LegacyLeft = m_LegacyWorks.CalculateEntityCount() + m_LegacyProps.CalculateEntityCount()
                         + m_LegacyPropBuffers.CalculateEntityCount() + m_LegacyRequests.CalculateEntityCount();
            RRWLog.Info("persistence: v2 migration" + (createSites ? "" : game ? " (no settings: works dropped)" : " (not in game mode: v2 data stripped only)")
                        + " converted=" + converted + " skipped=" + skipped + " maintenanceRestored=" + consumers + " iconsRemoved=" + icons
                        + " propsDeleted=" + props + " propBuffers=" + buffers + " requests=" + requests + " worksStripped=" + stripped
                        + (LegacyLeft > 0 ? " LEFT=" + LegacyLeft : ""));
        }

        private void LoadScan()
        {
            var gm = GameManager.instance;
            if (gm == null || gm.gameMode != GameMode.Game) return;
            if (!SiteRegistry.Loaded && ++m_LoadScanWait < kLoadScanMaxWait) return;
            m_LoadScanPending = false;
            long t0 = RRWPerf.Start();
            try
            {
                EntityManager.CompleteAllTrackedJobs();
                LaneScan.TakeLoadSnapshot(EntityManager, m_Sites);
            }
            catch (Exception e) { RRWLog.ErrorOnce("persistence load lane scan", e); }
            finally { RRWPerf.Stop(PerfSlot.Persistence, t0); }
        }

        // v2 used the first icon of this list that exists (RoadWorksUtils.GetIconPrefab).
        private Entity V2IconPrefab()
        {
            foreach (var name in s_V2IconNames)
            {
                try
                {
                    if (m_PrefabSystem.TryGetPrefab(new PrefabID(nameof(NotificationIconPrefab), name), out PrefabBase p)
                        && m_PrefabSystem.TryGetEntity(p, out Entity e)) return e;
                }
                catch { }
            }
            return Entity.Null;
        }

        // Read-only: counts saved sites that still carry the removed paused bit, every 16 updates for kPausedCheckUpdates
        // after the registry is loaded. The Director's rebuild (order 210) runs before this system (215), so the first
        // count is normally already 0; a non-zero count at the end means the Director did not clear it (Warn once).
        private void CheckLegacyPaused()
        {
            m_PausedCheckLeft--;
            if (m_PausedFirstCount >= 0 && (m_PausedCheckLeft & 15) != 0 && m_PausedCheckLeft > 0) return;
            int n = CountLegacyPaused(EntityManager, m_Sites);
            LegacyPausedLeft = n;
            if (m_PausedFirstCount < 0)
            {
                m_PausedFirstCount = n;
                if (n > 0) RRWLog.Info("persistence: " + n + " saved sites still carry the removed 'paused' bit after the registry rebuild (waiting for the Director to clear it)");
                else RRWLog.Verbose("persistence: no saved site carries the removed 'paused' bit after load");
            }
            if (n == 0)
            {
                if (m_PausedFirstCount > 0) RRWLog.Info("persistence: removed 'paused' bit cleared on every saved site (old paused works resume)");
                m_PausedCheckLeft = 0;
                return;
            }
            if (m_PausedCheckLeft == 0)
                RRWLog.Warn("persistence: " + n + " saved sites still carry the removed 'paused' bit " + kPausedCheckUpdates
                            + " updates after load (Director should clear SiteFlags.LegacyPaused on rebuild)");
        }

        public static int CountLegacyPaused(EntityManager em, EntityQuery sites)
        {
            if (sites.IsEmptyIgnoreFilter) return 0;
            var arr = sites.ToComponentDataArray<RoadWorksSite>(Allocator.Temp);
            int n = 0;
            for (int i = 0; i < arr.Length; i++) if (arr[i].Has(SiteFlags.LegacyPaused)) n++;
            arr.Dispose();
            return n;
        }

        private void EnsureProjectIds() => SiteRegistry.EnsureIdsFromSaved(m_Sites);   // Core helper

        // v2 never stored what the player paid; estimate it like v2's RoadWorksUtils.ConstructionCost so a migrated
        // construction still refunds something when cancelled.
        private int EstimateConstructionCost(Entity edge)
        {
            try
            {
                var em = EntityManager;
                if (!em.HasComponent<Composition>(edge)) return 0;
                Entity comp = em.GetComponentData<Composition>(edge).m_Edge;
                if (comp == Entity.Null || !em.Exists(comp) || !em.HasComponent<PlaceableNetComposition>(comp)) return 0;
                var e = em.GetComponentData<Edge>(edge);
                var none = default(NetElevation);
                var start = em.HasComponent<NetElevation>(e.m_Start) ? em.GetComponentData<NetElevation>(e.m_Start) : none;
                var end = em.HasComponent<NetElevation>(e.m_End) ? em.GetComponentData<NetElevation>(e.m_End) : none;
                return math.max(0, NetUtils.GetConstructionCost(em.GetComponentData<Curve>(edge), start, end, em.GetComponentData<PlaceableNetComposition>(comp)));
            }
            catch (Exception ex)
            {
                RRWLog.ErrorOnce("persistence cost estimate", ex);
                return 0;
            }
        }
    }
}
