using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using NetEdge = Game.Net.Edge;

namespace RealisticRoadWorks.V3.UI
{
    public enum EtaState
    {
        Working = 0,     // "≈ 21 h 40 min of work · done tomorrow 11:20"
        CrewsOff = 1,    // outside the shift: "Crews off until 07:00"
        // 2 was "paused" (pause works was removed); the value stays unused so the .mjs constants keep their meaning
        Clearing = 3,    // demolition mobilisation: waiting for traffic to clear
        Finishing = 4,   // p >= 1, the Director is closing the site
    }

    // Everything the info section shows for ONE project, resolved from the selected entity. Read-only on ECS
    // and the registry; reused instance (no per-frame allocation). Shared by the section, its triggers and the dev dump.
    public sealed class WorksPanelState
    {
        // ---- selection
        public bool Valid;
        public uint ProjectId;
        public Entity FirstEdge;               // first edge of the project (chain order when the registry has it)
        public int SelectedInProject;          // selected works edges that belong to the shown project
        public int SelectedTotal;              // segments in the selection (aggregate elements, or 1)
        public readonly List<Entity> ProjectEdges = new List<Entity>(16);

        // ---- state
        public WorksKind Kind;
        public VisualMode Mode;
        public WorksPhase Phase;
        public float F;                        // phase fraction
        public float P;                        // project progress
        public SiteFlags Flags;
        public bool Rushed, Clearing, Completing, Working, Incompatible, CancelledBuild;
        public bool Releasing;                 // the project hands the road back (machines leaving, ProjectRecord.Releasing)
        public uint WorkRequired, WorkDone;

        // ---- traffic
        public ClosureLevel Closure;           // applied (Traffic), max over the project's edges
        public int BuildingsWaiting, Rerouted, ParkedMoved;
        public RoadZones OpenLanes;            // union of EdgeRecord.OpenLanesApplied over the project's Closed edges
        public bool CarHalfOpen => (OpenLanes & RoadZones.Carriageway) != 0;
        public bool SidewalksOpen => (OpenLanes & RoadZones.Sidewalks) != 0;   // any sidewalk bit (SidewalkLeft / SidewalkRight)
        // ---- staged traffic. APPLIED state from Traffic (EdgeRecord.*Applied), decisions from the Director.
        public RoadZones SoftApplied;          // union of EdgeRecord.SoftApplied over the project's Closed edges (groups draining now)
        public RoadZones ClosedBApplied;       // union of EdgeRecord.ClosedBApplied (car groups closed to through traffic, houses reachable)
        public bool CarAccessKept;             // every Closed edge: each car half is applied open or CLOSED-B (no building waits for a car)
        public StageSwitch Switch;             // ProjectRecord.Switch
        public bool CarHalfAllowed;            // ProjectRecord.CarHalfAllowed
        public StageBlockReason StageReason;   // ProjectRecord.StageBlockReason
        public byte StageIndex, StageCount;    // ProjectRecord.StageIndex / StageCount (C4a = 0 / C4b = 1 of 2)
        public bool StagedC4;                  // construction, mode A, Finishing, staged opening on (the window where a car half may open)
        // "Switching sides": a C4 switch is in its Swap / Drain step, or a car half is draining (SOFT) on the lanes.
        public bool SwitchingSides =>
            (Kind == WorksKind.Construction && (Switch == StageSwitch.Swap || Switch == StageSwitch.Drain))
            || (SoftApplied & RoadZones.Carriageway) != 0;
        // The C4 switch waits for the crew to leave the works half (Vacate; progress is held meanwhile).
        public bool CrewChangingSides => Kind == WorksKind.Construction && Switch == StageSwitch.Vacate;
        // Pip level for the traffic row: Closed with one car half open reads as a slow zone (amber), not as fully closed.
        public ClosureLevel DisplayClosure => Closure == ClosureLevel.Closed && CarHalfOpen ? ClosureLevel.SlowZone : Closure;

        // ---- money
        public long Paid, Refund, RushCost;
        public bool InstantCancel, CanRush, CanAfford, CanCancel;

        // ---- time
        public EtaState Eta;
        public double HoursLeft;               // crew working hours left at the current rate
        public double CalendarFrames;          // simulation frames until done, walking the shift windows
        public float ShiftStart;               // normalised day time the (effective) shift starts
        public float NormalizedTime;

        // ---- camera
        public float3 FrontPosition;
        public bool HasFront;

        // ---- crews. Crews is the Director's latched count (ProjectRecord.Crews, 1 = one crew, the
        // single-crew panel); CrewFill[i] is crew i's own sweep through its section (0..1, PhasePlan.CrewFront relative to the section),
        // FocusCrew the crew nearest the camera (the one the focus button shows first). Fixed array: no allocation per refresh.
        public int Crews = 1, FocusCrew, CrewsSpawned;
        public float SectionLength;            // trimmed chain length / Crews (m), for the dev dump and the section text
        public readonly float[] CrewFill = new float[RRWConst.kMaxCrewsPerProject];
        // Upgrade works: one crew per band of the current window, side by side along the whole road (CrewFill = each band's progress).
        public bool ShowCrews => Crews > 1 && !IsComplete && (Mode == VisualMode.FullDig || IsUpgrade);

        // ---- upgrade works (mode H)
        // ModeH: any HalfWidth construction (no cancel and no cancel refund, also before its upgrade data is known).
        // IsUpgrade: the project's view carries its upgrade data (ProjectView.IsUpgrade); View is that view (windows, band phases).
        public bool ModeH => Mode == VisualMode.HalfWidth && Kind == WorksKind.Construction;
        public bool IsUpgrade;
        public bool Gravel;                    // a new gravel road: survey, excavation and the gravel base only (PhasePlan.GravelEnd)
        public UpgradeClass UpClass;           // project class (the saved edge class until the registry has the project)
        public ProjectView View;

        private readonly List<Entity> m_Selected = new List<Entity>(32);

        public void Clear()
        {
            Valid = false;
            ProjectId = 0;
            FirstEdge = Entity.Null;
            SelectedInProject = SelectedTotal = 0;
            ProjectEdges.Clear();
            m_Selected.Clear();
            Kind = WorksKind.Construction;
            Mode = VisualMode.FullDig;
            Phase = WorksPhase.Survey;
            F = P = 0f;
            Flags = SiteFlags.None;
            Rushed = Clearing = Completing = Working = Incompatible = CancelledBuild = Releasing = false;
            WorkRequired = WorkDone = 0;
            Closure = ClosureLevel.Open;
            BuildingsWaiting = Rerouted = ParkedMoved = 0;
            OpenLanes = RoadZones.None;
            SoftApplied = ClosedBApplied = RoadZones.None;
            CarAccessKept = false;
            Switch = StageSwitch.None;
            CarHalfAllowed = false;
            StageReason = StageBlockReason.None;
            StageIndex = StageCount = 0;
            StagedC4 = false;
            Paid = Refund = RushCost = 0;
            InstantCancel = CanRush = CanAfford = CanCancel = false;
            Eta = EtaState.Working;
            HoursLeft = CalendarFrames = 0.0;
            ShiftStart = 0f;
            NormalizedTime = 0f;
            FrontPosition = default;
            HasFront = false;
            Crews = 1;
            FocusCrew = CrewsSpawned = 0;
            SectionLength = 0f;
            for (int i = 0; i < CrewFill.Length; i++) CrewFill[i] = 0f;
            IsUpgrade = false;
            UpClass = UpgradeClass.None;
            View = default;
        }

        public bool IsComplete => Phase == WorksPhase.Complete || Completing;

        // ------------------------------------------------------------------ selection

        // Resolves the selected entity to works edges and picks the project of the first one.
        // Cheap: component lookups only. Returns Valid.
        public bool ResolveSelection(EntityManager em, Entity selected)
        {
            Clear();
            if (selected == Entity.Null || !em.Exists(selected)) return false;

            if (em.HasBuffer<AggregateElement>(selected))
            {
                var buf = em.GetBuffer<AggregateElement>(selected, true);
                SelectedTotal = buf.Length;
                for (int i = 0; i < buf.Length; i++)
                {
                    Entity e = buf[i].m_Edge;
                    if (IsWorksEdge(em, e)) m_Selected.Add(e);
                }
            }
            else if (em.HasComponent<NetEdge>(selected))
            {
                SelectedTotal = 1;
                if (IsWorksEdge(em, selected)) m_Selected.Add(selected);
            }
            else
            {
                // A prop, decal or puppet (or one of its sub-objects / trailers) of a works site was clicked.
                Entity edge = EdgeOfDerived(em, selected);
                if (edge != Entity.Null && IsWorksEdge(em, edge)) m_Selected.Add(edge);
            }
            if (m_Selected.Count == 0) return false;

            var firstSite = em.GetComponentData<RoadWorksSite>(m_Selected[0]);
            ProjectId = firstSite.m_ProjectId;

            if (SiteRegistry.TryGetProject(ProjectId, out ProjectRecord pr) && pr.Edges.Count > 0)
            {
                for (int i = 0; i < pr.Edges.Count; i++)
                    if (IsWorksEdge(em, pr.Edges[i])) ProjectEdges.Add(pr.Edges[i]);
            }
            if (ProjectEdges.Count == 0)
            {
                // Registry not built yet (first frame of a site / right after a load): the selected edges of the project.
                for (int i = 0; i < m_Selected.Count; i++)
                    if (em.GetComponentData<RoadWorksSite>(m_Selected[i]).m_ProjectId == ProjectId) ProjectEdges.Add(m_Selected[i]);
            }
            if (ProjectEdges.Count == 0) return false;

            for (int i = 0; i < m_Selected.Count; i++)
                if (em.GetComponentData<RoadWorksSite>(m_Selected[i]).m_ProjectId == ProjectId) SelectedInProject++;
            if (SelectedTotal == 0) SelectedTotal = SelectedInProject;     // prop / puppet path: the whole project
            if (SelectedTotal < SelectedInProject) SelectedTotal = SelectedInProject;

            FirstEdge = ProjectEdges[0];
            Valid = true;
            return true;
        }

        public static bool IsWorksEdge(EntityManager em, Entity e) =>
            e != Entity.Null && em.Exists(e) && em.HasComponent<RoadWorksSite>(e) && !em.HasComponent<Deleted>(e) && !em.HasComponent<Temp>(e);

        // Edge (or project's first edge) behind a derived entity: RRWDerived.m_Site, RRWDerived/RRWMachine project id.
        // Walks Owner / Controller up a few levels so puppet sub-objects, loads and trailers resolve too.
        public static Entity EdgeOfDerived(EntityManager em, Entity e)
        {
            for (int depth = 0; depth < 4 && e != Entity.Null && em.Exists(e); depth++)
            {
                if (em.HasComponent<RRWDerived>(e))
                {
                    var d = em.GetComponentData<RRWDerived>(e);
                    if (d.m_Site != Entity.Null && IsWorksEdge(em, d.m_Site)) return d.m_Site;
                    Entity pe = FirstEdgeOfProject(em, d.m_ProjectId);
                    if (pe != Entity.Null) return pe;
                }
                if (em.HasComponent<RRWMachine>(e))
                {
                    Entity pe = FirstEdgeOfProject(em, em.GetComponentData<RRWMachine>(e).m_ProjectId);
                    if (pe != Entity.Null) return pe;
                }
                if (em.HasComponent<Game.Vehicles.Controller>(e))
                {
                    Entity c = em.GetComponentData<Game.Vehicles.Controller>(e).m_Controller;
                    if (c != Entity.Null && c != e && em.Exists(c) && (em.HasComponent<RRWMachine>(c) || em.HasComponent<RRWDerived>(c))) { e = c; continue; }
                }
                if (em.HasComponent<Owner>(e)) { e = em.GetComponentData<Owner>(e).m_Owner; continue; }
                break;
            }
            return Entity.Null;
        }

        public static Entity FirstEdgeOfProject(EntityManager em, uint projectId)
        {
            if (projectId == 0 || !SiteRegistry.TryGetProject(projectId, out ProjectRecord pr)) return Entity.Null;
            for (int i = 0; i < pr.Edges.Count; i++)
                if (IsWorksEdge(em, pr.Edges[i])) return pr.Edges[i];
            return Entity.Null;
        }

        // ------------------------------------------------------------------ numbers

        // Fills state, traffic, money and time for the resolved project. Call after ResolveSelection returned true.
        public void Compute(EntityManager em, Entity city, float normalizedTime, RRWSetting s)
        {
            if (!Valid) return;
            NormalizedTime = normalizedTime;
            var first = em.GetComponentData<RoadWorksSite>(FirstEdge);
            WorkRequired = first.m_WorkRequired;
            WorkDone = first.m_WorkDone;
            Mode = first.Mode;

            bool haveProject = SiteRegistry.TryGetProject(ProjectId, out ProjectRecord pr) && pr.Edges.Count > 0;
            if (haveProject)
            {
                Kind = pr.Kind;
                Phase = pr.Phase;
                F = pr.PhaseFraction;
                P = pr.Progress;
                Flags = pr.Flags;
                Clearing = pr.ClearingTraffic;
                Working = pr.Working;
                Rerouted = pr.Rerouted;
                ParkedMoved = pr.ParkedMoved;
                FrontPosition = pr.FrontPosition;
                HasFront = !math.all(pr.FrontPosition == float3.zero);
                Releasing = pr.Releasing;
                Switch = pr.Switch;
                CarHalfAllowed = pr.CarHalfAllowed;
                StageReason = pr.StageBlockReason;
                StageIndex = pr.StageIndex;
                StageCount = pr.StageCount;
                View = pr.View();
                IsUpgrade = View.IsUpgrade;
                UpClass = IsUpgrade ? View.Upgrade.Class : first.UpClass;
                // Upgrade works: the re-marking window under the one-direction primitive runs the staged markings layout of a new road.
                StagedC4 = (pr.Kind == WorksKind.Construction && pr.Mode == VisualMode.FullDig && pr.Phase == WorksPhase.Finishing && pr.StageCtx.Staged)
                           || (IsUpgrade && View.Upgrade.RemarkHalf);
                ComputeCrews(pr, View);
            }
            else
            {
                Kind = first.Kind;
                P = first.Progress;
                Phase = PhasePlan.PhaseOf(Kind, P, out F);
                Flags = first.Flags;
                Working = false;
                UpClass = first.UpClass;
            }
            // Never let a stale record contradict the saved site's kind (cancel turns a construction into a demolition).
            if (Kind != first.Kind)
            {
                Kind = first.Kind; P = first.Progress; Phase = PhasePlan.PhaseOf(Kind, P, out F); Crews = 1; FocusCrew = 0;
                IsUpgrade = false; View = default;
            }

            Rushed = (Flags & SiteFlags.Rushed) != 0;
            Incompatible = (Flags & SiteFlags.Incompatible) != 0;
            Gravel = Kind == WorksKind.Construction && !IsUpgrade && EcsUtil.IsGravel(em, FirstEdge);
            CancelledBuild = (Flags & SiteFlags.CancelledBuild) != 0;
            Completing = Phase == WorksPhase.Complete;
            if (em.HasComponent<RoadWorksRuntime>(FirstEdge))
            {
                var rt = em.GetComponentData<RoadWorksRuntime>(FirstEdge);
                if (rt.Has(RuntimeFlags.Completing)) Completing = true;
                if (rt.Has(RuntimeFlags.ClearingTraffic)) Clearing = true;
                if (rt.Has(RuntimeFlags.Releasing)) Releasing = true;
            }

            // ---- traffic and money over the project's edges
            Closure = ClosureLevel.Open;
            BuildingsWaiting = 0;
            OpenLanes = RoadZones.None;
            SoftApplied = ClosedBApplied = RoadZones.None;
            bool anyClosed = false, carKept = true;
            Paid = 0;
            Refund = 0;
            RushCost = 0;
            int rushPercent = s != null ? s.RushCostPercent : 50;
            InstantCancel = !ModeH && Kind == WorksKind.Construction && P < RRWConst.kInstantCancelProgress && (s == null || s.InstantCancelUnfinished);
            for (int i = 0; i < ProjectEdges.Count; i++)
            {
                Entity e = ProjectEdges[i];
                var site = em.GetComponentData<RoadWorksSite>(e);
                if (SiteRegistry.TryGetEdge(e, out EdgeRecord er))
                {
                    if (er.ClosureApplied > Closure) Closure = er.ClosureApplied;
                    BuildingsWaiting += er.BuildingsWaiting;
                    if (er.ClosureApplied == ClosureLevel.Closed)
                    {
                        OpenLanes |= er.OpenLanesApplied;
                        SoftApplied |= er.SoftApplied;
                        ClosedBApplied |= er.ClosedBApplied;
                        anyClosed = true;
                        // car halves of this edge (Traffic's report; both when unknown) each applied open or CLOSED-B
                        RoadZones present = er.ZonesPresent & RoadZones.Carriageway;
                        if (present == RoadZones.None) present = RoadZones.Carriageway;
                        RoadZones served = (er.OpenLanesApplied | er.ClosedBApplied) & RoadZones.Carriageway;
                        if ((er.OpenLanesApplied & RoadZones.Carriageway) == 0 || (served & present) != present) carKept = false;
                    }
                }
                float len = em.HasComponent<Curve>(e) ? em.GetComponentData<Curve>(e).m_Length : math.abs(site.m_ChainU1 - site.m_ChainU0);
                Paid += math.max(0, site.m_PaidCost);
                if (site.IsUpgrade && site.Mode == VisualMode.HalfWidth)
                {
                    // Upgrade works are never cancelled (the bulldozer ends them with its own refund); the rush floor is the
                    // upgrade's share of a full rebuild of the chain with the new road's class.
                    RushCost += WorkTime.RushCost(site, len, P, rushPercent, FullRebuildFrames(em, e, site, s));
                    continue;
                }
                Refund += WorkTime.CancelRefund(math.max(0, site.m_PaidCost), P, InstantCancel);
                RushCost += WorkTime.RushCost(math.max(0, site.m_PaidCost), len, P, rushPercent);
            }

            CarAccessKept = anyClosed && carKept;

            bool done = IsComplete;
            CanRush = !Rushed && !done && !Incompatible && P < 1f;
            if (!CanRush) RushCost = 0;
            CanAfford = CanRush && EcsUtil.CanAfford(em, city, (int)math.min(RushCost, int.MaxValue));
            if (ModeH)
                CanCancel = false;
            else if (Kind == WorksKind.Construction)
                CanCancel = !done && !Incompatible;
            else
                // Demolition: only D0, and never a cancelled build (calling that off would keep a road already refunded).
                CanCancel = !done && !Incompatible && !CancelledBuild && Phase == WorksPhase.BreakUp;

            // ---- time
            ShiftWindow shift = RRWDebug.IgnoreShift ? ShiftWindow.AllDay : WorkTime.Effective(s != null ? s.Shift : ShiftWindow.Day, Rushed);
            WorkTime.ShiftBounds(shift, out ShiftStart, out _);
            float rate = WorkTime.Rate(Rushed, RRWDebug.WorkTimeScale);
            if (rate <= 0f) rate = WorkTime.Rate(Rushed, 1f);
            double remaining = math.max(0.0, (double)WorkRequired * ((Gravel ? RRWConst.kGravelEndP : 1.0) - P));
            HoursLeft = remaining / rate / RRWConst.kFramesPerHour;
            CalendarFrames = WorkTime.CalendarFramesToFinish(normalizedTime, remaining, shift, rate);

            if (done) Eta = EtaState.Finishing;
            else if (Clearing) Eta = EtaState.Clearing;
            else if (!WorkTime.InShift(normalizedTime, shift)) Eta = EtaState.CrewsOff;
            else Eta = EtaState.Working;
        }

        // Work frames of a full rebuild of the site's chain with the road's (new) class: the reference of an upgrade's rush floor.
        // 0 (floor share 1) without settings.
        public static uint FullRebuildFrames(EntityManager em, Entity edge, in RoadWorksSite site, RRWSetting s) =>
            s == null ? 0u : WorkTime.FullRebuildFrames(site.m_ChainLength, EcsUtil.RoadClass(em, edge), s);

        // Crew count, focus crew and each crew's sweep through its own section. Pure reads of the record.
        // Upgrade works: the crews are the bands of the current window, each along the whole road; a crew's fill is its band's progress.
        private void ComputeCrews(ProjectRecord pr, in ProjectView v)
        {
            Crews = v.CrewCount;
            FocusCrew = math.clamp(pr.FocusCrew, 0, Crews - 1);
            CrewsSpawned = math.max(0, pr.CrewsSpawned);
            float trimmed = v.Trim1 - v.Trim0;
            for (int i = 0; i < CrewFill.Length; i++) CrewFill[i] = 0f;
            if (v.IsUpgrade)
            {
                SectionLength = trimmed > 0f ? trimmed : v.U;
                for (int i = 0; i < Crews && i < CrewFill.Length; i++)
                {
                    int band = v.Upgrade.CrewBand(i);
                    CrewFill[i] = band >= 0 ? math.saturate(v.Upgrade.BandG(band)) : 0f;
                }
                return;
            }
            SectionLength = (trimmed > 0f ? trimmed : v.U) / Crews;
            for (int i = 0; i < Crews && i < CrewFill.Length; i++)
            {
                float a = PhasePlan.SectionStart(i, Crews, v.U), b = PhasePlan.SectionStart(i + 1, Crews, v.U);
                CrewFill[i] = b - a > 1e-3f ? math.saturate((PhasePlan.CrewFront(v, i) - a) / (b - a)) : 1f;
            }
        }

        // Cheap change signature for the per-frame refresh check (no allocation).
        public static uint Signature(EntityManager em, uint projectId, Entity firstEdge)
        {
            if (projectId == 0 || !SiteRegistry.TryGetProject(projectId, out ProjectRecord pr)) return 0xFFFFFFFFu;
            uint h = projectId;
            h = h * 31u + (uint)pr.Phase;
            h = h * 31u + (uint)(math.saturate(pr.Progress) * 1000f);
            h = h * 31u + (uint)pr.Flags;
            h = h * 31u + (pr.Working ? 1u : 0u) + (pr.ClearingTraffic ? 2u : 0u);
            h = h * 31u + (uint)pr.Edges.Count;
            h = h * 31u + (uint)pr.Rerouted;
            h = h * 31u + (uint)pr.ParkedMoved;
            h = h * 31u + (uint)pr.Kind;
            h = h * 31u + (pr.Releasing ? 1u : 0u);
            // Switch step, car-half verdict and the applied lane groups of EVERY edge (a half opens or drains per edge).
            h = h * 31u + (uint)pr.Switch;
            h = h * 31u + (pr.CarHalfAllowed ? 1u : 0u) + ((uint)pr.StageBlockReason << 1);
            h = h * 31u + pr.StageIndex;
            // The crew count (latched per phase) and the focus crew (highlighted segment of the crew gauge).
            h = h * 31u + (uint)pr.Crews;
            h = h * 31u + (uint)pr.FocusCrew;
            // Upgrade works: the current window's traffic primitive and reason (they change without a phase change).
            if (pr.Mode == VisualMode.HalfWidth && pr.Upgrade != null && pr.Upgrade.Schedule.N > 0)
            {
                int lw = math.clamp(pr.Upgrade.LayoutWindowAt(pr.Progress), 0, RRWConst.kUwMaxWindows - 1);
                h = h * 31u + (uint)lw;
                h = h * 31u + (uint)pr.Upgrade.Primitive[lw] + ((uint)pr.Upgrade.WindowReason[lw] << 4);
                h = h * 31u + (uint)pr.Upgrade.Revision;
            }
            uint closure = 0, waiting = 0, open = 0, soft = 0, closedB = 0;
            bool any = false;
            for (int i = 0; i < pr.Edges.Count; i++)
            {
                if (!SiteRegistry.TryGetEdge(pr.Edges[i], out EdgeRecord er)) continue;
                any = true;
                closure = math.max(closure, (uint)er.ClosureApplied);
                waiting += (uint)er.BuildingsWaiting;
                open |= (uint)er.OpenLanesApplied;
                soft |= (uint)er.SoftApplied;
                closedB |= (uint)er.ClosedBApplied;
            }
            if (!any && firstEdge != Entity.Null) h = h * 31u + 7u;
            h = h * 31u + closure;
            h = h * 31u + waiting;
            h = h * 31u + open;
            h = h * 31u + soft;
            h = h * 31u + closedB;
            return h;
        }
    }
}
