using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace QuickTranslate.Controls;

/// <summary>
/// Segmented-control item. Selection and hover layers are painted from code because
/// VisualState setters inside a WinUI template do not reliably revert on state exit.
/// </summary>
public sealed class SegmentButton : Button
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected),
        typeof(bool),
        typeof(SegmentButton),
        new PropertyMetadata(false, OnIsSelectedChanged));

    public static readonly DependencyProperty SelectionLayerVisibleProperty = DependencyProperty.Register(
        nameof(SelectionLayerVisible),
        typeof(bool),
        typeof(SegmentButton),
        new PropertyMetadata(true, OnSelectionLayerVisibleChanged));

    private Border? _hoverLayer;
    private Border? _checkLayer;
    private Border? _focusRing;
    private Brush? _hoverBrush;
    private Brush? _pressedBrush;
    private Brush? _selectedBrush;
    private bool _pointerOver;
    private bool _pressed;

    public SegmentButton()
    {
        PointerEntered += (_, _) =>
        {
            _pointerOver = true;
            UpdateLayers();
        };
        PointerExited += (_, _) =>
        {
            _pointerOver = false;
            _pressed = false;
            UpdateLayers();
        };
        PointerPressed += (_, _) =>
        {
            _pressed = true;
            UpdateLayers();
        };
        PointerReleased += (_, _) =>
        {
            _pressed = false;
            UpdateLayers();
        };
        PointerCaptureLost += (_, _) =>
        {
            _pressed = false;
            UpdateLayers();
        };
        GotFocus += (_, _) =>
        {
            if (_focusRing is not null)
            {
                // Only keyboard focus needs the ring; a ring after every click looks noisy.
                _focusRing.Opacity = FocusState == FocusState.Keyboard ? 1 : 0;
            }
        };
        LostFocus += (_, _) =>
        {
            if (_focusRing is not null)
            {
                _focusRing.Opacity = 0;
            }
        };
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>
    /// Allows a parent to own the selected background, as with the animated navigation pill.
    /// </summary>
    public bool SelectionLayerVisible
    {
        get => (bool)GetValue(SelectionLayerVisibleProperty);
        set => SetValue(SelectionLayerVisibleProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _hoverLayer = GetTemplateChild("HoverLayer") as Border;
        _checkLayer = GetTemplateChild("CheckLayer") as Border;
        _focusRing = GetTemplateChild("FocusRing") as Border;
        _hoverBrush = LookupBrush("QtSegmentHoverBrush");
        _pressedBrush = LookupBrush("QtSegmentPressedBrush");
        _selectedBrush = LookupBrush("QtSegmentSelectedBrush");
        UpdateLayers();
    }

    private static void OnIsSelectedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SegmentButton)sender).UpdateLayers();

    private static void OnSelectionLayerVisibleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SegmentButton)sender).UpdateLayers();

    private static Brush? LookupBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private void UpdateLayers()
    {
        if (_hoverLayer is not null)
        {
            _hoverLayer.Background = _pressed
                ? _pressedBrush
                : _pointerOver
                    ? _hoverBrush
                    : null;
        }

        if (_checkLayer is not null)
        {
            _checkLayer.Background = IsSelected && SelectionLayerVisible ? _selectedBrush : null;
        }
    }
}
