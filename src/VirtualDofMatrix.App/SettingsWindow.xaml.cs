using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.Core;
using WpfMessageBox = System.Windows.MessageBox;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace VirtualDofMatrix.App;

// Overview: SettingsWindow is a working-copy editor that applies runtime-safe changes without mutating the live config until confirmed.
public partial class SettingsWindow : Window
{
    private const string CustomResolution = "Custom";
    private static readonly JsonSerializerOptions FingerprintSerializerOptions = new(JsonSerializerDefaults.Web);

    private AppConfig _working;
    private readonly CabinetXmlService _cabinetXmlService;
    private string _lastAppliedFingerprint = string.Empty;
    private IReadOnlyList<VirtualToyListItem> _virtualToys = [];
    private IReadOnlyList<VirtualToyListItem> _hardwareToys = [];
    private readonly string _activeScopeLabel;
    private readonly bool _isTableScoped;

    public SettingsWindow(AppConfig source, CabinetXmlService cabinetXmlService, string? activeTableOrRomName = null)
    {
        _working = Clone(source);
        _cabinetXmlService = cabinetXmlService;
        _isTableScoped = !string.IsNullOrWhiteSpace(activeTableOrRomName);
        _activeScopeLabel = _isTableScoped ? $"Table/ROM: {activeTableOrRomName}" : "Global";

        InitializeComponent();
        PopulateControls();
        LoadToyCollections();
        SettingsTabs.SelectedIndex = 0;
        CaptureCurrentAsCleanState();
        UpdateSummary();
    }

    public AppConfig? Result { get; private set; }

    private void PopulateControls()
    {
        // Keep choice lists centralized so we don't drift between XAML defaults and runtime options.
        ResolutionPresetCombo.ItemsSource = new[] { "SD (32x8)", "HD (64x16)", "Ultra (128x32)", CustomResolution };
        QualityCombo.ItemsSource = VisualQualityProfiles.Names;
        DotShapeCombo.ItemsSource = new[] { "circle", "square" };
        RendererCombo.ItemsSource = new[] { "gpu", "cpu" };

        WidthTextBox.Text = _working.Matrix.Width.ToString();
        HeightTextBox.Text = _working.Matrix.Height.ToString();

        ResolutionPresetCombo.SelectedItem = DetectResolutionPreset(_working.Matrix.Width, _working.Matrix.Height);
        CustomResolutionPanel.Visibility = Equals(ResolutionPresetCombo.SelectedItem, CustomResolution) ? Visibility.Visible : Visibility.Collapsed;

        AlwaysOnTopCheckBox.IsChecked = _working.Window.AlwaysOnTop;
        DotShapeCombo.SelectedItem = _working.Matrix.DotShape;
        DotBrightnessSlider.Value = NormalizeDotBrightness(_working.Matrix.Brightness);
        DotBrightnessValueText.Text = $"{DotBrightnessSlider.Value:0.00}";
        RendererCombo.SelectedItem = NormalizeRenderer(_working.Matrix.Renderer);

        if (!VisualQualityProfiles.Names.Contains(_working.Settings.VisualQuality, StringComparer.OrdinalIgnoreCase))
        {
            _working.Settings.VisualQuality = VisualQualityProfiles.High;
        }

        QualityCombo.SelectedItem = _working.Settings.VisualQuality;

        CabinetPathTextBox.Text = _working.Settings.CabinetXmlPath;
        AutoUpdateCabinetCheckBox.IsChecked = _working.Settings.AutoUpdateCabinetOnResolutionChange;
        DebugCheckBox.IsChecked = _working.Debug.LogProtocol;
        LogFramesCheckBox.IsChecked = _working.Debug.LogFrames;
        UpdateAdvancedControlState();

        RefreshLedStripList(_working.Settings.CabinetXmlPath);
        // Conversational note: default landing filter shows the most actionable list for day-one workflows.
        FilterShowEnabledOnlyChip.IsChecked = false;
        FilterGlobalChip.IsChecked = !_isTableScoped;
        ToyScopeText.Text = $"Scope: {_activeScopeLabel}";
        UpdateSelectionTooltips();
    }

    private void OnResolutionPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        var preset = ResolutionPresetCombo.SelectedItem?.ToString() ?? string.Empty;
        CustomResolutionPanel.Visibility = preset == CustomResolution ? Visibility.Visible : Visibility.Collapsed;

        switch (preset)
        {
            case "SD (32x8)":
                WidthTextBox.Text = "32";
                HeightTextBox.Text = "8";
                break;
            case "HD (64x16)":
                WidthTextBox.Text = "64";
                HeightTextBox.Text = "16";
                break;
            case "Ultra (128x32)":
                WidthTextBox.Text = "128";
                HeightTextBox.Text = "32";
                break;
        }

        OnSettingChanged(sender, e);
    }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        OnSettingChanged(sender, e);
    }

    private void OnDotShapeChanged(object sender, SelectionChangedEventArgs e) => OnSettingChanged(sender, e);

    private void OnDotBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        DotBrightnessValueText.Text = $"{NormalizeDotBrightness(e.NewValue):0.00}";
        OnSettingChanged(sender, e);
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, DebugCheckBox))
        {
            UpdateAdvancedControlState();
        }

        UpdateSelectionTooltips();
        UpdateSummary();
        UpdateDirtyState();
    }

    private void OnTextSettingChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSummary();
        UpdateDirtyState();
    }

    private void OnBrowseCabinetXml(object sender, RoutedEventArgs e)
    {
        var picker = new Win32OpenFileDialog
        {
            Filter = "Cabinet XML|Cabinet.xml;*.xml|All files|*.*",
            CheckFileExists = true,
            Title = "Select Cabinet.xml",
        };

        if (picker.ShowDialog(this) == true)
        {
            CabinetPathTextBox.Text = picker.FileName;
            RefreshLedStripList(picker.FileName);
            LoadToyCollections();
            OnSettingChanged(sender, e);
        }
    }

    private void OnVirtualToyFilterChanged(object sender, RoutedEventArgs e)
    {
        ApplyVirtualToyFilter();
    }

    private void RefreshLedStripList(string? cabinetPath)
    {
        LedStripCombo.ItemsSource = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(cabinetPath))
        {
            LedStripCombo.Text = _working.Settings.CabinetToyName;
            return;
        }

        var resolvedCabinetPath = cabinetPath;
        if (!File.Exists(resolvedCabinetPath))
        {
            LedStripCombo.Text = _working.Settings.CabinetToyName;
            return;
        }

        try
        {
            var toys = _cabinetXmlService.GetLedStripToyNames(resolvedCabinetPath);
            LedStripCombo.ItemsSource = toys;
            LedStripCombo.Text = toys.FirstOrDefault(x => x.Equals(_working.Settings.CabinetToyName, StringComparison.OrdinalIgnoreCase))
                ?? toys.FirstOrDefault(x => x.Equals("Matrix1", StringComparison.OrdinalIgnoreCase))
                ?? toys.FirstOrDefault()
                ?? _working.Settings.CabinetToyName;
        }
        catch
        {
            LedStripCombo.Text = _working.Settings.CabinetToyName;
        }
    }

    private void LoadToyCollections()
    {
        _virtualToys = [];
        _hardwareToys = [];

        var cabinetPath = CabinetPathTextBox.Text;
        if (!File.Exists(cabinetPath))
        {
            HardwareSummaryHeaderText.Text = "Hardware Toys (read-only): 0";
            HardwareNamesItemsControl.ItemsSource = Array.Empty<string>();
            VirtualToysList.ItemsSource = Array.Empty<string>();
            return;
        }

        try
        {
            var inventory = _cabinetXmlService.GetToyInventory(cabinetPath);
            _virtualToys = inventory.VirtualToys
                .Select(entry => new VirtualToyListItem(
                    Name: entry.Name,
                    Enabled: ResolveEnabled(entry.Name),
                    IsGlobal: !_isTableScoped))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _hardwareToys = inventory.HardwareToys
                .Select(entry => new VirtualToyListItem(
                    Name: entry.Name,
                    Enabled: ResolveEnabled(entry.Name),
                    IsGlobal: !_isTableScoped))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            // Keep startup/settings resilient to malformed XML; UI falls back to an empty summary instead of crashing.
            _virtualToys = [];
            _hardwareToys = [];
        }

        HardwareSummaryHeaderText.Text = $"Hardware Toys (read-only): {_hardwareToys.Count}";
        HardwareNamesItemsControl.ItemsSource = _hardwareToys.Select(x => x.Name).ToArray();
        ApplyVirtualToyFilter();
    }

    private void ApplyVirtualToyFilter()
    {
        var showEnabledOnly = FilterShowEnabledOnlyChip.IsChecked == true;
        var showGlobal = FilterGlobalChip.IsChecked == true;

        var filtered = _virtualToys.Where(item =>
        {
            var enabledPass = !showEnabledOnly || item.Enabled;
            var scopePass = !showGlobal || item.IsGlobal;
            return enabledPass && scopePass;
        });

        VirtualToysList.ItemsSource = filtered
            .Select(x => $"{x.Name}  •  {(x.Enabled ? "Enabled" : "Disabled")}  •  {(_isTableScoped ? _activeScopeLabel : "Global")}")
            .ToArray();
    }

    private bool ResolveEnabled(string toyName)
    {
        var configuredToy = _working.Routing.Toys.FirstOrDefault(x => x.Id.Equals(toyName, StringComparison.OrdinalIgnoreCase));
        return configuredToy?.Enabled ?? true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryBuildConfig(out var config, out var error))
        {
            WpfMessageBox.Show(this, error, "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = config;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnResetDisplayDefaults(object sender, RoutedEventArgs e)
    {
        var defaults = new AppConfig();

        ResolutionPresetCombo.SelectedItem = DetectResolutionPreset(defaults.Matrix.Width, defaults.Matrix.Height);
        WidthTextBox.Text = defaults.Matrix.Width.ToString();
        HeightTextBox.Text = defaults.Matrix.Height.ToString();
        CustomResolutionPanel.Visibility = Equals(ResolutionPresetCombo.SelectedItem, CustomResolution) ? Visibility.Visible : Visibility.Collapsed;

        AlwaysOnTopCheckBox.IsChecked = defaults.Window.AlwaysOnTop;
        DotShapeCombo.SelectedItem = defaults.Matrix.DotShape;
        DotBrightnessSlider.Value = NormalizeDotBrightness(defaults.Matrix.Brightness);
        QualityCombo.SelectedItem = defaults.Settings.VisualQuality;
        RendererCombo.SelectedItem = NormalizeRenderer(defaults.Matrix.Renderer);

        OnSettingChanged(sender, e);
    }

    private bool TryBuildConfig(out AppConfig config, out string error)
    {
        config = Clone(_working);
        error = string.Empty;

        if (!int.TryParse(WidthTextBox.Text, out var width) || !int.TryParse(HeightTextBox.Text, out var height))
        {
            error = "Width and height must be valid whole numbers.";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            error = "Width and height must be positive values.";
            return false;
        }

        if (width * height > CabinetXmlService.SafeMaxLedTotal)
        {
            error = $"Total LEDs must be at or below {CabinetXmlService.SafeMaxLedTotal}.";
            return false;
        }

        config.Matrix.Width = width;
        config.Matrix.Height = height;
        config.Window.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;
        config.Matrix.DotShape = DotShapeCombo.SelectedItem?.ToString() ?? "circle";
        config.Matrix.Brightness = NormalizeDotBrightness(DotBrightnessSlider.Value);

        config.Settings.VisualQuality = QualityCombo.SelectedItem?.ToString() ?? VisualQualityProfiles.High;
        if (config.Settings.VisualQuality != VisualQualityProfiles.Custom)
        {
            VisualQualityProfiles.ApplyPreset(config.Matrix, config.Settings.VisualQuality);
        }
        config.Matrix.Renderer = NormalizeRenderer(RendererCombo.SelectedItem?.ToString());

        config.Debug.LogProtocol = DebugCheckBox.IsChecked == true;
        config.Debug.LogFrames = config.Debug.LogProtocol && LogFramesCheckBox.IsChecked == true;
        config.Settings.AutoUpdateCabinetOnResolutionChange = AutoUpdateCabinetCheckBox.IsChecked == true;
        config.Settings.CabinetXmlPath = CabinetPathTextBox.Text.Trim();
        config.Settings.DofConfigFolderPath = Path.GetDirectoryName(config.Settings.CabinetXmlPath) ?? config.Settings.DofConfigFolderPath;
        config.Settings.CabinetToyName = string.IsNullOrWhiteSpace(LedStripCombo.Text) ? "Matrix1" : LedStripCombo.Text.Trim();

        return true;
    }

    private void UpdateSummary()
    {
        var widthText = WidthTextBox.Text;
        var heightText = HeightTextBox.Text;

        if (!int.TryParse(widthText, out var width) || !int.TryParse(heightText, out var height) || width <= 0 || height <= 0)
        {
            SummaryText.Text = "Summary: enter a valid positive width/height.";
            SummaryStatusText.Text = "Status: invalid resolution.";
            return;
        }

        var total = width * height;
        SummaryText.Text = $"Summary: {width}x{height} ({total} LEDs), toy '{(string.IsNullOrWhiteSpace(LedStripCombo.Text) ? "Matrix1" : LedStripCombo.Text)}'.";
        SummaryStatusText.Text = total <= CabinetXmlService.SafeMaxLedTotal
            ? $"Status: compatible with safe DOF target (<= {CabinetXmlService.SafeMaxLedTotal})."
            : $"Status: exceeds safe DOF target (<= {CabinetXmlService.SafeMaxLedTotal}).";
    }

    private void UpdateSelectionTooltips()
    {
        DotShapeCombo.ToolTip = DotShapeCombo.SelectedItem?.ToString() switch
        {
            "circle" => "Circle: rounded LED style with lens-like look.",
            "square" => "Square: block-style pixel look with hard corners.",
            _ => "Choose dot geometry for matrix rendering.",
        };

        QualityCombo.ToolTip = QualityCombo.SelectedItem?.ToString() switch
        {
            VisualQualityProfiles.Low => "Low: fastest flat RGB pass, no temporal smoothing or tone mapping.",
            VisualQualityProfiles.High => "High: tone mapping + temporal smoothing for richer color output.",
            VisualQualityProfiles.Custom => "Custom: respects current values from settings.json as hand-edited.",
            _ => "Select a quality profile for performance versus visual fidelity.",
        };
    }

    private void UpdateAdvancedControlState()
    {
        var debugLoggingEnabled = DebugCheckBox.IsChecked == true;
        LogFramesCheckBox.IsEnabled = debugLoggingEnabled;
    }

    private static string NormalizeRenderer(string? renderer)
    {
        return renderer?.Equals("cpu", StringComparison.OrdinalIgnoreCase) == true ? "cpu" : "gpu";
    }

    private static double NormalizeDotBrightness(double value)
    {
        var clamped = Math.Clamp(value, 0.0, 1.0);
        return Math.Round(clamped / 0.05, MidpointRounding.AwayFromZero) * 0.05;
    }

    private void CaptureCurrentAsCleanState()
    {
        if (TryBuildConfig(out var config, out _))
        {
            _lastAppliedFingerprint = BuildFingerprint(config);
        }
        else
        {
            _lastAppliedFingerprint = string.Empty;
        }

        UpdateDirtyState();
    }

    private void UpdateDirtyState()
    {
        if (!TryBuildConfig(out _, out _))
        {
            return;
        }
    }

    private static string BuildFingerprint(AppConfig config) => JsonSerializer.Serialize(config, FingerprintSerializerOptions);

    private static string DetectResolutionPreset(int width, int height)
    {
        if (width == 32 && height == 8)
        {
            return "SD (32x8)";
        }

        if (width == 64 && height == 16)
        {
            return "HD (64x16)";
        }

        return width == 128 && height == 32 ? "Ultra (128x32)" : CustomResolution;
    }

    private static AppConfig Clone(AppConfig config)
    {
        return new AppConfig
        {
            Transport = new TransportConfig { PipeName = config.Transport.PipeName },
            Matrix = new MatrixConfig
            {
                Renderer = config.Matrix.Renderer,
                Width = config.Matrix.Width,
                Height = config.Matrix.Height,
                Mapping = config.Matrix.Mapping,
                DotShape = config.Matrix.DotShape,
                MinDotSpacing = config.Matrix.MinDotSpacing,
                Brightness = config.Matrix.Brightness,
                Gamma = config.Matrix.Gamma,
                ToneMapping = new ToneMappingConfig
                {
                    Enabled = config.Matrix.ToneMapping.Enabled,
                    KneeStart = config.Matrix.ToneMapping.KneeStart,
                    Strength = config.Matrix.ToneMapping.Strength,
                },
                TemporalSmoothing = new TemporalSmoothingConfig
                {
                    Enabled = config.Matrix.TemporalSmoothing.Enabled,
                    RiseAlpha = config.Matrix.TemporalSmoothing.RiseAlpha,
                    FallAlpha = config.Matrix.TemporalSmoothing.FallAlpha,
                },
                Visual = new MatrixVisualConfig
                {
                    FlatShading = config.Matrix.Visual.FlatShading,
                    FullBrightnessRadiusMinPct = config.Matrix.Visual.FullBrightnessRadiusMinPct,
                    OffStateTintR = config.Matrix.Visual.OffStateTintR,
                    OffStateTintG = config.Matrix.Visual.OffStateTintG,
                    OffStateTintB = config.Matrix.Visual.OffStateTintB,
                    OffStateAlpha = config.Matrix.Visual.OffStateAlpha,
                    LensFalloff = config.Matrix.Visual.LensFalloff,
                    SpecularHotspot = config.Matrix.Visual.SpecularHotspot,
                    RimHighlight = config.Matrix.Visual.RimHighlight,
                },
                Bloom = new BloomConfig
                {
                    Enabled = config.Matrix.Bloom.Enabled,
                    Threshold = config.Matrix.Bloom.Threshold,
                    SoftKnee = config.Matrix.Bloom.SoftKnee,
                    DownsampleDivisor = config.Matrix.Bloom.DownsampleDivisor,
                    NearRadiusPx = config.Matrix.Bloom.NearRadiusPx,
                    FarRadiusPx = config.Matrix.Bloom.FarRadiusPx,
                    NearStrength = config.Matrix.Bloom.NearStrength,
                    FarStrength = config.Matrix.Bloom.FarStrength,
                },
            },
            Window = new WindowConfig
            {
                AlwaysOnTop = config.Window.AlwaysOnTop,
                Borderless = config.Window.Borderless,
                Left = config.Window.Left,
                Top = config.Window.Top,
                Width = config.Window.Width,
                Height = config.Window.Height,
            },
            Debug = new DebugConfig
            {
                ShowDebug = config.Debug.ShowDebug,
                LogProtocol = config.Debug.LogProtocol,
                LogFrames = config.Debug.LogFrames,
            },
            Settings = new SettingsConfig
            {
                DofConfigFolderPath = config.Settings.DofConfigFolderPath,
                CabinetXmlPath = config.Settings.CabinetXmlPath,
                CabinetToyName = config.Settings.CabinetToyName,
                AutoUpdateCabinetOnResolutionChange = config.Settings.AutoUpdateCabinetOnResolutionChange,
                VisualQuality = config.Settings.VisualQuality,
            },
        };
    }

    private sealed record VirtualToyListItem(
        string Name,
        bool Enabled,
        bool IsGlobal);
}
