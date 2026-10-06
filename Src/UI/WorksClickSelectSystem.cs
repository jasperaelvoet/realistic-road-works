using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Mathematics;
using Game;
using Game.Audio;
using Game.Common;
using Game.Input;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using NetEdge = Game.Net.Edge;
using ObjTransform = Game.Objects.Transform;

namespace RealisticRoadWorks.V3.UI
{
    // Click path into the "Road works" panel (requirement: clicking a hidden C1 trench, a barrier or a puppet opens the
    // section). Root cause (game code): the default tool raycasts StaticObjects | MovingObjects | Labels | Icons
    // (+ Lots), never Net (DefaultToolSystem.InitializeRaycast; Net only with debugSelect), so in vanilla a road is selected
    // only through its street-name label. Our site bodies are invisible to that raycast:
    //  * the works road itself: Net is not in the mask (and it is Hidden);
    //  * props: late Owner = node (chosen after in-game tests) -> Objects.RaycastJobs.ValidateResult walks the owner to
    //    the node, the type becomes Net, and the result is dropped because Net is not in the mask;
    //  * puppets: vehicles enter the moving search tree only through CarCurrentLane / CarTrailerLane / ParkedCar
    //    (Vehicles.ReferencesSystem) and lane-object buffers; puppets strip those (verified in game), so RaycastSystem
    //    never sees them.
    //    Making them raycastable would mean writing vanilla search trees from Machines (and vanilla would then generate Temp
    //    copies of a vehicle archetype on hover) - rejected as unsafe.
    //  * only the closure icon (Owner = edge) is selectable: SelectEntityJob walks an icon's Owner chain to the edge.
    //
    // Fix, two layers (both main thread, ToolUpdate right after DefaultToolSystem, i.e. before SelectedInfoUISystem in
    // UIUpdate reads the selection, so the panel never sees an intermediate state):
    //  1. Click: on the frame the default tool's apply action is pressed over the world, after vanilla wrote
    //     ToolSystem.selected, run our own ray test against the works sites (the corridor of every works edge, at road
    //     level and at trench-floor level, plus the boxes of our props and puppets). When it hits and vanilla selected
    //     nothing, one of our derived entities, or something farther along the ray than our hit, select the works street
    //     (Aggregate, selectedIndex = the edge's element) or, without an aggregate, the edge.
    //  2. Redirect: whenever the selection changes to one of our derived entities (prop, puppet, area, or a sub-object /
    //     trailer of one), replace it with the works street of its site.
    // Never throws out of OnUpdate (guard); allocation only on a click.
    [RegisterSystem(SystemUpdatePhase.ToolUpdate, After = typeof(DefaultToolSystem), Order = RRWOrder.ClickSelect)]
    public partial class WorksClickSelectSystem : GameSystemBase
    {
        private const float kCorridorMargin = 1.5f;      // m beyond the composition half width
        private const float kFloorExtra = 1.5f;          // m below the subgrade depth (trench floor + spoil)
        private const float kMinFloorDepth = 2.5f;
        private const float kSampleStep = 2f;            // m between corridor samples (<< half width: no gaps)
        private const int kMaxSamples = 512;
        private const float kVanillaSlack = 1f;          // m: our hit must be this much closer than vanilla's to win

        private ToolSystem m_ToolSystem;
        private DefaultToolSystem m_DefaultTool;
        private ToolRaycastSystem m_ToolRaycast;
        private CameraUpdateSystem m_Camera;
        private AudioManager m_Audio;
        private EntityQuery m_DerivedObjects;
        private EntityQuery m_SoundQuery;
        private readonly RRWGuard m_Guard = new RRWGuard("ui click select");

        private Func<bool>[] m_Pressed;          // WasPressedThisFrame of the default tool's apply action states
        private int m_BindAttempts;
        private Entity m_LastSelected;
        private readonly WorksPanelState m_Check = new WorksPanelState();

        // ---- diagnostics (rrw.ui.click)
        public static int Clicks, Hits, Selected, Redirects, KeptVanilla, Misses;
        public static string LastClick = "none";
        public static string BindInfo = "not bound";

        public enum HitKind { None, Corridor, Object }

        public struct Hit
        {
            public HitKind Kind;
            public Entity Edge;        // works edge to select
            public Entity Object;      // derived object hit (Object kind)
            public float T;            // ray parameter (0..1 of the raycast segment)
            public float3 Position;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_DefaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            m_ToolRaycast = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
            m_Camera = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_Audio = World.GetOrCreateSystemManaged<AudioManager>();
            m_DerivedObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RRWDerived>(), ComponentType.ReadOnly<ObjTransform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_SoundQuery = GetEntityQuery(ComponentType.ReadOnly<ToolUXSoundSettingsData>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_LastSelected = Entity.Null;
            m_Guard.Reset();
            Clicks = Hits = Selected = Redirects = KeptVanilla = Misses = 0;
            LastClick = "none";
        }

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted) return;
            if (GameManager.instance == null || GameManager.instance.gameMode != GameMode.Game) return;
            if (SiteRegistry.Edges.Count == 0) { m_LastSelected = m_ToolSystem.selected; return; }
            try
            {
                long t0 = RRWPerf.Start();
                if (m_ToolSystem.activeTool == m_DefaultTool && ClickedThisFrame()) OnClick();
                RedirectDerivedSelection();
                RRWPerf.Stop(PerfSlot.UI, t0);
                m_Guard.Ok();
            }
            catch (Exception e) { m_Guard.Fail(e); }
        }

        // ------------------------------------------------------------------ input

        private bool ClickedThisFrame()
        {
            if (m_Pressed == null && !BindActions()) return false;
            if ((m_ToolRaycast.raycastFlags & (RaycastFlags.DebugDisable | RaycastFlags.UIDisable)) != 0) return false;
            var im = InputManager.instance;
            if (im == null || !im.controlOverWorld) return false;
            for (int i = 0; i < m_Pressed.Length; i++)
                if (m_Pressed[i]()) return true;
            return false;
        }

        // The default tool reads its (protected) applyAction: an override that is "Default Tool" while it hovers an
        // entity and "Mouse Apply" otherwise. Both states are created in DefaultToolSystem.OnCreate and never replaced, so
        // bind their WasPressedThisFrame once. UIInputAction states only report presses while enabled, i.e. while the
        // default tool uses them.
        private bool BindActions()
        {
            if (m_BindAttempts >= 3) return false;
            m_BindAttempts++;
            try
            {
                var list = new List<Func<bool>>(3);
                var names = new List<string>(3);
                const BindingFlags kF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                TryBind(typeof(DefaultToolSystem).GetField("m_DefaultToolApply", kF), list, names);
                TryBind(typeof(ToolBaseSystem).GetField("m_MouseApply", kF), list, names);
                TryBind(typeof(ToolBaseSystem).GetField("m_DefaultApply", kF), list, names);
                if (list.Count == 0)
                {
                    BindInfo = "FAILED (no apply action states found)";
                    if (m_BindAttempts >= 3) RRWLog.Warn("ui click select: default tool apply actions not found; only the derived-selection redirect works");
                    return false;
                }
                m_Pressed = list.ToArray();
                BindInfo = "bound " + string.Join(",", names.ToArray());
                RRWLog.Info("ui click select: " + BindInfo);
                return true;
            }
            catch (Exception e)
            {
                BindInfo = "FAILED (" + e.GetType().Name + ")";
                RRWLog.ErrorOnce("ui click select bind", e);
                return false;
            }
        }

        private void TryBind(FieldInfo fi, List<Func<bool>> list, List<string> names)
        {
            if (fi == null) return;
            object state = fi.GetValue(m_DefaultTool);
            if (state == null) return;
            MethodInfo target = null;
            Type itf = typeof(IProxyAction);
            if (itf.IsAssignableFrom(state.GetType()))
            {
                var map = state.GetType().GetInterfaceMap(itf);
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                    if (map.InterfaceMethods[i].Name == "WasPressedThisFrame" && map.InterfaceMethods[i].GetParameters().Length == 0)
                    { target = map.TargetMethods[i]; break; }
            }
            if (target == null) target = state.GetType().GetMethod("WasPressedThisFrame", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (target == null || target.ReturnType != typeof(bool)) return;
            list.Add((Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), state, target));
            names.Add(fi.Name);
        }

        // ------------------------------------------------------------------ click

        private void OnClick()
        {
            Clicks++;
            if (!TryGetRay(out Line3.Segment ray)) { LastClick = "no camera"; return; }
            Entity vanilla = m_ToolSystem.selected;   // what DefaultToolSystem selected this frame (Null = nothing)
            if (!Pick(EntityManager, ray, m_DerivedObjects, out Hit hit))
            {
                Misses++;
                LastClick = "miss (vanilla " + Describe(vanilla) + ")";
                return;
            }
            Hits++;
            var em = EntityManager;
            bool vanillaOurs = vanilla != Entity.Null && WorksPanelState.EdgeOfDerived(em, vanilla) != Entity.Null;
            bool vanillaWorks = vanilla != Entity.Null && !vanillaOurs && m_Check.ResolveSelection(em, vanilla);
            if (vanillaWorks)
            {
                // A works street / edge already (label or closure icon): the panel shows it as is.
                KeptVanilla++;
                LastClick = "hit " + DescribeHit(hit) + ", kept vanilla works selection " + Describe(vanilla);
                return;
            }
            if (vanilla != Entity.Null && !vanillaOurs)
            {
                // Vanilla selected something else. Keep it unless our hit is clearly in front of vanilla's.
                float vt = VanillaHitT(ray);
                if (!(hit.T < vt - kVanillaSlack / math.max(1f, math.length(ray.b - ray.a))))
                {
                    KeptVanilla++;
                    LastClick = "hit " + DescribeHit(hit) + ", kept vanilla " + Describe(vanilla) + " (closer)";
                    return;
                }
            }
            Select(hit.Edge, true);
            LastClick = "hit " + DescribeHit(hit) + " over vanilla " + Describe(vanilla) + " -> " + Describe(m_ToolSystem.selected);
            RRWLog.Info("ui click select: " + LastClick);
        }

        private bool TryGetRay(out Line3.Segment ray)
        {
            ray = default;
            if (m_Camera == null || !m_Camera.TryGetViewer(out Viewer viewer) || viewer == null || viewer.camera == null) return false;
            ray = ToolRaycastSystem.CalculateRaycastLine(viewer.camera);
            return math.all(math.isfinite(ray.a)) && math.all(math.isfinite(ray.b)) && math.lengthsq(ray.b - ray.a) > 1f;
        }

        private float VanillaHitT(Line3.Segment ray)
        {
            if (!m_ToolRaycast.GetRaycastResult(out RaycastResult r)) return float.MaxValue;
            float3 d = ray.b - ray.a;
            float l2 = math.lengthsq(d);
            if (l2 <= 0f) return float.MaxValue;
            return math.dot(r.m_Hit.m_HitPosition - ray.a, d) / l2;
        }

        // ------------------------------------------------------------------ redirect

        private void RedirectDerivedSelection()
        {
            Entity sel = m_ToolSystem.selected;
            if (sel == m_LastSelected) return;
            m_LastSelected = sel;
            if (sel == Entity.Null) return;
            var em = EntityManager;
            if (!em.Exists(sel) || em.HasComponent<NetEdge>(sel) || em.HasBuffer<AggregateElement>(sel)) return;
            Entity edge = WorksPanelState.EdgeOfDerived(em, sel);
            if (edge == Entity.Null) return;
            Redirects++;
            Select(edge, false);
            RRWLog.Info("ui click select: redirected selection " + RRWLog.E(sel) + " -> " + Describe(m_ToolSystem.selected));
        }

        // Selects the works street of an edge (Aggregate + element index, like a click on its name label) or the edge.
        private void Select(Entity edge, bool sound)
        {
            ResolveTarget(EntityManager, edge, out Entity target, out int index);
            if (target == Entity.Null) return;
            bool changed = m_ToolSystem.selected != target;
            m_ToolSystem.selected = target;
            m_ToolSystem.selectedIndex = index;
            m_LastSelected = target;
            Selected++;
            if (sound && changed) PlaySelectSound();
        }

        public static void ResolveTarget(EntityManager em, Entity edge, out Entity target, out int index)
        {
            target = edge;
            index = -1;
            if (edge == Entity.Null || !em.Exists(edge)) { target = Entity.Null; return; }
            if (!em.HasComponent<Aggregated>(edge)) return;
            Entity agg = em.GetComponentData<Aggregated>(edge).m_Aggregate;
            if (agg == Entity.Null || !em.Exists(agg) || em.HasComponent<Deleted>(agg) || !em.HasBuffer<AggregateElement>(agg)) return;
            var buf = em.GetBuffer<AggregateElement>(agg, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_Edge != edge) continue;
                target = agg;
                index = i;
                return;
            }
        }

        private void PlaySelectSound()
        {
            try
            {
                if (m_Audio == null || m_SoundQuery.IsEmptyIgnoreFilter) return;
                Entity clip = m_SoundQuery.GetSingleton<ToolUXSoundSettingsData>().m_SelectEntitySound;
                if (clip != Entity.Null) m_Audio.PlayUISound(clip);
            }
            catch (Exception e) { RRWLog.ErrorOnce("ui click select sound", e); }
        }

        // ------------------------------------------------------------------ ray test (also used by rrw.ui.click probe)

        // Nearest works hit along the ray: our props / puppets (oriented prefab bounds) and the corridor of every works
        // edge, tested as the ray's crossing of the horizontal plane through each centre-line sample, at road level and at
        // trench-floor level, within the half width + margin. Main thread; only on a click (or the dev probe).
        public static bool Pick(EntityManager em, Line3.Segment ray, EntityQuery derivedObjects, out Hit best)
        {
            best = default;
            best.T = float.MaxValue;
            float3 d = ray.b - ray.a;
            if (math.abs(d.y) < 1e-4f) return false;

            // ---- derived objects (props, puppets, trailers)
            if (!derivedObjects.IsEmptyIgnoreFilter)
            {
                var ents = derivedObjects.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < ents.Length; i++)
                    {
                        Entity e = ents[i];
                        if (em.HasComponent<Hidden>(e)) continue;
                        Entity prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                        if (!em.HasComponent<ObjectGeometryData>(prefab)) continue;
                        var bounds = em.GetComponentData<ObjectGeometryData>(prefab).m_Bounds;
                        ObjTransform tr = em.HasComponent<InterpolatedTransform>(e)
                            ? em.GetComponentData<InterpolatedTransform>(e).ToTransform()
                            : em.GetComponentData<ObjTransform>(e);
                        if (!math.all(math.isfinite(tr.m_Position))) continue;
                        quaternion inv = math.inverse(tr.m_Rotation);
                        var local = new Line3.Segment { a = math.mul(inv, ray.a - tr.m_Position), b = math.mul(inv, ray.b - tr.m_Position) };
                        if (!MathUtils.Intersect(bounds, local, out float2 t) || t.x < 0f || t.x >= best.T) continue;
                        Entity edge = WorksPanelState.EdgeOfDerived(em, e);
                        if (edge == Entity.Null) continue;
                        best = new Hit { Kind = HitKind.Object, Edge = edge, Object = e, T = t.x, Position = MathUtils.Position(ray, t.x) };
                    }
                }
                finally { ents.Dispose(); }
            }

            // ---- works corridors
            foreach (var kv in SiteRegistry.Edges)
            {
                Entity edge = kv.Key;
                if (!WorksPanelState.IsWorksEdge(em, edge) || !em.HasComponent<Curve>(edge)) continue;
                var curve = em.GetComponentData<Curve>(edge);
                EdgeRecord rec = kv.Value;
                float half = rec != null && rec.Section.HalfWidth > 0.5f ? rec.Section.HalfWidth : EcsUtil.CompositionWidth(em, edge) * 0.5f;
                half += kCorridorMargin;
                float depth = math.max(rec != null ? rec.SubgradeDepth : 0f, kMinFloorDepth) + kFloorExtra;
                float half2 = half * half;
                int n = (int)math.clamp(math.ceil(curve.m_Length / kSampleStep), 2f, kMaxSamples);
                for (int s = 0; s <= n; s++)
                {
                    float3 p = MathUtils.Position(curve.m_Bezier, s / (float)n);
                    for (int level = 0; level < 2; level++)
                    {
                        float y = level == 0 ? p.y : p.y - depth;
                        float t = (y - ray.a.y) / d.y;
                        if (t < 0f || t > 1f || t >= best.T) continue;
                        float3 q = ray.a + d * t;
                        if (math.distancesq(q.xz, p.xz) > half2) continue;
                        best = new Hit { Kind = HitKind.Corridor, Edge = edge, Object = Entity.Null, T = t, Position = q };
                    }
                }
            }
            return best.Kind != HitKind.None;
        }

        // ------------------------------------------------------------------ text

        private string Describe(Entity e)
        {
            if (e == Entity.Null) return "nothing";
            var em = EntityManager;
            if (!em.Exists(e)) return RRWLog.E(e) + "(gone)";
            string kind = em.HasBuffer<AggregateElement>(e) ? "street" : em.HasComponent<NetEdge>(e) ? "edge"
                : em.HasComponent<RRWMachine>(e) ? "puppet" : em.HasComponent<RRWDerived>(e) ? "derived" : "entity";
            return kind + " " + RRWLog.E(e);
        }

        public static string DescribeHit(Hit h) =>
            h.Kind == HitKind.None ? "none"
            : h.Kind + " edge " + RRWLog.E(h.Edge) + (h.Object != Entity.Null ? " via " + RRWLog.E(h.Object) : "") + " at " + RRWLog.F3(h.Position);

        // Dev probe: what a click at the current mouse position would select (no selection change).
        public string Probe()
        {
            if (!TryGetRay(out Line3.Segment ray)) return "no camera";
            var em = EntityManager;
            if (!Pick(em, ray, m_DerivedObjects, out Hit hit)) return "no works under the mouse (vanilla raycast " + VanillaDescribe() + ")";
            ResolveTarget(em, hit.Edge, out Entity target, out int index);
            return "hit " + DescribeHit(hit) + " t=" + RRWLog.F(hit.T) + " -> would select " + Describe(target)
                   + (index >= 0 ? " element " + index : "") + " (vanilla raycast " + VanillaDescribe() + ")";
        }

        private string VanillaDescribe()
        {
            if (!m_ToolRaycast.GetRaycastResult(out RaycastResult r)) return "nothing";
            return Describe(r.m_Owner) + " at " + RRWLog.F3(r.m_Hit.m_HitPosition);
        }
    }
}
