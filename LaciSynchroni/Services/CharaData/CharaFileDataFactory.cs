using LaciSynchroni.Common.Data;
using LaciSynchroni.FileCache;
using LaciSynchroni.Services.CharaData.Models;

namespace LaciSynchroni.Services.CharaData;

public sealed class CharaFileDataFactory
{
    private readonly FileCacheManager _fileCacheManager;

    public CharaFileDataFactory(FileCacheManager fileCacheManager)
    {
        _fileCacheManager = fileCacheManager;
    }

    public CharaFileData Create(string description, CharacterData characterCacheDto)
    {
        return new CharaFileData(_fileCacheManager, description, characterCacheDto);
    }
}