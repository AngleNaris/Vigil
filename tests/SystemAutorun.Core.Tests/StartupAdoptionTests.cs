using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class StartupAdoptionTests
{
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static StartupEntry Entry => new("HKLM:fixture", "HKLM", "fixture", "unparsed native command",
        @"\\unreachable.invalid\never-probe\fixture.exe", "--native", null, @"HKLM\Run", null,
        false, null, null, true, false, null);
    private static AddTaskRequest TaskForm => new("adopted-fixture", AutorunTaskType.WslBoot,
        Distro: "never-run-fixture", HealthType: HealthCheckType.WslRunning);

    [Fact]
    public void ReviewedReadOnlyMachineEntryDoesNotRequireNativeWritePermissionOrTargetIo()
    {
        var query = new StartupAdoptionRequest(Entry, TaskForm, true).Validate(Entry.Id);
        Assert.Equal(new StartupListQuery("machine", "registry", "active", "fixture", 200), query);
        Assert.True(AgentProtocol.IsConfigurationMutation("startup-adopt"));
        Assert.True(AgentProtocol.IsMutation("STARTUP-ADOPT"));
    }

    [Theory]
    [InlineData("HKCU", "user", "registry")] [InlineData("HKLM", "machine", "registry")]
    [InlineData("HKLM-WOW64", "machine", "registry")] [InlineData("Startup", "user", "shortcut")]
    [InlineData("Common Startup", "machine", "shortcut")]
    public void EveryFixedSourceDerivesItsOwnBoundedReadScope(string source, string scope, string kind)
    {
        var entry = Entry with { Source = source };
        var query = new StartupAdoptionRequest(entry, TaskForm, true).Validate(entry.Id);
        Assert.Equal(scope, query.Scope); Assert.Equal(kind, query.Kind);
        Assert.Equal("active", query.State); Assert.Equal(200, query.Limit);
    }

    [Fact]
    public void ExplicitConfirmationIsRequiredEvenForAnOtherwiseValidForm()
    {
        var error = Assert.Throws<AgentCommandException>(() => new StartupAdoptionRequest(Entry, TaskForm).Validate(Entry.Id));
        Assert.Equal("authorization_required", error.Code);
    }

    [Theory]
    [InlineData("null-entry")] [InlineData("null-task")] [InlineData("id")]
    [InlineData("disabled")] [InlineData("managed")] [InlineData("error")]
    [InlineData("target")] [InlineData("source")] [InlineData("name")]
    public void InvalidObservationCannotAuthorizeEvenAConfigurationOnlyWrite(string defect)
    {
        var entry = defect switch
        {
            "null-entry" => null!, "disabled" => Entry with { IsDisabled = true },
            "managed" => Entry with { ManagedTask = "existing" }, "error" => Entry with { Error = "unreadable" },
            "target" => Entry with { Target = " " }, "source" => Entry with { Source = "arbitrary" },
            "name" => Entry with { Name = new string('x', 1025) }, _ => Entry,
        };
        var request = new StartupAdoptionRequest(entry, defect == "null-task" ? null! : TaskForm, true);
        Assert.ThrowsAny<ArgumentException>(() => request.Validate(defect == "id" ? "another" : Entry.Id));
    }

    [Fact]
    public void LongNamesDoNotEscapeTheBoundedQueryContract()
    {
        var entry = Entry with { Name = new string('x', 300) };
        Assert.Null(new StartupAdoptionRequest(entry, TaskForm, true).Validate(entry.Id).Search);
    }

    [Theory]
    [InlineData("nativeWritePerformed")] [InlineData("executionRequested")]
    [InlineData("native-true")] [InlineData("execution-true")] [InlineData("entry")]
    [InlineData("task")] [InlineData("revision")] [InlineData("operation")]
    [InlineData("failed")] [InlineData("action")]
    public void BadPostCommitReceiptsAreOutcomeUnknownNotPermissionToRepeat(string defect)
    {
        var operation = AgentProtocol.NewOperationId();
        var node = JsonSerializer.SerializeToNode(new WorkspaceMutation<StartupAdoptionResult>(
            new(Entry.Id, new(true, "add", TaskForm.Name, "owned-backup"), false, false), Revision, DateTimeOffset.UtcNow), AgentProtocol.JsonOptions)!;
        var value = node["value"]!;
        if (defect is "nativeWritePerformed" or "executionRequested") { value.AsObject().Remove(defect); }
        else if (defect == "native-true") { value["nativeWritePerformed"] = true; }
        else if (defect == "execution-true") { value["executionRequested"] = true; }
        else if (defect == "entry") { value["entryId"] = "another"; }
        else if (defect == "task") { value["configuration"]!["taskName"] = "another"; }
        else if (defect == "failed") { value["configuration"]!["succeeded"] = false; }
        else if (defect == "action") { value["configuration"]!["action"] = "remove"; }
        var response = new AgentResponse("request", true, JsonSerializer.SerializeToElement(node),
            Revision: defect == "revision" ? new string('b', 64) : Revision,
            OperationId: defect == "operation" ? AgentProtocol.NewOperationId() : operation);
        var error = Assert.Throws<AgentOutcomeUnknownException>(() =>
            AgentStartupClient.ReadAdoptionReceipt(response, Entry.Id, TaskForm.Name, operation));
        Assert.Equal(operation, error.OperationId);
    }

    [Theory]
    [InlineData("startup-adopt", "startup_entry_changed", true)]
    [InlineData("STARTUP-ADOPT", "startup_inventory_incomplete", true)]
    [InlineData("startup-adopt", "startup_preflight_unavailable", true)]
    [InlineData("startup-adopt", "startup_preflight_permission_denied", true)]
    [InlineData("task-add", "startup_entry_changed", false)]
    [InlineData("task-add", "startup_inventory_incomplete", false)]
    [InlineData("task-add", "startup_preflight_unavailable", false)]
    [InlineData("task-add", "startup_preflight_permission_denied", false)]
    [InlineData("startup-adopt", "io_error", false)]
    [InlineData("startup-adopt", "permission_denied", false)]
    public async Task OnlyThisCommandsProvenPreflightFailuresAreTerminal(string command, string code, bool terminal)
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-adoption-journal-tests-").FullName;
        try
        {
            var journal = new OperationJournal(new ProjectPaths(root)); var operation = AgentProtocol.NewOperationId();
            var request = new AgentRequest("request", command, OperationId: operation);
            var response = await journal.RunAsync(request, _ => Task.FromResult(new AgentResponse("request", false, Error: "fixture", ErrorCode: code)));
            Assert.Equal(terminal ? code : "outcome_unknown", response.ErrorCode);
            Assert.Equal(terminal ? OperationState.Completed : OperationState.Unknown, (await journal.GetAsync(operation))!.State);
            var replay = await new OperationJournal(new ProjectPaths(root)).RunAsync(request, _ => throw new InvalidOperationException("Do not repeat."));
            Assert.True(replay.Replayed); Assert.Equal(response.ErrorCode, replay.ErrorCode);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("SystemAutorun-adoption-journal-tests-", Path.GetFileName(resolved), StringComparison.Ordinal);
            Directory.Delete(resolved, true);
        }
    }

    [Fact]
    public void ValidReplayedReceiptKeepsItsOriginalObservationTime()
    {
        var operation = AgentProtocol.NewOperationId(); var time = DateTimeOffset.UtcNow.AddDays(-1);
        var mutation = new WorkspaceMutation<StartupAdoptionResult>(new(Entry.Id,
            new(true, "add", TaskForm.Name, "owned-backup"), false, false), Revision, time);
        var response = new AgentResponse("request", true, mutation, Revision: Revision, OperationId: operation, Replayed: true);
        Assert.Equal(time, AgentStartupClient.ReadAdoptionReceipt(response, Entry.Id, TaskForm.Name, operation).CapturedAt);
    }
}
