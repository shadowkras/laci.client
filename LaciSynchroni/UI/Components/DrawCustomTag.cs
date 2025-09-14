using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.UI.Handlers;
using System.Collections.Immutable;

namespace LaciSynchroni.UI.Components;

public class DrawCustomTag(
    string tag,
    IImmutableList<DrawUserPair> drawPairs,
    IImmutableList<Pair> allPairs,
    TagHandler tagHandler,
    UiSharedService uiSharedService)
    // server index is just used for component ID, we can pass any negative
    : DrawFolderBase(drawPairs, allPairs, uiSharedService)
{
    protected override bool RenderIfEmpty => tag switch
    {
        TagHandler.CustomUnpairedTag => false,
        TagHandler.CustomOnlineTag => false,
        TagHandler.CustomOfflineTag => false,
        TagHandler.CustomVisibleTag => false,
        TagHandler.CustomAllTag => true,
        TagHandler.CustomOfflineSyncshellTag => false,
        _ => throw new InvalidOperationException("Can only render custom tags")
    };

    protected override bool RenderMenu => false;
    protected override bool IsOpen => tagHandler.IsGlobalTagOpen(tag);
    protected override string ComponentId => tag;
    
    protected override float DrawIcon()
    {
        var icon = tag switch
        {
            TagHandler.CustomUnpairedTag => FontAwesomeIcon.ArrowsLeftRight,
            TagHandler.CustomOnlineTag => FontAwesomeIcon.Link,
            TagHandler.CustomOfflineTag => FontAwesomeIcon.Unlink,
            TagHandler.CustomOfflineSyncshellTag => FontAwesomeIcon.Unlink,
            TagHandler.CustomVisibleTag => FontAwesomeIcon.Eye,
            TagHandler.CustomAllTag => FontAwesomeIcon.User,
            _ => throw new InvalidOperationException("Can only render custom tags")
        };

        ImGui.AlignTextToFramePadding();
        _uiSharedService.IconText(icon);
        ImGui.SameLine();
        return ImGui.GetCursorPosX();
    }

    protected override void DrawMenu(float menuWidth)
    {
        // No Menu is drawn for custom tags
    }

    protected override void DrawName(float width)
    {
        ImGui.AlignTextToFramePadding();

        string name = tag switch
        {
            TagHandler.CustomUnpairedTag => "One-sided Individual Pairs",
            TagHandler.CustomOnlineTag => "Online / Paused by you",
            TagHandler.CustomOfflineTag => "Offline / Paused by other",
            TagHandler.CustomOfflineSyncshellTag => "Offline Syncshell Users",
            TagHandler.CustomVisibleTag => "Visible",
            TagHandler.CustomAllTag => "Users",
            _ => throw new InvalidOperationException("Can only render custom pairs"),
        };

        ImGui.TextUnformatted(name);
    }

    protected override float DrawRightSide(float currentRightSideX)
    {
        return currentRightSideX;
    }

    protected override void ToggleOpen()
    {
        tagHandler.ToggleGlobalTagOpen(tag);
    }
}