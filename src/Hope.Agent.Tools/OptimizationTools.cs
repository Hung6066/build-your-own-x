using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Tools;

namespace Hope.Agent.Tools;

/// <summary>
/// Optimization tools that apply graph/scheduling algorithms on top of base HIS tools.
///
/// Algorithms used:
///   - <see cref="OptimizeBatchAppointmentsTool"/>  : Min-Cost Max-Flow (Successive Shortest Paths + SPFA)
///   - <see cref="RankTriagePatientsTool"/>          : Weighted multi-criteria scoring (EDF-inspired)
///   - <see cref="ThrottleNotificationsTool"/>       : Token-bucket rate limiting
/// </summary>

// ── Min-Cost Max-Flow: Batch Appointment Optimizer ───────────────────────────

/// <summary>
/// Phân bổ tối ưu nhiều bệnh nhân vào các slot khả dụng bằng Min-Cost Max-Flow.
///
/// Graph:
///   source(0) → patient_node(i)  : capacity=1, cost=0
///   patient_node(i) → slot_node(j): capacity=1, cost = wait_penalty + specialty_mismatch
///   slot_node(j) → sink(N)       : capacity=1, cost=0
///
/// Đầu vào JSON:
/// {
///   "requests": [
///     { "patient_id": "P001", "specialty": "Tim mạch", "urgency": "high", "preferred_time_iso": "2025-07-01T09:00:00Z" }
///   ],
///   "slots": [
///     { "slot_id": "S1", "doctor_id": "DR001", "specialty": "Tim mạch", "time_iso": "2025-07-01T09:00:00Z", "room": "P.201" }
///   ]
/// }
///
/// Đầu ra: danh sách assignment { patient_id, slot_id, doctor_id, cost } + tổng min_cost.
/// </summary>
public sealed class OptimizeBatchAppointmentsTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "optimize_batch_appointments",
        "Assigns multiple patients to available slots using Min-Cost Max-Flow to minimize total wait time and specialty mismatch penalties.",
        """
        {
          "type": "object",
          "properties": {
            "requests": {
              "type": "array",
              "description": "List of patient booking requests",
              "items": {
                "type": "object",
                "properties": {
                  "patient_id":          {"type": "string"},
                  "specialty":           {"type": "string"},
                  "urgency":             {"type": "string", "enum": ["critical","high","medium","low"]},
                  "preferred_time_iso":  {"type": "string", "description": "ISO-8601 preferred appointment time"}
                },
                "required": ["patient_id", "specialty"]
              }
            },
            "slots": {
              "type": "array",
              "description": "List of available appointment slots",
              "items": {
                "type": "object",
                "properties": {
                  "slot_id":    {"type": "string"},
                  "doctor_id":  {"type": "string"},
                  "specialty":  {"type": "string"},
                  "time_iso":   {"type": "string"}
                },
                "required": ["slot_id", "doctor_id", "specialty", "time_iso"]
              }
            }
          },
          "required": ["requests", "slots"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;

        var requests = ParseRequests(args.GetProperty("requests"));
        var slots = ParseSlots(args.GetProperty("slots"));

        if (requests.Count == 0 || slots.Count == 0)
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                assignments = Array.Empty<object>(),
                total_min_cost = 0,
                unassigned_patients = requests.Select(r => r.PatientId).ToArray(),
                algorithm = "min-cost-max-flow",
            }));
        }

        var (assignments, totalCost) = SolveMinCostMaxFlow(requests, slots);

        var assignedIds = assignments.Select(a => a.PatientId).ToHashSet();
        var unassigned = requests.Where(r => !assignedIds.Contains(r.PatientId))
                                 .Select(r => r.PatientId)
                                 .ToArray();

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            assignments = assignments.Select(a => new
            {
                patient_id = a.PatientId,
                slot_id = a.SlotId,
                doctor_id = a.DoctorId,
                specialty = a.SlotSpecialty,
                time_iso = a.TimeIso,
                cost = a.Cost,
            }).ToArray(),
            total_min_cost = totalCost,
            unassigned_patients = unassigned,
            algorithm = "min-cost-max-flow",
            solver = "successive-shortest-paths-spfa",
            optimized_at = DateTimeOffset.UtcNow.ToString("O"),
        }));
    }

    // ── Domain models ─────────────────────────────────────────────────────────

    private sealed record PatientRequest(
        string PatientId,
        string Specialty,
        string Urgency,
        DateTimeOffset? PreferredTime);

    private sealed record SlotInfo(
        string SlotId,
        string DoctorId,
        string Specialty,
        DateTimeOffset Time);

    private sealed record Assignment(
        string PatientId,
        string SlotId,
        string DoctorId,
        string SlotSpecialty,
        string TimeIso,
        int Cost);

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static List<PatientRequest> ParseRequests(JsonElement arr)
    {
        var list = new List<PatientRequest>();
        foreach (var el in arr.EnumerateArray())
        {
            DateTimeOffset? pref = null;
            if (el.TryGetProperty("preferred_time_iso", out var pt) && pt.GetString() is { } pts)
                _ = DateTimeOffset.TryParse(pts, out var parsed) ? (pref = parsed) : pref;

            list.Add(new PatientRequest(
                PatientId: el.GetProperty("patient_id").GetString()!,
                Specialty: el.GetProperty("specialty").GetString()!,
                Urgency: el.TryGetProperty("urgency", out var u) ? u.GetString() ?? "medium" : "medium",
                PreferredTime: pref));
        }
        return list;
    }

    private static List<SlotInfo> ParseSlots(JsonElement arr)
    {
        var list = new List<SlotInfo>();
        foreach (var el in arr.EnumerateArray())
        {
            if (!DateTimeOffset.TryParse(el.GetProperty("time_iso").GetString(), out var t))
                t = DateTimeOffset.UtcNow;

            list.Add(new SlotInfo(
                SlotId: el.GetProperty("slot_id").GetString()!,
                DoctorId: el.GetProperty("doctor_id").GetString()!,
                Specialty: el.GetProperty("specialty").GetString()!,
                Time: t));
        }
        return list;
    }

    // ── Min-Cost Max-Flow (Successive Shortest Paths with SPFA) ──────────────
    //
    // Node layout:
    //   0           = source
    //   1..P        = patient nodes  (P = requests.Count)
    //   P+1..P+S    = slot nodes     (S = slots.Count)
    //   P+S+1       = sink
    //
    // Edges:
    //   source → patient_i : cap=1, cost=0
    //   patient_i → slot_j : cap=1, cost=ComputeEdgeCost(request_i, slot_j)
    //   slot_j → sink      : cap=1, cost=0

    private static (List<Assignment> Assignments, int TotalCost) SolveMinCostMaxFlow(
        List<PatientRequest> requests,
        List<SlotInfo> slots)
    {
        int p = requests.Count;
        int s = slots.Count;
        int n = p + s + 2;   // total nodes
        int source = 0;
        int sink = p + s + 1;

        // Adjacency list of edge indices
        var graph = new List<int>[n];
        for (int i = 0; i < n; i++) graph[i] = [];

        var cap = new List<int>();
        var cost = new List<int>();
        var to = new List<int>();

        void AddEdge(int u, int v, int c, int w)
        {
            graph[u].Add(cap.Count);
            cap.Add(c); cost.Add(w); to.Add(v);
            graph[v].Add(cap.Count);
            cap.Add(0); cost.Add(-w); to.Add(u);   // reverse edge
        }

        // source → patient nodes
        for (int i = 0; i < p; i++)
            AddEdge(source, i + 1, 1, 0);

        // patient → slot edges
        for (int i = 0; i < p; i++)
        {
            for (int j = 0; j < s; j++)
            {
                int edgeCost = ComputeEdgeCost(requests[i], slots[j]);
                AddEdge(i + 1, p + j + 1, 1, edgeCost);
            }
        }

        // slot nodes → sink
        for (int j = 0; j < s; j++)
            AddEdge(p + j + 1, sink, 1, 0);

        // Run Successive Shortest Paths using SPFA (Bellman-Ford queue variant)
        int totalFlow = 0;
        int totalCost = 0;
        int maxFlow = Math.Min(p, s);

        while (totalFlow < maxFlow)
        {
            // SPFA to find shortest (min-cost) augmenting path from source to sink
            var dist = new int[n];
            Array.Fill(dist, int.MaxValue);
            dist[source] = 0;

            var inQueue = new bool[n];
            var prev = new int[n];     // prev node
            var prevEdge = new int[n]; // prev edge index
            Array.Fill(prev, -1);
            Array.Fill(prevEdge, -1);

            var queue = new Queue<int>();
            queue.Enqueue(source);
            inQueue[source] = true;

            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                inQueue[u] = false;

                foreach (int eid in graph[u])
                {
                    int v = to[eid];
                    if (cap[eid] > 0 && dist[u] != int.MaxValue && dist[u] + cost[eid] < dist[v])
                    {
                        dist[v] = dist[u] + cost[eid];
                        prev[v] = u;
                        prevEdge[v] = eid;
                        if (!inQueue[v])
                        {
                            queue.Enqueue(v);
                            inQueue[v] = true;
                        }
                    }
                }
            }

            if (dist[sink] == int.MaxValue) break; // no more augmenting paths

            // Augment along the path
            int cur = sink;
            while (cur != source)
            {
                int eid = prevEdge[cur];
                cap[eid]--;
                cap[eid ^ 1]++;
                cur = prev[cur];
            }

            totalFlow++;
            totalCost += dist[sink];
        }

        // Extract assignments by scanning used patient→slot edges (cap went from 1→0)
        var assignments = new List<Assignment>();
        for (int i = 0; i < p; i++)
        {
            foreach (int eid in graph[i + 1])
            {
                // Original forward edge: was cap=1, now cap=0 means it was used
                int v = to[eid];
                if (v >= p + 1 && v <= p + s && cap[eid] == 0 && cost[eid] >= 0)
                {
                    int j = v - p - 1;
                    assignments.Add(new Assignment(
                        PatientId: requests[i].PatientId,
                        SlotId: slots[j].SlotId,
                        DoctorId: slots[j].DoctorId,
                        SlotSpecialty: slots[j].Specialty,
                        TimeIso: slots[j].Time.ToString("O"),
                        Cost: cost[eid]));
                    break;
                }
            }
        }

        return (assignments, totalCost);
    }

    /// <summary>
    /// Hàm chi phí cạnh patient_i → slot_j.
    /// Chi phí thấp hơn = ưu tiên hơn.
    ///
    /// Thành phần:
    ///   - Mismatch chuyên khoa : +100 nếu specialty khác nhau
    ///   - Độ chênh giờ hẹn    : +1 cho mỗi 15 phút lệch so với preferred_time
    ///   - Urgency discount     : -20 cho critical, -10 cho high (ưu tiên slot sớm)
    /// </summary>
    private static int ComputeEdgeCost(PatientRequest req, SlotInfo slot)
    {
        int c = 0;

        // Specialty mismatch penalty
        if (!string.Equals(req.Specialty, slot.Specialty, StringComparison.OrdinalIgnoreCase))
            c += 100;

        // Wait time penalty: deviation from preferred time (in 15-min units)
        if (req.PreferredTime.HasValue)
        {
            double diffMinutes = Math.Abs((slot.Time - req.PreferredTime.Value).TotalMinutes);
            c += (int)(diffMinutes / 15);
        }

        // Urgency discount
        c -= req.Urgency switch
        {
            "critical" => 20,
            "high" => 10,
            _ => 0,
        };

        return Math.Max(0, c); // prevent negative costs
    }
}

// ── Weighted Triage Ranking ───────────────────────────────────────────────────

/// <summary>
/// Xếp hạng nhiều bệnh nhân theo thứ tự ưu tiên phục vụ bằng weighted multi-criteria scoring.
///
/// Công thức:
///   score = w_severity * severity_score
///         + w_wait    * (1 / (wait_minutes + 1))
///         + w_risk    * risk_score
///         - w_resource * resource_load
///
/// Bệnh nhân có score cao nhất được phục vụ trước (EDF-inspired).
/// </summary>
public sealed class RankTriagePatientsTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "rank_triage_patients",
        "Ranks a list of triage patients by optimized priority score using weighted multi-criteria scheduling (EDF-inspired). Returns ordered list with scores.",
        """
        {
          "type": "object",
          "properties": {
            "patients": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "patient_id":      {"type": "string"},
                  "severity":        {"type": "string", "enum": ["critical","severe","moderate","mild"]},
                  "wait_minutes":    {"type": "number", "description": "Minutes already waited"},
                  "risk_flags":      {"type": "array", "items": {"type": "string"}, "description": "e.g. [\"chest_pain\",\"oxygen_below_90\"]"},
                  "resource_load":   {"type": "number", "description": "0.0-1.0 current load of the care resource needed"}
                },
                "required": ["patient_id", "severity"]
              }
            },
            "weights": {
              "type": "object",
              "description": "Optional custom weights (all default to 1.0)",
              "properties": {
                "severity": {"type": "number"},
                "wait":     {"type": "number"},
                "risk":     {"type": "number"},
                "resource": {"type": "number"}
              }
            }
          },
          "required": ["patients"]
        }
        """);

    private static readonly Dictionary<string, int> SeverityScore = new(StringComparer.OrdinalIgnoreCase)
    {
        ["critical"] = 100,
        ["severe"] = 75,
        ["moderate"] = 40,
        ["mild"] = 10,
    };

    private static readonly HashSet<string> HighRiskFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "chest_pain", "oxygen_below_90", "unconscious", "stroke_symptoms",
        "sepsis", "anaphylaxis", "gi_bleeding",
    };

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;

        double wSeverity = 1.0, wWait = 1.0, wRisk = 1.0, wResource = 1.0;
        if (args.TryGetProperty("weights", out var w))
        {
            if (w.TryGetProperty("severity", out var ws)) wSeverity = ws.GetDouble();
            if (w.TryGetProperty("wait", out var ww)) wWait = ww.GetDouble();
            if (w.TryGetProperty("risk", out var wr)) wRisk = wr.GetDouble();
            if (w.TryGetProperty("resource", out var wres)) wResource = wres.GetDouble();
        }

        var ranked = args.GetProperty("patients")
            .EnumerateArray()
            .Select(p =>
            {
                var id = p.GetProperty("patient_id").GetString()!;
                var severity = p.GetProperty("severity").GetString()!;
                var waitMin = p.TryGetProperty("wait_minutes", out var wm) ? wm.GetDouble() : 0;
                var resourceLoad = p.TryGetProperty("resource_load", out var rl) ? rl.GetDouble() : 0;

                var flags = p.TryGetProperty("risk_flags", out var rf)
                    ? rf.EnumerateArray().Select(f => f.GetString()!).ToArray()
                    : [];

                int sevScore = SeverityScore.TryGetValue(severity, out var ss) ? ss : 0;
                int riskBonus = flags.Count(f => HighRiskFlags.Contains(f)) * 15;
                double waitScore = 1.0 / (waitMin + 1) * 100; // normalized

                double score = wSeverity * sevScore
                             + wWait * waitScore
                             + wRisk * riskBonus
                             - wResource * (resourceLoad * 20);

                return new
                {
                    patient_id = id,
                    severity,
                    priority_score = Math.Round(score, 2),
                    breakdown = new
                    {
                        severity_contribution = Math.Round(wSeverity * sevScore, 2),
                        wait_contribution = Math.Round(wWait * waitScore, 2),
                        risk_contribution = Math.Round(wRisk * riskBonus, 2),
                        resource_penalty = Math.Round(wResource * resourceLoad * 20, 2),
                    },
                    active_risk_flags = flags.Where(f => HighRiskFlags.Contains(f)).ToArray(),
                };
            })
            .OrderByDescending(p => p.priority_score)
            .Select((p, idx) => new { rank = idx + 1, p.patient_id, p.severity, p.priority_score, p.breakdown, p.active_risk_flags })
            .ToArray();

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            ranked_patients = ranked,
            total = ranked.Length,
            algorithm = "weighted-multi-criteria-edf",
            scored_at = DateTimeOffset.UtcNow.ToString("O"),
        }));
    }
}

// ── Notification Token Bucket ─────────────────────────────────────────────────

/// <summary>
/// Áp dụng token-bucket rate limiting để quyết định notification nào được gửi ngay,
/// notification nào bị throttle (delay), và notification nào bị drop.
///
/// Mỗi (patient_id, channel) có bucket riêng. Mỗi bucket có:
///   - capacity   : số token tối đa (= burst limit)
///   - refill_rate: token/phút
///   - current    : token hiện tại (truyền vào từ caller hoặc mặc định = capacity)
/// </summary>
public sealed class ThrottleNotificationsTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "throttle_notifications",
        "Applies token-bucket rate limiting to a batch of notifications, returning send/delay/drop decisions per notification.",
        """
        {
          "type": "object",
          "properties": {
            "notifications": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "notification_id": {"type": "string"},
                  "patient_id":      {"type": "string"},
                  "channel":         {"type": "string", "enum": ["sms","email","push","in_app"]},
                  "urgency":         {"type": "string", "enum": ["critical","high","medium","low"]},
                  "message":         {"type": "string"}
                },
                "required": ["notification_id", "patient_id", "channel", "urgency"]
              }
            },
            "bucket_config": {
              "type": "object",
              "description": "Optional per-channel bucket config. Defaults: sms={capacity:5,refill_rate:2}, email={capacity:20,refill_rate:10}, push={capacity:10,refill_rate:5}",
              "properties": {
                "sms":    {"type": "object", "properties": {"capacity": {"type":"number"}, "refill_rate": {"type":"number"}}},
                "email":  {"type": "object", "properties": {"capacity": {"type":"number"}, "refill_rate": {"type":"number"}}},
                "push":   {"type": "object", "properties": {"capacity": {"type":"number"}, "refill_rate": {"type":"number"}}},
                "in_app": {"type": "object", "properties": {"capacity": {"type":"number"}, "refill_rate": {"type":"number"}}}
              }
            }
          },
          "required": ["notifications"]
        }
        """);

    private static readonly Dictionary<string, (int Capacity, int RefillRate)> DefaultConfig = new()
    {
        ["sms"] = (5, 2),
        ["email"] = (20, 10),
        ["push"] = (10, 5),
        ["in_app"] = (50, 20),
    };

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;

        // Parse channel config overrides
        var config = new Dictionary<string, (int Capacity, int RefillRate)>(DefaultConfig);
        if (args.TryGetProperty("bucket_config", out var bc))
        {
            foreach (var channel in new[] { "sms", "email", "push", "in_app" })
            {
                if (bc.TryGetProperty(channel, out var ch))
                {
                    int cap = ch.TryGetProperty("capacity", out var ca) ? ca.GetInt32() : config[channel].Capacity;
                    int rate = ch.TryGetProperty("refill_rate", out var rr) ? rr.GetInt32() : config[channel].RefillRate;
                    config[channel] = (cap, rate);
                }
            }
        }

        // Simulate token buckets per (patient_id, channel) — start full
        var buckets = new Dictionary<string, int>();

        var results = new List<object>();

        foreach (var n in args.GetProperty("notifications").EnumerateArray())
        {
            var notifId = n.GetProperty("notification_id").GetString()!;
            var patId = n.GetProperty("patient_id").GetString()!;
            var channel = n.GetProperty("channel").GetString()!;
            var urgency = n.GetProperty("urgency").GetString()!;

            var bucketKey = $"{patId}:{channel}";
            if (!buckets.TryGetValue(bucketKey, out _))
                buckets[bucketKey] = config.TryGetValue(channel, out var cfg) ? cfg.Capacity : 5;

            int tokens = buckets[bucketKey];
            string decision;
            string? delayReason = null;

            if (urgency == "critical")
            {
                // Critical notifications bypass throttle — always send
                decision = "send";
            }
            else if (tokens >= 1)
            {
                buckets[bucketKey] = tokens - 1;
                decision = "send";
            }
            else if (urgency == "high")
            {
                // High urgency: delay instead of drop
                decision = "delay";
                delayReason = $"Token bucket empty for {channel}. Retry after next refill cycle.";
            }
            else
            {
                // medium/low with no tokens → drop
                decision = "drop";
                delayReason = $"Rate limit exceeded for {channel}. Non-urgent notification dropped.";
            }

            results.Add(new
            {
                notification_id = notifId,
                patient_id = patId,
                channel,
                urgency,
                decision,
                reason = delayReason,
                tokens_remaining = buckets.GetValueOrDefault(bucketKey, 0),
            });
        }

        var sentCount = results.Count(r => ((dynamic)r).decision == "send");
        var delayCount = results.Count(r => ((dynamic)r).decision == "delay");
        var dropCount = results.Count(r => ((dynamic)r).decision == "drop");

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            decisions = results,
            summary = new { sent = sentCount, delayed = delayCount, dropped = dropCount },
            algorithm = "token-bucket",
            processed_at = DateTimeOffset.UtcNow.ToString("O"),
        }));
    }
}
