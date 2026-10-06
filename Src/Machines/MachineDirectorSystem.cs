using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.SceneFlow;
using Game.Simulation;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;
using ObjTransform = Game.Objects.Transform;
using ObjSubObject = Game.Objects.SubObject;

// MachineDirectorSystem (order 240, Modification1 before GenerateAreasSystem): rosters, spawn /
// despawn (LOD: spawn within 600 m, despawn only out of sight), choreography re-plans, truck cycles, loads, lights,
// post-site puppets. Every structural change of the module happens here (and in MachineTagSystem at Mod4).
namespace RealisticRoadWorks.V3.Machines
{
    [RegisterSystem(SystemUpdatePhase.Modification1, Before = typeof(Game.Tools.GenerateAreasSystem), Order = RRWOrder.MachineDirector)]
    public partial class MachineDirectorSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private TerrainSystem m_Terrain;
        private LightingSystem m_Lighting;
        private CameraUpdateSystem m_Camera;
        private Game.Objects.SearchSystem m_ObjSearch;
        private Game.Net.SearchSystem m_NetSearch;
        private readonly RRWGuard m_Guard = new RRWGuard("machines director");
        private readonly List<ProjectRecord> m_Projects = new List<ProjectRecord>(32);
        private readonly List<Puppet> m_Scratch = new List<Puppet>(64);
        private readonly List<uint> m_Ids = new List<uint>(16);
        private readonly HashSet<uint> m_Seen = new HashSet<uint>();
        private readonly PlanContext m_Ctx = new PlanContext();
        private static readonly MachineRole[] s_Order =
        {
            MachineRole.Excavator, MachineRole.Loader, MachineRole.TruckA, MachineRole.TruckB, MachineRole.CrewTruck, MachineRole.Finisher,
        };
        private readonly List<MachineRole> m_Priority = new List<MachineRole>(6);
        private sealed class CamCmp : IComparer<ProjectRecord>
        {
            public int Compare(ProjectRecord a, ProjectRecord b) => a.CameraDistance.CompareTo(b.CameraDistance);
        }
        private static readonly CamCmp s_CamCmp = new CamCmp();
        private bool m_Faulted;
        private float3 m_Cam = new float3(float.NaN);

        public static int ActivePuppets => MachineRegistry.All.Count;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_Lighting = World.GetOrCreateSystemManaged<LightingSystem>();
            m_Camera = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_ObjSearch = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
            m_NetSearch = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            RRWIntrospection.RegisterDumper("Machines", Dump);
            RRWIntrospection.RegisterChecker("Machines", Check);
        }

        protected override void OnDestroy()
        {
            try { MachineTrackStore.Dispose(); } catch (Exception e) { RRWLog.ErrorOnce("machines dispose", e); }
            base.OnDestroy();
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            try
            {
                MachineRegistry.Clear();
                MachineTrackStore.Clear();
                Rigs.Clear();
                PuppetFactory.Clear();
                ClearDigStatics();   // dust puff list (entities are not saved: LivePath), truck geometry, IK arms
                m_Guard.Reset();
                m_Faulted = false;
            }
            catch (Exception e) { RRWLog.ErrorOnce("machines preload", e); }
        }

        protected override void OnUpdate()
        {
            if (GameManager.instance == null || GameManager.instance.gameMode != GameMode.Game) return;
            if (SiteRegistry.Projects.Count == 0 && MachineRegistry.All.Count == 0) return;
            if (m_Guard.Faulted)
            {
                if (!m_Faulted) { m_Faulted = true; TryShutdown(); }
                // Machines are disabled and every puppet was removed: keep publishing the (truthful) empty
                // report, or the Director would hold every release for the full cap and staged opening would never open.
                if (MachineRegistry.All.Count > 0 && (RRWClock.UpdateIndex & 63u) == 0u) TryShutdown();   // a shutdown that failed: retry
                try { RRWClock.Update(World); if (MachineRegistry.All.Count == 0) MachineReport.WriteDisabled(); }
                catch (Exception e) { RRWLog.ErrorOnce("machines report (disabled)", e); }
                return;
            }
            long t0 = RRWPerf.Start();
            MxPerf.FrameBegin(EntityManager);
            m_Reported = false;
            try
            {
                RRWClock.Update(World);
                Body();
                m_Guard.Ok();
            }
            catch (Exception e) { m_Guard.Fail(e); }
            finally
            {
                // an exception before step R must not leave the report stale (the Director would fail closed for nothing)
                if (!m_Reported)
                {
                    double now = double.NaN;
                    try { now = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime); } catch { }
                    if (!double.IsNaN(now)) WriteReport(now);
                }
            }
            MxPerf.FrameEnd();
            RRWPerf.Stop(PerfSlot.Machines, t0);
        }

        private bool m_Reported;

        // After repeated failures: remove every puppet (they are decoration only) so nothing is left stuck.
        private void TryShutdown()
        {
            try
            {
                MachineTrackStore.CompleteReaders();
                var em = EntityManager;
                foreach (var p in MachineRegistry.All) if (p.Alive(em)) PuppetFactory.Delete(em, p.E);
                MachineRegistry.Clear();
                RRWLog.Warn("machines: director disabled after repeated errors; all machines removed until the next load");
            }
            catch (Exception e) { RRWLog.ErrorOnce("machines shutdown", e); }
        }

        // ------------------------------------------------------------ frame

        private void Body()
        {
            var em = EntityManager;
            MxPerf.Begin(MxT.SyncReaders);
            MachineTrackStore.CompleteReaders();
            MxPerf.End(MxT.SyncReaders);
            MxPerf.LiveOn();
            UpdateClock();
            double now = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
            var settings = RRWSettings.Current;
            if (settings == null) { WriteReport(now); return; }
            MxPerf.Begin(MxT.Validate);
            if (MachineDebug.ClearRollers)
            {
                // dev rrw.mx.roller clear: every roller unit goes now (the roster respawns per its rules)
                MachineDebug.ClearRollers = false;
                m_Scratch.Clear();
                foreach (var q in MachineRegistry.All) if (q.IsRoller) m_Scratch.Add(q);
                foreach (var q in m_Scratch) Despawn(em, q, "dev rrw.mx.roller clear");
                m_Scratch.Clear();
            }
            ValidatePuppets(em);
            if (MachineDebug.RespawnDiggers) RespawnDiggers(em);
            MxPerf.End(MxT.Validate);
            float3 cam = CameraPivot();
            m_Cam = cam;
            // the camera frustum for the rollers' out-of-view test (once per update)
            try { RollerView.Refresh(World); } catch (Exception e) { RRWLog.ErrorOnce("machines roller view", e); }

            // A) tracks for every project that has or may get machines; then pack them for pose evaluation
            MxPerf.Begin(MxT.A_Tracks);
            m_Projects.Clear();
            foreach (var pr in SiteRegistry.Projects.Values) m_Projects.Add(pr);
            m_Projects.Sort(s_CamCmp);   // perf: a static comparer (List.Sort(Comparison) allocates a wrapper per call)
            m_Seen.Clear();
            foreach (var pr in m_Projects)
            {
                m_Seen.Add(pr.Id);
                var st = State(pr);
                bool has = st.Any();
                bool may = settings.MachinesOn && ModeHasMachines(pr) && pr.AllowMachines && !pr.ClearingTraffic && pr.Phase != WorksPhase.Complete && !pr.Releasing;
                has |= HasLeavers(pr.Id);   // leaving puppets plan their drive-out on the project track
                if (!has && !may) continue;
                var td = MachineTrackStore.GetOrCreate(pr.Id);
                td.ProjectGone = false;
                bool reset = FirstRuntimeFlags(em, pr, out var rf) && (rf & (RuntimeFlags.ModelReset | RuntimeFlags.NeedsRebuild)) != 0;
                TrackBuilder.Update(em, m_Terrain, pr, td, reset);
            }
            MachineTrackStore.MarkGone(m_Seen);   // perf: no iterator allocation
            TrackBuilder.ProcessSlices(em, m_Terrain);
            MachineTrackStore.Flush();
            MxPerf.End(MxT.A_Tracks);

            // A2) crew LOD over every project - nearest crew fronts first, <= kMaxActiveCrewsGlobal crews
            // with puppets; crews beyond it keep their fronts and lose their puppets once out of sight
            MxPerf.Begin(MxT.P_Budget);
            try { CrewLod(em, settings, cam); }
            catch (Exception e) { RRWLog.ErrorOnce("machines crew lod", e); }
            MxPerf.End(MxT.P_Budget);

            // B) projects
            MxBudget.Reset();
            MxPerf.Begin(MxT.B_Projects);
            foreach (var pr in m_Projects)
            {
                try { ProcessProject(em, pr, settings, now, cam); }
                catch (Exception e) { RRWLog.ErrorOnce("machines project", e); }
            }
            MxPerf.End(MxT.B_Projects);

            // C) puppets whose project vanished (not through completion) become post-site puppets
            MxPerf.Begin(MxT.C_Gone);
            m_Scratch.Clear();
            m_Scratch.AddRange(MachineRegistry.All);
            foreach (var p in m_Scratch)
            {
                if (p.PostSite) continue;
                if (!SiteRegistry.Projects.ContainsKey(p.ProjectId)) MakePostSite(em, p, null, now, "project gone");
            }
            MxPerf.End(MxT.C_Gone);

            // G) runtime separation guard over every puppet (all projects, post-site included), before the
            // plans are written to the entities in D
            MxPerf.Begin(MxT.G_Guard);
            try { SeparationGuard(em, now); }
            catch (Exception e) { RRWLog.ErrorOnce("machines separation guard", e); }
            MxPerf.End(MxT.G_Guard);

            // M) observed motion vs MachineLimits (rrw.mx.check / rrw.check), after the guard; a puppet moving far faster than its
            // role ever may (twice in a row) is stopped before its plan is written (runaway guard)
            MxPerf.Begin(MxT.M_Monitor);
            try
            {
                m_Runaway.Clear();
                var all = MachineRegistry.All;
                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i];
                    if (p.SpawnUpdate == RRWClock.UpdateIndex) MxSpeed.Restart(p);
                    if (MxSpeed.Observe(p, now)) m_Runaway.Add(p);
                }
                foreach (var p in m_Runaway) StopRunaway(em, p, now);
                m_Runaway.Clear();
            }
            catch (Exception e) { RRWLog.ErrorOnce("machines speed monitor", e); }
            MxPerf.End(MxT.M_Monitor);

            // D) per puppet: visibility, despawn, loads, lights, plan writes
            MxPerf.Begin(MxT.D_PerPuppet);
            float daylight = SafeDaylight();
            m_Scratch.Clear();
            m_Scratch.AddRange(MachineRegistry.All);
            foreach (var p in m_Scratch)
            {
                try { PerPuppet(em, p, now, cam, daylight, settings); }
                catch (Exception e) { RRWLog.ErrorOnce("machines puppet", e); }
            }
            MxPerf.End(MxT.D_PerPuppet);
            MxPerf.Begin(MxT.D_Budget);
            BudgetPressure(em);
            MxPerf.End(MxT.D_Budget);

            // E2) IK dig cycle builds, Breakout / Dump events, truck loads, dust puffs
            try { DigPass(em, settings, now); }
            catch (Exception e) { RRWLog.ErrorOnce("machines dig pass", e); }

            // dev watch (rrw.mx.check watch=1): every update counts whether any pair overlaps (overlap-seconds, target 0)
            MxPerf.Begin(MxT.DevWatch);
            if (MachineDebug.CheckEvery > 0)
            {
                try
                {
                    MachineDebug.CheckedFrames++;
                    if (MachineChecks.CountOverlaps(out string firstOverlap) > 0)
                    {
                        if (MachineDebug.OverlapFrames++ == 0 || (MachineDebug.OverlapFrames & 63) == 1)
                            RRWLog.Info("machines: overlap now: " + firstOverlap + " (watch overlapFrames=" + MachineDebug.OverlapFrames + ")");
                    }
                }
                catch (Exception e) { RRWLog.ErrorOnce("machines watch overlaps", e); }
            }
            if (MachineDebug.CheckEvery > 0 && MachineDebug.Watch != null && RRWClock.UpdateIndex % (uint)MachineDebug.CheckEvery == 0u)
            {
                try { MachineDebug.Watch(em); } catch (Exception e) { RRWLog.ErrorOnce("machines watch", e); MachineDebug.CheckEvery = 0; }
            }
            MxPerf.End(MxT.DevWatch);

            // E) housekeeping
            MxPerf.Begin(MxT.E_Housekeeping);
            MachineTrackStore.Collect(m_Ids);
            MachineTrackStore.Flush();
            if ((RRWClock.UpdateIndex & 255u) == 0u)
            {
                m_Ids.Clear();
                foreach (var kv in MachineRegistry.States)
                    if (!SiteRegistry.Projects.ContainsKey(kv.Key) && !kv.Value.Any()) m_Ids.Add(kv.Key);
                foreach (var id in m_Ids) MachineRegistry.States.Remove(id);
            }
            foreach (var pr in m_Projects) pr.MachineCount = State(pr).RoleCount();
            MxPerf.End(MxT.E_Housekeeping);

            // R) machine report: written last, after every plan, despawn and guard splice of this update
            WriteReport(now);
        }

        private readonly List<Puppet> m_Runaway = new List<Puppet>(4);

        // Runaway guard: p moved far faster than its role ever may (MxSpeed.RunawaySpeed) - a jump an earlier plan carried on,
        // never real motion. It stops where it is and plans again from rest: a working role re-plans this update's successor,
        // a leaver plans its drive-off again (RetryLeavers). One far off its chain is removed instead (a working role spawns
        // again at its anchor). Counted; logged once per puppet.
        private void StopRunaway(EntityManager em, Puppet p, double now)
        {
            MxSpeed.Runaways++;
            MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
            string what = p + " v=" + RRWLog.F(p.LastV) + " limit=" + RRWLog.F(p.LastVLimit) + " leg=" + (LegKind)s.LegKind + " u=" + RRWLog.F(s.U) +
                          (p.PostSite ? " leaving" : " act=" + p.Activity);
            if (MxSpeed.FirstRunaway.Length == 0) MxSpeed.FirstRunaway = what;
            bool onChain = SiteRegistry.TryGetProject(p.ProjectId, out var pr) && pr != null &&
                           s.U >= -MxConst.kSecSlack && s.U <= pr.ChainLength + MxConst.kSecSlack;
            if (!p.RunawayLogged)
            {
                p.RunawayLogged = true;
                RRWLog.Info("machines: runaway stopped: " + what + (onChain ? " (stops here, plans again from rest)" : " (far off its road: removed)"));
            }
            if (!onChain || p.Plan.Count <= 0)
            {
                Despawn(em, p, "runaway far off its road");
                return;
            }
            Choreo.StopHere(p, MachineRegistry.Clock, now);
            MxSpeed.Restart(p);
            if (p.PostSite)
            {
                // the drive-off is planned again from here (conflict-scanned); without an exit it stands until it is removed
                if (!float.IsNaN(p.LeaveEndU)) { p.LeaveContinue = true; p.ReplanAt = now; }
            }
            else p.IntentKey = int.MinValue;   // the role plans again in the next update, from rest
        }

        private void WriteReport(double now)
        {
            m_Reported = true;
            MxPerf.Begin(MxT.R_Report);
            try { MachineReport.Write(now); }
            catch (Exception e) { RRWLog.ErrorOnce("machines report", e); }   // no report -> stale -> the Director holds (fails closed)
            MxPerf.End(MxT.R_Report);
        }

        // Machine seconds since the puppet began leaving, or since it first saw its project release the road (whichever is
        // earlier); NaN-safe: 0 when neither applies.
        internal static double LeaveAge(Puppet p, ProjectRecord pr, double now)
        {
            double start = p.LeaveTau;
            if (pr != null && pr.Releasing)
            {
                if (double.IsNaN(p.ReleaseTau)) p.ReleaseTau = now;
                if (double.IsNaN(start) || p.ReleaseTau < start) start = p.ReleaseTau;
            }
            else if (pr != null) p.ReleaseTau = double.NaN;   // release ended (dev SetProgress back): a later release starts fresh
            return double.IsNaN(start) ? 0.0 : math.max(0.0, now - start);
        }

        // Seconds PAST the leave cap that applies (< 0 = not reached).
        // Two deadlines, the earlier wins: the puppet's own drive-out cap (LeaveTau + LeaveCap, LeaveCap scaled to its route at
        // the role's leaving limits) and the release cap (ReleaseTau + kPostSiteMaxSeconds, tied to the Director's release gate
        // kCompletionMachineWaitSimFrames, so it stays fixed). With LeaveCap = kPostSiteMaxSeconds this is the plain fixed cap.
        internal static double LeaveOver(Puppet p, ProjectRecord pr, double now, out double age, out double cap)
        {
            LeaveAge(p, pr, now);   // keeps ReleaseTau up to date
            double over = double.NegativeInfinity;
            age = 0.0;
            cap = RRWConst.kPostSiteMaxSeconds;
            if (!double.IsNaN(p.LeaveTau))
            {
                double a = math.max(0.0, now - p.LeaveTau), c = math.max(RRWConst.kPostSiteMaxSeconds, p.LeaveCap);
                over = a - c; age = a; cap = c;
            }
            if (pr != null && pr.Releasing && !double.IsNaN(p.ReleaseTau))
            {
                double a = math.max(0.0, now - p.ReleaseTau), c = RRWConst.kPostSiteMaxSeconds;
                if (a - c > over) { over = a - c; age = a; cap = c; }
            }
            return double.IsNegativeInfinity(over) ? -cap : over;
        }

        private static bool HasLeavers(uint projectId)
        {
            foreach (var p in MachineRegistry.All) if (p.PostSite && p.ProjectId == projectId) return true;
            return false;
        }

        private void UpdateClock()
        {
            float scale = math.max(0.01f, RRWDebug.MachineClockScale);
            if (!MachineRegistry.ClockInit)
            {
                MachineRegistry.Clock = new ClockData { Tau0 = 1000.0, Frame0 = RRWClock.RenderFrame, Scale = scale };
                MachineRegistry.ClockInit = true;
                return;
            }
            var c = MachineRegistry.Clock;
            if (math.abs(c.Scale - scale) > 1e-4f)
            {
                double tau = c.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
                MachineRegistry.Clock = new ClockData { Tau0 = tau, Frame0 = RRWClock.RenderFrame, Scale = scale };
                // RenderFrameTime is folded into Tau0: the next frames use whole-frame offsets from Frame0 plus their own frac
                MachineRegistry.Clock.Tau0 -= RRWClock.RenderFrameTime / 60.0 * scale;
                RRWLog.Info("machines: machine clock scale " + RRWLog.F(c.Scale) + " -> " + RRWLog.F(scale));
            }
        }

        private float3 CameraPivot()
        {
            try
            {
                var gc = m_Camera != null ? m_Camera.gamePlayController : null;
                if (gc != null) { var v = gc.pivot; return new float3(v.x, v.y, v.z); }
                if (m_Camera != null) return m_Camera.position;
            }
            catch { }
            return new float3(float.NaN);
        }

        private float SafeDaylight()
        {
            try { return m_Lighting != null ? m_Lighting.dayLightBrightness : 1f; } catch { return 1f; }
        }

        private static MachineProjectState State(ProjectRecord pr)
        {
            var st = pr.Get<MachineProjectState>(ModuleSlot.Machines);
            if (st == null)
            {
                if (!MachineRegistry.States.TryGetValue(pr.Id, out st)) st = new MachineProjectState { ProjectId = pr.Id };
                pr.Set(ModuleSlot.Machines, st);
            }
            MachineRegistry.States[pr.Id] = st;
            return st;
        }

        private static bool HasPuppets(MachineProjectState st) => st.Any();

        // ------------------------------------------------------------ crew LOD (step A2)

        private struct CrewRank { public ProjectRecord P; public int Crew, Crews; public float Dist; public bool Has; }
        private sealed class CrewRankCmp : IComparer<CrewRank>
        {
            public int Compare(CrewRank a, CrewRank b)
            {
                int c = a.Dist.CompareTo(b.Dist);
                if (c != 0) return c;
                c = a.P.Id.CompareTo(b.P.Id);
                return c != 0 ? c : a.Crew.CompareTo(b.Crew);
            }
        }
        private static readonly CrewRankCmp s_RankCmp = new CrewRankCmp();
        private readonly List<CrewRank> m_Ranks = new List<CrewRank>(64);

        // Every crew of every project that may have machines is ranked by the camera distance to its front (ChainMap.Point of
        // PhasePlan.CrewFront). The nearest kMaxActiveCrewsGlobal crews may have puppets (one-crew projects: the Director's
        // AllowMachines alone decides the radius; several crews: a crew spawns only within kMachineSpawnRadius of
        // its front). A crew outside that set (or beyond kMachineDespawnRadius) is LOD-out: no spawns, and its puppets are removed
        // once out of sight (PerPuppet). Its front keeps moving.
        private void CrewLod(EntityManager em, RRWSetting settings, float3 cam)
        {
            m_Ranks.Clear();
            bool camOk = !math.any(math.isnan(cam));
            foreach (var pr in m_Projects)
            {
                var st = State(pr);
                var view = pr.View();
                int n = view.CrewCount;
                bool may = settings.MachinesOn && ModeHasMachines(pr) && pr.AllowMachines && !pr.ClearingTraffic && pr.Phase != WorksPhase.Complete && !pr.Releasing;
                for (int i = 0; i < st.Crews.Length; i++)
                {
                    var cs = st.Crews[i];
                    int live = 0;
                    for (int r = 0; r < cs.Roles.Length; r++) if (cs.Roles[r] != null) live++;
                    live += cs.RollerCount();   // a crew with only a roller has puppets too
                    cs.Live = live;
                    if (i >= n || !may) { cs.SpawnOk = false; cs.LodOut = false; continue; }
                    float d = pr.CameraDistance;
                    if (n > 1)
                    {
                        // crew front distance, refreshed every 8 updates per project (staggered) - O(edges) per crew
                        d = cs.CamDist;
                        if (float.IsNaN(d) || ((RRWClock.UpdateIndex + pr.Id) & 7u) == 0u)
                        {
                            d = float.MaxValue;
                            if (camOk && ChainMap.Point(em, pr, PhasePlan.CrewFront(view, i), out float3 pos, out _)) d = math.distance(pos.xz, cam.xz);
                        }
                    }
                    cs.CamDist = d;
                    m_Ranks.Add(new CrewRank { P = pr, Crew = i, Crews = n, Dist = d, Has = live > 0 });
                }
            }
            m_Ranks.Sort(s_RankCmp);
            int active = 0, lod = 0;
            for (int k = 0; k < m_Ranks.Count; k++)
            {
                var x = m_Ranks[k];
                var cs = State(x.P).Crews[x.Crew];
                bool near = x.Crews <= 1 || x.Dist < RRWConst.kMachineSpawnRadius;
                bool far = x.Crews > 1 && x.Dist > RRWConst.kMachineDespawnRadius;
                if (active < RRWConst.kMaxActiveCrewsGlobal && (x.Has || near) && !far)
                {
                    active++;
                    cs.SpawnOk = near;
                    cs.LodOut = false;
                }
                else
                {
                    cs.SpawnOk = false;
                    cs.LodOut = x.Has;
                    if (x.Has) lod++;
                }
            }
            MachineDebug.CrewsActive = active;
            MachineDebug.CrewsLodOut = lod;
            // puppets follow their crew's LOD state
            foreach (var pr in m_Projects)
            {
                var st = State(pr);
                for (int i = 0; i < st.Crews.Length; i++)
                {
                    var cs = st.Crews[i];
                    for (int r = 0; r < cs.Roles.Length; r++) if (cs.Roles[r] != null) cs.Roles[r].LodOut = cs.LodOut;
                    for (int r = 0; r < cs.Rollers.Length; r++) if (cs.Rollers[r] != null) cs.Rollers[r].LodOut = cs.LodOut;
                }
            }
        }

        private static bool FirstRuntimeFlags(EntityManager em, ProjectRecord pr, out RuntimeFlags flags)
        {
            flags = RuntimeFlags.None;
            bool any = false;
            for (int i = 0; i < pr.Edges.Count; i++)
            {
                var e = pr.Edges[i];
                if (!em.Exists(e) || !em.HasComponent<RoadWorksRuntime>(e)) continue;
                flags |= em.GetComponentData<RoadWorksRuntime>(e).m_Flags;
                any = true;
            }
            return any;
        }

        // Dev (rrw.mx.dust): remove the diggers so the next project pass spawns them with the requested clone.
        private void RespawnDiggers(EntityManager em)
        {
            MachineDebug.RespawnDiggers = false;
            m_Scratch.Clear();
            foreach (var p in MachineRegistry.All) if (p.Kind == MachineKind.Excavator && !p.PostSite) m_Scratch.Add(p);
            foreach (var p in m_Scratch)
            {
                if (MachineRegistry.States.TryGetValue(p.ProjectId, out var st)) st.SpawnAtAnchor = true;
                Despawn(em, p, "dev rrw.mx.dust");
            }
            m_Scratch.Clear();
        }

        private void ValidatePuppets(EntityManager em)
        {
            m_Scratch.Clear();
            foreach (var p in MachineRegistry.All) if (!p.Alive(em)) m_Scratch.Add(p);
            foreach (var p in m_Scratch)
            {
                MachineRegistry.Remove(p);
                RRWLog.Verbose("machines: " + p + " vanished (deleted by something else)");
            }
        }

        private static FrontRef FrontOf(ProjectRecord pr, int crew, int crews)
        {
            var view = pr.View();
            if (view.IsUpgrade) return UwFrontOf(pr, view, crew);   // mode H: the crew's band front
            bool swap = view.SwapActive;
            return new FrontRef
            {
                Model = pr.Model,
                Phase = (byte)pr.Phase,
                PStart = PhasePlan.PhaseStart(pr.Phase),
                PEnd = PhasePlan.PhaseEnd(pr.Phase),
                U = pr.ChainLength,
                Swap = swap ? (byte)1 : (byte)0,   // stage-aware C4 front (PhasePlan.MainFront(ph, f, U, swap))
                // the C4 front floor (monotonic C4 front when the swap is off), as PhasePlan.MainFront(view)
                Floor = pr.Phase == WorksPhase.Finishing && !swap ? math.max(0f, pr.StageCtx.FrontFloor) : 0f,
                // this crew's section front (PhasePlan.SectionFront)
                Crews = (byte)math.clamp(crews, 1, RRWConst.kMaxCrewsPerProject),
                Crew = (byte)math.clamp(crew, 0, RRWConst.kMaxCrewsPerProject - 1),
            };
        }

        // ------------------------------------------------------------ projects

        private void ProcessProject(EntityManager em, ProjectRecord pr, RRWSetting settings, double now, float3 cam)
        {
            var st = State(pr);
            MachineTrackStore.TryGet(pr.Id, out var td);
            bool has = st.Any();
            FirstRuntimeFlags(em, pr, out var rf);
            // release: completion frame N on, or a D0 call-off: no spawn, every puppet leaves
            bool completing = pr.Phase == WorksPhase.Complete || (rf & RuntimeFlags.Completing) != 0 || pr.Releasing || (rf & RuntimeFlags.Releasing) != 0;
            if (td == null || !td.Valid)
            {
                if (has && completing)
                {
                    CollectRoles(st);
                    foreach (var p in m_Scratch) MakePostSite(em, p, null, now, "release (no track)");
                    m_Scratch.Clear();
                }
                return;
            }
            var c = ProjectContext(em, pr, st, td, now);
            int n = c.View.CrewCount;
            if (completing)
            {
                if (!st.Completed)
                {
                    st.Completed = true;
                    // the machine nearest to its chain end leaves first: it takes the end-most free slot, so nobody has to
                    // drive through a machine that already stopped
                    SetCrew(c, 0, st);
                    CollectRoles(st);
                    m_EndCtx = c; m_EndNow = now;
                    m_Scratch.Sort(m_EndCmp);
                    int cnt = m_Scratch.Count;
                    foreach (var p in m_Scratch)
                    {
                        SetCrew(c, math.min(p.Crew, n - 1), st);
                        MakePostSite(em, p, c, now, pr.Phase == WorksPhase.Complete ? "completion" : "release");
                    }
                    m_Scratch.Clear();
                    m_EndCtx = null;
                    RRWLog.Info("machines: project #" + pr.Id + " releases the road: " + cnt + " machine(s) drive off (removed when out of sight, at the latest " +
                                RRWLog.F((float)RRWConst.kPostSiteMaxSeconds) + " s later)");
                }
                return;
            }
            bool modelReset = (rf & RuntimeFlags.ModelReset) != 0;
            bool rebuild = (rf & RuntimeFlags.NeedsRebuild) != 0;
            bool phaseChanged = st.Initialised && st.LastPhase != pr.Phase;
            bool workingChanged = st.Initialised && st.LastWorking != pr.Working;
            bool allowed = settings.MachinesOn && ModeHasMachines(pr) && pr.AllowMachines && !pr.ClearingTraffic;
            int stage = pr.Phase == WorksPhase.Finishing ? c.Stage.Index : 0;
            // a crew re-latch (Director: Revision / ModelReset / C4 Vacate -> Swap) or a C4a -> C4b stage change
            // re-assigns the crews like a phase change
            bool crewsChanged = st.Initialised && st.LastCrews != n;
            bool stageChanged = st.Initialised && st.LastStage >= 0 && st.LastStage != stage;
            if (!st.Initialised || modelReset || rebuild || (allowed && !st.LastAllowed)) st.SpawnAtAnchor = true;

            // continuous ProgressModel re-anchors: update the front model in place (no re-plan; |jump| ~ 0). A re-anchor
            // that changes the front SPEED (rush, rate change) re-plans the front-relative roles instead, so their velocity
            // changes within their acceleration (an in-place swap would step it)
            if (!modelReset && !SameModel(st.LastModel, pr.Model)) Reanchor(st, pr, now);
            st.LastModel = pr.Model;

            // "Rebuild visuals": respawn every puppet at its anchor
            if (rebuild && has)
            {
                CollectRoles(st);
                foreach (var p in m_Scratch)
                {
                    if (p.Alive(em)) PuppetFactory.Delete(em, p.E);
                    MachineRegistry.Remove(p);
                    MachineRegistry.Despawned++;
                }
                m_Scratch.Clear();
                for (int i = 0; i < st.Crews.Length; i++) st.Crews[i].ResetGrid();
            }
            // mode H: a crew whose band changed (window switch) sends its machines off along the old band; a machine whose box is
            // no longer machine-safe vacates the band
            if (c.Upgrade && has)
            {
                UwCrewBands(em, c, st, now, n);
                UwVacate(em, c, st, now, n);
                has = st.Any();
            }
            else if (c.Upgrade) UwCrewBands(em, c, st, now, n);
            // hand-over between crew layouts / phases: keep, re-map (crew relay) or leave + spawn at the anchor
            if (!rebuild && has && (phaseChanged || modelReset || crewsChanged || stageChanged))
            {
                MxPerf.Begin(MxT.B_HandOver);
                try { HandOver(em, c, st, now, phaseChanged ? "phase" : modelReset ? "model reset" : crewsChanged ? "crews " + st.LastCrews + "->" + n : "stage"); }
                catch (Exception e) { RRWLog.ErrorOnce("machines hand-over", e); }
                MxPerf.End(MxT.B_HandOver);
            }
            // new sections: the crews' dig grids start again (the excavators re-plan their hop windows)
            if (crewsChanged) for (int i = 0; i < st.Crews.Length; i++) st.Crews[i].ResetGrid();
            else if (phaseChanged || modelReset || stageChanged)
                for (int i = 0; i < st.Crews.Length; i++) { st.Crews[i].ResetLag(); st.Crews[i].ResetOwnerWait(); }   // new front model
            // puppets of crews beyond the layout (none after a hand-over) leave
            for (int k = n; k < st.Crews.Length; k++)
            {
                var roles = st.Crews[k].Roles;
                for (int r = 0; r < roles.Length; r++)
                {
                    var p = roles[r];
                    if (p == null) continue;
                    SetCrew(c, n - 1, st);
                    MakePostSite(em, p, c, now, "crew " + k + " removed (" + n + " crews)");
                }
                var rl = st.Crews[k].Rollers;
                for (int r = 0; r < rl.Length; r++)
                {
                    var p = rl[r];
                    if (p == null) continue;
                    SetCrew(c, n - 1, st);
                    MakePostSite(em, p, c, now, "crew " + k + " removed (" + n + " crews)");
                }
            }

            int budget = math.max(0, settings.MachineBudget);   // per crew ("Machines per crew")
            st.ClearGate();
            bool force0 = phaseChanged || modelReset || workingChanged || crewsChanged || stageChanged;
            for (int crew = 0; crew < n; crew++)
            {
                SetCrew(c, crew, st);
                ProcessCrew(em, c, st, pr, td, settings, now, budget, allowed, force0, phaseChanged, modelReset, workingChanged);
            }
            st.LastPhase = pr.Phase;
            st.LastWorking = pr.Working;
            st.LastAllowed = allowed;
            st.LastCrews = n;
            st.LastStage = stage;
            st.Initialised = true;
            st.SpawnAtAnchor = false;
        }

        // The roster of one crew (one per section).
        private void ProcessCrew(EntityManager em, PlanContext c, MachineProjectState st, ProjectRecord pr, TrackData td, RRWSetting settings, double now,
                                 int budget, bool allowed, bool force0, bool phaseChanged, bool modelReset, bool workingChanged)
        {
            var cs = c.CS;
            int working = 0;
            BuildPriority(c);
            foreach (var role in m_Priority)
            {
                var slot = c.Crew.Get(role);
                var p = cs.Roles[(int)role];
                bool want = slot.Active && RoleUsable(c, role, slot);
                if (!want)
                {
                    // a role that ended leaves the site (C2 excavator at the reveal, C4 leavers after
                    // f >= .1, a narrow-road TruckB, ...) and is removed out of sight / at the kPostSiteMaxSimFrames cap
                    if (p != null) MakePostSite(em, p, c, now, "role ended (" + c.View.Phase + ")");
                    continue;
                }
                if (slot.Activity == MachineActivity.DriveOut)
                {
                    // DriveOut roles are never spawned; a live one drives to its slot (Core Crew: Trim1) in its lane
                    if (p != null) MakePostSite(em, p, c, now, "drive-out (" + c.View.Phase + ")", slot.U);
                    continue;
                }
                if (p == null)
                {
                    if (!allowed || working >= budget) continue;
                    // crew LOD - only the nearest crews spawn (the front of a crew without puppets keeps moving)
                    if (!cs.SpawnOk) { Gate(st, role, "crew LOD (far crew or > kMaxActiveCrewsGlobal crews)", null); continue; }
                    // no spawn while a stage switch runs or nothing is ready; C4 halves: RMV01 roster only
                    if (c.NoSpawn) { Gate(st, role, c.NoSpawnWhy, null); continue; }
                    if (c.HalvesActive && KindOf(role) != MachineKind.Rmv) { Gate(st, role, "C4 half: RMV01 roster only", null); continue; }
                    // the C2 grader's verge parking (f >= .95) is for a grader that was working; never spawn one to park
                    if (role == MachineRole.Loader && slot.Activity == MachineActivity.Parked) continue;
                    if (cs.NoVergePhase[(int)role] == pr.Phase) continue;
                    MxPerf.Begin(MxT.B_SpawnFits);
                    bool fits = SpawnFits(c, role, slot);
                    MxPerf.End(MxT.B_SpawnFits);
                    if (!fits) continue;
                    if (CountLive() >= RRWConst.kMaxMachinesGlobal && !EvictOne(em)) continue;
                    if (now < cs.SpawnRetryAt[(int)role]) continue;   // its spawn spot was taken a moment ago
                    if (cs.DoneKey[(int)role] == CrewState.DoneKeyOf(pr.Phase, c.Stage.Index)) continue;   // its part of this stage is done
                    MxPerf.Begin(MxT.B_Spawn);
                    p = Spawn(em, c, role, slot, now, settings);
                    MxPerf.End(MxT.B_Spawn);
                    MxPerf.Count(p != null ? MxC.Spawns : MxC.SpawnDeferred);
                    if (p != null) { working++; MachineRegistry.Spawned++; }
                    continue;
                }
                p.Outgoing = false;
                working++;
                if (!c.ReadyOk)
                {
                    // fails closed: nothing ready (stale drain report / both car halves busy): no new plan into
                    // the road; the committed plan runs on (leavers and drive-outs above are not affected)
                    Gate(st, role, "re-plan held: ", c.NoSpawnWhy);
                    continue;
                }
                bool frontModel = p.Plan.FrontA.Swap != c.Front.Swap || p.Plan.FrontA.Floor != c.Front.Floor ||
                                  p.Plan.FrontA.Crews != c.Front.Crews || p.Plan.FrontA.Crew != c.Front.Crew ||   // crew section changed
                                  (c.Upgrade && !p.Plan.FrontA.SameUpgradeBand(c.Front));                           // mode H: band phase changed
                // (the crew's phase: the project phase, for mode H band crews their band's equivalent phase)
                bool force = force0 || p.PlannedTrackRevision != td.Revision || p.PlannedPhase != c.View.Phase || frontModel;
                int key = RoleKey(c, p, role, slot);
                if (key != p.IntentKey) force = true;
                // dev rrw.mx.perf: which re-plan triggers fire (a trigger that fires every update shows ~1 per live role per frame)
                MxPerf.CountIf(phaseChanged, MxC.ForcePhase);
                MxPerf.CountIf(modelReset, MxC.ForceModelReset);
                MxPerf.CountIf(workingChanged, MxC.ForceWorking);
                MxPerf.CountIf(p.PlannedTrackRevision != td.Revision, MxC.ForceTrackRev);
                MxPerf.CountIf(p.PlannedPhase != c.View.Phase, MxC.ForcePlannedPhase);
                MxPerf.CountIf(frontModel, MxC.ForceFrontModel);
                MxPerf.CountIf(key != p.IntentKey, MxC.ForceIntent);
                if (p.IsTruck) UpdateTruck(p, c, role, slot, now, force);
                else if (force || now >= p.ReplanAt || MinimalSwitch(p, c, slot))
                {
                    MxPerf.Count(force ? MxC.ReplanForced : now >= p.ReplanAt ? MxC.ReplanTimer : MxC.ReplanMinimal);
                    var saved = p.Plan;
                    bool held = p.GuardHeld;
                    // PlanDig writes the crew's dig grid (re-phase, GridEpoch) before the release check
                    // decides; a rejected plan restores it, so a held excavator's retries never re-plan the loading trucks
                    var grid = held ? cs.SnapGrid() : default;
                    Replan(p, c, role, slot);
                    if (held && !ReleaseCheckTimed(p, c, saved, force)) cs.RestoreGrid(grid);
                    if (role == MachineRole.Loader) GraderGuard(c, p, saved, true);
                    // a leading C4 crew truck drives off before its painter closes in at the end of the last section
                    if (role == MachineRole.CrewTruck && p.LeadPhase == pr.Phase && p.LeadCrew == c.CrewIndex && p.LeadStage == c.Stage.Index)
                    {
                        if (Choreo.LeadEnd(c, p, out double tLead))
                        {
                            cs.DoneKey[(int)role] = CrewState.DoneKeyOf(pr.Phase, c.Stage.Index);
                            MakePostSite(em, p, c, now, "lead vehicle drives off ahead of its painter (end of the section)");
                            continue;
                        }
                        p.ReplanAt = math.min(p.ReplanAt, tLead);
                    }
                    if (VergeRoleBlocked(c, role, slot, p))
                    {
                        cs.NoVergePhase[(int)role] = pr.Phase;
                        MakePostSite(em, p, c, now, "verge blocked: verge roles stay on the verge or are not used");
                        continue;
                    }
                }
                else if (role == MachineRole.Loader) GraderGuard(c, p, default, false);
                p.IntentKey = key;
                p.Activity = slot.Activity;
            }
            // the rollers come after every puppet role (lowest budget priority)
            MxPerf.Begin(MxT.B_Rollers);
            try { ProcessRollers(em, c, st, pr, td, settings, now, budget, ref working, allowed, force0); }
            catch (Exception e) { RRWLog.ErrorOnce("machines rollers", e); }
            MxPerf.End(MxT.B_Rollers);
        }

        // Every role puppet of every crew -> m_Scratch.
        private void CollectRoles(MachineProjectState st)
        {
            m_Scratch.Clear();
            for (int k = 0; k < st.Crews.Length; k++)
            {
                var roles = st.Crews[k].Roles;
                for (int r = 0; r < roles.Length; r++) if (roles[r] != null) m_Scratch.Add(roles[r]);
                var rl = st.Crews[k].Rollers;   // rollers too
                for (int r = 0; r < rl.Length; r++) if (rl[r] != null) m_Scratch.Add(rl[r]);
            }
        }

        // completion ordering (allocation-free comparer over the context of this call)
        private PlanContext m_EndCtx;
        private double m_EndNow;
        private Comparison<Puppet> m_EndCmpCache;
        private Comparison<Puppet> m_EndCmp => m_EndCmpCache ?? (m_EndCmpCache = (a, b) => EndDistance(m_EndCtx, a, m_EndNow).CompareTo(EndDistance(m_EndCtx, b, m_EndNow)));

        private void Reanchor(MachineProjectState st, ProjectRecord pr, double now)
        {
            var clk = MachineRegistry.Clock;
            for (int k = 0; k < st.Crews.Length; k++)
            {
                var roles = st.Crews[k].Roles;
                int nr = roles.Length + st.Crews[k].Rollers.Length;   // rollers plan their windows from the front model too
                for (int r = 0; r < nr; r++)
                {
                    var p = r < roles.Length ? roles[r] : st.Crews[k].Rollers[r - roles.Length];
                    // (a mode H band front belongs to its band's phase, not the project's: the model is the project's all the same)
                    if (p == null || p.PostSite || (p.Plan.FrontA.Upgrade == 0 && p.Plan.FrontA.Phase != (byte)pr.Phase)) continue;
                    var nf = p.Plan.FrontA;
                    nf.Model = pr.Model;
                    float dv = math.abs(MachineMotion.FrontSpeed(nf, clk, now) - MachineMotion.FrontSpeed(p.Plan.FrontA, clk, now));
                    // in place when the speed step stays well inside the role's acceleration (the Director's drift re-anchors every
                    // kModelWindow frames change the slope by far less than 1 %); a rush / rate change re-plans. A hopping role
                    // re-plans at its next cycle boundary (its upcoming anchors would shift mid-hop).
                    bool hop = p.Plan.Count > 0 && p.Plan.Get(p.Plan.Count - 1).Kind == (byte)LegKind.DigHop;
                    float tol = hop ? 0.01f : 0.1f * math.max(0.05f, p.Lim.Accel);
                    if (dv > tol && !p.IsTruck)
                    {
                        p.IntentKey = int.MinValue;   // re-plan this update: Begin keeps the running leg on the old model (FrontB)
                        MxPerf.Count(MxC.ModelReplans);
                        continue;
                    }
                    p.Plan.FrontA.Model = pr.Model;
                    MxPerf.Count(MxC.ModelReanchors);
                    // (the mover's copy only needs the new model when a remaining leg reads FrontA)
                    if (PlanSamples.ReadsFrontA(p.Plan, now)) p.PlanDirty = true;
                    // the legs run on (no re-plan, no restart). A plan whose remaining legs do not
                    // read FrontA keeps every cache; otherwise only the samples the re-anchor moved against the cache's reference model
                    // are dropped (refilled on demand by the budgeted consumers, never all at once here); counters ReanchorKept /
                    // ReanchorPartial / ReanchorFull (rrw.mx.perf)
                    PlanSamples.Reanchored(p, clk, now);
                }
            }
        }

        // ------------------------------------------------------------ hand-over

        private readonly List<Puppet> m_HoPuppets = new List<Puppet>(16);
        private struct HoPair { public int P, Crew; public float T; public bool Late; }
        private sealed class HoCmp : IComparer<HoPair>
        {
            public int Compare(HoPair a, HoPair b)
            {
                int c = a.T.CompareTo(b.T);
                if (c != 0) return c;
                c = a.Crew.CompareTo(b.Crew);
                return c != 0 ? c : a.P.CompareTo(b.P);
            }
        }
        private static readonly HoCmp s_HoCmp = new HoCmp();
        private readonly List<HoPair> m_HoPairs = new List<HoPair>(64);
        private readonly List<Puppet> m_HoRole = new List<Puppet>(8);

        // At a phase change, ModelReset, crew re-latch or C4 stage change every live role puppet of the project is matched to the new
        // crew slots role by role, shortest travel time first: it keeps its slot, or is RE-MAPPED to another crew's slot of the same
        // role (crew relay, no respawn), when it reaches the slot's anchor within PhasePlan.HandOverSeconds at its MachineLimits.
        // Otherwise it leaves (PlanLeave at VLeave) and that crew's role spawns at its anchor (CrewState.AnchorMask). A role without
        // any usable slot stays with its crew so the roster ends it as before ("role ended" / DriveOut to its slot).
        private void HandOver(EntityManager em, PlanContext c, MachineProjectState st, double now, string why)
        {
            var view = c.View;
            int n = view.CrewCount;
            float budgetS = PhasePlan.HandOverSeconds(view);
            var clk = MachineRegistry.Clock;
            m_HoPuppets.Clear();
            for (int k = 0; k < st.Crews.Length; k++)
            {
                var roles = st.Crews[k].Roles;
                for (int r = 0; r < roles.Length; r++)
                    if (roles[r] != null) { m_HoPuppets.Add(roles[r]); roles[r] = null; }
            }
            int kept = 0, remapped = 0, left = 0;
            for (int r = 0; r < (int)MachineRole.Count; r++)
            {
                var role = (MachineRole)r;
                m_HoRole.Clear();
                foreach (var p in m_HoPuppets) if (p.Role == role) m_HoRole.Add(p);
                if (m_HoRole.Count == 0) continue;
                m_HoPairs.Clear();
                int slotMask = 0;
                for (int i = 0; i < n; i++)
                {
                    var plan = PhasePlan.Crew(view, i);
                    var slot = plan.Get(role);
                    if (!slot.Active || slot.Activity == MachineActivity.DriveOut || !RoleUsable(c, role, slot)) continue;
                    if (c.HalvesActive && KindOf(role) != MachineKind.Rmv) continue;
                    slotMask |= 1 << i;
                    // a truck starts its cycle at its base slot; every other role at its slot anchor
                    float au = role == MachineRole.TruckA ? plan.BaseSlotU : role == MachineRole.TruckB ? plan.BaseSlotUB : slot.U;
                    float anchor = math.clamp(au, c.Trim0, c.Trim1);
                    // an anchor in view must not get a clone popping in: a live puppet of the role that misses the
                    // budget relays late (drives there at its limits) instead of leaving
                    bool anchorInView = AnchorInView(c, anchor);
                    float lateS = anchorInView ? math.max(budgetS, RRWConst.kHandOverLateSeconds) : budgetS;
                    for (int j = 0; j < m_HoRole.Count; j++)
                    {
                        var p = m_HoRole[j];
                        if (p.Plan.Count <= 0) continue;
                        if (c.Upgrade && p.Crew != i) continue;   // mode H: crews work different bands, a machine never crosses to another
                        MachineMotion.State(p.Plan, clk, now, out var s);
                        var lim = MachineLimits.Of(role, view.Phase);
                        float t = MachineLimits.MinLegSeconds(anchor - s.U, lim.VFwd, lim.Accel);
                        if (t <= lateS) m_HoPairs.Add(new HoPair { P = j, Crew = i, T = t, Late = t > budgetS });
                    }
                }
                m_HoPairs.Sort(s_HoCmp);
                int usedP = 0, usedC = 0;
                foreach (var hp in m_HoPairs)
                {
                    if ((usedP & (1 << hp.P)) != 0 || (usedC & (1 << hp.Crew)) != 0) continue;
                    usedP |= 1 << hp.P;
                    usedC |= 1 << hp.Crew;
                    var p = m_HoRole[hp.P];
                    st.Crews[hp.Crew].Roles[r] = p;
                    if (hp.Late)
                    {
                        MxSpeed.HandLate++;
                        p.IntentKey = int.MinValue;   // re-plan: the catch-up drive runs at the role's limits (arrives late)
                        RRWLog.Verbose("machines: hand-over (" + why + ") " + p + " relays LATE to crew " + hp.Crew + " (" + RRWLog.F(hp.T) + " s > " + RRWLog.F(budgetS) + " s; anchor in view)");
                    }
                    if (p.Crew != hp.Crew)
                    {
                        RRWLog.Verbose("machines: hand-over (" + why + ") " + p + " -> crew " + hp.Crew + " (" + RRWLog.F(hp.T) + " s <= " + RRWLog.F(budgetS) + " s)");
                        p.Crew = hp.Crew;
                        p.IntentKey = int.MinValue;
                        p.OutAttempt = p.RetAttempt = -1;
                        if (p.Alive(em) && em.HasComponent<RRWMachine>(p.E))
                        {
                            var m = em.GetComponentData<RRWMachine>(p.E);
                            m.m_Crew = (byte)hp.Crew;
                            em.SetComponentData(p.E, m);
                        }
                        remapped++;
                    }
                    else kept++;
                }
                for (int j = 0; j < m_HoRole.Count; j++)
                {
                    if ((usedP & (1 << j)) != 0) continue;
                    var p = m_HoRole[j];
                    if (slotMask == 0 && p.Crew < n && st.Crews[p.Crew].Roles[r] == null)
                    {
                        st.Crews[p.Crew].Roles[r] = p;   // the roster ends the role (role ended / drive-out) as before
                        continue;
                    }
                    SetCrew(c, math.clamp(p.Crew, 0, n - 1), st);
                    MakePostSite(em, p, c, now, "hand-over (" + why + "): no " + role + " slot within " + RRWLog.F(budgetS) + " s");
                    left++;
                }
                for (int i = 0; i < n; i++)
                    if ((slotMask & (1 << i)) != 0 && (usedC & (1 << i)) == 0) st.Crews[i].AnchorMask |= 1 << r;
            }
            MxSpeed.HandKept += kept;
            MxSpeed.HandRemapped += remapped;
            MxSpeed.HandLeft += left;
            if (remapped + left > 0)
                RRWLog.Verbose("machines: project #" + c.Project.Id + " hand-over (" + why + ", " + n + " crews, " + RRWLog.F(budgetS) + " s): kept " + kept +
                               ", re-mapped " + remapped + ", left " + left);
            m_HoPuppets.Clear();
            m_HoRole.Clear();
            // the rollers, role by role like the puppets (C2 -> C3 relay at the section boundaries)
            HandOverRollers(em, c, st, now, why, budgetS);
        }

        // A hand-over anchor the player may be looking at (camera known and within kMachineSpawnRadius of it).
        // Unknown camera: false (leave + anchor spawn).
        private static bool AnchorInView(PlanContext c, float u)
        {
            if (c.Track == null || math.any(math.isnan(c.Cam))) return false;
            if (!c.Tv.Point(c.Track.Slot, u, out var tp)) return false;
            return math.distance(tp.Pos.xz, c.Cam.xz) <= RRWConst.kMachineSpawnRadius;
        }

        // dev rrw.mx.perf: ReleaseCheck in its own timing section
        private static bool ReleaseCheckTimed(Puppet p, PlanContext c, MachinePlan held, bool force)
        {
            MxPerf.Begin(MxT.B_ReleaseCheck);
            bool kept = ReleaseCheck(p, c, held, force);
            MxPerf.End(MxT.B_ReleaseCheck);
            return kept;
        }

        // A puppet the separation guard stopped moves again only when its new role plan meets nobody over the
        // next kGuardReleaseWindow s; otherwise it keeps standing and retries in kGuardRetry s. A forced re-plan (phase /
        // intent change) is committed anyway - the guard brakes it again if it runs into someone.
        // Returns true when the new plan is kept (false: the held plan was restored). The check is
        // bounded by the per-update check budget; an unfinished check counts as a conflict (retried in kGuardRetry s).
        private static bool ReleaseCheck(Puppet p, PlanContext c, MachinePlan held, bool force)
        {
            if (!force && Choreo.FindConflict(p, p.Plan, c, c.Now, c.Now + MxConst.kGuardReleaseWindow, out double tc, out _, out Puppet who, MxConst.kGuardMargin, null))
            {
                // the motion it wants (make-way: a standing blocker clears this path)
                if (who != null) { p.Wanted = p.Plan; p.WantedTau = c.Now; }
                p.Plan = held;
                p.PlanDirty = true;
                p.ReplanAt = c.Now + MxConst.kGuardRetry;
                p.GuardBlocker = who ?? p.GuardBlocker;
                return false;
            }
            RRWLog.Verbose("machines: guard releases " + p + " after " + RRWLog.F((float)(c.Now - p.GuardHeldSince)) + " s");
            Choreo.ClearBlocked(p);
            return true;
        }

        // Distance from a puppet to the chain end it leaves by (completion ordering).
        private static float EndDistance(PlanContext c, Puppet p, double now)
        {
            if (p.Plan.Count <= 0) return 0f;
            MachineMotion.State(p.Plan, c.Clk, now, out var s);
            return math.abs(Choreo.LeaveEnd(c, s.U, s.Hu >= 0f ? (sbyte)1 : (sbyte)-1) - s.U);
        }

        // The verge roles whose box would otherwise stand on the trench floor in the way of the trucks - the C1
        // grader following on the verge and the C2 parked excavator - are not used while their verge is blocked.
        private static bool IsVergeRole(PlanContext c, MachineRole role, CrewSlot slot) =>
            !c.Visible && math.abs(slot.Lateral) >= 0.99f &&
            ((role == MachineRole.Loader && slot.Activity == MachineActivity.Follow && c.View.Phase == WorksPhase.Excavation) ||
             (role == MachineRole.Loader && slot.Activity == MachineActivity.Parked) ||    // C2 grader parked on the verge
             (role == MachineRole.Excavator && slot.Activity == MachineActivity.Parked));

        private static bool VergeRoleBlocked(PlanContext c, MachineRole role, CrewSlot slot, Puppet p) => p.VergeBlocked && IsVergeRole(c, role, slot);

        // While a group is kept (open, about to open, draining or not ready), a role whose box
        // cannot fit the works half / the carriageway at its anchor is not spawned (an existing one keeps the group busy until it leaves). A verge role is not
        // spawned when its verge is blocked (cached per phase in MachineProjectState.NoVergePhase).
        private static bool SpawnFits(PlanContext c, MachineRole role, CrewSlot slot)
        {
            if (IsVergeRole(c, role, slot))
            {
                var vp = Probe(role);
                float vu = Choreo.ClampU(c, vp, Choreo.ShiftShared(c, slot.U));
                float vl = Choreo.LatOf(c, slot.Lateral, vu, vp);
                if (!Choreo.VergeSlot(c, vp, ref vu, ref vl, slot.Lateral < 0f ? -0.45f : 0.45f))
                {
                    c.CS.NoVergePhase[(int)role] = c.View.Phase;
                    if (RRWLog.VerboseEnabled) RRWLog.Verbose("machines: project #" + c.Project.Id + " crew " + c.CrewIndex + " " + role + " not used in " + c.View.Phase + ": its verge is blocked");
                    return false;
                }
            }
            if (!c.Visible && !c.KeepLeft && !c.KeepRight && !c.KeepParkL && !c.KeepParkR) return true;
            var probe = c.LateralMetres ? Probe(role, UwScaleOf(c, KindOf(role))) : Probe(role);
            float u = math.clamp(slot.U, c.Trim0, c.Trim1);
            if (c.LateralMetres) u = Choreo.ClampU(c, probe, u);
            bool ok = Choreo.LatRange(c, probe, u, out _, out _);
            if (!ok && c.LateralMetres) MxUpgradeStats.NoFit++;
            if (!ok && (c.State.NoFitLogged & (1 << (int)role)) == 0)
            {
                c.State.NoFitLogged |= 1 << (int)role;
                RRWLog.Info("machines: project #" + c.Project.Id + " " + role + " is not spawned: its box does not fit the works half / carriageway while lanes are open");
            }
            return ok;
        }

        // Perf: reusable probe puppets (a Puppet allocates its lists and caches; SpawnFits runs every update per waiting role)
        private static readonly Puppet[] s_Probes = new Puppet[(int)MachineRole.Count];
        private static Puppet Probe(MachineRole role)
        {
            var p = s_Probes[(int)role];
            if (p == null) s_Probes[(int)role] = p = new Puppet { Kind = KindOf(role), Role = role };
            ProbeSize(p);
            return p;
        }

        // A probe for a mode H band crew: the box of a live puppet of the same kind AND root scale, else the defaults scaled to it.
        private static readonly Puppet[] s_UwProbes = new Puppet[(int)MachineRole.Count];
        private static Puppet Probe(MachineRole role, float scale)
        {
            var p = s_UwProbes[(int)role];
            if (p == null) s_UwProbes[(int)role] = p = new Puppet { Kind = KindOf(role), Role = role, Upgrade = true };
            p.UwBand = -1;
            p.UwDropStretch = false;
            p.Scale = scale;
            foreach (var o in MachineRegistry.All)
                if (o.Kind == p.Kind && math.abs(o.Scale - scale) < 1e-3f) { p.BoxHalfWid = o.BoxHalfWid; p.BoxHalfLen = o.BoxHalfLen; p.BoxOffZ = o.BoxOffZ; return p; }
            ProbeSize(p);
            if (p.ExcavatorRig && math.abs(scale - RRWConst.kExcavatorScale) > 1e-3f)
            {
                float k = scale / RRWConst.kExcavatorScale;
                p.BoxHalfWid *= k; p.BoxHalfLen *= k;
            }
            return p;
        }

        // Root scale of a band crew's machine: the mini excavator (and grader) when the band is too narrow for the full one.
        private static float UwScaleOf(PlanContext c, MachineKind kind) =>
            c.LateralMetres && c.Crew.SmallMachines && (kind == MachineKind.Excavator || kind == MachineKind.Grader) ? RRWConst.kMiniExcavatorScale
            : kind == MachineKind.Excavator || kind == MachineKind.Grader ? RRWConst.kExcavatorScale : 1f;

        // Box width of a role before it exists (the last measured puppet of the same kind, else conservative defaults).
        private static void ProbeSize(Puppet probe)
        {
            foreach (var o in MachineRegistry.All)
                if (o.Kind == probe.Kind) { probe.BoxHalfWid = o.BoxHalfWid; probe.BoxHalfLen = o.BoxHalfLen; return; }
            switch (probe.Kind)
            {
                case MachineKind.Excavator:
                case MachineKind.Grader: probe.BoxHalfWid = 2.75f; probe.BoxHalfLen = 5.1f; break;
                case MachineKind.Truck: probe.BoxHalfWid = 1.5f; probe.BoxHalfLen = 4.25f; break;
                default: probe.BoxHalfWid = 1.25f; probe.BoxHalfLen = 3.6f; break;
            }
        }

        private static bool SameModel(in ProgressModel a, in ProgressModel b) => a.P0 == b.P0 && a.Frame0 == b.Frame0 && a.PPerFrame == b.PPerFrame;

        // Project-level part of the planning context (lane policy, overlap neighbours); SetCrew selects the crew.
        private PlanContext ProjectContext(EntityManager em, ProjectRecord pr, MachineProjectState st, TrackData td, double now)
        {
            MxPerf.Begin(MxT.B_Context);
            var c = m_Ctx;
            c.Em = em;
            c.Project = pr;
            c.View = pr.View();
            c.ViewBase = c.View;
            c.Upgrade = c.View.IsUpgrade;
            c.LateralMetres = false;
            c.UwBand = -1;
            c.State = st;
            c.Track = td;
            c.Tv = MachineTrackStore.View();
            c.Clk = MachineRegistry.Clock;
            c.Now = now;
            c.Working = pr.Working;
            c.Trim0 = c.View.Trim0;
            c.Trim1 = c.View.Trim1;
            c.ObjSearch = m_ObjSearch;
            c.NetSearch = m_NetSearch;
            c.Verbose = RRWDebug.Verbose;
            c.Cam = m_Cam;
            // rollers / IK digging
            c.Ps = m_PrefabSystem;
            var set = RRWSettings.Current;
            c.DetailedDigging = set != null && set.DetailedDiggingOn;
            c.RollersOn = set != null && set.RollersOn;
            SetCrew(c, 0, st);
            float flatMin = float.MaxValue;
            for (int i = 0; i < pr.Edges.Count; i++)
                if (SiteRegistry.TryGetEdge(pr.Edges[i], out var r)) flatMin = math.min(flatMin, r.Section.FlatHalfWidth);
            if (flatMin == float.MaxValue) flatMin = 6f;
            c.FlatHalfMin = flatMin;
            c.Narrow = 2f * flatMin < RRWConst.kTurnMinFlatWidth;
            c.CanTurn = !c.Narrow;
            SetLanePolicy(c, pr);
            c.LaneReadyOk = c.ReadyOk; c.LaneNoSpawn = c.NoSpawn; c.LaneNoSpawnWhy = c.NoSpawnWhy;
            c.LaneNarrow = c.Narrow; c.LaneCanTurn = c.CanTurn;
            if (c.Upgrade) UwCrewPolicy(c);   // crew 0 (SetCrew ran before the lane policy)
            // overlap planner inputs: this project's puppets + other projects' puppets near the chain
            c.Others.Clear();
            bool haveMid = ChainMap.Point(em, pr, 0.5f * pr.ChainLength, out float3 mid, out _);
            float reach = 0.5f * pr.ChainLength + RRWConst.kCrossProjectRadius + MxConst.kTrackExtra;
            foreach (var o in MachineRegistry.All)
            {
                if (o.ProjectId == pr.Id) { c.Others.Add(o); continue; }
                if (haveMid && o.HasPos && math.distance(o.LastPos.xz, mid.xz) < reach) c.Others.Add(o);
            }
            MxPerf.End(MxT.B_Context);
            return c;
        }

        // Select crew `crew` of the context's project: its CrewPlan, section, section front model and dig grid.
        private static void SetCrew(PlanContext c, int crew, MachineProjectState st)
        {
            int n = c.View.CrewCount;
            crew = math.clamp(crew, 0, n - 1);
            c.CrewIndex = crew;
            c.Crews = n;
            c.CS = st.Crews[crew];
            if (c.Upgrade) c.View = c.ViewBase;
            c.Crew = PhasePlan.Crew(c.View, crew);
            c.SecLo = c.Crew.SecLo;
            c.SecHi = c.Crew.SecHi;
            if (!(c.SecHi > c.SecLo)) { c.SecLo = c.Trim0; c.SecHi = c.Trim1; }
            c.Front = FrontOf(c.Project, crew, n);
            c.FrontSpeed = MachineMotion.FrontSpeed(c.Front, c.Clk, c.Now);
            if (c.Upgrade) UwCrewPolicy(c);   // band crew: metres, its band's phase, readiness
        }

        private PlanContext Context(EntityManager em, ProjectRecord pr, MachineProjectState st, TrackData td, double now, int crew)
        {
            var c = ProjectContext(em, pr, st, td, now);
            SetCrew(c, crew, st);
            return c;
        }

        // Lane policy. Groups a box keeps out of (c.Keep):
        //  * every group NOT in ProjectRecord.WorkZonesReady (closed + drained on every edge; stricter than "not open"):
        //    a group that is open, draining (SOFT) or whose drain report is stale is never planned, moved, backed out or parked into;
        //  * the groups the stage wants open or is draining (stage.Open | stage.Soft), so a half that is about to open is vacated
        //    before the report is taken (C4a: the chain-left half while the crew works in the chain-right half; C4b mirrored).
        // Visible road (construction C3/C4/Complete, or any kept sidewalk / open group): boxes stay inside the carriageway, no
        // verge slots, a K-turn only where its sweep fits (never with a kept half). A kept car half -> the per-half clamp relative
        // to EdgeSection.DirSplit (Choreo.LatRange); a kept parking strip -> the drive lanes on that side; a kerb fence that stands
        // or will stand (sidewalk open / wanted open beside a closed car half) -> fence clearance (inset: kKerbFenceInset +
        // kFenceMachineClearance). ReadyOk = at least one car half ready: otherwise no new plan / spawn (committed plans and
        // leavers run on: fails closed). NoSpawn also while a stage switch runs (Vacate / Swap / Drain).
        // Read every update from the Director's fields of this frame (order 210 < 240).
        private static void SetLanePolicy(PlanContext c, ProjectRecord pr)
        {
            var mst = c.State;
            bool constr = pr.Kind == WorksKind.Construction;
            var ph = pr.Phase;
            var v = c.View;
            c.Open = pr.OpenLanes & RoadZones.AllLanes;
            c.Releasing = pr.Releasing;
            c.Stage = PhasePlan.Stage(v);
            c.HalvesActive = v.HalvesActive;
            c.Ready = MachineDebug.LegacyReady ? RoadZones.AllLanes & ~c.Open & ~pr.SoftZones : pr.WorkZonesReady & RoadZones.AllLanes;
            RoadZones notReady = RoadZones.AllLanes & ~c.Ready;
            RoadZones wanted = (c.Stage.Open | c.Stage.Soft | c.Open | pr.SoftZones) & RoadZones.AllLanes;
            c.Keep = notReady | wanted;
            c.ReadyOk = (c.Ready & RoadZones.Carriageway) != RoadZones.None;
            c.NoSpawn = false;
            c.NoSpawnWhy = "";
            bool sw = pr.Switch == StageSwitch.Vacate || pr.Switch == StageSwitch.Swap || pr.Switch == StageSwitch.Drain;
            if (sw || !c.ReadyOk)
            {
                c.NoSpawn = true;
                // Perf: the gate text is rebuilt only when its inputs change (no per-update string allocation)
                if (mst == null || mst.WhySwitch != pr.Switch || mst.WhyReady != c.Ready || mst.NoSpawnWhy.Length == 0)
                {
                    string why = sw ? "stage switch " + pr.Switch : "no car half in WorkZonesReady (" + RoadZoneMath.Describe(c.Ready) + ")";
                    if (mst == null) { c.NoSpawnWhy = why; }
                    else { mst.NoSpawnWhy = why; mst.WhySwitch = pr.Switch; mst.WhyReady = c.Ready; }
                }
                if (mst != null) c.NoSpawnWhy = mst.NoSpawnWhy;
            }
            bool visiblePhase = constr && (ph == WorksPhase.Paving || ph == WorksPhase.Finishing || ph == WorksPhase.Complete);
            bool hidden = PhasePlan.IsHidden(pr.Kind, pr.Mode, ph);
            // a kept sidewalk on a visible road (C3/C4, D0 with open / draining house sides): stay inside the carriageway
            c.Visible = visiblePhase || c.Open != RoadZones.None || (!hidden && (c.Keep & RoadZones.Sidewalks) != RoadZones.None)
                        || v.IsUpgrade;   // upgrade works: the road stays visible and in use
            c.KeepLeft = (c.Keep & RoadZones.LeftHalf) != 0;
            c.KeepRight = (c.Keep & RoadZones.RightHalf) != 0;
            // parking strips that hold parked cars (EdgeRecord.ZonesParked: edges with buildings keep vanilla parking,
            // verified in game) are kept like a not-ready strip - boxes stay on the drive lanes where they fit (no spawn gate: WorkZonesReady
            // is unchanged)
            RoadZones parked = RoadZones.None;
            for (int i = 0; i < pr.Edges.Count; i++)
                if (SiteRegistry.TryGetEdge(pr.Edges[i], out var er)) parked |= er.ZonesParked & RoadZones.Parking;
            c.KeepParkL = c.Visible && ((c.Keep | parked) & RoadZones.ParkingLeft) != 0;
            c.KeepParkR = c.Visible && ((c.Keep | parked) & RoadZones.ParkingRight) != 0;
            // kerb fences beside an open / coming sidewalk whose car half is closed (PhasePlan.Props FenceSides, anticipated)
            RoadZones walk = (c.Open | c.Stage.Open) & RoadZones.Sidewalks;
            RoadZones openCar = (c.Open | c.Stage.Open) & RoadZones.Carriageway;
            c.FenceL = c.Visible && (walk & RoadZones.SidewalkLeft) != 0 && (openCar & RoadZones.LeftHalf) == 0;
            c.FenceR = c.Visible && (walk & RoadZones.SidewalkRight) != 0 && (openCar & RoadZones.RightHalf) == 0;
            c.FenceInset = RRWGates.FenceLateral == FenceLateral.Inset;
            c.CarriageHalfMin = TrackBuilder.CarriageHalfMin(c.Track);
            if (c.Visible)
                c.CanTurn = !c.Narrow && !c.KeepLeft && !c.KeepRight && c.CarriageHalfMin >= MxConst.kTurnSweepHalf;
        }

        // Dev / log: why the crew of a project is gated this update (rrw.mx.zones, rrw.dump; logged once per change).
        // Perf: the gate is stored as (role, reason, detail) and only turned into text for dumps / a changed reason.
        private static void Gate(MachineProjectState st, MachineRole role, string why, string detail)
        {
            st.GateRole = role;
            st.GatePrefix = why;
            st.GateDetail = detail;
            if (ReferenceEquals(st.LastGateLog, why) && ReferenceEquals(st.LastGateDetail, detail)) return;
            st.LastGateLog = why;
            st.LastGateDetail = detail;
            if (RRWLog.VerboseEnabled) RRWLog.Verbose("machines: project #" + st.ProjectId + " " + role + " waits: " + why + detail);
        }

        private void BuildPriority(PlanContext c)
        {
            m_Priority.Clear();
            var owner = c.Crew.FrontOwner(c.View.Phase);
            m_Priority.Add(owner);
            if (owner != MachineRole.TruckA) m_Priority.Add(MachineRole.TruckA);
            foreach (var r in s_Order)
            {
                bool have = false;   // (no List.Contains: the enum comparer may box)
                for (int i = 0; i < m_Priority.Count; i++) if (m_Priority[i] == r) { have = true; break; }
                if (!have) m_Priority.Add(r);
            }
        }

        // Narrow sites: no TruckB; verge roles only when the verge is usable.
        private bool RoleUsable(PlanContext c, MachineRole role, CrewSlot slot)
        {
            if (c.LateralMetres)
            {
                // a mode H band is one lane for the machines: one truck in line, loaded from the front; next to driveways the
                // excavator dumps on its spoil only (a loading truck would stand in a keep-out ahead of it)
                if (role == MachineRole.TruckB && slot.Activity != MachineActivity.DriveOut) return false;
                if (slot.Activity == MachineActivity.LoadAtFront && Choreo.UwKeepOutsOnSide(c, c.UwBand)) return false;
            }
            if (c.Narrow && role == MachineRole.TruckB) return false;
            // Without K-turns (visible road: C3) the single feed truck shuttles from a rolling base ahead of the
            // paver; the C3 waiting truck (reverse-in hand-over at the hopper) was the main source of trucks driving through
            // each other and is not used there
            if (!c.CanTurn && role == MachineRole.TruckB && slot.Activity != MachineActivity.DriveOut) return false;
            return true;
        }

        private static int IntentKey(MachineRole role, CrewSlot s, PlanContext c)
        {
            int h = (int)s.Activity * 7919 + (int)s.Load * 131 + s.Facing * 17 + (int)math.round(s.Lateral * 10f) * 3;
            if (s.Activity == MachineActivity.Parked || s.Activity == MachineActivity.DriveOut) h = h * 31 + (int)math.round(s.U);
            h = h * 31 + (c.Working ? 1 : 0);
            return h;
        }

        private static int CountLive() => MachineRegistry.All.Count;

        // ------------------------------------------------------------ planning dispatch

        private void Replan(Puppet p, PlanContext c, MachineRole role, CrewSlot slot)
        {
            MxPerf.Begin(MxT.B_Replan);
            MxPerf.Count(MxC.ReplanCalls);
            switch (slot.Activity)
            {
                case MachineActivity.Dig:
                case MachineActivity.Break:
                {
                    float lat = c.Narrow ? 0f : Choreo.DiggerLat(c, slot.Lateral, slot.U, p);
                    float off = slot.U - c.FrontAt(c.Now);
                    off = math.abs(off + RRWConst.kExcavatorBack) < 2f ? -RRWConst.kExcavatorBack : off;
                    var anim = slot.Activity == MachineActivity.Break ? AnimKind.Break : AnimKind.Dig;
                    if (p.Kind == MachineKind.LoaderDigger) anim = AnimKind.Dig;
                    float A = math.clamp(c.FrontAt(c.Now) + off, Choreo.CrewLo(c, p), Choreo.CrewHi(c, p));
                    bool digging = p.Plan.Count > 0 && p.Plan.Get(p.Plan.Count - 1).Kind == (byte)LegKind.DigHop;
                    bool across = TrackBuilder.CrossesMinimal(c.Track, A - 6f, A + 6f);
                    p.PlannedAcross = across && c.Working;
                    if (across)
                        Choreo.PlanFollow(p, c, off, Choreo.CrewLo(c, p), Choreo.CrewHi(c, p), lat, 1, AnimKind.Rest);   // mode-D span: drive across
                    else if (!c.Working && digging) Choreo.PlanIdle(p, c);                                                 // finish the cycle, rest pose
                    else if (!c.Working) Choreo.PlanStatic(p, c, A, lat, 1, true, AnimKind.Rest);
                    else Choreo.PlanDig(p, c, off, lat, anim);
                    break;
                }
                case MachineActivity.Spread:
                {
                    float lat = Choreo.LatOf(c, slot.Lateral, slot.U, p);
                    float off = slot.U - c.FrontAt(c.Now);
                    p.PlannedAcross = c.Working && TrackBuilder.CrossesMinimal(c.Track, slot.U - 8f, slot.U + 8f);   // mode-D drive-across (Replan compares it)
                    if (p.Kind == MachineKind.Grader)
                    {
                        // excavator spreading / levelling the dumped heaps: pivot 6 m behind the spread point (bucket reach)
                        float lo = Choreo.CrewLo(c, p), hi = Choreo.CrewHi(c, p);
                        GraderClamp(c, p, lat, ref lo, ref hi);
                        if (!c.Working || TrackBuilder.CrossesMinimal(c.Track, slot.U - 8f, slot.U + 8f))
                            Choreo.PlanFollow(p, c, off - 6f, lo, hi, lat, 1, AnimKind.Rest);
                        else Choreo.PlanGrade(p, c, off - 6f, lo, hi, lat, AnimKind.Spread, MxConst.kGradeHopStart, MxConst.kGradeHopDur);
                        break;
                    }
                    if (!c.Working || TrackBuilder.CrossesMinimal(c.Track, slot.U - 8f, slot.U + 8f))
                        Choreo.PlanFollow(p, c, off - 7f, Choreo.CrewLo(c, p), Choreo.CrewHi(c, p), lat, 1, AnimKind.Carry);
                    else Choreo.PlanSpread(p, c, off, lat);
                    break;
                }
                case MachineActivity.Scrape:
                {
                    float lat = Choreo.LatOf(c, slot.Lateral, slot.U, p);
                    if (p.Kind == MachineKind.Grader)
                    {
                        // excavator stripping the topsoil ahead of it and casting it to the left verge side
                        float lo = Choreo.CrewLo(c, p), hi = Choreo.CrewHi(c, p);
                        GraderClamp(c, p, lat, ref lo, ref hi);
                        if (!c.Working) Choreo.PlanFollow(p, c, -6f, lo, hi, lat, 1, AnimKind.Rest);
                        else Choreo.PlanGrade(p, c, -6f, lo, hi, lat, AnimKind.Scrape, MxConst.kStripHopStart, MxConst.kStripHopDur);
                        break;
                    }
                    if (!c.Working) Choreo.PlanFollow(p, c, -5f, Choreo.CrewLo(c, p), Choreo.CrewHi(c, p), lat, 1, AnimKind.Scrape);
                    else Choreo.PlanScrape(p, c, -5f, lat);
                    break;
                }
                case MachineActivity.Pave:
                case MachineActivity.Paint:
                case MachineActivity.Follow:
                case MachineActivity.Wait:
                {
                    FollowParams(c, p, role, slot, out float off, out float lo, out float hi, out float lat, out var ret);
                    if (p.Kind == MachineKind.Grader) GraderClamp(c, p, lat, ref lo, ref hi);
                    var anim = p.Kind == MachineKind.Loader ? AnimKind.Carry : AnimKind.Rest;
                    Choreo.PlanFollow(p, c, off, lo, hi, lat, slot.Facing >= 0 ? (sbyte)1 : (sbyte)-1, anim, ret);
                    break;
                }
                case MachineActivity.DriveOut:
                {
                    // (ProcessProject turns a live DriveOut role into a leaving puppet before it gets here)
                    Choreo.PlanLeave(p, c, slot.U, out _);
                    p.Outgoing = true;
                    break;
                }
                default:
                {
                    // Parked (and anything unexpected): stand at the slot
                    float u = Choreo.ClampU(c, p, Choreo.ShiftShared(c, slot.U));
                    float lat = Choreo.LatOf(c, slot.Lateral, u, p);
                    p.VergeBlocked = math.abs(slot.Lateral) >= 0.99f && !Choreo.VergeSlot(c, p, ref u, ref lat, slot.Lateral < 0f ? -0.45f : 0.45f);
                    // a parked slot never stands on another machine's resting box or in a working machine's path
                    if (Choreo.ResolveStatic(c, p, ref u, lat, out string shifted) && RRWLog.VerboseEnabled)
                        RRWLog.Verbose("machines: " + p + " parked " + shifted + " (u=" + RRWLog.F(u) + ")");
                    Choreo.UwShiftOutOfKeepOut(c, p, ref u, lat, slot.Facing >= 0 ? 1f : -1f);   // mode H: never parked in a driveway
                    var anim = p.Kind == MachineKind.Loader || p.Kind == MachineKind.LoaderDigger ? AnimKind.Carry : AnimKind.Rest;
                    Choreo.PlanStatic(p, c, u, lat, slot.Facing >= 0 ? (sbyte)1 : (sbyte)-1, p.Kind == MachineKind.Rmv, anim);
                    break;
                }
            }
            MxPerf.End(MxT.B_Replan);
        }

        // Follow offsets per (phase, role), mirroring PhasePlan.Crew (anchor = max(StartSlotIn(k), F - d), section-relative)
        // and the return-aware anchors (PhasePlan.ReturnAware - home by the deadline, moving back at
        // kCrewReturnSpeedFactor x VFwd): the C1 grader (StartSlotIn 1, end of C1), the C3 crew truck (StartSlotIn 0, end of C3) and
        // crew 0's C4 crew truck (StartSlotIn 0, kC4TeardownF).
        public static void FollowParams(PlanContext c, Puppet p, MachineRole role, CrewSlot slot, out float off, out float lo, out float hi, out float lat, out Choreo.RetSpec ret)   // (public: offline tests)
        {
            float F = c.FrontAt(c.Now);
            lo = Choreo.CrewLo(c, p);
            hi = Choreo.CrewHi(c, p);
            off = slot.U - F;
            ret = default;
            var ph = c.View.Phase;
            var v = c.View;
            float sl = c.SecLo, sh = c.SecHi;
            switch (role)
            {
                case MachineRole.Loader:
                    if (ph == WorksPhase.Excavation)
                    {
                        off = -30f;
                        if (v.PhaseSeconds > 0f)
                        {
                            // StartSlotIn(2) (= PhasePlan C1 grader home), clear of the crew props / depots
                            float home = PhasePlan.StartSlotIn(2, sl, sh);
                            lo = math.max(lo, home);
                            ret = Ret(c, home, 1f, role);
                        }
                        else lo = math.max(lo, PhasePlan.StartSlotIn(2, sl, sh));
                    }
                    else if (ph == WorksPhase.Paving) { off = -15f; lo = math.max(lo, PhasePlan.StartSlotIn(1, sl, sh)); }
                    break;
                case MachineRole.CrewTruck:
                    if (ph == WorksPhase.Paving)
                    {
                        // with rollers between the paver and the crew truck it follows further back (PhasePlan.Crew)
                        off = c.Crew.Rollers >= 2 ? -RRWConst.kC3CrewTruckBackTwoRollers : c.Crew.Rollers == 1 ? -RRWConst.kC3CrewTruckBackOneRoller : -32f;
                        // = PhasePlan C3 crew truck home (crews > 0 clear of crew i-1's paver at the boundary)
                        float home = PhasePlan.StartSlotIn(0, sl, sh) + (c.CrewIndex > 0 ? RRWConst.kC3InteriorHomeShift : 0f);
                        lo = math.max(lo, home);
                        if (v.PhaseSeconds > 0f) ret = Ret(c, home, 1f, role);
                    }
                    else if (ph == WorksPhase.Finishing)
                    {
                        off = -15f;
                        float home = PhasePlan.StartSlotIn(0, sl, sh);
                        lo = math.max(lo, home);
                        if (c.CrewIndex == 0 && v.PhaseSeconds > 0f) ret = Ret(c, home, RRWConst.kC4TeardownF, role);
                    }
                    break;
                case MachineRole.Finisher:
                    off = RRWConst.kFinisherLead;
                    break;
                case MachineRole.TruckB:
                    if (slot.Activity == MachineActivity.Wait) off = 25f;
                    break;
            }
            if (hi < lo) hi = lo;
            float u = math.clamp(F + off, lo, hi);
            if (ret.On) u = math.min(u, ret.Home + ret.Speed * (float)math.max(0.0, ret.By - c.Now));
            lat = Choreo.LatOf(c, slot.Lateral, u, p);
            if (!c.LateralMetres && math.abs(slot.Lateral) >= 0.99f)
            {
                // verge followers: validate the verge around the anchor; blocked -> floor fallback lane
                float uu = u, ll = lat;
                p.VergeBlocked = !Choreo.VergeSlot(c, p, ref uu, ref ll, slot.Lateral < 0f ? -0.45f : 0.45f);
                if (math.abs(ll) < math.abs(lat) - 0.01f) lat = ll;
                // VergeSlot moved the anchor FORWARD to a clear verge spot (props / depots / blocked verge):
                // hold there (raise lo) instead of discarding it; a backward shift is ignored (the follow would
                // otherwise be pinned behind its front for the rest of the plan)
                else if (!p.VergeBlocked && uu > u + 0.05f) { lo = math.max(lo, uu); if (hi < lo) hi = lo; }
            }
            // A crew truck held at its home (its anchor below the lower clamp: C3 / C4 at the section start) never waits in the
            // path of its crew's path owner (the paver / painter starting at the section start drove into it; at a trimmed chain start both
            // stood on the same u): a lateral clear of it, else the home behind it (only when it gets there without passing
            // the owner; the paths that count are those reached before the front takes the crew truck off its home). Otherwise:
            //  * it stands ahead of its painter IN the painter's lane of a narrow C4 works half (C3 -> C4 relay): it LEADS the painter for
            //    the rest of the phase / stage (Choreo.LeadParams) - never reversing through it;
            //  * the owner is ahead of it or can pass it (another lane / the verge): the front owner waits ahead of its home instead
            //    (CrewState.OwnerLo, read below by the owner's own FollowParams);
            //  * a crew truck that does not exist yet (spawn): its spawn spot waits until the owner has moved on (SpawnSpot).
            if (role == MachineRole.CrewTruck && c.Track != null)
            {
                bool live = p.Plan.Count > 0 && c.CS.Roles[(int)MachineRole.CrewTruck] == p;
                int stage = c.Stage.Index;
                bool leading = live && ph == WorksPhase.Finishing && p.LeadPhase == ph && p.LeadStage == stage && p.LeadCrew == c.CrewIndex;
                if (leading && Choreo.LeadParams(c, p, out float lOff, out float lLo, out float lHi))
                {
                    off = lOff; lo = lLo; hi = lHi; ret = default;
                }
                else if (F + off < lo + 0.5f)
                {
                    if (leading) p.LeadPhase = WorksPhase.None;   // its painter is gone: follow as usual
                    float uMin = c.Crews > 1 ? math.max(Choreo.AnchorLo(c, p), c.SecLo - 2f * MxConst.kSecSlack) : Choreo.AnchorLo(c, p);
                    // the home is held until the front takes the crew truck off it: (home - (F + off)) / front speed (+ 10 s)
                    float vF = math.abs(c.FrontSpeed);
                    float horizon = vF > 0.01f ? math.clamp((lo - (F + off)) / vF + 10f, 10f, 300f) : 300f;
                    Choreo.ClearOfPaths(c, p, ref lo, ref lat, uMin, horizon, out Puppet blk, out int rel);
                    if (hi < lo) hi = lo;
                    bool own = blk != null && !blk.IsRoller && blk.Crew == c.CrewIndex && blk.Role == c.Crew.FrontOwner(ph);
                    if (own && live && rel == Choreo.kRelAheadInBand)
                    {
                        if (ph == WorksPhase.Finishing && Choreo.LeadParams(c, p, out lOff, out lLo, out lHi))
                        {
                            p.LeadPhase = ph; p.LeadStage = stage; p.LeadCrew = c.CrewIndex;
                            off = lOff; lo = lLo; hi = lHi; ret = default;
                            MachineDebug.CrewTruckLeads++;
                            RRWLog.Verbose("machines: " + p + " leads " + blk + " (it stands ahead of it in a works half too narrow to pass)");
                        }
                        // (C3: no lead - the feed truck works ahead of the paver; the guard's make-way moves the crew truck)
                    }
                    else if (own && live)
                    {
                        float want = lo + Choreo.SepU(p, blk);
                        if (!(c.CS.OwnerLoPhase == ph && math.abs(c.CS.OwnerLo - want) < 0.5f))
                        {
                            c.CS.OwnerLo = want;
                            c.CS.OwnerLoPhase = ph;
                            blk.IntentKey = int.MinValue;   // the owner re-plans with the raised clamp next update
                            MachineDebug.OwnerWaits++;
                            RRWLog.Verbose("machines: " + blk + " waits ahead of " + p + " at u>=" + RRWLog.F(want) + " (no room behind / beside it at the section start)");
                        }
                    }
                }
                if (!(live && p.LeadPhase == ph && p.LeadStage == stage && p.LeadCrew == c.CrewIndex) && Choreo.BehindOwnerHi(c, p, out float oHi) && oHi < hi)
                {
                    // the owner's anchor stops at its upper clamp (the last crew's painter at AnchorHi, ~10 m before its front
                    // reaches Trim1): the follower behind it never closes in further than the separation (F - 15 came within 6.9 m)
                    hi = oHi;
                    if (lo > hi) lo = hi;
                }
            }
            // The paver / painter of a crew whose crew truck stands (or is homing) right behind its anchor in its lane -
            // at a clamped section start, or where the painter spawned (slid ahead of the crew truck's spot) - waits ahead of it instead
            // of driving back onto it; the crew truck's own FollowParams then keeps its home behind (or raises CrewState.OwnerLo below)
            if (role == MachineRole.Finisher && (ph == WorksPhase.Paving || ph == WorksPhase.Finishing) && c.Track != null)
            {
                var ctp = c.CS.Roles[(int)MachineRole.CrewTruck];
                if (ctp != null && ctp != p && !ctp.PostSite && !ctp.Outgoing && ctp.Plan.Count > 0 &&
                    !(ctp.LeadPhase == ph && ctp.LeadCrew == c.CrewIndex && ctp.LeadStage == c.Stage.Index))
                {
                    MachineMotion.State(ctp.Plan, c.Clk, c.Now, out var cts0);
                    var cl = ctp.Plan.Get(ctp.Plan.Count - 1);
                    float home = cl.Kind == (byte)LegKind.Follow ? cl.Lo : cl.Kind == (byte)LegKind.Hold ? cl.U0 : cts0.U;
                    float ctU = math.max(cts0.U, home);
                    float band = p.BoxHalfWid + ctp.BoxHalfWid + 2f * MxConst.kOverlapMargin;
                    bool inBand = math.abs(cts0.Lat - lat) < band || math.abs(cl.L0 - lat) < band;
                    float pU = math.max(F + off, lo);
                    if (p.Plan.Count > 0) { MachineMotion.State(p.Plan, c.Clk, c.Now, out var ps0); pU = ps0.U; }
                    float want = ctU + Choreo.SepU(p, ctp);
                    if (inBand && ctU <= pU + 0.05f && want > lo && want <= hi)
                    {
                        lo = want;
                    }
                }
            }
            if (role == MachineRole.Finisher && c.CS.OwnerLoPhase == ph && c.CS.OwnerLo > lo)
            {
                // ... only while that crew truck still stands at (or drives to) that home: its plan ends in its Follow leg clamped
                // there or in a hold there (it may have left, or follows its front by now)
                var ct = c.CS.Roles[(int)MachineRole.CrewTruck];
                if (ct != null && !ct.PostSite && !ct.Outgoing && ct.Plan.Count > 0)
                {
                    var last = ct.Plan.Get(ct.Plan.Count - 1);
                    float home = last.Kind == (byte)LegKind.Follow ? last.Lo : last.Kind == (byte)LegKind.Hold ? last.U0 : float.NaN;
                    if (!float.IsNaN(home) && math.abs(home + Choreo.SepU(ct, p) - c.CS.OwnerLo) < 1.0f)
                    {
                        lo = math.min(c.CS.OwnerLo, math.max(lo, hi));
                        if (hi < lo) hi = lo;
                    }
                }
            }
        }

        // PhasePlan.ReturnAware as a leg cap: home by (fEnd - f) x PhaseSeconds - kCrewReturnMarginSeconds from now.
        private static Choreo.RetSpec Ret(PlanContext c, float home, float fEnd, MachineRole role)
        {
            var v = c.View;
            float ps = v.PhaseSeconds;
            if (!(ps > 0f)) return default;
            float tLeft = math.max(0f, (fEnd - v.F) * ps - RRWConst.kCrewReturnMarginSeconds);
            var lim = MachineLimits.Of(role, v.Phase);
            // the anchor moves back while the machine faces forward (it reverses): never faster than its VRev (the crew truck's
            // 0.5 x 6 m/s would be 3 m/s against VRev 2); a slower return simply starts earlier and is home by the same deadline
            float speed = math.min(RRWConst.kCrewReturnSpeedFactor * lim.VFwd, RRWConst.kCrewReturnRevFactor * lim.VRev);   // = PhasePlan.ReturnAware
            return new Choreo.RetSpec { Home = home, Speed = speed, By = c.Now + tLeft };
        }

        // Grader planning clamp: a digger standing still (parked / finished) on the grader's lane is a wall the grader's
        // front-relative anchor never crosses (keeps the role plan valid instead of relying on the guard's yields).
        private static void GraderClamp(PlanContext c, Puppet g, float lat, ref float lo, ref float hi)
        {
            var e = c.CS.Roles[(int)MachineRole.Excavator];
            if (e == null || e == g || e.PostSite || e.Plan.Count <= 0 || g.Plan.Count <= 0) return;
            var last = e.Plan.Get(e.Plan.Count - 1);
            if (last.Kind != (byte)LegKind.Hold || !last.OpenEnded) return;
            MachineMotion.State(e.Plan, c.Clk, math.max(c.Now, e.Plan.Epoch + last.T0), out var es);
            if (math.abs(es.Lat - lat) >= Choreo.SepLat(g, e)) return;
            MachineMotion.State(g.Plan, c.Clk, c.Now, out var gs);
            float sep = Choreo.SepU(g, e);
            if (es.U >= gs.U) hi = math.min(hi, es.U - sep);
            else lo = math.max(lo, es.U + sep);
            if (hi < lo) hi = lo;
        }

        // dev rrw.mx.perf: the excavator <-> grader guard in its own timing section
        private void GraderGuard(PlanContext c, Puppet g, MachinePlan previous, bool replanned)
        {
            MxPerf.Begin(MxT.B_GraderGuard);
            Guard(c, g, previous, replanned);
            MxPerf.End(MxT.B_GraderGuard);
        }

        // Excavator <-> grader guard (the two excavator-sized machines must never intersect). The digger owns the
        // front and never yields; the grader's plan is checked against the digger's plan over kGuardHorizon seconds after
        // every grader re-plan and once per machine second; a predicted contact replaces it with a yield plan
        // (Choreo.YieldBegin / YieldStep) that stands for kGuardYieldHold seconds before the role plan is tried again.
        // The check runs inside the per-update check budget (cut: retried next update) and the periodic
        // checks of several graders are staggered by seed; a predicted contact starts a yield search that evaluates ONE candidate per
        // update (Choreo.YieldBegin / YieldStep).
        private void Guard(PlanContext c, Puppet g, MachinePlan previous, bool replanned)
        {
            if (g == null || g.PostSite || g.Plan.Count <= 0) return;
            var e = c.CS.Roles[(int)MachineRole.Excavator];
            if (e == null || e == g || e.PostSite || e.Plan.Count <= 0) { g.Yielding = false; g.YieldK = -1; return; }
            if (replanned) g.YieldK = -1;   // a new role plan: a running yield search is moot (the new plan is checked below)
            if (g.YieldK >= 0) { YieldStep(c, g, e); return; }
            if (!replanned && c.Now < g.GuardNext) return;
            // a yield that found no clean slot stands until its hold ends (no re-yield every second)
            if (!replanned && g.Yielding && g.YieldFailed && c.Now < g.ReplanAt) return;
            long t0 = MxBudget.Start();
            // (a check cut twice in a row runs to its end: it always gets a result)
            long dl = g.CheckAborts >= 2 ? Choreo.NoDeadline : MxBudget.CheckDeadline(t0);
            bool hit = Choreo.PairConflict(g, g.Plan, e, c, c.Now, c.Now + MxConst.kGuardHorizon, out double tc, true, dl, out bool aborted);
            MxBudget.StopCheck(t0);
            if (aborted) { g.CheckAborts++; g.GuardNext = c.Now; return; }   // (no result: the plan is checked again next update)
            g.CheckAborts = 0;
            g.GuardNext = c.Now + 1.0 + (g.Seed & 15) * (1.0 / 60.0);
            if (!hit)
            {
                if (replanned) g.Yielding = false;
                return;
            }
            if (replanned && previous.Count > 0) g.Plan = previous;   // yield from the motion that is actually running
            Choreo.YieldBegin(g, e, c, tc);
            YieldStep(c, g, e);
        }

        private static void YieldStep(PlanContext c, Puppet g, Puppet e)
        {
            long t0 = MxBudget.Start();
            // (a candidate whose check was cut twice in a row runs to its end: the search always ends)
            bool done = Choreo.YieldStep(g, e, c, g.CheckAborts >= 2 ? Choreo.NoDeadline : MxBudget.CheckDeadline(t0), out bool clean, out string how);
            MxBudget.StopCheck(t0);
            if (!done) return;
            g.Yielding = true;
            g.YieldFailed = !clean;
            MachineDebug.GuardYields++;
            if (!clean) MachineDebug.GuardFailures++;
            string msg = "machines: guard p" + g.ProjectId + " grader yields to the excavator (contact predicted in " + RRWLog.F((float)(g.YieldTc - c.Now)) +
                         " s): " + how + (clean ? "" : " - NO conflict-free slot (verges blocked, floor too narrow)");
            if (!clean) RRWLog.Once("machines-guard-fail-" + g.ProjectId, msg);
            else if (MachineDebug.LogDespawns) RRWLog.Info(msg);
            else RRWLog.Verbose(msg);
        }

        // A Dig / Break / Spread role re-plans when the mode-D (Minimal) decision Replan makes CHANGES: the drive-across
        // plan vs the work plan (Puppet.PlannedAcross). Comparing the RUNNING leg kind instead would make every Drive / Hold before the
        // first DigHop count as "not working" and re-plan every 64 updates: the 12.6 s hold before an IK dig cycle would never run out
        // (each re-plan adds a short drive to the re-anchored anchor and moves the first cycle one grid slot on).
        private bool MinimalSwitch(Puppet p, PlanContext c, CrewSlot slot)
        {
            if (!c.Working || (RRWClock.UpdateIndex & 63u) != (p.Seed & 63u)) return false;
            if (slot.Activity != MachineActivity.Dig && slot.Activity != MachineActivity.Break && slot.Activity != MachineActivity.Spread) return false;
            if (p.Plan.Count <= 0) return false;
            return AcrossMinimal(c, p, slot) != p.PlannedAcross;
        }

        // The mode-D decision of Replan for a working Dig / Break / Spread role (the same ranges).
        private static bool AcrossMinimal(PlanContext c, Puppet p, CrewSlot slot)
        {
            if (slot.Activity == MachineActivity.Spread) return TrackBuilder.CrossesMinimal(c.Track, slot.U - 8f, slot.U + 8f);
            float off = slot.U - c.FrontAt(c.Now);
            off = math.abs(off + RRWConst.kExcavatorBack) < 2f ? -RRWConst.kExcavatorBack : off;
            float A = math.clamp(c.FrontAt(c.Now) + off, Choreo.CrewLo(c, p), Choreo.CrewHi(c, p));
            return TrackBuilder.CrossesMinimal(c.Track, A - 6f, A + 6f);
        }

        // Truck state machine: Home (idle at base) -> Outbound (approach + work) -> Return (+ trip wait) -> ...
        private void UpdateTruck(Puppet p, PlanContext c, MachineRole role, CrewSlot slot, double now, bool force)
        {
            var cfg = Choreo.TruckConfig(c, p, role, slot);
            bool workActivity = slot.Activity == MachineActivity.LoadAtFront || slot.Activity == MachineActivity.Shuttle ||
                                slot.Activity == MachineActivity.DumpAtFront || slot.Activity == MachineActivity.Wait;
            if (!workActivity)
            {
                if (force || p.Plan.Count == 0 || (p.GuardHeld && now >= p.ReplanAt))
                {
                    var saved = p.Plan;
                    bool held = p.GuardHeld;
                    Replan(p, c, role, slot);
                    if (held) ReleaseCheckTimed(p, c, saved, force);
                }
                return;
            }
            // A forced return (not working / phase or intent change) is time-sliced like every truck
            // plan (inline it would run up to kOverlapTries + 1 full-horizon conflict checks): it is marked here (RetForced) and planned in
            // this and the next updates inside the planning budget; the truck keeps its committed plan meanwhile
            if (!c.Working)
            {
                if (force || p.Stage == TruckStage.Outbound)
                {
                    // finish the current leg, then wait at the base slot
                    p.OutAttempt = p.RetAttempt = -1;
                    if (p.Stage != TruckStage.Home || force) { p.RetForced = 1; p.Stage = TruckStage.Home; }
                }
                if (p.RetForced != 0) ForcedReturn(p, c, cfg);
                return;
            }
            if (force)
            {
                p.OutAttempt = p.RetAttempt = -1;
                p.RetForced = 2;
                p.Stage = TruckStage.Home;
            }
            if (p.RetForced != 0) { ForcedReturn(p, c, cfg); return; }
            // The excavator re-phased the crew's dig grid (first cycle on its arrival). A loading trip planned on the old
            // phase that has no dumps yet re-plans its outbound onto the new one (time-sliced like any outbound); with dumps the grid
            // stays locked (Choreo.GridLocked), so this never strands a load
            if (cfg.Mode == 1 && p.Stage == TruckStage.Outbound && c.CS != null && p.GridEpoch != c.CS.GridEpoch)
            {
                p.GridEpoch = c.CS.GridEpoch;
                if (p.Buckets == 0 && p.AmountPct <= 0)
                {
                    p.Stage = TruckStage.Home;
                    p.OutAttempt = p.RetAttempt = -1;
                    p.ReplanAt = now;
                    MachineDebug.TruckGridReplans++;
                }
            }
            if (now < p.ReplanAt) return;
            var other = c.CS.Roles[(int)(role == MachineRole.TruckA ? MachineRole.TruckB : MachineRole.TruckA)];
            // both halves of the cycle are time-sliced (one attempt per update, global budget)
            if (p.Stage == TruckStage.Outbound) Choreo.PlanTruckReturn(p, c, cfg, true);
            else Choreo.PlanTruckOutbound(p, c, cfg, now, other);
        }

        // A pending forced return (UpdateTruck): one time-sliced step; once decided (committed or stood still) the truck waits at its
        // base as before (Stage Home; after a forced re-plan the next outbound is planned 1 s after the arrival).
        private static void ForcedReturn(Puppet p, PlanContext c, in TruckCfg cfg)
        {
            if (!Choreo.PlanTruckReturn(p, c, cfg, false)) return;
            byte mode = p.RetForced;
            p.RetForced = 0;
            p.Stage = TruckStage.Home;
            if (mode == 2) p.ReplanAt = ArrivalOf(p) + 1.0;
        }

        // Machine time the last leg of the plan starts (arrival at the final hold).
        private static double ArrivalOf(Puppet p)
        {
            if (p.Plan.Count <= 0) return 0.0;
            return p.Plan.Epoch + p.Plan.Get(p.Plan.Count - 1).T0;
        }

        // ------------------------------------------------------------ spawn

        private Puppet Spawn(EntityManager em, PlanContext c, MachineRole role, CrewSlot slot, double now, RRWSetting settings)
        {
            var kind = KindOf(role);
            Entity prefab = PrefabFor(kind, settings, out float scale, out kind);
            if (prefab == Entity.Null) return null;
            // mode H: a band too narrow for the full excavator gets the mini excavator (the same rig at a smaller root scale)
            if (c.LateralMetres && c.Crew.SmallMachines && (kind == MachineKind.Excavator || kind == MachineKind.Grader)) scale = RRWConst.kMiniExcavatorScale;
            var p = new Puppet
            {
                ProjectId = c.Project.Id, Role = role, Kind = kind, Prefab = prefab, Scale = scale,
                Seed = (ushort)c.Project.Seed, SpawnUpdate = RRWClock.UpdateIndex, Track = c.Track,
                Crew = c.CrewIndex, LimPhase = c.View.Phase,
                Upgrade = c.Upgrade, UwBand = c.LateralMetres ? c.UwBand : -1,
            };
            var rnd = new Unity.Mathematics.Random((uint)(c.Project.Seed * 7919u + (uint)role * 104729u + (uint)c.CrewIndex * 15485863u) | 1u);
            p.LightJitter = rnd.NextFloat(-0.05f, 0.05f);
            PuppetFactory.Measure(em, p);
            PuppetFactory.MeasureFootprint(em, m_PrefabSystem, p);
            var plan0 = new MachinePlan { Track = c.Track.Slot, Role = (byte)role, MKind = (byte)kind, SlewDeg = 90f, FrontA = c.Front, FrontB = c.Front };
            PuppetFactory.Footprint(p, ref plan0);

            // start state: the role anchor directly (after load / rebuild / ModelReset / allowance gain; a role whose puppet
            // left at a hand-over, and the C4b crews at their section starts) or the site entry
            int bit = 1 << (int)role;
            bool atAnchor = c.State.SpawnAtAnchor || (c.CS.AnchorMask & bit) != 0 || (c.HalvesActive && c.Stage.Index == 1);
            StartState(c, p, role, slot, atAnchor, out float u0, out float lat0, out sbyte f0);
            // never spawn inside another machine - slide along the lane, else try again next update
            // (1 s later - no Puppet allocation every update while the spot is taken / in a path owner's path).
            // A mode H band strip is one lane wide: the crew truck and the trucks waiting at its entry would keep the entry
            // taken for good, so a band machine whose entry spot is taken starts at its work anchor instead.
            bool spot = SpawnSpot(c, p, ref u0, lat0);
            if (!spot && c.LateralMetres && !atAnchor)
            {
                StartState(c, p, role, slot, true, out u0, out lat0, out f0);
                spot = SpawnSpot(c, p, ref u0, lat0);
            }
            if (!spot)
            {
                c.CS.SpawnRetryAt[(int)role] = now + 1.0;
                if (RRWLog.VerboseEnabled) RRWLog.Verbose("machines: spawn of p" + c.Project.Id + "/c" + c.CrewIndex + "/" + role + " deferred: no free spot near u=" + RRWLog.F(u0) + " lat=" + RRWLog.F(lat0));
                return null;
            }
            // the spawn box lies only in groups of WorkZonesReady (Outside aside); else wait (the gate opens
            // when the group is drained / the half is no longer kept). Mode H band crews: inside the band's machine-safe run
            if (c.LateralMetres ? !UwSpawnBoxOk(c, p, u0, lat0) : (StandZones(c, p, u0, lat0) & RoadZones.AllLanes & ~c.Ready) != RoadZones.None)
            {
                Gate(c.State, role, c.LateralMetres ? "spawn box outside the band's machine-safe run" : "spawn box touches a not-ready group", null);
                return null;
            }
            if ((c.CS.AnchorMask & bit) != 0) { c.CS.AnchorMask &= ~bit; MxSpeed.HandAnchorSpawns++; }
            // a freshly (re)built track may still have this edge queued at FloorRel 0 (the new puppet's NowU is NaN,
            // so the "under a puppet" rule missed it): refresh the spawn edge now, the puppet never floats above a trench
            try { TrackBuilder.EnsureY(em, m_Terrain, c.Track, u0 - 20f, u0 + 20f); }
            catch (Exception ex) { RRWLog.ErrorOnce("machines spawn track y", ex); }
            var leg = new MachineLeg { T0 = 0f, T1 = float.PositiveInfinity, Kind = (byte)LegKind.Hold, Facing = f0, U0 = u0, L0 = lat0 };
            plan0.Epoch = now;
            plan0.Set(0, leg);
            plan0.Count = 1;
            p.Plan = plan0;
            LoadKind load = slot.Load == LoadKind.None ? LoadKind.Stone : slot.Load;
            int pct = 0;
            if (p.IsTruck)
            {
                var cfg = Choreo.TruckConfig(c, p, role, slot);
                pct = cfg.Mode == 1 ? 0 : 100;
                p.Stage = TruckStage.Home;
                p.ReplanAt = now + (role == MachineRole.TruckB ? 20.0 : 1.0) + (p.Seed % 5);
            }
            else Replan(p, c, role, slot);
            p.Activity = slot.Activity;
            p.IntentKey = RoleKey(c, p, role, slot);
            p.PlannedPhase = c.View.Phase;
            p.PlannedWorking = c.Working;
            p.PlannedTrackRevision = c.Track.Revision;
            MachineMotion.State(p.Plan, c.Clk, now, out var s);
            if (!MachineMotion.Pose(p.Plan, c.Tv, s, out float3 pos, out quaternion rot)) return null;
            bool noSub = p.ExcavatorRig;
            Entity e = PuppetFactory.Create(em, p, p.Plan, pos, rot, noSub, load, pct);
            if (e == Entity.Null)
            {
                RRWLog.Once("machines-spawnfail-" + kind, "machines: cannot build a puppet archetype for " + EcsUtil.PrefabName(m_PrefabSystem, em, prefab));
                return null;
            }
            p.E = e;
            p.PlanDirty = false;
            p.LastPos = pos;
            p.HasPos = true;
            c.CS.Roles[(int)role] = p;
            MachineRegistry.Add(p);
            // the planning context of this update sees the new puppet (otherwise the crew truck spawned right
            // after its painter would test its spawn spot / home against a neighbour list without the painter and spawn on it)
            if (!c.Others.Contains(p)) c.Others.Add(p);
            RRWLog.Once("machines-kind-" + kind, "machines: first " + kind + " puppet (" + role + ") = " + EcsUtil.PrefabName(m_PrefabSystem, em, prefab) +
                        " scale=" + RRWLog.F(scale) + " box=" + RRWLog.F(2f * p.BoxHalfLen) + "x" + RRWLog.F(2f * p.BoxHalfWid) + " offZ=" + RRWLog.F(p.BoxOffZ) +
                        " footprint=" + p.FootSource + " " + RRWLog.F(2f * p.Plan.HalfLen) + "x" + RRWLog.F(2f * p.Plan.HalfWid) + " off=" + RRWLog.F(p.Plan.FootOff));
            if (RRWLog.VerboseEnabled)
            RRWLog.Verbose("machines: spawn " + p + " at u=" + RRWLog.F(u0) + " lat=" + RRWLog.F(lat0) + (atAnchor ? " (anchor)" : " (entry)") +
                           " box=" + RRWLog.F(2f * p.BoxHalfLen) + "x" + RRWLog.F(2f * p.BoxHalfWid) + " foot=" + p.FootSource + " " + RRWLog.F(2f * p.Plan.HalfLen) +
                           "x" + RRWLog.F(2f * p.Plan.HalfWid) + " off=" + RRWLog.F(p.Plan.FootOff));
            return p;
        }

        // Zones (RoadZoneMath.OfLateral, chain frame) of a box of p standing at (u, lat) along the chain (both box ends).
        internal static RoadZones StandZones(PlanContext c, Puppet p, float u, float lat)
        {
            float hl = p.BoxHalfLen + math.abs(p.BoxOffZ), hw = p.BoxHalfWid;
            var z = RoadZones.None;
            if (TrackBuilder.SectionAt(c.Track, u - hl, out var s0, out bool r0)) z |= RoadZoneMath.OfLateral(lat - hw, lat + hw, s0, r0);
            if (TrackBuilder.SectionAt(c.Track, u + hl, out var s1, out bool r1)) z |= RoadZoneMath.OfLateral(lat - hw, lat + hw, s1, r1);
            return z;
        }

        // A spawn spot is also out of every working machine's path (InWorkPath; not for the front owner itself): sliding a
        // truck forward past the excavator that stood on its base spot would put it in the excavator's way (seen in D0) - it waits instead.
        public static bool SpawnSpot(PlanContext c, Puppet p, ref float u, float lat)   // (public: offline tests)
        {
            bool owner = !p.IsRoller && p.Role == c.Crew.FrontOwner(c.View.Phase);
            bool pathHit = false;
            for (int k = 0; k < 9; k++)
            {
                int m = (k + 1) / 2;
                float want = u + ((k & 1) == 1 ? -1f : 1f) * m * MxConst.kParkStep;
                float uu = Choreo.ClampU(c, p, want);
                if (k > 0 && math.abs(uu - want) > 0.5f) continue;
                if (!Choreo.SpotFree(p, c, uu, lat)) continue;
                if (c.Upgrade && Choreo.UwBandOf(c, p) >= 0 && Choreo.UwInKeepOut(c, p, uu, lat)) continue;   // never spawned standing in a driveway
                if (!owner && Choreo.InWorkPath(c, p, uu, lat, MxConst.kSpawnPathHorizon, MxConst.kSpawnPathHorizonOther) != null) { pathHit = true; continue; }
                u = uu;
                return true;
            }
            if (pathHit) MachineDebug.SpawnPathDeferrals++;
            return false;
        }

        private static MachineKind KindOf(MachineRole r)
        {
            switch (r)
            {
                case MachineRole.Excavator: return MachineKind.Excavator;
                case MachineRole.Loader: return MachineKind.Grader;
                case MachineRole.TruckA:
                case MachineRole.TruckB: return MachineKind.Truck;
                default: return MachineKind.Rmv;
            }
        }

        private Entity PrefabFor(MachineKind kind, RRWSetting settings, out float scale, out MachineKind actual)
        {
            scale = 1f;
            actual = kind;
            switch (kind)
            {
                case MachineKind.Excavator:
                {
                    Entity e = Entity.Null;
                    if (RRWPrefabRegistry.ExcavatorOk)
                    {
                        // Bucket dust: in game testing the bucket emitter rendered as a dark blob and its look on a
                        // digging bucket (below the trench edge, 0.4 root scale) cannot be guaranteed, so the product always
                        // spawns the Quiet clone and the dust heaps (Props) carry the dust. The dusty clone (Props' "RRW Dust
                        // VFX" when EffectsOk, else vanilla DustcloudSmallVFX) is spawned only for evaluation (dev rrw.mx.dust 1).
                        bool dust = settings.DustOn && MachineDebug.BucketDust && MachinePrefabSystem.DustyCloneOk;
                        // Props' per-load VFX slot check said the "RRW Dust VFX" clone is drawn through a wrong slot
                        // (black blobs): never show it on the bucket either (Core RRWPrefabRegistry.DustCloneState).
                        if (dust && MachinePrefabSystem.DustyCloneUsesRrwVfx && RRWPrefabRegistry.DustCloneState < 0)
                        {
                            dust = false;
                            RRWLog.Once("machines-dusty-clone-invalid", "machines: bucket dust requested but Props' RRW Dust VFX check failed this load: digger stays Quiet");
                        }
                        // the clone's dust only while Props publishes the clone source (DustVanilla false); with
                        // the vanilla source (default in every build) a clone registered with the RRW Dust VFX stays Quiet
                        if (dust && MachinePrefabSystem.DustyCloneUsesRrwVfx && RRWPrefabRegistry.DustVanilla)
                        {
                            dust = false;
                            RRWLog.Once("machines-dusty-clone-vanilla", "machines: bucket dust requested, but the dusty clone carries the RRW Dust VFX while Props uses the vanilla dust source (RRWPrefabRegistry.DustVanilla): digger stays Quiet");
                        }
                        e = PrefabCatalog.Car(m_PrefabSystem, dust ? PrefabNames.RoadExcavator : PrefabNames.RoadExcavatorQuiet);
                        if (e == Entity.Null) e = PrefabCatalog.Car(m_PrefabSystem, PrefabNames.RoadExcavatorQuiet);
                    }
                    if (e != Entity.Null) { scale = RRWConst.kExcavatorScale; return e; }
                    // fallback: the unscaled FrontendLoader01 digs (never the vanilla MiningExcavator01: big dust)
                    actual = MachineKind.LoaderDigger;
                    RRWLog.Once("machines-excavator-fallback", "machines: RRW Road Excavator clone unavailable: FrontendLoader01 digs instead");
                    return PrefabCatalog.Car(m_PrefabSystem, PrefabNames.Loader);
                }
                case MachineKind.Grader:
                {
                    // the Loader role is a second (dust-free) excavator that grades / spreads / strips with its bucket
                    Entity e = RRWPrefabRegistry.ExcavatorOk ? PrefabCatalog.Car(m_PrefabSystem, PrefabNames.RoadExcavatorQuiet) : Entity.Null;
                    if (e == Entity.Null && RRWPrefabRegistry.ExcavatorOk) e = PrefabCatalog.Car(m_PrefabSystem, PrefabNames.RoadExcavator);
                    if (e != Entity.Null) { scale = RRWConst.kExcavatorScale; return e; }
                    actual = MachineKind.Loader;
                    RRWLog.Once("machines-grader-fallback", "machines: RRW Road Excavator clone unavailable: the Loader role uses FrontendLoader01");
                    return PrefabCatalog.Car(m_PrefabSystem, PrefabNames.Loader);
                }
                case MachineKind.Loader: return PrefabCatalog.Car(m_PrefabSystem, PrefabNames.Loader);
                case MachineKind.Truck: return PrefabCatalog.Car(m_PrefabSystem, PrefabNames.DumpTruck);
                default: return PrefabCatalog.Car(m_PrefabSystem, PrefabNames.CrewTruck);
            }
        }

        public static void StartState(PlanContext c, Puppet p, MachineRole role, CrewSlot slot, bool atAnchor, out float u, out float lat, out sbyte facing)   // (public: offline tests)
        {
            facing = slot.Facing >= 0 ? (sbyte)1 : (sbyte)-1;
            if (p.IsTruck)
            {
                var cfg = Choreo.TruckConfig(c, p, role, slot);
                u = cfg.BaseU;
                lat = cfg.BaseLat;
                float stop = Choreo.WorkStopAt(c, cfg, c.Now);   // C3 feed: the hopper of the (clamped) paver
                // without K-turns the truck starts at its rolling base (no first reverse across the whole site)
                // an inline truck never waits closer to the excavator than the inline clearance (Choreo.InlineBaseCap)
                if (c.Narrow || !c.CanTurn) { u = Choreo.InlineBaseCap(c, p, cfg, Choreo.RollingBase(c, p, cfg, Choreo.ClampU(c, p, stop))); lat = cfg.WorkLat; }
                // park facing the way the first trip starts: away from the work when it is within reversing distance
                facing = math.abs(stop - u) <= RRWConst.kMaxReverseLeg - 2f || c.Narrow || !c.CanTurn ? (sbyte)-cfg.Dir : cfg.Dir;
                return;
            }
            float F = c.FrontAt(c.Now);
            float anchor;
            switch (slot.Activity)
            {
                case MachineActivity.Dig:
                case MachineActivity.Break: anchor = F - RRWConst.kExcavatorBack; break;
                case MachineActivity.Spread: anchor = slot.U - 7f; break;
                case MachineActivity.Scrape: anchor = F - 5f; break;
                case MachineActivity.Pave:
                case MachineActivity.Paint:
                case MachineActivity.Follow:
                case MachineActivity.Wait:
                    FollowParams(c, p, role, slot, out float off, out float lo, out float hi, out _, out var ret);
                    anchor = math.clamp(F + off, lo, hi);
                    if (ret.On) anchor = math.min(anchor, ret.Home + ret.Speed * (float)math.max(0.0, ret.By - c.Now));
                    break;
                default: anchor = Choreo.ShiftShared(c, slot.U); break;
            }
            anchor = Choreo.ClampU(c, p, anchor);
            if (!atAnchor)
            {
                // a role that starts now comes in from the site entry (the "gate") and drives to its anchor; when the anchor is
                // far down the chain it appears 40 m short of it instead of crossing the whole site. The
                // gate is a chain end that connects to the road network - the start side (facing +u), unless only the end
                // connects and the role can face back (K-turn possible, or its slot faces -u), then the end side (facing -u).
                // The lateral below lies in the stage's works half (LatOf / LatRange).
                bool fromEnd = !Choreo.EndConnected(c, false) && Choreo.EndConnected(c, true) && (c.CanTurn || slot.Facing < 0);
                if (!fromEnd)
                {
                    float entry = Choreo.ClampU(c, p, Choreo.ShiftShared(c, c.Trim0 + 2f + p.BoxHalfLen));
                    anchor = anchor - entry > 120f ? Choreo.ClampU(c, p, anchor - 40f) : math.min(anchor, entry);
                    // a crew after the first comes in at its own section start (never from another crew's section)
                    if (c.Crews > 1 && c.CrewIndex > 0) anchor = math.max(anchor, Choreo.ClampU(c, p, c.SecLo - MxConst.kSecSlack + p.BoxHalfLen));
                    facing = 1;
                }
                else
                {
                    float entry = Choreo.ClampU(c, p, Choreo.ShiftShared(c, c.Trim1 - 2f - p.BoxHalfLen));
                    anchor = entry - anchor > 120f ? Choreo.ClampU(c, p, anchor + 40f) : math.max(anchor, entry);
                    if (c.Crews > 1 && c.CrewIndex < c.Crews - 1) anchor = math.min(anchor, Choreo.ClampU(c, p, c.SecHi + MxConst.kSecSlack - p.BoxHalfLen));
                    facing = -1;
                }
            }
            u = anchor;
            bool dig = slot.Activity == MachineActivity.Dig || slot.Activity == MachineActivity.Break;
            float v = !c.LateralMetres && math.abs(slot.Lateral) >= 0.99f ? (slot.Lateral < 0f ? -0.45f : 0.45f) : slot.Lateral;
            lat = dig ? Choreo.DiggerLat(c, v, u, p) : Choreo.LatOf(c, v, u, p);
            if (c.Narrow && p.Kind == MachineKind.Excavator) lat = 0f;
        }

        // ------------------------------------------------------------ post-site

        // The puppet leaves its role for good. With a context it drives to the chain end
        // inside the trims in its own lane (Choreo.PlanLeave; endU = the DriveOut slot, else the end ahead of its nose) and
        // stops there; without one (project / track gone) it freezes where the current leg ends. Either way it is removed
        // when out of sight, and at the latest kPostSiteMaxSimFrames after this call (PerPuppet), even if visible. A puppet
        // that already leaves is never re-planned here.
        private void MakePostSite(EntityManager em, Puppet p, PlanContext c, double now, string why, float endU = float.NaN)
        {
            if (p.PostSite) return;
            MxPerf.Begin(MxT.X_PostSite);
            MxPerf.Count(MxC.PostSites);
            MakePostSiteCore(em, p, c, now, why, endU);
            MxPerf.End(MxT.X_PostSite);
        }

        private void MakePostSiteCore(EntityManager em, Puppet p, PlanContext c, double now, string why, float endU)
        {
            string where = "";
            p.LeaveCap = RRWConst.kPostSiteMaxSeconds;
            p.LeaveContinue = false;
            p.LeaveVmax = -1f;   // no convoy cap on a fresh drive-off
            p.ConvoyLeader = null;
            p.ConvoyDepart = double.NaN;
            p.Outgoing = true;   // the leave plan uses the leaving speed row (VLeave of the role's last working phase)
            p.OutAttempt = p.RetAttempt = -1;
            if (c != null && p.Plan.Count > 0)
            {
                try
                {
                    MachineMotion.State(p.Plan, c.Clk, now, out var s);
                    // a connected chain end (never a dead end when the other connects); camera-far when both do.
                    // with several crews, the exit crossing fewer working crews first
                    float end = Choreo.ChooseExit(c, s.U, s.Hu >= 0f ? (sbyte)1 : (sbyte)-1, endU, p, out string exitWhy);
                    // never head-on through the working crew to reach the only connected end
                    end = Choreo.YieldToCrew(c, p, s.U, s.Lat, end, ref exitWhy);
                    // never reverse hundreds of metres - no K-turn of any size fits and the exit lies far behind: the end ahead
                    end = Choreo.NoTurnAhead(c, p, s.U, s.Lat, s.Hu >= 0f ? (sbyte)1 : (sbyte)-1, end, ref exitWhy);
                    p.LeaveEndU = end;
                    p.ExitWhy = exitWhy;
                    p.OtherExitTried = false;
                    Choreo.PlanLeave(p, c, end, out where, out bool more);
                    p.LeaveContinue = more;
                    // the leave cap covers the whole route at the role's leaving limits (interior crews)
                    p.LeaveCap = Choreo.LeaveNeedSeconds(p, c, s.U, s.Hu >= 0f ? (sbyte)1 : (sbyte)-1, end, now);
                    if (p.LeaveCap > RRWConst.kPostSiteMaxSeconds + 0.5) { MachineDebug.LongLeaveCaps++; where += " cap " + RRWLog.F((float)p.LeaveCap) + " s"; }
                    where += " exit u=" + RRWLog.F(end) + " (" + exitWhy + ")";
                    // a stop at a chain end that is not a connected exit, or a C4 Vacate leaver that cannot reach one soon
                    ClassifyDeadEnd(p, c, s.U, s.Hu >= 0f ? (sbyte)1 : (sbyte)-1, end);
                    UwLeaveEnd(p, c, end);
                    if (p.DeadEndLeave) where += " DEAD-END(" + p.DeadEndWhy + ")";
                }
                catch (Exception e) { RRWLog.ErrorOnce("machines leave plan", e); }
            }
            else if (p.Plan.Count > 0)
            {
                // project gone: stand where the current leg ends
                var plan = p.Plan;
                double tOld = now - plan.Epoch;
                int i = MachineMotion.ActiveLeg(plan, tOld);
                MachineMotion.State(plan, MachineRegistry.Clock, now, out var s);
                var hold = new MachineLeg { T0 = (float)tOld, T1 = float.PositiveInfinity, Kind = (byte)LegKind.Hold, Facing = s.Hu >= 0f ? (sbyte)1 : (sbyte)-1, U0 = s.U, L0 = s.Lat, Odo0 = s.Odo, Front = 0 };
                var cur = plan.Get(i);
                if (cur.Kind == (byte)LegKind.Hold) { cur.T1 = float.PositiveInfinity; plan.Set(i, cur); plan.Count = i + 1; }
                else if (i + 1 < MachinePlan.MaxLegs)
                {
                    // freeze: everything front-relative stops; drives/turns are cut at the current state
                    cur.T1 = (float)tOld;
                    plan.Set(i, cur);
                    plan.Set(i + 1, hold);
                    plan.Count = i + 2;
                }
                plan.FrontA.Model.PPerFrame = 0f;
                plan.FrontB.Model.PPerFrame = 0f;
                p.Plan = plan;
                p.PlanDirty = true;
                where = "frozen (no project track)";
            }
            if (c == null || p.Plan.Count <= 0) p.DeadEndLeave = false;
            p.PostSite = true;
            p.Outgoing = true;
            p.PostSiteSince = RRWClock.UpdateIndex;
            p.LeaveSince = math.max(1u, RRWClock.UpdateIndex);
            p.LeaveTau = now;
            p.LeaveWhy = why ?? "";
            if (!p.LeaveContinue) p.ReplanAt = double.PositiveInfinity;   // a chunked reverse re-plans its next part (RetryLeavers)
            p.Loads.Clear();
            p.Feeding = false;
            p.ZoneEnter = p.ZoneExit = double.NegativeInfinity;
            Choreo.ClearBlocked(p);
            if (MachineRegistry.States.TryGetValue(p.ProjectId, out var st)) st.Detach(p);
            if (p.Alive(em)) PuppetFactory.Retag(em, p.E, p.ProjectId, DerivedGroup.PostSite, DerivedGroup.PostSite);
            MachineDebug.Leaves++;
            if (MachineDebug.LogDespawns || RRWLog.VerboseEnabled)
            {
                string msg = "machines: " + p + " leaves (" + why + "): " + where;
                if (MachineDebug.LogDespawns) RRWLog.Info(msg);
                else RRWLog.Verbose(msg);
            }
        }

        // The leave stop of p is a DEAD END when the exits are known (at least one end connects) and the
        // end it heads for does not connect (YieldToCrew / LeaverOtherEnd / dead-end chains), or when a C4 Vacate runs and p needs
        // longer than kDeadEndVacateSeconds to a connected exit. Such a leaver is removed out of view or kDeadEndParkRemoveSeconds after
        // it stopped (PerPuppet). Exits not computed yet never count (both ends count as exits); an isolated road (ProjectView.ExitsKnown with
        // neither end connected) makes every leave stop a dead end.
        internal static void ClassifyDeadEnd(Puppet p, PlanContext c, float fromU, sbyte facing, float end)
        {
            p.DeadEndLeave = false;
            p.DeadEndWhy = "";
            var v = c.View;
            if (!v.ExitAtStart && !v.ExitAtEnd)
            {
                // An isolated road (exits computed, neither end connects): every leave stop is a dead end
                if (!v.ExitsKnown) return;
                p.DeadEndLeave = true;
                p.DeadEndWhy = "isolated road: neither chain end connects";
                return;
            }
            bool atEnd = math.abs(end - c.Trim1) < math.abs(end - c.Trim0);
            if (!Choreo.EndConnected(c, atEnd))
            {
                p.DeadEndLeave = true;
                p.DeadEndWhy = "stop at the " + (atEnd ? "chain end" : "chain start") + ", not a connected exit";
                return;
            }
            if (c.Project != null && c.Project.Switch == StageSwitch.Vacate)
            {
                var lim = p.Lim;
                float t = MachineLimits.MinLegSeconds(end - fromU, math.max(0.1f, lim.VFor((end - fromU) * facing < 0f, false, true) * MxConst.kSpeedMargin), math.max(0.05f, lim.Accel));
                if (t > MxConst.kDeadEndVacateSeconds)
                {
                    p.DeadEndLeave = true;
                    p.DeadEndWhy = "C4 Vacate: " + RRWLog.F(t) + " s to the connected exit";
                }
            }
        }

        // ------------------------------------------------------------ per puppet

        private void PerPuppet(EntityManager em, Puppet p, double now, float3 cam, float daylight, RRWSetting settings)
        {
            if (!p.Alive(em)) return;
            uint ui = RRWClock.UpdateIndex;
            uint dt = ui - p.LastCullCheck;
            if (p.IsRoller)
            {
                // A roller is not an entity: its position from the plan pose, "out of view" from the camera frustum
                // (GeometryUtility planes of this update) and the distance - never Renderer.isVisible (shadow cascades set it)
                MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var rs);
                if (MachineMotion.Pose(p.Plan, MachineTrackStore.View(), rs, out float3 rpos, out _)) { p.LastPos = rpos; p.HasPos = true; }
                p.CamDist = math.any(math.isnan(cam)) ? 0f : math.distance(p.LastPos.xz, cam.xz);
                if (dt >= 4u)
                {
                    p.LastCullCheck = ui;
                    bool inFrustum = RollerView.InFrustum(p.LastPos);
                    p.CulledUpdates = !inFrustum ? p.CulledUpdates + (int)math.min(dt, 64u) : 0;
                }
                p.InView = p.CamDist <= RRWConst.kMachineDespawnRadius && p.CulledUpdates < RRWConst.kMachineCulledUpdates;
            }
            else
            {
                // position + visibility (machine LOD)
                MxPerf.Begin(MxT.D_ReadTransform);
                if (em.HasComponent<ObjTransform>(p.E)) { p.LastPos = em.GetComponentData<ObjTransform>(p.E).m_Position; p.HasPos = true; }
                MxPerf.End(MxT.D_ReadTransform);
                p.CamDist = math.any(math.isnan(cam)) ? 0f : math.distance(p.LastPos.xz, cam.xz);
                if (dt >= 4u)
                {
                    p.LastCullCheck = ui;
                    MxPerf.Begin(MxT.D_ReadTransform);
                    bool culled = em.HasComponent<CullingInfo>(p.E) && em.GetComponentData<CullingInfo>(p.E).m_CullingIndex == 0;
                    MxPerf.End(MxT.D_ReadTransform);
                    p.CulledUpdates = culled ? p.CulledUpdates + (int)math.min(dt, 64u) : 0;
                }
            }
            bool outOfSight = p.CamDist > RRWConst.kMachineDespawnRadius || p.CulledUpdates >= RRWConst.kMachineCulledUpdates;
            bool projectAllows = false;
            if (SiteRegistry.TryGetProject(p.ProjectId, out var pr))
                projectAllows = settings.MachinesOn && pr.AllowMachines && ModeHasMachines(pr);
            // A puppet of a LOD-out crew (beyond kMaxActiveCrewsGlobal / far) goes like a leaver: out of sight only
            bool candidate = p.PostSite || p.Outgoing || !projectAllows || p.LodOut;
            if (candidate && outOfSight && ui - p.SpawnUpdate > 30u)
            {
                // The normal end of a leaver - removed once out of the camera's view (counted for rrw.mx.check)
                if (p.PostSite || p.Outgoing)
                {
                    if (p.CamDist > RRWConst.kMachineDespawnRadius) MachineDebug.FarRemovals++;
                    else MachineDebug.CulledRemovals++;
                    if (p.DeadEndLeave) MachineDebug.DeadEndRemovalsCulled++;   // dead-end leaver
                }
                Despawn(em, p, "out of sight dist=" + RRWLog.F(p.CamDist) + " culled=" + p.CulledUpdates + (p.PostSite ? " post-site" : p.Outgoing ? " outgoing" : p.LodOut ? " crew LOD" : " not allowed"));
                return;
            }
            // A leaving puppet is gone at the latest kPostSiteMaxSimFrames after it began
            // leaving (or after the project began releasing the road), EVEN IF VISIBLE - no machine stays parked on a road
            // Measured on the MACHINE clock (game time: frozen while paused, follows the game speed, independent of the frame
            // rate), so a leaver never vanishes while the game is paused and always gets its full drive-out time.
            // A leaver standing at a chain end that is NOT a connected exit (or a C4 Vacate leaver far from
            // one) is removed kDeadEndParkRemoveSeconds machine s after it stopped there, EVEN IF VISIBLE - never at the full leave cap
            if (p.PostSite && p.DeadEndLeave && !p.LeaveContinue && p.Plan.Count > 0)
            {
                double stop = MachineMotion.MotionEnd(p.Plan);
                if (!double.IsNaN(stop) && !double.IsInfinity(stop) && now >= stop + RRWConst.kDeadEndParkRemoveSeconds && !p.GuardHeld)
                {
                    MachineDebug.DeadEndRemovalsVisible++;
                    MachineDebug.LastDeadEnd = p + " (" + p.DeadEndWhy + ") stood " + RRWLog.F((float)(now - stop)) + " s dist=" + RRWLog.F(p.CamDist);
                    RRWLog.Info("machines: dead-end leaver removed " + MachineDebug.LastDeadEnd);
                    Despawn(em, p, "dead-end leaver (" + p.DeadEndWhy + ")");
                    return;
                }
            }
            // A leaver that reached its CONNECTED exit drives off the site into the road network: removed kExitParkRemoveSeconds
            // machine s after it stopped there, even if visible. Otherwise it would stand there until out of view or the 120 s leave cap, holding
            // the release gate (completion) / the C4 Vacate -> Swap step (MachineZones) all that time. This applies to every
            // leaver (not only at release), and only the FIRST in the exit queue (nothing standing between it and the exit end); the
            // queue then moves up (AdvanceExitQueue: the others re-plan their drive-off to the freed slots).
            if (p.PostSite && !p.DeadEndLeave && !p.LeaveContinue && pr != null && p.Plan.Count > 0 && !p.GuardHeld && !p.StandStill &&
                AtConnectedExit(p, pr) && ExitAhead(p, now) == null)
            {
                double stop = MachineMotion.MotionEnd(p.Plan);
                if (!double.IsNaN(stop) && !double.IsInfinity(stop) && now >= stop + MxConst.kExitParkRemoveSeconds)
                {
                    if (outOfSight) MachineDebug.ExitRemovalsCulled++; else MachineDebug.ExitRemovalsVisible++;
                    RRWLog.Info("machines: " + p + " drove off through the exit u=" + RRWLog.F(p.LeaveEndU) + " (" + (pr.Releasing ? "release" :
                                pr.Switch == StageSwitch.Vacate ? "C4 vacate" : "leave") + "; stood " + RRWLog.F((float)(now - stop)) + " s, dist=" + RRWLog.F(p.CamDist) + ")");
                    float exitU = p.LeaveEndU;
                    uint pid = p.ProjectId;
                    Despawn(em, p, "left through the exit");
                    AdvanceExitQueue(pid, exitU, now);
                    return;
                }
            }
            // A roller of upgrade works belongs to its band's compaction (base course, fresh asphalt, backfill). Once it leaves (the
            // band's phase or window ended) it is gone as soon as its drive-off stopped, wherever that is, and at the latest
            // kUwRollerLeaveSeconds after it began leaving, even in view: it never stays on the site into the next window (the
            // re-marking crew works there next). FullDig rollers keep the leave rules above and below.
            if (p.IsRoller && p.Upgrade && p.PostSite && p.Plan.Count > 0 && !double.IsNaN(p.LeaveTau))
            {
                double stop = MachineMotion.MotionEnd(p.Plan);
                bool stopped = !p.LeaveContinue && !p.GuardHeld && !double.IsNaN(stop) && !double.IsInfinity(stop) &&
                               now >= stop + MxConst.kExitParkRemoveSeconds;
                bool late = now - p.LeaveTau >= MxConst.kUwRollerLeaveSeconds;
                if (stopped || late)
                {
                    MxUpgradeStats.RollerRemovals++;
                    string why = "upgrade works roller left its band (" + p.LeaveWhy + "): " + (stopped ? "drive-off stopped" : "leaving for " +
                                 RRWLog.F((float)(now - p.LeaveTau)) + " s") + (outOfSight ? "" : ", in view") + " dist=" + RRWLog.F(p.CamDist);
                    RRWLog.Info("machines: " + p + " removed: " + why);
                    Despawn(em, p, why);
                    return;
                }
            }
            double leaveOver = LeaveOver(p, pr, now, out double leaveAge, out double leaveCap);
            if (leaveOver >= 0.0)
            {
                // The LAST RESORT (leavers normally go once culled / far, above): counted and logged
                MachineDebug.CapDespawns++;
                if (!outOfSight) { MachineDebug.VisibleCapRemovals++; MachineDebug.LastVisibleCapUpdate = math.max(1u, ui); MachineDebug.LastVisibleCap = p + " (" + p.LeaveWhy + ")"; }
                string why = "cap: leaving for " + RRWLog.F((float)leaveAge) + " s (" + p.LeaveWhy + ") dist=" + RRWLog.F(p.CamDist) + (outOfSight ? "" : " VISIBLE") +
                             " exit u=" + RRWLog.F(p.LeaveEndU) + " (" + p.ExitWhy + ")";
                RRWLog.Info("machines: " + (outOfSight ? "cap removal " : "visible cap removal ") + p + " at the " + RRWLog.F((float)leaveCap) +
                            " s leave cap (" + why + ")");
                Despawn(em, p, why);
                return;
            }

            if (p.IsRoller)
            {
                // Lights for the GameObject (the render system swaps shared materials on a change); no ECS writes
                if (((ui + p.Seed) & 7u) == 0u || p.PlanDirty)
                {
                    p.Plan.Flags = LightFlags(p, now, daylight, settings);
                    p.PlanDirty = false;
                }
                return;
            }

            // truck loads; a different resource respawns the truck in place
            if (p.IsTruck && p.Loads.Count > 0)
            {
                MxPerf.Begin(MxT.D_Loads);
                int idx = -1;
                for (int i = 0; i < p.Loads.Count; i++) if (p.Loads[i].Tau <= now) idx = i;
                if (idx >= 0)
                {
                    var step = p.Loads[idx];
                    LoadKind want = step.Kind == LoadKind.None ? p.Load : step.Kind;
                    if (want != p.Load && p.Load != LoadKind.None) Respawn(em, p, want, step.Pct, now);
                    else if (step.Pct != p.AmountPct) PuppetFactory.SetAmount(em, p, step.Pct);
                }
                MxPerf.End(MxT.D_Loads);
            }

            // lights
            if (((ui + p.Seed) & 7u) == 0u || p.PlanDirty)
            {
                var flags = LightFlags(p, now, daylight, settings);
                if (flags != p.Plan.Flags)
                {
                    p.Plan.Flags = flags;
                    p.PlanDirty = true;
                    if (!em.HasComponent<EffectsUpdated>(p.E))
                    {
                        MxPerf.Begin(MxT.X_EcsStructural);
                        em.AddComponent<EffectsUpdated>(p.E);
                        MxPerf.End(MxT.X_EcsStructural);
                        MxPerf.Count(MxC.EcsEffectsUpdated);
                    }
                }
            }

            if (p.PlanDirty)
            {
                MxPerf.Begin(MxT.D_PlanWrite);
                em.SetComponentData(p.E, p.Plan);
                MxPerf.End(MxT.D_PlanWrite);
                MxPerf.Count(MxC.PlanWrites);
                p.PlanDirty = false;
            }
        }

        // The puppet of the same project standing between leaver p's stop and its exit end in p's lane band (the
        // exit queue ahead of it, or a machine parked at the end barriers); null = the way out is clear.
        public static Puppet ExitAhead(Puppet p, double now)
        {
            if (p.Plan.Count <= 0 || float.IsNaN(p.LeaveEndU)) return null;
            var last = p.Plan.Get(p.Plan.Count - 1);
            float u = last.U0, lat = last.L0;
            float dir = p.LeaveEndU >= u ? 1f : -1f;
            var clk = MachineRegistry.Clock;
            foreach (var q in MachineRegistry.All)
            {
                if (q == p || q.ProjectId != p.ProjectId || q.Plan.Count <= 0) continue;
                MachineMotion.State(q.Plan, clk, now, out var s);
                float d = (s.U - u) * dir;
                if (d <= 0.5f || (p.LeaveEndU - s.U) * dir < -(q.BoxHalfLen + math.abs(q.BoxOffZ))) continue;   // behind p / beyond the exit
                if (math.abs(s.Lat - lat) >= p.BoxHalfWid + q.BoxHalfWid + 2f * MxConst.kOverlapMargin) continue;   // another lane
                return q;
            }
            return null;
        }

        // A leaver left through exit `exitU`: the leavers of the project queued for the same exit (standing at the end
        // of their drive-off) re-plan it (RetryLeavers, conflict-scanned) and move up to the freed slots - the exit queue drains.
        public static void AdvanceExitQueue(uint projectId, float exitU, double now)   // (public: offline tests)
        {
            foreach (var q in MachineRegistry.All)
            {
                if (q.ProjectId != projectId || !q.PostSite || q.LeaveContinue || float.IsNaN(q.LeaveEndU) || math.abs(q.LeaveEndU - exitU) > 0.5f) continue;
                if (q.Plan.Count <= 0 || MachineMotion.MotionEnd(q.Plan) > now) continue;   // still driving: it takes its slot as planned
                q.LeaveContinue = true;
                q.ReplanAt = now;
                MachineDebug.ExitQueueAdvances++;
            }
        }

        // The leaver's committed plan ends standing (open-ended hold) within kExitReach of its exit end, and that end connects
        // to the road network (exits not computed yet: both count; an isolated road is a dead end: never).
        public static bool AtConnectedExit(Puppet p, ProjectRecord pr)   // (public: offline tests)
        {
            if (float.IsNaN(p.LeaveEndU) || p.Plan.Count <= 0) return false;
            var last = p.Plan.Get(p.Plan.Count - 1);
            if (last.Kind != (byte)LegKind.Hold || !last.OpenEnded) return false;
            var v = pr.View();
            bool atEnd = math.abs(p.LeaveEndU - v.Trim1) < math.abs(p.LeaveEndU - v.Trim0);
            if (math.abs(p.LeaveEndU - (atEnd ? v.Trim1 : v.Trim0)) > 1f) return false;
            bool connected = !v.ExitAtStart && !v.ExitAtEnd ? !v.ExitsKnown : (atEnd ? v.ExitAtEnd : v.ExitAtStart);
            return connected && math.abs(last.U0 - p.LeaveEndU) <= MxConst.kExitReach;
        }

        private TransformFlags LightFlags(Puppet p, double now, float daylight, RRWSetting settings)
        {
            if (p.PostSite && RRWClock.UpdateIndex - p.PostSiteSince > (uint)MxConst.kPostSiteParkUpdates)
            {
                if (!p.LightsOff) { p.LightsOff = true; RRWLog.Verbose("machines: " + p + " parked: engine idle, lights off"); }
                return 0;
            }
            TransformFlags f = TransformFlags.RearLights;
            bool dark = daylight + p.LightJitter < 0.5f;
            if (dark) f |= TransformFlags.MainLights | TransformFlags.WorkLights;
            if (p.Kind == MachineKind.Rmv && !p.PostSite) f |= TransformFlags.WarningLights;
            // The roller's beacon while on site when BeaconsOn (like the crew truck's warning lights)
            if (p.IsRoller && !p.PostSite && settings != null && settings.BeaconsOn) f |= TransformFlags.WarningLights;
            if (p.Role == MachineRole.Finisher && p.Activity == MachineActivity.Paint && !p.PostSite) f |= ArrowBoard(p, now);
            if (p.SigOverride >= 0)
            {
                // dev rrw.mx.sig: force the arrow-board bits on any puppet
                f &= ~(TransformFlags.SignalAnimation1 | TransformFlags.SignalAnimation2);
                if ((p.SigOverride & 1) != 0) f |= TransformFlags.SignalAnimation1;
                if ((p.SigOverride & 2) != 0) f |= TransformFlags.SignalAnimation2;
            }
            return f;
        }

        // The painter's arrow board points traffic to the side it may pass on.
        // Vanilla (game code, MaintenanceVehicleAISystem.GetWorkingFlags): a truck on a RightLimit lane (works on its right,
        // traffic passes on its left) shows SignalAnimation1, on a LeftLimit lane SignalAnimation2 - geometric, NOT mirrored for
        // left-hand traffic (only the no-limit default is). So Auto = SignalAnimation1 when the stage's works half lies on the
        // painter's right (chain-right works half and facing +u, or chain-left and facing -u), else SignalAnimation2; left-hand
        // traffic needs no extra flip (the works half is a chain-frame fact). RRWGates.ArrowBoard: Flip / Off / Both override.
        internal static TransformFlags ArrowBoard(Puppet p, double now)
        {
            var mode = RRWGates.ArrowBoard;
            if (mode == ArrowBoardMode.Off) return 0;
            if (mode == ArrowBoardMode.Both) return TransformFlags.SignalAnimation1 | TransformFlags.SignalAnimation2;
            bool anim1 = ArrowWorksOnRight(p, now);
            if (mode == ArrowBoardMode.Flip) anim1 = !anim1;
            return anim1 ? TransformFlags.SignalAnimation1 : TransformFlags.SignalAnimation2;
        }

        internal static bool ArrowWorksOnRight(Puppet p, double now)
        {
            MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
            bool facingPlus = s.Hu >= 0f;
            bool worksRight = s.Lat >= 0f;
            if (SiteRegistry.TryGetProject(p.ProjectId, out var pr))
            {
                var works = PhasePlan.Stage(pr.View()).Works & RoadZones.Carriageway;
                if (works == RoadZones.RightHalf) worksRight = true;
                else if (works == RoadZones.LeftHalf) worksRight = false;
            }
            return worksRight == facingPlus;
        }

        private void Respawn(EntityManager em, Puppet p, LoadKind kind, int pct, double now)
        {
            var tv = MachineTrackStore.View();
            MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
            if (!MachineMotion.Pose(p.Plan, tv, s, out float3 pos, out quaternion rot))
            {
                if (!em.HasComponent<ObjTransform>(p.E)) return;
                var t = em.GetComponentData<ObjTransform>(p.E);
                pos = t.m_Position; rot = t.m_Rotation;
            }
            Entity old = p.E;
            Entity e = PuppetFactory.Create(em, p, p.Plan, pos, rot, p.ExcavatorRig, kind, pct);
            if (e == Entity.Null) { PuppetFactory.SetAmount(em, p, pct); return; }
            PuppetFactory.Delete(em, old);
            p.E = e;
            p.PlanDirty = false;
            MachineRegistry.Rekey(p, old);
            MachineRegistry.Respawned++;
            MxPerf.Count(MxC.Respawns);
            if (p.PostSite) PuppetFactory.Retag(em, e, p.ProjectId, DerivedGroup.PostSite, DerivedGroup.PostSite);
            RRWLog.Verbose("machines: " + p + " respawned with " + kind + " " + pct + "%");
        }

        private void Despawn(EntityManager em, Puppet p, string why)
        {
            if (p.Alive(em)) PuppetFactory.Delete(em, p.E);
            MachineRegistry.Remove(p);
            MachineRegistry.Despawned++;
            MxPerf.Count(MxC.Despawns);
            if (MachineDebug.LogDespawns) RRWLog.Info("machines: despawn " + p + " (" + why + ")");
            else RRWLog.Verbose("machines: despawn " + p + " (" + why + ")");
        }

        // Global budget: when over kMaxMachinesGlobal, the oldest post-site puppet that is out of sight goes first.
        private void BudgetPressure(EntityManager em)
        {
            if (MachineRegistry.All.Count <= RRWConst.kMaxMachinesGlobal) return;
            EvictOne(em);
        }

        private bool EvictOne(EntityManager em)
        {
            Puppet best = null;
            foreach (var p in MachineRegistry.All)
            {
                if (!p.PostSite && !p.Outgoing) continue;
                bool outOfSight = p.CamDist > RRWConst.kMachineDespawnRadius || p.CulledUpdates >= RRWConst.kMachineCulledUpdates;
                if (!outOfSight) continue;
                if (best == null || p.PostSiteSince < best.PostSiteSince) best = p;
            }
            if (best == null) return false;
            Despawn(em, best, "global budget");
            return true;
        }

        // ------------------------------------------------------------ introspection (rrw.dump / rrw.check)

        private string Dump(EntityManager em, Entity edge)
        {
            if (!SiteRegistry.TryGetEdge(edge, out var r)) return "no record";
            var sb = new StringBuilder();
            sb.Append("machines project p").Append(r.ProjectId);
            if (SiteRegistry.TryGetProject(r.ProjectId, out var prj))
            {
                uint ui = RRWClock.UpdateIndex;
                sb.Append(" report: zones=").Append(prj.MachineZones).Append(" onCarriageway=").Append(prj.MachinesOnCarriageway)
                  .Append(" age=").Append(prj.MachinesReportUpdate == 0 ? "never" : unchecked((int)(ui - prj.MachinesReportUpdate)).ToString())
                  .Append(prj.MachinesReportFresh(ui) ? " fresh" : " STALE")
                  .Append(" openLanes=").Append(prj.OpenLanes).Append(" releaseSince=").Append(prj.ReleaseSince)
                  .Append(prj.Releasing ? " (releasing " + unchecked((int)(ui - prj.ReleaseSince)) + " updates)" : "")
                  .Append(" ready=").Append(RoadZoneMath.Describe(prj.WorkZonesReady)).Append(MachineDebug.LegacyReady ? "(LEGACY)" : "")
                  .Append(" switch=").Append(prj.Switch).Append(" stuck=").Append(prj.MachinesStuck)
                  .Append(" exits=").Append(prj.ExitAtStart ? "S" : "-").Append(prj.ExitAtEnd ? "E" : "-")
                  .Append(" arrow=").Append(RRWGates.ArrowBoard);
            }
            if (MachineRegistry.States.TryGetValue(r.ProjectId, out var st))
            {
                if (st.GateWhy.Length > 0) sb.Append(" gate=").Append(st.GateWhy);
                double now = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
                ProjectView view = default;
                bool havePr = SiteRegistry.TryGetProject(r.ProjectId, out var prr);
                if (havePr) view = prr.View();
                int nCrews = havePr ? view.CrewCount : 1;
                for (int k = 0; k < st.Crews.Length; k++)
                {
                    var cs = st.Crews[k];
                    if (k >= nCrews && !cs.Any()) continue;
                    // one line per crew (section, front, LOD, dig grid)
                    var cp = havePr && k < nCrews ? PhasePlan.Crew(view, k) : default;
                    sb.Append("\n  crew ").Append(k).Append('/').Append(nCrews).Append(" sec=[").Append(RRWLog.F(cp.SecLo)).Append(',').Append(RRWLog.F(cp.SecHi)).Append(']')
                      .Append(" front=").Append(havePr && k < nCrews ? RRWLog.F(PhasePlan.CrewFront(view, k)) : "-")
                      .Append(" v=").Append(havePr ? RRWLog.F(PhasePlan.CrewFrontSpeed(view)) : "-")
                      .Append(" spawnOk=").Append(cs.SpawnOk).Append(" lodOut=").Append(cs.LodOut).Append(" cam=").Append(RRWLog.F(cs.CamDist))
                      .Append(" live=").Append(cs.Live).Append(" anchorMask=").Append(cs.AnchorMask)
                      .Append(" digOrigin=").Append(double.IsNaN(cs.DigOrigin) ? "nan" : RRWLog.F((float)(cs.DigOrigin % 100000.0)))
                      .Append(" hop=").Append(RRWLog.F(cs.DigHopStart)).Append('+').Append(RRWLog.F(cs.DigHopDur)).Append("s rate=").Append(RRWLog.F(cs.DigRate));
                }
                // roller slots / units per crew and the IK dig state of the excavators
                for (int k = 0; k < st.Crews.Length && havePr; k++)
                {
                    if (k >= nCrews) break;
                    var cp = PhasePlan.Crew(view, k);
                    for (int i = 0; i < RRWConst.kMaxRollersPerCrew; i++)
                    {
                        var rs = cp.Roller(i);
                        var ru = st.Crews[k].Rollers[i];
                        if (!rs.Active && ru == null) continue;
                        sb.Append("\n  c").Append(k).Append(" roller").Append(i).Append(' ').Append(rs.Slot.Activity).Append(' ').Append(rs.Duty)
                          .Append(" window=[").Append(RRWLog.F(rs.WinLo)).Append(',').Append(RRWLog.F(rs.WinHi)).Append(']')
                          .Append(rs.RelayOnly ? " RELAY-ONLY" : "").Append(rs.WorksHalfOnly ? " works-half" : "").Append(" anchor=").Append(RRWLog.F(rs.Slot.U))
                          .Append(" unit=").Append(ru != null ? ru.ToString() + (ru.InView ? " in view" : " out of view") + " cam=" + RRWLog.F(ru.CamDist) : "none")
                          .Append(" defer=").Append(double.IsNaN(st.Crews[k].RollerDeferSince[i]) ? "-" : RRWLog.F((float)(now - st.Crews[k].RollerDeferSince[i])) + "s");
                    }
                    var ex = st.Crews[k].Roles[(int)MachineRole.Excavator];
                    if (ex != null && ex.Dig != null && ex.Dig.Cur != null)
                        sb.Append("\n  c").Append(k).Append(" dig ").Append(ex.Dig.Cur.SummaryText()).Append(" events(breakouts=").Append(ex.Dig.Breakouts)
                          .Append(" dumps=").Append(ex.Dig.Dumps).Append(" truck=").Append(ex.Dig.TruckDumps).Append(" spoil=").Append(ex.Dig.SpoilDumps).Append(')');
                    else if (st.Crews[k].DigC > 0f)
                        sb.Append("\n  c").Append(k).Append(" dig grid C=").Append(RRWLog.F(st.Crews[k].DigC)).Append("s ik=").Append(st.Crews[k].DigIk);
                }
                for (int k = 0; k < st.Crews.Length; k++)
                foreach (var p in st.Crews[k].Roles)
                {
                    if (p == null) continue;
                    MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
                    sb.Append("\n  c").Append(p.Crew).Append(' ').Append(p.Role).Append(' ').Append(p.Kind).Append(" e=").Append(RRWLog.E(p.E))
                      .Append(" act=").Append(p.Activity).Append(" leg=").Append(s.Leg).Append('/').Append(p.Plan.Count).Append(':').Append((LegKind)s.LegKind)
                      .Append(" u=").Append(RRWLog.F(s.U)).Append(" lat=").Append(RRWLog.F(s.Lat)).Append(" v=").Append(RRWLog.F(s.Speed))
                      .Append(" stage=").Append(p.Stage).Append(" load=").Append(p.Load).Append(' ').Append(p.AmountPct).Append('%')
                      .Append(" cam=").Append(RRWLog.F(p.CamDist)).Append(" culled=").Append(p.CulledUpdates)
                      .Append(" foot=").Append(p.FootSource).Append(p.Yielding ? " YIELDING" : "")
                      .Append(" zones=").Append(p.ZonesNow).Append('/').Append(p.ZonesPlan)
                      .Append(p.GuardHeld ? " GUARD-HELD(" + p.GuardBlocker + ")" : "").Append(p.StandStill ? " STAND-STILL(" + p.GuardBlocker + ")" : "")
                      .Append(p.Stuck ? " STUCK(" + RRWLog.F((float)(now - p.StuckSince)) + "s ep=" + p.StuckEpisodes + ")" : "")
                      .Append(" vmax=").Append(RRWLog.F(p.MaxV)).Append('/').Append(RRWLog.F(p.Lim.VFwd)).Append(" amax=").Append(RRWLog.F(p.MaxA)).Append('/').Append(RRWLog.F(p.Lim.Accel))
                      .Append(p.ViolV + p.ViolA > 0 ? " VIOLATIONS(v=" + p.ViolV + " a=" + p.ViolA + ")" : "");
                }
            }
            int post = 0;
            foreach (var p in MachineRegistry.All)
            {
                if (!p.PostSite) continue;
                post++;
                if (p.ProjectId != r.ProjectId) continue;
                double now2 = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
                MachineMotion.State(p.Plan, MachineRegistry.Clock, now2, out var s2);
                sb.Append("\n  leaving ").Append(p.Role).Append(' ').Append(p.Kind).Append(" e=").Append(RRWLog.E(p.E)).Append(" why=").Append(p.LeaveWhy)
                  .Append(" for=").Append(p.LeaveSince == 0 ? "?" : unchecked((int)(RRWClock.UpdateIndex - p.LeaveSince)).ToString()).Append(" updates")
                  .Append(" u=").Append(RRWLog.F(s2.U)).Append(" lat=").Append(RRWLog.F(s2.Lat)).Append(" v=").Append(RRWLog.F(s2.Speed))
                  .Append(" zones=").Append(p.ZonesNow).Append('/').Append(p.ZonesPlan).Append(" cam=").Append(RRWLog.F(p.CamDist))
                  .Append(" exit=").Append(RRWLog.F(p.LeaveEndU)).Append(" (").Append(p.ExitWhy).Append(')')
                  .Append(p.GuardHeld ? " GUARD-HELD" : "").Append(p.Stuck ? " STUCK" : "");
            }
            sb.Append("\n  global puppets=").Append(MachineRegistry.All.Count).Append(" postSite=").Append(post)
              .Append(" guardYields=").Append(MachineDebug.GuardYields).Append(" guardFailures=").Append(MachineDebug.GuardFailures)
              .Append(" sepGuard brakes=").Append(MachineDebug.GuardBrakes).Append(" contacts=").Append(MachineDebug.GuardContacts)
              .Append(" backOuts=").Append(MachineDebug.GuardBackOuts).Append(" deadlocks=").Append(MachineDebug.GuardDeadlocks)
              .Append(" standStills=").Append(MachineDebug.StandStills).Append(" leaves=").Append(MachineDebug.Leaves)
              .Append(" capDespawns=").Append(MachineDebug.CapDespawns).Append(" visibleCap=").Append(MachineDebug.VisibleCapRemovals)
              .Append(" stuckNow=").Append(MachineDebug.StuckNow).Append(" stuckEpisodes=").Append(MachineDebug.StuckEpisodes)
              .Append("\n  round4 crewsActive=").Append(MachineDebug.CrewsActive).Append(" crewsLodOut=").Append(MachineDebug.CrewsLodOut)
              .Append(' ').Append(MxSpeed.Summary());
            return sb.ToString();
        }

        private void Check(EntityManager em, List<string> problems)
        {
            try
            {
                MachineTrackStore.CompleteReaders();
                int missing = 0;
                foreach (var p in MachineRegistry.All)
                {
                    // a roller is a managed GameObject unit (E = Entity.Null): no LivePath to check
                    if (p.IsRoller || !p.Alive(em)) continue;
                    if (!em.HasComponent<LivePath>(p.E)) missing++;
                    missing += MissingLivePath(em, p.E, 0);
                }
                if (missing > 0) problems.Add("machines: " + missing + " puppet entities / descendants without LivePath");
                int overlaps = MachineChecks.CountOverlaps(out string first);
                if (overlaps > 0) problems.Add("machines: " + overlaps + " overlapping machine pairs now (first: " + first + ")");
                MachineChecks.Round2(em, problems);
                MachineChecks.Round4(em, problems);
                MachineChecks.Round5(em, problems, true);   // rollers, IK digging, puffs; liveRollerObjects vs units
                MachineChecks.RoundUpgrade(em, problems);   // mode H: machine-safe runs, clearances, driveway keep-outs, slew
            }
            catch (Exception e) { problems.Add("machines: check failed: " + e.Message); }
        }

        private static int MissingLivePath(EntityManager em, Entity e, int depth)
        {
            if (depth > 4 || !em.HasBuffer<ObjSubObject>(e)) return 0;
            int n = 0;
            var buf = em.GetBuffer<ObjSubObject>(e, true);
            for (int i = 0; i < buf.Length; i++)
            {
                var s = buf[i].m_SubObject;
                if (!em.Exists(s) || em.HasComponent<Deleted>(s)) continue;
                if (!em.HasComponent<LivePath>(s)) n++;
                n += MissingLivePath(em, s, depth + 1);
            }
            return n;
        }
    }

    // Debug switches of the module (dev commands flip them; release builds never do).
    public static class MachineDebug
    {
        public static int TruckGridReplans;          // loading trips re-planned onto a re-phased dig grid
        public static bool LogDespawns;
        public static int CheckEvery;                 // rrw.mx.check watch: run the live check every N updates (0 = off)
        public static Action<EntityManager> Watch;    // set by the dev commands (rrw.mx.check watch)
        public static Action<EntityManager> Trace;    // set by the dev commands (rrw.mx.trace), run in Rendering after the bones
        public static bool BucketDust;                // rrw.mx.dust 1: spawn the dusty excavator clone (evaluation only; product = Quiet)
        public static bool RespawnDiggers;            // set by rrw.mx.dust: the director respawns the diggers once
        public static int GuardYields, GuardFailures; // excavator <-> grader guard counters (rrw.mx.check / rrw.dump)
        // guard / leave counters (rrw.mx.check / rrw.mx.zones / rrw.dump)
        public static int GuardBrakes, GuardContacts, GuardBackOuts, GuardDeadlocks; // separation guard
        public static int StandStills;                // truck plans replaced by a stand-still (known conflict)
        public static int CapDespawns, Leaves;        // leaving puppets removed at the kPostSiteMaxSimFrames cap / leaves started
        public static string LastGuard = "";
        public static int OverlapFrames, CheckedFrames; // watch mode: updates with >= 1 overlapping pair / updates checked
        // ---- leave removals, deadlocks, stuck machines (rrw.mx.check)
        public static int LongLeaveCaps;              // leavers whose route needs longer than kPostSiteMaxSeconds (scaled cap)
        public static int VisibleCapRemovals;         // leavers removed at the leave cap while VISIBLE (last resort; target 0)
        public static uint LastVisibleCapUpdate;      // RRWClock.UpdateIndex of the last visible cap removal (rrw.check window)
        public static string LastVisibleCap = "";
        public static int CulledRemovals, FarRemovals; // leavers removed out of view (culled) / beyond kMachineDespawnRadius
        public static int DeadlockEpisodes;           // mutual waits that reached kGuardDeadlockSeconds (counted once per episode)
        public static int StandStillEpisodes;         // trucks that began a stand-still (StandStills counts every retry)
        public static int StuckEpisodes, StuckNow, StuckMax; // stuck episodes started / puppets stuck now / max at once
        public static double StuckLongest;            // longest stuck duration seen (machine s)
        public static int ResYield, ResBackOut, ResOtherBackOut, ResReplan, ResOtherExit, ResRemoved, ResNone; // resolutions by kind
        // leavers whose way to the only connected exit runs through a WORKING machine of their project
        public static int LeaverDeadEndYields;        // ... parked at the other (dead) end instead (planned, or as a stuck resolution)
        public static int LeaverCrewCrossings;        // ... that had to cross it anyway (the other end was blocked by the crew too)
        public static bool LegacyReady;               // dev rrw.mx.ready legacy: WorkZonesReady := AllLanes & ~OpenLanes & ~SoftZones
                                                      // (diagnosis while the Director does not publish WorkZonesReady yet)
        public static string LastStuck = "";
        // ---- crew LOD gauges of the last update
        public static int CrewsActive, CrewsLodOut;
        // ---- dead-end leaver removals, the runtime bite override (rrw.mx.dig bite=; NaN = kDigBite)
        public static int DeadEndRemovalsVisible, DeadEndRemovalsCulled;
        public static string LastDeadEnd = "";
        public static float DigBite = float.NaN;
        // dev rrw.mx.roller spawn / clear, rrw.mx.dig trace
        public static uint ForceRollerProject, ForceRollerUntil;
        public static int ForceRollerCrew, ForceRollerIndex;
        public static bool ClearRollers;
        public static Puppet DigTrace;
        public static double DigTraceUntil;
        // ---- static slots, make-way, prompt leavers (rrw.mx.check "separation")
        public static int MakeWays, MakeWayFails, MakeWayLeaves; // standing blockers moved aside / attempts without a spot / blockers sent off
        public static int BlockingLeaverRemovals;     // leavers removed because they blocked a working machine with nowhere to go aside
        public static int ExitRemovalsVisible, ExitRemovalsCulled; // release: leavers removed at their connected exit stop
        public static int CompactTurns, NoTurnAhead;  // leavers: compact K-turns / no room to turn -> the end ahead (dead end)
        public static int InlineWaits;                // narrow inline loading: outbound delayed until there is room behind the excavator
        public static int StaticShifts, OwnerWaits, BaseLaneSwaps, SpawnPathDeferrals, LeaveSlotShifts;
        public static int CrewTruckLeads, ExitQueueAdvances;   // C4 swap: crew trucks leading their painter; exit queue re-plans
        public static int HeadOnSwitches, HeadOnUnresolved, ConvoyFollows;   // head-on leavers (exit switch / none) / convoy speed caps
        public static string LastMakeWay = "";
        public static string Round7Summary() =>
            "makeWay(done=" + MakeWays + " none=" + MakeWayFails + " leaves=" + MakeWayLeaves + " blockingLeaverRemovals=" + BlockingLeaverRemovals + ")" +
            " exitRemovals(visible=" + ExitRemovalsVisible + " culled=" + ExitRemovalsCulled + ") turns(compact=" + CompactTurns + " noTurnAhead=" + NoTurnAhead + ")" +
            " inlineWaits=" + InlineWaits + " staticShifts=" + StaticShifts + " leaveSlotShifts=" + LeaveSlotShifts + " ownerWaits=" + OwnerWaits +
            " baseLaneSwaps=" + BaseLaneSwaps + " spawnPathDeferrals=" + SpawnPathDeferrals + " crewTruckLeads=" + CrewTruckLeads + " exitQueueAdvances=" + ExitQueueAdvances +
            " headOn(switches=" + HeadOnSwitches + " unresolved=" + HeadOnUnresolved + " convoys=" + ConvoyFollows + ")" +
            (LastMakeWay.Length > 0 ? " lastMakeWay=" + LastMakeWay : "");
    }

    // Live invariant checks shared by rrw.check and rrw.mx.check.
    public static partial class MachineChecks
    {
        private static readonly PlanContext s_Ctx = new PlanContext();
        private static readonly List<Choreo.Obb> s_Boxes = new List<Choreo.Obb>(64);
        private static readonly List<ChainState> s_States = new List<ChainState>(64);
        private static readonly List<bool> s_Ok = new List<bool>(64);

        // Overlapping pairs now: TRUE boxes (no planner margin), pair rules of Choreo.PairHit (the digger at work is tested
        // with its chassis against its own trucks; the C3 feed truck / paver pair with true contact). There is no
        // blanket "excavator x truck" exemption, so a truck driving through a parked / retired excavator counts.
        public static int CountOverlaps(out string first)
        {
            first = "";
            var all = MachineRegistry.All;
            if (all.Count < 2) return 0;
            s_Ctx.Clk = MachineRegistry.Clock;
            s_Ctx.Tv = MachineTrackStore.View();
            double now = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
            s_Boxes.Clear(); s_States.Clear(); s_Ok.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                bool ok = Choreo.BoxAt(all[i], all[i].Plan, s_Ctx, now, out var b, out var st);
                s_Boxes.Add(b); s_States.Add(st); s_Ok.Add(ok);
            }
            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (!s_Ok[i]) continue;
                for (int j = i + 1; j < all.Count; j++)
                {
                    if (!s_Ok[j]) continue;
                    if (math.distancesq(s_Boxes[i].C, s_Boxes[j].C) > 900f) continue;
                    if (Choreo.PairHit(all[i], s_States[i], s_Boxes[i], all[j], s_States[j], s_Boxes[j], 0f))
                    {
                        if (n == 0) first = all[i] + " x " + all[j] + " gap=" + RRWLog.F(Choreo.Gap(s_Boxes[i], s_Boxes[j]));
                        n++;
                    }
                }
            }
            return n;
        }

        // Lane invariants (rrw.check lines that must stay 0):
        //  * puppets inside an open group: the box NOW touches a group of ProjectRecord.OpenLanes;
        //  * puppets of a releasing project (or leaving) older than kPostSiteMaxSimFrames (+ 2 updates of slack);
        //  * puppets standing Outside the trimmed chain;
        //  * a stale machine report for a project with puppets.
        // Readiness and staging invariants:
        //  * a working puppet (not leaving) whose box NOW or committed plan touches a group outside WorkZonesReady (fresh
        //    machine report; a plan committed before the group left WorkZonesReady counts too: it should have been re-planned);
        //  * C4 with halves: a working box outside the stage's works half (touches a car half that is not stage.Works);
        //  * a puppet stuck longer than 2 x kMachineStuckSeconds.
        public static void Round2(EntityManager em, List<string> problems)
        {
            uint ui = RRWClock.UpdateIndex;
            double now = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
            int inOpen = 0, overCap = 0, outside = 0, stale = 0, notReady = 0, offHalf = 0, stuckLong = 0;
            string fOpen = "", fCap = "", fOut = "", fStale = "", fReady = "", fHalf = "", fStuck = "";
            int dummy = 0;
            foreach (var p in MachineRegistry.All)
            {
                if (!p.Alive(em) || !SiteRegistry.TryGetProject(p.ProjectId, out var pr)) continue;
                var v = pr.View();
                MachineReport.Zones(p, v.Trim0, v.Trim1, now, out var zNow, out var zPlan, ref dummy);
                var open = pr.OpenLanes & RoadZones.AllLanes;
                bool band = p.Upgrade && p.UwBand >= 0;   // mode H band crews: RoundUpgrade (machine-safe sub-strips, not groups)
                if (!band && (zNow & open) != 0) { if (inOpen++ == 0) fOpen = p + " zones=" + zNow + " open=" + open; }
                if (!band && !p.PostSite && !p.Outgoing && pr.MachinesReportFresh(ui) && !MachineDebug.LegacyReady)
                {
                    var bad = (zNow | zPlan) & RoadZones.AllLanes & ~pr.WorkZonesReady;
                    if (bad != RoadZones.None && pr.Switch == StageSwitch.None)
                    { if (notReady++ == 0) fReady = p + " zones now/plan=" + zNow + "/" + zPlan + " notReady=" + RoadZoneMath.Describe(bad) + " ready=" + RoadZoneMath.Describe(pr.WorkZonesReady); }
                    if (v.HalvesActive)
                    {
                        var works = PhasePlan.Stage(v).Works & RoadZones.Carriageway;
                        var off = zNow & RoadZones.Carriageway & ~works;
                        if (works != RoadZones.None && off != RoadZones.None)
                        { if (offHalf++ == 0) fHalf = p + " zones=" + zNow + " works=" + works; }
                    }
                }
                if (p.Stuck && !double.IsNaN(p.StuckSince) && now - p.StuckSince + RRWConst.kMachineStuckSeconds > 2.0 * RRWConst.kMachineStuckSeconds)
                { if (stuckLong++ == 0) fStuck = p + " stuck for " + RRWLog.F((float)(now - p.StuckSince + RRWConst.kMachineStuckSeconds)) + " s waiting for " + p.GuardBlocker; }
                double leaveOver = MachineDirectorSystem.LeaveOver(p, pr, now, out double leaveAge, out double leaveCap);
                if (leaveOver > 2.0 / 60.0)
                { if (overCap++ == 0) fCap = p + " leaving for " + RRWLog.F((float)leaveAge) + " s (cap " + RRWLog.F((float)leaveCap) + " s)"; }
                if ((zNow & RoadZones.Outside) != 0 && p.Plan.Count > 0)
                {
                    MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
                    if (math.abs(s.Speed) < 0.05f) { if (outside++ == 0) fOut = p + " u=" + RRWLog.F(s.U) + " trims=[" + RRWLog.F(v.Trim0) + "," + RRWLog.F(v.Trim1) + "]"; }
                }
            }
            foreach (var pr in SiteRegistry.Projects.Values)
            {
                bool any = false;
                foreach (var p in MachineRegistry.All) if (p.ProjectId == pr.Id) { any = true; break; }
                if (any && !pr.MachinesReportFresh(ui)) { if (stale++ == 0) fStale = "p" + pr.Id + " age=" + unchecked((int)(ui - pr.MachinesReportUpdate)); }
            }
            if (inOpen > 0) problems.Add("machines: " + inOpen + " puppets inside an OPEN lane group (first: " + fOpen + ")");
            if (overCap > 0) problems.Add("machines: " + overCap + " leaving / releasing puppets older than their leave cap (first: " + fCap + ")");
            // a leaver removed at its cap while VISIBLE (the cap is scaled to its route, so 0 is expected)
            if (MachineDebug.LastVisibleCapUpdate != 0 && unchecked(ui - MachineDebug.LastVisibleCapUpdate) < 3600u)
                problems.Add("machines: a leaver was removed at its leave cap while VISIBLE in the last 3600 updates (" + MachineDebug.VisibleCapRemovals
                             + " in total; last: " + MachineDebug.LastVisibleCap + ")");
            if (outside > 0) problems.Add("machines: " + outside + " puppets standing outside the trimmed chain (first: " + fOut + ")");
            if (stale > 0) problems.Add("machines: " + stale + " projects with puppets but a stale machine report (first: " + fStale + ")");
            if (notReady > 0) problems.Add("machines: " + notReady + " working puppets in / planned into a group outside WorkZonesReady (first: " + fReady + ")");
            if (offHalf > 0) problems.Add("machines: " + offHalf + " C4 puppets with a box outside the stage's works half (first: " + fHalf + ")");
            if (stuckLong > 0) problems.Add("machines: " + stuckLong + " puppets stuck longer than 2 x kMachineStuckSeconds (first: " + fStuck + ")");
        }

        // Crew and speed invariants (rrw.check lines that must stay 0):
        //  * speed / acceleration violations of the observed motion against MachineLimits (MxSpeed, since the last reset);
        //  * runaway stops and carried speeds dropped by the runaway guard (MxSpeed.Runaways / RunawayClamps);
        //  * a working puppet (not leaving) more than kOutOfRangeCheck outside its crew's range [SecLo, SecHi];
        //  * a puppet of a crew index beyond the project's crew count;
        //  * the job-side front model (FrontLin) disagreeing with PhasePlan.SectionFront (front model mismatch > 1 cm).
        public static void Round4(EntityManager em, List<string> problems)
        {
            if (MxSpeed.SpeedViolations > 0) problems.Add("machines: " + MxSpeed.SpeedViolations + " speed violations (first: " + MxSpeed.FirstSpeed + "; last: " + MxSpeed.LastSpeed + ")");
            if (MxSpeed.AccelViolations > 0) problems.Add("machines: " + MxSpeed.AccelViolations + " acceleration violations (first: " + MxSpeed.FirstAccel + "; last: " + MxSpeed.LastAccel + ")");
            if (MxSpeed.Runaways > 0) problems.Add("machines: " + MxSpeed.Runaways + " runaway stops (first: " + MxSpeed.FirstRunaway + ")");
            if (MxSpeed.RunawayClamps > 0) problems.Add("machines: " + MxSpeed.RunawayClamps + " carried speeds above the runaway limit dropped (first: " + MxSpeed.FirstRunawayClamp + ")");
            double now = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
            int outRange = 0, badCrew = 0, frontBad = 0;
            string fOut = "", fCrew = "", fFront = "";
            foreach (var p in MachineRegistry.All)
            {
                if (!p.Alive(em) || p.PostSite || p.Outgoing || p.Plan.Count <= 0 || !SiteRegistry.TryGetProject(p.ProjectId, out var pr)) continue;
                var v = pr.View();
                int n = v.CrewCount;
                if (p.Crew < 0 || p.Crew >= n) { if (badCrew++ == 0) fCrew = p + " crews=" + n; continue; }
                var cp = PhasePlan.Crew(v, p.Crew);
                MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
                if (cp.SecHi > cp.SecLo && (s.U < cp.SecLo - MxConst.kOutOfRangeCheck || s.U > cp.SecHi + MxConst.kOutOfRangeCheck))
                { if (outRange++ == 0) fOut = p + " u=" + RRWLog.F(s.U) + " section=[" + RRWLog.F(cp.SecLo) + "," + RRWLog.F(cp.SecHi) + "] act=" + p.Activity; }
                var fr = p.Plan.FrontA;
                if (fr.Phase == (byte)pr.Phase)
                {
                    MachineMotion.FrontLin(fr, MachineRegistry.Clock, now, out float flin, out _, out float fmin, out float fmax);
                    float a = math.clamp(flin, fmin, fmax), b = MachineMotion.Front(fr, MachineRegistry.Clock, now);
                    if (math.abs(a - b) > 0.01f) { if (frontBad++ == 0) fFront = p + " lin=" + RRWLog.F(a) + " front=" + RRWLog.F(b); }
                }
            }
            if (outRange > 0) problems.Add("machines: " + outRange + " working puppets more than " + RRWLog.F(MxConst.kOutOfRangeCheck) + " m outside their crew's section (first: " + fOut + ")");
            if (badCrew > 0) problems.Add("machines: " + badCrew + " puppets of a crew beyond the project's crew count (first: " + fCrew + ")");
            if (frontBad > 0) problems.Add("machines: " + frontBad + " puppets whose linear front model disagrees with PhasePlan.SectionFront (first: " + fFront + ")");
        }

        // Roller and IK digging invariants (rrw.check and rrw.mx.check lines that must stay 0):
        //  * roller speed / acceleration violations (the speed invariant, rollers' row);
        //  * a compacting roller outside its pass window by more than 1 m (+ the window's own motion over one pass, 30 s);
        //  * a roller's root Y vs the track floor at its pivot > 0.05 m (render frame);
        //  * live RRW_Roller_* GameObjects != roller units (dev: allocates, only with `objects`);
        //  * IK reachedErr > 0.2 m, the tip under the floor guard (floor - bite) by > 0.02 m, the bucket closer than kDigTruckClearMin to
        //    the truck box (DEVTOOLS), a loading truck whose load differs from its counted dumps;
        //  * dust puffs alive longer than kDustPuffLifeSeconds + 1 s.
        public static void Round5(EntityManager em, List<string> problems, bool objects)
        {
            double now = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
            int win = 0, rootY = 0, ikErr = 0, below = 0, clear = 0, load = 0, worldGap = 0, spoilOut = 0;
            string fWin = "", fRoot = "", fIk = "", fBelow = "", fClear = "", fLoad = "", fGap = "", fSpoil = "";
            int units = 0;
            foreach (var p in MachineRegistry.All)
            {
                if (p.IsRoller)
                {
                    if (p.Unit != null) units++;
                    if (p.PostSite || p.Outgoing || p.Plan.Count <= 0 || p.Activity != MachineActivity.Compact) continue;
                    if (p.RootYErr > 0.05f) { if (rootY++ == 0) fRoot = p + " rootYErr=" + RRWLog.F(p.RootYErr); }
                    if (double.IsNaN(p.PassStart) || now < p.PassStart || !SiteRegistry.TryGetProject(p.ProjectId, out var pr)) continue;
                    var v = pr.View();
                    if (p.Crew < 0 || p.Crew >= v.CrewCount) continue;
                    var rs = PhasePlan.Crew(v, p.Crew).Roller(p.RollerIndex);
                    if (rs.Slot.Activity != MachineActivity.Compact || float.IsNaN(rs.WinLo)) continue;
                    MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
                    float tol = 1f + math.abs(PhasePlan.CrewFrontSpeed(v)) * 30f;
                    if (s.U < rs.WinLo - tol || s.U > rs.WinHi + 1f)
                    { if (win++ == 0) fWin = p + " u=" + RRWLog.F(s.U) + " window=[" + RRWLog.F(rs.WinLo) + "," + RRWLog.F(rs.WinHi) + "] tol=" + RRWLog.F(tol); }
                    continue;
                }
                if (p.Dig != null && p.Dig.Frames > 0)
                {
                    var d = p.Dig;
                    if (d.MaxErr > 0.2f) { if (ikErr++ == 0) fIk = p + " reachedErr=" + RRWLog.F(d.MaxErr); }
                    if (d.TipBelowFloor > 0.02f) { if (below++ == 0) fBelow = p + " tip " + RRWLog.F(d.TipBelowFloor) + " m under floor - bite"; }
                    if (d.TruckClearMin < RRWConst.kDigTruckClearMin) { if (clear++ == 0) fClear = p + " truckClearMin=" + RRWLog.F(d.TruckClearMin); }
                    // the drag tip vs the world floor under it; spoil dumps with no point inside the bounds
                    if (d.WorldFloorGap > 0.12f) { if (worldGap++ == 0) fGap = p + " worldFloorGap=" + RRWLog.F(d.WorldFloorGap) + " floorH=" + RRWLog.F(d.LastFloorH); }
                    if (d.SpoilOutsideCycles > 0) { if (spoilOut++ == 0) fSpoil = p + " cycles=" + d.SpoilOutsideCycles + " last: " + d.LastSpoil; }
                }
                if (p.IsTruck && p.EventLoads && !p.PostSite)
                {
                    int want = (int)math.round(DigLoad.Share(p.Buckets) * 100f);
                    if (p.AmountPct != want && p.Buckets > 0) { if (load++ == 0) fLoad = p + " amount=" + p.AmountPct + "% dumps=" + p.Buckets + " (" + want + "%)"; }
                }
            }
            long rv = MxSpeed.RoleViolV[(int)MachineRole.Count], ra = MxSpeed.RoleViolA[(int)MachineRole.Count];
            if (rv + ra > 0) problems.Add("machines: rollers exceeded their MachineLimits row (speed " + rv + ", accel " + ra + ")");
            if (win > 0) problems.Add("machines: " + win + " compacting rollers outside their pass window (first: " + fWin + ")");
            if (rootY > 0) problems.Add("machines: " + rootY + " rollers with root Y off the track floor > 0.05 m (first: " + fRoot + ")");
            if (ikErr > 0) problems.Add("machines: " + ikErr + " IK diggers with reachedErr > 0.2 m (first: " + fIk + ")");
            if (below > 0) problems.Add("machines: " + below + " IK buckets below the floor guard by > 0.02 m (first: " + fBelow + ")");
            if (clear > 0) problems.Add("machines: " + clear + " IK buckets closer than " + RRWLog.F(RRWConst.kDigTruckClearMin) + " m to the truck box (first: " + fClear + ")");
            if (load > 0) problems.Add("machines: " + load + " loading trucks whose load differs from their counted dumps (first: " + fLoad + ")");
            if (worldGap > 0) problems.Add("machines: " + worldGap + " IK diggers whose drag tip is > 0.12 m off the world floor under it (first: " + fGap + ")");
            if (spoilOut > 0) problems.Add("machines: " + spoilOut + " IK diggers dumped spoil outside the carriageway / works footprint (first: " + fSpoil + ")");
            int old = MachineDirectorSystem.PuffsOverAge(now);
            if (old > 0) problems.Add("machines: " + old + " dust puffs alive longer than " + RRWLog.F(RRWConst.kDustPuffLifeSeconds + 1f) + " s");
            if (RollerRenderSystem.Faulted) problems.Add("machines: the roller pass is disabled after an exception (rollers off until the next load)");
            if (objects)
            {
                int live = RollerWorld.LiveRootObjects();
                if (live != units) problems.Add("machines: liveRollerObjects=" + live + " != roller units " + units + " (strays or leaks; destroyed objects vanish at the frame end)");
            }
        }

        public static string Round5Summary()
        {
            int units = 0, compacting = 0;
            foreach (var p in MachineRegistry.All) if (p.IsRoller) { units++; if (!p.PostSite && p.Activity == MachineActivity.Compact) compacting++; }
            return "rollers(units=" + units + " compacting=" + compacting + " spawns=" + MachineDirectorSystem.RollerSpawns + " relays=" + MachineDirectorSystem.RollerRelays +
                   " gateSpawns=" + MachineDirectorSystem.RollerGateSpawns + " rollerInViewSpawns=" + MachineDirectorSystem.RollerInViewSpawns + " driveIns=" + MachineDirectorSystem.RollerDriveIns +
                   " defers=" + MachineDirectorSystem.RollerDefers + " leaves=" + MachineDirectorSystem.RollerLeaves + " posed=" + RollerRenderSystem.Posed +
                   " R.Roller avg/max=" + RRWLog.F(RollerRenderSystem.AvgMsPerRoller) + "/" + RRWLog.F(RollerRenderSystem.MaxMsPerRoller) + " ms per roller" +
                   (RollerRenderSystem.Faulted ? " FAULTED" : "") + " created=" + RollerWorld.Created_ + " destroyed=" + RollerWorld.Destroyed + " strays=" + RollerWorld.StraysSwept + ")" +
                   " dig(buildsLastUpdate=" + MachineDirectorSystem.DigBuildsFrame + " puffs live=" + MachineDirectorSystem.PuffsLive + " spawned=" + MachineDirectorSystem.PuffsSpawned +
                   " skipped=" + MachineDirectorSystem.PuffsSkipped + " deleted=" + MachineDirectorSystem.PuffsDeleted +
                   (MachineDirectorSystem.LastPuffSkip.Length > 0 ? " lastSkip=" + MachineDirectorSystem.LastPuffSkip : "") + ")" +
                   " deadEndRemovals(visible=" + MachineDebug.DeadEndRemovalsVisible + ", culled=" + MachineDebug.DeadEndRemovalsCulled + ")" +
                   (MachineDebug.LastDeadEnd.Length > 0 ? " lastDeadEnd=" + MachineDebug.LastDeadEnd : "");
        }

        // Overlapping pairs among parked post-site machines (must always be 0).
        public static int CountPostSiteOverlaps(out string first)
        {
            first = "";
            var all = MachineRegistry.All;
            s_Ctx.Clk = MachineRegistry.Clock;
            s_Ctx.Tv = MachineTrackStore.View();
            double now = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (!all[i].PostSite || !Choreo.Box(all[i], all[i].Plan, s_Ctx, now, out var a)) continue;
                a.HL -= MxConst.kOverlapMargin; a.HW -= MxConst.kOverlapMargin;
                for (int j = i + 1; j < all.Count; j++)
                {
                    if (!all[j].PostSite || !Choreo.Box(all[j], all[j].Plan, s_Ctx, now, out var b)) continue;
                    b.HL -= MxConst.kOverlapMargin; b.HW -= MxConst.kOverlapMargin;
                    if (Choreo.Overlap(a, b)) { if (n == 0) first = all[i] + " x " + all[j]; n++; }
                }
            }
            return n;
        }

        // Smallest true clearance (m) between a project's digger (Excavator role) and its grader (Loader role) now;
        // +inf when no project has both. <= 0 means they intersect (must always be > 0).
        public static float MinDiggerGraderGap(out string who)
        {
            who = "";
            float best = float.PositiveInfinity;
            s_Ctx.Clk = MachineRegistry.Clock;
            s_Ctx.Tv = MachineTrackStore.View();
            double now = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
            foreach (var st in MachineRegistry.States.Values)
            for (int k = 0; k < st.Crews.Length; k++)
            {
                var e = st.Crews[k].Roles[(int)MachineRole.Excavator];
                var g = st.Crews[k].Roles[(int)MachineRole.Loader];
                if (e == null || g == null) continue;
                if (!Choreo.Box(e, e.Plan, s_Ctx, now, out var a) || !Choreo.Box(g, g.Plan, s_Ctx, now, out var b)) continue;
                a.HL -= MxConst.kOverlapMargin; a.HW -= MxConst.kOverlapMargin;
                b.HL -= MxConst.kOverlapMargin; b.HW -= MxConst.kOverlapMargin;
                float gap = Choreo.Gap(a, b);
                if (gap < best) { best = gap; who = e + " x " + g + (g.Yielding ? " (grader yielding)" : ""); }
            }
            return best;
        }
    }
}
