using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Rag;
using Hope.Agent.Application.Tools;

namespace Hope.Agent.Tools;

// Demonstrates platform reuse for the Logistics industry.
// Same IAgentTool contract as HealthcareTools — no Runtime/Router/Memory/Security changes needed.

public sealed class TrackShipmentTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "track_shipment",
        "Look up the latest status, last-mile ETA and current location of a shipment.",
        """
        {
          "type": "object",
          "properties": {
            "tracking_number": {"type": "string"},
            "carrier": {"type": "string", "description": "Optional: GHN, GHTK, J&T, VNPost, DHL, FedEx, UPS"}
          },
          "required": ["tracking_number"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var result = JsonSerializer.Serialize(new
        {
            tracking_number = args.GetProperty("tracking_number").GetString(),
            carrier = args.TryGetProperty("carrier", out var c) ? c.GetString() : "GHN",
            status = "in_transit",
            current_hub = "HCM-Sorting-Center",
            next_hub = "HN-Last-Mile",
            eta_iso = DateTime.UtcNow.AddHours(28).ToString("O"),
            updated_at = DateTime.UtcNow.ToString("O"),
        });
        return Task.FromResult(result);
    }
}

public sealed class OptimizeRouteTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "optimize_delivery_route",
        "Compute an optimised multi-stop delivery sequence for a driver given pickup and drop-off coordinates.",
        """
        {
          "type": "object",
          "properties": {
            "driver_id": {"type": "string"},
            "vehicle_type": {"type": "string", "enum": ["motorbike", "van", "truck"]},
            "stops": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "stop_id": {"type": "string"},
                  "lat": {"type": "number"},
                  "lng": {"type": "number"},
                  "service_window_start": {"type": "string", "format": "date-time"},
                  "service_window_end": {"type": "string", "format": "date-time"}
                },
                "required": ["stop_id", "lat", "lng"]
              }
            }
          },
          "required": ["driver_id", "stops"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var stops = args.GetProperty("stops").EnumerateArray()
            .Select(s => s.GetProperty("stop_id").GetString()!)
            .ToList();
        var result = JsonSerializer.Serialize(new
        {
            driver_id = args.GetProperty("driver_id").GetString(),
            optimised_sequence = stops,
            estimated_total_minutes = stops.Count * 18,
            estimated_distance_km = stops.Count * 4.2,
            algorithm = "MCMF + nearest-neighbour seed",
        });
        return Task.FromResult(result);
    }
}

public sealed class WarehouseInventoryTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "query_warehouse_inventory",
        "Query stock levels of a SKU across one or more warehouses (WMS pass-through).",
        """
        {
          "type": "object",
          "properties": {
            "sku": {"type": "string"},
            "warehouse_codes": {"type": "array", "items": {"type": "string"}}
          },
          "required": ["sku"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var sku = args.GetProperty("sku").GetString();
        var warehouses = args.TryGetProperty("warehouse_codes", out var w)
            ? w.EnumerateArray().Select(x => x.GetString()).ToArray()
            : ["WH-HN-01", "WH-HCM-01", "WH-DN-01"];
        var result = JsonSerializer.Serialize(new
        {
            sku,
            levels = warehouses.Select(code => new
            {
                warehouse = code,
                on_hand = 142,
                reserved = 18,
                available = 124,
            }),
        });
        return Task.FromResult(result);
    }
}

public sealed class FreightQuoteTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "freight_quote",
        "Get a freight rate quote for moving a parcel or pallet between two locations.",
        """
        {
          "type": "object",
          "properties": {
            "origin": {"type": "string"},
            "destination": {"type": "string"},
            "weight_kg": {"type": "number"},
            "volume_m3": {"type": "number"},
            "service_level": {"type": "string", "enum": ["economy", "standard", "express"]}
          },
          "required": ["origin", "destination", "weight_kg"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var weight = args.GetProperty("weight_kg").GetDouble();
        var service = args.TryGetProperty("service_level", out var s) ? s.GetString() : "standard";
        var multiplier = service switch { "express" => 1.8, "economy" => 0.7, _ => 1.0 };
        var result = JsonSerializer.Serialize(new
        {
            origin = args.GetProperty("origin").GetString(),
            destination = args.GetProperty("destination").GetString(),
            weight_kg = weight,
            service_level = service,
            price_vnd = (long)Math.Round(25_000 + weight * 8_500 * multiplier),
            estimated_transit_days = service == "express" ? 1 : service == "economy" ? 5 : 3,
            quote_id = Guid.NewGuid().ToString("N")[..10],
        });
        return Task.FromResult(result);
    }
}

public sealed class CustomsClassifyTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "classify_customs_hs_code",
        "Suggest an HS code (Harmonised System) and customs duty estimate for an item description.",
        """
        {
          "type": "object",
          "properties": {
            "item_description": {"type": "string"},
            "country_of_origin": {"type": "string"},
            "declared_value_usd": {"type": "number"}
          },
          "required": ["item_description"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var result = JsonSerializer.Serialize(new
        {
            item = args.GetProperty("item_description").GetString(),
            suggested_hs_code = "8517.13.00",
            confidence = 0.82,
            duty_rate_percent = 5.0,
            note = "Verify against the latest Vietnam customs schedule before filing.",
        });
        return Task.FromResult(result);
    }
}

public sealed class LogisticsSopSearchTool(IRetriever retriever) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "search_logistics_sop",
        "Search internal logistics SOPs, customs procedures and carrier handbooks. Returns top relevant excerpts.",
        """
        {
          "type": "object",
          "properties": {
            "query": {"type": "string"},
            "top_k": {"type": "integer", "default": 5}
          },
          "required": ["query"]
        }
        """);

    public async Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var query = args.GetProperty("query").GetString() ?? string.Empty;
        var topK = args.TryGetProperty("top_k", out var k) ? k.GetInt32() : 5;
        var hits = await retriever.SearchAsync(
            new RetrievalQuery(query, "logistics_sop", TopK: Math.Max(topK, 4) * 2, FinalK: topK), ct);
        return JsonSerializer.Serialize(new
        {
            query,
            hits = hits.Select(h => new
            {
                title = h.Title,
                url = h.Url,
                score = h.Score,
                content = h.Content,
            }),
        });
    }
}
