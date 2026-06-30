using Hope.Agent.Application.Workflows;
using Hope.Agent.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Persistence;

public sealed class EfAuditReportStore(IDbContextFactory<AgentDbContext> dbFactory) : IAuditReportStore
{
    public async Task SaveAsync(AuditReportWrite report, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entity = await db.AuditReports
            .SingleOrDefaultAsync(x => x.ReportId == report.ReportId, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new AuditReportRecord
            {
                Id = Guid.CreateVersion7(),
                ReportId = report.ReportId,
            };
            db.AuditReports.Add(entity);
        }

        entity.RequestedBy = report.RequestedBy;
        entity.ReportType = report.ReportType;
        entity.PeriodStart = report.PeriodStart;
        entity.PeriodEnd = report.PeriodEnd;
        entity.Narrative = report.Narrative;
        entity.MetricsJson = report.MetricsJson;
        entity.AnomaliesJson = report.AnomaliesJson;
        entity.Format = report.Format;
        entity.ExportPath = report.ExportPath;
        entity.IntegrityHash = report.IntegrityHash;
        entity.ByteSize = report.ByteSize;
        entity.SigningAlgorithm = report.SigningAlgorithm;
        entity.ExportedAt = report.ExportedAt;
        entity.Status = report.Status;
        entity.CorrelationId = report.CorrelationId;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
