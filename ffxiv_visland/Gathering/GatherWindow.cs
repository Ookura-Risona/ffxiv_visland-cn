using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using ECommons.SimpleGui;
using ImGuiNET;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Numerics;
using visland.Helpers;
using visland.IPC;
using static visland.Gathering.GatherRouteDB;

namespace visland.Gathering;

public class GatherWindow : Window, IDisposable
{
    private readonly UITree _tree = new();
    private readonly List<System.Action> _postDraw = [];

    public GatherRouteDB RouteDB = null!;
    public GatherRouteExec Exec = new();
    public GatherDebug _debug = null!;

    private int selectedRouteIndex = -1;
    public static bool loop;

    private readonly List<uint> Colours = GenericHelpers.GetSheet<UIColor>()!.Select(x => x.UIForeground).ToList();
    private Vector4 greenColor = new Vector4(0x5C, 0xB8, 0x5C, 0xFF) / 0xFF;
    private Vector4 redColor = new Vector4(0xD9, 0x53, 0x4F, 0xFF) / 0xFF;
    private Vector4 yellowColor = new Vector4(0xD9, 0xD9, 0x53, 0xFF) / 0xFF;

    private readonly List<int> Items = GenericHelpers.GetSheet<Item>()?.Select(x => (int)x.RowId).ToList()!;
    private ExcelSheet<Item> _items = null!;

    private string searchString = string.Empty;
    private readonly List<Route> FilteredRoutes = [];
    private FontAwesomeIcon PlayIcon => Exec.CurrentRoute != null && !Exec.Paused ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play;
    private string PlayTooltip => Exec.CurrentRoute == null ? "开始" : Exec.Paused ? "继续" : "暂停";

    public GatherWindow() : base("采集自动化", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        Size = new Vector2(800, 800);
        SizeCondition = ImGuiCond.FirstUseEver;
        RouteDB = Service.Config.Get<GatherRouteDB>();

        _debug = new(Exec);
        _items = GenericHelpers.GetSheet<Item>()!;
    }

    public void Setup()
    {
        EzConfigGui.Window.Size = new Vector2(800, 800);
        EzConfigGui.Window.SizeCondition = ImGuiCond.FirstUseEver;
        RouteDB = Service.Config.Get<GatherRouteDB>();

        _debug = new(Exec);
        _items = GenericHelpers.GetSheet<Item>()!;
    }

    public void Dispose() => Exec.Dispose();

    public override void PreOpenCheck() => Exec.Update();

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs)
        {
            using (var tab = ImRaii.TabItem("路线"))
                if (tab)
                {
                    DrawExecution();
                    ImGui.Separator();
                    ImGui.Spacing();

                    var cra = ImGui.GetContentRegionAvail();
                    var sidebar = cra with { X = cra.X * 0.40f };
                    var editor = cra with { X = cra.X * 0.60f };

                    DrawSidebar(sidebar);
                    ImGui.SameLine();
                    DrawEditor(editor);

                    foreach (var a in _postDraw)
                        a();
                    _postDraw.Clear();
                }
            //using (var tab = ImRaii.TabItem("Shopping"))
            //    if (tab)
            //        _autoGather.Draw();
            using (var tab = ImRaii.TabItem("日志"))
                if (tab)
                    InternalLog.PrintImgui();
            using (var tab = ImRaii.TabItem("调试"))
                if (tab)
                    _debug.Draw();
        }
    }

    private void DrawExecution()
    {
        ImGuiEx.Text("状态: ");
        ImGui.SameLine();

        if (Exec.CurrentRoute != null)
            Utils.FlashText($"{(Exec.Paused ? "暂停中" : Exec.Waiting ? "等待中" : "运行中")}", new Vector4(1.0f, 1.0f, 1.0f, 1.0f), Exec.Paused ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f), 2);
        ImGui.SameLine();

        if (Exec.CurrentRoute == null || Exec.CurrentWaypoint >= Exec.CurrentRoute.Waypoints.Count)
        {
            ImGui.Text("当前无运行中路线");
            return;
        }

        if (Exec.CurrentRoute != null) // Finish() call could've reset it
        {
            ImGui.SameLine();
            ImGuiEx.Text($"{Exec.CurrentRoute.Name}: 步骤 #{Exec.CurrentWaypoint + 1} {Exec.CurrentRoute.Waypoints[Exec.CurrentWaypoint].Position}");

            if (Exec.Waiting)
            {
                ImGui.SameLine();
                ImGuiEx.Text($"等待 {Exec.WaitUntil - System.Environment.TickCount64}ms");
            }
        }

        ImGui.SameLine();
        ImGuiEx.Text($"State: {Exec.CurrentState}");
    }

    private unsafe void DrawSidebar(Vector2 size)
    {
        using (ImRaii.Child("Sidebar", size, false))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
            {
                RouteDB.Routes.Add(new() { Name = "未命名路线" });
                RouteDB.NotifyModified();
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("创建");
            ImGui.SameLine();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.FileImport))
                TryImport(RouteDB);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("从剪贴板导入路线\");\n");

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
                ImGui.OpenPopup("Advanced Options");
            DrawRouteSettingsPopup();

            ImGui.SameLine();
            RapidImport();

            ImGuiEx.TextV("搜索: ");
            ImGui.SameLine();
            ImGuiEx.SetNextItemFullWidth();
            if (ImGui.InputText("###RouteSearch", ref searchString, 500))
            {
                FilteredRoutes.Clear();
                if (searchString.Length > 0)
                {
                    foreach (var route in RouteDB.Routes)
                    {
                        if (route.Name.Contains(searchString, System.StringComparison.CurrentCultureIgnoreCase) || route.Group.Contains(searchString, System.StringComparison.CurrentCultureIgnoreCase))
                            FilteredRoutes.Add(route);
                    }
                }
            }

            ImGui.Separator();

            using (ImRaii.Child("routes"))
            {
                var groups = GetGroups(RouteDB, true);
                foreach (var group in groups)
                {
                    foreach (var _ in _tree.Node($"{group}###{groups.IndexOf(group)}", contextMenu: () => ContextMenuGroup(group)))
                    {
                        var routeSource = FilteredRoutes.Count > 0 ? FilteredRoutes : RouteDB.Routes;
                        for (var i = 0; i < routeSource.Count; i++)
                        {
                            var route = routeSource[i];
                            var routeGroup = string.IsNullOrEmpty(route.Group) ? "None" : route.Group;
                            if (routeGroup == group)
                            {
                                if (ImGui.Selectable($"{route.Name} ({route.Waypoints.Count} steps)###{i}", i == selectedRouteIndex))
                                    selectedRouteIndex = i;
                                //if (ImRaii.ContextPopup($"{route.Name}{i}"))
                                //{
                                //    selectedRouteIndex = i;
                                //    ContextMenuRoute(routeSource[i]);
                                //}
                            }
                        }
                    }
                }
            }
        }
    }

    internal static bool RapidImportEnabled = false;
    private void RapidImport()
    {
        if (ImGui.Checkbox("启用快速导入", ref RapidImportEnabled))
            ImGui.SetClipboardText("");

        ImGuiComponents.HelpMarker("只需复制多个预设，即可轻松导入它们\nVisland 将读取您的剪切板并尝试导入您复制的所有内容\n启用后，您的剪贴板将被清除");
        if (RapidImportEnabled)
        {
            try
            {
                var text = ImGui.GetClipboardText();
                if (text != "")
                {
                    TryImport(RouteDB);
                    ImGui.SetClipboardText("");
                }
            }
            catch (Exception e)
            {
                Svc.Log.Error(e.Message, e);
            }
        }
    }

    private void DrawRouteSettingsPopup()
    {
        using var popup = ImRaii.Popup("Advanced Options");
        if (popup.Success)
        {
            Utils.DrawSection("全局路线编辑选项", ImGuiColors.ParsedGold);
            if (ImGui.SliderFloat("默认步骤半径", ref RouteDB.DefaultWaypointRadius, 0, 100))
                RouteDB.NotifyModified();
            if (ImGui.SliderFloat("默认交互半径", ref RouteDB.DefaultInteractionRadius, 0, 100))
                RouteDB.NotifyModified();

            Utils.DrawSection("Global Route Operation Options", ImGuiColors.ParsedGold);

            if (ImGui.Checkbox("开始时自动切换至采集模式", ref RouteDB.GatherModeOnStart))
                RouteDB.NotifyModified();
            ImGuiComponents.HelpMarker("当在无人岛开启路线时，自动切换为“采集模式");

            using (ImRaii.Disabled())
            {
                if (ImGui.Checkbox("发生错误时停止路线", ref RouteDB.DisableOnErrors))
                    RouteDB.NotifyModified();
            }
            ImGuiComponents.HelpMarker("因物品达到上限而无法采集时, 停止路线运行");

            if (ImGui.Checkbox("区域间传送", ref RouteDB.TeleportBetweenZones))
                RouteDB.NotifyModified();

            Utils.WorkInProgressIcon();
            ImGui.SameLine();
            if (ImGui.Checkbox("自动采集", ref RouteDB.AutoGather))
                RouteDB.NotifyModified();
            ImGuiComponents.HelpMarker($"Applies to non-island routes only. Will auto gather the item in the \"Item Target\" field and use the best actions available.");

            //if (ImGui.SliderInt("Land Distance", ref RouteDB.LandDistance, 1, 30))
            //    RouteDB.NotifyModified();
            //ImGuiComponents.HelpMarker("Only applies to waypoints auto generated from node scanning. How far to land from the node to land and switch from fly pathfinding to ground pathfinding.");

            Utils.DrawSection("全局路线设置", ImGuiColors.ParsedGold);

            if (ImGui.Checkbox("运行路线时自动精制魔晶石", ref RouteDB.ExtractMateria))
                RouteDB.NotifyModified();
            if (ImGui.Checkbox("运行路线时自动修理装备", ref RouteDB.RepairGear))
                RouteDB.NotifyModified();
            if (ImGui.SliderFloat("修理阈值", ref RouteDB.RepairPercent, 0, 100))
                RouteDB.NotifyModified();
            if (ImGui.Checkbox("运行路线时自动精选收藏品", ref RouteDB.PurifyCollectables))
                RouteDB.NotifyModified();
            ImGuiComponents.HelpMarker($"Also known as {GenericHelpers.GetRow<Addon>(2160)!.Value.Text}");
            if (ImGui.Checkbox("运行路线时检查 AutoRetainer", ref RouteDB.AutoRetainerIntegration))
                RouteDB.NotifyModified();
            ImGuiComponents.HelpMarker($"Will enable multi mode when you have any retainers or submarines returned across any enabled characters. Requires the current character to be set as the Preferred Character and the Teleport to FC config enabled in AutoRetainer.");
            if (ExcelCombos.ExcelSheetCombo("##Foods", out Item i, _ => $"[{RouteDB.GlobalFood}] {GenericHelpers.GetRow<Item>((uint)RouteDB.GlobalFood)?.Name}", x => $"[{x.RowId}] {x.Name}", x => x.ItemUICategory.RowId == 46))
            {
                RouteDB.GlobalFood = (int)i.RowId;
                RouteDB.NotifyModified();
            }
            if (RouteDB.GlobalFood != 0)
            {
                ImGui.SameLine();
                if (ImGuiEx.IconButton(FontAwesomeIcon.Undo, "ClearGlobalFood"))
                {
                    RouteDB.GlobalFood = 0;
                    RouteDB.NotifyModified();
                }
            }
            ImGuiComponents.HelpMarker("此处设置的食物将应用于所有路线，除非在路线设置中将其覆盖。");
        }
    }

    private void DrawEditor(Vector2 size)
    {
        if (selectedRouteIndex == -1) return;

        var routeSource = FilteredRoutes.Count > 0 ? FilteredRoutes : RouteDB.Routes;
        if (routeSource.Count == 0) return;
        var route = selectedRouteIndex >= routeSource.Count ? routeSource.Last() : routeSource[selectedRouteIndex];

        using (ImRaii.Child("Editor", size))
        {
            if (ImGuiComponents.IconButton(PlayIcon))
            {
                if (Exec.CurrentRoute != null)
                    Exec.Paused = !Exec.Paused;
                if (Exec.CurrentRoute == null && route.Waypoints.Count > 0)
                    Exec.Start(route, 0, true, loop, route.Waypoints[0].Pathfind);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(PlayTooltip);
            ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button, loop ? greenColor : redColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, loop ? greenColor : redColor);
            if (ImGuiComponents.IconButton(FontAwesomeIcon.SyncAlt))
                loop ^= true;
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("循环");
            ImGui.SameLine();

            if (Exec.CurrentRoute != null)
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Stop))
                    Exec.Finish();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("停止");
                ImGui.SameLine();
            }

            var canDelete = !ImGui.GetIO().KeyCtrl;
            using (ImRaii.Disabled(canDelete))
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
                {
                    if (Exec.CurrentRoute == route)
                        Exec.Finish();
                    RouteDB.Routes.Remove(route);
                    RouteDB.NotifyModified();
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("删除路线 (按住 CTRL)");
            ImGui.SameLine();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.FileExport))
            {
                ImGui.SetClipboardText(JsonConvert.SerializeObject(route));
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("导出路线 (\uE052 Base64)");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.SetClipboardText(Utils.ToCompressedBase64(route));
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.EllipsisH))
                ImGui.OpenPopup("##MassEditing");
            DrawMassEditContextMenu(route);

            var name = route.Name;
            var group = route.Group;
            var movementType = Service.Condition[ConditionFlag.InFlight] ? Movement.MountFly : Service.Condition[ConditionFlag.Mounted] ? Movement.MountNoFly : Movement.Normal;
            ImGuiEx.TextV("名称: ");
            ImGui.SameLine();
            if (ImGui.InputText("##name", ref name, 256))
            {
                route.Name = name;
                RouteDB.NotifyModified();
            }
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
            {
                Exec.Finish();
                var player = Service.ClientState.LocalPlayer;
                if (player != null)
                {
                    route.Waypoints.Add(new() { Position = player.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType });
                    RouteDB.NotifyModified();
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("+步骤: 移动至当前位置");
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.UserPlus))
            {
                var target = Service.TargetManager.Target;
                if (target != null)
                {
                    route.Waypoints.Add(new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType, InteractWithOID = target.DataId, InteractWithName = target.Name.ToString().ToLower() });
                    RouteDB.NotifyModified();
                    Exec.Start(route, route.Waypoints.Count - 1, false, false);
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("+步骤: 与目标交互");

            ImGuiEx.TextV("分组: ");
            ImGui.SameLine();
            if (ImGui.InputText("##group", ref group, 256))
            {
                route.Group = group;
                RouteDB.NotifyModified();
            }

            if (RouteDB.AutoGather)
            {
                ImGuiEx.TextV("目标物品: ");
                ImGui.SameLine();
                if (ExcelCombos.ExcelSheetCombo("##Gatherables", out GatheringItem gatherable, _ => $"[{route.TargetGatherItem}] {GenericHelpers.GetRow<Item>((uint)route.TargetGatherItem)?.Name.ToString()}", x => $"[{x.RowId}] {GenericHelpers.GetRow<Item>(x.Item.RowId)?.Name.ToString()}", x => x.Item.RowId != 0))
                {
                    route.TargetGatherItem = (int)gatherable.Item.RowId;
                    RouteDB.NotifyModified();
                }
                if (route.TargetGatherItem != 0)
                {
                    ImGui.SameLine();
                    if (ImGuiEx.IconButton(FontAwesomeIcon.Undo, "ClearItemTarget"))
                    {
                        route.TargetGatherItem = 0;
                        RouteDB.NotifyModified();
                    }
                }
            }

            using (ImRaii.Child("waypoints"))
            {
                for (var i = 0; i < route.Waypoints.Count; ++i)
                {
                    var wp = route.Waypoints[i];
                    foreach (var wn in _tree.Node($"#{i + 1}: [x: {wp.Position.X:f0}, y: {wp.Position.Y:f0}, z: {wp.Position.Z:f0}] ({wp.Movement}) {(wp.InteractWithOID != 0 ? $" @ {wp.InteractWithName} ({wp.InteractWithOID:X})" : "")}###{i}", color: wp.IsPhantom ? ImGuiColors.HealerGreen.ToHex() : 0xffffffff, contextMenu: () => ContextMenuWaypoint(route, i)))
                        DrawWaypoint(wp);
                }
            }
        }
    }


    private bool pathfind;
    private int zoneID;
    private float radius;
    private InteractionType interaction;
    private void DrawMassEditContextMenu(Route route)
    {
        using var popup = ImRaii.Popup("##MassEditing");
        if (!popup) return;

        Utils.DrawSection("路线设置", ImGuiColors.ParsedGold);
        if (ExcelCombos.ExcelSheetCombo("##Foods", out Item i, _ => $"[{route.Food}] {GenericHelpers.GetRow<Item>((uint)route.Food)?.Name}", x => $"[{x.RowId}] {x.Name}", x => x.ItemUICategory.RowId == 46))
        {
            route.Food = (int)i.RowId;
            RouteDB.NotifyModified();
        }
        if (RouteDB.GlobalFood != 0)
        {
            ImGui.SameLine();
            if (ImGuiEx.IconButton(FontAwesomeIcon.Undo, "ClearLocalFood"))
            {
                route.Food = 0;
                RouteDB.NotifyModified();
            }
        }
        ImGuiComponents.HelpMarker("此处设置的食物将仅适用于此路线，并覆盖全局食物设置。");

        Utils.DrawSection("批量编辑", ImGuiColors.ParsedGold);
        ImGui.Checkbox("寻路", ref pathfind);
        ImGui.SameLine();
        if (ImGui.Button("应用于全部###Pathfind"))
        {
            route?.Waypoints.ForEach(x => x.Pathfind = pathfind);
            RouteDB.NotifyModified();
        }

        ImGui.InputInt("区域", ref zoneID);
        ImGui.SameLine();
        if (ImGui.Button("应用于全部###Zone"))
        {
            route?.Waypoints.ForEach(x => x.ZoneID = zoneID);
            RouteDB.NotifyModified();
        }

        ImGui.InputFloat("半径", ref radius);
        ImGui.SameLine();
        if (ImGui.Button("应用于全部###Radius"))
        {
            route?.Waypoints.ForEach(x => x.Radius = radius);
            RouteDB.NotifyModified();
        }

        UICombo.Enum("交互类型", ref interaction);
        ImGui.SameLine();
        if (ImGui.Button("应用于全部###Interaction"))
        {
            route?.Waypoints.ForEach(x => x.Interaction = interaction);
            RouteDB.NotifyModified();
        }
    }

    private void DrawWaypoint(Waypoint wp)
    {
        if (ImGuiEx.IconButton(FontAwesomeIcon.MapMarker) && PlayerEx.Available)
        {
            wp.Position = PlayerEx.Object.Position;
            wp.ZoneID = Service.ClientState.TerritoryType;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("设置为当前位置");
        ImGui.SameLine();
        if (ImGui.InputFloat3("位置", ref wp.Position))
            RouteDB.NotifyModified();

        if (ImGui.InputInt("区域 ID", ref wp.ZoneID))
            RouteDB.NotifyModified();

        if (ImGui.InputFloat("半径 (y)", ref wp.Radius))
            RouteDB.NotifyModified();

        if (UICombo.Enum("移动模式", ref wp.Movement))
            RouteDB.NotifyModified();

        ImGui.SameLine();
        using (var noNav = ImRaii.Disabled(!Utils.HasPlugin(NavmeshIPC.Name)))
        {
            if (ImGui.Checkbox("寻路?", ref wp.Pathfind))
                RouteDB.NotifyModified();
        }
        if (!Utils.HasPlugin(NavmeshIPC.Name))
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip($"此功能需要安装并启用 {NavmeshIPC.Name}");

        if (ImGuiComponents.IconButton(FontAwesomeIcon.UserPlus))
        {
            if (wp.InteractWithOID == default)
            {
                var target = Service.TargetManager.Target;
                if (target != null)
                {
                    wp.Position = target.Position;
                    wp.Radius = RouteDB.DefaultInteractionRadius;
                    wp.InteractWithName = target.Name.ToString().ToLower();
                    wp.InteractWithOID = target.DataId;
                    RouteDB.NotifyModified();
                }
            }
            else
                wp.InteractWithOID = default;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("为步骤添加/移除目标");
        ImGui.SameLine();
        if (ImGuiEx.IconButton(FontAwesomeIcon.CommentDots))
        {
            wp.showInteractions ^= true;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("切换至交互");
        ImGui.SameLine();
        if (ImGuiEx.IconButton(FontAwesomeIcon.Clock))
        {
            wp.showWaits ^= true;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("切换至等待");

        if (wp.showInteractions)
        {
            if (UICombo.Enum("交互方式", ref wp.Interaction))
                RouteDB.NotifyModified();
            switch (wp.Interaction)
            {
                case InteractionType.None: break;
                case InteractionType.Standard: break;
                case InteractionType.StartRoute:
                    if (UICombo.String("路线名称", RouteDB.Routes.Select(r => r.Name).ToArray(), ref wp.RouteName))
                        RouteDB.NotifyModified();
                    break;
                case InteractionType.NodeScan:
                    ImGui.SameLine();
                    Utils.WorkInProgressIcon();
                    ImGuiComponents.HelpMarker("Node scanning will check the object table for nearby targetable gathering points, failing that will use your gatherer's reveal node ability and navigate to that. It will create a new phantom waypoint with the aforementioned information and navigate to it. Every phantom waypoint will also node scan. These special waypoints do not get saved to the route.");
                    ImGui.TextUnformatted("This feature will have trouble with land nodes at the moment.");
                    break;
            }
        }

        if (wp.showWaits)
        {
            if (ImGui.InputFloat2("Eorzean Time Wait", ref wp.WaitTimeET, "%.0f"))
                RouteDB.NotifyModified();
            if (ImGui.SliderInt("等待 (ms)", ref wp.WaitTimeMs, 0, 60000))
                RouteDB.NotifyModified();
            if (UICombo.Enum("Wait for Condition", ref wp.WaitForCondition))
                RouteDB.NotifyModified();
        }
    }

    private void ContextMenuGroup(string group)
    {
        var old = group;
        ImGuiEx.TextV("名称: ");
        ImGui.SameLine();
        if (ImGui.InputText("##groupname", ref group, 256))
        {
            RouteDB.Routes.Where(r => r.Group == old).ToList().ForEach(r => r.Group = group);
            RouteDB.NotifyModified();
        }
    }

    private void ContextMenuRoute(Route r)
    {
        var group = r.Group;
        ImGuiEx.TextV("分组: ");
        ImGui.SameLine();
        if (ImGui.InputText("##group", ref group, 256))
        {
            r.Group = group;
            RouteDB.NotifyModified();
        }
        if (ImGui.BeginMenu("Add Route to Existing Group"))
        {
            var groupsCmr = GetGroups(RouteDB, true);
            foreach (var groupCmr in groupsCmr)
            {
                if (ImGui.MenuItem(groupCmr))
                    r.Group = groupCmr;
                RouteDB.NotifyModified();
            }
            ImGui.EndMenu();
        }
    }

    private void ContextMenuWaypoint(Route r, int i)
    {
        if (ImGui.MenuItem("仅执行此步"))
            Exec.Start(r, i, false, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem("从此步开始执行路线一次"))
            Exec.Start(r, i, true, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem("从此步开始执行路线一次"))
            Exec.Start(r, i, true, true, r.Waypoints[i].Pathfind);

        var movementType = Service.Condition[ConditionFlag.InFlight] ? Movement.MountFly : Service.Condition[ConditionFlag.Mounted] ? Movement.MountNoFly : Movement.Normal;
        var target = Service.TargetManager.Target;

        if (ImGui.MenuItem($"切换至 {(r.Waypoints[i].InteractWithOID != default ? "移动步骤" : "交互步骤")}"))
        {
            _postDraw.Add(() =>
            {
                r.Waypoints[i].InteractWithOID = r.Waypoints[i].InteractWithOID != default ? default : target?.DataId ?? default;
                RouteDB.NotifyModified();
            });
        }

        if (ImGui.MenuItem("在上方插入"))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (Service.ClientState.LocalPlayer != null)
                    {
                        r.Waypoints.Insert(i, new() { Position = Service.ClientState.LocalPlayer.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (target != null)
                    {
                        r.Waypoints.Insert(i, new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType, InteractWithOID = target.DataId, InteractWithName = target.Name.ToString().ToLower() });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }

        if (ImGui.MenuItem("在下方插入"))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (Service.ClientState.LocalPlayer != null)
                    {
                        r.Waypoints.Insert(i + 1, new() { Position = Service.ClientState.LocalPlayer.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (target != null)
                    {
                        r.Waypoints.Insert(i + 1, new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType, InteractWithOID = target.DataId, InteractWithName = target.Name.ToString().ToLower() });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }

        if (ImGui.MenuItem("上移"))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    var wp = r.Waypoints[i];
                    r.Waypoints.RemoveAt(i);
                    r.Waypoints.Insert(i - 1, wp);
                    RouteDB.NotifyModified();
                }
            });
        }

        if (ImGui.MenuItem("下移"))
        {
            _postDraw.Add(() =>
            {
                if (i + 1 < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    var wp = r.Waypoints[i];
                    r.Waypoints.RemoveAt(i);
                    r.Waypoints.Insert(i + 1, wp);
                    RouteDB.NotifyModified();
                }
            });
        }

        if (ImGui.MenuItem("删除"))
        {
            _postDraw.Add(() =>
            {
                if (i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    r.Waypoints.RemoveAt(i);
                    RouteDB.NotifyModified();
                }
            });
        }
    }
}
