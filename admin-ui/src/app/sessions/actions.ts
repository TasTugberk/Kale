"use server";

import { revalidatePath } from "next/cache";
import { text, type FormState } from "@/lib/admin-requests";
import { stopSession } from "@/lib/open-sessions";
import { adminApi, requireSameOrigin } from "@/lib/server";

export async function stopSessionAction(_previous: FormState<undefined>, form: FormData): Promise<FormState<undefined>> {
  await requireSameOrigin();
  const outcome = await stopSession(adminApi, text(form, "sessionId"));
  revalidatePath("/sessions");
  return outcome;
}
