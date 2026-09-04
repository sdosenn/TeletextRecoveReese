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
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TeletextRecoveReese.Core;

namespace TeletextRecoveReese;

public partial class MainWindow : Window
{
    private sealed class ToggleablePacketProgress(
        bool enabled,
        Action<IReadOnlyList<byte[]>> handler)
        : IVbiDecodedPacketProgress
    {
        private volatile bool _enabled = enabled;

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public bool IsEnabled => _enabled;

        public void Report(IReadOnlyList<byte[]> value)
        {
            if (!_enabled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_enabled) handler(value);
            }, DispatcherPriority.Background);
        }
    }

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
    private bool _broadcastReadOnlyExplanationShown;

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

    private sealed class EnhancementListEntry(
        string text,
        int designationCode,
        int tripletNumber,
        EnhancementPacket? packet)
        : INotifyPropertyChanged
    {
        private bool _isHoverRelated;
        private bool _isSelected;

        public string Text { get; } = text;
        public int DesignationCode { get; } = designationCode;
        public int TripletNumber { get; } = tripletNumber;
        public EnhancementPacket? Packet { get; } = packet;
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

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
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
        public bool? ToolbarOnBottom { get; set; }
        public string? VideoEncoder { get; set; }
        public double? VideoSecondsPerPage { get; set; }
        public bool? VideoAnimateFlash { get; set; }
        public int? VideoResolutionIndex { get; set; }
        public int? VideoAspectIndex { get; set; }
        public bool? ShowVideoBookmarks { get; set; }
        public List<RecentFileEntry> RecentFiles { get; set; } = new();
        public List<CaptureCardPreset> CustomCaptureCardPresets { get; set; } = new();
        public string? LastCaptureCardPresetName { get; set; }
        public string? LastLiveCaptureInterface { get; set; }
        public int? LastLiveCaptureInput { get; set; }
        public ulong? LastLiveCaptureStandard { get; set; }
        public bool? ShowRawVbiPreview { get; set; }
        public bool? ShowVideoCapturePreview { get; set; }
        public bool? DisableLiveVbiVideoPreview { get; set; }
        public bool? ShowLiveDeconvolvedPage { get; set; }
        public bool? RecordRawVbiToDisk { get; set; }
    }

    private sealed class CaptureCardPreset
    {
        public string Name { get; set; } = string.Empty;
        public string Chipset { get; set; } = string.Empty;
        public string Interface { get; set; } = string.Empty;
        public double SampleRate { get; set; }
        public int LineLength { get; set; }
        public int LineStart { get; set; }
        public int LineStartEnd { get; set; }
        public string SampleType { get; set; } = "UInt8";
        public int FieldLines { get; set; }
        public int FieldRangeStart { get; set; }
        public int FieldRangeEnd { get; set; }
        public float StandardDeviationThreshold { get; set; } = 14;
        public float SignalLevelThreshold { get; set; } = 64;
        public float CriFcRangeThreshold { get; set; } = 28;
        public double CriFcConfidenceThreshold { get; set; } = 0.35;
        public bool IsBuiltIn { get; set; }

        public override string ToString() => IsBuiltIn ? Name : $"{Name} (Custom)";
    }

    private sealed class ToggleableDeconvolutionControl(
        bool enabled,
        int clockSearchLineCount) : IVbiDeconvolutionControl
    {
        private int _enabled = enabled ? 1 : 0;
        private readonly int[] _clockSearchOffsets =
            new int[Math.Max(clockSearchLineCount, 1)];
        private readonly double[] _manualPacketSpanSamples =
            Enumerable.Repeat(-1.0, Math.Max(clockSearchLineCount, 1)).ToArray();
        private readonly int[] _lineDecodingEnabled =
            Enumerable.Repeat(1, Math.Max(clockSearchLineCount, 1)).ToArray();
        public bool Enabled
        {
            get => Volatile.Read(ref _enabled) != 0;
            set => Volatile.Write(ref _enabled, value ? 1 : 0);
        }

        public int ClockSearchLinePeriod { get; } = Math.Max(clockSearchLineCount, 1);

        public bool GetLineDecodingEnabled(int fieldLine)
        {
            int lineWithinField = fieldLine % Math.Max(ClockSearchLinePeriod, 1);
            return lineWithinField < _lineDecodingEnabled.Length
                && Volatile.Read(ref _lineDecodingEnabled[lineWithinField]) != 0;
        }

        public void SetLineDecodingEnabled(int lineWithinField, bool enabled)
        {
            if ((uint)lineWithinField < (uint)_lineDecodingEnabled.Length)
                Volatile.Write(
                    ref _lineDecodingEnabled[lineWithinField], enabled ? 1 : 0);
        }

        public int GetClockSearchOffset(int fieldLine)
        {
            int lineWithinField = fieldLine % Math.Max(ClockSearchLinePeriod, 1);
            return lineWithinField < _clockSearchOffsets.Length
                ? Volatile.Read(ref _clockSearchOffsets[lineWithinField])
                : 0;
        }

        public void SetClockSearchOffset(int lineWithinField, int samples)
        {
            if ((uint)lineWithinField < (uint)_clockSearchOffsets.Length)
                Volatile.Write(ref _clockSearchOffsets[lineWithinField], samples);
        }

        public double GetManualPacketSpanSamples(int fieldLine)
        {
            int lineWithinField = fieldLine % Math.Max(ClockSearchLinePeriod, 1);
            return lineWithinField < _manualPacketSpanSamples.Length
                ? Volatile.Read(ref _manualPacketSpanSamples[lineWithinField])
                : -1;
        }

        public void SetManualPacketSpanSamples(int lineWithinField, double samples)
        {
            if ((uint)lineWithinField < (uint)_manualPacketSpanSamples.Length)
                Volatile.Write(ref _manualPacketSpanSamples[lineWithinField], samples);
        }
    }

    private enum LiveCaptureCompletionChoice
    {
        Discard,
        OpenDecoded,
    }

    private sealed class RecordingReadStream(Stream source, Stream recording) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count > 0)
                await recording.WriteAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            if (read > 0) recording.Write(buffer, offset, read);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static readonly CaptureCardPreset[] BuiltInCaptureCardPresets =
    {
        new() { Name = "SAA7131 PCI", Chipset = "SAA7131", Interface = "PCI", SampleRate = 27000000, LineLength = 2048, LineStart = 0, LineStartEnd = 60, SampleType = "UInt8", FieldLines = 17, FieldRangeStart = 0, FieldRangeEnd = 16, IsBuiltIn = true },
        new() { Name = "SAA7131 USB", Chipset = "SAA7131", Interface = "USB", SampleRate = 27000000, LineLength = 1440, LineStart = 0, LineStartEnd = 20, SampleType = "UInt8", FieldLines = 16, FieldRangeStart = 0, FieldRangeEnd = 16, IsBuiltIn = true },
        new() { Name = "SAA7135 PCI", Chipset = "SAA7135", Interface = "PCI / DirectShow", SampleRate = 27000000, LineLength = 1600, LineStart = 0, LineStartEnd = 60, SampleType = "UInt8", FieldLines = 18, FieldRangeStart = 0, FieldRangeEnd = 18, IsBuiltIn = true },
        new() { Name = "August VGB100 USB", Chipset = "August VGB100", Interface = "USB", SampleRate = 27000000, LineLength = 1440, LineStart = 0, LineStartEnd = 20, SampleType = "UInt8", FieldLines = 16, FieldRangeStart = 0, FieldRangeEnd = 16, IsBuiltIn = true },
        new() { Name = "Elgato Video Capture (0FD9:0033)", Chipset = "Empia EM2860 + SAA711x", Interface = "USB", SampleRate = 13500000, LineLength = 720, LineStart = 0, LineStartEnd = 20, SampleType = "UInt8", FieldLines = 18, FieldRangeStart = 0, FieldRangeEnd = 18, IsBuiltIn = true },
        new() { Name = "Elgato Video Capture V2 (0FD9:0037)", Chipset = "Conexant CX231xx", Interface = "USB", SampleRate = 27000000, LineLength = 1440, LineStart = 0, LineStartEnd = 20, SampleType = "UInt8", FieldLines = 18, FieldRangeStart = 0, FieldRangeEnd = 18, IsBuiltIn = true },
        new() { Name = "BT8x8 PCI", Chipset = "BT8x8", Interface = "PCI", SampleRate = 35468950, LineLength = 2048, LineStart = 60, LineStartEnd = 130, SampleType = "UInt8", FieldLines = 16, FieldRangeStart = 0, FieldRangeEnd = 16, IsBuiltIn = true },
        new() { Name = "CX88 PCI", Chipset = "CX88", Interface = "PCI", SampleRate = 35468950, LineLength = 2048, LineStart = 90, LineStartEnd = 150, SampleType = "UInt8", FieldLines = 18, FieldRangeStart = 1, FieldRangeEnd = 17, IsBuiltIn = true },
        new() { Name = "VHS-decode Full TBC", Chipset = "VHS-decode", Interface = "TBC file", SampleRate = 17730000, LineLength = 1135, LineStart = 160, LineStartEnd = 190, SampleType = "UInt16", FieldLines = 313, FieldRangeStart = 6, FieldRangeEnd = 22, IsBuiltIn = true },
        new() { Name = "VHS-decode VBI-only TBC", Chipset = "VHS-decode", Interface = "TBC-VBI file", SampleRate = 17730000, LineLength = 1135, LineStart = 160, LineStartEnd = 190, SampleType = "UInt16", FieldLines = 16, FieldRangeStart = 0, FieldRangeEnd = 16, IsBuiltIn = true },
    };

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
    private NativeMenuItem? _nativeToolbarOnBottomMenuItem;
    private NativeMenuItem? _nativeExportVideoMenuItem;
    private NativeMenuItem? _nativeOpenRecentMenuItem;
    private NativeMenuItem? _nativeG0SubsetMenuItem;
    private NativeMenuItem? _nativeCreateSquashedStreamMenuItem;
    private NativeMenuItem? _nativeOpenLiveVbiCaptureMenuItem;
    private NativeMenuItem? _nativeSaveCapturedStreamMenuItem;
    private NativeMenuItem? _nativeDisableLiveVbiVideoPreviewMenuItem;
    private readonly string? _ffmpegPath;
    private bool _showX26EnhancementsSidebar = true;
    private bool _showVideoBookmarks = true;
    private bool _updatingVideoBookmarkText;
    private bool _updatingPageBookmarkList;
    private bool _pageBookmarkNavigationPending;
    private bool _previewingBroadcastVersion;
    private int? _squashFileG0Subset;
    private int? _broadcastFileG0Subset;
    private int _pinnedTransferRowHighlight = -1;
    private bool _suppressNextTransferRowClick;
    private int _blockBrowseVersionIndex = -1;
    private (int magazine, int page, int subpage)? _blockBrowseAddress;
    private int _blockBrowseColumn = -1;
    private int _blockBrowseRow = -1;
    private int _blockBrowseWidth;
    private int _blockBrowseHeight;
    private bool _blockBrowseHasPendingEdit;
    private readonly DispatcherTimer _flashTimer;
    private readonly DispatcherTimer _flashRollTimer;
    private bool _flashPhaseVisible = true;
    private bool _flashRollActive;
    private int _flashRollStartVersion;
    private int _flashRollOffset;
    private (int magazine, int page, int subpage)? _flashRollAddress;
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
        bool liveVbiCaptureAvailable = !OperatingSystem.IsMacOS();
        OpenLiveVbiCaptureMenuItem.IsEnabled = liveVbiCaptureAvailable;
        if (_nativeOpenLiveVbiCaptureMenuItem is not null)
            _nativeOpenLiveVbiCaptureMenuItem.IsEnabled = liveVbiCaptureAvailable;
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
        _flashRollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Render,
            OnFlashRollTick);
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
        SquashGrid.DiacriticDeleteRequested += OnDiacriticDeleteRequested;
        SquashGrid.EnhancementHoverChanged += OnEnhancementHoverChanged;
        BroadcastGrid.CellSelected += OnBroadcastGridCellSelected;
        InitializeBlankSquashDocument();
        // Handle keyboard navigation in the tunnel phase, before the menu can
        // consume the cursor keys for its own focus navigation.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        Closed += (_, _) => _flashTimer.Stop();
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        Opened -= OnWindowOpened;
        InitializeStartupFontChoices(_sessionState.GridFontFamily);
        ApplyGridFont(_sessionState.GridFontFamily, persist: false);
        if (_loadLastSession)
            await RestoreSessionFilesAsync();
    }

    private void InitializeStartupFontChoices(string? requestedFamilyName)
    {
        _installedFontFamilies.Clear();

        // Resolving every installed typeface is surprisingly expensive on Linux
        // systems with large Fontconfig catalogs. The complete list is only needed
        // by the font picker, so startup probes just the saved font, our preferred
        // fonts and Avalonia's default font.
        IEnumerable<string> candidates = new[] { requestedFamilyName }
            .Concat(PreferredGridFontNames)
            .Append(FontManager.Current.DefaultFontFamily.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string name in candidates)
        {
            var family = new FontFamily(name);
            if (CanResolveSystemFont(family))
                _installedFontFamilies.Add(new FontChoice(name, family));
        }

        // Avalonia can report success here by silently resolving an unknown
        // family to the platform fallback. Fonts such as TIFAX installed in a
        // macOS Fonts directory must be registered with our runtime collection
        // before the saved family name is applied.
        if (OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(requestedFamilyName))
            LoadMacFontsMissingFromSystemCatalog(CancellationToken.None);

        if (_installedFontFamilies.Count == 0)
        {
            FontFamily family = FontManager.Current.DefaultFontFamily;
            _installedFontFamilies.Add(new FontChoice(family.Name, family));
        }
    }

    private void InitializeInstalledFonts(CancellationToken cancellationToken = default)
    {
        var installedFonts = new List<FontChoice>();
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FontFamily family in FontManager.Current.SystemFonts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(family.Name) || !knownNames.Add(family.Name))
                continue;
            if (CanResolveSystemFont(family))
                installedFonts.Add(new FontChoice(family.Name, family));
        }

        _installedFontFamilies.Clear();
        _installedFontFamilies.AddRange(installedFonts);

        if (OperatingSystem.IsMacOS())
            LoadMacFontsMissingFromSystemCatalog(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
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

    private void LoadMacFontsMissingFromSystemCatalog(CancellationToken cancellationToken)
    {
        void UpsertFontChoice(string name, FontFamily family)
        {
            int existingIndex = _installedFontFamilies.FindIndex(choice =>
                string.Equals(choice.Name, name, StringComparison.OrdinalIgnoreCase));
            var choice = new FontChoice(name, family);
            if (existingIndex >= 0)
                _installedFontFamilies[existingIndex] = choice;
            else
                _installedFontFamilies.Add(choice);
        }

        foreach ((string name, FontFamily family) in _loadedMacFontFamilies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpsertFontChoice(name, family);
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
                cancellationToken.ThrowIfCancellationRequested();
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
                        var family = new FontFamily($"{MacInstalledFontCollectionKey}#{name}");
                        _loadedMacFontFamilies[name] = family;
                        UpsertFontChoice(name, family);
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
        try
        {
            if (!await LoadInstalledFontsWithProgressAsync())
                return;
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not load fonts", ex.Message);
            return;
        }

        FontChoice? choice = await ShowFontPickerAsync();
        if (choice is null) return;

        ApplyGridFont(choice.Name, persist: true);
        try
        {
            await SaveSessionStateAsync();
        }
        catch { }
    }

    private async Task<bool> LoadInstalledFontsWithProgressAsync()
    {
        using var cancellation = new CancellationTokenSource();
        List<FontChoice> previousChoices = _installedFontFamilies.ToList();
        var progress = new ProgressBar
        {
            IsIndeterminate = true,
            Width = 300,
            Height = 8,
        };
        var abortButton = new Button
        {
            Content = "Abort",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var dialog = new Window
        {
            Title = "Loading fonts",
            Width = 380,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Loading installed fonts…",
                        FontSize = 16,
                        FontWeight = FontWeight.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = "This can take a moment on systems with many fonts.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    progress,
                    abortButton,
                },
            },
        };

        Exception? loadError = null;
        bool aborted = false;
        bool loading = true;
        abortButton.Click += (_, _) =>
        {
            abortButton.IsEnabled = false;
            abortButton.Content = "Aborting…";
            cancellation.Cancel();
        };
        dialog.Closing += (_, e) =>
        {
            if (loading)
                e.Cancel = true;
        };
        dialog.Opened += async (_, _) =>
        {
            // Let Avalonia render the dialog before starting the expensive scan.
            await Task.Yield();
            try
            {
                await Task.Run(
                    () => InitializeInstalledFonts(cancellation.Token),
                    cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                aborted = true;
                _installedFontFamilies.Clear();
                _installedFontFamilies.AddRange(previousChoices);
            }
            catch (Exception ex)
            {
                loadError = ex;
            }
            finally
            {
                loading = false;
                dialog.Close();
            }
        };

        await dialog.ShowDialog(this);
        if (loadError is not null)
            throw new InvalidOperationException(
                $"The installed font list could not be loaded: {loadError.Message}",
                loadError);
        return !aborted;
    }

    private async void OnCaptureCardPresetsClicked(object? sender, RoutedEventArgs e) =>
        await ShowCaptureCardPresetsAsync();

    private async Task ShowCaptureCardPresetsAsync()
    {
        _sessionState.CustomCaptureCardPresets ??= new List<CaptureCardPreset>();
        var presetList = new ListBox { Width = 270, MinHeight = 330 };
        var details = new TextBlock
        {
            Width = 390,
            MinHeight = 250,
            FontFamily = new FontFamily("Menlo,DejaVu Sans Mono,monospace"),
            TextWrapping = TextWrapping.Wrap,
        };
        var newButton = new Button { Content = "New…", Width = 90 };
        var deleteButton = new Button { Content = "Delete", Width = 90, IsEnabled = false };
        var closeButton = new Button { Content = "Close", Width = 90, IsCancel = true };

        List<CaptureCardPreset> GetPresets() => BuiltInCaptureCardPresets
            .Concat(_sessionState.CustomCaptureCardPresets)
            .ToList();

        void RefreshPresets(CaptureCardPreset? select = null)
        {
            List<CaptureCardPreset> presets = GetPresets();
            presetList.ItemsSource = presets;
            presetList.SelectedItem = select ?? presets.FirstOrDefault();
        }

        void ShowDetails(CaptureCardPreset? preset)
        {
            if (preset is null)
            {
                details.Text = "Select a preset to see its capture parameters.";
                deleteButton.IsEnabled = false;
                return;
            }

            details.Text =
                $"Name              {preset.Name}\n" +
                $"Chipset / family  {preset.Chipset}\n" +
                $"Interface         {preset.Interface}\n\n" +
                $"Sample rate       {preset.SampleRate:N0} Hz\n" +
                $"Line length       {preset.LineLength} samples\n" +
                $"Line start range  {preset.LineStart}–{preset.LineStartEnd} (end exclusive)\n" +
                $"Sample type       {preset.SampleType}\n" +
                $"Field lines       {preset.FieldLines}\n" +
                $"Field range       {preset.FieldRangeStart}–{preset.FieldRangeEnd} (end exclusive)\n" +
                $"Std-dev threshold {preset.StandardDeviationThreshold:0.##}\n" +
                $"Signal threshold  {preset.SignalLevelThreshold:0.##}\n" +
                $"CRI/FC range      {preset.CriFcRangeThreshold:0.##}\n" +
                $"CRI/FC confidence {preset.CriFcConfidenceThreshold:0.##}\n\n" +
                (preset.IsBuiltIn ? "Built-in preset" : "User preset — stored in session.json");
            deleteButton.IsEnabled = !preset.IsBuiltIn;
        }

        var dialog = new Window
        {
            Title = "Capture card presets",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            ColumnSpacing = 18,
            Children = { presetList, details },
        };
        Grid.SetColumn(details, 1);
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,* ,Auto") };
        var editButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { newButton, deleteButton },
        };
        buttons.Children.Add(editButtons);
        Grid.SetColumn(closeButton, 2);
        buttons.Children.Add(closeButton);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "VBI capture card configurations",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                },
                columns,
                buttons,
            },
        };

        presetList.SelectionChanged += (_, _) =>
            ShowDetails(presetList.SelectedItem as CaptureCardPreset);
        newButton.Click += async (_, _) =>
        {
            CaptureCardPreset? preset = await ShowNewCaptureCardPresetAsync(dialog, GetPresets());
            if (preset is null) return;
            _sessionState.CustomCaptureCardPresets.Add(preset);
            SaveSessionState();
            RefreshPresets(preset);
        };
        deleteButton.Click += (_, _) =>
        {
            if (presetList.SelectedItem is not CaptureCardPreset { IsBuiltIn: false } preset) return;
            _sessionState.CustomCaptureCardPresets.Remove(preset);
            SaveSessionState();
            RefreshPresets();
        };
        closeButton.Click += (_, _) => dialog.Close();

        RefreshPresets();
        await dialog.ShowDialog(this);
    }

    private static async Task<CaptureCardPreset?> ShowNewCaptureCardPresetAsync(
        Window owner,
        IReadOnlyCollection<CaptureCardPreset> existingPresets)
    {
        var name = new TextBox { Width = 250, PlaceholderText = "Card manufacturer and model" };
        var chipset = new TextBox { Width = 250, PlaceholderText = "e.g. SAA7131" };
        var cardInterface = new ComboBox
        {
            Width = 250,
            ItemsSource = new[] { "PCI", "PCIe", "USB", "TBC file", "Other" },
            SelectedIndex = 0,
        };
        var sampleRate = new NumericUpDown { Width = 250, Minimum = 1, Maximum = 1000000000, Value = 27000000, Increment = 1000 };
        var lineLength = new NumericUpDown { Width = 250, Minimum = 1, Maximum = 100000, Value = 2048 };
        var lineStart = new NumericUpDown { Width = 115, Minimum = 0, Maximum = 100000, Value = 0 };
        var lineStartEnd = new NumericUpDown { Width = 115, Minimum = 1, Maximum = 100000, Value = 60 };
        var sampleType = new ComboBox { Width = 250, ItemsSource = new[] { "UInt8", "UInt16" }, SelectedIndex = 0 };
        var fieldLines = new NumericUpDown { Width = 250, Minimum = 1, Maximum = 10000, Value = 17 };
        var fieldRangeStart = new NumericUpDown { Width = 115, Minimum = 0, Maximum = 10000, Value = 0 };
        var fieldRangeEnd = new NumericUpDown { Width = 115, Minimum = 1, Maximum = 10000, Value = 16 };
        var stdDevThreshold = new NumericUpDown { Width = 250, Minimum = 0, Maximum = 255, Value = 14, Increment = 1 };
        var signalThreshold = new NumericUpDown { Width = 250, Minimum = 0, Maximum = 255, Value = 64, Increment = 1 };
        var criFcRangeThreshold = new NumericUpDown { Width = 250, Minimum = 0, Maximum = 255, Value = 28, Increment = 1 };
        var criFcConfidenceThreshold = new NumericUpDown
        {
            Width = 250, Minimum = 0, Maximum = 1, Value = 0.35m,
            Increment = 0.01m, FormatString = "0.00",
        };
        var error = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap };
        var saveButton = new Button { Content = "Save", Width = 90, IsDefault = true };
        var cancelButton = new Button { Content = "Cancel", Width = 90, IsCancel = true };

        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 9,
            ColumnSpacing = 12,
        };
        void AddField(int row, string label, Control control)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            form.Children.Add(text);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            form.Children.Add(control);
        }
        static StackPanel RangeControls(Control start, Control end) => new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { start, new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center }, end },
        };
        AddField(0, "Card preset name", name);
        AddField(1, "Chipset / family", chipset);
        AddField(2, "Interface", cardInterface);
        AddField(3, "Sample rate (Hz)", sampleRate);
        AddField(4, "Line length (samples)", lineLength);
        AddField(5, "Line start range", RangeControls(lineStart, lineStartEnd));
        AddField(6, "Sample type", sampleType);
        AddField(7, "Lines per field", fieldLines);
        AddField(8, "Field range", RangeControls(fieldRangeStart, fieldRangeEnd));
        AddField(9, "Std-dev threshold", stdDevThreshold);
        AddField(10, "Signal level threshold", signalThreshold);
        AddField(11, "CRI/FC range threshold", criFcRangeThreshold);
        AddField(12, "CRI/FC confidence", criFcConfidenceThreshold);

        var dialog = new Window
        {
            Title = "New capture card preset",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Width = 470,
                Spacing = 12,
                Children =
                {
                    form,
                    error,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, saveButton },
                    },
                },
            },
        };

        CaptureCardPreset? result = null;
        saveButton.Click += (_, _) =>
        {
            string presetName = name.Text?.Trim() ?? string.Empty;
            int start = (int)(lineStart.Value ?? 0);
            int startEnd = (int)(lineStartEnd.Value ?? 0);
            int rangeStart = (int)(fieldRangeStart.Value ?? 0);
            int rangeEnd = (int)(fieldRangeEnd.Value ?? 0);
            int lines = (int)(fieldLines.Value ?? 0);
            if (string.IsNullOrWhiteSpace(presetName))
                error.Text = "Enter a card preset name.";
            else if (existingPresets.Any(p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase)))
                error.Text = "A preset with this name already exists.";
            else if (startEnd <= start)
                error.Text = "Line start range end must be greater than its start.";
            else if (rangeEnd <= rangeStart || rangeEnd > lines)
                error.Text = "Field range must be non-empty and fit inside the number of field lines.";
            else
            {
                result = new CaptureCardPreset
                {
                    Name = presetName,
                    Chipset = chipset.Text?.Trim() ?? string.Empty,
                    Interface = cardInterface.SelectedItem?.ToString() ?? "Other",
                    SampleRate = (double)(sampleRate.Value ?? 27000000),
                    LineLength = (int)(lineLength.Value ?? 2048),
                    LineStart = start,
                    LineStartEnd = startEnd,
                    SampleType = sampleType.SelectedItem?.ToString() ?? "UInt8",
                    FieldLines = lines,
                    FieldRangeStart = rangeStart,
                    FieldRangeEnd = rangeEnd,
                    StandardDeviationThreshold = (float)(stdDevThreshold.Value ?? 14),
                    SignalLevelThreshold = (float)(signalThreshold.Value ?? 64),
                    CriFcRangeThreshold = (float)(criFcRangeThreshold.Value ?? 28),
                    CriFcConfidenceThreshold = (double)(criFcConfidenceThreshold.Value ?? 0.35m),
                };
                dialog.Close();
            }
        };
        cancelButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
        return result;
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
            Text = "TELETEXT PREVIEW\nABCDEFGHIJKLMNOPQRSTUVWXYZ\nabcdefghijklmnopqrstuvwxyz\n0123456789  ! ? . , : ;",
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
        if (_closeConfirmed) return;

        bool hasUnsavedCapturedStream = HasUnsavedCapturedStream();
        if (!hasUnsavedCapturedStream && !_squashDirty) return;

        e.Cancel = true;
        if (_closeDialogOpen) return;

        _closeDialogOpen = true;
        try
        {
            if (hasUnsavedCapturedStream)
            {
                UnsavedCaptureCloseChoice choice = await ConfirmUnsavedCapturedStreamOnCloseAsync();
                if (choice == UnsavedCaptureCloseChoice.Cancel) return;
                if (choice == UnsavedCaptureCloseChoice.Save
                    && !await SaveCapturedStreamAsync())
                    return;
            }

            if (_squashDirty && !await ConfirmCloseWithoutSavingAsync()) return;

            _closeConfirmed = true;
            Close();
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }

    private enum UnsavedCaptureCloseChoice
    {
        Cancel,
        Discard,
        Save,
    }

    private async Task<UnsavedCaptureCloseChoice> ConfirmUnsavedCapturedStreamOnCloseAsync()
    {
        UnsavedCaptureCloseChoice choice = UnsavedCaptureCloseChoice.Cancel;
        var cancelButton = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var discardButton = new Button { Content = "Close without saving", Width = 155 };
        var saveButton = new Button { Content = "Save…", Width = 90, IsDefault = true };
        var dialog = new Window
        {
            Title = "Unsaved captured stream",
            Width = 500,
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
                        Text = $"The Untitled full broadcast contains {_broadcastPackets.Count:N0} captured packets. Save it before closing?",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, discardButton, saveButton },
                    },
                },
            },
        };
        cancelButton.Click += (_, _) => dialog.Close();
        discardButton.Click += (_, _) =>
        {
            choice = UnsavedCaptureCloseChoice.Discard;
            dialog.Close();
        };
        saveButton.Click += (_, _) =>
        {
            choice = UnsavedCaptureCloseChoice.Save;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        return choice;
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
        bool shiftModifier = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (activeGrid == SquashGrid && shiftModifier)
            SquashGrid.RecoveryBrowseActive = true;

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
                await WarnBroadcastReadOnlyAsync();
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
                await WarnBroadcastReadOnlyAsync();
            }
            return;
        }

        // Do not turn unhandled Ctrl/Cmd shortcuts into printable characters.
        if (commandModifier) return;

        if (!commandModifier && activeGrid == SquashGrid && shiftModifier
            && e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            BrowseSelectedBlockVersion(e.Key == Key.Right ? 1 : -1);
            return;
        }

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
            await WarnBroadcastReadOnlyAsync();
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
                    && !TrySetLevel15DiacriticReplacingCorruptPackets(
                        page, x, y, actualChar, diacritical, out string enhancementError))
                {
                    PlaySystemErrorSound();
                    await ShowEnhancementErrorAsync("Cannot add diacritic", enhancementError, page);
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

    private bool TrySetLevel15DiacriticReplacingCorruptPackets(
        TeletextPage page,
        int column,
        int row,
        char baseCharacter,
        int diacritical,
        out string error)
    {
        error = string.Empty;
        if (PageAssembler.TrySetLevel15Diacritic(
                page, column, row, baseCharacter, diacritical, out error))
            return true;

        if (!error.Contains("uncorrectable triplet", StringComparison.OrdinalIgnoreCase))
            return false;

        return PageAssembler.TryInsertLevel15DiacriticRawPreserving(
            page, column, row, baseCharacter, diacritical, out error);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftShift or Key.RightShift)
            && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        SquashGrid.RecoveryBrowseActive = false;
        if (_blockBrowseHasPendingEdit && SquashGrid.Page is { } page)
            CommitPageEdit(page);
        _blockBrowseHasPendingEdit = false;
        ResetBlockVersionBrowse();
    }

    private void BrowseSelectedBlockVersion(int direction)
    {
        if (SquashGrid.Page is not { } squashPage) return;
        var address = (squashPage.Magazine, squashPage.PageNumber, squashPage.SubPage);
        var versions = _store.GetInstances(address.Magazine, address.PageNumber, address.SubPage);
        if (versions.Count == 0)
        {
            _ = SquashGrid.FlashRecoveryBoundaryAsync();
            return;
        }

        int column = Math.Clamp(SquashGrid.SelectedColumn, 0, 39);
        int row = Math.Clamp(SquashGrid.SelectedRow, 0, 24);
        int width = Math.Min(Math.Max(SquashGrid.SelectionWidth, 1), 40 - column);
        int height = Math.Min(Math.Max(SquashGrid.SelectionHeight, 1), 25 - row);
        bool sameBlock = _blockBrowseAddress == address
            && _blockBrowseColumn == column && _blockBrowseRow == row
            && _blockBrowseWidth == width && _blockBrowseHeight == height;
        if (!sameBlock)
        {
            _blockBrowseAddress = address;
            _blockBrowseColumn = column;
            _blockBrowseRow = row;
            _blockBrowseWidth = width;
            _blockBrowseHeight = height;
            _blockBrowseVersionIndex = direction > 0 ? -1 : versions.Count;
            EnsurePageHistory(squashPage);
        }

        int targetVersion = _blockBrowseVersionIndex + direction;
        if (targetVersion < 0 || targetVersion >= versions.Count)
        {
            _ = SquashGrid.FlashRecoveryBoundaryAsync();
            return;
        }

        byte[] block = CreateByteBlock(
            versions[targetVersion].Page,
            column,
            row,
            width,
            height);
        PasteByteBlockIntoSquash(
            block,
            column,
            row,
            updateSquashSelection: false,
            commitEdit: false);
        _blockBrowseVersionIndex = targetVersion;
        _blockBrowseHasPendingEdit = true;
        SelectBroadcastAddress(address, targetVersion, persistRecentPosition: false);
        _ = SquashGrid.ShowSelectionStatusAsync($"v{targetVersion + 1}/{versions.Count}");
    }

    private void ResetBlockVersionBrowse()
    {
        _blockBrowseVersionIndex = -1;
        _blockBrowseAddress = null;
        _blockBrowseColumn = -1;
        _blockBrowseRow = -1;
        _blockBrowseWidth = 0;
        _blockBrowseHeight = 0;
    }

    private async Task<byte[]?> CopySelectionAsync(TeletextGridControl grid)
    {
        if (grid.Page is not { } page || Clipboard is null) return null;

        int startX = Math.Clamp(grid.SelectedColumn, 0, 39);
        int startY = Math.Clamp(grid.SelectedRow, 0, 24);
        int width = Math.Min(Math.Max(grid.SelectionWidth, 1), 40 - startX);
        int height = Math.Min(Math.Max(grid.SelectionHeight, 1), 25 - startY);
        byte[] block = CreateByteBlock(page, startX, startY, width, height);
        var item = DataTransferItem.Create(TeletextClipboardFormat, block);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await Clipboard.SetDataAsync(transfer);
        return block;
    }

    private static byte[] CreateByteBlock(
        TeletextPage page,
        int startX,
        int startY,
        int width,
        int height)
    {
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
        bool updateSquashSelection,
        bool commitEdit = true)
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
        if (commitEdit)
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

    private async Task WarnBroadcastReadOnlyAsync()
    {
        bool showExplanation = BroadcastPaneGrid.IsVisible
            && !SquashPaneGrid.IsVisible
            && !_broadcastReadOnlyExplanationShown;
        if (showExplanation)
            _broadcastReadOnlyExplanationShown = true;

        PlaySystemErrorSound();
        await BroadcastGrid.FlashReadOnlyWarningAsync();

        if (!showExplanation) return;
        await ShowMessageAsync(
            "Full broadcast capture is read-only",
            "A full broadcast capture cannot be edited directly.\n\n" +
            "To restore and edit Teletext pages, choose Page > Create Squashed Stream. " +
            "The squashed stream will open in the editable left pane.");
    }

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
        UpdateG0SubsetMenuChecks();
    }

    private void OnBroadcastGridCellSelected(object? sender, EventArgs e)
    {
        BroadcastGrid.IsActive = true;
        SquashGrid.IsActive = false;
        SquashGrid.ClearSelection();
        UpdateVideoBookmarkUi();
        UpdateG0SubsetMenuChecks();
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
            await ShowEnhancementErrorAsync("Cannot move diacritic", error, page);
            return;
        }

        CommitPageEdit(page);
        UpdateEnhancementList(page);
        SquashGrid.InvalidateVisual();
    }

    private void OnDiacriticDeleteRequested(object? sender, DiacriticDeleteRequestedEventArgs e)
    {
        if (sender != SquashGrid || SquashGrid.Page is not { } page) return;
        var packet = page.EnhancementPackets.FirstOrDefault(
            candidate => candidate.DesignationCode == e.DesignationCode
                && candidate.Triplets.Any(triplet => triplet.TripletNumber == e.TripletNumber));
        if (packet is null) return;

        EnsurePageHistory(page);
        if (!PageAssembler.DeleteEnhancementTriplet(page, packet, e.TripletNumber)) return;

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
                Content = "◀︎",
                Width = 32,
                Height = 23,
                FontSize = 12,
                FontFamily = new FontFamily("Menlo,DejaVu Sans Mono,monospace"),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = i,
                Margin = new Thickness(0, 0, 0, 1)
            };
            btn.Click += OnTransferRowClicked;
            btn.AddHandler(
                InputElement.PointerPressedEvent,
                OnTransferRowPointerPressed,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
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
        SetTransferRowHighlight(-1);
    }

    private void OnTransferRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button { Tag: int row }
            || !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            || !e.GetCurrentPoint((Button)sender).Properties.IsLeftButtonPressed)
            return;

        _pinnedTransferRowHighlight = _pinnedTransferRowHighlight == row ? -1 : row;
        _suppressNextTransferRowClick = true;
        SetPinnedTransferRowHighlight(_pinnedTransferRowHighlight);
    }

    private void SetTransferRowHighlight(int row)
    {
        SquashGrid.SetTransferRowHighlight(row);
        BroadcastGrid.SetTransferRowHighlight(row);
    }

    private void SetPinnedTransferRowHighlight(int row)
    {
        SquashGrid.SetPinnedTransferRowHighlight(row);
        BroadcastGrid.SetPinnedTransferRowHighlight(row);
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

    private void OnToolbarOnBottomClicked(object? sender, RoutedEventArgs e) =>
        SetToolbarOnBottom(ToolbarOnBottomMenuItem.IsChecked, saveSession: true);

    private void OnNativeToolbarOnBottomClicked(object? sender, EventArgs e)
    {
        bool onBottom = !(_sessionState.ToolbarOnBottom ?? false);
        SetToolbarOnBottom(onBottom, saveSession: true);

        Dispatcher.UIThread.Post(() =>
        {
            if (_nativeToolbarOnBottomMenuItem is not null)
                _nativeToolbarOnBottomMenuItem.IsChecked = onBottom;
        }, DispatcherPriority.Background);
    }

    private void SetToolbarOnBottom(bool onBottom, bool saveSession)
    {
        Grid.SetRow(SquashToolbarsStack, onBottom ? 2 : 1);
        Grid.SetRow(SquashContentGrid, onBottom ? 1 : 2);
        Grid.SetRow(BroadcastToolbarsStack, onBottom ? 2 : 1);
        Grid.SetRow(BroadcastContentGrid, onBottom ? 1 : 2);

        ToolbarOnBottomMenuItem.IsChecked = onBottom;
        if (_nativeToolbarOnBottomMenuItem is not null)
            _nativeToolbarOnBottomMenuItem.IsChecked = onBottom;

        _sessionState.ToolbarOnBottom = onBottom;
        if (saveSession)
            SaveSessionState();

        FitWindowToContent();
    }

    private void OnNativeOpenSquashedClicked(object? sender, EventArgs e) =>
        OnOpenSquashedClicked(sender, new RoutedEventArgs());

    private void OnNativeOpenClicked(object? sender, EventArgs e) =>
        OnOpenClicked(sender, new RoutedEventArgs());

    private void OnNativeOpenVbiCaptureClicked(object? sender, EventArgs e) =>
        OnOpenVbiCaptureClicked(sender, new RoutedEventArgs());

    private void OnNativeOpenLiveVbiCaptureClicked(object? sender, EventArgs e) =>
        OnOpenLiveVbiCaptureClicked(sender, new RoutedEventArgs());

    private void OnNativeSaveClicked(object? sender, EventArgs e) =>
        OnSaveClicked(sender, new RoutedEventArgs());

    private void OnNativeSaveAsClicked(object? sender, EventArgs e) =>
        OnSaveAsClicked(sender, new RoutedEventArgs());

    private void OnNativeSaveCapturedStreamClicked(object? sender, EventArgs e) =>
        OnSaveCapturedStreamClicked(sender, new RoutedEventArgs());

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

    private void OnNativeCreateSquashedStreamClicked(object? sender, EventArgs e) =>
        OnCreateSquashedStreamClicked(sender, new RoutedEventArgs());

    private void OnNativeG0SubsetClicked(object? sender, EventArgs e)
    {
        if (sender is NativeMenuItem item)
            ApplyG0SubsetSelection(item.CommandParameter?.ToString());
        Dispatcher.UIThread.Post(UpdateG0SubsetMenuChecks, DispatcherPriority.Background);
    }

    private void OnG0SubsetClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item)
            ApplyG0SubsetSelection(item.CommandParameter?.ToString());
    }

    private void ApplyG0SubsetSelection(string? selection)
    {
        TeletextGridControl? grid = GetG0TargetGrid();
        if (grid is null) return;

        int? selectedSubset;
        if (string.Equals(selection, "auto", StringComparison.OrdinalIgnoreCase))
            selectedSubset = null;
        else if (string.Equals(selection, "default", StringComparison.OrdinalIgnoreCase))
            selectedSubset = -1;
        else if (int.TryParse(selection, out int option) && option is >= 0 and <= 6)
            selectedSubset = option;
        else
            return;

        bool broadcast = grid == BroadcastGrid;
        if (broadcast)
            _broadcastFileG0Subset = selectedSubset;
        else
            _squashFileG0Subset = selectedSubset;

        if (grid.Page is { } page)
            PageAssembler.SetNationalOptionOverride(page, selectedSubset);
        grid.InvalidateVisual();
        if (grid == SquashGrid)
            UpdateEnhancementList(grid.Page);
        UpdateG0SubsetMenuChecks();
    }

    private void ApplyFileG0SubsetToPage(TeletextPage page, bool broadcast)
    {
        int? selectedSubset = broadcast ? _broadcastFileG0Subset : _squashFileG0Subset;
        if (page.NationalOptionOverride == selectedSubset) return;
        PageAssembler.SetNationalOptionOverride(page, selectedSubset);
    }

    private void UpdateG0SubsetMenuChecks()
    {
        TeletextGridControl? grid = GetG0TargetGrid();
        bool broadcast = grid == BroadcastGrid;
        int? selectedSubset = broadcast ? _broadcastFileG0Subset : _squashFileG0Subset;

        foreach (MenuItem item in G0SubsetMenuItem.Items.OfType<MenuItem>())
            item.IsChecked = IsG0MenuSelectionChecked(item.CommandParameter?.ToString(), selectedSubset);

        if (_nativeG0SubsetMenuItem?.Menu is { } nativeMenu)
        {
            foreach (NativeMenuItem item in nativeMenu.Items.OfType<NativeMenuItem>())
                item.IsChecked = IsG0MenuSelectionChecked(item.CommandParameter?.ToString(), selectedSubset);
        }
    }

    private static bool IsG0MenuSelectionChecked(string? selection, int? selectedSubset)
    {
        if (string.Equals(selection, "auto", StringComparison.OrdinalIgnoreCase))
            return !selectedSubset.HasValue;
        if (string.Equals(selection, "default", StringComparison.OrdinalIgnoreCase))
            return selectedSubset == -1;
        return int.TryParse(selection, out int option)
            && option is >= 0 and <= 6
            && selectedSubset == option;
    }

    private TeletextGridControl? GetG0TargetGrid()
    {
        if (SquashPaneGrid.IsVisible && !BroadcastPaneGrid.IsVisible) return SquashGrid;
        if (BroadcastPaneGrid.IsVisible && !SquashPaneGrid.IsVisible) return BroadcastGrid;
        return IsActiveGrid();
    }

    private void OnNativeChooseFontClicked(object? sender, EventArgs e) =>
        OnChooseFontClicked(sender, new RoutedEventArgs());

    private void OnDisableLiveVbiVideoPreviewClicked(object? sender, RoutedEventArgs e) =>
        SetDisableLiveVbiVideoPreview(DisableLiveVbiVideoPreviewMenuItem.IsChecked, saveSession: true);

    private void OnNativeDisableLiveVbiVideoPreviewClicked(object? sender, EventArgs e)
    {
        // Cocoa updates the exported checkbox at a different point in the click
        // cycle, so toggle from the saved model value.
        bool disabled = !(_sessionState.DisableLiveVbiVideoPreview ?? false);
        SetDisableLiveVbiVideoPreview(disabled, saveSession: true);
        Dispatcher.UIThread.Post(() =>
        {
            if (_nativeDisableLiveVbiVideoPreviewMenuItem is not null)
                _nativeDisableLiveVbiVideoPreviewMenuItem.IsChecked = disabled;
        }, DispatcherPriority.Background);
    }

    private void SetDisableLiveVbiVideoPreview(bool disabled, bool saveSession)
    {
        DisableLiveVbiVideoPreviewMenuItem.IsChecked = disabled;
        if (_nativeDisableLiveVbiVideoPreviewMenuItem is not null)
            _nativeDisableLiveVbiVideoPreviewMenuItem.IsChecked = disabled;
        _sessionState.DisableLiveVbiVideoPreview = disabled;
        if (saveSession)
            SaveSessionState();
    }

    private void OnNativeCaptureCardPresetsClicked(object? sender, EventArgs e) =>
        OnCaptureCardPresetsClicked(sender, new RoutedEventArgs());

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

        _nativeG0SubsetMenuItem = menu.Items
            .OfType<NativeMenuItem>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Page", StringComparison.Ordinal))?
            .Menu?.Items
            .OfType<NativeMenuItem>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "G0 Subset", StringComparison.Ordinal));

        _nativeCreateSquashedStreamMenuItem = menu.Items
            .OfType<NativeMenuItem>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Page", StringComparison.Ordinal))?
            .Menu?.Items
            .OfType<NativeMenuItem>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Create Squashed Stream", StringComparison.Ordinal));

        if (menu.Items.Count > 0 && menu.Items[0] is NativeMenuItem { Menu: { } fileMenu })
        {
            _nativeOpenLiveVbiCaptureMenuItem = fileMenu.Items
                .OfType<NativeMenuItem>()
                .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Open VBI Capture", StringComparison.Ordinal))?
                .Menu?.Items
                .OfType<NativeMenuItem>()
                .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Live Capture…", StringComparison.Ordinal));
            _nativeOpenRecentMenuItem = fileMenu.Items
                .OfType<NativeMenuItem>()
                .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Open Recent", StringComparison.Ordinal))
                ?? fileMenu.Items.ElementAtOrDefault(2) as NativeMenuItem;
            _nativeSaveCapturedStreamMenuItem = fileMenu.Items
                .OfType<NativeMenuItem>()
                .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Save Captured Stream…", StringComparison.Ordinal));
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
            _nativeToolbarOnBottomMenuItem = viewMenu.Items.OfType<NativeMenuItem>()
                .FirstOrDefault(item => item.Header?.ToString() == "Toolbar on Bottom");
        }

        _nativeDisableLiveVbiVideoPreviewMenuItem = menu.Items
            .OfType<NativeMenuItem>()
            .FirstOrDefault(item => item.Header?.ToString() == "Options")?
            .Menu?.Items.OfType<NativeMenuItem>()
            .FirstOrDefault(item => item.Header?.ToString()
                == "Disable video preview in live VBI capture");
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
        if (_suppressNextTransferRowClick)
        {
            _suppressNextTransferRowClick = false;
            return;
        }
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

    private sealed record LiveCaptureInterface(string Name, string Path, string Kind)
    {
        public override string ToString() => Name;
    }

    private async void OnOpenLiveVbiCaptureClicked(object? sender, RoutedEventArgs e)
    {
        if (OperatingSystem.IsMacOS()) return;
        _sessionState.CustomCaptureCardPresets ??= new List<CaptureCardPreset>();
        List<CaptureCardPreset> presets = BuiltInCaptureCardPresets
            .Concat(_sessionState.CustomCaptureCardPresets)
            .ToList();
        var interfaceCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var cardNameText = new TextBlock
        {
            Text = "Capture card: —",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
        };
        var inputCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        var standardCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        var presetCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = presets,
            IsEnabled = false,
        };
        presetCombo.SelectedItem = presets.FirstOrDefault(p =>
                                       string.Equals(p.Name, _sessionState.LastCaptureCardPresetName, StringComparison.OrdinalIgnoreCase))
                                   ?? presets.FirstOrDefault();
        var statusText = new TextBlock
        {
            Width = 440,
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
        };
        bool disableVideoPreview = _sessionState.DisableLiveVbiVideoPreview ?? false;
        var previewImage = new Image
        {
            Stretch = Stretch.Uniform,
            IsVisible = !disableVideoPreview,
        };
        var previewDisabledText = new TextBlock
        {
            Text = "Live video preview is disabled in Options.",
            Foreground = Brushes.LightGray,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            IsVisible = disableVideoPreview,
        };
        var previewContent = new Grid
        {
            Children = { previewImage, previewDisabledText },
        };
        var previewStatusText = new TextBlock
        {
            Text = disableVideoPreview
                ? "Live video preview is disabled in Options; only the VBI interface will be opened."
                : OperatingSystem.IsWindows()
                ? "DirectShow preview will appear after a capture device is selected."
                : _ffmpegPath is null
                ? "FFmpeg is required to display the live video preview."
                : "Video preview will appear after a VBI interface is selected.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
        };
        var previewBorder = new Border
        {
            Width = 440,
            // PAL/NTSC analogue video uses non-square stored pixels. Present the
            // preview at its normal 4:3 display aspect instead of the raw 720x576
            // (5:4) storage aspect or the previous 16:9-shaped preview area.
            Height = 330,
            Background = Brushes.Black,
            BorderBrush = new SolidColorBrush(Color.Parse("#3f3f46")),
            BorderThickness = new Thickness(1),
            Child = previewContent,
        };
        var refreshButton = new Button { Content = "Refresh", Width = 90 };
        var useButton = new Button { Content = "Start capture", Width = 110, IsDefault = true };
        var cancelButton = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var recordRawVbiCheckBox = new CheckBox
        {
            Content = "Write raw VBI to temp file to save later",
            IsChecked = _sessionState.RecordRawVbiToDisk == true,
        };
        ToolTip.SetTip(
            recordRawVbiCheckBox,
            "Keeps the complete raw sample stream so it can be saved when capture stops");
        var dialog = new Window
        {
            Title = "Live VBI capture",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Width = 480,
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,*"),
                        ColumnSpacing = 12,
                        Children =
                        {
                            new StackPanel
                            {
                                Spacing = 6,
                                Children =
                                {
                                    new TextBlock { Text = "VBI interface", FontWeight = FontWeight.SemiBold },
                                    interfaceCombo,
                                    cardNameText,
                                },
                            },
                            new StackPanel
                            {
                                [Grid.ColumnProperty] = 1,
                                Spacing = 6,
                                Children =
                                {
                                    new TextBlock { Text = "Capture card preset", FontWeight = FontWeight.SemiBold },
                                    presetCombo,
                                },
                            },
                        },
                    },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,*"),
                        ColumnSpacing = 12,
                        Children =
                        {
                            new StackPanel
                            {
                                Spacing = 6,
                                Children =
                                {
                                    new TextBlock { Text = "Video input", FontWeight = FontWeight.SemiBold },
                                    inputCombo,
                                },
                            },
                            new StackPanel
                            {
                                [Grid.ColumnProperty] = 1,
                                Spacing = 6,
                                Children =
                                {
                                    new TextBlock { Text = "Video standard", FontWeight = FontWeight.SemiBold },
                                    standardCombo,
                                },
                            },
                        },
                    },
                    statusText,
                    previewBorder,
                    previewStatusText,
                    recordRawVbiCheckBox,
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                        ColumnSpacing = 8,
                        Children = { refreshButton, cancelButton, useButton },
                    },
                },
            },
        };
        Grid.SetColumn(refreshButton, 0);
        Grid.SetColumn(cancelButton, 2);
        Grid.SetColumn(useButton, 3);

        LinuxV4l2DeviceInfo? selectedDevice = null;
        DirectShowDeviceInfo? selectedDirectShowDevice = null;
        string? selectedVideoInterface = null;
        Process? previewProcess = null;
        WindowsDirectShowPreview? directShowPreview = null;
        CancellationTokenSource? previewCancellation = null;
        int previewGeneration = 0;
        bool suppressInterfaceSelection = false;
        bool suppressPreviewRestart = false;

        void ClearPreviewImage()
        {
            if (previewImage.Source is IDisposable source)
                source.Dispose();
            previewImage.Source = null;
        }

        void StopPreview()
        {
            directShowPreview?.Dispose();
            directShowPreview = null;
            previewCancellation?.Cancel();
            previewCancellation?.Dispose();
            previewCancellation = null;
            if (previewProcess is { HasExited: false })
            {
                try { previewProcess.Kill(entireProcessTree: true); } catch { }
            }
            previewProcess?.Dispose();
            previewProcess = null;
            previewGeneration++;
        }

        async Task RestartPreviewAsync()
        {
            if (suppressPreviewRestart) return;
            StopPreview();
            ClearPreviewImage();
            if (disableVideoPreview)
            {
                previewStatusText.Text =
                    "Live video preview is disabled in Options; only the VBI interface will be opened.";
                return;
            }
            if (OperatingSystem.IsWindows())
            {
                if (interfaceCombo.SelectedItem is not LiveCaptureInterface captureInterface
                    || inputCombo.SelectedItem is not DirectShowVideoInput selectedDirectShowInput
                    || standardCombo.SelectedItem is not DirectShowVideoStandard selectedDirectShowStandard)
                    return;

                int windowsGeneration = ++previewGeneration;
                previewStatusText.Text = $"Opening DirectShow preview from {captureInterface.Name}…";
                try
                {
                    directShowPreview = await Task.Run(() => new WindowsDirectShowPreview(
                        captureInterface.Name,
                        selectedDirectShowInput,
                        selectedDirectShowStandard,
                        frame => Dispatcher.UIThread.Post(() =>
                        {
                            if (windowsGeneration != previewGeneration) return;
                            try
                            {
                                var bitmap = new WriteableBitmap(
                                    new PixelSize(frame.Width, frame.Height),
                                    new Vector(96, 96),
                                    PixelFormat.Bgra8888,
                                    AlphaFormat.Opaque);
                                using (ILockedFramebuffer locked = bitmap.Lock())
                                {
                                    for (int row = 0; row < frame.Height; row++)
                                        Marshal.Copy(
                                            frame.Bgra, row * frame.Stride,
                                            IntPtr.Add(locked.Address, row * locked.RowBytes),
                                            frame.Stride);
                                }
                                if (previewImage.Source is IDisposable oldSource)
                                    oldSource.Dispose();
                                previewImage.Source = bitmap;
                            }
                            catch (Exception ex)
                            {
                                previewStatusText.Text = $"DirectShow preview frame failed: {ex.Message}";
                            }
                        }, DispatcherPriority.Background)));
                    previewStatusText.Text = $"Live DirectShow preview · {selectedDirectShowInput.Name} · {selectedDirectShowStandard.Name}";
                }
                catch (Exception ex)
                {
                    if (windowsGeneration == previewGeneration)
                        previewStatusText.Text = $"DirectShow preview unavailable: {ex.Message}";
                }
                return;
            }

            if (!OperatingSystem.IsLinux()
                || selectedDevice is null
                || selectedVideoInterface is null
                || inputCombo.SelectedItem is not LinuxV4l2Input selectedInput
                || standardCombo.SelectedItem is not LinuxV4l2Standard selectedStandard)
                return;

            if (_ffmpegPath is null)
            {
                previewStatusText.Text = "FFmpeg was not found; live video preview is unavailable.";
                return;
            }

            int generation = ++previewGeneration;
            previewStatusText.Text = $"Opening preview from {selectedVideoInterface}…";
            try
            {
                await Task.Run(() => LinuxVbiCaptureStream.ConfigureDevice(
                    selectedDevice.Path, selectedInput.Index, selectedStandard.Id));
                if (generation != previewGeneration) return;

                var startInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (string argument in new[]
                         {
                             "-hide_banner", "-loglevel", "error", "-nostdin",
                             "-f", "video4linux2", "-i", selectedVideoInterface,
                             "-vf", "yadif=0:-1:0,fps=25,scale=440:330,setsar=1",
                             "-f", "image2pipe",
                             "-vcodec", "mjpeg", "pipe:1",
                         })
                    startInfo.ArgumentList.Add(argument);

                previewProcess = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Could not start FFmpeg video preview.");
                previewProcess.BeginErrorReadLine();
                previewCancellation = new CancellationTokenSource();
                CancellationToken token = previewCancellation.Token;
                previewStatusText.Text = $"Live preview · 4:3 · 25 fps · {selectedVideoInterface} · {selectedInput.Name} · {selectedStandard.Name}";
                _ = ReadMjpegFramesAsync(previewProcess.StandardOutput.BaseStream, token, frame =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation != previewGeneration) return;
                        try
                        {
                            using var stream = new MemoryStream(frame, writable: false);
                            var bitmap = new Bitmap(stream);
                            if (previewImage.Source is IDisposable oldSource)
                                oldSource.Dispose();
                            previewImage.Source = bitmap;
                        }
                        catch { }
                    }, DispatcherPriority.Background);
                });
            }
            catch (Exception ex)
            {
                if (generation == previewGeneration)
                    previewStatusText.Text = $"Video preview unavailable: {ex.Message}";
            }
        }

        void UpdateStartButton()
        {
            bool linuxSelectionComplete = !OperatingSystem.IsLinux()
                || selectedDevice is not null
                && inputCombo.SelectedItem is LinuxV4l2Input
                && standardCombo.SelectedItem is LinuxV4l2Standard;
            bool windowsSelectionComplete = !OperatingSystem.IsWindows()
                || selectedDirectShowDevice is not null
                && selectedDirectShowDevice.HasVbiPin
                && inputCombo.SelectedItem is DirectShowVideoInput
                && standardCombo.SelectedItem is DirectShowVideoStandard;
            useButton.IsEnabled = interfaceCombo.SelectedItem is LiveCaptureInterface
                                  && presetCombo.SelectedItem is CaptureCardPreset
                                  && linuxSelectionComplete
                                  && windowsSelectionComplete;
        }

        async Task LoadSelectedVbiDeviceAsync()
        {
            selectedDevice = null;
            selectedDirectShowDevice = null;
            selectedVideoInterface = null;
            StopPreview();
            ClearPreviewImage();
            cardNameText.Text = "Capture card: —";
            inputCombo.ItemsSource = null;
            standardCombo.ItemsSource = null;
            inputCombo.IsEnabled = false;
            standardCombo.IsEnabled = false;
            presetCombo.IsEnabled = false;
            statusText.Foreground = Brushes.LightGray;
            UpdateStartButton();
            if (interfaceCombo.SelectedItem is not LiveCaptureInterface captureInterface)
                return;

            if (OperatingSystem.IsWindows())
            {
                statusText.Text = $"Reading DirectShow properties for {captureInterface.Name}…";
                cardNameText.Text = "Capture card: reading…";
                try
                {
                    DirectShowDeviceInfo device = await Task.Run(() =>
                        WindowsDirectShowCapture.QueryDevice(captureInterface.Name));
                    if (interfaceCombo.SelectedItem is not LiveCaptureInterface current
                        || !string.Equals(current.Path, captureInterface.Path, StringComparison.Ordinal))
                        return;

                    selectedDirectShowDevice = device;
                    cardNameText.Text = $"Capture card: {device.Name}";
                    List<DirectShowVideoInput> inputs = device.Inputs.ToList();
                    inputCombo.ItemsSource = inputs;
                    inputCombo.SelectedItem = inputs.FirstOrDefault(input =>
                                                   input.PinIndex == _sessionState.LastLiveCaptureInput)
                                               ?? inputs.FirstOrDefault(input =>
                                                   input.PinIndex == device.CurrentInputPin)
                                               ?? inputs.FirstOrDefault();
                    inputCombo.IsEnabled = inputs.Count > 0;

                    List<DirectShowVideoStandard> standards = device.Standards.ToList();
                    standardCombo.ItemsSource = standards;
                    standardCombo.SelectedItem = standards.FirstOrDefault(standard =>
                                                      (ulong)(int)standard.Value == _sessionState.LastLiveCaptureStandard)
                                                  ?? standards.FirstOrDefault(standard =>
                                                      standard.Value == device.CurrentStandard)
                                                  ?? standards.FirstOrDefault();
                    standardCombo.IsEnabled = standards.Count > 0;
                    presetCombo.IsEnabled = true;
                    if (device.HasVbiPin)
                    {
                        statusText.Foreground = Brushes.LightGreen;
                        statusText.Text = string.IsNullOrWhiteSpace(device.VbiPinName)
                            ? "VBI output detected. Inputs and analogue TV standards are reported by the DirectShow driver."
                            : $"VBI output detected: {device.VbiPinName}. Inputs and analogue TV standards are reported by the DirectShow driver.";
                    }
                    else
                    {
                        statusText.Foreground = Brushes.OrangeRed;
                        statusText.Text = "This capture device does not expose a DirectShow VBI output pin. Live VBI capture is unavailable.";
                    }
                }
                catch (Exception ex)
                {
                    statusText.Foreground = Brushes.OrangeRed;
                    cardNameText.Text = "Capture card: unavailable";
                    statusText.Text = $"Could not inspect {captureInterface.Name}: {ex.Message}";
                }
                UpdateStartButton();
                return;
            }

            if (!OperatingSystem.IsLinux())
            {
                presetCombo.IsEnabled = true;
                UpdateStartButton();
                return;
            }

            statusText.Text = $"Reading {captureInterface.Path}…";
            cardNameText.Text = "Capture card: reading…";
            try
            {
                LinuxV4l2DeviceInfo device = await Task.Run(() =>
                    LinuxVbiCaptureStream.QueryDevice(captureInterface.Path));
                if (interfaceCombo.SelectedItem is not LiveCaptureInterface current
                    || !string.Equals(current.Path, captureInterface.Path, StringComparison.Ordinal))
                    return;

                selectedDevice = device;
                selectedVideoInterface = await Task.Run(() => FindRelatedLinuxVideoInterface(device.BusInfo));
                cardNameText.Text = $"Capture card: {device.Card}";
                suppressPreviewRestart = true;
                List<LinuxV4l2Input> inputs = device.Inputs.ToList();
                inputCombo.ItemsSource = inputs;
                inputCombo.SelectedItem = inputs.FirstOrDefault(input =>
                                               input.Index == _sessionState.LastLiveCaptureInput)
                                           ?? inputs.FirstOrDefault(input => input.Index == device.CurrentInputIndex)
                                           ?? inputs.FirstOrDefault();
                inputCombo.IsEnabled = inputs.Count > 0;
                RefreshStandardsForInput();
                suppressPreviewRestart = false;
                presetCombo.IsEnabled = true;
                statusText.Text = selectedVideoInterface is null
                    ? $"{device.Driver} · no related video interface was found."
                    : $"{device.Driver} · video interface {selectedVideoInterface}";
                if (selectedVideoInterface is null)
                    previewStatusText.Text = "No video interface belonging to this VBI device was found.";
                await RestartPreviewAsync();
            }
            catch (Exception ex)
            {
                cardNameText.Text = "Capture card: unavailable";
                statusText.Text = $"Could not inspect {captureInterface.Path}: {ex.Message}";
            }
            UpdateStartButton();
        }

        void RefreshStandardsForInput()
        {
            if (selectedDevice is null || inputCombo.SelectedItem is not LinuxV4l2Input input)
            {
                standardCombo.ItemsSource = null;
                standardCombo.IsEnabled = false;
                UpdateStartButton();
                return;
            }

            List<LinuxV4l2Standard> standards = selectedDevice.Standards
                .Where(standard => (standard.Id & input.SupportedStandards) != 0)
                .ToList();
            standardCombo.ItemsSource = standards;
            standardCombo.SelectedItem = standards.FirstOrDefault(standard =>
                                                 standard.Id == _sessionState.LastLiveCaptureStandard)
                                             ?? standards.FirstOrDefault(standard =>
                                                 standard.Id == selectedDevice.CurrentStandardId)
                                             ?? standards.FirstOrDefault(standard =>
                                                 (standard.Id & selectedDevice.CurrentStandardId) != 0)
                                             ?? standards.FirstOrDefault();
            standardCombo.IsEnabled = standards.Count > 0;
            UpdateStartButton();
        }

        async Task RefreshInterfacesAsync()
        {
            refreshButton.IsEnabled = false;
            useButton.IsEnabled = false;
            statusText.Foreground = Brushes.LightGray;
            statusText.Text = "Searching for capture interfaces…";
            List<LiveCaptureInterface> interfaces = await DiscoverLiveCaptureInterfacesAsync();
            interfaceCombo.ItemsSource = interfaces;
            suppressInterfaceSelection = true;
            interfaceCombo.SelectedItem = interfaces.FirstOrDefault(item =>
                                                  string.Equals(item.Path, _sessionState.LastLiveCaptureInterface, StringComparison.Ordinal))
                                              ?? interfaces.FirstOrDefault();
            suppressInterfaceSelection = false;
            statusText.Text = interfaces.Count > 0
                ? OperatingSystem.IsMacOS()
                    ? $"Found {interfaces.Count} serial interface(s). The device must provide raw VBI samples using the selected card configuration."
                    : OperatingSystem.IsLinux()
                        ? $"Found {interfaces.Count} Linux VBI device(s)."
                        : $"Found {interfaces.Count} DirectShow video capture device(s). VBI-pin validation occurs when the live transport is opened."
                : OperatingSystem.IsMacOS()
                    ? "No serial interfaces were found under /dev/cu.* or /dev/tty.*."
                    : OperatingSystem.IsLinux()
                        ? "No /dev/vbi* devices were found. Check the capture driver and device permissions."
                        : "No DirectShow video capture devices were reported by Windows.";
            refreshButton.IsEnabled = true;
            await LoadSelectedVbiDeviceAsync();
        }

        presetCombo.SelectionChanged += (_, _) => UpdateStartButton();
        recordRawVbiCheckBox.IsCheckedChanged += (_, _) =>
        {
            _sessionState.RecordRawVbiToDisk = recordRawVbiCheckBox.IsChecked == true;
            SaveSessionState();
        };
        inputCombo.SelectionChanged += async (_, _) =>
        {
            if (OperatingSystem.IsLinux()) RefreshStandardsForInput();
            else UpdateStartButton();
            await RestartPreviewAsync();
        };
        standardCombo.SelectionChanged += async (_, _) =>
        {
            UpdateStartButton();
            await RestartPreviewAsync();
        };
        interfaceCombo.SelectionChanged += async (_, _) =>
        {
            if (!suppressInterfaceSelection)
                await LoadSelectedVbiDeviceAsync();
        };
        refreshButton.Click += async (_, _) => await RefreshInterfacesAsync();
        cancelButton.Click += (_, _) => dialog.Close();
        useButton.Click += async (_, _) =>
        {
            if (presetCombo.SelectedItem is not CaptureCardPreset preset
                || interfaceCombo.SelectedItem is not LiveCaptureInterface captureInterface)
                return;
            LinuxV4l2Input? captureInput = inputCombo.SelectedItem as LinuxV4l2Input;
            LinuxV4l2Standard? captureStandard = standardCombo.SelectedItem as LinuxV4l2Standard;
            DirectShowVideoInput? directShowInput = inputCombo.SelectedItem as DirectShowVideoInput;
            DirectShowVideoStandard? directShowStandard = standardCombo.SelectedItem as DirectShowVideoStandard;
            if (OperatingSystem.IsLinux() && (captureInput is null || captureStandard is null))
                return;
            if (OperatingSystem.IsWindows() && (directShowInput is null || directShowStandard is null))
                return;
            _sessionState.LastCaptureCardPresetName = preset.Name;
            _sessionState.LastLiveCaptureInterface = captureInterface.Path;
            _sessionState.LastLiveCaptureInput = captureInput?.Index ?? directShowInput?.PinIndex;
            _sessionState.LastLiveCaptureStandard = captureStandard?.Id
                ?? (directShowStandard is null ? null : (ulong)(int)directShowStandard.Value);
            SaveSessionState();
            StopPreview();
            dialog.Close();
            if (OperatingSystem.IsLinux())
                await StartLiveVbiCaptureAsync(
                    captureInterface, preset, selectedVideoInterface,
                    recordRawVbiCheckBox.IsChecked == true,
                    captureInput, captureStandard, null, null);
            else if (OperatingSystem.IsWindows())
                await StartLiveVbiCaptureAsync(
                    captureInterface, preset, null,
                    recordRawVbiCheckBox.IsChecked == true,
                    null, null, directShowInput, directShowStandard);
        };

        dialog.Closed += (_, _) =>
        {
            StopPreview();
            ClearPreviewImage();
        };
        dialog.Opened += async (_, _) => await RefreshInterfacesAsync();
        await dialog.ShowDialog(this);
    }

    private async Task StartLiveVbiCaptureAsync(
        LiveCaptureInterface captureInterface,
        CaptureCardPreset preset,
        string? videoInterfacePath,
        bool recordRawVbi,
        LinuxV4l2Input? linuxInput,
        LinuxV4l2Standard? linuxStandard,
        DirectShowVideoInput? directShowInput,
        DirectShowVideoStandard? directShowStandard)
    {
        LiveVbiCaptureStream? input = null;
        try
        {
            if (OperatingSystem.IsLinux() && linuxInput is not null && linuxStandard is not null)
                input = new LinuxVbiCaptureStream(
                    captureInterface.Path, linuxInput.Index, linuxStandard.Id);
            else if (OperatingSystem.IsWindows() && directShowInput is not null && directShowStandard is not null)
                input = new WindowsDirectShowVbiCaptureStream(
                    captureInterface.Name, directShowInput, directShowStandard,
                    preset.LineLength, preset.FieldLines,
                    enableVideoPreview: !(_sessionState.DisableLiveVbiVideoPreview ?? false));
            else
                throw new PlatformNotSupportedException("No live VBI transport is available for this platform.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not open live VBI capture", ex.Message);
            return;
        }

        await using (input)
        {
            var options = new VbiCaptureOptions(
                preset.Name,
                input.SamplingRate,
                input.SamplesPerLine,
                preset.LineStart,
                preset.LineStartEnd,
                IsUInt16: false,
                FieldLines: input.LinesPerFrame,
                FieldRangeStart: 0,
                FieldRangeEnd: input.LinesPerFrame,
                StandardDeviationThreshold: preset.StandardDeviationThreshold,
                SignalLevelThreshold: preset.SignalLevelThreshold,
                CriFcRangeThreshold: preset.CriFcRangeThreshold,
                CriFcConfidenceThreshold: preset.CriFcConfidenceThreshold);
            string temporaryOutput = Path.Combine(
                Path.GetTempPath(), $"TeletextRecoveReese-live-{Guid.NewGuid():N}.t42");
            string? temporaryRawCapture = recordRawVbi
                ? Path.Combine(
                    Path.GetTempPath(), $"TeletextRecoveReese-live-{Guid.NewGuid():N}.vbi")
                : null;
            using var cancellation = new CancellationTokenSource();
            using var videoPreviewCancellation = new CancellationTokenSource();
            var phaseText = new TextBlock
            {
                Text = $"Opening {captureInterface.Name}…",
                TextWrapping = TextWrapping.Wrap,
            };
            var detailText = new TextBlock { Foreground = Brushes.LightGray };
            var timingText = new TextBlock { Foreground = Brushes.LightGray };
            var progressBar = new ProgressBar
            {
                Width = 500,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsIndeterminate = true,
            };
            int rawPreviewSamples = Math.Min(
                input.SamplesPerLine,
                (int)Math.Ceiling(input.SamplingRate / 6_937_500.0 * 368)
                + Math.Max(preset.LineStartEnd, 0) + 300 + 32);
            int rawPreviewWidth = rawPreviewSamples;
            int rawPreviewHeight = Math.Max(
                input.FirstFieldLines, input.SecondFieldLines) * 10;
            var rawPreviewBitmap = new WriteableBitmap(
                new PixelSize(rawPreviewWidth, rawPreviewHeight),
                new Vector(96, 96),
                PixelFormats.Bgra8888,
                AlphaFormat.Premul);
            var rawPreviewImage = new Image
            {
                Source = rawPreviewBitmap,
                Width = 500,
                Height = 135,
                Stretch = Stretch.Fill,
            };
            var rawPreviewTitle = new TextBlock
            {
                Text = "Raw VBI input — sampled preview",
                FontWeight = FontWeight.SemiBold,
            };
            var rawPreviewFieldCombo = new ComboBox
            {
                Width = 95,
                ItemsSource = new[] { "Field 1", "Field 2" },
                SelectedIndex = 0,
                IsEnabled = input.SecondFieldLines > 0,
            };
            var rawPreviewHeader = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { rawPreviewTitle, rawPreviewFieldCombo },
            };
            Grid.SetColumn(rawPreviewFieldCombo, 1);
            var rawPreviewBorder = new Border
            {
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.Parse("#3f3f46")),
                BorderThickness = new Thickness(1),
                Child = rawPreviewImage,
            };
            var rawPreviewInfoText = new TextBlock
            {
                Text = "Waiting for the first raw VBI frame…",
                Foreground = Brushes.LightGray,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Height = 22,
            };
            const int videoPreviewWidth = 240;
            const int videoPreviewHeight = 180;
            bool disableVideoPreview = _sessionState.DisableLiveVbiVideoPreview ?? false;
            var videoPreviewBitmap = new WriteableBitmap(
                new PixelSize(videoPreviewWidth, videoPreviewHeight),
                new Vector(96, 96),
                PixelFormats.Bgra8888,
                AlphaFormat.Premul);
            var videoPreviewImage = new Image
            {
                Source = videoPreviewBitmap,
                Width = videoPreviewWidth,
                Height = videoPreviewHeight,
                Stretch = Stretch.Uniform,
                IsVisible = !disableVideoPreview,
            };
            var videoPreviewDisabledText = new TextBlock
            {
                Text = "Live video preview is disabled in Options.",
                Foreground = Brushes.LightGray,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12),
                IsVisible = disableVideoPreview,
            };
            var videoPreviewContent = new Grid
            {
                Children = { videoPreviewImage, videoPreviewDisabledText },
            };
            var videoPreviewBorder = new Border
            {
                Width = 240,
                Height = 180,
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.Parse("#3f3f46")),
                BorderThickness = new Thickness(1),
                Child = videoPreviewContent,
            };
            var videoPreviewInfoText = new TextBlock
            {
                Text = disableVideoPreview
                    ? "Only the VBI interface is active; video preview was not opened."
                    : videoInterfacePath is null && input is not WindowsDirectShowVbiCaptureStream
                    ? "No related video interface was found."
                    : "Waiting for the first raw YUV video frame…",
                Foreground = Brushes.LightGray,
                TextWrapping = TextWrapping.Wrap,
            };
            var showRawPreviewCheckBox = new CheckBox
            {
                Content = "Raw VBI preview (sampled, up to 10 fps)",
                IsChecked = _sessionState.ShowRawVbiPreview ?? true,
            };
            bool showRawPreview = showRawPreviewCheckBox.IsChecked == true;
            int rawPreviewEnabled = showRawPreview ? 1 : 0;
            var showVideoPreviewCheckBox = new CheckBox
            {
                Content = OperatingSystem.IsWindows()
                    ? "Live video preview (real time)"
                    : "Live video preview (every 5 seconds)",
                IsChecked = !disableVideoPreview && (_sessionState.ShowVideoCapturePreview ?? true),
                IsEnabled = !disableVideoPreview
                            && (videoInterfacePath is not null || input is WindowsDirectShowVbiCaptureStream),
            };
            int videoPreviewEnabled = showVideoPreviewCheckBox.IsChecked == true ? 1 : 0;
            var showLiveCheckBox = new CheckBox
            {
                Content = "Show deconvolved page",
                IsChecked = _sessionState.ShowLiveDeconvolvedPage ?? true,
            };
            var livePageLockTextBox = new TextBox
            {
                Width = 110,
                MaxLength = 3,
                PlaceholderText = "Page",
                IsEnabled = showLiveCheckBox.IsChecked == true,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(
                livePageLockTextBox,
                "Optional three-digit hexadecimal Teletext page, for example 100, 205 or 8FF");
            var showLiveControls = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children = { showLiveCheckBox, livePageLockTextBox },
            };
            var runDeconvolutionCheckBox = new CheckBox
            {
                Content = "Live deconvolution",
                IsChecked = true,
            };
            var deconvolutionControl = new ToggleableDeconvolutionControl(
                true, input.LinesPerFrame);
            var resetAllClockOffsetsButton = new Button
            {
                Content = "Reset all",
                Width = 105,
                IsEnabled = !VbiDeconvolutionEngine.UseLegacyFixedDetectionForTest,
            };
            var autoSearchStatus = new TextBlock
            {
                Text = VbiDeconvolutionEngine.UseLegacyFixedDetectionForTest
                    ? "Test mode: fixed 64 / 28 / 0.35 thresholds; line offsets are ignored."
                    : "Enable CRI start detection for each line that should be tracked.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            };
            const int clockBankSize = 5;
            int adjustableLineCount = input.FirstFieldLines;
            int clockBankCount = Math.Max(
                1, (adjustableLineCount + clockBankSize - 1) / clockBankSize);
            var clockBankCombo = new ComboBox
            {
                Width = 105,
                ItemsSource = Enumerable.Range(0, clockBankCount)
                    .Select(bank =>
                    {
                        int start = bank * clockBankSize;
                        int end = Math.Min(start + clockBankSize - 1, adjustableLineCount - 1);
                        return $"Lines {start + 1}–{end + 1}";
                    })
                    .ToArray(),
                SelectedIndex = 0,
            };
            var clockBankControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { clockBankCombo, resetAllClockOffsetsButton },
            };
            Grid.SetColumn(clockBankControls, 1);
            var autoTrackLineCheckBoxes = new CheckBox[5];
            var autoEndFitCheckBoxes = new CheckBox[5];
            var decodeFirstFieldCheckBoxes = new CheckBox[5];
            var decodeSecondFieldCheckBoxes = new CheckBox[5];
            var clockOffsetValueTexts = new TextBlock[5];
            var clockOffsetRows = new Grid[5];
            var visibleClockLines = Enumerable.Repeat(-1, clockBankSize).ToArray();
            var lastDetectedClockStarts = Enumerable.Repeat(-1, input.LinesPerFrame).ToArray();
            var autoEndFitEnabled = new int[input.LinesPerFrame];
            var autoEndSpanHistories = Enumerable.Range(0, input.LinesPerFrame)
                .Select(_ => new Queue<double>())
                .ToArray();
            int autoTrackFirstFieldLineMask = 0;
            int autoTrackSecondFieldLineMask = 0;
            int selectedRawPreviewField = 0;
            int selectedClockBank = 0;
            bool updatingClockBank = false;
            var clockOffsetsPanel = new StackPanel
            {
                Width = 500,
                Spacing = 3,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "CRI tracking",
                                Foreground = Brushes.LightGray,
                                VerticalAlignment = VerticalAlignment.Center,
                            },
                            clockBankControls,
                        },
                    },
                    new TextBlock
                    {
                        Text = $"Preset thresholds: std-dev {preset.StandardDeviationThreshold:0.##} · signal {preset.SignalLevelThreshold:0.##} · CRI/FC range {preset.CriFcRangeThreshold:0.##} · confidence {preset.CriFcConfidenceThreshold:0.##}",
                        Foreground = Brushes.Gray,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    autoSearchStatus,
                },
            };
            clockOffsetsPanel.IsEnabled =
                !VbiDeconvolutionEngine.UseLegacyFixedDetectionForTest;
            for (int slot = 0; slot < clockBankSize; slot++)
            {
                int sliderSlot = slot;
                var valueText = new TextBlock
                {
                    Text = $"Line {sliderSlot + 1}: 0",
                    Width = 110,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    TextAlignment = TextAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                clockOffsetValueTexts[sliderSlot] = valueText;
                var autoTrackCheckBox = new CheckBox
                {
                    Content = "CRI start detection",
                    VerticalAlignment = VerticalAlignment.Center,
                };
                autoTrackLineCheckBoxes[sliderSlot] = autoTrackCheckBox;
                ToolTip.SetTip(
                    autoTrackCheckBox,
                    "Auto-detect this physical line in the currently selected field on every rendered VBI preview frame");
                var autoEndFitCheckBox = new CheckBox
                {
                    Content = "Auto end",
                    VerticalAlignment = VerticalAlignment.Center,
                };
                autoEndFitCheckBoxes[sliderSlot] = autoEndFitCheckBox;
                ToolTip.SetTip(
                    autoEndFitCheckBox,
                    "Automatically find the end of the VBI burst and linearly fit all 360 bits between the detected start and end");
                var decodeFirstFieldCheckBox = new CheckBox
                {
                    Content = $"D{sliderSlot + 1}",
                    IsChecked = true,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var decodeSecondFieldCheckBox = new CheckBox
                {
                    Content = $"D{sliderSlot + input.FirstFieldLines + 1}",
                    IsChecked = true,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                decodeFirstFieldCheckBoxes[sliderSlot] = decodeFirstFieldCheckBox;
                decodeSecondFieldCheckBoxes[sliderSlot] = decodeSecondFieldCheckBox;
                ToolTip.SetTip(
                    decodeFirstFieldCheckBox,
                    "Decode this physical VBI line from the first field");
                ToolTip.SetTip(
                    decodeSecondFieldCheckBox,
                    "Decode this physical VBI line from the second field");
                autoTrackCheckBox.IsCheckedChanged += (_, _) =>
                {
                    if (updatingClockBank) return;
                    int line = visibleClockLines[sliderSlot];
                    if (line < 0) return;
                    int bit = 1 << line;
                    int field = Volatile.Read(ref selectedRawPreviewField);
                    int physicalLine = field == 0
                        ? line
                        : line + input.FirstFieldLines;
                    if (field == 0)
                    {
                        int mask = Volatile.Read(ref autoTrackFirstFieldLineMask);
                        Volatile.Write(
                            ref autoTrackFirstFieldLineMask,
                            autoTrackCheckBox.IsChecked == true ? mask | bit : mask & ~bit);
                    }
                    else
                    {
                        int mask = Volatile.Read(ref autoTrackSecondFieldLineMask);
                        Volatile.Write(
                            ref autoTrackSecondFieldLineMask,
                            autoTrackCheckBox.IsChecked == true ? mask | bit : mask & ~bit);
                    }
                    if (autoTrackCheckBox.IsChecked != true)
                    {
                        deconvolutionControl.SetClockSearchOffset(physicalLine, 0);
                        if (physicalLine < lastDetectedClockStarts.Length)
                            Volatile.Write(ref lastDetectedClockStarts[physicalLine], -1);
                        valueText.Text = $"Line {physicalLine + 1}: 0";
                    }
                };
                autoEndFitCheckBox.IsCheckedChanged += (_, _) =>
                {
                    if (updatingClockBank) return;
                    int line = visibleClockLines[sliderSlot];
                    if (line < 0) return;
                    int physicalLine = Volatile.Read(ref selectedRawPreviewField) == 0
                        ? line
                        : line + input.FirstFieldLines;
                    if (physicalLine >= input.LinesPerFrame) return;
                    bool enabled = autoEndFitCheckBox.IsChecked == true;
                    Volatile.Write(
                        ref autoEndFitEnabled[physicalLine], enabled ? 1 : 0);
                    lock (autoEndSpanHistories[physicalLine])
                        autoEndSpanHistories[physicalLine].Clear();
                    if (enabled)
                    {
                        if (autoTrackCheckBox.IsChecked != true)
                            autoTrackCheckBox.IsChecked = true;
                        double nominalSpan = 360.0 * input.SamplingRate / 6_937_500.0;
                        deconvolutionControl.SetManualPacketSpanSamples(
                            physicalLine, nominalSpan);
                    }
                    else
                    {
                        deconvolutionControl.SetManualPacketSpanSamples(physicalLine, -1);
                    }
                };
                decodeFirstFieldCheckBox.IsCheckedChanged += (_, _) =>
                {
                    if (updatingClockBank) return;
                    int line = visibleClockLines[sliderSlot];
                    if (line < 0) return;
                    deconvolutionControl.SetLineDecodingEnabled(
                        line, decodeFirstFieldCheckBox.IsChecked == true);
                };
                decodeSecondFieldCheckBox.IsCheckedChanged += (_, _) =>
                {
                    if (updatingClockBank) return;
                    int line = visibleClockLines[sliderSlot];
                    if (line < 0) return;
                    int secondFieldLine = line + input.FirstFieldLines;
                    if (secondFieldLine < input.LinesPerFrame)
                        deconvolutionControl.SetLineDecodingEnabled(
                            secondFieldLine,
                            decodeSecondFieldCheckBox.IsChecked == true);
                };
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
                    ColumnSpacing = 8,
                    Children =
                    {
                        valueText, autoTrackCheckBox, autoEndFitCheckBox,
                        decodeFirstFieldCheckBox, decodeSecondFieldCheckBox,
                    },
                };
                Grid.SetColumn(autoTrackCheckBox, 1);
                Grid.SetColumn(autoEndFitCheckBox, 2);
                Grid.SetColumn(decodeFirstFieldCheckBox, 3);
                Grid.SetColumn(decodeSecondFieldCheckBox, 4);
                clockOffsetRows[sliderSlot] = row;
                clockOffsetsPanel.Children.Add(row);
            }

            void UpdateClockBank()
            {
                updatingClockBank = true;
                try
                {
                    int bankStart = Math.Max(clockBankCombo.SelectedIndex, 0) * clockBankSize;
                    Volatile.Write(ref selectedClockBank, Math.Max(clockBankCombo.SelectedIndex, 0));
                    int selectedField = Volatile.Read(ref selectedRawPreviewField);
                    int trackedMask = selectedField == 0
                        ? Volatile.Read(ref autoTrackFirstFieldLineMask)
                        : Volatile.Read(ref autoTrackSecondFieldLineMask);
                    for (int slot = 0; slot < clockBankSize; slot++)
                    {
                        int line = bankStart + slot;
                        bool valid = line < adjustableLineCount;
                        visibleClockLines[slot] = valid ? line : -1;
                        clockOffsetRows[slot].IsEnabled = valid;
                        if (!valid)
                        {
                            clockOffsetValueTexts[slot].Text = "Unused";
                            autoTrackLineCheckBoxes[slot].IsChecked = false;
                            autoEndFitCheckBoxes[slot].IsChecked = false;
                            decodeFirstFieldCheckBoxes[slot].IsChecked = false;
                            decodeSecondFieldCheckBoxes[slot].IsChecked = false;
                            continue;
                        }

                        int physicalLine = selectedField == 0
                            ? line
                            : line + input.FirstFieldLines;
                        int offset = deconvolutionControl.GetClockSearchOffset(physicalLine);
                        clockOffsetValueTexts[slot].Text =
                            $"Line {physicalLine + 1}: {offset:+0;-0;0}";
                        autoTrackLineCheckBoxes[slot].IsChecked =
                            (trackedMask & (1 << line)) != 0;
                        autoEndFitCheckBoxes[slot].IsChecked =
                            Volatile.Read(ref autoEndFitEnabled[physicalLine]) != 0;
                        decodeFirstFieldCheckBoxes[slot].Content = $"D{line + 1}";
                        decodeFirstFieldCheckBoxes[slot].IsChecked =
                            deconvolutionControl.GetLineDecodingEnabled(line);
                        int secondFieldLine = line + input.FirstFieldLines;
                        bool secondFieldLineValid = secondFieldLine < input.LinesPerFrame;
                        decodeSecondFieldCheckBoxes[slot].Content =
                            secondFieldLineValid ? $"D{secondFieldLine + 1}" : "Unused";
                        decodeSecondFieldCheckBoxes[slot].IsEnabled = secondFieldLineValid;
                        decodeSecondFieldCheckBoxes[slot].IsChecked =
                            secondFieldLineValid
                            && deconvolutionControl.GetLineDecodingEnabled(secondFieldLine);
                    }
                }
                finally
                {
                    updatingClockBank = false;
                }
            }
            clockBankCombo.SelectionChanged += (_, _) => UpdateClockBank();
            UpdateClockBank();
            long fpsBaselineFrames = 0;
            long fpsBaselineTimestamp = Stopwatch.GetTimestamp();
            var stopButton = new Button
            {
                Content = "Stop capture",
                Width = 110,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var dialog = new Window
            {
                Title = "Live VBI capture",
                Height = Math.Clamp(ClientSize.Height - 80, 520, 700),
                SizeToContent = SizeToContent.Width,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new Grid
                {
                    Width = 826,
                    RowDefinitions = new RowDefinitions("*,Auto"),
                    Children =
                    {
                        new ScrollViewer
                        {
                            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                            Content = new StackPanel
                            {
                                Width = 790,
                                Margin = new Thickness(18, 18, 18, 10),
                                Spacing = 12,
                                Children =
                                {
                                    new Grid
                                    {
                                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                                        ColumnSpacing = 14,
                                        Children =
                                        {
                                            new StackPanel
                                            {
                                                Spacing = 8,
                                                Children =
                                                {
                                                    rawPreviewHeader,
                                                    rawPreviewBorder,
                                                    rawPreviewInfoText,
                                                    clockOffsetsPanel,
                                                },
                                            },
                                            new StackPanel
                                            {
                                                [Grid.ColumnProperty] = 1,
                                                Width = 240,
                                                Spacing = 8,
                                                Children =
                                                {
                                                    new TextBlock
                                                    {
                                                        Text = "Video input",
                                                        FontWeight = FontWeight.SemiBold,
                                                    },
                                                    videoPreviewBorder,
                                                    videoPreviewInfoText,
                                                    showRawPreviewCheckBox,
                                                    showVideoPreviewCheckBox,
                                                    runDeconvolutionCheckBox,
                                                    showLiveControls,
                                                },
                                            },
                                        },
                                    },
                                    phaseText,
                                    progressBar,
                                    detailText,
                                    timingText,
                                },
                            },
                        },
                        new Border
                        {
                            [Grid.RowProperty] = 1,
                            Padding = new Thickness(18, 8, 18, 12),
                            Child = stopButton,
                        },
                    },
                },
            };
            bool allowClose = false;
            bool rawPreviewActive = true;
            bool videoPreviewActive = true;
            long lastRawPreviewTimestamp = 0;
            long rawPreviewFrameNumber = 0;
            int rawPreviewHasFrame = 0;
            int rawPreviewWorkerBusy = 0;
            int videoSnapshotNumber = 0;
            int directShowVideoFramePending = 0;
            CancellationTokenSource? videoPreviewPipeCancellation = null;
            if (!disableVideoPreview && input is WindowsDirectShowVbiCaptureStream directShowCapture)
            {
                directShowCapture.TryAcquireVideoFrame = () =>
                    videoPreviewActive
                    && Volatile.Read(ref videoPreviewEnabled) != 0
                    && Interlocked.CompareExchange(ref directShowVideoFramePending, 1, 0) == 0;
                directShowCapture.ReleaseVideoFrame = () =>
                    Interlocked.Exchange(ref directShowVideoFramePending, 0);
                directShowCapture.VideoFrameCaptured = frame =>
                {
                    byte[] pixels = ScaleBgraFrame(
                        frame.Bgra, frame.Width, frame.Height, frame.Stride,
                        videoPreviewWidth, videoPreviewHeight);
                    int frameNumber = Interlocked.Increment(ref videoSnapshotNumber);
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (!videoPreviewActive) return;
                            UpdateBgraBitmap(videoPreviewBitmap, pixels, videoPreviewWidth, videoPreviewHeight);
                            videoPreviewImage.InvalidateVisual();
                            videoPreviewInfoText.Text =
                                $"DirectShow live video · frame {frameNumber:N0} · {frame.Width}×{frame.Height}";
                        }
                        finally
                        {
                            Interlocked.Exchange(ref directShowVideoFramePending, 0);
                        }
                    }, DispatcherPriority.Render);
                };
            }
            rawPreviewFieldCombo.SelectionChanged += (_, _) =>
            {
                Volatile.Write(
                    ref selectedRawPreviewField,
                    Math.Clamp(rawPreviewFieldCombo.SelectedIndex, 0, 1));
                Interlocked.Exchange(ref rawPreviewHasFrame, 0);
                Interlocked.Exchange(ref lastRawPreviewTimestamp, 0);
                UpdateClockBank();
            };
            input.RawFrameCaptured = rawFrame =>
            {
                if (!rawPreviewActive) return;
                int firstFieldTrackedMask =
                    Volatile.Read(ref autoTrackFirstFieldLineMask);
                int secondFieldTrackedMask =
                    Volatile.Read(ref autoTrackSecondFieldLineMask);
                bool liveUpdate = Volatile.Read(ref rawPreviewEnabled) != 0;
                bool renderPreview = liveUpdate || Volatile.Read(ref rawPreviewHasFrame) == 0;
                if (renderPreview)
                {
                    long now = Stopwatch.GetTimestamp();
                    long previous = Interlocked.Read(ref lastRawPreviewTimestamp);
                    const int rawPreviewMaximumFps = 10;
                    if (now - previous < Stopwatch.Frequency / rawPreviewMaximumFps
                        || Interlocked.CompareExchange(ref lastRawPreviewTimestamp, now, previous) != previous)
                        renderPreview = false;
                }
                bool periodicAutoSearch = renderPreview
                    && (firstFieldTrackedMask != 0 || secondFieldTrackedMask != 0);
                bool runAutoSearch = periodicAutoSearch;
                if (!renderPreview && !runAutoSearch) return;

                if (Interlocked.CompareExchange(ref rawPreviewWorkerBusy, 1, 0) != 0)
                    return;

                // LinuxVbiCaptureStream reuses its frame buffer. Copy it quickly,
                // then do all analysis and bitmap scaling away from the VBI reader.
                byte[] snapshot = (byte[])rawFrame.Clone();
                int previewField = Volatile.Read(ref selectedRawPreviewField);
                _ = Task.Run(() =>
                {
                    try
                    {
                        int previewFirstLine = previewField == 0
                            ? 0
                            : input.FirstFieldLines;
                        int previewLineCount = previewField == 0
                            ? input.FirstFieldLines
                            : input.SecondFieldLines;
                        var autoStartAcceptedThisFrame =
                            new bool[input.LinesPerFrame];
                        if (runAutoSearch)
                        {
                            var detectedUpdates = new List<(
                                int Field, int PhysicalLine, int Offset)>();
                            int rejectedJumps = 0;
                            DetectField(
                                field: 0,
                                firstLine: 0,
                                lineCount: input.FirstFieldLines,
                                firstFieldTrackedMask);
                            DetectField(
                                field: 1,
                                firstLine: input.FirstFieldLines,
                                lineCount: input.SecondFieldLines,
                                secondFieldTrackedMask);

                            void DetectField(
                                int field,
                                int firstLine,
                                int lineCount,
                                int trackedMask)
                            {
                                if (trackedMask == 0 || lineCount <= 0) return;
                                byte[] fieldFrame = firstLine == 0
                                    ? snapshot
                                    : snapshot.AsSpan(
                                        firstLine * input.SamplesPerLine,
                                        lineCount * input.SamplesPerLine).ToArray();
                                int?[] detectedOffsets =
                                    VbiDeconvolutionEngine.FindClockRunInOffsets(
                                        fieldFrame, options,
                                        Math.Min(lineCount, adjustableLineCount),
                                        maximumOffsetSamples: input.SamplesPerLine / 2,
                                        lineMask: trackedMask);
                                for (int line = 0; line < detectedOffsets.Length; line++)
                                {
                                    if ((trackedMask & (1 << line)) == 0) continue;
                                    if (detectedOffsets[line] is not int offset) continue;
                                    int physicalLine = firstLine + line;
                                    int detectedStart = preset.LineStart + offset;
                                    // Keep CRI tracking deliberately simple: a
                                    // slightly clipped negative start is valid,
                                    // but nothing in the latter half of the raw
                                    // line can be the clock run-in.
                                    bool accepted = detectedStart < input.SamplesPerLine / 2;
                                    if (!accepted)
                                    {
                                        rejectedJumps++;
                                        continue;
                                    }
                                    autoStartAcceptedThisFrame[physicalLine] = true;
                                    Volatile.Write(
                                        ref lastDetectedClockStarts[physicalLine],
                                        detectedStart);
                                    deconvolutionControl.SetClockSearchOffset(
                                        physicalLine, offset);
                                    detectedUpdates.Add((field, physicalLine, offset));
                                }
                            }
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (!rawPreviewActive) return;
                                UpdateClockBank();
                                int found = detectedUpdates.Count;
                                int searched =
                                    System.Numerics.BitOperations.PopCount(
                                        (uint)firstFieldTrackedMask)
                                    + System.Numerics.BitOperations.PopCount(
                                        (uint)secondFieldTrackedMask);
                                autoSearchStatus.Text =
                                    $"CRI tracking updated {found} of {searched} enabled physical VBI lines across both fields"
                                    + (rejectedJumps > 0
                                        ? $"; rejected {rejectedJumps} implausible jump(s)."
                                        : ".");
                            });
                        }

                        bool peakFitUpdated = false;
                        for (int physicalLine = 0;
                             physicalLine < input.LinesPerFrame;
                             physicalLine++)
                        {
                            double span = deconvolutionControl
                                .GetManualPacketSpanSamples(physicalLine);
                            if (span < 0) continue;

                            int start = Volatile.Read(
                                ref lastDetectedClockStarts[physicalLine]);
                            if (start < 0)
                                start = preset.LineStart
                                    + deconvolutionControl.GetClockSearchOffset(physicalLine);

                            if (Volatile.Read(
                                    ref autoEndFitEnabled[physicalLine]) != 0)
                            {
                                if (!autoStartAcceptedThisFrame[physicalLine])
                                    continue;
                                double nominalSpan = 360.0 * input.SamplingRate
                                    / 6_937_500.0;
                                if (!TryFindRawVbiBurstEnd(
                                        snapshot, input.SamplesPerLine,
                                        physicalLine, start, nominalSpan,
                                        preset.CriFcRangeThreshold,
                                        out double detectedEnd))
                                    continue;

                                double detectedSpan = detectedEnd - start;
                                Queue<double> history =
                                    autoEndSpanHistories[physicalLine];
                                double filteredSpan;
                                lock (history)
                                {
                                    history.Enqueue(detectedSpan);
                                    while (history.Count > 7) history.Dequeue();
                                    double[] ordered = history.Order().ToArray();
                                    filteredSpan = ordered[ordered.Length / 2];
                                }
                                filteredSpan = Math.Clamp(
                                    filteredSpan, 600.0,
                                    input.SamplesPerLine - 1.0);
                                deconvolutionControl.SetManualPacketSpanSamples(
                                    physicalLine, filteredSpan);
                                peakFitUpdated = true;
                                continue;
                            }
                        }
                        if (peakFitUpdated)
                            Dispatcher.UIThread.Post(UpdateClockBank);

                        if (!renderPreview) return;
                        int[] clockSearchOffsets = Enumerable.Range(
                                previewFirstLine, previewLineCount)
                            .Select(deconvolutionControl.GetClockSearchOffset)
                            .ToArray();
                        double[] manualPacketSpanSamples = Enumerable.Range(
                                previewFirstLine, previewLineCount)
                            .Select(deconvolutionControl.GetManualPacketSpanSamples)
                            .ToArray();
                        int timingLineMask = previewLineCount >= 32
                            ? -1
                            : (1 << previewLineCount) - 1;
                        byte[] timingFrame = previewFirstLine == 0
                            ? snapshot
                            : snapshot.AsSpan(
                                previewFirstLine * input.SamplesPerLine,
                                previewLineCount * input.SamplesPerLine).ToArray();
                        VbiLineTiming?[] lineTimings =
                            VbiDeconvolutionEngine.FindLineTimings(
                                timingFrame, options, clockSearchOffsets,
                                manualPacketSpanSamples,
                                previewLineCount, timingLineMask);
                        int[] detectedClockStarts = Enumerable.Range(0, previewLineCount)
                            .Select(line => lineTimings[line]?.StartSample ?? -1)
                            .ToArray();
                        int nominalPacketSamples = (int)Math.Round(
                            360.0 * input.SamplingRate / 6_937_500.0);
                        int[] detectedLineEnds = Enumerable.Range(0, previewLineCount)
                            .Select(line => lineTimings[line]?.EndSample ?? -1)
                            .ToArray();
                        int[] manualLineEndSamples = Enumerable.Range(0, previewLineCount)
                            .Select(line =>
                            {
                                double span = manualPacketSpanSamples[line];
                                if (span < 0) return -1;
                                int start = detectedClockStarts[line];
                                if (start < 0)
                                    start = preset.LineStart + clockSearchOffsets[line];
                                return (int)Math.Round(start + span);
                            })
                            .ToArray();
                        bool[] pllAdjustedLines = lineTimings
                            .Select(timing => timing?.PllAdjusted == true)
                            .ToArray();
                        int selectedBankStart = Volatile.Read(ref selectedClockBank)
                            * clockBankSize;
                        int selectedBankLineCount = Math.Min(
                            clockBankSize,
                            Math.Max(previewLineCount - selectedBankStart, 0));
                        byte[] pixels = BuildRawVbiPreview(
                            snapshot, input.SamplesPerLine,
                            previewFirstLine, previewLineCount,
                            rawPreviewSamples, rawPreviewWidth, rawPreviewHeight,
                            preset.LineStart, preset.LineStartEnd,
                            clockSearchOffsets,
                            detectedClockStarts,
                            detectedLineEnds,
                            pllAdjustedLines,
                            manualLineEndSamples,
                            selectedBankStart, selectedBankLineCount,
                            out byte minimumSample, out byte maximumSample);
                        long frameNumber = Interlocked.Increment(ref rawPreviewFrameNumber);
                        Volatile.Write(ref rawPreviewHasFrame, 1);
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!rawPreviewActive) return;
                            try
                            {
                                UpdateBgraBitmap(rawPreviewBitmap, pixels, rawPreviewWidth, rawPreviewHeight);
                                rawPreviewImage.InvalidateVisual();
                                rawPreviewInfoText.Text =
                                    $"Frame {frameNumber:N0} · F{previewField + 1} · levels {minimumSample}–{maximumSample} · CRI {preset.LineStart}–{preset.LineStartEnd}"
                                    + (clockSearchOffsets.All(offset => offset == 0)
                                        ? string.Empty
                                        : " · adjusted")
                                    + (Volatile.Read(ref rawPreviewEnabled) == 0 ? " · paused" : string.Empty);
                            }
                            catch (Exception ex)
                            {
                                rawPreviewInfoText.Text = $"Raw VBI preview render failed: {ex.Message}";
                            }
                        }, DispatcherPriority.Render);
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!rawPreviewActive) return;
                            rawPreviewInfoText.Text = $"Raw VBI preview processing failed: {ex.Message}";
                        });
                    }
                    finally
                    {
                        Interlocked.Exchange(ref rawPreviewWorkerBusy, 0);
                    }
                });
            };
            resetAllClockOffsetsButton.Click += (_, _) =>
            {
                Volatile.Write(ref autoTrackFirstFieldLineMask, 0);
                Volatile.Write(ref autoTrackSecondFieldLineMask, 0);
                Array.Fill(lastDetectedClockStarts, -1);
                for (int line = 0; line < input.LinesPerFrame; line++)
                {
                    deconvolutionControl.SetClockSearchOffset(line, 0);
                    deconvolutionControl.SetManualPacketSpanSamples(line, -1);
                    Volatile.Write(ref autoEndFitEnabled[line], 0);
                    lock (autoEndSpanHistories[line])
                        autoEndSpanHistories[line].Clear();
                    deconvolutionControl.SetLineDecodingEnabled(line, true);
                }
                UpdateClockBank();
                autoSearchStatus.Text =
                    "All line offsets, CRI tracking and automatic endpoints were reset; decoding was enabled for every line.";
            };
            void StopVideoPreviewPipe()
            {
                CancellationTokenSource? pipe = Interlocked.Exchange(
                    ref videoPreviewPipeCancellation, null);
                if (pipe is null) return;
                try { pipe.Cancel(); } catch (ObjectDisposedException) { }
                pipe.Dispose();
            }
            void StartVideoPreviewPipe()
            {
                if (input is WindowsDirectShowVbiCaptureStream)
                {
                    videoPreviewInfoText.Text = "Waiting for the next DirectShow video frame…";
                    return;
                }
                if (videoInterfacePath is null || !videoPreviewActive
                    || videoPreviewCancellation.IsCancellationRequested)
                    return;

                StopVideoPreviewPipe();
                var pipe = CancellationTokenSource.CreateLinkedTokenSource(
                    videoPreviewCancellation.Token);
                CancellationToken pipeToken = pipe.Token;
                Interlocked.Exchange(ref videoPreviewPipeCancellation, pipe);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await LinuxVbiCaptureStream.CaptureVideoFramesAsync(
                            videoInterfacePath,
                            TimeSpan.FromSeconds(5),
                            frame =>
                            {
                                byte[] pixels = BuildVideoPreviewBgra(
                                    frame, videoPreviewWidth, videoPreviewHeight);
                                int frameNumber = Interlocked.Increment(ref videoSnapshotNumber);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    if (!videoPreviewActive) return;
                                    try
                                    {
                                        UpdateBgraBitmap(
                                            videoPreviewBitmap, pixels,
                                            videoPreviewWidth, videoPreviewHeight);
                                        videoPreviewImage.InvalidateVisual();
                                        videoPreviewInfoText.Text =
                                            $"Raw video snapshot {frameNumber:N0} · {frame.Width}×{frame.Height} · {FourCc(frame.PixelFormat)} · {VideoFieldName(frame.Field)}"
                                            + (Volatile.Read(ref videoPreviewEnabled) == 0 ? " · paused" : string.Empty);
                                    }
                                    catch (Exception ex)
                                    {
                                        videoPreviewInfoText.Text = $"Video preview render failed: {ex.Message}";
                                    }
                                }, DispatcherPriority.Render);

                                // When initially disabled, keep the requested first
                                // frozen frame and immediately close /dev/video.
                                return Volatile.Read(ref videoPreviewEnabled) != 0;
                            },
                            pipeToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (pipeToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (videoPreviewActive && !pipeToken.IsCancellationRequested)
                                videoPreviewInfoText.Text = $"Video preview unavailable: {ex.Message}";
                        });
                    }
                    finally
                    {
                        if (ReferenceEquals(Interlocked.CompareExchange(
                                ref videoPreviewPipeCancellation, null, pipe), pipe))
                            pipe.Dispose();
                    }
                }, pipeToken);
            }
            void StopCapture()
            {
                cancellation.Cancel();
                videoPreviewCancellation.Cancel();
                StopVideoPreviewPipe();
                // A V4L2 read can be blocked waiting for the next complete frame;
                // closing the device guarantees that Stop does not wait forever.
                try { input.Dispose(); } catch { }
                stopButton.IsEnabled = false;
                phaseText.Text = "Stopping live capture…";
            }
            dialog.Closing += (_, args) =>
            {
                if (allowClose) return;
                args.Cancel = true;
                StopCapture();
            };
            stopButton.Click += (_, _) => StopCapture();
            showRawPreviewCheckBox.IsCheckedChanged += (_, _) =>
            {
                bool enabled = showRawPreviewCheckBox.IsChecked == true;
                Volatile.Write(ref rawPreviewEnabled, enabled ? 1 : 0);
                if (enabled)
                    rawPreviewInfoText.Text = rawPreviewInfoText.Text?.Replace(" · paused", string.Empty);
                else if (!string.IsNullOrWhiteSpace(rawPreviewInfoText.Text)
                         && !rawPreviewInfoText.Text.EndsWith(" · paused", StringComparison.Ordinal))
                    rawPreviewInfoText.Text += " · paused";
                _sessionState.ShowRawVbiPreview = enabled;
                SaveSessionState();
            };
            showVideoPreviewCheckBox.IsCheckedChanged += (_, _) =>
            {
                bool enabled = showVideoPreviewCheckBox.IsChecked == true;
                Volatile.Write(ref videoPreviewEnabled, enabled ? 1 : 0);
                if (enabled)
                {
                    videoPreviewInfoText.Text = videoPreviewInfoText.Text?.Replace(" · paused", string.Empty);
                    StartVideoPreviewPipe();
                }
                else
                {
                    if (videoPreviewImage.Source is not null
                        && !videoPreviewInfoText.Text.EndsWith(" · paused", StringComparison.Ordinal))
                        videoPreviewInfoText.Text += " · paused";
                    StopVideoPreviewPipe();
                }
                _sessionState.ShowVideoCapturePreview = enabled;
                SaveSessionState();
            };
            runDeconvolutionCheckBox.IsCheckedChanged += (_, _) =>
            {
                // This controls only CPU/OpenCL work. Video preview has its own
                // independent pipe controlled by showVideoPreviewCheckBox.
                deconvolutionControl.Enabled = runDeconvolutionCheckBox.IsChecked == true;
                Interlocked.Exchange(ref fpsBaselineFrames, input.CapturedFrames);
                Interlocked.Exchange(ref fpsBaselineTimestamp, Stopwatch.GetTimestamp());
            };
            dialog.Closed += (_, _) =>
            {
                rawPreviewActive = false;
                videoPreviewActive = false;
                videoPreviewCancellation.Cancel();
                StopVideoPreviewPipe();
                input.RawFrameCaptured = null;
                if (input is WindowsDirectShowVbiCaptureStream directShowCapture)
                {
                    directShowCapture.TryAcquireVideoFrame = null;
                    directShowCapture.ReleaseVideoFrame = null;
                    directShowCapture.VideoFrameCaptured = null;
                }
                rawPreviewBitmap.Dispose();
                videoPreviewImage.Source = null;
                videoPreviewBitmap.Dispose();
            };

            if (!disableVideoPreview
                && (videoInterfacePath is not null || input is WindowsDirectShowVbiCaptureStream))
                StartVideoPreviewPipe();

            PageAssembler? liveAssembler = null;
            int livePacketIndex = 0;
            TeletextPage? lockedLivePage = null;
            string activeLivePageLock = string.Empty;
            bool TryGetLockedLivePage(out int magazine, out int pageNumber)
            {
                magazine = 0;
                pageNumber = 0;
                string text = livePageLockTextBox.Text?.Trim() ?? string.Empty;
                return text.Length == 3
                    && text[0] is >= '1' and <= '8'
                    && int.TryParse(
                        text.AsSpan(1), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out pageNumber)
                    && (magazine = text[0] - '0') is >= 1 and <= 8;
            }
            static TeletextPage CreateWaitingPageDisplay(
                TeletextPage body,
                TeletextPage header,
                int lockedMagazine,
                int lockedPageNumber)
            {
                var display = new TeletextPage
                {
                    Magazine = lockedMagazine,
                    PageNumber = lockedPageNumber,
                    SubPage = body.SubPage,
                    NationalOption = body.NationalOption,
                    NationalOptionOverride = body.NationalOptionOverride,
                    Newsflash = body.Newsflash,
                    Subtitle = body.Subtitle,
                    Suppress = body.Suppress,
                };
                for (int row = 0; row < 25; row++)
                {
                    display.RawRows[row] = body.RawRows[row];
                    display.RawRowPacketIndices[row] =
                        body.RawRowPacketIndices[row];
                    for (int column = 0; column < 40; column++)
                        display.Grid[column, row] = body.Grid[column, row];
                }

                // Columns 0-7 are reserved for receiver-generated information.
                // Keep the broadcaster's live service header (columns 8-39)
                // untouched and place the requested page at receiver position 3.
                for (int column = 8; column < 40; column++)
                    display.Grid[column, 0] = header.Grid[column, 0];
                string requested = $"P{lockedMagazine}{lockedPageNumber:X2}";
                const int requestedPageColumn = 2;
                for (int index = 0; index < requested.Length; index++)
                {
                    Cell cell = display.Grid[requestedPageColumn + index, 0];
                    cell.Character = requested[index];
                    cell.EnhancementText = null;
                    cell.IsMosaic = false;
                    cell.MosaicPattern = 0;
                    cell.MosaicHeld = false;
                    cell.HoldMosaics = false;
                    cell.Conceal = false;
                    display.Grid[requestedPageColumn + index, 0] = cell;
                }
                return display;
            }
            void InitializeLivePreview()
            {
                if (liveAssembler is not null) return;
                _store.Clear();
                _broadcastPackets.Clear();
                ClearBroadcastPane();
                _broadcastFileOpen = true;
                BroadcastPaneGrid.IsVisible = true;
                SquashGrid.IsActive = false;
                BroadcastInfoText.Text = $"Live VBI — {captureInterface.Name}";
                BroadcastFilePathText.Text = $"{captureInterface.Path} — live capture";
                UpdateWorkspacePaneVisibility();
                // Pane layout may activate a broadcast-only grid, so disable it
                // afterwards. The pane is a monitor rather than an editor while
                // capture is running.
                BroadcastGrid.IsActive = false;
                BroadcastGrid.ClearSelection();
                SquashGrid.IsActive = false;
                UpdateG0SubsetMenuChecks();
                FitWindowToContent();
                liveAssembler = new PageAssembler(_store, decodeEnhancements: false);
            }
            var packetReporter = new ToggleablePacketProgress(
                showLiveCheckBox.IsChecked == true,
                packets =>
                {
                    if (liveAssembler is null) InitializeLivePreview();
                    TeletextPage? latestPage = null;
                    TeletextPage? latestHeaderPage = null;
                    string lockText = livePageLockTextBox.Text?.Trim()
                        .ToUpperInvariant() ?? string.Empty;
                    bool pageLocked = lockText.Length > 0;
                    bool validPageLock = TryGetLockedLivePage(
                        out int lockedMagazine,
                        out int lockedPageNumber);
                    if (!string.Equals(
                            activeLivePageLock, lockText,
                            StringComparison.Ordinal))
                    {
                        activeLivePageLock = lockText;
                        lockedLivePage = null;
                    }
                    foreach (byte[] packet in packets)
                    {
                        _broadcastPackets.Add(packet);
                        liveAssembler!.Feed(packet, livePacketIndex++);
                        latestHeaderPage =
                            liveAssembler.LastUpdatedPage ?? latestHeaderPage;
                        TeletextPage? finalized = liveAssembler.LastFinalizedPage;
                        if (finalized is null) continue;
                        if (!pageLocked)
                            latestPage = finalized;
                        else if (validPageLock
                                 && finalized.Magazine == lockedMagazine
                                 && finalized.PageNumber == lockedPageNumber)
                            lockedLivePage = finalized;
                    }
                    if (pageLocked)
                    {
                        if (!validPageLock || latestHeaderPage is null) return;
                        TeletextPage body = lockedLivePage
                            ?? BroadcastGrid.Page
                            ?? new TeletextPage
                            {
                                Magazine = lockedMagazine,
                                PageNumber = lockedPageNumber,
                            };
                        if (lockedLivePage is not null)
                            ApplyFileG0SubsetToPage(lockedLivePage, broadcast: true);
                        BroadcastGrid.Page = CreateWaitingPageDisplay(
                            body, latestHeaderPage,
                            lockedMagazine, lockedPageNumber);
                        BroadcastGrid.InvalidateVisual();
                        return;
                    }
                    if (latestPage is null) return;
                    ApplyFileG0SubsetToPage(latestPage, broadcast: true);
                    BroadcastGrid.Page = latestPage;
                    BroadcastGrid.InvalidateVisual();
                });
            if (packetReporter.Enabled) InitializeLivePreview();
            showLiveCheckBox.IsCheckedChanged += (_, _) =>
            {
                bool enabled = showLiveCheckBox.IsChecked == true;
                if (enabled && !packetReporter.Enabled)
                {
                    liveAssembler = null;
                    livePacketIndex = 0;
                    InitializeLivePreview();
                }
                packetReporter.Enabled = enabled;
                livePageLockTextBox.IsEnabled = enabled;
                _sessionState.ShowLiveDeconvolvedPage = enabled;
                SaveSessionState();
            };

            var elapsed = Stopwatch.StartNew();
            VbiDeconvolutionProgress lastProgress = default;
            var reporter = new Progress<VbiDeconvolutionProgress>(value =>
            {
                lastProgress = value;
                long baselineFrames = Interlocked.Read(ref fpsBaselineFrames);
                long baselineTimestamp = Interlocked.Read(ref fpsBaselineTimestamp);
                double fpsElapsedSeconds = Stopwatch.GetElapsedTime(baselineTimestamp).TotalSeconds;
                double captureFps = fpsElapsedSeconds > 0
                    ? (input.CapturedFrames - baselineFrames) / fpsElapsedSeconds
                    : 0;
                phaseText.Text = deconvolutionControl.Enabled
                    ? $"Live deconvolution — {captureFps:0.0} fps"
                    : $"Capture only — {captureFps:0.0} fps";
                string headerDiagnostics = liveAssembler is null
                    ? "Headers waiting…"
                    : $"Headers {liveAssembler.HeaderPacketsAccepted:N0} ok / {liveAssembler.HeaderPacketsRejected:N0} rejected   " +
                      $"MRAG rejected {liveAssembler.AddressPacketsRejected:N0}   " +
                      $"Orphan rows {liveAssembler.OrphanedBodyRows:N0}   Repeated rows {liveAssembler.RepeatedBodyRows:N0}";
                detailText.Text =
                    $"Frames {value.ProcessedLines / Math.Max(input.LinesPerFrame, 1):N0}   Lines {value.ProcessedLines:N0}   Teletext {value.TeletextLines:N0}   Packets {value.PacketsWritten:N0}\n" +
                    headerDiagnostics;
                timingText.Text = $"Elapsed {FormatVbiDuration(elapsed.Elapsed)}   Device {input.SamplingRate:N0} Hz · {input.SamplesPerLine} samples/line · {input.LinesPerFrame} lines/frame";
            });
            Exception? failure = null;
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var output = new FileStream(
                        temporaryOutput, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (temporaryRawCapture is null)
                    {
                        await VbiDeconvolutionEngine.DeconvolveAsync(
                            input, output, options, reporter, packetReporter,
                            cancellation.Token, deconvolutionControl);
                    }
                    else
                    {
                        await using var rawOutput = new FileStream(
                            temporaryRawCapture, FileMode.CreateNew, FileAccess.Write,
                            FileShare.None, 1024 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var recordingInput = new RecordingReadStream(input, rawOutput);
                        await VbiDeconvolutionEngine.DeconvolveAsync(
                            recordingInput, output, options, reporter, packetReporter,
                            cancellation.Token, deconvolutionControl);
                    }
                }
                catch (Exception ex) { failure = ex; }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    allowClose = true;
                    dialog.Close();
                });
            });
            await dialog.ShowDialog(this);

            // Pages assembled for the live monitor are deliberately temporary.
            // Leave the editor empty after capture; the decoded stream below is
            // the only path that may populate it again.
            packetReporter.Enabled = false;
            liveAssembler = null;
            _store.Clear();
            _broadcastPackets.Clear();
            ClearBroadcastPane();
            BroadcastInfoText.Text = "Full broadcast";
            BroadcastFilePathText.Text = FormatFileFooter(null, 0);
            if (_squashFileOpen)
            {
                SquashGrid.IsActive = true;
                BroadcastGrid.IsActive = false;
            }
            else
            {
                SquashGrid.IsActive = false;
                BroadcastGrid.IsActive = true;
            }
            UpdateWorkspacePaneVisibility();
            UpdateWindowAndPaneTitles();
            FitWindowToContent();

            try
            {
                long packetCount = File.Exists(temporaryOutput)
                    ? new FileInfo(temporaryOutput).Length / 42
                    : lastProgress.PacketsWritten;
                LiveCaptureCompletionChoice completionChoice =
                    await ShowLiveCaptureCompletionDialogAsync(
                        temporaryRawCapture, packetCount);
                if (completionChoice == LiveCaptureCompletionChoice.Discard)
                    return;

                if (failure is not null
                    && failure is not OperationCanceledException
                    && !cancellation.IsCancellationRequested)
                {
                    await ShowMessageAsync("Live VBI capture failed", failure.Message);
                    return;
                }
                if (packetCount <= 0)
                {
                    await ShowMessageAsync("Live VBI capture", "Capture stopped without recovering any Teletext packets.");
                    return;
                }

                // Open first as an untitled full broadcast. Saving afterwards only
                // assigns a path to this already-open document.
                SquashGrid.IsActive = false;
                SquashGrid.ClearSelection();
                BroadcastGrid.IsActive = true;
                await using (var decoded = File.OpenRead(temporaryOutput))
                    await LoadBroadcastStreamAsync(decoded, filePath: null);
                _sessionState.BroadcastFilePath = null;
                UpdateWindowAndPaneTitles();
                SaveSessionState();

                if (await ConfirmSaveLiveDecodedCaptureAsync(packetCount))
                    await SaveOpenedLiveDecodedCaptureAsync(temporaryOutput);
            }
            finally
            {
                try { if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput); } catch { }
                try
                {
                    if (temporaryRawCapture is not null && File.Exists(temporaryRawCapture))
                        File.Delete(temporaryRawCapture);
                }
                catch { }
            }
        }
    }

    private async Task<List<LiveCaptureInterface>> DiscoverLiveCaptureInterfacesAsync()
    {
        if (OperatingSystem.IsMacOS())
            return DiscoverDeviceFiles(["cu.*", "tty.*"], "Serial");
        if (OperatingSystem.IsLinux())
            return DiscoverDeviceFiles(["vbi*"], "Video4Linux VBI");
        if (!OperatingSystem.IsWindows())
            return new List<LiveCaptureInterface>();

        try
        {
            IReadOnlyList<string> names = await Task.Run(WindowsDirectShowCapture.DiscoverDeviceNames);
            return names.Select(name => new LiveCaptureInterface(name, name, "DirectShow")).ToList();
        }
        catch
        {
            return new List<LiveCaptureInterface>();
        }
    }

    private static string? FindRelatedLinuxVideoInterface(string busInfo)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(busInfo)) return null;
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles("/dev", "video*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return null;
        }

        foreach (string path in paths)
        {
            try
            {
                LinuxV4l2DeviceIdentity identity = LinuxVbiCaptureStream.QueryIdentity(path);
                if ((identity.DeviceCapabilities & 0x00000001) != 0
                    && string.Equals(identity.BusInfo, busInfo, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            catch
            {
                // An unrelated or inaccessible video node must not hide the VBI device.
            }
        }
        return null;
    }

    private static async Task ReadMjpegFramesAsync(
        Stream input,
        CancellationToken cancellationToken,
        Action<byte[]> reportFrame)
    {
        var readBuffer = new byte[32 * 1024];
        MemoryStream? frame = null;
        bool previousWasFf = false;
        try
        {
            while (true)
            {
                int count = await input.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                for (int index = 0; index < count; index++)
                {
                    byte value = readBuffer[index];
                    if (frame is null)
                    {
                        if (previousWasFf && value == 0xD8)
                        {
                            frame = new MemoryStream(256 * 1024);
                            frame.WriteByte(0xFF);
                            frame.WriteByte(0xD8);
                            previousWasFf = false;
                        }
                        else
                        {
                            previousWasFf = value == 0xFF;
                        }
                        continue;
                    }

                    frame.WriteByte(value);
                    if (previousWasFf && value == 0xD9)
                    {
                        reportFrame(frame.ToArray());
                        frame.Dispose();
                        frame = null;
                        previousWasFf = false;
                    }
                    else
                    {
                        previousWasFf = value == 0xFF;
                        if (frame.Length > 8 * 1024 * 1024)
                        {
                            frame.Dispose();
                            frame = null;
                            previousWasFf = false;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // FFmpeg closed its image pipe or the capture device stopped producing frames.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private static byte[] BuildVideoPreviewBgra(
        LinuxV4l2VideoFrame frame,
        int outputWidth,
        int outputHeight)
    {
        const uint Yu12 = 0x32315559;
        const uint Yv12 = 0x32315659;
        const uint Yuyv = 0x56595559;
        const uint Uyvy = 0x59565955;
        const uint Grey = 0x59455247;
        var output = new byte[checked(outputWidth * outputHeight * 4)];
        int yStride = frame.BytesPerLine > 0
            ? frame.BytesPerLine
            : frame.PixelFormat is Yuyv or Uyvy
                ? checked(frame.Width * 2)
                : frame.Width;
        int chromaStride = Math.Max(yStride / 2, 1);
        int yPlaneSize = checked(yStride * frame.Height);
        int chromaPlaneSize = checked(chromaStride * ((frame.Height + 1) / 2));

        for (int outputY = 0; outputY < outputHeight; outputY++)
        for (int outputX = 0; outputX < outputWidth; outputX++)
        {
            int sourceX = Math.Min(outputX * frame.Width / outputWidth, frame.Width - 1);
            int sourceY = Math.Min(outputY * frame.Height / outputHeight, frame.Height - 1);
            bool interlaced = frame.Field is 4 or 8 or 9;
            if (interlaced)
            {
                // Bob-deinterlace from the first field. The preview is meant for
                // identifying the source, and displaying both temporally distinct
                // fields directly creates combing on VHS motion.
                sourceY &= ~1;
            }
            int y, u = 128, v = 128;
            switch (frame.PixelFormat)
            {
                case Yu12:
                case Yv12:
                    y = frame.Data[sourceY * yStride + sourceX];
                    int chromaRow = interlaced
                        ? (sourceY / 4) * 2 + sourceY % 2
                        : sourceY / 2;
                    int chromaIndex = chromaRow * chromaStride + sourceX / 2;
                    int firstChromaOffset = yPlaneSize;
                    int secondChromaOffset = yPlaneSize + chromaPlaneSize;
                    u = frame.Data[(frame.PixelFormat == Yu12 ? firstChromaOffset : secondChromaOffset) + chromaIndex];
                    v = frame.Data[(frame.PixelFormat == Yu12 ? secondChromaOffset : firstChromaOffset) + chromaIndex];
                    break;
                case Yuyv:
                {
                    int pair = sourceY * yStride + (sourceX & ~1) * 2;
                    y = frame.Data[pair + (sourceX & 1) * 2];
                    u = frame.Data[pair + 1];
                    v = frame.Data[pair + 3];
                    break;
                }
                case Uyvy:
                {
                    int pair = sourceY * yStride + (sourceX & ~1) * 2;
                    u = frame.Data[pair];
                    y = frame.Data[pair + 1 + (sourceX & 1) * 2];
                    v = frame.Data[pair + 2];
                    break;
                }
                case Grey:
                    y = frame.Data[sourceY * yStride + sourceX];
                    break;
                default:
                    throw new NotSupportedException(
                        $"Video preview does not support pixel format {FourCc(frame.PixelFormat)}.");
            }

            int c = Math.Max(y - 16, 0);
            int d = u - 128;
            int e = v - 128;
            byte red = (byte)Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
            byte green = (byte)Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
            byte blue = (byte)Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);
            int target = (outputY * outputWidth + outputX) * 4;
            output[target] = blue;
            output[target + 1] = green;
            output[target + 2] = red;
            output[target + 3] = 255;
        }
        return output;
    }

    private static byte[] ScaleBgraFrame(
        byte[] source, int sourceWidth, int sourceHeight, int sourceStride,
        int targetWidth, int targetHeight)
    {
        var target = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
        {
            int sourceY = Math.Min(sourceHeight - 1, y * sourceHeight / targetHeight);
            for (int x = 0; x < targetWidth; x++)
            {
                int sourceX = Math.Min(sourceWidth - 1, x * sourceWidth / targetWidth);
                int from = sourceY * sourceStride + sourceX * 4;
                int to = (y * targetWidth + x) * 4;
                target[to] = source[from];
                target[to + 1] = source[from + 1];
                target[to + 2] = source[from + 2];
                target[to + 3] = 255;
            }
        }
        return target;
    }

    private static string FourCc(uint value) => new string(new[]
    {
        (char)(value & 0xFF),
        (char)((value >> 8) & 0xFF),
        (char)((value >> 16) & 0xFF),
        (char)((value >> 24) & 0xFF),
    });

    private static string VideoFieldName(uint field) => field switch
    {
        1 => "progressive",
        2 => "top field",
        3 => "bottom field",
        4 => "interlaced",
        5 => "sequential top/bottom",
        6 => "sequential bottom/top",
        7 => "alternating fields",
        8 => "interlaced top-first",
        9 => "interlaced bottom-first",
        _ => $"field {field}",
    };

    private static bool TryFindRawVbiBurstEnd(
        byte[] rawFrame,
        int samplesPerLine,
        int physicalLine,
        int detectedStart,
        double nominalSpan,
        double minimumSignalRange,
        out double endSample)
    {
        endSample = -1;
        if (samplesPerLine <= 0 || physicalLine < 0
            || rawFrame.Length < (physicalLine + 1) * samplesPerLine)
            return false;

        int lineOffset = physicalLine * samplesPerLine;
        int activeFrom = Math.Clamp(detectedStart + 24, 1, samplesPerLine - 2);
        int activeTo = Math.Clamp(
            (int)Math.Round(detectedStart + nominalSpan * 0.55),
            activeFrom + 1, samplesPerLine - 1);
        byte activeMinimum = 255;
        byte activeMaximum = 0;
        for (int sample = activeFrom; sample < activeTo; sample++)
        {
            byte value = rawFrame[lineOffset + sample];
            activeMinimum = Math.Min(activeMinimum, value);
            activeMaximum = Math.Max(activeMaximum, value);
        }
        double activeRange = activeMaximum - activeMinimum;
        if (activeRange < minimumSignalRange) return false;

        // DirectShow/WDM drivers can leave stale DMA or PCI padding after the
        // useful waveform. Find the first sustained quiet interval and ignore
        // everything after it, rather than selecting the strongest later edge.
        int quietRunLength = Math.Clamp(
            (int)Math.Round(nominalSpan / 35.0), 32, 48);
        int searchFrom = Math.Max(
            2,
            // Do not confuse a long constant run inside packet data with the
            // real transition into the flat post-packet portion of the line.
            (int)Math.Round(detectedStart + nominalSpan * 0.78));
        int searchTo = Math.Min(
            samplesPerLine - 2,
            (int)Math.Round(detectedStart + nominalSpan * 1.08));
        if (searchTo <= searchFrom) return false;

        double quietTransitionThreshold = Math.Max(1.5, activeRange / 80.0);
        int quietRun = 0;
        int quietStart = -1;
        for (int sample = searchFrom; sample <= searchTo; sample++)
        {
            int transition = Math.Abs(
                rawFrame[lineOffset + sample]
                - rawFrame[lineOffset + sample - 1]);
            if (transition <= quietTransitionThreshold)
            {
                quietRun++;
                if (quietRun < quietRunLength) continue;
                quietStart = sample - quietRunLength + 1;
                break;
            }
            quietRun = 0;
        }
        if (quietStart < 0)
        {
            // The selected line window can end while the packet is still active
            // (notably a 1440-sample DirectShow profile). In that case the only
            // honest endpoint available is the final captured sample.
            int tailFrom = Math.Max(1, samplesPerLine - quietRunLength);
            double tailTransitionEnergy = 0;
            for (int sample = tailFrom; sample < samplesPerLine; sample++)
                tailTransitionEnergy += Math.Abs(
                    rawFrame[lineOffset + sample]
                    - rawFrame[lineOffset + sample - 1]);
            tailTransitionEnergy /= Math.Max(samplesPerLine - tailFrom, 1);
            if (tailTransitionEnergy > quietTransitionThreshold)
            {
                endSample = samplesPerLine - 1;
                return true;
            }
            return false;
        }

        double transitionThreshold = Math.Max(2.0, activeRange * 0.08);
        int refineWidth = Math.Clamp(quietRunLength / 2, 12, 24);
        int refineFrom = Math.Max(searchFrom, quietStart - refineWidth);
        int refineTo = quietStart;
        int lastTransition = -1;
        for (int sample = refineFrom; sample <= refineTo; sample++)
        {
            double contrast = Math.Abs(
                rawFrame[lineOffset + sample + 1]
                - rawFrame[lineOffset + sample - 1]);
            if (contrast >= transitionThreshold) lastTransition = sample;
        }
        endSample = lastTransition >= 0 ? lastTransition + 0.5 : quietStart;
        return true;
    }

    private static byte[] BuildRawVbiPreview(
        byte[] rawFrame,
        int samplesPerLine,
        int firstLine,
        int lineCount,
        int displayedSamples,
        int outputWidth,
        int outputHeight,
        int clockSearchStart,
        int clockSearchEnd,
        IReadOnlyList<int> clockSearchOffsets,
        IReadOnlyList<int> detectedClockStarts,
        IReadOnlyList<int> detectedLineEnds,
        IReadOnlyList<bool> pllAdjustedLines,
        IReadOnlyList<int> manualLineEndSamples,
        int selectedLineStart,
        int selectedLineCount,
        out byte minimumSample,
        out byte maximumSample)
    {
        var pixels = new byte[checked(outputWidth * outputHeight * 4)];
        minimumSample = 0;
        maximumSample = 0;
        if (samplesPerLine <= 0 || firstLine < 0 || lineCount <= 0
            || displayedSamples <= 0
            || rawFrame.Length < samplesPerLine * (firstLine + lineCount))
            return pixels;

        displayedSamples = Math.Min(displayedSamples, samplesPerLine);

        minimumSample = 255;
        maximumSample = 0;
        for (int line = 0; line < lineCount; line++)
        for (int sample = 0; sample < displayedSamples; sample++)
        {
            byte value = rawFrame[(firstLine + line) * samplesPerLine + sample];
            minimumSample = Math.Min(minimumSample, value);
            maximumSample = Math.Max(maximumSample, value);
        }
        int sampleRange = Math.Max(maximumSample - minimumSample, 1);

        for (int outputY = 0; outputY < outputHeight; outputY++)
        {
            int sourceLine = Math.Min(outputY * lineCount / outputHeight, lineCount - 1);
            int sourceLineOffset = (firstLine + sourceLine) * samplesPerLine;
            int outputOffset = outputY * outputWidth * 4;
            for (int outputX = 0; outputX < outputWidth; outputX++)
            {
                int pixelOffset = outputOffset + outputX * 4;
                int start = outputX * displayedSamples / outputWidth;
                int end = Math.Max((outputX + 1) * displayedSamples / outputWidth, start + 1);
                int sum = 0;
                for (int sample = start; sample < end; sample++)
                    sum += rawFrame[sourceLineOffset + sample];
                int sampleValue = sum / (end - start);
                byte displayed = (byte)((sampleValue - minimumSample) * 255 / sampleRange);
                pixels[pixelOffset] = displayed;
                pixels[pixelOffset + 1] = displayed;
                pixels[pixelOffset + 2] = displayed;
                pixels[pixelOffset + 3] = 255;
            }
        }

        // Mark the preset's raw-sample window used to locate the clock run-in.
        DrawSearchMarker(clockSearchStart, 0x00, 0xA5, 0xFF); // orange (BGRA)
        DrawSearchMarker(clockSearchEnd, 0xFF, 0xD7, 0x00);   // cyan (BGRA)
        DrawDetectedMarkers();
        DrawLineEndMarkers();
        DrawManualEndMarkers();
        DrawSelectedBankOutline();
        return pixels;

        void DrawDetectedMarkers()
        {
            int markerWidth = Math.Max(1, (int)Math.Round(outputWidth / 500.0 * 2));
            for (int y = 0; y < outputHeight; y++)
            {
                int sourceLine = Math.Min(y * lineCount / outputHeight, lineCount - 1);
                if (sourceLine >= detectedClockStarts.Count) continue;
                int sample = detectedClockStarts[sourceLine];
                if (sample < 0 || sample >= displayedSamples) continue;
                int x = (int)Math.Round(sample * (outputWidth - 1.0) / displayedSamples);
                for (int dx = 0; dx < markerWidth && x + dx < outputWidth; dx++)
                {
                    int offset = (y * outputWidth + x + dx) * 4;
                    pixels[offset] = 0x40;
                    pixels[offset + 1] = 0xFF;
                    pixels[offset + 2] = 0x40;
                    pixels[offset + 3] = 0xFF;
                }
            }
        }

        void DrawLineEndMarkers()
        {
            int markerWidth = Math.Max(1, (int)Math.Round(outputWidth / 500.0 * 2));
            for (int y = 0; y < outputHeight; y++)
            {
                int sourceLine = Math.Min(y * lineCount / outputHeight, lineCount - 1);
                if (sourceLine >= detectedLineEnds.Count) continue;
                int sample = detectedLineEnds[sourceLine];
                if (sample < 0 || sample >= displayedSamples) continue;
                bool adjusted = sourceLine < pllAdjustedLines.Count
                    && pllAdjustedLines[sourceLine];
                int x = (int)Math.Round(sample * (outputWidth - 1.0) / displayedSamples);
                for (int dx = 0; dx < markerWidth && x + dx < outputWidth; dx++)
                {
                    int offset = (y * outputWidth + x + dx) * 4;
                    pixels[offset] = adjusted ? (byte)0xFF : (byte)0x20;
                    pixels[offset + 1] = adjusted ? (byte)0x40 : (byte)0xE0;
                    pixels[offset + 2] = 0xFF;
                    pixels[offset + 3] = 0xFF;
                }
            }
        }

        void DrawManualEndMarkers()
        {
            int markerWidth = Math.Max(1, (int)Math.Round(outputWidth / 500.0 * 2));
            for (int y = 0; y < outputHeight; y++)
            {
                int sourceLine = Math.Min(y * lineCount / outputHeight, lineCount - 1);
                if (sourceLine >= manualLineEndSamples.Count) continue;
                int sample = manualLineEndSamples[sourceLine];
                if (sample < 0 || sample >= displayedSamples) continue;
                int x = (int)Math.Round(sample * (outputWidth - 1.0) / displayedSamples);
                for (int dx = 0; dx < markerWidth && x + dx < outputWidth; dx++)
                {
                    int offset = (y * outputWidth + x + dx) * 4;
                    pixels[offset] = 0x20;
                    pixels[offset + 1] = 0x20;
                    pixels[offset + 2] = 0xFF;
                    pixels[offset + 3] = 0xFF;
                }
            }
        }

        void DrawSelectedBankOutline()
        {
            if (selectedLineCount <= 0 || selectedLineStart < 0
                || selectedLineStart >= lineCount)
                return;

            int endLine = Math.Min(selectedLineStart + selectedLineCount, lineCount);
            int top = selectedLineStart * outputHeight / lineCount;
            int bottom = Math.Max(top, endLine * outputHeight / lineCount - 1);
            const int thickness = 2;
            for (int y = top; y <= bottom; y++)
            for (int x = 0; x < outputWidth; x++)
            {
                bool border = y < top + thickness || y > bottom - thickness
                    || x < thickness || x >= outputWidth - thickness;
                if (!border) continue;
                int offset = (y * outputWidth + x) * 4;
                pixels[offset] = 0xFF;     // blue
                pixels[offset + 1] = 0x66; // green
                pixels[offset + 2] = 0xCC; // red
                pixels[offset + 3] = 0xFF;
            }
        }

        void DrawSearchMarker(int baseSample, byte blue, byte green, byte red)
        {
            // The bitmap is scaled down to the 500 px preview. Keep each marker
            // two visible pixels wide after that scaling.
            int markerWidth = Math.Max(
                1, (int)Math.Round(outputWidth / 500.0 * 2));
            for (int y = 0; y < outputHeight; y++)
            {
            int sourceLine = Math.Min(y * lineCount / outputHeight, lineCount - 1);
            int lineCorrection = sourceLine < clockSearchOffsets.Count
                ? clockSearchOffsets[sourceLine]
                : 0;
            int sample = baseSample + lineCorrection;
            if (sample < 0 || sample >= displayedSamples) continue;
            int x = (int)Math.Round(sample * (outputWidth - 1.0) / displayedSamples);
            for (int dx = 0; dx < markerWidth && x + dx < outputWidth; dx++)
            {
                int offset = (y * outputWidth + x + dx) * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = 255;
            }
            }
        }
    }

    private static void UpdateBgraBitmap(
        WriteableBitmap bitmap,
        byte[] pixels,
        int width,
        int height)
    {
        using ILockedFramebuffer framebuffer = bitmap.Lock();
        int rowBytes = width * 4;
        for (int row = 0; row < height; row++)
        {
            Marshal.Copy(
                pixels,
                row * rowBytes,
                IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                rowBytes);
        }
    }

    private static List<LiveCaptureInterface> DiscoverDeviceFiles(
        IReadOnlyList<string> patterns,
        string kind)
    {
        var result = new List<LiveCaptureInterface>();
        foreach (string pattern in patterns)
        {
            try
            {
                result.AddRange(Directory.EnumerateFiles("/dev", pattern)
                    .Select(path => new LiveCaptureInterface(Path.GetFileName(path), path, kind)));
            }
            catch
            {
                // Missing permissions or a disappearing hot-plug interface should
                // not prevent the dialog from listing the remaining devices.
            }
        }
        return result
            .GroupBy(item => item.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async void OnOpenVbiCaptureClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open raw VBI capture",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Raw VBI captures") { Patterns = new[] { "*.vbi", "*.tbc" } },
                FilePickerFileTypes.All,
            },
        });
        IStorageFile? file = files.Count > 0 ? files[0] : null;
        if (file is null) return;
        if (!file.Path.IsFile)
        {
            await ShowMessageAsync("Open VBI Capture", "VBI deconvolution currently requires a local file.");
            return;
        }

        CaptureCardPreset? preset = await ShowVbiPresetSelectionAsync();
        if (preset is null) return;
        bool showLivePreview = _sessionState.ShowLiveDeconvolvedPage ?? true;
        _sessionState.LastCaptureCardPresetName = preset.Name;
        SaveSessionState();

        string inputPath = file.Path.LocalPath;
        string temporaryOutput = Path.Combine(Path.GetTempPath(), $"TeletextRecoveReese-{Guid.NewGuid():N}.t42");
        using var cancellation = new CancellationTokenSource();
        var phaseText = new TextBlock { Text = "Preparing OpenCL deconvolution…", TextWrapping = TextWrapping.Wrap };
        var detailText = new TextBlock { Foreground = Brushes.LightGray };
        var timingText = new TextBlock { Text = "Elapsed 00:00:00   Expected: calculating…", Foreground = Brushes.LightGray };
        var showLiveCheckBox = new CheckBox
        {
            Content = "Show deconvolved page",
            IsChecked = showLivePreview,
        };
        var progressBar = new ProgressBar { Width = 480, Minimum = 0, Maximum = 100 };
        var abortButton = new Button { Content = "Abort", Width = 90 };
        Grid.SetColumn(abortButton, 1);
        var progressDialog = new Window
        {
            Title = "Deconvolving VBI capture",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Width = 500,
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
                {
                    phaseText,
                    progressBar,
                    detailText,
                    timingText,
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        Children =
                        {
                            showLiveCheckBox,
                            abortButton,
                        },
                    },
                },
            },
        };
        bool allowClose = false;
        progressDialog.Closing += (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            cancellation.Cancel();
            abortButton.IsEnabled = false;
            phaseText.Text = "Aborting…";
        };
        abortButton.Click += (_, _) =>
        {
            cancellation.Cancel();
            abortButton.IsEnabled = false;
            phaseText.Text = "Aborting…";
        };

        VbiDeconvolutionResult? result = null;
        Exception? failure = null;
        var elapsedTimer = Stopwatch.StartNew();
        PageAssembler? liveAssembler = null;
        int livePacketIndex = 0;
        void InitializeLivePreview()
        {
            if (liveAssembler is not null) return;
            _store.Clear();
            _broadcastPackets.Clear();
            ClearBroadcastPane();
            _broadcastFileOpen = true;
            BroadcastPaneGrid.IsVisible = true;
            BroadcastGrid.IsActive = true;
            SquashGrid.IsActive = false;
            SquashGrid.ClearSelection();
            BroadcastInfoText.Text = $"Full broadcast — {Path.GetFileName(inputPath)}";
            BroadcastFilePathText.Text = $"{Path.GetFileName(inputPath)} — live deconvolution";
            UpdateWorkspacePaneVisibility();
            UpdateG0SubsetMenuChecks();
            FitWindowToContent();
            liveAssembler = new PageAssembler(_store, decodeEnhancements: false);
        }
        var packetReporter = new ToggleablePacketProgress(showLivePreview, packets =>
        {
            if (liveAssembler is null) InitializeLivePreview();
            TeletextPage? latestPage = null;
            foreach (byte[] packet in packets)
            {
                _broadcastPackets.Add(packet);
                liveAssembler!.Feed(packet, livePacketIndex++);
                latestPage = liveAssembler.LastFinalizedPage ?? latestPage;
            }
            if (latestPage is not null)
            {
                ApplyFileG0SubsetToPage(latestPage, broadcast: true);
                BroadcastGrid.Page = latestPage;
                BroadcastGrid.InvalidateVisual();
                BroadcastFilePathText.Text =
                    $"{Path.GetFileName(inputPath)} — live — {latestPage.Magazine}{latestPage.PageNumber:X2}-{latestPage.SubPage:X4} — {_broadcastPackets.Count:N0} packets";
            }
        });
        if (showLivePreview) InitializeLivePreview();
        showLiveCheckBox.IsCheckedChanged += (_, _) =>
        {
            bool enabled = showLiveCheckBox.IsChecked == true;
            if (enabled && !packetReporter.Enabled)
            {
                // Packets are deliberately dropped while preview is disabled. Start
                // a fresh live assembler so later body rows cannot attach to a page
                // whose intervening header was skipped.
                liveAssembler = null;
                livePacketIndex = 0;
                InitializeLivePreview();
            }
            packetReporter.Enabled = enabled;
            _sessionState.ShowLiveDeconvolvedPage = enabled;
            SaveSessionState();
        };
        VbiDeconvolutionProgress lastProgress = default;
        var reporter = new Progress<VbiDeconvolutionProgress>(value =>
        {
            lastProgress = value;
            progressBar.Value = value.Percent;
            phaseText.Text = $"Deconvolving with OpenCL — {value.Percent:0.0}%";
            detailText.Text = $"Lines {value.ProcessedLines:N0}/{value.TotalLines:N0}   Teletext {value.TeletextLines:N0}   Packets {value.PacketsWritten:N0}";
            TimeSpan elapsed = elapsedTimer.Elapsed;
            if (value.ProcessedLines > 0 && value.TotalLines > 0)
            {
                double totalSeconds = elapsed.TotalSeconds * value.TotalLines / value.ProcessedLines;
                TimeSpan expectedTotal = TimeSpan.FromSeconds(Math.Max(totalSeconds, 0));
                TimeSpan remaining = expectedTotal > elapsed ? expectedTotal - elapsed : TimeSpan.Zero;
                timingText.Text = $"Elapsed {FormatVbiDuration(elapsed)}   Expected {FormatVbiDuration(expectedTotal)}   Remaining {FormatVbiDuration(remaining)}";
            }
            else
            {
                timingText.Text = $"Elapsed {FormatVbiDuration(elapsed)}   Expected: calculating…";
            }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(temporaryOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var options = new VbiCaptureOptions(
                    preset.Name, preset.SampleRate, preset.LineLength, preset.LineStart,
                    preset.LineStartEnd, preset.SampleType == "UInt16", preset.FieldLines,
                    preset.FieldRangeStart, preset.FieldRangeEnd,
                    preset.StandardDeviationThreshold,
                    preset.SignalLevelThreshold, preset.CriFcRangeThreshold,
                    preset.CriFcConfidenceThreshold);
                result = await VbiDeconvolutionEngine.DeconvolveAsync(
                    input, output, options, reporter, packetReporter, cancellation.Token);
            }
            catch (Exception ex) { failure = ex; }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                allowClose = true;
                progressDialog.Close();
            });
        });
        await progressDialog.ShowDialog(this);
        bool openingPartialCapture = false;

        void DiscardPartialPreview()
        {
            if (liveAssembler is null) return;
            _store.Clear();
            _broadcastPackets.Clear();
            ClearBroadcastPane();
            BroadcastInfoText.Text = "Full broadcast";
            BroadcastFilePathText.Text = FormatFileFooter(null, 0);
            if (_squashFileOpen)
            {
                SquashGrid.IsActive = true;
                BroadcastGrid.IsActive = false;
            }
            UpdateWorkspacePaneVisibility();
            UpdateWindowAndPaneTitles();
            FitWindowToContent();
        }

        try
        {
            if (failure is OperationCanceledException || cancellation.IsCancellationRequested)
            {
                packetReporter.Enabled = false;
                long partialPacketCount = File.Exists(temporaryOutput)
                    ? new FileInfo(temporaryOutput).Length / 42
                    : lastProgress.PacketsWritten;
                if (partialPacketCount <= 0)
                {
                    DiscardPartialPreview();
                    await ShowMessageAsync("VBI deconvolution", "Deconvolution was aborted before any Teletext packets were recovered.");
                    return;
                }

                bool openPartial = await ConfirmOpenPartialVbiAsync(partialPacketCount);
                if (!openPartial)
                {
                    DiscardPartialPreview();
                    return;
                }

                result = new VbiDeconvolutionResult(
                    lastProgress.ProcessedLines,
                    lastProgress.TeletextLines,
                    partialPacketCount,
                    "OpenCL — partial capture (aborted)");
                openingPartialCapture = true;
                failure = null;
            }
            if (failure is not null)
            {
                await ShowMessageAsync("VBI deconvolution failed", failure.Message);
                return;
            }
            if (result is null || result.PacketsWritten == 0)
            {
                await ShowMessageAsync("VBI deconvolution", "No Teletext packets were recovered. Check the capture-card preset and input format.");
                return;
            }

            IStorageFile? savedOutputFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save deconvolved T42 capture (Cancel to open without saving)",
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(inputPath)}.t42",
                DefaultExtension = "t42",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Raw Teletext packet stream") { Patterns = new[] { "*.t42" } },
                },
            });
            string? savedOutputPath = savedOutputFile?.Path.IsFile == true
                ? savedOutputFile.Path.LocalPath
                : null;
            if (savedOutputFile is not null && savedOutputPath is null)
            {
                await ShowMessageAsync("Save deconvolved capture", "The selected destination is not a local file.");
                return;
            }
            if (savedOutputPath is not null
                && string.Equals(Path.GetFullPath(savedOutputPath), Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase))
            {
                await ShowMessageAsync("Save deconvolved capture", "The output file cannot be the same as the source VBI capture.");
                return;
            }

            if (savedOutputPath is not null)
            {
                try
                {
                    File.Copy(temporaryOutput, savedOutputPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync("Could not save deconvolved capture", ex.Message);
                    return;
                }
            }

            CaptureRecentFilePositions();
            string decodedPath = savedOutputPath ?? temporaryOutput;
            await using var decoded = File.OpenRead(decodedPath);
            await LoadBroadcastStreamAsync(decoded, savedOutputPath ?? inputPath);
            if (savedOutputPath is not null)
            {
                await RememberFileAsync(savedOutputPath, broadcast: true);
            }
            else
            {
                // A raw VBI source cannot be restored later by the ordinary T42
                // session loader. Keep its visible source name only for this run.
                _broadcastFilePath = null;
                _sessionState.BroadcastFilePath = null;
                UpdateWindowAndPaneTitles();
                SaveSessionState();
            }
            await ShowMessageAsync(
                openingPartialCapture ? "Partial VBI capture opened" : "VBI deconvolution complete",
                $"Recovered {result.PacketsWritten:N0} packets from {result.TeletextLines:N0} detected Teletext lines.\nOpenCL device: {result.OpenClDevice}");
        }
        finally
        {
            try { if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput); } catch { }
        }
    }

    private static string FormatVbiDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays}.{value:hh\\:mm\\:ss}";
        return value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private async Task<CaptureCardPreset?> ShowVbiPresetSelectionAsync()
    {
        _sessionState.CustomCaptureCardPresets ??= new List<CaptureCardPreset>();
        List<CaptureCardPreset> presets = BuiltInCaptureCardPresets.Concat(_sessionState.CustomCaptureCardPresets).ToList();
        var combo = new ComboBox { Width = 420, ItemsSource = presets };
        combo.SelectedItem = presets.FirstOrDefault(p => string.Equals(p.Name, _sessionState.LastCaptureCardPresetName, StringComparison.OrdinalIgnoreCase))
                             ?? presets.FirstOrDefault();
        var details = new TextBlock { Width = 420, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap };
        void UpdateDetails()
        {
            if (combo.SelectedItem is not CaptureCardPreset p) return;
            details.Text =
                $"{p.SampleRate:N0} Hz · {p.LineLength} samples · {p.SampleType}\n" +
                $"Line start {p.LineStart}–{p.LineStartEnd} · field {p.FieldRangeStart}–{p.FieldRangeEnd} of {p.FieldLines}\n" +
                $"Thresholds: std-dev {p.StandardDeviationThreshold:0.##} · signal {p.SignalLevelThreshold:0.##} · CRI/FC range {p.CriFcRangeThreshold:0.##} · confidence {p.CriFcConfidenceThreshold:0.##}";
        }
        combo.SelectionChanged += (_, _) => UpdateDetails();
        var openButton = new Button { Content = "Deconvolve", Width = 105, IsDefault = true };
        var cancelButton = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var dialog = new Window
        {
            Title = "Open VBI capture",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Width = 440,
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Capture card configuration", FontSize = 16, FontWeight = FontWeight.SemiBold },
                    combo,
                    details,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, openButton },
                    },
                },
            },
        };
        CaptureCardPreset? result = null;
        openButton.Click += (_, _) =>
        {
            if (combo.SelectedItem is CaptureCardPreset selected)
                result = selected;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();
        UpdateDetails();
        await dialog.ShowDialog(this);
        return result;
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
            UpdateG0SubsetMenuChecks();
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
                UpdateG0SubsetMenuChecks();
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
            UpdateG0SubsetMenuChecks();
            FitWindowToContent();
        }
        finally
        {
            EndLoading(broadcast: true);
        }
    }

    private void ClearBroadcastPane()
    {
        StopFlashRoll();
        _broadcastReadOnlyExplanationShown = false;
        _broadcastFileG0Subset = null;
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
        UpdateBroadcastVersionButtons();
        UpdateNavigationButtons();
    }

    private void ClearSquashPane()
    {
        _squashFileG0Subset = null;
        _suppressComboEvents = true;
        SquashMagazineComboBox.Items.Clear();
        SquashPageNumberComboBox.Items.Clear();
        SquashSubpageComboBox.Items.Clear();
        _squashPage = new TeletextPage();
        SquashGrid.Page = null;
        SquashGrid.ClearSelection();
        EnhancementItemsControl.Items.Clear();
        EnhancementInfoText.Text = "X/26 enhancements (0)";
        EnhancementClipboardButton.IsEnabled = false;
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
        UpdateSaveCapturedStreamMenuVisibility();
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

    private bool HasUnsavedCapturedStream() =>
        _broadcastFileOpen
        && string.IsNullOrWhiteSpace(_broadcastFilePath)
        && _broadcastPackets.Count > 0;

    private void UpdateSaveCapturedStreamMenuVisibility()
    {
        bool visible = HasUnsavedCapturedStream();
        SaveCapturedStreamMenuItem.IsVisible = visible;
        if (_nativeSaveCapturedStreamMenuItem is not null)
            _nativeSaveCapturedStreamMenuItem.IsVisible = visible;
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

    private void RestorePage(TeletextPage page, PageSnapshot snapshot)
    {
        SyncEnhancementPacketDeletions(page, snapshot);

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

    private void SyncEnhancementPacketDeletions(TeletextPage page, PageSnapshot snapshot)
    {
        if (!_pageHistories.TryGetValue(page, out var history)) return;

        var knownPacketIndices = history.States
            .SelectMany(state => state.EnhancementPackets)
            .Select(packet => packet.PacketIndex)
            .Where(index => index >= 0)
            .ToHashSet();
        var restoredPacketIndices = snapshot.EnhancementPackets
            .Select(packet => packet.PacketIndex)
            .Where(index => index >= 0)
            .ToHashSet();

        foreach (int packetIndex in knownPacketIndices)
        {
            if (restoredPacketIndices.Contains(packetIndex))
                _deletedSquashPacketIndices.Remove(packetIndex);
            else
                _deletedSquashPacketIndices.Add(packetIndex);
        }
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

        SetToolbarOnBottom(_sessionState.ToolbarOnBottom ?? false, saveSession: false);
        SetDisableLiveVbiVideoPreview(
            _sessionState.DisableLiveVbiVideoPreview ?? false,
            saveSession: false);
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
        if (BroadcastPaneGrid.IsVisible && !SquashPaneGrid.IsVisible)
        {
            _ = WarnBroadcastReadOnlyAsync();
            return;
        }

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
        _sessionState.ToolbarOnBottom = ToolbarOnBottomMenuItem.IsChecked;

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

    private void SelectBroadcastAddress(
        (int magazine, int page, int subpage) address,
        int versionIndex,
        bool persistRecentPosition = true)
    {
        StopFlashRoll();
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
        UpdateBroadcastVersionButtons();
        UpdateNavigationButtons();
        UpdateVideoBookmarkUi();
        if (persistRecentPosition)
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
        ApplyFileG0SubsetToPage(_squashPage, broadcast: false);
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
            EnhancementClipboardButton.IsEnabled = false;
            AddEnhancementListEntry("No X/26 enhancement packets on this page.");
            return;
        }

        EnhancementClipboardButton.IsEnabled = true;

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
                        triplet.TripletNumber,
                        packet);
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
                    triplet.TripletNumber,
                    packet);
                displayedTriplets++;

                // The remaining packet bytes after this marker are padding, not enhancements.
                if (extendedMode == 0x1F && (triplet.Data & 1) != 0)
                    break;
            }
        }

        EnhancementInfoText.Text =
            $"X/26: {page.EnhancementPackets.Count} packet(s), {displayedTriplets} triplet(s)";
    }

    private async void OnCopyEnhancementDiagnosticsClicked(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is null || SquashGrid.Page is not { } page) return;

        var report = new StringBuilder();
        report.AppendLine("TeletextRecoveReese X/26 packet diagnostics");
        report.AppendLine($"Page: {page.Magazine}{page.PageNumber:X2}");
        report.AppendLine($"Subpage: {page.SubPage:X4}");
        report.AppendLine($"Packets: {page.EnhancementPackets.Count}");
        report.AppendLine($"Captured: {DateTimeOffset.Now:O}");
        report.AppendLine();

        foreach (var packet in page.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
        {
            report.AppendLine($"PACKET designation=0x{packet.DesignationCode:X1} sourceIndex={packet.PacketIndex}");
            report.AppendLine($"RAW42: {string.Join(' ', packet.RawPacket.Select(value => $"{value:X2}"))}");
            foreach (var triplet in packet.Triplets.OrderBy(triplet => triplet.TripletNumber))
            {
                string status = triplet.UncorrectableError
                    ? "UNCORRECTABLE"
                    : triplet.CorrectedError ? "CORRECTED" : "OK";
                report.AppendLine(
                    $"  t{triplet.TripletNumber:00}: address=0x{triplet.Address:X2} " +
                    $"mode=0x{triplet.Mode:X2} extended=0x{triplet.ExtendedMode:X2} " +
                    $"data=0x{triplet.Data:X2} hamming={status} | {DescribeEnhancementTriplet(triplet)}");
            }
            report.AppendLine();
        }

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(report.ToString()));
        await Clipboard.SetDataAsync(transfer);

        EnhancementClipboardButton.Content = "✓";
        await Task.Delay(900);
        EnhancementClipboardButton.Content = "📋";
    }

    private void AddEnhancementListEntry(
        string text,
        int designationCode = -1,
        int tripletNumber = -1,
        EnhancementPacket? packet = null)
    {
        var entry = new EnhancementListEntry(text, designationCode, tripletNumber, packet);
        _enhancementListEntries.Add(entry);
        EnhancementItemsControl.Items.Add(entry);
        if (designationCode >= 0 && tripletNumber >= 0)
            _enhancementEntriesByTriplet[(designationCode, tripletNumber)] = entry;
    }

    private void OnEnhancementSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selectedEntries = (EnhancementItemsControl.SelectedItems?
            .OfType<EnhancementListEntry>() ?? Enumerable.Empty<EnhancementListEntry>())
            .ToHashSet();
        foreach (var entry in _enhancementListEntries)
            entry.IsSelected = entry.Packet is not null && selectedEntries.Contains(entry);
    }

    private void OnDeleteEnhancementPacketClicked(object? sender, RoutedEventArgs e)
    {
        if (_squashPage is null
            || sender is not Button { DataContext: EnhancementListEntry entry }
            || entry.Packet is not { } packet
            || !_squashPage.EnhancementPackets.Contains(packet))
            return;

        EnsurePageHistory(_squashPage);
        if (!PageAssembler.DeleteEnhancementTriplet(
                _squashPage,
                packet,
                entry.TripletNumber))
            return;

        CommitPageEdit(_squashPage);
        UpdateEnhancementList(_squashPage);
        SquashGrid.InvalidateVisual();
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
        SquashDeletePageButton.IsEnabled = squashIndex >= 0;
        UpdateRestorationProgress(squashAddresses.Count, squashIndex);

        SquashJumpToBroadcastButton.IsEnabled = _broadcastFileOpen;
        BroadcastJumpToSquashButton.IsEnabled = _squashFileOpen;
        UpdateCreateSquashedStreamMenuAvailability();
        UpdateSquashAddressToolbarVisibility();
    }

    private void UpdateCreateSquashedStreamMenuAvailability()
    {
        bool canCreate = _broadcastFileOpen
            && _broadcastPackets.Count > 0
            && !_squashPaneEstablished;
        CreateSquashedStreamMenuItem.IsEnabled = canCreate;
        if (_nativeCreateSquashedStreamMenuItem is not null)
            _nativeCreateSquashedStreamMenuItem.IsEnabled = canCreate;
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
        UpdateCreateSquashedStreamMenuAvailability();
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
        StopFlashRoll();
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
        StopFlashRoll();
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
        StopFlashRoll();
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
        else
            UpdateBroadcastVersionButtons();
    }

    private void OnVersionComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        StopFlashRoll();
        if (!TryGetSelectedMagazine(out var magazine)) return;
        if (!TryGetSelectedPageNumber(out var page)) return;
        if (!TryGetSelectedSubpage(out var subpage)) return;
        if (VersionComboBox.SelectedIndex < 0) return;

        var instances = _store.GetInstances(magazine, page, subpage);
        if (VersionComboBox.SelectedIndex >= instances.Count) return;

        var selectedPage = instances[VersionComboBox.SelectedIndex].Page;
        PrepareBroadcastPageForDisplay(selectedPage);
        BroadcastGrid.Page = selectedPage;
        UpdateBroadcastVersionButtons();
        UpdateNavigationButtons();
        UpdateVideoBookmarkUi();
        PersistRecentFilePositions();
    }

    private void OnBroadcastVersionToolbarSizeChanged(object? sender, SizeChangedEventArgs e) =>
        UpdateBroadcastVersionButtons();

    private void UpdateBroadcastVersionButtons()
    {
        if (BroadcastVersionButtonsGrid is null) return;
        BroadcastVersionButtonsGrid.Children.Clear();

        int total = VersionComboBox.Items.Count;
        BroadcastFlashRollButton.IsEnabled = total > 1;
        int selected = VersionComboBox.SelectedIndex;
        if (total <= 0 || selected < 0)
        {
            BroadcastFastPreviewText.IsVisible = false;
            return;
        }

        double availableWidth = BroadcastEditToolbar.Bounds.Width - 12;
        const double buttonSlotWidth = 60;
        const double fastPreviewWidth = 105;
        bool showFastPreview = availableWidth > 0
            && total * buttonSlotWidth + fastPreviewWidth <= availableWidth;
        BroadcastFastPreviewText.IsVisible = showFastPreview;
        double buttonsAvailableWidth = showFastPreview
            ? availableWidth - fastPreviewWidth
            : availableWidth;
        int visibleCount = availableWidth > 0
            ? Math.Clamp((int)(buttonsAvailableWidth / buttonSlotWidth), 1, total)
            : Math.Min(total, 8);
        int first = Math.Clamp(selected - visibleCount / 2, 0, total - visibleCount);

        for (int versionIndex = first; versionIndex < first + visibleCount; versionIndex++)
        {
            int capturedIndex = versionIndex;
            var button = new Button
            {
                Content = $"v{versionIndex}",
                Width = 58,
                Height = 30,
                Margin = new Thickness(1, 0),
                Padding = new Thickness(4, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = versionIndex == selected ? FontWeight.Bold : FontWeight.Normal,
                Background = versionIndex == selected
                    ? new SolidColorBrush(Color.Parse("#506FAF"))
                    : new SolidColorBrush(Color.Parse("#3A3A3D")),
                BorderBrush = versionIndex == selected
                    ? new SolidColorBrush(Color.Parse("#A8C8FF"))
                    : new SolidColorBrush(Color.Parse("#55555A")),
            };
            button.Click += (_, _) => VersionComboBox.SelectedIndex = capturedIndex;
            button.PointerEntered += (_, _) => PreviewBroadcastVersion(capturedIndex);
            button.PointerExited += (_, _) => RestoreSelectedBroadcastVersionAfterPreview();
            BroadcastVersionButtonsGrid.Children.Add(button);
        }
    }

    private void PreviewBroadcastVersion(int versionIndex)
    {
        if (_flashRollActive) return;
        if (!TryGetSelectedMagazine(out int magazine)
            || !TryGetSelectedPageNumber(out int page)
            || !TryGetSelectedSubpage(out int subpage))
            return;

        var instances = _store.GetInstances(magazine, page, subpage);
        if (versionIndex < 0 || versionIndex >= instances.Count) return;
        var previewPage = instances[versionIndex].Page;
        PrepareBroadcastPageForDisplay(previewPage);
        _previewingBroadcastVersion = true;
        BroadcastGrid.Page = previewPage;
    }

    private void RestoreSelectedBroadcastVersionAfterPreview()
    {
        if (_flashRollActive) return;
        if (!_previewingBroadcastVersion
            || !TryGetSelectedMagazine(out int magazine)
            || !TryGetSelectedPageNumber(out int page)
            || !TryGetSelectedSubpage(out int subpage))
            return;

        _previewingBroadcastVersion = false;
        var instances = _store.GetInstances(magazine, page, subpage);
        int selected = VersionComboBox.SelectedIndex;
        if (selected < 0 || selected >= instances.Count) return;
        var selectedPage = instances[selected].Page;
        PrepareBroadcastPageForDisplay(selectedPage);
        BroadcastGrid.Page = selectedPage;
    }

    private void OnBroadcastFlashRollClicked(object? sender, RoutedEventArgs e)
    {
        if (_flashRollActive)
        {
            StopFlashRoll();
            return;
        }

        if (!TryGetBroadcastAddress(out var address)) return;
        var instances = _store.GetInstances(address.magazine, address.page, address.subpage);
        int selected = VersionComboBox.SelectedIndex;
        if (instances.Count < 2 || selected < 0 || selected >= instances.Count) return;

        RestoreSelectedBroadcastVersionAfterPreview();
        _flashRollAddress = address;
        _flashRollStartVersion = selected;
        _flashRollOffset = 0;
        _flashRollActive = true;
        _previewingBroadcastVersion = true;
        UpdateFlashRollButton();
        _flashRollTimer.Start();
    }

    private void OnFlashRollTick(object? sender, EventArgs e)
    {
        if (!_flashRollActive
            || _flashRollAddress is not { } address
            || !TryGetBroadcastAddress(out var currentAddress)
            || currentAddress != address)
        {
            StopFlashRoll();
            return;
        }

        var instances = _store.GetInstances(address.magazine, address.page, address.subpage);
        if (instances.Count < 2 || _flashRollStartVersion >= instances.Count)
        {
            StopFlashRoll();
            return;
        }

        _flashRollOffset = (_flashRollOffset + 1) % instances.Count;
        int versionIndex = (_flashRollStartVersion + _flashRollOffset) % instances.Count;
        var page = instances[versionIndex].Page;
        PrepareBroadcastPageForDisplay(page);
        BroadcastGrid.Page = page;
    }

    private void StopFlashRoll()
    {
        if (!_flashRollActive)
        {
            _flashRollTimer?.Stop();
            UpdateFlashRollButton();
            return;
        }

        _flashRollTimer.Stop();
        _flashRollActive = false;
        _flashRollAddress = null;
        _flashRollOffset = 0;
        _previewingBroadcastVersion = false;

        if (TryGetBroadcastAddress(out var address))
        {
            var instances = _store.GetInstances(address.magazine, address.page, address.subpage);
            int selected = VersionComboBox.SelectedIndex;
            if (selected >= 0 && selected < instances.Count)
            {
                var selectedPage = instances[selected].Page;
                PrepareBroadcastPageForDisplay(selectedPage);
                BroadcastGrid.Page = selectedPage;
            }
        }

        UpdateFlashRollButton();
    }

    private void UpdateFlashRollButton()
    {
        if (BroadcastFlashRollButton is null) return;
        BroadcastFlashRollButton.Content = _flashRollActive ? "Roll: On" : "Roll: Off";
        BroadcastFlashRollButton.Background = _flashRollActive
            ? new SolidColorBrush(Color.Parse("#8A5B20"))
            : null;
    }

    private void PrepareBroadcastPageForDisplay(TeletextPage page)
    {
        ApplyFileG0SubsetToPage(page, broadcast: true);
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
            ApplyFileG0SubsetToPage(_squashPage, broadcast: false);
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

    private async Task<RecoverySquashOptions?> ShowRecoverySquashOptionsAsync()
    {
        var minimumRows = new NumericUpDown
        {
            Minimum = 0, Maximum = 24, Value = 3, Width = 150,
        };
        var limitSubpages = new CheckBox
        {
            Content = "Discard subpages above",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var maximumSubpage = new NumericUpDown
        {
            Minimum = 0, Maximum = 0x1FFF, Value = 99, Width = 150,
        };
        var standardPages = new CheckBox
        {
            Content = "Standard decimal page numbers only (100–899)",
            IsChecked = true,
        };
        var minimumReceptions = new NumericUpDown
        {
            Minimum = 1, Maximum = 1000, Value = 1, Width = 150,
        };
        var requireServiceHeader = new CheckBox
        {
            Content = "Require service-name header match",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var headerSimilarity = new NumericUpDown
        {
            Minimum = 0, Maximum = 100, Value = 60, Width = 150,
        };
        var createButton = new Button
        {
            Content = "Create",
            Width = 90,
            IsDefault = true,
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 90,
            IsCancel = true,
        };
        var dialog = new Window
        {
            Title = "Create squashed stream",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Width = 590,
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Recovery filters",
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 16,
                    },
                    new TextBlock
                    {
                        Text = "Filters remove implausible page addresses before the best rows are combined. Singleton pages remain allowed by default.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.LightGray,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "Minimum distinct body rows", Width = 300, VerticalAlignment = VerticalAlignment.Center },
                            minimumRows,
                        },
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children = { limitSubpages, maximumSubpage, new TextBlock { Text = "(decimal)", VerticalAlignment = VerticalAlignment.Center } },
                    },
                    standardPages,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "Minimum receptions of an address", Width = 300, VerticalAlignment = VerticalAlignment.Center },
                            minimumReceptions,
                        },
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children = { requireServiceHeader, headerSimilarity, new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center } },
                    },
                    new TextBlock
                    {
                        Text = "The service-name signature is learned automatically from stable header characters across the stream. Changing clocks and page numbers are ignored.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.LightGray,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, createButton },
                    },
                },
            },
        };

        limitSubpages.IsCheckedChanged += (_, _) =>
            maximumSubpage.IsEnabled = limitSubpages.IsChecked == true;
        requireServiceHeader.IsCheckedChanged += (_, _) =>
            headerSimilarity.IsEnabled = requireServiceHeader.IsChecked == true;

        RecoverySquashOptions? result = null;
        createButton.Click += (_, _) =>
        {
            result = new RecoverySquashOptions
            {
                MinimumBodyRows = (int)(minimumRows.Value ?? 3),
                MaximumSubpage = limitSubpages.IsChecked == true
                    ? (int)(maximumSubpage.Value ?? 99)
                    : null,
                StandardDecimalPagesOnly = standardPages.IsChecked == true,
                MinimumReceptions = (int)(minimumReceptions.Value ?? 1),
                RequireServiceHeader = requireServiceHeader.IsChecked == true,
                MinimumHeaderSimilarityPercent = (int)(headerSimilarity.Value ?? 60),
            };
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return result;
    }

    private async void OnCreateSquashedStreamClicked(object? sender, RoutedEventArgs e)
    {
        if (!_broadcastFileOpen || _broadcastPackets.Count == 0 || _squashPaneEstablished) return;

        RecoverySquashOptions? options = await ShowRecoverySquashOptionsAsync();
        if (options is null) return;

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Width = 420,
            Height = 18,
        };
        var progressText = new TextBlock { Text = "Preparing recovery…" };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var dialog = new Window
        {
            Title = "Create squashed stream",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children = { progressText, progressBar, cancelButton },
            },
        };
        using var cancellation = new CancellationTokenSource();
        bool operationFinished = false;
        cancelButton.Click += (_, _) =>
        {
            cancelButton.IsEnabled = false;
            progressText.Text = "Cancelling…";
            cancellation.Cancel();
            dialog.Close();
        };
        dialog.Closing += (_, _) =>
        {
            if (!operationFinished)
                cancellation.Cancel();
        };

        IProgress<(string phase, int completed, int total)> progress =
            new Progress<(string phase, int completed, int total)>(state =>
        {
            progressBar.Maximum = Math.Max(state.total, 1);
            progressBar.Value = Math.Clamp(state.completed, 0, Math.Max(state.total, 1));
            int percent = state.total > 0 ? state.completed * 100 / state.total : 0;
            progressText.Text = $"{state.phase}… {state.completed} / {state.total} ({percent}%)";
        });

        dialog.Show(this);
        await Task.Yield();
        try
        {
            var packets = await Task.Run(() => RecoverySquasher.Build(
                _broadcastPackets,
                options,
                (phase, completed, total) => progress.Report((phase, completed, total)),
                cancellation.Token));
            operationFinished = true;
            dialog.Close();

            if (packets.Count == 0)
            {
                await ShowMessageAsync("Create squashed stream", "No recoverable pages were found in the broadcast.");
                return;
            }

            await using var stream = new MemoryStream(packets.Count * 42);
            foreach (byte[] packet in packets)
                await stream.WriteAsync(packet);
            stream.Position = 0;
            await LoadSquashStreamAsync(stream, filePath: null);
            _squashFilePath = null;
            _squashPaneEstablished = true;
            UpdateSquashFileFooter();
            SetSquashDirty(true);
            UpdateWorkspacePaneVisibility();
            FitWindowToContent();
        }
        catch (OperationCanceledException)
        {
            operationFinished = true;
            dialog.Close();
        }
        catch (Exception ex)
        {
            operationFinished = true;
            dialog.Close();
            await ShowMessageAsync("Squash recovery failed", ex.Message);
        }
    }

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

    private async Task<bool> ConfirmOpenPartialVbiAsync(long packetCount)
    {
        bool open = false;
        var discardButton = new Button { Content = "Discard", Width = 90 };
        var openButton = new Button { Content = "Open partial", Width = 110 };
        var dialog = new Window
        {
            Title = "VBI deconvolution aborted",
            Width = 460,
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
                        Text = $"Deconvolution was aborted, but {packetCount:N0} Teletext packets were recovered. Open or save the partial capture?",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { discardButton, openButton },
                    },
                },
            },
        };
        discardButton.Click += (_, _) => dialog.Close();
        openButton.Click += (_, _) => { open = true; dialog.Close(); };
        await dialog.ShowDialog(this);
        return open;
    }

    private async Task<LiveCaptureCompletionChoice> ShowLiveCaptureCompletionDialogAsync(
        string? rawCapturePath,
        long packetCount)
    {
        LiveCaptureCompletionChoice choice = LiveCaptureCompletionChoice.Discard;
        bool hasRawCapture = rawCapturePath is not null && File.Exists(rawCapturePath);
        var saveRawButton = new Button
        {
            Content = "Save VBI file…",
            Width = 120,
            IsEnabled = hasRawCapture,
        };
        if (!hasRawCapture)
            ToolTip.SetTip(saveRawButton, "Raw VBI recording was disabled for this capture");
        var discardButton = new Button
        {
            Content = "Discard all",
            Width = 100,
            Background = new SolidColorBrush(Color.Parse("#B42318")),
            Foreground = Brushes.White,
        };
        var openButton = new Button
        {
            Content = "Open decoded .t42",
            Width = 145,
            IsEnabled = packetCount > 0,
        };
        var dialog = new Window
        {
            Title = "Live VBI capture complete",
            Width = 560,
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
                        Text = packetCount > 0
                            ? hasRawCapture
                                ? $"Recovered {packetCount:N0} Teletext packets. You can save the raw VBI capture, then open the decoded stream or discard everything."
                                : $"Recovered {packetCount:N0} Teletext packets. Raw VBI recording was disabled; open the decoded stream or discard everything."
                            : hasRawCapture
                                ? "No Teletext packets were recovered. You can still save the raw VBI capture for later analysis."
                                : "No Teletext packets were recovered. Raw VBI recording was disabled for this capture.",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                        ColumnSpacing = 8,
                        Children = { saveRawButton, discardButton, openButton },
                    },
                },
            },
        };
        Grid.SetColumn(discardButton, 2);
        Grid.SetColumn(openButton, 3);

        saveRawButton.Click += async (_, _) =>
        {
            if (rawCapturePath is null) return;
            saveRawButton.IsEnabled = false;
            try
            {
                IStorageFile? destination = await StorageProvider.SaveFilePickerAsync(
                    new FilePickerSaveOptions
                    {
                        Title = "Save raw VBI capture",
                        SuggestedFileName = $"live-{DateTime.Now:yyyyMMdd-HHmmss}.vbi",
                        DefaultExtension = "vbi",
                        FileTypeChoices = new[]
                        {
                            new FilePickerFileType("Raw VBI sample stream")
                            {
                                Patterns = new[] { "*.vbi", "*.bin" },
                            },
                        },
                    });
                string? destinationPath = destination?.Path.IsFile == true
                    ? destination.Path.LocalPath
                    : null;
                if (destination is not null && destinationPath is null)
                {
                    await ShowMessageAsync("Save raw VBI capture", "The selected destination is not a local file.");
                    return;
                }
                if (destinationPath is not null)
                {
                    File.Copy(rawCapturePath, destinationPath, overwrite: true);
                    saveRawButton.Content = "Save VBI again…";
                }
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Could not save raw VBI capture", ex.Message);
            }
            finally
            {
                saveRawButton.IsEnabled = true;
            }
        };
        discardButton.Click += (_, _) => dialog.Close();
        openButton.Click += (_, _) =>
        {
            choice = LiveCaptureCompletionChoice.OpenDecoded;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        return choice;
    }

    private async Task<bool> ConfirmSaveLiveDecodedCaptureAsync(long packetCount)
    {
        bool save = false;
        var noButton = new Button { Content = "No, keep Untitled", Width = 140 };
        var yesButton = new Button { Content = "Save decoded .t42…", Width = 150 };
        var dialog = new Window
        {
            Title = "Save decoded live capture?",
            Width = 500,
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
                        Text = $"The decoded full stream is open as Untitled ({packetCount:N0} packets). Save it as a .t42 file?",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { noButton, yesButton },
                    },
                },
            },
        };
        noButton.Click += (_, _) => dialog.Close();
        yesButton.Click += (_, _) => { save = true; dialog.Close(); };
        await dialog.ShowDialog(this);
        return save;
    }

    private async Task SaveOpenedLiveDecodedCaptureAsync(string temporaryOutput)
    {
        IStorageFile? destination = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save decoded live capture",
                SuggestedFileName = $"live-{DateTime.Now:yyyyMMdd-HHmmss}.t42",
                DefaultExtension = "t42",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Raw Teletext packet stream")
                    {
                        Patterns = new[] { "*.t42" },
                    },
                },
            });
        string? destinationPath = destination?.Path.IsFile == true
            ? destination.Path.LocalPath
            : null;
        if (destination is not null && destinationPath is null)
        {
            await ShowMessageAsync("Save decoded live capture", "The selected destination is not a local file.");
            return;
        }
        if (destinationPath is null) return;

        try
        {
            File.Copy(temporaryOutput, destinationPath, overwrite: true);
            _broadcastFilePath = destinationPath;
            BroadcastFilePathText.Text = FormatFileFooter(
                destinationPath, _store.TotalInstanceCount);
            UpdateWindowAndPaneTitles();
            await RememberFileAsync(destinationPath, broadcast: true);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not save decoded live capture", ex.Message);
        }
    }

    private async Task ShowEnhancementErrorAsync(
        string title,
        string message,
        TeletextPage page)
    {
        if (!message.Contains("uncorrectable triplet", StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync(title, message);
            return;
        }

        var corrupt = page.EnhancementPackets
            .OrderBy(packet => packet.DesignationCode)
            .SelectMany(packet => packet.Triplets.Select(triplet => (Packet: packet, Triplet: triplet)))
            .FirstOrDefault(item => item.Triplet.UncorrectableError);
        if (corrupt.Packet is null)
        {
            await ShowMessageAsync(title, message);
            return;
        }

        bool deleteTriplet = false;
        var okButton = new Button { Content = "OK", Width = 80 };
        var deleteButton = new Button { Content = "Delete triplet", Width = 120 };
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
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { okButton, deleteButton },
                    },
                },
            },
        };
        okButton.Click += (_, _) => dialog.Close();
        deleteButton.Click += (_, _) =>
        {
            deleteTriplet = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);

        if (!deleteTriplet) return;
        EnsurePageHistory(page);
        if (!PageAssembler.DeleteEnhancementTriplet(
                page,
                corrupt.Packet,
                corrupt.Triplet.TripletNumber))
            return;

        CommitPageEdit(page);
        UpdateEnhancementList(page);
        SquashGrid.InvalidateVisual();
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
    private async void OnSaveCapturedStreamClicked(object? sender, RoutedEventArgs e) =>
        await SaveCapturedStreamAsync();

    private async Task<bool> SaveCapturedStreamAsync()
    {
        if (!HasUnsavedCapturedStream()) return false;

        IStorageFile? destination = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save captured full broadcast stream",
                SuggestedFileName = $"captured-stream-{DateTime.Now:yyyyMMdd-HHmmss}.t42",
                DefaultExtension = "t42",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Raw Teletext packet stream")
                    {
                        Patterns = new[] { "*.t42" },
                    },
                },
            });
        string? destinationPath = destination?.Path.IsFile == true
            ? destination.Path.LocalPath
            : null;
        if (destination is not null && destinationPath is null)
        {
            await ShowMessageAsync("Save captured stream", "The selected destination is not a local file.");
            return false;
        }
        if (destinationPath is null) return false;

        try
        {
            await using (var output = new FileStream(
                             destinationPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                foreach (byte[] packet in _broadcastPackets)
                    await output.WriteAsync(packet);
                await output.FlushAsync();
            }

            _broadcastFilePath = destinationPath;
            _sessionState.BroadcastFilePath = destinationPath;
            BroadcastFilePathText.Text = FormatFileFooter(
                destinationPath, _store.TotalInstanceCount);
            UpdateWindowAndPaneTitles();
            await RememberFileAsync(destinationPath, broadcast: true);
            SaveSessionState();
            return true;
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not save captured stream", ex.Message);
            return false;
        }
    }

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
