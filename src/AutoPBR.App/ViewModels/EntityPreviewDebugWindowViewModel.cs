using AutoPBR.App.Lang;
using AutoPBR.Core.Models;

using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoPBR.App.ViewModels;

public partial class EntityPreviewDebugWindowViewModel : ViewModelBase, IThemedWindowAppearance
{
    private readonly MainWindowViewModel _main;
    private bool _syncingForceCpu;

    public EntityPreviewDebugWindowViewModel(MainWindowViewModel main)
    {
        _main = main;
        _main.PropertyChanged += MainOnPropertyChanged;

        _forceCpuSkinning = _main.Preview3DForceEntityCpuSkinning;
        _logDrawContractEveryFrame = EntityPreviewDebugSettings.LogDrawContractEveryFrame;
        _showDepthLayerDebug = EntityPreviewDebugSettings.ShowDepthLayerDebug;
        _lerBasisOverrideMode = (int)EntityPreviewDebugSettings.LerBasisOverride;
        _useLegacyTranslationTimesRotationPartPose = EntityPreviewDebugSettings.UseLegacyTranslationTimesRotationPartPose;
        _skipAllPartTreeRepair = EntityPreviewDebugSettings.SkipAllPartTreeRepair;
        _repairGlobalReparentRules = EntityPreviewDebugSettings.RepairGlobalReparentRules;
        _repairQuadrupedLegReparent = EntityPreviewDebugSettings.RepairQuadrupedLegReparent;
        _repairForceLegReparentOnFlatBake = EntityPreviewDebugSettings.RepairForceLegReparentOnFlatBake;
        _repairHeadStackLegReparent = EntityPreviewDebugSettings.RepairHeadStackLegReparent;
        _repairRemoveDuplicateRootSiblings = EntityPreviewDebugSettings.RepairRemoveDuplicateRootSiblings;
        _repairCollapseInnerBody = EntityPreviewDebugSettings.RepairCollapseInnerBody;
        _repairDeduplicateNestedPartIds = EntityPreviewDebugSettings.RepairDeduplicateNestedPartIds;
        _repairZeroEquineRootOffset = EntityPreviewDebugSettings.RepairZeroEquineRootOffset;
    }

    public LocalizedStrings Strings => _main.Strings;

    public IBrush WindowBackground => _main.WindowBackground;
    public IBrush CardBackground => _main.CardBackground;
    public IBrush CardBorderBrush => _main.CardBorderBrush;
    public IBrush ForegroundBrush => _main.ForegroundBrush;
    public IBrush AccentBrush => _main.AccentBrush;

    [ObservableProperty] private bool _forceCpuSkinning;
    [ObservableProperty] private bool _logDrawContractEveryFrame;
    [ObservableProperty] private bool _showDepthLayerDebug;
    [ObservableProperty] private int _lerBasisOverrideMode;
    [ObservableProperty] private bool _useLegacyTranslationTimesRotationPartPose;
    [ObservableProperty] private bool _skipAllPartTreeRepair;
    [ObservableProperty] private bool _repairGlobalReparentRules;
    [ObservableProperty] private bool _repairQuadrupedLegReparent;
    [ObservableProperty] private bool _repairForceLegReparentOnFlatBake;
    [ObservableProperty] private bool _repairHeadStackLegReparent;
    [ObservableProperty] private bool _repairRemoveDuplicateRootSiblings;
    [ObservableProperty] private bool _repairCollapseInnerBody;
    [ObservableProperty] private bool _repairDeduplicateNestedPartIds;
    [ObservableProperty] private bool _repairZeroEquineRootOffset;

    public bool PartPoseErTimesT
    {
        get => !UseLegacyTranslationTimesRotationPartPose;
        set
        {
            if (value)
            {
                UseLegacyTranslationTimesRotationPartPose = false;
            }
        }
    }

    public bool PartPoseLegacyTxEr
    {
        get => UseLegacyTranslationTimesRotationPartPose;
        set
        {
            if (value)
            {
                UseLegacyTranslationTimesRotationPartPose = true;
            }
        }
    }

    public bool LerPolicyDefault
    {
        get => LerBasisOverrideMode == (int)EntityPreviewLerBasisOverride.PolicyDefault;
        set
        {
            if (value)
            {
                LerBasisOverrideMode = (int)EntityPreviewLerBasisOverride.PolicyDefault;
            }
        }
    }

    public bool LerStandardWorldRoot
    {
        get => LerBasisOverrideMode == (int)EntityPreviewLerBasisOverride.StandardWorldRoot;
        set
        {
            if (value)
            {
                LerBasisOverrideMode = (int)EntityPreviewLerBasisOverride.StandardWorldRoot;
            }
        }
    }

    public bool LerRightComposeLocalChain
    {
        get => LerBasisOverrideMode == (int)EntityPreviewLerBasisOverride.RightComposeLocalChain;
        set
        {
            if (value)
            {
                LerBasisOverrideMode = (int)EntityPreviewLerBasisOverride.RightComposeLocalChain;
            }
        }
    }

    public bool LerSkip
    {
        get => LerBasisOverrideMode == (int)EntityPreviewLerBasisOverride.Skip;
        set
        {
            if (value)
            {
                LerBasisOverrideMode = (int)EntityPreviewLerBasisOverride.Skip;
            }
        }
    }

    public void Detach() => _main.PropertyChanged -= MainOnPropertyChanged;

    partial void OnForceCpuSkinningChanged(bool value)
    {
        if (_syncingForceCpu)
        {
            return;
        }

        if (_main.Preview3DForceEntityCpuSkinning != value)
        {
            _main.Preview3DForceEntityCpuSkinning = value;
        }
    }

    partial void OnLogDrawContractEveryFrameChanged(bool value)
    {
        EntityPreviewDebugSettings.LogDrawContractEveryFrame = value;
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnShowDepthLayerDebugChanged(bool value)
    {
        EntityPreviewDebugSettings.ShowDepthLayerDebug = value;
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnLerBasisOverrideModeChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 3);
        if (clamped != value)
        {
            LerBasisOverrideMode = clamped;
            return;
        }

        EntityPreviewDebugSettings.LerBasisOverride = (EntityPreviewLerBasisOverride)clamped;
        NotifyLerRadioProperties();
        ApplyMeshAffectingRefresh();
    }

    partial void OnUseLegacyTranslationTimesRotationPartPoseChanged(bool value)
    {
        EntityPreviewDebugSettings.UseLegacyTranslationTimesRotationPartPose = value;
        OnPropertyChanged(nameof(PartPoseErTimesT));
        OnPropertyChanged(nameof(PartPoseLegacyTxEr));
        ApplyMeshAffectingRefresh();
    }

    partial void OnSkipAllPartTreeRepairChanged(bool value)
    {
        EntityPreviewDebugSettings.SkipAllPartTreeRepair = value;
        ApplyMeshAffectingRefresh();
    }

    partial void OnRepairGlobalReparentRulesChanged(bool value)
    {
        EntityPreviewDebugSettings.RepairGlobalReparentRules = value;
        ApplyMeshAffectingRefresh();
    }

    partial void OnRepairQuadrupedLegReparentChanged(bool value)
    {
        EntityPreviewDebugSettings.RepairQuadrupedLegReparent = value;
        ApplyMeshAffectingRefresh();
    }

    partial void OnRepairForceLegReparentOnFlatBakeChanged(bool value)
    {
        EntityPreviewDebugSettings.RepairForceLegReparentOnFlatBake = value;
        ApplyMeshAffectingRefresh();
    }

    partial void OnRepairHeadStackLegReparentChanged(bool value)
    {
        EntityPreviewDebugSettings.RepairHeadStackLegReparent = value;
        ApplyMeshAffectingRefresh();
    }

    partial void OnRepairRemoveDuplicateRootSiblingsChanged(bool value)
    {
        EntityPreviewDebugSettings.RepairRemoveDuplicateRootSiblings = value;
        ApplyMeshAffectingRefresh();
    }

    partial void OnRepairCollapseInnerBodyChanged(bool value)
    {
        EntityPreviewDebugSettings.RepairCollapseInnerBody = value;
        ApplyMeshAffectingRefresh();
    }

    partial void OnRepairDeduplicateNestedPartIdsChanged(bool value)
    {
        EntityPreviewDebugSettings.RepairDeduplicateNestedPartIds = value;
        ApplyMeshAffectingRefresh();
    }

    partial void OnRepairZeroEquineRootOffsetChanged(bool value)
    {
        EntityPreviewDebugSettings.RepairZeroEquineRootOffset = value;
        ApplyMeshAffectingRefresh();
    }

    private void ApplyMeshAffectingRefresh()
    {
        EntityPreviewDebugSettings.NotifyMeshAffectingChange();
        _main.TriggerPreviewRefreshForDebug();
    }

    private void NotifyLerRadioProperties()
    {
        OnPropertyChanged(nameof(LerPolicyDefault));
        OnPropertyChanged(nameof(LerStandardWorldRoot));
        OnPropertyChanged(nameof(LerRightComposeLocalChain));
        OnPropertyChanged(nameof(LerSkip));
    }

    private void MainOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.Strings))
        {
            OnPropertyChanged(nameof(Strings));
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.WindowBackground) or
            nameof(MainWindowViewModel.CardBackground) or
            nameof(MainWindowViewModel.CardBorderBrush) or
            nameof(MainWindowViewModel.ForegroundBrush) or
            nameof(MainWindowViewModel.AccentBrush))
        {
            OnPropertyChanged(nameof(WindowBackground));
            OnPropertyChanged(nameof(CardBackground));
            OnPropertyChanged(nameof(CardBorderBrush));
            OnPropertyChanged(nameof(ForegroundBrush));
            OnPropertyChanged(nameof(AccentBrush));
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.Preview3DForceEntityCpuSkinning))
        {
            if (ForceCpuSkinning == _main.Preview3DForceEntityCpuSkinning)
            {
                return;
            }

            _syncingForceCpu = true;
            ForceCpuSkinning = _main.Preview3DForceEntityCpuSkinning;
            _syncingForceCpu = false;
        }
    }
}
