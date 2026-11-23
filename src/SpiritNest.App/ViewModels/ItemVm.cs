using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using SpiritNest.Models;
using SpiritNest.Services;

namespace SpiritNest.ViewModels;

public sealed class ItemVm : INotifyPropertyChanged
{
    private BitmapImage? _icon;
    private bool _isBroken;
    private bool _isSelected;
    private bool _isLaunching;

    public ItemInfo Model { get; }
    public string Id => Model.Id;
    public string DisplayName => Model.DisplayName;
    public bool IsBroken
    {
        get => _isBroken;
        set
        {
            if (_isBroken == value) return;
            _isBroken = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ContentOpacity));
            OnPropertyChanged(nameof(CanInteract));
            OnPropertyChanged(nameof(BrokenTip));
        }
    }

    public string BrokenTip => IsBroken ? $"{DisplayName}（已失效）" : DisplayName;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }
    /// <summary>True while waiting for the target app to respond after click.</summary>
    public bool IsLaunching
    {
        get => _isLaunching;
        set
        {
            if (_isLaunching == value) return;
            _isLaunching = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ContentOpacity));
            OnPropertyChanged(nameof(CanInteract));
        }
    }
    public bool CanInteract => !IsLaunching && !IsBroken;
    /// <summary>Icon and label fade while launching; the spinner stays full strength.</summary>
    public double ContentOpacity => IsLaunching ? 0.35 : IsBroken ? 0.82 : 1.0;
    public BitmapImage? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    public bool IsUrl =>
        string.Equals(Model.ItemKind, "Url", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Model.StorageMode, "Url", StringComparison.OrdinalIgnoreCase);

    public ItemVm(ItemInfo model) => Model = model;

    public void LoadIcon(IconService icons)
    {
        if (IsUrl)
        {
            Icon = icons.GetUrlIcon(Model.IconCacheKey ?? Model.Id);
            IsBroken = Model.IsBroken;
            return;
        }

        if (ShellPlaces.IsShell(Model) &&
            ShellPlaces.TryGetIconLocation(Model, out var iconFile, out var iconIndex))
        {
            Icon = icons.GetIndexedIcon(iconFile, iconIndex, ShellPlaces.IconCacheKey(Model));
            IsBroken = false;
            return;
        }

        var path = Model.StoredPath ?? Model.TargetPath;
        Icon = icons.GetIcon(path, Model.IconCacheKey ?? Model.Id);
        IsBroken = Model.IsBroken;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
