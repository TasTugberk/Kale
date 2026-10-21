"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { afterSubmit, text, type FormState, type SubmitState } from "@/lib/admin-requests";
import {
  addGroupRole, addMember, createGroup, deleteGroup, groupFromForm, removeGroupRole, removeMember, renameGroup,
  type Group, type GroupFields,
} from "@/lib/groups";
import { adminApi, requireSameOrigin } from "@/lib/server";

// Groups show on the groups pages and on their members' pages.
function showChange() {
  revalidatePath("/groups", "layout");
  revalidatePath("/users", "layout");
}

export async function createGroupAction(
  _previous: SubmitState<Group, GroupFields> | undefined, form: FormData,
): Promise<SubmitState<Group, GroupFields>> {
  await requireSameOrigin();
  const group = groupFromForm(form);
  const outcome = await createGroup(adminApi, group);
  showChange();
  return afterSubmit(outcome, group);
}

export async function renameGroupAction(
  _previous: SubmitState<undefined, GroupFields> | undefined, form: FormData,
): Promise<SubmitState<undefined, GroupFields>> {
  await requireSameOrigin();
  const group = groupFromForm(form);
  const outcome = await renameGroup(adminApi, text(form, "groupId"), group);
  showChange();
  return afterSubmit(outcome, group);
}

/** Deleted, the group's page is gone: back to the list. */
export async function deleteGroupAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await deleteGroup(adminApi, text(form, "groupId"));
  showChange();
  if (outcome.ok) {
    redirect("/groups");
  }

  return outcome;
}

export async function addMemberAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await addMember(adminApi, text(form, "groupId"), text(form, "userId"));
  showChange();
  return outcome;
}

export async function removeMemberAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await removeMember(adminApi, text(form, "groupId"), text(form, "userId"));
  showChange();
  return outcome;
}

export async function addGroupRoleAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await addGroupRole(adminApi, text(form, "groupId"), text(form, "roleId"));
  showChange();
  return outcome;
}

export async function removeGroupRoleAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await removeGroupRole(adminApi, text(form, "groupId"), text(form, "roleId"));
  showChange();
  return outcome;
}
