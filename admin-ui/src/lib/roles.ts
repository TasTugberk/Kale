import { change, jsonBody, nothing, read, text, type ApiCall, type Outcome } from "./admin-requests";
import { listApplications, type Application } from "./applications";

/**
 * The admin API's roles and operations. Operations are registered by the application itself, so they are only
 * read here; roles are the admin's to create, rename, grant operations to and delete.
 */

export interface Operation {
  id: string;
  name: string;
  /** No longer registered by the application: it can't be granted any more. */
  isObsolete: boolean;
}

export interface Role {
  id: string;
  name: string;
  description: string | null;
  /** Granted directly (the ones these imply aren't listed). */
  operations: string[];
}

export interface RoleFields {
  name: string;
  description: string | null;
}

export function listOperations(call: ApiCall, applicationId: string): Promise<Operation[]> {
  return read<Operation[]>(call, `applications/${encodeURIComponent(applicationId)}/operations`);
}

export function listRoles(call: ApiCall, applicationId: string): Promise<Role[]> {
  return read<Role[]>(call, `applications/${encodeURIComponent(applicationId)}/roles`);
}

/** An application with its roles, for choosing a role (assigning it to a user or a group). */
export interface ApplicationRoles {
  application: Application;
  roles: Role[];
}

/** Every application with its roles. There are few applications, so this is one call each. */
export async function listRolesByApplication(call: ApiCall): Promise<ApplicationRoles[]> {
  const applications = await listApplications(call);
  const roleLists = await Promise.all(applications.map(application => listRoles(call, application.id)));
  return applications.map((application, i) => ({ application, roles: roleLists[i] }));
}

/** The choices without the roles already held (by a user directly, or by a group). */
export function rolesNotHeld(choices: ApplicationRoles[], heldRoleIds: string[]): ApplicationRoles[] {
  const held = new Set(heldRoleIds);
  return choices.map(choice => ({ application: choice.application, roles: choice.roles.filter(role => !held.has(role.id)) }));
}

export function createRole(call: ApiCall, applicationId: string, role: RoleFields): Promise<Outcome<undefined>> {
  return change(call, `applications/${encodeURIComponent(applicationId)}/roles`, jsonBody("POST", role), nothing);
}

export function renameRole(call: ApiCall, roleId: string, role: RoleFields): Promise<Outcome<undefined>> {
  return change(call, `roles/${encodeURIComponent(roleId)}`, jsonBody("PUT", role), nothing);
}

/** Replaces the role's operations with these (full names, e.g. billing.InvoiceRead). */
export function setRoleOperations(call: ApiCall, roleId: string, operations: string[]): Promise<Outcome<undefined>> {
  return change(call, `roles/${encodeURIComponent(roleId)}/operations`, jsonBody("PUT", { operations }), nothing);
}

/** Also stops the role's sessions (AuthService does it in the same transaction). */
export function deleteRole(call: ApiCall, roleId: string): Promise<Outcome<undefined>> {
  return change(call, `roles/${encodeURIComponent(roleId)}`, { method: "DELETE" }, nothing);
}

export function roleFromForm(form: FormData): RoleFields {
  const description = text(form, "description");
  return { name: text(form, "name"), description: description === "" ? null : description };
}

/** Operations the role still holds that can't be granted any more; saving the role's operations drops them. */
export function obsoleteGrants(granted: string[], operations: Operation[]): string[] {
  const obsolete = new Set(operations.filter(operation => operation.isObsolete).map(operation => operation.name));
  return granted.filter(name => obsolete.has(name));
}

/** One role of an application (the admin API has no endpoint for a single role); undefined when there's none. */
export async function findRole(call: ApiCall, applicationId: string, roleId: string): Promise<Role | undefined> {
  return (await listRoles(call, applicationId)).find(role => role.id === roleId);
}

/** What the "add operations" dialog offers: the current operations the role doesn't hold yet. */
export function grantableOperations(role: Role, operations: Operation[]): Operation[] {
  return operations.filter(operation => !operation.isObsolete && !role.operations.includes(operation.name));
}

/** The role's operations with `added` granted too. Obsolete grants are left out: AuthService refuses to save them. */
export function operationsAfterAdding(granted: string[], added: string[], operations: Operation[]): string[] {
  return [...new Set([...withoutObsolete(granted, operations), ...added])];
}

/** The role's operations without `removed` (and without obsolete grants, like operationsAfterAdding). */
export function operationsAfterRemoving(granted: string[], removed: string, operations: Operation[]): string[] {
  return withoutObsolete(granted, operations).filter(name => name !== removed);
}

function withoutObsolete(granted: string[], operations: Operation[]): string[] {
  const obsolete = obsoleteGrants(granted, operations);
  return granted.filter(name => !obsolete.includes(name));
}

// The admin API only replaces a role's whole list. The role is read again here, not taken from the page, so a
// change another admin made since the page loaded isn't undone.

export async function addRoleOperations(
  call: ApiCall, applicationId: string, roleId: string, added: string[],
): Promise<Outcome<undefined>> {
  const [role, operations] = await Promise.all([findRole(call, applicationId, roleId), listOperations(call, applicationId)]);
  if (!role) {
    return { ok: false, refusal: "not-found" };
  }

  return setRoleOperations(call, roleId, operationsAfterAdding(role.operations, added, operations));
}

export async function removeRoleOperation(
  call: ApiCall, applicationId: string, roleId: string, removed: string,
): Promise<Outcome<undefined>> {
  const [role, operations] = await Promise.all([findRole(call, applicationId, roleId), listOperations(call, applicationId)]);
  if (!role) {
    return { ok: false, refusal: "not-found" };
  }

  return setRoleOperations(call, roleId, operationsAfterRemoving(role.operations, removed, operations));
}
