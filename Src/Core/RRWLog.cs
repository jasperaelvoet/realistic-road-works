using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Rendering;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // Shared logger on top of Mod.Log (Logs/RealisticRoadWorks.log). Product lines are "rrw <module> ...";
    // dev command output uses "dev rrw ..." (Src/Dev). Errors are counted and each distinct one is printed once,
    // so a per-frame failure never floods the log.
    public static class RRWLog
    {
        private static readonly HashSet<string> s_Once = new HashSet<string>();
        public static int Errors;

        public static void Info(string msg) => Mod.Log.Info("rrw " + msg);
        public static void Warn(string msg) => Mod.Log.Warn("rrw " + msg);
        public static void Error(string msg) { Errors++; Mod.Log.Error("rrw " + msg); }
        // True when Verbose lines are written: check it before building a verbose string in a per-frame path.
        public static bool VerboseEnabled => RRWDebug.Verbose || (RRWSettings.Current != null && RRWSettings.Current.VerboseLogging);
        public static void Verbose(string msg) { if (VerboseEnabled) Mod.Log.Info("rrw " + msg); }
        // Dev command output without the product prefix: "dev rrw ..." lines (Src/Dev and module dev commands).
        public static void Dev(string msg) => Mod.Log.Info(msg);

        public static void Once(string key, string msg)
        {
            if (s_Once.Count < 500 && s_Once.Add(key)) Mod.Log.Info("rrw " + msg);
        }

        // Counts every exception; prints each distinct (where, type, message) once.
        public static void ErrorOnce(string where, Exception e)
        {
            Errors++;
            string key = where + "|" + e.GetType().Name + "|" + e.Message;
            if (s_Once.Count < 500 && s_Once.Add(key)) Mod.Log.Error("rrw ERROR in " + where + " (#" + Errors + "): " + e);
        }

        public static string F(float v) => float.IsNaN(v) ? "nan" : float.IsInfinity(v) ? (v > 0 ? "inf" : "-inf") : v.ToString("0.###", CultureInfo.InvariantCulture);
        public static string F3(float3 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";
        public static string E(Entity e) => e.Index + ":" + e.Version;
    }

    // Per-module failure guard: a system that throws 30 frames in a row disables its optional work instead of
    // spamming. Usage: if (m_Guard.Faulted) return; try { ...; m_Guard.Ok(); } catch (Exception e) { m_Guard.Fail(e); }
    public sealed class RRWGuard
    {
        private readonly string m_Name;
        private int m_Consecutive;
        public bool Faulted { get; private set; }
        public int Limit = 30;

        public RRWGuard(string name) { m_Name = name; }
        public void Ok() => m_Consecutive = 0;

        public void Fail(Exception e)
        {
            RRWLog.ErrorOnce(m_Name, e);
            if (++m_Consecutive >= Limit && !Faulted)
            {
                Faulted = true;
                RRWLog.Error(m_Name + " failed " + Limit + " frames in a row: disabled until the next load (see the first error above)");
            }
        }

        public void Reset() { m_Consecutive = 0; Faulted = false; }
    }

    // Clock snapshot for the current frame (main thread). Any system may call Update(World) first; it is idempotent per frame.
    // RenderFrame is read at the caller's phase: at Mod1 it is the value PrepareRendering produced LAST frame (one frame stale);
    // that is fine for animation because every consumer uses the same snapshot. Never anchor progress on it (use SimFrame).
    public static class RRWClock
    {
        public static int RenderFrameCount = -1;  // UnityEngine.Time.frameCount of the snapshot
        public static uint SimFrame;             // SimulationSystem.frameIndex
        public static uint SimDelta;             // simulation frames since the previous snapshot (0 while paused)
        public static uint RenderFrame;          // RenderingSystem.frameIndex (tracks sim frames, interpolated)
        public static float RenderFrameTime;     // RenderingSystem.frameTime (0..1)
        public static float NormalizedTime;      // TimeSystem.normalizedTime (0..1 of the day)
        public static float SelectedSpeed;       // SimulationSystem.selectedSpeed (0 = paused)
        // Incremented once per rendered frame (the first Update call of the frame). Keeps counting while the game is
        // PAUSED (Mod1 runs while paused), so every throttle, hold, GC age and resnap delay uses it.
        // Sim frames are for progress only; RenderFrame(+Time) is for animation only.
        public static uint UpdateIndex;
        private static uint s_LastSim;
        private static bool s_Have;

        public static void Update(World world)
        {
            int fc = UnityEngine.Time.frameCount;
            if (fc == RenderFrameCount) return;
            RenderFrameCount = fc;
            UpdateIndex++;
            var sim = world.GetExistingSystemManaged<SimulationSystem>();
            var rs = world.GetExistingSystemManaged<RenderingSystem>();
            var ts = world.GetExistingSystemManaged<TimeSystem>();
            if (sim != null)
            {
                uint f = sim.frameIndex;
                SimDelta = s_Have && f >= s_LastSim ? f - s_LastSim : 0u;
                if (SimDelta > 600) SimDelta = 0;   // load / jump: never accrue a burst
                s_LastSim = f;
                s_Have = true;
                SimFrame = f;
                SelectedSpeed = sim.selectedSpeed;
            }
            if (rs != null) { RenderFrame = rs.frameIndex; RenderFrameTime = rs.frameTime; }
            if (ts != null) NormalizedTime = ts.normalizedTime;
        }

        // Call from OnGamePreload so the first frame after a load never accrues.
        public static void Reset() { s_Have = false; SimDelta = 0; RenderFrameCount = -1; }
    }
}
