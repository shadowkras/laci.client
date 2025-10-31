using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using LaciSynchroni.Common.Data.Enum;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services;
using LaciSynchroni.SyncConfiguration;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.UI.Handlers;
using LaciSynchroni.WebAPI;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace LaciSynchroni.UI.Components;

public class DrawVisibleTagFolder : DrawCustomTag
{
    private readonly ApiController _apiController;
    private readonly SyncConfigService _configService;
    private readonly SyncMediator _mediator;

    private static readonly VisibleGroupKeyComparer GroupKeyComparer = VisibleGroupKeyComparer.Instance;
    private IImmutableList<DrawUserPair>? _lastDrawPairsRef;
    private List<GroupBucket>? _cachedBuckets;
    private readonly HashSet<VisibleGroupKey> _openGroups = new(GroupKeyComparer);

    public DrawVisibleTagFolder(
        IImmutableList<DrawUserPair> drawPairs,
        IImmutableList<Pair> allPairs,
        TagHandler tagHandler,
        UiSharedService uiSharedService,
        ApiController apiController,
        SyncConfigService configService,
        SyncMediator mediator)
        : base(TagHandler.CustomVisibleTag, drawPairs, allPairs, tagHandler, uiSharedService)
    {
        _apiController = apiController;
        _configService = configService;
        _mediator = mediator;
    }

    protected override void DrawOpenedContent()
    {
        var config = _configService.Current;

        if (!config.ShowCharacterNameInsteadOfNotesForVisible || config.PreferNotesOverNamesForVisible)
        {
            base.DrawOpenedContent();
            return;
        }

        if (!DrawPairs.Any())
        {
            ImGui.TextUnformatted("No users (online)");
            ImGui.Separator();
            return;
        }

        var buckets = GetOrBuildBuckets();

        foreach (var bucket in buckets)
        {
            if (bucket.Key is null || bucket.Members.Count <= 1)
            {
                bucket.Members[0].DrawPairedClient();
                continue;
            }

            DrawGroupedVisibleCharacter(bucket.Key.Value, bucket.Members);
        }

        ImGui.Separator();
    }

    private List<GroupBucket> GetOrBuildBuckets()
    {
        if (ReferenceEquals(_lastDrawPairsRef, DrawPairs) && _cachedBuckets != null)
        {
            return _cachedBuckets;
        }

        _lastDrawPairsRef = DrawPairs;

        var estimated = DrawPairs.Count;
        var orderedBuckets = new List<GroupBucket>(estimated);
        var groupedBuckets = new Dictionary<VisibleGroupKey, GroupBucket>(estimated, GroupKeyComparer);

        foreach (var drawPair in DrawPairs)
        {
            if (TryCreateKey(drawPair, out var key))
            {
                if (!groupedBuckets.TryGetValue(key, out var bucket))
                {
                    bucket = new GroupBucket(key);
                    groupedBuckets[key] = bucket;
                    orderedBuckets.Add(bucket);
                }

                bucket.Members.Add(drawPair);
            }
            else
            {
                var bucket = new GroupBucket(null);
                bucket.Members.Add(drawPair);
                orderedBuckets.Add(bucket);
            }
        }

        _cachedBuckets = orderedBuckets;
        return _cachedBuckets;
    }

    private bool TryCreateKey(DrawUserPair drawPair, out VisibleGroupKey key)
    {
        var playerName = drawPair.Pair.PlayerName;
        var homeWorld = drawPair.Pair.VisibleHomeWorldId;

        if (string.IsNullOrWhiteSpace(playerName) || homeWorld is null)
        {
            key = default;
            return false;
        }

        key = new VisibleGroupKey(playerName.Trim(), homeWorld.Value);
        return true;
    }

    private void DrawGroupedVisibleCharacter(VisibleGroupKey key, List<DrawUserPair> members)
    {
        using var id = ImRaii.PushId($"visible-group-{key.WorldId}-{key.Name}");

        string displayName = key.Name;
        string? worldName = _uiSharedService.WorldData.TryGetValue(key.WorldId, out var world)
            ? world
            : null;

        var serverCount = members.Count;
        var headerText = $"{displayName} ({serverCount} servers)";

        var isOpen = _openGroups.Contains(key);
        using (ImRaii.Child($"visible-group-row-{key.WorldId}-{key.Name}", new System.Numerics.Vector2(UiSharedService.GetWindowContentRegionWidth() - ImGui.GetCursorPosX(), ImGui.GetFrameHeight()), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.SetCursorPosX(0f);
            var caretIcon = isOpen ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight;

            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(caretIcon);
            if (ImGui.IsItemClicked())
            {
                if (isOpen)
                {
                    _openGroups.Remove(key);
                    isOpen = false;
                }
                else
                {
                    _openGroups.Add(key);
                    isOpen = true;
                }
            }

            ImGui.SameLine();
            using var eyeId = ImRaii.PushId($"eye-{key.WorldId}-{displayName}");
            _uiSharedService.IconText(FontAwesomeIcon.Eye, ImGuiColors.ParsedGreen);
            if (ImGui.IsItemHovered())
            {
                UiSharedService.AttachToolTip(BuildTooltip(displayName, worldName, members));
            }
            if (ImGui.IsItemClicked())
            {
                _mediator.Publish(new TargetPairMessage(members[0].Pair));
            }

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(headerText);
        }

        if (!isOpen)
        {
            return;
        }

        var caretWidth = _uiSharedService.GetIconSize(FontAwesomeIcon.CaretRight).X;
        var eyeWidth = _uiSharedService.GetIconSize(FontAwesomeIcon.Eye).X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var childIndent = caretWidth + spacing + eyeWidth + spacing;
        
        using var indent = ImRaii.PushIndent(childIndent, false);
        foreach (var member in members)
        {
            var serverName = _apiController.GetServerNameByIndex(member.Pair.ServerIndex);
            member.DrawPairedClient(serverName, showTooltip: false, showIcon: false);
        }
    }

    private string BuildTooltip(string displayName, string? worldName, List<DrawUserPair> members)
    {
        var sb = new StringBuilder();

        sb.Append(displayName).Append(' ').Append('@').Append(' ')
          .Append(!string.IsNullOrEmpty(worldName) ? worldName : "Home World");

        sb.Append(UiSharedService.TooltipSeparator);
        sb.Append("Connections:");

        bool anyVisible = members.Any(m => m.Pair.IsVisible);

        foreach (var member in members)
        {
            var pair = member.Pair;
            var serverName = _apiController.GetServerNameByIndex(pair.ServerIndex);
            
            sb.AppendLine().Append("• ").Append(serverName);

            string statusLine;
            if (pair.IsPaused)
            {
                statusLine = pair.UserData.AliasOrUID + " is paused";
            }
            else if (pair.IsVisible)
            {
                statusLine = pair.UserData.AliasOrUID + " is visible: " + pair.PlayerName;
            }
            else if (pair.IsOnline)
            {
                statusLine = pair.UserData.AliasOrUID + " is online";
            }
            else
            {
                statusLine = pair.UserData.AliasOrUID + " is offline";
            }
            sb.AppendLine().Append("  ").Append(statusLine);

            var pairingInfo = member.BuildPairingInfoSection(false);
            if (!string.IsNullOrEmpty(pairingInfo))
            {
                var pairingLines = pairingInfo.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in pairingLines)
                {
                    sb.AppendLine().Append("  ").Append(line);
                }
            }
        }

        if (anyVisible)
        {
            sb.AppendLine().Append("Click to target this player");
        }

        var any = members.Select(m => m.Pair).FirstOrDefault(p =>
            p.LastAppliedDataBytes >= 0 || p.LastAppliedApproximateVRAMBytes >= 0 || p.LastAppliedDataTris >= 0);
        if (any != null)
        {
            sb.Append(UiSharedService.TooltipSeparator);
            sb.Append(DrawUserPair.GetModInfoText(any));
        }

        return sb.ToString().TrimEnd();
    }

    private sealed class GroupBucket
    {
        public GroupBucket(VisibleGroupKey? key)
        {
            Key = key;
        }

        public VisibleGroupKey? Key { get; }
        public List<DrawUserPair> Members { get; } = new();
    }

    private readonly record struct VisibleGroupKey(string Name, ushort WorldId);

    private sealed class VisibleGroupKeyComparer : IEqualityComparer<VisibleGroupKey>
    {
        public static VisibleGroupKeyComparer Instance { get; } = new();

        public bool Equals(VisibleGroupKey x, VisibleGroupKey y)
        {
            return x.WorldId == y.WorldId
                   && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(VisibleGroupKey obj)
        {
            var hash = obj.WorldId.GetHashCode();
            hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
            return hash;
        }
    }
}

