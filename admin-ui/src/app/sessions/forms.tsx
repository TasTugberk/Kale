"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { CircleStop } from "lucide-react";
import { ConfirmButton, Refusal } from "@/components/form-parts";
import { Code } from "@/components/ui/badge";
import { DataTable, type DataColumn } from "@/components/ui/data-table";
import type { OpenSession } from "@/lib/open-sessions";
import { stopSessionAction } from "./actions";

/** A session with its times already formatted by the page (in the configured time zone). */
export interface SessionRow extends OpenSession {
  started: string;
  expires: string;
}

export function SessionsTable({ sessions }: { sessions: SessionRow[] }) {
  const t = useTranslations("Sessions");
  const columns: DataColumn<SessionRow>[] = [
    {
      accessorKey: "userName",
      header: t("user"),
      cell: ({ row }) => <a href={`/users/${encodeURIComponent(row.original.userId)}`} className="font-medium">{row.original.userName}</a>,
    },
    { accessorKey: "applicationKey", header: t("application"), cell: ({ row }) => <Code>{row.original.applicationKey}</Code> },
    { accessorKey: "roleName", header: t("role") },
    // Sorted by the ISO time (which sorts like the time itself), shown formatted.
    { accessorKey: "createdAt", header: t("started"), enableGlobalFilter: false, cell: ({ row }) => <span className="whitespace-nowrap">{row.original.started}</span> },
    { accessorKey: "expiresAt", header: t("expires"), enableGlobalFilter: false, cell: ({ row }) => <span className="whitespace-nowrap">{row.original.expires}</span> },
    {
      id: "actions",
      enableSorting: false,
      header: () => <span className="sr-only">{t("actions")}</span>,
      cell: ({ row }) => <StopSessionButton session={row.original} />,
    },
  ];

  return <DataTable columns={columns} data={sessions} empty={t("none")} pageSize={20} initialSorting={[{ id: "createdAt", desc: true }]} />;
}

function StopSessionButton({ session }: { session: OpenSession }) {
  const t = useTranslations("Sessions");
  const [state, action, pending] = useActionState(stopSessionAction, undefined);
  return (
    <form action={action} className="grid justify-items-end gap-1">
      <input type="hidden" name="sessionId" value={session.id} />
      <ConfirmButton question={t("confirmStop", { userName: session.userName, key: session.applicationKey })} disabled={pending} variant="dangerOutline">
        <CircleStop aria-hidden />{t("stop")}
      </ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}
