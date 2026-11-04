import { notFound } from "next/navigation";
import { getFormatter, getTimeZone, getTranslations } from "next-intl/server";
import { Button, ButtonLink } from "@/components/ui/button";
import { Card, CardHeader } from "@/components/ui/card";
import { Select } from "@/components/ui/fields";
import { PageHeader } from "@/components/ui/page-header";
import { listApplications } from "@/lib/applications";
import { listOpenSessions } from "@/lib/open-sessions";
import { adminApi } from "@/lib/server";
import { getUser } from "@/lib/users";
import { SessionsTable } from "./forms";

export const dynamic = "force-dynamic";

/** Open sessions, newest first; ?applicationId= and ?userId= narrow them down. */
export default async function SessionsPage({ searchParams }: { searchParams: Promise<{ applicationId?: string; userId?: string }> }) {
  const { applicationId, userId } = await searchParams;
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

  const sessions = await listOpenSessions(adminApi, { applicationId: applicationId || undefined, userId });
  // Formatted here, in the configured time zone; the table sorts by the raw times.
  const time = (value: string) => format.dateTime(new Date(value), { dateStyle: "short", timeStyle: "short" });
  const rows = sessions.map(session => ({ ...session, started: time(session.createdAt), expires: time(session.expiresAt) }));

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
        <SessionsTable sessions={rows} />
      </Card>
    </>
  );
}
