/*
    USP_CheckMobileAppVersion - compare versions as versions, not as decimal numbers.

    APPLIES TO UDD_KHATABTOA_TEST (and, when the 1.1.0 app is deployed, the new server's
    UDD_KHATABTOA - see ROLLOUT below). There is no USE line: open the target database
    first. The guard below refuses anything else.

    WHY. The procedure compared versions with CAST(... AS FLOAT):
      - "1.0.0", "1.1.0" or "1.1.1" cannot be cast, so the check came back
        ERROR | Error converting data type nvarchar to float, and logged nothing. That is
        why the app had to send only MAJOR.MINOR ("1.0").
      - As decimals, 1.10 ranks BELOW 1.9.
      - The latest row was picked with a string MAX(Current_Version), which has the same
        problem ("1.10" < "1.9" as text).
    The version COLUMNS are fine as they are: Mst_AppVersion and BtoA_AppVersion_CheckLog
    hold versions as nvarchar(20), which stores "1.1.1" perfectly well. Only the
    comparison was wrong.

    THE CHANGE.
      - New dbo.fn_GpsVersionKey(@Version): an inline table function that turns
        MAJOR[.MINOR[.PATCH]] into one sortable bigint (major*10^12 + minor*10^6 + patch).
        Anything from '+' on (build metadata) is ignored; missing parts count as 0, so
        "1.0" = "1.0.0"; each part is digits only, at most 6. Anything else gives NULL.
      - The procedure keeps its parameters, result columns, statuses
        (FORCE_UPDATE_REQUIRED / UPDATE_AVAILABLE / UP_TO_DATE / WARNING / ERROR) and the
        BtoA_AppVersion_CheckLog insert. It now picks the latest row by version key and
        compares by version key. A client version that is not a version returns ERROR
        "Invalid version format"; a non-version in Mst_AppVersion returns ERROR too, so a
        data mistake is visible instead of quietly reading as "up to date".
      - IsForceUpdate is returned only when an update is needed. The old procedure passed
        the row's flag through even for UP_TO_DATE, so once a release was marked forced,
        apps already on it were told to update too.
      - The leftover PRINT debug lines are gone.
    Old builds that still send "1.0" compare correctly.

    ROLLOUT. Apply this with or before the 1.1.0 app on any server: 1.1.0 sends its full
    version, and the old procedure answers that with ERROR (the app then fails open, so
    no update is ever forced).

    Sections: 1 guard, 2 helper, 3 procedure, 4 check, 5 undo (commented out).
*/

-------------------------------------------------------------------------------
-- 1. GUARD (SET NOEXEC ON stops every later batch in SSMS, which a THROW would not)
-------------------------------------------------------------------------------
IF DB_NAME() NOT IN ('UDD_KHATABTOA_TEST', 'UDD_KHATABTOA')
BEGIN
    RAISERROR('Refusing to run: open UDD_KHATABTOA_TEST (or the new server''s UDD_KHATABTOA) first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 2. HELPER
-------------------------------------------------------------------------------
CREATE OR ALTER FUNCTION dbo.fn_GpsVersionKey (@Version NVARCHAR(50))
RETURNS TABLE
AS
RETURN
    WITH v AS
    (
        -- Trim, and drop build metadata ("1.2.3+45" -> "1.2.3").
        SELECT LTRIM(RTRIM(LEFT(ISNULL(@Version, N''), CHARINDEX(N'+', ISNULL(@Version, N'') + N'+') - 1))) AS s
    ),
    p AS
    (
        SELECT s, LEN(s) - LEN(REPLACE(s, N'.', N'')) AS dots
        FROM v
    ),
    parts AS
    (
        SELECT
            -- Digits and dots only: PARSENAME would otherwise strip [brackets] or "quotes".
            CASE WHEN s LIKE N'%[^0-9.]%' THEN 0 ELSE 1 END AS plain,
            CASE dots WHEN 0 THEN s     WHEN 1 THEN PARSENAME(s, 2) WHEN 2 THEN PARSENAME(s, 3) END AS major,
            CASE dots WHEN 0 THEN N'0'  WHEN 1 THEN PARSENAME(s, 1) WHEN 2 THEN PARSENAME(s, 2) END AS minor,
            CASE dots WHEN 0 THEN N'0'  WHEN 1 THEN N'0'            WHEN 2 THEN PARSENAME(s, 1) END AS patch
        FROM p
    )
    SELECT CASE
               WHEN plain = 1
                AND LEN(major) BETWEEN 1 AND 6
                AND LEN(minor) BETWEEN 1 AND 6
                AND LEN(patch) BETWEEN 1 AND 6
               THEN TRY_CAST(major AS BIGINT) * 1000000000000
                  + TRY_CAST(minor AS BIGINT) * 1000000
                  + TRY_CAST(patch AS BIGINT)
           END AS VersionKey
    FROM parts;
GO

-------------------------------------------------------------------------------
-- 3. PROCEDURE
-------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE [dbo].[USP_CheckMobileAppVersion]
(
    @App_Platform           NVARCHAR(50),        -- 'Android' or 'iOS'
    @Client_Version         NVARCHAR(20),        -- Current version on mobile, e.g. 1.1.0 (1.0 from old builds)
    @Client_Device_Info     NVARCHAR(MAX) = NULL, -- Optional: device info for logging
    @Cby                    int,
    @Crole                  int
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NeedsUpdate        BIT = 0;
    DECLARE @IsForceUpdate      BIT = 0;
    DECLARE @Latest_Version     NVARCHAR(20) = NULL;
    DECLARE @MinRequired_Ver    NVARCHAR(20) = NULL;
    DECLARE @Update_Message     NVARCHAR(MAX) = NULL;
    DECLARE @Download_URL       NVARCHAR(500) = NULL;
    DECLARE @File_Size_MB       DECIMAL(10,2) = NULL;
    DECLARE @Release_Notes      NVARCHAR(MAX) = NULL;
    DECLARE @Status             NVARCHAR(100) = 'OK';
    DECLARE @ClientKey          BIGINT = NULL;
    DECLARE @MinKey             BIGINT = NULL;
    DECLARE @LatestKey          BIGINT = NULL;

    BEGIN TRY
        -- Validate input
        IF @App_Platform IS NULL OR @Client_Version IS NULL
        BEGIN
            SELECT
                CAST(0 AS BIT) AS NeedsUpdate,
                CAST(0 AS BIT) AS IsForceUpdate,
                'ERROR' AS Status,
                'Invalid platform or version' AS Message,
                NULL AS Latest_Version,
                NULL AS MinRequired_Version,
                NULL AS Download_URL,
                NULL AS File_Size_MB,
                NULL AS Release_Notes;
            RETURN;
        END

        SELECT @ClientKey = VersionKey FROM dbo.fn_GpsVersionKey(@Client_Version);
        IF @ClientKey IS NULL
        BEGIN
            SELECT
                CAST(0 AS BIT) AS NeedsUpdate,
                CAST(0 AS BIT) AS IsForceUpdate,
                'ERROR' AS Status,
                'Invalid version format' AS Message,
                NULL AS Latest_Version,
                NULL AS MinRequired_Version,
                NULL AS Download_URL,
                NULL AS File_Size_MB,
                NULL AS Release_Notes;
            RETURN;
        END

        -- Latest version info for the platform: the active row with the highest
        -- Current_Version, compared as a version (not as text).
        SELECT TOP 1
            @Latest_Version = a.Latest_Version,
            @MinRequired_Ver = a.MinRequired_Version,
            @Update_Message = a.Update_Message,
            @Download_URL = a.Download_URL,
            @File_Size_MB = a.File_Size_MB,
            @Release_Notes = a.Release_Notes,
            @IsForceUpdate = a.IsForceUpdate
        FROM [masterDB_prod].[dbo].[Mst_AppVersion] a
        CROSS APPLY dbo.fn_GpsVersionKey(a.Current_Version) k
        WHERE a.[App_Platform] = @App_Platform
          AND a.[IsActive] = 1
        ORDER BY k.VersionKey DESC, a.AppVersion_Id DESC;

        -- Check if version exists in the system
        IF @Latest_Version IS NULL
        BEGIN
            SELECT
                CAST(0 AS BIT) AS NeedsUpdate,
                CAST(0 AS BIT) AS IsForceUpdate,
                'WARNING' AS Status,
                'Platform/Version not found in system' AS Message,
                @Latest_Version AS Latest_Version,
                @MinRequired_Ver AS MinRequired_Version,
                @Download_URL AS Download_URL,
                @File_Size_MB AS File_Size_MB,
                @Release_Notes AS Release_Notes;
            RETURN;
        END

        SELECT @LatestKey = VersionKey FROM dbo.fn_GpsVersionKey(@Latest_Version);
        SELECT @MinKey = VersionKey FROM dbo.fn_GpsVersionKey(@MinRequired_Ver);

        -- A non-version in Mst_AppVersion is a data mistake: say so rather than
        -- quietly reporting "up to date". A NULL minimum simply means "no minimum".
        IF @LatestKey IS NULL OR (@MinRequired_Ver IS NOT NULL AND @MinKey IS NULL)
        BEGIN
            SELECT
                CAST(0 AS BIT) AS NeedsUpdate,
                CAST(0 AS BIT) AS IsForceUpdate,
                'ERROR' AS Status,
                'Invalid version in Mst_AppVersion' AS Message,
                NULL AS Latest_Version,
                NULL AS MinRequired_Version,
                NULL AS Download_URL,
                NULL AS File_Size_MB,
                NULL AS Release_Notes;
            RETURN;
        END

        -- Determine if client version is below minimum required
        IF @ClientKey < @MinKey
        BEGIN
            SET @NeedsUpdate = 1;
            SET @IsForceUpdate = 1;
            SET @Status = 'FORCE_UPDATE_REQUIRED';
            SET @Update_Message = 'Your app version is no longer supported. Please update to continue.';
        END
        -- Determine if update is available (but not mandatory)
        ELSE IF @ClientKey < @LatestKey
        BEGIN
            SET @NeedsUpdate = 1;
            SET @Status = 'UPDATE_AVAILABLE';
            IF @Update_Message IS NULL
                SET @Update_Message = 'A new version is available. Please update to get the latest features.';
        END
        ELSE
        BEGIN
            SET @NeedsUpdate = 0;
            -- The row's IsForceUpdate says the update TO the latest version is mandatory.
            -- An app already on it has nothing to be forced to (the old procedure
            -- returned the flag regardless, telling current apps to update).
            SET @IsForceUpdate = 0;
            SET @Status = 'UP_TO_DATE';
            SET @Update_Message = 'Your app is up to date.';
        END

        -- Log the version check
        INSERT INTO [dbo].[BtoA_AppVersion_CheckLog]
        ([Platform], [Client_Version], [Latest_Version], [NeedsUpdate], [Status], [Device_Info], [Check_DateTime],[Cby],[Crole])
        VALUES
        (@App_Platform, @Client_Version, @Latest_Version, @NeedsUpdate, @Status, @Client_Device_Info, GETDATE(),@Cby,@Crole);

        -- Return result to client
        SELECT
            @NeedsUpdate AS NeedsUpdate,
            @IsForceUpdate AS IsForceUpdate,
            @Status AS Status,
            @Update_Message AS Message,
            @Latest_Version AS Latest_Version,
            @MinRequired_Ver AS MinRequired_Version,
            @Download_URL AS Download_URL,
            @File_Size_MB AS File_Size_MB,
            @Release_Notes AS Release_Notes;

    END TRY
    BEGIN CATCH
        SELECT
            CAST(0 AS BIT) AS NeedsUpdate,
            CAST(0 AS BIT) AS IsForceUpdate,
            'ERROR' AS Status,
            ERROR_MESSAGE() AS Message,
            NULL AS Latest_Version,
            NULL AS MinRequired_Version,
            NULL AS Download_URL,
            NULL AS File_Size_MB,
            NULL AS Release_Notes;
    END CATCH
END;
GO

-------------------------------------------------------------------------------
-- 4. CHECK - the helper on its own (read-only).
-------------------------------------------------------------------------------
SELECT t.v AS Version, k.VersionKey
FROM (VALUES (N'1.0'), (N'1.0.0'), (N'1.1.0'), (N'1.1.1'), (N'1.9'), (N'1.10'), (N'1.2.3+45'),
             (N'2'), (N'abc'), (N'1..0'), (N'1.2.3.4'), (N'[1].2'), (N'')) t(v)
CROSS APPLY dbo.fn_GpsVersionKey(t.v) k;
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 5. UNDO - the original procedure exactly as it was before db/24, and drop the helper.
-------------------------------------------------------------------------------
/*
DROP FUNCTION IF EXISTS dbo.fn_GpsVersionKey;

CREATE OR ALTER PROCEDURE [dbo].[USP_CheckMobileAppVersion]
(
    @App_Platform           NVARCHAR(50),        -- 'Android' or 'iOS'
    @Client_Version         NVARCHAR(20),        -- Current version on mobile
    @Client_Device_Info     NVARCHAR(MAX) = NULL, -- Optional: device info for logging
	@Cby					int,
	@Crole					int
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NeedsUpdate        BIT = 0;
    DECLARE @IsForceUpdate      BIT = 0;
    DECLARE @Latest_Version     NVARCHAR(20) = NULL;
    DECLARE @MinRequired_Ver    NVARCHAR(20) = NULL;
    DECLARE @Update_Message     NVARCHAR(MAX) = NULL;
    DECLARE @Download_URL       NVARCHAR(500) = NULL;
    DECLARE @File_Size_MB       DECIMAL(10,2) = NULL;
    DECLARE @Release_Notes      NVARCHAR(MAX) = NULL;
    DECLARE @Status             NVARCHAR(100) = 'OK';

    BEGIN TRY
        -- Validate input
        IF @App_Platform IS NULL OR @Client_Version IS NULL
        BEGIN
            SELECT
                CAST(0 AS BIT) AS NeedsUpdate,
                CAST(0 AS BIT) AS IsForceUpdate,
                'ERROR' AS Status,
                'Invalid platform or version' AS Message,
                NULL AS Latest_Version,
                NULL AS MinRequired_Version,
                NULL AS Download_URL,
                NULL AS File_Size_MB,
                NULL AS Release_Notes;
            RETURN;
        END

        -- Get latest version info for the platform
        SELECT TOP 1
            @Latest_Version = Latest_Version,
            @MinRequired_Ver = MinRequired_Version,
            @Update_Message = Update_Message,
            @Download_URL = Download_URL,
            @File_Size_MB = File_Size_MB,
            @Release_Notes = Release_Notes,
            @IsForceUpdate = IsForceUpdate
        FROM [masterDB_prod].[dbo].[Mst_AppVersion]
        WHERE [App_Platform] = @App_Platform
          AND [IsActive] = 1
          AND [Current_Version] = (
              SELECT MAX([Current_Version])
              FROM masterDB_prod.[dbo].[Mst_AppVersion]
              WHERE [App_Platform] = @App_Platform
                AND [IsActive] = 1
          );
	print 1
        -- Check if version exists in the system
        IF @Latest_Version IS NULL
        BEGIN
            SELECT
                CAST(0 AS BIT) AS NeedsUpdate,
                CAST(0 AS BIT) AS IsForceUpdate,
                'WARNING' AS Status,
                'Platform/Version not found in system' AS Message,
                @Latest_Version AS Latest_Version,
                @MinRequired_Ver AS MinRequired_Version,
                @Download_URL AS Download_URL,
                @File_Size_MB AS File_Size_MB,
                @Release_Notes AS Release_Notes;
            RETURN;
        END
 print 2
 print @Client_Version
 print @MinRequired_Ver
        -- Determine if client version is below minimum required
        IF CAST(@Client_Version AS FLOAT) < CAST(@MinRequired_Ver AS FLOAT)
        BEGIN
		print 2.1
            SET @NeedsUpdate = 1;
            SET @IsForceUpdate = 1;
            SET @Status = 'FORCE_UPDATE_REQUIRED';
            SET @Update_Message = 'Your app version is no longer supported. Please update to continue.';
        END
        -- Determine if update is available (but not mandatory)
        ELSE IF CAST(@Client_Version AS FLOAT) < CAST(@Latest_Version AS FLOAT)
        BEGIN
            SET @NeedsUpdate = 1;
            SET @Status = 'UPDATE_AVAILABLE';
            IF @Update_Message IS NULL
                SET @Update_Message = 'A new version is available. Please update to get the latest features.';
        END
        ELSE
        BEGIN
            SET @NeedsUpdate = 0;
            SET @Status = 'UP_TO_DATE';
            SET @Update_Message = 'Your app is up to date.';
        END
 print 3
        -- Log the version check
        INSERT INTO [dbo].[BtoA_AppVersion_CheckLog]
        ([Platform], [Client_Version], [Latest_Version], [NeedsUpdate], [Status], [Device_Info], [Check_DateTime],[Cby],[Crole])
        VALUES
        (@App_Platform, @Client_Version, @Latest_Version, @NeedsUpdate, @Status, @Client_Device_Info, GETDATE(),@Cby,@Crole);
 print 4
        -- Return result to client
        SELECT
            @NeedsUpdate AS NeedsUpdate,
            @IsForceUpdate AS IsForceUpdate,
            @Status AS Status,
            @Update_Message AS Message,
            @Latest_Version AS Latest_Version,
            @MinRequired_Ver AS MinRequired_Version,
            @Download_URL AS Download_URL,
            @File_Size_MB AS File_Size_MB,
            @Release_Notes AS Release_Notes;

    END TRY
    BEGIN CATCH
        SELECT
            CAST(0 AS BIT) AS NeedsUpdate,
            CAST(0 AS BIT) AS IsForceUpdate,
            'ERROR' AS Status,
            ERROR_MESSAGE() AS Message,
            NULL AS Latest_Version,
            NULL AS MinRequired_Version,
            NULL AS Download_URL,
            NULL AS File_Size_MB,
            NULL AS Release_Notes;
    END CATCH
END;
*/
