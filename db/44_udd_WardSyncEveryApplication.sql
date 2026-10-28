/*
    Property GPS: the ward sync offers EVERY workable application, one per App_Id.

    APPLIES TO UDD_KHATABTOA on the TEST DB server (DB-NEW-KHATA-TE), or to the laptop copy
    UDD_KHATABTOA_TEST on localhost\MSSQLSERVER2019. The guard refuses anything else, and refuses
    unless USP_S_GpsWardSync is the db/42 version (ULB + ward number, 8 parameters).

    WHY. Until now USP_S_GpsWardSync kept only the newest workable application per EPID
    (ROW_NUMBER() OVER (PARTITION BY App_MotherEPID ...) = 1), because the phone stored one record
    per EPID. App 1.3.0 stores one record per App_Id, so every application waiting for the RI
    (App_Status 10) - and every one QC returned (400) - is offered on its own. A rejected
    application moves to 11 and a submitted one to 13; both leave the list by themselves.

    WHAT CHANGES - USP_S_GpsWardSync only:
      - a new LAST parameter @OnePerEpid bit = 1.
          1 (the default, what app 1.2.0 gets because it does not send it): exactly as before.
            Without this, a 1.2.0 phone would be offered two applications for one EPID, could keep
            only one of them, and would download forever.
          0 (sent by app 1.3.0): every workable application in the ward.
      - the ward list joins BtoA_EPIDMetaData through EXISTS instead of JOIN, so an application
        with two metadata rows in the ward is still one row (#ward's primary key is App_Id).
      - the closed-list comment no longer mentions "superseded".
    Everything else - the filters (active, fee paid, status 10/400, ULB, ward, active site row),
    the counts, the batch, its columns, the closed list - is the db/42 text unchanged.
    NOT CHANGED: every other procedure, table, index and trigger.

    BACKUP AND UNDO. Section 2 saves the current text, SET options and GRANTs in
    dbo.ZZ_PropertyGpsApi_ProcBackup_44 (once; a re-run keeps the first copy). Section 5
    (commented out) puts it back. db/42's own backup table holds the text from BEFORE db/42,
    so it must not be used to undo this script.

    Sections: 1 guard, 2 backup, 3 the procedure, 4 checks (read-only), 5 undo (commented out).
*/

-------------------------------------------------------------------------------
-- 1. GUARD (SET NOEXEC ON stops every later batch in SSMS too)
-------------------------------------------------------------------------------
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')),
       Instance = CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName'));

DECLARE @why nvarchar(2000) = NULL;

IF NOT (   (DB_NAME() = N'UDD_KHATABTOA'
            AND CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) LIKE N'DB-NEW-KHATA-TE%')
        OR (DB_NAME() = N'UDD_KHATABTOA_TEST'
            AND CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName')) = N'MSSQLSERVER2019'))
    SET @why = N'Refusing to run: open UDD_KHATABTOA on the test DB server, or UDD_KHATABTOA_TEST on the laptop, first.';
ELSE IF OBJECT_ID(N'dbo.USP_S_GpsWardSync', N'P') IS NULL
    SET @why = N'Refusing to run: dbo.USP_S_GpsWardSync does not exist here. Run db/42 first. Nothing changed.';
ELSE IF NOT EXISTS (SELECT 1 FROM sys.parameters
                    WHERE object_id = OBJECT_ID(N'dbo.USP_S_GpsWardSync') AND name = N'@UlbCode')
    SET @why = N'Refusing to run: USP_S_GpsWardSync is not the db/42 (ULB + ward number) version. Run db/42 first. Nothing changed.';
ELSE IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.USP_S_GpsWardSync')) IS NULL
    SET @why = N'Refusing to run: no text can be saved for USP_S_GpsWardSync (encrypted?), so it could not be put back. Nothing changed.';

IF @why IS NOT NULL
BEGIN
    RAISERROR(N'%s', 16, 1, @why);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 2. BACKUP - what is there now, once. Undo (section 5) restores from here.
-------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ZZ_PropertyGpsApi_ProcBackup_44', N'U') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_ProcBackup_44
    (
        [name]                 sysname       NOT NULL PRIMARY KEY,
        [definition]           nvarchar(max) NULL,
        saved_at               datetime      NOT NULL CONSTRAINT DF_ZZ_PgpsProcBackup44 DEFAULT (GETDATE()),
        uses_ansi_nulls        bit           NULL,
        uses_quoted_identifier bit           NULL,
        grants                 nvarchar(max) NULL    -- GRANT / DENY statements, run again after a restore
    );
GO

-- Kept on a re-run, so it always holds what was there before db/44.
INSERT INTO dbo.ZZ_PropertyGpsApi_ProcBackup_44 ([name], [definition], uses_ansi_nulls, uses_quoted_identifier, grants)
SELECT N'USP_S_GpsWardSync', m.[definition], m.uses_ansi_nulls, m.uses_quoted_identifier, g.grants
FROM sys.sql_modules m
OUTER APPLY (SELECT grants = STRING_AGG(CONVERT(nvarchar(max),
                        CASE dp.[state] WHEN 'D' THEN N'DENY ' ELSE N'GRANT ' END
                        -- Pinned collations: see db/42 (Msg 4191 / 8124).
                        + dp.permission_name COLLATE DATABASE_DEFAULT
                        + N' ON dbo.' + QUOTENAME(ob.name) COLLATE DATABASE_DEFAULT
                        + N' TO ' + QUOTENAME(pr.name) COLLATE DATABASE_DEFAULT
                        + CASE dp.[state] WHEN 'W' THEN N' WITH GRANT OPTION' ELSE N'' END), N'; ')
             FROM sys.database_permissions dp
             JOIN sys.objects ob ON ob.object_id = dp.major_id
             JOIN sys.database_principals pr ON pr.principal_id = dp.grantee_principal_id
             WHERE dp.class = 1 AND dp.major_id = m.object_id AND dp.minor_id = 0) g
WHERE m.object_id = OBJECT_ID(N'dbo.USP_S_GpsWardSync')
  AND NOT EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_ProcBackup_44 b WHERE b.[name] = N'USP_S_GpsWardSync');
GO

SELECT [name], saved_at, HasText = CASE WHEN [definition] IS NULL THEN 0 ELSE 1 END, grants
FROM dbo.ZZ_PropertyGpsApi_ProcBackup_44;

-- Nothing changes unless the text is saved. In a batch of its own, so a failed backup batch
-- above still stops the rest.
IF NOT EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_ProcBackup_44
               WHERE [name] = N'USP_S_GpsWardSync' AND [definition] IS NOT NULL)
BEGIN
    RAISERROR('The backup is incomplete (see the grid above). Nothing after this point runs.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 3. USP_S_GpsWardSync - what this phone is missing, 100 applications at a time
--
--    Result sets, always all three, in this order:
--      1. ServerCount, LocalCount, MissingCount
--      2. the next batch (several rows per corner plot)
--      3. AppId, AppStatus for held App_Ids that are no longer workable here
--         (empty unless @IncludeClosed = 1; AppStatus NULL = no longer active)
-------------------------------------------------------------------------------
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [dbo].[USP_S_GpsWardSync]
    @UlbCode         int,
    @WardNumber      nvarchar(20),            -- ward number as text, matched trimmed
    @LocalCount      int           = 0,
    @KnownAppIds     nvarchar(max) = NULL,   -- comma-separated App_Ids the phone holds for this ward
    @ReturnedAppIds  nvarchar(max) = NULL,   -- of those, the ones the phone already shows as returned (400)
    @BatchSize       int           = 100,
    @IncludeClosed   bit           = 0,
    -- Of those, the ones the phone holds in an outdated shape (2026-10-05: road cards built
    -- from one road for all, before each row's own declaration was sent). Re-sent while
    -- still workable; still counted as held, so a closed one is reported closed.
    @RefreshAppIds   nvarchar(max) = NULL,
    -- db/44. 1 = only the newest workable application per EPID (app 1.2.0 and older keep one
    -- record per EPID); 0 = every workable application, one per App_Id (app 1.3.0).
    @OnePerEpid      bit           = 1
AS
BEGIN
    SET NOCOUNT ON;

    IF @BatchSize IS NULL OR @BatchSize < 1 OR @BatchSize > 100
        SET @BatchSize = 100;

    SET @WardNumber = LTRIM(RTRIM(@WardNumber));
    SET @OnePerEpid = ISNULL(@OnePerEpid, 1);

    CREATE TABLE #known    (App_Id int NOT NULL PRIMARY KEY);
    CREATE TABLE #returned (App_Id int NOT NULL PRIMARY KEY);
    CREATE TABLE #refresh  (App_Id int NOT NULL PRIMARY KEY);
    CREATE TABLE #ward     (App_Id int NOT NULL PRIMARY KEY, App_Status int NOT NULL, App_Cdte datetime NULL);
    CREATE TABLE #missing  (App_Id int NOT NULL PRIMARY KEY, App_Cdte datetime NULL);
    CREATE TABLE #batch    (App_Id int NOT NULL PRIMARY KEY);

    INSERT INTO #known (App_Id)
    SELECT DISTINCT v.App_Id
    FROM STRING_SPLIT(ISNULL(@KnownAppIds, N''), N',') s
    CROSS APPLY (SELECT App_Id = TRY_CAST(LTRIM(RTRIM(s.value)) AS int)) v
    WHERE v.App_Id IS NOT NULL;

    INSERT INTO #returned (App_Id)
    SELECT DISTINCT v.App_Id
    FROM STRING_SPLIT(ISNULL(@ReturnedAppIds, N''), N',') s
    CROSS APPLY (SELECT App_Id = TRY_CAST(LTRIM(RTRIM(s.value)) AS int)) v
    WHERE v.App_Id IS NOT NULL;

    INSERT INTO #refresh (App_Id)
    SELECT DISTINCT v.App_Id
    FROM STRING_SPLIT(ISNULL(@RefreshAppIds, N''), N',') s
    CROSS APPLY (SELECT App_Id = TRY_CAST(LTRIM(RTRIM(s.value)) AS int)) v
    WHERE v.App_Id IS NOT NULL;

    -- Every workable application in the ward: one row per App_Id. With @OnePerEpid = 1 only
    -- the newest per EPID, as before db/44. No IsPushedToGps here. The metadata is matched with
    -- EXISTS, so an application with two metadata rows in the ward is still one row.
    INSERT INTO #ward (App_Id, App_Status, App_Cdte)
    SELECT x.App_Id, x.App_Status, x.App_Cdte
    FROM (
        SELECT ap.App_Id, ap.App_Status, ap.App_Cdte,
               rn = ROW_NUMBER() OVER (PARTITION BY ap.App_MotherEPID ORDER BY ap.App_Id DESC)
        FROM dbo.BtoAMainApp ap WITH (NOLOCK)
        WHERE ap.App_Active = 1
          AND ap.isProcessingFeePaid = 1
          AND ap.App_Status IN (10, 400)
          AND ISNULL(ap.App_MotherEPID, N'') <> N''
          AND EXISTS (SELECT 1 FROM dbo.BtoA_EPIDMetaData md WITH (NOLOCK)
                      WHERE md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
                        AND md.MD_ULBCode = @UlbCode
                        AND LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId))) = @WardNumber)
          AND EXISTS (SELECT 1 FROM dbo.[BtoA_SiteDtls] sd WITH (NOLOCK)
                      WHERE sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1)
    ) x
    WHERE @OnePerEpid = 0 OR x.rn = 1;

    -- Missing: not on the phone at all, on it but not yet shown as returned by QC, or on it
    -- in an outdated shape the phone asked to have replaced.
    INSERT INTO #missing (App_Id, App_Cdte)
    SELECT w.App_Id, w.App_Cdte
    FROM #ward w
    WHERE NOT EXISTS (SELECT 1 FROM #known k WHERE k.App_Id = w.App_Id)
       OR (w.App_Status = 400 AND NOT EXISTS (SELECT 1 FROM #returned r WHERE r.App_Id = w.App_Id))
       OR EXISTS (SELECT 1 FROM #refresh f WHERE f.App_Id = w.App_Id);

    -- 1. Counts. LocalCount is echoed so the API log shows both sides of the comparison.
    SELECT ServerCount  = (SELECT COUNT(*) FROM #ward),
           LocalCount   = ISNULL(@LocalCount, 0),
           MissingCount = (SELECT COUNT(*) FROM #missing);

    -- 2. The next batch - oldest application first, as the queue is worked.
    INSERT INTO #batch (App_Id)
    SELECT TOP (@BatchSize) m.App_Id
    FROM #missing m
    ORDER BY m.App_Cdte, m.App_Id;

    -- The ULB and ward names, once: every row of the batch is in this ULB and ward.
    -- mst_AROMapping holds one row per street, with stray tabs and spaces in its text.
    DECLARE @UlbName nvarchar(500), @WardName nvarchar(500);
    SELECT TOP (1)
           @UlbName  = LTRIM(RTRIM(REPLACE(a.ULBName_EN,  CHAR(9), ''))),
           @WardName = LTRIM(RTRIM(REPLACE(a.WardName_EN, CHAR(9), '')))
    FROM UDD_MASTERDB.dbo.mst_AROMapping a WITH (NOLOCK)
    WHERE a.ULBCode = @UlbCode
      AND LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), ''))) = @WardNumber
    ORDER BY CASE WHEN a.WardName_EN IS NULL THEN 1 ELSE 0 END;

    SELECT DISTINCT
        ISNULL(ap.[App_Id], 0) AS AppID
        ,ap.[App_MotherEPID]   AS MotherEPID
        ,ap.[App_Type]         AS Type
        ,ap.[App_Status]       AS Status
        ,ap.[App_Remarks]      AS Remarks
        ,ap.[App_AdditionalInfo] AS AdditionalInfo
        ,ap.[App_DisplayId]    AS AppDisplayId
        ,CASE
            WHEN ap.[App_Source] IS NULL OR ap.[App_Source] = 'A' OR LEN(ap.[App_Source]) = 0 THEN 'BtoAKhata'
            WHEN ap.[App_Source] = 'newkhata' THEN 'SinglePlotApproval'
         END AS ApplicationType
        ,ap.[App_MotherSASID]  AS MotherSASID
        ,ISNULL(ap.[isProcessingFeePaid], 0) AS isProcessingFeePaid
        ,ap.[ProcessingFee]    AS ProcessingFee
        ,sd.[Site_Id]
        ,sd.[Site_App_Id]      AS ApplicationId
        ,sd.[Site_EPID_Id]     AS EpID
        -- ULB and ward number (db/42). ZoneId, WardId and GBAZoneName_En are no longer returned.
        ,CONVERT(int, md.MD_ULBCode) AS UlbCode
        ,@UlbName              AS UlbName
        ,LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId))) AS WardNumber
        ,@WardName             AS WardName
        -- The street as text only: street ids are text now (siteDetails.streetId). The raw
        -- Site_StreetId is not returned, so a text value there cannot break the API's int field.
        ,CONVERT(nvarchar(50), sd.Site_StreetId) AS StreetNumber
        ,srd.[Rd_Roadtype]     AS RoadType
        ,srd.[Rd_RoadId]       AS RoadId
        ,srd.[Rd_RoadName]     AS Roadname
        ,srd.Rd_isPresentInPublicRoadList
        ,srd.Rd_PrivateRoadId
        ,srd.Rd_PrivateRoadText
        ,srd.Rd_PrivateRoadName
        -- What the citizen typed for this road ("3rd Cross"), as opposed to Rd_RoadName,
        -- the street-master road it sits on or nearest to. Each road card needs its own.
        ,srd.Rd_EnteredRoadName
        ,sd.[Site_IsSameLocationAsKhata] AS IsSameLocationAsKhata
        ,sd.[Site_Latitude]    AS Latitude
        ,sd.[Site_Longitude]   AS Longitude
        ,sd.[Site_Order]       AS SiteOrder
        ,sd.[App_IsPropertySurvey]
        ,sd.[App_IsPropertySurveyLocated]
        ,md.MD_JSON            AS EpidJSON
        ,md.MD_KhataType       AS Khatatype
        ,md.MD_Latitude        AS KhataLatitude
        ,md.MD_Longitude       AS KhataLongitude
        ,ap.App_Cdte
        ,sd.DcConversionType
        ,sd.PrivateRoadId
        ,sd.PrivateRoadText
        ,sd.PrivateRoadName
        ,sd.Site_AdditionalInfo
        ,sd.publicRoadname
        ,sd.propertyUseType
        ,sd.comercialExtentinSqft
        ,sd.residentailsExtentinSqft
        ,sd.Site_isCornorPlot  AS isCornorPlot
        ,sd.Site_numberOfRoadFacingSides
        ,srd.Rd_RoadRow_ID     AS Rd_RoadRow_ID
    FROM #batch b
    JOIN dbo.BtoAMainApp ap WITH (NOLOCK) ON ap.App_Id = b.App_Id
    JOIN dbo.[BtoA_SiteDtls] sd WITH (NOLOCK) ON sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1
    JOIN dbo.BtoA_EPIDMetaData md WITH (NOLOCK)
      ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
    -- Active road rows only: a row the citizen's declaration no longer carries
    -- (Rd_RoadActive = 0) is not a road for the officer to verify. NULL counts as active.
    LEFT JOIN dbo.BtoA_SiteRoadDetails srd WITH (NOLOCK)
      ON srd.Rd_App_Id = ap.App_Id AND ISNULL(srd.Rd_RoadActive, 1) = 1
    ORDER BY App_Cdte, AppID;

    -- 3. Held but no longer workable here: submitted, decided, deactivated or moved ward
    --    (and, with @OnePerEpid = 1, superseded by a newer application on the same EPID).
    IF @IncludeClosed = 1
        SELECT AppId     = k.App_Id,
               AppStatus = CASE WHEN ap.App_Active = 1 THEN ap.App_Status END
        FROM #known k
        LEFT JOIN dbo.BtoAMainApp ap WITH (NOLOCK) ON ap.App_Id = k.App_Id
        WHERE NOT EXISTS (SELECT 1 FROM #ward w WHERE w.App_Id = k.App_Id);
    ELSE
        SELECT AppId = CAST(NULL AS int), AppStatus = CAST(NULL AS int) WHERE 1 = 0;
END
GO

-- CREATE OR ALTER keeps the procedure's existing GRANTs; nothing to re-grant.

-------------------------------------------------------------------------------
-- 4. CHECKS (read-only)
-------------------------------------------------------------------------------
-- 4.1 The parameters: 9, the last one @OnePerEpid bit with default 1.
SELECT p.parameter_id, p.name, TYPE_NAME(p.user_type_id) AS type, p.max_length
FROM sys.parameters p
WHERE p.object_id = OBJECT_ID(N'dbo.USP_S_GpsWardSync')
ORDER BY p.parameter_id;

SELECT ParamCount = COUNT(*),
       Ok = CASE WHEN COUNT(*) = 9
                  AND MAX(CASE WHEN name = N'@OnePerEpid' AND parameter_id = 9 THEN 1 ELSE 0 END) = 1
                 THEN 1 ELSE 0 END
FROM sys.parameters
WHERE object_id = OBJECT_ID(N'dbo.USP_S_GpsWardSync');

-- 4.2 Per ULB + ward: how many applications the phone is offered before (one per EPID) and
--     after (one per App_Id). Counts only; no personal data.
;WITH w AS (
    SELECT ap.App_Id, ap.App_MotherEPID,
           UlbCode = md.MD_ULBCode,
           WardNumber = LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId)))
    FROM dbo.BtoAMainApp ap WITH (NOLOCK)
    JOIN dbo.BtoA_EPIDMetaData md WITH (NOLOCK)
      ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
    WHERE ap.App_Active = 1 AND ap.isProcessingFeePaid = 1 AND ap.App_Status IN (10, 400)
      AND ISNULL(ap.App_MotherEPID, N'') <> N'' AND md.MD_ULBCode IS NOT NULL
      AND EXISTS (SELECT 1 FROM dbo.[BtoA_SiteDtls] sd WITH (NOLOCK)
                  WHERE sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1)
)
SELECT UlbCode, WardNumber,
       OnePerEpid_Before = COUNT(DISTINCT App_MotherEPID),
       EveryApplication_After = COUNT(DISTINCT App_Id)
FROM w
GROUP BY UlbCode, WardNumber
-- An alias cannot be used inside an ORDER BY expression (Msg 207), so the counts are repeated.
ORDER BY COUNT(DISTINCT App_Id) - COUNT(DISTINCT App_MotherEPID) DESC, UlbCode, WardNumber;
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 5. UNDO (commented out) - puts back the text saved in section 2, with its GRANTs.
-------------------------------------------------------------------------------
/*
DECLARE @def nvarchar(max), @grants nvarchar(max);
SELECT @def = [definition], @grants = grants
FROM dbo.ZZ_PropertyGpsApi_ProcBackup_44 WHERE [name] = N'USP_S_GpsWardSync';

IF @def IS NULL
    RAISERROR('No saved text for USP_S_GpsWardSync. Nothing changed.', 16, 1);
ELSE
BEGIN
    EXEC (N'DROP PROCEDURE IF EXISTS dbo.USP_S_GpsWardSync');
    EXEC (@def);                                     -- the saved CREATE PROCEDURE text
    IF @grants IS NOT NULL EXEC (@grants);
    SELECT Restored = N'USP_S_GpsWardSync', ParamCount =
        (SELECT COUNT(*) FROM sys.parameters WHERE object_id = OBJECT_ID(N'dbo.USP_S_GpsWardSync'));
END
*/
