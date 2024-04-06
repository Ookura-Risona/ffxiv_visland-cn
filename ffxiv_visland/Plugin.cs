using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Common;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ECommons;
using ECommons.Automation;
using ECommons.Reflection;
using ImGuiNET;
using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Newtonsoft.Json;
using visland.Export;
using visland.Farm;
using visland.Gathering;
using visland.Granary;
using visland.Helpers;
using visland.IPC;
using visland.Pasture;
using visland.Questing;
using visland.Workshop;
using Module = ECommons.Module;

namespace visland;

public sealed class Plugin : IDalamudPlugin
{
    public static string Name => "visland";

    public DalamudPluginInterface Dalamud { get; init; }
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    internal static Plugin P;
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    private readonly VislandIPC _vislandIPC;
    internal TaskManager TaskManager;
    internal Memory Memory;

    private VislandIPC _vislandIPC;

    public WindowSystem WindowSystem = new("visland");
    private readonly GatherWindow _wndGather;
    private readonly WorkshopWindow _wndWorkshop;
    private readonly GranaryWindow _wndGranary;
    private readonly PastureWindow _wndPasture;
    private readonly FarmWindow _wndFarm;
    private readonly ExportWindow _wndExports;

    public Plugin(DalamudPluginInterface dalamud)
    {
        var dir = dalamud.ConfigDirectory;
        if (!dir.Exists)
            dir.Create();
        var dalamudRoot =
            dalamud.GetType().Assembly.GetType("Dalamud.Service`1", true)!
                .MakeGenericType(dalamud.GetType().Assembly.GetType("Dalamud.Dalamud", true)!).GetMethod("Get")!
                .Invoke(null, BindingFlags.Default, null, Array.Empty<object>(), null);
        var dalamudStartInfo = dalamudRoot.GetFoP<DalamudStartInfo>("StartInfo");

        ECommonsMain.Init(dalamud, this, Module.DalamudReflector);
        DalamudReflector.RegisterOnInstalledPluginsChangedEvents(CheckIPC);
        Service.Init(dalamud);
        AutoCutsceneSkipper.Init(null);
        AutoCutsceneSkipper.Disable();

        dalamud.Create<Service>();
        dalamud.UiBuilder.Draw += WindowSystem.Draw;

        Service.Config.Initialize();
        if (dalamud.ConfigFile.Exists)
            Service.Config.LoadFromFile(dalamud.ConfigFile);
        Service.Config.Modified += (_, _) => Service.Config.SaveToFile(dalamud.ConfigFile);

        Dalamud = dalamud;
        P = this;
        TaskManager = new TaskManager { AbortOnTimeout = true, TimeLimitMS = 20000 };
        Memory = new Memory();

        _wndGather = new GatherWindow();
        _wndWorkshop = new WorkshopWindow();
        _wndGranary = new GranaryWindow();
        _wndPasture = new PastureWindow();
        _wndFarm = new FarmWindow();
        _wndExports = new ExportWindow();

        _vislandIPC = new VislandIPC(_wndGather);

        WindowSystem.AddWindow(_wndGather);
        WindowSystem.AddWindow(_wndWorkshop);
        WindowSystem.AddWindow(_wndGranary);
        WindowSystem.AddWindow(_wndPasture);
        WindowSystem.AddWindow(_wndFarm);
        WindowSystem.AddWindow(_wndExports);
        Service.CommandManager.AddHandler("/visland", new CommandInfo(OnCommand)
        {
            HelpMessage = "开启采集界面\n" +
                          $"/{Name} moveto <X> <Y> <Z> → 移动至指定坐标\n" +
                          $"/{Name} movedir <X> <Y> <Z> → 根据当前面向移动指定单位距离\n" +
                          $"/{Name} stop → 停止当前路线\n" +
                          $"/{Name} pause → 暂停当前路线\n" +
                          $"/{Name} resume → 继续当前路线\n" +
                          $"/{Name} exec <name> → 循环运行指定名称的路线\n" +
                          $"/{Name} execonce <name> → 运行指定名称的路线一次\n" +
                          $"/{Name} exectemp <base64 route> → 循环运行临时路线\n" +
                          $"/{Name} exectemponce <base64 route> → 运行临时路线一次",
            ShowInHelp = true
        });
        Dalamud.UiBuilder.OpenConfigUi += () => _wndGather.IsOpen = true;
    }

    public void Dispose()
    {
        _vislandIPC.Dispose();
        WindowSystem.RemoveAllWindows();
        Service.CommandManager.RemoveHandler("/visland");
        _wndGather.Dispose();
        _wndWorkshop.Dispose();
        _wndGranary.Dispose();
        _wndPasture.Dispose();
        _wndFarm.Dispose();
        _wndExports.Dispose();
        ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string arguments)
    {
        Service.Log.Debug($"cmd: '{command}', args: '{arguments}'");
        if (arguments.Length == 0)
        {
            _wndGather.IsOpen ^= true;
        }
        else
        {
            var args = arguments.Split(' ');
            switch (args[0])
            {
                case "moveto":
                    if (args.Length > 3)
                        MoveToCommand(args, false);
                    break;
                case "movedir":
                    if (args.Length > 3)
                        MoveToCommand(args, true);
                    break;
                case "stop":
                    _wndGather.Exec.Finish();
                    break;
                case "pause":
                    _wndGather.Exec.Paused = true;
                    break;
                case "resume":
                    _wndGather.Exec.Paused = false;
                    break;
                case "exec":
                    ExecuteCommand(string.Join(" ", args.Skip(1)), false);
                    break;
                case "execonce":
                    ExecuteCommand(string.Join(" ", args.Skip(1)), true);
                    break;
                case "exectemp":
                    ExecuteTempRoute(args[1], false);
                    break;
                case "exectemponce":
                    ExecuteTempRoute(args[1], true);
                    break;
            }
        }
    }

    private void ExecuteTempRoute(string base64, bool once)
    {
        var json = base64.FromCompressedBase64();
        var route = JsonConvert.DeserializeObject<GatherRouteDB.Route>(json);
        if (route != null)
            _wndGather.Exec.Start(route, 0, true, !once);
    }

    private void MoveToCommand(string[] args, bool relativeToPlayer)
    {
        var originActor = relativeToPlayer ? Service.ClientState.LocalPlayer : null;
        var origin = originActor?.Position ?? new Vector3();
        var offset = new Vector3(float.Parse(args[1], CultureInfo.InvariantCulture),
            float.Parse(args[2], CultureInfo.InvariantCulture), float.Parse(args[3], CultureInfo.InvariantCulture));
        var route = new GatherRouteDB.Route { Name = "Temporary", Waypoints = [] };
        route.Waypoints.Add(new GatherRouteDB.Waypoint
            { Position = origin + offset, Radius = 0.5f, InteractWithName = "", InteractWithOID = 0 });
        _wndGather.Exec.Start(route, 0, false, false);
    }

    private void ExecuteCommand(string name, bool once)
    {
        var route = _wndGather.RouteDB.Routes.Find(r => r.Name == name);
        if (route != null)
            _wndGather.Exec.Start(route, 0, true, !once);
    }

    private void CheckIPC()
    {
        if (Utils.HasPlugin(BossModIPC.Name))
            BossModIPC.Init();
        if (Utils.HasPlugin(NavmeshIPC.Name))
            NavmeshIPC.Init();
    }
}