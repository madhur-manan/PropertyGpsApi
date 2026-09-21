/*
    Restores BtoA_MainApp_Officer.Ofcr_CorrectedRoadDetails in UDD_KHATABTOA_TEST.

    APPLIES TO UDD_KHATABTOA_TEST ONLY.

    Why this is needed: the BtoA_ -> Ofcr_ rename in this database renamed most columns
    of BtoA_MainApp_Officer and dropped this one outright. Comparing the table against
    BtoA_MainApp_Officer_Backup shows every other difference is a rename with a
    counterpart -

        ApplicationDisplayId -> Ofcr_ApplicationDisplayId
        Ofcr_BtoA_MainAppId  -> Ofcr_App_Id
        SsaId                -> Ofcr_SsaId
        PropertyId           -> Ofcr_MotherEPID
        ApplicationDate      -> Ofcr_ApplicationDate

    - and this one alone has none. The database's own procedures never stopped using it:
    USP_IU_BtoA_MainApp_Officer still declares @Ofcr_CorrectedRoadDetails and inserts it,
    Tr_BtoA_MainApp_Officer still copies it into history, and three read procedures still
    select it. So every submit fails at runtime with

        Invalid column name 'Ofcr_CorrectedRoadDetails'

    which is what the app hit after the media check began passing.

    NULLABLE, where the backup had it NOT NULL, and deliberately so. The table already
    holds 40,313 rows written while the column did not exist, so the officers who filed
    them were never asked the question. Adding NOT NULL DEFAULT 0 would stamp "road
    details were not corrected" onto forty thousand surveys that recorded no such thing.
    NULL says what is true: not recorded. New rows carry the officer's real answer.

    FOR THE DBA: check whether the live database's BtoA_MainApp_Officer still has this
    column. If it does not, USP_IU_BtoA_MainApp_Officer is broken on live in exactly the
    same way, and no officer submission can be succeeding through it.
*/

USE UDD_KHATABTOA_TEST;
GO

IF DB_NAME() <> 'UDD_KHATABTOA_TEST'
    THROW 50001, 'Refusing to run: this script is for UDD_KHATABTOA_TEST only.', 1;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.BtoA_MainApp_Officer')
      AND name = 'Ofcr_CorrectedRoadDetails')
BEGIN
    ALTER TABLE dbo.BtoA_MainApp_Officer ADD Ofcr_CorrectedRoadDetails bit NULL;
    PRINT 'Added BtoA_MainApp_Officer.Ofcr_CorrectedRoadDetails (bit, nullable).';
END
ELSE
    PRINT 'BtoA_MainApp_Officer.Ofcr_CorrectedRoadDetails already exists; nothing to do.';
GO

-- Verify
SELECT TableName = 'BtoA_MainApp_Officer',
       ColumnName = c.name,
       DataType = t.name,
       IsNullable = c.is_nullable
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.BtoA_MainApp_Officer')
  AND c.name = 'Ofcr_CorrectedRoadDetails';
GO
