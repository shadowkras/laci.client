using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services;
using LaciSynchroni.SyncConfiguration;
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

    private static readonly VisibleGroupKeyComparer GroupKeyComparer = VisibleGroupKeyComparer.Instance;
    private IImmutableList<DrawUserPair>? _lastDrawPairsRef;
    private List<GroupBucket>? _cachedBuckets;

    public DrawVisibleTagFolder(
        IImmutableList<DrawUserPair> drawPairs,
        IImmutableList<Pair> allPairs,
        TagHandler tagHandler,
        UiSharedService uiSharedService,
        ApiController apiController,
        SyncConfigService configService)
        : base(TagHandler.CustomVisibleTag, drawPairs, allPairs, tagHandler, uiSharedService)
    {
        _apiController = apiController;
        _configService = configService;
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

        var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        var open = ImGui.TreeNodeEx($"##visible-header-{key.WorldId}-{displayName}", flags);

        ImGui.SameLine();
        _uiSharedService.IconText(FontAwesomeIcon.Eye, ImGuiColors.ParsedGreen);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(headerText);

        if (ImGui.IsItemHovered())
        {
            UiSharedService.AttachToolTip(BuildTooltip(worldName, key.WorldId, members));
        }

        if (!open)
        {
            return;
        }

        ImGui.TreePush(string.Empty);
        using var indent = ImRaii.PushIndent(ImGui.GetTreeNodeToLabelSpacing());
        foreach (var member in members)
        {
            var serverName = _apiController.GetServerNameByIndex(member.Pair.ServerIndex);
            member.DrawPairedClient(serverName);
        }
        ImGui.TreePop();
    }

    private string BuildTooltip(string? worldName, ushort worldId, List<DrawUserPair> members)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(worldName))
        {
            sb.Append("Home World: ").Append(worldName).Append(' ').Append('(').Append(worldId).Append(')');
        }
        else
        {
            sb.Append("Home World ID: ").Append(worldId);
        }

        sb.AppendLine();
        sb.AppendLine("Connections:");

        foreach (var member in members)
        {
            var serverName = _apiController.GetServerNameByIndex(member.Pair.ServerIndex);
            sb.Append("• ").Append(serverName).AppendLine();
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

