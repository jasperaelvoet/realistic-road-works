using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game;

namespace RealisticRoadWorks.Dev
{
    // Reads ModsData/RealisticRoadWorks/cmd.txt every frame, runs each line as a dev command, deletes the file.
    [RegisterSystem(SystemUpdatePhase.MainLoop)]
    public partial class DevCommandSystem : GameSystemBase
    {
        private string m_File;
        private Dictionary<string, IDevCommand> m_Commands;
        private DevContext m_Context;

        protected override void OnCreate()
        {
            base.OnCreate();
            string dir = Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", "RealisticRoadWorks");
            Directory.CreateDirectory(dir);
            m_File = Path.Combine(dir, "cmd.txt");
            // A duplicate name (two modules registering the same command) keeps the first and warns, instead of throwing
            // here and disabling every dev command.
            m_Commands = new Dictionary<string, IDevCommand>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in typeof(Mod).Assembly.GetTypes()
                .Where(t => typeof(IDevCommand).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface && t.GetConstructor(Type.EmptyTypes) != null)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .Select(t => (IDevCommand)Activator.CreateInstance(t)))
            {
                if (c.Name == null) continue;
                if (m_Commands.TryGetValue(c.Name, out var first))
                    Mod.Log.Warn($"dev command '{c.Name}' registered twice ({first.GetType().FullName} kept, {c.GetType().FullName} ignored)");
                else m_Commands.Add(c.Name, c);
            }
            m_Context = new DevContext { World = World, EntityManager = EntityManager };
            Mod.Log.Info($"dev commands: {string.Join(", ", m_Commands.Keys.OrderBy(k => k))}");
        }

        protected override void OnUpdate()
        {
            if (!File.Exists(m_File)) return;
            string[] lines;
            try { lines = File.ReadAllLines(m_File); File.Delete(m_File); }
            catch { return; }
            foreach (var raw in lines)
            {
                var parts = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                if (parts[0] == "help")
                {
                    foreach (var c in m_Commands.Values.OrderBy(c => c.Name)) Mod.Log.Info($"dev help {c.Name}: {c.Help}");
                    continue;
                }
                if (!m_Commands.TryGetValue(parts[0], out var cmd)) { Mod.Log.Warn($"dev unknown command '{parts[0]}'"); continue; }
                try { cmd.Run(m_Context, parts.Skip(1).ToArray()); }
                catch (Exception e) { Mod.Log.Warn($"dev '{raw}' failed: {e}"); }
            }
        }
    }
}
