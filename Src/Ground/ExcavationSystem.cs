using System;
using System.Collections.Generic;
using System.Text;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Ground
{
    // ExcavationSystem (ModificationEnd, Before TerrainSystem, order 540).
    // Port of the terrain prototype (verified in game):
    // - Clones the 3 composition entities of every HideWanted mode-A works edge (Instantiate), repoints the edge's
    //   Composition at them and writes TerrainComposition = vanilla + TerrainProfile (clip far underground, real trench).
    // - Re-renders with trigger b (Updated on the edge, here, right before TerrainSystem: no lane/geometry regeneration).
    //   Global cap: one trigger update per kTerrainTriggerMinUpdates; bypassed on hide frames (creation / demolition hide), the reveal-hold
    //   entry, rebuild and composition adoption.
    // - Reveal (flicker rule, verified in game): never un-hide AND restore the clip in one frame. Cloned -> RevealHold (clones kept,
    //   floor <= kRevealBedY) for kRevealHoldUpdates -> vanilla values on the clones (+ trigger) -> Restoring -> repoint
    //   to the vanilla sources next update -> Vanilla (RoadWorksGround.Settled).
    // - Adopts foreign compositions (upgrade, new connection), samples natural ground, GC's clones, fixes up after loads.
    // Single writer of RoadWorksGround and of Composition on works edges. Writes only m_NatAbove/m_NatBelow
    // and NaturalSampled on RoadWorksSite.
    [RegisterSystem(SystemUpdatePhase.ModificationEnd, Before = typeof(TerrainSystem), Order = RRWOrder.Excavation)]
    public partial class ExcavationSystem : GameSystemBase
    {
        internal static ExcavationSystem Instance;

        public const int kSweepIntervalUpdates = 16;       // orphan-clone sweep while works exist
        public const int kIdleSweepIntervalUpdates = 64;   // orphan-clone sweep with an empty registry
        public const int kVanillaWriteMaxWaitUpdates = 5;  // hold-end vanilla write waits at most this long for the cap
        public const int kIneligibleRecheckUpdates = 30;
        public const int kFaultAfterFails = 30;            // per-edge consecutive failures -> forced hold -> vanilla
        public const int kDropAfterFails = 60;             // -> emergency restore + forget

        private struct NodeMemo
        {
            public bool Dig;
            public TerrainProfile Shallowest;
        }

        private TerrainSystem m_Terrain;
        private CloneGc m_Gc;
        private EntityQuery m_CloneQuery;
        private readonly RRWGuard m_Guard = new RRWGuard("ground ExcavationSystem");
        private readonly List<GroundEdgeState> m_States = new List<GroundEdgeState>(64);
        private readonly List<Entity> m_TriggerBatch = new List<Entity>(64);
        private readonly HashSet<Entity> m_TriggerSet = new HashSet<Entity>();
        private readonly List<Entity> m_Remove = new List<Entity>(16);
        private readonly List<Entity> m_SweepEdges = new List<Entity>(8);
        private readonly List<Entity> m_Connected = new List<Entity>(8);
        private readonly List<NaturalResult> m_NatResults = new List<NaturalResult>(8);
        private readonly Dictionary<Entity, NodeMemo> m_NodeMemo = new Dictionary<Entity, NodeMemo>(64);
        private uint m_Now;
        private uint m_NextSweep;
        private bool m_CapOpen, m_AnyBypass, m_AnyPending;
        private int m_RebuildCount;
        private int m_NatCursor;

        public static bool Faulted => Instance != null && Instance.m_Guard.Faulted;
        public int CloneEntityCount => m_CloneQuery.CalculateEntityCount();
        internal CloneGc Gc => m_Gc;

        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            m_Terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            var compRefs = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Composition>() },
                Options = EntityQueryOptions.IncludeDisabledEntities,
            });
            var orphanRefs = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Orphan>() },
                Options = EntityQueryOptions.IncludeDisabledEntities,
            });
            m_CloneQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RRWCompositionClone>() },
                Options = EntityQueryOptions.IncludeDisabledEntities,
            });
            m_Gc = new CloneGc(compRefs, orphanRefs, m_CloneQuery);
            RRWIntrospection.RegisterDumper("Ground", Dump);
            RRWIntrospection.RegisterChecker("Ground", Check);
            RRWLog.Info("ground ExcavationSystem created (ModificationEnd before TerrainSystem)");
        }

        protected override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            int n = GroundTable.Edges.Count;
            GroundTable.Clear();
            GroundTable.Abandoned.Clear();
            GroundTable.LoadFixupPending = true;
            NaturalSampler.GpuInFlight = 0;
            m_Gc.Reset();
            m_Guard.Reset();
            m_NextSweep = 0;
            m_NodeMemo.Clear();
            if (n > 0) RRWLog.Info("ground preload: forgot " + n + " edge states (clones are cleaned up by the load fixup)");
        }

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted) return;
            long t0 = RRWPerf.Start();
            try
            {
                RRWClock.Update(World);
                var gm = GameManager.instance;
                if (gm == null || gm.gameMode != GameMode.Game) return;
                Tick(RRWClock.UpdateIndex);
                m_Guard.Ok();
            }
            catch (Exception e) { m_Guard.Fail(e); }
            finally { RRWPerf.Stop(PerfSlot.Ground, t0); }
        }

        // ================================================================ frame

        private void Tick(uint now)
        {
            var em = EntityManager;
            m_Now = now;
            if (GroundTable.LoadFixupPending)
            {
                GroundTable.LoadFixupPending = false;
                try { m_Gc.LoadFixup(em, now); } catch (Exception e) { RRWLog.ErrorOnce("ground LoadFixup", e); }
                m_NextSweep = now;
            }
            try { DrainNaturalResults(em); } catch (Exception e) { RRWLog.ErrorOnce("ground natural results", e); }

            bool work = SiteRegistry.Edges.Count > 0 || GroundTable.Edges.Count > 0 || GroundTable.PendingDestroy.Count > 0;
            if (!work)
            {
                // Orphan clone references must be restored even with an empty registry (exception to the idle-when-empty rule).
                if (Due(m_NextSweep))
                {
                    m_NextSweep = now + kIdleSweepIntervalUpdates;
                    if (m_CloneQuery.CalculateEntityCount() > 0) RunSweep(em, now);
                }
                return;
            }

            m_CapOpen = !GroundTable.HaveTriggered || unchecked(now - GroundTable.LastTriggerUpdate) >= RRWConst.kTerrainTriggerMinUpdates;
            m_AnyBypass = false;
            m_AnyPending = false;
            m_RebuildCount = 0;
            m_TriggerBatch.Clear();
            m_TriggerSet.Clear();
            m_NodeMemo.Clear();

            // 1. registry edges -> states
            foreach (var kv in SiteRegistry.Edges)
            {
                if (!GroundTable.Edges.TryGetValue(kv.Key, out var st))
                {
                    st = new GroundEdgeState { Edge = kv.Key, CreatedUpdate = now };
                    st.ClearSlots();
                    GroundTable.Edges.Add(kv.Key, st);
                }
                st.SeenUpdate = now;
                st.Record = kv.Value;
            }

            // 2. clones referenced by edges we have no state for (lost state, load, never-known edges)
            if (Due(m_NextSweep))
            {
                m_NextSweep = now + kSweepIntervalUpdates;
                RunSweep(em, now);
            }

            // 3. per-edge state machine
            m_States.Clear();
            m_States.AddRange(GroundTable.Edges.Values);
            m_Remove.Clear();
            for (int i = 0; i < m_States.Count; i++) RunState(m_States[i]);

            // 4. writes: every pending edge when a trigger update happens (bypass, or the global cap is open)
            if (m_AnyBypass || (m_AnyPending && m_CapOpen))
            {
                int before = m_TriggerBatch.Count;
                for (int i = 0; i < m_States.Count; i++)
                {
                    var st = m_States[i];
                    if (!st.Pending || m_Remove.Contains(st.Edge)) continue;
                    try { Write(st); }
                    catch (Exception e) { RRWLog.ErrorOnce("ground write", e); st.Fails++; }
                }
                if (m_TriggerBatch.Count - before > GroundTable.MaxBatchEdges) GroundTable.MaxBatchEdges = m_TriggerBatch.Count - before;
                if (m_TriggerBatch.Count > before || m_AnyBypass)
                {
                    GroundTable.HaveTriggered = true;
                    GroundTable.LastTriggerUpdate = now;
                    GroundTable.TriggerUpdates++;
                }
            }

            // 5. trigger b: Updated on the edge (only if not already Updated), right before TerrainSystem
            for (int i = 0; i < m_TriggerBatch.Count; i++)
            {
                Entity e = m_TriggerBatch[i];
                if (EcsUtil.Alive(em, e) && !em.HasComponent<Updated>(e)) em.AddComponent<Updated>(e);
            }
            GroundTable.TriggeredEdges += m_TriggerBatch.Count;

            // 6. output + forget finished states
            for (int i = 0; i < m_States.Count; i++)
            {
                var st = m_States[i];
                if (m_Remove.Contains(st.Edge)) continue;
                bool inReg = st.SeenUpdate == now;
                NotePendingAge(st);
                if (inReg)
                {
                    try
                    {
                        SyncAppliedArgs(st);
                        WriteOutput(em, st);
                        NoteDemolitionReady(em, st);
                    }
                    catch (Exception e) { RRWLog.ErrorOnce("ground output", e); }
                }
                else if (st.Stage == GroundStage.Vanilla) m_Remove.Add(st.Edge);
            }
            for (int i = 0; i < m_Remove.Count; i++) GroundTable.Edges.Remove(m_Remove[i]);

            if (m_RebuildCount > 0)
                RRWLog.Info("ground: re-applied trench profiles on " + m_RebuildCount + " hidden works edges (load / rebuild visuals), in the same update (trigger bypass)");

            // 7. natural sampling (at most one edge per update)
            try { SampleNatural(em, now); } catch (Exception e) { RRWLog.ErrorOnce("ground natural sampling", e); }

            // 8. clone GC
            try { m_Gc.Collect(em, now); }
            catch (Exception e) { RRWLog.ErrorOnce("ground gc", e); }
        }

        private void AddTrigger(Entity edge)
        {
            if (m_TriggerSet.Add(edge)) m_TriggerBatch.Add(edge);
        }

        private bool Due(uint at) => unchecked((int)(m_Now - at)) >= 0;

        private void RunSweep(EntityManager em, uint now)
        {
            try
            {
                m_Gc.Sweep(em, now, m_SweepEdges);
                for (int i = 0; i < m_SweepEdges.Count; i++)
                {
                    Entity edge = m_SweepEdges[i];
                    if (GroundTable.Edges.ContainsKey(edge)) continue;
                    var st = new GroundEdgeState { Edge = edge, CreatedUpdate = now };
                    st.ClearSlots();
                    if (SiteRegistry.TryGetEdge(edge, out var rec)) { st.SeenUpdate = now; st.Record = rec; }
                    GroundTable.Edges.Add(edge, st);
                    AdoptExisting(st, em.GetComponentData<Composition>(edge));
                    RRWLog.Info("ground: edge " + RRWLog.E(edge) + " references RRW clones without a state (load / lost site): reveal hold, then restore");
                }
            }
            catch (Exception e) { RRWLog.ErrorOnce("ground sweep", e); }
        }

        // ================================================================ state machine

        private void RunState(GroundEdgeState st)
        {
            try
            {
                RunStateInner(st);
                st.Fails = 0;
            }
            catch (Exception e)
            {
                RRWLog.ErrorOnce("ground edge state", e);
                st.Fails++;
                if (st.Fails == kFaultAfterFails && !st.Faulted)
                {
                    st.Faulted = true;
                    RRWLog.Warn("ground edge " + RRWLog.E(st.Edge) + " failed " + kFaultAfterFails + " updates in a row: forcing it through the reveal hold to vanilla");
                }
                else if (st.Fails >= kDropAfterFails) EmergencyDrop(st);
            }
        }

        private void RunStateInner(GroundEdgeState st)
        {
            var em = EntityManager;
            uint now = m_Now;
            Entity edge = st.Edge;
            if (!em.Exists(edge) || em.HasComponent<Deleted>(edge)) { DropDead(st); return; }
            bool inReg = st.SeenUpdate == now;
            if (!inReg) st.Record = null;
            if (!em.HasComponent<Composition>(edge))
            {
                if (st.Stage == GroundStage.Vanilla && !inReg) m_Remove.Add(edge);
                return;
            }
            var comp = em.GetComponentData<Composition>(edge);
            Eval(st);

            // orphan bookkeeping (rrw.check: no non-site edge references a clone beyond kOrphanCloneMaxUpdates)
            if (!inReg && st.Stage != GroundStage.Vanilla) { if (!st.IsOrphan) { st.IsOrphan = true; st.OrphanSinceUpdate = now; } }
            else st.IsOrphan = false;

            bool wanted = st.Wanted;
            if (wanted && !st.PrevHideWanted) st.WantedSinceUpdate = now;
            st.PrevHideWanted = wanted;

            // demolition Complete: the road stays hidden, the edge stays Cloned at Morph(0) until the
            // Director deletes it. Remember when it got there (dev check / log, frame-N bypass below).
            if (inReg && st.HideWantedRaw && st.SiteKind == WorksKind.Demolition && st.Phase == WorksPhase.Complete)
            {
                if (st.CompleteSinceUpdate == 0) st.CompleteSinceUpdate = now == 0 ? 1u : now;
            }
            else if (st.CompleteSinceUpdate != 0) { st.CompleteSinceUpdate = 0; st.DemoReadyLogged = false; }

            switch (st.Stage)
            {
                case GroundStage.Vanilla:
                    if (wanted) TryClone(st, comp);
                    else if (SlotsReferenceClones(comp)) { AdoptExisting(st, comp); RRWLog.Info("ground: works edge " + RRWLog.E(edge) + " references RRW clones without clone state: reveal hold, then restore"); }
                    else if (st.Ineligible && st.HideWantedRaw && Due(st.IneligibleCheckUpdate))
                    {
                        st.IneligibleCheckUpdate = now + kIneligibleRecheckUpdates;
                        if (EcsUtil.DigEligible(em, edge, out _)) { st.Ineligible = false; RRWLog.Info("ground: edge " + RRWLog.E(edge) + " is eligible for excavation again"); }
                    }
                    break;

                default:
                    if (!CheckSlots(st, ref comp)) return;   // vanilla is mid-change on a slot: retry next update
                    switch (st.Stage)
                    {
                        case GroundStage.Cloned:
                            if (wanted)
                            {
                                if (st.Rebuild) { ForceRewrite(st); m_RebuildCount++; }
                                // Demolition "frame N: Morph(0)": the first Complete update writes the final profile without
                                // waiting for the global trigger cap (once; usually a no-op since D2 f >= .9 already targets t = 0)
                                if (st.CompleteSinceUpdate == now && !(st.Written[0] && st.Applied[0].SameAs(st.Target))) st.Bypass = true;
                                if (m_CapOpen || st.Bypass) SetDesiredTargets(st);
                            }
                            else EnterHold(st);
                            break;

                        case GroundStage.RevealHold:
                            if (wanted)
                            {
                                // Hidden again before the clip returned (cancel to hidden, dev jump back): no repoint happened.
                                st.Stage = GroundStage.Cloned;
                                st.VanillaPending = false;
                                SetDesiredTargets(st);
                                st.Bypass = true;
                                RRWLog.Verbose("ground edge " + RRWLog.E(edge) + " hidden again during the reveal hold");
                            }
                            else if (st.HideWantedRaw && !st.VanillaPending)
                            {
                                // Ineligible / faulted but the Director still hides the road: never return the clip under a
                                // hidden road (hole). Keep holding; the 45-update hold counts from the frame it is visible.
                                st.HoldStartUpdate = now;
                                if (!st.IneligibleLogged)
                                {
                                    st.IneligibleLogged = true;
                                    RRWLog.Warn("ground: edge " + RRWLog.E(edge) + " cannot be excavated (" + (st.Faulted ? "faulted" : "ineligible") +
                                                ") but is still hidden: holding the reveal floor until the road is shown");
                                }
                            }
                            else if (!st.VanillaPending)
                            {
                                if (unchecked(now - st.HoldStartUpdate) >= RRWConst.kRevealHoldUpdates)
                                {
                                    for (int s = 0; s < 3; s++) st.Desired[s] = TerrainProfile.Vanilla;
                                    st.DesiredT = 0f;
                                    st.DesiredY = 0f;
                                    st.VanillaPending = true;
                                    st.Pending = true;
                                }
                            }
                            else if (unchecked(now - st.HoldStartUpdate) >= RRWConst.kRevealHoldUpdates + kVanillaWriteMaxWaitUpdates)
                                st.Bypass = true;   // waited long enough for the cap (orphans must settle within kOrphanCloneMaxUpdates)
                            break;

                        case GroundStage.Restoring:
                            if (wanted)
                            {
                                st.Stage = GroundStage.Cloned;
                                SetDesiredTargets(st);
                                st.Bypass = true;
                                RRWLog.Verbose("ground edge " + RRWLog.E(edge) + " hidden again while restoring");
                            }
                            else if (now != st.RestoreWrittenUpdate) FinishRestore(st, comp);
                            break;
                    }
                    break;
            }
            if (st.Pending) m_AnyPending = true;
            if (st.Bypass) m_AnyBypass = true;
        }

        // Pending-age bookkeeping after this update's writes (rrw.check / rrw.ground.status: the global trigger cap must
        // never starve an edge when several crew fronts deepen several edges at once; every pending edge is written together).
        private void NotePendingAge(GroundEdgeState st)
        {
            if (!st.Pending) { st.PendingSinceUpdate = 0; return; }
            if (st.PendingSinceUpdate == 0) { st.PendingSinceUpdate = m_Now == 0 ? 1u : m_Now; return; }
            uint age = unchecked(m_Now - st.PendingSinceUpdate);
            if (st.Stage == GroundStage.Cloned && age > GroundTable.MaxPendingAge) GroundTable.MaxPendingAge = age;
        }

        // Target of a registry edge for this update (memoised; neighbours read it for node-end arbitration).
        private void Eval(GroundEdgeState st)
        {
            uint now = m_Now;
            if (st.TargetUpdate == now) return;
            st.TargetUpdate = now;
            st.Wanted = false;
            st.Rebuild = false;
            st.HideWantedRaw = false;
            st.Target = TerrainProfile.Vanilla;
            st.TargetT = 0f;
            st.TargetY = 0f;
            if (st.SeenUpdate != now) return;
            var em = EntityManager;
            Entity edge = st.Edge;
            if (!em.HasComponent<RoadWorksRuntime>(edge) || !em.HasComponent<RoadWorksSite>(edge)) return;
            var rt = em.GetComponentData<RoadWorksRuntime>(edge);
            var site = em.GetComponentData<RoadWorksSite>(edge);
            st.Rebuild = rt.Has(RuntimeFlags.NeedsRebuild);
            st.HideWantedRaw = rt.HideWanted && site.Mode == VisualMode.FullDig;
            st.SiteKind = site.Kind;
            st.Phase = rt.m_Phase;
            st.HideFrameFlag = rt.Has(RuntimeFlags.HideFrame);
            var input = PlanInput.From(site, rt);   // carries rt.m_Crews: C1 depth / D2 restore per edge over the crew sections
            int crews = rt.m_Crews <= 1 ? 1 : rt.m_Crews;
            if (crews != st.Crews)
            {
                // A re-latch (phase change, model reset, chain change, C4 switch) re-targets like a progress change; at a
                // phase change every section front is at its start or end, so the target does not move (layout independent)
                if (st.Crews > 0)
                {
                    st.CrewChanges++;
                    GroundTable.CrewChanges++;
                    RRWLog.Verbose("ground: edge " + RRWLog.E(edge) + " crews " + st.Crews + " -> " + crews + " (" + rt.m_Phase + " f=" + RRWLog.F(rt.m_PhaseFraction) + ")");
                }
                st.Crews = crews;
            }
            st.DoneFraction = PhasePlan.EdgeDoneFraction(rt.m_Phase, rt.m_PhaseFraction, site.m_ChainLength, crews, site.ChainLo, site.ChainHi);
            var target = PhasePlan.Terrain(input);
            if (st.HideWantedRaw && target.Kind == TerrainProfileKind.Vanilla)
            {
                // Defensive: the timeline wants the road hidden but has no terrain target. Never leave the clip at the
                // surface under a hidden road: hold the reveal floor instead.
                target = TerrainProfile.Bed(RRWConst.kRevealBedY);
                RRWLog.Once("ground-hidden-vanilla-" + st.Phase, "ground: hidden works edge without a terrain target (phase " + st.Phase + "): using the reveal floor");
            }
            st.Target = target;
            TargetTY(input, target, out st.TargetT, out st.TargetY);
            st.Wanted = st.HideWantedRaw && !st.Ineligible && !st.Faulted;
        }

        // MORPH t / y of the applied edge-slot profile (RoadWorksGround.m_AppliedT/Y: the cancel-restore input).
        // Core's TerrainProfile carries the arguments it was built from, so this can never drift from PhasePlan.
        private static void TargetTY(in PlanInput s, in TerrainProfile p, out float t, out float y)
        {
            if (p.Kind == TerrainProfileKind.Bed || p.Kind == TerrainProfileKind.Morph) { t = p.MorphT; y = p.MorphY; return; }
            t = 0f; y = 0f;
        }

        private void SetDesiredTargets(GroundEdgeState st)
        {
            var em = EntityManager;
            st.Desired[0] = st.Target;
            st.DesiredT = st.TargetT;
            st.DesiredY = st.TargetY;
            Edge e = em.HasComponent<Edge>(st.Edge) ? em.GetComponentData<Edge>(st.Edge) : default;
            st.Desired[1] = NodeProfile(e.m_Start, st.Target);
            st.Desired[2] = NodeProfile(e.m_End, st.Target);
            UpdatePending(st);
        }

        private static void UpdatePending(GroundEdgeState st)
        {
            bool p = false;
            for (int s = 0; s < 3; s++)
                if (st.Clone[s] != Entity.Null && (!st.Written[s] || !st.Applied[s].SameAs(st.Desired[s]))) p = true;
            if (p) st.Pending = true;
        }

        // Node end: `dig` (the shallowest connected works profile) when every connected edge is a
        // registry edge that wants to be hidden; otherwise the edge's own target with only the clip moved (ClipOnly).
        private TerrainProfile NodeProfile(Entity node, in TerrainProfile own)
        {
            if (node == Entity.Null) return own.ClipOnly();
            if (!m_NodeMemo.TryGetValue(node, out var memo))
            {
                memo = new NodeMemo { Dig = false, Shallowest = own };
                EcsUtil.ConnectedEdges(EntityManager, node, m_Connected);
                if (m_Connected.Count > 0)
                {
                    bool dig = true;
                    bool have = false;
                    TerrainProfile best = default;
                    for (int i = 0; i < m_Connected.Count && dig; i++)
                    {
                        if (!GroundTable.Edges.TryGetValue(m_Connected[i], out var other) || other.SeenUpdate != m_Now) { dig = false; break; }
                        Eval(other);
                        if (!other.Wanted) { dig = false; break; }
                        if (!have || other.Target.Shallowness > best.Shallowness) { best = other.Target; have = true; }
                    }
                    if (dig && have) memo = new NodeMemo { Dig = true, Shallowest = best };
                }
                m_NodeMemo[node] = memo;
            }
            return memo.Dig ? memo.Shallowest : own.ClipOnly();
        }

        private void ForceRewrite(GroundEdgeState st)
        {
            for (int s = 0; s < 3; s++) st.Written[s] = false;
            st.Pending = true;
            st.Bypass = true;
        }

        private void TryClone(GroundEdgeState st, Composition comp)
        {
            var em = EntityManager;
            uint now = m_Now;
            Entity edge = st.Edge;
            for (int s = 0; s < 3; s++)
            {
                Entity cur = GroundTable.Slot(comp, s);
                Entity src = ResolveSource(cur);
                if (!EcsUtil.ValidComposition(em, cur) || !EcsUtil.ValidComposition(em, src))
                {
                    if (!st.MissLogged)
                    {
                        st.MissLogged = true;
                        GroundTable.CreationMisses++;
                        RRWLog.Warn("ground: creation-frame miss edge=" + RRWLog.E(edge) + " slot=" + GroundTable.SlotName(s) +
                                    " (composition not ready at ModificationEnd); retrying every update (fallback: rrw.hideafterground 1)");
                    }
                    return;
                }
            }
            var n = comp;
            for (int s = 0; s < 3; s++)
            {
                Entity cur = GroundTable.Slot(comp, s);
                Entity src = ResolveSource(cur);
                if (cur != src) GroundTable.QueueDestroy(cur, now);   // a stale clone of ours: replace it
                st.Src[s] = src;
                st.Clone[s] = MakeClone(src, s, edge);
                st.Written[s] = false;
                st.Applied[s] = TerrainProfile.Vanilla;
                GroundTable.SetSlot(ref n, s, st.Clone[s]);
            }
            st.Stage = GroundStage.Cloned;
            st.VanillaPending = false;
            st.IsOrphan = false;
            st.NaturalSinceUpdate = 0;
            SetDesiredTargets(st);
            st.Pending = true;
            st.Bypass = true;
            em.SetComponentData(edge, n);

            string kind = st.Target.Kind.ToString().ToLowerInvariant();
            if (st.Rebuild) m_RebuildCount++;
            else if (st.WantedSinceUpdate == now || st.HideFrameFlag)
            {
                GroundTable.CreationHits++;
                RRWLog.Info("ground: " + kind + " applied at " + (st.SiteKind == WorksKind.Construction ? "creation" : "hide") + " frame edge=" + RRWLog.E(edge) +
                            (st.Record != null ? " project=" + st.Record.ProjectId : "") + " phase=" + st.Phase + " profile=[" + st.Target + "]");
            }
            else
            {
                RRWLog.Warn("ground: " + kind + " applied " + unchecked(now - st.WantedSinceUpdate) + " updates after the hide frame edge=" + RRWLog.E(edge) +
                            " (creation-frame miss: a hole may have shown)");
            }
            st.MissLogged = false;
        }

        // A slot pointing at one of our clones resolves to that clone's vanilla source.
        private Entity ResolveSource(Entity cur)
        {
            var em = EntityManager;
            if (cur != Entity.Null && em.Exists(cur) && em.HasComponent<RRWCompositionClone>(cur))
            {
                Entity src = em.GetComponentData<RRWCompositionClone>(cur).m_Source;
                if (EcsUtil.ValidComposition(em, src)) return src;
            }
            return cur;
        }

        private Entity MakeClone(Entity src, int slot, Entity edge)
        {
            var em = EntityManager;
            Entity c = em.Instantiate(src);
            if (em.HasComponent<Created>(c)) em.RemoveComponent<Created>(c);
            if (em.HasComponent<Updated>(c)) em.RemoveComponent<Updated>(c);
            if (em.HasComponent<Deleted>(c)) em.RemoveComponent<Deleted>(c);
            var tag = new RRWCompositionClone { m_Source = src, m_Edge = edge, m_Slot = (byte)slot };
            if (em.HasComponent<RRWCompositionClone>(c)) em.SetComponentData(c, tag);
            else em.AddComponentData(c, tag);
            GroundTable.ClonesMade++;
            return c;
        }

        private bool SlotsReferenceClones(Composition comp)
        {
            var em = EntityManager;
            for (int s = 0; s < 3; s++)
            {
                Entity cur = GroundTable.Slot(comp, s);
                if (cur != Entity.Null && em.Exists(cur) && em.HasComponent<RRWCompositionClone>(cur) && !GroundTable.Abandoned.Contains(cur)) return true;
            }
            return false;
        }

        // The edge references clones but we have no clone state for it: take them over and run the hold from now.
        private void AdoptExisting(GroundEdgeState st, Composition comp)
        {
            var em = EntityManager;
            st.ClearSlots();
            for (int s = 0; s < 3; s++)
            {
                Entity cur = GroundTable.Slot(comp, s);
                if (cur == Entity.Null || !em.Exists(cur) || !em.HasComponent<RRWCompositionClone>(cur) || GroundTable.Abandoned.Contains(cur)) continue;
                var tag = em.GetComponentData<RRWCompositionClone>(cur);
                st.Clone[s] = cur;
                st.Src[s] = tag.m_Source;
                st.Applied[s] = TerrainProfile.Bed(RRWConst.kRevealBedY);   // unknown values: assume the reveal floor
                st.Desired[s] = st.Applied[s];
                st.Written[s] = false;                                       // forces the vanilla write at the hold end
                GroundTable.PendingDestroy.Remove(cur);
                if (tag.m_Edge != st.Edge)
                {
                    tag.m_Edge = st.Edge;
                    em.SetComponentData(cur, tag);
                }
            }
            st.Stage = GroundStage.RevealHold;
            st.HoldStartUpdate = m_Now;
            st.VanillaPending = false;
            st.Pending = false;
            st.AppliedT = 1f;
            st.AppliedY = RRWConst.kRevealBedY;
            if (st.SeenUpdate != m_Now) { st.IsOrphan = true; st.OrphanSinceUpdate = m_Now; }
        }

        // Compare every slot with our clone and the recorded source; re-assert, or adopt a new vanilla source.
        // Returns false when a slot is momentarily invalid (try again next update).
        private bool CheckSlots(GroundEdgeState st, ref Composition comp)
        {
            var em = EntityManager;
            uint now = m_Now;
            Entity edge = st.Edge;
            var n = comp;
            bool needTrigger = false;
            for (int s = 0; s < 3; s++)
            {
                Entity cur = GroundTable.Slot(comp, s);
                Entity clone = st.Clone[s];
                if (clone != Entity.Null && cur == clone) continue;   // steady state (owned clones are never destroyed)
                bool cloneAlive = clone != Entity.Null && EcsUtil.ValidComposition(em, clone);
                if (clone == Entity.Null && st.Stage != GroundStage.Cloned && !st.Wanted) continue;   // slot not ours (adopted partial)
                if (!EcsUtil.ValidComposition(em, cur)) return false;
                bool curIsClone = em.HasComponent<RRWCompositionClone>(cur);
                Entity curSrc = ResolveSource(cur);
                if (cloneAlive && curSrc == st.Src[s])
                {
                    // vanilla reset to the same source (Mod3 CompositionSelectSystem on an Updated edge): re-assert
                    GroundTable.SetSlot(ref n, s, clone);
                    st.Repoints++;
                    if (curIsClone && cur != clone) GroundTable.QueueDestroy(cur, now);
                    needTrigger = true;
                    continue;
                }
                // Foreign composition (upgrade / new connection / slot never cloned): adopt it as the new source.
                if (!EcsUtil.ValidComposition(em, curSrc)) return false;
                if (s == 0 && !st.Ineligible && !EcsUtil.DigEligible(em, edge, out string why))
                {
                    st.Ineligible = true;
                    st.IneligibleCheckUpdate = now + kIneligibleRecheckUpdates;
                    RRWLog.Warn("ground: edge " + RRWLog.E(edge) + " adopted a composition that is not eligible for excavation (" + why +
                                "): reveal hold, then vanilla (the Director should switch the site to Minimal)");
                }
                if (clone != Entity.Null) GroundTable.QueueDestroy(clone, now);
                if (curIsClone) GroundTable.QueueDestroy(cur, now);
                st.Src[s] = curSrc;
                st.Clone[s] = MakeClone(curSrc, s, edge);
                st.Written[s] = false;
                st.Applied[s] = TerrainProfile.Vanilla;
                if (st.Stage == GroundStage.Cloned && st.Desired[s].Kind == TerrainProfileKind.Vanilla && !st.Wanted)
                    st.Desired[s] = TerrainProfile.Bed(RRWConst.kRevealBedY);
                GroundTable.SetSlot(ref n, s, st.Clone[s]);
                st.Pending = true;
                st.Bypass = true;
                st.Reclones++;
                st.Adoptions++;
                GroundTable.Adoptions++;
                RRWLog.Info("ground: edge " + RRWLog.E(edge) + " slot=" + GroundTable.SlotName(s) + " adopted new composition " + RRWLog.E(curSrc) +
                            " (upgrade / reconnection) stage=" + st.Stage);
            }
            if (!GroundTable.Same(n, comp))
            {
                em.SetComponentData(edge, n);
                comp = n;
            }
            if (needTrigger) AddTrigger(edge);
            return true;
        }

        // Cloned -> RevealHold (road visible again, or the site is gone): keep the clones; never let the floor be deeper
        // than kRevealBedY or the ground poke through the road surface (Natural/Morph brackets): Bed(kRevealBedY).
        private void EnterHold(GroundEdgeState st)
        {
            st.Stage = GroundStage.RevealHold;
            st.HoldStartUpdate = m_Now;
            st.VanillaPending = false;
            for (int s = 0; s < 3; s++)
            {
                if (st.Clone[s] == Entity.Null) continue;
                st.Desired[s] = HoldProfile(st.Written[s] ? st.Applied[s] : TerrainProfile.Vanilla);
            }
            if (st.Desired[0].Kind == TerrainProfileKind.Bed && !(st.Written[0] && st.Applied[0].SameAs(st.Desired[0])))
            {
                st.DesiredT = 1f;
                st.DesiredY = st.Desired[0].Min.y;
            }
            else
            {
                st.DesiredT = st.AppliedT;
                st.DesiredY = st.AppliedY;
            }
            st.Pending = false;
            UpdatePending(st);
            if (st.Pending) st.Bypass = true;
            RRWLog.Verbose("ground edge " + RRWLog.E(st.Edge) + " reveal hold starts (applied " + st.Applied[0].Kind + ", " + (st.Pending ? "floor raised to " + RRWLog.F(RRWConst.kRevealBedY) : "unchanged") + ")");
        }

        private static TerrainProfile HoldProfile(in TerrainProfile applied)
        {
            switch (applied.Kind)
            {
                case TerrainProfileKind.Bed:
                    return applied.Max.y >= RRWConst.kRevealBedY - 1e-4f ? applied : TerrainProfile.Bed(RRWConst.kRevealBedY);
                case TerrainProfileKind.ClipOnly:
                    return applied;
                default:
                    return TerrainProfile.Bed(RRWConst.kRevealBedY);
            }
        }

        // Restoring -> Vanilla: the vanilla values have been on the clones for >= 1 update; repoint to the sources.
        private void FinishRestore(GroundEdgeState st, Composition comp)
        {
            var em = EntityManager;
            uint now = m_Now;
            var n = comp;
            for (int s = 0; s < 3; s++)
            {
                Entity clone = st.Clone[s];
                if (clone == Entity.Null) continue;
                Entity cur = GroundTable.Slot(comp, s);
                if (cur == clone)
                {
                    Entity src = st.Src[s];
                    if (!EcsUtil.ValidComposition(em, src) && em.Exists(clone) && em.HasComponent<RRWCompositionClone>(clone))
                        src = em.GetComponentData<RRWCompositionClone>(clone).m_Source;
                    if (!EcsUtil.ValidComposition(em, src))
                    {
                        // Cannot happen with vanilla (compositions are never destroyed); the clone carries vanilla values,
                        // so keeping it referenced is harmless. Abandon it rather than block the site forever.
                        GroundTable.Abandoned.Add(clone);
                        RRWLog.Once("ground-abandon", "ground: source composition of a clone vanished; the clone is kept (vanilla values) edge=" + RRWLog.E(st.Edge));
                        continue;
                    }
                    GroundTable.SetSlot(ref n, s, src);
                }
                GroundTable.QueueDestroy(clone, now);
            }
            if (!GroundTable.Same(n, comp)) em.SetComponentData(st.Edge, n);
            st.ClearSlots();
            st.Stage = GroundStage.Vanilla;
            st.NaturalSinceUpdate = 0;
            st.IsOrphan = false;
            RRWLog.Verbose("ground edge " + RRWLog.E(st.Edge) + " restored to vanilla compositions (settled)");
        }

        private void Write(GroundEdgeState st)
        {
            var em = EntityManager;
            uint now = m_Now;
            bool changed = false;
            for (int s = 0; s < 3; s++)
            {
                Entity clone = st.Clone[s];
                if (clone == Entity.Null || !em.Exists(clone)) continue;
                if (st.Written[s] && st.Applied[s].SameAs(st.Desired[s])) continue;
                WriteTerrainComposition(clone, st.Src[s], st.Desired[s]);
                st.Applied[s] = st.Desired[s];
                st.Written[s] = true;
                changed = true;
            }
            st.AppliedT = st.DesiredT;
            st.AppliedY = st.DesiredY;
            if (changed)
            {
                AddTrigger(st.Edge);
                st.StepUpdate = now;
                st.Triggers++;
            }
            if (st.Applied[0].Kind == TerrainProfileKind.Natural) { if (st.NaturalSinceUpdate == 0) st.NaturalSinceUpdate = now == 0 ? 1u : now; }
            else st.NaturalSinceUpdate = 0;
            st.Pending = false;
            st.Bypass = false;
            if (st.VanillaPending)
            {
                st.VanillaPending = false;
                st.Stage = GroundStage.Restoring;
                st.RestoreWrittenUpdate = now;
            }
        }

        // vanilla TerrainComposition of the source (default when it has none; verified in game: hasTC = 0) + profile offsets.
        private void WriteTerrainComposition(Entity clone, Entity src, in TerrainProfile p)
        {
            var em = EntityManager;
            TerrainComposition v = default;
            if (src != Entity.Null && em.Exists(src) && em.HasComponent<TerrainComposition>(src)) v = em.GetComponentData<TerrainComposition>(src);
            var r = p.Apply(v);
            if (em.HasComponent<TerrainComposition>(clone)) em.SetComponentData(clone, r);
            else em.AddComponentData(clone, r);
        }

        private void DropDead(GroundEdgeState st)
        {
            for (int s = 0; s < 3; s++) if (st.Clone[s] != Entity.Null) GroundTable.QueueDestroy(st.Clone[s], m_Now);
            if (!m_Remove.Contains(st.Edge)) m_Remove.Add(st.Edge);
        }

        // Last resort for an edge that keeps throwing: repoint whatever we can to the sources, trigger, forget it.
        private void EmergencyDrop(GroundEdgeState st)
        {
            var em = EntityManager;
            try
            {
                if (EcsUtil.Alive(em, st.Edge) && em.HasComponent<Composition>(st.Edge))
                {
                    var comp = em.GetComponentData<Composition>(st.Edge);
                    var n = comp;
                    for (int s = 0; s < 3; s++)
                    {
                        if (st.Clone[s] == Entity.Null || GroundTable.Slot(comp, s) != st.Clone[s]) continue;
                        if (EcsUtil.ValidComposition(em, st.Src[s])) GroundTable.SetSlot(ref n, s, st.Src[s]);
                    }
                    if (!GroundTable.Same(n, comp)) em.SetComponentData(st.Edge, n);
                    AddTrigger(st.Edge);
                }
            }
            catch (Exception e) { RRWLog.ErrorOnce("ground emergency restore", e); }
            for (int s = 0; s < 3; s++) if (st.Clone[s] != Entity.Null) GroundTable.QueueDestroy(st.Clone[s], m_Now);
            if (!m_Remove.Contains(st.Edge)) m_Remove.Add(st.Edge);
            RRWLog.Warn("ground edge " + RRWLog.E(st.Edge) + " dropped after " + st.Fails + " failing updates (emergency restore to vanilla compositions)");
        }

        // ================================================================ output

        // The applied MORPH t / y only advanced on writes, and a write only happens when the QUANTISED profile changes
        // (1/16 m steps, TerrainProfile.SameAs). Near the end of D2 Morph(t) and Morph(0) quantise to the same values for
        // t up to a few %, so the last write usually happened at t > 0 and m_AppliedT stayed there for good while the
        // edge already carried exactly the Morph(0) composition: the Director (waiting for Morph && t == 0) never saw
        // it and timed out after kCompletionTimeoutUpdates (a bug seen in testing). When the edge slot's applied
        // profile IS the target profile, the target's arguments describe it exactly: adopt them. Nothing is written,
        // nothing is triggered, the terrain is unchanged (no pop). Also keeps the cancel-restore input exact.
        private void SyncAppliedArgs(GroundEdgeState st)
        {
            if (st.Stage != GroundStage.Cloned || !st.Wanted || st.TargetUpdate != m_Now) return;
            if (st.Clone[0] == Entity.Null || !st.Written[0] || !st.Applied[0].SameAs(st.Target)) return;
            st.AppliedT = st.TargetT;
            st.AppliedY = st.TargetY;
        }

        // Director's demolition delete condition (DirectorCompletion.EdgeReadyToFinish): Morph at t = 0, or settled.
        private static bool DemolitionOutputReady(in RoadWorksGround g) =>
            (g.m_Kind == TerrainProfileKind.Morph && g.m_AppliedT <= 1e-3f) || g.Settled;

        private void NoteDemolitionReady(EntityManager em, GroundEdgeState st)
        {
            if (st.CompleteSinceUpdate == 0 || st.DemoReadyLogged || !em.HasComponent<RoadWorksGround>(st.Edge)) return;
            var g = em.GetComponentData<RoadWorksGround>(st.Edge);
            if (!DemolitionOutputReady(g)) return;
            st.DemoReadyLogged = true;
            uint wait = unchecked(m_Now - st.CompleteSinceUpdate);
            GroundTable.DemolitionReady++;
            if (wait > GroundTable.DemolitionReadyMaxWait) GroundTable.DemolitionReadyMaxWait = wait;
            RRWLog.Info("ground: demolition edge " + RRWLog.E(st.Edge) + (st.Record != null ? " project=" + st.Record.ProjectId : "") +
                        " at Morph(0) (natural, ready to delete) " + wait + " updates after Complete; last terrain step " +
                        unchecked(m_Now - st.StepUpdate) + " updates ago, profile=[" + st.Applied[0] + "]");
        }

        private void WriteOutput(EntityManager em, GroundEdgeState st)
        {
            Entity edge = st.Edge;
            if (!em.HasComponent<RoadWorksGround>(edge)) return;
            var g = em.GetComponentData<RoadWorksGround>(edge);
            var o = g;
            if (st.Stage == GroundStage.Vanilla)
            {
                o.m_Kind = TerrainProfileKind.Vanilla;
                o.m_Flags = GroundFlags.None;
                o.m_FloorMinRel = 0f;
                o.m_FloorMaxRel = 0f;
                o.m_AppliedY = 0f;
                o.m_AppliedT = 0f;
            }
            else
            {
                var a = st.Applied[0];
                o.m_Kind = a.Kind;
                var f = GroundFlags.Clones;
                if (st.Stage == GroundStage.RevealHold) f |= GroundFlags.RevealHold;
                if (st.Stage == GroundStage.Restoring) f |= GroundFlags.Restoring;
                o.m_Flags = f;
                o.m_FloorMinRel = a.MiddleCap;
                o.m_FloorMaxRel = a.MiddleFloor;
                o.m_AppliedY = st.AppliedY;
                o.m_AppliedT = st.AppliedT;
            }
            o.m_StepUpdate = st.StepUpdate;
            o.m_HoldStartUpdate = st.HoldStartUpdate;
            if (o.m_Kind != g.m_Kind || o.m_Flags != g.m_Flags || o.m_FloorMinRel != g.m_FloorMinRel || o.m_FloorMaxRel != g.m_FloorMaxRel ||
                o.m_AppliedY != g.m_AppliedY || o.m_AppliedT != g.m_AppliedT || o.m_StepUpdate != g.m_StepUpdate || o.m_HoldStartUpdate != g.m_HoldStartUpdate)
                em.SetComponentData(edge, o);
        }

        // ================================================================ natural ground

        private void SampleNatural(EntityManager em, uint now)
        {
            int count = m_States.Count;
            if (count == 0) return;
            for (int k = 0; k < count; k++)
            {
                int i = (m_NatCursor + k) % count;
                var st = m_States[i];
                if (st.SeenUpdate != now || m_Remove.Contains(st.Edge)) continue;
                if (TrySample(em, st, now)) { m_NatCursor = (i + 1) % count; return; }
            }
        }

        // Returns true when it spent this update's sampling budget.
        private bool TrySample(EntityManager em, GroundEdgeState st, uint now)
        {
            Entity edge = st.Edge;
            if (!em.HasComponent<RoadWorksSite>(edge) || !em.HasComponent<RoadWorksRuntime>(edge)) return false;
            var site = em.GetComponentData<RoadWorksSite>(edge);
            if (site.Mode != VisualMode.FullDig) return false;
            var rt = em.GetComponentData<RoadWorksRuntime>(edge);
            if (site.Kind == WorksKind.Construction)
            {
                if (st.NatCpuDone) return false;
                if (site.Has(SiteFlags.NaturalSampled) || site.Has(SiteFlags.Replaced)) { st.NatCpuDone = true; return false; }
                if (rt.m_Phase != WorksPhase.Survey) { st.NatCpuDone = true; return false; }   // too late: never change brackets mid-morph
                if (st.Stage != GroundStage.Cloned || !st.Written[0] || st.Applied[0].Kind != TerrainProfileKind.Natural || st.NaturalSinceUpdate == 0) return false;
                if (unchecked(now - st.NaturalSinceUpdate) < RRWConst.kNaturalSampleDelayUpdates) return false;
                if (!NaturalSampler.TryGeometry(em, edge, st.Record, rt.m_GradeOffset, out var geo)) return false;
                if (!NaturalSampler.SampleCpu(m_Terrain, geo, true, true, out float above, out float below, out int n))
                {
                    st.NaturalSinceUpdate = now;   // heights not available yet: try again later
                    return true;
                }
                WriteNatural(em, edge, above, below, false, "cpu centre+verge n=" + n);
                st.NatCpuDone = true;
                return true;
            }
            // demolition: verge (CPU) + GPU base at D0 start, GPU retry at D1 start; never once D2 started (would pop)
            if (rt.m_Phase == WorksPhase.BreakUp && !st.NatD0Done)
            {
                st.NatD0Done = true;
                if (!NaturalSampler.TryGeometry(em, edge, st.Record, rt.m_GradeOffset, out var geo)) return true;
                if (NaturalSampler.SampleCpu(m_Terrain, geo, false, true, out float above, out float below, out int n))
                    WriteNatural(em, edge, above, below, true, "cpu verge n=" + n);
                if (!st.GpuInFlight && NaturalSampler.RequestGpu(m_Terrain, edge, geo, rt.m_Phase)) st.GpuInFlight = true;
                return true;
            }
            if (rt.m_Phase == WorksPhase.Removal && !st.NatD1Done)
            {
                st.NatD0Done = true;
                st.NatD1Done = true;
                if (!NaturalSampler.TryGeometry(em, edge, st.Record, rt.m_GradeOffset, out var geo)) return true;
                if (!site.NaturalValid && NaturalSampler.SampleCpu(m_Terrain, geo, false, true, out float above, out float below, out int n))
                    WriteNatural(em, edge, above, below, true, "cpu verge n=" + n);
                if (!st.GpuInFlight && NaturalSampler.RequestGpu(m_Terrain, edge, geo, rt.m_Phase)) st.GpuInFlight = true;
                return true;
            }
            return false;
        }

        private void DrainNaturalResults(EntityManager em)
        {
            m_NatResults.Clear();
            lock (GroundTable.NaturalLock)
            {
                if (GroundTable.NaturalResults.Count == 0) return;
                m_NatResults.AddRange(GroundTable.NaturalResults);
                GroundTable.NaturalResults.Clear();
            }
            for (int i = 0; i < m_NatResults.Count; i++)
            {
                var r = m_NatResults[i];
                if (GroundTable.Edges.TryGetValue(r.Edge, out var st)) st.GpuInFlight = false;
                if (float.IsNaN(r.Above) || float.IsNaN(r.Below)) continue;
                GroundTable.GpuResults++;
                if (!EcsUtil.Alive(em, r.Edge) || !em.HasComponent<RoadWorksSite>(r.Edge) || !em.HasComponent<RoadWorksRuntime>(r.Edge)) continue;
                var site = em.GetComponentData<RoadWorksSite>(r.Edge);
                var rt = em.GetComponentData<RoadWorksRuntime>(r.Edge);
                // brackets are only used from D2 on: a late result must never change a running morph
                if (site.Kind != WorksKind.Demolition || (rt.m_Phase != WorksPhase.BreakUp && rt.m_Phase != WorksPhase.Removal)) continue;
                WriteNatural(em, r.Edge, r.Above, r.Below, true, r.Source);
            }
        }

        // Ground may write ONLY m_NatAbove, m_NatBelow and NaturalSampled on the saved site. Read-modify-write.
        private static void WriteNatural(EntityManager em, Entity edge, float above, float below, bool union, string source)
        {
            if (float.IsNaN(above) || float.IsNaN(below)) return;
            var site = em.GetComponentData<RoadWorksSite>(edge);
            float a = math.clamp(above, 0f, 100f), b = math.clamp(below, 0f, 100f);
            if (union && site.NaturalValid) { a = math.max(a, site.m_NatAbove); b = math.max(b, site.m_NatBelow); }
            site.m_NatAbove = a;
            site.m_NatBelow = b;
            site.Set(SiteFlags.NaturalSampled, true);
            em.SetComponentData(edge, site);
            RRWLog.Verbose("ground natural sampled edge=" + RRWLog.E(edge) + " above=" + RRWLog.F(a) + " below=" + RRWLog.F(b) + " (" + source + ")");
        }

        // ================================================================ dev hooks (always registered; called by rrw.dump / rrw.check)

        private string Dump(EntityManager em, Entity edge)
        {
            var sb = new StringBuilder();
            if (!GroundTable.Edges.TryGetValue(edge, out var st))
            {
                sb.Append("ground: no state (vanilla, never cloned)");
            }
            else
            {
                uint now = RRWClock.UpdateIndex;
                sb.Append("ground stage=").Append(st.Stage)
                  .Append(" wanted=").Append(st.Wanted ? 1 : 0)
                  .Append(" target=[").Append(st.Target).Append(']')
                  .Append(" appliedT=").Append(RRWLog.F(st.AppliedT)).Append(" appliedY=").Append(RRWLog.F(st.AppliedY))
                  .Append(" pending=").Append(st.Pending ? 1 : 0)
                  .Append(st.PendingSinceUpdate != 0 ? "(" + unchecked(now - st.PendingSinceUpdate) + " upd)" : "")
                  .Append(" crews=").Append(st.Crews).Append(" done=").Append(RRWLog.F(st.DoneFraction)).Append(" crewChanges=").Append(st.CrewChanges)
                  .Append(" step=").Append(st.StepUpdate).Append(" (").Append(unchecked(now - st.StepUpdate)).Append(" upd ago)");
                if (st.Stage == GroundStage.RevealHold || st.Stage == GroundStage.Restoring)
                    sb.Append(" holdAge=").Append(unchecked(now - st.HoldStartUpdate)).Append('/').Append(RRWConst.kRevealHoldUpdates).Append(" vanillaPending=").Append(st.VanillaPending ? 1 : 0);
                if (st.CompleteSinceUpdate != 0) sb.Append(" demoComplete=").Append(unchecked(now - st.CompleteSinceUpdate)).Append(" upd ready=").Append(st.DemoReadyLogged ? 1 : 0);
                if (st.IsOrphan) sb.Append(" ORPHAN since ").Append(unchecked(now - st.OrphanSinceUpdate)).Append(" upd");
                if (st.Ineligible) sb.Append(" INELIGIBLE");
                if (st.Faulted) sb.Append(" FAULTED");
                sb.Append(" triggers=").Append(st.Triggers).Append(" repoints=").Append(st.Repoints).Append(" adoptions=").Append(st.Adoptions)
                  .Append(" nat(cpu=").Append(st.NatCpuDone ? 1 : 0).Append(",d0=").Append(st.NatD0Done ? 1 : 0).Append(",d1=").Append(st.NatD1Done ? 1 : 0).Append(",gpu=").Append(st.GpuInFlight ? "busy" : "idle").Append(')');
                Composition c = em.HasComponent<Composition>(edge) ? em.GetComponentData<Composition>(edge) : default;
                for (int s = 0; s < 3; s++)
                {
                    Entity cur = GroundTable.Slot(c, s);
                    sb.Append("\n  slot ").Append(GroundTable.SlotName(s))
                      .Append(" cur=").Append(RRWLog.E(cur)).Append(cur != Entity.Null && cur == st.Clone[s] ? "(clone)" : "")
                      .Append(" src=").Append(RRWLog.E(st.Src[s]))
                      .Append(" clone=").Append(RRWLog.E(st.Clone[s]))
                      .Append(" written=").Append(st.Written[s] ? 1 : 0)
                      .Append(" applied=[").Append(st.Applied[s]).Append(']');
                    if (st.Pending) sb.Append(" desired=[").Append(st.Desired[s]).Append(']');
                }
            }
            if (em.HasComponent<RoadWorksGround>(edge))
            {
                var g = em.GetComponentData<RoadWorksGround>(edge);
                sb.Append("\n  output kind=").Append(g.m_Kind).Append(" flags=").Append(g.m_Flags).Append(" settled=").Append(g.Settled ? 1 : 0)
                  .Append(" floor=[").Append(RRWLog.F(g.m_FloorMaxRel)).Append(',').Append(RRWLog.F(g.m_FloorMinRel)).Append(']')
                  .Append(" t=").Append(RRWLog.F(g.m_AppliedT)).Append(" y=").Append(RRWLog.F(g.m_AppliedY));
            }
            if (em.HasComponent<RoadWorksSite>(edge))
            {
                var site = em.GetComponentData<RoadWorksSite>(edge);
                sb.Append("\n  natural sampled=").Append(site.Has(SiteFlags.NaturalSampled) ? 1 : 0)
                  .Append(" above=").Append(RRWLog.F(site.m_NatAbove)).Append(" below=").Append(RRWLog.F(site.m_NatBelow));
            }
            return sb.ToString();
        }

        private void Check(EntityManager em, List<string> problems)
        {
            uint now = RRWClock.UpdateIndex;
            foreach (var kv in SiteRegistry.Edges)
            {
                Entity edge = kv.Key;
                if (!EcsUtil.Alive(em, edge) || !em.HasComponent<RoadWorksRuntime>(edge) || !em.HasComponent<RoadWorksSite>(edge)) continue;
                var rt = em.GetComponentData<RoadWorksRuntime>(edge);
                var site = em.GetComponentData<RoadWorksSite>(edge);
                if (!rt.HideWanted || site.Mode != VisualMode.FullDig) continue;
                if (!GroundTable.Edges.TryGetValue(edge, out var st)) { problems.Add("ground: HideWanted edge " + RRWLog.E(edge) + " has no ground state"); continue; }
                if (st.Ineligible || st.Faulted) { problems.Add("ground: HideWanted edge " + RRWLog.E(edge) + " is " + (st.Faulted ? "faulted" : "ineligible") + " (no trench)"); continue; }
                if (unchecked(now - st.CreatedUpdate) < 2) continue;
                if (st.Stage != GroundStage.Cloned) { problems.Add("ground: HideWanted edge " + RRWLog.E(edge) + " stage=" + st.Stage + " (expected Cloned)"); continue; }
                if (!em.HasComponent<Composition>(edge)) continue;
                var c = em.GetComponentData<Composition>(edge);
                for (int s = 0; s < 3; s++)
                {
                    Entity cur = GroundTable.Slot(c, s);
                    if (cur != st.Clone[s] || !em.HasComponent<RRWCompositionClone>(cur))
                        problems.Add("ground: HideWanted edge " + RRWLog.E(edge) + " slot " + GroundTable.SlotName(s) + " is not its clone (" + RRWLog.E(cur) + ")");
                }
            }
            int holdLimit = RRWConst.kRevealHoldUpdates + kVanillaWriteMaxWaitUpdates + 10;
            int demoLimit = RRWConst.kTerrainTriggerMinUpdates + 5;
            foreach (var st in GroundTable.Edges.Values)
            {
                if (st.CompleteSinceUpdate != 0 && unchecked(now - st.CompleteSinceUpdate) > demoLimit && EcsUtil.Alive(em, st.Edge) && em.HasComponent<RoadWorksGround>(st.Edge))
                {
                    var g = em.GetComponentData<RoadWorksGround>(st.Edge);
                    if (!DemolitionOutputReady(g))
                        problems.Add("ground: demolition edge " + RRWLog.E(st.Edge) + " completed " + unchecked(now - st.CompleteSinceUpdate) + " updates ago but output is kind=" + g.m_Kind +
                                     " t=" + RRWLog.F(g.m_AppliedT) + " stage=" + st.Stage + " (the Director deletes at Morph t=0; expected within " + demoLimit + " updates)");
                }
                if (st.IsOrphan && unchecked(now - st.OrphanSinceUpdate) > RRWConst.kOrphanCloneMaxUpdates)
                    problems.Add("ground: non-site edge " + RRWLog.E(st.Edge) + " still references clones after " + unchecked(now - st.OrphanSinceUpdate) + " updates (stage " + st.Stage + ")");
                // A Cloned edge whose new depth waits longer than a few trigger periods (cap starvation)
                if (st.Stage == GroundStage.Cloned && st.PendingSinceUpdate != 0 && unchecked(now - st.PendingSinceUpdate) > 3 * RRWConst.kTerrainTriggerMinUpdates + 5)
                    problems.Add("ground: R4 edge " + RRWLog.E(st.Edge) + " has waited " + unchecked(now - st.PendingSinceUpdate) + " updates for its terrain write (trigger cap "
                                 + RRWConst.kTerrainTriggerMinUpdates + ")");
                if (st.Stage == GroundStage.RevealHold && unchecked(now - st.HoldStartUpdate) > holdLimit)
                    problems.Add("ground: edge " + RRWLog.E(st.Edge) + " stuck in the reveal hold for " + unchecked(now - st.HoldStartUpdate) + " updates");
            }
            try
            {
                int unowned = m_Gc.CountUnowned(em);
                if (unowned > 0) problems.Add("ground: " + unowned + " clone entities are neither owned nor queued for destruction (sweep picks them up within " + kSweepIntervalUpdates + " updates)");
            }
            catch (Exception e) { problems.Add("ground: clone count failed: " + e.Message); }
        }
    }
}
