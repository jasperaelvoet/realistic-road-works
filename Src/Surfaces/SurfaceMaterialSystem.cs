using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;
using Game.Rendering;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace RealisticRoadWorks.V3.Surfaces
{
    // Reflection access to AreaBatchSystem's managed batch list.
    // ManagedBatchData is a private CLASS, so the Material reference read here is the live one AreaRenderSystem draws.
    internal static class SurfaceBatch
    {
        private static FieldInfo s_List, s_Mat, s_Prio;
        private static bool s_Broken;

        public struct Entry
        {
            public int Index;
            public Material Material;
            public int Priority;
        }

        static IList List(AreaBatchSystem abs)
        {
            if (abs == null || s_Broken) return null;
            if (s_List == null)
            {
                s_List = typeof(AreaBatchSystem).GetField("m_ManagedBatchData", BindingFlags.NonPublic | BindingFlags.Instance);
                if (s_List == null) { s_Broken = true; RRWLog.Warn("surfaces: AreaBatchSystem.m_ManagedBatchData not found: cover layers cannot hide lane markings"); return null; }
            }
            return s_List.GetValue(abs) as IList;
        }

        static bool Read(object o, int index, out Entry e)
        {
            e = default;
            if (o == null) return false;
            if (s_Mat == null)
            {
                var t = o.GetType();
                s_Mat = t.GetField("m_Material", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                s_Prio = t.GetField("m_RendererPriority", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (s_Mat == null) { s_Broken = true; return false; }
            }
            e.Index = index;
            e.Material = s_Mat.GetValue(o) as Material;
            e.Priority = s_Prio != null ? (int)s_Prio.GetValue(o) : 0;
            return e.Material != null;
        }

        public static string BatchName(string prefabName) => "Area batch (" + prefabName + ")";

        // The batch of a clone: RenderedAreaData.m_BatchIndex first (verified by the material name), else a full scan
        // (AreaBatchSystem remaps the batches when one is removed).
        public static bool Find(AreaBatchSystem abs, EntityManager em, SurfaceClone c, out Entry e)
        {
            e = default;
            var list = List(abs);
            if (list == null || c == null) return false;
            string want = BatchName(c.Spec.Name);
            if (em.Exists(c.Entity) && em.HasComponent<RenderedAreaData>(c.Entity))
            {
                int bi = em.GetComponentData<RenderedAreaData>(c.Entity).m_BatchIndex;
                if (bi >= 0 && bi < list.Count && Read(list[bi], bi, out e) && e.Material.name == want) return true;
            }
            for (int i = 0; i < list.Count; i++)
                if (Read(list[i], i, out e) && e.Material.name == want) return true;
            e = default;
            return false;
        }

        // Cheap check that index still holds this material instance.
        public static bool StillAt(AreaBatchSystem abs, int index, Material m)
        {
            var list = List(abs);
            if (list == null || index < 0 || index >= list.Count) return false;
            return Read(list[index], index, out var e) && ReferenceEquals(e.Material, m);
        }

        // Dev override (rrw.surf.line prio=): ManagedBatchData.m_RendererPriority is read every frame by
        // AreaBatchSystem.GetAreaBatch, so writing it takes effect on the next draw.
        public static bool SetPriority(AreaBatchSystem abs, int index, int prio)
        {
            var list = List(abs);
            if (list == null || s_Prio == null || index < 0 || index >= list.Count || list[index] == null) return false;
            s_Prio.SetValue(list[index], prio);
            return true;
        }

        // HDRP draws mesh decals only for queues 1000..2500 (k_RenderQueue_AllOpaque); outside, the decal disappears.
        public static int ClampQueue(int q) => math.clamp(q, 1000, 2500);
    }

    // SurfaceMaterialSystem (PreCulling After AreaBatchSystem, order 610). Raises the render queue of the two
    // Cover layers ("RRW Base Course Cover", "RRW Fresh Asphalt Cover") by kRenderQueueRaise (2000 -> 2100, clamped to
    // 2500) so they sort after the lane markings and hide them until painted (verified in game: they sort after ALL markings).
    // The raise is per layer (SurfaceState.QueueRaiseOf): covers +100, the yellow TempMarking variants
    // +RRWGates.TempLineQueueRaise (2200: over the markings AND the covers), rrw.surf.line test clones their override; capped at
    // 2500. It also re-asserts the line clones' roundness in the batch material (colossal_AreaParameters.x =
    // clamp(r,.01,.99) * 0.75, as AreaBatchSystem computes it) and a dev renderer-priority override. Switch changes apply within a
    // frame (the per-frame check compares the wanted values).
    // A cheap per-frame check re-applies immediately when the batch material instance changed (batches rebuilt/remapped);
    // a full lookup runs every kSelfHealInterval updates.
    [RegisterSystem(SystemUpdatePhase.PreCulling, After = typeof(AreaBatchSystem), Order = RRWOrder.SurfaceMaterial)]
    public partial class SurfaceMaterialSystem : GameSystemBase
    {
        private sealed class Target
        {
            public SurfaceClone Clone;
            public Material Mat;
            public int Index = -1;
            public int OrigQueue;
            public int Applied;
            public float AppliedParam = float.NaN;   // colossal_AreaParameters.x written (roundness-managed clones)
            public int AppliedPrio = int.MinValue;   // renderer priority written (dev override), MinValue = untouched
        }

        private static readonly int s_AreaParameters = Shader.PropertyToID("colossal_AreaParameters");

        // colossal_AreaParameters.x of a roundness (AreaBatchSystem: clamp(r, .01, .99) * minNodeDistance, Surface = 0.75)
        internal static float ParamOf(float roundness) =>
            math.clamp(roundness, 0.01f, 0.99f) * Game.Areas.AreaUtils.GetMinNodeDistance(Game.Areas.AreaType.Surface);

        static int WantedQueue(Target t) => SurfaceBatch.ClampQueue(t.OrigQueue + SurfaceState.QueueRaiseOf(t.Clone.Spec));

        static int WantedPrio(Target t)
        {
            int o = SurfaceState.PriorityOverrideOf(t.Clone.Spec);
            if (o != int.MinValue) return o;
            return t.AppliedPrio == int.MinValue ? int.MinValue : t.Clone.Spec.Priority;   // restore once after an override
        }

        static bool StyleOk(Target t)
        {
            float r = SurfaceState.RoundnessOf(t.Clone.Spec);
            if (!float.IsNaN(r) && t.AppliedParam != ParamOf(r)) return false;
            int p = WantedPrio(t);
            return p == int.MinValue || p == t.AppliedPrio;
        }

        private AreaBatchSystem m_AreaBatch;
        private readonly RRWGuard m_Guard = new RRWGuard("surfaces material");
        private Target[] m_Targets;
        private int m_TargetClones = -1;
        private uint m_LastFull;
        private bool m_ForceFull = true;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AreaBatch = World.GetOrCreateSystemManaged<AreaBatchSystem>();
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            // Our clone batches outlive a city. Keep the raise (areas respawn in the first frame after the load and must
            // already hide markings); just re-validate the material instances on the next frame.
            m_ForceFull = true;
            m_Guard.Reset();
        }

        protected override void OnDestroy()
        {
            try
            {
                if (m_Targets != null)
                    foreach (var t in m_Targets)
                        if (t.Mat != null && t.Applied != 0) t.Mat.renderQueue = t.OrigQueue;
            }
            catch { }
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted || !SurfaceState.Registered) return;
            var gm = Game.SceneFlow.GameManager.instance;
            if (gm == null || gm.gameMode != GameMode.Game) return;
            try
            {
                RRWClock.Update(World);
                if (m_Targets == null || m_TargetClones != SurfaceState.Clones.Count) BuildTargets();
                uint now = RRWClock.UpdateIndex;
                bool full = m_ForceFull || now - m_LastFull >= RRWConst.kSelfHealInterval;
                for (int i = 0; i < m_Targets.Length; i++)
                {
                    var t = m_Targets[i];
                    // cheap per-frame check; a missing batch is only searched for on the full passes
                    if (!full && (t.Mat == null || (t.Mat.renderQueue == t.Applied && t.Applied == WantedQueue(t) && StyleOk(t)
                                                    && SurfaceBatch.StillAt(m_AreaBatch, t.Index, t.Mat)))) continue;
                    Apply(t);
                }
                if (full) { m_LastFull = now; m_ForceFull = false; }
                m_Guard.Ok();
            }
            catch (Exception e) { m_Guard.Fail(e); }
        }

        private void BuildTargets()
        {
            int n = 0;
            foreach (var c in SurfaceState.Clones.Values) if (c.Spec.RaisedQueue) n++;
            m_Targets = new Target[n];
            int k = 0;
            foreach (var c in SurfaceState.Clones.Values) if (c.Spec.RaisedQueue) m_Targets[k++] = new Target { Clone = c };
            m_TargetClones = SurfaceState.Clones.Count;
            m_ForceFull = true;
        }

        private void Apply(Target t)
        {
            if (!SurfaceBatch.Find(m_AreaBatch, EntityManager, t.Clone, out var e)) { t.Mat = null; t.Index = -1; return; }
            var m = e.Material;
            if (!ReferenceEquals(t.Mat, m))
            {
                t.Mat = m;
                t.OrigQueue = m.shader != null ? m.shader.renderQueue : m.renderQueue;   // the queue AreaBatchSystem starts from
                t.AppliedParam = float.NaN;
                if (t.AppliedPrio != int.MinValue) t.AppliedPrio = e.Priority;            // a rebuilt batch carries the registered prio
                RRWLog.Verbose("surfaces: material \"" + m.name + "\" captured queue=" + m.renderQueue + " shaderQueue=" + t.OrigQueue);
            }
            t.Index = e.Index;
            var spec = t.Clone.Spec;
            int q = WantedQueue(t);
            if (m.renderQueue != q)
            {
                m.renderQueue = q;
                RRWLog.Once("surf-queue-" + spec.Name + "-" + q, "surfaces: render queue of \"" + spec.Name + "\" " + t.OrigQueue + " -> " + q
                            + " (raise +" + SurfaceState.QueueRaiseOf(spec) + (spec.TempSource >= 0 ? ", yellow lines over markings and covers)" : spec.TestLine ? ", test line)" : ", covers lane markings)"));
            }
            t.Applied = q;
            // Roundness of the line clones in the batch material (the prefab-side expand is SurfaceStyle's job)
            float r = SurfaceState.RoundnessOf(spec);
            if (!float.IsNaN(r))
            {
                float x = ParamOf(r);
                Vector4 cur = m.GetVector(s_AreaParameters);
                if (cur.x != x)
                {
                    cur.x = x;
                    m.SetVector(s_AreaParameters, cur);
                    RRWLog.Once("surf-round-" + spec.Name + "-" + RRWLog.F(r), "surfaces: \"" + spec.Name + "\" batch roundness " + RRWLog.F(r) + " (AreaParameters.x=" + RRWLog.F(x) + ")");
                }
                t.AppliedParam = x;
            }
            int p = WantedPrio(t);
            if (p != int.MinValue && p != e.Priority)
            {
                if (SurfaceBatch.SetPriority(m_AreaBatch, e.Index, p))
                    RRWLog.Info("surfaces: \"" + spec.Name + "\" renderer priority " + e.Priority + " -> " + p + (p == spec.Priority ? " (restored)" : " (rrw.surf.line prio=)"));
            }
            t.AppliedPrio = p == spec.Priority && SurfaceState.PriorityOverrideOf(spec) == int.MinValue ? int.MinValue : p;
        }

        // Dev/introspection: current queue of a cover clone (-1 = no batch yet).
        internal static int CurrentQueue(World world, string prefabName)
        {
            try
            {
                if (!SurfaceState.Clones.TryGetValue(prefabName, out var c)) return -1;
                var abs = world.GetExistingSystemManaged<AreaBatchSystem>();
                return SurfaceBatch.Find(abs, world.EntityManager, c, out var e) ? e.Material.renderQueue : -1;
            }
            catch { return -1; }
        }

        // Dev/introspection: "queue=Q prio=P round=R expand=X lod=L" of a clone as the renderer sees it.
        internal static string Describe(World world, string prefabName)
        {
            try
            {
                if (!SurfaceState.Clones.TryGetValue(prefabName, out var c)) return "not registered";
                var em = world.EntityManager;
                var abs = world.GetExistingSystemManaged<AreaBatchSystem>();
                string s = SurfaceBatch.Find(abs, em, c, out var e)
                    ? "queue=" + e.Material.renderQueue + " prio=" + e.Priority + " areaParam.x=" + RRWLog.F(e.Material.GetVector(s_AreaParameters).x)
                    : "no batch";
                if (em.Exists(c.Entity) && em.HasComponent<RenderedAreaData>(c.Entity))
                    s += " expand=" + RRWLog.F(em.GetComponentData<RenderedAreaData>(c.Entity).m_ExpandAmount);
                if (em.Exists(c.Entity) && em.HasComponent<AreaGeometryData>(c.Entity))
                    s += " lod=" + RRWLog.F(em.GetComponentData<AreaGeometryData>(c.Entity).m_LodBias);
                return s + " round=" + RRWLog.F(c.AppliedRoundness) + " (" + c.SourceStyle + ")";
            }
            catch (Exception ex) { return "error " + ex.Message; }
        }
    }

    // Runtime style of the line clones on the PREFAB side (experimental switches). Called by SurfaceAreaSystem at Mod1 every
    // update (a few float compares). When RRWGates.TempLineRoundness / TempLineLodBias (or a rrw.surf.line round= override)
    // differs from what a clone carries, it rewrites:
    //   * RenderedArea.m_Roundness / m_LodBias on the clone object (used if AreaBatchSystem / AreaInitializeSystem rebuild it);
    //   * RenderedAreaData.m_ExpandAmount = clamp(r,.01,.99) * 0.75 / 2 on the prefab entity (AreaBatchSystem: the
    //     per-side growth AddTriangles applies to every area of the batch);
    //   * AreaGeometryData.m_LodBias (AreaInitializeSystem -> GeometrySystem.CalculateGeometry m_MinLod).
    // The caller then marks every live area of those prefabs Updated so the batch triangles and the LOD are rebuilt; the batch
    // material's AreaParameters.x follows in SurfaceMaterialSystem the same frame (PreCulling).
    internal static class SurfaceStyle
    {
        public static void Sync(EntityManager em, HashSet<Entity> restyled)
        {
            restyled.Clear();
            foreach (var c in SurfaceState.Clones.Values)
            {
                var spec = c.Spec;
                float r = SurfaceState.RoundnessOf(spec), lod = SurfaceState.LodBiasOf(spec);
                bool rDiff = !float.IsNaN(r) && r != c.AppliedRoundness;
                bool lDiff = !float.IsNaN(lod) && lod != c.AppliedLodBias;
                if (!rDiff && !lDiff) continue;
                if (c.Entity == Entity.Null || !em.Exists(c.Entity)) continue;
                var ra = c.Prefab != null ? c.Prefab.GetComponent<RenderedArea>() : null;
                if (rDiff)
                {
                    float rr = math.clamp(r, 0f, 1f);
                    if (ra != null) ra.m_Roundness = rr;
                    if (em.HasComponent<RenderedAreaData>(c.Entity))
                    {
                        var d = em.GetComponentData<RenderedAreaData>(c.Entity);
                        d.m_ExpandAmount = SurfaceMaterialSystem.ParamOf(rr) * 0.5f;
                        em.SetComponentData(c.Entity, d);
                    }
                }
                if (lDiff)
                {
                    if (ra != null) ra.m_LodBias = lod;
                    if (em.HasComponent<AreaGeometryData>(c.Entity))
                    {
                        var g = em.GetComponentData<AreaGeometryData>(c.Entity);
                        g.m_LodBias = lod;
                        em.SetComponentData(c.Entity, g);
                    }
                }
                string line = "\"" + spec.Name + "\"" + (rDiff ? " roundness " + RRWLog.F(c.AppliedRoundness) + " -> " + RRWLog.F(r)
                              + " (each side grows " + RRWLog.F(SurfaceMaterialSystem.ParamOf(r) * 0.5f) + " m)" : "")
                              + (lDiff ? " lodBias " + RRWLog.F(c.AppliedLodBias) + " -> " + RRWLog.F(lod) : "");
                if (rDiff) c.AppliedRoundness = r;
                if (lDiff) c.AppliedLodBias = lod;
                restyled.Add(c.Entity);
                SurfaceState.StyleSyncs++;
                SurfaceState.LastStyle = "u" + RRWClock.UpdateIndex + " " + line + " gates rev " + RRWGates.Revision;
                RRWLog.Info("surfaces: style " + line + " (gates rev " + RRWGates.Revision + "); its areas are re-batched");
            }
        }
    }
}
