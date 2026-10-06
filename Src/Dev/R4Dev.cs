#if DEVTOOLS
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;

// Multi-crew dev aids. Core + dev framework only (Src/Dev is compiled into every module
// build), so nothing here references another module's types.
//
//   rrw.list / rrw.dump        crews=n focus=i spawned=k next=m, per crew: section, working range, front u, sweep; per-crew front
//                              speed vs the front owner's work pace (ok / overWork / frontLag)
//   rrw.crews                  crew table of every project (latched vs. what the latch would pick now)
//   rrw.crews <site> <n|auto>  dev override of the Director's latch (WorksRequestType 14 = SetCrews); reports after 1 s
//                              whether the Director applied it
//   rrw.crews max=<n>          the "Crews per road" setting for this session (takes effect at the next latch: phase change,
//                              rrw.phase / rrw.p, rrw.rebuild)
//   rrw.cam / rrw.focus crew=i follow / frame crew i's front (ChainMap.RenderCrewFront)
//   rrw.perf                   + per-crew numbers (crews, active crews, puppets averaged over the window) and the performance verdict
//   rrw.check                  multi-crew Core invariants (R4Dev.Invariants)
namespace RealisticRoadWorks.V3.DevCmds
{
    internal static class R4Dev
    {
        // WorksRequestType.SetCrews (= 14). Value = n
        // (1..kMaxCrewsPerProject) pins the crew count of the project until the next phase change; 0 = back to the automatic latch.
        public const WorksRequestType kSetCrews = WorksRequestType.SetCrews;

        public static int CrewsMax => RRWSettings.Current != null ? RRWSettings.Current.CrewsMax : RRWConst.kDefaultCrewsPerProject;

        static string F2(float v) => float.IsNaN(v) ? "nan" : v.ToString("0.00", CultureInfo.InvariantCulture);
        static string F3(float v) => float.IsNaN(v) ? "nan" : v.ToString("0.000", CultureInfo.InvariantCulture);

        // Crew count the latch would pick NOW (the Director re-latches only at its latch events: phase change, rebuild).
        public static int Expected(ProjectRecord p) => PhasePlan.CrewCount(p.View(), CrewsMax);

        // Front owner's limits vs the per-crew front speed: ok (<= VWork), overWork (<= VFwd), frontLag (> VFwd).
        public static string SpeedText(ProjectRecord p)
        {
            var v = p.View();
            float vc = PhasePlan.CrewFrontSpeed(v);
            float vw = MachineLimits.WorkSpeed(v.Kind, v.Phase);
            if (!(vc > 0f) || !(vw > 0f)) return "vCrew=" + F3(vc) + " vWork=" + F3(vw) + " (n/a)";
            var plan = PhasePlan.Crew(v, 0);
            MachineRole owner = plan.FrontOwner(v.Phase);
            var lim = MachineLimits.Of(owner, v.Phase);
            string status = vc <= vw * (1f + 1e-3f) ? "ok" : vc <= lim.VFwd ? "overWork" : "frontLag";
            return "vCrew=" + F3(vc) + " vWork=" + F3(vw) + " vFwd(" + owner + ")=" + F2(lim.VFwd) + " " + status;
        }

        // Appended to the rrw.list line of a project.
        public static string ListFlags(ProjectRecord p)
        {
            int next = Expected(p);
            return " crews=" + p.Crews + " focus=" + p.FocusCrew + " spawned=" + p.CrewsSpawned
                   + (next != p.Crews ? " next=" + next : "") + " " + SpeedText(p);
        }

        // One line per crew (rrw.list, only with more than one crew; rrw.dump always).
        public static void CrewLines(ProjectRecord p, StringBuilder sb, string prefix)
        {
            var v = p.View();
            int n = v.CrewCount;
            var fs = FrontSet.Main(v);
            for (int i = 0; i < n; i++)
            {
                float a = PhasePlan.SectionStart(i, n, v.U), b = PhasePlan.SectionStart(i + 1, n, v.U);
                float f = PhasePlan.CrewFront(v, i);
                var plan = PhasePlan.Crew(v, i);
                sb.Append(prefix).Append("crew ").Append(i).Append('/').Append(n)
                  .Append(" sec=[").Append(DevSites.F1(a)).Append(',').Append(DevSites.F1(b)).Append("] len=").Append(DevSites.F1(b - a))
                  .Append(" range=[").Append(DevSites.F1(plan.SecLo)).Append(',').Append(DevSites.F1(plan.SecHi)).Append(']')
                  .Append(" F=").Append(DevSites.F1(f)).Append(" Q=").Append(DevSites.F1(fs.Q(i)))
                  .Append(" sweep=").Append(F3(b - a > 1e-3f ? math.saturate((f - a) / (b - a)) : 1f))
                  .Append(i == p.FocusCrew ? " focus" : "").Append('\n');
            }
        }

        public static void DumpProject(ProjectRecord p, StringBuilder sb)
        {
            var v = p.View();
            sb.Append("  r4 crews=").Append(p.Crews).Append(" focus=").Append(p.FocusCrew).Append(" spawned=").Append(p.CrewsSpawned)
              .Append(" next=").Append(Expected(p)).Append(" setting=").Append(CrewsMax)
              .Append(" workSeconds=").Append(DevSites.F1(p.WorkSeconds)).Append(" phaseSeconds=").Append(DevSites.F1(v.PhaseSeconds))
              .Append(" leadIn=").Append(DevSites.F1(PhasePlan.LeadInSeconds(v))).Append("s handOver=").Append(DevSites.F1(PhasePlan.HandOverSeconds(v))).Append('s')
              .Append(" vSingle=").Append(F3(PhasePlan.SingleFrontSpeed(v.Phase, v.U, v.WorkSeconds, v.Phase == WorksPhase.Finishing && v.SwapActive)))
              .Append(' ').Append(SpeedText(p));
            if ((p.Flags & SiteFlags.CancelledBuild) != 0) sb.Append(" cancelCrews=").Append(v.CancelCrewCount);
            sb.Append('\n');
            CrewLines(p, sb, "  r4 ");
        }

        // Multi-crew Core invariants (rrw.check). Every line names the module that has to fix it.
        public static void Invariants(EntityManager em, List<string> problems)
        {
            int activeCrews = 0;
            bool anyFresh = false;
            uint now = RRWClock.UpdateIndex;
            foreach (var p in SiteRegistry.Projects.Values)
            {
                string n = "p" + p.Id + " (crews)";
                if (p.Crews < 1 || p.Crews > RRWConst.kMaxCrewsPerProject)
                    problems.Add(n + " Crews=" + p.Crews + " outside [1, " + RRWConst.kMaxCrewsPerProject + "] (Director latch)");
                var view = p.View();
                int crews = view.CrewCount;
                bool upgrade = view.IsUpgrade;   // mode H: one crew per band of the window, not chain sections
                if (p.FocusCrew < 0 || p.FocusCrew >= crews)
                    problems.Add(n + " FocusCrew=" + p.FocusCrew + " outside [0, " + (crews - 1) + "] (Director)");
                if (crews > 1 && p.Mode != VisualMode.FullDig && !upgrade)
                    problems.Add(n + " mode " + p.Mode + " with " + crews + " crews (only mode A splits into sections)");
                if (upgrade && crews > math.max(1, view.Upgrade.BandsIn(view.Upgrade.LayoutWindow)))
                    problems.Add(n + " mode H with " + crews + " crews but " + view.Upgrade.BandsIn(view.Upgrade.LayoutWindow) + " bands in window "
                                 + view.Upgrade.LayoutWindow + " (one crew per band)");
                if (crews > 1 && !upgrade)
                {
                    float trimmed = (p.TrimU1 > 0f ? p.TrimU1 : p.ChainLength) - p.TrimU0;
                    float sec = (trimmed > 0f ? trimmed : p.ChainLength) / crews;
                    if (sec < RRWConst.kMinSectionLength - 1f)
                        problems.Add(n + " sections of " + DevSites.F1(sec) + " m < kMinSectionLength " + RRWConst.kMinSectionLength
                                     + " (" + crews + " crews on " + DevSites.F1(trimmed) + " m; the Director latch and SetCrews never allow this)");
                }
                if (PhasePlan.HasMachines(p.Mode) && p.Phase != WorksPhase.Complete && p.Phase != WorksPhase.None && !p.Releasing && !(p.WorkSeconds > 0f))
                    problems.Add(n + " WorkSeconds=" + DevSites.F(p.WorkSeconds) + " (Director must write it every update; without it the return-aware anchors are off)");
                // RoadWorksRuntime.m_Crews on every edge = the project's latch (0 = one crew)
                for (int i = 0; i < p.Edges.Count; i++)
                {
                    Entity e = p.Edges[i];
                    if (!em.Exists(e) || !em.HasComponent<RoadWorksRuntime>(e)) continue;
                    int rc = math.max(1, (int)em.GetComponentData<RoadWorksRuntime>(e).m_Crews);
                    if (rc != crews)
                    {
                        problems.Add(n + " edge " + RRWLog.E(e) + " runtime m_Crews=" + rc + " != project Crews " + crews + " (Director writes every edge every update; Ground depth uses it)");
                        break;
                    }
                }
                if (p.CrewsSpawned < 0 || p.CrewsSpawned > RRWConst.kMaxCrewsPerProject)
                    problems.Add(n + " CrewsSpawned=" + p.CrewsSpawned + " outside [0, " + RRWConst.kMaxCrewsPerProject + "] (Machines report)");
                if (p.MachinesReportFresh(now))
                {
                    anyFresh = true;
                    activeCrews += math.max(0, p.CrewsSpawned);
                }
            }
            if (anyFresh && activeCrews > RRWConst.kMaxActiveCrewsGlobal)
                problems.Add("crews: " + activeCrews + " crews with puppets > kMaxActiveCrewsGlobal " + RRWConst.kMaxActiveCrewsGlobal + " (Machines budget)");
        }
    }

    // Per-frame crew sampling for rrw.perf (target: Machines < 0.05 ms per ACTIVE crew): averages over the measuring window so a
    // crew that spawns or leaves mid-window is weighted by its time on site. Ticked by DevCameraSystem (PreCulling, every frame).
    internal static class PerfCrews
    {
        public static bool Running;
        static long s_Frames, s_Projects, s_Active, s_Crews, s_Spawned, s_Puppets;
        static int s_MaxActive, s_MaxCrews, s_MaxPuppets;

        public static void Begin()
        {
            Running = true;
            s_Frames = s_Projects = s_Active = s_Crews = s_Spawned = s_Puppets = 0;
            s_MaxActive = s_MaxCrews = s_MaxPuppets = 0;
        }

        public static void Sample()
        {
            if (!Running) return;
            int projects = 0, crews = 0, spawned = 0, puppets = 0;
            foreach (var p in SiteRegistry.Projects.Values)
            {
                if (p.Phase == WorksPhase.Complete) continue;
                projects++;
                crews += p.View().CrewCount;
                spawned += math.max(0, p.CrewsSpawned);
                puppets += math.max(0, p.MachineCount);
            }
            s_Frames++;
            s_Projects += projects;
            s_Crews += crews;
            s_Spawned += spawned;
            s_Puppets += puppets;
            s_MaxCrews = math.max(s_MaxCrews, crews);
            s_MaxActive = math.max(s_MaxActive, spawned);
            s_MaxPuppets = math.max(s_MaxPuppets, puppets);
        }

        // "perf crews ..." + "perf targets ..." lines. machinesMs / totalMs: whole-window sums; worstMs/worstSlot: the largest single
        // system update of the window.
        public static void Append(StringBuilder sb, int frames, double machinesMs, double totalMs, double worstMs, string worstSlot)
        {
            Running = false;
            if (s_Frames == 0) Sample();     // "rrw.perf 0": one sample now
            Running = false;
            double fr = System.Math.Max(1, s_Frames);
            double activeAvg = s_Spawned / fr;
            double perCrew = activeAvg > 1e-3 ? machinesMs / frames / activeAvg : double.NaN;
            var ci = CultureInfo.InvariantCulture;
            sb.Append("perf crews projects=").Append((s_Projects / fr).ToString("0.0", ci))
              .Append(" crews=").Append((s_Crews / fr).ToString("0.0", ci)).Append(" (max ").Append(s_MaxCrews).Append(')')
              .Append(" activeCrews=").Append(activeAvg.ToString("0.0", ci)).Append(" (max ").Append(s_MaxActive).Append(", cap ").Append(RRWConst.kMaxActiveCrewsGlobal).Append(')')
              .Append(" puppets=").Append((s_Puppets / fr).ToString("0.0", ci)).Append(" (max ").Append(s_MaxPuppets).Append(", cap ").Append(RRWConst.kMaxMachinesGlobal).Append(')')
              .Append(" machines/activeCrew=").Append(double.IsNaN(perCrew) ? "n/a" : perCrew.ToString("0.0000", ci)).Append("ms\n");
            double totalAvg = totalMs / frames;
            sb.Append("perf targets total=").Append(totalAvg.ToString("0.000", ci)).Append("ms/f<0.6 ").Append(totalAvg < 0.6 ? "OK" : "FAIL")
              .Append(" | machines/activeCrew=").Append(double.IsNaN(perCrew) ? "n/a" : perCrew.ToString("0.0000", ci)).Append("ms<0.05 ")
              .Append(double.IsNaN(perCrew) ? "n/a" : perCrew < 0.05 ? "OK" : "FAIL")
              .Append(" | worst update=").Append(worstMs.ToString("0.00", ci)).Append("ms (").Append(worstSlot ?? "-").Append(")<2 ")
              .Append(worstMs < 2.0 ? "OK" : "FAIL").Append('\n');
        }
    }

    public sealed class RrwCrewsCommand : IDevCommand
    {
        public string Name => "rrw.crews";
        public string Help => "rrw.crews [site [n|auto]] [max=<1..6>] - crew table; <site> <n> pins the crew count (Director SetCrews, until the next phase), " +
                              "auto = automatic latch; max= sets the 'Crews per road' setting for this session (applies at the next latch)";

        public void Run(DevContext ctx, string[] a)
        {
            string max = DevSites.Opt(a, "max", null);
            if (max != null)
            {
                var s = RRWSettings.Current;
                if (s == null || !DevSites.TryInt(max, out int m)) { DevSites.Out(ctx, "crews: bad max '" + max + "'"); return; }
                s.MaxCrewsPerProject = math.clamp(m, 1, RRWConst.kMaxCrewsPerProject);
                DevSites.Out(ctx, "crews setting 'Crews per road' = " + s.CrewsMax + " (session only; the Director re-latches at the next phase change, rrw.phase / rrw.p or rrw.rebuild)");
            }
            var pos = DevSites.Positional(a);
            if (pos.Count == 0) { Table(ctx); return; }
            var list = new List<ProjectRecord>();
            if (!DevSites.Resolve(ctx, pos[0], list, out _, out string err)) { DevSites.Out(ctx, "crews: " + err); return; }
            if (pos.Count == 1) { foreach (var p in list) Line(ctx, p); return; }
            int n;
            if (pos[1].Equals("auto", System.StringComparison.OrdinalIgnoreCase) || pos[1] == "0") n = 0;
            else if (!DevSites.TryInt(pos[1], out n) || n < 1 || n > RRWConst.kMaxCrewsPerProject)
            {
                DevSites.Out(ctx, "crews: n must be 1.." + RRWConst.kMaxCrewsPerProject + " or auto");
                return;
            }
            foreach (var p in list)
            {
                Req.Enqueue(p, R4Dev.kSetCrews, n);
                DevSites.Out(ctx, "crews request SetCrews(14) p" + p.Id + " " + p.Crews + " -> " + (n == 0 ? "auto (" + R4Dev.Expected(p) + ")" : n.ToString()));
                uint id = p.Id;
                int want = n == 0 ? -1 : n;
                DevDeferred.After(1f, () =>
                {
                    if (!SiteRegistry.TryGetProject(id, out var q)) return;
                    if (want < 0) { DevSites.Out(ctx, "crews p" + id + " now " + q.Crews + " (auto latch would pick " + R4Dev.Expected(q) + ")"); return; }
                    DevSites.Out(ctx, q.Crews == want
                        ? "crews p" + id + " applied: " + q.Crews + " crews"
                        : "crews p" + id + " not " + want + " after 1 s: Crews=" + q.Crews + " (capped so no section is under "
                          + RRWConst.kMinSectionLength + " m, mode D / complete = 1 crew, or the game is paused)");
                });
            }
        }

        private static void Table(DevContext ctx)
        {
            var list = DevSites.Sorted();
            DevSites.Out(ctx, "crews projects=" + list.Count + " setting=" + R4Dev.CrewsMax + " max=" + RRWConst.kMaxCrewsPerProject
                              + " section=[" + RRWConst.kMinSectionLength + "," + RRWConst.kMaxSectionLength + "]m activeCap=" + RRWConst.kMaxActiveCrewsGlobal);
            foreach (var p in list) Line(ctx, p);
        }

        private static void Line(DevContext ctx, ProjectRecord p)
        {
            var sb = new StringBuilder();
            sb.Append("crews #").Append(DevSites.ListIndex(p)).Append(" p").Append(p.Id).Append(' ').Append(DevSites.Code(p.Phase))
              .Append(" U=").Append(DevSites.F1(p.ChainLength)).Append(R4Dev.ListFlags(p)).Append('\n');
            R4Dev.CrewLines(p, sb, "crews   ");
            DevSites.Lines(ctx, sb);
        }
    }
}
#endif
