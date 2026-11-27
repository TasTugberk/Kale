"use client";

import { useActionState, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { UserPlus } from "lucide-react";
import { Refusal } from "@/components/form-parts";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogActions } from "@/components/ui/dialog";
import { Field, Input } from "@/components/ui/fields";
import { ServerDataTable, type ServerColumn } from "@/components/ui/server-data-table";
import type { SubmitState } from "@/lib/admin-requests";
import { listHref, type ListQuery, type Page } from "@/lib/paging";
import type { NewUser, User } from "@/lib/users";
import { createUserAction } from "./actions";

/** One page of users: the admin API searches, sorts and pages them (the list can be long). */
export function UsersTable({ users, query }: { users: Page<User>; query: ListQuery }) {
  const t = useTranslations("Users");
  const columns: ServerColumn<User>[] = [
    {
      id: "userName",
      sort: "userName",
      header: t("userName"),
      cell: user => <a href={`/users/${encodeURIComponent(user.id)}`} className="font-medium">{user.userName}</a>,
    },
    { id: "displayName", sort: "displayName", header: t("displayName"), cell: user => user.displayName },
    { id: "status", header: t("status"), cell: user => <UserStatus isActive={user.isActive} /> },
  ];

  return (
    <ServerDataTable
      columns={columns}
      rows={users.items}
      rowId={user => user.id}
      total={users.total}
      pageSize={users.pageSize}
      // The API's default order is by user name; show it as sorted so.
      query={query.sort ? query : { ...query, sort: "userName", desc: false }}
      href={next => listHref("/users", next)}
      empty={t("none")}
    />
  );
}

export function UserStatus({ isActive }: { isActive: boolean }) {
  const t = useTranslations("Users");
  return isActive ? <Badge tone="green">{t("active")}</Badge> : <Badge tone="red">{t("disabled")}</Badge>;
}

/** Creates the user, then opens their page (to give access and roles). */
export function NewUserDialog() {
  const t = useTranslations("Users");
  const tf = useTranslations("Forms");
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [state, action, pending] = useActionState(
    async (previous: SubmitState<User, Omit<NewUser, "password">> | undefined, form: FormData) => {
      const result = await createUserAction(previous, form);
      if (result.outcome.ok) {
        router.push(`/users/${encodeURIComponent(result.outcome.value.id)}`);
      }

      return result;
    },
    undefined,
  );
  const typed = state?.submitted;
  return (
    <Dialog open={open} onOpenChange={setOpen} title={t("new")} trigger={<Button><UserPlus aria-hidden />{t("new")}</Button>}>
      <form action={action} className="grid gap-4">
        <Field label={t("userName")}>
          <Input name="userName" required autoComplete="off" defaultValue={typed?.userName} />
        </Field>
        <Field label={t("displayName")}>
          <Input name="displayName" required defaultValue={typed?.displayName} />
        </Field>
        {/* Never filled in again after a refusal: the password isn't sent back to the browser. */}
        <Field label={t("password")} hint={t("passwordHint")}>
          <Input name="password" type="password" required autoComplete="new-password" />
        </Field>
        <Refusal state={state?.outcome} />
        <DialogActions>
          <Button type="button" variant="secondary" onClick={() => setOpen(false)}>{tf("cancel")}</Button>
          <Button type="submit" busy={pending}>{t("create")}</Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
