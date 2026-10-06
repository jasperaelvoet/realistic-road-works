using System;
using System.Threading;
using Game;
using Game.Common;
using Game.Objects;
using Game.Rendering;
using Game.SceneFlow;
using Game.Simulation;
using RealisticRoadWorks.Dev;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using ObjTransform = Game.Objects.Transform;

// MachineMoverSystem (order 600, PreCulling before PreCullingSystem; port of the prototype mover, verified in game):
// writes all 4 TransformFrame slots at n = b + 16k (k = -1..2) plus Transform and Moving from the machine clock, so
// vanilla interpolation, tyres, sway, lights and engine audio follow real motion. Pure function of (MachinePlan, clock,
// track): freezes when paused, scales with game speed and RRWDebug.MachineClockScale, never drifts.
namespace RealisticRoadWorks.V3.Machines
{
    [RegisterSystem(SystemUpdatePhase.PreCulling, Before = typeof(PreCullingSystem), Order = RRWOrder.MachineMover)]
    public partial class MachineMoverSystem : GameSystemBase
    {
        public static int JobErrors;
        public static bool Disabled;

        private struct MoveJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<MachinePlan> PlanType;
            public ComponentTypeHandle<ObjTransform> TransformType;
            public ComponentTypeHandle<Moving> MovingType;
            public BufferTypeHandle<TransformFrame> FrameType;
            [ReadOnly] public SharedComponentTypeHandle<UpdateFrame> UpdateFrameType;
            [ReadOnly] public NativeArray<TrackSample> Samples;
            [ReadOnly] public NativeArray<TrackHeader> Headers;
            public ClockData Clock;
            public uint Frame;
            public float FrameTime;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                try
                {
                    var tv = new TrackView { Samples = Samples, Headers = Headers };
                    uint u = chunk.GetSharedComponent(UpdateFrameType).m_Index;
                    var plans = chunk.GetNativeArray(ref PlanType);
                    var transforms = chunk.GetNativeArray(ref TransformType);
                    var movings = chunk.GetNativeArray(ref MovingType);
                    var frameAcc = chunk.GetBufferAccessor(ref FrameType);
                    int count = chunk.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var p = plans[i];
                        if (p.Count <= 0 || p.Track < 0) continue;
                        var frames = frameAcc[i];
                        if (frames.Length != 4) continue;   // UpdateGroupSystem has not initialised the slots yet
                        uint num = unchecked(Frame - u - 32u);
                        uint b = num & ~15u;
                        bool ok = true;
                        for (int k = -1; k <= 2 && ok; k++)
                        {
                            uint n = unchecked(b + (uint)(16 * k));
                            uint tN = unchecked(n + u + 32u);
                            if (!MachineMotion.Sample(p, tv, Clock, tN, 0f, out float3 pos, out quaternion rot, out float3 vel, out TransformFlags flags)) { ok = false; break; }
                            var tf = frames[(int)((n >> 4) & 3u)];
                            tf.m_Position = pos;
                            tf.m_Velocity = vel;
                            tf.m_Rotation = rot;
                            tf.m_Flags = flags;
                            frames[(int)((n >> 4) & 3u)] = tf;
                        }
                        if (!ok) continue;
                        if (MachineMotion.Sample(p, tv, Clock, Frame, FrameTime, out float3 pNow, out quaternion rNow, out float3 vNow, out _))
                        {
                            transforms[i] = new ObjTransform(pNow, rNow);
                            var mv = movings[i];
                            mv.m_Velocity = vNow;
                            mv.m_AngularVelocity = float3.zero;
                            movings[i] = mv;
                        }
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref JobErrors);
                }
            }
        }

        private EntityQuery m_Query;
        private RenderingSystem m_Rendering;
        private ComponentTypeHandle<MachinePlan> m_PlanType;
        private ComponentTypeHandle<ObjTransform> m_TransformType;
        private ComponentTypeHandle<Moving> m_MovingType;
        private BufferTypeHandle<TransformFrame> m_FrameType;
        private SharedComponentTypeHandle<UpdateFrame> m_UpdateFrameType;
        private int m_Reported;
        private bool m_MainThread;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Rendering = World.GetOrCreateSystemManaged<RenderingSystem>();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<MachinePlan>(), ComponentType.ReadWrite<TransformFrame>(), ComponentType.ReadWrite<ObjTransform>(),
                    ComponentType.ReadWrite<Moving>(), ComponentType.ReadOnly<UpdateFrame>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            RequireForUpdate(m_Query);
            m_PlanType = CheckedStateRef.GetComponentTypeHandle<MachinePlan>(true);
            m_TransformType = CheckedStateRef.GetComponentTypeHandle<ObjTransform>(false);
            m_MovingType = CheckedStateRef.GetComponentTypeHandle<Moving>(false);
            m_FrameType = CheckedStateRef.GetBufferTypeHandle<TransformFrame>(false);
            m_UpdateFrameType = CheckedStateRef.GetSharedComponentTypeHandle<UpdateFrame>();
        }

        protected override void OnUpdate()
        {
            if (Disabled) return;
            if (GameManager.instance == null || GameManager.instance.gameMode != GameMode.Game) return;
            if (JobErrors != m_Reported)
            {
                m_Reported = JobErrors;
                RRWLog.Warn("machines: mover job caught exception(s): total=" + JobErrors + (JobErrors >= 10 ? " -> mover disabled until the next load" : ""));
                if (JobErrors >= 10) { Disabled = true; return; }
            }
            long t0 = RRWPerf.Start();
            try
            {
                var tv = MachineTrackStore.View();
                if (!tv.IsCreated) return;
                m_PlanType.Update(ref CheckedStateRef);
                m_TransformType.Update(ref CheckedStateRef);
                m_MovingType.Update(ref CheckedStateRef);
                m_FrameType.Update(ref CheckedStateRef);
                m_UpdateFrameType.Update(ref CheckedStateRef);
                var job = new MoveJob
                {
                    PlanType = m_PlanType,
                    TransformType = m_TransformType,
                    MovingType = m_MovingType,
                    FrameType = m_FrameType,
                    UpdateFrameType = m_UpdateFrameType,
                    Samples = tv.Samples,
                    Headers = tv.Headers,
                    Clock = MachineRegistry.Clock,
                    Frame = m_Rendering.frameIndex,
                    FrameTime = m_Rendering.frameTime,
                };
                if (!m_MainThread)
                {
                    try
                    {
                        JobHandle h = JobChunkExtensions.Schedule(job, m_Query, Dependency);
                        Dependency = h;
                        MachineTrackStore.Readers = JobHandle.CombineDependencies(MachineTrackStore.Readers, h);
                        return;
                    }
                    catch (Exception e)
                    {
                        m_MainThread = true;
                        RRWLog.Warn("machines: mover scheduling failed, running on the main thread: " + e.Message);
                    }
                }
                Dependency.Complete();
                JobChunkExtensions.Run(job, m_Query);
            }
            catch (Exception e)
            {
                RRWLog.ErrorOnce("machines mover", e);
                if (++JobErrors >= 10) Disabled = true;
            }
            finally { RRWPerf.Stop(PerfSlot.MachineJobs, t0); }
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            JobErrors = 0;
            m_Reported = 0;
            Disabled = false;
        }
    }
}
