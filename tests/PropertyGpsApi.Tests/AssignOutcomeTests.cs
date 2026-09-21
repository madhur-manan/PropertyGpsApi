using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Properties;

namespace PropertyGpsApi.Tests;

/// <summary>
/// Reading USP_IU_Architect_AssignedApp's verdict.
///
/// The procedure rejects with a single 400 and an English sentence, so the
/// error code is recovered from the wording — which makes three specific
/// phrases load-bearing text. Nothing tested this while it lived in the
/// controller as a nested ternary.
///
/// The codes matter to the app: QUEUE_FULL tells an officer to finish what they
/// are holding, ALREADY_ASSIGNED tells them to pick a different record. Getting
/// one wrong sends them to the wrong remedy.
/// </summary>
public class AssignOutcomeTests
{
    private static AssignOutcome Rejected(string? message) => new(400, 0, 0, message);

    [Theory]
    [InlineData("Already Assigned to another officer")]
    [InlineData("already assigned")]
    [InlineData("ALREADY ASSIGNED")]
    public void A_record_someone_else_holds_reads_as_already_assigned(string message)
        => Assert.Equal(ApiErrorCodes.AlreadyAssigned, Rejected(message).RejectionCode);

    [Fact]
    public void A_full_queue_reads_as_queue_full()
        => Assert.Equal(
            ApiErrorCodes.QueueFull,
            Rejected("You currently have two applications pending in your queue").RejectionCode);

    [Fact]
    public void Too_many_reassignments_reads_as_the_reassign_limit()
        => Assert.Equal(
            ApiErrorCodes.ReassignLimitReached,
            Rejected("This has reached the maximum allowed limit").RejectionCode);

    [Fact]
    public void An_unrecognised_sentence_degrades_to_a_generic_code()
    {
        // A reworded procedure must produce a generic code, never a wrong one —
        // the officer still sees the sentence verbatim either way.
        var outcome = Rejected("Something the procedure has never said before.");

        Assert.Equal(ApiErrorCodes.AssignRejected, outcome.RejectionCode);
        Assert.Equal("Something the procedure has never said before.", outcome.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_rejection_with_no_reason_still_says_something(string? message)
    {
        var outcome = Rejected(message);

        Assert.Equal("The record could not be allotted.", outcome.Text);
        Assert.Equal(ApiErrorCodes.AssignRejected, outcome.RejectionCode);
    }

    [Fact]
    public void The_sentence_is_trimmed_for_display()
        => Assert.Equal("Already Assigned", Rejected("  Already Assigned  ").Text);

    [Fact]
    public void Only_200_counts_as_success()
    {
        Assert.True(new AssignOutcome(200, 1, 2, null).Succeeded);
        Assert.False(new AssignOutcome(400, 0, 0, null).Succeeded);
        Assert.False(new AssignOutcome(0, 0, 0, null).Succeeded);
    }
}
