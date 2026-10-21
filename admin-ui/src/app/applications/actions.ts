"use server";

import { revalidatePath } from "next/cache";
import { afterSubmit, text, type FormState, type SubmitState } from "@/lib/admin-requests";
import {
  createApplication, newApplicationFromForm, rotateSecret, setApplicationActive, type CreatedApplication,
  type NewApplication,
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
