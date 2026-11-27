import { change, nothing, read, type ApiCall, type Outcome } from "./admin-requests";
import { apiPath, uiPageSize, type ListQuery, type Page } from "./paging";

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

/** One page of open sessions, newest first (the admin API's only order), narrowed down and searched there. */
export function listOpenSessions(call: ApiCall, filter: SessionFilter, query: ListQuery): Promise<Page<OpenSession>> {
  return read<Page<OpenSession>>(call, apiPath("sessions", query, uiPageSize, {
    applicationId: filter.applicationId, userId: filter.userId,
  }));
}

export function stopSession(call: ApiCall, id: string): Promise<Outcome<undefined>> {
  return change(call, `sessions/${encodeURIComponent(id)}`, { method: "DELETE" }, nothing);
}
