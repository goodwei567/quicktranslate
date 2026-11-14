using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QuickTranslate.Services;
using Windows.Storage.Streams;

namespace QuickTranslate.Controls;

/// <summary>Native Markdown with locally rendered math; the original text is kept for copying/history.</summary>
public sealed class MathMarkdownTextBlock : MarkdownTextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(MathMarkdownTextBlock),
        new PropertyMetadata(string.Empty, OnSourceTextChanged));
    private int _revision;
    private bool _loaded;
    private uint? _renderedColor;

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public MathMarkdownTextBlock()
    {
        Loaded += (_, _) =>
        {
            _loaded = true;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _revision++;
            Text = string.Empty;
            Config = new MarkdownConfig();
        };
        ActualThemeChanged += (_, _) => Refresh();
    }

    private static void OnSourceTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MathMarkdownTextBlock)sender).Refresh();

    private async void Refresh()
    {
        var revision = ++_revision;
        if (!_loaded)
        {
            return;
        }
        var source = SourceText ?? string.Empty;
        if (source.IndexOfAny(['$', '\\']) < 0)
        {
            if (Config?.ImageProvider is not null)
            {
                Config = new MarkdownConfig();
            }
            Text = source;
            return;
        }
        try
        {
            var parsed = await Task.Run(() => MathMarkdownProcessor.Process(source));
            if (!_loaded || revision != _revision)
            {
                return;
            }
            var brush = Foreground as SolidColorBrush;
            var color = brush?.Color ?? (ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
            var argb = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            var images = new Dictionary<string, FormulaImage>();
            var markdown = parsed.Markdown;
            var pixels = 0d;
            foreach (var (url, formula) in parsed.Formulas)
            {
                var rendered = pixels >= 2_000_000 ? null : await FormulaRenderer.RenderAsync(formula, FontSize, argb);
                if (!_loaded || revision != _revision)
                {
                    return;
                }
                // Bound decoded bitmap memory as well as the shared compressed cache.
                var imagePixels = rendered is null ? 0 : rendered.Width * rendered.Height * 4;
                if (rendered is null || pixels + imagePixels > 2_000_000)
                {
                    markdown = markdown.Replace(MathMarkdownProcessor.ImageMarkdown(url, formula),
                        MathMarkdownProcessor.LiteralMarkdown(formula));
                    continue;
                }
                pixels += imagePixels;
                images.Add(url, rendered);
            }
            if (_renderedColor != argb)
            {
                Text = string.Empty;
            }
            _renderedColor = argb;
            Config = new MarkdownConfig { ImageProvider = new FormulaImageProvider(this, parsed.Formulas, images) };
            Text = markdown;
        }
        catch (Exception)
        {
            if (_loaded && revision == _revision)
            {
                Text = source;
            }
        }
    }

    private sealed class FormulaImageProvider(
        MathMarkdownTextBlock owner, IReadOnlyDictionary<string, MathFormula> formulas,
        IReadOnlyDictionary<string, FormulaImage> images) : IImageProvider
    {
        public bool ShouldUseThisProvider(string url) => url.StartsWith(MathMarkdownProcessor.ImagePrefix, StringComparison.Ordinal);

        public async Task<Image> GetImage(string url)
        {
            var image = new Image { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            if (!formulas.TryGetValue(url, out var formula) || !images.TryGetValue(url, out var rendered))
            {
                return image;
            }
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(rendered.Png);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
            var available = owner.ActualWidth > 0 ? Math.Max(100, owner.ActualWidth - 32) : 420;
            var ratio = Math.Min(1, available / rendered.Width);
            image.Width = rendered.Width * ratio;
            image.Height = rendered.Height * ratio;
            if (!formula.IsDisplay)
            {
                image.Transform3D = new Microsoft.UI.Xaml.Media.Media3D.CompositeTransform3D { TranslateY = 3 };
            }
            AutomationProperties.SetName(image, formula.LaTeX);
            ToolTipService.SetToolTip(image, formula.LaTeX);
            return image;
        }
    }
}
