using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Skua.Core.Interfaces;
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
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly ObservableCollection<EntityRow> _entities = [];
    private EntityRow[] _entitySnapshot = [];
    private readonly ObservableCollection<string> _players = [];
    private readonly ObservableCollection<AuraRow> _auras = [];
    private readonly ObservableCollection<SkillRow> _skills = [];
    private readonly ObservableCollection<string> _quests = [];
    private readonly ObservableCollection<InventoryRow> _inventory = [];
    private readonly ObservableCollection<EventRow> _events = [];
    private readonly ObservableCollection<GameDataRecord> _dataRecords = [];
    private readonly List<GameDataRecord> _swfSourceRecords = [];
    private List<GameDataRecord> _dataSnapshot = [];
    private MapItem[] _mapItemSnapshot = [];
    private bool _mapAnalysisRunning;
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
        DataRecordList.ItemsSource = _dataRecords;

        EntityList.SelectionChanged += EntityList_SelectionChanged;
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Skua.Shared.Avalonia.Services.WindowPlacementHelper.CenterOnActiveScreen(this);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshTelemetry();
        RefreshDataCatalog();
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

    private void RefreshDataCatalog_Click(object? sender, RoutedEventArgs e) => RefreshDataCatalog();

    private void DataSearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyDataFilter();

    private void DataCategoryBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyDataFilter();

    private void DataRecordList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataRecordList.SelectedItem is not GameDataRecord record)
            return;

        DataDetailTitle.Text = record.Name;
        DataDetailMeta.Text = $"{record.Category}  /  {record.Metadata}";
        DataDetailText.Text = record.Details;
        EvaluateShopButton.IsEnabled = record.SourceObject is ShopItem shopItem &&
            record.SourceShopId == _bot.Shops.ID &&
            _bot.Shops.IsLoaded &&
            _bot.Shops.Items.Any(item => item.ID == shopItem.ID && item.ShopItemID == shopItem.ShopItemID);
    }

    private void EvaluateShopGates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataRecordList.SelectedItem is not GameDataRecord record ||
            record.SourceObject is not ShopItem item ||
            record.SourceShopId != _bot.Shops.ID ||
            !_bot.Shops.IsLoaded)
        {
            DataStatusText.Text = "SHOP REQUIREMENT CHECK UNAVAILABLE  /  LOAD THE RELEVANT SHOP FIRST";
            return;
        }

        try
        {
            List<string> unmetRequirements = _bot.Shops.GetUnmetPurchaseRequirements(item);
            string evaluation = unmetRequirements.Count == 0
                ? "No unmet quest, inventory material or token requirements were reported by the client-side checker."
                : string.Join(Environment.NewLine, unmetRequirements.Select(static requirement => $"• {requirement}"));
            DataDetailText.Text = $"{record.Details}{Environment.NewLine}{Environment.NewLine}" +
                $"LIVE CLIENT REQUIREMENT CHECK:{Environment.NewLine}{evaluation}{Environment.NewLine}{Environment.NewLine}" +
                "Note: this checker evaluates the client-visible quest/material/token gates. Server-side validation and any separate level, faction or membership checks remain authoritative.";
            DataStatusText.Text = $"SHOP GATES INSPECTED  /  {item.Name} [{item.ID}]  /  NO PURCHASE REQUEST SENT";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"SHOP REQUIREMENT CHECK FAILED  /  {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE shop requirement check failed: {ex}");
        }
    }

    private void RefreshDataCatalog()
    {
        List<GameDataRecord> records = new();
        Dictionary<int, List<string>> itemReferences = new();

        try
        {
            _bot.Quests.LoadCachedQuests();
            QuestData[] cachedQuests = _bot.Quests.Cached.ToArray();
            foreach (QuestData quest in cachedQuests)
            {
                records.Add(CreateQuestRecord(quest));
                AddItemReferences(itemReferences, quest.AcceptRequirements, $"Quest {quest.Name} [{quest.ID}] / accept");
                AddItemReferences(itemReferences, quest.Requirements, $"Quest {quest.Name} [{quest.ID}] / turn-in");
                AddItemReferences(itemReferences, quest.Rewards, $"Quest {quest.Name} [{quest.ID}] / reward");
                AddItemReferences(itemReferences, quest.SimpleRewards, $"Quest {quest.Name} [{quest.ID}] / reward roll");
            }

            QuestRecordCountText.Text = cachedQuests.Length.ToString("N0");
        }
        catch (Exception ex)
        {
            QuestRecordCountText.Text = "0";
            records.Add(CreateStatusRecord("Quest database unavailable", ex.Message, "QUESTS"));
        }

        try
        {
            HashSet<int> loadedShopIds = new();
            foreach (ShopInfo shop in _bot.Shops.LoadedCache.ToArray())
            {
                if (!loadedShopIds.Add(shop.ID))
                    continue;

                foreach (ShopItem item in shop.Items.ToArray())
                {
                    records.Add(CreateShopRecord(shop.ID, shop.Name, item));
                    AddItemReferences(itemReferences, item.Requirements, $"Shop {shop.Name} [{shop.ID}] / {item.Name}");
                    AddItemReference(itemReferences, item, $"Shop {shop.Name} [{shop.ID}] / listed item");
                }
            }

            if (_bot.Shops.IsLoaded && loadedShopIds.Add(_bot.Shops.ID))
            {
                foreach (ShopItem item in _bot.Shops.Items.ToArray())
                {
                    records.Add(CreateShopRecord(_bot.Shops.ID, _bot.Shops.Name, item));
                    AddItemReferences(itemReferences, item.Requirements, $"Shop {_bot.Shops.Name} [{_bot.Shops.ID}] / {item.Name}");
                    AddItemReference(itemReferences, item, $"Shop {_bot.Shops.Name} [{_bot.Shops.ID}] / listed item");
                }
            }

            ShopRecordCountText.Text = records.Count(static record => record.Category == "SHOP").ToString("N0");
        }
        catch (Exception ex)
        {
            ShopRecordCountText.Text = "0";
            records.Add(CreateStatusRecord("Loaded shop data unavailable", ex.Message, "SHOPS"));
        }

        foreach (MapItem mapItem in _mapItemSnapshot)
        {
            records.Add(CreateMapItemRecord(mapItem));
        }

        MapItemRecordCountText.Text = _mapItemSnapshot.Length.ToString("N0");

        try
        {
            foreach (ItemBase drop in _bot.Drops.CurrentDropInfos.ToArray())
            {
                records.Add(CreateDropRecord(drop));
                AddItemReference(itemReferences, drop, "Current runtime drop queue");
            }
        }
        catch (Exception ex)
        {
            records.Add(CreateStatusRecord("Runtime drop queue unavailable", ex.Message, "DROPS"));
        }

        try
        {
            Monster[] monsters = _bot.Monsters.MapMonsters.ToArray();
            foreach (Monster monster in monsters)
                records.Add(CreateMonsterRecord(monster));
        }
        catch (Exception ex)
        {
            records.Add(CreateStatusRecord("Current monster field unavailable", ex.Message, "MONSTERS"));
        }

        foreach ((int itemId, List<string> references) in itemReferences.OrderBy(static pair => pair.Key))
        {
            string[] distinctReferences = references.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string title = $"Item reference [{itemId}]";
            string details = $"ITEM ID: {itemId}{Environment.NewLine}" +
                $"REFERENCE COUNT: {distinctReferences.Length:N0}{Environment.NewLine}{Environment.NewLine}" +
                string.Join(Environment.NewLine, distinctReferences.Select(static reference => $"• {reference}"));
            records.Add(new GameDataRecord
            {
                Category = "ITEM REFERENCE",
                Name = title,
                Summary = $"{distinctReferences.Length:N0} quest, shop, reward or runtime references",
                Metadata = $"Item ID {itemId}",
                Details = details,
                SearchText = $"{title} {details}"
            });
        }

        records.AddRange(_swfSourceRecords);
        _dataSnapshot = records.OrderBy(static record => record.Category).ThenBy(static record => record.Name).ToList();
        DataRecordCountText.Text = _dataSnapshot.Count.ToString("N0");
        DataStatusText.Text = $"CATALOG UPDATED  /  {_dataSnapshot.Count:N0} RECORDS  /  QUESTS, SHOPS, MAP ITEMS, DROPS, MONSTERS AND SOURCE";
        SwfSourceStatusText.Text = _swfSourceRecords.Count > 0
            ? $"CLIENT SWF INDEXED  /  {_swfSourceRecords.Count:N0} SOURCE FILES"
            : "CLIENT SWF SOURCE NOT INDEXED";

        ApplyDataFilter();
    }

    private void ApplyDataFilter()
    {
        string search = DataSearchBox?.Text?.Trim() ?? string.Empty;
        string category = (DataCategoryBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ALL DATA";
        IEnumerable<GameDataRecord> query = _dataSnapshot;

        if (!string.Equals(category, "ALL DATA", StringComparison.OrdinalIgnoreCase))
        {
            query = category switch
            {
                "QUESTS" => query.Where(static record => record.Category == "QUEST" || record.Category == "QUEST SOURCE"),
                "SHOPS" => query.Where(static record => record.Category == "SHOP" || record.Category == "SHOP SOURCE"),
                "MAP ITEMS" => query.Where(static record => record.Category == "MAP ITEM" || record.Category == "MAP SOURCE"),
                "DROPS" => query.Where(static record => record.Category == "DROP" || record.Category == "DROP SOURCE"),
                "MONSTERS" => query.Where(static record => record.Category == "MONSTER"),
                "ACHIEVEMENTS" => query.Where(static record => record.Category == "ACHIEVEMENT"),
                "ITEM REFERENCES" => query.Where(static record => record.Category == "ITEM REFERENCE"),
                "SWF SOURCE" => query.Where(static record => record.Category.EndsWith("SOURCE", StringComparison.Ordinal)),
                _ => query
            };
        }

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(record => record.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));

        GameDataRecord[] matches = query.Take(2000).ToArray();
        _dataRecords.Clear();
        foreach (GameDataRecord record in matches)
            _dataRecords.Add(record);

        DataResultMetric.Text = $"{matches.Length:N0} MATCHES";
        if (matches.Length == 0)
        {
            DataDetailTitle.Text = "No matching records";
            DataDetailMeta.Text = "Try a broader search or refresh the source catalog.";
            DataDetailText.Text = string.Empty;
        }
        else if (DataRecordList.SelectedItem is not GameDataRecord selected || !matches.Contains(selected))
        {
            DataRecordList.SelectedIndex = 0;
        }
    }

    private async void AnalyzeMap_Click(object? sender, RoutedEventArgs e)
    {
        if (_mapAnalysisRunning)
            return;

        if (!_bot.Map.Loaded)
        {
            DataStatusText.Text = "MAP ANALYSIS UNAVAILABLE  /  LOAD INTO A MAP FIRST";
            return;
        }

        string ffdecDirectory = Path.Combine(AppContext.BaseDirectory, "FFDec");
        if (!File.Exists(Path.Combine(ffdecDirectory, "ffdec.bat")))
        {
            DataStatusText.Text = $"FFDEC NOT FOUND  /  {ffdecDirectory}";
            return;
        }

        _mapAnalysisRunning = true;
        AnalyzeMapButtonState(true);
        DataStatusText.Text = "ANALYZING CURRENT MAP SWF  /  FIRST RUN MAY DOWNLOAD AND DECOMPILE THE MAP FILE";
        try
        {
            MapItem[] mapItems = await Task.Run(() => _bot.Map.FindMapItems()?.ToArray() ?? []);
            _mapItemSnapshot = mapItems;
            RefreshDataCatalog();
            DataStatusText.Text = $"MAP ANALYSIS COMPLETE  /  {mapItems.Length:N0} MAP ITEM REFERENCES FOUND";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"MAP ANALYSIS FAILED  /  {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE map analysis failed: {ex}");
        }
        finally
        {
            _mapAnalysisRunning = false;
            AnalyzeMapButtonState(false);
        }
    }

    private void AnalyzeMapButtonState(bool running)
    {
        if (AnalyzeMapButton is not null)
        {
            AnalyzeMapButton.IsEnabled = !running;
            AnalyzeMapButton.Content = running ? "ANALYZING MAP..." : "ANALYZE CURRENT MAP";
        }
    }

    private async void DecompileSwf_Click(object? sender, RoutedEventArgs e)
    {
        if (_swfIndexRunning)
            return;

        string swfPath = Path.Combine(AppContext.BaseDirectory, "skua.swf");
        string ffdecDirectory = Path.Combine(AppContext.BaseDirectory, "FFDec");
        _swfIndexRunning = true;
        DecompileSwfButtonState(true);
        DataStatusText.Text = "DECOMPILING CLIENT SWF  /  FFDEC IS RUNNING IN THE BACKGROUND";
        try
        {
            SwfSourceIndexResult result = await SwfSourceIndexer.DecompileAsync(swfPath, ffdecDirectory);
            _swfSourceRecords.Clear();
            _swfSourceRecords.AddRange(result.Records);
            RefreshDataCatalog();
            SwfSourceStatusText.Text = $"CLIENT SWF INDEXED  /  {_swfSourceRecords.Count:N0} FILES  /  {result.ExportDirectory}";
            DataStatusText.Text = $"CLIENT SWF DECOMPILED  /  {_swfSourceRecords.Count:N0} SOURCE FILES INDEXED";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"SWF DECOMPILATION FAILED  /  {ex.Message}";
            SwfSourceStatusText.Text = "SWF SOURCE INDEX FAILED";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE SWF indexing failed: {ex}");
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
            DecompileSwfButton.Content = running ? "DECOMPILING SWF..." : "DECOMPILE CLIENT SWF";
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
            string exportPath = Path.Combine(exportDirectory, $"game-data-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            string json = JsonSerializer.Serialize(_dataSnapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(exportPath, json);
            DataStatusText.Text = $"EXPORTED {_dataSnapshot.Count:N0} RECORDS  /  {exportPath}";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"CATALOG EXPORT FAILED  /  {ex.Message}";
        }
    }

    private static GameDataRecord CreateQuestRecord(QuestData quest)
    {
        string acceptRequirements = FormatItems(quest.AcceptRequirements);
        string turnInRequirements = FormatItems(quest.Requirements);
        string rewards = FormatItems(quest.Rewards);
        string rewardRates = quest.SimpleRewards.Count == 0
            ? "No simple reward-rate entries."
            : string.Join(Environment.NewLine, quest.SimpleRewards.Select(static item => $"{item.Name} [ID {item.ID}] ×{item.Quantity} / {item.Rate:0.###}% / type {item.Type}"));
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
            Name = $"{quest.Name} [{quest.ID}]",
            Summary = $"Level {quest.Level}  /  {quest.AcceptRequirements.Count:N0} accept items  /  {quest.Requirements.Count:N0} turn-in items  /  {quest.Rewards.Count:N0} rewards",
            Metadata = $"QuestData.json  /  ID {quest.ID}",
            Details = details,
            SearchText = $"{details} {acceptRequirements} {turnInRequirements} {rewards} {rewardRates}"
        };
    }

    private static GameDataRecord CreateShopRecord(int shopId, string shopName, ShopItem item)
    {
        string requirements = FormatItems(item.Requirements);
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
            Name = $"{item.Name} [{item.ID}]",
            Summary = $"{item.Cost:N0} {currency}  /  {NullLabel(item.Faction)}  /  {item.Requirements.Count:N0} materials",
            Metadata = $"Shop {shopName} [{shopId}]  /  ShopItemID {item.ShopItemID}",
            Details = details,
            SearchText = $"{details} {requirements} {item.TokenName}",
            SourceShopId = shopId,
            SourceObject = item
        };
    }

    private static GameDataRecord CreateMapItemRecord(MapItem mapItem)
    {
        string details =
            $"MAP ITEM ID: {mapItem.ID}{Environment.NewLine}" +
            $"QUEST ID: {mapItem.QuestID}{Environment.NewLine}" +
            $"MAP: {mapItem.MapName}{Environment.NewLine}" +
            $"MAP FILE: {mapItem.MapFilePath}{Environment.NewLine}" +
            $"{Environment.NewLine}This association was extracted from the current map SWF by VibeSkua's map-item parser.";
        return new GameDataRecord
        {
            Category = "MAP ITEM",
            Name = $"Map item [{mapItem.ID}]",
            Summary = $"Quest {mapItem.QuestID}  /  {mapItem.MapName}",
            Metadata = mapItem.MapFilePath,
            Details = details,
            SearchText = $"{details} {mapItem.ID} {mapItem.QuestID} {mapItem.MapName}"
        };
    }

    private static GameDataRecord CreateDropRecord(ItemBase item)
    {
        string details =
            $"CURRENT DROP: {item.Name}{Environment.NewLine}" +
            $"ITEM ID: {item.ID}{Environment.NewLine}" +
            $"QUANTITY: {item.Quantity:N0}{Environment.NewLine}" +
            $"CATEGORY: {item.CategoryString}{Environment.NewLine}" +
            $"TEMPORARY: {item.Temp}{Environment.NewLine}" +
            $"AC ITEM: {item.Coins}{Environment.NewLine}" +
            $"{Environment.NewLine}This is an observed runtime drop queue entry, not a complete monster drop table.";
        return new GameDataRecord
        {
            Category = "DROP",
            Name = $"{item.Name} [{item.ID}]",
            Summary = $"Observed quantity ×{item.Quantity:N0}  /  {item.CategoryString}",
            Metadata = "Runtime drop queue",
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
            Name = $"{monster.Name} [{monster.ID}]",
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

    private static string FormatItems(IEnumerable<ItemBase> items)
    {
        string[] lines = items.Select(static item => $"{item.Name} [ID {item.ID}] ×{item.Quantity:N0}").ToArray();
        return lines.Length == 0 ? "None" : string.Join(Environment.NewLine, lines);
    }

    private static void AddItemReferences(Dictionary<int, List<string>> references, IEnumerable<ItemBase> items, string source)
    {
        foreach (ItemBase item in items)
            AddItemReference(references, item, source);
    }

    private static void AddItemReference(Dictionary<int, List<string>> references, ItemBase item, string source)
    {
        if (item.ID <= 0)
            return;

        if (!references.TryGetValue(item.ID, out List<string>? entries))
        {
            entries = new List<string>();
            references[item.ID] = entries;
        }

        entries.Add($"{item.Name} / {source}");
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