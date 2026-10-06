using System;
using System.Linq;
using System.Reflection;
using Colossal.Logging;
using Game;
using Game.Modding;
using RealisticRoadWorks.Dev;
using RealisticRoadWorks.V3;

namespace RealisticRoadWorks
{
    public class Mod : IMod
    {
        public static readonly ILog Log = LogManager.GetLogger(nameof(RealisticRoadWorks)).SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info($"{nameof(OnLoad)} v3");
            // Settings first: systems are created (OnCreate) while they are registered below and may read them.
            try { RRWSettings.Register(this); }
            catch (Exception e) { Log.Error($"settings registration failed: {e}"); }
            RegisterAttributedSystems(updateSystem);
        }

        public void OnDispose()
        {
            Log.Info(nameof(OnDispose));
            try { RRWSettings.Unregister(); } catch (Exception e) { Log.Warn($"settings unregister failed: {e.Message}"); }
        }

        // Every system marked [RegisterSystem] is added to the game loop at the declared phase/ordering.
        private static void RegisterAttributedSystems(UpdateSystem updateSystem)
        {
            var types = typeof(Mod).Assembly.GetTypes()
                .Select(t => (t, a: t.GetCustomAttribute<RegisterSystemAttribute>()))
                .Where(x => x.a != null)
                .OrderBy(x => x.a.Order);
            foreach (var (t, a) in types)
            {
                try
                {
                    MethodInfo m;
                    if (a.Before != null)
                        m = Generic(nameof(UpdateSystem.UpdateBefore), 2).MakeGenericMethod(t, a.Before);
                    else if (a.After != null)
                        m = Generic(nameof(UpdateSystem.UpdateAfter), 2).MakeGenericMethod(t, a.After);
                    else
                        m = Generic(nameof(UpdateSystem.UpdateAt), 1).MakeGenericMethod(t);
                    m.Invoke(updateSystem, new object[] { a.Phase });
                    Log.Info($"Registered {t.Name} at {a.Phase}{(a.Before != null ? " before " + a.Before.Name : "")}{(a.After != null ? " after " + a.After.Name : "")}");
                }
                catch (Exception e)
                {
                    Log.Error($"Failed to register {t.Name}: {e}");
                }
            }
        }

        private static MethodInfo Generic(string name, int arity) =>
            typeof(UpdateSystem).GetMethods().First(m => m.Name == name && m.IsGenericMethodDefinition &&
                m.GetGenericArguments().Length == arity && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(SystemUpdatePhase));
    }
}
