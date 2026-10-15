using Auth.Contracts.V1;
using AuthService.Persistence;
using AuthService.Sessions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Grpc;

public sealed class SessionGrpcService(AuthDbContext db, SessionCheck check) : SessionService.SessionServiceBase
{
    public override async Task<GetSessionResponse> GetSession(GetSessionRequest request, ServerCallContext context)
    {
        var caller = await context.RequireActiveApplicationAsync(db);
        var sessionId = AppCaller.ParseId(request.SessionId, "session_id");

        var session = await db.Sessions
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
