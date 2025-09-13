using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using LaciSynchroni.Common.Data.Extensions;
using SinusSynchronous.PlayerData.Pairs;
using SinusSynchronous.Services.ServerConfiguration;
using SinusSynchronous.SinusConfiguration.Models;
using SinusSynchronous.UI.Handlers;
using SinusSynchronous.WebAPI;
using System.Collections.Immutable;

namespace SinusSynchronous.UI.Components;

public class DrawFolderTag : DrawFolderBase
{
    private readonly ApiController _apiController;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly TagHandler _tagHandler;
    private readonly SelectPairForTagUi _selectPairForTagUi;
    private readonly TagWithServerIndex _tag;

    public DrawFolderTag(TagWithServerIndex tag, IImmutableList<DrawUserPair> drawPairs, IImmutableList<Pair> allPairs,
        TagHandler tagHandler, ApiController apiController, SelectPairForTagUi selectPairForTagUi, UiSharedService uiSharedService, ServerConfigurationManager serverConfigManager)
        : base(drawPairs, allPairs, uiSharedService)
    {
        _apiController = apiController;
        _selectPairForTagUi = selectPairForTagUi;
        _serverConfigManager = serverConfigManager;
        _tagHandler = tagHandler;
        _tag = tag;
    }

    protected override bool RenderIfEmpty => true;
    protected override bool RenderMenu => true;
    protected override bool IsOpen => _tagHandler.IsTagOpen(_tag.ServerIndex, _tag.Tag);
    protected override string ComponentId => $"{_tag.ServerIndex}-{_tag.Tag}";
    

    protected override float DrawIcon()
    {
        ImGui.AlignTextToFramePadding();
        _uiSharedService.IconText(FontAwesomeIcon.Folder);
        AddTooltip();
        ImGui.SameLine();
        return ImGui.GetCursorPosX();
    }
    
    protected override void DrawMenu(float menuWidth)
    {
        ImGui.TextUnformatted("Group Menu");
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Users, "Select Pairs", menuWidth, true))
        {
            _selectPairForTagUi.Open(_tag.ServerIndex, _tag.Tag);
        }
        UiSharedService.AttachToolTip("Select Individual Pairs for this Pair Group");
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Delete Pair Group", menuWidth, true) && UiSharedService.CtrlPressed())
        {
            _tagHandler.RemoveTag(_tag.ServerIndex, _tag.Tag);
        }
        UiSharedService.AttachToolTip("Hold CTRL to remove this Group permanently." + Environment.NewLine +
            "Note: this will not unpair with users in this Group.");
    }

    protected override void DrawName(float width)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(_tag.Tag);
        AddTooltip();
    }

    protected override float DrawRightSide(float currentRightSideX)
    {
        if (!_allPairs.Any()) return currentRightSideX;

        var allArePaused = _allPairs.All(pair => pair.UserPair.OwnPermissions.IsPaused());
        var pauseButton = allArePaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var pauseButtonX = _uiSharedService.GetIconButtonSize(pauseButton).X;

        var buttonPauseOffset = currentRightSideX - pauseButtonX;
        ImGui.SameLine(buttonPauseOffset);
        if (_uiSharedService.IconButton(pauseButton))
        {
            if (allArePaused)
            {
                ResumeAllPairs(_allPairs);
            }
            else
            {
                PauseRemainingPairs(_allPairs);
            }
        }

        var action = allArePaused ? "Resume" : "Pause";
        UiSharedService.AttachToolTip($"{action} pairing with all pairs in {_tag}");
        return currentRightSideX;
    }

    protected override void ToggleOpen()
    {
        _tagHandler.ToggleTagOpen(_tag.ServerIndex, _tag.Tag);
    }

    private void AddTooltip()
    {
        var serverName = _serverConfigManager.GetServerNameByIndex(_tag.ServerIndex);
        var serverText = $"For server {serverName}";
        UiSharedService.AttachToolTip( serverText + Environment.NewLine + OnlinePairs + " online" + Environment.NewLine + TotalPairs + " total");
    }

    private void PauseRemainingPairs(IEnumerable<Pair> availablePairs)
    {
        foreach (IGrouping<int, Pair> grouping in availablePairs.GroupBy(pair => pair.ServerIndex, pair => pair))
        {
            _ = _apiController.SetBulkPermissions(grouping.Key, new(grouping
                    .ToDictionary(g => g.UserData.UID, g =>
                    {
                        var perm = g.UserPair.OwnPermissions;
                        perm.SetPaused(paused: true);
                        return perm;
                    }, StringComparer.Ordinal), new(StringComparer.Ordinal)))
                .ConfigureAwait(false);
        }
    }

    private void ResumeAllPairs(IEnumerable<Pair> availablePairs)
    {
        foreach (IGrouping<int, Pair> grouping in availablePairs.GroupBy(pair => pair.ServerIndex, pair => pair))
        {
            _ = _apiController.SetBulkPermissions(grouping.Key, new(grouping
                    .ToDictionary(g => g.UserData.UID, g =>
                    {
                        var perm = g.UserPair.OwnPermissions;
                        perm.SetPaused(paused: false);
                        return perm;
                    }, StringComparer.Ordinal), new(StringComparer.Ordinal)))
                .ConfigureAwait(false);
        }
    }
}