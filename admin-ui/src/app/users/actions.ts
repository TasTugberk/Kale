"use server";

import { revalidatePath } from "next/cache";
import { afterSubmit, text, type FormState, type SubmitState } from "@/lib/admin-requests";
import { adminApi, requireSameOrigin } from "@/lib/server";
import {
  accessFromForm, assignRole, createUser, newUserFromForm, setAccess, setUserActive, unassignRole, withoutPassword,
  type AccessFields, type NewUser, type User,
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

export async function setUserActiveAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await setUserActive(adminApi, text(form, "userId"), text(form, "isActive") === "true");
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

export async function assignRoleAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await assignRole(adminApi, text(form, "userId"), text(form, "roleId"));
  showChange();
  return outcome;
}

export async function unassignRoleAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await unassignRole(adminApi, text(form, "userId"), text(form, "roleId"));
  showChange();
  return outcome;
}
