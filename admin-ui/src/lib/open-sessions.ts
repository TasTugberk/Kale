import { change, nothing, read, type ApiCall, type Outcome } from "./admin-requests";

/**
 * The admin API's open sessions (not ended, not expired): who is signed in where, with which role. Stopping one
 * takes effect at once; client apps are told through an event.
 */

export interface OpenSession {
  id: string;
  userId: string;
  userName: string;
  applicationKey: string;
  roleName: string;
  createdAt: string;
  expiresAt: string;
}

export interface SessionFilter {
  applicationId?: string;
  userId?: string;
}

export function listOpenSessions(call: ApiCall, filter: SessionFilter): Promise<OpenSession[]> {
  const query = new URLSearchParams();
  if (filter.applicationId) {
    query.set("applicationId", filter.applicationId);
  }

  if (filter.userId) {
    query.set("userId", filter.userId);
  }

  const search = query.toString();
  return read<OpenSession[]>(call, search ? `sessions?${search}` : "sessions");
}

export function stopSession(call: ApiCall, id: string): Promise<Outcome<undefined>> {
  return change(call, `sessions/${encodeURIComponent(id)}`, { method: "DELETE" }, nothing);
}
