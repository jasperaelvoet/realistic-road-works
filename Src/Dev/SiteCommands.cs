#if DEVTOOLS
using System.Collections.Generic;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Routes;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Hidden = Game.Tools.Hidden;

// rrw.* dev commands on Core only. Output lines start with "dev rrw". Every state change goes through
// WorksRequests (drained by the Director at Mod1) or the RRWDebug switches; commands never make structural changes.
namespace RealisticRoadWorks.V3.DevCmds
{
    internal static class Req
    {
        public static void Enqueue(ProjectRecord p, WorksRequestType type, float value = 0f, WorksPhase phase = WorksPhase.None)
        {
            WorksRequests.Enqueue(new WorksRequest { Type = type, Edge = DevSites.FirstEdge(p), ProjectId = p.Id, Value = value, Phase = phase });
        }

        // Shared body of the simple per-project request commands.
        public static void Run(DevContext ctx, string[] a, string usage, global::System.Func<ProjectRecord, WorksRequestType?> pick, string label)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 1) { DevSites.Out(ctx, "usage: " + usage); return; }
            var list = new List<ProjectRecord>();
            if (!DevSites.Resolve(ctx, pos[0], list, out _, out string err)) { DevSites.Out(ctx, label + ": " + err); return; }
            foreach (var p in list)
            {
                var t = pick(p);
                if (t == null) { DevSites.Out(ctx, label + " p" + p.Id + " skipped"); continue; }
                Enqueue(p, t.Value);
                DevSites.Out(ctx, "request " + t.Value + " p" + p.Id);
            }
        }
    }

    public sealed class RrwListCommand : IDevCommand
    {
        public string Name => "rrw.list";
        public string Help => "rrw.list - every road works project (#n index used by the other rrw.* commands); crews=n focus=i spawned=k " +
                              "and one 'crew i' line per section when n > 1; rollers=<latched> spawned=<live units> (+ chain=DeadEnd when the chain blocks a car half)";
        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            var list = DevSites.Sorted();
            DevSites.Out(ctx, "list projects=" + list.Count + " edges=" + SiteRegistry.Edges.Count + " loaded=" + SiteRegistry.Loaded
                              + " nextId=" + SiteRegistry.NextProjectId + " timescale=" + DevSites.F(RRWDebug.WorkTimeScale)
                              + " machines=" + DevSites.F(RRWDebug.MachineClockScale) + " shift=" + (RRWDebug.IgnoreShift ? "ignore" : "obey")
                              + " layers=" + LayerCommand.Describe());
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                string flags = "";
                if (p.Releasing) flags += " releasing";
                if (p.OpenLanes != RoadZones.None) flags += " open=" + R2Dev.Zones(p.OpenLanes);
                flags += R3Dev.ListFlags(p);
                if ((p.Flags & SiteFlags.Rushed) != 0) flags += " rushed";
                if ((p.Flags & SiteFlags.Replaced) != 0) flags += " replaced";
                if ((p.Flags & SiteFlags.CancelledBuild) != 0) flags += " cancelled(" + DevSites.Code(p.CancelPhase) + "@" + DevSites.F1(p.CancelFront) + ")";
                if ((p.Flags & SiteFlags.Migrated) != 0) flags += " migrated";
                if (p.ClearingTraffic) flags += " clearing-traffic";
                DevSites.Out(ctx, "#" + i + " p" + p.Id + " " + p.Kind + " mode=" + DevSites.ModeCode(p.Mode) + " phase=" + p.Phase
                                  + " f=" + p.PhaseFraction.ToString("0.00", global::System.Globalization.CultureInfo.InvariantCulture)
                                  + " p=" + p.Progress.ToString("0.000", global::System.Globalization.CultureInfo.InvariantCulture)
                                  + " U=" + DevSites.F1(p.ChainLength) + " trim=[" + DevSites.F1(p.TrimU0) + "," + DevSites.F1(p.TrimU1 > 0f ? p.TrimU1 : p.ChainLength) + "]"
                                  + " edges=" + p.Edges.Count + " front=" + DevSites.F3(p.FrontPosition) + " u=" + DevSites.F1(p.FrontU)
                                  + " work=" + (p.Working ? "on" : "off") + " closure=" + p.Closure + " machines=" + p.MachineCount
                                  + (p.AllowMachines ? "" : "(off)") + " cam=" + DevSites.F1(p.CameraDistance) + "m"
                                  + " eta=" + DevSites.Hours(DevSites.EtaHours(em, p)) + flags + R4Dev.ListFlags(p) + R5Dev.ListFlags(p));
                if (p.View().CrewCount > 1)
                {
                    var sb = new StringBuilder();
                    R4Dev.CrewLines(p, sb, "   ");
                    DevSites.Lines(ctx, sb);
                }
            }
        }
    }

    public sealed class RrwDumpCommand : IDevCommand
    {
        public string Name => "rrw.dump";
        public string Help => "rrw.dump <site> - full state: saved site, runtime, ground output, registry records, module dumpers";
        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 1) { DevSites.Out(ctx, "usage: " + Help); return; }
            var list = new List<ProjectRecord>();
            if (!DevSites.Resolve(ctx, pos[0], list, out Entity only, out string err)) { DevSites.Out(ctx, "dump: " + err); return; }
            var em = ctx.EntityManager;
            var sb = new StringBuilder();
            foreach (var p in list)
            {
                sb.Clear();
                DumpProject(em, p, sb);
                for (int i = 0; i < p.Edges.Count; i++)
                {
                    if (only != Entity.Null && p.Edges[i] != only) continue;
                    DumpEdge(em, p.Edges[i], i, sb);
                }
                DevSites.Lines(ctx, sb);
            }
        }

        private static void DumpProject(EntityManager em, ProjectRecord p, StringBuilder sb)
        {
            sb.Append("dump p").Append(p.Id).Append(" #").Append(DevSites.ListIndex(p)).Append(' ').Append(p.Kind).Append(" mode=").Append(DevSites.ModeCode(p.Mode))
              .Append(" phase=").Append(p.Phase).Append(" f=").Append(DevSites.F(p.PhaseFraction)).Append(" p=").Append(DevSites.F(p.Progress))
              .Append(" U=").Append(DevSites.F(p.ChainLength)).Append(" edges=").Append(p.Edges.Count).Append(" rev=").Append(p.Revision).Append('\n');
            sb.Append("  flags=").Append(p.Flags).Append(" working=").Append(p.Working).Append(" closure=").Append(p.Closure)
              .Append(" clearingTraffic=").Append(p.ClearingTraffic).Append(" cancel=").Append(DevSites.Code(p.CancelPhase)).Append('@').Append(DevSites.F(p.CancelFront)).Append('\n');
            sb.Append("  trim=[").Append(DevSites.F(p.TrimU0)).Append(',').Append(DevSites.F(p.TrimU1)).Append("] startJunction=").Append(p.StartIsJunction)
              .Append(" endJunction=").Append(p.EndIsJunction).Append(" startShared=").Append(p.StartShared).Append(" endShared=").Append(p.EndShared).Append('\n');
            sb.Append("  front u=").Append(DevSites.F(p.FrontU)).Append(" pos=").Append(DevSites.F3(p.FrontPosition))
              .Append(" renderFront=").Append(DevSites.F(ChainMap.RenderFront(p, RRWClock.RenderFrame, RRWClock.RenderFrameTime)))
              .Append(" cam=").Append(DevSites.F1(p.CameraDistance)).Append("m allowMachines=").Append(p.AllowMachines).Append(" machines=").Append(p.MachineCount).Append('\n');
            sb.Append("  model P0=").Append(DevSites.F(p.Model.P0)).Append(" Frame0=").Append(p.Model.Frame0).Append(" dP/frame=").Append(p.Model.PPerFrame.ToString("E3", global::System.Globalization.CultureInfo.InvariantCulture))
              .Append(" seed=").Append(p.Seed).Append(" rerouted=").Append(p.Rerouted).Append(" parkedMoved=").Append(p.ParkedMoved)
              .Append(" eta=").Append(DevSites.Hours(DevSites.EtaHours(em, p))).Append('\n');
            sb.Append("  plan ").Append(PlanSummary(p)).Append('\n');
            R2Dev.DumpProject(em, p, sb);
            R4Dev.DumpProject(p, sb);
            R5Dev.DumpProject(p, sb);
        }

        private static string PlanSummary(ProjectRecord p)
        {
            var v = p.View();
            var s = new StringBuilder();
            for (int l = 0; l < (int)SurfaceLayer.Count; l++)
            {
                // a layer is a set of per-crew pieces
                PhasePlan.SurfaceSpans((SurfaceLayer)l, v, out SpanSet set);
                if (set.IsEmpty) continue;
                s.Append((SurfaceLayer)l).Append(set.ToString()).Append(' ');
            }
            var pp = PhasePlan.Props(v);
            s.Append("barriers=").Append(pp.Barriers);
            return s.ToString();
        }

        private static void DumpEdge(EntityManager em, Entity e, int i, StringBuilder sb)
        {
            sb.Append(" edge[").Append(i).Append("] ").Append(RRWLog.E(e));
            if (!em.Exists(e)) { sb.Append(" GONE\n"); return; }
            sb.Append(" hidden=").Append(em.HasComponent<Hidden>(e)).Append(" deleted=").Append(em.HasComponent<Deleted>(e));
            if (em.HasComponent<Curve>(e)) sb.Append(" len=").Append(DevSites.F(em.GetComponentData<Curve>(e).m_Length));
            if (em.HasComponent<Composition>(e))
            {
                var c = em.GetComponentData<Composition>(e);
                sb.Append(" comp=").Append(RRWLog.E(c.m_Edge)).Append(em.Exists(c.m_Edge) && em.HasComponent<RRWCompositionClone>(c.m_Edge) ? "(clone)" : "(vanilla)");
            }
            if (em.HasComponent<Edge>(e))
            {
                var ed = em.GetComponentData<Edge>(e);
                sb.Append(" nodes=").Append(RRWLog.E(ed.m_Start)).Append(em.HasComponent<Hidden>(ed.m_Start) ? "(h)" : "")
                  .Append('/').Append(RRWLog.E(ed.m_End)).Append(em.HasComponent<Hidden>(ed.m_End) ? "(h)" : "");
            }
            sb.Append('\n');
            if (em.HasComponent<RoadWorksSite>(e))
            {
                var s = em.GetComponentData<RoadWorksSite>(e);
                sb.Append("  saved kind=").Append(s.Kind).Append(" mode=").Append(DevSites.ModeCode(s.Mode)).Append(" flags=").Append(s.Flags)
                  .Append(" work=").Append(s.m_WorkDone).Append('/').Append(s.m_WorkRequired).Append(" p=").Append(DevSites.F(s.Progress))
                  .Append(" project=").Append(s.m_ProjectId).Append(" u=[").Append(DevSites.F(s.m_ChainU0)).Append(',').Append(DevSites.F(s.m_ChainU1))
                  .Append("] U=").Append(DevSites.F(s.m_ChainLength)).Append(" seed=").Append(s.m_Seed).Append(" paid=").Append(s.m_PaidCost)
                  .Append(" nat=+").Append(DevSites.F(s.m_NatAbove)).Append("/-").Append(DevSites.F(s.m_NatBelow))
                  .Append(" restore t=").Append(DevSites.F(s.RestoreT)).Append(" y=").Append(DevSites.F(s.RestoreY))
                  .Append(" cancel=").Append(DevSites.Code(s.CancelPhase)).Append('@').Append(DevSites.F(s.m_CancelFront)).Append('\n');
            }
            else sb.Append("  saved: MISSING RoadWorksSite\n");
            if (em.HasComponent<RoadWorksRuntime>(e))
            {
                var r = em.GetComponentData<RoadWorksRuntime>(e);
                sb.Append("  runtime phase=").Append(r.m_Phase).Append(" f=").Append(DevSites.F(r.m_PhaseFraction)).Append(" p=").Append(DevSites.F(r.m_Progress))
                  .Append(" flags=").Append(r.m_Flags).Append(" closureTarget=").Append(r.m_ClosureTarget)
                  .Append(" edgeLen=").Append(DevSites.F(r.m_EdgeLength)).Append(" dSub=").Append(DevSites.F(r.m_SubgradeDepth))
                  .Append(" grade=").Append(DevSites.F(r.m_GradeOffset)).Append(" phaseChanged=").Append(r.m_PhaseChangedFrame)
                  .Append(" openLanes=").Append(R2Dev.Zones(r.m_OpenLanes)).Append(" softZones=").Append(R2Dev.Zones(r.m_SoftZones)).Append('\n');
            }
            else sb.Append("  runtime: none\n");
            if (em.HasComponent<RoadWorksGround>(e))
            {
                var g = em.GetComponentData<RoadWorksGround>(e);
                sb.Append("  ground kind=").Append(g.m_Kind).Append(" flags=").Append(g.m_Flags).Append(" settled=").Append(g.Settled)
                  .Append(" floor min=").Append(DevSites.F(g.m_FloorMinRel)).Append(" max=").Append(DevSites.F(g.m_FloorMaxRel))
                  .Append(" y=").Append(DevSites.F(g.m_AppliedY)).Append(" t=").Append(DevSites.F(g.m_AppliedT))
                  .Append(" step@").Append(g.m_StepUpdate).Append(" hold@").Append(g.m_HoldStartUpdate).Append(" now@").Append(RRWClock.UpdateIndex).Append('\n');
            }
            else sb.Append("  ground: none\n");
            if (SiteRegistry.TryGetEdge(e, out var rec))
            {
                var sec = rec.Section;
                sb.Append("  record chain#").Append(rec.ChainIndex).Append(" arc=").Append(rec.Arc != null ? DevSites.F(rec.Arc.Length) : "null")
                  .Append(" geoRev=").Append(rec.GeometryRevision).Append(" hiddenApplied=").Append(rec.HiddenApplied)
                  .Append(" dependants=").Append(rec.Dependants).Append(" waiting=").Append(rec.BuildingsWaiting)
                  .Append(" closureApplied=").Append(rec.ClosureApplied).Append(" drained=").Append(rec.TrafficDrained)
                  .Append(" openLanesApplied=").Append(R2Dev.Zones(rec.OpenLanesApplied)).Append('\n');
                sb.Append("  section half=").Append(DevSites.F(sec.HalfWidth)).Append(" flat=").Append(DevSites.F(sec.FlatHalfWidth))
                  .Append(" grade=").Append(DevSites.F(sec.GradeOffset)).Append(" trims=").Append(DevSites.F(sec.TrimStart)).Append('/').Append(DevSites.F(sec.TrimEnd))
                  .Append(" carriage=[").Append(DevSites.F(sec.CarriageLo)).Append(',').Append(DevSites.F(sec.CarriageHi)).Append("] intervals=").Append(sec.IntervalCount);
                sb.Append(" slots=");
                for (int k = 0; k < rec.Slots.Length; k++) if (rec.Slots[k] != null) sb.Append((ModuleSlot)k).Append(',');
                sb.Append('\n');
                R3Dev.DumpEdge(em, e, rec, sb);
            }
            else sb.Append("  record: none\n");
            foreach (var kv in RRWIntrospection.Dumpers)
            {
                string d;
                try { d = kv.Value(em, e); }
                catch (global::System.Exception ex) { d = "dumper failed: " + ex.GetType().Name + ": " + ex.Message; }
                if (!string.IsNullOrEmpty(d)) sb.Append("  ").Append(kv.Key).Append(": ").Append(d.Replace("\n", "\n    ")).Append('\n');
            }
        }
    }

    public class RrwProgressCommand : IDevCommand
    {
        public virtual string Name => "rrw.p";
        public string Help => Name + " <site> <p> - set progress (0..1, or 58%) on every edge of the project (Director: SetProgress, model reset)";
        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 2 || !DevSites.TryFloat(pos[1], out float v)) { DevSites.Out(ctx, "usage: " + Help); return; }
            if (v > 1f && v <= 100f) v /= 100f;
            v = math.saturate(v);
            var list = new List<ProjectRecord>();
            if (!DevSites.Resolve(ctx, pos[0], list, out _, out string err)) { DevSites.Out(ctx, "p: " + err); return; }
            foreach (var p in list)
            {
                Req.Enqueue(p, WorksRequestType.SetProgress, v);
                var ph = PhasePlan.PhaseOf(p.Kind, v, out _);
                DevSites.Out(ctx, "p p" + p.Id + " " + p.Progress.ToString("0.000", global::System.Globalization.CultureInfo.InvariantCulture)
                                  + " -> " + v.ToString("0.000", global::System.Globalization.CultureInfo.InvariantCulture) + " phase " + ph);
            }
        }
    }

    public sealed class RrwSetCommand : RrwProgressCommand
    {
        public override string Name => "rrw.set";
    }

    public sealed class RrwPhaseCommand : IDevCommand
    {
        public string Name => "rrw.phase";
        public string Help => "rrw.phase <site> <C0..C4|D0..D2> [f=0] - jump to a phase start (+ fraction f within it)";
        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 2 || !DevSites.TryPhase(pos[1], out WorksPhase ph)) { DevSites.Out(ctx, "usage: " + Help); return; }
            float f = pos.Count > 2 && DevSites.TryFloat(pos[2], out float pf) ? pf : DevSites.OptF(a, "f", 0f);
            f = math.saturate(f);
            var list = new List<ProjectRecord>();
            if (!DevSites.Resolve(ctx, pos[0], list, out _, out string err)) { DevSites.Out(ctx, "phase: " + err); return; }
            foreach (var p in list)
            {
                if (PhasePlan.KindOf(ph) != p.Kind)
                {
                    DevSites.Out(ctx, "phase p" + p.Id + " is a " + p.Kind + ": " + DevSites.Code(ph) + " does not apply");
                    continue;
                }
                Req.Enqueue(p, WorksRequestType.JumpPhase, f, ph);
                float target = PhasePlan.ProgressAt(ph, f);
                DevSites.Out(ctx, "p p" + p.Id + " " + p.Progress.ToString("0.000", global::System.Globalization.CultureInfo.InvariantCulture)
                                  + " -> " + target.ToString("0.000", global::System.Globalization.CultureInfo.InvariantCulture) + " phase " + ph + " f=" + DevSites.F(f));
            }
        }
    }

    public sealed class RrwTimeScaleCommand : IDevCommand
    {
        public string Name => "rrw.timescale";
        public string Help => "rrw.timescale [x] [shift=obey|ignore] [machines=<y>] - global work-time scale (all sites, survives loads); machine clock defaults to x";
        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count > 0)
            {
                if (!DevSites.TryFloat(pos[0], out float x) || x < 0f) { DevSites.Out(ctx, "usage: " + Help); return; }
                RRWDebug.WorkTimeScale = math.min(x, 10000f);
                RRWDebug.MachineClockScale = math.clamp(DevSites.OptF(a, "machines", RRWDebug.WorkTimeScale), 0f, 10000f);
            }
            else
            {
                string m = DevSites.Opt(a, "machines", null);
                if (m != null && DevSites.TryFloat(m, out float y)) RRWDebug.MachineClockScale = math.clamp(y, 0f, 10000f);
            }
            string shift = DevSites.Opt(a, "shift", null);
            if (shift != null)
            {
                string s = shift.ToLowerInvariant();
                if (s == "ignore" || s == "0" || s == "off") RRWDebug.IgnoreShift = true;
                else if (s == "obey" || s == "1" || s == "on") RRWDebug.IgnoreShift = false;
            }
            DevSites.Out(ctx, "timescale " + DevSites.F(RRWDebug.WorkTimeScale) + " machines=" + DevSites.F(RRWDebug.MachineClockScale)
                              + " shift=" + (RRWDebug.IgnoreShift ? "ignore" : "obey"));
        }
    }

    public sealed class LayerCommand : IDevCommand
    {
        public string Name => "rrw.layer";
        public string Help => "rrw.layer <hide|terrain|surfaces|machines|bones|props|closure|icons|wear|previewhide|all> <0|1> - switch a layer (RRWDebug.Layers)";

        public static string Describe()
        {
            if (RRWDebug.Layers == DebugLayers.All) return "all";
            var sb = new StringBuilder();
            foreach (DebugLayers l in global::System.Enum.GetValues(typeof(DebugLayers)))
            {
                if (l == DebugLayers.None || l == DebugLayers.All) continue;
                if (RRWDebug.On(l)) sb.Append(sb.Length > 0 ? "," : "").Append(l.ToString().ToLowerInvariant());
            }
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 2) { DevSites.Out(ctx, "usage: " + Help + " (now layers=" + Describe() + ")"); return; }
            DebugLayers l;
            switch (pos[0].ToLowerInvariant())
            {
                case "hide": l = DebugLayers.Hide; break;
                case "terrain": l = DebugLayers.Terrain; break;
                case "surfaces": l = DebugLayers.Surfaces; break;
                case "machines": l = DebugLayers.Machines; break;
                case "bones": l = DebugLayers.Bones; break;
                case "props": l = DebugLayers.Props; break;
                case "closure": l = DebugLayers.Closure; break;
                case "icons": l = DebugLayers.Icons; break;
                case "wear": l = DebugLayers.Wear; break;
                case "previewhide": l = DebugLayers.PreviewHide; break;   // U1 A/B test: Director's PreviewHideSystem
                case "all": l = DebugLayers.All; break;
                default: DevSites.Out(ctx, "unknown layer '" + pos[0] + "'"); return;
            }
            bool on = DevSites.Bool(pos[1], true);
            if (on) RRWDebug.Layers |= l; else RRWDebug.Layers &= ~l;
            DevSites.Out(ctx, "layers=" + Describe());
        }
    }

    public sealed class HideAfterGroundCommand : IDevCommand
    {
        public string Name => "rrw.hideafterground";
        public string Help => "rrw.hideafterground <0|1> - fallback: hide works roads only after Ground applied its profile";
        public void Run(DevContext ctx, string[] a)
        {
            if (a.Length > 0) RRWDebug.HideAfterGround = DevSites.Bool(a[0], RRWDebug.HideAfterGround);
            DevSites.Out(ctx, "hideafterground " + (RRWDebug.HideAfterGround ? 1 : 0));
        }
    }

    public sealed class VerboseCommand : IDevCommand
    {
        public string Name => "rrw.verbose";
        public string Help => "rrw.verbose <0|1> - extra logging (RRWDebug.Verbose)";
        public void Run(DevContext ctx, string[] a)
        {
            if (a.Length > 0) RRWDebug.Verbose = DevSites.Bool(a[0], RRWDebug.Verbose);
            DevSites.Out(ctx, "verbose " + (RRWDebug.Verbose ? 1 : 0));
        }
    }

    public sealed class FinishCommand : IDevCommand
    {
        public string Name => "rrw.finish";
        public string Help => "rrw.finish <site> - complete the works now (Director: Finish)";
        public void Run(DevContext ctx, string[] a) => Req.Run(ctx, a, Help, p => WorksRequestType.Finish, "finish");
    }

    public sealed class CancelCommand : IDevCommand
    {
        public string Name => "rrw.cancel";
        public string Help => "rrw.cancel <site> [instant] - cancel like the UI button (instant = delete + full refund, constructions only)";
        public void Run(DevContext ctx, string[] a)
        {
            bool instant = global::System.Array.Exists(a, s => s.Equals("instant", global::System.StringComparison.OrdinalIgnoreCase));
            Req.Run(ctx, a, Help, p => instant && p.Kind == WorksKind.Construction ? WorksRequestType.CancelInstant : WorksRequestType.Cancel, "cancel");
        }
    }

    public sealed class RushCommand : IDevCommand
    {
        public string Name => "rrw.rush";
        public string Help => "rrw.rush <site> - rush the works (Director charges RushCost)";
        public void Run(DevContext ctx, string[] a) => Req.Run(ctx, a, Help, p => (p.Flags & SiteFlags.Rushed) != 0 ? (WorksRequestType?)null : WorksRequestType.Rush, "rush");
    }

    public sealed class RebuildCommand : IDevCommand
    {
        public string Name => "rrw.rebuild";
        public string Help => "rrw.rebuild - every module respawns its layer (RebuildVisuals)";
        public void Run(DevContext ctx, string[] a)
        {
            WorksRequests.Enqueue(new WorksRequest { Type = WorksRequestType.RebuildVisuals });
            DevSites.Out(ctx, "request RebuildVisuals");
        }
    }

    public sealed class FinishAllCommand : IDevCommand
    {
        public string Name => "rrw.finishall";
        public string Help => "rrw.finishall - complete every works in the city (FinishAll)";
        public void Run(DevContext ctx, string[] a)
        {
            WorksRequests.Enqueue(new WorksRequest { Type = WorksRequestType.FinishAll });
            DevSites.Out(ctx, "request FinishAll");
        }
    }

    public sealed class StartCommand : IDevCommand
    {
        private static int s_Batch = 1000;
        public string Name => "rrw.start";
        public string Help => "rrw.start <roads> [c|d] - start works on existing roads as ONE batch (roads: 3 | 0-4 | 0,2,5 = road indices of the dev 'list' command)";
        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 1) { DevSites.Out(ctx, "usage: " + Help); return; }
            bool demolition = pos.Count > 1 && pos[1].StartsWith("d", global::System.StringComparison.OrdinalIgnoreCase);
            var roads = ctx.Roads();
            var idx = new List<int>();
            foreach (var part in pos[0].Split(','))
            {
                int dash = part.IndexOf('-');
                if (dash > 0 && DevSites.TryInt(part.Substring(0, dash), out int lo) && DevSites.TryInt(part.Substring(dash + 1), out int hi))
                    for (int i = lo; i <= hi; i++) idx.Add(i);
                else if (DevSites.TryInt(part, out int one)) idx.Add(one);
                else { DevSites.Out(ctx, "start: bad road list '" + pos[0] + "'"); return; }
            }
            var em = ctx.EntityManager;
            int batch = ++s_Batch, n = 0, skipped = 0;
            var seen = new HashSet<Entity>();
            foreach (int i in idx)
            {
                if (i < 0 || i >= roads.Count) { skipped++; continue; }
                Entity e = roads[i];
                if (!seen.Add(e) || em.HasComponent<RoadWorksSite>(e)) { skipped++; continue; }
                WorksRequests.Enqueue(new WorksRequest
                {
                    Type = demolition ? WorksRequestType.StartDemolition : WorksRequestType.StartConstruction,
                    Edge = e,
                    Batch = batch,
                });
                n++;
            }
            DevSites.Out(ctx, "start " + (demolition ? "demolition" : "construction") + " edges=" + n + " batch=" + batch + (skipped > 0 ? " skipped=" + skipped : ""));
        }
    }

    public sealed class CheckCommand : IDevCommand
    {
        public string Name => "rrw.check";
        public string Help => "rrw.check - Core invariants + every module checker";
        public void Run(DevContext ctx, string[] a)
        {
            var problems = new List<string>();
            var em = ctx.EntityManager;
            try { CoreInvariants(em, problems); }
            catch (global::System.Exception e) { problems.Add("core check failed: " + e.GetType().Name + ": " + e.Message); }
            try { R2Dev.Invariants(em, problems); }
            catch (global::System.Exception e) { problems.Add("R2Dev check failed: " + e.GetType().Name + ": " + e.Message); }
            try { R3Dev.Invariants(em, problems); }
            catch (global::System.Exception e) { problems.Add("R3Dev check failed: " + e.GetType().Name + ": " + e.Message); }
            try { R4Dev.Invariants(em, problems); }
            catch (global::System.Exception e) { problems.Add("R4Dev check failed: " + e.GetType().Name + ": " + e.Message); }
            try { R5Dev.Invariants(em, problems); }
            catch (global::System.Exception e) { problems.Add("R5Dev check failed: " + e.GetType().Name + ": " + e.Message); }
            try { UwCheck.Invariants(em, problems); }
            catch (global::System.Exception e) { problems.Add("upgrade works check failed: " + e.GetType().Name + ": " + e.Message); }
            foreach (var kv in RRWIntrospection.Checkers)
            {
                try { kv.Value(em, problems); }
                catch (global::System.Exception e) { problems.Add(kv.Key + " checker failed: " + e.GetType().Name + ": " + e.Message); }
            }
            DevSites.Out(ctx, "check gates " + RRWGates.Describe() + " | " + R3Dev.CityText());
            if (problems.Count == 0)
            {
                DevSites.Out(ctx, "check ok (" + SiteRegistry.Projects.Count + " projects, " + SiteRegistry.Edges.Count + " edges, " + RRWIntrospection.Checkers.Count + " module checkers)");
                return;
            }
            DevSites.Out(ctx, "check FAIL " + problems.Count + ":");
            for (int i = 0; i < problems.Count && i < 60; i++) DevSites.Out(ctx, "check  - " + problems[i]);
            if (problems.Count > 60) DevSites.Out(ctx, "check  ... " + (problems.Count - 60) + " more");
        }

        private static void CoreInvariants(EntityManager em, List<string> problems)
        {
            foreach (var kv in SiteRegistry.Edges)
            {
                Entity e = kv.Key;
                string n = "edge " + RRWLog.E(e);
                if (!em.Exists(e)) { problems.Add(n + " in registry but gone"); continue; }
                bool site = em.HasComponent<RoadWorksSite>(e), rt = em.HasComponent<RoadWorksRuntime>(e), gr = em.HasComponent<RoadWorksGround>(e);
                if (!site || !rt || !gr) { problems.Add(n + " missing" + (site ? "" : " RoadWorksSite") + (rt ? "" : " RoadWorksRuntime") + (gr ? "" : " RoadWorksGround")); continue; }
                var r = em.GetComponentData<RoadWorksRuntime>(e);
                bool hidden = em.HasComponent<Hidden>(e);
                if (hidden != r.Hidden) problems.Add(n + " Hidden=" + hidden + " but runtime Hidden=" + r.Hidden);
                if (r.Hidden && r.m_Mode == VisualMode.FullDig)
                {
                    var g = em.GetComponentData<RoadWorksGround>(e);
                    if (g.m_Kind == TerrainProfileKind.Vanilla || g.m_Kind == TerrainProfileKind.Unknown)
                        problems.Add(n + " hidden mode-A edge with ground " + g.m_Kind + " (clip / hole risk)");
                }
                var s = em.GetComponentData<RoadWorksSite>(e);
                if (s.m_ProjectId != kv.Value.ProjectId) problems.Add(n + " saved project " + s.m_ProjectId + " != record " + kv.Value.ProjectId);
            }
            foreach (var p in SiteRegistry.Projects.Values)
            {
                uint done = 0, req = 0;
                bool first = true;
                foreach (var e in p.Edges)
                {
                    if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                    var s = em.GetComponentData<RoadWorksSite>(e);
                    if (first) { done = s.m_WorkDone; req = s.m_WorkRequired; first = false; continue; }
                    if (s.m_WorkDone != done || s.m_WorkRequired != req)
                    {
                        problems.Add("p" + p.Id + " edges disagree: work " + s.m_WorkDone + "/" + s.m_WorkRequired + " vs " + done + "/" + req);
                        break;
                    }
                }
                if (p.Id >= SiteRegistry.NextProjectId) problems.Add("NextProjectId " + SiteRegistry.NextProjectId + " <= project id " + p.Id);
            }

            // saved ids vs NextProjectId (sites not yet in the registry included)
            var siteQ = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadWorksSite>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
            var sites = siteQ.ToComponentDataArray<RoadWorksSite>(Allocator.Temp);
            uint maxId = 0;
            for (int i = 0; i < sites.Length; i++) maxId = math.max(maxId, sites[i].m_ProjectId);
            int unregistered = siteQ.CalculateEntityCount() - SiteRegistry.Edges.Count;
            sites.Dispose();
            siteQ.Dispose();
            if (SiteRegistry.Loaded && maxId >= SiteRegistry.NextProjectId) problems.Add("NextProjectId " + SiteRegistry.NextProjectId + " <= saved id " + maxId);
            if (SiteRegistry.Loaded && unregistered > 0) problems.Add(unregistered + " RoadWorksSite entities not in the registry yet (ok only in the frame they were created)");

            // derived entities: LivePath + owner in the registry
            var derQ = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RRWDerived>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            var der = derQ.ToEntityArray(Allocator.Temp);
            var derData = derQ.ToComponentDataArray<RRWDerived>(Allocator.Temp);
            int noLive = 0, orphan = 0;
            for (int i = 0; i < der.Length; i++)
            {
                if (!em.HasComponent<LivePath>(der[i])) noLive++;
                Entity site = derData[i].m_Site;
                if (site != Entity.Null && !SiteRegistry.Edges.ContainsKey(site)) orphan++;
            }
            der.Dispose();
            derData.Dispose();
            derQ.Dispose();
            if (noLive > 0) problems.Add(noLive + " RRWDerived entities without LivePath (would be saved)");
            if (orphan > 0) problems.Add(orphan + " RRWDerived entities whose site is not in the registry (GC pending?)");

            // visible tool previews of hidden works edges (verified in game to cause preview flicker). The Director hides them
            // with TempFlags.Hidden (renderer cull), not with the Hidden component.
            var tq = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Edge>() },
                None = new[] { ComponentType.ReadOnly<Hidden>(), ComponentType.ReadOnly<Deleted>() },
            });
            var tents = tq.ToEntityArray(Allocator.Temp);
            var tdata = tq.ToComponentDataArray<Temp>(Allocator.Temp);
            int visiblePreviews = 0;
            for (int i = 0; i < tents.Length; i++)
            {
                if ((tdata[i].m_Flags & (TempFlags.Delete | TempFlags.Hidden)) != 0) continue;
                Entity o = tdata[i].m_Original;
                if (o == Entity.Null || !SiteRegistry.Edges.ContainsKey(o) || !em.HasComponent<RoadWorksRuntime>(o)) continue;
                if (em.GetComponentData<RoadWorksRuntime>(o).Hidden) visiblePreviews++;
            }
            tents.Dispose();
            tdata.Dispose();
            tq.Dispose();
            if (visiblePreviews > 0) problems.Add(visiblePreviews + " visible Temp previews of hidden works edges");
        }
    }

    public sealed class PerfCommand : IDevCommand
    {
        public string Name => "rrw.perf";
        public string Help => "rrw.perf [seconds=10] [detail=1] - reset RRWPerf, report per-module main-thread time after n real seconds (0 = report now); " +
                              "detail=1 (default) adds the module sections / counters (lines 'perfd <group>', see rrw.mx.perf); " +
                              "'perf crews' / 'perf targets' lines: crews and active crews over the window, Machines ms per active crew, performance verdict";
        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            float secs = pos.Count > 0 && DevSites.TryFloat(pos[0], out float s0) ? s0 : DevSites.OptF(a, "seconds", 10f);
            Measure(ctx, secs, DevSites.OptF(a, "detail", 1f) > 0f ? new string[0] : null);
        }

        // Shared by rrw.mx.perf. groups: null = no detail lines, empty = every detail group, else those groups.
        internal static void Measure(DevContext ctx, float secs, string[] groups, global::System.Action done = null)
        {
            if (secs <= 0f) { Report(ctx, global::System.Math.Max(1, RRWPerf.Frames), "now", groups); done?.Invoke(); return; }
            RRWPerf.Reset();
            PerfCrews.Begin();
            int f0 = UnityEngine.Time.frameCount;
            DevSites.Out(ctx, "perf measuring " + DevSites.F(secs) + "s ...");
            DevDeferred.After(secs, () =>
            {
                done?.Invoke();
                Report(ctx, global::System.Math.Max(1, UnityEngine.Time.frameCount - f0), DevSites.F(secs) + "s", groups);
            });
        }

        private static void Report(DevContext ctx, int frames, string window, string[] groups)
        {
            double total = 0, machines = 0, worst = 0;
            string worstSlot = null;
            var sb = new StringBuilder();
            for (int i = 0; i < (int)PerfSlot.Count; i++)
            {
                int calls = RRWPerf.Calls[i];
                if (calls == 0) continue;
                double ms = RRWPerf.Ms(RRWPerf.Ticks[i]);
                total += ms;
                if ((PerfSlot)i == PerfSlot.Machines || (PerfSlot)i == PerfSlot.MachineJobs) machines += ms;
                double mx = RRWPerf.Ms(RRWPerf.MaxTicks[i]);
                if (mx > worst) { worst = mx; worstSlot = ((PerfSlot)i).ToString(); }
                sb.Append("perf ").Append((PerfSlot)i).Append(" avg=").Append((ms / frames).ToString("0.000", global::System.Globalization.CultureInfo.InvariantCulture))
                  .Append("ms/frame perCall=").Append((ms / calls).ToString("0.000", global::System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" max=").Append(RRWPerf.Ms(RRWPerf.MaxTicks[i]).ToString("0.00", global::System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" calls=").Append(calls).Append('\n');
            }
            sb.Append("perf total avg=").Append((total / frames).ToString("0.000", global::System.Globalization.CultureInfo.InvariantCulture))
              .Append("ms/frame frames=").Append(frames).Append(" window=").Append(window).Append(" sites=").Append(SiteRegistry.Edges.Count)
              .Append(" projects=").Append(SiteRegistry.Projects.Count).Append(" (budget all < 0.6 ms)\n");
            PerfCrews.Append(sb, frames, machines, total, worst, worstSlot);   // per-crew numbers + the performance verdict
            if (groups != null) RRWPerfDetail.Append(sb, frames, groups.Length == 0 ? null : groups);
            DevSites.Lines(ctx, sb);
        }
    }
}
#endif
