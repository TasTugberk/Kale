using Auth.Contracts.V1;
using AuthService.Persistence;
using AuthService.Sessions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Grpc;

public sealed class SessionGrpcService(AuthDbContext db, SessionCheck check, SessionStopper stopper) : SessionService.SessionServiceBase
{
    /// <summary>
    /// An application ends one of its own sessions (its user signed out), from its server: the browser may never
    /// reach connect/endsession, or arrive there without a valid Kale cookie. Ended through SessionStopper, so the
    /// "session stopped" event is in the same save; an already ended session stays as it was.
    /// </summary>
    public override async Task<EndSessionResponse> EndSession(EndSessionRequest request, ServerCallContext context)
    {
        var caller = await context.RequireActiveApplicationAsync(db);
        var sessionId = AppCaller.ParseId(request.SessionId, "session_id");

        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.ApplicationId == caller.Id, context.CancellationToken)
            ?? throw AppCaller.NotFound("Session");

        if (stopper.Stop(session))
        {
            await db.SaveChangesAsync(context.CancellationToken);
        }

        return new EndSessionResponse();
    }

    public override async Task<GetSessionResponse> GetSession(GetSessionRequest request, ServerCallContext context)
    {
        var caller = await context.RequireActiveApplicationAsync(db);
        var sessionId = AppCaller.ParseId(request.SessionId, "session_id");

        var session = await db.Sessions
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.ApplicationId == caller.Id, context.CancellationToken)
            ?? throw AppCaller.NotFound("Session");

        return new GetSessionResponse
        {
            Status = await check.IsActiveAsync(session, context.CancellationToken) ? SessionStatus.Active : SessionStatus.Ended,
            RoleId = session.RoleId.ToString(),
            UserId = session.UserId.ToString(),
            ExpiresAt = Timestamp.FromDateTimeOffset(session.ExpiresAt),
        };
    }
}
