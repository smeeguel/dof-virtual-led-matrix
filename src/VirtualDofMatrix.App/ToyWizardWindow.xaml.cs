using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.Core;
using WpfMessageBox = System.Windows.MessageBox;

namespace VirtualDofMatrix.App;

// Overview: ToyWizardWindow is a simple guided editor for creating or updating a toy route with beginner-friendly labels.
public partial class ToyWizardWindow : Window
{
    private const int SafeMaxBulbs = CabinetXmlService.SafeMaxLedTotal;
    private const int SafeMaxStripBulbs = 1100;
    private const int PreviewLedCap = 512;
    private static readonly Regex NumberedNameRegex = new("^(Strip|Matrix)(\\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly IReadOnlyList<ColorPresetOption> BackgroundColorPresets =
    [
        new("Black", "#000000"),
        new("Charcoal", "#202020"),
        new("Slate", "#2F4F4F"),
        new("Silver", "#C0C0C0"),
        new("White", "#FFFFFF"),
        new("Crimson", "#DC143C"),
        new("Firebrick", "#B22222"),
        new("Orange Red", "#FF4500"),
        new("Orange", "#FFA500"),
        new("Gold", "#FFD700"),
        new("Khaki", "#F0E68C"),
        new("Lime Green", "#32CD32"),
        new("Forest Green", "#228B22"),
        new("Teal", "#008080"),
        new("Cyan", "#00FFFF"),
        new("Dodger Blue", "#1E90FF"),
        new("Royal Blue", "#4169E1"),
        new("Navy", "#000080"),
        new("Purple", "#800080"),
        new("Magenta", "#FF00FF"),
        new("Hot Pink", "#FF69B4"),
        new("Brown", "#8B4513"),
        new("Chocolate", "#D2691E"),
        new("Beige", "#F5F5DC"),
    ];

    private readonly IReadOnlyList<ToyRouteConfig> _existingToys;
    private readonly ToyRouteConfig? _editingToy;
    private readonly bool _isEdit;
    private readonly int _defaultStripBulbSize;
    private bool _suppressEvents;
    private string _lastSuggestedName = string.Empty;

    public ToyWizardWindow(IReadOnlyList<ToyRouteConfig> existingToys, ToyRouteConfig? editingToy = null, int defaultStripBulbSize = 32)
    {
        _existingToys = existingToys;
        _editingToy = editingToy;
        _isEdit = editingToy is not null;
        _defaultStripBulbSize = Math.Max(1, defaultStripBulbSize);

        InitializeComponent();
        InitializeForm();
    }

    public ToyRouteConfig? Result { get; private set; }

    private void InitializeForm()
    {
        _suppressEvents = true;

        Title = _isEdit ? "Edit Virtual Toy" : "Add Virtual Toy";
        TypeCombo.SelectedIndex = (_editingToy?.Kind.Equals("strip", StringComparison.OrdinalIgnoreCase) == true) ? 0 : 1;
        MatrixPresetCombo.SelectedIndex = 0;
        MappingCombo.SelectedIndex = 0;
        DotShapeCombo.SelectedIndex = 0;
        StripOrientationCombo.SelectedIndex = 0;
        WindowBackgroundColorCombo.ItemsSource = BackgroundColorPresets;

        if (_editingToy is null)
        {
            SetDefaultsForNewToy();
        }
        else
        {
            // Conversational note: this path intentionally pre-fills every toy-specific field so edits feel reversible and predictable.
            PopulateFromExistingToy(_editingToy);
        }

        _suppressEvents = false;
        UpdateTypePanels();
        UpdatePreviewAndValidation();
    }

    private void SetDefaultsForNewToy()
    {
        _lastSuggestedName = BuildSuggestedName(isStrip: false);
        NameTextBox.Text = _lastSuggestedName;

        MatrixWidthTextBox.Text = "32";
        MatrixHeightTextBox.Text = "8";
        StripLengthTextBox.Text = "256";

        MinDotSpacingTextBox.Text = "2";
        BrightnessTextBox.Text = "1.0";
        GammaTextBox.Text = "0.8";
        FillGapCheckBox.IsChecked = false;

        UseGlobalWindowCheckBox.IsChecked = true;
        WindowAlwaysOnTopCheckBox.IsChecked = true;
        WindowBorderlessCheckBox.IsChecked = true;
        WindowLockAspectCheckBox.IsChecked = true;
        WindowBackgroundVisibleCheckBox.IsChecked = true;
        SelectBackgroundPreset("#000000");

        BloomEnabledCheckBox.IsChecked = true;
        BloomThresholdTextBox.Text = "0.72";
        BloomSoftKneeTextBox.Text = "0.18";
        BloomNearRadiusTextBox.Text = "2";
        BloomFarRadiusTextBox.Text = "10";
        BloomNearStrengthTextBox.Text = "1.0";
        BloomFarStrengthTextBox.Text = "0.2";
    }

    private void PopulateFromExistingToy(ToyRouteConfig toy)
    {
        var isStrip = toy.Kind.Equals("strip", StringComparison.OrdinalIgnoreCase);
        NameTextBox.Text = string.IsNullOrWhiteSpace(toy.Name) ? toy.Id : toy.Name;

        MatrixWidthTextBox.Text = toy.Mapping.Width.ToString();
        MatrixHeightTextBox.Text = toy.Mapping.Height.ToString();
        StripLengthTextBox.Text = Math.Max(1, toy.Source.Length).ToString();

        SelectPresetIfKnown(toy.Mapping.Width, toy.Mapping.Height);
        SelectMapping(toy.Mapping.Mode);
        SelectDotShape(toy.Render.DotShape);

        MinDotSpacingTextBox.Text = toy.Render.MinDotSpacing.ToString();
        BrightnessTextBox.Text = toy.Render.Brightness.ToString("0.###");
        GammaTextBox.Text = toy.Render.Gamma.ToString("0.###");
        FillGapCheckBox.IsChecked = toy.Render.FillGapEnabled;

        UseGlobalWindowCheckBox.IsChecked = toy.Window.UseGlobalWindow;
        WindowAlwaysOnTopCheckBox.IsChecked = toy.Window.AlwaysOnTop;
        WindowBorderlessCheckBox.IsChecked = toy.Window.Borderless;
        WindowLockAspectCheckBox.IsChecked = toy.Window.LockAspectRatio;
        WindowBackgroundVisibleCheckBox.IsChecked = toy.Window.BackgroundVisible;
        SelectBackgroundPreset(string.IsNullOrWhiteSpace(toy.Window.BackgroundColor) ? "#000000" : toy.Window.BackgroundColor);

        BloomEnabledCheckBox.IsChecked = toy.Bloom.Enabled;
        BloomThresholdTextBox.Text = toy.Bloom.Threshold.ToString("0.###");
        BloomSoftKneeTextBox.Text = toy.Bloom.SoftKnee.ToString("0.###");
        BloomNearRadiusTextBox.Text = toy.Bloom.NearRadiusPx.ToString();
        BloomFarRadiusTextBox.Text = toy.Bloom.FarRadiusPx.ToString();
        BloomNearStrengthTextBox.Text = toy.Bloom.NearStrength.ToString("0.###");
        BloomFarStrengthTextBox.Text = toy.Bloom.FarStrength.ToString("0.###");

        if (isStrip)
        {
            TypeCombo.SelectedIndex = 0;
            // Conversational note: strip toys can be one row or one column; infer orientation from mapping and dimensions.
            StripOrientationCombo.SelectedIndex = toy.Mapping.Mode.Equals("ColumnMajor", StringComparison.OrdinalIgnoreCase)
                || (toy.Mapping.Width == 1 && toy.Mapping.Height > 1)
                ? 1
                : 0;
        }
    }

    private void SelectPresetIfKnown(int width, int height)
    {
        var preset = (width, height) switch
        {
            (32, 8) => "32 x 8",
            (64, 16) => "64 x 16",
            (128, 32) => "128 x 32",
            (128, 64) => "128 x 64",
            _ => "Custom",
        };

        foreach (ComboBoxItem item in MatrixPresetCombo.Items)
        {
            if (item.Content?.ToString() == preset)
            {
                MatrixPresetCombo.SelectedItem = item;
                CustomMatrixPanel.Visibility = preset == "Custom" ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
        }
    }

    private void SelectMapping(string mode)
    {
        foreach (ComboBoxItem item in MappingCombo.Items)
        {
            if (string.Equals(item.Content?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
            {
                MappingCombo.SelectedItem = item;
                return;
            }
        }
    }

    private void SelectDotShape(string dotShape)
    {
        foreach (ComboBoxItem item in DotShapeCombo.Items)
        {
            if (string.Equals(item.Content?.ToString(), dotShape, StringComparison.OrdinalIgnoreCase))
            {
                DotShapeCombo.SelectedItem = item;
                return;
            }
        }
    }

    private void OnInputsChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (ReferenceEquals(sender, TypeCombo) && !_isEdit && string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            _lastSuggestedName = BuildSuggestedName(IsStripTypeSelected());
            NameTextBox.Text = _lastSuggestedName;
        }
        else if (ReferenceEquals(sender, TypeCombo) && !_isEdit && NameTextBox.Text.Trim().Equals(_lastSuggestedName, StringComparison.OrdinalIgnoreCase))
        {
            _lastSuggestedName = BuildSuggestedName(IsStripTypeSelected());
            NameTextBox.Text = _lastSuggestedName;
        }

        if (ReferenceEquals(sender, TypeCombo) && !_isEdit)
        {
            ApplyTypeDefaultsForNewToy();
        }

        UpdateTypePanels();
        UpdatePreviewAndValidation();
    }

    private void OnMatrixPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        var preset = (MatrixPresetCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Custom";
        CustomMatrixPanel.Visibility = preset == "Custom" ? Visibility.Visible : Visibility.Collapsed;

        // Conversational note: presets write directly to the size fields so users always see exact numbers we validate against.
        switch (preset)
        {
            case "32 x 8":
                MatrixWidthTextBox.Text = "32";
                MatrixHeightTextBox.Text = "8";
                break;
            case "64 x 16":
                MatrixWidthTextBox.Text = "64";
                MatrixHeightTextBox.Text = "16";
                break;
            case "128 x 32":
                MatrixWidthTextBox.Text = "128";
                MatrixHeightTextBox.Text = "32";
                break;
            case "128 x 64":
                MatrixWidthTextBox.Text = "128";
                MatrixHeightTextBox.Text = "64";
                break;
        }

        UpdatePreviewAndValidation();
    }

    private void UpdateTypePanels()
    {
        var isStrip = IsStripTypeSelected();
        StripDimensionsPanel.Visibility = isStrip ? Visibility.Visible : Visibility.Collapsed;
        MatrixDimensionsPanel.Visibility = isStrip ? Visibility.Collapsed : Visibility.Visible;
        PreviewStatusText.Text = isStrip
            ? "Preview shows strip LED order left to right."
            : "Preview shows matrix numbering using your chosen width/height.";
    }

    private bool IsStripTypeSelected() => (TypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Single strip";
    private bool IsVerticalStripSelected() => (StripOrientationCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Vertical";

    private void UpdatePreviewAndValidation()
    {
        var validation = ValidateInputs();
        ValidationText.Text = validation.ErrorMessage;

        if (!validation.IsValid)
        {
            PreviewGrid.Children.Clear();
            return;
        }

        var width = validation.Width;
        var height = validation.Height;
        var total = validation.Total;
        var previewCount = Math.Min(total, PreviewLedCap);

        // Conversational note: we keep preview bounded so the settings UI stays responsive on very large toy sizes.
        PreviewGrid.Children.Clear();
        if (IsStripTypeSelected())
        {
            // Conversational note: strip previews stay one-dimensional by design (single row or single column).
            if (IsVerticalStripSelected())
            {
                PreviewGrid.Columns = 1;
                PreviewGrid.Rows = previewCount;
            }
            else
            {
                PreviewGrid.Columns = previewCount;
                PreviewGrid.Rows = 1;
            }
        }
        else
        {
            PreviewGrid.Columns = width;
            PreviewGrid.Rows = height;
        }
        for (var ledIndex = 1; ledIndex <= previewCount; ledIndex++)
        {
            var cell = new Border
            {
                Margin = new Thickness(2),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 85, 85)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 17, 17)),
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(3),
                Child = new TextBlock
                {
                    Text = ledIndex.ToString(),
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(237, 237, 237)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    FontSize = 11,
                },
            };
            PreviewGrid.Children.Add(cell);
        }

        var suffix = total > PreviewLedCap ? $" (showing first {PreviewLedCap})" : string.Empty;
        PreviewStatusText.Text = $"{total} LEDs total{suffix}.";
    }

    private (bool IsValid, string ErrorMessage, int Width, int Height, int Total) ValidateInputs()
    {
        var name = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return (false, "Enter a name before saving.", 0, 0, 0);
        }

        var duplicate = _existingToys.Any(toy =>
            !toy.Id.Equals(_editingToy?.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(GetVisibleName(toy), name, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            return (false, "That name is already in use. Choose a different name.", 0, 0, 0);
        }

        if (!TryGetDimensions(out var width, out var height, out var total, out var dimensionError))
        {
            return (false, dimensionError, 0, 0, 0);
        }

        if (!TryParseInt(MinDotSpacingTextBox.Text, out var spacing) || spacing < 0)
        {
            return (false, "Dot spacing must be a whole number (0 or higher).", 0, 0, 0);
        }

        if (!TryParseDouble(BrightnessTextBox.Text, out var brightness) || brightness < 0 || brightness > 1)
        {
            return (false, "Brightness must be between 0.0 and 1.0.", 0, 0, 0);
        }

        if (!TryParseDouble(GammaTextBox.Text, out var gamma) || gamma <= 0)
        {
            return (false, "Gamma must be a positive number.", 0, 0, 0);
        }

        if (!TryParseDouble(BloomThresholdTextBox.Text, out var bloomThreshold) || bloomThreshold < 0 || bloomThreshold > 1)
        {
            return (false, "Glow threshold must be between 0.0 and 1.0.", 0, 0, 0);
        }

        if (!TryParseDouble(BloomSoftKneeTextBox.Text, out var bloomSoftKnee) || bloomSoftKnee < 0)
        {
            return (false, "Glow soft knee must be 0 or higher.", 0, 0, 0);
        }

        if (!TryParseInt(BloomNearRadiusTextBox.Text, out var nearRadius) || nearRadius < 0)
        {
            return (false, "Near radius must be 0 or higher.", 0, 0, 0);
        }

        if (!TryParseInt(BloomFarRadiusTextBox.Text, out var farRadius) || farRadius < 0)
        {
            return (false, "Far radius must be 0 or higher.", 0, 0, 0);
        }

        if (!TryParseDouble(BloomNearStrengthTextBox.Text, out var nearStrength) || nearStrength < 0)
        {
            return (false, "Near strength must be 0 or higher.", 0, 0, 0);
        }

        if (!TryParseDouble(BloomFarStrengthTextBox.Text, out var farStrength) || farStrength < 0)
        {
            return (false, "Far strength must be 0 or higher.", 0, 0, 0);
        }

        return (true, string.Empty, width, height, total);
    }

    private bool TryGetDimensions(out int width, out int height, out int total, out string error)
    {
        width = 0;
        height = 0;
        total = 0;
        error = string.Empty;

        if (IsStripTypeSelected())
        {
            if (!TryParseInt(StripLengthTextBox.Text, out var stripLength) || stripLength <= 0)
            {
                error = "Bulb count must be a positive whole number.";
                return false;
            }

            if (stripLength > SafeMaxStripBulbs)
            {
                error = $"Single strip toys support up to {SafeMaxStripBulbs} bulbs.";
                return false;
            }

            if (IsVerticalStripSelected())
            {
                width = 1;
                height = stripLength;
            }
            else
            {
                width = stripLength;
                height = 1;
            }
            total = stripLength;
            return true;
        }

        if (!TryParseInt(MatrixWidthTextBox.Text, out width) || !TryParseInt(MatrixHeightTextBox.Text, out height) || width <= 0 || height <= 0)
        {
            error = "Columns and rows must be positive whole numbers.";
            return false;
        }

        total = width * height;
        if (total > SafeMaxBulbs)
        {
            error = $"Total bulbs must stay at or below {SafeMaxBulbs}.";
            return false;
        }

        return true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var validation = ValidateInputs();
        if (!validation.IsValid)
        {
            WpfMessageBox.Show(this, validation.ErrorMessage, "Check your toy settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = NameTextBox.Text.Trim();
        var id = _editingToy?.Id ?? BuildUniqueRouteId(name);
        var mapping = IsStripTypeSelected()
            ? (IsVerticalStripSelected() ? "ColumnMajor" : "RowMajor")
            : ((MappingCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "TopDownAlternateRightLeft");

        Result = new ToyRouteConfig
        {
            Id = id,
            Name = name,
            Enabled = _editingToy?.Enabled ?? true,
            Kind = IsStripTypeSelected() ? "strip" : "matrix",
            Source = new ToySourceConfig
            {
                CanonicalStart = _editingToy?.Source.CanonicalStart ?? 0,
                Length = validation.Total,
                StripIndex = _editingToy?.Source.StripIndex,
                StripOffset = _editingToy?.Source.StripOffset,
            },
            Mapping = new ToyMappingConfig
            {
                Width = validation.Width,
                Height = validation.Height,
                Mode = mapping,
            },
            Window = new ToyWindowOptionsConfig
            {
                UseGlobalWindow = UseGlobalWindowCheckBox.IsChecked == true,
                AlwaysOnTop = WindowAlwaysOnTopCheckBox.IsChecked == true,
                Borderless = WindowBorderlessCheckBox.IsChecked == true,
                LockAspectRatio = WindowLockAspectCheckBox.IsChecked == true,
                BackgroundVisible = WindowBackgroundVisibleCheckBox.IsChecked == true,
                BackgroundColor = GetSelectedBackgroundColorHex(),
                Left = _editingToy?.Window.Left,
                Top = _editingToy?.Window.Top,
                Width = ResolveWindowWidth(validation.Total),
                Height = ResolveWindowHeight(validation.Total),
            },
            Render = new ToyRenderOptionsConfig
            {
                DotShape = (DotShapeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "circle",
                MinDotSpacing = int.Parse(MinDotSpacingTextBox.Text),
                FillGapEnabled = FillGapCheckBox.IsChecked == true,
                Brightness = double.Parse(BrightnessTextBox.Text),
                Gamma = double.Parse(GammaTextBox.Text),
            },
            Bloom = new ToyBloomOptionsConfig
            {
                Enabled = BloomEnabledCheckBox.IsChecked == true,
                Threshold = double.Parse(BloomThresholdTextBox.Text),
                SoftKnee = double.Parse(BloomSoftKneeTextBox.Text),
                NearRadiusPx = int.Parse(BloomNearRadiusTextBox.Text),
                FarRadiusPx = int.Parse(BloomFarRadiusTextBox.Text),
                NearStrength = double.Parse(BloomNearStrengthTextBox.Text),
                FarStrength = double.Parse(BloomFarStrengthTextBox.Text),
            },
            OutputTargets = BuildOutputTargets(),
        };

        DialogResult = true;
        Close();
    }

    private static bool TryParseInt(string raw, out int value) => int.TryParse(raw?.Trim(), out value);

    private static bool TryParseDouble(string raw, out double value) => double.TryParse(raw?.Trim(), out value);

    private void SelectBackgroundPreset(string hex)
    {
        var selected = BackgroundColorPresets.FirstOrDefault(preset => preset.Hex.Equals(hex, StringComparison.OrdinalIgnoreCase))
            ?? BackgroundColorPresets[0];
        WindowBackgroundColorCombo.SelectedItem = selected;
    }

    private string GetSelectedBackgroundColorHex()
    {
        return (WindowBackgroundColorCombo.SelectedItem as ColorPresetOption)?.Hex ?? "#000000";
    }

    private double? ResolveWindowWidth(int stripLength)
    {
        if (_editingToy?.Window.Width is not null)
        {
            return _editingToy.Window.Width;
        }

        if (!IsStripTypeSelected())
        {
            return _editingToy?.Window.Width;
        }

        var spacing = Math.Max(0, int.Parse(MinDotSpacingTextBox.Text));
        return IsVerticalStripSelected()
            ? _defaultStripBulbSize
            : (_defaultStripBulbSize + spacing) * stripLength;
    }

    private double? ResolveWindowHeight(int stripLength)
    {
        if (_editingToy?.Window.Height is not null)
        {
            return _editingToy.Window.Height;
        }

        if (!IsStripTypeSelected())
        {
            return _editingToy?.Window.Height;
        }

        var spacing = Math.Max(0, int.Parse(MinDotSpacingTextBox.Text));
        return IsVerticalStripSelected()
            ? (_defaultStripBulbSize + spacing) * stripLength
            : _defaultStripBulbSize;
    }

    private string BuildSuggestedName(bool isStrip)
    {
        var prefix = isStrip ? "Strip" : "Matrix";
        var highest = 0;

        foreach (var toy in _existingToys)
        {
            var match = NumberedNameRegex.Match(GetVisibleName(toy));
            if (!match.Success || !match.Groups[1].Value.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(match.Groups[2].Value, out var n))
            {
                highest = Math.Max(highest, n);
            }
        }

        return $"{prefix}{highest + 1}";
    }

    private static string GetVisibleName(ToyRouteConfig toy) => string.IsNullOrWhiteSpace(toy.Name) ? toy.Id : toy.Name;

    private string BuildUniqueRouteId(string name)
    {
        var normalized = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "toy";
        }

        var candidate = normalized;
        var index = 2;
        while (_existingToys.Any(toy => toy.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{normalized}-{index}";
            index++;
        }

        return candidate;
    }

    private List<ToyAdapterTargetConfig> BuildOutputTargets()
    {
        if (_editingToy is not null && _editingToy.OutputTargets.Count > 0)
        {
            return _editingToy.OutputTargets
                .Select(target => new ToyAdapterTargetConfig
                {
                    Adapter = target.Adapter,
                    Enabled = target.Enabled,
                    Options = new Dictionary<string, string>(target.Options, StringComparer.OrdinalIgnoreCase),
                })
                .ToList();
        }

        return
        [
            new ToyAdapterTargetConfig
            {
                Adapter = "viewer",
                Enabled = true,
            },
        ];
    }

    private void ApplyTypeDefaultsForNewToy()
    {
        // Conversational note: strip defaults bias toward overlay workflows (transparent background + free resize axis).
        if (IsStripTypeSelected())
        {
            // Conversational note: strips are typically rendered as a single axis, so default to viewport-filling spacing.
            FillGapCheckBox.IsChecked = true;
            WindowLockAspectCheckBox.IsChecked = false;
            WindowBackgroundVisibleCheckBox.IsChecked = false;
        }
        else
        {
            FillGapCheckBox.IsChecked = false;
            WindowLockAspectCheckBox.IsChecked = true;
            WindowBackgroundVisibleCheckBox.IsChecked = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class ColorPresetOption
    {
        public ColorPresetOption(string name, string hex)
        {
            Name = name;
            Hex = hex;
            Label = $"{name} ({hex})";
            SwatchBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        }

        public string Name { get; }

        public string Hex { get; }

        public string Label { get; }

        public SolidColorBrush SwatchBrush { get; }
    }
}
