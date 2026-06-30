using System.Threading.Channels;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Ships security events to SIEM (Splunk/Sentinel) in CEF format.
/// Closes gap H-3. This is a fire-and-forget asynchronous message queue
/// with no Serilog dependency. The SiemSerilogSink (in API layer) adapts
/// Serilog log events into calls to this class.
///
/// Configure via appsettings: "Siem:Endpoint", "Siem:Token", "Siem:Enabled".
/// </summary>
public sealed class SiemSink : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _vendor;
    private readonly string _product;
    private readonly string _version;
    private readonly Channel<(string Cef, DateTimeOffset Timestamp)> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _pump;

    public SiemSink(HttpClient http, string endpoint, string vendor = "Hope.Agent", string product = "AI-Agent", string version = "1.0")
    {
        _http = http;
        _endpoint = endpoint;
        _vendor = vendor;
        _product = product;
        _version = version;
        _channel = Channel.CreateBounded<(string, DateTimeOffset)>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _cts = new CancellationTokenSource();
        _pump = PumpAsync(_cts.Token);
    }

    /// <summary>Fire-and-forget: enqueue a CEF event for async shipping to SIEM.</summary>
    public void Fire(string signatureId, string signatureName, string severity, string? extension = null)
    {
        var cef = $"CEF:0|{_vendor}|{_product}|{_version}|{signatureId}|{signatureName}|{severity}|{extension ?? string.Empty}";
        _channel.Writer.TryWrite((cef, DateTimeOffset.UtcNow));
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var batch = new List<(string Cef, DateTimeOffset)>(50);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                batch.Clear();
                while (batch.Count < 50 && _channel.Reader.TryRead(out var item))
                    batch.Add(item);

                if (batch.Count == 0)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                foreach (var (cef, _) in batch)
                {
                    await _http.PostAsync(_endpoint,
                        new StringContent(cef, System.Text.Encoding.UTF8, "text/plain"), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SIEM pump error: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.Complete();
        _cts.Dispose();
    }
}
