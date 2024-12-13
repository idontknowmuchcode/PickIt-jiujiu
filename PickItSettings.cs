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
    Linear,
    Gaussian,
    Perlin,
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
        
        [Menu("Show Debug Info", "Display fatigue and movement debug information")]
        public ToggleNode ShowDebugInfo { get; set; } = new ToggleNode(false);

        [Menu("Log Mouse Movement", "Debug feature - leave off unless troubleshooting")]
        public ToggleNode LogMovement { get; set; } = new ToggleNode(false);

        [Menu("Movement Type", "For most human-like movement:\n1. Start with 'Gaussian' - it's the most natural\n2. 'Perlin' adds subtle noise/shakiness like a real hand\n3. 'Combined' mixes Gaussian + Perlin + Bezier for anti-pattern\n4. Avoid 'Bezier' alone as it's too predictable\n\nTip: Switch between Gaussian and Combined occasionally")]
        public ListNode MovementType { get; set; } = new ListNode() { 
            Value = MouseMovementMode.Gaussian.ToString(),
            Values = Enum.GetNames(typeof(MouseMovementMode)).ToList()
        };

        [Menu("Base Movement Speed", "Controls how fast the mouse moves\n" +
            "Higher = faster, more direct\n" +
            "Lower = slower, smoother")]
        public RangeNode<int> BaseSpeed { get; set; } = new RangeNode<int>(25, 15, 100);

        [Menu("Minimum Steps", "Controls how many points the mouse follows\n" +
            "More steps = smoother movement but slower\n" +
            "Fewer steps = faster but more rigid")]
        public RangeNode<int> MinSteps { get; set; } = new RangeNode<int>(8, 4, 15);

      

        [Menu("Base Delay (ms)", "Controls time between each mouse movement step:\n\n- Higher values (25ms): Slower, deliberate movement\n- Lower values (10ms): Faster movement\n- Works with MinSteps and BaseSpeed for overall pattern\n\nRecommended: 18-25ms\n")]
        public RangeNode<int> BaseDelay { get; set; } = new RangeNode<int>(22, 15, 50);

        [Menu("Randomization Amount", "\nRecommended: 0.12-0.18\n<0.1: High (too mechanical)\n>0.2: High (too erratic)\n>0.3: Extreme")]
        public RangeNode<float> RandomizationFactor { get; set; } = new RangeNode<float>(0.15f, 0.08f, 0.4f);

    }

    [Menu("Mouse Movement")]
    public MouseMovementSettings MouseMovement { get; set; } = new MouseMovementSettings();

    [Submenu]
    public class FatigueSettings
    {
        [Menu("Enable Fatigue System", "Simulates human-like mouse fatigue:\n\n" +
            "- Gradually increases movement variation over time\n" +
            "- Recovers during periods of inactivity\n" +
            "- Affects cursor speed and precision\n" +
            "- Creates more natural, varied movements\n\n" +
            "Recommended: Keep enabled for most human-like behavior")]
        public ToggleNode EnableFatigue { get; set; } = new ToggleNode(true);

        [Menu("Base Fatigue Increase", "Base amount of fatigue added per action\nHigher = faster fatigue buildup")]
        public RangeNode<float> BaseFatigueIncrease { get; set; } = new RangeNode<float>(0.002f, 0.0005f, 0.005f);

        [Menu("Distance-Based Fatigue", "Additional fatigue based on mouse movement distance\nHigher = more fatigue for longer movements")]
        public RangeNode<float> DistanceFatigueMultiplier { get; set; } = new RangeNode<float>(0.001f, 0.0002f, 0.003f);

        [Menu("Recovery Rate", "Chance to reduce fatigue slightly each action\nHigher = faster recovery")]
        public RangeNode<float> RecoveryChance { get; set; } = new RangeNode<float>(0.15f, 0.05f, 0.25f);

        [Menu("Recovery Amount", "How much fatigue is reduced when recovery occurs")]
        public RangeNode<float> RecoveryAmount { get; set; } = new RangeNode<float>(0.03f, 0.01f, 0.08f);

        [Menu("Rest Recovery Time", "Minutes of inactivity before fatigue fully resets")]
        public RangeNode<float> RestRecoveryMinutes { get; set; } = new RangeNode<float>(3.0f, 1.0f, 8.0f);

        [Menu("Maximum Fatigue", "Maximum fatigue level that can accumulate\nHigher = more variation in late-game movements")]
        public RangeNode<float> MaxFatigue { get; set; } = new RangeNode<float>(0.8f, 0.4f, 1.2f);

        [Menu("Fatigue Impact", "How much fatigue affects mouse movement\nHigher = more pronounced effect on movement")]
        public RangeNode<float> FatigueImpactMultiplier { get; set; } = new RangeNode<float>(0.2f, 0.1f, 0.4f);
    }

    [Menu("Fatigue System")]
    public FatigueSettings Fatigue { get; set; } = new FatigueSettings();
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