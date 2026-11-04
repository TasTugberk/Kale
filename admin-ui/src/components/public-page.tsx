import { ButtonLink } from "./ui/button";

/** A page for someone who isn't signed in (any more): a title, why, and one link to sign in. AppShell centres it. */
export function PublicPage({ title, explanation, link }: { title: string; explanation?: string; link: { href: string; label: string } }) {
  return (
    <div className="text-center">
      <h1 className="text-xl font-semibold text-slate-900">{title}</h1>
      {explanation && <p className="mt-2 text-sm text-slate-600">{explanation}</p>}
      <ButtonLink href={link.href} variant="primary" size="md" className="mt-6 w-full">{link.label}</ButtonLink>
    </div>
  );
}
