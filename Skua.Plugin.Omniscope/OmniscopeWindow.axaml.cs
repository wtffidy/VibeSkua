using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Core.Models.Monsters;

namespace Skua.Plugin.Omniscope;

/// <summary>
/// Displays live, read-only game telemetry using the public VibeSkua scripting interfaces.
/// </summary>
public partial class OmniscopeWindow : Window
{
    private readonly IScriptInterface _bot;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly ObservableCollection<EntityRow> _entities = [];
    private readonly ObservableCollection<EventRow> _events = [];
    private string _lastMap = string.Empty;
    private string _lastTarget = string.Empty;
    private string _lastPlayerState = string.Empty;
    private string _lastVitals = string.Empty;
    private int _lastMonsterCount = -1;
    private bool _refreshing;

    public OmniscopeWindow(IScriptInterface bot)
    {
        InitializeComponent();
        _bot = bot;
        EntityList.ItemsSource = _entities;
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
            ConnectionText.Text = loggedIn ? "LIVE TELEMETRY" : "WAITING FOR GAME SESSION";
            PlayerNameText.Text = loggedIn ? _bot.Player.Username : "No active player session";
            PlayerClassText.Text = $"Class: {_bot.Player.CurrentClass?.Name ?? "Unavailable"}";
            LevelText.Text = loggedIn ? _bot.Player.Level.ToString("N0") : "--";
            StateText.Text = loggedIn ? GetPlayerState(_bot.Player.State) : "OFFLINE";
            GoldText.Text = loggedIn ? _bot.Player.Gold.ToString("N0") : "--";
            CoinsText.Text = loggedIn ? _bot.Player.Coins.ToString("N0") : "--";
            GuildText.Text = loggedIn ? NullLabel(_bot.Player.Guild) : "--";
            ServerText.Text = loggedIn ? NullLabel(_bot.Player.ServerIP) : "--";
            VitalsMetric.Text = loggedIn ? $"{_bot.Player.Health:N0} / {_bot.Player.MaxHealth:N0} HP" : "-- / -- HP";
            ManaMetric.Text = loggedIn ? $"{_bot.Player.Mana:N0} / {_bot.Player.MaxMana:N0} MP" : "-- / -- MP";
            PositionMetric.Text = loggedIn ? $"X {_bot.Player.X:N0}  /  Y {_bot.Player.Y:N0}" : "X --  /  Y --";
            CellMetric.Text = loggedIn ? $"Cell {NullLabel(_bot.Player.Cell)}  /  Pad {NullLabel(_bot.Player.Pad)}" : "Cell --  /  Pad --";

            bool mapLoaded = _bot.Map.Loaded;
            string mapName = mapLoaded ? _bot.Map.FullName : "Map unavailable";
            MapMetric.Text = mapName;
            RoomMetric.Text = mapLoaded ? $"Room {_bot.Map.RoomNumber}  /  {_bot.Map.RoomID}  /  {_bot.Map.PlayerCount} players" : "Waiting for map data";
            SessionText.Text = loggedIn
                ? $"Avatar loaded: {_bot.Player.Loaded}\nTarget: {(_bot.Player.Target?.Name ?? "None")}\nCombat: {(_bot.Player.InCombat ? "Engaged" : "Idle")}"
                : "Waiting for the game client to expose a logged-in avatar.";

            if (_lastMap != mapName)
            {
                AddEvent("WORLD TRANSITION", mapName);
                _lastMap = mapName;
            }

            string playerState = loggedIn ? GetPlayerState(_bot.Player.State) : "OFFLINE";
            if (_lastPlayerState != playerState)
            {
                AddEvent("PLAYER STATE", playerState);
                _lastPlayerState = playerState;
            }

            string vitals = loggedIn ? $"{_bot.Player.Health}/{_bot.Player.MaxHealth} HP · {_bot.Player.Mana}/{_bot.Player.MaxMana} MP" : "Offline";
            if (_lastVitals != vitals && _lastVitals.Length > 0)
                AddEvent("VITALS UPDATE", vitals);
            _lastVitals = vitals;

            string target = _bot.Player.Target is Monster currentTarget ? $"{currentTarget.Name} [{currentTarget.ID}]" : "No target";
            if (_lastTarget != target)
            {
                AddEvent("TARGET CHANGE", target);
                _lastTarget = target;
            }

            Monster[] monsters = mapLoaded ? _bot.Monsters.MapMonsters.ToArray() : [];
            int aliveCount = monsters.Count(static monster => monster.Alive);
            EntityMetric.Text = monsters.Length.ToString("N0");
            EntitySubMetric.Text = $"{aliveCount} alive  /  {monsters.Length - aliveCount} inactive";
            if (_lastMonsterCount != monsters.Length)
            {
                AddEvent("ENTITY FIELD", $"{monsters.Length} monsters observed in the current map");
                _lastMonsterCount = monsters.Length;
            }

            EntityRow[] rows = monsters
                .OrderByDescending(static monster => monster.Alive)
                .ThenBy(static monster => monster.Name)
                .Take(250)
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

            bool entitiesChanged = _entities.Count != rows.Length;
            if (!entitiesChanged)
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    if (!_entities[i].HasSameDisplay(rows[i]))
                    {
                        entitiesChanged = true;
                        break;
                    }
                }
            }

            if (entitiesChanged)
            {
                string? selectedId = (EntityList.SelectedItem as EntityRow)?.Id;
                _entities.Clear();
                foreach (EntityRow row in rows)
                    _entities.Add(row);

                EntityList.SelectedItem = selectedId is null ? null : _entities.FirstOrDefault(row => row.Id == selectedId);
            }

            if (EntityList.SelectedItem is EntityRow selected)
                ShowEntityDetails(selected);
        }
        catch (Exception ex)
        {
            ConnectionText.Text = "TELEMETRY DEGRADED";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE telemetry refresh failed: {ex}");
        }
        finally
        {
            _refreshing = false;
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

    private void AddEvent(string category, string detail)
    {
        _events.Insert(0, new EventRow
        {
            Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            Category = category,
            Detail = detail
        });

        while (_events.Count > 200)
            _events.RemoveAt(_events.Count - 1);

        EventCountText.Text = $"{_events.Count:N0} EVENTS";
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
            Name == other.Name &&
            Cell == other.Cell &&
            Health == other.Health &&
            State == other.State &&
            Id == other.Id;
    }

    private sealed class EventRow
    {
        public string Time { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }
}