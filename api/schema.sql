IF OBJECT_ID(N'dbo.ArcadiaUsers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ArcadiaUsers (
        UserId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ArcadiaUsers PRIMARY KEY,
        Email NVARCHAR(254) NOT NULL,
        NormalizedEmail NVARCHAR(254) NOT NULL,
        DisplayName NVARCHAR(80) NOT NULL,
        PasswordSalt VARBINARY(16) NOT NULL,
        PasswordHash VARBINARY(32) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_ArcadiaUsers_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_ArcadiaUsers_NormalizedEmail UNIQUE (NormalizedEmail)
    );
END;

IF OBJECT_ID(N'dbo.ArcadiaSessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ArcadiaSessions (
        TokenHash VARBINARY(32) NOT NULL CONSTRAINT PK_ArcadiaSessions PRIMARY KEY,
        UserId UNIQUEIDENTIFIER NOT NULL,
        ExpiresAt DATETIME2(0) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_ArcadiaSessions_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_ArcadiaSessions_Users FOREIGN KEY (UserId)
            REFERENCES dbo.ArcadiaUsers (UserId) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_ArcadiaSessions_UserId'
      AND object_id = OBJECT_ID(N'dbo.ArcadiaSessions')
)
    CREATE INDEX IX_ArcadiaSessions_UserId ON dbo.ArcadiaSessions (UserId);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_ArcadiaSessions_ExpiresAt'
      AND object_id = OBJECT_ID(N'dbo.ArcadiaSessions')
)
    CREATE INDEX IX_ArcadiaSessions_ExpiresAt ON dbo.ArcadiaSessions (ExpiresAt);

IF OBJECT_ID(N'dbo.ArcadiaLibraries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ArcadiaLibraries (
        UserId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ArcadiaLibraries PRIMARY KEY,
        Revision BIGINT NOT NULL,
        Payload NVARCHAR(MAX) NOT NULL,
        UpdatedAt DATETIME2(0) NOT NULL,
        CONSTRAINT FK_ArcadiaLibraries_Users FOREIGN KEY (UserId)
            REFERENCES dbo.ArcadiaUsers (UserId) ON DELETE CASCADE,
        CONSTRAINT CK_ArcadiaLibraries_Revision CHECK (Revision > 0),
        CONSTRAINT CK_ArcadiaLibraries_Payload CHECK (ISJSON(Payload) = 1)
    );
END;
