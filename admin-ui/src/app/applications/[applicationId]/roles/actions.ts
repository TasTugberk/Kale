"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { afterSubmit, listFromForm, text, type FormState, type SubmitState } from "@/lib/admin-requests";
import {
  addRoleOperations, createRole, deleteRole, removeRoleOperation, renameRole, roleFromForm, type RoleFields,
} from "@/lib/roles";
import { adminApi, requireSameOrigin } from "@/lib/server";

// Every application's roles page and role page show the change.
function showChange() {
  revalidatePath("/applications/[applicationId]/roles", "layout");
}

export async function createRoleAction(
  _previous: SubmitState<undefined, RoleFields> | undefined, form: FormData,
): Promise<SubmitState<undefined, RoleFields>> {
  await requireSameOrigin();
  const submitted = roleFromForm(form);
  const outcome = await createRole(adminApi, text(form, "applicationId"), submitted);
  showChange();
  return afterSubmit(outcome, submitted);
}

export async function renameRoleAction(
  _previous: SubmitState<undefined, RoleFields> | undefined, form: FormData,
): Promise<SubmitState<undefined, RoleFields>> {
  await requireSameOrigin();
  const submitted = roleFromForm(form);
  const outcome = await renameRole(adminApi, text(form, "roleId"), submitted);
  showChange();
  return afterSubmit(outcome, submitted);
}

/** The operations picked in the "add operations" dialog, on top of what the role holds. */
export async function addOperationsAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await addRoleOperations(adminApi, text(form, "applicationId"), text(form, "roleId"), listFromForm(form, "operation"));
  showChange();
  return outcome;
}

export async function removeOperationAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await removeRoleOperation(adminApi, text(form, "applicationId"), text(form, "roleId"), text(form, "operation"));
  showChange();
  return outcome;
}

/** Deleted (here or already elsewhere), the role's page is gone: back to the application's roles. */
export async function deleteRoleAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await deleteRole(adminApi, text(form, "roleId"));
  showChange();
  if (outcome.ok || outcome.refusal === "not-found") {
    redirect(`/applications/${encodeURIComponent(text(form, "applicationId"))}/roles`);
  }

  return outcome;
}
