using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

public class BatchPolicyTests
{
    [Fact]
    public void MixedBatch_ModelWrite_and_UiAction_is_rejected()
    {
        var kinds = new[] { ExecutionKind.ModelWrite, ExecutionKind.UiAction };
        var result = BatchPolicy.ValidateBatchKinds(kinds);

        Assert.NotNull(result);
        Assert.Equal(false, result!["ok"]?.GetValue<bool>());
        Assert.Equal("bad_request",
            (result["error"] as System.Text.Json.Nodes.JsonObject)?["code"]?.GetValue<string>());
    }

    [Fact]
    public void PureModelWrite_batch_is_allowed()
    {
        var kinds = new[] { ExecutionKind.ModelWrite, ExecutionKind.ModelWrite };
        var result = BatchPolicy.ValidateBatchKinds(kinds);
        Assert.Null(result);
    }

    [Fact]
    public void PureUiAction_batch_is_allowed()
    {
        var kinds = new[] { ExecutionKind.UiAction, ExecutionKind.UiAction };
        var result = BatchPolicy.ValidateBatchKinds(kinds);
        Assert.Null(result);
    }

    [Fact]
    public void PureReadOnly_batch_is_allowed()
    {
        var kinds = new[] { ExecutionKind.ReadOnly, ExecutionKind.ReadOnly };
        var result = BatchPolicy.ValidateBatchKinds(kinds);
        Assert.Null(result);
    }

    [Fact]
    public void Mixed_ReadOnly_and_UiAction_is_allowed()
    {
        // ReadOnly + UiAction is fine — neither needs a transaction
        var kinds = new[] { ExecutionKind.ReadOnly, ExecutionKind.UiAction };
        var result = BatchPolicy.ValidateBatchKinds(kinds);
        Assert.Null(result);
    }

    [Fact]
    public void Mixed_ReadOnly_and_ModelWrite_is_allowed()
    {
        // ReadOnly + ModelWrite is fine — ModelWrite opens a transaction that
        // read-only steps safely share
        var kinds = new[] { ExecutionKind.ReadOnly, ExecutionKind.ModelWrite };
        var result = BatchPolicy.ValidateBatchKinds(kinds);
        Assert.Null(result);
    }

    [Fact]
    public void Empty_batch_is_allowed()
    {
        var result = BatchPolicy.ValidateBatchKinds(
            System.Array.Empty<ExecutionKind>());
        Assert.Null(result);
    }
}
