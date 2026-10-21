import { notFound } from "next/navigation";
import { getFormatter, getTimeZone, getTranslations } from "next-intl/server";
import { listApplications } from "@/lib/applications";
import { listOpenSessions } from "@/lib/open-sessions";
import { adminApi } from "@/lib/server";
import { getUser } from "@/lib/users";
import { StopSessionForm } from "./forms";

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
  const time = (value: string) => format.dateTime(new Date(value), { dateStyle: "short", timeStyle: "short" });

  return (
    <>
      <h1>{t("title")}</h1>
      <p className="hint">{t("explanation", { timeZone: await getTimeZone() })}</p>
      {/* A plain GET form: the filter is in the address, so it can be bookmarked and reloaded. */}
      <form method="get" action="/sessions">
        {userId && <input type="hidden" name="userId" value={userId} />}
        <select name="applicationId" aria-label={t("application")} defaultValue={applicationId ?? ""}>
          <option value="">{t("allApplications")}</option>
          {applications.map(application => <option key={application.id} value={application.id}>{application.key}</option>)}
        </select>{" "}
        <button type="submit">{t("filter")}</button>
      </form>
      {user && (
        <p>
          {t("forUser", { userName: user.userName })}{" "}
          <a href={applicationId ? `/sessions?applicationId=${encodeURIComponent(applicationId)}` : "/sessions"}>{t("allUsers")}</a>
        </p>
      )}
      {sessions.length === 0
        ? <p className="hint">{t("none")}</p>
        : (
          <table>
            <thead>
              <tr>
                <th>{t("user")}</th>
                <th>{t("application")}</th>
                <th>{t("role")}</th>
                <th>{t("started")}</th>
                <th>{t("expires")}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {sessions.map(session => (
                <tr key={session.id}>
                  <td><a href={`/users/${encodeURIComponent(session.userId)}`}>{session.userName}</a></td>
                  <td><code>{session.applicationKey}</code></td>
                  <td>{session.roleName}</td>
                  <td>{time(session.createdAt)}</td>
                  <td>{time(session.expiresAt)}</td>
                  <td><StopSessionForm session={session} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
    </>
  );
}
