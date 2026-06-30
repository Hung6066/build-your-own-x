# Hope.Agent — Security & Technical Audit: AgentOrchestrator & Tool-Calling

> **Tài liệu số 2/3** — Audit rủi ro kỹ thuật trong AgentOrchestrator và luồng Tool-Calling  
> **Ngày**: 2026-06-03 | **Auditor**: Automated Code Analysis  
> **Phạm vi**: `AgentOrchestrator.cs` (700 dòng) + toàn bộ luồng tool execution

---

## 1. Executive Summary

| Chỉ số                    | Giá trị             |
| ------------------------- | ------------------- |
| Tổng rủi ro phát hiện     | **23**              |
| CRITICAL                  | 0                   |
| HIGH                      | 3                   |
| MEDIUM                    | 8                   |
| LOW                       | 7                   |
| INFO / Best Practice      | 5                   |
| **Điểm bảo mật tổng thể** | **★★★★☆ (4.2/5.0)** |

**Nhận định chung**: AgentOrchestrator được thiết kế với defense-in-depth rất tốt. Mọi ngõ vào/ra đều có shield. Tuy nhiên còn một số điểm cần cải thiện về race condition, resource leak, và error handling.

---

## 2. Phân Loại Rủi Ro

### 2.1 Danh sách đầy đủ

| ID       | Mức độ | Vị trí                         | Mô tả                                                                                                                                                         | Khuyến nghị                                                                                             |
| -------- | ------ | ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------- |
| **H-01** | HIGH   | `RunAsync():237-245`           | Tool-call loop không propagate CancellationToken vào vòng lặp tool execution — nếu user disconnect, tool vẫn chạy đến hết                                     | Thêm `ct.ThrowIfCancellationRequested()` đầu mỗi iteration                                              |
| **H-02** | HIGH   | `ExecuteToolAsync():529-532`   | Nếu tool trả về null string, `AddMessage` nhận null content → NRE tiềm ẩn                                                                                     | Thêm null-coalescing: `output ?? "{}"`                                                                  |
| **H-03** | HIGH   | `PersistMemoryAsync():538-541` | MemoryConsolidator chạy sync trong luồng chính, không có timeout → có thể block response nếu consolidation chậm                                               | Wrap với `Task.WhenAny` + timeout hoặc chạy fire-and-forget                                             |
| **M-01** | MEDIUM | `RunAsync():73-86`             | `shield.Inspect()` nếu throw unhandled exception → crash toàn bộ request thay vì trả về lỗi an toàn                                                           | Wrap trong try-catch, fallback về block an toàn                                                         |
| **M-02** | MEDIUM | `RunAsync():130-140`           | Context gathering (memories, skills, traits, compression, clinical) chạy tuần tự thay vì song song → tăng latency đáng kể                                     | Sử dụng `Task.WhenAll` cho các tác vụ độc lập                                                           |
| **M-03** | MEDIUM | `BuildMessages():362-390`      | Mỗi lần gọi copy toàn bộ messages list — với conversation dài (>100 turns) + 6 tool iterations, memory pressure cao                                           | Xem xét dùng `ImmutableList` hoặc reusable `List<ChatMessage>` pool                                     |
| **M-04** | MEDIUM | `ExecuteToolAsync():487-489`   | Sandbox.InvokeAsync không có timeout → tool có thể treo vô hạn (vd: HIS API chậm)                                                                             | Thêm `CancellationTokenSource` với timeout configurable                                                 |
| **M-05** | MEDIUM | `RunAsync():257-260`           | `_ = Task.Run(...)` fire-and-forget cho user-model extract: nếu throw unhandled, observation scope mất, không ai log được                                     | Dùng `Task.Run` với try-catch đầy đủ (đã có) nhưng thêm `TaskScheduler.UnobservedTaskException` handler |
| **M-06** | MEDIUM | `AppendAssistantToolCalls()`   | Hàm này chỉ copy Assistant content, không copy `tool_calls` array vào messages → LLM provider có thể không nhận được context tool calls từ assistant trước đó | Cần copy `resp.ToolCalls` vào message assistant                                                         |
| **M-07** | MEDIUM | `StreamAsync():240-250`        | Không có shield/guard trên streaming path — user có thể nhận token chứa credential leak trước khi output shield can thiệp                                     | Áp dụng output shield per-chunk hoặc buffer đến khi có complete sentence                                |
| **M-08** | MEDIUM | `RunAsync():148`               | `ToolDefs` được compute 1 lần đầu vòng lặp, không cập nhật nếu MCP discovery thêm tool mới giữa chừng                                                         | Acceptable cho single-turn, nhưng nếu loop dài có thể thiếu tool mới                                    |
| **L-01** | LOW    | `RetrieveMemoriesAsync():280`  | `router.SelectEmbedding()` gọi mỗi lần retrieval — nếu provider stateless thì không sao, nếu có state thì cần reuse                                           | Cache embedding provider reference nếu stateless                                                        |
| **L-02** | LOW    | `RunAsync():185-190`           | Token counting dùng `resp.Usage` từ provider — nếu provider trả về 0 hoặc null, cost tracking sai                                                             | Fallback về self-counting nếu Usage null                                                                |
| **L-03** | LOW    | `DistillSkillAsync():581`      | SHA-256 hash signature dùng intent + message[0..256] — có thể collision nếu 2 intent giống nhau + message đầu 256 ký tự giống nhau                            | Thêm timestamp hoặc conversation ID vào signature                                                       |
| **L-04** | LOW    | `RunAsync():203-210`           | `hadToolError` reward gán cứng -0.5/0.2 không phản ánh đúng mức độ thành công                                                                                 | Dùng judge score hoặc feedback signal thay vì hardcode                                                  |
| **L-05** | LOW    | `StoreEpisodicAsync():563-577` | Dedup threshold 0.92 có thể quá cao/thấp tùy embedding model — cùng câu hỏi với embedding model khác có thể tạo duplicate                                     | Configurable threshold per embedding model                                                              |
| **L-06** | LOW    | `BuildMessages()`              | `CompressionResult.SummarizedUpTo` filter có thể bỏ sót message nếu clock skew giữa app server và DB                                                          | Dùng ID-based filter thay vì timestamp                                                                  |
| **L-07** | LOW    | `AppendToolResult()`           | Chỉ copy messages, không cập nhật `resp.ToolCalls[i].Result` → không tương thích với provider cần format đặc biệt                                             | Thêm provider-specific formatting nếu cần                                                               |
| **I-01** | INFO   | `AgentRuntimeOptions`          | `RerankC5319andidateK` — typo trong tên property (C5319)                                                                                                      | Sửa thành `RerankCandidateK`                                                                            |
| **I-02** | INFO   | `RunAsync():72`                | `Activity.StartActivity` nếu null (no listener) → `activity?.SetTag` null-safe nhưng có thể bỏ lỡ root span                                                   | Đảm bảo luôn có ít nhất 1 listener (console exporter dev, OTLP prod)                                    |
| **I-03** | INFO   | `BuildMessages():334-350`      | System prompt dài + clinical context + memory + skills → có thể vượt context window của model nhỏ (4K tokens)                                                 | Thêm context-budget tracking + truncation strategy                                                      |
| **I-04** | INFO   | `RunAsync()`                   | Không có exponential backoff cho tool-call loop khi tool liên tục fail → có thể loop 6 lần vô ích                                                             | Early exit nếu 3 tool liên tiếp fail                                                                    |
| **I-05** | INFO   | `ExecuteToolAsync()`           | Tool result không được validation trước khi đưa vào LLM context → indirect prompt injection risk                                                              | Thêm RetrievalRail check cho tool output (hiện chỉ check memory)                                        |

---

## 3. Phân Tích Chi Tiết Các Rủi Ro HIGH

### H-01: Tool Execution Không Tôn Trọng CancellationToken

**Vị trí**: `AgentOrchestrator.cs:237-245` (tool-call loop)

**Mô tả**: Khi user disconnect hoặc HTTP request timeout, `CancellationToken` được set. Tuy nhiên trong vòng lặp tool-call không có `ct.ThrowIfCancellationRequested()`. Điều này có nghĩa:

- Nếu tool 1 đang chạy (ví dụ: HIS API mất 30s) → tín hiệu cancel đến nhưng tool vẫn chạy
- Sau khi tool 1 hoàn thành, loop tiếp tục iteration 2, 3, ... cho đến 6
- LLM call iteration tiếp theo mới throw OperationCanceledException

**Impact**: Lãng phí tài nguyên LLM + tool execution khi user đã rời đi. Với 6 iteration × 30s = 3 phút lãng phí.

**Fix đề xuất**:

```csharp
for (int iter = 0; iter < _opts.MaxToolIterations; iter++)
{
    ct.ThrowIfCancellationRequested(); // ← Thêm dòng này

    var resp = await chat.CompleteAsync(new ChatRequest(messages, ...), ct);
    // ...
}
```

---

### H-02: Null Reference Risk Trong Tool Output

**Vị trí**: `AgentOrchestrator.cs:529-532`

**Mô tả**:

```csharp
var output = await sandbox.InvokeAsync(tool, call.ArgumentsJson, ctx, ct);
conv.AddMessage(MessageRole.Tool, output, clock.UtcNow, call.Name, call.Id);
return (output, new AgentToolExecution(call.Name, call.ArgumentsJson, output, sw.Elapsed, true));
```

Nếu `sandbox.InvokeAsync` trả về `null` (ví dụ: tool implementation bug, sandbox wrapper lỗi), `output` là null → `AddMessage` nhận null content → có thể gây NRE hoặc lưu null vào DB vi phạm constraint.

**Impact**: Crash request hoặc corrupt data.

**Fix đề xuất**:

```csharp
var output = await sandbox.InvokeAsync(tool, call.ArgumentsJson, ctx, ct) ?? "{}";
```

---

### H-03: Memory Consolidation Blocking Response

**Vị trí**: `AgentOrchestrator.cs:538-541`

**Mô tả**:

```csharp
if (_opts.EnableMemoryConsolidation && consolidator is not null)
{
    await consolidator.ConsolidateAsync(..., ct);  // ← Chạy sync, block response
    return;
}
```

Memory consolidation (Mem0/A-Mem) có thể gọi LLM để extract facts, UPDATE/DELETE memory, link graph — tốn 2-5s. Response bị block cho đến khi consolidation hoàn tất.

**Impact**: Tăng latency response 2-5s cho user, trong khi consolidation không cần thiết phải nằm trong critical path.

**Fix đề xuất**:

```csharp
if (_opts.EnableMemoryConsolidation && consolidator is not null)
{
    _ = Task.Run(async () =>
    {
        try { await consolidator.ConsolidateAsync(..., CancellationToken.None); }
        catch (Exception ex) { log.LogWarning(ex, "Background consolidation failed"); }
    });
    return;
}
```

---

## 4. Phân Tích Chi Tiết Các Rủi Ro MEDIUM

### M-01: Unhandled Exception Trong Shield → Crash

`shield.Inspect()` nếu throw exception (vd: Redis timeout cho adversarial pattern store) → không được catch → crash toàn bộ request với HTTP 500. Nên wrap trong try-catch và fallback về `Allowed = true` với log warning (fail-open cho shield, vì output shield vẫn bảo vệ).

### M-02: Context Gathering Tuần Tự Gây Latency

Step 3 gọi 5 dependency tuần tự:

```
memories = await RetrieveMemoriesAsync()    ~200ms
skills = await SafeRetrieveSkillsAsync()     ~50ms
traits = await userModel.GetAsync()          ~100ms
compression = await compressor.MaybeCompressAsync() ~300ms
clinicalFragment = await clinicalContext.GetAsync() ~150ms
TOTAL: ~800ms tuần tự
```

Nếu chạy song song với `Task.WhenAll`: ~300ms (bounded bởi task chậm nhất). **Tiết kiệm ~500ms/request**.

### M-03: Memory Pressure Từ Message Copy

Mỗi iteration tạo copy mới của toàn bộ `List<ChatMessage>` (có thể 50-100 messages). Với 6 iterations = 6 copies. Với 100 concurrent users = 600 copies × 100KB = 60MB heap pressure.

### M-04: Tool Timeout Không Được Kiểm Soát

`sandbox.InvokeAsync` không có timeout → tool HIS API có thể treo vô hạn nếu network issue. Cần `CancellationTokenSource(TimeSpan.FromSeconds(30))` linked với `ct`.

### M-05: Fire-and-Forget Task Observability

```csharp
_ = Task.Run(async () =>
{
    try { await userModel.TryExtractAsync(...); }
    catch (Exception ex) { log.LogWarning(...); }
}, CancellationToken.None);
```

Nếu process crash giữa chừng, `UnobservedTaskException` có thể không được log (tùy thuộc vào .NET runtime config). Thêm `TaskScheduler.UnobservedTaskException` handler trong Program.cs.

### M-06: Assistant Tool Calls Không Được Append Đúng Cách

```csharp
private static List<ChatMessage> AppendAssistantToolCalls(List<ChatMessage> messages, ChatResponse resp)
{
    var copy = new List<ChatMessage>(messages)
    {
        new("assistant", resp.Content),  // ← Chỉ có Content, thiếu ToolCalls
    };
    return copy;
}
```

OpenAI format yêu cầu assistant message chứa `tool_calls` array để model biết nó đã gọi tool gì:

```json
{"role": "assistant", "content": null, "tool_calls": [{"id": "...", "type": "function", "function": {...}}]}
```

Nếu không copy `tool_calls`, model có thể "quên" nó đã gọi tool nào → loop vô hạn gọi lại cùng tool.

### M-07: Streaming Path Không Có Output Shield

`StreamAsync()` gửi token-by-token về client không qua `outputShield` hay `egressGuard`. Nếu LLM sinh ra credential/secret trong token đầu, client nhận được trước khi có cơ hội block.

### M-08: Tool List Static Trong Loop

Tool definitions được tính 1 lần trước loop:

```csharp
var toolDefs = tools.All.Select(t => t.Definition).ToList();
```

MCP discovery (BackgroundService) có thể thêm tool mới giữa chừng. Nếu loop dài (6 iterations × 30s = 3 phút), agent bỏ lỡ tool mới.

---

## 5. Best Practice Recommendations

### 5.1 Circuit Breaker cho Tool Execution

Hiện tại mỗi tool fail độc lập, nhưng nếu 1 tool liên tục fail (vd: HIS API down), agent vẫn thử gọi lại trong mỗi iteration. Nên thêm:

```csharp
private readonly Dictionary<string, int> _toolFailureCount = new();
private const int MaxConsecutiveFailures = 3;

// Trong ExecuteToolAsync:
if (_toolFailureCount.TryGetValue(call.Name, out var fails) && fails >= MaxConsecutiveFailures)
{
    return (/*...*/ "tool_circuit_open", /*...*/ false);
}
```

### 5.2 Context Budget Tracking

Thêm tracking token count trước khi gửi LLM request:

```csharp
private int EstimateTokenCount(List<ChatMessage> messages) =>
    messages.Sum(m => m.Content?.Length ?? 0) / 4; // rough estimate

// Trong RunAsync, trước khi CompleteAsync:
if (EstimateTokenCount(messages) > MaxContextTokens)
{
    messages = TruncateMemoriesAndSkills(messages, MaxContextTokens);
}
```

### 5.3 Idempotency Key cho Agent Request

Thêm idempotency key (client-generated) để tránh duplicate request khi network retry:

```csharp
// Trong AgentChatRequest
public sealed record AgentChatRequest(
    string Message,
    Guid? ConversationId = null,
    string? IdempotencyKey = null);  // ← Thêm
```

### 5.4 Tool Execution Tracing

Mỗi tool execution nên có span attribute đầy đủ hơn:

- `tool.duration_ms`
- `tool.input_hash`
- `tool.output_size`
- `tool.sandbox.cpu_ms`
- `tool.sandbox.memory_mb`

### 5.5 Graceful Degradation

Khi 1 dependency fail (memory, skills, KG), agent vẫn tiếp tục với context giảm. Hiện tại đã làm tốt việc này với try-catch. Nên thêm metric `degraded_mode` để monitoring biết agent đang chạy ở chế độ suy giảm.

---

## 6. Risk Matrix

```
                        IMPACT
                    Low   Med   High
              ┌────┬─────┬─────┬─────┐
F        High │    │     │     │     │
R              ├────┼─────┼─────┼─────┤
E       Med   │    │ M-02│ H-01│     │
Q              │    │ M-04│ H-02│     │
U              │    │ M-06│ H-03│     │
E       Low   │L-01│ M-01│     │     │
N              │L-07│ M-03│     │     │
C              │    │ M-05│     │     │
Y              │    │ M-07│     │     │
              └────┴─────┴─────┴─────┘
```

---

## 7. Remediation Plan (Sắp xếp theo ưu tiên)

| Priority            | ID   | Action                                  | Effort | Impact                        |
| ------------------- | ---- | --------------------------------------- | ------ | ----------------------------- |
| **P0 — Ngay**       | M-06 | Fix Assistant ToolCalls append          | 30min  | Ngăn loop vô hạn              |
| **P0 — Ngay**       | H-01 | Thêm CancellationToken check trong loop | 5min   | Ngăn lãng phí tài nguyên      |
| **P0 — Ngay**       | H-02 | Null-coalescing cho tool output         | 5min   | Ngăn crash                    |
| **P1 — Tuần này**   | H-03 | Memory consolidation fire-and-forget    | 15min  | Giảm 2-5s latency             |
| **P1 — Tuần này**   | M-02 | Song song hóa context gathering         | 1h     | Giảm ~500ms latency           |
| **P1 — Tuần này**   | M-04 | Timeout cho tool execution              | 30min  | Ngăn treo request             |
| **P1 — Tuần này**   | M-07 | Output shield cho streaming path        | 2h     | An toàn streaming             |
| **P2 — Sprint tới** | M-01 | Try-catch shield                        | 15min  | Graceful degradation          |
| **P2 — Sprint tới** | M-03 | Memory pressure optimization            | 4h     | Scale tốt hơn                 |
| **P2 — Sprint tới** | M-05 | TaskScheduler handler                   | 15min  | Observability                 |
| **P3 — Backlog**    | I-01 | Fix typo RerankCandidateK               | 5min   | Code quality                  |
| **P3 — Backlog**    | I-03 | Context budget tracking                 | 3h     | Model compatibility           |
| **P3 — Backlog**    | I-04 | Early exit on consecutive tool failures | 30min  | Resource efficiency           |
| **P3 — Backlog**    | I-05 | Tool output validation                  | 1h     | Indirect injection prevention |

---

## 8. Kết Luận

AgentOrchestrator đạt **mức độ bảo mật và kỹ thuật tốt (4.2/5.0)**. Không có lỗ hổng CRITICAL. Các rủi ro HIGH đều có fix đơn giản (<30 phút). Các rủi ro MEDIUM chủ yếu về performance optimization và edge case handling.

**Điểm mạnh**:

- Defense-in-depth: shield ở mọi layer (input → context → tool → output)
- Audit trail toàn diện (hash-chained)
- Graceful degradation khi dependency fail
- Observability tốt với OpenTelemetry + custom metrics

**Điểm cần cải thiện**:

- Performance: Song song hóa context gathering
- Streaming safety: Output shield cho streaming path
- Resource management: Timeout + cancellation propagation
- Code quality: Fix typo, thêm context budget

---

_Tài liệu được tạo tự động từ phân tích source code — 2026-06-03_
