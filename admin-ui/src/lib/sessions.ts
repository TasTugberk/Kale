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

  // Ids of sessions AuthService ended, until they would have expired anyway: the browser still sends the cookie
  // (on a reload, Back, another tab), and must then hear "ended", not "never signed in" (see AdminApiCaller).
  private readonly ended = new Map<string, number>();

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

    for (const [id, forgetAt] of this.ended) {
      if (this.now() >= forgetAt) {
        this.ended.delete(id);
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

  /** Signed out: forgotten completely. */
  delete(id: string): void {
    this.sessions.delete(id);
  }

  /** Ended by AuthService: forgotten, but the id stays known as ended until the session would have expired. */
  end(id: string): void {
    const entry = this.sessions.get(id);
    this.sessions.delete(id);
    if (entry) {
      this.ended.set(id, entry.createdAt + sessionLifetimeMs);
    }
  }

  wasEnded(id: string): boolean {
    const forgetAt = this.ended.get(id);
    return forgetAt !== undefined && this.now() < forgetAt;
  }
}

export function needsRefresh(session: AdminSession, now: number): boolean {
  return session.accessTokenExpiresAt - renewBeforeExpiryMs <= now;
}
