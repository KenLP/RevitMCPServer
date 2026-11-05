using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPAddin.Commands;

namespace RevitMCPAddin;

/// <summary>
/// Marshals incoming command requests onto the Revit main UI thread, and owns
/// the transaction lifecycle.
///
/// HTTP requests arrive on background threads inside <see cref="Server.McpHttpServer"/>.
/// They cannot touch the Revit API directly — Revit API calls must run inside an
/// ExternalEvent handler on the main thread.  We enqueue a <see cref="PendingRequest"/>
/// (carrying a <see cref="TaskCompletionSource{TResult}"/>), raise the ExternalEvent,
/// and the HTTP handler awaits the TCS for the result.
///
/// Transaction policy:
///   - For a single read-only command  → no transaction at all.
///   - For a single write command       → opens one Transaction named "MCP: &lt;cmd&gt;".
///   - For a batch                      → opens ONE Transaction named "MCP: Batch (n ops)"
///                                        and runs every sub-command inside it.  If any
///                                        sub-command throws and stopOnError is true the
///                                        transaction is rolled back.
/// </summary>
public sealed class RevitMCPExternalEventHandler : IExternalEventHandler
{
    private readonly CommandRegistry _registry;
    private readonly ConcurrentQueue<PendingRequest> _queue = new();
    private ExternalEvent? _externalEvent;

    // Write gates (both optional): an audit sink and the read-back failure policy.
    private IAuditSink? _audit;
    private WriteGateOptions _gates = new();
    // preview_required mode only: outstanding single-use approval tokens.
    private ApprovalStore? _approvals;
    // Application-level dialog policy (null = none configured).
    private DialogMonitor? _dialogs;

    public RevitMCPExternalEventHandler(CommandRegistry registry)
    {
        _registry = registry;
    }

    public void AttachExternalEvent(ExternalEvent externalEvent)
    {
        _externalEvent = externalEvent;
    }

    public CommandRegistry Registry => _registry;

    /// <summary>
    /// Installs the audit sink (null = no audit) and the read-back policy. Called once by the host
    /// at start-up, before the listener accepts requests.
    /// </summary>
    public void ConfigureWriteGates(IAuditSink? audit, WriteGateOptions? options)
    {
        _audit = audit;
        _gates = options ?? new WriteGateOptions();
        _approvals = _gates.Mutation == MutationMode.PreviewRequired ? new ApprovalStore(_gates.ApprovalTtl) : null;
    }

    /// <summary>
    /// Hands commands the application-level dialog monitor (null = no policy file). Called once by
    /// the host at start-up, before the listener accepts requests.
    /// </summary>
    public void ConfigureDialogs(DialogMonitor? dialogs) => _dialogs = dialogs;

    /// <summary>The configured mutation mode (reported on <c>/health</c>).</summary>
    public MutationMode MutationMode => _gates.Mutation;

    /// <summary>Enqueue a single command call.</summary>
    public Task<JsonObject> EnqueueAsync(string commandName, JsonObject? parameters, bool dryRun = false,
        RequestMeta? meta = null)
    {
        var pending = new PendingRequest(
            kind: RequestKind.Single,
            commandName: commandName,
            parameters: parameters ?? new JsonObject(),
            steps: null,
            stopOnError: false,
            dryRun: dryRun,
            meta: meta);
        Enqueue(pending);
        return pending.Completion.Task;
    }

    /// <summary>Enqueue a batch (array of sub-commands inside one transaction).</summary>
    public Task<JsonObject> EnqueueBatchAsync(IList<BatchStep> steps, bool stopOnError, bool dryRun = false,
        RequestMeta? meta = null)
    {
        var pending = new PendingRequest(
            kind: RequestKind.Batch,
            commandName: "batch",
            parameters: new JsonObject(),
            steps: steps,
            stopOnError: stopOnError,
            dryRun: dryRun,
            meta: meta);
        Enqueue(pending);
        return pending.Completion.Task;
    }

    private void Enqueue(PendingRequest req)
    {
        _queue.Enqueue(req);
        // Raise() is documented as safe to call from any thread.
        _externalEvent?.Raise();
    }

    public void Execute(UIApplication app)
    {
        // Drain the whole queue in one Revit-thread tick.  Each top-level
        // request runs in its own try/catch so one bad request can't poison
        // the others sharing this tick.
        while (_queue.TryDequeue(out var req))
        {
            var sw = Stopwatch.StartNew();
            var stepMs = new List<long>();
            JsonObject result;
            JsonObject? approval = null;
            try
            {
                // preview_required: bind the request BEFORE it runs (a command may touch its params).
                var binding = _approvals is not null && NeedsApproval(req) ? Binding(app, req) : null;
                var refused = binding is not null && !req.DryRun ? CheckApproval(binding, req, out approval) : null;
                result = refused ?? req.Kind switch
                {
                    RequestKind.Single => RunSingle(app, req.CommandName, req.Parameters, req.DryRun),
                    RequestKind.Batch  => RunBatch(app, req.Steps!, req.StopOnError, req.DryRun, stepMs),
                    _ => JsonResult.Error("internal", "Unknown request kind."),
                };
                if (binding is not null && req.DryRun) approval = IssueApproval(binding, result);
            }
            catch (Exception ex)
            {
                result = JsonResult.Error("command_failed", ex.Message, ex.GetType().FullName);
            }
            sw.Stop();

            // Audit after the result is final; it can never change or fail the result.
            WriteAudit(app, req, result, sw.ElapsedMilliseconds, stepMs, approval);
            req.Completion.SetResult(result);
        }
    }

    private JsonObject RunSingle(UIApplication app, string commandName, JsonObject parameters, bool dryRun)
    {
        if (!_registry.TryGet(commandName, out var command) || command is null)
            return JsonResult.Error("unknown_command",
                $"No command registered for '{commandName}'.");

        var ctx = BuildContext(app, parameters, dryRun);

        // Read-only and UI-action commands run WITHOUT a model transaction.
        if (command.Execution is ExecutionKind.ReadOnly or ExecutionKind.UiAction)
        {
            // A UI action cannot be previewed by rollback — there's no model
            // change to undo and the UI effect can't be reverted. In dry-run
            // we therefore report a no-op instead of mutating UI state.
            if (dryRun && command.Execution == ExecutionKind.UiAction)
            {
                return JsonResult.Success(new JsonObject
                {
                    ["dryRun"] = true,
                    ["committed"] = false,
                    ["skipped"] = true,
                    ["changeSummary"] =
                        $"Dry-run: UI action '{commandName}' not executed (UI changes cannot be rolled back).",
                });
            }

            try
            {
                var data = command.Execute(ctx);
                var result = JsonResult.Success(data);
                if (dryRun) result["dryRun"] = true;
                return result;
            }
            catch (RevitCommandException ex)
            {
                return JsonResult.Error(ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                return JsonResult.Error("command_failed", ex.Message, ex.GetType().FullName);
            }
        }

        // Model-write commands always need a document + a transaction.
        var doc = ctx.RequireDoc();
        using var tx = new Transaction(doc, $"MCP: {commandName}");
        if (command.SuppressWarningsOnCommit) SuppressCommitWarnings(tx);
        try
        {
            tx.Start();
            var data = command.Execute(ctx);

            if (dryRun)
            {
                // Roll back — model is unchanged, but we still return the result
                // so the caller can preview what *would* have happened.
                if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                var result = JsonResult.Success(data);
                result["dryRun"] = true;
                result["committed"] = false;
                return result;
            }

            var status = TransactionStatus.Committed;
            if (tx.HasStarted() && !tx.HasEnded())
                status = tx.Commit();

            var envelope = JsonResult.Success(data);
            if (data is not JsonObject obj) return envelope;

            // Read-back after the commit (never on dry-run): the command's own Verify, or the
            // generic existence check of data.affected. A commit Revit rolled back is a failure no
            // matter what the command reports.
            var verify = status == TransactionStatus.Committed
                ? RunVerify(command, ctx, obj, doc)
                : VerifyResult.Fail($"Revit did not commit the transaction (status {status}).");
            return WriteGate.ApplyVerify(envelope, obj, verify, _gates.VerifyFailure);
        }
        catch (RevitCommandException ex)
        {
            try { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); } catch { }
            return JsonResult.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            try { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); } catch { }
            return JsonResult.Error("command_failed", ex.Message, ex.GetType().FullName);
        }
    }

    private JsonObject RunBatch(UIApplication app, IList<BatchStep> steps, bool stopOnError, bool dryRun,
        List<long>? stepMs = null)
    {
        if (steps.Count == 0)
            return JsonResult.Error("bad_request", "Batch must contain at least one step.");

        // Resolve all handlers up-front and verify they exist.
        var resolved = new List<(BatchStep step, IRevitCommand cmd)>(steps.Count);
        var anyWrite = false;
        foreach (var step in steps)
        {
            if (!_registry.TryGet(step.CommandName, out var cmd) || cmd is null)
                return JsonResult.Error("unknown_command",
                    $"No command registered for '{step.CommandName}'.");
            resolved.Add((step, cmd));
            if (cmd.Execution == ExecutionKind.ModelWrite) anyWrite = true;
        }

        // Mixed batches are rejected: UI effects cannot be rolled back alongside
        // model changes, leading to unpredictable state on failure or dry-run.
        var mixedError = BatchPolicy.ValidateBatchKinds(resolved.Select(r => r.cmd.Execution));
        if (mixedError is not null) return mixedError;

        // No model writes (only read-only / UI actions): no transaction required.
        if (!anyWrite)
        {
            var roResults = new JsonArray();
            for (var i = 0; i < resolved.Count; i++)
            {
                var (step, cmd) = resolved[i];

                // Dry-run skips UI actions — they cannot be rolled back.
                if (dryRun && cmd.Execution == ExecutionKind.UiAction)
                {
                    var skipEnv = JsonResult.Success(new JsonObject
                    {
                        ["dryRun"] = true,
                        ["committed"] = false,
                        ["skipped"] = true,
                        ["changeSummary"] =
                            $"Dry-run: UI action '{step.CommandName}' not executed.",
                    });
                    stepMs?.Add(0);
                    skipEnv["index"] = i;
                    skipEnv["command"] = step.CommandName;
                    roResults.Add(skipEnv);
                    continue;
                }

                var ctx = BuildContext(app, step.Parameters, dryRun);
                var swRo = Stopwatch.StartNew();
                var r = RunStepCaptured(cmd, ctx);
                stepMs?.Add(swRo.ElapsedMilliseconds);
                r["index"] = i;
                r["command"] = step.CommandName;
                roResults.Add(r);
            }
            var roEnvelope = JsonResult.Success(new JsonObject
            {
                ["count"] = resolved.Count,
                ["results"] = roResults,
            });
            if (dryRun) roEnvelope["dryRun"] = true;
            return roEnvelope;
        }

        // Mixed / write batch: single transaction across the lot.
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("Batch requires an active Revit document.");
        var results = new JsonArray();
        var hadFailure = false;
        // Successful steps whose data can carry a read-back: (index, command, context, data).
        var verifiable = new List<(int Index, string Name, IRevitCommand Cmd, CommandContext Ctx, JsonObject Data)>();

        using var tx = new Transaction(doc, $"MCP: Batch ({resolved.Count} ops)");
        // One opted-in step is enough: the batch commits as ONE transaction, so a single step's
        // commit-time warning dialog would deadlock the whole batch exactly like a single call.
        if (resolved.Any(r => r.cmd.SuppressWarningsOnCommit)) SuppressCommitWarnings(tx);
        tx.Start();
        try
        {
            for (var i = 0; i < resolved.Count; i++)
            {
                var (step, cmd) = resolved[i];
                var ctx = BuildContext(app, step.Parameters, dryRun);
                var swStep = Stopwatch.StartNew();

                JsonObject stepEnvelope;
                try
                {
                    var data = cmd.Execute(ctx);
                    stepMs?.Add(swStep.ElapsedMilliseconds);
                    stepEnvelope = JsonResult.Success(data);
                    if (!dryRun && cmd.Execution == ExecutionKind.ModelWrite && data is JsonObject vdata)
                        verifiable.Add((i, step.CommandName, cmd, ctx, vdata));
                }
                catch (RevitCommandException ex)
                {
                    stepMs?.Add(swStep.ElapsedMilliseconds);
                    hadFailure = true;
                    stepEnvelope = JsonResult.Error(ex.Code, ex.Message);
                    stepEnvelope["index"] = i;
                    stepEnvelope["command"] = step.CommandName;
                    results.Add(stepEnvelope);

                    if (stopOnError)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return new JsonObject
                        {
                            ["ok"] = false,
                            ["error"] = new JsonObject
                            {
                                ["code"] = "batch_aborted",
                                ["message"] = $"Batch aborted at step {i} ('{step.CommandName}'): {ex.Message}",
                            },
                            ["committed"] = false,
                            ["results"] = results,
                        };
                    }
                    continue;
                }
                catch (Exception ex)
                {
                    stepMs?.Add(swStep.ElapsedMilliseconds);
                    hadFailure = true;
                    stepEnvelope = JsonResult.Error("step_failed", ex.Message, ex.GetType().FullName);
                    stepEnvelope["index"] = i;
                    stepEnvelope["command"] = step.CommandName;
                    results.Add(stepEnvelope);

                    if (stopOnError)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return new JsonObject
                        {
                            ["ok"] = false,
                            ["error"] = new JsonObject
                            {
                                ["code"] = "batch_aborted",
                                ["message"] = $"Batch aborted at step {i} ('{step.CommandName}'): {ex.Message}",
                            },
                            ["committed"] = false,
                            ["results"] = results,
                        };
                    }
                    continue;
                }
                stepEnvelope["index"] = i;
                stepEnvelope["command"] = step.CommandName;
                results.Add(stepEnvelope);
            }

            if (tx.HasStarted() && !tx.HasEnded())
            {
                if (dryRun)
                    tx.RollBack();
                else if (_gates.VerifyFailure == VerifyFailureMode.Error && verifiable.Count > 0)
                {
                    // Strict: read back BEFORE committing, inside the same transaction, so a failure
                    // can still roll the whole batch back — one undo step, all or nothing.
                    doc.Regenerate();
                    var raw = verifiable.Select(v => RunVerify(v.Cmd, v.Ctx, v.Data, doc)).ToList();
                    var final = WriteGate.Supersede(verifiable.Select((v, k) => (v.Index, v.Data, raw[k])).ToList());
                    var checks = verifiable.Select((v, k) => (v, r: final[k])).ToList();
                    foreach (var (v, r) in checks) v.Data["verify"] = r.ToJson();
                    if (WriteGate.BatchMustRollBack(checks.Select(c => c.r), _gates.VerifyFailure))
                    {
                        tx.RollBack();
                        return WriteGate.BatchVerifyFailed(results,
                            checks.Where(c => c.r.IsFailed).Select(c => (c.v.Index, c.v.Name, c.r)));
                    }
                    tx.Commit();
                }
                else
                {
                    var status = tx.Commit();
                    // Report mode: read back AFTER the commit, so commit-time changes are visible.
                    var raw = verifiable.Select(v => status == TransactionStatus.Committed
                        ? RunVerify(v.Cmd, v.Ctx, v.Data, doc)
                        : VerifyResult.Fail($"Revit did not commit the transaction (status {status}).")).ToList();
                    var final = WriteGate.Supersede(verifiable.Select((v, k) => (v.Index, v.Data, raw[k])).ToList());
                    for (var k = 0; k < verifiable.Count; k++) verifiable[k].Data["verify"] = final[k].ToJson();
                }
            }
        }
        catch
        {
            try { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); } catch { }
            throw;
        }

        var batchResult = new JsonObject
        {
            ["ok"] = true,
            ["committed"] = !dryRun,
            ["count"] = resolved.Count,
            ["hadFailures"] = hadFailure,
            ["results"] = results,
        };
        if (dryRun) batchResult["dryRun"] = true;
        return batchResult;
    }

    /// <summary>A request needs approval when it can change the model: a write command, or a batch with one.</summary>
    private bool NeedsApproval(PendingRequest req) =>
        req.Kind == RequestKind.Single
            ? IsModelWrite(req.CommandName)
            : req.Steps?.Any(st => IsModelWrite(st.CommandName)) == true;

    private bool IsModelWrite(string name) =>
        _registry.TryGet(name, out var cmd) && cmd?.Execution == ExecutionKind.ModelWrite;

    private static string Binding(UIApplication app, PendingRequest req)
    {
        var docKey = DocKey(app);
        return req.Kind == RequestKind.Single
            ? ApprovalBinding.ForCommand(req.CommandName, req.Parameters, docKey)
            : ApprovalBinding.ForBatch(req.Steps!.Select(st => (st.CommandName, (JsonObject?)st.Parameters)),
                req.StopOnError, docKey);
    }

    /// <summary>Consumes the request's token; returns the refusal envelope, or null when approved.</summary>
    private JsonObject? CheckApproval(string binding, PendingRequest req, out JsonObject? note)
    {
        var token = req.Meta?.ApprovalToken;
        var check = _approvals!.Consume(token, binding);
        note = new JsonObject
        {
            ["state"] = check.Ok ? "consumed" : check.Code == ApprovalCheck.Required ? "required" : "mismatch",
            ["token"] = string.IsNullOrWhiteSpace(token) ? null : ApprovalStore.Fingerprint(token!),
        };
        return check.Ok ? null : JsonResult.Error(check.Code!, check.Message!);
    }

    /// <summary>
    /// After a successful dry-run of a write: mint the token that approves exactly this request.
    /// Single command → <c>data.approvalToken</c>; batch → beside <c>results</c> at the top level,
    /// where every other batch field lives.
    /// </summary>
    private JsonObject? IssueApproval(string binding, JsonObject result)
    {
        if (result["ok"]?.GetValue<bool>() != true) return null;
        var (token, expires) = _approvals!.Issue(binding);
        var target = result["data"] as JsonObject ?? result;
        target["approvalToken"] = token;
        target["approvalExpiresAt"] = expires.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        return new JsonObject { ["state"] = "issued", ["token"] = ApprovalStore.Fingerprint(token) };
    }

    /// <summary>Document identity for bindings and the audit: path, or title for an unsaved model.</summary>
    private static string? DocKey(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            return doc is null ? null : (string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName);
        }
        catch { return null; }
    }

    /// <summary>The command's own read-back if it has one, else the generic <c>affected</c> check.</summary>
    private static VerifyResult RunVerify(IRevitCommand cmd, CommandContext ctx, JsonObject data, Document doc)
    {
        try
        {
            return cmd is IVerifiableCommand v ? v.Verify(ctx, data) : ReadBack.AffectedInModel(doc, data);
        }
        catch (Exception ex)
        {
            // A broken check is not evidence the write failed: say so instead of guessing.
            return VerifyResult.Skip($"read-back could not run: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void WriteAudit(UIApplication app, PendingRequest req, JsonObject result, long totalMs, List<long> stepMs,
        JsonObject? approval)
    {
        var sink = _audit;
        if (sink is null) return;
        try
        {
            var docKey = DocKey(app);
            string? revit = null;
            try { revit = app.Application.VersionNumber; } catch { }
            var meta = req.Meta ?? RequestMeta.None;
            var now = DateTime.UtcNow;
            var requestId = meta.RequestId ?? Guid.NewGuid().ToString("N").Substring(0, 12);

            if (req.Kind == RequestKind.Single)
            {
                _registry.TryGet(req.CommandName, out var cmd);
                var kind = cmd?.Execution;
                if (kind == ExecutionKind.ReadOnly && !sink.IncludeReads) return;
                sink.Write(Record(now, requestId, revit, docKey, req.CommandName, kind?.ToString(), null, null,
                    req.DryRun, result, totalMs, req.Parameters, meta, approval));
                return;
            }

            // Batch: one line per step + one summary line, all sharing batchId.
            var steps = req.Steps ?? new List<BatchStep>();
            var kinds = steps.Select(st => _registry.TryGet(st.CommandName, out var c) ? c?.Execution : null).ToList();
            var anyWrite = kinds.Any(k => k == ExecutionKind.ModelWrite);
            var anyUi = kinds.Any(k => k == ExecutionKind.UiAction);
            if (!anyWrite && !anyUi && kinds.All(k => k is not null) && !sink.IncludeReads) return;

            var batchId = requestId;
            var stepResults = result["results"] as JsonArray ?? (result["data"] as JsonObject)?["results"] as JsonArray;
            if (stepResults is not null)
            {
                foreach (var node in stepResults)
                {
                    if (node is not JsonObject se) continue;
                    var i = se["index"]?.GetValue<int>() ?? -1;
                    if (i < 0 || i >= steps.Count) continue;
                    var name = steps[i].CommandName;
                    sink.Write(Record(now, $"{batchId}/{i}", revit, docKey, name, kinds[i]?.ToString(), batchId, i,
                        req.DryRun, se, i < stepMs.Count ? stepMs[i] : 0, steps[i].Parameters, meta));
                }
            }
            var batchKind = anyWrite ? ExecutionKind.ModelWrite : anyUi ? ExecutionKind.UiAction : ExecutionKind.ReadOnly;
            var stepsNode = new JsonArray(steps.Select(st => (JsonNode?)new JsonObject
            {
                ["command"] = st.CommandName,
                ["params"] = st.Parameters.DeepClone(),
            }).ToArray());
            sink.Write(Record(now, batchId, revit, docKey, "batch", batchKind.ToString(), batchId, null,
                req.DryRun, result, totalMs, new JsonObject { ["steps"] = stepsNode, ["stopOnError"] = req.StopOnError }, meta,
                approval));
        }
        catch
        {
            // The sink reports its own failures; building a record must never break a command.
        }
    }

    private static AuditRecord Record(DateTime ts, string id, string? revit, string? docKey, string command,
        string? kind, string? batchId, int? step, bool dryRun, JsonObject envelope, long ms, JsonNode? parameters,
        RequestMeta meta, JsonObject? approval = null)
    {
        var ok = envelope["ok"]?.GetValue<bool>() ?? false;
        var data = envelope["data"] as JsonObject;
        return new AuditRecord
        {
            TimestampUtc = ts,
            Id = id,
            RevitVersion = revit,
            DocumentKey = docKey,
            Command = command,
            Kind = kind,
            BatchId = batchId,
            Step = step,
            DryRun = dryRun,
            Ok = ok,
            ErrorCode = (envelope["error"] as JsonObject)?["code"]?.GetValue<string>(),
            DurationMs = ms,
            Parameters = parameters?.DeepClone(),
            Affected = data?["affected"]?.DeepClone(),
            Verify = data?["verify"]?.DeepClone(),
            Client = meta.Client,
            Trace = meta.Trace,
            Approval = approval?.DeepClone(),
        };
    }

    private static JsonObject RunStepCaptured(IRevitCommand cmd, CommandContext ctx)
    {
        try
        {
            var data = cmd.Execute(ctx);
            return JsonResult.Success(data);
        }
        catch (RevitCommandException ex)
        {
            return JsonResult.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return JsonResult.Error("step_failed", ex.Message, ex.GetType().FullName);
        }
    }

    private CommandContext BuildContext(UIApplication app, JsonObject parameters, bool dryRun = false) => new()
    {
        App = app,
        Doc = app.ActiveUIDocument?.Document,
        Parameters = parameters,
        DryRun = dryRun,
        Dialogs = _dialogs,
    };

    public string GetName() => "RevitMCPExternalEventHandler";

    /// <summary>
    /// Installs a failures preprocessor that deletes WARNINGS at commit so no modal warning dialog
    /// can fire on the UI thread — which, for a headless HTTP caller, deadlocks the entire add-in
    /// until a human clicks the box. Errors are left alone: they still fail the transaction and
    /// surface through the normal error envelope. Only applied for commands that opted in via
    /// <see cref="IRevitCommand.SuppressWarningsOnCommit"/>; those commands are responsible for
    /// reporting the suppressed condition in their own response.
    /// </summary>
    private static void SuppressCommitWarnings(Transaction tx)
    {
        var options = tx.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(CommitWarningSwallower.Instance);
        tx.SetFailureHandlingOptions(options);
    }

    private sealed class CommitWarningSwallower : IFailuresPreprocessor
    {
        public static readonly CommitWarningSwallower Instance = new();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            accessor.DeleteAllWarnings();
            return FailureProcessingResult.Continue;
        }
    }

    private enum RequestKind { Single, Batch }

    private sealed class PendingRequest
    {
        public RequestKind Kind { get; }
        public string CommandName { get; }
        public JsonObject Parameters { get; }
        public IList<BatchStep>? Steps { get; }
        public bool StopOnError { get; }
        public bool DryRun { get; }
        public RequestMeta? Meta { get; }
        public TaskCompletionSource<JsonObject> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingRequest(
            RequestKind kind,
            string commandName,
            JsonObject parameters,
            IList<BatchStep>? steps,
            bool stopOnError,
            bool dryRun = false,
            RequestMeta? meta = null)
        {
            Kind = kind;
            CommandName = commandName;
            Parameters = parameters;
            Steps = steps;
            StopOnError = stopOnError;
            DryRun = dryRun;
            Meta = meta;
        }
    }
}

/// <summary>One step inside a batch request.</summary>
public sealed record BatchStep(string CommandName, JsonObject Parameters);
