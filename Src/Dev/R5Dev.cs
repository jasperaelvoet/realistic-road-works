#if DEVTOOLS
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;

// Road roller and IK dig dev aids. Core + dev framework only (Src/Dev is compiled into every module build), so nothing
// here references another module's types; the runtime side of rollers / IK digging is Machines' rrw.mx.roller / rrw.mx.dig.
//
//   rrw.list               + rollers=<latched> spawned=<RollersSpawned> (setting=off when RollersOn is off)
//   rrw.dump               "r5" lines: chain exits + ChainCarHalfBlock verdict, per crew the roller slots of PhasePlan.Crew (duty,
//                          activity, window, gap to the crew front, crew truck, relay, works half, pass speed, vibration) and, in
//                          C1 / D0 / D1, the nominal DigSchedule with the hop-window verdict of the crew front speed
//   rrw.rollers            roller plan table (Core); on|off = the "Road rollers" setting for this session; preview = plan as if the
//                          Director had latched rollers; sweep[=step] = the plan over the phase; phase=<C2|C3|D2> = another phase
//   rrw.dig                IK dig schedule (Core DigSchedule): keys, events, hop windows, truck load per dump, departure time;
//                          <site> adds the hop verdict for its crews; detailed=on|off / dust=on|off = settings for this session
//   rrw.check              roller Core invariants (R5Dev.Invariants)
namespace RealisticRoadWorks.V3.DevCmds
{
    internal static class R5Dev
    {
        static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        static string F2(float v) => float.IsNaN(v) ? "nan" : v.ToString("0.00", Ci);
        static string F1(float v) => DevSites.F1(v);

        public static RRWSetting S => RRWSettings.Current;
        public static bool RollersOn => S != null && S.RollersOn;
        public static int ExpectedRollers => RollersOn ? RRWConst.kMaxRollersPerCrew : 0;

        // ------------------------------------------------------------------ rrw.list / rrw.dump

        public static string ListFlags(ProjectRecord p)
        {
            string s = " rollers=" + p.Rollers + " spawned=" + p.RollersSpawned;
            if (p.Rollers != ExpectedRollers) s += "(setting " + (RollersOn ? "on" : "off") + ", relatch pending)";
            StageBlockReason chain = ChainVerdict(p);
            if (chain != StageBlockReason.None) s += " chain=" + chain;
            return s;
        }

        public static StageBlockReason ChainVerdict(ProjectRecord p) =>
            p.ExitsKnown ? PhasePlan.ChainCarHalfBlock(p.ExitAtStart, p.ExitAtEnd, RRWGates.DeadEndRule) : StageBlockReason.None;   // exits not computed yet: no verdict (Director rule)

        public static void DumpProject(ProjectRecord p, StringBuilder sb)
        {
            var v = p.View();
            var chain = ChainVerdict(p);
            bool halfOpen = (p.OpenLanes & RoadZones.Carriageway) != RoadZones.None;
            sb.Append("  r5 rollers latched=").Append(p.Rollers).Append(" spawned=").Append(p.RollersSpawned)
              .Append(" setting=").Append(RollersOn ? "on" : "off").Append(" (expected latch ").Append(ExpectedRollers).Append(')')
              .Append(" | chain exits start=").Append(p.ExitAtStart ? "yes" : "no").Append(" end=").Append(p.ExitAtEnd ? "yes" : "no")
              .Append(" deadEndRule=").Append(RRWGates.DeadEndRule ? 1 : 0).Append(" verdict=").Append(chain)
              .Append(chain != StageBlockReason.None && halfOpen ? " CAR HALF OPEN (P3a)" : "").Append('\n');
            RollerLines(v, sb, "  r5 ");
            if (DigModeOf(v.Phase, out DigMode mode))
            {
                var ds = DigSchedule.Nominal(mode);
                sb.Append("  r5 dig ").Append(mode).Append(" nominal: ").Append(ScheduleText(ds)).Append('\n');
                sb.Append("  r5 dig hop ").Append(HopVerdict(v, ds)).Append('\n');
            }
        }

        // One line per planned roller slot of every crew (nothing when the phase plans none).
        public static int RollerLines(in ProjectView v, StringBuilder sb, string prefix)
        {
            int n = v.CrewCount, lines = 0;
            for (int i = 0; i < n; i++)
            {
                var plan = PhasePlan.Crew(v, i);
                for (int r = 0; r < 2; r++)
                {
                    var rs = plan.Roller(r);
                    if (!rs.Active && rs.Duty == RollerDuty.None) continue;
                    sb.Append(prefix).Append("crew ").Append(i).Append('/').Append(n).Append(" R").Append(r).Append(' ').Append(SlotText(plan, rs)).Append('\n');
                    lines++;
                }
                if (plan.Rollers == 0 && v.Rollers > 0 && PlansRollers(v.Phase))
                {
                    sb.Append(prefix).Append("crew ").Append(i).Append('/').Append(n).Append(" no roller slot (sec ")
                      .Append(F1(plan.SecHi - plan.SecLo)).Append(" m, RollerCountFor=").Append(PhasePlan.RollerCountFor(v, plan.SecHi - plan.SecLo)).Append(")\n");
                    lines++;
                }
            }
            return lines;
        }

        public static bool PlansRollers(WorksPhase ph) => ph == WorksPhase.Foundation || ph == WorksPhase.Paving || ph == WorksPhase.Restore;

        public static string SlotText(in CrewPlan plan, in RollerSlot rs)
        {
            var sb = new StringBuilder();
            sb.Append(rs.Slot.Activity).Append(' ').Append(rs.Duty);
            if (rs.Slot.Activity == MachineActivity.Compact)
                sb.Append(" win=[").Append(F1(rs.WinLo)).Append(',').Append(F1(rs.WinHi)).Append("] len=").Append(F1(rs.WinHi - rs.WinLo))
                  .Append(" F-top=").Append(F1(plan.FrontU - rs.WinHi));
            else sb.Append(" u=").Append(F1(rs.Slot.U)).Append(" facing=").Append(rs.Slot.Facing);
            sb.Append(" F=").Append(F1(plan.FrontU)).Append(" sec=[").Append(F1(plan.SecLo)).Append(',').Append(F1(plan.SecHi)).Append(']');
            if (plan.CrewTruck.Active) sb.Append(" crewTruck=").Append(F1(plan.CrewTruck.U));
            sb.Append(" v=").Append(F2(rs.PassSpeed)).Append(rs.Vibrate ? " vib" : " static");
            if (rs.RelayOnly) sb.Append(" RELAY-ONLY");
            if (rs.WorksHalfOnly) sb.Append(" works-half");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ dig schedule

        public static bool DigModeOf(WorksPhase ph, out DigMode mode)
        {
            mode = ph == WorksPhase.BreakUp ? DigMode.Break : DigMode.Dig;
            return ph == WorksPhase.Excavation || ph == WorksPhase.BreakUp || ph == WorksPhase.Removal;
        }

        public static string ScheduleText(in DigSchedule s)
        {
            var sb = new StringBuilder();
            sb.Append("period=").Append(F2(s.Period)).Append(" keys(end) lower=").Append(F2(s.TLower)).Append(" penetrate=").Append(F2(s.TPenetrate))
              .Append(" drag=").Append(F2(s.TDrag)).Append(" curl=").Append(F2(s.TCurl)).Append(" lift=").Append(F2(s.TLift))
              .Append(" swing=").Append(F2(s.TSwing)).Append('(').Append(F2(s.Swing)).Append("s) dump=").Append(F2(s.TDump))
              .Append(" shake=").Append(F2(s.TShake)).Append(" return=").Append(F2(s.TReturn)).Append('(').Append(F2(s.Return))
              .Append("s) hold=").Append(F2(s.Hold)).Append("s | breakoutT=").Append(F2(s.BreakoutT)).Append(" dumpT=").Append(F2(s.DumpT))
              .Append(" stationaryEnd=").Append(F2(s.StationaryEnd)).Append(" hopDefault=").Append(F2(s.HopDefault))
              .Append(" hopMax=").Append(F2(s.HopWindowMax)).Append(s.Compressed ? " COMPRESSED" : "");
            if (s.Mode == DigMode.Break)
            {
                sb.Append(" strikes=");
                for (int i = 0; i < RRWConst.kBreakStrikes; i++) sb.Append(i > 0 ? "," : "").Append(F2(s.StrikeT(i)));
            }
            return sb.ToString();
        }

        // Longest rest-to-rest distance in T seconds at (V, A).
        public static float MaxDist(float T, float V, float A)
        {
            if (!(T > 0f) || !(V > 0f) || !(A > 0f)) return 0f;
            return T <= 2f * V / A ? A * T * T * 0.25f : V * (T - V / A);
        }

        // Shortest rest-to-rest time for distance d at (V, A).
        public static float MinTime(float d, float V, float A)
        {
            if (!(d > 0f)) return 0f;
            if (!(V > 0f) || !(A > 0f)) return float.PositiveInfinity;
            return d <= V * V / A ? 2f * math.sqrt(d / A) : d / V + V / A;
        }

        // Per cycle the excavator (and its loading truck) hop vCrew x Period inside the hop window.
        public static string HopVerdict(in ProjectView v, in DigSchedule ds)
        {
            float vc = PhasePlan.CrewFrontSpeed(v);
            var lim = MachineLimits.Of(MachineRole.Excavator, v.Phase);
            float d = (vc > 0f ? vc : 0f) * ds.Period;
            float need = MinTime(d, lim.VFwd, lim.Accel);
            float covDef = MaxDist(ds.HopDefault, lim.VFwd, lim.Accel), covMax = MaxDist(ds.HopWindowMax, lim.VFwd, lim.Accel);
            string verdict = !(vc > 0f) ? "front not moving (unknown WorkSeconds?)"
                : need <= ds.HopDefault + 1e-3f ? "fits the default window"
                : need <= ds.HopWindowMax + 1e-3f ? "window grows to " + F2(math.ceil(need * 2f) * 0.5f) + " s"
                : "LAGS (needs " + F2(need) + " s > hopMax: frontLag expected)";
            return "F0=" + v.CrewFront(0).ToString("0.0", Ci) + "m vCrew=" + vc.ToString("0.000", Ci) + "m/s hop/cycle=" + F2(d) + "m needs " + F2(need)
                   + "s at excavator vFwd=" + F2(lim.VFwd) + " accel=" + F2(lim.Accel) + " | covers " + F2(covDef) + "m in hopDefault, "
                   + F2(covMax) + "m in hopMax, VWork x period=" + F2(lim.VWork * ds.Period) + "m | " + verdict;
        }

        // ------------------------------------------------------------------ rrw.check (Core invariants)

        // RollersOn vs latched Rollers mismatches, sampled every kSampleEvery rendered updates (DevCameraSystem): the Director only
        // re-latches at a Crews latch event, so a mismatch is a problem once it survived a phase change. Also tracks a car half that
        // stays open on a chain ChainCarHalfBlock blocks, which the Director must re-close at once (trap reason).
        const int kSampleEvery = 30;
        const int kHalfOpenSamples = 4;   // ~2 s of consecutive samples (the Director decides OpenLanes every update)
        static int s_Tick;
        static readonly Dictionary<uint, WorksPhase> s_MismatchPhase = new Dictionary<uint, WorksPhase>();
        static readonly Dictionary<uint, int> s_HalfOpenOnBlocked = new Dictionary<uint, int>();

        public static void Sample()
        {
            if (++s_Tick < kSampleEvery) return;
            s_Tick = 0;
            if (SiteRegistry.Projects.Count == 0) { s_MismatchPhase.Clear(); s_HalfOpenOnBlocked.Clear(); return; }
            int want = ExpectedRollers;
            foreach (var p in SiteRegistry.Projects.Values)
            {
                if (p.Rollers != want && p.Phase != WorksPhase.Complete && p.Phase != WorksPhase.None)
                {
                    if (!s_MismatchPhase.ContainsKey(p.Id)) s_MismatchPhase[p.Id] = p.Phase;
                }
                else s_MismatchPhase.Remove(p.Id);
                bool open = (p.OpenLanes & RoadZones.Carriageway) != RoadZones.None;
                if (open && ChainVerdict(p) != StageBlockReason.None)
                    s_HalfOpenOnBlocked[p.Id] = (s_HalfOpenOnBlocked.TryGetValue(p.Id, out int c) ? c : 0) + 1;
                else s_HalfOpenOnBlocked.Remove(p.Id);
            }
            if (s_MismatchPhase.Count > 0) Prune(s_MismatchPhase);
            if (s_HalfOpenOnBlocked.Count > 0) Prune(s_HalfOpenOnBlocked);
        }

        static void Prune<T>(Dictionary<uint, T> d)
        {
            List<uint> gone = null;
            foreach (var id in d.Keys) if (!SiteRegistry.Projects.ContainsKey(id)) (gone ?? (gone = new List<uint>())).Add(id);
            if (gone != null) foreach (var id in gone) d.Remove(id);
        }

        public static void Invariants(EntityManager em, List<string> problems)
        {
            uint now = RRWClock.UpdateIndex;
            int liveRollers = 0;
            bool anyFresh = false;
            const float tol = 0.05f;
            foreach (var p in SiteRegistry.Projects.Values)
            {
                string n = "p" + p.Id + " (rollers)";
                var v = p.View();
                if (p.Rollers < 0 || p.Rollers > RRWConst.kMaxRollersPerCrew)
                    problems.Add(n + " Rollers=" + p.Rollers + " outside [0, " + RRWConst.kMaxRollersPerCrew + "] (Director latch)");
                if (s_MismatchPhase.TryGetValue(p.Id, out WorksPhase since) && since != p.Phase && p.Rollers != ExpectedRollers)
                    problems.Add(n + " Rollers=" + p.Rollers + " but the 'Road rollers' setting is " + (RollersOn ? "on" : "off") + " since " + DevSites.Code(since)
                                 + " (now " + DevSites.Code(p.Phase) + "): the Director must re-latch Rollers with Crews at the phase change");
                if (s_HalfOpenOnBlocked.TryGetValue(p.Id, out int samples) && samples >= kHalfOpenSamples)
                    problems.Add(n + " car half " + R2Dev.Zones(p.OpenLanes & RoadZones.Carriageway) + " open for ~" + (samples * kSampleEvery / 60) + " s on a chain ChainCarHalfBlock blocks ("
                                 + ChainVerdict(p) + ", exits start=" + (p.ExitAtStart ? "yes" : "no") + " end=" + (p.ExitAtEnd ? "yes" : "no") + "; Director trap reason)");
                if (p.RollersSpawned < 0) problems.Add(n + " RollersSpawned=" + p.RollersSpawned + " < 0 (Machines report)");
                if (p.MachinesReportFresh(now))
                {
                    anyFresh = true;
                    liveRollers += math.max(0, p.RollersSpawned);
                    if (p.RollersSpawned > p.MachineCount)
                        problems.Add(n + " RollersSpawned=" + p.RollersSpawned + " > MachineCount=" + p.MachineCount + " (rollers count in MachineCount; Machines report)");
                }
                CheckSlots(p, v, n, tol, problems);
            }
            if (anyFresh && liveRollers > RRWConst.kMaxRollersGlobal)
                problems.Add("rollers: " + liveRollers + " live rollers > kMaxRollersGlobal " + RRWConst.kMaxRollersGlobal + " (Machines budget)");
        }

        // Roller slots of PhasePlan.Crew (pure): inside the trims and their crew section (relay / drive-out slots excepted), windows
        // behind the crew front by the duty's distance, ahead of the crew truck, long enough, only in C2 / C3 / D2.
        static void CheckSlots(ProjectRecord p, in ProjectView v, string n, float tol, List<string> problems)
        {
            if (v.Rollers <= 0 || v.Phase == WorksPhase.Complete || v.Phase == WorksPhase.None) return;
            int crews = v.CrewCount;
            for (int i = 0; i < crews; i++)
            {
                var plan = PhasePlan.Crew(v, i);
                if (plan.Rollers > 0 && !PlansRollers(v.Phase))
                    problems.Add(n + " crew " + i + " plans " + plan.Rollers + " roller(s) in " + v.Phase + " (only C2 / C3 / D2)");
                if (plan.Rollers > v.Rollers)
                    problems.Add(n + " crew " + i + " plans " + plan.Rollers + " rollers > latched " + v.Rollers);
                for (int r = 0; r < 2; r++)
                {
                    var rs = plan.Roller(r);
                    if (!rs.Active) continue;
                    string w = n + " crew " + i + " R" + r + " " + rs.Slot.Activity + " " + rs.Duty;
                    float u = rs.Slot.U;
                    if (!math.isfinite(u) || u < v.Trim0 - tol || u > v.Trim1 + tol)
                        problems.Add(w + " anchor u=" + F1(u) + " outside the trims [" + F1(v.Trim0) + ", " + F1(v.Trim1) + "] (Core PhasePlan.Crew)");
                    bool exempt = rs.RelayOnly || rs.Slot.Activity == MachineActivity.DriveOut;
                    if (!exempt && (u < plan.SecLo - tol || u > plan.SecHi + tol))
                        problems.Add(w + " anchor u=" + F1(u) + " outside its section [" + F1(plan.SecLo) + ", " + F1(plan.SecHi) + "] (Core)");
                    if (rs.RelayOnly && (i == 0 || v.Phase != WorksPhase.Paving))
                        problems.Add(w + " is RelayOnly outside C3 / on crew 0 (only crew > 0 in C3)");
                    if (rs.WorksHalfOnly && !(v.Phase == WorksPhase.Paving && v.F >= RRWConst.kRollerC3WorksHalfF - 1e-4f))
                        problems.Add(w + " WorksHalfOnly in " + v.Phase + " f=" + F2(v.F) + " (only C3 f >= " + RRWConst.kRollerC3WorksHalfF + ")");
                    if (rs.Slot.Activity != MachineActivity.Compact) continue;
                    float back = rs.Duty == RollerDuty.Breakdown ? RRWConst.kRollerC3Back0 : rs.Duty == RollerDuty.Finish ? RRWConst.kRollerC3Back1 : RRWConst.kRollerC2Back;
                    if (!(rs.WinHi - rs.WinLo >= RRWConst.kRollerMinPass - tol))
                        problems.Add(w + " window [" + F1(rs.WinLo) + ", " + F1(rs.WinHi) + "] shorter than kRollerMinPass " + RRWConst.kRollerMinPass + " (Core)");
                    if (rs.WinHi > plan.FrontU - back + tol)
                        problems.Add(w + " window top " + F1(rs.WinHi) + " less than " + back + " m behind the crew front " + F1(plan.FrontU) + " (Core)");
                    if (rs.WinLo < plan.SecLo + RRWConst.kRollerEndClear - tol || rs.WinHi > plan.SecHi - RRWConst.kRollerEndClear + tol)
                        problems.Add(w + " window [" + F1(rs.WinLo) + ", " + F1(rs.WinHi) + "] not inside its section [" + F1(plan.SecLo) + ", " + F1(plan.SecHi) + "] - kRollerEndClear (Core)");
                    if (v.Phase == WorksPhase.Paving && plan.CrewTruck.Active && rs.WinLo < plan.CrewTruck.U + RRWConst.kRollerCrewTruckGap - tol)
                        problems.Add(w + " window bottom " + F1(rs.WinLo) + " closer than " + RRWConst.kRollerCrewTruckGap + " m to the crew truck at " + F1(plan.CrewTruck.U) + " (Core)");
                }
            }
        }
    }

    // rrw.rollers [site|all] [on|off] [preview] [sweep[=step]] [phase=<C2|C3|D2>]
    public sealed class RrwRollersCommand : IDevCommand
    {
        public string Name => "rrw.rollers";
        public string Help => "rrw.rollers [site|all] [on|off] [preview] [sweep[=0.1]] [phase=C2|C3|D2] - roller plan (Core PhasePlan.Crew) per crew; on|off = the " +
                              "'Road rollers' setting for this session (the Director re-latches at the next phase change / rrw.rebuild / rrw.crews <site> n); " +
                              "preview = as if latched; sweep = plan over f (phase= another phase of the kind). Runtime units: rrw.mx.roller";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            bool preview = false, sweep = false;
            float step = DevSites.OptF(a, "sweep", 0.1f);
            for (int i = pos.Count - 1; i >= 0; i--)
            {
                string w = pos[i].ToLowerInvariant();
                if (w == "on" || w == "off")
                {
                    var s = RRWSettings.Current;
                    if (s == null) { DevSites.Out(ctx, "rollers: no settings"); return; }
                    s.Rollers = w == "on";
                    DevSites.Out(ctx, "rollers setting 'Road rollers' = " + w + " (session only) -> RollersOn=" + s.RollersOn
                                      + " (MachinesOn=" + s.MachinesOn + "); latched values change at the next Crews latch (phase change, rrw.rebuild, rrw.crews <site> <n>); "
                                      + "Machines also stops spawning at once and live rollers leave");
                    pos.RemoveAt(i);
                }
                else if (w == "preview") { preview = true; pos.RemoveAt(i); }
                else if (w == "sweep") { sweep = true; pos.RemoveAt(i); }
            }
            if (DevSites.Opt(a, "sweep", null) != null) sweep = true;
            step = math.clamp(step, 0.01f, 0.5f);
            string phArg = DevSites.Opt(a, "phase", null);
            WorksPhase phase = WorksPhase.None;
            if (phArg != null && !DevSites.TryPhase(phArg, out phase)) { DevSites.Out(ctx, "rollers: bad phase '" + phArg + "'"); return; }

            var list = new List<ProjectRecord>();
            if (pos.Count == 0) list.AddRange(DevSites.Sorted());
            else if (!DevSites.Resolve(ctx, pos[0], list, out _, out string err)) { DevSites.Out(ctx, "rollers: " + err); return; }
            int liveSum = 0;
            foreach (var p in list) liveSum += math.max(0, p.RollersSpawned);
            DevSites.Out(ctx, "rollers setting=" + (R5Dev.RollersOn ? "on" : "off") + " (expected latch " + R5Dev.ExpectedRollers + ") projects=" + list.Count
                              + " spawned=" + liveSum + " caps perCrew=" + RRWConst.kMaxRollersPerCrew + " global=" + RRWConst.kMaxRollersGlobal
                              + " budget(MachinesPerCrew)=" + (RRWSettings.Current != null ? RRWSettings.Current.MachineBudget : 0)
                              + (preview ? " | PREVIEW (Rollers=" + RRWConst.kMaxRollersPerCrew + ")" : ""));
            var sb = new StringBuilder();
            foreach (var p in list)
            {
                sb.Clear();
                var v = p.View();
                if (preview) v.Rollers = RRWConst.kMaxRollersPerCrew;
                if (phase != WorksPhase.None)
                {
                    if (PhasePlan.KindOf(phase) != v.Kind) { DevSites.Out(ctx, "rollers p" + p.Id + " is a " + v.Kind + ": " + DevSites.Code(phase) + " does not apply"); continue; }
                    v.Phase = phase;
                    sweep = true;
                }
                sb.Append("rollers p").Append(p.Id).Append(' ').Append(DevSites.Code(v.Phase)).Append(" f=").Append(DevSites.F(v.F))
                  .Append(" crews=").Append(v.CrewCount).Append(" latched=").Append(p.Rollers).Append(" spawned=").Append(p.RollersSpawned)
                  .Append(" mode=").Append(DevSites.ModeCode(p.Mode)).Append(" trim=[").Append(DevSites.F1(v.Trim0)).Append(',').Append(DevSites.F1(v.Trim1)).Append("]\n");
                if (!sweep)
                {
                    if (R5Dev.RollerLines(v, sb, "rollers   ") == 0)
                        sb.Append("rollers   none planned").Append(v.Rollers <= 0 ? " (latched 0: setting off / Director not latching yet; try 'preview')" : R5Dev.PlansRollers(v.Phase) ? "" : " (not a roller phase)").Append('\n');
                }
                else
                {
                    for (float f = 0f; f <= 1f + 1e-4f; f += step)
                    {
                        var w = v;
                        w.F = math.min(1f, f);
                        int crews = w.CrewCount;
                        sb.Append("rollers   f=").Append(w.F.ToString("0.00", global::System.Globalization.CultureInfo.InvariantCulture));
                        for (int i = 0; i < crews; i++)
                        {
                            var plan = PhasePlan.Crew(w, i);
                            sb.Append(" | c").Append(i).Append(" F=").Append(DevSites.F1(plan.FrontU));
                            if (plan.CrewTruck.Active) sb.Append(" ct=").Append(DevSites.F1(plan.CrewTruck.U));
                            for (int r = 0; r < 2; r++)
                            {
                                var rs = plan.Roller(r);
                                if (!rs.Active) continue;
                                sb.Append(" R").Append(r).Append('=');
                                if (rs.Slot.Activity == MachineActivity.Compact) sb.Append('[').Append(DevSites.F1(rs.WinLo)).Append(',').Append(DevSites.F1(rs.WinHi)).Append(']');
                                else sb.Append(rs.Slot.Activity).Append('@').Append(DevSites.F1(rs.Slot.U));
                                if (rs.RelayOnly) sb.Append("(relay)");
                                if (rs.WorksHalfOnly) sb.Append("(wh)");
                            }
                        }
                        sb.Append('\n');
                    }
                }
                DevSites.Lines(ctx, sb);
            }
        }
    }

    // rrw.dig [site] [mode=dig|break] [swing=<s>] [ret=<s>] [events <t0> <t1>] [detailed=on|off] [dust=on|off]
    public sealed class RrwDigCommand : IDevCommand
    {
        public string Name => "rrw.dig";
        public string Help => "rrw.dig [site] [mode=dig|break] [swing=<s>] [ret=<s>] [events <t0> <t1>] [detailed=on|off] [dust=on|off] - IK dig schedule (Core " +
                              "DigSchedule, period " + RRWConst.kDigCyclePeriod + " s): keys, events, hop windows, truck load per dump and departure; with <site> the hop verdict " +
                              "of its crew front; detailed/dust = the 'Detailed digging' / 'Dust' settings for this session. Runtime IK: rrw.mx.dig";

        public void Run(DevContext ctx, string[] a)
        {
            var s = RRWSettings.Current;
            string det = DevSites.Opt(a, "detailed", null), dust = DevSites.Opt(a, "dust", null);
            if (s != null && det != null) s.DetailedDigging = On(det);
            if (s != null && dust != null) s.DustEffects = On(dust);
            DevSites.Out(ctx, "dig settings detailed=" + (s != null && s.DetailedDigging) + " -> DetailedDiggingOn=" + (s != null && s.DetailedDiggingOn)
                              + " (BonesOn=" + (s != null && s.BonesOn) + ") dust=" + (s != null && s.DustEffects) + " -> DigDustOn=" + (s != null && s.DigDustOn)
                              + " | puff prefab done=" + RRWPrefabRegistry.DustPuffDone + " ok=" + RRWPrefabRegistry.DustPuffOk
                              + (det != null || dust != null ? " (session only; each excavator switches at its next re-plan / cycle boundary)" : ""));

            var pos = DevSites.Positional(a);
            int ev = pos.FindIndex(x => x.Equals("events", global::System.StringComparison.OrdinalIgnoreCase));
            double t0 = 0, t1 = RRWConst.kDigCyclePeriod * 4;
            if (ev >= 0)
            {
                if (ev + 2 < pos.Count && DevSites.TryFloat(pos[ev + 1], out float e0) && DevSites.TryFloat(pos[ev + 2], out float e1)) { t0 = e0; t1 = e1; pos.RemoveRange(ev, 3); }
                else { pos.RemoveAt(ev); }
            }
            string m = DevSites.Opt(a, "mode", null);
            DigMode mode = m != null && m.ToLowerInvariant().StartsWith("b") ? DigMode.Break : DigMode.Dig;
            float swing = DevSites.OptF(a, "swing", float.NaN), ret = DevSites.OptF(a, "ret", float.NaN);

            var list = new List<ProjectRecord>();
            if (pos.Count > 0)
            {
                if (!DevSites.Resolve(ctx, pos[0], list, out _, out string err)) { DevSites.Out(ctx, "dig: " + err); return; }
            }
            if (list.Count == 0)
            {
                Schedule(ctx, DigSchedule.Make(mode, swing, ret), t0, t1, "dig " + mode);
                return;
            }
            foreach (var p in list)
            {
                var v = p.View();
                if (!R5Dev.DigModeOf(v.Phase, out DigMode pm))
                {
                    DevSites.Out(ctx, "dig p" + p.Id + " " + DevSites.Code(v.Phase) + ": no dig cycle in this phase (C1 / D0 / D1 only); grader bucket placement runs in C0 / C2 / D2");
                    continue;
                }
                var ds = DigSchedule.Make(m != null ? mode : pm, swing, ret);
                Schedule(ctx, ds, t0, t1, "dig p" + p.Id + " " + DevSites.Code(v.Phase) + " " + ds.Mode);
                DevSites.Out(ctx, "dig p" + p.Id + " hop " + R5Dev.HopVerdict(v, ds) + " crews=" + v.CrewCount);
            }
        }

        static bool On(string s) { s = s.ToLowerInvariant(); return s == "on" || s == "1" || s == "true"; }

        static void Schedule(DevContext ctx, DigSchedule ds, double t0, double t1, string label)
        {
            DevSites.Out(ctx, label + " " + R5Dev.ScheduleText(ds));
            var e = ds.EventsBetween(t0, t1, 0.0, out int dumps);
            double nth = ds.NthDumpAfter(t0, 0.0, RRWConst.kBucketsPerLoad);
            double depart = nth + (ds.TShake - ds.DumpT) + RRWConst.kTruckLeaveAfterShake;
            var sb = new StringBuilder();
            sb.Append(label).Append(" events in (").Append(t0.ToString("0.00", CultureInfo.InvariantCulture)).Append(", ").Append(t1.ToString("0.00", CultureInfo.InvariantCulture))
              .Append("] origin 0: ").Append(e).Append(" dumps=").Append(dumps)
              .Append(" | truck arriving empty at t0: ").Append(RRWConst.kBucketsPerLoad).Append("th dump at ").Append(nth.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" -> departs ").Append(depart.ToString("0.00", CultureInfo.InvariantCulture)).Append(" | load per dump:");
            for (int i = 0; i <= RRWConst.kBucketsPerLoad; i++) sb.Append(' ').Append(i).Append('=').Append(DigLoad.Amount(i)).Append(DigLoad.Full(i) ? "(full)" : "");
            DevSites.Out(ctx, sb.ToString());
            // key at a few cycle times (what rrw.mx.dig should show live)
            sb.Clear();
            sb.Append(label).Append(" keys");
            for (float t = 0f; t < ds.Period; t += 2.5f)
            {
                var k = ds.KeyAt(t, out float u);
                sb.Append(' ').Append(t.ToString("0.0", CultureInfo.InvariantCulture)).Append('=').Append(k).Append('(').Append(u.ToString("0.00", CultureInfo.InvariantCulture)).Append(')')
                  .Append(ds.RootMayMove(t) ? "" : "*");
            }
            sb.Append("  (* = root must stand still)");
            DevSites.Out(ctx, sb.ToString());
        }
    }
}
#endif
