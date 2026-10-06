using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.UI.Menu;

namespace RealisticRoadWorks.Dev
{
    // Saves the running city under a new name through the menu's own save path (same metadata and preview as the Save Game
    // screen). Test cities only: the name must start with "rrw-uw" or "UWE" and must not exist yet, so a save is never overwritten.
    public class SaveGameCommand : IDevCommand
    {
        private static readonly Regex kAllowed = new Regex(@"^(rrw-uw|UWE)[A-Za-z0-9_-]{0,40}$");

        public string Name => "savegame";
        public string Help => "savegame <name> - save the city as a NEW save (names rrw-uw* / UWE* only, never overwrites); no menu needed";

        public void Run(DevContext ctx, string[] args)
        {
            if (args.Length != 1 || !kAllowed.IsMatch(args[0])) { ctx.Log("savegame: refused - name must match " + kAllowed); return; }
            string name = args[0];
            var menu = ctx.World.GetExistingSystemManaged<MenuUISystem>();
            if (menu == null) { ctx.Log("savegame: no MenuUISystem"); return; }
            var t = typeof(MenuUISystem);
            var savesBinding = t.GetField("m_SavesBinding", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(menu);
            var saves = savesBinding?.GetType().GetProperty("value")?.GetValue(savesBinding) as IEnumerable;
            if (saves == null) { ctx.Log("savegame: save list not readable - refused"); return; }
            foreach (var s in saves)
            {
                var dn = s.GetType().GetProperty("displayName")?.GetValue(s) as string;
                if (dn == name) { ctx.Log("savegame: '" + name + "' exists - refused (never overwrite)"); return; }
            }
            // The quick-save path renders its own preview from the main camera, so no menu screen has to be open. It saves under
            // the "last save name", which a normal save sets too.
            var lastName = t.GetField("m_LastSaveNameBinding", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(menu);
            var update = lastName?.GetType().GetMethod("Update", new[] { typeof(string) });
            var m = t.GetMethod("SafeQuickSave", BindingFlags.NonPublic | BindingFlags.Instance);
            if (update == null || m == null) { ctx.Log("savegame: quick-save path not found"); return; }
            update.Invoke(lastName, new object[] { name });
            m.Invoke(menu, null);
            ctx.Log("savegame: queued '" + name + "'");
        }
    }
}
