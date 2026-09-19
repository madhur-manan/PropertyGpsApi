/*
    Columns the Single Plot GPS app collects that currently have nowhere to be stored.

    APPLIES TO UDD_KHATABTOA_TEST ONLY.

    Every statement is additive, nullable and guarded, so the script is safe to re-run and
    changes no existing row, index or query plan. Nothing is dropped or retyped.

    Verified absent before writing this (2026-09-18):

      BtoA_SiteRoadDetails_Officer has Ofcr_Nearest_Private_Road_Document and
      Ofcr_Nearest_Public_Road_Document, but no column for the SERVED NOTICE photograph
      the officer captures per private road.

      BtoA_MainApp_Officer has 54 columns and none of them hold the property photograph,
      the map screenshot or the khata note sheet - the three application-level captures.
      It also has no home for govtPropertyDetails, the officer's written justification for
      asserting encroachment on government land, which is the highest-consequence free
      text in the survey.

      Ofcr_IsGovtProperty is bit NOT NULL. The app offers three answers - No, Yes, and
      "refer to surveyor" - and also allows the question to be left unanswered. A bit can
      represent neither the third value nor "not answered". Rather than retype a NOT NULL
      column that 40,449 rows and other callers already depend on, this adds a nullable
      int alongside it, mirroring the pattern BBMP already uses in
      BtoA_RejectedDetails_Officer (RI_IsGovtProperty int NULL +
      RI_IsGovtProperty_comments varchar(255) NULL). The bit keeps working for existing
      readers; the int carries the full answer.
*/

USE UDD_KHATABTOA_TEST;
GO

IF DB_NAME() <> 'UDD_KHATABTOA_TEST'
    THROW 50001, 'Refusing to run: this script is for UDD_KHATABTOA_TEST only.', 1;
GO

-------------------------------------------------------------------------------
-- Per-road: the served-notice photograph
-------------------------------------------------------------------------------
IF COL_LENGTH('dbo.BtoA_SiteRoadDetails_Officer', 'Ofcr_Notice_Document') IS NULL
    ALTER TABLE dbo.BtoA_SiteRoadDetails_Officer
        ADD Ofcr_Notice_Document nvarchar(max) NULL;
GO

-------------------------------------------------------------------------------
-- Application level: the three captures, and the government-property answer
-------------------------------------------------------------------------------
IF COL_LENGTH('dbo.BtoA_MainApp_Officer', 'Ofcr_Property_Document') IS NULL
    ALTER TABLE dbo.BtoA_MainApp_Officer
        ADD Ofcr_Property_Document nvarchar(max) NULL;      -- property photograph
GO

IF COL_LENGTH('dbo.BtoA_MainApp_Officer', 'Ofcr_Map_Document') IS NULL
    ALTER TABLE dbo.BtoA_MainApp_Officer
        ADD Ofcr_Map_Document nvarchar(max) NULL;           -- map screenshot
GO

IF COL_LENGTH('dbo.BtoA_MainApp_Officer', 'Ofcr_NoteSheet_Document') IS NULL
    ALTER TABLE dbo.BtoA_MainApp_Officer
        ADD Ofcr_NoteSheet_Document nvarchar(max) NULL;     -- khata note sheet: image OR pdf
GO

IF COL_LENGTH('dbo.BtoA_MainApp_Officer', 'Ofcr_GovtPropertyDetails') IS NULL
    ALTER TABLE dbo.BtoA_MainApp_Officer
        ADD Ofcr_GovtPropertyDetails nvarchar(1000) NULL;
GO

-- 0 = no, 1 = yes, 2 = refer to surveyor, NULL = not answered.
-- Ofcr_IsGovtProperty (bit NOT NULL) is left exactly as it is for existing readers.
IF COL_LENGTH('dbo.BtoA_MainApp_Officer', 'Ofcr_IsGovtProperty_Value') IS NULL
    ALTER TABLE dbo.BtoA_MainApp_Officer
        ADD Ofcr_IsGovtProperty_Value int NULL;
GO

-------------------------------------------------------------------------------
-- Confirm
-------------------------------------------------------------------------------
SELECT tbl = OBJECT_NAME(c.object_id), col = c.name, type = t.name,
       len = c.max_length, nullable = c.is_nullable
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.name IN ('Ofcr_Notice_Document','Ofcr_Property_Document','Ofcr_Map_Document',
                 'Ofcr_NoteSheet_Document','Ofcr_GovtPropertyDetails','Ofcr_IsGovtProperty_Value')
ORDER BY tbl, col;
GO
