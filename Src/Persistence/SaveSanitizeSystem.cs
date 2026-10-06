using System;
using System.Collections.Generic;
using Game;
using Game.Net;
using Game.Prefabs;
using Game.Serialization;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;

namespace RealisticRoadWorks.V3.Persistence
{
    // Serialize, right before SerializerSystem (order 900). Safety net so a save never contains anything
    // the mod derives at runtime: (a) our notification icons on works edges, (b) RRWDerived entities that somehow lack
    // LivePath, (c) every descendant of a puppet / derived prop without LivePath. They get Temp (excluded by the
    // SerializerSystem query) and SaveRestoreSystem removes it right after the write. (b) and (c) should always be 0.
    [RegisterSystem(SystemUpdatePhase.Serialize, Before = typeof(SerializerSystem), Order = RRWOrder.SaveSanitize)]
    public partial class SaveSanitizeSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private SaveScan m_Scan;
        private EntityQuery m_SitesWithoutEdge;
        private EntityQuery m_Sites;
        private readonly List<Entity> m_Found = new List<Entity>();
        private readonly RRWGuard m_Guard = new RRWGuard("persistence SaveSanitize");

        internal static SaveScan Scan;   // shared with the checker and the dev dry run

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Scan = new SaveScan(EntityManager,
                GetEntityQuery(SaveScan.IconsDesc()), GetEntityQuery(SaveScan.DerivedDesc()),
                GetEntityQuery(SaveScan.RootsDesc()), GetEntityQuery(SaveScan.OwnedDesc()));
            Scan = m_Scan;
            m_SitesWithoutEdge = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadWorksSite>() },
                None = new[] { ComponentType.ReadOnly<Edge>() },
            });
            m_Sites = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadWorksSite>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
            RRWIntrospection.RegisterChecker("Persistence", Check);
            RRWIntrospection.RegisterDumper("Persistence", Dump);
        }

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted) return;
            long t0 = RRWPerf.Start();
            try
            {
                EntityManager.CompleteAllTrackedJobs();
                // A previous sanitize whose restore never ran (should not happen): undo it first.
                if (SaveGuard.Pending.Count > 0)
                {
                    int r = SaveGuard.Restore(EntityManager);
                    RRWLog.Warn("persistence: previous save guard was still active, restored " + r);
                }
                m_Found.Clear();
                var counts = m_Scan.Run(m_Found, IconPrefab(PrefabNames.IconClosed), IconPrefab(PrefabNames.IconSlowZone), true);
                var em = EntityManager;
                for (int i = 0; i < m_Found.Count; i++)
                {
                    Entity e = m_Found[i];
                    if (!em.Exists(e) || em.HasComponent<Temp>(e)) continue;
                    em.AddComponent<Temp>(e);
                    SaveGuard.Pending.Add(e);
                }
                SaveGuard.PendingFrame = UnityEngine.Time.frameCount;
                var tails = SaveTails();
                string msg = "persistence: save sanitize icons=" + counts.Icons + " derivedNoLivePath=" + counts.Derived
                             + " descendantsNoLivePath=" + counts.Descendants + " works edges=" + SiteRegistry.Edges.Count
                             + " upgrade " + tails.Text();
                if (counts.Derived + counts.Descendants > 0) RRWLog.Warn(msg + " (derived entities without LivePath were kept out of the save)");
                else RRWLog.Info(msg);
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

        // The upgrade tails this save writes (UpgradeTails.LastSave, compared with the next load). The serializer writes every mode 2
        // construction as finished in its v1 fields; a tail that fails validation is still written and dropped by the reader (the
        // site then finishes as mode D), and a plan-less mode 2 construction finishes the same way. Both are logged once per save.
        private TailCensus SaveTails()
        {
            var bad = m_TailEdges;
            var noPlan = m_NoPlanEdges;
            var c = UpgradeTails.Take(m_Sites, false, bad, noPlan);
            UpgradeTails.LastSave = c;
            if (c.Invalid > 0)
                RRWLog.Warn("persistence: " + c.Invalid + " upgrade plans fail validation and are written anyway: the next load drops them and those sites "
                            + "finish as mode D (edges " + LaneScan.Edges(bad) + ")");
            if (c.HalfNoPlan > 0)
                RRWLog.Warn("persistence: " + c.HalfNoPlan + " mode 2 constructions without an upgrade plan are written as finished (edges " + LaneScan.Edges(noPlan) + ")");
            if (c.HalfOther > 0)
                RRWLog.Warn("persistence: " + c.HalfOther + " mode 2 sites are not constructions (a cancelled upgrade site?): they reload as mode D");
            return c;
        }

        private readonly List<Entity> m_TailEdges = new List<Entity>(8);
        private readonly List<Entity> m_NoPlanEdges = new List<Entity>(8);

        private Entity IconPrefab(string name)
        {
            try { return PrefabCatalog.Icon(m_PrefabSystem, name); }
            catch { return Entity.Null; }
        }

        // rrw.check hook: no RRWDerived without LivePath, no puppet descendant without LivePath,
        // no RoadWorksSite on an entity without Edge, no v2 leftovers.
        private void Check(EntityManager em, List<string> problems)
        {
            var c = m_Scan.Run(null, Entity.Null, Entity.Null, false);
            if (c.Derived > 0) problems.Add("persistence: " + c.Derived + " RRWDerived entities without LivePath (would be saved)");
            if (c.Descendants > 0) problems.Add("persistence: " + c.Descendants + " puppet/prop descendants without LivePath (would be saved)");
            int noEdge = m_SitesWithoutEdge.CalculateEntityCount();
            if (noEdge > 0) problems.Add("persistence: " + noEdge + " RoadWorksSite on entities without Edge");
            if (LegacyMigrationSystem.LegacyLeft > 0) problems.Add("persistence: " + LegacyMigrationSystem.LegacyLeft + " v2 components left after migration");
            if (SaveGuard.Pending.Count > 0) problems.Add("persistence: save guard still holds " + SaveGuard.Pending.Count + " Temp-marked entities");
            // Saved modes 0 / 2 / 3 are valid; mode 2 (HalfWidth: upgrade works) must never be marked Incompatible.
            if (!m_Sites.IsEmptyIgnoreFilter)
            {
                var arr = m_Sites.ToComponentDataArray<RoadWorksSite>(Unity.Collections.Allocator.Temp);
                int unknown = 0, halfIncompatible = 0, crewsNoCancel = 0, crewsMixed = 0, halfNoPlan = 0, planNotHalf = 0, badTail = 0;
                // SiteFlags bits 9-11 (CancelCrewsMask) hold the crew count of a CANCELLED construction; they are
                // valid saved bits (never Incompatible), meaningful only with CancelledBuild and identical on every edge of a project.
                Dictionary<uint, SiteFlags> cancelCrews = null;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (!LaneScan.ModeValid(arr[i].Mode)) unknown++;
                    else if (arr[i].Mode == VisualMode.HalfWidth && arr[i].Has(SiteFlags.Incompatible)) halfIncompatible++;
                    // upgrade tails: mode 2 always carries a plan kind (the reader turns plan-less mode 2 sites into mode D), a plan
                    // kind only on a mode 2 construction (the only site whose tail is written), and the tail is consistent
                    if (arr[i].Mode == VisualMode.HalfWidth && arr[i].Plan == PlanKind.None) halfNoPlan++;
                    else if (arr[i].Plan != PlanKind.None && !arr[i].WritesTail) planNotHalf++;
                    else if (!arr[i].TailValid()) badTail++;
                    SiteFlags cc = arr[i].Flags & SiteFlags.CancelCrewsMask;
                    if (cc != SiteFlags.None && !arr[i].Has(SiteFlags.CancelledBuild)) crewsNoCancel++;
                    if (arr[i].m_ProjectId == 0) continue;
                    if (cancelCrews == null) cancelCrews = new Dictionary<uint, SiteFlags>();
                    if (!cancelCrews.TryGetValue(arr[i].m_ProjectId, out SiteFlags seen)) cancelCrews.Add(arr[i].m_ProjectId, cc);
                    else if (seen != cc) crewsMixed++;
                }
                arr.Dispose();
                if (unknown > 0) problems.Add("persistence: " + unknown + " saved sites with an unknown VisualMode (valid: 0 FullDig, 2 HalfWidth, 3 Minimal)");
                if (halfIncompatible > 0) problems.Add("persistence: " + halfIncompatible + " HalfWidth (mode 2) sites marked Incompatible (mode 2 is valid)");
                if (crewsNoCancel > 0) problems.Add("persistence: " + crewsNoCancel + " saved sites carry cancel crew bits (SiteFlags 9-11) without CancelledBuild (Tools / Director must drop them together)");
                if (halfNoPlan > 0) problems.Add("persistence: " + halfNoPlan + " mode 2 (HalfWidth) sites without an upgrade plan (a save writes the constructions among them as finished; they reload as mode D)");
                if (planNotHalf > 0) problems.Add("persistence: " + planNotHalf + " sites carry an upgrade plan but are not mode 2 constructions (a save drops the plan)");
                if (badTail > 0) problems.Add("persistence: " + badTail + " upgrade plans fail validation (would be dropped on load)");
                if (crewsMixed > 0) problems.Add("persistence: " + crewsMixed + " saved sites disagree with their project's cancel crew count (SiteFlags 9-11)");
            }
            // Save safety: an RRW restriction ref that survived the last load / would be written by the last save.
            if (LaneScan.LoadTaken && LaneScan.LoadCity.RrwRefs + LaneScan.LoadWorks.RrwRefs > 0)
                problems.Add("persistence: the last load brought " + (LaneScan.LoadCity.RrwRefs + LaneScan.LoadWorks.RrwRefs) + " RRW access-restriction refs (sentinel/dangling) out of the save");
            if (LaneScan.AuditTaken && LaneScan.AuditWorks.RrwRefs > 0)
                problems.Add("persistence: the last save wrote " + LaneScan.AuditWorks.RrwRefs + " RRW access-restriction refs on works lanes (" + LaneScan.AuditWhen + ")");
            // Upgrade works: the lane values our lane blockers cause (blockage; Forbidden of the soft visual drop) never reach a save.
            if (LaneScan.AuditTaken && LaneScan.AuditDropBlocked > 0)
                problems.Add("persistence: the last save wrote " + LaneScan.AuditDropBlocked + " blocked mode H drop lanes (" + LaneScan.AuditWhen
                             + "; Traffic's save guard must neutralise the blockers' lane values)");
            if (LaneScan.AuditTaken && LaneScan.VisualDropEverOn && LaneScan.AuditDropForbidden > 0)
                problems.Add("persistence: the last save wrote " + LaneScan.AuditDropForbidden + " Forbidden mode H drop lanes while the visual drop was in use (" + LaneScan.AuditWhen + ")");
            // Pause works was removed; the Director clears the old bit on its first-frame rebuild.
            if (SiteRegistry.Loaded)
            {
                int paused = LegacyMigrationSystem.CountLegacyPaused(em, m_Sites);
                if (paused > 0) problems.Add("persistence: " + paused + " saved sites still carry the removed 'paused' bit (SiteFlags.LegacyPaused; Director must clear it)");
            }
#if DEVTOOLS
            // Dust puffs (age, LivePath, Owner) and roller GameObjects through loads / outside game mode
            if (LegacyMigrationSystem.R5AuditQuery != default) R5Audit.Check(em, LegacyMigrationSystem.R5AuditQuery, problems);
#endif
        }

        private string Dump(EntityManager em, Entity edge)
        {
            if (!em.Exists(edge) || !em.HasComponent<RoadWorksSite>(edge)) return null;
            var s = em.GetComponentData<RoadWorksSite>(edge);
            return "saved v" + RoadWorksSite.kVersion + "/" + RoadWorksSite.kPayloadV2 + "B mode=" + s.Mode
                   + (s.Plan != PlanKind.None ? " plan=" + s.Plan + " class=" + s.UpClass + " bands=" + s.m_BandCount + " windows=" + s.m_WindowCount
                       + (s.TailValid() ? "" : " TAIL-INVALID") : "")
                   + (s.m_LoadNote == RoadWorksSite.kNoteHalfWidthNoPlan ? " loaded-as-older-mode2-resave" : s.m_LoadNote == RoadWorksSite.kNoteBadTail ? " loaded-tail-dropped" : "")
                   + (s.Mode == VisualMode.HalfWidth && s.Plan == PlanKind.None ? "(no upgrade plan: written as finished)"
                      : LaneScan.ModeValid(s.Mode) ? "" : "(UNKNOWN)")
                   + (s.Has(SiteFlags.Migrated) ? " migrated-from-v2" : "")
                   + (s.Has(SiteFlags.Incompatible) ? " INCOMPATIBLE" : "")
                   + (s.Has(SiteFlags.LegacyPaused) ? " LEGACY-PAUSED-BIT" : "")
                   + ((s.Flags & SiteFlags.CancelCrewsMask) != 0 || s.Has(SiteFlags.CancelledBuild)
                       ? " cancelCrews=" + PhasePlan.CancelCrewsOf(s.Flags) + (s.Has(SiteFlags.CancelledBuild) ? "" : "(STRAY, no CancelledBuild)") : "");
        }
    }

    // Serialize, right after SerializerSystem (order 910): takes Temp off everything SaveSanitize marked.
    [RegisterSystem(SystemUpdatePhase.Serialize, After = typeof(SerializerSystem), Order = RRWOrder.SaveRestore)]
    public partial class SaveRestoreSystem : GameSystemBase
    {
        private readonly RRWGuard m_Guard = new RRWGuard("persistence SaveRestore");

        protected override void OnUpdate()
        {
            if (SaveGuard.Pending.Count == 0 || m_Guard.Faulted) return;
            long t0 = RRWPerf.Start();
            try
            {
                EntityManager.CompleteAllTrackedJobs();
                int n = SaveGuard.Restore(EntityManager);
                RRWLog.Verbose("persistence: save restore " + n);
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);   // Pending is kept: the Mod1 watchdog (LegacyMigrationSystem) retries next frame
            }
            finally
            {
                RRWPerf.Stop(PerfSlot.Persistence, t0);
            }
        }
    }
}
