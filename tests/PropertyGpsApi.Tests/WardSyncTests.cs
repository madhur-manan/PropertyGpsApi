using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Interfaces;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The new Fetch / Update: the phone says which App_Ids it holds, the server sends the next
/// 100 it lacks, and the phone acknowledges each batch once it is saved.
///
/// The SQL lives in db/20 and was exercised against UDD_KHATABTOA_TEST directly. What is
/// tested here is everything the API decides before and around it: the wire contract the
/// app depends on, the limits, and that a ward the officer is not mapped to is refused
/// before the B2A database is touched.
/// </summary>
public class WardSyncTests
{
    private static readonly JsonSerializerOptions Strict = new();

    public class TheWireContract
    {
        /// <summary>The body lib/services/http/http_single_site_remote.dart builds.</summary>
        [Fact]
        public void The_body_the_app_sends_binds_to_the_request()
        {
            const string body = """
                {"ulbCode":1,"wardNumber":"54","localCount":3,
                 "knownAppIds":[101,102,103],"returnedAppIds":[103],"batchSize":100,"includeClosed":true}
                """;

            var request = JsonSerializer.Deserialize<WardSyncRequest>(body, Strict)!;

            Assert.Equal(1, request.UlbCode);
            Assert.Equal("54", request.WardNumber);
            Assert.Equal(3, request.LocalCount);
            Assert.Equal([101, 102, 103], request.KnownAppIds);
            Assert.Equal([103], request.ReturnedAppIds);
            Assert.Equal(100, request.BatchSize);
            Assert.True(request.IncludeClosed);
        }

        /// <summary>An older build's zone and ward ids still bind, and play no part.</summary>
        [Fact]
        public void An_older_body_with_zone_and_ward_ids_still_binds()
        {
            var request = JsonSerializer.Deserialize<WardSyncRequest>(
                """{"corporationId":1,"zoneId":102,"wardId":54,"ulbCode":1,"wardNumber":"12A","localCount":0}""", Strict)!;

            Assert.Equal((1, "12A"), (request.UlbCode, request.WardNumber));
            Assert.Equal((1, 102, 54), (request.CorporationId, request.ZoneId, request.WardId));
        }

        [Fact]
        public void A_first_sync_may_send_no_ids_at_all()
        {
            var request = JsonSerializer.Deserialize<WardSyncRequest>(
                """{"ulbCode":1,"wardNumber":"54","localCount":0}""", Strict)!;

            Assert.Empty(request.KnownAppIds);
            Assert.Empty(request.ReturnedAppIds);
            Assert.Equal(WardSyncLimits.MaxBatch, request.BatchSize);
            Assert.False(request.IncludeClosed);
        }

        [Fact]
        public void The_response_names_the_fields_the_progress_screen_reads()
        {
            var json = JsonSerializer.Serialize(new WardSyncResponse
            {
                ServerCount = 2470,
                LocalCount = 1200,
                MissingCount = 1270,
                Items = [new PropertyDto { AppId = 7, Status = 400 }],
                Closed = [new WardSyncClosedDto { AppId = 9, AppStatus = 13 }]
            }, Strict);

            Assert.Contains("\"serverCount\":2470", json);
            Assert.Contains("\"localCount\":1200", json);
            Assert.Contains("\"missingCount\":1270", json);
            Assert.Contains("\"returned\":1", json);
            Assert.Contains("\"items\":[", json);
            Assert.Contains("\"status\":400", json);
            Assert.Contains("\"closed\":[{\"appId\":9,\"appStatus\":13}]", json);
        }

        [Fact]
        public void The_ack_body_the_app_sends_binds()
        {
            var request = JsonSerializer.Deserialize<WardSyncAckRequest>(
                """{"ulbCode":1,"wardNumber":"54","appIds":[101,102]}""", Strict)!;

            Assert.Equal((1, "54"), (request.UlbCode, request.WardNumber));
            Assert.Equal([101, 102], request.AppIds);
        }

        [Theory]
        [InlineData(typeof(WardSyncRequest))]
        [InlineData(typeof(WardSyncResponse))]
        [InlineData(typeof(WardSyncClosedDto))]
        [InlineData(typeof(WardSyncHolderDto))]
        [InlineData(typeof(WardSyncAckRequest))]
        [InlineData(typeof(WardSyncAckResponse))]
        public void Every_property_declares_its_wire_name(Type dto)
        {
            var missing = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is null)
                .Select(p => p.Name)
                .ToList();

            Assert.True(missing.Count == 0,
                $"{dto.Name} is missing [JsonPropertyName] on: {string.Join(", ", missing)}");
        }
    }

    /// <summary>
    /// Reported 2026-10-12 with a phone and an emulator as two RIs in one ward: a record one
    /// of them released stayed "allotted to another officer" on the other, because a record
    /// is downloaded once and its holder was never sent again.
    /// </summary>
    public class TheHolders
    {
        private static AssignmentRow Held(int appId, int officer) => new() { AppId = appId, Arch_Id = officer };

        [Fact]
        public void Every_open_record_the_phone_has_gets_its_holder_and_a_free_one_gets_zero()
        {
            var holders = WardSyncService.Holders(
                [7, 8, 9],
                new Dictionary<int, AssignmentRow> { [7] = Held(7, 11320), [9] = Held(9, 11331) });

            Assert.Equal(
                [(7, 11320), (8, 0), (9, 11331)],
                holders.Select(h => (h.AppId, h.AssignedToUserId)));
        }

        [Fact]
        public void A_record_reported_closed_is_left_out_and_each_id_counts_once() =>
            Assert.Equal([7, 9], WardSyncService.StillOpen([7, 8, 9, 7], [8]));

        [Fact]
        public void The_holders_travel_with_their_wire_names()
        {
            var json = JsonSerializer.Serialize(new WardSyncResponse
            {
                Holders = [new WardSyncHolderDto { AppId = 8, AssignedToUserId = 0 }]
            });

            Assert.Contains("\"holders\":[{\"appId\":8,\"assignedToUserId\":0}]", json);
        }

        /// <summary>Asked about every record a phone holds, so the ids cannot be one parameter each.</summary>
        [Fact]
        public void The_holder_lookup_takes_its_ids_as_one_string()
        {
            Assert.Contains("STRING_SPLIT(@appIds, ',')", AssignmentReader.Sql);
            Assert.DoesNotContain("IN @appIds", AssignmentReader.Sql);
        }

        // ---- the pull-down: propertyinfo/ward-sync/holders --------------------------------

        private static readonly Jurisdiction Hoodi = new() { UlbCode = 1, WardNumber = "54", WardName = "Hoodi" };

        private static WardSyncService Service(params Jurisdiction[] mapped) => new(
            new ThrowingConnections(),
            Options.Create(new StoredProcedureOptions()),
            new MappedOfficer(mapped),
            null!,
            null!,
            NullLogger<WardSyncService>.Instance);

        [Fact]
        public async Task The_pull_down_refuses_a_ward_that_is_not_the_officers_before_any_query()
        {
            var refused = await Assert.ThrowsAsync<ApiException>(() => Service(Hoodi).HoldersAsync(
                new WardHoldersRequest { UlbCode = 1, WardNumber = "112", AppIds = [7] }, 11320, default));

            Assert.Equal(403, refused.StatusCode);
            Assert.Equal(ApiErrorCodes.OutsideJurisdiction, refused.Code);
        }

        [Fact]
        public async Task The_pull_down_with_nothing_on_the_phone_asks_the_database_nothing()
        {
            var reply = await Service(Hoodi).HoldersAsync(new WardHoldersRequest { UlbCode = 1, WardNumber = "54" }, 11320, default);
            Assert.Empty(reply.Holders);
        }

        [Fact]
        public async Task The_pull_down_refuses_a_non_positive_id()
        {
            await Assert.ThrowsAsync<ApiException>(() => Service(Hoodi).HoldersAsync(
                new WardHoldersRequest { UlbCode = 1, WardNumber = "54", AppIds = [7, 0] }, 11320, default));
        }

        /// <summary>
        /// Only applications in the asked-for ward come back, so the pull-down never names the
        /// holder of anything outside the officer's jurisdiction.
        /// </summary>
        [Fact]
        public void The_pull_down_query_is_limited_to_the_ward_and_reads_the_active_holder()
        {
            var sql = WardSyncService.HoldersSql;
            Assert.Contains("STRING_SPLIT(@appIds, ',')", sql);
            Assert.Contains("md.MD_ULBCode = @ulbCode", sql);
            Assert.Contains("AND LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId))) = @wardNumber", sql);
            Assert.DoesNotContain("MD_ZoneId", sql);
            Assert.Contains("a.IsActive = 1", sql);
            Assert.Contains("ISNULL(h.Arch_Id, 0)", sql);
        }

        [Fact]
        public void The_pull_down_body_and_reply_use_the_apps_names()
        {
            var request = JsonSerializer.Deserialize<WardHoldersRequest>(
                """{"ulbCode":1,"wardNumber":"54","appIds":[7,8]}""")!;
            Assert.Equal((1, "54"), (request.UlbCode, request.WardNumber));
            Assert.Equal([7, 8], request.AppIds);

            var reply = JsonSerializer.Serialize(new WardHoldersResponse
            {
                Holders = [new WardSyncHolderDto { AppId = 7, AssignedToUserId = 11331 }]
            });
            Assert.Equal("""{"holders":[{"appId":7,"assignedToUserId":11331}]}""", reply);
        }
    }

    public class TheLimits
    {
        private static List<ValidationResult> Validate(object model)
        {
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(model, new ValidationContext(model), results, true);
            return results;
        }

        private static WardSyncRequest Page(int batchSize = 100, int localCount = 0) => new()
        {
            UlbCode = 1, WardNumber = "54", BatchSize = batchSize, LocalCount = localCount
        };

        [Theory]
        [InlineData(0)]
        [InlineData(101)]
        public void A_batch_outside_1_to_100_is_refused(int batchSize) =>
            Assert.NotEmpty(Validate(Page(batchSize)));

        [Fact]
        public void A_full_batch_of_100_is_accepted() =>
            Assert.Empty(Validate(Page(100)));

        [Fact]
        public void A_negative_count_is_refused() =>
            Assert.NotEmpty(Validate(Page(localCount: -1)));

        [Fact]
        public void A_ward_is_required() =>
            Assert.NotEmpty(Validate(new WardSyncRequest { UlbCode = 1 }));

        [Fact]
        public void A_ulb_is_required() =>
            Assert.NotEmpty(Validate(new WardSyncRequest { WardNumber = "54" }));

        [Theory]
        [InlineData("   ")]
        [InlineData("123456789012345678901")]   // 21 characters
        public void A_blank_or_overlong_ward_number_is_refused(string ward)
        {
            Assert.NotEmpty(Validate(new WardSyncRequest { UlbCode = 1, WardNumber = ward }));
            Assert.NotEmpty(Validate(new WardSyncAckRequest { UlbCode = 1, WardNumber = ward, AppIds = [1] }));
            Assert.NotEmpty(Validate(new WardHoldersRequest { UlbCode = 1, WardNumber = ward }));
            Assert.NotEmpty(Validate(new ReturnedSurveyRequest { UlbCode = 1, WardNumber = ward, AppId = 1 }));
        }

        [Theory]
        [InlineData("12A")]
        [InlineData(" 54 ")]
        [InlineData("12345678901234567890")]   // 20 characters
        public void A_text_ward_number_of_up_to_20_characters_is_accepted(string ward) =>
            Assert.Empty(Validate(new WardSyncRequest { UlbCode = 1, WardNumber = ward }));

        /// <summary>
        /// USP_S_GpsWardSync takes the ward as @UlbCode + @WardNumber, the trimmed text, ahead
        /// of the parameters it always had; @ZoneId / @WardId are gone.
        /// </summary>
        [Fact]
        public void The_page_procedure_is_given_the_ulb_and_the_trimmed_ward_number()
        {
            var request = new WardSyncRequest
            {
                UlbCode = 1, WardNumber = " 12A ", ZoneId = 102, WardId = 54, LocalCount = 2,
                KnownAppIds = [5, 6], ReturnedAppIds = [6], BatchSize = 500, IncludeClosed = true, RefreshAppIds = [5]
            };

            var p = WardSyncService.PageParameters(request, WardSyncService.RequireWard(request.UlbCode, request.WardNumber));

            Assert.Equal(
                ["@UlbCode", "@WardNumber", "@LocalCount", "@KnownAppIds", "@ReturnedAppIds", "@BatchSize", "@IncludeClosed", "@RefreshAppIds"],
                p.ParameterNames.Select(n => "@" + n));
            Assert.Equal(1, p.Get<int>("@UlbCode"));
            Assert.Equal("12A", p.Get<string>("@WardNumber"));
            Assert.Equal("5,6", p.Get<string>("@KnownAppIds"));
            Assert.Equal(100, p.Get<int>("@BatchSize"));
        }

        /// <summary>
        /// App 1.3.0 keeps one record per App_Id and asks for every application (db/44). The
        /// parameter goes last and only then: a server without db/44 does not know it.
        /// </summary>
        [Fact]
        public void Every_application_is_asked_for_only_when_the_phone_says_so()
        {
            var every = new WardSyncRequest { UlbCode = 1, WardNumber = "11", OnePerEpid = false };
            var p = WardSyncService.PageParameters(every, "11");
            Assert.Equal("@OnePerEpid", "@" + p.ParameterNames.Last());
            Assert.False(p.Get<bool>("@OnePerEpid"));

            var older = new WardSyncRequest { UlbCode = 1, WardNumber = "11" };
            Assert.True(older.OnePerEpid);
            Assert.DoesNotContain("OnePerEpid", WardSyncService.PageParameters(older, "11").ParameterNames);
        }

        [Fact]
        public void A_phone_that_does_not_say_gets_one_application_per_epid()
        {
            var request = JsonSerializer.Deserialize<WardSyncRequest>("""{"ulbCode":1,"wardNumber":"11"}""")!;
            Assert.True(request.OnePerEpid);
        }

        [Fact]
        public void A_returned_survey_is_read_only_from_the_ward_asked_for()
        {
            var sql = WardSyncService.ReturnedAppSql;
            Assert.Contains("md.MD_ULBCode = @ulbCode", sql);
            Assert.Contains("AND LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId))) = @wardNumber", sql);
            Assert.DoesNotContain("MD_ZoneId", sql);
        }

        [Fact]
        public void An_ack_of_nothing_is_refused() =>
            Assert.NotEmpty(Validate(new WardSyncAckRequest { UlbCode = 1, WardNumber = "54", AppIds = [] }));

        [Fact]
        public void An_ack_larger_than_one_batch_is_refused() =>
            Assert.NotEmpty(Validate(new WardSyncAckRequest
            {
                UlbCode = 1, WardNumber = "54", AppIds = Enumerable.Range(1, 101).ToList()
            }));

        [Fact]
        public void An_ack_of_one_full_batch_is_accepted() =>
            Assert.Empty(Validate(new WardSyncAckRequest
            {
                UlbCode = 1, WardNumber = "54", AppIds = Enumerable.Range(1, 100).ToList()
            }));

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(100, 100)]
        [InlineData(500, 100)]
        public void The_batch_sent_to_the_procedure_is_never_more_than_100(int asked, int sent) =>
            Assert.Equal(sent, WardSyncService.ClampBatch(asked));

        [Fact]
        public void Ids_go_to_the_procedure_as_plain_comma_separated_numbers() =>
            Assert.Equal("5,3,9", WardSyncService.ToCsv([5, 3, 5, 9]));

        [Fact]
        public void No_ids_is_an_empty_list_not_a_null() =>
            Assert.Equal("", WardSyncService.ToCsv([]));
    }

    public class WhoMaySync
    {
        private static readonly Jurisdiction Hoodi = new() { UlbCode = 1, WardNumber = "54", WardName = "Hoodi" };
        private static readonly Jurisdiction Domlur = new() { UlbCode = 1, WardNumber = "112", WardName = "Domlur" };

        private static WardSyncService Service(params Jurisdiction[] mapped) => new(
            new ThrowingConnections(),
            Options.Create(new StoredProcedureOptions()),
            new MappedOfficer(mapped),
            null!,
            null!,
            NullLogger<WardSyncService>.Instance);

        private static WardSyncRequest Page(int ulb, string ward, params int[] known) => new()
        {
            UlbCode = ulb, WardNumber = ward, LocalCount = known.Length, KnownAppIds = known
        };

        [Fact]
        public async Task A_ward_the_officer_is_not_mapped_to_is_refused_before_any_query()
        {
            var refused = await Assert.ThrowsAsync<ApiException>(
                () => Service(Hoodi).PageAsync(Page(1, "112"), 11320, default));

            Assert.Equal(403, refused.StatusCode);
            Assert.Equal(ApiErrorCodes.OutsideJurisdiction, refused.Code);
        }

        /// <summary>Every RI of the ward may read a returned survey (N2) - and only of that ward.</summary>
        [Fact]
        public async Task A_returned_survey_outside_the_officers_wards_is_refused_before_any_query()
        {
            var refused = await Assert.ThrowsAsync<ApiException>(
                () => Service(Hoodi).ReturnedSurveyAsync(
                    new ReturnedSurveyRequest { UlbCode = 1, WardNumber = "112", AppId = 113 }, 11320, default));

            Assert.Equal(403, refused.StatusCode);
            Assert.Equal(ApiErrorCodes.OutsideJurisdiction, refused.Code);
        }

        [Fact]
        public async Task An_officer_mapped_to_no_ward_may_sync_nothing()
        {
            var refused = await Assert.ThrowsAsync<ApiException>(
                () => Service().PageAsync(Page(1, "54"), 11320, default));

            Assert.Equal(403, refused.StatusCode);
        }

        /// <summary>
        /// The token carries only one ward, so a two-ward RI checked against it was refused
        /// their second ward. The sync checks the mapping instead.
        /// </summary>
        [Fact]
        public async Task A_two_ward_officer_may_sync_either_ward()
        {
            var service = Service(Hoodi, Domlur);

            await Assert.ThrowsAsync<NotSupportedException>(() => service.PageAsync(Page(1, "54"), 1036, default));
            await Assert.ThrowsAsync<NotSupportedException>(() => service.PageAsync(Page(1, "112"), 1036, default));
        }

        [Fact]
        public async Task The_same_ward_number_in_another_ulb_is_not_the_mapped_ward()
        {
            var refused = await Assert.ThrowsAsync<ApiException>(
                () => Service(Hoodi).PageAsync(Page(2, "54"), 11320, default));

            Assert.Equal(403, refused.StatusCode);
        }

        /// <summary>The ward number is text: matched trimmed and ignoring case, never parsed.</summary>
        [Fact]
        public async Task A_text_ward_number_matches_trimmed_and_ignoring_case()
        {
            var service = Service(new Jurisdiction { UlbCode = 7, WardNumber = "12A" });

            await Assert.ThrowsAsync<NotSupportedException>(() => service.PageAsync(Page(7, " 12a "), 11320, default));
            await Assert.ThrowsAsync<ApiException>(() => service.PageAsync(Page(7, "12"), 11320, default));
        }

        [Theory]
        [InlineData(0, "54")]
        [InlineData(1, "")]
        [InlineData(1, "   ")]
        [InlineData(1, "123456789012345678901")]   // 21 characters
        public async Task A_missing_or_overlong_ward_is_a_bad_request_before_any_query(int ulb, string ward)
        {
            var refused = await Assert.ThrowsAsync<ApiException>(
                () => Service(Hoodi).PageAsync(Page(ulb, ward), 11320, default));

            Assert.Equal(400, refused.StatusCode);
        }

        [Fact]
        public async Task An_ack_for_an_unmapped_ward_is_refused_before_any_update()
        {
            var refused = await Assert.ThrowsAsync<ApiException>(
                () => Service(Hoodi).AckAsync(
                    new WardSyncAckRequest { UlbCode = 1, WardNumber = "112", AppIds = [1, 2] }, 11320, default));

            Assert.Equal(403, refused.StatusCode);
        }

        [Fact]
        public async Task An_ack_for_a_mapped_ward_reaches_the_database()
        {
            await Assert.ThrowsAsync<NotSupportedException>(
                () => Service(Hoodi).AckAsync(
                    new WardSyncAckRequest { UlbCode = 1, WardNumber = "54", AppIds = [1, 2] }, 11320, default));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task An_id_that_cannot_be_an_application_is_refused(int bad)
        {
            var refused = await Assert.ThrowsAsync<ApiException>(
                () => Service(Hoodi).PageAsync(Page(1, "54", 101, bad), 11320, default));

            Assert.Equal(400, refused.StatusCode);
        }
    }

    /// <summary>The ULB/ward dropdowns: a field officer sees only the wards they are mapped to.</summary>
    public class WhichWardsTheDropdownShows
    {
        private static readonly IReadOnlyList<Jurisdiction> Mapped = [new() { UlbCode = 1, WardNumber = "54" }];

        private static readonly IReadOnlyList<Jurisdiction> ProcedureRows =
        [
            new() { UlbCode = 1, WardNumber = "54" },
            new() { UlbCode = 1, WardNumber = "55" },
            new() { UlbCode = 2, WardNumber = "60" }
        ];

        [Theory]
        [InlineData(117)]
        public void A_field_officer_gets_exactly_their_mapped_wards(int roleId) =>
            Assert.Equal(Mapped, OfficerService.ChooseJurisdictions(roleId, Mapped, ProcedureRows));

        [Theory]
        [InlineData(117)]
        public void A_field_officer_with_no_mapping_gets_no_wards_rather_than_someone_elses(int roleId) =>
            Assert.Empty(OfficerService.ChooseJurisdictions(roleId, [], ProcedureRows));

        [Fact]
        public void Other_roles_keep_their_own_ulb_and_ward() =>
            Assert.Equal(ProcedureRows, OfficerService.ChooseJurisdictions(125, [], ProcedureRows));
    }

    private sealed class ThrowingConnections : ISqlConnectionFactory
    {
        public Task<SqlConnection> OpenAsync(DbTarget target, CancellationToken ct = default)
            => throw new NotSupportedException("the ward check passed; a query was attempted");
    }

    private sealed class MappedOfficer(IReadOnlyList<Jurisdiction> wards) : IOfficerService
    {
        public Task<IReadOnlyList<Jurisdiction>> MappedWardsAsync(long officerId, CancellationToken ct) =>
            Task.FromResult(wards);

        public Task<bool> OfficerExistsAsync(string mobile, int roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> StoreOtpAsync(string mobile, string otp, CancellationToken ct) => throw new NotSupportedException();
        public Task<OtpValidationResult> ValidateOtpAsync(string mobile, string otp, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordLoginAsync(Officer officer, string? clientIp, CancellationToken ct) => throw new NotSupportedException();
        public Task<Officer?> LoadAsync(string mobile, CancellationToken ct) => throw new NotSupportedException();
        public Task<VerifyOtpResponse> ProfileAsync(string? mobile, CancellationToken ct) => throw new NotSupportedException();
    }
}
