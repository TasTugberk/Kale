import { randomBytes } from "node:crypto";

/** What the admin UI keeps for a signed-in admin. Only on the server: the browser holds just the session id. */
export interface AdminSession {
  accessToken: string;
  refreshToken?: string;
  idToken?: string;
  /** Milliseconds since the epoch. */
  accessTokenExpiresAt: number;
}

// AuthService's sessions last 8 hours (absolute); the admin UI's don't outlive them.
const sessionLifetimeMs = 8 * 60 * 60 * 1000;

// Renew the access token this long before it expires, so a request never goes out with one about to lapse.
const renewBeforeExpiryMs = 60 * 1000;

/**
 * Sessions in memory, keyed by an unguessable id (the session cookie). A restart signs admins out, which is
 * acceptable for one admin UI instance and keeps tokens out of cookies and out of any store on disk.
 */
export class SessionStore {
  private readonly sessions = new Map<string, { session: AdminSession; createdAt: number }>();

  constructor(private readonly now: () => number) {}

  /** How many sessions are kept (for tests). */
  get size(): number {
    return this.sessions.size;
  }

  create(session: AdminSession): string {
    // Sessions that were never looked up again would otherwise stay in memory until a restart.
    for (const [id, entry] of this.sessions) {
      if (this.now() - entry.createdAt >= sessionLifetimeMs) {
        this.sessions.delete(id);
      }
    }

    const id = randomBytes(32).toString("base64url");
    this.sessions.set(id, { session, createdAt: this.now() });
    return id;
  }

  get(id: string): AdminSession | undefined {
    const entry = this.sessions.get(id);
    if (entry && this.now() - entry.createdAt >= sessionLifetimeMs) {
      this.sessions.delete(id);
      return undefined;
    }

    return entry?.session;
  }

  update(id: string, session: AdminSession): void {
    const entry = this.sessions.get(id);
    if (entry) {
      entry.session = session;
    }
  }

  delete(id: string): void {
    this.sessions.delete(id);
  }
}

export function needsRefresh(session: AdminSession, now: number): boolean {
  return session.accessTokenExpiresAt - renewBeforeExpiryMs <= now;
}
