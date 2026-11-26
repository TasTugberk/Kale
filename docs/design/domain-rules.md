# Kale's domain rules in ten minutes

The rules a change to AuthService must keep. Each one is short here and links to where it lives; [CLAUDE.md](../../CLAUDE.md) ("Decisions") has the details, and the [handover](../handover/handover.en.md) (§6.4) has the data model.

## What belongs to whom

- **Users and groups are global.** Operations, roles, access rows (`user_applications`) and sessions belong to one application.
- **The database enforces it, not only the code:** composite foreign keys carry `application_id` (names end in `_same_application`), so a role can't hold another application's operation and a session can't use another application's role.
- **Operations come only from the application's enum,** registered at its start. One that stops being declared becomes *obsolete* (after `Operations:ObsoleteAfter`), never deleted: it can't be granted and doesn't count. If it comes back, it is active again **with its old role grants**, which were kept.
- **Implications** (`[Implies]`) are expanded when a role's effective operations are computed, so an endpoint names only the lowest operation it needs.

## When a session is ACTIVE

One definition, in [`Sessions/SessionCheck.cs`](../../src/AuthService/Sessions/SessionCheck.cs), used for every request a client app checks and for every token refresh. A session is ACTIVE only if **all** of these hold:

1. It hasn't ended (`EndedAt` is null: there is no status column) and isn't past `ExpiresAt` (8 h from sign-in, not sliding).
2. The user is active.
3. The user's access row for the application is active.
4. The application is active.
5. The user still holds the session's role, directly or through a group.

Anything else is ended for every client, whatever the token says. Access tokens last 10 minutes and carry no operations, so a change reaches a running application through its session check, the "session stopped" and "role edited" events, and the next refresh.

## Lifetimes

- **A session lasts at most 8 h** from its sign-in (`Sessions:Lifetime`), however often its tokens are refreshed.
- **Kale's own sign-in cookie (`kale.signin`) also lasts 8 h**, from the password. A session started just before that cookie ends runs its own 8 h, so **one password entry can cover up to 16 h** in one application.
- Finished sessions (ended or expired) are deleted `Sessions:KeepFinishedFor` (7 days) after they finished.

## One open session per user and application

A new sign-in to an application (another device, or a role switch) ends the user's earlier open session there; sessions in other applications stay. A unique index (`ux_sessions_one_open_per_user_and_application`) is the backstop; the sign-in's row lock is what keeps two sign-ins from colliding.

## Who may end a session, and how

**Every end goes through `SessionStopper`**, which sets `EndedAt` and adds a `SessionStopped` event to the outbox **in the same save** as the change that caused it. Nothing else may end or delete an open session; sessions *restrict* deleting their user, role or access row precisely so that nothing removes them without an event.

Sessions end when:

- the user signs out (Kale's sign-out page, or the application's server through gRPC `EndSession`), or signs in again to the same application;
- an admin stops the session;
- the user is disabled, deleted, gets a new password, or has two-step sign-in reset;
- their access to the application is deactivated, the role is taken from them (unless a group still gives it), a group change takes it away, or the role is deleted;
- the application is deactivated or deleted.

Some of these (e.g. editing a role's operations) leave the session open but change what it holds: clients learn it through `RoleEdited`.

## Nobody may lock everyone out

No change may leave nobody with `auth.Admin` (`Admin/LastAdminCheck.cs`): another active user with active access to `auth` must still hold it. `auth` itself can't be deactivated or deleted.

## Locks, and their order

Changes that must not race (a sign-in and a disable, a role delete and a sign-in with that role, two admins at once) take row locks inside their transaction, so one waits for the other instead of both acting on what they read before. They take them in one order (groups, then roles, then users, then access rows; several rows of a table in id order; the last-admin advisory lock last), with sign-in's shared locks as the documented exception, so they can't deadlock. [locking.md](locking.md) has the table of who locks what and why: **read it before adding or moving a lock.**
