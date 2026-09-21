/*
    Lets BtoA_MainApp_Officer accept an insert again in UDD_KHATABTOA_TEST.

    APPLIES TO UDD_KHATABTOA_TEST ONLY.

    The column:

        Ofcr_CorrectedOfcr_Corrected_PropertyArea   bit NOT NULL, no default

    is a casualty of the BtoA_ -> Ofcr_ rename. Its name is literally the prefix applied
    twice - "Ofcr_Corrected" + "Ofcr_Corrected_PropertyArea" - and the same pass left
    another one behind in the same table ("Ofcr_Existing_Ofcr_IsGovtProperty" sitting
    beside "Ofcr_Existing_IsGovtProperty").

    The consequence is not cosmetic. Nothing in this database writes it any more: no
    procedure, trigger or view in sys.sql_modules mentions the mangled name, and the
    insert procedure USP_IU_BtoA_MainApp_Officer has no parameter that matches it. Being
    NOT NULL with no default, it therefore rejects EVERY insert into the table -

        Cannot insert the value NULL into column 'Ofcr_CorrectedOfcr_Corrected_PropertyArea'

    - so no officer submission can succeed in this database at all, ours or anyone's.

    Made nullable rather than given a DEFAULT (0). The column holds real data on the
    40,313 rows copied from live - 23,882 zeros and 16,431 ones - so it means something
    there, and stamping 0 on every new row would record an answer this database has no
    way to collect. NULL says what is true: this deployment cannot populate it.

    Existing rows are untouched; widening to NULL does not rewrite them.

    FOR THE DBA - two questions this raises about LIVE:
      1. What is this column properly called on live, and does its procedure populate it?
         If live carries the same mangled name, live inserts are failing the same way.
      2. The same rename pass produced Ofcr_Existing_Ofcr_IsGovtProperty. Worth a sweep
         for every double-prefixed column before this database is trusted as a mirror.
*/

USE UDD_KHATABTOA_TEST;
GO

IF DB_NAME() <> 'UDD_KHATABTOA_TEST'
    THROW 50001, 'Refusing to run: this script is for UDD_KHATABTOA_TEST only.', 1;
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.BtoA_MainApp_Officer')
      AND name = 'Ofcr_CorrectedOfcr_Corrected_PropertyArea'
      AND is_nullable = 0)
BEGIN
    ALTER TABLE dbo.BtoA_MainApp_Officer
        ALTER COLUMN Ofcr_CorrectedOfcr_Corrected_PropertyArea bit NULL;
    PRINT 'Ofcr_CorrectedOfcr_Corrected_PropertyArea is now nullable.';
END
ELSE
    PRINT 'Already nullable or absent; nothing to do.';
GO

SELECT ColumnName = c.name, DataType = t.name, IsNullable = c.is_nullable
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.BtoA_MainApp_Officer')
  AND c.name = 'Ofcr_CorrectedOfcr_Corrected_PropertyArea';
GO
