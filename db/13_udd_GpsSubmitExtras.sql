/*
    Two small procedures owned entirely by PropertyGpsApi, writing the columns added in
    12_v1_SubmitSchema_Additions.sql.

    APPLIES TO UDD_KHATABTOA_TEST ONLY.

    Why not extend USP_IU_BtoA_MainApp_Officer and USP_IU_BtoA_SiteRoadDetails_Officer
    with more optional parameters: those two are called by the JC portal and the QC
    workflow as well as by this API. Every change to them is a change to somebody else's
    write path. These additions are ours alone, so they live in procedures nobody else
    calls and can be altered without a blast radius.

    Both are UPDATE-only and idempotent. They run after the legacy procedures inside the
    same transaction, so the rows they target already exist.
*/

USE UDD_KHATABTOA_TEST;
GO

IF DB_NAME() <> 'UDD_KHATABTOA_TEST'
    THROW 50001, 'Refusing to run: this script is for UDD_KHATABTOA_TEST only.', 1;
GO

-------------------------------------------------------------------------------
-- Application level: the three captures, and the government-property answer
-------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE [dbo].[USP_U_BtoA_GpsSubmitExtras_App]
    @Ofcr_ApplicationDisplayId nvarchar(100),
    @PropertyDocument          nvarchar(max) = NULL,
    @MapDocument               nvarchar(max) = NULL,
    @NoteSheetDocument         nvarchar(max) = NULL,
    @GovtPropertyDetails       nvarchar(1000) = NULL,
    @IsGovtPropertyValue       int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- COALESCE, not assignment: a resubmission that re-sends only some photographs must
    -- not blank the ones it left out. Evidence is only ever added, never cleared here.
    UPDATE dbo.BtoA_MainApp_Officer
       SET Ofcr_Property_Document    = COALESCE(@PropertyDocument,    Ofcr_Property_Document),
           Ofcr_Map_Document         = COALESCE(@MapDocument,         Ofcr_Map_Document),
           Ofcr_NoteSheet_Document   = COALESCE(@NoteSheetDocument,   Ofcr_NoteSheet_Document),
           Ofcr_GovtPropertyDetails  = COALESCE(@GovtPropertyDetails, Ofcr_GovtPropertyDetails),
           Ofcr_IsGovtProperty_Value = COALESCE(@IsGovtPropertyValue, Ofcr_IsGovtProperty_Value)
     WHERE Ofcr_ApplicationDisplayId = @Ofcr_ApplicationDisplayId;

    SELECT RowsAffected = @@ROWCOUNT;
END
GO

-------------------------------------------------------------------------------
-- Per road: the served-notice photograph
--
-- Keyed on Ofcr_RowId, which USP_IU_BtoA_SiteRoadDetails_Officer returns as RoadRowId,
-- so this always targets the row that call just wrote.
-------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE [dbo].[USP_U_BtoA_GpsRoadNoticeDocument]
    @RoadRowId       int,
    @NoticeDocument  nvarchar(max) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE dbo.BtoA_SiteRoadDetails_Officer
       SET Ofcr_Notice_Document = COALESCE(@NoticeDocument, Ofcr_Notice_Document)
     WHERE Ofcr_RowId = @RoadRowId;

    SELECT RowsAffected = @@ROWCOUNT;
END
GO
