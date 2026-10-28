/*
    Test data: Revenue Inspectors for the TEST server, two per Mahadevapura ward, each copied
    from test officer 11320 (role 116, corporation 556, zone 102) with their own name, mobile
    and ward.

    APPLIES TO masterDB_prod ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74) ONLY.
    masterDB_prod is also the name of the LIVE master database, so the guard checks the
    server as well as the database name and refuses everything else - including the laptop.

    FILL IN the names and mobiles in #ri (section 0) before running, and run the WHOLE script
    in one go: #ri lives for the SSMS session. The real names and numbers are kept out of git:
    this repo copy has empty slots; the filled copy lives outside the repository.

    Wards (GBA zone 102, Mahadevapura): 54 Hoodi, 82 Garudacharpalya, 83 Kadugodi,
    84 Hagadooru. 11320 has no mst_Ofcr_WardMapping rows, so neither do these: the API
    falls back to the officer's own ward (Ofcr_WardId), and each RI is offered exactly one.
    The two RIs of a ward see the same cases; the allotment decides who works one.

    Each signs in like 11320: OTP by SMS to the mobile, and is bound to the first phone they
    sign in on (GpsOfficerSession).

    Sections: 0 the list, 1 checks (read-only), 2 create, 3 undo (commented out).
*/

USE masterDB_prod;
GO

-- SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not.
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'));
IF DB_NAME() <> 'masterDB_prod'
   OR CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) NOT LIKE 'DB-NEW-KHATA-TE%'
BEGIN
    RAISERROR('Refusing to run: open masterDB_prod on the TEST DB server (DB-NEW-KHATA-TE) first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 0. The RIs: name, 10-digit mobile, ward
-------------------------------------------------------------------------------
IF OBJECT_ID('tempdb..#ri') IS NOT NULL DROP TABLE #ri;
CREATE TABLE #ri (Seq int IDENTITY PRIMARY KEY, Name nvarchar(200) NOT NULL, Mobile nvarchar(30) NOT NULL, WardId int NOT NULL);
INSERT INTO #ri (Name, Mobile, WardId) VALUES
    (N'<name>', N'<mobile>', 54),
    (N'<name>', N'<mobile>', 54),
    (N'<name>', N'<mobile>', 82),
    (N'<name>', N'<mobile>', 82),
    (N'<name>', N'<mobile>', 83),
    (N'<name>', N'<mobile>', 83),
    (N'<name>', N'<mobile>', 84),
    (N'<name>', N'<mobile>', 84);
GO

-------------------------------------------------------------------------------
-- 1. Checks (read-only). Every column should say 'ok' and TakenBy be empty.
-------------------------------------------------------------------------------
SELECT r.Seq, r.Name, r.Mobile, r.WardId,
       Mobile_ = CASE WHEN r.Mobile LIKE '[6-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]' THEN 'ok' ELSE 'NOT A 10-DIGIT MOBILE' END,
       TakenBy = (SELECT TOP (1) CONCAT(o.Ofcr_Id, ' ', o.Ofcr_Name) FROM dbo.mst_Officer o WHERE o.Ofcr_MNo = r.Mobile),
       InList  = CASE WHEN (SELECT COUNT(*) FROM #ri x WHERE x.Mobile = r.Mobile) > 1 THEN 'REPEATED' ELSE 'ok' END,
       FixedOtpList = CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.USP_I_OTP')) LIKE N'%' + r.Mobile + N'%'
                           THEN 'ON THE LIST - pick another number' ELSE 'ok' END,
       Ward = ISNULL((SELECT TOP (1) LTRIM(RTRIM(REPLACE(a.BBMPWardName, CHAR(9), '')))
                      FROM dbo.mst_AROMapping a JOIN dbo.mst_Officer t ON t.Ofcr_Id = 11320
                      WHERE a.GBAZoneID = t.Ofcr_ZoneId AND a.easthiULBNAME_CorpId = t.Ofcr_CorporationId
                        AND a.BBMPWardId = r.WardId),
                     'NOT IN 11320''S ZONE')
FROM #ri r
ORDER BY r.Seq;

SELECT Template = Ofcr_Id, Ofcr_RoleId, Ofcr_CorporationId, Ofcr_ZoneId, Ofcr_WardId, Ofcr_Active
FROM dbo.mst_Officer WHERE Ofcr_Id = 11320;
GO

-------------------------------------------------------------------------------
-- 2. Create
-------------------------------------------------------------------------------
SET XACT_ABORT ON;
BEGIN TRAN;

    DECLARE @template bigint = 11320;

    IF EXISTS (SELECT 1 FROM #ri WHERE Name LIKE N'<%'
                  OR Mobile NOT LIKE '[6-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]')
        THROW 50001, 'Fill in every name and a 10-digit mobile in #ri (section 0). Nothing was created.', 1;
    IF EXISTS (SELECT 1 FROM #ri r JOIN dbo.mst_Officer o ON o.Ofcr_MNo = r.Mobile)
        THROW 50002, 'A mobile already belongs to an officer - see section 1. Nothing was created.', 1;
    IF EXISTS (SELECT Mobile FROM #ri GROUP BY Mobile HAVING COUNT(*) > 1)
        THROW 50003, 'A mobile is repeated in #ri. Nothing was created.', 1;
    IF EXISTS (SELECT 1 FROM #ri r WHERE OBJECT_DEFINITION(OBJECT_ID('dbo.USP_I_OTP')) LIKE N'%' + r.Mobile + N'%')
        THROW 50004, 'A mobile is on USP_I_OTP''s fixed-OTP list - pick another. Nothing was created.', 1;
    IF EXISTS (SELECT 1 FROM #ri r WHERE NOT EXISTS (
                   SELECT 1 FROM dbo.mst_AROMapping a JOIN dbo.mst_Officer t ON t.Ofcr_Id = @template
                   WHERE a.GBAZoneID = t.Ofcr_ZoneId AND a.easthiULBNAME_CorpId = t.Ofcr_CorporationId
                     AND a.BBMPWardId = r.WardId))
        THROW 50005, 'A ward is not in 11320''s zone and corporation. Nothing was created.', 1;

    INSERT INTO dbo.mst_Officer
        (Ofcr_Name, Ofcr_MNo, Ofcr_RoleId, Ofcr_DistId, Ofcr_CorporationId, Ofcr_ZoneId,
         Ofcr_DivisionId, Ofcr_WardId, Ofcr_Active, Ofcr_AprCode, Ofcr_RejCode,
         Ofcr_AdditionalInfo, Ofcr_CBy, Ofcr_CDte, Ofcr_CRole, NoOfFailedAttempt,
         Ofcr_Level, Ofcr_ApplicationId, Ofcr_Center)
    SELECT r.Name, r.Mobile, t.Ofcr_RoleId, t.Ofcr_DistId, t.Ofcr_CorporationId, t.Ofcr_ZoneId,
           t.Ofcr_DivisionId, r.WardId, 1, t.Ofcr_AprCode, t.Ofcr_RejCode,
           N'PROPERTY GPS TEST OFFICER (db/37)', NULL, GETDATE(), t.Ofcr_CRole, 0,
           t.Ofcr_Level, t.Ofcr_ApplicationId, t.Ofcr_Center
    FROM #ri r
    CROSS JOIN dbo.mst_Officer t
    WHERE t.Ofcr_Id = @template
    ORDER BY r.Seq;

    IF @@ROWCOUNT <> (SELECT COUNT(*) FROM #ri)
        THROW 50006, 'Test officer 11320 was not found. Nothing was created.', 1;

COMMIT TRAN;
GO

-- What the API will give each new officer (the query it runs at sign-in): one ward each.
SELECT o.Ofcr_Id, o.Ofcr_Name, o.Ofcr_MNo, o.Ofcr_RoleId, o.Ofcr_Active,
       GbaZoneId = aro.GBAZoneID, BbmpWardId = aro.BBMPWardId, BbmpWardName = aro.BBMPWardName
FROM dbo.mst_Officer o
LEFT JOIN dbo.mst_Ofcr_WardMapping wm
       ON wm.Map_OfcrId = o.Ofcr_Id AND wm.Map_OfcrRoleId = o.Ofcr_RoleId AND ISNULL(wm.isActive, 1) = 1
JOIN (SELECT DISTINCT GBAZoneID, easthiULBNAME_CorpId, BBMPWardId, BBMPWardName FROM dbo.mst_AROMapping) aro
       ON aro.GBAZoneID = ISNULL(wm.Map_ZoneID, o.Ofcr_ZoneId)
      AND aro.easthiULBNAME_CorpId = ISNULL(wm.Map_CorpId, o.Ofcr_CorporationId)
      AND aro.BBMPWardId = ISNULL(wm.Map_WardId, o.Ofcr_WardId)
WHERE o.Ofcr_AdditionalInfo = N'PROPERTY GPS TEST OFFICER (db/37)'
ORDER BY aro.BBMPWardId, o.Ofcr_Id;
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 3. Undo - removes every officer this script created and its phone binding
-------------------------------------------------------------------------------
/*
SET XACT_ABORT ON;
BEGIN TRAN;
    DELETE s FROM dbo.GpsOfficerSession s
    JOIN dbo.mst_Officer o ON o.Ofcr_Id = s.OfficerId
    WHERE o.Ofcr_AdditionalInfo = N'PROPERTY GPS TEST OFFICER (db/37)';

    DELETE m FROM dbo.mst_Ofcr_WardMapping m
    JOIN dbo.mst_Officer o ON o.Ofcr_Id = m.Map_OfcrId
    WHERE o.Ofcr_AdditionalInfo = N'PROPERTY GPS TEST OFFICER (db/37)';

    DELETE FROM dbo.mst_Officer WHERE Ofcr_AdditionalInfo = N'PROPERTY GPS TEST OFFICER (db/37)';
    SELECT OfficersRemoved = @@ROWCOUNT;
COMMIT TRAN;
*/
