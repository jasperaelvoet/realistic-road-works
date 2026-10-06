#if DEVTOOLS
using System;
using System.Collections.Generic;
using Game;
using Game.Rendering;
using Game.SceneFlow;
using RealisticRoadWorks.Dev;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.DevCmds
{
    // State of the video-capture camera (rrw.cam / rrw.focus). Written by the commands (MainLoop), read by DevCameraSystem.
    internal static class DevCam
    {
        public const float kAutoYawOffset = 35f;   // degrees off the perpendicular

        public static bool Active;           // DevCameraSystem drives the camera
        public static bool Follow;           // false = one-shot framing, then release
        public static bool Snap;             // next update jumps straight to the target (start of a shot)
        public static uint ProjectId;
        public static float Dist = 200f, Pitch = 35f, Lead = 0f, Tau = 1.5f, Orbit = 0f;
        public static bool AutoYaw = true;
        public static bool AutoForward;      // yaw=autofwd: look ahead (+u) instead of back over the finished work
        public static float YawDeg;
        public static float OrbitAccum;

        // our own smoothed state + what we wrote last (player-override detection)
        public static bool HaveState;
        public static float3 Pivot;
        public static float Zoom, Yaw, PitchNow;
        public static float LastFrontU;
        // Crew whose front the camera frames (fixed index; clamped to the project's crews each frame). With one crew: the
        // single project front (ChainMap.RenderFront).
        public static int Crew;

        // Chain u the camera frames now (render domain, no lead).
        public static float FrontU(ProjectRecord p)
        {
            if (p.View().CrewCount <= 1) return ChainMap.RenderFront(p, RRWClock.RenderFrame, RRWClock.RenderFrameTime);
            return ChainMap.RenderCrewFront(p, RRWClock.RenderFrame, RRWClock.RenderFrameTime, Crew);
        }

        public static void Start(uint project, bool follow)
        {
            ProjectId = project;
            Follow = follow;
            Active = true;
            Snap = true;
            HaveState = false;
            OrbitAccum = 0f;
        }

        public static void Stop(string why)
        {
            if (!Active) return;
            Active = false;
            HaveState = false;
            RRWLog.Dev("dev rrw cam stopped (" + why + ")");
        }

        // Auto yaw (degrees, CameraController convention: view direction (sin yaw, 0, cos yaw)): the camera stands on the
        // chain-right side, 35 degrees off the perpendicular towards +u, and looks back across the road towards -u, so
        // the finished trench recedes diagonally behind the working front (front owner in the middle third).
        // autofwd mirrors it: from the -u side looking ahead along +u over the untouched ground.
        public static float AutoYawFor(float3 chainDir)
        {
            float2 d = math.normalizesafe(chainDir.xz, new float2(0f, 1f));
            float2 right = new float2(d.y, -d.x);
            float a = math.radians(kAutoYawOffset);
            float2 v = -right * math.cos(a) + (AutoForward ? d : -d) * math.sin(a);
            return math.degrees(math.atan2(v.x, v.y));
        }

        public static float DeltaAngle(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        public static float Wrap(float a)
        {
            a %= 360f;
            if (a > 180f) a -= 360f;
            if (a < -180f) a += 360f;
            return a;
        }
    }

    // Deferred dev actions (rrw.perf reports), ticked every rendered frame by DevCameraSystem. Real time (dev timeouts).
    internal static class DevDeferred
    {
        private struct Item { public float Due; public Action Act; }
        private static readonly List<Item> s_Items = new List<Item>();

        public static void After(float seconds, Action act) =>
            s_Items.Add(new Item { Due = UnityEngine.Time.realtimeSinceStartup + seconds, Act = act });

        public static void Tick()
        {
            if (s_Items.Count == 0) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            for (int i = s_Items.Count - 1; i >= 0; i--)
            {
                if (s_Items[i].Due > now) continue;
                var act = s_Items[i].Act;
                s_Items.RemoveAt(i);
                try { act(); }
                catch (Exception e) { RRWLog.ErrorOnce("dev deferred", e); }
            }
        }
    }

    // PreCulling, before CameraUpdateSystem (order 590; DEVTOOLS only). Frames the project's work front and,
    // with follow=1, follows it with exponential smoothing on the render-domain front (ChainMap.RenderFront: the same
    // front the machines follow, so it does not jitter at rrw.timescale 60). With several crews it follows crew
    // DevCam.Crew (ChainMap.RenderCrewFront; rrw.cam ... crew=i, default the Director's FocusCrew at the start). Stops when the player moves the camera
    // (pivot > 2 m, zoom > 5 %, yaw > 3 deg away from what we wrote), on rrw.cam off, when the project is gone, on load.
    // Never structural changes.
    [RegisterSystem(SystemUpdatePhase.PreCulling, Before = typeof(CameraUpdateSystem), Order = RRWOrder.DevCamera)]
    public partial class DevCameraSystem : GameSystemBase
    {
        private CameraUpdateSystem m_Camera;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Camera = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            DevCam.Stop("load");
        }

        protected override void OnUpdate()
        {
            DevDeferred.Tick();
            PerfCrews.Sample();     // rrw.perf crew averages (only while a window is open)
            try { R5Dev.Sample(); } // Rollers-vs-setting and car-half-on-blocked-chain persistence (rrw.check), every 30 updates
            catch (Exception e) { RRWLog.ErrorOnce("dev r5 sample", e); }
            if (!DevCam.Active) return;
            try
            {
                Drive();
            }
            catch (Exception e)
            {
                RRWLog.ErrorOnce("dev camera", e);
                DevCam.Stop("error: " + e.GetType().Name);
            }
        }

        private void Drive()
        {
            var gm = GameManager.instance;
            if (gm == null || gm.gameMode != GameMode.Game) { DevCam.Stop("not in game"); return; }
            var cam = m_Camera != null ? m_Camera.gamePlayController : null;
            if (cam == null) { DevCam.Stop("no gameplay camera"); return; }
            if (!SiteRegistry.TryGetProject(DevCam.ProjectId, out var p)) { DevCam.Stop("project p" + DevCam.ProjectId + " gone"); return; }

            // player override: the controller moved away from what we wrote last frame
            if (DevCam.HaveState && !DevCam.Snap)
            {
                UnityEngine.Vector3 cp = cam.pivot;
                float dxz = math.distance(new float2(cp.x, cp.z), DevCam.Pivot.xz);
                float dz = math.abs(cam.zoom - DevCam.Zoom) / math.max(1f, DevCam.Zoom);
                float dy = math.abs(DevCam.DeltaAngle(DevCam.Yaw, cam.rotation.y));
                if (dxz > 2f || dz > 0.05f || dy > 3f) { DevCam.Stop("player moved the camera"); return; }
            }

            RRWClock.Update(World);
            float u = DevCam.FrontU(p) + DevCam.Lead;   // the followed crew's front (RenderCrewFront)
            u = math.clamp(u, 0f, math.max(0f, p.ChainLength));
            if (!ChainMap.Point(EntityManager, p, u, out float3 pos, out float3 dir)) { DevCam.Stop("front lookup failed"); return; }
            DevCam.LastFrontU = u;

            float dt = math.clamp(UnityEngine.Time.unscaledDeltaTime, 0f, 0.25f);
            DevCam.OrbitAccum = DevCam.Wrap(DevCam.OrbitAccum + DevCam.Orbit * dt);
            float yawTarget = DevCam.Wrap((DevCam.AutoYaw ? DevCam.AutoYawFor(dir) : DevCam.YawDeg) + DevCam.OrbitAccum);
            var zr = cam.zoomRange;
            float zoomTarget = math.clamp(DevCam.Dist, zr.min, zr.max);

            if (DevCam.Snap || !DevCam.HaveState)
            {
                DevCam.Pivot = pos;
                DevCam.Zoom = zoomTarget;
                DevCam.Yaw = yawTarget;
                DevCam.PitchNow = DevCam.Pitch;
                DevCam.Snap = false;
                DevCam.HaveState = true;
            }
            else
            {
                float k = DevCam.Tau > 1e-3f ? 1f - math.exp(-dt / DevCam.Tau) : 1f;
                DevCam.Pivot += (pos - DevCam.Pivot) * k;
                DevCam.Zoom += (zoomTarget - DevCam.Zoom) * k;
                DevCam.Yaw = DevCam.Wrap(DevCam.Yaw + DevCam.DeltaAngle(DevCam.Yaw, yawTarget) * k);
                DevCam.PitchNow += (DevCam.Pitch - DevCam.PitchNow) * k;
            }

            cam.pivot = new UnityEngine.Vector3(DevCam.Pivot.x, DevCam.Pivot.y, DevCam.Pivot.z);
            cam.zoom = DevCam.Zoom;
            cam.rotation = new UnityEngine.Vector3(DevCam.PitchNow, DevCam.Yaw, 0f);

            if (!DevCam.Follow)
            {
                DevCam.Active = false;   // one-shot framing done (rrw.focus / follow=0)
                DevCam.HaveState = false;
            }
        }
    }
}
#endif
