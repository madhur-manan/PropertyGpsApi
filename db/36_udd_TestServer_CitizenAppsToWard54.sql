/*
    Test data: three applications submitted from the citizen module (2026-10-12/13) shown on
    the RI phones in Hoodi (zone 102, ward 54).

    APPLIES TO UDD_KHATABTOA ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74) ONLY.
    The guard checks the server as well as the database name and refuses everything else.

    Why they do not show today (checked 2026-10-15): all three are at App_Status 10, active,
    fee paid, with an active site row - but their EPID metadata says zone 0 / ward 11, and
    zone 0 is no zone, so no RI anywhere receives them. That looks like the citizen module
    writing the zone wrong; worth raising with that team. And all three share EPID
    38-521-372: the ward sync sends the newest application of an EPID per ward, and the phone
    keeps one application per EPID, so only one of them could ever show.

    What it writes (section 2, one transaction):
      - BtoA_EPIDMetaData of the three: MD_ZoneId 102, MD_WardId 54, and the zone/ward names
        an existing Hoodi application carries;
      - 202610120000018 gets EPID 38-521-372-A and 202610120000019 gets 38-521-372-B
        (BtoAMainApp.App_MotherEPID and MD_MotherEPID together - the ward sync joins the
        two on it); 202610130000021 keeps 38-521-372.
    It saves the old values to ZZ_PropertyGpsApi_CitizenApps first; section 3 puts them back.

    Then on the phones: Fetch / Update in Hoodi. They are new to the phones, so they come down
    on their own - no clearing of storage needed.

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
-- 1. Checks (read-only)
-------------------------------------------------------------------------------
SELECT ap.App_Id, ap.App_DisplayId, ap.App_MotherEPID, ap.App_Status, ap.App_Active, ap.isProcessingFeePaid,
       md.MD_MotherEPID, md.MD_ZoneId, md.MD_Zonename, md.MD_WardId, md.MD_WardName
FROM dbo.BtoAMainApp ap
LEFT JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id
WHERE ap.App_DisplayId IN (N'202610120000018', N'202610120000019', N'202610130000021')
ORDER BY ap.App_Id;

-- The names a Hoodi application carries (copied to the three).
SELECT TOP (1) md.MD_Zonename, md.MD_WardName
FROM dbo.BtoA_EPIDMetaData md
WHERE md.MD_ZoneId = 102 AND md.MD_WardId = 54 AND md.MD_WardName IS NOT NULL;

-- Must return no rows: the new test EPIDs are not in use.
SELECT TakenBy = App_Id, App_MotherEPID FROM dbo.BtoAMainApp
WHERE App_MotherEPID IN (N'38-521-372-A', N'38-521-372-B');
GO

-------------------------------------------------------------------------------
-- 2. Apply
-------------------------------------------------------------------------------
SET XACT_ABORT ON;
BEGIN TRAN;

DECLARE @apps TABLE (App_DisplayId nvarchar(50) PRIMARY KEY, NewEpid nvarchar(30) NULL);
INSERT INTO @apps VALUES (N'202610120000018', N'38-521-372-A'),
                         (N'202610120000019', N'38-521-372-B'),
                         (N'202610130000021', NULL);   -- keeps its EPID

IF (SELECT COUNT(*) FROM dbo.BtoAMainApp ap JOIN @apps a ON a.App_DisplayId = ap.App_DisplayId) <> 3
    THROW 50001, 'Not all three applications were found. Nothing changed.', 1;
IF EXISTS (SELECT 1 FROM dbo.BtoAMainApp ap JOIN @apps a ON a.NewEpid = ap.App_MotherEPID)
    THROW 50002, 'A new test EPID is already in use - see section 1. Nothing changed.', 1;

DECLARE @ZoneName nvarchar(4000), @WardName nvarchar(4000);
SELECT TOP (1) @ZoneName = md.MD_Zonename, @WardName = md.MD_WardName
FROM dbo.BtoA_EPIDMetaData md
WHERE md.MD_ZoneId = 102 AND md.MD_WardId = 54 AND md.MD_WardName IS NOT NULL;

IF OBJECT_ID(N'dbo.ZZ_PropertyGpsApi_CitizenApps') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_CitizenApps
    (
        App_Id      int PRIMARY KEY,
        OldEpid     nvarchar(30) NULL,
        NewEpid     nvarchar(30) NULL,
        OldZoneId   int NULL,
        OldZoneName nvarchar(4000) NULL,
        OldWardId   int NULL,
        OldWardName nvarchar(4000) NULL,
        SavedOn     datetime NOT NULL CONSTRAINT DF_ZZ_PgpsCitizenApps DEFAULT (GETDATE())
    );

-- Kept on a re-run, so it always holds the original values.
INSERT INTO dbo.ZZ_PropertyGpsApi_CitizenApps (App_Id, OldEpid, NewEpid, OldZoneId, OldZoneName, OldWardId, OldWardName)
SELECT ap.App_Id, ap.App_MotherEPID, ISNULL(a.NewEpid, ap.App_MotherEPID),
       md.MD_ZoneId, md.MD_Zonename, md.MD_WardId, md.MD_WardName
FROM dbo.BtoAMainApp ap
JOIN @apps a ON a.App_DisplayId = ap.App_DisplayId
JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
WHERE NOT EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_CitizenApps z WHERE z.App_Id = ap.App_Id);

UPDATE md
SET md.MD_ZoneId = 102, md.MD_WardId = 54,
    md.MD_Zonename = ISNULL(@ZoneName, md.MD_Zonename), md.MD_WardName = ISNULL(@WardName, md.MD_WardName),
    md.MD_MotherEPID = z.NewEpid
FROM dbo.BtoA_EPIDMetaData md
JOIN dbo.ZZ_PropertyGpsApi_CitizenApps z ON z.App_Id = md.MD_APP_ID AND md.MD_MotherEPID = z.OldEpid;

UPDATE ap SET ap.App_MotherEPID = z.NewEpid
FROM dbo.BtoAMainApp ap
JOIN dbo.ZZ_PropertyGpsApi_CitizenApps z ON z.App_Id = ap.App_Id AND ap.App_MotherEPID = z.OldEpid;

COMMIT;

SELECT ap.App_Id, ap.App_DisplayId, ap.App_MotherEPID, md.MD_MotherEPID, md.MD_ZoneId, md.MD_WardId, md.MD_WardName
FROM dbo.BtoAMainApp ap
JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
JOIN dbo.ZZ_PropertyGpsApi_CitizenApps z ON z.App_Id = ap.App_Id
ORDER BY ap.App_Id;   -- three rows, all 102/54, three different EPIDs
GO

-------------------------------------------------------------------------------
-- 3. Undo (commented out): puts the EPIDs and the zone/ward back.
-------------------------------------------------------------------------------
/*
SET XACT_ABORT ON;
BEGIN TRAN;
UPDATE md
SET md.MD_ZoneId = z.OldZoneId, md.MD_Zonename = z.OldZoneName,
    md.MD_WardId = z.OldWardId, md.MD_WardName = z.OldWardName,
    md.MD_MotherEPID = z.OldEpid
FROM dbo.BtoA_EPIDMetaData md
JOIN dbo.ZZ_PropertyGpsApi_CitizenApps z ON z.App_Id = md.MD_APP_ID AND md.MD_MotherEPID = z.NewEpid;

UPDATE ap SET ap.App_MotherEPID = z.OldEpid
FROM dbo.BtoAMainApp ap
JOIN dbo.ZZ_PropertyGpsApi_CitizenApps z ON z.App_Id = ap.App_Id AND ap.App_MotherEPID = z.NewEpid;

DROP TABLE dbo.ZZ_PropertyGpsApi_CitizenApps;
COMMIT;
*/
