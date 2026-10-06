using System;
using Game;

namespace RealisticRoadWorks.Dev
{
    // Put on a GameSystemBase subclass to have Mod.OnLoad register it. Use Before OR After (or neither = UpdateAt).
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RegisterSystemAttribute : Attribute
    {
        public SystemUpdatePhase Phase { get; }
        public Type Before { get; set; }
        public Type After { get; set; }
        public int Order { get; set; }
        public RegisterSystemAttribute(SystemUpdatePhase phase) { Phase = phase; }
    }
}
