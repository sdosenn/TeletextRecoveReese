using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TeletextRecoveReese.Core;

namespace TeletextRecoveReese;

public partial class MainWindow
{
    private sealed class ManagedSquashSubpage
    {
        public required (int magazine, int page, int subpage) Address { get; init; }
        public required IReadOnlyList<PageInstance> Copies { get; init; }
        public bool Deleted { get; set; }
    }

    private sealed class ManagedSquashPage
    {
        public required int OriginalNumber { get; init; }
        public int Number { get; set; }
        public required List<ManagedSquashSubpage> Subpages { get; init; }
        public override string ToString()
        {
            int count = Subpages.Count(sub => !sub.Deleted);
            return $"{Number:X3} · {count} {(count == 1 ? "subpage" : "subpages") }"
                + (Number == OriginalNumber ? string.Empty : $" (was {OriginalNumber:X3})");
        }
    }

    private async void OnManageSquashedPagesClicked(object? sender, RoutedEventArgs e) =>
        await ShowSquashedPageManagerAsync();

    private async void OnNativeManageSquashedPagesClicked(object? sender, EventArgs e) =>
        await ShowSquashedPageManagerAsync();

    private bool CanManageSquashedPages() =>
        _squashFileOpen && _squashStore.GetKnownPages().Take(2).Count() == 2;

    private async Task ShowSquashedPageManagerAsync()
    {
        if (!CanManageSquashedPages()) return;
        List<ManagedSquashPage> CreateEntries() => _squashStore.GetKnownPages().Select(page => new ManagedSquashPage
        {
            OriginalNumber = page.magazine << 8 | page.page,
            Number = page.magazine << 8 | page.page,
            Subpages = _squashStore.GetKnownSubpages(page.magazine, page.page).Select(subpage => new ManagedSquashSubpage
            {
                Address = (page.magazine, page.page, subpage),
                Copies = _squashStore.GetInstances(page.magazine, page.page, subpage),
            }).ToList(),
        }).ToList();
        var entries = CreateEntries();
        var search = new TextBox { PlaceholderText = "Find page…" };
        var pages = new ListBox { SelectionMode = SelectionMode.Multiple };
        var subpages = new ListBox { SelectionMode = SelectionMode.Multiple };
        var heading = new TextBlock { FontSize = 18, FontWeight = FontWeight.SemiBold };
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray };
        var number = new TextBox { Width = 85, MaxLength = 3, PlaceholderText = "123" };
        var renumber = new Button { Content = "Renumber", IsEnabled = false };
        var removePages = new Button { Content = "Remove selected pages", HorizontalAlignment = HorizontalAlignment.Stretch };
        var removeSubpages = new Button { Content = "Remove selected subpages" };
        var selectAllPages = new Button { Content = "Select all", HorizontalAlignment = HorizontalAlignment.Stretch };
        var selectAllSubpages = new Button { Content = "Select all subpages" };
        var reset = new Button { Content = "Reset changes" };
        var apply = new Button { Content = "Apply", MinWidth = 90, IsEnabled = false };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        ManagedSquashPage? focused = null;
        bool refreshing = false;
        var dialog = new Window
        {
            Title = "Manage squashed pages", Width = 1020, Height = 740,
            MinWidth = 860, MinHeight = 540, CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        subpages.ItemTemplate = new FuncDataTemplate<ManagedSquashSubpage>((sub, _) =>
        {
            if (sub is null) return new TextBlock();
            var preview = new TeletextGridControl { IsHitTestVisible = false, SuppressFlash = true };
            preview.SetFontFamily(
                App.GridFontFamily ?? FontFamily.Default,
                App.GridFontFamilyName,
                App.GridFontWeight,
                App.GridFontSizeScale,
                App.GridFontThickness);
            void Display(int index)
            {
                var page = sub.Copies[Math.Clamp(index, 0, sub.Copies.Count - 1)].Page;
                ApplyFileG0SubsetToPage(page);
                preview.Page = page;
            }
            Display(0);
            var copies = new ComboBox
            {
                ItemsSource = Enumerable.Range(0, sub.Copies.Count).Select(i => $"Copy {i + 1}").ToArray(),
                SelectedIndex = 0, IsVisible = sub.Copies.Count > 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            copies.SelectionChanged += (_, _) => Display(copies.SelectedIndex);
            var details = new StackPanel
            {
                Spacing = 8, Margin = new Thickness(12, 8),
                Children =
                {
                    new TextBlock { Text = $"Subpage {sub.Address.subpage:X4}", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{sub.Copies.Count} {(sub.Copies.Count == 1 ? "copy" : "copies")}" },
                    copies,
                },
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*"), Margin = new Thickness(4) };
            row.Children.Add(new Viewbox { Child = preview, Width = 310, Height = 200, Stretch = Stretch.Uniform });
            Grid.SetColumn(details, 1);
            row.Children.Add(details);
            return row;
        });

        bool TryNumber(out int value)
        {
            string text = number.Text?.Trim() ?? string.Empty;
            return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                   && text.Length == 3 && value is >= 0x100 and <= 0x8FF;
        }
        void UpdateActions()
        {
            int removed = entries.Sum(page => page.Subpages.Count(sub => sub.Deleted));
            int renamed = entries.Count(page => page.Number != page.OriginalNumber && page.Subpages.Any(sub => !sub.Deleted));
            apply.IsEnabled = removed > 0 || renamed > 0;
            reset.IsEnabled = apply.IsEnabled;
            notice.Text = apply.IsEnabled
                ? $"Pending: remove {removed} subpages (all copies), renumber {renamed} pages. Apply to update the document."
                : "Ctrl/Shift selects multiple pages or subpages. Changes stay pending until Apply.";
            removePages.IsEnabled = pages.SelectedItems?.Count > 0;
            removeSubpages.IsEnabled = subpages.SelectedItems?.Count > 0;
            renumber.IsEnabled = pages.SelectedItems?.Count == 1 && focused is not null
                && TryNumber(out int target) && target != focused.Number
                && !entries.Any(page => page != focused && page.Number == target && page.Subpages.Any(sub => !sub.Deleted));
        }
        void ShowSubpages(ManagedSquashPage? page)
        {
            focused = page;
            heading.Text = page is null ? "Subpages" : $"Page {page.Number:X3} — all subpages";
            subpages.ItemsSource = page?.Subpages.Where(sub => !sub.Deleted).ToList();
            number.Text = page?.Number.ToString("X3") ?? string.Empty;
            UpdateActions();
        }
        void Refresh(ManagedSquashPage? preferred = null)
        {
            refreshing = true;
            string filter = search.Text?.Trim() ?? string.Empty;
            var visible = entries.Where(page => page.Subpages.Any(sub => !sub.Deleted)
                && (page.Number.ToString("X3").Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || page.OriginalNumber.ToString("X3").Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(page => page.Number).ToList();
            pages.ItemsSource = visible;
            pages.SelectedItem = preferred is not null && visible.Contains(preferred) ? preferred : visible.FirstOrDefault();
            refreshing = false;
            ShowSubpages(pages.SelectedItem as ManagedSquashPage);
        }
        pages.SelectionChanged += (_, e) =>
        {
            if (refreshing) return;
            ShowSubpages(e.AddedItems.OfType<ManagedSquashPage>().LastOrDefault()
                ?? pages.SelectedItems?.OfType<ManagedSquashPage>().LastOrDefault());
        };
        subpages.SelectionChanged += (_, _) => UpdateActions();
        search.TextChanged += (_, _) => Refresh(focused);
        number.TextChanged += (_, _) => UpdateActions();
        renumber.Click += (_, _) =>
        {
            if (!renumber.IsEnabled || focused is null || !TryNumber(out int target)) return;
            focused.Number = target;
            Refresh(focused);
        };
        removePages.Click += (_, _) =>
        {
            foreach (var page in pages.SelectedItems?.OfType<ManagedSquashPage>().ToArray() ?? [])
                foreach (var sub in page.Subpages) sub.Deleted = true;
            Refresh();
        };
        removeSubpages.Click += (_, _) =>
        {
            foreach (var sub in subpages.SelectedItems?.OfType<ManagedSquashSubpage>().ToArray() ?? []) sub.Deleted = true;
            Refresh(focused);
        };
        selectAllPages.Click += (_, _) => pages.SelectAll();
        selectAllSubpages.Click += (_, _) => subpages.SelectAll();
        reset.Click += (_, _) => { entries = CreateEntries(); Refresh(); };
        cancel.Click += (_, _) => dialog.Close();
        apply.Click += (_, _) =>
        {
            ApplySquashedPageManagement(entries);
            dialog.Close();
        };

        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8 };
        left.Children.Add(search);
        Grid.SetRow(pages, 1); left.Children.Add(pages);
        Grid.SetRow(selectAllPages, 2); left.Children.Add(selectAllPages);
        Grid.SetRow(removePages, 3); left.Children.Add(removePages);
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        right.Children.Add(heading);
        Grid.SetRow(subpages, 1); right.Children.Add(subpages);
        var subActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { selectAllSubpages, removeSubpages } };
        Grid.SetRow(subActions, 2); right.Children.Add(subActions);
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*"), ColumnSpacing = 16, Children = { left, right } };
        Grid.SetColumn(right, 1);
        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 12),
            Children = { new TextBlock { Text = "New page number", VerticalAlignment = VerticalAlignment.Center }, number, renumber },
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(notice);
        Grid.SetColumn(reset, 1); footer.Children.Add(reset);
        Grid.SetColumn(cancel, 2); footer.Children.Add(cancel);
        Grid.SetColumn(apply, 3); footer.Children.Add(apply);
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children = { top, columns, footer } };
        Grid.SetRow(columns, 1); Grid.SetRow(footer, 2);
        dialog.Content = layout;
        Refresh(entries.FirstOrDefault(page => TryGetSquashAddress(out var current)
            && page.OriginalNumber == (current.magazine << 8 | current.page)));
        await dialog.ShowDialog(this);
    }

    private void ApplySquashedPageManagement(IReadOnlyList<ManagedSquashPage> entries)
    {
        TryGetSquashAddress(out var current);
        var selected = entries.FirstOrDefault(page => page.OriginalNumber == (current.magazine << 8 | current.page));
        var target = selected is null ? current : (selected.Number >> 8, selected.Number & 255, current.subpage);
        // Prepare all moved copies before changing the document.
        var additions = entries.Where(page => page.Number != page.OriginalNumber)
            .SelectMany(page => page.Subpages.Where(sub => !sub.Deleted).SelectMany(sub => sub.Copies.Select(copy =>
                new PageInstance
                {
                    Magazine = page.Number >> 8, PageNumber = page.Number & 255, Subpage = sub.Address.subpage,
                    Page = SquashPageOperations.Renumber(copy.Page, page.Number >> 8, page.Number & 255),
                }))).ToList();
        foreach (var page in entries)
        foreach (var sub in page.Subpages.Where(sub => sub.Deleted || page.Number != page.OriginalNumber))
        {
            foreach (var instance in _squashStore.RemoveAddress(sub.Address.magazine, sub.Address.page, sub.Address.subpage))
            {
                MarkSourcePacketsDeleted(instance.Page);
                _pageHistories.Remove(instance.Page);
            }
        }
        foreach (var instance in additions)
        {
            for (int row = 1; row <= 24; row++)
                if (instance.Page.RawRows[row] is not null) instance.RowsReceived.Add(row);
            _squashStore.AddInstance(instance);
            EnsurePageHistory(instance.Page);
        }
        _blockBrowseAddress = null;
        _flashRollAddress = null;
        _structuralDirty = true;
        if (_squashStore.TotalInstanceCount == 0) AddBlankSquashPage(1, 0, 0);
        PopulateSquashPageCombo();
        SelectSquashAddress(_squashStore.GetInstances(target.Item1, target.Item2, target.Item3).Count > 0
            ? target : _squashStore.GetKnownAddresses().First());
        UpdateDirtyFromHistories();
        UpdateSquashFileFooter();
        UpdateUndoToolbar();
        UpdateNavigationButtons();
    }
}
