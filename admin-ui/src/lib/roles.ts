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

/** The ticked "operation" check boxes. */
export function operationsFromForm(form: FormData): string[] {
  return form.getAll("operation").filter(value => typeof value === "string");
}

/** Operations the role still holds that can't be granted any more; saving the role's operations drops them. */
export function obsoleteGrants(granted: string[], operations: Operation[]): string[] {
  const obsolete = new Set(operations.filter(operation => operation.isObsolete).map(operation => operation.name));
  return granted.filter(name => obsolete.has(name));
}
