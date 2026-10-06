/*
    Mst_AppVersion - make 1.1.0 the current, required Android version of Property GPS.

    APPLIES TO masterDB_prod ON THE LAPTOP ONLY (localhost\MSSQLSERVER2019). There is no
    USE line: open masterDB_prod first. The guard refuses any other database and any
    other server.

    WHY. The Android row still describes the old app: versions 1.0 / 1.0 / 1.0, no forced
    update, and a download link to com.bbmp.ri. Property GPS 1.1.0 (com.gba.property_gps)
    is the first build that encrypts its requests, so 1.0.0 builds must be made to update.
    USP_CheckMobileAppVersion (fixed in db/24) reads this row for every check.

    CAUTION. The old RI app (com.bbmp.ri) may read this same Android row on other servers.
    Do not run an equivalent there without deciding what that app should be told.

    Only the active Android row changes. The Web and iOS rows are not touched.

    Sections: 1 guard, 2 preview, 3 apply, 4 undo (commented out).
*/

-------------------------------------------------------------------------------
-- 1. GUARD (SET NOEXEC ON stops every later batch in SSMS, which a THROW would not)
-------------------------------------------------------------------------------
-- The laptop runs the named instance MSSQLSERVER2019; the servers run default instances.
IF DB_NAME() <> 'masterDB_prod'
   OR ISNULL(CAST(SERVERPROPERTY('InstanceName') AS sysname), N'') <> N'MSSQLSERVER2019'
BEGIN
    RAISERROR('Refusing to run: this script is for masterDB_prod on the laptop''s MSSQLSERVER2019 instance only.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 2. PREVIEW
-------------------------------------------------------------------------------
SELECT AppVersion_Id, App_Platform, Current_Version, MinRequired_Version, Latest_Version,
       IsForceUpdate, IsActive, Download_URL, Release_Date, UDte
FROM dbo.Mst_AppVersion
WHERE App_Platform = N'Android';
GO

-------------------------------------------------------------------------------
-- 3. APPLY
-------------------------------------------------------------------------------
UPDATE dbo.Mst_AppVersion
SET Current_Version     = N'1.1.0',
    Latest_Version      = N'1.1.0',
    MinRequired_Version = N'1.1.0',
    IsForceUpdate       = 1,
    Download_URL        = N'https://play.google.com/store/apps/details?id=com.gba.property_gps',
    Release_Date        = GETDATE(),
    UDte                = GETDATE()
WHERE App_Platform = N'Android'
  AND IsActive = 1;

SELECT CONCAT(@@ROWCOUNT, ' Android row(s) now at 1.1.0') AS Result;
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 4. UNDO - the Android row (AppVersion_Id 1) exactly as it was on the laptop on 2026-10-05.
-------------------------------------------------------------------------------
/*
UPDATE dbo.Mst_AppVersion
SET Current_Version     = N'1.0',
    Latest_Version      = N'1.0',
    MinRequired_Version = N'1.0',
    IsForceUpdate       = 0,
    Download_URL        = N'https://play.google.com/store/apps/details?id=com.bbmp.ri',
    Release_Date        = '2026-08-20T17:44:18.127',
    UDte                = NULL
WHERE AppVersion_Id = 1
  AND App_Platform = N'Android';
*/
