#if DEVTOOLS
using System.Collections.Generic;
using System.Globalization;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;
using ObjTransform = Game.Objects.Transform;

// Props module dev commands (DEVTOOLS builds only; named "rrw.<module>.<verb>"). Read-only except respawn, which only
// sets a flag that PropSystem consumes at Modification1 (no structural change in MainLoop).
namespace RealisticRoadWorks.V3.Props
{
    internal static class PropsDev
    {
        public static void Log(string msg) => Mod.Log.Info("dev rrw props " + msg);

        // "p<id>" / "#n" (index in the registry projects sorted by id) / "all" (0) / empty (0)
        public static uint Project(string arg)
        {
            if (string.IsNullOrEmpty(arg) || arg == "all") return 0;
            if (arg[0] == 'p' && uint.TryParse(arg.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id)) return id;
            if (arg[0] == '#' && int.TryParse(arg.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                var ids = new List<uint>(SiteRegistry.Projects.Keys);
                ids.Sort();
                return n >= 0 && n < ids.Count ? ids[n] : uint.MaxValue;
            }
            return uint.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint raw) ? raw : uint.MaxValue;
        }

        public static string Arg(string[] a, int i, string def) => a != null && a.Length > i ? a[i] : def;
    }

    // rrw.props.list [p<id>|#n|all]: per project slot counts by kind, heaps, beacons, owners, thinning, far state.
    public sealed class PropsListCommand : IDevCommand
    {
        public string Name => "rrw.props.list";
        public string Help => "rrw.props.list [p<id>|#n|all] - props per project: counts by kind, heaps/dust/beacons, explicit-Y, owned, overridden, thinning, far";

        public void Run(DevContext ctx, string[] a)
        {
            uint want = PropsDev.Project(PropsDev.Arg(a, 0, "all"));
            var em = ctx.EntityManager;
            int shown = 0;
            foreach (var kv in PropState.Projects)
            {
                if (want != 0 && kv.Key != want) continue;
                var pp = kv.Value;
                var counts = new int[(int)PropKind.Count];
                int heaps = 0, dust = 0, expl = 0, owned = 0, over = 0, hidden = 0, dead = 0;
                foreach (var skv in pp.Slots)
                {
                    var sl = skv.Value;
                    counts[(int)PropKeys.Kind(sl.Key)]++;
                    if (sl.Heap) heaps++;
                    if (sl.Dust) dust++;
                    if (sl.Explicit) expl++;
                    if (!EcsUtil.Alive(em, sl.Entity)) { dead++; continue; }
                    if (em.HasComponent<Owner>(sl.Entity)) owned++;
                    if (em.HasComponent<Overridden>(sl.Entity)) over++;
                    if (em.HasComponent<Game.Tools.Hidden>(sl.Entity)) hidden++;
                }
                var parts = new List<string>();
                for (int i = 0; i < counts.Length; i++) if (counts[i] > 0) parts.Add((PropKind)i + "=" + counts[i]);
                SiteRegistry.TryGetProject(pp.Id, out var p);
                PropsDev.Log("p" + pp.Id + (p != null ? " " + p.Kind + " " + p.Phase + " f=" + RRWLog.F(p.PhaseFraction) + " closure=" + p.Closure + " dist=" + RRWLog.F(p.CameraDistance) : " (no record)") +
                             " total=" + pp.Slots.Count + " [" + string.Join(" ", parts) + "] heaps=" + heaps + " dust=" + dust + " explicitY=" + expl +
                             " owned=" + owned + " overridden=" + over + " hidden=" + hidden + " dead=" + dead + " thin=" + pp.Thin + " est=" + pp.Estimate +
                             " far=" + pp.Far + " camChain=" + RRWLog.F(pp.CamDist) + "m farChanges=" + pp.FarChanges + " spawned=" + pp.Spawned + " replaced=" + pp.Replaced + " moved=" + pp.Moved + " deleted=" + pp.Deleted +
                             " | barriers=" + pp.LastBarriers + " fence=" + pp.LastFence + (pp.FencesFit ? "" : "(dropped:budget " + pp.FenceEstimate + ")") +
                             " openLanes=" + (p != null ? p.OpenLanes.ToString() : "-") + " releasing=" + (p != null && p.Releasing) + " released=" + pp.Released +
                             " foreign=" + pp.Foreign.Count + " endRespawns=" + pp.EndRespawns + " healDeleted=" + pp.HealDeleted +
                             " skippedForeign=" + pp.SkippedForeign + " skippedOutside=" + pp.SkippedOutside);
                // crews and diff counters
                if (p != null)
                {
                    var v = p.View();
                    PropsDev.Log("p" + pp.Id + " r4 crews=" + v.CrewCount + " focus=" + p.FocusCrew + " depots=" + pp.Depots + " [" + PropSystem.DepotList(v) + "]" +
                                 " depotProps=" + counts[(int)PropKind.CrewDepot] + " | diffs full=" + pp.FullDiffs + " front=" + pp.FrontDiffs +
                                 " frontApplied=" + pp.FrontApplied + " heal scanned=" + pp.HealScanned + " missing=" + pp.HealMissing + " deferred=" + pp.HealDeferred +
                                 " fillCache=" + pp.FillCacheValid + " | front step=" + PropCrewLayout.FrontStep(v) + " dividerPick=" + PropCrewLayout.DividerPickKey(v));
                }
                // staged-traffic device plan and layout of the last diff
                if (pp.HavePlan)
                {
                    var pl = pp.LastPlan;
                    PropsDev.Log("p" + pp.Id + " r3 plan: fenceSides=" + RoadZoneMath.Describe(pl.FenceSides) + " barriers=" + pl.Barriers + " worksHalf=" + RoadZoneMath.Describe(pl.WorksHalf) +
                                 " divider=" + pl.Divider + " signs=" + pl.Signs + " approachSignals=" + pl.ApproachSignals + " inLaneReady=" + pl.InLaneReady +
                                 " openEntryAtEnd=" + pl.OpenEntryAtEnd + " | ready=" + (p != null ? RoadZoneMath.Describe(p.WorkZonesReady) : "-") +
                                 " soft=" + (p != null ? RoadZoneMath.Describe(p.SoftZones) : "-") + " switch=" + (p != null ? p.Switch.ToString() : "-") +
                                 " lht=" + RRWCity.LeftHandTraffic + " na=" + RRWCity.NaTheme + " cityKnown=" + RRWCity.Known);
                    PropsDev.Log("p" + pp.Id + " r3 devices: " + pp.DeviceLine + " fenceRuns=" + pp.FenceRuns + " joint=" + RRWLog.F(pp.FenceDelta) +
                                 " lampSite=" + pp.LampSite + " signalWrites=" + pp.SignalWrites + " signalAsserts=" + pp.SignalAsserts);
                    for (int i = 0; i < pp.Access.Count && i < 12; i++)
                    {
                        var ap = pp.Access[i];
                        SiteRegistry.TryGetEdge(ap.Edge, out var rec);
                        PropsDev.Log("p" + pp.Id + " r3 access " + RRWLog.E(ap.Building) + " edge " + RRWLog.E(ap.Edge) + " u=" + RRWLog.F(ap.U) + " side=" + (ap.Side == 0 ? "L" : "R") +
                                     " closedB=" + (rec != null ? RoadZoneMath.Describe(rec.ClosedBApplied) : "-") + " gap=" +
                                     (rec != null && (rec.ClosedBApplied & (ap.Side == 0 ? RoadZones.LeftHalf : RoadZones.RightHalf)) != 0 ? "yes" : "no"));
                    }
                }
                shown++;
            }
            PropsDev.Log("list projects=" + shown + " totalProps=" + PropState.TotalProps + " ownerApplied=" + PropOwnerSystem.Applied +
                         " ownerRemoved=" + PropOwnerSystem.Removed + " heapWrites=" + HeapFillSystem.Writes + " | gates " + RRWGates.Describe());
        }
    }

    // rrw.props.foreign [p<id>|#n|all]: the non-works roads each project keeps its props out of,
    // the chain-end topology state (pending end respawns) and every prop that stands outside [TrimU0, TrimU1].
    public sealed class PropsForeignCommand : IDevCommand
    {
        public string Name => "rrw.props.foreign";
        public string Help => "rrw.props.foreign [p<id>|#n|all] - non-works roads next to each chain (props are kept out of them), end-topology state, props outside the trims";

        public void Run(DevContext ctx, string[] a)
        {
            uint want = PropsDev.Project(PropsDev.Arg(a, 0, "all"));
            var em = ctx.EntityManager;
            foreach (var kv in PropState.Projects)
            {
                if (want != 0 && kv.Key != want) continue;
                var pp = kv.Value;
                SiteRegistry.TryGetProject(pp.Id, out var p);
                PropsDev.Log("foreign p" + pp.Id + " roads=" + pp.Foreign.Count + " topoKnown=" + pp.TopoKnown + " pendingStart=" + pp.PendingStart + " pendingEnd=" + pp.PendingEnd +
                             " endRespawns=" + pp.EndRespawns + " healDeleted=" + pp.HealDeleted + " skippedForeign=" + pp.SkippedForeign + " skippedOutside=" + pp.SkippedOutside +
                             (p != null ? " startJunction=" + p.StartIsJunction + " endJunction=" + p.EndIsJunction + " trims=[" + RRWLog.F(p.TrimU0) + "," + RRWLog.F(p.TrimU1) + "] U=" + RRWLog.F(p.ChainLength) : " (no record)"));
                for (int i = 0; i < pp.Foreign.Count; i++)
                {
                    var f = pp.Foreign[i];
                    PropsDev.Log("foreign p" + pp.Id + "  road " + RRWLog.E(f.Edge) + " hw=" + RRWLog.F(f.HalfWidth) + (f.Connected ? " connected" : " found-under-prop") +
                                 " alive=" + EcsUtil.Alive(em, f.Edge) + " a=" + RRWLog.F3(f.Curve.a) + " d=" + RRWLog.F3(f.Curve.d));
                }
                if (p == null) continue;
                int shown = 0;
                foreach (var skv in pp.Slots)
                {
                    var sl = skv.Value;
                    if (sl.Entity == Entity.Null) continue;
                    float t1 = p.TrimU1 > 0f ? p.TrimU1 : p.ChainLength;
                    if (sl.U >= p.TrimU0 - 0.1f && sl.U <= t1 + 0.1f) continue;
                    if (shown++ < 12) PropsDev.Log("foreign p" + pp.Id + "  outside " + PropKeys.Name(sl.Key) + " u=" + RRWLog.F(sl.U) + (sl.AllowOutside ? " (hidden dead-end cap line: allowed)" : " NOT ALLOWED"));
                }
            }
        }
    }

    // rrw.props.check: the module checker (alive, LivePath, RRWDerived, Overridden, owner, quantity, explicit Y vs floor).
    public sealed class PropsCheckCommand : IDevCommand
    {
        public string Name => "rrw.props.check";
        public string Help => "rrw.props.check - props invariants incl. 'props keep their Y' (explicit-Y props vs FloorWorldY +-0.05 m)";

        public void Run(DevContext ctx, string[] a)
        {
            var sys = ctx.World.GetExistingSystemManaged<PropSystem>();
            if (sys == null) { PropsDev.Log("check: PropSystem not running"); return; }
            var problems = new List<string>();
            var detail = new List<string>();
            sys.CheckInto(ctx.EntityManager, problems, detail);
            foreach (var d in detail) PropsDev.Log("check " + d);
            for (int i = 0; i < problems.Count && i < 40; i++) PropsDev.Log("check FAIL " + problems[i]);
            PropsDev.Log(problems.Count == 0 ? "check ok" : "check FAIL " + problems.Count);
        }
    }

    // rrw.props.respawn [p<id>|#n|all]: replace every prop of the project(s) at the next Modification1 (new first, then old deleted).
    public sealed class PropsRespawnCommand : IDevCommand
    {
        public string Name => "rrw.props.respawn";
        public string Help => "rrw.props.respawn [p<id>|#n|all] - respawn props (same slots, seeds and blink phases; dust variants re-evaluated)";

        public void Run(DevContext ctx, string[] a)
        {
            uint id = PropsDev.Project(PropsDev.Arg(a, 0, "all"));
            if (id == 0) PropState.DevRespawnAll = true; else PropState.DevRespawnProject = id;
            PropsDev.Log("respawn requested " + (id == 0 ? "all" : "p" + id));
        }
    }

    // rrw.props.clones: effect clone registration report (RRW Amber Light, RRW Dust VFX, beacon barrier, dust heaps).
    public sealed class PropsClonesCommand : IDevCommand
    {
        public string Name => "rrw.props.clones";
        public string Help => "rrw.props.clones - effect clone registration (where, sources, clones, VFX skipped?) and resolved prefab entities";

        public void Run(DevContext ctx, string[] a)
        {
            var ps = ctx.World.GetExistingSystemManaged<PrefabSystem>();
            PropsDev.Log("clones done=" + PropClones.Done + " in=" + PropClones.RegisteredIn + " attempts=" + PropClones.Attempts + " sources=" + PropClones.SourcesFound +
                         " vfxSkipped=" + PropClones.VfxSkipped + " effectsOk=" + RRWPrefabRegistry.EffectsOk + " clones=[" + PropClones.Names() + "]");
            Show(ps, PrefabNames.AmberLight, PropClones.AmberLight);
            Show(ps, PrefabNames.DustVfx, PropClones.DustVfx);
            Show(ps, PrefabNames.BarrierBeacon, PropClones.BarrierBeacon);
            Show(ps, PrefabNames.DustHeapOre, PropClones.DustHeapOre);
            Show(ps, PrefabNames.DustHeapStone, PropClones.DustHeapStone);
            Show(ps, PrefabNames.ConeLamp, PropClones.ConeLamp);
            Show(ps, PropClones.kDustHeapOreVanilla, PropClones.DustHeapOreVanilla);
            Show(ps, PropClones.kDustHeapStoneVanilla, PropClones.DustHeapStoneVanilla);
            Show(ps, PrefabNames.DustPuff, PropClones.DustPuff);
            PropsDev.Log("clones dust puff done=" + RRWPrefabRegistry.DustPuffDone + " ok=" + RRWPrefabRegistry.DustPuffOk + " state: " + PropClones.PuffState
                         + " overridableCleared=" + PropClones.PuffOverridableCleared + " animFrames=" + PropClones.PuffAnimFrames
                         + " (expect " + (int)math.round(RRWConst.kDustPuffAnimSeconds * 60f) + ") | test: rrw.props.puff <site>");
            PropsDev.Log("clones vanilla dust " + PropClones.VanillaDustInfo + " | clone " + PropClones.Describe(PropClones.DustVfx));
            PropsDev.Log("clones dust source=" + DustVfx.ModeName + " check: " + DustVfx.Report);
            foreach (var line in PropClones.Report) PropsDev.Log("clones report " + line);
        }

        private static void Show(PrefabSystem ps, string name, PrefabBase p)
        {
            Entity e = PropClones.Resolve(ps, p);
            PropsDev.Log("clone " + name + " " + (p == null ? "MISSING" : "entity " + RRWLog.E(e)));
        }
    }

    // rrw.props.dust [check|auto|clone|vanilla]: dust-rendering A/B switch and VFX slot check of the "RRW Dust VFX" clone.
    // check: re-runs the VFXSystem slot check at the next Mod1 (result in the log: "props: dust vfx check ...").
    // auto/clone/vanilla: which carriers the dust heaps use; existing dust heaps are swapped in place (new entity first).
    public sealed class PropsDustCommand : IDevCommand
    {
        public string Name => "rrw.props.dust";
        public string Help => "rrw.props.dust [check|auto|clone|vanilla] - dust heap effect source (RRW Dust VFX clone vs vanilla DustcloudSmallVFX) and the clone's VFX slot check";

        public void Run(DevContext ctx, string[] a)
        {
            string sub = PropsDev.Arg(a, 0, "").ToLowerInvariant();
            switch (sub)
            {
                case "check":
                    DustVfx.State = 0;
                    DustVfx.Attempts = 0;
                    DustVfx.NextTry = 0;
                    DustVfx.Report = "re-check requested";
                    break;
                case "auto": DustVfx.Mode = DustSource.Auto; PropState.DustReresolve = true; break;
                case "clone": DustVfx.Mode = DustSource.Clone; PropState.DustReresolve = true; break;
                case "vanilla": DustVfx.Mode = DustSource.Vanilla; PropState.DustReresolve = true; break;
                case "": break;
                default: PropsDev.Log("dust: unknown '" + sub + "'. " + Help); return;
            }
            RRWPrefabRegistry.DustVanilla = DustVfx.UseVanilla;   // Machines' dusty excavator follows at once
            PropsDev.Log("dust source=" + DustVfx.ModeName + " dustVanilla=" + RRWPrefabRegistry.DustVanilla + " state=" + DustVfx.State + " attempts=" + DustVfx.Attempts + " check: " + DustVfx.Report +
                         " | vanilla " + PropClones.VanillaDustInfo + " | clone " + PropClones.Describe(PropClones.DustVfx));
        }
    }

    // rrw.props.beacons [p<id>|#n|all]: every beacon with its blink phase (0..59 frames) in line order, to verify the chase.
    public sealed class PropsBeaconsCommand : IDevCommand
    {
        public string Name => "rrw.props.beacons";
        public string Help => "rrw.props.beacons [p<id>|#n|all] - beacon barriers per line with blink phases (chase at closed ends, random elsewhere)";

        public void Run(DevContext ctx, string[] a)
        {
            uint want = PropsDev.Project(PropsDev.Arg(a, 0, "all"));
            var em = ctx.EntityManager;
            var ps = ctx.World.GetExistingSystemManaged<PrefabSystem>();
            Entity beacon = PropClones.Resolve(ps, PropClones.BarrierBeacon);
            foreach (var kv in PropState.Projects)
            {
                if (want != 0 && kv.Key != want) continue;
                var keys = new List<int>(kv.Value.Slots.Keys);
                keys.Sort();
                var line = new List<string>();
                int lastLine = -1;
                foreach (int key in keys)
                {
                    var sl = kv.Value.Slots[key];
                    if (sl.Prefab != beacon || beacon == Entity.Null) continue;
                    int lineId = (int)PropKeys.Kind(key) * 16 + PropKeys.Sub(key);
                    if (lineId != lastLine && line.Count > 0) { PropsDev.Log("beacons p" + kv.Key + " " + string.Join(" ", line)); line.Clear(); }
                    lastLine = lineId;
                    ushort seed = EcsUtil.Alive(em, sl.Entity) && em.HasComponent<PseudoRandomSeed>(sl.Entity) ? em.GetComponentData<PseudoRandomSeed>(sl.Entity).m_Seed : sl.Seed;
                    line.Add(PropKeys.Name(key) + ":ph" + BeaconPhase.PhaseOf(seed));
                }
                if (line.Count > 0) PropsDev.Log("beacons p" + kv.Key + " " + string.Join(" ", line));
            }
        }
    }

    // rrw.props.y [p<id>|#n|all]: explicit-Y props with their current Y vs the floor the module would place them at now.
    public sealed class PropsYCommand : IDevCommand
    {
        public string Name => "rrw.props.y";
        public string Help => "rrw.props.y [p<id>|#n|all] - sample of explicit-Y props: transform Y, Elevation, edge, step update";

        public void Run(DevContext ctx, string[] a)
        {
            uint want = PropsDev.Project(PropsDev.Arg(a, 0, "all"));
            var em = ctx.EntityManager;
            foreach (var kv in PropState.Projects)
            {
                if (want != 0 && kv.Key != want) continue;
                int shown = 0;
                foreach (var skv in kv.Value.Slots)
                {
                    var sl = skv.Value;
                    if (!sl.Explicit || !EcsUtil.Alive(em, sl.Entity)) continue;
                    var tr = em.GetComponentData<ObjTransform>(sl.Entity);
                    string el = em.HasComponent<Game.Objects.Elevation>(sl.Entity) ? "elev(" + RRWLog.F(em.GetComponentData<Game.Objects.Elevation>(sl.Entity).m_Elevation) + "," + em.GetComponentData<Game.Objects.Elevation>(sl.Entity).m_Flags + ")" : "no-elev";
                    uint step = em.HasComponent<RoadWorksGround>(sl.SiteEdge) ? em.GetComponentData<RoadWorksGround>(sl.SiteEdge).m_StepUpdate : 0u;
                    PropsDev.Log("y p" + kv.Key + " " + PropKeys.Name(sl.Key) + " " + sl.Y + " pos=" + RRWLog.F3(tr.m_Position) + " " + el + " edge=" + RRWLog.E(sl.SiteEdge) + " step=" + step);
                    if (++shown >= 24) break;
                }
            }
        }
    }
}
#endif
