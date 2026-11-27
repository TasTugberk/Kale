"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { CircleStop } from "lucide-react";
import { ConfirmButton, Refusal } from "@/components/form-parts";
import { Code } from "@/components/ui/badge";
import { ServerDataTable, type ServerColumn } from "@/components/ui/server-data-table";
import type { OpenSession } from "@/lib/open-sessions";
import { listHref, type Filters, type ListQuery } from "@/lib/paging";
import { stopSessionAction } from "./actions";

/** A session with its times already formatted by the page (in the configured time zone). */
export interface SessionRow extends OpenSession {
  started: string;
  expires: string;
}

/**
 * One page of open sessions, newest first. The admin API searches and pages them (there can be many); it has one
 * order only, so the titles don't sort. `filters` are the page's ?applicationId= and ?userId=, kept on every link.
 */
export function SessionsTable({ sessions, total, pageSize, query, filters }: {
  sessions: SessionRow[];
  total: number;
  pageSize: number;
  query: ListQuery;
  filters: Filters;
}) {
  const t = useTranslations("Sessions");
  const columns: ServerColumn<SessionRow>[] = [
    {
      id: "user",
      header: t("user"),
      cell: session => <a href={`/users/${encodeURIComponent(session.userId)}`} className="font-medium">{session.userName}</a>,
    },
    { id: "application", header: t("application"), cell: session => <Code>{session.applicationKey}</Code> },
    { id: "role", header: t("role"), cell: session => session.roleName },
    { id: "started", header: t("started"), cell: session => <span className="whitespace-nowrap">{session.started}</span> },
    { id: "expires", header: t("expires"), cell: session => <span className="whitespace-nowrap">{session.expires}</span> },
    { id: "actions", header: <span className="sr-only">{t("actions")}</span>, cell: session => <StopSessionButton session={session} /> },
  ];

  return (
    <ServerDataTable
      columns={columns}
      rows={sessions}
      rowId={session => session.id}
      total={total}
      pageSize={pageSize}
      query={query}
      href={next => listHref("/sessions", next, filters)}
      empty={t("none")}
    />
  );
}

function StopSessionButton({ session }: { session: OpenSession }) {
  const t = useTranslations("Sessions");
  const [state, action, pending] = useActionState(stopSessionAction, undefined);
  return (
    <form action={action} className="grid justify-items-end gap-1">
      <input type="hidden" name="sessionId" value={session.id} />
      <ConfirmButton question={t("confirmStop", { userName: session.userName, key: session.applicationKey })} busy={pending} variant="dangerOutline">
        <CircleStop aria-hidden />{t("stop")}
      </ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}
