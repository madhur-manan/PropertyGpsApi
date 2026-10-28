using System.Text.Json;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;
using static PropertyGpsApi.Services.VerificationSubmitService;

namespace PropertyGpsApi.Tests;

/// <summary>
/// N2: QC returns a survey (App_Status 400); it is free to any RI of the ward, any of them may
/// read the earlier answers, the one who allots it corrects and resubmits, and QC then sees
/// that one survey only.
///
/// The SQL was run against UDD_KHATABTOA_TEST (db/32 sets App 113 up); what is pinned here is
/// what it must keep saying, and every decision the API makes around it.
/// </summary>
public class QcReturnTests
{
    private const long Me = 11320, Other = 11331;
    private static readonly DateTime Sent = new(2026, 10, 13, 10, 0, 0);

    public class WhoMaySubmit
    {
        private static SubmitStateRow State(int? status, long? holder = null, long? lastRi = null,
            DateTime? riOn = null, DateTime? reviewOn = null) => new()
        {
            AppStatus = status, HolderId = holder, LastRiBy = lastRi, LastRiOn = riOn, LastReviewOn = reviewOn
        };

        [Fact]
        public void A_waiting_application_is_accepted_as_before() =>
            Assert.Equal(SubmitGate.Accept, Gate(State(10), Me));

        [Fact]
        public void An_application_with_no_status_is_accepted_as_before() =>
            Assert.Equal(SubmitGate.Accept, Gate(State(null), Me));

        [Fact]
        public void A_returned_survey_is_accepted_from_the_officer_who_allotted_it() =>
            Assert.Equal(SubmitGate.Accept, Gate(State(400, holder: Me, lastRi: Other, riOn: Sent), Me));

        [Fact]
        public void A_returned_survey_is_refused_from_an_officer_who_has_not_allotted_it() =>
            Assert.Equal(SubmitGate.NotHolder, Gate(State(400, holder: Other), Me));

        [Fact]
        public void A_returned_survey_nobody_has_allotted_is_refused_even_from_its_first_surveyor() =>
            Assert.Equal(SubmitGate.NotHolder, Gate(State(400, holder: null, lastRi: Me, riOn: Sent), Me));

        /// <summary>
        /// Role 116 surveys land at 13, role 117 at 15 (approve) or 12 (reject) - db/30. A retry
        /// after a lost reply, or an edit made meanwhile, replaces my earlier send as before N2.
        /// </summary>
        [Theory]
        [InlineData(13)] [InlineData(15)] [InlineData(12)]
        public void I_may_send_my_own_survey_again_until_a_reviewer_acts(int statusAfterMySubmit) =>
            Assert.Equal(SubmitGate.Accept, Gate(State(statusAfterMySubmit, lastRi: Me, riOn: Sent), Me));

        [Theory]
        [InlineData(13)] [InlineData(15)] [InlineData(12)]
        public void A_survey_another_officer_has_sent_is_closed_to_me(int status) =>
            Assert.Equal(SubmitGate.SubmittedByOther, Gate(State(status, lastRi: Other, riOn: Sent), Me));

        [Theory]
        [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(16)] [InlineData(150)] [InlineData(300)]
        public void Once_a_reviewer_has_written_since_the_survey_nobody_may_submit(int status) =>
            Assert.Equal(SubmitGate.Closed,
                Gate(State(status, holder: Me, lastRi: Me, riOn: Sent, reviewOn: Sent.AddHours(1)), Me));

        [Fact]
        public void An_earlier_review_does_not_close_a_later_survey() =>
            Assert.Equal(SubmitGate.Accept,
                Gate(State(13, lastRi: Me, riOn: Sent, reviewOn: Sent.AddDays(-1)), Me));

        [Fact]
        public void A_status_past_waiting_with_no_RI_survey_at_all_is_closed() =>
            Assert.Equal(SubmitGate.Closed, Gate(State(16), Me));
    }

    public class TheStateQuery
    {
        [Fact]
        public void Reads_a_live_holding_only_not_a_completed_one() =>
            Assert.Contains("a.IsActive = 1 AND a.Status = 'Pending'", SubmitStateSql);

        [Fact]
        public void Reads_the_last_survey_from_RI_rows_only() =>
            Assert.Contains("s.Status_Active = 1 AND s.CRole IN (116, 117)", SubmitStateSql);

        [Fact]
        public void Reads_every_reviewer_not_only_QC() =>
            Assert.Contains("q.CRole NOT IN (116, 117)", SubmitStateSql);
    }

    public class ReplacingTheFirstSurvey
    {
        [Theory]
        [InlineData(nameof(SupersedeOtherRoadsSql))]
        [InlineData(nameof(SupersedeOtherDocumentsSql))]
        [InlineData(nameof(SupersedeOtherStatusSql))]
        public void Retires_only_other_officers_live_rows_never_deletes(string name)
        {
            var sql = (string)typeof(VerificationSubmitService)
                .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .GetValue(null)!;

            Assert.StartsWith("UPDATE", sql.TrimStart());
            Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<> @cby", sql);
        }

        [Fact]
        public void Only_road_documents_are_retired() =>
            Assert.Contains("docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444'", SupersedeOtherDocumentsSql);

        [Fact]
        public void QCs_own_rows_are_left_alone()
        {
            Assert.Contains("CRole IN (116, 117)", SupersedeOtherStatusSql);
            Assert.Contains("ISNULL(CRole, 0) IN (0, 116, 117)", SupersedeOtherRoadsSql);
            Assert.Contains("ISNULL(docTrn_CRole, 1) IN (1, 116, 117)", SupersedeOtherDocumentsSql);
        }
    }

    public class AReturnedSurveyIsFree
    {
        [Fact]
        public void The_holder_reader_ignores_a_completed_row_on_a_returned_survey() =>
            Assert.Contains("NOT (a.Status = 'Completed' AND EXISTS", AssignmentReader.Sql);

        [Fact]
        public void So_does_the_pull_down() =>
            Assert.Contains("AND NOT (a.Status = 'Completed' AND ap.App_Status = 400)", WardSyncService.HoldersSql);

        [Fact]
        public void Allotting_retires_only_a_completed_row_and_only_at_400()
        {
            var sql = ApplicationService.ReleaseReturnedSql;
            Assert.Contains("a.IsActive = 1 AND a.Status = 'Completed'", sql);
            Assert.Contains("m.App_Status = 400", sql);
            Assert.Contains("IsActive = 0, Remarks = 'Returned by QC'", sql);
        }
    }

    public class QcRemarks
    {
        [Fact]
        public void Come_from_QCs_newest_active_row_only() =>
            Assert.Contains("s.Status_Active = 1 AND s.CRole = @qcRole", WardSyncService.QcRemarksSql);

        [Fact]
        public void Travel_on_the_property_as_qcRemarks() =>
            Assert.Contains("\"qcRemarks\":\"Recheck\"",
                JsonSerializer.Serialize(new PropertyDto { QcRemarks = "Recheck" }));

        [Fact]
        public void The_returned_survey_reply_uses_the_apps_names()
        {
            var json = JsonSerializer.Serialize(new ReturnedSurveyResponse
            {
                AppId = 113, ApplicationId = "202510150000113", QcRemarks = "Recheck", SubmittedBy = Me,
                Survey = new EarlierSurveyDto()
            });
            foreach (var name in new[] { "appId", "applicationId", "qcRemarks", "submittedBy", "submittedOn", "survey" })
                Assert.Contains($"\"{name}\"", json);
        }
    }
}
