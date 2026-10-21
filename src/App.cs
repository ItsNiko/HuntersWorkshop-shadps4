// SPDX-License-Identifier: GPL-3.0-or-later
using System;
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
            C<TextBox>("ItemSearch").TextChanged += (s, e) => FilterItems();
            C<ComboBox>("Category").SelectionChanged += (s, e) => FilterItems();
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
            C<TextBox>("Quantity").TextChanged += (s, e) => { if (!busy) Refresh(); };
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
                await RunOperation("Granting your selected item…", () => engine.GiveItem(item, level, quantity));
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

        void Navigate(int target, bool favorites)
        {
            page = target; favoritesOnly = favorites;
            C<Grid>("ItemsPage").Visibility = target == 0 ? Visibility.Visible : Visibility.Collapsed;
            C<Grid>("GemsPage").Visibility = target == 1 ? Visibility.Visible : Visibility.Collapsed;
            C<Grid>("SessionPage").Visibility = target == 2 ? Visibility.Visible : Visibility.Collapsed;
            C<ToggleButton>("NavItems").IsChecked = target == 0 && !favorites;
            C<ToggleButton>("NavFavorites").IsChecked = target == 0 && favorites;
            C<ToggleButton>("NavGems").IsChecked = target == 1;
            C<ToggleButton>("NavSession").IsChecked = target == 2;
            C<TextBlock>("ItemsHeading").Text = favorites ? "Your favorites" : "Items & equipment";
            if (target == 0) FilterItems();
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
            var items = catalog.Items.Where(x =>
                (x.Known || C<CheckBox>("AdvancedItems").IsChecked == true) &&
                (!favoritesOnly || preferences.Favorites.Contains(ItemKey(x))) &&
                (category == "All categories" || category == x.Group) &&
                (variant == "All variants" || variant == x.Variant) &&
                (Search(x.Name, search) || x.Id.ToString() == search || x.DisplayId.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
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
            C<ComboBox>("Upgrade").ItemsSource = (item == null ? new[] { 0 } : item.Upgrades).Select(n => new UpgradeChoice { Value = n }).ToArray();
            C<ComboBox>("Upgrade").SelectedIndex = 0;
            C<ComboBox>("Upgrade").IsEnabled = item != null && item.Max > 0;
            C<TextBox>("Quantity").Text = "1";
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

        string Prompt(string title, string initial)
        {
            var dialog = new Window { Owner = window, Title = title, Width = 440, Height = 210, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Color("#1C1E22"), Foreground = Color("#E6E0D5"), Icon = window.Icon };
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = title, FontFamily = new FontFamily("Georgia"), FontSize = 22, Margin = new Thickness(0, 0, 0, 15) });
            var input = new TextBox { Text = initial, MaxLength = 80 }; panel.Children.Add(input);
            var save = new Button { Content = "Save recipe", IsDefault = true, Margin = new Thickness(0, 15, 0, 0), Style = (Style)Resources["PrimaryButton"] };
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
            bool gemValid = effects[0] >= 0;
            C<Button>("GiveGem").IsEnabled = snapshot.Ready && !busy && gemValid;
            C<Button>("UseEffect").IsEnabled = C<DataGrid>("EffectGrid").SelectedItem != null;
            C<TextBox>("PortPath").Text = engine.ShadPath ?? "";
            C<TextBlock>("SavePath").Text = String.IsNullOrWhiteSpace(engine.UserPath) ? "Saves are found in shadPS4's user folder (portable \"user\" folder, or %APPDATA%\\shadPS4\\user)." : "Save directory: " + engine.UserPath;
            if (engine.LastBackup != null) C<TextBlock>("BackupStatus").Text = "Automatic snapshot verified for this character.";
        }
    }
}
