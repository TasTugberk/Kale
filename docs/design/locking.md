# Locking in AuthService

Status: describes the code on `main` as of 2026-11-05. Keep it in step whenever a change adds or reorders a lock.

AuthService runs as several instances against one PostgreSQL database, and many of its rules span several rows ("no session may start after the user is disabled", "somebody must keep `auth.Admin`"). It keeps those rules with **row locks and transaction-scoped advisory locks**, taken explicitly with `SELECT … FOR …` at the start of each transaction. Every lock below is held until its transaction commits or rolls back.

The reasoning for each lock is also in a comment next to it in the code. This file collects the order rules in one place.

## Who locks what, in which order

| Code | Transaction takes, in this order | Why |
|---|---|---|
| `SignIn/UserSignIn.StartAsync` | 1. `users` row, `FOR SHARE` · 2. all `roles` of the application, `ORDER BY id FOR KEY SHARE` · 3. the `user_applications` row, `FOR UPDATE` · then inserts the session (its foreign keys take `KEY SHARE` on the user, role and access row) | (1) A disable or access change (which locks the user `FOR UPDATE`) either waits for this sign-in and then stops its session, or this waits and sees the change. Sign-ins of the same user to different applications don't block each other (shared lock). (2) Before the access row, the same order as a role delete, so the two take turns instead of deadlocking. `KEY SHARE` doesn't block other sign-ins. (3) Two sign-ins of the same user to the same application take turns, so the later one finds and ends the earlier one's session. |
| `Users/UserAccounts` (disable) | 1. `users` row, `FOR UPDATE` · 2. last-admin advisory lock (if the user is an admin) · then the change and `SessionStopped` events | (1) against a concurrent sign-in, see above |
| `Users/UserAccess.SetApplicationAccessAsync` | 1. `users` row, `FOR UPDATE` · 2. last-admin advisory lock (if an admin loses access to `auth`) · then the access row changes and sessions stop | Same as above; "the user first, then the access row", like sign-in |
| `Users/UserAccess.AssignRoleAsync` | 1. `users` row, `FOR UPDATE` · then inserts the `user_roles` row and, when the user has none, the `user_applications` row (foreign keys take `KEY SHARE` on the role and the application) | Adding a role takes nothing away, so there's no last-admin check. The user's lock also keeps two changes from both creating the missing access row. The role's `KEY SHARE` comes after the user: an exception to rule 1 below, harmless because `KEY SHARE` only waits for a role delete (or an application edit), and `RoleEditor` (or `ApplicationService`) never locks users |
| `Users/UserAccess.UnassignRoleAsync` | 1. `users` row, `FOR UPDATE` · then the `user_roles` deletion is **saved** · 2. last-admin advisory lock (only if the user really lost `auth.Admin`) | The admin check is asked before and after the saved (uncommitted) change, so only a change that really takes `auth.Admin` away needs another admin |
| `Groups/GroupEditor.AddMemberAsync` | 1. `groups` row, `FOR SHARE` · 2. the `users` row, `FOR UPDATE` · then inserts the `user_groups` row and the missing `user_applications` rows | (1) Members join side by side, but not while the group's roles change, so the member gets access for exactly the roles read. (2) Like `UserAccess`: takes turns with sign-in and with other access changes of the user. Adding takes nothing away: no last-admin check |
| `Groups/GroupEditor.AddRoleAsync` | 1. `groups` row, `FOR UPDATE` · 2. the members' `users` rows, `ORDER BY id FOR UPDATE` · then inserts the `group_roles` row (`KEY SHARE` on the role) and the members' missing `user_applications` rows | (1) A member joining at the same moment either commits first and is among the members who get access, or waits and then sees the role. (2) As for `AddMemberAsync`, in id order. The role's `KEY SHARE` after the users: the same harmless exception as `UserAccess.AssignRoleAsync` |
| `Groups/GroupEditor` (anything that can take roles away) | 1. `groups` row, `FOR UPDATE` · 2. the affected `users` rows, `ORDER BY id FOR UPDATE` · then the change is **saved** · 3. last-admin advisory lock (if one of them lost `auth.Admin`) · then sessions whose role they no longer hold stop | (1) A member joining at the same moment (it holds the group `FOR SHARE`) either commits first and is among the members read, or waits. (2) In id order, so two group changes sharing users can't deadlock |
| `Roles/RoleEditor.DeleteAsync` | 1. `roles` row, `FOR UPDATE` · 2. last-admin advisory lock (if it's a role of `auth`) · then stops and removes the role's sessions, clears it as a default role, deletes it (cascading to `role_operations`, `user_roles`, `group_roles`) | (1) A sign-in saving a session with this role holds `KEY SHARE` on it, so it either commits first (and its session is found and stopped) or waits |
| `Roles/RoleEditor` (set operations) | 1. `roles` row, `FOR UPDATE` · 2. last-admin advisory lock (if `auth.Admin` is taken from a role of `auth`) | Two edits of one role take turns; otherwise the role could end up with both sets |
| `Applications/ApplicationService` (update, test-token permission) | `applications` row, `FOR UPDATE` | The second edit reads the OpenIddict client only after the first committed; otherwise OpenIddict refuses a stale concurrency token |
| `Operations/OperationRegistrar` | advisory lock `(1001, hashtext(application id))` | Several instances of one application register at startup at the same moment. They take turns, so the second finds the first's rows. Other applications aren't blocked |
| `Keys/KeyRing.RefreshAsync` | advisory lock `(1002, 0)` | Several AuthService instances apply the rotation policy at once: one creates keys, the others find them |
| `Admin/LastAdminCheck.EnsureAnotherAdminRemainsAsync` | advisory lock `(1002, 0)`, inside the caller's transaction (it throws without one) | Two changes at once (two admins disabling each other) could both still see the other admin and both commit, leaving nobody |
| `Outbox/OutboxPublisher` | up to 100 `outbox_messages` rows, `ORDER BY id FOR UPDATE SKIP LOCKED` | Several instances publish without sending the same rows; the rows are deleted in the same transaction |

## The order rules

When a transaction takes more than one of these, it takes them in this order:

1. **`groups`**, then **`roles`**, then **`users`**, then **`user_applications`**. Sign-in takes `users` before `roles`, but only with `FOR SHARE`. That doesn't conflict with `RoleEditor`, which never locks `users`.
2. **Several rows of one table are taken in `id` order** (`ORDER BY id` in the locking query).
3. **The last-admin advisory lock** comes after the row locks.
4. **Advisory locks are always transaction-scoped** (`pg_advisory_xact_lock`), so they can't leak past a commit or a failure.

Locks also come from foreign keys. Inserting a row takes `FOR KEY SHARE` on the rows it references, and deleting a referenced row needs the rows that reference it. Many of the waits above depend on that, for example sign-in's session insert against `RoleEditor`'s `FOR UPDATE` on the role. Remember it when you add a table that references `users`, `roles` or `user_applications`.

## Known issues

- **Advisory key `(1002, 0)` is used twice:** by `KeyRing` and by `LastAdminCheck`. Each comment says only that it differs from 1001. As a result, key rotation and last-admin checks wait for each other for no reason. Neither takes another lock after this one, so no deadlock was found. Fix: give one of them its own number, and keep all advisory lock numbers in one class.
- **A possible deadlock between two admin changes to the same `auth` admin role (not reproduced).**
  - `UserAccess.UnassignRoleAsync` saves the deletion of a `user_roles` row, which locks that row, and only then takes the last-admin lock.
  - `RoleEditor.DeleteAsync` takes the last-admin lock first, and its cascade then needs the same `user_roles` row.
  - If one admin removes role R (of `auth`, holding `auth.Admin`) from user U while another admin deletes R, each could wait for the other. `GroupEditor` follows the same "save, then check" order.
  - PostgreSQL would detect the deadlock after `deadlock_timeout` (1 s by default) and fail one of the two requests (SQLSTATE 40P01). The data stays consistent, and retrying works.
  - To fix it: write a test that runs both at once. If it confirms the deadlock, take the last-admin lock before the save in `UnassignRoleAsync` and `GroupEditor`, at the cost of taking it for every change that *might* remove admin rights.

## Adding a lock

- Take it at the start of the transaction, with raw SQL (`ExecuteSqlAsync($"SELECT 1 FROM … FOR …")`), and comment **why** next to it.
- Fit it into the order above, and add a row to this table.
- Test the race for real: two contexts, two transactions, on PostgreSQL (`[Collection(UsesPostgres.Name)]`). `tests/AuthService.Tests/Admin/LastAdminTests.cs` shows how.
