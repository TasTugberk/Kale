"use server";

import { revalidatePath } from "next/cache";
import { afterSubmit, text, type FormState, type SubmitState } from "@/lib/admin-requests";
import {
  createRole, deleteRole, operationsFromForm, renameRole, roleFromForm, setRoleOperations, type RoleFields,
} from "@/lib/roles";
import { adminApi, requireSameOrigin } from "@/lib/server";

// Every application's roles page shows the change.
function showChange() {
  revalidatePath("/applications/[applicationId]/roles", "page");
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

export async function setOperationsAction(
  _previous: SubmitState<undefined, string[]> | undefined, form: FormData,
): Promise<SubmitState<undefined, string[]>> {
  await requireSameOrigin();
  const submitted = operationsFromForm(form);
  const outcome = await setRoleOperations(adminApi, text(form, "roleId"), submitted);
  showChange();
  return afterSubmit(outcome, submitted);
}

export async function deleteRoleAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await deleteRole(adminApi, text(form, "roleId"));
  showChange();
  return outcome;
}
