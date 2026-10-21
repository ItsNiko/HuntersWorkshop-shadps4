// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HuntersWorkshop
{
    public sealed class UpgradeChoice
    {
        public int Value { get; set; }
        public string Label { get { return "+" + Value; } }
        public override string ToString() { return Label; }
    }
    public sealed class ActivityEntry
    {
        public string Time { get; set; }
        public string Message { get; set; }
    }

    public sealed class App : Application
    {
        Catalog catalog;
        Preferences preferences;
        Engine engine;
        Window window;
        bool busy, favoritesOnly;
        int page;
        readonly int[] effects = { -1, -1, -1, -1, -1, -1 };
        readonly TextBlock[] slots = new TextBlock[6];
        readonly Button[] slotButtons = new Button[6];
        GemEffect[] orderedEffects;
        readonly ObservableCollection<ActivityEntry> history = new ObservableCollection<ActivityEntry>();
        DispatcherTimer timer;
        readonly List<Button> kitButtons = new List<Button>();
        bool loadingItem, loadingKinds;
        readonly object recentLock = new object();
        readonly List<KitItem> pendingRecent = new List<KitItem>();
        readonly List<Button> recentButtons = new List<Button>();
        StackPanel recentPanel;
        T C<T>(string name) where T : class { return window.FindName(name) as T; }
        bool Rune { get { return C<ComboBox>("GemKind").SelectedIndex == 1; } }
        Item SelectedItem { get { return C<DataGrid>("ItemGrid").SelectedItem as Item; } }
        static Brush Color(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }
        public static string ItemKey(Item item) { return item.Category + ":" + item.Id; }

        [STAThread]
        public static int Main(string[] args)
        {
            Mutex instance = null;
            bool ownsInstance = false;
            try
            {
                instance = new Mutex(false, "Local\\HuntersWorkshopDesktop-0.3");
                try { ownsInstance = instance.WaitOne(0); }
                catch (AbandonedMutexException) { ownsInstance = true; }
                if (!ownsInstance)
                {
                    MessageBox.Show("Hunter's Workshop is already open. Use its existing window.", "Hunter's Workshop");
                    return 0;
                }
                var app = new App();
                app.Startup += (s, e) => app.Start();
                return app.Run();
            }
            catch (Exception ex)
            {
                Storage.Log("Fatal: " + ex);
                MessageBox.Show(ex.Message, "Hunter's Workshop");
                return 1;
            }
            finally
            {
                if (ownsInstance) instance.ReleaseMutex();
                if (instance != null) instance.Dispose();
            }
        }

        static BitmapSource Bitmap(string resource)
        {
            using (var stream = typeof(App).Assembly.GetManifestResourceStream(resource))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        void Start()
        {
            using (var stream = typeof(App).Assembly.GetManifestResourceStream("Theme.xaml"))
                Resources = (ResourceDictionary)XamlReader.Load(stream);
            catalog = Catalog.Read<Catalog>("catalog.json");
            preferences = Storage.Load();
            engine = new Engine(catalog) { UserPath = preferences.UserDirectory };
            orderedEffects = catalog.Effects.OrderBy(x => x.Name).ThenByDescending(x => x.Rating).ThenBy(x => x.Id).ToArray();
            using (var stream = typeof(App).Assembly.GetManifestResourceStream("Main.xaml")) window = (Window)XamlReader.Load(stream);
            window.Icon = Bitmap("HuntersWorkshop.ico");
            C<Image>("HeroImage").Source = Bitmap("workshop.png");
            C<Image>("BrandIcon").Source = Bitmap("emblem.png");
            window.MinHeight = Math.Min(window.MinHeight, SystemParameters.WorkArea.Height - 16);
            window.MinWidth = Math.Min(window.MinWidth, SystemParameters.WorkArea.Width - 16);
            window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height - 16);
            window.Width = Math.Min(window.Width, SystemParameters.WorkArea.Width - 16);
            C<Button>("Minimize").Click += (s, e) => window.WindowState = WindowState.Minimized;
            C<Button>("Maximize").Click += (s, e) => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            C<Button>("Close").Click += (s, e) => window.Close();
            C<ComboBox>("Category").ItemsSource = new[] { "All categories", "Weapons", "Armor", "Items", "Accessories (advanced)" };
            C<ComboBox>("Category").SelectedIndex = 0;
            RefreshKinds();
            C<ComboBox>("Variant").ItemsSource = new[] { "All variants", "Standard", "Uncanny", "Lost" };
            C<ComboBox>("Variant").SelectedIndex = 0;
            C<ComboBox>("GemKind").ItemsSource = new[] { "Blood gems", "Caryll runes" };
            C<ComboBox>("GemKind").SelectedIndex = 0;
            C<ComboBox>("TargetSlot").ItemsSource = new[] { "1 · Primary", "2 · Secondary", "3 · Secondary", "4 · Secondary", "5 · Secondary", "6 · Secondary" };
            C<ComboBox>("TargetSlot").SelectedIndex = 0;
            C<ListBox>("History").ItemsSource = history;
            BuildSlots();
            C<ToggleButton>("NavItems").Click += (s, e) => Navigate(0, false);
            C<ToggleButton>("NavGems").Click += (s, e) => Navigate(1, false);
            C<ToggleButton>("NavFavorites").Click += (s, e) => Navigate(0, true);
            C<ToggleButton>("NavSession").Click += (s, e) => Navigate(2, false);
            C<ToggleButton>("NavKits").Click += (s, e) => Navigate(3, false);
            C<ToggleButton>("NavSaves").Click += (s, e) => Navigate(4, false);
            C<Button>("RestoreLatest").Click += (s, e) => RestoreLatest();
            C<TextBox>("ItemSearch").TextChanged += (s, e) => FilterItems();
            C<ComboBox>("Category").SelectionChanged += (s, e) => { RefreshKinds(); FilterItems(); };
            C<ComboBox>("Kind").SelectionChanged += (s, e) => { if (!loadingKinds) FilterItems(); };
            C<CheckBox>("SearchDescriptions").Click += (s, e) => FilterItems();
            C<ComboBox>("Variant").SelectionChanged += (s, e) => FilterItems();
            C<CheckBox>("AdvancedItems").Click += (s, e) => FilterItems();
            C<DataGrid>("ItemGrid").SelectionChanged += (s, e) => SelectItem();
            C<TextBox>("EffectSearch").TextChanged += (s, e) => FilterEffects();
            C<CheckBox>("AdvancedEffects").Click += (s, e) => FilterEffects();
            C<ComboBox>("GemKind").SelectionChanged += (s, e) => { for (int i = 0; i < 6; i++) effects[i] = -1; SetGemKind(); };
            C<ComboBox>("TargetSlot").SelectionChanged += (s, e) => UpdateSlots();
            C<Button>("UseEffect").Click += (s, e) => AssignEffect();
            C<DataGrid>("EffectGrid").MouseDoubleClick += (s, e) => AssignEffect();
            C<DataGrid>("EffectGrid").SelectionChanged += (s, e) => C<Button>("UseEffect").IsEnabled = C<DataGrid>("EffectGrid").SelectedItem != null;
            C<Button>("ClearEffects").Click += (s, e) => { for (int i = 1; i < 6; i++) effects[i] = -1; UpdateSlots(); };
            C<Button>("Favorite").Click += (s, e) => Favorite();
            C<Button>("Less").Click += (s, e) => StepQuantity(-1);
            C<Button>("More").Click += (s, e) => StepQuantity(1);
            C<TextBox>("Quantity").TextChanged += (s, e) =>
            {
                int typed;
                if (!loadingItem && Int32.TryParse(C<TextBox>("Quantity").Text.Trim(), out typed) && typed >= 1 && typed <= 999 && typed != preferences.ItemQuantity)
                { preferences.ItemQuantity = typed; Persist(); }
                if (!busy) Refresh();
            };
            C<ComboBox>("Upgrade").SelectionChanged += (s, e) =>
            {
                var choice = C<ComboBox>("Upgrade").SelectedItem as UpgradeChoice;
                if (!loadingItem && choice != null && choice.Value != preferences.ItemUpgrade) { preferences.ItemUpgrade = choice.Value; Persist(); }
            };
            C<Button>("GiveMax").Click += async (s, e) =>
            {
                Item item = SelectedItem;
                if (item == null) return;
                var upgrade = C<ComboBox>("Upgrade").SelectedItem as UpgradeChoice;
                int level = upgrade == null ? 0 : upgrade.Value;
                int full = Math.Min(item.Stack, 999);
                await RunOperation("Granting a full stack…", () => GrantAndRecord(item, level, full));
            };
            C<Button>("AddToKit").Click += (s, e) =>
            {
                Item item = SelectedItem;
                if (item == null) return;
                var upgrade = C<ComboBox>("Upgrade").SelectedItem as UpgradeChoice;
                int level = upgrade == null ? 0 : upgrade.Value;
                int amount;
                if (!Int32.TryParse(C<TextBox>("Quantity").Text.Trim(), out amount)) amount = 1;
                AddToDraft(item, level, amount);
            };
            C<Button>("Connect").Click += async (s, e) => await RunOperation("Connecting to shadPS4…", () =>
            {
                if (engine.Connected) { engine.Dispose(); return Task.FromResult("Disconnected. Inventory changes remain in your save."); }
                engine.Connect();
                preferences.ShadDirectory = engine.ShadPath;
                preferences.UserDirectory = engine.UserPath;
                try { Storage.Save(preferences); }
                catch (IOException ex) { Storage.Log("Could not save the detected shadPS4 folder: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { Storage.Log("Could not save the detected shadPS4 folder: " + ex.Message); }
                return Task.FromResult("Connected. Wait for Hunter ready, then choose your item or gem.");
            });
            C<Button>("Backup").Click += async (s, e) => await RunOperation("Creating and verifying your save snapshot…", () => Task.FromResult("Save snapshot verified: " + engine.Backup()));
            C<Button>("OpenBackups").Click += (s, e) => OpenFolder(Storage.Backups);
            C<Button>("OpenData").Click += (s, e) => OpenFolder(Storage.Root);
            C<Button>("BrowsePort").Click += (s, e) => BrowsePort();
            C<Button>("SaveRecipe").Click += (s, e) => SaveRecipe();
            C<Button>("LoadRecipe").Click += (s, e) => LoadRecipe();
            C<Button>("GiveItem").Click += async (s, e) =>
            {
                int quantity;
                if (!Int32.TryParse(C<TextBox>("Quantity").Text, out quantity)) { Notify("Enter a whole-number quantity.", true); return; }
                Item item = SelectedItem;
                var upgrade = C<ComboBox>("Upgrade").SelectedItem as UpgradeChoice;
                int level = upgrade == null ? 0 : upgrade.Value;
                await RunOperation("Granting your selected item…", () => GrantAndRecord(item, level, quantity));
            };
            C<Button>("GiveGem").Click += async (s, e) =>
            {
                bool rune = Rune;
                int shape = SelectedShape();
                int source = C<ComboBox>("GemSource").SelectedItem == null ? -1 : (int)C<ComboBox>("GemSource").SelectedItem;
                int[] chosen = (int[])effects.Clone();
                await RunOperation("Creating and checking your " + (rune ? "rune" : "blood gem") + "…", () => engine.GiveGem(rune, shape, source, chosen));
            };
            window.PreviewKeyDown += (s, e) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F && page < 2)
                { (page == 0 ? C<TextBox>("ItemSearch") : C<TextBox>("EffectSearch")).Focus(); e.Handled = true; }
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D && page == 0 && !busy) { Favorite(); e.Handled = true; }
            };
            window.Closing += (s, e) =>
            {
                if (busy) { e.Cancel = true; Notify("Wait for the current operation to finish before closing."); return; }
                try { engine.Dispose(); if (timer != null) timer.Stop(); }
                catch (Exception ex) { e.Cancel = true; Notify(ex.Message, true); }
            };
            SetGemKind();
            Navigate(0, false);
            Refresh();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => { if (!busy) Refresh(); };
            timer.Start();
            window.Show();
        }

        void BuildSlots()
        {
            for (int i = 0; i < 6; i++)
            {
                int index = i;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                slots[i] = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
                slotButtons[i] = new Button { Content = slots[i], Padding = new Thickness(10, 11, 8, 11), HorizontalContentAlignment = HorizontalAlignment.Left };
                slotButtons[i].Click += (s, e) => C<ComboBox>("TargetSlot").SelectedIndex = index;
                row.Children.Add(slotButtons[i]);
                var clear = new Button { Content = "×", Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Clear effect " + (i + 1), Style = (Style)Resources["QuietButton"] };
                Grid.SetColumn(clear, 1);
                clear.Click += (s, e) => { effects[index] = -1; UpdateSlots(); };
                row.Children.Add(clear);
                C<StackPanel>("Slots").Children.Add(row);
            }
        }

        sealed class Kit
        {
            public string Name, Description;
            public int[][] Parts; // { category, id, default quantity }
        }

        static readonly Kit[] Kits =
        {
            new Kit { Name = "Hunter's supplies", Description = "Blood Vials, Quicksilver Bullets, Antidotes, Sedatives, Beast Blood Pellets, Molotovs, Pebbles and Throwing Knives.",
                Parts = new[] { new[] { 4, 1000, 20 }, new[] { 4, 900, 20 }, new[] { 4, 1100, 10 }, new[] { 4, 1101, 10 }, new[] { 4, 1110, 10 }, new[] { 4, 1200, 10 }, new[] { 4, 1230, 20 }, new[] { 4, 1240, 20 } } },
            new Kit { Name = "Weapon upgrade materials", Description = "Blood Stone Shard, Twin Blood Stone Shards, Blood Stone Chunk and Blood Rock.",
                Parts = new[] { new[] { 4, 3000, 99 }, new[] { 4, 3010, 99 }, new[] { 4, 3020, 99 }, new[] { 4, 3030, 99 } } },
            new Kit { Name = "Blood Echoes boost", Description = "Coldblood items to use from your inventory for echoes.",
                Parts = new[] { new[] { 4, 1513, 20 }, new[] { 4, 1516, 10 }, new[] { 4, 1519, 5 }, new[] { 4, 1592, 3 } } },
            new Kit { Name = "Insight", Description = "Madman's Knowledge to use from your inventory.",
                Parts = new[] { new[] { 4, 1500, 10 } } }
        };

        sealed class Grant { public Item Item; public int Upgrade, Quantity; }

        // One editable amount in a kit card. Value is the last committed (clamped, remembered) amount.
        sealed class KitRow
        {
            public Item Item; public int Upgrade, Limit, Default, Value; public TextBox Box; public Action<int> Save;
        }

        static int Commit(KitRow row)
        {
            int n;
            if (!Int32.TryParse(row.Box.Text.Trim(), out n)) n = row.Value;
            n = Math.Max(1, Math.Min(n, row.Limit));
            row.Box.Text = n.ToString();
            if (n != row.Value) { row.Value = n; row.Save(n); }
            return n;
        }

        static string ItemLabel(Item item, int upgrade)
        {
            return item.Name + (item.Category == 0 && upgrade > 0 ? " +" + upgrade : "");
        }

        StackPanel NewCard(StackPanel host, string title, string description)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, FontFamily = new FontFamily("Georgia"), FontSize = 20, Margin = new Thickness(0, 0, 0, 6) });
            if (!String.IsNullOrEmpty(description))
                panel.Children.Add(new TextBlock { Text = description, Style = (Style)Resources["MutedText"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            host.Children.Add(new Border { Style = (Style)Resources["Card"], Margin = new Thickness(0, 0, 0, 15), Child = panel });
            return panel;
        }

        KitRow AddRow(Panel panel, string label, Item item, int upgrade, int limit, int value, int defaultValue, Action<int> save, Action remove)
        {
            var row = new KitRow { Item = item, Upgrade = upgrade, Limit = limit, Save = save };
            row.Value = Math.Max(1, Math.Min(value, limit));
            row.Default = Math.Max(1, Math.Min(defaultValue, limit));
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = Color("#C3BDB6"), Margin = new Thickness(0, 0, 10, 0) });
            row.Box = new TextBox { Text = row.Value.ToString(), HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = "Amount (1–" + limit + ")" };
            Grid.SetColumn(row.Box, 1);
            grid.Children.Add(row.Box);
            row.Box.LostFocus += (sender, args) => Commit(row);
            if (remove != null)
            {
                var x = new Button { Content = "×", Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove from kit", Style = (Style)Resources["QuietButton"] };
                x.Click += (sender, args) => remove();
                Grid.SetColumn(x, 2);
                grid.Children.Add(x);
            }
            panel.Children.Add(grid);
            return row;
        }

        WrapPanel NewBar(StackPanel panel)
        {
            var bar = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(bar);
            return bar;
        }

        Button Btn(Panel bar, string text, bool primary, Action click)
        {
            var button = new Button { Content = text, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 6) };
            if (primary) button.Style = (Style)Resources["PrimaryButton"];
            button.Click += (sender, args) => click();
            bar.Children.Add(button);
            return button;
        }

        static List<Grant> CollectGrants(List<KitRow> rows)
        {
            var list = new List<Grant>();
            foreach (KitRow row in rows) list.Add(new Grant { Item = row.Item, Upgrade = row.Upgrade, Quantity = Commit(row) });
            return list;
        }

        static void SetAll(List<KitRow> rows, bool max)
        {
            foreach (KitRow row in rows) { row.Box.Text = (max ? row.Limit : row.Default).ToString(); Commit(row); }
        }

        void BuildKits()
        {
            var host = C<StackPanel>("KitList");
            host.Children.Clear(); kitButtons.Clear();
            var recentCard = NewCard(host, "Recently given", null);
            recentPanel = new StackPanel();
            recentCard.Children.Add(recentPanel);
            FillRecent();
            BuildBuilder(host);
            if (preferences.CustomKits.Count > 0)
                host.Children.Add(new TextBlock { Text = "YOUR KITS", Style = (Style)Resources["Eyebrow"], Margin = new Thickness(2, 4, 0, 10) });
            foreach (CustomKit kit in preferences.CustomKits.ToArray()) BuildCustomCard(host, kit);
            host.Children.Add(new TextBlock { Text = "READY-MADE KITS", Style = (Style)Resources["Eyebrow"], Margin = new Thickness(2, 4, 0, 10) });
            foreach (Kit kit in Kits) BuildReadyMadeCard(host, kit);
            BuildFavoritesCard(host);
        }

        void BuildReadyMadeCard(StackPanel host, Kit kit)
        {
            var panel = NewCard(host, kit.Name, kit.Description + " Amounts are editable and remembered.");
            var rows = new List<KitRow>();
            foreach (int[] part in kit.Parts)
            {
                Item item = catalog.Items.FirstOrDefault(x => x.Category == part[0] && x.Id == part[1]);
                if (item == null) continue;
                string key = kit.Name + "|" + part[0] + ":" + part[1];
                int stored;
                if (!preferences.KitQuantities.TryGetValue(key, out stored)) stored = part[2];
                rows.Add(AddRow(panel, item.Name, item, 0, Math.Min(item.Stack, 999), stored, part[2], n => { preferences.KitQuantities[key] = n; Persist(); }, null));
            }
            var bar = NewBar(panel);
            kitButtons.Add(Btn(bar, "Give kit", true, () => GiveKit(kit.Name, CollectGrants(rows))));
            Btn(bar, "Max all", false, () => SetAll(rows, true));
            Btn(bar, "Reset amounts", false, () => SetAll(rows, false));
        }

        void BuildCustomCard(StackPanel host, CustomKit kit)
        {
            var panel = NewCard(host, kit.Name, kit.Items.Count + (kit.Items.Count == 1 ? " item" : " items") + ". Amounts are editable and remembered.");
            var rows = new List<KitRow>();
            foreach (KitItem entry in kit.Items)
            {
                Item item = catalog.Items.FirstOrDefault(x => x.Category == entry.Category && x.Id == entry.Id);
                if (item == null) continue;
                KitItem target = entry;
                rows.Add(AddRow(panel, ItemLabel(item, entry.Upgrade), item, entry.Upgrade, Math.Min(item.Stack, 999), entry.Quantity, entry.Quantity, n => { target.Quantity = n; Persist(); }, null));
            }
            var bar = NewBar(panel);
            kitButtons.Add(Btn(bar, "Give kit", true, () => GiveKit(kit.Name, CollectGrants(rows))));
            Btn(bar, "Max all", false, () => SetAll(rows, true));
            Btn(bar, "Edit in builder", false, () =>
            {
                var copy = new CustomKit { Name = kit.Name };
                foreach (KitItem source in kit.Items) copy.Items.Add(new KitItem { Category = source.Category, Id = source.Id, Upgrade = source.Upgrade, Quantity = source.Quantity });
                preferences.Draft = copy; Persist(); BuildKits();
                Notify("Kit loaded into the builder. Change it and press Save kit to replace it.");
            });
            Btn(bar, "Delete", false, () =>
            {
                if (MessageBox.Show(window, "Delete the kit \"" + kit.Name + "\"?", "Hunter's Workshop", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                preferences.CustomKits.Remove(kit); Persist(); BuildKits();
            });
        }

        void BuildBuilder(StackPanel host)
        {
            var panel = NewCard(host, "Kit builder", "On the Items page, pick an item, amount and reinforcement, then press Add to kit. Name and save the kit here. Saved kits can be given, edited or deleted any time.");
            var nameBox = new TextBox { Text = preferences.Draft.Name ?? "", MaxLength = 80, Tag = "Kit name", Margin = new Thickness(0, 0, 0, 10) };
            nameBox.TextChanged += (sender, args) => { preferences.Draft.Name = nameBox.Text; };
            nameBox.LostFocus += (sender, args) => Persist();
            panel.Children.Add(nameBox);
            var rows = new List<KitRow>();
            foreach (KitItem entry in preferences.Draft.Items.ToArray())
            {
                Item item = catalog.Items.FirstOrDefault(x => x.Category == entry.Category && x.Id == entry.Id);
                if (item == null) continue;
                KitItem target = entry;
                rows.Add(AddRow(panel, ItemLabel(item, entry.Upgrade), item, entry.Upgrade, Math.Min(item.Stack, 999), entry.Quantity, 1,
                    n => { target.Quantity = n; Persist(); },
                    () => { preferences.Draft.Items.Remove(target); Persist(); BuildKits(); }));
            }
            if (rows.Count == 0)
                panel.Children.Add(new TextBlock { Text = "No items yet.", Style = (Style)Resources["MutedText"], Margin = new Thickness(0, 0, 0, 4) });
            var bar = NewBar(panel);
            Btn(bar, "Save kit", true, () =>
            {
                string kitName = (nameBox.Text ?? "").Trim();
                if (kitName.Length < 1 || kitName.Length > 80) { Notify("Give the kit a name between 1 and 80 characters.", true); return; }
                if (preferences.Draft.Items.Count == 0) { Notify("Add at least one item to the kit first.", true); return; }
                foreach (KitRow row in rows) Commit(row);
                var saved = new CustomKit { Name = kitName };
                foreach (KitItem draftItem in preferences.Draft.Items) saved.Items.Add(new KitItem { Category = draftItem.Category, Id = draftItem.Id, Upgrade = draftItem.Upgrade, Quantity = draftItem.Quantity });
                preferences.CustomKits.RemoveAll(k => String.Equals(k.Name, kitName, StringComparison.OrdinalIgnoreCase));
                preferences.CustomKits.Add(saved);
                preferences.Draft = new CustomKit();
                Persist(); BuildKits();
                Notify("Kit saved: " + kitName);
            });
            Btn(bar, "Clear builder", false, () => { preferences.Draft = new CustomKit(); Persist(); BuildKits(); });
        }

        void BuildFavoritesCard(StackPanel host)
        {
            int count = catalog.Items.Count(x => preferences.Favorites.Contains(ItemKey(x)));
            var panel = NewCard(host, "Your favorites", count == 0
                ? "Star items on the Items page and they will be granted from here."
                : count + (count == 1 ? " favorite item" : " favorite items") + ", each granted at the amount below (weapons at +0, capped to each item's carry limit). The amount is remembered.");
            var row = AddRow(panel, "Amount of each favorite", null, 0, 999, preferences.FavoritesKitQuantity, 1, n => { preferences.FavoritesKitQuantity = n; Persist(); }, null);
            var bar = NewBar(panel);
            kitButtons.Add(Btn(bar, "Give favorites", true, () =>
            {
                int amount = Commit(row);
                var grants = new List<Grant>();
                foreach (Item item in catalog.Items.Where(x => preferences.Favorites.Contains(ItemKey(x))))
                    grants.Add(new Grant { Item = item, Upgrade = 0, Quantity = Math.Min(amount, Math.Min(item.Stack, 999)) });
                GiveKit("Your favorites", grants);
            }));
        }

        async void GiveKit(string name, List<Grant> grants)
        {
            var valid = new List<Grant>();
            foreach (Grant grant in grants)
            {
                try { catalog.ValidateItem(grant.Item, grant.Upgrade, grant.Quantity); valid.Add(grant); }
                catch (InvalidOperationException) { }
            }
            if (valid.Count == 0) { Notify(grants.Count == 0 ? "This kit has nothing to give yet." : "Nothing in this kit can be given.", true); return; }
            int skipped = grants.Count - valid.Count;
            await RunOperation("Giving kit: " + name + "…", async () =>
            {
                int done = 0;
                try
                {
                    foreach (Grant grant in valid) { await engine.GiveItem(grant.Item, grant.Upgrade, grant.Quantity); Remember(grant.Item, grant.Upgrade, grant.Quantity); done++; }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Kit stopped after " + done + " of " + valid.Count + " items: " + ex.Message);
                }
                return name + ": " + done + " item" + (done == 1 ? "" : "s") + " given" + (skipped > 0 ? " (" + skipped + " skipped)" : "") + ".";
            });
        }

        // ---- Categories and filtering -------------------------------------------------------------
        const string UnusedKind = "Unused / unnamed";
        static readonly string[] WeaponKinds = { "Melee", "Firearms", "Shields & torches" };
        static readonly string[] ArmorKinds = { "Head", "Body", "Hands", "Legs" };
        static readonly string[] ItemKinds = { "Healing & consumables", "Throwables & ammo", "Weapon buffs", "Echoes & Insight", "Upgrade materials", "Hunter tools", "Tools & bells", "Keys & story items", "Hunter badges", "Messenger accessories", "Chalices", "Ritual materials", "Other" };

        static bool HasAny(string text, params string[] words)
        {
            foreach (string word in words) if (text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Best-effort sub-category: armor by slot id, items by id range, weapons by name.
        static string ItemKind(Item item)
        {
            if (!item.Known) return UnusedKind;
            string name = item.Name ?? "";
            int id = item.Id;
            if (item.Category == 0)
            {
                if (HasAny(name, "Shield", "Torch")) return "Shields & torches";
                if (HasAny(name, "Blunderbuss", "Pistol", "Cannon", "Gatling", "Evelyn", "Flamesprayer") || (HasAny(name, "Rifle") && !HasAny(name, "Spear"))) return "Firearms";
                return "Melee";
            }
            if (item.Category == 1)
            {
                switch ((id / 1000) % 10)
                {
                    case 0: return "Head";
                    case 1: return "Body";
                    case 2: return "Hands";
                    default: return "Legs";
                }
            }
            if (item.Category == 4)
            {
                if (HasAny(name, "Chalice") || (id >= 6000 && id < 7000)) return "Chalices";
                if (HasAny(name, "Bell")) return "Tools & bells";
                if ((id >= 700 && id <= 706) || (id >= 1000 && id < 1200)) return "Healing & consumables";
                if (id == 900 || (id >= 1200 && id < 1300)) return "Throwables & ammo";
                if (id >= 1300 && id < 1400) return "Weapon buffs";
                if (id >= 1500 && id < 1600) return "Echoes & Insight";
                if (id >= 2000 && id < 2200) return "Hunter tools";
                if (id >= 3000 && id < 3100) return "Upgrade materials";
                if (id < 700 || (id >= 1400 && id < 1500) || (id >= 4100 && id < 4110)) return "Tools & bells";
                if ((id >= 4000 && id < 4100) || (id >= 4300 && id < 4400)) return "Keys & story items";
                if (id >= 4110 && id < 4130) return "Hunter badges";
                if (id >= 4900 && id < 5000) return "Messenger accessories";
                if (id >= 7000 && id < 8000) return "Ritual materials";
                return "Other";
            }
            return "Accessories";
        }

        void RefreshKinds()
        {
            string category = C<ComboBox>("Category").SelectedItem as string;
            string[] kinds = category == "Weapons" ? WeaponKinds : category == "Armor" ? ArmorKinds : category == "Items" ? ItemKinds : new string[0];
            var list = new List<string> { "All kinds" };
            list.AddRange(kinds);
            if (kinds.Length > 0) list.Add(UnusedKind);
            loadingKinds = true;
            try
            {
                C<ComboBox>("Kind").ItemsSource = list;
                C<ComboBox>("Kind").SelectedIndex = 0;
                C<ComboBox>("Kind").IsEnabled = kinds.Length > 0;
            }
            finally { loadingKinds = false; }
        }

        // ---- Recently given (regrant list) --------------------------------------------------------
        // Grants run on a worker thread, so they are queued here and merged into the saved list on the
        // interface thread when the operation ends.
        void Remember(Item item, int upgrade, int quantity)
        {
            lock (recentLock) pendingRecent.Add(new KitItem { Category = item.Category, Id = item.Id, Upgrade = upgrade, Quantity = quantity });
        }

        void FlushRecent()
        {
            KitItem[] fresh;
            lock (recentLock) { fresh = pendingRecent.ToArray(); pendingRecent.Clear(); }
            if (fresh.Length == 0) return;
            foreach (KitItem entry in fresh)
            {
                KitItem added = entry;
                preferences.Recent.RemoveAll(x => x.Category == added.Category && x.Id == added.Id && x.Upgrade == added.Upgrade);
                preferences.Recent.Insert(0, added);
            }
            if (preferences.Recent.Count > 30) preferences.Recent.RemoveRange(30, preferences.Recent.Count - 30);
            Persist();
            if (page == 3) FillRecent();
        }

        async Task<string> GrantAndRecord(Item item, int upgrade, int quantity)
        {
            string message = await engine.GiveItem(item, upgrade, quantity);
            Remember(item, upgrade, quantity);
            return message;
        }

        async void Regrant(KitItem entry)
        {
            Item item = catalog.Items.FirstOrDefault(x => x.Category == entry.Category && x.Id == entry.Id);
            if (item == null) { Notify("That item is no longer in the catalogue.", true); return; }
            int quantity = Math.Max(1, Math.Min(entry.Quantity, Math.Min(item.Stack, 999)));
            int upgrade = entry.Upgrade;
            await RunOperation("Granting " + ItemLabel(item, upgrade) + "…", () => GrantAndRecord(item, upgrade, quantity));
        }

        void AddToDraft(Item item, int level, int amount)
        {
            amount = Math.Max(1, Math.Min(amount, Math.Min(item.Stack, 999)));
            var draft = preferences.Draft;
            KitItem existing = draft.Items.FirstOrDefault(x => x.Category == item.Category && x.Id == item.Id && x.Upgrade == level);
            if (existing != null) existing.Quantity = amount;
            else if (draft.Items.Count >= 60) { Notify("A kit can hold up to 60 items.", true); return; }
            else draft.Items.Add(new KitItem { Category = item.Category, Id = item.Id, Upgrade = level, Quantity = amount });
            Persist();
            Notify(ItemLabel(item, level) + " ×" + amount + " added to the kit builder (" + draft.Items.Count + " in draft). Open Kits & bundles to name and save it.");
            if (page == 3) BuildKits();
        }

        void FillRecent()
        {
            if (recentPanel == null) return;
            recentPanel.Children.Clear();
            recentButtons.Clear();
            bool any = false;
            foreach (KitItem entry in preferences.Recent.ToArray())
            {
                KitItem current = entry;
                Item item = catalog.Items.FirstOrDefault(x => x.Category == current.Category && x.Id == current.Id);
                if (item == null) continue;
                any = true;
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(new TextBlock { Text = ItemLabel(item, current.Upgrade) + "  ×" + current.Quantity, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = Color("#C3BDB6") });
                var again = new Button { Content = "Give again", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
                again.Click += (sender, args) => Regrant(current);
                Grid.SetColumn(again, 1);
                grid.Children.Add(again);
                var add = new Button { Content = "+ kit", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Add to the kit builder" };
                add.Click += (sender, args) => AddToDraft(item, current.Upgrade, current.Quantity);
                Grid.SetColumn(add, 2);
                grid.Children.Add(add);
                recentPanel.Children.Add(grid);
                recentButtons.Add(again);
            }
            if (!any)
            {
                recentPanel.Children.Add(new TextBlock { Text = "Nothing given yet. Items you give appear here so you can give them again in one click.", Style = (Style)Resources["MutedText"], TextWrapping = TextWrapping.Wrap });
                return;
            }
            var bar = NewBar(recentPanel);
            Btn(bar, "Clear list", false, () => { preferences.Recent.Clear(); Persist(); FillRecent(); });
        }

        // ---- Save manager ---------------------------------------------------------------------------
        static string Size(long bytes)
        {
            if (bytes >= 1024L * 1024) return (bytes / 1048576.0).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return bytes + " B";
        }

        string UserFolder()
        {
            if (!String.IsNullOrWhiteSpace(engine.UserPath)) return engine.UserPath;
            return String.IsNullOrWhiteSpace(engine.ShadPath) ? null : Engine.ShadUserDirectory(engine.ShadPath);
        }

        async void SaveOperation(string description, Func<Task<string>> operation)
        {
            await RunOperation(description, operation);
            if (page == 4) BuildSaves();
        }

        void RestoreSnapshot(SnapshotInfo snapshot)
        {
            if (busy) return;
            string label = String.IsNullOrWhiteSpace(snapshot.Label) ? "" : " (" + snapshot.Label + ")";
            if (MessageBox.Show(window, "Restore the snapshot from " + snapshot.Title + label + "?\n\nYour current saves are backed up first, so this can be undone from the list. Close shadPS4 completely before you continue.",
                "Hunter's Workshop", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SaveOperation("Restoring your save snapshot…", () => Task.FromResult(engine.RestoreSnapshot(snapshot)));
        }

        void RestoreLatest()
        {
            SnapshotInfo latest;
            try { latest = SaveBackup.Snapshots(Storage.Backups).FirstOrDefault(); }
            catch (Exception ex) { Notify(ex.Message, true); return; }
            if (latest == null) { Notify("There are no snapshots yet. Use Back up saves now first.", true); return; }
            RestoreSnapshot(latest);
        }

        void BuildSaves()
        {
            var host = C<StackPanel>("SaveList");
            host.Children.Clear();
            string user = UserFolder();
            List<SnapshotInfo> snapshots;
            try { snapshots = SaveBackup.Snapshots(Storage.Backups); }
            catch (Exception ex) { Notify(ex.Message, true); snapshots = new List<SnapshotInfo>(); }

            // Current saves
            string[] files = new string[0];
            string problem = null;
            if (user != null)
            {
                try { files = SaveBackup.ListFiles(user, Engine.SaveTitles).ToArray(); }
                catch (IOException ex) { problem = ex.Message; }
                catch (UnauthorizedAccessException ex) { problem = ex.Message; }
            }
            string summary;
            if (user == null) summary = "No shadPS4 folder yet. Connect to the game or choose the folder in Session.";
            else if (problem != null) summary = problem;
            else if (files.Length == 0) summary = "No Bloodborne saves found in " + user + ".";
            else
            {
                long total = 0; DateTime newest = DateTime.MinValue;
                foreach (string file in files)
                {
                    var info = new FileInfo(file);
                    total += info.Length;
                    if (info.LastWriteTime > newest) newest = info.LastWriteTime;
                }
                summary = files.Length + " save files · " + Size(total) + " · last written " + newest.ToString("ddd d MMM · HH:mm:ss") + "\n" + user;
            }
            var current = NewCard(host, "Your saves", summary);
            var bar = NewBar(current);
            Btn(bar, "Back up now", true, () => SaveOperation("Creating and verifying your save snapshot…", () => Task.FromResult("Save snapshot verified: " + engine.Backup())));
            Btn(bar, "Restore latest snapshot", false, RestoreLatest);
            if (user != null) Btn(bar, "Open save folder  ↗", false, () => OpenFolder(Path.Combine(user, "savedata")));
            Btn(bar, "Refresh", false, BuildSaves);
            if (files.Length > 0)
            {
                var list = new StackPanel();
                foreach (string file in files)
                {
                    var info = new FileInfo(file);
                    list.Children.Add(new TextBlock { Text = file.Substring(user.Length).TrimStart(Path.DirectorySeparatorChar) + "   ·   " + Size(info.Length) + "   ·   " + info.LastWriteTime.ToString("d MMM HH:mm"), FontSize = 11, Foreground = Color("#9C948E"), Margin = new Thickness(0, 0, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis });
                }
                current.Children.Add(new Expander { Header = "Save files on disk", Foreground = Color("#B8AFA9"), Margin = new Thickness(0, 6, 0, 0), Content = new ScrollViewer { MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list } });
            }

            // Snapshots
            long snapshotBytes = snapshots.Sum(x => x.Bytes);
            var header = NewCard(host, "Snapshots", snapshots.Count == 0
                ? "No snapshots yet. One is made automatically before your first grant of a session, or press Back up now."
                : snapshots.Count + (snapshots.Count == 1 ? " snapshot" : " snapshots") + " · " + Size(snapshotBytes) + ". Restoring always backs up your current saves first.");
            var headerBar = NewBar(header);
            Btn(headerBar, "Open backup folder  ↗", false, () => OpenFolder(Storage.Backups));
            if (snapshots.Count > 10)
                Btn(headerBar, "Keep newest 10, delete the rest", false, () =>
                {
                    if (busy) return;
                    int extra = snapshots.Count - 10;
                    if (MessageBox.Show(window, "Permanently delete the " + extra + " oldest snapshots?", "Hunter's Workshop", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    var doomed = snapshots.Skip(10).ToList();
                    SaveOperation("Deleting old snapshots…", () =>
                    {
                        foreach (SnapshotInfo old in doomed) SaveBackup.Delete(old, Storage.Backups);
                        return Task.FromResult(doomed.Count + " old snapshots deleted.");
                    });
                });
            foreach (SnapshotInfo snapshot in snapshots)
            {
                SnapshotInfo item = snapshot;
                string detail = item.Files + (item.Files == 1 ? " file" : " files") + " · " + Size(item.Bytes);
                var card = NewCard(host, item.Title, String.IsNullOrWhiteSpace(item.Label) ? detail : item.Label + "\n" + detail);
                var row = NewBar(card);
                Btn(row, "Restore", true, () => RestoreSnapshot(item));
                Btn(row, "Label", false, () =>
                {
                    string text = Prompt("Label this snapshot", item.Label ?? "", "Save label");
                    if (text == null) return;
                    try { SaveBackup.SetLabel(item, text); BuildSaves(); }
                    catch (Exception ex) { Notify(ex.Message, true); }
                });
                Btn(row, "Open  ↗", false, () => OpenFolder(item.Folder));
                Btn(row, "Delete", false, () =>
                {
                    if (busy) return;
                    if (MessageBox.Show(window, "Permanently delete the snapshot from " + item.Title + "?", "Hunter's Workshop", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    SaveOperation("Deleting the snapshot…", () => { SaveBackup.Delete(item, Storage.Backups); return Task.FromResult("Snapshot deleted."); });
                });
            }
        }

        void Navigate(int target, bool favorites)
        {
            page = target; favoritesOnly = favorites;
            C<Grid>("ItemsPage").Visibility = target == 0 ? Visibility.Visible : Visibility.Collapsed;
            C<Grid>("GemsPage").Visibility = target == 1 ? Visibility.Visible : Visibility.Collapsed;
            C<Grid>("SessionPage").Visibility = target == 2 ? Visibility.Visible : Visibility.Collapsed;
            C<Grid>("KitsPage").Visibility = target == 3 ? Visibility.Visible : Visibility.Collapsed;
            C<Grid>("SavesPage").Visibility = target == 4 ? Visibility.Visible : Visibility.Collapsed;
            C<ToggleButton>("NavItems").IsChecked = target == 0 && !favorites;
            C<ToggleButton>("NavFavorites").IsChecked = target == 0 && favorites;
            C<ToggleButton>("NavGems").IsChecked = target == 1;
            C<ToggleButton>("NavSession").IsChecked = target == 2;
            C<ToggleButton>("NavKits").IsChecked = target == 3;
            C<ToggleButton>("NavSaves").IsChecked = target == 4;
            C<TextBlock>("ItemsHeading").Text = favorites ? "Your favorites" : "Items & equipment";
            if (target == 0) FilterItems();
            if (target == 3) { BuildKits(); Refresh(); }
            if (target == 4) BuildSaves();
        }

        public static bool Search(string text, string query)
        {
            return (query ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .All(word => (text ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        void FilterItems()
        {
            Item previous = SelectedItem;
            string search = C<TextBox>("ItemSearch").Text.Trim();
            string category = C<ComboBox>("Category").SelectedItem as string;
            string variant = C<ComboBox>("Variant").SelectedItem as string;
            string kind = C<ComboBox>("Kind").SelectedItem as string;
            bool anyKind = kind == null || kind == "All kinds";
            bool advanced = C<CheckBox>("AdvancedItems").IsChecked == true;
            bool inDescriptions = C<CheckBox>("SearchDescriptions").IsChecked == true;
            var items = catalog.Items.Where(x =>
                (x.Known || advanced || kind == UnusedKind) &&
                (!favoritesOnly || preferences.Favorites.Contains(ItemKey(x))) &&
                (category == "All categories" || category == x.Group) &&
                (variant == "All variants" || variant == x.Variant) &&
                (anyKind || ItemKind(x) == kind) &&
                (Search(x.Name, search) || (inDescriptions && Search(x.Description, search)) || x.Id.ToString() == search || x.DisplayId.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(x => x.Name).ToArray();
            C<DataGrid>("ItemGrid").ItemsSource = items;
            C<DataGrid>("ItemGrid").SelectedItem = previous != null && items.Contains(previous) ? previous : items.FirstOrDefault();
            C<TextBlock>("ItemCount").Text = items.Length.ToString("N0") + (items.Length == 1 ? " entry" : " entries");
            C<TextBlock>("NoItems").Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectItem();
        }

        void SelectItem()
        {
            Item item = SelectedItem;
            C<TextBlock>("ItemName").Text = item == null ? "Choose an item" : item.Name;
            C<TextBlock>("Description").Text = item == null ? "Your selection will appear here." : System.Text.RegularExpressions.Regex.Replace(item.Description.Replace("\r\n", "\n"), @"(?<!\n)\n(?!\n)", " ");
            C<TextBlock>("Description").ToolTip = item == null ? null : item.Description;
            C<TextBlock>("ItemBadge").Text = item == null ? "" : (item.Variant == "Standard" ? item.Group.ToUpperInvariant() : "CHALICE · " + item.Variant.ToUpperInvariant());
            C<TextBlock>("UnusedNote").Visibility = item != null && !item.Known ? Visibility.Visible : Visibility.Collapsed;
            loadingItem = true;
            try
            {
                var choices = (item == null ? new[] { 0 } : item.Upgrades).Select(n => new UpgradeChoice { Value = n }).ToArray();
                C<ComboBox>("Upgrade").ItemsSource = choices;
                int remembered = System.Array.FindIndex(choices, ch => ch.Value == preferences.ItemUpgrade);
                C<ComboBox>("Upgrade").SelectedIndex = remembered < 0 ? 0 : remembered;
                int limit = item == null ? 1 : Math.Min(item.Stack, 999);
                C<TextBox>("Quantity").Text = Math.Max(1, Math.Min(preferences.ItemQuantity, limit)).ToString();
            }
            finally { loadingItem = false; }
            C<ComboBox>("Upgrade").IsEnabled = item != null && item.Max > 0;
            C<TextBlock>("ItemLimit").Text = item == null ? "" : "Carry stack limit: " + Math.Min(item.Stack, 999) + " · " + item.DisplayId;
            C<Button>("Favorite").Content = item != null && preferences.Favorites.Contains(ItemKey(item)) ? "★" : "☆";
            C<Button>("Favorite").IsEnabled = item != null;
            if (!busy) Refresh();
        }

        void Favorite()
        {
            Item item = SelectedItem;
            if (item == null) return;
            string key = ItemKey(item);
            if (preferences.Favorites.Contains(key)) preferences.Favorites.Remove(key); else preferences.Favorites.Add(key);
            Persist();
            if (favoritesOnly) FilterItems(); else C<Button>("Favorite").Content = preferences.Favorites.Contains(key) ? "★" : "☆";
        }

        void StepQuantity(int delta)
        {
            int number;
            if (!Int32.TryParse(C<TextBox>("Quantity").Text, out number)) number = 1;
            C<TextBox>("Quantity").Text = Math.Max(1, Math.Min(SelectedItem == null ? 1 : Math.Min(SelectedItem.Stack, 999), (long)number + delta)).ToString();
        }

        void SetGemKind()
        {
            C<ComboBox>("Shape").ItemsSource = Rune ? new[] { "Standard Caryll rune", "Oath rune" } : new[] { "Radial", "Triangle", "Waning", "Circle", "Droplet" };
            C<ComboBox>("Shape").SelectedIndex = 0;
            int[] sources = catalog.Gems.Where(x => (x.Flags & (Rune ? 2 : 1)) != 0).Select(x => x.Id).ToArray();
            C<ComboBox>("GemSource").ItemsSource = sources;
            C<ComboBox>("GemSource").SelectedItem = !Rune && sources.Contains(90150) ? 90150 : sources.First();
            C<TextBlock>("GemTitle").Text = Rune ? "Custom Caryll rune" : "Custom blood gem";
            C<Button>("GiveGem").Content = Rune ? "Create Caryll rune" : "Create blood gem";
            C<ComboBox>("RecipeList").ItemsSource = preferences.Recipes.ToArray();
            FilterEffects(); UpdateSlots();
        }

        int SelectedShape()
        {
            int selected = C<ComboBox>("Shape").SelectedIndex;
            var shapes = Rune ? new[] { 1, 2 } : new[] { 1, 2, 4, 8, 63 };
            return selected < 0 || selected >= shapes.Length ? -1 : shapes[selected];
        }

        void FilterEffects()
        {
            string search = C<TextBox>("EffectSearch").Text.Trim();
            var items = orderedEffects.Where(x => (x.Known || C<CheckBox>("AdvancedEffects").IsChecked == true) &&
                (!x.Known || x.Rune == Rune) && Search(x.Label, search)).ToArray();
            C<DataGrid>("EffectGrid").ItemsSource = items;
            C<TextBlock>("EffectCount").Text = items.Length.ToString("N0") + " effects";
            C<TextBlock>("NoEffects").Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void AssignEffect()
        {
            var effect = C<DataGrid>("EffectGrid").SelectedItem as GemEffect;
            int slot = C<ComboBox>("TargetSlot").SelectedIndex;
            if (effect == null || slot < 0) return;
            effects[slot] = effect.Id; UpdateSlots();
        }

        void UpdateSlots()
        {
            for (int i = 0; i < 6; i++)
            {
                var effect = catalog.Effects.FirstOrDefault(x => x.Id == effects[i]);
                slots[i].Text = (i + 1) + " · " + (effect == null ? (i == 0 ? "Choose a primary effect" : "Empty secondary slot") : effect.EffectText);
                slotButtons[i].ToolTip = effect == null ? "Select this slot, then assign an effect." : effect.Label;
                bool selected = C<ComboBox>("TargetSlot").SelectedIndex == i;
                slotButtons[i].Background = Color(selected ? "#3A2833" : "#24252A");
                slotButtons[i].BorderBrush = Color(selected ? "#AD737D" : "#3A363E");
            }
            if (!busy) Refresh();
        }

        void SaveRecipe()
        {
            try
            {
                int source = (int)C<ComboBox>("GemSource").SelectedItem;
                catalog.ValidateGem(Rune, SelectedShape(), source, effects);
                var primary = catalog.Effects.First(x => x.Id == effects[0]);
                string name = Prompt("Save your creation", primary.Name);
                if (name == null) return;
                if (String.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new InvalidOperationException("Use a recipe name between 1 and 80 characters.");
                var recipe = new Recipe { Name = name.Trim(), Rune = Rune, Shape = SelectedShape(), Source = source, Effects = (int[])effects.Clone() };
                preferences.Recipes.RemoveAll(x => String.Equals(x.Name, recipe.Name, StringComparison.OrdinalIgnoreCase));
                preferences.Recipes.Add(recipe); Persist();
                C<ComboBox>("RecipeList").ItemsSource = preferences.Recipes.ToArray();
                C<ComboBox>("RecipeList").SelectedItem = recipe;
                Notify("Recipe saved: " + recipe.Name);
            }
            catch (Exception ex) { Notify(ex.Message, true); }
        }

        void LoadRecipe()
        {
            var recipe = C<ComboBox>("RecipeList").SelectedItem as Recipe;
            if (recipe == null) { Notify("Choose a saved recipe first."); return; }
            try
            {
                catalog.ValidateGem(recipe.Rune, recipe.Shape, recipe.Source, recipe.Effects);
                C<ComboBox>("GemKind").SelectedIndex = recipe.Rune ? 1 : 0;
                C<ComboBox>("Shape").SelectedIndex = System.Array.IndexOf(recipe.Rune ? new[] { 1, 2 } : new[] { 1, 2, 4, 8, 63 }, recipe.Shape);
                C<ComboBox>("GemSource").SelectedItem = recipe.Source;
                System.Array.Copy(recipe.Effects, effects, 6); UpdateSlots();
                Notify("Loaded recipe: " + recipe.Name);
            }
            catch (Exception ex) { Notify("This recipe cannot be loaded: " + ex.Message, true); }
        }

        string Prompt(string title, string initial, string button = "Save recipe")
        {
            var dialog = new Window { Owner = window, Title = title, Width = 440, Height = 210, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Color("#1C1E22"), Foreground = Color("#E6E0D5"), Icon = window.Icon };
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = title, FontFamily = new FontFamily("Georgia"), FontSize = 22, Margin = new Thickness(0, 0, 0, 15) });
            var input = new TextBox { Text = initial, MaxLength = 80 }; panel.Children.Add(input);
            var save = new Button { Content = button, IsDefault = true, Margin = new Thickness(0, 15, 0, 0), Style = (Style)Resources["PrimaryButton"] };
            save.Click += (s, e) => dialog.DialogResult = true; panel.Children.Add(save);
            dialog.Content = panel; dialog.Loaded += (s, e) => { input.Focus(); input.SelectAll(); };
            return dialog.ShowDialog() == true ? input.Text : null;
        }

        void BrowsePort()
        {
            if (engine.Connected) { Notify("Disconnect before changing the shadPS4 folder."); return; }
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Select the shadPS4 folder (the one that contains shadPS4.exe)", SelectedPath = preferences.ShadDirectory ?? "", ShowNewFolderButton = false })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                try
                {
                    engine.SetFolder(dialog.SelectedPath);
                    preferences.ShadDirectory = engine.ShadPath; preferences.UserDirectory = engine.UserPath; Persist(); Refresh();
                    Notify("shadPS4 folder selected. You can now back up saves while the game is closed.");
                }
                catch (Exception ex) { Notify(ex.Message, true); }
            }
        }

        void Persist()
        {
            try { Storage.Save(preferences); }
            catch (Exception ex) { Notify("Preferences could not be saved: " + ex.Message, true); }
        }
        void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Notify(ex.Message, true); }
        }
        void OpenFolder(string path) { try { Directory.CreateDirectory(path); Open(path); } catch (Exception ex) { Notify(ex.Message, true); } }

        async Task RunOperation(string description, Func<Task<string>> operation)
        {
            if (busy) return;
            busy = true;
            C<Grid>("Workspace").IsEnabled = false;
            C<Button>("Connect").IsEnabled = false;
            C<ProgressBar>("BusyProgress").Visibility = Visibility.Visible;
            C<TextBlock>("Activity").Text = description;
            try { Notify(await Task.Run(operation)); }
            catch (Exception ex) { Notify(ex.Message, true); }
            finally
            {
                busy = false;
                C<Grid>("Workspace").IsEnabled = true;
                C<ProgressBar>("BusyProgress").Visibility = Visibility.Collapsed;
                FlushRecent();
                Refresh();
            }
        }

        void Notify(string message, bool error = false)
        {
            C<TextBlock>("Activity").Text = message;
            C<TextBlock>("Activity").Foreground = Color(error ? "#E7A09F" : "#C8BAB0");
            C<System.Windows.Shapes.Ellipse>("ActivityDot").Fill = Color(error ? "#CB7378" : "#AA8B86");
            history.Insert(0, new ActivityEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Message = message });
            while (history.Count > 40) history.RemoveAt(history.Count - 1);
            Storage.Log((error ? "Error: " : "") + message);
        }

        void Refresh()
        {
            var snapshot = engine.Inspect();
            C<TextBlock>("Status").Text = engine.Connected ? snapshot.Message : "Waiting for Bloodborne";
            C<System.Windows.Shapes.Ellipse>("StatusDot").Fill = Color(snapshot.Ready ? "#A1B28C" : engine.Connected ? "#C7AA80" : "#74676B");
            C<Button>("Connect").Content = engine.Connected ? "Disconnect" : "Connect to Bloodborne";
            C<Button>("Connect").IsEnabled = !busy;
            int quantity;
            var upgrade = C<ComboBox>("Upgrade").SelectedItem as UpgradeChoice;
            bool itemValid = SelectedItem != null && Int32.TryParse(C<TextBox>("Quantity").Text, out quantity);
            if (itemValid)
            {
                Int32.TryParse(C<TextBox>("Quantity").Text, out quantity);
                try { catalog.ValidateItem(SelectedItem, upgrade == null ? 0 : upgrade.Value, quantity); }
                catch (InvalidOperationException) { itemValid = false; }
            }
            C<Button>("GiveItem").IsEnabled = snapshot.Ready && !busy && itemValid;
            bool maxValid = false;
            if (SelectedItem != null)
            {
                try { catalog.ValidateItem(SelectedItem, upgrade == null ? 0 : upgrade.Value, Math.Min(SelectedItem.Stack, 999)); maxValid = true; }
                catch (InvalidOperationException) { }
            }
            C<Button>("GiveMax").IsEnabled = snapshot.Ready && !busy && maxValid;
            C<Button>("AddToKit").IsEnabled = SelectedItem != null && !busy;
            bool gemValid = effects[0] >= 0;
            C<Button>("GiveGem").IsEnabled = snapshot.Ready && !busy && gemValid;
            C<Button>("UseEffect").IsEnabled = C<DataGrid>("EffectGrid").SelectedItem != null;
            C<TextBox>("PortPath").Text = engine.ShadPath ?? "";
            C<TextBlock>("SavePath").Text = String.IsNullOrWhiteSpace(engine.UserPath) ? "Saves are found in shadPS4's user folder (portable \"user\" folder, or %APPDATA%\\shadPS4\\user)." : "Save directory: " + engine.UserPath;
            if (engine.LastBackup != null) C<TextBlock>("BackupStatus").Text = "Automatic snapshot verified for this character.";
            foreach (Button kit in kitButtons) kit.IsEnabled = snapshot.Ready && !busy;
            foreach (Button again in recentButtons) again.IsEnabled = snapshot.Ready && !busy;
        }
    }
}
