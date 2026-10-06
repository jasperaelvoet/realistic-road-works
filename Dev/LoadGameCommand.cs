using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Colossal;
using Colossal.Serialization.Entities;
using Game;
using Game.Assets;
using Game.City;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Menu;

namespace RealisticRoadWorks.Dev
{
    // Loads a test city by its save name through the same GameManager call as the Load Game screen, with the options stored in
    // the save. Test cities only: the name must be rrw-testtown or start with "rrw-uw" / "UWE"; autosaves are never picked.
    public class LoadGameCommand : IDevCommand
    {
        private static readonly Regex kAllowed = new Regex(@"^(rrw-testtown|(rrw-uw|UWE)[A-Za-z0-9_-]{0,40})$");

        public string Name => "loadgame";
        public string Help => "loadgame <name> - load a test save by name (rrw-testtown, rrw-uw*, UWE* only; never an autosave)";

        public void Run(DevContext ctx, string[] args)
        {
            if (args.Length != 1 || !kAllowed.IsMatch(args[0])) { ctx.Log("loadgame: refused - name must match " + kAllowed); return; }
            string name = args[0];
            var menu = ctx.World.GetExistingSystemManaged<MenuUISystem>();
            var savesBinding = typeof(MenuUISystem).GetField("m_SavesBinding", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(menu);
            var saves = savesBinding?.GetType().GetProperty("value")?.GetValue(savesBinding) as IEnumerable;
            if (saves == null) { ctx.Log("loadgame: save list not readable"); return; }
            SaveInfo found = null;
            int matches = 0;
            foreach (var o in saves)
            {
                if (o is SaveInfo s && !s.autoSave && s.displayName == name) { found = found ?? s; matches++; }
            }
            if (found == null) { ctx.Log("loadgame: no save named '" + name + "'"); return; }
            var map = ctx.World.GetExistingSystemManaged<MapMetadataSystem>();
            map.mapName = found.mapName;
            map.prefabReferences = found.prefabReferences;
            var city = ctx.World.GetExistingSystemManaged<CityConfigurationSystem>();
            city.overrideLoadedOptions = false;
            city.overrideThemeName = null;
            var meta = found.metaData;
            TaskManager.instance.EnqueueTask("SaveLoadGame", () => GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, meta), 1);
            ctx.Log("loadgame: loading '" + name + "'" + (matches > 1 ? " (" + matches + " saves share that name; first one)" : ""));
        }
    }
}
