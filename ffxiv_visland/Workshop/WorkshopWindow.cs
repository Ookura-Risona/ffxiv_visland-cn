using Dalamud.Interface.Utility.Raii;
using visland.Helpers;
using ImGuiNET;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace visland.Workshop;

unsafe class WorkshopWindow : UIAttachedWindow
{
    private WorkshopConfig _config;
    private WorkshopManual _manual = new();
    private WorkshopOCImport _oc = new();
    private WorkshopDebug _debug = new();

    public WorkshopWindow() : base("工房自动化", "MJICraftSchedule", new(500, 650))
    {
        _config = Service.Config.Get<WorkshopConfig>();
    }

    public override void PreOpenCheck()
    {
        base.PreOpenCheck();
        var agent = AgentMJICraftSchedule.Instance();
        IsOpen &= agent != null && agent->Data != null;

        _oc.Update();
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("Tabs");
        if (!tabs) return;
        using (var tab = ImRaii.TabItem("使用预设"))
            if (tab)
                _oc.Draw();
        using (var tab = ImRaii.TabItem("设置"))
            if (tab)
                DrawSettings();
    }

    public override void OnOpen()
    {
        if (_config.AutoOpenNextDay)
        {
            WorkshopUtils.SetCurrentCycle(AgentMJICraftSchedule.Instance()->Data->CycleInProgress + 1);
        }
        if (_config.AutoImport)
        {
            _oc.ImportRecsFromClipboard(true);
        }
    }

    private void DrawSettings()
    {
        if (ImGui.Checkbox("开启时自动选择下一周期", ref _config.AutoOpenNextDay))
            _config.NotifyModified();
        if (ImGui.Checkbox("开启窗口时自动尝试导入剪贴板内容", ref _config.AutoImport))
            _config.NotifyModified();
    }
}
