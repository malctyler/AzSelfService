# ADR 0002: Job Queue Pattern for Terraform Execution

**Date:** May 11, 2026  
**Status:** ACCEPTED  
**Context:** Designing Terraform execution model for self-service provisioning  

## Problem

When a user submits a deployment request, how should we execute Terraform?

**Two approaches:**

1. **Direct (Synchronous):** User submits form → API calls `terraform apply` → Returns result after 2-5 minutes
2. **Queue-Based (Asynchronous):** User submits form → API stores request → Worker polls queue → Executes Terraform asynchronously

## Decision

**We adopt Queue-Based Asynchronous Execution.**

## Rationale

### 1. HTTP Timeout Issues (Direct Approach Breaks)

**Direct execution problem:**
- `POST /api/deployments` → `terraform apply` takes 3 minutes
- HTTP client timeout (default: 30 seconds) fires → request hangs
- User thinks deployment failed, but actually it's still running

**Queue-based solution:**
- `POST /api/deployments` returns immediately with deployment ID
- Execution happens in background
- User polls status, sees progress in real-time

### 2. Auditability & Durability (Job Record)

**Direct execution:**
- Failure = no job record → hard to debug
- Retry = user must resubmit form (lossy)
- Audit trail incomplete

**Queue-based:**
- Every job in database with full history
- Logs, inputs, outputs all persisted
- Retry without user resubmission

### 3. Scalability (Multiple Workers)

**Direct execution:**
- Each API instance must run Terraform
- Resource-intensive, difficult to scale
- Terraform state locking gets complex

**Queue-based:**
- Decouple API from Terraform execution
- Multiple worker processes (future: scale to 10 workers)
- Natural job distribution

### 4. Future Features (Without Redesign)

Queue-based pattern enables (post-MVP, no redesign):

- **Approvals:** Request → Queue → Approval → Execute
- **Rate limiting:** Max 10 concurrent deployments per customer
- **Scheduled deployments:** "Deploy at 2 AM UTC"
- **Retries:** Automatic retry for transient failures
- **Prioritization:** VIP customers jump the queue

**Direct approach:** Would require complete rewrite to add these.

### 5. Error Recovery (Graceful Degradation)

**Scenario:** Worker crashes mid-deployment

**Direct approach:**
- Request lost, customer has no idea what happened
- Terraform state might be inconsistent

**Queue-based approach:**
- Job stays in queue with status=RUNNING
- Deployment data persists in database
- New worker picks up job, detects incomplete, retries or completes gracefully

---

## Design

```
User submits request
    ↓
API validates & stores job (status=QUEUED)
    ↓
API returns 202 ACCEPTED with deployment_id
    ↓
Frontend polls: GET /api/deployments/{id}
    ↓
HostedService polls database for QUEUED jobs
    ↓
Worker executes Terraform asynchronously
    ↓
Worker streams logs to DB
    ↓
Frontend displays logs in real-time
    ↓
On completion: status = SUCCEEDED | FAILED
```

### MVP Implementation

**Queue Storage:** PostgreSQL (simple, sufficient)
- Deployments table with status column
- HostedService polls every 5 seconds

**Worker:** Background service or separate container

**Not needed for MVP:**
- External queue (RabbitMQ, Service Bus)
- Message broker
- Complex retry logic

---

## Consequences

### Positive

✓ User gets immediate feedback (202 ACCEPTED)  
✓ Deployments are auditable and retryable  
✓ Worker can crash without losing job  
✓ Easy to scale (add more workers)  
✓ Real-time log streaming  
✓ Foundation for approvals, rate limiting, scheduling  
✓ Better error handling and recovery  

### Negative

✗ Added complexity (queue + worker + polling)  
✗ Requires database query polling (not as elegant as message broker)  
✗ User needs to poll for status (not push notifications, but polling is fine for MVP)  
✗ Potential for orphaned jobs (handled by timeout logic)  

---

## Alternatives Considered

### Alternative 1: Direct Synchronous Execution

```csharp
[HttpPost("/api/deployments")]
public IActionResult CreateDeployment(CreateDeploymentRequest request)
{
    var result = ExecuteTerraform(request); // Blocks for 2-5 minutes
    return Ok(result);
}
```

**Why rejected:**
- HTTP timeout issues
- No auditability
- Cannot scale
- No retry capability

### Alternative 2: Lambda/Function-Based Execution

```
POST /api/deployments → Trigger Azure Function → Function executes Terraform
```

**Why rejected (for MVP):**
- Added complexity with Functions runtime
- Cold start delays (might exceed customer expectations)
- Post-MVP: valid upgrade path (Workers → Container Apps Jobs → AKS)

### Alternative 3: Fire-and-Forget Without Persistence

```
POST /api/deployments → Start background task → Return immediately
(No database record)
```

**Why rejected:**
- No auditability
- If worker crashes, job is lost
- Cannot retry
- Cannot investigate failures

---

## Migration Path (Future)

**MVP:** PostgreSQL polling  
**Phase 2:** Optional upgrade to Service Bus / Event Grid (transparent to API)  
**Phase 3:** Container Apps Jobs or AKS for worker pools  

No breaking changes if we upgrade queue technology.

---

## Implementation Notes

### Database Polling (MVP)

```csharp
public class DeploymentJobService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var queuedDeployments = await _db.Deployments
                .Where(d => d.Status == DeploymentStatus.Queued)
                .OrderBy(d => d.CreatedAt)
                .Take(5)
                .ToListAsync();

            foreach (var deployment in queuedDeployments)
            {
                await _terraformExecutor.ExecuteAsync(deployment);
            }

            await Task.Delay(5000, stoppingToken); // Poll every 5 seconds
        }
    }
}
```

### Deployment Model

```csharp
public enum DeploymentStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public class Deployment
{
    public Guid Id { get; set; }
    public DeploymentStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
}
```

---

## References

- [Asynchronous Processing Pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/async-request-reply)
- [Background Tasks in .NET](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.backgroundworker)
- [Azure Service Bus for Job Queue](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-queues-topics-subscriptions)

