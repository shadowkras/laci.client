using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.UI.Handlers;
using System.Numerics;

namespace LaciSynchroni.UI.Components;

public class SelectPairForTagUi
{
    private readonly TagHandler _tagHandler;
    private readonly IdDisplayHandler _uidDisplayHandler;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private string _filter = string.Empty;
    private bool _opened = false;
    private HashSet<string> _peopleInGroup = new(StringComparer.Ordinal);
    private bool _show = false;
    private string _tag = string.Empty;
    private int _serverIndex = -1;

    public SelectPairForTagUi(TagHandler tagHandler, IdDisplayHandler uidDisplayHandler, ServerConfigurationManager serverConfigurationManager)
    {
        _tagHandler = tagHandler;
        _uidDisplayHandler = uidDisplayHandler;
        _serverConfigurationManager = serverConfigurationManager;
    }

    public void Draw(List<Pair> pairs)
    {
        var workHeight = ImGui.GetMainViewport().WorkSize.Y / ImGuiHelpers.GlobalScale;
        var minSize = new Vector2(400, workHeight < 400 ? workHeight : 400) * ImGuiHelpers.GlobalScale;
        var maxSize = new Vector2(400, 1000) * ImGuiHelpers.GlobalScale;

        var popupName = $"Choose Users for Group {_tag}";

        if (!_show)
        {
            _opened = false;
        }

        if (_show && !_opened)
        {
            ImGui.SetNextWindowSize(minSize);
            UiSharedService.CenterNextWindow(minSize.X, minSize.Y, ImGuiCond.Always);
            ImGui.OpenPopup(popupName);
            _opened = true;
        }

        ImGui.SetNextWindowSizeConstraints(minSize, maxSize);
        if (ImGui.BeginPopupModal(popupName, ref _show, ImGuiWindowFlags.Popup | ImGuiWindowFlags.Modal))
        {
            var serverName = _serverConfigurationManager.GetServerNameByIndex(_serverIndex);
            ImGui.TextUnformatted($"Select users for group {_tag} on server {serverName}");

            ImGui.InputTextWithHint("##filter", "Filter", ref _filter, 255, ImGuiInputTextFlags.None);
            foreach (var item in pairs
                .Where(IsRelevant)
                .OrderBy(PairName, StringComparer.OrdinalIgnoreCase)
                .ToList())
            {
                var isInGroup = _peopleInGroup.Contains(item.UserData.UID);
                if (ImGui.Checkbox(PairName(item), ref isInGroup))
                {
                    if (isInGroup)
                    {
                        _tagHandler.AddTagToPairedUid(_serverIndex, item.UserData.UID, _tag);
                        _peopleInGroup.Add(item.UserData.UID);
                    }
                    else
                    {
                        _tagHandler.RemoveTagFromPairedUid(_serverIndex, item.UserData.UID, _tag);
                        _peopleInGroup.Remove(item.UserData.UID);
                    }
                }
            }
            ImGui.EndPopup();
        }
        else
        {
            _filter = string.Empty;
            _show = false;
        }
    }

    public void Open(int serverIndex, string tag)
    {
        _peopleInGroup = _tagHandler.GetOtherUidsForTag(serverIndex, tag);
        _tag = tag;
        _show = true;
        _serverIndex = serverIndex;
    }

    private string PairName(Pair pair)
    {
        return _uidDisplayHandler.GetPlayerText(pair).text;
    }

    private bool IsRelevant(Pair pair)
    {
        if (pair.ServerIndex != _serverIndex)
        {
            // Different server => can't show
            return false;
        }
        if (string.IsNullOrEmpty(_filter))
        {
            return true;
        }

        return PairName(pair).Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }
}