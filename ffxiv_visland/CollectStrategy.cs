using System.ComponentModel;

namespace visland;

public enum CollectStrategy
{
    [Description("手动")]
    Manual,

    [Description("自动 (禁止超限)")]
    NoOvercap,

    [Description("自动 (允许超限)")]
    FullAuto,
}
