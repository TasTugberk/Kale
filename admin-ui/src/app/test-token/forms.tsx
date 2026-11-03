"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { KeyRound } from "lucide-react";
import { OneTimeSecret, Refusal } from "@/components/form-parts";
import { Button } from "@/components/ui/button";
import { Field, Select } from "@/components/ui/fields";
import { adminUiApplicationKey } from "@/lib/applications";
import type { ApplicationRoles } from "@/lib/roles";
import type { User } from "@/lib/users";
import { testTokenAction } from "./actions";

export function TestTokenForm({ choices, users }: { choices: ApplicationRoles[]; users: User[] }) {
  const t = useTranslations("TestToken");
  const [state, action, pending] = useActionState(testTokenAction, undefined);
  return (
    <form action={action} className="grid gap-4">
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("application")}>
          <Select name="applicationKey" required className="w-full font-mono text-xs">
            {/* Not "auth": Kale refuses test tokens for the admin UI itself. */}
            {choices.filter(choice => choice.application.key !== adminUiApplicationKey).map(choice => (
              <option key={choice.application.id} value={choice.application.key}>{choice.application.key}</option>
            ))}
          </Select>
        </Field>
        <Field label={t("user")}>
          <Select name="userId" required className="w-full">
            {users.map(user => <option key={user.id} value={user.id}>{user.userName} ({user.displayName})</option>)}
          </Select>
        </Field>
      </div>
      <Field label={t("role")} hint={t("roleHint")}>
        <Select name="roleId" className="w-full">
          <option value="">{t("defaultRole")}</option>
          {choices.filter(choice => choice.roles.length > 0).map(choice => (
            <optgroup key={choice.application.id} label={choice.application.key}>
              {choice.roles.map(role => <option key={role.id} value={role.id}>{role.name}</option>)}
            </optgroup>
          ))}
        </Select>
      </Field>
      <div><Button type="submit" disabled={pending}><KeyRound aria-hidden />{t("get")}</Button></div>
      <Refusal state={state} />
      {state?.ok && (
        <>
          <OneTimeSecret label={t("token", { minutes: Math.round(state.value.expiresInSeconds / 60) })} secret={state.value.accessToken} note={t("tokenNote")} />
          <div className="grid gap-1.5">
            <p className="text-sm text-slate-600">{t("try")}</p>
            <code className="block rounded-md bg-slate-900 p-3 font-mono text-xs break-all text-slate-100">
              curl -H &quot;Authorization: Bearer {state.value.accessToken}&quot; &lt;{t("yourEndpoint")}&gt;
            </code>
          </div>
        </>
      )}
    </form>
  );
}
