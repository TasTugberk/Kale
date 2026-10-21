"use server";

import { revalidatePath } from "next/cache";
import type { FormState } from "@/lib/admin-requests";
import {
  createApplication, newApplicationFromForm, rotateSecret, setApplicationActive, text, type CreatedApplication,
} from "@/lib/applications";
import { adminApi, requireSameOrigin } from "@/lib/server";

export async function createApplicationAction(
  _previous: FormState<CreatedApplication>, form: FormData,
): Promise<FormState<CreatedApplication>> {
  await requireSameOrigin();
  const outcome = await createApplication(adminApi, newApplicationFromForm(form));
  revalidatePath("/applications");
  return outcome;
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
