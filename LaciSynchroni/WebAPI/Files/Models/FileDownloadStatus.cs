using LaciSynchroni.Common.Dto.Files;

namespace LaciSynchroni.WebAPI.Files.Models;

public class FileDownloadStatus
{
    public readonly int ServerIndex;
    public string Hash { get; set; } = string.Empty;
    public DownloadStatus DownloadStatus { get; set; }
    public long TotalBytes { get; set; }
    public int TotalFiles { get; set; }
    public long TransferredBytes { get; set; }
    public int TransferredFiles { get; set; }

    public FileDownloadStatus(int serverIndex)
    {
        ServerIndex = serverIndex;
    }
}