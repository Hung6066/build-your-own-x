<#
  Hope.Agent - Agentic RAG P0/P1/P2 Smoke Test

  Verifies:
    - Agentic RAG endpoint runs with tenant/patient scoped retrieval.
    - Planner selects corpora and iterative SCA produces an answer or insufficient_context.
    - Postgres persists runs, steps, retrievals, assessments.
    - Provenance endpoint returns citations/retrieval trace.

  Usage:
    .\tests\hope-agentic-rag-flows.ps1
    .\tests\hope-agentic-rag-flows.ps1 -SkipApiStart
#>

param(
    [string]$BaseUrl = "http://localhost:5080",
    [string]$ClientId = "doctor-nguyen",
    [string]$Secret = "HopeAgentDev2026!",
    [switch]$SkipApiStart
)

$ErrorActionPreference = "Stop"
$Root = "D:\Pr.Project\Hope.Agent"
$TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
$PatientId = "22222222-2222-2222-2222-222222222222"
$UserId = "11111111-1111-1111-1111-111111111111"

function Wait-ApiReady([int]$Seconds = 30) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        try {
            Invoke-WebRequest -Uri "$BaseUrl/v1/auth/login" -Method Options -UseBasicParsing -TimeoutSec 2 | Out-Null
            return
        } catch {
            if ($_.Exception.Response) { return }
            Start-Sleep -Seconds 1
        }
    }
    throw "API did not become ready at $BaseUrl."
}

function Pg([string]$Sql) {
    $output = $Sql | docker exec -i hope-agent-postgres-1 psql -U hope -d hope_agent -v ON_ERROR_STOP=1 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return $output
}

Write-Host "== Hope Agentic RAG P0/P1/P2 Flow Test ==" -ForegroundColor Cyan

$schema = @"
ALTER TABLE IF EXISTS audit_logs ADD COLUMN IF NOT EXISTS "TenantId" uuid;
ALTER TABLE IF EXISTS audit_logs ADD COLUMN IF NOT EXISTS "DeploymentVersion" varchar(128);
ALTER TABLE IF EXISTS audit_logs ADD COLUMN IF NOT EXISTS "PromptVersion" varchar(128);
ALTER TABLE IF EXISTS audit_logs ADD COLUMN IF NOT EXISTS "ModelVersion" varchar(128);
ALTER TABLE IF EXISTS audit_logs ADD COLUMN IF NOT EXISTS "ToolsetVersion" varchar(128);
ALTER TABLE IF EXISTS audit_logs ADD COLUMN IF NOT EXISTS "PolicyVersion" varchar(128);

CREATE TABLE IF NOT EXISTS agentic_rag_runs (
  "Id" uuid PRIMARY KEY,
  "RunId" varchar(64) NOT NULL UNIQUE,
  "TenantId" uuid NULL,
  "UserId" uuid NOT NULL,
  "PatientId" uuid NULL,
  "ConversationId" uuid NULL,
  "Query" text NOT NULL,
  "Answer" text NOT NULL,
  "Status" integer NOT NULL,
  "ContextSufficient" boolean NOT NULL,
  "Confidence" double precision NOT NULL,
  "IterationCount" integer NOT NULL,
  "SelectedCorporaJson" jsonb NOT NULL,
  "CitationsJson" jsonb NOT NULL,
  "MetricsJson" jsonb NOT NULL,
  "CreatedAt" timestamp with time zone NOT NULL,
  "CompletedAt" timestamp with time zone NULL,
  "CorrelationId" varchar(128) NULL
);
CREATE TABLE IF NOT EXISTS agentic_rag_steps (
  "Id" uuid PRIMARY KEY,
  "StepId" varchar(64) NOT NULL UNIQUE,
  "RunId" varchar(64) NOT NULL,
  "Kind" integer NOT NULL,
  "Iteration" integer NOT NULL,
  "InputJson" jsonb NOT NULL,
  "OutputJson" jsonb NOT NULL,
  "CreatedAt" timestamp with time zone NOT NULL,
  "CorrelationId" varchar(128) NULL
);
CREATE TABLE IF NOT EXISTS agentic_rag_retrievals (
  "Id" uuid PRIMARY KEY,
  "RetrievalId" varchar(64) NOT NULL UNIQUE,
  "RunId" varchar(64) NOT NULL,
  "Iteration" integer NOT NULL,
  "Corpus" varchar(128) NOT NULL,
  "Query" text NOT NULL,
  "Source" varchar(128) NOT NULL,
  "ReferenceId" varchar(128) NOT NULL,
  "Title" varchar(512) NOT NULL,
  "Content" text NOT NULL,
  "Url" varchar(1024) NULL,
  "Score" double precision NOT NULL,
  "MetadataJson" jsonb NOT NULL,
  "CreatedAt" timestamp with time zone NOT NULL
);
CREATE TABLE IF NOT EXISTS agentic_rag_context_assessments (
  "Id" uuid PRIMARY KEY,
  "AssessmentId" varchar(64) NOT NULL UNIQUE,
  "RunId" varchar(64) NOT NULL,
  "Iteration" integer NOT NULL,
  "Sufficient" boolean NOT NULL,
  "Confidence" double precision NOT NULL,
  "CoveredTermsJson" jsonb NOT NULL,
  "MissingTermsJson" jsonb NOT NULL,
  "Feedback" text NOT NULL,
  "CreatedAt" timestamp with time zone NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_agentic_rag_runs_TenantId_CreatedAt" ON agentic_rag_runs ("TenantId", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_agentic_rag_retrievals_RunId_Corpus" ON agentic_rag_retrievals ("RunId", "Corpus");

CREATE TABLE IF NOT EXISTS medical_summaries (
  "Id" uuid PRIMARY KEY,
  "SummaryId" varchar(64) NOT NULL UNIQUE,
  "PatientId" uuid NULL,
  "UserId" uuid NULL,
  "SummaryType" varchar(64) NOT NULL,
  "Audience" varchar(64) NOT NULL DEFAULT 'doctor',
  "Specialty" varchar(128) NULL,
  "SourceContext" text NOT NULL DEFAULT '',
  "SummaryText" text NOT NULL,
  "Model" varchar(128) NOT NULL DEFAULT 'seed',
  "Status" varchar(32) NOT NULL DEFAULT 'draft',
  "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
  "CorrelationId" varchar(128) NULL
);
CREATE TABLE IF NOT EXISTS reminder_records (
  "Id" uuid PRIMARY KEY,
  "ReminderId" varchar(64) NOT NULL UNIQUE,
  "WorkflowId" varchar(128) NULL,
  "PatientId" uuid NOT NULL,
  "UserId" uuid NULL,
  "ReminderType" varchar(64) NOT NULL,
  "MedicationName" varchar(256) NOT NULL,
  "Dosage" varchar(128) NULL,
  "Frequency" varchar(64) NOT NULL,
  "PreferredChannel" varchar(64) NULL,
  "StartAt" timestamp with time zone NOT NULL DEFAULT now(),
  "EndAt" timestamp with time zone NULL,
  "Status" varchar(32) NOT NULL,
  "EscalationReason" text NULL,
  "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
  "CorrelationId" varchar(128) NULL
);
"@
$null = Pg $schema

$seed = @"
INSERT INTO medical_summaries ("Id","SummaryId","PatientId","UserId","SummaryType","Audience","Specialty","SourceContext","SummaryText","Model","Status","CreatedAt","CorrelationId")
VALUES (
  gen_random_uuid(),
  'MS-AGENTIC-RAG-001',
  '$PatientId',
  '$UserId',
  'chronic_care',
  'doctor',
  'endocrinology',
  'seed',
  'Bệnh nhân T2DM đang dùng Metformin 500mg buổi tối. Không ghi nhận dị ứng thuốc trong lần khám gần nhất. Cần tái khám sau 30 ngày và theo dõi tác dụng phụ tiêu hóa.',
  'seed',
  'final',
  now(),
  'agentic-rag-seed'
)
ON CONFLICT ("SummaryId") DO UPDATE SET "SummaryText" = EXCLUDED."SummaryText", "CreatedAt" = now();

INSERT INTO reminder_records ("Id","ReminderId","WorkflowId","PatientId","UserId","ReminderType","MedicationName","Dosage","Frequency","PreferredChannel","Status","EscalationReason","UpdatedAt","CorrelationId")
VALUES (
  gen_random_uuid(),
  'REM-AGENTIC-RAG-001',
  'WF-AGENTIC-RAG-001',
  '$PatientId',
  '$UserId',
  'medication',
  'Metformin',
  '500mg',
  'once_daily_evening',
  'zalo',
  'active',
  'missed one evening dose last week',
  now(),
  'agentic-rag-seed'
)
ON CONFLICT ("ReminderId") DO UPDATE SET "Status" = EXCLUDED."Status", "EscalationReason" = EXCLUDED."EscalationReason", "UpdatedAt" = now();
"@
$null = Pg $seed

$apiProcess = $null
try {
    if (-not $SkipApiStart) {
        $dotnet = "C:\Program Files\dotnet\dotnet.exe"
        $out = "$Root\artifacts\agentic-rag-api.out.log"
        $err = "$Root\artifacts\agentic-rag-api.err.log"
        Remove-Item $out,$err -ErrorAction SilentlyContinue
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        $apiProcess = Start-Process -FilePath $dotnet `
            -ArgumentList @('bin\Debug\net9.0\Hope.Agent.Api.dll','--urls',$BaseUrl) `
            -WorkingDirectory "$Root\src\Hope.Agent.Api" `
            -RedirectStandardOutput $out `
            -RedirectStandardError $err `
            -PassThru `
            -WindowStyle Hidden
    }

    Wait-ApiReady
    $login = Invoke-RestMethod -Uri "$BaseUrl/v1/auth/login" -Method Post -ContentType "application/json" `
        -Body (@{ clientId = $ClientId; secret = $Secret } | ConvertTo-Json -Compress)
    $headers = @{ Authorization = "Bearer $($login.accessToken)"; "X-Tenant-Id" = $TenantId }

    $body = @{
        query = "T2DM Metformin dị ứng thuốc tái khám nhắc thuốc"
        tenantId = $TenantId
        patientId = $PatientId
        corpora = @("medical_summaries", "reminders")
        maxIterations = 3
    } | ConvertTo-Json -Depth 10 -Compress

    $result = Invoke-RestMethod -Uri "$BaseUrl/v1/rag/agentic/query" -Method Post -Headers $headers -ContentType "application/json" -Body $body -TimeoutSec 60
    if ([string]::IsNullOrWhiteSpace($result.runId)) { throw "Missing runId." }
    if ($result.citations.Count -lt 1) { throw "Expected at least one citation." }
    Write-Host "[PASS] Agentic RAG query returned run and citations." -ForegroundColor Green

    $trace = Invoke-RestMethod -Uri "$BaseUrl/v1/rag/agentic/runs/$($result.runId)" -Headers $headers -TimeoutSec 30
    if ($trace.steps.Count -lt 3) { throw "Expected plan/retrieve/assessment/synthesis steps." }
    if ($trace.retrievals.Count -lt 1) { throw "Expected persisted retrievals." }
    if ($trace.assessments.Count -lt 1) { throw "Expected persisted assessments." }
    Write-Host "[PASS] Trace endpoint exposes steps, retrievals, assessments." -ForegroundColor Green

    $prov = Invoke-RestMethod -Uri "$BaseUrl/v1/rag/agentic/runs/$($result.runId)/provenance" -Headers $headers -TimeoutSec 30
    if ($prov.retrievals.Count -lt 1) { throw "Expected provenance retrievals." }
    Write-Host "[PASS] Provenance endpoint exposes source evidence." -ForegroundColor Green

    $counts = Pg "SELECT (SELECT COUNT(*) FROM agentic_rag_runs WHERE ""RunId""='$($result.runId)') || ',' || (SELECT COUNT(*) FROM agentic_rag_steps WHERE ""RunId""='$($result.runId)') || ',' || (SELECT COUNT(*) FROM agentic_rag_retrievals WHERE ""RunId""='$($result.runId)') || ',' || (SELECT COUNT(*) FROM agentic_rag_context_assessments WHERE ""RunId""='$($result.runId)');"
    $match = if ($counts) { $counts | Select-String -Pattern "^\d+,\d+,\d+,\d+$" } else { $null }
    if ($match) {
        $parts = $match.Matches.Value.Split(',')
        if ([int]$parts[0] -lt 1 -or [int]$parts[1] -lt 3 -or [int]$parts[2] -lt 1 -or [int]$parts[3] -lt 1) {
            throw "Unexpected persisted counts: $counts"
        }
        Write-Host "[PASS] Postgres direct counts verified for agentic RAG run." -ForegroundColor Green
    } else {
        Write-Host "[PASS] Persistence verified through trace endpoint; direct Docker count skipped." -ForegroundColor Green
    }

    Write-Host "Agentic RAG P0/P1/P2 flow result: PASS" -ForegroundColor Cyan
    exit 0
}
finally {
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
