using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TeletextRecoveReese.Core;

namespace TeletextRecoveReese;

public partial class MainWindow : Window
{
    private readonly PageStore _store = new();
    private readonly PageStore _squashStore = new();
    private readonly List<byte[]> _broadcastPackets = new();
    private readonly List<byte[]> _squashPackets = new();
    private readonly HashSet<int> _deletedSquashPacketIndices = new();
    private readonly bool _loadLastSession;

    // Guards against SelectionChanged handlers firing (and re-triggering each other)
    // while we're populating combo boxes programmatically.
    private bool _suppressComboEvents;

    private TeletextPage _squashPage = new();
    private string? _squashFilePath;
    private string? _broadcastFilePath;
    private bool _squashDirty;
    private bool _broadcastFileOpen;
    private bool _squashFileOpen;
    private bool _mosaicColorMode;
    private bool _structuralDirty;
    private bool _squashPaneEstablished;
    private bool _closeConfirmed;
    private bool _closeDialogOpen;

    private sealed class HexNumericConverter(int digits, bool packedSubpage = false) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string text = value?.ToString()?.Trim() ?? string.Empty;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text[2..];
            if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int displayed))
                return AvaloniaProperty.UnsetValue;
            if (packedSubpage && (displayed & ~0x3F7F) != 0)
                return AvaloniaProperty.UnsetValue;
            int raw = packedSubpage
                ? (displayed & 0x7F) | ((displayed >> 1) & 0x1F80)
                : displayed;
            return (decimal)raw;
        }

        // NumericUpDown's TextConverter intentionally uses Convert for text -> value
        // and ConvertBack for value -> text (the reverse of a normal display binding).
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not decimal numeric) return string.Empty;
            int raw = decimal.ToInt32(numeric);
            int displayed = packedSubpage
                ? (raw & 0x7F) | ((raw & 0x1F80) << 1)
                : raw;
            return displayed.ToString($"X{digits}", CultureInfo.InvariantCulture);
        }

        public static int PackSubpage(decimal logicalValue)
        {
            int raw = decimal.ToInt32(logicalValue);
            return (raw & 0x7F) | ((raw & 0x1F80) << 1);
        }
    }

    private sealed class PageSnapshot
    {
        public byte[]?[] Rows { get; } = new byte[25][];
        public List<(byte[] RawPacket, int PacketIndex)> EnhancementPackets { get; } = new();
    }

    private sealed class PageHistory
    {
        public List<PageSnapshot> States { get; } = new();
        public int Position { get; set; }
        public int SavedPosition { get; set; }
    }

    private sealed class EnhancementListEntry(string text, int designationCode, int tripletNumber)
        : INotifyPropertyChanged
    {
        private bool _isHoverRelated;

        public string Text { get; } = text;
        public int DesignationCode { get; } = designationCode;
        public int TripletNumber { get; } = tripletNumber;
        public IBrush? HighlightBackground => _isHoverRelated
            ? new SolidColorBrush(Color.Parse("#70409050"))
            : null;

        public bool IsHoverRelated
        {
            get => _isHoverRelated;
            set
            {
                if (_isHoverRelated == value) return;
                _isHoverRelated = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightBackground)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly Dictionary<TeletextPage, PageHistory> _pageHistories = new();
    private readonly List<EnhancementListEntry> _enhancementListEntries = new();
    private readonly Dictionary<(int DesignationCode, int TripletNumber), EnhancementListEntry>
        _enhancementEntriesByTriplet = new();
    private readonly HashSet<TeletextPage> _broadcastEnhancementsScanned = new();

    private sealed class SessionState
    {
        public string? BroadcastFilePath { get; set; }
        public string? SquashFilePath { get; set; }
        public int? BroadcastMagazine { get; set; }
        public int? BroadcastPage { get; set; }
        public int? BroadcastSubpage { get; set; }
        public int? BroadcastVersion { get; set; }
        public int? SquashMagazine { get; set; }
        public int? SquashPage { get; set; }
        public int? SquashSubpage { get; set; }
        public string? GridFontFamily { get; set; }
        public bool? ShowX26EnhancementsSidebar { get; set; }
        public bool? ShowControlCodes { get; set; }
        public bool? ShowSquashControlCodes { get; set; }
        public bool? ShowBroadcastControlCodes { get; set; }
        public bool? ShowSquashSelectionBytes { get; set; }
        public bool? ShowBroadcastSelectionBytes { get; set; }
        public bool? ShowSquashDiacritics { get; set; }
        public bool? ShowBroadcastDiacritics { get; set; }
        public bool? SuppressFlash { get; set; }
        public string? VideoEncoder { get; set; }
        public double? VideoSecondsPerPage { get; set; }
        public bool? VideoAnimateFlash { get; set; }
        public int? VideoResolutionIndex { get; set; }
        public int? VideoAspectIndex { get; set; }
        public bool? ShowVideoBookmarks { get; set; }
        public List<RecentFileEntry> RecentFiles { get; set; } = new();
    }

    private sealed class RecentFileEntry
    {
        public string Path { get; set; } = string.Empty;
        public bool BroadcastPane { get; set; }
        public int? Magazine { get; set; }
        public int? Page { get; set; }
        public int? Subpage { get; set; }
        public int? Version { get; set; }
        public List<VideoBookmarkEntry> VideoBookmarks { get; set; } = new();
        public bool PageBookmarksInitialized { get; set; }
    }

    private sealed class VideoBookmarkEntry
    {
        public int Magazine { get; set; }
        public int Page { get; set; }
        public int Subpage { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed record PageBookmarkListEntry(VideoBookmarkEntry Bookmark, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record FontChoice(string Name, FontFamily Family)
    {
        public override string ToString() => Name;
    }

    private sealed record VideoEncoderChoice(string Name, string Description)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(Description)
            ? Name
            : $"{Name} — {Description}";
    }

    private sealed class RuntimeFontCollection(Uri key) : FontCollectionBase
    {
        public override Uri Key { get; } = key;
    }

    private readonly string _sessionStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TeletextRecoveReese",
        "session.json");

    private SessionState _sessionState = new();
    private static readonly Uri MacInstalledFontCollectionKey = new("fonts:MacInstalled");
    private static readonly string[] PreferredGridFontNames =
        { "TIFAX", "TeleText", "Menlo", "DejaVu Sans Mono", "Consolas" };
    private readonly List<FontChoice> _installedFontFamilies = new();
    private readonly RuntimeFontCollection _macInstalledFontCollection = new(MacInstalledFontCollectionKey);
    private readonly HashSet<string> _loadedMacFontFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FontFamily> _loadedMacFontFamilies =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _activeGridFontFamily;
    private NativeMenuItem? _nativeUndoMenuItem;
    private NativeMenuItem? _nativeRedoMenuItem;
    private NativeMenuItem? _nativeX26EnhancementsMenuItem;
    private NativeMenuItem? _nativeVideoBookmarksMenuItem;
    private NativeMenuItem? _nativeSuppressFlashMenuItem;
    private NativeMenuItem? _nativeExportVideoMenuItem;
    private NativeMenuItem? _nativeOpenRecentMenuItem;
    private readonly string? _ffmpegPath;
    private bool _showX26EnhancementsSidebar = true;
    private bool _showVideoBookmarks = true;
    private bool _updatingVideoBookmarkText;
    private bool _updatingPageBookmarkList;
    private bool _pageBookmarkNavigationPending;
    private readonly DispatcherTimer _flashTimer;
    private bool _flashPhaseVisible = true;
    private int _fitWindowRequest;

    private static readonly DataFormat<byte[]> TeletextClipboardFormat =
        DataFormat.CreateBytesApplicationFormat("com.teletextrecovereese.raw-byte-block.v2");

    public MainWindow() : this(false)
    {
    }

    public MainWindow(bool loadLastSession)
    {
        _loadLastSession = loadLastSession;
        InitializeComponent();
        Title = AppVersion.DisplayName;
        if (OperatingSystem.IsMacOS())
        {
            WindowMenu.IsVisible = false;
        }
        else
        {
            WindowMenu.IsVisible = true;
            ClearValue(NativeMenu.MenuProperty);
        }
        InitializeNativeMenuReferences();
        _ffmpegPath = FindFfmpegExecutable();
        ExportVideoMenuItem.IsEnabled = _ffmpegPath is not null;
        if (_nativeExportVideoMenuItem is not null)
            _nativeExportVideoMenuItem.IsEnabled = _ffmpegPath is not null;
        LoadSessionState();
        _showVideoBookmarks = _sessionState.ShowVideoBookmarks ?? true;
        RebuildOpenRecentMenus();
        ApplyToggleSessionState();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashPhaseVisible = !_flashPhaseVisible;
            SquashGrid.FlashPhaseVisible = _flashPhaseVisible;
            BroadcastGrid.FlashPhaseVisible = _flashPhaseVisible;
        };
        _flashTimer.Start();
        SetX26EnhancementsSidebarVisibility(
            _sessionState.ShowX26EnhancementsSidebar ?? true,
            resizeWindow: false);
        InitializeTransferButtons();
        SquashGrid.IsActive = true;
        BroadcastGrid.IsActive = false;
        BroadcastGrid.ClearSelection();

        // The editor always starts with a real, saveable blank 100/0000 page.
        // Only the broadcast pane and row-transfer controls stay hidden until a
        // full capture is opened (or restored with -loadlast).
        if (BroadcastPaneGrid != null) BroadcastPaneGrid.IsVisible = false;
        if (TransferPaneGrid != null) TransferPaneGrid.IsVisible = false;
        ApplyX26EnhancementsSidebarVisibility(resizeWindow: false);
        ApplyVideoBookmarkSidebarVisibility(resizeWindow: false);
        SquashGrid.CellSelected += OnSquashGridCellSelected;
        SquashGrid.DiacriticMoveRequested += OnDiacriticMoveRequested;
        SquashGrid.EnhancementHoverChanged += OnEnhancementHoverChanged;
        BroadcastGrid.CellSelected += OnBroadcastGridCellSelected;
        InitializeBlankSquashDocument();
        // Handle keyboard navigation in the tunnel phase, before the menu can
        // consume the cursor keys for its own focus navigation.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        Closed += (_, _) => _flashTimer.Stop();
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        Opened -= OnWindowOpened;
        InitializeInstalledFonts();
        ApplyGridFont(_sessionState.GridFontFamily, persist: false);
        if (_loadLastSession)
            await RestoreSessionFilesAsync();
    }

    private void InitializeInstalledFonts()
    {
        _installedFontFamilies.Clear();
        _installedFontFamilies.AddRange(FontManager.Current.SystemFonts
            .Where(font => !string.IsNullOrWhiteSpace(font.Name))
            .GroupBy(font => font.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FontChoice(group.Key, group.First()))
            .Where(choice => CanResolveSystemFont(choice.Family)));

        if (OperatingSystem.IsMacOS())
            LoadMacFontsMissingFromSystemCatalog();

        _installedFontFamilies.Sort((left, right) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
    }

    private static bool CanResolveSystemFont(FontFamily family)
    {
        try
        {
            return FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out _);
        }
        catch
        {
            return false;
        }
    }

    private void LoadMacFontsMissingFromSystemCatalog()
    {
        foreach ((string name, FontFamily family) in _loadedMacFontFamilies)
        {
            if (!_installedFontFamilies.Any(choice =>
                    string.Equals(choice.Name, name, StringComparison.OrdinalIgnoreCase)))
                _installedFontFamilies.Add(new FontChoice(name, family));
        }

        string userFonts = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Fonts");

        bool addedTypeface = false;
        foreach (string directory in new[] { userFonts, "/Library/Fonts" })
        {
            if (!Directory.Exists(directory)) continue;

            IEnumerable<string> fontFiles;
            try
            {
                fontFiles = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (string path in fontFiles)
            {
                string extension = Path.GetExtension(path);
                if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!_loadedMacFontFiles.Add(path)) continue;

                try
                {
                    using FileStream stream = File.OpenRead(path);
                    if (!_macInstalledFontCollection.TryAddGlyphTypeface(stream, out GlyphTypeface? glyphTypeface))
                        continue;

                    addedTypeface = true;
                    IEnumerable<string?> names = new[]
                        {
                            glyphTypeface.FamilyName,
                            glyphTypeface.TypographicFamilyName,
                        }
                        .Concat(glyphTypeface.FamilyNames.Values);

                    foreach (string name in names
                                 .Where(name => !string.IsNullOrWhiteSpace(name))
                                 .Select(name => name!)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (_installedFontFamilies.Any(choice =>
                                string.Equals(choice.Name, name, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var family = new FontFamily($"{MacInstalledFontCollectionKey}#{name}");
                        _loadedMacFontFamilies[name] = family;
                        _installedFontFamilies.Add(new FontChoice(name, family));
                    }
                }
                catch
                {
                    // A malformed or unsupported font must not prevent the picker from opening.
                }
            }
        }

        if (addedTypeface)
            FontManager.Current.AddFontCollection(_macInstalledFontCollection);
    }

    private async void OnChooseFontClicked(object? sender, RoutedEventArgs e)
    {
        InitializeInstalledFonts();

        FontChoice? choice = await ShowFontPickerAsync();
        if (choice is null) return;

        ApplyGridFont(choice.Name, persist: true);
        try
        {
            await SaveSessionStateAsync();
        }
        catch { }
    }

    private async Task<FontChoice?> ShowFontPickerAsync()
    {
        var searchBox = new TextBox
        {
            PlaceholderText = "Search fonts...",
            Margin = new Thickness(0, 0, 0, 8),
        };
        var fontList = new ListBox
        {
            ItemsSource = _installedFontFamilies,
            MinHeight = 220,
        };
        var selectedName = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var preview = new TextBlock
        {
            Text = "TELETEXT PREVIEW\nABCDEFGHIJKLMNOPQRSTUVWXYZ\nabcdefghijklmnopqrstuvwxyz\n0123456789  ! ? . , : ;  ČĆŽŠĐ čćžšđ",
            FontSize = 24,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
        };
        var previewBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#181818")),
            BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 10, 0, 12),
            MinHeight = 150,
            Child = new StackPanel
            {
                Children = { selectedName, preview },
            },
        };
        var okButton = new Button
        {
            Content = "OK",
            Width = 90,
            IsDefault = true,
            IsEnabled = false,
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 90,
            IsCancel = true,
        };
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, okButton },
        };
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(14),
        };
        content.Children.Add(searchBox);
        Grid.SetRow(fontList, 1);
        content.Children.Add(fontList);
        Grid.SetRow(previewBorder, 2);
        content.Children.Add(previewBorder);
        Grid.SetRow(buttonPanel, 3);
        content.Children.Add(buttonPanel);

        var dialog = new Window
        {
            Title = "Choose grid font",
            Width = 620,
            Height = 610,
            MinWidth = 480,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#242424")),
            Content = content,
        };

        void UpdatePreview()
        {
            if (fontList.SelectedItem is not FontChoice chosen) return;
            selectedName.Text = chosen.Name;
            preview.FontFamily = chosen.Family;
            okButton.IsEnabled = true;
        }

        fontList.SelectionChanged += (_, _) => UpdatePreview();
        searchBox.TextChanged += (_, _) =>
        {
            string query = searchBox.Text?.Trim() ?? string.Empty;
            fontList.ItemsSource = string.IsNullOrEmpty(query)
                ? _installedFontFamilies
                : _installedFontFamilies.Where(choice =>
                    choice.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        };
        okButton.Click += (_, _) => dialog.Close(fontList.SelectedItem as FontChoice);
        cancelButton.Click += (_, _) => dialog.Close(null);

        string? initial = _activeGridFontFamily;
        if (initial is not null)
            fontList.SelectedItem = _installedFontFamilies.FirstOrDefault(choice =>
                string.Equals(choice.Name, initial, StringComparison.OrdinalIgnoreCase));
        if (fontList.SelectedItem is null && _installedFontFamilies.Count > 0)
            fontList.SelectedIndex = 0;

        return await dialog.ShowDialog<FontChoice?>(this);
    }

    private void ApplyGridFont(string? requestedFamilyName, bool persist)
    {
        FontChoice? choice = _installedFontFamilies.FirstOrDefault(installedFont =>
            string.Equals(installedFont.Name, requestedFamilyName, StringComparison.OrdinalIgnoreCase));
        choice ??= PreferredGridFontNames
            .Select(preferredName => _installedFontFamilies.FirstOrDefault(installedFont =>
                string.Equals(installedFont.Name, preferredName, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(installedFont => installedFont is not null);
        choice ??= _installedFontFamilies.FirstOrDefault(installedFont =>
            string.Equals(
                installedFont.Name,
                FontManager.Current.DefaultFontFamily.Name,
                StringComparison.OrdinalIgnoreCase));
        choice ??= _installedFontFamilies.FirstOrDefault();
        if (choice is null) return;

        SquashGrid.SetFontFamily(choice.Family, choice.Name);
        BroadcastGrid.SetFontFamily(choice.Family, choice.Name);
        _activeGridFontFamily = choice.Name;

        if (persist || !string.Equals(
                requestedFamilyName,
                choice.Name,
                StringComparison.OrdinalIgnoreCase))
            _sessionState.GridFontFamily = choice.Name;
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        CaptureSessionSelection();
        SaveSessionState();
        if (_closeConfirmed || !_squashDirty) return;

        e.Cancel = true;
        if (_closeDialogOpen) return;

        _closeDialogOpen = true;
        bool closeWithoutSaving = await ConfirmCloseWithoutSavingAsync();
        _closeDialogOpen = false;

        if (closeWithoutSaving)
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private async Task<bool> ConfirmCloseWithoutSavingAsync()
    {
        bool confirmed = false;
        var closeButton = new Button
        {
            Content = "Close without saving",
            Width = 155,
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 90,
            IsDefault = true,
            IsCancel = true,
        };
        var dialog = new Window
        {
            Title = "Unsaved pages",
            Width = 470,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = "The edited pages have not been saved. Close the application and discard these changes?",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, closeButton },
                    }
                }
            }
        };
        closeButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancelButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return confirmed;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (VideoBookmarkTextBox.IsKeyboardFocusWithin)
            return;

        var activeGrid = IsActiveGrid();
        if (activeGrid == null) return;

        bool commandModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (commandModifier && e.Key == Key.O)
        {
            e.Handled = true;
            await OpenSquashFileAsync();
            return;
        }

        if (commandModifier && e.Key == Key.S)
        {
            e.Handled = true;
            await SaveSquashAsync(forcePicker: false);
            return;
        }

        if (commandModifier && e.Key == Key.N)
        {
            e.Handled = true;
            await CreateNewPageAsync();
            return;
        }

        if (commandModifier && e.Key == Key.C)
        {
            e.Handled = true;
            byte[]? copiedBlock = await CopySelectionAsync(activeGrid);
            if (activeGrid == BroadcastGrid
                && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                && copiedBlock is not null)
            {
                PasteByteBlockIntoSquash(
                    copiedBlock,
                    BroadcastGrid.SelectedColumn,
                    BroadcastGrid.SelectedRow,
                    updateSquashSelection: false);
            }
            return;
        }

        if (commandModifier && e.Key == Key.V)
        {
            e.Handled = true;
            if (activeGrid == SquashGrid)
            {
                await PasteSelectionAsync();
            }
            else
            {
                PlaySystemErrorSound();
                await BroadcastGrid.FlashReadOnlyWarningAsync();
            }
            return;
        }

        if (commandModifier && e.Key == Key.Z)
        {
            e.Handled = true;
            if (activeGrid == SquashGrid)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) RedoCurrentPage();
                else UndoCurrentPage();
            }
            else
            {
                PlaySystemErrorSound();
                await BroadcastGrid.FlashReadOnlyWarningAsync();
            }
            return;
        }

        // Do not turn unhandled Ctrl/Cmd shortcuts into printable characters.
        if (commandModifier) return;

        int deltaX = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
        int deltaY = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
        if (deltaX != 0 || deltaY != 0)
        {
            int column = Math.Clamp(activeGrid.SelectedColumn + deltaX, 0, 39);
            int row = Math.Clamp(activeGrid.SelectedRow + deltaY, 0, 24);
            activeGrid.SetSelectionSize(1, 1);
            activeGrid.MoveSelectionTo(column, row);
            e.Handled = true;
            return;
        }

        if (activeGrid == BroadcastGrid && IsEditingKey(e))
        {
            e.Handled = true;
            PlaySystemErrorSound();
            _ = BroadcastGrid.FlashReadOnlyWarningAsync();
            return;
        }

        if (activeGrid != SquashGrid) return;

        if (e.Key == Key.Back)
        {
            e.Handled = true;
            DeletePreviousCell();
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            int nextRow = Math.Min(activeGrid.SelectedRow + 1, 24);
            activeGrid.SetSelectionSize(1, 1);
            activeGrid.MoveSelectionTo(0, nextRow);
            return;
        }

        // A precomposed Unicode letter is stored as its plain G0 base character plus
        // a Level 1.5 X/26 diacritical enhancement. Ordinary printable G0 input keeps
        // the existing direct Level-1 path.
        bool isLevel15Diacritic = TryGetLevel15Diacritic(e, out char actualChar, out int diacritical);
        if (!isLevel15Diacritic && !TryGetTeletextCharacter(e, out actualChar)) return;
        e.Handled = true;

        int x = activeGrid.SelectedColumn;  // horizontal (0-39)
        int y = activeGrid.SelectedRow;     // vertical (0-24)

        // Write, resize selection to 1x1, advance
        if (x >= 0 && x < 40 && y >= 0 && y < 25)
        {
            var page = activeGrid.Page;
            if (page != null)
            {
                EnsurePageHistory(page);
                // Mosaic toggle if cell is mosaic: only Q/A/Z/W/S/X
                if (IsMosaicModeBeforeCell(page, x, y))
                {
                    char c = char.ToUpperInvariant(actualChar);
                    int bit = c == 'Q' ? 0 : c == 'A' ? 2 : c == 'Z' ? 4 : c == 'W' ? 1 : c == 'S' ? 3 : c == 'X' ? 5 : -1;
                    if (bit < 0) return;
                    int payloadIndex = (y == 0) ? (2 + x) : (2 + x);
                    byte oldPattern = page.Grid[x, y].MosaicPattern;
                    byte newPattern = (byte)(oldPattern ^ (1 << bit));
                    if (!(y == 0 && x < 8) && payloadIndex >= 2 && payloadIndex < 42)
                    {
                        byte[] raw = page.RawRows[y] is { } existing
                            ? (byte[])existing.Clone()
                            : CreateBlankPacket(page, y);
                        byte mosaicCode = newPattern < 32
                            ? (byte)(0x20 + newPattern)
                            : (byte)(0x60 + (newPattern - 32));
                        raw[payloadIndex] = WithOddParity(mosaicCode);
                        PageAssembler.ApplyRow(page, y, raw);
                    }
                    var cell = page.Grid[x, y];
                    cell.IsMosaic = true; cell.MosaicPattern = newPattern; cell.Character = ' ';
                    page.Grid[x, y] = cell;
                    PageAssembler.ApplyLevel15Enhancements(page);
                    CommitPageEdit(page);
                    activeGrid.InvalidateVisual();
                    return;
                }

                if (isLevel15Diacritic
                    && !PageAssembler.TrySetLevel15Diacritic(
                        page, x, y, actualChar, diacritical, out string enhancementError))
                {
                    PlaySystemErrorSound();
                    await ShowMessageAsync("Cannot add diacritic", enhancementError);
                    return;
                }

                page.Grid[x, y].Character = actualChar;

                // Update raw packet correctly: header (row 0) payload starts at grid col 8
                if (!(y == 0 && x < 8))
                {
                    byte[] raw = page.RawRows[y] is { } existing
                        ? (byte[])existing.Clone()
                        : CreateBlankPacket(page, y);
                    int payloadIndex = (y == 0) ? (2 + x) : (2 + x);
                    if (payloadIndex >= 2 && payloadIndex < 42)
                    {
                        byte code = actualChar == ' ' ? (byte)0x20 : (byte)actualChar;
                        raw[payloadIndex] = WithOddParity(code);
                    }
                    PageAssembler.ApplyRow(page, y, raw);
                }

                CommitPageEdit(page);
                if (isLevel15Diacritic)
                {
                    UpdateEnhancementList(page);
                    _ = activeGrid.FlashDiacriticConfirmationAsync(x, y);
                }
                activeGrid.InvalidateVisual();
            }
        }

        // Resize selection to single cell at new position
        activeGrid.SetSelectionSize(1, 1);

        // Advance horizontally (wrap at end of line)
        x++;
        if (x >= 40)
        {
            x = 0;
            y++;
        }

        // Stop at bottom-right corner (past last cell)
        if (y >= 25) return;

        activeGrid.MoveSelectionTo(x, y);
    }

    private async Task<byte[]?> CopySelectionAsync(TeletextGridControl grid)
    {
        if (grid.Page is not { } page || Clipboard is null) return null;

        int startX = Math.Clamp(grid.SelectedColumn, 0, 39);
        int startY = Math.Clamp(grid.SelectedRow, 0, 24);
        int width = Math.Min(Math.Max(grid.SelectionWidth, 1), 40 - startX);
        int height = Math.Min(Math.Max(grid.SelectionHeight, 1), 25 - startY);
        // Binary layout: "T42", version, width, height, then for every selected
        // cell a presence byte followed by the exact captured payload byte.
        var block = new byte[6 + width * height * 2];
        block[0] = (byte)'T';
        block[1] = (byte)'4';
        block[2] = (byte)'2';
        block[3] = 2;
        block[4] = (byte)width;
        block[5] = (byte)height;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                int sourceX = startX + x;
                int sourceY = startY + y;
                int binaryIndex = 6 + index * 2;

                // In row 0, columns 0-7 are page metadata rather than display bytes.
                if (!(sourceY == 0 && sourceX < 8))
                {
                    byte[] raw = page.RawRows[sourceY]
                        ?? CreateBlankPacket(page, sourceY);
                    block[binaryIndex] = 1;
                    block[binaryIndex + 1] = raw[2 + sourceX];
                }
            }
        }

        var item = DataTransferItem.Create(TeletextClipboardFormat, block);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await Clipboard.SetDataAsync(transfer);
        return block;
    }

    private async Task PasteSelectionAsync()
    {
        if (SquashGrid.Page is null || Clipboard is null) return;

        byte[]? block = await Clipboard.TryGetValueAsync(TeletextClipboardFormat);
        if (block is null || block.Length < 6
            || block[0] != (byte)'T' || block[1] != (byte)'4' || block[2] != (byte)'2'
            || block[3] != 2)
            return;

        int startX = Math.Clamp(SquashGrid.SelectedColumn, 0, 39);
        int startY = Math.Clamp(SquashGrid.SelectedRow, 0, 24);
        PasteByteBlockIntoSquash(block, startX, startY, updateSquashSelection: true);
    }

    private void PasteByteBlockIntoSquash(
        byte[] block,
        int startX,
        int startY,
        bool updateSquashSelection)
    {
        if (SquashGrid.Page is not { } page || block.Length < 6
            || block[0] != (byte)'T' || block[1] != (byte)'4' || block[2] != (byte)'2'
            || block[3] != 2)
            return;

        int blockWidth = block[4];
        int blockHeight = block[5];
        if (blockWidth <= 0 || blockHeight <= 0
            || block.Length < 6 + blockWidth * blockHeight * 2)
            return;

        startX = Math.Clamp(startX, 0, 39);
        startY = Math.Clamp(startY, 0, 24);
        int pasteWidth = Math.Min(blockWidth, 40 - startX);
        int pasteHeight = Math.Min(blockHeight, 25 - startY);
        EnsurePageHistory(page);

        for (int y = 0; y < pasteHeight; y++)
        {
            int targetY = startY + y;
            byte[] raw = page.RawRows[targetY] is { } existing
                ? (byte[])existing.Clone()
                : CreateBlankPacket(page, targetY);

            for (int x = 0; x < pasteWidth; x++)
            {
                int sourceIndex = y * blockWidth + x;
                int binaryIndex = 6 + sourceIndex * 2;
                int targetX = startX + x;

                // Header columns 0-7 contain page metadata, not display cells, so a
                // rectangular content paste deliberately leaves them untouched.
                if (!(targetY == 0 && targetX < 8)
                    && block[binaryIndex] != 0)
                {
                    raw[2 + targetX] = block[binaryIndex + 1];
                }
            }

            // Decode from the start of the complete target row. Attribute state is
            // therefore determined only by actual control bytes present before/in
            // the pasted block, exactly as it will be when exported and re-opened.
            PageAssembler.ApplyRow(page, targetY, raw);
        }

        if (updateSquashSelection)
            SquashGrid.SetSelectionSize(pasteWidth, pasteHeight);
        CommitPageEdit(page);
        SquashGrid.InvalidateVisual();
    }

    private static bool TryGetTeletextCharacter(KeyEventArgs e, out char character)
    {
        character = default;
        // Avalonia/Linux does not consistently populate KeySymbol for Space when a
        // Button owns keyboard focus. Recognize it explicitly so the tunnel handler
        // consumes it as editor input before Button can interpret it as another click.
        if (e.Key == Key.Space)
        {
            character = ' ';
            return true;
        }

        // X11 can also omit KeySymbol for shifted number-row punctuation while
        // still reporting the physical key and modifiers correctly.
        if (e.Key == Key.D1 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            character = '!';
            return true;
        }

        string? symbol = e.KeySymbol;
        if (string.IsNullOrEmpty(symbol) || symbol.Length != 1) return false;

        char candidate = symbol[0];
        if (candidate is < '\x20' or > '\x7F') return false;

        character = candidate;
        return true;
    }

    private static bool IsMosaicModeBeforeCell(TeletextPage page, int column, int row)
    {
        if (column is < 0 or >= 40 || row is < 0 or >= 25
            || page.RawRows[row] is not { Length: 42 } raw)
            return page.Grid[column, row].IsMosaic;

        bool mosaicMode = false;
        int firstColumn = row == 0 ? 8 : 0;
        for (int currentColumn = firstColumn; currentColumn < column; currentColumn++)
        {
            byte code = (byte)(raw[2 + currentColumn] & 0x7F);
            if (code is <= 0x07)
                mosaicMode = false;
            else if (code is >= 0x10 and <= 0x17)
                mosaicMode = true;
        }

        return mosaicMode;
    }

    private static bool TryGetLevel15Diacritic(
        KeyEventArgs e,
        out char baseCharacter,
        out int diacritical)
    {
        baseCharacter = default;
        diacritical = -1;
        string? symbol = e.KeySymbol;
        if (string.IsNullOrEmpty(symbol)) return false;

        if (symbol is "Đ" or "đ")
        {
            bool upperCase = symbol == "Đ";
            baseCharacter = upperCase ? 'D' : 'd';
            diacritical = upperCase ? 16 : 17;
            return true;
        }

        string decomposed = symbol.Normalize(NormalizationForm.FormD);
        if (decomposed.Length != 2 || decomposed[0] is < '\x20' or > '\x7F')
            return false;

        diacritical = decomposed[1] switch
        {
            '\u0300' => 1,
            '\u0301' => 2,
            '\u0302' => 3,
            '\u0303' => 4,
            '\u0304' => 5,
            '\u0306' => 6,
            '\u0307' => 7,
            '\u0308' => 8,
            '\u0323' => 9,
            '\u030A' => 10,
            '\u0327' => 11,
            '\u0332' => 12,
            '\u030B' => 13,
            '\u0328' => 14,
            '\u030C' => 15,
            _ => -1,
        };
        if (diacritical < 0) return false;

        baseCharacter = decomposed[0];
        return true;
    }

    private static bool IsEditingKey(KeyEventArgs e) =>
        TryGetTeletextCharacter(e, out _)
        || TryGetLevel15Diacritic(e, out _, out _)
        || e.Key is Key.Back or Key.Delete or Key.Enter;

    private void DeletePreviousCell()
    {
        if (SquashGrid.Page is not { } page) return;

        int x = SquashGrid.SelectedColumn;
        int y = SquashGrid.SelectedRow;
        if (x > 0)
        {
            x--;
        }
        else if (y > 0)
        {
            y--;
            x = 39;
        }
        else
        {
            return;
        }

        if (y == 0 && x < 8)
        {
            SquashGrid.MoveSelectionTo(8, 0);
            return;
        }

        EnsurePageHistory(page);
        byte[] raw = page.RawRows[y] is { } existing
            ? (byte[])existing.Clone()
            : CreateBlankPacket(page, y);
        raw[2 + x] = WithOddParity(0x20);
        PageAssembler.ApplyRow(page, y, raw);
        CommitPageEdit(page);
        SquashGrid.SetSelectionSize(1, 1);
        SquashGrid.MoveSelectionTo(x, y);
        SquashGrid.InvalidateVisual();
    }

    private static void PlaySystemErrorSound()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                MessageBeep(0x00000010); // MB_ICONHAND / system error sound
                return;
            }

            if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/canberra-gtk-play"))
            {
                var sound = new ProcessStartInfo
                {
                    FileName = "/usr/bin/canberra-gtk-play",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                sound.ArgumentList.Add("--id=dialog-error");
                Process.Start(sound);
                return;
            }

            Console.Write('\a');
        }
        catch
        {
            // Visual feedback still works on systems where no desktop bell exists.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    private static byte WithOddParity(byte sevenBitValue)
    {
        byte value = (byte)(sevenBitValue & 0x7F);
        int setBits = 0;
        for (int bit = 0; bit < 7; bit++)
            setBits += (value >> bit) & 1;

        return setBits % 2 == 0 ? (byte)(value | 0x80) : value;
    }

    private TeletextGridControl? IsActiveGrid()
    {
        if (SquashGrid.IsActive) return SquashGrid;
        if (BroadcastGrid.IsActive) return BroadcastGrid;
        return null;
    }

    private void OnSquashGridCellSelected(object? sender, EventArgs e)
    {
        SquashGrid.IsActive = true;
        BroadcastGrid.IsActive = false;
        BroadcastGrid.ClearSelection();
        UpdateCellAwareToolbar();
        UpdateVideoBookmarkUi();
    }

    private void OnBroadcastGridCellSelected(object? sender, EventArgs e)
    {
        BroadcastGrid.IsActive = true;
        SquashGrid.IsActive = false;
        SquashGrid.ClearSelection();
        UpdateVideoBookmarkUi();
    }

    private async void OnDiacriticMoveRequested(object? sender, DiacriticMoveRequestedEventArgs e)
    {
        if (sender != SquashGrid || SquashGrid.Page is not { } page) return;
        EnsurePageHistory(page);

        if (!PageAssembler.TryMoveLevel15Diacritic(
                page,
                e.DesignationCode,
                e.TripletNumber,
                e.TargetColumn,
                e.TargetRow,
                out string error))
        {
            PlaySystemErrorSound();
            await ShowMessageAsync("Cannot move diacritic", error);
            return;
        }

        CommitPageEdit(page);
        UpdateEnhancementList(page);
        SquashGrid.InvalidateVisual();
    }

    private void InitializeTransferButtons()
    {
        if (TransferButtonsGrid == null) return;

        for (int i = 0; i < 25; i++)
        {
            var btn = new Button
            {
                Content = "◀",
                Width = 32,
                Height = 23,
                FontSize = 10,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = i,
                Margin = new Thickness(0, 0, 0, 1)
            };
            btn.Click += OnTransferRowClicked;
            btn.PointerEntered += OnTransferRowPointerEntered;
            btn.PointerExited += OnTransferRowPointerExited;
            TransferButtonsGrid.Children.Add(btn);
        }
    }

    private void OnTransferRowPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Button { Tag: int row }) return;
        SquashGrid.SetTransferRowHighlight(row);
        BroadcastGrid.SetTransferRowHighlight(row);
    }

    private void OnTransferRowPointerExited(object? sender, PointerEventArgs e)
    {
        SquashGrid.SetTransferRowHighlight(-1);
        BroadcastGrid.SetTransferRowHighlight(-1);
    }

    /// <summary>
    /// Opens the About window as a modal dialog tied to the main window.
    /// </summary>
    private void AboutButton_Click(object? sender, RoutedEventArgs e)
    {
        var aboutWindow = new AboutWindow();
        aboutWindow.ShowDialog(this);
    }

    private void OnX26EnhancementsSidebarClicked(object? sender, RoutedEventArgs e)
    {
        bool isVisible = X26EnhancementsMenuItem.IsChecked;
        SetX26EnhancementsSidebarVisibility(isVisible, resizeWindow: true);
        _sessionState.ShowX26EnhancementsSidebar = isVisible;
        SaveSessionState();
    }

    private void OnNativeX26EnhancementsSidebarClicked(object? sender, EventArgs e)
    {
        // The macOS native menu exporter updates IsChecked at a different point in
        // the click cycle than the managed Menu control. Toggle from the actual
        // saved preference so hiding the entire left pane does not change it.
        bool isVisible = !_showX26EnhancementsSidebar;
        SetX26EnhancementsSidebarVisibility(isVisible, resizeWindow: true);
        _sessionState.ShowX26EnhancementsSidebar = isVisible;
        SaveSessionState();
    }

    private void OnSuppressFlashClicked(object? sender, RoutedEventArgs e)
    {
        SetSuppressFlash(SuppressFlashMenuItem.IsChecked);
    }

    private void OnNativeSuppressFlashClicked(object? sender, EventArgs e)
    {
        bool suppress = !SquashGrid.SuppressFlash;
        SetSuppressFlash(suppress);

        // Cocoa updates the exported checkbox at a different point in the native
        // click cycle. Re-assert the model value after that cycle has completed.
        Dispatcher.UIThread.Post(() =>
        {
            if (_nativeSuppressFlashMenuItem is not null)
                _nativeSuppressFlashMenuItem.IsChecked = suppress;
        }, DispatcherPriority.Background);
    }

    private void SetSuppressFlash(bool suppress)
    {
        SquashGrid.SuppressFlash = suppress;
        BroadcastGrid.SuppressFlash = suppress;
        SuppressFlashMenuItem.IsChecked = suppress;
        if (_nativeSuppressFlashMenuItem is not null)
            _nativeSuppressFlashMenuItem.IsChecked = suppress;
        _sessionState.SuppressFlash = suppress;
        SaveSessionState();
    }

    private void OnNativeOpenSquashedClicked(object? sender, EventArgs e) =>
        OnOpenSquashedClicked(sender, new RoutedEventArgs());

    private void OnNativeOpenClicked(object? sender, EventArgs e) =>
        OnOpenClicked(sender, new RoutedEventArgs());

    private void OnNativeSaveClicked(object? sender, EventArgs e) =>
        OnSaveClicked(sender, new RoutedEventArgs());

    private void OnNativeSaveAsClicked(object? sender, EventArgs e) =>
        OnSaveAsClicked(sender, new RoutedEventArgs());

    private void OnNativeExportScreenshotClicked(object? sender, EventArgs e) =>
        OnExportScreenshotClicked(sender, new RoutedEventArgs());

    private void OnNativeBatchExportScreenshotsClicked(object? sender, EventArgs e) =>
        OnBatchExportScreenshotsClicked(sender, new RoutedEventArgs());

    private void OnNativeExportVideoClicked(object? sender, EventArgs e) =>
        OnExportVideoClicked(sender, new RoutedEventArgs());

    private void OnNativeExitClicked(object? sender, EventArgs e) =>
        OnExitClicked(sender, new RoutedEventArgs());

    private void OnNativeUndoClicked(object? sender, EventArgs e) =>
        OnUndoClicked(sender, new RoutedEventArgs());

    private void OnNativeRedoClicked(object? sender, EventArgs e) =>
        OnRedoClicked(sender, new RoutedEventArgs());

    private void OnNativeInsertControlCodeClicked(object? sender, EventArgs e)
    {
        if (sender is NativeMenuItem item
            && int.TryParse(item.CommandParameter?.ToString(), out int code)
            && code is >= 0 and <= 0x1F)
            InsertControlCodeAtSelection((byte)code);
    }

    private void OnNativeNewPageClicked(object? sender, EventArgs e) =>
        OnNewPageClicked(sender, new RoutedEventArgs());

    private void OnNativeDeletePageClicked(object? sender, EventArgs e) =>
        OnDeletePageClicked(sender, new RoutedEventArgs());

    private void OnNativeG0SubsetClicked(object? sender, EventArgs e)
    {
        if (sender is NativeMenuItem item)
            ApplyG0SubsetSelection(item.CommandParameter?.ToString());
    }

    private void OnG0SubsetClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item)
            ApplyG0SubsetSelection(item.CommandParameter?.ToString());
    }

    private void ApplyG0SubsetSelection(string? selection)
    {
        TeletextGridControl? grid = IsActiveGrid();
        if (grid?.Page is not { } page) return;

        int? nationalOptionOverride;
        if (string.Equals(selection, "auto", StringComparison.OrdinalIgnoreCase))
            nationalOptionOverride = null;
        else if (string.Equals(selection, "default", StringComparison.OrdinalIgnoreCase))
            nationalOptionOverride = -1;
        else if (int.TryParse(selection, out int option) && option is >= 0 and <= 6)
            nationalOptionOverride = option;
        else
            return;

        PageAssembler.SetNationalOptionOverride(page, nationalOptionOverride);
        grid.InvalidateVisual();
        if (grid == SquashGrid)
            UpdateEnhancementList(page);
    }

    private void OnNativeChooseFontClicked(object? sender, EventArgs e) =>
        OnChooseFontClicked(sender, new RoutedEventArgs());

    private void SetX26EnhancementsSidebarVisibility(bool isVisible, bool resizeWindow)
    {
        _showX26EnhancementsSidebar = isVisible;
        ApplyX26EnhancementsSidebarVisibility(resizeWindow);
    }

    private void ApplyX26EnhancementsSidebarVisibility(bool resizeWindow)
    {
        bool squashVisible = SquashPaneGrid.IsVisible;
        bool broadcastVisible = BroadcastPaneGrid.IsVisible;
        bool dualView = squashVisible && broadcastVisible;
        bool leftOnlyView = squashVisible && !broadcastVisible;
        bool isVisible = leftOnlyView || (dualView && _showX26EnhancementsSidebar);

        X26EnhancementsMenuItem.IsEnabled = dualView;
        X26EnhancementsMenuItem.IsChecked = leftOnlyView ||
                                            (dualView && _showX26EnhancementsSidebar);
        if (_nativeX26EnhancementsMenuItem is not null)
        {
            _nativeX26EnhancementsMenuItem.IsEnabled = dualView;
            _nativeX26EnhancementsMenuItem.IsChecked = X26EnhancementsMenuItem.IsChecked;
        }

        X26SidebarSection.IsVisible = isVisible;
        UpdateLeftSidebarSectionLayout();
        SquashContentGrid.InvalidateMeasure();
        MainGrid.InvalidateMeasure();

        if (resizeWindow || !IsVisible)
            FitWindowToContent();
    }

    private void OnVideoBookmarksSidebarClicked(object? sender, RoutedEventArgs e) =>
        SetVideoBookmarksSidebarVisibility(VideoBookmarksMenuItem.IsChecked, resizeWindow: true);

    private void OnNativeVideoBookmarksSidebarClicked(object? sender, EventArgs e)
    {
        bool show = !_showVideoBookmarks;
        SetVideoBookmarksSidebarVisibility(show, resizeWindow: true);
        Dispatcher.UIThread.Post(() =>
        {
            if (_nativeVideoBookmarksMenuItem is not null)
                _nativeVideoBookmarksMenuItem.IsChecked = show;
        }, DispatcherPriority.Background);
    }

    private void SetVideoBookmarksSidebarVisibility(bool show, bool resizeWindow)
    {
        _showVideoBookmarks = show;
        _sessionState.ShowVideoBookmarks = show;
        ApplyVideoBookmarkSidebarVisibility(resizeWindow);
        SaveSessionState();
    }

    private void ApplyVideoBookmarkSidebarVisibility(bool resizeWindow)
    {
        bool squashVisible = SquashPaneGrid.IsVisible;
        bool broadcastVisible = BroadcastPaneGrid.IsVisible;
        bool dualView = squashVisible && broadcastVisible;
        bool leftBookmarksVisible = squashVisible && (!dualView || _showVideoBookmarks);

        VideoBookmarksMenuItem.IsEnabled = dualView;
        VideoBookmarksMenuItem.IsChecked = leftBookmarksVisible;
        if (_nativeVideoBookmarksMenuItem is not null)
        {
            _nativeVideoBookmarksMenuItem.IsEnabled = dualView;
            _nativeVideoBookmarksMenuItem.IsChecked = leftBookmarksVisible;
        }
        VideoBookmarkSidebarSection.IsVisible = leftBookmarksVisible;
        UpdateLeftSidebarSectionLayout();
        if (resizeWindow || !IsVisible) FitWindowToContent();
    }

    private void UpdateLeftSidebarSectionLayout()
    {
        bool showX26 = X26SidebarSection.IsVisible;
        bool showBookmarks = VideoBookmarkSidebarSection.IsVisible;
        SidebarSectionsGrid.RowDefinitions[0].Height = showX26
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        SidebarSectionsGrid.RowDefinitions[1].Height = showBookmarks
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        SidebarSectionsGrid.RowSpacing = showX26 && showBookmarks ? 8 : 0;
        EnhancementSidebar.IsVisible = showX26 || showBookmarks;
        SquashContentGrid.InvalidateMeasure();
        MainGrid.InvalidateMeasure();
    }

    private void OnVideoBookmarkTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingVideoBookmarkText) return;
        TeletextPage? page = SquashGrid.Page;
        string? path = _squashFilePath;
        if (page is null || string.IsNullOrWhiteSpace(path)) return;

        RecentFileEntry? file = _sessionState.RecentFiles.FirstOrDefault(entry =>
            string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
        if (file is null) return;
        file.VideoBookmarks ??= new List<VideoBookmarkEntry>();
        VideoBookmarkEntry? bookmark = file.VideoBookmarks.FirstOrDefault(item =>
            item.Magazine == page.Magazine && item.Page == page.PageNumber && item.Subpage == page.SubPage);
        string name = VideoBookmarkTextBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            if (bookmark is not null) file.VideoBookmarks.Remove(bookmark);
        }
        else if (bookmark is null)
        {
            file.VideoBookmarks.Add(new VideoBookmarkEntry
            {
                Magazine = page.Magazine,
                Page = page.PageNumber,
                Subpage = page.SubPage,
                Name = name,
            });
        }
        else
        {
            bookmark.Name = name;
        }
        SaveSessionState();
        UpdateVideoBookmarkLists();
    }

    private void UpdateVideoBookmarkUi()
    {
        _updatingVideoBookmarkText = true;
        VideoBookmarkTextBox.Text = GetVideoBookmarkName(_squashFilePath, SquashGrid.Page);
        _updatingVideoBookmarkText = false;
        UpdateVideoBookmarkLists();
    }

    private string GetVideoBookmarkName(string? path, TeletextPage? page)
    {
        if (string.IsNullOrWhiteSpace(path) || page is null) return string.Empty;
        return _sessionState.RecentFiles.FirstOrDefault(entry =>
                string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))?
            .VideoBookmarks?.FirstOrDefault(bookmark =>
                bookmark.Magazine == page.Magazine
                && bookmark.Page == page.PageNumber
                && bookmark.Subpage == page.SubPage)?.Name ?? string.Empty;
    }

    private void UpdateVideoBookmarkLists()
    {
        UpdateVideoBookmarkList(VideoBookmarkListBox, VideoBookmarkInfoText, _squashFilePath);
    }

    private void UpdateVideoBookmarkList(ListBox list, TextBlock heading, string? path)
    {
        RecentFileEntry? file = string.IsNullOrWhiteSpace(path) ? null : _sessionState.RecentFiles.FirstOrDefault(entry =>
            string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
        List<VideoBookmarkEntry> bookmarks = file?.VideoBookmarks ?? new List<VideoBookmarkEntry>();
        _updatingPageBookmarkList = true;
        list.Items.Clear();
        foreach (VideoBookmarkEntry bookmark in bookmarks
                     .OrderBy(item => item.Magazine).ThenBy(item => item.Page).ThenBy(item => item.Subpage))
        {
            list.Items.Add(new PageBookmarkListEntry(
                bookmark,
                $"{bookmark.Magazine}{bookmark.Page:X2}-{bookmark.Subpage:X4} — {bookmark.Name}"));
        }
        list.SelectedIndex = -1;
        _updatingPageBookmarkList = false;
        heading.Text = $"Page bookmarks ({bookmarks.Count})";
    }

    private void OnPageBookmarkSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingPageBookmarkList
            || _pageBookmarkNavigationPending
            || VideoBookmarkListBox.SelectedItem is not PageBookmarkListEntry selected)
            return;
        VideoBookmarkEntry bookmark = selected.Bookmark;
        var address = (bookmark.Magazine, bookmark.Page, bookmark.Subpage);
        if (_squashStore.GetInstances(address.Magazine, address.Page, address.Subpage).Count == 0)
            return;

        _pageBookmarkNavigationPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                SelectSquashAddress(address);
            }
            finally
            {
                _pageBookmarkNavigationPending = false;
            }
        }, DispatcherPriority.Background);
    }

    private void FitWindowToContent()
    {
        if (!IsVisible)
        {
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            SizeToContent = SizeToContent.WidthAndHeight;
            InvalidateMeasure();
            return;
        }

        int request = ++_fitWindowRequest;
        Dispatcher.UIThread.Post(() =>
        {
            if (request != _fitWindowRequest || Content is not Control root) return;

            root.InvalidateMeasure();
            root.Measure(Size.Infinity);
            Size desiredSize = root.DesiredSize;
            if (desiredSize.Width <= 0 || desiredSize.Height <= 0) return;

            SizeToContent = SizeToContent.Manual;
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            ClientSize = new Size(
                Math.Ceiling(desiredSize.Width),
                Math.Ceiling(desiredSize.Height));
        }, DispatcherPriority.Render);
    }

    private void InitializeNativeMenuReferences()
    {
        NativeMenu? menu = NativeMenu.GetMenu(this);
        if (menu is null) return;

        if (menu.Items.Count > 0 && menu.Items[0] is NativeMenuItem { Menu: { } fileMenu })
        {
            _nativeOpenRecentMenuItem = fileMenu.Items
                .OfType<NativeMenuItem>()
                .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Open Recent", StringComparison.Ordinal))
                ?? fileMenu.Items.ElementAtOrDefault(2) as NativeMenuItem;
            _nativeExportVideoMenuItem = fileMenu.Items
                .OfType<NativeMenuItem>()
                .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Export Video…", StringComparison.Ordinal));
        }

        if (menu.Items.Count > 1 && menu.Items[1] is NativeMenuItem { Menu: { } editMenu })
        {
            if (editMenu.Items.Count > 0) _nativeUndoMenuItem = editMenu.Items[0] as NativeMenuItem;
            if (editMenu.Items.Count > 1) _nativeRedoMenuItem = editMenu.Items[1] as NativeMenuItem;
        }

        if (menu.Items.Count > 2 && menu.Items[2] is NativeMenuItem { Menu: { } viewMenu })
        {
            _nativeX26EnhancementsMenuItem = viewMenu.Items.OfType<NativeMenuItem>()
                .FirstOrDefault(item => item.Header?.ToString() == "X/26 Enhancements Sidebar");
            _nativeVideoBookmarksMenuItem = viewMenu.Items.OfType<NativeMenuItem>()
                .FirstOrDefault(item => item.Header?.ToString() == "Page Bookmarks Sidebar");
            _nativeSuppressFlashMenuItem = viewMenu.Items.OfType<NativeMenuItem>()
                .FirstOrDefault(item => item.Header?.ToString() == "Suppress Flash");
        }
    }

    /// <summary>Copies one row (0=header, 1-24=body) from the currently displayed
    /// broadcast-stream page into the squash page, at the BYTE level - it copies the
    /// exact raw 42-byte packet that was captured, then re-decodes it via the same
    /// ApplyRow path used for live capture, so the squash page's raw bytes and
    /// decoded grid both stay byte-for-byte faithful to what was actually broadcast
    /// (important for saving back a real stream later, not just an approximation
    /// re-encoded from decoded text/colors).</summary>
    private void OnTransferRowClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int row) return;
        if (row is < 0 or > 24) return; // grid only has rows 0-24 (25 rows total)
        if (BroadcastGrid.Page is not { } sourcePage) return;

        // A capture can have no packet at all for a visually blank row. Such a row
        // must still be transferable so it can intentionally clear the target row.
        // Preserve captured bytes exactly when present; otherwise create a valid
        // blank packet with the correct magazine/row address.
        var sourceRaw = sourcePage.RawRows[row];
        var raw = sourceRaw is not null
            ? (byte[])sourceRaw.Clone()
            : CreateBlankPacket(sourcePage, row);

        // Always write into the page currently visible in the left pane. This matters
        // after loading/navigating a squashed capture, where the visible page may no
        // longer be the original _squashPage instance created at startup.
        var targetPage = SquashGrid.Page ?? _squashPage;
        _squashPage = targetPage;
        EnsurePageHistory(targetPage);

        // Keep the recovered page independent from the broadcast instance. Editing
        // the copied row later must never mutate the source capture's raw packet.
        PageAssembler.ApplyRow(targetPage, row, raw);
        CommitPageEdit(targetPage);

        // Page's grid is mutated in place, so the Avalonia Page property itself does
        // not change and cannot automatically trigger a redraw.
        SquashGrid.InvalidateVisual();
    }

    private static byte[] CreateBlankPacket(TeletextPage page, int row)
    {
        var packet = new byte[42];
        int magazineBits = page.Magazine == 8 ? 0 : page.Magazine & 0x07;
        int magazineRowAddress = magazineBits | (row << 3);

        packet[0] = Hamming.Encode84(magazineRowAddress & 0x0F);
        packet[1] = Hamming.Encode84((magazineRowAddress >> 4) & 0x0F);

        // A parity-protected teletext space is 0x20 (already has odd parity).
        Array.Fill(packet, (byte)0x20, 2, 40);

        if (row == 0)
        {
            packet[2] = Hamming.Encode84(page.PageNumber & 0x0F);
            packet[3] = Hamming.Encode84((page.PageNumber >> 4) & 0x0F);
            packet[4] = Hamming.Encode84(page.SubPage & 0x0F);
            packet[5] = Hamming.Encode84((page.SubPage >> 4) & 0x0F);
            packet[6] = Hamming.Encode84((page.SubPage >> 8) & 0x0F);
            packet[7] = Hamming.Encode84((page.SubPage >> 12) & 0x0F);
            packet[8] = Hamming.Encode84(0);
            packet[9] = Hamming.Encode84(0);
        }

        return packet;
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open teletext broadcast capture",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Teletext files") { Patterns = new[] { "*.tti", "*.t42" } },
                FilePickerFileTypes.All
            }
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null) return;

        string displayPath = file.Path.IsFile ? file.Path.LocalPath : file.Path.ToString();
        CaptureRecentFilePositions();
        await using var stream = await file.OpenReadAsync();
        await LoadBroadcastStreamAsync(stream, displayPath);
        await RememberFileAsync(file.Path.IsFile ? file.Path.LocalPath : null, broadcast: true);
    }

    private async void OnOpenSquashedClicked(object? sender, RoutedEventArgs e) =>
        await OpenSquashFileAsync();

    private async Task OpenSquashFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open teletext page or squashed capture",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Teletext files") { Patterns = new[] { "*.tti", "*.t42" } },
                FilePickerFileTypes.All
            }
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null) return;

        string displayPath = file.Path.IsFile ? file.Path.LocalPath : file.Path.ToString();
        CaptureRecentFilePositions();
        await using var stream = await file.OpenReadAsync();
        bool openedAsBroadcast = await LoadSquashStreamAsync(stream, displayPath);
        await RememberFileAsync(
            file.Path.IsFile ? file.Path.LocalPath : null,
            broadcast: openedAsBroadcast);
    }

    private async Task LoadBroadcastStreamAsync(Stream stream, string? filePath = null)
    {
        BeginLoading(broadcast: true, filePath);
        try
        {
            _store.Clear();
            _broadcastPackets.Clear();
            ClearBroadcastPane();
            await AssembleStreamAsync(
                stream,
                _store,
                _broadcastPackets,
                percent => UpdateLoadingProgress(true, percent),
                decodeEnhancements: false);
            _broadcastFilePath = filePath;
            BroadcastFilePathText.Text = FormatFileFooter(filePath, _store.TotalInstanceCount);
            PopulatePageCombo();
            _broadcastFileOpen = true;
            UpdateNavigationButtons();

            if (SquashInfoText != null) SquashInfoText.IsVisible = true;
            if (SquashEditToolbar != null) SquashEditToolbar.IsVisible = true;
            UpdateWorkspacePaneVisibility();
            UpdateSquashAddressToolbarVisibility();
            FitWindowToContent();
        }
        finally
        {
            EndLoading(broadcast: true);
        }
    }

    private async Task<bool> LoadSquashStreamAsync(Stream stream, string? filePath = null)
    {
        _squashPaneEstablished = true;
        bool detectedFullBroadcast = false;
        BeginLoading(broadcast: false, filePath);
        try
        {
            _squashStore.Clear();
            _squashPackets.Clear();
            _deletedSquashPacketIndices.Clear();
            ClearSquashPane();
            await AssembleStreamAsync(
                stream,
                _squashStore,
                _squashPackets,
                percent => UpdateLoadingProgress(false, percent),
                decodeEnhancements: false);
            detectedFullBroadcast = _squashStore.GetKnownAddresses().Any(address =>
                _squashStore.GetInstances(address.magazine, address.page, address.subpage).Count > 1);

            if (!detectedFullBroadcast)
            {
                // X/26 is deliberately skipped during format detection so a full
                // broadcast accidentally opened here stays fast. Decode it only
                // after the file has been confirmed as an editable squash/single.
                _squashStore.Clear();
                var enhancementAssembler = new PageAssembler(_squashStore, decodeEnhancements: true);
                for (int packetIndex = 0; packetIndex < _squashPackets.Count; packetIndex++)
                    enhancementAssembler.Feed(_squashPackets[packetIndex], packetIndex);
                enhancementAssembler.FinalizeAll();

                if (_squashStore.TotalInstanceCount == 0)
                    InitializeBlankSquashDocument();
                else
                    PopulateSquashPageCombo();

                if (SquashInfoText != null) SquashInfoText.IsVisible = true;
                if (SquashEditToolbar != null) SquashEditToolbar.IsVisible = true;
                _squashFilePath = filePath;
                UpdateSquashFileFooter();
                _squashFileOpen = true;
                SetSquashDirty(false);
                UpdateWorkspacePaneVisibility();
                UpdateNavigationButtons();
            }
        }
        finally
        {
            EndLoading(broadcast: false);
        }

        if (!detectedFullBroadcast) return false;

        await LoadCapturedPacketsAsBroadcastAsync(_squashPackets, filePath);
        _sessionState.SquashFilePath = null;
        await ShowMessageAsync(
            "Full broadcast detected",
            "This file contains multiple versions of the same page and appears to be a full broadcast capture. It has been opened in the read-only Full Broadcast view.");
        return true;
    }

    private async Task LoadCapturedPacketsAsBroadcastAsync(
        IReadOnlyList<byte[]> packets,
        string? filePath)
    {
        BeginLoading(broadcast: true, filePath);
        try
        {
            _store.Clear();
            _broadcastPackets.Clear();
            ClearBroadcastPane();
            _squashPaneEstablished = false;

            var assembler = new PageAssembler(_store, decodeEnhancements: false);
            int total = Math.Max(packets.Count, 1);
            for (int index = 0; index < packets.Count; index++)
            {
                byte[] packet = (byte[])packets[index].Clone();
                _broadcastPackets.Add(packet);
                assembler.Feed(packet, index);

                if ((index & 0x3FF) == 0)
                {
                    UpdateLoadingProgress(true, index * 100 / total);
                    await Task.Yield();
                }
            }
            assembler.FinalizeAll();
            UpdateLoadingProgress(true, 100);

            _broadcastFilePath = filePath;
            BroadcastFilePathText.Text = FormatFileFooter(filePath, _store.TotalInstanceCount);
            PopulatePageCombo();
            _broadcastFileOpen = true;
            _squashFileOpen = false;
            _squashFilePath = null;
            SetSquashDirty(false);
            UpdateWorkspacePaneVisibility();
            UpdateNavigationButtons();
            FitWindowToContent();
        }
        finally
        {
            EndLoading(broadcast: true);
        }
    }

    private void ClearBroadcastPane()
    {
        _broadcastEnhancementsScanned.Clear();
        _suppressComboEvents = true;
        MagazineComboBox.Items.Clear();
        PageNumberComboBox.Items.Clear();
        SubpageComboBox.Items.Clear();
        VersionComboBox.Items.Clear();
        BroadcastGrid.Page = null;
        BroadcastGrid.ClearSelection();
        _broadcastFilePath = null;
        _broadcastFileOpen = false;
        _suppressComboEvents = false;
        UpdateNavigationButtons();
    }

    private void ClearSquashPane()
    {
        _suppressComboEvents = true;
        SquashMagazineComboBox.Items.Clear();
        SquashPageNumberComboBox.Items.Clear();
        SquashSubpageComboBox.Items.Clear();
        _squashPage = new TeletextPage();
        SquashGrid.Page = null;
        SquashGrid.ClearSelection();
        EnhancementItemsControl.Items.Clear();
        EnhancementInfoText.Text = "X/26 enhancements (0)";
        _pageHistories.Clear();
        _deletedSquashPacketIndices.Clear();
        _structuralDirty = false;
        _squashFileOpen = false;
        _suppressComboEvents = false;
        UpdateNavigationButtons();
        UpdateUndoToolbar();
    }

    private static async Task AssembleStreamAsync(
        Stream stream,
        PageStore store,
        List<byte[]> capturedPackets,
        Action<int> reportProgress,
        bool decodeEnhancements)
    {
        var assembler = new PageAssembler(store, decodeEnhancements);
        var packet = new byte[42];
        int filled = 0;
        long startPosition = stream.CanSeek ? stream.Position : 0;
        long totalBytes = stream.CanSeek ? Math.Max(stream.Length - startPosition, 1) : 0;
        long processedBytes = 0;
        int lastPercent = -1;

        reportProgress(0);
        await Task.Delay(1);

        while (true)
        {
            int bytesRead = await stream.ReadAsync(packet.AsMemory(filled, packet.Length - filled));
            if (bytesRead == 0) break;

            filled += bytesRead;
            processedBytes += bytesRead;

            if (totalBytes > 0)
            {
                int percent = (int)Math.Min(100, processedBytes * 100 / totalBytes);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    reportProgress(percent);
                    await Task.Delay(1);
                }
            }

            if (filled != packet.Length) continue;

            var capturedPacket = (byte[])packet.Clone();
            int packetIndex = capturedPackets.Count;
            capturedPackets.Add(capturedPacket);
            assembler.Feed(capturedPacket, packetIndex);
            filled = 0;
        }

        assembler.FinalizeAll();
        reportProgress(100);
    }

    private void BeginLoading(bool broadcast, string? filePath)
    {
        string fileName = string.IsNullOrWhiteSpace(filePath)
            ? string.Empty
            : Path.GetFileName(filePath);

        if (broadcast)
        {
            BroadcastPaneGrid.IsVisible = true;
            BroadcastInfoText.Text = string.IsNullOrEmpty(fileName)
                ? "Full broadcast"
                : $"Full broadcast — {fileName}";
            BroadcastFilePathText.Text = FormatFileFooter(filePath, 0);
            BroadcastLoadingText.Text = "Loading... 0%";
            BroadcastLoadingOverlay.IsVisible = true;
        }
        else
        {
            SquashInfoText.IsVisible = true;
            SquashInfoText.Text = string.IsNullOrEmpty(fileName)
                ? "Squashed page"
                : $"Squashed page — {fileName}";
            SquashFilePathText.Text = FormatFileFooter(filePath, 0);
            SquashLoadingText.Text = "Loading... 0%";
            SquashLoadingOverlay.IsVisible = true;
        }

        UpdateWorkspacePaneVisibility();
        UpdateSquashAddressToolbarVisibility();
        FitWindowToContent();
    }

    private void UpdateLoadingProgress(bool broadcast, int percent)
    {
        if (broadcast)
            BroadcastLoadingText.Text = $"Loading... {percent}%";
        else
            SquashLoadingText.Text = $"Loading... {percent}%";
    }

    private void EndLoading(bool broadcast)
    {
        if (broadcast)
            BroadcastLoadingOverlay.IsVisible = false;
        else
            SquashLoadingOverlay.IsVisible = false;
    }

    private static string FormatFileFooter(string? filePath, int pagesRead) =>
        string.IsNullOrWhiteSpace(filePath)
            ? $"Pages: {pagesRead}"
            : $"{filePath} — Pages: {pagesRead}";

    private void UpdateSquashFileFooter() =>
        SquashFilePathText.Text = FormatFileFooter(
            _squashFilePath,
            _squashStore.TotalInstanceCount);

    private void SetSquashDirty(bool dirty)
    {
        _squashDirty = dirty;
        UpdateWindowAndPaneTitles();
    }

    private void UpdateWindowAndPaneTitles()
    {
        bool broadcastVisible = BroadcastPaneGrid.IsVisible;
        bool dualPane = broadcastVisible && _squashPaneEstablished;
        bool broadcastOnly = broadcastVisible && !_squashPaneEstablished;
        string dirtyMarker = _squashDirty ? " *" : string.Empty;
        string squashFileName = string.IsNullOrWhiteSpace(_squashFilePath)
            ? "Untitled"
            : Path.GetFileName(_squashFilePath);
        string squashPaneFileName = string.IsNullOrWhiteSpace(_squashFilePath)
            ? "Untitled.t42"
            : Path.GetFileName(_squashFilePath);
        string broadcastFileName = string.IsNullOrWhiteSpace(_broadcastFilePath)
            ? "Untitled"
            : Path.GetFileName(_broadcastFilePath);

        if (dualPane)
        {
            Title = AppVersion.DisplayName;
            SquashInfoText.IsVisible = true;
            BroadcastInfoText.IsVisible = true;
            SquashInfoText.Text = $"Squashed page — {squashPaneFileName}{dirtyMarker}";
            BroadcastInfoText.Text = $"Full broadcast — {broadcastFileName}";
        }
        else if (broadcastOnly)
        {
            Title = $"{AppVersion.DisplayName} - {broadcastFileName}";
            BroadcastInfoText.IsVisible = false;
            BroadcastInfoText.Text = "Full broadcast";
        }
        else
        {
            Title = $"{AppVersion.DisplayName} - {squashFileName}{dirtyMarker}";
            SquashInfoText.IsVisible = false;
            SquashInfoText.Text = "Squashed page";
        }
    }

    private PageHistory EnsurePageHistory(TeletextPage page)
    {
        if (_pageHistories.TryGetValue(page, out var existing)) return existing;

        var history = new PageHistory();
        history.States.Add(CapturePage(page));
        history.Position = 0;
        history.SavedPosition = 0;
        _pageHistories[page] = history;
        return history;
    }

    private static PageSnapshot CapturePage(TeletextPage page)
    {
        var snapshot = new PageSnapshot();
        for (int row = 0; row < 25; row++)
            snapshot.Rows[row] = page.RawRows[row] is { } raw ? (byte[])raw.Clone() : null;
        foreach (var packet in page.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
            snapshot.EnhancementPackets.Add(((byte[])packet.RawPacket.Clone(), packet.PacketIndex));
        return snapshot;
    }

    private static bool SnapshotsEqual(PageSnapshot left, PageSnapshot right)
    {
        for (int row = 0; row < 25; row++)
        {
            var a = left.Rows[row];
            var b = right.Rows[row];
            if (a is null || b is null)
            {
                if (a is not null || b is not null) return false;
            }
            else if (!a.AsSpan().SequenceEqual(b))
            {
                return false;
            }
        }
        if (left.EnhancementPackets.Count != right.EnhancementPackets.Count) return false;
        for (int index = 0; index < left.EnhancementPackets.Count; index++)
        {
            var a = left.EnhancementPackets[index];
            var b = right.EnhancementPackets[index];
            if (a.PacketIndex != b.PacketIndex || !a.RawPacket.AsSpan().SequenceEqual(b.RawPacket))
                return false;
        }
        return true;
    }

    private void CommitPageEdit(TeletextPage page)
    {
        if (!_squashPaneEstablished)
        {
            _squashPaneEstablished = true;
            UpdateWorkspacePaneVisibility();
        }

        var history = EnsurePageHistory(page);
        var snapshot = CapturePage(page);
        if (SnapshotsEqual(history.States[history.Position], snapshot))
        {
            UpdateUndoToolbar();
            return;
        }

        if (history.Position < history.States.Count - 1)
        {
            history.States.RemoveRange(
                history.Position + 1,
                history.States.Count - history.Position - 1);
            if (history.SavedPosition > history.Position)
                history.SavedPosition = -1;
        }

        history.States.Add(snapshot);
        history.Position++;
        UpdateDirtyFromHistories();
        UpdateUndoToolbar();
    }

    private static void RestorePage(TeletextPage page, PageSnapshot snapshot)
    {
        // Decode the Level-1 rows without the current enhancement overlay first.
        // Otherwise a moved diacritic can be re-applied while the old raw rows are
        // being restored and leave stale display state at its former destination.
        page.EnhancementPackets.Clear();
        PageAssembler.ApplyLevel15Enhancements(page);
        for (int row = 0; row < 25; row++)
        {
            if (snapshot.Rows[row] is { } raw)
            {
                PageAssembler.ApplyRow(page, row, (byte[])raw.Clone());
            }
            else
            {
                page.RawRows[row] = null;
                for (int column = 0; column < 40; column++)
                    page.Grid[column, row] = Cell.Default;
            }
        }
        PageAssembler.ReplaceEnhancementPackets(page, snapshot.EnhancementPackets);
    }

    private void UndoCurrentPage()
    {
        if (SquashGrid.Page is not { } page) return;
        var history = EnsurePageHistory(page);
        if (history.Position <= 0) return;

        history.Position--;
        RestorePage(page, history.States[history.Position]);
        UpdateEnhancementList(page);
        SquashGrid.InvalidateVisual();
        UpdateDirtyFromHistories();
        UpdateUndoToolbar();
    }

    private void RedoCurrentPage()
    {
        if (SquashGrid.Page is not { } page) return;
        var history = EnsurePageHistory(page);
        if (history.Position >= history.States.Count - 1) return;

        history.Position++;
        RestorePage(page, history.States[history.Position]);
        UpdateEnhancementList(page);
        SquashGrid.InvalidateVisual();
        UpdateDirtyFromHistories();
        UpdateUndoToolbar();
    }

    private void UpdateDirtyFromHistories()
    {
        bool dirty = _structuralDirty
            || _pageHistories.Values.Any(history => history.Position != history.SavedPosition);
        SetSquashDirty(dirty);
    }

    private void MarkHistoriesSaved()
    {
        _structuralDirty = false;
        foreach (var history in _pageHistories.Values)
            history.SavedPosition = history.Position;
        UpdateDirtyFromHistories();
    }

    private void UpdateUndoToolbar()
    {
        if (SquashGrid.Page is not { } page || !_pageHistories.TryGetValue(page, out var history))
        {
            UndoMenuItem.IsEnabled = false;
            RedoMenuItem.IsEnabled = false;
            if (_nativeUndoMenuItem is not null) _nativeUndoMenuItem.IsEnabled = false;
            if (_nativeRedoMenuItem is not null) _nativeRedoMenuItem.IsEnabled = false;
            UndoInfoText.Text = "History: 0/0";
            return;
        }

        int totalActions = history.States.Count - 1;
        UndoMenuItem.IsEnabled = history.Position > 0;
        RedoMenuItem.IsEnabled = history.Position < totalActions;
        if (_nativeUndoMenuItem is not null) _nativeUndoMenuItem.IsEnabled = history.Position > 0;
        if (_nativeRedoMenuItem is not null) _nativeRedoMenuItem.IsEnabled = history.Position < totalActions;
        UndoInfoText.Text = $"History: {history.Position}/{totalActions}";
    }

    private void OnUndoClicked(object? sender, RoutedEventArgs e) => UndoCurrentPage();
    private void OnRedoClicked(object? sender, RoutedEventArgs e) => RedoCurrentPage();

    private void OnColorModeClicked(object? sender, RoutedEventArgs e)
    {
        _mosaicColorMode = !_mosaicColorMode;
        ColorModeButton.Content = _mosaicColorMode ? "Mosaic" : "Alpha";
    }

    private void OnControlCodesClicked(object? sender, RoutedEventArgs e)
    {
        bool show = !SquashGrid.ShowControlCodes;
        SquashGrid.ShowControlCodes = show;
        ControlCodesButton.Content = show ? "Codes: On" : "Codes: Off";
        ControlCodesButton.Background = show
            ? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#296A43"))
            : null;
    }

    private void OnBroadcastControlCodesClicked(object? sender, RoutedEventArgs e)
    {
        bool show = !BroadcastGrid.ShowControlCodes;
        BroadcastGrid.ShowControlCodes = show;
        BroadcastControlCodesButton.Content = show ? "Codes: On" : "Codes: Off";
        BroadcastControlCodesButton.Background = show
            ? new SolidColorBrush(Color.Parse("#296A43"))
            : null;
    }

    private void OnSelectionBytesClicked(object? sender, RoutedEventArgs e)
    {
        bool show = !SquashGrid.ShowSelectionBytes;
        SquashGrid.ShowSelectionBytes = show;
        SelectionBytesButton.Content = show ? "Bytes: On" : "Bytes: Off";
        SelectionBytesButton.Background = show
            ? new SolidColorBrush(Color.Parse("#296A43"))
            : null;
        _sessionState.ShowSquashSelectionBytes = show;
        SaveSessionState();
    }

    private void OnBroadcastSelectionBytesClicked(object? sender, RoutedEventArgs e)
    {
        bool show = !BroadcastGrid.ShowSelectionBytes;
        BroadcastGrid.ShowSelectionBytes = show;
        BroadcastSelectionBytesButton.Content = show ? "Bytes: On" : "Bytes: Off";
        BroadcastSelectionBytesButton.Background = show
            ? new SolidColorBrush(Color.Parse("#296A43"))
            : null;
        _sessionState.ShowBroadcastSelectionBytes = show;
        SaveSessionState();
    }

    private void OnDiacriticsClicked(object? sender, RoutedEventArgs e)
    {
        bool show = !SquashGrid.ShowDiacriticMarkers;
        SquashGrid.ShowDiacriticMarkers = show;
        DiacriticsButton.Content = show ? "Diacritics: On" : "Diacritics: Off";
        DiacriticsButton.Background = show
            ? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#7A2830"))
            : null;
    }

    private void OnBroadcastDiacriticsClicked(object? sender, RoutedEventArgs e)
    {
        bool show = !BroadcastGrid.ShowDiacriticMarkers;
        BroadcastGrid.ShowDiacriticMarkers = show;
        BroadcastDiacriticsButton.Content = show ? "Diacritics: On" : "Diacritics: Off";
        BroadcastDiacriticsButton.Background = show
            ? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#7A2830"))
            : null;

        if (show && BroadcastGrid.Page is { } page)
        {
            EnsureBroadcastEnhancementsLoaded(page);
            BroadcastGrid.InvalidateVisual();
        }
    }

    private void ApplyToggleSessionState()
    {
        bool legacyShowCodes = _sessionState.ShowControlCodes ?? false;
        bool showSquashCodes = _sessionState.ShowSquashControlCodes ?? legacyShowCodes;
        SquashGrid.ShowControlCodes = showSquashCodes;
        ControlCodesButton.Content = showSquashCodes ? "Codes: On" : "Codes: Off";
        ControlCodesButton.Background = showSquashCodes
            ? new SolidColorBrush(Color.Parse("#296A43"))
            : null;

        bool showBroadcastCodes = _sessionState.ShowBroadcastControlCodes ?? legacyShowCodes;
        BroadcastGrid.ShowControlCodes = showBroadcastCodes;
        BroadcastControlCodesButton.Content = showBroadcastCodes ? "Codes: On" : "Codes: Off";
        BroadcastControlCodesButton.Background = showBroadcastCodes
            ? new SolidColorBrush(Color.Parse("#296A43"))
            : null;

        bool showSquashSelectionBytes = _sessionState.ShowSquashSelectionBytes ?? false;
        SquashGrid.ShowSelectionBytes = showSquashSelectionBytes;
        SelectionBytesButton.Content = showSquashSelectionBytes ? "Bytes: On" : "Bytes: Off";
        SelectionBytesButton.Background = showSquashSelectionBytes
            ? new SolidColorBrush(Color.Parse("#296A43"))
            : null;

        bool showBroadcastSelectionBytes = _sessionState.ShowBroadcastSelectionBytes ?? false;
        BroadcastGrid.ShowSelectionBytes = showBroadcastSelectionBytes;
        BroadcastSelectionBytesButton.Content = showBroadcastSelectionBytes ? "Bytes: On" : "Bytes: Off";
        BroadcastSelectionBytesButton.Background = showBroadcastSelectionBytes
            ? new SolidColorBrush(Color.Parse("#296A43"))
            : null;

        bool showSquashDiacritics = _sessionState.ShowSquashDiacritics ?? false;
        SquashGrid.ShowDiacriticMarkers = showSquashDiacritics;
        DiacriticsButton.Content = showSquashDiacritics ? "Diacritics: On" : "Diacritics: Off";
        DiacriticsButton.Background = showSquashDiacritics
            ? new SolidColorBrush(Color.Parse("#7A2830"))
            : null;

        bool showBroadcastDiacritics = _sessionState.ShowBroadcastDiacritics ?? false;
        BroadcastGrid.ShowDiacriticMarkers = showBroadcastDiacritics;
        BroadcastDiacriticsButton.Content = showBroadcastDiacritics ? "Diacritics: On" : "Diacritics: Off";
        BroadcastDiacriticsButton.Background = showBroadcastDiacritics
            ? new SolidColorBrush(Color.Parse("#7A2830"))
            : null;

        bool suppressFlash = _sessionState.SuppressFlash ?? false;
        SquashGrid.SuppressFlash = suppressFlash;
        BroadcastGrid.SuppressFlash = suppressFlash;
        SuppressFlashMenuItem.IsChecked = suppressFlash;
        if (_nativeSuppressFlashMenuItem is not null)
            _nativeSuppressFlashMenuItem.IsChecked = suppressFlash;
    }

    private void OnColorClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || !int.TryParse(button.Tag?.ToString(), out int colorCode)
            || colorCode is < 0 or > 7)
            return;

        InsertControlCodeAtSelection((byte)(colorCode + (_mosaicColorMode ? 0x10 : 0x00)));
    }

    private void OnInsertControlCodeClicked(object? sender, RoutedEventArgs e)
    {
        object? parameter = sender switch
        {
            MenuItem item => item.CommandParameter,
            NativeMenuItem item => item.CommandParameter,
            _ => null,
        };
        if (int.TryParse(parameter?.ToString(), out int code) && code is >= 0 and <= 0x1F)
            InsertControlCodeAtSelection((byte)code);
    }

    private void OnBackgroundAttributeClicked(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedSquashCell() is not { } cell) return;
        InsertControlCodeAtSelection(cell.Background == TeletextColor.Black ? (byte)0x1D : (byte)0x1C);
    }

    private void OnFlashAttributeClicked(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedSquashCell() is not { } cell) return;
        InsertControlCodeAtSelection(cell.Flash ? (byte)0x09 : (byte)0x08);
    }

    private void OnHeightAttributeClicked(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedSquashCell() is not { } cell) return;
        byte code = (cell.DoubleHeight, cell.DoubleWidth) switch
        {
            (false, false) => 0x0D,
            (false, true) => 0x0F,
            (true, false) => 0x0C,
            _ => 0x0E,
        };
        InsertControlCodeAtSelection(code);
    }

    private void OnWidthAttributeClicked(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedSquashCell() is not { } cell) return;
        byte code = (cell.DoubleHeight, cell.DoubleWidth) switch
        {
            (false, false) => 0x0E,
            (true, false) => 0x0F,
            (false, true) => 0x0C,
            _ => 0x0D,
        };
        InsertControlCodeAtSelection(code);
    }

    private void OnMosaicShapeAttributeClicked(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedSquashCell() is not { } cell) return;
        InsertControlCodeAtSelection(cell.MosaicSeparated ? (byte)0x19 : (byte)0x1A);
    }

    private void OnHoldMosaicAttributeClicked(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedSquashCell() is not { } cell) return;
        InsertControlCodeAtSelection(cell.HoldMosaics ? (byte)0x1F : (byte)0x1E);
    }

    private Cell? GetSelectedSquashCell()
    {
        if (SquashGrid.Page is not { } page) return null;
        int x = SquashGrid.SelectedColumn;
        int y = SquashGrid.SelectedRow;
        return x is >= 0 and < 40 && y is >= 0 and < 25 ? page.Grid[x, y] : null;
    }

    private void InsertControlCodeAtSelection(byte controlCode)
    {
        if (SquashGrid.Page is not { } page) return;
        int x = SquashGrid.SelectedColumn;
        int y = SquashGrid.SelectedRow;
        if (x is < 0 or >= 40 || y is < 0 or >= 25 || (y == 0 && x < 8))
        {
            PlaySystemErrorSound();
            return;
        }

        EnsurePageHistory(page);
        byte[] raw = page.RawRows[y] is { } existing
            ? (byte[])existing.Clone()
            : CreateBlankPacket(page, y);
        raw[2 + x] = WithOddParity(controlCode);
        PageAssembler.ApplyRow(page, y, raw);
        CommitPageEdit(page);
        SquashGrid.InvalidateVisual();

        if (x < 39)
            SquashGrid.MoveSelectionTo(x + 1, y);
        else
            UpdateCellAwareToolbar();
    }

    private void UpdateCellAwareToolbar()
    {
        if (GetSelectedSquashCell() is not { } cell) return;

        bool blackBackground = cell.Background == TeletextColor.Black;
        BackgroundAttributeButton.Content = blackBackground ? "🟨" : "⬛";
        ToolTip.SetTip(BackgroundAttributeButton, blackBackground ? "New background" : "Black background");

        FlashAttributeButton.Content = cell.Flash ? "◉" : "⚡";
        ToolTip.SetTip(FlashAttributeButton, cell.Flash ? "Steady" : "Flash");
        HeightAttributeButton.Content = "H×2";
        HeightAttributeButton.Background = cell.DoubleHeight
            ? new SolidColorBrush(Color.Parse("#296A43"))
            : null;
        ToolTip.SetTip(
            HeightAttributeButton,
            cell.DoubleHeight ? "Disable double height" : "Enable double height");
        WidthAttributeButton.Content = "W×2";
        WidthAttributeButton.Background = cell.DoubleWidth
            ? new SolidColorBrush(Color.Parse("#296A43"))
            : null;
        ToolTip.SetTip(
            WidthAttributeButton,
            cell.DoubleWidth ? "Disable double width" : "Enable double width");

        MosaicShapeAttributeButton.Content = cell.MosaicSeparated ? "CON" : "SEP";
        ToolTip.SetTip(
            MosaicShapeAttributeButton,
            cell.MosaicSeparated ? "Contiguous mosaics" : "Separated mosaics");
        HoldMosaicAttributeButton.Content = cell.HoldMosaics ? "▶" : "✋";
        ToolTip.SetTip(HoldMosaicAttributeButton, cell.HoldMosaics ? "Release mosaics" : "Hold mosaics");
    }

    private void LoadSessionState()
    {
        try
        {
            if (File.Exists(_sessionStatePath))
            {
                string json = File.ReadAllText(_sessionStatePath);
                _sessionState = JsonSerializer.Deserialize<SessionState>(json) ?? new SessionState();
            }
        }
        catch
        {
            _sessionState = new SessionState();
        }
    }

    private async Task RestoreSessionFilesAsync()
    {
        if (_sessionState.SquashFilePath is { } squashPath && File.Exists(squashPath))
        {
            try
            {
                await using var stream = File.OpenRead(squashPath);
                await LoadSquashStreamAsync(stream, squashPath);
            }
            catch { }
        }

        if (_sessionState.BroadcastFilePath is { } broadcastPath && File.Exists(broadcastPath))
        {
            try
            {
                await using var stream = File.OpenRead(broadcastPath);
                await LoadBroadcastStreamAsync(stream, broadcastPath);
            }
            catch { }
        }

        if (_sessionState.SquashMagazine is { } squashMagazine
            && _sessionState.SquashPage is { } squashPage
            && _sessionState.SquashSubpage is { } squashSubpage
            && _squashStore.GetInstances(squashMagazine, squashPage, squashSubpage).Count > 0)
        {
            SelectSquashAddress((squashMagazine, squashPage, squashSubpage));
        }

        if (_sessionState.BroadcastMagazine is { } broadcastMagazine
            && _sessionState.BroadcastPage is { } broadcastPage
            && _sessionState.BroadcastSubpage is { } broadcastSubpage
            && _store.GetInstances(broadcastMagazine, broadcastPage, broadcastSubpage).Count > 0)
        {
            SelectBroadcastAddress(
                (broadcastMagazine, broadcastPage, broadcastSubpage),
                _sessionState.BroadcastVersion ?? 0);
        }
    }

    private async Task RememberFileAsync(string? path, bool broadcast)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (broadcast)
            _sessionState.BroadcastFilePath = path;
        else
            _sessionState.SquashFilePath = path;

        AddOrUpdateRecentFile(path, broadcast);

        try
        {
            await SaveSessionStateAsync();
        }
        catch { }
    }

    private void AddOrUpdateRecentFile(string path, bool broadcast)
    {
        _sessionState.RecentFiles ??= new List<RecentFileEntry>();
        RecentFileEntry? existing = _sessionState.RecentFiles.FirstOrDefault(entry =>
            string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) _sessionState.RecentFiles.Remove(existing);
        var entry = existing ?? new RecentFileEntry { Path = path };
        entry.BroadcastPane = broadcast;
        entry.VideoBookmarks ??= new List<VideoBookmarkEntry>();
        CaptureRecentFilePosition(entry);
        EnsureDefaultIndexBookmark(entry, broadcast);
        _sessionState.RecentFiles.Insert(0, entry);
        if (_sessionState.RecentFiles.Count > 10)
            _sessionState.RecentFiles.RemoveRange(10, _sessionState.RecentFiles.Count - 10);
        RebuildOpenRecentMenus();
        UpdateVideoBookmarkUi();
    }

    private void EnsureDefaultIndexBookmark(RecentFileEntry entry, bool broadcast)
    {
        if (broadcast) return;
        List<int> indexSubpages = _squashStore.GetKnownSubpages(1, 0x00).ToList();
        if (indexSubpages.Count == 0)
        {
            entry.PageBookmarksInitialized = true;
            return;
        }

        int firstSubpage = indexSubpages[0];
        VideoBookmarkEntry? existingIndex = entry.VideoBookmarks.FirstOrDefault(bookmark =>
            bookmark.Magazine == 1 && bookmark.Page == 0
            && string.Equals(bookmark.Name, "Index", StringComparison.OrdinalIgnoreCase));
        if (existingIndex is not null)
            existingIndex.Subpage = firstSubpage;
        else if (!entry.PageBookmarksInitialized)
        {
            entry.VideoBookmarks.Add(new VideoBookmarkEntry
            {
                Magazine = 1,
                Page = 0,
                Subpage = firstSubpage,
                Name = "Index",
            });
        }
        entry.PageBookmarksInitialized = true;
    }

    private void CaptureRecentFilePositions()
    {
        if (_sessionState.RecentFiles is null) return;
        foreach (RecentFileEntry entry in _sessionState.RecentFiles)
            CaptureRecentFilePosition(entry);
    }

    private void PersistRecentFilePositions()
    {
        CaptureRecentFilePositions();
        SaveSessionState();
    }

    private void CaptureRecentFilePosition(RecentFileEntry entry)
    {
        if (entry.BroadcastPane
            && !string.IsNullOrWhiteSpace(_broadcastFilePath)
            && string.Equals(entry.Path, _broadcastFilePath, StringComparison.OrdinalIgnoreCase)
            && TryGetBroadcastAddress(out var broadcastAddress))
        {
            entry.Magazine = broadcastAddress.magazine;
            entry.Page = broadcastAddress.page;
            entry.Subpage = broadcastAddress.subpage;
            entry.Version = Math.Max(VersionComboBox.SelectedIndex, 0);
        }
        else if (!entry.BroadcastPane
                 && !string.IsNullOrWhiteSpace(_squashFilePath)
                 && string.Equals(entry.Path, _squashFilePath, StringComparison.OrdinalIgnoreCase)
                 && TryGetSquashAddress(out var squashAddress))
        {
            entry.Magazine = squashAddress.magazine;
            entry.Page = squashAddress.page;
            entry.Subpage = squashAddress.subpage;
            entry.Version = null;
        }
    }

    private void RebuildOpenRecentMenus()
    {
        _sessionState.RecentFiles ??= new List<RecentFileEntry>();
        OpenRecentMenuItem.Items.Clear();
        NativeMenu? nativeMenu = _nativeOpenRecentMenuItem?.Menu;
        nativeMenu?.Items.Clear();

        foreach (RecentFileEntry entry in _sessionState.RecentFiles.Take(10))
        {
            string pane = entry.BroadcastPane ? "Full Broadcast" : "Squashed/Single";
            string label = $"{Path.GetFileName(entry.Path)} — {pane}";
            var item = new MenuItem { Header = label, Tag = entry };
            item.Click += OnOpenRecentClicked;
            OpenRecentMenuItem.Items.Add(item);

            var nativeItem = new NativeMenuItem { Header = label };
            nativeItem.Click += async (_, _) => await OpenRecentFileAsync(entry);
            nativeMenu?.Items.Add(nativeItem);
        }

        bool hasItems = _sessionState.RecentFiles.Count > 0;
        OpenRecentMenuItem.IsEnabled = hasItems;
        if (_nativeOpenRecentMenuItem is not null)
        {
            _nativeOpenRecentMenuItem.IsEnabled = hasItems;
        }
    }

    private async void OnOpenRecentClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: RecentFileEntry entry })
            await OpenRecentFileAsync(entry);
    }

    private async Task OpenRecentFileAsync(RecentFileEntry entry)
    {
        CaptureRecentFilePositions();
        if (!File.Exists(entry.Path))
        {
            _sessionState.RecentFiles.Remove(entry);
            RebuildOpenRecentMenus();
            SaveSessionState();
            await ShowMessageAsync("Open Recent", "The selected file no longer exists and was removed from Open Recent.");
            return;
        }

        try
        {
            await using var stream = File.OpenRead(entry.Path);
            bool actualBroadcast = entry.BroadcastPane;
            if (entry.BroadcastPane)
                await LoadBroadcastStreamAsync(stream, entry.Path);
            else
                actualBroadcast = await LoadSquashStreamAsync(stream, entry.Path);

            if (entry.Magazine is { } magazine && entry.Page is { } page && entry.Subpage is { } subpage)
            {
                if (actualBroadcast && _store.GetInstances(magazine, page, subpage).Count > 0)
                    SelectBroadcastAddress((magazine, page, subpage), entry.Version ?? 0);
                else if (!actualBroadcast && _squashStore.GetInstances(magazine, page, subpage).Count > 0)
                    SelectSquashAddress((magazine, page, subpage));
            }

            await RememberFileAsync(entry.Path, actualBroadcast);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not open recent file", ex.Message);
        }
    }

    private void CaptureSessionSelection()
    {
        CaptureRecentFilePositions();
        _sessionState.ShowX26EnhancementsSidebar = _showX26EnhancementsSidebar;
        _sessionState.ShowSquashControlCodes = SquashGrid.ShowControlCodes;
        _sessionState.ShowBroadcastControlCodes = BroadcastGrid.ShowControlCodes;
        _sessionState.ShowSquashSelectionBytes = SquashGrid.ShowSelectionBytes;
        _sessionState.ShowBroadcastSelectionBytes = BroadcastGrid.ShowSelectionBytes;
        _sessionState.ShowSquashDiacritics = SquashGrid.ShowDiacriticMarkers;
        _sessionState.ShowBroadcastDiacritics = BroadcastGrid.ShowDiacriticMarkers;
        _sessionState.SuppressFlash = SquashGrid.SuppressFlash;

        if (!string.IsNullOrWhiteSpace(_squashFilePath)
            && TryGetSquashAddress(out var squashAddress))
        {
            _sessionState.SquashMagazine = squashAddress.magazine;
            _sessionState.SquashPage = squashAddress.page;
            _sessionState.SquashSubpage = squashAddress.subpage;
        }

        if (!string.IsNullOrWhiteSpace(_broadcastFilePath)
            && TryGetBroadcastAddress(out var broadcastAddress))
        {
            _sessionState.BroadcastMagazine = broadcastAddress.magazine;
            _sessionState.BroadcastPage = broadcastAddress.page;
            _sessionState.BroadcastSubpage = broadcastAddress.subpage;
            _sessionState.BroadcastVersion = Math.Max(VersionComboBox.SelectedIndex, 0);
        }
    }

    private void SaveSessionState()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_sessionStatePath);
            if (directory is not null) Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(_sessionState, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_sessionStatePath, json);
        }
        catch { }
    }

    private async Task SaveSessionStateAsync()
    {
        string? directory = Path.GetDirectoryName(_sessionStatePath);
        if (directory is not null) Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(_sessionState, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_sessionStatePath, json);
    }

    private void OnBroadcastPreviousClicked(object? sender, RoutedEventArgs e) => NavigateBroadcast(-1);
    private void OnBroadcastNextClicked(object? sender, RoutedEventArgs e) => NavigateBroadcast(1);
    private void OnSquashPreviousClicked(object? sender, RoutedEventArgs e) => NavigateSquash(-1);
    private void OnSquashNextClicked(object? sender, RoutedEventArgs e) => NavigateSquash(1);

    private void NavigateBroadcast(int direction)
    {
        var addresses = _store.GetKnownAddresses().ToList();
        if (!TryGetBroadcastAddress(out var current) || addresses.Count == 0) return;
        int index = addresses.FindIndex(address => address == current);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= addresses.Count) return;
        SelectBroadcastAddress(addresses[target], versionIndex: 0);
    }

    private void NavigateSquash(int direction)
    {
        var addresses = _squashStore.GetKnownAddresses().ToList();
        if (!TryGetSquashAddress(out var current) || addresses.Count == 0) return;
        int index = addresses.FindIndex(address => address == current);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= addresses.Count) return;
        SelectSquashAddress(addresses[target]);
    }

    private void SelectBroadcastAddress((int magazine, int page, int subpage) address, int versionIndex)
    {
        var instances = _store.GetInstances(address.magazine, address.page, address.subpage);
        if (instances.Count == 0) return;
        versionIndex = Math.Clamp(versionIndex, 0, instances.Count - 1);

        _suppressComboEvents = true;
        MagazineComboBox.SelectedItem = address.magazine.ToString();
        PageNumberComboBox.Items.Clear();
        foreach (var page in _store.GetKnownPageNumbers(address.magazine))
            PageNumberComboBox.Items.Add(FormatPageNumberLabel(page));
        PageNumberComboBox.SelectedItem = FormatPageNumberLabel(address.page);
        SubpageComboBox.Items.Clear();
        foreach (var subpage in _store.GetKnownSubpages(address.magazine, address.page))
            SubpageComboBox.Items.Add(FormatSubpageLabel(subpage));
        SubpageComboBox.SelectedItem = FormatSubpageLabel(address.subpage);
        VersionComboBox.Items.Clear();
        for (int i = 0; i < instances.Count; i++)
            VersionComboBox.Items.Add($"v{i}");
        VersionComboBox.SelectedIndex = versionIndex;
        var selectedPage = instances[versionIndex].Page;
        PrepareBroadcastPageForDisplay(selectedPage);
        BroadcastGrid.Page = selectedPage;
        _suppressComboEvents = false;
        UpdateNavigationButtons();
        UpdateVideoBookmarkUi();
        PersistRecentFilePositions();
    }

    private void SelectSquashAddress((int magazine, int page, int subpage) address)
    {
        var instances = _squashStore.GetInstances(address.magazine, address.page, address.subpage);
        if (instances.Count == 0) return;

        _suppressComboEvents = true;
        SquashMagazineComboBox.SelectedItem = address.magazine.ToString();
        SquashPageNumberComboBox.Items.Clear();
        foreach (var page in _squashStore.GetKnownPageNumbers(address.magazine))
            SquashPageNumberComboBox.Items.Add(FormatPageNumberLabel(page));
        SquashPageNumberComboBox.SelectedItem = FormatPageNumberLabel(address.page);
        SquashSubpageComboBox.Items.Clear();
        foreach (var subpage in _squashStore.GetKnownSubpages(address.magazine, address.page))
            SquashSubpageComboBox.Items.Add(FormatSubpageLabel(subpage));
        SquashSubpageComboBox.SelectedItem = FormatSubpageLabel(address.subpage);
        _squashPage = instances[0].Page;
        SquashGrid.Page = _squashPage;
        UpdateEnhancementList(_squashPage);
        _suppressComboEvents = false;
        UpdateNavigationButtons();
        UpdateUndoToolbar();
        UpdateVideoBookmarkUi();
        PersistRecentFilePositions();
    }

    private void UpdateEnhancementList(TeletextPage? page)
    {
        EnhancementItemsControl.Items.Clear();
        _enhancementListEntries.Clear();
        _enhancementEntriesByTriplet.Clear();

        if (page is null || page.EnhancementPackets.Count == 0)
        {
            EnhancementInfoText.Text = "X/26 enhancements (0)";
            AddEnhancementListEntry("No X/26 enhancement packets on this page.");
            return;
        }

        int displayedTriplets = 0;
        int activeRow = -1;
        int activeColumn = -1;

        foreach (var packet in page.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
        {
            foreach (var triplet in packet.Triplets)
            {
                if (triplet.UncorrectableError)
                {
                    AddEnhancementListEntry(
                        $"d{packet.DesignationCode:X1} t{triplet.TripletNumber:00} · Hamming 24/18 decoding error",
                        packet.DesignationCode,
                        triplet.TripletNumber);
                    displayedTriplets++;
                    continue;
                }

                int extendedMode = triplet.ExtendedMode;
                if (extendedMode == 0x04)
                {
                    activeRow = EnhancementRow(triplet.Address);
                    activeColumn = triplet.Data;
                }
                else if (extendedMode == 0x07 && triplet.Address == 63)
                {
                    activeRow = 0;
                    activeColumn = 8;
                }
                else if (triplet.Address < 40 && extendedMode is >= 0x20 and <= 0x3F)
                {
                    activeColumn = triplet.Address;
                }

                string position = activeRow >= 0 && activeColumn >= 0
                    ? $"r{activeRow:00} c{activeColumn:00}"
                    : triplet.Address < 40
                        ? $"c{triplet.Address:00}"
                        : "global";
                string correction = triplet.CorrectedError ? " · corrected 1-bit error" : string.Empty;
                AddEnhancementListEntry(
                    $"d{packet.DesignationCode:X1} t{triplet.TripletNumber:00} · {position} · " +
                    $"{DescribeEnhancementTriplet(triplet)}{correction}",
                    packet.DesignationCode,
                    triplet.TripletNumber);
                displayedTriplets++;

                // The remaining packet bytes after this marker are padding, not enhancements.
                if (extendedMode == 0x1F && (triplet.Data & 1) != 0)
                    break;
            }
        }

        EnhancementInfoText.Text =
            $"X/26: {page.EnhancementPackets.Count} packet(s), {displayedTriplets} triplet(s)";
    }

    private void AddEnhancementListEntry(
        string text,
        int designationCode = -1,
        int tripletNumber = -1)
    {
        var entry = new EnhancementListEntry(text, designationCode, tripletNumber);
        _enhancementListEntries.Add(entry);
        EnhancementItemsControl.Items.Add(entry);
        if (designationCode >= 0 && tripletNumber >= 0)
            _enhancementEntriesByTriplet[(designationCode, tripletNumber)] = entry;
    }

    private void OnEnhancementHoverChanged(object? sender, EnhancementHoverChangedEventArgs e)
    {
        foreach (var entry in _enhancementListEntries)
            entry.IsHoverRelated = false;

        if (e.DesignationCode < 0 || e.TripletNumber < 0 || _squashPage is null)
            return;

        (int DesignationCode, int TripletNumber)? activePosition = null;
        foreach (var packet in _squashPage.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
        {
            foreach (var triplet in packet.Triplets)
            {
                if (!triplet.UncorrectableError && triplet.ExtendedMode is 0x04 or 0x07)
                    activePosition = (packet.DesignationCode, triplet.TripletNumber);

                if (packet.DesignationCode == e.DesignationCode
                    && triplet.TripletNumber == e.TripletNumber)
                {
                    if (activePosition is { } position
                        && _enhancementEntriesByTriplet.TryGetValue(position, out var positionEntry))
                        positionEntry.IsHoverRelated = true;

                    if (_enhancementEntriesByTriplet.TryGetValue(
                            (e.DesignationCode, e.TripletNumber),
                            out var characterEntry))
                        characterEntry.IsHoverRelated = true;
                    return;
                }
            }
        }
    }

    private static string DescribeEnhancementTriplet(EnhancementTriplet triplet)
    {
        int mode = triplet.ExtendedMode;
        return mode switch
        {
            0x00 => $"Full-screen colour · data 0x{triplet.Data:X2}",
            0x01 => $"Full-row colour · data 0x{triplet.Data:X2}",
            0x04 => $"Set active position (Level 1.5) · row {EnhancementRow(triplet.Address)}, column {triplet.Data}",
            0x07 => "Address display row 0 (Level 1.5)",
            >= 0x08 and <= 0x0D => $"PDC data · mode 0x{mode:X2}, data 0x{triplet.Data:X2}",
            0x10 => $"Origin modifier · data 0x{triplet.Data:X2}",
            >= 0x11 and <= 0x13 => $"Invoke object · mode 0x{mode:X2}, data 0x{triplet.Data:X2}",
            >= 0x15 and <= 0x17 => $"Define object · mode 0x{mode:X2}, data 0x{triplet.Data:X2}",
            0x18 => $"DRCS mode · data 0x{triplet.Data:X2}",
            0x1F => $"{DescribeTermination(triplet.Data)} (Level 1.5)",
            0x20 => $"Foreground colour · data 0x{triplet.Data:X2}",
            0x21 => $"G1 block-mosaic character · 0x{triplet.Data:X2}",
            0x22 => $"G3 smooth-mosaic character (Level 1.5) · 0x{triplet.Data:X2}",
            0x23 => $"Background colour · data 0x{triplet.Data:X2}",
            0x26 => $"PDC data · 0x{triplet.Data:X2}",
            0x27 => $"Additional flash function · data 0x{triplet.Data:X2}",
            0x28 => $"Modified G0/G2 character set · data 0x{triplet.Data:X2}",
            0x29 => $"G0 character · 0x{triplet.Data:X2}",
            0x2B => $"G3 smooth-mosaic character (Level 2.5) · 0x{triplet.Data:X2}",
            0x2C => $"Display attributes · data 0x{triplet.Data:X2}",
            0x2D => $"DRCS character · 0x{triplet.Data:X2}",
            0x2E => $"Font style (Level 3.5) · data 0x{triplet.Data:X2}",
            0x2F => $"G2 supplementary character (Level 1.5) · 0x{triplet.Data:X2}",
            >= 0x30 and <= 0x3F => DescribeDiacritical(triplet.Data, mode - 0x30),
            _ => $"Reserved mode 0x{mode:X2} · data 0x{triplet.Data:X2}"
        };
    }

    private static string DescribeDiacritical(int characterCode, int diacritical)
    {
        string[] names =
        [
            "no diacritical", "grave", "acute", "circumflex", "tilde", "macron", "breve", "dot above",
            "diaeresis", "dot below", "ring", "cedilla", "underscore", "double acute", "ogonek", "caron"
        ];
        string[] combiningMarks =
        [
            "", "\u0300", "\u0301", "\u0302", "\u0303", "\u0304", "\u0306", "\u0307",
            "\u0308", "\u0323", "\u030A", "\u0327", "\u0332", "\u030B", "\u0328", "\u030C"
        ];

        string source = characterCode is >= 0x20 and <= 0x7E
            ? ((char)characterCode).ToString()
            : $"0x{characterCode:X2}";
        string result = characterCode is >= 0x20 and <= 0x7E
            ? (source + combiningMarks[diacritical]).Normalize()
            : source;
        return $"G0 character {names[diacritical]} (Level 1.5) · {source} → {result}";
    }

    private static int EnhancementRow(int address) => address == 40 ? 24 : address - 40;

    private static string DescribeTermination(int data) => (data & 0x07) switch
    {
        0 => "End object; more objects follow",
        1 => "End last object; intermediate object data follow",
        2 => "End object on last page; more objects follow",
        3 => "End last object on last page",
        4 => "End local object; more local objects follow",
        5 => "End last local object",
        6 => "End enhancement data; local objects follow",
        _ => "End enhancement data; no local objects follow"
    };

    private async void OnJumpToBroadcastClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSquashAddress(out var address)) return;
        if (_store.GetInstances(address.magazine, address.page, address.subpage).Count == 0)
        {
            await ShowPageNotFoundAsync(address, "full broadcast");
            return;
        }
        SelectBroadcastAddress(address, versionIndex: 0);
    }

    private async void OnJumpToSquashClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryGetBroadcastAddress(out var address)) return;
        if (_squashStore.GetInstances(address.magazine, address.page, address.subpage).Count == 0)
        {
            await ShowPageNotFoundAsync(address, "squashed capture");
            return;
        }
        SelectSquashAddress(address);
    }

    private async Task ShowPageNotFoundAsync(
        (int magazine, int page, int subpage) address,
        string target)
    {
        var closeButton = new Button
        {
            Content = "OK",
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var dialog = new Window
        {
            Title = "Page not found",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Page {address.magazine}{address.page:X2}, subpage {address.subpage:X4}, does not exist in the loaded {target}.",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    closeButton,
                }
            }
        };
        closeButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private bool TryGetBroadcastAddress(out (int magazine, int page, int subpage) address)
    {
        address = default;
        if (!TryGetSelectedMagazine(out var magazine)
            || !TryGetSelectedPageNumber(out var page)
            || !TryGetSelectedSubpage(out var subpage)) return false;
        address = (magazine, page, subpage);
        return true;
    }

    private bool TryGetSquashAddress(out (int magazine, int page, int subpage) address)
    {
        address = default;
        if (!TryGetSelectedSquashMagazine(out var magazine)
            || !TryGetSelectedSquashPageNumber(out var page)
            || !TryGetSelectedSquashSubpage(out var subpage)) return false;
        address = (magazine, page, subpage);
        return true;
    }

    private void UpdateNavigationButtons()
    {
        var broadcastAddresses = _store.GetKnownAddresses().ToList();
        int broadcastIndex = TryGetBroadcastAddress(out var broadcastAddress)
            ? broadcastAddresses.FindIndex(address => address == broadcastAddress)
            : -1;
        BroadcastPreviousButton.IsEnabled = broadcastIndex > 0;
        BroadcastNextButton.IsEnabled = broadcastIndex >= 0 && broadcastIndex < broadcastAddresses.Count - 1;

        var squashAddresses = _squashStore.GetKnownAddresses().ToList();
        int squashIndex = TryGetSquashAddress(out var squashAddress)
            ? squashAddresses.FindIndex(address => address == squashAddress)
            : -1;
        SquashPreviousButton.IsEnabled = squashIndex > 0;
        SquashNextButton.IsEnabled = squashIndex >= 0 && squashIndex < squashAddresses.Count - 1;
        UpdateRestorationProgress(squashAddresses.Count, squashIndex);

        SquashJumpToBroadcastButton.IsEnabled = _broadcastFileOpen;
        BroadcastJumpToSquashButton.IsEnabled = _squashFileOpen;
        UpdateSquashAddressToolbarVisibility();
    }

    private void UpdateRestorationProgress(int totalAddresses, int currentIndex)
    {
        int completed = currentIndex >= 0 ? currentIndex + 1 : 0;
        SquashRestorationProgressBar.Maximum = Math.Max(totalAddresses, 1);
        SquashRestorationProgressBar.Value = Math.Clamp(completed, 0, totalAddresses);

        int percentage = totalAddresses > 0
            ? (int)Math.Round(completed * 100.0 / totalAddresses)
            : 0;
        SquashRestorationProgressText.Text =
            $"{completed} / {totalAddresses}  ({percentage}%)";
    }

    private void UpdateSquashAddressToolbarVisibility()
    {
        bool hasMultipleSquashAddresses = _squashStore.GetKnownAddresses().Take(2).Count() > 1;
        SquashToolbar.IsVisible = hasMultipleSquashAddresses;
    }

    private void UpdateWorkspacePaneVisibility()
    {
        bool broadcastVisible = BroadcastPaneGrid.IsVisible;
        bool broadcastOnly = broadcastVisible && !_squashPaneEstablished;
        bool transferVisible = broadcastVisible && _squashPaneEstablished;
        bool squashVisible = !broadcastOnly;

        SquashPaneGrid.IsVisible = squashVisible;
        TransferPaneGrid.IsVisible = transferVisible;
        UpdateWindowAndPaneTitles();
        ApplyX26EnhancementsSidebarVisibility(resizeWindow: false);
        ApplyVideoBookmarkSidebarVisibility(resizeWindow: false);
        FitWindowToContent();

        if (broadcastOnly)
        {
            SquashGrid.IsActive = false;
            SquashGrid.ClearSelection();
            BroadcastGrid.IsActive = true;
        }
    }

    // ---- Full broadcast stream: Magazine -> Page -> Subpage -> Version cascade --

    private void PopulatePageCombo()
    {
        _suppressComboEvents = true;

        MagazineComboBox.Items.Clear();
        foreach (var magazine in _store.GetKnownMagazines())
            MagazineComboBox.Items.Add(magazine.ToString());

        _suppressComboEvents = false;

        if (MagazineComboBox.Items.Count > 0)
            MagazineComboBox.SelectedIndex = 0; // triggers OnMagazineComboChanged, cascades down
        else
            UpdateNavigationButtons();
    }

    private void PopulateSquashPageCombo()
    {
        _suppressComboEvents = true;

        SquashMagazineComboBox.Items.Clear();
        foreach (var magazine in _squashStore.GetKnownMagazines())
            SquashMagazineComboBox.Items.Add(magazine.ToString());

        _suppressComboEvents = false;

        if (SquashMagazineComboBox.Items.Count > 0)
            SquashMagazineComboBox.SelectedIndex = 0; // cascades to OnSquashMagazineComboChanged
        else
            UpdateNavigationButtons();
    }

    private void OnMagazineComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (!TryGetSelectedMagazine(out var magazine)) return;

        _suppressComboEvents = true;
        PageNumberComboBox.Items.Clear();
        foreach (var page in _store.GetKnownPageNumbers(magazine))
            PageNumberComboBox.Items.Add(FormatPageNumberLabel(page));
        _suppressComboEvents = false;

        if (PageNumberComboBox.Items.Count > 0)
            PageNumberComboBox.SelectedIndex = 0; // cascades to OnPageNumberComboChanged
    }

    private void OnPageNumberComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (!TryGetSelectedMagazine(out var magazine)) return;
        if (!TryGetSelectedPageNumber(out var page)) return;

        _suppressComboEvents = true;
        SubpageComboBox.Items.Clear();
        foreach (var subpage in _store.GetKnownSubpages(magazine, page))
            SubpageComboBox.Items.Add(FormatSubpageLabel(subpage));
        _suppressComboEvents = false;

        if (SubpageComboBox.Items.Count > 0)
            SubpageComboBox.SelectedIndex = 0; // cascades to OnSubpageComboChanged
    }

    private void OnSubpageComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (!TryGetSelectedMagazine(out var magazine)) return;
        if (!TryGetSelectedPageNumber(out var page)) return;
        if (!TryGetSelectedSubpage(out var subpage)) return;

        var instances = _store.GetInstances(magazine, page, subpage);

        _suppressComboEvents = true;
        VersionComboBox.Items.Clear();
        for (int i = 0; i < instances.Count; i++)
            VersionComboBox.Items.Add($"v{i}");
        _suppressComboEvents = false;

        if (VersionComboBox.Items.Count > 0)
            VersionComboBox.SelectedIndex = 0; // cascades to OnVersionComboChanged
    }

    private void OnVersionComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (!TryGetSelectedMagazine(out var magazine)) return;
        if (!TryGetSelectedPageNumber(out var page)) return;
        if (!TryGetSelectedSubpage(out var subpage)) return;
        if (VersionComboBox.SelectedIndex < 0) return;

        var instances = _store.GetInstances(magazine, page, subpage);
        if (VersionComboBox.SelectedIndex >= instances.Count) return;

        var selectedPage = instances[VersionComboBox.SelectedIndex].Page;
        PrepareBroadcastPageForDisplay(selectedPage);
        BroadcastGrid.Page = selectedPage;
        UpdateNavigationButtons();
        UpdateVideoBookmarkUi();
        PersistRecentFilePositions();
    }

    private void PrepareBroadcastPageForDisplay(TeletextPage page)
    {
        if (BroadcastGrid.ShowDiacriticMarkers)
            EnsureBroadcastEnhancementsLoaded(page);
    }

    private void EnsureBroadcastEnhancementsLoaded(TeletextPage page)
    {
        if (!_broadcastEnhancementsScanned.Add(page)) return;

        int headerPacketIndex = page.RawRowPacketIndices[0];
        if (headerPacketIndex < 0 || headerPacketIndex >= _broadcastPackets.Count) return;

        for (int packetIndex = headerPacketIndex + 1; packetIndex < _broadcastPackets.Count; packetIndex++)
        {
            byte[] rawPacket = _broadcastPackets[packetIndex];
            var low = Hamming.Decode84(rawPacket[0]);
            var high = Hamming.Decode84(rawPacket[1]);
            if (low.UncorrectableError || high.UncorrectableError) continue;

            int mrag = low.Value | (high.Value << 4);
            int row = (mrag >> 3) & 0x1F;
            int magazineBits = mrag & 0x07;
            int magazine = magazineBits == 0 ? 8 : magazineBits;
            if (magazine != page.Magazine) continue;
            if (row == 0) break;
            if (row != 26) continue;

            var enhancement = PageAssembler.DecodeEnhancementPacket(rawPacket, packetIndex);
            if (enhancement is not null)
                page.EnhancementPackets.Add(enhancement);
        }

        if (page.EnhancementPackets.Count > 0)
            PageAssembler.ApplyLevel15Enhancements(page);
    }

    private bool TryGetSelectedMagazine(out int magazine)
    {
        magazine = 0;
        return MagazineComboBox.SelectedItem is string s && int.TryParse(s, out magazine);
    }

    private bool TryGetSelectedPageNumber(out int page)
    {
        page = 0;
        return PageNumberComboBox.SelectedItem is string s
            && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out page);
    }

    private bool TryGetSelectedSubpage(out int subpage)
    {
        subpage = 0;
        return SubpageComboBox.SelectedItem is string s && TryParseSubpageLabel(s, out subpage);
    }

    // ---- Recovered squash: not wired to a store yet - "Open squashed..." and the
    // actual squash-building workflow (transfer buttons in the middle column) come
    // later. These handlers exist now purely so the XAML's SelectionChanged bindings
    // compile; they intentionally do nothing yet.

    private void OnSquashMagazineComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (!TryGetSelectedSquashMagazine(out var magazine)) return;

        _suppressComboEvents = true;
        SquashPageNumberComboBox.Items.Clear();
        foreach (var page in _squashStore.GetKnownPageNumbers(magazine))
            SquashPageNumberComboBox.Items.Add(FormatPageNumberLabel(page));
        _suppressComboEvents = false;

        if (SquashPageNumberComboBox.Items.Count > 0)
            SquashPageNumberComboBox.SelectedIndex = 0; // cascades to OnSquashPageNumberComboChanged
    }

    private void OnSquashPageNumberComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (!TryGetSelectedSquashMagazine(out var magazine)) return;
        if (!TryGetSelectedSquashPageNumber(out var page)) return;

        _suppressComboEvents = true;
        SquashSubpageComboBox.Items.Clear();
        foreach (var subpage in _squashStore.GetKnownSubpages(magazine, page))
            SquashSubpageComboBox.Items.Add(FormatSubpageLabel(subpage));
        _suppressComboEvents = false;

        if (SquashSubpageComboBox.Items.Count > 0)
            SquashSubpageComboBox.SelectedIndex = 0; // cascades to OnSquashSubpageComboChanged
    }

    private void OnSquashSubpageComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (!TryGetSelectedSquashMagazine(out var magazine)) return;
        if (!TryGetSelectedSquashPageNumber(out var page)) return;
        if (!TryGetSelectedSquashSubpage(out var subpage)) return;

        var instances = _squashStore.GetInstances(magazine, page, subpage);
        if (instances.Count > 0)
        {
            _suppressComboEvents = true;
            // No version combo for squash - just use the first instance
            _squashPage = instances[0].Page;
            SquashGrid.Page = _squashPage;
            UpdateEnhancementList(_squashPage);
            _suppressComboEvents = false;
        }
        UpdateNavigationButtons();
        UpdateUndoToolbar();
        UpdateVideoBookmarkUi();
        PersistRecentFilePositions();
    }

    private bool TryGetSelectedSquashMagazine(out int magazine)
    {
        magazine = 0;
        return SquashMagazineComboBox.SelectedItem is string s && int.TryParse(s, out magazine);
    }

    private bool TryGetSelectedSquashPageNumber(out int page)
    {
        page = 0;
        return SquashPageNumberComboBox.SelectedItem is string s
            && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out page);
    }

    private bool TryGetSelectedSquashSubpage(out int subpage)
    {
        subpage = 0;
        return SquashSubpageComboBox.SelectedItem is string s && TryParseSubpageLabel(s, out subpage);
    }

    private void OnSquashVersionComboChanged(object? sender, SelectionChangedEventArgs e) { }

    // ---- Label formatting (kept in one place so parsing always matches formatting) --

    private static string FormatPageNumberLabel(int page) => $"{page:X2}";

    private static string FormatSubpageLabel(int subpage) => $"{subpage:X4}";

    private static bool TryParseSubpageLabel(string label, out int subpage) =>
        int.TryParse(label, System.Globalization.NumberStyles.HexNumber, null, out subpage);

    // ---- Menu handlers -------------------------------------------------------------

    private async void OnNewPageClicked(object? sender, RoutedEventArgs e) =>
        await CreateNewPageAsync();

    private async Task CreateNewPageAsync()
    {
        var choice = await ShowNewPageDialogAsync();
        if (choice is not { } address) return;

        if (_squashStore.GetInstances(address.magazine, address.page, address.subpage).Count > 0)
        {
            await ShowPageAlreadyExistsAsync(address);
            return;
        }

        AddBlankSquashPage(address.magazine, address.page, address.subpage);
        _squashPaneEstablished = true;
        _structuralDirty = true;
        ShowSquashEditor();
        PopulateSquashPageCombo();
        SelectSquashAddress(address);
        UpdateSquashFileFooter();
        UpdateDirtyFromHistories();
    }

    private async void OnDeletePageClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSquashAddress(out var current)
            || _squashStore.GetInstances(current.magazine, current.page, current.subpage).Count == 0)
            return;

        if (!await ConfirmDeletePageAsync(current)) return;

        var oldAddresses = _squashStore.GetKnownAddresses().ToList();
        int oldIndex = oldAddresses.FindIndex(address => address == current);
        var removed = _squashStore.RemoveAddress(current.magazine, current.page, current.subpage);
        foreach (var instance in removed)
        {
            MarkSourcePacketsDeleted(instance.Page);
            _pageHistories.Remove(instance.Page);
        }

        _structuralDirty = true;
        if (_squashStore.TotalInstanceCount == 0)
        {
            AddBlankSquashPage(1, 0x00, 0x0000);
            PopulateSquashPageCombo();
            SelectSquashAddress((1, 0x00, 0x0000));
        }
        else
        {
            var target = oldIndex > 0 ? oldAddresses[oldIndex - 1] : oldAddresses[1];
            PopulateSquashPageCombo();
            SelectSquashAddress(target);
        }

        UpdateDirtyFromHistories();
        UpdateSquashFileFooter();
        UpdateUndoToolbar();
        UpdateNavigationButtons();
    }

    private void ShowSquashEditor()
    {
        _squashFileOpen = true;
        SquashInfoText.IsVisible = true;
        SquashEditToolbar.IsVisible = true;
        SquashGrid.IsActive = true;
        BroadcastGrid.IsActive = false;
        UpdateWorkspacePaneVisibility();
        UpdateSquashAddressToolbarVisibility();
        FitWindowToContent();
    }

    private void InitializeBlankSquashDocument()
    {
        _squashPage = AddBlankSquashPage(1, 0x00, 0x0000);
        PopulateSquashPageCombo();
        SelectSquashAddress((1, 0x00, 0x0000));
        ShowSquashEditor();
        _structuralDirty = false;
        MarkHistoriesSaved();
        UpdateSquashFileFooter();
    }

    private TeletextPage AddBlankSquashPage(int magazine, int pageNumber, int subpage)
    {
        var page = new TeletextPage
        {
            Magazine = magazine,
            PageNumber = pageNumber,
            SubPage = subpage,
        };
        var instance = new PageInstance
        {
            Magazine = magazine,
            PageNumber = pageNumber,
            Subpage = subpage,
            Page = page,
        };

        for (int row = 0; row < 25; row++)
            PageAssembler.ApplyRow(page, row, CreateBlankPacket(page, row));

        _squashStore.AddInstance(instance);
        EnsurePageHistory(page);
        return page;
    }

    private async Task<(int magazine, int page, int subpage)?> ShowNewPageDialogAsync()
    {
        var pageInput = new NumericUpDown
        {
            Minimum = 0x100,
            Maximum = 0x8FF,
            Increment = 1,
            Value = 0x100,
            Width = 130,
            TextConverter = new HexNumericConverter(3),
        };
        var subpageInput = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 0x1FFF,
            Increment = 1,
            Value = 0,
            Width = 130,
            TextConverter = new HexNumericConverter(4, packedSubpage: true),
        };
        var okButton = new Button { Content = "Add", Width = 90 };
        var cancelButton = new Button { Content = "Cancel", Width = 90 };
        (int magazine, int page, int subpage)? result = null;

        var dialog = new Window
        {
            Title = "New teletext page",
            Width = 390,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "Enter hexadecimal page and subpage numbers." },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        RowDefinitions = new RowDefinitions("Auto,Auto"),
                        RowSpacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "Page (100–8FF)", VerticalAlignment = VerticalAlignment.Center },
                            pageInput,
                            new TextBlock { Text = "Subpage (0000–3F7F)", VerticalAlignment = VerticalAlignment.Center, [Grid.RowProperty] = 1 },
                            subpageInput,
                        }
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, okButton },
                    }
                }
            }
        };
        Grid.SetColumn(pageInput, 1);
        Grid.SetColumn(subpageInput, 1);
        Grid.SetRow(subpageInput, 1);
        okButton.Click += (_, _) =>
        {
            int combined = decimal.ToInt32(pageInput.Value ?? 0x100);
            result = ((combined >> 8) & 0x0F, combined & 0xFF,
                HexNumericConverter.PackSubpage(subpageInput.Value ?? 0));
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return result;
    }

    private async Task ShowPageAlreadyExistsAsync((int magazine, int page, int subpage) address)
    {
        await ShowMessageAsync(
            "Page already exists",
            $"Page {address.magazine}{address.page:X2}, subpage {address.subpage:X4}, already exists.");
    }

    private async Task<bool> ConfirmDeletePageAsync((int magazine, int page, int subpage) address)
    {
        bool confirmed = false;
        var yesButton = new Button { Content = "Yes", Width = 90 };
        var noButton = new Button { Content = "No", Width = 90 };
        var dialog = new Window
        {
            Title = "Delete teletext page",
            Width = 430,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Permanently delete page {address.magazine}{address.page:X2}, subpage {address.subpage:X4}?",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { noButton, yesButton },
                    }
                }
            }
        };
        yesButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        noButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return confirmed;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var closeButton = new Button
        {
            Content = "OK",
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    closeButton,
                }
            }
        };
        closeButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private void MarkSourcePacketsDeleted(TeletextPage page)
    {
        int headerIndex = page.RawRowPacketIndices[0];
        if (headerIndex < 0 || headerIndex >= _squashPackets.Count) return;

        for (int index = headerIndex; index < _squashPackets.Count; index++)
        {
            if (!TryDecodePacketAddress(_squashPackets[index], out int magazine, out int row))
                continue;
            if (index > headerIndex && magazine == page.Magazine && row == 0)
                break;
            if (magazine == page.Magazine)
                _deletedSquashPacketIndices.Add(index);
        }
    }

    private static bool TryDecodePacketAddress(byte[] packet, out int magazine, out int row)
    {
        magazine = row = 0;
        if (packet.Length != 42) return false;
        var low = Hamming.Decode84(packet[0]);
        var high = Hamming.Decode84(packet[1]);
        if (low.UncorrectableError || high.UncorrectableError) return false;
        int address = low.Value | (high.Value << 4);
        row = (address >> 3) & 0x1F;
        int magazineBits = address & 0x07;
        magazine = magazineBits == 0 ? 8 : magazineBits;
        return true;
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e) => await SaveSquashAsync(forcePicker: false);
    private async void OnSaveAsClicked(object? sender, RoutedEventArgs e) => await SaveSquashAsync(forcePicker: true);

    private static string? FindFfmpegExecutable()
    {
        string executableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(OperatingSystem.IsMacOS()
                ? new[] { "/opt/homebrew/bin", "/usr/local/bin" }
                : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal);

        foreach (string directory in directories)
        {
            try
            {
                string candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private async Task<List<VideoEncoderChoice>> GetFfmpegVideoEncodersAsync()
    {
        if (_ffmpegPath is null) return new List<VideoEncoderChoice>();
        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-encoders");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start FFmpeg.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = (await stdoutTask) + "\n" + (await stderrTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(output.Trim());

        return output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 7 && line[0] == 'V')
            .Select(line => line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[1] != "=")
            .Select(parts => new VideoEncoderChoice(parts[1], parts.Length > 2 ? parts[2].Trim() : string.Empty))
            .GroupBy(encoder => encoder.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(encoder => encoder.Name, StringComparer.Ordinal)
            .ToList();
    }

    private List<TeletextPage> GetVideoExportPages(out TeletextGridControl grid)
    {
        bool useBroadcast = BroadcastGrid.IsActive
            || (!SquashPaneGrid.IsVisible && BroadcastPaneGrid.IsVisible);
        if (useBroadcast)
        {
            grid = BroadcastGrid;
            return _store.GetKnownAddresses()
                .SelectMany(address => _store.GetInstances(address.magazine, address.page, address.subpage))
                .Select(instance => instance.Page)
                .ToList();
        }

        grid = SquashGrid;
        return _squashStore.GetKnownAddresses()
            .Select(address => _squashStore.GetInstances(address.magazine, address.page, address.subpage))
            .Where(instances => instances.Count > 0)
            .Select(instances => instances[0].Page)
            .ToList();
    }

    private async void OnExportVideoClicked(object? sender, RoutedEventArgs e)
    {
        if (_ffmpegPath is null)
        {
            await ShowMessageAsync("Export video", "FFmpeg was not found in PATH.");
            return;
        }

        List<TeletextPage> pages = GetVideoExportPages(out TeletextGridControl grid);
        if (pages.Count == 0)
        {
            await ShowMessageAsync("Export video", "There are no pages to export.");
            return;
        }

        List<VideoEncoderChoice> encoders;
        try
        {
            encoders = await GetFfmpegVideoEncodersAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("FFmpeg encoder detection failed", ex.Message);
            return;
        }
        if (encoders.Count == 0)
        {
            await ShowMessageAsync("Export video", "FFmpeg did not report any video encoders.");
            return;
        }

        string[] popularEncoderNames =
        [
            "libx264", "h264_videotoolbox",
            "libx265", "hevc_videotoolbox",
            "libsvtav1", "libaom-av1", "av1_videotoolbox",
            "libvpx-vp9", "prores_ks", "mpeg4",
        ];
        var encodersByName = encoders.ToDictionary(encoder => encoder.Name, StringComparer.Ordinal);
        List<VideoEncoderChoice> offeredEncoders = popularEncoderNames
            .Where(encodersByName.ContainsKey)
            .Select(name => encodersByName[name])
            .Take(10)
            .ToList();
        if (offeredEncoders.Count == 0)
            offeredEncoders = encoders.Take(10).ToList();

        var encoderCombo = new ComboBox { ItemsSource = offeredEncoders, Width = 390 };
        encoderCombo.SelectedItem = offeredEncoders.FirstOrDefault(encoder =>
                string.Equals(encoder.Name, _sessionState.VideoEncoder, StringComparison.Ordinal))
            ?? offeredEncoders[0];
        var durationInput = new NumericUpDown
        {
            Minimum = 0.5m,
            Maximum = 60m,
            Increment = 0.5m,
            Value = (decimal)Math.Clamp(_sessionState.VideoSecondsPerPage ?? 5.0, 0.5, 60.0),
            Width = 135,
        };
        var animateFlashCheck = new CheckBox
        {
            Content = "Animate flashing content",
            IsChecked = _sessionState.VideoAnimateFlash ?? true,
        };
        var resolutionCombo = new ComboBox
        {
            ItemsSource = new[] { "Original (600 px high)", "HD (1080 px high)" },
            SelectedIndex = Math.Clamp(_sessionState.VideoResolutionIndex ?? 0, 0, 1),
            Width = 190,
        };
        var aspectCombo = new ComboBox
        {
            ItemsSource = new[] { "Original aspect (16:15)", "Stretch to 4:3" },
            SelectedIndex = Math.Clamp(_sessionState.VideoAspectIndex ?? 0, 0, 1),
            Width = 190,
        };
        var exportButton = new Button { Content = "Choose output…", Width = 120, IsDefault = true };
        var cancelButton = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        (VideoEncoderChoice Encoder, double Seconds, bool AnimateFlash, int Width, int Height)? settings = null;
        var settingsDialog = new Window
        {
            Title = "Export teletext video",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = $"Exporting {pages.Count} page(s) with FFmpeg." },
                    new StackPanel { Spacing = 5, Children = { new TextBlock { Text = "Video encoder" }, encoderCombo } },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "Seconds per page", VerticalAlignment = VerticalAlignment.Center },
                            durationInput,
                        }
                    },
                    animateFlashCheck,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new StackPanel { Spacing = 5, Children = { new TextBlock { Text = "Resolution" }, resolutionCombo } },
                            new StackPanel { Spacing = 5, Children = { new TextBlock { Text = "Aspect ratio" }, aspectCombo } },
                        }
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, exportButton },
                    }
                }
            }
        };
        exportButton.Click += (_, _) =>
        {
            if (encoderCombo.SelectedItem is not VideoEncoderChoice encoder) return;
            bool hd = resolutionCombo.SelectedIndex == 1;
            bool fourByThree = aspectCombo.SelectedIndex == 1;
            int height = hd ? 1080 : 600;
            int width = fourByThree ? (hd ? 1440 : 800) : (hd ? 1152 : 640);
            settings = (
                encoder,
                decimal.ToDouble(durationInput.Value ?? 5m),
                animateFlashCheck.IsChecked == true,
                width,
                height);
            _sessionState.VideoEncoder = encoder.Name;
            _sessionState.VideoSecondsPerPage = settings.Value.Seconds;
            _sessionState.VideoAnimateFlash = settings.Value.AnimateFlash;
            _sessionState.VideoResolutionIndex = resolutionCombo.SelectedIndex;
            _sessionState.VideoAspectIndex = aspectCombo.SelectedIndex;
            SaveSessionState();
            settingsDialog.Close();
        };
        cancelButton.Click += (_, _) => settingsDialog.Close();
        await settingsDialog.ShowDialog(this);
        if (settings is not { } selected) return;

        using var outputFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save teletext video",
            SuggestedFileName = "teletext-video.mkv",
            DefaultExtension = "mkv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Matroska video")
                {
                    Patterns = new[] { "*.mkv" },
                    MimeTypes = new[] { "video/x-matroska" },
                },
                FilePickerFileTypes.All,
            }
        });
        if (outputFile is null) return;
        if (!outputFile.Path.IsFile)
        {
            await ShowMessageAsync("Export video", "FFmpeg export requires a local output file.");
            return;
        }

        await ExportVideoWithFfmpegAsync(
            grid,
            pages,
            selected.Encoder.Name,
            selected.Seconds,
            selected.AnimateFlash,
            selected.Width,
            selected.Height,
            GetCurrentVideoBookmarks(grid),
            outputFile.Path.LocalPath);
    }

    private IReadOnlyList<VideoBookmarkEntry> GetCurrentVideoBookmarks(TeletextGridControl grid)
    {
        if (grid == BroadcastGrid) return Array.Empty<VideoBookmarkEntry>();
        string? path = grid == BroadcastGrid ? _broadcastFilePath : _squashFilePath;
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<VideoBookmarkEntry>();
        RecentFileEntry? file = _sessionState.RecentFiles.FirstOrDefault(entry =>
            string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
        return file?.VideoBookmarks is { } bookmarks
            ? bookmarks
            : Array.Empty<VideoBookmarkEntry>();
    }

    private async Task ExportVideoWithFfmpegAsync(
        TeletextGridControl grid,
        IReadOnlyList<TeletextPage> pages,
        string encoder,
        double secondsPerPage,
        bool animateFlash,
        int outputWidth,
        int outputHeight,
        IReadOnlyList<VideoBookmarkEntry> bookmarks,
        string outputPath)
    {
        const double flashPhaseSeconds = 0.5;
        double totalDuration = secondsPerPage * pages.Count;
        int totalImages = pages.Count * (animateFlash ? 2 : 1);
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"teletext-video-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        var progressText = new TextBlock { Text = "Rendering video frames…", HorizontalAlignment = HorizontalAlignment.Center };
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Width = 400, Height = 18 };
        var abortButton = new Button { Content = "Abort", Width = 90, HorizontalAlignment = HorizontalAlignment.Right };
        using var cancellation = new CancellationTokenSource();
        abortButton.Click += (_, _) =>
        {
            abortButton.IsEnabled = false;
            abortButton.Content = "Aborting…";
            cancellation.Cancel();
        };
        var progressDialog = new Window
        {
            Title = "Exporting video",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 14,
                Children = { progressText, progressBar, abortButton },
            }
        };
        progressDialog.Show(this);

        try
        {
            // Give the native window/compositor a chance to paint the dialog before
            // the first synchronous PNG render starts.
            await Task.Delay(50, cancellation.Token);
            int renderedImages = 0;
            var pageImages = new List<(string Visible, string? Hidden)>();
            for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                TeletextPage page = pages[pageIndex];
                string visiblePath = Path.Combine(tempDirectory, $"page-{pageIndex:D5}-visible.png");
                progressText.Text = $"Creating frame {renderedImages + 1} of {totalImages}…";
                await Task.Delay(1, cancellation.Token);
                await using (var stream = File.Create(visiblePath))
                {
                    grid.SaveScreenshotPng(stream, page, animateFlash: animateFlash, flashVisible: true);
                    await stream.FlushAsync();
                }
                renderedImages++;
                progressBar.Value = renderedImages * 50.0 / totalImages;

                string? hiddenPath = null;
                if (animateFlash)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    hiddenPath = Path.Combine(tempDirectory, $"page-{pageIndex:D5}-hidden.png");
                    progressText.Text = $"Creating frame {renderedImages + 1} of {totalImages}…";
                    await Task.Delay(1, cancellation.Token);
                    await using var stream = File.Create(hiddenPath);
                    grid.SaveScreenshotPng(stream, page, animateFlash: true, flashVisible: false);
                    await stream.FlushAsync();
                    renderedImages++;
                    progressBar.Value = renderedImages * 50.0 / totalImages;
                }
                pageImages.Add((visiblePath, hiddenPath));
                await Task.Delay(1, cancellation.Token);
            }

            var concat = new StringBuilder();
            string? lastImage = null;
            foreach (var images in pageImages)
            {
                double remaining = secondsPerPage;
                int phase = 0;
                while (remaining > 0.0001)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    string image = animateFlash && phase % 2 == 1 ? images.Hidden! : images.Visible;
                    double segmentDuration = animateFlash
                        ? Math.Min(flashPhaseSeconds, remaining)
                        : remaining;
                    concat.AppendLine($"file '{image}'");
                    concat.AppendLine($"duration {segmentDuration.ToString("0.###", CultureInfo.InvariantCulture)}");
                    lastImage = image;
                    remaining -= segmentDuration;
                    phase++;
                }
            }
            // The concat demuxer ignores the final duration unless the last image
            // is repeated once without a duration directive.
            concat.AppendLine($"file '{lastImage}'");
            string concatPath = Path.Combine(tempDirectory, "timeline.ffconcat");
            await File.WriteAllTextAsync(concatPath, concat.ToString(), cancellation.Token);

            progressText.Text = $"Encoding with {encoder}…";
            var startInfo = new ProcessStartInfo(_ffmpegPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in new[]
            {
                "-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0",
                "-i", concatPath, "-r", "2", "-vf", $"scale={outputWidth}:{outputHeight}:flags=lanczos", "-c:v", encoder,
                "-pix_fmt", "yuv420p", "-progress", "pipe:1", "-nostats", outputPath,
            })
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start FFmpeg.");
            using var cancellationRegistration = cancellation.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }
            });
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if ((line.StartsWith("out_time_us=", StringComparison.Ordinal)
                        || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                    && long.TryParse(line.AsSpan(12), out long elapsedMicroseconds))
                {
                    double elapsedSeconds = elapsedMicroseconds / 1_000_000.0;
                    progressBar.Value = 50 + Math.Min(elapsedSeconds / totalDuration, 1.0) * 50;
                    progressText.Text = $"Encoding {Math.Min(elapsedSeconds, totalDuration):0.0} of {totalDuration:0.0} seconds…";
                }
            }
            await process.WaitForExitAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            string error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "FFmpeg encoding failed." : error.Trim());

            progressDialog.Close();
            string bookmarkText = BuildVideoBookmarkText(pages, bookmarks, secondsPerPage);
            await ShowVideoExportCompleteAsync(outputPath, bookmarkText);
        }
        catch (OperationCanceledException)
        {
            progressDialog.Close();
            try { File.Delete(outputPath); } catch { }
            await ShowMessageAsync("Video export aborted", "The video export was cancelled.");
        }
        catch (Exception ex)
        {
            progressDialog.Close();
            await ShowMessageAsync("Video export failed", ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    private static string BuildVideoBookmarkText(
        IReadOnlyList<TeletextPage> pages,
        IReadOnlyList<VideoBookmarkEntry> bookmarks,
        double secondsPerPage)
    {
        var lines = new List<(int Seconds, string Text)>();
        foreach (VideoBookmarkEntry bookmark in bookmarks)
        {
            int pageIndex = pages.ToList().FindIndex(page =>
                page.Magazine == bookmark.Magazine
                && page.PageNumber == bookmark.Page
                && page.SubPage == bookmark.Subpage);
            if (pageIndex < 0 || string.IsNullOrWhiteSpace(bookmark.Name)) continue;
            int seconds = Math.Max(0, (int)Math.Round(pageIndex * secondsPerPage));
            TimeSpan time = TimeSpan.FromSeconds(seconds);
            string timestamp = time.TotalHours >= 1
                ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{time.Minutes:00}:{time.Seconds:00}";
            string address = $"{bookmark.Magazine}{bookmark.Page:X2}";
            if (bookmark.Subpage != 0) address += $"-{bookmark.Subpage:X4}";
            lines.Add((seconds, $"{timestamp} ({address}) - {bookmark.Name}"));
        }
        return string.Join(Environment.NewLine, lines.OrderBy(line => line.Seconds).Select(line => line.Text));
    }

    private async Task ShowVideoExportCompleteAsync(string outputPath, string bookmarkText)
    {
        var closeButton = new Button { Content = "OK", Width = 80, IsDefault = true };
        var bookmarkBox = new TextBox
        {
            Text = bookmarkText,
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Width = 560,
            MinHeight = 120,
            MaxHeight = 300,
        };
        var dialog = new Window
        {
            Title = "Video export complete",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = $"Video saved to:\n{outputPath}", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = "Page bookmarks (select and copy):", FontWeight = FontWeight.SemiBold },
                    bookmarkBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { closeButton },
                    },
                }
            }
        };
        closeButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async void OnExportScreenshotClicked(object? sender, RoutedEventArgs e)
    {
        TeletextGridControl? grid = IsActiveGrid();
        if (grid?.Page is not { } page)
        {
            await ShowMessageAsync("Export screenshot", "There is no current page to export.");
            return;
        }

        string pageAddress = $"{page.Magazine:X1}{page.PageNumber:X2}-{page.SubPage:X4}";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export current teletext page as PNG",
            SuggestedFileName = $"teletext-{pageAddress}.png",
            DefaultExtension = "png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG image")
                {
                    Patterns = new[] { "*.png" },
                    MimeTypes = new[] { "image/png" },
                    AppleUniformTypeIdentifiers = new[] { "public.png" },
                }
            }
        });

        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            grid.SaveScreenshotPng(stream);
            await stream.FlushAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Screenshot export failed", ex.Message);
        }
    }

    private async void OnBatchExportScreenshotsClicked(object? sender, RoutedEventArgs e)
    {
        List<TeletextPage> pages = _squashStore.GetKnownAddresses()
            .Select(address => _squashStore.GetInstances(address.magazine, address.page, address.subpage))
            .Where(instances => instances.Count > 0)
            .Select(instances => instances[0].Page)
            .ToList();
        if (pages.Count == 0)
        {
            await ShowMessageAsync("Batch screenshot export", "There are no pages to export.");
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder for batch screenshots",
            AllowMultiple = false,
        });
        using var folder = folders.Count > 0 ? folders[0] : null;
        if (folder is null) return;

        var progressText = new TextBlock
        {
            Text = $"Exporting 0 of {pages.Count}...",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = pages.Count,
            Width = 380,
            Height = 18,
        };
        var progressDialog = new Window
        {
            Title = "Batch screenshot export",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children = { progressText, progressBar },
            },
        };
        progressDialog.Show(this);

        int exported = 0;
        try
        {
            for (int index = 0; index < pages.Count; index++)
            {
                string fileName = $"{index + 1}.png";
                using var file = await folder.GetFileAsync(fileName)
                    ?? await folder.CreateFileAsync(fileName)
                    ?? throw new IOException($"Could not create {fileName}.");
                await using var stream = await file.OpenWriteAsync();
                if (stream.CanSeek) stream.SetLength(0);
                SquashGrid.SaveScreenshotPng(stream, pages[index]);
                await stream.FlushAsync();

                exported = index + 1;
                progressBar.Value = exported;
                progressText.Text = $"Exporting {exported} of {pages.Count}...";
                await Task.Delay(1);
            }
        }
        catch (Exception ex)
        {
            progressDialog.Close();
            await ShowMessageAsync(
                "Batch screenshot export failed",
                $"Exported {exported} of {pages.Count} images.\n\n{ex.Message}");
            return;
        }

        progressDialog.Close();
        await ShowMessageAsync(
            "Batch screenshot export complete",
            $"Exported {exported} PNG images to the selected folder.");
    }

    private async Task SaveSquashAsync(bool forcePicker)
    {
        if (!forcePicker && !string.IsNullOrWhiteSpace(_squashFilePath)
            && Path.IsPathRooted(_squashFilePath))
        {
            await using var directStream = File.Create(_squashFilePath);
            await WriteSquashCaptureAsync(directStream);
            MarkHistoriesSaved();
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save squashed T42 capture",
            SuggestedFileName = string.IsNullOrWhiteSpace(_squashFilePath)
                ? "squashed.t42"
                : Path.GetFileName(_squashFilePath),
            DefaultExtension = "t42",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Raw 42-byte teletext capture") { Patterns = new[] { "*.t42" } }
            }
        });

        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        await WriteSquashCaptureAsync(stream);

        string displayPath = file.Path.IsFile ? file.Path.LocalPath : file.Path.ToString();
        _squashFilePath = displayPath;
        UpdateSquashFileFooter();
        MarkHistoriesSaved();
        await RememberFileAsync(file.Path.IsFile ? file.Path.LocalPath : null, broadcast: false);
    }

    private async Task WriteSquashCaptureAsync(Stream stream)
    {
        foreach (var packet in BuildSquashOutputPackets())
            await stream.WriteAsync(packet);

        await stream.FlushAsync();
    }

    private IReadOnlyList<byte[]> BuildSquashOutputPackets()
    {
        // A brand-new recovery page has no source capture. Emit a complete 25-packet
        // Level-1 page so every display row has a byte-level representation.
        if (_squashPackets.Count == 0)
        {
            var unsourcedPages = _squashStore.AllInstances.Select(instance => instance.Page).ToList();
            if (unsourcedPages.Count == 0)
                unsourcedPages.Add(_squashPage);

            var newPagePackets = new List<byte[]>(unsourcedPages.Count * 25);
            foreach (var page in unsourcedPages)
            {
                for (int row = 0; row < 25; row++)
                    newPagePackets.Add((byte[])(page.RawRows[row]
                        ?? CreateBlankPacket(page, row)).Clone());
                foreach (var enhancement in page.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
                    newPagePackets.Add((byte[])enhancement.RawPacket.Clone());
            }
            return newPagePackets;
        }

        var output = _squashPackets.Select(packet => (byte[])packet.Clone()).ToList();
        var pages = _squashStore.AllInstances.Select(instance => instance.Page).ToList();
        if (!pages.Any(page => ReferenceEquals(page, _squashPage)))
            pages.Add(_squashPage);

        var insertAfter = new Dictionary<int, List<byte[]>>();

        foreach (var page in pages)
        {
            int anchor = page.RawRowPacketIndices
                .Concat(page.EnhancementPackets.Select(packet => packet.PacketIndex))
                .Where(index => index >= 0)
                .DefaultIfEmpty(-1)
                .Max();

            // A page created after opening a capture has no mapped source packets;
            // append it as a complete page, beginning with its header.
            if (anchor < 0)
            {
                anchor = output.Count - 1;
                if (!insertAfter.TryGetValue(anchor, out var completePage))
                    insertAfter[anchor] = completePage = new List<byte[]>();
                for (int row = 0; row < 25; row++)
                    completePage.Add((byte[])(page.RawRows[row]
                        ?? CreateBlankPacket(page, row)).Clone());
                foreach (var enhancement in page.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
                    completePage.Add((byte[])enhancement.RawPacket.Clone());
                continue;
            }

            foreach (var enhancement in page.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
            {
                if (enhancement.PacketIndex >= 0 && enhancement.PacketIndex < output.Count)
                {
                    output[enhancement.PacketIndex] = (byte[])enhancement.RawPacket.Clone();
                }
                else
                {
                    if (!insertAfter.TryGetValue(anchor, out var newEnhancements))
                        insertAfter[anchor] = newEnhancements = new List<byte[]>();
                    newEnhancements.Add((byte[])enhancement.RawPacket.Clone());
                }
            }

            for (int row = 0; row < 25; row++)
            {
                var raw = page.RawRows[row];
                if (raw is null) continue;

                int packetIndex = page.RawRowPacketIndices[row];
                if (packetIndex >= 0 && packetIndex < output.Count)
                {
                    output[packetIndex] = (byte[])raw.Clone();
                }
                else
                {
                    if (!insertAfter.TryGetValue(anchor, out var missingRows))
                        insertAfter[anchor] = missingRows = new List<byte[]>();
                    missingRows.Add((byte[])raw.Clone());
                }
            }
        }

        if (insertAfter.Count == 0 && _deletedSquashPacketIndices.Count == 0) return output;

        var expanded = new List<byte[]>(output.Count + insertAfter.Values.Sum(rows => rows.Count));
        for (int index = 0; index < output.Count; index++)
        {
            if (!_deletedSquashPacketIndices.Contains(index))
                expanded.Add(output[index]);
            if (!_deletedSquashPacketIndices.Contains(index)
                && insertAfter.TryGetValue(index, out var additions))
                expanded.AddRange(additions);
        }
        return expanded;
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

    private static TeletextPage MakeDemoPage()
    {
        var page = new TeletextPage { Magazine = 1, PageNumber = 0x00 };
        WriteText(page, 0, 0, "TELETEXT EDITOR - DEMO", TeletextColor.Yellow);
        WriteText(page, 0, 2, "Open a .t42 capture via File > Open to get started.", TeletextColor.Cyan);
        return page;
    }

    private static void WriteText(TeletextPage page, int x, int y, string text, TeletextColor color)
    {
        for (int i = 0; i < text.Length && x + i < 40; i++)
        {
            var cell = page.Grid[x + i, y];
            cell.Character = text[i];
            cell.Foreground = color;
            page.Grid[x + i, y] = cell;
        }
    }
}
