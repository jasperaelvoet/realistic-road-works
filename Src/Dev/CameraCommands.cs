#if DEVTOOLS
using System.Collections.Generic;
using System.Globalization;
using Game.Rendering;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.DevCmds
{
    internal static class CamArgs
    {
        // Parses the shared framing options into DevCam. Returns false (with a message) on a bad value.
        public static bool Apply(DevContext ctx, string[] a, float defDist, bool defFollow)
        {
            DevCam.Dist = math.clamp(DevSites.OptF(a, "dist", defDist), 5f, 5000f);
            DevCam.Pitch = math.clamp(DevSites.OptF(a, "pitch", 35f), 1f, 89f);
            DevCam.Lead = DevSites.OptF(a, "lead", 0f);
            DevCam.Tau = math.max(0f, DevSites.OptF(a, "tau", 1.5f));
            DevCam.Orbit = DevSites.OptF(a, "orbit", 0f);
            string yaw = DevSites.Opt(a, "yaw", "auto").ToLowerInvariant();
            DevCam.AutoForward = yaw == "autofwd";
            DevCam.AutoYaw = yaw == "auto" || yaw == "autofwd";
            if (!DevCam.AutoYaw)
            {
                if (!DevSites.TryFloat(yaw, out float y)) { DevSites.Out(ctx, "cam: bad yaw '" + yaw + "' (auto | autofwd | degrees)"); return false; }
                DevCam.YawDeg = DevCam.Wrap(y);
            }
            return true;
        }

        public static ProjectRecord One(DevContext ctx, string site, string label)
        {
            var list = new List<ProjectRecord>();
            if (!DevSites.Resolve(ctx, site, list, out _, out string err)) { DevSites.Out(ctx, label + ": " + err); return null; }
            return list[0];
        }

        // crew=i (clamped), default the Director's FocusCrew (the crew nearest the camera). False on a bad value.
        public static bool ApplyCrew(DevContext ctx, string[] a, ProjectRecord p)
        {
            int n = p.View().CrewCount;
            string c = DevSites.Opt(a, "crew", null);
            if (c == null) { DevCam.Crew = math.clamp(p.FocusCrew, 0, n - 1); return true; }
            if (!DevSites.TryInt(c, out int i) || i < 0) { DevSites.Out(ctx, "cam: bad crew '" + c + "' (0.." + (n - 1) + ")"); return false; }
            if (i >= n) DevSites.Out(ctx, "cam: p" + p.Id + " has " + n + " crew(s) now; following crew " + (n - 1) + " until it has more");
            DevCam.Crew = i;
            return true;
        }

        // Front u of the framed crew (current frame, not render-interpolated).
        public static float CrewU(ProjectRecord p) => p.View().CrewCount <= 1 ? p.FrontU : PhasePlan.CrewFront(p.View(), DevCam.Crew);

        public static string CrewText(ProjectRecord p)
        {
            int n = p.View().CrewCount;
            return n <= 1 ? "" : " crew=" + math.min(DevCam.Crew, n - 1) + "/" + n;
        }

        public static float YawNow(DevContext ctx, ProjectRecord p, out float3 front)
        {
            front = p.FrontPosition;
            if (!ChainMap.Point(ctx.EntityManager, p, CrewU(p) + DevCam.Lead, out float3 pos, out float3 dir)) return DevCam.YawDeg;
            front = pos;
            return DevCam.Wrap((DevCam.AutoYaw ? DevCam.AutoYawFor(dir) : DevCam.YawDeg) + DevCam.OrbitAccum);
        }

        public static string N(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }

    public sealed class CamCommand : IDevCommand
    {
        public string Name => "rrw.cam";
        public string Help => "rrw.cam <site> [dist=200] [pitch=35] [yaw=auto|autofwd|<deg>] [follow=1] [lead=0] [tau=1.5] [orbit=0] [crew=i] | rrw.cam off | rrw.cam (print) - " +
                              "video camera on the work front (crew i's front; default the crew nearest the camera)";
        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count == 0)
            {
                Print(ctx);
                return;
            }
            if (pos[0].Equals("off", global::System.StringComparison.OrdinalIgnoreCase) || pos[0] == "0")
            {
                if (DevCam.Active) DevCam.Stop("rrw.cam off");
                else DevSites.Out(ctx, "cam off");
                return;
            }
            var p = CamArgs.One(ctx, pos[0], "cam");
            if (p == null) return;
            if (!CamArgs.Apply(ctx, a, 200f, true)) return;
            if (!CamArgs.ApplyCrew(ctx, a, p)) return;
            bool follow = DevSites.Bool(DevSites.Opt(a, "follow", "1"), true);
            DevCam.Start(p.Id, follow);
            if (!DevSites.Bool(DevSites.Opt(a, "snap", "1"), true))
            {
                // glide in from the current view instead of cutting to the front
                var cam = ctx.System<CameraUpdateSystem>().gamePlayController;
                if (cam != null)
                {
                    UnityEngine.Vector3 pv = cam.pivot, rot = cam.rotation;
                    DevCam.Pivot = new float3(pv.x, pv.y, pv.z);
                    DevCam.Zoom = cam.zoom;
                    DevCam.Yaw = rot.y;
                    DevCam.PitchNow = rot.x;
                    DevCam.HaveState = true;
                    DevCam.Snap = false;
                }
            }
            float yaw = CamArgs.YawNow(ctx, p, out _);
            DevSites.Out(ctx, "cam p" + p.Id + " " + (follow ? "follow" : "frame") + " dist=" + CamArgs.N(DevCam.Dist) + " pitch=" + CamArgs.N(DevCam.Pitch)
                              + " yaw=" + CamArgs.N(yaw) + (DevCam.AutoYaw ? (DevCam.AutoForward ? "(autofwd)" : "(auto)") : "")
                              + " lead=" + CamArgs.N(DevCam.Lead) + " tau=" + CamArgs.N(DevCam.Tau) + " orbit=" + CamArgs.N(DevCam.Orbit)
                              + CamArgs.CrewText(p) + " front u=" + DevSites.F1(CamArgs.CrewU(p)));
        }

        private static void Print(DevContext ctx)
        {
            var cam = ctx.System<CameraUpdateSystem>().gamePlayController;
            if (cam == null) { DevSites.Out(ctx, "cam: no gameplay camera"); return; }
            UnityEngine.Vector3 pv = cam.pivot, rot = cam.rotation;
            if (DevCam.Active && SiteRegistry.TryGetProject(DevCam.ProjectId, out var p))
            {
                DevSites.Out(ctx, "cam following p" + p.Id + CamArgs.CrewText(p) + " front u=" + DevSites.F1(DevCam.LastFrontU));
                DevSites.Out(ctx, "cam rrw.cam p" + p.Id + " dist=" + CamArgs.N(DevCam.Dist) + " pitch=" + CamArgs.N(DevCam.Pitch)
                                  + " yaw=" + (DevCam.AutoYaw ? (DevCam.AutoForward ? "autofwd" : "auto") : CamArgs.N(DevCam.YawDeg))
                                  + " follow=" + (DevCam.Follow ? 1 : 0) + " lead=" + CamArgs.N(DevCam.Lead) + " tau=" + CamArgs.N(DevCam.Tau)
                                  + " orbit=" + CamArgs.N(DevCam.Orbit) + (p.View().CrewCount > 1 ? " crew=" + DevCam.Crew : ""));
            }
            DevSites.Out(ctx, "cam rrw.campos " + CamArgs.N(pv.x) + " " + CamArgs.N(pv.y) + " " + CamArgs.N(pv.z)
                              + " dist=" + CamArgs.N(cam.zoom) + " pitch=" + CamArgs.N(rot.x) + " yaw=" + CamArgs.N(rot.y));
        }
    }

    public sealed class FocusWorksCommand : IDevCommand
    {
        public string Name => "rrw.focus";
        public string Help => "rrw.focus <site> [dist=150] [pitch=35] [yaw=auto|autofwd|<deg>] [lead=0] [crew=i] - one-shot framing of the work front (rrw.cam follow=0)";
        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 1) { DevSites.Out(ctx, "usage: " + Help); return; }
            var p = CamArgs.One(ctx, pos[0], "focus");
            if (p == null) return;
            if (!CamArgs.Apply(ctx, a, 150f, false)) return;
            if (!CamArgs.ApplyCrew(ctx, a, p)) return;
            DevCam.Orbit = 0f;
            DevCam.Start(p.Id, false);
            CamArgs.YawNow(ctx, p, out float3 front);
            DevSites.Out(ctx, "focus p" + p.Id + CamArgs.CrewText(p) + " front=" + DevSites.F3(front) + " u=" + DevSites.F1(CamArgs.CrewU(p)));
        }
    }

    public sealed class CamPosCommand : IDevCommand
    {
        public string Name => "rrw.campos";
        public string Help => "rrw.campos <x> <y> <z> [dist=] [pitch=] [yaw=] - fixed camera (reproducible shots; rrw.cam prints the current one)";
        public void Run(DevContext ctx, string[] a)
        {
            var pos = DevSites.Positional(a);
            if (pos.Count < 3 || !DevSites.TryFloat(pos[0], out float x) || !DevSites.TryFloat(pos[1], out float y) || !DevSites.TryFloat(pos[2], out float z))
            {
                DevSites.Out(ctx, "usage: " + Help);
                return;
            }
            var cam = ctx.System<CameraUpdateSystem>().gamePlayController;
            if (cam == null) { DevSites.Out(ctx, "campos: no gameplay camera"); return; }
            if (DevCam.Active) DevCam.Stop("rrw.campos");
            UnityEngine.Vector3 rot = cam.rotation;
            var zr = cam.zoomRange;
            cam.pivot = new UnityEngine.Vector3(x, y, z);
            cam.zoom = math.clamp(DevSites.OptF(a, "dist", cam.zoom), zr.min, zr.max);
            cam.rotation = new UnityEngine.Vector3(math.clamp(DevSites.OptF(a, "pitch", rot.x), 1f, 89f), DevCam.Wrap(DevSites.OptF(a, "yaw", rot.y)), 0f);
            DevSites.Out(ctx, "campos (" + CamArgs.N(x) + "," + CamArgs.N(y) + "," + CamArgs.N(z) + ") dist=" + CamArgs.N(cam.zoom)
                              + " pitch=" + CamArgs.N(cam.rotation.x) + " yaw=" + CamArgs.N(cam.rotation.y));
        }
    }
}
#endif
