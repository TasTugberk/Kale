"use client";

import { useActionState, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Plus } from "lucide-react";
import { Refusal } from "@/components/form-parts";
import { Button } from "@/components/ui/button";
import { DataTable, type DataColumn } from "@/components/ui/data-table";
import { Dialog, DialogActions } from "@/components/ui/dialog";
import { Field, Input } from "@/components/ui/fields";
import type { SubmitState } from "@/lib/admin-requests";
import type { Group, GroupFields } from "@/lib/groups";
import { createGroupAction } from "./actions";

export function GroupsTable({ groups }: { groups: Group[] }) {
  const t = useTranslations("Groups");
  const columns: DataColumn<Group>[] = [
    {
      accessorKey: "name",
      header: t("name"),
      cell: ({ row }) => <a href={`/groups/${encodeURIComponent(row.original.id)}`} className="font-medium">{row.original.name}</a>,
    },
    { id: "description", accessorFn: group => group.description ?? "", header: t("description"), cell: ({ row }) => <span className="text-slate-500">{row.original.description}</span> },
  ];

  return <DataTable columns={columns} data={groups} empty={t("noGroups")} initialSorting={[{ id: "name", desc: false }]} />;
}

/** Creates the group, then opens its page (to add members and roles). */
export function NewGroupDialog() {
  const t = useTranslations("Groups");
  const tf = useTranslations("Forms");
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [state, action, pending] = useActionState(async (previous: SubmitState<Group, GroupFields> | undefined, form: FormData) => {
    const result = await createGroupAction(previous, form);
    if (result.outcome.ok) {
      router.push(`/groups/${encodeURIComponent(result.outcome.value.id)}`);
    }

    return result;
  }, undefined);
  const typed = state?.submitted;
  return (
    <Dialog open={open} onOpenChange={setOpen} title={t("new")} trigger={<Button><Plus aria-hidden />{t("new")}</Button>}>
      <form action={action} className="grid gap-4">
        <Field label={t("name")}>
          <Input name="name" required defaultValue={typed?.name} />
        </Field>
        <Field label={t("description")}>
          <Input name="description" defaultValue={typed?.description ?? ""} />
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
