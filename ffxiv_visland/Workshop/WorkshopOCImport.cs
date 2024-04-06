using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ImGuiNET;
using Lumina.Data;
using Lumina.Excel;
using Lumina.Excel.GeneratedSheets;
using visland.Helpers;

namespace visland.Workshop;

public unsafe class WorkshopOCImport
{
    public WorkshopSolver.Recs Recommendations = new();

    private readonly WorkshopConfig _config;
    private readonly ExcelSheet<MJICraftworksObject> _craftSheet;
    private readonly List<string> _botNames;
    private readonly List<Func<bool>> _pendingActions = [];
    private bool IgnoreFourthWorkshop;

    public WorkshopOCImport()
    {
        _config = Service.Config.Get<WorkshopConfig>();
        _craftSheet = Service.DataManager.GetExcelSheet<MJICraftworksObject>()!;
        _botNames = _craftSheet.Select(r =>
                OfficialNameToBotName((r.Item.GetDifferentLanguage(ClientLanguage.English).Value?.Name.RawString ?? r.Item.Value?.Name.RawString)!))
            .ToList();
    }

    public void Update()
    {
        var numDone = _pendingActions.TakeWhile(f => f()).Count();
        _pendingActions.RemoveRange(0, numDone);
    }

    public void Draw()
    {
        using var
            globalDisable =
                ImRaii.Disabled(_pendingActions.Count >
                                0); // disallow any manipulations while delayed actions are in progress

        ImGui.TextColored(ImGuiColors.DalamudYellow, "从剪贴板导入");

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("原理为检测剪贴板内每一行的物品名 (不包含诸如海岛, 开拓工房之类的前缀词), 然后解析为对应预设");

        ImGui.Separator();

        ImGui.AlignTextToFramePadding();
        ImGui.Text("来源:");

        ImGui.SameLine();
        if (ImGui.Button("Overseas Casuals (国际服)"))
            ImportRecsFromClipboard(false);

        ImGui.SameLine();
        if (ImGui.Button("静态作业 (国服)"))
        {
            try
            {
                var staticSchedule = Regex.Replace(ImGui.GetClipboardText().Trim(), @"\[[^\]]*\]|.+收益.+", "", RegexOptions.Multiline);
                staticSchedule = Regex.Replace(staticSchedule, @"^(?=\d\.)", "Cycle ", RegexOptions.Multiline).Replace('.', '\n');
                staticSchedule = staticSchedule
                    .Split('\n')
                    .Where(i => !string.IsNullOrWhiteSpace(i))
                    .Aggregate((i, j) => i + "\n" + j)
                    .Replace('、', '\n');
                Recommendations = ParseRecs(staticSchedule);
            }
            catch (Exception ex)
            {
                ReportError($"错误: {ex.Message}");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("动态作业 (国服)"))
        {
            try
            {
                string repeat(string scheduleLine)
                {
                    var result = Regex.Match(scheduleLine, @"(?<times>\d)[×x](?<things>.+)", RegexOptions.Compiled);
                    if (!result.Success)
                    {
                        result = Regex.Match(scheduleLine, @"(?<things>.+)[×x](?<times>\d)", RegexOptions.Compiled);
                    }
                    if (!result.Success)
                    {
                        return scheduleLine;
                    }
                    var times = int.Parse(result.Groups["times"].Value);
                    var things = result.Groups["things"].Value.Trim();
                    return string.Join("\n", Enumerable.Repeat(things, times));
                }

                var staticSchedule = Regex.Replace(ImGui.GetClipboardText().Trim(), @"\[[^\]]*\]|.+收益.+", "", RegexOptions.Multiline);
                staticSchedule = Regex.Replace(staticSchedule, @"^(?=\d\.)", "Cycle ", RegexOptions.Multiline).Replace('.', '\n');
                staticSchedule = staticSchedule
                    .Split('\n')
                    .Where(i => !string.IsNullOrWhiteSpace(i))
                    .Select(repeat)
                    .Aggregate((i, j) => i + "\n" + j)
                    .Replace('、', '\n');
                Recommendations = ParseRecs(staticSchedule);
            }
            catch (Exception ex)
            {
                ReportError($"错误: {ex.Message}");
            }
        }

        ImGuiComponents.HelpMarker("用于从剪贴板导入来自 蜡笔桶 的静态工房日程安排\n" +
                                   "你可以通过点击本按钮来打开对应的腾讯文档页面");

        if (ImGui.IsItemClicked())
            Util.OpenLink("https://docs.qq.com/doc/DTUNRZkJjTVhvT2Nv");

        if (Recommendations.Empty)
            return;

        ImGui.Dummy(new(12));

        if (!_config.UseFavorSolver)
        {
            ImGui.TextUnformatted("推荐方案");
            ImGuiComponents.HelpMarker("点击 \"本周推荐\" 或 \"下周推荐\" 按钮以生成一个用于生成自定义作业的 OC 服务器机器人文本指令.\n" +
                                       "然后点击 #bot-spam 按钮以打开 Discord 并切换到指定频道, 粘贴并发送命令, 然后复制机器人所输出的文本.\n" +
                                       "最后点击 \"Override 4th workshop\" 按钮以将常规推荐换为求解器推荐方案");

            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Clipboard, "本周推荐"))
                ImGui.SetClipboardText(CreateFavorRequestCommand(false));
            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Clipboard, "下周推荐"))
                ImGui.SetClipboardText(CreateFavorRequestCommand(true));

            if (ImGui.Button("Overseas Casuals > #bot-spam"))
                Util.OpenLink("discord://discord.com/channels/1034534280757522442/1034985297391407126");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                Util.OpenLink("https://discord.com/channels/1034534280757522442/1034985297391407126");
            ImGuiComponents.HelpMarker("\uE051: Discord app\n\uE052: Discord 浏览器页面");

            ImGui.Text("将剪贴板中的方案应用于:");

            if (ImGui.Button("1-3号工房的生产安排"))
                OverrideSideRecsAsapClipboard();
            if (ImGui.Button("4号工房的生产安排"))
                OverrideSideRecsLastWorkshopClipboard();
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "使用求解器方案:");

            ImGui.Separator();

            ImGui.BeginGroup();
            ImGuiEx.TextV("1 - 3 号工房:");

            ImGuiEx.TextV("4 号工房:");
            ImGui.EndGroup();

            ImGui.SameLine();

            ImGui.BeginGroup();
            if (ImGui.Button("本周##asap"))
                OverrideSideRecsAsapSolver(false);
            ImGui.SameLine();
            if (ImGui.Button("下周##asap"))
                OverrideSideRecsAsapSolver(true);

            if (ImGui.Button("本周##4th"))
                OverrideSideRecsLastWorkshopSolver(false);
            ImGui.SameLine();
            if (ImGui.Button("下周##4th"))
                OverrideSideRecsLastWorkshopSolver(true);
            ImGui.EndGroup();
        }

        ImGui.Dummy(new(12));

        ImGui.TextColored(ImGuiColors.DalamudYellow, "应用生产安排:");

        ImGui.Separator();

        if (ImGui.Button("    本周    "))
            ApplyRecommendations(false);
        ImGui.SameLine();
        if (ImGui.Button("    下周    "))
            ApplyRecommendations(true);
        ImGui.SameLine();
        ImGui.Checkbox("忽略 4 号工房", ref IgnoreFourthWorkshop);

        ImGui.Dummy(new(12));

        DrawCycleRecommendations();
    }

    public void ImportRecsFromClipboard(bool silent)
    {
        try
        {
            Recommendations = ParseRecs(ImGui.GetClipboardText());
        }
        catch (Exception ex)
        {
            ReportError($"错误: {ex.Message}", silent);
        }
    }

    private void DrawCycleRecommendations()
    {
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.NoKeepColumnsVisible;
        var maxWorkshops = WorkshopUtils.GetMaxWorkshops();

        using var scrollSection = ImRaii.Child("ScrollableSection");
        foreach (var (c, r) in Recommendations.Enumerate())
        {
            ImGuiEx.TextV($"第 {c} 天:");
            ImGui.SameLine();
            if (ImGui.Button($"应用##{c}"))
                ApplyRecommendationToCurrentCycle(r);

            using var outerTable = ImRaii.Table($"table_{c}", r.Workshops.Count, tableFlags);
            if (outerTable)
            {
                var workshopLimit = r.Workshops.Count - (IgnoreFourthWorkshop && r.Workshops.Count > 1 ? 1 : 0);
                if (r.Workshops.Count <= 1)
                {
                    ImGui.TableSetupColumn(IgnoreFourthWorkshop ? $"工房 1-{maxWorkshops - 1}" : "所有工房");
                }
                else if (r.Workshops.Count < maxWorkshops)
                {
                    var numDuplicates = 1 + maxWorkshops - r.Workshops.Count;
                    ImGui.TableSetupColumn($"工房 1-{numDuplicates}");
                    for (var i = 1; i < workshopLimit; ++i)
                        ImGui.TableSetupColumn($"工房 {i + numDuplicates}");
                }
                else
                {
                    // favors
                    for (var i = 0; i < workshopLimit; ++i)
                        ImGui.TableSetupColumn($"工房 {i + 1}");
                }

                ImGui.TableHeadersRow();

                ImGui.TableNextRow();
                for (var i = 0; i < workshopLimit; ++i)
                {
                    ImGui.TableNextColumn();
                    using var innerTable = ImRaii.Table($"table_{c}_{i}", 2, tableFlags);
                    if (innerTable)
                    {
                        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
                        foreach (var rec in r.Workshops[i].Slots)
                        {
                            ImGui.TableNextRow();

                            ImGui.TableNextColumn();
                            var iconSize = ImGui.GetTextLineHeight() * 1.5f;
                            var iconSizeVec = new Vector2(iconSize, iconSize);
                            var craftworkItemIcon = _craftSheet.GetRow(rec.CraftObjectId)!.Item.Value!.Icon;
                            ImGui.Image(Service.TextureProvider.GetIcon(craftworkItemIcon)!.ImGuiHandle, iconSizeVec,
                                Vector2.Zero, Vector2.One);

                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(_botNames[(int)rec.CraftObjectId]);
                        }
                    }
                }
            }
        }
    }

    private string CreateFavorRequestCommand(bool nextWeek)
    {
        var state = MJIManager.Instance()->FavorState;
        if (state == null || state->UpdateState != 2)
        {
            ReportError($"方案数据无效: {state->UpdateState}");
            return "";
        }

        var sheetCraft = Service.LuminaGameData.GetExcelSheet<MJICraftworksObject>(Language.English)!;
        var res = "/favors";
        var offset = nextWeek ? 6 : 3;
        for (var i = 0; i < 3; ++i)
        {
            var id = state->CraftObjectIds[offset + i];
            // the bot doesn't like names with apostrophes because it "breaks their formulas"
            var name = sheetCraft.GetRow(id)?.Item.Value?.Name;
            if (name != null)
                res += $" favor{i + 1}:{_botNames[id].Replace("\'", "")}";
        }

        return res;
    }

    private void OverrideSideRecsLastWorkshopClipboard()
    {
        try
        {
            var overrideRecs = ParseRecOverrides(ImGui.GetClipboardText());
            if (overrideRecs.Count > Recommendations.Schedules.Count)
                throw new Exception($"覆盖列表安排超出了时间范围: {overrideRecs.Count} > {Recommendations.Schedules.Count}");
            OverrideSideRecsLastWorkshop(overrideRecs);
        }
        catch (Exception ex)
        {
            ReportError($"错误: {ex.Message}");
        }
    }

    private void OverrideSideRecsLastWorkshopSolver(bool nextWeek)
    {
        EnsureDemandFavorsAvailable();
        _pendingActions.Add(() =>
        {
            OverrideSideRecsLastWorkshop(SolveRecOverrides(nextWeek));
            return true;
        });
    }

    private void OverrideSideRecsLastWorkshop(List<WorkshopSolver.WorkshopRec> overrides)
    {
        foreach (var (r, o) in Recommendations.Schedules.Zip(overrides))
        {
            // if base recs have >1 workshop, remove last (assume we always want to override 4th workshop)
            if (r.Workshops.Count > 1)
                r.Workshops.RemoveAt(r.Workshops.Count - 1);
            // and add current override as a schedule for last workshop
            r.Workshops.Add(o);
        }

        if (overrides.Count > Recommendations.Schedules.Count)
            Service.ChatGui.Print("警告: 未能成功导入所有生产安排", "visland");
    }

    private void OverrideSideRecsAsapClipboard()
    {
        try
        {
            var overrideRecs = ParseRecOverrides(ImGui.GetClipboardText());
            if (overrideRecs.Count > Recommendations.Schedules.Count * 4)
                throw new Exception($"覆盖列表安排超出了时间范围: {overrideRecs.Count} > 4 * {Recommendations.Schedules.Count}");
            OverrideSideRecsAsap(overrideRecs);
        }
        catch (Exception ex)
        {
            ReportError($"错误: {ex.Message}");
        }
    }

    private void OverrideSideRecsAsapSolver(bool nextWeek)
    {
        EnsureDemandFavorsAvailable();
        _pendingActions.Add(() =>
        {
            OverrideSideRecsAsap(SolveRecOverrides(nextWeek));
            return true;
        });
    }

    private void OverrideSideRecsAsap(List<WorkshopSolver.WorkshopRec> overrides)
    {
        var nextOverride = 0;
        foreach (var r in Recommendations.Schedules)
        {
            var batchSize = Math.Min(4, overrides.Count - nextOverride);
            if (batchSize == 0)
                break; // nothing left to override

            // if base recs have >1 workshop, remove last (assume we always want to override 4th workshop)
            if (r.Workshops.Count > 1)
                r.Workshops.RemoveAt(r.Workshops.Count - 1);
            var maxLeft = 4 - batchSize;
            if (r.Workshops.Count > maxLeft)
                r.Workshops.RemoveRange(maxLeft, r.Workshops.Count - maxLeft);
            r.Workshops.AddRange(overrides.Skip(nextOverride).Take(batchSize));
            nextOverride += batchSize;
        }

        if (nextOverride < overrides.Count)
            Service.ChatGui.Print("警告: 未能成功导入所有生产安排", "visland");
    }

    private WorkshopSolver.Recs ParseRecs(string str)
    {
        var result = new WorkshopSolver.Recs();
        var curRec = new WorkshopSolver.DayRec();

        var nextSlot = 24;
        var curCycle = 0;

        foreach (var l in str.Split('\n', '\r'))
            if (TryParseCycleStart(l, out var cycle))
            {
                // complete previous cycle; if the number was not known, assume it is next cycle - 1
                result.Add(curCycle > 0 ? curCycle : cycle - 1, curRec);
                curRec = new WorkshopSolver.DayRec();
                nextSlot = 24;
                curCycle = cycle;
            }
            else switch (l)
            {
                case "First 3 Workshops":
                case "All Workshops":
                {
                    // just a sanity check...
                    if (!curRec.Empty)
                        throw new Exception("Unexpected start of 1st workshop recs");
                    break;
                }
                case "4th Workshop":
                    // ensure next item goes into new rec list
                    // TODO: do we want to add an extra empty list if this is the first line?..
                    nextSlot = 24;
                    break;
                default:
                {
                    if (TryParseItem(l) is { } item)
                    {
                        if (nextSlot + item.CraftingTime > 24)
                        {
                            // start next workshop schedule
                            curRec.Workshops.Add(new WorkshopSolver.WorkshopRec());
                            nextSlot = 0;
                        }

                        curRec.Workshops.Last().Add(nextSlot, item.RowId);
                        nextSlot += item.CraftingTime;
                    }

                    break;
                }
            }

        // complete current cycle; if the number was not known, assume it is tomorrow.
        // On the 7th day, importing a rec will assume the next week, but we can't import into the next week so just modulo it to the first week. Theoretically shouldn't cause problems.
        result.Add(curCycle > 0 ? curCycle : (AgentMJICraftSchedule.Instance()->Data->CycleInProgress + 2) % 7, curRec);

        return result;
    }

    private static bool TryParseCycleStart(string str, out int cycle)
    {
        // OC has two formats:
        // - single day recs are 'Season N (mmm dd-dd), Cycle C Recommendations'
        // - multi day recs are 'Season N (mmm dd-dd) Cycle K-L Recommendations' followed by 'Cycle C'
        if (str.StartsWith("Cycle ")) return int.TryParse(str.Substring(6, 1), out cycle);

        if (str.StartsWith("Season ") && str.IndexOf(", Cycle ") is var cycleStart && cycleStart > 0)
        {
            return int.TryParse(str.Substring(cycleStart + 8, 1), out cycle);
        }

        cycle = 0;
        return false;
    }

    private MJICraftworksObject? TryParseItem(string line)
    {
        var matchingRows = _botNames.Select((n, i) => (n, i))
            .Where(t => !string.IsNullOrEmpty(t.n) && IsMatch(line, t.n)).ToList();
        if (matchingRows.Count <= 1)
            return matchingRows.Count > 0 ? _craftSheet.GetRow((uint)matchingRows.First().i) : null;
        {
            matchingRows = matchingRows.OrderByDescending(t => MatchingScore(t.n, line)).ToList();
            Service.Log.Info(
                $"Row '{line}' matches {matchingRows.Count} items: {string.Join(", ", matchingRows.Select(r => r.n))}\n" +
                "First one is most likely the correct match. Please report if this is wrong.");
        }

        return matchingRows.Count > 0 ? _craftSheet.GetRow((uint)matchingRows.First().i) : null;
    }


    private static bool IsMatch(string x, string y)
    {
        return Regex.IsMatch(x, $@"\b{Regex.Escape(y)}\b");
    }

    private static object MatchingScore(string item, string line)
    {
        var score = 0;

        // primitive matching based on how long the string matches. Enough for now but could need expanding later
        if (line.Contains(item))
            score += item.Length;

        return score;
    }

    private List<WorkshopSolver.WorkshopRec> ParseRecOverrides(string str)
    {
        var result = new List<WorkshopSolver.WorkshopRec>();
        var nextSlot = 24;

        foreach (var l in str.Split('\n', '\r'))
            if (l.StartsWith("Schedule #"))
            {
                // ensure next item goes into new rec list
                nextSlot = 24;
            }
            else if (TryParseItem(l) is var item && item != null)
            {
                if (nextSlot + item.CraftingTime > 24)
                {
                    // start next workshop schedule
                    result.Add(new WorkshopSolver.WorkshopRec());
                    nextSlot = 0;
                }

                result.Last().Add(nextSlot, item.RowId);
                nextSlot += item.CraftingTime;
            }

        return result;
    }

    private List<WorkshopSolver.WorkshopRec> SolveRecOverrides(bool nextWeek)
    {
        var mji = MJIManager.Instance();
        if (mji->IsPlayerInSanctuary == 0) return [];
        var state = new WorkshopSolver.FavorState();
        var offset = nextWeek ? 6 : 3;
        for (var i = 0; i < 3; ++i)
        {
            state.CraftObjectIds[i] = mji->FavorState->CraftObjectIds[i + offset];
            state.CompletedCounts[i] =
                mji->FavorState->NumDelivered[i + offset] + mji->FavorState->NumScheduled[i + offset];
        }

        if (!mji->DemandDirty) state.Popularity.Set(nextWeek ? mji->NextPopularity : mji->CurrentPopularity);

        try
        {
            return new WorkshopSolverFavorSheet(state).Recs;
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
            Service.Log.Error(
                $"Current favors: {state.CraftObjectIds[0]} #{state.CompletedCounts[0]}, {state.CraftObjectIds[1]} #{state.CompletedCounts[1]}, {state.CraftObjectIds[2]} #{state.CompletedCounts[2]}");
            return [];
        }
    }

    public static string OfficialNameToBotName(string name)
    {
        if (name.StartsWith("海岛"))
            return name.Remove(0, 2);
        if (name.StartsWith("开拓工房"))
            return name.Remove(0, 4);
        return name;
    }


    private void EnsureDemandFavorsAvailable()
    {
        if (MJIManager.Instance()->DemandDirty)
        {
            WorkshopUtils.RequestDemandFavors();
            _pendingActions.Add(() =>
                !MJIManager.Instance()->DemandDirty && MJIManager.Instance()->FavorState->UpdateState == 2);
        }
    }

    private void ApplyRecommendation(int cycle, WorkshopSolver.DayRec rec)
    {
        var maxWorkshops = WorkshopUtils.GetMaxWorkshops();
        foreach (var w in rec.Enumerate(maxWorkshops))
            if (!IgnoreFourthWorkshop || w.workshop < maxWorkshops - 1)
                foreach (var r in w.rec.Slots)
                    WorkshopUtils.ScheduleItemToWorkshop(r.CraftObjectId, r.Slot, cycle, w.workshop);
    }

    private void ApplyRecommendationToCurrentCycle(WorkshopSolver.DayRec rec)
    {
        var cycle = AgentMJICraftSchedule.Instance()->Data->CycleDisplayed;
        ApplyRecommendation(cycle, rec);
        WorkshopUtils.ResetCurrentCycleToRefreshUI();
    }

    private void ApplyRecommendations(bool nextWeek)
    {
        // TODO: clear recs!

        try
        {
            var agentData = AgentMJICraftSchedule.Instance()->Data;
            if (Recommendations.Schedules.Count > 5)
                throw new Exception($"Too many days in recs: {Recommendations.Schedules.Count}");

            var forbiddenCycles = nextWeek ? 0 : (1u << (agentData->CycleInProgress + 1)) - 1;
            if ((Recommendations.CyclesMask & forbiddenCycles) != 0)
                throw new Exception("Some of the cycles in schedule are already in progress or are done");

            var currentRestCycles = nextWeek ? agentData->RestCycles >> 7 : agentData->RestCycles & 0x7F;
            if ((currentRestCycles & Recommendations.CyclesMask) != 0)
            {
                // we need to change rest cycles - set to C1 and last unused
                var freeCycles = ~Recommendations.CyclesMask & 0x7F;
                if ((freeCycles & 1) == 0)
                    throw new Exception(
                        "Sorry, we assume C1 is always rest - set rest days manually to match your schedule");
                var rest = (1u << (31 - BitOperations.LeadingZeroCount(freeCycles))) | 1;
                if (BitOperations.PopCount(rest) != 2)
                    throw new Exception("Something went wrong, failed to determine rest days");

                var changedRest = rest ^ currentRestCycles;
                if ((changedRest & forbiddenCycles) != 0)
                    throw new Exception(
                        "Can't apply this schedule: it would require changing rest days for cycles that are in progress or already done");

                var newRest = nextWeek
                    ? (rest << 7) | (agentData->RestCycles & 0x7F)
                    : (agentData->RestCycles & 0x3F80) | rest;
                WorkshopUtils.SetRestCycles(newRest);
            }

            var cycle = agentData->CycleDisplayed;
            foreach (var (c, r) in Recommendations.Enumerate())
                ApplyRecommendation(c - 1 + (nextWeek ? 7 : 0), r);
            WorkshopUtils.ResetCurrentCycleToRefreshUI();
        }
        catch (Exception ex)
        {
            ReportError($"Error: {ex.Message}");
        }
    }

    private static void ReportError(string msg, bool silent = false)
    {
        Service.Log.Error(msg);
        if (!silent)
            Service.ChatGui.PrintError(msg);
    }
}