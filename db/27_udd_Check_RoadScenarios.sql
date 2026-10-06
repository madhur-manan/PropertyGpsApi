/*
    CHECK: replay the road scenarios of a survey submit at the database level.

    APPLIES TO UDD_KHATABTOA_TEST ON THE LAPTOP ONLY. There is no USE line: open
    UDD_KHATABTOA_TEST first. The guard refuses anything else.

    WHAT IT DOES. For each road scenario of the 2026-10-05 scenario matrix that reaches the
    database, it calls the procedures exactly as VerificationSubmitService.SubmitAsync does,
    in the same order and with the same parameter values:

        sp_getapplock 'gps:submit:<display id>'            (Exclusive, Transaction, 5000)
        App_Id lookup (App_DisplayId, App_Active = 1)
        DeclaredRoadsSql                                   (CheckRoadRows: an unknown roadRowId
                                                            refuses the submit, UNKNOWN_ROAD_ROW;
                                                            an unanswered declared road warns)
        HealAppIdSql                                       (WriteApplicationAsync, D33)
        dbo.USP_IU_BtoA_MainApp_Officer                    (@Ofcr_App_Id = the App_Id)
        HeldSql                                            (0 -> SUBMIT_REJECTED)
        per road, ordered by RoadRowId ?? 0 then payload order (WriteRoadsAsync):
            dbo.USP_IU_BtoA_SiteRoadDetails_Officer
            dbo.USP_U_BtoA_GpsRoadNoticeDocument           (RoadRowId > 0 and a notice)
            ClearNoticeSql                                 (RoadRowId > 0 and no notice, D22)
        SupersedeRoadRowsSql, SupersedeDocumentsSql, and
        EnsureDocumentSql per live private declared road   (D5 / D22)
        dbo.USP_U_BtoA_GpsSubmitExtras_App                 (WriteExtrasAsync)
        dbo.USP_IU_BtoA_StatusDetail_Officer               (WriteStatusAsync -> App_Status 13,
                                                            fires trg_BtoAMainApp and the db/23
                                                            e-Khata trigger)
        dbo.USP_U_BtoA_GpsCompleteAssignment               (CompleteAssignmentAsync)
        App_Status read back

    on real status-10 applications of GBA zone 102 / ward 54 chosen read-only on 2026-10-05
    (no officer rows, no private-road documents, never App 113 or 370), snapshots what each
    write stored, and prints PASS/FAIL per expectation.

    The SQL the API sends itself (the *Sql constants of VerificationSubmitService, and
    RoadCountSql / StreetNameSql of HistoryService) is copied here word for word;
    RoadSqlMirrorTests in the API test project fails when the two drift. The one rewrite:
    Dapper's list expansion "IN @live" is "IN (SELECT v FROM @live)" here.

    NOTHING PERSISTS. Every write happens inside one BEGIN TRAN that is always rolled back;
    the results are kept in table variables, which survive the rollback. A before/after
    comparison of row counts, history tables and the statuses of the test apps (and of
    App 113 / 370) is printed at the end as section "PERSIST".

    Expected values are what the code does since the 2026-10-06 road-flow fixes. A note
    "[Dn]" marks a check whose value that fix changed, or whose value is still today's
    behaviour pending decision Dn. A FAIL is a mismatch with what the API itself intends.

    A submit the API refuses before writing anything (column Refuse: by Validate or by
    CheckRoadRows) is not replayed; section REFUSE checks the fixture against the refusing
    rule instead. A road picked from the street list is sent as the KSRSAC Road_ID and
    Road_Name the list returns since the D2 fix, the way the app sends it after its D3 fix.

    Fixture conventions (all invented): officers 9900001 / 9900002, role 116 (RI), points
    P1 (12.9101, 77.5601) road front and P2 (12.9105, 77.5609) nearest public road, media
    base https://gps.test.invalid/v1/api/gbagps/singlesite/propertyinfo/file/view/, typed
    names 'Test ...'. Officer mobile is NULL (no OTP / mobile involved).

    Sections: 1 guard, 2 run (single batch).
*/

-------------------------------------------------------------------------------
-- 1. GUARD (SET NOEXEC ON stops every later batch in SSMS, which a THROW would not)
-------------------------------------------------------------------------------
IF DB_NAME() <> 'UDD_KHATABTOA_TEST'
   OR ISNULL(CAST(SERVERPROPERTY('InstanceName') AS sysname), N'') <> N'MSSQLSERVER2019'
BEGIN
    RAISERROR('Refusing to run: this script is for UDD_KHATABTOA_TEST on the laptop only.', 16, 1);
    SET NOEXEC ON;
END
ELSE IF @@TRANCOUNT > 0
BEGIN
    RAISERROR('Refusing to run: a transaction is already open on this connection.', 16, 1);
    SET NOEXEC ON;
END
ELSE IF DB_ID(N'ekhata_test') IS NULL
     AND OBJECT_DEFINITION(OBJECT_ID(N'dbo.trg_BtoAMainApp_UpdateNewKhata')) NOT LIKE N'%DB_ID(N''ekhata_test'') IS NULL%'
BEGIN
    RAISERROR('Refusing to run: ekhata_test is absent and the db/23 guard is not applied, so the status write would fail.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 2. RUN
-------------------------------------------------------------------------------
SET NOCOUNT ON;
SET XACT_ABORT OFF;   -- the procedures set it themselves; we must reach ROLLBACK and the report

DECLARE @Role int = 116;
DECLARE @MediaBase nvarchar(200) = N'https://gps.test.invalid/v1/api/gbagps/singlesite/propertyinfo/file/view/';

-- One row per submit, in run order. Later rows on the same App_Id are resubmissions.
-- Refuse = the code the API refuses this payload with before writing (not replayed).
-- eWarn = expected DECLARED_ROAD_NOT_ANSWERED warnings (active, non-blank rows with no entry).
DECLARE @sub TABLE (
    seq int PRIMARY KEY, sc varchar(12), AppId int, Officer bigint,
    Corner int, Sides int, DeclCorrect int, Mutate varchar(20) NULL, Title nvarchar(200),
    Refuse varchar(20) NULL, eWarn int NOT NULL DEFAULT 0);

INSERT @sub (seq, sc, AppId, Officer, Corner, Sides, DeclCorrect, Mutate, Title, Refuse, eWarn) VALUES
 ( 1,'S1'    ,  960,9900001,0,1,1,NULL,N'Declared public road confirmed correct',NULL,0),
 ( 2,'S3'    ,40440,9900001,0,1,1,NULL,N'Second declared road not found (answered correct first, capture left over)',NULL,0),
 ( 3,'S4'    , 5303,9900001,0,1,1,NULL,N'Details wrong -> public road picked from the street list (Road_ID 637)',NULL,0),
 ( 4,'S5'    ,12619,9900001,0,1,1,NULL,N'Details wrong -> private, typed name, roadId 999, gallery notice',NULL,0),
 ( 5,'S6'    ,16211,9900001,1,2,0,NULL,N'Officer-added public road (Road_ID 637)',NULL,0),
 ( 6,'S7'    ,16310,9900001,1,2,0,NULL,N'Officer-added private road (999), camera notice',NULL,0),
 ( 7,'S17c'  ,16310,9900001,1,2,0,NULL,N'Resubmit S7 unchanged (added road)',NULL,0),
 ( 8,'S8'    , 8313,9900001,1,2,1,NULL,N'Corner plot, two declared private roads confirmed, notices jpg + png (S2/S16)',NULL,0),
 ( 9,'S17a'  , 8313,9900001,1,2,1,'appid0',N'Resubmit S8 unchanged (QC return, same officer); its row carries Ofcr_App_Id 0 as written before D33',NULL,0),
 (10,'S17b'  , 8313,9900001,1,2,1,NULL,N'Resubmit: card 1 -> private 999 typed, card 2 -> public (D22)',NULL,0),
 (11,'S17e'  , 8313,9900002,1,2,1,NULL,N'Resubmit S8 by a different officer',NULL,0),
 (12,'S9a'   ,54786,9900001,0,1,0,NULL,N'Road count decreased to 1, card 2 not found (answered wrong)',NULL,0),
 (13,'S9b'   ,  689,9900001,1,5,0,NULL,N'Road count increased to 5, three added public roads',NULL,0),
 (14,'S10b'  ,52590,9900001,1,2,1,NULL,N'Two declared rows with the same Rd_RoadId, both confirmed',NULL,0),
 (15,'S10c'  ,53424,9900001,1,2,1,NULL,N'Both declared not found, two added private roads Test Lane / test lane',NULL,0),
 (16,'S11'   ,19252,9900001,0,1,1,NULL,N'Declared row not sent: only an added public road (N3 warning)',NULL,1),
 (17,'S12ws' ,29452,9900001,1,2,1,NULL,N'Inactive citizen rows, ward-sync payload (active rows only)',NULL,0),
 (18,'S12old',30813,9900001,1,2,1,NULL,N'Inactive citizen rows, old-fetch payload (inactive rows ticked not found)',NULL,0),
 (19,'S13'   ,14325,9900001,0,1,1,NULL,N'Road not in the KSRSAC master',NULL,0),
 (20,'S13v'  ,14852,9900001,0,1,1,'streetnull',N'Property MD_StreetId NULL: correct road reported unverified (D31)',NULL,0),
 (21,'S15v'  ,21187,9900001,0,1,1,NULL,N'Stored road without roadRowId: app sends gpsSid (Site_Id) (D25 / N3)','UNKNOWN_ROAD_ROW',0),
 (22,'S18a'  ,  251,9900001,0,1,1,NULL,N'Private row, Rd_RoadId 0, flag 1, sent with no evidence (D15)','REQUIRED',0),
 (23,'S18b'  ,55932,9900001,1,2,1,NULL,N'Public rows with flag 0 (shown as private, evidence demanded)',NULL,0),
 (24,'S21'   ,36579,9900001,1,2,1,NULL,N'Declared road not found + added replacement (Road_ID 637)',NULL,0),
 (25,'D24'   ,67674,9900001,1,2,1,NULL,N'300-character typed street name','TOO_LONG',0),
 (26,'S17f'  ,  960,9900001,0,1,0,NULL,N'Resubmit S1: the same road now private (update branch writes no document; D22)',NULL,0);

-- One row per road object in the submit payload, in payload order.
-- Caps = road-front P1 + photo and nearest-public P2 + photo. PubOnly = only the nearest-public
-- capture (left over from an earlier answer). Notice = extension of the served-notice file.
-- CitRoadId = the citizen row's Rd_RoadId the fixture assumes (pre-check). SiteRow = RoadRowId
-- is the Site_Id (gpsSid fallback). eMode = expected write path. eKsr = expected KsracMatched.
-- KeptNoticeSeq = notice expected to be the one written by that earlier submit (none since D22).
DECLARE @p TABLE (
    seq int, ord int, RoadRowId int NULL, RoadId varchar(20) NULL, RoadName nvarchar(600) NULL,
    ActualRoadName nvarchar(600) NULL, RoadType nvarchar(100) NULL, IsPresent int, RoadStatus int,
    IsCorrect int NULL, Caps bit, PubOnly bit, Notice varchar(4) NULL, CitRoadId int NULL, SiteRow bit,
    eMode varchar(6), eKsr bit, KeptNoticeSeq int NULL, Note nvarchar(300) NULL,
    wseq int NULL, PRIMARY KEY (seq, ord));

DECLARE @long nvarchar(600) = REPLICATE(N'Test Long Road ', 20);   -- 300 characters

INSERT @p (seq,ord,RoadRowId,RoadId,RoadName,ActualRoadName,RoadType,IsPresent,RoadStatus,IsCorrect,Caps,PubOnly,Notice,CitRoadId,SiteRow,eMode,eKsr,KeptNoticeSeq,Note) VALUES
 -- S1
 ( 1,1,26491,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,0,1,0,0,NULL,976,0,'insert',1,NULL,NULL),
 -- S3
 ( 2,1,22240,'9317',N'Bile Shivale',N'Bile Shivale','public',1,0,1,0,0,NULL,9317,0,'insert',1,NULL,NULL),
 ( 2,2,22241,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,2,1,0,1,NULL,976,0,'insert',1,NULL,N'[D13] not-found road still carries the earlier answer (correct=1) and a nearest-public capture'),
 -- S4
 ( 3,1,28436,'637' ,N'Green woods layout',N'Green woods layout','public',1,1,0,0,0,NULL,976,0,'insert',1,NULL,N'[D2] Road_ID from the street list (was street id 279); [D3] RoadName is the picked road'),
 -- S5
 ( 4,1,33423,'999' ,N'Bile Shivale',N'Test Lane 4','private',0,1,0,1,0,'jpg',976,0,'insert',0,NULL,N'[D7] decision: private branch sends 999'),
 -- S6
 ( 5,1,35784,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,0,1,0,0,NULL,976,0,'insert',1,NULL,NULL),
 ( 5,2,NULL ,'637' ,N'Green woods layout',N'Green woods layout','public',1,3,0,0,0,NULL,NULL,0,'insert',1,NULL,N'[D2][D3]'),
 -- S7
 ( 6,1,35852,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,0,1,0,0,NULL,976,0,'insert',1,NULL,NULL),
 ( 6,2,NULL ,'999' ,NULL,N'Test Service Lane','private',0,3,0,1,0,'jpg',NULL,0,'insert',0,NULL,N'DocumentTran DocumentId NULL'),
 -- S17c (resubmit S7)
 ( 7,1,35852,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,0,1,0,0,NULL,976,0,'update',1,NULL,NULL),
 ( 7,2,NULL ,'999' ,NULL,N'Test Service Lane','private',0,3,0,1,0,'jpg',NULL,0,'insert',0,NULL,N'[D5] SiteRoadRowID NULL never matches the upsert key: inserted again, the earlier row superseded'),
 -- S8
 ( 8,1,884  ,'976' ,N'Bile Shivale',N'LG LAKE DEW','private',0,0,1,1,0,'jpg',976,0,'insert',1,NULL,NULL),
 ( 8,2,885  ,'976' ,N'Bile Shivale',N'ESSEL GARDEN','private',0,0,1,1,0,'png',976,0,'insert',1,NULL,NULL),
 -- S17a (resubmit S8 unchanged)
 ( 9,1,884  ,'976' ,N'Bile Shivale',N'LG LAKE DEW','private',0,0,1,1,0,'jpg',976,0,'update',1,NULL,NULL),
 ( 9,2,885  ,'976' ,N'Bile Shivale',N'ESSEL GARDEN','private',0,0,1,1,0,'png',976,0,'update',1,NULL,NULL),
 -- S17b: card 1 private 999 typed (road id changes), card 2 flipped to public
 (10,1,884  ,'999' ,N'Bile Shivale',N'Test Lane X','private',0,1,0,1,0,'jpg',976,0,'insert',0,NULL,N'[D5] road id changed 976 -> 999: new row; the old 976 row is superseded'),
 (10,2,885  ,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,1,0,0,0,NULL,976,0,'update',1,NULL,N'[D22] the private answer''s notice is cleared and its document deactivated'),
 -- S17e: another officer, same answers as S8
 (11,1,884  ,'976' ,N'Bile Shivale',N'LG LAKE DEW','private',0,0,1,1,0,'jpg',976,0,'insert',1,NULL,N'[D35] decision: second officer rows beside the first officer''s'),
 (11,2,885  ,'976' ,N'Bile Shivale',N'ESSEL GARDEN','private',0,0,1,1,0,'png',976,0,'insert',1,NULL,N'[D35]'),
 -- S9a
 (12,1,12872,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,0,1,0,0,NULL,976,0,'insert',1,NULL,NULL),
 (12,2,12873,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,2,0,0,0,NULL,976,0,'insert',1,NULL,NULL),
 -- S9b
 (13,1,347  ,'624' ,N'Belattur maruthi layout',N'Belattur maruthi layout','public',1,0,1,0,0,NULL,624,0,'insert',1,NULL,NULL),
 (13,2,348  ,'624' ,N'Belattur maruthi layout',N'Belattur maruthi layout','public',1,0,1,0,0,NULL,624,0,'insert',1,NULL,NULL),
 (13,3,NULL ,'637' ,N'Green woods layout',N'Green woods layout','public',1,3,0,0,0,NULL,NULL,0,'insert',1,NULL,N'[D2][D3]'),
 (13,4,NULL ,'655' ,N'Motappa garden Phase 1 and 2',N'Motappa garden Phase 1 and 2','public',1,3,0,0,0,NULL,NULL,0,'insert',1,NULL,N'[D2][D3]'),
 (13,5,NULL ,'980' ,N'Anugraha Layout',N'Anugraha Layout','public',1,3,0,0,0,NULL,NULL,0,'insert',1,NULL,N'[D2][D3]'),
 -- S10b
 (14,1,12290,'980' ,N'Anugraha Layout',N'Anugraha Layout','public',1,0,1,0,0,NULL,980,0,'insert',1,NULL,NULL),
 (14,2,12291,'980' ,N'Anugraha Layout',N'Anugraha Layout','public',1,0,1,0,0,NULL,980,0,'insert',1,NULL,NULL),
 -- S10c
 (15,1,12493,'621' ,N'Ayyappa Nagar Main Road',N'Ayyappa Nagar Main Road','public',1,2,NULL,0,0,NULL,621,0,'insert',1,NULL,NULL),
 (15,2,12494,'621' ,N'Ayyappa Nagar Main Road',N'Ayyappa Nagar Main Road','public',1,2,NULL,0,0,NULL,621,0,'insert',1,NULL,NULL),
 (15,3,NULL ,'999' ,NULL,N'Test Lane','private',0,3,0,1,0,'jpg',NULL,0,'insert',0,NULL,NULL),
 (15,4,NULL ,'999' ,NULL,N' test lane ','private',0,3,0,1,0,'jpg',NULL,0,'insert',0,NULL,N'same road as card 3 by name; no server rule (app-only)'),
 -- S11
 (16,1,NULL ,'637' ,N'Green woods layout',N'Green woods layout','public',1,3,0,0,0,NULL,NULL,0,'insert',1,NULL,N'[D2][D3]'),
 -- S12 ward sync
 (17,1,5595 ,'9343',N'Gramatana limits',N'NORTH BY 40 WIDE ROAD','private',0,0,1,1,0,'jpg',9343,0,'insert',1,NULL,NULL),
 (17,2,5596 ,'9343',N'Gramatana limits',N'WEST BY 25 WIDE ROAD','private',0,0,1,1,0,'jpg',9343,0,'insert',1,NULL,NULL),
 -- S12 old fetch: no entered names, inactive rows present and ticked not found
 (18,1,7126 ,'638' ,N'Hodi Gramatana',N'Hodi Gramatana','private',0,2,NULL,0,0,NULL,638,0,'insert',1,NULL,N'[D14] row the citizen had deactivated (the API old fetch now leaves it out)'),
 (18,2,7473 ,'621' ,N'Ayyappa Nagar Main Road',N'Ayyappa Nagar Main Road','public',0,2,NULL,0,0,NULL,621,0,'insert',1,NULL,N'[D14]'),
 (18,3,7474 ,'636' ,N'Gramatana limits',N'Gramatana limits','private',0,2,NULL,0,0,NULL,636,0,'insert',1,NULL,N'[D14]'),
 (18,4,7475 ,'9343',N'Gramatana limits',N'Gramatana limits','private',0,0,1,1,0,'jpg',9343,0,'insert',1,NULL,N'[D14] typed name lost on an old-fetch payload'),
 (18,5,7477 ,'636' ,N'Gramatana limits',N'Gramatana limits','private',0,0,1,1,0,'jpg',636,0,'insert',1,NULL,N'[D14]'),
 -- S13
 (19,1,34582,'123456',N'Test Unknown Road',N'Test Unknown Road','public',1,0,1,0,0,NULL,976,0,'insert',0,NULL,N'fixture road id/name (citizen row holds 976)'),
 -- S13 variant
 (20,1,34962,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,0,1,0,0,NULL,976,0,'insert',0,NULL,N'[D31] correct road, but no MD_StreetId -> no mst_AROMapping row -> unverified (DBA request F5)'),
 -- S15 variant: RoadRowId = Site_Id (refused, N3)
 (21,1,12066,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,0,1,0,0,NULL,NULL,1,'insert',1,NULL,N'[D25][N3] Site_Id as the road row id'),
 -- S18a: roadName = privateRoadName, actualRoadName = privateRoadText, roadId '0' (refused, D15)
 (22,1,26188,'0'   ,N'Elus Road',N'Bile Shivale','private',1,0,1,0,0,NULL,0,0,'insert',0,NULL,N'[D1][D15][D32] private road with no evidence'),
 -- S18b
 (23,1,13202,'976' ,N'Bile Shivale',N'2nd Cross , Asha Township','public',0,0,1,1,0,'jpg',976,0,'insert',1,NULL,N'[D1] public road with evidence; no DocumentTran (type public)'),
 (23,2,13203,'976' ,N'Bile Shivale',N'3rd Cross , Asha Township','public',0,0,1,1,0,'jpg',976,0,'insert',1,NULL,N'[D1]'),
 -- S21
 (24,1,18858,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,0,1,0,0,NULL,976,0,'insert',1,NULL,NULL),
 (24,2,18859,'976' ,N'Bile Shivale',N'Bile Shivale','public',1,2,NULL,0,0,NULL,976,0,'insert',1,NULL,NULL),
 (24,3,NULL ,'637' ,N'Green woods layout',N'Green woods layout','public',1,3,0,0,0,NULL,NULL,0,'insert',1,NULL,N'[D2][D3]'),
 -- D24: names longer than 250 (refused, TOO_LONG)
 (25,1,16351,'976' ,N'Bile Shivale',@long,'public',0,0,1,1,0,'jpg',976,0,'insert',1,NULL,N'[D24] proc parameter and column are nvarchar(250)'),
 (25,2,16353,'976' ,N'Bile Shivale',N'DODDAGUBBI MAIN ROAD','public',0,0,1,1,0,'jpg',976,0,'insert',1,NULL,NULL),
 -- S17f (resubmit S1): the declared road is now private, same road id -> the proc's update branch
 (26,1,26491,'976' ,N'Bile Shivale',N'Test Lane P','private',0,1,0,1,0,'jpg',976,0,'update',1,NULL,N'[D22] the update branch inserts no document; EnsureDocumentSql adds it');

-- WriteRoadsAsync order: OrderBy(RoadRowId ?? 0), stable on payload order.
;WITH o AS (SELECT seq, ord, ROW_NUMBER() OVER (PARTITION BY seq ORDER BY ISNULL(RoadRowId,0), ord) AS w FROM @p)
UPDATE p SET wseq = o.w FROM @p p JOIN o ON o.seq = p.seq AND o.ord = p.ord;

DECLARE @res TABLE (id int IDENTITY PRIMARY KEY, sec varchar(10), sc varchar(12), seq int NULL,
    item nvarchar(60), chk nvarchar(80), expected nvarchar(400), actual nvarchar(400),
    result AS (CASE WHEN ISNULL(expected, N'<NULL>') + N'|' = ISNULL(actual, N'<NULL>') + N'|' COLLATE Latin1_General_BIN2
                    THEN 'PASS' ELSE 'FAIL' END),
    note nvarchar(300));

-------------------------------------------------------------------------------
-- 2a. PRE-CHECK: the chosen applications still have the shapes the fixtures assume
-------------------------------------------------------------------------------
DECLARE @pre TABLE (problem nvarchar(300));

INSERT @pre
SELECT CONCAT(N'seq ', s.seq, N' app ', s.AppId, N': ', x.why)
FROM @sub s
CROSS APPLY (SELECT TOP 1 why FROM (VALUES
    (CASE WHEN s.AppId IN (113, 370) THEN N'App 113/370 must never be used' END),
    (CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.BtoAMainApp a WHERE a.App_Id = s.AppId AND a.App_Status = 10 AND a.App_Active = 1)
          THEN N'not an active status-10 application' END),
    (CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.BtoA_EPIDMetaData m WHERE m.MD_APP_ID = s.AppId AND m.MD_ZoneId = 102 AND m.MD_WardId = 54)
          THEN N'not in zone 102 / ward 54' END),
    (CASE WHEN EXISTS (SELECT 1 FROM dbo.BtoAMainApp a JOIN dbo.BtoA_MainApp_Officer o ON o.Ofcr_ApplicationDisplayId = a.App_DisplayId WHERE a.App_Id = s.AppId)
            OR EXISTS (SELECT 1 FROM dbo.BtoA_SiteRoadDetails_Officer o WHERE o.Ofcr_App_Id = s.AppId)
            OR EXISTS (SELECT 1 FROM dbo.BtoA_DocumentTran d WHERE d.docTrn_App_Id = s.AppId AND d.docTrn_Mdoc_id = 4 AND d.UniqueIdentifier = N'444')
          THEN N'already has officer rows or private-road documents' END)
    ) w(why) WHERE why IS NOT NULL) x
WHERE s.seq = (SELECT MIN(s2.seq) FROM @sub s2 WHERE s2.AppId = s.AppId);

INSERT @pre
SELECT CONCAT(N'seq ', p.seq, N' road ', p.RoadRowId, N': citizen row missing or Rd_RoadId <> ', p.CitRoadId)
FROM @p p JOIN @sub s ON s.seq = p.seq
WHERE p.CitRoadId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.BtoA_SiteRoadDetails r
                  WHERE r.Rd_RoadRow_ID = p.RoadRowId AND r.Rd_App_Id = s.AppId AND r.Rd_RoadId = p.CitRoadId);

INSERT @pre
SELECT CONCAT(N'seq ', p.seq, N': ', p.RoadRowId, N' is not the Site_Id of app ', s.AppId)
FROM @p p JOIN @sub s ON s.seq = p.seq
WHERE p.SiteRow = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.BtoA_SiteDtls d WHERE d.Site_App_Id = s.AppId AND d.Site_Id = p.RoadRowId);

IF EXISTS (SELECT 1 FROM @pre)
BEGIN
    SELECT 'PRECHECK FAILED - nothing was run' AS section, problem FROM @pre;
    RETURN;
END

-------------------------------------------------------------------------------
-- 2b. REFUSE: the payloads the API refuses before writing, by the rule that refuses them.
--     Validate runs first (TOO_LONG, D24; the private-road evidence, D15), then
--     CheckRoadRows against the application's rows (UNKNOWN_ROAD_ROW, N3).
-------------------------------------------------------------------------------
INSERT @res (sec, sc, seq, item, chk, expected, actual, note)
SELECT 'REFUSE', s.sc, s.seq, CONCAT(N'app ', s.AppId), N'refused before any write with', s.Refuse,
       CASE
         WHEN EXISTS (SELECT 1 FROM @p p WHERE p.seq = s.seq AND (LEN(p.RoadName) > 250 OR LEN(p.ActualRoadName) > 250))
           THEN 'TOO_LONG'
         WHEN EXISTS (SELECT 1 FROM @p p WHERE p.seq = s.seq AND p.RoadStatus <> 2 AND LTRIM(RTRIM(p.RoadType)) = N'private'
                                           AND (p.Caps = 0 OR p.Notice IS NULL))
           THEN 'REQUIRED'
         WHEN EXISTS (SELECT 1 FROM @p p WHERE p.seq = s.seq AND p.RoadRowId IS NOT NULL
                                           AND NOT EXISTS (SELECT 1 FROM dbo.BtoA_SiteRoadDetails r
                                                            WHERE r.Rd_App_Id = s.AppId AND r.Rd_RoadRow_ID = p.RoadRowId))
           THEN 'UNKNOWN_ROAD_ROW'
       END,
       CASE s.Refuse WHEN 'UNKNOWN_ROAD_ROW' THEN N'[N3] used to be filed as given (Site_Id stored as Ofcr_SiteRoadRowID)'
                     WHEN 'REQUIRED' THEN N'[D15] used to be stored with a NULL-url private-road document'
                     WHEN 'TOO_LONG' THEN N'[D24] used to be cut to 250 characters' END
  FROM @sub s;

-------------------------------------------------------------------------------
-- 2c. BASELINE (read before the transaction; compared after the rollback)
-------------------------------------------------------------------------------
DECLARE @appList nvarchar(max) = (SELECT STRING_AGG(CAST(AppId AS nvarchar(20)), N',') FROM (SELECT DISTINCT AppId FROM @sub) d) + N',113,370';
DECLARE @snapSql nvarchar(max) = N'
SELECT k, v FROM (VALUES
 (N''BtoA_SiteRoadDetails_Officer rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_SiteRoadDetails_Officer)),
 (N''BtoA_SiteRoadDetails_Officer max Ofcr_RowId'', (SELECT CAST(MAX(Ofcr_RowId) AS bigint) FROM dbo.BtoA_SiteRoadDetails_Officer)),
 (N''BtoA_MainApp_Officer rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_MainApp_Officer)),
 (N''BtoA_MainApp_Officer Ofcr_App_Id checksum'', (SELECT CAST(CHECKSUM_AGG(CHECKSUM(Ofcr_RowId, Ofcr_App_Id)) AS bigint) FROM dbo.BtoA_MainApp_Officer)),
 (N''BtoA_StatusDetail_Officer rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_StatusDetail_Officer)),
 (N''BtoA_DocumentTran rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_DocumentTran)),
 (N''BtoA_DocumentTran active checksum'', (SELECT CAST(CHECKSUM_AGG(CHECKSUM(docTrn_Id, docTrn_Active)) AS bigint) FROM dbo.BtoA_DocumentTran)),
 (N''BtoAMainApp_Hist rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoAMainApp_Hist)),
 (N''BtoA_Status_Master_Tran_Hist rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_Status_Master_Tran_Hist)),
 (N''BtoA_MainApp_Officer_Hist rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_MainApp_Officer_Hist)),
 (N''BtoA_StatusDetail_Officer_Hist rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_StatusDetail_Officer_Hist)),
 (N''BtoA_Architect_AssignedApp_hist rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_Architect_AssignedApp_hist)),
 (N''BtoA_SiteRoadDetails_Officer_Hist rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_SiteRoadDetails_Officer_Hist)),
 (N''BtoA_SiteRoadDetails_hist rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_SiteRoadDetails_hist)),
 (N''test apps + 113/370: BtoAMainApp checksum'', (SELECT CAST(CHECKSUM_AGG(CHECKSUM(App_Id, App_Status, App_UDte, App_AdditionalInfo, App_Active)) AS bigint) FROM dbo.BtoAMainApp WHERE App_Id IN (' + @appList + N'))),
 (N''test apps + 113/370: App_Status sum'', (SELECT SUM(CAST(App_Status AS bigint)) FROM dbo.BtoAMainApp WHERE App_Id IN (' + @appList + N'))),
 (N''test apps + 113/370: EPIDMetaData checksum'', (SELECT CAST(CHECKSUM_AGG(CHECKSUM(MD_APP_ID, MD_StreetId, MD_UDte)) AS bigint) FROM dbo.BtoA_EPIDMetaData WHERE MD_APP_ID IN (' + @appList + N'))),
 (N''test apps + 113/370: Status_Master_Tran checksum'', (SELECT CAST(CHECKSUM_AGG(CHECKSUM(Sts_MainAppId, Sts_MainAppStatus, Sts_CurrStatusId, Udt)) AS bigint) FROM dbo.BtoA_Status_Master_Tran WHERE Sts_MainAppId IN (' + @appList + N'))),
 (N''test apps + 113/370: AssignedApp checksum'', (SELECT CAST(CHECKSUM_AGG(CHECKSUM(AppId, Status, IsActive, CompletedDate, UDate)) AS bigint) FROM dbo.BtoA_Architect_AssignedApp WHERE AppId IN (' + @appList + N'))),
 (N''test apps + 113/370: officer road rows'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id IN (' + @appList + N'))),
 (N''officers 9900001/9900002: rows anywhere'', (SELECT COUNT_BIG(*) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE CBy IN (9900001, 9900002))
     + (SELECT COUNT_BIG(*) FROM dbo.BtoA_MainApp_Officer WHERE CBy IN (9900001, 9900002))
     + (SELECT COUNT_BIG(*) FROM dbo.BtoA_StatusDetail_Officer WHERE CBy IN (9900001, 9900002))
     + (SELECT COUNT_BIG(*) FROM dbo.BtoA_DocumentTran WHERE docTrn_CBy IN (9900001, 9900002)))
) t(k, v);';

DECLARE @before TABLE (k nvarchar(100) PRIMARY KEY, v bigint NULL);
DECLARE @after  TABLE (k nvarchar(100) PRIMARY KEY, v bigint NULL);
INSERT @before EXEC sys.sp_executesql @snapSql;

-------------------------------------------------------------------------------
-- 2d. REPLAY (one transaction, always rolled back)
-------------------------------------------------------------------------------
-- What each road write returned and what it stored (snapshot after its submit finished).
DECLARE @ret TABLE (seq int, ord int, OfcrRowId int NULL, St bit NULL, Ksr bit NULL, Msg nvarchar(400) NULL,
    PrivUrl nvarchar(600) NULL, PubUrl nvarchar(600) NULL, NoticeUrl nvarchar(600) NULL, NoticeRows int NULL,
    DocDelta int NULL, NewDocDocumentId nvarchar(100) NULL, NewDocUrl nvarchar(600) NULL, NewDocActive int NULL,
    DocUrlRowsForSrid int NULL, PRIMARY KEY (seq, ord));
DECLARE @snap TABLE (seq int, ord int, Ofcr_App_Id int, DisplayId nvarchar(50), CorrRoad int, CtId int, CtVal nvarchar(50),
    RT nvarchar(50), PUB int, RID int NULL, RN nvarchar(600) NULL, ARN nvarchar(600) NULL, SRID nvarchar(500) NULL,
    PrivLat float NULL, PrivLng float NULL, PrivDoc nvarchar(600) NULL, PubLat float NULL, PubLng float NULL,
    PubDoc nvarchar(600) NULL, Notice nvarchar(600) NULL, Act int NULL, CBy bigint NULL, UBy bigint NULL,
    PRIMARY KEY (seq, ord));
DECLARE @main TABLE (seq int PRIMARY KEY, LockRc int, LookupAppId int NULL, AppMsg nvarchar(400), AppSt bit,
    MainRows int, Ofcr_App_Id int NULL, CorrRoad int NULL, Corner int NULL, Sides int NULL, NearLat nvarchar(50) NULL,
    NearLng nvarchar(50) NULL, MainCBy bigint NULL, MainUBy bigint NULL, Aditional nvarchar(max) NULL,
    AppStatus int NULL, AppAddl nvarchar(400) NULL, ActiveStatusRowsOfficer int NULL, StatusRowsAllActive int NULL,
    MainHistDelta int NULL, AssignRows int NULL, Held int NULL, Unknown int NULL, Warn int NULL, Superseded int NULL);
DECLARE @inTx TABLE (k nvarchar(100), v nvarchar(400));

DECLARE @appRet TABLE (Message nvarchar(max), Status bit, AppId int, DisplayRequestID nvarchar(50));
DECLARE @rr TABLE (Message nvarchar(400), Status bit, RoadRowId int, DisplayRequestID varchar(50), KsracMatched bit);
DECLARE @rows TABLE (RowsAffected int);
DECLARE @heldT TABLE (n int);
DECLARE @declared TABLE (RowId int, Active bit, RoadId nvarchar(50), RoadName nvarchar(max), EnteredRoadName nvarchar(max),
    PrivateRoadName nvarchar(max), PrivateRoadText nvarchar(max));
DECLARE @live TABLE (v nvarchar(100), url nvarchar(600));
DECLARE @hist TABLE (RoadCount int, StreetName nvarchar(600));

DECLARE @seq int, @app int, @disp nvarchar(50), @epid nvarchar(100), @off bigint, @corner int, @sides int,
        @decl int, @mutate varchar(20), @lookup int, @lockRc int, @stamp nvarchar(20), @nearLat nvarchar(50),
        @nearLng nvarchar(50), @aditional nvarchar(max), @histBefore int, @k int, @kmax int, @ord int,
        @rowId int, @roadId varchar(20), @rn nvarchar(600), @arn nvarchar(600), @rt nvarchar(100), @pub int,
        @st int, @corr int, @caps bit, @pubOnly bit, @notice varchar(4), @ordinal nvarchar(20),
        @privUrl nvarchar(600), @pubUrl nvarchar(600), @noticeUrl nvarchar(600), @docBefore int, @docAfter int,
        @maxDocRoad bigint, @now datetime, @err nvarchar(2000), @sqlBit1 bit, @ctv nvarchar(100), @rid int,
        @srid nvarchar(1000), @pLat float, @pLng float, @nLat float, @nLng float, @retRow int, @lockRes nvarchar(255),
        @corrRoad bit, @rowsBefore int,
        -- the parameter names of the API's own SQL, so it runs here word for word
        @id nvarchar(50), @appId int, @cby bigint, @crole int, @startedAt datetime, @maxDocBefore bigint,
        @roadRowId int, @url nvarchar(600), @officerId bigint;

BEGIN TRAN;
BEGIN TRY
    SET @seq = (SELECT MIN(seq) FROM @sub WHERE Refuse IS NULL);
    WHILE @seq IS NOT NULL
    BEGIN
        SELECT @app = s.AppId, @off = s.Officer, @corner = s.Corner, @sides = s.Sides, @decl = s.DeclCorrect,
               @mutate = s.Mutate
          FROM @sub s WHERE s.seq = @seq;
        SELECT @disp = a.App_DisplayId FROM dbo.BtoAMainApp a WHERE a.App_Id = @app;
        SELECT @epid = m.MD_MotherEPID FROM dbo.BtoA_EPIDMetaData m WHERE m.MD_APP_ID = @app;
        SET @stamp = CONCAT(N'20261005_12', RIGHT(N'00' + CAST(@seq AS nvarchar(3)), 2), N'00');   -- distinct per submit
        SET @now = GETUTCDATE();
        SELECT @id = @disp, @cby = @off, @crole = @Role;

        -- Real submits are seconds apart. Here they are milliseconds apart, and datetime ticks
        -- every 3.33 ms: without a pause a row the previous submit stamped could share the
        -- tick of this submit's @startedAt and escape SupersedeRoadRowsSql.
        WAITFOR DELAY '00:00:00.020';

        -- scenario setup only; rolled back with everything else
        IF @mutate = 'streetnull'
            UPDATE dbo.BtoA_EPIDMetaData SET MD_StreetId = NULL WHERE MD_APP_ID = @app;
        IF @mutate = 'appid0'      -- the officer row as the API wrote it before the D33 fix
            UPDATE dbo.BtoA_MainApp_Officer SET Ofcr_App_Id = 0 WHERE Ofcr_ApplicationDisplayId = @disp;

        -- _firstPublic: the first road carrying a nearest-public capture
        IF EXISTS (SELECT 1 FROM @p WHERE seq = @seq AND (Caps = 1 OR PubOnly = 1))
            SELECT @nearLat = N'12.9105', @nearLng = N'77.5609';
        ELSE
            SELECT @nearLat = NULL, @nearLng = NULL;

        SET @aditional = CONCAT(N'{"IsLandUntraceable":0,"IsLandLocationUpdated":0,"KhataRecommendation":null,',
            N'"KhataComments":null,"SingleSiteId":null,"ApplicationType":"BtoAKhata","siteArea":null,',
            N'"eastWest":null,"northSouth":null,"isDeclaredRoadFacingSidesCorrect":', @decl, N'}');

        SET @histBefore = (SELECT COUNT(*) FROM dbo.BtoAMainApp_Hist WHERE App_Id = @app);

        -- SubmitAsync: lock, App_Id lookup
        SET @lockRes = N'gps:submit:' + @disp;
        EXEC @lockRc = sys.sp_getapplock @Resource = @lockRes,@LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;
        SET @lookup = (SELECT App_Id FROM dbo.BtoAMainApp WHERE App_DisplayId = @disp AND App_Active = 1);
        SET @appId = @lookup;

        -- CheckRoadRows over DeclaredRoadsSql (the refusal is checked in section REFUSE;
        -- here: unknown ids, which must be 0 for a replayed submit, and the warnings)
        DELETE @declared;
        INSERT @declared (RowId, Active, RoadId, RoadName, EnteredRoadName, PrivateRoadName, PrivateRoadText)
        SELECT RowId = Rd_RoadRow_ID,
               Active = CAST(CASE WHEN ISNULL(Rd_RoadActive, 1) = 1 THEN 1 ELSE 0 END AS bit),
               RoadId = CAST(Rd_RoadId AS nvarchar(50)), RoadName = Rd_RoadName,
               EnteredRoadName = Rd_EnteredRoadName, PrivateRoadName = Rd_PrivateRoadName,
               PrivateRoadText = Rd_PrivateRoadText
        FROM dbo.BtoA_SiteRoadDetails
        WHERE Rd_App_Id = @appId;

        -- WriteApplicationAsync: heal, write, held
        UPDATE dbo.BtoA_MainApp_Officer SET Ofcr_App_Id = @appId
         WHERE Ofcr_ApplicationDisplayId = @id AND Ofcr_App_Id = 0;

        -- RoadDetailsCorrected: the app sends siteDetails.isRoadDetailsCorrect null, so any
        -- road that is not unchanged means corrected (D9)
        SET @corrRoad = CASE WHEN EXISTS (SELECT 1 FROM @p WHERE seq = @seq AND RoadStatus <> 0) THEN 1 ELSE 0 END;

        DELETE @appRet;
        INSERT @appRet
        EXEC dbo.USP_IU_BtoA_MainApp_Officer
            @Ofcr_RowId = 0, @Ofcr_ApplicationDisplayId = @disp, @Ofcr_App_Id = @lookup,
            @Ofcr_SsaId = NULL, @Ofcr_MotherEPID = @epid, @Ofcr_ApplicationDate = @now,
            @Ofcr_PropertyLandExistOnSpot = 1, @Ofcr_IsAllBhoomiSurveyNosCorrect = 1, @Ofcr_IsGovtProperty = 0,
            @Ofcr_CorrectedGpsLocation = 0, @Ofcr_CorrectedLatitude = NULL, @Ofcr_CorrectedLongitude = NULL,
            @Ofcr_CorrectedPropertyUseType = 0, @Ofcr_CorrectedPropertyUseTypeId = NULL,
            @Ofcr_CorrectedPropertyUseTypeValue = NULL, @Ofcr_CorrectedCommercialArea = NULL,
            @Ofcr_CorrectedResidentialArea = NULL, @Ofcr_CorrectedIndustrialArea = NULL,
            @Ofcr_CorrectedRoadDetails = @corrRoad,
            @Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing = @corner, @Ofcr_RoadFacingSides = @sides,
            @Ofcr_RoadWidthInFrontOfPropertyInSqft = 29.527559999999998, @Ofcr_RoadWidthInFrontOfPropertyInSqmt = 9.0,
            @Ofcr_nearest_public_Road_Latitude = @nearLat, @Ofcr_nearest_public_Road_Longitude = @nearLng,
            @Ofcr_gpsStatusFullWorkflowJSON = NULL, @Ofcr_Aditional = @aditional,
            @CBy = @off, @CRole = @Role, @matchedSurveyNo = NULL, @surveyRemark = N'Test road scenario',
            @Ofcr_IsBuildingExists = 0, @mstPlanPropertyUseType = NULL;

        DELETE @heldT;
        INSERT @heldT
        SELECT COUNT(*) FROM dbo.BtoA_MainApp_Officer
         WHERE Ofcr_ApplicationDisplayId = @id AND Ofcr_App_Id = @appId;

        -- WriteRoadsAsync
        SET @startedAt = (SELECT GETDATE());
        SET @maxDocBefore = (SELECT ISNULL(MAX(docTrn_Id), 0) FROM dbo.BtoA_DocumentTran
                              WHERE docTrn_App_Id = @appId AND docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444');
        DELETE @live;

        SELECT @k = 1, @kmax = (SELECT MAX(wseq) FROM @p WHERE seq = @seq);
        WHILE @k <= @kmax
        BEGIN
            SELECT @ord = ord, @rowId = RoadRowId, @roadId = RoadId, @rn = RoadName, @arn = ActualRoadName,
                   @rt = RoadType, @pub = IsPresent, @st = RoadStatus, @corr = IsCorrect, @caps = Caps,
                   @pubOnly = PubOnly, @notice = Notice
              FROM @p WHERE seq = @seq AND wseq = @k;

            -- LocalDiskMediaStore name: {epid}-{RoadRowId|0}-{slot}-{stamp}-{8hex}.ext
            SET @ordinal = ISNULL(CAST(@rowId AS nvarchar(20)), N'0');
            SET @privUrl = CASE WHEN @caps = 1 THEN CONCAT(@MediaBase, @epid, N'/', @epid, N'-', @ordinal, N'-private-', @stamp, N'-',
                                 LOWER(LEFT(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N''), 8)), N'.jpg') END;
            SET @pubUrl  = CASE WHEN @caps = 1 OR @pubOnly = 1 THEN CONCAT(@MediaBase, @epid, N'/', @epid, N'-', @ordinal, N'-public-', @stamp, N'-',
                                 LOWER(LEFT(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N''), 8)), N'.jpg') END;
            SET @noticeUrl = CASE WHEN @notice IS NOT NULL THEN CONCAT(@MediaBase, @epid, N'/', @epid, N'-', @ordinal, N'-notice-', @stamp, N'-',
                                 LOWER(LEFT(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N''), 8)), N'.', @notice) END;

            SELECT @docBefore = COUNT(*), @maxDocRoad = ISNULL(MAX(docTrn_Id), 0)
              FROM dbo.BtoA_DocumentTran WHERE docTrn_App_Id = @app AND docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444';

            -- The exact parameter values RoadParameters builds:
            --   Corrected = RoadCorrected (the answer, else roadStatus <> 0); Type_Id = roadStatus;
            --   Type_Value = CorrectionTypeValue(roadStatus); IsPresent = AsBit(flag);
            --   RoadId = int.TryParse(roadId) else NULL; SiteRoadRowID = RoadRowId?.ToString()
            SET @sqlBit1 = CASE WHEN @corr = 0 THEN 1 WHEN @corr = 1 THEN 0 WHEN @st <> 0 THEN 1 ELSE 0 END;
            SET @ctv = CASE @st WHEN 0 THEN N'unchanged' WHEN 1 THEN N'updated' WHEN 2 THEN N'delete' WHEN 3 THEN N'added' END;
            SET @rid = TRY_CAST(@roadId AS int);
            SET @srid = CAST(@rowId AS nvarchar(20));
            SET @pLat = CASE WHEN @caps = 1 THEN 12.9101 END;
            SET @pLng = CASE WHEN @caps = 1 THEN 77.5601 END;
            SET @nLat = CASE WHEN @caps = 1 OR @pubOnly = 1 THEN 12.9105 END;
            SET @nLng = CASE WHEN @caps = 1 OR @pubOnly = 1 THEN 77.5609 END;

            DELETE @rr;
            INSERT @rr
            EXEC dbo.USP_IU_BtoA_SiteRoadDetails_Officer
                @BtoA_RoadRowId = 0, @BtoA_MainAppId = 0, @BtoA_ApplicationDisplayId = @disp,
                @BtoA_Corrected_Road_Details = @sqlBit1,
                @BtoA_Correction_Type_Id = @st, @BtoA_Correction_Type_Value = @ctv,
                @BtoA_RoadType = @rt, @BtoA_IsPresentInPublicRoadList = @pub,
                @BtoA_RoadId = @rid, @BtoA_RoadName = @rn, @BtoA_ActualRoadName = @arn,
                @BtoA_Nearest_Public_Road_Latitude = @nLat, @BtoA_Nearest_Public_Road_Longitude = @nLng,
                @CBy = @off, @CRole = @Role,
                @BtoA_Nearest_Private_Road_Latitude = @pLat, @BtoA_Nearest_Private_Road_Longitude = @pLng,
                @BtoA_Nearest_Private_Road_Document = @privUrl, @BtoA_Nearest_Public_Road_Document = @pubUrl,
                @BtoA_SiteRoadRowID = @srid;

            SELECT @docAfter = COUNT(*)
              FROM dbo.BtoA_DocumentTran WHERE docTrn_App_Id = @app AND docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444';

            INSERT @ret (seq, ord, OfcrRowId, St, Ksr, Msg, PrivUrl, PubUrl, NoticeUrl, DocDelta)
            SELECT @seq, @ord, RoadRowId, Status, KsracMatched, Message, @privUrl, @pubUrl, @noticeUrl, @docAfter - @docBefore
              FROM @rr;

            UPDATE r SET NewDocDocumentId = d.DocumentId, NewDocUrl = d.docTrn_url, NewDocActive = CAST(d.docTrn_Active AS int)
              FROM @ret r
              CROSS APPLY (SELECT TOP 1 * FROM dbo.BtoA_DocumentTran d
                           WHERE d.docTrn_App_Id = @app AND d.docTrn_Mdoc_id = 4 AND d.UniqueIdentifier = N'444'
                             AND d.docTrn_Id > @maxDocRoad ORDER BY d.docTrn_Id DESC) d
             WHERE r.seq = @seq AND r.ord = @ord;

            UPDATE @ret SET DocUrlRowsForSrid =
                (SELECT COUNT(*) FROM dbo.BtoA_DocumentTran d
                  WHERE d.docTrn_App_Id = @app AND d.docTrn_Mdoc_id = 4 AND d.UniqueIdentifier = N'444'
                    AND d.DocumentId = @srid AND d.docTrn_url = @privUrl)
             WHERE seq = @seq AND ord = @ord;

            -- if (row.RoadRowId > 0): the notice if there is one, else ClearNoticeSql
            SET @retRow = (SELECT OfcrRowId FROM @ret WHERE seq = @seq AND ord = @ord);
            IF @retRow > 0 AND @notice IS NOT NULL
            BEGIN
                DELETE @rows;
                INSERT @rows EXEC dbo.USP_U_BtoA_GpsRoadNoticeDocument @RoadRowId = @retRow, @NoticeDocument = @noticeUrl;
                UPDATE @ret SET NoticeRows = (SELECT MAX(RowsAffected) FROM @rows) WHERE seq = @seq AND ord = @ord;
            END
            ELSE IF @retRow > 0
            BEGIN
                SET @roadRowId = @retRow;
                UPDATE dbo.BtoA_SiteRoadDetails_Officer
                   SET Ofcr_Notice_Document = NULL
                 WHERE Ofcr_RowId = @roadRowId AND Ofcr_App_Id = @appId AND CBy = @cby
                   AND Ofcr_Notice_Document IS NOT NULL;
            END

            -- livePrivate: a live private declared road, with its private-road photo
            IF @st <> 2 AND LTRIM(RTRIM(@rt)) = N'private' AND @rowId IS NOT NULL
                INSERT @live (v, url) VALUES (@srid, @privUrl);

            SET @k += 1;
        END

        -- SupersedeEarlierRowsAsync
        SET @rowsBefore = (SELECT COUNT(*) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id = @app AND isActive = 1);

        UPDATE dbo.BtoA_SiteRoadDetails_Officer
           SET isActive = 0, UBy = @cby, UDte = GETDATE(), URole = @crole
         WHERE Ofcr_ApplicationDisplayId = @id AND CBy = @cby AND CRole = @crole
           AND isActive = 1 AND ISNULL(UDte, CDte) < @startedAt;

        UPDATE d
           SET docTrn_Active = 0, docTrn_UBy = @cby, docTrn_UDte = GETDATE(), docTrn_URole = @crole
          FROM dbo.BtoA_DocumentTran d
         WHERE d.docTrn_App_Id = @appId AND d.docTrn_Mdoc_id = 4 AND d.UniqueIdentifier = N'444'
           AND d.docTrn_CBy = @cby AND d.docTrn_Active = 1
           AND NOT (
                (d.DocumentId IS NULL AND d.docTrn_Id > @maxDocBefore)
             OR (d.DocumentId IS NOT NULL AND d.DocumentId IN (SELECT v FROM @live)
                 AND d.docTrn_Id = (SELECT MAX(x.docTrn_Id) FROM dbo.BtoA_DocumentTran x
                                     WHERE x.docTrn_App_Id = d.docTrn_App_Id AND x.docTrn_Mdoc_id = 4
                                       AND x.UniqueIdentifier = N'444' AND x.DocumentId = d.DocumentId
                                       AND x.docTrn_CBy = @cby)));

        SET @srid = (SELECT MIN(v) FROM @live);
        WHILE @srid IS NOT NULL
        BEGIN
            SET @url = (SELECT url FROM @live WHERE v = @srid);
            IF NOT EXISTS (SELECT 1 FROM dbo.BtoA_DocumentTran
                            WHERE docTrn_App_Id = @appId AND docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444'
                              AND DocumentId = @srid AND docTrn_CBy = @cby AND docTrn_Active = 1)
                INSERT INTO dbo.BtoA_DocumentTran
                       (docTrn_App_Id, docTrn_Mdoc_id, docTrn_Active, docTrn_url, docTrn_CBy, docTrn_CDte,
                        docTrn_CRole, DigitalSketchUpload_flag, UniqueIdentifier, DocumentId)
                VALUES (@appId, 4, 1, @url, @cby, GETDATE(), @crole, 1, N'444', @srid);
            SET @srid = (SELECT MIN(v) FROM @live WHERE v > @srid);
        END

        -- WriteExtrasAsync (no property/map/note-sheet photos in these fixtures)
        DELETE @rows;
        INSERT @rows EXEC dbo.USP_U_BtoA_GpsSubmitExtras_App
            @Ofcr_ApplicationDisplayId = @disp, @PropertyDocument = NULL, @MapDocument = NULL,
            @NoteSheetDocument = NULL, @GovtPropertyDetails = NULL, @IsGovtPropertyValue = 0;

        -- WriteStatusAsync: VerdictFor(null) = (10, null). This is the App_Status -> 13 update
        -- that fires trg_BtoAMainApp and trg_BtoAMainApp_UpdateNewKhata (db/23 guard).
        EXEC dbo.USP_IU_BtoA_StatusDetail_Officer
            @Ofcr_StatusRowId = 0, @Ofcr_App_Id = @lookup, @Ofcr_ApplicationDisplayId = @disp,
            @Ofcr_Mobile_Number = NULL, @Status_Date = @now, @Status_Id = 10,
            @Status_Remark = N'Test road scenario', @Status_Rejected_Reason = NULL, @Status_Value = NULL,
            @CBy = @off, @CRole = @Role, @isAutoEscalated = 0, @autoEscalatedDate = NULL;

        -- CompleteAssignmentAsync (the test officers hold no assignment: 0 rows)
        DELETE @rows;
        INSERT @rows EXEC dbo.USP_U_BtoA_GpsCompleteAssignment @AppId = @lookup, @OfficerId = @off, @RoleId = @Role;

        -- Snapshot what this submit left behind
        INSERT @main (seq, LockRc, LookupAppId, AppMsg, AppSt, MainRows, Ofcr_App_Id, CorrRoad, Corner, Sides, NearLat, NearLng,
                      MainCBy, MainUBy, Aditional, AppStatus, AppAddl, ActiveStatusRowsOfficer, StatusRowsAllActive,
                      MainHistDelta, AssignRows, Held, Unknown, Warn, Superseded)
        SELECT @seq, @lockRc, @lookup,
               (SELECT TOP 1 Message FROM @appRet ORDER BY Status), (SELECT MIN(CAST(Status AS int)) FROM @appRet),
               (SELECT COUNT(*) FROM dbo.BtoA_MainApp_Officer WHERE Ofcr_ApplicationDisplayId = @disp),
               o.Ofcr_App_Id, CAST(o.Ofcr_CorrectedRoadDetails AS int), CAST(o.Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing AS int),
               o.Ofcr_RoadFacingSides, o.Ofcr_nearest_public_Road_Latitude, o.Ofcr_nearest_public_Road_Longitude,
               o.CBy, o.UBy, o.Ofcr_Aditional,
               a.App_Status, a.App_AdditionalInfo,
               (SELECT COUNT(*) FROM dbo.BtoA_StatusDetail_Officer WHERE Ofcr_App_Id = @app AND CBy = @off AND Status_Active = 1),
               (SELECT COUNT(*) FROM dbo.BtoA_StatusDetail_Officer WHERE Ofcr_App_Id = @app AND Status_Active = 1),
               (SELECT COUNT(*) FROM dbo.BtoAMainApp_Hist WHERE App_Id = @app) - @histBefore,
               (SELECT MAX(RowsAffected) FROM @rows),
               (SELECT MAX(n) FROM @heldT),
               (SELECT COUNT(*) FROM @p p WHERE p.seq = @seq AND p.RoadRowId IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM @declared d WHERE d.RowId = p.RoadRowId)),
               -- DeclaredRoadRow.IsBlank = PropertyMapper.IsBlankRoad
               (SELECT COUNT(*) FROM @declared d
                 WHERE d.Active = 1
                   AND NOT ((NULLIF(LTRIM(RTRIM(d.RoadId)), N'') IS NULL OR LTRIM(RTRIM(d.RoadId)) = N'0')
                            AND NULLIF(LTRIM(RTRIM(d.RoadName)), N'') IS NULL
                            AND NULLIF(LTRIM(RTRIM(d.EnteredRoadName)), N'') IS NULL
                            AND NULLIF(LTRIM(RTRIM(d.PrivateRoadName)), N'') IS NULL
                            AND NULLIF(LTRIM(RTRIM(d.PrivateRoadText)), N'') IS NULL)
                   AND NOT EXISTS (SELECT 1 FROM @p p WHERE p.seq = @seq AND p.RoadRowId = d.RowId)),
               @rowsBefore - (SELECT COUNT(*) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id = @app AND isActive = 1)
          FROM dbo.BtoAMainApp a
          OUTER APPLY (SELECT TOP 1 * FROM dbo.BtoA_MainApp_Officer WHERE Ofcr_ApplicationDisplayId = @disp ORDER BY Ofcr_RowId) o
         WHERE a.App_Id = @app;

        INSERT @snap
        SELECT r.seq, r.ord, o.Ofcr_App_Id, o.Ofcr_ApplicationDisplayId, CAST(o.Ofcr_Corrected_Road_Details AS int),
               o.Ofcr_Correction_Type_Id, o.Ofcr_Correction_Type_Value, o.Ofcr_RoadType, CAST(o.Ofcr_IsPresentInPublicRoadList AS int),
               o.Ofcr_RoadId, o.Ofcr_RoadName, o.Ofcr_ActualRoadName, o.Ofcr_SiteRoadRowID,
               o.Ofcr_Nearest_Private_Road_Latitude, o.Ofcr_Nearest_Private_Road_Longitude, o.Ofcr_Nearest_Private_Road_Document,
               o.Ofcr_Nearest_Public_Road_Latitude, o.Ofcr_Nearest_Public_Road_Longitude, o.Ofcr_Nearest_Public_Road_Document,
               o.Ofcr_Notice_Document, CAST(o.isActive AS int), o.CBy, o.UBy
          FROM @ret r JOIN dbo.BtoA_SiteRoadDetails_Officer o ON o.Ofcr_RowId = r.OfcrRowId
         WHERE r.seq = @seq;

        SET @seq = (SELECT MIN(seq) FROM @sub WHERE seq > @seq AND Refuse IS NULL);
    END

    ---------------------------------------------------------------------------
    -- Final-state checks that need the open transaction
    ---------------------------------------------------------------------------
    -- Write order: added roads (RoadRowId null -> 0) are written before declared ones.
    INSERT @res (sec, sc, seq, item, chk, expected, actual, note)
    SELECT 'ORDER', s.sc, s.seq, N'all roads', N'added rows written first (OrderBy RoadRowId ?? 0)', N'1',
           CASE WHEN MAX(CASE WHEN p.RoadRowId IS NULL THEN r.OfcrRowId END) < MIN(CASE WHEN p.RoadRowId IS NOT NULL THEN r.OfcrRowId END)
                THEN N'1' ELSE N'0' END, NULL
      FROM @sub s JOIN @p p ON p.seq = s.seq JOIN @ret r ON r.seq = p.seq AND r.ord = p.ord
     WHERE s.seq IN (5, 6, 13, 24)
     GROUP BY s.sc, s.seq;

    -- The officer's history line for app 8313 (HistoryService RoadCountSql / StreetNameSql)
    SET @officerId = 9900001;
    INSERT @hist (RoadCount, StreetName)
    SELECT RoadCount = (SELECT COUNT(*) FROM dbo.BtoA_SiteRoadDetails_Officer rd WITH (NOLOCK)
                          WHERE rd.Ofcr_ApplicationDisplayId = mao.Ofcr_ApplicationDisplayId
                            AND rd.CBy = @officerId AND rd.isActive = 1
                            AND ISNULL(rd.Ofcr_Correction_Type_Id, 0) <> 2),
           StreetName = (SELECT TOP (1) COALESCE(NULLIF(LTRIM(RTRIM(rn.Ofcr_ActualRoadName)), N''),
                                                 NULLIF(LTRIM(RTRIM(rn.Ofcr_RoadName)), N''))
                           FROM dbo.BtoA_SiteRoadDetails_Officer rn WITH (NOLOCK)
                          WHERE rn.Ofcr_ApplicationDisplayId = mao.Ofcr_ApplicationDisplayId
                            AND rn.CBy = @officerId AND rn.isActive = 1
                            AND ISNULL(rn.Ofcr_Correction_Type_Id, 0) <> 2
                            AND COALESCE(NULLIF(LTRIM(RTRIM(rn.Ofcr_ActualRoadName)), N''),
                                         NULLIF(LTRIM(RTRIM(rn.Ofcr_RoadName)), N'')) IS NOT NULL
                          ORDER BY CASE WHEN TRY_CAST(rn.Ofcr_SiteRoadRowID AS int) IS NULL THEN 1 ELSE 0 END,
                                   TRY_CAST(rn.Ofcr_SiteRoadRowID AS int), rn.Ofcr_RowId)
      FROM dbo.BtoA_MainApp_Officer mao WITH (NOLOCK)
      JOIN dbo.BtoAMainApp a ON a.App_DisplayId = mao.Ofcr_ApplicationDisplayId
     WHERE a.App_Id = 8313;

    -- S17 on app 8313: four submits, two officers
    INSERT @res (sec, sc, seq, item, chk, expected, actual, note) VALUES
     ('FINAL','S17',NULL,N'app 8313',N'officer road rows in total', N'5',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id = 8313),
        N'2 (S8) + 1 new for the 999 change (S17b) + 2 for the second officer (S17e)'),
     ('FINAL','S17',NULL,N'app 8313',N'ACTIVE rows for citizen road 884', N'2',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id = 8313 AND Ofcr_SiteRoadRowID = N'884' AND isActive = 1),
        N'[D5] one per officer (was 3: the superseded 976 row stayed active)'),
     ('FINAL','S17',NULL,N'app 8313',N'ACTIVE rows for road 884 by officer 9900001', N'1',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id = 8313 AND Ofcr_SiteRoadRowID = N'884' AND isActive = 1 AND CBy = 9900001),
        N'[D5] the 999 row; the 976 row it replaced is inactive (was 2)'),
     ('FINAL','S17',NULL,N'app 8313',N'the superseded 884 / 976 row of officer 9900001', N'0 by 9900001',
        (SELECT TOP 1 CONCAT(CAST(isActive AS int), N' by ', UBy) FROM dbo.BtoA_SiteRoadDetails_Officer
          WHERE Ofcr_App_Id = 8313 AND Ofcr_SiteRoadRowID = N'884' AND Ofcr_RoadId = 976 AND CBy = 9900001), N'[D5]'),
     ('FINAL','S17',NULL,N'app 8313',N'private-road DocumentTran rows (Mdoc 4 / 444)', N'5',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_DocumentTran WHERE docTrn_App_Id = 8313 AND docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444'),
        N'884 x3, 885 x2 (rows are deactivated, never deleted)'),
     ('FINAL','S17',NULL,N'app 8313',N'ACTIVE private-road documents of officer 9900001', N'884',
        (SELECT STRING_AGG(DocumentId, N',') FROM dbo.BtoA_DocumentTran WHERE docTrn_App_Id = 8313 AND docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444' AND docTrn_Active = 1 AND docTrn_CBy = 9900001),
        N'[D5][D22] only the newest for the one road still private (was 3 active)'),
     ('FINAL','S17',NULL,N'app 8313',N'ACTIVE document for 884 of officer 9900001 is the S17b capture', N'1',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_DocumentTran d JOIN @ret r ON r.seq = 10 AND r.ord = 1 AND d.docTrn_url = r.PrivUrl
          WHERE d.docTrn_App_Id = 8313 AND d.docTrn_Mdoc_id = 4 AND d.UniqueIdentifier = N'444' AND d.docTrn_Active = 1 AND d.DocumentId = N'884' AND d.docTrn_CBy = 9900001), NULL),
     ('FINAL','S17',NULL,N'app 8313',N'ACTIVE DocumentTran rows for 885 after it became public', N'1',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_DocumentTran WHERE docTrn_App_Id = 8313 AND docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444' AND DocumentId = N'885' AND docTrn_Active = 1),
        N'[D22] only the second officer''s, for whom it is still private (was 2)'),
     ('FINAL','S17',NULL,N'app 8313',N'BtoA_MainApp_Officer rows (keyed by display id only)', N'1',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_MainApp_Officer o JOIN dbo.BtoAMainApp a ON a.App_DisplayId = o.Ofcr_ApplicationDisplayId WHERE a.App_Id = 8313),
        N'[D35] decision: the second officer overwrites the first officer''s application row'),
     ('FINAL','S17',NULL,N'app 8313',N'active BtoA_StatusDetail_Officer rows', N'2',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_StatusDetail_Officer WHERE Ofcr_App_Id = 8313 AND Status_Active = 1),
        N'[D35] decision: one per officer; the status proc deactivates only rows with the same CBy/CRole'),
     ('FINAL','S17',NULL,N'app 8313',N'history RoadCount of officer 9900001', N'2',
        (SELECT CAST(MAX(RoadCount) AS nvarchar(10)) FROM @hist),
        N'[D23] the 999 row and 885; not the superseded row, nor the second officer''s (was 5)'),
     ('FINAL','S17',NULL,N'app 8313',N'history StreetName of officer 9900001', N'Test Lane X',
        (SELECT MAX(StreetName) FROM @hist), N'[D23] lowest row id first, numerically, of his current roads'),
     ('FINAL','S17c',NULL,N'app 16310',N'ACTIVE added (type 3, SiteRoadRowID NULL) rows', N'1',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id = 16310 AND Ofcr_SiteRoadRowID IS NULL AND Ofcr_Correction_Type_Id = 3 AND isActive = 1),
        N'[D5] was 2'),
     ('FINAL','S17c',NULL,N'app 16310',N'officer road rows in total', N'3',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id = 16310), N'rows are deactivated, never deleted'),
     ('FINAL','S17c',NULL,N'app 16310',N'ACTIVE private-road documents (added road: DocumentId NULL)', N'1',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_DocumentTran WHERE docTrn_App_Id = 16310 AND docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444' AND docTrn_Active = 1),
        N'[D5] the S17c one; the S7 one is deactivated (was 2)'),
     ('FINAL','S17f',NULL,N'app 960',N'ACTIVE private-road documents for 26491 with the S17f capture', N'1',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_DocumentTran d JOIN @ret r ON r.seq = 26 AND r.ord = 1 AND d.docTrn_url = r.PrivUrl
          WHERE d.docTrn_App_Id = 960 AND d.docTrn_Mdoc_id = 4 AND d.UniqueIdentifier = N'444' AND d.docTrn_Active = 1 AND d.DocumentId = N'26491' AND d.docTrn_CBy = 9900001),
        N'[D22] public -> private on the update branch: EnsureDocumentSql wrote it (was 0)'),
     ('FINAL','S10c',NULL,N'app 53424',N'added rows with RoadId 999 and no SiteRoadRowID', N'2',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id = 53424 AND Ofcr_RoadId = 999 AND Ofcr_SiteRoadRowID IS NULL), NULL),
     ('FINAL','S11',NULL,N'app 19252',N'officer rows referencing the declared row 37110', N'0',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id = 19252 AND Ofcr_SiteRoadRowID = N'37110'),
        N'[N3] reported as a DECLARED_ROAD_NOT_ANSWERED warning, see APP'),
     ('FINAL','S12old',NULL,N'app 30813',N'inactive (deleted) rows for citizen rows already Rd_RoadActive = 0', N'3',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_SiteRoadDetails_Officer o JOIN dbo.BtoA_SiteRoadDetails r ON CAST(r.Rd_RoadRow_ID AS nvarchar(20)) = o.Ofcr_SiteRoadRowID
          WHERE o.Ofcr_App_Id = 30813 AND r.Rd_RoadActive = 0 AND o.isActive = 0),
        N'[D14] an old-fetch payload naming inactive rows is still accepted; the API fetch no longer sends them'),
     ('FINAL','S12old',NULL,N'app 30813',N'ACTIVE private-road documents of roads marked not found', N'0',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_DocumentTran WHERE docTrn_App_Id = 30813 AND docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444' AND docTrn_Active = 1 AND DocumentId IN (N'7126', N'7474')),
        N'[D5] the proc adds a NULL-url document for a deleted private road; SupersedeDocumentsSql deactivates it (was 2)'),
     ('FINAL','S12ws',NULL,N'app 29452',N'officer rows for inactive citizen rows', N'0',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.BtoA_SiteRoadDetails_Officer o JOIN dbo.BtoA_SiteRoadDetails r ON CAST(r.Rd_RoadRow_ID AS nvarchar(20)) = o.Ofcr_SiteRoadRowID
          WHERE o.Ofcr_App_Id = 29452 AND r.Rd_RoadActive = 0), NULL),
     -- The KSRSAC join the proc runs, evaluated for the pair the citizen row really holds (S18a)
     ('FINAL','S18a',NULL,N'app 251 row 26188',N'KSRSAC match for (Rd_PrivateRoadId, Rd_PrivateRoadText)', N'1',
        (SELECT CASE WHEN EXISTS (
            SELECT 1 FROM dbo.BtoA_EPIDMetaData M
              JOIN masterDB_prod.dbo.mst_AROMapping A ON A.GBAZoneID = M.MD_ZoneId AND A.BBMPWardId = M.MD_WardId AND A.BBMPStreetId = M.MD_StreetId
              JOIN masterDB_prod.dbo.MstRoadKSRAC R ON R.BBMPZoneID = A.BBMPZoneID AND R.BBMPWardId = A.BBMPWardId
              JOIN dbo.BtoA_SiteRoadDetails S ON S.Rd_RoadRow_ID = 26188
             WHERE M.MD_APP_ID = 251 AND R.Road_ID = TRY_CAST(S.Rd_PrivateRoadId AS int) AND R.Road_Name = S.Rd_PrivateRoadText)
          THEN N'1' ELSE N'0' END),
        N'[D32] the row does hold a verifiable nearest public road; the app''s D32 mapping sends it'),
     ('FINAL','S14',NULL,N'MstRoadKSRAC',N'Road_ID 999 rows outside BBMP zone 5 / ward 184', N'0',
        (SELECT CAST(COUNT(*) AS nvarchar(10)) FROM masterDB_prod.dbo.MstRoadKSRAC WHERE Road_ID = 999 AND NOT (BBMPZoneID = 5 AND BBMPWardId = 184)),
        N'999 is never verified outside ward 184, where a matching name would falsely verify'),
     ('FINAL','D24',NULL,N'USP_IU_BtoA_SiteRoadDetails_Officer',N'@BtoA_RoadName / @BtoA_ActualRoadName length (chars)', N'250/250',
        (SELECT CONCAT(MAX(CASE WHEN name = N'@BtoA_RoadName' THEN max_length / 2 END), N'/', MAX(CASE WHEN name = N'@BtoA_ActualRoadName' THEN max_length / 2 END))
           FROM sys.parameters WHERE object_id = OBJECT_ID(N'dbo.USP_IU_BtoA_SiteRoadDetails_Officer')),
        N'[D24] the API now refuses longer names (TOO_LONG) and declares size 250'),
     ('FINAL','trigger',NULL,N'trg_BtoAMainApp_UpdateNewKhata',N'db/23 guard present / ekhata_test absent', N'1/1',
        CONCAT(CASE WHEN OBJECT_DEFINITION(OBJECT_ID(N'dbo.trg_BtoAMainApp_UpdateNewKhata')) LIKE N'%DB_ID(N''ekhata_test'') IS NULL%' THEN 1 ELSE 0 END,
               N'/', CASE WHEN DB_ID(N'ekhata_test') IS NULL THEN 1 ELSE 0 END), NULL);

    INSERT @inTx (k, v)
    SELECT N'test apps at App_Status 13 inside the transaction',
           CONCAT((SELECT COUNT(*) FROM dbo.BtoAMainApp WHERE App_Id IN (SELECT AppId FROM @sub WHERE Refuse IS NULL) AND App_Status = 13), N' of ',
                  (SELECT COUNT(DISTINCT AppId) FROM @sub WHERE Refuse IS NULL));
END TRY
BEGIN CATCH
    SET @err = CONCAT(N'seq ', @seq, N' (', ERROR_PROCEDURE(), N' line ', ERROR_LINE(), N'): ', ERROR_MESSAGE());
    INSERT @res (sec, sc, seq, item, chk, expected, actual, note)
    VALUES ('ERROR', (SELECT sc FROM @sub WHERE seq = @seq), @seq, N'script', N'no SQL error', N'none', @err, NULL);
END CATCH

IF @@TRANCOUNT > 0 ROLLBACK TRAN;

-------------------------------------------------------------------------------
-- 2e. CHECKS from the snapshots (table variables survive the rollback)
-------------------------------------------------------------------------------
-- Application row and status, per submit
INSERT @res (sec, sc, seq, item, chk, expected, actual, note)
SELECT 'APP', s.sc, s.seq, CONCAT(N'app ', s.AppId), v.chk, v.e, v.a, v.n
  FROM @sub s JOIN @main m ON m.seq = s.seq
 CROSS APPLY (VALUES
    (N'sp_getapplock granted (0/1)', N'1', CASE WHEN m.LockRc >= 0 THEN N'1' ELSE N'0' END, NULL),
    (N'App_Id lookup', CAST(s.AppId AS nvarchar(20)), CAST(m.LookupAppId AS nvarchar(20)), NULL),
    (N'road row ids not of this application', N'0', CAST(m.Unknown AS nvarchar(10)), N'[N3] else UNKNOWN_ROAD_ROW'),
    (N'DECLARED_ROAD_NOT_ANSWERED warnings', CAST(s.eWarn AS nvarchar(10)), CAST(m.Warn AS nvarchar(10)), N'[N3]'),
    (N'MainApp proc Status', N'1', CAST(CAST(m.AppSt AS int) AS nvarchar(5)), m.AppMsg),
    (N'BtoA_MainApp_Officer rows for display id', N'1', CAST(m.MainRows AS nvarchar(10)), NULL),
    (N'BtoA_MainApp_Officer.Ofcr_App_Id', CAST(s.AppId AS nvarchar(20)), CAST(m.Ofcr_App_Id AS nvarchar(20)),
        CASE WHEN s.Mutate = 'appid0' THEN N'[D33] row written with 0 before the fix: healed, then updated'
             ELSE N'[D33] the API passes the App_Id (was 0)' END),
    (N'officer row held under the App_Id (HeldSql)', N'1', CAST(m.Held AS nvarchar(10)), N'[D33] 0 -> SUBMIT_REJECTED'),
    (N'Ofcr_CorrectedRoadDetails',
        CASE WHEN EXISTS (SELECT 1 FROM @p p WHERE p.seq = s.seq AND p.RoadStatus <> 0) THEN N'1' ELSE N'0' END,
        CAST(m.CorrRoad AS nvarchar(5)),
        CASE WHEN EXISTS (SELECT 1 FROM @p p WHERE p.seq = s.seq AND p.RoadStatus <> 0) THEN N'[D9] a road was changed (was 0)' END),
    (N'Ofcr_IsCornerPlot...', CAST(s.Corner AS nvarchar(5)), CAST(m.Corner AS nvarchar(5)), NULL),
    (N'Ofcr_RoadFacingSides', CAST(s.Sides AS nvarchar(5)), CAST(m.Sides AS nvarchar(5)), NULL),
    (N'nearest_public_Road lat,lng',
        CASE WHEN EXISTS (SELECT 1 FROM @p p WHERE p.seq = s.seq AND (p.Caps = 1 OR p.PubOnly = 1)) THEN N'12.9105,77.5609' END,
        CASE WHEN m.NearLat IS NOT NULL OR m.NearLng IS NOT NULL THEN CONCAT(m.NearLat, N',', m.NearLng) END,
        CASE WHEN s.seq = 2 THEN N'[D13] promoted from a not-found road''s left-over capture' END),
    (N'Ofcr_Aditional isDeclaredRoadFacingSidesCorrect', CAST(s.DeclCorrect AS nvarchar(5)),
        CAST(JSON_VALUE(m.Aditional, '$.isDeclaredRoadFacingSidesCorrect') AS nvarchar(5)), NULL),
    (N'application row writer (UBy on resubmit, else CBy)',
        CAST(s.Officer AS nvarchar(20)),
        CAST(CASE WHEN EXISTS (SELECT 1 FROM @sub s0 WHERE s0.AppId = s.AppId AND s0.seq < s.seq AND s0.Refuse IS NULL) THEN m.MainUBy ELSE m.MainCBy END AS nvarchar(20)), NULL),
    (N'App_Status after the status proc (e-Khata trigger path)', N'13', CAST(m.AppStatus AS nvarchar(10)), NULL),
    (N'App_AdditionalInfo', N'status details Received Over the GPS API', m.AppAddl, NULL),
    (N'trg_BtoAMainApp history rows written', N'1', CASE WHEN m.MainHistDelta >= 1 THEN N'1' ELSE N'0' END,
        CONCAT(N'delta ', m.MainHistDelta)),
    (N'active status rows for this officer', N'1', CAST(m.ActiveStatusRowsOfficer AS nvarchar(10)), NULL),
    (N'assignment rows completed (test officer holds none)', N'0', CAST(m.AssignRows AS nvarchar(10)), NULL),
    (N'earlier road rows superseded by this submit',
        CAST(CASE s.seq WHEN 7 THEN 1 WHEN 10 THEN 1 ELSE 0 END AS nvarchar(10)), CAST(m.Superseded AS nvarchar(10)),
        CASE WHEN s.seq IN (7, 10) THEN N'[D5] the earlier added row (S17c) / the 884 row whose road id changed (S17b)' END)
 ) v(chk, e, a, n);

-- Each road write
INSERT @res (sec, sc, seq, item, chk, expected, actual, note)
SELECT 'ROAD', s.sc, s.seq,
       CASE WHEN p.RoadRowId IS NULL THEN CONCAT(N'added #', p.ord) ELSE CONCAT(N'road ', p.RoadRowId) END,
       v.chk,
       REPLACE(v.e, ISNULL(e.MD_MotherEPID, N'~'), N'{epid}'),
       REPLACE(v.a, ISNULL(e.MD_MotherEPID, N'~'), N'{epid}'),
       v.n
  FROM @p p
  JOIN @sub s ON s.seq = p.seq
  LEFT JOIN @ret r ON r.seq = p.seq AND r.ord = p.ord
  LEFT JOIN @snap o ON o.seq = p.seq AND o.ord = p.ord
  OUTER APPLY (SELECT TOP 1 MD_MotherEPID FROM dbo.BtoA_EPIDMetaData WHERE MD_APP_ID = s.AppId) e
 CROSS APPLY (VALUES
    (N'proc Status (stored)', N'1', CAST(CAST(r.St AS int) AS nvarchar(5)), NULL),
    (N'write path', p.eMode, CASE WHEN r.Msg LIKE N'Record inserted%' THEN N'insert' WHEN r.Msg LIKE N'Record updated%' THEN N'update' ELSE r.Msg END, NULL),
    (N'returned RoadRowId is the officer row', N'1', CASE WHEN r.OfcrRowId > 0 AND o.seq IS NOT NULL THEN N'1' ELSE N'0' END, NULL),
    (N'Ofcr_App_Id', CAST(s.AppId AS nvarchar(20)), CAST(o.Ofcr_App_Id AS nvarchar(20)), NULL),
    (N'Correction_Type_Id', CAST(p.RoadStatus AS nvarchar(5)), CAST(o.CtId AS nvarchar(5)), NULL),
    (N'Correction_Type_Value',
        CASE p.RoadStatus WHEN 0 THEN N'unchanged' WHEN 1 THEN N'updated' WHEN 2 THEN N'delete' WHEN 3 THEN N'added' END, o.CtVal,
        N'[D10] decision: legacy rows use No Change / Update / Delete / Add'),
    (N'isActive', CASE WHEN p.RoadStatus = 2 THEN N'0' ELSE N'1' END, CAST(o.Act AS nvarchar(5)),
        CASE WHEN p.RoadStatus = 2 THEN N'[D10] decision: legacy Delete rows are isActive 1' END),
    (N'RoadType', p.RoadType, o.RT, NULL),
    (N'IsPresentInPublicRoadList', CAST(p.IsPresent AS nvarchar(5)), CAST(o.PUB AS nvarchar(5)), NULL),
    (N'RoadId', CAST(TRY_CAST(p.RoadId AS int) AS nvarchar(20)), CAST(o.RID AS nvarchar(20)), NULL),
    (N'RoadName', p.RoadName, o.RN, NULL),
    (N'ActualRoadName',   -- long names shown as their length; a truncation still differs
        CASE WHEN LEN(p.ActualRoadName) > 80 THEN CONCAT(N'len ', LEN(p.ActualRoadName), N': ', LEFT(p.ActualRoadName, 15), N'...') ELSE p.ActualRoadName END,
        CASE WHEN LEN(o.ARN) > 80 THEN CONCAT(N'len ', LEN(o.ARN), N': ', LEFT(o.ARN, 15), N'...') ELSE o.ARN END, NULL),
    (N'SiteRoadRowID', CAST(p.RoadRowId AS nvarchar(20)), o.SRID, NULL),
    (N'Corrected_Road_Details',
        CASE WHEN p.IsCorrect = 0 THEN N'1' WHEN p.IsCorrect = 1 THEN N'0' WHEN p.RoadStatus <> 0 THEN N'1' ELSE N'0' END,
        CAST(o.CorrRoad AS nvarchar(5)),
        CASE WHEN p.IsCorrect IS NULL AND p.RoadStatus <> 0 THEN N'[D9] no answer on a changed road: corrected (was 0)' END),
    (N'KSRSAC matched', CAST(CAST(p.eKsr AS int) AS nvarchar(5)), CAST(CAST(r.Ksr AS int) AS nvarchar(5)), p.Note),
    (N'private capture lat,lng', CASE WHEN p.Caps = 1 THEN N'12.9101,77.5601' END,
        CASE WHEN o.PrivLat IS NOT NULL OR o.PrivLng IS NOT NULL THEN CONCAT(CONVERT(nvarchar(30), o.PrivLat), N',', CONVERT(nvarchar(30), o.PrivLng)) END, NULL),
    (N'private capture document', r.PrivUrl, o.PrivDoc, NULL),
    (N'public capture lat,lng', CASE WHEN p.Caps = 1 OR p.PubOnly = 1 THEN N'12.9105,77.5609' END,
        CASE WHEN o.PubLat IS NOT NULL OR o.PubLng IS NOT NULL THEN CONCAT(CONVERT(nvarchar(30), o.PubLat), N',', CONVERT(nvarchar(30), o.PubLng)) END, NULL),
    (N'public capture document', r.PubUrl, o.PubDoc, NULL),
    (N'notice document',
        CASE WHEN p.KeptNoticeSeq IS NOT NULL THEN (SELECT k.NoticeUrl FROM @ret k WHERE k.seq = p.KeptNoticeSeq AND k.ord = p.ord) ELSE r.NoticeUrl END,
        o.Notice, CASE WHEN p.seq = 10 AND p.ord = 2 THEN N'[D22] the old notice is cleared (was kept by COALESCE)' END),
    (N'notice proc rows', CASE WHEN p.Notice IS NOT NULL THEN N'1' END, CAST(r.NoticeRows AS nvarchar(5)), NULL),
    (N'DocumentTran rows added by the proc',
        CASE WHEN p.eMode = 'insert' AND p.RoadType = N'Private' THEN N'1' ELSE N'0' END,   -- CI: 'private' = 'Private'
        CAST(r.DocDelta AS nvarchar(5)),
        CASE WHEN p.RoadType = N'Private' AND p.Caps = 0 THEN N'deleted private road: a NULL-url document, deactivated by SupersedeDocumentsSql' END)
 ) v(chk, e, a, n)
 WHERE s.Refuse IS NULL;

-- The DocumentTran row a private-road insert wrote, and the one a private-road update rewrote
INSERT @res (sec, sc, seq, item, chk, expected, actual, note)
SELECT 'DOC', s.sc, s.seq,
       CASE WHEN p.RoadRowId IS NULL THEN CONCAT(N'added #', p.ord) ELSE CONCAT(N'road ', p.RoadRowId) END,
       v.chk, REPLACE(v.e, ISNULL(e.MD_MotherEPID, N'~'), N'{epid}'), REPLACE(v.a, ISNULL(e.MD_MotherEPID, N'~'), N'{epid}'), v.n
  FROM @p p JOIN @sub s ON s.seq = p.seq JOIN @ret r ON r.seq = p.seq AND r.ord = p.ord
  OUTER APPLY (SELECT TOP 1 MD_MotherEPID FROM dbo.BtoA_EPIDMetaData WHERE MD_APP_ID = s.AppId) e
 CROSS APPLY (VALUES
    (N'new DocumentTran.DocumentId = SiteRoadRowID', CAST(p.RoadRowId AS nvarchar(20)), r.NewDocDocumentId,
        CASE WHEN p.RoadRowId IS NULL THEN N'added private road: DocumentId NULL' END),
    (N'new DocumentTran.docTrn_url = private capture', r.PrivUrl, r.NewDocUrl, NULL),
    (N'new DocumentTran.docTrn_Active (at the write)', N'1', CAST(r.NewDocActive AS nvarchar(5)), NULL)
 ) v(chk, e, a, n)
 WHERE p.eMode = 'insert' AND p.RoadType = N'Private' AND s.Refuse IS NULL
UNION ALL
SELECT 'DOC', s.sc, s.seq, CONCAT(N'road ', p.RoadRowId), N'update path rewrote DocumentTran url for this road', N'1',
       CAST(CASE WHEN r.DocUrlRowsForSrid >= 1 THEN 1 ELSE 0 END AS nvarchar(5)), NULL
  FROM @p p JOIN @sub s ON s.seq = p.seq JOIN @ret r ON r.seq = p.seq AND r.ord = p.ord
 WHERE p.eMode = 'update' AND p.RoadType = N'Private' AND p.RoadRowId IS NOT NULL AND s.Refuse IS NULL
   AND EXISTS (SELECT 1 FROM @p p0 WHERE p0.seq < p.seq AND p0.RoadRowId = p.RoadRowId AND p0.RoadType = N'Private');

-------------------------------------------------------------------------------
-- 2f. PERSIST: nothing may survive the rollback
-------------------------------------------------------------------------------
INSERT @after EXEC sys.sp_executesql @snapSql;

INSERT @res (sec, sc, seq, item, chk, expected, actual, note)
SELECT 'PERSIST', NULL, NULL, N'after ROLLBACK', b.k, CAST(b.v AS nvarchar(40)), CAST(a.v AS nvarchar(40)), NULL
  FROM @before b LEFT JOIN @after a ON a.k = b.k;

INSERT @res (sec, sc, seq, item, chk, expected, actual, note)
VALUES ('PERSIST', NULL, NULL, N'after ROLLBACK', N'open transactions', N'0', CAST(@@TRANCOUNT AS nvarchar(5)), NULL);

-------------------------------------------------------------------------------
-- 2g. REPORT
-------------------------------------------------------------------------------
SELECT k AS [inside the transaction], v FROM @inTx;

SELECT sec, sc, seq, item, chk, expected, actual, result, note FROM @res ORDER BY id;

SELECT result, COUNT(*) AS checks FROM @res GROUP BY result;

SELECT sc, MIN(seq) AS seq, SUM(CASE WHEN result = 'PASS' THEN 1 ELSE 0 END) AS pass,
       SUM(CASE WHEN result = 'FAIL' THEN 1 ELSE 0 END) AS fail
  FROM @res WHERE sc IS NOT NULL GROUP BY sc ORDER BY MIN(ISNULL(seq, 999)), sc;
GO
