using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared;
using ExileCore2.Shared.Cache;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using ImGuiNET;
using ItemFilterLibrary;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore2.PoEMemory;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace PickIt;

public partial class PickIt : BaseSettingsPlugin<PickItSettings>
{
    private readonly CachedValue<List<LabelOnGround>> _chestLabels;
    private readonly CachedValue<LabelOnGround> _portalLabel;
    private readonly CachedValue<List<LabelOnGround>> _corpseLabels;
    private readonly CachedValue<bool[,]> _inventorySlotsCache;
    private ServerInventory _inventoryItems;
    private SyncTask<bool> _pickUpTask;
    private long _lastClick;
    private List<ItemFilter> _itemFilters;
    private bool _pluginBridgeModeOverride;
    private bool[,] InventorySlots => _inventorySlotsCache.Value;
    private readonly Stopwatch _sinceLastClick = Stopwatch.StartNew();
    private Element UIHoverWithFallback => GameController.IngameState.UIHover switch { null or { Address: 0 } => GameController.IngameState.UIHoverElement, var s => s };
    private bool OkayToClick => _sinceLastClick.ElapsedMilliseconds > Settings.PauseBetweenClicks;

    public PickIt()
    {
        _inventorySlotsCache = new FrameCache<bool[,]>(() => GetContainer2DArray(_inventoryItems));
        _chestLabels = new TimeCache<List<LabelOnGround>>(UpdateChestList, 200);
        _corpseLabels = new TimeCache<List<LabelOnGround>>(UpdateCorpseList, 200);
        _portalLabel = new TimeCache<LabelOnGround>(() => GetLabel(@"^Metadata/(MiscellaneousObjects|Effects/Microtransactions)/.*Portal"), 200);
    }

    public override bool Initialise()
    {
        #region Register keys

        Settings.PickUpKey.OnValueChanged += () => Input.RegisterKey(Settings.PickUpKey);
        Settings.ProfilerHotkey.OnValueChanged += () => Input.RegisterKey(Settings.ProfilerHotkey);

        Input.RegisterKey(Settings.PickUpKey);
        Input.RegisterKey(Settings.ProfilerHotkey);
        Input.RegisterKey(Keys.Escape);

        #endregion

        Settings.ReloadFilters.OnPressed = LoadRuleFiles;
        LoadRuleFiles();
        GameController.PluginBridge.SaveMethod("PickIt.ListItems", () => GetItemsToPickup(false).Select(x => x.QueriedItem).ToList());
        GameController.PluginBridge.SaveMethod("PickIt.IsActive", () => _pickUpTask?.GetAwaiter().IsCompleted == false);
        GameController.PluginBridge.SaveMethod("PickIt.SetWorkMode", (bool running) => { _pluginBridgeModeOverride = running; });
        return true;
    }

    private enum WorkMode
    {
        Stop,
        Lazy,
        Manual
    }

    private WorkMode GetWorkMode()
    {
        if (!GameController.Window.IsForeground() ||
            !Settings.Enable ||
            Input.GetKeyState(Keys.Escape))
        {
            _pluginBridgeModeOverride = false;
            return WorkMode.Stop;
        }

        if (Input.GetKeyState(Settings.ProfilerHotkey.Value))
        {
            var sw = Stopwatch.StartNew();
            var looseVar2 = GetItemsToPickup(false).FirstOrDefault();
            sw.Stop();
            LogMessage($"GetItemsToPickup Elapsed Time: {sw.ElapsedTicks} Item: {looseVar2?.BaseName} Distance: {looseVar2?.Distance}");
        }

        if (Input.GetKeyState(Settings.PickUpKey.Value) || _pluginBridgeModeOverride)
        {
            return WorkMode.Manual;
        }

        if (CanLazyLoot())
        {
            return WorkMode.Lazy;
        }

        return WorkMode.Stop;
    }

    private DateTime DisableLazyLootingTill { get; set; }

    public override void Tick()
    {
        var playerInvCount = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories?.Count;
        if (playerInvCount is null or 0)
            return;

        #region HoverPickit
        if (Settings.AutoClickHoveredLootInRange.Value)
        {
            var hoverItemIcon = UIHoverWithFallback.AsObject<HoverItemIcon>();
            if (hoverItemIcon != null && !GameController.IngameState.IngameUi.InventoryPanel.IsVisible &&
                !Input.IsKeyDown(Keys.LButton))
            {
                if (hoverItemIcon.Item != null && OkayToClick)
                {
                    var groundItem =
                        GameController.IngameState.IngameUi.ItemsOnGroundLabels.FirstOrDefault(e =>
                            e.Label.Address == hoverItemIcon.Address);
                    if (groundItem != null)
                    {
                        var doWePickThis = Settings.PickUpEverything || (_itemFilters?.Any(filter =>
                            filter.Matches(new ItemData(groundItem.ItemOnGround, GameController))) ?? false);
                        if (doWePickThis && groundItem?.ItemOnGround.DistancePlayer < 20f)
                        {
                            _sinceLastClick.Restart();
                            Input.Click(MouseButtons.Left);
                        }
                    }
                }
            }
        }
        #endregion

        _inventoryItems = GameController.Game.IngameState.Data.ServerData.PlayerInventories[0].Inventory;
        DrawIgnoredCellsSettings();
        if (Input.GetKeyState(Settings.LazyLootingPauseKey)) DisableLazyLootingTill = DateTime.Now.AddSeconds(2);
        
        return;
    }

    public override void Render()
    {
        if (Settings.DebugHighlight)
        {
            foreach (var item in GetItemsToPickup(false))
            {
                Graphics.DrawFrame(item.QueriedItem.ClientRect, Color.Violet, 5);
            }
        }
        
        if (GetWorkMode() != WorkMode.Stop)
        {
            TaskUtils.RunOrRestart(ref _pickUpTask, RunPickerIterationAsync);
        }
        else
        {
            _pickUpTask = null;
        }

        if (Settings.FilterTest.Value is { Length: > 0 } &&
            GameController.IngameState.UIHover is { Address: not 0 } h &&
            h.Entity.IsValid)
        {
            var f = ItemFilter.LoadFromString(Settings.FilterTest);
            var matched = f.Matches(new ItemData(h.Entity, GameController));
            DebugWindow.LogMsg($"Debug item match: {matched}");
        }
    }

    //TODO: Make function pretty
    private void DrawIgnoredCellsSettings()
    {
        if (!Settings.ShowInventoryView.Value)
            return;

        var opened = true;

        const ImGuiWindowFlags nonMoveableFlag = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground |
                                                 ImGuiWindowFlags.NoTitleBar |
                                                 ImGuiWindowFlags.NoInputs |
                                                 ImGuiWindowFlags.NoFocusOnAppearing;

        ImGui.SetNextWindowPos(Settings.InventoryPos.Value);
        if (ImGui.Begin($"{Name}##InventoryCellMap", ref opened,nonMoveableFlag))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0,0));

            var numb = 0;
            for (var i = 0; i < 5; i++)
            for (var j = 0; j < 12; j++)
            {
                var toggled = Convert.ToBoolean(InventorySlots[i, j]);
                if (ImGui.Checkbox($"##{numb}IgnoredCells", ref toggled)) InventorySlots[i, j] = toggled;

                if (j != 11) ImGui.SameLine();

                numb += 1;
            }

            ImGui.PopStyleVar(2);

            ImGui.End();
        }
    }

    private bool DoWePickThis(PickItItemData item)
    {
        return Settings.PickUpEverything || (_itemFilters?.Any(filter => filter.Matches(item)) ?? false);
    }

    private List<LabelOnGround> UpdateChestList()
    {
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is { } path &&
                   (Settings.ClickQuestChests && path.StartsWith("Metadata/Chests/QuestChests/", StringComparison.Ordinal) ||
                    path.StartsWith("Metadata/Chests/LeaguesExpedition/", StringComparison.Ordinal) ||
                    path.StartsWith("Metadata/Chests/LegionChests/", StringComparison.Ordinal) ||
                    path.StartsWith("Metadata/Chests/Blight", StringComparison.Ordinal) ||
                    path.StartsWith("Metadata/Chests/Breach/", StringComparison.Ordinal) ||
                    path.StartsWith("Metadata/Chests/IncursionChest", StringComparison.Ordinal)) &&
                   entity.HasComponent<Chest>();
        }

        if (GameController.EntityListWrapper.OnlyValidEntities.Any(IsFittingEntity))
        {
            return GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible
                .Where(x => x.Address != 0 &&
                            x.IsVisible &&
                            IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return [];
    }

    private List<LabelOnGround> UpdateCorpseList()
    {
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is "Metadata/Terrain/Leagues/Necropolis/Objects/NecropolisCorpseMarker";
        }

        if (GameController.EntityListWrapper.OnlyValidEntities.Any(IsFittingEntity))
        {
            return GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible
                .Where(x => x.Address != 0 &&
                            x.IsVisible &&
                            IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return [];
    }

    private bool CanLazyLoot()
    {
        if (!Settings.LazyLooting) return false;
        if (DisableLazyLootingTill > DateTime.Now) return false;
        try
        {
            if (Settings.NoLazyLootingWhileEnemyClose && GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
                    .Any(x => x?.GetComponent<Monster>() != null && x.IsValid && x.IsHostile && x.IsAlive
                              && !x.IsHidden && !x.Path.Contains("ElementalSummoned")
                              && Vector3.Distance(GameController.Player.Pos, x.GetComponent<Render>().Pos) < Settings.PickupRange)) return false;
        }
        catch (NullReferenceException)
        {
        }

        return true;
    }

    private bool ShouldLazyLoot(PickItItemData item)
    {
        if (item == null)
        {
            return false;
        }

        var itemPos = item.QueriedItem.Entity.Pos;
        var playerPos = GameController.Player.Pos;
        return Math.Abs(itemPos.Z - playerPos.Z) <= 50 &&
               itemPos.Xy().DistanceSquared(playerPos.Xy()) <= 275 * 275;
    }

    private bool IsLabelClickable(Element element, RectangleF? customRect)
    {
        if (element is not { IsValid: true, IsVisible: true, IndexInParent: not null })
        {
            return false;
        }

        var center = (customRect ?? element.GetClientRect()).Center;

        var gameWindowRect = GameController.Window.GetWindowRectangleTimeCache with { Location = Vector2.Zero };
        gameWindowRect.Inflate(-36, -36);
        return gameWindowRect.Contains(center.X, center.Y);
    }

    private bool IsPortalTargeted(LabelOnGround portalLabel)
    {
        if (portalLabel == null)
        {
            return false;
        }

        // extra checks in case of HUD/game update. They are easy on CPU
        return
            GameController.IngameState.UIHover.Address == portalLabel.Address ||
            GameController.IngameState.UIHover.Address == portalLabel.ItemOnGround.Address ||
            GameController.IngameState.UIHover.Address == portalLabel.Label.Address ||
            GameController.IngameState.UIHoverElement.Address == portalLabel.Address ||
            GameController.IngameState.UIHoverElement.Address == portalLabel.ItemOnGround.Address ||
            GameController.IngameState.UIHoverElement.Address ==
            portalLabel.Label.Address || // this is the right one
            GameController.IngameState.UIHoverTooltip.Address == portalLabel.Address ||
            GameController.IngameState.UIHoverTooltip.Address == portalLabel.ItemOnGround.Address ||
            GameController.IngameState.UIHoverTooltip.Address == portalLabel.Label.Address ||
            portalLabel.ItemOnGround?.HasComponent<Targetable>() == true &&
            portalLabel.ItemOnGround?.GetComponent<Targetable>()?.isTargeted == true;
    }

    private static bool IsPortalNearby(LabelOnGround portalLabel, Element element)
    {
        if (portalLabel == null) return false;
        var rect1 = portalLabel.Label.GetClientRectCache;
        var rect2 = element.GetClientRectCache;
        rect1.Inflate(100, 100);
        rect2.Inflate(100, 100);
        return rect1.Intersects(rect2);
    }

    private LabelOnGround GetLabel(string id)
    {
        var labels = GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels;
        if (labels == null)
        {
            return null;
        }

        var regex = new Regex(id);
        var labelQuery =
            from labelOnGround in labels
            where labelOnGround?.Label is { IsValid: true, Address: > 0, IsVisible: true }
            let itemOnGround = labelOnGround.ItemOnGround
            where itemOnGround?.Metadata is { } metadata && regex.IsMatch(metadata)
            let dist = GameController?.Player?.GridPos.DistanceSquared(itemOnGround.GridPos)
            orderby dist
            select labelOnGround;

        return labelQuery.FirstOrDefault();
    }

    #region (Re)Loading Rules

    private void LoadRuleFiles()
    {
        var pickitConfigFileDirectory = ConfigDirectory;
        var existingRules = Settings.PickitRules;

        if (!string.IsNullOrEmpty(Settings.CustomConfigDir))
        {
            var customConfigFileDirectory = Path.Combine(Path.GetDirectoryName(ConfigDirectory), Settings.CustomConfigDir);

            if (Directory.Exists(customConfigFileDirectory))
            {
                pickitConfigFileDirectory = customConfigFileDirectory;
            }
            else
            {
                DebugWindow.LogError("[Pickit] custom config folder does not exist.", 15);
            }
        }

        try
        {
            var newRules = new DirectoryInfo(pickitConfigFileDirectory).GetFiles("*.ifl")
                .Select(x => new PickitRule(x.Name, Path.GetRelativePath(pickitConfigFileDirectory, x.FullName), false))
                .ExceptBy(existingRules.Select(x => x.Location), x => x.Location)
                .ToList();
            foreach (var groundRule in existingRules)
            {
                var fullPath = Path.Combine(pickitConfigFileDirectory, groundRule.Location);
                if (File.Exists(fullPath))
                {
                    newRules.Add(groundRule);
                }
                else
                {
                    LogError($"File '{groundRule.Name}' not found.");
                }
            }

            _itemFilters = newRules
                .Where(rule => rule.Enabled)
                .Select(rule => ItemFilter.LoadFromPath(Path.Combine(pickitConfigFileDirectory, rule.Location)))
                .ToList();

            Settings.PickitRules = newRules;
        }
        catch (Exception ex)
        {
            LogError($"[Pickit] Error loading filters: {ex}.", 15);
        }
    }

    private async SyncTask<bool> RunPickerIterationAsync()
    {
        LogMessage("RunPickerIterationAsync");
        if (!GameController.Window.IsForeground()) return true;

        var pickUpThisItem = GetItemsToPickup(true).FirstOrDefault();
        var workMode = GetWorkMode();
        if (workMode == WorkMode.Manual || workMode == WorkMode.Lazy && ShouldLazyLoot(pickUpThisItem))
        {
            if (Settings.ItemizeCorpses)
            {
                var corpseLabel = _corpseLabels?.Value.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer < Settings.PickupRange &&
                    IsLabelClickable(x.Label, null));

                if (corpseLabel != null)
                {
                    await PickAsync(corpseLabel.ItemOnGround, corpseLabel.Label?.GetChildFromIndices(0, 2, 1), null, _corpseLabels.ForceUpdate);
                    return true;
                }
            }

            if (Settings.ClickChests)
            {
                var chestLabel = _chestLabels?.Value.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer < Settings.PickupRange &&
                    IsLabelClickable(x.Label, null));

                if (chestLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= chestLabel.ItemOnGround.DistancePlayer))
                {
                    await PickAsync(chestLabel.ItemOnGround, chestLabel.Label, null, _chestLabels.ForceUpdate);
                    return true;
                }
            }

            if (pickUpThisItem == null)
            {
                return true;
            }

            pickUpThisItem.AttemptedPickups++;
            await PickAsync(pickUpThisItem.QueriedItem.Entity, pickUpThisItem.QueriedItem.Label, pickUpThisItem.QueriedItem.ClientRect, () => { });
        }

        return true;
    }

    private IEnumerable<PickItItemData> GetItemsToPickup(bool filterAttempts)
    {
        var labels = GameController.Game.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels?
            .Where(x=> x.Entity?.DistancePlayer is {} distance && distance < Settings.PickupRange)
            .OrderBy(x => x.Entity?.DistancePlayer ?? int.MaxValue);

        return labels?
            .Where(x => x.Entity?.Path != null && IsLabelClickable(x.Label, x.ClientRect))
            .Select(x => new PickItItemData(x, GameController))
            .Where(x => x.Entity != null
                        && (!filterAttempts || x.AttemptedPickups == 0)
                        && DoWePickThis(x)
                        && (Settings.PickUpWhenInventoryIsFull || CanFitInventory(x))) ?? [];
    }

    private async SyncTask<bool> PickAsync(Entity item, Element label, RectangleF? customRect, Action onNonClickable)
    {
        var tryCount = 0;
        while (tryCount < 3)
        {
            if (!IsLabelClickable(label, customRect))
            {
                onNonClickable();
                return true;
            }

            if (!Settings.IgnoreMoving && GameController.Player.GetComponent<Actor>().isMoving)
            {
                if (item.DistancePlayer > Settings.ItemDistanceToIgnoreMoving.Value)
                {
                    await TaskUtils.NextFrame();
                    continue;
                }
            }

            var position = label.GetClientRect().ClickRandom(5, 3) + GameController.Window.GetWindowRectangleTimeCache.TopLeft;
            if (OkayToClick)
            {
                if (!IsTargeted(item, label))
                {
                    await SetCursorPositionAsync(position, item, label);
                }
                else
                {
                    if (await CheckPortal(label)) return true;
                    if (!IsTargeted(item, label))
                    {
                        await TaskUtils.NextFrame();
                        continue;
                    }

                    Input.Click(MouseButtons.Left);
                    _sinceLastClick.Restart();
                    tryCount++;
                }
            }

            await TaskUtils.NextFrame();
        }

        return true;
    }

    private async Task<bool> CheckPortal(Element label)
    {
        if (!IsPortalNearby(_portalLabel.Value, label)) return false;
        // in case of portal nearby do extra checks with delays
        if (IsPortalTargeted(_portalLabel.Value))
        {
            return true;
        }

        await Task.Delay(25);
        return IsPortalTargeted(_portalLabel.Value);
    }

    private static bool IsTargeted(Entity item, Element label)
    {
        if (item == null) return false;
        if (item.GetComponent<Targetable>()?.isTargeted is { } isTargeted)
        {
            return isTargeted;
        }

        return label is { HasShinyHighlight: true };
    }

    private class MouseMovement
    {
        public static double GaussianRandom(double mean, double stdDev)
        {
            double u1 = 1.0 - Random.Shared.NextDouble();
            double u2 = 1.0 - Random.Shared.NextDouble();
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            return mean + stdDev * randStdNormal;
        }

        public static double PerlinRandom(double mean, double amplitude, double t)
        {
            double noise = Math.Sin(t) * Math.Cos(t * 2.1) * Math.Sin(t * 1.72);
            return mean + noise * amplitude;
        }

        public static double BezierRandom(double start, double end, double t)
        {
            double control1 = start + (end - start) * 0.3 * (1 + Random.Shared.NextDouble());
            double control2 = start + (end - start) * 0.7 * (1 + Random.Shared.NextDouble());
            return Math.Pow(1 - t, 3) * start + 
                   3 * Math.Pow(1 - t, 2) * t * control1 + 
                   3 * (1 - t) * Math.Pow(t, 2) * control2 + 
                   Math.Pow(t, 3) * end;
        }

        public static Vector2 GetNextPoint(Vector2 start, Vector2 end, float progress, MouseMovementMode mode)
        {
            switch (mode)
            {
                case MouseMovementMode.Gaussian:
                    var interpolated = Vector2.Lerp(start, end, progress);
                    return interpolated + new Vector2(
                        (float)GaussianRandom(0, 2),
                        (float)GaussianRandom(0, 2)
                    );

                case MouseMovementMode.Perlin:
                    return new Vector2(
                        (float)PerlinRandom(start.X + (end.X - start.X) * progress, 2, progress * 10),
                        (float)PerlinRandom(start.Y + (end.Y - start.Y) * progress, 2, progress * 10)
                    );

                case MouseMovementMode.Bezier:
                    return new Vector2(
                        (float)BezierRandom(start.X, end.X, progress),
                        (float)BezierRandom(start.Y, end.Y, progress)
                    );

                case MouseMovementMode.Combined:
                    var bezierBase = new Vector2(
                        (float)BezierRandom(start.X, end.X, progress),
                        (float)BezierRandom(start.Y, end.Y, progress)
                    );
                    
                    var perlinOffset = new Vector2(
                        (float)PerlinRandom(0, 0.3, progress * 10),
                        (float)PerlinRandom(0, 0.3, progress * 10)
                    );
                    
                    var gaussianOffset = new Vector2(
                        (float)GaussianRandom(0, 0.1),
                        (float)GaussianRandom(0, 0.1)
                    );
                    
                    return bezierBase + (perlinOffset * 0.7f) + (gaussianOffset * 0.3f);

                default:
                    return Vector2.Lerp(start, end, progress);
            }
        }
    }

    private async SyncTask<bool> SetCursorPositionAsync(Vector2 position, Entity item, Element label)
    {
        var currentPos = Input.MousePosition;
        var targetPos = position;
        var movementMode = Enum.Parse<MouseMovementMode>(Settings.MouseMovement.MovementType.Value);
        
        if (Settings.MouseMovement.LogMovement)
        {
            DebugWindow.LogMsg($"[Mouse] Start: {currentPos:F0} -> Target: {targetPos:F0} (Distance: {Vector2.Distance(currentPos, targetPos):F0})");
            DebugWindow.LogMsg($"[Mouse] Using movement mode: {movementMode}");
        }
        
        var distance = Vector2.Distance(currentPos, targetPos);
        var baseSteps = Math.Max(Settings.MouseMovement.MinSteps.Value, 
            (int)(distance / Settings.MouseMovement.BaseSpeed.Value));
        
        var randomFactor = Settings.MouseMovement.RandomizationFactor.Value;
        
        // Calculate steps based on selected movement mode
        var actualSteps = movementMode switch
        {
            MouseMovementMode.Gaussian => (int)MouseMovement.GaussianRandom(baseSteps, baseSteps * randomFactor),
            MouseMovementMode.Perlin => (int)MouseMovement.PerlinRandom(baseSteps, baseSteps * randomFactor, Random.Shared.NextDouble() * 10),
            MouseMovementMode.Bezier => (int)MouseMovement.BezierRandom(baseSteps * (1 - randomFactor), baseSteps * (1 + randomFactor), Random.Shared.NextDouble()),
            MouseMovementMode.Combined => (int)((MouseMovement.GaussianRandom(baseSteps, baseSteps * randomFactor/2) + 
                                                MouseMovement.PerlinRandom(baseSteps, baseSteps * randomFactor/2, Random.Shared.NextDouble() * 10) +
                                                MouseMovement.BezierRandom(baseSteps * (1 - randomFactor/2), baseSteps * (1 + randomFactor/2), Random.Shared.NextDouble())) / 3),
            _ => baseSteps
        };
        
        if (Settings.MouseMovement.LogMovement)
        {
            DebugWindow.LogMsg($"[Mouse] Using {actualSteps} steps (base: {baseSteps})");
        }
        
        var lastDelay = 0;
        var totalTime = 0;
        
        for (int i = 0; i < actualSteps; i++)
        {
            var progress = (i + 1f) / actualSteps;
            var speed = 1 - Math.Pow(Math.Abs(progress - 0.5) * 2, 2);
            
            var nextPos = MouseMovement.GetNextPoint(
                currentPos, 
                targetPos, 
                progress, 
                movementMode
            );
            
            Input.SetCursorPos(nextPos);
            
            var baseDelay = Math.Max(5, Settings.MouseMovement.BaseDelay.Value * (1 - speed));
            var newDelay = (int)MouseMovement.GetNextPoint(
                new Vector2((float)lastDelay, (float)lastDelay),
                new Vector2((float)baseDelay, (float)baseDelay),
                progress,
                movementMode).X;
                
            newDelay = Math.Max(1, newDelay);
            lastDelay = newDelay;
            totalTime += newDelay;
            
            if (Settings.MouseMovement.LogMovement && i % 5 == 0)
            {
                DebugWindow.LogMsg($"[Mouse] Step {i}: Pos={nextPos:F0}, Delay={newDelay}ms, Speed={speed:F2}");
            }
            
            await Task.Delay(newDelay);
        }
        
        Input.SetCursorPos(targetPos);
        if (Settings.MouseMovement.LogMovement)
        {
            DebugWindow.LogMsg($"[Mouse] Complete - Total time: {totalTime}ms");
        }
        
        return await TaskUtils.CheckEveryFrame(() => IsTargeted(item, label), new CancellationTokenSource(60).Token);
    }

    #endregion
}