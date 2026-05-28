# Hope.Agent — Security Operations Runbook

> Audience: on-call SRE / security engineers · Scope: production deployments handling PHI

---

## 1. JWT Signing Key Rotation (RS256)

**When:** scheduled every 90 days · immediately on suspected compromise

### 1.1 Generate new RSA-2048 key pair

```powershell
# On a trusted workstation (NOT on a production host)
openssl genrsa -out jwt-private-new.pem 2048
openssl rsa -in jwt-private-new.pem -pubout -out jwt-public-new.pem
```

### 1.2 Stage rotation (zero downtime)

The `RotatingJwtKeyProvider` accepts **two** keys simultaneously — `Current` issues + verifies, `Previous` verifies only. Rotation proceeds in three steps:

| Step                    | `Jwt:PrivateKeyPath` | `Jwt:PublicKeyPath` | `Jwt:PreviousPublicKeyPath` | Result                                      |
| ----------------------- | -------------------- | ------------------- | --------------------------- | ------------------------------------------- |
| Start                   | `old.pem`            | `old.pub.pem`       | _(empty)_                   | Steady state                                |
| **1. Stage new public** | `old.pem`            | `old.pub.pem`       | `new.pub.pem`               | API accepts old, ready to verify new        |
| **2. Cut over**         | `new.pem`            | `new.pub.pem`       | `old.pub.pem`               | API issues new tokens, still verifies old   |
| **3. Retire old**       | `new.pem`            | `new.pub.pem`       | _(empty)_                   | After `Auth:AccessTokenLifetimeMinutes` × 2 |

Restart sequence: rolling restart of API replicas after each step. JWKS endpoint `/.well-known/jwks.json` reflects the change immediately.

### 1.3 Emergency rotation (suspected key compromise)

1. Skip step 1 — go straight to step 2 with `PreviousPublicKeyPath` empty.
2. Force-revoke all refresh tokens: `redis-cli --scan --pattern 'rt:*' | xargs redis-cli del`
3. Invalidate every active access token by bumping `Jwt:Issuer` (forces all clients to re-login).
4. Audit-log the rotation in the security incident ledger.

---

## 2. Refresh Token Family Compromise

**Signal:** SIEM alert on `auth.refresh.replay_family_revoked` event.

### Response

The system has already revoked the entire family. Operator must:

1. Identify the affected user from the log line: `userId={UserId} familyId={FamilyId}`.
2. Cross-reference recent `auth.login.success` and `auth.login.failed` events for that user.
3. If session anomaly suspected (new IP/country, impossible travel):
   - Force password reset for the user.
   - Revoke all sessions: `redis-cli --scan --pattern 'rt-fam:{userId-N}:*' | xargs redis-cli del`
4. File incident report referencing `correlationId`.

---

## 3. Webhook Secret Rotation

**When:** every 90 days · on partner request · on suspected leak

### Process (multi-secret tolerance — TODO when WebhookOptions.PreviousSecret is added)

Current implementation has a **single secret** — rotation requires brief coordination:

1. Notify partner of upcoming rotation 24 h in advance.
2. Generate new secret: `openssl rand -base64 48`
3. Update partner system with new secret.
4. Update Hope.Agent `Webhook:Secret` config and restart API (rolling).
5. Verify next webhook event arrives with valid HMAC.

**Window of failure:** if partner sends a webhook between steps 3 and 4 (or vice versa), it will be rejected with 401 and the partner's retry mechanism kicks in. Acceptable for non-clinical events; risky for billing.

---

## 4. Database (Postgres) Credential Rotation

**When:** every 90 days · on personnel change with credential access

```powershell
# 1. Create new role with same privileges
psql -h $PGHOST -U postgres -c "CREATE USER hope_v2 WITH PASSWORD '<new>' IN ROLE hope;"

# 2. Update Key Vault secret
az keyvault secret set --vault-name hope-prod --name Postgres-ConnectionString --value "Host=...;Username=hope_v2;Password=<new>"

# 3. Rolling restart API replicas
docker service update --force ivf_api

# 4. After 24 h with no errors from old user, drop it
psql -h $PGHOST -U postgres -c "DROP USER hope;"
```

---

## 5. PHI Leak Suspected in Logs

**Trigger:** any log line containing a raw SSN/CCCD/phone/email pattern that did NOT come through the redactor.

### Containment

1. **Stop ingestion immediately:** scale Serilog → OTLP exporter sinks to zero.
   ```powershell
   docker service scale ivf_otel_collector=0
   ```
2. Snapshot the affected log indices for forensic review (do NOT delete).
3. Identify the unredacted property — check `PhiDestructuringPolicy` cache and verify the source type is in the `Hope.Agent.*` namespace.
4. If the leak is from a third-party type, wrap the log call: `log.LogInformation("...", redactor.Redact(json));`

### Notification (HIPAA breach trigger)

If unauthorized disclosure of PHI is confirmed:

- < 60 days from discovery: notify affected individuals
- < 60 days from discovery: notify HHS (if ≥ 500 individuals affected)
- Maintain breach log per § 164.408

---

## 6. Audit Chain Integrity Verification

**When:** monthly · on suspected database tampering

```powershell
# Verify the audit hash chain end-to-end
dotnet run --project tools/audit-verify -- --since 2026-01-01
```

The tool walks events in `OccurredAt` order, recomputes `SHA-256(prev || canonical(data))`, and asserts every stored `chain.hash` matches. Any mismatch → tamper detected.

If verification fails:

1. Locate the first failing row by id.
2. Compare PayloadJson against the most recent off-site backup.
3. Restore the canonical row; rebuild forward chain.
4. File incident — assume database write access was compromised, rotate Postgres credentials.

---

## 7. DPoP Token Binding Drift

**Signal:** elevated 401 rate with title `invalid_dpop:*` or `thumbprint_mismatch`.

| Reason code                     | Cause                               | Action                                           |
| ------------------------------- | ----------------------------------- | ------------------------------------------------ |
| `iat_skew`                      | Client clock > 60 s out of sync     | Have client re-sync NTP                          |
| `replay`                        | Same `jti` seen within 5 min        | Possible MITM — investigate client               |
| `htm_mismatch` / `htu_mismatch` | Client signed wrong method/URI      | Client SDK bug — check version                   |
| `thumbprint_mismatch`           | Access token bound to different key | Client lost private key — force re-login         |
| `bad_signature`                 | Crypto failure                      | Inspect proof header structure; check JWK format |

---

## 8. Idempotency Store Under Load

**Signal:** Redis memory > 80% with prefix `idem:*` consuming majority.

### Tuning

```json
"Idempotency": {
  "RetentionHours": 6   // Reduce from default 24
}
```

Lower retention reduces memory at the cost of accepting duplicate writes from clients that retry > N hours later. For clinical workflows, **never** go below 1 h.

**Emergency drain:**

```powershell
redis-cli --scan --pattern 'idem:*' | xargs redis-cli unlink
```

Acceptable side-effect: in-flight retries within the next hour may execute twice. Coordinate with clinical ops.

---

## 9. LLM Egress PHI Leak Alert

**Signal:** `egress.spotlight_token_in_response` event.

The egress guard caught the model echoing spotlight delimiters — likely a prompt injection partially succeeded. Response was replaced with the generic refusal.

### Investigation

1. Pull the full `correlationId` chain: user query, retrieved RAG chunks, LLM response.
2. Identify the poisoned source — look for `<DATA_UNTRUSTED>` or `IGNORE PREVIOUS` patterns in `MemoryRecord.Content`.
3. Quarantine the source document: set `metadata.quarantined = true` and remove from Qdrant filter.
4. If the source was uploaded by a tenant, suspend their upload privilege and trigger admin review.

---

## 10. Patient Access Denial Investigation

**Signal:** elevated rate of `authz.patient.denied` events.

A clinician is repeatedly attempting cross-patient access. Possible causes:

1. **UI bug** — frontend cached a stale patient context. Reproduce + ticket frontend team.
2. **Misconfigured claim** — `patients` claim missing from the user's profile in the identity store. Reissue token.
3. **Insider threat** — clinician systematically probing patients they aren't assigned. Escalate to compliance officer with the access-attempt timeline.

Query for impact:

```sql
SELECT actor, COUNT(*) AS denials, MIN(occurred_at), MAX(occurred_at)
FROM audit_events
WHERE action = 'authz.patient.denied'
  AND occurred_at >= NOW() - INTERVAL '24 hours'
GROUP BY actor
ORDER BY denials DESC;
```

---

## 11. Tenant Boundary Breach Investigation

**Signal:** `authz.tenant.denied` log lines.

Similar to § 10 but across organisational boundaries. Treat as a higher-severity event by default — cross-tenant access attempts often indicate stolen credentials.

Immediate steps:

1. Revoke the affected user's refresh token family.
2. Notify the user's tenant admin via the security incident channel.
3. Investigate whether the access token used was bound to DPoP (check `cnf.jkt`) — if yes, the attacker likely controls the device.

---

## 12. Routine Verification Checklist

Run weekly on staging, monthly on production:

- [ ] `dotnet build Hope.Agent.sln` — must be 0 errors / 0 warnings
- [ ] `pwsh tools/hope-security.ps1 -IncludeTransitive -FailOnSeverity High` — no vulnerable packages
- [ ] CodeQL workflow green on main branch
- [ ] Gitleaks scan green on last 100 commits
- [ ] ZAP baseline scan green
- [ ] JWKS endpoint reachable + serves expected `kid`
- [ ] `/healthz/live` returns 200
- [ ] `/.well-known/security.txt` reachable
- [ ] Random sample of 100 audit-chain rows verifies clean
- [ ] PHI redactor unit tests pass with current locale patterns

---

## Appendix A — Useful Redis Keys

| Prefix                           | Purpose                        | TTL   |
| -------------------------------- | ------------------------------ | ----- |
| `rt:{sha256(token)}`             | Active refresh token           | 7 d   |
| `rt-burned:{sha256(token)}`      | Tombstone for replay detection | 7 d   |
| `rt-fam:{userId-N}:{familyId-N}` | Family member set              | 7 d   |
| `idem:{sha256(user:key)}`        | Idempotency entry              | 24 h  |
| `dpop:jti:{jti}`                 | DPoP replay cache              | 5 min |
| `audit:chain:head`               | Latest audit chain hash        | ∞     |
| `embed:{sha256(text):model}`     | Embedding cache                | 24 h  |

## Appendix B — Useful Log Event Keys (`Hope.Agent.Auth` category)

| Key                                  | Severity    | Trigger                               |
| ------------------------------------ | ----------- | ------------------------------------- |
| `auth.login.failed`                  | Warning     | Invalid credential                    |
| `auth.login.success`                 | Information | Login OK                              |
| `auth.refresh.replay_or_expired`     | Warning     | Unknown token                         |
| `auth.refresh.replay_family_revoked` | **Warning** | Burned token replayed → family killed |
| `auth.refresh.success`               | Debug       | Normal rotation                       |
| `auth.revoke`                        | Information | Client logout                         |
| `authz.patient.denied`               | Warning     | Cross-patient access blocked          |
| `authz.tenant.denied`                | Warning     | Cross-tenant access blocked           |
| `egress.spotlight_token_in_response` | Warning     | LLM echoed control tokens             |
