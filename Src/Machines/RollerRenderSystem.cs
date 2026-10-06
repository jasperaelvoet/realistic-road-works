using System;
using Colossal.Serialization.Entities;
using Game;
using Game.Objects;
using Game.Rendering;
using Game.SceneFlow;
using RealisticRoadWorks.Dev;
using Unity.Mathematics;
using UnityEngine;

// RollerRenderSystem (PreCulling, Before PreCullingSystem, RRWOrder.RollerRender 605, after MachineMover).
// Every rendered frame, every roller unit is posed from its Puppet's MachinePlan at the machine clock (render frame + frameTime x
// MachineClockScale - the puppets' clock; frozen while paused, follows game speed): the pivot state from MachineMotion.State, the
// rear and front drum contacts on the path at s -/+ kRollerWheelbase / 2 (verified kinematics: curves articulate the machine, clamped
// at +-kRollerMaxArticulationDeg), Y from the Machines track floor table at each drum (pitch), the root 1 cm into the surface,
// drum spin from the odometer, vibration shimmer on vibrating forward passes (LOD0 only), LOD by camera distance, lights from the
// puppet's light flags (set by the Director) by shared material swaps, the beacon phase materials once per frame.
// Cleanup: OnGamePreload and the first update outside game mode destroy every roller GameObject (+ stray sweep); the Machines debug
// layer off destroys them too (the Director then drops the units). A throwing roller pass disables itself (log once), destroys every
// roller GameObject and the Director stops spawning rollers: puppets are not affected.
namespace RealisticRoadWorks.V3.Machines
{
    [RegisterSystem(SystemUpdatePhase.PreCulling, Before = typeof(PreCullingSystem), Order = RRWOrder.RollerRender)]
    public partial class RollerRenderSystem : GameSystemBase
    {
        public static float Lod0 = RRWConst.kRollerLod0Distance, Lod1 = RRWConst.kRollerLod1Distance;   // dev rrw.mx.roller lod
        public static bool Faulted;           // the roller pass threw: rollers disabled until the next load (Director: no roller spawns)
        public static int Posed, FrameErrors;
        public static float LastFrameMs, AvgMsPerRoller, MaxMsPerRoller;   // dev (rrw.mx.roller, rrw.mx.check): target <= 0.02 ms per roller
        private RenderingSystem m_Rendering;
        private bool m_LeftGame;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Rendering = World.GetOrCreateSystemManaged<RenderingSystem>();
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            try
            {
                RollerWorld.ClearAll("game preload " + purpose + " " + mode);
                RollerMaterials.ResetBeaconLevels();
            }
            catch (Exception e) { RRWLog.ErrorOnce("machines roller preload", e); }
            Faulted = false;
            m_LeftGame = false;
        }

        protected override void OnDestroy()
        {
            try
            {
                RollerWorld.ClearAll("system destroy");
                RollerMaterials.Destroy();
                RollerModel.Destroy();
            }
            catch (Exception e) { RRWLog.ErrorOnce("machines roller destroy", e); }
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            var gm = GameManager.instance;
            if (gm == null || gm.gameMode != GameMode.Game)
            {
                // first update outside game mode (menu, editor): every roller GameObject goes (+ strays)
                if (!m_LeftGame)
                {
                    m_LeftGame = true;
                    try { RollerWorld.ClearAll("left game mode " + (gm != null ? gm.gameMode.ToString() : "null")); }
                    catch (Exception e) { RRWLog.ErrorOnce("machines roller menu cleanup", e); }
                }
                return;
            }
            m_LeftGame = false;
            if (Faulted) return;
            var all = MachineRegistry.All;
            bool any = false;
            for (int i = 0; i < all.Count; i++) if (all[i].IsRoller && all[i].Unit != null) { any = true; break; }
            if (!any) return;
            // the Machines debug layer off: the rollers go now (the Director drops the units: Alive() false)
            if (!RRWDebug.On(DebugLayers.Machines))
            {
                RollerWorld.ClearAll("machines layer off");
                return;
            }
            long t0 = RRWPerf.Start();
            MxPerf.Begin(MxT.R_Roller);
            try
            {
                var tv = MachineTrackStore.View();
                if (!tv.IsCreated) return;
                var clk = MachineRegistry.Clock;
                double tau = clk.Tau(m_Rendering.frameIndex, m_Rendering.frameTime);
                RollerMaterials.UpdateBeacons(tau);
                var cam = RollerView.Cam(World);
                bool haveCam = cam != null;
                Vector3 camPos = haveCam ? cam.transform.position : Vector3.zero;
                int posed = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i];
                    if (!p.IsRoller || p.Unit == null || p.Unit.Root == null || p.Plan.Count <= 0) continue;
                    Pose(p, tv, clk, tau, haveCam, camPos);
                    posed++;
                }
                Posed = posed;
                MxPerf.Count(MxC.RollerPoses, posed);
            }
            catch (Exception e)
            {
                // A roller unit that throws disables the roller pass (log once) and removes every roller GameObject
                Faulted = true;
                FrameErrors++;
                RRWLog.Warn("machines: roller pass disabled after an exception (rollers removed until the next load): " + e);
                try { RollerWorld.ClearAll("roller pass faulted"); } catch { }
            }
            finally
            {
                MxPerf.End(MxT.R_Roller);
                LastFrameMs = (float)RRWPerf.Ms(RRWPerf.Start() - t0);
                if (Posed > 0)
                {
                    float per = LastFrameMs / Posed;
                    AvgMsPerRoller = AvgMsPerRoller <= 0f ? per : 0.98f * AvgMsPerRoller + 0.02f * per;   // dev: R.Roller per unit
                    if (per > MaxMsPerRoller) MaxMsPerRoller = per;
                }
                RRWPerf.Stop(PerfSlot.MachineJobs, t0);
            }
        }

        // World point of the track at (u, lat): centre line + lateral, Y = the track floor table there (floor / ramp / carriageway).
        static bool Ground(in TrackView tv, int slot, float u, float lat, out float3 w)
        {
            w = default;
            if (!tv.Point(slot, u, out var tp)) return false;
            float2 xz = tp.Pos.xz + tp.Right * lat;
            float rel = tp.HeightRel(lat);
            w = new float3(xz.x, tp.Pos.y + (float.IsNaN(rel) ? 0f : rel), xz.y);
            return true;
        }

        // Lateral of the running leg at chain u (the drums sample the lane change of a forward pass at their own u: verified look,
        // the frames yaw naturally through the shift).
        internal static float LatAlong(in MachineLeg leg, float u, float fallback)
        {
            if (leg.Kind == (byte)LegKind.Drive && math.abs(leg.L1 - leg.L0) > 1e-4f)
            {
                sbyte mv = leg.Move >= 0 ? (sbyte)1 : (sbyte)-1;
                float D = math.abs(leg.U1 - leg.U0);
                float d = math.clamp((u - leg.U0) * mv, 0f, D);
                float w = math.max(0.05f, leg.P1 - leg.P0);
                return leg.L0 + (leg.L1 - leg.L0) * MachineMotion.Smooth((d - leg.P0) / w);
            }
            if (leg.Kind == (byte)LegKind.Hold) return leg.L0;
            return fallback;
        }

        static void Pose(Puppet p, in TrackView tv, in ClockData clk, double tau, bool haveCam, Vector3 camPos)
        {
            var u = p.Unit;
            MachineMotion.State(p.Plan, clk, tau, out var s);
            var leg = p.Plan.Get(s.Leg);
            int slot = p.Plan.Track;
            float f = s.Hu >= 0f ? 1f : -1f;                 // nose direction along +u
            float wb = RollerModel.WheelbaseHalf;
            float uR = s.U - f * wb, uF = s.U + f * wb;
            if (!Ground(tv, slot, uR, LatAlong(leg, uR, s.Lat), out float3 r) ||
                !Ground(tv, slot, s.U, s.Lat, out float3 j) ||
                !Ground(tv, slot, uF, LatAlong(leg, uF, s.Lat), out float3 fr))
            {
                u.SetLodAndLights(2, false, false, RollerMaterials.kBeaconOff);
                return;
            }
            float3 dirR = math.normalizesafe(j - r, new float3(0f, 0f, 1f));
            float3 root = r + dirR * wb;
            float3 dirF = math.normalizesafe(fr - root, dirR);
            var rootRot = Quaternion.LookRotation(new Vector3(dirR.x, dirR.y, dirR.z), Vector3.up);
            var frontRot = Quaternion.LookRotation(new Vector3(dirF.x, dirF.y, dirF.z), Vector3.up);
            var local = Quaternion.Inverse(rootRot) * frontRot;
            float yaw = Mathf.DeltaAngle(0f, local.eulerAngles.y);
            float clamped = Mathf.Clamp(yaw, -RRWConst.kRollerMaxArticulationDeg, RRWConst.kRollerMaxArticulationDeg);
            if (clamped != yaw) local = local * Quaternion.Euler(0f, clamped - yaw, 0f);   // path beyond the joint stop
            u.ArticulationDeg = clamped;
            p.RootYErr = math.abs(root.y - j.y);                                                // dev: joint vs track floor at the pivot
            // camera distance (LOD) and light state
            float camDist = haveCam ? Vector3.Distance(camPos, new Vector3(root.x, root.y, root.z)) : 0f;
            u.CamDist = camDist;
            int lod = !haveCam ? 0 : camDist < Lod0 ? 0 : camDist < Lod1 ? 1 : 2;
            var fl = p.Plan.Flags;
            bool lamps = (fl & (TransformFlags.MainLights | TransformFlags.WorkLights)) != 0;
            bool tail = (fl & TransformFlags.RearLights) != 0;
            int beacon = (fl & TransformFlags.WarningLights) != 0 ? u.BeaconPhase : RollerMaterials.kBeaconOff;
            u.SetLodAndLights(lod, lamps, tail, beacon);
            if (lod >= 2) { u.LastTau = tau; return; }
            // drums: no slip (odometer); vibration only on vibrating forward passes while moving, and only while the clock runs
            float drumDeg = (float)(math.degrees(s.Odo / RollerModel.DrumR) % 360.0);
            bool running = !double.IsNaN(u.LastTau) && tau > u.LastTau + 1e-6;
            bool vib = lod == 0 && running && s.Anim == (byte)AnimKind.Compact && math.abs(s.Speed) > 0.15f;
            u.LastTau = tau;
            u.Apply(new Vector3(root.x, root.y - MxConst.kRollerJointDrop, root.z), rootRot, local, drumDeg, vib ? RRWConst.kRollerVibAmplitude : 0f);
        }
    }
}
