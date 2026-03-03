using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoPBR.App.Models;

public partial class TextureKeyItem : ObservableObject
{
    public TextureKeyItem(string key, bool isIgnored = false)
    {
        Key = key;
        IsIgnored = isIgnored;
    }

    public string Key { get; }

    [ObservableProperty]
    private bool isIgnored;
}

