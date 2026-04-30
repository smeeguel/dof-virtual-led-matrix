using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace VirtualDofMatrix.Installer.ViewModels;

public sealed class InstallerState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _installFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "VirtualDofMatrix");
    private string _dofRootPath = @"C:\DirectOutput";
    private string _dofConfigPath = @"C:\DirectOutput\Config";
    private bool _dofDetected;
    private bool _backupEnabled = true;
    private string _backupPath = string.Empty;
    private string _toyTemplate = "single_matrix";
    private bool _launchAfterInstall = true;
    private bool _isUpgrade;

    public string InstallFolder   { get => _installFolder;   set => Set(ref _installFolder, value); }
    public string DofRootPath     { get => _dofRootPath;     set => Set(ref _dofRootPath, value); }
    public string DofConfigPath   { get => _dofConfigPath;   set => Set(ref _dofConfigPath, value); }
    public bool   DofDetected     { get => _dofDetected;     set => Set(ref _dofDetected, value); }
    public bool   BackupEnabled   { get => _backupEnabled;   set => Set(ref _backupEnabled, value); }
    public string BackupPath      { get => _backupPath;      set => Set(ref _backupPath, value); }
    public string ToyTemplate     { get => _toyTemplate;     set => Set(ref _toyTemplate, value); }
    public bool   LaunchAfterInstall { get => _launchAfterInstall; set => Set(ref _launchAfterInstall, value); }
    public bool   IsUpgrade       { get => _isUpgrade;       set => Set(ref _isUpgrade, value); }

    public string ProductVersion { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
