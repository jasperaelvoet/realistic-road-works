using System;
using System.Collections.Generic;
using System.Threading;
using Game;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

// MachineBoneSystem (order 700, Rendering after ObjectInterpolateSystem; port of the prototype bone system, verified in game):
// dig / breaker / loader cycles as pure functions of (leg anim, cycle time, machine clock), the excavator's root scale 0.4
// with the tyre fix, and the hydraulic piston aiming after all bone writes. Written every frame in an IJob (vanilla
// InitializeBonesSystem resets rigs on NearCameraUpdated / Updated).
namespace RealisticRoadWorks.V3.Machines
{
    public struct BoneOp
    {
        public Entity Entity;
        public int Sub, Bone, Bone2;
        public byte Kind;          // 0 set rotation, 1 set scale, 2 rolling tyre, 3 steering tyre, 4 piston pair aim
        public quaternion Rot;     // kind 0: local rotation; kind 2/3: rest rotation
        public float3 Scale;
        public float Angle;
        public Entity Mesh;        // kind 4
    }

    // Pose keyframes (degrees). Tunable live with rrw.mx.pose (DEVTOOLS). Signs from the rig (RESEARCH bone list):
    // Body rest is -90 deg about X, so Body Z = slew (+ = towards the machine's right). Arm01 (boom) +X lowers the boom,
    // Arm02 (stick) +X pulls the stick in, Arm02Bucket +X curls the bucket.
    public static class MachinePoses
    {
        // excavator dig cycle: time (s), slew, boom, stick, bucket
        public static readonly float[,] Dig =
        {
            { 0.0f,   0f, -22f,  10f,  40f },   // K0: lifted, bucket empty (cycle start = cycle end)
            { 2.0f,   0f,  14f, -32f, -28f },   // boom down, stick out, bucket open at the face
            { 3.5f,   0f,  10f,  18f,  62f },   // curl: scoop
            { 5.0f,   0f, -24f,  12f,  62f },   // boom up (root stationary until here)
            { 6.5f,   1f, -24f,  12f,  62f },   // slew to the truck (1 = SlewDeg)
            { 7.5f,   1f, -18f, -18f, -38f },   // dump
            { 10.0f,  0f, -22f,  10f,  40f },   // slew back, ready
        };
        public static float SlewSign = 1f;
        public static readonly float[] Rest = { 0f, -30f, 35f, 55f };        // travel / parked: boom up, stick tucked
        // grader (excavator clone in the Loader role) on the 10 s grid; same columns as Dig (slew in units of 90 deg)
        // Spread: back-drag levelling - reach out, bucket flat on the gravel, pull the stick in, lift; the machine steps
        // forward while the arm is carried (MxConst.kGradeHopStart = 6 s).
        public static readonly float[,] Grade =
        {
            { 0.0f,   0f, -12f,  -8f,  12f },   // ready: arm half out, bucket flat and clear
            { 1.6f,   0f,  12f, -34f, -14f },   // reach out, lower the bucket onto the heap / bed
            { 4.2f,   0f,  12f,  20f,   2f },   // drag: stick in with the bucket flat (levels the gravel)
            { 5.4f,   0f,  -6f,  14f,  10f },   // lift clear
            { 7.0f,   0f, -12f,  -8f,  12f },   // carried while stepping forward
            { 10.0f,  0f, -12f,  -8f,  12f },
        };
        // Scrape: topsoil strip - reach, strip back, curl, slew left, cast on the verge side, slew back (step 8.5-10 s).
        public static readonly float[,] Strip =
        {
            { 0.0f,   0f, -12f,  -8f,  12f },   // ready
            { 1.5f,   0f,  12f, -34f, -22f },   // reach out, bucket teeth down
            { 3.4f,   0f,  12f,  16f,  10f },   // strip towards the machine
            { 4.2f,   0f,   8f,  18f,  58f },   // curl: bucket full
            { 5.0f,   0f, -18f,  12f,  58f },   // lift
            { 6.2f, -0.7f, -18f,  6f,  58f },   // slew to the left (verge side)
            { 7.0f, -0.7f, -12f, -14f, -34f },  // cast
            { 8.4f,   0f, -12f,  -8f,  12f },   // slew back, ready
            { 10.0f,  0f, -12f,  -8f,  12f },
        };
        public static readonly float[] BreakBase = { 0f, 10f, -8f, -12f };  // breaker on the road surface
        public static float BreakStick = 12f, BreakBucket = 20f, BreakPeriod = 3f;
        // loader: ArmRotation (+ = lower), Claw (+ = tilt down)
        public static float LoaderRestArm = -8f, LoaderRestClaw = -15f;
        public static float LoaderLowArm = 12f, LoaderLowClaw = 6f;
        public static float LoaderHighArm = -25f, LoaderHighClaw = -20f;
        public static float LoaderSign = 1f;

        static float S(float x) => MachineMotion.Smooth(x);

        // Excavator angles (slew, boom, stick, bucket) for an anim at cycle time t.
        public static float4 Excavator(byte anim, float t, float slewDeg, float param)
        {
            switch ((AnimKind)anim)
            {
                case AnimKind.Dig: return DigAt(t, slewDeg);
                case AnimKind.Spread: return KeyAt(Grade, t, 90f);
                case AnimKind.Scrape: return KeyAt(Strip, t, 90f);
                case AnimKind.Break:
                {
                    // the hop may start earlier than kHopStart (window grown for a realistic hop speed): param = hop start
                    float hs = param >= 1f && param <= 10f ? param : MxConst.kHopStart;
                    if (t < hs)
                    {
                        float ph = 2f * math.PI * t / math.max(0.5f, BreakPeriod);
                        float w = S(t / 0.8f) * S((hs - t) / 0.8f);
                        return new float4(BreakBase[0], BreakBase[1], BreakBase[2] + BreakStick * math.sin(ph) * w, BreakBase[3] + BreakBucket * math.sin(ph + 1.5708f) * w);
                    }
                    // hop: lift slightly
                    float h = S((t - hs) / 1.2f) * S((10f - t) / 1.2f);
                    return new float4(BreakBase[0], BreakBase[1] - 14f * h, BreakBase[2], BreakBase[3]);
                }
                default: return new float4(Rest[0], Rest[1], Rest[2], Rest[3]);
            }
        }

        public static float4 KeyAt(float[,] keys, float t, float slewDeg)
        {
            int n = keys.GetLength(0);
            t = math.clamp(t, 0f, keys[n - 1, 0]);
            for (int i = 0; i + 1 < n; i++)
            {
                float t0 = keys[i, 0], t1 = keys[i + 1, 0];
                if (t > t1) continue;
                float k = S((t - t0) / math.max(0.01f, t1 - t0));
                float4 a = new float4(keys[i, 1] * slewDeg * SlewSign, keys[i, 2], keys[i, 3], keys[i, 4]);
                float4 b = new float4(keys[i + 1, 1] * slewDeg * SlewSign, keys[i + 1, 2], keys[i + 1, 3], keys[i + 1, 4]);
                return math.lerp(a, b, k);
            }
            return new float4(keys[0, 1] * slewDeg * SlewSign, keys[0, 2], keys[0, 3], keys[0, 4]);
        }

        public static float4 DigAt(float t, float slewDeg)
        {
            int n = Dig.GetLength(0);
            t = math.clamp(t, 0f, Dig[n - 1, 0]);
            for (int i = 0; i + 1 < n; i++)
            {
                float t0 = Dig[i, 0], t1 = Dig[i + 1, 0];
                if (t > t1) continue;
                float k = S((t - t0) / math.max(0.01f, t1 - t0));
                float4 a = new float4(Dig[i, 1] * slewDeg * SlewSign, Dig[i, 2], Dig[i, 3], Dig[i, 4]);
                float4 b = new float4(Dig[i + 1, 1] * slewDeg * SlewSign, Dig[i + 1, 2], Dig[i + 1, 3], Dig[i + 1, 4]);
                return math.lerp(a, b, k);
            }
            return new float4(0f, Dig[0, 2], Dig[0, 3], Dig[0, 4]);
        }

        // Canonical pose of an anim at a leg boundary (blend source).
        public static float4 ExcavatorCanonical(byte anim, float slewDeg)
        {
            switch ((AnimKind)anim)
            {
                case AnimKind.Dig: return DigAt(0f, slewDeg);
                case AnimKind.Spread: return KeyAt(Grade, 0f, 90f);
                case AnimKind.Scrape: return KeyAt(Strip, 0f, 90f);
                case AnimKind.Break: return new float4(BreakBase[0], BreakBase[1], BreakBase[2], BreakBase[3]);
                default: return new float4(Rest[0], Rest[1], Rest[2], Rest[3]);
            }
        }

        // Loader (arm, claw).
        public static float2 Loader(byte anim, float t, float param)
        {
            switch ((AnimKind)anim)
            {
                case AnimKind.Spread: return math.lerp(new float2(LoaderLowArm, LoaderLowClaw), new float2(LoaderHighArm, LoaderHighClaw), math.saturate(param));
                case AnimKind.Scrape: return new float2(LoaderLowArm, LoaderLowClaw);
                case AnimKind.Dig:
                {
                    // fallback digger: bucket down - scoop - up - tip, on the same 10 s grid
                    float c = t % 10f;
                    if (c < 2f) return math.lerp(new float2(LoaderRestArm, LoaderRestClaw), new float2(LoaderLowArm, LoaderLowClaw), S(c / 2f));
                    if (c < 3.5f) return math.lerp(new float2(LoaderLowArm, LoaderLowClaw), new float2(LoaderLowArm, -25f), S((c - 2f) / 1.5f));
                    if (c < 6f) return math.lerp(new float2(LoaderLowArm, -25f), new float2(LoaderHighArm, -25f), S((c - 3.5f) / 2.5f));
                    if (c < 7.5f) return math.lerp(new float2(LoaderHighArm, -25f), new float2(LoaderHighArm, 25f), S((c - 6f) / 1.5f));
                    return math.lerp(new float2(LoaderHighArm, 25f), new float2(LoaderRestArm, LoaderRestClaw), S((c - 7.5f) / 2.5f));
                }
                default: return new float2(LoaderRestArm, LoaderRestClaw);
            }
        }

        public static float2 LoaderCanonical(byte anim)
        {
            switch ((AnimKind)anim)
            {
                case AnimKind.Spread:
                case AnimKind.Scrape: return new float2(LoaderLowArm, LoaderLowClaw);
                default: return new float2(LoaderRestArm, LoaderRestClaw);
            }
        }
    }

    [RegisterSystem(SystemUpdatePhase.Rendering, After = typeof(ObjectInterpolateSystem), Order = RRWOrder.MachineBones)]
    public partial class MachineBoneSystem : GameSystemBase
    {
        public static int JobErrors;
        public static bool Disabled;

        private struct BoneJob : IJob
        {
            public BufferLookup<Skeleton> Skeletons;
            public BufferLookup<Bone> Bones;
            [ReadOnly] public BufferLookup<ProceduralBone> ProcBones;
            [ReadOnly] public NativeArray<BoneOp> Ops;

            public void Execute()
            {
                try
                {
                    for (int n = 0; n < Ops.Length; n++)
                    {
                        var op = Ops[n];
                        if (!Skeletons.HasBuffer(op.Entity) || !Bones.HasBuffer(op.Entity)) continue;
                        var skels = Skeletons[op.Entity];
                        if (op.Sub < 0 || op.Sub >= skels.Length) continue;
                        var sk = skels[op.Sub];
                        if (sk.m_BoneOffset < 0 || sk.m_BufferAllocation.Empty) continue;
                        var bones = Bones[op.Entity];
                        if (op.Kind == 4)
                        {
                            if (Aim(op, bones, sk.m_BoneOffset)) { sk.m_CurrentUpdated = true; skels[op.Sub] = sk; }
                            continue;
                        }
                        int idx = sk.m_BoneOffset + op.Bone;
                        if (op.Bone < 0 || idx >= bones.Length) continue;
                        var b = bones[idx];
                        switch (op.Kind)
                        {
                            case 0: b.m_Rotation = op.Rot; break;
                            case 1: b.m_Scale = op.Scale; break;
                            case 2: b.m_Rotation = math.mul(op.Rot, quaternion.RotateX(op.Angle)); break;   // vanilla RollingTire
                            case 3:
                            {
                                // vanilla SteeringTire: rest * RotateY(steer) * RotateX(roll); keep vanilla's steer
                                float3 right = math.rotate(math.mul(math.inverse(op.Rot), b.m_Rotation), new float3(1f, 0f, 0f));
                                float steer = math.atan2(-right.z, right.x);
                                b.m_Rotation = math.mul(op.Rot, math.mul(quaternion.RotateY(steer), quaternion.RotateX(op.Angle)));
                                break;
                            }
                        }
                        bones[idx] = b;
                        sk.m_CurrentUpdated = true;
                        skels[op.Sub] = sk;
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref JobErrors);
                }
            }

            // Piston aiming (verified in game): rotate both piston bones so each points at its partner's current anchor.
            // Each starts from its CARRIED orientation (its rest local rotation under its parent's current pose, so the slew and the
            // boom roll it along) and gets the minimal rotation from there: no twist about the cylinder axis (an earlier rest-frame aim
            // twisted it by up to the slew angle), the eyes stay on their pins.
            private bool Aim(in BoneOp op, DynamicBuffer<Bone> bones, int off)
            {
                if (!ProcBones.HasBuffer(op.Mesh)) return false;
                var pb = ProcBones[op.Mesh];
                int a = op.Bone, b = op.Bone2;
                if (a < 0 || b < 0 || a >= pb.Length || b >= pb.Length || off + pb.Length > bones.Length) return false;
                float4x4 ra = RestMat(pb, a), rb = RestMat(pb, b);
                float3 r = rb.c3.xyz - ra.c3.xyz;
                float3 c = ObjMat(pb, bones, off, b).c3.xyz - ObjMat(pb, bones, off, a).c3.xyz;
                if (math.lengthsq(r) < 1e-8f || math.lengthsq(c) < 1e-8f) return false;
                quaternion carA = Carried(pb, bones, off, a), carB = Carried(pb, bones, off, b);
                float3 la = math.rotate(math.inverse(RotOf(ra)), r), lb = math.rotate(math.inverse(RotOf(rb)), -r);   // partner dir, own rest frame
                float3 da = math.rotate(carA, la), db = math.rotate(carB, lb);
                if (math.lengthsq(da) < 1e-8f || math.lengthsq(db) < 1e-8f) return false;
                float3 cn = math.normalize(c);
                SetObjectRotation(pb, bones, off, a, math.mul(FromTo(math.normalize(da), cn), carA));
                SetObjectRotation(pb, bones, off, b, math.mul(FromTo(math.normalize(db), -cn), carB));
                return true;
            }

            // Object rotation bone i would have with its rest LOCAL rotation under its parent's CURRENT pose.
            static quaternion Carried(DynamicBuffer<ProceduralBone> pb, DynamicBuffer<Bone> bones, int off, int i)
            {
                int par = pb[i].m_ParentIndex;
                quaternion parRot = par >= 0 ? RotOf(ObjMat(pb, bones, off, par)) : quaternion.identity;
                return math.normalize(math.mul(parRot, pb[i].m_Rotation));
            }

            static void SetObjectRotation(DynamicBuffer<ProceduralBone> pb, DynamicBuffer<Bone> bones, int off, int i, quaternion objRot)
            {
                int par = pb[i].m_ParentIndex;
                quaternion parRot = par >= 0 ? RotOf(ObjMat(pb, bones, off, par)) : quaternion.identity;
                var bone = bones[off + i];
                bone.m_Rotation = math.normalize(math.mul(math.inverse(parRot), objRot));
                bones[off + i] = bone;
            }

            static float4x4 ObjMat(DynamicBuffer<ProceduralBone> pb, DynamicBuffer<Bone> bones, int off, int i)
            {
                float4x4 m = float4x4.identity;
                int guard = 0;
                while (i >= 0 && i < pb.Length && guard++ < 64)
                {
                    var b = bones[off + i];
                    m = math.mul(float4x4.TRS(b.m_Position, b.m_Rotation, b.m_Scale), m);
                    i = pb[i].m_ParentIndex;
                }
                return m;
            }

            static float4x4 RestMat(DynamicBuffer<ProceduralBone> pb, int i)
            {
                float4x4 m = float4x4.identity;
                int guard = 0;
                while (i >= 0 && i < pb.Length && guard++ < 64)
                {
                    var b = pb[i];
                    m = math.mul(float4x4.TRS(b.m_Position, b.m_Rotation, b.m_Scale), m);
                    i = b.m_ParentIndex;
                }
                return m;
            }

            static quaternion RotOf(float4x4 m)
            {
                float3 c0 = m.c0.xyz, c1 = m.c1.xyz, c2 = m.c2.xyz;
                float l0 = math.length(c0), l1 = math.length(c1), l2 = math.length(c2);
                if (l0 < 1e-6f || l1 < 1e-6f || l2 < 1e-6f) return quaternion.identity;
                return math.normalize(new quaternion(new float3x3(c0 / l0, c1 / l1, c2 / l2)));
            }

            static quaternion FromTo(float3 a, float3 b)
            {
                float d = math.dot(a, b);
                if (d < -0.99999f)
                {
                    float3 axis = math.cross(a, math.abs(a.x) < 0.9f ? new float3(1f, 0f, 0f) : new float3(0f, 1f, 0f));
                    return quaternion.AxisAngle(math.normalize(axis), math.PI);
                }
                return math.normalize(new quaternion(new float4(math.cross(a, b), 1f + d)));
            }
        }

        private RenderingSystem m_Rendering;
        private PrefabSystem m_PrefabSystem;
        private BufferLookup<Skeleton> m_Skeletons;
        private BufferLookup<Bone> m_Bones;
        private BufferLookup<ProceduralBone> m_ProcBones;
        private readonly List<BoneOp> m_Ops = new List<BoneOp>(256);
        private readonly Dictionary<Entity, RigMap> m_Maps = new Dictionary<Entity, RigMap>();
        private int m_Reported;

        // Resolved bone indices of a prefab.
        private sealed class RigMap
        {
            public Rig Rig;
            public int BodySub = -1, Body = -1, BoomSub = -1, Boom = -1, StickSub = -1, Stick = -1, BucketSub = -1, Bucket = -1;
            public int ArmSub = -1, Arm = -1, ClawSub = -1, Claw = -1;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Rendering = World.GetOrCreateSystemManaged<RenderingSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Skeletons = CheckedStateRef.GetBufferLookup<Skeleton>(false);
            m_Bones = CheckedStateRef.GetBufferLookup<Bone>(false);
            m_ProcBones = CheckedStateRef.GetBufferLookup<ProceduralBone>(true);
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Maps.Clear();
            DigArms.Clear();
            JobErrors = 0;
            m_Reported = 0;
            Disabled = false;
        }

        protected override void OnUpdate()
        {
            if (Disabled || MachineRegistry.All.Count == 0) return;
            if (GameManager.instance == null || GameManager.instance.gameMode != GameMode.Game) return;
            if (JobErrors != m_Reported)
            {
                m_Reported = JobErrors;
                RRWLog.Warn("machines: bone job caught exception(s): total=" + JobErrors + (JobErrors >= 10 ? " -> bones disabled until the next load" : ""));
                if (JobErrors >= 10) { Disabled = true; return; }
            }
            if (MachineDebug.Trace != null)
            {
                try { MachineDebug.Trace(EntityManager); } catch (Exception e) { RRWLog.ErrorOnce("machines trace", e); MachineDebug.Trace = null; }
            }
            long t0 = RRWPerf.Start();
            try
            {
                BuildOps();
                if (m_Ops.Count == 0) return;
                var ops = new NativeArray<BoneOp>(m_Ops.Count, Allocator.TempJob);
                for (int i = 0; i < m_Ops.Count; i++) ops[i] = m_Ops[i];
                m_Skeletons.Update(ref CheckedStateRef);
                m_Bones.Update(ref CheckedStateRef);
                m_ProcBones.Update(ref CheckedStateRef);
                var job = new BoneJob { Skeletons = m_Skeletons, Bones = m_Bones, ProcBones = m_ProcBones, Ops = ops };
                JobHandle h = job.Schedule(Dependency);
                ops.Dispose(h);
                Dependency = h;
            }
            catch (Exception e)
            {
                RRWLog.ErrorOnce("machines bones", e);
                if (++JobErrors >= 10) Disabled = true;
            }
            finally { RRWPerf.Stop(PerfSlot.MachineJobs, t0); }
        }

        private RigMap Map(Puppet p)
        {
            if (m_Maps.TryGetValue(p.Prefab, out var m)) return m;
            m = new RigMap { Rig = Rigs.Get(EntityManager, m_PrefabSystem, p.Prefab) };
            var r = m.Rig;
            r.Find("Body", out m.BodySub, out m.Body);
            r.Find("Arm01", out m.BoomSub, out m.Boom);
            r.Find("Arm02", out m.StickSub, out m.Stick);
            r.Find("Arm02Bucket", out m.BucketSub, out m.Bucket);
            r.Find("ArmRotation", out m.ArmSub, out m.Arm);
            r.Find("Claw", out m.ClawSub, out m.Claw);
            m_Maps[p.Prefab] = m;
            return m;
        }

        private void BuildOps()
        {
            m_Ops.Clear();
            var settings = RRWSettings.Current;
            bool bonesOn = settings != null && settings.BonesOn;
            bool detailed = settings != null && settings.DetailedDiggingOn;
            uint frame = m_Rendering.frameIndex;
            float ft = m_Rendering.frameTime;
            var clk = MachineRegistry.Clock;
            double tau = clk.Tau(frame, ft);
            var tv = MachineTrackStore.View();
            foreach (var p in MachineRegistry.All)
            {
                if (p.IsRoller || p.E == Entity.Null || p.Plan.Count <= 0) continue;
                if (!p.ExcavatorRig && p.Kind != MachineKind.Loader && p.Kind != MachineKind.LoaderDigger) continue;
                var map = Map(p);
                if (!map.Rig.Any) continue;
                MachineMotion.State(p.Plan, clk, tau, out var s);
                if (p.ExcavatorRig)
                {
                    // root scale 0.4 + tyre roll fix: always, the rig is 34 m long unscaled
                    foreach (var sub in map.Rig.Subs)
                    {
                        for (int i = 0; i < sub.Names.Length; i++)
                        {
                            if (sub.Parent[i] < 0)
                                m_Ops.Add(new BoneOp { Entity = p.E, Sub = sub.Sub, Bone = i, Kind = 1, Scale = sub.RestScale[i] * p.Scale });
                            if (sub.Type[i] == BoneType.RollingTire || sub.Type[i] == BoneType.SteeringTire)
                            {
                                float radius = math.max(0.01f, sub.ObjPos[i].y);
                                double ang = s.Odo / (p.Scale * radius);
                                ang %= 2.0 * math.PI;
                                m_Ops.Add(new BoneOp { Entity = p.E, Sub = sub.Sub, Bone = i, Kind = (byte)(sub.Type[i] == BoneType.RollingTire ? 2 : 3), Rot = sub.Rest[i], Angle = (float)ang });
                            }
                        }
                    }
                    if (!bonesOn) continue;
                    float slew = p.Plan.SlewDeg > 0f ? p.Plan.SlewDeg : 90f;
                    // the IK cycle (digger: the IK DigHop leg's keys; grader: bucket placement) or the fixed keyframe cycle
                    float4 cur;
                    bool ik = false;
                    try { ik = IkPose(p, s, tau, tv, detailed, out cur); }
                    catch (Exception e)
                    {
                        RRWLog.ErrorOnce("machines ik pose", e);
                        ik = false;
                        cur = default;
                    }
                    if (!ik) cur = MachinePoses.Excavator(s.Anim, s.AnimTime, slew, s.AnimParam);
                    if (s.Blend < 1f) cur = math.lerp(Canonical(p, s.FromAnim, slew), cur, s.Blend);
                    if (p.Dig != null)
                    {
                        p.Dig.UsedIk = ik;
                        if (ik) { p.Dig.ReadyPoseLast = cur; p.Dig.LastPoseValid = true; }
                    }
                    AddRot(p, map.Rig, map.BodySub, map.Body, 2, cur.x);
                    AddRot(p, map.Rig, map.BoomSub, map.Boom, 0, cur.y);
                    AddRot(p, map.Rig, map.StickSub, map.Stick, 0, cur.z);
                    AddRot(p, map.Rig, map.BucketSub, map.Bucket, 0, cur.w);
                }
                else
                {
                    if (!bonesOn) continue;
                    float2 cur = MachinePoses.Loader(s.Anim, s.AnimTime, s.AnimParam);
                    if (s.Blend < 1f) cur = math.lerp(MachinePoses.LoaderCanonical(s.FromAnim), cur, s.Blend);
                    AddRot(p, map.Rig, map.ArmSub, map.Arm, 0, cur.x * MachinePoses.LoaderSign);
                    AddRot(p, map.Rig, map.ClawSub, map.Claw, 0, cur.y * MachinePoses.LoaderSign);
                }
                // pistons always aimed (every rig puppet, both modes; carried-orientation aim in the job)
                foreach (var pp in map.Rig.Pistons)
                    m_Ops.Add(new BoneOp { Entity = p.E, Sub = pp.Sub, Bone = pp.A, Bone2 = pp.B, Kind = 4, Mesh = pp.Mesh });
            }
        }

        // Blend source at a leg boundary: the keyframe canonical pose, or - for a rig whose last frames were IK - the last IK pose (the
        // dig cycle ends at Ready, the grader at its bucket placement), so leaving / entering the IK cycle never pops.
        private static float4 Canonical(Puppet p, byte fromAnim, float slew)
        {
            var a = (AnimKind)fromAnim;
            if (p.Dig != null && p.Dig.HaveLast && (a == AnimKind.Dig || a == AnimKind.Break || a == AnimKind.Spread || a == AnimKind.Scrape))
                return p.Dig.ReadyPoseLast;
            return MachinePoses.ExcavatorCanonical(fromAnim, slew);
        }

        // The IK pose of an excavator rig puppet this frame. Digger: inside an IK DigHop leg
        // (P0 = kDigCyclePeriod: the planner chose the IK schedule), the keys the Director built for this cycle. Grader: bucket
        // placement while spreading / scraping (Detailed digging on). False = the fixed keyframe cycle.
        private bool IkPose(Puppet p, in ChainState s, double tau, in TrackView tv, bool detailed, out float4 pose)
        {
            pose = default;
            var a = (AnimKind)s.Anim;
            if (p.Kind == MachineKind.Excavator)
            {
                if (s.LegKind != (byte)LegKind.DigHop || (a != AnimKind.Dig && a != AnimKind.Break)) return false;
                var leg = p.Plan.Get(s.Leg);
                if (math.abs(leg.P0 - RRWConst.kDigCyclePeriod) > 0.01f) return false;
                var d = p.Dig;
                if (d == null || d.Arm == null || d.Cur == null) return false;
                double cs = tau - s.AnimTime;
                var keys = d.Next != null && math.abs(d.Next.CycleStart - cs) <= 0.05 ? d.Next : d.Cur;
                float t = s.AnimTime;
                MxPerf.Begin(MxT.B_Ik);
                var tg = keys.Eval(t);
                var res = d.Arm.SolveArm(tg.Slew, tg.R, tg.H, tg.Att, keys.FloorH);
                MxPerf.End(MxT.B_Ik);
                MxPerf.Count(MxC.IkSolves);
                if (res.Refined) MxPerf.Count(MxC.IkRefines);
                pose = res.Pose.F4;
#if DEVTOOLS
                if (MachineDebug.DigTrace == p && tau <= MachineDebug.DigTraceUntil)
                    RRWLog.Info("dev rrw mx dig trace " + p + " t=" + RRWLog.F(t) + " seg=" + tg.SegIndex + " target(slew " + RRWLog.F(tg.Slew) + " r " + RRWLog.F(tg.R) +
                                " h " + RRWLog.F(tg.H) + " att " + RRWLog.F(tg.Att) + ") tip h=" + RRWLog.F(math.dot(res.TipObj, d.Arm.S)) + " floorH=" + RRWLog.F(keys.FloorH) +
                                " err=" + RRWLog.F(res.Err) + " limits=" + res.LimitsText() + (res.FloorRaised ? " floorRaised" : "") + " " + res.Pose);
#endif
                d.LastTarget = tg;
                d.LastRes = res;
                d.LastCycleT = t;
                d.LastErr = res.Err;
                d.Frames++;
                if (!res.Unreachable) d.MaxErr = math.max(d.MaxErr, res.Err);
                float tipH = math.dot(res.TipObj, d.Arm.S);
                d.TipBelowFloor = math.max(d.TipBelowFloor, keys.FloorH - tipH);   // > 0: the tip under the floor guard (floor - bite)
#if DEVTOOLS
                // world-space check of the drag - the solved tip's world Y against the track floor under the tip
                // (minus the bite, plus the scrape clearance); independent of keys.FloorH, so a frame error there shows up
                if (keys.Mode == DigMode.Dig && tg.SegIndex == 3 && tv.IsCreated && MachineMotion.Pose(p.Plan, tv, s, out float3 wr, out quaternion wq))
                {
                    float3 tipW = wr + math.rotate(wq, res.TipObj);
                    float2 hh = math.normalizesafe(new float2(s.Hu, s.Hl), new float2(1f, 0f));
                    float3 o = res.TipObj;
                    if (tv.Height(p.Plan.Track, s.U + hh.x * o.z - hh.y * o.x, s.Lat + hh.y * o.z + hh.x * o.x, out float fyw))
                        d.WorldFloorGap = math.max(d.WorldFloorGap, math.abs(tipW.y - (fyw - keys.Bite + keys.Clear)));
                }
#endif
#if DEVTOOLS
                if (keys.HasBox && tg.SegIndex >= 5 && tg.SegIndex <= 9)
                {
                    int n = d.Arm.BucketPoints(res.Pose.F4, s_Pts);
                    for (int i = 0; i < n; i++) d.TruckClearMin = math.min(d.TruckClearMin, keys.Box.Sdf(s_Pts[i]));
                }
#endif
                return true;
            }
            if (p.Kind == MachineKind.Grader)
            {
                if (!detailed || (a != AnimKind.Spread && a != AnimKind.Scrape)) return false;
                if (p.Dig == null) p.Dig = new DigState();
                var d = p.Dig;
                if (d.Arm == null) d.Arm = DigArms.Get(EntityManager, m_PrefabSystem, p.Prefab, p.Scale);
                if (d.Arm == null || !tv.IsCreated) return false;
                if (!MachineMotion.Pose(p.Plan, tv, s, out float3 root, out quaternion rot)) return false;
                MxPerf.Begin(MxT.B_Ik);
                // bucket tip at slew 0, kGraderBucketReach ahead of the swing axis; the floor from the track at the tip's chain u / lateral.
                // Its height in the arm's h axis (object space: the rig pitches with the floor), not a world-Y difference.
                float r = RRWConst.kGraderBucketReach;
                float z = d.Arm.ArmToObj(0f, r, 0f).z;
                float2 h = math.normalizesafe(new float2(s.Hu, s.Hl), new float2(1f, 0f));
                float floorH = DigFloor.World(tv, p.Plan.Track, s.U + h.x * z, s.Lat + h.y * z, out float3 fw) ? DigFloor.ObjH(fw, root, rot, d.Arm.S) : 0f;
                // skim while working forward / holding in a stroke / crawling; lifted while reversing or repositioning fast
                float vw = math.max(0.05f, p.Lim.VWork);
                bool lift = s.Speed < -0.05f || math.abs(s.Speed) > 1.5f * vw;
                float target = lift ? 1f : 0f;
                if (p.GraderLiftW < 0f || double.IsNaN(p.GraderLiftTau)) p.GraderLiftW = target;
                else
                {
                    float dt = (float)math.clamp(tau - p.GraderLiftTau, 0.0, 1.0);
                    float step = dt / math.max(0.05f, RRWConst.kGraderBlendSeconds);
                    p.GraderLiftW = p.GraderLiftW < target ? math.min(target, p.GraderLiftW + step) : math.max(target, p.GraderLiftW - step);
                }
                p.GraderLiftTau = tau;
                float w = MachineMotion.Smooth(p.GraderLiftW);
                float hTip = floorH + math.lerp(RRWConst.kGraderSkim, RRWConst.kGraderLift, w);
                var res = d.Arm.SolveArm(0f, r, hTip, RRWConst.kGraderAttitude, floorH);
                MxPerf.End(MxT.B_Ik);
                MxPerf.Count(MxC.IkSolves);
                if (res.Refined) MxPerf.Count(MxC.IkRefines);
                pose = res.Pose.F4;
                d.LastRes = res;
                d.LastErr = res.Err;
                d.LastFloorH = floorH;
                d.Frames++;
                if (!res.Unreachable) d.MaxErr = math.max(d.MaxErr, res.Err);
                d.TipBelowFloor = math.max(d.TipBelowFloor, floorH - math.dot(res.TipObj, d.Arm.S));
                return true;
            }
            return false;
        }

        private static readonly float3[] s_Pts = new float3[4];

        private void AddRot(Puppet p, Rig rig, int sub, int bone, int axis, float deg)
        {
            if (sub < 0 || bone < 0) return;
            var rs = rig.GetSub(sub);
            if (rs == null || bone >= rs.Rest.Length) return;
            float rad = math.radians(deg);
            quaternion r = axis == 0 ? quaternion.RotateX(rad) : axis == 1 ? quaternion.RotateY(rad) : quaternion.RotateZ(rad);
            m_Ops.Add(new BoneOp { Entity = p.E, Sub = sub, Bone = bone, Kind = 0, Rot = math.mul(rs.Rest[bone], r) });
        }
    }
}
