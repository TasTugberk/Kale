"use client";

import { useActionState, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { UserPlus } from "lucide-react";
import { Refusal } from "@/components/form-parts";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { DataTable, type DataColumn } from "@/components/ui/data-table";
import { Dialog, DialogActions } from "@/components/ui/dialog";
import { Field, Input } from "@/components/ui/fields";
import type { SubmitState } from "@/lib/admin-requests";
import type { NewUser, User } from "@/lib/users";
import { createUserAction } from "./actions";

export function UsersTable({ users }: { users: User[] }) {
  const t = useTranslations("Users");
  const columns: DataColumn<User>[] = [
    {
      accessorKey: "userName",
      header: t("userName"),
      cell: ({ row }) => <a href={`/users/${encodeURIComponent(row.original.id)}`} className="font-medium">{row.original.userName}</a>,
    },
    { accessorKey: "displayName", header: t("displayName") },
    {
      id: "status",
      accessorFn: user => user.isActive ? t("active") : t("disabled"),
      header: t("status"),
      cell: ({ row }) => <UserStatus isActive={row.original.isActive} />,
    },
  ];

  return <DataTable columns={columns} data={users} empty={t("none")} initialSorting={[{ id: "userName", desc: false }]} />;
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
          <Button type="submit" disabled={pending}>{t("create")}</Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
