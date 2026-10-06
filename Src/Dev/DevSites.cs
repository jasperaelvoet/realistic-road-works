#if DEVTOOLS
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.DevCmds
{
    // Site addressing and argument helpers for the rrw.* dev commands. Depends only on the core and the dev framework.
    //   #n   index in rrw.list (projects sorted by id); a bare number means the same
    //   p<id> project id
    //   e<i>  edge entity index (its project; rrw.dump shows only that edge)
    //   r<n>  road index from the dev framework's "list" command -> its project
    //   all   every project
    internal static class DevSites
    {
        public static void Out(DevContext ctx, string msg) => ctx.Log("rrw " + msg);

        public static List<ProjectRecord> Sorted()
        {
            var list = new List<ProjectRecord>(SiteRegistry.Projects.Values);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        public static int ListIndex(ProjectRecord p)
        {
            var list = Sorted();
            for (int i = 0; i < list.Count; i++) if (list[i] == p) return i;
            return -1;
        }

        // Resolves <site> to projects; `edge` is set for e<i> / r<n> addresses. False + error text when nothing matches.
        public static bool Resolve(DevContext ctx, string arg, List<ProjectRecord> output, out Entity edge, out string error)
        {
            output.Clear();
            edge = Entity.Null;
            error = null;
            if (string.IsNullOrEmpty(arg)) { error = "missing <site> (#n | p<id> | e<index> | r<road> | all)"; return false; }
            string a = arg.Trim().ToLowerInvariant();
            if (a == "all")
            {
                output.AddRange(Sorted());
                if (output.Count == 0) { error = "no road works"; return false; }
                return true;
            }
            if (a[0] == '#' || char.IsDigit(a[0]))
            {
                if (!TryInt(a[0] == '#' ? a.Substring(1) : a, out int n)) { error = "bad index '" + arg + "'"; return false; }
                var list = Sorted();
                if (n < 0 || n >= list.Count) { error = "no project #" + n + " (" + list.Count + " projects)"; return false; }
                output.Add(list[n]);
                return true;
            }
            if (a[0] == 'p')
            {
                if (!TryInt(a.Substring(1), out int id) || !SiteRegistry.TryGetProject((uint)id, out var p)) { error = "no project " + arg; return false; }
                output.Add(p);
                return true;
            }
            if (a[0] == 'e')
            {
                if (!TryInt(a.Substring(1), out int idx)) { error = "bad edge '" + arg + "'"; return false; }
                foreach (var kv in SiteRegistry.Edges)
                {
                    if (kv.Key.Index != idx) continue;
                    edge = kv.Key;
                    if (SiteRegistry.TryGetProject(kv.Value.ProjectId, out var p)) output.Add(p);
                    return output.Count > 0 || Fail(out error, "edge " + arg + " has no project");
                }
                error = "edge " + arg + " is not a works edge";
                return false;
            }
            if (a[0] == 'r')
            {
                Entity road;
                try { road = ctx.Road(a.Substring(1)); }
                catch (global::System.Exception e) { error = "bad road '" + arg + "': " + e.Message; return false; }
                if (!SiteRegistry.TryGetEdge(road, out var r)) { error = "road " + arg + " (" + RRWLog.E(road) + ") is not under works"; return false; }
                edge = road;
                if (SiteRegistry.TryGetProject(r.ProjectId, out var p)) output.Add(p);
                return output.Count > 0 || Fail(out error, "road " + arg + " has no project");
            }
            error = "unknown site '" + arg + "' (#n | p<id> | e<index> | r<road> | all)";
            return false;
        }

        private static bool Fail(out string error, string msg) { error = msg; return false; }

        public static bool TryInt(string s, out int v) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

        public static bool TryFloat(string s, out float v)
        {
            v = 0f;
            if (string.IsNullOrEmpty(s)) return false;
            bool pct = s.EndsWith("%");
            if (pct) s = s.Substring(0, s.Length - 1);
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
            if (pct) v /= 100f;
            return true;
        }

        // key=value options (case-insensitive key); positional = arguments without '='.
        public static string Opt(string[] a, string key, string def)
        {
            string k = key + "=";
            for (int i = 0; i < a.Length; i++)
                if (a[i].StartsWith(k, global::System.StringComparison.OrdinalIgnoreCase)) return a[i].Substring(k.Length);
            return def;
        }

        public static float OptF(string[] a, string key, float def) => TryFloat(Opt(a, key, null), out float v) ? v : def;

        public static List<string> Positional(string[] a)
        {
            var l = new List<string>();
            for (int i = 0; i < a.Length; i++) if (a[i].IndexOf('=') < 0) l.Add(a[i]);
            return l;
        }

        public static bool Bool(string s, bool def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            switch (s.ToLowerInvariant())
            {
                case "1": case "on": case "true": case "yes": return true;
                case "0": case "off": case "false": case "no": return false;
                default: return def;
            }
        }

        // C0..C4 / D0..D2 / phase names.
        public static bool TryPhase(string s, out WorksPhase ph)
        {
            ph = WorksPhase.None;
            if (string.IsNullOrEmpty(s)) return false;
            switch (s.ToUpperInvariant())
            {
                case "C0": case "SURVEY": ph = WorksPhase.Survey; return true;
                case "C1": case "EXCAVATION": ph = WorksPhase.Excavation; return true;
                case "C2": case "FOUNDATION": ph = WorksPhase.Foundation; return true;
                case "C3": case "PAVING": ph = WorksPhase.Paving; return true;
                case "C4": case "FINISHING": ph = WorksPhase.Finishing; return true;
                case "D0": case "BREAKUP": ph = WorksPhase.BreakUp; return true;
                case "D1": case "REMOVAL": ph = WorksPhase.Removal; return true;
                case "D2": case "RESTORE": ph = WorksPhase.Restore; return true;
                default: return false;
            }
        }

        public static string Code(WorksPhase ph)
        {
            switch (ph)
            {
                case WorksPhase.Survey: return "C0";
                case WorksPhase.Excavation: return "C1";
                case WorksPhase.Foundation: return "C2";
                case WorksPhase.Paving: return "C3";
                case WorksPhase.Finishing: return "C4";
                case WorksPhase.BreakUp: return "D0";
                case WorksPhase.Removal: return "D1";
                case WorksPhase.Restore: return "D2";
                case WorksPhase.Complete: return "done";
                default: return "-";
            }
        }

        public static Entity FirstEdge(ProjectRecord p) => p != null && p.Edges.Count > 0 ? p.Edges[0] : Entity.Null;

        // Calendar hours until the project is done (shift windows walked; IgnoreShift = around the clock).
        public static float EtaHours(EntityManager em, ProjectRecord p)
        {
            Entity e = FirstEdge(p);
            if (e == Entity.Null || !em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) return float.NaN;
            var s = em.GetComponentData<RoadWorksSite>(e);
            if (s.m_WorkDone >= s.m_WorkRequired) return 0f;
            var set = RRWSettings.Current;
            bool rushed = s.Has(SiteFlags.Rushed);
            ShiftWindow w = RRWDebug.IgnoreShift ? ShiftWindow.AllDay : WorkTime.Effective(set != null ? set.Shift : ShiftWindow.Day, rushed);
            double frames = WorkTime.CalendarFramesToFinish(RRWClock.NormalizedTime, s.m_WorkRequired - s.m_WorkDone, w,
                                                            WorkTime.Rate(rushed, RRWDebug.WorkTimeScale));
            return double.IsInfinity(frames) ? float.PositiveInfinity : (float)(frames / RRWConst.kFramesPerHour);
        }

        public static string Hours(float h) => float.IsNaN(h) ? "?" : float.IsInfinity(h) ? "inf" : h.ToString("0.0", CultureInfo.InvariantCulture) + "h";

        public static string F(float v) => RRWLog.F(v);
        public static string F1(float v) => float.IsNaN(v) ? "nan" : v.ToString("0.0", CultureInfo.InvariantCulture);
        public static string F3(float3 v) => "(" + F1(v.x) + "," + F1(v.y) + "," + F1(v.z) + ")";

        public static string ModeCode(VisualMode m) => m == VisualMode.FullDig ? "A" : "D";

        public static void Lines(DevContext ctx, StringBuilder sb)
        {
            foreach (var line in sb.ToString().Split('\n'))
                if (line.Length > 0) Out(ctx, line);
        }
    }
}
#endif
