#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Common;
using Game.Effects;
using Game.Objects;
using Game.Prefabs;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using ObjElevation = Game.Objects.Elevation;
using ObjTransform = Game.Objects.Transform;

// rrw.props.puff - spawns "RRW Dust Puff" emitters with EXACTLY the recipe Machines uses
// at breakout / dump (a port of the dig-animation prototype's puff spawn, verified in game), so the prefab registration, the Overridable fix,
// the phase seed and the Hidden carrier can be verified in game independently of the IK dig cycle:
//   ObjectData archetype + Transform; Game.Tools.Hidden (hides the heap mesh, the VFX still renders); PseudoRandomSeed from the
//   PhaseSeed search (the puff animation starts on the spawn frame); Elevation{0, 0}; NO Owner; LivePath + RRWDerived{m_Site =
//   the project's first edge, project id, DerivedGroup.DustPuff}; deleted after 0.9 x the animation period (deleting ends the
//   particles, so never earlier). A probe line ~20 frames after each spawn: hidden, overridden, cull index, enabled effects and
//   the live intensity (> 0 = the puff is drawing).
// The command only queues; PropSystem runs the queue at Modification1 (structural changes there). Props never spawns puffs
// otherwise (Machines does).
namespace RealisticRoadWorks.V3.Props
{
    internal sealed class PuffRequest
    {
        public uint ProjectId;          // 0 = world position only
        public bool HasPos;
        public float3 Pos;
        public int N = 1;
        public float Spacing = 3f;
        public float Dy;
        public bool Hide = true;
        public bool Clear, List;
    }

    internal struct DevPuff
    {
        public Entity E;
        public uint Born, Expire, ProbeAt;
        public uint PhaseErr;
        public ushort Seed;
    }

    internal static class PropsDevPuff
    {
        public static readonly List<PuffRequest> Queue = new List<PuffRequest>();
        public static readonly List<DevPuff> Live = new List<DevPuff>();
        public static int Spawned, Deleted, PhaseMisses, ProbesDrawing, ProbesSilent;
        public const float kLifeFrac = 0.9f;   // verified in game: the curve is 0 from 0.55 of the period on

        public static void Clear()
        {
            Queue.Clear();
            Live.Clear();
            Spawned = Deleted = PhaseMisses = ProbesDrawing = ProbesSilent = 0;
        }

        // Seed whose kLightState random puts the effect animation phase at 0 on frame f0 (EffectTransformSystem phase =
        // (frame + seed-random) % duration). Verified in game.
        public static ushort PhaseSeed(uint f0, uint dur, out uint err)
        {
            dur = math.max(1u, dur);
            uint want = (dur - (f0 % dur)) % dur;
            ushort best = 1;
            err = uint.MaxValue;
            for (int s = 1; s < 65536; s++)
            {
                var rnd = new PseudoRandomSeed((ushort)s).GetRandom(PseudoRandomSeed.kLightState);
                uint r = rnd.NextUInt(dur);
                uint d = r >= want ? r - want : want - r;
                d = math.min(d, dur - d);
                if (d < err) { err = d; best = (ushort)s; if (d == 0) break; }
            }
            return best;
        }
    }

    // rrw.props.puff <p<id>|#n> [n=1] [spacing=3] [dy=0] [hide=1] | at <x> <y> <z> [...] | list | clear
    public sealed class PropsPuffCommand : IDevCommand
    {
        public string Name => "rrw.props.puff";
        public string Help => "rrw.props.puff <p<id>|#n> [n=1] [spacing=3] [dy=0] [hide=1] | at <x> <y> <z> | list | clear - spawn '" + PrefabNames.DustPuff +
                              "' emitters with Machines' recipe (Hidden carrier, phase seed, no Owner, LivePath, DerivedGroup.DustPuff) at the project front; " +
                              "probe line ~20 frames later (enabled / liveIntensity > 0 = drawing)";

        public void Run(DevContext ctx, string[] a)
        {
            string first = PropsDev.Arg(a, 0, "").ToLowerInvariant();
            if (first == "" || first == "help") { PropsDev.Log("puff: " + Help); return; }
            var r = new PuffRequest();
            if (first == "clear") { r.Clear = true; PropsDevPuff.Queue.Add(r); PropsDev.Log("puff: clear queued"); return; }
            if (first == "list") { r.List = true; PropsDevPuff.Queue.Add(r); return; }
            int opt = 1;
            if (first == "at")
            {
                if (a.Length < 4 || !TryF(a[1], out float x) || !TryF(a[2], out float y) || !TryF(a[3], out float z)) { PropsDev.Log("puff: at <x> <y> <z>"); return; }
                r.HasPos = true;
                r.Pos = new float3(x, y, z);
                opt = 4;
            }
            else
            {
                uint id = PropsDev.Project(first);
                if (id == 0 || !SiteRegistry.TryGetProject(id, out var p)) { PropsDev.Log("puff: no project '" + first + "'"); return; }
                r.ProjectId = id;
                r.Pos = p.FrontPosition;
            }
            r.N = math.clamp(PropsDevFx.I(PropsDevFx.Kv(a, opt, "n"), 1), 1, 16);
            r.Spacing = PropsDevFx.F(PropsDevFx.Kv(a, opt, "spacing"), 3f);
            r.Dy = PropsDevFx.F(PropsDevFx.Kv(a, opt, "dy"), 0f);
            r.Hide = PropsDevFx.I(PropsDevFx.Kv(a, opt, "hide"), 1) != 0;
            PropsDevPuff.Queue.Add(r);
            PropsDev.Log("puff: queued " + r.N + " at " + RRWLog.F3(r.Pos) + (r.ProjectId != 0 ? " (p" + r.ProjectId + " front)" : "") + " hide=" + r.Hide
                         + " prefab ok=" + RRWPrefabRegistry.DustPuffOk + " (" + PropClones.PuffState + ")");
        }

        static bool TryF(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    public partial class PropSystem
    {
        // Modification1 (called from DevUpdate): runs queued puff requests, probes and deletes expired dev puffs.
        private void DevPuffTick()
        {
            var em = EntityManager;
            uint frame = RRWClock.RenderFrame;
            if (PropsDevPuff.Queue.Count > 0)
            {
                for (int i = 0; i < PropsDevPuff.Queue.Count; i++)
                {
                    var r = PropsDevPuff.Queue[i];
                    try
                    {
                        if (r.Clear) DevPuffClear(em);
                        else if (r.List) DevPuffList(em, frame);
                        else DevPuffSpawn(em, r, frame);
                    }
                    catch (Exception e) { PropsDev.Log("puff: failed: " + e.Message); }
                }
                PropsDevPuff.Queue.Clear();
            }
            for (int i = PropsDevPuff.Live.Count - 1; i >= 0; i--)
            {
                var d = PropsDevPuff.Live[i];
                if (!EcsUtil.Alive(em, d.E)) { PropsDevPuff.Live.RemoveAt(i); continue; }
                if (d.ProbeAt != 0 && unchecked((int)(frame - d.ProbeAt)) >= 0)
                {
                    d.ProbeAt = 0;
                    PropsDevPuff.Live[i] = d;
                    string probe = DevPuffProbe(em, d.E, out bool drawing);
                    if (drawing) PropsDevPuff.ProbesDrawing++; else PropsDevPuff.ProbesSilent++;
                    PropsDev.Log("puff: probe " + RRWLog.E(d.E) + " age=" + unchecked(frame - d.Born) + "f " + probe + " seed=" + d.Seed + " phaseErrFrames=" + d.PhaseErr
                                 + (drawing ? " DRAWING" : " SILENT (no enabled effect / zero intensity: check hide=0, Overridden, paused game)"));
                }
                if (unchecked((int)(frame - d.Expire)) >= 0)
                {
                    EcsUtil.MarkDeleted(em, d.E);
                    PropsDevPuff.Deleted++;
                    PropsDevPuff.Live.RemoveAt(i);
                }
            }
        }

        private void DevPuffSpawn(EntityManager em, PuffRequest r, uint frame)
        {
            Entity pe = PropClones.Resolve(m_PrefabSystem, PropClones.DustPuff);
            if (pe == Entity.Null || !em.HasComponent<ObjectData>(pe)) { PropsDev.Log("puff: '" + PrefabNames.DustPuff + "' not registered (" + PropClones.PuffState + ")"); return; }
            if (!RRWPrefabRegistry.DustPuffOk) PropsDev.Log("puff: WARNING DustPuffOk=false (" + PropClones.PuffState + "); spawning anyway");
            var arch = em.GetComponentData<ObjectData>(pe).m_Archetype;
            if (!arch.Valid) { PropsDev.Log("puff: prefab archetype not valid yet"); return; }
            uint dur = em.HasBuffer<EffectAnimation>(pe) && em.GetBuffer<EffectAnimation>(pe, true).Length > 0
                ? math.max(1u, em.GetBuffer<EffectAnimation>(pe, true)[0].m_DurationFrames) : (uint)math.round(RRWConst.kDustPuffAnimSeconds * 60f);
            Entity site = Entity.Null;
            if (r.ProjectId != 0 && SiteRegistry.TryGetProject(r.ProjectId, out var p) && p.Edges.Count > 0) site = p.Edges[0];
            ushort seed = PropsDevPuff.PhaseSeed(frame + 1u, dur, out uint perr);
            if (perr > 2) PropsDevPuff.PhaseMisses++;
            uint life = (uint)math.max(30f, dur * PropsDevPuff.kLifeFrac);
            for (int i = 0; i < r.N; i++)
            {
                float3 pos = r.Pos + new float3((i - (r.N - 1) * 0.5f) * r.Spacing, r.Dy, 0f);
                var arr = em.CreateEntity(arch, 1, Allocator.Temp);
                Entity e = arr[0];
                arr.Dispose();
                em.SetComponentData(e, new PrefabRef(pe));
                em.SetComponentData(e, new ObjTransform(pos, quaternion.identity));
                if (em.HasComponent<PseudoRandomSeed>(e)) em.SetComponentData(e, new PseudoRandomSeed(seed));
                else em.AddComponentData(e, new PseudoRandomSeed(seed));
                if (!em.HasComponent<Created>(e)) em.AddComponent<Created>(e);
                if (!em.HasComponent<Updated>(e)) em.AddComponent<Updated>(e);
                var el = new ObjElevation(0f, (ElevationFlags)0);
                if (em.HasComponent<ObjElevation>(e)) em.SetComponentData(e, el); else em.AddComponentData(e, el);
                if (r.Hide && !em.HasComponent<Game.Tools.Hidden>(e)) em.AddComponent<Game.Tools.Hidden>(e);
                EcsUtil.TagDerived(em, e, site, r.ProjectId, DerivedGroup.DustPuff);
                PropsDevPuff.Live.Add(new DevPuff { E = e, Born = frame, Expire = frame + life, ProbeAt = frame + 20u, PhaseErr = perr, Seed = seed });
                PropsDevPuff.Spawned++;
            }
            PropsDev.Log("puff: spawned " + r.N + " x " + PrefabNames.DustPuff + " at " + RRWLog.F3(r.Pos) + " site=" + RRWLog.E(site) + " hide=" + r.Hide
                         + " animFrames=" + dur + " life=" + life + "f seed=" + seed + " phaseErrFrames=" + perr + " frame=" + frame);
        }

        private void DevPuffClear(EntityManager em)
        {
            int n = 0;
            foreach (var d in PropsDevPuff.Live) if (EcsUtil.Alive(em, d.E)) { EcsUtil.MarkDeleted(em, d.E); n++; }
            PropsDevPuff.Deleted += n;
            PropsDevPuff.Live.Clear();
            PropsDev.Log("puff: cleared " + n);
        }

        private void DevPuffList(EntityManager em, uint frame)
        {
            PropsDev.Log("puff: live=" + PropsDevPuff.Live.Count + " spawned=" + PropsDevPuff.Spawned + " deleted=" + PropsDevPuff.Deleted
                         + " phaseMisses=" + PropsDevPuff.PhaseMisses + " probes drawing=" + PropsDevPuff.ProbesDrawing + " silent=" + PropsDevPuff.ProbesSilent
                         + " | prefab ok=" + RRWPrefabRegistry.DustPuffOk + " " + PropClones.PuffState);
            for (int i = 0; i < PropsDevPuff.Live.Count && i < 16; i++)
            {
                var d = PropsDevPuff.Live[i];
                PropsDev.Log("puff:  " + RRWLog.E(d.E) + " age=" + unchecked(frame - d.Born) + "f expires in " + unchecked((int)(d.Expire - frame)) + "f " + DevPuffProbe(em, d.E, out _));
            }
        }

        // Puff state of a live emitter: enabled effects and the live intensity (Effect x animation curve).
        private string DevPuffProbe(EntityManager em, Entity e, out bool drawing)
        {
            drawing = false;
            if (!EcsUtil.Alive(em, e)) return "gone";
            string s = "hidden=" + (em.HasComponent<Game.Tools.Hidden>(e) ? 1 : 0) + " overridden=" + (em.HasComponent<Overridden>(e) ? 1 : 0)
                       + " owner=" + (em.HasComponent<Owner>(e) ? 1 : 0) + " livePath=" + (em.HasComponent<Game.Routes.LivePath>(e) ? 1 : 0);
            if (em.HasComponent<Game.Rendering.CullingInfo>(e)) s += " cullIdx=" + em.GetComponentData<Game.Rendering.CullingInfo>(e).m_CullingIndex;
            if (!em.HasBuffer<EnabledEffect>(e)) return s + " enabled=n/a";
            var buf = em.GetBuffer<EnabledEffect>(e, true);
            s += " enabled=" + buf.Length;
            if (buf.Length == 0) return s;
            try
            {
                var ecs = World.GetExistingSystemManaged<EffectControlSystem>();
                var data = ecs.GetEnabledData(true, out Unity.Jobs.JobHandle deps);
                deps.Complete();
                int idx = buf[0].m_EnabledIndex;
                if (data.IsCreated && idx >= 0 && idx < data.Length)
                {
                    float li = data[idx].m_Intensity;
                    s += " liveIntensity=" + RRWLog.F(li);
                    drawing = li > 0f;
                }
            }
            catch (Exception ex) { s += " liveIntensity=err:" + ex.GetType().Name; }
            return s;
        }
    }
}
#endif
