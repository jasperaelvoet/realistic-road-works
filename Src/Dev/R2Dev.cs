#if DEVTOOLS
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Hidden = Game.Tools.Hidden;
using NetNode = Game.Net.Node;
using NotificationIcon = Game.Notifications.Icon;

// Dev aids: rrw.dump / rrw.check lines for the machine report, staged opening and the release
// gate, the U1 preview checks, and rrw.preview.errors. Core only (Src/Dev is compiled into every module build); the
// Director's preview set is read by reflection so this file never depends on Src/Director.
namespace RealisticRoadWorks.V3.DevCmds
{
    internal static class R2Dev
    {
        private const int kCheckSlackUpdates = 120;   // ~2 s of slack on the U5 timing checks

        // Per-side groups (walkL/walkR, parkL/parkR) through Core's RoadZoneMath.Describe.
        public static string Zones(RoadZones z) => RoadZoneMath.Describe(z);

        private static string Age(uint since, uint now)
        {
            if (since == 0) return "never";
            uint d = unchecked(now - since);
            return d + "u(" + DevSites.F1(d / 60f) + "s)";
        }

        // rrw.dump: one line per project with the machine report, staged opening and release state (all runtime, nothing saved).
        public static void DumpProject(EntityManager em, ProjectRecord p, StringBuilder sb)
        {
            uint now = RRWClock.UpdateIndex;
            bool fresh = p.MachinesReportFresh(now);
            sb.Append("  r2 machineZones=").Append(Zones(p.MachineZones)).Append(" onCarriageway=").Append(p.MachinesOnCarriageway)
              .Append(" report=").Append(Age(p.MachinesReportUpdate, now)).Append(fresh ? " fresh" : " STALE")
              .Append(" openLanes=").Append(Zones(p.OpenLanes))
              .Append(" releaseSince=").Append(p.ReleaseSince).Append(p.Releasing ? " (" + Age(p.ReleaseSince, now) + " ago)" : "")
              .Append(" puppets=").Append(CountPuppets(em, p.Id)).Append('\n');
            R3Dev.DumpProject(em, p, sb);
        }

        // ------------------------------------------------------------------ rrw.check

        public static void Invariants(EntityManager em, List<string> problems)
        {
            uint now = RRWClock.UpdateIndex;

            // U1: the Hidden COMPONENT on a Temp net entity drops it out of vanilla's junction topology (EdgeIterator)
            // and causes the false "collision" errors. Vanilla never puts it there; only our old preview hide did.
            int tempHidden = CountTempNetWithHidden(em);
            if (tempHidden > 0) problems.Add(tempHidden + " Temp net entities (edges/nodes) carry the Hidden component (U1: must be 0; PreviewHide uses TempFlags.Hidden)");

            Dictionary<uint, int> puppets = PuppetsByProject(em);
            foreach (var p in SiteRegistry.Projects.Values)
            {
                // M-open: no puppet in (or planned into) an open lane group
                // (mode H lane drops and switched-off new parking leave their groups open: a puppet on a ready drop lane or on a
                // switched-off parking lane touches that group without being in traffic)
                RoadZones inOpen = p.MachineZones & p.OpenLanes & RoadZones.AllLanes & ~UwCheck.MachineAllowance(p);
                if (p.MachinesReportFresh(now) && inOpen != 0)
                    problems.Add("p" + p.Id + " machines inside open lane group(s) " + Zones(inOpen) + " (M-open)");

                // U3: a mode-A construction never carries traffic mid-works on its carriageway as a slow zone
                if (p.Kind == WorksKind.Construction && p.Mode == VisualMode.FullDig && p.Phase != WorksPhase.Complete && !p.Releasing
                    && RRWDebug.On(DebugLayers.Closure))
                {
                    int slow = 0;
                    for (int i = 0; i < p.Edges.Count; i++)
                        if (SiteRegistry.TryGetEdge(p.Edges[i], out var er) && er.ClosureApplied == ClosureLevel.SlowZone) slow++;
                    if (slow > 0) problems.Add("p" + p.Id + " mode-A construction in " + p.Phase + " has " + slow + " SlowZone edge(s) (U3: Closed for the whole works, staged opening uses lane groups)");
                }

                // Staged opening: lane groups never open on an edge that is still hidden (trench)
                for (int i = 0; i < p.Edges.Count; i++)
                    if (SiteRegistry.TryGetEdge(p.Edges[i], out var er) && er.HiddenApplied && er.OpenLanesApplied != RoadZones.None)
                        problems.Add("edge " + RRWLog.E(p.Edges[i]) + " of p" + p.Id + " is hidden but has open lane groups " + Zones(er.OpenLanesApplied));

                if (!p.Releasing) continue;
                uint age = p.ReleaseSimAge;   // game time (sim frames): the caps never run while paused
                // U5 / M-release: every puppet gone at the latest kPostSiteMaxSimFrames after the release began
                if (puppets.TryGetValue(p.Id, out int n) && n > 0 && age > RRWConst.kPostSiteMaxSimFrames + kCheckSlackUpdates)
                    problems.Add("p" + p.Id + " still has " + n + " puppet(s) " + age + " sim frames after release (cap kPostSiteMaxSimFrames=" + RRWConst.kPostSiteMaxSimFrames + ")");
                // U3 release gate: the Director opens at the latest at its safety cap
                if (age > RRWConst.kCompletionMachineWaitSimFrames + kCheckSlackUpdates)
                {
                    int closed = 0;
                    for (int i = 0; i < p.Edges.Count; i++)
                        if (SiteRegistry.TryGetEdge(p.Edges[i], out var er) && er.ClosureApplied == ClosureLevel.Closed) closed++;
                    if (closed > 0) problems.Add("p" + p.Id + " released " + Age(p.ReleaseSince, now) + " ago but " + closed + " edge(s) still Closed (cap kCompletionMachineWaitSimFrames=" + RRWConst.kCompletionMachineWaitSimFrames + ")");
                }
            }

            // post-site puppets of projects that are gone entirely (informational unless many: Machines caps their age)
            int orphans = 0;
            foreach (var kv in puppets) if (!SiteRegistry.Projects.ContainsKey(kv.Key)) orphans += kv.Value;
            if (orphans > 8) problems.Add(orphans + " puppets belong to no registry project (post-site; Machines must despawn them within kPostSiteMaxSimFrames)");
        }

        public static int CountTempNetWithHidden(EntityManager em)
        {
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Hidden>() },
                Any = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<NetNode>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            int n = q.CalculateEntityCount();
            q.Dispose();
            return n;
        }

        private static Dictionary<uint, int> PuppetsByProject(EntityManager em)
        {
            var map = new Dictionary<uint, int>();
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RRWMachine>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            var data = q.ToComponentDataArray<RRWMachine>(Allocator.Temp);
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i].m_IsTrailer != 0) continue;
                map.TryGetValue(data[i].m_ProjectId, out int c);
                map[data[i].m_ProjectId] = c + 1;
            }
            data.Dispose();
            q.Dispose();
            return map;
        }

        private static int CountPuppets(EntityManager em, uint projectId)
        {
            return PuppetsByProject(em).TryGetValue(projectId, out int n) ? n : 0;
        }

        // ------------------------------------------------------------------ Director preview sets (reflection)

        private static bool s_Probed;
        private static HashSet<Entity> s_PreviewHidden, s_HiddenNodes;

        // DirectorShared.PreviewHidden / HiddenNodes, or null when the Director is not part of this build.
        public static HashSet<Entity> DirectorSet(string field)
        {
            if (!s_Probed)
            {
                s_Probed = true;
                try
                {
                    var t = typeof(SiteRegistry).Assembly.GetType("RealisticRoadWorks.V3.Director.DirectorShared");
                    const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    s_PreviewHidden = t?.GetField("PreviewHidden", bf)?.GetValue(null) as HashSet<Entity>;
                    s_HiddenNodes = t?.GetField("HiddenNodes", bf)?.GetValue(null) as HashSet<Entity>;
                }
                catch { s_PreviewHidden = s_HiddenNodes = null; }
            }
            return field == "PreviewHidden" ? s_PreviewHidden : field == "HiddenNodes" ? s_HiddenNodes : null;
        }

        // What an entity is in a road-tool preview (R new road, W' works copy, split piece, sub-object of one, ...).
        public static string Role(EntityManager em, Entity e)
        {
            var hiddenNodes = DirectorSet("HiddenNodes");
            for (int depth = 0; depth < 5 && e != Entity.Null && em.Exists(e); depth++)
            {
                string prefix = depth == 0 ? "" : "sub-object of ";
                if (em.HasComponent<Temp>(e))
                {
                    var t = em.GetComponentData<Temp>(e);
                    Entity o = t.m_Original;
                    if (em.HasComponent<Edge>(e))
                    {
                        if (o != Entity.Null && SiteRegistry.Edges.ContainsKey(o)) return prefix + "W' (temp copy of works edge " + RRWLog.E(o) + ")";
                        if (o != Entity.Null) return prefix + "temp copy of road " + RRWLog.E(o);
                        if ((t.m_Flags & TempFlags.Hidden) != 0) return prefix + "hidden temp piece (split remnant of a works road?)";
                        return prefix + "R (new road temp)";
                    }
                    if (em.HasComponent<NetNode>(e))
                    {
                        if (o != Entity.Null && hiddenNodes != null && hiddenNodes.Contains(o)) return prefix + "temp copy of hidden works node " + RRWLog.E(o);
                        if (o != Entity.Null && IsWorksNode(o)) return prefix + "temp copy of works chain node " + RRWLog.E(o);
                        if (o != Entity.Null) return prefix + "temp copy of node " + RRWLog.E(o);
                        return prefix + "new temp node";
                    }
                }
                else
                {
                    if (SiteRegistry.Edges.ContainsKey(e)) return prefix + "permanent works edge";
                    if (hiddenNodes != null && hiddenNodes.Contains(e)) return prefix + "permanent hidden works node";
                    if (em.HasComponent<RRWMachine>(e)) return prefix + "RRW puppet";
                    if (em.HasComponent<RRWDerived>(e)) return prefix + "RRW prop/decal";
                    if (em.HasComponent<NetNode>(e) && IsWorksNode(e)) return prefix + "permanent works chain node";
                }
                if (!em.HasComponent<Owner>(e)) break;
                e = em.GetComponentData<Owner>(e).m_Owner;
            }
            return "other";
        }

        private static bool IsWorksNode(Entity node)
        {
            foreach (var r in SiteRegistry.Edges.Values)
                if (r.StartNode == node || r.EndNode == node) return true;
            return false;
        }
    }

    // rrw.preview.errors - every entity the road tool flagged (Game.Tools.Error / Warning) and every tool notification icon,
    // with what it is relative to the works roads (reproduces the U1 false "collision" errors).
    public sealed class PreviewErrorsCommand : IDevCommand
    {
        private const int kMaxLines = 40;
        public string Name => "rrw.preview.errors";
        public string Help => "rrw.preview.errors - tool validation errors/warnings + tool icons (entity, prefab, Temp original/flags, Hidden, PreviewHidden, role); hover a road preview first";

        public void Run(DevContext ctx, string[] a)
        {
            var em = ctx.EntityManager;
            var ps = ctx.System<PrefabSystem>();
            var preview = R2Dev.DirectorSet("PreviewHidden");
            int errors = List(ctx, em, ps, preview, ComponentType.ReadOnly<Error>(), "ERROR");
            int warnings = List(ctx, em, ps, preview, ComponentType.ReadOnly<Warning>(), "warning");
            int icons = Icons(ctx, em, ps);
            int tempHidden = R2Dev.CountTempNetWithHidden(em);
            int flagged = CountTempFlagHidden(em);
            DevSites.Out(ctx, "preview errors=" + errors + " warnings=" + warnings + " toolIcons=" + icons
                              + " tempNetWithHiddenComponent=" + tempHidden + " (must be 0) tempNetFlaggedHidden=" + flagged
                              + " previewHiddenSet=" + (preview != null ? preview.Count.ToString() : "n/a")
                              + " layers=" + LayerCommand.Describe());
        }

        private static int List(DevContext ctx, EntityManager em, PrefabSystem ps, HashSet<Entity> preview, ComponentType tag, string label)
        {
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { tag },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            var arr = q.ToEntityArray(Allocator.Temp);
            int n = arr.Length;
            for (int i = 0; i < arr.Length && i < kMaxLines; i++)
            {
                Entity e = arr[i];
                var sb = new StringBuilder();
                sb.Append("preview ").Append(label).Append(' ').Append(RRWLog.E(e)).Append(" prefab='").Append(EcsUtil.PrefabName(ps, em, e) ?? "?").Append('\'');
                if (em.HasComponent<Temp>(e))
                {
                    var t = em.GetComponentData<Temp>(e);
                    sb.Append(" temp orig=").Append(RRWLog.E(t.m_Original)).Append(" flags=").Append(t.m_Flags);
                }
                else sb.Append(" permanent");
                sb.Append(" hiddenComp=").Append(em.HasComponent<Hidden>(e))
                  .Append(" inPreviewHidden=").Append(preview != null ? (preview.Contains(e) ? "yes" : "no") : "n/a")
                  .Append(" role=").Append(R2Dev.Role(em, e));
                DevSites.Out(ctx, sb.ToString());
            }
            if (n > kMaxLines) DevSites.Out(ctx, "preview ... " + (n - kMaxLines) + " more " + label);
            arr.Dispose();
            q.Dispose();
            return n;
        }

        // Tool notification icons (ValidationSystem.AddIcon: Owner = temp entity, Target = permanent entity, prefab = error type).
        private static int Icons(DevContext ctx, EntityManager em, PrefabSystem ps)
        {
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<NotificationIcon>(), ComponentType.ReadOnly<Temp>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            var arr = q.ToEntityArray(Allocator.Temp);
            int n = arr.Length;
            for (int i = 0; i < arr.Length && i < kMaxLines; i++)
            {
                Entity e = arr[i];
                Entity owner = em.HasComponent<Owner>(e) ? em.GetComponentData<Owner>(e).m_Owner : Entity.Null;
                Entity target = em.HasComponent<Target>(e) ? em.GetComponentData<Target>(e).m_Target : Entity.Null;
                DevSites.Out(ctx, "preview icon " + RRWLog.E(e) + " '" + (EcsUtil.PrefabName(ps, em, e) ?? "?") + "'"
                                  + " owner=" + RRWLog.E(owner) + (owner != Entity.Null && em.Exists(owner) ? " (" + R2Dev.Role(em, owner) + ")" : "")
                                  + " target=" + RRWLog.E(target) + (target != Entity.Null && em.Exists(target) ? " (" + R2Dev.Role(em, target) + ")" : ""));
            }
            if (n > kMaxLines) DevSites.Out(ctx, "preview ... " + (n - kMaxLines) + " more icons");
            arr.Dispose();
            q.Dispose();
            return n;
        }

        private static int CountTempFlagHidden(EntityManager em)
        {
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>() },
                Any = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<NetNode>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            var temps = q.ToComponentDataArray<Temp>(Allocator.Temp);
            int n = 0;
            for (int i = 0; i < temps.Length; i++)
                if ((temps[i].m_Flags & TempFlags.Hidden) != 0 && (temps[i].m_Flags & TempFlags.Delete) == 0) n++;
            temps.Dispose();
            q.Dispose();
            return n;
        }
    }
}
#endif
