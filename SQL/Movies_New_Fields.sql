-- =====================================================
-- Movies 表字段扩展 SQL 语句
-- 基于 TMDbLib.Objects.Movies.Movie 和用户需求新增字段
-- =====================================================

-- 1. TMDB/IMDB 标识字段
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS ImdbId NVARCHAR(50);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS Tagline NVARCHAR(500);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS Homepage NVARCHAR(500);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS Status NVARCHAR(50);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS IsAdult INTEGER DEFAULT 0;
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS IsVideo INTEGER DEFAULT 0;
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS BelongsToCollection NVARCHAR(500);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS Budget INTEGER;
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS Revenue INTEGER;
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS Popularity REAL;
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS VoteCount INTEGER;
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS OriginalLanguage NVARCHAR(10);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS ProductionCompanies NVARCHAR(2000);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS ProductionCountries NVARCHAR(1000);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS OriginCountry NVARCHAR(1000);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS Keywords NVARCHAR(2000);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS AlternativeTitles NVARCHAR(2000);

-- 2. 上映信息
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS ReleaseDate TEXT;

-- 3. 视频技术字段
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS VideoFormat NVARCHAR(50);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS VideoBitrate INTEGER;
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS FrameRate REAL;
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS Width INTEGER;
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS Height INTEGER;
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS AspectRatio NVARCHAR(20);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS ColorSpace NVARCHAR(50);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS BitDepth NVARCHAR(20);

-- 4. 音频技术字段
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS AudioChannels NVARCHAR(50);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS AudioBitrate INTEGER;
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS AudioLanguages NVARCHAR(200);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS SubtitleFormats NVARCHAR(200);

-- 5. 演职人员
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS Writer NVARCHAR(2000);

-- 6. 播放状态
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS PlaybackPosition REAL;

-- 7. 向量数据库支持
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS EmbeddingText NVARCHAR(10000);
ALTER TABLE Movies ADD COLUMN IF NOT EXISTS EmbeddingAt TEXT;

-- =====================================================
-- 创建索引以优化查询性能
-- =====================================================
CREATE INDEX IF NOT EXISTS idx_movies_tmdbid ON Movies(TmdbId);
CREATE INDEX IF NOT EXISTS idx_movies_imdbid ON Movies(ImdbId);
CREATE INDEX IF NOT EXISTS idx_movies_releaseyear ON Movies(ReleaseYear);
CREATE INDEX IF NOT EXISTS idx_movies_releasedate ON Movies(ReleaseDate);
CREATE INDEX IF NOT EXISTS idx_movies_rating ON Movies(Rating);
CREATE INDEX IF NOT EXISTS idx_movies_votecount ON Movies(VoteCount);
CREATE INDEX IF NOT EXISTS idx_movies_popularity ON Movies(Popularity);
CREATE INDEX IF NOT EXISTS idx_movies_genres ON Movies(Genres);
CREATE INDEX IF NOT EXISTS idx_movies_resolution ON Movies(Resolution);
CREATE INDEX IF NOT EXISTS idx_movies_videocodec ON Movies(VideoCodec);
CREATE INDEX IF NOT EXISTS idx_movies_audiocodec ON Movies(AudioCodec);
CREATE INDEX IF NOT EXISTS idx_movies_director ON Movies(Director);
CREATE INDEX IF NOT EXISTS idx_movies_language ON Movies(Language);
CREATE INDEX IF NOT EXISTS idx_movies_country ON Movies(Country);
CREATE INDEX IF NOT EXISTS idx_movies_releasegroup ON Movies(ReleaseGroup);
CREATE INDEX IF NOT EXISTS idx_movies_istvseries ON Movies(IsTVSeries);

-- =====================================================
-- 完整建表语句（备用，如果需要重建表）
-- =====================================================
/*
CREATE TABLE IF NOT EXISTS Movies (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TmdbId NVARCHAR(50),
    ImdbId NVARCHAR(50),
    Title NVARCHAR(500) NOT NULL,
    OriginalTitle NVARCHAR(500),
    Overview NVARCHAR(4000),
    Tagline NVARCHAR(500),
    PosterPath NVARCHAR(500),
    BackdropPath NVARCHAR(500),
    ReleaseDate TEXT,
    ReleaseYear INTEGER,
    Rating REAL,
    VoteCount INTEGER,
    Popularity REAL,
    Runtime INTEGER,
    Genres NVARCHAR(1000),
    Homepage NVARCHAR(500),
    Status NVARCHAR(50),
    IsAdult INTEGER DEFAULT 0,
    IsVideo INTEGER DEFAULT 0,
    BelongsToCollection NVARCHAR(500),
    Budget INTEGER,
    Revenue INTEGER,
    OriginalLanguage NVARCHAR(10),
    ProductionCompanies NVARCHAR(2000),
    ProductionCountries NVARCHAR(1000),
    OriginCountry NVARCHAR(1000),
    Keywords NVARCHAR(2000),
    AlternativeTitles NVARCHAR(2000),
    FilePath NVARCHAR(2000) NOT NULL,
    FileSize INTEGER,
    VideoCodec NVARCHAR(50),
    VideoFormat NVARCHAR(50),
    VideoBitrate INTEGER,
    FrameRate REAL,
    AudioCodec NVARCHAR(100),
    AudioChannels NVARCHAR(50),
    AudioBitrate INTEGER,
    AudioLanguages NVARCHAR(200),
    Resolution NVARCHAR(20),
    Width INTEGER,
    Height INTEGER,
    AspectRatio NVARCHAR(20),
    HdrType NVARCHAR(50),
    ColorSpace NVARCHAR(50),
    BitDepth NVARCHAR(20),
    Director NVARCHAR(500),
    Writer NVARCHAR(2000),
    Cast NVARCHAR(4000),
    Language NVARCHAR(200),
    Country NVARCHAR(200),
    ReleaseGroup NVARCHAR(200),
    SubtitleFormats NVARCHAR(200),
    IsFavorite INTEGER DEFAULT 0,
    CreatedAt TEXT,
    UpdatedAt TEXT,
    Tags NVARCHAR(2000),
    IsWatched INTEGER DEFAULT 0,
    UserRating INTEGER,
    WatchedAt TEXT,
    PlaybackPosition REAL,
    IsTVSeries INTEGER DEFAULT 0,
    SeasonNumber INTEGER,
    EpisodeNumber INTEGER,
    EmbeddingText NVARCHAR(10000),
    EmbeddingAt TEXT
);
*/

-- =====================================================
-- ConversationRecords 表（AI对话记录）
-- =====================================================

-- 创建表（如果不存在）
CREATE TABLE IF NOT EXISTS ConversationRecords (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId NVARCHAR(100) NOT NULL DEFAULT 'default',
    UserMessage NVARCHAR(4000) NOT NULL DEFAULT '',
    AgentResponse NVARCHAR(8000) NOT NULL DEFAULT '',
    Timestamp TEXT,
    IsSummary INTEGER DEFAULT 0
);

-- 添加缺失的列
ALTER TABLE ConversationRecords ADD COLUMN IF NOT EXISTS UserId NVARCHAR(100) NOT NULL DEFAULT 'default';
ALTER TABLE ConversationRecords ADD COLUMN IF NOT EXISTS UserMessage NVARCHAR(4000) NOT NULL DEFAULT '';
ALTER TABLE ConversationRecords ADD COLUMN IF NOT EXISTS AgentResponse NVARCHAR(8000) NOT NULL DEFAULT '';
ALTER TABLE ConversationRecords ADD COLUMN IF NOT EXISTS Timestamp TEXT;
ALTER TABLE ConversationRecords ADD COLUMN IF NOT EXISTS IsSummary INTEGER DEFAULT 0;

-- 创建索引
CREATE INDEX IF NOT EXISTS idx_conversations_userid ON ConversationRecords(UserId);
CREATE INDEX IF NOT EXISTS idx_conversations_timestamp ON ConversationRecords(Timestamp);
