"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { OneTimeSecret, Refusal } from "@/components/form-parts";
import { adminUiApplicationKey } from "@/lib/applications";
import type { ApplicationRoles } from "@/lib/roles";
import type { User } from "@/lib/users";
import { testTokenAction } from "./actions";

export function TestTokenForm({ choices, users }: { choices: ApplicationRoles[]; users: User[] }) {
  const t = useTranslations("TestToken");
  const [state, action, pending] = useActionState(testTokenAction, undefined);
  return (
    <form action={action} className="stacked">
      <label>
        {t("application")}
        <select name="applicationKey" required>
          {/* Not "auth": Kale refuses test tokens for the admin UI itself. */}
          {choices.filter(choice => choice.application.key !== adminUiApplicationKey).map(choice => (
            <option key={choice.application.id} value={choice.application.key}>{choice.application.key}</option>
          ))}
        </select>
      </label>
      <label>
        {t("user")}
        <select name="userId" required>
          {users.map(user => <option key={user.id} value={user.id}>{user.userName} ({user.displayName})</option>)}
        </select>
      </label>
      <label>
        {t("role")}
        <select name="roleId">
          <option value="">{t("defaultRole")}</option>
          {choices.filter(choice => choice.roles.length > 0).map(choice => (
            <optgroup key={choice.application.id} label={choice.application.key}>
              {choice.roles.map(role => <option key={role.id} value={role.id}>{role.name}</option>)}
            </optgroup>
          ))}
        </select>
      </label>
      <button type="submit" disabled={pending}>{t("get")}</button>
      <Refusal state={state} />
      {state?.ok && (
        <>
          <OneTimeSecret label={t("token", { minutes: Math.round(state.value.expiresInSeconds / 60) })} secret={state.value.accessToken} />
          <p className="hint">{t("try")}</p>
          <code className="example">curl -H &quot;Authorization: Bearer {state.value.accessToken}&quot; &lt;{t("yourEndpoint")}&gt;</code>
        </>
      )}
    </form>
  );
}
