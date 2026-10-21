import { change, find, jsonBody, nothing, read, text, type ApiCall, type Outcome } from "./admin-requests";

/** The admin API's users: accounts, their access to applications, and their directly assigned roles. */

export interface User {
  id: string;
  userName: string;
  displayName: string;
  isActive: boolean;
}

/** Access to one application: signing in to it needs an active row (and a role). */
export interface Access {
  applicationId: string;
  applicationKey: string;
  isActive: boolean;
  /** Picked at sign-in when the user doesn't choose; null for none. */
  defaultRoleId: string | null;
}

export interface AssignedRole {
  roleId: string;
  name: string;
  applicationKey: string;
}

export interface UserDetails extends User {
  applications: Access[];
  /** Assigned directly (roles from groups are on the groups). */
  roles: AssignedRole[];
  groups: { groupId: string; name: string }[];
}

export interface NewUser {
  userName: string;
  displayName: string;
  password: string;
}

export interface AccessFields {
  isActive: boolean;
  defaultRoleId: string | null;
}

export function listUsers(call: ApiCall): Promise<User[]> {
  return read<User[]>(call, "users");
}

export function getUser(call: ApiCall, id: string): Promise<UserDetails | undefined> {
  return find<UserDetails>(call, `users/${encodeURIComponent(id)}`);
}

export function createUser(call: ApiCall, user: NewUser): Promise<Outcome<User>> {
  return change(call, "users", jsonBody("POST", user), async response => (await response.json()) as User);
}

/** Disabling stops the user's sessions everywhere. */
export function setUserActive(call: ApiCall, id: string, isActive: boolean): Promise<Outcome<undefined>> {
  return change(call, `users/${encodeURIComponent(id)}/active`, jsonBody("PUT", { isActive }), nothing);
}

/** Grants or changes access; deactivating it stops the user's sessions in that application. */
export function setAccess(call: ApiCall, userId: string, applicationId: string, access: AccessFields): Promise<Outcome<undefined>> {
  return change(call, `users/${encodeURIComponent(userId)}/applications/${encodeURIComponent(applicationId)}`,
    jsonBody("PUT", access), nothing);
}

export function assignRole(call: ApiCall, userId: string, roleId: string): Promise<Outcome<undefined>> {
  return change(call, `users/${encodeURIComponent(userId)}/roles/${encodeURIComponent(roleId)}`, { method: "PUT" }, nothing);
}

/** Sessions using the role stop, unless one of the user's groups still grants it. */
export function unassignRole(call: ApiCall, userId: string, roleId: string): Promise<Outcome<undefined>> {
  return change(call, `users/${encodeURIComponent(userId)}/roles/${encodeURIComponent(roleId)}`, { method: "DELETE" }, nothing);
}

export function newUserFromForm(form: FormData): NewUser {
  // Not trimmed: spaces are part of a password.
  const password = form.get("password");
  return { userName: text(form, "userName"), displayName: text(form, "displayName"), password: typeof password === "string" ? password : "" };
}

/** What a refused "new user" form gets back: never the password, which would otherwise travel back to the browser. */
export function withoutPassword(user: NewUser): Omit<NewUser, "password"> {
  return { userName: user.userName, displayName: user.displayName };
}

export function accessFromForm(form: FormData): AccessFields {
  const defaultRoleId = text(form, "defaultRoleId");
  return { isActive: text(form, "isActive") === "true", defaultRoleId: defaultRoleId === "" ? null : defaultRoleId };
}
