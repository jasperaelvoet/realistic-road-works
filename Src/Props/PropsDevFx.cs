#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using ObjElevation = Game.Objects.Elevation;
using ObjTransform = Game.Objects.Transform;

// Dev commands for checking the Props experimental switches (RRWGates) in game, ported from the staged-traffic test commands:
//   rrw.props.fx:  fence panels, signs, amber head as standalone props on any road;
//   rrw.props.sig: amber head TrafficLight.m_State writes and watch.
// The commands only queue requests (MainLoop); PropSystem executes them at Modification1 (structural changes there are
// initialised by vanilla in the same frame) and PropOwnerSystem adds the late Owner at Modification4 (verified in game). Every
// spawned prop carries LivePath + RRWDerived (m_Site = Null: never garbage-collected, never saved) and RRWProp (project 0).
namespace RealisticRoadWorks.V3.Props
{
    internal sealed class FxRequest
    {
        public string Op = "spawn";      // spawn | clear | list
        public string Prefab;
        public Entity Edge;
        public float Lat, T0, T1 = 1f, Pitch = -1f, Yaw;
        public int N;
        public string Y = "curve";       // curve | ped | ground
        public bool Chord = true;
        public int Owner = -1;           // -1 = auto (no owner for traffic lights), 0 / 1
        public string Tag = "fx";
    }

    internal sealed class SigRequest
    {
        public string Target = "all";
        public int State = -1;
        public bool Watch, Stop;
    }

    internal static class PropsDevFx
    {
        public static readonly List<FxRequest> FxQueue = new List<FxRequest>();
        public static readonly List<SigRequest> SigQueue = new List<SigRequest>();
        public static readonly Dictionary<string, List<Entity>> Tags = new Dictionary<string, List<Entity>>();
        public static readonly List<Entity> Watch = new List<Entity>();
        public static string WatchLabel = "";
        public static uint WatchNext;
        public static int NextKey = 1;

        public static void Clear()
        {
            FxQueue.Clear();
            SigQueue.Clear();
            Tags.Clear();
            Watch.Clear();
            WatchLabel = "";
        }

        public static void Log(string msg) => Mod.Log.Info("dev rrw props " + msg);

        // key=value argument (case-insensitive key); null when absent
        public static string Kv(string[] a, int from, string key)
        {
            for (int i = from; a != null && i < a.Length; i++)
            {
                int eq = a[i].IndexOf('=');
                if (eq > 0 && string.Equals(a[i].Substring(0, eq), key, StringComparison.OrdinalIgnoreCase)) return a[i].Substring(eq + 1);
            }
            return null;
        }

        public static float F(string s, float def) => s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;
        public static int I(string s, int def) => s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;
    }

    // rrw.props.fx <prefab> <edge#> [lat=<m>] [t0=0] [t1=1] [pitch=<m>|auto] [n=<N>] [yaw=<deg>] [y=curve|ped|ground] [chord=1]
    //              [owner=0|1] [tag=<name>]
    // rrw.props.fx clear [tag|all] | rrw.props.fx list
    public sealed class PropsFxCommand : IDevCommand
    {
        public string Name => "rrw.props.fx";
        public string Help => "rrw.props.fx <prefab> <edge#> [lat=<m edge frame>] [t0=0] [t1=1] [pitch=<m>|auto] [n=<N>] [yaw=<deg>] [y=curve|ped|ground] [chord=1] [owner=0|1] [tag=] | clear [tag|all] | list" +
                              " - static prop run along a road (fence panels, signs, amber heads): chord-placed, pivot-corrected, late owner, LivePath; logs bounds, pivot, flags, joint gaps, foot Y error";

        public void Run(DevContext ctx, string[] a)
        {
            string first = PropsDev.Arg(a, 0, "");
            if (first == "" || first == "help") { PropsDevFx.Log("fx: " + Help); return; }
            if (first == "clear") { PropsDevFx.FxQueue.Add(new FxRequest { Op = "clear", Tag = PropsDev.Arg(a, 1, "all") }); PropsDevFx.Log("fx: clear queued"); return; }
            if (first == "list") { PropsDevFx.FxQueue.Add(new FxRequest { Op = "list" }); return; }
            if (a.Length < 2) { PropsDevFx.Log("fx: need <prefab> <edge#>. " + Help); return; }
            var r = new FxRequest { Prefab = first };
            try { r.Edge = ctx.Road(a[1]); }
            catch (Exception e) { PropsDevFx.Log("fx: bad edge '" + a[1] + "': " + e.Message); return; }
            r.Lat = PropsDevFx.F(PropsDevFx.Kv(a, 2, "lat"), 0f);
            r.T0 = math.saturate(PropsDevFx.F(PropsDevFx.Kv(a, 2, "t0"), 0f));
            r.T1 = math.saturate(PropsDevFx.F(PropsDevFx.Kv(a, 2, "t1"), 1f));
            string pitch = PropsDevFx.Kv(a, 2, "pitch");
            r.Pitch = pitch == null || pitch == "auto" ? -1f : PropsDevFx.F(pitch, -1f);
            r.N = PropsDevFx.I(PropsDevFx.Kv(a, 2, "n"), 0);
            r.Yaw = PropsDevFx.F(PropsDevFx.Kv(a, 2, "yaw"), 0f);
            r.Y = (PropsDevFx.Kv(a, 2, "y") ?? "curve").ToLowerInvariant();
            r.Chord = PropsDevFx.I(PropsDevFx.Kv(a, 2, "chord"), 1) != 0;
            r.Owner = PropsDevFx.I(PropsDevFx.Kv(a, 2, "owner"), -1);
            r.Tag = PropsDevFx.Kv(a, 2, "tag") ?? "fx";
            PropsDevFx.FxQueue.Add(r);
            PropsDevFx.Log("fx: queued " + r.Prefab + " on " + RRWLog.E(r.Edge) + " lat=" + RRWLog.F(r.Lat) + " t=[" + RRWLog.F(r.T0) + "," + RRWLog.F(r.T1) + "] tag=" + r.Tag);
        }
    }

    // rrw.props.sig <entity#[:ver]|tag|p<id>|all> <state|red|yellow|green|flash|amber|off|watch|stop>
    //   state: TrafficLight.m_State (1 red, 2 yellow, 4 green, 8 flashing; amber = 10 = yellow|flashing), written once at Mod1;
    //   watch: log the value every 60 updates (stop ends it). Product amber heads (p<id>) are re-asserted to 10 by Props on its
    //   next self-heal pass (<= 16 updates): use a dev-spawned head (rrw.props.fx EU_TrafficLightCar01 ...) for the state matrix.
    public sealed class PropsSigCommand : IDevCommand
    {
        public string Name => "rrw.props.sig";
        public string Help => "rrw.props.sig <entity#[:ver]|tag|p<id>|all> <0..15|red|yellow|green|flash|amber|off|watch|stop> - TrafficLight.m_State of amber heads";

        public void Run(DevContext ctx, string[] a)
        {
            if (a == null || a.Length < 2) { PropsDevFx.Log("sig: " + Help); return; }
            var r = new SigRequest { Target = a[0] };
            string v = a[1].ToLowerInvariant();
            switch (v)
            {
                case "watch": r.Watch = true; break;
                case "stop": r.Stop = true; break;
                case "red": r.State = 1; break;
                case "yellow": r.State = 2; break;
                case "green": r.State = 4; break;
                case "flash": r.State = 8; break;
                case "amber": r.State = 10; break;
                case "off": r.State = 0; break;
                default:
                    r.State = PropsDevFx.I(v, -1);
                    if (r.State < 0 || r.State > 0xFFFF) { PropsDevFx.Log("sig: bad state '" + v + "'. " + Help); return; }
                    break;
            }
            PropsDevFx.SigQueue.Add(r);
            PropsDevFx.Log("sig: queued " + r.Target + " " + v);
        }
    }

    public partial class PropSystem
    {
        partial void DevPreload() { PropsDevFx.Clear(); PropsDevPuff.Clear(); }

        partial void DevUpdate()
        {
            try
            {
                if (PropsDevPuff.Queue.Count > 0 || PropsDevPuff.Live.Count > 0) DevPuffTick();   // rrw.props.puff
                if (PropsDevFx.FxQueue.Count > 0)
                {
                    for (int i = 0; i < PropsDevFx.FxQueue.Count; i++)
                    {
                        var r = PropsDevFx.FxQueue[i];
                        try
                        {
                            if (r.Op == "clear") DevFxClear(r.Tag);
                            else if (r.Op == "list") DevFxList();
                            else DevFxSpawn(r);
                        }
                        catch (Exception e) { PropsDevFx.Log("fx: " + r.Op + " failed: " + e.Message); }
                    }
                    PropsDevFx.FxQueue.Clear();
                }
                if (PropsDevFx.SigQueue.Count > 0)
                {
                    for (int i = 0; i < PropsDevFx.SigQueue.Count; i++)
                    {
                        try { DevSig(PropsDevFx.SigQueue[i]); }
                        catch (Exception e) { PropsDevFx.Log("sig failed: " + e.Message); }
                    }
                    PropsDevFx.SigQueue.Clear();
                }
                if (PropsDevFx.Watch.Count > 0 && m_Update >= PropsDevFx.WatchNext)
                {
                    PropsDevFx.WatchNext = m_Update + 60u;
                    var em = EntityManager;
                    var sb = new System.Text.StringBuilder("sig watch " + PropsDevFx.WatchLabel + " update=" + m_Update + ":");
                    for (int i = 0; i < PropsDevFx.Watch.Count && i < 16; i++)
                    {
                        Entity e = PropsDevFx.Watch[i];
                        sb.Append(' ').Append(RRWLog.E(e)).Append('=');
                        sb.Append(EcsUtil.Alive(em, e) && em.HasComponent<TrafficLight>(e) ? ((int)em.GetComponentData<TrafficLight>(e).m_State).ToString() : "gone");
                    }
                    PropsDevFx.Log(sb.ToString());
                }
            }
            catch (Exception e) { RRWLog.ErrorOnce("props dev", e); }
        }

        private void DevFxClear(string tag)
        {
            var em = EntityManager;
            int n = 0;
            var keys = new List<string>(PropsDevFx.Tags.Keys);
            foreach (var k in keys)
            {
                if (tag != "all" && k != tag) continue;
                foreach (var e in PropsDevFx.Tags[k]) if (EcsUtil.Alive(em, e)) { EcsUtil.MarkDeleted(em, e); n++; }
                PropsDevFx.Tags.Remove(k);
            }
            PropsDevFx.Log("fx: cleared " + n + " props (" + tag + ")");
        }

        private void DevFxList()
        {
            var em = EntityManager;
            if (PropsDevFx.Tags.Count == 0) { PropsDevFx.Log("fx: no dev props"); return; }
            foreach (var kv in PropsDevFx.Tags)
            {
                int alive = 0, owned = 0, overridden = 0;
                foreach (var e in kv.Value)
                {
                    if (!EcsUtil.Alive(em, e)) continue;
                    alive++;
                    if (em.HasComponent<Owner>(e)) owned++;
                    if (em.HasComponent<Overridden>(e)) overridden++;
                }
                Entity first = kv.Value.Count > 0 ? kv.Value[0] : Entity.Null;
                PropsDevFx.Log("fx: tag " + kv.Key + " props=" + kv.Value.Count + " alive=" + alive + " owned=" + owned + " overridden=" + overridden +
                               " first=" + RRWLog.E(first) + (EcsUtil.Alive(em, first) ? " at " + RRWLog.F3(em.GetComponentData<ObjTransform>(first).m_Position) : ""));
            }
        }

        // One placement of a dev run (chord or tangent / n-mode), before entity creation.
        private struct FxPlace
        {
            public float3 Pos;
            public quaternion Rot;
            public float3 A, B;      // panel ends (long axis) after placement, XZ (joint gaps, foot Y)
        }

        private void DevFxSpawn(FxRequest r)
        {
            var em = EntityManager;
            if (!EcsUtil.Alive(em, r.Edge) || !em.HasComponent<Curve>(r.Edge)) { PropsDevFx.Log("fx: edge " + RRWLog.E(r.Edge) + " not alive"); return; }
            Entity prefab = PrefabCatalog.Static(m_PrefabSystem, r.Prefab);
            var info = Info(prefab);
            if (!info.Valid) { PropsDevFx.Log("fx: prefab '" + r.Prefab + "' not found / no valid archetype"); return; }
            var arc = new EdgeArc(em.GetComponentData<Curve>(r.Edge).m_Bezier);
            float L = arc.Length;
            float s0 = r.T0 * L, s1 = math.max(s0, r.T1 * L);
            bool longZ = info.Size.z >= info.Size.x;
            float len = math.max(info.Size.x, info.Size.z);
            float pitch = r.Pitch > 0f ? r.Pitch : len;
            float lift = r.Y == "ped" ? SidewalkLift(r.Edge) : 0f;
            bool ground = r.Y == "ground";
            bool isLight = em.HasComponent<TrafficLightData>(prefab);
            bool own = r.Owner < 0 ? !isLight : r.Owner != 0;
            var places = new List<FxPlace>(64);

            if (r.N > 0)
            {
                // n-mode (signs, heads): n props at the centres of n equal parts of [t0, t1], local +Z along the curve + yaw,
                // no long-axis rule, no pivot correction (the pole base stands at the point)
                for (int i = 0; i < r.N && i < 512; i++)
                {
                    float s = s0 + (i + 0.5f) / r.N * (s1 - s0);
                    float3 pos = arc.Offset(s, r.Lat);
                    pos.y = ground ? DevGroundY(pos) : pos.y + lift;
                    float2 d = math.normalizesafe(arc.Direction(s).xz, new float2(0f, 1f));
                    var rot = math.mul(quaternion.LookRotationSafe(new float3(d.x, 0f, d.y), math.up()), quaternion.RotateY(math.radians(r.Yaw)));
                    places.Add(new FxPlace { Pos = pos, Rot = rot, A = pos, B = pos });
                }
            }
            else
            {
                float s = s0;
                for (int guard = 0; guard < 512; guard++)
                {
                    float sN = r.Chord ? DevChordStep(arc, s, pitch, r.Lat) : s + pitch;
                    if (sN > s1 + 1e-3f) break;
                    float3 A = arc.Offset(s, r.Lat), B = arc.Offset(sN, r.Lat);
                    float3 mid = r.Chord ? (A + B) * 0.5f : arc.Offset((s + sN) * 0.5f, r.Lat);
                    float2 d = r.Chord ? math.normalizesafe((B - A).xz, new float2(0f, 1f)) : math.normalizesafe(arc.Direction((s + sN) * 0.5f).xz, new float2(0f, 1f));
                    float extra = r.Yaw + (longZ ? 0f : 90f);
                    var rot = math.mul(quaternion.LookRotationSafe(new float3(d.x, 0f, d.y), math.up()), quaternion.RotateY(math.radians(extra)));
                    float3 pos = mid;
                    pos.y = ground ? DevGroundY(mid) : (r.Chord ? math.min(A.y, B.y) - PropLayout.kPanelDrop : mid.y) + lift;
                    if (r.Chord) { float3 c = info.Centre; c.y = 0f; pos -= math.mul(rot, c); }
                    float3 axis = math.mul(rot, longZ ? new float3(0f, 0f, 1f) : new float3(1f, 0f, 0f));
                    float3 centre = pos + math.mul(rot, new float3(info.Centre.x, 0f, info.Centre.z));
                    places.Add(new FxPlace { Pos = pos, Rot = rot, A = centre - axis * len * 0.5f, B = centre + axis * len * 0.5f });
                    s = sN;
                }
            }
            if (places.Count == 0) { PropsDevFx.Log("fx: nothing placed (range " + RRWLog.F(s1 - s0) + " m < pitch " + RRWLog.F(pitch) + ")"); return; }

            if (!PropsDevFx.Tags.TryGetValue(r.Tag, out var list)) { list = new List<Entity>(); PropsDevFx.Tags[r.Tag] = list; }
            var arch = em.GetComponentData<ObjectData>(prefab).m_Archetype;
            Entity owner = own && ValidOwner(em, em.GetComponentData<Edge>(r.Edge).m_Start) ? em.GetComponentData<Edge>(r.Edge).m_Start : Entity.Null;
            float gapMin = float.MaxValue, gapMax = float.MinValue, gapSum = 0f, floatMax = 0f, buryMax = 0f, slopeMax = 0f;
            int gaps = 0;
            for (int i = 0; i < places.Count; i++)
            {
                var pl = places[i];
                var arr = em.CreateEntity(arch, 1, Allocator.Temp);
                Entity e = arr[0];
                arr.Dispose();
                em.SetComponentData(e, new PrefabRef(prefab));
                em.SetComponentData(e, new ObjTransform(pl.Pos, pl.Rot));
                if (em.HasComponent<PseudoRandomSeed>(e)) em.SetComponentData(e, new PseudoRandomSeed((ushort)(1 + (PropsDevFx.NextKey & 0x7FFF))));
                if (!em.HasComponent<Created>(e)) em.AddComponent<Created>(e);
                if (!em.HasComponent<Updated>(e)) em.AddComponent<Updated>(e);
                if (!ground)
                {
                    var el = new ObjElevation(0f, (ElevationFlags)0);
                    if (em.HasComponent<ObjElevation>(e)) em.SetComponentData(e, el); else em.AddComponentData(e, el);
                }
                em.AddComponentData(e, new RRWProp { m_ProjectId = 0, m_Key = PropsDevFx.NextKey++ });
                EcsUtil.TagDerived(em, e, Entity.Null, 0u, DerivedGroup.PropStatic);
                if (owner != Entity.Null) PropState.OwnerRequests.Add(new OwnerRequest { Prop = e, Owner = owner });
                list.Add(e);

                // stats: joint gap to the next panel (along this panel's axis), foot Y error at both long-axis ends
                if (r.N <= 0)
                {
                    if (i + 1 < places.Count)
                    {
                        float3 dir = math.normalizesafe(pl.B - pl.A);
                        float g = math.dot((places[i + 1].A - pl.B).xz, dir.xz);
                        gapMin = math.min(gapMin, g); gapMax = math.max(gapMax, g); gapSum += g; gaps++;
                    }
                    for (int f = 0; f < 2; f++)
                    {
                        float3 foot = f == 0 ? pl.A : pl.B;
                        float surf = ground ? DevGroundY(foot) : arc.Position(arc.Project(foot)).y + lift;
                        float err = pl.Pos.y - surf;
                        if (err > floatMax) floatMax = err;
                        if (-err > buryMax) buryMax = -err;
                    }
                    slopeMax = math.max(slopeMax, math.abs(pl.B.y - pl.A.y));
                }
            }
            string g0 = gaps > 0 ? " joints min=" + RRWLog.F(gapMin) + " max=" + RRWLog.F(gapMax) + " avg=" + RRWLog.F(gapSum / gaps) + " (pass: |gap| <= 0.30)" : "";
            string y0 = r.N <= 0 ? " footY floatMax=" + RRWLog.F(floatMax) + " buryMax=" + RRWLog.F(buryMax) + " (pass: float <= 0.05, buried <= 0.10)" : "";
            PropsDevFx.Log("fx: tag " + r.Tag + " spawned " + places.Count + " x " + r.Prefab + " on " + RRWLog.E(r.Edge) + " (L=" + RRWLog.F(L) + ") lat=" + RRWLog.F(r.Lat) +
                           " size=" + RRWLog.F3(info.Size) + " longAxis=" + (longZ ? "Z" : "X") + " boundsCentre(pivot offset)=" + RRWLog.F3(info.Centre) +
                           " flags=" + info.Flags + (isLight ? " TrafficLight(state " + DevLightState(list[list.Count - 1]) + ")" : "") +
                           " pitch=" + RRWLog.F(pitch) + " chord=" + r.Chord + " y=" + r.Y + " owner=" + (owner != Entity.Null ? RRWLog.E(owner) : "none") + g0 + y0);
        }

        private string DevLightState(Entity e)
        {
            var em = EntityManager;
            return EcsUtil.Alive(em, e) && em.HasComponent<TrafficLight>(e) ? ((int)em.GetComponentData<TrafficLight>(e).m_State).ToString() : "?";
        }

        private float DevGroundY(float3 p)
        {
            float y = TerrainY(p);
            return float.IsNaN(y) ? p.y : y;
        }

        private static float DevChordStep(EdgeArc arc, float s, float len, float lat)
        {
            float3 a = arc.Offset(s, lat);
            float sN = s + len;
            for (int k = 0; k < 6; k++)
            {
                float d = math.distance(a.xz, arc.Offset(sN, lat).xz);
                if (d < 1e-3f) break;
                float n = s + (sN - s) * len / d;
                bool done = math.abs(n - sN) < 1e-3f;
                sN = n;
                if (done) break;
            }
            return sN;
        }

        private void DevSig(SigRequest r)
        {
            var em = EntityManager;
            var targets = new List<Entity>();
            string t = r.Target;
            if (t == "all")
            {
                foreach (var kv in PropsDevFx.Tags) targets.AddRange(kv.Value);
                foreach (var kv in PropState.Projects) AddProductHeads(kv.Value, targets);
            }
            else if (t.Length > 1 && t[0] == 'p' && uint.TryParse(t.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid))
            {
                if (PropState.Projects.TryGetValue(pid, out var pp)) AddProductHeads(pp, targets);
            }
            else if (PropsDevFx.Tags.TryGetValue(t, out var tagged)) targets.AddRange(tagged);
            else
            {
                string[] parts = t.Split(':');
                if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                {
                    int ver = parts.Length > 1 ? PropsDevFx.I(parts[1], 0) : 0;
                    foreach (var kv in PropsDevFx.Tags) foreach (var e in kv.Value) if (e.Index == idx && (ver == 0 || e.Version == ver)) targets.Add(e);
                    foreach (var kv in PropState.Projects) foreach (var skv in kv.Value.Slots)
                        if (skv.Value.Entity.Index == idx && (ver == 0 || skv.Value.Entity.Version == ver)) targets.Add(skv.Value.Entity);
                }
            }
            targets.RemoveAll(e => !EcsUtil.Alive(em, e) || !em.HasComponent<TrafficLight>(e));
            if (r.Stop) { PropsDevFx.Watch.Clear(); PropsDevFx.Log("sig: watch stopped"); return; }
            if (targets.Count == 0) { PropsDevFx.Log("sig: no TrafficLight props for '" + t + "'"); return; }
            if (r.Watch)
            {
                PropsDevFx.Watch.Clear();
                PropsDevFx.Watch.AddRange(targets);
                PropsDevFx.WatchLabel = t;
                PropsDevFx.WatchNext = 0;
                PropsDevFx.Log("sig: watching " + targets.Count + " heads (" + t + "), every 60 updates");
                return;
            }
            foreach (var e in targets)
            {
                var tl = em.GetComponentData<TrafficLight>(e);
                int before = (int)tl.m_State;
                tl.m_State = (Game.Objects.TrafficLightState)r.State;
                em.SetComponentData(e, tl);
                PropsDevFx.Log("sig: " + RRWLog.E(e) + " state " + before + " -> " + r.State + (em.HasComponent<Owner>(e) ? " (OWNED: " + RRWLog.E(em.GetComponentData<Owner>(e).m_Owner) + ")" : " (unowned)"));
            }
        }

        private static void AddProductHeads(ProjectProps pp, List<Entity> into)
        {
            foreach (var kv in pp.Slots)
                if (PropKeys.Kind(kv.Key) == PropKind.Signal && kv.Value.Entity != Entity.Null) into.Add(kv.Value.Entity);
        }
    }
}
#endif
