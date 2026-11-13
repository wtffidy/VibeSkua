using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Auras;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Skills;
using Skua.Core.Models.Items;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;

namespace Skua.Plugin.Omniscope;

/// <summary>
/// Displays live, read-only game telemetry using the public VibeSkua scripting interfaces.
/// </summary>
public partial class OmniscopeWindow : Window
{
    private readonly IScriptInterface _bot;
    private static readonly Regex ActionScriptTokenRegex = new(
        @"(?<comment>//.*|/\*.*?(?:\*/|$))|(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')|(?<keyword>\b(?:package|import|class|interface|extends|implements|public|private|protected|internal|static|final|override|function|var|const|return|new|this|super|if|else|for|while|do|switch|case|break|continue|try|catch|throw|with|as|is|in|instanceof|typeof|void|delete|true|false|null|undefined|namespace)\b)|(?<number>\b(?:0[xX][0-9A-Fa-f]+|\d+(?:\.\d+)?)\b)|(?<function>\b[A-Za-z_$][A-Za-z0-9_$]*(?=\s*\())|(?<type>\b[A-Z][A-Za-z0-9_]*\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly SolidColorBrush CodeDefaultBrush = new(Color.Parse("#E3EBF4"));
    private static readonly SolidColorBrush CodeLineNumberBrush = new(Color.Parse("#71869F"));
    private static readonly SolidColorBrush CodeCommentBrush = new(Color.Parse("#83B99B"));
    private static readonly SolidColorBrush CodeStringBrush = new(Color.Parse("#E6C58C"));
    private static readonly SolidColorBrush CodeKeywordBrush = new(Color.Parse("#C2A7FF"));
    private static readonly SolidColorBrush CodeNumberBrush = new(Color.Parse("#F0A8B2"));
    private static readonly SolidColorBrush CodeFunctionBrush = new(Color.Parse("#83C9F4"));
    private static readonly SolidColorBrush CodeTypeBrush = new(Color.Parse("#79DCC7"));
    private const int MaximumVisibleCodeCharacters = 200_000;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly ObservableCollection<EntityRow> _entities = [];
    private EntityRow[] _entitySnapshot = [];
    private readonly ObservableCollection<string> _players = [];
    private readonly ObservableCollection<AuraRow> _auras = [];
    private readonly ObservableCollection<SkillRow> _skills = [];
    private readonly ObservableCollection<string> _quests = [];
    private readonly ObservableCollection<InventoryRow> _inventory = [];
    private readonly ObservableCollection<EventRow> _events = [];
    private readonly List<EventRow> _eventHistory = [];
    private string[] _playerSnapshot = [];
    private bool _avatarLiveFollow = true;
    private bool _entityLiveFollow = true;
    private bool _playerLiveFollow = true;
    private bool _eventLiveFollow = true;
    private readonly ObservableCollection<GameDataRecord> _dataRecords = [];
    private readonly ObservableCollection<GameDataRecord> _mapRecords = [];
    private readonly List<GameDataRecord> _swfSourceRecords = [];
    private readonly List<GameDataRecord> _mapShopRecords = [];
    private readonly object _dropHistoryLock = new();
    private readonly Queue<DropObservation> _dropHistory = new();
    private readonly Queue<MonsterKillObservation> _killHistory = new();
    private int _lastKilledMapId;
    private DateTimeOffset _lastKilledAt = DateTimeOffset.MinValue;
    private List<GameDataRecord> _dataSnapshot = [];
    private List<GameDataRecord> _mapSnapshot = [];
    private MapItem[] _mapItemSnapshot = [];
    private bool _mapAnalysisRunning;
    private bool _mapShopIndexRunning;
    private bool _mapShopLoadRunning;
    private bool _developerHelperRunning;
    private bool _swfIndexRunning;
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
    private bool _xamlInitialized;

    /// <summary>
    /// Provides the parameterless constructor required by Avalonia's runtime XAML loader.
    /// </summary>
    public OmniscopeWindow() : this(null!)
    {
    }

    /// <summary>
    /// Creates the live observatory for the active game client.
    /// </summary>
    /// <param name="bot">The scripting interface exposed by the current client.</param>
    public OmniscopeWindow(IScriptInterface bot)
    {
        InitializeComponent();
        _xamlInitialized = true;
        _bot = bot;
        if (_bot is null)
            return;

        EntityList.ItemsSource = _entities;
        PlayerList.ItemsSource = _players;
        AuraList.ItemsSource = _auras;
        SkillList.ItemsSource = _skills;
        QuestList.ItemsSource = _quests;
        InventoryList.ItemsSource = _inventory;
        EventList.ItemsSource = _events;
        DataRecordList.ItemsSource = _dataRecords;
        MapRecordList.ItemsSource = _mapRecords;

        EntityList.SelectionChanged += EntityList_SelectionChanged;
        EntityList.PointerWheelChanged += EntityList_PointerWheelChanged;
        EntityList.PointerPressed += EntityList_PointerPressed;
        PlayerList.PointerWheelChanged += PlayerList_PointerWheelChanged;
        PlayerList.PointerPressed += PlayerList_PointerPressed;
        EventList.PointerWheelChanged += EventList_PointerWheelChanged;
        EventList.PointerPressed += EventList_PointerPressed;
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_bot is null)
            return;

        try
        {
            _bot.Events.MonsterKilled += OnMonsterKilled;
            _bot.Events.ItemDropped += OnItemDropped;
            _bot.Events.QuestAccepted += OnQuestAccepted;
            _bot.Events.QuestTurnedIn += OnQuestTurnedIn;
            _bot.Events.TryBuyItem += OnTryBuyItem;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE event subscription failed: {ex}");
        }

        RefreshTelemetry();
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        try
        {
            _bot.Events.MonsterKilled -= OnMonsterKilled;
            _bot.Events.ItemDropped -= OnItemDropped;
            _bot.Events.QuestAccepted -= OnQuestAccepted;
            _bot.Events.QuestTurnedIn -= OnQuestTurnedIn;
            _bot.Events.TryBuyItem -= OnTryBuyItem;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE event unsubscription failed: {ex}");
        }
    }

    private void TelemetryRateBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_xamlInitialized || TelemetryRateBox?.SelectedItem is not ComboBoxItem selected ||
            !int.TryParse(selected.Tag?.ToString(), out int intervalMilliseconds))
            return;

        _refreshTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(intervalMilliseconds, 100, 10000));
    }

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

            int health = loggedIn ? _bot.Player.Health : 0;
            int maxHealth = loggedIn ? _bot.Player.MaxHealth : 0;
            int mana = loggedIn ? _bot.Player.Mana : 0;
            int maxMana = loggedIn ? _bot.Player.MaxMana : 0;
                VitalsMetric.Text = loggedIn ? $"{health:N0} / {maxHealth:N0} HP" : "-- / -- HP";
            SetProgress(HealthProgressBar, health, maxHealth);
            PositionMetric.Text = loggedIn ? $"X {_bot.Player.X:N0}  /  Y {_bot.Player.Y:N0}" : "X --  /  Y --";
            CellMetric.Text = loggedIn ? $"Cell {NullLabel(_bot.Player.Cell)}  /  Pad {NullLabel(_bot.Player.Pad)}" : "Cell --  /  Pad --";

            if (_avatarLiveFollow)
            {
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

                HealthText.Text = loggedIn ? $"{health:N0} / {maxHealth:N0}" : "-- / --";
                ManaText.Text = loggedIn ? $"{mana:N0} / {maxMana:N0}" : "-- / --";
                SetProgress(HealthDetailBar, health, maxHealth);
                SetProgress(ManaProgressBar, mana, maxMana);

                SessionText.Text = loggedIn
                ? $"Avatar loaded: {_bot.Player.Loaded}\nTarget: {(_bot.Player.Target?.Name ?? "None")}\nCombat: {(_bot.Player.InCombat ? "Engaged" : "Idle")}  /  AFK: {(_bot.Player.AFK ? "Yes" : "No")}"
                : "Waiting for the game client to expose a logged-in avatar.";

            }

            bool mapLoaded = _bot.Map.Loaded;
            string mapName = mapLoaded ? _bot.Map.FullName : "Map unavailable";
            MapMetric.Text = mapName;
            RoomMetric.Text = mapLoaded ? $"Room {_bot.Map.RoomNumber}  /  ID {_bot.Map.RoomID}  /  {_bot.Map.PlayerCount} players" : "Waiting for map data";
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

    private void OnMonsterKilled(int mapId)
    {
        DateTimeOffset timestamp = DateTimeOffset.Now;
        lock (_dropHistoryLock)
        {
            _lastKilledMapId = mapId;
            _lastKilledAt = timestamp;
            _killHistory.Enqueue(new MonsterKillObservation(timestamp, mapId));
            while (_killHistory.Count > 500)
                _killHistory.Dequeue();
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                AddEvent("MONSTER KILLED", $"Monster MapID {mapId}");
        });
    }

    private void OnItemDropped(ItemBase item, bool addedToInventory, int quantityNow)
    {
        DateTimeOffset timestamp = DateTimeOffset.Now;
        int itemId = item.ID;
        string itemName = NullLabel(item.Name);
        int itemQuantity = item.Quantity;
        int? precedingKillMapId = null;
        double? secondsAfterKill = null;

        lock (_dropHistoryLock)
        {
            if (_lastKilledAt != DateTimeOffset.MinValue)
            {
                TimeSpan elapsed = timestamp - _lastKilledAt;
                if (elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(12))
                {
                    precedingKillMapId = _lastKilledMapId;
                    secondsAfterKill = elapsed.TotalSeconds;
                }
            }

            _dropHistory.Enqueue(new DropObservation(
                timestamp,
                itemId,
                itemName,
                itemQuantity,
                addedToInventory,
                quantityNow,
                precedingKillMapId,
                secondsAfterKill));

            while (_dropHistory.Count > 500)
                _dropHistory.Dequeue();
        }

        string context = precedingKillMapId.HasValue
            ? $"  /  preceding kill MapID {precedingKillMapId.Value} ({secondsAfterKill:0.0}s earlier, correlation only)"
            : string.Empty;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                AddEvent("ITEM DROP", $"{itemName} [{itemId}] ×{itemQuantity}  /  Added to inventory: {addedToInventory}  /  quantity now: {quantityNow}{context}");
        });
    }

    private void OnQuestAccepted(int questId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                AddEvent("QUEST ACCEPTED", $"Quest ID {questId}");
        });
    }

    private void OnQuestTurnedIn(int questId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                AddEvent("QUEST TURNED IN", $"Quest ID {questId}");
        });
    }

    private void OnTryBuyItem(int shopId, int itemId, int shopItemId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                AddEvent("SHOP PURCHASE ATTEMPT", $"Shop {shopId}  /  Item {itemId}  /  ShopItemID {shopItemId}");
        });
    }

    private void RefreshDataCatalog_Click(object? sender, RoutedEventArgs e) => RefreshDataCatalog();

    private void DataSearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyDataFilter();

    private void DataCategoryBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyDataFilter();

    private void DataSortBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyDataFilter();

    private void MapSearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyMapFilter();

    private void MapCategoryBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyMapFilter();

    private void MapSortBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyMapFilter();

    private void MapRecordList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MapRecordList.SelectedItem is not GameDataRecord record)
        {
            LoadMapShopButton.IsEnabled = false;
            GenerateMergeShopButton.IsEnabled = false;
            GenerateMapQuestsButton.IsEnabled = false;
            return;
        }

        MapDetailTitle.Text = record.Name;
        MapDetailMeta.Text = $"{record.DisplayCategory}  /  {record.Metadata}";
        MapDetailText.Text = record.Details;
        UpdateDeveloperHelperButtons();
    }

    /// <summary>
    /// Keeps map actions disabled while a shop load or developer generator is already active.
    /// </summary>
    private void UpdateDeveloperHelperButtons()
    {
        bool ready = !_developerHelperRunning;
        bool isMapShop = MapRecordList.SelectedItem is GameDataRecord
        {
            Category: "MAP SHOP",
            SourceShopId: > 1
        } selectedShop;
        bool hasMap = MapRecordList.SelectedItem is GameDataRecord selectedRecord &&
            !string.IsNullOrWhiteSpace(selectedRecord.SourceMapName);

        LoadMapShopButton.IsEnabled = !_mapShopLoadRunning && isMapShop;
        GenerateMergeShopButton.IsEnabled = ready && isMapShop && hasMap;
        GenerateMapQuestsButton.IsEnabled = ready && hasMap;
        GenerateCurrentMapQuestsButton.IsEnabled = ready && _bot.Map.Loaded;
    }

    private void DataRecordList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataRecordList.SelectedItem is not GameDataRecord record)
            return;

        DataDetailTitle.Text = record.Name;
        DataDetailMeta.Text = $"{record.DisplayCategory}  /  {record.Metadata}";
        SetSyntaxHighlightedDetails(
            $"{record.Details}{Environment.NewLine}{Environment.NewLine}SOURCE INSPECTION:{Environment.NewLine}This is decompiled AQW client code. It describes client-side implementation and does not establish server-side validation.");
    }

    /// <summary>
    /// Renders the selected ActionScript record with a compact syntax palette and line numbers.
    /// </summary>
    /// <param name="source">The source text and metadata to display.</param>
    private void SetSyntaxHighlightedDetails(string source)
    {
        DataDetailText.Inlines.Clear();
        if (string.IsNullOrEmpty(source))
            return;

        bool truncated = source.Length > MaximumVisibleCodeCharacters;
        string displaySource = truncated ? source[..MaximumVisibleCodeCharacters] : source;
        string[] lines = displaySource.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool insideBlockComment = false;

        for (int index = 0; index < lines.Length; index++)
        {
            DataDetailText.Inlines.Add(new Run($"{index + 1,4}  ") { Foreground = CodeLineNumberBrush });
            AppendHighlightedCodeLine(lines[index], ref insideBlockComment);
            if (index < lines.Length - 1)
                DataDetailText.Inlines.Add(new LineBreak());
        }

        if (truncated)
        {
            DataDetailText.Inlines.Add(new LineBreak());
            DataDetailText.Inlines.Add(new Run("[DISPLAY TRUNCATED: SOURCE EXCEEDS 200,000 CHARACTERS]") { Foreground = CodeCommentBrush });
        }
    }

    /// <summary>
    /// Tokenizes one source line while carrying block-comment state between lines.
    /// </summary>
    /// <param name="line">The source line to tokenize.</param>
    /// <param name="insideBlockComment">Whether a block comment started on an earlier line.</param>
    private void AppendHighlightedCodeLine(string line, ref bool insideBlockComment)
    {
        int position = 0;
        while (position < line.Length)
        {
            if (insideBlockComment)
            {
                int commentEnd = line.IndexOf("*/", position, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    DataDetailText.Inlines.Add(new Run(line[position..]) { Foreground = CodeCommentBrush });
                    return;
                }

                int endPosition = commentEnd + 2;
                DataDetailText.Inlines.Add(new Run(line[position..endPosition]) { Foreground = CodeCommentBrush });
                position = endPosition;
                insideBlockComment = false;
                continue;
            }

            Match match = ActionScriptTokenRegex.Match(line, position);
            if (!match.Success)
            {
                DataDetailText.Inlines.Add(new Run(line[position..]) { Foreground = CodeDefaultBrush });
                return;
            }

            if (match.Index > position)
                DataDetailText.Inlines.Add(new Run(line[position..match.Index]) { Foreground = CodeDefaultBrush });

            SolidColorBrush brush = match.Groups["comment"].Success ? CodeCommentBrush
                : match.Groups["string"].Success ? CodeStringBrush
                : match.Groups["keyword"].Success ? CodeKeywordBrush
                : match.Groups["number"].Success ? CodeNumberBrush
                : match.Groups["function"].Success ? CodeFunctionBrush
                : match.Groups["type"].Success ? CodeTypeBrush
                : CodeDefaultBrush;
            DataDetailText.Inlines.Add(new Run(match.Value) { Foreground = brush });

            if (match.Groups["comment"].Success &&
                match.Value.StartsWith("/*", StringComparison.Ordinal) &&
                !match.Value.EndsWith("*/", StringComparison.Ordinal))
                insideBlockComment = true;

            position = match.Index + match.Length;
        }
    }

    private void RefreshDataCatalog()
    {
        _dataSnapshot = _swfSourceRecords
            .OrderBy(static record => record.Category)
            .ThenBy(static record => record.Name)
            .ToList();

        SourceFileCountText.Text = _swfSourceRecords.Count.ToString("N0");
        ClientSystemCountText.Text = _swfSourceRecords.Select(static record => record.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString("N0");
        ClientFunctionCountText.Text = _swfSourceRecords.Sum(static record => record.SourceFunctionCount).ToString("N0");

        SwfSourceStatusText.Text = _swfSourceRecords.Count > 0
            ? $"CLIENT SWF INDEXED  /  {_swfSourceRecords.Count:N0} SOURCE FILES"
            : "CLIENT SWF SOURCE NOT INDEXED";
        DataStatusText.Text = _swfSourceRecords.Count > 0
            ? $"CLIENT SOURCE READY  /  {_swfSourceRecords.Count:N0} FILES  /  {ClientFunctionCountText.Text} FUNCTIONS"
            : "CLIENT SOURCE NOT INDEXED  /  USE INDEX GAME SWF TO DECOMPILE THE CURRENT AQW CLIENT";

        ApplyDataFilter();
    }

    private void ApplyDataFilter()
    {
        if (!_xamlInitialized || DataSearchBox is null || DataCategoryBox is null || DataSortBox is null ||
            DataRecordList is null || DataResultMetric is null || DataDetailTitle is null ||
            DataDetailMeta is null || DataDetailText is null)
            return;

        string search = DataSearchBox.Text?.Trim() ?? string.Empty;
        string category = (DataCategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Client Code";
        string sort = (DataSortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Name (A-Z)";
        IEnumerable<GameDataRecord> query = _dataSnapshot;

        query = category switch
        {
            "Client Core" => query.Where(static record => record.Category == "CLIENT CORE"),
            "Network / API" => query.Where(static record => record.Category == "NETWORK / API"),
            "Game UI" => query.Where(static record => record.Category == "GAME UI"),
            "World / Map" => query.Where(static record => record.Category == "WORLD / MAP"),
            "Player / Avatar" => query.Where(static record => record.Category == "PLAYER / AVATAR"),
            "Inventory / Items" => query.Where(static record => record.Category == "INVENTORY / ITEMS"),
            "Combat / Skills" => query.Where(static record => record.Category == "COMBAT / SKILLS"),
            "Game Systems" => query.Where(static record => record.Category == "GAME SYSTEMS"),
            "Other Client Code" => query.Where(static record => record.Category == "OTHER"),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(record => record.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));

        query = sort switch
        {
            "Name (Z-A)" => query.OrderByDescending(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "Category" => query.OrderBy(static record => record.Category).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "ID (low-high)" => query.OrderBy(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "ID (high-low)" => query.OrderByDescending(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
        };

        GameDataRecord[] matches = query.Take(5000).ToArray();
        _dataRecords.Clear();
        foreach (GameDataRecord record in matches)
            _dataRecords.Add(record);

        DataResultMetric.Text = $"{matches.Length:N0} MATCHES";
        if (matches.Length == 0)
        {
            DataDetailTitle.Text = _dataSnapshot.Count == 0 ? "Client source not indexed" : "No matching source files";
            DataDetailMeta.Text = _dataSnapshot.Count == 0
                ? "Use INDEX GAME SWF to download and decompile the current AQW client."
                : "Try another client category or search term.";
            DataDetailText.Inlines.Clear();
        }
        else if (DataRecordList.SelectedItem is not GameDataRecord selected || !matches.Contains(selected))
        {
            DataRecordList.SelectedIndex = 0;
        }
    }

    private void ApplyMapFilter()
    {
        if (!_xamlInitialized || MapSearchBox is null || MapCategoryBox is null || MapSortBox is null ||
            MapRecordList is null || MapResultMetric is null || MapDetailTitle is null ||
            MapDetailMeta is null || MapDetailText is null)
            return;

        string search = MapSearchBox.Text?.Trim() ?? string.Empty;
        string category = (MapCategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All maps";
        string sort = (MapSortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Map (A-Z)";
        string currentMap = GetMapScriptName(_bot.Map.Name);
        IEnumerable<GameDataRecord> query = _mapSnapshot;

        query = category switch
        {
            "Current map" => query.Where(record => string.Equals(record.SourceMapName, currentMap, StringComparison.OrdinalIgnoreCase)),
            "Map shops" => query.Where(static record => record.Category == "MAP SHOP"),
            "Map items" => query.Where(static record => record.Category == "MAP ITEM"),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(record => record.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));

        query = sort switch
        {
            "Quest ID (low-high)" => query.OrderBy(static record => record.SourceObject is MapItem mapItem ? mapItem.QuestID : int.MaxValue).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "Map item ID (low-high)" => query.Where(static record => record.Category == "MAP ITEM").OrderBy(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "Map item ID (high-low)" => query.Where(static record => record.Category == "MAP ITEM").OrderByDescending(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "Shop ID (low-high)" => query.Where(static record => record.Category == "MAP SHOP").OrderBy(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "Shop ID (high-low)" => query.Where(static record => record.Category == "MAP SHOP").OrderByDescending(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderBy(static record => record.Metadata, StringComparer.OrdinalIgnoreCase).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
        };

        GameDataRecord[] matches = query.Take(5000).ToArray();
        _mapRecords.Clear();
        foreach (GameDataRecord record in matches)
            _mapRecords.Add(record);

        MapResultMetric.Text = $"{matches.Length:N0} MAP RECORDS";
        if (matches.Length == 0)
        {
            MapDetailTitle.Text = _mapSnapshot.Count == 0 ? "Map index not loaded" : "No matching map records";
            MapDetailMeta.Text = _mapSnapshot.Count == 0
                ? "Load the saved map index for map items, then join a map and use SCAN CURRENT MAP SHOPS."
                : "Try another map category or search term. Shop scanning indexes the currently joined map.";
            MapDetailText.Text = string.Empty;
            LoadMapShopButton.IsEnabled = false;
            GenerateMergeShopButton.IsEnabled = false;
            GenerateMapQuestsButton.IsEnabled = false;
        }
        else if (MapRecordList.SelectedItem is not GameDataRecord selected || !matches.Contains(selected))
        {
            MapRecordList.SelectedIndex = 0;
        }
    }

    private static int GetRecordId(GameDataRecord record)
    {
        if (record.SourceShopId is int shopId)
            return shopId;

        if (record.SourceQuestId is int questId)
            return questId;

        if (record.SourceObject is ShopItem shopItem)
            return shopItem.ID;

        if (record.SourceObject is MapItem mapItem)
            return mapItem.ID;

        Match match = Regex.Match($"{record.Metadata} {record.Name}", @"(?:ID\s*|#)(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out int id) ? id : int.MaxValue;
    }

    private async void AnalyzeMap_Click(object? sender, RoutedEventArgs e)
    {
        if (_mapAnalysisRunning)
            return;

        string cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VibeSkua",
            "cache",
            "0SavedMaps.json");

        _mapAnalysisRunning = true;
        AnalyzeMapButtonState(true);
        DataStatusText.Text = "LOADING SAVED MAP SWF REFERENCES";
        try
        {
            await LoadMapShopIndexCacheAsync();
            MapShopRecordCountText.Text = _mapShopRecords.Count.ToString("N0");
            if (!File.Exists(cachePath))
            {
                _mapSnapshot = _mapShopRecords.ToList();
                _mapRecords.Clear();
                MapSwfMapCountText.Text = "0";
                MapItemRecordCountText.Text = "0";
                ApplyMapFilter();
                DataStatusText.Text = _mapShopRecords.Count > 0
                    ? $"NO SAVED MAP ITEM INDEX  /  {_mapShopRecords.Count:N0} SHOP IDS RESTORED FROM PREVIOUS MAP SCANS"
                    : "NO SAVED MAP INDEX FOUND  /  LOAD MAPS IN-GAME FIRST";
                return;
            }

            string json = await File.ReadAllTextAsync(cachePath);
            Dictionary<string, List<MapItem>>? savedMaps = JsonSerializer.Deserialize<Dictionary<string, List<MapItem>>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            KeyValuePair<string, List<MapItem>>[] mapEntries = savedMaps?
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];

            List<GameDataRecord> mapRecords = new();
            foreach (KeyValuePair<string, List<MapItem>> mapEntry in mapEntries)
            {
                foreach (MapItem mapItem in mapEntry.Value)
                {
                    string questName = _bot.Quests.CachedDictionary.TryGetValue(mapItem.QuestID, out QuestData? questData) && questData is not null
                        ? questData.Name
                        : $"Quest ID {mapItem.QuestID}";
                    mapRecords.Add(CreateMapItemRecord(mapItem, questName, mapEntry.Key));
                }
            }

            _mapItemSnapshot = mapEntries.SelectMany(static pair => pair.Value).ToArray();
            _mapSnapshot = mapRecords
                .GroupBy(static record => $"{record.Metadata}|{GetRecordId(record)}")
                .Select(static group => group.First())
                .Concat(_mapShopRecords)
                .ToList();

            MapSwfMapCountText.Text = mapEntries.Length.ToString("N0");
            MapItemRecordCountText.Text = _mapSnapshot.Count(static record => record.Category == "MAP ITEM").ToString("N0");
            MapShopRecordCountText.Text = _mapShopRecords.Count.ToString("N0");
            ApplyMapFilter();
            DataStatusText.Text = $"LOADED {_mapSnapshot.Count:N0} MAP REFERENCES FROM {mapEntries.Length:N0} SAVED MAPS";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"SAVED MAP INDEX READ FAILED  /  {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE saved map index read failed: {ex}");
        }
        finally
        {
            _mapAnalysisRunning = false;
            AnalyzeMapButtonState(false);
        }
    }

    private static string MapShopIndexCachePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VibeSkua",
            "cache",
            "0SavedMapShops.json");

    private async Task LoadMapShopIndexCacheAsync()
    {
        _mapShopRecords.Clear();
        string cachePath = MapShopIndexCachePath;
        if (!File.Exists(cachePath))
            return;

        try
        {
            string json = await File.ReadAllTextAsync(cachePath);
            List<SavedMapShopIndexEntry>? entries = JsonSerializer.Deserialize<List<SavedMapShopIndexEntry>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            foreach (SavedMapShopIndexEntry entry in entries ?? [])
            {
                string mapName = GetMapScriptName(entry.MapName);
                if (string.IsNullOrWhiteSpace(mapName) || entry.ShopIds is null)
                    continue;

                foreach (int shopId in entry.ShopIds.Where(static id => id > 1).Distinct())
                    _mapShopRecords.Add(CreateMapShopRecord(shopId, mapName, entry.MapFilePath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE saved map shop index could not be read: {ex.Message}");
        }
    }

    private async Task SaveMapShopIndexCacheAsync()
    {
        string cachePath = MapShopIndexCachePath;
        try
        {
            List<SavedMapShopIndexEntry> entries = _mapShopRecords
                .Where(static record => !string.IsNullOrWhiteSpace(record.SourceMapName) && record.SourceShopId is > 1)
                .GroupBy(static record => record.SourceMapName!, StringComparer.OrdinalIgnoreCase)
                .Select(static group => new SavedMapShopIndexEntry
                {
                    MapName = group.Key,
                    MapFilePath = group.Select(static record => record.Metadata).FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path)) ?? string.Empty,
                    ShopIds = group.Select(static record => record.SourceShopId!.Value).Distinct().Order().ToArray()
                })
                .OrderBy(static entry => entry.MapName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(cachePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE saved map shop index could not be written: {ex.Message}");
            DataStatusText.Text = $"MAP SHOPS SCANNED, BUT THE LOCAL INDEX COULD NOT BE SAVED  /  {ex.Message}";
        }
    }

    private async void ScanCurrentMapShops_Click(object? sender, RoutedEventArgs e)
    {
        if (_mapShopIndexRunning)
            return;

        if (!_bot.Map.Loaded)
        {
            DataStatusText.Text = "MAP SHOP SCAN UNAVAILABLE  /  JOIN THE MAP YOU WANT TO INDEX FIRST";
            return;
        }

        string mapFilePath = _bot.Map.FilePath?.Split('?')[0] ?? string.Empty;
        string mapName = GetMapScriptName(_bot.Map.Name);
        if (string.IsNullOrWhiteSpace(mapFilePath) || string.IsNullOrWhiteSpace(mapName))
        {
            DataStatusText.Text = "MAP SHOP SCAN FAILED  /  THE CURRENT MAP DID NOT EXPOSE A VALID SWF PATH";
            return;
        }

        _mapShopIndexRunning = true;
        ScanMapShopsButton.IsEnabled = false;
        ScanMapShopsButton.Content = "FETCHING + SCANNING...";
        DataStatusText.Text = $"FETCHING THE MAP SWF FOR /{mapName}";
        try
        {
            string localMapSwfPath = await AqwMapSwfDownloader.DownloadAsync(mapFilePath);
            DataStatusText.Text = $"MAP SWF READY  /  DECOMPILING {Path.GetFileName(localMapSwfPath)}";
            string ffdecDirectory = SwfSourceIndexer.ResolveFfdecDirectory();
            SwfSourceIndexResult result = await SwfSourceIndexer.DecompileAsync(localMapSwfPath, ffdecDirectory);
            int[] shopIds = LocationSwfShopIndexer.ReadShopIds(result.ExportDirectory);
            List<GameDataRecord> mapShopRecords = shopIds
                .Select(shopId => CreateMapShopRecord(shopId, mapName, mapFilePath))
                .ToList();

            _mapShopRecords.RemoveAll(record => string.Equals(record.SourceMapName, mapName, StringComparison.OrdinalIgnoreCase));
            _mapShopRecords.AddRange(mapShopRecords);
            _mapSnapshot = _mapSnapshot
                .Where(record => record.Category != "MAP SHOP" ||
                    !string.Equals(record.SourceMapName, mapName, StringComparison.OrdinalIgnoreCase))
                .Concat(mapShopRecords)
                .ToList();

            MapShopRecordCountText.Text = _mapShopRecords.Count.ToString("N0");
            await SaveMapShopIndexCacheAsync();
            MapCategoryBox.SelectedIndex = 2;
            ApplyMapFilter();
            DataStatusText.Text = shopIds.Length > 0
                ? $"FOUND {shopIds.Length:N0} SHOP ID(S) IN /{mapName}  /  SELECT A SHOP TO LOAD OR GENERATE ITS MERGE SCRIPT"
                : $"NO SHOP IDS FOUND IN /{mapName}  /  MAP SWF SCAN COMPLETED";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"MAP SHOP SCAN FAILED  /  {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE map shop scan failed: {ex}");
        }
        finally
        {
            _mapShopIndexRunning = false;
            ScanMapShopsButton.IsEnabled = true;
            ScanMapShopsButton.Content = "SCAN CURRENT MAP SHOPS";
        }
    }

    private async void LoadMapShop_Click(object? sender, RoutedEventArgs e)
    {
        if (_mapShopLoadRunning ||
            MapRecordList.SelectedItem is not GameDataRecord { Category: "MAP SHOP", SourceShopId: int shopId } record ||
            shopId <= 1)
            return;

        _mapShopLoadRunning = true;
        LoadMapShopButton.IsEnabled = false;
        LoadMapShopButton.Content = "WAITING FOR AQW...";
        DataStatusText.Text = $"REQUESTING SHOP {shopId}  /  WAITING FOR THE GAME CLIENT";
        try
        {
            // ScriptShop.Load waits synchronously for the server response (up to 20 seconds).
            // Run it off the Avalonia UI thread, then copy the results into plain strings before returning.
            ShopLoadResult result = await Task.Run(() =>
            {
                _bot.Shops.Load(shopId);
                if (!_bot.Shops.IsLoaded || _bot.Shops.ID != shopId)
                    return new ShopLoadResult(false, shopId, string.Empty, []);

                string shopName = _bot.Shops.Name;
                string[] itemLines = _bot.Shops.Items
                    .Select(static item => $"{item.Name} [{item.ID}]  /  ShopItemID {item.ShopItemID}")
                    .ToArray();

                return new ShopLoadResult(true, shopId, shopName, itemLines);
            });

            if (!result.Loaded)
            {
                DataStatusText.Text = $"SHOP {shopId} DID NOT LOAD  /  AQW DID NOT CONFIRM THE REQUEST";
                return;
            }

            string items = result.ItemLines.Length == 0
                ? "The shop loaded, but no items were exposed by the client."
                : string.Join(Environment.NewLine, result.ItemLines);
            MapDetailText.Text = $"{record.Details}{Environment.NewLine}{Environment.NewLine}" +
                $"LOADED SHOP: {result.ShopName} [{result.ShopId}]{Environment.NewLine}" +
                $"ITEMS: {result.ItemLines.Length:N0}{Environment.NewLine}{items}{Environment.NewLine}{Environment.NewLine}" +
                "No purchase request was sent.";
            DataStatusText.Text = $"SHOP LOADED  /  {result.ShopName} [{result.ShopId}]  /  NO PURCHASE MADE";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"SHOP LOAD FAILED  /  {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE map shop load failed: {ex}");
        }
        finally
        {
            _mapShopLoadRunning = false;
            LoadMapShopButton.Content = "LOAD SHOP";
            UpdateDeveloperHelperButtons();
        }
    }

    private async void GenerateMergeShop_Click(object? sender, RoutedEventArgs e)
    {
        if (MapRecordList.SelectedItem is not GameDataRecord { Category: "MAP SHOP", SourceShopId: int shopId, SourceMapName: string mapName } ||
            shopId <= 1 || string.IsNullOrWhiteSpace(mapName))
            return;

        await RunDeveloperHelperAsync(
            "MergeTemplateHelperV3.cs",
            ("regenerateExisting", false),
            ("location", mapName),
            ("shopID", shopId),
            ("ignoreCaseStorage", false));
    }

    private async void GenerateCurrentMapQuests_Click(object? sender, RoutedEventArgs e)
    {
        string mapName = GetMapScriptName(_bot.Map.Name);
        if (!_bot.Map.Loaded || string.IsNullOrWhiteSpace(mapName))
        {
            DataStatusText.Text = "JOIN THE MAP YOU WANT TO GENERATE BEFORE STARTING THE WEEKLY RELEASE GENERATOR";
            return;
        }

        await RunDeveloperHelperAsync(
            "WeeklyReleaseGeneratorV3.cs",
            ("UseCurrentMap", true),
            ("MapName", mapName));
    }

    private async void GenerateMapQuests_Click(object? sender, RoutedEventArgs e)
    {
        if (MapRecordList.SelectedItem is not GameDataRecord record || string.IsNullOrWhiteSpace(record.SourceMapName))
            return;

        await RunDeveloperHelperAsync(
            "WeeklyReleaseGeneratorV3.cs",
            ("UseCurrentMap", false),
            ("MapName", record.SourceMapName));
    }

    private async Task RunDeveloperHelperAsync(string fileName, params (string Name, object Value)[] optionValues)
    {
        if (_developerHelperRunning)
            return;

        if (_bot.Manager is not IScriptManager manager)
        {
            DataStatusText.Text = "SCRIPT GENERATION UNAVAILABLE  /  SCRIPT MANAGER INTERFACE NOT FOUND";
            return;
        }

        if (manager.ScriptRunning)
        {
            DataStatusText.Text = "STOP THE CURRENT SCRIPT BEFORE STARTING A DEVELOPER GENERATOR";
            return;
        }

        string scriptPath = Path.Combine(ClientFileSources.SkuaScriptsDIR, "Tools", "ForDevelopers", fileName);
        if (!File.Exists(scriptPath))
        {
            DataStatusText.Text = $"GENERATOR SCRIPT NOT FOUND  /  {scriptPath}  /  UPDATE YOUR SCRIPTS REPOSITORY";
            return;
        }

        _developerHelperRunning = true;
        UpdateDeveloperHelperButtons();
        DataStatusText.Text = $"PREPARING {Path.GetFileNameWithoutExtension(fileName)}  /  COMPILING + APPLYING MAP OPTIONS";
        try
        {
            // Compilation, reflected option discovery, config reads, and config writes can all touch disk.
            // Keep the complete preparation phase away from the UI thread.
            manager.SetLoadedScript(scriptPath);
            await Task.Run(() =>
            {
                string source = File.ReadAllText(scriptPath);
                object? scriptInstance = manager.Compile(source);
                if (scriptInstance is null)
                    throw new InvalidOperationException($"Skua could not compile {fileName}. Check the script compiler output.");

                manager.LoadScriptConfig(scriptInstance);
                IScriptOptionContainer? config = manager.Config;
                if (config is null)
                    throw new InvalidOperationException($"{fileName} did not expose a script option container.");

                foreach ((string name, object value) in optionValues)
                    config.Set(name, value);
                config.Save();
            });

            DataStatusText.Text = $"STARTING {Path.GetFileNameWithoutExtension(fileName)}  /  HANDING OFF TO SCRIPT MANAGER";
            // StartScript performs synchronous setup after its awaits. Run the whole start flow off-thread too;
            // the actual script still executes on Skua's dedicated Script Thread.
            Exception? startFailure = await Task.Run(async () =>
                await manager.StartScript().ConfigureAwait(false));
            if (startFailure is not null)
                throw startFailure;

            DataStatusText.Text = $"STARTED {Path.GetFileNameWithoutExtension(fileName)}  /  MAP OPTIONS PREFILLED";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"SCRIPT GENERATION FAILED  /  {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE developer script launch failed: {ex}");
        }
        finally
        {
            _developerHelperRunning = false;
            UpdateDeveloperHelperButtons();
        }
    }

    private void AnalyzeMapButtonState(bool running)
    {
        if (AnalyzeMapButton is not null)
        {
            AnalyzeMapButton.IsEnabled = !running;
            AnalyzeMapButton.Content = running ? "READING MAP INDEX..." : "LOAD SAVED MAP INDEX";
        }
    }

    private async void DecompileSwf_Click(object? sender, RoutedEventArgs e)
    {
        if (_swfIndexRunning)
            return;

        _swfIndexRunning = true;
        DecompileSwfButtonState(true);
        DataStatusText.Text = "RESOLVING LATEST AQW GAME CLIENT  /  MANIFEST + CDN FALLBACK";
        try
        {
            string ffdecDirectory = SwfSourceIndexer.ResolveFfdecDirectory();
            AqwGameClientFile clientFile = await AqwGameClientResolver.DownloadLatestAsync();
            DataStatusText.Text = $"AQW CLIENT {clientFile.FileName} DOWNLOADED  /  STARTING FFDEC";
            SwfSourceIndexResult result = await SwfSourceIndexer.DecompileAsync(clientFile.LocalPath, ffdecDirectory);
            _swfSourceRecords.Clear();
            _swfSourceRecords.Add(new GameDataRecord
            {
                Category = "CLIENT CORE",
                Name = $"AQW game client / {clientFile.FileName}",
                Summary = $"{clientFile.ResolutionMethod}  /  {result.Records.Count:N0} source files indexed",
                Metadata = clientFile.SourceUri.ToString(),
                Details = $"RESOLUTION: {clientFile.ResolutionMethod}{Environment.NewLine}" +
                    $"SOURCE FILE: {clientFile.FileName}{Environment.NewLine}" +
                    $"LOCAL COPY: {clientFile.LocalPath}{Environment.NewLine}" +
                    $"DOWNLOAD SOURCE: {clientFile.SourceUri}{Environment.NewLine}" +
                    $"EXPORTED SOURCE: {result.ExportDirectory}{Environment.NewLine}" +
                    $"INDEXED ACTIONSCRIPT FILES: {result.Records.Count:N0}{Environment.NewLine}{Environment.NewLine}" +
                    "The SWF is the latest AQW game client resolved from the game-version manifest or CDN version-probing fallback. Client-side logic is not proof of server-side rules.",
                SearchText = $"AQW game client {clientFile.FileName} {clientFile.SourceUri} {clientFile.LocalPath}"
            });
            _swfSourceRecords.AddRange(result.Records);
            RefreshDataCatalog();
            SwfSourceStatusText.Text = $"AQW CLIENT INDEXED  /  {clientFile.FileName}  /  {_swfSourceRecords.Count:N0} RECORDS";
            DataStatusText.Text = $"AQW GAME CLIENT DECOMPILED  /  {result.Records.Count:N0} ACTIONSCRIPT FILES INDEXED";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"AQW CLIENT DECOMPILATION FAILED  /  {ex.Message}";
            SwfSourceStatusText.Text = "AQW CLIENT SOURCE INDEX FAILED";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE AQW client SWF indexing failed: {ex}");
        }
        finally
        {
            _swfIndexRunning = false;
            DecompileSwfButtonState(false);
        }
    }

    private void DecompileSwfButtonState(bool running)
    {
        if (DecompileSwfButton is not null)
        {
            DecompileSwfButton.IsEnabled = !running;
            DecompileSwfButton.Content = running ? "DOWNLOADING / INDEXING..." : "INDEX GAME SWF";
        }
    }

    private async void ExportData_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string exportDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeSkua",
                "Omniscope",
                "Exports");
            Directory.CreateDirectory(exportDirectory);
            string exportPath = Path.Combine(exportDirectory, $"omniscope-data-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            object export = new
            {
                ExportedAt = DateTimeOffset.Now,
                GameData = _dataSnapshot,
                MapData = _mapSnapshot
            };
            string json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(exportPath, json);
            DataStatusText.Text = $"EXPORTED {_dataSnapshot.Count:N0} GAME RECORDS + {_mapSnapshot.Count:N0} MAP REFERENCES  /  {exportPath}";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"CATALOG EXPORT FAILED  /  {ex.Message}";
        }
    }

    private static GameDataRecord CreateQuestRecord(QuestData quest)
    {
        ItemBase[] acceptItems = quest.AcceptRequirements?.ToArray() ?? [];
        ItemBase[] turnInItems = quest.Requirements?.ToArray() ?? [];
        ItemBase[] rewardsItems = quest.Rewards?.ToArray() ?? [];
        SimpleReward[] simpleRewards = quest.SimpleRewards?.ToArray() ?? [];
        string acceptRequirements = FormatItems(acceptItems);
        string turnInRequirements = FormatItems(turnInItems);
        string rewards = FormatItems(rewardsItems);
        string rewardRates = simpleRewards.Length == 0
            ? "No simple reward-rate entries."
            : string.Join(Environment.NewLine, simpleRewards.Select(static item => $"{NullLabel(item.Name)} [ID {item.ID}] ×{item.Quantity} / {item.Rate:0.###}% / type {item.Type}"));
        string details =
            $"QUEST: {quest.Name}{Environment.NewLine}" +
            $"QUEST ID: {quest.ID}{Environment.NewLine}" +
            $"LEVEL GATE: {quest.Level}{Environment.NewLine}" +
            $"MEMBERSHIP: {(quest.Upgrade ? "Member" : "Free")}{Environment.NewLine}" +
            $"CLASS GATE: {quest.RequiredClassID} / {quest.RequiredClassPoints:N0} class points{Environment.NewLine}" +
            $"FACTION GATE: {quest.RequiredFactionId} / {quest.RequiredFactionRep:N0} reputation{Environment.NewLine}" +
            $"REPEATABLE: {!quest.Once}{Environment.NewLine}" +
            $"GOLD REWARD: {quest.Gold:N0}{Environment.NewLine}" +
            $"XP REWARD: {quest.XP:N0}{Environment.NewLine}{Environment.NewLine}" +
            $"ACCEPT REQUIREMENTS:{Environment.NewLine}{acceptRequirements}{Environment.NewLine}{Environment.NewLine}" +
            $"TURN-IN REQUIREMENTS:{Environment.NewLine}{turnInRequirements}{Environment.NewLine}{Environment.NewLine}" +
            $"REWARDS:{Environment.NewLine}{rewards}{Environment.NewLine}{Environment.NewLine}" +
            $"SIMPLE REWARD RATES:{Environment.NewLine}{rewardRates}";

        return new GameDataRecord
        {
            Category = "QUEST",
            Name = NullLabel(quest.Name),
            Summary = $"Level {quest.Level}  /  {acceptItems.Length:N0} accept items  /  {turnInItems.Length:N0} turn-in items  /  {rewardsItems.Length:N0} rewards",
            Metadata = $"QuestData.json  /  ID {quest.ID}",
            Details = details,
            SearchText = $"{details} {acceptRequirements} {turnInRequirements} {rewards} {rewardRates}",
            SourceQuestId = quest.ID
        };
    }

    private static GameDataRecord CreateShopRecord(int shopId, string shopName, ShopItem item)
    {
        ItemBase[] requirementItems = item.Requirements?.ToArray() ?? [];
        string requirements = FormatItems(requirementItems);
        string currency = item.Coins ? "Adventure Coins" : "Gold";
        string token = item.TokenID > 0 || !string.IsNullOrWhiteSpace(item.TokenName)
            ? $"{item.TokenName} [ID {item.TokenID}] ×{item.TokenQuantity:0.###}"
            : "None";
        string details =
            $"SHOP: {shopName} [{shopId}]{Environment.NewLine}" +
            $"ITEM: {item.Name}{Environment.NewLine}" +
            $"ITEM ID: {item.ID}  /  SHOP ITEM ID: {item.ShopItemID}{Environment.NewLine}" +
            $"COST: {item.Cost:N0} {currency}{Environment.NewLine}" +
            $"REQUIRED LEVEL: {item.Level}{Environment.NewLine}" +
            $"FACTION: {NullLabel(item.Faction)}{Environment.NewLine}" +
            $"REQUIRED REPUTATION: {item.RequiredReputation:N0}{Environment.NewLine}" +
            $"TOKEN: {token}{Environment.NewLine}" +
            $"MERGE / TURN-IN MATERIALS:{Environment.NewLine}{requirements}";

        return new GameDataRecord
        {
            Category = "SHOP",
            Name = NullLabel(item.Name),
            Summary = $"{item.Cost:N0} {currency}  /  {NullLabel(item.Faction)}  /  {requirementItems.Length:N0} materials",
            Metadata = $"Shop {shopName} [{shopId}]  /  ShopItemID {item.ShopItemID}",
            Details = details,
            SearchText = $"{details} {requirements} {item.TokenName}",
            SourceShopId = shopId,
            SourceObject = item
        };
    }

    private static GameDataRecord CreateMapShopRecord(int shopId, string mapName, string mapFilePath)
    {
        string details =
            $"MAP: /{mapName}{Environment.NewLine}" +
            $"MAP SWF: {mapFilePath}{Environment.NewLine}" +
            $"SHOP ID: {shopId}{Environment.NewLine}{Environment.NewLine}" +
            "This shop ID was extracted from ActionScript in the map SWF. LOAD SHOP opens it without buying anything. " +
            "GENERATE MERGE SHOP runs MergeTemplateHelperV3 with this map and shop ID prefilled.";
        return new GameDataRecord
        {
            Category = "MAP SHOP",
            Name = $"Shop {shopId}",
            Summary = $"Shop ID {shopId}  /  /{mapName}",
            Metadata = mapFilePath,
            Details = details,
            SearchText = $"{details} {shopId} {mapName} {mapFilePath}",
            SourceShopId = shopId,
            SourceMapName = mapName
        };
    }

    private static string GetMapScriptName(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return string.Empty;

        string value = mapName.Trim().Replace('\\', '/');
        int mapsIndex = value.LastIndexOf("maps/", StringComparison.OrdinalIgnoreCase);
        if (mapsIndex >= 0)
            value = value[(mapsIndex + 5)..];

        string map = Path.GetFileNameWithoutExtension(value).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(map) ? value.ToLowerInvariant() : map;
    }

    private static GameDataRecord CreateMapItemRecord(MapItem mapItem, string questName, string mapKey)
    {
        string mapName = string.IsNullOrWhiteSpace(mapItem.MapName) ? mapKey : mapItem.MapName;
        string mapFilePath = string.IsNullOrWhiteSpace(mapItem.MapFilePath) ? mapKey : mapItem.MapFilePath;
        string details =
            $"MAP ITEM ID: {mapItem.ID}{Environment.NewLine}" +
            $"QUEST ID: {mapItem.QuestID}{Environment.NewLine}" +
            $"QUEST NAME: {questName}{Environment.NewLine}" +
            $"MAP: {mapName}{Environment.NewLine}" +
            $"MAP FILE: {mapFilePath}{Environment.NewLine}" +
            $"{Environment.NewLine}This association was extracted from VibeSkua's saved map-SWF index.";
        return new GameDataRecord
        {
            Category = "MAP ITEM",
            Name = $"Map link #{mapItem.ID}",
            Summary = $"Quest {mapItem.QuestID}  /  {mapName}",
            Metadata = mapFilePath,
            Details = details,
            SearchText = $"{details} {mapItem.ID} {mapItem.QuestID} {questName} {mapName} {mapKey}",
            SourceMapName = GetMapScriptName(mapName),
            SourceObject = mapItem
        };
    }

    private static GameDataRecord CreateDropRecord(ItemBase item)
    {
        string details =
            $"CURRENT DROP: {NullLabel(item.Name)}{Environment.NewLine}" +
            $"ITEM ID: {item.ID}{Environment.NewLine}" +
            $"QUANTITY: {item.Quantity:N0}{Environment.NewLine}" +
            $"CATEGORY: {item.CategoryString}{Environment.NewLine}" +
            $"TEMPORARY: {item.Temp}{Environment.NewLine}" +
            $"AC ITEM: {item.Coins}{Environment.NewLine}" +
            $"{Environment.NewLine}This is an observed runtime drop queue entry, not a complete monster drop table.";
        return new GameDataRecord
        {
            Category = "DROP",
            Name = NullLabel(item.Name),
            Summary = $"Observed quantity ×{item.Quantity:N0}  /  {item.CategoryString}",
            Metadata = "Runtime drop queue",
            Details = details,
            SearchText = details
        };
    }

    private static GameDataRecord CreateObservedDropRecord(DropObservation observation)
    {
        string killContext = observation.PrecedingMonsterMapId.HasValue
            ? $"Preceding kill MapID: {observation.PrecedingMonsterMapId.Value} / {observation.SecondsAfterKill:0.0}s earlier (temporal correlation only)"
            : "No recent monster-kill event was observed within the 12-second context window.";
        string details =
            $"OBSERVED DROP: {observation.Name}{Environment.NewLine}" +
            $"ITEM ID: {observation.ItemId}{Environment.NewLine}" +
            $"ITEM QUANTITY: {observation.ItemQuantity:N0}{Environment.NewLine}" +
            $"ADDED TO INVENTORY: {observation.AddedToInventory}{Environment.NewLine}" +
            $"INVENTORY QUANTITY AFTER EVENT: {observation.QuantityNow:N0}{Environment.NewLine}" +
            $"TIMESTAMP: {observation.Timestamp:O}{Environment.NewLine}" +
            $"{killContext}{Environment.NewLine}{Environment.NewLine}" +
            "A nearby kill is only temporal context. The game event does not identify the exact monster responsible for this item.";
        return new GameDataRecord
        {
            Category = "DROP",
            Name = NullLabel(observation.Name),
            Summary = $"{observation.Timestamp:HH:mm:ss}  /  ×{observation.ItemQuantity:N0}  /  Added: {observation.AddedToInventory}",
            Metadata = "Runtime ItemDropped event history",
            Details = details,
            SearchText = details
        };
    }

    private static GameDataRecord CreateMonsterRecord(Monster monster)
    {
        string details =
            $"MONSTER: {monster.Name}{Environment.NewLine}" +
            $"ENTITY ID: {monster.ID}{Environment.NewLine}" +
            $"MAP ID: {monster.MapID}{Environment.NewLine}" +
            $"RACE: {NullLabel(monster.Race)}{Environment.NewLine}" +
            $"CELL: {NullLabel(monster.Cell)}{Environment.NewLine}" +
            $"STATE: {monster.State}  /  ALIVE: {monster.Alive}{Environment.NewLine}" +
            $"HEALTH: {monster.HP:N0} / {monster.MaxHP:N0}{Environment.NewLine}" +
            $"CLIENT ASSET: {NullLabel(monster.FileName)}{Environment.NewLine}{Environment.NewLine}" +
            "A complete mob loot table is not supplied by the public monster snapshot.";
        return new GameDataRecord
        {
            Category = "MONSTER",
            Name = NullLabel(monster.Name),
            Summary = $"Map ID {monster.MapID}  /  {NullLabel(monster.Cell)}  /  {(monster.Alive ? "ALIVE" : "DOWN")}",
            Metadata = "Current map monster snapshot",
            Details = details,
            SearchText = details
        };
    }

    private static GameDataRecord CreateStatusRecord(string name, string message, string category)
    {
        return new GameDataRecord
        {
            Category = category,
            Name = name,
            Summary = message,
            Metadata = "Source status",
            Details = message,
            SearchText = $"{name} {message}"
        };
    }

    private static string FormatItems(IEnumerable<ItemBase>? items)
    {
        if (items is null)
            return "None";

        string[] lines = items
            .Where(static item => item is not null)
            .Select(static item => $"{NullLabel(item.Name)} [ID {item.ID}] ×{item.Quantity:N0}")
            .ToArray();
        return lines.Length == 0 ? "None" : string.Join(Environment.NewLine, lines);
    }

    private static void AddItemReferences(Dictionary<int, List<string>> references, IEnumerable<ItemBase>? items, string source)
    {
        if (items is null)
            return;

        foreach (ItemBase item in items)
        {
            if (item is not null)
                AddItemReference(references, item, source);
        }
    }

    private static void AddItemReference(Dictionary<int, List<string>> references, ItemBase item, string source)
    {
        if (item.ID <= 0)
            return;

        if (!references.TryGetValue(item.ID, out List<string>? entries) || entries is null)
        {
            entries = new List<string>();
            references[item.ID] = entries;
        }

        entries.Add($"{NullLabel(item.Name)} / {source}");
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

    private void AvatarFollowButton_Click(object? sender, RoutedEventArgs e) => SetAvatarLiveFollow(!_avatarLiveFollow);
    private void EntityFollowButton_Click(object? sender, RoutedEventArgs e) => SetEntityLiveFollow(!_entityLiveFollow);
    private void PlayerFollowButton_Click(object? sender, RoutedEventArgs e) => SetPlayerLiveFollow(!_playerLiveFollow);
    private void EventFollowButton_Click(object? sender, RoutedEventArgs e) => SetEventLiveFollow(!_eventLiveFollow);

    private void EntityList_PointerWheelChanged(object? sender, PointerWheelEventArgs e) => SetEntityLiveFollow(false);
    private void EntityList_PointerPressed(object? sender, PointerPressedEventArgs e) => SetEntityLiveFollow(false);
    private void PlayerList_PointerWheelChanged(object? sender, PointerWheelEventArgs e) => SetPlayerLiveFollow(false);
    private void PlayerList_PointerPressed(object? sender, PointerPressedEventArgs e) => SetPlayerLiveFollow(false);
    private void EventList_PointerWheelChanged(object? sender, PointerWheelEventArgs e) => SetEventLiveFollow(false);
    private void EventList_PointerPressed(object? sender, PointerPressedEventArgs e) => SetEventLiveFollow(false);

    private void SetAvatarLiveFollow(bool enabled)
    {
        _avatarLiveFollow = enabled;
        AvatarFollowButton.Content = enabled ? "PAUSE" : "RESUME";
        AvatarFollowStatus.Text = enabled ? "LIVE" : "PAUSED";
        AvatarFollowStatus.Foreground = enabled ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Goldenrod;
        if (enabled)
            RefreshTelemetry();
    }

    private void SetEntityLiveFollow(bool enabled)
    {
        _entityLiveFollow = enabled;
        EntityFollowButton.Content = enabled ? "PAUSE" : "RESUME";
        if (enabled)
            ApplyEntityFilter();
    }

    private void SetPlayerLiveFollow(bool enabled)
    {
        _playerLiveFollow = enabled;
        PlayerFollowButton.Content = enabled ? "PAUSE" : "RESUME";
        if (!enabled)
            return;

        _players.Clear();
        foreach (string name in _playerSnapshot)
            _players.Add(name);
    }

    private void SetEventLiveFollow(bool enabled)
    {
        _eventLiveFollow = enabled;
        EventFollowButton.Content = enabled ? "PAUSE" : "RESUME";
        if (enabled)
        {
            _events.Clear();
            foreach (EventRow eventRow in _eventHistory)
                _events.Add(eventRow);
        }

        EventCountText.Text = $"{_eventHistory.Count:N0} EVENTS{(enabled ? string.Empty : "  /  PAUSED")}";
    }

    private void ApplyEntityFilter()
    {
        string filter = EntitySearchBox?.Text?.Trim() ?? string.Empty;
        EntityRow[] rows = string.IsNullOrWhiteSpace(filter)
            ? _entitySnapshot
            : _entitySnapshot.Where(row =>
                row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Cell.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (!_entityLiveFollow)
            return;

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
        _playerSnapshot = names;
        RosterMetric.Text = $"Players in instance: {names.Length:N0}";
        if (_lastPlayerCount != names.Length)
        {
            if (_lastPlayerCount >= 0)
                AddEvent("WORLD ROSTER", $"{names.Length} players in the current instance");
            _lastPlayerCount = names.Length;
        }

        if (!_playerLiveFollow || _players.SequenceEqual(names))
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
        EventRow eventRow = new()
        {
            Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            Category = category,
            Detail = detail
        };

        _eventHistory.Insert(0, eventRow);
        while (_eventHistory.Count > 250)
            _eventHistory.RemoveAt(_eventHistory.Count - 1);

        if (_eventLiveFollow)
        {
            _events.Insert(0, eventRow);
            while (_events.Count > 250)
                _events.RemoveAt(_events.Count - 1);
        }

        EventCountText.Text = $"{_eventHistory.Count:N0} EVENTS{(_eventLiveFollow ? string.Empty : "  /  PAUSED")}";
    }

    private static void SetProgress(Border bar, double current, double maximum)
    {
        double ratio = maximum <= 0 ? 0 : Math.Clamp(current / maximum, 0, 1);
        if (bar.Parent is Border track)
            bar.Width = Math.Max(0, track.Bounds.Width * ratio);
    }

    private static string GetRecordActionHint(GameDataRecord record) => record.Category switch
    {
        "QUEST" => "This is a saved quest definition. Use CHECK QUEST to inspect the live character state, ACCEPT QUEST when available, or TURN IN QUEST when all requirements are met.",
        "SHOP" => "This item belongs to the shop shown above. LOAD SHOP opens that shop in the game. CHECK REQUIREMENTS inspects the live client-side purchase gates. Loading a shop does not buy the item.",
        "MAP ITEM" => "This is a map-item to quest association read from saved map data. It is a reference, not an action button. Use the quest ID to find its quest record.",
        "ITEM REFERENCE" => "This item ID is referenced by quests, rewards, shops, or runtime events. Select the related quest or shop result to use its available actions.",
        "DROP" => "This is a drop observation or drop-related record, not a guaranteed loot table. It does not identify a verified source unless the details explicitly say so.",
        "MONSTER" => "This is a monster snapshot or kill observation. It describes data Skua has seen, not a complete server-side monster database.",
        "ACHIEVEMENT" => "This is achievement-related client or cache information. Complete server-side achievement requirements are not exposed here.",
        _ when record.Category.EndsWith("SOURCE", StringComparison.Ordinal) || record.Category == "SWF SOURCE" => "This is decompiled AQW client source indexed for searching. It is reference material for investigation, not a directly executable game action.",
        _ => "This is reference data. Its details show what OMNISCOPE found and where the information came from; no direct game action is available for this record."
    };

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

    private sealed record DropObservation(
        DateTimeOffset Timestamp,
        int ItemId,
        string Name,
        int ItemQuantity,
        bool AddedToInventory,
        int QuantityNow,
        int? PrecedingMonsterMapId,
        double? SecondsAfterKill);

    private sealed record MonsterKillObservation(DateTimeOffset Timestamp, int MapId);

    private sealed class SavedMapShopIndexEntry
    {
        public SavedMapShopIndexEntry() { }
        public string MapName { get; set; } = string.Empty;
        public string MapFilePath { get; set; } = string.Empty;
        public int[] ShopIds { get; set; } = [];
    }

    private sealed record ShopLoadResult(bool Loaded, int ShopId, string ShopName, string[] ItemLines);

    private sealed class EventRow
    {
        public string Time { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }
}