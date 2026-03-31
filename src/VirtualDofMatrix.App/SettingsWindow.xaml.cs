using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

public partial class SettingsWindow : Window
{
    private const string CustomResolution = "Custom";

    private readonly AppConfig _working;
    private readonly CabinetXmlService _cabinetXmlService;

    public SettingsWindow(AppConfig source, CabinetXmlService cabinetXmlService)
    {
        _working = Clone(source);
        _cabinetXmlService = cabinetXmlService;

        InitializeComponent();
        PopulateControls();
        UpdateSummary();
    }

    public AppConfig? Result { get; private set; }

    public event EventHandler<AppConfig>? Applied;

    private void PopulateControls()
    {
        ResolutionPresetCombo.ItemsSource = new[] { "SD (32x8)", "HD (64x16)", "Ultra (128x32)", CustomResolution };
        QualityCombo.ItemsSource = VisualQualityProfiles.Names;
        ScaleModeCombo.ItemsSource = new[] { "Nearest", "Smooth" };
        DotShapeCombo.ItemsSource = new[] { "circle", "square" };

        WidthTextBox.Text = _working.Matrix.Width.ToString();
        HeightTextBox.Text = _working.Matrix.Height.ToString();

        ResolutionPresetCombo.SelectedItem = DetectResolutionPreset(_working.Matrix.Width, _working.Matrix.Height);
        CustomResolutionPanel.Visibility = Equals(ResolutionPresetCombo.SelectedItem, CustomResolution) ? Visibility.Visible : Visibility.Collapsed;

        AlwaysOnTopCheckBox.IsChecked = _working.Window.AlwaysOnTop;
        ScaleModeCombo.SelectedItem = _working.Matrix.Renderer.Equals("writeableBitmap", StringComparison.OrdinalIgnoreCase) ? "Smooth" : "Nearest";
        DotShapeCombo.SelectedItem = _working.Matrix.DotShape;

        if (!VisualQualityProfiles.Names.Contains(_working.Settings.VisualQuality, StringComparer.OrdinalIgnoreCase))
        {
            _working.Settings.VisualQuality = VisualQualityProfiles.Medium;
        }

        QualityCombo.SelectedItem = _working.Settings.VisualQuality;
        CustomQualityPanel.Visibility = _working.Settings.VisualQuality == VisualQualityProfiles.Custom ? Visibility.Visible : Visibility.Collapsed;

        ToneMappingCheckBox.IsChecked = _working.Matrix.ToneMapping.Enabled;
        TemporalSmoothingCheckBox.IsChecked = _working.Matrix.TemporalSmoothing.Enabled;
        BloomCheckBox.IsChecked = _working.Matrix.Bloom.Enabled;

        CabinetPathTextBox.Text = _working.Settings.CabinetXmlPath;
        AutoUpdateCabinetCheckBox.IsChecked = _working.Settings.AutoUpdateCabinetOnResolutionChange;
        DebugCheckBox.IsChecked = _working.Debug.LogProtocol;

        RefreshLedStripList(_working.Settings.CabinetXmlPath);
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

        UpdateSummary();
    }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        var quality = QualityCombo.SelectedItem?.ToString() ?? VisualQualityProfiles.Medium;
        CustomQualityPanel.Visibility = quality == VisualQualityProfiles.Custom ? Visibility.Visible : Visibility.Collapsed;
        if (quality != VisualQualityProfiles.Custom)
        {
            VisualQualityProfiles.ApplyPreset(_working.Matrix, quality);
            ToneMappingCheckBox.IsChecked = _working.Matrix.ToneMapping.Enabled;
            TemporalSmoothingCheckBox.IsChecked = _working.Matrix.TemporalSmoothing.Enabled;
            BloomCheckBox.IsChecked = _working.Matrix.Bloom.Enabled;
        }

        UpdateSummary();
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e) => UpdateSummary();

    private void OnTextSettingChanged(object sender, TextChangedEventArgs e) => UpdateSummary();

    private void OnBrowseCabinetXml(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Filter = "Cabinet XML|Cabinet.xml;*.xml|All files|*.*",
            CheckFileExists = true,
            Title = "Select Cabinet.xml",
        };

        if (picker.ShowDialog(this) == true)
        {
            CabinetPathTextBox.Text = picker.FileName;
            RefreshLedStripList(picker.FileName);
            UpdateSummary();
        }
    }

    private void RefreshLedStripList(string? cabinetPath)
    {
        LedStripCombo.ItemsSource = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(cabinetPath) || !File.Exists(cabinetPath))
        {
            LedStripCombo.Text = _working.Settings.CabinetToyName;
            return;
        }

        try
        {
            var toys = _cabinetXmlService.GetLedStripToyNames(cabinetPath);
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

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!TryBuildConfig(out var config, out var error))
        {
            MessageBox.Show(this, error, "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = config;
        Applied?.Invoke(this, config);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        OnApply(sender, e);
        if (Result is not null)
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
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
        config.Matrix.Renderer = Equals(ScaleModeCombo.SelectedItem, "Smooth") ? "writeableBitmap" : "primitive";
        config.Matrix.DotShape = DotShapeCombo.SelectedItem?.ToString() ?? "circle";

        config.Settings.VisualQuality = QualityCombo.SelectedItem?.ToString() ?? VisualQualityProfiles.Medium;
        if (config.Settings.VisualQuality == VisualQualityProfiles.Custom)
        {
            config.Matrix.ToneMapping.Enabled = ToneMappingCheckBox.IsChecked == true;
            config.Matrix.TemporalSmoothing.Enabled = TemporalSmoothingCheckBox.IsChecked == true;
            config.Matrix.Bloom.Enabled = BloomCheckBox.IsChecked == true;
        }
        else
        {
            VisualQualityProfiles.ApplyPreset(config.Matrix, config.Settings.VisualQuality);
        }

        config.Debug.LogProtocol = DebugCheckBox.IsChecked == true;
        config.Settings.AutoUpdateCabinetOnResolutionChange = AutoUpdateCabinetCheckBox.IsChecked == true;
        config.Settings.CabinetXmlPath = CabinetPathTextBox.Text.Trim();
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
            Transport = new TransportConfig { Mode = config.Transport.Mode, PipeName = config.Transport.PipeName },
            Serial = new SerialConfig
            {
                PortName = config.Serial.PortName,
                BaudRate = config.Serial.BaudRate,
                MaxLedsPerChannel = config.Serial.MaxLedsPerChannel,
                MaxStrips = config.Serial.MaxStrips,
                ReadTimeoutMs = config.Serial.ReadTimeoutMs,
                WriteTimeoutMs = config.Serial.WriteTimeoutMs,
                DtrEnable = config.Serial.DtrEnable,
            },
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
                    QualityPreset = config.Matrix.Bloom.QualityPreset,
                    Threshold = config.Matrix.Bloom.Threshold,
                    SmallRadius = config.Matrix.Bloom.SmallRadius,
                    WideRadius = config.Matrix.Bloom.WideRadius,
                    SmallStrength = config.Matrix.Bloom.SmallStrength,
                    WideStrength = config.Matrix.Bloom.WideStrength,
                    BufferScaleDivisor = config.Matrix.Bloom.BufferScaleDivisor,
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
                CabinetXmlPath = config.Settings.CabinetXmlPath,
                CabinetToyName = config.Settings.CabinetToyName,
                AutoUpdateCabinetOnResolutionChange = config.Settings.AutoUpdateCabinetOnResolutionChange,
                VisualQuality = config.Settings.VisualQuality,
            },
        };
    }
}
