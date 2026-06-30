using System.Text.Json;
using System.Text.RegularExpressions;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.Governance;
using Hope.Agent.Application.Rag;
using Hope.Agent.Domain.Rag;
using Hope.Agent.Infrastructure.Eventing;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Rag;

internal sealed class AgenticRagService(
    IDbContextFactory<AgentDbContext> dbFactory,
    IRetriever retriever,
    IOptionsMonitor<AgenticRagOptions> options,
    IOptionsMonitor<TenantIsolationOptions> tenantIsolation,
    IMemoryCache cache,
    ILogger<AgenticRagService> log) : IAgenticRagService
{
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}_-]{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<AgenticRagResult> RunAsync(AgenticRagRequest request, CancellationToken ct)
    {
        var opts = NormalizeOptions(options.CurrentValue);
        if (!opts.Enabled)
            throw new InvalidOperationException("Agentic RAG is disabled.");
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (tenantIsolation.CurrentValue.RequireTenantScopedRetrieval && request.TenantId is null)
            throw new InvalidOperationException("TenantId is required for agentic RAG retrieval.");

        var now = DateTimeOffset.UtcNow;
        var run = new AgenticRagRun
        {
            Id = Guid.CreateVersion7(),
            RunId = $"ARAG-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            TenantId = request.TenantId,
            UserId = request.UserId,
            PatientId = request.PatientId,
            ConversationId = request.ConversationId,
            Query = request.Query.Trim(),
            Status = AgenticRagRunStatus.Running,
            CreatedAt = now,
            CorrelationId = request.CorrelationId,
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await db.AgenticRagRuns.AddAsync(run, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var selected = SelectCorpora(request, opts).ToList();
        var query = request.Query.Trim();
        var allHits = new List<AgenticRagRetrieval>();
        AgenticRagContextAssessmentResult assessment = new(false, 0, [], ExtractQueryTerms(request.Query, opts), "no retrieval attempted");
        var maxIterations = Math.Clamp(request.MaxIterations ?? opts.MaxIterations, 1, 8);

        try
        {
            await AddStepAsync(run.RunId, AgenticRagStepKind.Plan, 0, new
            {
                request.Query,
                request.PatientId,
                request.TenantId,
                ExplicitCorpora = request.Corpora,
                AvailableCorpora = opts.Corpora,
            }, new { selected }, request.CorrelationId, ct).ConfigureAwait(false);

            for (var iteration = 1; iteration <= maxIterations; iteration++)
            {
                var iterationHits = await RetrieveAsync(run, query, selected, opts, iteration, ct).ConfigureAwait(false);
                allHits.AddRange(iterationHits);
                await AddStepAsync(run.RunId, AgenticRagStepKind.Retrieve, iteration, new { query, selected }, new
                {
                    hitCount = iterationHits.Count,
                    corpora = iterationHits.GroupBy(x => x.Corpus).ToDictionary(x => x.Key, x => x.Count()),
                }, request.CorrelationId, ct).ConfigureAwait(false);

                var distinctHits = OptimizeContext(allHits, opts).ToList();
                assessment = AssessContext(request.Query, distinctHits, opts);
                await PersistAssessmentAsync(run.RunId, iteration, assessment, ct).ConfigureAwait(false);
                await AddStepAsync(run.RunId, AgenticRagStepKind.AssessContext, iteration, new
                {
                    query,
                    hitCount = distinctHits.Count,
                }, assessment, request.CorrelationId, ct).ConfigureAwait(false);

                run.IterationCount = iteration;
                if (assessment.Sufficient)
                {
                    allHits = distinctHits;
                    break;
                }

                if (iteration < maxIterations && assessment.MissingTerms.Count > 0)
                {
                    var previous = query;
                    query = RewriteQuery(request.Query, assessment.MissingTerms, iteration);
                    await AddStepAsync(run.RunId, AgenticRagStepKind.RewriteQuery, iteration, new
                    {
                        previousQuery = previous,
                        assessment.MissingTerms,
                    }, new { rewrittenQuery = query }, request.CorrelationId, ct).ConfigureAwait(false);
                }
            }

            allHits = OptimizeContext(allHits, opts).ToList();
            var citations = BuildCitations(allHits, opts).ToList();
            var answer = SynthesizeAnswer(request.Query, assessment, citations, opts);
            var status = assessment.Sufficient ? AgenticRagRunStatus.Succeeded : AgenticRagRunStatus.InsufficientContext;

            await UpdateRunAsync(run.RunId, status, answer, assessment, selected, citations, allHits, ct).ConfigureAwait(false);
            await AddStepAsync(run.RunId, AgenticRagStepKind.Synthesize, run.IterationCount, new
            {
                request.Query,
                assessment.Sufficient,
                citationCount = citations.Count,
            }, new { status, answer, citations }, request.CorrelationId, ct).ConfigureAwait(false);

            return new AgenticRagResult(
                run.RunId,
                status,
                assessment.Sufficient,
                assessment.Confidence,
                answer,
                citations,
                selected,
                assessment.MissingTerms,
                run.IterationCount);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Agentic RAG run failed: {RunId}", run.RunId);
            await MarkFailedAsync(run.RunId, ex.Message, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AgenticRagRunTrace?> GetTraceAsync(string runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var run = await db.AgenticRagRuns.AsNoTracking().FirstOrDefaultAsync(x => x.RunId == runId, ct).ConfigureAwait(false);
        if (run is null) return null;
        var steps = await db.AgenticRagSteps.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Iteration).ThenBy(x => x.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        var retrievals = await db.AgenticRagRetrievals.AsNoTracking().Where(x => x.RunId == runId).OrderByDescending(x => x.Score).ToListAsync(ct).ConfigureAwait(false);
        var assessments = await db.AgenticRagContextAssessments.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Iteration).ToListAsync(ct).ConfigureAwait(false);
        return new AgenticRagRunTrace(run, steps, retrievals, assessments);
    }

    private async Task<IReadOnlyList<AgenticRagRetrieval>> RetrieveAsync(
        AgenticRagRun run,
        string query,
        IReadOnlyList<string> corpora,
        AgenticRagOptions opts,
        int iteration,
        CancellationToken ct)
    {
        var tasks = corpora.Select(corpus => RetrieveCorpusAsync(run, query, corpus, opts, iteration, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var hits = results.SelectMany(x => x).ToList();
        if (hits.Count == 0) return hits;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AgenticRagRetrievals.AddRangeAsync(hits, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return hits;
    }

    private async Task<IReadOnlyList<AgenticRagRetrieval>> RetrieveCorpusAsync(
        AgenticRagRun run,
        string query,
        string corpus,
        AgenticRagOptions opts,
        int iteration,
        CancellationToken ct)
    {
        if (!opts.Corpora.TryGetValue(corpus, out var spec) || !spec.Enabled)
            return [];

        var cacheKey = $"agentic-rag:{run.TenantId:N}:{run.PatientId:N}:{corpus}:{Sha(query)}";
        if (opts.CacheRetrievalResults && cache.TryGetValue(cacheKey, out IReadOnlyList<AgenticRagRetrieval>? cached) && cached is not null)
            return cached.Select(x => CloneForRun(x, run.RunId, iteration, query)).ToList();

        var hits = spec.Type.ToLowerInvariant() switch
        {
            "document" => await RetrieveDocumentCorpusAsync(run, query, corpus, spec, opts, iteration, ct).ConfigureAwait(false),
            "memory" => await RetrieveMemoryCorpusAsync(run, query, corpus, spec, iteration, ct).ConfigureAwait(false),
            "medical_summary" => await RetrieveMedicalSummaryCorpusAsync(run, query, corpus, spec, iteration, ct).ConfigureAwait(false),
            "reminder" => await RetrieveReminderCorpusAsync(run, query, corpus, spec, iteration, ct).ConfigureAwait(false),
            "appointment" => await RetrieveAppointmentCorpusAsync(run, query, corpus, spec, iteration, ct).ConfigureAwait(false),
            "audit" => await RetrieveAuditCorpusAsync(run, query, corpus, spec, iteration, ct).ConfigureAwait(false),
            "conversation" => await RetrieveConversationCorpusAsync(run, query, corpus, spec, iteration, ct).ConfigureAwait(false),
            _ => [],
        };

        if (opts.CacheRetrievalResults)
            cache.Set(cacheKey, hits, TimeSpan.FromMinutes(10));
        return hits;
    }

    private async Task<IReadOnlyList<AgenticRagRetrieval>> RetrieveDocumentCorpusAsync(
        AgenticRagRun run,
        string query,
        string corpus,
        AgenticRagCorpusOptions spec,
        AgenticRagOptions opts,
        int iteration,
        CancellationToken ct)
    {
        var filter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (run.TenantId is { } tenantId && opts.TenantScopedRetrievalRequired)
            filter["tenant_id"] = tenantId.ToString();
        if (run.PatientId is { } patientId)
            filter["patient_id"] = patientId.ToString();

        var hits = await retriever.SearchAsync(
            new RetrievalQuery(query, spec.Collection, opts.TopKPerCorpus, opts.FinalKPerCorpus, filter.Count == 0 ? null : filter, true),
            ct).ConfigureAwait(false);
        return hits
            .Select(hit => ToRetrieval(run.RunId, iteration, corpus, query, spec, "documents", hit.ChunkId.ToString(), hit.Title, hit.Content, hit.Url, hit.Score, hit.Metadata))
            .ToList();
    }

    private async Task<IReadOnlyList<AgenticRagRetrieval>> RetrieveMemoryCorpusAsync(AgenticRagRun run, string query, string corpus, AgenticRagCorpusOptions spec, int iteration, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.Memories.AsNoTracking().Where(x => x.TenantId == run.TenantId);
        if (run.PatientId is { } patientId) q = q.Where(x => x.UserId == patientId);
        return (await q.OrderByDescending(x => x.Importance).ThenByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => ToRetrieval(run.RunId, iteration, corpus, query, spec, "agent_memories", x.Id.ToString(), x.Kind.ToString(), x.Content, null, ScoreText(query, x.Content) * spec.SourceWeight, x.Metadata))
            .Where(x => x.Score > 0.05)
            .ToList();
    }

    private async Task<IReadOnlyList<AgenticRagRetrieval>> RetrieveMedicalSummaryCorpusAsync(AgenticRagRun run, string query, string corpus, AgenticRagCorpusOptions spec, int iteration, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.MedicalSummaries.AsNoTracking();
        if (run.PatientId is { } patientId) q = q.Where(x => x.PatientId == patientId);
        return (await q.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => ToRetrieval(run.RunId, iteration, corpus, query, spec, "medical_summaries", x.SummaryId, x.SummaryType, x.SummaryText, null, ScoreText(query, x.SummaryText) * spec.SourceWeight, new Dictionary<string, string> { ["status"] = x.Status, ["specialty"] = x.Specialty ?? "" }))
            .Where(x => x.Score > 0.05)
            .ToList();
    }

    private async Task<IReadOnlyList<AgenticRagRetrieval>> RetrieveReminderCorpusAsync(AgenticRagRun run, string query, string corpus, AgenticRagCorpusOptions spec, int iteration, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.ReminderRecords.AsNoTracking();
        if (run.PatientId is { } patientId) q = q.Where(x => x.PatientId == patientId);
        return (await q.OrderByDescending(x => x.UpdatedAt).Take(100).ToListAsync(ct).ConfigureAwait(false))
            .Select(x =>
            {
                var text = $"Reminder {x.ReminderType} {x.MedicationName} {x.Dosage} {x.Frequency} {x.Status} {x.EscalationReason}";
                return ToRetrieval(run.RunId, iteration, corpus, query, spec, "reminder_records", x.ReminderId, x.ReminderType, text, null, ScoreText(query, text) * spec.SourceWeight, new Dictionary<string, string> { ["status"] = x.Status, ["channel"] = x.PreferredChannel ?? "" });
            })
            .Where(x => x.Score > 0.05)
            .ToList();
    }

    private async Task<IReadOnlyList<AgenticRagRetrieval>> RetrieveAppointmentCorpusAsync(AgenticRagRun run, string query, string corpus, AgenticRagCorpusOptions spec, int iteration, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.AppointmentBookings.AsNoTracking();
        if (run.PatientId is { } patientId) q = q.Where(x => x.PatientId == patientId);
        return (await q.OrderByDescending(x => x.ConfirmedAt).Take(100).ToListAsync(ct).ConfigureAwait(false))
            .Select(x =>
            {
                var text = $"Appointment {x.Status} doctor {x.DoctorId} slot {x.SlotId} at {x.AppointmentTime} reason {x.Reason}";
                return ToRetrieval(run.RunId, iteration, corpus, query, spec, "appointment_bookings", x.BookingId, x.Status, text, null, ScoreText(query, text) * spec.SourceWeight, []);
            })
            .Where(x => x.Score > 0.05)
            .ToList();
    }

    private async Task<IReadOnlyList<AgenticRagRetrieval>> RetrieveAuditCorpusAsync(AgenticRagRun run, string query, string corpus, AgenticRagCorpusOptions spec, int iteration, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.AuditEvents.AsNoTracking().Where(x => x.TenantId == run.TenantId || x.TenantId == null);
        if (run.PatientId is { } patientId) q = q.Where(x => x.PatientId == patientId.ToString() || x.ResourceId == patientId.ToString());
        return (await q.OrderByDescending(x => x.OccurredAt).Take(100).ToListAsync(ct).ConfigureAwait(false))
            .Select(x =>
            {
                var text = $"{x.Action} {x.ResourceType} {x.ResourceId} {x.Reason} {x.PayloadJson}";
                return ToRetrieval(run.RunId, iteration, corpus, query, spec, "audit_logs", x.Id.ToString(), x.Action, text, null, ScoreText(query, text) * spec.SourceWeight, new Dictionary<string, string> { ["actor"] = x.Actor });
            })
            .Where(x => x.Score > 0.05)
            .ToList();
    }

    private async Task<IReadOnlyList<AgenticRagRetrieval>> RetrieveConversationCorpusAsync(AgenticRagRun run, string query, string corpus, AgenticRagCorpusOptions spec, int iteration, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.Messages.AsNoTracking().Join(db.Conversations.AsNoTracking(), m => m.ConversationId, c => c.Id, (m, c) => new { Message = m, Conversation = c });
        if (run.ConversationId is { } conversationId) q = q.Where(x => x.Message.ConversationId == conversationId);
        if (run.PatientId is { } patientId) q = q.Where(x => x.Conversation.UserId == patientId || x.Conversation.UserId == run.UserId);
        return (await q.OrderByDescending(x => x.Message.CreatedAt).Take(100).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => ToRetrieval(run.RunId, iteration, corpus, query, spec, "conversation_messages", x.Message.Id.ToString(), x.Message.Role.ToString(), x.Message.Content, null, ScoreText(query, x.Message.Content) * spec.SourceWeight, new Dictionary<string, string> { ["conversation_id"] = x.Message.ConversationId.ToString() }))
            .Where(x => x.Score > 0.05)
            .ToList();
    }

    private static AgenticRagRetrieval ToRetrieval(string runId, int iteration, string corpus, string query, AgenticRagCorpusOptions spec, string source, string referenceId, string title, string content, string? url, double score, Dictionary<string, string> metadata)
        => new()
        {
            Id = Guid.CreateVersion7(),
            RetrievalId = $"ARH-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            RunId = runId,
            Iteration = iteration,
            Corpus = corpus,
            Query = query,
            Source = source,
            ReferenceId = referenceId,
            Title = title,
            Content = Truncate(content, 2_000),
            Url = url,
            Score = score,
            MetadataJson = JsonSerializer.Serialize(metadata),
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private IReadOnlyList<string> SelectCorpora(AgenticRagRequest request, AgenticRagOptions opts)
    {
        if (request.Corpora is { Length: > 0 })
            return request.Corpora.Where(c => opts.Corpora.ContainsKey(c)).Take(opts.MaxCorporaPerRun).ToList();

        var queryTerms = ExtractQueryTerms($"{request.Query} {request.Goal}", opts).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scored = opts.Corpora
            .Where(x => x.Value.Enabled && (!x.Value.RequiresPatientId || request.PatientId is not null))
            .Select(x => new
            {
                Corpus = x.Key,
                Score = ScoreText(string.Join(' ', queryTerms), $"{x.Key} {x.Value.Description} {x.Value.Type} {x.Value.Collection}") + (x.Value.Type == "document" ? 0.05 : 0),
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Corpus)
            .Take(Math.Max(opts.MaxCorporaPerRun, 1))
            .Select(x => x.Corpus)
            .ToList();

        return scored.Count == 0 ? opts.Corpora.Keys.Take(Math.Max(opts.MaxCorporaPerRun, 1)).ToList() : scored;
    }

    private AgenticRagContextAssessmentResult AssessContext(string originalQuery, IReadOnlyList<AgenticRagRetrieval> hits, AgenticRagOptions opts)
    {
        var terms = ExtractQueryTerms(originalQuery, opts).ToList();
        if (terms.Count == 0)
            return new(hits.Count > 0, hits.Count > 0 ? 0.7 : 0, [], [], hits.Count > 0 ? "context available" : "no searchable terms");
        var context = string.Join('\n', hits.Take(24).Select(x => x.Content)).ToLowerInvariant();
        var covered = terms.Where(t => context.Contains(t, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = terms.Except(covered, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        var coverage = (double)covered.Count / terms.Count;
        var sourceDiversity = hits.Select(x => x.Corpus).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var confidence = Math.Clamp((coverage * 0.78) + Math.Min(sourceDiversity, 3) * 0.07 + Math.Min(hits.Count, 6) * 0.015, 0, 1);
        var sufficient = hits.Count > 0 && confidence >= opts.MinSufficiencyConfidence && missing.Count <= Math.Max(1, terms.Count / 3);
        var feedback = sufficient
            ? "Context sufficient for grounded synthesis."
            : missing.Count == 0
                ? "Retrieved context is too sparse; search adjacent corpora or more specific records."
                : $"Missing query evidence for: {string.Join(", ", missing)}.";
        return new(sufficient, confidence, covered, missing, feedback);
    }

    private static string RewriteQuery(string originalQuery, IReadOnlyList<string> missingTerms, int iteration)
        => $"{originalQuery} {string.Join(' ', missingTerms.Take(6))} evidence details iteration{iteration}";

    private static string SynthesizeAnswer(string query, AgenticRagContextAssessmentResult assessment, IReadOnlyList<AgenticRagCitation> citations, AgenticRagOptions opts)
    {
        if (!assessment.Sufficient)
        {
            return "insufficient_context: Chưa đủ bằng chứng trong các nguồn được phép để trả lời chắc chắn. "
                + assessment.Feedback
                + (citations.Count > 0 ? $" Có {citations.Count} nguồn liên quan nhưng vẫn còn thiếu dữ kiện." : " Không tìm thấy nguồn đủ liên quan.");
        }

        var snippets = citations.Take(5).Select((c, i) => $"[{i + 1}] {c.Excerpt}").ToList();
        return $"Dựa trên {citations.Count} nguồn đã truy xuất, câu trả lời cho truy vấn \"{query}\" là: "
            + string.Join(" ", snippets)
            + " Vui lòng xem citations/provenance để kiểm tra nguồn trước khi dùng cho quyết định lâm sàng.";
    }

    private static IEnumerable<AgenticRagCitation> BuildCitations(IReadOnlyList<AgenticRagRetrieval> hits, AgenticRagOptions opts)
        => hits.Take(12).Select(x => new AgenticRagCitation(x.Corpus, x.Source, x.ReferenceId, x.Title, Truncate(x.Content, 420), x.Url, x.Score));

    private static IEnumerable<AgenticRagRetrieval> OptimizeContext(IEnumerable<AgenticRagRetrieval> hits, AgenticRagOptions opts)
    {
        var sourceRanks = hits
            .GroupBy(x => x.Corpus, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.OrderByDescending(x => x.Score).Select((x, i) => new { Hit = x, Rank = i + 1 }))
            .GroupBy(x => $"{x.Hit.Source}:{x.Hit.ReferenceId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Hit.Score).First())
            .Select(x =>
            {
                var recency = Math.Exp(-Math.Max(0, (DateTimeOffset.UtcNow - x.Hit.CreatedAt).TotalDays) / 180d);
                var trust = SourceTrust(x.Hit.Source);
                var rrf = 1d / (60 + x.Rank);
                x.Hit.Score = Math.Clamp((x.Hit.Score * 0.68) + (rrf * 8) + (recency * 0.08) + (trust * 0.12), 0, 2);
                return x.Hit;
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        var selected = new List<AgenticRagRetrieval>();
        var simHashes = new List<ulong>();
        var corpusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var charBudget = Math.Max(opts.MaxContextChars, 2_000);
        var usedChars = 0;
        foreach (var hit in sourceRanks)
        {
            var simHash = SimHash(hit.Content);
            if (simHashes.Any(existing => HammingDistance(existing, simHash) <= 3))
                continue;

            var countForCorpus = corpusCounts.GetValueOrDefault(hit.Corpus);
            var diversityPenalty = countForCorpus >= Math.Max(opts.FinalKPerCorpus, 1) ? 0.82 : 1.0;
            hit.Score *= diversityPenalty;
            if (usedChars + hit.Content.Length > charBudget && selected.Count >= 6)
                continue;

            selected.Add(hit);
            simHashes.Add(simHash);
            corpusCounts[hit.Corpus] = countForCorpus + 1;
            usedChars += hit.Content.Length;
            if (selected.Count >= 24 || usedChars >= charBudget)
                break;
        }

        return selected.OrderByDescending(x => x.Score);
    }

    private async Task AddStepAsync(string runId, AgenticRagStepKind kind, int iteration, object input, object output, string? correlationId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AgenticRagSteps.AddAsync(new AgenticRagStep
        {
            Id = Guid.CreateVersion7(),
            StepId = $"ARS-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            RunId = runId,
            Kind = kind,
            Iteration = iteration,
            InputJson = JsonSerializer.Serialize(input),
            OutputJson = JsonSerializer.Serialize(output),
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
        }, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task PersistAssessmentAsync(string runId, int iteration, AgenticRagContextAssessmentResult assessment, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AgenticRagContextAssessments.AddAsync(new AgenticRagContextAssessment
        {
            Id = Guid.CreateVersion7(),
            AssessmentId = $"ARA-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            RunId = runId,
            Iteration = iteration,
            Sufficient = assessment.Sufficient,
            Confidence = assessment.Confidence,
            CoveredTermsJson = JsonSerializer.Serialize(assessment.CoveredTerms),
            MissingTermsJson = JsonSerializer.Serialize(assessment.MissingTerms),
            Feedback = assessment.Feedback,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task UpdateRunAsync(string runId, AgenticRagRunStatus status, string answer, AgenticRagContextAssessmentResult assessment, IReadOnlyList<string> selected, IReadOnlyList<AgenticRagCitation> citations, IReadOnlyList<AgenticRagRetrieval> hits, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var run = await db.AgenticRagRuns.FirstAsync(x => x.RunId == runId, ct).ConfigureAwait(false);
        run.Status = status;
        run.Answer = answer;
        run.ContextSufficient = assessment.Sufficient;
        run.Confidence = assessment.Confidence;
        run.SelectedCorporaJson = JsonSerializer.Serialize(selected);
        run.CitationsJson = JsonSerializer.Serialize(citations);
        run.MetricsJson = JsonSerializer.Serialize(new
        {
            retrievals = hits.Count,
            distinctCorpora = hits.Select(x => x.Corpus).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            maxContextChars = options.CurrentValue.MaxContextChars,
            retrievalStrategy = "hybrid-vector-bm25-rrf-simhash-budget",
            assessment.CoveredTerms,
            assessment.MissingTerms,
            assessment.Feedback,
        });
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.OutboxEvents.AddAsync(EfOutboxStore.ToEntity(new OutboxEventWrite(
            run.TenantId,
            "hope.rag.agentic.runs",
            run.RunId,
            JsonSerializer.Serialize(new
            {
                run.RunId,
                run.TenantId,
                run.PatientId,
                run.Status,
                run.ContextSufficient,
                run.Confidence,
                run.IterationCount,
                run.CompletedAt,
            }),
            CorrelationId: run.CorrelationId,
            IdempotencyKey: $"agentic-rag:{run.RunId}")), ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task MarkFailedAsync(string runId, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var run = await db.AgenticRagRuns.FirstOrDefaultAsync(x => x.RunId == runId, ct).ConfigureAwait(false);
        if (run is null) return;
        run.Status = AgenticRagRunStatus.Failed;
        run.Answer = $"agentic_rag_failed:{reason}";
        run.MetricsJson = JsonSerializer.Serialize(new { reason });
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static AgenticRagOptions NormalizeOptions(AgenticRagOptions input)
    {
        if (input.Corpora.Count > 0) return input;
        var corpora = new Dictionary<string, AgenticRagCorpusOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["clinical_guidelines"] = new() { Type = "document", Collection = "clinical_guidelines", Description = "Clinical guidelines, SOP, protocols and policy documents.", SourceWeight = 1.0 },
            ["patient_memory"] = new() { Type = "memory", Description = "Patient-scoped AI memory facts and prior clinical notes.", SourceWeight = 0.92, RequiresPatientId = true },
            ["medical_summaries"] = new() { Type = "medical_summary", Description = "Persisted medical summaries and discharge/visit summaries.", SourceWeight = 0.98, RequiresPatientId = true },
            ["reminders"] = new() { Type = "reminder", Description = "Medication and follow-up reminders, status and escalation history.", SourceWeight = 0.9, RequiresPatientId = true },
            ["appointments"] = new() { Type = "appointment", Description = "Appointment bookings, slot history, doctors and reasons.", SourceWeight = 0.86, RequiresPatientId = true },
            ["audit"] = new() { Type = "audit", Description = "Audit logs, compliance events, workflow and tool actions.", SourceWeight = 0.82 },
            ["conversation"] = new() { Type = "conversation", Description = "Conversation messages and previous user-agent interactions.", SourceWeight = 0.78 },
        };
        return new AgenticRagOptions
        {
            Enabled = input.Enabled,
            MaxIterations = input.MaxIterations,
            MaxCorporaPerRun = input.MaxCorporaPerRun,
            TopKPerCorpus = input.TopKPerCorpus,
            FinalKPerCorpus = input.FinalKPerCorpus,
            MaxContextChars = input.MaxContextChars,
            MinSufficiencyConfidence = input.MinSufficiencyConfidence,
            RequireCitations = input.RequireCitations,
            TenantScopedRetrievalRequired = input.TenantScopedRetrievalRequired,
            CacheRetrievalResults = input.CacheRetrievalResults,
            StopWords = input.StopWords,
            Corpora = corpora,
        };
    }

    private static IReadOnlyList<string> ExtractQueryTerms(string? text, AgenticRagOptions opts)
    {
        var stop = opts.StopWords.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TokenRegex.Matches(text ?? string.Empty)
            .Select(m => Normalize(m.Value))
            .Where(t => t.Length >= 3 && !stop.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();
    }

    private static double ScoreText(string query, string text)
    {
        var q = TokenRegex.Matches(query ?? string.Empty).Select(m => Normalize(m.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (q.Count == 0) return 0.1;
        var lower = (text ?? string.Empty).ToLowerInvariant();
        var covered = q.Count(t => lower.Contains(t, StringComparison.OrdinalIgnoreCase));
        return Math.Clamp((double)covered / q.Count, 0, 1);
    }

    private static AgenticRagRetrieval CloneForRun(AgenticRagRetrieval hit, string runId, int iteration, string query)
        => new()
        {
            Id = Guid.CreateVersion7(),
            RetrievalId = $"ARH-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            RunId = runId,
            Iteration = iteration,
            Corpus = hit.Corpus,
            Query = query,
            Source = hit.Source,
            ReferenceId = hit.ReferenceId,
            Title = hit.Title,
            Content = hit.Content,
            Url = hit.Url,
            Score = hit.Score,
            MetadataJson = hit.MetadataJson,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    private static string Truncate(string value, int max) => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
    private static string Sha(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16];
    private static double SourceTrust(string source) => source switch
    {
        "medical_summaries" => 1.0,
        "documents" => 0.95,
        "reminder_records" => 0.9,
        "appointment_bookings" => 0.86,
        "audit_logs" => 0.84,
        "agent_memories" => 0.78,
        "conversation_messages" => 0.68,
        _ => 0.6,
    };

    private static ulong SimHash(string text)
    {
        Span<int> vector = stackalloc int[64];
        foreach (Match match in TokenRegex.Matches(text ?? string.Empty))
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Normalize(match.Value)));
            var hash = BitConverter.ToUInt64(bytes, 0);
            for (var i = 0; i < 64; i++)
                vector[i] += ((hash >> i) & 1UL) == 1UL ? 1 : -1;
        }

        ulong result = 0;
        for (var i = 0; i < 64; i++)
            if (vector[i] > 0) result |= 1UL << i;
        return result;
    }

    private static int HammingDistance(ulong left, ulong right)
    {
        var value = left ^ right;
        var count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }
        return count;
    }
}
