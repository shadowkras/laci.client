using LaciSynchroni.Common.Dto.Files;

namespace LaciSynchroni.WebAPI.Files.Models;

public class DownloadFileTransfer : FileTransfer
{
    public DownloadFileTransfer(DownloadFileDto dto, int serverIndex) : base(dto, serverIndex)
    {
    }

    public override bool CanBeTransferred => Dto.FileExists && !Dto.IsForbidden && Dto.Size > 0;
    public Uri DownloadUri => new(Dto.DirectDownloadUrl ?? Dto.Url);
    public override long Total
    {
        set
        {
            // nothing to set
        }
        get => Dto.Size;
    }

    public long TotalRaw => Dto.RawSize;
    private DownloadFileDto Dto => (DownloadFileDto)TransferDto;
}