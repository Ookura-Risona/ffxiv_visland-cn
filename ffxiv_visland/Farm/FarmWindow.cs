using Dalamud.Interface.Utility.Raii;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets2;
using visland.Helpers;

namespace visland.Farm;

public unsafe class FarmWindow : UIAttachedWindow
{
    private FarmConfig _config;
    private FarmDebug _debug = new();

    public FarmWindow() : base("耕地自动化", "MJIFarmManagement", new(400, 600))
    {
        _config = Service.Config.Get<FarmConfig>();
    }

    public override void OnOpen()
    {
        if (_config.Collect != CollectStrategy.Manual)
        {
            var state = CalculateCollectResult();
            if (state == CollectResult.CanCollectSafely || _config.Collect == CollectStrategy.FullAuto && state == CollectResult.CanCollectWithOvercap)
            {
                CollectAll();
            }
        }
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs)
        {
            using (var tab = ImRaii.TabItem("主界面"))
                if (tab)
                    DrawMain();
            using (var tab = ImRaii.TabItem("Debug"))
                if (tab)
                    _debug.Draw();
        }
    }

    private void DrawMain()
    {
        if (UICombo.Enum("自动收取", ref _config.Collect))
            _config.NotifyModified();
        ImGui.Separator();

        var mji = MJIManager.Instance();
        var agent = AgentMJIFarmManagement.Instance();
        if (mji == null || mji->FarmState == null || mji->IslandState.Farm.EligibleForCare == 0 || agent == null)
        {
            ImGui.TextUnformatted("自走人偶当前不可用");
            return;
        }

        DrawGlobalOperations();
        ImGui.Separator();
        DrawPlotOperations();
    }

    private void DrawGlobalOperations()
    {
        var res = CalculateCollectResult();
        if (res != CollectResult.NothingToCollect)
        {
            // if there's uncollected stuff - propose to collect everything
            using (ImRaii.Disabled(res == CollectResult.EverythingCapped))
            {
                if (ImGui.Button("收取全部"))
                    CollectAll();
                if (res != CollectResult.CanCollectSafely)
                {
                    ImGui.SameLine();
                    using (ImRaii.PushColor(ImGuiCol.Text, 0xff0000ff))
                        ImGuiEx.TextV(res == CollectResult.EverythingCapped ? "背包已满!" : "警告: 部分资源即将超限!");
                }
            }
        }
        else
        {
            bool canDismiss = false, canEntrust = false;
            var agent = AgentMJIFarmManagement.Instance();
            for (int i = 0; i < agent->NumSlots; ++i)
            {
                bool cared = agent->SlotsSpan[i].UnderCare;
                canDismiss |= cared;
                canEntrust |= !cared && agent->SlotsSpan[i].SeedItemId != 0;
            }

            using (ImRaii.Disabled(!canDismiss))
                if (ImGui.Button("取消托管全部"))
                    DismissAll();
            ImGui.SameLine();
            using (ImRaii.Disabled(!canEntrust))
                if (ImGui.Button("托管全部"))
                    EntrustAll();
        }
    }

    private void DrawPlotOperations()
    {
        using var table = ImRaii.Table("table", 2);
        if (table)
        {
            ImGui.TableSetupColumn("Slot");
            ImGui.TableSetupColumn("Operations");
            ImGui.TableHeadersRow();

            var agent = AgentMJIFarmManagement.Instance();
            for (int i = 0; i < agent->NumSlots; ++i)
            {
                ref var slot = ref agent->SlotsSpan[i];
                var inventory = Utils.NumItems(slot.YieldItemId);
                bool overcap = inventory + slot.YieldAvailable > 999;
                bool full = inventory == 999;

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, full ? 0xff0000ff : 0xff00ffff, overcap))
                    ImGuiEx.TextV($"{slot.YieldName}: {inventory} + {slot.YieldAvailable} / 999");

                ImGui.TableNextColumn();
                if (slot.YieldAvailable > 0)
                {
                    using (ImRaii.Disabled(full))
                    {
                        if (ImGui.Button($"收取##{i}"))
                            CollectOne(i, false);
                        ImGui.SameLine();
                        if (ImGui.Button($"收取并取消托管##{i}"))
                            CollectOne(i, true);
                    }
                }
                else if (slot.UnderCare)
                {
                    if (ImGui.Button($"取消托管##{i}"))
                        DismissOne(i);
                }
                else if (slot.SeedItemId != 0)
                {
                    if (slot.WasUnderCare || Utils.NumCowries() >= 5)
                    {
                        if (ImGui.Button($"托管##{i}"))
                            EntrustOne(i, slot.SeedItemId);
                    }
                    // else: not enough cowries
                }
                // TODO: else - choose what to plant?
            }
        }
    }

    private CollectResult CalculateCollectResult()
    {
        var agent = AgentMJIFarmManagement.Instance();
        var mji = MJIManager.Instance();
        if (agent == null || agent->TotalAvailableYield <= 0 || mji == null || mji->FarmState == null)
            return CollectResult.NothingToCollect;

        var sheet = Service.LuminaGameData.GetExcelSheet<MJICropSeed>()!;
        var perCropYield = new int[sheet.RowCount];
        for (int i = 0; i < 20; ++i)
        {
            var seed = mji->FarmState->SeedType[i];
            if (seed != 0)
            {
                perCropYield[seed] += mji->FarmState->GardenerYield[i];
            }
        }

        bool anyOvercap = false;
        bool allFull = true;
        for (int i = 1; i < perCropYield.Length; ++i)
        {
            if (perCropYield[i] == 0)
                continue;

            var inventory = Utils.NumItems(sheet.GetRow((uint)i)!.Item.Row);
            allFull &= inventory >= 999;
            anyOvercap |= inventory + perCropYield[i] > 999;
        }
        return allFull ? CollectResult.EverythingCapped : anyOvercap ? CollectResult.CanCollectWithOvercap : CollectResult.CanCollectSafely;
    }

    private void CollectOne(int slot, bool dismissAfter)
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            Service.Log.Info($"正在收取第 {slot} 格, 取消托管={dismissAfter}");
            if (dismissAfter)
                mji->FarmState->CollectSingleAndDismiss((uint)slot);
            else
                mji->FarmState->CollectSingle((uint)slot);
        }
    }

    private void CollectAll()
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            Service.Log.Info("正在从耕地收取所有物品");
            mji->FarmState->UpdateExpectedTotalYield();
            mji->FarmState->CollectAll(true);
        }
    }

    private void DismissOne(int slot)
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            Service.Log.Info($"正在取消托管第 {slot} 格");
            mji->FarmState->Dismiss((uint)slot);
        }
    }

    private void DismissAll()
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            for (int i = 0; i < 20; ++i)
            {
                if (mji->FarmState->FarmSlotFlagsSpan[i].HasFlag(FarmSlotFlags.UnderCare))
                    mji->FarmState->Dismiss((uint)i);
            }
        }
    }

    private void EntrustOne(int slot, uint seedId)
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            mji->FarmState->Entrust((uint)slot, seedId);
        }
    }

    private void EntrustAll()
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            for (int i = 0; i < 20; ++i)
            {
                var seed = mji->FarmState->SeedType[i];
                if (seed != 0 && !mji->FarmState->FarmSlotFlagsSpan[i].HasFlag(FarmSlotFlags.UnderCare))
                {
                    mji->FarmState->Entrust((uint)i, mji->FarmState->SeedItemIds.Span[seed]);
                }
            }
        }
    }
}
