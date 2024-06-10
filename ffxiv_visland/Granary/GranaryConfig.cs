using System.ComponentModel;
using visland.Helpers;

namespace visland.Granary;

public class GranaryConfig : Configuration.Node
{
    public enum UpdateStrategy
    {
        [Description("手动")]
        Manual,

        [Description("自动 (目的地: 上次派遣地点)")]
        MaxCurrent,

        [Description("自动 (目的地: 数量最少的稀有资源)")]
        BestDifferent,

        [Description("自动 (目的地: 全部仓库中数量最少的稀有资源)")]
        BestSame,
    }

    public CollectStrategy Collect = CollectStrategy.Manual;
    public UpdateStrategy Reassign = UpdateStrategy.Manual;
}
