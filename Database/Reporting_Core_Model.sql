/*
    Reporting core model.
    Generated for review from Reporting COre.pdm and current sp2rdlGenExtension seed expectations.
*/

IF SCHEMA_ID(N'Reporting') IS NOT NULL
BEGIN
    THROW 51010, 'Reporting schema already exists. This bootstrap script is intended only for initial installation.', 1;
END;
GO

EXEC(N'CREATE SCHEMA [Reporting]');
GO

IF SCHEMA_ID(N'Localization') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [Localization]');
END;
GO

CREATE TABLE [Reporting].[ComponentType]
(
    [Id] int NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [CreatedBy] int NOT NULL,
    [LastModifiedBy] int NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_ComponentType_CreatedAt] DEFAULT (CURRENT_TIMESTAMP),
    [LastModifiedAt] datetime2(7) NULL,
    CONSTRAINT [PK_ComponentType] PRIMARY KEY CLUSTERED ([Id])
);
GO

CREATE TABLE [Reporting].[CompareOperator]
(
    [Id] tinyint NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [CreatedBy] int NOT NULL,
    [LastModifiedBy] int NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_CompareOperator_CreatedAt] DEFAULT (CURRENT_TIMESTAMP),
    [LastModifiedAt] datetime2(7) NULL,
    CONSTRAINT [PK_CompareOperator] PRIMARY KEY CLUSTERED ([Id])
);
GO

IF OBJECT_ID(N'[Localization].[Language]', N'U') IS NULL
BEGIN
    CREATE TABLE [Localization].[Language]
    (
        [Id] smallint IDENTITY(1,1) NOT NULL,
        [Code] nvarchar(10) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [CultureCode] nvarchar(20) NOT NULL,
        [IsRTL] bit NOT NULL CONSTRAINT [DF_Language_IsRTL] DEFAULT (0),
        CONSTRAINT [PK_Language] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [UNQ_Language_Code] UNIQUE ([Code]),
        CONSTRAINT [UNQ_Language_CultureCode] UNIQUE ([CultureCode])
    );
END;
GO

CREATE TABLE [Reporting].[Report]
(
    [Id] int IDENTITY(1,1) NOT NULL,
    [InternalName] varchar(100) NOT NULL,
    [Description] nvarchar(300) NOT NULL,
    [IsActive] bit NOT NULL CONSTRAINT [DF_Report_IsActive] DEFAULT (1),
    [CreatedBy] int NOT NULL,
    [LastModifiedBy] int NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Report_CreatedAt] DEFAULT (CURRENT_TIMESTAMP),
    [LastModifiedAt] datetime2(7) NULL,
    CONSTRAINT [PK_Report] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UNQ_Report_InternalName] UNIQUE ([InternalName])
);
GO

CREATE TABLE [Reporting].[Category]
(
    [Id] smallint NOT NULL,
    [ParentCategoryId] smallint NULL,
    [Name] nvarchar(200) NOT NULL,
    [SortOrder] int NOT NULL,
    [IsActive] bit NOT NULL CONSTRAINT [DF_Category_IsActive] DEFAULT (1),
    [CreatedBy] int NOT NULL,
    [LastModifiedBy] int NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Category_CreatedAt] DEFAULT (CURRENT_TIMESTAMP),
    [LastModifiedAt] datetime2(7) NULL,
    CONSTRAINT [PK_Category] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Category_Category] FOREIGN KEY ([ParentCategoryId]) REFERENCES [Reporting].[Category] ([Id])
);
GO

CREATE TABLE [Reporting].[ParameterDefinition]
(
    [Id] int IDENTITY(1,1) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [Label] nvarchar(200) NULL,
    [ComponentTypeId] int NOT NULL,
    [EntityKey] nvarchar(500) NOT NULL,
    [ValueFieldTemplate] nvarchar(200) NOT NULL,
    [DisplayFieldTemplate] nvarchar(300) NOT NULL,
    [InitialValue] nvarchar(1000) NULL,
    [DefaultSortOrder] int NULL,
    [StaticFilters] nvarchar(4000) NULL,
    [CreatedBy] int NOT NULL,
    [LastModifiedBy] int NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_ParameterDefinition_CreatedAt] DEFAULT (CURRENT_TIMESTAMP),
    [LastModifiedAt] datetime2(7) NULL,
    CONSTRAINT [PK_ParameterDefinition] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UNQ_ParameterDefinition_Name] UNIQUE ([Name]),
    CONSTRAINT [FK_ParameterDefinition_ComponentType] FOREIGN KEY ([ComponentTypeId]) REFERENCES [Reporting].[ComponentType] ([Id])
);
GO

CREATE TABLE [Reporting].[ReportVersion]
(
    [Id] int IDENTITY(1,1) NOT NULL,
    [ReportId] int NOT NULL,
    [LanguageId] smallint NULL,
    [Number] int NOT NULL,
    [Code] varchar(50) NOT NULL,
    [Label] nvarchar(50) NULL,
    [DescriptionOverride] nvarchar(300) NULL,
    [IsRemote] bit NOT NULL CONSTRAINT [DF_ReportVersion_IsRemote] DEFAULT (1),
    [Path] nvarchar(300) NULL,
    [EmbedFonts] bit NOT NULL CONSTRAINT [DF_ReportVersion_EmbedFonts] DEFAULT (CONVERT(bit, 1)),
    [ValidFrom] datetime2(7) NOT NULL CONSTRAINT [DF_ReportVersion_ValidFrom] DEFAULT (CURRENT_TIMESTAMP),
    [ValidTo] datetime2(7) NULL CONSTRAINT [DF_ReportVersion_ValidTo] DEFAULT (CAST('9999-12-31' AS date)),
    [CreatedBy] int NOT NULL,
    [LastModifiedBy] int NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_ReportVersion_CreatedAt] DEFAULT (CURRENT_TIMESTAMP),
    [LastModifiedAt] datetime2(7) NULL,
    CONSTRAINT [PK_ReportVersion] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UNQ_ReportVersion_RepNumLang] UNIQUE ([ReportId], [Number], [LanguageId]),
    CONSTRAINT [FK_ReportVersion_Report] FOREIGN KEY ([ReportId]) REFERENCES [Reporting].[Report] ([Id])
);
GO

IF OBJECT_ID(N'[Reporting].[FK_ReportVersion_Language]', N'F') IS NULL
BEGIN
    ALTER TABLE [Reporting].[ReportVersion] WITH CHECK
    ADD CONSTRAINT [FK_ReportVersion_Language]
        FOREIGN KEY ([LanguageId]) REFERENCES [Localization].[Language] ([Id]);
END;
GO

CREATE TRIGGER [Reporting].[TR_ReportVersion_ClosePreviousIntervals]
ON [Reporting].[ReportVersion]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT (UPDATE(ReportId) OR UPDATE(LanguageId) OR UPDATE(ValidFrom))
    BEGIN
        RETURN;
    END;

    UPDATE ExistingVersion
       SET ValidTo = DATEADD(day, -1, NewVersion.MinValidFrom),
           LastModifiedAt = SYSDATETIME()
    FROM [Reporting].[ReportVersion] AS ExistingVersion
    CROSS APPLY
    (
        SELECT MIN(CONVERT(date, InsertedVersion.ValidFrom)) AS MinValidFrom
        FROM inserted AS InsertedVersion
        WHERE InsertedVersion.ReportId = ExistingVersion.ReportId
          AND (InsertedVersion.LanguageId = ExistingVersion.LanguageId OR (InsertedVersion.LanguageId IS NULL AND ExistingVersion.LanguageId IS NULL))
          AND InsertedVersion.Id <> ExistingVersion.Id
          AND CONVERT(date, InsertedVersion.ValidFrom) > CONVERT(date, ExistingVersion.ValidFrom)
          AND CONVERT(date, InsertedVersion.ValidFrom) <= CONVERT(date, COALESCE(ExistingVersion.ValidTo, CONVERT(datetime2(7), '99991231', 112)))
    ) AS NewVersion
    WHERE NewVersion.MinValidFrom IS NOT NULL;
END;
GO

CREATE TABLE [Reporting].[ReportCategory]
(
    [ReportId] int NOT NULL,
    [CategoryId] smallint NOT NULL,
    [SortOrder] int NOT NULL,
    [IsVisible] bit NOT NULL CONSTRAINT [DF_ReportCategory_IsVisible] DEFAULT (1),
    [CreatedBy] int NOT NULL,
    [LastModifiedBy] int NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_ReportCategory_CreatedAt] DEFAULT (CURRENT_TIMESTAMP),
    [LastModifiedAt] datetime2(7) NULL,
    CONSTRAINT [PK_ReportCategoryReport] PRIMARY KEY CLUSTERED ([ReportId], [CategoryId]),
    CONSTRAINT [FK_ReportCategory_Report] FOREIGN KEY ([ReportId]) REFERENCES [Reporting].[Report] ([Id]),
    CONSTRAINT [FK_ReportCategory_Category] FOREIGN KEY ([CategoryId]) REFERENCES [Reporting].[Category] ([Id])
);
GO

CREATE TABLE [Reporting].[UiParameter]
(
    [Id] int IDENTITY(1,1) NOT NULL,
    [VersionId] int NOT NULL,
    [ParameterDefinitionId] int NOT NULL,
    [CreationOrder] tinyint NULL,
    [InitialValueOverride] nvarchar(1000) NULL,
    [LabelOverride] nvarchar(200) NULL,
    [DisplayFieldTemplateOverride] nvarchar(300) NULL,
    [IsRequired] bit NOT NULL CONSTRAINT [DF_UiParameter_IsRequired] DEFAULT (0),
    [IsAdditional] bit NOT NULL CONSTRAINT [DF_UiParameter_IsAdditional] DEFAULT (0),
    [DefaultSortOrderOverride] int NULL,
    [IsVisible] bit NOT NULL CONSTRAINT [DF_UiParameter_IsVisible] DEFAULT (1),
    [StaticValues] nvarchar(4000) NULL,
    [RuntimeSettings] nvarchar(4000) NULL,
    [CreatedBy] int NOT NULL,
    [LastModifiedBy] int NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_UiParameter_CreatedAt] DEFAULT (CURRENT_TIMESTAMP),
    [LastModifiedAt] datetime2(7) NULL,
    CONSTRAINT [PK_UiParameter] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [CK_UiParameter_RuntimeSettings_IsJson] CHECK ([RuntimeSettings] IS NULL OR ISJSON([RuntimeSettings]) = 1),
    CONSTRAINT [FK_UiParameter_ReportVersion] FOREIGN KEY ([VersionId]) REFERENCES [Reporting].[ReportVersion] ([Id]),
    CONSTRAINT [FK_UiParameter_ParameterDefinition] FOREIGN KEY ([ParameterDefinitionId]) REFERENCES [Reporting].[ParameterDefinition] ([Id])
);
GO

CREATE TABLE [Reporting].[UiParameterDependency]
(
    [Id] int IDENTITY(1,1) NOT NULL,
    [UiParameterId] int NOT NULL,
    [DependsOnUiParameterId] int NOT NULL,
    [CompareParams] bit NOT NULL CONSTRAINT [DF_UiParameterDependency_CompareParams] DEFAULT (0),
    [CompareOperatorId] tinyint NULL,
    [DependencyFilterPath] nvarchar(200) NULL,
    [CreatedBy] int NOT NULL,
    [LastModifiedBy] int NULL,
    [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_UiParameterDependency_CreatedAt] DEFAULT (CURRENT_TIMESTAMP),
    [LastModifiedAt] datetime2(7) NULL,
    CONSTRAINT [PK_UiParameterDependency] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UNQ_UIParameterDependency] UNIQUE ([UiParameterId], [DependsOnUiParameterId]),
    CONSTRAINT [CK_UiParameterDependency_NoSelfDependency] CHECK ([UiParameterId] <> [DependsOnUiParameterId]),
    CONSTRAINT [CK_UiParameterDependency_CompareOperatorRequired] CHECK
    (
        ([CompareParams] = 0 AND [CompareOperatorId] IS NULL)
        OR ([CompareParams] = 1 AND [CompareOperatorId] IS NOT NULL)
    ),
    CONSTRAINT [FK_UiParameterDependency_UiParameter] FOREIGN KEY ([UiParameterId]) REFERENCES [Reporting].[UiParameter] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_UiParameterDependency_DependsOnUiParameter] FOREIGN KEY ([DependsOnUiParameterId]) REFERENCES [Reporting].[UiParameter] ([Id]),
    CONSTRAINT [FK_UIParameterDependency_CompareOperator] FOREIGN KEY ([CompareOperatorId]) REFERENCES [Reporting].[CompareOperator] ([Id])
);
GO

DECLARE @SystemUserId int = 0;
DECLARE @Now datetime2(7) = SYSDATETIME();

MERGE [Reporting].[ComponentType] AS T
USING (VALUES
    (1, N'Text'),
    (2, N'Integer'),
    (3, N'Decimal'),
    (4, N'Date'),
    (5, N'DateTime'),
    (6, N'Checkbox'),
    (7, N'Dropdown'),
    (8, N'MultiSelect')
) AS S(Id, [Name])
   ON T.Id = S.Id
WHEN MATCHED THEN
    UPDATE SET [Name] = S.[Name], LastModifiedBy = @SystemUserId, LastModifiedAt = @Now
WHEN NOT MATCHED THEN
    INSERT (Id, [Name], CreatedBy, CreatedAt)
    VALUES (S.Id, S.[Name], @SystemUserId, @Now);
GO

DECLARE @SystemUserId int = 0;
DECLARE @Now datetime2(7) = SYSDATETIME();

MERGE [Reporting].[CompareOperator] AS T
USING (VALUES
    (1, N'>='),
    (2, N'<='),
    (3, N'>'),
    (4, N'<'),
    (5, N'=')
) AS S(Id, [Name])
   ON T.Id = S.Id
WHEN MATCHED THEN
    UPDATE SET [Name] = S.[Name], LastModifiedBy = @SystemUserId, LastModifiedAt = @Now
WHEN NOT MATCHED THEN
    INSERT (Id, [Name], CreatedBy, CreatedAt)
    VALUES (S.Id, S.[Name], @SystemUserId, @Now);
GO

SET IDENTITY_INSERT [Localization].[Language] ON;
GO

DECLARE @LanguageSeed TABLE
(
    Id smallint NOT NULL,
    Code nvarchar(10) NOT NULL,
    Name nvarchar(100) NOT NULL,
    CultureCode nvarchar(20) NOT NULL,
    IsRTL bit NOT NULL
);

INSERT INTO @LanguageSeed (Id, Code, Name, CultureCode, IsRTL)
VALUES
    (1, N'sr-Cyrl', N'Srpski ćirilica', N'sr-Cyrl-BA', 0),
    (2, N'sr-Latn', N'Srpski latinica', N'sr-Latn-BA', 0),
    (3, N'hr', N'Hrvatski', N'hr-HR', 0),
    (4, N'bs', N'Bosanski', N'bs-BA', 0),
    (5, N'en', N'English', N'en-US', 0);

MERGE [Localization].[Language] AS T
USING @LanguageSeed AS S
   ON T.Id = S.Id
WHEN MATCHED THEN
    UPDATE SET
        Code = S.Code,
        Name = S.Name,
        CultureCode = S.CultureCode,
        IsRTL = S.IsRTL
WHEN NOT MATCHED THEN
    INSERT (Id, Code, Name, CultureCode, IsRTL)
    VALUES (S.Id, S.Code, S.Name, S.CultureCode, S.IsRTL);
GO

SET IDENTITY_INSERT [Localization].[Language] OFF;
GO
