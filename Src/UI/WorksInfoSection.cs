using System;
using Colossal.UI.Binding;
using Game;
using Game.Net;
using Game.Rendering;
using Game.SceneFlow;
using Game.Simulation;
using Game.UI.InGame;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.UI
{
    // "Road works" section of the selected-info panel. Pattern verified in game (v2): InfoSectionBase added
    // as a middle section, rendered by UI/RealisticRoadWorks.mjs under the key = this type's full name.
    //
    // Two update paths (both main thread, UIUpdate):
    //  * SelectedInfoUISystem.PerformUpdate -> Reset() -> Update() -> OnUpdate (selection) -> OnProcess (numbers + texts).
    //    Vanilla only does that on selection change, Updated/BatchesUpdated, or every 256 sim frames (never while paused).
    //  * The phase tick (registered UpdateAt UIUpdate) runs OnUpdate every frame: while our section is shown it checks a
    //    cheap change signature at <= 4 Hz and asks the info panel to refresh, so phase changes, dev jumps and button
    //    results appear at once, also while paused. Nothing is allocated in that path.
    // Triggers only enqueue WorksRequests (the Director drains them at Mod1) or move the camera; no structural changes.
    [RegisterSystem(SystemUpdatePhase.UIUpdate, Order = RRWOrder.InfoSection)]
    public partial class WorksInfoSection : InfoSectionBase
    {
        public const string kGroup = "RealisticRoadWorks";
        public const string kIcon = "Media/Game/Icons/RoadMaintenanceDepot.svg";
        private const int kCheckEveryUpdates = 15;      // signature check cadence (~4 Hz)
        private const int kDebounceUpdates = 20;        // ignore repeated clicks on the same button within this many updates
        private const float kFocusZoom = 150f;          // closest typical viewing distance

        private readonly WorksPanelState m_State = new WorksPanelState();
        private readonly WorksPanelState m_TriggerState = new WorksPanelState();
        private readonly RRWGuard m_Guard = new RRWGuard("ui section");
        private readonly RRWGuard m_TriggerGuard = new RRWGuard("ui trigger");
        private CitySystem m_CitySystem;
        private TimeSystem m_TimeSystem;
        private CameraUpdateSystem m_CameraSystem;

        private bool m_InPerform;          // Reset() marks the next OnUpdate as the info panel's own Update() call
        private uint m_ShownProject;       // project the panel showed at the last PerformUpdate (0 = section hidden)
        private Entity m_ShownEdge;
        private uint m_ShownSignature;
        private uint m_LastCheck;
        private int m_ForceRefresh;        // refresh the panel on the next few updates (after a trigger)
        private readonly uint[] m_LastTrigger = new uint[4];

        private readonly PanelTexts m_Texts = new PanelTexts();

        protected override string group => kGroup;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_CameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_InfoUISystem.AddMiddleSection(this);
            AddBinding(new TriggerBinding(kGroup, "rush", OnRush));
            AddBinding(new TriggerBinding(kGroup, "cancel", OnCancel));
            AddBinding(new TriggerBinding(kGroup, "focus", OnFocus));
            RRWLog.Info("ui info section registered (key " + GetType().FullName + ")");
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_ShownProject = 0;
            m_ShownEdge = Entity.Null;
            m_ForceRefresh = 0;
            m_State.Clear();
            m_TriggerState.Clear();
            m_Guard.Reset();
            m_TriggerGuard.Reset();
        }

        protected override void Reset()
        {
            m_InPerform = true;
            visible = false;
            m_State.Clear();
        }

        protected override void OnUpdate()
        {
            if (m_InPerform)
            {
                m_InPerform = false;
                if (m_Guard.Faulted) { visible = false; return; }
                try
                {
                    visible = GameManager.instance != null && GameManager.instance.gameMode == GameMode.Game
                              && m_State.ResolveSelection(EntityManager, selectedEntity);
                    if (!visible) { m_ShownProject = 0; m_ShownEdge = Entity.Null; }
                    m_Guard.Ok();
                }
                catch (Exception e)
                {
                    visible = false;
                    m_ShownProject = 0;
                    m_Guard.Fail(e);
                }
                return;
            }
            Tick();
        }

        // Per-frame path (phase tick). Never throws.
        private void Tick()
        {
            if (m_Guard.Faulted) return;
            try
            {
                RRWClock.Update(World);
                if (m_ShownProject == 0 || selectedEntity == Entity.Null) { m_ForceRefresh = 0; return; }
                if (m_ForceRefresh > 0)
                {
                    m_ForceRefresh--;
                    m_InfoUISystem.RequestUpdate();
                    return;
                }
                if (RRWClock.UpdateIndex - m_LastCheck < kCheckEveryUpdates) return;
                m_LastCheck = RRWClock.UpdateIndex;
                long t = RRWPerf.Start();
                uint sig = WorksPanelState.Signature(EntityManager, m_ShownProject, m_ShownEdge);
                if (sig != m_ShownSignature) m_InfoUISystem.RequestUpdate();
                RRWPerf.Stop(PerfSlot.UI, t);
            }
            catch (Exception e) { m_Guard.Fail(e); }
        }

        protected override void OnProcess()
        {
            long t = RRWPerf.Start();
            try
            {
                var s = RRWSettings.Current;
                float now = m_TimeSystem != null ? m_TimeSystem.normalizedTime : RRWClock.NormalizedTime;
                m_State.Compute(EntityManager, m_CitySystem.City, now, s);
                m_ShownProject = m_State.ProjectId;
                m_ShownEdge = m_State.FirstEdge;
                m_ShownSignature = WorksPanelState.Signature(EntityManager, m_ShownProject, m_ShownEdge);
                m_Texts.Build(m_State, s);
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
                m_ShownProject = 0;
                visible = false;   // Write() then emits null: the panel simply has no works section
            }
            RRWPerf.Stop(PerfSlot.UI, t);
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            var st = m_State;
            var tx = m_Texts;
            W(writer, "title", tx.Title);
            W(writer, "kindText", tx.KindText);
            writer.PropertyName("kind"); writer.Write((int)st.Kind);
            // The reserved mode H (VisualMode.HalfWidth, only in saves of a newer build) is shown like mode D.
            writer.PropertyName("minimal"); writer.Write(st.Mode == VisualMode.Minimal || st.Mode == VisualMode.HalfWidth);
            writer.PropertyName("steps");
            writer.ArrayBegin(tx.StepCount);
            for (int i = 0; i < tx.StepCount; i++) writer.Write(tx.Steps[i] ?? "");
            writer.ArrayEnd();
            writer.PropertyName("stepIndex"); writer.Write(tx.StepIndex);
            writer.PropertyName("stepFraction"); writer.Write(st.IsComplete ? 1f : math.saturate(st.F));
            writer.PropertyName("progress"); writer.Write(math.saturate(st.P) * 100f);
            W(writer, "phaseText", tx.PhaseText);
            W(writer, "etaText", tx.EtaText);
            writer.PropertyName("etaState"); writer.Write((int)st.Eta);
            writer.PropertyName("working"); writer.Write(st.Eta == EtaState.Working);
            W(writer, "trafficText", tx.TrafficText);
            writer.PropertyName("closure"); writer.Write((int)st.DisplayClosure);   // pip level (one side open = amber)
            W(writer, "paidText", tx.PaidText);
            W(writer, "refundText", tx.RefundText);
            writer.PropertyName("rushCost"); writer.Write((int)math.min(st.RushCost, int.MaxValue));
            W(writer, "rushText", tx.RushText);
            writer.PropertyName("canRush"); writer.Write(st.CanRush);
            writer.PropertyName("rushed"); writer.Write(st.Rushed);
            writer.PropertyName("canAfford"); writer.Write(st.CanAfford);
            W(writer, "cantAffordText", tx.CantAffordText);
            writer.PropertyName("canCancel"); writer.Write(st.CanCancel);
            // A cancelled construction being restored cannot be called off: no button at all (not a greyed-out one).
            writer.PropertyName("showCancel"); writer.Write(!(st.Kind == WorksKind.Demolition && st.CancelledBuild));
            W(writer, "cancelText", tx.CancelText);
            W(writer, "confirmText", tx.ConfirmText);
            W(writer, "focusText", tx.FocusText);
            W(writer, "segmentsText", tx.SegmentsText);
            // "3 crews working" + one gauge segment per crew section (its own sweep 0..1); empty with one crew.
            W(writer, "crewsText", tx.CrewsText);
            int crews = !string.IsNullOrEmpty(tx.CrewsText) ? math.min(st.Crews, st.CrewFill.Length) : 0;
            writer.PropertyName("crewFills");
            writer.ArrayBegin(crews);
            for (int i = 0; i < crews; i++) writer.Write(st.CrewFill[i]);
            writer.ArrayEnd();
            writer.PropertyName("focusCrew"); writer.Write(st.FocusCrew);
            writer.PropertyName("complete"); writer.Write(st.IsComplete);
            writer.PropertyName("projectId"); writer.Write((int)st.ProjectId);
        }

        private static void W(IJsonWriter w, string name, string value)
        {
            w.PropertyName(name);
            w.Write(value ?? "");
        }

        // ------------------------------------------------------------------ triggers

        // Re-resolves the CURRENT selection at click time (the panel may be a frame old). False when nothing to act on.
        private bool ResolveForTrigger(int slot)
        {
            RRWClock.Update(World);
            uint now = RRWClock.UpdateIndex;
            if (m_LastTrigger[slot] != 0 && now - m_LastTrigger[slot] < kDebounceUpdates) return false;
            m_LastTrigger[slot] = now;
            if (GameManager.instance == null || GameManager.instance.gameMode != GameMode.Game) return false;
            if (!m_TriggerState.ResolveSelection(EntityManager, selectedEntity)) return false;
            float time = m_TimeSystem != null ? m_TimeSystem.normalizedTime : RRWClock.NormalizedTime;
            m_TriggerState.Compute(EntityManager, m_CitySystem.City, time, RRWSettings.Current);
            return true;
        }

        private void AfterTrigger()
        {
            m_ForceRefresh = 4;
            m_InfoUISystem.RequestUpdate();
        }

        private void Enqueue(WorksRequestType type, WorksPanelState st)
        {
            WorksRequests.Enqueue(new WorksRequest { Type = type, Edge = st.FirstEdge, ProjectId = st.ProjectId });
            RRWLog.Info("ui request " + type + " p" + st.ProjectId + " edges=" + st.ProjectEdges.Count + " p=" + RRWLog.F(st.P)
                        + (type == WorksRequestType.Rush ? " cost=" + st.RushCost : "")
                        + (type == WorksRequestType.Cancel || type == WorksRequestType.CancelInstant ? " refund=" + st.Refund : ""));
        }

        private void OnRush()
        {
            try
            {
                if (!ResolveForTrigger(0)) return;
                var st = m_TriggerState;
                if (!st.CanRush) return;
                if (!st.CanAfford) { RRWLog.Verbose("ui rush refused: cannot afford " + st.RushCost); return; }
                Enqueue(WorksRequestType.Rush, st);
                AfterTrigger();
                m_TriggerGuard.Ok();
            }
            catch (Exception e) { m_TriggerGuard.Fail(e); }
        }

        private void OnCancel()
        {
            try
            {
                if (!ResolveForTrigger(2)) return;
                var st = m_TriggerState;
                if (!st.CanCancel) return;
                Enqueue(st.Kind == WorksKind.Construction && st.InstantCancel ? WorksRequestType.CancelInstant : WorksRequestType.Cancel, st);
                AfterTrigger();
                m_TriggerGuard.Ok();
            }
            catch (Exception e) { m_TriggerGuard.Fail(e); }
        }

        private void OnFocus()
        {
            try
            {
                if (!ResolveForTrigger(3)) return;
                FocusFront(EntityManager, m_CameraSystem, m_InfoUISystem, m_TriggerState);
                m_TriggerGuard.Ok();
            }
            catch (Exception e) { m_TriggerGuard.Fail(e); }
        }

        // The focus button cycles through the crews. The first click shows the crew nearest the camera (the Director's
        // FocusCrew = FrontPosition); a click while the camera still stands where the previous click put it shows the next crew.
        private static uint s_LastFocusProject;
        private static int s_LastFocusCrew = -1;
        private static float3 s_LastFocusPivot;
        private const float kFocusCycleRadius = 2f;   // m (XZ): the player has not moved the camera since our last focus

        // Moves the gameplay camera pivot to a work front of the project, zoom 150, rotation kept (camera API verified in game in v2).
        // crew < 0: the crew nearest the camera, or the next crew on a repeated click; crew >= 0: that crew.
        // Public for the dev command (rrw.ui.focus).
        public static bool FocusFront(EntityManager em, CameraUpdateSystem cameraSystem, SelectedInfoUISystem info, WorksPanelState st, int crew = -1)
        {
            if (cameraSystem == null || !st.Valid) return false;
            var cam = cameraSystem.gamePlayController;
            if (cam == null) return false;
            UnityEngine.Vector3 pv = cam.pivot;
            float3 pivot = new float3(pv.x, pv.y, pv.z);
            if (crew < 0 && s_LastFocusProject == st.ProjectId && s_LastFocusCrew >= 0 && math.distance(pivot.xz, s_LastFocusPivot.xz) <= kFocusCycleRadius)
                crew = s_LastFocusCrew + 1;   // repeated click: next crew (wraps in TryFront)
            if (!TryFront(em, st, crew, out float3 pos, out int shown, out int crews)) return false;
            // Leave the follow camera (a puppet may be followed) so the gameplay camera is the active one again.
            if (cameraSystem.orbitCameraController != null && ReferenceEquals(cameraSystem.activeCameraController, cameraSystem.orbitCameraController))
                info?.Focus(Entity.Null);
            if (!ReferenceEquals(cameraSystem.activeCameraController, cam)) return false;   // photo / cinematic mode: leave it alone
            cam.pivot = new UnityEngine.Vector3(pos.x, pos.y, pos.z);
            var zr = cam.zoomRange;
            cam.zoom = math.clamp(kFocusZoom, zr.min, zr.max);
            s_LastFocusProject = st.ProjectId;
            s_LastFocusCrew = crews > 1 ? shown : -1;
            s_LastFocusPivot = pos;
            if (crews > 1) RRWLog.Info("ui focus p" + st.ProjectId + " crew " + shown + "/" + crews + " front=" + RRWLog.F3(pos));
            else RRWLog.Verbose("ui focus p" + st.ProjectId + " front=" + RRWLog.F3(pos));
            return true;
        }

        // Front to show: one crew -> the Director's FrontU; several -> crew `crew` (wrapped), or FocusCrew when crew < 0.
        private static bool TryFront(EntityManager em, WorksPanelState st, int crew, out float3 pos, out int shown, out int crews)
        {
            pos = default;
            shown = 0;
            crews = 1;
            if (SiteRegistry.TryGetProject(st.ProjectId, out ProjectRecord pr))
            {
                var v = pr.View();
                crews = v.CrewCount;
                float u = pr.FrontU;
                if (crews > 1)
                {
                    shown = crew < 0 ? math.clamp(pr.FocusCrew, 0, crews - 1) : crew % crews;
                    u = PhasePlan.CrewFront(v, shown);
                }
                u = math.clamp(u, math.min(pr.TrimU0, pr.ChainLength), pr.TrimU1 > 0f ? pr.TrimU1 : pr.ChainLength);
                if (ChainMap.Point(em, pr, u, out pos, out _) && math.all(math.isfinite(pos))) return true;
                if (!math.all(pr.FrontPosition == float3.zero)) { pos = pr.FrontPosition; return true; }
            }
            if (st.FirstEdge != Entity.Null && em.HasComponent<Curve>(st.FirstEdge))
            {
                pos = Colossal.Mathematics.MathUtils.Position(em.GetComponentData<Curve>(st.FirstEdge).m_Bezier, 0.5f);
                return true;
            }
            return false;
        }
    }
}
