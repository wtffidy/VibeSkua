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

            string mapName = _bot.Map.Loaded ? _bot.Map.FullName : "Map unavailable";
            MapMetric.Text = mapName;
            RoomMetric.Text = _bot.Map.Loaded ? $"Room {_bot.Map.RoomNumber}  /  {_bot.Map.RoomID}  /  {_bot.Map.PlayerCount} players" : "Waiting for map data";
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

            List<Monster> monsters = _bot.Map.Loaded ? _bot.Monsters.MapMonsters.ToList() : [];
            EntityMetric.Text = monsters.Count.ToString("N0");
            EntitySubMetric.Text = $"{monsters.Count(m => m.Alive)} alive  /  {monsters.Count - monsters.Count(m => m.Alive)} inactive";
            if (_lastMonsterCount != monsters.Count)
            {
                AddEvent("ENTITY FIELD", $"{monsters.Count} monsters observed in the current map");
                _lastMonsterCount = monsters.Count;
            }

            _entities.Clear();
            foreach (Monster monster in monsters.OrderByDescending(m => m.Alive).ThenBy(m => m.Name).Take(250))
            {
                _entities.Add(new EntityRow
                {
                    Name = monster.Name,
                    Cell = $"CELL {NullLabel(monster.Cell)}",
                    Health = $"{monster.HP:N0}/{monster.MaxHP:N0}",
                    State = monster.Alive ? "ALIVE" : "DOWN",
                    Id = monster.ID.ToString()
                });
            }
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
    }

    private sealed class EventRow
    {
        public string Time { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }
}