using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Mathematics;

namespace RealisticRoadWorks.Dev
{
    // Test-scene helpers for driving the game's own tools: pick a tool by prefab name (what a toolbar click does) and project world
    // points to input pixels, so scripted clicks land on exact road positions.
    public class ToolPickCommand : IDevCommand
    {
        private static Snap s_SnapBefore;
        private static bool s_SnapSaved;

        public string Name => "toolpick";
        public string Help => "toolpick <prefab name, _ for spaces> [mode=straight|curve|complex|continuous|grid|replace] [snap=off|on] [elev=<m>] | toolpick off - activate the tool of a road / upgrade prefab like a toolbar click";

        public void Run(DevContext ctx, string[] args)
        {
            var tools = ctx.System<ToolSystem>();
            if (args.Length == 0 || args[0] == "off")
            {
                tools.activeTool = ctx.System<DefaultToolSystem>();
                ctx.Log("toolpick: default tool");
                return;
            }
            string name = args[0].Replace('_', ' ');
            var prefab = Find(ctx, name, out int matches);
            if (prefab == null) { ctx.Log("toolpick: no net prefab named '" + name + "' (" + matches + " other prefabs with that name)"); return; }
            bool activated = tools.ActivatePrefabTool(prefab);
            var net = tools.activeTool as NetToolSystem;
            for (int i = 1; i < args.Length && net != null; i++)
            {
                string a = args[i];
                if (a.StartsWith("mode=")) net.mode = ParseMode(a.Substring(5), net.mode);
                else if (a == "snap=off")
                {
                    if (!s_SnapSaved) { s_SnapBefore = net.selectedSnap; s_SnapSaved = true; }
                    net.selectedSnap = Snap.None;
                }
                else if (a.StartsWith("elev=")) net.elevation = DevContext.F(a.Substring(5));
                else if (a == "snap=on")
                {
                    net.selectedSnap = s_SnapSaved ? s_SnapBefore : Snap.All;
                    s_SnapSaved = false;
                }
            }
            ctx.Log("toolpick: '" + name + "' (" + prefab.GetType().Name + ") activated=" + activated + " tool=" + (tools.activeTool?.toolID ?? "null")
                + (net != null ? " mode=" + net.mode + " actualMode=" + net.actualMode + " snap=" + net.selectedSnap + " elev=" + net.elevation.ToString("0.#", CultureInfo.InvariantCulture) : ""));
        }

        private static NetToolSystem.Mode ParseMode(string s, NetToolSystem.Mode fallback)
        {
            switch (s)
            {
                case "straight": return NetToolSystem.Mode.Straight;
                case "curve": return NetToolSystem.Mode.SimpleCurve;
                case "complex": return NetToolSystem.Mode.ComplexCurve;
                case "continuous": return NetToolSystem.Mode.Continuous;
                case "grid": return NetToolSystem.Mode.Grid;
                case "replace": return NetToolSystem.Mode.Replace;
                default: return fallback;
            }
        }

        // Net prefabs first (roads, tracks, upgrades); a building piece or render mesh with the same name is never picked.
        private static PrefabBase Find(DevContext ctx, string name, out int otherMatches)
        {
            otherMatches = 0;
            var ps = ctx.System<PrefabSystem>();
            var list = typeof(PrefabSystem).GetField("m_Prefabs", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ps) as List<PrefabBase>;
            if (list == null) return null;
            PrefabBase found = null;
            foreach (var p in list)
            {
                if (p == null || p.name != name) continue;
                if (p is NetPrefab && found == null) found = p;
                else otherMatches++;
            }
            return found;
        }
    }

    public class WorldToScreenCommand : IDevCommand
    {
        public string Name => "w2s";
        public string Help => "w2s <road#> <t>[,t2,..] [lat] | w2s xyz <x> <y> <z> - input pixel (3840x2160, top-left origin) of a road point; lat > 0 = right of the road's direction";

        public void Run(DevContext ctx, string[] a)
        {
            var cam = ctx.System<CameraUpdateSystem>().activeCamera ?? UnityEngine.Camera.main;
            if (cam == null) { ctx.Log("w2s: no camera"); return; }
            if (a.Length >= 4 && a[0] == "xyz")
            {
                Out(ctx, cam, new float3(DevContext.F(a[1]), DevContext.F(a[2]), DevContext.F(a[3])), "xyz");
                return;
            }
            var e = ctx.Road(a[0]);
            var bez = ctx.EntityManager.GetComponentData<Curve>(e).m_Bezier;
            float lat = a.Length > 2 ? DevContext.F(a[2]) : 0f;
            foreach (var ts in (a.Length > 1 ? a[1] : "0.5").Split(','))
            {
                float t = DevContext.F(ts);
                float3 p = MathUtils.Position(bez, t);
                float3 tan = MathUtils.Tangent(bez, t);
                float2 right = math.normalizesafe(new float2(tan.z, -tan.x));
                p.xz += right * lat;
                Out(ctx, cam, p, "road " + a[0] + " e" + e.Index + " t=" + t.ToString("0.###", CultureInfo.InvariantCulture) + " lat=" + lat.ToString("0.##", CultureInfo.InvariantCulture));
            }
        }

        private static void Out(DevContext ctx, UnityEngine.Camera cam, float3 p, string what)
        {
            var s = cam.WorldToScreenPoint(new UnityEngine.Vector3(p.x, p.y, p.z));
            float w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
            float gx = s.x * 3840f / w, gy = (h - s.y) * 2160f / h;
            bool onScreen = s.z > 0f && gx >= 0f && gx < 3840f && gy >= 0f && gy < 2160f;
            ctx.Log(string.Format(CultureInfo.InvariantCulture, "w2s {0} world=({1:0.0},{2:0.0},{3:0.0}) click {4:0} {5:0} depth={6:0.0} onScreen={7} screen={8}x{9}",
                what, p.x, p.y, p.z, gx, gy, s.z, onScreen, w, h));
        }
    }
}
