using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using ECommons;
using Dalamud;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ImGuiNET;
using Lumina.Data;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using visland.Helpers;
using Dalamud.Game;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;

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
        _craftSheet = GenericHelpers.GetSheet<MJICraftworksObject>(); // unlocalised sheet can't be fetched in english
        _botNames = _craftSheet.Select(r => OfficialNameToBotName(GenericHelpers.GetRow<Item>(r.Item.RowId, ClientLanguage.English)!.Value.Name.ExtractText())).ToList();
    }


    public void Update()
    {
        var numDone = _pendingActions.TakeWhile(f => f()).Count();
        _pendingActions.RemoveRange(0, numDone);
    }

    public void Draw()
    {
        using var globalDisable = ImRaii.Disabled(_pendingActions.Count > 0);

        ImGui.TextColored(ImGuiColors.DalamudYellow, "从剪贴板导入 (仅支持国服作业)");
        ImGui.Separator();

        ImGui.AlignTextToFramePadding();
        ImGui.Text("来源:");

        ImGui.SameLine();
        if (ImGui.Button("3 工房版本"))
            ProcessFormatThreeWorkshops(false);

        ImGui.SameLine();
        if (ImGui.Button("4 工房版本"))
            ProcessFormatFourWorkshops(false);

        ImGui.SameLine();
        ImGui.TextDisabled("|");

        ImGui.SameLine();
        if (ImGui.Button("国服作业集 (蜡笔桶)"))
            Util.OpenLink("https://docs.qq.com/doc/DTUNRZkJjTVhvT2Nv");

        if (Recommendations.Empty) return;

        ImGui.Dummy(new(12));

        ImGui.TextColored(ImGuiColors.DalamudYellow, "批量应用");
        ImGui.Separator();

        if (ImGui.Button("    本周    "))
            ApplyRecommendations(false);

        ImGui.SameLine();
        if (ImGui.Button("    下周    "))
            ApplyRecommendations(true);

        ImGui.SameLine();
        ImGui.Checkbox("忽略 4 号工房", ref IgnoreFourthWorkshop);

        ImGui.SameLine();
        ImGui.TextDisabled("|");

        ImGui.SameLine();
        if (ImGui.Button("清空当前天生产安排"))
            Recommendations = new();

        ImGui.Dummy(new(12));

        ImGui.TextColored(ImGuiColors.DalamudYellow, "单独应用");
        ImGui.Separator();

        DrawCycleRecommendations();
    }

    public void ProcessFormatThreeWorkshops(bool isSilent = true)
    {
        try
        {
            var staticSchedule = ImGui.GetClipboardText().Trim();

            var pattern = @"D(\d+):(.+)";
            var matches = Regex.Matches(staticSchedule, pattern, RegexOptions.Multiline);

            var formattedSchedule = new StringBuilder();
            foreach (Match match in matches)
            {
                var day = match.Groups[1].Value;
                var tasks = match.Groups[2].Value.Trim();

                // 处理重复的任务
                tasks = Regex.Replace(tasks, @"(\d+)×", match => string.Join("、", Enumerable.Repeat("", int.Parse(match.Groups[1].Value))));

                formattedSchedule.AppendLine($"Cycle {day}");
                formattedSchedule.AppendLine(tasks.Replace("、", "\n"));
            }

            var result = formattedSchedule.ToString().Trim();
            Recommendations = ParseRecs(result);
        }
        catch (Exception ex)
        {
            ReportError($"错误: {ex.Message}", isSilent);
        }
    }

    public void ProcessFormatFourWorkshops(bool isSilent = true)
    {
        try
        {
            var staticSchedule = ImGui.GetClipboardText().Trim();

            var pattern = @"D(\d+):(.+)";
            var matches = Regex.Matches(staticSchedule, pattern, RegexOptions.Multiline);

            var formattedSchedule = new StringBuilder();
            var currentDay = "";

            foreach (Match match in matches)
            {
                var day = match.Groups[1].Value;
                var tasks = match.Groups[2].Value.Trim();

                if (day != currentDay)
                {
                    if (currentDay != "")
                        formattedSchedule.AppendLine();
                    formattedSchedule.AppendLine($"Cycle {day}");
                    currentDay = day;
                }

                if (tasks == "休息")
                {
                    formattedSchedule.AppendLine(tasks);
                    continue;
                }

                // 处理重复的任务
                tasks = Regex.Replace(tasks, @"(\d+)×", match => string.Join("、", Enumerable.Repeat("", int.Parse(match.Groups[1].Value))));

                // 分割任务并添加工房信息
                var taskList = tasks.Split('、');
                for (var i = 0; i < taskList.Length; i++)
                {
                    var workshop = (i == 0 && currentDay == day) ? "工房1-3: " : "工房4: ";
                    formattedSchedule.AppendLine(workshop + taskList[i]);
                }
            }

            var result = formattedSchedule.ToString().Trim();
            Recommendations = ParseRecs(result);
        }
        catch (Exception ex)
        {
            ReportError($"错误: {ex.Message}", isSilent);
        }
    }

    public void ImportRecsFromClipboard(bool silent)
    {
        try
        {
            ProcessFormatThreeWorkshops();
            ProcessFormatFourWorkshops();
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
                    for (var i = 0; i < workshopLimit; ++i)
                        ImGui.TableSetupColumn($"工房 {i + 1}");
                }

                ImGui.TableHeadersRow();

                ImGui.TableNextRow();
                for (var i = 0; i < workshopLimit; ++i)
                {
                    ImGui.TableNextColumn();
                    using var innerTable = ImRaii.Table($"table_{c}_{i}", 2, tableFlags);
                    if(!innerTable) continue;

                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
                    foreach (var rec in r.Workshops[i].Slots)
                    {
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        var iconSize = ImGui.GetTextLineHeight() * 1.5f;
                        var iconSizeVec = new Vector2(iconSize, iconSize);
                        var craftworkItemIcon = _craftSheet.GetRow(rec.CraftObjectId)!.Item.Value!.Icon;
                        ImGui.Image(Service.TextureProvider.GetFromGameIcon(new GameIconLookup(craftworkItemIcon)).GetWrapOrEmpty().ImGuiHandle, iconSizeVec, Vector2.Zero, Vector2.One);

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(_botNames[(int)rec.CraftObjectId]);
                    }
                }
            }
        }
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

    private static bool IsMatch(string x, string y) => Regex.IsMatch(x, $@"\b{Regex.Escape(y)}\b");

    private static object MatchingScore(string item, string line)
    {
        var score = 0;

        // primitive matching based on how long the string matches. Enough for now but could need expanding later
        if (line.Contains(item))
            score += item.Length;

        return score;
    }

    public static string OfficialNameToBotName(string name)
    {
        if (name.StartsWith("海岛"))
            return name.Remove(0, 2);
        if (name.StartsWith("开拓工房"))
            return name.Remove(0, 4);
        return name;
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