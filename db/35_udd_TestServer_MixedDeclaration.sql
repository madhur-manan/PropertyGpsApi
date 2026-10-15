/*
    Test data for the Mixed-use fix (app 1.1.11) on the TEST server: one free ward 54 test
    application becomes a Mixed declaration, with declared extents the way the citizen
    application files them - square feet, adding up to the plot area in sq.ft (on 1,165 real
    Mixed sites, commercial + residential sq.ft = the site area).

    APPLIES TO UDD_KHATABTOA ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74) ONLY.
    The guard checks the server as well as the database name and refuses everything else.

    Which application: @Epid below (default 9990000110, TEST OWNER 10), the newest active one
    of that EPID in ward 102/54. Its plot area comes from its EPID metadata
    (MD_JSON $.SiteDetails.SiteArea, square metres).

    What it writes (section 2, one transaction), the application's active site row only:
      BtoA_SiteDtls.propertyUseType = 3 (Mixed), comercialExtentinSqft = 40 % of the plot in
      sq.ft, residentailsExtentinSqft = the rest.
    It saves the old values to ZZ_PropertyGpsApi_MixedTest first; section 3 puts them back.

    The phones keep what they downloaded: after this, clear the app's storage, sign in and
    Fetch / Update ward 54 so the application comes down again as Mixed.

    Sections: 1 checks (read-only), 2 apply, 3 undo (commented out).
*/

USE UDD_KHATABTOA;
GO

-- SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not.
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'));
IF DB_NAME() <> 'UDD_KHATABTOA'
   OR CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) NOT LIKE 'DB-NEW-KHATA-TE%'
BEGIN
    RAISERROR('Refusing to run: open UDD_KHATABTOA on the TEST DB server (DB-NEW-KHATA-TE) first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 1. Checks (read-only): the application, its site row and its plot area
-------------------------------------------------------------------------------
DECLARE @Epid nvarchar(30) = N'9990000110', @ZoneId int = 102, @WardId int = 54;

SELECT TOP (1) ap.App_Id, ap.App_DisplayId, ap.App_Status, s.Site_Id, s.propertyUseType,
       s.comercialExtentinSqft, s.residentailsExtentinSqft,
       SiteAreaSqM = TRY_CAST(JSON_VALUE(md.MD_JSON, '$.SiteDetails.SiteArea') AS float),
       SiteAreaSqFt = ROUND(TRY_CAST(JSON_VALUE(md.MD_JSON, '$.SiteDetails.SiteArea') AS float) / 0.09290304, 2)
FROM dbo.BtoAMainApp ap
JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
JOIN dbo.BtoA_SiteDtls s ON s.Site_App_Id = ap.App_Id AND s.Site_active = 1
WHERE ap.App_MotherEPID = @Epid AND ap.App_Active = 1
  AND md.MD_ZoneId = @ZoneId AND md.MD_WardId = @WardId
ORDER BY ap.App_Id DESC;
GO

-------------------------------------------------------------------------------
-- 2. Apply
-------------------------------------------------------------------------------
SET XACT_ABORT ON;
BEGIN TRAN;

DECLARE @Epid nvarchar(30) = N'9990000110', @ZoneId int = 102, @WardId int = 54;
DECLARE @AppId int, @SiteId int, @SiteSqM float;

SELECT TOP (1) @AppId = ap.App_Id, @SiteId = s.Site_Id,
       @SiteSqM = TRY_CAST(JSON_VALUE(md.MD_JSON, '$.SiteDetails.SiteArea') AS float)
FROM dbo.BtoAMainApp ap
JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
JOIN dbo.BtoA_SiteDtls s ON s.Site_App_Id = ap.App_Id AND s.Site_active = 1
WHERE ap.App_MotherEPID = @Epid AND ap.App_Active = 1
  AND md.MD_ZoneId = @ZoneId AND md.MD_WardId = @WardId
ORDER BY ap.App_Id DESC;

IF @AppId IS NULL
    THROW 50001, 'No active application with an active site row for this EPID in ward 102/54. Nothing changed.', 1;
IF ISNULL(@SiteSqM, 0) <= 0
    THROW 50002, 'The application has no plot area in its EPID metadata (SiteDetails.SiteArea). Nothing changed.', 1;

IF OBJECT_ID(N'dbo.ZZ_PropertyGpsApi_MixedTest') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_MixedTest
    (
        Site_Id          int PRIMARY KEY,
        App_Id           int NOT NULL,
        OldUseType       int NULL,
        OldCommercialSqFt decimal(18, 2) NULL,
        OldResidentialSqFt decimal(18, 2) NULL,
        SavedOn          datetime NOT NULL CONSTRAINT DF_ZZ_PgpsMixedTest DEFAULT (GETDATE())
    );

-- Kept on a re-run, so it always holds the original values.
IF NOT EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_MixedTest WHERE Site_Id = @SiteId)
    INSERT INTO dbo.ZZ_PropertyGpsApi_MixedTest (Site_Id, App_Id, OldUseType, OldCommercialSqFt, OldResidentialSqFt)
    SELECT s.Site_Id, s.Site_App_Id, s.propertyUseType, s.comercialExtentinSqft, s.residentailsExtentinSqft
    FROM dbo.BtoA_SiteDtls s WHERE s.Site_Id = @SiteId;

DECLARE @PlotSqFt decimal(18, 2) = ROUND(@SiteSqM / 0.09290304, 2);
DECLARE @CommercialSqFt decimal(18, 2) = ROUND(@PlotSqFt * 0.4, 2);

UPDATE dbo.BtoA_SiteDtls
SET propertyUseType = 3,
    comercialExtentinSqft = @CommercialSqFt,
    residentailsExtentinSqft = @PlotSqFt - @CommercialSqFt
WHERE Site_Id = @SiteId;

COMMIT;

SELECT App_Id = @AppId, Site_Id = @SiteId, PlotSqM = @SiteSqM, PlotSqFt = @PlotSqFt,
       s.propertyUseType, s.comercialExtentinSqft, s.residentailsExtentinSqft
FROM dbo.BtoA_SiteDtls s WHERE s.Site_Id = @SiteId;   -- use type 3, the two add up to PlotSqFt
GO

-------------------------------------------------------------------------------
-- 3. Undo (commented out): puts the site row's use type and extents back.
-------------------------------------------------------------------------------
/*
SET XACT_ABORT ON;
BEGIN TRAN;
UPDATE s SET s.propertyUseType = z.OldUseType,
             s.comercialExtentinSqft = z.OldCommercialSqFt,
             s.residentailsExtentinSqft = z.OldResidentialSqFt
FROM dbo.BtoA_SiteDtls s
JOIN dbo.ZZ_PropertyGpsApi_MixedTest z ON z.Site_Id = s.Site_Id;
SELECT Restored = @@ROWCOUNT;
DROP TABLE dbo.ZZ_PropertyGpsApi_MixedTest;
COMMIT;
*/
