using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The acknowledgement that makes ward delivery once-only.
///
/// After the device stores a fetched ward it posts push-status, and a successful
/// acknowledgement sets IsPushedToGps = 1 on BtoAMainApp. USP_S_GetAppDetails then
/// stops returning that application to anybody, on any device, forever. So an
/// acknowledgement sent for a record the device did not actually store is not a
/// cosmetic bug - it strands that record, held by nobody and offered to nobody.
///
/// Nothing tested this endpoint on either side before now.
/// </summary>
public class PushStatusTests
{
    // Deliberately strict: no camelCase policy and case-sensitive, so a property only
    // binds if it declares its wire name explicitly. That is the contract an app already
    // in the field depends on.
    private static readonly JsonSerializerOptions Strict = new();

    /// <summary>
    /// USP_U_GpsPushedDetails branches on exactly 200 and 500. A third value takes no
    /// branch, returns no result set, and is reported as "not accepted" while the
    /// application keeps IsPushedToGps = 0 - a ward that re-downloads forever with no
    /// error raised anywhere. The mapping is closed in the service for that reason, and
    /// this is what holds it closed.
    /// </summary>
    [Fact]
    public void A_stored_record_maps_to_the_procedures_success_branch()
        => Assert.Equal(200, PushStatusService.StatusCodeFor(true));

    [Fact]
    public void A_record_the_device_could_not_store_maps_to_the_failure_branch()
        => Assert.Equal(500, PushStatusService.StatusCodeFor(false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_input_can_produce_a_code_the_procedure_does_not_branch_on(bool success)
    {
        var code = PushStatusService.StatusCodeFor(success);

        Assert.True(code is PushStatusService.GpsSuccess or PushStatusService.GpsFailure,
            $"StatusCodeFor({success}) produced {code}, which the procedure ignores.");
    }

    public class TheRemarkWrittenAgainstTheRecord
    {
        private static string Remark(string? message, bool success = true) =>
            PushStatusService.RemarkFor(new PushStatusItem
            {
                ApplicationId = "202608250103674",
                Epid = "3263540784",
                Success = success,
                Message = message
            });

        [Fact]
        public void The_device_gets_the_last_word_when_it_sends_one()
            => Assert.Equal("Wrote 48 of 50 rows", Remark("Wrote 48 of 50 rows"));

        [Fact]
        public void And_is_trimmed_before_it_is_stored()
            => Assert.Equal("Disk full", Remark("  Disk full\n"));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Silence_from_a_successful_device_still_records_something(string? message)
            => Assert.Equal("Stored on device via PropertyGpsApi", Remark(message));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Silence_from_a_failing_device_says_it_failed(string? message)
        {
            // Never the success wording. This string is what somebody reads months later
            // when asking why a record was never surveyed.
            Assert.Equal("Device reported a failure", Remark(message, success: false));
        }
    }

    public class TheWireContract
    {
        /// <summary>
        /// Exactly the body `_acknowledge` builds in
        /// lib/services/http/http_single_site_remote.dart. If a C# rename ever breaks this,
        /// every device in the field stops being able to acknowledge anything.
        /// </summary>
        [Fact]
        public void The_body_the_app_sends_binds_to_the_request()
        {
            const string body = """
                {"items":[{"applicationId":"202608250103674","epid":"3263540784","success":true,"message":null}]}
                """;

            var request = JsonSerializer.Deserialize<PushStatusRequest>(body, Strict);

            var item = Assert.Single(request!.Items);
            Assert.Equal("202608250103674", item.ApplicationId);
            Assert.Equal("3263540784", item.Epid);
            Assert.True(item.Success);
            Assert.Null(item.Message);
        }

        [Fact]
        public void An_item_with_no_success_field_is_treated_as_not_stored()
        {
            // bool defaults to false, and false is the safe default here: the record stays
            // unpushed and is offered again. A default of true would strand it.
            const string body = """
                {"items":[{"applicationId":"1","epid":"2"}]}
                """;

            var request = JsonSerializer.Deserialize<PushStatusRequest>(body, Strict);

            Assert.False(Assert.Single(request!.Items).Success);
        }

        [Fact]
        public void The_response_names_the_fields_the_app_will_need_to_read()
        {
            // The app discards this body today. It should not, and it cannot start reading
            // it if the names drift in the meantime.
            var json = JsonSerializer.Serialize(new PushStatusResponse
            {
                Accepted = 1,
                Rejected = 1,
                Results =
                [
                    new PushStatusResult
                    {
                        ApplicationId = "1", Epid = "2", Accepted = false,
                        Message = "No matching application found."
                    }
                ]
            }, Strict);

            Assert.Contains("\"accepted\":1", json);
            Assert.Contains("\"rejected\":1", json);
            Assert.Contains("\"results\":[", json);
            Assert.Contains("\"applicationId\":\"1\"", json);
            Assert.Contains("\"epid\":\"2\"", json);
            Assert.Contains("\"message\":\"No matching application found.\"", json);
        }

        [Theory]
        [InlineData(typeof(PushStatusRequest))]
        [InlineData(typeof(PushStatusItem))]
        [InlineData(typeof(PushStatusResponse))]
        [InlineData(typeof(PushStatusResult))]
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

    public class WhatTheEndpointRefuses
    {
        private static List<ValidationResult> Validate(object model)
        {
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(model, new ValidationContext(model), results, true);
            return results;
        }

        private static PushStatusItem Item(int n) => new()
        {
            ApplicationId = $"app-{n}",
            Epid = $"{n}",
            Success = true
        };

        [Fact]
        public void An_acknowledgement_of_nothing_is_rejected()
        {
            // A device with nothing to report should not call at all. An empty batch is far
            // more likely to be a bug than an intention.
            Assert.NotEmpty(Validate(new PushStatusRequest { Items = [] }));
        }

        [Fact]
        public void A_batch_larger_than_the_agreed_limit_is_rejected()
        {
            var request = new PushStatusRequest
            {
                Items = Enumerable.Range(1, 501).Select(Item).ToList()
            };

            Assert.NotEmpty(Validate(request));
        }

        [Fact]
        public void A_full_sized_batch_is_accepted()
        {
            var request = new PushStatusRequest
            {
                Items = Enumerable.Range(1, 500).Select(Item).ToList()
            };

            Assert.Empty(Validate(request));
        }

        [Theory]
        [InlineData("", "3263540784")]
        [InlineData("202608250103674", "")]
        public void An_item_that_names_no_application_is_rejected(string applicationId, string epid)
        {
            // The procedure matches on both. A blank one matches nothing, so the device would
            // believe a record was acknowledged that was never touched.
            var item = new PushStatusItem
            {
                ApplicationId = applicationId,
                Epid = epid,
                Success = true
            };

            Assert.NotEmpty(Validate(item));
        }
    }
}
