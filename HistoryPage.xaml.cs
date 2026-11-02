using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using QuickTranslate.Models;
using Windows.ApplicationModel.DataTransfer;

namespace QuickTranslate;

public sealed partial class HistoryPage : Page
{
    private IReadOnlyList<TranslationHistoryEntry> _allEntries = [];
    private bool _initialized;

    public HistoryPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _initialized = true;
        App.Current.History.Changed -= History_Changed;
        App.Current.History.Changed += History_Changed;
        RefreshList();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _initialized = false;
        App.Current.History.Changed -= History_Changed;
    }

    private void History_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshList);

    private void RefreshList()
    {
        _allEntries = App.Current.History.GetEntries();
        var query = SearchBox.Text.Trim();
        var favoritesOnly = FavoritesOnlyToggle.IsSelected;
        var filtered = _allEntries
            .Where(entry => !favoritesOnly || entry.IsFavorite)
            .Where(entry => query.Length == 0 ||
                entry.SourceText.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                entry.ResultText.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                entry.ModelId.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();

        HistoryList.ItemsSource = filtered;
        HistorySubtitleTextBlock.Text = filtered.Length == _allEntries.Count
            ? $"共 {_allEntries.Count} 条记录，其中 {_allEntries.Count(entry => entry.IsFavorite)} 条收藏。记录仅保存在本机。"
            : $"显示 {filtered.Length} 条，共 {_allEntries.Count} 条记录。";
        EmptyPanel.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHintTextBlock.Text =
            _allEntries.Count == 0
                ? "开启“保存翻译历史”后，完成的划词和手动处理会出现在这里。"
                : favoritesOnly
                    ? "还没有收藏记录。点击卡片上的星标即可收藏。"
                    : "换个关键词试试，或关闭“只看收藏”。";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            RefreshList();
        }
    }

    private void FavoritesOnlyToggle_Click(object sender, RoutedEventArgs e)
    {
        FavoritesOnlyToggle.IsSelected = !FavoritesOnlyToggle.IsSelected;
        if (_initialized)
        {
            RefreshList();
        }
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } ||
            FindEntry(id) is not { } entry)
        {
            return;
        }

        try
        {
            App.Current.History.SetFavorite(id, !entry.IsFavorite);
            ShowStatus(entry.IsFavorite ? "已取消收藏" : "已加入收藏", string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus("无法更新收藏", ex.Message, isError: true);
        }
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        try
        {
            App.Current.History.Delete(id);
            ShowStatus("记录已删除", string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus("无法删除记录", ex.Message, isError: true);
        }
    }

    private void CopyEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || FindEntry(id) is not { } entry)
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(entry.ResultText);
            Clipboard.SetContent(package);
            ShowStatus("已复制结果", string.Empty);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            ShowStatus("剪贴板暂不可用", ex.Message, isError: true);
        }
    }

    private async void ViewEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || FindEntry(id) is not { } entry)
        {
            return;
        }

        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(new TextBlock
        {
            Text = "原文",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        content.Children.Add(new TextBlock
        {
            Text = entry.SourceText,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        content.Children.Add(new TextBlock
        {
            Text = "结果",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        content.Children.Add(new MarkdownTextBlock
        {
            Text = entry.ResultText,
            IsTextSelectionEnabled = true,
            UsePipeTables = true,
            UseTaskLists = true,
            UseAutoLinks = true,
            DisableHtml = true
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{entry.TaskLabel} · {entry.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}",
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = "关闭"
        };
        await dialog.ShowAsync();
    }

    private async void ClearAllHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空全部历史？",
            Content = "收藏记录也会一并删除，此操作无法撤销。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            App.Current.History.Clear();
            ShowStatus("历史记录已清空", string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus("无法清空历史", ex.Message, isError: true);
        }
    }

    private TranslationHistoryEntry? FindEntry(string id) =>
        _allEntries.FirstOrDefault(entry => entry.Id == id);

    private void ShowStatus(string title, string message, bool isError = false)
    {
        HistoryStatusBar.Title = title;
        HistoryStatusBar.Message = message;
        HistoryStatusBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        HistoryStatusBar.IsOpen = true;
    }
}
