/*
    What a Revenue Inspector's submission writes, and how to check each of it.

    READ ONLY. Every statement here is a SELECT - safe to run against any environment,
    including live. Set @DisplayId to the application you want to inspect and run the whole
    file; each section prints one result set.

    Submitting one survey calls three stored procedures, in this order, and they touch six
    tables between them:

        USP_IU_BtoA_MainApp_Officer          -> BtoA_MainApp_Officer          (the answers)
        USP_IU_BtoA_SiteRoadDetails_Officer  -> BtoA_SiteRoadDetails_Officer  (the roads)
                                             -> BtoA_DocumentTran             (the uploads)
        USP_IU_BtoA_StatusDetail_Officer     -> BtoA_StatusDetail_Officer     (the status row)
                                             -> BtoA_Status_Master_Tran       (the audit trail)
                                             -> BtoAMainApp.App_Status        (10 -> 13)

    App_Status 13 is "Data Received from RI" (masterDB_prod.dbo.Mst_AppStatus). The value is
    chosen by role inside USP_IU_BtoA_StatusDetail_Officer: role 116 - the Revenue Inspector,
    which is the role this GPS app signs in as - maps to 13.

    Section 7 is the one that will surprise you. See its note.
*/

DECLARE @DisplayId nvarchar(50) = '202608250103674';   -- <-- the application to inspect

DECLARE @AppId int =
    (SELECT App_Id FROM BtoAMainApp WHERE App_DisplayId = @DisplayId AND App_Active = 1);

IF @AppId IS NULL
BEGIN
    SELECT Problem = 'No active application with that App_DisplayId in ' + DB_NAME();
    RETURN;
END

-------------------------------------------------------------------------------
-- 1. BtoAMainApp - the application's own status
--    This is the row that decides whether the application has moved on. 10 is
--    pending with the RI, 13 is submitted.
-------------------------------------------------------------------------------
SELECT Section = '1. BtoAMainApp (App_Status)',
       App_Id, App_DisplayId, App_Status, App_MotherEPID,
       IsPushedToGps, PushedToGpsDate, PushedToGpsRemark
FROM BtoAMainApp
WHERE App_Id = @AppId;

-------------------------------------------------------------------------------
-- 2. BtoA_MainApp_Officer - the survey itself
--    One row per submission. Everything the officer answered on site: the
--    corrected coordinates, the use type, the areas, the document URLs.
--
--    MATCHED ON THE DISPLAY ID, NOT App_Id, AND THAT IS DELIBERATE.
--    USP_IU_BtoA_MainApp_Officer leaves Ofcr_App_Id = 0 on the rows it writes, so
--    "WHERE Ofcr_App_Id = @AppId" returns nothing for a survey that plainly
--    exists. Section 2b counts how widespread that is. Until the procedure is
--    fixed, every query against this table has to go through the display id -
--    which also means these rows join to nothing by key.
-------------------------------------------------------------------------------
SELECT Section = '2. BtoA_MainApp_Officer (the answers)',
       Ofcr_RowId, Ofcr_App_Id, Ofcr_ApplicationDisplayId,
       Ofcr_PropertyLandExistOnSpot, Ofcr_IsGovtProperty,
       Ofcr_CorrectedGpsLocation, Ofcr_CorrectedLatitude, Ofcr_CorrectedLongitude,
       Ofcr_CorrectedPropertyUseTypeValue,
       Ofcr_CorrectedResidentialArea, Ofcr_CorrectedCommercialArea,
       Ofcr_Property_Document, Ofcr_Map_Document, Ofcr_NoteSheet_Document,
       isActive, CBy, CDte, CRole
FROM BtoA_MainApp_Officer
WHERE Ofcr_ApplicationDisplayId = @DisplayId
ORDER BY CDte DESC;

-------------------------------------------------------------------------------
-- 2b. How many survey rows have lost their application id, and do any document
--     URLs point somewhere unreachable?
--
--     A URL containing localhost was written while the API ran on a developer
--     machine. Media:PublicBaseUrl is concatenated in at the moment of
--     submission and stored permanently, so changing configuration afterwards
--     does not repair these rows - the officer's photograph is simply
--     unreachable from any device.
-------------------------------------------------------------------------------
SELECT Section          = '2b. Data health in BtoA_MainApp_Officer',
       TotalRows        = COUNT(*),
       MissingAppId     = SUM(CASE WHEN ISNULL(Ofcr_App_Id, 0) = 0 THEN 1 ELSE 0 END),
       UnreachableDocUrl = SUM(CASE WHEN Ofcr_Property_Document LIKE '%localhost%'
                                      OR Ofcr_Map_Document      LIKE '%localhost%'
                                      OR Ofcr_NoteSheet_Document LIKE '%localhost%'
                                    THEN 1 ELSE 0 END)
FROM BtoA_MainApp_Officer;

-------------------------------------------------------------------------------
-- 3. BtoA_SiteRoadDetails_Officer - the roads
--    One row per road the officer recorded. Ofcr_IsPresentInPublicRoadList = 1
--    means the road matched the KSRSAC master for that ward; 0 means it was
--    stored anyway and reported back to the app as unverified.
-------------------------------------------------------------------------------
SELECT Section = '3. BtoA_SiteRoadDetails_Officer (the roads)',
       Ofcr_SiteRoadRowID, Ofcr_App_Id, Ofcr_RoadId, Ofcr_RoadName, Ofcr_ActualRoadName,
       Ofcr_RoadType, Ofcr_IsPresentInPublicRoadList, Ofcr_Corrected_Road_Details,
       Ofcr_RMPRoadWideningInMtrs,
       Ofcr_Nearest_Public_Road_Latitude, Ofcr_Nearest_Public_Road_Longitude,
       CBy, CDte, CRole
FROM BtoA_SiteRoadDetails_Officer
WHERE Ofcr_App_Id = @AppId
ORDER BY CDte DESC;

-------------------------------------------------------------------------------
-- 4. BtoA_DocumentTran - road documents only
--    Written by USP_IU_BtoA_SiteRoadDetails_Officer, and ONLY when the officer
--    supplied a nearest-public-road or nearest-private-road photograph. A survey
--    without one adds nothing here.
--
--    The officer's property and map photographs do NOT come here. They are stored
--    as URLs in Ofcr_Property_Document / Ofcr_Map_Document / Ofcr_NoteSheet_Document
--    on BtoA_MainApp_Officer - section 2. So older rows in this table usually
--    belong to the citizen's original application, not to the RI's survey; check
--    docTrn_CDte and docTrn_CRole before concluding a submission wrote them.
-------------------------------------------------------------------------------
SELECT Section = '4. BtoA_DocumentTran (the uploads)',
       docTrn_Id, docTrn_App_Id, docTrn_Mdoc_id, docTrn_MSubdoc_id,
       docTrn_url, docTrn_Active, docTrn_Remarks,
       docTrn_CBy, docTrn_CDte, docTrn_CRole
FROM BtoA_DocumentTran
WHERE docTrn_App_Id = @AppId
ORDER BY docTrn_CDte DESC;

-------------------------------------------------------------------------------
-- 5. BtoA_StatusDetail_Officer - who submitted it and when
--    CBy is the officer id and CRole the role (116 = Revenue Inspector). This is
--    the attribution trail: if CBy is 0 or null, the submission was recorded
--    without an owner.
-------------------------------------------------------------------------------
SELECT Section = '5. BtoA_StatusDetail_Officer (attribution)',
       Ofcr_App_Id, Ofcr_ApplicationDisplayId, Status_Id, Status_Date,
       Status_Remark, Status_Rejected_Reason, Status_Value, Status_Active,
       CBy, CDte, CRole
FROM BtoA_StatusDetail_Officer
WHERE Ofcr_App_Id = @AppId
ORDER BY CDte DESC;

-------------------------------------------------------------------------------
-- 6. BtoA_Status_Master_Tran - the transition itself
--    Sts_PrevStatusId -> Sts_CurrStatusId would be the movement, 10 -> 13 for a
--    first RI submission.
--
--    OBSERVED EMPTY for submissions made through this API. The procedure
--    references the table but evidently does not write to it on the role-116
--    path, so the cross-application audit trail has no record of the transition
--    even though App_Status did change. Section 5 is the attribution that does
--    get written. Worth raising with the DBA alongside the Ofcr_App_Id = 0 issue.
-------------------------------------------------------------------------------
SELECT Section = '6. BtoA_Status_Master_Tran (the transition)', *
FROM BtoA_Status_Master_Tran
WHERE Sts_MainAppId = @AppId;

-------------------------------------------------------------------------------
-- 7. BtoA_Architect_AssignedApp - the assignment, which is NOT closed
--
--    Submitting does not touch this table. USP_IU_BtoA_StatusDetail_Officer never
--    references it and never calls USP_U_Status_Master_Tran, which is the legacy
--    procedure that would. So the row stays Status='Pending', IsActive=1 forever,
--    even though the survey is finished and the application has moved to 13.
--
--    That matters because USP_IU_Architect_AssignedApp refuses a third pending
--    assignment. An officer is therefore permanently blocked after two surveys,
--    and only a DBA can release them. Expect to see Status='Pending' below for an
--    application that is plainly complete.
-------------------------------------------------------------------------------
SELECT Section = '7. BtoA_Architect_AssignedApp (still open - see note)',
       a.Assign_Id, a.AppId, a.Arch_Id, a.Arch_Name, a.Arch_Role,
       a.Status, a.IsActive, a.AssignmentDate, a.CompletedDate, a.Remarks,
       StillHeld = CASE WHEN a.IsActive = 1 AND a.Status = 'Pending'
                        THEN 'YES - counts against this officer''s limit of 2'
                        ELSE 'no' END
FROM BtoA_Architect_AssignedApp a
WHERE a.AppId = @AppId;

-------------------------------------------------------------------------------
-- 8. One-line summary
-------------------------------------------------------------------------------
SELECT Section         = '8. Summary',
       App_DisplayId   = m.App_DisplayId,
       App_Status      = m.App_Status,
       SurveyRows      = (SELECT COUNT(*) FROM BtoA_MainApp_Officer         WHERE Ofcr_ApplicationDisplayId = @DisplayId),
       RoadRows        = (SELECT COUNT(*) FROM BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id  = @AppId),
       DocumentRows    = (SELECT COUNT(*) FROM BtoA_DocumentTran            WHERE docTrn_App_Id = @AppId),
       StatusRows      = (SELECT COUNT(*) FROM BtoA_StatusDetail_Officer    WHERE Ofcr_App_Id  = @AppId),
       TransitionRows  = (SELECT COUNT(*) FROM BtoA_Status_Master_Tran      WHERE Sts_MainAppId = @AppId),
       AssignmentsOpen = (SELECT COUNT(*) FROM BtoA_Architect_AssignedApp
                          WHERE AppId = @AppId AND IsActive = 1 AND Status = 'Pending')
FROM BtoAMainApp m
WHERE m.App_Id = @AppId;
