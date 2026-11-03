import { change, changeEach, find, jsonBody, nothing, read, text, type ApiCall, type Outcome } from "./admin-requests";

/**
 * The admin API's groups: flat sets of users that hold roles together. Taking a member or a role away stops
 * the sessions that depended on it (AuthService does it in the same transaction).
 */

export interface Group {
  id: string;
  name: string;
  description: string | null;
}

export interface GroupDetails extends Group {
  members: { userId: string; userName: string; displayName: string }[];
  roles: { roleId: string; name: string; applicationKey: string }[];
}

export interface GroupFields {
  name: string;
  description: string | null;
}

export function listGroups(call: ApiCall): Promise<Group[]> {
  return read<Group[]>(call, "groups");
}

export function getGroup(call: ApiCall, id: string): Promise<GroupDetails | undefined> {
  return find<GroupDetails>(call, `groups/${encodeURIComponent(id)}`);
}

export function createGroup(call: ApiCall, group: GroupFields): Promise<Outcome<Group>> {
  return change(call, "groups", jsonBody("POST", group), async response => (await response.json()) as Group);
}

export function renameGroup(call: ApiCall, id: string, group: GroupFields): Promise<Outcome<undefined>> {
  return change(call, `groups/${encodeURIComponent(id)}`, jsonBody("PUT", group), nothing);
}

export function deleteGroup(call: ApiCall, id: string): Promise<Outcome<undefined>> {
  return change(call, `groups/${encodeURIComponent(id)}`, { method: "DELETE" }, nothing);
}

export function addMember(call: ApiCall, groupId: string, userId: string): Promise<Outcome<undefined>> {
  return change(call, `groups/${encodeURIComponent(groupId)}/members/${encodeURIComponent(userId)}`, { method: "PUT" }, nothing);
}

export function removeMember(call: ApiCall, groupId: string, userId: string): Promise<Outcome<undefined>> {
  return change(call, `groups/${encodeURIComponent(groupId)}/members/${encodeURIComponent(userId)}`, { method: "DELETE" }, nothing);
}

export function addGroupRole(call: ApiCall, groupId: string, roleId: string): Promise<Outcome<undefined>> {
  return change(call, `groups/${encodeURIComponent(groupId)}/roles/${encodeURIComponent(roleId)}`, { method: "PUT" }, nothing);
}

export function removeGroupRole(call: ApiCall, groupId: string, roleId: string): Promise<Outcome<undefined>> {
  return change(call, `groups/${encodeURIComponent(groupId)}/roles/${encodeURIComponent(roleId)}`, { method: "DELETE" }, nothing);
}

/** The users chosen together; stops at the first refusal. */
export function addMembers(call: ApiCall, groupId: string, userIds: string[]): Promise<Outcome<undefined>> {
  return changeEach(userIds, userId => addMember(call, groupId, userId));
}

/** The roles chosen together; stops at the first refusal. */
export function addGroupRoles(call: ApiCall, groupId: string, roleIds: string[]): Promise<Outcome<undefined>> {
  return changeEach(roleIds, roleId => addGroupRole(call, groupId, roleId));
}

export function groupFromForm(form: FormData): GroupFields {
  const description = text(form, "description");
  return { name: text(form, "name"), description: description === "" ? null : description };
}
