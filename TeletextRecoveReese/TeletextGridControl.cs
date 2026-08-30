using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using TeletextRecoveReese.Core;

namespace TeletextRecoveReese;

public sealed class DiacriticMoveRequestedEventArgs(
    int designationCode,
    int tripletNumber,
    int sourceColumn,
    int sourceRow,
    int targetColumn,
    int targetRow) : EventArgs
{
    public int DesignationCode { get; } = designationCode;
    public int TripletNumber { get; } = tripletNumber;
    public int SourceColumn { get; } = sourceColumn;
    public int SourceRow { get; } = sourceRow;
    public int TargetColumn { get; } = targetColumn;
    public int TargetRow { get; } = targetRow;
}

public sealed class EnhancementHoverChangedEventArgs(int designationCode, int tripletNumber) : EventArgs
{
    public int DesignationCode { get; } = designationCode;
    public int TripletNumber { get; } = tripletNumber;
}

public sealed class DiacriticDeleteRequestedEventArgs(int designationCode, int tripletNumber) : EventArgs
{
    public int DesignationCode { get; } = designationCode;
    public int TripletNumber { get; } = tripletNumber;
}

/// <summary>
/// Manually renders the 40x24 teletext grid via DrawingContext (no per-cell
/// TextBox/TextBlock - that would be too slow and wouldn't look authentic).
/// </summary>
public class TeletextGridControl : Control
{
    public static readonly StyledProperty<TeletextPage?> PageProperty =
        AvaloniaProperty.Register<TeletextGridControl, TeletextPage?>(nameof(Page));

    public event EventHandler? CellSelected;
    public event EventHandler<DiacriticMoveRequestedEventArgs>? DiacriticMoveRequested;
    public event EventHandler<DiacriticDeleteRequestedEventArgs>? DiacriticDeleteRequested;
    public event EventHandler<EnhancementHoverChangedEventArgs>? EnhancementHoverChanged;

    public bool IsActive { get; set; } = true;
    private bool _renderingScreenshot;
    private TeletextPage? _screenshotPageOverride;
    private bool _screenshotAnimateFlash;
    private bool _screenshotFlashVisible = true;

    public void SaveScreenshotPng(
        Stream stream,
        TeletextPage? page = null,
        bool animateFlash = false,
        bool flashVisible = true)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)(Columns * CellWidth), (int)(Rows * CellHeight)),
            new Vector(96, 96));
        _renderingScreenshot = true;
        _screenshotPageOverride = page;
        _screenshotAnimateFlash = animateFlash;
        _screenshotFlashVisible = flashVisible;
        try
        {
            InvalidateVisual();
            bitmap.Render(this);
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        }
        finally
        {
            _screenshotPageOverride = null;
            _screenshotAnimateFlash = false;
            _screenshotFlashVisible = true;
            _renderingScreenshot = false;
            InvalidateVisual();
        }
    }

    public void ClearSelection()
    {
        _hasSelection = false;
        InvalidateVisual();
    }

    public TeletextPage? Page
    {
        get => GetValue(PageProperty);
        set
        {
            SetValue(PageProperty, value);
            if (value is not null && IsActive)
            {
                NormalizeSelection();
                _hasSelection = true;
            }
            InvalidateVisual();
        }
    }

    private const int Columns = 40;
    private const int Rows = 25;

    // Classic teletext cell aspect ratio ~ 1:1.5 (height/width)
    private const double CellWidth = 16;
    private const double CellHeight = 24;

    private Typeface _gridTypeface = new(FontFamily.Default);
    private bool _useTifaxNineWorkaround;

    public void SetFontFamily(FontFamily fontFamily, string? familyName = null)
    {
        _gridTypeface = new Typeface(fontFamily);
        _useTifaxNineWorkaround = (familyName ?? fontFamily.Name)
            .Contains("Tifax", StringComparison.OrdinalIgnoreCase);
        InvalidateVisual();
    }

    // Selection is painted after all page content so it always remains visible.
    private static readonly Brush SelFillBrush = new SolidColorBrush(Color.Parse("#553344AA"));
    private static readonly Brush SelBorderBrush = new SolidColorBrush(Color.Parse("#B8D0D0D0"));
    private static readonly Pen SelBorderPen = new(SelBorderBrush, 2, DashStyle.Dash);
    private static readonly Brush RecoverySelectionFillBrush = new SolidColorBrush(Color.Parse("#5540B860"));
    private static readonly Brush RecoverySelectionBorderBrush = new SolidColorBrush(Color.Parse("#D090F0A0"));
    private static readonly Pen RecoverySelectionBorderPen = new(RecoverySelectionBorderBrush, 2, DashStyle.Dash);
    // Same opacity/weight as the normal selector; warning pulses change only the hue.
    private static readonly Brush WarningFillBrush = new SolidColorBrush(Color.Parse("#55AA3344"));
    private static readonly Brush WarningBorderBrush = new SolidColorBrush(Color.Parse("#B8F0A0A8"));
    private static readonly Pen WarningBorderPen = new(WarningBorderBrush, 2, DashStyle.Dash);
    private static readonly Brush ControlCodeFillBrush = new SolidColorBrush(Color.Parse("#4430B060"));
    private static readonly Brush ControlCodeBorderBrush = new SolidColorBrush(Color.Parse("#C080E0A0"));
    private static readonly Pen ControlCodeBorderPen = new(ControlCodeBorderBrush, 1.5, DashStyle.Dash);
    private static readonly Typeface ControlCodeTypeface = new("Consolas, DejaVu Sans Mono, monospace");
    private static readonly Brush DiacriticFillBrush = new SolidColorBrush(Color.Parse("#44D03040"));
    private static readonly Brush DiacriticBorderBrush = new SolidColorBrush(Color.Parse("#E0FF7080"));
    private static readonly Pen DiacriticBorderPen = new(DiacriticBorderBrush, 1.5, DashStyle.Dash);
    private static readonly Brush TransferRowFillBrush = new SolidColorBrush(Color.Parse("#44C030C8"));
    private static readonly Brush TransferRowBorderBrush = new SolidColorBrush(Color.Parse("#E8F080F0"));
    private static readonly Pen TransferRowBorderPen = new(TransferRowBorderBrush, 1.5, DashStyle.Dash);
    private static readonly Brush PinnedTransferRowFillBrush = new SolidColorBrush(Color.Parse("#44D0B020"));
    private static readonly Brush PinnedTransferRowBorderBrush = new SolidColorBrush(Color.Parse("#FFF0D050"));
    private static readonly Pen PinnedTransferRowBorderPen = new(PinnedTransferRowBorderBrush, 2, DashStyle.Dash);
    private static readonly Brush HoverInfoBackgroundBrush = new SolidColorBrush(Color.Parse("#F02B2B2B"));
    private static readonly Brush HoverInfoBorderBrush = new SolidColorBrush(Color.Parse("#FF707070"));
    private static readonly Brush HoverInfoTextBrush = new SolidColorBrush(Color.Parse("#FFF2F2F2"));
    private static readonly Brush HoverInfoShadowBrush = new SolidColorBrush(Color.Parse("#66000000"));
    private static readonly Pen HoverInfoBorderPen = new(HoverInfoBorderBrush, 1);
    private static readonly Typeface HoverInfoTypeface = new("Inter, Segoe UI, sans-serif");

    private int _anchorRow = 0;
    private int _anchorCol = 0;
    private int _selectedRow = 0;
    private int _selectedColumn = 0;
    private int _selectionWidth = 1;
    private int _selectionHeight = 1;

    // Drag selection support
    private bool _isDragging = false;
    private bool _hasSelection = true;
    private int _dragRow = 0;
    private int _dragCol = 0;
    private bool _readOnlyWarning;
    private bool _recoveryBrowseActive;
    private bool _hideRecoverySelection;
    private string? _selectionStatusText;
    private int _selectionStatusGeneration;
    private int _recoveryBlinkGeneration;
    private int _warningGeneration;
    private bool _showControlCodes;
    private bool _showSelectionBytes;
    private bool _showDiacriticMarkers;
    private bool _suppressFlash;
    private bool _flashPhaseVisible = true;
    private bool _isDraggingDiacritic;
    private int _draggedDesignationCode = -1;
    private int _draggedTripletNumber = -1;
    private int _hoveredEnhancementDesignationCode = -1;
    private int _hoveredEnhancementTripletNumber = -1;
    private int _diacriticFlashColumn = -1;
    private int _diacriticFlashRow = -1;
    private bool _showDiacriticFlash;
    private int _diacriticFlashGeneration;
    private int _draggedDiacriticSourceColumn;
    private int _draggedDiacriticSourceRow;
    private int _draggedDiacriticTargetColumn;
    private int _draggedDiacriticTargetRow;
    private string? _hoverInfoText;
    private Point _hoverInfoPosition;
    private int _transferRowHighlight = -1;
    private int _pinnedTransferRowHighlight = -1;

    public TeletextGridControl()
    {
        Focusable = true;
    }

    public int SelectedRow => _selectedRow;
    public int SelectedColumn => _selectedColumn;
    public int SelectionWidth => _selectionWidth;
    public int SelectionHeight => _selectionHeight;

    public bool RecoveryBrowseActive
    {
        get => _recoveryBrowseActive;
        set
        {
            if (_recoveryBrowseActive == value) return;
            _recoveryBrowseActive = value;
            if (!value) _hideRecoverySelection = false;
            InvalidateVisual();
        }
    }

    public bool ShowControlCodes
    {
        get => _showControlCodes;
        set
        {
            if (_showControlCodes == value) return;
            _showControlCodes = value;
            CloseHoverInfoOverlay();
            InvalidateVisual();
        }
    }

    public bool ShowSelectionBytes
    {
        get => _showSelectionBytes;
        set
        {
            if (_showSelectionBytes == value) return;
            _showSelectionBytes = value;
            InvalidateVisual();
        }
    }

    public bool ShowDiacriticMarkers
    {
        get => _showDiacriticMarkers;
        set
        {
            if (_showDiacriticMarkers == value) return;
            _showDiacriticMarkers = value;
            if (!value)
            {
                ContextMenu?.Close();
                ContextMenu = null;
            }
            CloseHoverInfoOverlay();
            InvalidateVisual();
        }
    }

    public bool SuppressFlash
    {
        get => _suppressFlash;
        set
        {
            if (_suppressFlash == value) return;
            _suppressFlash = value;
            InvalidateVisual();
        }
    }

    public bool FlashPhaseVisible
    {
        get => _flashPhaseVisible;
        set
        {
            if (_flashPhaseVisible == value) return;
            _flashPhaseVisible = value;
            if (!SuppressFlash) InvalidateVisual();
        }
    }

    public async Task FlashDiacriticConfirmationAsync(int column, int row)
    {
        int generation = ++_diacriticFlashGeneration;
        _diacriticFlashColumn = column;
        _diacriticFlashRow = row;

        for (int pulse = 0; pulse < 2; pulse++)
        {
            _showDiacriticFlash = true;
            InvalidateVisual();
            await Task.Delay(110);
            if (generation != _diacriticFlashGeneration) return;

            _showDiacriticFlash = false;
            InvalidateVisual();
            await Task.Delay(90);
            if (generation != _diacriticFlashGeneration) return;
        }

        _diacriticFlashColumn = -1;
        _diacriticFlashRow = -1;
        InvalidateVisual();
    }

    public void SetTransferRowHighlight(int row)
    {
        int normalized = row is >= 0 and < Rows ? row : -1;
        if (_transferRowHighlight == normalized) return;
        _transferRowHighlight = normalized;
        InvalidateVisual();
    }

    public void SetPinnedTransferRowHighlight(int row)
    {
        int normalized = row is >= 0 and < Rows ? row : -1;
        if (_pinnedTransferRowHighlight == normalized) return;
        _pinnedTransferRowHighlight = normalized;
        InvalidateVisual();
    }

    public void SetSelectionSize(int w, int h)
    {
        _selectionWidth = w;
        _selectionHeight = h;
        InvalidateVisual();
    }

    public void MoveSelectionTo(int col, int row)
    {
        _selectedColumn = Math.Clamp(col, 0, Columns - 1);
        _selectedRow = Math.Clamp(row, 0, Rows - 1);
        _anchorCol = _selectedColumn;
        _anchorRow = _selectedRow;
        _dragCol = _selectedColumn;
        _dragRow = _selectedRow;
        _hasSelection = true;
        InvalidateVisual();
        CellSelected?.Invoke(this, EventArgs.Empty);
    }

    private void NormalizeSelection()
    {
        _selectedColumn = Math.Clamp(_selectedColumn, 0, Columns - 1);
        _selectedRow = Math.Clamp(_selectedRow, 0, Rows - 1);
        _selectionWidth = Math.Clamp(_selectionWidth, 1, Columns - _selectedColumn);
        _selectionHeight = Math.Clamp(_selectionHeight, 1, Rows - _selectedRow);
        _anchorCol = _selectedColumn;
        _anchorRow = _selectedRow;
        _dragCol = _selectedColumn + _selectionWidth - 1;
        _dragRow = _selectedRow + _selectionHeight - 1;
    }

    public async Task FlashReadOnlyWarningAsync()
    {
        int generation = ++_warningGeneration;

        for (int pulse = 0; pulse < 6; pulse++)
        {
            if (generation != _warningGeneration) return;
            _readOnlyWarning = pulse % 2 == 0;
            InvalidateVisual();
            await Task.Delay(110);
        }

        if (generation == _warningGeneration)
        {
            _readOnlyWarning = false;
            InvalidateVisual();
        }
    }

    public async Task ShowSelectionStatusAsync(string text)
    {
        int generation = ++_selectionStatusGeneration;
        _selectionStatusText = text;
        InvalidateVisual();
        await Task.Delay(1000);
        if (generation != _selectionStatusGeneration) return;
        _selectionStatusText = null;
        InvalidateVisual();
    }

    public async Task FlashRecoveryBoundaryAsync()
    {
        int generation = ++_recoveryBlinkGeneration;
        for (int pulse = 0; pulse < 4; pulse++)
        {
            if (generation != _recoveryBlinkGeneration) return;
            _hideRecoverySelection = pulse % 2 == 0;
            InvalidateVisual();
            await Task.Delay(100);
        }
        if (generation == _recoveryBlinkGeneration)
        {
            _hideRecoverySelection = false;
            InvalidateVisual();
        }
    }

    static TeletextGridControl()
    {
        AffectsRender<TeletextGridControl>(PageProperty);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        int col = Math.Clamp((int)(pos.X / CellWidth), 0, Columns - 1);
        int row = Math.Clamp((int)(pos.Y / CellHeight), 0, Rows - 1);
        bool rightButtonPressed = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;

        // ContextMenu is a control property and otherwise remains attached after
        // the first valid diacritic click. Detach that stale menu before every new
        // right-click so Avalonia cannot reopen it over an unrelated cell.
        if (rightButtonPressed)
        {
            ContextMenu?.Close();
            ContextMenu = null;
        }

        base.OnPointerPressed(e);
        Focus();

        if (ShowDiacriticMarkers && Page is not null
            && rightButtonPressed
            && DiacriticDeleteRequested is not null
            && Page.Grid[col, row].EnhancementDesignationCode >= 0)
        {
            var cell = Page.Grid[col, row];
            var deleteItem = new MenuItem { Header = "Delete diacritic" };
            deleteItem.Click += (_, _) => DiacriticDeleteRequested?.Invoke(
                this,
                new DiacriticDeleteRequestedEventArgs(
                    cell.EnhancementDesignationCode,
                    cell.EnhancementTripletNumber));
            var menu = new ContextMenu { ItemsSource = new[] { deleteItem } };
            ContextMenu = menu;
            menu.Closed += (_, _) =>
            {
                if (ReferenceEquals(ContextMenu, menu))
                    ContextMenu = null;
            };
            CloseHoverInfoOverlay();
            menu.Open(this);
            e.Handled = true;
            return;
        }

        if (ShowDiacriticMarkers && Page is not null
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && Page.Grid[col, row].EnhancementDesignationCode >= 0)
        {
            var cell = Page.Grid[col, row];
            _isDraggingDiacritic = true;
            _draggedDesignationCode = cell.EnhancementDesignationCode;
            _draggedTripletNumber = cell.EnhancementTripletNumber;
            _draggedDiacriticSourceColumn = _draggedDiacriticTargetColumn = col;
            _draggedDiacriticSourceRow = _draggedDiacriticTargetRow = row;
            CloseHoverInfoOverlay();
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        _anchorRow = row;
        _anchorCol = col;
        _selectedRow = row;
        _selectedColumn = col;
        _selectionWidth = 1;
        _selectionHeight = 1;
        _hasSelection = true;
        _isDragging = true;
        _dragRow = row;
        _dragCol = col;
        InvalidateVisual();

        CellSelected?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDraggingDiacritic)
        {
            var dragPosition = e.GetPosition(this);
            _draggedDiacriticTargetColumn = Math.Clamp((int)(dragPosition.X / CellWidth), 0, Columns - 1);
            _draggedDiacriticTargetRow = Math.Clamp((int)(dragPosition.Y / CellHeight), 0, Rows - 1);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        UpdateHoverInfoOverlay(e.GetPosition(this));
        if (!_isDragging) return;
        var pos = e.GetPosition(this);
        int col = Math.Clamp((int)(pos.X / CellWidth), 0, Columns - 1);
        int row = Math.Clamp((int)(pos.Y / CellHeight), 0, Rows - 1);

        _dragRow = row;
        _dragCol = col;

        // Calculate selection rectangle (mouse drag multi-select)
        int minRow = Math.Min(_anchorRow, row);
        int maxRow = Math.Max(_anchorRow, row);
        int minCol = Math.Min(_anchorCol, col);
        int maxCol = Math.Max(_anchorCol, col);
        _selectionWidth = maxCol - minCol + 1;
        _selectionHeight = maxRow - minRow + 1;
        _selectedRow = minRow;
        _selectedColumn = minCol;
        // We'll draw the rectangle in render; store just the anchor and drag
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDraggingDiacritic)
        {
            _isDraggingDiacritic = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            DiacriticMoveRequested?.Invoke(this, new DiacriticMoveRequestedEventArgs(
                _draggedDesignationCode,
                _draggedTripletNumber,
                _draggedDiacriticSourceColumn,
                _draggedDiacriticSourceRow,
                _draggedDiacriticTargetColumn,
                _draggedDiacriticTargetRow));
            InvalidateVisual();
            return;
        }
        _isDragging = false;
        CellSelected?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        CloseHoverInfoOverlay();
    }

    public override void Render(DrawingContext context)
    {
        var gridBounds = new Rect(0, 0, Columns * CellWidth, Rows * CellHeight);
        context.FillRectangle(Brushes.Black, gridBounds);
        TeletextPage? renderedPage = _screenshotPageOverride ?? Page;

        if (renderedPage is null)
        {
            if (!_renderingScreenshot)
                DrawSelection(context);
            return;
        }

        // EN 300 706 / QTeletextMaker Level-1 behaviour: if any Double Height
        // (0x0D) or Double Size (0x0F) attribute occurs in a row, the entire next
        // row is reserved for bottom halves. Its own Level-1 bytes are not shown.
        bool[] bottomHalfRows = FindLevelOneBottomHalfRows(renderedPage);

        // Pass 1: everything except double-height/width cells. Those are deferred to
        // pass 2 so they can be drawn scaled-up and painted LAST - a double-height
        // character visually overlaps into the row below it, and needs to paint over
        // whatever that row's own (unrelated) content would otherwise show there.
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                var cell = renderedPage.Grid[x, y];
                var origin = new Point(x * CellWidth, y * CellHeight);

                if (bottomHalfRows[y])
                {
                    // Normal-sized cells before the size code produce a space with
                    // the same attributes below them. Enlarged glyphs are painted
                    // over this background during pass 2 from the row above.
                    var upperCell = renderedPage.Grid[x, y - 1];
                    if (upperCell.Background != TeletextColor.Black)
                    {
                        context.FillRectangle(
                            ColorBrush(upperCell.Background),
                            new Rect(origin, new Size(CellWidth, CellHeight)));
                    }
                    continue;
                }

                if (cell.DoubleHeight || cell.DoubleWidth) continue; // handled (background + content) in pass 2

                if (cell.Background != TeletextColor.Black)
                {
                    context.FillRectangle(
                        ColorBrush(cell.Background),
                        new Rect(origin, new Size(CellWidth, CellHeight)));
                }

                if (cell.Conceal || ShouldHideFlashingCell(cell)) continue;

                DrawCellContent(context, origin, cell);
            }
        }

        // Pass 2: double-height / double-width / double-size cells. Clip the whole
        // pass to the grid so a scaled cell in the last column/row cannot paint
        // outside the control.
        using (context.PushClip(gridBounds))
        {
            // Paint every background before any enlarged content. Otherwise the
            // background of a character's reserved right/bottom half can be drawn
            // later and cut the already-painted glyph in half.
            for (int y = 0; y < Rows; y++)
            {
                for (int x = 0; x < Columns; x++)
                {
                    if (bottomHalfRows[y]) continue;
                    var cell = renderedPage.Grid[x, y];
                    if (!cell.DoubleHeight && !cell.DoubleWidth) continue;

                    double scaleY = cell.DoubleHeight ? 2.0 : 1.0;
                    var origin = new Point(x * CellWidth, y * CellHeight);

                    // Paint one cell-width of background for every source cell. A
                    // double-width character uses the following cell as its right
                    // half; scaling that following cell's background horizontally
                    // would erase part of the character.
                    if (cell.Background != TeletextColor.Black)
                    {
                        using (context.PushTransform(Matrix.CreateScale(1.0, scaleY)))
                        {
                            var backgroundOrigin = new Point(origin.X, origin.Y / scaleY);
                            context.FillRectangle(
                                ColorBrush(cell.Background),
                                new Rect(backgroundOrigin, new Size(CellWidth, CellHeight)));
                        }
                    }
                }
            }

            for (int y = 0; y < Rows; y++)
            {
                for (int x = 0; x < Columns; x++)
                {
                    if (bottomHalfRows[y]) continue;
                    var cell = renderedPage.Grid[x, y];
                    if (!cell.DoubleHeight && !cell.DoubleWidth) continue;
                    if (cell.Conceal || ShouldHideFlashingCell(cell)
                        || (cell.DoubleWidth && !IsDoubleWidthLeadCell(renderedPage, x, y)))
                        continue;

                    double scaleX = cell.DoubleWidth ? 2.0 : 1.0;
                    double scaleY = cell.DoubleHeight ? 2.0 : 1.0;
                    var origin = new Point(x * CellWidth, y * CellHeight);
                    using (context.PushTransform(Matrix.CreateScale(scaleX, scaleY)))
                    {
                        var scaledOrigin = new Point(origin.X / scaleX, origin.Y / scaleY);
                        DrawCellContent(context, scaledOrigin, cell);
                    }
                }
            }
        }

        if (!_renderingScreenshot)
        {
            DrawControlCodeOverlays(context);
            DrawSelectionByteOverlays(context);
            DrawDiacriticOverlays(context);
            DrawTransferRowHighlight(context);
            DrawPinnedTransferRowHighlight(context);

            // Always paint the selection last, above text, mosaics, control-code markers
            // and double-size cells.
            DrawSelection(context);
            DrawHoverInfoOverlay(context);
        }

    }

    private bool ShouldHideFlashingCell(Cell cell) =>
        cell.Flash && (_renderingScreenshot
            ? _screenshotAnimateFlash && !_screenshotFlashVisible
            : !SuppressFlash && !FlashPhaseVisible);

    private static bool IsDoubleWidthLeadCell(TeletextPage page, int column, int row)
    {
        // The size control occupies the first cell of a double-width run. After
        // it, alternating cells contain a character and its reserved right half.
        int runStart = column;
        while (runStart > 0 && page.Grid[runStart - 1, row].DoubleWidth)
            runStart--;

        return column > runStart && (column - runStart) % 2 == 1;
    }

    private static bool[] FindLevelOneBottomHalfRows(TeletextPage page)
    {
        var bottomHalfRows = new bool[Rows];

        // QTeletextMaker considers display rows 1-23. A top half may start only
        // through row 22, leaving row 23 for its bottom half; row 24 is unaffected.
        for (int row = 1; row < 24; row++)
        {
            bool hasDoubleHeightAttribute = false;
            if (page.RawRows[row] is { Length: 42 } raw)
            {
                for (int column = 0; column < Columns; column++)
                {
                    byte code = (byte)(raw[2 + column] & 0x7F);
                    if (code is 0x0D or 0x0F)
                    {
                        hasDoubleHeightAttribute = true;
                        break;
                    }
                }
            }

            if (hasDoubleHeightAttribute && row < 23)
            {
                bottomHalfRows[row + 1] = true;
                row++; // a bottom-half row cannot itself begin another pair
            }
        }

        return bottomHalfRows;
    }

    private void DrawControlCodeOverlays(DrawingContext context)
    {
        if (!ShowControlCodes || Page is null) return;

        for (int row = 0; row < Rows; row++)
        {
            int firstColumn = row == 0 ? 8 : 0;
            for (int column = firstColumn; column < Columns; column++)
            {
                if (!TryGetControlCode(column, row, out byte code)) continue;
                double x = column * CellWidth;
                double y = row * CellHeight;
                var rect = new Rect(x + 1, y + 1, CellWidth - 2, CellHeight - 2);
                context.FillRectangle(ControlCodeFillBrush, rect);
                context.DrawRectangle(ControlCodeBorderPen, rect);

                string hex = $"{code:X2}";
                IBrush digitBrush = InvertedColorBrush(Page.Grid[column, row].Background);
                var firstDigit = new FormattedText(
                    hex[0].ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    ControlCodeTypeface,
                    12,
                    digitBrush);
                var secondDigit = new FormattedText(
                    hex[1].ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    ControlCodeTypeface,
                    12,
                    digitBrush);
                context.DrawText(
                    firstDigit,
                    new Point(x + 1.0, y + 1.0));
                context.DrawText(
                    secondDigit,
                    new Point(x + CellWidth - secondDigit.Width - 1.0, y + 7.0));
            }
        }
    }

    private void DrawSelectionByteOverlays(DrawingContext context)
    {
        if (!ShowSelectionBytes || !IsActive || !_hasSelection || Page is null) return;

        int minRow = Math.Min(_anchorRow, _dragRow);
        int minColumn = Math.Min(_anchorCol, _dragCol);
        int maxRow = Math.Min(minRow + _selectionHeight, Rows);
        int maxColumn = Math.Min(minColumn + _selectionWidth, Columns);
        for (int row = minRow; row < maxRow; row++)
        {
            if (Page.RawRows[row] is not { Length: 42 } raw) continue;
            for (int column = minColumn; column < maxColumn; column++)
                DrawByteOverlay(context, column, row, (byte)(raw[2 + column] & 0x7F));
        }
    }

    private void DrawByteOverlay(DrawingContext context, int column, int row, byte value)
    {
        double x = column * CellWidth;
        double y = row * CellHeight;
        var rect = new Rect(x + 1, y + 1, CellWidth - 2, CellHeight - 2);
        context.FillRectangle(ControlCodeFillBrush, rect);
        context.DrawRectangle(ControlCodeBorderPen, rect);

        string hex = $"{value:X2}";
        IBrush digitBrush = InvertedColorBrush(Page!.Grid[column, row].Background);
        var firstDigit = new FormattedText(
            hex[0].ToString(),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            ControlCodeTypeface,
            12,
            digitBrush);
        var secondDigit = new FormattedText(
            hex[1].ToString(),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            ControlCodeTypeface,
            12,
            digitBrush);
        context.DrawText(firstDigit, new Point(x + 1.0, y + 1.0));
        context.DrawText(secondDigit, new Point(x + CellWidth - secondDigit.Width - 1.0, y + 7.0));
    }

    private void DrawDiacriticOverlays(DrawingContext context)
    {
        if (ShowDiacriticMarkers && Page is not null)
        {
            for (int row = 0; row < Rows; row++)
            for (int column = 0; column < Columns; column++)
            {
                if (string.IsNullOrEmpty(Page.Grid[column, row].EnhancementText)) continue;
                if (column == _diacriticFlashColumn && row == _diacriticFlashRow) continue;
                DrawDiacriticMarker(context, column, row);
            }
        }

        if (ShowDiacriticMarkers && _isDraggingDiacritic)
        {
            var targetRect = new Rect(
                _draggedDiacriticTargetColumn * CellWidth + 1,
                _draggedDiacriticTargetRow * CellHeight + 1,
                CellWidth - 2,
                CellHeight - 2);
            context.FillRectangle(WarningFillBrush, targetRect);
            context.DrawRectangle(WarningBorderPen, targetRect);
        }

        if (_showDiacriticFlash && _diacriticFlashColumn >= 0 && _diacriticFlashRow >= 0)
            DrawDiacriticMarker(context, _diacriticFlashColumn, _diacriticFlashRow);
    }

    private static void DrawDiacriticMarker(DrawingContext context, int column, int row)
    {
        var rect = new Rect(
            column * CellWidth + 1,
            row * CellHeight + 1,
            CellWidth - 2,
            CellHeight - 2);
        context.FillRectangle(DiacriticFillBrush, rect);
        context.DrawRectangle(DiacriticBorderPen, rect);
    }

    private void DrawTransferRowHighlight(DrawingContext context)
    {
        if (_transferRowHighlight < 0) return;
        var rect = new Rect(
            1,
            _transferRowHighlight * CellHeight + 1,
            Columns * CellWidth - 2,
            CellHeight - 2);
        context.FillRectangle(TransferRowFillBrush, rect);
        context.DrawRectangle(TransferRowBorderPen, rect);
    }

    private void DrawPinnedTransferRowHighlight(DrawingContext context)
    {
        if (_pinnedTransferRowHighlight < 0) return;
        var rect = new Rect(
            1,
            _pinnedTransferRowHighlight * CellHeight + 1,
            Columns * CellWidth - 2,
            CellHeight - 2);
        context.FillRectangle(PinnedTransferRowFillBrush, rect);
        context.DrawRectangle(PinnedTransferRowBorderPen, rect);
    }

    private void UpdateHoverInfoOverlay(Point position)
    {
        int column = Math.Clamp((int)(position.X / CellWidth), 0, Columns - 1);
        int row = Math.Clamp((int)(position.Y / CellHeight), 0, Rows - 1);

        string? tip = null;
        if (ShowDiacriticMarkers && Page is not null
            && !string.IsNullOrEmpty(Page.Grid[column, row].EnhancementText))
        {
            var cell = Page.Grid[column, row];
            tip = $"Level 1.5 diacritic — {cell.EnhancementDescription}";
            SetHoveredEnhancement(cell.EnhancementDesignationCode, cell.EnhancementTripletNumber);
        }
        else if (ShowControlCodes && TryGetControlCode(column, row, out byte code))
        {
            tip = $"0x{code:X2} — {ControlCodeName(code)}";
            SetHoveredEnhancement(-1, -1);
        }

        if (tip is null)
        {
            CloseHoverInfoOverlay();
            return;
        }

        _hoverInfoText = tip;
        _hoverInfoPosition = position;
        InvalidateVisual();
    }

    private void CloseHoverInfoOverlay()
    {
        SetHoveredEnhancement(-1, -1);
        if (_hoverInfoText is null) return;
        _hoverInfoText = null;
        InvalidateVisual();
    }

    private void SetHoveredEnhancement(int designationCode, int tripletNumber)
    {
        if (_hoveredEnhancementDesignationCode == designationCode
            && _hoveredEnhancementTripletNumber == tripletNumber)
            return;

        _hoveredEnhancementDesignationCode = designationCode;
        _hoveredEnhancementTripletNumber = tripletNumber;
        EnhancementHoverChanged?.Invoke(
            this,
            new EnhancementHoverChangedEventArgs(designationCode, tripletNumber));
    }

    private void DrawHoverInfoOverlay(DrawingContext context)
    {
        if (_hoverInfoText is null) return;

        var text = new FormattedText(
            _hoverInfoText,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            HoverInfoTypeface,
            12,
            HoverInfoTextBrush);

        const double horizontalPadding = 8;
        const double verticalPadding = 5;
        const double pointerOffset = 14;
        double width = text.Width + horizontalPadding * 2;
        double height = text.Height + verticalPadding * 2;
        double x = _hoverInfoPosition.X + pointerOffset;
        double y = _hoverInfoPosition.Y + pointerOffset;

        if (x + width > Bounds.Width)
            x = _hoverInfoPosition.X - width - pointerOffset;
        if (y + height > Bounds.Height)
            y = _hoverInfoPosition.Y - height - pointerOffset;

        x = Math.Clamp(x, 0, Math.Max(0, Bounds.Width - width));
        y = Math.Clamp(y, 0, Math.Max(0, Bounds.Height - height));

        var overlayRect = new Rect(x, y, width, height);
        var shadowRect = overlayRect.Translate(new Vector(2, 2));
        context.FillRectangle(HoverInfoShadowBrush, shadowRect);
        context.DrawRectangle(HoverInfoBackgroundBrush, HoverInfoBorderPen, overlayRect);
        context.DrawText(text, new Point(x + horizontalPadding, y + verticalPadding));
    }

    private bool TryGetControlCode(int column, int row, out byte code)
    {
        code = 0;
        if (Page?.RawRows[row] is not { } raw || raw.Length != 42) return false;
        if (row == 0 && column < 8) return false;
        code = (byte)(raw[2 + column] & 0x7F);
        return code <= 0x1F;
    }

    private static string ControlCodeName(byte code)
    {
        string[] colors = ["Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White"];
        if (code <= 0x07) return $"Alpha color: {colors[code]}";
        if (code is >= 0x10 and <= 0x17) return $"Mosaic color: {colors[code - 0x10]}";

        return code switch
        {
            0x08 => "Flash",
            0x09 => "Steady",
            0x0A => "End box",
            0x0B => "Start box",
            0x0C => "Normal size",
            0x0D => "Double height",
            0x0E => "Double width",
            0x0F => "Double size",
            0x18 => "Conceal display",
            0x19 => "Contiguous mosaic graphics",
            0x1A => "Separated mosaic graphics",
            0x1B => "Escape / switch character set",
            0x1C => "Black background",
            0x1D => "New background",
            0x1E => "Hold mosaics",
            0x1F => "Release mosaics",
            _ => "Control code",
        };
    }

    private void DrawSelection(DrawingContext context)
    {
        if (!IsActive || !_hasSelection || _selectionWidth <= 0 || _selectionHeight <= 0) return;
        if (_recoveryBrowseActive && _hideRecoverySelection) return;

        int minRow = Math.Min(_anchorRow, _dragRow);
        int minCol = Math.Min(_anchorCol, _dragCol);
        double drawX = minCol * CellWidth;
        double drawY = minRow * CellHeight;
        double drawW = _selectionWidth * CellWidth;
        double drawH = _selectionHeight * CellHeight;
        var rect = new Rect(drawX, drawY, drawW, drawH);

        IBrush fill = _readOnlyWarning
            ? WarningFillBrush
            : _recoveryBrowseActive ? RecoverySelectionFillBrush : SelFillBrush;
        Pen border = _readOnlyWarning
            ? WarningBorderPen
            : _recoveryBrowseActive ? RecoverySelectionBorderPen : SelBorderPen;
        context.FillRectangle(fill, rect);
        context.DrawRectangle(
            border,
            new Rect(drawX + 1, drawY + 1, drawW - 2, drawH - 2));

        if (string.IsNullOrEmpty(_selectionStatusText)) return;
        var status = new FormattedText(
            _selectionStatusText,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            HoverInfoTypeface,
            14,
            Brushes.White);
        double labelWidth = status.Width + 12;
        double labelHeight = status.Height + 6;
        double gridWidth = Columns * CellWidth;
        double gridHeight = Rows * CellHeight;
        const double gap = 4;
        Point? labelPosition = null;

        // Prefer the vertical sides because they remain easy to associate with a
        // wide selected block. Fall back to either horizontal side when the block
        // reaches the top/bottom edge. Never obscure the selected cells.
        if (drawY >= labelHeight + gap)
            labelPosition = new Point(
                Math.Clamp(drawX + (drawW - labelWidth) / 2, 0, gridWidth - labelWidth),
                drawY - labelHeight - gap);
        else if (drawY + drawH + gap + labelHeight <= gridHeight)
            labelPosition = new Point(
                Math.Clamp(drawX + (drawW - labelWidth) / 2, 0, gridWidth - labelWidth),
                drawY + drawH + gap);
        else if (drawX + drawW + gap + labelWidth <= gridWidth)
            labelPosition = new Point(
                drawX + drawW + gap,
                Math.Clamp(drawY + (drawH - labelHeight) / 2, 0, gridHeight - labelHeight));
        else if (drawX >= labelWidth + gap)
            labelPosition = new Point(
                drawX - labelWidth - gap,
                Math.Clamp(drawY + (drawH - labelHeight) / 2, 0, gridHeight - labelHeight));

        if (labelPosition is not { } position) return;
        var labelRect = new Rect(position, new Size(labelWidth, labelHeight));
        context.FillRectangle(
            new SolidColorBrush(Color.Parse("#CC163A20")),
            labelRect);
        context.DrawRectangle(RecoverySelectionBorderPen, labelRect);
        context.DrawText(status, new Point(position.X + 6, position.Y + 3));
    }

    private void DrawCellContent(DrawingContext context, Point origin, Cell cell)
    {
        if (!string.IsNullOrEmpty(cell.EnhancementText))
        {
            if (cell.EnhancementBaseCharacter != '\0')
            {
                DrawCharacterText(
                    context,
                    origin,
                    cell.EnhancementBaseCharacter.ToString(),
                    cell.Foreground);
                DrawTeletextDiacritical(
                    context,
                    origin,
                    cell.EnhancementBaseCharacter,
                    cell.EnhancementDiacritical,
                    cell.Foreground);
            }
            else
            {
                DrawCharacterText(context, origin, cell.EnhancementText, cell.Foreground);
            }
        }
        else if (cell.IsMosaic)
        {
            DrawSixel(context, origin, cell.MosaicPattern, ColorBrush(cell.Foreground), cell.MosaicSeparated);
        }
        else if (cell.Character != ' ')
        {
            DrawCharacterText(context, origin, cell.Character.ToString(), cell.Foreground);
        }
    }

    private void DrawCharacterText(
        DrawingContext context,
        Point origin,
        string displayText,
        TeletextColor foreground)
    {
        bool rotateTifaxSixIntoNine = _useTifaxNineWorkaround && displayText == "9";
        if (rotateTifaxSixIntoNine)
            displayText = "6";

        double fontSize = CellHeight * 0.85;
        var text = new FormattedText(
            displayText,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _gridTypeface,
            fontSize,
            ColorBrush(foreground));

        double offsetX = (CellWidth - text.Width) / 2.0;
        double offsetY = (CellHeight - text.Height) / 2.0 + 3;
        var textOrigin = new Point(origin.X + offsetX, origin.Y + offsetY);

        if (rotateTifaxSixIntoNine)
        {
            // The subsequent 180-degree transform reverses translation direction,
            // so +0.5 here moves the final rendered glyph 0.5 px left and up.
            textOrigin = new Point(textOrigin.X + 0.5, textOrigin.Y + 0.5);
            var cellCenter = new Point(
                origin.X + CellWidth / 2.0,
                origin.Y + CellHeight / 2.0);
            using (context.PushTransform(Matrix.CreateRotation(Math.PI, cellCenter)))
                context.DrawText(text, textOrigin);
        }
        else
        {
            context.DrawText(text, textOrigin);
        }
    }

    private static void DrawTeletextDiacritical(
        DrawingContext context,
        Point origin,
        char baseCharacter,
        int diacritical,
        TeletextColor foreground)
    {
        if (diacritical <= 0 || diacritical > 16) return;

        bool upperCase = char.IsUpper(baseCharacter);
        double centerX = origin.X + CellWidth / 2.0;
        double top = origin.Y + (upperCase ? -1.5 : 3.5);
        double bottom = top + 3.0;
        double below = origin.Y + CellHeight - 2.0;
        var pen = new Pen(ColorBrush(foreground), 1.5);

        void Line(double x1, double y1, double x2, double y2) =>
            context.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));

        switch (diacritical)
        {
            case 1: // Grave
                Line(centerX - 2.5, top, centerX + 1.5, bottom);
                break;
            case 2: // Acute
                Line(centerX - 1.5, bottom, centerX + 2.5, top);
                break;
            case 3: // Circumflex
                Line(centerX - 3.0, bottom, centerX, top);
                Line(centerX, top, centerX + 3.0, bottom);
                break;
            case 4: // Tilde
                Line(centerX - 3.5, bottom - 1.5, centerX - 1.5, top + 0.5);
                Line(centerX - 1.5, top + 0.5, centerX + 1.5, bottom - 0.5);
                Line(centerX + 1.5, bottom - 0.5, centerX + 3.5, top + 0.5);
                break;
            case 5: // Macron
                Line(centerX - 3.5, top + 1.5, centerX + 3.5, top + 1.5);
                break;
            case 6: // Breve
                Line(centerX - 3.0, top, centerX - 1.5, bottom);
                Line(centerX - 1.5, bottom, centerX + 1.5, bottom);
                Line(centerX + 1.5, bottom, centerX + 3.0, top);
                break;
            case 7: // Dot above
                context.DrawEllipse(
                    ColorBrush(foreground),
                    null,
                    new Point(centerX, top + 1.5),
                    1.25,
                    1.25);
                break;
            case 8: // Diaeresis
                context.DrawEllipse(
                    ColorBrush(foreground),
                    null,
                    new Point(centerX - 2.25, top + 1.5),
                    1.0,
                    1.0);
                context.DrawEllipse(
                    ColorBrush(foreground),
                    null,
                    new Point(centerX + 2.25, top + 1.5),
                    1.0,
                    1.0);
                break;
            case 9: // Dot below
                context.DrawEllipse(
                    ColorBrush(foreground),
                    null,
                    new Point(centerX, below),
                    1.25,
                    1.25);
                break;
            case 10: // Ring
                context.DrawEllipse(
                    null,
                    pen,
                    new Point(centerX, top + 1.5),
                    2.0,
                    2.0);
                break;
            case 11: // Cedilla
                Line(centerX, below - 1.0, centerX - 1.5, below + 1.5);
                Line(centerX - 1.5, below + 1.5, centerX + 1.0, below + 2.5);
                break;
            case 12: // Underscore
                Line(centerX - 4.0, below + 1.0, centerX + 4.0, below + 1.0);
                break;
            case 13: // Double acute
                Line(centerX - 3.5, bottom, centerX - 0.5, top);
                Line(centerX + 0.5, bottom, centerX + 3.5, top);
                break;
            case 14: // Ogonek
                Line(centerX + 1.5, below - 1.0, centerX, below + 1.5);
                Line(centerX, below + 1.5, centerX + 2.0, below + 2.5);
                break;
            case 15: // Caron
                double caronX = !upperCase && char.ToLowerInvariant(baseCharacter) == 'c'
                    ? centerX + 1.0
                    : centerX - (upperCase ? 1.0 : 0.5);
                double caronTop = top + (upperCase ? 1.0 : 0.0);
                double caronBottom = bottom + (upperCase ? 1.0 : 0.0);
                Line(caronX - 3.0, caronTop, caronX, caronBottom);
                Line(caronX, caronBottom, caronX + 3.0, caronTop);
                break;
            case 16: // Stroke for Latin G2 Đ/đ
                double strokeY = origin.Y + (upperCase ? 11.5 : 6.5);
                var strokePen = new Pen(ColorBrush(foreground), 2.0);
                if (upperCase)
                    context.DrawLine(
                        strokePen,
                        new Point(centerX - 5.5, strokeY),
                        new Point(centerX, strokeY));
                else
                    context.DrawLine(
                        strokePen,
                        new Point(centerX - 0.5, strokeY),
                        new Point(centerX + 6.5, strokeY));
                break;
        }
    }

    /// <summary>
    /// Draws a G1 mosaic sixel: a 2-wide x 3-tall block where each of the 6 sub-cells
    /// is independently on/off. Bit order (verified against handwiki.org's teletext
    /// character set table, see PageAssembler.DecodeRowInto for the cross-check):
    /// bit0=top-left, bit1=top-right, bit2=mid-left, bit3=mid-right,
    /// bit4=bottom-left, bit5=bottom-right.
    /// </summary>
    private static void DrawSixel(DrawingContext context, Point origin, byte pattern, IBrush brush, bool separated = false)
    {
        double halfW = CellWidth / 2;
        double thirdH = CellHeight / 3;
        double gap = separated ? 1.0 : 0.0;

        for (int bit = 0; bit < 6; bit++)
        {
            if ((pattern & (1 << bit)) == 0) continue;

            int col = bit % 2;       // 0=left, 1=right
            int rowIdx = bit / 2;    // 0=top, 1=mid, 2=bottom

            var rect = new Rect(
                origin.X + col * halfW + gap,
                origin.Y + rowIdx * thirdH + gap,
                halfW - gap * 2,
                thirdH - gap * 2);

            context.FillRectangle(brush, rect);
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Columns * CellWidth, Rows * CellHeight);

    private static IBrush ColorBrush(TeletextColor c) => c switch
    {
        TeletextColor.Black => Brushes.Black,
        TeletextColor.Red => Brushes.Red,
        TeletextColor.Green => Brushes.Lime,
        TeletextColor.Yellow => Brushes.Yellow,
        TeletextColor.Blue => Brushes.Blue,
        TeletextColor.Magenta => Brushes.Magenta,
        TeletextColor.Cyan => Brushes.Cyan,
        TeletextColor.White => Brushes.White,
        _ => Brushes.White,
    };

    private static IBrush InvertedColorBrush(TeletextColor background) => background switch
    {
        TeletextColor.Black => Brushes.White,
        TeletextColor.Red => Brushes.Cyan,
        TeletextColor.Green => Brushes.Magenta,
        TeletextColor.Yellow => Brushes.Blue,
        TeletextColor.Blue => Brushes.Yellow,
        TeletextColor.Magenta => Brushes.Lime,
        TeletextColor.Cyan => Brushes.Red,
        TeletextColor.White => Brushes.Black,
        _ => Brushes.White,
    };
}
