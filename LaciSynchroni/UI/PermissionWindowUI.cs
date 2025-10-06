using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using LaciSynchroni.Common.Data.Enum;
using LaciSynchroni.Common.Data.Extensions;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.UI.Components;
using LaciSynchroni.Utils;
using LaciSynchroni.WebAPI;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.UI;

public class PermissionWindowUI : WindowMediatorSubscriberBase
{
    public Pair CurrentPair { get; set; }

    private readonly UiSharedService _uiSharedService;
    private readonly ApiController _apiController;
    private readonly ServerConfigurationManager _serverManager;
    private UserPermissions _ownPermissions;
    private IEnumerable<Pair> _currentPairs { get; set; }
    private readonly ServerSelectorSmall _serverSelector;
    private int _serverForProfile = 0;
    private bool _singleProfile => _currentPairs.Count() == 1;

    public PermissionWindowUI(ILogger<PermissionWindowUI> logger, IEnumerable<Pair> pairs, SyncMediator mediator, UiSharedService uiSharedService,
        ServerConfigurationManager serverManager, ApiController apiController, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Permissions for " + pairs.First().PlayerIdentification + "###LaciSynchroniPermissions", performanceCollectorService)
    {
        _serverManager = serverManager;
        _uiSharedService = uiSharedService;
        _apiController = apiController;
        _currentPairs = pairs;
        CurrentPair = pairs.First();
        _ownPermissions = pairs.First().UserPair.OwnPermissions.DeepClone();

        _serverSelector = new ServerSelectorSmall(index => _serverForProfile = index);

        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize;
        SizeConstraints = new()
        {
            MinimumSize = new(450, 100),
            MaximumSize = new(450, 500)
        };
        IsOpen = true;
    }

    protected override void DrawInternal()
    {
        if (!_singleProfile)
        {
            var serverIndexes = _currentPairs.Select(p => p.ServerIndex).ToArray();
            _serverSelector.Draw(_serverManager.GetServerNamesByIndexes(serverIndexes), serverIndexes, 512);
            CurrentPair = _currentPairs?.FirstOrDefault(p => p.ServerIndex == _serverForProfile) ?? CurrentPair;
            _ownPermissions = CurrentPair.UserPair.OwnPermissions.DeepClone();
        }

        var sticky = _ownPermissions.IsSticky();
        var paused = _ownPermissions.IsPaused();
        var disableSounds = _ownPermissions.IsDisableSounds();
        var disableAnimations = _ownPermissions.IsDisableAnimations();
        var disableVfx = _ownPermissions.IsDisableVFX();
        var style = ImGui.GetStyle();
        var indentSize = ImGui.GetFrameHeight() + style.ItemSpacing.X;

        _uiSharedService.BigText("Permissions for " + CurrentPair.UserData.AliasOrUID);

        if (_singleProfile)
        {
            var serverName = _apiController.GetServerNameByIndex(CurrentPair.ServerIndex);
            ImGui.Text("@ " + serverName);
        }
        
        ImGuiHelpers.ScaledDummy(1f);

        if (ImGui.Checkbox("Preferred Permissions", ref sticky))
        {
            _ownPermissions.SetSticky(sticky);
        }
        _uiSharedService.DrawHelpText("Preferred Permissions, when enabled, will exclude this user from any permission changes on any syncshells you share with this user.");

        ImGuiHelpers.ScaledDummy(1f);


        if (ImGui.Checkbox("Pause Sync", ref paused))
        {
            _ownPermissions.SetPaused(paused);
        }
        _uiSharedService.DrawHelpText("Pausing will completely cease any sync with this user." + UiSharedService.TooltipSeparator
            + "Note: this is bidirectional, either user pausing will cease sync completely.");
        var otherPerms = CurrentPair.UserPair.OtherPermissions;

        var otherIsPaused = otherPerms.IsPaused();
        var otherDisableSounds = otherPerms.IsDisableSounds();
        var otherDisableAnimations = otherPerms.IsDisableAnimations();
        var otherDisableVFX = otherPerms.IsDisableVFX();

        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherIsPaused, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(CurrentPair.UserData.AliasOrUID + " has " + (!otherIsPaused ? "not " : string.Empty) + "paused you");
        }

        ImGuiHelpers.ScaledDummy(0.5f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0.5f);

        if (ImGui.Checkbox("Disable Sounds", ref disableSounds))
        {
            _ownPermissions.SetDisableSounds(disableSounds);
        }
        _uiSharedService.DrawHelpText("Disabling sounds will remove all sounds synced with this user on both sides." + UiSharedService.TooltipSeparator
            + "Note: this is bidirectional, either user disabling sound sync will stop sound sync on both sides.");
        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherDisableSounds, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(CurrentPair.UserData.AliasOrUID + " has " + (!otherDisableSounds ? "not " : string.Empty) + "disabled sound sync with you");
        }

        if (ImGui.Checkbox("Disable Animations", ref disableAnimations))
        {
            _ownPermissions.SetDisableAnimations(disableAnimations);
        }
        _uiSharedService.DrawHelpText("Disabling sounds will remove all animations synced with this user on both sides." + UiSharedService.TooltipSeparator
            + "Note: this is bidirectional, either user disabling animation sync will stop animation sync on both sides.");
        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherDisableAnimations, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(CurrentPair.UserData.AliasOrUID + " has " + (!otherDisableAnimations ? "not " : string.Empty) + "disabled animation sync with you");
        }

        if (ImGui.Checkbox("Disable VFX", ref disableVfx))
        {
            _ownPermissions.SetDisableVFX(disableVfx);
        }
        _uiSharedService.DrawHelpText("Disabling sounds will remove all VFX synced with this user on both sides." + UiSharedService.TooltipSeparator
            + "Note: this is bidirectional, either user disabling VFX sync will stop VFX sync on both sides.");
        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherDisableVFX, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(CurrentPair.UserData.AliasOrUID + " has " + (!otherDisableVFX ? "not " : string.Empty) + "disabled VFX sync with you");
        }

        ImGuiHelpers.ScaledDummy(0.5f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0.5f);

        bool hasChanges = _ownPermissions != CurrentPair.UserPair.OwnPermissions;

        using (ImRaii.Disabled(!hasChanges))
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Save"))
            {
                _ = _apiController.SetBulkPermissions(CurrentPair.ServerIndex, new(
                    new(StringComparer.Ordinal)
                    {
                        { CurrentPair.UserData.UID, _ownPermissions }
                    },
                    new(StringComparer.Ordinal)
                ));
            }
        UiSharedService.AttachToolTip("Save and apply all changes");

        var rightSideButtons = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Undo, "Revert") +
            _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.ArrowsSpin, "Reset to Default");
        var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;

        ImGui.SameLine(availableWidth - rightSideButtons);

        using (ImRaii.Disabled(!hasChanges))
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Undo, "Revert"))
            {
                _ownPermissions = CurrentPair.UserPair.OwnPermissions.DeepClone();
            }
        UiSharedService.AttachToolTip("Revert all changes");

        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowsSpin, "Reset to Default"))
        {
            var defaultPermissions = _apiController.GetDefaultPermissionsForServer(CurrentPair.ServerIndex)!;
            _ownPermissions.SetSticky(CurrentPair.IsDirectlyPaired || defaultPermissions.IndividualIsSticky);
            _ownPermissions.SetPaused(false);
            _ownPermissions.SetDisableVFX(CurrentPair.IsDirectlyPaired ? defaultPermissions.DisableIndividualVFX : defaultPermissions.DisableGroupVFX);
            _ownPermissions.SetDisableSounds(CurrentPair.IsDirectlyPaired ? defaultPermissions.DisableIndividualSounds : defaultPermissions.DisableGroupSounds);
            _ownPermissions.SetDisableAnimations(CurrentPair.IsDirectlyPaired ? defaultPermissions.DisableIndividualAnimations : defaultPermissions.DisableGroupAnimations);
            _ = _apiController.SetBulkPermissions(CurrentPair.ServerIndex, new(
                new(StringComparer.Ordinal)
                {
                    { CurrentPair.UserData.UID, _ownPermissions }
                },
                new(StringComparer.Ordinal)
            ));
        }
        UiSharedService.AttachToolTip("This will set all permissions to your defined default permissions in the Laci Synchroni Settings");

        var ySize = ImGui.GetCursorPosY() + style.FramePadding.Y * ImGuiHelpers.GlobalScale + style.FrameBorderSize;
        ImGui.SetWindowSize(new(400, ySize));
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}