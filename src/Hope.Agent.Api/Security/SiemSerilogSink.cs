using Hope.Agent.Infrastructure.Security;

namespace Hope.Agent.Api.Security;

/// <summary>
/// Serilog sink adapter for SiemSink — lives in the API layer where Serilog is available.
/// Maps security-relevant log events to CEF format and enqueues them via SiemSink.Fire().
/// Closes gap H-3.
/// </summary>
public sealed class SiemSerilogSink : Serilog.Core.ILogEventSink
{
    private readonly SiemSink _sink;

    public SiemSerilogSink(SiemSink sink)
    {
        _sink = sink;
    }

    public void Emit(Serilog.Events.LogEvent logEvent)
    {
        if (!IsSecurityEvent(logEvent)) return;

        var (sigId, sigName) = GetSignature(logEvent);
        var severity = GetCefSeverity(logEvent.Level);

        _sink.Fire(sigId, sigName, severity);
    }

    private static bool IsSecurityEvent(Serilog.Events.LogEvent evt)
    {
        var template = evt.MessageTemplate.Text;
        return template.Contains("auth.login") ||
               template.Contains("tool_access") ||
               template.Contains("prompt.blocked") ||
               template.Contains("egress.blocked") ||
               template.Contains("audit.chain") ||
               template.Contains("authz.tenant.denied") ||
               template.Contains("injection");
    }

    private static (string id, string name) GetSignature(Serilog.Events.LogEvent evt)
    {
        var template = evt.MessageTemplate.Text;
        if (template.Contains("auth.login.failed")) return ("1001", "Brute Force Attempt");
        if (template.Contains("tool_access_denied")) return ("2001", "Insider Threat");
        if (template.Contains("prompt.blocked")) return ("3001", "Jailbreak Attempt");
        if (template.Contains("egress.blocked")) return ("4001", "Data Exfiltration Attempt");
        if (template.Contains("audit.chain.verification_failed")) return ("5001", "Tamper Evidence");
        if (template.Contains("authz.tenant.denied")) return ("1002", "Cross-Tenant Access Attempt");
        if (template.Contains("injection")) return ("3002", "Injection Attempt");
        return ("9999", "Security Event");
    }

    private static string GetCefSeverity(Serilog.Events.LogEventLevel level) => level switch
    {
        Serilog.Events.LogEventLevel.Fatal => "10",
        Serilog.Events.LogEventLevel.Error => "8",
        Serilog.Events.LogEventLevel.Warning => "5",
        _ => "3"
    };
}
