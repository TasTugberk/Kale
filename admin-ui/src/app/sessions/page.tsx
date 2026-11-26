import { notFound, redirect } from "next/navigation";
import { getFormatter, getTimeZone, getTranslations } from "next-intl/server";
import { Button, ButtonLink } from "@/components/ui/button";
import { Card, CardHeader } from "@/components/ui/card";
import { Select } from "@/components/ui/fields";
import { PageHeader } from "@/components/ui/page-header";
import { listApplications } from "@/lib/applications";
import { listOpenSessions } from "@/lib/open-sessions";
import { lastPageIfPast, listHref, listQueryFrom } from "@/lib/paging";
import { adminApi } from "@/lib/server";
import { getUser } from "@/lib/users";
import { SessionsTable } from "./forms";

export const dynamic = "force-dynamic";

/**
 * One page of open sessions, newest first; ?applicationId= and ?userId= narrow them down, ?search= and ?page= come
 * from the address too (see lib/paging.ts).
 */
export default async function SessionsPage({ searchParams }: {
  searchParams: Promise<{ applicationId?: string; userId?: string; search?: string; page?: string }>;
}) {
  const params = await searchParams;
  const { applicationId, userId } = params;
  const query = listQueryFrom(params);
  const t = await getTranslations("Sessions");
  const format = await getFormatter();

  const user = userId ? await getUser(adminApi, userId) : undefined;
  if (userId && !user) {
    notFound();
  }

  // An application id that isn't one of them is "not found", like an unknown user (not an empty list).
  const applications = await listApplications(adminApi);
  if (applicationId && !applications.some(application => application.id === applicationId)) {
    notFound();
  }

  const filters = { applicationId: applicationId || undefined, userId };
  const sessions = await listOpenSessions(adminApi, filters, query);
  // A page past the end (a stale bookmark, sessions ended meanwhile): the last page instead of an empty one.
  const last = lastPageIfPast(query.page, sessions.total, sessions.pageSize);
  if (last !== undefined) {
    redirect(listHref("/sessions", { ...query, page: last }, filters));
  }
  // Formatted here, in the configured time zone.
  const time = (value: string) => format.dateTime(new Date(value), { dateStyle: "short", timeStyle: "short" });
  const rows = sessions.items.map(session => ({ ...session, started: time(session.createdAt), expires: time(session.expiresAt) }));

  return (
    <>
      <PageHeader title={t("title")} description={t("explanation", { timeZone: await getTimeZone() })} />
      <Card>
        <CardHeader
          title={user ? t("forUser", { userName: user.userName }) : t("everyone")}
          description={user && <a href={applicationId ? `/sessions?applicationId=${encodeURIComponent(applicationId)}` : "/sessions"}>{t("allUsers")}</a>}
          actions={
            // A plain GET form: the filter is in the address, so it can be bookmarked and reloaded.
            <form method="get" action="/sessions" className="flex items-center gap-2">
              {userId && <input type="hidden" name="userId" value={userId} />}
              <Select name="applicationId" aria-label={t("application")} defaultValue={applicationId ?? ""}>
                <option value="">{t("allApplications")}</option>
                {applications.map(application => <option key={application.id} value={application.id}>{application.key}</option>)}
              </Select>
              <Button type="submit" variant="secondary">{t("filter")}</Button>
              {(applicationId || userId) && <ButtonLink href="/sessions" variant="ghost" size="md">{t("clear")}</ButtonLink>}
            </form>
          }
        />
        <SessionsTable sessions={rows} total={sessions.total} pageSize={sessions.pageSize} query={query} filters={filters} />
      </Card>
    </>
  );
}
