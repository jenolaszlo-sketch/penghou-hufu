using System.Text;
using System.Text.Json.Nodes;
using Hufu.LocalHost;
using Xunit;

namespace Hufu.LocalHost.Tests;

public sealed class LocalHostApplicationTests
{
    [Fact]
    public async Task ExactOperatorApprovalExecutesOnceAndReopensWithDurableOutcome()
    {
        using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.PreviewAsync("operation-1");
        Assert.Equal("Prepared", preview.Status);
        Assert.Equal("old", fixture.Content);
        Assert.Equal("docs/file.txt", preview.Target);
        Assert.Equal(3, preview.OriginalBytes);
        Assert.Equal(3, preview.ProposedBytes);
        using (var worker = fixture.Worker("operation-1"))
            Assert.NotEqual("Succeeded", (await worker.ApplyAsync("operation-1")).Status);
        Assert.Equal("old", fixture.Content);
        Assert.Equal("Recorded", (await fixture.ApproveAsync("operation-1", preview.AdmissionIdentity!)).Status);
        using (var worker = fixture.Worker("operation-1"))
            Assert.Equal("Succeeded", (await worker.ApplyAsync("operation-1")).Status);
        Assert.Equal("new", fixture.Content);
        using (var reopened = fixture.Operator("operation-1"))
            Assert.Equal("Completed", (await reopened.InspectAsync("operation-1")).Status);
        using (var replay = fixture.Worker("operation-1"))
            Assert.Equal("AlreadyRecorded", (await replay.ApplyAsync("operation-1")).Status);
        Assert.Equal("new", fixture.Content);
    }

    [Fact]
    public async Task ConfirmationMustBindTheExactCapturedAdmission()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PreviewAsync("wrong-confirmation");
        Assert.Equal("ConfirmationMismatch", (await fixture.ApproveAsync("wrong-confirmation", new string('0', 64))).Status);
        using var worker = fixture.Worker("wrong-confirmation");
        Assert.NotEqual("Succeeded", (await worker.ApplyAsync("wrong-confirmation")).Status);
        Assert.Equal("old", fixture.Content);
    }

    [Fact]
    public async Task ChangedTargetRequiresANewPreviewAndNeverUsesOldApproval()
    {
        using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.PreviewAsync("changed-file");
        File.WriteAllText(fixture.Target, "other", new UTF8Encoding(false));
        Assert.Equal("PreviewChangedOrUnavailable", (await fixture.ApproveAsync("changed-file", preview.AdmissionIdentity!)).Status);
        using var worker = fixture.Worker("changed-file");
        Assert.NotEqual("Succeeded", (await worker.ApplyAsync("changed-file")).Status);
        Assert.Equal("other", fixture.Content);
    }

    [Fact]
    public async Task ChangedProtectedDraftDoesNotAcquireApprovalForChangedPayload()
    {
        using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.PreviewAsync("changed-draft");
        var draftPath = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "control"), "patch-*.json"));
        var draft = JsonNode.Parse(File.ReadAllText(draftPath))!;
        draft["ReplacementBase64"] = Base64("bad");
        File.WriteAllText(draftPath, draft.ToJsonString());
        Assert.Equal("PreviewChangedOrUnavailable", (await fixture.ApproveAsync("changed-draft", preview.AdmissionIdentity!)).Status);
        using var worker = fixture.Worker("changed-draft");
        Assert.NotEqual("Succeeded", (await worker.ApplyAsync("changed-draft")).Status);
        Assert.Equal("old", fixture.Content);
    }

    [Fact]
    public async Task WithdrawnApprovalPreventsMutationAfterProcessReopen()
    {
        using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.PreviewAsync("withdrawn");
        Assert.Equal("Recorded", (await fixture.ApproveAsync("withdrawn", preview.AdmissionIdentity!)).Status);
        using (var manager = fixture.Operator("withdrawn"))
            Assert.Equal("Revoked", (await manager.WithdrawAsync("withdrawn")).Status);
        using var worker = fixture.Worker("withdrawn");
        Assert.NotEqual("Succeeded", (await worker.ApplyAsync("withdrawn")).Status);
        Assert.Equal("old", fixture.Content);
    }

    [Fact]
    public async Task CurrentAuthorityRevocationBlocksAlreadyApprovedPatch()
    {
        using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.PreviewAsync("authority-revoked");
        await fixture.ApproveAsync("authority-revoked", preview.AdmissionIdentity!);
        using (var manager = fixture.Operator())
            Assert.Equal("Applied", (await manager.RevokeAsync()).Status);
        using var worker = fixture.Worker("authority-revoked");
        Assert.NotEqual("Succeeded", (await worker.ApplyAsync("authority-revoked")).Status);
        Assert.Equal("old", fixture.Content);
    }

    [Fact]
    public async Task ExpiredApprovalCannotBeReused()
    {
        using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.PreviewAsync("expired");
        Assert.Equal("Recorded", (await fixture.ApproveAsync("expired", preview.AdmissionIdentity!, 1)).Status);
        await Task.Delay(1300);
        using var worker = fixture.Worker("expired");
        Assert.NotEqual("Succeeded", (await worker.ApplyAsync("expired")).Status);
        Assert.Equal("old", fixture.Content);
    }

    [Fact]
    public async Task WorkerCannotUseManagementCommandsOrAnotherOperation()
    {
        using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.PreviewAsync("worker");
        using var worker = fixture.Worker("worker");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => worker.ApproveAsync("worker", preview.AdmissionIdentity!));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => worker.WithdrawAsync("worker"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => worker.InspectAsync("worker"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => worker.RevokeAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => worker.ApplyAsync("other-operation"));
        Assert.Equal("old", fixture.Content);
    }

    [Fact]
    public async Task RepeatedPreviewDoesNotOverwriteFrozenIntent()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.PreviewAsync("same-operation");
        using var worker = fixture.Worker("same-operation");
        var repeated = await worker.PreviewAsync("same-operation", 0, 3, Base64("bad"));
        Assert.Equal("OperationAlreadyExists", repeated.Status);
        Assert.NotNull(first.AdmissionIdentity);
        Assert.Equal("old", fixture.Content);
    }

    [Fact]
    public async Task ApplicationLeasePreventsCompetingCommandExecution()
    {
        using var fixture = await Fixture.CreateAsync();
        using (var first = fixture.Operator())
            Assert.Throws<IOException>(() => fixture.Operator());
        using var afterRelease = fixture.Operator();
        Assert.Equal("old", fixture.Content);
    }

    [Fact]
    public async Task ApprovalRequiresCompleteOperatorReviewAndRetriesPreserveExpiry()
    {
        using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.PreviewAsync("reviewed-retry");
        using (var manager = fixture.Operator("reviewed-retry"))
        {
            Assert.Equal("OperatorReviewRequired", (await manager.ApproveAsync("reviewed-retry", preview.AdmissionIdentity!)).Status);
            var review = await manager.ReviewAsync("reviewed-retry");
            Assert.Equal("Reviewed", review.Facts.Status);
            Assert.Equal("old", review.BeforeUtf8);
            Assert.Equal("new", review.AfterUtf8);
            Assert.Equal("Recorded", (await manager.ApproveAsync("reviewed-retry", preview.AdmissionIdentity!, 60)).Status);
        }
        var draftPath = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "control"), "patch-*.json"));
        var expiry = JsonNode.Parse(File.ReadAllText(draftPath))!["ApprovalExpiresAt"]!.ToString();
        using (var manager = fixture.Operator("reviewed-retry"))
            Assert.Equal("Replayed", (await manager.ApproveAsync("reviewed-retry", preview.AdmissionIdentity!, 300)).Status);
        Assert.Equal(expiry, JsonNode.Parse(File.ReadAllText(draftPath))!["ApprovalExpiresAt"]!.ToString());
        Assert.Equal("old", fixture.Content);
    }

    [Fact]
    public async Task OversizedHumanReviewNeverEnablesApproval()
    {
        using var fixture = await Fixture.CreateAsync(new string('x', 8193));
        LocalHostResult preview;
        using (var worker = fixture.Worker("large-review"))
            preview = await worker.PreviewAsync("large-review", 0, 8193, Base64("new"));
        Assert.Equal("Prepared", preview.Status);
        using (var manager = fixture.Operator("large-review"))
        {
            var review = await manager.ReviewAsync("large-review");
            Assert.Equal("ReviewTooLarge", review.Facts.Status);
            Assert.Null(review.BeforeUtf8);
            Assert.Null(review.AfterUtf8);
            Assert.Equal("OperatorReviewRequired", (await manager.ApproveAsync("large-review", preview.AdmissionIdentity!)).Status);
        }
        Assert.Equal(new string('x', 8193), fixture.Content);
    }

    [Fact]
    public async Task PublishedBootstrapCanFinalizeAfterLostResponseWithoutRepublishing()
    {
        using var fixture = await Fixture.CreateAsync();
        var configPath = Path.Combine(fixture.Root, "control", "host.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["PublicationApproved"] = true;
        config["BootstrapSeedBase64"] = Base64("old");
        File.WriteAllText(configPath, config.ToJsonString());
        using (var worker = fixture.Worker("blocked-bootstrap"))
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.PreviewAsync("blocked-bootstrap", 0, 3, Base64("new")));
        using (var manager = fixture.Operator())
        {
            var before = await manager.Store.ReadCurrentAsync(manager.Actor, manager.Context);
            Assert.Equal(1, before.Record!.Sequence);
            Assert.Equal("Initialized", (await manager.ActivateAsync()).Status);
            var after = await manager.Store.ReadCurrentAsync(manager.Actor, manager.Context);
            Assert.Equal(1, after.Record!.Sequence);
        }
        Assert.False(JsonNode.Parse(File.ReadAllText(configPath))!["PublicationApproved"]!.GetValue<bool>());
        Assert.Null(JsonNode.Parse(File.ReadAllText(configPath))!["BootstrapSeedBase64"]);
        Assert.Equal("Prepared", (await fixture.PreviewAsync("after-finalize")).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedBootstrapResumesOnlyItsExactSeed(bool seedAlreadyWritten)
    {
        using var fixture = await Fixture.CreateBeforePublicationAsync();
        if (!seedAlreadyWritten) File.Delete(fixture.Target);
        using (var manager = fixture.Operator())
        {
            Assert.Equal("Initialized", (await manager.ActivateAsync()).Status);
            Assert.Equal(1, (await manager.Store.ReadCurrentAsync(manager.Actor, manager.Context)).Record!.Sequence);
        }
        Assert.Equal("old", fixture.Content);
        Assert.Equal("Prepared", (await fixture.PreviewAsync("resumed-bootstrap")).Status);
    }

    [Fact]
    public async Task InterruptedBootstrapNeverOverwritesAChangedSeed()
    {
        using var fixture = await Fixture.CreateBeforePublicationAsync();
        File.WriteAllText(fixture.Target, "partial");
        using var manager = fixture.Operator();
        Assert.Equal("BootstrapSeedChanged", (await manager.ActivateAsync()).Status);
        Assert.Equal(Penghou.Hufu.AuthorityReadStatus.NotFound,
            (await manager.Store.ReadCurrentAsync(manager.Actor, manager.Context)).Status);
        Assert.Equal("partial", fixture.Content);
    }

    [Fact]
    public async Task ExpiredBootstrapApprovalCannotPublishOrSeed()
    {
        using var fixture = await Fixture.CreateBeforePublicationAsync();
        File.Delete(fixture.Target);
        var configPath = Path.Combine(fixture.Root, "control", "host.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["PublicationApprovalExpiry"] = DateTimeOffset.UtcNow.AddSeconds(-1);
        File.WriteAllText(configPath, config.ToJsonString());
        using var manager = fixture.Operator();
        Assert.Equal("ActivationDeniedOrUnavailable", (await manager.ActivateAsync()).Status);
        Assert.False(File.Exists(fixture.Target));
        Assert.Equal(Penghou.Hufu.AuthorityReadStatus.NotFound,
            (await manager.Store.ReadCurrentAsync(manager.Actor, manager.Context)).Status);
    }

    [Fact]
    public async Task UnknownOrOversizedProtectedConfigurationFailsClosed()
    {
        using var fixture = await Fixture.CreateAsync();
        var configPath = Path.Combine(fixture.Root, "control", "host.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["UnknownAuthority"] = true;
        File.WriteAllText(configPath, config.ToJsonString());
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => fixture.Operator());
        File.WriteAllText(configPath, new string('x', 2_097_153));
        Assert.Throws<InvalidDataException>(() => fixture.Operator());
        Assert.Equal("old", fixture.Content);
    }

    [Theory]
    [InlineData("../file.txt")]
    [InlineData("docs/FILE.txt")]
    [InlineData("docs/con.txt")]
    [InlineData("c:/file.txt")]
    public void UnsafeTargetsAreRejected(string target) => Assert.False(LocalHostApplication.ValidTarget(target));

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private sealed class Fixture(string root) : IDisposable
    {
        public string Root => root;
        public string Target => Path.Combine(root, "workspace", "docs", "file.txt");
        public string Content => File.ReadAllText(Target);
        public static async Task<Fixture> CreateAsync(string initial = "old")
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Hufu.LocalHost.Qualification-" + Guid.NewGuid().ToString("N"));
            var fixture = new Fixture(root);
            try
            {
                Assert.Equal("Initialized", (await LocalHostApplication.InitializeAsync(root, "docs/file.txt", Base64(initial))).Status);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public static async Task<Fixture> CreateBeforePublicationAsync()
        {
            var fixture = await CreateAsync();
            // Reconstruct only this disposable fixture's pre-publication crash
            // checkpoint. No host command deletes a database or resets authority.
            var control = Path.Combine(fixture.Root, "control");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(Path.Combine(control, "authority.db" + suffix));
            var configPath = Path.Combine(control, "host.json");
            var config = JsonNode.Parse(File.ReadAllText(configPath))!;
            config["PublicationApproved"] = true;
            config["BootstrapSeedBase64"] = Base64("old");
            File.WriteAllText(configPath, config.ToJsonString());
            return fixture;
        }
        public LocalHostApplication Worker(string operation) => LocalHostApplication.Open(root, LocalHostMode.Worker, operation);
        public LocalHostApplication Operator(string? operation = null) => LocalHostApplication.Open(root, LocalHostMode.Operator, operation);
        public async Task<LocalHostResult> PreviewAsync(string operation)
        { using var worker = Worker(operation); return await worker.PreviewAsync(operation, 0, 3, Base64("new")); }
        public async Task<LocalHostResult> ApproveAsync(string operation, string identity, int seconds = 300)
        {
            using var manager = Operator(operation);
            await manager.ReviewAsync(operation);
            return await manager.ApproveAsync(operation, identity, seconds);
        }
        public void Dispose()
        {
            var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var full = Path.GetFullPath(root);
            var name = Path.GetFileName(full);
            if (Path.GetDirectoryName(full) != profile || !name.StartsWith("Hufu.LocalHost.Qualification-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(name["Hufu.LocalHost.Qualification-".Length..], "N", out _))
                throw new InvalidOperationException("Refusing cleanup outside the generated qualification scope.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
