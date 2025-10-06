using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using LaciSynchroni.Common.Data.Extensions;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.UI.Components;
using LaciSynchroni.WebAPI;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace LaciSynchroni.UI;

public class StandaloneProfileUi : WindowMediatorSubscriberBase
{
    private readonly ProfileManager _profileManager;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly UiSharedService _uiSharedService;
    private readonly ApiController _apiController;
    private bool _adjustedForScrollBars = false;
    private byte[] _lastProfilePicture = [];
    private byte[] _lastSupporterPicture = [];
    private IDalamudTextureWrap? _supporterTextureWrap;
    private IDalamudTextureWrap? _textureWrap;
    private IEnumerable<Pair> _currentPairs { get; set; }
    private readonly ServerSelectorSmall _serverSelector;
    private int _serverForProfile = 0;
    private bool _singleProfile => _currentPairs.Count() == 1;

    public Pair CurrentPair { get; set; }

    public StandaloneProfileUi(ILogger<StandaloneProfileUi> logger, SyncMediator mediator, UiSharedService uiBuilder,
        ServerConfigurationManager serverManager, ProfileManager profileManager, PairManager pairManager, IEnumerable<Pair> pairs,
        ApiController apiController,
        PerformanceCollectorService performanceCollector)
        : base(logger, mediator, "Sync Profile of " + pairs.First().PlayerName + "##LaciSynchroniStandaloneProfileUI", performanceCollector)
    {
        _uiSharedService = uiBuilder;
        _serverManager = serverManager;
        _profileManager = profileManager;
        _currentPairs = pairs;
        _pairManager = pairManager;
        _apiController = apiController;
        _serverSelector = new ServerSelectorSmall(index => _serverForProfile = index);

        CurrentPair = pairs.First();

        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize;
        var spacing = ImGui.GetStyle().ItemSpacing;
        Size = new(512 + spacing.X * 3 + ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().WindowBorderSize, 512);

        IsOpen = true;
    }

    protected override void DrawInternal()
    {
        try
        {
            var spacing = ImGui.GetStyle().ItemSpacing;

            if (!_singleProfile)
            {
                var serverIndexes = _currentPairs.Select(p => p.ServerIndex).ToArray();
                _serverSelector.Draw(_serverManager.GetServerNamesByIndexes(serverIndexes), serverIndexes, 512);
                CurrentPair = _currentPairs?.FirstOrDefault(p => p.ServerIndex == _serverForProfile) ?? CurrentPair;
            }

            var msg = new ServerBasedUserKey(CurrentPair.UserData, CurrentPair.ServerIndex);
            var syncProfile = _profileManager.GetSyncProfile(msg);

            if (_textureWrap == null || !syncProfile.ImageData.Value.SequenceEqual(_lastProfilePicture))
            {
                _textureWrap?.Dispose();
                _lastProfilePicture = syncProfile.ImageData.Value;
                _textureWrap = _uiSharedService.LoadImage(_lastProfilePicture);
            }

            if (_supporterTextureWrap == null || !syncProfile.SupporterImageData.Value.SequenceEqual(_lastSupporterPicture))
            {
                _supporterTextureWrap?.Dispose();
                _supporterTextureWrap = null;
                if (!string.IsNullOrEmpty(syncProfile.Base64SupporterPicture))
                {
                    _lastSupporterPicture = syncProfile.SupporterImageData.Value;
                    _supporterTextureWrap = _uiSharedService.LoadImage(_lastSupporterPicture);
                }
            }

            var drawList = ImGui.GetWindowDrawList();
            var rectMin = drawList.GetClipRectMin();
            var rectMax = drawList.GetClipRectMax();
            var headerSize = ImGui.GetCursorPosY() - ImGui.GetStyle().WindowPadding.Y;

            using (_uiSharedService.UidFont.Push())
            {
                if (_singleProfile)
                {
                    var serverName = _apiController.GetServerNameByIndex(CurrentPair.ServerIndex);
                    UiSharedService.ColorText($"{CurrentPair.UserData.AliasOrUID} @ {serverName}", ImGuiColors.HealerGreen);
                }
                else
                {
                    UiSharedService.ColorText($"{CurrentPair.UserData.AliasOrUID}", ImGuiColors.HealerGreen);
                }
            }

            ImGuiHelpers.ScaledDummy(new Vector2(spacing.Y, spacing.Y));
            var textPos = ImGui.GetCursorPosY() - headerSize;
            ImGui.Separator();
            var pos = ImGui.GetCursorPos() with { Y = ImGui.GetCursorPosY() - headerSize };
            ImGuiHelpers.ScaledDummy(new Vector2(256, 256 + spacing.Y));
            var postDummy = ImGui.GetCursorPosY();
            ImGui.SameLine();
            var descriptionTextSize = ImGui.CalcTextSize(syncProfile.Description, wrapWidth: 256f);
            var descriptionChildHeight = rectMax.Y - pos.Y - rectMin.Y - spacing.Y * 2;
            if (descriptionTextSize.Y > descriptionChildHeight && !_adjustedForScrollBars)
            {
                Size = Size!.Value with { X = Size.Value.X + ImGui.GetStyle().ScrollbarSize };
                _adjustedForScrollBars = true;
            }
            else if (descriptionTextSize.Y < descriptionChildHeight && _adjustedForScrollBars)
            {
                Size = Size!.Value with { X = Size.Value.X - ImGui.GetStyle().ScrollbarSize };
                _adjustedForScrollBars = false;
            }
            var childFrame = ImGuiHelpers.ScaledVector2(256 + ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().WindowBorderSize, descriptionChildHeight);
            childFrame = childFrame with
            {
                X = childFrame.X + (_adjustedForScrollBars ? ImGui.GetStyle().ScrollbarSize : 0),
                Y = childFrame.Y / ImGuiHelpers.GlobalScale
            };
            if (ImGui.BeginChildFrame(1000, childFrame))
            {
                using var _ = _uiSharedService.GameFont.Push();
                ImGui.TextWrapped(syncProfile.Description);
            }
            ImGui.EndChildFrame();

            ImGui.SetCursorPosY(postDummy);
            var note = _serverManager.GetNoteForUid(CurrentPair.ServerIndex, CurrentPair.UserData.UID);
            if (!string.IsNullOrEmpty(note))
            {
                UiSharedService.ColorText(note, ImGuiColors.DalamudGrey);
            }
            string status = CurrentPair.IsVisible ? "Visible" : (CurrentPair.IsOnline ? "Online" : "Offline");
            UiSharedService.ColorText(status, (CurrentPair.IsVisible || CurrentPair.IsOnline) ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
            if (CurrentPair.IsVisible)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"({CurrentPair.PlayerName})");
            }
            if (CurrentPair.UserPair != null)
            {
                ImGui.TextUnformatted("Directly paired");
                if (CurrentPair.UserPair.OwnPermissions.IsPaused())
                {
                    ImGui.SameLine();
                    UiSharedService.ColorText("You: paused", ImGuiColors.DalamudYellow);
                }
                if (CurrentPair.UserPair.OtherPermissions.IsPaused())
                {
                    ImGui.SameLine();
                    UiSharedService.ColorText("They: paused", ImGuiColors.DalamudYellow);
                }
            }

            if (CurrentPair.UserPair?.Groups.Count > 0)
            {
                ImGui.TextUnformatted("Paired through Syncshells:");
                foreach (var group in CurrentPair.UserPair.Groups)
                {
                    var groupNote = _serverManager.GetNoteForGid(CurrentPair.ServerIndex, group);
                    var groupName = _pairManager.GroupPairs.First(f => string.Equals(f.Key.GroupFullInfo.GID, group, StringComparison.Ordinal)).Key.GroupFullInfo.GroupAliasOrGID;
                    var groupString = string.IsNullOrEmpty(groupNote) ? groupName : $"{groupNote} ({groupName})";
                    ImGui.TextUnformatted("- " + groupString);
                }
            }

            var padding = ImGui.GetStyle().WindowPadding.X / 2;
            bool tallerThanWide = _textureWrap.Height >= _textureWrap.Width;
            var stretchFactor = tallerThanWide ? 256f * ImGuiHelpers.GlobalScale / _textureWrap.Height : 256f * ImGuiHelpers.GlobalScale / _textureWrap.Width;
            var newWidth = _textureWrap.Width * stretchFactor;
            var newHeight = _textureWrap.Height * stretchFactor;
            var remainingWidth = (256f * ImGuiHelpers.GlobalScale - newWidth) / 2f;
            var remainingHeight = (256f * ImGuiHelpers.GlobalScale - newHeight) / 2f;
            drawList.AddImage(_textureWrap.Handle, new Vector2(rectMin.X + padding + remainingWidth, rectMin.Y + spacing.Y + pos.Y + remainingHeight),
                new Vector2(rectMin.X + padding + remainingWidth + newWidth, rectMin.Y + spacing.Y + pos.Y + remainingHeight + newHeight));
            if (_supporterTextureWrap != null)
            {
                const float iconSize = 38;
                drawList.AddImage(_supporterTextureWrap.Handle,
                    new Vector2(rectMax.X - iconSize - spacing.X, rectMin.Y + (textPos / 2) - (iconSize / 2)),
                    new Vector2(rectMax.X - spacing.X, rectMin.Y + iconSize + (textPos / 2) - (iconSize / 2)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during draw tooltip");
        }
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}