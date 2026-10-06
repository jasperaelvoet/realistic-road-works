#if DEVTOOLS
using RealisticRoadWorks.Dev;

namespace RealisticRoadWorks.V3.Tooling
{
    // rrw.tools.last - what the last road-tool apply and the last bulldozer click did.
    public sealed class ToolsLastCommand : IDevCommand
    {
        public string Name => "rrw.tools.last";
        public string Help => "rrw.tools.last - classification of the last tool apply (new/replaced/split/combine/upgrade) and the last bulldozer intercept";
        public void Run(DevContext ctx, string[] args)
        {
            ctx.Log("rrw tools apply: " + WorksTagSystem.LastSummary);
            ctx.Log("rrw tools preview-hidden temps classified since load=" + WorksTagSystem.TotalPreviewHiddenTemps
                    + " hidden on apply=" + WorksTagSystem.TotalHiddenOnApply + " (remnants of hidden works roads keep their site and stay hidden)");
            ctx.Log("rrw tools bulldoze: " + BulldozeInterceptSystem.LastSummary);
        }
    }
}
#endif
