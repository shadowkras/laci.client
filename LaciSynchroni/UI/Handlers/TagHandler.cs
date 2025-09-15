using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration.Models;

namespace LaciSynchroni.UI.Handlers;

public class TagHandler
{
    public const string CustomAllTag = "Laci_All";
    public const string CustomOfflineTag = "Laci_Offline";
    public const string CustomOfflineSyncshellTag = "Laci_OfflineSyncshell";
    public const string CustomOnlineTag = "Laci_Online";
    public const string CustomUnpairedTag = "Laci_Unpaired";
    public const string CustomVisibleTag = "Laci_Visible";
    private readonly ServerConfigurationManager _serverConfigurationManager;

    public TagHandler(ServerConfigurationManager serverConfigurationManager)
    {
        _serverConfigurationManager = serverConfigurationManager;
    }

    public void AddTag(int serverIndex, string tag)
    {
        _serverConfigurationManager.AddTag(serverIndex, tag);
    }

    public void AddTagToPairedUid(int serverIndex, string uid, string tagName)
    {
        _serverConfigurationManager.AddTagForUid(serverIndex, uid, tagName);
    }

    public List<TagWithServerIndex> GetAllTagsSorted()
    {
        return _serverConfigurationManager.GetServerInfo()
            .SelectMany((_, index) =>
            {
                var tags = _serverConfigurationManager.GetServerAvailablePairTags(index);
                return tags.Select(tag => new TagWithServerIndex(index, tag));
            })
            .OrderBy(t => t.Tag, StringComparer.Ordinal)
            .ToList();
    }

    public List<string> GetAllTagsForServerSorted(int serverIndex)
    {
        return _serverConfigurationManager.GetServerAvailablePairTags(serverIndex)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    public HashSet<string> GetOtherUidsForTag(int serverIndex, string tag)
    {
        return _serverConfigurationManager.GetUidsForTag(serverIndex, tag);
    }

    public bool HasAnyTag(int serverIndex, string uid)
    {
        return _serverConfigurationManager.HasTags(serverIndex, uid);
    }

    public bool HasTag(int serverIndex, string uid, string tagName)
    {
        return _serverConfigurationManager.ContainsTag(serverIndex, uid, tagName);
    }

    /// <summary>
    /// Is this tag opened in the paired clients UI?
    /// </summary>
    /// <param name="serverIndex">server the tag belongs to</param>
    /// <param name="tag">the tag</param>
    /// <returns>open true/false</returns>
    public bool IsTagOpen(int serverIndex, string tag)
    {
        return _serverConfigurationManager.ContainsOpenPairTag(serverIndex, tag);
    }

    /// <summary>
    /// For tags tag are "global", for example the syncshell grouping folder. These are not actually tags, but internally
    /// used identifiers for UI elements that can be persistently opened/closed
    /// </summary>
    /// <param name="tag"></param>
    /// <returns></returns>
    public bool IsGlobalTagOpen(string tag)
    {
        return _serverConfigurationManager.ContainsGlobalOpenPairTag(tag);
    }

    public void RemoveTag(int serverIndex, string tag)
    {
        _serverConfigurationManager.RemoveTag(serverIndex, tag);
    }

    public void RemoveTagFromPairedUid(int serverIndex, string uid, string tagName)
    {
        _serverConfigurationManager.RemoveTagForUid(serverIndex, uid, tagName);
    }

    public void ToggleTagOpen(int serverIndex, string tag)
    {
        if (IsTagOpen(serverIndex, tag))
        {
            _serverConfigurationManager.RemoveOpenPairTag(serverIndex, tag);
        }
        else
        {
            _serverConfigurationManager.AddOpenPairTag(serverIndex, tag);
        }
    }

    public void ToggleGlobalTagOpen(string tag)
    {
        if (IsGlobalTagOpen(tag))
        {
            _serverConfigurationManager.RemoveOpenGlobalPairTag(tag);
        }
        else
        {
            _serverConfigurationManager.AddGlobalOpenPairTag(tag);
        }
    }
}