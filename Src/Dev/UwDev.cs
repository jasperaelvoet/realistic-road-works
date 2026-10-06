#if DEVTOOLS
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;

// rrw.uw.* dev commands for upgrade works (mode H: partial upgrade works on a road whose type changed).
//   rrw.uw [site]          the runtime state: UpgradeRuntime, the view's windows and bands, every edge's bands, sub-strips and drops
//   rrw.uw.start           a mode H project on existing roads without the tool (SiteFactory.CreateUpgradeProjects), e.g. a
//                          one-sided widening band on a building-free road
//   rrw.uw.write           writes an upgrade tail onto a running mode D construction (save / load compatibility checks)
//   rrw.uw.dump            the saved bytes of a site, what version 3.0.0 reads from them and a read-back
namespace RealisticRoadWorks.V3.DevCmds
{
    internal static class UwDev
    {
        // "BL:-7.5:-3.5" = kind (B build, R rebuild, X remove, K re-marking) + side (L, R, M middle, B both) : lo : hi [: g<g0>],
        // CHAIN-frame laterals; several separated by ','.
        public static bool ParseBands(string spec, List<UpgradeBand> output, out string error)
        {
            output.Clear();
            error = null;
            foreach (var raw in spec.Split(','))
            {
                var t = raw.Trim();
                if (t.Length == 0) continue;
                var parts = t.Split(':');
                if (parts.Length < 3 || parts[0].Length != 2) { error = "bad band '" + t + "' (want e.g. BL:-7.5:-3.5)"; return false; }
                BandKind kind;
                switch (char.ToUpperInvariant(parts[0][0]))
                {
                    case 'B': kind = BandKind.Build; break;
                    case 'R': kind = BandKind.Rebuild; break;
                    case 'X': kind = BandKind.Remove; break;
                    case 'K': kind = BandKind.Remark; break;
                    default: error = "bad band kind in '" + t + "' (B R X K)"; return false;
                }
                BandSide side;
                switch (char.ToUpperInvariant(parts[0][1]))
                {
                    case 'L': side = BandSide.Left; break;
                    case 'R': side = BandSide.Right; break;
                    case 'M': side = BandSide.Middle; break;
                    case 'B': side = BandSide.Both; break;
                    default: error = "bad band side in '" + t + "' (L R M B)"; return false;
                }
                if (!DevSites.TryFloat(parts[1], out float lo) || !DevSites.TryFloat(parts[2], out float hi) || !(hi > lo))
                { error = "bad laterals in '" + t + "' (lo < hi)"; return false; }
                float g0 = 0f;
                if (parts.Length > 3 && parts[3].StartsWith("g") && !DevSites.TryFloat(parts[3].Substring(1), out g0)) { error = "bad g0 in '" + t + "'"; return false; }
                output.Add(UpgradeBand.Make(kind, side, lo, hi, 0, BandTraffic.Undecided, g0));
            }
            if (output.Count == 0) { error = "no bands"; return false; }
            if (output.Count > RRWConst.kUwMaxBands) { error = "more than " + RRWConst.kUwMaxBands + " bands"; return false; }
            return true;
        }

        public static bool ParseTraffic(string s, out BandTraffic t)
        {
            t = BandTraffic.Undecided;
            switch ((s ?? "").ToLowerInvariant())
            {
                case "none": t = BandTraffic.None; return true;
                case "sidewalk": t = BandTraffic.Sidewalk; return true;
                case "drop": t = BandTraffic.Drop; return true;
                case "half": t = BandTraffic.Half; return true;
                case "carriageway": t = BandTraffic.Carriageway; return true;
                case "dressing": t = BandTraffic.Dressing; return true;
                case "undecided": return true;
                default: return false;
            }
        }

        public static string Hex(List<byte> b, int from, int count)
        {
            var sb = new StringBuilder(count * 2);
            for (int i = from; i < from + count && i < b.Count; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        public static string Describe(in RoadWorksSite s)
        {
            var sb = new StringBuilder();
            sb.Append(s.Kind).Append(" mode=").Append(s.Mode).Append(" plan=").Append(s.Plan);
            if (s.Plan != PlanKind.None)
            {
                sb.Append(" class=").Append(s.UpClass).Append(" bands=").Append(s.m_BandCount).Append(" windows=").Append(s.m_WindowCount)
                  .Append(" setup=").Append(s.m_SetupP8).Append(" flags=").Append(s.UpFlags).Append(" ends=");
                for (int k = 0; k < s.m_WindowCount; k++) { if (k > 0) sb.Append(','); sb.Append(s.WinEnd8(k)); }
                for (int i = 0; i < s.m_BandCount; i++) sb.Append(" | ").Append(s.Band(i).ToString());
                sb.Append(s.TailValid() ? " valid" : " INVALID");
            }
            sb.Append(" p=").Append(s.Progress.ToString("0.0000", CultureInfo.InvariantCulture)).Append(" done=").Append(s.m_WorkDone)
              .Append('/').Append(s.m_WorkRequired).Append(" tailHash=").Append(s.TailHash().ToString("X8"));
            if (s.m_LoadNote != RoadWorksSite.kNoteNone) sb.Append(" loadNote=").Append(s.m_LoadNote);
            return sb.ToString();
        }

        // rrw.list flags of a mode 2 project: window / windows, the primitive applied now (and why the layout window fell to
        // Dressing); "vacating" while a window start keeps the previous window's layout applied.
        public static string ListFlags(ProjectRecord p)
        {
            var v = p.View();
            if (!v.IsUpgrade) return " modeH(no plan)";
            var u = v.Upgrade;
            string w = u.InSetup ? "setup" : u.InTeardown ? "teardown" : (u.Window + 1) + "/" + u.WindowCount;
            return " uw=" + w + " " + u.AppliedTraffic + (u.Reason != StageBlockReason.None ? "(" + u.Reason + ")" : "")
                   + (u.Vacating ? " vacating(w" + u.AppliedWindow + ")" : "") + " bands=" + u.BandCount
                   + (u.AllAtOnce ? " allAtOnce" : "");
        }

        // Every saved field equal (the runtime-only load note excluded); NaN equals NaN.
        public static bool SameSaved(in RoadWorksSite a, in RoadWorksSite b)
        {
            return a.m_Kind == b.m_Kind && a.m_Mode == b.m_Mode && a.m_Flags == b.m_Flags && a.m_WorkDone == b.m_WorkDone
                   && a.m_WorkRequired == b.m_WorkRequired && a.m_ProjectId == b.m_ProjectId && Same(a.m_ChainU0, b.m_ChainU0)
                   && Same(a.m_ChainU1, b.m_ChainU1) && Same(a.m_ChainLength, b.m_ChainLength) && a.m_Seed == b.m_Seed
                   && a.m_PaidCost == b.m_PaidCost && Same(a.m_NatAbove, b.m_NatAbove) && Same(a.m_NatBelow, b.m_NatBelow)
                   && a.m_RestoreT == b.m_RestoreT && a.m_RestoreY16 == b.m_RestoreY16 && a.m_CancelPhase == b.m_CancelPhase
                   && Same(a.m_CancelFront, b.m_CancelFront) && a.m_PlanKind == b.m_PlanKind && a.m_UpgradeClass == b.m_UpgradeClass
                   && a.m_BandCount == b.m_BandCount && a.m_WindowCount == b.m_WindowCount && a.m_SetupP8 == b.m_SetupP8
                   && a.m_UpFlags == b.m_UpFlags && a.m_Band0.Equals(b.m_Band0) && a.m_Band1.Equals(b.m_Band1)
                   && a.m_Band2.Equals(b.m_Band2) && a.m_Band3.Equals(b.m_Band3) && a.m_WinEnds == b.m_WinEnds;
        }

        private static bool Same(float x, float y) => math.asint(x) == math.asint(y) || (float.IsNaN(x) && float.IsNaN(y));
    }

    public sealed class UwWriteCommand : IDevCommand
    {
        public string Name => "rrw.uw.write";
        public string Help => "rrw.uw.write <site> [class=mixed|widen|narrow|remark] [bands=BL:-7.5:-3.5,KB:-3.5:3.5] [parallel=0|1] [rushed=0|1] "
                              + "[plan=1|2] [prim=<window>:<none|sidewalk|drop|half|carriageway|dressing>,...] [flags=allatonce,precover] [legacy=1] - "
                              + "turns a running mode D construction project into a mode H upgrade site (saved tail; progress unchanged). Bands in "
                              + "CHAIN-frame metres: kind B build / R rebuild / X remove / K re-marking + side L / R / M middle / B both [:g<g0>]; "
                              + "windows from the product scheduler. legacy=1: mode 2 WITHOUT a plan (what an older version writes back). "
                              + "The Director rebuilds the project from the new tail in its next update (mode H from then on; legacy=1 runs without a "
                              + "plan: no lane closure). For tool-less mode H projects on idle roads use rrw.uw.start";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 1) { DevSites.Out(ctx, "usage: " + Help); return; }
            var list = new List<ProjectRecord>();
            if (!DevSites.Resolve(ctx, pos[0], list, out _, out string err)) { DevSites.Out(ctx, "uw write: " + err); return; }
            var em = ctx.EntityManager;

            bool legacy = DevSites.Bool(DevSites.Opt(a, "legacy", null), false);
            var chainBands = new List<UpgradeBand>();
            if (!UwDev.ParseBands(DevSites.Opt(a, "bands", "BL:-7.5:-3.5,KB:-3.5:3.5"), chainBands, out string berr)) { DevSites.Out(ctx, "uw write: " + berr); return; }
            bool parallel = DevSites.Bool(DevSites.Opt(a, "parallel", null), false);
            bool rushed = DevSites.Bool(DevSites.Opt(a, "rushed", null), false);
            int planArg = DevSites.TryInt(DevSites.Opt(a, "plan", "1"), out int pa) ? pa : 1;
            var plan = planArg == 2 ? PlanKind.Reconstruction : PlanKind.Upgrade;
            var flags = UpgradeFlags.None;
            foreach (var f in (DevSites.Opt(a, "flags", "") ?? "").Split(','))
            {
                if (f.Equals("allatonce", System.StringComparison.OrdinalIgnoreCase)) flags |= UpgradeFlags.AllAtOnce;
                else if (f.Equals("precover", System.StringComparison.OrdinalIgnoreCase)) flags |= UpgradeFlags.PreCover;
            }

            // schedule from the product scheduler (chain frame bands are the same for every edge here)
            var cb = new List<ChainBand>();
            var map = new int[chainBands.Count];
            UpgradePlan.Union(chainBands, cb, map);
            float ratio = RRWSettings.Current != null ? WorkTime.DemolitionRatio(RRWSettings.Current) : 0.5f;
            var sch = UpgradePlan.Windows(cb, parallel, rushed, ratio);
            var prim = new BandTraffic[RRWConst.kUwMaxWindows];
            for (int k = 0; k < prim.Length; k++) prim[k] = BandTraffic.Undecided;
            foreach (var pr in (DevSites.Opt(a, "prim", "") ?? "").Split(','))
            {
                var kv = pr.Split(':');
                if (kv.Length != 2) continue;
                if (!DevSites.TryInt(kv[0], out int w) || w < 0 || w >= RRWConst.kUwMaxWindows || !UwDev.ParseTraffic(kv[1], out var t))
                { DevSites.Out(ctx, "uw write: bad prim '" + pr + "'"); return; }
                prim[w] = t;
            }
            var bandsArr = new UpgradeBand[chainBands.Count];
            for (int i = 0; i < chainBands.Count; i++) bandsArr[i] = chainBands[i];
            var cls = DevSites.Opt(a, "class", null) == null ? UpgradePlan.ClassOf(bandsArr, bandsArr.Length) : ParseClass(DevSites.Opt(a, "class", null));
            if (cls == UpgradeClass.None || cls > UpgradeClass.Mixed) { DevSites.Out(ctx, "uw write: bad class (mixed widen narrow remark)"); return; }
            DevSites.Out(ctx, "uw write chain bands " + UpgradePlan.Describe(cb) + " schedule " + sch + " class=" + cls + (legacy ? " LEGACY (mode 2, no plan)" : ""));

            foreach (var p in list)
            {
                if (p.Kind != WorksKind.Construction || p.Mode == VisualMode.FullDig || p.Releasing || p.Progress >= 1f
                    || (p.Flags & SiteFlags.CancelledBuild) != 0)
                {
                    DevSites.Out(ctx, "uw write p" + p.Id + " skipped: needs a running mode D construction (is " + p.Kind + " mode=" + p.Mode
                                      + " p=" + DevSites.F(p.Progress) + (p.Releasing ? " releasing" : "") + ")");
                    continue;
                }
                int n = 0;
                foreach (var e in p.Edges)
                {
                    if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                    var site = em.GetComponentData<RoadWorksSite>(e);
                    if (site.Mode == VisualMode.FullDig) continue;
                    site.Mode = VisualMode.HalfWidth;
                    if (legacy) site.ClearTail();
                    else
                    {
                        bool rev = RoadZoneMath.ChainReversed(site.m_ChainU0, site.m_ChainU1);
                        var eb = new UpgradeBand[RRWConst.kUwMaxBands];
                        for (int i = 0; i < chainBands.Count; i++)
                        {
                            int w = map[i] >= 0 ? cb[map[i]].Window : 0;
                            var b = UpgradePlan.ToChain(chainBands[i], rev);   // chain -> edge frame (the same flip)
                            b.SetWinPrim(w, prim[w]);
                            eb[i] = b;
                        }
                        site.SetTail(plan, cls, sch, flags, eb[0], eb[1], eb[2], eb[3], chainBands.Count);
                    }
                    em.SetComponentData(e, site);
                    n++;
                    DevSites.Out(ctx, "uw write p" + p.Id + " e" + e.Index + " " + UwDev.Describe(site));
                }
                DevSites.Out(ctx, "uw write p" + p.Id + " edges=" + n + " done: " + (legacy ? "mode 2 without a plan (no lane closure, written as finished)"
                                  : "mode H from the next Director update") + "; save now and compare 'rrw.uw.dump p" + p.Id + "' / 'rrw.save.scan' after the reload");
            }
        }

        private static UpgradeClass ParseClass(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "remark": return UpgradeClass.Remark;
                case "widen": return UpgradeClass.Widen;
                case "narrow": return UpgradeClass.Narrow;
                case "mixed": return UpgradeClass.Mixed;
                default: return UpgradeClass.None;
            }
        }
    }

    public sealed class UwDumpCommand : IDevCommand
    {
        public string Name => "rrw.uw.dump";
        public string Help => "rrw.uw.dump <site> - per edge: the decoded upgrade tail, the schedule at the current p (window, band g, equivalent "
                              + "phase), the exact saved bytes, what the 3.0.0 reader sees in them and a read-back round trip";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 1) { DevSites.Out(ctx, "usage: " + Help); return; }
            var list = new List<ProjectRecord>();
            if (!DevSites.Resolve(ctx, pos[0], list, out Entity only, out string err)) { DevSites.Out(ctx, "uw dump: " + err); return; }
            var em = ctx.EntityManager;
            var ic = CultureInfo.InvariantCulture;
            foreach (var p in list)
            {
                foreach (var e in p.Edges)
                {
                    if (only != Entity.Null && e != only) continue;
                    if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                    var s = em.GetComponentData<RoadWorksSite>(e);
                    string head = "uw dump p" + p.Id + " e" + e.Index + " ";
                    DevSites.Out(ctx, head + UwDev.Describe(s));

                    if (s.Plan != PlanKind.None && s.Schedule.Valid)
                    {
                        var sch = s.Schedule;
                        float prog = s.Progress;
                        UpgradePlan.At(sch, prog, out int w, out float gw);
                        var sb = new StringBuilder();
                        sb.Append(head).Append("schedule ").Append(sch).Append(" at p=").Append(prog.ToString("0.0000", ic)).Append(": window=")
                          .Append(w < 0 ? "setup" : w >= sch.N ? "teardown" : w.ToString(ic)).Append(" gw=").Append(gw.ToString("0.000", ic));
                        bool remarkAfter;
                        for (int i = 0; i < s.m_BandCount; i++)
                        {
                            var b = s.Band(i);
                            var st = UpgradePlan.StateOf(sch, b.Window, b.G0f, prog, out float g);
                            remarkAfter = false;
                            for (int j = 0; j < s.m_BandCount; j++)
                                if (s.Band(j).Kind == BandKind.Remark && s.Band(j).Window > b.Window) remarkAfter = true;
                            UpgradePlan.Equivalent(b.Kind, remarkAfter, g, out var ph, out float f);
                            sb.Append(" | ").Append(b.Kind).Append('/').Append(b.Side).Append(" w").Append(b.Window).Append(' ').Append(st)
                              .Append(" g=").Append(g.ToString("0.000", ic)).Append(' ').Append(ph).Append(" f=").Append(f.ToString("0.00", ic));
                        }
                        DevSites.Out(ctx, sb.ToString());
                    }

                    // the bytes a save writes, the 3.0.0 view of them, and a read-back with this build's reader
                    var wr = new UwBytesWriter();
                    s.Serialize(wr);
                    int total = wr.Bytes.Count;
                    DevSites.Out(ctx, head + "bytes=" + total + " header=" + UwDev.Hex(wr.Bytes, 0, 4) + " v1=" + UwDev.Hex(wr.Bytes, 4, RoadWorksSite.kPayloadV1)
                                      + " tail=" + UwDev.Hex(wr.Bytes, 4 + RoadWorksSite.kPayloadV1, total - 4 - RoadWorksSite.kPayloadV1));
                    var bytes = wr.Bytes.ToArray();
                    var r300 = new UwBytesReader(bytes);
                    var old = new SiteReader300();
                    try { old.Deserialize(r300); }
                    catch (System.Exception ex) { DevSites.Out(ctx, head + "3.0.0 view FAILED: " + ex.Message); continue; }
                    float p300 = old.m_WorkRequired == 0 ? 1f : math.saturate(old.m_WorkDone / (float)old.m_WorkRequired);
                    DevSites.Out(ctx, head + "3.0.0 view: consumed=" + r300.Position + "/" + total + " mode=" + old.m_Mode + " kind=" + old.m_Kind
                                      + " done=" + old.m_WorkDone + "/" + old.m_WorkRequired + " p=" + p300.ToString("0.0000", ic)
                                      + (old.Incompatible ? " INCOMPATIBLE" : p300 >= 1f ? " (finished: completes at once there)" : " (running)")
                                      + (r300.Position == total ? " ok" : " FAIL"));
                    var rb = new UwBytesReader(bytes);
                    var back = new RoadWorksSite();
                    try { back.Deserialize(rb); }
                    catch (System.Exception ex) { DevSites.Out(ctx, head + "read-back FAILED: " + ex.Message); continue; }
                    bool same = UwDev.SameSaved(s, back);
                    DevSites.Out(ctx, head + "read-back: consumed=" + rb.Position + "/" + total + " " + (same ? "identical ok" : "DIFFERENT: " + UwDev.Describe(back))
                                      + (back.m_LoadNote != RoadWorksSite.kNoteNone ? " loadNote=" + back.m_LoadNote : ""));
                }
            }
        }
    }

    public sealed class UwCommand : IDevCommand
    {
        public string Name => "rrw.uw";
        public string Help => "rrw.uw [site] [strips=1] - mode H runtime: UpgradeRuntime (schedule, bands, primitive per window, zones), the view (window, "
                              + "primitive, zones, machine-safe), its chain bands, and per edge the saved bands, chain index, sub-strips, drop report, "
                              + "parking off and driveway keep-outs (strips=1: every sub-strip). No site: every mode 2 project";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            var list = new List<ProjectRecord>();
            if (pos.Count > 0)
            {
                if (!DevSites.Resolve(ctx, pos[0], list, out _, out string err)) { DevSites.Out(ctx, "uw: " + err); return; }
            }
            else foreach (var p in DevSites.Sorted()) if (p.Mode == VisualMode.HalfWidth || p.Upgrade != null) list.Add(p);
            if (list.Count == 0) { DevSites.Out(ctx, "uw: no mode H project"); return; }
            bool strips = DevSites.Bool(DevSites.Opt(a, "strips", null), false);
            uint now = RRWClock.UpdateIndex;
            foreach (var p in list)
            {
                string head = "uw p" + p.Id + " #" + DevSites.ListIndex(p);
                var rt = p.Upgrade;
                if (rt == null)
                {
                    DevSites.Out(ctx, head + " mode " + DevSites.ModeCode(p.Mode) + " " + p.Kind + " without UpgradeRuntime"
                                      + (p.Mode == VisualMode.HalfWidth ? " (no plan: slow zone, no lane closed)" : ""));
                    continue;
                }
                DevSites.Out(ctx, head + " " + rt.Describe());
                var v = p.View();
                var u = v.Upgrade;
                string w = !u.Valid ? "none" : u.InSetup ? "setup" : u.InTeardown ? "teardown" : u.Window.ToString(CultureInfo.InvariantCulture);
                DevSites.Out(ctx, head + " view p=" + DevSites.F(p.Progress) + " " + p.Kind + " phase=" + DevSites.Code(p.Phase) + " f=" + DevSites.F(p.PhaseFraction)
                                  + " window=" + w + "/" + u.WindowCount + " layout=" + u.LayoutWindow + " gw=" + DevSites.F(u.Gw) + " traffic=" + u.Traffic
                                  + " applied=w" + u.AppliedWindow + " " + u.AppliedTraffic + " " + RoadZoneMath.Describe(u.AppliedZones)
                                  + (u.Vacating ? " VACATING" : "")
                                  + (u.Reason != StageBlockReason.None ? "(" + u.Reason + ")" : "") + " closure=" + p.Closure
                                  + " zones=" + RoadZoneMath.Describe(u.Zones) + " next=" + RoadZoneMath.Describe(u.NextZones)
                                  + " preClosed=" + RoadZoneMath.Describe(u.PreClosed) + " safe=" + RoadZoneMath.Describe(u.MachineSafe)
                                  + " ready=" + RoadZoneMath.Describe(p.WorkZonesReady) + " open=" + RoadZoneMath.Describe(p.OpenLanes)
                                  + " soft=" + RoadZoneMath.Describe(p.SoftZones) + " switch=" + p.Switch + " crews=" + p.Crews
                                  + " machines=" + RoadZoneMath.Describe(p.MachineZones) + (p.MachinesReportFresh(now) ? "" : "(stale)")
                                  + (u.AllAtOnce ? " allAtOnce" : "") + (u.PreCover ? " preCover" : "") + (rt.DerivedFresh ? "" : " DERIVED-STALE"));
                for (int i = 0; i < u.BandCount; i++)
                {
                    int crew = u.CrewOfBand(i);
                    DevSites.Out(ctx, head + " band " + i + " " + u.Band(i).ToString() + " " + u.StateOf(i) + " g=" + DevSites.F(u.BandG(i))
                                      + " prim=" + u.Prim(u.Band(i).Window) + (crew >= 0 ? " crew " + crew : ""));
                }
                foreach (var e in p.Edges)
                {
                    if (!SiteRegistry.TryGetEdge(e, out var er)) continue;
                    var es = er.Upgrade;
                    string eh = head + " e" + e.Index;
                    if (es == null) { DevSites.Out(ctx, eh + " without UpgradeEdgeState"); continue; }
                    var sb = new StringBuilder();
                    sb.Append(eh).Append(' ').Append(es.Plan).Append(' ').Append(es.Class).Append(es.ChainReversed ? " reversed" : "")
                      .Append(" geo=").Append(er.GeometryRevision).Append(" tail=").Append(es.TailRevision).Append(" bands:");
                    for (int i = 0; i < es.BandCount; i++) sb.Append(' ').Append(es.Bands[i].ToString()).Append("->").Append(es.ChainIndex[i]);
                    sb.Append(es.ChainIndexCurrent(rt) ? "" : " CHAIN-INDEX-STALE");
                    sb.Append(" strips=");
                    for (int i = 0; i < es.BandCount; i++) { if (i > 0) sb.Append(','); sb.Append(es.SubStrips[i].Count); }
                    sb.Append(es.SubStripsFromLayout ? "(layout" : "(section").Append(es.SubStripsRevision == er.GeometryRevision && es.SubStripsTail == es.TailRevision ? ")" : " STALE)");
                    sb.Append(" drop lanes=").Append(es.DropLanes.Count).Append(" w=").Append(es.DropWindow)
                      .Append(" for=").Append(es.DropWindowFor(er.GeometryRevision, u))
                      .Append(" current=").Append(es.DropWindowFor(er.GeometryRevision, u) >= 0 ? 1 : 0)
                      .Append(" applied=").Append(es.DropApplied ? 1 : 0).Append(" registered=").Append(es.BlockersRegistered ? 1 : 0)
                      .Append(" intrusion=").Append(es.DropIntrusion ? 1 : 0).Append(" clean=").Append(es.DropCleanChecks)
                      .Append(" ready=").Append(es.DropReadyFor(er.GeometryRevision, u) ? 1 : 0)
                      .Append(" centres=").Append(es.DropCentresWritten ? "ok" : es.DropLaneCentres.Count + "/" + es.DropLanes.Count)
                      .Append(es.DropSafeRange(out float su0, out float su1) ? " safeU=[" + DevSites.F(su0) + "," + DevSites.F(su1) + "]" : " safeU-")
                      .Append(" parkingOff=").Append(es.ParkingOffLanes.Count).Append(" keepOuts=").Append(es.DrivewayKeepOut.Count)
                      .Append(" buildings=").Append(er.BuildingCount).Append(er.CutEdge ? " cutEdge" : "");
                    DevSites.Out(ctx, sb.ToString());
                    if (!strips) continue;
                    for (int i = 0; i < es.BandCount; i++)
                        foreach (var st in es.SubStrips[i])
                            DevSites.Out(ctx, eh + " band " + i + " strip " + st.Kind + " [" + DevSites.F(st.Lo) + "," + DevSites.F(st.Hi) + "] dir=" + st.Dir
                                              + " closed=" + (UpgradeZones.SubStripClosed(er, u, st, i) ? 1 : 0)
                                              + " parkingOff=" + (UpgradeZones.SubStripParkingOff(er, st) ? 1 : 0)
                                              + " machineSafe=" + (PhasePlan.MachineSafe(UpgradeZones.SubStripStateOf(er, u, st, i, false), false) ? 1 : 0));
                }
            }
        }
    }

    public sealed class UwStartCommand : IDevCommand
    {
        public string Name => "rrw.uw.start";
        public string Help => "rrw.uw.start <roads> <remark|widen|narrow|mixed> [lo:hi:kind:side[:out] ...] [frame=chain|edge] [rushed=1] [dry=1] - "
                              + "starts mode H upgrade works on existing roads without the tool (SiteFactory.CreateUpgradeProjects; the road type "
                              + "does not change). roads: indices of the dev 'list' command (3 | 0-4 | 0,2,5); each chain of them is one project. "
                              + "Bands in metres, CHAIN frame by default (+ = right of the chain direction; frame=edge: each edge's own curve): kind "
                              + "build|rebuild|remove|remark (b r x k), side left|right|middle|both (l r m b), out = partly outside the road. Without "
                              + "bands: remark = the carriageway; widen = the chain-right outer 3 m of the carriageway + remark; narrow = 3 m beyond the "
                              + "chain-right road edge + remark; mixed = widen + 3 m beyond the chain-left road edge. dry=1: print the projects only";

        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 2) { DevSites.Out(ctx, "usage: " + Help); return; }
            var settings = RRWSettings.Current;
            var gm = Game.SceneFlow.GameManager.instance;
            if (settings == null || gm == null || gm.gameMode != Game.GameMode.Game || !SiteRegistry.Loaded)
            { DevSites.Out(ctx, "uw start: needs a loaded city in game mode"); return; }
            var cls = ParseClass(pos[1]);
            if (cls == UpgradeClass.None) { DevSites.Out(ctx, "uw start: bad class '" + pos[1] + "' (remark widen narrow mixed)"); return; }
            bool edgeFrame = string.Equals(DevSites.Opt(a, "frame", "chain"), "edge", System.StringComparison.OrdinalIgnoreCase);
            bool rushed = DevSites.Bool(DevSites.Opt(a, "rushed", null), false);
            bool dry = DevSites.Bool(DevSites.Opt(a, "dry", null), false);
            var given = new List<UpgradeBand>();
            for (int i = 2; i < pos.Count; i++)
            {
                if (!ParseBand(pos[i], out var b, out string berr)) { DevSites.Out(ctx, "uw start: " + berr); return; }
                given.Add(b);
            }
            if (given.Count > RRWConst.kUwMaxBands) { DevSites.Out(ctx, "uw start: more than " + RRWConst.kUwMaxBands + " bands"); return; }

            // roads -> edges (idle road edges only)
            var em = ctx.EntityManager;
            var idx = new List<int>();
            if (!DevSites.RoadIndices(pos[0], idx)) { DevSites.Out(ctx, "uw start: bad road list '" + pos[0] + "'"); return; }
            var roads = ctx.Roads();
            var input = new List<ChainEdgeIn>();
            var seen = new HashSet<Entity>();
            int skipped = 0;
            foreach (int i in idx)
            {
                if (i < 0 || i >= roads.Count) { skipped++; continue; }
                Entity e = roads[i];
                if (!seen.Add(e) || em.HasComponent<RoadWorksSite>(e) || !em.HasComponent<Game.Net.Edge>(e) || !em.HasComponent<Game.Net.Curve>(e))
                { skipped++; continue; }
                var ed = em.GetComponentData<Game.Net.Edge>(e);
                input.Add(new ChainEdgeIn { Edge = e, StartNode = ed.m_Start, EndNode = ed.m_End, Length = math.max(0.01f, em.GetComponentData<Game.Net.Curve>(e).m_Length) });
            }
            if (input.Count == 0) { DevSites.Out(ctx, "uw start: no idle road among '" + pos[0] + "' (skipped " + skipped + ")"); return; }

            // chain direction of every edge: the factory builds the same chains from the same list
            var reversed = new Dictionary<Entity, bool>();
            foreach (var ch in ChainBuilder.Build(input))
                foreach (var ce in ch.Edges) reversed[ce.Edge] = ce.U1 < ce.U0;

            var edges = new List<SiteFactoryEdge>(input.Count);
            var specs = new List<UpgradeSpec>(input.Count);
            var cross = new List<SubStrip>(16);
            var edgeBands = new UpgradeBand[RRWConst.kUwMaxBands];
            var chainDefaults = new List<UpgradeBand>();
            var lines = new List<string>();
            for (int k = 0; k < input.Count; k++)
            {
                Entity e = input[k].Edge;
                bool rev = reversed.TryGetValue(e, out bool r) && r;
                var arc = new EdgeArc(em.GetComponentData<Game.Net.Curve>(e).m_Bezier);
                var sec = EcsUtil.MeasureSection(em, e, arc);
                sec.CrossSection(RRWCity.LeftHandTraffic, cross);
                if (given.Count == 0 && chainDefaults.Count == 0) DefaultBands(cls, sec, rev, chainDefaults);
                var src = given.Count > 0 ? given : chainDefaults;
                int n = src.Count;
                var spec = new UpgradeSpec { Class = cls, BandCount = n };
                for (int i = 0; i < n; i++)
                {
                    // chain frame -> this edge's frame (the same mirror); frame=edge bands are taken as they are
                    edgeBands[i] = edgeFrame && given.Count > 0 ? src[i] : UpgradePlan.ToChain(src[i], rev);
                    spec.SetBand(i, edgeBands[i]);
                }
                int buildings = em.HasBuffer<Game.Buildings.ConnectedBuilding>(e) ? em.GetBuffer<Game.Buildings.ConnectedBuilding>(e, true).Length : 0;
                bool keeps = UpgradeLanes.EveryDirectionKeepsLane(cross, edgeBands, n, -1);
                edges.Add(new SiteFactoryEdge
                {
                    Edge = e,
                    StartNode = input[k].StartNode,
                    EndNode = input[k].EndNode,
                    Length = input[k].Length,
                    DigEligible = EcsUtil.DigEligible(em, e, out _),
                    PaidCost = 0,
                    Dependants = buildings > 0,
                    Buildings = buildings > 0,
                    KeepsLanes = keeps,
                });
                specs.Add(spec);
                lines.Add("e" + e.Index + (rev ? " reversed" : "") + " buildings=" + buildings + " keepsLanes=" + keeps + " half=" + DevSites.F(sec.HalfWidth)
                          + " carriage=[" + DevSites.F(sec.CarriageLo) + "," + DevSites.F(sec.CarriageHi) + "] spec " + spec.ToString());
            }
            var chainList = given.Count > 0 && !edgeFrame ? given : chainDefaults;
            if (chainList.Count > 0)
            {
                var arr = chainList.ToArray();
                var byBands = UpgradePlan.ClassOf(arr, arr.Length);
                if (byBands != cls) DevSites.Out(ctx, "uw start: note: the bands read as " + byBands + " (the sites keep the class " + cls + ")");
            }
            foreach (var l in lines) DevSites.Out(ctx, "uw start " + l);

            var rc = EcsUtil.RoadClass(em, edges[0].Edge);
            var extra = rushed ? SiteFlags.Rushed : SiteFlags.None;
            var results = new List<SiteFactoryResult>();
            if (dry)
            {
                var log = new List<string>();
                SiteFactory.CreateUpgradeProjects(edges, specs, rc, UpgradeRates.From(settings), extra, results, log, dryRun: true);
                foreach (var res in results)
                    DevSites.Out(ctx, "uw start dry e" + res.Edge.Index + " hours=" + DevSites.F(WorkTime.HoursFromFrames(res.Site.m_WorkRequired)) + " " + UwDev.Describe(res.Site));
                DevSites.Out(ctx, "uw start dry: " + results.Count + " sites (nothing created; gates uwdrop=" + RRWGates.UpgradeDrop + " uwprecover=" + RRWGates.UpgradePreCover + ")");
                return;
            }
            em.CompleteAllTrackedJobs();
            SiteFactory.CreateUpgradeProjects(edges, specs, rc, settings, extra, results);
            var projects = new HashSet<uint>();
            int added = 0;
            foreach (var res in results)
            {
                if (!em.Exists(res.Edge) || em.HasComponent<RoadWorksSite>(res.Edge)) { DevSites.Out(ctx, "uw start e" + res.Edge.Index + " skipped (site appeared)"); continue; }
                em.AddComponentData(res.Edge, res.Site);
                projects.Add(res.Site.m_ProjectId);
                added++;
                DevSites.Out(ctx, "uw start p" + res.Site.m_ProjectId + " e" + res.Edge.Index + " " + UwDev.Describe(res.Site)
                                  + (res.CorridorPrev != 0 || res.CorridorNext != 0 ? " corridor " + res.CorridorPrev + "/" + res.CorridorNext : ""));
            }
            DevSites.Out(ctx, "uw start projects=" + projects.Count + " sites=" + added + (skipped > 0 ? " skipped roads=" + skipped : "")
                              + " (the Director adopts them in its next update; 'rrw.uw' shows the runtime)");
        }

        private static UpgradeClass ParseClass(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "remark": return UpgradeClass.Remark;
                case "widen": return UpgradeClass.Widen;
                case "narrow": return UpgradeClass.Narrow;
                case "mixed": return UpgradeClass.Mixed;
                default: return UpgradeClass.None;
            }
        }

        // "lo:hi:kind:side[:out]"
        private static bool ParseBand(string t, out UpgradeBand b, out string error)
        {
            b = default;
            error = null;
            var parts = t.Split(':');
            if (parts.Length < 4 || parts.Length > 5) { error = "bad band '" + t + "' (want lo:hi:kind:side[:out], e.g. 3:6:build:right)"; return false; }
            if (!DevSites.TryFloat(parts[0], out float lo) || !DevSites.TryFloat(parts[1], out float hi) || !(hi > lo))
            { error = "bad laterals in '" + t + "' (lo < hi, metres)"; return false; }
            BandKind kind;
            switch (parts[2].ToLowerInvariant())
            {
                case "b": case "build": kind = BandKind.Build; break;
                case "r": case "rebuild": kind = BandKind.Rebuild; break;
                case "x": case "remove": kind = BandKind.Remove; break;
                case "k": case "remark": kind = BandKind.Remark; break;
                default: error = "bad band kind in '" + t + "' (build rebuild remove remark)"; return false;
            }
            BandSide side;
            switch (parts[3].ToLowerInvariant())
            {
                case "l": case "left": side = BandSide.Left; break;
                case "r": case "right": side = BandSide.Right; break;
                case "m": case "middle": side = BandSide.Middle; break;
                case "b": case "both": side = BandSide.Both; break;
                default: error = "bad band side in '" + t + "' (left right middle both)"; return false;
            }
            bool outside = parts.Length == 5 && parts[4].Equals("out", System.StringComparison.OrdinalIgnoreCase);
            if (parts.Length == 5 && !outside) { error = "bad band flag in '" + t + "' (only 'out')"; return false; }
            b = UpgradeBand.Make(kind, side, lo, hi, 0, BandTraffic.Undecided, 0f, outside);
            return true;
        }

        // Default chain-frame bands of a class, from the first edge's section (strips of 3 m).
        private static void DefaultBands(UpgradeClass cls, in EdgeSection sec, bool rev, List<UpgradeBand> output)
        {
            output.Clear();
            RoadZoneMath.CarriageChain(sec, rev, out float lo, out float hi);
            float hw = sec.HalfWidth;
            const float strip = 3f;
            if (cls == UpgradeClass.Widen || cls == UpgradeClass.Mixed)
                output.Add(UpgradeBand.Make(BandKind.Build, BandSide.Right, math.max(lo, hi - strip), hi));
            if (cls == UpgradeClass.Narrow)
                output.Add(UpgradeBand.Make(BandKind.Remove, BandSide.Right, hw, hw + strip, 0, BandTraffic.Undecided, 0f, true));
            if (cls == UpgradeClass.Mixed)
                output.Add(UpgradeBand.Make(BandKind.Remove, BandSide.Left, -hw - strip, -hw, 0, BandTraffic.Undecided, 0f, true));
            output.Add(UpgradeBand.Make(BandKind.Remark, BandSide.Both, lo, hi));
        }
    }
}
#endif
