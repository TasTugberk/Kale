"use server";

import { notFound } from "next/navigation";
import type { FormState } from "@/lib/admin-requests";
import { isDevelopmentOrTesting } from "@/lib/config";
import { requestTestToken, requireSameOrigin } from "@/lib/server";
import { readTestTokenAnswer, testTokenRequestFromForm, type TestToken } from "@/lib/test-tokens";

export async function testTokenAction(_previous: FormState<TestToken>, form: FormData): Promise<FormState<TestToken>> {
  await requireSameOrigin();
  // AuthService refuses outside Development/Testing too; this keeps the UI from even asking.
  if (!isDevelopmentOrTesting({ KALE_ENVIRONMENT: process.env.KALE_ENVIRONMENT })) {
    notFound();
  }

  return readTestTokenAnswer(await requestTestToken(testTokenRequestFromForm(form)));
}
