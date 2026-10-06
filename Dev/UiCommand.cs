using System.Reflection;
using Game.SceneFlow;

namespace RealisticRoadWorks.Dev
{
    // ui 0|1 - hide/show the whole game UI (same switch photo mode and the benchmark use), for clean video capture.
    public class UiCommand : IDevCommand
    {
        public string Name => "ui";
        public string Help => "ui 0|1 - hide (0) or show (1) the game UI (GameManager.userInterface.view.enabled)";

        public void Run(DevContext ctx, string[] args)
        {
            bool show = args.Length == 0 || args[0] != "0";
            var ui = GameManager.instance?.userInterface;
            if (ui == null) { ctx.Log("ui: no userInterface"); return; }
            var view = ui.GetType().GetProperty("view", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ui);
            var prop = view?.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) { ctx.Log("ui: view.enabled not found"); return; }
            prop.SetValue(view, show);
            ctx.Log("ui " + (show ? "shown" : "hidden"));
        }
    }
}
