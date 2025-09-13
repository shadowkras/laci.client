using LaciSynchroni.Common.Dto.Files;

namespace LaciSynchroni.WebAPI.Files.Models;

public class UploadFileTransfer : FileTransfer
{
    public UploadFileTransfer(UploadFileDto dto, int serverIndex) : base(dto, serverIndex)
    {
    }

    public string LocalFile { get; set; } = string.Empty;
    public override long Total { get; set; }
}