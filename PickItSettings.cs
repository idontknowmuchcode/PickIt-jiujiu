using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using System.Linq;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using System.Numerics;

namespace PickIt;

public enum MouseMovementMode
{
    Gaussian,
    Perlin,
    Bezier,
    Combined
}

public class PickItSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public ToggleNode ShowInventoryView { get; set; } = new ToggleNode(true);
    public RangeNode<Vector2> InventoryPos { get; set; } = new RangeNode<Vector2>(new Vector2(0, 0), Vector2.Zero, new Vector2(4000, 4000));
    public HotkeyNode ProfilerHotkey { get; set; } = Keys.None;
    public HotkeyNode PickUpKey { get; set; } = Keys.F;
    public ToggleNode PickUpWhenInventoryIsFull { get; set; } = new ToggleNode(false);
    public RangeNode<int> PickupRange { get; set; } = new RangeNode<int>(600, 1, 1000);
    public ToggleNode IgnoreMoving { get; set; } = new ToggleNode(false);
    public RangeNode<int> ItemDistanceToIgnoreMoving { get; set; } = new RangeNode<int>(20, 0, 1000);
    public RangeNode<int> PauseBetweenClicks { get; set; } = new RangeNode<int>(100, 0, 500);
    public ToggleNode AutoClickHoveredLootInRange { get; set; } = new ToggleNode(false);
    public ToggleNode LazyLooting { get; set; } = new ToggleNode(false);
    public ToggleNode NoLazyLootingWhileEnemyClose { get; set; } = new ToggleNode(false);
    public HotkeyNode LazyLootingPauseKey { get; set; } = new HotkeyNode(Keys.Space);
    public ToggleNode PickUpEverything { get; set; } = new ToggleNode(false);
    public ToggleNode ClickChests { get; set; } = new ToggleNode(true);
    public ToggleNode ClickQuestChests { get; set; } = new ToggleNode(true);
    public ToggleNode ItemizeCorpses { get; set; } = new ToggleNode(true);

    [JsonIgnore]
    public TextNode FilterTest { get; set; } = new TextNode();

    [JsonIgnore]
    public ButtonNode ReloadFilters { get; set; } = new ButtonNode();

    [Menu("Use a Custom \"\\config\\custom_folder\" folder ")]
    public TextNode CustomConfigDir { get; set; } = new TextNode();

    public List<PickitRule> PickitRules = new List<PickitRule>();

    [JsonIgnore]
    public FilterNode Filters { get; } = new FilterNode();

    [JsonIgnore]
    public ToggleNode DebugHighlight { get; set; } = new ToggleNode(false);

    [Submenu]
    public class MouseMovementSettings
    {
        [Menu("Movement Type", "For most human-like movement:\n1. Start with 'Gaussian' - it's the most natural\n2. 'Perlin' adds subtle noise/shakiness like a real hand\n3. 'Combined' mixes Gaussian + Perlin + Bezier for anti-pattern\n4. Avoid 'Bezier' alone as it's too predictable\n\nTip: Switch between Gaussian and Combined occasionally")]
        public ListNode MovementType { get; set; } = new ListNode() { 
            Value = MouseMovementMode.Gaussian.ToString(),
            Values = Enum.GetNames(typeof(MouseMovementMode)).ToList()
        };

        [Menu("Base Movement Speed", "Controls how fast the mouse moves\n" +
            "Higher = faster, more direct\n" +
            "Lower = slower, smoother")]
        public RangeNode<int> BaseSpeed { get; set; } = new RangeNode<int>(40, 10, 100);

        [Menu("Minimum Steps", "Controls how many points the mouse follows\n" +
            "More steps = smoother movement but slower\n" +
            "Fewer steps = faster but more rigid")]
        public RangeNode<int> MinSteps { get; set; } = new RangeNode<int>(5, 3, 20);

        [Menu("Log Mouse Movement", "Debug feature - leave off unless troubleshooting")]
        public ToggleNode LogMovement { get; set; } = new ToggleNode(false);

        [Menu("Base Delay (ms)", "Controls time between each mouse movement step:\n\n- Higher values (25ms): Slower, deliberate movement\n- Lower values (10ms): Faster movement\n- Works with MinSteps and BaseSpeed for overall pattern\n\nRecommended: 18-25ms\n")]
        public RangeNode<int> BaseDelay { get; set; } = new RangeNode<int>(20, 5, 50);

        [Menu("Randomization Amount", "\nRecommended: 0.12-0.18\n<0.1: High (too mechanical)\n>0.2: High (too erratic)\n>0.3: Extreme")]
        public RangeNode<float> RandomizationFactor { get; set; } = new RangeNode<float>(0.15f, 0.12f, 0.18f);
    }

    [Menu("Mouse Movement")]
    public MouseMovementSettings MouseMovement { get; set; } = new MouseMovementSettings();
}

[Submenu(RenderMethod = nameof(Render))]
public class FilterNode
{
    public void Render(PickIt pickit)
    {
        if (ImGui.Button("Open filter Folder"))
        {
            var configDir = pickit.ConfigDirectory;
            var customConfigFileDirectory = !string.IsNullOrEmpty(pickit.Settings.CustomConfigDir)
                ? Path.Combine(Path.GetDirectoryName(pickit.ConfigDirectory), pickit.Settings.CustomConfigDir)
                : null;

            var directoryToOpen = Directory.Exists(customConfigFileDirectory)
                ? customConfigFileDirectory
                : configDir;

            Process.Start("explorer.exe", directoryToOpen);
        }

        ImGui.Separator();
        ImGui.BulletText("Select Rules To Load");
        ImGui.BulletText("Ordering rule sets so general items will match first rather than last will improve performance");

        var tempNpcInvRules = new List<PickitRule>(pickit.Settings.PickitRules); // Create a copy

        for (int i = 0; i < tempNpcInvRules.Count; i++)
        {
            ImGui.PushID(i);
            if (ImGui.ArrowButton("##upButton", ImGuiDir.Up) && i > 0)
                (tempNpcInvRules[i - 1], tempNpcInvRules[i]) = (tempNpcInvRules[i], tempNpcInvRules[i - 1]);

            ImGui.SameLine();
            ImGui.Text(" ");
            ImGui.SameLine();

            if (ImGui.ArrowButton("##downButton", ImGuiDir.Down) && i < tempNpcInvRules.Count - 1)
                (tempNpcInvRules[i + 1], tempNpcInvRules[i]) = (tempNpcInvRules[i], tempNpcInvRules[i + 1]);

            ImGui.SameLine();
            ImGui.Text(" - ");
            ImGui.SameLine();

            ImGui.Checkbox($"{tempNpcInvRules[i].Name}###enabled", ref tempNpcInvRules[i].Enabled);
            ImGui.PopID();
        }

        pickit.Settings.PickitRules = tempNpcInvRules;
    }
}

public record PickitRule(string Name, string Location, bool Enabled)
{
    public bool Enabled = Enabled;
}