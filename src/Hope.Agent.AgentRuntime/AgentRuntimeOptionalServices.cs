using Hope.Agent.Application.Autonomy;
using Hope.Agent.Application.Context;
using Hope.Agent.Application.Locking;
using Hope.Agent.Application.Memory;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Billing;

namespace Hope.Agent.AgentRuntime;

internal sealed record AgentRuntimeOptionalServices(
    IClinicalContextProvider? ClinicalContext,
    IMemoryConsolidator? Consolidator,
    IMemoryReranker? Reranker,
    ITenantBillingService? Billing,
    IDistributedLock? DistributedLock,
    IAutonomyDecisionService? Autonomy,
    IContextProvenanceStore? ProvenanceStore);
