"use server";

import { revalidatePath } from "next/cache";
import { afterSubmit, text, type FormState, type SubmitState } from "@/lib/admin-requests";
import {
  applicationChangesFromForm, changeApplication, createApplication, newApplicationFromForm, rotateSecret,
  setApplicationActive, type ApplicationChanges, type CreatedApplication, type NewApplication,
} from "@/lib/applications";
import { adminApi, requireSameOrigin } from "@/lib/server";

export async function createApplicationAction(
  _previous: SubmitState<CreatedApplication, NewApplication> | undefined, form: FormData,
): Promise<SubmitState<CreatedApplication, NewApplication>> {
  await requireSameOrigin();
  const submitted = newApplicationFromForm(form);
  const outcome = await createApplication(adminApi, submitted);
  revalidatePath("/applications");
  return afterSubmit(outcome, submitted);
}

export async function rotateSecretAction(_previous: FormState<string>, form: FormData): Promise<FormState<string>> {
  await requireSameOrigin();
  return rotateSecret(adminApi, text(form, "id"));
}

export async function setActiveAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await setApplicationActive(adminApi, text(form, "id"), text(form, "isActive") === "true");
  revalidatePath("/applications");
  return outcome;
}

export async function changeApplicationAction(
  _previous: SubmitState<undefined, ApplicationChanges> | undefined, form: FormData,
): Promise<SubmitState<undefined, ApplicationChanges>> {
  await requireSameOrigin();
  const changes = applicationChangesFromForm(form);
  const outcome = await changeApplication(adminApi, text(form, "id"), changes);
  // The list and the application's own pages show the name.
  revalidatePath("/applications", "layout");
  return afterSubmit(outcome, changes);
}
