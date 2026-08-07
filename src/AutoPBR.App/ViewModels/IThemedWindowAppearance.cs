using Avalonia.Media;

namespace AutoPBR.App.ViewModels;

/// <summary>Shared palette brushes used by shell / dialog windows.</summary>
internal interface IThemedWindowAppearance
{
    IBrush WindowBackground { get; }

    IBrush ForegroundBrush { get; }
}
