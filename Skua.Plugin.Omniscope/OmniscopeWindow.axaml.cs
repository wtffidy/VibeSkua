using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Core.Models.Auras;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Skills;

namespace Skua.Plugin.Omniscope;

/// <summary>
/// Displays live, read-only game telemetry using the public VibeSkua scripting interfaces.
/// </summary>
public partial class OmniscopeWindow : Window
{
    private readonly IScriptInterface _bot;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly ObservableCollection<EntityRow> _entities = [];
    private EntityRow[] _entitySnapshot = [];
    private readonly ObservableCollection<string> _players = [];
    private readonly ObservableCollection<AuraRow> _auras = [];
    private readonly ObservableCollection<SkillRow> _skills = [];
    private readonly ObservableCollection<string> _quests = [];
    private readonly ObservableCollection<InventoryRow> _inventory = [];
    private readonly ObservableCollection<EventRow> _events = [];
    private string _lastMap = string.Empty;
    private string _lastTarget = string.Empty;
    private string _lastPlayerState = string.Empty;
    private string _lastVitals = string.Empty;
    private string _lastClass = string.Empty;
    private string _lastAuraSignature = string.Empty;
    private string _lastQuestSignature = string.Empty;
    private int _lastMonsterCount = -1;
    private int _lastPlayerCount = -1;
    private int _lastKillCount = -1;
    private bool _refreshing;

    public OmniscopeWindow(IScriptInterface bot)
    {
        InitializeComponent();
        _bot = bot;

        EntityList.ItemsSource = _entities;
        PlayerList.ItemsSource = _players;
        AuraList.ItemsSource = _auras;
        SkillList.ItemsSource = _skills;
        QuestList.ItemsSource = _quests;
        InventoryList.ItemsSource = _inventory;
        EventList.ItemsSource = _events;

        EntityList.SelectionChanged += EntityList_SelectionChanged;
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Skua.Shared.Avalonia.Services.WindowPlacementHelper.CenterOnActiveScreen(this);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshTelemetry();
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e) => _refreshTimer.Stop();

    private void RefreshTimer_Tick(object? sender, EventArgs e) => RefreshTelemetry();

    private void RefreshTelemetry()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            DateTime now = DateTime.Now;
            ClockText.Text = now.ToString("HH:mm:ss");

            bool loggedIn = _bot.Player.LoggedIn;
            bool playing = _bot.Player.Playing;
            ConnectionText.Text = loggedIn ? playing ? "LIVE / AVATAR ACTIVE" : "CONNECTED / LOADING AVATAR" : "WAITING FOR GAME SESSION";
            ConnectionIndicator.Fill = playing ? Avalonia.Media.Brushes.MediumSpringGreen : loggedIn ? Avalonia.Media.Brushes.Goldenrod : Avalonia.Media.Brushes.IndianRed;

            PlayerNameText.Text = loggedIn ? _bot.Player.Username : "No active player session";
            PlayerClassText.Text = $"Class: {_bot.Player.CurrentClass?.Name ?? "Unavailable"}  /  Rank {_bot.Player.CurrentClassRank}";
            LevelText.Text = loggedIn ? _bot.Player.Level.ToString("N0") : "--";
            XpText.Text = loggedIn ? _bot.Stats.ExperienceProgressText : "XP unavailable";
            SetProgress(XpProgressBar, _bot.Stats.ExperienceProgress, 100);
            StateText.Text = loggedIn ? GetPlayerState(_bot.Player.State) : "OFFLINE";
            GoldText.Text = loggedIn ? _bot.Player.Gold.ToString("N0") : "--";
            CoinsText.Text = loggedIn ? _bot.Player.Coins.ToString("N0") : "--";
            GuildText.Text = loggedIn ? NullLabel(_bot.Player.Guild) : "--";
            ServerText.Text = loggedIn ? NullLabel(_bot.Player.ServerIP) : "--";

            int health = loggedIn ? _bot.Player.Health : 0;
            int maxHealth = loggedIn ? _bot.Player.MaxHealth : 0;
            int mana = loggedIn ? _bot.Player.Mana : 0;
            int maxMana = loggedIn ? _bot.Player.MaxMana : 0;
            VitalsMetric.Text = loggedIn ? $"{health:N0} / {maxHealth:N0} HP" : "-- / -- HP";
            HealthText.Text = loggedIn ? $"{health:N0} / {maxHealth:N0}" : "-- / --";
            ManaText.Text = loggedIn ? $"{mana:N0} / {maxMana:N0}" : "-- / --";
            ManaMetric.Text = loggedIn ? $"{mana:N0} / {maxMana:N0} MP" : "-- / -- MP";
            SetProgress(HealthProgressBar, health, maxHealth);
            SetProgress(HealthDetailBar, health, maxHealth);
            SetProgress(ManaProgressBar, mana, maxMana);

            PositionMetric.Text = loggedIn ? $"X {_bot.Player.X:N0}  /  Y {_bot.Player.Y:N0}" : "X --  /  Y --";
            CellMetric.Text = loggedIn ? $"Cell {NullLabel(_bot.Player.Cell)}  /  Pad {NullLabel(_bot.Player.Pad)}" : "Cell --  /  Pad --";

            bool mapLoaded = _bot.Map.Loaded;
            string mapName = mapLoaded ? _bot.Map.FullName : "Map unavailable";
            MapMetric.Text = mapName;
            RoomMetric.Text = mapLoaded ? $"Room {_bot.Map.RoomNumber}  /  ID {_bot.Map.RoomID}  /  {_bot.Map.PlayerCount} players" : "Waiting for map data";
            SessionText.Text = loggedIn
                ? $"Avatar loaded: {_bot.Player.Loaded}\nTarget: {(_bot.Player.Target?.Name ?? "None")}\nCombat: {(_bot.Player.InCombat ? "Engaged" : "Idle")}  /  AFK: {(_bot.Player.AFK ? "Yes" : "No")}"
                : "Waiting for the game client to expose a logged-in avatar.";

            TrackChange(ref _lastMap, mapName, "WORLD TRANSITION");
            string playerState = loggedIn ? GetPlayerState(_bot.Player.State) : "OFFLINE";
            TrackChange(ref _lastPlayerState, playerState, "PLAYER STATE");

            string className = loggedIn ? _bot.Player.CurrentClass?.Name ?? "Unknown class" : "Offline";
            TrackChange(ref _lastClass, className, "CLASS CHANGE");

            string vitals = loggedIn ? $"{health}/{maxHealth} HP · {mana}/{maxMana} MP" : "Offline";
            if (_lastVitals.Length > 0 && _lastVitals != vitals)
                AddEvent("VITALS UPDATE", vitals);
            _lastVitals = vitals;

            string target = _bot.Player.Target is Monster currentTarget ? $"{currentTarget.Name} [{currentTarget.ID}]" : "No target";
            TrackChange(ref _lastTarget, target, "TARGET CHANGE");

            Monster[] monsters = mapLoaded ? _bot.Monsters.MapMonsters.ToArray() : [];
            int aliveCount = monsters.Count(static monster => monster.Alive);
            EntityMetric.Text = monsters.Length.ToString("N0");
            EntitySubMetric.Text = $"{aliveCount} alive  /  {monsters.Length - aliveCount} inactive";
            if (_lastMonsterCount != monsters.Length)
            {
                if (_lastMonsterCount >= 0)
                    AddEvent("ENTITY FIELD", $"{monsters.Length} monsters observed in the current map");
                _lastMonsterCount = monsters.Length;
            }

            UpdateEntities(monsters);
            UpdatePlayers(mapLoaded);
            UpdateAuras(loggedIn);
            UpdateSkills(loggedIn);
            UpdateSessionAnalytics(loggedIn);

            Skua.Core.Models.Quests.Quest[] activeQuests = loggedIn ? _bot.Quests.Active.ToArray() : [];
            Skua.Core.Models.Quests.Quest[] readyQuests = loggedIn ? _bot.Quests.Completed.ToArray() : [];
            string questSignature = $"{activeQuests.Length}:{readyQuests.Length}";
            QuestMetric.Text = $"Quests: {activeQuests.Length:N0} active  /  {readyQuests.Length:N0} ready";
            if (_lastQuestSignature.Length > 0 && _lastQuestSignature != questSignature)
                AddEvent("QUEST TRACKER", QuestMetric.Text);
            _lastQuestSignature = questSignature;

            string[] questRows = readyQuests.Take(4).Select(static quest => $"READY  /  {quest.Name} [{quest.ID}]")
                .Concat(activeQuests.Take(4).Select(static quest => $"ACTIVE  /  {quest.Name} [{quest.ID}]"))
                .ToArray();
            if (!_quests.SequenceEqual(questRows))
            {
                _quests.Clear();
                foreach (string questRow in questRows)
                    _quests.Add(questRow);
            }
        }
        catch (Exception ex)
        {
            ConnectionText.Text = "TELEMETRY DEGRADED";
            ConnectionIndicator.Fill = Avalonia.Media.Brushes.OrangeRed;
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE telemetry refresh failed: {ex}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateEntities(Monster[] monsters)
    {
        _entitySnapshot = monsters
            .OrderByDescending(static monster => monster.Alive)
            .ThenBy(static monster => monster.Name)
            .Take(500)
            .Select(monster => new EntityRow
            {
                Name = monster.Name,
                Cell = $"CELL {NullLabel(monster.Cell)}",
                Health = $"{monster.HP:N0}/{monster.MaxHP:N0}",
                State = monster.Alive ? "ALIVE" : "DOWN",
                Id = monster.ID.ToString(),
                Source = monster
            })
            .ToArray();

        ApplyEntityFilter();
    }

    private void EntitySearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyEntityFilter();

    private void ApplyEntityFilter()
    {
        string filter = EntitySearchBox?.Text?.Trim() ?? string.Empty;
        EntityRow[] rows = string.IsNullOrWhiteSpace(filter)
            ? _entitySnapshot
            : _entitySnapshot.Where(row =>
                row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Cell.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        bool changed = _entities.Count != rows.Length;
        if (!changed)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (!_entities[i].HasSameDisplay(rows[i]))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
            return;

        string? selectedId = (EntityList.SelectedItem as EntityRow)?.Id;
        _entities.Clear();
        foreach (EntityRow row in rows)
            _entities.Add(row);

        EntityList.SelectedItem = selectedId is null ? null : _entities.FirstOrDefault(row => row.Id == selectedId);
    }

    private void UpdatePlayers(bool mapLoaded)
    {
        string[] names = mapLoaded ? _bot.Map.PlayerNames?.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static name => name).ToArray() ?? [] : [];
        RosterMetric.Text = $"Players in instance: {names.Length:N0}";
        if (_lastPlayerCount != names.Length)
        {
            if (_lastPlayerCount >= 0)
                AddEvent("WORLD ROSTER", $"{names.Length} players in the current instance");
            _lastPlayerCount = names.Length;
        }

        if (_players.SequenceEqual(names))
            return;

        _players.Clear();
        foreach (string name in names)
            _players.Add(name);
    }

    private void UpdateAuras(bool loggedIn)
    {
        AuraSnapshot[] snapshots = loggedIn ? _bot.Self.Snapshots.ToArray() : [];
        AuraRow[] rows = snapshots
            .OrderBy(static aura => aura.Name)
            .Select(aura => new AuraRow
            {
                Name = aura.Name,
                Stacks = aura.Stacks > 1 ? $"×{aura.Stacks}" : aura.Persistent ? "PASSIVE" : "ACTIVE",
                Duration = aura.Persistent || aura.Duration <= 0 ? "Persistent effect" : $"{Math.Max(0, aura.RemainingTime):0}s remaining"
            })
            .ToArray();

        AuraMetric.Text = $"Self effects: {rows.Length:N0}  /  Target: {_bot.Target.Snapshots.Count:N0}";
        string signature = string.Join("|", rows.Select(static row => $"{row.Name}:{row.Stacks}:{row.Duration}"));
        if (_lastAuraSignature.Length > 0 && _lastAuraSignature != signature)
            AddEvent("AURA FIELD", $"{rows.Length} self effects currently visible");
        _lastAuraSignature = signature;

        if (_auras.Count == rows.Length && _auras.Zip(rows).All(static pair => pair.First.HasSameDisplay(pair.Second)))
            return;

        _auras.Clear();
        foreach (AuraRow row in rows)
            _auras.Add(row);
    }

    private void UpdateSkills(bool loggedIn)
    {
        SkillInfo[] skills = loggedIn ? _bot.Player.Skills?.ToArray() ?? [] : [];
        SkillRow[] rows = skills.Take(5).Select((skill, index) => new SkillRow
        {
            Slot = index == 0 ? "AA" : (index + 1).ToString(),
            Name = string.IsNullOrWhiteSpace(skill.Name) ? $"Skill {index + 1}" : skill.Name,
            Cooldown = $"CD {(skill.Cooldown / 1000d):0.#}s",
            Description = $"MP {skill.MP:N0}  /  Range {skill.Range:N0}  /  {skill.Type}"
        }).ToArray();

        SkillMetric.Text = loggedIn
            ? $"Engine: {(_bot.Skills.TimerRunning ? "RUNNING" : "IDLE")}  /  {_bot.Skills.SkillInterval} ms  /  {_bot.Skills.SkillUseMode}"
            : "Combat engine: offline";

        if (_skills.Count == rows.Length && _skills.Zip(rows).All(static pair => pair.First.HasSameDisplay(pair.Second)))
            return;

        _skills.Clear();
        foreach (SkillRow row in rows)
            _skills.Add(row);
    }

    private void UpdateSessionAnalytics(bool loggedIn)
    {
        TimeSpan sessionTime = _bot.Stats.SessionTime;
        SessionTimeMetric.Text = sessionTime.ToString(@"hh\:mm\:ss");
        KillsMetric.Text = loggedIn ? _bot.Stats.Kills.ToString("N0") : "0";
        DropsMetric.Text = loggedIn ? _bot.Stats.Drops.ToString("N0") : "0";
        DeathsMetric.Text = loggedIn ? _bot.Stats.Deaths.ToString("N0") : "0";
        KpmMetric.Text = loggedIn ? _bot.Stats.KillsPerMinute.ToString("N1") : "0.0";

        int inventoryUsed = loggedIn ? _bot.Stats.InventoryFilledSpace : 0;
        int inventoryCapacity = loggedIn ? _bot.Stats.InventorySpace : 0;
        InventoryMetric.Text = loggedIn ? $"{inventoryUsed:N0} / {inventoryCapacity:N0} slots" : "-- / --";
        SetProgress(InventoryProgressBar, inventoryUsed, inventoryCapacity);

        InventoryRow[] inventoryRows = loggedIn
            ? _bot.Inventory.Items
                .OrderByDescending(static item => item.Equipped)
                .ThenBy(static item => item.Name)
                .Take(12)
                .Select(item => new InventoryRow
                {
                    Name = $"{(item.Equipped ? "EQUIPPED  /  " : string.Empty)}{(_bot.Inventory.IsFavorited(item.ID) ? "★  " : string.Empty)}{item.Name}",
                    Quantity = $"×{item.Quantity:N0}"
                })
                .ToArray()
            : [];
        if (_inventory.Count != inventoryRows.Length || !_inventory.Zip(inventoryRows).All(static pair => pair.First.HasSameDisplay(pair.Second)))
        {
            _inventory.Clear();
            foreach (InventoryRow row in inventoryRows)
                _inventory.Add(row);
        }

        if (loggedIn && _lastKillCount != _bot.Stats.Kills)
        {
            if (_lastKillCount >= 0)
                AddEvent("COMBAT METRICS", $"{_bot.Stats.Kills - _lastKillCount:+#;-#;0} kills · {_bot.Stats.KillsPerMinute:N1}/min");
            _lastKillCount = _bot.Stats.Kills;
        }
    }

    private void EntityList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (EntityList.SelectedItem is EntityRow selected)
        {
            ShowEntityDetails(selected);
            return;
        }

        SelectedEntityText.Text = "Select a monster to inspect its live properties.";
        SelectedEntityDetailsText.Text = "Read-only entity inspection";
    }

    private void ShowEntityDetails(EntityRow row)
    {
        Monster? monster = row.Source;
        if (monster is null)
            return;

        SelectedEntityText.Text = $"{monster.Name}  /  ID {monster.ID}";
        SelectedEntityDetailsText.Text =
            $"Race: {NullLabel(monster.Race)}   Map ID: {monster.MapID}   State: {monster.State}\n" +
            $"HP: {monster.HP:N0} / {monster.MaxHP:N0}   Cell: {NullLabel(monster.Cell)}\n" +
            $"Client asset: {NullLabel(monster.FileName)}";
    }

    private void TrackChange(ref string previous, string current, string category)
    {
        if (previous.Length > 0 && previous != current)
            AddEvent(category, current);

        previous = current;
    }

    private void AddEvent(string category, string detail)
    {
        _events.Insert(0, new EventRow
        {
            Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            Category = category,
            Detail = detail
        });

        while (_events.Count > 250)
            _events.RemoveAt(_events.Count - 1);

        EventCountText.Text = $"{_events.Count:N0} EVENTS";
    }

    private static void SetProgress(Border bar, double current, double maximum)
    {
        double ratio = maximum <= 0 ? 0 : Math.Clamp(current / maximum, 0, 1);
        if (bar.Parent is Border track)
            bar.Width = Math.Max(0, track.Bounds.Width * ratio);
    }

    private static string GetPlayerState(int state) => state switch
    {
        0 => "DEAD",
        1 => "IDLE",
        2 => "IN COMBAT",
        _ => $"STATE {state}"
    };

    private static string NullLabel(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

    private sealed class EntityRow
    {
        public string Name { get; init; } = string.Empty;
        public string Cell { get; init; } = string.Empty;
        public string Health { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string Id { get; init; } = string.Empty;
        public Monster? Source { get; init; }

        public bool HasSameDisplay(EntityRow other) =>
            Name == other.Name && Cell == other.Cell && Health == other.Health &&
            State == other.State && Id == other.Id;
    }

    private sealed class AuraRow
    {
        public string Name { get; init; } = string.Empty;
        public string Stacks { get; init; } = string.Empty;
        public string Duration { get; init; } = string.Empty;

        public bool HasSameDisplay(AuraRow other) =>
            Name == other.Name && Stacks == other.Stacks && Duration == other.Duration;
    }

    private sealed class SkillRow
    {
        public string Slot { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Cooldown { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;

        public bool HasSameDisplay(SkillRow other) =>
            Slot == other.Slot && Name == other.Name && Cooldown == other.Cooldown &&
            Description == other.Description;
    }

    private sealed class InventoryRow
    {
        public string Name { get; init; } = string.Empty;
        public string Quantity { get; init; } = string.Empty;

        public bool HasSameDisplay(InventoryRow other) => Name == other.Name && Quantity == other.Quantity;
    }

    private sealed class EventRow
    {
        public string Time { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }
}