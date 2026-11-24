"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { afterSubmit, listFromForm, text, type FormState, type SubmitState } from "@/lib/admin-requests";
import { adminApi, requireSameOrigin } from "@/lib/server";
import {
  accessFromForm, assignRoles, createUser, deleteUser, newUserFromForm, passwordFromForm, resetTwoFactor, setAccess,
  setPassword, setUserActive, unassignRole, updateUser, userNamesFromForm, withoutPassword, type AccessFields,
  type NewUser, type User, type UserNames,
} from "@/lib/users";

// The list and every user's page show the change.
function showChange() {
  revalidatePath("/users", "layout");
}

export async function createUserAction(
  _previous: SubmitState<User, Omit<NewUser, "password">> | undefined, form: FormData,
): Promise<SubmitState<User, Omit<NewUser, "password">>> {
  await requireSameOrigin();
  const user = newUserFromForm(form);
  const outcome = await createUser(adminApi, user);
  showChange();
  return afterSubmit(outcome, withoutPassword(user));
}

/** Corrected names; a refused form starts again from what was typed. */
export async function updateUserAction(
  _previous: SubmitState<undefined, UserNames> | undefined, form: FormData,
): Promise<SubmitState<undefined, UserNames>> {
  await requireSameOrigin();
  const names = userNamesFromForm(form);
  const outcome = await updateUser(adminApi, text(form, "userId"), names);
  showChange();
  return afterSubmit(outcome, names);
}

/** Deleted (here or already elsewhere), the user's page is gone: back to the list. */
export async function deleteUserAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await deleteUser(adminApi, text(form, "userId"));
  showChange();
  if (outcome.ok || outcome.refusal === "not-found") {
    redirect("/users");
  }

  return outcome;
}

export async function setUserActiveAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await setUserActive(adminApi, text(form, "userId"), text(form, "isActive") === "true");
  showChange();
  return outcome;
}

/**
 * A new password, set by the admin. Only the outcome comes back, never the password: after a refusal the admin
 * types it again.
 */
export async function setPasswordAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await setPassword(adminApi, text(form, "userId"), passwordFromForm(form));
  showChange();
  return outcome;
}

export async function resetTwoFactorAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await resetTwoFactor(adminApi, text(form, "userId"));
  showChange();
  return outcome;
}

/** Gives access to an application, or changes it (active or not, default role). */
export async function setAccessAction(
  _previous: SubmitState<undefined, AccessFields> | undefined, form: FormData,
): Promise<SubmitState<undefined, AccessFields>> {
  await requireSameOrigin();
  const access = accessFromForm(form);
  const outcome = await setAccess(adminApi, text(form, "userId"), text(form, "applicationId"), access);
  showChange();
  return afterSubmit(outcome, access);
}

/** The roles picked in the "assign roles" dialog. */
export async function assignRolesAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await assignRoles(adminApi, text(form, "userId"), listFromForm(form, "roleId"));
  showChange();
  return outcome;
}

export async function unassignRoleAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await unassignRole(adminApi, text(form, "userId"), text(form, "roleId"));
  showChange();
  return outcome;
}
