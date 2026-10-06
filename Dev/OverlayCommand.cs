using Game;
using Game.Rendering;
using Game.Simulation;
namespace RealisticRoadWorks.Dev
{
    // Video-capture helpers: photo-mode style overlay switch (street names, notification icons) and a visual time-of-day override.
    // Both are latched and re-applied every frame: an autosave thumbnail (ScreenCaptureHelper) resets hideOverlay to false.
    internal static class DevLook
    {
        public static bool HideOverlay;
        public static float TimeOfDay = -1f;    // < 0: game clock
    }

    public class OverlayCommand : IDevCommand
    {
        public string Name => "overlay";
        public string Help => "overlay 0|1 - hide (0) or show (1) world overlays: street names, notification icons (RenderingSystem.hideOverlay, kept through autosaves)";
        public void Run(DevContext ctx, string[] args)
        {
            DevLook.HideOverlay = args.Length > 0 && args[0] == "0";
            ctx.World.GetExistingSystemManaged<RenderingSystem>().hideOverlay = DevLook.HideOverlay;
            ctx.Log("overlay " + (DevLook.HideOverlay ? "hidden" : "shown"));
        }
    }

    public class TimeOfDayCommand : IDevCommand
    {
        public string Name => "tod";
        public string Help => "tod <hour 0..24>|off - visual time of day (PlanetarySystem.overrideTime; the simulation clock keeps running)";
        public void Run(DevContext ctx, string[] args)
        {
            float h;
            DevLook.TimeOfDay = args.Length > 0 && float.TryParse(args[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out h) ? h : -1f;
            if (DevLook.TimeOfDay < 0f) ctx.World.GetExistingSystemManaged<PlanetarySystem>().overrideTime = false;
            ctx.Log(DevLook.TimeOfDay < 0f ? "tod off (game clock)" : "tod " + DevLook.TimeOfDay.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [RegisterSystem(SystemUpdatePhase.PreCulling)]
    public partial class DevLookSystem : GameSystemBase
    {
        private RenderingSystem m_Rendering;
        private PlanetarySystem m_Planetary;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Rendering = World.GetOrCreateSystemManaged<RenderingSystem>();
            m_Planetary = World.GetOrCreateSystemManaged<PlanetarySystem>();
        }

        protected override void OnUpdate()
        {
            if (DevLook.HideOverlay && !m_Rendering.hideOverlay) m_Rendering.hideOverlay = true;
            if (DevLook.TimeOfDay >= 0f)
            {
                m_Planetary.overrideTime = true;
                m_Planetary.time = DevLook.TimeOfDay;
            }
        }
    }
}
